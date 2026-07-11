# 02 — Build flow map (bootstrap on MSBuild 4.0, groundwork for .NET 10)

Extracted ncc command lines (verified from a real `/verbosity:detailed` Stage1
Release rebuild, exit 0, 0 errors, ~38 s) are in section 7.

## 1. Stage flow (ASCII)

```
boot-4.0\  (checked-in binaries: Nemerle.dll, Nemerle.Compiler.dll,
            Nemerle.Macros.dll, ncc.exe, ncc32.exe, ncc64.exe)
    |
    |  NPrepareBoot: copy *.exe *.dll -> $(NBoot) = bin\<Cfg>\net-4.0\boot\
    |  NPrepareKeys: copy misc\keys\*.snk -> bin\<Cfg>\net-4.0\keys\
    |  NTasks: csc-build Nemerle.MSBuild.Tasks.csproj -> $(NBoot)\Nemerle.MSBuild.Tasks.dll
    |          (+ copies tools\msbuild-task\Nemerle.MSBuild.targets to $(NBoot))
    v
Stage1: msbuild @(NCompilerProject) with Nemerle=$(NBoot)
        -> bin\<Cfg>\net-4.0\Stage1\ {Nemerle.dll, Nemerle.Compiler.dll,
           Nemerle.Macros.dll, ncc.exe, ncc32.exe, ncc64.exe}
        + copy NTasksFiles (task dll + targets) into Stage1 so it is a
          self-contained "compiler directory"
    v
Stage2: same projects, Nemerle=bin\...\Stage1  -> bin\...\Stage2
    v
Stage3: same projects, Nemerle=bin\...\Stage2  -> bin\...\Stage3
    v
Stage4: same projects, Nemerle=bin\...\Stage3  -> bin\...\Stage4
    v
Validate: ildasm Stage3 vs Stage4 outputs, strip // comments, fc/diff the IL,
          then peverify   (requires Framework SDK -> NOT runnable on this machine)
    v
CompilerTests: build test framework + Linq/Unsafe/Peg/CSharp/WPF, copy runner +
          testsuite\ into bin\...\Tests\{positive,negative}, run
          Nemerle.Compiler.Test.exe over testsuite\positive|negative\*.n

Umbrella dev targets: DevBuildQuick(=Stage1+Install), DevBuild2Stage(default),
DevBuildFull(=Stage4+Validate+CompilerTests+PowerPack+Install).
```

