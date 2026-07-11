# 12 — WP-C: SELF-HOST blockers (strong-name / GetIsFriend / codedom) — iteration log

Goal: remove the three remaining SELF-HOST blockers identified in `01-api-inventory.md` /
`11-emission-log.md`'s "remaining known gaps", so that Stage1 ncc, running on .NET 10, can
compile the Nemerle toolchain's own -keyfile-signed projects that use
`InternalsVisibleTo(..., PublicKey=...)`:

1. `ncc\external\LibraryReference.n` `GetIsFriend`/`snKey()` — used `SR.StrongNameKeyPair` to
   derive OUR public key when checking a referenced assembly's `InternalsVisibleTo` grants.
2. `ncc\hierarchy\CustomAttribute.n` `read_keypair` / `CreateAssemblyName` — used
   `SR.StrongNameKeyPair` / `AssemblyName.KeyPair` to sign the OUTPUT assembly from `-keyfile`.
3. `ncc\codedom\*` — compiled into `Nemerle.Compiler.dll` but unused by ncc itself, and pulls in
   `System.CodeDom`/`System.Configuration`, neither of which is in the .NET 10 shared framework.

Builds on WP-B (`11-emission-log.md`), which left all three as open gaps and had already built
the runtime-detection/bridge infrastructure (`CoreEmitBridge.n`, `dotnet-port\Nemerle.CoreEmit\`)
and a (then-unwired) `GetPublicKeyFromSnk` CAPI-blob parser.

## Key design decision: task 1 needs NO runtime dispatch at all

Per `03-dotnet-runtime-facts.md` fact 5, the `.snk` file is a CAPI `PRIVATEKEYBLOB`; deriving the
*public* key from it is pure byte manipulation (+ SHA1, for the 8-byte token) — no CoreCLR-only
API is involved anywhere. So unlike the rest of the CoreCLR port (which isolates CLR4-only calls
into never-invoked-on-core methods, per WP-B's central JIT-time-resolution lesson), this logic
runs identically on CLR4 and CoreCLR. New file `ncc\misc\SnkUtils.n` (plain Nemerle, compiles
under boot-4.0) implements it once and is used, unconditionally, by both call sites:

- `GetPublicKeyFromFile`/`GetPublicKeyFromBytes` — parses the CAPI `PRIVATEKEYBLOB` (bType 0x07,
  magic `RSA2`) into a strong-name PUBLIC key blob (or passes through an already-public blob, the
  `sn -p` case), exactly the recipe in `03-dotnet-runtime-facts.md` fact 5 / RECIPE R5.
- `GetPublicKeyToken` — last 8 bytes of `SHA1(publicKeyBlob)`, reversed.
- `ToHexString` — hex encoding for comparing against `InternalsVisibleTo`'s
  `PublicKey=...`/`PublicKeyToken=...` clauses.

`StrongNameKeyPair` is removed from `LibraryReference.n` **entirely, for both runtimes** — task 1
explicitly called for this since `GetIsFriend` only ever needs the public key, never signs
anything, so there is no reason to keep the CLR4-only, PNSE-on-core type in that path at all.

Task 2 (`CustomAttribute.n`) is different: on CLR4 it must keep *real* signing (byte-for-byte
identical to before), which genuinely requires `StrongNameKeyPair`/`AssemblyName.KeyPair` — those
still throw `PlatformNotSupportedException` on CoreCLR (constraints doc / fact 5), so that half
gets the usual CLR4/Core dual-path isolation via `CoreEmitBridge.IsCoreClr`.

## Two real bugs found and fixed along the way (both pre-existing, both on the acceptance-test's
## critical path, neither previously exercised by the test suite — no test uses IVT+PublicKey)

While wiring up the WP-C acceptance test (`InternalsVisibleTo("libB, PublicKey=<hex>")` between
two -keyfile-signed assemblies), `GetIsFriend`'s `[asmName, pKey]` match arm turned out to be
**dead code on CLR4 too**, for two independent reasons — both fixed in `LibraryReference.n`:

1. **Inverted guard**: the arm's `when` clause required
   `string.IsNullOrEmpty(Options.StrongAssemblyKeyName)` — i.e. it only activated when we had *no*
   key file, and its body immediately tried `File.Open(Options.StrongAssemblyKeyName, ...)` on that
   same (then-empty) path, which throws unconditionally. Fixed to `!string.IsNullOrEmpty(...)`
   (we can only compare against our own public key if we actually have one).
2. **Wrong split delimiter**: `pKey.ToLower().SplitToList(array[','])` split "publickey=&lt;hex&gt;"
   on comma — but there is no comma in that substring, only `=`. The 2-element pattern
   `["publickey", key]` therefore could never match on any runtime. Fixed to
   `array['=']`.

Found by testing on CoreCLR (task 4a/4b): libB's compile failed with an internal-compiler-error
("Invalid assembly public key") first (a *third*, unrelated bug — see below), and after fixing
that, with "no member named `Secret`" (friend check silently returning false due to bug 2 above).
Re-verified the fix is not CoreCLR-specific by re-running the exact same two-step compile natively
on CLR4 (task 4d) — same two bugs, same fix, same result on both runtimes, confirming `GetIsFriend`
truly needed no dual-path.

## Third bug: `BinaryWriter.Write` overload selection differs between Nemerle and C# for numeric
## literals

The first working build of `SnkUtils.GetPublicKeyFromBytes` (`ncc\misc\SnkUtils.n`) passed bare
`0x00002400u`/`0x00008004u`/`0x31415352u` literals straight to `BinaryWriter.Write(...)`, exactly
mirroring the already-verified C# recipe in `dotnet-port\Nemerle.CoreEmit\Emitter.cs`
(`GetPublicKeyFromSnkBytes`, from `03-dotnet-runtime-facts.md`). It compiled and ran, but the
comparison in task 4c (`AssemblyLoadContext.LoadFromAssemblyPath` on a referencing assembly) failed
with `System.Security.SecurityException: Invalid assembly public key.` — the embedded blob was
malformed. Root cause, confirmed by reading the raw `AssemblyDef.PublicKey` blob via
`System.Reflection.Metadata`: **Nemerle's numeric-literal overload resolution picks the narrowest
`Write` overload the literal's *value* fits in, ignoring the `u` suffix** (unlike C#, where `u`
pins the literal to `uint`). `0x00002400u` (9216) and `0x00008004u` (32772) both fit `ushort`, so
`w.Write (0x00002400u)` silently bound to `Write(UInt16)` (2 bytes) instead of `Write(UInt32)`
(4 bytes), truncating two 4-byte fields to 2 bytes each and shifting everything after them.
`0x31415352u` (830M+) doesn't fit `ushort`/`Int16` and happened to bind correctly. Fix: introduce
explicitly `: uint`-typed locals (`sigAlgId`, `hashAlgId`, `aiKeyAlg`, `rsaMagic`) and pass those to
`Write` instead of bare literals — forces unambiguous overload resolution. Re-verified byte-for-byte
identical to the known-good C# output (`dotnet-port\Nemerle.CoreEmit\Emitter.GetPublicKeyFromSnk`,
cross-checked directly via a throwaway `dotnet run` tool against `misc\keys\Nemerle.snk`) before
and after the fix confirmed the corruption and the repair.

**Lesson for future Nemerle→BCL porting**: when transliterating a C#/verified-recipe that relies on
suffixed numeric literals (`u`/`L`/etc.) resolving to a *specific* overload, do not trust Nemerle to
honor the suffix the way C# does for overload selection — bind through an explicitly typed local
first, or add an explicit `:>` cast, whenever the call has multiple integer-width overloads.

## Files changed

- `ncc\misc\SnkUtils.n` (new) — runtime-neutral `.snk` CAPI blob parsing (`GetPublicKeyFromFile`,
  `GetPublicKeyFromBytes`, `GetPublicKeyToken`, `ToHexString`). No CoreEmitBridge/reflection
  involved; identical code path on CLR4 and CoreCLR.
- `ncc\external\LibraryReference.n` — `GetIsFriend` rewritten: `StrongNameKeyPair` removed
  entirely (both runtimes), lazy `ourPublicKey()` via `SnkUtils`, plus the two dead-code fixes
  above (inverted `IsNullOrEmpty` guard, `,`→`=` split delimiter).
- `ncc\hierarchy\CustomAttribute.n` — `read_keypair` renamed `read_keypair_clr4` (body unchanged,
  CLR4-only); new `set_assembly_key_clr4` (real signing, `an.KeyPair = ...`, unchanged behavior);
  new `set_assembly_key_core` (derives public key via `SnkUtils`, `an.SetPublicKey(...)` +
  `an.Flags |= AssemblyNameFlags.PublicKey`, one-time `Message.Warning` via
  `warn_public_key_only_once`); new `set_assembly_key` dispatcher on `CoreEmitBridge.IsCoreClr`;
  `CreateAssemblyName` no longer reads `an.KeyPair` as an "already have a key?" check (that getter
  also throws `PlatformNotSupportedException` on CoreCLR) — replaced with a local `has_key : bool`.
- `ncc\generation\CoreEmitBridge.n` — removed the now-superseded `GetPublicKeyFromSnk` bridge
  method/field (dead code from WP-B, superseded by the direct, dual-runtime `SnkUtils`).
- `Nemerle.Compiler.nproj` — removed the `<Compile Include="ncc\codedom\*.n">` item group (with an
  explanatory comment). `ncc.build` (NAnt) intentionally left untouched (unused per the work
  order). No `System.Configuration`/`System.CodeDom` `<Reference>` existed in this file to remove.

## Acceptance test (task 4) — commands + outputs

Setup: `misc\keys\Nemerle.snk` public key (1024-bit RSA, 160-byte blob) hex, obtained via a
throwaway net10.0 console tool calling `Nemerle.CoreEmit.Emitter.GetPublicKeyFromSnk` (cross-check
target, since it predates this WP and was already verified against `03-dotnet-runtime-facts.md`):

```
PublicKeyToken=E080A9C724E2BFCD   (matches the known Nemerle.snk token)
```

`libA.n`:
```nemerle
using System.Runtime.CompilerServices;
[assembly: InternalsVisibleTo("libB, PublicKey=0024...cbf")]   // full 320-hex-char key
namespace LibA
{
  public class Greeter
  {
    internal static Secret() : string { "internal-secret-42" }
    public Greet(name : string) : string { $"Hello, $name!" }
  }
}
```
`libB.n`:
```nemerle
namespace LibB
{
  public class Caller
  {
    public static CallInternal() : string { LibA.Greeter.Secret() }   // uses libA's INTERNAL member
  }
}
```

### 4a/4b — CoreCLR (.NET 10), S1 = `bin\Release\net-4.0\Stage1`

```
$ dotnet exec $S1\ncc.exe -keyfile:misc\keys\Nemerle.snk -target:library -out:libA.dll libA.n
warning: running on CoreCLR: the output assembly's public key ... is embedded ... but NOT strong-name signed
libA.n:9: warning N10003: method LibA.Greeter.Secret() ... never been referenced
exit=0

$ dotnet exec $S1\ncc.exe -keyfile:misc\keys\Nemerle.snk -target:library -ref:libA.dll -out:libB.dll libB.n
warning: running on CoreCLR: ... NOT strong-name signed
exit=0
```
Both compiled — libB successfully resolved and called `LibA.Greeter.Secret()`, an `internal`
member, proving `GetIsFriend` recognizes the `PublicKey=` grant on CoreCLR.

### 4c — reflection round-trip on CoreCLR

```csharp
var asm = AssemblyLoadContext.Default.LoadFromAssemblyPath(".../libA.dll");
Console.WriteLine(asm.GetName().FullName);
// -> libA, Version=0.0.0.0, Culture=neutral, PublicKeyToken=e080a9c724e2bfcd

var caller = AssemblyLoadContext.Default.LoadFromAssemblyPath(".../libB.dll");
var mi = caller.GetType("LibB.Caller").GetMethod("CallInternal", ...);
Console.WriteLine(mi.Invoke(null, null));
// -> internal-secret-42
```
`PublicKeyToken=e080a9c724e2bfcd` confirmed in the loaded identity; the internal call round-trips
correctly end to end (compile on core → load on core → invoke on core).

Plus the standing M1 regressions, re-run on core:
```
$ dotnet exec $S1\ncc.exe -out:hello10.exe hello.n && dotnet exec hello10.exe        # Hello!, exit 0
$ dotnet exec $S1\ncc.exe -out:hello2_10.exe hello2.n && dotnet exec hello2_10.exe   # 1, exit 0
$ dotnet exec $S1\ncc.exe -target:library -out:libtest10.dll libA.n                  # exit 0
$ dotnet exec $S1\ncc.exe -resource:res1.txt -out:helloRes10.exe hello.n && dotnet exec helloRes10.exe  # Hello!, exit 0
```
(hello2 needs `Nemerle.dll` copied next to the output exe in the scratch dir — a scratch-dir
artifact unrelated to this WP, same as noted in `11-emission-log.md`.)

### 4d — CLR4 regression, S1\ncc.exe run natively (no `dotnet exec`)

```
$ $S1\ncc.exe -out:hello4.exe hello.n && .\hello4.exe                # Hello!, exit 0
$ $S1\ncc.exe -out:hello2_4.exe hello2.n && .\hello2_4.exe           # 1, exit 0
$ $S1\ncc.exe -keyfile:misc\keys\Nemerle.snk -target:library -out:libA.dll libA.n     # exit 0
$ $S1\ncc.exe -keyfile:misc\keys\Nemerle.snk -target:library -ref:libA.dll -out:libB.dll libB.n  # exit 0
```
No "running on CoreCLR..." warning printed (confirms the CLR4 branch, not the Core one, was taken).
Genuine strong-name signing verified independently of ncc, via the .NET Framework SDK's `sn.exe`:
```
$ sn.exe -vf libA.dll
アセンブリ 'libA.dll' は有効です      (= "assembly libA.dll is valid" -- i.e. real, verifiable signature)
$ sn.exe -T libA.dll
公開キー トークン e080a9c724e2bfcd    (= public key token e080a9c724e2bfcd)
```
`sn -vf` reporting the assembly as *valid* (not merely present) confirms this is a real
cryptographic strong-name signature, not the CoreCLR public-key-only ("delay-signed-equivalent")
identity — i.e. CLR4 behavior is genuinely unchanged by this WP.

### codedom exclusion verification

Stage1 build is clean (0 errors) with `ncc\codedom\*.n` removed from `Nemerle.Compiler.nproj`.
Confirmed via reflection over the built `Nemerle.Compiler.dll` that no `*CodeDom*`/`NemerleCode*`
type remains:
```
Loaded Nemerle.Compiler.dll, scanned all types for Namespace.Contains("CodeDom") / Name.Contains("NemerleCode")
-> "No CodeDom-related types found in Nemerle.Compiler.dll -- codedom exclusion confirmed."
```
`grep`-confirmed `System.CodeDom`/`System.Configuration` usage is confined to the 4 removed
`ncc\codedom\*.n` files; no other file under `ncc\`/`lib\`/`macros\` references them. The only
other repo-wide hits are in `VsIntegration\`/`snippets\` (out of scope) and
`testsuite\positive\codedom.n` (a `<Content>` item, not compiled into any of the 4 core projects).

## Surprises / notes for the stage2 orchestration package

- `GetIsFriend`'s `PublicKey=`/`PublicKeyToken=` matching was **silently non-functional on CLR4
  before this WP** (two independent bugs, see above) — this WP is therefore not just a CoreCLR
  port but the first time this feature has ever worked, on either runtime. Worth flagging in case
  downstream tooling/docs assumed it "already worked on Framework".
- `CoreEmitBridge.GetPublicKeyFromSnk` (added in WP-B as unwired scaffolding) is now removed; its
  C# counterpart `Nemerle.CoreEmit.Emitter.GetPublicKeyFromSnk`/`GetPublicKeyFromSnkBytes` in
  `dotnet-port\Nemerle.CoreEmit\Emitter.cs` was left in place (harmless, still useful as an
  independent cross-check implementation / for any future C#-side tooling) but is no longer called
  by ncc itself — all `-keyfile` logic now goes through `ncc\misc\SnkUtils.n` directly.
  Nemerle.CoreEmit.dll still needs to ship next to Stage1's `ncc.exe` (unrelated: still used for
  the emission save path from WP-B).
- Real strong-name signing (CLR4) still computes the actual RSA signature over the image the
  original way (`AssemblyBuilder.Save` with a real `KeyPair`) — nothing about the signing
  *mechanism* changed, only how `GetIsFriend` derives a comparison public key and how the CoreCLR
  path embeds a public-key-only identity. `-delaysign` was not touched (no such attribute path
  exists in this codebase currently — grepped, no hits); if it's added later it only affects the
  CLR4 branch (`set_assembly_key_clr4`), the Core branch already behaves like a delay-signed
  assembly unconditionally (per fact 5, CoreCLR never verifies signatures anyway).
- Self-host proper (recompiling Nemerle.dll/Nemerle.Compiler.dll/Nemerle.Macros.dll themselves on
  CoreCLR) was **not** attempted in this WP — task scope was the three blockers plus a synthetic
  two-assembly acceptance test. The real Nemerle build does not currently use
  `InternalsVisibleTo(..., PublicKey=...)` between its own assemblies (grepped: no hits outside
  `testsuite\`), so stage2 orchestration should not assume this path has been exercised against
  the actual `lib\`/`ncc\` sources — only against the synthetic libA/libB pair.
- PDB (`-debug`) on CoreCLR is still unwired (WP-B gap, untouched here, still warns-and-skips).
  `-linkres`/Win32 `-res` on CoreCLR are still unwired (WP-B gap, untouched here).
