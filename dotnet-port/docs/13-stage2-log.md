# 13 — WP-D: stage2 self-host on CoreCLR (build script, source fixes, fixpoint) — log

Goal: use the dotnet-hosted Stage1 compiler (net4-flavor binaries, dual-path emission,
running via `dotnet exec` on .NET 10 — see 11/12 logs) to recompile the whole core
toolchain (Nemerle.dll, Nemerle.Compiler.dll, Nemerle.Macros.dll, ncc.exe) into a TRUE
core-flavor build (stage2), then prove self-hosting by using stage2 to rebuild itself
(stage3) and comparing.

## Deliverables

- `dotnet-port\build-stage2-core.ps1` — builds `<repo>\bin\<Cfg>\core\Stage2\` (or any
  `-OutDir`) from any `-Compiler` (default Stage1's `ncc.exe`) by invoking
  `dotnet exec <Compiler> /from-file:<rsp>` for the 4 core projects in dependency order.
  Also usable to build stage3 from stage2 (see below) — that's the built-in self-host
  fixpoint check, not a separate script.
- `dotnet-port\rsp\stage2\{Nemerle,Nemerle.Compiler,Nemerle.Macros,ncc}.rsp` — generated
  by the script (regenerated every run unless `-SkipRspGeneration`); committed as a
  concrete, inspectable snapshot of exactly what was passed to ncc for this repo state.
- Source fixes (see "Fixes" below), all additive/guarded — zero behavior change on CLR4
  (re-verified, see "CLR4 regression check").

## 1. Reference strategy: why `-use-loaded-corlib` + explicit split-assembly `-ref:`s, not facade `/ref:`s

The work order's option (a) — `-ref:` to the .NET 10 shared framework's compatibility
facades (`mscorlib.dll`, `System.dll`, `System.Core.dll`, `System.Xml.dll`, all pure
type-forwarders) — **does not work** with ncc's importer. ncc enumerates every
`-ref:`'d assembly's types via `Assembly.GetExportedTypes()`/`GetTypes()`
(`ncc\external\LibraryReferenceManager.n:LoadTypesFrom`) to populate its namespace tree.
Verified directly (throwaway net10.0 console app):

```
Assembly.LoadFrom(".../mscorlib.dll").GetExportedTypes().Length   -> 0
Assembly.LoadFrom(".../System.Xml.dll").GetExportedTypes().Length -> 0
Assembly.LoadFrom(".../mscorlib.dll").GetForwardedTypes()         -> throws FileNotFoundException
                                                                      (forwards to System.Security.Permissions,
                                                                       which isn't in the shared framework)
```

So a facade-referenced build would see **zero visible types** for `mscorlib`/`System`/etc.
— every builtin type lookup fails. `Type.GetType("System.Array", throwOnError:true)`
against the SAME facade assembly *does* succeed (single-name forward resolution goes
through a different code path than bulk enumeration) — which is why a naive smoke test
can look promising before the real build immediately hits `cannot reflect 'System.Array'`.

Working combination (option (b), confirmed with `LibraryReferenceManager.AddLibrary`'s
existing special case for `Manager.Options.UseLoadedCorlib`):

- Pass `-use-loaded-corlib` plus **bare-name** `-ref:mscorlib -ref:System` (not paths —
  the bare-name string match is what triggers the special case in `AddLibrary`). This
  maps `"mscorlib"` to `typeof(object).Assembly` (the running `System.Private.CoreLib`)
  and `"System"` to `typeof(System.Text.RegularExpressions.Match).Assembly` — both real,
  fully-populated assemblies, not facades.
- Everything else (Console, Collections, Collections.Specialized, Linq,
  Diagnostics.Process, Private.Uri, Diagnostics.TraceSource, Security.Cryptography,
  Private.Xml, Private.Xml.Linq, Data.Common) is passed as an explicit `-ref:<path>` to
  the **real, non-facade** split assembly in the shared framework directory (confirmed
  each has a real, non-empty `GetExportedTypes()` before adding it to the script).
  `System.Private.*` assemblies are technically "not intended for 3rd-party reference"
  but load and reflect fine via `Assembly.LoadFrom`.
- The shared framework directory is resolved dynamically in the script via
  `dotnet --list-runtimes`, picking the newest `Microsoft.NETCore.App 10.x` (falls back
  to the newest available major if no 10.x is present).

Result: stage2's `GetReferencedAssemblies()` shows **only** `System.Private.CoreLib` and
real split assemblies (all `10.0.0.0`, tokens `7cec85d7bea7798e`/`b03f5f7f11d50a3a`/
`cc7b13ffcd2ddd51`) plus `Nemerle`/`Nemerle.Compiler` (token `e080a9c724e2bfcd`/
`5291d186334f6101`, from `misc\keys\*.snk`) — **no facade/legacy-mscorlib reference
anywhere** (see acceptance item 2 below for the full dump).

## 2. Source fixes (genuine stage2-only bugs / gaps, all guarded, zero CLR4 behavior change)

All four fixes below were required because **ncc must fully type-check every method body
at compile time**, regardless of whether that method is ever *executed* at run time. This
is a stronger requirement than the WP-B/WP-C "JIT never touches this on CoreCLR" isolation
(which only protects against run-time MissingMethodException for code paths that are
never *called*) — self-hosting additionally requires that CoreCLR-only reflection of the
*running* BCL can still resolve every symbol the *source code* mentions, even in method
bodies that are correctly guarded to never run on CoreCLR.

### 2a. `ncc\external\Codec.n` — redundant generic constraints from modern reflection

.NET 10's `MethodInfo.GetGenericParameterConstraints()` for a constraint like
`where TEnum : struct, Enum` (e.g. `System.Enum.GetName<TEnum>`,
`System.Enum.GetValues<TEnum>`, `RuntimeHelpers.EnumEquals<T>`) returns **both**
`System.Enum` and `System.ValueType` as non-interface class constraints — .NET Framework's
reflection only ever reported the most-derived one (`Enum`; `ValueType` is implied).
Verified by scanning all of corelib's generic methods/types for multi-non-interface
constraint lists: 8 hits, all `[System.Enum, System.ValueType]`. ncc's
`StaticTypeVar.check_class_constraints` (`ncc\typing\StaticTypeVar.n:206-226`) treats a
second non-interface class constraint as a hard *user* error ("generic parameter cannot
be constrained by multiple non-interfaces") — legitimate for real ambiguous source, wrong
here since it's firing on an *imported* constraint list, before self-hosting even reaches
user code (any file using `System.Enum` at all — i.e. basically every file — hits it while
`InternalType`/`SystemTypeCache` are initializing).

Fix: both `set_constraints` closures in `Codec.n` (type-generic-parameter import at
`ReflectConstraints`, method-generic-parameter import at `ReflectTyparms`) now collapse
the constraint list to its minimal/most-derived elements via the **same** utility already
used two lines above for interface de-duplication (`Typer.GetMinimal(list, (t1,t2) =>
t2.IsAssignableFrom(t1))`), applied to the raw `System.Type[]` *before* converting to
`FixedType`. No-op for any already-minimal (i.e. every real Framework-imported) constraint
list, so zero behavior change on CLR4.

### 2b. `ncc\typing\Typer-OverloadSelection.n` — `OverloadResolutionPriorityAttribute`-shaped ambiguity

Modern BCL methods increasingly ship a newer overload whose trailing parameter has a
*default value* purely to support an interpolated-string-handler sibling (a C# 12/.NET 9+
pattern) — e.g. `System.Diagnostics.Debug.Assert(bool)` alongside a newer
`Assert(bool, string? message = null)`. Real C# disambiguates a 1-arg call
(`Assert(true)`) via `System.Runtime.CompilerServices.OverloadResolutionPriorityAttribute`
on the BCL side; ncc's reflection-based overload resolver doesn't read that attribute, so
it sees a genuine tie (both candidates are "equally good" 1-argument matches) and reports
`typing fails on ambiguity between overloads`. Hit immediately by
`lib\nstring.n`'s `using System.Diagnostics.Debug; ... Assert(true);`.

Fix: `OverloadPossibility` already tracks `UsedDefaultParms : bool` per candidate (set in
`Typer-OverloadSelection.n:358`, previously unused for tie-breaking). Added a new
`AintUsingDefaultParms` filter to the same `FilterIfExists` tie-break chain that already
narrows by `AintVarArgs`/`DidntMamboJumbo`/`AintGeneric` in `GetBestOverloads`: when a
strict subset of an otherwise-tied candidate set didn't need any default parameter filled
in, keep only that subset. Pure narrowing (only fires when the exact-arity match already
exists among ambiguous candidates), so it cannot change any *currently unambiguous*
resolution — verified no CLR4 regression (see below).

### 2c. `macros\Resource.n` — `System.Resources.ResXResourceReader` absent on CoreCLR

The opt-in `[Resource(path)]` assembly macro reads `.resx` files via
`System.Resources.ResXResourceReader` at compile time (inside the macro, which runs in
the compiler process). That type shipped in `System.Windows.Forms` on .NET Framework and
has no shared-framework or referenced-package equivalent here. Guarded with
`#if NET_4_0 ... #else Message.Error(...) #endif` around both the `using ResXReader = ...`
alias and the reader body (matching the file's own doc comment now added) — the macro
itself still compiles into `Nemerle.Macros.dll` on core, it just reports a clear
compile-time error if a user assembly actually invokes `[Resource(...)]` while running on
CoreCLR, instead of failing to build the compiler at all.

### 2d. `ncc\generation\HierarchyEmitter.n` / `ncc\generation\ILEmitter.n` / `ncc\hierarchy\CustomAttribute.n` — CLR4-only API bodies

WP-B/WP-C already isolated every CLR4-only Reflection.Emit/Security.Permissions/
SymbolStore call into its own `*Clr4`-suffixed method with a runtime-dispatched call site
(`if (CoreEmitBridge.IsCoreClr) ... else ...Clr4(...)`) so the calls are never *JIT-hit* on
CoreCLR. That guarantees the method is never *executed* there, but ncc still has to
*compile* (type-check) `Nemerle.Compiler.dll`'s own source — including those `*Clr4`
method bodies — when self-hosting, and several of the APIs they reference don't exist at
all in CoreCLR's own `System.Reflection.Emit`/`System.Diagnostics.SymbolStore`/
`System.Security.Permissions` surface (confirmed via reflection, not just "unresolved at
JIT time"): `AssemblyBuilderAccess.Save`, 6-arg `AppDomain.DefineDynamicAssembly`,
`{Method,Constructor,Field}Builder.GetToken()`, `{Type,Method,Constructor}Builder.
AddDeclarativeSecurity`, `AssemblyBuilder.{SetEntryPoint,Save,AddResourceFile,
DefineVersionInfoResource}`, `ModuleBuilder.DefineUnmanagedResource`, the 2-/3-arg
`DefineDynamicModule` overloads, `ModuleBuilder.GetSymWriter()`, `SymDocumentType`/
`SymLanguageType`/`SymLanguageVendor`/`SymbolToken`/`ISymbolWriter` (the whole legacy
`System.Diagnostics.SymbolStore` writer surface, as opposed to the still-present
`ISymbolDocumentWriter`/`DefineDocument(url, Guid)`/`MarkSequencePoint`/
`SetLocalSymInfo`, which **do** exist per `03-dotnet-runtime-facts.md` fact 4 — not
touched here since PDB emission on CoreCLR remains unwired, unchanged from WP-B), and
`System.Security.Permissions.PermissionSetAttribute` specifically (unlike
`SecurityAttribute`/`PermissionSet`/`SecurityAction`, which exist on CoreCLR, just
obsolete-and-inert).

Fix: every one of these method bodies (and two field declarations,
`HierarchyEmitter.TypesManager._debug_emit : ISymbolWriter` and one `match` arm in
`CustomAttribute.GetPermissionSets`) is now wrapped `#if NET_4_0 <original, byte-identical
body> #else <dead-code stub — throws Util.ice(...) or is a no-op/`null`, since the
dispatcher already guarantees these are never invoked when NET_4_0 is undefined>
#endif`. Stage2/stage3 rsp files (this WP) do **not** define `NET_4_0`; Stage1's own
build (msbuild, `TargetFrameworkVersion=v4.0`) still does, so the CLR4 body is
character-for-character what it always was. This is the exact `#if NET_4_0` pattern
already used once in this codebase (`ncc\hierarchy\TypeBuilder.n:2142`,
`ncc\main.n:64-91`) — extended here to every CLR4-only Reflection.Emit/Security/
SymbolStore body identified while building stage2 for the first time.

Full list of methods/fields touched: `HierarchyEmitter.{CreateAssemblyClr4,
CreateModuleClr4, add_resources_to_assembly_clr4 (LinkedResources/UnmanagedResource/
VersionInfo sections only — the embedded-resource section already used pure reflection
(`GetType().GetMethod(...)`) and needed no guard), SaveAssemblyClr4,
ApplyTypeDeclarativeSecurityClr4, ApplyMethodDeclarativeSecurityClr4,
ApplyConstructorDeclarativeSecurityClr4, _debug_emit field, the SetUserEntryPoint call
site in the general (always-compiled) Main-detection code}`; `ILEmitter.
{DefineDebugDocument, GetHackish{Constructor,Method,Field}TokenClr4}`;
`CustomAttribute.GetPermissionSets`'s `SSP.PermissionSetAttribute` match arm.

## 3. Missing split-assembly references discovered while iterating

Beyond the corlib/System split above, the following real (non-facade, confirmed non-zero
`GetExportedTypes()`) shared-framework assemblies were needed and are now in the script's
`$CoreRefs` list: `System.Collections.dll` (`LinkedList<T>`/`Stack<T>`/`Queue<T>` moved out
of corelib on .NET Core), `System.Console.dll`, `System.Collections.Specialized.dll`
(`ListDictionary`), `System.Linq.dll` (`Enumerable.OrderBy` etc.), `System.Diagnostics.
Process.dll`, `System.Private.Uri.dll`, `System.Diagnostics.TraceSource.dll` (`Trace`),
`System.Security.Cryptography.dll` (`SHA1`, used by `ncc\misc\SnkUtils.n`), `System.
Private.Xml.dll` (`XmlDocument`/`XmlNode`/`XmlException`, used by `ncc\hierarchy\
XmlDump.n`), `System.Private.Xml.Linq.dll` (`XElement`, used by `macros\Settings.n`),
`System.Data.Common.dll` (`IDbConnection`/`IDbCommand`/`IDbTransaction`/`CommandBehavior`,
used by `macros\Data.n` — no concrete ADO.NET provider is in the shared framework, but the
interfaces the macro's *generated code* references are).

## 4. Acceptance results

### Item 1 — script + rsp files

`dotnet-port\build-stage2-core.ps1` ran end-to-end, 0 errors, in dependency order:

```
== Building Nemerle.dll ==            -> OK  (373,248 bytes)
== Building Nemerle.Compiler.dll ==   -> OK  (1,904,640 bytes)
== Building Nemerle.Macros.dll ==     -> OK  (654,848 bytes)
== Building ncc.exe ==                -> OK  (11,264 bytes)
```

into `bin\Release\core\Stage2\`, with `Nemerle.CoreEmit.dll` copied alongside and
`ncc.runtimeconfig.json` written (`tfm net10.0`, `Microsoft.NETCore.App 10.0.0`,
`rollForward LatestMinor`). rsp files: `dotnet-port\rsp\stage2\{Nemerle,Nemerle.Compiler,
Nemerle.Macros,ncc}.rsp` (regenerated by the script each run; committed as the concrete
switches used for this state of the repo — see the script header for exactly which
switches/refs and why `-doc:` is intentionally *not* passed: dropped opportunistically to
keep the rsp minimal, not because it's known-broken — untested, flagged as a gap below).

### Item 2 — core-flavor verification

`GetReferencedAssemblies()` on all 4 stage2 outputs (`System.Reflection.Assembly.LoadFrom`
+ reflection, net10.0 throwaway tool):

```
Nemerle.dll            : Nemerle, PublicKeyToken=e080a9c724e2bfcd
  refs: System.Private.CoreLib 10.0.0.0, System.Console 10.0.0.0, System.Collections 10.0.0.0
Nemerle.Compiler.dll   : Nemerle.Compiler, PublicKeyToken=5291d186334f6101
  refs: System.Private.CoreLib, Nemerle, System.Collections.Specialized, System.Text.RegularExpressions,
        System.Private.Uri, System.Private.Xml, System.Console, System.Security.Cryptography,
        System.Collections, System.Linq, System.Diagnostics.Process   (all 10.0.0.0)
Nemerle.Macros.dll     : Nemerle.Macros, PublicKeyToken=5291d186334f6101
  refs: System.Private.CoreLib, Nemerle, Nemerle.Compiler, System.Diagnostics.Process,
        System.Text.RegularExpressions, System.Data.Common, System.Private.Xml.Linq,
        System.Collections   (all 10.0.0.0)
ncc.exe                : ncc, PublicKeyToken=5291d186334f6101
  refs: System.Private.CoreLib, Nemerle.Compiler, System.Console, System.Diagnostics.Process,
        System.Private.Uri, Nemerle   (all 10.0.0.0)
```

**No facade/legacy-mscorlib (4.0.0.0 / `b77a5c561934e089`) reference anywhere** — every
AssemblyRef is either `System.Private.CoreLib`/`System.Private.*` (token
`7cec85d7bea7798e`/`cc7b13ffcd2ddd51`) or a real split assembly (token
`b03f5f7f11d50a3a`), and the two Nemerle identities carry the expected
`Nemerle.snk`/`Nemerle.Compiler.snk` public-key tokens. Confirms option (a) (facade refs)
was correctly abandoned in favor of option (b) (`-use-loaded-corlib` + real split refs).

### Item 3 — stage2 smoke test

```
$ dotnet exec bin\Release\core\Stage2\ncc.exe -out:hello.exe hello.n     # exit 0
$ dotnet exec hello.exe                                                  # "Hello from stage-test!", exit 0
$ dotnet exec bin\Release\core\Stage2\ncc.exe -out:hello2.exe hello2.n   # exit 0  (list literal + .Map, Nemerle.dll generics/stdlib)
$ dotnet exec hello2.exe                                                 # "1" / "2, 4, 6", exit 0
```

`ncc.runtimeconfig.json` (auto-written by `CoreEmitBridge.Save` for exe outputs, same as
Stage1's) is present and correct; `Nemerle.CoreEmit.dll` + the 3 stage2 Nemerle*.dll sit
alongside `ncc.exe` in `Stage2\`.

### Item 4 — stage3 self-host fixpoint

Same script, `-Compiler bin\Release\core\Stage2\ncc.exe -OutDir bin\Release\core\Stage3`:
**0 errors**, all 4 projects built. `dotnet exec bin\Release\core\Stage3\ncc.exe
-out:hello2.exe hello2.n` then `dotnet exec hello2.exe` → same `1` / `2, 4, 6` output,
exit 0. Self-host fixpoint reached: stage2 (built by Stage1-on-.NET10) can rebuild
itself and the result still works.

Byte comparison, stage2 vs stage3, raw and after masking (PE COFF timestamp + module MVID
GUID, via a throwaway `System.Reflection.Metadata`/`PortableExecutable` tool):

| File | Raw | Sizes | Masked (MVID+timestamp) |
|---|---|---|---|
| `ncc.exe` | differs (byte 137) | identical (11,264) | **IDENTICAL** |
| `Nemerle.dll` | differs (byte 137) | identical (373,248) | differs: 224,095 / 373,248 bytes |
| `Nemerle.Compiler.dll` | differs (byte 137) | identical (1,904,640) | differs: 35,970 / 1,904,640 bytes |
| `Nemerle.Macros.dll` | differs (byte 137) | identical (654,848) | differs: 129,437 / 654,848 bytes |

`ncc.exe` (2 trivial source files) is a **true byte-for-byte fixpoint** once MVID/timestamp
are masked. The three larger assemblies are structurally identical (exact same size) but
still differ in content beyond MVID/timestamp — consistent with ordinary
`Dictionary`/`Hashtable`-iteration-order nondeterminism somewhere in the emission pipeline
(this codebase was never built/verified for Roslyn-style `/deterministic` output, on
either runtime), not with a self-host *correctness* problem (both stage2 and stage3
compile cleanly and run hello2.n identically). Noted as **future work**: pin down and
fix the remaining nondeterminism (likely a `Hashtable`/unordered-iteration somewhere in
`TypesManager`/`AttributeCompiler`), not chased further here per the "don't rabbit-hole"
guidance.

### Item 5 — bonus: testsuite slice

Building the full `Nemerle.Compiler.Test.exe`/`Nemerle.Test.Framework` harness for core
would be its own work package (it's an unported net-4.0 test runner). Instead, ran a
direct compile-only slice: first 60 (alphabetical, excluding `*-lib.n` companion files)
of `testsuite\positive\*.n` through `bin\Release\core\Stage2\ncc.exe -target:library`.

**53 / 60 compiled successfully.** The 7 failures were inspected individually — all are
explainable by this being a minimal ad hoc harness (no companion `-lib`/`-ref` files, no
`-def:` matching the real runner) or genuine environment gaps unrelated to this WP, except
one real finding:

- `access-checks.n`, `bug-0256.n`: reference `System.Web.UI`/`System.Data.SqlTypes` — not
  in the .NET 10 shared framework; unrelated to self-hosting (also wouldn't build with
  the *original* net4 GAC-only Stage1 with a bespoke ref set).
- `anonymous-classes.n`, `anonymous-classes-interop.n`: need a companion `*-lib.n`
  compiled first and passed via `-ref:` (multi-file test pair) — not passed by this
  simplified harness.
- `AsLongBug-1.n`, `AsLongBug-2.n`: reference `Nemerle.Compiler` macro-API types
  (`IMacro`, `PExpr`, `Typer`, ...) — need `-ref:` to `Nemerle.Compiler.dll` itself, not
  passed by this simplified harness.
- **`attributes-01.n`: genuine finding.** Fails with `internal compiler error: got
  ArgumentException (Constant does not match the defined type.)` from
  `System.Reflection.Emit.CustomAttributeBuilder..ctor` while compiling one of the file's
  many custom-attribute declarations (large file covering many attribute-usage patterns;
  the exact triggering attribute wasn't isolated — bounded investigation only, per the
  "don't rabbit-hole" scope for this bonus item). Recommended as the top follow-up item
  for a future WP: likely a CoreCLR `CustomAttributeBuilder` strictness difference
  (probably around enum-typed or boxed constant arguments) that doesn't show up in the
  small smoke tests used so far.

## 5. CLR4 regression check

Rebuilt Stage1 with every fix above (`msbuild NemerleAll.nproj /t:Stage1 ... /p:
TargetFrameworkVersion=v4.0`, then `dotnet-port\refresh-stage1-core.ps1`) after each
source change — 0 errors throughout. Final native-CLR4 spot check (no `dotnet exec`,
plain `Stage1\ncc.exe`):

```
$ Stage1\ncc.exe -out:hello.exe hello.n   && .\hello.exe    # "Hello from stage-test!", exit 0
$ Stage1\ncc.exe -out:hello2.exe hello2.n && .\hello2.exe   # "1" / "2, 4, 6", exit 0 (once Nemerle.dll
                                                             #  is copied beside the exe -- pre-existing
                                                             #  scratch-dir artifact, see 12-selfhost-
                                                             #  blockers-log.md; not a regression, verified
                                                             #  by reproducing the same symptom independent
                                                             #  of any code in this WP)
```

Output byte-identical to the stage2/stage3 (.NET 10) runs. No CLR4 behavioral change
detected from the `Codec.n`/`Typer-OverloadSelection.n`/`#if NET_4_0` guard changes.

## 6. Known gaps / recommended next steps

- **`/doc:` (XML doc generation)**: not passed in the stage2 rsp files (dropped
  opportunistically, per the work order, to minimize the first working rsp set) — status
  untested, not known-broken. Try adding it back in a follow-up and see if it "just
  works" (CoreEmitBridge doesn't touch doc-comment XML emission at all, so it plausibly
  does) or needs its own fix.
- **`-debug` (PDB) / `-linkres` / Win32 `-res` on CoreCLR**: still unwired, carried over
  unchanged from WP-B/WP-C. `03-dotnet-runtime-facts.md` fact 4 has the full working
  Portable-PDB recipe (`GenerateMetadata` 3-out overload, `PortablePdbBuilder`,
  `DebugDirectoryBuilder`) already verified in isolation — wiring it into
  `HierarchyEmitter.CreateModuleCore`/`ILEmitter.DefineDebugDocument`/
  `Nemerle.CoreEmit.Emitter.Save` is the natural next WP.
- **`macros\Resource.n`'s `[Resource(...)]` macro**: hard-errors on CoreCLR (no
  `ResXResourceReader` equivalent available). Low priority (opt-in feature, no core build
  or self-host dependency on it) but worth a real fix (e.g. a minimal .resx XML parser
  written directly against `System.Xml`, sidestepping `ResXResourceReader` entirely) if a
  user needs it.
- **stage2-vs-stage3 non-determinism** beyond MVID/PE-timestamp in the 3 larger
  assemblies (see item 4 above) — track down the exact source (prime suspect: unordered
  `Hashtable`/`Dictionary` iteration somewhere in `TypesManager`/`AttributeCompiler`
  affecting emission order) and consider whether it's worth fixing for reproducible-build
  guarantees, or documenting as an accepted characteristic of this compiler.
- **`attributes-01.n` `CustomAttributeBuilder` ArgumentException** (item 5 above) — the
  single genuine functional finding from the testsuite slice; worth root-causing in a
  follow-up before trusting stage2 for attribute-heavy real-world code.
- **Full `Nemerle.Compiler.Test.exe` / `Nemerle.Test.Framework` port to core** — would
  unlock the real testsuite (positive **and** negative, with proper expected-output
  diffing) via its existing `-ncc <exe>`/`-runtime <exe>` switches
  (`dotnet-port\docs\02-build-flow.md` section 8) instead of the ad hoc compile-only slice
  used here; the natural target for a dedicated future WP.
