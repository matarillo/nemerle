namespace Nemerle.ProjectInfo;

/// <summary>
/// The IDE engine's colorizer classification, mirrored from
/// <c>VsIntegration/Nemerle.Compiler.Utils/Nemerle.Completion2/CodeModel/ScanTokenColor.n</c>
/// by name and value.  Kept here (rather than referencing the engine assembly)
/// for the same reason as <see cref="CompletionMapping"/>: the mapping below stays
/// a pure, unit-testable function.  The server converts the engine enum with a
/// plain <c>(NemerleScanTokenColor)(int)</c> cast and verifies at startup that the
/// two enumerations still agree (see <c>NemerleSemanticTokensHandler</c>).
/// </summary>
public enum NemerleScanTokenColor
{
    // The first six values are fixed by the VS2010 colorizer contract.
    Keyword = 1,
    Comment = 2,
    Identifier = 3,
    String = 4,
    Number = 5,
    Text = 6,

    Operator = 7,
    Preprocessor,
    StringEx,
    VerbatimString,
    VerbatimStringEx,

    UserType,
    UserTypeDelegate,
    UserTypeEnum,
    UserTypeInterface,
    UserTypeValueType,

    Quotation,
    QuotationText,
    QuotationKeyword,
    QuotationComment,
    QuotationIdentifier,
    QuotationString,
    QuotationNumber,
    QuotationOperator,
    QuotationStringEx,
    QuotationVerbatimString,
    QuotationVerbatimStringEx,

    QuotationUserType,
    QuotationUserTypeDelegate,
    QuotationUserTypeEnum,
    QuotationUserTypeInterface,
    QuotationUserTypeValueType,

    HighlightOne,
    HighlightTwo,

    CommentTODO,
    CommentBUG,
    CommentHACK,

    QuotationCommentTODO,
    QuotationCommentBUG,
    QuotationCommentHACK,

    RecursiveString,
    RecursiveStringEx,
    QuotationRecursiveString,
    QuotationRecursiveStringEx,

    Field,
    Event,
    Method,
    Property,
}

/// <summary>
/// LSP semantic token type, as an index into the legend
/// <see cref="SemanticTokenMapping.TokenTypes"/>.  <see cref="None"/> means "emit
/// no token", which lets the client's TextMate grammar keep colorizing that span.
/// </summary>
public enum NemerleSemanticTokenType
{
    None = -1,
    Keyword = 0,
    Macro,
    Comment,
    String,
    Number,
    Operator,
    Variable,
    Type,
    Class,
    Interface,
    Enum,
    Struct,
    Property,
    Method,
    Event,
}

/// <summary>
/// LSP semantic token modifiers, as a bit set over
/// <see cref="SemanticTokenMapping.TokenModifiers"/>.  Both are Nemerle-specific
/// (no standard LSP modifier fits), so the VS Code extension declares them in
/// <c>contributes.semanticTokenModifiers</c> and gives them a theme fallback in
/// <c>contributes.semanticTokenScopes</c>.
/// </summary>
[Flags]
public enum NemerleSemanticTokenModifier
{
    None = 0,

    /// <summary>Inside a quasi-quotation (<c>&lt;[ ... ]&gt;</c>).</summary>
    Quotation = 1 << 0,

    /// <summary>
    /// An escape sequence or a <c>$</c> splice/interpolation run inside a string
    /// literal (the engine's <c>*StringEx</c> colors).
    /// </summary>
    Escape = 1 << 1,
}

