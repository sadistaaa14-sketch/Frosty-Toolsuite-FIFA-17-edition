using FrostySdk.Interfaces;
using FrostySdk.IO;
using FrostySdk.Managers;
using FrostySdk.Resources;
using System.IO;
using System.Text;
using System;
using FrostySdk;
using System.Collections.Generic;
using Frosty.Core;

namespace BundleRefTablePlugin
{
    public class BundleRefTableResource : Resource
    {
        public string Name;

        public ModifiedBundleRefTableResource modResource = null;

        // Parsed data (for ContainsAsset lookups)
        public List<AssetLookup> assetLookups;
        public List<Asset> assets;

        // Raw binary preserved from Read() — SaveBytes works on this directly
        private byte[] rawData;

        // Parsed header values (stored pointers, NOT file offsets)
        private ulong namePtr;
        private ulong alPtr;
        private ulong brPtr;
        private ulong assetsPtr;
        private ulong bundlesPtr;
        private ulong emptyPtr;
        private uint alCount;
        private uint brCount;
        private uint assetCount;
        private uint unkHash;
        private uint bundleCountActual;

        public class AssetLookup
        {
            public uint Hash { get; set; }
            public uint BundleRefIndex { get; set; }
            public uint AssetIndex { get; set; }
        }

        public class Asset
        {
            public string Name { get; set; }
            public string Path { get; set; }
        }

        public BundleRefTableResource()
        {
        }

        public override void Read(NativeReader reader, AssetManager am, ResAssetEntry entry, ModifiedResource modifiedData)
        {
            base.Read(reader, am, entry, modifiedData);

            if (modifiedData != null && modifiedData is ModifiedBundleRefTableResource)
            {
                modResource = modifiedData as ModifiedBundleRefTableResource;
            }

            // Store entire raw resource bytes
            long origPos = reader.Position;
            reader.Position = 0;
            rawData = reader.ReadBytes((int)reader.Length);
            reader.Position = origPos;

            // Parse header (reader starts at 0 = stored offset 0x00, file offset 0x10)
            reader.Position = 0;
            namePtr = reader.ReadULong();
            alPtr = reader.ReadULong();
            brPtr = reader.ReadULong();
            assetsPtr = reader.ReadULong();
            bundlesPtr = reader.ReadULong();
            emptyPtr = reader.ReadULong();

            alCount = reader.ReadUInt();
            brCount = reader.ReadUInt();
            assetCount = reader.ReadUInt();
            reader.Position += 4;
            unkHash = reader.ReadUInt();
            reader.Position += 4;
            reader.Position += 4;
            reader.Position += 4;

            // Compute bundle count from gap between bundles and reloc
            uint relocOffset = BitConverter.ToUInt32(resMeta, 0);
            bundleCountActual = (relocOffset - (uint)bundlesPtr) / 16;

            // Read BRT name string
            Name = ReadCStr(rawData, (int)namePtr);

            // Parse asset lookups (for ContainsAsset)
            assetLookups = new List<AssetLookup>();
            long alStart = (long)alPtr;
            for (int i = 0; i < alCount; i++)
            {
                long off = alStart + i * 12;
                assetLookups.Add(new AssetLookup
                {
                    Hash = BitConverter.ToUInt32(rawData, (int)off),
                    BundleRefIndex = BitConverter.ToUInt32(rawData, (int)off + 4),
                    AssetIndex = BitConverter.ToUInt32(rawData, (int)off + 8)
                });
            }

            // Parse assets (for ContainsAsset and display)
            assets = new List<Asset>();
            long assStart = (long)assetsPtr;
            for (int i = 0; i < assetCount; i++)
            {
                long off = assStart + i * 16;
                ulong np = BitConverter.ToUInt64(rawData, (int)off);
                ulong pp = BitConverter.ToUInt64(rawData, (int)off + 8);
                assets.Add(new Asset
                {
                    Name = ReadCStr(rawData, (int)np),
                    Path = ReadCStr(rawData, (int)pp)
                });
            }
        }

