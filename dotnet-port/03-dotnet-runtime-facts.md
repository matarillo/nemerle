# 03 — .NET 10 Runtime Facts for the ncc SRE Backend Port

Verified 2026-07-12 on Windows 11, .NET SDK 10.0.301, runtime 10.0.x, with local test programs in
`scratchpad\wp-c\EmitTests\` (net10.0 console app, arg-dispatched tests `t1`..`t7`; run with
`dotnet run -- tN`). Primary web source: the PersistedAssemblyBuilder class docs
(https://learn.microsoft.com/en-us/dotnet/api/system.reflection.emit.persistedassemblybuilder),
whose Remarks section contains official samples for entry point, resources, and PDB.

Legend: **[V]** = VERIFIED-LOCALLY, **[D]** = DOCUMENTED (web source), **[U]** = UNCERTAIN.

---

## FACTS

### 1. PersistedAssemblyBuilder basics

- **[V]** `PersistedAssemblyBuilder` exists in-box (System.Reflection.Emit.dll, no NuGet needed) and
  **is a subclass of `AssemblyBuilder`** (`PersistedAssemblyBuilder : AssemblyBuilder`, sealed).
  Code holding an `AssemblyBuilder` reference can call `DefineDynamicModule`, `SetCustomAttribute`
  etc. polymorphically. Introduced in .NET 9 **[D]**.
- **[V]** Only ONE `DefineDynamicModule` overload exists on core: `DefineDynamicModule(string name)`.
  No `(name, fileName)` / `(name, fileName, emitSymbolInfo)` overloads. There is no per-module
  filename: the whole single-module assembly is written by `Save(path)` / `Save(Stream)`.
- **[V]** Only ONE module per dynamic assembly: a second `DefineDynamicModule` throws
  `InvalidOperationException` ("You cannot have more than one dynamic module...").
- **[V]** The module name passed to `DefineDynamicModule` becomes the ModuleDef row name in the
  saved file (verified via SRM: `ModuleDef name: T1Module`). In-memory `Module.Name` reports
  `<In Memory Module>`; `Module.ScopeName` reports the given name.
- **[V]** `Save(string path)` and `Save(Stream)` both work. Saved DLL loads via `Assembly.LoadFrom`
  on .NET 10 and its methods execute (invoked `Add(2,3) == 5`).
- **[V]** **All `TypeBuilder`s must have `CreateType()` called before `Save`/`GenerateMetadata`**,
  otherwise `NotSupportedException` ("The invoked member is not supported before the type is
  created"). See also fact 7 on ordering.
- **[V]** With `coreAssembly = typeof(object).Assembly`, the saved assembly's AssemblyRef table
  contains **`System.Private.CoreLib, 10.0.0.0`** (full public key in the ref blob;
  `GetReferencedAssemblies` shows `PublicKeyToken=7cec85d7bea7798e`). This loads and runs fine on
  the same/newer CoreCLR (both `Assembly.LoadFrom` and `dotnet exec` verified), but produces
  non-portable, implementation-bound references. Because ncc imports metadata via runtime
  reflection, all other refs will likewise be implementation assemblies (System.Console etc.) —
  acceptable for M1/self-host on the same runtime.
- **[D]** The sanctioned path for TFM-clean output later: open reference assemblies with
  `MetadataLoadContext` and pass `context.CoreAssembly` as `coreAssembly`, using MLC types
  everywhere instead of `typeof(...)`. (Official docs, PersistedAssemblyBuilder Remarks.)

### 2. Entry point / runnable exe

- **[V]** Full recipe verified end-to-end (see RECIPES): `GenerateMetadata(out ilStream, out
  fieldData)` → `ManagedPEBuilder(header: new PEHeaderBuilder(imageCharacteristics:
  Characteristics.ExecutableImage), ..., entryPoint: MetadataTokens.MethodDefinitionHandle(
  mainBuilder.MetadataToken))` → serialize to `HelloExe.dll` + hand-written
  `HelloExe.runtimeconfig.json` → `dotnet exec HelloExe.dll` printed output and returned the
  emitted exit code (42). CorHeader entry-point token in the file: 0x06000001.
- **[V]** **Token timing (critical):** `MethodBuilder.MetadataToken` on a persisted builder is `0`
  before `GenerateMetadata`/`Save` — even after `CreateType()`. It becomes valid (0x06xxxxxx) only
  AFTER `GenerateMetadata` (or `Save`) runs. So: `CreateType()` all types → `GenerateMetadata(...)`
  → only then read `main.MetadataToken` for the entry-point handle. `CreateType` alone is NOT
  sufficient; `GenerateMetadata` is what assigns tokens. (Docs confirm: "The metadata tokens for
  all members are populated on the Save operation." **[D]**)
- **[V]** File extension is irrelevant to the host: saving as `.dll` and running `dotnet exec
  foo.dll` works; `.exe` naming is cosmetic on core (no apphost is produced by SRE — if a
  double-clickable exe is wanted later, copy/patch an apphost, out of scope for M1).
- **[V]** `PEHeaderBuilder.CreateExecutableHeader()` (docs' variant) and
  `new PEHeaderBuilder(imageCharacteristics: Characteristics.ExecutableImage)` both produce a
  runnable image; default `subsystem` is WindowsCui. For a DLL use
  `PEHeaderBuilder.CreateLibraryHeader()` (or `Characteristics.ExecutableImage |
  Characteristics.Dll`) — plain `Save()` does this for you, so the ManagedPEBuilder path is only
  needed when you want an entry point, resources, PDB, or other PE options.

### 3. Managed / Win32 resources

- **[V]** `ModuleBuilder.DefineManifestResource` — **MISSING on core**.
  `AssemblyBuilder.AddResourceFile` — MISSING. `AssemblyBuilder.DefineVersionInfoResource` —
  MISSING. `AssemblyBuilder.DefineUnmanagedResource` — MISSING. `ModuleBuilder.DefineResource`
  (.resources writer) — MISSING.
- **[V]** Replacement recipe verified: after `GenerateMetadata`, call
  `metadata.AddManifestResource(ManifestResourceAttributes.Public, metadata.GetOrAddString(name),
  implementation: default, offset)` per resource, where the resource bytes live in one
  `BlobBuilder` passed as `managedResources:` to `ManagedPEBuilder`. **Each entry = 4-byte LE
  length prefix + raw bytes, 8-byte aligned; `offset` is the entry's start offset in that blob.**
  Round-trip verified: `Assembly.LoadFrom(...).GetManifestResourceNames()/GetManifestResourceStream`
  returned both resources with correct contents, `ResourceLocation = Embedded,
  ContainedInManifestFile`. This covers ncc `-res` (embedded). Multiple resources per assembly
  work (docs sample shows offset 0 for a single resource; for several, use the returned offsets).
- **[D]** Linked resources (ncc `-linkres`): no AddResourceFile equivalent in SRE; the
  MetadataBuilder path would be `AddAssemblyFile(name, hash, containsMetadata: false)` +
  `AddManifestResource(..., implementation: fileHandle, offset: 0)`. Not locally verified —
  defer, rarely used.
- **[U→plan]** Win32 VERSIONINFO / unmanaged resources: no SRE API on core. The later path is
  `ManagedPEBuilder`'s `nativeResources: ResourceSectionBuilder` parameter — subclass
  `ResourceSectionBuilder` and serialize a VERSIONINFO structure yourself (Roslyn's
  `Win32ResourceConversions`/CVT code is the reference implementation), or post-process the PE.
  **Recommendation: skip Win32 resources for M1** (managed `AssemblyVersionAttribute` etc. still
  work; only Explorer file-properties version info is lost).

### 4. Debug symbols (PDB)

- **[V]** PDB emission for PersistedAssemblyBuilder **exists and works on .NET 10** (added in
  .NET 9 wave of PersistedAssemblyBuilder work **[D]**):
  - `ModuleBuilder.DefineDocument(string url, Guid language)` and
    `(string, Guid, Guid, Guid)` overloads exist and return `ISymbolDocumentWriter`.
  - `ILGenerator.MarkSequencePoint(ISymbolDocumentWriter, int, int, int, int)` exists.
  - `LocalBuilder.SetLocalSymInfo(string)` exists.
  - `GenerateMetadata` has a 3-out overload: `GenerateMetadata(out BlobBuilder ilStream,
    out BlobBuilder mappedFieldData, out MetadataBuilder pdbBuilder)`.
- **[V]** Full round trip verified: emitted 2 sequence points + a named local, serialized
  `PortablePdbBuilder(pdbMetadata, metadata.GetRowCounts(), entryPointHandle)` to a standalone
  .pdb, wired `DebugDirectoryBuilder.AddCodeViewEntry` into `ManagedPEBuilder`; reading the .pdb
  back with `MetadataReaderProvider.FromPortablePdbStream` showed the exact sequence points
  (lines 10/11), the document name, and local name `myLocal`.
- **[D]** Embedded PDB alternative: `debugDirectoryBuilder.AddEmbeddedPortablePdbEntry(pdbBlob,
  pdbBuilder.FormatVersion)` (docs sample). Note this produces **Portable PDBs** only — ncc's old
  Windows-PDB (`ISymWrapper`) code is dead on core.
- Consequence for ncc: `-debug` output is implementable in M1-quality via the same recipe; the old
  `DefineDocument` on ModuleBuilder maps 1:1.

### 5. Strong name (public-key-only embedding)

- **[V]** `AssemblyName.SetPublicKey(pk)` (+ `Flags |= AssemblyNameFlags.PublicKey`) on the name
  given to `PersistedAssemblyBuilder` makes the saved assembly carry the key:
  saved identity `SignedAsm, ... PublicKeyToken=e080a9c724e2bfcd` (from `misc\keys\Nemerle.snk`),
  AssemblyDef flags = `PublicKey`, 160-byte key blob present, `Assembly.LoadFrom` succeeds.
- **[V]** The image is **not signature-signed**: `CorFlags = ILOnly` only — the `StrongNameSigned`
  bit is NOT set (equivalent to a delay-signed assembly). CoreCLR does not verify strong-name
  signatures, so this loads and binds by full identity anyway. If a *real* signature is ever
  needed (for .NET Framework consumers), compute it over the image and patch the reserved blob —
  defer; not needed for core self-host.
- **[V]** Extracting the public key from an `.snk` key PAIR without `StrongNameKeyPair` (type is
  absent/nonfunctional on core): the snk is a CAPI `PRIVATEKEYBLOB` (bType 0x07, magic `RSA2`).
  Parse header, take `bitlen`, `pubexp`, and the modulus (first bitlen/8 bytes after the
  20-byte header), and build the strong-name public key blob:
  `[SigAlgID=0x2400][HashAlgID=0x8004][cb][0x06,0x02,0,0][0x2400]['RSA1'][bitlen][pubexp][modulus]`.
  Working code in RECIPES. Cross-checked against
  `RSACryptoServiceProvider.ImportCspBlob(pair)/ExportCspBlob(false)` — byte-identical result
  (so the CSP route is a valid alternative; the manual parse avoids any platform doubt).
  Token = last 8 bytes of SHA-1(publicKeyBlob), reversed — computed token matched the token in
  the saved file's identity exactly.
- **[V]** Nemerle.snk public key token: `e080a9c724e2bfcd` (596-byte snk, 1024-bit key, 160-byte
  public key blob).

### 6. API removals vs. the 2005-era backend (all **[V]** on net10.0)

| API | On core? | Replacement |
|---|---|---|
| `ModuleBuilder.GetMethodToken/GetFieldToken/GetTypeToken/GetConstructorToken/GetSignatureToken` | MISSING | not needed; for raw tokens use `MemberInfo.MetadataToken` after `GenerateMetadata`/`Save` |
| `MethodBuilder.GetToken()`, `FieldBuilder.GetToken()`, `ConstructorBuilder.GetToken()` | MISSING | `MetadataToken` property (see timing caveats below) |
| `TypeBuilder.CreateType()` | EXISTS (returns `Type`) | keep as-is; `CreateTypeInfo()` also exists |
| `MethodRental` | MISSING (type not found) | none; drop |
| `AssemblyBuilderAccess` | only `Run=1`, `RunAndCollect=9` | `(AssemblyBuilderAccess)2` (Save) and `3` (RunAndSave) throw `ArgumentException: Illegal enum value`. The Save path must construct `PersistedAssemblyBuilder` instead of `DefineDynamicAssembly` |
| `ModuleBuilder.FullyQualifiedName` | Run-module: returns `"RefEmit_InMemoryManifestModule"`; **persisted module: throws `NotImplementedException`** | don't call on persisted modules; use `ScopeName` (returns the DefineDynamicModule name) |
| `ModuleBuilder.DefineManifestResource` etc. | MISSING | see fact 3 |
| `TypeBuilder.GetMethod/GetField/GetConstructor(Type, ...)` static helpers | EXIST | same semantics as Framework incl. the restriction below |

- **[V]** `TypeBuilder.GetMethod(typeof(List<string>), List<>.Add)` still throws
  `ArgumentException: 'type' must be or must contain a TypeBuilder as a generic argument` — the
  exact MS.NET limitation ILEmitter's `FrameworkGetMethod` works around. The legit case
  (`TypeBuilderInstantiation` + `MethodBuilder`) works fine on core.
- **[V]** Token identity for the "hackish" lookup: on a **Run** builder, `MethodBuilder.MetadataToken`
  is valid immediately after `DefineMethod` (0x06000001) and **equals the created runtime method's
  `MetadataToken` after `CreateType`** — so `GetHackishMethod`-style matching (enumerate
  `GetMethods(DeclaredOnly)` on the generic type definition, compare `MetadataToken`) ports
  directly: just replace `builder.GetToken().Token` with `builder.MetadataToken`. On a
  **persisted** builder, `MetadataToken` is 0 until `GenerateMetadata`/`Save` — but the hackish
  path only fires when the generic *definition* is a RuntimeType (imported, tokens always valid),
  so this does not break it.
- What ILEmitter.n's hack is (from `ncc\generation\ILEmitter.n:238-339`): `FrameworkGetMethod/
  Field/Constructor` detect that the constructed generic type is a plain RuntimeType (not a
  TypeBuilderInstantiation). `TypeBuilder.GetMethod` rejects those, so `GetHackishMethod` instead
  enumerates the *generic type definition's* members and picks the one whose `MetadataToken`
  matches, then (for the runtime-runtime case) uses it directly, else feeds the remapped
  definition-member into `TypeBuilder.GetMethod`. Port note: for pure runtime-generic
  instantiations, on core you can simply reflect on the constructed type directly
  (`typeof(List<string>).GetMethod("Add")` works), so the hack can stay or shrink.

### 7. AppDomain.TypeResolve on core

- **[V]** The event **exists and can be subscribed** (`AppDomain.CurrentDomain.TypeResolve +=`)
  — no exception, no obsoletion error at runtime.
- **[V]** It **fires** for `Assembly.GetType("NotYetCreatedTypeBuilderName")` on a dynamic
  assembly (handler received `Name=Later`, `RequestingAssembly=TR`).
- **[V]** It does **NOT** fire for the emission scenario HierarchyEmitter relies on:
  `derived.CreateType()` with an uncreated `TypeBuilder` base throws
  `NotSupportedException: The invoked member is not supported before the type is created`
  *without* raising TypeResolve — both for Run and Persisted builders (Framework instead raised
  TypeResolve and let the handler create the base). It also does not fire for
  `Assembly.CreateInstance` of a missing type.
- **[V]** Persisted builder ordering requirements: `Save`/`GenerateMetadata` throw
  `NotSupportedException` if ANY defined type is uncreated; creating base first then derived then
  `Save` works. **Port consequence: the `resolve_hack` TypeResolve subscription is useless on
  core for CreateType ordering — HierarchyEmitter must topologically order `CreateType` calls
  (bases/interfaces/nested-enclosing first).** Keep the event subscription only if some load-time
  GetType path needs it; it is harmless.

---

## RECIPES (working code, from `scratchpad\wp-c\EmitTests\Program.cs`)

All compiled and executed on net10.0. Usings:

```csharp
using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
```

### R1. Persisted save of a DLL (the simple path)

```csharp
var pab = new PersistedAssemblyBuilder(new AssemblyName("MyAsm"), typeof(object).Assembly);
AssemblyBuilder ab = pab;                       // polymorphic use is fine
ModuleBuilder mb = ab.DefineDynamicModule("MyAsm"); // exactly one module; name -> ModuleDef.Name
TypeBuilder tb = mb.DefineType("N.T", TypeAttributes.Public | TypeAttributes.Class);
MethodBuilder add = tb.DefineMethod("Add", MethodAttributes.Public | MethodAttributes.Static,
                                    typeof(int), new[] { typeof(int), typeof(int) });
