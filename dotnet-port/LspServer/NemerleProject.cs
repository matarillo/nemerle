using System.Collections.Concurrent;
using System.Diagnostics;
using Nemerle.Compiler;
using Nemerle.Compiler.Parsetree;
using Nemerle.Compiler.Utils.Async;
using Nemerle.Completion2;
using Nemerle.Completion2.Factories;

namespace Nemerle.LanguageServer.Engine;

internal sealed record EngineDiagnostic(
    string Uri,
    int Version,
    int Line,
    int Column,
    int EndLine,
    int EndColumn,
    MessageKind Kind,
    string Message);

/// <summary>
/// IIdeProject adapter for all documents currently opened by the LSP client.
/// The initial milestone deliberately rebuilds the small in-memory project on
/// every buffer change.  This is slower than the engine's relocation path but
/// guarantees syntax and method-body type diagnostics without touching disk.
/// </summary>
internal sealed class NemerleProject : IIdeProject, IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly object _engineOperations = new();
    private readonly Dictionary<string, InMemoryNemerleSource> _sourcesByUri =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<int, InMemoryNemerleSource> _sourcesByFileIndex = [];
    private readonly Dictionary<int, CompilerMessage[]> _parseMessages = [];
    private readonly Dictionary<MemberBuilder, CompilerMessage[]> _methodMessages =
        new(ReferenceEqualityComparer.Instance);
    private CompilerMessage[] _topLevelMessages = [];
    private readonly CancellationTokenSource _pumpCancellation = new();
    private readonly Task _responsePump;
    private readonly IIdeEngine _engine;

    public NemerleProject(TextWriter engineLog)
    {
        _engine = EngineFactory.Create(this, engineLog, false);
        _responsePump = Task.Run(PumpResponsesAsync);
    }

    public event Action<IReadOnlyList<EngineDiagnostic>>? DiagnosticsChanged;
    public void Open(string uri, string path, string text, int version)
    {
        lock (_engineOperations)
        {
            lock (_gate)
            {
                if (_sourcesByUri.TryGetValue(uri, out var existing))
                {
                    existing.Update(text, version);
                }
                else
                {
                    var source = new InMemoryNemerleSource(uri, path, text, version);
                    _sourcesByUri.Add(uri, source);
                    _sourcesByFileIndex[source.FileIndex] = source;
                }
            }

            _ = _engine.BeginReloadProject();
        }
    }

    public void Change(string uri, string text, int version)
    {
        lock (_engineOperations)
        {
            lock (_gate)
            {
                if (!_sourcesByUri.TryGetValue(uri, out var source))
                    throw new InvalidOperationException($"didChange received for unopened document: {uri}");
                source.Update(text, version);
            }

            _ = _engine.BeginReloadProject();
        }
    }

    public void Close(string uri)
    {
        lock (_engineOperations)
        {
            InMemoryNemerleSource? source;
            lock (_gate)
            {
                if (!_sourcesByUri.Remove(uri, out source))
                    return;

                _sourcesByFileIndex.Remove(source.FileIndex);
                _parseMessages.Remove(source.FileIndex);

                foreach (var member in _methodMessages.Keys
                             .Where(m => m.Location.FileIndex == source.FileIndex)
                             .ToArray())
                    _methodMessages.Remove(member);
            }

            _engine.NotifySourceDeleted(source.FileIndex);
            _ = _engine.BeginReloadProject();
        }
    }

    public IReadOnlyList<(string Uri, int Version)> GetOpenDocuments()
    {
        lock (_gate)
            return _sourcesByUri.Values
                .Select(static source => (source.Uri, source.CurrentVersion))
                .ToArray();
    }

    public IEnumerable<string> GetAssemblyReferences() => [];
    public IEnumerable<string> GetMacroAssemblyReferences() => [];

    public CompilationOptions GetOptions()
    {
        var options = new CompilationOptions
        {
            GreedyReferences = false,
            ColorMessages = false,
            IgnoreConfusion = true,
        };
        options.DefineConstant("DEBUG");
        options.DefineConstant("TRACE");
        return options;
    }

    public IIdeSource? GetSource(int fileIndex)
    {
        lock (_gate) return _sourcesByFileIndex.GetValueOrDefault(fileIndex);
    }

    public IEnumerable<IIdeSource> GetSources()
    {
        lock (_gate) return _sourcesByUri.Values.Cast<IIdeSource>().ToArray();
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
            if (_sourcesByFileIndex.TryGetValue(compileUnit.FileIndex, out var source) &&
                compileUnit.SourceVersion == source.CurrentVersion)
                _parseMessages[compileUnit.FileIndex] = compileUnit.ParseCompilerMessages.ToArray();
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
    public void ShowMessage(string message, MessageType messageType) =>
        Console.Error.WriteLine($"nemerle engine {messageType}: {message}");
    public GotoInfo[] LookupLocationsFromDebugInformation(GotoInfo info) => [];
    public void SetHighlights(IIdeSource source, IEnumerable<GotoInfo> highlights) { }
    public void AddUnimplementedMembers(
        IIdeSource source,
        TypeBuilder type,
        IEnumerable<IGrouping<FixedType.Class, IMember>> unimplementedMembers) { }
    public void AddOverrideMembers(IIdeSource source, TypeBuilder type, IEnumerable<IMember> notOverridden) { }
    public void TypesTreeCreated() => RaiseDiagnosticsChanged();

    public async ValueTask DisposeAsync()
    {
        _engine.Close();
        _pumpCancellation.Cancel();
        try { await _responsePump.ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        _pumpCancellation.Dispose();
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
                Console.Error.WriteLine($"nemerle response callback failed: {ex}");
            }

            await Task.Delay(10, _pumpCancellation.Token).ConfigureAwait(false);
        }
    }

    private void RaiseDiagnosticsChanged()
    {
        List<EngineDiagnostic> result;
        lock (_gate)
        {
            var messages = _topLevelMessages
                .Concat(_parseMessages.Values.SelectMany(static m => m))
                .Concat(_methodMessages.Values.SelectMany(static m => m))
                .Distinct(CompilerMessageComparer.Instance);

            result = [];
            foreach (var message in messages)
            {
                var location = message.Location;
                if (!_sourcesByFileIndex.TryGetValue(location.FileIndex, out var source))
                    continue;

                result.Add(new EngineDiagnostic(
                    source.Uri,
                    source.CurrentVersion,
                    location.Line,
                    location.Column,
                    location.EndLine,
                    location.EndColumn,
                    message.Kind,
                    message.Msg ?? string.Empty));
            }
        }

        DiagnosticsChanged?.Invoke(result);
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
