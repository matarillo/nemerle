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

    public WorkspaceManager(NemerleProject project, ServerLog log)
    {
        _project = project;
        _log = log;
    }

    public event Action<IReadOnlyList<DocumentDiagnostics>> DiagnosticsChanged
    {
        add => _project.DiagnosticsChanged += value;
        remove => _project.DiagnosticsChanged -= value;
    }

    public void OpenDocument(string uri, string fileSystemPath, string text, int version) =>
        _project.Open(uri, fileSystemPath, text, version);

    public void ChangeDocument(string uri, string text, int version) =>
        _project.Change(uri, text, version);

    public void CloseDocument(string uri) =>
        _project.Close(uri);

    public async Task<WorkspaceApplyResult> ApplySnapshotAsync(
        NemerleProjectSnapshot snapshot,
        CancellationToken cancellationToken)
    {
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
