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

  test('resolves go-to-definition to the declaration and returns an openable URI', async () => {
    const serverPath = process.env.NEMERLE_TEST_SERVER_PATH;
    assert.ok(serverPath, 'NEMERLE_TEST_SERVER_PATH was not supplied to the extension host');
    await vscode.workspace
      .getConfiguration('nemerle')
      .update('server.path', serverPath, vscode.ConfigurationTarget.Global);

    const uri = vscode.Uri.file(path.join(workspaceRoot(), 'Editing.n'));
    const document = await vscode.workspace.openTextDocument(uri);
    const editor = await vscode.window.showTextDocument(document);

    const extension = vscode.extensions.getExtension<NemerleExtensionApi>(extensionId);
    assert.ok(extension, `Extension ${extensionId} was not found`);
    const api = await extension.activate();
    await waitUntil(() => api.projectStatus?.state === 'applied', 'the single .nproj project snapshot to apply');

    const probeLines = [
      'module Editing',
      '{',
      '  Run() : void',
      '  {',
      '    def target = 1;',
      '    System.Console.WriteLine(target);',
      '  }',
      '}',
      '',
    ];
    const probe = probeLines.join('\n');
    await editor.edit((edit) => {
      const all = new vscode.Range(document.positionAt(0), document.positionAt(document.getText().length));
      edit.replace(all, probe);
    });
    // The usage of "target" inside WriteLine(target) is on line 5 (0-based); its
    // declaration "def target" is on line 4.
    const usageLine = 5;
    const usageChar = (probeLines[usageLine] ?? '').indexOf('target');
    const position = new vscode.Position(usageLine, usageChar);

    // The empty starting content is already diagnostic-free, so waiting on
    // diagnostics would not prove the edited buffer was analyzed; instead poll
    // the definition provider until the engine has rebuilt the new buffer.
    let result: Array<vscode.Location | vscode.LocationLink> = [];
    const deadline = Date.now() + 60_000;
    while (Date.now() < deadline) {
      result =
        (await vscode.commands.executeCommand<Array<vscode.Location | vscode.LocationLink>>(
          'vscode.executeDefinitionProvider',
          uri,
          position,
        )) ?? [];
      if (result.length > 0) {
        break;
      }
      await new Promise((resolve) => setTimeout(resolve, 200));
    }
    assert.ok(result.length > 0, 'the definition provider returned at least one location');

    const first = result[0];
    assert.ok(first, 'the definition result had a first location');
    const targetUri = first instanceof vscode.Location ? first.uri : first.targetUri;
    const targetRange = first instanceof vscode.Location ? first.range : first.targetRange;
    // VS Code parsed the server's file URI into a Uri it can open: its fsPath
    // resolves to the same source file (drive-letter case aside).
    assert.equal(
      targetUri.fsPath.toLowerCase(),
      uri.fsPath.toLowerCase(),
      'the definition location is an openable URI for the same source file',
    );
    assert.equal(targetRange.start.line, 4, 'the definition points at the declaration line');

    await vscode.commands.executeCommand('workbench.action.revertAndCloseActiveEditor');
  });

  // WP-O5a: the differentiator, seen from the real client. `surroundwith` is a
  // keyword only because the buffer opened Nemerle.Surround, so no grammar could
  // know it; the engine reports it as a `macro` token while `def` stays `keyword`.
  test('colors a macro-introduced keyword through semantic tokens', async () => {
    const serverPath = process.env.NEMERLE_TEST_SERVER_PATH;
    assert.ok(serverPath, 'NEMERLE_TEST_SERVER_PATH was not supplied to the extension host');
    await vscode.workspace
      .getConfiguration('nemerle')
      .update('server.path', serverPath, vscode.ConfigurationTarget.Global);

    const uri = vscode.Uri.file(path.join(workspaceRoot(), 'Editing.n'));
    const document = await vscode.workspace.openTextDocument(uri);
    const editor = await vscode.window.showTextDocument(document);

    const extension = vscode.extensions.getExtension<NemerleExtensionApi>(extensionId);
    assert.ok(extension, `Extension ${extensionId} was not found`);
    const api = await extension.activate();
    await waitUntil(() => api.projectStatus?.state === 'applied', 'the single .nproj project snapshot to apply');

    const probeLines = [
      'using Nemerle.Surround;',
      '',
      'module Editing',
      '{',
      '  Run() : void',
      '  {',
      '    def value = 1;',
      '    surroundwith (probe)',
      '      System.Console.WriteLine(value);',
      '  }',
      '}',
      '',
    ];
    await editor.edit((edit) => {
      const all = new vscode.Range(document.positionAt(0), document.positionAt(document.getText().length));
      edit.replace(all, probeLines.join('\n'));
    });

    const legend = await vscode.commands.executeCommand<vscode.SemanticTokensLegend>(
      'vscode.provideDocumentSemanticTokensLegend',
      uri,
    );
    assert.ok(legend, 'the client registered a semantic tokens legend for the document');
    assert.ok(legend.tokenTypes.includes('macro'), 'the legend carries the macro token type');
    assert.ok(legend.tokenModifiers.includes('quotation'), 'the legend carries the Nemerle quotation modifier');

    // Both words start at column 4 of their line (0-based).
    const defKey = '6:4';
    const macroKey = '7:4';
    let types = new Map<string, string>();
    const deadline = Date.now() + 60_000;
    while (Date.now() < deadline) {
      const tokens = await vscode.commands.executeCommand<vscode.SemanticTokens | undefined>(
        'vscode.provideDocumentSemanticTokens',
        uri,
      );
      types = decodeSemanticTokenTypes(tokens, legend);
      if (types.get(macroKey) === 'macro') {
        break;
      }
      await new Promise((resolve) => setTimeout(resolve, 200));
    }

    assert.equal(types.get(macroKey), 'macro', 'surroundwith is colored as a macro-introduced keyword');
    assert.equal(types.get(defKey), 'keyword', 'def stays a plain keyword');

    await vscode.commands.executeCommand('workbench.action.revertAndCloseActiveEditor');
  });
});

/**
 * Decodes the LSP/VS Code semantic token delta encoding into a
 * "line:character" -> token type name map (0-based positions).
 */
function decodeSemanticTokenTypes(
  tokens: vscode.SemanticTokens | undefined,
  legend: vscode.SemanticTokensLegend,
): Map<string, string> {
  const result = new Map<string, string>();
  if (!tokens) {
    return result;
  }

  let line = 0;
  let character = 0;
  for (let i = 0; i + 4 < tokens.data.length; i += 5) {
    const deltaLine = tokens.data[i] ?? 0;
    const deltaStart = tokens.data[i + 1] ?? 0;
    line += deltaLine;
    character = deltaLine === 0 ? character + deltaStart : deltaStart;
    result.set(`${line}:${character}`, legend.tokenTypes[tokens.data[i + 3] ?? -1] ?? '');
  }

  return result;
}

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
