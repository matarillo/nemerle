using Nemerle.LanguageServer.Engine;
using Nemerle.ProjectInfo;
using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;

using LspRange = OmniSharp.Extensions.LanguageServer.Protocol.Models.Range;

namespace Nemerle.LanguageServer;

/// <summary>
/// <c>textDocument/definition</c> handler.  Delegates to the shared engine
/// workspace (<see cref="NemerleProject.GetDefinition"/>), which runs the
/// synchronous <c>GetGotoInfo</c> engine API under the engine-serialization lock,
/// then converts each in-workspace <c>GotoInfo</c> to an LSP location
/// (<see cref="GotoMapping"/>).  A symbol resolving only to a metadata / external
/// member yields an empty result and an Info log (WP-M4 acceptance 4); source
/// generation for external members (VS2010's <c>GenerateCode</c>) is a non-goal.
/// </summary>
internal sealed class NemerleDefinitionHandler : DefinitionHandlerBase
{
    private static readonly TextDocumentSelector Selector =
        TextDocumentSelector.ForLanguage("nemerle");

    private readonly NemerleProject _project;
    private readonly ServerLog _log;

    public NemerleDefinitionHandler(NemerleProject project, ServerLog log)
    {
        _project = project;
        _log = log;
    }

    public override Task<LocationOrLocationLinks?> Handle(DefinitionParams request, CancellationToken token)
    {
        var uri = request.TextDocument.Uri.ToString();
        var position = request.Position;

        var result = _project.GetDefinition(uri, position.Line, position.Character);
        if (result.Locations.Count == 0)
        {
            if (result.ExternalOnly)
                _log.Info(
                    $"nemerle definition resolved to a metadata (external assembly) member with no source location at {uri} {position.Line}:{position.Character}");
            return Task.FromResult<LocationOrLocationLinks?>(new LocationOrLocationLinks());
        }

        var links = result.Locations.Select(location =>
            (LocationOrLocationLink)ToLocation(location));
        return Task.FromResult<LocationOrLocationLinks?>(new LocationOrLocationLinks(links));
    }

    protected override DefinitionRegistrationOptions CreateRegistrationOptions(
        DefinitionCapability capability,
        ClientCapabilities clientCapabilities) =>
        new() { DocumentSelector = Selector };

    internal static Location ToLocation(NemerleGotoLocation location) => new()
    {
        Uri = DocumentUri.From(location.Uri),
        Range = new LspRange(
            new Position(location.StartLine, location.StartCharacter),
            new Position(location.EndLine, location.EndCharacter)),
    };
}
