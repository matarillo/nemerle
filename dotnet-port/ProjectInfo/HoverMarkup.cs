using System.Text.RegularExpressions;

namespace Nemerle.ProjectInfo;

/// <summary>
/// Converts the IDE engine's VS2010-era pseudo-markup hint strings
/// (<c>QuickTipInfo.Text</c>: <c>&lt;lb/&gt;</c>, <c>&lt;keyword&gt;</c>,
/// <c>&lt;b&gt;</c>, <c>&lt;hint&gt;</c>, <c>&lt;params&gt;</c>, ... with
/// HtmlMangling-escaped source text) into LSP hover payloads.  The rules are a
/// pure function so they can be pinned by unit tests (WP-M2, §6.3 of
/// 29-devenv2-plan.md); the handler wraps the result in a
/// <c>MarkupContent</c> with the client-negotiated kind.
///
/// Two invariants matter: no raw pseudo-markup tag ever reaches the client, and
/// nothing derived from the analyzed source is interpreted as markdown (the
/// markdown form fences the whole hint, so metacharacters render literally).
/// </summary>
public static partial class HoverMarkup
{
    // The engine's line break.  Matched before generic tag stripping (which would
    // otherwise just delete it) and tolerant of "<lb/>" / "<lb />" spelling.
    [GeneratedRegex(@"<\s*lb\s*/?\s*>", RegexOptions.IgnoreCase)]
    private static partial Regex LineBreakTag();

    // A declaration hover ends with the declaration's source location as a
    // final blank-line-separated paragraph: "file:line:col:endLine:endCol:".
    // Anchored to the end and to the whole paragraph so an identifier or doc
    // sentence containing a lookalike substring is never touched.
    [GeneratedRegex(@"\n\n[^\n]+:\d+:\d+:\d+:\d+:\s*$")]
    private static partial Regex DeclarationLocationTail();

    // Any remaining pseudo-markup tag: <keyword>, </keyword>, <b>, <hint value='...'>,
    // <code>, <pre>, <params>, <pname>, <ptype>, ...  Source-derived '<'/'>' are
    // still HtmlMangling-escaped as &lt;/&gt; at this point, so this never eats a
    // real angle bracket from the analyzed code.
    [GeneratedRegex(@"<[^<>]*>")]
    private static partial Regex AnyTag();

    // A self-closing <hint value='...' key='...' /> tag: SubHintForType emits these
    // for a bare type simple name ("SMap", "array", ...) that the old VS2010 UI
    // rendered inline from the "value" attribute. Attribute order and quote style
    // are not fixed, so this just isolates the whole self-closing tag; the value
    // itself is pulled out by HintValueAttribute() below. Paired <hint>...</hint>
    // tags (used elsewhere for tooltips-on-punctuation) never match here because
    // they have no trailing "/>", so they still fall through to AnyTag() unchanged.
    [GeneratedRegex(@"<\s*hint\b[^<>]*/\s*>", RegexOptions.IgnoreCase)]
    private static partial Regex SelfClosingHintTag();

    // The value='...'/"..." attribute inside an already-isolated self-closing hint
    // tag. Searched (not anchored), so it is found regardless of attribute order.
    [GeneratedRegex(@"\bvalue\s*=\s*(?:'([^']*)'|""([^""]*)"")", RegexOptions.IgnoreCase)]
    private static partial Regex HintValueAttribute();

    /// <summary>
    /// Strips pseudo-markup to a plain-text hint: <c>&lt;lb/&gt;</c> becomes a
    /// newline, every other tag is removed (its visible content is kept), and
    /// the HtmlMangling escapes (<c>&amp;amp;</c>/<c>&amp;lt;</c>/<c>&amp;gt;</c>)
    /// are restored.  Used directly when the client did not advertise markdown.
    /// </summary>
    public static string ToPlainText(string? markup)
    {
        if (string.IsNullOrEmpty(markup))
            return string.Empty;

        var text = LineBreakTag().Replace(markup, "\n");
        // Expand self-closing <hint value='...' /> tags to their visible type
        // name before the generic tag stripper below would otherwise delete the
        // whole tag (this was the R-H / E7-D information loss: a hover like
        // "args : []" instead of "args : array[string]"; 38-prerelease-wp-n2-log.md
        // §5.1). A malformed/valueless self-closing hint tag is left as-is here
        // and falls through to AnyTag() removal, i.e. today's behavior, so no raw
        // markup ever leaks either way.
        text = SelfClosingHintTag().Replace(text, ExpandSelfClosingHint);
        text = AnyTag().Replace(text, string.Empty);
        // Reverse HintHelper.HtmlMangling (& -> &amp;, > -> &gt;, < -> &lt;):
        // decode &amp; last so an escaped "&amp;lt;" round-trips to "&lt;" rather
        // than collapsing to "<". This also covers entities that were carried
        // inside an expanded hint value above (that step never itself decodes).
        text = text
            .Replace("&lt;", "<")
            .Replace("&gt;", ">")
            .Replace("&amp;", "&");
        return NormalizeNewlines(text).Trim();
    }

    // Extracts the visible text from a self-closing <hint value='...' /> tag.
    // Returns the match unchanged when there is no "value" attribute, so the
    // caller's fallback (AnyTag() removal) applies exactly as it did before this
    // expansion step existed.
    private static string ExpandSelfClosingHint(Match tag)
    {
        var value = HintValueAttribute().Match(tag.Value);
        if (!value.Success)
            return tag.Value;
        return value.Groups[1].Success ? value.Groups[1].Value : value.Groups[2].Value;
    }

    /// <summary>
    /// Removes the trailing source-location paragraph
    /// (<c>"...\n\nfile:line:col:endLine:endCol:"</c>) that the engine appends
    /// to declaration hovers.  That paragraph is how the Visual Studio tooltip
    /// showed "declared at"; an LSP client gets the same answer from
    /// go-to-definition, so left in place it only makes every declaration
    /// hover end with a raw absolute-path line.  Apply to the engine text
    /// before <see cref="ToPlainText"/>/<see cref="ToMarkdown"/>.
    /// </summary>
    public static string StripDeclarationLocationTail(string? markup)
    {
        if (string.IsNullOrEmpty(markup))
            return string.Empty;

        return DeclarationLocationTail().Replace(markup, string.Empty);
    }

    /// <summary>
    /// Renders the hint as GitHub-flavored markdown: the plain-text form fenced
    /// as a Nemerle code block.  Fencing keeps every markdown metacharacter (and
    /// any punctuation from source identifiers) literal, so the signature reads
    /// as code and nothing from the analyzed source is interpreted as markup.
    /// The fence length grows past any backtick run in the hint so the block
    /// cannot be closed early.
    /// </summary>
    public static string ToMarkdown(string? markup)
    {
        var text = ToPlainText(markup);
        if (text.Length == 0)
            return string.Empty;

        var fence = new string('`', Math.Max(3, LongestBacktickRun(text) + 1));
        return $"{fence}nemerle\n{text}\n{fence}";
    }

    private static string NormalizeNewlines(string text) =>
        text.Replace("\r\n", "\n").Replace('\r', '\n');

    private static int LongestBacktickRun(string text)
    {
        var longest = 0;
        var current = 0;
        foreach (var c in text)
        {
            if (c == '`')
            {
                current++;
                if (current > longest)
                    longest = current;
            }
            else
            {
                current = 0;
            }
        }

        return longest;
    }
}
