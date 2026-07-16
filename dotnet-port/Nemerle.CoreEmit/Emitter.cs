// Nemerle.CoreEmit: CoreCLR-only emission backend for the Nemerle compiler (ncc).
//
// The Nemerle compiler sources (ncc\generation\*.n) are compiled by the net4-era
// bootstrap compiler and must therefore never reference PersistedAssemblyBuilder,
// System.Reflection.Metadata or any other CoreCLR-only API directly -- those types
// simply do not exist in the net4 BCL the bootstrap compiler type-checks against.
//
// This assembly is built separately, targeting net10.0 (where these APIs live in
// the shared framework, System.Reflection.Emit.dll -- no NuGet packages needed) and
// is loaded by the compiler (ncc\generation\CoreEmitBridge.n) via Assembly.LoadFrom +
// reflection ONLY when running on CoreCLR. On .NET Framework this assembly is never
// loaded at all; the compiler uses its original System.Reflection.Emit.AssemblyBuilder
// based backend (AppDomain.DefineDynamicAssembly, AssemblyBuilder.Save, etc.)
//
// See dotnet-port\03-dotnet-runtime-facts.md for the verified recipes this file is
// based on.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;

namespace Nemerle.CoreEmit
{
    public static class Emitter
    {
        // dotnet-port WP-F/17-resources-fixes-log.md: PersistedAssemblyBuilder.SetCustomAttribute
        // (the assembly-level, not type-level, overload) corrupts the saved PE when the
        // attribute's constructor belongs to a TypeBuilder defined in the SAME persisted
        // assembly (verified with a minimal repro independent of Nemerle -- the equivalent
        // TypeBuilder.SetCustomAttribute call with the exact same locally-defined
        // ConstructorBuilder works fine, so this is specific to the assembly-level overload).
        // The saved file loads with BadImageFormatException even though ncc reports zero
        // compile errors -- e.g. `[assembly: SomeLocallyDefinedAttribute]`. Constructors from
        // already-compiled (external) assemblies are unaffected (PersistedAssemblyBuilder
        // correctly synthesizes the MemberRef for those during GenerateMetadata), so this
        // path is only needed for local (TypeBuilder-declared) attribute constructors --
        // SetAssemblyCustomAttribute below detects that case and defers such attributes to a
        // pending list, later flushed directly into the MetadataBuilder returned by
        // GenerateMetadata() (see FlushPendingAssemblyAttributes in Save), instead of ever
        // calling the broken AssemblyBuilder.SetCustomAttribute for them.
        private static readonly ConditionalWeakTable<AssemblyBuilder, List<CustomAttributeBuilder>> s_pendingAssemblyAttributes = new();

        private static ConstructorInfo GetCabCtor(CustomAttributeBuilder cab)
        {
            FieldInfo fi = typeof(CustomAttributeBuilder).GetField("m_con", BindingFlags.NonPublic | BindingFlags.Instance);
            if (fi == null)
                throw new InvalidOperationException(
                    "Nemerle.CoreEmit.Emitter: System.Reflection.Emit.CustomAttributeBuilder no longer has " +
                    "a private field named 'm_con' on this runtime -- the assembly-level-local-attribute " +
                    "workaround (see dotnet-port\\17-resources-fixes-log.md) needs updating.");
            return (ConstructorInfo)fi.GetValue(cab);
        }

        private static byte[] GetCabBlob(CustomAttributeBuilder cab)
        {
            FieldInfo fi = typeof(CustomAttributeBuilder).GetField("m_blob", BindingFlags.NonPublic | BindingFlags.Instance);
            if (fi == null)
                throw new InvalidOperationException(
                    "Nemerle.CoreEmit.Emitter: System.Reflection.Emit.CustomAttributeBuilder no longer has " +
                    "a private field named 'm_blob' on this runtime -- the assembly-level-local-attribute " +
                    "workaround (see dotnet-port\\17-resources-fixes-log.md) needs updating.");
            return (byte[])fi.GetValue(cab);
        }