Key path properties (NemerleAll.nproj lines 17-37):
- `NVer` = `net-4.0` (from `TargetFrameworkVersion=v4.0` + MSBuild present)
- `NBin` = `$(NRoot)\bin\$(Configuration)\net-4.0`, `NObj` likewise under obj\
- `NBoot` = `$(NBin)\boot\`; `NRootBoot` = `$(NRoot)\boot-4.0\`
- `NProjectConstants` = `RUNTIME_MS;NET_4_0` (+`DEBUG` for Debug cfg), passed to
  every stage as `DefineConstants`.
- Every stage passes: `OutputPath=$(NCurBin); IntermediateOutputPath=$(NCurObj)\;
  DefineConstants=...; Nemerle=$(NPrevBin); NKeysDir=$(NBin)\keys; SDKBin=$(SDKBin)`.
  **`Nemerle` is the single property that selects the compiler binaries directory.**

## 2. @(NCompilerProject) and other item groups

### Core compiler projects — @(NCompilerProject) (built 4x, once per stage)

| Artifact | Project file | Source dirs (Compile items) | Notes |
|---|---|---|---|
| Nemerle.dll | `F:\dev\nemerle_projects\github-nemerle\Nemerle.nproj` | `lib\*.n` | Runtime/stdlib. Library, `NoStdLib=true`, `GreedyReferences=false`, EnabledWarnings 10006, key `Nemerle.snk`. Refs: mscorlib, System, System.Xml. AfterBuild makes publisher-policy dll via AL.exe — skipped when `$(SDKBin)\al.exe` missing. |
| Nemerle.Compiler.dll | `Nemerle.Compiler.nproj` | `ncc\CompilationOptions.n`, `ncc\passes.n`, `ncc\{parsing,codedom,CompilerMessage,completion,external,external\ExternalMemberInfo,external\ExternalTypeInfo,generation,hierarchy,misc,optimization,typing}\*.n` | Library, key `Nemerle.Compiler.snk`. Refs: mscorlib, System, System.Core, System.Xml. ProjectReference: Nemerle.nproj. Same AL publisher-policy AfterBuild (conditional). |
| Nemerle.Macros.dll | `Nemerle.Macros.nproj` | `macros\*.n` | Library. Refs: mscorlib, System, System.Data, System.Xml, System.Windows.Forms, System.Core, System.Xml.Linq. ProjectReferences: Nemerle.Compiler.nproj, Nemerle.nproj. |
| ncc.exe | `ncc.nproj` | `ncc\main.n` + `ncc\misc\AssemblyInfo.n` | Exe driver. Refs: mscorlib, System. ProjectReferences: Nemerle.Compiler, Nemerle. Release adds `DocumentationFile`. |
| ncc32.exe | `ncc32.nproj` | (imports ncc.nproj) | Just sets `PlatformTarget=x86`, `AssemblyName=ncc32`. |
| ncc64.exe | `ncc64.nproj` | (imports ncc.nproj) | x64 variant; only included when CPU is AMD64. |

All core .nproj share: `DefineConstants` RUNTIME_MS + `_stage3` (+NET_4_0),
`NoStdLib=true` (do NOT auto-load Nemerle.dll — they build it themselves),
`GreedyReferences=false`, `Nemerle` defaults to `$(MSBuildProjectDirectory)\boot-4.0`
when not passed, `<Import Project="$(Nemerle)\Nemerle.MSBuild.targets" />`.

### Other item groups in NemerleAll.nproj (lines 51-168)

| Group | Contents | Used by target |
|---|---|---|
| `NTasksProject` | `Nemerle.MSBuild.Tasks.csproj` (or `Nemerle.XBuild.Tasks.csproj` on mono) | NTasks |
| `CSharpCompilerFiles` | `$(NBin)\PowerPack\{CSharpParser.dll, ncc.parser.csharp.dll, Nemerle.Peg.dll}` | CompilerTests (copied next to tests) |
| `NTestFramework` | `snippets\Nemerle.Test\Nemerle.Test.Framework`, `...\Nemerle.Compiler.Test`, `snippets\Nemerle.Diff\{Nemerle.Diff, Nemerle.Diff.Tests}` | TestFramework |
| `NPeg` / `NCSharp` | peg-parser and csharp-parser snippet projects | _PegAndCSharp |
| `NComputationExpressions`, `NAsync`, `NStatechart`, `NPowerPack`, `NWpf` | snippet libraries | _PowerPack chain |
| `NLinq` | `Linq\Macro\Linq.nproj` -> Nemerle.Linq.dll | Linq (needed by CompilerTests) |
| `NUnsafe` | `snippets\Nemerle.Unsafe\...` -> Nemerle.Unsafe.dll | Unsafe (needed by CompilerTests) |
| `NTools` | nemish, Nemerle.Evaluation, Nemerle.NAnt.Tasks, reflector-addon | Tools |
| `NIntegrationProject` | VS2010 integration csprojs (v4.0 flavor: `snippets\VS2010\{WpfHint,Nemerle.Compiler.Utils,Nemerle.VisualStudio}`) | _Integration |
| `CompilerTestsFiles` | `$(NBin)\Tests\**\*.*` | CompilerTests cleanup |

## 3. How ncc is invoked — the Ncc MSBuild task

Chain: `.nproj` -> `<Import Project="$(Nemerle)\Nemerle.MSBuild.targets" />` ->
targets file declares
`<UsingTask TaskName="Nemerle.Tools.MSBuildTask.Ncc" AssemblyFile="$(Nemerle)\Nemerle.MSBuild.Tasks.dll"/>`
and *itself imports* `$(MSBuildBinPath)\Microsoft.Common.targets`, overriding
`CoreCompile` to call `<Ncc ...>` instead of `<Csc>`.

Task source: `F:\dev\nemerle_projects\github-nemerle\tools\msbuild-task\MSBuildTask.cs`
(built by root `Nemerle.MSBuild.Tasks.csproj`; the targets file is copied beside the
dll in AfterBuild). Class `Ncc : Microsoft.Build.Tasks.ManagedCompiler` (a ToolTask):

- **Out-of-proc**: it runs `ncc.exe` as a separate process (ToolTask.Execute).
  Nothing is hosted in the MSBuild appdomain.
- **Response file**: everything except the exe name goes into a temporary
  response file; `GetResponseFileSwitch` returns `/from-file:"<rsp>"`, matching
  ncc's `-from-file` / `@` option. So the real OS command line is just
  `<path>\ncc.exe /from-file:"...tmp"`.
- **Binary selection** (`GenerateFullPathToTool`/`FindExecutable`):
  1. `$(CompilerPath)` task parameter = MSBuild property **`$(Nemerle)`** — first
     match `$(Nemerle)\ncc.exe` wins. This is the swap point.
  2. else the directory containing Nemerle.MSBuild.Tasks.dll itself;
  3. else registry `HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\ncc.exe`;
  4. else bare `ncc.exe` from PATH.
  `ToolName` is `ncc.exe` (or `ncc`/`ncc.bat` under the MONO build of the task).
  There is also `ToolPath="$(CscToolPath)"` passed from the targets — if set,
  ToolTask uses `ToolPath\ToolName` directly (unused in this repo's builds).
- **Switches generated** (`AddResponseFileCommandsImpl`), each on its own line in
  the rsp: `/debugger` (opt), `/optimize` (opt), `/checked[+-]`, `/no-color`,
  `/lib:`, `/nowarn:`, `/dowarn:`, `/no-stdlib`, `/no-stdmacros`,
  `/greedy-references:-` (when GreedyReferences != true), `/warn:` (if !=4),
  `/indentation-syntax`, `/doc:`, `/define:` (';'-joined), `/win32res:`,
  `/platform:`, `/addmodule:`, `/delaysign[+-]`, `/keycontainer:`, `/keyfile:`,
  `/linkresource:`, `/nologo`, `/resource:`, `/target:`, `/warnaserror[+-]`,
  `/win32icon:`, `/debug[+-]`, `/project-path:`, `/root-namespace:`, `/main:`,
  `/stack-size:`, then **sources** (plain file names), then `/fromfile:` for each
  CompilerResponseFile, **`/ref:<resolved path>` per non-macro ReferencePath**,
  **`/macros:<path>` per macro reference**, `$(CustomArguments)` verbatim, and
  finally `/out:<IntermediateAssembly>`.
- **Macro references**: targets adds `ResolveMacroAssemblyReferences` (RAR over
  `@(MacroReference)` -> `@(MacroReferencePath)`) and an ItemDefinitionGroup that
  gives `MacroProjectReference` items `OutputItemType=macro`; CoreCompile splits
  `@(ReferencePath)` into `_NonMacroReferencePath` (-> `/ref:`) and
  `_MacroProjectReferencePath` (-> `/macros:`).
- Error/warning lines from ncc stdout are re-parsed by regex into MSBuild
  Log.LogError/LogWarning (supports both `path(l,c,l,c): error:` and old
  `path:l:c:` formats).

## 4. How framework references reach ncc

- The `.nproj` files list plain `<Reference Include="mscorlib" />, System, ...`.
  Standard `ResolveAssemblyReferences` (from Microsoft.Common.targets, imported by
  Nemerle.MSBuild.targets) resolves them against `$(TargetFrameworkDirectory)`
  reference assemblies, producing full paths in `@(ReferencePath)`; the Ncc task
  then emits `/ref:<full path to mscorlib.dll>` etc. So **ncc receives absolute
  Framework reference-assembly paths, not bare names** — good news for a dotnet
  port: point RAR (or a script) at any ref-assembly set and ncc will use it.
- ncc side (`ncc\CompilationOptions.n`):
  - `-reference|-r|-ref <s>` appends to `ReferencedLibraries` (line 393);
    `-library-path|-lib|-L` appends to `LibraryPaths` (398).
  - `-no-stdlib|-nostdlib` sets `DoNotLoadStdlib` (561); `-no-stdmacros` sets
    `DoNotLoadMacros`; `-use-loaded-corlib` sets `UseLoadedCorlib` (565) —
    "use already loaded mscorlib.dll and System.dll" of the *running* CLR.
- Defaults (`ncc\passes.n` LoadExternalLibraries, lines 563-636): **unless
  `-no-stdlib`, ncc implicitly `AddLibrary`s `mscorlib`, `System`, `Nemerle`,
  `System.Xml` by bare name**. `-no-stdmacros` gates loading of
  `Nemerle.Macros` (strong name `PublicKeyToken=5291d186334f6101`, version = the
  running Nemerle.Compiler's version). It also probes `AppDomain.BaseDirectory`
  for `ncc.parser.*.dll` plugin parsers.
- Resolution (`ncc\external\LibraryReferenceManager.n`): names with `/` or `\`
  -> `Assembly.LoadFrom(path)`; names with `,` -> `Assembly.Load(fullname)`;
  bare names -> probe `_lib_path` (dir of Nemerle.dll's assembly, CWD, dir of
  System.dll of the running runtime, dir of the task/compiler assembly, dir of
  mscorlib of the running runtime, plus `-lib` paths) for `<name>.dll`, finally
  `Assembly.LoadWithPartialName`. With `UseLoadedCorlib`, `mscorlib`/`System`
  map directly to the *loaded* corlib/System assemblies.
  **Implication for .NET 10 hosting: ncc reflects over reference assemblies with
  System.Reflection (LoadFrom), so the compiler process's own runtime and the
  target refs must be load-compatible; `-use-loaded-corlib` + `-no-stdlib` are the
  existing escape hatches.**
- The compiler-build projects themselves all set `NoStdLib=true` and pass every
  reference explicitly (their `/ref:` list includes the previous stage's
  Nemerle.dll via ProjectReference resolution), plus
  `/greedy-references:-` so ncc does not chase transitive assembly refs.

## 5. Support targets and what is skippable

| Target | What it does | Modern-machine notes |
|---|---|---|
| `InitTools` | `GetFrameworkPath`/`GetFrameworkSdkPath` -> `FW40`, `SDK_3`; probes v8.0A/v8.1A/v10.0A SDK dirs for al.exe to set `SDKBin`; defines `GacUtil`,`Ildasm`,`PEVerify`,`NGen`,`Junction`,`Nuget` properties; prints them. | Pure property discovery + Messages. `NGen`/`GacUtil`/`Junction` are **never executed by any target in NemerleAll.nproj** (grep confirms: only echoed). Without a Framework SDK, `SDKBin` stays empty -> AL publisher-policy AfterBuild steps in the three lib .nproj files are skipped (`Condition="Exists('$(SDKBin)\al.exe')"`). `Ildasm`/`PEVerify` are used only by `Validate`. Skippable/replaceable wholesale for a dotnet port. |
| `NTasks` | Builds `Nemerle.MSBuild.Tasks.csproj` (C#, TFV v3.5 by default, references Microsoft.Build.Tasks.v4.0/Utilities.v4.0 under `/tv:4.0`) into `$(NBoot)`; AfterBuild copies `Nemerle.MSBuild.targets` beside it. Registers outputs as `@(NTasksFiles)` which every stage copies into its output dir. | Needs csc + old MSBuild assemblies. Under dotnet MSBuild the task would have to be rebuilt against Microsoft.Build.Utilities.Core (ManagedCompiler no longer public — see §6). |
| `NPrepareBoot` | Copies `boot-4.0\*.exe;*.dll` -> `bin\<cfg>\net-4.0\boot\`. | Trivial; keep as a copy step in any port. |
| `NPrepareKeys` | Copies `misc\keys\*.snk` -> `bin\<cfg>\net-4.0\keys`; projects use `$(NKeysDir)\Nemerle(.Compiler).snk` for `/keyfile:`. | Full-key .snk signing; works on .NET Core ncc only if ncc's own strong-name emit works there (ncc passes keyfile to Reflection.Emit / SRE AssemblyName.KeyPair on Framework — will need attention in the port). |
| `Install` | Requires a prior stage (`NCurBin`); copies `$(NCurBin)\*.*` + Linq/Unsafe/TestFramework/PowerPack/Nuget/VsIntegration outputs to `$(NInstall)` (default `%ProgramFiles%\Nemerle\Net-4.0`), excluding duplicate core dlls. **No GAC, no ngen, no registry writes** — plain xcopy. | Optional; skip for port work or point `NInstall` anywhere. |
| `Validate` | ildasm both of the last two stages, comment-strip via MSBuild.Community.Tasks.FileUpdate (`ExternalDependences\MSBuild.Community.Tasks.dll`), `fc` the IL, `peverify` stage4. | Requires SDK ildasm/peverify -> not runnable here. For the port, replace with `dotnet-ildasm`/`ilverify` or byte-compare determinism checks. |
| Registry usage | Only inside the Ncc task's `FindExecutable` fallback (App Paths\ncc.exe) — never hit when `$(Nemerle)` is set, and it is always set during bootstrap. | Non-issue. |

## 6. MSBuild-4.0 / Framework-specific hazards for `dotnet msbuild`

Things in the current chain that break under .NET (Core) MSBuild:

1. **`ToolsVersion="4.0"` + `/tv:4.0`** on NemerleAll.nproj and all .nproj files —
   dotnet MSBuild ignores/errors on old ToolsVersion semantics (it tolerates the
   attribute but `$(MSBuildBinPath)\msbuild.exe` existence check in line 4 of
   NemerleAll.nproj (`UseMSBuild`) fails: dotnet's MSBuild dir has MSBuild.dll,
   not msbuild.exe -> the repo would take the **mono** code path, wrong defaults).
2. **`Nemerle.MSBuild.Tasks.dll`** is compiled against
   `Microsoft.Build.Tasks.v4.0` / `Utilities.v4.0` and derives from
   `Microsoft.Build.Tasks.ManagedCompiler`, which is **internal/absent** in
   Microsoft.Build.Tasks.Core on .NET — the task must be rewritten as a plain
   `ToolTask` (the ~40 lines of switch emission in `AddResponseFileCommandsImpl`
   are the only real logic, plus the response-file trick and log parsing). It also
   uses `Microsoft.Win32.Registry` and private-reflection into
   `CommandLineBuilder` (`GetCommandLine`, `AppendTextWithQuoting`).
3. **`<Import Project="$(MSBuildBinPath)\Microsoft.Common.targets" />`** inside
   Nemerle.MSBuild.targets — on dotnet MSBuild the file exists as
   Microsoft.Common.targets but expects the Sdk-style bootstrapping; the legacy
   non-Sdk import path *does* still work (`Microsoft.Common.props` not imported,
   GetFrameworkPath etc. mostly stubbed), but `TargetFrameworkVersion=v4.0` RAR
   resolution of `mscorlib`/`System` needs .NET Framework reference assemblies
   (`Microsoft.NETFramework.ReferenceAssemblies` package or installed ref dirs) —
   there is no implicit Framework targeting pack on a bare dotnet SDK box.
4. **`GetFrameworkPath` / `GetFrameworkSdkPath` tasks** (InitTools) exist but
   return empty on core MSBuild; harmless here (values only echoed/AL-gated).
5. **`AL` task** (publisher policy AfterBuild in the three lib projects) — no
   al.exe; already skipped via `Exists('$(SDKBin)\al.exe')`.
6. **`MSBuild.Community.Tasks.dll`** (`FileUpdate`, `TemplateFile`,
   `GetGitTagRevision` UsingTask in root project files) — net20 assembly;
   UsingTask will fail to load in dotnet MSBuild if those targets run
   (Validate, AfterBuild policy templates). Avoid those targets.
7. **resx/resgen**: none of the four core projects have `EmbeddedResource` items,
   so `CreateManifestResourceNames`/resgen is not exercised for the bootstrap.
   (Only /resource: plumbing exists in the task; unused here.)
8. **Strong-name signing inside ncc** and **System.Reflection.Emit.Save** —
   ncc.exe emits assemblies via SRE `AssemblyBuilder.Save`, which does not exist
   on .NET Core; that is the compiler-porting problem (separate doc), not a
   targets problem, but it defines what "dotnet-hosted ncc" must replace.
9. **`$(ProgramFiles)` install default, `fc`, `IF EXIST ... DEL` Exec commands** —
   Windows cmd specifics in Validate/CompilerTests; fine on Windows, replace for
   cross-plat.

### Smallest path to compile lib/ macros/ ncc/ with a dotnet-hosted ncc

Skip the entire targets chain. The Ncc task is just "write rsp, run
`ncc.exe /from-file:rsp`". Equivalent bootstrap without MSBuild:

```
dotnet ncc.dll -no-color -greedy-references:- -no-stdlib -nostdmacros(*) \
    -def:RUNTIME_MS;NET_4_0;TRACE;_stage3 -optimize -debug- \
    -keyfile:misc\keys\<key>.snk -target:library|exe \
    -ref:<corlib refs...> -ref:<prev-stage Nemerle.dll ...> \
    <expanded source globs> -out:<artifact>