/// <summary>
/// Maps the engine colorizer's <see cref="NemerleScanTokenColor"/> onto the LSP
/// semantic token legend (WP-O5a, 47-wp-o-plan.md §5).  Pure function, unit-tested.
///
/// <para>Only standard LSP token types are used, so every default VS Code theme
/// colors the result without any extension-side theme rules; the Nemerle-specific
/// distinctions ride on the two custom modifiers above.</para>
///
/// <para>The differentiator this exists for: a keyword that a syntax macro added
/// to the current <c>GlobalEnv</c> (via a <c>using</c> of a macro namespace) is
/// reported as <see cref="NemerleSemanticTokenType.Macro"/> rather than
/// <see cref="NemerleSemanticTokenType.Keyword"/>.  A TextMate grammar cannot know
/// those words, because they only exist after the compiler loaded the macros.
/// The caller decides which words qualify (see the server's
/// <c>env.Keywords \ CoreEnv.Keywords</c> test) and passes
/// <c>macroKeyword: true</c>.</para>
/// </summary>
public static class SemanticTokenMapping
{
    /// <summary>The legend's token types, in <see cref="NemerleSemanticTokenType"/> order.</summary>
    public static readonly string[] TokenTypes =
    [
        "keyword",
        "macro",
        "comment",
        "string",
        "number",
        "operator",
        "variable",
        "type",
        "class",
        "interface",
        "enum",
        "struct",
        "property",
        "method",
        "event",
    ];

    /// <summary>The legend's token modifiers, in <see cref="NemerleSemanticTokenModifier"/> bit order.</summary>
    public static readonly string[] TokenModifiers =
    [
        "quotation",
        "escape",
    ];

