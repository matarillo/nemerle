using System.Diagnostics;
using Nemerle.LanguageServer.Engine;
using Nemerle.ProjectInfo;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;

using LspRange = OmniSharp.Extensions.LanguageServer.Protocol.Models.Range;

namespace Nemerle.LanguageServer;

/// <summary>
/// <c>textDocument/hover</c> handler.  Delegates to the shared engine workspace
/// (<see cref="NemerleProject.GetHoverAsync"/>) through the
/// <see cref="EngineRequestBridge"/>, converts the engine's pseudo-markup hint
/// to an LSP <see cref="MarkupContent"/> (markdown when the client advertised it,
/// otherwise plain text), and maps the target range to 0-based UTF-16 LSP
/// coordinates.  The extension needs no change: it follows the negotiated
/// <c>hoverProvider</c> capability.
/// </summary>
internal sealed class NemerleHoverHandler : HoverHandlerBase
{
    private static readonly TextDocumentSelector Selector =
        TextDocumentSelector.ForLanguage("nemerle");

    private readonly NemerleProject _project;
    private readonly ServerLog _log;
    private bool _preferMarkdown;

    public NemerleHoverHandler(NemerleProject project, ServerLog log)
    {
        _project = project;
        _log = log;
    }

    public override async Task<Hover?> Handle(HoverParams request, CancellationToken token)
    {
        var uri = request.TextDocument.Uri.ToString();
        var position = request.Position;

        var stopwatch = Stopwatch.StartNew();
        var hover = await _project
            .GetHoverAsync(uri, position.Line, position.Character, token)
            .ConfigureAwait(false);
        if (hover is null)
            return null;

        // Warm-hover response time (WP-M2 acceptance criterion 6): surfaced via
        // window/logMessage so the extension Output Channel and integration test
        // can record it without stderr.
        _log.Log(
            $"nemerle hover computed in {stopwatch.Elapsed.TotalMilliseconds:F0} ms at {uri} {position.Line}:{position.Character}");

        var value = _preferMarkdown
            ? HoverMarkup.ToMarkdown(hover.Text)
            : HoverMarkup.ToPlainText(hover.Text);
        if (string.IsNullOrEmpty(value))
            return null;

        return new Hover
        {
            Contents = new MarkedStringsOrMarkupContent(new MarkupContent
            {
                Kind = _preferMarkdown ? MarkupKind.Markdown : MarkupKind.PlainText,
                Value = value,
            }),
            Range = ToLspRange(hover.Range),
        };
    }

    protected override HoverRegistrationOptions CreateRegistrationOptions(
        HoverCapability capability,
        ClientCapabilities clientCapabilities)
    {
        // Negotiated once at registration: prefer markdown only if the client
        // listed it in hover.contentFormat, otherwise fall back to plain text
        // (LSP 3.17 §textDocument/hover).
        _preferMarkdown = capability?.ContentFormat is { } formats &&
                          formats.Any(kind => kind == MarkupKind.Markdown);
        return new HoverRegistrationOptions { DocumentSelector = Selector };
    }

    private static LspRange? ToLspRange(EngineHoverRange? range)
    {
        if (range is null)
            return null;

        var startLine = Math.Max(0, range.Line - 1);
        var startCharacter = Math.Max(0, range.Column - 1);
        var endLine = range.EndLine > 0 ? range.EndLine - 1 : startLine;
        var endCharacter = range.EndColumn > 0 ? range.EndColumn - 1 : startCharacter;

        if (endLine < startLine || (endLine == startLine && endCharacter < startCharacter))
        {
            endLine = startLine;
            endCharacter = startCharacter;
        }

        return new LspRange(
            new Position(startLine, startCharacter),
            new Position(endLine, endCharacter));
    }
}
