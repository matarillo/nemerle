# 01 — .NET-Framework-only API inventory (ncc/, lib/, macros/)

Scope: sources building the core toolchain — `ncc/` (→ Nemerle.Compiler.dll + ncc.exe), `lib/` (→ Nemerle.dll), `macros/` (→ Nemerle.Macros.dll).
Excluded: `snippets/`, `VsIntegration/`, `tools/`, `testsuite/`.

Classification:
- **BLOCKER-M1** — blocks: ncc running on .NET 10 compiling hello-world to runnable output.
- **BLOCKER-SELFHOST** — blocks: recompiling Nemerle.dll / Nemerle.Macros.dll / the compiler itself on .NET 10.
- **LATER** — full-port polish (obsolete APIs, dead Framework paths, behavioral drift).
- **OK** — works on .NET 10 as-is (verified against .NET Core API surface).

Known context (not re-derived here): net4-built ncc.exe already runs under `dotnet exec`; metadata importer patched (`ncc\external\ExternalTypeInfo\ExternalTypeInfo.n` skips ref-return/byref-like members). The emission work package (DefineDynamicAssembly 6-arg, ILEmitter token hacks, AssemblyBuilder.Save, GetSymWriter, GetPermissionSets) is owned elsewhere but LISTED in §3 for completeness.

---

## 1. AppDomain usage

Complete list of AppDomain hits in ncc/lib/macros (there are only 6):

| File:Line | Snippet | Verdict |
|---|---|---|
| `ncc\generation\HierarchyEmitter.n:74` | `System.AppDomain.CurrentDomain.TypeResolve += resolve_hack;` | **OK** |
| `ncc\generation\HierarchyEmitter.n:80` | `... TypeResolve -= resolve_hack;` (in `Dispose`) | **OK** |
| `ncc\generation\HierarchyEmitter.n:117` | `System.AppDomain.CurrentDomain.DefineDynamicAssembly (...)` 6-arg | **BLOCKER-M1** (known emission package, §3) |
| `ncc\external\LibraryReferenceManager.n:356/368` | `AppDomain.CurrentDomain.AssemblyResolve += / -= OnAssemblyResolve;` | **OK** |
| `ncc\passes.n:594` | `def parsersDirectory = AppDomain.CurrentDomain.BaseDirectory;` (scan for `ncc.parser.*.dll`) | **OK** |

Key answer requested: **`AppDomain.TypeResolve` DOES exist and fires on .NET Core / .NET 10.** `AppDomain` survives as a shim over the single default domain; `TypeResolve`, `AssemblyResolve`, `BaseDirectory`, `CurrentDomain` are all functional. The `resolve_hack` (returns `_assembly_builder` so incomplete TypeBuilders resolve during `CreateType`) will keep working with a runtime `AssemblyBuilder`; whether it is still needed with `PersistedAssemblyBuilder` is for the emission package to decide.

No usage anywhere of `AppDomain.CreateDomain`, `Unload`, `CreateInstance`, `DoCallBack`, domain evidence, or `ReflectionOnlyAssemblyResolve`. AppDomain surface is therefore NOT a porting problem outside the one `DefineDynamicAssembly` call.

## 2. Assembly loading, probing, macro loading (`ncc\external\LibraryReferenceManager.n`)

How references are located: `_lib_path` (ctor, lines 62–78) = dir(Nemerle.dll) :: `Environment.CurrentDirectory` :: dir(System.Text.RegularExpressions assembly) :: dir(Nemerle.Compiler.dll) :: dir(System.Private.CoreLib) :: user `-lib` paths. `LookupAssembly` (248–309) probes those dirs for `<name>.dll`, uses `Assembly.LoadFrom`; names containing `,` go through `Assembly.Load(fullName)`; final fallback is `Assembly.LoadWithPartialName` (GAC).