    /// <summary>
    /// Classifies one colorizer token.  <paramref name="macroKeyword"/> is only
    /// consulted for the keyword colors; passing it for anything else is ignored,
    /// so the caller may compute it unconditionally.
    /// </summary>
    public static (NemerleSemanticTokenType Type, NemerleSemanticTokenModifier Modifiers) Classify(
        NemerleScanTokenColor color,
        bool macroKeyword)
    {
        var keyword = macroKeyword ? NemerleSemanticTokenType.Macro : NemerleSemanticTokenType.Keyword;
        return color switch
        {
            // The preprocessor is not a macro (it is not part of the extensible
            // syntax); it reads as a keyword, which is also how the engine's
            // VS colorizer grouped it.
            NemerleScanTokenColor.Preprocessor =>
                (NemerleSemanticTokenType.Keyword, NemerleSemanticTokenModifier.None),
            NemerleScanTokenColor.Keyword => (keyword, NemerleSemanticTokenModifier.None),
            NemerleScanTokenColor.QuotationKeyword => (keyword, NemerleSemanticTokenModifier.Quotation),

            // TODO/BUG/HACK comments are deliberately folded into plain comments:
            // no standard modifier means "annotation", and inventing one for a
            // cosmetic upstream extra is outside this WP's "small and closed" scope.
            NemerleScanTokenColor.Comment
                or NemerleScanTokenColor.CommentTODO
                or NemerleScanTokenColor.CommentBUG
                or NemerleScanTokenColor.CommentHACK =>
                (NemerleSemanticTokenType.Comment, NemerleSemanticTokenModifier.None),
            NemerleScanTokenColor.QuotationComment
                or NemerleScanTokenColor.QuotationCommentTODO
                or NemerleScanTokenColor.QuotationCommentBUG
                or NemerleScanTokenColor.QuotationCommentHACK =>
                (NemerleSemanticTokenType.Comment, NemerleSemanticTokenModifier.Quotation),

            NemerleScanTokenColor.String
                or NemerleScanTokenColor.VerbatimString
                or NemerleScanTokenColor.RecursiveString =>
                (NemerleSemanticTokenType.String, NemerleSemanticTokenModifier.None),
            NemerleScanTokenColor.StringEx
                or NemerleScanTokenColor.VerbatimStringEx
                or NemerleScanTokenColor.RecursiveStringEx =>
                (NemerleSemanticTokenType.String, NemerleSemanticTokenModifier.Escape),
            NemerleScanTokenColor.QuotationString
                or NemerleScanTokenColor.QuotationVerbatimString
                or NemerleScanTokenColor.QuotationRecursiveString =>
                (NemerleSemanticTokenType.String, NemerleSemanticTokenModifier.Quotation),
            NemerleScanTokenColor.QuotationStringEx
                or NemerleScanTokenColor.QuotationVerbatimStringEx
                or NemerleScanTokenColor.QuotationRecursiveStringEx =>
                (NemerleSemanticTokenType.String,
                    NemerleSemanticTokenModifier.Quotation | NemerleSemanticTokenModifier.Escape),

            NemerleScanTokenColor.Number => (NemerleSemanticTokenType.Number, NemerleSemanticTokenModifier.None),
            NemerleScanTokenColor.QuotationNumber =>
                (NemerleSemanticTokenType.Number, NemerleSemanticTokenModifier.Quotation),

            // The <[ and ]> delimiters themselves carry the Quotation color.
            NemerleScanTokenColor.Operator => (NemerleSemanticTokenType.Operator, NemerleSemanticTokenModifier.None),
            NemerleScanTokenColor.Quotation or NemerleScanTokenColor.QuotationOperator =>
                (NemerleSemanticTokenType.Operator, NemerleSemanticTokenModifier.Quotation),

            NemerleScanTokenColor.Identifier => (NemerleSemanticTokenType.Variable, NemerleSemanticTokenModifier.None),
            NemerleScanTokenColor.QuotationIdentifier =>
                (NemerleSemanticTokenType.Variable, NemerleSemanticTokenModifier.Quotation),

            // No LSP token type for a delegate; "type" is the least surprising
            // (CompletionMapping makes the same call with the class icon).
            NemerleScanTokenColor.UserTypeDelegate =>
                (NemerleSemanticTokenType.Type, NemerleSemanticTokenModifier.None),
            NemerleScanTokenColor.QuotationUserTypeDelegate =>
                (NemerleSemanticTokenType.Type, NemerleSemanticTokenModifier.Quotation),
            NemerleScanTokenColor.UserType => (NemerleSemanticTokenType.Class, NemerleSemanticTokenModifier.None),
            NemerleScanTokenColor.QuotationUserType =>
                (NemerleSemanticTokenType.Class, NemerleSemanticTokenModifier.Quotation),
            NemerleScanTokenColor.UserTypeInterface =>
                (NemerleSemanticTokenType.Interface, NemerleSemanticTokenModifier.None),
            NemerleScanTokenColor.QuotationUserTypeInterface =>
                (NemerleSemanticTokenType.Interface, NemerleSemanticTokenModifier.Quotation),
            NemerleScanTokenColor.UserTypeEnum => (NemerleSemanticTokenType.Enum, NemerleSemanticTokenModifier.None),
            NemerleScanTokenColor.QuotationUserTypeEnum =>
                (NemerleSemanticTokenType.Enum, NemerleSemanticTokenModifier.Quotation),
            NemerleScanTokenColor.UserTypeValueType =>
                (NemerleSemanticTokenType.Struct, NemerleSemanticTokenModifier.None),
            NemerleScanTokenColor.QuotationUserTypeValueType =>
                (NemerleSemanticTokenType.Struct, NemerleSemanticTokenModifier.Quotation),

            // LSP has no "field"; property is the closest standard type.
            NemerleScanTokenColor.Field or NemerleScanTokenColor.Property =>
                (NemerleSemanticTokenType.Property, NemerleSemanticTokenModifier.None),
            NemerleScanTokenColor.Method => (NemerleSemanticTokenType.Method, NemerleSemanticTokenModifier.None),
            NemerleScanTokenColor.Event => (NemerleSemanticTokenType.Event, NemerleSemanticTokenModifier.None),

            // Text/whitespace, quotation whitespace, and the two hover-highlight
            // overlays (which this path never produces: the LSP colorizer sets no
            // highlights) get no token at all.
            _ => (NemerleSemanticTokenType.None, NemerleSemanticTokenModifier.None),
        };
    }

    /// <summary>The legend name of a token type, or null for <see cref="NemerleSemanticTokenType.None"/>.</summary>
    public static string? TypeName(NemerleSemanticTokenType type) =>
        type == NemerleSemanticTokenType.None ? null : TokenTypes[(int)type];

    /// <summary>The legend names of a modifier bit set, in legend order.</summary>
    public static string[] ModifierNames(NemerleSemanticTokenModifier modifiers)
    {
        if (modifiers == NemerleSemanticTokenModifier.None)
            return [];

        var names = new List<string>(TokenModifiers.Length);
        for (var bit = 0; bit < TokenModifiers.Length; bit++)
        {
            if (((int)modifiers & (1 << bit)) != 0)
                names.Add(TokenModifiers[bit]);
        }

        return names.ToArray();
    }
}
