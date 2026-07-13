import * as path from 'node:path';
import * as vscode from 'vscode';
import {
  type NemerleClientController,
  type ProjectInfoLoadResult,
} from './clientController';
import {
  isExcludedProjectPath,
  normalizeComparablePath,
  resolveSelectedProject,
  sortAndDedupeProjects,
} from './projectDiscovery';

const selectedProjectKey = 'nemerle.selectedProject';
const discoveryExclude = '**/{bin,obj,node_modules,.git,.vs,dist}/**';
const maxReferenceWatchers = 64;

export interface ProjectStatusSnapshot {
  readonly state: string;
  readonly selectedProject?: string;
  readonly result?: ProjectInfoLoadResult;
  readonly message: string;
}

export class NemerleProjectController implements vscode.Disposable {
  private readonly statusItem: vscode.StatusBarItem;
  private readonly watcher: vscode.FileSystemWatcher;
  private readonly importsWatcher: vscode.FileSystemWatcher;
  private readonly assetsWatcher: vscode.FileSystemWatcher;
  private readonly sourceWatcher: vscode.FileSystemWatcher;
  private referenceWatchers: vscode.FileSystemWatcher[] = [];
  private watchedSourceFiles = new Set<string>();
  private selectedProject: string | undefined;
  private status: ProjectStatusSnapshot = { state: 'unselected', message: 'No project selected.' };
  private debounceTimer: ReturnType<typeof setTimeout> | undefined;
  private pendingForceReload = false;
  private generation = 0;
  private loadOperation: Promise<void> = Promise.resolve();
  private disposed = false;

  public constructor(
    private readonly context: vscode.ExtensionContext,
    private readonly client: NemerleClientController,
    private readonly output: vscode.LogOutputChannel,
  ) {
    this.statusItem = vscode.window.createStatusBarItem(
      'nemerle.projectStatus',
      vscode.StatusBarAlignment.Left,
      10,
    );
    this.statusItem.name = 'Nemerle Project';
    this.statusItem.command = 'nemerle.showProjectStatus';
    this.statusItem.show();
    this.watcher = vscode.workspace.createFileSystemWatcher('**/*.nproj');
    this.watcher.onDidCreate((uri) => this.scheduleDiscovery(uri), this, context.subscriptions);
    this.watcher.onDidDelete((uri) => this.scheduleDiscovery(uri), this, context.subscriptions);
    this.watcher.onDidChange((uri) => this.scheduleProjectFileReload(uri), this, context.subscriptions);
    // Imported targets/props (including obj/*.nuget.g.*) and NuGet assets
    // change the MSBuild evaluation result, so they force a fresh snapshot
    // query.  Plain .n disk edits only need the cached snapshot re-applied so
    // the engine rereads closed sources from disk.
    this.importsWatcher = vscode.workspace.createFileSystemWatcher('**/*.{targets,props}');
    this.importsWatcher.onDidCreate(() => this.scheduleSelectedReload(true), this, context.subscriptions);
    this.importsWatcher.onDidChange(() => this.scheduleSelectedReload(true), this, context.subscriptions);
    this.importsWatcher.onDidDelete(() => this.scheduleSelectedReload(true), this, context.subscriptions);
    this.assetsWatcher = vscode.workspace.createFileSystemWatcher('**/obj/project.assets.json');
    this.assetsWatcher.onDidCreate(() => this.scheduleSelectedReload(true), this, context.subscriptions);
    this.assetsWatcher.onDidChange(() => this.scheduleSelectedReload(true), this, context.subscriptions);
    this.assetsWatcher.onDidDelete(() => this.scheduleSelectedReload(true), this, context.subscriptions);
    this.sourceWatcher = vscode.workspace.createFileSystemWatcher('**/*.n');
    this.sourceWatcher.onDidCreate((uri) => this.scheduleSourceRefresh(uri), this, context.subscriptions);
    this.sourceWatcher.onDidChange((uri) => this.scheduleSourceRefresh(uri), this, context.subscriptions);
    this.sourceWatcher.onDidDelete((uri) => this.scheduleSourceRefresh(uri), this, context.subscriptions);
    context.subscriptions.push(
      this.statusItem,
      this.watcher,
      this.importsWatcher,
      this.assetsWatcher,
      this.sourceWatcher,
    );
    this.renderStatus();
  }

