namespace Nemerle.ProjectInfo;

/// <summary>
/// One LSP <c>TextEdit</c>, addressed to a document: a 0-based UTF-16 range and
/// the text that replaces it.  An empty range (start == end) is an insertion,
/// which is how WP-P5 (codeAction) will express a generated member.
/// </summary>
public sealed record NemerleTextEdit(
    string Uri,
    int StartLine,
    int StartCharacter,
    int EndLine,
    int EndCharacter,
    string NewText);

/// <summary>All edits for one document, ordered by position.</summary>
public sealed record NemerleDocumentEdits(string Uri, IReadOnlyList<NemerleTextEdit> Edits);

/// <summary>
/// An LSP <c>WorkspaceEdit</c>: the per-document edit lists, ordered by URI so
/// the result is deterministic (tests, and a stable Output log).
/// </summary>
public sealed record NemerleWorkspaceEdit(IReadOnlyList<NemerleDocumentEdits> Documents)
{
    public int DocumentCount => Documents.Count;

    public int EditCount
    {
        get
        {
            var total = 0;
            foreach (var document in Documents)
                total += document.Edits.Count;
            return total;
        }
    }
}

/// <summary>
/// The outcome of assembling a workspace edit: either an edit, or the reason it
/// could not be assembled safely.  A conflict is never silently repaired,
/// because both ways of repairing it (dropping one edit, or merging them) change
/// the user's code in a way nobody asked for.
/// </summary>
public sealed record NemerleWorkspaceEditResult(NemerleWorkspaceEdit? Edit, string? Conflict)
{
    public bool IsUsable => Edit is not null && Conflict is null;
}

/// <summary>
/// Assembles LSP <c>WorkspaceEdit</c>s out of individual text edits.  Pure and
/// free of any engine or OmniSharp dependency, like <see cref="GotoMapping"/> and
/// <see cref="SignatureHelpMapping"/>, so the grouping/ordering/conflict rules are
/// pinned by unit tests rather than only by an end-to-end scenario.
///
/// <para>Introduced by WP-P3 (rename) and shared with WP-P5 (codeAction): rename
/// supplies one replacement per occurrence, a code action supplies inserted text
/// at a position, and both need the same three guarantees below.</para>
///
/// <para><b>Why ordering and de-duplication are not cosmetic.</b>  LSP applies the
/// edits of one document against the document's <em>original</em> state, and
/// forbids two edits of the same array from overlapping - a client encountering
/// overlap may reject the whole edit or apply it unpredictably.  The engine can
/// report the same location twice (partial types are the known case, see
/// <see cref="GotoMapping.ToLocations"/>), so exact duplicates that ask for the
/// same replacement are collapsed to one edit; anything else that overlaps is
/// reported as a conflict and the caller refuses.</para>
/// </summary>
public static class WorkspaceEditMapping
{
    /// <summary>
    /// Groups the edits per document, orders them by position, collapses exact
    /// duplicates, and refuses on any remaining overlap.
    /// </summary>
    public static NemerleWorkspaceEditResult Build(IEnumerable<NemerleTextEdit> edits)
    {
        var byUri = new SortedDictionary<string, List<NemerleTextEdit>>(StringComparer.Ordinal);
        foreach (var edit in edits)
        {
            if (!byUri.TryGetValue(edit.Uri, out var list))
            {
                list = [];
                byUri.Add(edit.Uri, list);
            }

            list.Add(edit);
        }

        var documents = new List<NemerleDocumentEdits>(byUri.Count);
        foreach (var (uri, list) in byUri)
        {
            list.Sort(CompareByPosition);

            var ordered = new List<NemerleTextEdit>(list.Count);
            foreach (var edit in list)
            {
                if (ordered.Count > 0)
                {
                    var previous = ordered[^1];
                    // Two insertions at the same point are additive, not
                    // conflicting: both texts land there, and only their relative
                    // order is the client's choice.  WP-P5 emits insertions, so
                    // this is the case that must not be mistaken for a collision.
                    if (SameRange(previous, edit) && !IsInsertion(edit))
                    {
                        // The same span twice: identical replacements are the
                        // engine reporting one location twice, which is benign.
                        // Two different replacements for one span is a real
                        // disagreement and must not be resolved by luck.
                        if (string.Equals(previous.NewText, edit.NewText, StringComparison.Ordinal))
                            continue;

                        return new NemerleWorkspaceEditResult(
                            null,
                            $"two different replacements for {uri} " +
                            $"{edit.StartLine}:{edit.StartCharacter}-{edit.EndLine}:{edit.EndCharacter}");
                    }

                    if (Overlaps(previous, edit))
                        return new NemerleWorkspaceEditResult(
                            null,
                            $"overlapping edits in {uri} at " +
                            $"{previous.StartLine}:{previous.StartCharacter}-{previous.EndLine}:{previous.EndCharacter} " +
                            $"and {edit.StartLine}:{edit.StartCharacter}-{edit.EndLine}:{edit.EndCharacter}");
                }

                ordered.Add(edit);
            }

            documents.Add(new NemerleDocumentEdits(uri, ordered));
        }

        return new NemerleWorkspaceEditResult(new NemerleWorkspaceEdit(documents), null);
    }

    private static int CompareByPosition(NemerleTextEdit left, NemerleTextEdit right)
    {
        var byStartLine = left.StartLine.CompareTo(right.StartLine);
        if (byStartLine != 0)
            return byStartLine;

        var byStartCharacter = left.StartCharacter.CompareTo(right.StartCharacter);
        if (byStartCharacter != 0)
            return byStartCharacter;

        var byEndLine = left.EndLine.CompareTo(right.EndLine);
        return byEndLine != 0 ? byEndLine : left.EndCharacter.CompareTo(right.EndCharacter);
    }

    /// <summary>An empty range: the edit inserts rather than replaces.</summary>
    private static bool IsInsertion(NemerleTextEdit edit) =>
        edit.StartLine == edit.EndLine && edit.StartCharacter == edit.EndCharacter;

    private static bool SameRange(NemerleTextEdit left, NemerleTextEdit right) =>
        left.StartLine == right.StartLine &&
        left.StartCharacter == right.StartCharacter &&
        left.EndLine == right.EndLine &&
        left.EndCharacter == right.EndCharacter;

    /// <summary>
    /// True when <paramref name="later"/> starts before <paramref name="earlier"/>
    /// ends.  Touching ranges (one ends exactly where the next begins) do not
    /// overlap, and two insertions at the same point do not either - they are
    /// ordered, not conflicting.
    /// </summary>
    private static bool Overlaps(NemerleTextEdit earlier, NemerleTextEdit later) =>
        later.StartLine != earlier.EndLine
            ? later.StartLine < earlier.EndLine
            : later.StartCharacter < earlier.EndCharacter;
}
