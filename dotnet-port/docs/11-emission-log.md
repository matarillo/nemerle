# 11 — WP-B: dual-path emission (CLR4 / CoreCLR) — iteration log

Goal: make `ncc`'s emission layer (`ncc\generation\HierarchyEmitter.n`, `ncc\generation\ILEmitter.n`)
work when the Stage1 compiler runs on CoreCLR (.NET 10), while leaving CLR4 (.NET Framework 4)
behavior byte-for-byte identical. Builds on WP-A (`10-metadata-import-log.md`), which got the
importer/typer clean on .NET 10 and left emission failing at `TypesManager.CreateAssembly()`
(`MissingMethodException` for the 6-arg `AppDomain.DefineDynamicAssembly`).

## Architecture implemented

- New project `dotnet-port\Nemerle.CoreEmit\Nemerle.CoreEmit.csproj` (net10.0, no NuGet deps,
  `Emitter.cs`). Carries every CoreCLR-only Reflection.Emit / Reflection.Metadata call:
  `PersistedAssemblyBuilder` construction, the `GenerateMetadata` + `ManagedPEBuilder` save
  recipe (entry point, embedded resources, runtimeconfig.json), and CAPI `.snk` public-key
  parsing (`GetPublicKeyFromSnk`, not wired into the compiler yet — see gaps).
- New file `ncc\generation\CoreEmitBridge.n` (`internal module CoreEmitBridge` in
  `Nemerle.Compiler`, auto-picked-up by the `ncc\generation\*.n` glob in
  `Nemerle.Compiler.nproj`). Single runtime-detection point:
  `IsCoreClr = Type.GetType("System.Reflection.Emit.PersistedAssemblyBuilder, System.Reflection.Emit", false) != null`.
  Lazily `Assembly.LoadFrom`s `Nemerle.CoreEmit.dll` from the directory of the running
  `Nemerle.Compiler.dll` (`Assembly.GetExecutingAssembly().Location`) and caches 4 `MethodInfo`s
  (`CreateBuilder`, `CreateRunBuilder`, `Save`, `GetPublicKeyFromSnk`), all invoked via
  `MethodInfo.Invoke`. This file has zero references to core-only types, so it compiles fine
  under boot-4.0 (only string-based `Type.GetType` lookups at run time).
- `dotnet-port\refresh-stage1-core.ps1`: rebuilds Nemerle.CoreEmit and copies the dll next to
  Stage1's `ncc.exe`; ensures `ncc.runtimeconfig.json` exists (net10.0 / Microsoft.NETCore.App
  10.0.0 / LatestMinor, matching the Phase-0 baseline file).

## Central lesson (confirmed empirically, drove every change)

On CoreCLR, a method fails to JIT-compile — `MissingMethodException` / `TypeLoadException` —
the moment the JIT needs to resolve a *token* it cannot load, **even along a branch never
taken at run time** (local-variable types, or a direct call whose target/return type can't be
resolved). This is not limited to declaring locals (as WP-A found for `PermissionSet`): it also
applies to a bare `typeof(X)` (`ldtoken`) inside a method, and to any call chain where the
callee's return type must be resolved to generate the call (e.g. `Foo().Iter(bar)` needs `Foo`'s
return type fully resolved for the generic `Iter` instantiation).

Fix pattern used everywhere: isolate the CLR4-only/unavailable-API code into a **separate
method with a safe signature** (only common BCL / Nemerle types in its parameters and return
type), and guard the *call site* with `if/unless (CoreEmitBridge.IsCoreClr) ...`. The isolated
method is then only ever JIT-compiled when actually invoked, i.e. never on CoreCLR.

## Changes, by file

### `ncc\generation\HierarchyEmitter.n`

1. **`CreateAssembly`** split into `CreateAssemblyClr4` (verbatim original body: 6-arg
   `AppDomain.DefineDynamicAssembly`, `PermissionSet`/`SecurityAction` locals,
   `GetPermissionSets(assembly_attributes)`) and `CreateAssemblyCore` (`CoreEmitBridge.CreateBuilder`
   for disk output, `CoreEmitBridge.CreateRunBuilder` — `AssemblyBuilderAccess.RunAndCollect` —
   for `-compile-to-memory`). No CAS equivalent exists on CoreCLR (facts doc / plan); assembly-level
   security attributes are simply dropped there (see `is_security_attribute` below).
