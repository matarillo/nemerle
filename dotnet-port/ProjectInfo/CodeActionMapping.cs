using System.Text;

namespace Nemerle.ProjectInfo;

/// <summary>
/// One group of members the engine says are missing from a type: the interface
/// they come from (or the base type, for overrides) and the source the engine's
/// generator produced for them, as written - starting at column 0, one member per
/// block.
/// </summary>
public sealed record NemerleMemberGeneration(string Owner, int MemberCount, string GeneratedSource);

/// <summary>
/// Where generated members go: the insertion point (0-based LSP position, always
/// an empty range) and the text of the line up to it, which decides whether the
/// insertion has to start on a fresh line.
/// </summary>
public sealed record NemerleInsertionPoint(string Uri, int Line, int Character, string LinePrefix);

/// <summary>An offered code action: its title and the single edit it performs.</summary>
public sealed record NemerleCodeAction(string Title, NemerleTextEdit Edit);

/// <summary>
/// Turns the engine's "these members are missing" answer into LSP code actions.
/// Pure and free of any engine or OmniSharp dependency, like
/// <see cref="RenameMapping"/> and <see cref="GotoMapping"/>, so the text
/// assembly - which is what the user ends up with in their file - is pinned by
/// unit tests rather than only by an end-to-end scenario (WP-P5).
///
/// <para><b>Why re-indent here.</b>  The engine's <c>SourceGenerator</c> starts at
/// indent level 0 and its <c>_indentSize</c> is protected, so the generated
/// members always come back flush left.  Inserting them like that into a type
/// body technically compiles and looks broken, so every non-empty line is
/// prefixed with the indentation of the line the insertion lands on.</para>
/// </summary>
public static class CodeActionMapping
{
    /// <summary>
    /// Builds the edit for one group.  The text is re-indented to
    /// <paramref name="indent"/>, opened with a newline when the insertion point
    /// is not already at the start of a line (a one-line <c>class Foo { }</c> is
    /// the case that needs it), and closed with a newline plus the indentation
    /// that was displaced, so the brace that followed stays where it was.
    /// </summary>
    public static NemerleCodeAction? ToCodeAction(
        NemerleMemberGeneration generation,
        NemerleInsertionPoint insertion,
        string indent,
        string titleFormat)
    {
        var body = Reindent(generation.GeneratedSource, indent);
        if (body.Length == 0)
            return null;

        var builder = new StringBuilder();
        // Only whitespace before the insertion point means the caller placed it
        // at the start of a line (typically just before the closing brace), so
        // the members can start right there.
        if (!IsBlank(insertion.LinePrefix))
            builder.Append('\n');

        builder.Append(body);
        if (!body.EndsWith('\n'))
            builder.Append('\n');

        // Put back what stood before the insertion point on that line (the
        // closing brace's own indentation), so it is not left flush left.
        builder.Append(insertion.LinePrefix);

        var edit = new NemerleTextEdit(
            insertion.Uri,
            insertion.Line,
            insertion.Character,
            insertion.Line,
            insertion.Character,
            builder.ToString());

        var title = string.Format(
            titleFormat,
            generation.Owner,
            generation.MemberCount,
            generation.MemberCount == 1 ? "member" : "members");
        return new NemerleCodeAction(title, edit);
    }

    /// <summary>
    /// Re-indents generated source to <paramref name="indent"/>: every non-empty
    /// line keeps its own relative indentation and gains the target prefix, and
    /// line endings are normalized to <c>\n</c> (the client re-applies the
    /// document's own convention when it inserts).
    /// </summary>
    public static string Reindent(string generated, string indent)
    {
        if (string.IsNullOrWhiteSpace(generated))
            return string.Empty;

        var builder = new StringBuilder(generated.Length + 32);
        foreach (var raw in generated.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            var line = raw.TrimEnd();
            if (line.Length == 0)
            {
                // Keep blank lines between members, but without trailing spaces.
                if (builder.Length > 0)
                    builder.Append('\n');
                continue;
            }

            builder.Append(indent).Append(line).Append('\n');
        }

        // Blank lines *between* members are kept; the ones the generator leaves at
        // the end are not, or every action would push the closing brace further
        // down the file.
        while (builder.Length > 0 && builder[^1] == '\n')
            builder.Length--;

        return builder.Length == 0 ? string.Empty : builder.Append('\n').ToString();
    }

    /// <summary>
    /// The indentation to give inserted members: one step deeper than the line
    /// the insertion lands on.  The step matches the surrounding file when the
    /// prefix is made of spaces or tabs; with nothing to go on it falls back to
    /// two spaces, which is what the Nemerle sources in this repository use.
    /// </summary>
    public static string IndentFor(string linePrefix)
    {
        if (linePrefix.Length > 0 && linePrefix.All(c => c == '\t'))
            return linePrefix + "\t";

        var spaces = 0;
        foreach (var c in linePrefix)
        {
            if (c != ' ')
                break;
            spaces++;
        }

        return new string(' ', spaces + 2);
    }

    private static bool IsBlank(string text) => text.All(char.IsWhiteSpace);
}