ILGenerator il = add.GetILGenerator();
il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldarg_1); il.Emit(OpCodes.Add); il.Emit(OpCodes.Ret);
tb.CreateType();                                // MANDATORY for every TypeBuilder, bases first
pab.Save(path);                                 // or Save(Stream)
```

### R2. Runnable exe (entry point) — the compiler's exe-save path

```csharp
// ... define everything, CreateType() every TypeBuilder, THEN:
MetadataBuilder metadata = pab.GenerateMetadata(out BlobBuilder ilStream, out BlobBuilder fieldData);
// tokens are valid only from this point on:
var entryPointHandle = MetadataTokens.MethodDefinitionHandle(mainMethodBuilder.MetadataToken);

var peBuilder = new ManagedPEBuilder(
    header: new PEHeaderBuilder(imageCharacteristics: Characteristics.ExecutableImage),
            // == PEHeaderBuilder.CreateExecutableHeader(); default subsystem = WindowsCui
    metadataRootBuilder: new MetadataRootBuilder(metadata),
    ilStream: ilStream,
    mappedFieldData: fieldData,
    entryPoint: entryPointHandle);
var peBlob = new BlobBuilder();
peBuilder.Serialize(peBlob);
using (var fs = File.Create("Hello.dll")) peBlob.WriteContentTo(fs);

