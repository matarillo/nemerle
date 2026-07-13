import { spawn } from 'node:child_process';
import * as fs from 'node:fs';
import * as path from 'node:path';
import { downloadAndUnzipVSCode } from '@vscode/test-electron';

async function main(): Promise<void> {
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

  const executable = await downloadAndUnzipVSCode({ version: '1.128.0' });
  const args = [
    workspacePath,
    '--no-sandbox',
    '--disable-gpu-sandbox',
    '--disable-updates',
    '--skip-welcome',
    '--skip-release-notes',
    '--disable-extensions',
    `--extensionTestsPath=${extensionTestsPath}`,
    `--extensionDevelopmentPath=${extensionDevelopmentPath}`,
    `--user-data-dir=${path.join(extensionDevelopmentPath, '.vscode-test', 'user-data-untrusted')}`,
    `--extensions-dir=${path.join(extensionDevelopmentPath, '.vscode-test', 'extensions-untrusted')}`,
  ];
  const env = { ...process.env };
  delete env.ELECTRON_RUN_AS_NODE;
  delete env.VSCODE_ESM_ENTRYPOINT;
  env.NEMERLE_TEST_MODE = 'untrusted';
  env.NEMERLE_TEST_SERVER_PATH = serverPath;

  await new Promise<void>((resolve, reject) => {
    const child = spawn(executable, args, { env, stdio: 'inherit' });
    const timeout = setTimeout(() => {
      child.kill();
      reject(new Error('Untrusted VS Code extension test timed out.'));
    }, 90_000);
    child.once('error', (error) => {
      clearTimeout(timeout);
      reject(error);
    });
    child.once('exit', (code, signal) => {
      clearTimeout(timeout);
      if (code === 0) {
        resolve();
      } else {
        reject(new Error(`Untrusted VS Code extension test exited with ${code ?? signal}.`));
      }
    });
  });
}

void main().catch((error: unknown) => {
  console.error(error);
  process.exitCode = 1;
});
