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
        var probeOnly = args.Contains("--wp-n2-probe", StringComparer.Ordinal);
        var serverArgument = args.FirstOrDefault(argument => !argument.StartsWith("--", StringComparison.Ordinal));
        _serverDll = serverArgument is not null
            ? Path.GetFullPath(serverArgument)
            : Path.Combine(_repoRoot, "dotnet-port", "LspServer", "bin", "Release", "net10.0",
                "Nemerle.LanguageServer.dll");
        if (!File.Exists(_serverDll))
        {
            Console.Error.WriteLine($"Server assembly not found: {_serverDll}");
            return 2;
        }

        try
        {
            if (probeOnly)
            {
                EnsureWpN2FixturesBuilt();
                await RunScenarioAsync("WP-N2 concrete Sokoban/CompTimeSolver reproduction probe",
                    WpN2ConcreteReprosAsync);
                Console.WriteLine("PASS WP-N2 concrete reproduction probe");
                return 0;
            }

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
            await RunScenarioAsync(
                "Semantic tokens: macro-introduced keyword, quotation/escape modifiers, dynamic on using removal",
                SemanticTokensAsync);
            await RunScenarioAsync(
                "Semantic tokens: a keyword from the project's own macro library (macro-only reference)",
                SemanticTokensUserMacroKeywordAsync);
            await RunScenarioAsync(
                "Semantic tokens: a macro library on its own (quotation bodies, compiler API types)",
                SemanticTokensMacroLibraryAsync);
            await RunScenarioAsync(
                "Semantic tokens: the very first request (before any analysis) still answers with tokens",
                SemanticTokensBeforeTheEngineIsReadyAsync);
            await RunScenarioAsync(
                "Semantic tokens: a silent client is nudged until it asks once, then left alone",
                SemanticTokensStartupRefreshRetryAsync);
            await RunScenarioAsync("Incremental: method-body relocation avoids full rebuild; stale/hover/definition hold",
                IncrementalRelocationAsync);
            await RunScenarioAsync("Incremental: structural edit falls back to a full types-tree rebuild",
                IncrementalStructuralFallbackAsync);
            await RunScenarioAsync("Incremental vs full-reload edit-to-diagnostics measurement (Sokoban)",
                IncrementalTimingAsync);
            await RunScenarioAsync("Provenance: matching toolchain is logged and does not interrupt",
                ProvenanceMatchAsync);
            await RunScenarioAsync("Provenance: toolchain/server version mismatch raises window/showMessage",
                ProvenanceMismatchAsync);
            await RunScenarioAsync("Incremental escape hatch off restores full-document sync + reload",
                IncrementalDisabledAsync);
            Console.WriteLine("PASS all WP-L3 LSP integration scenarios");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
    }

    // ----- WP-N2 observation-only probe: concrete reported locations -----

    private static async Task WpN2ConcreteReprosAsync(LspTestClient client)
    {
        var sokobanProject = Sample("Sokoban", "Sokoban", "Sokoban.nproj");
        var loadMark = client.Mark();
        AssertLoadedAndApplied(await LoadProjectAsync(client, sokobanProject), expectedSources: 5);
        _ = await client.WaitForAsync(
            message => LspTestClient.IsLogMessageContaining(message, "engine rebuild finished", out _),
            loadMark, "the initial Sokoban full engine rebuild");

        // N2.1: the R-H hover markup fix (38-prerelease-wp-n2-log.md §5.1/§6 N2.1)
        // expands self-closing <hint value='...' /> tags instead of deleting them,
        // so these four owner-reported Sokoban hovers must now carry their type
        // simple name(s), not just the surrounding namespace/[]/[,] skeleton.
        var probes = new[]
        {
            new { File = "main.n", Needle = "args", Occurrence = 1, ExpectedLine = 7, Label = "function parameter args", ExpectedTypeSubstrings = new[] { "array", "string" } },
            new { File = "splayheap.n", Needle = "SMap", Occurrence = 1, ExpectedLine = 7, Label = "variant field type SMap", ExpectedTypeSubstrings = new[] { "SMap" } },
            new { File = "treesearch.n", Needle = "depth", Occurrence = 1, ExpectedLine = 15, Label = "inferred mutable local depth", ExpectedTypeSubstrings = new[] { "int" } },
            new { File = "sokoban.n", Needle = "Hashtable [string, SMap]", Occurrence = 1, ExpectedLine = 109, Label = "generic field type Hashtable", ExpectedTypeSubstrings = new[] { "Hashtable", "string", "SMap" } },
        };

        var opened = new Dictionary<string, (string Uri, string Text)>();
        string? mainHoverValue = null;
        foreach (var probe in probes)
        {
            var source = Sample("Sokoban", "Sokoban", probe.File);
            var uri = new Uri(source).AbsoluteUri;
            var text = await File.ReadAllTextAsync(source);
            var (line, character) = LocateUtf16(text, probe.Needle, probe.Occurrence);
            if (line != probe.ExpectedLine - 1)
                throw new InvalidDataException(
                    $"{probe.File} probe moved: expected line {probe.ExpectedLine}, located line {line + 1}.");

            var mark = client.Mark();
            await DidOpenAsync(client, uri, text, 1);
            var publish = await client.WaitForAsync(
                message => IsPublishFor(message, uri, out var p) && VersionOf(p) == 1,
                mark, $"analysis of WP-N2 probe {probe.File}:{probe.ExpectedLine}");
            var diagnostics = publish.GetProperty("params").GetProperty("diagnostics");
            var hover = await HoverAsync(client, uri, line, character);
            var value = HoverValue(hover);
            Console.WriteLine(
                $"    HOVER {probe.File}:{line + 1}:{character + 1} ({probe.Label}), diagnostics={diagnostics.GetArrayLength()}: " +
                JsonSerializer.Serialize(value));
            if (value is null)
                throw new InvalidDataException($"R-H {probe.Label}: hover returned no content.");
            AssertNoRawMarkup(value, $"R-H {probe.Label}");
            foreach (var expected in probe.ExpectedTypeSubstrings)
            {
                if (!value.Contains(expected, StringComparison.Ordinal))
                    throw new InvalidDataException(
                        $"R-H {probe.Label}: hover did not contain the expected type text '{expected}': {value}");
            }
            var definitions = await DefinitionAsync(client, uri, line, character);
            Console.WriteLine($"    DEFINITION {probe.File}:{line + 1}:{character + 1} count={definitions.Length}");
            foreach (var definition in definitions)
            {
                var (definitionUri, definitionLine, definitionCharacter) = LocationAt(definition);
                Console.WriteLine(
                    $"      {new Uri(definitionUri!).LocalPath}:{definitionLine + 1}:{definitionCharacter + 1}");
            }
            opened[probe.File] = (uri, text);
            if (probe.File == "main.n")
                mainHoverValue = value;
        }

        if (mainHoverValue is null)
            throw new InvalidDataException("R-H main.n args: initial hover value was not captured for the relocation/reload repeat.");

        // Repeat the first reported hover after the actual relocation path and
        // after a forced project reload.  This distinguishes stable engine text
        // from a one-off bridge/version race, and pins that the fixed (not the
        // historically-broken) text is what survives relocation/reload.
        var main = opened["main.n"];
        var incrementalMark = client.Mark();
        var editedMain = await ReplaceRangedAsync(client, main.Uri, main.Text, "//try", "// try", 2);
        _ = await client.WaitForAsync(
            message => LspTestClient.IsLogMessageContaining(message, "incremental update (relocation)", out _),
            incrementalMark, "Sokoban main.n relocation for the WP-N2 hover repeat");
        _ = await client.WaitForAsync(
            message => IsPublishFor(message, main.Uri, out var p) && VersionOf(p) == 2,
            incrementalMark, "version-2 main.n diagnostics after relocation");
        var (mainLine, mainCharacter) = LocateUtf16(editedMain, "args", 1);
        var relocatedHoverValue = HoverValue(await HoverAsync(client, main.Uri, mainLine, mainCharacter));
        Console.WriteLine("    HOVER main.n after relocation: " + JsonSerializer.Serialize(relocatedHoverValue));
        if (relocatedHoverValue != mainHoverValue)
            throw new InvalidDataException(
                $"R-H function parameter args: hover after relocation ('{relocatedHoverValue}') did not match the initial fixed hover ('{mainHoverValue}').");

        var reloadMark = client.Mark();
        AssertLoadedAndApplied(await LoadProjectAsync(client, sokobanProject), expectedSources: 5);
        _ = await client.WaitForAsync(
            message => LspTestClient.IsLogMessageContaining(message, "engine rebuild finished", out _),
            reloadMark, "forced Sokoban project reload for the WP-N2 hover repeat");
        var reloadedHoverValue = HoverValue(await HoverAsync(client, main.Uri, mainLine, mainCharacter));
        Console.WriteLine("    HOVER main.n after project reload: " + JsonSerializer.Serialize(reloadedHoverValue));
        if (reloadedHoverValue != mainHoverValue)
            throw new InvalidDataException(
                $"R-H function parameter args: hover after project reload ('{reloadedHoverValue}') did not match the initial fixed hover ('{mainHoverValue}').");

        // Declaration-position hovers (the member NAME, not a type position).
        // Two historical warts are pinned fixed here: the engine used to append
        // the member type a second time ("field: elem : SMap; : SMap" -- the
        // type is already part of member.ToString()), and the hover ended with
        // a raw "declared at" path paragraph ("...splayheap.n:7:32:7:43:")
        // written for the VS tooltip; the LSP handler now strips that tail.
        var declarationProbes = new[]
        {
            new
            {
                Needle = "elem", Occurrence = 1, Label = "field declaration elem",
                ExpectedSubstrings = new[] { "field", "elem", "SMap" },
                DuplicationSignature = "; : ",
            },
            new
            {
                Needle = "Min : option", Occurrence = 1, Label = "property declaration Min",
                ExpectedSubstrings = new[] { "Min", "option" },
                DuplicationSignature = "} : ",
            },
        };
        var splayheap = opened["splayheap.n"];
        foreach (var probe in declarationProbes)
        {
            var (line, character) = LocateUtf16(splayheap.Text, probe.Needle, probe.Occurrence);
            var value = HoverValue(await HoverAsync(client, splayheap.Uri, line, character));
            Console.WriteLine(
                $"    HOVER splayheap.n:{line + 1}:{character + 1} ({probe.Label}): " +
                JsonSerializer.Serialize(value));
            if (value is null)
                throw new InvalidDataException($"{probe.Label}: hover returned no content.");
            AssertNoRawMarkup(value, probe.Label);
            foreach (var expected in probe.ExpectedSubstrings)
            {
                if (!value.Contains(expected, StringComparison.Ordinal))
                    throw new InvalidDataException(
                        $"{probe.Label}: hover did not contain '{expected}': {value}");
            }
            if (value.Contains(probe.DuplicationSignature, StringComparison.Ordinal))
                throw new InvalidDataException(
                    $"{probe.Label}: hover still shows the member type twice ('{probe.DuplicationSignature}'): {value}");
            if (value.Contains("splayheap.n:", StringComparison.Ordinal))
                throw new InvalidDataException(
                    $"{probe.Label}: hover still ends with the raw declared-at path paragraph: {value}");
        }

        // R-R/E8 (N2.4): SMap is used throughout the five-source Sokoban
        // project as a field/local/parameter/return-type annotation, a
        // generic type argument, a constructor call and a static-member-
        // access qualifier.  Both a type-annotation origin and the class
        // declaration itself must now return the identical, complete set;
        // the previous legacy collector returned zero from both origins
        // (38-prerelease-wp-n2-log.md §4.3/§5.3).
        var splay = opened["splayheap.n"];
        var (smapLine, smapCharacter) = LocateUtf16(splay.Text, "SMap", 1);
        var referencesFromAnnotation = await ReferencesAtAsync(
            client, splay.Uri, smapLine, smapCharacter, includeDeclaration: true);
        Console.WriteLine(
            $"    REFERENCES splayheap.n:{smapLine + 1}:{smapCharacter + 1} SMap count={referencesFromAnnotation.Length}");
        foreach (var reference in referencesFromAnnotation)
        {
            var (uri, line, character) = LocationAt(reference);
            Console.WriteLine($"      {new Uri(uri!).LocalPath}:{line + 1}:{character + 1}");
        }

        var sokoban = opened["sokoban.n"];
        var (smapDeclarationLine, smapDeclarationCharacter) = LocateUtf16(sokoban.Text, "SMap", 5);
        var referencesFromDeclaration = await ReferencesAtAsync(
            client, sokoban.Uri, smapDeclarationLine, smapDeclarationCharacter, includeDeclaration: true);
        Console.WriteLine(
            $"    REFERENCES sokoban.n:{smapDeclarationLine + 1}:{smapDeclarationCharacter + 1} SMap declaration " +
            $"count={referencesFromDeclaration.Length}");
        foreach (var reference in referencesFromDeclaration)
        {
            var (uri, line, character) = LocationAt(reference);
            Console.WriteLine($"      {new Uri(uri!).LocalPath}:{line + 1}:{character + 1}");
        }

        // Measured exact count: every literal "SMap" occurrence in real code
        // across the five Sokoban sources (field/local/parameter/return-type
        // annotations, generic type arguments such as
        // "Hashtable.[string, SMap]", constructor calls, static-member-access
        // qualifiers such as "SMap.Leq(...)", and the class declaration
        // itself) is 55; a 56th literal occurrence inside a comment
        // ("/* end of SMap class */" in sokoban.n) is correctly excluded
        // since it is not part of the AST.
        const int expectedSmapCount = 55;
        static HashSet<(string Path, int Line, int Character)> ToLocationSet(JsonElement[] locations) =>
            locations.Select(l =>
            {
                var (uri, line, character) = LocationAt(l);
                return (NormalizedLocalPath(uri!), line, character);
            }).ToHashSet();

        if (referencesFromAnnotation.Length != expectedSmapCount)
            throw new InvalidDataException(
                $"R-R SMap from the splayheap.n annotation origin: expected {expectedSmapCount} locations, " +
                $"got {referencesFromAnnotation.Length}.");
        if (referencesFromDeclaration.Length != expectedSmapCount)
            throw new InvalidDataException(
                $"R-R SMap from the sokoban.n declaration origin: expected {expectedSmapCount} locations, " +
                $"got {referencesFromDeclaration.Length}.");
        if (!ToLocationSet(referencesFromAnnotation).SetEquals(ToLocationSet(referencesFromDeclaration)))
            throw new InvalidDataException(
                "R-R SMap: the annotation origin and the declaration origin returned different location sets.");

        var smapDeclarationPath = NormalizedLocalPath(sokoban.Uri);
        var smapDeclarationKey = (smapDeclarationPath, smapDeclarationLine, smapDeclarationCharacter);
        if (!ToLocationSet(referencesFromDeclaration).Contains(smapDeclarationKey))
            throw new InvalidDataException("R-R SMap: includeDeclaration=true did not include the class declaration.");

        var annotationWithoutDeclaration = await ReferencesAtAsync(
            client, splay.Uri, smapLine, smapCharacter, includeDeclaration: false);
        Console.WriteLine(
            $"    REFERENCES splayheap.n:{smapLine + 1}:{smapCharacter + 1} SMap includeDeclaration=false " +
            $"count={annotationWithoutDeclaration.Length}");
        if (annotationWithoutDeclaration.Length != expectedSmapCount - 1)
            throw new InvalidDataException(
                $"R-R SMap: includeDeclaration=false should drop exactly the declaration " +
                $"(with={expectedSmapCount}, without={annotationWithoutDeclaration.Length}).");
        if (ToLocationSet(annotationWithoutDeclaration).Contains(smapDeclarationKey))
            throw new InvalidDataException("R-R SMap: includeDeclaration=false still returned the declaration location.");

        // Historical E7 is broader than the four owner-reported Sokoban hovers.
        // Measure its two engine boundaries explicitly: local/method hint text,
        // and null resolution on a static-call type qualifier / type annotation.
        const string e7Source = """
            class Box
            {
            }

            module E7Probe
            {
              public Compute(value : int) : int
              {
                value + 1
              }

              public Run() : void
              {
                def localValue = Compute(1);
                def annotated : Box = Box();
                _ = E7Probe.Compute(localValue);
                _ = annotated;
                def sb : System.Text.StringBuilder = System.Text.StringBuilder();
                _ = sb;
              }
            }
            """;
        var e7Uri = new Uri(Path.Combine(CreateTempDirectory("wp-n2-e7"), "e7.n")).AbsoluteUri;
        await OpenAndAwaitAnalysisAsync(client, e7Uri, e7Source);
        // E7-D (local value usage, method call): the same hint-value markup fix as
        // R-H applies here, so these two must now show their types too. E7-R
        // (static method type qualifier, project-type annotation): N2.2 fixed
        // Project.FindObject/ExprFinder (38-prerelease-wp-n2-log.md §5.2/§6) so
        // both now resolve too - "Box" already worked before N2.2 (markup-only;
        // see the E7-D fix note above) and must not regress, "E7Probe" was the
        // null-resolution defect N2.2 fixes. ExpectedDefinitionCount pins the
        // definition-side improvement separately from the hover text.
        var e7Probes = new[]
        {
            new { Needle = "localValue", Occurrence = 2, Label = "local value usage", ExpectedTypeSubstrings = (string[]?)["int"], ExpectedDefinitionCount = 1 },
            new { Needle = "Compute", Occurrence = 2, Label = "method call", ExpectedTypeSubstrings = (string[]?)["int"], ExpectedDefinitionCount = 1 },
            new { Needle = "E7Probe", Occurrence = 2, Label = "static method type qualifier", ExpectedTypeSubstrings = (string[]?)["E7Probe"], ExpectedDefinitionCount = 1 },
            new { Needle = "Box", Occurrence = 2, Label = "type annotation", ExpectedTypeSubstrings = (string[]?)["Box"], ExpectedDefinitionCount = 1 },
        };
        foreach (var probe in e7Probes)
        {
            var (line, character) = LocateUtf16(e7Source, probe.Needle, probe.Occurrence);
            var hover = HoverValue(await HoverAsync(client, e7Uri, line, character));
            var definitions = await DefinitionAsync(client, e7Uri, line, character);
            Console.WriteLine(
                $"    E7 {probe.Label} {line + 1}:{character + 1}: " +
                $"hover={JsonSerializer.Serialize(hover)}, definition={definitions.Length}");
            if (probe.ExpectedTypeSubstrings is { } expectedSubstrings)
            {
                if (hover is null)
                    throw new InvalidDataException($"E7 {probe.Label}: hover returned no content.");
                AssertNoRawMarkup(hover, $"E7 {probe.Label}");
                foreach (var expected in expectedSubstrings)
                {
                    if (!hover.Contains(expected, StringComparison.Ordinal))
                        throw new InvalidDataException(
                            $"E7 {probe.Label}: hover did not contain the expected type text '{expected}': {hover}");
                }
            }
            if (definitions.Length != probe.ExpectedDefinitionCount)
                throw new InvalidDataException(
                    $"E7 {probe.Label}: expected {probe.ExpectedDefinitionCount} definition location(s), got {definitions.Length}.");
            foreach (var definition in definitions)
            {
                var (definitionUri, _, _) = LocationAt(definition);
                if (!AreEquivalentDocumentUris(definitionUri, e7Uri))
                    throw new InvalidDataException($"E7 {probe.Label}: definition pointed outside the e7.n fixture: {definitionUri}");
            }
        }

        // E7-R matrix (38 §6 N2.2): an external (BCL) type annotation.  The
        // qualifier/annotation position-resolution fix in FindObject is
        // type-agnostic, so it applies here too, but external types have no
        // workspace source location, so definition staying an empty result is
        // correct behavior (the same shape as DefinitionExternalEmptyAsync),
        // not a defect. Hover is measured rather than required: whether BCL
        // metadata resolves a QuickTip is a separate, independent concern from
        // the position-resolution fix this work item makes (§6 N2.2 - measure,
        // assert only if it resolves).
        {
            var (line, character) = LocateUtf16(e7Source, "StringBuilder", 1);
            var hover = HoverValue(await HoverAsync(client, e7Uri, line, character));
            var definitions = await DefinitionAsync(client, e7Uri, line, character);
            Console.WriteLine(
                $"    E7 external type annotation {line + 1}:{character + 1}: " +
                $"hover={JsonSerializer.Serialize(hover)}, definition={definitions.Length}");
            if (definitions.Length != 0)
                throw new InvalidDataException(
                    $"E7-R external type annotation: definition should be empty (no workspace source), got {definitions.Length}.");
            if (hover is not null)
            {
                AssertNoRawMarkup(hover, "E7-R external type annotation");
                if (!hover.Contains("StringBuilder", StringComparison.Ordinal))
                    throw new InvalidDataException(
                        $"E7-R external type annotation: hover resolved but did not contain 'StringBuilder': {hover}");
            }
        }

        // Historical E8 claimed a containing-type search boundary; that claim is
        // distinct from the SMap/type-annotation zero-result reported above and
        // must itself be tested.  Use a member with usages in two other type
        // declarations so the distinction is visible even in one source file.
        const string e8Source = """
            class Shared
            {
              public Ping() : int { 1 }
            }

            class First
            {
              public Run(value : Shared) : int { value.Ping() }
            }

            class Second
            {
              public Run(value : Shared) : int { value.Ping() }
            }
            """;
        var e8Uri = new Uri(Path.Combine(CreateTempDirectory("wp-n2-e8"), "e8.n")).AbsoluteUri;
        await OpenAndAwaitAnalysisAsync(client, e8Uri, e8Source);
        var e8Path = NormalizedLocalPath(e8Uri);
        var expectedPingLocations = Enumerable.Range(1, 3)
            .Select(occurrence => LocateUtf16(e8Source, "Ping", occurrence))
            .Select(pos => (e8Path, pos.Line, pos.Character))
            .ToHashSet();
        foreach (var occurrence in new[] { 1, 2 })
        {
            var (line, character) = LocateUtf16(e8Source, "Ping", occurrence);
            var crossTypeReferences = await ReferencesAtAsync(
                client, e8Uri, line, character, includeDeclaration: true);
            Console.WriteLine(
                $"    E8 Ping occurrence {occurrence} {line + 1}:{character + 1}: " +
                $"references={crossTypeReferences.Length}");
            foreach (var reference in crossTypeReferences)
            {
                var (_, referenceLine, referenceCharacter) = LocationAt(reference);
                Console.WriteLine($"      e8.n:{referenceLine + 1}:{referenceCharacter + 1}");
            }
            // Positive control (38 §6 N2.4, §5.6): cross-type member usages
            // already worked before N2.4 and must not regress. Ping is
            // declared on Shared and called from two unrelated types (First,
            // Second); both the declaration origin (occurrence 1) and a
            // call-site origin (occurrence 2) must return the same exact
            // 3-location set.
            if (crossTypeReferences.Length != 3)
                throw new InvalidDataException(
                    $"E8 Ping occurrence {occurrence}: expected 3 locations, got {crossTypeReferences.Length}.");
            var actualPingSet = crossTypeReferences.Select(r =>
            {
                var (uri, l, c) = LocationAt(r);
                return (NormalizedLocalPath(uri!), l, c);
            }).ToHashSet();
            if (!actualPingSet.SetEquals(expectedPingLocations))
                throw new InvalidDataException(
                    $"E8 Ping occurrence {occurrence}: returned a different location set than expected.");
        }

        // N2.4 type-usage matrix (38 §6 N2.4): a self-contained fixture
        // covering every annotation shape the current-project semantic walk
        // must recognize - field type, local-variable type, parameter type,
        // return type, and a constructor call - spread across two classes,
        // so the walk must reach both a type's own declaring members and a
        // second, unrelated type's members starting from a single
        // declaration-origin root.
        const string typeUsageMatrixSource = """
            class Widget
            {
            }

            class Container
            {
              private mutable part : Widget;

              public Make() : Widget
              {
                def local : Widget = Widget();
                local
              }

              public Configure(item : Widget) : void
              {
                _ = item;
              }
            }

            class Holder
            {
              private mutable cached : Widget;

              public Wrap(source : Widget) : Widget
              {
                source
              }
            }
            """;
        var typeUsageMatrixUri =
            new Uri(Path.Combine(CreateTempDirectory("wp-n2-type-matrix"), "matrix.n")).AbsoluteUri;
        await OpenAndAwaitAnalysisAsync(client, typeUsageMatrixUri, typeUsageMatrixSource);
        var typeUsageMatrixPath = NormalizedLocalPath(typeUsageMatrixUri);

        // "Widget" appears exactly 9 times in real code: the class
        // declaration, two field-type annotations (Container.part,
        // Holder.cached), a local-variable annotation and a constructor call
        // on the same line in Make(), a return-type annotation in Make(), a
        // parameter-type annotation in Configure(), and a parameter-type plus
        // a return-type annotation (two occurrences) in Wrap().
        var expectedMatrixLocations = Enumerable.Range(1, 9)
            .Select(occurrence => LocateUtf16(typeUsageMatrixSource, "Widget", occurrence))
            .Select(pos => (typeUsageMatrixPath, pos.Line, pos.Character))
            .ToHashSet();

        var (matrixDeclLine, matrixDeclCharacter) = LocateUtf16(typeUsageMatrixSource, "Widget", 1);
        var (matrixAnnotationLine, matrixAnnotationCharacter) = LocateUtf16(typeUsageMatrixSource, "Widget", 2);

        foreach (var (originLabel, originLine, originCharacter) in new[]
        {
            ("declaration", matrixDeclLine, matrixDeclCharacter),
            ("field annotation", matrixAnnotationLine, matrixAnnotationCharacter),
        })
        {
            var references = await ReferencesAtAsync(
                client, typeUsageMatrixUri, originLine, originCharacter, includeDeclaration: true);
            Console.WriteLine(
                $"    N2.4 matrix Widget from {originLabel} {originLine + 1}:{originCharacter + 1}: " +
                $"references={references.Length}");
            if (references.Length != expectedMatrixLocations.Count)
                throw new InvalidDataException(
                    $"N2.4 matrix Widget from {originLabel}: expected {expectedMatrixLocations.Count} locations, " +
                    $"got {references.Length}.");
            var actualMatrixSet = references.Select(r =>
            {
                var (uri, l, c) = LocationAt(r);
                return (NormalizedLocalPath(uri!), l, c);
            }).ToHashSet();
            if (!actualMatrixSet.SetEquals(expectedMatrixLocations))
                throw new InvalidDataException(
                    $"N2.4 matrix Widget from {originLabel}: returned a different location set than expected.");
        }

        var successProject = Sample("CompTimeSolver", "Success", "Success.nproj");
        var successSource = Sample("CompTimeSolver", "Success", "success.n");
        var successUri = new Uri(successSource).AbsoluteUri;
        var successText = await File.ReadAllTextAsync(successSource);
        AssertLoadedAndApplied(await LoadProjectAsync(client, successProject), expectedSources: 1);
        var successMark = client.Mark();
        await DidOpenAsync(client, successUri, successText, 1);
        var successPublish = await client.WaitForAsync(
            message => IsPublishFor(message, successUri, out var p) && VersionOf(p) == 1,
            successMark, "CompTimeSolver/Success editor diagnostics");
        var successDiagnostics = successPublish.GetProperty("params").GetProperty("diagnostics");
        Console.WriteLine("    DIAGNOSTICS CompTimeSolver/Success/success.n: " +
            successDiagnostics.GetRawText());
        // R-P (N2.3 parser parity): a top-level-expression program that builds
        // cleanly with ncc must also analyze cleanly in the IntelliSense-mode
        // engine (no "expecting type declaration" parse errors).
        if (successDiagnostics.GetArrayLength() != 0)
            throw new InvalidOperationException(
                "R-P: CompTimeSolver/Success must produce zero editor diagnostics, got: " +
                successDiagnostics.GetRawText());
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

    // ----- Semantic tokens (WP-O5a) -----

    // Colorization is lexical (the engine's ScanLexer), so this fixture does not
    // need to type-check: what matters is that the `using` puts Nemerle.Surround's
    // syntax macro into the line's GlobalEnv, which is what turns `surroundwith`
    // into a keyword the TextMate grammar could never know about.
    private const string SemanticTokensProbeSource = """
        using Nemerle.Surround;

        module SemanticTokensProbe
        {
          // a line comment
          Run() : void
          {
            def value = 21;
            def text = $"value = $value";
            def quoted = <[ 1 + 2 ]>;
            surroundwith (probe)
              System.Console.WriteLine(text);
            _ = quoted;
          }
        }
        """;

    private static async Task SemanticTokensAsync(LspTestClient client)
    {
        var (legendTypes, legendModifiers) = ServerSemanticTokensLegend(client);
        // The legend is the server's contract with the client; pin its head and
        // the two Nemerle-specific modifiers (the extension manifest declares the
        // same names).
        if (legendTypes.Length < 2 || legendTypes[0] != "keyword" || legendTypes[1] != "macro")
            throw new InvalidDataException(
                "semantic tokens legend did not start with keyword/macro: " + string.Join(",", legendTypes));
        if (!legendModifiers.SequenceEqual(["quotation", "escape"]))
            throw new InvalidDataException(
                "semantic tokens legend modifiers changed: " + string.Join(",", legendModifiers));

        var uri = new Uri(Path.Combine(CreateTempDirectory("semantic-tokens"), "tokens.n")).AbsoluteUri;
        var openMark = client.Mark();
        await OpenAndAwaitAnalysisAsync(client, uri, SemanticTokensProbeSource);
        // A line's keyword set comes from its GlobalEnv, which exists only after
        // the types tree was built - so the server asks the client to re-query
        // then.  Waiting for that request is both how a real client behaves and
        // what makes this scenario deterministic instead of racing the build.
        _ = await client.WaitForAsync(
            LspTestClient.IsSemanticTokensRefresh,
            openMark,
            "the server's semantic tokens refresh request after the first types-tree build");
        var tokens = await RequestSemanticTokensAsync(client, uri);
        if (tokens.Length == 0)
            throw new InvalidDataException("semanticTokens/full returned no tokens.");

        // Plain keywords, literals and comments keep their obvious classification.
        AssertToken(tokens, SemanticTokensProbeSource, "using", 1, "keyword", [], "the using keyword");
        AssertToken(tokens, SemanticTokensProbeSource, "module", 1, "keyword", [], "the module keyword");
        AssertToken(tokens, SemanticTokensProbeSource, "def", 1, "keyword", [], "the def keyword");
        AssertToken(tokens, SemanticTokensProbeSource, "21", 1, "number", [], "an integer literal");
        AssertToken(
            tokens, SemanticTokensProbeSource, "// a line comment", 1, "comment", [], "a line comment");

        // The differentiator: `surroundwith` is a keyword only because the using
        // opened a namespace whose syntax macro defines it.
        AssertToken(
            tokens, SemanticTokensProbeSource, "surroundwith", 1, "macro", [],
            "a keyword introduced by a syntax macro");

        // Quasi-quotation: the delimiters and everything inside carry the
        // quotation modifier, which is how a client can shade the quoted region.
        AssertToken(
            tokens, SemanticTokensProbeSource, "<[", 1, "operator", ["quotation"], "the <[ delimiter");
        AssertToken(
            tokens, SemanticTokensProbeSource, "1 + 2", 1, "number", ["quotation"],
            "a literal inside a quotation");

        // A string literal is one plain run plus an escape/splice run for `$value`.
        var (stringLine, _) = LocateUtf16(SemanticTokensProbeSource, "$\"value = $value\"", 1);
        if (!tokens.Any(t => t.Line == stringLine && t.Type == "string" && t.Modifiers.Length == 0))
            throw new InvalidDataException($"no plain string token on line {stringLine + 1}.");
        if (!tokens.Any(t => t.Line == stringLine && t.Type == "string" && t.Modifiers.Contains("escape")))
            throw new InvalidDataException(
                $"the $-splice inside the string literal on line {stringLine + 1} was not marked as an escape run.");

        // Dynamic, not a hardcoded word list: drop the using and the very same
        // word is just an identifier again.
        var withoutUsing = SemanticTokensProbeSource.Replace(
            "using Nemerle.Surround;", "// no macro import", StringComparison.Ordinal);
        var mark = client.Mark();
        await DidChangeAsync(client, uri, withoutUsing, 2);
        _ = await client.WaitForAsync(
            message => IsPublishFor(message, uri, out var p) && VersionOf(p) == 2,
            mark,
            "analysis of the probe without the macro import");
        _ = await client.WaitForAsync(
            LspTestClient.IsSemanticTokensRefresh, mark, "the refresh request for the rebuilt buffer");
        var afterTokens = await RequestSemanticTokensAsync(client, uri);
        AssertToken(
            afterTokens, withoutUsing, "surroundwith", 1, "variable", [],
            "the same word with the macro import removed");
        AssertToken(afterTokens, withoutUsing, "def", 1, "keyword", [], "a core keyword is unaffected");

        // A real, large, macro-using source (654 lines) as a loose file: the whole
        // document is colorized per request, so record what that costs and that it
        // survives a file the compiler cannot fully resolve.
        var sokoban = Sample("Sokoban", "Sokoban", "sokoban.n");
        var sokobanUri = new Uri(sokoban).AbsoluteUri;
        var sokobanMark = client.Mark();
        await OpenAndAwaitAnalysisAsync(client, sokobanUri, await File.ReadAllTextAsync(sokoban));
        _ = await client.WaitForAsync(
            LspTestClient.IsSemanticTokensRefresh, sokobanMark, "the refresh request after opening sokoban.n");
        _ = await RequestSemanticTokensAsync(client, sokobanUri);
        var stopwatch = Stopwatch.StartNew();
        var sokobanTokens = await RequestSemanticTokensAsync(client, sokobanUri);
        stopwatch.Stop();
        Console.WriteLine(
            $"    warm semanticTokens/full round-trip on sokoban.n (654 lines): " +
            $"{stopwatch.Elapsed.TotalMilliseconds:F0} ms, {sokobanTokens.Length} tokens");
        if (sokobanTokens.Length == 0)
            throw new InvalidDataException("semanticTokens/full returned no tokens for sokoban.n.");
    }

    /// <summary>
    /// A client that never asks for tokens must be nudged more than once: on a
    /// window reload the editor restores its documents while the server is still
    /// starting, and the refresh that follows the startup build can arrive before
    /// the client attached the document, where it is dropped rather than queued
    /// (observed on WSL: the first paint kept grammar coloring until the file was
    /// edited or the color theme switched).  This pins the retry: stay silent and
    /// count the refreshes, then ask once and require that the retries stop.
    /// </summary>
    private static async Task SemanticTokensStartupRefreshRetryAsync(LspTestClient client)
    {
        var uri = new Uri(Path.Combine(CreateTempDirectory("semantic-tokens-refresh"), "quiet.n")).AbsoluteUri;

        // A real client asks as soon as it registers the provider, which at
        // startup is before the document reached the workspace and before the
        // compiler initialized.  That answer is necessarily empty, and it must not
        // be mistaken for "the client is served" - doing so silently disabled the
        // nudging and left the first paint uncolored (WSL, 53-wp-o5-log.md).
        var early = await RequestSemanticTokensAsync(client, uri);
        if (early.Length != 0)
            throw new InvalidDataException(
                $"a request for a document that is not open returned {early.Length} tokens.");

        var mark = client.Mark();
        await OpenAndAwaitAnalysisAsync(client, uri, SemanticTokensProbeSource);

        // Retry schedule is 1s/3s/8s after the build, so ~5 s of silence must
        // produce the initial refresh plus at least the first two retries.
        await client.DrainAsync(TimeSpan.FromSeconds(5));
        var refreshesWhileSilent = client.CountMessages(LspTestClient.IsSemanticTokensRefresh, mark);
        Console.WriteLine($"    refresh requests while the client stayed silent: {refreshesWhileSilent}");
        if (refreshesWhileSilent < 2)
            throw new InvalidDataException(
                $"the server nudged a silent client only {refreshesWhileSilent} time(s); the startup retry is not working.");

        // One request is enough to prove the client is attached: no more nudging
        // (a client that keeps being told to re-query would re-render forever).
        var tokens = await RequestSemanticTokensAsync(client, uri);
        if (tokens.Length == 0)
            throw new InvalidDataException("semanticTokens/full returned no tokens for the retry probe.");

        await client.AssertQuietAsync(
            LspTestClient.IsSemanticTokensRefresh,
            client.Mark(),
            TimeSpan.FromSeconds(6),
            "semantic tokens refresh after the client asked once");
    }

    /// <summary>
    /// The same feature, but with a keyword defined by a USER macro instead of one
    /// that ships inside Nemerle.dll.  This is the path the differentiator actually
    /// claims ("any keyword your own macros define") and it runs through different
    /// plumbing: the keyword only exists because the engine loaded
    /// samples/SyntaxMacro/SyntaxMacros.dll as a macro-only reference of the
    /// consuming project (NemerleMacroReference -> GetMacroAssemblyReferences ->
    /// LoadPluginsFrom), and the file opened that namespace.
    /// </summary>
    private static async Task SemanticTokensUserMacroKeywordAsync(LspTestClient client)
    {
        var project = Sample("SyntaxMacro", "SyntaxDemo", "SyntaxDemo.nproj");
        AssertLoadedAndApplied(await LoadProjectAsync(client, project), expectedSources: 1);

        var source = Sample("SyntaxMacro", "SyntaxDemo", "Program.n");
        var uri = new Uri(source).AbsoluteUri;
        var text = await File.ReadAllTextAsync(source);
        await OpenAndAwaitAnalysisAsync(client, uri, text);

        var tokens = await RequestSemanticTokensAsync(client, uri);
        AssertToken(
            tokens, text, "twice", 1, "macro", [],
            "a keyword defined by a macro library of this project");
        AssertToken(tokens, text, "mutable", 1, "keyword", [], "a core keyword in the same file");
        AssertToken(tokens, text, "module", 1, "keyword", [], "another core keyword");

        // Same proof of dynamism as the stdlib scenario, one level further out: the
        // keyword disappears with the using, even though the macro reference stays.
        var withoutUsing = text.Replace("using SyntaxMacro;", "// no macro import", StringComparison.Ordinal);
        var mark = client.Mark();
        await DidChangeAsync(client, uri, withoutUsing, 2);
        _ = await client.WaitForAsync(
            message => IsPublishFor(message, uri, out var p) && VersionOf(p) == 2,
            mark,
            "analysis of the demo without the macro import");
        var afterTokens = await RequestSemanticTokensAsync(client, uri);
        AssertToken(
            afterTokens, withoutUsing, "twice", 1, "variable", [],
            "the same word once the macro namespace is not opened");

        // Leave the fixture's source as it is on disk for the next scenarios.
        await DidChangeAsync(client, uri, text, 3);
        await DidCloseAsync(client, uri);
    }

    /// <summary>
    /// The macro library on its own: a project whose only source is macro
    /// definitions.  It is written against the compiler's own API and is mostly
    /// quasi-quotation, so it exercises what no other fixture does - the
    /// <c>quotation</c> modifier over a real <c>&lt;[ ... ]&gt;</c> body, and user
    /// types resolved from Nemerle.Compiler.dll.
    /// </summary>
    private static async Task SemanticTokensMacroLibraryAsync(LspTestClient client)
    {
        var project = Sample("SyntaxMacro", "SyntaxMacros", "SyntaxMacros.nproj");
        AssertLoadedAndApplied(await LoadProjectAsync(client, project), expectedSources: 1);

        var source = Sample("SyntaxMacro", "SyntaxMacros", "macros.n");
        var uri = new Uri(source).AbsoluteUri;
        var text = await File.ReadAllTextAsync(source);
        await OpenAndAwaitAnalysisAsync(client, uri, text);

        var tokens = await RequestSemanticTokensAsync(client, uri);
        if (tokens.Length == 0)
            throw new InvalidDataException("a macro definition file produced no semantic tokens.");

        // Needles that also occur in the file's prose comments would match there
        // first, so anchor on the declaration itself.
        AssertToken(tokens, text, "macro Twice", 1, "keyword", [], "the macro declaration keyword");
        AssertToken(tokens, text, "<[", 1, "operator", ["quotation"], "the quotation delimiter");

        // Everything between <[ and ]> is inside the quotation, including the
        // splices; nothing outside it may carry the modifier.
        var (quotationLine, _) = LocateUtf16(text, "<[", 1);
        var inQuotation = tokens
            .Where(t => t.Line == quotationLine && t.Modifiers.Contains("quotation"))
            .ToArray();
        if (inQuotation.Length < 3)
            throw new InvalidDataException(
                $"expected the quotation body to be marked, but line {quotationLine + 1} has only " +
                $"{inQuotation.Length} quotation token(s).");
        var (usingLine, _) = LocateUtf16(text, "using", 1);
        if (tokens.Any(t => t.Line == usingLine && t.Modifiers.Contains("quotation")))
            throw new InvalidDataException("a token outside any quotation carries the quotation modifier.");

        // A type from the referenced compiler assembly: resolving it needs the
        // types tree, so this also pins that user types are classified at all.
        var pexpr = tokens.FirstOrDefault(t =>
        {
            var (line, character) = LocateUtf16(text, "PExpr)", 1);
            return t.Line == line && t.Character == character;
        });
        Console.WriteLine($"    the PExpr parameter type classified as: {pexpr?.Type ?? "(no token)"}");
        if (pexpr is null || pexpr.Type is not ("class" or "type" or "struct" or "interface" or "enum"))
            throw new InvalidDataException(
                $"the PExpr parameter type was classified as '{pexpr?.Type ?? "(none)"}' instead of a type.");

        await DidCloseAsync(client, uri);
    }

    /// <summary>
    /// The first paint after a window reload depends on a single request: the
    /// client asks as soon as it registers the provider - before the compiler
    /// initialized - and caches the answer.  Measured on WSL, an empty answer is
    /// never re-queried (not even on workspace/semanticTokens/refresh), so that
    /// one request must be answered with real tokens.  This pins it: open a
    /// document and ask immediately, without waiting for any analysis.
    /// </summary>
    private static async Task SemanticTokensBeforeTheEngineIsReadyAsync(LspTestClient client)
    {
        var uri = new Uri(Path.Combine(CreateTempDirectory("semantic-tokens-cold"), "cold.n")).AbsoluteUri;
        await DidOpenAsync(client, uri, SemanticTokensProbeSource, 1);

        var stopwatch = Stopwatch.StartNew();
        var tokens = await RequestSemanticTokensAsync(client, uri);
        Console.WriteLine(
            $"    cold semanticTokens/full (asked before any analysis): {stopwatch.Elapsed.TotalMilliseconds:F0} ms, {tokens.Length} tokens");
        if (tokens.Length == 0)
            throw new InvalidDataException(
                "the first request answered with no tokens; the client caches that and the document stays uncolored.");

        // Not just any tokens: the macro keyword needs the types tree, which is
        // exactly what the request had to wait for.
        AssertToken(
            tokens, SemanticTokensProbeSource, "surroundwith", 1, "macro", [],
            "a macro-introduced keyword in the very first answer");
    }

    private sealed record DecodedSemanticToken(int Line, int Character, int Length, string Type, string[] Modifiers);

    private static (string[] Types, string[] Modifiers) ServerSemanticTokensLegend(LspTestClient client)
    {
        if (!client.InitializeResult.TryGetProperty("capabilities", out var capabilities) ||
            !capabilities.TryGetProperty("semanticTokensProvider", out var provider) ||
            !provider.TryGetProperty("legend", out var legend))
            throw new InvalidDataException(
                "the server did not advertise semanticTokensProvider with a legend: " + client.InitializeResult);

        return (
            legend.GetProperty("tokenTypes").EnumerateArray().Select(e => e.GetString() ?? "").ToArray(),
            legend.GetProperty("tokenModifiers").EnumerateArray().Select(e => e.GetString() ?? "").ToArray());
    }

    /// <summary>
    /// Requests <c>textDocument/semanticTokens/full</c> and decodes the flat
    /// 5-tuple delta encoding (LSP 3.17 §textDocument/semanticTokens) back into
    /// absolute positions and legend names.
    /// </summary>
    private static async Task<DecodedSemanticToken[]> RequestSemanticTokensAsync(LspTestClient client, string uri)
    {
        var response = await client.RequestAsync("textDocument/semanticTokens/full", new
        {
            textDocument = new { uri },
        });
        if (response.TryGetProperty("error", out var error))
            throw new InvalidDataException("semanticTokens/full request failed: " + error);
        var result = response.GetProperty("result");
        if (result.ValueKind != JsonValueKind.Object || !result.TryGetProperty("data", out var data))
            throw new InvalidDataException("semanticTokens/full returned no data: " + result);

        var (legendTypes, legendModifiers) = ServerSemanticTokensLegend(client);
        var tokens = new List<DecodedSemanticToken>();
        var line = 0;
        var character = 0;
        var length = data.GetArrayLength();
        if (length % 5 != 0)
            throw new InvalidDataException($"semanticTokens/full data length {length} is not a multiple of 5.");

        for (var i = 0; i < length; i += 5)
        {
            var deltaLine = data[i].GetInt32();
            var deltaStart = data[i + 1].GetInt32();
            line += deltaLine;
            character = deltaLine == 0 ? character + deltaStart : deltaStart;

            var typeIndex = data[i + 3].GetInt32();
            if (typeIndex < 0 || typeIndex >= legendTypes.Length)
                throw new InvalidDataException($"semantic token type index {typeIndex} is outside the legend.");

            var bits = data[i + 4].GetInt32();
            var modifiers = new List<string>();
            for (var bit = 0; bit < legendModifiers.Length; bit++)
            {
                if ((bits & (1 << bit)) != 0)
                    modifiers.Add(legendModifiers[bit]);
            }

            tokens.Add(new DecodedSemanticToken(
                line, character, data[i + 2].GetInt32(), legendTypes[typeIndex], modifiers.ToArray()));
        }

        return tokens.ToArray();
    }

    private static void AssertToken(
        DecodedSemanticToken[] tokens,
        string source,
        string needle,
        int occurrence,
        string expectedType,
        string[] expectedModifiers,
        string description)
    {
        var (line, character) = LocateUtf16(source, needle, occurrence);
        var token = tokens.FirstOrDefault(t => t.Line == line && t.Character == character)
            ?? throw new InvalidDataException(
                $"{description}: no semantic token starts at {line + 1}:{character + 1} ('{needle}').");
        if (token.Type != expectedType)
            throw new InvalidDataException(
                $"{description}: expected token type '{expectedType}' at {line + 1}:{character + 1} ('{needle}'), got '{token.Type}'.");
        if (!token.Modifiers.OrderBy(m => m, StringComparer.Ordinal)
                .SequenceEqual(expectedModifiers.OrderBy(m => m, StringComparer.Ordinal)))
            throw new InvalidDataException(
                $"{description}: expected modifiers [{string.Join(",", expectedModifiers)}] at " +
                $"{line + 1}:{character + 1} ('{needle}'), got [{string.Join(",", token.Modifiers)}].");
    }

    // ----- Incremental rebuild (relocation) scenarios (WP-M5) -----

    private const string IncrementalBaseSource = """
        module IncrementalProbe
        {
          public Run() : void
          {
            def value : int = 1;
            System.Console.WriteLine(value);
          }

          public Other() : int
          {
            42
          }
        }
        """;

    private static async Task<string> SetupIncrementalProjectAsync(
        LspTestClient client, string suffix, string source)
    {
        var dir = CreateTempDirectory(suffix);
        var projectPath = Path.Combine(dir, "Temp.nproj");
        var sourcePath = Path.Combine(dir, "probe.n");
        var uri = new Uri(sourcePath).AbsoluteUri;
        var coreTargets = Path.Combine(_repoRoot, "dotnet-port", "msbuild", "Nemerle.Core.targets");
        await File.WriteAllTextAsync(sourcePath, source);
        await File.WriteAllTextAsync(projectPath, TempProjectXml(coreTargets, ["probe.n"]));
        RunDotnet($"restore \"{projectPath}\"", dir);
        AssertLoadedAndApplied(await LoadProjectAsync(client, projectPath), expectedSources: 1);
        return uri;
    }

    private static async Task IncrementalRelocationAsync(LspTestClient client)
    {
        var uri = await SetupIncrementalProjectAsync(client, "incremental-reloc", IncrementalBaseSource);
        var text = IncrementalBaseSource;

        var openMark = client.Mark();
        await DidOpenAsync(client, uri, text, 1);
        await client.WaitForAsync(
            message => IsPublishFor(message, uri, out var p) && VersionOf(p) == 1 && CountOf(p) == 0,
            openMark, "clean analysis of the opened project source");

        // 1. A method-body edit that introduces a type error is answered by a
        //    relocation (method re-typing), not a full types-tree rebuild.
        var editMark = client.Mark();
        var stopwatch = Stopwatch.StartNew();
        text = await ReplaceRangedAsync(client, uri, text, "= 1;", "= \"x\";", 2);

        // Acceptance 1: the edit takes the relocation path.  Anchor the "no full
        // rebuild" check just after the relocation trace so a late-arriving
        // rebuild-finished trace from the initial open (its logMessage can trail
        // the version-1 publish this scenario already waited for) is not mistaken
        // for one caused by the edit.
        _ = await client.WaitForAsync(
            message => LspTestClient.IsLogMessageContaining(message, "incremental update (relocation)", out _),
            editMark, "the incremental relocation trace for the in-body edit");
        var quietMark = client.Mark();
        _ = await client.WaitForAsync(
            message => IsPublishFor(message, uri, out var p) && VersionOf(p) == 2 && HasError(p),
            editMark, "error diagnostics after the in-body type error edit (version 2)");
        stopwatch.Stop();
        Console.WriteLine($"    in-body edit-to-error: {stopwatch.Elapsed.TotalMilliseconds:F0} ms");

        await client.AssertQuietAsync(
            message => LspTestClient.IsLogMessageContaining(message, "nemerle engine rebuild finished", out _),
            quietMark, TimeSpan.FromSeconds(2),
            "a full types-tree rebuild trace after a method-body relocation");

        // 2. Fixing the body clears the error, again via relocation.
        var fixMark = client.Mark();
        text = await ReplaceRangedAsync(client, uri, text, "= \"x\";", "= 2;", 3);
        _ = await client.WaitForAsync(
            message => IsPublishFor(message, uri, out var p) && VersionOf(p) == 3 && CountOf(p) == 0,
            fixMark, "cleared diagnostics after fixing the in-body error (version 3)");

        // 3. Rapid stale sequence: version 4 (broken) is immediately superseded by
        //    version 5 (fixed); no error may survive the clean version-5 publish.
        var staleMark = client.Mark();
        text = await ReplaceRangedAsync(client, uri, text, "= 2;", "= \"y\";", 4);
        text = await ReplaceRangedAsync(client, uri, text, "= \"y\";", "= 3;", 5);
        _ = await client.WaitForAsync(
            message => IsPublishFor(message, uri, out var p) && VersionOf(p) == 5 && CountOf(p) == 0,
            staleMark, "clean version-5 diagnostics after the rapid relocation sequence");
        var afterStale = client.Mark();
        await client.AssertQuietAsync(
            message => IsPublishFor(message, uri, out var p) && HasError(p),
            afterStale, TimeSpan.FromSeconds(2), "stale error diagnostics after the clean version-5 publish");

        // 4. hover + definition remain correct with incremental enabled (acceptance 7).
        var (vLine, vChar) = LocateUtf16(text, "value", 2);
        var hover = await HoverAsync(client, uri, vLine, vChar);
        if (HoverValue(hover) is not { Length: > 0 } hoverText)
            throw new InvalidDataException("Hover after incremental edits returned no content.");
        AssertNoRawMarkup(hoverText, "hover after incremental edits");
        if ((await DefinitionAsync(client, uri, vLine, vChar)).Length == 0)
            throw new InvalidDataException("Definition after incremental edits returned no location.");
    }

    private static async Task IncrementalStructuralFallbackAsync(LspTestClient client)
    {
        var uri = await SetupIncrementalProjectAsync(client, "incremental-struct", IncrementalBaseSource);
        var text = IncrementalBaseSource;

        var openMark = client.Mark();
        await DidOpenAsync(client, uri, text, 1);
        await client.WaitForAsync(
            message => IsPublishFor(message, uri, out var p) && VersionOf(p) == 1 && CountOf(p) == 0,
            openMark, "clean analysis before the structural edit");

        // Inserting a whole new method changes the compile unit's structure, so the
        // engine falls back from relocation to a full types-tree rebuild.
        var editMark = client.Mark();
        var (line, ch) = LocateUtf16(text, "public Other()", 1);
        var index = LineCharToIndex(text, line, ch);
        const string inserted = "public Added() : int\n  {\n    7\n  }\n\n  ";
        await DidChangeRangedAsync(client, uri, 2, line, ch, line, ch, inserted);
        text = string.Concat(text.AsSpan(0, index), inserted, text.AsSpan(index));

        _ = await client.WaitForAsync(
            message => IsPublishFor(message, uri, out var p) && VersionOf(p) == 2 && CountOf(p) == 0,
            editMark, "clean diagnostics after the structural (added method) edit");
        // Acceptance 3: the fallback rebuilt the types tree.
        _ = await client.WaitForAsync(
            message => LspTestClient.IsLogMessageContaining(message, "nemerle engine rebuild finished", out _),
            editMark, "a full types-tree rebuild trace after the structural edit");

        // hover / definition are correct after the fallback rebuild.
        var (aLine, aChar) = LocateUtf16(text, "Added", 1);
        if (HoverValue(await HoverAsync(client, uri, aLine, aChar)) is not { Length: > 0 })
            throw new InvalidDataException("Hover on the newly added method returned nothing after the rebuild.");
        if ((await DefinitionAsync(client, uri, aLine, aChar)).Length == 0)
            throw new InvalidDataException("Definition on the newly added method returned no location after the rebuild.");
    }

    private static async Task IncrementalTimingAsync(LspTestClient client)
    {
        var sokobanProject = Sample("Sokoban", "Sokoban", "Sokoban.nproj");
        var mainPath = Sample("Sokoban", "Sokoban", "main.n");
        var mainUri = new Uri(mainPath).AbsoluteUri;
        var mainText = await File.ReadAllTextAsync(mainPath);

        var on = await MeasureEditToDiagnosticsAsync(client, sokobanProject, mainUri, mainText, incremental: true);
        Console.WriteLine($"    incremental ON  edit-to-diagnostics: p50 {on.P50:F0} ms, p95 {on.P95:F0} ms (n={on.N})");

        await using var offClient = await LspTestClient.StartAsync(_serverDll, _repoRoot,
            new Dictionary<string, string> { ["NEMERLE_INCREMENTAL_UPDATE"] = "0" });
        var off = await MeasureEditToDiagnosticsAsync(offClient, sokobanProject, mainUri, mainText, incremental: false);
        Console.WriteLine($"    incremental OFF edit-to-diagnostics: p50 {off.P50:F0} ms, p95 {off.P95:F0} ms (n={off.N})");
        await offClient.ShutdownAsync();

        if (on.P50 > 2000)
            throw new InvalidDataException(
                $"Incremental edit-to-diagnostics p50 {on.P50:F0} ms exceeded the 2000 ms sanity ceiling.");
    }

    private static async Task IncrementalDisabledAsync(LspTestClient defaultClient)
    {
        _ = defaultClient; // The escape hatch needs its own server; the default client stays idle.
        await using var client = await LspTestClient.StartAsync(_serverDll, _repoRoot,
            new Dictionary<string, string> { ["NEMERLE_INCREMENTAL_UPDATE"] = "0" });

        // Acceptance 6: with the escape hatch off the server advertises full-document
        // sync (change kind 1), not incremental (2).
        var sync = client.InitializeResult.GetProperty("capabilities").GetProperty("textDocumentSync");
        var change = sync.ValueKind == JsonValueKind.Number ? sync.GetInt32() : sync.GetProperty("change").GetInt32();
        if (change != 1)
            throw new InvalidDataException($"Escape hatch off should advertise full sync (1); got {change}.");

        var uri = await SetupIncrementalProjectAsync(client, "incremental-off", IncrementalBaseSource);
        var text = IncrementalBaseSource;
        var openMark = client.Mark();
        await DidOpenAsync(client, uri, text, 1);
        await client.WaitForAsync(
            message => IsPublishFor(message, uri, out var p) && VersionOf(p) == 1 && CountOf(p) == 0,
            openMark, "clean analysis (escape hatch off)");

        // Full-document changes still drive diagnostics (previous behavior).
        var brokenText = text.Replace("= 1;", "= \"x\";", StringComparison.Ordinal);
        var editMark = client.Mark();
        await DidChangeAsync(client, uri, brokenText, 2);
        _ = await client.WaitForAsync(
            message => IsPublishFor(message, uri, out var p) && VersionOf(p) == 2 && HasError(p),
            editMark, "error diagnostics with the escape hatch off (full reload)");
        if (client.LogMessages(openMark).Any(m =>
                m.Message.Contains("incremental update (relocation)", StringComparison.Ordinal)))
            throw new InvalidDataException("The relocation path ran even though the escape hatch is off.");

        var fixMark = client.Mark();
        await DidChangeAsync(client, uri, text, 3);
        _ = await client.WaitForAsync(
            message => IsPublishFor(message, uri, out var p) && VersionOf(p) == 3 && CountOf(p) == 0,
            fixMark, "cleared diagnostics with the escape hatch off");

        await client.ShutdownAsync();
    }

    private static async Task<(double P50, double P95, int N)> MeasureEditToDiagnosticsAsync(
        LspTestClient client, string projectPath, string uri, string baseText, bool incremental)
    {
        AssertLoadedAndApplied(await LoadProjectAsync(client, projectPath), expectedSources: 5);
        var text = baseText;
        var openMark = client.Mark();
        await DidOpenAsync(client, uri, text, 1);
        await client.WaitForAsync(
            message => IsPublishFor(message, uri, out var p) && VersionOf(p) == 1,
            openMark, "initial Sokoban main.n analysis");

        const string markerA = "//try";
        const string markerB = "//trz";
        var version = 1;

        // A warm-up edit excluded from the samples.
        version++;
        text = await SendToggleAsync(client, uri, text, markerA, markerB, version, incremental);
        await client.WaitForAsync(
            message => IsPublishFor(message, uri, out var p) && VersionOf(p) == version, openMark,
            "warm-up edit publish");

        var samples = new List<double>();
        for (var i = 0; i < 12; i++)
        {
            var (from, to) = i % 2 == 0 ? (markerB, markerA) : (markerA, markerB);
            version++;
            var editMark = client.Mark();
            var stopwatch = Stopwatch.StartNew();
            text = await SendToggleAsync(client, uri, text, from, to, version, incremental);
            var capturedVersion = version;
            await client.WaitForAsync(
                message => IsPublishFor(message, uri, out var p) && VersionOf(p) == capturedVersion, editMark,
                $"diagnostics publish for version {capturedVersion}");
            stopwatch.Stop();
            samples.Add(stopwatch.Elapsed.TotalMilliseconds);
        }

        samples.Sort();
        return (samples[samples.Count / 2], samples[(int)(samples.Count * 0.95)], samples.Count);
    }

    private static async Task<string> SendToggleAsync(
        LspTestClient client, string uri, string text, string from, string to, int version, bool incremental)
    {
        var (line, ch) = LocateUtf16(text, from, 1);
        var index = LineCharToIndex(text, line, ch);
        var updated = string.Concat(text.AsSpan(0, index), to, text.AsSpan(index + from.Length));
        if (incremental)
            await DidChangeRangedAsync(client, uri, version, line, ch, line, ch + from.Length, to);
        else
            await DidChangeAsync(client, uri, updated, version);
        return updated;
    }

    /// <summary>
    /// Sends a single-line ranged replacement of <paramref name="oldText"/> with
    /// <paramref name="newText"/> and returns the buffer text the client now holds.
    /// </summary>
    private static async Task<string> ReplaceRangedAsync(
        LspTestClient client, string uri, string text, string oldText, string newText, int version)
    {
        var (line, ch) = LocateUtf16(text, oldText, 1);
        var index = LineCharToIndex(text, line, ch);
        await DidChangeRangedAsync(client, uri, version, line, ch, line, ch + oldText.Length, newText);
        return string.Concat(text.AsSpan(0, index), newText, text.AsSpan(index + oldText.Length));
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

    // ----- WP-M6 provenance (acceptance 6) -----

    /// <summary>
    /// A project built with the repository's own dist\ncc - the same bits this server was
    /// packed from - must report the toolchain at Info level and must NOT interrupt the user.
    /// The negative half matters as much as the positive one: a warning that also fires on
    /// correct setups is a warning nobody reads.
    /// </summary>
    private static async Task ProvenanceMatchAsync(LspTestClient client)
    {
        var dir = CreateTempDirectory("provenance-match");
        var projectPath = Path.Combine(dir, "Temp.nproj");
        var coreTargets = Path.Combine(_repoRoot, "dotnet-port", "msbuild", "Nemerle.Core.targets");
        await File.WriteAllTextAsync(Path.Combine(dir, "probe.n"), "module Probe { Value : int = 1; }");
        await File.WriteAllTextAsync(projectPath, TempProjectXml(coreTargets, ["probe.n"]));
        RunDotnet($"restore \"{projectPath}\"", dir);

        var mark = client.Mark();
        AssertLoadedAndApplied(await LoadProjectAsync(client, projectPath), expectedSources: 1);

        var logs = client.LogMessages(mark);
        var matched = logs.Any(entry =>
            entry.Message.Contains("nemerle project toolchain:", StringComparison.Ordinal) &&
            entry.Message.Contains("matches the language server", StringComparison.Ordinal));
        if (!matched)
            throw new InvalidDataException(
                "Expected an Info log naming the project's toolchain as matching. Logs: " +
                string.Join(" | ", logs.Select(entry => $"[{entry.Type}] {entry.Message}")));

        var shown = client.ShowMessages(mark);
        if (shown.Count != 0)
            throw new InvalidDataException(
                "A same-generation toolchain must not raise window/showMessage, but got: " +
                string.Join(" | ", shown.Select(entry => entry.Message)));
    }

    /// <summary>
    /// A project whose $(NccLayoutDir) holds Nemerle bits of a different assembly version must
    /// raise a user-facing window/showMessage naming both generations.
    ///
    /// Forcing the condition needs a Nemerle.dll whose version differs from the server's, and
    /// the port only ever produces one generation at a time. So the fixture points
    /// $(NccLayoutDir) at a directory holding some OTHER managed assembly renamed to
    /// Nemerle.dll: the check reads the assembly version off that file, which is exactly the
    /// identity a real mixed install would disagree on. This works because the project-info
    /// query runs -target:ResolveReferences, which never invokes ncc - so the bogus layout is
    /// never asked to compile anything.
    /// </summary>
    private static async Task ProvenanceMismatchAsync(LspTestClient client)
    {
        var dir = CreateTempDirectory("provenance-mismatch");
        var fakeLayout = Path.Combine(dir, "fake-ncc");
        Directory.CreateDirectory(fakeLayout);

        // Any managed assembly with a version other than the port's 1.2.0.x will do; the test
        // asserts on the versions it actually reads rather than hard-coding them.
        var donor = typeof(System.Text.Json.JsonDocument).Assembly.Location;
        var fakeNemerle = Path.Combine(fakeLayout, "Nemerle.dll");
        File.Copy(donor, fakeNemerle, overwrite: true);
        await File.WriteAllTextAsync(
            Path.Combine(fakeLayout, "ncc-info.json"),
            """{"commit":"0000000000000000000000000000000000000000","describe":"0000000","configuration":"Release"}""");

        var fakeVersion = System.Reflection.AssemblyName.GetAssemblyName(fakeNemerle).Version!.ToString();
        // Derive from _serverDll rather than assuming a path: this suite also runs against the
        // server extracted from the VSIX (test-bundled-server.ps1), where it lives elsewhere.
        var serverVersion = System.Reflection.AssemblyName
            .GetAssemblyName(Path.Combine(Path.GetDirectoryName(_serverDll)!, "Nemerle.dll")).Version!.ToString();
        if (fakeVersion == serverVersion)
            throw new InvalidDataException("Fixture is not a mismatch: the donor assembly happens to share the server's version.");

        var coreTargets = Path.Combine(_repoRoot, "dotnet-port", "msbuild", "Nemerle.Core.targets");
        var projectPath = Path.Combine(dir, "Temp.nproj");
        await File.WriteAllTextAsync(Path.Combine(dir, "probe.n"), "module Probe { Value : int = 1; }");
        // NccLayoutDir set in the project body wins over the targets' Condition-guarded default.
        await File.WriteAllTextAsync(
            projectPath,
            TempProjectXml(coreTargets, ["probe.n"])
                .Replace(
                    "<OutputType>Library</OutputType>",
                    $"<OutputType>Library</OutputType>{Environment.NewLine}    <NccLayoutDir>{fakeLayout}{Path.DirectorySeparatorChar}</NccLayoutDir>",
                    StringComparison.Ordinal));
        RunDotnet($"restore \"{projectPath}\"", dir);

        var mark = client.Mark();
        await LoadProjectAsync(client, projectPath);

        var shown = client.ShowMessages(mark);
        var warning = shown.FirstOrDefault(entry => entry.Message.Contains("version mismatch", StringComparison.Ordinal));
        if (warning.Message is null)
            throw new InvalidDataException(
                "Expected a window/showMessage warning about the toolchain mismatch, got: " +
                string.Join(" | ", shown.Select(entry => $"[{entry.Type}] {entry.Message}")));
        if (warning.Type != 2)
            throw new InvalidDataException($"Expected MessageType.Warning (2) for the mismatch, got {warning.Type}.");
        if (!warning.Message.Contains(fakeVersion, StringComparison.Ordinal) ||
            !warning.Message.Contains(serverVersion, StringComparison.Ordinal))
            throw new InvalidDataException(
                $"The warning must name both generations ({fakeVersion} and {serverVersion}), got: {warning.Message}");
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

    private static Task DidChangeRangedAsync(
        LspTestClient client, string uri, int version,
        int startLine, int startCharacter, int endLine, int endCharacter, string newText) =>
        client.NotifyAsync("textDocument/didChange", new
        {
            textDocument = new { uri, version },
            contentChanges = new[]
            {
                new
                {
                    range = new
                    {
                        start = new { line = startLine, character = startCharacter },
                        end = new { line = endLine, character = endCharacter },
                    },
                    text = newText,
                },
            },
        });

    /// <summary>
    /// The UTF-16 string index of a 0-based (line, character) position, counting
    /// CRLF/CR/LF as single line breaks (matching the server's buffer model).
    /// </summary>
    private static int LineCharToIndex(string text, int line, int character)
    {
        var offset = 0;
        var currentLine = 0;
        while (currentLine < line && offset < text.Length)
        {
            var c = text[offset];
            if (c == '\r')
            {
                offset++;
                if (offset < text.Length && text[offset] == '\n')
                    offset++;
                currentLine++;
            }
            else if (c == '\n')
            {
                offset++;
                currentLine++;
            }
            else
            {
                offset++;
            }
        }

        return offset + character;
    }

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

    /// <summary>
    /// A case-insensitive file-path key for de-duplicating/comparing sets of
    /// locations across two different URI strings that may name the same
    /// file with different drive-letter casing (Windows paths are case-
    /// insensitive; <see cref="AreEquivalentDocumentUris"/> already compares
    /// this way for single-URI checks).
    /// </summary>
    private static string NormalizedLocalPath(string uri) => new Uri(uri).LocalPath.ToLowerInvariant();

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
                     // Builds SyntaxMacros.dll too: the engine can only turn `twice`
                     // into a keyword if the macro assembly exists on disk.
                     Sample("SyntaxMacro", "SyntaxDemo", "SyntaxDemo.nproj"),
                 })
        {
            Console.WriteLine($"    building fixture {Path.GetFileName(project)}");
            RunDotnet($"build \"{project}\" -c Debug --nologo -v:q", _repoRoot);
        }
    }

    private static void EnsureWpN2FixturesBuilt()
    {
        foreach (var project in new[]
                 {
                     Sample("Sokoban", "Sokoban", "Sokoban.nproj"),
                     Sample("CompTimeSolver", "Success", "Success.nproj"),
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
