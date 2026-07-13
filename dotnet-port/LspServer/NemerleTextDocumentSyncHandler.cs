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
/// OmniSharp LSP adapter.  All Nemerle-specific state remains in
/// NemerleProject so future hover/completion/definition handlers can share the
/// same engine without depending on the document synchronization handler.
/// </summary>
internal sealed class NemerleTextDocumentSyncHandler : TextDocumentSyncHandlerBase
{
    private static readonly TextDocumentSelector Selector =
        TextDocumentSelector.ForLanguage("nemerle");

    private readonly ILanguageServerFacade _server;
    private readonly NemerleProject _project;

    public NemerleTextDocumentSyncHandler(ILanguageServerFacade server, NemerleProject project)
    {
        _server = server;
        _project = project;
        _project.DiagnosticsChanged += PublishDiagnostics;
    }

    public override Task<Unit> Handle(DidOpenTextDocumentParams request, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var document = request.TextDocument;
        _project.Open(
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

        _project.Change(
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
        _project.Close(request.TextDocument.Uri.ToString());
        Publish(request.TextDocument.Uri, null, []);
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

    private void PublishDiagnostics(IReadOnlyList<EngineDiagnostic> diagnostics)
    {
        var byDocument = diagnostics.ToLookup(static d => d.Uri, StringComparer.OrdinalIgnoreCase);
        foreach (var document in _project.GetOpenDocuments())
        {
            Publish(
                DocumentUri.From(document.Uri),
                document.Version,
                byDocument[document.Uri].Select(ToLspDiagnostic));
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
