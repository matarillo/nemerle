namespace Nemerle.ProjectInfo;

/// <summary>
/// Why a rename was refused.  Every refusal is reported to the user and logged;
/// none of them is a silent no-op, because a rename that quietly does nothing and
/// a rename that quietly does half the job look the same in an editor.
/// </summary>
public enum NemerleRenameRefusal
{
    /// <summary>Not a refusal.</summary>
    None,

    /// <summary>The document is not open in this server.</summary>
    DocumentNotOpen,

    /// <summary>The engine resolved nothing at this position.</summary>
    NoSymbol,

    /// <summary>
    /// The engine resolved something, but no occurrence of it covers the caret.
    /// This is the guard against renaming the enclosing declaration when the
    /// caret sits on whitespace or punctuation inside it - the engine answers
    /// with the enclosing type there (measured in WP-P2), which is a reasonable
    /// answer for "highlight" and a destructive one for "rename".
    /// </summary>
    CaretNotOnSymbol,

    /// <summary>
    /// The symbol has no source location in this workspace: a metadata / BCL /
    /// NuGet member.  There is nothing to edit.
    /// </summary>
    ExternalSymbol,

    /// <summary>
    /// The symbol is used here but declared outside the engine workspace.
    /// Renaming would edit the uses and leave the declaration, i.e. break the
    /// build.  This is <c>39-prerelease-wp-n2-log.md</c> §7-4's cross-project
    /// rule, enforced mechanically rather than by intent.
    ///
    /// <para>Measured (WP-P3): this is also what a BCL / NuGet member looks like
    /// from the usages route, because the engine's usage collection for such a
    /// member reports only the in-workspace call sites and no source-less
    /// declaration target.  <see cref="ExternalSymbol"/> therefore covers only the
    /// case where nothing navigable came back at all, and the wording of this one
    /// names both possibilities.</para>
    /// </summary>
    DeclarationOutsideWorkspace,

    /// <summary>
    /// At least one occurrence's source text differs from the symbol's own name,
    /// so the usage set is not a set of plain identifier occurrences and blind
    /// replacement would corrupt the file.
    /// </summary>
    InconsistentOccurrences,

    /// <summary>The requested new name is not a legal Nemerle identifier.</summary>
    InvalidNewName,

    /// <summary>
    /// The requested new name is a keyword - either a base language keyword or
    /// one a syntax macro introduced into this file's environment.
    /// </summary>
    NewNameIsKeyword,
}

/// <summary>
/// The answer to <c>textDocument/prepareRename</c>: the range the editor should
/// offer to rename, or the reason there is none.
/// </summary>
public sealed record NemerleRenamePreparation(
    NemerleGotoLocation? Range,
    NemerleRenameRefusal Refusal)
{
    public bool CanRename => Range is not null && Refusal == NemerleRenameRefusal.None;
}

/// <summary>
/// One occurrence to be rewritten: its location, and the source text currently in
/// that range (null when the server has no text for that document).
/// </summary>
public sealed record NemerleRenameOccurrence(NemerleGotoLocation Location, string? Text);