        /// <summary>
        /// Saves the BRT binary following add_asset.py's approach:
        /// 1. Preserve original string table in order
        /// 2. Insert new strings before the BRT name
        /// 3. Rebuild sections with pointer updates
        /// 4. Append new entries at the end of each section
        /// </summary>
        public override byte[] SaveBytes()
        {
            if (rawData == null)
                throw new InvalidOperationException("No raw BRT data available");

            // ── 1. Find string table boundaries ─────────────────────────
            // Strings start at stored offset 0x50 (file 0x60)
            int strStart = 0x50;

            // Find the BRT name string — it's the last string in the table
            int brtNameStart = FindString(rawData, Name, strStart);
            if (brtNameStart < 0)
                throw new InvalidOperationException("BRT name string not found: " + Name);

            int brtNameEnd = brtNameStart + Encoding.ASCII.GetByteCount(Name) + 1; // +1 for null

            // ── 2. Collect new strings to insert ────────────────────────
            // New entries from AddDupeEntry modifications
            // We need strings for new asset names and paths
            List<string> newStrings = new List<string>();
            HashSet<string> newStringSet = new HashSet<string>();
            Dictionary<string, int> newStringOffsets = new Dictionary<string, int>();

            int newAssetStartIndex = (int)assetCount; // original asset count
            for (int i = newAssetStartIndex; i < assets.Count; i++)
            {
                string name = assets[i].Name;
                string path = assets[i].Path;
                if (!HasString(rawData, name, strStart, brtNameStart) && !newStringSet.Contains(name))
                {
                    newStrings.Add(name);
                    newStringSet.Add(name);
                }
                if (!HasString(rawData, path, strStart, brtNameStart) && !newStringSet.Contains(path))
                {
                    newStrings.Add(path);
                    newStringSet.Add(path);
                }
            }

            // Calculate inserted bytes
            byte[] newStrBytes = BuildStringBlock(newStrings);
            int insertLen = newStrBytes.Length;

            // Build string offset map for new strings
            int insertPos = brtNameStart; // insert before BRT name
            for (int i = 0, off = insertPos; i < newStrings.Count; i++)
            {
                newStringOffsets[newStrings[i]] = off;
                off += Encoding.ASCII.GetByteCount(newStrings[i]) + 1;
            }

            // ── 3. Build new string table ───────────────────────────────
            byte[] preStrings = new byte[brtNameStart - strStart];
            Array.Copy(rawData, strStart, preStrings, 0, preStrings.Length);

            byte[] brtNameBytes = new byte[brtNameEnd - brtNameStart];
            Array.Copy(rawData, brtNameStart, brtNameBytes, 0, brtNameBytes.Length);

            byte[] newStrtab = Concat(preStrings, newStrBytes, brtNameBytes);
            // Pad to 16
            int padLen = (16 - ((strStart + newStrtab.Length) % 16)) % 16;
            byte[] strtabPadded = new byte[newStrtab.Length + padLen];
            Array.Copy(newStrtab, strtabPadded, newStrtab.Length);

            // ── 4. Pointer update function ──────────────────────────────
            // All existing stored pointers stay the same EXCEPT the BRT name
            // which shifts by insertLen
            int brtNameStoredPtr = brtNameStart;
            Func<ulong, ulong> updatePtr = (old) =>
            {
                if ((int)old == brtNameStoredPtr)
                    return (ulong)(brtNameStoredPtr + insertLen);
                return old;
            };

            // ── 5. Section layout ───────────────────────────────────────
            int newBrCount = (int)brCount;
            int newAssetCount = assets.Count;
            int newAlCount = assetLookups.Count;

            int strtabResStart = 0x50;
            int newBrRes = strtabResStart + strtabPadded.Length;
            int newAssetsRes = newBrRes + (newBrCount * 24);
            int newAlRes = newAssetsRes + (newAssetCount * 16);
            int alEndRes = newAlRes + (newAlCount * 12);
            int alPadLen2 = (16 - (alEndRes % 16)) % 16;
            int newBundlesRes = alEndRes + alPadLen2;
            int newRelocRes = newBundlesRes + ((int)bundleCountActual * 16);

            // ── 6. Build sections from raw data + new entries ───────────

            // Bundle Refs (unchanged, just update pointers)
            byte[] newBrBytes = new byte[newBrCount * 24];
            for (int i = 0; i < brCount; i++)
            {
                int srcOff = (int)brPtr + i * 24;
                ulong p0 = updatePtr(BitConverter.ToUInt64(rawData, srcOff));
                ulong p1 = updatePtr(BitConverter.ToUInt64(rawData, srcOff + 8));
                // Bundle pointer: remap to new bundles offset
                ulong oldBundlePtr = BitConverter.ToUInt64(rawData, srcOff + 16);
                uint bi = (uint)(oldBundlePtr - bundlesPtr) / 16;
                ulong p2 = (ulong)newBundlesRes + bi * 16;

                int dstOff = i * 24;
                WriteU64(newBrBytes, dstOff, p0);
                WriteU64(newBrBytes, dstOff + 8, p1);
                WriteU64(newBrBytes, dstOff + 16, p2);
            }

            // Assets (original + new)
            byte[] newAssetsBytes = new byte[newAssetCount * 16];
            for (int i = 0; i < assetCount; i++)
            {
                int srcOff = (int)assetsPtr + i * 16;
                ulong p0 = updatePtr(BitConverter.ToUInt64(rawData, srcOff));
                ulong p1 = updatePtr(BitConverter.ToUInt64(rawData, srcOff + 8));

                int dstOff = i * 16;
                WriteU64(newAssetsBytes, dstOff, p0);
                WriteU64(newAssetsBytes, dstOff + 8, p1);
            }
            // Append new assets
            for (int i = (int)assetCount; i < assets.Count; i++)
            {
                ulong np = (ulong)GetStringPtr(rawData, assets[i].Name, strStart, brtNameStart, newStringOffsets);
                ulong pp = (ulong)GetStringPtr(rawData, assets[i].Path, strStart, brtNameStart, newStringOffsets);

                int dstOff = i * 16;
                WriteU64(newAssetsBytes, dstOff, np);
                WriteU64(newAssetsBytes, dstOff + 8, pp);
            }

            // Asset Lookups (sorted by hash)
            assetLookups.Sort((a, b) => a.Hash.CompareTo(b.Hash));
            byte[] newAlBytes = new byte[newAlCount * 12];
            for (int i = 0; i < newAlCount; i++)
            {
                int dstOff = i * 12;
                WriteU32(newAlBytes, dstOff, assetLookups[i].Hash);
                WriteU32(newAlBytes, dstOff + 4, assetLookups[i].BundleRefIndex);
                WriteU32(newAlBytes, dstOff + 8, assetLookups[i].AssetIndex);
            }

            // AL padding
            byte[] alPad = new byte[alPadLen2];

            // Bundles (unchanged, just update pointers)
            byte[] newBundlesBytes = new byte[(int)bundleCountActual * 16];
            for (int i = 0; i < bundleCountActual; i++)
            {
                int srcOff = (int)bundlesPtr + i * 16;
                ulong p0 = updatePtr(BitConverter.ToUInt64(rawData, srcOff));
                uint parentIdx = (uint)(BitConverter.ToUInt64(rawData, srcOff + 8) - bundlesPtr) / 16;
                ulong p1 = (ulong)newBundlesRes + parentIdx * 16;

                int dstOff = i * 16;
                WriteU64(newBundlesBytes, dstOff, p0);
                WriteU64(newBundlesBytes, dstOff + 8, p1);
            }

            // ── 7. Reloc table ──────────────────────────────────────────
            List<uint> reloc = new List<uint>();
            reloc.Add(0x00); reloc.Add(0x08); reloc.Add(0x10);
            reloc.Add(0x18); reloc.Add(0x20); reloc.Add(0x28);

            for (int i = 0; i < newBrCount; i++)
            {
                uint b = (uint)(newBrRes + i * 24);
                reloc.Add(b); reloc.Add(b + 8); reloc.Add(b + 16);
            }
            for (int i = 0; i < newAssetCount; i++)
            {
                uint b = (uint)(newAssetsRes + i * 16);
                reloc.Add(b); reloc.Add(b + 8);
            }
            for (int i = 0; i < bundleCountActual; i++)
            {
                uint b = (uint)(newBundlesRes + i * 16);
                reloc.Add(b); reloc.Add(b + 8);
            }
            reloc.Sort();

            byte[] relocBytes = new byte[reloc.Count * 4];
            for (int i = 0; i < reloc.Count; i++)
                WriteU32(relocBytes, i * 4, reloc[i]);

            // ── 8. Header ───────────────────────────────────────────────
            byte[] header = new byte[0x50];
            Array.Copy(rawData, 0, header, 0, 0x50);

            WriteU64(header, 0x00, updatePtr(namePtr));
            WriteU64(header, 0x08, (ulong)newAlRes);
            WriteU64(header, 0x10, (ulong)newBrRes);
            WriteU64(header, 0x18, (ulong)newAssetsRes);
            WriteU64(header, 0x20, (ulong)newBundlesRes);
            WriteU64(header, 0x28, updatePtr(emptyPtr));
            WriteU32(header, 0x30, (uint)newAlCount);
            WriteU32(header, 0x34, (uint)newBrCount);
            WriteU32(header, 0x38, (uint)newAssetCount);

            // ── 9. ResMeta ──────────────────────────────────────────────
            byte[] relocOffBytes = BitConverter.GetBytes((uint)newRelocRes);
            byte[] relocSizeBytes = BitConverter.GetBytes((uint)relocBytes.Length);
            relocOffBytes.CopyTo(resMeta, 0);
            relocSizeBytes.CopyTo(resMeta, 4);

            // ── 10. Assemble ────────────────────────────────────────────
            return Concat(header, strtabPadded, newBrBytes, newAssetsBytes,
                         newAlBytes, alPad, newBundlesBytes, relocBytes);
        }

