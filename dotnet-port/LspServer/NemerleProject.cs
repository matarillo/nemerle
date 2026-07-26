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

/// <summary>Engine location of a hover target, in the engine's 1-based line/column.</summary>
internal sealed record EngineHoverRange(int Line, int Column, int EndLine, int EndColumn);

/// <summary>
/// A hover result carrying the raw engine hint text (still pseudo-markup; the
/// handler converts it with <see cref="Nemerle.ProjectInfo.HoverMarkup"/>) and
/// the optional target range.
/// </summary>
internal sealed record EngineHover(string Text, EngineHoverRange? Range);

/// <summary>
/// One completion item.  <see cref="Detail"/> is the cheap inline hint;
/// the expensive documentation is computed lazily by
/// <see cref="NemerleProject.ResolveCompletionDescription"/> using
/// <see cref="Generation"/> + <see cref="Index"/>.
/// </summary>
internal sealed record EngineCompletionItem(
    NemerleCompletionKind Kind,
    string Label,
    string? Detail,
    long Generation,
    int Index);

/// <summary>
/// A completion result: the items and the generation they belong to.  The
/// generation lets <c>completionItem/resolve</c> confirm the cached element list
/// is still the one it is resolving against (WP-M3 §, "one-generation cache").
/// </summary>
internal sealed record EngineCompletionResult(long Generation, IReadOnlyList<EngineCompletionItem> Items);

/// <summary>
/// A definition/references result: the navigable source locations plus
/// <see cref="ExternalOnly"/>, which is true when the engine resolved the symbol
/// to at least one target but none had an in-workspace source location (a
/// metadata / external-assembly member).  The handler logs that case at Info
/// level and returns an empty result (WP-M4 acceptance 4).
/// </summary>
internal sealed record EngineGotoResult(IReadOnlyList<NemerleGotoLocation> Locations, bool ExternalOnly)
{
    public static readonly EngineGotoResult Empty = new([], false);
}

/// <summary>
/// A rename result (WP-P3): the workspace edit, or the reason the rename was
/// refused.  <see cref="Conflict"/> carries the detail of an edit that could not
/// be assembled (overlapping or contradictory edits);  <see cref="OldName"/> is
/// the symbol's current text, for the log line.
/// </summary>
internal sealed record EngineRenameResult(
    NemerleWorkspaceEdit? Edit,
    NemerleRenameRefusal Refusal,
    string? Conflict,
    string? OldName)
{
    public bool IsUsable => Edit is not null && Refusal == NemerleRenameRefusal.None;

    public static EngineRenameResult Refused(NemerleRenameRefusal refusal) =>
        new(null, refusal, null, null);
}

/// <summary>
/// One semantic token, already in LSP coordinates (0-based line and UTF-16
/// character, length in UTF-16 code units) and classified into the legend of
/// <see cref="SemanticTokenMapping"/>.  Tokens never span lines: the engine
/// colorizer is line-based, which is also what LSP requires.
/// </summary>
internal sealed record EngineSemanticToken(
    int Line,
    int Character,
    int Length,
    NemerleSemanticTokenType Type,
    NemerleSemanticTokenModifier Modifiers);

