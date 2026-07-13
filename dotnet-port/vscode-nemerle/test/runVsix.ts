import { spawn, spawnSync } from 'node:child_process';
import * as fs from 'node:fs';
import * as path from 'node:path';
import {
  downloadAndUnzipVSCode,
  resolveCliPathFromVSCodeExecutablePath,
} from '@vscode/test-electron';

/**
 * WP-L4 clean-machine equivalent: installs the packaged VSIX into a fresh,
 * isolated extensions-dir/user-data-dir pair and runs the vsix test suite in
 * that VS Code instance.  nemerle.server.path stays unset, so the extension
 * must resolve and start the language server bundled inside the installed
 * VSIX.  The development copy of this repository's extension is NOT loaded
 * (--extensionDevelopmentPath points at a manifest-only driver extension).
 */
async function main(): Promise<void> {
  const extensionDevelopmentPath = path.resolve(__dirname, '..', '..');
  const driverPath = path.join(extensionDevelopmentPath, 'test', 'vsix-driver');
  const extensionTestsPath = path.resolve(__dirname, 'suite', 'index');
  const workspacePath = path.join(extensionDevelopmentPath, 'test-workspace');

  const manifest = JSON.parse(
    fs.readFileSync(path.join(extensionDevelopmentPath, 'package.json'), 'utf8'),
  ) as { version: string };
  const vsixPath = path.join(extensionDevelopmentPath, `vscode-nemerle-${manifest.version}.vsix`);
  if (!fs.existsSync(vsixPath)) {
    throw new Error(`VSIX not found: ${vsixPath}. Run "npm run package" first.`);
  }

  // Fresh isolation directories = the clean-machine part of the scenario.
  const userDataDir = path.join(extensionDevelopmentPath, '.vscode-test', 'user-data-vsix');
  const extensionsDir = path.join(extensionDevelopmentPath, '.vscode-test', 'extensions-vsix');
  fs.rmSync(userDataDir, { recursive: true, force: true });
  fs.rmSync(extensionsDir, { recursive: true, force: true });

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

  const executable = await downloadAndUnzipVSCode({ version: '1.128.0' });
  const cliPath = resolveCliPathFromVSCodeExecutablePath(executable);

  const install = spawnSync(cliPath, [
    '--install-extension', vsixPath,
    '--extensions-dir', extensionsDir,
    '--user-data-dir', userDataDir,
  ], {
    encoding: 'utf8',
    stdio: 'inherit',
    // The Windows CLI is bin\code.cmd; cmd shims need a shell.
    shell: process.platform === 'win32',
  });
  if (install.error !== undefined) {
    throw install.error;
  }
  if (install.status !== 0) {
    throw new Error(`VSIX installation failed with exit code ${install.status ?? 'unknown'}.`);
  }

  const args = [
    workspacePath,
    '--no-sandbox',
    '--disable-gpu-sandbox',
    '--disable-updates',
    '--skip-welcome',
    '--skip-release-notes',
    '--disable-workspace-trust',
    `--extensionTestsPath=${extensionTestsPath}`,
    `--extensionDevelopmentPath=${driverPath}`,
    `--user-data-dir=${userDataDir}`,
    `--extensions-dir=${extensionsDir}`,
  ];
  const env = { ...process.env };
  delete env.ELECTRON_RUN_AS_NODE;
  delete env.VSCODE_ESM_ENTRYPOINT;
  delete env.NEMERLE_TEST_SERVER_PATH;
  env.NEMERLE_TEST_MODE = 'vsix';
  env.NEMERLE_TEST_EXTENSIONS_DIR = extensionsDir;

  await new Promise<void>((resolve, reject) => {
    const child = spawn(executable, args, { env, stdio: 'inherit' });
    const timeout = setTimeout(() => {
      child.kill();
      reject(new Error('VSIX VS Code extension test timed out.'));
    }, 300_000);
    child.once('error', (error) => {
      clearTimeout(timeout);
      reject(error);
    });
    child.once('exit', (code, signal) => {
      clearTimeout(timeout);
      if (code === 0) {
        resolve();
      } else {
        reject(new Error(`VSIX VS Code extension test exited with ${code ?? signal}.`));
      }
    });
  });
}

void main().catch((error: unknown) => {
  console.error(error);
  process.exitCode = 1;
});
