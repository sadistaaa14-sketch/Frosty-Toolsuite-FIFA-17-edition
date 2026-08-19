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

        // TEMPORARY A/B TEST: when true, the new bundle ref is still written into
        // the BRT, but the duplicated-asset lookups keep pointing at the SOURCE
        // bundle ref (Messi) instead of the new one. Set false to restore the
        // re-pointing behaviour.
        public static bool A_B_TEST_LOOKUPS_AT_SOURCE_REF = false;

        // Parsed data (for ContainsAsset lookups)
        public List<AssetLookup> assetLookups;
        public List<Asset> assets;
        public List<BundleRef> bundleRefs;
        public List<BundleInfo> bundles;

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

        public class BundleRef
        {
            public string Name { get; set; }
            public string Path { get; set; }
            public uint BundleIndex { get; set; }
        }

        public class BundleInfo
        {
            public string Name { get; set; }
            public uint ParentIndex { get; set; }
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

            // Parse bundle refs (name, path, bundle index)
            bundleRefs = new List<BundleRef>();
            long brStart = (long)brPtr;
            for (int i = 0; i < brCount; i++)
            {
                long off = brStart + i * 24;
                ulong np = BitConverter.ToUInt64(rawData, (int)off);
                ulong pp = BitConverter.ToUInt64(rawData, (int)off + 8);
                ulong bp = BitConverter.ToUInt64(rawData, (int)off + 16);
                bundleRefs.Add(new BundleRef
                {
                    Name = ReadCStr(rawData, (int)np),
                    Path = ReadCStr(rawData, (int)pp),
                    BundleIndex = (uint)((bp - bundlesPtr) / 16)
                });
            }

            // Parse bundles (name, parent index)
            bundles = new List<BundleInfo>();
            long bundlesStart = (long)bundlesPtr;
            for (int i = 0; i < bundleCountActual; i++)
            {
                long off = bundlesStart + i * 16;
                ulong np = BitConverter.ToUInt64(rawData, (int)off);
                ulong pp = BitConverter.ToUInt64(rawData, (int)off + 8);
                bundles.Add(new BundleInfo
                {
                    Name = ReadCStr(rawData, (int)np),
                    ParentIndex = (uint)((pp - bundlesPtr) / 16)
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

            // ── 0. Sort newly-added assets to match the original per-player
            // (path, name) ordering, and remap lookup asset indices accordingly.
            int origAssetCount0 = (int)assetCount;
            int newAssetCount0 = assets.Count - origAssetCount0;
            if (newAssetCount0 > 0)
            {
                Asset[] newAssetRefs = new Asset[newAssetCount0];
                for (int i = 0; i < newAssetCount0; i++)
                    newAssetRefs[i] = assets[origAssetCount0 + i];

                assets.Sort(origAssetCount0, newAssetCount0, Comparer<Asset>.Create((a, b) =>
                {
                    int cmp = string.CompareOrdinal(a.Path, b.Path);
                    return cmp != 0 ? cmp : string.CompareOrdinal(a.Name, b.Name);
                }));

                Dictionary<uint, uint> remap = new Dictionary<uint, uint>();
                for (int i = 0; i < newAssetCount0; i++)
                {
                    uint oldIdx = (uint)(origAssetCount0 + i);
                    int newIdx = assets.IndexOf(newAssetRefs[i], origAssetCount0);
                    if (newIdx >= 0)
                        remap[oldIdx] = (uint)newIdx;
                }

                for (int i = 0; i < assetLookups.Count; i++)
                {
                    if (remap.TryGetValue(assetLookups[i].AssetIndex, out uint newIdx))
                        assetLookups[i].AssetIndex = newIdx;
                }
            }

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
            // Emit new strings in the original BRT layout: a new asset's path first,
            // followed by its (sorted) names.
            string lastPath = null;
            for (int i = newAssetStartIndex; i < assets.Count; i++)
            {
                string name = assets[i].Name;
                string path = assets[i].Path;
                if (path != lastPath)
                {
                    if (!HasString(rawData, path, strStart, brtNameStart) && !newStringSet.Contains(path))
                    {
                        newStrings.Add(path);
                        newStringSet.Add(path);
                    }
                    lastPath = path;
                }
                if (!HasString(rawData, name, strStart, brtNameStart) && !newStringSet.Contains(name))
                {
                    newStrings.Add(name);
                    newStringSet.Add(name);
                }
            }

            // New bundle ref strings (name/path) and new bundle strings (name)
            for (int i = (int)brCount; i < bundleRefs.Count; i++)
            {
                string name = bundleRefs[i].Name;
                string path = bundleRefs[i].Path;
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
            for (int i = (int)bundleCountActual; i < bundles.Count; i++)
            {
                string name = bundles[i].Name;
                if (!HasString(rawData, name, strStart, brtNameStart) && !newStringSet.Contains(name))
                {
                    newStrings.Add(name);
                    newStringSet.Add(name);
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
            int newBrCount = bundleRefs.Count;
            int newAssetCount = assets.Count;
            int newAlCount = assetLookups.Count;
            int newBundleCount = bundles.Count;

            int strtabResStart = 0x50;
            int newBrRes = strtabResStart + strtabPadded.Length;
            int newAssetsRes = newBrRes + (newBrCount * 24);
            int newAlRes = newAssetsRes + (newAssetCount * 16);
            int alEndRes = newAlRes + (newAlCount * 12);
            int alPadLen2 = (16 - (alEndRes % 16)) % 16;
            int newBundlesRes = alEndRes + alPadLen2;
            int newRelocRes = newBundlesRes + (newBundleCount * 16);

            // ── 6. Build sections from raw data + new entries ───────────

            // Bundle Refs (original + new)
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
            // Append new bundle refs
            for (int i = (int)brCount; i < bundleRefs.Count; i++)
            {
                ulong p0 = (ulong)GetStringPtr(rawData, bundleRefs[i].Name, strStart, brtNameStart, newStringOffsets);
                ulong p1 = (ulong)GetStringPtr(rawData, bundleRefs[i].Path, strStart, brtNameStart, newStringOffsets);
                ulong p2 = (ulong)newBundlesRes + bundleRefs[i].BundleIndex * 16;

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

            // Bundles (original + new)
            byte[] newBundlesBytes = new byte[newBundleCount * 16];
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
            // Append new bundles
            for (int i = (int)bundleCountActual; i < bundles.Count; i++)
            {
                ulong p0 = (ulong)GetStringPtr(rawData, bundles[i].Name, strStart, brtNameStart, newStringOffsets);
                ulong p1 = (ulong)newBundlesRes + bundles[i].ParentIndex * 16;

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
            for (int i = 0; i < newBundleCount; i++)
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

            // First materialise any new blueprint bundles + bundle refs (the
            // duplicated player gets its own bundle instead of reusing the
            // source's), and remember each new bundle ref index by name.
            // Two bundle-ref shapes:
            //  - Style A (folder ref): ref.Name = folder, ref.Path = "". A new asset at
            //    "<folder>/<name>" is matched by its folder.
            //  - Style B (per-asset ref, e.g. _psd_brt / _actor_brt): ref.Name = asset
            //    name, ref.Path = folder. A new asset at "<folder>/<name>" is matched by
            //    its full path.
            Dictionary<string, uint> refIndexByFolder = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, uint> refIndexByFullPath = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
            if (modResource != null)
            {
                foreach (BundleRefAddition add in modResource.BundleRefAdditions)
                {
                    uint idx = AddBundleRef(add.BundleRefName, add.BundleRefPath, add.BundleIndex);

                    string folder = string.IsNullOrEmpty(add.BundleRefPath) ? add.BundleRefName : add.BundleRefPath;
                    refIndexByFolder[folder] = idx;

                    if (!string.IsNullOrEmpty(add.BundleRefPath))
                        refIndexByFullPath[add.BundleRefPath + "/" + add.BundleRefName] = idx;
                }
            }

            foreach (KeyValuePair<string, string> kvp in modResource.DuplicationDict)
            {
                // A duplicated asset lives under "<new player path>/<name>"; if that
                // path has a new bundle ref, point the lookup at it. Otherwise fall
                // back to the source's bundle ref (legacy behaviour).
                uint? refIdx = null;
                if (!A_B_TEST_LOOKUPS_AT_SOURCE_REF)
                {
                    if (refIndexByFullPath.TryGetValue(kvp.Key, out uint fullFound))
                    {
                        refIdx = fullFound;
                    }
                    else
                    {
                        int slash = kvp.Key.LastIndexOf('/');
                        if (slash > 0 && refIndexByFolder.TryGetValue(kvp.Key.Substring(0, slash), out uint folderFound))
                            refIdx = folderFound;
                    }
                }

                AddDupeEntry(kvp.Key, kvp.Value, refIdx);
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

        /// <summary>
        /// Records a duplication that should live in a brand new blueprint bundle
        /// rather than reusing the source asset's bundle ref. The bundle/ref are
        /// materialised later in ApplyModifiedResource (at mod launch).
        /// </summary>
        public bool DupeAssetToNewBundle(string newAssetPath, string existingAssetPath,
            string newBundleRefName)
        {
            if (modResource == null)
                modResource = new ModifiedBundleRefTableResource();

            uint bundleIndex = GetSourceBundleIndex(existingAssetPath, out string sourceRefName, out string sourceRefPath);

            // Style A (folder ref): the source ref carries the whole folder in Name and
            // an empty Path, so the new ref mirrors the folder name.
            //
            // Style B (per-asset ref, e.g. _psd_brt / _actor_brt): the source ref carries
            // the asset NAME in Name and the folder in Path. The new ref must do the
            // same, so derive both from the new asset path.
            string newRefName;
            string newRefPath;
            if (string.IsNullOrEmpty(sourceRefPath))
            {
                newRefName = newBundleRefName;
                newRefPath = "";
            }
            else
            {
                int slash = newAssetPath.LastIndexOf('/');
                newRefName = newAssetPath.Substring(slash + 1);
                newRefPath = newAssetPath.Substring(0, slash);
            }

            modResource.AddBundleRefAddition(newRefName, newRefPath, bundleIndex);

            return modResource.AddAsset(newAssetPath, existingAssetPath);
        }

        /// <summary>
        /// Returns the parent bundle index of the bundle that the given existing
        /// asset's bundle ref points to ("the parent of the source bundle").
        /// </summary>
        public uint GetSourceBundleIndex(string existingAssetPath, out string sourceRefName, out string sourceRefPath)
        {
            sourceRefName = "";
            sourceRefPath = "";
            uint oldHash = BRTUtils.Fnv1Hash32(existingAssetPath);
            foreach (AssetLookup lookup in assetLookups)
            {
                if (lookup.Hash == oldHash)
                {
                    uint refIdx = lookup.BundleRefIndex;
                    if (refIdx < bundleRefs.Count)
                    {
                        sourceRefName = bundleRefs[(int)refIdx].Name;
                        sourceRefPath = bundleRefs[(int)refIdx].Path;
                        return bundleRefs[(int)refIdx].BundleIndex;
                    }
                    break;
                }
            }
            return 0;
        }

        /// <summary>
        /// Adds (or reuses) a new bundle + bundle ref and returns the bundle ref
        /// index. The new bundle is the duplicated BundleRefTableBlueprintBundle
        /// content path, parented under the source bundle's parent.
        /// </summary>
        public uint AddBundleRef(string bundleRefName, string bundleRefPath, uint bundleIndex)
        {
            for (int i = 0; i < bundleRefs.Count; i++)
            {
                if (bundleRefs[i].Name.Equals(bundleRefName, StringComparison.OrdinalIgnoreCase)
                    && bundleRefs[i].Path.Equals(bundleRefPath, StringComparison.OrdinalIgnoreCase))
                {
                    bundleRefs[i].BundleIndex = bundleIndex;
                    return (uint)i;
                }
            }

            bundleRefs.Add(new BundleRef { Name = bundleRefName, Path = bundleRefPath, BundleIndex = bundleIndex });
            return (uint)(bundleRefs.Count - 1);
        }

        public bool RevertDupe(string newAssetPath)
        {
            if (modResource == null)
                return false;

            return modResource.RemoveAsset(newAssetPath);
        }

        public bool AddDupeEntry(string newAssetPath, string existingAssetPath, uint? bundleRefIndex = null)
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
                    uint bri = bundleRefIndex ?? assetLookups[i].BundleRefIndex;

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

    public class BundleRefAddition
    {
        public string BundleRefName;
        public string BundleRefPath;
        public uint BundleIndex;
    }

    public class ModifiedBundleRefTableResource : ModifiedResource
    {
        // App.AssetManager is only populated inside the Frosty Editor process. FrostyModExecutor
        // (used by FrostyModManager to actually launch mods) builds and uses its own local
        // AssetManager instance instead and never sets this static, so it is null here during a
        // real mod launch — guard for that below. When it IS available, its internal lookups are
        // not thread-safe, but ReadInternal can be invoked concurrently (ProcessModResources uses
        // Parallel.ForEach), so serialize access to avoid a race inside GetEbxEntry.
        private static readonly object s_assetManagerLock = new object();

        public Dictionary<string, string> DuplicationDict { get { return newAssetMapping; } }
        public List<BundleRefAddition> BundleRefAdditions { get { return bundleRefAdditions; } }

        private Dictionary<string, string> newAssetMapping = new Dictionary<string, string>();
        private List<BundleRefAddition> bundleRefAdditions = new List<BundleRefAddition>();

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

        public void AddBundleRefAddition(string bundleRefName, string bundleRefPath, uint bundleIndex)
        {
            foreach (BundleRefAddition add in bundleRefAdditions)
            {
                if (add.BundleRefName.Equals(bundleRefName, StringComparison.OrdinalIgnoreCase)
                    && add.BundleRefPath.Equals(bundleRefPath, StringComparison.OrdinalIgnoreCase))
                {
                    add.BundleIndex = bundleIndex;
                    return;
                }
            }
            bundleRefAdditions.Add(new BundleRefAddition
            {
                BundleRefName = bundleRefName,
                BundleRefPath = bundleRefPath,
                BundleIndex = bundleIndex
            });
        }

        public override void ReadInternal(NativeReader reader)
        {
            int count = reader.ReadInt();
            FileLogger.Log("    BRT.ReadInternal: count={0}", count);

            for (int i = 0; i < count; i++)
            {
                string newAsset = reader.ReadNullTerminatedString();
                string oldAsset = reader.ReadNullTerminatedString();

                FileLogger.Log("    BRT.ReadInternal [{0}/{1}]: new='{2}' old='{3}'", i + 1, count, newAsset, oldAsset);

                EbxAssetEntry existingNewEntry = null;
                if (App.AssetManager != null)
                {
                    lock (s_assetManagerLock)
                    {
                        existingNewEntry = App.AssetManager.GetEbxEntry(newAsset.ToLower());
                    }
                }
                else
                {
                    FileLogger.Log("      → App.AssetManager is null (running outside the Editor, e.g. FrostyModExecutor) — skipping existence check, keeping mapping");
                }
                if (existingNewEntry != null && !existingNewEntry.IsAdded)
                {
                    FileLogger.Log("      → SKIPPED (entry exists but IsAdded=false)");
                    continue;
                }

                if (existingNewEntry == null)
                    FileLogger.Log("      → entry not found in AM (will keep mapping)");
                else
                    FileLogger.Log("      → entry found, IsAdded=true (will keep mapping)");

                if (!newAssetMapping.ContainsKey(newAsset))
                {
                    newAssetMapping.Add(newAsset, oldAsset);
                    FileLogger.Log("      → added to mapping");
                }
                else
                {
                    FileLogger.Log("      → SKIPPED (already in mapping)");
                }
            }

            // bundle ref additions (new blueprint bundles). Older mods end here.
            if (reader.Position < reader.Length)
            {
                int version = reader.ReadInt();
                int brCount = reader.ReadInt();
                for (int i = 0; i < brCount; i++)
                {
                    string refName = reader.ReadNullTerminatedString();
                    string refPath = (version >= 1) ? reader.ReadNullTerminatedString() : "";
                    uint bundleIndex = reader.ReadUInt();
                    AddBundleRefAddition(refName, refPath, bundleIndex);
                    FileLogger.Log("    BRT.ReadInternal bundleRef [{0}/{1}]: ref='{2}' path='{3}' bundleIndex={4}",
                        i + 1, brCount, refName, refPath, bundleIndex);
                }
            }

            FileLogger.Log("    BRT.ReadInternal: done, total pairs={0}, bundleRefAdditions={1}",
                newAssetMapping.Count, bundleRefAdditions.Count);
        }

        public override void SaveInternal(NativeWriter writer)
        {
            writer.Write(newAssetMapping.Count);
            foreach (string key in newAssetMapping.Keys)
            {
                writer.WriteNullTerminatedString(key);
                writer.WriteNullTerminatedString(newAssetMapping[key]);
            }

            // Bundle-ref additions: version 1 adds the Path field (Style-B per-asset
            // bundle refs). Legacy mods (version 0) stored only Name + BundleIndex.
            writer.Write(1);
            writer.Write(bundleRefAdditions.Count);
            foreach (BundleRefAddition add in bundleRefAdditions)
            {
                writer.WriteNullTerminatedString(add.BundleRefName);
                writer.WriteNullTerminatedString(add.BundleRefPath ?? "");
                writer.Write(add.BundleIndex);
            }
        }
    }
}