        /// <summary>
        /// Applies an assembly-level custom attribute, routing around the
        /// PersistedAssemblyBuilder.SetCustomAttribute bug described above when (and only
        /// when) the attribute's constructor belongs to a not-yet-baked TypeBuilder in this
        /// same assembly. Must be used (instead of AssemblyBuilder.SetCustomAttribute
        /// directly) for every assembly-level attribute on the CoreCLR path; Save's
        /// FlushPendingAssemblyAttributes writes back any deferred ones after
        /// GenerateMetadata().
        /// </summary>
        public static void SetAssemblyCustomAttribute(AssemblyBuilder ab, CustomAttributeBuilder cab)
        {
            ConstructorInfo con = GetCabCtor(cab);
            if (con.DeclaringType is TypeBuilder)
            {
                List<CustomAttributeBuilder> pending = s_pendingAssemblyAttributes.GetOrCreateValue(ab);
                pending.Add(cab);
            }
            else
            {
                ab.SetCustomAttribute(cab);
            }
        }

        private static void FlushPendingAssemblyAttributes(AssemblyBuilder ab, MetadataBuilder metadata)
        {
            if (!s_pendingAssemblyAttributes.TryGetValue(ab, out List<CustomAttributeBuilder> pending))
                return;

            // The Assembly table has exactly one row (this assembly); its EntityHandle is
            // always token 0x20000001 in the module being generated.
            EntityHandle assemblyHandle = MetadataTokens.EntityHandle(0x20000001);
            foreach (CustomAttributeBuilder cab in pending)
            {
                ConstructorInfo con = GetCabCtor(cab);
                byte[] blob = GetCabBlob(cab);
                // Valid only after GenerateMetadata() has run (see the MetadataToken timing
                // note on entryPointMethod below).
                EntityHandle ctorHandle = MetadataTokens.EntityHandle(con.MetadataToken);
                BlobHandle blobHandle = metadata.GetOrAddBlob(blob);
                metadata.AddCustomAttribute(assemblyHandle, ctorHandle, blobHandle);
            }
            s_pendingAssemblyAttributes.Remove(ab);
        }

        /// <summary>
        /// Creates a persisted (savable) dynamic assembly builder. Used for the
        /// "-target:exe/library, save to disk" path on CoreCLR (the equivalent of
        /// AppDomain.DefineDynamicAssembly(..., AssemblyBuilderAccess.Save, ...) on
        /// .NET Framework, which does not exist on CoreCLR).
        /// </summary>
        public static AssemblyBuilder CreateBuilder(AssemblyName name)
        {
            return new PersistedAssemblyBuilder(name, typeof(object).Assembly);
        }

        /// <summary>
        /// Creates a run-only (never persisted) dynamic assembly builder. Used for the
        /// "-compile-to-memory" path on CoreCLR (AssemblyBuilderAccess.Save/RunAndSave do
        /// not exist there; only Run and RunAndCollect do).
        /// </summary>
        public static AssemblyBuilder CreateRunBuilder(AssemblyName name)
        {
            return AssemblyBuilder.DefineDynamicAssembly(name, AssemblyBuilderAccess.RunAndCollect);
        }

        // Deterministic content ID (MVID / PE timestamp / PDB ID) derived from a SHA1 hash of
        // the emitted bytes, same technique Roslyn's csc /deterministic uses -- SHA1 here is
        // purely a content-addressing digest, not a security primitive.
        private static BlobContentId ComputeDeterministicId(IEnumerable<Blob> blobs)
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
            foreach (var blob in blobs)
                hash.AppendData(blob.GetBytes());
            return BlobContentId.FromHash(hash.GetHashAndReset());
        }

        // PersistedAssemblyBuilder.GenerateMetadata bakes a random MVID into the GUID heap
        // before ManagedPEBuilder ever runs, so ComputeDeterministicId's hash input above still
        // includes that random value -- the derived MVID/PE-timestamp differ across otherwise
        // identical builds. Roslyn avoids this by reserving a zeroed MVID blob upfront and
        // patching it in afterwards; PersistedAssemblyBuilder has no such hook, so we do the
        // same fixup after the fact: zero both stamp fields, hash the rest of the file, and
        // write the hash-derived MVID/timestamp back into the same two spots.
        private static void PatchDeterministicPeStamp(byte[] peBytes)
        {
            using var peReader = new PEReader(ImmutableArray.Create(peBytes));
            MetadataReader mdReader = peReader.GetMetadataReader();
            GuidHandle mvidHandle = mdReader.GetModuleDefinition().Mvid;

            int guidHeapStart = peReader.PEHeaders.MetadataStartOffset + mdReader.GetHeapMetadataOffset(HeapIndex.Guid);
            int guidIndex = MetadataTokens.GetHeapOffset(mvidHandle); // 1-based; each GUID heap entry is 16 bytes
            int mvidOffset = guidHeapStart + (guidIndex - 1) * 16;

            if (new Guid(peBytes.AsSpan(mvidOffset, 16)) != mdReader.GetGuid(mvidHandle))
                throw new InvalidOperationException(
                    "Nemerle.CoreEmit.Emitter: computed MVID offset does not match the module's " +
                    "own Mvid value -- offset math must be wrong on this runtime, refusing to patch.");

            int timeDateStampOffset = peReader.PEHeaders.CoffHeaderStartOffset + 4;

            Array.Clear(peBytes, mvidOffset, 16);
            Array.Clear(peBytes, timeDateStampOffset, 4);

            BlobContentId id = BlobContentId.FromHash(SHA1.HashData(peBytes));

            id.Guid.ToByteArray().CopyTo(peBytes, mvidOffset);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(peBytes.AsSpan(timeDateStampOffset, 4), id.Stamp);
        }