File.WriteAllText("Hello.runtimeconfig.json",
  """{"runtimeOptions":{"tfm":"net10.0","framework":{"name":"Microsoft.NETCore.App","version":"10.0.0"}}}""");
// run: dotnet exec Hello.dll   -> prints, exit code = Main's return value (verified: 42)
```

Timing verified: `main.MetadataToken` is 0 before AND after `CreateType`; valid only after
`GenerateMetadata`. Never cache builder tokens earlier.

### R3. Embedded managed resources (ncc -res)

```csharp
MetadataBuilder metadata = pab.GenerateMetadata(out BlobBuilder ilStream, out BlobBuilder fieldData);

var resBlob = new BlobBuilder();
uint AddRes(byte[] data)                    // entry = 4-byte LE length + bytes, 8-aligned
{
    resBlob.Align(8);
    uint off = (uint)resBlob.Count;
    resBlob.WriteInt32(data.Length);
    resBlob.WriteBytes(data);
    return off;
}
metadata.AddManifestResource(ManifestResourceAttributes.Public,
    metadata.GetOrAddString("res1.bin"), implementation: default, offset: AddRes(bytes1));
metadata.AddManifestResource(ManifestResourceAttributes.Public,
    metadata.GetOrAddString("res2.bin"), implementation: default, offset: AddRes(bytes2));

