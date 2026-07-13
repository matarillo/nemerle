import * as vscode from 'vscode';
import { NemerleClientController } from './clientController';
import { NemerleProjectController, type ProjectStatusSnapshot } from './projectController';

let controller: NemerleClientController | undefined;
let projectController: NemerleProjectController | undefined;

export interface NemerleExtensionApi {
  readonly serverProcessId: number | undefined;
  readonly projectStatus: ProjectStatusSnapshot | undefined;
}

export function activate(context: vscode.ExtensionContext): NemerleExtensionApi {
  const output = vscode.window.createOutputChannel('Nemerle Language Server', { log: true });
  controller = new NemerleClientController(output);
  projectController = new NemerleProjectController(context, controller, output);

  context.subscriptions.push(
    controller,
    vscode.commands.registerCommand('nemerle.restartLanguageServer', async () => {
      await controller?.restart();
      await projectController?.initialize();
    }),
    vscode.commands.registerCommand('nemerle.showOutput', () => {
      controller?.showOutput();
    }),
    vscode.commands.registerCommand('nemerle.selectProject', async () => {
      await projectController?.selectProject();
    }),
    vscode.commands.registerCommand('nemerle.reloadProject', async () => {
      await projectController?.reloadProject();
    }),
    vscode.commands.registerCommand('nemerle.showProjectStatus', () => {
      projectController?.showStatus();
    }),
    vscode.workspace.onDidGrantWorkspaceTrust(() => {
      void controller?.start().then(() => projectController?.initialize());
    }),
    vscode.workspace.onDidChangeConfiguration((event) => {
      if (event.affectsConfiguration('nemerle.server.path') ||
          event.affectsConfiguration('nemerle.dotnet.path')) {
        void controller?.restart().then(() => projectController?.initialize());
      } else if (event.affectsConfiguration('nemerle.server.trace')) {
        void controller?.updateTrace();
      } else if (event.affectsConfiguration('nemerle.project')) {
        void projectController?.initialize();
      } else if (event.affectsConfiguration('nemerle.projectConfiguration') ||
                 event.affectsConfiguration('nemerle.projectPlatform') ||
                 event.affectsConfiguration('nemerle.projectTargetFramework')) {
        void projectController?.reloadProject();
      }
    }),
  );

  void controller.start().then(() => projectController?.initialize());
  return {
    get serverProcessId(): number | undefined {
      return controller?.serverProcessId;
    },
    get projectStatus(): ProjectStatusSnapshot | undefined {
      return projectController?.currentStatus;
    },
  };
}

export async function deactivate(): Promise<void> {
  const current = controller;
  const currentProject = projectController;
  controller = undefined;
  projectController = undefined;
  currentProject?.dispose();
  await current?.disposeAsync();
}
