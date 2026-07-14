using Nemerle.LanguageServer.Engine;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;

namespace Nemerle.LanguageServer;

/// <summary>
/// <c>textDocument/references</c> handler.  Delegates to the shared engine
/// workspace (<see cref="NemerleProject.GetReferences"/>), which runs the
/// synchronous <c>GetGotoInfo(..., GotoKind.Usages)</c> engine API under the
/// engine-serialization lock.  <c>context.includeDeclaration</c> selects whether
/// the declaration entries are kept alongside the usages.
/// </summary>
internal sealed class NemerleReferencesHandler : ReferencesHandlerBase
{
    private static readonly TextDocumentSelector Selector =
        TextDocumentSelector.ForLanguage("nemerle");

    private readonly NemerleProject _project;

    public NemerleReferencesHandler(NemerleProject project)
    {
        _project = project;
    }

    public override Task<LocationContainer?> Handle(ReferenceParams request, CancellationToken token)
    {
        var uri = request.TextDocument.Uri.ToString();
        var position = request.Position;
        var includeDeclaration = request.Context?.IncludeDeclaration ?? true;

        var result = _project.GetReferences(uri, position.Line, position.Character, includeDeclaration);
        if (result.Locations.Count == 0)
            return Task.FromResult<LocationContainer?>(new LocationContainer());

        var locations = result.Locations.Select(NemerleDefinitionHandler.ToLocation);
        return Task.FromResult<LocationContainer?>(new LocationContainer(locations));
    }

    protected override ReferenceRegistrationOptions CreateRegistrationOptions(
        ReferenceCapability capability,
        ClientCapabilities clientCapabilities) =>
        new() { DocumentSelector = Selector };
}
