namespace Nemerle.ProjectInfo;

/// <summary>
/// Mirror of the engine's <c>Nemerle.Completion2.UsageType</c> (name and ordinal
/// value), kept here so this assembly stays free of an engine reference - the
/// same trade-off <see cref="CompletionMapping"/> makes for <c>GlyphType</c> and
/// <see cref="SemanticTokenMapping"/> for <c>ScanTokenColor</c>.
///
/// <para>Measured (WP-P2): today the engine only ever produces
/// <see cref="Definition"/> and <see cref="Usage"/>.  The <c>Generated*</c> and
/// <c>External*</c> members exist in the engine enum but no usage-collection
/// path constructs a <c>GotoInfo</c> with them (the only references are in
/// <c>Nemerle.Completion2/Tests/Heavy.Tests/Runner.n</c>).  They are mapped
/// anyway so a future engine that starts emitting them degrades sensibly instead
/// of silently classifying them as reads.</para>
/// </summary>
public enum NemerleUsageType
{
    /// <summary>The binding occurrence: a declaration, not necessarily a write.</summary>
    Definition = 0,
    Usage = 1,
    GeneratedDefinition = 2,
    GeneratedUsage = 3,
    ExternalDefinition = 4,
    ExternalUsage = 5,
}

/// <summary>
/// One engine goto target, distilled to editor-neutral primitives so the
/// conversion to an LSP location can live here (free of any engine or OmniSharp
/// dependency, like <see cref="HoverMarkup"/> and <see cref="CompletionMapping"/>)
/// and be pinned by unit tests.  Coordinates are the engine's 1-based line/column
/// (UTF-16 code units, matching .NET string indexing).  <see cref="FileIndex"/>
/// is the compiler's source-file index: a positive value marks an in-workspace
/// source with a real on-disk path in <see cref="FilePath"/>; a zero (or negative)
/// value marks a metadata / external-assembly member with no navigable source.
/// </summary>
public sealed record NemerleGotoTarget(
    string? FilePath,
    int FileIndex,
    int Line,
    int Column,
    int EndLine,
    int EndColumn,
    NemerleUsageType UsageType)
{
    /// <summary>
    /// Whether this entry is the symbol's declaration rather than a use of it.
    /// Provenance (plain / generated / external) does not change that, so all
    /// three <c>*Definition</c> members answer true; in practice only
    /// <see cref="NemerleUsageType.Definition"/> is ever produced.
    /// </summary>
    public bool IsDefinition =>
        UsageType is NemerleUsageType.Definition
            or NemerleUsageType.GeneratedDefinition
            or NemerleUsageType.ExternalDefinition;
}

/// <summary>An LSP location: a document URI and a 0-based UTF-16 range.</summary>
public sealed record NemerleGotoLocation(
    string Uri,
    int StartLine,
    int StartCharacter,
    int EndLine,
    int EndCharacter);

/// <summary>
/// LSP <c>DocumentHighlightKind</c>, with the protocol's own numbering so the
/// handler can hand the value straight to the client.
/// </summary>
public enum NemerleDocumentHighlightKind
{
    Text = 1,
    Read = 2,
    Write = 3,
}

/// <summary>
/// One <c>textDocument/documentHighlight</c> entry: a 0-based UTF-16 range in the
/// requested document (the URI is implicit - highlights never leave the file that
/// was asked about) plus its read/write kind.
/// </summary>
public sealed record NemerleDocumentHighlight(
    int StartLine,
    int StartCharacter,
    int EndLine,
    int EndCharacter,
    NemerleDocumentHighlightKind Kind);

/// <summary>
/// Converts the engine's <c>GotoInfo</c> results (as
/// <see cref="NemerleGotoTarget"/>) to LSP <see cref="NemerleGotoLocation"/>s.
/// Pure function, unit-tested; the handler wraps each URI in an OmniSharp
/// <c>DocumentUri</c>.  Only in-workspace source locations become navigable
/// locations, so a definition/references request landing on a metadata / BCL /
/// NuGet member yields an empty result rather than a bogus URI (WP-M4
/// acceptance 4).
/// </summary>
public static class GotoMapping
{
    /// <param name="includeDeclaration">
    /// When false, the declaration entries are dropped (the LSP
    /// <c>references</c> <c>context.includeDeclaration = false</c> case); when
    /// true, declarations are kept alongside usages.
    /// </param>
    public static IReadOnlyList<NemerleGotoLocation> ToLocations(
        IEnumerable<NemerleGotoTarget> targets,
        bool includeDeclaration)
    {
        var result = new List<NemerleGotoLocation>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var target in targets)
        {
            if (!includeDeclaration && target.IsDefinition)
                continue;
            // FileIndex <= 0 is a metadata/external member; an empty path or an
            // absent end position is not a navigable source location either.
            if (target.FileIndex <= 0 ||
                string.IsNullOrEmpty(target.FilePath) ||
                target.Line <= 0 || target.EndLine <= 0)
                continue;

            var uri = ToUri(target.FilePath);
            var (startLine, startCharacter, endLine, endCharacter) = ToRange(target);

            // The engine can report the same declaration/usage more than once
            // (e.g. partial types); collapse exact duplicates.
            var key = $"{uri}|{startLine}|{startCharacter}|{endLine}|{endCharacter}";
            if (seen.Add(key))
                result.Add(new NemerleGotoLocation(uri, startLine, startCharacter, endLine, endCharacter));
        }

