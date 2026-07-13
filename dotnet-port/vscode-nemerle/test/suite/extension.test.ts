import assert from 'node:assert/strict';
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

suite('Nemerle extension', () => {
  suiteTeardown(async () => {
    await vscode.workspace
      .getConfiguration('nemerle')
      .update('server.path', undefined, vscode.ConfigurationTarget.Global);
    await vscode.commands.executeCommand('workbench.action.closeAllEditors');
  });

  test('registers .n and supplies line/block comment editing', async () => {
    const uri = vscode.Uri.file(path.join(workspaceRoot(), 'Editing.n'));
    const document = await vscode.workspace.openTextDocument(uri);
    const editor = await vscode.window.showTextDocument(document);
    assert.equal(document.languageId, 'nemerle');

    editor.selection = new vscode.Selection(0, 0, 0, 0);
    await vscode.commands.executeCommand('editor.action.commentLine');
    assert.match(document.lineAt(0).text, /^\s*\/\//u);
    await vscode.commands.executeCommand('editor.action.commentLine');
    assert.doesNotMatch(document.lineAt(0).text, /^\s*\/\//u);

    editor.selection = new vscode.Selection(0, 0, 0, 'module'.length);
    await vscode.commands.executeCommand('editor.action.blockComment');
    assert.match(document.lineAt(0).text, /^\/\*\s*module\s*\*\/\s*Editing/u);
    await vscode.commands.executeCommand('workbench.action.revertAndCloseActiveEditor');
  });

  test('applies auto-closing, auto-surrounding, and brace indentation', async () => {
    const document = await vscode.workspace.openTextDocument({ language: 'nemerle', content: '' });
    const editor = await vscode.window.showTextDocument(document);

    await vscode.commands.executeCommand('type', { text: '{' });
    assert.equal(document.getText(), '{}');

    editor.selection = new vscode.Selection(0, 0, 0, 2);
    await vscode.commands.executeCommand('type', { text: '"' });
    assert.equal(document.getText(), '"{}"');

    await editor.edit((edit) => {
      edit.replace(new vscode.Range(0, 0, 0, document.lineAt(0).text.length), '{');
    });
    editor.selection = new vscode.Selection(0, 1, 0, 1);
    await vscode.commands.executeCommand('editor.action.insertLineAfter');
    await vscode.commands.executeCommand('type', { text: 'value' });
    assert.match(document.lineAt(1).text, /^\s+value$/u);
    await vscode.commands.executeCommand('workbench.action.revertAndCloseActiveEditor');
  });

  test('shows and clears unsaved diagnostics and restarts without retaining the old process', async () => {
    const serverPath = process.env.NEMERLE_TEST_SERVER_PATH;
    assert.ok(serverPath, 'NEMERLE_TEST_SERVER_PATH was not supplied to the extension host');
    await vscode.workspace
      .getConfiguration('nemerle')
      .update('server.path', serverPath, vscode.ConfigurationTarget.Global);

    const uri = vscode.Uri.file(path.join(workspaceRoot(), 'Broken.n'));
    const document = await vscode.workspace.openTextDocument(uri);
    const editor = await vscode.window.showTextDocument(document);
    assert.equal(document.languageId, 'nemerle');

    const extension = vscode.extensions.getExtension<NemerleExtensionApi>(extensionId);
    assert.ok(extension, `Extension ${extensionId} was not found`);
    const api = await extension.activate();

    await waitUntil(
      () => api.projectStatus?.state === 'applied',
      'the single .nproj project snapshot to load and apply automatically',
    );
    assert.equal(api.projectStatus?.result?.sourceCount, 1);
    assert.equal(api.projectStatus?.result?.assemblyReferenceCount, 0);
    assert.equal(api.projectStatus?.result?.macroReferenceCount, 0);
    assert.equal(api.projectStatus?.result?.appliedToEngine, true);
    assert.equal(api.projectStatus?.result?.sourceFiles.length, 1);
    assert.match(api.projectStatus?.result?.sourceFiles[0] ?? '', /Editing\.n$/u);
    const selection = vscode.commands.executeCommand('nemerle.selectProject');
    await new Promise((resolve) => setTimeout(resolve, 250));
    await vscode.commands.executeCommand('workbench.action.acceptSelectedQuickOpenItem');
    await selection;
    await waitUntil(() => api.projectStatus?.state === 'applied', 'the QuickPick project selection to apply');
    await vscode.commands.executeCommand('nemerle.reloadProject');
    await waitUntil(() => api.projectStatus?.state === 'applied', 'the project snapshot to reload and apply');
    await vscode.commands.executeCommand('nemerle.showProjectStatus');

    await waitUntil(
      () => vscode.languages.getDiagnostics(uri).some((diagnostic) =>
        diagnostic.severity === vscode.DiagnosticSeverity.Error),
      'an error diagnostic for Broken.n',
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

    // A project source: the unsaved buffer overrides the clean disk content,
    // and revert+close returns the file to disk-backed analysis.
    const editingUri = vscode.Uri.file(path.join(workspaceRoot(), 'Editing.n'));
    const editingDocument = await vscode.workspace.openTextDocument(editingUri);
    const editingEditor = await vscode.window.showTextDocument(editingDocument);
    await editingEditor.edit((edit) => {
      const all = new vscode.Range(
        editingDocument.positionAt(0),
        editingDocument.positionAt(editingDocument.getText().length));
      edit.replace(all, 'module Editing\n{\n  Bad() : void\n  {\n    def value : int = "wrong";\n    System.Console.WriteLine(value);\n  }\n}\n');
    });
    assert.equal(editingDocument.isDirty, true);
    await waitUntil(
      () => vscode.languages.getDiagnostics(editingUri).some((diagnostic) =>
        diagnostic.severity === vscode.DiagnosticSeverity.Error),
      'an error diagnostic for the unsaved project source Editing.n',
    );
    await vscode.commands.executeCommand('workbench.action.revertAndCloseActiveEditor');
    await waitUntil(
      () => vscode.languages.getDiagnostics(editingUri).length === 0,
      'project source diagnostics to clear after revert and close',
    );

    const firstPid = await waitForPid(api);
    await vscode.commands.executeCommand('nemerle.restartLanguageServer');
    const secondPid = await waitForPid(api, firstPid);
    assert.notEqual(secondPid, firstPid);
    await waitUntil(() => !processExists(firstPid), `old server process ${firstPid} to exit`);

    await vscode.commands.executeCommand('nemerle.showOutput');
    await vscode.commands.executeCommand('workbench.action.revertAndCloseActiveEditor');
  });
});

function workspaceRoot(): string {
  const folder = vscode.workspace.workspaceFolders?.[0];
  assert.ok(folder);
  return folder.uri.fsPath;
}

async function waitForPid(api: NemerleExtensionApi, previous?: number): Promise<number> {
  let result: number | undefined;
  await waitUntil(() => {
    const pid = api.serverProcessId;
    if (pid !== undefined && pid !== previous) {
      result = pid;
      return true;
    }
    return false;
  }, previous === undefined ? 'language server process to start' : 'replacement server process to start');
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

function processExists(pid: number): boolean {
  try {
    process.kill(pid, 0);
    return true;
  } catch {
    return false;
  }
}
