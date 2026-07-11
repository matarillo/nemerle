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
using System.IO;
using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace Nemerle.CoreEmit
{
    public static class Emitter
    {
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
        public static void Save(AssemblyBuilder ab, string outputPath, MethodInfo entryPointMethod,
                                 bool isWinexe, string[] embeddedResources, bool emitRuntimeConfig)
        {
            var pab = (PersistedAssemblyBuilder)ab;

            MetadataBuilder metadata = pab.GenerateMetadata(out BlobBuilder ilStream, out BlobBuilder fieldData);

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

            Characteristics characteristics = entryPointMethod != null
                ? Characteristics.ExecutableImage
                : (Characteristics.ExecutableImage | Characteristics.Dll);
            Subsystem subsystem = isWinexe ? Subsystem.WindowsGui : Subsystem.WindowsCui;

            var header = new PEHeaderBuilder(imageCharacteristics: characteristics, subsystem: subsystem);

            var peBuilder = new ManagedPEBuilder(
                header: header,
                metadataRootBuilder: new MetadataRootBuilder(metadata),
                ilStream: ilStream,
                mappedFieldData: fieldData,
                managedResources: resourcesBlob,
                entryPoint: entryPointHandle);

            var peBlob = new BlobBuilder();
            peBuilder.Serialize(peBlob);

            string dir = Path.GetDirectoryName(Path.GetFullPath(outputPath));
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            using (var fs = File.Create(outputPath))
                peBlob.WriteContentTo(fs);

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