        return result;
    }

    /// <summary>
    /// Reduces the same usage collection to the LSP
    /// <c>textDocument/documentHighlight</c> answer for one document: the entries
    /// whose <c>FileIndex</c> is <paramref name="fileIndex"/>, converted to 0-based
    /// UTF-16 ranges and classified read/write.
    ///
    /// <para><b>Why the same collection as references/rename.</b>  Highlighting is
    /// the visible half of a rename: whatever lights up is what a rename will
    /// touch.  Deriving both from one collection makes that true by construction
    /// rather than by two implementations happening to agree
    /// (<c>56-wp-p-plan.md</c> §4 documentHighlight).</para>
    ///
    /// <para><b>Why declarations become <c>Write</c>.</b>  LSP has no
    /// "definition" kind - only <c>Text</c> / <c>Read</c> / <c>Write</c> - and
    /// every mainstream server marks the declaring occurrence <c>Write</c>, which
    /// is what themes style as the distinguished one.  Note that the engine's
    /// <c>Definition</c> means <em>binding occurrence</em>, not assignment: a
    /// later <c>mutable</c> store is reported as <c>Usage</c> and therefore shows
    /// up as <c>Read</c>.  Calling that out rather than pretending otherwise -
    /// distinguishing real writes would need dataflow the engine's usage
    /// collection does not carry.</para>
    ///
    /// <para><c>External*</c> entries are dropped outright: an external-assembly
    /// member has no source in this (or any) workspace file, so it can never be a
    /// highlight in the document being edited.  The <c>FileIndex</c> filter would
    /// drop them anyway; the explicit test says so at the point it matters.</para>
    /// </summary>
    /// <param name="fileIndex">
    /// The compiler's source-file index of the document being highlighted.  A
    /// non-positive value (no such source) yields an empty result.
    /// </param>
    public static IReadOnlyList<NemerleDocumentHighlight> ToDocumentHighlights(
        IEnumerable<NemerleGotoTarget> targets,
        int fileIndex)
    {
        if (fileIndex <= 0)
            return [];

        var result = new List<NemerleDocumentHighlight>();
        var byRange = new Dictionary<(int, int, int, int), int>();

        foreach (var target in targets)
        {
            if (target.UsageType is NemerleUsageType.ExternalDefinition
                or NemerleUsageType.ExternalUsage)
                continue;
            if (target.FileIndex != fileIndex || target.Line <= 0 || target.EndLine <= 0)
                continue;

            var range = ToRange(target);
            var kind = target.IsDefinition
                ? NemerleDocumentHighlightKind.Write
                : NemerleDocumentHighlightKind.Read;

            // Same range reported twice (partial types, or a declaration the
            // engine also lists as a use of itself): keep one entry, and let the
            // declaration's Write win so the declaring occurrence never
            // degrades to Read depending on collection order.
            if (byRange.TryGetValue(range, out var existing))
            {
                if (kind == NemerleDocumentHighlightKind.Write &&
                    result[existing].Kind != NemerleDocumentHighlightKind.Write)
                    result[existing] = result[existing] with { Kind = kind };
                continue;
            }

            byRange.Add(range, result.Count);
            result.Add(new NemerleDocumentHighlight(range.Item1, range.Item2, range.Item3, range.Item4, kind));
        }

        return result;
    }

    /// <summary>
    /// Engine range (1-based, end-exclusive column) to LSP range (0-based UTF-16),
    /// clamped so a missing or inverted end position degrades to an empty range at
    /// the start rather than to a protocol error.
    /// </summary>
    private static (int StartLine, int StartCharacter, int EndLine, int EndCharacter) ToRange(
        NemerleGotoTarget target)
    {
        var startLine = Math.Max(0, target.Line - 1);
        var startCharacter = Math.Max(0, target.Column - 1);
        var endLine = target.EndLine > 0 ? target.EndLine - 1 : startLine;
        var endCharacter = target.EndColumn > 0 ? target.EndColumn - 1 : startCharacter;

        if (endLine < startLine || (endLine == startLine && endCharacter < startCharacter))
        {
            endLine = startLine;
            endCharacter = startCharacter;
        }

        return (startLine, startCharacter, endLine, endCharacter);
    }

    /// <summary>
    /// Builds a <c>file://</c> URI for a source path, normalizing it (uppercased
    /// drive letter, resolved separators) with the shared
    /// <see cref="ProjectPathNormalizer"/> first so the URI matches the one the
    /// diagnostics publisher produces for the same file.
    /// </summary>
    public static string ToUri(string filePath) =>
        new Uri(ProjectPathNormalizer.NormalizeFile(filePath)).AbsoluteUri;
}
