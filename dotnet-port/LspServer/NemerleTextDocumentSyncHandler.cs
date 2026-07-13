using MediatR;
using Nemerle.Compiler;
using Nemerle.LanguageServer.Engine;
using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using OmniSharp.Extensions.LanguageServer.Protocol.Server;
using OmniSharp.Extensions.LanguageServer.Protocol.Server.Capabilities;

using LspRange = OmniSharp.Extensions.LanguageServer.Protocol.Models.Range;

namespace Nemerle.LanguageServer;

/// <summary>
/// OmniSharp LSP adapter.  All Nemerle-specific state remains in the engine
/// workspace (WorkspaceManager/NemerleProject) so future hover/completion/
/// definition handlers can share the same engine without depending on the
/// document synchronization handler.
/// </summary>
internal sealed class NemerleTextDocumentSyncHandler : TextDocumentSyncHandlerBase
{
    private static readonly TextDocumentSelector Selector =
        TextDocumentSelector.ForLanguage("nemerle");

    private readonly ILanguageServerFacade _server;
    private readonly WorkspaceManager _workspace;
    private readonly object _publishGate = new();
    private readonly Dictionary<string, (int? Version, int PayloadHash)> _published =
        new(StringComparer.OrdinalIgnoreCase);

    public NemerleTextDocumentSyncHandler(ILanguageServerFacade server, WorkspaceManager workspace)
    {
        _server = server;
        _workspace = workspace;
        _workspace.DiagnosticsChanged += PublishDiagnostics;
    }

    public override Task<Unit> Handle(DidOpenTextDocumentParams request, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var document = request.TextDocument;
        _workspace.OpenDocument(
            document.Uri.ToString(),
            document.Uri.GetFileSystemPath(),
            document.Text,
            document.Version ?? 0);
        return Unit.Task;
    }

    public override Task<Unit> Handle(DidChangeTextDocumentParams request, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var changes = request.ContentChanges.ToArray();
        if (changes.Length != 1 || changes[0].Range is not null)
            throw new InvalidOperationException("The server advertised full document synchronization.");

        _workspace.ChangeDocument(
            request.TextDocument.Uri.ToString(),
            changes[0].Text,
            request.TextDocument.Version ?? 0);
        return Unit.Task;
    }

    public override Task<Unit> Handle(DidSaveTextDocumentParams request, CancellationToken token) =>
        Unit.Task;

    public override Task<Unit> Handle(DidCloseTextDocumentParams request, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        _workspace.CloseDocument(request.TextDocument.Uri.ToString());
        return Unit.Task;
    }

    protected override TextDocumentSyncRegistrationOptions CreateRegistrationOptions(
        TextSynchronizationCapability capability,
        ClientCapabilities clientCapabilities) => new()
        {
            DocumentSelector = Selector,
            Change = TextDocumentSyncKind.Full,
            Save = false,
        };

    public override TextDocumentAttributes GetTextDocumentAttributes(DocumentUri uri) =>
        new(uri, "nemerle");

    private void PublishDiagnostics(IReadOnlyList<DocumentDiagnostics> documents)
    {
        lock (_publishGate)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var document in documents)
            {
                seen.Add(document.Uri);
                if (document.Diagnostics is null)
                    continue; // Not analyzed at its current version yet; keep the previous state.

                var payloadHash = ComputePayloadHash(document.Diagnostics);
                if (_published.TryGetValue(document.Uri, out var previous) &&
                    previous.Version == document.Version &&
                    previous.PayloadHash == payloadHash)
                    continue;

                Publish(
                    DocumentUri.From(document.Uri),
                    document.Version,
                    document.Diagnostics.Select(ToLspDiagnostic));
                _published[document.Uri] = (document.Version, payloadHash);
            }

            // Documents that left the workspace (closed loose files, sources
            // removed from the project) get their diagnostics cleared once.
            foreach (var uri in _published.Keys.Where(uri => !seen.Contains(uri)).ToArray())
            {
                Publish(DocumentUri.From(uri), null, []);
                _published.Remove(uri);
            }
        }
    }

    private void Publish(DocumentUri uri, int? version, IEnumerable<Diagnostic> diagnostics)
    {
        _server.TextDocument.PublishDiagnostics(new PublishDiagnosticsParams
        {
            Uri = uri,
            Version = version,
            Diagnostics = new Container<Diagnostic>(diagnostics),
        });
    }

    private static int ComputePayloadHash(IReadOnlyList<EngineDiagnostic> diagnostics)
    {
        var hash = new HashCode();
        foreach (var diagnostic in diagnostics)
        {
            hash.Add(diagnostic.Line);
            hash.Add(diagnostic.Column);
            hash.Add(diagnostic.EndLine);
            hash.Add(diagnostic.EndColumn);
            hash.Add(diagnostic.Kind);
            hash.Add(diagnostic.Message, StringComparer.Ordinal);
        }

        return hash.ToHashCode();
    }

    private static Diagnostic ToLspDiagnostic(EngineDiagnostic diagnostic)
    {
        var startLine = Math.Max(0, diagnostic.Line - 1);
        var startCharacter = Math.Max(0, diagnostic.Column - 1);
        var endLine = diagnostic.EndLine > 0 ? diagnostic.EndLine - 1 : startLine;
        var endCharacter = diagnostic.EndColumn > 0 ? diagnostic.EndColumn - 1 : startCharacter;

        if (endLine < startLine || endLine == startLine && endCharacter < startCharacter)
        {
            endLine = startLine;
            endCharacter = startCharacter;
        }

        return new Diagnostic
        {
            Range = new LspRange(
                new Position(startLine, startCharacter),
                new Position(endLine, endCharacter)),
            Severity = diagnostic.Kind switch
            {
                MessageKind.Error => DiagnosticSeverity.Error,
                MessageKind.Warning => DiagnosticSeverity.Warning,
                _ => DiagnosticSeverity.Hint,
            },
            Source = "nemerle",
            Message = diagnostic.Message,
        };
    }
}
