namespace Nemerle.ProjectInfo;

/// <summary>
/// One edit as the engine's formatter reports it (<c>FormatterResult</c>):
/// 1-based line/column, end-exclusive, and the text that replaces the span.  An
/// empty span is an insertion; an empty replacement is a deletion.
/// </summary>
public sealed record NemerleFormatterResult(
    int StartLine,
    int StartColumn,
    int EndLine,
    int EndColumn,
    string ReplacementString);

/// <summary>
/// Converts the engine formatter's results to LSP text edits.  Pure and
/// engine-free like the other mappings, so the coordinate conversion and the
/// filtering rules are unit-tested rather than only exercised end to end.
///
/// <para>The formatter is the VS2010-era <c>CodeIndentationStage2</c>; WP-P4
/// evaluates whether its output is fit to offer at all.  The conversion is kept
/// separate from that judgement so the evidence can be gathered with the same
/// code that would ship.</para>
/// </summary>
public static class FormattingMapping
{
    /// <summary>
    /// Converts and orders the formatter's results, dropping the ones that would
    /// not change anything and refusing the whole set if two of them overlap
    /// (LSP applies a document's edits against its original text).
    /// </summary>
    /// <param name="currentText">
    /// The document's current text, used to drop no-op edits: the formatter
    /// re-emits a line's indentation even when it already matches, and a client
    /// that receives those marks the file dirty for nothing.
    /// </param>
    public static NemerleWorkspaceEditResult ToTextEdits(
        string uri,
        IEnumerable<NemerleFormatterResult> results,
        string currentText)
    {
        var lines = SplitLines(currentText);
        var edits = new List<NemerleTextEdit>();

        foreach (var result in results)
        {
            if (result.StartLine <= 0 || result.EndLine <= 0)
                continue;

            var startLine = result.StartLine - 1;
            var startCharacter = Math.Max(0, result.StartColumn - 1);
            var endLine = result.EndLine - 1;
            var endCharacter = Math.Max(0, result.EndColumn - 1);
            if (endLine < startLine || (endLine == startLine && endCharacter < startCharacter))
                continue;

            var replacement = result.ReplacementString ?? string.Empty;
            if (IsNoOp(lines, startLine, startCharacter, endLine, endCharacter, replacement))
                continue;

            edits.Add(new NemerleTextEdit(uri, startLine, startCharacter, endLine, endCharacter, replacement));
        }

        return WorkspaceEditMapping.Build(edits);
    }

    /// <summary>True when the span already holds exactly the replacement text.</summary>
    private static bool IsNoOp(
        IReadOnlyList<string> lines,
        int startLine,
        int startCharacter,
        int endLine,
        int endCharacter,
        string replacement)
    {
        if (startLine != endLine)
            return false;
        if (startLine >= lines.Count)
            return false;

        var line = lines[startLine];
        if (startCharacter > line.Length || endCharacter > line.Length)
            return false;

        return string.CompareOrdinal(line, startCharacter, replacement, 0, endCharacter - startCharacter) == 0 &&
            endCharacter - startCharacter == replacement.Length;
    }

    /// <summary>
    /// Applies edits to a document exactly as a conforming client would: against
    /// the original text, last edit first so earlier offsets stay valid.  Used by
    /// the WP-P4 evaluation (and by tests) to see what the user would actually
    /// get.
    /// </summary>
    public static string Apply(string text, IReadOnlyList<NemerleTextEdit> edits)
    {
        var lines = new List<string>(SplitLines(text));
        foreach (var edit in edits.OrderByDescending(e => e.StartLine).ThenByDescending(e => e.StartCharacter))
        {
            if (edit.StartLine >= lines.Count || edit.EndLine >= lines.Count)
                continue;

            var start = lines[edit.StartLine];
            var end = lines[edit.EndLine];
            var startCharacter = Math.Min(edit.StartCharacter, start.Length);
            var endCharacter = Math.Min(edit.EndCharacter, end.Length);

            var replaced = start[..startCharacter] + edit.NewText + end[endCharacter..];
            lines.RemoveRange(edit.StartLine, edit.EndLine - edit.StartLine + 1);
            lines.InsertRange(edit.StartLine, replaced.Split('\n'));
        }

        return string.Join("\n", lines);
    }

    private static IReadOnlyList<string> SplitLines(string text) =>
        text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
}
