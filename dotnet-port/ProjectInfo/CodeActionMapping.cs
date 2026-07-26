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
        var onOwnLine = IsBlank(insertion.LinePrefix);
        // Where the edit actually goes.  When the insertion point is preceded only
        // by whitespace - the usual case, a closing brace on its own line - the
        // edit starts at column 0 and re-emits that whitespace after the members.
        // Inserting *at* the caret instead would add the line's indentation on top
        // of the members' own, which put the first member two columns deeper than
        // the rest (found by a hands-on check of WP-P5; the unit tests only
        // covered a brace at column 0, where the two are the same thing).
        var character = onOwnLine ? 0 : insertion.Character;

        if (!onOwnLine)
        {
            // A one-line `class Foo { }`: break after the code that is already
            // there, and let what follows the caret continue on its own line.
            builder.Append('\n');
        }

        builder.Append(body);
        if (!body.EndsWith('\n'))
            builder.Append('\n');

        // Nothing is re-emitted in the common case: the edit went in at column 0,
        // so the original line - its indentation and its closing brace - simply
        // follows the inserted members untouched.  Only a mid-line insertion has
        // to hand back the line's indentation, because there the caret is already
        // past it.
        if (!onOwnLine)
            builder.Append(LeadingWhitespace(insertion.LinePrefix));

        var edit = new NemerleTextEdit(
            insertion.Uri,
            insertion.Line,
            character,
            insertion.Line,
            character,
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
    /// line keeps its own relative nesting and gains the target prefix, and line
    /// endings are normalized to <c>\n</c> (the client re-applies the document's
    /// own convention when it inserts).
    ///
    /// <para><b>The engine's own nesting is re-expressed in the target's unit.</b>
    /// <c>SourceGenerator</c> emits one <em>tab</em> per level inside a member it
    /// generates (a property's <c>get</c>, a method body).  Prefixing that with
    /// spaces produced lines like <c>"    " + "\t"</c>: mixed indentation whose
    /// depth then depends on the reader's tab width, which is what a hands-on
    /// check of WP-P5 rejected.  Each leading tab is therefore replaced by one
    /// indentation step of the same kind as <paramref name="indent"/> - so a
    /// space-indented file gets spaces, and a tab-indented one keeps tabs.</para>
    /// </summary>
    public static string Reindent(string generated, string indent)
    {
        if (string.IsNullOrWhiteSpace(generated))
            return string.Empty;

        // One nesting step, in the same currency as the insertion point's own
        // indentation.  Two spaces is the step IndentFor uses when it has no tabs
        // to go on, and is what the Nemerle sources in this repository use.
        var step = indent.Length > 0 && indent.All(c => c == '\t') ? "\t" : "  ";

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

            builder.Append(indent);

            var content = 0;
            while (content < line.Length && (line[content] == ' ' || line[content] == '\t'))
            {
                builder.Append(line[content] == '\t' ? step : " ");
                content++;
            }

            builder.Append(line, content, line.Length - content).Append('\n');
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

    private static string LeadingWhitespace(string text)
    {
        var i = 0;
        while (i < text.Length && (text[i] == ' ' || text[i] == '\t'))
            i++;
        return text[..i];
    }
}