| File:Line | Snippet | Verdict | Notes |
|---|---|---|---|
| `LibraryReferenceManager.n:224-225` | `SR.Assembly.Load (name)` | **OK** | Loads into default ALC. |
| `LibraryReferenceManager.n:229` | `SR.Assembly.LoadFrom (path)` | **OK** | Works on core; no unload (compiler never unloads anyway; `CompilationOptions.n:65` "do not unload external libraries"). |
| `LibraryReferenceManager.n:303` | `Assembly.LoadWithPartialName(name);` | **LATER** | Exists on core (obsolete), but there is no GAC — effectively returns null. Net effect: every reference must be reachable via `-lib`/cwd/compiler dir. Also note existing bug: the result is not returned (statement value dropped → returns implicit value) — path is dead-ish. |
| `LibraryReferenceManager.n:236-244` | `System.Uri(assembly.CodeBase).LocalPath` (also `:342`, `:589`, and for `AssemblyName` at `:243`) | **LATER** | `Assembly.CodeBase` is obsolete (SYSLIB0012) but still returns a Location-derived URI on .NET 10 (throws only in single-file publish). **`AssemblyName.CodeBase` (line 241-244) is null on core** → `Uri(null)` throws; that overload is reached from the `GreedyReferences` path (line 144). Replace all with `Assembly.Location`. |
| `LibraryReferenceManager.n:207-218` | mono `mono/gac` path rewriting in `DirectoryOfCodebase` | **OK** (dead on core, harmless) |
| `LibraryReferenceManager.n:90-96` | `\| "mscorlib" when Manager.Options.UseLoadedCorlib => typeof (System.Object).Assembly` | **OK** | With `-use-loaded-corlib` maps "mscorlib" → System.Private.CoreLib and "System" → the S.T.RegularExpressions assembly. Default is **false** (`CompilationOptions.n:136`), so default path probes `_lib_path` for `mscorlib.dll` — found because the shared-framework dir (in `_lib_path`) ships the mscorlib.dll facade. Fragile but working; recommend forcing `UseLoadedCorlib=true` on core. |
| `LibraryReferenceManager.n:311-349` | `load_macro`: `lib.GetType(name)`, `GetConstructor(EmptyTypes)`, `ctor.Invoke(null)`, cast to `IMacro` | **OK** | Plain reflection instantiation; macro assemblies execute in-process in the default ALC. IMacro-identity mismatch diagnostics (332-346) still valid. |
| `LibraryReferenceManager.n:352-370` | `AssemblyResolve` hook during `LoadContents` | **OK** | |
| `LibraryReferenceManager.n:384-399` | `LoadPluginsFrom`: `LookupAssembly(name)` then `assemblyLoad(name + strongPart)` | **OK** | strongPart fallback = `Assembly.Load` by display name incl. `PublicKeyToken=5291d186334f6101` (`ncc\passes.n:640-645`); on core this resolves from app base dir by simple name (PKT not enforced) — works when Nemerle.Macros.dll sits next to ncc. |
| `ncc\passes.n:571-574` | `LibrariesManager.AddLibrary("mscorlib", true); ... "System" ... "Nemerle" ... "System.Xml"` | **OK** | "System"/"System.Xml" resolve to shared-framework facades on .NET 10. See §8 for the hardcoded-name inventory. |
| `ncc\main.n:100,164` | `Uri(Assembly.GetExecutingAssembly().CodeBase).LocalPath` (respawn dir + default LibraryPaths) | **LATER** | Replace with `Assembly.Location` / `AppContext.BaseDirectory`. Works today under `dotnet exec`. |

No `ReflectionOnlyLoad`, no `Assembly.LoadFile`, no `RuntimeEnvironment.GetRuntimeDirectory`, no registry-based framework probing anywhere in ncc/lib/macros. `Environment.GetEnvironmentVariable` appears only in `ncc\main.n:69-70` (PROCESSOR_ARCHITECTURE, OK) and `macros\ExpandEnv.n:134` / `macros\GeneratedAssemblyVersion.n:118` (OK).

## 3. Emission pipeline (KNOWN work package — listed for completeness only)