var peBuilder = new ManagedPEBuilder(
    header: PEHeaderBuilder.CreateLibraryHeader(),   // or ExecutableImage + entryPoint for exe
    metadataRootBuilder: new MetadataRootBuilder(metadata),
    ilStream: ilStream,
    mappedFieldData: fieldData,
    managedResources: resBlob);
// serialize as in R2. Verified: GetManifestResourceNames/Stream round-trip, location=Embedded.
```

Private resources: use `ManifestResourceAttributes.Private`. For `.resources`-format content,
write with `System.Resources.ResourceWriter` to a MemoryStream first (docs sample), then embed the
bytes the same way. Combine freely with R2 (entryPoint) and R4 (PDB) — all are independent
`ManagedPEBuilder` arguments.

### R4. Portable PDB (verified; ship with -debug)

```csharp
ISymbolDocumentWriter doc = moduleBuilder.DefineDocument("C:\\src\\file.n", languageGuid);
il.MarkSequencePoint(doc, startLine, startCol, endLine, endCol);   // before emitting the point's IL
localBuilder.SetLocalSymInfo("name");
// ... CreateType all, then:
MetadataBuilder metadata = pab.GenerateMetadata(out var ilStream, out var fieldData,
                                                out MetadataBuilder pdbMetadata);
var pdbBuilder = new PortablePdbBuilder(pdbMetadata, metadata.GetRowCounts(),
                                        entryPointHandleOrDefault);
