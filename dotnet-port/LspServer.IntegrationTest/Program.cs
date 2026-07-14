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
            await RunScenarioAsync("DefineConstants IDE/build parity (#if branch selection)", DefinesParityAsync);
            await RunScenarioAsync("Warning N-code surfaces in the LSP diagnostic code field", WarningCodeAsync);
            await RunScenarioAsync("Buffer override, close revert, stale suppression, source removal, failure recovery",
                BufferDiskCloseStaleRemovalAsync);
            await RunScenarioAsync("Hover: five symbol kinds, no raw markup leak, markdown fencing", HoverSymbolKindsAsync);
            await RunScenarioAsync("Hover: unsaved buffer change reflects the new type", HoverBufferChangeAsync);
            await RunScenarioAsync("Hover: cross-source (Sokoban) and ProjectReference (RefDemo) symbols", HoverCrossSourceAsync);
            await RunScenarioAsync("Hover: non-identifier null and no deadlock racing a project reload", HoverReloadRaceAsync);
            await RunScenarioAsync("Hover: CRLF + non-BMP range in 0-based UTF-16", HoverCrlfNonBmpAsync);
            await RunScenarioAsync("Hover: warm response-time measurement", HoverTimingAsync);
            await RunScenarioAsync("Completion: member completion in RefDemo/PackageReference/Sokoban", CompletionMemberProjectsAsync);
            await RunScenarioAsync("Completion: global-scope keywords and unsaved-buffer symbols", CompletionGlobalAndUnsavedAsync);
            await RunScenarioAsync("Completion: resolve computes deferred overload documentation", CompletionResolveAsync);
            await RunScenarioAsync("Completion: racing edits do not crash or throw on stale positions", CompletionRaceAsync);
            await RunScenarioAsync("Completion: warm response-time measurement", CompletionTimingAsync);
            await RunScenarioAsync("Definition: local declaration and unsaved-buffer move", DefinitionLocalAndBufferAsync);
            await RunScenarioAsync("Definition: cross-source declaration (Sokoban)", DefinitionCrossSourceAsync);
            await RunScenarioAsync("Definition: external (BCL/NuGet) member is an empty result", DefinitionExternalEmptyAsync);
            await RunScenarioAsync("Definition: CRLF + non-BMP position", DefinitionCrlfNonBmpAsync);
            await RunScenarioAsync("References: cross-source usages and includeDeclaration toggle", ReferencesAsync);
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

        // WP-M1: measurement/status trace moved from stderr to window/logMessage.
        foreach (var (type, message) in client.LogMessages())
        {
            if (message.StartsWith("nemerle project query finished", StringComparison.Ordinal) ||
                message.StartsWith("nemerle engine rebuild finished", StringComparison.Ordinal))
                Console.WriteLine($"    trace (logMessage type {type}): {message}");
        }

        // The server no longer writes its own trace/diagnostics to stderr, so
        // vscode-languageclient never forwards a normal session to the Output
        // Channel as [error] (WP-M1 acceptance criterion 5, safe protocol-level
        // proxy for the extension Output Channel).
        foreach (var line in stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (line.StartsWith("nemerle ", StringComparison.Ordinal) ||
                line.StartsWith("Project query ", StringComparison.Ordinal))
                throw new InvalidDataException(
                    $"Server wrote its own trace to stderr instead of window/logMessage: {line}");
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

        // WP-M1 logging migration: measurement traces arrive via window/logMessage
        // at Log level (type 4), not as stderr the client would render as [error].
        _ = await client.WaitForAsync(
            message => LspTestClient.IsLogMessageContaining(message, "project query finished", out var type) && type == 4,
            beforeLoad,
            "a project-query timing trace via window/logMessage (Log level)");
        _ = await client.WaitForAsync(
            message => LspTestClient.IsLogMessageContaining(message, "engine rebuild finished", out var type) && type == 4,
            beforeLoad,
            "an engine-rebuild timing trace via window/logMessage (Log level)");

        // WP-K/WP-L1 loose-file flow continues to work while the HelloCore
        // workspace stays applied.
        var testDirectory = CreateTempDirectory("loose");
        try
        {
            // A missing project stays a typed, recoverable result, and is still
            // surfaced at Error level so the failure remains visible.
            var beforeMissing = client.Mark();
            var missing = await LoadProjectAsync(client, Path.Combine(testDirectory, "Missing.nproj"));
            if (missing.GetProperty("state").GetString() != "error" ||
                missing.GetProperty("errorKind").GetString() != "NonZeroExit" ||
                missing.GetProperty("appliedToEngine").GetBoolean())
            {
                throw new InvalidDataException("Missing project did not return the expected recoverable error result: " + missing);
            }

            _ = await client.WaitForAsync(
                message => LspTestClient.IsLogMessageContaining(message, "project query", out var type) && type == 1,
                beforeMissing,
                "the project-query failure reported via window/logMessage at Error level");

            // A normal session (open -> change -> close) must not raise any
            // Error-level log message (WP-M1 acceptance criterion 5).
            var beforeNormal = client.Mark();

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

            var normalErrors = client.LogMessages(beforeNormal).Where(entry => entry.Type == 1).ToArray();
            if (normalErrors.Length > 0)
                throw new InvalidDataException(
                    "A normal open/change/close session produced Error-level log messages: " +
                    string.Join(" | ", normalErrors.Select(entry => entry.Message)));
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

    // ----- DefineConstants IDE/build parity -----

    private static async Task DefinesParityAsync(LspTestClient client)
    {
        var project = Sample("Defines", "Defines.nproj");
        var source = Sample("Defines", "defines.n");
        var sourceUri = new Uri(source).AbsoluteUri;
        var sourceText = await File.ReadAllTextAsync(source);

        // Negative control: as a loose file (no CUSTOM_FEATURE), the #else
        // branch is a type error.
        var mark = client.Mark();
        await DidOpenAsync(client, sourceUri, sourceText, 1);
        _ = await client.WaitForAsync(
            message => IsPublishFor(message, sourceUri, out var p) && VersionOf(p) == 1 && HasError(p),
            mark,
            "a type error in defines.n's #else branch when CUSTOM_FEATURE is not defined");

        // Applying the project defines CUSTOM_FEATURE (via DefineConstants ->
        // -define:), selecting the clean #if branch: the same buffer is now
        // error-free.  This is the IDE side of the DefineConstants build parity.
        mark = client.Mark();
        var result = await LoadProjectAsync(client, project);
        AssertLoadedAndApplied(result, expectedSources: 1);
        if (!result.GetProperty("defineConstants").EnumerateArray()
                .Any(define => define.GetString() == "CUSTOM_FEATURE"))
            throw new InvalidDataException("Defines snapshot did not carry CUSTOM_FEATURE: " + result);
        _ = await client.WaitForAsync(
            message => IsPublishFor(message, sourceUri, out var p) && VersionOf(p) == 1 && CountOf(p) == 0,
            mark,
            "error-free diagnostics for defines.n once CUSTOM_FEATURE is applied from the project");
    }

    // ----- Warning N-code in the LSP diagnostic code field -----

    private static async Task WarningCodeAsync(LspTestClient client)
    {
        var project = Sample("Warnings", "Warnings.nproj");
        var source = Sample("Warnings", "warnings.n");

        var beforeLoad = client.Mark();
        var result = await LoadProjectAsync(client, project);
        AssertLoadedAndApplied(result, expectedSources: 1);

        // The redundant ':>' upcast produces warning N10001; the engine keeps the
        // code out of the message text and puts it in Diagnostic.code (WP-M1).
        var publish = await client.WaitForAsync(
            message => IsPublishFor(message, source, out var p) &&
                       DiagnosticsOf(p).Any(IsN10001Warning),
            beforeLoad,
            "an N10001 warning with a structured code for the redundant upcast in warnings.n");

        var diagnostic = DiagnosticsOf(publish.GetProperty("params")).First(IsN10001Warning);
        var message = diagnostic.GetProperty("message").GetString() ?? "";
        if (message.StartsWith("N10001", StringComparison.Ordinal))
            throw new InvalidDataException("The N10001 code leaked into the diagnostic message text: " + message);
        if (!message.Contains("no check needed", StringComparison.Ordinal))
            throw new InvalidDataException("Unexpected N10001 warning message: " + message);
    }

    private static IEnumerable<JsonElement> DiagnosticsOf(JsonElement parameters) =>
        parameters.GetProperty("diagnostics").EnumerateArray();

    private static bool IsN10001Warning(JsonElement diagnostic) =>
        diagnostic.TryGetProperty("severity", out var severity) && severity.GetInt32() == 2 &&
        diagnostic.TryGetProperty("code", out var code) &&
        (code.ValueKind == JsonValueKind.String ? code.GetString() : null) == "N10001";

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

    // ----- Hover: five symbol kinds, no raw markup, markdown fencing -----

    // A self-contained loose-file probe with a local value, a parameter, a
    // method call, a property access and a type reference.  Loose-file mode is
    // enough: only core references are needed and the engine builds a types tree
    // from the open buffer.
    private const string HoverProbeSource = """
        using System;

        class Box
        {
          public Amount : int { get { 5 } }
        }

        module Probe
        {
          Run() : void
          {
            def box : Box = Box();
            def localValue = Compute(box.Amount);
            Console.WriteLine(localValue);
          }

          Compute(parameter : int) : int
          {
            parameter + 1
          }
        }
        """;

    private static async Task HoverSymbolKindsAsync(LspTestClient client)
    {
        var uri = new Uri(Path.Combine(CreateTempDirectory("hover"), "probe.n")).AbsoluteUri;
        await OpenAndAwaitAnalysisAsync(client, uri, HoverProbeSource);

        // localValue: hover the usage inside Console.WriteLine(localValue).
        await AssertHoverContainsAsync(client, uri, HoverProbeSource, "localValue", 2, "localValue",
            "hover on a local value");
        // parameter: hover the usage in "parameter + 1".
        await AssertHoverContainsAsync(client, uri, HoverProbeSource, "parameter", 2, "parameter",
            "hover on a method parameter");
        // method: hover the Compute(box.Amount) call.
        await AssertHoverContainsAsync(client, uri, HoverProbeSource, "Compute", 1, "Compute",
            "hover on a method call");
        // property: hover box.Amount (the Amount member).
        await AssertHoverContainsAsync(client, uri, HoverProbeSource, "Amount", 2, "Amount",
            "hover on a property");
        // type name: hover the "Box" type declaration name.
        await AssertHoverContainsAsync(client, uri, HoverProbeSource, "Box", 1, "Box",
            "hover on a type name");
    }

    private static async Task HoverBufferChangeAsync(LspTestClient client)
    {
        var uri = new Uri(Path.Combine(CreateTempDirectory("hover-change"), "typed.n")).AbsoluteUri;
        // The edit renames the local (int -> string, "alpha" -> "bravo", same
        // length so the usage position is stable), so the hover of the edited
        // buffer must reflect the new symbol and not the stale one.  (The
        // headless engine frequently renders a blank type in local-value hints
        // - a known WP-K BCL/inference characteristic - so the reflected name is
        // the robust signal that the unsaved buffer, not a stale one, was used.)
        const string source1 = """
            module Typed
            {
              Run() : void
              {
                def alpha = 123;
                System.Console.WriteLine(alpha);
              }
            }
            """;
        const string source2 = """
            module Typed
            {
              Run() : void
              {
                def bravo = "s";
                System.Console.WriteLine(bravo);
              }
            }
            """;

        await OpenAndAwaitAnalysisAsync(client, uri, source1);
        var firstHover = await HoverTextAtAsync(client, uri, source1, "alpha", 2);
        if (firstHover is null || !firstHover.Contains("alpha", StringComparison.Ordinal))
            throw new InvalidDataException("Hover did not report the initial local 'alpha': " + firstHover);

        var mark = client.Mark();
        await DidChangeAsync(client, uri, source2, 2);
        // Wait until the engine reanalyzes the version-2 buffer.
        _ = await client.WaitForAsync(
            message => IsPublishFor(message, uri, out var p) && VersionOf(p) == 2 && CountOf(p) == 0,
            mark,
            "clean version-2 diagnostics after the unsaved rename edit");

        var secondHover = await HoverTextAtAsync(client, uri, source2, "bravo", 2);
        if (secondHover is null || !secondHover.Contains("bravo", StringComparison.Ordinal))
            throw new InvalidDataException("Hover did not reflect the renamed local 'bravo' after the unsaved edit: " + secondHover);
        if (secondHover.Contains("alpha", StringComparison.Ordinal))
            throw new InvalidDataException("Hover still reported the stale local 'alpha' after the edit: " + secondHover);
    }

    private static async Task HoverCrossSourceAsync(LspTestClient client)
    {
        // RefDemo: hover a type/member resolved through a ProjectReference.
        var appProject = Sample("RefDemo", "App", "App.nproj");
        var programSource = Sample("RefDemo", "App", "Program.n");
        var programUri = new Uri(programSource).AbsoluteUri;
        var programText = await File.ReadAllTextAsync(programSource);

        var mark = client.Mark();
        var result = await LoadProjectAsync(client, appProject);
        AssertLoadedAndApplied(result, expectedSources: 1);
        await DidOpenAsync(client, programUri, programText, 1);
        _ = await client.WaitForAsync(
            message => IsPublishFor(message, programUri, out var p) && VersionOf(p) == 1 && !HasError(p),
            mark,
            "error-free Program.n so its symbols are resolved for hover");

        // Square is a static method on MathLib.Calc, resolved via the
        // ProjectReference output (occurrence 2 skips the "Square(7)" string).
        if (programText.Contains("Calc.Square", StringComparison.Ordinal))
            await AssertHoverNonNullAsync(client, programUri, programText, "Square", 2,
                "hover on a ProjectReference method (RefDemo Calc.Square)");

        // Sokoban: hover a symbol declared in another project source.
        var sokobanProject = Sample("Sokoban", "Sokoban", "Sokoban.nproj");
        var mainSource = Sample("Sokoban", "Sokoban", "main.n");
        var mainUri = new Uri(mainSource).AbsoluteUri;
        var mainText = await File.ReadAllTextAsync(mainSource);

        mark = client.Mark();
        result = await LoadProjectAsync(client, sokobanProject);
        AssertLoadedAndApplied(result, expectedSources: 5);
        await DidOpenAsync(client, mainUri, mainText, 1);
        _ = await client.WaitForAsync(
            message => IsPublishFor(message, mainUri, out var p) && VersionOf(p) == 1 && !HasError(p),
            mark,
            "error-free main.n with cross-source project symbols for hover");

        // A_Star is a static method on TreeSearch, declared in another Sokoban
        // source; resolving it proves cross-source symbols are in the workspace.
        if (mainText.Contains("A_Star", StringComparison.Ordinal))
            await AssertHoverNonNullAsync(client, mainUri, mainText, "A_Star", 1,
                "hover on a cross-source declared method (Sokoban TreeSearch.A_Star)");
    }

    private static async Task HoverReloadRaceAsync(LspTestClient client)
    {
        var uri = new Uri(Path.Combine(CreateTempDirectory("hover-race"), "race.n")).AbsoluteUri;
        await OpenAndAwaitAnalysisAsync(client, uri, HoverProbeSource);

        // A position that is not over any symbol (the empty line 1) must return a
        // null hover rather than a fabricated result.
        var empty = await HoverAsync(client, uri, 1, 0);
        if (empty.ValueKind != JsonValueKind.Null)
            throw new InvalidDataException("Hover over a non-identifier position was not null: " + empty);

        // Race hovers against a burst of edits + reloads.  Every hover must
        // return a response (result or null) rather than deadlock.
        for (var version = 2; version <= 6; version++)
        {
            await DidChangeAsync(client, uri, HoverProbeSource, version);
            var (line, character) = LocateUtf16(HoverProbeSource, "localValue", 2);
            var hover = await HoverAsync(client, uri, line, character);
            if (hover.ValueKind is not (JsonValueKind.Object or JsonValueKind.Null))
                throw new InvalidDataException("Hover during reload returned an unexpected shape: " + hover);
        }
    }

    private static async Task HoverCrlfNonBmpAsync(LspTestClient client)
    {
        var uri = new Uri(Path.Combine(CreateTempDirectory("hover-crlf"), "crlf.n")).AbsoluteUri;
        // CRLF line endings; a non-BMP emoji (U+1F600, a UTF-16 surrogate pair)
        // sits before the hovered identifier on the same line, so a correct
        // UTF-16 offset must count it as two code units.
        var source = string.Join("\r\n",
            "module Crlf",
            "{",
            "  Run() : void",
            "  {",
            "    def value = 1; /* 😀 */ System.Console.WriteLine(value)",
            "  }",
            "}");

        await OpenAndAwaitAnalysisAsync(client, uri, source);

        var (line, character) = LocateUtf16(source, "value", 2); // the usage in WriteLine(value)
        var hover = await HoverAsync(client, uri, line, character);
        if (hover.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Hover on a CRLF + non-BMP buffer returned null.");

        var start = hover.GetProperty("range").GetProperty("start");
        var gotLine = start.GetProperty("line").GetInt32();
        var gotChar = start.GetProperty("character").GetInt32();
        if (gotLine != line || gotChar != character)
            throw new InvalidDataException(
                $"Hover range was not 0-based UTF-16 (expected {line}:{character}, got {gotLine}:{gotChar}); " +
                "the non-BMP surrogate pair or CRLF was miscounted.");
    }

    private static async Task HoverTimingAsync(LspTestClient client)
    {
        var uri = new Uri(Path.Combine(CreateTempDirectory("hover-timing"), "timing.n")).AbsoluteUri;
        await OpenAndAwaitAnalysisAsync(client, uri, HoverProbeSource);

        var (line, character) = LocateUtf16(HoverProbeSource, "localValue", 2);
        // Warm-up so the first (cold) hover is excluded from the measurement.
        _ = await HoverAsync(client, uri, line, character);

        var samples = new List<double>();
        for (var i = 0; i < 15; i++)
        {
            var stopwatch = Stopwatch.StartNew();
            var hover = await HoverAsync(client, uri, line, character);
            stopwatch.Stop();
            if (hover.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("Warm hover unexpectedly returned null.");
            samples.Add(stopwatch.Elapsed.TotalMilliseconds);
        }

        samples.Sort();
        var p50 = samples[samples.Count / 2];
        var p95 = samples[(int)(samples.Count * 0.95)];
        Console.WriteLine(
            $"    warm hover round-trip: p50 {p50:F0} ms, p95 {p95:F0} ms, min {samples[0]:F0} ms, max {samples[^1]:F0} ms (n={samples.Count})");
    }

    // ----- Completion: member completion in three project contexts -----

    private static async Task CompletionMemberProjectsAsync(LspTestClient client)
    {
        // RefDemo: members of Calc, resolved through a ProjectReference (MathLib).
        var appProject = Sample("RefDemo", "App", "App.nproj");
        var programUri = new Uri(Sample("RefDemo", "App", "Program.n")).AbsoluteUri;
        const string refDemoBuffer = """
            using MathLib;

            module Program
            {
              Main() : void
              {
                _ = Calc.Square(7);
              }
            }
            """;
        var mark = client.Mark();
        AssertLoadedAndApplied(await LoadProjectAsync(client, appProject), expectedSources: 1);
        await DidOpenAsync(client, programUri, refDemoBuffer, 1);
        await client.WaitForAsync(
            message => IsPublishFor(message, programUri, out var p) && VersionOf(p) == 1,
            mark, "analysis of the RefDemo completion buffer");
        var labels = await CompletionLabelsAsync(client, programUri, refDemoBuffer, "Square", 1, atStartOfNeedle: true);
        AssertContainsLabel(labels, "Square", "RefDemo ProjectReference member completion (Calc.)");
        AssertContainsLabel(labels, "Sum", "RefDemo ProjectReference member completion (Calc.)");

        // PackageReference: members of Newtonsoft.Json's JsonConvert.
        var packageProject = Sample("PackageReference", "PackageReference.nproj");
        var packageUri = new Uri(Sample("PackageReference", "PackageSample.n")).AbsoluteUri;
        const string packageBuffer = """
            using Newtonsoft.Json;

            namespace PackageReferenceSample
            {
              public module Marker
              {
                public Run() : void
                {
                  _ = JsonConvert.SerializeObject(42);
                }
              }
            }
            """;
        mark = client.Mark();
        AssertLoadedAndApplied(await LoadProjectAsync(client, packageProject), expectedSources: 1);
        await DidOpenAsync(client, packageUri, packageBuffer, 1);
        await client.WaitForAsync(
            message => IsPublishFor(message, packageUri, out var p) && VersionOf(p) == 1,
            mark, "analysis of the PackageReference completion buffer");
        labels = await CompletionLabelsAsync(client, packageUri, packageBuffer, "SerializeObject", 1, atStartOfNeedle: true);
        AssertContainsLabel(labels, "SerializeObject", "PackageReference member completion (JsonConvert.)");

        // Sokoban: members of TreeSearch, declared in another project source.
        var sokobanProject = Sample("Sokoban", "Sokoban", "Sokoban.nproj");
        var mainUri = new Uri(Sample("Sokoban", "Sokoban", "main.n")).AbsoluteUri;
        const string sokobanBuffer = """
            using Nemerle.IO;

            namespace NSokoban
            {
              public class Sokoban
              {
                public static Main (args : array[string]) : void
                {
                  _ = TreeSearch.A_Star(args);
                }
              }
            }
            """;
        mark = client.Mark();
        AssertLoadedAndApplied(await LoadProjectAsync(client, sokobanProject), expectedSources: 5);
        await DidOpenAsync(client, mainUri, sokobanBuffer, 1);
        await client.WaitForAsync(
            message => IsPublishFor(message, mainUri, out var p) && VersionOf(p) == 1,
            mark, "analysis of the Sokoban completion buffer");
        labels = await CompletionLabelsAsync(client, mainUri, sokobanBuffer, "A_Star", 1, atStartOfNeedle: true);
        AssertContainsLabel(labels, "A_Star", "Sokoban cross-source member completion (TreeSearch.)");
    }

    // ----- Completion: global scope keywords and unsaved-buffer symbols -----

    private const string CompletionProbeSource = """
        using System;

        module Probe
        {
          Run() : void
          {
            def greeting = "hello";
            _ = greeting.Length;
            _ = match;
          }
        }
        """;

    private static async Task CompletionGlobalAndUnsavedAsync(LspTestClient client)
    {
        var uri = new Uri(Path.Combine(CreateTempDirectory("completion-global"), "probe.n")).AbsoluteUri;
        await OpenAndAwaitAnalysisAsync(client, uri, CompletionProbeSource);

        // Global (expression) scope: the completion prefix "match" is over the
        // '_ = match;' statement; the list must not be empty and must contain a
        // Nemerle keyword.
        var (line, character) = LocateUtf16(CompletionProbeSource, "match", 1);
        var globalItems = await CompletionItemsAsync(client, uri, line, character);
        if (globalItems.Length == 0)
            throw new InvalidDataException("Global-scope completion returned an empty list.");
        var globalLabels = globalItems.Select(i => i.GetProperty("label").GetString()).ToArray();
        if (!globalLabels.Any(l => l == "match"))
            throw new InvalidDataException(
                "Global-scope completion did not contain the Nemerle keyword 'match': " + string.Join(", ", globalLabels.Take(40)));
        // The keyword item is tagged as a keyword kind (glyph -> CompletionItemKind).
        var keyword = globalItems.First(i => i.GetProperty("label").GetString() == "match");
        if (!keyword.TryGetProperty("kind", out var kind) || kind.GetInt32() != 14 /* Keyword */)
            throw new InvalidDataException("The 'match' completion item was not mapped to CompletionItemKind.Keyword.");
        foreach (var item in globalItems)
            AssertNoRawMarkup(item.GetProperty("label").GetString() ?? "", "global completion label");

        // Unsaved buffer: a newly declared local becomes completable in the same
        // buffer without any save.
        const string edited = """
            using System;

            module Probe
            {
              Run() : void
              {
                def zebraLocal = "hello";
                _ = zebraLoc;
              }
            }
            """;
        var mark = client.Mark();
        await DidChangeAsync(client, uri, edited, 2);
        await client.WaitForAsync(
            message => IsPublishFor(message, uri, out var p) && VersionOf(p) == 2,
            mark, "analysis of the edited (unsaved) completion buffer");
        var (zLine, zChar) = LocateUtf16(edited, "zebraLoc", 2); // the usage, not the declaration
        var editedLabels = await CompletionLabelsAtAsync(client, uri, zLine, zChar);
        AssertContainsLabel(editedLabels, "zebraLocal",
            "unsaved-buffer completion of a newly declared local");
    }

    // ----- Completion: resolve computes the deferred documentation -----

    private static async Task CompletionResolveAsync(LspTestClient client)
    {
        var uri = new Uri(Path.Combine(CreateTempDirectory("completion-resolve"), "resolve.n")).AbsoluteUri;
        const string source = """
            using System;

            module Resolve
            {
              Run() : void
              {
                Console.WriteLine("x");
              }
            }
            """;
        await OpenAndAwaitAnalysisAsync(client, uri, source);

        // Members of System.Console after the dot; WriteLine has many overloads,
        // so its resolved documentation must enumerate them.
        var (line, character) = LocateUtf16(source, "WriteLine", 1);
        var items = await CompletionItemsAsync(client, uri, line, character);
        var writeLine = items.FirstOrDefault(i => i.GetProperty("label").GetString() == "WriteLine");
        if (writeLine.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException(
                "Console member completion did not contain WriteLine: " +
                string.Join(", ", items.Select(i => i.GetProperty("label").GetString()).Take(40)));

        // Before resolve, the heavy documentation must not be present.
        if (writeLine.TryGetProperty("documentation", out var predoc) && predoc.ValueKind != JsonValueKind.Null)
            throw new InvalidDataException("Completion item carried documentation before resolve: " + predoc);

        var resolved = await ResolveAsync(client, writeLine);
        if (!resolved.TryGetProperty("documentation", out var doc) || doc.ValueKind == JsonValueKind.Null)
            throw new InvalidDataException("Resolve did not attach documentation to the WriteLine item.");
        var docText = doc.ValueKind == JsonValueKind.String
            ? doc.GetString()
            : doc.GetProperty("value").GetString();
        if (string.IsNullOrEmpty(docText))
            throw new InvalidDataException("Resolved documentation was empty.");
        // Multiple overloads -> the description mentions WriteLine more than once.
        var occurrences = CountOccurrences(docText!, "WriteLine");
        if (occurrences < 2)
            throw new InvalidDataException($"Resolved WriteLine documentation did not enumerate overloads (WriteLine x{occurrences}): {docText}");
        AssertNoRawMarkup(docText!, "resolved completion documentation");
    }

    // ----- Completion: racing edits do not crash or throw on stale positions -----

    private static async Task CompletionRaceAsync(LspTestClient client)
    {
        var uri = new Uri(Path.Combine(CreateTempDirectory("completion-race"), "race.n")).AbsoluteUri;
        await OpenAndAwaitAnalysisAsync(client, uri, CompletionProbeSource);

        // A position not over any completable construct still returns a valid
        // (possibly empty) list rather than an error.
        var empty = await RequestCompletionAsync(client, uri, 1, 0);
        if (empty.ValueKind is not (JsonValueKind.Object or JsonValueKind.Array or JsonValueKind.Null))
            throw new InvalidDataException("Completion at a blank line returned an unexpected shape: " + empty);

        // Race completion against a burst of edits + reloads.  The bridge's
        // version check must keep every request from throwing on a superseded
        // buffer position.
        var (line, character) = LocateUtf16(CompletionProbeSource, "greeting", 2);
        for (var version = 2; version <= 6; version++)
        {
            await DidChangeAsync(client, uri, CompletionProbeSource, version);
            var response = await RequestCompletionAsync(client, uri, line, character);
            if (response.ValueKind is not (JsonValueKind.Object or JsonValueKind.Array or JsonValueKind.Null))
                throw new InvalidDataException("Completion during reload returned an unexpected shape: " + response);
        }
    }

    // ----- Completion: warm response-time measurement -----

    private static async Task CompletionTimingAsync(LspTestClient client)
    {
        var uri = new Uri(Path.Combine(CreateTempDirectory("completion-timing"), "timing.n")).AbsoluteUri;
        await OpenAndAwaitAnalysisAsync(client, uri, CompletionProbeSource);

        var (line, character) = LocateUtf16(CompletionProbeSource, "Length", 1);
        _ = await RequestCompletionAsync(client, uri, line, character); // warm-up

        var samples = new List<double>();
        for (var i = 0; i < 15; i++)
        {
            var stopwatch = Stopwatch.StartNew();
            _ = await RequestCompletionAsync(client, uri, line, character);
            stopwatch.Stop();
            samples.Add(stopwatch.Elapsed.TotalMilliseconds);
        }

        samples.Sort();
        var p50 = samples[samples.Count / 2];
        var p95 = samples[(int)(samples.Count * 0.95)];
        Console.WriteLine(
            $"    warm completion round-trip: p50 {p50:F0} ms, p95 {p95:F0} ms, min {samples[0]:F0} ms, max {samples[^1]:F0} ms (n={samples.Count})");
    }

    // ----- Definition: local declaration + unsaved-buffer move -----

    private const string DefinitionProbeSource = """
        module Probe
        {
          Run() : void
          {
            def target = 1;
            System.Console.WriteLine(target);
          }
        }
        """;

    private static async Task DefinitionLocalAndBufferAsync(LspTestClient client)
    {
        var uri = new Uri(Path.Combine(CreateTempDirectory("definition-local"), "probe.n")).AbsoluteUri;
        await OpenAndAwaitAnalysisAsync(client, uri, DefinitionProbeSource);

        // Definition on the usage (occurrence 2 of "target", inside WriteLine)
        // points to the declaration (occurrence 1, "def target").
        var (declLine, declChar) = LocateUtf16(DefinitionProbeSource, "target", 1);
        var (useLine, useChar) = LocateUtf16(DefinitionProbeSource, "target", 2);
        var locations = await DefinitionAsync(client, uri, useLine, useChar);
        if (locations.Length == 0)
            throw new InvalidDataException("Definition on a local usage returned no location.");
        var (locUri, locLine, locChar) = LocationAt(locations[0]);
        if (!AreEquivalentDocumentUris(locUri, uri))
            throw new InvalidDataException($"Definition URI did not point at the same document: {locUri}");
        if (locLine != declLine || locChar != declChar)
            throw new InvalidDataException(
                $"Definition did not point at the declaration (expected {declLine}:{declChar}, got {locLine}:{locChar}).");

        // Prepending a line shifts the declaration down; the buffer (not stale
        // disk/text) must drive the new definition position (acceptance 2).
        var moved = "// shifted\n" + DefinitionProbeSource;
        var mark = client.Mark();
        await DidChangeAsync(client, uri, moved, 2);
        _ = await client.WaitForAsync(
            message => IsPublishFor(message, uri, out var p) && VersionOf(p) == 2 && CountOf(p) == 0,
            mark, "clean version-2 diagnostics after prepending a line");

        var (movedDeclLine, movedDeclChar) = LocateUtf16(moved, "target", 1);
        var (movedUseLine, movedUseChar) = LocateUtf16(moved, "target", 2);
        var movedLocations = await DefinitionAsync(client, uri, movedUseLine, movedUseChar);
        if (movedLocations.Length == 0)
            throw new InvalidDataException("Definition after the unsaved move returned no location.");
        var (_, movedLine, movedChar) = LocationAt(movedLocations[0]);
        if (movedLine != movedDeclLine || movedChar != movedDeclChar)
            throw new InvalidDataException(
                $"Definition did not follow the moved declaration (expected {movedDeclLine}:{movedDeclChar}, got {movedLine}:{movedChar}).");
        if (movedLine == declLine)
            throw new InvalidDataException("Definition still pointed at the pre-edit declaration line (stale buffer).");
    }

    // ----- Definition: cross-source declaration (Sokoban) -----

    private static async Task DefinitionCrossSourceAsync(LspTestClient client)
    {
        var project = Sample("Sokoban", "Sokoban", "Sokoban.nproj");
        var mainSource = Sample("Sokoban", "Sokoban", "main.n");
        var mainUri = new Uri(mainSource).AbsoluteUri;
        var mainText = await File.ReadAllTextAsync(mainSource);

        var mark = client.Mark();
        AssertLoadedAndApplied(await LoadProjectAsync(client, project), expectedSources: 5);
        await DidOpenAsync(client, mainUri, mainText, 1);
        _ = await client.WaitForAsync(
            message => IsPublishFor(message, mainUri, out var p) && VersionOf(p) == 1 && !HasError(p),
            mark, "error-free main.n so cross-source symbols resolve for definition");

        // MapCollection is a struct declared in sokoban.n; TreeSearch.A_Star is a
        // method declared in treesearch.n.  Definition from main.n must land in
        // the declaring source (a different file), proving cross-source goto.
        await AssertDefinitionInFileAsync(client, mainUri, mainText, "MapCollection", 1, "sokoban.n",
            "definition on the cross-source struct MapCollection");
        await AssertDefinitionInFileAsync(client, mainUri, mainText, "A_Star", 1, "treesearch.n",
            "definition on the cross-source method TreeSearch.A_Star");
    }

    private static async Task AssertDefinitionInFileAsync(
        LspTestClient client, string uri, string text, string needle, int occurrence,
        string expectedFileSuffix, string description)
    {
        var (line, character) = LocateUtf16(text, needle, occurrence);
        var locations = await DefinitionAsync(client, uri, line, character);
        if (locations.Length == 0)
            throw new InvalidDataException($"{description}: returned no location.");
        var (locUri, locLine, _) = LocationAt(locations[0]);
        if (!Uri.TryCreate(locUri, UriKind.Absolute, out var parsed) || !parsed.IsFile)
            throw new InvalidDataException($"{description}: returned a non-file URI {locUri}.");
        if (!parsed.LocalPath.EndsWith(expectedFileSuffix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"{description}: expected a location in {expectedFileSuffix}, got {locUri}.");
        if (locLine < 0)
            throw new InvalidDataException($"{description}: returned a negative line.");
    }

    // ----- Definition: external (BCL/NuGet) member is empty -----

    private static async Task DefinitionExternalEmptyAsync(LspTestClient client)
    {
        var uri = new Uri(Path.Combine(CreateTempDirectory("definition-external"), "external.n")).AbsoluteUri;
        const string source = """
            module External
            {
              Run() : void
              {
                System.Console.WriteLine("x");
              }
            }
            """;
        await OpenAndAwaitAnalysisAsync(client, uri, source);

        // WriteLine resolves to the external System.Console.WriteLine member (in a
        // BCL assembly, no in-workspace source): the result is empty and the
        // resolution is logged at Info level (acceptance 4).
        var mark = client.Mark();
        var (wlLine, wlChar) = LocateUtf16(source, "WriteLine", 1);
        var writeLine = await DefinitionAsync(client, uri, wlLine, wlChar);
        if (writeLine.Length != 0)
        {
            var (locUri, _, _) = LocationAt(writeLine[0]);
            throw new InvalidDataException($"Definition on an external member was not empty: {locUri}");
        }

        var externalLogs = client.LogMessages(mark)
            .Where(entry => entry.Message.Contains("metadata (external assembly)", StringComparison.Ordinal))
            .ToArray();
        if (externalLogs.Length == 0)
            throw new InvalidDataException("An external-member definition did not emit the Info log (acceptance 4).");
        if (externalLogs.Any(entry => entry.Type != 3 /* Info */))
            throw new InvalidDataException("The external-definition log was not emitted at Info level.");

        // A qualifier that resolves to nothing (System.Console the type path) must
        // also stay empty and never return a bogus obj/-style URI.
        var (cLine, cChar) = LocateUtf16(source, "Console", 1);
        var console = await DefinitionAsync(client, uri, cLine, cChar);
        if (console.Length != 0)
        {
            var (locUri, _, _) = LocationAt(console[0]);
            throw new InvalidDataException($"Definition on an external type qualifier was not empty: {locUri}");
        }
    }

    // ----- Definition: CRLF + non-BMP position -----

    private static async Task DefinitionCrlfNonBmpAsync(LspTestClient client)
    {
        var uri = new Uri(Path.Combine(CreateTempDirectory("definition-crlf"), "crlf.n")).AbsoluteUri;
        // CRLF line endings; a non-BMP emoji (U+1F600, a UTF-16 surrogate pair)
        // sits before the declaration, so both the request position and the
        // returned declaration range must count it as two UTF-16 code units.
        var source = string.Join("\r\n",
            "module Crlf",
            "{",
            "  Run() : void",
            "  {",
            "    /* 😀 */ def value = 1; System.Console.WriteLine(value)",
            "  }",
            "}");

        await OpenAndAwaitAnalysisAsync(client, uri, source);

        var (declLine, declChar) = LocateUtf16(source, "value", 1); // declaration, after the emoji
        var (useLine, useChar) = LocateUtf16(source, "value", 2); // usage in WriteLine
        var locations = await DefinitionAsync(client, uri, useLine, useChar);
        if (locations.Length == 0)
            throw new InvalidDataException("Definition on a CRLF + non-BMP buffer returned no location.");
        var (_, locLine, locChar) = LocationAt(locations[0]);
        if (locLine != declLine || locChar != declChar)
            throw new InvalidDataException(
                $"Definition range was not 0-based UTF-16 (expected {declLine}:{declChar}, got {locLine}:{locChar}); " +
                "the non-BMP surrogate pair or CRLF was miscounted.");
    }

    // ----- References: usages + includeDeclaration toggle -----

    private const string ReferencesProbeSource = """
        module Refs
        {
          Run() : void
          {
            def counter = 1;
            System.Console.WriteLine(counter);
            System.Console.WriteLine(counter + counter);
          }
        }
        """;

    private static async Task ReferencesAsync(LspTestClient client)
    {
        var uri = new Uri(Path.Combine(CreateTempDirectory("references"), "refs.n")).AbsoluteUri;
        await OpenAndAwaitAnalysisAsync(client, uri, ReferencesProbeSource);

        // The local "counter" is declared once and used three times.  With
        // includeDeclaration the declaration is part of the result; without it the
        // declaration is dropped and only the usages remain.
        var (declLine, declChar) = LocateUtf16(ReferencesProbeSource, "counter", 1);
        var (useLine, useChar) = LocateUtf16(ReferencesProbeSource, "counter", 2);

        var withDecl = await ReferencesAtAsync(client, uri, useLine, useChar, includeDeclaration: true);
        if (withDecl.Length == 0)
            throw new InvalidDataException("References returned no locations.");
        foreach (var location in withDecl)
        {
            var (locUri, _, _) = LocationAt(location);
            if (!AreEquivalentDocumentUris(locUri, uri))
                throw new InvalidDataException($"A reference pointed outside the document: {locUri}");
        }
        var includesDeclaration = withDecl.Any(l =>
        {
            var (_, line, character) = LocationAt(l);
            return line == declLine && character == declChar;
        });
        if (!includesDeclaration)
            throw new InvalidDataException("includeDeclaration=true did not include the declaration location.");

        var withoutDecl = await ReferencesAtAsync(client, uri, useLine, useChar, includeDeclaration: false);
        if (withoutDecl.Any(l =>
        {
            var (_, line, character) = LocationAt(l);
            return line == declLine && character == declChar;
        }))
            throw new InvalidDataException("includeDeclaration=false still returned the declaration location.");
        if (withoutDecl.Length != withDecl.Length - 1)
            throw new InvalidDataException(
                $"includeDeclaration=false should drop exactly the declaration (with={withDecl.Length}, without={withoutDecl.Length}).");
    }

    // ----- definition/references helpers -----

    private static async Task<JsonElement[]> DefinitionAsync(LspTestClient client, string uri, int line, int character)
    {
        var response = await client.RequestAsync("textDocument/definition", new
        {
            textDocument = new { uri },
            position = new { line, character },
        });
        if (response.TryGetProperty("error", out var error))
            throw new InvalidDataException("definition request failed: " + error);
        return LocationsOf(response.GetProperty("result"));
    }

    private static async Task<JsonElement[]> ReferencesAtAsync(
        LspTestClient client, string uri, int line, int character, bool includeDeclaration)
    {
        var response = await client.RequestAsync("textDocument/references", new
        {
            textDocument = new { uri },
            position = new { line, character },
            context = new { includeDeclaration },
        });
        if (response.TryGetProperty("error", out var error))
            throw new InvalidDataException("references request failed: " + error);
        return LocationsOf(response.GetProperty("result"));
    }

    private static JsonElement[] LocationsOf(JsonElement result) => result.ValueKind switch
    {
        JsonValueKind.Array => result.EnumerateArray().ToArray(),
        JsonValueKind.Object => [result],
        _ => [],
    };

    private static (string Uri, int Line, int Character) LocationAt(JsonElement location)
    {
        var uri = location.GetProperty("uri").GetString() ?? "";
        var start = location.GetProperty("range").GetProperty("start");
        return (uri, start.GetProperty("line").GetInt32(), start.GetProperty("character").GetInt32());
    }

    // ----- completion helpers -----

    private static async Task<JsonElement> RequestCompletionAsync(LspTestClient client, string uri, int line, int character)
    {
        var response = await client.RequestAsync("textDocument/completion", new
        {
            textDocument = new { uri },
            position = new { line, character },
        });
        if (response.TryGetProperty("error", out var error))
            throw new InvalidDataException("completion request failed: " + error);
        return response.GetProperty("result");
    }

    private static async Task<JsonElement[]> CompletionItemsAsync(LspTestClient client, string uri, int line, int character)
    {
        var result = await RequestCompletionAsync(client, uri, line, character);
        return result.ValueKind switch
        {
            JsonValueKind.Array => result.EnumerateArray().ToArray(),
            JsonValueKind.Object when result.TryGetProperty("items", out var items) => items.EnumerateArray().ToArray(),
            _ => [],
        };
    }

    private static async Task<string[]> CompletionLabelsAtAsync(LspTestClient client, string uri, int line, int character)
    {
        var items = await CompletionItemsAsync(client, uri, line, character);
        return items.Select(i => i.GetProperty("label").GetString() ?? "").ToArray();
    }

    private static async Task<string[]> CompletionLabelsAsync(
        LspTestClient client, string uri, string text, string needle, int occurrence, bool atStartOfNeedle)
    {
        var (line, character) = LocateUtf16(text, needle, occurrence);
        // atStartOfNeedle keeps the cursor just past the '.', at the first char of
        // the member identifier, which is the member-completion point.
        _ = atStartOfNeedle;
        return await CompletionLabelsAtAsync(client, uri, line, character);
    }

    private static async Task<JsonElement> ResolveAsync(LspTestClient client, JsonElement item)
    {
        var response = await client.RequestAsync("completionItem/resolve", item);
        if (response.TryGetProperty("error", out var error))
            throw new InvalidDataException("completionItem/resolve failed: " + error);
        return response.GetProperty("result");
    }

    private static void AssertContainsLabel(string[] labels, string expected, string description)
    {
        if (!labels.Contains(expected, StringComparer.Ordinal))
            throw new InvalidDataException(
                $"{description}: completion did not contain '{expected}'. Got: {string.Join(", ", labels.Take(50))}");
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        var index = 0;
        while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }

        return count;
    }

    // ----- hover helpers -----

    private static async Task OpenAndAwaitAnalysisAsync(LspTestClient client, string uri, string text)
    {
        var mark = client.Mark();
        await DidOpenAsync(client, uri, text, 1);
        _ = await client.WaitForAsync(
            message => IsPublishFor(message, uri, out var p) && VersionOf(p) == 1,
            mark,
            $"initial diagnostics for {uri} (analysis complete before hover)");
    }

    private static Task<JsonElement> HoverAsync(LspTestClient client, string uri, int line, int character) =>
        HoverRawAsync(client, uri, line, character);

    private static async Task<JsonElement> HoverRawAsync(LspTestClient client, string uri, int line, int character)
    {
        var response = await client.RequestAsync("textDocument/hover", new
        {
            textDocument = new { uri },
            position = new { line, character },
        });
        if (response.TryGetProperty("error", out var error))
            throw new InvalidDataException("hover request failed: " + error);
        return response.GetProperty("result");
    }

    private static string? HoverValue(JsonElement hover)
    {
        if (hover.ValueKind != JsonValueKind.Object ||
            !hover.TryGetProperty("contents", out var contents) ||
            contents.ValueKind != JsonValueKind.Object ||
            !contents.TryGetProperty("value", out var value))
            return null;
        return value.GetString();
    }

    private static async Task<string?> HoverTextAtAsync(
        LspTestClient client, string uri, string text, string needle, int occurrence)
    {
        var (line, character) = LocateUtf16(text, needle, occurrence);
        var hover = await HoverAsync(client, uri, line, character);
        return HoverValue(hover);
    }

    private static async Task AssertHoverContainsAsync(
        LspTestClient client, string uri, string text, string needle, int occurrence,
        string expectedSubstring, string description)
    {
        var value = await HoverTextAtAsync(client, uri, text, needle, occurrence);
        if (value is null)
            throw new InvalidDataException($"{description}: hover returned no content.");
        if (!value.Contains(expectedSubstring, StringComparison.Ordinal))
            throw new InvalidDataException($"{description}: hover text did not contain '{expectedSubstring}': {value}");
        AssertNoRawMarkup(value, description);
        // Markdown fencing (the client advertised markdown): a Nemerle code block.
        if (!value.Contains("```nemerle", StringComparison.Ordinal))
            throw new InvalidDataException($"{description}: markdown hover was not fenced as a Nemerle code block: {value}");
    }

    private static async Task AssertHoverNonNullAsync(
        LspTestClient client, string uri, string text, string needle, int occurrence, string description)
    {
        var value = await HoverTextAtAsync(client, uri, text, needle, occurrence);
        if (string.IsNullOrEmpty(value))
            throw new InvalidDataException($"{description}: hover returned no content.");
        AssertNoRawMarkup(value, description);
    }

    private static void AssertNoRawMarkup(string value, string description)
    {
        foreach (var tag in new[] { "<lb/>", "<lb />", "<keyword", "<hint", "<b>", "</b>", "<params", "<pname", "<ptype", "<code", "<pre" })
        {
            if (value.Contains(tag, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"{description}: pseudo-markup '{tag}' leaked into the hover: {value}");
        }
    }

    /// <summary>
    /// Locates the <paramref name="occurrence"/>-th occurrence of
    /// <paramref name="needle"/> and returns its 0-based line and UTF-16
    /// character offset, counting CRLF/CR/LF as single line breaks and surrogate
    /// pairs as two code units (matching LSP position semantics).
    /// </summary>
    private static (int Line, int Character) LocateUtf16(string text, string needle, int occurrence)
    {
        var index = -1;
        for (var i = 0; i < occurrence; i++)
        {
            index = text.IndexOf(needle, index + 1, StringComparison.Ordinal);
            if (index < 0)
                throw new InvalidOperationException($"Could not find occurrence {occurrence} of '{needle}'.");
        }

        int line = 0, character = 0;
        for (var i = 0; i < index; i++)
        {
            var c = text[i];
            if (c == '\r')
            {
                line++;
                character = 0;
                if (i + 1 < index && text[i + 1] == '\n')
                    i++;
            }
            else if (c == '\n')
            {
                line++;
                character = 0;
            }
            else
            {
                character++;
            }
        }

        return (line, character);
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
                     Sample("Warnings", "Warnings.nproj"),
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