        public void ApplyModifiedResource(ModifiedResource inModResource)
        {
            modResource = inModResource as ModifiedBundleRefTableResource;

            foreach (KeyValuePair<string, string> kvp in modResource.DuplicationDict)
            {
                AddDupeEntry(kvp.Key, kvp.Value);
            }
        }

        public override ModifiedResource SaveModifiedResource()
        {
            return modResource;
        }

        public bool ContainsAsset(string assetPath)
        {
            uint hash = BRTUtils.Fnv1Hash32(assetPath);
            for (int i = 0; i < assetLookups.Count; i++)
            {
                if (assetLookups[i].Hash == hash)
                    return true;
            }
            return false;
        }

        public bool DupeAsset(string newAssetPath, string existingAssetPath)
        {
            if (modResource == null)
                modResource = new ModifiedBundleRefTableResource();

            return modResource.AddAsset(newAssetPath, existingAssetPath);
        }

        public bool RevertDupe(string newAssetPath)
        {
            if (modResource == null)
                return false;

            return modResource.RemoveAsset(newAssetPath);
        }

        public bool AddDupeEntry(string newAssetPath, string existingAssetPath)
        {
            uint oldHash = BRTUtils.Fnv1Hash32(existingAssetPath);
            uint newHashFull = BRTUtils.Fnv1Hash32(newAssetPath);
            uint newHashName = BRTUtils.Fnv1Hash32(newAssetPath.Substring(newAssetPath.LastIndexOf("/") + 1));

            // Remove existing entries with new hashes (reverse iterate)
            List<int> indicesToRemove = new List<int>();
            for (int i = 0; i < assetLookups.Count; i++)
            {
                if (assetLookups[i].Hash == newHashFull || assetLookups[i].Hash == newHashName)
                    indicesToRemove.Add(i);
            }
            for (int idx = indicesToRemove.Count - 1; idx >= 0; idx--)
                assetLookups.RemoveAt(indicesToRemove[idx]);

            // Find existing hash
            for (int i = 0; i < assetLookups.Count; i++)
            {
                if (assetLookups[i].Hash == oldHash)
                {
                    uint bri = assetLookups[i].BundleRefIndex;

                    Asset newAsset = new Asset();
                    newAsset.Name = newAssetPath.Substring(newAssetPath.LastIndexOf("/") + 1);
                    newAsset.Path = newAssetPath.Substring(0, newAssetPath.LastIndexOf("/")).Trim('/');

                    // Check if asset already exists
                    uint ai = 0;
                    bool found = false;
                    for (int j = 0; j < assets.Count; j++)
                    {
                        if (assets[j].Name == newAsset.Name && assets[j].Path == newAsset.Path)
                        {
                            ai = (uint)j;
                            found = true;
                            break;
                        }
                    }

                    if (!found)
                    {
                        assets.Add(newAsset);
                        ai = (uint)(assets.Count - 1);
                    }

                    assetLookups.Add(new AssetLookup { Hash = newHashFull, BundleRefIndex = bri, AssetIndex = ai });
                    assetLookups.Add(new AssetLookup { Hash = newHashName, BundleRefIndex = bri, AssetIndex = ai });
                    return true;
                }
            }

            return false;
        }