        /// <summary>
        /// Saves a PersistedAssemblyBuilder previously created by <see cref="CreateBuilder"/>
        /// to disk, replacing AssemblyBuilder.Save/SetEntryPoint/DefineVersionInfoResource
        /// (none of which exist on CoreCLR) with the GenerateMetadata + ManagedPEBuilder
        /// recipe. Every TypeBuilder defined in the assembly MUST have had CreateType()
        /// called on it already (bases/interfaces before derived types), otherwise
        /// GenerateMetadata throws NotSupportedException.
        /// </summary>
        /// <param name="ab">The (Persisted)AssemblyBuilder to save.</param>
        /// <param name="outputPath">Full path of the output file (.dll/.exe -- extension is cosmetic on core).</param>
        /// <param name="entryPointMethod">The Main method (as MethodBuilder, still un-tokenized) or null for a library.</param>
        /// <param name="isWinexe">true to mark the PE subsystem as Windows GUI instead of console.</param>
        /// <param name="embeddedResources">"name|absoluteFilePath" pairs for -res embedded resources, or null/empty.</param>
        /// <param name="emitRuntimeConfig">When true and entryPointMethod != null, also writes "&lt;basename&gt;.runtimeconfig.json" next to outputPath.</param>
        /// <param name="emitDebug">When true, also writes a standalone Portable PDB
        /// ("&lt;basename&gt;.pdb" next to outputPath) built from the documents / sequence
        /// points / local names recorded via ModuleBuilder.DefineDocument,
        /// ILGenerator.MarkSequencePoint and LocalBuilder.SetLocalSymInfo, and wires a
        /// CodeView entry for it into the PE's debug directory (ncc -debug). See
        /// dotnet-port\03-dotnet-runtime-facts.md recipe R4.</param>
        /// <param name="win32ResourceFile">Path to a Win32 .RES file (ncc -win32-resource),
        /// or null. Converted to a .rsrc section by <see cref="ResFileResourceSection"/> and
        /// passed as ManagedPEBuilder's nativeResources (see dotnet-port\17-resources-fixes-log.md).</param>
        /// <param name="linkedResources">"name|absoluteFilePath" pairs for -linkres linked
        /// (non-embedded) resources, or null/empty. Written as ECMA-335 File + ManifestResource
        /// table rows (metadata is verified spec-correct -- see 17-resources-fixes-log.md --
        /// but note System.Reflection.Assembly.GetManifestResourceInfo/GetManifestResourceStream
        /// return null for these on CoreCLR, a runtime limitation unrelated to this encoding;
        /// consistent with .NET Core's removal of multi-file-assembly support).</param>
        public static void Save(AssemblyBuilder ab, string outputPath, MethodInfo entryPointMethod,
                                 bool isWinexe, string[] embeddedResources, bool emitRuntimeConfig,
                                 bool emitDebug, string win32ResourceFile, string[] linkedResources)
        {
            var pab = (PersistedAssemblyBuilder)ab;

            BlobBuilder ilStream;
            BlobBuilder fieldData;
            MetadataBuilder pdbMetadata = null;
            MetadataBuilder metadata = emitDebug
                ? pab.GenerateMetadata(out ilStream, out fieldData, out pdbMetadata)
                : pab.GenerateMetadata(out ilStream, out fieldData);

            // Assembly-level attributes whose constructor is a local (TypeBuilder) type were
            // deferred by SetAssemblyCustomAttribute instead of being applied directly (see
            // that method's doc comment); write them into the metadata now that constructor
            // tokens are valid.
            FlushPendingAssemblyAttributes(ab, metadata);

            // Token timing: MethodBuilder.MetadataToken is only valid AFTER GenerateMetadata.
            MethodDefinitionHandle entryPointHandle = default;
            if (entryPointMethod != null)
                entryPointHandle = MetadataTokens.MethodDefinitionHandle(entryPointMethod.MetadataToken);

            BlobBuilder resourcesBlob = null;
            if (embeddedResources != null && embeddedResources.Length > 0)
            {
                resourcesBlob = new BlobBuilder();
                foreach (string entry in embeddedResources)
                {
                    int sep = entry.IndexOf('|');
                    if (sep < 0)
                        continue;
                    string name = entry.Substring(0, sep);
                    string filePath = entry.Substring(sep + 1);
                    byte[] data = File.ReadAllBytes(filePath);

                    resourcesBlob.Align(8);
                    uint offset = (uint)resourcesBlob.Count;
                    resourcesBlob.WriteInt32(data.Length);
                    resourcesBlob.WriteBytes(data);

                    metadata.AddManifestResource(
                        ManifestResourceAttributes.Public,
                        metadata.GetOrAddString(name),
                        implementation: default,
                        offset: offset);
                }
            }

            // Linked resources (-linkres): a File table row (with a content hash, as
            // csc/link.exe write) + a ManifestResource row whose Implementation points at
            // that File row instead of embedding the bytes. Spec-correct (verified via
            // System.Reflection.Metadata against a hand-built repro -- see
            // 17-resources-fixes-log.md) but NOTE: System.Reflection's own
            // Assembly.GetManifestResourceInfo/GetManifestResourceStream return null for
            // File-implementation resources on CoreCLR (confirmed independently of ncc);
            // this is a CoreCLR limitation (multi-file assemblies are not supported there),
            // not a defect in this encoding.
            if (linkedResources != null && linkedResources.Length > 0)
            {
                foreach (string entry in linkedResources)
                {
                    int sep = entry.IndexOf('|');
                    if (sep < 0)
                        continue;
                    string name = entry.Substring(0, sep);
                    string filePath = entry.Substring(sep + 1);
                    byte[] hash = System.Security.Cryptography.SHA1.HashData(File.ReadAllBytes(filePath));

                    AssemblyFileHandle fileHandle = metadata.AddAssemblyFile(
                        metadata.GetOrAddString(Path.GetFileName(filePath)),
                        metadata.GetOrAddBlob(hash),
                        containsMetadata: false);
                    metadata.AddManifestResource(
                        ManifestResourceAttributes.Public,
                        metadata.GetOrAddString(name),
                        implementation: fileHandle,
                        offset: 0);
                }
            }

            Characteristics characteristics = entryPointMethod != null
                ? Characteristics.ExecutableImage
                : (Characteristics.ExecutableImage | Characteristics.Dll);
            Subsystem subsystem = isWinexe ? Subsystem.WindowsGui : Subsystem.WindowsCui;

            var header = new PEHeaderBuilder(imageCharacteristics: characteristics, subsystem: subsystem);

            // Portable PDB (ncc -debug): serialize the PDB metadata collected by
            // GenerateMetadata's 3-out overload into a standalone .pdb next to the
            // output, and point a CodeView debug-directory entry at it. Must happen
            // before ManagedPEBuilder.Serialize (the PE embeds the PDB's BlobContentId).
            DebugDirectoryBuilder debugDir = null;
            BlobBuilder pdbBlob = null;
            string pdbPath = null;
            if (emitDebug)
            {
                pdbPath = Path.ChangeExtension(Path.GetFullPath(outputPath), ".pdb");
                var pdbBuilder = new PortablePdbBuilder(pdbMetadata, metadata.GetRowCounts(), entryPointHandle, idProvider: ComputeDeterministicId);
                pdbBlob = new BlobBuilder();
                BlobContentId pdbId = pdbBuilder.Serialize(pdbBlob);
                debugDir = new DebugDirectoryBuilder();
                debugDir.AddCodeViewEntry(pdbPath, pdbId, pdbBuilder.FormatVersion);
            }

            ResourceSectionBuilder nativeResources = win32ResourceFile != null
                ? ResFileResourceSection.FromFile(win32ResourceFile)
                : null;

            var peBuilder = new ManagedPEBuilder(
                header: header,
                metadataRootBuilder: new MetadataRootBuilder(metadata),
                ilStream: ilStream,
                mappedFieldData: fieldData,
                managedResources: resourcesBlob,
                nativeResources: nativeResources,
                debugDirectoryBuilder: debugDir,
                entryPoint: entryPointHandle,
                deterministicIdProvider: ComputeDeterministicId);

            var peBlob = new BlobBuilder();
            peBuilder.Serialize(peBlob);
            byte[] peBytes = peBlob.ToArray();
            PatchDeterministicPeStamp(peBytes);

            string dir = Path.GetDirectoryName(Path.GetFullPath(outputPath));
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            File.WriteAllBytes(outputPath, peBytes);

            if (emitDebug)
            {
                using (var fs = File.Create(pdbPath))
                    pdbBlob.WriteContentTo(fs);
            }

            if (entryPointMethod != null && emitRuntimeConfig)
            {
                string cfgPath = Path.Combine(
                    Path.GetDirectoryName(Path.GetFullPath(outputPath)) ?? ".",
                    Path.GetFileNameWithoutExtension(outputPath) + ".runtimeconfig.json");
                File.WriteAllText(cfgPath,
                    "{\n" +
                    "  \"runtimeOptions\": {\n" +
                    "    \"tfm\": \"net10.0\",\n" +
                    "    \"framework\": { \"name\": \"Microsoft.NETCore.App\", \"version\": \"10.0.0\" },\n" +
                    "    \"rollForward\": \"LatestMinor\"\n" +
                    "  }\n" +
                    "}\n");
            }
        }

