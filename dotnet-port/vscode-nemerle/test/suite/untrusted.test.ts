import assert from 'node:assert/strict';
import * as path from 'node:path';
import * as vscode from 'vscode';
import type { NemerleExtensionApi } from '../../src/extension';

const extensionId = 'nemerle-project.vscode-nemerle';

suite('Nemerle extension in Restricted Mode', () => {
  teardown(async () => {
    await vscode.workspace
      .getConfiguration('nemerle')
      .update('server.path', undefined, vscode.ConfigurationTarget.Global);
    await vscode.commands.executeCommand('workbench.action.closeAllEditors');
  });

  test('keeps language support but never starts the server', async () => {
    assert.equal(vscode.workspace.isTrusted, false);
    const serverPath = process.env.NEMERLE_TEST_SERVER_PATH;
    assert.ok(serverPath);
    await vscode.workspace
      .getConfiguration('nemerle')
      .update('server.path', serverPath, vscode.ConfigurationTarget.Global);

    const folder = vscode.workspace.workspaceFolders?.[0];
    assert.ok(folder);
    const document = await vscode.workspace.openTextDocument(
      vscode.Uri.file(path.join(folder.uri.fsPath, 'Broken.n')),
    );
    await vscode.window.showTextDocument(document);
    assert.equal(document.languageId, 'nemerle');

    const extension = vscode.extensions.getExtension<NemerleExtensionApi>(extensionId);
    assert.ok(extension);
    const api = await extension.activate();
    await delay(750);
    assert.equal(api.serverProcessId, undefined);

    // Command enablement is a UI hint only. The runtime guard must also reject
    // a direct programmatic invocation in an untrusted workspace.
    await vscode.commands.executeCommand('nemerle.restartLanguageServer');
    await delay(750);
    assert.equal(api.serverProcessId, undefined);
  });
});

function delay(milliseconds: number): Promise<void> {
  return new Promise((resolve) => setTimeout(resolve, milliseconds));
}