All in `ncc\generation\` unless noted. These are the items owned by the other work package:

| File:Line | Snippet | Verdict |
|---|---|---|
| `HierarchyEmitter.n:117-120` | `AppDomain.CurrentDomain.DefineDynamicAssembly(_assembly_name, access, dir, required, optional, refused)` | **BLOCKER-M1** — overload gone; core only has `AssemblyBuilder.DefineDynamicAssembly(name, access[, attrs])`; persisted output needs .NET 9+ `PersistedAssemblyBuilder`. |
| `HierarchyEmitter.n:131-135` | `DefineDynamicModule(name, fileName, emitSymbolInfo)` | **BLOCKER-M1** — core: single-module, `DefineDynamicModule(name)` only. |
| `HierarchyEmitter.n:137,469` | `_debug_emit = _module_builder.GetSymWriter()` / `ISymbolWriter` | **BLOCKER-M1 (with `-debug`)** — `GetSymWriter`/`ISymbolWriter` don't exist on core; `PersistedAssemblyBuilder` PDB flow is MetadataBuilder-based. |
| `ILEmitter.n:170` | `_module_builder.DefineDocument(...)`; `ILEmitter.n:1751,2581-2614` `_ilg.MarkSequencePoint(...)` | **BLOCKER-M1 (with `-debug`)** — both absent on core. (`using System.Diagnostics.SymbolStore` also in `ncc\optimization\Unification.n:40`, `Propagator.n:43` — unused imports, trim when recompiling.) |
| `ILEmitter.n:242,259,276` | `ctr.GetToken().Token` / `meth.GetToken().Token` / `fld.GetToken().Token` ("HACKS FOR MS.NET BUGS", lines 238-289) | **BLOCKER-M1** — `*Builder.GetToken()` removed on core; `MetadataToken` property exists instead. |
| `HierarchyEmitter.n:309` | `_assembly_builder.Save(fileName, portableExecutableKind, imageFileMachine)` | **BLOCKER-M1** — no Save on core; PersistedAssemblyBuilder + PEBuilder. |
| `HierarchyEmitter.n:295-299` | `_assembly_builder.SetEntryPoint(mi, PEFileKinds...)` | **BLOCKER-M1** (for .exe) — absent on core; entry point set via PEHeaderBuilder/MetadataBuilder. |
| `HierarchyEmitter.n:102-120` + `ncc\hierarchy\CustomAttribute.n:164-188` | `GetPermissionSets` → `System.Security.PermissionSet`, `SSP.SecurityAction`, `SSP.PermissionSetAttribute.CreatePermissionSet()` | **BLOCKER-M1** — CAS types are not in the .NET 10 shared framework; `CreateAssembly` declares `PermissionSet` locals, so the method fails to JIT even for hello-world. Needs System.Security.Permissions shim package or (better) deletion. |
| `HierarchyEmitter.n:582,1048,1109` | `type_builder.AddDeclarativeSecurity(...)`, `MethodBuilder/ConstructorBuilder.AddDeclarativeSecurity` | **BLOCKER-M1** (same package) — API removed on core; only reached when security attrs present, but the containing methods reference the types. |
| `HierarchyEmitter.n:162-188` | reflection hack for `ModuleBuilder.DefineManifestResource` / mono `AssemblyBuilder.EmbedResource` (+ `InternalTypes.n:222-225` `AssemblyBuilder_EmbedResourceFile` lookup) | **BLOCKER-SELFHOST** — neither method exists on core → "cannot find API for saving resources". PersistedAssemblyBuilder handles resources via MetadataBuilder. Hello-world doesn't embed resources → not M1. |
| `HierarchyEmitter.n:202` | `_assembly_builder.AddResourceFile(name, file)` (linked resources) | **LATER** — absent on core; only with `-linkres`. |
| `HierarchyEmitter.n:215` | `_module_builder.DefineUnmanagedResource(uresource)` | **LATER** — absent on core; only with `-res` (win32 resource). |
| `HierarchyEmitter.n:226` | `_assembly_builder.DefineVersionInfoResource()` | **BLOCKER-M1** — absent on core AND on the default (no `-res`) path for every compile that saves to disk. Part of the Save package. |
| `ILEmitter.n:2243` | `SystemTypeCache.RuntimeHelpers_get_OffsetToStringData` (init at `InternalTypes.n:216`) | **OK** — `RuntimeHelpers.OffsetToStringData` still exists on .NET 10 (obsolete SYSLIB0054); revisit LATER. |

## 4. Strong naming / security / crypto

| File:Line | Snippet | Verdict | Notes |
|---|---|---|---|
| `ncc\hierarchy\CustomAttribute.n:627-638` | `read_keypair`: `SR.StrongNameKeyPair(File.Open(name, ...))` | **BLOCKER-SELFHOST** | `StrongNameKeyPair` ctors throw `PlatformNotSupportedException` on core. Reached whenever `-keyfile` (`Options.StrongAssemblyKeyName`, line 793-795) or `[assembly: AssemblyKeyFile]` is used — the Nemerle build signs Nemerle.dll/Macros/Compiler (PKT 5291d186334f6101). Hello-world unsigned → not M1. Core route: put public key into `AssemblyName.SetPublicKey` (compile-time full signing needs a new mechanism or delay-sign+external signer). |
| `ncc\hierarchy\CustomAttribute.n:794,814,817` | `an.KeyPair = read_keypair(...)`; `if (an.KeyPair != null)` | **BLOCKER-SELFHOST** | `AssemblyName.KeyPair` getter/setter throw PNSE on core. Note line 814 READS `KeyPair` whenever an `AssemblyKeyFileAttribute` is seen, even if `-keyfile` wasn't passed. |
| `ncc\hierarchy\CustomAttribute.n:791` | `an.CodeBase = string.Concat("file:///", Directory.GetCurrentDirectory());` | **LATER** | `AssemblyName.CodeBase` setter exists on core (obsolete, ignored). Delete. |
| `ncc\external\LibraryReference.n:118-122` | `snKey()`: `SR.StrongNameKeyPair(keyFile)` in `GetIsFriend` | **BLOCKER-SELFHOST** | Runs when a REFERENCED assembly carries `InternalsVisibleTo("<current>, PublicKey=...")` — exactly the Nemerle.dll ↔ Nemerle.Compiler.dll self-host scenario. Throws PNSE on core. Replace with parsing the key file bytes directly (`AssemblyName.GetPublicKey` of self, or read .snk format). |
| `ncc\external\LibraryReference.n:130` | `System.Security.Cryptography.SHA1Managed()` | **OK** | Exists on core (obsolete SYSLIB0021); swap to `SHA1.Create()` LATER. |
| `lib\AssemblyInfo.n:47` | `[assembly: System.Security.AllowPartiallyTrustedCallers]` | **OK** | Attribute exists on .NET 10 (inert). Remove LATER. |
| `ncc\hierarchy\TypeBuilder.n:2142-2144` | quotation emits `[System.Security.SecurityCritical]` under `#if NET_4_0` | **OK** | Attribute exists on core (inert). The `#if NET_4_0` governs the compiler's own build defines — decide define set for the core build. |