var pdbBlob = new BlobBuilder();
BlobContentId pdbId = pdbBuilder.Serialize(pdbBlob);
using (var fs = File.Create("MyAsm.pdb")) pdbBlob.WriteContentTo(fs);
var debugDir = new DebugDirectoryBuilder();
debugDir.AddCodeViewEntry("MyAsm.pdb", pdbId, pdbBuilder.FormatVersion);
// (embedded alternative: debugDir.AddEmbeddedPortablePdbEntry(pdbBlob, pdbBuilder.FormatVersion))
new ManagedPEBuilder(..., debugDirectoryBuilder: debugDir).Serialize(peBlob);
```

### R5. Public key from .snk + embedding (ncc -keyfile, unsigned/“delay-signed”)

```csharp
static byte[] GetStrongNamePublicKeyFromKeyPair(byte[] snk)
{
    if (snk[0] != 0x07)                                    // not a private blob:
    {
        if (snk.Length > 12 && BitConverter.ToUInt32(snk, 0) == 0x2400)
            return snk;                                    // already a public key blob (sn -p)
        throw new ArgumentException("not a CAPI key blob");
    }
    if (BitConverter.ToUInt32(snk, 8) != 0x32415352) throw new ArgumentException("magic != RSA2");
    int bitlen  = BitConverter.ToInt32(snk, 12);
    uint pubexp = BitConverter.ToUInt32(snk, 16);
    byte[] modulus = snk.AsSpan(20, bitlen / 8).ToArray(); // little-endian, as stored

    int cb = 8 + 12 + modulus.Length;                      // PUBLICKEYBLOB size
    byte[] res = new byte[12 + cb];
    using var w = new BinaryWriter(new MemoryStream(res));
    w.Write(0x00002400u);            // SigAlgID  = CALG_RSA_SIGN
    w.Write(0x00008004u);            // HashAlgID = CALG_SHA1
    w.Write(cb);
    w.Write((byte)0x06); w.Write((byte)0x02); w.Write((ushort)0); // PUBLICKEYBLOB header
    w.Write(0x00002400u);            // aiKeyAlg
    w.Write(0x31415352u);            // 'RSA1'
    w.Write(bitlen); w.Write(pubexp); w.Write(modulus);
    return res;
}
// token (for diagnostics): last 8 bytes of SHA1(publicKey), reversed.

