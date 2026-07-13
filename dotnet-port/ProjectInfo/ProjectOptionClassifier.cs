namespace Nemerle.ProjectInfo;

internal static class ProjectOptionClassifier
{
    private static readonly string[] UnsupportedSemanticStrings =
    [
        "-reference", "-r", "-ref", "-library-path", "-lib", "-L", "-macros", "-m",
        "-pkg-config", "-pkg", "-p", "-from-file", "-disable-keyword", "-no-keyword",
        "-root-namespace", "-rns", "-main", "-target", "-t", "-platform",
    ];

    private static readonly string[] UnsupportedSemanticFlags =
    [
        "-no-stdmacros", "-nostdmacros", "-no-stdlib", "-nostdlib", "-use-loaded-corlib",
        "-target-library", "-tdll", "-target-exe", "-texe",
    ];

    private static readonly string[] UnsupportedSemanticBooleans = ["-greedy-references", "-greedy"];
    private static readonly string[] DiagnosticStrings = ["-warn", "-W", "-nowarn", "-dowarn"];
    private static readonly string[] DiagnosticBooleans = ["-warnaserror"];
    private static readonly string[] DiagnosticFlags = ["-pedantic-lexer"];
    private static readonly string[] KnownNonSemanticStrings =
    [
        "-out", "-o", "-project-path", "-pp", "-output-path", "-doc", "-win32-resource",
        "-win32res", "-resource", "-res", "-linkresource", "-linkres", "-keyfile",
        "-optimize-options", "-Oopt", "-min-switch-size-variants", "-Oswv",
        "-dump-typed-method", "-dm", "-min-switch-size-ordinals", "-Oswo",
    ];
    private static readonly string[] KnownNonSemanticBooleans = ["-debug", "-g", "-progress-bar", "-bar"];
    private static readonly string[] KnownNonSemanticFlags =
    [
        "-general-tail-call-opt", "-Ot", "-ignore-confusion", "-throw-on-error", "-early-exit",
        "-dump-typed-tree", "-dt", "-print-expressions-type", "-pet", "-additional-debug", "-ad",
        "-dump-decision-trees", "-dd", "-boolean-constant-matching-opt", "-Obcm",
        "-ordinal-constant-matching-opt", "-Oocm", "-string-constant-matching-opt", "-Oscm",
        "-nologo", "-no-color", "-no-progress-bar", "-q", "-stats", "-warn-help", "-optimize",
        "-O", "-debugger", "-stats2",
    ];

    public static ProjectOptionSnapshot Classify(string? raw)
    {
        raw ??= string.Empty;
        var tokens = raw.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var supported = new List<string>();
        var unsupportedSemantic = new List<string>();
        var unsupportedDiagnostic = new List<string>();
        var defines = new List<string>();
        bool? checkedOverflow = null;
        var indentationSyntax = false;

        for (var index = 0; index < tokens.Length; index++)
        {
            var rawToken = tokens[index];
            var token = NormalizeOptionPrefix(rawToken);
            if (TryReadString(tokens, ref index, token, ["-define", "-d", "-def"], out var defineValue))
            {
                supported.Add(rawToken);
                defines.AddRange(defineValue.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
            }
            else if (token is "-indentation-syntax" or "-i")
            {
                supported.Add(rawToken);
                indentationSyntax = true;
            }
            else if (TryReadBoolean(token, "-checked", out var value))
            {
                supported.Add(rawToken);
                checkedOverflow = value;
            }
            else if (rawToken.StartsWith('@') ||
                     TryReadString(tokens, ref index, token, UnsupportedSemanticStrings, out _) ||
                     UnsupportedSemanticFlags.Contains(token, StringComparer.Ordinal) ||
                     IsBoolean(token, UnsupportedSemanticBooleans))
            {
                unsupportedSemantic.Add(rawToken);
            }
            else if (TryReadString(tokens, ref index, token, DiagnosticStrings, out _) ||
                     DiagnosticFlags.Contains(token, StringComparer.Ordinal) ||
                     IsBoolean(token, DiagnosticBooleans))
            {
                unsupportedDiagnostic.Add(rawToken);
            }
            else if (TryReadString(tokens, ref index, token, KnownNonSemanticStrings, out _) ||
                     KnownNonSemanticFlags.Contains(token, StringComparer.Ordinal) ||
                     IsBoolean(token, KnownNonSemanticBooleans))
            {
                // Preserved in Raw. These options affect output/code generation, not the WP-L2 semantic snapshot.
            }
            else
            {
                // Unknown/malformed options and bare tokens can alter compiler inputs; never silently discard them.
                unsupportedSemantic.Add(rawToken);
            }
        }

        return new ProjectOptionSnapshot(
            raw,
            tokens,
            supported,
            unsupportedSemantic,
            unsupportedDiagnostic,
            defines.Distinct(StringComparer.Ordinal).ToArray(),
            checkedOverflow,
            indentationSyntax);
    }

    private static string NormalizeOptionPrefix(string token)
    {
        if (token.StartsWith("--", StringComparison.Ordinal))
            return "-" + token[2..];
        if (token.Length > 0 && token[0] == '/')
            return "-" + token[1..];
        return token;
    }

    private static bool TryReadString(
        string[] tokens,
        ref int index,
        string token,
        string[] names,
        out string value)
    {
        foreach (var name in names)
        {
            if (token.StartsWith(name + ":", StringComparison.Ordinal))
            {
                value = token[(name.Length + 1)..];
                return true;
            }
            if (token == name && index + 1 < tokens.Length)
            {
                value = tokens[++index];
                return true;
            }
        }
        value = string.Empty;
        return false;
    }

    private static bool IsBoolean(string token, string[] names) =>
        names.Any(name => TryReadBoolean(token, name, out _));

    private static bool TryReadBoolean(string token, string name, out bool value)
    {
        if (token == name || token == name + "+")
        {
            value = true;
            return true;
        }
        if (token == name + "-")
        {
            value = false;
            return true;
        }
        value = false;
        return false;
    }
}
