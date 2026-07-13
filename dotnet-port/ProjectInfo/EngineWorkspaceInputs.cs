namespace Nemerle.ProjectInfo;

/// <summary>
/// The subset of a WP-L2 <see cref="NemerleProjectSnapshot"/> that the WP-L3
/// engine workspace actually applies, derived by a pure function so the
/// build-parity rules stay unit-testable without the compiler engine.
/// </summary>
public sealed record EngineWorkspaceInputs(
    string ProjectPath,
    string ProjectDirectory,
    IReadOnlyList<string> SourceFiles,
    IReadOnlyList<string> AssemblyReferences,
    IReadOnlyList<string> MacroReferences,
    IReadOnlyList<string> Defines,
    bool? CheckIntegerOverflow,
    bool IndentationSyntax,
    IReadOnlyList<string> Warnings)
{
    /// <summary>
    /// Maps a WP-L2 snapshot onto engine inputs with the same semantics the
    /// build gives ncc:
    /// - sources, assembly references, and macro-only references are used as-is
    ///   (macro references never join the assembly reference list);
    /// - defines come only from supported "-define" values inside
    ///   NemerleAdditionalOptions, because Nemerle.Core.targets does not pass
    ///   the MSBuild DefineConstants property to the compiler task (documented
    ///   WP-L2 build gap), so applying it here would diverge from dotnet build;
    /// - unsupported semantic/diagnostic options remain warnings.
    /// </summary>
    public static EngineWorkspaceInputs FromSnapshot(NemerleProjectSnapshot snapshot)
    {
        var warnings = new List<string>(snapshot.Warnings);
        if (snapshot.DefineConstants.Except(snapshot.Options.AdditionalDefines, StringComparer.Ordinal).Any())
        {
            warnings.Add(
                "MSBuild DefineConstants (" + string.Join(";", snapshot.DefineConstants) +
                ") are not applied to the analysis engine because Nemerle.Core.targets does not pass them to ncc; " +
                "use NemerleAdditionalOptions -define:... for symbols that must affect compilation.");
        }

        return new EngineWorkspaceInputs(
            snapshot.ProjectPath,
            snapshot.ProjectDirectory,
            snapshot.SourceFiles,
            snapshot.AssemblyReferences,
            snapshot.MacroReferences,
            snapshot.Options.AdditionalDefines,
            snapshot.Options.CheckIntegerOverflow,
            snapshot.Options.IndentationSyntax,
            warnings);
    }
}