```

concretely, 4 response files generated once from the globs in §2 (lib\*.n; the
13 ncc\ source dirs; macros\*.n; ncc\main.n+AssemblyInfo.n), invoked in
dependency order Nemerle -> Nemerle.Compiler -> Nemerle.Macros -> ncc, with
`-ref` pointing at the previous stage's outputs and at whatever corlib refs the
dotnet-hosted ncc understands (`-use-loaded-corlib` may substitute for
mscorlib/System refs). Exact current Stage1 command lines: §7.

(*) note: current builds do NOT pass -no-stdmacros (it is commented out in the
.nproj files) — the boot compiler loads its own Nemerle.Macros by strong name.

## 7. Extracted Stage1 ncc command lines (Release, net-4.0) — VERIFIED

Extracted from `msbuild NemerleAll.nproj /t:Stage1 /p:Configuration=Release
/p:NTargetName=Rebuild /tv:4.0 /p:TargetFrameworkVersion=v4.0 /verbosity:detailed`
(build succeeded: 0 errors, 14 warnings). The actual OS invocation is
`bin\Release\net-4.0\boot\ncc.exe /from-file:"<temp rsp>"`; the switch lines
below are the rsp contents (one switch per line; abbreviated here with
`R=F:\dev\nemerle_projects\github-nemerle`, `S1=$R\bin\Release\net-4.0\Stage1`,
`GAC=C:\WINDOWS\Microsoft.Net\assembly`, all v4.0_4.0.0.0__b77a5c561934e089).

Important environmental note: because this machine has **no .NET Framework
targeting pack**, RAR emitted warning MSB3644 and resolved
mscorlib/System/System.Core/System.Xml/... **from the GAC** (`GAC_64`/`GAC_MSIL`
paths below) instead of reference assemblies. On a machine with the targeting
pack these would be `%ProgramFiles(x86)%\Reference Assemblies\Microsoft\
Framework\.NETFramework\v4.0\*.dll`. Either way, ncc receives absolute paths.

### Nemerle.dll (37 sources = lib\*.n)
```
/optimize  /no-color  /dowarn:10006  /no-stdlib  /greedy-references:-
/doc:$S1\Nemerle.xml
/define:RUNTIME_MS;NET_4_0
/keyfile:$R\bin\Release\net-4.0\keys\Nemerle.snk
/target:library  /debug-
/project-path:$R\Nemerle.nproj
<lib\*.n : 37 files>
/ref:$GAC\GAC_64\mscorlib\...\mscorlib.dll
/ref:$GAC\GAC_MSIL\System.Core\...\System.Core.dll
/ref:$GAC\GAC_MSIL\System\...\System.dll
/ref:$GAC\GAC_MSIL\System.Xml\...\System.Xml.dll
/out:$R\obj\Release\net-4.0\Stage1\Nemerle.dll
```

### Nemerle.Compiler.dll (89 sources = the ncc\ dirs listed in §2)
```
/optimize  /no-color  /dowarn:10006  /no-stdlib  /greedy-references:-
/doc:$S1\Nemerle.Compiler.xml
/define:RUNTIME_MS;NET_4_0
/keyfile:...\keys\Nemerle.Compiler.snk
/target:library  /debug-
/project-path:$R\Nemerle.Compiler.nproj
<ncc\CompilationOptions.n, ncc\passes.n, ncc\{parsing,codedom,CompilerMessage,
 completion,external,external\ExternalMemberInfo,external\ExternalTypeInfo,
 generation,hierarchy,misc,optimization,typing}\*.n : 89 files>