## 5. System.CodeDom (`ncc\codedom\`)

Files: `NemerleCodeProvider.n`, `NemerleCodeGenerator.n`, `NemerleCodeCompiler.n`, `NemerleMemberAttributeConverter.n`.

**They ARE compiled into Nemerle.Compiler.dll**: `Nemerle.Compiler.nproj:74` (`<Compile Include="ncc\codedom\*.n">`) and `ncc\ncc.build:46` (`<include name="codedom\*.n" />`). No other core project includes them (the VsIntegration CodeDom is separate and out of scope).

- **BLOCKER-SELFHOST**: recompiling Nemerle.Compiler.dll as-is on .NET 10 needs `System.CodeDom` (NuGet) — `CodeDomProvider`, `CodeCompiler`, `ICodeGenerator` etc. are not in the shared framework — plus `System.Configuration` (`NemerleCodeCompiler.n:39`, appears to be an unused using; `System.Configuration.ConfigurationManager` package or just delete the using). `NemerleCodeCompiler` shells out to an external ncc process (mono-inherited CSharpCodeCompiler port), so at runtime it would *function* with the package, but `CodeDomProvider.CompileAssemblyFrom*` conventions are PNSE-flavored on core.
- **Recommendation**: exclude `ncc\codedom\*.n` from the .NET 10 build of Nemerle.Compiler.dll (nothing inside ncc/ calls it — it is a public service for hosts). Then this whole section disappears.
- **OK for M1**: ncc.exe never touches these types when compiling.

## 6. lib/ (Nemerle.dll sources — recompiled BY ncc during self-host)

Nothing here blocks running the compiler; these matter when ncc recompiles Nemerle.dll targeting .NET 10 and when Nemerle.dll runs on .NET 10.