  public get currentStatus(): ProjectStatusSnapshot {
    return this.status;
  }

  public async initialize(): Promise<void> {
    if (this.disposed) {
      return;
    }
    if (!vscode.workspace.isTrusted) {
      this.selectedProject = undefined;
      this.setStatus('restricted', 'Workspace is not trusted; server and MSBuild project queries are disabled.');
      return;
    }
    const folders = vscode.workspace.workspaceFolders ?? [];
    if (folders.length === 0) {
      this.selectedProject = undefined;
      this.setStatus('unselected', 'Open a folder containing a .nproj project.');
      return;
    }
    if (folders.length !== 1) {
      this.selectedProject = undefined;
      this.setStatus('unsupported', 'A single workspace folder with one selected project is supported.');
      return;
    }
    const folder = folders[0];
    if (folder === undefined) {
      return;
    }

    const candidates = await this.discoverProjects();
    if (candidates.length === 0) {
      this.selectedProject = undefined;
      this.setStatus('empty', 'No Nemerle .nproj projects were found in the workspace.');
      return;
    }
    const configured = vscode.workspace.getConfiguration('nemerle').get<string>('project', '');
    const stored = this.context.workspaceState.get<string>(selectedProjectKey);
    const selection = resolveSelectedProject(folder.uri.fsPath, candidates, configured, stored);
    if (selection.configuredMissing !== undefined) {
      this.selectedProject = undefined;
      this.setStatus('error', `Configured Nemerle project was not found among workspace candidates: ${selection.configuredMissing}`);
      return;
    }
    if (selection.selected === undefined) {
      this.selectedProject = undefined;
      this.setStatus('unselected', `${candidates.length} Nemerle projects found. Use Nemerle: Select Project.`);
      return;
    }
    this.selectedProject = selection.selected;
    await this.context.workspaceState.update(selectedProjectKey, this.selectedProject);
    await this.loadSelected(false);
  }

  public async selectProject(): Promise<void> {
    if (!this.ensureTrusted('select a project')) {
      return;
    }
    const folders = vscode.workspace.workspaceFolders ?? [];
    if (folders.length !== 1) {
      this.setStatus('unsupported', 'A single workspace folder with one selected project is supported.');
      return;
    }
    const folder = folders[0];
    if (folder === undefined) {
      return;
    }
    const candidates = await this.discoverProjects();
    if (candidates.length === 0) {
      this.setStatus('empty', 'No Nemerle .nproj projects were found in the workspace.');
      return;
    }
    const items = candidates.map((projectPath) => ({
      label: path.basename(projectPath),
      description: path.relative(folder.uri.fsPath, projectPath),
      projectPath,
    }));
    const selected = await vscode.window.showQuickPick(items, {
      placeHolder: 'Select the Nemerle project used for the engine workspace',
      matchOnDescription: true,
    });
    if (selected === undefined) {
      return;
    }
    this.selectedProject = selected.projectPath;
    await this.context.workspaceState.update(selectedProjectKey, this.selectedProject);
    await this.loadSelected(true);
  }

  public async reloadProject(): Promise<void> {
    if (!this.ensureTrusted('reload the project')) {
      return;
    }
    this.clearSchedule();
    if (this.selectedProject === undefined) {
      await this.initialize();
      if (this.selectedProject === undefined) {
        void vscode.window.showInformationMessage(this.status.message);
      }
      return;
    }
    await this.loadSelected(true);
  }

  public showStatus(): void {
    this.output.info(this.formatStatus());
    this.output.show(true);
    void vscode.window.showInformationMessage(this.formatStatus());
  }

  public dispose(): void {
    this.disposed = true;
    this.generation++;
    this.clearSchedule();
    this.disposeReferenceWatchers();
  }

