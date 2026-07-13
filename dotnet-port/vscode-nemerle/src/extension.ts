import * as vscode from 'vscode';
import { NemerleClientController } from './clientController';

let controller: NemerleClientController | undefined;

export interface NemerleExtensionApi {
  readonly serverProcessId: number | undefined;
}

export function activate(context: vscode.ExtensionContext): NemerleExtensionApi {
  const output = vscode.window.createOutputChannel('Nemerle Language Server', { log: true });
  controller = new NemerleClientController(output);

  context.subscriptions.push(
    controller,
    vscode.commands.registerCommand('nemerle.restartLanguageServer', async () => {
      await controller?.restart();
    }),
    vscode.commands.registerCommand('nemerle.showOutput', () => {
      controller?.showOutput();
    }),
    vscode.workspace.onDidGrantWorkspaceTrust(() => {
      void controller?.start();
    }),
    vscode.workspace.onDidChangeConfiguration((event) => {
      if (event.affectsConfiguration('nemerle.server.path')) {
        void controller?.restart();
      } else if (event.affectsConfiguration('nemerle.server.trace')) {
        void controller?.updateTrace();
      }
    }),
  );

  void controller.start();
  return {
    get serverProcessId(): number | undefined {
      return controller?.serverProcessId;
    },
  };
}

export async function deactivate(): Promise<void> {
  const current = controller;
  controller = undefined;
  await current?.disposeAsync();
}
