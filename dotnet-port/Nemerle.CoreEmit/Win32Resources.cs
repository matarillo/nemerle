// Nemerle.CoreEmit.Win32Resources: minimal RES (Win32 "resource file") -> COFF .rsrc
// section converter for the CoreCLR emission path.
//
// ncc's `-win32-resource`/`-win32res` (Win32 .res file) support used
// AssemblyBuilder.DefineVersionInfoResource/ModuleBuilder.DefineUnmanagedResource on
// .NET Framework (see HierarchyEmitter.add_resources_to_assembly_clr4) -- neither exists
// on CoreCLR. PersistedAssemblyBuilder/ManagedPEBuilder instead accept a pre-built
// ResourceSectionBuilder (the ".rsrc" section content) directly, so this file implements
// the RES -> .rsrc conversion by hand (a compact analogue of Roslyn's CvtResFile /
// Cci.ResourceSection), targeting exactly the single-.res-file, no-version-info-fallback
// case ncc needs. See dotnet-port\docs\17-resources-fixes-log.md for the design notes and
// dotnet-port\docs\03-dotnet-runtime-facts.md / 13-stage2-log.md for prior CoreCLR findings
// this build on (e.g. legacy Reflection.Emit unmanaged-resource APIs are CLR4-only).
//
// RES file format (informal but stable, matches what rc.exe/cvtres.exe produce and what
// link.exe consumes): a sequence of "resource" records, each:
//   DWORD DataSize
//   DWORD HeaderSize            (size of the variable-length header that follows, incl. these two DWORDs? no -- excludes them; see below)
//   [Type]                       WORD 0xFFFF + WORD id, OR a null-terminated UTF-16LE string
//   [Name]                       same encoding as Type
//   (padding to the next DWORD boundary)
//   DWORD DataVersion
//   WORD  MemoryFlags
//   WORD  LanguageId
//   DWORD Version
//   DWORD Characteristics
//   <DataSize bytes of raw resource data>
//   (padding to the next DWORD boundary)
// The very first record in a 32-bit .RES file is conventionally an empty "signature"
// record (DataSize=0, Type=id 0, Name=id 0, everything else 0) -- present here purely as
// a marker for tools; the parser below tolerates its presence or absence.
//
// .rsrc section format (ECMA-335 has no section on this -- it's plain Win32/PE, see the
// Microsoft PE/COFF specification, "The .rsrc Section"): a 3-level tree of
// IMAGE_RESOURCE_DIRECTORY nodes (Type -> Name -> Language), each followed by its
// IMAGE_RESOURCE_DIRECTORY_ENTRY array (named entries first, sorted ordinal-ignore-case;
// then ID entries, sorted ascending), terminating in IMAGE_RESOURCE_DATA_ENTRY leaves
// that point (by RVA) at the raw resource bytes. Named Type/Name keys are stored in a
// side string table (2-byte UTF-16 code-unit count + the UTF-16LE characters,
// unterminated).

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace Nemerle.CoreEmit
{
    /// <summary>
    /// A resource key at the Type or Name level of the .rsrc tree: either a 16-bit
    /// numeric ID or a name string (mutually exclusive, exactly like IMAGE_RESOURCE_DIRECTORY_ENTRY.Name).
    /// </summary>
    internal readonly struct ResKey : IComparable<ResKey>
    {
        public readonly bool IsId;
        public readonly ushort Id;
        public readonly string Name;

        public ResKey(ushort id) { IsId = true; Id = id; Name = null; }
        public ResKey(string name) { IsId = false; Id = 0; Name = name; }

        public override bool Equals(object obj) =>
            obj is ResKey other && IsId == other.IsId && Id == other.Id &&
            string.Equals(Name, other.Name, StringComparison.OrdinalIgnoreCase);

        public override int GetHashCode() =>
            IsId ? Id.GetHashCode() : StringComparer.OrdinalIgnoreCase.GetHashCode(Name ?? "");

        // Win32 resource directory entries are sorted: named entries first (ordinal,
        // case-insensitive), then ID entries (ascending numeric).
        public int CompareTo(ResKey other)
        {
            if (!IsId && other.IsId) return -1;
            if (IsId && !other.IsId) return 1;
            return IsId ? Id.CompareTo(other.Id) : string.Compare(Name, other.Name, StringComparison.OrdinalIgnoreCase);
        }
    }

    internal sealed class ResEntry
    {
        public ResKey Type;
        public ResKey Name;
        public ushort Language;
        public byte[] Data;
    }

    /// <summary>
    /// Parses a 32-bit Win32 .RES file (as produced by rc.exe/cvtres.exe, or by ncc's own
    /// -win32-resource on .NET Framework) and serializes it as a .rsrc
    /// <see cref="ResourceSectionBuilder"/> for <see cref="System.Reflection.Metadata.Ecma335.ManagedPEBuilder"/>'s
    /// nativeResources parameter.
    /// </summary>
    public sealed class ResFileResourceSection : ResourceSectionBuilder
    {
        private readonly List<ResEntry> _entries;

        public ResFileResourceSection(byte[] resFileBytes)
        {
            _entries = Parse(resFileBytes);
        }

        public static ResFileResourceSection FromFile(string path) =>
            new ResFileResourceSection(File.ReadAllBytes(path));

        private static List<ResEntry> Parse(byte[] bytes)
        {
            var result = new List<ResEntry>();
            int pos = 0;
            while (pos < bytes.Length)
            {
                if (pos + 8 > bytes.Length)
                    break; // trailing padding/garbage -- ignore
                int entryStart = pos;
                uint dataSize = ReadU32(bytes, ref pos);
                uint headerSize = ReadU32(bytes, ref pos);
                // HeaderSize is measured from the start of the entry, i.e. it INCLUDES the
                // two DataSize/HeaderSize DWORDs just read (verified against a real
                // rc.exe/cvtres.exe-produced .res: header total 32 bytes for a fixed-size
                // numeric type+name, of which only 24 follow these two fields).
                int headerStart = entryStart;

                ResKey type = ReadTypeOrName(bytes, ref pos);
                ResKey name = ReadTypeOrName(bytes, ref pos);
                AlignTo4(ref pos);

                _ = ReadU32(bytes, ref pos); // DataVersion
                _ = ReadU16(bytes, ref pos); // MemoryFlags
                ushort language = ReadU16(bytes, ref pos);
                _ = ReadU32(bytes, ref pos); // Version
                _ = ReadU32(bytes, ref pos); // Characteristics

                int consumedHeader = pos - headerStart;
                if (consumedHeader != headerSize)
                {
                    // Tolerate minor deviations (e.g. a tool that padded differently) by
                    // trusting HeaderSize as authoritative for where data starts -- but never
                    // let a malformed/garbage HeaderSize (e.g. 0, from mis-parsed input) send
                    // us backwards or keep us in place, which would spin forever.
                    int corrected = headerStart + (int)headerSize;
                    if (corrected <= entryStart)
                        throw new InvalidDataException(
                            $"malformed .RES file: entry at offset {entryStart} has an implausible HeaderSize ({headerSize})");
                    pos = corrected;
                }

                if ((long)pos + dataSize > bytes.Length)
                    throw new InvalidDataException(
                        $"malformed .RES file: entry at offset {entryStart} claims DataSize {dataSize} but only {bytes.Length - pos} bytes remain");

                byte[] data = new byte[dataSize];
                Array.Copy(bytes, pos, data, 0, (int)dataSize);
                pos += (int)dataSize;
                AlignTo4(ref pos);

                bool isSignatureEntry = dataSize == 0 && type.IsId && type.Id == 0 && name.IsId && name.Id == 0;
                if (!isSignatureEntry)
                    result.Add(new ResEntry { Type = type, Name = name, Language = language, Data = data });
            }
            return result;
        }

        private static uint ReadU32(byte[] b, ref int pos)
        {
            uint v = BitConverter.ToUInt32(b, pos);
            pos += 4;
            return v;
        }

        private static ushort ReadU16(byte[] b, ref int pos)
        {
            ushort v = BitConverter.ToUInt16(b, pos);
            pos += 2;
            return v;
        }

        private static void AlignTo4(ref int pos)
        {
            while (pos % 4 != 0) pos++;
        }

        private static ResKey ReadTypeOrName(byte[] b, ref int pos)
        {
            ushort marker = BitConverter.ToUInt16(b, pos);
            if (marker == 0xFFFF)
            {
                ushort id = BitConverter.ToUInt16(b, pos + 2);
                pos += 4;
                return new ResKey(id);
            }
            else
            {
                int start = pos;
                while (BitConverter.ToUInt16(b, pos) != 0) pos += 2;
                string s = System.Text.Encoding.Unicode.GetString(b, start, pos - start);
                pos += 2; // null terminator
                return new ResKey(s);
            }
        }

        protected override void Serialize(BlobBuilder builder, SectionLocation location)
        {
            // Group into the 3-level tree: Type -> Name -> Language -> data.
            var byType = new SortedDictionary<ResKey, SortedDictionary<ResKey, SortedDictionary<ushort, byte[]>>>();
            foreach (ResEntry e in _entries)
            {
                if (!byType.TryGetValue(e.Type, out var byName))
                    byType[e.Type] = byName = new SortedDictionary<ResKey, SortedDictionary<ushort, byte[]>>();
                if (!byName.TryGetValue(e.Name, out var byLang))
                    byName[e.Name] = byLang = new SortedDictionary<ushort, byte[]>();
                byLang[e.Language] = e.Data; // last one wins on an (exact) duplicate key
            }

            // ---- pass 1: lay out every fixed-size structure and compute its offset
            // (relative to the start of this .rsrc section / blob). ----
            const int DirHeaderSize = 16;
            const int DirEntrySize = 8;
            const int DataEntrySize = 16;

            int typeCount = byType.Count;
            int nameCount = byType.Values.Sum(byName => byName.Count);
            int langCount = byType.Values.SelectMany(byName => byName.Values).Sum(byLang => byLang.Count);

            int offset = 0;
            int typeDirOffset = offset; offset += DirHeaderSize + typeCount * DirEntrySize;

            var nameDirOffsetByType = new Dictionary<ResKey, int>();
            foreach (var (typeKey, byName) in byType)
            {
                nameDirOffsetByType[typeKey] = offset;
                offset += DirHeaderSize + byName.Count * DirEntrySize;
            }

            var langDirOffsetByTypeName = new Dictionary<(ResKey, ResKey), int>();
            foreach (var (typeKey, byName) in byType)
                foreach (var (nameKey, byLang) in byName)
                {
                    langDirOffsetByTypeName[(typeKey, nameKey)] = offset;
                    offset += DirHeaderSize + byLang.Count * DirEntrySize;
                }

            // Data entries, in the same (type, name, lang) traversal order used everywhere else.
            var leaves = new List<(ResKey type, ResKey name, ushort lang, byte[] data, int dataEntryOffset)>();
            foreach (var (typeKey, byName) in byType)
                foreach (var (nameKey, byLang) in byName)
                    foreach (var (lang, data) in byLang)
                    {
                        leaves.Add((typeKey, nameKey, lang, data, offset));
                        offset += DataEntrySize;
                    }

            // String table: one entry per named Type/Name key actually used (referenced by
            // directory-entry offset, so duplicates across different directories each get
            // their own copy here -- simplest and perfectly legal, just slightly less
            // compact than full de-duplication).
            var stringOffsetByType = new Dictionary<ResKey, int>();
            var stringOffsetByTypeName = new Dictionary<(ResKey, ResKey), int>();
            foreach (var (typeKey, byName) in byType)
            {
                if (!typeKey.IsId)
                {
                    stringOffsetByType[typeKey] = offset;
                    offset += 2 + typeKey.Name.Length * 2;
                }
                foreach (var nameKey in byName.Keys)
                {
                    if (!nameKey.IsId)
                    {
                        stringOffsetByTypeName[(typeKey, nameKey)] = offset;
                        offset += 2 + nameKey.Name.Length * 2;
                    }
                }
            }

            // Raw data, 4-byte aligned, one block per leaf (in the same order as `leaves`).
            var dataOffsets = new int[leaves.Count];
            for (int i = 0; i < leaves.Count; i++)
            {
                offset = Align(offset, 4);
                dataOffsets[i] = offset;
                offset += leaves[i].data.Length;
            }
            offset = Align(offset, 4);

            // ---- pass 2: emit. ----
            var buf = new byte[offset];

            void WriteU16(int at, ushort v) { buf[at] = (byte)v; buf[at + 1] = (byte)(v >> 8); }
            void WriteU32(int at, uint v)
            {
                buf[at] = (byte)v; buf[at + 1] = (byte)(v >> 8);
                buf[at + 2] = (byte)(v >> 16); buf[at + 3] = (byte)(v >> 24);
            }

            void WriteDirHeader(int at, int numNamed, int numId)
            {
                WriteU32(at, 0);       // Characteristics
                WriteU32(at + 4, 0);   // TimeDateStamp
                WriteU16(at + 8, 0);   // MajorVersion
                WriteU16(at + 10, 0);  // MinorVersion
                WriteU16(at + 12, (ushort)numNamed);
                WriteU16(at + 14, (ushort)numId);
            }

            void WriteDirEntry(int at, uint nameField, uint offsetField) { WriteU32(at, nameField); WriteU32(at + 4, offsetField); }

            // Type-level directory + entries.
            {
                int namedCount = byType.Keys.Count(k => !k.IsId);
                WriteDirHeader(typeDirOffset, namedCount, typeCount - namedCount);
                int entryAt = typeDirOffset + DirHeaderSize;
                foreach (ResKey typeKey in byType.Keys) // SortedDictionary already yields named-then-id, each ordinal
                {
                    uint nameField = typeKey.IsId ? (uint)typeKey.Id : (0x80000000u | (uint)stringOffsetByType[typeKey]);
                    uint subdirField = 0x80000000u | (uint)nameDirOffsetByType[typeKey];
                    WriteDirEntry(entryAt, nameField, subdirField);
                    entryAt += DirEntrySize;
                }
            }

            // Name-level directories + entries, one per type.
            foreach (var (typeKey, byName) in byType)
            {
                int dirAt = nameDirOffsetByType[typeKey];
                int namedCount = byName.Keys.Count(k => !k.IsId);
                WriteDirHeader(dirAt, namedCount, byName.Count - namedCount);
                int entryAt = dirAt + DirHeaderSize;
                foreach (ResKey nameKey in byName.Keys)
                {
                    uint nameField = nameKey.IsId ? (uint)nameKey.Id : (0x80000000u | (uint)stringOffsetByTypeName[(typeKey, nameKey)]);
                    uint subdirField = 0x80000000u | (uint)langDirOffsetByTypeName[(typeKey, nameKey)];
                    WriteDirEntry(entryAt, nameField, subdirField);
                    entryAt += DirEntrySize;
                }
            }

            // Language-level directories + entries (leaves point at data entries, not subdirectories).
            {
                int leafIndex = 0;
                foreach (var (typeKey, byName) in byType)
                    foreach (var (nameKey, byLang) in byName)
                    {
                        int dirAt = langDirOffsetByTypeName[(typeKey, nameKey)];
                        // Language IDs are numeric-only (never named) in the resource format.
                        WriteDirHeader(dirAt, 0, byLang.Count);
                        int entryAt = dirAt + DirHeaderSize;
                        foreach (ushort lang in byLang.Keys)
                        {
                            int dataEntryOffset = leaves[leafIndex].dataEntryOffset;
                            WriteDirEntry(entryAt, lang, (uint)dataEntryOffset); // high bit clear: points at a data entry, not a subdirectory
                            entryAt += DirEntrySize;
                            leafIndex++;
                        }
                    }
            }

            // Data entries: OffsetToData is an absolute RVA (section RVA + in-section offset).
            for (int i = 0; i < leaves.Count; i++)
            {
                int at = leaves[i].dataEntryOffset;
                WriteU32(at, (uint)(location.RelativeVirtualAddress + dataOffsets[i])); // OffsetToData (RVA)
                WriteU32(at + 4, (uint)leaves[i].data.Length);                          // Size
                WriteU32(at + 8, 1200);                                                 // CodePage (1200 = UTF-16LE; arbitrary but conventional)
                WriteU32(at + 12, 0);                                                   // Reserved
            }

            // String table (UTF-16LE, 2-byte length prefix counting UTF-16 code units, no terminator).
            foreach (var (typeKey, at) in stringOffsetByType)
            {
                WriteU16(at, (ushort)typeKey.Name.Length);
                System.Text.Encoding.Unicode.GetBytes(typeKey.Name, 0, typeKey.Name.Length, buf, at + 2);
            }
            foreach (var (key, at) in stringOffsetByTypeName)
            {
                string s = key.Item2.Name;
                WriteU16(at, (ushort)s.Length);
                System.Text.Encoding.Unicode.GetBytes(s, 0, s.Length, buf, at + 2);
            }

            // Raw resource data.
            for (int i = 0; i < leaves.Count; i++)
                Array.Copy(leaves[i].data, 0, buf, dataOffsets[i], leaves[i].data.Length);

            builder.WriteBytes(buf);
        }

        private static int Align(int value, int alignment) => (value + alignment - 1) / alignment * alignment;
    }
}
