import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import * as fs from 'node:fs';
import * as path from 'node:path';
import * as vscode from 'vscode';
import type { NemerleExtensionApi } from '../../src/extension';

const extensionId = 'nemerle-project.vscode-nemerle';
const fixedSource = `module Broken
{
  Main() : void
  {
    def value : int = 1;
    System.Console.WriteLine(value);
  }
}
`;

suite('Nemerle VSIX clean-machine install', () => {
  suiteTeardown(async () => {
    await vscode.commands.executeCommand('workbench.action.closeAllEditors');
  });

  test('bundled language server starts from the installed VSIX without configuration', async () => {
    const extensionsDir = process.env.NEMERLE_TEST_EXTENSIONS_DIR;
    assert.ok(extensionsDir, 'NEMERLE_TEST_EXTENSIONS_DIR was not supplied to the extension host');

    // The extension under test is the installed VSIX in the isolated
    // extensions dir, not this repository's development copy.
    const extension = vscode.extensions.getExtension<NemerleExtensionApi>(extensionId);
    assert.ok(extension, `Extension ${extensionId} was not found`);
    const normalizedExtensionPath = normalize(extension.extensionPath);
    assert.ok(
      normalizedExtensionPath.startsWith(normalize(extensionsDir) + path.sep),
      `Extension was not loaded from the isolated extensions dir: ${extension.extensionPath}`,
    );
    const bundledServer = path.join(extension.extensionPath, 'server', 'Nemerle.LanguageServer.dll');
    assert.ok(fs.existsSync(bundledServer), `Bundled server missing in the installed VSIX: ${bundledServer}`);
    assert.ok(
      fs.existsSync(path.join(extension.extensionPath, 'server', 'Nemerle.LanguageServer.runtimeconfig.json')),
      'Bundled runtimeconfig.json missing in the installed VSIX',
    );
    assert.ok(
      fs.existsSync(path.join(extension.extensionPath, 'THIRD-PARTY-NOTICES.md')),
      'THIRD-PARTY-NOTICES.md missing in the installed VSIX',
    );

    // Clean machine equivalent: no server path is configured anywhere.
    assert.equal(vscode.workspace.getConfiguration('nemerle').get<string>('server.path', ''), '');

    const uri = vscode.Uri.file(path.join(workspaceRoot(), 'Broken.n'));
    const document = await vscode.workspace.openTextDocument(uri);
    const editor = await vscode.window.showTextDocument(document);
    assert.equal(document.languageId, 'nemerle');

    const api = await extension.activate();
    const pid = await waitForPid(api);

    // The running server process must be the bundled one.
    const commandLine = processCommandLine(pid);
    assert.ok(
      normalize(commandLine).includes(normalize(bundledServer)),
      `Server process ${pid} command line does not reference the bundled server: ${commandLine}`,
    );

    await waitUntil(
      () => api.projectStatus?.state === 'applied',
      'the single .nproj project snapshot to load and apply automatically',
    );
    assert.equal(api.projectStatus?.result?.appliedToEngine, true);

    await waitUntil(
      () => vscode.languages.getDiagnostics(uri).some((diagnostic) =>
        diagnostic.severity === vscode.DiagnosticSeverity.Error),
      'an error diagnostic for Broken.n from the bundled server',
    );

    await editor.edit((edit) => {
      const all = new vscode.Range(document.positionAt(0), document.positionAt(document.getText().length));
      edit.replace(all, fixedSource);
    });
    assert.equal(document.isDirty, true);
    await waitUntil(
      () => vscode.languages.getDiagnostics(uri).length === 0,
      'diagnostics to clear after the unsaved fix',
    );
    await vscode.commands.executeCommand('workbench.action.revertAndCloseActiveEditor');
  });
});

function workspaceRoot(): string {
  const folder = vscode.workspace.workspaceFolders?.[0];
  assert.ok(folder);
  return folder.uri.fsPath;
}

function normalize(value: string): string {
  // Windows paths compare case-insensitively and mix separators; POSIX paths
  // are case-sensitive and already use '/', so they must compare verbatim.
  if (process.platform === 'win32') {
    return value.replace(/\//gu, '\\').toLowerCase();
  }
  return value;
}

function processCommandLine(pid: number): string {
  if (process.platform === 'linux') {
    // /proc/<pid>/cmdline is the argv vector, NUL-separated.
    return fs.readFileSync(`/proc/${pid}/cmdline`, 'utf8').split('\0').join(' ').trim();
  }
  if (process.platform === 'win32') {
    const result = spawnSync('powershell.exe', [
      '-NoProfile',
      '-Command',
      `(Get-CimInstance Win32_Process -Filter "ProcessId=${pid}").CommandLine`,
    ], { encoding: 'utf8', shell: false });
    assert.equal(result.status, 0, `Could not query the command line of process ${pid}: ${result.stderr}`);
    return result.stdout.trim();
  }
  const result = spawnSync('ps', ['-p', String(pid), '-o', 'command='], { encoding: 'utf8', shell: false });
  assert.equal(result.status, 0, `Could not query the command line of process ${pid}: ${result.stderr}`);
  return result.stdout.trim();
}

async function waitForPid(api: NemerleExtensionApi): Promise<number> {
  let result: number | undefined;
  await waitUntil(() => {
    const pid = api.serverProcessId;
    if (pid !== undefined) {
      result = pid;
      return true;
    }
    return false;
  }, 'the bundled language server process to start');
  return result as number;
}

async function waitUntil(predicate: () => boolean, description: string): Promise<void> {
  const deadline = Date.now() + 60_000;
  while (Date.now() < deadline) {
    if (predicate()) {
      return;
    }
    await new Promise((resolve) => setTimeout(resolve, 100));
  }
  throw new Error(`Timed out waiting for ${description}.`);
}
