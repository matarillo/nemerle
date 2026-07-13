import * as fs from 'node:fs/promises';
import * as path from 'node:path';

export interface ServerLaunchSpec {
  readonly command: string;
  readonly args: readonly string[];
  readonly cwd: string;
  readonly serverPath: string;
}

export class ServerConfigurationError extends Error {
  public constructor(message: string) {
    super(message);
    this.name = 'ServerConfigurationError';
  }
}

export async function resolveServerLaunch(serverPath: string): Promise<ServerLaunchSpec> {
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
      command: 'dotnet',
      args: ['exec', fullPath],
      cwd: path.dirname(fullPath),
      serverPath: fullPath,
    };
  }

  return {
    command: fullPath,
    args: [],
    cwd: path.dirname(fullPath),
    serverPath: fullPath,
  };
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
