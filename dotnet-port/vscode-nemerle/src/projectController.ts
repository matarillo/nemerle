import * as path from 'node:path';
import * as vscode from 'vscode';
import {
  type NemerleClientController,
  type ProjectInfoLoadResult,
} from './clientController';
import {
  isExcludedProjectPath,
  resolveSelectedProject,
  sortAndDedupeProjects,
} from './projectDiscovery';

const selectedProjectKey = 'nemerle.selectedProject';
const discoveryExclude = '**/{bin,obj,node_modules,.git,.vs,dist}/**';

export interface ProjectStatusSnapshot {
  readonly state: string;
  readonly selectedProject?: string;
  readonly result?: ProjectInfoLoadResult;
  readonly message: string;
}

export class NemerleProjectController implements vscode.Disposable {
  private readonly statusItem: vscode.StatusBarItem;
  private readonly watcher: vscode.FileSystemWatcher;
  private selectedProject: string | undefined;
  private status: ProjectStatusSnapshot = { state: 'unselected', message: 'No project selected.' };
  private debounceTimer: ReturnType<typeof setTimeout> | undefined;
  private generation = 0;
  private loadOperation: Promise<void> = Promise.resolve();
  private disposed = false;

  public constructor(
    private readonly context: vscode.ExtensionContext,
    private readonly client: NemerleClientController,
    private readonly output: vscode.LogOutputChannel,
  ) {
    this.statusItem = vscode.window.createStatusBarItem(
      'nemerle.projectStatus',
      vscode.StatusBarAlignment.Left,
      10,
    );
    this.statusItem.name = 'Nemerle Project';
    this.statusItem.command = 'nemerle.showProjectStatus';
    this.statusItem.show();
    this.watcher = vscode.workspace.createFileSystemWatcher('**/*.nproj');
    this.watcher.onDidCreate((uri) => this.scheduleDiscovery(uri), this, context.subscriptions);
    this.watcher.onDidDelete((uri) => this.scheduleDiscovery(uri), this, context.subscriptions);
    this.watcher.onDidChange((uri) => this.scheduleReload(uri), this, context.subscriptions);
    context.subscriptions.push(this.statusItem, this.watcher);
    this.renderStatus();
  }

  public get currentStatus(): ProjectStatusSnapshot {
    return this.status;
  }

  public async initialize(): Promise<void> {
    if (this.disposed) {
      return;
    }
    if (!vscode.workspace.isTrusted) {
      this.selectedProject = undefined;
      this.setStatus('restricted', 'Workspace is not trusted; server and MSBuild project queries are disabled.');
      return;
    }
    const folders = vscode.workspace.workspaceFolders ?? [];
    if (folders.length === 0) {
      this.selectedProject = undefined;
      this.setStatus('unselected', 'Open a folder containing a .nproj project.');
      return;
    }
    if (folders.length !== 1) {
      this.selectedProject = undefined;
      this.setStatus('unsupported', 'WP-L2 supports one workspace folder and one selected project only.');
      return;
    }
    const folder = folders[0];
    if (folder === undefined) {
      return;
    }

    const candidates = await this.discoverProjects();
    if (candidates.length === 0) {
      this.selectedProject = undefined;
      this.setStatus('empty', 'No Nemerle .nproj projects were found in the workspace.');
      return;
    }
    const configured = vscode.workspace.getConfiguration('nemerle').get<string>('project', '');
    const stored = this.context.workspaceState.get<string>(selectedProjectKey);
    const selection = resolveSelectedProject(folder.uri.fsPath, candidates, configured, stored);
    if (selection.configuredMissing !== undefined) {
      this.selectedProject = undefined;
      this.setStatus('error', `Configured Nemerle project was not found among workspace candidates: ${selection.configuredMissing}`);
      return;
    }
    if (selection.selected === undefined) {
      this.selectedProject = undefined;
      this.setStatus('unselected', `${candidates.length} Nemerle projects found. Use Nemerle: Select Project.`);
      return;
    }
    this.selectedProject = selection.selected;
    await this.context.workspaceState.update(selectedProjectKey, this.selectedProject);
    await this.loadSelected(false);
  }

