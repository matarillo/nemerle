using System.Diagnostics;
using Nemerle.Compiler;
using Nemerle.Compiler.Parsetree;
using Nemerle.Compiler.Utils.Async;
using Nemerle.Completion2;
using Nemerle.Completion2.Factories;
using Nemerle.ProjectInfo;

namespace Nemerle.LanguageServer.Engine;

internal sealed record EngineDiagnostic(
    int Line,
    int Column,
    int EndLine,
    int EndColumn,
    MessageKind Kind,
    string Message,
    string? Code);

/// <summary>
/// One document's diagnostics inside a workspace-wide notification.
/// <see cref="Diagnostics"/> is null when the engine has not (re)analyzed the
/// document's current version yet: the publisher must keep its previous state
/// instead of clearing it, which is how stale results from an in-flight build
/// are suppressed.  Documents missing from a notification left the workspace
/// and must be cleared.
/// </summary>
internal sealed record DocumentDiagnostics(
    string Uri,
    int? Version,
    IReadOnlyList<EngineDiagnostic>? Diagnostics);

/// <summary>
/// IIdeProject adapter for the single engine workspace: all sources of the
/// applied WP-L2 project snapshot (open LSP buffers override disk-backed text)
/// plus any open loose files.  Without an applied snapshot it degrades to the
/// WP-K/WP-L1 loose-file behavior.  Engine mutations are serialized by
/// <c>_engineOperations</c>; document/message state by <c>_gate</c>.
/// </summary>
internal sealed class NemerleProject : IIdeProject, IAsyncDisposable
{
    private static readonly TimeSpan ChangeReloadDebounce = TimeSpan.FromMilliseconds(300);

    private sealed class DocumentState
    {
        public required InMemoryNemerleSource Source { get; init; }
        public required string PublishUri { get; set; }
        public bool IsOpen { get; set; }
        public bool IsProjectSource { get; set; }
    }

    private readonly object _gate = new();
    private readonly object _engineOperations = new();
    private readonly object _publishOrdering = new();
    private readonly Dictionary<string, DocumentState> _documentsByPath =
        new(ProjectPathNormalizer.Comparer);
    private readonly Dictionary<int, DocumentState> _documentsByFileIndex = [];
    private readonly Dictionary<string, string> _openUriToPath =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<int, (int Version, CompilerMessage[] Messages)> _parseMessages = [];
    private readonly Dictionary<MemberBuilder, CompilerMessage[]> _methodMessages =
        new(ReferenceEqualityComparer.Instance);
    private CompilerMessage[] _topLevelMessages = [];
    private EngineWorkspaceInputs? _appliedInputs;
    private readonly Timer _reloadTimer;
    private long _reloadStartedTimestamp;
    private bool _disposed;
    private readonly CancellationTokenSource _pumpCancellation = new();
    private readonly Task _responsePump;
    private readonly IIdeEngine _engine;
    private readonly ServerLog _log;

    public NemerleProject(ServerLog log)
    {
        _log = log;
        _reloadTimer = new Timer(OnReloadTimer);
        _engine = EngineFactory.Create(this, log.AsTextWriter(), false);
        _responsePump = Task.Run(PumpResponsesAsync);
    }

    public event Action<IReadOnlyList<DocumentDiagnostics>>? DiagnosticsChanged;

    public void Open(string uri, string fileSystemPath, string text, int version)
    {
        lock (_engineOperations)
        {
            lock (_gate)
            {
                var path = ProjectPathNormalizer.NormalizeFile(fileSystemPath);
                if (_documentsByPath.TryGetValue(path, out var state))
                {
                    // Opening takes ownership from the disk-backed state, so
                    // results computed for it no longer apply; version numbers
                    // switch to the client's sequence with this open.
                    if (!state.IsOpen)
                        DropMessagesForFile(state.Source.FileIndex);
                    state.Source.Update(text, version);
                    state.PublishUri = uri;
                    state.IsOpen = true;
                }
                else
                {
                    var source = new InMemoryNemerleSource(path, text, version);
                    state = new DocumentState { Source = source, PublishUri = uri, IsOpen = true };
                    _documentsByPath.Add(path, state);
                    _documentsByFileIndex[source.FileIndex] = state;
                }

                _openUriToPath[uri] = path;
            }

            RequestEngineReload(immediate: true);
        }
    }

