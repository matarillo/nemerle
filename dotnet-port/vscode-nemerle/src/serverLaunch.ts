import { execFile } from 'node:child_process';
import * as fs from 'node:fs/promises';
import * as path from 'node:path';
import { promisify } from 'node:util';

const execFileAsync = promisify(execFile);

export type ServerLaunchSource = 'configured' | 'bundled';

export interface ServerLaunchSpec {
  readonly command: string;
  readonly args: readonly string[];
  readonly cwd: string;
  readonly serverPath: string;
  readonly dotnetExecutable: string;
  readonly source: ServerLaunchSource;
}

export class ServerConfigurationError extends Error {
  public constructor(message: string) {
    super(message);
    this.name = 'ServerConfigurationError';
  }
}

export class DotNetRuntimeError extends Error {
  public constructor(message: string) {
    super(message);
    this.name = 'DotNetRuntimeError';
  }
}

export async function resolveServerLaunch(
  serverPath: string,
  dotnetPath = 'dotnet',
  bundledServerPath?: string,
): Promise<ServerLaunchSpec> {
  const dotnetExecutable = await resolveDotNetExecutable(dotnetPath);
  const configuredPath = serverPath.trim();
  let source: ServerLaunchSource;
  let candidatePath: string;
  if (configuredPath.length > 0) {
    if (!path.isAbsolute(configuredPath)) {
      throw new ServerConfigurationError(
        `Nemerle language server path must be absolute: ${configuredPath}`,
      );
    }
    source = 'configured';
    candidatePath = configuredPath;
  } else if (bundledServerPath !== undefined && bundledServerPath.trim().length > 0) {
    source = 'bundled';
    candidatePath = bundledServerPath;
  } else {
    throw new ServerConfigurationError(
      'No bundled Nemerle language server is available and nemerle.server.path is not configured. ' +
      'Set nemerle.server.path to Nemerle.LanguageServer.dll.',
    );
  }

  const fullPath = path.normalize(candidatePath);
  let stat: Awaited<ReturnType<typeof fs.stat>>;
  try {
    stat = await fs.stat(fullPath);
  } catch (error: unknown) {
    if (isMissingFileError(error)) {
      throw new ServerConfigurationError(
        source === 'bundled'
          ? `The bundled Nemerle language server is missing: ${fullPath}. ` +
            'Reinstall the extension; for a development build, stage the server with ' +
            'dotnet-port\\vscode-nemerle\\pack-server.ps1 or set nemerle.server.path.'
          : `Nemerle language server file does not exist: ${fullPath}`,
      );
    }
    throw error;
  }

  if (!stat.isFile()) {
    throw new ServerConfigurationError(`Nemerle language server path is not a file: ${fullPath}`);
  }

  if (path.extname(fullPath).toLowerCase() === '.dll') {
    return {
      command: dotnetExecutable,
      args: ['exec', fullPath],
      cwd: path.dirname(fullPath),
      serverPath: fullPath,
      dotnetExecutable,
      source,
    };
  }

  return {
    command: fullPath,
    args: [],
    cwd: path.dirname(fullPath),
    serverPath: fullPath,
    dotnetExecutable,
    source,
  };
}

export async function resolveDotNetExecutable(dotnetPath: string): Promise<string> {
  const configuredPath = dotnetPath.trim();
  if (configuredPath.length === 0 || configuredPath === 'dotnet') {
    return 'dotnet';
  }
  if (!path.isAbsolute(configuredPath)) {
    throw new ServerConfigurationError(
      `Nemerle dotnet executable override must be an absolute file path: ${configuredPath}`,
    );
  }
  const fullPath = path.normalize(configuredPath);
  let stat: Awaited<ReturnType<typeof fs.stat>>;
  try {
    stat = await fs.stat(fullPath);
  } catch (error: unknown) {
    if (isMissingFileError(error)) {
      throw new ServerConfigurationError(`Nemerle dotnet executable does not exist: ${fullPath}`);
    }
    throw error;
  }
  if (!stat.isFile()) {
    throw new ServerConfigurationError(`Nemerle dotnet executable path is not a file: ${fullPath}`);
  }
  return fullPath;
}

/**
 * Parses `dotnet --list-runtimes` output into the installed
 * Microsoft.NETCore.App version strings, in listing order.
 */
export function parseNetCoreAppRuntimes(listRuntimesOutput: string): string[] {
  const versions: string[] = [];
  for (const line of listRuntimesOutput.split(/\r?\n/u)) {
    const match = /^Microsoft\.NETCore\.App (\S+) \[.+\]$/u.exec(line.trim());
    if (match !== null && match[1] !== undefined) {
      versions.push(match[1]);
    }
  }
  return versions;
}

export function findRequiredRuntime(versions: readonly string[], requiredMajor: number): string | undefined {
  return versions.find((version) => {
    const major = Number.parseInt(version.split('.')[0] ?? '', 10);
    return major === requiredMajor;
  });
}

/**
 * Verifies that the resolved dotnet host exists and provides the
 * Microsoft.NETCore.App runtime the DLL-shaped server needs
 * (net10.0 with rollForward LatestPatch in the shipped runtimeconfig).
 * Returns the matched runtime version for logging.
 */
export async function ensureDotNetRuntime(
  dotnetExecutable: string,
  requiredMajor = 10,
): Promise<string> {
  let stdout: string;
  try {
    const result = await execFileAsync(dotnetExecutable, ['--list-runtimes'], {
      shell: false,
      windowsHide: true,
      timeout: 15_000,
      encoding: 'utf8',
    });
    stdout = result.stdout;
  } catch (error: unknown) {
    if (isMissingFileError(error)) {
      throw new DotNetRuntimeError(
        `The .NET host '${dotnetExecutable}' was not found. Install the .NET ${requiredMajor} runtime ` +
        `(https://dotnet.microsoft.com/download/dotnet/${requiredMajor}.0) or set nemerle.dotnet.path ` +
        'to an absolute dotnet executable path.',
      );
    }
    const detail = error instanceof Error ? error.message : String(error);
    throw new DotNetRuntimeError(
      `'${dotnetExecutable} --list-runtimes' failed, so the installed .NET runtimes could not be determined: ${detail}`,
    );
  }

  const versions = parseNetCoreAppRuntimes(stdout);
  const matched = findRequiredRuntime(versions, requiredMajor);
  if (matched === undefined) {
    const found = versions.length > 0 ? versions.join(', ') : 'none';
    throw new DotNetRuntimeError(
      `The Nemerle language server needs the Microsoft.NETCore.App ${requiredMajor}.x runtime, but ` +
      `'${dotnetExecutable} --list-runtimes' reported: ${found}. Install the .NET ${requiredMajor} runtime ` +
      `(https://dotnet.microsoft.com/download/dotnet/${requiredMajor}.0).`,
    );
  }
  return matched;
}

export function formatLaunchSpec(spec: ServerLaunchSpec): string {
  return [spec.command, ...spec.args].map(quoteForDisplay).join(' ');
}

function quoteForDisplay(value: string): string {
  return /\s/u.test(value) ? JSON.stringify(value) : value;
}

function isMissingFileError(error: unknown): error is Error & { code: string } {
  return error instanceof Error && 'code' in error && (error as { code?: unknown }).code === 'ENOENT';
}
