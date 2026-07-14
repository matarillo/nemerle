using System.Text.RegularExpressions;

namespace Nemerle.ProjectInfo;

/// <summary>
/// Extracts the leading "N####: " Nemerle warning-code prefix that
/// ncc\parsing\Utility.n's Message.Warning prepends to a coded warning message,
/// so it can be surfaced as a structured diagnostic code (an MSBuild warning
/// code column via Log.LogWarning, or an LSP Diagnostic.code) instead of being
/// left inline in the human-readable text.  Uncoded warnings (ncc code == -1)
/// have no such prefix and pass through unchanged.
/// </summary>
public static partial class NemerleWarningCode
{
    // ncc emits "N<digits>: <message>" for coded warnings (see
    // ncc\parsing\Utility.n Message.Warning: def m = $"N$code: $m").  Singleline
    // so a multi-line message body after the prefix is preserved intact.
    [GeneratedRegex(@"^N(?<code>\d+):[ ](?<rest>.*)$", RegexOptions.Singleline)]
    private static partial Regex CodePrefix();

    /// <summary>
    /// If <paramref name="message"/> starts with an "N####: " code prefix,
    /// returns the code as "N####" and the remaining message with the prefix
    /// stripped; otherwise returns (null, message unchanged).
    /// </summary>
    public static (string? Code, string Message) Extract(string? message)
    {
        if (string.IsNullOrEmpty(message))
            return (null, message ?? string.Empty);

        var match = CodePrefix().Match(message);
        return match.Success
            ? ("N" + match.Groups["code"].Value, match.Groups["rest"].Value)
            : (null, message);
    }
}