| File:Line | Snippet | Verdict | Notes |
|---|---|---|---|
| `lib\core.n:34,49,55,66`, `lib\list.n:48-49`, `lib\option.n:39-40`, `lib\hashtable.n:42`, `lib\HashSetEx.n:12`, `lib\internal-numbered.n` (19 sites) | `[System.Serializable]` / `[Nemerle.MarkOptions (System.Serializable)]` | **OK** | `SerializableAttribute` exists on .NET 10 (BinaryFormatter is gone, attribute is inert). Consider removing LATER. |
| `lib\hashtable.n:103` | `protected this (info : SerializationInfo, context : StreamingContext)` calling base `Dictionary` serialization ctor | **LATER** (watch: can become BLOCKER-SELFHOST) | Base `Dictionary(SerializationInfo, StreamingContext)` ctor still exists on .NET 10 but is `[Obsolete(SYSLIB0051)]` — warning-level, so expected to compile with a warning. Simplest fix: delete the ctor. |
| `lib\list.n:49` + compiler codegen `ncc\hierarchy\TypeBuilder.n:2137-2155` | Serializable constant variant options get `IObjectReference.GetRealObject` (`InternalType.IObjectReference`, `InternalTypes.n:652`) | **OK** / **LATER** | `IObjectReference` + `StreamingContext` exist on .NET 10 (obsolete SYSLIB0050); type lookup at compiler startup succeeds (forwarded via mscorlib facade). Recompiling lib will emit obsolete warnings. |
| `lib\LazyValue.n:116,124` | `System.Threading.Thread(()=>val=f())` | **OK** | |
| `lib\concurrency.n:70-90` | `Monitor.Enter/Exit/Wait/Pulse` | **OK** | No `Thread.Abort/Suspend/Resume` anywhere in lib/ (or ncc/, macros/). |
| `lib\AssemblyInfo.n:47` | `AllowPartiallyTrustedCallers` | **OK** (inert) | |
| `lib\getopt.n`, `lib\input.n`, `lib\PipeReader.n`, `lib\PipeWriter.n`, `lib\Diagnostics.n` | Console/IO/System.Diagnostics | **OK** | Nothing Framework-only found (no PerformanceCounter/EventLog). |

No AppDomain, no Remoting, no BinaryFormatter, no registry, no `Encoding.Default` anywhere in lib/.

## 7. macros/ (Nemerle.Macros.dll sources)

Macros run inside the compiler process, so anything here executes on .NET 10 during compilation; the *generated* code additionally has to compile/run against the target framework.

| File:Line | Snippet | Verdict | Notes |
|---|---|---|---|
| `macros\Data.n:33` | `using System.Data;` + `connection.Open()` at compile time (`ConfigureConnection` macro) | **LATER** (verify at selfhost) | `System.Data` facade / `System.Data.Common` in shared framework covers `IDbConnection` etc., so Nemerle.Macros.dll should still compile. Runtime DB providers (e.g. `System.Data.SqlClient`) are NuGet-only — feature degrades, does not block. |
| `macros\Settings.n:56-114` | quotations generating `System.Configuration.ApplicationSettingsBase`, `UserScopedSettingAttribute`, ... | **OK** to compile (quotations are untyped PExpr — no assembly reference needed); **LATER** for consumers (target project needs System.Configuration.ConfigurationManager package). |
| `macros\xml.n:29-30` | `using System.Xml; using System.Xml.Serialization;` | **OK** | Both in shared framework. |
| `macros\GeneratedAssemblyVersion.n:118`, `macros\AssemblyVersionFromSVN.n:105,125` | `Process` spawning git/svn, `Environment.GetEnvironmentVariable("GIT_PATH")` | **OK** | |
| `macros\concurrency.n:31` | `using System.Threading;` (chords via Monitor) | **OK** | |
| `macros\assertions.n:365` | `Thread.CurrentThread.ManagedThreadId` | **OK** | |
| `macros\core.n:470-472` | `lock` macro expands to `Monitor.Enter/Exit` | **OK** | Pre-4.0 pattern (no `Monitor.Enter(obj, ref bool)`); modernize LATER. |
| `macros\ExpandEnv.n:134` | `Environment.GetEnvironmentVariable(var)` | **OK** | |

No CodeDom, no AppDomain, no registry, no Remoting in macros/.

## 8. Hardcoded mscorlib / assembly-qualified type-name strings (category 6)

Exhaustive list of hits for `mscorlib` and string-based type resolution in ncc/lib/macros:

| File:Line | Snippet | Verdict |
|---|---|---|
| `ncc\passes.n:571` | `LibrariesManager.AddLibrary("mscorlib", true);` | **OK** (resolves via shared-framework facade file or `-use-loaded-corlib`; see §2) |
| `ncc\CompilationOptions.n:566` | `-use-loaded-corlib` help text | **OK** (docs only) |
| `ncc\external\LibraryReferenceManager.n:90` | `"mscorlib" when ...UseLoadedCorlib => typeof(System.Object).Assembly` | **OK** |
| `ncc\generation\ILEmitter.n:2243` | `[mscorlib]System...` in a comment | **OK** (comment) |
| `lib\internal.n:54` | comment referencing MS mscorlib.dll | **OK** (comment) |
| `ncc\passes.n:643` | `", Version=$version, Culture=neutral, PublicKeyToken=5291d186334f6101"` fallback name for Nemerle.Macros | **OK** on core (only used if plain lookup fails); **LATER** delete — strong-name pinning is meaningless on core. |
| `ncc\external\ExternalTypeInfo\ExternalTypeInfo.n:624` | `System.Type.GetType("System.Runtime.CompilerServices.IsByRefLikeAttribute", false)` | **OK** (part of the existing core patch; not assembly-qualified, resolves in corlib) |
| `ncc\main.n:155-156` | `typeof(object).Assembly.GetType("System.RuntimeType") != null` (`needs_bigger_stack`) | **OK** — `System.RuntimeType` exists in System.Private.CoreLib → returns true → compiler runs on a big-stack thread (works on core). |
| `ncc\external\InternalTypes.n:170-201,603-665` | `Reflect("System.Array")` etc. / `lookup("System.Boolean")` etc. | **OK** — resolved via NameTree from loaded reference metadata, not `Type.GetType`; all listed types exist on .NET 10 (incl. `System.Runtime.CompilerServices.IsVolatile`, `System.Reflection.Missing`, `System.Runtime.Serialization.IObjectReference`, `System.Reflection.AssemblyKeyFileAttribute`). A missing one would `Util.ice` at startup — none is missing. |
| `ncc\external\InternalTypes.n:222-225` | `Reflect("System.Reflection.Emit.AssemblyBuilder").GetMethod("EmbedResourceFile", ...)` | **OK at startup** (type resolves via facade; the mono-only method legitimately returns null and is null-checked). Interacts with the §3 resources item. |

There are no `"..., mscorlib"`-style assembly-qualified type strings anywhere in ncc/lib/macros.

## 9. Miscellaneous (console, culture, encoding, process, threading)

| File:Line | Snippet | Verdict | Notes |
|---|---|---|---|
| `ncc\main.n:56` | `Environment.OSVersion.Platform :> int` | **OK** | |
| `ncc\main.n:98-139` | x86/x64 respawn: `Process.Start` of sibling `ncc32.exe`/`ncc64.exe` (or `mono`) | **LATER** | Only triggered by explicit `-platform:x86/x64` arch mismatch. On .NET 10 the arch-specific-exe model is wrong (single ncc under `dotnet`). Default hello-world (`Platform == ""`) never enters this path. |
| `ncc\main.n:140-148` | `Threading.Thread(main_with_catching, stack_kilos * 1024)` big-stack thread | **OK** | Thread maxStackSize honored on core. Self-host NEEDS this (deep recursion in typer). |
| `ncc\parsing\Source.n:90` | `StreamReader(file, UTF8Encoding(true, true))` | **OK** | Explicit UTF-8 + BOM detect; `Encoding.Default` used nowhere in the three dirs. |
| `ncc\parsing\Lexer.n:1243+` | `Char.ToLower(..., CultureInfo.InvariantCulture)` | **OK** | Invariant culture used consistently; no culture pitfalls found. `ncc\main.n:127` `StringComparison.InvariantCultureIgnoreCase` — OK. |
| `ncc\passes.n:459-495` | `ProgressBar` console writes | **OK** | |
| `ncc\hierarchy\XmlDump.n:63`, `ncc\passes.n:721` | `XmlDocument.Save` for `-doc` output | **OK** | System.Xml (System.Private.Xml) in shared framework. |
| `ncc\hierarchy\TypesManager.n:419` | `WriteAllText(path, code, Text.Encoding.UTF8)` | **OK** | |
| `ncc\hierarchy\CustomAttribute.n:820` | `an.CultureInfo = CultureInfo(take_string(parms))` | **OK** | |
| `ncc\passes.n:52` | `[System.Serializable, Record]` on a compiler type | **OK** (inert) | |

Also checked and NOT present anywhere in the three dirs: `BinaryFormatter`, `System.Runtime.Remoting`, `MarshalByRefObject`, `Microsoft.Win32` / registry, `Thread.Abort/Suspend/Resume`, `ReaderWriterLock`, `Console.InputEncoding/OutputEncoding`, `RuntimeEnvironment.GetRuntimeDirectory`, `EnterpriseServices`, `System.Web`, WinForms/Drawing.