/// <summary>
/// The rename rules, as pure functions over the engine's usage collection
/// (<see cref="NemerleGotoTarget"/>) - engine- and OmniSharp-free like
/// <see cref="GotoMapping"/>, so each refusal is pinned by a unit test.
///
/// <para><b>The governing principle is that a refusal is cheap and a wrong rename
/// is not.</b>  A rename that misses an occurrence, or rewrites something that
/// was not the symbol, silently breaks the user's build in a way they will
/// attribute to their own edit.  Every rule below therefore refuses on doubt
/// instead of doing its best.</para>
/// </summary>
public static class RenameMapping
{
    /// <summary>
    /// Decides whether the symbol at the caret can be renamed, and returns the
    /// range the editor should pre-select.
    /// </summary>
    /// <param name="targets">The engine's usage collection, declaration included.</param>
    /// <param name="fileIndex">The compiler's file index of the document asked about.</param>
    /// <param name="lspLine">0-based caret line.</param>
    /// <param name="lspCharacter">0-based caret character (UTF-16 code units).</param>
    public static NemerleRenamePreparation Prepare(
        IReadOnlyList<NemerleGotoTarget> targets,
        int fileIndex,
        int lspLine,
        int lspCharacter)
    {
        if (targets.Count == 0)
            return new NemerleRenamePreparation(null, NemerleRenameRefusal.NoSymbol);

        var locations = GotoMapping.ToLocations(targets, includeDeclaration: true);
        if (locations.Count == 0)
            return new NemerleRenamePreparation(null, NemerleRenameRefusal.ExternalSymbol);

        // The declaration must be one of the in-workspace locations.  When the
        // symbol comes from a referenced project the engine still finds its uses
        // here, but the declaring source is not part of this engine workspace, so
        // no target is both a definition and in-workspace.
        var declared = false;
        foreach (var target in targets)
        {
            if (target.IsDefinition && target.FileIndex > 0 && !string.IsNullOrEmpty(target.FilePath))
            {
                declared = true;
                break;
            }
        }

        if (!declared)
        {
            // Both cases mean "the declaration is not ours to edit", but they read
            // very differently to a user: a BCL / NuGet member (the engine reports
            // it with no source at all) versus a symbol from a referenced project.
            var external = false;
            foreach (var target in targets)
            {
                if (target.FileIndex <= 0 || string.IsNullOrEmpty(target.FilePath))
                {
                    external = true;
                    break;
                }
            }

            return new NemerleRenamePreparation(
                null,
                external
                    ? NemerleRenameRefusal.ExternalSymbol
                    : NemerleRenameRefusal.DeclarationOutsideWorkspace);
        }

        var caret = FindCaretOccurrence(targets, fileIndex, lspLine, lspCharacter);
        return caret is null
            ? new NemerleRenamePreparation(null, NemerleRenameRefusal.CaretNotOnSymbol)
            : new NemerleRenamePreparation(caret, NemerleRenameRefusal.None);
    }

    /// <summary>
    /// The occurrence in this document that covers the caret.  The end is treated
    /// as inclusive: editors commonly report the caret sitting immediately after
    /// the last character of the identifier the user means (<c>foo|</c>).
    /// </summary>
    private static NemerleGotoLocation? FindCaretOccurrence(
        IReadOnlyList<NemerleGotoTarget> targets,
        int fileIndex,
        int lspLine,
        int lspCharacter)
    {
        NemerleGotoLocation? best = null;
        foreach (var target in targets)
        {
            if (target.FileIndex != fileIndex)
                continue;

            var candidates = GotoMapping.ToLocations([target], includeDeclaration: true);
            if (candidates.Count == 0)
                continue;

            var location = candidates[0];
            if (Covers(location, lspLine, lspCharacter) &&
                (best is null || IsNarrower(location, best)))
                best = location;
        }

        return best;
    }

    private static bool Covers(NemerleGotoLocation location, int line, int character)
    {
        if (line < location.StartLine || line > location.EndLine)
            return false;
        if (line == location.StartLine && character < location.StartCharacter)
            return false;
        if (line == location.EndLine && character > location.EndCharacter)
            return false;
        return true;
    }

    /// <summary>
    /// Prefers the smallest covering range.  A caret on an identifier can be
    /// covered by both the identifier and an enclosing declaration's range when
    /// the engine reports both; the identifier is what the user pointed at.
    /// </summary>
    private static bool IsNarrower(NemerleGotoLocation candidate, NemerleGotoLocation incumbent)
    {
        var candidateLines = candidate.EndLine - candidate.StartLine;
        var incumbentLines = incumbent.EndLine - incumbent.StartLine;
        if (candidateLines != incumbentLines)
            return candidateLines < incumbentLines;

        return candidateLines == 0 &&
            candidate.EndCharacter - candidate.StartCharacter <
                incumbent.EndCharacter - incumbent.StartCharacter;
    }