    public void Change(string uri, string text, int version)
    {
        lock (_engineOperations)
        {
            lock (_gate)
            {
                if (!_openUriToPath.TryGetValue(uri, out var path) ||
                    !_documentsByPath.TryGetValue(path, out var state))
                    throw new InvalidOperationException($"didChange received for unopened document: {uri}");
                state.Source.Update(text, version);
            }

            RequestEngineReload(immediate: false);
        }
    }

    public void Close(string uri)
    {
        lock (_engineOperations)
        {
            int? deletedFileIndex = null;
            lock (_gate)
            {
                if (!_openUriToPath.Remove(uri, out var path) ||
                    !_documentsByPath.TryGetValue(path, out var state))
                    return;

                state.IsOpen = false;
                var fileIndex = state.Source.FileIndex;
                DropMessagesForFile(fileIndex);

                if (state.IsProjectSource)
                {
                    // Revert to disk-backed content; the source stays in the
                    // project.  Bumping the version invalidates buffer-based
                    // engine results, and the synthetic empty parse entry
                    // clears stale buffer diagnostics until the next build
                    // republishes disk-based ones.
                    var text = TryReadDiskText(path) ?? string.Empty;
                    var version = state.Source.CurrentVersion + 1;
                    state.Source.Update(text, version);
                    _parseMessages[fileIndex] = (version, []);
                }
                else
                {
                    _documentsByPath.Remove(path);
                    _documentsByFileIndex.Remove(fileIndex);
                    deletedFileIndex = fileIndex;
                }
            }

            if (deletedFileIndex is { } index)
                _engine.NotifySourceDeleted(index);
            RaiseDiagnosticsChanged();
            RequestEngineReload(immediate: true);
        }
    }

    /// <summary>
    /// Replaces the engine workspace with the given project inputs.  Closed
    /// project sources use the pre-read disk texts; open documents keep their
    /// LSP buffer.  Sources missing from <paramref name="texts"/> (unreadable
    /// files) are skipped; the caller reports them as warnings.
    /// </summary>
    public void ApplyProject(EngineWorkspaceInputs inputs, IReadOnlyDictionary<string, string> texts)
    {
        lock (_engineOperations)
        {
            var deletedFileIndexes = new List<int>();
            lock (_gate)
            {
                var newSources = new HashSet<string>(ProjectPathNormalizer.Comparer);
                foreach (var sourceFile in inputs.SourceFiles)
                    newSources.Add(ProjectPathNormalizer.NormalizeFile(sourceFile));

                foreach (var (path, state) in _documentsByPath.ToArray())
                {
                    if (!state.IsProjectSource || newSources.Contains(path))
                        continue;

                    state.IsProjectSource = false;
                    if (!state.IsOpen)
                    {
                        var fileIndex = state.Source.FileIndex;
                        _documentsByPath.Remove(path);
                        _documentsByFileIndex.Remove(fileIndex);
                        DropMessagesForFile(fileIndex);
                        deletedFileIndexes.Add(fileIndex);
                    }
                }

                foreach (var path in newSources)
                {
                    if (_documentsByPath.TryGetValue(path, out var state))
                    {
                        state.IsProjectSource = true;
                        if (!state.IsOpen && texts.TryGetValue(path, out var text))
                            state.Source.Update(text, state.Source.CurrentVersion + 1);
                    }
                    else if (texts.TryGetValue(path, out var text))
                    {
                        var source = new InMemoryNemerleSource(path, text, 0);
                        state = new DocumentState
                        {
                            Source = source,
                            PublishUri = new Uri(path).AbsoluteUri,
                            IsProjectSource = true,
                        };
                        _documentsByPath.Add(path, state);
                        _documentsByFileIndex[source.FileIndex] = state;
                    }
                }

                _appliedInputs = inputs;
            }

            foreach (var fileIndex in deletedFileIndexes)
                _engine.NotifySourceDeleted(fileIndex);
            RaiseDiagnosticsChanged();
            RequestEngineReload(immediate: true);
        }
    }

    public IEnumerable<string> GetAssemblyReferences()
    {
        lock (_gate) return _appliedInputs?.AssemblyReferences.ToArray() ?? [];
    }

    public IEnumerable<string> GetMacroAssemblyReferences()
    {
        lock (_gate) return _appliedInputs?.MacroReferences.ToArray() ?? [];
    }