  private async discoverProjects(): Promise<string[]> {
    const uris = await vscode.workspace.findFiles('**/*.nproj', discoveryExclude);
    return sortAndDedupeProjects(uris.map((uri) => uri.fsPath));
  }

  private loadSelected(forceReload: boolean): Promise<void> {
    const run = async (): Promise<void> => this.loadSelectedCore(forceReload);
    const result = this.loadOperation.then(run, run);
    this.loadOperation = result.catch((error: unknown) => {
      const message = error instanceof Error ? error.message : String(error);
      this.output.error(`Nemerle project operation failed: ${message}`);
    });
    return result;
  }

  private async loadSelectedCore(forceReload: boolean): Promise<void> {
    const projectPath = this.selectedProject;
    if (projectPath === undefined) {
      return;
    }
    const currentGeneration = ++this.generation;
    this.setStatus('loading', `Loading project snapshot: ${projectPath}`);
    const configuration = vscode.workspace.getConfiguration('nemerle');
    try {
      const result = await this.client.loadProject({
        projectPath,
        configuration: configuration.get<string>('projectConfiguration', 'Debug'),
        platform: configuration.get<string>('projectPlatform', 'AnyCPU'),
        targetFramework: configuration.get<string>('projectTargetFramework', ''),
        forceReload,
      });
      if (this.disposed || currentGeneration !== this.generation) {
        return;
      }
      if (result === undefined) {
        this.setStatus('error', 'The language server is not running; project snapshot was not loaded.');
      } else if (result.state === 'error') {
        const message = `${result.errorKind ?? 'QueryError'}: ${result.errorMessage ?? 'Project query failed.'}`;
        this.setStatus('error', message, result);
        this.output.error(message);
        if (result.errorDetails !== undefined && result.errorDetails.length > 0) {
          this.output.error(result.errorDetails);
        }
        void vscode.window.showErrorMessage(`Nemerle project load failed. ${message}`, 'Show Output').then((choice) => {
          if (choice === 'Show Output') {
            this.output.show(true);
          }
        });
      } else if (!result.appliedToEngine) {
        const message = `Project snapshot loaded but was not applied to the analysis engine${result.applyError !== undefined ? `: ${result.applyError}` : '.'}`;
        this.setStatus('notApplied', message, result);
        this.output.error(message);
      } else {
        this.watchedSourceFiles = new Set(result.sourceFiles.map(normalizeComparablePath));
        this.updateReferenceWatchers(result);
        this.setStatus('applied', `Applied ${result.projectPath ?? projectPath} to the analysis engine.`, result);
        this.output.info(this.formatStatus());
        for (const warning of result.warnings) {
          this.output.warn(warning);
        }
      }
    } catch (error: unknown) {
      if (this.disposed || currentGeneration !== this.generation) {
        return;
      }
      const message = error instanceof Error ? error.message : String(error);
      this.setStatus('error', `Project request failed: ${message}`);
      this.output.error(`Project request failed without stopping the language server: ${message}`);
    }
  }

  private scheduleDiscovery(uri: vscode.Uri): void {
    if (isExcludedProjectPath(uri.fsPath)) {
      return;
    }
    this.schedule(() => this.initialize());
  }

  private scheduleProjectFileReload(uri: vscode.Uri): void {
    if (isExcludedProjectPath(uri.fsPath) || this.selectedProject === undefined) {
      return;
    }
    if (normalizeComparablePath(uri.fsPath) === normalizeComparablePath(this.selectedProject)) {
      this.scheduleSelectedReload(true);
    }
  }

  private scheduleSourceRefresh(uri: vscode.Uri): void {
    if (this.selectedProject === undefined ||
        !this.watchedSourceFiles.has(normalizeComparablePath(uri.fsPath))) {
      return;
    }
    this.scheduleSelectedReload(false);
  }

  private scheduleSelectedReload(forceReload: boolean): void {
    if (this.selectedProject === undefined || !vscode.workspace.isTrusted) {
      return;
    }
    this.pendingForceReload = this.pendingForceReload || forceReload;
    this.schedule(() => {
      const force = this.pendingForceReload;
      this.pendingForceReload = false;
      return this.loadSelected(force);
    });
  }

