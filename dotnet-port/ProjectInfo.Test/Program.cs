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
            SemanticTokenMappingTests();
            SignatureHelpMappingTests();
            GotoMappingTests();
            RenameMappingTests();
            CodeActionMappingTests();
            IncrementalSyncTests();
            PathNormalizerTests();
            ToolchainProvenanceTests();
            await ErrorTests();
            await ProviderTests();
            if (args.Contains("--integration", StringComparer.Ordinal))
            {
                await SampleIntegrationTests();
                await SdkPackageTests();
            }
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
        // The fixture lists one source twice, differing only in case (Program.n / program.n on
        // C:\ / c:\). Whether that is one file or two is a filesystem property, and ProjectPath-
        // Normalizer mirrors it: case-insensitive on Windows (deduped to 1), case-sensitive on
        // Linux/macOS (kept as 2). This is the same platform split PathNormalizerTests documents.
        Equal(OperatingSystem.IsWindows() ? 1 : 2, snapshot.SourceFiles.Count, "case-folded duplicate source normalization matches the platform filesystem");
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

        // Self-closing <hint value='...' /> (SubHintForType's bare type simple name,
        // e.g. "<hint value='SMap' key='42' />") expands to the value text instead of
        // being deleted like a decorative tag (WP-N2 N2.1, R-H / E7-D: this was the
        // hint markup information loss that turned "args : array[string]" into
        // "args : []").
        Equal("args : array[string]",
            HoverMarkup.ToPlainText("args : <hint value='array' key='1' />[<hint value='string' key='2' />]"),
            "self-closing hint value expands to the visible type name (single-quoted, value before key)");
        Equal("SMap",
            HoverMarkup.ToPlainText("<hint value=\"SMap\" key=\"7\" />"),
            "self-closing hint value expands with double-quoted attributes");
        Equal("SMap",
            HoverMarkup.ToPlainText("<hint key='7' value='SMap' />"),
            "self-closing hint value expands regardless of attribute order (key before value)");
        Equal("SMap",
            HoverMarkup.ToPlainText("<hint   value = 'SMap'   key = '7'   />"),
            "self-closing hint value expands across arbitrary whitespace");
        Equal("SMap",
            HoverMarkup.ToPlainText("<hint value='SMap'/>"),
            "self-closing hint value expands with no space before the closing slash");

        // Entities inside the value survive the expansion step untouched and are
        // decoded once, by the existing entity-decode step that already ran after
        // tag stripping (not double-decoded here).
        Equal("List<int>",
            HoverMarkup.ToPlainText("<hint value='List&lt;int&gt;' key='9' />"),
            "escaped entities inside a self-closing hint value are decoded exactly once");

        // Malformed / valueless self-closing hint tags fall back to today's
        // behavior: the tag is simply removed, same as any other decorative tag,
        // so raw pseudo-markup never leaks to the client.
        Equal("x", HoverMarkup.ToPlainText("<hint key='1' />x"),
            "a self-closing hint tag with no value attribute falls back to tag removal");
        var malformed = HoverMarkup.ToPlainText("<hint value />x");
        True(!malformed.Contains('<') && !malformed.Contains('>') && malformed.Contains('x'),
            "a self-closing hint tag with a bare 'value' (no '=') falls back to tag removal, no markup leaks");

        // Paired <hint>...</hint> tags (no self-closing "/>") keep behaving exactly
        // as before: the tags are removed and the inner content is kept.
        Equal("the parameter",
            HoverMarkup.ToPlainText("<hint value='name' key='3'>the parameter</hint>"),
            "paired hint tags with a value attribute still keep only their inner text");

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

        // ToMarkdown reuses ToPlainText, so the fence wraps the expanded hint
        // value too: the markdown path gets the same R-H / E7-D fix for free.
        Equal("```nemerle\nSMap\n```",
            HoverMarkup.ToMarkdown("<hint value='SMap' key='42' />"),
            "markdown fences the expanded self-closing hint value");

        // Metacharacters and residual identifiers are never interpreted as markdown.
        var meta = HoverMarkup.ToMarkdown("value *n* _k_ [x](y) : int");
        True(meta.Contains("*n*") && meta.Contains("[x](y)"), "markdown metacharacters are kept literal inside the fence");

        // A hint containing a backtick run gets a longer fence so it cannot close early.
        var withTicks = HoverMarkup.ToMarkdown("a ``` b");
        True(withTicks.StartsWith("````nemerle\n", StringComparison.Ordinal) && withTicks.EndsWith("\n````", StringComparison.Ordinal),
            "fence grows past an embedded backtick run");

        // Declaration hovers end with a "declared at" paragraph
        // ("\n\nfile:line:col:endLine:endCol:") written for the VS tooltip; the
        // LSP handler strips it (the editor answers "where" via go-to-definition).
        Equal("public field: elem : NSokoban.SMap;",
            HoverMarkup.StripDeclarationLocationTail(
                "public field: elem : NSokoban.SMap;\n\n/home/user/src/splayheap.n:7:32:7:43:"),
            "a trailing unix-path location paragraph is stripped");
        Equal("field: x : int;",
            HoverMarkup.StripDeclarationLocationTail("field: x : int;\n\nC:\\src\\a b\\file.n:1:2:3:4:"),
            "a trailing windows-path location paragraph (drive colon, spaces) is stripped");
        Equal("no tail here",
            HoverMarkup.StripDeclarationLocationTail("no tail here"),
            "text without a location paragraph is unchanged");
        Equal("see foo.n:1:2:3:4: for details",
            HoverMarkup.StripDeclarationLocationTail("see foo.n:1:2:3:4: for details"),
            "a lookalike substring that is not a whole trailing paragraph is kept");
        Equal("only doc text\n\nnot a location",
            HoverMarkup.StripDeclarationLocationTail("only doc text\n\nnot a location"),
            "a final paragraph that is not file:line:col:endLine:endCol: is kept");
        Equal(string.Empty, HoverMarkup.StripDeclarationLocationTail(null),
            "null markup yields empty text when stripping the location tail");
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

    private static void SemanticTokenMappingTests()
    {
        // The legend is a wire contract shared with the VS Code manifest and the
        // raw LSP scenario: keyword/macro lead the types, and the two custom
        // modifiers keep their bit order.
        Equal("keyword", SemanticTokenMapping.TokenTypes[0], "legend type 0");
        Equal("macro", SemanticTokenMapping.TokenTypes[1], "legend type 1");
        Equal(15, SemanticTokenMapping.TokenTypes.Length, "legend type count");
        True(SemanticTokenMapping.TokenModifiers.SequenceEqual(["quotation", "escape"]), "legend modifiers");
        Equal<string?>(null, SemanticTokenMapping.TypeName(NemerleSemanticTokenType.None), "None has no legend name");
        Equal("event", SemanticTokenMapping.TypeName(NemerleSemanticTokenType.Event), "last legend name");
        True(
            SemanticTokenMapping.ModifierNames(
                    NemerleSemanticTokenModifier.Escape | NemerleSemanticTokenModifier.Quotation)
                .SequenceEqual(["quotation", "escape"]),
            "modifier names come back in legend order");
        Equal(0, SemanticTokenMapping.ModifierNames(NemerleSemanticTokenModifier.None).Length, "no modifiers");

        // The feature's reason to exist: the same Keyword color classifies as
        // `macro` when the caller determined the word only became a keyword
        // because a syntax macro added it to the file's environment.
        Equal(
            (NemerleSemanticTokenType.Keyword, NemerleSemanticTokenModifier.None),
            SemanticTokenMapping.Classify(NemerleScanTokenColor.Keyword, macroKeyword: false),
            "a core keyword");
        Equal(
            (NemerleSemanticTokenType.Macro, NemerleSemanticTokenModifier.None),
            SemanticTokenMapping.Classify(NemerleScanTokenColor.Keyword, macroKeyword: true),
            "a macro-introduced keyword");
        Equal(
            (NemerleSemanticTokenType.Macro, NemerleSemanticTokenModifier.Quotation),
            SemanticTokenMapping.Classify(NemerleScanTokenColor.QuotationKeyword, macroKeyword: true),
            "a macro-introduced keyword inside a quotation");
        // The preprocessor is not part of the extensible syntax, so macroKeyword
        // must not leak into it.
        Equal(
            (NemerleSemanticTokenType.Keyword, NemerleSemanticTokenModifier.None),
            SemanticTokenMapping.Classify(NemerleScanTokenColor.Preprocessor, macroKeyword: true),
            "a preprocessor directive is never a macro");

        // String families: verbatim/recursive collapse onto `string`, and the *Ex
        // variants (escape sequences and $-splices) add the escape modifier.
        foreach (var plain in new[]
                 {
                     NemerleScanTokenColor.String, NemerleScanTokenColor.VerbatimString,
                     NemerleScanTokenColor.RecursiveString,
                 })
            Equal(
                (NemerleSemanticTokenType.String, NemerleSemanticTokenModifier.None),
                SemanticTokenMapping.Classify(plain, macroKeyword: false),
                $"{plain} is a plain string");
        foreach (var ex in new[]
                 {
                     NemerleScanTokenColor.StringEx, NemerleScanTokenColor.VerbatimStringEx,
                     NemerleScanTokenColor.RecursiveStringEx,
                 })
            Equal(
                (NemerleSemanticTokenType.String, NemerleSemanticTokenModifier.Escape),
                SemanticTokenMapping.Classify(ex, macroKeyword: false),
                $"{ex} is an escape run");
        Equal(
            (NemerleSemanticTokenType.String,
                NemerleSemanticTokenModifier.Quotation | NemerleSemanticTokenModifier.Escape),
            SemanticTokenMapping.Classify(NemerleScanTokenColor.QuotationVerbatimStringEx, macroKeyword: false),
            "an escape run inside a quotation carries both modifiers");

        // Type colors keep the engine's distinctions where LSP has a type for them.
        Equal(
            (NemerleSemanticTokenType.Class, NemerleSemanticTokenModifier.None),
            SemanticTokenMapping.Classify(NemerleScanTokenColor.UserType, macroKeyword: false), "a user type");
        Equal(
            (NemerleSemanticTokenType.Interface, NemerleSemanticTokenModifier.None),
            SemanticTokenMapping.Classify(NemerleScanTokenColor.UserTypeInterface, macroKeyword: false), "an interface");
        Equal(
            (NemerleSemanticTokenType.Enum, NemerleSemanticTokenModifier.None),
            SemanticTokenMapping.Classify(NemerleScanTokenColor.UserTypeEnum, macroKeyword: false), "an enum");
        Equal(
            (NemerleSemanticTokenType.Struct, NemerleSemanticTokenModifier.None),
            SemanticTokenMapping.Classify(NemerleScanTokenColor.UserTypeValueType, macroKeyword: false), "a value type");
        Equal(
            (NemerleSemanticTokenType.Type, NemerleSemanticTokenModifier.None),
            SemanticTokenMapping.Classify(NemerleScanTokenColor.UserTypeDelegate, macroKeyword: false),
            "a delegate falls back to the generic type");
        Equal(
            (NemerleSemanticTokenType.Class, NemerleSemanticTokenModifier.Quotation),
            SemanticTokenMapping.Classify(NemerleScanTokenColor.QuotationUserType, macroKeyword: false),
            "a user type inside a quotation");

        // Members: LSP has no "field", so it shares `property`.
        Equal(
            (NemerleSemanticTokenType.Property, NemerleSemanticTokenModifier.None),
            SemanticTokenMapping.Classify(NemerleScanTokenColor.Field, macroKeyword: false), "a field");
        Equal(
            (NemerleSemanticTokenType.Method, NemerleSemanticTokenModifier.None),
            SemanticTokenMapping.Classify(NemerleScanTokenColor.Method, macroKeyword: false), "a method");

        // Special comments deliberately fold into plain comments; whitespace, plain
        // text and the hover-highlight overlays emit no token at all so the
        // client's grammar keeps coloring those spans.
        Equal(
            (NemerleSemanticTokenType.Comment, NemerleSemanticTokenModifier.None),
            SemanticTokenMapping.Classify(NemerleScanTokenColor.CommentTODO, macroKeyword: false),
            "a TODO comment is still a comment");
        Equal(
            (NemerleSemanticTokenType.Comment, NemerleSemanticTokenModifier.Quotation),
            SemanticTokenMapping.Classify(NemerleScanTokenColor.QuotationCommentHACK, macroKeyword: false),
            "a HACK comment inside a quotation");
        foreach (var none in new[]
                 {
                     NemerleScanTokenColor.Text, NemerleScanTokenColor.QuotationText,
                     NemerleScanTokenColor.HighlightOne, NemerleScanTokenColor.HighlightTwo,
                 })
            Equal(
                (NemerleSemanticTokenType.None, NemerleSemanticTokenModifier.None),
                SemanticTokenMapping.Classify(none, macroKeyword: false),
                $"{none} emits no token");
        Equal(
            (NemerleSemanticTokenType.None, NemerleSemanticTokenModifier.None),
            SemanticTokenMapping.Classify((NemerleScanTokenColor)9999, macroKeyword: false),
            "an unmirrored engine color emits no token");
    }

    private static void SignatureHelpMappingTests()
    {
        // Nothing to show is null, not an empty container: an empty
        // SignatureHelp leaves an empty popup open in VS Code.
        True(SignatureHelpMapping.ToSignatureHelp(null) is null, "no tip maps to null");
        True(SignatureHelpMapping.ToSignatureHelp(new NemerleMethodTip([], 0, 0)) is null,
            "a tip with no overloads maps to null");

        // The label is composed here, in Nemerle notation, from the pieces the
        // engine exposes (name, per-parameter "name : type", return type).
        var twoParameters = new NemerleTipSignature(
            "Combine", "int", null,
            [
                new NemerleTipParameter("first", "first : int", null),
                new NemerleTipParameter("second", "second : int", "the addend"),
            ]);
        var help = SignatureHelpMapping.ToSignatureHelp(new NemerleMethodTip([twoParameters], 0, 1));
        True(help is not null, "a tip with one overload maps to signature help");
        var signature = help!.Signatures[0];
        Equal("Combine(first : int, second : int) : int", signature.Label, "composed signature label");

        // The parameter ranges are half-open [start, end) offsets that address
        // exactly their own text - the property substring matching cannot give
        // when two parameters share a type, which is the common case.
        Equal(2, signature.Parameters.Count, "parameter count");
        Equal("first : int", signature.Label[signature.Parameters[0].Start..signature.Parameters[0].End],
            "first parameter range addresses its own text");
        Equal("second : int", signature.Label[signature.Parameters[1].Start..signature.Parameters[1].End],
            "second parameter range addresses its own text");
        True(signature.Parameters[0].End < signature.Parameters[1].Start, "parameter ranges do not overlap");
        Equal("the addend", signature.Parameters[1].Documentation, "parameter documentation");
        Equal<string?>(null, signature.Parameters[0].Documentation, "an undocumented parameter has no documentation");
        Equal(1, help.ActiveParameter, "activeParameter comes from ParameterIndex");

        // A member with no parameters still renders (the popup then shows only
        // the return type), and a member whose return type the engine could not
        // name drops the " : " tail instead of printing an empty type.
        Equal(
            "Nothing() : void",
            SignatureHelpMapping.ToSignatureHelp(
                new NemerleMethodTip([new NemerleTipSignature("Nothing", "void", null, [])], 0, 0))!
                .Signatures[0].Label,
            "a zero-parameter signature");
        Equal(
            "Nothing()",
            SignatureHelpMapping.ToSignatureHelp(
                new NemerleMethodTip([new NemerleTipSignature("Nothing", "  ", null, [])], 0, 0))!
                .Signatures[0].Label,
            "an unnamed return type drops the trailing colon");

        // DefaultMethod is -1 when the engine could not resolve the call to one
        // overload (List.FindIndex missed); an out-of-range activeSignature is a
        // protocol error, so it falls back to the first (smallest-arity) one.
        var overloads = new NemerleMethodTip(
            [twoParameters, twoParameters with { ReturnType = "string" }], -1, 0);
        Equal(0, SignatureHelpMapping.ToSignatureHelp(overloads)!.ActiveSignature,
            "an unresolved DefaultMethod falls back to the first overload");
        Equal(1, SignatureHelpMapping.ToSignatureHelp(overloads with { DefaultSignature = 1 })!.ActiveSignature,
            "a resolved DefaultMethod is passed through");
        Equal(0, SignatureHelpMapping.ToSignatureHelp(overloads with { DefaultSignature = 7 })!.ActiveSignature,
            "an out-of-range DefaultMethod falls back to the first overload");

        // activeParameter is deliberately NOT clamped to the parameter count:
        // LSP renders an out-of-range index as "no parameter highlighted", which
        // is right for a call with more arguments than the overload takes.
        Equal(5, SignatureHelpMapping.ToSignatureHelp(overloads with { ParameterIndex = 5 })!.ActiveParameter,
            "an argument past the last parameter keeps its index");
        Equal(0, SignatureHelpMapping.ToSignatureHelp(overloads with { ParameterIndex = -3 })!.ActiveParameter,
            "a negative parameter index is corrected");

        // XmlDoc summaries arrive with the source file's line breaks and
        // indentation; a signature popup needs one line, and a multi-line label
        // would also make the parameter offsets address text the editor renders
        // differently.
        var documented = SignatureHelpMapping.ToSignatureHelp(new NemerleMethodTip(
            [
                new NemerleTipSignature(
                    " Serialize ", "string", "\n   Converts the value\n   to JSON.\n  ",
                    [new NemerleTipParameter("value", " value\n : object ", "   the\tvalue  ")]),
            ], 0, 0))!;
        Equal("Converts the value to JSON.", documented.Signatures[0].Documentation,
            "a multi-line XmlDoc summary is collapsed to one line");
        Equal("Serialize(value : object) : string", documented.Signatures[0].Label,
            "whitespace inside the engine's strings is collapsed too");
        Equal("the value", documented.Signatures[0].Parameters[0].Documentation,
            "parameter documentation is collapsed as well");
        Equal<string?>(null,
            SignatureHelpMapping.ToSignatureHelp(new NemerleMethodTip(
                [new NemerleTipSignature("X", "int", "   \n ", [])], 0, 0))!.Signatures[0].Documentation,
            "a whitespace-only summary is no documentation at all");

        // A parameter the engine could not render still gets a non-empty span,
        // so the offsets stay well-formed.
        var degenerate = SignatureHelpMapping.ToSignatureHelp(new NemerleMethodTip(
            [
                new NemerleTipSignature("F", "int", null,
                [
                    new NemerleTipParameter("only", "", null),
                    new NemerleTipParameter("", "", null),
                ]),
            ], 0, 0))!.Signatures[0];
        Equal("F(only, _) : int", degenerate.Label, "empty displays fall back to the name, then to a placeholder");
        Equal("only", degenerate.Label[degenerate.Parameters[0].Start..degenerate.Parameters[0].End],
            "the name fallback is still addressed by its range");
        Equal("_", degenerate.Label[degenerate.Parameters[1].Start..degenerate.Parameters[1].End],
            "the placeholder is still addressed by its range");
    }

    private static void GotoMappingTests()
    {
        // A cross-platform source path so the URI/range assertions do not depend
        // on the OS-specific drive-letter handling (checked separately below).
        var file = OperatingSystem.IsWindows() ? @"C:\repo\App\Program.n" : "/repo/App/Program.n";

        // An in-workspace source target (FileIndex > 0) converts from 1-based
        // engine coordinates to a 0-based UTF-16 LSP range.
        var def = new NemerleGotoTarget(file, FileIndex: 3, Line: 5, Column: 7, EndLine: 5, EndColumn: 12, NemerleUsageType.Definition);
        var one = GotoMapping.ToLocations([def], includeDeclaration: true);
        Equal(1, one.Count, "an in-workspace source target yields one location");
        Equal(4, one[0].StartLine, "line is converted to 0-based");
        Equal(6, one[0].StartCharacter, "column is converted to 0-based");
        Equal(4, one[0].EndLine, "end line is converted to 0-based");
        Equal(11, one[0].EndCharacter, "end column is converted to 0-based");
        Equal(GotoMapping.ToUri(file), one[0].Uri, "location URI matches the shared path->URI helper");

        // A metadata / external member (FileIndex 0, no source path) is dropped so
        // definition on a BCL/NuGet symbol yields an empty result (acceptance 4).
        var external = new NemerleGotoTarget(null, FileIndex: 0, Line: 0, Column: 0, EndLine: 0, EndColumn: 0, NemerleUsageType.Definition);
        Equal(0, GotoMapping.ToLocations([external], includeDeclaration: true).Count,
            "a metadata/external target is not navigable");
        // A target with a FileIndex but no end position is not navigable either.
        var noRange = new NemerleGotoTarget(file, FileIndex: 3, Line: 5, Column: 7, EndLine: 0, EndColumn: 0, NemerleUsageType.Usage);
        Equal(0, GotoMapping.ToLocations([noRange], includeDeclaration: true).Count,
            "a target without an end position is not navigable");

        // includeDeclaration drops/keeps the declaration entries (references).
        var usage = new NemerleGotoTarget(file, FileIndex: 3, Line: 9, Column: 3, EndLine: 9, EndColumn: 8, NemerleUsageType.Usage);
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

        // --- documentHighlight (WP-P2): the same collection, one file, kinds ---

        var otherFile = OperatingSystem.IsWindows() ? @"C:\repo\App\Other.n" : "/repo/App/Other.n";
        var elsewhere = new NemerleGotoTarget(otherFile, FileIndex: 4, Line: 2, Column: 1, EndLine: 2, EndColumn: 6, NemerleUsageType.Usage);
        var highlights = GotoMapping.ToDocumentHighlights([def, usage, elsewhere], fileIndex: 3);
        Equal(2, highlights.Count, "highlights keep only the entries from the requested file");
        Equal(NemerleDocumentHighlightKind.Write, highlights[0].Kind, "the declaration is a Write highlight");
        Equal(4, highlights[0].StartLine, "the declaration highlight is 0-based");
        Equal(6, highlights[0].StartCharacter, "the declaration highlight column is 0-based");
        Equal(11, highlights[0].EndCharacter, "the declaration highlight end column is 0-based");
        Equal(NemerleDocumentHighlightKind.Read, highlights[1].Kind, "a use is a Read highlight");
        Equal(8, highlights[1].StartLine, "the use highlight is the second entry");

        // Cross-file entries are excluded by FileIndex, not by path: an entry from
        // another source can never be a highlight of the document being edited.
        Equal(1, GotoMapping.ToDocumentHighlights([elsewhere], fileIndex: 4).Count,
            "the same entry is a highlight when its own file is the one requested");

        // External-assembly members are dropped explicitly (they also have no
        // in-workspace FileIndex, but the kind is what states the intent).
        var externalUse = new NemerleGotoTarget(file, FileIndex: 3, Line: 5, Column: 7, EndLine: 5, EndColumn: 12, NemerleUsageType.ExternalUsage);
        Equal(0, GotoMapping.ToDocumentHighlights([externalUse], fileIndex: 3).Count,
            "an external-assembly entry is never a highlight");

        // Generated definitions/usages classify like their plain counterparts.
        var generatedDef = new NemerleGotoTarget(file, FileIndex: 3, Line: 20, Column: 1, EndLine: 20, EndColumn: 4, NemerleUsageType.GeneratedDefinition);
        var generatedUse = new NemerleGotoTarget(file, FileIndex: 3, Line: 21, Column: 1, EndLine: 21, EndColumn: 4, NemerleUsageType.GeneratedUsage);
        var generated = GotoMapping.ToDocumentHighlights([generatedDef, generatedUse], fileIndex: 3);
        Equal(NemerleDocumentHighlightKind.Write, generated[0].Kind, "a generated definition is still a Write");
        Equal(NemerleDocumentHighlightKind.Read, generated[1].Kind, "a generated usage is still a Read");

        // A duplicate range collapses, and the declaration's Write survives
        // regardless of which order the engine reported the two in.
        var sameAsDef = new NemerleGotoTarget(file, FileIndex: 3, Line: 5, Column: 7, EndLine: 5, EndColumn: 12, NemerleUsageType.Usage);
        var collapsedReadFirst = GotoMapping.ToDocumentHighlights([sameAsDef, def], fileIndex: 3);
        Equal(1, collapsedReadFirst.Count, "a duplicate range collapses to one highlight");
        Equal(NemerleDocumentHighlightKind.Write, collapsedReadFirst[0].Kind, "Write wins when the use was seen first");
        var collapsedWriteFirst = GotoMapping.ToDocumentHighlights([def, sameAsDef], fileIndex: 3);
        Equal(1, collapsedWriteFirst.Count, "a duplicate range collapses in the other order too");
        Equal(NemerleDocumentHighlightKind.Write, collapsedWriteFirst[0].Kind, "Write is not downgraded by a later Read");

        // A document with no engine source index has no highlights.
        Equal(0, GotoMapping.ToDocumentHighlights([def, usage], fileIndex: 0).Count,
            "a document with no compiler file index yields no highlights");

        // The kind values are the protocol's own numbering, so the handler can
        // cast straight to DocumentHighlightKind.
        Equal(2, (int)NemerleDocumentHighlightKind.Read, "Read is the LSP value 2");
        Equal(3, (int)NemerleDocumentHighlightKind.Write, "Write is the LSP value 3");

        // The mirror must keep the engine's ordinals (the server casts across).
        Equal(0, (int)NemerleUsageType.Definition, "UsageType.Definition mirrors ordinal 0");
        Equal(1, (int)NemerleUsageType.Usage, "UsageType.Usage mirrors ordinal 1");
        Equal(5, (int)NemerleUsageType.ExternalUsage, "UsageType.ExternalUsage mirrors ordinal 5");
    }

    /// <summary>
    /// WP-P3.  Each refusal below is a rule that exists because the alternative
    /// quietly damages the user's code, so each one is pinned here rather than
    /// only end to end.
    /// </summary>
    private static void RenameMappingTests()
    {
        var file = OperatingSystem.IsWindows() ? @"C:\repo\App\Program.n" : "/repo/App/Program.n";
        var other = OperatingSystem.IsWindows() ? @"C:\repo\App\Other.n" : "/repo/App/Other.n";
        var uri = GotoMapping.ToUri(file);
        var otherUri = GotoMapping.ToUri(other);

        // 1-based engine coordinates, end-exclusive: "value" at line 5, cols 7..12.
        var declaration = new NemerleGotoTarget(file, 3, 5, 7, 5, 12, NemerleUsageType.Definition);
        var use = new NemerleGotoTarget(file, 3, 9, 3, 9, 8, NemerleUsageType.Usage);
        var crossFileUse = new NemerleGotoTarget(other, 4, 2, 1, 2, 6, NemerleUsageType.Usage);

        // --- Prepare ---

        Equal(NemerleRenameRefusal.NoSymbol,
            RenameMapping.Prepare([], 3, 4, 6).Refusal,
            "nothing resolved at the caret is refused as NoSymbol");

        var external = new NemerleGotoTarget(null, 0, 0, 0, 0, 0, NemerleUsageType.Definition);
        Equal(NemerleRenameRefusal.ExternalSymbol,
            RenameMapping.Prepare([external], 3, 4, 6).Refusal,
            "a metadata-only symbol has no source to rewrite");

        // Uses here, declaration not in the workspace: the ProjectReference case
        // (39-prerelease-wp-n2-log.md §7-4).  Renaming would edit the uses and
        // leave the declaration, i.e. break the build.
        Equal(NemerleRenameRefusal.DeclarationOutsideWorkspace,
            RenameMapping.Prepare([use], 3, 8, 4).Refusal,
            "a symbol declared outside this workspace is refused");

        // Same shape, but the engine also reported a source-less target: that is a
        // BCL / NuGet member, and saying "another project" would be misleading.
        Equal(NemerleRenameRefusal.ExternalSymbol,
            RenameMapping.Prepare([use, external], 3, 8, 4).Refusal,
            "uses of a metadata member are refused as external, not as cross-project");

        // The caret must be on an occurrence: inside a declaration but off any
        // name, the engine answers with the enclosing type, which is a fine
        // highlight and a destructive rename.
        Equal(NemerleRenameRefusal.CaretNotOnSymbol,
            RenameMapping.Prepare([declaration, use], 3, 0, 0).Refusal,
            "a caret that no occurrence covers is refused");

        var prepared = RenameMapping.Prepare([declaration, use], 3, 8, 4);
        True(prepared.CanRename, "a caret on a use of an in-workspace symbol can be renamed");
        Equal(8, prepared.Range!.StartLine, "the offered range is the occurrence under the caret");
        Equal(2, prepared.Range!.StartCharacter, "the offered range is 0-based");
        Equal(7, prepared.Range!.EndCharacter, "the offered range ends where the identifier does");

        // The caret sitting immediately after the last character still counts:
        // editors report `foo|` that way.
        True(RenameMapping.Prepare([declaration, use], 3, 8, 7).CanRename,
            "a caret at the trailing edge of an identifier is still on it");
        Equal(NemerleRenameRefusal.CaretNotOnSymbol,
            RenameMapping.Prepare([declaration, use], 3, 8, 8).Refusal,
            "a caret past the identifier is not on it");

        // The caret is matched in the requested document only.
        Equal(NemerleRenameRefusal.CaretNotOnSymbol,
            RenameMapping.Prepare([declaration, crossFileUse], 3, 1, 2).Refusal,
            "an occurrence in another file does not satisfy the caret rule");

        // --- New-name validation ---

        var keywords = new HashSet<string>(StringComparer.Ordinal) { "def", "surroundwith" };
        Equal(NemerleRenameRefusal.None, RenameMapping.ValidateNewName("total", keywords), "a plain identifier is accepted");
        Equal(NemerleRenameRefusal.None, RenameMapping.ValidateNewName("_x1'", keywords), "underscore, digits and a prime are identifier characters");
        Equal(NemerleRenameRefusal.InvalidNewName, RenameMapping.ValidateNewName("1counter", keywords), "an identifier cannot start with a digit");
        Equal(NemerleRenameRefusal.InvalidNewName, RenameMapping.ValidateNewName("a-b", keywords), "an operator character is not an identifier");
        Equal(NemerleRenameRefusal.InvalidNewName, RenameMapping.ValidateNewName("", keywords), "an empty name is refused");
        Equal(NemerleRenameRefusal.InvalidNewName, RenameMapping.ValidateNewName(null, keywords), "a missing name is refused");
        Equal(NemerleRenameRefusal.NewNameIsKeyword, RenameMapping.ValidateNewName("def", keywords), "a base keyword is refused");
        // The set is this file's environment, so a macro-introduced keyword is
        // refused exactly where the macro is in scope (the WP-O5a distinction).
        Equal(NemerleRenameRefusal.NewNameIsKeyword, RenameMapping.ValidateNewName("surroundwith", keywords), "a macro-introduced keyword is refused");
        Equal(NemerleRenameRefusal.None, RenameMapping.ValidateNewName("surroundwith", new HashSet<string>(StringComparer.Ordinal)), "the same word is fine where the macro is not in scope");

        // --- Occurrence verification ---

        var here = new NemerleGotoLocation(uri, 4, 6, 4, 11);
        var alsoHere = new NemerleGotoLocation(uri, 8, 2, 8, 7);
        Equal(NemerleRenameRefusal.None,
            RenameMapping.CheckOccurrences(
                [new NemerleRenameOccurrence(here, "value"), new NemerleRenameOccurrence(alsoHere, "value")],
                "value"),
            "occurrences whose text is the symbol's name are accepted");
        Equal(NemerleRenameRefusal.InconsistentOccurrences,
            RenameMapping.CheckOccurrences(
                [new NemerleRenameOccurrence(here, "value"), new NemerleRenameOccurrence(alsoHere, "other")],
                "value"),
            "an occurrence whose text is not the symbol's name stops the rename");
        Equal(NemerleRenameRefusal.InconsistentOccurrences,
            RenameMapping.CheckOccurrences(
                [new NemerleRenameOccurrence(here, "value"), new NemerleRenameOccurrence(alsoHere, null)],
                "value"),
            "an occurrence the server has no buffer for is a refusal, not permission");

        // --- Workspace edit assembly ---

        var edit = RenameMapping.ToWorkspaceEdit(
            [
                new NemerleRenameOccurrence(alsoHere, "value"),
                new NemerleRenameOccurrence(here, "value"),
                new NemerleRenameOccurrence(new NemerleGotoLocation(otherUri, 1, 0, 1, 5), "value"),
            ],
            "total");
        True(edit.IsUsable, "a well-formed rename assembles");
        Equal(2, edit.Edit!.DocumentCount, "edits are grouped per document");
        Equal(3, edit.Edit!.EditCount, "every occurrence became an edit");
        // Documents come out ordered by URI, so address the one under test by name.
        var edited = edit.Edit!.Documents.Single(document => document.Uri == uri);
        Equal(4, edited.Edits[0].StartLine, "edits within a document are ordered by position");
        Equal(8, edited.Edits[1].StartLine, "the later occurrence follows it");
        Equal("total", edited.Edits[0].NewText, "each edit inserts the new name");

        // Exact duplicates collapse (the engine can report one location twice),
        // but two different replacements for one span are a real disagreement.
        var duplicated = WorkspaceEditMapping.Build(
            [
                new NemerleTextEdit(uri, 4, 6, 4, 11, "total"),
                new NemerleTextEdit(uri, 4, 6, 4, 11, "total"),
            ]);
        Equal(1, duplicated.Edit!.EditCount, "an exactly duplicated edit collapses");
        var contradictory = WorkspaceEditMapping.Build(
            [
                new NemerleTextEdit(uri, 4, 6, 4, 11, "total"),
                new NemerleTextEdit(uri, 4, 6, 4, 11, "sum"),
            ]);
        True(!contradictory.IsUsable, "two different replacements for one span are a conflict");

        // Overlap is refused rather than repaired: LSP applies a document's edits
        // against its original text and forbids overlapping ranges.
        var overlapping = WorkspaceEditMapping.Build(
            [
                new NemerleTextEdit(uri, 4, 6, 4, 11, "total"),
                new NemerleTextEdit(uri, 4, 9, 4, 14, "total"),
            ]);
        True(!overlapping.IsUsable, "overlapping edits are refused");
        True(overlapping.Conflict is not null, "the conflict says which ranges collided");

        // Touching ranges do not overlap, and neither do two insertions at one
        // point - WP-P5 will emit those for generated members.
        var touching = WorkspaceEditMapping.Build(
            [
                new NemerleTextEdit(uri, 4, 6, 4, 11, "total"),
                new NemerleTextEdit(uri, 4, 11, 4, 16, "total"),
                new NemerleTextEdit(uri, 6, 0, 6, 0, "  Added() : void { }\n"),
                new NemerleTextEdit(uri, 6, 0, 6, 0, "  AlsoAdded() : void { }\n"),
            ]);
        True(touching.IsUsable, "touching ranges and insertions at one point are not overlaps");
        Equal(4, touching.Edit!.EditCount, "all four edits survive");
    }

    /// <summary>
    /// WP-P5.  The engine generates members flush left (its <c>_indentSize</c>
    /// starts at 0 and is protected), so what the user actually gets in their file
    /// is decided here.
    /// </summary>
    private static void CodeActionMappingTests()
    {
        var file = OperatingSystem.IsWindows() ? @"C:\repo\App\Program.n" : "/repo/App/Program.n";
        var uri = GotoMapping.ToUri(file);

        // Indentation: one step deeper than the line the insertion lands on, in
        // that line's own whitespace style.
        Equal("  ", CodeActionMapping.IndentFor(""), "a flush-left closing brace indents members by one step");
        Equal("    ", CodeActionMapping.IndentFor("  "), "a two-space brace indents members by four");
        Equal("\t\t", CodeActionMapping.IndentFor("\t"), "a tab-indented brace stays tabbed");

        // Re-indentation keeps relative structure, normalizes line endings, and
        // does not leave trailing whitespace on blank lines.
        var generated = "public Draw() : void{ throw System.NotImplementedException() }\r\n\r\n";
        var reindented = CodeActionMapping.Reindent(generated, "  ");
        Equal("  public Draw() : void{ throw System.NotImplementedException() }\n", reindented,
            "generated source is indented and its trailing blank line dropped");
        Equal(string.Empty, CodeActionMapping.Reindent("   \n\n", "  "), "whitespace-only generation yields nothing");

        var multi = CodeActionMapping.Reindent("a\n  b\n", "\t");
        Equal("\ta\n\t  b\n", multi, "relative indentation inside a member is preserved");

        // The common case: the insertion point is just before a closing brace
        // that sits at the start of its line.
        var atLineStart = new NemerleInsertionPoint(uri, 9, 0, "");
        var action = CodeActionMapping.ToCodeAction(
            new NemerleMemberGeneration("IWidget", 2, "public Draw() : void{ }\npublic Stop() : void{ }\n"),
            atLineStart,
            "  ",
            "Implement {0} ({1} {2})");
        True(action is not null, "a non-empty generation becomes an action");
        Equal("Implement IWidget (2 members)", action!.Title, "the title names the interface and the count");
        Equal(9, action.Edit.StartLine, "the edit is at the insertion line");
        Equal(action.Edit.StartLine, action.Edit.EndLine, "the edit is an insertion");
        Equal(action.Edit.StartCharacter, action.Edit.EndCharacter, "the edit has an empty range");
        Equal("  public Draw() : void{ }\n  public Stop() : void{ }\n", action.Edit.NewText,
            "both members are inserted, indented, with the brace left at column 0");

        // One member: the title reads naturally.
        var single = CodeActionMapping.ToCodeAction(
            new NemerleMemberGeneration("IWidget", 1, "public Draw() : void{ }\n"),
            atLineStart,
            "  ",
            "Implement {0} ({1} {2})");
        Equal("Implement IWidget (1 member)", single!.Title, "a single member is not pluralized");

        // A one-line `class Foo { }`: the insertion point is mid-line, so the
        // members have to start on a fresh line, and the text that preceded them
        // is put back so the brace keeps its place.
        var midLine = new NemerleInsertionPoint(uri, 3, 12, "class Foo { ");
        var wrapped = CodeActionMapping.ToCodeAction(
            new NemerleMemberGeneration("IWidget", 1, "public Draw() : void{ }\n"),
            midLine,
            "  ",
            "Implement {0} ({1} {2})");
        Equal("\n  public Draw() : void{ }\nclass Foo { ", wrapped!.Edit.NewText,
            "a mid-line insertion opens with a newline and restores what preceded it");

        // An indented brace: its own indentation is restored after the members.
        var indentedBrace = new NemerleInsertionPoint(uri, 7, 2, "  ");
        var nested = CodeActionMapping.ToCodeAction(
            new NemerleMemberGeneration("IWidget", 1, "public Draw() : void{ }\n"),
            indentedBrace,
            "    ",
            "Implement {0} ({1} {2})");
        Equal("    public Draw() : void{ }\n  ", nested!.Edit.NewText,
            "the nested brace's indentation is put back after the members");

        // Nothing to insert is not an action.
        True(CodeActionMapping.ToCodeAction(
            new NemerleMemberGeneration("IWidget", 0, "   "),
            atLineStart,
            "  ",
            "Implement {0} ({1} {2})") is null,
            "an empty generation offers no action");
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

    /// <summary>
    /// WP-M6 (§6.8): the toolchain/server generation comparison. The rule under test is that
    /// only a positive disagreement between two KNOWN assembly versions warns - absence of
    /// evidence must stay silent, or the warning becomes noise users learn to dismiss.
    /// </summary>
    private static void ToolchainProvenanceTests()
    {
        var server = new NemerleProvenance("5b5e0e4f6dcea9278e469cb377a4b45830ed1b03", "5b5e0e4f6", "1.2.0.601", "language server");
        var same = new NemerleProvenance("5b5e0e4f6dcea9278e469cb377a4b45830ed1b03", "5b5e0e4f6", "1.2.0.601", "dist/ncc");
        var older = new NemerleProvenance("aaaaaaaaabbbbbbbbbccccccccc", "aaaaaaaaa", "1.2.0.547", "dist/ncc");

        True(ToolchainProvenance.DescribeMismatch(server, same) is null, "same assembly version does not warn");

        var mismatch = ToolchainProvenance.DescribeMismatch(server, older);
        True(mismatch is not null, "different assembly versions warn");
        True(mismatch!.Contains("1.2.0.547", StringComparison.Ordinal) && mismatch.Contains("1.2.0.601", StringComparison.Ordinal),
            "the warning names BOTH generations (naming one is what the raw FileLoadException already does)");

        // Unknown on either side means "no evidence", not "mismatch".
        True(ToolchainProvenance.DescribeMismatch(server, NemerleProvenance.Unknown) is null, "unknown toolchain does not warn");
        True(ToolchainProvenance.DescribeMismatch(NemerleProvenance.Unknown, older) is null, "unknown server does not warn");
        True(ToolchainProvenance.DescribeMismatch(
                server with { AssemblyVersion = "" },
                older with { AssemblyVersion = "" }) is null,
            "two unreadable versions do not warn even when commits differ");

        // Differing commits at the same assembly version are NOT a load hazard (the version is
        // what binding uses), so they must not warn either.
        True(ToolchainProvenance.DescribeMismatch(server, same with { Commit = "0123456789abcdef" }) is null,
            "same version with a different commit does not warn");

        Equal("1.2.0.601 (5b5e0e4f6, language server)", server.Describe_Short(), "short provenance rendering");
        True(!NemerleProvenance.Unknown.IsKnown, "empty provenance is not known");

        // Reading a directory that has neither a json nor a Nemerle.dll yields Unknown rather
        // than throwing: a missing diagnostic aid must never break a session.
        var empty = ToolchainProvenance.Read(Path.GetTempPath(), "definitely-not-there.json", "test");
        True(!empty.IsKnown, "absent provenance reads as unknown");
        True(!ToolchainProvenance.Read(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString()), "ncc-info.json", "test").IsKnown,
            "nonexistent directory reads as unknown");
    }

    /// <summary>
    /// WP-M6: the Nemerle.Sdk.Unofficial package as an artifact. Asserts the two things that
    /// silently produce a broken package rather than a failed pack - the nupkg entry layout
    /// (Sdk/, targets/, tasks/, tools/ncc/ at the exact paths Sdk.props/Sdk.targets reference)
    /// and the import structure of an SDK-based project (§8: Microsoft.NET.Sdk-derived and
    /// Nemerle-derived properties coexisting, with the Nemerle CoreCompile winning).
    /// Requires `pwsh dotnet-port\pack-tool.ps1 -Pack` to have run.
    /// </summary>
    private static async Task SdkPackageTests()
    {
        var root = FindRepositoryRoot();
        var feed = Path.Combine(root, "dotnet-port", "dist", "release");
        var packages = Directory.Exists(feed)
            ? Directory.GetFiles(feed, "Nemerle.Sdk.Unofficial.*.nupkg")
            : [];
        if (packages.Length == 0)
        {
            Console.WriteLine("SKIP Sdk package tests (no Nemerle.Sdk.Unofficial nupkg; run pack-tool.ps1 -Pack)");
            return;
        }

        var nupkg = packages.OrderBy(static path => path, StringComparer.Ordinal).Last();
        using (var archive = System.IO.Compression.ZipFile.OpenRead(nupkg))
        {
            var entries = archive.Entries.Select(static e => e.FullName).ToArray();
            foreach (var required in new[]
                     {
                         "Sdk/Sdk.props",
                         "Sdk/Sdk.targets",
                         "targets/Nemerle.Core.targets",
                         "tasks/Nemerle.MSBuild.Tasks.dll",
                         "tools/ncc/ncc.dll",
                         "tools/ncc/Nemerle.dll",
                         "tools/ncc/Nemerle.Compiler.dll",
                         "tools/ncc/Nemerle.Macros.dll",
                         "tools/ncc/Nemerle.CoreEmit.dll",
                         "tools/ncc/Nemerle.Compiler.Hosting.dll",
                         "tools/ncc/ncc-info.json",
                     })
                True(entries.Contains(required, StringComparer.Ordinal), $"package contains {required}");

            // The CLI wrappers bake machine-specific absolute paths into ncc.default.rsp, which
            // would make the package non-relocatable; nunit is a stray of pack-tool.ps1's
            // blanket *.dll copy of the Stage2 output. Neither belongs in a nupkg.
            foreach (var unwanted in new[] { "tools/ncc/ncc.default.rsp", "tools/ncc/ncc.cmd", "tools/ncc/gen-default-rsp.ps1", "tools/ncc/nunit.framework.dll", "tools/ncc/ncc.exe" })
                True(!entries.Contains(unwanted, StringComparer.Ordinal), $"package excludes {unwanted}");

            // The shipped build logic must be the same file the repo checkout imports: the whole
            // point of the WP-M6 consolidation is that there is nothing to drift.
            var shipped = archive.GetEntry("targets/Nemerle.Core.targets")!;
            using var reader = new StreamReader(shipped.Open());
            var shippedText = await reader.ReadToEndAsync();
            var repoText = await File.ReadAllTextAsync(Path.Combine(root, "dotnet-port", "msbuild", "Nemerle.Core.targets"));
            Equal(repoText.Replace("\r\n", "\n"), shippedText.Replace("\r\n", "\n"), "packaged targets are byte-identical to the repo's");
        }

        // Evaluation test (§8): the import structure of a real Sdk-based project. Uses the
        // template's own shape via the staged copy, resolved from the local feed.
        var probe = Path.Combine(Path.GetTempPath(), "nemerle-sdk-probe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(probe);
        try
        {
            var version = Path.GetFileNameWithoutExtension(nupkg)["Nemerle.Sdk.Unofficial.".Length..];
            await File.WriteAllTextAsync(Path.Combine(probe, "NuGet.config"),
                $"""
                 <?xml version="1.0" encoding="utf-8"?>
                 <configuration>
                   <packageSources>
                     <clear />
                     <add key="nemerle-local" value="{feed}" />
                   </packageSources>
                 </configuration>
                 """);
            await File.WriteAllTextAsync(Path.Combine(probe, "Probe.nproj"),
                $"""
                 <Project Sdk="Nemerle.Sdk.Unofficial/{version}">
                   <PropertyGroup>
                     <OutputType>Exe</OutputType>
                     <TargetFramework>net10.0</TargetFramework>
                   </PropertyGroup>
                 </Project>
                 """);
            await File.WriteAllTextAsync(Path.Combine(probe, "Program.n"),
                "module Program\n{\n  Main() : void { System.Console.WriteLine(\"probe\"); }\n}\n");

            var executor = new SystemProcessExecutor();
            var query = await executor.RunAsync(
                new ProcessSpec(
                    "dotnet",
                    [
                        "msbuild", Path.Combine(probe, "Probe.nproj"), "-nologo",
                        // TargetFramework: only Microsoft.NET.Sdk can produce it, proving the
                        // explicit import inside Sdk.props/Sdk.targets took effect.
                        // NccLayoutDir: only Nemerle's Sdk.props sets it.
                        // NemerleCompile: proves the default **/*.n glob ran.
                        "-getProperty:TargetFramework,NccLayoutDir,ProduceReferenceAssembly,UseAppHost,GenerateDependencyFile",
                        "-getItem:NemerleCompile",
                    ],
                    probe,
                    TimeSpan.FromMinutes(2)),
                CancellationToken.None);
            Equal(0, query.ExitCode, "SDK-based project evaluates (stderr: " + query.StandardError + ")");

            using var document = System.Text.Json.JsonDocument.Parse(query.StandardOutput);
            var properties = document.RootElement.GetProperty("Properties");
            Equal("net10.0", properties.GetProperty("TargetFramework").GetString(), "Microsoft.NET.Sdk property present (explicit import worked)");
            True((properties.GetProperty("NccLayoutDir").GetString() ?? "").Replace('\\', '/').Contains("nemerle.sdk.unofficial/" + version + "/", StringComparison.OrdinalIgnoreCase),
                "NccLayoutDir points into the resolved package, not the repo");
            // Nemerle's defaults must survive the Microsoft.NET.Sdk import that follows them.
            Equal("false", properties.GetProperty("ProduceReferenceAssembly").GetString(), "ncc has no /refout: equivalent, so the SDK's ref-assembly optimization stays off");
            Equal("false", properties.GetProperty("UseAppHost").GetString(), "no native apphost");
            Equal("true", properties.GetProperty("GenerateDependencyFile").GetString(), "deps.json keeps the SDK default (WP-O3): the Nemerle runtime closure is supplied by the Nemerle.Runtime.Unofficial package, so it lands in deps.json legitimately");
            var globbed = document.RootElement.GetProperty("Items").GetProperty("NemerleCompile");
            Equal(1, globbed.GetArrayLength(), "default **/*.n glob found the single source exactly once");
        }
        finally
        {
            try { Directory.Delete(probe, true); } catch (IOException) { /* best effort */ }
        }
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
