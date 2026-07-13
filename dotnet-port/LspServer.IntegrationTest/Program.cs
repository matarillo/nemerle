using System.Diagnostics;
using System.Text.Json;

namespace Nemerle.LanguageServer.IntegrationTest;

/// <summary>
/// Raw stdio LSP integration scenarios for the WP-L3 project-aware engine
/// workspace, plus the WP-K/WP-L1 loose-file regression flow.
/// </summary>
internal static class Program
{
    private static string _serverDll = "";
    private static string _repoRoot = "";

    public static async Task<int> Main(string[] args)
    {
        _repoRoot = FindRepositoryRoot();
        _serverDll = args.Length > 0
            ? Path.GetFullPath(args[0])
            : Path.Combine(_repoRoot, "dotnet-port", "LspServer", "bin", "Release", "net10.0",
                "Nemerle.LanguageServer.dll");
        if (!File.Exists(_serverDll))
        {
            Console.Error.WriteLine($"Server assembly not found: {_serverDll}");
            return 2;
        }

        try
        {
            EnsureFixturesBuilt();
            await RunScenarioAsync("HelloCore apply + loose-file regression", HelloCoreAndLooseRegressionAsync);
            await RunScenarioAsync("RefDemo ProjectReference project-aware analysis", RefDemoProjectAwareAsync);
            await RunScenarioAsync("PackageReference resolved assembly in engine workspace", PackageReferenceProjectAwareAsync);
            await RunScenarioAsync("Sokoban macro-only reference and cross-source resolution", SokobanMacroWorkspaceAsync);
            await RunScenarioAsync("Buffer override, close revert, stale suppression, source removal, failure recovery",
                BufferDiskCloseStaleRemovalAsync);
            Console.WriteLine("PASS all WP-L3 LSP integration scenarios");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
    }

    private static async Task RunScenarioAsync(string name, Func<LspTestClient, Task> scenario)
    {
        Console.WriteLine($"--- {name}");
        var stopwatch = Stopwatch.StartNew();
        await using var client = await LspTestClient.StartAsync(_serverDll, _repoRoot);
        string stderr;
        try
        {
            await scenario(client);
            await client.ShutdownAsync();
            stderr = await client.DumpServerStderrAsync();
        }
        catch (Exception)
        {
            var failureStderr = await client.DumpServerStderrAsync();
            if (!string.IsNullOrWhiteSpace(failureStderr))
                Console.Error.WriteLine("--- server stderr ---\n" + failureStderr);
            throw;
        }

        foreach (var line in stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (line.StartsWith("nemerle project query finished", StringComparison.Ordinal) ||
                line.StartsWith("nemerle engine rebuild finished", StringComparison.Ordinal))
                Console.WriteLine($"    trace: {line}");
        }

        Console.WriteLine($"    ok ({stopwatch.Elapsed.TotalSeconds:F1} s)");
    }

    // ----- Scenario 1: HelloCore snapshot applied to the engine + loose-file regression -----

    private static async Task HelloCoreAndLooseRegressionAsync(LspTestClient client)
    {
        var helloProject = Sample("HelloCore", "HelloCore.nproj");
        var helloSource = Sample("HelloCore", "hello.n");

        var beforeLoad = client.Mark();
        var result = await LoadProjectAsync(client, helloProject);
        AssertLoadedAndApplied(result, expectedSources: 1);
        if (!result.GetProperty("sourceFiles")[0].GetString()!.EndsWith("hello.n", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("HelloCore sourceFiles did not contain hello.n.");

        // The closed project source becomes part of the engine workspace and
        // gets an error-free diagnostics publish without an LSP version.
        _ = await client.WaitForAsync(
            message => IsPublishFor(message, helloSource, out var p) && VersionOf(p) is null && !HasError(p),
            beforeLoad,
            "error-free unversioned diagnostics for the closed project source hello.n");

        // WP-K/WP-L1 loose-file flow continues to work while the HelloCore
        // workspace stays applied.
        var testDirectory = CreateTempDirectory("loose");
        try
        {
            // A missing project stays a typed, recoverable result.
            var missing = await LoadProjectAsync(client, Path.Combine(testDirectory, "Missing.nproj"));
            if (missing.GetProperty("state").GetString() != "error" ||
                missing.GetProperty("errorKind").GetString() != "NonZeroExit" ||
                missing.GetProperty("appliedToEngine").GetBoolean())
            {
                throw new InvalidDataException("Missing project did not return the expected recoverable error result: " + missing);
            }

            var loosePath = Path.Combine(testDirectory, "Broken.n");
            var looseUri = new Uri(loosePath).AbsoluteUri;
            const string brokenSource = """
                module Broken
                {
                  Main() : void
                  {
                    def value : int = "wrong";
                  }
                }
                """;
            const string fixedSource = """
                module Broken
                {
                  Main() : void
                  {
                    def value : int = 1;
                    System.Console.WriteLine(value);
                  }
                }
                """;
            await File.WriteAllTextAsync(loosePath, brokenSource);

            var mark = client.Mark();
            await DidOpenAsync(client, looseUri, brokenSource, 1);
            var errorPublish = await client.WaitForAsync(
                message => IsPublishFor(message, looseUri, out var p) && VersionOf(p) == 1 && HasError(p),
                mark,
                "an error diagnostic for the loose Broken.n");
            var start = FirstDiagnostic(errorPublish).GetProperty("range").GetProperty("start");
            if (start.GetProperty("line").GetInt32() < 0 || start.GetProperty("character").GetInt32() < 0)
                throw new InvalidDataException("Diagnostic range was not converted to zero-based LSP coordinates.");

            mark = client.Mark();
            await DidChangeAsync(client, looseUri, fixedSource, 2);
            _ = await client.WaitForAsync(
                message => IsPublishFor(message, looseUri, out var p) && VersionOf(p) == 2 && CountOf(p) == 0,
                mark,
                "empty version-2 diagnostics after the unsaved fix");

            mark = client.Mark();
            await DidCloseAsync(client, looseUri);
            _ = await client.WaitForAsync(
                message => IsPublishFor(message, looseUri, out var p) && VersionOf(p) is null && CountOf(p) == 0,
                mark,
                "unversioned empty diagnostics after closing the loose file");
        }
        finally
        {
            TryDeleteDirectory(testDirectory);
        }
    }

    // ----- Scenario 2: RefDemo ProjectReference -----

    private static async Task RefDemoProjectAwareAsync(LspTestClient client)
    {
        var appProject = Sample("RefDemo", "App", "App.nproj");
        var programSource = Sample("RefDemo", "App", "Program.n");
        var programUri = new Uri(programSource).AbsoluteUri;
        var programText = await File.ReadAllTextAsync(programSource);

        // Negative control: without the project, MathLib's Calc is unbound.
        var mark = client.Mark();
        await DidOpenAsync(client, programUri, programText, 1);
        _ = await client.WaitForAsync(
            message => IsPublishFor(message, programUri, out var p) && VersionOf(p) == 1 && HasError(p),
            mark,
            "loose-file unbound-name errors in Program.n before the project is applied");

        mark = client.Mark();
        var result = await LoadProjectAsync(client, appProject);
        AssertLoadedAndApplied(result, expectedSources: 1);
        if (!ContainsPathEndingWith(result.GetProperty("assemblyReferences"), "MathLib.dll"))
            throw new InvalidDataException("RefDemo assembly references did not include MathLib.dll: " + result);

        // The same open buffer becomes error-free once the ProjectReference
        // output is applied to the engine workspace.
        _ = await client.WaitForAsync(
            message => IsPublishFor(message, programUri, out var p) && VersionOf(p) == 1 && !HasError(p),
            mark,
            "error-free diagnostics for Program.n after applying App.nproj");
    }

    // ----- Scenario 3: PackageReference -----

    private static async Task PackageReferenceProjectAwareAsync(LspTestClient client)
    {
        var project = Sample("PackageReference", "PackageReference.nproj");
        var source = Sample("PackageReference", "PackageSample.n");
        var sourceUri = new Uri(source).AbsoluteUri;

        var result = await LoadProjectAsync(client, project);
        AssertLoadedAndApplied(result, expectedSources: 1);
        var references = result.GetProperty("assemblyReferences");
        if (!ContainsPathEndingWith(references, "Newtonsoft.Json.dll"))
            throw new InvalidDataException("PackageReference assembly references did not include Newtonsoft.Json.dll.");
        if (ContainsPathEndingWith(references, "System.Runtime.dll"))
            throw new InvalidDataException("Framework facade leaked into the applied assembly references.");

        // An unsaved buffer using the package type must analyze cleanly.
        const string bufferText = """
            using Newtonsoft.Json;

            namespace PackageReferenceSample
            {
              public module Marker
              {
                public Name : string { get { JsonConvert.SerializeObject(42) } }
              }
            }
            """;
        var mark = client.Mark();
        await DidOpenAsync(client, sourceUri, bufferText, 1);
        _ = await client.WaitForAsync(
            message => IsPublishFor(message, sourceUri, out var p) && VersionOf(p) == 1 && !HasError(p),
            mark,
            "error-free diagnostics for an unsaved buffer using Newtonsoft.Json");
    }

    // ----- Scenario 4: Sokoban macro-only reference -----

    private static async Task SokobanMacroWorkspaceAsync(LspTestClient client)
    {
        var project = Sample("Sokoban", "Sokoban", "Sokoban.nproj");
        var mainSource = Sample("Sokoban", "Sokoban", "main.n");
        var macroConsumerSource = Sample("Sokoban", "Sokoban", "sokoban.n");
        var mainUri = new Uri(mainSource).AbsoluteUri;

        var beforeLoad = client.Mark();
        var result = await LoadProjectAsync(client, project);
        AssertLoadedAndApplied(result, expectedSources: 5);
        if (!ContainsPathEndingWith(result.GetProperty("macroReferences"), "SokobanMacros.dll"))
            throw new InvalidDataException("Sokoban macro references did not include SokobanMacros.dll.");
        if (ContainsPathEndingWith(result.GetProperty("assemblyReferences"), "SokobanMacros.dll"))
            throw new InvalidDataException("Macro-only SokobanMacros.dll leaked into the assembly references.");

        // sokoban.n (closed) uses the NextMove/UseTunnelMacro macros; an
        // error-free publish proves the macro assembly was loaded as a
        // compile-time plugin in the engine workspace.
        _ = await client.WaitForAsync(
            message => IsPublishFor(message, macroConsumerSource, out var p) && VersionOf(p) is null && !HasError(p),
            beforeLoad,
            "error-free diagnostics for the closed macro-using sokoban.n");

        // main.n resolves MapCollection/SMap/TreeSearch/LocalSearch declared in
        // the project's other sources.
        var mark = client.Mark();
        var mainText = await File.ReadAllTextAsync(mainSource);
        await DidOpenAsync(client, mainUri, mainText, 1);
        _ = await client.WaitForAsync(
            message => IsPublishFor(message, mainUri, out var p) && VersionOf(p) == 1 && !HasError(p),
            mark,
            "error-free diagnostics for main.n with cross-source project symbols");
    }

    // ----- Scenario 5: buffer/disk precedence, close revert, stale suppression, removal, recovery -----

    private static async Task BufferDiskCloseStaleRemovalAsync(LspTestClient client)
    {
        var testDirectory = CreateTempDirectory("project");
        try
        {
            var projectPath = Path.Combine(testDirectory, "Temp.nproj");
            var brokenPath = Path.Combine(testDirectory, "broken.n");
            var otherPath = Path.Combine(testDirectory, "other.n");
            var brokenUri = new Uri(brokenPath).AbsoluteUri;
            var coreTargets = Path.Combine(_repoRoot, "dotnet-port", "msbuild", "Nemerle.Core.targets");

            const string brokenDiskSource = """
                module TempBroken
                {
                  public Run() : void
                  {
                    def value : int = "wrong";
                    System.Console.WriteLine(value);
                  }
                }
                """;
            const string fixedBufferSource = """
                module TempBroken
                {
                  public Run() : void
                  {
                    def value : int = TempOther.Helper();
                    System.Console.WriteLine(value);
                  }
                }
                """;
            const string brokenAgainBufferSource = """
                module TempBroken
                {
                  public Run() : void
                  {
                    def value : int = "still wrong";
                    System.Console.WriteLine(value);
                  }
                }
                """;
            const string otherDiskSource = """
                module TempOther
                {
                  public Helper() : int { 42 }
                }
                """;

            await File.WriteAllTextAsync(brokenPath, brokenDiskSource);
            await File.WriteAllTextAsync(otherPath, otherDiskSource);
            await File.WriteAllTextAsync(projectPath, TempProjectXml(coreTargets, ["broken.n", "other.n"]));
            RunDotnet($"restore \"{projectPath}\"", testDirectory);

            // 5a. Applying the snapshot surfaces the closed broken.n disk error.
            var mark = client.Mark();
            var result = await LoadProjectAsync(client, projectPath);
            AssertLoadedAndApplied(result, expectedSources: 2);
            _ = await client.WaitForAsync(
                message => IsPublishFor(message, brokenUri, out var p) && VersionOf(p) is null && HasError(p),
                mark,
                "disk-backed error diagnostics for the closed project source broken.n");

            // 5b. The open buffer overrides disk: the disk file stays broken,
            // but a fixed unsaved buffer (which also uses the other project
            // source) analyzes cleanly.
            mark = client.Mark();
            await DidOpenAsync(client, brokenUri, brokenDiskSource, 1);
            _ = await client.WaitForAsync(
                message => IsPublishFor(message, brokenUri, out var p) && VersionOf(p) == 1 && HasError(p),
                mark,
                "version-1 error diagnostics for the opened broken.n");

            mark = client.Mark();
            await DidChangeAsync(client, brokenUri, fixedBufferSource, 2);
            _ = await client.WaitForAsync(
                message => IsPublishFor(message, brokenUri, out var p) && VersionOf(p) == 2 && CountOf(p) == 0,
                mark,
                "empty version-2 diagnostics for the fixed unsaved buffer (disk still broken)");
            if (await File.ReadAllTextAsync(brokenPath) != brokenDiskSource)
                throw new InvalidDataException("The disk file changed; the buffer-over-disk proof is invalid.");

            // 5c. Stale suppression: version 3 (broken) is immediately
            // superseded by version 4 (fixed).  After the version-4 empty
            // publish, no error diagnostics may appear for this document.
            mark = client.Mark();
            await DidChangeAsync(client, brokenUri, brokenAgainBufferSource, 3);
            await DidChangeAsync(client, brokenUri, fixedBufferSource, 4);
            _ = await client.WaitForAsync(
                message => IsPublishFor(message, brokenUri, out var p) && VersionOf(p) == 4 && CountOf(p) == 0,
                mark,
                "empty version-4 diagnostics after the rapid change sequence");
            var afterCleanMark = client.Mark();
            await client.AssertQuietAsync(
                message => IsPublishFor(message, brokenUri, out var p) && HasError(p),
                afterCleanMark,
                TimeSpan.FromSeconds(3),
                "stale error diagnostics after the version-4 empty publish");

            // 5d. Closing reverts to disk-backed content: the buffer's clean
            // state is cleared and the broken disk content is re-analyzed.
            mark = client.Mark();
            await DidCloseAsync(client, brokenUri);
            _ = await client.WaitForAsync(
                message => IsPublishFor(message, brokenUri, out var p) && VersionOf(p) is null && CountOf(p) == 0,
                mark,
                "unversioned empty diagnostics right after closing the project source");
            _ = await client.WaitForAsync(
                message => IsPublishFor(message, brokenUri, out var p) && VersionOf(p) is null && HasError(p),
                mark,
                "disk-backed error diagnostics after the close reverted to disk content");

            // 5e. Removing the source from the project clears its diagnostics.
            await File.WriteAllTextAsync(projectPath, TempProjectXml(coreTargets, ["other.n"]));
            mark = client.Mark();
            result = await LoadProjectAsync(client, projectPath);
            AssertLoadedAndApplied(result, expectedSources: 1);
            _ = await client.WaitForAsync(
                message => IsPublishFor(message, brokenUri, out var p) && CountOf(p) == 0,
                mark,
                "cleared diagnostics for the source removed from the project");

            // 5f. Failure recovery: an unparsable project is a typed error and
            // the engine workspace/server keep working.
            var badProjectPath = Path.Combine(testDirectory, "Bad.nproj");
            await File.WriteAllTextAsync(badProjectPath, "<Project Sdk=\"Microsoft.NET.Sdk\"><Broken></Project>");
            var badResult = await LoadProjectAsync(client, badProjectPath);
            if (badResult.GetProperty("state").GetString() != "error" ||
                badResult.GetProperty("appliedToEngine").GetBoolean())
            {
                throw new InvalidDataException("The unparsable project did not return a recoverable error: " + badResult);
            }

            var loosePath = Path.Combine(testDirectory, "AfterFailure.n");
            var looseUri = new Uri(loosePath).AbsoluteUri;
            const string looseBroken = """
                module AfterFailure
                {
                  Main() : void
                  {
                    def value : int = "wrong";
                  }
                }
                """;
            mark = client.Mark();
            await DidOpenAsync(client, looseUri, looseBroken, 1);
            _ = await client.WaitForAsync(
                message => IsPublishFor(message, looseUri, out var p) && VersionOf(p) == 1 && HasError(p),
                mark,
                "diagnostics for a loose file after the failed project reload");
        }
        finally
        {
            TryDeleteDirectory(testDirectory);
        }
    }

    // ----- helpers -----

    private static string TempProjectXml(string coreTargets, IReadOnlyList<string> sources)
    {
        var items = string.Join(
            Environment.NewLine,
            sources.Select(source => $"    <NemerleCompile Include=\"{source}\" />"));
        return $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <OutputType>Library</OutputType>
                <TargetFramework>net10.0</TargetFramework>
                <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
                <UseAppHost>false</UseAppHost>
                <GenerateDependencyFile>false</GenerateDependencyFile>
              </PropertyGroup>
              <ItemGroup>
            {items}
              </ItemGroup>
              <Import Project="{coreTargets}" />
            </Project>
            """;
    }

    private static async Task<JsonElement> LoadProjectAsync(LspTestClient client, string projectPath)
    {
        var response = await client.RequestAsync("nemerle/projectInfo/load", new
        {
            projectPath,
            configuration = "Debug",
            platform = "AnyCPU",
            targetFramework = "",
            dotNetExecutable = "dotnet",
            forceReload = true,
        });
        if (response.TryGetProperty("error", out var error))
            throw new InvalidDataException("Project-info custom request failed: " + error);
        return response.GetProperty("result");
    }

    private static void AssertLoadedAndApplied(JsonElement result, int expectedSources)
    {
        if (result.GetProperty("state").GetString() != "loaded" ||
            result.GetProperty("sourceCount").GetInt32() != expectedSources ||
            !result.GetProperty("appliedToEngine").GetBoolean() ||
            result.GetProperty("sourceFiles").GetArrayLength() != expectedSources)
        {
            throw new InvalidDataException("Project snapshot was not loaded+applied as expected: " + result);
        }
    }

    private static Task DidOpenAsync(LspTestClient client, string uri, string text, int version) =>
        client.NotifyAsync("textDocument/didOpen", new
        {
            textDocument = new { uri, languageId = "nemerle", version, text },
        });

    private static Task DidChangeAsync(LspTestClient client, string uri, string text, int version) =>
        client.NotifyAsync("textDocument/didChange", new
        {
            textDocument = new { uri, version },
            contentChanges = new[] { new { text } },
        });

    private static Task DidCloseAsync(LspTestClient client, string uri) =>
        client.NotifyAsync("textDocument/didClose", new { textDocument = new { uri } });

    private static bool IsPublishFor(JsonElement message, string pathOrUri, out JsonElement parameters)
    {
        parameters = default;
        return message.TryGetProperty("method", out var method) &&
               method.GetString() == "textDocument/publishDiagnostics" &&
               message.TryGetProperty("params", out parameters) &&
               AreEquivalentDocumentUris(
                   parameters.GetProperty("uri").GetString(),
                   pathOrUri.Contains("://", StringComparison.Ordinal) ? pathOrUri : new Uri(pathOrUri).AbsoluteUri);
    }

    private static int? VersionOf(JsonElement parameters) =>
        parameters.TryGetProperty("version", out var version) && version.ValueKind == JsonValueKind.Number
            ? version.GetInt32()
            : null;

    private static int CountOf(JsonElement parameters) =>
        parameters.GetProperty("diagnostics").GetArrayLength();

    private static bool HasError(JsonElement parameters) =>
        parameters.GetProperty("diagnostics").EnumerateArray().Any(diagnostic =>
            diagnostic.TryGetProperty("severity", out var severity) && severity.GetInt32() == 1);

    private static JsonElement FirstDiagnostic(JsonElement publish) =>
        publish.GetProperty("params").GetProperty("diagnostics")[0];

    private static bool ContainsPathEndingWith(JsonElement array, string suffix) =>
        array.EnumerateArray().Any(element =>
            element.GetString()?.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) == true);

    private static bool AreEquivalentDocumentUris(string? left, string right)
    {
        if (left is null || !Uri.TryCreate(left, UriKind.Absolute, out var leftUri) ||
            !Uri.TryCreate(right, UriKind.Absolute, out var rightUri))
            return false;

        if (leftUri.IsFile && rightUri.IsFile)
            return string.Equals(leftUri.LocalPath, rightUri.LocalPath, StringComparison.OrdinalIgnoreCase);

        return leftUri.Equals(rightUri);
    }

    private static string Sample(params string[] segments) =>
        Path.Combine([_repoRoot, "dotnet-port", "samples", .. segments]);

    private static void EnsureFixturesBuilt()
    {
        // ResolveReferences resolves ProjectReference/PackageReference outputs
        // without compiling them, so referenced assemblies (MathLib.dll,
        // SokobanMacros.dll, restored packages) must exist on disk first.
        foreach (var project in new[]
                 {
                     Sample("HelloCore", "HelloCore.nproj"),
                     Sample("RefDemo", "App", "App.nproj"),
                     Sample("PackageReference", "PackageReference.nproj"),
                     Sample("Sokoban", "Sokoban", "Sokoban.nproj"),
                 })
        {
            Console.WriteLine($"    building fixture {Path.GetFileName(project)}");
            RunDotnet($"build \"{project}\" -c Debug --nologo -v:q", _repoRoot);
        }
    }

    private static void RunDotnet(string arguments, string workingDirectory)
    {
        var startInfo = new ProcessStartInfo("dotnet", arguments)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory,
        };
        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Could not start dotnet {arguments}.");
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"dotnet {arguments} failed ({process.ExitCode}):\n{stdout}\n{stderr}");
    }

    private static string CreateTempDirectory(string suffix)
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"nemerle-lsp-{suffix}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static void TryDeleteDirectory(string directory)
    {
        try { Directory.Delete(directory, recursive: true); }
        catch { }
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (Directory.Exists(Path.Combine(current.FullName, "dotnet-port")) &&
                Directory.Exists(Path.Combine(current.FullName, "ncc")))
                return current.FullName;
            current = current.Parent;
        }

        // Fall back to the current working directory (the documented
        // repository-root invocation).
        var cwd = Directory.GetCurrentDirectory();
        if (Directory.Exists(Path.Combine(cwd, "dotnet-port")))
            return cwd;
        throw new InvalidOperationException("Could not locate the repository root.");
    }
}