var an = new AssemblyName("MyAsm");
an.SetPublicKey(GetStrongNamePublicKeyFromKeyPair(File.ReadAllBytes("Nemerle.snk")));
an.Flags |= AssemblyNameFlags.PublicKey;
var pab = new PersistedAssemblyBuilder(an, typeof(object).Assembly);
// ... emit, Save. Verified: identity has PublicKeyToken, AssemblyDef flags = PublicKey,
// CorFlags lacks StrongNameSigned (no signature) — loads fine on CoreCLR.
```

(`RSACryptoServiceProvider.ImportCspBlob(snk)` + `ExportCspBlob(false)` + the 12-byte header
produces byte-identical output — verified — but the manual parse has zero platform dependencies.)

---

## Port checklist derived from these facts

1. Save path: replace `DefineDynamicAssembly(..., Save/RunAndSave)` + `AssemblyBuilder.Save(file)`
   with `PersistedAssemblyBuilder` + `Save(path)`; exe output goes through R2 instead of
   `SetEntryPoint`/`PEFileKinds` (both absent on core). Emit a runtimeconfig.json next to exes.
2. Guarantee `CreateType()` on every TypeBuilder before save, ordered bases/ifaces first;
   drop reliance on TypeResolve for ordering (fact 7).
3. Replace `GetToken().Token` with `MetadataToken` in the hackish member lookup (fact 6).
4. Never touch `FullyQualifiedName` of the persisted module; use `ScopeName`.
5. `-res` via R3; `-linkres` deferred; Win32 version resources deferred (fact 3).
6. `-debug` via R4 (portable PDB only); `-keyfile` via R5 (public key only, no signature).
