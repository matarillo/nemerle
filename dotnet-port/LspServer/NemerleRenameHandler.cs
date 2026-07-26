using System.Diagnostics;
using Nemerle.LanguageServer.Engine;
using Nemerle.ProjectInfo;
using OmniSharp.Extensions.JsonRpc;
using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;

namespace Nemerle.LanguageServer;

/// <summary>
/// <c>textDocument/rename</c> and <c>textDocument/prepareRename</c> (WP-P3).
///
/// <para>The edits come from the same usage collection as
/// <c>textDocument/references</c> and <c>textDocument/documentHighlight</c>
/// (<see cref="NemerleProject.GetRenameEditsAsync"/>), so what a rename rewrites
/// is what the editor already showed the user.  Every decision - which symbols
/// may be renamed, whether the new name is legal, whether the occurrences still
/// match - lives in the pure <see cref="RenameMapping"/> and is unit-tested
/// there.</para>
///
/// <para><b>Refusals are surfaced, never silent.</b>  <c>prepareRename</c>
/// answering null keeps VS Code from opening its input box at all, which is the
/// right moment to say "not here"; a refusal at <c>rename</c> time is returned as
/// a request error so the editor shows the reason instead of appearing to do
/// nothing.  Both are logged, because the Output pane is the only place a user
/// can see why (WP-O5a §設計-3).</para>
///
/// <para>The extension needs no manifest change: rename is a negotiated server
/// capability, and <c>prepareProvider</c> is advertised from the registration
/// options below.</para>
/// </summary>
internal sealed class NemerleRenameHandler : RenameHandlerBase
{
    internal static readonly TextDocumentSelector Selector =
        TextDocumentSelector.ForLanguage("nemerle");

    private readonly NemerleProject _project;
    private readonly ServerLog _log;

    public NemerleRenameHandler(NemerleProject project, ServerLog log)
    {
        _project = project;
        _log = log;
    }

    public override async Task<WorkspaceEdit?> Handle(RenameParams request, CancellationToken token)
    {
        var uri = request.TextDocument.Uri.ToString();
        var position = request.Position;

        var stopwatch = Stopwatch.StartNew();
        var result = await _project
            .GetRenameEditsAsync(uri, position.Line, position.Character, request.NewName, token)
            .ConfigureAwait(false);

        if (!result.IsUsable || result.Edit is not { } edit)
        {
            var reason = result.Conflict is null
                ? RenameMapping.Describe(result.Refusal)
                : $"{RenameMapping.Describe(result.Refusal)} ({result.Conflict})";
            _log.Log(
                $"nemerle rename refused at {uri} {position.Line}:{position.Character} " +
                $"-> '{request.NewName}': {reason}");

            // A rename the server will not perform must say so: returning an
            // empty edit would look like a successful no-op.
            throw new OmniSharp.Extensions.JsonRpc.RpcErrorException(-32602, null, reason);
        }

        _log.Log(
            $"nemerle rename computed in {stopwatch.Elapsed.TotalMilliseconds:F0} ms: " +
            $"'{result.OldName}' -> '{request.NewName}', {edit.EditCount} edit(s) in " +
            $"{edit.DocumentCount} document(s)");

        var changes = new Dictionary<DocumentUri, IEnumerable<TextEdit>>();
        foreach (var document in edit.Documents)
        {
            changes.Add(
                DocumentUri.Parse(document.Uri),
                document.Edits.Select(ToTextEdit).ToArray());
        }

        return new WorkspaceEdit { Changes = changes };
    }

    protected override RenameRegistrationOptions CreateRegistrationOptions(
        RenameCapability capability,
        ClientCapabilities clientCapabilities) =>
        new()
        {
            DocumentSelector = Selector,
            // The server decides what may be renamed, so the editor must ask
            // before it opens its input box (see the Handle overload above).
            PrepareProvider = true,
        };

    private static TextEdit ToTextEdit(NemerleTextEdit edit) =>
        new()
        {
            Range = new OmniSharp.Extensions.LanguageServer.Protocol.Models.Range(
                new Position(edit.StartLine, edit.StartCharacter),
                new Position(edit.EndLine, edit.EndCharacter)),
            NewText = edit.NewText,
        };
}

/// <summary>
/// <c>textDocument/prepareRename</c> (WP-P3).  Separate from
/// <see cref="NemerleRenameHandler"/> only because OmniSharp models the two
/// requests as two handler bases; the rules are the same ones, asked earlier.
///
/// <para>Answering null here is what keeps VS Code from opening its rename input
/// box at all, which is a better way to say "not this symbol" than accepting a
/// new name and then failing.</para>
/// </summary>
internal sealed class NemerlePrepareRenameHandler : PrepareRenameHandlerBase
{
    private readonly NemerleProject _project;
    private readonly ServerLog _log;

    public NemerlePrepareRenameHandler(NemerleProject project, ServerLog log)
    {
        _project = project;
        _log = log;
    }

    public override Task<RangeOrPlaceholderRange?> Handle(
        PrepareRenameParams request,
        CancellationToken token)
    {
        var uri = request.TextDocument.Uri.ToString();
        var position = request.Position;

        var preparation = _project.PrepareRename(uri, position.Line, position.Character);
        if (!preparation.CanRename || preparation.Range is not { } range)
        {
            _log.Log(
                $"nemerle rename not offered at {uri} {position.Line}:{position.Character}: " +
                RenameMapping.Describe(preparation.Refusal));
            return Task.FromResult<RangeOrPlaceholderRange?>(null);
        }

        return Task.FromResult<RangeOrPlaceholderRange?>(
            new RangeOrPlaceholderRange(
                new OmniSharp.Extensions.LanguageServer.Protocol.Models.Range(
                    new Position(range.StartLine, range.StartCharacter),
                    new Position(range.EndLine, range.EndCharacter))));
    }

    protected override RenameRegistrationOptions CreateRegistrationOptions(
        RenameCapability capability,
        ClientCapabilities clientCapabilities) =>
        new()
        {
            DocumentSelector = NemerleRenameHandler.Selector,
            PrepareProvider = true,
        };
}
