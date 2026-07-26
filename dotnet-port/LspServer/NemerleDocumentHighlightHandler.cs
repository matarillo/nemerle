using System.Diagnostics;
using Nemerle.Completion2;
using Nemerle.LanguageServer.Engine;
using Nemerle.ProjectInfo;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;

namespace Nemerle.LanguageServer;

/// <summary>
/// <c>textDocument/documentHighlight</c> handler (WP-P2): every occurrence of the
/// symbol under the caret, inside the current file only.
///
/// <para>Delegates to <see cref="NemerleProject.GetDocumentHighlightsAsync"/>,
/// which runs the engine's usage collection - the same one
/// <c>textDocument/references</c> and (WP-P3) rename use - on the AsyncWorker
/// thread, and converts the result with the pure
/// <see cref="GotoMapping.ToDocumentHighlights"/>.</para>
///
/// <para>The extension needs no manifest change: document highlight is a
/// negotiated server capability that vscode-languageclient wires up on its own,
/// and it has no trigger characters or settings to declare.</para>
/// </summary>
internal sealed class NemerleDocumentHighlightHandler : DocumentHighlightHandlerBase
{
    private static readonly TextDocumentSelector Selector =
        TextDocumentSelector.ForLanguage("nemerle");

    private readonly NemerleProject _project;
    private readonly ServerLog _log;

    public NemerleDocumentHighlightHandler(NemerleProject project, ServerLog log)
    {
        _project = project;
        _log = log;
        WarnOnUsageTypeMirrorDrift(log);
    }

    public override async Task<DocumentHighlightContainer?> Handle(
        DocumentHighlightParams request,
        CancellationToken token)
    {
        var uri = request.TextDocument.Uri.ToString();
        var position = request.Position;

        var stopwatch = Stopwatch.StartNew();
        var highlights = await _project
            .GetDocumentHighlightsAsync(uri, position.Line, position.Character, token)
            .ConfigureAwait(false);

        if (highlights.Count == 0)
        {
            // A silent empty answer is indistinguishable from "the client never
            // asked" in the Output pane, which is the only view a user has of
            // this feature (WP-O5a §設計-3, WP-P1 §設計-4).
            _log.Trace(
                $"nemerle document highlight empty at {uri} {position.Line}:{position.Character} " +
                $"({stopwatch.Elapsed.TotalMilliseconds:F0} ms)");
            return null;
        }

        var writes = highlights.Count(h => h.Kind == NemerleDocumentHighlightKind.Write);
        _log.Trace(
            $"nemerle document highlight computed in {stopwatch.Elapsed.TotalMilliseconds:F0} ms at " +
            $"{uri} {position.Line}:{position.Character} ({highlights.Count} occurrence(s), " +
            $"{writes} write / {highlights.Count - writes} read)");

        return new DocumentHighlightContainer(highlights.Select(ToDocumentHighlight));
    }

    protected override DocumentHighlightRegistrationOptions CreateRegistrationOptions(
        DocumentHighlightCapability capability,
        ClientCapabilities clientCapabilities) =>
        new() { DocumentSelector = Selector };

    private static DocumentHighlight ToDocumentHighlight(NemerleDocumentHighlight highlight) =>
        new()
        {
            Range = new OmniSharp.Extensions.LanguageServer.Protocol.Models.Range(
                new Position(highlight.StartLine, highlight.StartCharacter),
                new Position(highlight.EndLine, highlight.EndCharacter)),
            Kind = (DocumentHighlightKind)(int)highlight.Kind,
        };

    /// <summary>
    /// <see cref="NemerleUsageType"/> mirrors the engine's <c>UsageType</c> so the
    /// read/write classification can live in the engine-free ProjectInfo assembly
    /// (the same trade-off <see cref="CompletionMapping"/> makes for the glyph
    /// enum and <see cref="SemanticTokenMapping"/> for the colorizer's).  The
    /// engine is a shared source that also serves the legacy VS integration, so
    /// check the two still agree and say so once if they drifted: an unmirrored
    /// usage kind would silently be cast to a nonexistent value.
    /// </summary>
    private static void WarnOnUsageTypeMirrorDrift(ServerLog log)
    {
        foreach (var name in Enum.GetNames<UsageType>())
        {
            var engineValue = (int)Enum.Parse<UsageType>(name);
            if (!Enum.TryParse<NemerleUsageType>(name, out var mirrored) ||
                (int)mirrored != engineValue)
            {
                log.Warning(
                    $"nemerle document highlight: engine usage type '{name}' ({engineValue}) is not " +
                    "mirrored by NemerleUsageType; occurrences of that kind will be misclassified.");
            }
        }
    }
}
