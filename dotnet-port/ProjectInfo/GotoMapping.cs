namespace Nemerle.ProjectInfo;

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
    bool IsDefinition);

/// <summary>An LSP location: a document URI and a 0-based UTF-16 range.</summary>
public sealed record NemerleGotoLocation(
    string Uri,
    int StartLine,
    int StartCharacter,
    int EndLine,
    int EndCharacter);

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
            var startLine = Math.Max(0, target.Line - 1);
            var startCharacter = Math.Max(0, target.Column - 1);
            var endLine = target.EndLine > 0 ? target.EndLine - 1 : startLine;
            var endCharacter = target.EndColumn > 0 ? target.EndColumn - 1 : startCharacter;

            if (endLine < startLine || (endLine == startLine && endCharacter < startCharacter))
            {
                endLine = startLine;
                endCharacter = startCharacter;
            }

            // The engine can report the same declaration/usage more than once
            // (e.g. partial types); collapse exact duplicates.
            var key = $"{uri}|{startLine}|{startCharacter}|{endLine}|{endCharacter}";
            if (seen.Add(key))
                result.Add(new NemerleGotoLocation(uri, startLine, startCharacter, endLine, endCharacter));
        }

        return result;
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
