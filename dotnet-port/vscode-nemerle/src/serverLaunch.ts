import * as fs from 'node:fs/promises';
import * as path from 'node:path';

export interface ServerLaunchSpec {
  readonly command: string;
  readonly args: readonly string[];
  readonly cwd: string;
  readonly serverPath: string;
  readonly dotnetExecutable: string;
}

export class ServerConfigurationError extends Error {
  public constructor(message: string) {
    super(message);
    this.name = 'ServerConfigurationError';
  }
}

export async function resolveServerLaunch(
  serverPath: string,
  dotnetPath = 'dotnet',
): Promise<ServerLaunchSpec> {
  const dotnetExecutable = await resolveDotNetExecutable(dotnetPath);
  const configuredPath = serverPath.trim();
  if (configuredPath.length === 0) {
    throw new ServerConfigurationError(
      'Nemerle language server path is not configured. Set nemerle.server.path to Nemerle.LanguageServer.dll.',
    );
  }

  if (!path.isAbsolute(configuredPath)) {
    throw new ServerConfigurationError(
      `Nemerle language server path must be absolute: ${configuredPath}`,
    );
  }

  const fullPath = path.normalize(configuredPath);
  let stat: Awaited<ReturnType<typeof fs.stat>>;
  try {
    stat = await fs.stat(fullPath);
  } catch (error: unknown) {
    if (isMissingFileError(error)) {
      throw new ServerConfigurationError(`Nemerle language server file does not exist: ${fullPath}`);
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
    };
  }

  return {
    command: fullPath,
    args: [],
    cwd: path.dirname(fullPath),
    serverPath: fullPath,
    dotnetExecutable,
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

export function formatLaunchSpec(spec: ServerLaunchSpec): string {
  return [spec.command, ...spec.args].map(quoteForDisplay).join(' ');
}

function quoteForDisplay(value: string): string {
  return /\s/u.test(value) ? JSON.stringify(value) : value;
}

function isMissingFileError(error: unknown): error is Error & { code: string } {
  return error instanceof Error && 'code' in error && (error as { code?: unknown }).code === 'ENOENT';
}
