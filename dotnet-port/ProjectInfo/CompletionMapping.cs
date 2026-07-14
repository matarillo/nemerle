namespace Nemerle.ProjectInfo;

/// <summary>
/// Editor-neutral completion item kind.  The IDE engine tags each
/// <c>CompletionElem</c> with an integer glyph (the <c>GlyphType</c> enum, whose
/// members are spaced by 6 for a VS2010 icon-strip layout plus 205/206 for the
/// keyword/snippet glyphs); this enum is the LSP-facing category the handler
/// maps 1:1 onto <c>CompletionItemKind</c>.  Keeping the mapping here (free of
/// any engine or OmniSharp dependency, like <see cref="HoverMarkup"/>) lets it
/// be pinned by unit tests (WP-M3, §6.3/§8 of 29-devenv2-plan.md).
/// </summary>
public enum NemerleCompletionKind
{
    /// <summary>Fallback for an unrecognized glyph.</summary>
    Text,
    Class,
    Interface,
    Enum,
    EnumMember,
    Struct,
    Field,
    Property,
    Method,
    Function,
    Event,
    Constant,
    Variable,
    Module,
    Keyword,
}

/// <summary>
/// Maps the engine's integer completion glyph to an editor-neutral
/// <see cref="NemerleCompletionKind"/>.  Pure function, unit-tested; the handler
/// translates the result to the LSP <c>CompletionItemKind</c>.
/// </summary>
public static class CompletionMapping
{
    // Mirrors Nemerle.Completion2.GlyphType (GlyphType.n).  Kept as raw integers
    // so ProjectInfo needs no reference to the engine assembly.  Members are
    // 6 * n (a VS icon-strip stride) with Snippet/Keyword as 205/206.
    private const int Class = 0;
    private const int Const = 6 * 1;
    private const int Delegate = 6 * 2;
    private const int Enum = 6 * 3;
    private const int EnumValue = 6 * 4;
    private const int Event = 6 * 5;
    private const int Field = 6 * 7;
    private const int Interface = 6 * 8;
    private const int Block = 6 * 9;
    private const int Variant = 6 * 10;
    private const int VariantOption = 6 * 11;
    private const int Method = 6 * 12;
    private const int Function = 6 * 13;
    private const int Namespace = 6 * 15; // GlyphType.Operator shares this value
    private const int Property = 6 * 17;
    private const int Struct = 6 * 18;
    private const int Macro = 6 * 20;
    private const int Local = 6 * 23;
    private const int Snippet = 205;
    private const int KeywordGlyph = 206;

    public static NemerleCompletionKind GlyphToKind(int glyph) => glyph switch
    {
        Class => NemerleCompletionKind.Class,
        Const => NemerleCompletionKind.Constant,
        // No LSP kind for a delegate; a class icon is the least surprising.
        Delegate => NemerleCompletionKind.Class,
        Enum => NemerleCompletionKind.Enum,
        EnumValue => NemerleCompletionKind.EnumMember,
        Event => NemerleCompletionKind.Event,
        Field => NemerleCompletionKind.Field,
        Interface => NemerleCompletionKind.Interface,
        // A block-return label reads best as a local variable.
        Block => NemerleCompletionKind.Variable,
        // A variant is a discriminated union of options; its options are its
        // members.  Class/EnumMember are the closest LSP kinds.
        Variant => NemerleCompletionKind.Class,
        VariantOption => NemerleCompletionKind.EnumMember,
        Method => NemerleCompletionKind.Method,
        Function => NemerleCompletionKind.Function,
        Namespace => NemerleCompletionKind.Module,
        Property => NemerleCompletionKind.Property,
        Struct => NemerleCompletionKind.Struct,
        // Macros invoke like functions/keywords; a function icon fits both.
        Macro => NemerleCompletionKind.Function,
        Local => NemerleCompletionKind.Variable,
        // The engine emits the snippet glyph for keyword completions (and as a
        // catch-all for ambiguous/unloaded entries); keyword is the dominant,
        // most useful reading.
        Snippet => NemerleCompletionKind.Keyword,
        KeywordGlyph => NemerleCompletionKind.Keyword,
        _ => NemerleCompletionKind.Text,
    };
}
