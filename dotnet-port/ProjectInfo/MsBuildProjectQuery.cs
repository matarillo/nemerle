namespace Nemerle.ProjectInfo;

public interface IProjectSnapshotLoader
{
    Task<NemerleProjectSnapshot> LoadAsync(ProjectQueryKey key, CancellationToken cancellationToken);
}

public sealed class MsBuildProjectQuery : IProjectSnapshotLoader
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(60);

    private readonly IProcessExecutor _processExecutor;
    private readonly TimeSpan _timeout;

    public MsBuildProjectQuery(IProcessExecutor? processExecutor = null, TimeSpan? timeout = null)
    {
        _processExecutor = processExecutor ?? new SystemProcessExecutor();
        _timeout = timeout ?? DefaultTimeout;
    }

    public async Task<NemerleProjectSnapshot> LoadAsync(
        ProjectQueryKey key,
        CancellationToken cancellationToken)
    {
        var spec = new ProcessSpec(
            key.DotNetExecutable,
            BuildArguments(key),
            Path.GetDirectoryName(key.ProjectPath)!,
            _timeout);
        var result = await _processExecutor.RunAsync(spec, cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            throw new ProjectQueryException(
                ProjectQueryErrorKind.NonZeroExit,
                $"MSBuild project query exited with code {result.ExitCode}.",
                result.StandardError,
                result.StandardOutput,
                result.ExitCode);
        }
        if (string.IsNullOrWhiteSpace(result.StandardOutput))
        {
            throw new ProjectQueryException(
                ProjectQueryErrorKind.EmptyOutput,
                "MSBuild project query returned empty stdout.",
                result.StandardError,
                exitCode: result.ExitCode);
        }

        return MsBuildJsonParser.Parse(key, result.StandardOutput);
    }

    public static IReadOnlyList<string> BuildArguments(ProjectQueryKey key)
    {
        var arguments = new List<string>
        {
            "msbuild",
            key.ProjectPath,
            "-nologo",
            "-verbosity:quiet",
            "-target:ResolveReferences",
            "-getItem:NemerleCompile,ReferencePath,NemerleMacroReference",
            "-getProperty:MSBuildProjectFullPath,MSBuildProjectDirectory,TargetFramework,Configuration,Platform,DefineConstants,NemerleAdditionalOptions",
            "-property:Configuration=" + key.Configuration,
            "-property:Platform=" + key.Platform,
        };
        if (key.TargetFramework.Length > 0)
            arguments.Add("-property:TargetFramework=" + key.TargetFramework);
        return arguments;
    }
}
