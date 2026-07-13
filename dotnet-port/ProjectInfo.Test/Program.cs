using Nemerle.ProjectInfo;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        try
        {
            await ParserTests();
            await ErrorTests();
            await ProviderTests();
            if (args.Contains("--integration", StringComparer.Ordinal))
                await SampleIntegrationTests();
            Console.WriteLine(args.Contains("--integration", StringComparer.Ordinal)
                ? "PASS project info unit + sample integration tests"
                : "PASS project info unit tests");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
    }

    private static Task ParserTests()
    {
        var fixture = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "Fixtures", "msbuild-mixed.json"));
        var key = ProjectQueryKey.Create("dotnet", @"C:\repo\App\App.nproj");
        var snapshot = MsBuildJsonParser.Parse(key, fixture);
        Equal(1, snapshot.SourceFiles.Count, "case-insensitive duplicate source normalization");
        Equal(2, snapshot.AssemblyReferences.Count, "framework facade exclusion");
        True(snapshot.AssemblyReferences.Any(path => path.EndsWith("MathLib.dll", StringComparison.OrdinalIgnoreCase)), "project reference retained");
        True(snapshot.AssemblyReferences.Any(path => path.EndsWith("Newtonsoft.Json.dll", StringComparison.OrdinalIgnoreCase)), "package reference retained");
        True(snapshot.AssemblyReferences.All(path => !path.EndsWith("System.Runtime.dll", StringComparison.OrdinalIgnoreCase)), "framework facade absent");
        Equal(1, snapshot.MacroReferences.Count, "macro reference retained separately");
        True(snapshot.AssemblyReferences.All(path => !path.EndsWith("Macros.dll", StringComparison.OrdinalIgnoreCase)), "macro-only reference absent from assemblies");
        True(snapshot.DefineConstants.SequenceEqual(["NET", "TRACE", "FEATURE", "SECOND"]), "property and supported additional defines");
        Equal(false, snapshot.Options.CheckIntegerOverflow, "checked option");
        True(snapshot.Options.IndentationSyntax, "indentation option");
        True(snapshot.Options.UnsupportedSemanticOptions.Contains("-disable-keyword:foo"), "unsupported semantic option warning");
        Equal(2, snapshot.Warnings.Count, "semantic and diagnostic warning count");

        Throws(ProjectQueryErrorKind.InvalidJson, () => MsBuildJsonParser.Parse(key, "not json"));
        Throws(ProjectQueryErrorKind.MissingField, () => MsBuildJsonParser.Parse(key, "{\"Properties\":{},\"Items\":{}}"));
        var missingMetadata = fixture.Replace("\"FullPath\": \"C:\\\\repo\\\\App\\\\Program.n\"", "\"NotFullPath\": \"Program.n\"", StringComparison.Ordinal);
        Throws(ProjectQueryErrorKind.MissingField, () => MsBuildJsonParser.Parse(key, missingMetadata));
        return Task.CompletedTask;
    }

    private static async Task ErrorTests()
    {
        var key = ProjectQueryKey.Create("missing-dotnet-for-nemerle-project-info-test", @"C:\repo\App\App.nproj");
        var query = new MsBuildProjectQuery(new SystemProcessExecutor(), TimeSpan.FromSeconds(2));
        await ThrowsAsync(ProjectQueryErrorKind.StartFailure, () => query.LoadAsync(key, CancellationToken.None));

        var empty = new MsBuildProjectQuery(new ResultExecutor(new ProcessResult(0, "", "stderr")));
        await ThrowsAsync(ProjectQueryErrorKind.EmptyOutput, () => empty.LoadAsync(key, CancellationToken.None));
        var failed = new MsBuildProjectQuery(new ResultExecutor(new ProcessResult(7, "ignored", "failure")));
        await ThrowsAsync(ProjectQueryErrorKind.NonZeroExit, () => failed.LoadAsync(key, CancellationToken.None));
        var invalid = new MsBuildProjectQuery(new ResultExecutor(new ProcessResult(0, "{", "")));
        await ThrowsAsync(ProjectQueryErrorKind.InvalidJson, () => invalid.LoadAsync(key, CancellationToken.None));
        var timedOut = new MsBuildProjectQuery(new ThrowingExecutor(ProjectQueryErrorKind.Timeout));
        await ThrowsAsync(ProjectQueryErrorKind.Timeout, () => timedOut.LoadAsync(key, CancellationToken.None));

        var spec = MsBuildProjectQuery.BuildArguments(key);
        Equal("msbuild", spec[0], "dotnet subcommand");
        True(spec.Contains("-target:ResolveReferences"), "target precedes post-target query");
        True(spec.All(argument => !argument.Contains('"')), "ArgumentList values are not shell-quoted");
    }

    private static async Task ProviderTests()
    {
        var loader = new CountingLoader();
        await using var provider = new ProjectInfoProvider(loader);
        var firstKey = ProjectQueryKey.Create("dotnet", @"C:\repo\One\One.nproj");
        var secondKey = ProjectQueryKey.Create("dotnet", @"C:\repo\Two\Two.nproj");

        var first = provider.GetSnapshotAsync(firstKey, false, CancellationToken.None);
        var joined = provider.GetSnapshotAsync(firstKey, false, CancellationToken.None);
        await Task.WhenAll(first, joined);
        Equal(1, loader.CallCount, "same-key single-flight");

        _ = await provider.GetSnapshotAsync(firstKey, false, CancellationToken.None);
        Equal(1, loader.CallCount, "cache hit");
        _ = await provider.GetSnapshotAsync(firstKey, true, CancellationToken.None);
        Equal(2, loader.CallCount, "explicit reload");

        await Task.WhenAll(
            provider.GetSnapshotAsync(firstKey, true, CancellationToken.None),
            provider.GetSnapshotAsync(secondKey, true, CancellationToken.None));
        Equal(1, loader.MaximumConcurrency, "global process serialization");

        var cancellingLoader = new CancellingLoader();
        await using var cancellingProvider = new ProjectInfoProvider(cancellingLoader);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        await ThrowsAsync(
            ProjectQueryErrorKind.Cancelled,
            () => cancellingProvider.GetSnapshotAsync(firstKey, false, cancellation.Token));

        var disposalProvider = new ProjectInfoProvider(new CancellingLoader());
        var pendingAtDispose = disposalProvider.GetSnapshotAsync(firstKey, false, CancellationToken.None);
        await Task.Delay(20);
        await disposalProvider.DisposeAsync();
        await ThrowsAsync(ProjectQueryErrorKind.Cancelled, () => pendingAtDispose);
    }

    private static async Task SampleIntegrationTests()
    {
        var root = FindRepositoryRoot();
        var projects = new[]
        {
            Path.Combine(root, "dotnet-port", "samples", "HelloCore", "HelloCore.nproj"),
            Path.Combine(root, "dotnet-port", "samples", "RefDemo", "App", "App.nproj"),
            Path.Combine(root, "dotnet-port", "samples", "PackageReference", "PackageReference.nproj"),
            Path.Combine(root, "dotnet-port", "samples", "Sokoban", "Sokoban", "Sokoban.nproj"),
        };
        await using var provider = new ProjectInfoProvider();
        var snapshots = new List<NemerleProjectSnapshot>();
        foreach (var project in projects)
            snapshots.Add(await provider.GetSnapshotAsync(ProjectQueryKey.Create("dotnet", project), true, CancellationToken.None));

        True(snapshots[0].SourceFiles.Any(path => path.EndsWith("hello.n", StringComparison.OrdinalIgnoreCase)), "HelloCore source");
        Equal("Debug", snapshots[0].Configuration, "HelloCore configuration");
        True(snapshots[0].DefineConstants.Contains("NET10_0"), "HelloCore defines");
        True(snapshots[1].AssemblyReferences.Any(path => path.EndsWith("MathLib.dll", StringComparison.OrdinalIgnoreCase)), "RefDemo project output");
        True(snapshots[2].AssemblyReferences.Any(path => path.EndsWith("Newtonsoft.Json.dll", StringComparison.OrdinalIgnoreCase)), "PackageReference output");
        True(snapshots[2].AssemblyReferences.All(path => !path.Contains("Microsoft.NETCore.App.Ref", StringComparison.OrdinalIgnoreCase)), "Package fixture framework facades excluded");
        True(snapshots[3].MacroReferences.Any(path => path.EndsWith("SokobanMacros.dll", StringComparison.OrdinalIgnoreCase)), "Sokoban macro output");
        True(snapshots[3].AssemblyReferences.All(path => !path.EndsWith("SokobanMacros.dll", StringComparison.OrdinalIgnoreCase)), "Sokoban macro output not assembly reference");
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(Environment.CurrentDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, ".git")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root was not found.");
    }

    private static void True(bool condition, string description)
    {
        if (!condition)
            throw new InvalidOperationException("Assertion failed: " + description);
    }

    private static void Equal<T>(T expected, T actual, string description)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"Assertion failed ({description}): expected {expected}, got {actual}.");
    }

    private static void Throws(ProjectQueryErrorKind kind, Action action)
    {
        try { action(); }
        catch (ProjectQueryException ex) when (ex.Kind == kind) { return; }
        throw new InvalidOperationException($"Expected ProjectQueryException({kind}).");
    }

    private static async Task ThrowsAsync(ProjectQueryErrorKind kind, Func<Task> action)
    {
        try { await action(); }
        catch (ProjectQueryException ex) when (ex.Kind == kind) { return; }
        throw new InvalidOperationException($"Expected ProjectQueryException({kind}).");
    }

    private sealed class ResultExecutor(ProcessResult result) : IProcessExecutor
    {
        public Task<ProcessResult> RunAsync(ProcessSpec spec, CancellationToken cancellationToken) => Task.FromResult(result);
    }

    private sealed class ThrowingExecutor(ProjectQueryErrorKind kind) : IProcessExecutor
    {
        public Task<ProcessResult> RunAsync(ProcessSpec spec, CancellationToken cancellationToken) =>
            Task.FromException<ProcessResult>(new ProjectQueryException(kind, "synthetic process failure"));
    }

    private sealed class CountingLoader : IProjectSnapshotLoader
    {
        private int _concurrency;
        public int CallCount { get; private set; }
        public int MaximumConcurrency { get; private set; }

        public async Task<NemerleProjectSnapshot> LoadAsync(ProjectQueryKey key, CancellationToken cancellationToken)
        {
            CallCount++;
            var concurrency = Interlocked.Increment(ref _concurrency);
            MaximumConcurrency = Math.Max(MaximumConcurrency, concurrency);
            try
            {
                await Task.Delay(40, cancellationToken);
                var json = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "Fixtures", "msbuild-mixed.json"));
                return MsBuildJsonParser.Parse(key, json);
            }
            finally
            {
                Interlocked.Decrement(ref _concurrency);
            }
        }
    }

    private sealed class CancellingLoader : IProjectSnapshotLoader
    {
        public async Task<NemerleProjectSnapshot> LoadAsync(ProjectQueryKey key, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException();
        }
    }
}