    /// <summary>
    /// Checks the requested new name.  <paramref name="contextKeywords"/> is the
    /// set of words that are keywords in the edited file's environment - base
    /// language keywords plus anything a syntax macro added through the file's
    /// <c>using</c>s (WP-O5a's distinction).  Naming a value after one of those
    /// produces a file that no longer parses, which is exactly the failure the
    /// user cannot diagnose.
    /// </summary>
    public static NemerleRenameRefusal ValidateNewName(
        string? newName,
        IReadOnlySet<string> contextKeywords)
    {
        if (string.IsNullOrEmpty(newName) || !IsIdentifier(newName))
            return NemerleRenameRefusal.InvalidNewName;

        return contextKeywords.Contains(newName)
            ? NemerleRenameRefusal.NewNameIsKeyword
            : NemerleRenameRefusal.None;
    }

    /// <summary>
    /// The lexer's identifier rule (<c>ncc/parsing/Lexer.n</c>: <c>IsIdBeginning</c>
    /// is a letter or <c>_</c>, and <c>get_id</c> continues over letters, digits,
    /// <c>_</c> and <c>'</c>).  Mirrored rather than called because ProjectInfo
    /// carries no compiler reference; the mirror is small, and it is the rule the
    /// tests state.
    /// </summary>
    private static bool IsIdentifier(string name)
    {
        if (!char.IsLetter(name[0]) && name[0] != '_')
            return false;

        for (var i = 1; i < name.Length; i++)
        {
            var c = name[i];
            if (!char.IsLetter(c) && !char.IsDigit(c) && c != '_' && c != '\'')
                return false;
        }

        return true;
    }

    /// <summary>
    /// Verifies that every occurrence really is an occurrence of
    /// <paramref name="expected"/> before anything is rewritten.  The usage
    /// collection is an AST result, not a text search, so this cannot normally
    /// fail; it is here because the cost of it failing unnoticed is the user's
    /// source, and because it also catches the case where the buffer moved under
    /// a stale collection.  A null text (no buffer for that document) is treated
    /// as a failure rather than as permission.
    /// </summary>
    public static NemerleRenameRefusal CheckOccurrences(
        IReadOnlyList<NemerleRenameOccurrence> occurrences,
        string expected)
    {
        if (occurrences.Count == 0)
            return NemerleRenameRefusal.NoSymbol;

        foreach (var occurrence in occurrences)
        {
            if (!string.Equals(occurrence.Text, expected, StringComparison.Ordinal))
                return NemerleRenameRefusal.InconsistentOccurrences;
        }

        return NemerleRenameRefusal.None;
    }

    /// <summary>
    /// Turns the verified occurrences into a workspace edit that replaces each
    /// with <paramref name="newName"/>.
    /// </summary>
    public static NemerleWorkspaceEditResult ToWorkspaceEdit(
        IReadOnlyList<NemerleRenameOccurrence> occurrences,
        string newName)
    {
        var edits = new List<NemerleTextEdit>(occurrences.Count);
        foreach (var occurrence in occurrences)
        {
            var location = occurrence.Location;
            edits.Add(new NemerleTextEdit(
                location.Uri,
                location.StartLine,
                location.StartCharacter,
                location.EndLine,
                location.EndCharacter,
                newName));
        }

        return WorkspaceEditMapping.Build(edits);
    }

    /// <summary>A user-facing sentence for a refusal, used in the LSP error and in the log.</summary>
    public static string Describe(NemerleRenameRefusal refusal) => refusal switch
    {
        NemerleRenameRefusal.None => "",
        NemerleRenameRefusal.DocumentNotOpen => "The document is not open.",
        NemerleRenameRefusal.NoSymbol => "There is no symbol to rename at this position.",
        NemerleRenameRefusal.CaretNotOnSymbol => "Place the caret on the name you want to rename.",
        NemerleRenameRefusal.ExternalSymbol =>
            "This symbol is declared in a referenced assembly, so it has no source to rename.",
        NemerleRenameRefusal.DeclarationOutsideWorkspace =>
            "This symbol is not declared in this project (it comes from a referenced project or "
            + "assembly). Renaming it here would change only its uses; rename it where it is declared.",
        NemerleRenameRefusal.InconsistentOccurrences =>
            "The occurrences of this symbol no longer match its name (the document may have changed); "
            + "nothing was renamed.",
        NemerleRenameRefusal.InvalidNewName => "That is not a valid Nemerle identifier.",
        NemerleRenameRefusal.NewNameIsKeyword => "That name is a keyword in this file.",
        _ => "The rename was refused.",
    };
}
