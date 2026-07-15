import { execFileSync, spawn } from 'node:child_process';
import * as fs from 'node:fs';
import * as path from 'node:path';
import { downloadAndUnzipVSCode } from '@vscode/test-electron';

/**
 * WP-M6 acceptance 4: runs the extension host against a project that consumes the
 * Nemerle.Sdk NuGet package rather than importing the repository's targets.
 *
 * The workspace is generated here instead of being committed, for two reasons: the project
 * must pin an SDK version, which tracks the compiler generation and would rot in the tree; and
 * its NuGet.config must point at dotnet-port/dist/release, which is a generated directory. Both
 * are derived from whichever package pack-tool.ps1 -Pack actually produced, so this test can
 * never drift from the artifact it is meant to test.
 *
 * Requires: pwsh dotnet-port\pack-tool.ps1 -Pack
 */
async function main(): Promise<void> {
  const extensionDevelopmentPath = path.resolve(__dirname, '..', '..');
  const extensionTestsPath = path.resolve(__dirname, 'suite', 'index');
  const feed = path.resolve(extensionDevelopmentPath, '..', 'dist', 'release');

  const packageVersion = findSdkPackageVersion(feed);
  if (packageVersion === undefined) {
    console.error(
      `No Nemerle.Sdk.Unofficial package found in ${feed}.\n` +
      'Run `pwsh dotnet-port\\pack-tool.ps1 -Pack` first; this suite tests the packaged SDK.',
    );
    process.exitCode = 1;
    return;
  }

  const workspacePath = path.join(extensionDevelopmentPath, 'test-workspace-sdk');
  writeGeneratedWorkspace(workspacePath, feed, packageVersion);
  console.log(`Sdk workspace -> ${workspacePath} (Nemerle.Sdk.Unofficial/${packageVersion})`);

  // The project-info query runs `-target:ResolveReferences`, which needs project.assets.json.
  // The committed test-workspace carries a restored obj/; this one is generated fresh, so it
  // has to be restored here or every snapshot query fails with NETSDK1004 and the test would
  // report "no diagnostics" for a reason that has nothing to do with the SDK package.
  console.log('Restoring the generated workspace ...');
  execFileSync('dotnet', ['restore', path.join(workspacePath, 'SdkProbe.nproj')], {
    cwd: workspacePath,
    stdio: 'inherit',
  });

  const defaultServerPath = path.resolve(
    extensionDevelopmentPath, '..', 'LspServer', 'bin', 'Release', 'net10.0', 'Nemerle.LanguageServer.dll',
  );
  const serverPath = process.env.NEMERLE_TEST_SERVER_PATH ?? defaultServerPath;
  if (!fs.existsSync(serverPath)) {
    throw new Error(`Integration test server does not exist: ${serverPath}`);
  }

  const executable = await downloadAndUnzipVSCode({ version: '1.128.0' });
  const args = [
    workspacePath,
    '--no-sandbox',
    '--disable-gpu-sandbox',
    '--disable-updates',
    '--skip-welcome',
    '--skip-release-notes',
    '--disable-extensions',
    // Without this the extension refuses to run MSBuild queries at all ("Workspace is not
    // trusted"), which is correct behaviour (runUntrusted.ts asserts it) but would make this
    // suite time out for a reason unrelated to the SDK package.
    '--disable-workspace-trust',
    `--extensionTestsPath=${extensionTestsPath}`,
    `--extensionDevelopmentPath=${extensionDevelopmentPath}`,
    `--user-data-dir=${path.join(extensionDevelopmentPath, '.vscode-test', 'user-data-sdk')}`,
    `--extensions-dir=${path.join(extensionDevelopmentPath, '.vscode-test', 'extensions-sdk')}`,
  ];
  const env = { ...process.env };
  delete env.ELECTRON_RUN_AS_NODE;
  delete env.VSCODE_ESM_ENTRYPOINT;
  env.NEMERLE_TEST_MODE = 'sdk';
  env.NEMERLE_TEST_SERVER_PATH = serverPath;

  await new Promise<void>((resolve, reject) => {
    const child = spawn(executable, args, { env, stdio: 'inherit' });
    const timeout = setTimeout(() => {
      child.kill();
      reject(new Error('Sdk-based VS Code extension test timed out.'));
    }, 180_000);
    child.once('error', (error) => {
      clearTimeout(timeout);
      reject(error);
    });
    child.once('exit', (code, signal) => {
      clearTimeout(timeout);
      if (code === 0) {
        resolve();
      } else {
        reject(new Error(`Sdk-based VS Code extension test exited with ${code ?? signal}.`));
      }
    });
  });
}

/** Newest Nemerle.Sdk.Unofficial.<version>.nupkg in the local feed, or undefined. */
function findSdkPackageVersion(feed: string): string | undefined {
  if (!fs.existsSync(feed)) {
    return undefined;
  }
  const prefix = 'Nemerle.Sdk.Unofficial.';
  const versions = fs.readdirSync(feed)
    .filter((name) => name.startsWith(prefix) && name.endsWith('.nupkg'))
    .map((name) => name.slice(prefix.length, -'.nupkg'.length))
    .sort();
  return versions.at(-1);
}

function writeGeneratedWorkspace(workspacePath: string, feed: string, packageVersion: string): void {
  fs.rmSync(workspacePath, { recursive: true, force: true });
  fs.mkdirSync(workspacePath, { recursive: true });

  // <clear /> so the SDK can only come from the local feed: resolving it from anywhere else
  // would make this test a lie.
  fs.writeFileSync(path.join(workspacePath, 'NuGet.config'), `<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="nemerle-local" value="${feed}" />
  </packageSources>
</configuration>
`);

  // No <Import>, no NemerleCompile items, no boilerplate properties: everything comes from the
  // package, including the **/*.n glob. That is the point of the test.
  fs.writeFileSync(path.join(workspacePath, 'SdkProbe.nproj'), `<Project Sdk="Nemerle.Sdk.Unofficial/${packageVersion}">
  <PropertyGroup>
    <OutputType>Library</OutputType>
    <TargetFramework>net10.0</TargetFramework>
  </PropertyGroup>
</Project>
`);

  // Deliberately broken: assigning a string to an int is a TYPE error, so it can only be
  // reported by an engine that actually analysed the project.
  fs.writeFileSync(path.join(workspacePath, 'SdkProbe.n'), `module SdkProbe
{
  Value() : int
  {
    def value : int = "not an int";
    value
  }
}
`);
}

void main().catch((error: unknown) => {
  console.error(error);
  process.exitCode = 1;
});