  public async selectProject(): Promise<void> {
    if (!this.ensureTrusted('select a project')) {
      return;
    }
    const folders = vscode.workspace.workspaceFolders ?? [];
    if (folders.length !== 1) {
      this.setStatus('unsupported', 'WP-L2 supports one workspace folder and one selected project only.');
      return;
    }
    const folder = folders[0];
    if (folder === undefined) {
      return;
    }
    const candidates = await this.discoverProjects();
    if (candidates.length === 0) {
      this.setStatus('empty', 'No Nemerle .nproj projects were found in the workspace.');
      return;
    }
    const items = candidates.map((projectPath) => ({
      label: path.basename(projectPath),
      description: path.relative(folder.uri.fsPath, projectPath),
      projectPath,
    }));
    const selected = await vscode.window.showQuickPick(items, {
      placeHolder: 'Select the single Nemerle project used for the WP-L2 snapshot',
      matchOnDescription: true,
    });
    if (selected === undefined) {
      return;
    }
    this.selectedProject = selected.projectPath;
    await this.context.workspaceState.update(selectedProjectKey, this.selectedProject);
    await this.loadSelected(true);
  }

  public async reloadProject(): Promise<void> {
    if (!this.ensureTrusted('reload the project')) {
      return;
    }
    if (this.debounceTimer !== undefined) {
      clearTimeout(this.debounceTimer);
      this.debounceTimer = undefined;
    }
    if (this.selectedProject === undefined) {
      await this.initialize();
      if (this.selectedProject === undefined) {
        void vscode.window.showInformationMessage(this.status.message);
      }
      return;
    }
    await this.loadSelected(true);
  }

  public showStatus(): void {
    this.output.info(this.formatStatus());
    this.output.show(true);
    void vscode.window.showInformationMessage(this.formatStatus());
  }

  public dispose(): void {
    this.disposed = true;
    this.generation++;
    if (this.debounceTimer !== undefined) {
      clearTimeout(this.debounceTimer);
      this.debounceTimer = undefined;
    }
  }

  private async discoverProjects(): Promise<string[]> {
    const uris = await vscode.workspace.findFiles('**/*.nproj', discoveryExclude);
    return sortAndDedupeProjects(uris.map((uri) => uri.fsPath));
  }

  private loadSelected(forceReload: boolean): Promise<void> {
    const run = async (): Promise<void> => this.loadSelectedCore(forceReload);
    const result = this.loadOperation.then(run, run);
    this.loadOperation = result.catch((error: unknown) => {
      const message = error instanceof Error ? error.message : String(error);
      this.output.error(`Nemerle project operation failed: ${message}`);
    });
    return result;
  }

  private async loadSelectedCore(forceReload: boolean): Promise<void> {
    const projectPath = this.selectedProject;
    if (projectPath === undefined) {
      return;
    }
    const currentGeneration = ++this.generation;
    this.setStatus('loading', `Loading project snapshot: ${projectPath}`);
    const configuration = vscode.workspace.getConfiguration('nemerle');
    try {
      const result = await this.client.loadProject({
        projectPath,
        configuration: configuration.get<string>('projectConfiguration', 'Debug'),
        platform: configuration.get<string>('projectPlatform', 'AnyCPU'),
        targetFramework: configuration.get<string>('projectTargetFramework', ''),
        forceReload,
      });
      if (this.disposed || currentGeneration !== this.generation) {
        return;
      }
      if (result === undefined) {
        this.setStatus('error', 'The language server is not running; project snapshot was not loaded.');
      } else if (result.state === 'error') {
        const message = `${result.errorKind ?? 'QueryError'}: ${result.errorMessage ?? 'Project query failed.'}`;
        this.setStatus('error', message, result);
        this.output.error(message);
        if (result.errorDetails !== undefined && result.errorDetails.length > 0) {
          this.output.error(result.errorDetails);
        }
        void vscode.window.showErrorMessage(`Nemerle project load failed. ${message}`, 'Show Output').then((choice) => {
          if (choice === 'Show Output') {
            this.output.show(true);
          }
        });
      } else {
        this.setStatus('loaded', `Loaded snapshot for ${result.projectPath ?? projectPath}.`, result);
        this.output.info(this.formatStatus());
        for (const warning of result.warnings) {
          this.output.warn(warning);
        }
      }
    } catch (error: unknown) {
      if (this.disposed || currentGeneration !== this.generation) {
        return;
      }
      const message = error instanceof Error ? error.message : String(error);
      this.setStatus('error', `Project request failed: ${message}`);
      this.output.error(`Project request failed without stopping the language server: ${message}`);
    }
  }

