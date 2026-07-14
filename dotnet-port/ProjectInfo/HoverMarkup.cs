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

    // Any remaining pseudo-markup tag: <keyword>, </keyword>, <b>, <hint value='...'>,
    // <code>, <pre>, <params>, <pname>, <ptype>, ...  Source-derived '<'/'>' are
    // still HtmlMangling-escaped as &lt;/&gt; at this point, so this never eats a
    // real angle bracket from the analyzed code.
    [GeneratedRegex(@"<[^<>]*>")]
    private static partial Regex AnyTag();

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
        text = AnyTag().Replace(text, string.Empty);
        // Reverse HintHelper.HtmlMangling (& -> &amp;, > -> &gt;, < -> &lt;):
        // decode &amp; last so an escaped "&amp;lt;" round-trips to "&lt;" rather
        // than collapsing to "<".
        text = text
            .Replace("&lt;", "<")
            .Replace("&gt;", ">")
            .Replace("&amp;", "&");
        return NormalizeNewlines(text).Trim();
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
