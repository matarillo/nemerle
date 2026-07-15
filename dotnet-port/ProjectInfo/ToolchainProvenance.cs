using System.Reflection;
using System.Text.Json;

namespace Nemerle.ProjectInfo;

/// <summary>
/// Provenance of one set of Nemerle bits: which commit they were built from, and which assembly
/// version consumers bind against.
/// </summary>
/// <param name="Commit">Full commit SHA recorded at pack time, or empty if unknown.</param>
/// <param name="Describe">`git describe --long --always --dirty` at pack time, or empty.</param>
/// <param name="AssemblyVersion">Version of Nemerle.dll, the identity that actually governs
/// assembly loading. Empty when it could not be read.</param>
/// <param name="Source">Where this record came from, for log messages.</param>
public sealed record NemerleProvenance(
    string Commit,
    string Describe,
    string AssemblyVersion,
    string Source)
{
    public static readonly NemerleProvenance Unknown = new("", "", "", "");

    public bool IsKnown => AssemblyVersion.Length > 0 || Commit.Length > 0;

    /// <summary>Short, log-friendly rendering: "1.2.0.601 (5b5e0e4f6, dist/ncc)".</summary>
    public string Describe_Short()
    {
        var version = AssemblyVersion.Length > 0 ? AssemblyVersion : "unknown version";
        var commit = Commit.Length >= 9 ? Commit[..9] : Commit;
        var detail = string.Join(", ", new[] { commit, Source }.Where(static s => s.Length > 0));
        return detail.Length > 0 ? $"{version} ({detail})" : version;
    }
}

/// <summary>
/// Reads and compares the provenance of the two independently-packed halves of a Nemerle
/// installation: the language server's own bundled assemblies (server\bundle-info.json, written
/// by pack-server.ps1 since WP-L4) and the toolchain a project builds with ($(NccLayoutDir)'s
/// ncc-info.json, written by pack-tool.ps1 since WP-M6).
///
/// Why this exists (29-devenv2-plan.md §6.8): Nemerle assembly versions derive from the source
/// generation, so an editor session whose server was built from one commit and whose project
/// builds with a toolchain from another fails at load time with FileLoadException - a symptom
/// that says nothing about the cause. Comparing the two records turns that into one sentence
/// naming both generations.
/// </summary>
public static class ToolchainProvenance
{
    /// <summary>Reads a pack-tool.ps1 / pack-server.ps1 provenance record next to the given
    /// assemblies. Never throws: unknown provenance disables the check rather than breaking a
    /// session over a diagnostic aid.</summary>
    public static NemerleProvenance Read(string directory, string infoFileName, string source)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            return NemerleProvenance.Unknown;

        var commit = "";
        var describe = "";
        var infoPath = Path.Combine(directory, infoFileName);
        if (File.Exists(infoPath))
        {
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(infoPath));
                var root = document.RootElement;
                if (root.TryGetProperty("commit", out var c) && c.ValueKind == JsonValueKind.String)
                    commit = c.GetString() ?? "";
                if (root.TryGetProperty("describe", out var d) && d.ValueKind == JsonValueKind.String)
                    describe = d.GetString() ?? "";
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                // Fall through: the assembly version below is the part that actually matters.
            }
        }

        // Read the version off Nemerle.dll rather than trusting the json's record of it: the
        // assembly is what a mismatch is ABOUT, and a hand-edited or stale json should not be
        // able to claim otherwise.
        var assemblyVersion = ReadNemerleAssemblyVersion(directory);
        if (commit.Length == 0 && describe.Length == 0 && assemblyVersion.Length == 0)
            return NemerleProvenance.Unknown;

        return new NemerleProvenance(commit, describe, assemblyVersion, source);
    }

    private static string ReadNemerleAssemblyVersion(string directory)
    {
        var path = Path.Combine(directory, "Nemerle.dll");
        if (!File.Exists(path))
            return "";
        try
        {
            return AssemblyName.GetAssemblyName(path).Version?.ToString() ?? "";
        }
        catch (Exception ex) when (ex is BadImageFormatException or IOException or UnauthorizedAccessException)
        {
            return "";
        }
    }

    /// <summary>
    /// Compares the server's bits against the toolchain a project builds with, and returns the
    /// user-facing warning text, or null when there is nothing to say.
    ///
    /// Deliberately conservative - it warns only on a positive disagreement between two KNOWN
    /// assembly versions. Unknown provenance on either side (a project built by other means, a
    /// server run straight from bin\, a layout packed before WP-M6) means "no evidence", not
    /// "mismatch"; warning there would train users to ignore the warning.
    /// </summary>
    public static string? DescribeMismatch(NemerleProvenance server, NemerleProvenance toolchain)
    {
        if (server.AssemblyVersion.Length == 0 || toolchain.AssemblyVersion.Length == 0)
            return null;
        if (string.Equals(server.AssemblyVersion, toolchain.AssemblyVersion, StringComparison.Ordinal))
            return null;

        return
            $"Nemerle toolchain/language server version mismatch: this project builds with {toolchain.Describe_Short()}, " +
            $"but the language server is {server.Describe_Short()}. Analysis results may disagree with `dotnet build`, " +
            "and loading the project's assemblies can fail. Rebuild or repack both from the same commit.";
    }
}