    public CompilationOptions GetOptions()
    {
        var options = new CompilationOptions
        {
            GreedyReferences = false,
            ColorMessages = false,
            IgnoreConfusion = true,
        };

        EngineWorkspaceInputs? inputs;
        lock (_gate) inputs = _appliedInputs;

        if (inputs is null)
        {
            // Loose-file mode: keep the WP-K defaults.
            options.DefineConstant("DEBUG");
            options.DefineConstant("TRACE");
        }
        else
        {
            options.ProjectPath = inputs.ProjectPath;
            foreach (var define in inputs.Defines)
                options.DefineConstant(define);
            if (inputs.CheckIntegerOverflow is { } checkedOverflow)
                options.CheckIntegerOverflow = checkedOverflow;
            options.IndentationSyntax = inputs.IndentationSyntax;
        }

        return options;
    }

    public IIdeSource? GetSource(int fileIndex)
    {
        lock (_gate) return _documentsByFileIndex.GetValueOrDefault(fileIndex)?.Source;
    }

    public IEnumerable<IIdeSource> GetSources()
    {
        lock (_gate) return _documentsByPath.Values.Select(static state => (IIdeSource)state.Source).ToArray();
    }

    public void ClearAllCompilerMessages()
    {
        lock (_gate)
        {
            _topLevelMessages = [];
            _parseMessages.Clear();
            _methodMessages.Clear();
        }
    }

    public void SetCompilerMessageForCompileUnit(CompileUnit compileUnit)
    {
        lock (_gate)
        {
            if (_documentsByFileIndex.TryGetValue(compileUnit.FileIndex, out var state) &&
                compileUnit.SourceVersion == state.Source.CurrentVersion)
            {
                _parseMessages[compileUnit.FileIndex] =
                    (compileUnit.SourceVersion, compileUnit.ParseCompilerMessages.ToArray());
            }
        }

        RaiseDiagnosticsChanged();
    }

    public void SetMethodCompilerMessages(MemberBuilder member, IEnumerable<CompilerMessage> messages)
    {
        if (member is IntelliSenseModeMethodBuilder method &&
            method.TypesTreeVersion != _engine.TypesTreeVersion)
            return;

        lock (_gate) _methodMessages[member] = messages.ToArray();
        RaiseDiagnosticsChanged();
    }

    public void ClearMethodCompilerMessages(MemberBuilder member)
    {
        lock (_gate) _methodMessages.Remove(member);
        RaiseDiagnosticsChanged();
    }

    public void SetTopLevelCompilerMessages(IEnumerable<CompilerMessage> messages)
    {
        lock (_gate) _topLevelMessages = messages.ToArray();
        RaiseDiagnosticsChanged();
    }

    public void SetStatusText(string text) => Trace.WriteLine(text);
    public void ShowMessage(string message, MessageType messageType)
    {
        switch (messageType)
        {
            case MessageType.Error: _log.Error($"nemerle engine: {message}"); break;
            case MessageType.Warning: _log.Warning($"nemerle engine: {message}"); break;
            case MessageType.Hint: _log.Log($"nemerle engine: {message}"); break;
            default: _log.Info($"nemerle engine: {message}"); break;
        }
    }
    public GotoInfo[] LookupLocationsFromDebugInformation(GotoInfo info) => [];
    public void SetHighlights(IIdeSource source, IEnumerable<GotoInfo> highlights) { }
    public void AddUnimplementedMembers(
        IIdeSource source,
        TypeBuilder type,
        IEnumerable<IGrouping<FixedType.Class, IMember>> unimplementedMembers) { }
    public void AddOverrideMembers(IIdeSource source, TypeBuilder type, IEnumerable<IMember> notOverridden) { }

    public void TypesTreeCreated()
    {
        var startedAt = Interlocked.Read(ref _reloadStartedTimestamp);
        if (startedAt != 0)
            _log.Log(
                $"nemerle engine rebuild finished after {Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds:F0} ms");
        RaiseDiagnosticsChanged();
    }

