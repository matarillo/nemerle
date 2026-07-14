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
    /// - defines are the snapshot's full <see cref="NemerleProjectSnapshot.DefineConstants"/>,
    ///   which already unions the MSBuild DefineConstants property with any
    ///   "-define" values inside NemerleAdditionalOptions.  WP-M1 wired
    ///   Nemerle.Core.targets to pass DefineConstants to ncc as "-define:", so
    ///   the engine now applies the same set the build does (the earlier WP-L2/L3
    ///   gap where DefineConstants was reported as a warning is closed);
    /// - unsupported semantic/diagnostic options remain warnings.
    /// </summary>
    public static EngineWorkspaceInputs FromSnapshot(NemerleProjectSnapshot snapshot)
    {
        return new EngineWorkspaceInputs(
            snapshot.ProjectPath,
            snapshot.ProjectDirectory,
            snapshot.SourceFiles,
            snapshot.AssemblyReferences,
            snapshot.MacroReferences,
            snapshot.DefineConstants,
            snapshot.Options.CheckIntegerOverflow,
            snapshot.Options.IndentationSyntax,
            snapshot.Warnings);
    }
}