        // ── Helpers ─────────────────────────────────────────────────────────

        private static string ReadCStr(byte[] data, int offset)
        {
            if (offset < 0 || offset >= data.Length) return "";
            int end = offset;
            while (end < data.Length && data[end] != 0) end++;
            return Encoding.ASCII.GetString(data, offset, end - offset);
        }

        private static int FindString(byte[] data, string target, int searchStart)
        {
            byte[] needle = Encoding.ASCII.GetBytes(target);
            for (int i = searchStart; i <= data.Length - needle.Length - 1; i++)
            {
                if (i > searchStart && data[i - 1] != 0) continue; // must be start of string
                bool match = true;
                for (int j = 0; j < needle.Length; j++)
                {
                    if (data[i + j] != needle[j]) { match = false; break; }
                }
                if (match && data[i + needle.Length] == 0) return i;
            }
            return -1;
        }

        private static bool HasString(byte[] data, string target, int searchStart, int searchEnd)
        {
            byte[] needle = Encoding.ASCII.GetBytes(target);
            for (int i = searchStart; i <= searchEnd - needle.Length; i++)
            {
                if (i > searchStart && data[i - 1] != 0) continue;
                bool match = true;
                for (int j = 0; j < needle.Length; j++)
                {
                    if (data[i + j] != needle[j]) { match = false; break; }
                }
                if (match && data[i + needle.Length] == 0) return i >= 0;
            }
            return false;
        }