    public async ValueTask DisposeAsync()
    {
        lock (_engineOperations)
        {
            _disposed = true;
            _reloadTimer.Dispose();
        }

        _engine.Close();
        _pumpCancellation.Cancel();
        try { await _responsePump.ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        _pumpCancellation.Dispose();
    }

    /// <summary>Caller must hold <c>_engineOperations</c>.</summary>
    private void RequestEngineReload(bool immediate)
    {
        if (_disposed)
            return;

        if (immediate)
        {
            _reloadTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            BeginEngineReload();
        }
        else
        {
            _reloadTimer.Change(ChangeReloadDebounce, Timeout.InfiniteTimeSpan);
        }
    }

    private void OnReloadTimer(object? state)
    {
        lock (_engineOperations)
        {
            if (_disposed)
                return;
            BeginEngineReload();
        }
    }

    /// <summary>Caller must hold <c>_engineOperations</c>.</summary>
    private void BeginEngineReload()
    {
        Interlocked.Exchange(ref _reloadStartedTimestamp, Stopwatch.GetTimestamp());
        _ = _engine.BeginReloadProject();
    }

    private void DropMessagesForFile(int fileIndex)
    {
        _parseMessages.Remove(fileIndex);
        foreach (var member in _methodMessages.Keys
                     .Where(m => m.Location.FileIndex == fileIndex)
                     .ToArray())
            _methodMessages.Remove(member);
    }

    private string? TryReadDiskText(string path)
    {
        try
        {
            return File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            _log.Warning($"nemerle workspace could not reread project source '{path}': {ex.Message}");
            return null;
        }
    }

    private async Task PumpResponsesAsync()
    {
        while (!_pumpCancellation.IsCancellationRequested)
        {
            try
            {
                AsyncWorker.DispatchResponses();
            }
            catch (Exception ex)
            {
                _log.Error($"nemerle response callback failed: {ex}");
            }

            await Task.Delay(10, _pumpCancellation.Token).ConfigureAwait(false);
        }
    }

    private void RaiseDiagnosticsChanged()
    {
        // Serializes payload construction and delivery: the response pump and
        // the document/apply threads both raise this event, and a payload
        // built from older state must never be delivered after a newer one.
        lock (_publishOrdering)
        {
            DiagnosticsChanged?.Invoke(BuildDiagnosticsPayload());
        }
    }

    private IReadOnlyList<DocumentDiagnostics> BuildDiagnosticsPayload()
    {
        List<DocumentDiagnostics> result;
        lock (_gate)
        {
            var perFile = new Dictionary<int, List<EngineDiagnostic>>();
            var messages = _topLevelMessages
                .Concat(_parseMessages.Values.SelectMany(static entry => entry.Messages))
                .Concat(_methodMessages.Values.SelectMany(static m => m))
                .Distinct(CompilerMessageComparer.Instance);

            foreach (var message in messages)
            {
                var location = message.Location;
                if (!_documentsByFileIndex.ContainsKey(location.FileIndex))
                    continue;

                if (!perFile.TryGetValue(location.FileIndex, out var list))
                    perFile[location.FileIndex] = list = [];
                // Coded compiler messages arrive with an "N####: " prefix (via
                // ncc's report/MessageOccured path, which ProcessTopLevelCompilerMessage
                // feeds into the engine's CompilerMessage.Msg); surface the code in
                // Diagnostic.code and keep the prefix out of the human-readable text.
                var (code, text) = NemerleWarningCode.Extract(message.Msg);
                list.Add(new EngineDiagnostic(
                    location.Line,
                    location.Column,
                    location.EndLine,
                    location.EndColumn,
                    message.Kind,
                    text,
                    code));
            }

            result = new List<DocumentDiagnostics>(_documentsByPath.Count);
            foreach (var state in _documentsByPath.Values)
            {
                var fileIndex = state.Source.FileIndex;
                var currentVersion = state.Source.CurrentVersion;

                // A document counts as analyzed only when the engine parsed
                // exactly its current text version; otherwise the previous
                // published state is kept (Diagnostics = null) until the
                // pending rebuild reports fresh results.
                IReadOnlyList<EngineDiagnostic>? diagnostics = null;
                if (_parseMessages.TryGetValue(fileIndex, out var parsed) &&
                    parsed.Version == currentVersion)
                {
                    diagnostics = perFile.TryGetValue(fileIndex, out var list)
                        ? list
                        : [];
                }

                result.Add(new DocumentDiagnostics(
                    state.PublishUri,
                    state.IsOpen ? currentVersion : null,
                    diagnostics));
            }
        }

        return result;
    }

    private sealed class CompilerMessageComparer : IEqualityComparer<CompilerMessage>
    {
        public static readonly CompilerMessageComparer Instance = new();

        public bool Equals(CompilerMessage? x, CompilerMessage? y) =>
            ReferenceEquals(x, y) ||
            x is not null && y is not null && x.Location.Equals(y.Location) &&
            x.Kind == y.Kind && string.Equals(x.Msg, y.Msg, StringComparison.Ordinal);

        public int GetHashCode(CompilerMessage obj) =>
            HashCode.Combine(obj.Location, obj.Kind, obj.Msg);
    }
}