  private scheduleDiscovery(uri: vscode.Uri): void {
    if (isExcludedProjectPath(uri.fsPath)) {
      return;
    }
    this.schedule(() => this.initialize());
  }

  private scheduleReload(uri: vscode.Uri): void {
    if (isExcludedProjectPath(uri.fsPath) || this.selectedProject === undefined) {
      return;
    }
    const left = process.platform === 'win32' ? uri.fsPath.toLowerCase() : uri.fsPath;
    const right = process.platform === 'win32' ? this.selectedProject.toLowerCase() : this.selectedProject;
    if (path.normalize(left) === path.normalize(right)) {
      this.schedule(() => this.loadSelected(true));
    }
  }

  private schedule(action: () => Promise<void>): void {
    if (this.debounceTimer !== undefined) {
      clearTimeout(this.debounceTimer);
    }
    this.debounceTimer = setTimeout(() => {
      this.debounceTimer = undefined;
      void action();
    }, 350);
  }

  private ensureTrusted(action: string): boolean {
    if (vscode.workspace.isTrusted) {
      return true;
    }
    this.setStatus('restricted', 'Workspace is not trusted; server and MSBuild project queries are disabled.');
    void vscode.window.showWarningMessage(`Trust this workspace before Nemerle can ${action}.`);
    return false;
  }

  private setStatus(state: string, message: string, result?: ProjectInfoLoadResult): void {
    this.status = { state, selectedProject: this.selectedProject, result, message };
    this.renderStatus();
  }

  private renderStatus(): void {
    const labels: Record<string, string> = {
      restricted: '$(shield) Nemerle: Restricted',
      unsupported: '$(warning) Nemerle: One Folder Only',
      empty: '$(circle-slash) Nemerle: No Project',
      unselected: '$(question) Nemerle: Select Project',
      loading: '$(sync~spin) Nemerle: Loading',
      loaded: '$(project) Nemerle: Project Loaded',
      error: '$(error) Nemerle: Project Error',
    };
    this.statusItem.text = labels[this.status.state] ?? '$(project) Nemerle';
    this.statusItem.tooltip = this.formatStatus();
  }

  private formatStatus(): string {
    const result = this.status.result;
    const lines = [this.status.message];
    if (this.selectedProject !== undefined) {
      lines.push(`Project: ${this.selectedProject}`);
    }
    if (result?.state === 'loaded') {
      lines.push(`Configuration: ${result.configuration ?? ''} | Platform: ${result.platform ?? ''} | Target framework: ${result.targetFramework ?? ''}`);
      lines.push(`Sources: ${result.sourceCount} | Assembly references: ${result.assemblyReferenceCount} | Macro references: ${result.macroReferenceCount}`);
      if (result.warnings.length > 0) {
        lines.push(...result.warnings.map((warning) => `Warning: ${warning}`));
      }
    }
    lines.push('WP-L2 snapshot only: it is not applied to the analysis engine; diagnostics are not project-aware until WP-L3.');
    return lines.join('\n');
  }
}
