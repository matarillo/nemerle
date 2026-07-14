using Nemerle.ProjectInfo;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        try
        {
            await ParserTests();
            EngineInputTests();
            WarningCodeTests();
            HoverMarkupTests();
            CompletionMappingTests();
            GotoMappingTests();
            IncrementalSyncTests();
            PathNormalizerTests();
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

    private static void EngineInputTests()
    {
        var fixture = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "Fixtures", "msbuild-mixed.json"));
        var key = ProjectQueryKey.Create("dotnet", @"C:\repo\App\App.nproj");
        var snapshot = MsBuildJsonParser.Parse(key, fixture);
        var inputs = EngineWorkspaceInputs.FromSnapshot(snapshot);

        Equal(snapshot.ProjectPath, inputs.ProjectPath, "engine input project path");
        True(inputs.SourceFiles.SequenceEqual(snapshot.SourceFiles), "engine input sources");
        True(inputs.AssemblyReferences.SequenceEqual(snapshot.AssemblyReferences), "engine input assembly references");
        True(inputs.MacroReferences.SequenceEqual(snapshot.MacroReferences), "engine input macro references");
        True(inputs.MacroReferences.All(path => !inputs.AssemblyReferences.Contains(path)),
            "macro-only references never join the assembly reference list");
        // WP-M1 build parity: the engine now applies the full DefineConstants
        // set (MSBuild DefineConstants property unioned with any -define inside
        // NemerleAdditionalOptions), because Nemerle.Core.targets passes the same
        // set to ncc as "-define:".  There is no longer a gap warning.
        True(inputs.Defines.SequenceEqual(snapshot.DefineConstants), "engine defines are the full DefineConstants set");
        True(inputs.Defines.Contains("NET") && inputs.Defines.Contains("TRACE"), "MSBuild DefineConstants property is applied");
        True(inputs.Defines.Contains("FEATURE") && inputs.Defines.Contains("SECOND"), "NemerleAdditionalOptions -define values remain applied");
        Equal(false, inputs.CheckIntegerOverflow, "checked option propagated");
        True(inputs.IndentationSyntax, "indentation option propagated");
        True(inputs.Warnings.SequenceEqual(snapshot.Warnings), "no DefineConstants gap warning is synthesized anymore");
        True(!inputs.Warnings.Any(warning => warning.Contains("DefineConstants", StringComparison.Ordinal)),
            "the DefineConstants gap warning is gone");
    }

    private static void WarningCodeTests()
    {
        var (code, message) = NemerleWarningCode.Extract("N10003: `Broken.helper' is not externally visible and has never been referenced");
        Equal("N10003", code, "coded warning prefix is extracted");
        Equal("`Broken.helper' is not externally visible and has never been referenced", message, "coded warning message is stripped of its prefix");

        var (noCode, unchanged) = NemerleWarningCode.Extract("this match clause is unused");
        True(noCode is null, "uncoded warning has no extracted code");
        Equal("this match clause is unused", unchanged, "uncoded warning message is unchanged");

        var (multiCode, multiMessage) = NemerleWarningCode.Extract("N10001: line one\nline two");
        Equal("N10001", multiCode, "multi-line coded warning still yields the code");
        Equal("line one\nline two", multiMessage, "multi-line coded warning keeps its full body");

        var (nullCode, nullMessage) = NemerleWarningCode.Extract(null);
        True(nullCode is null && nullMessage.Length == 0, "null message is handled");
    }

    private static void HoverMarkupTests()
    {
        // <lb/> becomes a newline; the spelling variant is tolerated.
        Equal("a\nb", HoverMarkup.ToPlainText("a<lb/>b"), "<lb/> becomes a newline");
        Equal("a\nb", HoverMarkup.ToPlainText("a<lb />b"), "<lb /> spelling variant becomes a newline");

        // Decorative tags are removed, their visible content kept.
        Equal("public Run() : void",
            HoverMarkup.ToPlainText("<keyword>public</keyword> Run() : void"),
            "decorative keyword tags are stripped");
        Equal("Instance property: T.Name : int",
            HoverMarkup.ToPlainText("Instance property: T.<b>Name</b> : int"),
            "bold tags are stripped");

        // <hint value='...'>text</hint> keeps only the visible text, never the attribute.
        var hint = HoverMarkup.ToPlainText("<hint value='name'>the parameter</hint>");
        Equal("the parameter", hint, "hint tag keeps inner text and drops the value attribute");

        // HtmlMangling is reversed; &amp; is decoded last so &amp;lt; round-trips to &lt;.
        Equal("List<int> & Map<K,V>",
            HoverMarkup.ToPlainText("List&lt;int&gt; &amp; Map&lt;K,V&gt;"),
            "escaped angle brackets and ampersand are restored");
        Equal("&lt;", HoverMarkup.ToPlainText("&amp;lt;"), "&amp;lt; round-trips to &lt;");

        // No raw pseudo-markup tag ever survives (params/pname/ptype clusters too).
        var messy = "<params><pname>x</pname> <ptype>: int</ptype></params><lb/><code><pre>y</pre></code>";
        var plain = HoverMarkup.ToPlainText(messy);
        True(!plain.Contains('<') && !plain.Contains('>'), "no raw pseudo-markup tag survives");
        True(plain.Contains("x") && plain.Contains(": int") && plain.Contains("y"), "visible content is preserved");

        Equal(string.Empty, HoverMarkup.ToPlainText(null), "null markup yields empty text");
        Equal(string.Empty, HoverMarkup.ToPlainText(""), "empty markup yields empty text");
        Equal(string.Empty, HoverMarkup.ToMarkdown("   "), "whitespace-only markup yields empty markdown");

        // Markdown fences the whole hint so metacharacters render literally.
        var md = HoverMarkup.ToMarkdown("<keyword>public</keyword> M(a : int) : void");
        Equal("```nemerle\npublic M(a : int) : void\n```", md, "markdown fences the hint as a Nemerle code block");
        True(md.StartsWith("```nemerle\n", StringComparison.Ordinal) && md.EndsWith("\n```", StringComparison.Ordinal),
            "markdown result is a fenced code block");

        // Metacharacters and residual identifiers are never interpreted as markdown.
        var meta = HoverMarkup.ToMarkdown("value *n* _k_ [x](y) : int");
        True(meta.Contains("*n*") && meta.Contains("[x](y)"), "markdown metacharacters are kept literal inside the fence");

        // A hint containing a backtick run gets a longer fence so it cannot close early.
        var withTicks = HoverMarkup.ToMarkdown("a ``` b");
        True(withTicks.StartsWith("````nemerle\n", StringComparison.Ordinal) && withTicks.EndsWith("\n````", StringComparison.Ordinal),
            "fence grows past an embedded backtick run");
    }

    private static void CompletionMappingTests()
    {
        // Glyph integers mirror Nemerle.Completion2.GlyphType (members spaced by
        // 6, plus 205/206 for the snippet/keyword glyphs).
        Equal(NemerleCompletionKind.Class, CompletionMapping.GlyphToKind(0), "class glyph");
        Equal(NemerleCompletionKind.Constant, CompletionMapping.GlyphToKind(6), "const glyph");
        Equal(NemerleCompletionKind.Class, CompletionMapping.GlyphToKind(12), "delegate glyph falls back to class");
        Equal(NemerleCompletionKind.Enum, CompletionMapping.GlyphToKind(18), "enum glyph");
        Equal(NemerleCompletionKind.EnumMember, CompletionMapping.GlyphToKind(24), "enum value glyph");
        Equal(NemerleCompletionKind.Event, CompletionMapping.GlyphToKind(30), "event glyph");
        Equal(NemerleCompletionKind.Field, CompletionMapping.GlyphToKind(42), "field glyph");
        Equal(NemerleCompletionKind.Interface, CompletionMapping.GlyphToKind(48), "interface glyph");
        Equal(NemerleCompletionKind.Variable, CompletionMapping.GlyphToKind(54), "block glyph maps to variable");
        Equal(NemerleCompletionKind.Class, CompletionMapping.GlyphToKind(60), "variant glyph maps to class");
        Equal(NemerleCompletionKind.EnumMember, CompletionMapping.GlyphToKind(66), "variant option glyph maps to enum member");
        Equal(NemerleCompletionKind.Method, CompletionMapping.GlyphToKind(72), "method glyph");
        Equal(NemerleCompletionKind.Function, CompletionMapping.GlyphToKind(78), "function glyph");
        Equal(NemerleCompletionKind.Module, CompletionMapping.GlyphToKind(90), "namespace/operator glyph maps to module");
        Equal(NemerleCompletionKind.Property, CompletionMapping.GlyphToKind(102), "property glyph");
        Equal(NemerleCompletionKind.Struct, CompletionMapping.GlyphToKind(108), "struct glyph");
        Equal(NemerleCompletionKind.Function, CompletionMapping.GlyphToKind(120), "macro glyph maps to function");
        Equal(NemerleCompletionKind.Variable, CompletionMapping.GlyphToKind(138), "local glyph maps to variable");
        Equal(NemerleCompletionKind.Keyword, CompletionMapping.GlyphToKind(205), "snippet glyph maps to keyword");
        Equal(NemerleCompletionKind.Keyword, CompletionMapping.GlyphToKind(206), "keyword glyph");
        Equal(NemerleCompletionKind.Text, CompletionMapping.GlyphToKind(999), "unknown glyph falls back to text");
        Equal(NemerleCompletionKind.Text, CompletionMapping.GlyphToKind(-1), "negative glyph falls back to text");

        // The completion detail/documentation carries the same VS2010 pseudo-markup
        // as hover (LocalValue.MakeHint emits <lb/>, macro hints emit <keyword>),
        // so it is stripped by the same pure function (WP-M3 acceptance 5).
        Equal("(local) value : int\ndefined in M",
            HoverMarkup.ToPlainText("(local) value : int<lb/>defined in M"),
            "completion documentation reuses the hover markup stripper");
    }

    private static void GotoMappingTests()
    {
        // A cross-platform source path so the URI/range assertions do not depend
        // on the OS-specific drive-letter handling (checked separately below).
        var file = OperatingSystem.IsWindows() ? @"C:\repo\App\Program.n" : "/repo/App/Program.n";

        // An in-workspace source target (FileIndex > 0) converts from 1-based
        // engine coordinates to a 0-based UTF-16 LSP range.
        var def = new NemerleGotoTarget(file, FileIndex: 3, Line: 5, Column: 7, EndLine: 5, EndColumn: 12, IsDefinition: true);
        var one = GotoMapping.ToLocations([def], includeDeclaration: true);
        Equal(1, one.Count, "an in-workspace source target yields one location");
        Equal(4, one[0].StartLine, "line is converted to 0-based");
        Equal(6, one[0].StartCharacter, "column is converted to 0-based");
        Equal(4, one[0].EndLine, "end line is converted to 0-based");
        Equal(11, one[0].EndCharacter, "end column is converted to 0-based");
        Equal(GotoMapping.ToUri(file), one[0].Uri, "location URI matches the shared path->URI helper");

        // A metadata / external member (FileIndex 0, no source path) is dropped so
        // definition on a BCL/NuGet symbol yields an empty result (acceptance 4).
        var external = new NemerleGotoTarget(null, FileIndex: 0, Line: 0, Column: 0, EndLine: 0, EndColumn: 0, IsDefinition: true);
        Equal(0, GotoMapping.ToLocations([external], includeDeclaration: true).Count,
            "a metadata/external target is not navigable");
        // A target with a FileIndex but no end position is not navigable either.
        var noRange = new NemerleGotoTarget(file, FileIndex: 3, Line: 5, Column: 7, EndLine: 0, EndColumn: 0, IsDefinition: false);
        Equal(0, GotoMapping.ToLocations([noRange], includeDeclaration: true).Count,
            "a target without an end position is not navigable");

        // includeDeclaration drops/keeps the declaration entries (references).
        var usage = new NemerleGotoTarget(file, FileIndex: 3, Line: 9, Column: 3, EndLine: 9, EndColumn: 8, IsDefinition: false);
        var withDecl = GotoMapping.ToLocations([def, usage], includeDeclaration: true);
        Equal(2, withDecl.Count, "includeDeclaration=true keeps the declaration alongside the usage");
        var withoutDecl = GotoMapping.ToLocations([def, usage], includeDeclaration: false);
        Equal(1, withoutDecl.Count, "includeDeclaration=false drops the declaration entry");
        Equal(8, withoutDecl[0].StartLine, "the surviving reference is the usage");

        // Exact duplicates (e.g. partial types reporting the same location) collapse.
        Equal(1, GotoMapping.ToLocations([def, def], includeDeclaration: true).Count, "exact duplicate locations collapse");

        // Windows: the URI carries an upper-cased drive letter and file scheme.
        if (OperatingSystem.IsWindows())
        {
            var uri = GotoMapping.ToUri(@"c:\repo\App\Program.n");
            True(uri.StartsWith("file:///C:/", StringComparison.Ordinal), $"URI has an upper-cased drive letter and file scheme: {uri}");
            True(uri.EndsWith("/Program.n", StringComparison.Ordinal), $"URI ends with the source file: {uri}");
        }
    }

    private static void IncrementalSyncTests()
    {
        // --- ApplyChange: the server's buffer must track the client's document ---

        // Single-line insertion.
        Equal("abcXYdef",
            IncrementalSync.ApplyChange("abcdef", NemerleContentChange.Ranged(0, 3, 0, 3, "XY")),
            "single-line insertion splices at the UTF-16 offset");

        // Deletion (empty replacement).
        Equal("aef",
            IncrementalSync.ApplyChange("abcdef", NemerleContentChange.Ranged(0, 1, 0, 4, "")),
            "deletion removes the [start, end) span");

        // Replacement.
        Equal("aZef",
            IncrementalSync.ApplyChange("abcdef", NemerleContentChange.Ranged(0, 1, 0, 4, "Z")),
            "replacement swaps the span for the new text");

        // Multi-line insertion inside a LF document.
        Equal("abc\nd1\n2ef",
            IncrementalSync.ApplyChange("abc\ndef", NemerleContentChange.Ranged(1, 1, 1, 1, "1\n2")),
            "multi-line insertion on line 1 (LF)");

        // CRLF: an insertion on line 1 keeps the CRLF intact.
        Equal("ab\r\ncXd",
            IncrementalSync.ApplyChange("ab\r\ncd", NemerleContentChange.Ranged(1, 1, 1, 1, "X")),
            "CRLF newline is preserved by an insertion after it");

        // CRLF: a range that spans the newline removes the whole "\r\n".
        Equal("abcd",
            IncrementalSync.ApplyChange("ab\r\ncd", NemerleContentChange.Ranged(0, 2, 1, 0, "")),
            "a range across a CRLF removes both code units");

        // Non-BMP: the emoji is two UTF-16 units; an insertion after it lands correctly.
        Equal("a😀Zb",
            IncrementalSync.ApplyChange("a😀b", NemerleContentChange.Ranged(0, 3, 0, 3, "Z")),
            "insertion after a surrogate pair uses UTF-16 offsets");

        // Multi-line range replacement.
        Equal("aXhi",
            IncrementalSync.ApplyChange("abc\ndef\nghi", NemerleContentChange.Ranged(0, 1, 2, 1, "X")),
            "a range spanning multiple lines is replaced");

        // A whole-document change returns its text verbatim.
        Equal("brand new",
            IncrementalSync.ApplyChange("anything", NemerleContentChange.FullReplace("brand new")),
            "a full replacement ignores the previous text");

        // --- ComputeRelocation: 0-based UTF-16 -> engine 1-based Begin/Old/New ---

        // Insertion: Old == Begin (nothing removed), New advances by the inserted length.
        var insert = IncrementalSync.ComputeRelocation(NemerleContentChange.Ranged(0, 3, 0, 3, "XY"));
        Equal(new NemerleRelocation(1, 4, 1, 4, 1, 6), insert, "insertion relocation (Begin==Old, New advances)");

        // Deletion: New == Begin (nothing inserted), Old is the removed span's end.
        var delete = IncrementalSync.ComputeRelocation(NemerleContentChange.Ranged(0, 1, 0, 4, ""));
        Equal(new NemerleRelocation(1, 2, 1, 5, 1, 2), delete, "deletion relocation (Begin==New, Old is old end)");

        // Replacement: Begin != Old and Begin != New (the engine's "isUpdate" case).
        var replace = IncrementalSync.ComputeRelocation(NemerleContentChange.Ranged(0, 1, 0, 4, "Z"));
        Equal(new NemerleRelocation(1, 2, 1, 5, 1, 3), replace, "replacement relocation is an update on both ends");

        // Multi-line insertion: New moves to a later line, its character is the last line's length.
        var multiLine = IncrementalSync.ComputeRelocation(NemerleContentChange.Ranged(1, 1, 1, 1, "1\n2"));
        Equal(new NemerleRelocation(2, 2, 2, 2, 3, 2), multiLine, "multi-line insertion New end is on a later line");

        // CRLF inside the inserted text counts as one line break.
        var crlfInsert = IncrementalSync.ComputeRelocation(NemerleContentChange.Ranged(2, 5, 2, 5, "a\r\nbc"));
        Equal(new NemerleRelocation(3, 6, 3, 6, 4, 3), crlfInsert, "CRLF in inserted text is a single line break");

        // Non-BMP inserted text: the surrogate pair counts as two UTF-16 units.
        var emojiInsert = IncrementalSync.ComputeRelocation(NemerleContentChange.Ranged(0, 1, 0, 1, "😀"));
        Equal(new NemerleRelocation(1, 2, 1, 2, 1, 4), emojiInsert, "inserted surrogate pair advances New by two units");

        // A whole-document change has no relocation.
        var threw = false;
        try { IncrementalSync.ComputeRelocation(NemerleContentChange.FullReplace("x")); }
        catch (ArgumentException) { threw = true; }
        True(threw, "ComputeRelocation rejects a whole-document change");
    }

    private static void PathNormalizerTests()
    {
        if (!OperatingSystem.IsWindows())
            return;

        Equal(@"C:\repo\App\Program.n", ProjectPathNormalizer.NormalizeFile(@"c:\repo\App\Program.n"),
            "drive letter is normalized to upper case");
        Equal(@"C:\repo\App\Program.n", ProjectPathNormalizer.NormalizeFile(@"C:\repo\Lib\..\App\.\Program.n"),
            "relative segments are resolved");
        var deduped = ProjectPathNormalizer.NormalizeDistinct([@"C:\repo\a.n", @"c:\REPO\A.N"]);
        Equal(1, deduped.Count, "case-differing duplicates collapse to one path");
        True(ProjectPathNormalizer.Comparer.Equals(@"C:\repo\a.n", @"c:\Repo\A.N"), "path comparer is case-insensitive on Windows");
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
