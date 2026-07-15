using Nemerle.ProjectInfo;

namespace Nemerle.LanguageServer.Engine;

public sealed record WorkspaceApplyResult(bool Applied, IReadOnlyList<string> Warnings, string? Error);

/// <summary>
/// Coordinates the WP-L2 project snapshot and the engine workspace: converts
/// snapshots into engine inputs, reads closed project sources from disk, and
/// routes LSP document lifecycle events into the single
/// <see cref="NemerleProject"/>.  All engine mutations remain serialized by
/// NemerleProject itself; this class only prepares inputs outside its locks.
/// </summary>
internal sealed class WorkspaceManager
{
    private readonly NemerleProject _project;
    private readonly ServerLog _log;

    /// <summary>Provenance of the Nemerle assemblies this server process is running on, read
    /// once from its own directory (WP-M6, §6.8).</summary>
    private readonly NemerleProvenance _serverProvenance;

    /// <summary>Layout whose mismatch has already been shown, so a reload of the same project
    /// does not re-interrupt the user. Reset implicitly by pointing at a different toolchain.</summary>
    private string _warnedToolchainDir = "";

    public WorkspaceManager(NemerleProject project, ServerLog log)
    {
        _project = project;
        _log = log;
        _serverProvenance = ToolchainProvenance.Read(
            AppContext.BaseDirectory, "bundle-info.json", "language server");
        _log.Info(_serverProvenance.IsKnown
            ? $"nemerle language server toolchain: {_serverProvenance.Describe_Short()}"
            : "nemerle language server toolchain: provenance unknown (no bundle-info.json beside the server, and Nemerle.dll's version could not be read)");
    }

    /// <summary>
    /// Compares the toolchain a project builds with against this server's own bits and, on a
    /// positive mismatch, puts it in front of the user. Nemerle assembly versions track the
    /// source generation, so a mixed pair fails at load time with a FileLoadException that names
    /// neither generation - see 29-devenv2-plan.md §6.8 and 28-vscode-packaging-log.md.
    /// </summary>
    private void CheckToolchainProvenance(NemerleProjectSnapshot snapshot)
    {
        if (snapshot.NccLayoutDir.Length == 0)
            return;

        var toolchain = ToolchainProvenance.Read(snapshot.NccLayoutDir, "ncc-info.json", snapshot.NccLayoutDir);
        if (!toolchain.IsKnown)
            return;

        var mismatch = ToolchainProvenance.DescribeMismatch(_serverProvenance, toolchain);
        if (mismatch is null)
        {
            _log.Info($"nemerle project toolchain: {toolchain.Describe_Short()} (matches the language server)");
            _warnedToolchainDir = "";
            return;
        }

        _log.Warning(mismatch);
        if (!string.Equals(_warnedToolchainDir, snapshot.NccLayoutDir, StringComparison.Ordinal))
        {
            _warnedToolchainDir = snapshot.NccLayoutDir;
            _log.ShowWarning(mismatch);
        }
    }

    public event Action<IReadOnlyList<DocumentDiagnostics>> DiagnosticsChanged
    {
        add => _project.DiagnosticsChanged += value;
        remove => _project.DiagnosticsChanged -= value;
    }

    public void OpenDocument(string uri, string fileSystemPath, string text, int version) =>
        _project.Open(uri, fileSystemPath, text, version);

    public void ChangeDocument(string uri, IReadOnlyList<NemerleContentChange> changes, int version) =>
        _project.Change(uri, changes, version);

    public void CloseDocument(string uri) =>
        _project.Close(uri);

    public async Task<WorkspaceApplyResult> ApplySnapshotAsync(
        NemerleProjectSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        CheckToolchainProvenance(snapshot);

        var inputs = EngineWorkspaceInputs.FromSnapshot(snapshot);
        var warnings = new List<string>(inputs.Warnings);
        var texts = new Dictionary<string, string>(ProjectPathNormalizer.Comparer);
        foreach (var sourceFile in inputs.SourceFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = ProjectPathNormalizer.NormalizeFile(sourceFile);
            try
            {
                texts[path] = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                warnings.Add($"Project source could not be read and was skipped: {path} ({ex.Message})");
            }
        }

        try
        {
            _project.ApplyProject(inputs, texts);
        }
        catch (Exception ex)
        {
            _log.Error($"nemerle workspace apply failed: {ex}");
            return new WorkspaceApplyResult(false, warnings, ex.Message);
        }

        return new WorkspaceApplyResult(true, warnings, null);
    }
}
