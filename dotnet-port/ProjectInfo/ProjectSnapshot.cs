namespace Nemerle.ProjectInfo;

public sealed record ProjectQueryKey(
    string DotNetExecutable,
    string ProjectPath,
    string Configuration,
    string Platform,
    string TargetFramework)
{
    public static ProjectQueryKey Create(
        string dotNetExecutable,
        string projectPath,
        string? configuration = null,
        string? platform = null,
        string? targetFramework = null) =>
        new(
            string.IsNullOrWhiteSpace(dotNetExecutable) ? "dotnet" : dotNetExecutable.Trim(),
            ProjectPathNormalizer.NormalizeFile(projectPath),
            string.IsNullOrWhiteSpace(configuration) ? "Debug" : configuration.Trim(),
            string.IsNullOrWhiteSpace(platform) ? "AnyCPU" : platform.Trim(),
            targetFramework?.Trim() ?? string.Empty);
}

public sealed record ProjectOptionSnapshot(
    string Raw,
    IReadOnlyList<string> Tokens,
    IReadOnlyList<string> SupportedSemanticOptions,
    IReadOnlyList<string> UnsupportedSemanticOptions,
    IReadOnlyList<string> UnsupportedDiagnosticOptions,
    IReadOnlyList<string> AdditionalDefines,
    bool? CheckIntegerOverflow,
    bool IndentationSyntax);

public sealed record NemerleProjectSnapshot(
    ProjectQueryKey QueryKey,
    string ProjectPath,
    string ProjectDirectory,
    string Configuration,
    string Platform,
    string TargetFramework,
    IReadOnlyList<string> SourceFiles,
    IReadOnlyList<string> AssemblyReferences,
    IReadOnlyList<string> MacroReferences,
    IReadOnlyList<string> DefineConstants,
    ProjectOptionSnapshot Options,
    IReadOnlyList<string> Warnings,
    DateTimeOffset LoadedAtUtc,
    /// <summary>The compiler layout this project builds with ($(NccLayoutDir)): the repository's
    /// dist\ncc, or a versioned folder in the NuGet global packages folder for a project using
    /// the Nemerle Sdk package. Empty when the project does not define it. Used to compare the
    /// toolchain's generation against the language server's own (WP-M6, 29-devenv2-plan.md
    /// §6.8); mixed generations otherwise surface as FileLoadException, because Nemerle assembly
    /// versions track the source generation.</summary>
    string NccLayoutDir = "");

public static class ProjectPathNormalizer
{
    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    /// <summary>Case sensitivity used to compare normalized paths on this platform.</summary>
    public static StringComparer Comparer => PathComparer;

    public static string NormalizeFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("A path must not be empty.", nameof(path));

        var fullPath = Path.GetFullPath(path.Trim());
        if (OperatingSystem.IsWindows() && fullPath.Length >= 2 && fullPath[1] == ':')
            fullPath = char.ToUpperInvariant(fullPath[0]) + fullPath[1..];
        return fullPath;
    }

    public static string NormalizeDirectory(string path) =>
        Path.TrimEndingDirectorySeparator(NormalizeFile(path));

    public static IReadOnlyList<string> NormalizeDistinct(IEnumerable<string> paths) =>
        paths.Select(NormalizeFile).Distinct(PathComparer).ToArray();
}
