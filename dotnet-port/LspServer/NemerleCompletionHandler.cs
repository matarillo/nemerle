using Newtonsoft.Json.Linq;
using Nemerle.LanguageServer.Engine;
using Nemerle.ProjectInfo;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;

namespace Nemerle.LanguageServer;

/// <summary>
/// <c>textDocument/completion</c> (+ <c>completionItem/resolve</c>) handler.
/// Delegates to the shared engine workspace through the
/// <see cref="EngineRequestBridge"/> (reused from WP-M2) and maps each engine
/// <c>CompletionElem</c> to an LSP <see cref="CompletionItem"/> with an
/// editor-neutral kind (<see cref="CompletionMapping"/>).  The heavy
/// documentation (overload list / XmlDoc summary) is not sent up front: the item
/// carries a <c>{gen, index}</c> data token and the documentation is filled in on
/// resolve, so a large member list stays cheap to produce (WP-M3 acceptance 4).
/// </summary>
internal sealed class NemerleCompletionHandler : CompletionHandlerBase
{
    private static readonly TextDocumentSelector Selector =
        TextDocumentSelector.ForLanguage("nemerle");

    private readonly NemerleProject _project;

    public NemerleCompletionHandler(NemerleProject project)
    {
        _project = project;
    }

    public override async Task<CompletionList> Handle(CompletionParams request, CancellationToken token)
    {
        var uri = request.TextDocument.Uri.ToString();
        var position = request.Position;

        var result = await _project
            .GetCompletionAsync(uri, position.Line, position.Character, token)
            .ConfigureAwait(false);
        if (result is null)
            return new CompletionList(isIncomplete: false);

        var items = result.Items.Select(item => new CompletionItem
        {
            Label = item.Label,
            Kind = ToLspKind(item.Kind),
            Detail = item.Detail,
            // completionItem/resolve echoes Data back; it identifies which cached
            // element to expand into documentation.
            Data = new JObject
            {
                ["gen"] = item.Generation,
                ["index"] = item.Index,
            },
        });

        // The list is complete for this position: the client filters it as the
        // user keeps typing without re-querying (LSP isIncomplete = false).
        return new CompletionList(items, isIncomplete: false);
    }

    public override Task<CompletionItem> Handle(CompletionItem request, CancellationToken token)
    {
        if (request.Data is not { Type: JTokenType.Object } data ||
            data["gen"]?.Value<long>() is not { } generation ||
            data["index"]?.Value<int>() is not { } index)
            return Task.FromResult(request);

        var description = _project.ResolveCompletionDescription(generation, index);
        if (string.IsNullOrEmpty(description))
            return Task.FromResult(request);

        // Plain text keeps the (already markup-stripped) documentation literal;
        // it may contain signatures and XmlDoc prose that must not be reparsed as
        // markdown.
        return Task.FromResult(request with
        {
            Documentation = new StringOrMarkupContent(new MarkupContent
            {
                Kind = MarkupKind.PlainText,
                Value = description,
            }),
        });
    }

    protected override CompletionRegistrationOptions CreateRegistrationOptions(
        CompletionCapability capability,
        ClientCapabilities clientCapabilities)
    {
        return new CompletionRegistrationOptions
        {
            DocumentSelector = Selector,
            // '.' re-triggers member completion after a receiver; the client also
            // triggers on identifier characters on its own.
            TriggerCharacters = new Container<string>("."),
            ResolveProvider = true,
        };
    }

    private static CompletionItemKind ToLspKind(NemerleCompletionKind kind) => kind switch
    {
        NemerleCompletionKind.Class => CompletionItemKind.Class,
        NemerleCompletionKind.Interface => CompletionItemKind.Interface,
        NemerleCompletionKind.Enum => CompletionItemKind.Enum,
        NemerleCompletionKind.EnumMember => CompletionItemKind.EnumMember,
        NemerleCompletionKind.Struct => CompletionItemKind.Struct,
        NemerleCompletionKind.Field => CompletionItemKind.Field,
        NemerleCompletionKind.Property => CompletionItemKind.Property,
        NemerleCompletionKind.Method => CompletionItemKind.Method,
        NemerleCompletionKind.Function => CompletionItemKind.Function,
        NemerleCompletionKind.Event => CompletionItemKind.Event,
        NemerleCompletionKind.Constant => CompletionItemKind.Constant,
        NemerleCompletionKind.Variable => CompletionItemKind.Variable,
        NemerleCompletionKind.Module => CompletionItemKind.Module,
        NemerleCompletionKind.Keyword => CompletionItemKind.Keyword,
        _ => CompletionItemKind.Text,
    };
}