/ref:mscorlib.dll   /ref:$S1\Nemerle.dll   /ref:System.Core.dll
/ref:System.dll     /ref:System.Xml.dll      (absolute GAC paths as above)
/out:$R\obj\Release\net-4.0\Stage1\Nemerle.Compiler.dll
```

### Nemerle.Macros.dll (37 sources = macros\*.n)
```
/optimize  /no-color  /no-stdlib  /greedy-references:-
/doc:$S1\Nemerle.Macros.xml
/define:RUNTIME_MS;NET_4_0
/keyfile:...\keys\Nemerle.Compiler.snk
/target:library  /debug-
/project-path:$R\Nemerle.Macros.nproj
<macros\*.n : 37 files>
/ref:mscorlib.dll  /ref:$S1\Nemerle.Compiler.dll  /ref:$S1\Nemerle.dll
/ref:System.Core.dll  /ref:System.Data.dll ($GAC\GAC_64)  /ref:System.dll
/ref:System.Windows.Forms.dll  /ref:System.Xml.dll  /ref:System.Xml.Linq.dll
/out:$R\obj\Release\net-4.0\Stage1\Nemerle.Macros.dll
```

### ncc.exe (2 sources)
```
/optimize  /no-color  /no-stdlib  /greedy-references:-
/doc:$S1\ncc.xml
/define:RUNTIME_MS;NET_4_0
/keyfile:...\keys\Nemerle.Compiler.snk
/target:exe  /debug-
/project-path:$R\ncc.nproj
ncc\misc\AssemblyInfo.n
ncc\main.n
/ref:mscorlib.dll  /ref:$S1\Nemerle.Compiler.dll  /ref:$S1\Nemerle.dll
/ref:System.Core.dll  /ref:System.dll
/out:$R\obj\Release\net-4.0\Stage1\ncc.exe
```

### ncc32.exe / ncc64.exe (2 sources each)
Same as ncc.exe plus `/platform:x86` (mscorlib resolved from `GAC_32`) resp.
`/platform:x64`, out `ncc32.exe`/`ncc64.exe` — but **without `/optimize`,
`/doc:` and `/debug-`**: their `Platform` is x86/x64, so neither
`'Release|AnyCPU'` nor `'Debug|AnyCPU'` PropertyGroup in ncc.nproj matches.
A build quirk worth reproducing consciously (or fixing) in the port.

Observations:
- `/checked` never appears (`CheckIntegerOverflow` unset in these projects, so
  the +/- switch is omitted; ncc's internal default is checked=true).
- No `/warn:` (WarningLevel is the default 4), no `/nologo`, no `/resource:` —
  the core four have no resx/embedded resources.
- `/dowarn:10006` only for Nemerle.dll and Nemerle.Compiler.dll.
- `System.Core` reaches even projects that don't list it (implicit v4.0
  `AddlExplicitReference` logic in Microsoft.Common.targets pulls it into RAR).
- ProjectReference outputs arrive as ordinary `/ref:` absolute paths into
  obj-copied bin (Stage1 dir), i.e. references and macro loading during the
  bootstrap never depend on GAC-installed Nemerle.
- Output goes to `obj\...\Stage1\` and Common.targets copies to `bin\...\Stage1\`.

## 8. Test suite hook (regression gate)

- Target `CompilerTests` (NemerleAll.nproj lines 357-388), depends on
  `Linq;Unsafe;_PegAndCSharp;TestFramework;_Wpf`.
- `TestFramework` builds `snippets\Nemerle.Test\Nemerle.Test.Framework` +
  `snippets\Nemerle.Test\Nemerle.Compiler.Test` (+ Nemerle.Diff) against
  `Nemerle=$(NCurBin)` (i.e. the stage under test) into `$(NBin)\TestFramework`.
- CompilerTests copies the runner + its deps (which include the stage's
  Nemerle.dll/Nemerle.Compiler.dll) + helper dlls (Nemerle.Linq, Nemerle.Unsafe,
  CSharpParser, ncc.parser.csharp, Nemerle.Peg, Nemerle.WPF) + everything in
  `testsuite\` into `$(NBin)\Tests\positive` and `...\negative`, then runs:

```
Tests\positive\Nemerle.Compiler.Test.exe -output:. \
    "-p:-nowarn:10003 -def:RUNTIME_MS;NET_4_0" \
    testsuite\positive\*.n *.nnn *.cs      (and same for negative\)
```

- Runner (`snippets\Nemerle.Test\Nemerle.Compiler.Test\Main.n`): by default uses
  **HostedNcc** — compiles each test **in-process** through the
  Nemerle.Compiler.dll sitting next to the exe (ManagerClass API), then executes
  positive tests and diffs their output; `-n/-ncc <path>` switches to
  **ExternalNcc** (spawns a compiler process per test) and `-r/-runtime <exe>`
  runs produced test exes under a given runtime — both switches are exactly what
  a dotnet-hosted port needs (`-ncc "dotnet ncc.dll"`-style wrapper +
  `-runtime dotnet`). Expected errors/warnings are encoded in test-file comments;
  negative suite asserts diagnostics, positive suite asserts runtime output.
- TeamCity/VisualStudio listeners available via `-team-city-test-suite` etc.