        private static int GetStringPtr(byte[] data, string target, int searchStart, int brtNameStart,
            Dictionary<string, int> newOffsets)
        {
            if (newOffsets.ContainsKey(target)) return newOffsets[target];

            int off = FindString(data, target, searchStart);
            if (off >= 0 && off < brtNameStart) return off;

            throw new InvalidOperationException("String not found in BRT: " + target);
        }

        private static byte[] BuildStringBlock(List<string> strings)
        {
            using (MemoryStream ms = new MemoryStream())
            {
                foreach (string s in strings)
                {
                    byte[] b = Encoding.ASCII.GetBytes(s);
                    ms.Write(b, 0, b.Length);
                    ms.WriteByte(0);
                }
                return ms.ToArray();
            }
        }

        private static void WriteU32(byte[] buf, int off, uint val)
        {
            buf[off] = (byte)(val & 0xFF);
            buf[off + 1] = (byte)((val >> 8) & 0xFF);
            buf[off + 2] = (byte)((val >> 16) & 0xFF);
            buf[off + 3] = (byte)((val >> 24) & 0xFF);
        }

        private static void WriteU64(byte[] buf, int off, ulong val)
        {
            buf[off] = (byte)(val & 0xFF);
            buf[off + 1] = (byte)((val >> 8) & 0xFF);
            buf[off + 2] = (byte)((val >> 16) & 0xFF);
            buf[off + 3] = (byte)((val >> 24) & 0xFF);
            buf[off + 4] = (byte)((val >> 32) & 0xFF);
            buf[off + 5] = (byte)((val >> 40) & 0xFF);
            buf[off + 6] = (byte)((val >> 48) & 0xFF);
            buf[off + 7] = (byte)((val >> 56) & 0xFF);
        }

        private static byte[] Concat(params byte[][] arrays)
        {
            int total = 0;
            foreach (byte[] a in arrays) total += a.Length;
            byte[] result = new byte[total];
            int pos = 0;
            foreach (byte[] a in arrays)
            {
                Array.Copy(a, 0, result, pos, a.Length);
                pos += a.Length;
            }
            return result;
        }
    }

    public class ModifiedBundleRefTableResource : ModifiedResource
    {
        public Dictionary<string, string> DuplicationDict { get { return newAssetMapping; } }

        private Dictionary<string, string> newAssetMapping = new Dictionary<string, string>();

        public bool AddAsset(string newAsset, string oldAsset)
        {
            if (!newAssetMapping.ContainsKey(newAsset))
            {
                newAssetMapping.Add(newAsset, oldAsset);
                return true;
            }
            return false;
        }

        public bool RemoveAsset(string newAsset)
        {
            if (newAssetMapping.ContainsKey(newAsset))
            {
                newAssetMapping.Remove(newAsset);
                return true;
            }
            return false;
        }

        public override void ReadInternal(NativeReader reader)
        {
            int count = reader.ReadInt();
            for (int i = 0; i < count; i++)
            {
                string newAsset = reader.ReadNullTerminatedString();
                string oldAsset = reader.ReadNullTerminatedString();

                EbxAssetEntry existingNewEntry = App.AssetManager.GetEbxEntry(newAsset.ToLower());
                if (existingNewEntry != null && !existingNewEntry.IsAdded)
                    continue;

                newAssetMapping.Add(newAsset, oldAsset);
            }
        }

        public override void SaveInternal(NativeWriter writer)
        {
            writer.Write(newAssetMapping.Count);
            foreach (string key in newAssetMapping.Keys)
            {
                writer.WriteNullTerminatedString(key);
                writer.WriteNullTerminatedString(newAssetMapping[key]);
            }
        }
    }
}