## 10. Summary

### Counts by verdict

| Verdict | Items | Where |
|---|---|---|
| BLOCKER-M1 | 8 (all in the known emission package) | DefineDynamicAssembly 6-arg; DefineDynamicModule(file); AssemblyBuilder.Save; SetEntryPoint; DefineVersionInfoResource; GetPermissionSets/CAS types (fails JIT of CreateAssembly); GetToken hacks; symbol writer (`-debug` only) |
| BLOCKER-SELFHOST | 5 | StrongNameKeyPair in read_keypair; AssemblyName.KeyPair; StrongNameKeyPair in LibraryReference.GetIsFriend (IVT+PublicKey check on referenced Nemerle assemblies); manifest-resource embedding; ncc\codedom needs System.CodeDom + System.Configuration (or exclusion from build) |
| LATER | ~10 | Assembly/AssemblyName.CodeBase (5 sites, incl. a real null-crash in `-greedy`); LoadWithPartialName GAC fallback; ncc32/ncc64 respawn; PKT-pinned Nemerle.Macros load string; AddResourceFile / DefineUnmanagedResource; hashtable serialization ctor (SYSLIB0051); SHA1Managed; lock-macro pattern; Data.n/Settings.n ecosystem deps |
| OK | everything else | AppDomain TypeResolve/AssemblyResolve/BaseDirectory all work on core; Assembly.Load/LoadFrom; macro loading and in-process execution; InternalTypes/SystemTypeCache startup lookups; Serializable attributes; lib/ and macros/ almost entirely clean |

### Top-10 riskiest items

1. `ncc\generation\HierarchyEmitter.n:117` — `AppDomain.DefineDynamicAssembly` 6-arg → PersistedAssemblyBuilder rewrite (M1, known pkg).
2. `ncc\generation\HierarchyEmitter.n:102-120` + `ncc\hierarchy\CustomAttribute.n:164-188` — CAS `PermissionSet` locals mean `CreateAssembly` fails to JIT even for hello-world with no security attributes; must be excised, not merely gated (M1, known pkg).
3. `ncc\generation\HierarchyEmitter.n:309/295/226` — `Save` + `SetEntryPoint` + unconditional `DefineVersionInfoResource()` on the default save path (M1, known pkg).
4. `ncc\generation\ILEmitter.n:242,259,276` — `*Builder.GetToken()` hacks; use `MetadataToken` (M1, known pkg).
5. `ncc\generation\HierarchyEmitter.n:137` + `ncc\generation\ILEmitter.n:170,1751` — GetSymWriter/DefineDocument/MarkSequencePoint; Nemerle's own build uses `-debug`, so this leaks into SELFHOST too (known pkg).
6. `ncc\external\LibraryReference.n:118-122` — `StrongNameKeyPair` inside `GetIsFriend`, executed while READING references that declare `InternalsVisibleTo(..., PublicKey=...)` — the Nemerle assemblies do exactly this, so self-host trips it immediately (SELFHOST, this package).
7. `ncc\hierarchy\CustomAttribute.n:627-638,794,814` — `read_keypair`/`AssemblyName.KeyPair` PNSE; strong-named Nemerle build (SELFHOST).
8. `ncc\codedom\*` — compiled into Nemerle.Compiler.dll (`Nemerle.Compiler.nproj:74`, `ncc\ncc.build:46`); exclude from the core build or add System.CodeDom + System.Configuration.ConfigurationManager references (SELFHOST).
9. `ncc\generation\HierarchyEmitter.n:162-188` + `ncc\external\InternalTypes.n:222` — manifest-resource embedding relies on reflection over Framework/mono-only methods; on core it degrades to "cannot find API for saving resources" (SELFHOST if the Nemerle build embeds resources, else LATER).
10. `ncc\external\LibraryReferenceManager.n:236-244,303` — CodeBase-based location (incl. `AssemblyName.CodeBase` → `Uri(null)` crash in the `-greedy` path) and the dead `LoadWithPartialName` GAC fallback; replace with `Assembly.Location` + explicit probing (LATER, but on every load path).

Cross-cutting note: the compiler resolves "mscorlib"/"System"/"System.Xml" today by file-probing the shared-framework directory for the facade DLLs it finds via `typeof(...)` anchor assemblies (§2) — this works but is fragile; the core-native answer is to default `UseLoadedCorlib` to true under .NET and/or pass a reference-assembly set via `-lib`.