  private schedule(action: () => Promise<void>): void {
    if (this.debounceTimer !== undefined) {
      clearTimeout(this.debounceTimer);
    }
    this.debounceTimer = setTimeout(() => {
      this.debounceTimer = undefined;
      void action();
    }, 350);
  }

  private clearSchedule(): void {
    if (this.debounceTimer !== undefined) {
      clearTimeout(this.debounceTimer);
      this.debounceTimer = undefined;
    }
    this.pendingForceReload = false;
  }

  private updateReferenceWatchers(result: ProjectInfoLoadResult): void {
    this.disposeReferenceWatchers();
    const references = [...result.assemblyReferences, ...result.macroReferences];
    if (references.length > maxReferenceWatchers) {
      this.output.warn(
        `Watching only the first ${maxReferenceWatchers} of ${references.length} resolved references for changes.`);
    }
    for (const reference of references.slice(0, maxReferenceWatchers)) {
      const pattern = new vscode.RelativePattern(
        vscode.Uri.file(path.dirname(reference)),
        path.basename(reference),
      );
      const watcher = vscode.workspace.createFileSystemWatcher(pattern);
      // A rebuilt dependency keeps its resolved path, so the cached snapshot
      // stays valid; re-applying it makes the engine reload the assembly
      // (byte-level load keyed by file write time).
      watcher.onDidCreate(() => this.scheduleSelectedReload(false));
      watcher.onDidChange(() => this.scheduleSelectedReload(false));
      this.referenceWatchers.push(watcher);
    }
  }

  private disposeReferenceWatchers(): void {
    for (const watcher of this.referenceWatchers) {
      watcher.dispose();
    }
    this.referenceWatchers = [];
  }

  private ensureTrusted(action: string): boolean {
    if (vscode.workspace.isTrusted) {
      return true;
    }
    this.setStatus('restricted', 'Workspace is not trusted; server and MSBuild project queries are disabled.');
    void vscode.window.showWarningMessage(`Trust this workspace before Nemerle can ${action}.`);
    return false;
  }

  private setStatus(state: string, message: string, result?: ProjectInfoLoadResult): void {
    this.status = { state, selectedProject: this.selectedProject, result, message };
    this.renderStatus();
  }

  private renderStatus(): void {
    const labels: Record<string, string> = {
      restricted: '$(shield) Nemerle: Restricted',
      unsupported: '$(warning) Nemerle: One Folder Only',
      empty: '$(circle-slash) Nemerle: No Project',
      unselected: '$(question) Nemerle: Select Project',
      loading: '$(sync~spin) Nemerle: Loading',
      applied: '$(project) Nemerle: Project Applied',
      notApplied: '$(warning) Nemerle: Not Applied',
      error: '$(error) Nemerle: Project Error',
    };
    this.statusItem.text = labels[this.status.state] ?? '$(project) Nemerle';
    this.statusItem.tooltip = this.formatStatus();
  }

  private formatStatus(): string {
    const result = this.status.result;
    const lines = [this.status.message];
    if (this.selectedProject !== undefined) {
      lines.push(`Project: ${this.selectedProject}`);
    }
    if (result?.state === 'loaded') {
      lines.push(`Configuration: ${result.configuration ?? ''} | Platform: ${result.platform ?? ''} | Target framework: ${result.targetFramework ?? ''}`);
      lines.push(`Sources: ${result.sourceCount} | Assembly references: ${result.assemblyReferenceCount} | Macro references: ${result.macroReferenceCount}`);
      if (result.warnings.length > 0) {
        lines.push(...result.warnings.map((warning) => `Warning: ${warning}`));
      }
      if (result.appliedToEngine) {
        lines.push('The snapshot is applied to the analysis engine; diagnostics are project-aware.');
      } else {
        lines.push(`The snapshot is NOT applied to the analysis engine${result.applyError !== undefined ? `: ${result.applyError}` : '.'}`);
      }
    } else {
      lines.push('No project snapshot is applied; the language server analyzes open files only.');
    }
    return lines.join('\n');
  }
}