2. Module creation split into `CreateModuleClr4` (original 2-/3-arg `DefineDynamicModule` +
   `GetSymWriter`, unchanged) and `CreateModuleCore` (1-arg `DefineDynamicModule` — the only
   overload that exists on CoreCLR, per `03-dotnet-runtime-facts.md` fact 1 — plus a one-time
   `Message.Warning` when `-debug` was requested, since PDB emission is not wired in this WP).
3. **`add_resources_to_assembly`** → `add_resources_to_assembly_clr4` (verbatim; contains direct
   calls to `AssemblyBuilder.AddResourceFile` / `ModuleBuilder.DefineUnmanagedResource` /
   `AssemblyBuilder.DefineVersionInfoResource`, all absent on CoreCLR and — critically —
   *unconditionally reachable in the method body*, so the whole method had to move, not just be
   branched around). New `add_resources_to_assembly_core` collects
   `Manager.Options.EmbeddedResources` into `"name|absoluteFilePath"` pairs (consumed by
   `CoreEmitBridge.Save`'s managed-resources parameter); linked resources and Win32 resources are
   warned-and-skipped; version info is silently skipped (no user-visible switch for it).
4. **`SaveAssembly`** split into `SaveAssemblyClr4` (original `SetEntryPoint` + `AssemblyBuilder.Save`
   with the same catch clauses) and `SaveAssemblyCore` (`CoreEmitBridge.Save`, which internally
   does `GenerateMetadata` → `ManagedPEBuilder` → serialize → write `<basename>.runtimeconfig.json`
   when there's an entry point). Entry-point resolution (`_need_entry_point`/`_entry_point` →
   `Message.Error` if missing) is now done once in the shared dispatcher.
5. Three call sites of `GetPermissionSets(...).Iter(...AddDeclarativeSecurity)` — in
   `TypeBuilder.CreateEmitBuilder` (runs for every type), `MethodBuilder.CreateMethodBuilder`
   (every method) and `CreateConstructorBuilder` (every constructor) — wrapped in
   `unless (CoreEmitBridge.IsCoreClr) ApplyTypeDeclarativeSecurityClr4 (...)` /
   `ApplyMethodDeclarativeSecurityClr4 ()` / `ApplyConstructorDeclarativeSecurityClr4 ()`. These
   three methods, not just their bodies, had to be brand new private methods (not inline code
   under an `unless`) — otherwise `CreateEmitBuilder`/`CreateMethodBuilder`/`CreateConstructorBuilder`
   themselves would fail to JIT on CoreCLR for *every* type/method/constructor, not just ones with
   security attributes.

### `ncc\hierarchy\CustomAttribute.n`

- `is_security_attribute` (called from `CompileAttribute`, `GetCompiledAssemblyAttributes`, and
  `GetPermissionSets` — all on the hot attribute-compiling path, exercised for essentially any
  source file with custom attributes) contained a bare `typeof(SSP.SecurityAttribute)` — an
  `ldtoken` that CoreCLR cannot resolve (`System.Security.Permissions` isn't in the shared
  framework). Split into a safe dispatcher (`!CoreEmitBridge.IsCoreClr && is_security_attribute_clr4(ti)`,
  short-circuited) and `is_security_attribute_clr4` (original body, unchanged). `GetPermissionSets`
  itself was left untouched — since none of its (now three) call sites invoke it directly from a
  core-reachable method any more, its `System.Security.Permissions`-typed signature never gets
  JIT-touched on CoreCLR.

### `ncc\generation\ILEmitter.n`

- `GetHackishConstructor`/`GetHackishMethod`/`GetHackishField` ("HACKS FOR MS.NET BUGS") used
  `ctr.GetToken().Token` for `*Builder` instances, `.MetadataToken` otherwise.
  `03-dotnet-runtime-facts.md` fact 6 says `MetadataToken` alone suffices on CoreCLR — true, but
  **naively replacing `GetToken()` with `MetadataToken` unconditionally broke CLR4**: it caused
  `hello2.n` (generics via `list[int]` from `Nemerle.dll`) to compile fine but crash the *produced
  executable* at run time on .NET Framework (see "regression found and fixed" below). Root cause:
  `GetToken()` and `MetadataToken` are not always equivalent for `Save`-mode `AssemblyBuilder`s on
  .NET Framework — that discrepancy is exactly why this code was originally titled "HACKS FOR
  MS.NET BUGS". Fix: kept the *exact* original CLR4 logic, moved verbatim into
  `GetHackish{Constructor,Method,Field}TokenClr4`, and added a safe dispatcher
  (`GetHackish{...}Token`) that uses `MetadataToken` directly on CoreCLR and calls the Clr4 variant
  otherwise — same isolation pattern as everywhere else, so the `GetToken()` calls (missing on
  CoreCLR) never get JIT-touched there.

### CreateType ordering (item 3 in the work order) — no code change needed

`TypesManager.Iter`/`IterConditionally` (`ncc\hierarchy\TypesManager.n:236-286`) already do a
DFS over each `TypeBuilder`'s `iterate_first` (transitive closure of base types + enclosing type,
computed in `construct_subtyping_map`, `ncc\hierarchy\TypeBuilder.n:1355-1367`) before invoking
the passed function — so `EmitImplementation`/`FinalizeType`/`CreateSystemType`
(`type_builder.CreateType()`, `ncc\hierarchy\TypeBuilder.n:1784`) already run bases/enclosing-type
before derived/nested, regardless of source declaration order. Verified empirically (see M1
results below) with base declared *after* derived in source, multi-level inheritance, nested
types, interfaces and a generic type — all matched CLR4 output exactly on .NET 10. The
`AppDomain.TypeResolve` `resolve_hack` (facts doc fact 7: doesn't fire for this scenario on core)
is therefore dead weight for CreateType ordering purposes on CoreCLR but is left in place
unchanged (harmless, `AppDomain.TypeResolve` subscribe/unsubscribe both work on core per the API
inventory).

### Module.FullyQualifiedName (item 4) — no occurrences found

`grep FullyQualifiedName` across `ncc\` found no hits; nothing to change.

## Regression found and fixed during this WP

Naive first pass replaced `GetToken().Token` with `MetadataToken` unconditionally (no CLR4/Core
split) in `ILEmitter.n`. Stage1 built fine and `hello.n` still worked on CLR4, but `hello2.n`
(`def l = [1,2,3]; WriteLine($"$(l.Head)")`, i.e. `Nemerle.Builtins.list[int]` from `Nemerle.dll`)
compiled to an exe that **crashed at run time on .NET Framework** with an unhandled-exception
process exit (`-532462766` / `0xE0434352`). Diagnosed by re-running through `cmd`/bash directly
(PowerShell was swallowing the exception text) — turned out to be layered: first a red herring
(`Nemerle.dll` missing from the scratch run directory, unrelated), then, after fixing that, a
real regression traced to the `GetHackishMethod`/`GetHackishField` token change. Fixed by
splitting into Clr4/Core variants as described above; re-verified both `hello.n` and `hello2.n`
on CLR4 (native) after the fix — both pass. This is the concrete reason every "safe on core"
substitution in this WP was implemented as a **guarded dual path** rather than a blanket
replacement, even when the facts doc says the core-side API "should" behave the same as legacy.

## M1 results (exact commands + outputs)

Build:
```
& "$env:SystemRoot\Microsoft.NET\Framework\v4.0.30319\msbuild.exe" NemerleAll.nproj /target:Stage1 /p:Configuration=Release /verbosity:minimal /p:NTargetName=Build /tv:4.0 /p:TargetFrameworkVersion=v4.0 /nologo
pwsh dotnet-port\refresh-stage1-core.ps1
```
0 errors (only the pre-existing MSB3644/MSB3270 GAC-fallback warnings, plus one expected Nemerle
`N10003` "never referenced" warning for `CoreEmitBridge.GetPublicKeyFromSnk`, which is scaffolding
for a future `-keyfile` work package).

hello.n (`class Hello { public static Main() : void { System.Console.WriteLine("Hello!"); } }`):
```
$ dotnet exec .../Stage1/ncc.exe -out:hello10.exe hello.n     # exit 0
$ dotnet exec hello10.exe                                     # -> Hello!   exit 0
```
`hello10.runtimeconfig.json` written automatically:
```json
{ "runtimeOptions": { "tfm": "net10.0", "framework": { "name": "Microsoft.NETCore.App", "version": "10.0.0" }, "rollForward": "LatestMinor" } }
```

hello2.n (`def l = [1,2,3]; System.Console.WriteLine($"$(l.Head)");`):
```
$ dotnet exec .../Stage1/ncc.exe -out:hello2_10.exe hello2.n   # exit 0
$ dotnet exec hello2_10.exe                                    # -> 1   exit 0
```

`-target:library` (LibTest.Greeter.Greet(string):string):
```
$ dotnet exec .../Stage1/ncc.exe -target:library -out:libtest10.dll libtest.n   # exit 0
```
Verified loadable + invokable via a separate net10.0 console app (`Assembly.LoadFrom` +
reflection): `Greeter.Greet("CoreCLR")` → `"Hello, CoreCLR!"`.

`-resource:res1.txt` (embedded managed resource) on core: compiled, ran (`Hello!`), and the
resource round-tripped (`Assembly.GetManifestResourceNames()` → `res1.txt`, content byte-identical).

`-debug` on core: emits `warning: debug symbols are not yet supported when running the compiler
on CoreCLR; compiling without -debug` and otherwise compiles/runs normally (no crash, no pdb).

CreateType ordering stress test (`hier.n`/`hier2.n`, run on both CLR4 and .NET 10, byte-identical
output both times): 3-level class inheritance (`Puppy : Dog : Animal`) including a base declared
*after* its derived classes in source, an interface implemented by a class two levels down the
hierarchy, a nested type (`Outer.Inner : Animal`), and a generic class (`Container[T]`) — all
resolved/ran correctly on both runtimes.

## CLR4 regression result

All re-verified on native `bin\Release\net-4.0\Stage1\ncc.exe` after the fixes above:
- `hello.n` → `Hello!`, exit 0.
- `hello2.n` → `1`, exit 0.
- `hier.n` / `hier2.n` (inheritance/interfaces/nested/generic stress tests) → identical output to
  .NET 10 run, exit 0.
- `-target:library` → builds `libtest4.dll` (exit 0).
- `-debug` → builds `hellodbg4.exe` + `hellodbg4.pdb`, runs `Hello!` (unchanged from before this WP).
- `-resource:res1.txt` → builds and runs `Hello!` (unchanged).

No behavioral change detected on CLR4 anywhere exercised.

## Remaining known gaps (for the next work package / follow-ups)

- **Debug symbols on CoreCLR**: not wired. `03-dotnet-runtime-facts.md` fact 4 shows the full
  Portable-PDB recipe (`GenerateMetadata` 3-out overload, `PortablePdbBuilder`,
  `DebugDirectoryBuilder`) is implementable — `CoreEmitBridge.Save`/`Nemerle.CoreEmit.Emitter.Save`
  would need a PDB-aware overload, and `HierarchyEmitter.CreateModuleCore` would need to stop
  warning-and-skipping once wired. Left as a TODO per the work order.
- **Win32 resources / linked resources on CoreCLR** (`-res`, `-linkres`): warned and skipped
  (`add_resources_to_assembly_core`). No SRE API exists on core for either; the facts doc's
  fallback (`ResourceSectionBuilder` subclass for Win32, `AddAssemblyFile`+`AddManifestResource`
  for linked) is unimplemented.
- **`-keyfile` / strong-name signing on CoreCLR**: `Nemerle.CoreEmit.Emitter.GetPublicKeyFromSnk`
  (CAPI blob parsing, no `StrongNameKeyPair` dependency) is implemented and unit-verified logic
  (ported from the verified recipe in `03-dotnet-runtime-facts.md`), but **not wired into
  `ncc\hierarchy\CustomAttribute.n`** (`read_keypair`, `AssemblyName.KeyPair` usage at lines
  ~641/814+) or into `CreateAssemblyCore`/`CoreEmitBridge.CreateBuilder`. This is the same
  BLOCKER-SELFHOST item called out in `01-api-inventory.md` (#7) — needed for self-hosting
  Nemerle.dll/Nemerle.Compiler.dll/Nemerle.Macros.dll, which are all strong-named. The next work
  package should: (a) make `CreateAssemblyCore` call `GetPublicKeyFromSnk` + `AssemblyName.SetPublicKey`
  when `Manager.Options.StrongAssemblyKeyName` is set, and (b) replace `read_keypair`'s
  `StrongNameKeyPair` usage and `CustomAttribute.n`'s `AssemblyName.KeyPair` get/set with the
  public-key-only path on core (delay-signed equivalent; CoreCLR doesn't verify strong-name
  signatures anyway, see facts doc fact 5).
- **`LibraryReference.n:118-122` (`GetIsFriend`/`snKey`, `StrongNameKeyPair`)**: also
  BLOCKER-SELFHOST, not touched in this WP (empirically not hit by hello.n/hello2.n, per WP-A's
  and this WP's testing, but WILL be hit compiling Nemerle.Compiler.dll itself since
  `lib\AssemblyInfo.n`/friend-assembly declarations use `InternalsVisibleTo(..., PublicKey=...)`
  between Nemerle.dll and Nemerle.Compiler.dll). Needs the same public-key-blob-parsing treatment
  as `GetPublicKeyFromSnk`, but for verifying a token match rather than producing a key.
- **`ncc\codedom\*`**: still compiled into `Nemerle.Compiler.dll` unconditionally; untouched by
  this WP (M1-safe since ncc.exe itself never calls into it), but per `01-api-inventory.md` will
  block a CoreCLR *self-*build of `Nemerle.Compiler.dll` (needs `System.CodeDom` NuGet package or
  exclusion from the core build).
- **`AssemblyBuilderAccess.RunAndCollect` for `-compile-to-memory` on core**: implemented
  (`CreateAssemblyCore`/`CoreEmitBridge.CreateRunBuilder`) but not exercised by any test in this
  WP (would need the `Nemerle.Compiler.Test`/`HostedNcc` in-process harness, which is its own
  build/porting effort — flagged for the next WP's test-suite work, not chased further here per
  the "stop rather than rabbit-hole" rule).
- **CodeBase/`Assembly.LoadWithPartialName`/GAC-fallback cleanups** (`01-api-inventory.md` §2,
  §10 LATER items): unrelated to emission, intentionally out of scope.
- `CoreEmitBridge.GetPublicKeyFromSnk`/`Nemerle.CoreEmit.Emitter.GetPublicKeyFromSnk` are
  currently dead code from ncc's point of view (only reachable once the `-keyfile` wiring above is
  done) — hence the `N10003` "never referenced" warning during the Stage1 build; expected and
  harmless.

## Files changed

- `dotnet-port\Nemerle.CoreEmit\Nemerle.CoreEmit.csproj` (new)
- `dotnet-port\Nemerle.CoreEmit\Emitter.cs` (new)
- `dotnet-port\refresh-stage1-core.ps1` (new)
- `ncc\generation\CoreEmitBridge.n` (new)
- `ncc\generation\HierarchyEmitter.n` (dual-path `CreateAssembly`/module creation/
  `add_resources_to_assembly`/`SaveAssembly`; declarative-security call sites isolated)
- `ncc\generation\ILEmitter.n` (`GetHackish{Constructor,Method,Field}` token lookup, dual-path)
- `ncc\hierarchy\CustomAttribute.n` (`is_security_attribute` dual-path)
