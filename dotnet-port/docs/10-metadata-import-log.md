# WP-A: Metadata importer tolerance for modern BCL shapes (.NET 10)

Goal: `dotnet exec bin\Release\net-4.0\Stage1\ncc.exe -out:hello.exe hello.n` must get past
metadata loading/typing on .NET 10. Emission failures are out of scope (other WP).

## Iteration 1

Baseline error (ncc on .NET 10, `-ignore-confusion`):

```
error: ref type referenced System.Char&
error: internal compiler error: assertion failed in file ncc\external\InternalTypes.n, line 684.
  at InternalTypeClass.InitSystemTypes() ...
```

Analysis:
- `System.Char&` = ref-return of `String.GetPinnableReference()` hitting the `IsByRef`
  error branch in `LibraryReference.TypeOfType` (ncc\external\LibraryReference.n:185).
- The line-684 assert is `single(String_tc, "Concat")` in `InitSystemTypes`: on .NET 10
  there are TWO static 2-arg Concat overloads whose first param is not object —
  `Concat(string, string)` and `Concat(ReadOnlySpan<char>, ReadOnlySpan<char>)`.
  Filtering members with byref-like structs in the signature fixes this too.

Change: ncc\external\ExternalTypeInfo\ExternalTypeInfo.n
- Added `byref_like_attribute` (resolved once via
  `Type.GetType("System.Runtime.CompilerServices.IsByRefLikeAttribute", false)` — null on
  CLR4, non-null on .NET Core, so behavior on .NET Framework is untouched),
  `is_byref_like`, `contains_unsupported_type` (recurses through byref/pointer/array
  element types and generic args), and `is_unsupported(m : SR.MemberInfo)` which flags:
  - methods with byref return types or byref-like structs anywhere in the signature,
  - ref fields / fields of byref-like type,
  - ref-returning properties / properties involving byref-like types (incl. indexer params).
- Hooked it into `collect_members`:
  `unless ((is_internal(m) && !this.library.IsFriend) || is_unsupported(m)) ...`
  This is the single funnel through which all external members are imported
  (property/event accessors mirror the enumerated methods, so they stay consistent).

Result: rebuild OK; CLR4 regression OK (hello4.exe prints Hello!); on .NET 10 both
import errors are gone. New failure:

```
MissingMethodException: System.AppDomain.DefineDynamicAssembly(AssemblyName,
AssemblyBuilderAccess, String, PermissionSet, PermissionSet, PermissionSet)
  at TypesManager.CreateAssembly()
```

That is emission territory — but it fires BEFORE method bodies are typed
(pipeline: Hierarchy.Run -> CreateAssembly -> EmitAuxDecls -> EmitDecls; bodies are
typed lazily inside EmitDecls). So to actually prove the importer survives typing,
iterations 2-4 used a TEMPORARY, since-reverted shim in the emission code.

## Iterations 2-4 (temporary emission shims, used only as a test vehicle, REVERTED)

Shim diff preserved at scratchpad `wp-a\temp-emission-shims.diff` (files:
ncc\generation\HierarchyEmitter.n, ncc\hierarchy\CustomAttribute.n). Findings the
emission WP will need — on CoreCLR a method whose body references a
missing member throws MissingMethodException/TypeLoadException when the CALLING
method is JIT-compiled, so legacy-only Reflection.Emit calls must live in separate
methods that are never invoked (and hence never JITted) on .NET Core:

1. `AppDomain.DefineDynamicAssembly` 6-arg call moved to a helper; core path used
   `AssemblyBuilder.DefineDynamicAssembly(AssemblyName, AssemblyBuilderAccess)` via
   reflection + `DefineDynamicModule(name)` (1-arg; the 2/3-arg overloads and
   `GetSymWriter` do not exist on core and must also be isolated).
2. `AttributeCompilerClass.GetPermissionSets` fails to JIT on core
   (`System.Security.Permissions, Version=0.0.0.0` assembly not present). Guarded to
   return `[]` on core (detected via `Type.GetType("System.Runtime.Loader.AssemblyLoadContext,
   System.Runtime.Loader")`), legacy body split into a separate method. It is called
   from CreateAssembly AND from TypeBuilder.CreateEmitBuilder for every type.

With the shims in place, on .NET 10:

- hello.n: full pipeline ran — metadata import, InitSystemTypes, body typing
  (Console.WriteLine overload resolution), IL emission — and failed ONLY at save:
  `MissingMethodException: Void System.Reflection.Emit.AssemblyBuilder.Save(String,
  PortableExecutableKinds, ImageFileMachine) at TypesManager.SaveAssembly()`.
  No import/typing errors at all. (= the "errors only at save time" goal)
- hello2.n (generics + Nemerle stdlib: `def l = [1,2,3]; WriteLine($"$(l.Head)")`):
  typing fully succeeded (list[int] from Nemerle.dll, Head, string-interpolation
  macros); failed later inside IL emission:
  `TypeLoadException: Could not load type 'System.Reflection.Emit.FieldToken' from
  mscorlib at ILEmitter.GetHackishField(...)` — emission WP scope
  (ncc\generation\ILEmitter.n uses the FieldToken/MethodToken hack removed on core).

No further importer fixes were needed: iteration 1's member filter was sufficient.

## Final state (shims reverted, only ncc\external change kept)

- Changed file (permanent): `ncc\external\ExternalTypeInfo\ExternalTypeInfo.n` only.
- .NET 10: `dotnet exec ...\Stage1\ncc.exe -out:hello10.exe hello.n` passes metadata
  loading/typing-prerequisites and fails with the (expected, out-of-scope) emission
  error at `TypesManager.CreateAssembly()`: MissingMethodException for the 6-arg
  `AppDomain.DefineDynamicAssembly`. Identical for hello2.n.
- CLR4 regression: Stage1 ncc.exe natively compiles hello.n and hello2.n; both
  executables run and print the expected output (`Hello!` / `1`).
- Note for emission WP: after fixing CreateAssembly + GetPermissionSets, the next
  blockers (verified empirically) are ILEmitter's FieldToken/MethodToken hacks and
  finally AssemblyBuilder.Save.