/// <summary>
/// A colorized document.  <see cref="FromTypesTree"/> is false when no line
/// resolved a <c>GlobalEnv</c> - the engine had not built (or had not yet built)
/// the types tree, so the colorizer fell back to the core environment: keywords,
/// strings, comments and numbers are right, but **macro-introduced keywords and
/// user types are not distinguished**.  The caller waits for a complete answer
/// rather than handing that to a client which caches it.
/// </summary>
internal sealed record EngineSemanticTokens(
    IReadOnlyList<EngineSemanticToken> Tokens,
    bool FromTypesTree);

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
    // Last completion result kept for completionItem/resolve.  Incremented on
    // every completion and on every engine reload, so a resolve whose generation
    // no longer matches is answered without (stale) documentation.  Guarded by
    // _gate.
    private long _completionGeneration;
    private CompletionElem[] _completionElems = [];
    private EngineWorkspaceInputs? _appliedInputs;
    private readonly Timer _reloadTimer;
    private long _reloadStartedTimestamp;
    // Coalesced reload state, guarded by _engineOperations.  A pending full
    // reload always wins over queued incremental updates (it re-analyzes every
    // source, so the individual edits it would relocate are already covered).
    private readonly HashSet<InMemoryNemerleSource> _pendingIncrementalSources = [];
    private bool _pendingFullReload;
    private bool _disposed;
    private readonly CancellationTokenSource _pumpCancellation = new();
    private readonly Task _responsePump;
    private readonly IIdeEngine _engine;
    private readonly ServerLog _log;
    private readonly bool _incrementalEnabled;
    private readonly EngineRequestBridge _bridge = new();

    // documentHighlight (WP-P2) comes back through a callback rather than on its
    // request, so exactly one may be in flight: the gate is what makes an
    // unkeyed IIdeProject.SetHighlights unambiguous.  See
    // GetDocumentHighlightsAsync / AwaitHighlightAsync.
    private readonly SemaphoreSlim _highlightGate = new(1, 1);
    // The mailbox of that single in-flight request, or null.  Written by the LSP
    // thread under _highlightGate, read by the AsyncWorker thread in SetHighlights.
    private volatile HighlightDelivery? _highlightDelivery;
    // The same ceiling EngineRequestBridge uses, so a wedged engine cannot hang
    // an LSP request.
    private static readonly TimeSpan HighlightTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan HighlightPollInterval = TimeSpan.FromMilliseconds(5);
    // How long "completed" is allowed to precede "delivered".  The two are
    // adjacent statements on the worker thread, so this only has to cover a
    // scheduling hiccup, not any real work.
    private static readonly TimeSpan HighlightSettle = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan HighlightSettlePollInterval = TimeSpan.FromMilliseconds(2);

    // The same single-in-flight arrangement for the member-suggestion callbacks
    // (WP-P5).  See GetCodeActionsAsync / AwaitMemberDeliveryAsync.
    private readonly SemaphoreSlim _memberSuggestionGate = new(1, 1);
    private volatile MemberSuggestionDelivery? _memberSuggestion;
    private static readonly TimeSpan MemberSuggestionTimeout = TimeSpan.FromSeconds(10);
    // Longer than the highlight settle on purpose: those callbacks are invoked by
    // the worker itself, these are queued to the response queue and dispatched by
    // this class's pump, which polls every 10 ms.
    private static readonly TimeSpan MemberSuggestionSettle = TimeSpan.FromMilliseconds(750);

    public NemerleProject(ServerLog log, ServerOptions options)
    {
        _log = log;
        _incrementalEnabled = options.IncrementalUpdate;
        _reloadTimer = new Timer(OnReloadTimer);
        _engine = EngineFactory.Create(this, log.AsTextWriter(), false);
        _responsePump = Task.Run(PumpResponsesAsync);
    }

    public event Action<IReadOnlyList<DocumentDiagnostics>>? DiagnosticsChanged;

    /// <summary>
    /// Raised after the engine (re)built the types tree.  Results that depend on
    /// the tree rather than on the buffer alone - semantic tokens, whose keyword
    /// set comes from each line's <c>GlobalEnv</c> - are only complete from this
    /// point on, and the client has to be told to ask again (WP-O5a).
    /// </summary>
    public event Action? TypesTreeRebuilt;

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

            RequestFullReload(immediate: true);
        }
    }

    public void Change(string uri, IReadOnlyList<NemerleContentChange> changes, int version)
    {
        lock (_engineOperations)
        {
            InMemoryNemerleSource source;
            bool useIncremental;
            lock (_gate)
            {
                if (!_openUriToPath.TryGetValue(uri, out var path) ||
                    !_documentsByPath.TryGetValue(path, out var state))
                    throw new InvalidOperationException($"didChange received for unopened document: {uri}");
                source = state.Source;

                // The relocation path applies only to a single ranged edit of a
                // project source with a loaded project: UpdateCompileUnit compares
                // the reparsed structure against the built types tree, and the
                // relocation-queue merge assumes consecutive per-change versions
                // (one didChange == one version), so a multi-change batch, a
                // whole-document replacement, a loose file, or a not-yet-loaded
                // project falls back to a full reload.
                useIncremental = _incrementalEnabled
                    && state.IsProjectSource
                    && _appliedInputs is not null
                    && changes.Count == 1
                    && changes[0].HasRange;

                if (useIncremental)
                {
                    var relocation = source.ApplyRangedChange(changes[0], version);
                    source.EnqueueRelocation(relocation, version);
                }
                else
                {
                    // Drop any queued relocations so a later incremental edit never
                    // merges its request across this rebuild boundary.
                    source.ClearRelocationRequests();
                    source.ApplyChanges(changes, version);
                }
            }

            if (useIncremental)
                RequestIncrementalUpdate(source);
            else
                RequestFullReload(immediate: false);
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
            RequestFullReload(immediate: true);
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
            RequestFullReload(immediate: true);
        }
    }

    /// <summary>
    /// Computes hover (QuickTip) information for an open document at an LSP
    /// position (0-based line/character, UTF-16 code units).  Returns null when
    /// the document is not open, the position is not over a symbol, or the
    /// request was cancelled/superseded/stale.  The request is enqueued under
    /// <c>_engineOperations</c> (serialized with document changes and reloads)
    /// but awaited off the lock via <see cref="EngineRequestBridge"/>, so a
    /// hover during a project reload resolves to null/result/cancel without
    /// deadlocking.
    /// </summary>
    public async Task<EngineHover?> GetHoverAsync(
        string uri,
        int lspLine,
        int lspCharacter,
        CancellationToken token)
    {
        Task<EngineRequestBridge.RequestResult<QuickTipInfo?>> pending;
        lock (_engineOperations)
        {
            InMemoryNemerleSource source;
            int expectedVersion;
            lock (_gate)
            {
                if (_disposed ||
                    !_openUriToPath.TryGetValue(uri, out var path) ||
                    !_documentsByPath.TryGetValue(path, out var state))
                    return null;
                source = state.Source;
                expectedVersion = source.CurrentVersion;
            }

            // Engine coordinates are 1-based; the LSP character is a UTF-16 code
            // unit offset, matching the source's .NET string indexing.
            var line = lspLine + 1;
            var column = lspCharacter + 1;
            pending = _bridge.RunAsync<QuickTipInfo?>(
                () => _engine.BeginGetQuickTipInfo(source, line, column),
                () => source.CurrentVersion,
                expectedVersion,
                static request => ((QuickTipInfoAsyncRequest)request).QuickTipInfo,
                token);
        }

        var result = await pending.ConfigureAwait(false);
        if (!result.IsUsable || result.Value is not { } tip || string.IsNullOrEmpty(tip.Text))
            return null;

        var location = tip.Location;
        var range = location.IsEmpty || string.IsNullOrEmpty(location.File)
            ? null
            : new EngineHoverRange(location.Line, location.Column, location.EndLine, location.EndColumn);
        return new EngineHover(tip.Text, range);
    }

    /// <summary>
    /// Computes completion items for an open document at an LSP position (0-based
    /// line/character, UTF-16 code units).  Returns null when the document is not
    /// open or the request was cancelled/superseded/stale.  Like hover, the
    /// request is enqueued under <c>_engineOperations</c> but awaited off the lock
    /// via <see cref="EngineRequestBridge"/> (reused from WP-M2); the completion
    /// request is forced out by an in-flight rebuild, which the bridge reports as
    /// a cancellation rather than a fabricated empty list.  The returned items
    /// carry only a cheap detail string; each element's expensive documentation
    /// is deferred to <see cref="ResolveCompletionDescription"/>.
    /// </summary>
    public async Task<EngineCompletionResult?> GetCompletionAsync(
        string uri,
        int lspLine,
        int lspCharacter,
        CancellationToken token)
    {
        Task<EngineRequestBridge.RequestResult<CompletionElem[]?>> pending;
        lock (_engineOperations)
        {
            InMemoryNemerleSource source;
            int expectedVersion;
            lock (_gate)
            {
                if (_disposed ||
                    !_openUriToPath.TryGetValue(uri, out var path) ||
                    !_documentsByPath.TryGetValue(path, out var state))
                    return null;
                source = state.Source;
                expectedVersion = source.CurrentVersion;
            }

            var line = lspLine + 1;
            var column = lspCharacter + 1;
            pending = _bridge.RunAsync<CompletionElem[]?>(
                () =>
                {
                    // Replicates Engine.BeginCompletion without waiting: the engine
                    // exposes only the synchronous Completion() in IIdeEngine, but
                    // CompletionAsyncRequest is public and AsyncWorker.AddWork lets
                    // the bridge enqueue it and poll for completion off the lock
                    // (no engine-source change; keeps the shared engine untouched).
                    var request = new CompletionAsyncRequest(_engine, source, line, column);
                    AsyncWorker.AddWork(request);
                    return request;
                },
                () => source.CurrentVersion,
                expectedVersion,
                static request => ((CompletionAsyncRequest)request).CompletionElems,
                token);
        }

        var result = await pending.ConfigureAwait(false);
        if (!result.IsUsable || result.Value is not { } elems)
            return null;

        lock (_gate)
        {
            var generation = ++_completionGeneration;
            _completionElems = elems;

            var items = new List<EngineCompletionItem>(elems.Length);
            for (var index = 0; index < elems.Length; index++)
            {
                var elem = elems[index];
                if (string.IsNullOrEmpty(elem.DisplayName))
                    continue;

                // Detail is the cheap inline hint (the engine's Info string, e.g.
                // "keyword"); documentation is deferred to resolve.  The same
                // pseudo-markup stripper as hover keeps raw tags out.
                var detail = HoverMarkup.ToPlainText(elem.Info);
                items.Add(new EngineCompletionItem(
                    CompletionMapping.GlyphToKind(elem.GlyphType),
                    elem.DisplayName,
                    string.IsNullOrEmpty(detail) ? null : detail,
                    generation,
                    index));
            }

            return new EngineCompletionResult(generation, items);
        }
    }

    /// <summary>
    /// Computes the deferred documentation for a completion item (its overload
    /// list / XmlDoc summary via <c>CompletionElem.Description</c>).  Returns null
    /// when the generation no longer matches the cached element list (a newer
    /// completion or an engine reload happened), so resolve never renders
    /// documentation for a superseded generation.
    /// </summary>
    public string? ResolveCompletionDescription(long generation, int index)
    {
        CompletionElem elem;
        lock (_gate)
        {
            if (generation != _completionGeneration ||
                index < 0 || index >= _completionElems.Length)
                return null;
            elem = _completionElems[index];
        }

        // Description reads cached member metadata / XmlDoc off the worker thread;
        // it reads immutable snapshots (a concurrent rebuild only orphans them),
        // and the generation guard drops results whose snapshot was replaced.
        var description = HoverMarkup.ToPlainText(elem.Description);
        return string.IsNullOrEmpty(description) ? null : description;
    }

    /// <summary>
    /// Computes definition locations for an open document at an LSP position.
    /// Unlike hover/completion, <c>GetGotoInfo</c> is a synchronous engine API, so
    /// it runs directly under <c>_engineOperations</c> (serialized with document
    /// changes and reloads, §6.2 "the synchronous API is serialized under the
    /// lock") rather than through the async bridge.
    /// </summary>
    public EngineGotoResult GetDefinition(string uri, int lspLine, int lspCharacter) =>
        GetGoto(uri, lspLine, lspCharacter, GotoKind.Definition, includeDeclaration: true);

    /// <summary>
    /// Computes reference locations for an open document at an LSP position.
    /// <paramref name="includeDeclaration"/> maps the LSP
    /// <c>references</c> <c>context.includeDeclaration</c>: when false the
    /// declaration entries are dropped from the usages.
    /// </summary>
    public EngineGotoResult GetReferences(string uri, int lspLine, int lspCharacter, bool includeDeclaration) =>
        GetGoto(uri, lspLine, lspCharacter, GotoKind.Usages, includeDeclaration);

    private EngineGotoResult GetGoto(
        string uri,
        int lspLine,
        int lspCharacter,
        GotoKind kind,
        bool includeDeclaration)
    {
        lock (_engineOperations)
        {
            InMemoryNemerleSource source;
            lock (_gate)
            {
                if (_disposed ||
                    !_openUriToPath.TryGetValue(uri, out var path) ||
                    !_documentsByPath.TryGetValue(path, out var state))
                    return EngineGotoResult.Empty;
                source = state.Source;
            }

            // Engine coordinates are 1-based; the LSP character is a UTF-16 code
            // unit offset, matching the source's .NET string indexing.
            var line = lspLine + 1;
            var column = lspCharacter + 1;
            var infos = _engine.GetGotoInfo(source, line, column, kind);
            if (infos is null || infos.Length == 0)
                return EngineGotoResult.Empty;

            var targets = FlattenGotoInfos(infos);
            var locations = GotoMapping.ToLocations(targets, includeDeclaration);
            // The engine returned targets but none were navigable source
            // locations: the symbol resolved to a metadata / external member.
            var externalOnly = locations.Count == 0;
            return new EngineGotoResult(locations, externalOnly);
        }
    }

    /// <summary>
    /// Computes <c>textDocument/documentHighlight</c> for an open document: every
    /// occurrence of the symbol under the caret <em>inside that document</em>,
    /// classified read/write.  Returns an empty list when the document is not
    /// open, the caret is not on a symbol, or the request was
    /// cancelled/superseded/stale.
    ///
    /// <para><b>Route: the engine's own highlighter</b> (WP-P2;
    /// <c>56-wp-p-plan.md</c> §4 named <c>GetGotoInfo(..., Usages)</c> as its
    /// first candidate).  That candidate is not available: <c>IIdeEngine</c>
    /// (<c>Nemerle.Completion2/Engine/IEngine.n</c>) publishes only the
    /// <em>synchronous</em> <c>GetGotoInfo</c>; the worker-resident
    /// <c>BeginGetGotoInfo</c> lives on the <c>internal</c> <c>Engine</c> class.
    /// And the synchronous form cannot be called from a worker work item, because
    /// it is itself <c>BeginGetGotoInfo</c> + <c>AsyncWaitHandle.WaitOne()</c> -
    /// the worker would be waiting on itself.  Publishing it would be a shared
    /// source change (<c>56-wp-p-plan.md</c> §3-3).  <c>BeginHighlightUsages</c>
    /// is on the interface, already runs on the worker, and is already
    /// file-filtered, so it is the only route that honors the thread contract
    /// without touching the engine.</para>
    ///
    /// <para><b>What that costs, and how it is paid.</b>  The result comes back
    /// through the <c>IIdeProject.SetHighlights</c> callback, which the engine
    /// invokes <em>after</em> <c>request.MarkAsCompleted()</c>
    /// (<c>Engine-HighlightUsages.n</c>), so completion is not delivery, and the
    /// callback carries only the <c>IIdeSource</c> - no key to tell two answers
    /// apart.  Correlation is therefore structural rather than by key: this method
    /// holds <c>_highlightGate</c> for the whole round trip, so exactly one
    /// highlight request is ever in flight, and it does not release the gate until
    /// that request has delivered or is known dead (see
    /// <see cref="AwaitHighlightAsync"/>).  A cancelled request is drained, not
    /// abandoned, precisely so its late callback cannot be handed to the next
    /// caret position.</para>
    ///
    /// <para><b>Agreement with references/rename.</b>  Both routes end in the same
    /// <c>Project.FindUsages</c>; the highlighter passes
    /// <c>onlyThisFile = true</c>, which narrows only the <em>textual candidate
    /// pre-scan</em> to this file and then post-filters by <c>FileIndex</c>
    /// exactly as <c>GotoMapping.ToDocumentHighlights</c> would.  The integration
    /// suite pins the two against each other (highlights == references restricted
    /// to this document) rather than assuming it.</para>
    ///
    /// <para>The flattening runs on the worker for free here: <c>SetHighlights</c>
    /// is called <em>by</em> the worker, so <c>GotoInfo.FilePath</c> - which reads
    /// <c>Location</c>'s process-wide, unsynchronized file-name list - is read on
    /// the only thread allowed to touch engine state.</para>
    /// </summary>
    public async Task<IReadOnlyList<NemerleDocumentHighlight>> GetDocumentHighlightsAsync(
        string uri,
        int lspLine,
        int lspCharacter,
        CancellationToken token)
    {
        InMemoryNemerleSource source;
        lock (_gate)
        {
            if (_disposed ||
                !_openUriToPath.TryGetValue(uri, out var path) ||
                !_documentsByPath.TryGetValue(path, out var state))
            {
                _log.Log($"nemerle document highlight skipped: {uri} is not an open document");
                return [];
            }

            source = state.Source;
        }

        try
        {
            await _highlightGate.WaitAsync(token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return [];
        }

        try
        {
            var delivery = new HighlightDelivery(source);
            _highlightDelivery = delivery;

            AsyncRequest request;
            int expectedVersion;
            lock (_engineOperations)
            {
                lock (_gate)
                {
                    if (_disposed)
                        return [];
                    expectedVersion = source.CurrentVersion;
                }

                // Engine coordinates are 1-based; the LSP character is a UTF-16
                // code unit offset, matching the source's .NET string indexing.
                request = _engine.BeginHighlightUsages(source, lspLine + 1, lspCharacter + 1);
            }

            var (outcome, targets) = await AwaitHighlightAsync(request, delivery).ConfigureAwait(false);
            if (outcome != EngineRequestBridge.RequestOutcome.Completed ||
                token.IsCancellationRequested ||
                source.CurrentVersion != expectedVersion)
            {
                var reason = token.IsCancellationRequested
                    ? EngineRequestBridge.RequestOutcome.Cancelled
                    : outcome == EngineRequestBridge.RequestOutcome.Completed
                        ? EngineRequestBridge.RequestOutcome.Stale
                        : outcome;
                _log.Log(
                    $"nemerle document highlight unavailable at {uri} {lspLine}:{lspCharacter} " +
                    $"(engine request {reason})");
                return [];
            }

            return targets is null or { Length: 0 }
                ? []
                : GotoMapping.ToDocumentHighlights(targets, source.FileIndex);
        }
        finally
        {
            _highlightDelivery = null;
            _highlightGate.Release();
        }
    }

    /// <summary>
    /// The mailbox one <c>BeginHighlightUsages</c> round trip delivers into.
    /// <see cref="Targets"/> stays null until <see cref="SetHighlights"/> runs, so
    /// "not delivered yet" stays distinguishable from the perfectly ordinary
    /// answer "no occurrences" (the caret is not on a symbol).
    /// </summary>
    private sealed class HighlightDelivery(IIdeSource source)
    {
        public IIdeSource Source { get; } = source;

        public volatile NemerleGotoTarget[]? Targets;
    }

    /// <summary>
    /// Waits for a highlight request to <em>deliver</em>, which is not the same as
    /// waiting for it to complete: the engine calls <c>MarkAsCompleted()</c> and
    /// only then invokes the callback (<c>Engine-HighlightUsages.n</c>).
    ///
    /// <para>So completion is treated as "the answer is one statement away on the
    /// worker": after it is observed, this settles for a short window and, if
    /// nothing arrived, concludes the request threw (the only path that completes
    /// without calling back - <c>AsyncWorker.ThreadProc</c> completes a work item
    /// that raised).  The LSP cancellation token is deliberately <em>not</em>
    /// honored here: the caller has already decided to drop the answer, and
    /// draining the request is what keeps its late callback from being handed to
    /// the next caret position.  The wait is bounded by the same ceiling
    /// <see cref="EngineRequestBridge"/> uses, and the worker is single-threaded
    /// anyway, so a drained request delays nothing that was not already queued
    /// behind it.</para>
    /// </summary>
    private static async Task<(EngineRequestBridge.RequestOutcome Outcome, NemerleGotoTarget[]? Targets)>
        AwaitHighlightAsync(AsyncRequest request, HighlightDelivery delivery)
    {
        var deadline = DateTime.UtcNow + HighlightTimeout;

        while (delivery.Targets is null && !request.IsCompleted)
        {
            if (DateTime.UtcNow >= deadline)
            {
                request.Stop = true;
                return (EngineRequestBridge.RequestOutcome.TimedOut, null);
            }

            await Task.Delay(HighlightPollInterval).ConfigureAwait(false);
        }

        var settle = DateTime.UtcNow + HighlightSettle;
        while (delivery.Targets is null && DateTime.UtcNow < settle)
            await Task.Delay(HighlightSettlePollInterval).ConfigureAwait(false);

        if (delivery.Targets is null)
            return (EngineRequestBridge.RequestOutcome.Cancelled, null);

        // The worker sets Stop before completing a request it forced out (only
        // CloseProject can force this kind out), so a set flag means the answer
        // was superseded rather than produced.
        return request.Stop
            ? (EngineRequestBridge.RequestOutcome.Cancelled, null)
            : (EngineRequestBridge.RequestOutcome.Completed, delivery.Targets);
    }

    /// <summary>
    /// Reads the engine's <c>GotoInfo</c>s into the engine-independent
    /// <see cref="NemerleGotoTarget"/> that <see cref="GotoMapping"/> consumes.
    ///
    /// <para>Shared by the highlight callback - where it runs on the worker, as it
    /// should, because <c>GotoInfo.FilePath</c> reads the compiler's process-wide
    /// file-name list - and by the definition/references/rename path, which calls
    /// the synchronous <c>GetGotoInfo</c> and flattens on the LSP thread.  That
    /// asymmetry is deliberate and bounded: <c>GetGotoInfo</c> is itself
    /// <c>BeginGetGotoInfo</c> plus <c>AsyncWaitHandle.WaitOne()</c>, so the
    /// resolution runs on the worker and only this last read does not
    /// (<c>56-wp-p-plan.md</c> §3-1, which retracts the plan's original claim that
    /// the whole path ran off-worker).  Moving the read as well would need
    /// <c>BeginGetGotoInfo</c> on <c>IIdeEngine</c>, which it is not.</para>
    /// </summary>
    private static NemerleGotoTarget[] FlattenGotoInfos(IEnumerable<GotoInfo> infos)
    {
        var targets = new List<NemerleGotoTarget>();
        foreach (var info in infos)
        {
            targets.Add(new NemerleGotoTarget(
                info.FilePath,
                info.FileIndex,
                info.Line,
                info.Column,
                info.EndLine,
                info.EndColumn,
                (NemerleUsageType)(int)info.UsageType));
        }

        return [.. targets];
    }

    /// <summary>
    /// Collects the engine's usage set (declaration included) for a position in an
    /// open document, plus the document itself.  The source is null when the
    /// document is not open; an empty target array means the engine resolved
    /// nothing there.  Same route as <see cref="GetReferences"/> - deliberately,
    /// so what rename rewrites is exactly what references lists and what
    /// documentHighlight paints.
    /// </summary>
    private (NemerleGotoTarget[] Targets, InMemoryNemerleSource? Source) CollectUsages(
        string uri,
        int lspLine,
        int lspCharacter)
    {
        lock (_engineOperations)
        {
            InMemoryNemerleSource source;
            lock (_gate)
            {
                if (_disposed ||
                    !_openUriToPath.TryGetValue(uri, out var path) ||
                    !_documentsByPath.TryGetValue(path, out var state))
                    return ([], null);
                source = state.Source;
            }

            // Engine coordinates are 1-based; the LSP character is a UTF-16 code
            // unit offset, matching the source's .NET string indexing.
            var infos = _engine.GetGotoInfo(source, lspLine + 1, lspCharacter + 1, GotoKind.Usages);
            return (infos is null ? [] : FlattenGotoInfos(infos), source);
        }
    }

    /// <summary>
    /// Answers <c>textDocument/prepareRename</c>: the range of the name under the
    /// caret, or the reason it cannot be renamed.  The rules live in the pure
    /// <see cref="RenameMapping.Prepare"/>; this only supplies the usage set.
    /// </summary>
    public NemerleRenamePreparation PrepareRename(string uri, int lspLine, int lspCharacter)
    {
        var (targets, source) = CollectUsages(uri, lspLine, lspCharacter);
        return source is null
            ? new NemerleRenamePreparation(null, NemerleRenameRefusal.DocumentNotOpen)
            : RenameMapping.Prepare(targets, source.FileIndex, lspLine, lspCharacter);
    }

    /// <summary>
    /// Computes the <c>WorkspaceEdit</c> for <c>textDocument/rename</c>, or the
    /// refusal that stopped it.
    ///
    /// <para><b>Every step can refuse, and refusing is the safe outcome.</b>  A
    /// rename that misses an occurrence, or rewrites something that was not the
    /// symbol, breaks the user's build in a way they will blame on their own
    /// edit.  So: the symbol must be declared inside this engine workspace
    /// (<see cref="RenameMapping.Prepare"/>, which is where
    /// <c>39-prerelease-wp-n2-log.md</c> §7-4's cross-project rule is enforced),
    /// the new name must be a legal identifier that is not a keyword in this
    /// file's environment, every occurrence's current text must still be the
    /// symbol's own name, and the resulting edits must not overlap.</para>
    /// </summary>
    public async Task<EngineRenameResult> GetRenameEditsAsync(
        string uri,
        int lspLine,
        int lspCharacter,
        string newName,
        CancellationToken token)
    {
        var (targets, source) = CollectUsages(uri, lspLine, lspCharacter);
        if (source is null)
            return EngineRenameResult.Refused(NemerleRenameRefusal.DocumentNotOpen);

        var preparation = RenameMapping.Prepare(targets, source.FileIndex, lspLine, lspCharacter);
        if (preparation.Range is not { } caretRange)
            return EngineRenameResult.Refused(preparation.Refusal);

        var keywords = await GetContextKeywordsAsync(source, lspLine + 1, token).ConfigureAwait(false);
        var nameRefusal = RenameMapping.ValidateNewName(newName, keywords);
        if (nameRefusal != NemerleRenameRefusal.None)
            return EngineRenameResult.Refused(nameRefusal);

        var occurrences = ReadOccurrences(targets);
        var expected = FindOccurrenceText(occurrences, caretRange);
        if (expected is null)
            return EngineRenameResult.Refused(NemerleRenameRefusal.InconsistentOccurrences);

        var occurrenceRefusal = RenameMapping.CheckOccurrences(occurrences, expected);
        if (occurrenceRefusal != NemerleRenameRefusal.None)
            return EngineRenameResult.Refused(occurrenceRefusal);

        var edit = RenameMapping.ToWorkspaceEdit(occurrences, newName);
        return edit.IsUsable
            ? new EngineRenameResult(edit.Edit, NemerleRenameRefusal.None, null, expected)
            : new EngineRenameResult(null, NemerleRenameRefusal.InconsistentOccurrences, edit.Conflict, expected);
    }

    /// <summary>
    /// Reads the current source text of every navigable occurrence.  The text
    /// comes from the server's own buffers (project sources are registered as
    /// documents even when no editor has them open), so an occurrence in a file
    /// the server does not have is reported as a null text, which
    /// <see cref="RenameMapping.CheckOccurrences"/> treats as a refusal rather
    /// than as permission to rewrite it unseen.
    /// </summary>
    private List<NemerleRenameOccurrence> ReadOccurrences(NemerleGotoTarget[] targets)
    {
        var occurrences = new List<NemerleRenameOccurrence>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var target in targets)
        {
            var locations = GotoMapping.ToLocations([target], includeDeclaration: true);
            if (locations.Count == 0)
                continue;

            var location = locations[0];
            var key = $"{location.Uri}|{location.StartLine}|{location.StartCharacter}|" +
                $"{location.EndLine}|{location.EndCharacter}";
            if (!seen.Add(key))
                continue;

            occurrences.Add(new NemerleRenameOccurrence(location, ReadRegion(target)));
        }

        return occurrences;
    }

    /// <summary>
    /// The text an engine target currently spans, or null when the server has no
    /// buffer for that document or the range no longer fits it.  Reads the
    /// document's own text state (guarded by the source's gate), not engine
    /// state, so it does not need the worker.
    /// </summary>
    private string? ReadRegion(NemerleGotoTarget target)
    {
        InMemoryNemerleSource source;
        lock (_gate)
        {
            if (!_documentsByFileIndex.TryGetValue(target.FileIndex, out var state))
                return null;
            source = state.Source;
        }

        try
        {
            // The target keeps the engine's 1-based, end-exclusive coordinates,
            // which is exactly what GetRegion takes.
            return source.GetRegion(target.Line, target.Column, target.EndLine, target.EndColumn);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private static string? FindOccurrenceText(
        List<NemerleRenameOccurrence> occurrences,
        NemerleGotoLocation caretRange)
    {
        foreach (var occurrence in occurrences)
        {
            if (occurrence.Location == caretRange)
                return occurrence.Text;
        }

        return null;
    }

    /// <summary>
    /// The words that are keywords for the edited line: the core environment's
    /// set plus whatever a syntax macro added through this file's <c>using</c>s
    /// (the WP-O5a distinction, read the same way the colorizer reads it).
    ///
    /// <para>Runs on the AsyncWorker thread - <c>GetActiveEnv</c> walks the
    /// declaration tree, which is engine state.  If the engine cannot answer
    /// (not initialized yet, request superseded, timed out), this falls back to
    /// the lexer's static <c>BaseKeywords</c> rather than refusing the rename:
    /// the failure that lets through is "the user chose a macro keyword as a
    /// name", which the compiler reports immediately on the next build, whereas
    /// refusing would block a legitimate rename over a transient engine state.
    /// The fallback is logged.</para>
    /// </summary>
    private async Task<IReadOnlySet<string>> GetContextKeywordsAsync(
        InMemoryNemerleSource source,
        int engineLine,
        CancellationToken token)
    {
        var keywords = new HashSet<string>(StringComparer.Ordinal);
        foreach (var keyword in LexerBase.BaseKeywords)
            keywords.Add(keyword);

        Task<EngineRequestBridge.RequestResult<HashSet<string>?>> pending;
        lock (_engineOperations)
        {
            int expectedVersion;
            lock (_gate)
            {
                if (_disposed)
                    return keywords;
                expectedVersion = source.CurrentVersion;
            }

            pending = _bridge.RunAsync<HashSet<string>?>(
                () => BeginProbeKeywords(source, engineLine),
                () => source.CurrentVersion,
                expectedVersion,
                static request => ((KeywordProbeRequest)request).Keywords,
                token);
        }

        var result = await pending.ConfigureAwait(false);
        if (!result.IsUsable || result.Value is null)
        {
            _log.Log(
                "nemerle rename: could not read this file's keyword environment " +
                $"(engine request {result.Outcome}); using the base keyword set only");
            return keywords;
        }

        foreach (var keyword in result.Value)
            keywords.Add(keyword);

        return keywords;
    }

    /// <summary>
    /// The work item that reads the keyword environment on the worker.  Borrows
    /// <c>AsyncRequestType.EmptyRequest</c> like <see cref="ColorizeRequest"/> and
    /// the signature-help flattening, rather than adding a kind to the shared
    /// <c>AsyncRequestType</c>.
    /// </summary>
    private sealed class KeywordProbeRequest(
        IIdeEngine engine,
        IIdeSource source,
        int line,
        Action<AsyncRequest> work)
        : AsyncRequest(AsyncRequestType.EmptyRequest, engine, source, work)
    {
        public int Line { get; } = line;

        public HashSet<string>? Keywords { get; set; }
    }

    private AsyncRequest BeginProbeKeywords(InMemoryNemerleSource source, int engineLine)
    {
        var request = new KeywordProbeRequest(_engine, source, engineLine, RunProbeKeywords);
        AsyncWorker.AddWork(request);
        return request;
    }

    /// <summary>Runs on the AsyncWorker thread.</summary>
    private void RunProbeKeywords(AsyncRequest request)
    {
        var probe = (KeywordProbeRequest)request;
        try
        {
            if (request.Stop || _disposed)
                return;

            if (!_engine.RequestOnInitEngine() ||
                _engine is not ManagerClass manager ||
                manager.CoreEnv is null)
                return;

            var keywords = new HashSet<string>(StringComparer.Ordinal);
            foreach (var keyword in manager.CoreEnv.Keywords)
                keywords.Add(keyword);

            var active = _engine.GetActiveEnv(((InMemoryNemerleSource)request.Source).FileIndex, probe.Line);
            if (active.Field0 is not null)
            {
                foreach (var keyword in active.Field0.Keywords)
                    keywords.Add(keyword);
            }

            probe.Keywords = keywords;
        }
        catch (Exception ex)
        {
            _log.Warning($"nemerle rename keyword probe failed: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            // The worker only completes a request itself when the work threw
            // (AsyncWorker.ThreadProc), so the success path must complete it.
            request.MarkAsCompleted();
        }
    }

    /// <summary>
    /// Offers the "implement missing members" / "override inherited members" code
    /// actions for a position (WP-P5).
    ///
    /// <para><b>Both engine entry points answer through a callback, and both
    /// throw right after queueing it.</b>  <c>Engine-FindUnimplementedMembers.n</c>
    /// runs <c>AsyncWorker.AddResponse(...)</c> and then <c>assert(false)</c>
    /// (upstream leftover); <c>AsyncWorker.ThreadProc</c> catches that and marks
    /// the request completed, so the response survives - measured, not assumed
    /// (see the log for this WP).  The response is dispatched on this class's
    /// response pump, not on the worker, so the callback only <em>stores</em> the
    /// engine objects; nothing is read off them there.  Enumerating the grouping
    /// would already be engine work: its key is a <c>FixedType.Class</c>, whose
    /// equality goes through the type solver.</para>
    ///
    /// <para>The reading and the source generation therefore happen in a second
    /// work item on the worker, the same two-hop shape WP-P1 uses, and only plain
    /// strings cross back.</para>
    /// </summary>
    public async Task<IReadOnlyList<NemerleCodeAction>> GetCodeActionsAsync(
        string uri,
        int lspLine,
        int lspCharacter,
        CancellationToken token)
    {
        InMemoryNemerleSource source;
        lock (_gate)
        {
            if (_disposed ||
                !_openUriToPath.TryGetValue(uri, out var path) ||
                !_documentsByPath.TryGetValue(path, out var state))
            {
                _log.Log($"nemerle code action skipped: {uri} is not an open document");
                return [];
            }

            source = state.Source;
        }

        var actions = new List<NemerleCodeAction>();
        var implement = await SuggestMembersAsync(source, lspLine, lspCharacter, token)
            .ConfigureAwait(false);
        AppendActions(actions, source, implement, "Implement {0} ({1} {2})");

        return actions;
    }

    /// <summary>
    /// Turns one worker-side generation into an offered action, reading the
    /// insertion line's own indentation from the buffer (document text, not
    /// engine state) so the inserted members line up with the type's body.
    /// </summary>
    private void AppendActions(
        List<NemerleCodeAction> actions,
        InMemoryNemerleSource source,
        MemberSuggestion? suggestion,
        string titleFormat)
    {
        if (suggestion is null || suggestion.Generations.Count == 0)
            return;

        // The engine's location is 1-based and its end column is exclusive, so
        // the closing brace of the type body sits at EndColumn - 1.
        var engineLine = suggestion.InsertionLine;
        var engineColumn = Math.Max(1, suggestion.InsertionColumn - 1);
        string linePrefix;
        try
        {
            linePrefix = engineColumn > 1
                ? source.GetRegion(engineLine, 1, engineLine, engineColumn)
                : string.Empty;
        }
        catch (ArgumentOutOfRangeException)
        {
            _log.Warning(
                $"nemerle code action: the insertion point {engineLine}:{engineColumn} is outside the buffer");
            return;
        }

        var insertion = new NemerleInsertionPoint(
            GotoMapping.ToUri(source.Path),
            engineLine - 1,
            engineColumn - 1,
            linePrefix);
        var indent = CodeActionMapping.IndentFor(linePrefix);

        foreach (var generation in suggestion.Generations)
        {
            var action = CodeActionMapping.ToCodeAction(generation, insertion, indent, titleFormat);
            if (action is not null)
                actions.Add(action);
        }
    }

    /// <summary>
    /// One round trip: ask the engine for the missing members at a position, wait
    /// for the callback, then generate their source on the worker.
    /// </summary>
    private async Task<MemberSuggestion?> SuggestMembersAsync(
        InMemoryNemerleSource source,
        int lspLine,
        int lspCharacter,
        CancellationToken token)
    {
        try
        {
            await _memberSuggestionGate.WaitAsync(token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return null;
        }

        try
        {
            var delivery = new MemberSuggestionDelivery(source);
            _memberSuggestion = delivery;

            AsyncRequest request;
            int expectedVersion;
            lock (_engineOperations)
            {
                lock (_gate)
                {
                    if (_disposed)
                        return null;
                    expectedVersion = source.CurrentVersion;
                }

                // Engine coordinates are 1-based; the LSP character is a UTF-16
                // code unit offset, matching the source's .NET string indexing.
                request = _engine.BeginFindUnimplementedMembers(source, lspLine + 1, lspCharacter + 1);
            }

            if (!await AwaitMemberDeliveryAsync(request, delivery).ConfigureAwait(false))
                return null;

            if (token.IsCancellationRequested || source.CurrentVersion != expectedVersion)
                return null;

            Task<EngineRequestBridge.RequestResult<MemberSuggestion?>> pending;
            lock (_engineOperations)
            {
                pending = _bridge.RunAsync<MemberSuggestion?>(
                    () => BeginGenerateMembers(source, delivery),
                    () => source.CurrentVersion,
                    expectedVersion,
                    static request => ((GenerateMembersRequest)request).Suggestion,
                    token);
            }

            var generated = await pending.ConfigureAwait(false);
            if (!generated.IsUsable)
            {
                _log.Log(
                    $"nemerle code action: member generation did not complete ({generated.Outcome})");
                return null;
            }

            return generated.Value;
        }
        finally
        {
            _memberSuggestion = null;
            _memberSuggestionGate.Release();
        }
    }

    /// <summary>
    /// Waits for one <c>AddUnimplementedMembers</c> / <c>AddOverrideMembers</c>
    /// callback.  Like the highlight mailbox (WP-P2) the correlation is
    /// structural - the gate keeps exactly one request in flight - but the settle
    /// window has to be longer here: the engine hands its answer to the
    /// <em>response</em> queue, which this class's pump drains every 10 ms, so
    /// delivery necessarily lands after completion.
    /// </summary>
    private static async Task<bool> AwaitMemberDeliveryAsync(
        AsyncRequest request,
        MemberSuggestionDelivery delivery)
    {
        var deadline = DateTime.UtcNow + MemberSuggestionTimeout;
        while (!delivery.Delivered && !request.IsCompleted)
        {
            if (DateTime.UtcNow >= deadline)
            {
                request.Stop = true;
                return false;
            }

            await Task.Delay(HighlightPollInterval).ConfigureAwait(false);
        }

        var settle = DateTime.UtcNow + MemberSuggestionSettle;
        while (!delivery.Delivered && DateTime.UtcNow < settle)
            await Task.Delay(HighlightSettlePollInterval).ConfigureAwait(false);

        return delivery.Delivered && !request.Stop;
    }

    /// <summary>
    /// The mailbox one member-suggestion round trip delivers into.  The engine
    /// objects are stored untouched: they are read on the worker
    /// (<see cref="RunGenerateMembers"/>), never on the response pump.
    /// </summary>
    private sealed class MemberSuggestionDelivery(IIdeSource source)
    {
        public IIdeSource Source { get; } = source;

        public volatile TypeBuilder? Type;

        public volatile IEnumerable<IGrouping<FixedType.Class, IMember>>? Unimplemented;

        public volatile bool Delivered;
    }

    /// <summary>Generated members plus where they go, in engine coordinates.</summary>
    internal sealed record MemberSuggestion(
        IReadOnlyList<NemerleMemberGeneration> Generations,
        int InsertionLine,
        int InsertionColumn);

    private sealed class GenerateMembersRequest(
        IIdeEngine engine,
        IIdeSource source,
        MemberSuggestionDelivery delivery,
        Action<AsyncRequest> work)
        : AsyncRequest(AsyncRequestType.EmptyRequest, engine, source, work)
    {
        public MemberSuggestionDelivery Delivery { get; } = delivery;

        public MemberSuggestion? Suggestion { get; set; }
    }

    private AsyncRequest BeginGenerateMembers(
        InMemoryNemerleSource source,
        MemberSuggestionDelivery delivery)
    {
        var request = new GenerateMembersRequest(_engine, source, delivery, RunGenerateMembers);
        AsyncWorker.AddWork(request);
        return request;
    }

    /// <summary>Runs on the AsyncWorker thread.</summary>
    private void RunGenerateMembers(AsyncRequest request)
    {
        var generate = (GenerateMembersRequest)request;
        try
        {
            if (request.Stop || _disposed || generate.Delivery.Type is not { } type)
                return;

            var source = (InMemoryNemerleSource)request.Source;
            var fileIndex = source.FileIndex;

            // A type can be partial: insert into the part declared in this file.
            Location? part = null;
            foreach (var ast in type.AstParts)
            {
                if (ast.Location.FileIndex == fileIndex)
                {
                    part = ast.Location;
                    break;
                }
            }

            if (part is not { } body)
                return;

            var generations = GenerateImplementations(fileIndex, generate.Delivery.Unimplemented);

            if (generations.Count > 0)
                generate.Suggestion = new MemberSuggestion(generations, body.EndLine, body.EndColumn);
        }
        catch (Exception ex)
        {
            // A generator failure must not take the worker loop down; the caret
            // simply gets no code action (and the Output says why).
            _log.Warning($"nemerle code action generation failed: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            // The worker only completes a request itself when the work threw
            // (AsyncWorker.ThreadProc), so the success path must complete it.
            request.MarkAsCompleted();
        }
    }

    /// <summary>
    /// Runs on the AsyncWorker thread: one generation per interface, in the
    /// engine's own <c>InterfaceMemberImplSourceGenerator</c> rendering (via the
    /// public <c>Utils.GenerateMemberImplementation</c> helper, which is what the
    /// legacy VS "implement members" dialog uses).
    /// </summary>
    private static List<NemerleMemberGeneration> GenerateImplementations(
        int fileIndex,
        IEnumerable<IGrouping<FixedType.Class, IMember>>? groups)
    {
        var generations = new List<NemerleMemberGeneration>();
        if (groups is null)
            return generations;

        foreach (var group in groups)
        {
            var writer = new StringWriter();
            var count = 0;
            var interfaceMembers = group.Key.tycon.GetMembers();
            var writtenProperties = new HashSet<IProperty>();

            foreach (var member in group)
            {
                // The compiler tracks an unimplemented property as its accessor
                // methods, so generating them as-is would emit `get_Name() :
                // string` - which compiles but does not implement the property,
                // leaving the very error the action promised to fix.  Map each
                // accessor back to its property and emit the property once.
                var property = FindOwningProperty(interfaceMembers, member);
                var target = member;
                if (property is not null)
                {
                    if (!writtenProperties.Add(property))
                        continue;
                    target = property;
                }

                Nemerle.Compiler.Utils.Utils.GenerateMemberImplementation(
                    // Positional: the engine's parameter is named `explicit`,
                    // which C# cannot use as an argument name.  Order is
                    // (writer, fileIndex, type, member, explicit, accessMods,
                    // implName, generateXmlDoc).
                    writer, fileIndex, group.Key, target, false, "public", null, false);
                count++;
            }

            if (count > 0)
                generations.Add(new NemerleMemberGeneration(group.Key.ToString(), count, writer.ToString()));
        }

        return generations;
    }

    /// <summary>
    /// The property whose getter or setter <paramref name="member"/> is, or null
    /// when it is an ordinary method.  Matched by identity against the declaring
    /// interface's own members rather than by stripping a <c>get_</c> prefix off
    /// the name, so a method that merely looks like an accessor is not mistaken
    /// for one.  Runs on the AsyncWorker thread.
    /// </summary>
    private static IProperty? FindOwningProperty(IEnumerable<IMember> interfaceMembers, IMember member)
    {
        if (member is not IMethod method)
            return null;

        foreach (var candidate in interfaceMembers)
        {
            if (candidate is not IProperty property)
                continue;
            if (ReferenceEquals(property.GetGetter(), method) || ReferenceEquals(property.GetSetter(), method))
                return property;
        }

        return null;
    }

    /// <summary>
    /// Computes the method tip (signature help) for an open document at an LSP
    /// position (0-based line/character, UTF-16 code units), flattened into the
    /// engine-independent <see cref="NemerleMethodTip"/> that
    /// <see cref="SignatureHelpMapping"/> maps to LSP shapes.  Returns null when
    /// the document is not open, the caret is not inside a call's argument list,
    /// or the request was cancelled/superseded/stale.
    ///
    /// <para><b>Two worker round trips, on purpose.</b>  The engine's
    /// <c>BeginGetMethodTipInfo</c> already runs on the AsyncWorker thread, so
    /// computing the tip honors the single-thread contract (see
    /// <see cref="GetSemanticTokensAsync"/> for what happens when it is broken).
    /// But the resulting <c>MethodTipInfo</c> is a lazy view over the engine's
    /// members: <c>GetDescription</c> / <c>GetParameterInfo</c> call
    /// <c>XmlDocReader.GetInfo</c>, whose cache is plain unsynchronized mutable
    /// state (a module-level <c>Map</c> reassigned on every miss, and an
    /// <c>XmlDocFile</c> that reloads itself in place when the .xml file's
    /// timestamp moved).  Reading it from the LSP thread while the worker reads
    /// it too is a data race on engine state, so the flattening is posted back
    /// to the worker as a second request instead.  The cost is one extra queue
    /// hop; the alternative is the class of bug WP-O5a spent a WP finding.</para>
    /// </summary>
    public async Task<NemerleMethodTip?> GetSignatureHelpAsync(
        string uri,
        int lspLine,
        int lspCharacter,
        CancellationToken token)
    {
        InMemoryNemerleSource source;
        Task<EngineRequestBridge.RequestResult<MethodTipInfo?>> pending;
        lock (_engineOperations)
        {
            int expectedVersion;
            lock (_gate)
            {
                if (_disposed ||
                    !_openUriToPath.TryGetValue(uri, out var path) ||
                    !_documentsByPath.TryGetValue(path, out var state))
                {
                    _log.Log($"nemerle signature help skipped: {uri} is not an open document");
                    return null;
                }

                source = state.Source;
                expectedVersion = source.CurrentVersion;
            }

            // Engine coordinates are 1-based; the LSP character is a UTF-16 code
            // unit offset, matching the source's .NET string indexing.
            var line = lspLine + 1;
            var column = lspCharacter + 1;
            pending = _bridge.RunAsync<MethodTipInfo?>(
                () => _engine.BeginGetMethodTipInfo(source, line, column),
                () => source.CurrentVersion,
                expectedVersion,
                static request => ((MethodTipInfoAsyncRequest)request).MethodTipInfo,
                token);
        }

        var result = await pending.ConfigureAwait(false);
        if (!result.IsUsable)
        {
            _log.Log(
                $"nemerle signature help unavailable at {uri} {lspLine}:{lspCharacter} (engine request {result.Outcome})");
            return null;
        }

        // HasTip is a plain Location read on the tip itself; the members behind
        // it are only touched by the worker-side flattening below.
        if (result.Value is not { } tip || !tip.HasTip)
            return null;

        Task<EngineRequestBridge.RequestResult<NemerleMethodTip?>> describing;
        lock (_engineOperations)
        {
            int expectedVersion;
            lock (_gate)
            {
                if (_disposed)
                    return null;
                expectedVersion = source.CurrentVersion;
            }

            describing = _bridge.RunAsync<NemerleMethodTip?>(
                () => BeginDescribeMethodTip(source, tip),
                () => source.CurrentVersion,
                expectedVersion,
                static request => ((MethodTipDescriptionRequest)request).Description,
                token);
        }

        var described = await describing.ConfigureAwait(false);
        if (!described.IsUsable)
        {
            _log.Log(
                $"nemerle signature help unavailable at {uri} {lspLine}:{lspCharacter} (description request {described.Outcome})");
            return null;
        }

        return described.Value;
    }

    /// <summary>
    /// The work item that flattens a <c>MethodTipInfo</c> into plain data on the
    /// engine's worker thread.  Like <see cref="ColorizeRequest"/> it borrows
    /// <c>AsyncRequestType.EmptyRequest</c> rather than adding a request kind to
    /// the shared <c>AsyncRequestType</c> enum, and is therefore never forced out
    /// by anything but <c>CloseProject</c>.
    /// </summary>
    private sealed class MethodTipDescriptionRequest(
        IIdeEngine engine,
        IIdeSource source,
        MethodTipInfo tip,
        Action<AsyncRequest> work)
        : AsyncRequest(AsyncRequestType.EmptyRequest, engine, source, work)
    {
        public MethodTipInfo Tip { get; } = tip;

        public NemerleMethodTip? Description { get; set; }
    }

    /// <summary>
    /// Enqueues the flattening pass.  Runs synchronously up to the enqueue, so
    /// the caller may hold <c>_engineOperations</c> here.
    /// </summary>
    private AsyncRequest BeginDescribeMethodTip(InMemoryNemerleSource source, MethodTipInfo tip)
    {
        var request = new MethodTipDescriptionRequest(_engine, source, tip, RunDescribeMethodTip);
        AsyncWorker.AddWork(request);
        return request;
    }

    /// <summary>Runs on the AsyncWorker thread.</summary>
    private void RunDescribeMethodTip(AsyncRequest request)
    {
        var describe = (MethodTipDescriptionRequest)request;
        try
        {
            if (request.Stop || _disposed)
                return;

            describe.Description = DescribeMethodTip(describe.Tip);
        }
        catch (Exception ex)
        {
            // A malformed tip must not take the worker loop down; the request
            // simply answers "no signature help" (and says so in the Output).
            _log.Warning($"nemerle signature help failed: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            // The worker only completes a request itself when the work threw
            // (AsyncWorker.ThreadProc), so the success path must complete it.
            request.MarkAsCompleted();
        }
    }

    /// <summary>
    /// Reads every overload out of the engine's tip.  Runs on the AsyncWorker
    /// thread (see <see cref="GetSignatureHelpAsync"/>).
    /// </summary>
    private static NemerleMethodTip? DescribeMethodTip(MethodTipInfo tip)
    {
        var count = tip.GetCount();
        if (count <= 0)
            return null;

        var signatures = new List<NemerleTipSignature>(count);
        for (var index = 0; index < count; index++)
        {
            var parameterCount = tip.GetParameterCount(index);
            var parameters = new List<NemerleTipParameter>(Math.Max(0, parameterCount));
            for (var parameter = 0; parameter < parameterCount; parameter++)
            {
                // The engine returns a Nemerle tuple (name, display, doc).
                var info = tip.GetParameterInfo(index, parameter);
                parameters.Add(new NemerleTipParameter(
                    info.Field0 ?? string.Empty,
                    info.Field1 ?? string.Empty,
                    info.Field2));
            }

            signatures.Add(new NemerleTipSignature(
                tip.GetName(index) ?? string.Empty,
                tip.GetType(index) ?? string.Empty,
                tip.GetDescription(index),
                parameters));
        }

        return new NemerleMethodTip(signatures, tip.DefaultMethod, tip.ParameterIndex);
    }

    /// <summary>
    /// Colorizes a whole open document with the engine's <c>ScanLexer</c> and
    /// returns LSP semantic tokens (WP-O5a).  Returns null when the document is not
    /// open, the engine has no initialized compiler yet, or the answer no longer
    /// applies (superseded / the buffer moved on); the client then keeps its
    /// TextMate coloring instead of being handed a half-classified document.
    ///
    /// <para><b>Why this goes through the AsyncWorker instead of just running under
    /// <c>_engineOperations</c>.</b>  The IDE engine is single-threaded by contract:
    /// every one of its entry points asserts it
    /// (<c>AsyncWorker.CheckCurrentThreadIsTheAsyncWorker</c>, ~10 call sites), and
    /// every other feature here - hover, completion, definition, references, the
    /// types-tree build - reaches it through a <c>Begin*</c> request on that one
    /// worker thread.  <c>_engineOperations</c> serializes this class's own
    /// bookkeeping; it does <b>not</b> serialize against the worker, which is where
    /// reloads and method-body typing run.</para>
    ///
    /// <para><b>What breaks when the colorizer runs off that thread.</b>
    /// <c>ScanLexer.GetIdentifierColor</c> calls <c>GlobalEnv.LookupType</c> for
    /// every identifier in the document, which forces lazy construction of
    /// <c>LibraryReference.ExternalTypeInfo</c> for referenced-assembly types.  That
    /// constructor publishes <c>this</c> into the shared namespace-tree cache before
    /// it assigns <c>direct_supertypes</c> - deliberately, to break recursion within
    /// one thread ("first cache ourself to avoid loops").  A second thread that
    /// resolves the same type meanwhile gets the half-built instance and dereferences
    /// the still-null field in <c>SuperClass()</c>.  Observed as a
    /// NullReferenceException inside the worker's method-body typing, which
    /// <c>IntelliSenseModeMethodBuilder</c> then reports as an error pinned to the
    /// method body's opening brace, leaving <c>_bodyTyped</c> null so hover inside
    /// that method stays dead until the file is edited.  It is timing-dependent
    /// (widest while a reload re-reflects the references), so it survives test runs
    /// and shows up in real editor sessions.</para>
    ///
    /// <para><b>Alternatives that were rejected.</b>  Locking around the engine
    /// cannot work: the shared state is the compiler's global type caches, which
    /// this class has no way to fence.  Dropping the <c>LookupType</c> call would
    /// lose user-type coloring and still leave <c>GetActiveEnv</c>'s declaration-tree
    /// walk running off-thread.  Making <c>ExternalTypeInfo</c> publish itself only
    /// once fully built would break the single-thread recursion guard it exists
    /// for.  Queueing is the fix the engine already prescribes.</para>
    /// </summary>
    public async Task<EngineSemanticTokens?> GetSemanticTokensAsync(string uri, CancellationToken token)
    {
        Task<EngineRequestBridge.RequestResult<EngineSemanticTokens?>> pending;
        lock (_engineOperations)
        {
            InMemoryNemerleSource source;
            int expectedVersion;
            lock (_gate)
            {
                if (_disposed ||
                    !_openUriToPath.TryGetValue(uri, out var path) ||
                    !_documentsByPath.TryGetValue(path, out var state))
                {
                    _log.Log($"nemerle semantic tokens skipped: {uri} is not an open document");
                    return null;
                }

                source = state.Source;
                expectedVersion = source.CurrentVersion;
            }

            pending = _bridge.RunAsync<EngineSemanticTokens?>(
                () => BeginColorize(source),
                () => source.CurrentVersion,
                expectedVersion,
                static request => ((ColorizeRequest)request).Tokens,
                token);
        }

        var result = await pending.ConfigureAwait(false);
        return result.IsUsable ? result.Value : null;
    }

    /// <summary>
    /// The colorize work item.  It carries its result the way the engine's own
    /// requests do (<c>QuickTipInfoAsyncRequest.QuickTipInfo</c> etc.), so
    /// <see cref="EngineRequestBridge"/> can extract it after completion.
    ///
    /// <para><b>Why <c>AsyncRequestType.EmptyRequest</c>.</b>  The colorizer is not
    /// one of the engine's own request kinds, and giving it one would mean editing
    /// <c>AsyncRequestType</c> in the VsIntegration sources that the legacy Visual
    /// Studio integration also builds from - a shared-source change for a
    /// server-only feature.  <c>EmptyRequest</c> is the engine's existing name for
    /// "a work item the queue carries but does not reason about", and the engine
    /// uses it exactly this way itself (<c>Engine-BuildTypeTree.n</c> posts a bare
    /// <c>AsyncRequest(AsyncRequestType.EmptyRequest, this, null, emptyWork)</c>).
    /// The one behavioral consequence is <c>AsyncRequest.IsForceOutBy</c>: an
    /// <c>EmptyRequest</c> is superseded only by <c>CloseProject</c>, never by a
    /// later build or edit.  That is the safe direction - the request is never
    /// silently dropped - and the cost, redundant colorize passes when requests
    /// pile up, is avoided on the caller's side instead: the handler waits for the
    /// types-tree signal rather than polling.</para>
    /// </summary>
    private sealed class ColorizeRequest(IIdeEngine engine, IIdeSource source, Action<AsyncRequest> work)
        : AsyncRequest(AsyncRequestType.EmptyRequest, engine, source, work)
    {
        public EngineSemanticTokens? Tokens { get; set; }
    }

    /// <summary>
    /// Enqueues a colorize pass on the engine's worker thread.  Runs synchronously
    /// up to the enqueue (before the first await in
    /// <see cref="EngineRequestBridge.RunAsync{T}"/>), so the caller may hold
    /// <c>_engineOperations</c> here and await the result after releasing it.
    /// </summary>
    private AsyncRequest BeginColorize(InMemoryNemerleSource source)
    {
        var request = new ColorizeRequest(_engine, source, RunColorize);
        AsyncWorker.AddWork(request);
        return request;
    }

    /// <summary>Runs on the AsyncWorker thread.</summary>
    private void RunColorize(AsyncRequest request)
    {
        var source = (InMemoryNemerleSource)request.Source;
        try
        {
            if (request.Stop || _disposed)
                return;

            // ScanLexer takes its base keyword set from ManagerClass.CoreEnv (and
            // asserts on it), which exists only once InitCompiler ran.  At startup
            // the client asks before that, and the answer has to be "nothing yet"
            // rather than a half-classified document (see the refresh retry in
            // NemerleSemanticTokensHandler).
            if (!_engine.RequestOnInitEngine() ||
                _engine is not ManagerClass manager ||
                manager.CoreEnv is null)
                return;

            ((ColorizeRequest)request).Tokens = Tokenize(manager, source);
        }
        catch (Exception ex)
        {
            // A colorizer failure must not take out the worker loop; the document
            // simply falls back to grammar-based coloring.
            _log.Warning($"nemerle semantic tokens failed for {source.Path}: {ex}");
        }
        finally
        {
            // The worker only completes a request itself when the work threw
            // (AsyncWorker.ThreadProc); on the success path every work item is
            // expected to do it, as the engine's own Begin* handlers do.
            request.MarkAsCompleted();
        }
    }

    /// <summary>
    /// True when the URI belongs to a document the client has open.  Lets the
    /// semantic tokens handler tell "the compiler is not ready yet, waiting is
    /// worth it" apart from "this document is not ours, answering now is right".
    /// </summary>
    public bool IsDocumentOpen(string uri)
    {
        lock (_gate) return !_disposed && _openUriToPath.ContainsKey(uri);
    }

    /// <summary>Runs on the AsyncWorker thread; see <see cref="GetSemanticTokensAsync"/>.</summary>
    private EngineSemanticTokens Tokenize(ManagerClass manager, InMemoryNemerleSource source)
    {
        var text = source.GetText();
        var coreKeywords = manager.CoreEnv.Keywords;
        var lexer = new ScanLexer(manager);
        lexer.SetFileName(source.Path);

        var tokens = new List<EngineSemanticToken>();
        // The colorizer carries multi-line constructs (block comments, verbatim
        // and recursive strings, quotations) across lines in this state word, the
        // same way the VS scanner threads it through IScanner.
        var state = ScanState.None;
        GlobalEnv? env = null;
        TypeBuilder? typeBuilder = null;
        // The line span the cached env/typeBuilder is valid for, as reported by
        // GetActiveEnv itself: that keeps the declaration-tree walk to once per
        // declaration instead of once per line.
        var envFirstLine = 0;
        var envLastLine = -1;
        // Whether the types tree answered at all: without it the colorizer runs on
        // CoreEnv, which cannot know macro-introduced keywords or user types.
        var fromTypesTree = false;

        var lineNumber = 0;
        foreach (var line in SplitLines(text))
        {
            lineNumber++;
            if (lineNumber < envFirstLine || lineNumber > envLastLine)
            {
                var active = _engine.GetActiveEnv(source.FileIndex, lineNumber);
                // A null env means no types tree yet (or a line outside every
                // declaration); keep the last known one, as the VS scanner does,
                // and let SetLine fall back to CoreEnv when there is none at all.
                if (active.Field0 is not null)
                {
                    env = active.Field0;
                    typeBuilder = active.Field1;
                    fromTypesTree = true;
                }

                envFirstLine = active.Field2;
                envLastLine = active.Field3;
            }

            lexer.SetLine(lineNumber, line, 0, env, typeBuilder);

            // The lexer reports end-of-line on the token that closes the line; the
            // iteration cap is a safety net so a colorizer defect degrades the
            // coloring instead of hanging the request loop.
            var cap = line.Length + 2;
            for (var i = 0; i < cap; i++)
            {
                var info = lexer.GetToken(state);
                state = info.State;
                AddSemanticToken(tokens, info, line, lineNumber, env, coreKeywords);
                if (info.IsEndOfLine)
                    break;
            }
        }

        return new EngineSemanticTokens(tokens, fromTypesTree);
    }

    private static void AddSemanticToken(
        List<EngineSemanticToken> tokens,
        ScanTokenInfo info,
        string line,
        int lineNumber,
        GlobalEnv? env,
        Nemerle.Collections.Set<string> coreKeywords)
    {
        var location = info.Token.Location;
        // Engine columns are 1-based with an exclusive end; the lexer can run the
        // end past the line (skip_to_end), and pending tokens may be empty.
        var start = location.Column - 1;
        var length = location.EndColumn - location.Column;
        if (start < 0 || start >= line.Length || length <= 0)
            return;
        if (start + length > line.Length)
            length = line.Length - start;

        var color = (NemerleScanTokenColor)(int)info.Color;
        var (type, modifiers) = SemanticTokenMapping.Classify(
            color,
            IsMacroKeyword(color, line, start, length, env, coreKeywords));
        if (type == NemerleSemanticTokenType.None)
            return;

        tokens.Add(new EngineSemanticToken(lineNumber - 1, start, length, type, modifiers));
    }

    /// <summary>
    /// True when a keyword token is only a keyword because a syntax macro added it
    /// to this line's <c>GlobalEnv</c> — that is, it is absent from the core
    /// environment that every file gets.  This is the one classification a
    /// TextMate grammar cannot make (the word does not exist until the compiler
    /// loaded the macros named by the file's <c>using</c>s), so it is reported as
    /// a distinct token type.
    /// </summary>
    private static bool IsMacroKeyword(
        NemerleScanTokenColor color,
        string line,
        int start,
        int length,
        GlobalEnv? env,
        Nemerle.Collections.Set<string> coreKeywords)
    {
        if (env is null ||
            (color != NemerleScanTokenColor.Keyword && color != NemerleScanTokenColor.QuotationKeyword))
            return false;

        var word = line.Substring(start, length);
        return env.Keywords.Contains(word) && !coreKeywords.Contains(word);
    }

    /// <summary>
    /// Splits a document into lines, treating CRLF, CR and LF alike (the same
    /// line model as <see cref="InMemoryNemerleSource.LineCount"/>), without
    /// materializing the whole document a second time.
    /// </summary>
    private static IEnumerable<string> SplitLines(string text)
    {
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c is not ('\r' or '\n'))
                continue;

            yield return text[start..i];
            if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
                i++;
            start = i + 1;
        }

        yield return text[start..];
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
    /// <summary>
    /// The engine's delivery point for <c>BeginHighlightUsages</c> (WP-P2).  Runs
    /// on the AsyncWorker thread, immediately after the request was marked
    /// completed, which is why the flattening - including
    /// <c>GotoInfo.FilePath</c>, a read of the compiler's process-wide file-name
    /// list - is done right here rather than handed to the LSP thread.
    ///
    /// <para>At most one highlight request is in flight
    /// (<see cref="GetDocumentHighlightsAsync"/> holds a gate for its whole round
    /// trip), so the mailbox is unambiguous even though the callback carries no
    /// request identity.  A callback with no mailbox installed is the engine
    /// answering a request whose caller already gave up and drained; dropping it
    /// is correct.</para>
    /// </summary>
    public void SetHighlights(IIdeSource source, IEnumerable<GotoInfo> highlights)
    {
        var delivery = _highlightDelivery;
        if (delivery is null || !ReferenceEquals(delivery.Source, source))
            return;

        try
        {
            delivery.Targets = FlattenGotoInfos(highlights);
        }
        catch (Exception ex)
        {
            // A malformed usage list must not take the worker loop down; the
            // request answers "no highlights" (and says so in the Output).
            _log.Warning($"nemerle document highlight failed: {ex.GetType().Name}: {ex.Message}");
            delivery.Targets = [];
        }
    }
    /// <summary>
    /// The engine's answer to <c>BeginFindUnimplementedMembers</c> (WP-P5).
    ///
    /// <para><b>Runs on the response pump, not on the worker</b>
    /// (<c>Engine-FindUnimplementedMembers.n</c> hands it to
    /// <c>AsyncWorker.AddResponse</c>).  So it stores the engine objects and
    /// touches nothing: even enumerating <paramref name="unimplementedMembers"/>
    /// would be engine work, because the grouping's key is a
    /// <c>FixedType.Class</c> and comparing those goes through the type solver.
    /// Reading and generating happens on the worker in
    /// <see cref="RunGenerateMembers"/>.</para>
    /// </summary>
    public void AddUnimplementedMembers(
        IIdeSource source,
        TypeBuilder type,
        IEnumerable<IGrouping<FixedType.Class, IMember>> unimplementedMembers)
    {
        var delivery = _memberSuggestion;
        if (delivery is null || !ReferenceEquals(delivery.Source, source))
            return;

        delivery.Type = type;
        delivery.Unimplemented = unimplementedMembers;
        delivery.Delivered = true;
    }

    /// <summary>
    /// The engine's answer to <c>BeginFindMethodsToOverride</c>.  Nothing asks for
    /// it: WP-P5 ships the implement-members action only, because the engine
    /// returns overrides as one flat list that at a plain class is four
    /// <c>System.Object</c> virtuals (measured), and offering "override 4 members
    /// of Object" on every class declaration is noise.  Doing it properly means
    /// one action per member, with its own fixture - deferred rather than shipped
    /// untested.
    /// </summary>
    public void AddOverrideMembers(IIdeSource source, TypeBuilder type, IEnumerable<IMember> notOverridden) { }

    public void TypesTreeCreated()
    {
        // Consume the start stamp rather than just reading it.  The field records
        // one start at a time, so when several reloads are requested in quick
        // succession - which the editor's watchers do - a plain read makes every
        // completion report its duration from the *latest* start.  A real session's
        // log then shows several rebuilds "finishing" a few tens of ms apart, which
        // reads as overlapping builds and is not what happened.  Zeroing it means
        // each start is reported once, by the first completion after it, and any
        // further completion prints no duration instead of a fabricated one.
        var startedAt = Interlocked.Exchange(ref _reloadStartedTimestamp, 0);
        if (startedAt != 0)
            _log.Log(
                $"nemerle engine rebuild finished after {Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds:F0} ms");
        else
            _log.Log("nemerle engine rebuild finished");
        RaiseDiagnosticsChanged();
        TypesTreeRebuilt?.Invoke();
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
        // _highlightGate is deliberately not disposed: an in-flight highlight
        // request would then fault on Release, and a SemaphoreSlim whose
        // AvailableWaitHandle was never taken holds nothing to release.
    }

    /// <summary>Caller must hold <c>_engineOperations</c>.</summary>
    private void RequestFullReload(bool immediate)
    {
        if (_disposed)
            return;

        // A full reload supersedes any queued incremental updates.
        _pendingFullReload = true;
        _pendingIncrementalSources.Clear();

        if (immediate)
        {
            _reloadTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            FlushPending();
        }
        else
        {
            _reloadTimer.Change(ChangeReloadDebounce, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>Caller must hold <c>_engineOperations</c>.</summary>
    private void RequestIncrementalUpdate(InMemoryNemerleSource source)
    {
        if (_disposed || _pendingFullReload)
            return;

        _pendingIncrementalSources.Add(source);
        _reloadTimer.Change(ChangeReloadDebounce, Timeout.InfiniteTimeSpan);
    }

    private void OnReloadTimer(object? state)
    {
        lock (_engineOperations)
        {
            if (_disposed)
                return;
            FlushPending();
        }
    }

    /// <summary>Caller must hold <c>_engineOperations</c>.</summary>
    private void FlushPending()
    {
        if (_disposed)
            return;

        if (_pendingFullReload)
        {
            _pendingFullReload = false;
            _pendingIncrementalSources.Clear();
            BeginEngineReload();
            return;
        }

        if (_pendingIncrementalSources.Count == 0)
            return;

        var sources = _pendingIncrementalSources.ToArray();
        _pendingIncrementalSources.Clear();

        // Any edit invalidates the cached completion elements (and the members
        // they point at), whether the engine relocates a method or rebuilds the
        // types tree; bump the generation so a pending resolve returns nothing
        // rather than stale documentation.
        BumpCompletionGeneration();
        Interlocked.Exchange(ref _reloadStartedTimestamp, Stopwatch.GetTimestamp());

        var requests = new List<AsyncRequest>(sources.Length);
        foreach (var source in sources)
        {
            _log.Log($"nemerle engine incremental update (relocation) for {source.Path}");
            requests.Add(_engine.BeginUpdateCompileUnit(source));
        }

        _ = MonitorUpdateForRebuildAsync(requests);
    }

    /// <summary>Caller must hold <c>_engineOperations</c>.</summary>
    private void BeginEngineReload()
    {
        Interlocked.Exchange(ref _reloadStartedTimestamp, Stopwatch.GetTimestamp());
        // A reload rebuilds the types tree, so the cached completion elements
        // (and the members they point at) belong to the old generation; bump the
        // generation and drop them so a pending resolve returns nothing rather
        // than stale documentation.
        BumpCompletionGeneration();
        _ = _engine.BeginReloadProject();
    }

    private void BumpCompletionGeneration()
    {
        lock (_gate)
        {
            _completionGeneration++;
            _completionElems = [];
        }
    }

    /// <summary>
    /// Waits (off any lock) for the enqueued <c>BeginUpdateCompileUnit</c>
    /// requests to finish, then drives any pending types-tree rebuild the engine
    /// flagged during them.  <c>ProcessPendingTypesTreeRequest</c> is a no-op when
    /// the edit relocated cleanly (a method-body change never rebuilds the types
    /// tree, WP-M5 acceptance 1); it runs <c>BuildTypesTree</c> only when the
    /// compile unit's structure changed or the relocation failed (acceptance 3/5).
    /// </summary>
    private async Task MonitorUpdateForRebuildAsync(IReadOnlyList<AsyncRequest> requests)
    {
        try
        {
            foreach (var request in requests)
            {
                while (!request.IsCompleted)
                {
                    if (_disposed)
                        return;
                    await Task.Delay(10).ConfigureAwait(false);
                }
            }

            if (_disposed)
                return;

            _engine.ProcessPendingTypesTreeRequest();
        }
        catch (Exception ex)
        {
            if (!_disposed)
                _log.Error($"nemerle incremental rebuild follow-up failed: {ex}");
        }
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