        /// <summary>
        /// Extracts a strong-name PUBLIC key blob (suitable for AssemblyName.SetPublicKey)
        /// from a CAPI PRIVATEKEYBLOB .snk file (what "sn -k" produces), without using
        /// StrongNameKeyPair (ctors throw PlatformNotSupportedException on CoreCLR).
        /// If the file already IS a public key blob (e.g. produced by "sn -p"), it is
        /// returned unchanged.
        /// </summary>
        public static byte[] GetPublicKeyFromSnk(string snkPath)
        {
            byte[] snk = File.ReadAllBytes(snkPath);
            return GetPublicKeyFromSnkBytes(snk);
        }

        internal static byte[] GetPublicKeyFromSnkBytes(byte[] snk)
        {
            if (snk.Length < 20 + 8)
                throw new ArgumentException("key blob too short");

            if (snk[0] != 0x07) // not a CAPI PRIVATEKEYBLOB
            {
                if (snk.Length > 12 && BitConverter.ToUInt32(snk, 0) == 0x2400)
                    return snk; // already a strong-name public key blob
                throw new ArgumentException("not a CAPI private key blob (bType=" + snk[0] + ")");
            }

            uint magic = BitConverter.ToUInt32(snk, 8);
            if (magic != 0x32415352) // 'RSA2'
                throw new ArgumentException("magic != RSA2");

            int bitlen = BitConverter.ToInt32(snk, 12);
            uint pubexp = BitConverter.ToUInt32(snk, 16);
            int modLen = bitlen / 8;
            byte[] modulus = new byte[modLen];
            Buffer.BlockCopy(snk, 20, modulus, 0, modLen);

            int cb = 8 + 12 + modLen; // BLOBHEADER + RSAPUBKEY + modulus
            byte[] res = new byte[12 + cb];
            using (var w = new BinaryWriter(new MemoryStream(res)))
            {
                w.Write(0x00002400u); // SigAlgID  = CALG_RSA_SIGN
                w.Write(0x00008004u); // HashAlgID = CALG_SHA1
                w.Write(cb);
                w.Write((byte)0x06); w.Write((byte)0x02); w.Write((ushort)0); // PUBLICKEYBLOB header
                w.Write(0x00002400u); // aiKeyAlg
                w.Write(0x31415352u); // 'RSA1'
                w.Write(bitlen);
                w.Write(pubexp);
                w.Write(modulus);
            }
            return res;
        }

        /// <summary>Strong-name token: last 8 bytes of SHA1(publicKeyBlob), reversed.</summary>
        public static byte[] GetPublicKeyToken(byte[] publicKey)
        {
            byte[] hash = System.Security.Cryptography.SHA1.HashData(publicKey);
            byte[] token = new byte[8];
            for (int i = 0; i < 8; i++)
                token[i] = hash[hash.Length - 1 - i];
            return token;
        }
    }
}
