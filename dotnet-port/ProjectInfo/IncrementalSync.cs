namespace Nemerle.ProjectInfo;

/// <summary>
/// One LSP <c>textDocument/didChange</c> content change.  When
/// <see cref="HasRange"/> is true the change replaces the 0-based UTF-16 range
/// <c>[Start, End)</c> with <see cref="Text"/> (incremental sync); when false it
/// is a whole-document replacement and <see cref="Text"/> is the new full text.
/// </summary>
public sealed record NemerleContentChange(
    bool HasRange,
    int StartLine,
    int StartCharacter,
    int EndLine,
    int EndCharacter,
    string Text)
{
    /// <summary>A whole-document replacement (no range).</summary>
    public static NemerleContentChange FullReplace(string text) =>
        new(false, 0, 0, 0, 0, text);

    /// <summary>An incremental range replacement (0-based UTF-16 line/character).</summary>
    public static NemerleContentChange Ranged(
        int startLine, int startCharacter, int endLine, int endCharacter, string text) =>
        new(true, startLine, startCharacter, endLine, endCharacter, text);
}

/// <summary>
/// An engine relocation request in the engine's 1-based line/column coordinates:
/// the edit's start (<c>Begin</c>), the end of the replaced span in the old text
/// (<c>Old</c>), and the end of the inserted span in the new text (<c>New</c>).
/// This is the shape the VS integration produced from a
/// <c>TextLineChange</c> (iStart/iOldEnd/iNewEnd, each +1), reproduced here from
/// an LSP incremental change.  Editor- and engine-neutral so the UTF-16 → 1-based
/// conversion is unit-tested here; <c>NemerleProject</c> maps it to the engine's
/// <c>RelocationRequest</c> (like <see cref="HoverMarkup"/> / <see cref="GotoMapping"/>).
/// </summary>
public sealed record NemerleRelocation(
    int BeginLine,
    int BeginCharacter,
    int OldEndLine,
    int OldEndCharacter,
    int NewEndLine,
    int NewEndCharacter);

/// <summary>
/// Pure helpers for LSP incremental document sync (WP-M5): applying a content
/// change to a text buffer and deriving the engine relocation request from it.
/// Both follow the same UTF-16 code-unit / CRLF-aware line model as
/// <c>InMemoryNemerleSource</c>, and both are unit-tested so the incremental
/// path never diverges the server's buffer from the client's document.
/// </summary>
public static class IncrementalSync
{
    /// <summary>
    /// Applies <paramref name="change"/> to <paramref name="text"/> and returns
    /// the new full text.  A ranged change splices <c>Text</c> over the
    /// <c>[Start, End)</c> UTF-16 span; a full replacement returns <c>Text</c>.
    /// </summary>
    public static string ApplyChange(string text, NemerleContentChange change)
    {
        if (!change.HasRange)
            return change.Text;

        var start = GetOffset(text, change.StartLine, change.StartCharacter);
        var end = GetOffset(text, change.EndLine, change.EndCharacter);
        if (end < start)
            end = start;
        return string.Concat(text.AsSpan(0, start), change.Text, text.AsSpan(end));
    }

    /// <summary>
    /// Derives the engine relocation (1-based Begin/Old/New points) from a ranged
    /// change.  <c>Begin</c> and <c>Old</c> come straight from the LSP range
    /// (start and end, +1); <c>New</c> is the end position the inserted text
    /// reaches from the start (line breaks and the last line's UTF-16 length).
    /// </summary>
    public static NemerleRelocation ComputeRelocation(NemerleContentChange change)
    {
        if (!change.HasRange)
            throw new ArgumentException("A relocation can only be derived from a ranged change.", nameof(change));

        var (newEndLine, newEndCharacter) = ComputeNewEnd(change.StartLine, change.StartCharacter, change.Text);
        return new NemerleRelocation(
            change.StartLine + 1,
            change.StartCharacter + 1,
            change.EndLine + 1,
            change.EndCharacter + 1,
            newEndLine + 1,
            newEndCharacter + 1);
    }

    /// <summary>
    /// The 0-based UTF-16 end position reached by inserting <paramref name="text"/>
    /// at (<paramref name="startLine"/>, <paramref name="startCharacter"/>).
    /// </summary>
    private static (int Line, int Character) ComputeNewEnd(int startLine, int startCharacter, string text)
    {
        var line = startLine;
        var lastLineStart = -1;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '\r')
            {
                if (i + 1 < text.Length && text[i + 1] == '\n')
                    i++;
                line++;
                lastLineStart = i + 1;
            }
            else if (c == '\n')
            {
                line++;
                lastLineStart = i + 1;
            }
        }

        var character = lastLineStart < 0
            ? startCharacter + text.Length
            : text.Length - lastLineStart;
        return (line, character);
    }

    /// <summary>
    /// The UTF-16 offset of a 0-based (line, character) position, CRLF-aware and
    /// clamped so a position at/after end-of-line or end-of-document stays in
    /// range (never splitting a CRLF or crossing into the next line).
    /// </summary>
    private static int GetOffset(string text, int line, int character)
    {
        var offset = 0;
        var currentLine = 0;
        while (currentLine < line && offset < text.Length)
        {
            var c = text[offset];
            if (c == '\r')
            {
                offset++;
                if (offset < text.Length && text[offset] == '\n')
                    offset++;
                currentLine++;
            }
            else if (c == '\n')
            {
                offset++;
                currentLine++;
            }
            else
            {
                offset++;
            }
        }

        var lineEnd = offset;
        while (lineEnd < text.Length && text[lineEnd] is not ('\r' or '\n'))
            lineEnd++;

        var result = offset + Math.Max(0, character);
        return result > lineEnd ? lineEnd : result;
    }
}
