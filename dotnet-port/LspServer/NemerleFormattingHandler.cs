using System.Diagnostics;
using Nemerle.LanguageServer.Engine;
using Nemerle.ProjectInfo;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;

namespace Nemerle.LanguageServer;

/// <summary>
/// <c>textDocument/formatting</c> (WP-P4).  Delegates to the IDE engine's own
/// formatter (<c>Nemerle.Completion2.CodeFormatting</c>, whose only active stage
/// is the VS2010-era <c>CodeIndentationStage2</c> - <c>CodeLineBreakingStage</c>
/// is commented out inside <c>Formatter</c> itself), so this reindents; it does
/// not reflow.
///
/// <para>Edits are filtered and ordered by the pure
/// <see cref="FormattingMapping"/>: results that would replace a span with the
/// text already in it are dropped, because a client that receives them marks the
/// file dirty for nothing.</para>
/// </summary>
internal sealed class NemerleFormattingHandler : DocumentFormattingHandlerBase
{
    private static readonly TextDocumentSelector Selector =
        TextDocumentSelector.ForLanguage("nemerle");

    private readonly NemerleProject _project;
    private readonly ServerLog _log;

    public NemerleFormattingHandler(NemerleProject project, ServerLog log)
    {
        _project = project;
        _log = log;
    }

    public override async Task<TextEditContainer?> Handle(
        DocumentFormattingParams request,
        CancellationToken token)
    {
        var uri = request.TextDocument.Uri.ToString();
        var tabSize = (int)request.Options.TabSize;
        var insertTabs = !request.Options.InsertSpaces;

        var stopwatch = Stopwatch.StartNew();
        var results = await _project
            .GetFormattingResultsAsync(uri, insertTabs, tabSize, tabSize, token)
            .ConfigureAwait(false);

        if (results is null)
            return null;

        var text = _project.GetDocumentText(uri);
        if (text is null)
            return null;

        var edit = FormattingMapping.ToTextEdits(uri, results, text);
        if (!edit.IsUsable || edit.Edit is null)
        {
            _log.Warning(
                $"nemerle formatting produced edits that cannot be applied together: {edit.Conflict}");
            return null;
        }

        var edits = edit.Edit.Documents.Count == 0 ? [] : edit.Edit.Documents[0].Edits;
        _log.Log(
            $"nemerle formatting computed in {stopwatch.Elapsed.TotalMilliseconds:F0} ms for {uri}: " +
            $"{edits.Count} edit(s) from {results.Count} formatter result(s)");

        return edits.Count == 0
            ? new TextEditContainer()
            : new TextEditContainer(edits.Select(ToTextEdit));
    }

    protected override DocumentFormattingRegistrationOptions CreateRegistrationOptions(
        DocumentFormattingCapability capability,
        ClientCapabilities clientCapabilities) =>
        new() { DocumentSelector = Selector };

    private static TextEdit ToTextEdit(NemerleTextEdit edit) =>
        new()
        {
            Range = new OmniSharp.Extensions.LanguageServer.Protocol.Models.Range(
                new Position(edit.StartLine, edit.StartCharacter),
                new Position(edit.EndLine, edit.EndCharacter)),
            NewText = edit.NewText,
        };
}
