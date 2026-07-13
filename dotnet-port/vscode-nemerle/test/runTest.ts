import * as fs from 'node:fs';
import * as path from 'node:path';
import { spawnSync } from 'node:child_process';
import { runTests } from '@vscode/test-electron';

async function main(): Promise<void> {
  // VS Code's integrated extension host sets these for its own child process.
  // A downloaded Code.exe must start as Electron, not inherit Node mode.
  delete process.env.ELECTRON_RUN_AS_NODE;
  delete process.env.VSCODE_ESM_ENTRYPOINT;

  const extensionDevelopmentPath = path.resolve(__dirname, '..', '..');
  const extensionTestsPath = path.resolve(__dirname, 'suite', 'index');
  const workspacePath = path.join(extensionDevelopmentPath, 'test-workspace');
  const defaultServerPath = path.resolve(
    extensionDevelopmentPath,
    '..',
    'LspServer',
    'bin',
    'Release',
    'net10.0',
    'Nemerle.LanguageServer.dll',
  );
  const serverPath = process.env.NEMERLE_TEST_SERVER_PATH ?? defaultServerPath;
  if (!fs.existsSync(serverPath)) {
    throw new Error(`Integration test server does not exist: ${serverPath}`);
  }

  const restore = spawnSync('dotnet', ['restore', path.join(workspacePath, 'Single.nproj')], {
    cwd: workspacePath,
    shell: false,
    stdio: 'inherit',
  });
  if (restore.error !== undefined) {
    throw restore.error;
  }
  if (restore.status !== 0) {
    throw new Error(`Integration test project restore failed with exit code ${restore.status ?? 'unknown'}.`);
  }

  await runTests({
    version: '1.128.0',
    extensionDevelopmentPath,
    extensionTestsPath,
    extensionTestsEnv: {
      NEMERLE_TEST_SERVER_PATH: serverPath,
    },
    launchArgs: [
      workspacePath,
      '--disable-extensions',
      '--disable-workspace-trust',
      '--user-data-dir',
      path.join(extensionDevelopmentPath, '.vscode-test', 'user-data-trusted'),
    ],
  });
}

void main().catch((error: unknown) => {
  console.error(error);
  process.exitCode = 1;
});
