using System.Diagnostics;
using Nemerle.LanguageServer.Engine;
using Nemerle.ProjectInfo;
using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;

namespace Nemerle.LanguageServer;

/// <summary>
/// <c>textDocument/codeAction</c> (WP-P5): the two actions the IDE engine can
/// already produce - implement an interface's missing members, and override
/// inherited ones.  The generated source is the engine's own
/// (<c>InterfaceMemberImplSourceGenerator</c> through the public
/// <c>Utils.GenerateMemberImplementation</c>, which is what the legacy VS
/// "implement members" dialog uses), so what appears in the file is what the
/// Nemerle toolchain has always produced for this, not a rendering invented here.
///
/// <para>The edit is assembled by the same
/// <see cref="WorkspaceEditMapping"/> rename uses, which is why insertions at one
/// point are first-class there: several missing interfaces produce several
/// actions, each inserting at the same place.</para>
///
/// <para>The extension needs no manifest change: code actions are a negotiated
/// server capability.</para>
/// </summary>
internal sealed class NemerleCodeActionHandler : CodeActionHandlerBase
{
    private static readonly TextDocumentSelector Selector =
        TextDocumentSelector.ForLanguage("nemerle");

    private readonly NemerleProject _project;
    private readonly ServerLog _log;

    public NemerleCodeActionHandler(NemerleProject project, ServerLog log)
    {
        _project = project;
        _log = log;
    }

    public override async Task<CommandOrCodeActionContainer?> Handle(
        CodeActionParams request,
        CancellationToken token)
    {
        var uri = request.TextDocument.Uri.ToString();
        // The caret, or the start of the selection: the engine resolves the type
        // that encloses it.
        var position = request.Range.Start;

        var stopwatch = Stopwatch.StartNew();
        var actions = await _project
            .GetCodeActionsAsync(uri, position.Line, position.Character, token)
            .ConfigureAwait(false);

        if (actions.Count == 0)
        {
            _log.Trace(
                $"nemerle code actions: none at {uri} {position.Line}:{position.Character} " +
                $"({stopwatch.Elapsed.TotalMilliseconds:F0} ms)");
            return new CommandOrCodeActionContainer();
        }

        _log.Trace(
            $"nemerle code actions computed in {stopwatch.Elapsed.TotalMilliseconds:F0} ms at " +
            $"{uri} {position.Line}:{position.Character}: " +
            string.Join(", ", actions.Select(action => $"'{action.Title}'")));

        return new CommandOrCodeActionContainer(actions.Select(ToCodeAction));
    }

    /// <summary>
    /// Nothing is deferred: each action already carries its edit, so resolve is
    /// the identity.  (The base class requires it.)
    /// </summary>
    public override Task<CodeAction> Handle(CodeAction request, CancellationToken token) =>
        Task.FromResult(request);

    protected override CodeActionRegistrationOptions CreateRegistrationOptions(
        CodeActionCapability capability,
        ClientCapabilities clientCapabilities) =>
        new()
        {
            DocumentSelector = Selector,
            // Both actions fix a type that does not compile yet (or is about to
            // not compile), so they belong on the quick-fix lightbulb rather than
            // in the refactor submenu.
            CodeActionKinds = new Container<CodeActionKind>(CodeActionKind.QuickFix),
            ResolveProvider = false,
        };

    private static CommandOrCodeAction ToCodeAction(NemerleCodeAction action)
    {
        var edit = action.Edit;
        var changes = new Dictionary<DocumentUri, IEnumerable<TextEdit>>
        {
            [DocumentUri.Parse(edit.Uri)] =
            [
                new TextEdit
                {
                    Range = new OmniSharp.Extensions.LanguageServer.Protocol.Models.Range(
                        new Position(edit.StartLine, edit.StartCharacter),
                        new Position(edit.EndLine, edit.EndCharacter)),
                    NewText = edit.NewText,
                },
            ],
        };

        return new CodeAction
        {
            Title = action.Title,
            Kind = CodeActionKind.QuickFix,
            Edit = new WorkspaceEdit { Changes = changes },
        };
    }
}
