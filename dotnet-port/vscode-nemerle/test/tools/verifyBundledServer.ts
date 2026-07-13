import * as fs from 'node:fs';
import * as path from 'node:path';

/**
 * Structural verification of the staged bundled server (server/), run by
 * `npm run verify-server` before `vsce package` and reused by the unit tests.
 * It enforces the WP-L4 packaging rules:
 *  - the server closure staged by pack-server.ps1 is complete, and
 *  - every bundled non-Nemerle assembly is covered by THIRD-PARTY-NOTICES.md.
 */

export const requiredServerFiles: readonly string[] = [
  'Nemerle.LanguageServer.dll',
  'Nemerle.LanguageServer.runtimeconfig.json',
  'Nemerle.LanguageServer.deps.json',
  'Nemerle.dll',
  'Nemerle.Compiler.dll',
  'Nemerle.Macros.dll',
  'Nemerle.Compiler.Utils.dll',
  'Nemerle.ProjectInfo.dll',
  'OmniSharp.Extensions.JsonRpc.dll',
  'OmniSharp.Extensions.LanguageProtocol.dll',
  'OmniSharp.Extensions.LanguageServer.dll',
  'OmniSharp.Extensions.LanguageServer.Shared.dll',
  'MediatR.dll',
  'Newtonsoft.Json.dll',
  'Nerdbank.Streams.dll',
  'System.Reactive.dll',
  'bundle-info.json',
];

export function listServerAssemblies(serverDir: string): string[] {
  const assemblies: string[] = [];
  const walk = (directory: string): void => {
    for (const entry of fs.readdirSync(directory, { withFileTypes: true })) {
      const fullPath = path.join(directory, entry.name);
      if (entry.isDirectory()) {
        walk(fullPath);
      } else if (entry.name.toLowerCase().endsWith('.dll')) {
        assemblies.push(fullPath);
      }
    }
  };
  walk(serverDir);
  return assemblies;
}

/**
 * Maps a bundled assembly file to the identifier THIRD-PARTY-NOTICES.md must
 * mention: the assembly base name with any `.resources` satellite suffix
 * stripped (all bundled NuGet packages name their assembly after the package).
 */
export function noticeIdentifierFor(assemblyPath: string): string {
  const baseName = path.basename(assemblyPath, '.dll');
  return baseName.endsWith('.resources') ? baseName.slice(0, -'.resources'.length) : baseName;
}

export function verifyBundledServer(
  serverDir: string,
  noticesPath: string,
): string[] {
  const problems: string[] = [];
  if (!fs.existsSync(serverDir)) {
    return [`Bundled server directory does not exist: ${serverDir}. Run dotnet-port\\vscode-nemerle\\pack-server.ps1 first.`];
  }
  for (const required of requiredServerFiles) {
    if (!fs.existsSync(path.join(serverDir, required))) {
      problems.push(`Missing required bundled server file: ${required}`);
    }
  }

  if (!fs.existsSync(noticesPath)) {
    problems.push(`Missing third-party notices file: ${noticesPath}`);
    return problems;
  }
  // Every bundled assembly, first-party Nemerle (BSD-3-Clause) included,
  // must be mentioned in the notices file.
  const notices = fs.readFileSync(noticesPath, 'utf8');
  const unmentioned = new Set<string>();
  for (const assembly of listServerAssemblies(serverDir)) {
    const identifier = noticeIdentifierFor(assembly);
    if (!notices.includes(identifier)) {
      unmentioned.add(identifier);
    }
  }
  for (const identifier of [...unmentioned].sort()) {
    problems.push(`Bundled assembly is not covered by THIRD-PARTY-NOTICES.md: ${identifier}`);
  }
  return problems;
}

function main(): void {
  const extensionRoot = path.resolve(__dirname, '..', '..', '..');
  const serverDir = path.join(extensionRoot, 'server');
  const noticesPath = path.join(extensionRoot, 'THIRD-PARTY-NOTICES.md');
  const problems = verifyBundledServer(serverDir, noticesPath);
  if (problems.length > 0) {
    for (const problem of problems) {
      console.error(`verify-server: ${problem}`);
    }
    process.exitCode = 1;
    return;
  }
  const count = listServerAssemblies(serverDir).length;
  console.log(`verify-server: OK (${count} bundled assemblies, notices coverage verified)`);
}

if (require.main === module) {
  main();
}
