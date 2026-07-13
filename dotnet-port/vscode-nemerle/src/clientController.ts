import * as vscode from 'vscode';
import {
  LanguageClient,
  Trace,
  type Executable,
  type LanguageClientOptions,
} from 'vscode-languageclient/node';
import {
  formatLaunchSpec,
  resolveServerLaunch,
  ServerConfigurationError,
} from './serverLaunch';

export interface ProjectInfoLoadRequest {
  readonly projectPath: string;
  readonly configuration: string;
  readonly platform: string;
  readonly targetFramework: string;
  readonly dotNetExecutable: string;
  readonly forceReload: boolean;
}

export interface ProjectInfoLoadResult {
  readonly state: 'loaded' | 'error';
  readonly projectPath?: string;
  readonly projectDirectory?: string;
  readonly configuration?: string;
  readonly platform?: string;
  readonly targetFramework?: string;
  readonly sourceCount: number;
  readonly assemblyReferenceCount: number;
  readonly macroReferenceCount: number;
  readonly defineConstants: readonly string[];
  readonly warnings: readonly string[];
  readonly errorKind?: string;
  readonly errorMessage?: string;
  readonly errorDetails?: string;
  readonly appliedToEngine: false;
}

const configurationSection = 'nemerle';

export class NemerleClientController implements vscode.Disposable {
  private client: LanguageClient | undefined;
  private operation: Promise<void> = Promise.resolve();
  private disposed = false;
  private lastConfigurationError: string | undefined;
  private dotnetExecutable = 'dotnet';
  private readonly projectCancellations = new Set<vscode.CancellationTokenSource>();

  public constructor(private readonly output: vscode.LogOutputChannel) {}

  public start(): Promise<void> {
    return this.enqueue(() => this.startCore());
  }

  public restart(): Promise<void> {
    return this.enqueue(async () => {
      await this.stopCore();
      await this.startCore();
    });
  }

  public updateTrace(): Promise<void> {
    return this.enqueue(async () => {
      if (this.client !== undefined) {
        await this.client.setTrace(readTraceSetting());
      }
    });
  }

  public showOutput(): void {
    this.output.show(true);
  }

  public get serverProcessId(): number | undefined {
    return this.client?.serverProcess?.pid;
  }

  public get resolvedDotNetExecutable(): string {
    return this.dotnetExecutable;
  }

  public async loadProject(
    request: Omit<ProjectInfoLoadRequest, 'dotNetExecutable'>,
  ): Promise<ProjectInfoLoadResult | undefined> {
    const client = this.client;
    if (client === undefined || !vscode.workspace.isTrusted) {
      return undefined;
    }
    const cancellation = new vscode.CancellationTokenSource();
    this.projectCancellations.add(cancellation);
    try {
      return await client.sendRequest<ProjectInfoLoadResult>(
        'nemerle/projectInfo/load',
        { ...request, dotNetExecutable: this.dotnetExecutable },
        cancellation.token,
      );
    } finally {
      this.projectCancellations.delete(cancellation);
      cancellation.dispose();
    }
  }

  public dispose(): void {
    void this.disposeAsync();
  }

  public disposeAsync(): Promise<void> {
    this.disposed = true;
    return this.enqueue(async () => {
      await this.stopCore();
      this.output.dispose();
    }, true);
  }

  private async startCore(): Promise<void> {
    if (this.disposed || this.client !== undefined) {
      return;
    }

    if (!vscode.workspace.isTrusted) {
      this.output.info('Workspace is not trusted; the Nemerle language server was not started.');
      return;
    }

    const configuredPath = vscode.workspace
      .getConfiguration(configurationSection)
      .get<string>('server.path', '');
    const configuredDotnet = vscode.workspace
      .getConfiguration(configurationSection)
      .get<string>('dotnet.path', 'dotnet');

    let launch;
    try {
      launch = await resolveServerLaunch(configuredPath, configuredDotnet);
      this.dotnetExecutable = launch.dotnetExecutable;
      this.lastConfigurationError = undefined;
    } catch (error: unknown) {
      if (error instanceof ServerConfigurationError) {
        this.reportConfigurationError(error.message);
        return;
      }
      throw error;
    }

    const serverOptions: Executable = {
      command: launch.command,
      args: [...launch.args],
      options: {
        cwd: launch.cwd,
        shell: false,
      },
    };
    const clientOptions: LanguageClientOptions = {
      documentSelector: [{ language: 'nemerle', scheme: 'file' }],
      outputChannel: this.output,
      traceOutputChannel: this.output,
      connectionOptions: {
        maxRestartCount: 0,
      },
      initializationFailedHandler: (error: Error) => {
        this.output.error(`Nemerle language server initialization failed: ${error.message}`);
        return false;
      },
    };

    const client = new LanguageClient(
      'nemerle',
      'Nemerle Language Server',
      serverOptions,
      clientOptions,
    );
    this.client = client;
    this.output.info(`Starting Nemerle language server: ${formatLaunchSpec(launch)}`);

    try {
      await client.start();
      await client.setTrace(readTraceSetting());
      this.output.info('Nemerle language server started.');
    } catch (error: unknown) {
      this.client = undefined;
      await client.dispose();
      const message = error instanceof Error ? error.message : String(error);
      this.output.error(`Could not start the Nemerle language server: ${message}`);
      void vscode.window.showErrorMessage(
        `Could not start the Nemerle language server. ${message}`,
        'Show Output',
      ).then((choice) => {
        if (choice === 'Show Output') {
          this.showOutput();
        }
      });
    }
  }

  private async stopCore(): Promise<void> {
    for (const cancellation of this.projectCancellations) {
      cancellation.cancel();
      cancellation.dispose();
    }
    this.projectCancellations.clear();
    const client = this.client;
    this.client = undefined;
    if (client === undefined) {
      return;
    }

    this.output.info('Stopping Nemerle language server...');
    try {
      await client.dispose();
    } finally {
      this.output.info('Nemerle language server stopped.');
    }
  }

  private reportConfigurationError(message: string): void {
    this.output.error(message);
    if (this.lastConfigurationError === message) {
      return;
    }

    this.lastConfigurationError = message;
    void vscode.window.showErrorMessage(message, 'Open Settings', 'Show Output').then(async (choice) => {
      if (choice === 'Open Settings') {
        await vscode.commands.executeCommand('workbench.action.openSettings', 'nemerle.server.path');
      } else if (choice === 'Show Output') {
        this.showOutput();
      }
    });
  }

  private enqueue(operation: () => Promise<void>, allowDisposed = false): Promise<void> {
    const run = async (): Promise<void> => {
      if (!allowDisposed && this.disposed) {
        return;
      }
      await operation();
    };
    const result = this.operation.then(run, run);
    this.operation = result.catch((error: unknown) => {
      const message = error instanceof Error ? error.stack ?? error.message : String(error);
      this.output.error(`Nemerle extension operation failed: ${message}`);
    });
    return this.operation;
  }
}

function readTraceSetting(): Trace {
  const value = vscode.workspace
    .getConfiguration(configurationSection)
    .get<string>('server.trace', 'off');
  switch (value) {
    case 'messages':
      return Trace.Messages;
    case 'verbose':
      return Trace.Verbose;
    default:
      return Trace.Off;
  }
}
