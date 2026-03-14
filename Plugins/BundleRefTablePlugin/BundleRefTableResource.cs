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

        public ulong assetLookupPtr;
        public ulong bundleRefPtr;
        public ulong assetsPtr;
        public ulong bundlesPtr;

        public uint assetLookupCount;
        public uint bundleRefCount;
        public uint assetCount;
        public uint bundleCount = 0;
        public uint unkHash;

        public List<BundleRef> bundleRefs;
        public List<AssetLookup> assetLookups;
        public List<Asset> assets;
        public List<Bundle> bundles;

        public class AssetLookup
        {
            public ulong Hash { get; set; }
            public uint BundleRefIndex { get; set; }
            public uint AssetIndex { get; set; }

            public AssetLookup()
            {
                Hash = 0;
                BundleRefIndex = 0;
                AssetIndex = 0;
            }

            public AssetLookup(NativeReader reader)
            {
                if (BRTUtils.IsLegacyBrtFormat)
                    Hash = reader.ReadUInt();
                else
                    Hash = reader.ReadULong();
                BundleRefIndex = reader.ReadUInt();
                AssetIndex = reader.ReadUInt();
            }

            public void Write(NativeWriter writer)
            {
                if (BRTUtils.IsLegacyBrtFormat)
                    writer.Write((uint)Hash);
                else
                    writer.Write(Hash);
                writer.Write(BundleRefIndex);
                writer.Write(AssetIndex);
            }
        }

        public class Asset
        {
            public string Name { get; set; }
            public string Path { get; set; }

            public Asset()
            {
                Name = "";
                Path = "";
            }

            public Asset(NativeReader reader)
            {
                Name = BRTUtils.ReadString(reader, reader.ReadULong());
                Path = BRTUtils.ReadString(reader, reader.ReadULong());
            }

            public void Write(NativeWriter writer, Dictionary<string, ulong> stringMap)
            {
                writer.Write(stringMap[Name.ToLower()]);
                writer.Write(stringMap[Path.ToLower()]);
            }
        }

        public class BundleRef
        {
            public string Name { get; set; }
            public string Directory { get; set; }
            public uint BundleIndex { get; set; }

            public BundleRef()
            {
                Name = "";
                Directory = "";
                BundleIndex = 0;
            }

            public BundleRef(NativeReader reader, ulong bundlesPtr)
            {
                Name = BRTUtils.ReadString(reader, reader.ReadULong());
                Directory = BRTUtils.ReadString(reader, reader.ReadULong());
                BundleIndex = (uint)(reader.ReadULong() - bundlesPtr) / 16;
            }

            public void Write(NativeWriter writer, Dictionary<string, ulong> stringMap, uint bundleOffset)
            {
                writer.Write(stringMap[Name.ToLower()]);
                writer.Write(stringMap[Directory.ToLower()]);
                writer.Write((ulong)bundleOffset + (BundleIndex * 16));
            }
        }

        public class Bundle
        {
            public string Name { get; set; }
            public uint ParentBundleIndex { get; set; }

            public Bundle()
            {
                Name = "";
                ParentBundleIndex = 0;
            }

            public Bundle(NativeReader reader, ulong bundlesPtr)
            {
                Name = BRTUtils.ReadString(reader, reader.ReadULong());
                ParentBundleIndex = (uint)(reader.ReadULong() - bundlesPtr) / 16;
            }

            public void Write(NativeWriter writer, Dictionary<string, ulong> stringMap, uint bundleOffset)
            {
                writer.Write(stringMap[Name.ToLower()]);
                writer.Write((ulong)bundleOffset + (ParentBundleIndex * 16));
            }
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

            Name = BRTUtils.ReadString(reader, reader.ReadULong());

            assetLookupPtr = reader.ReadULong();
            bundleRefPtr = reader.ReadULong();
            assetsPtr = reader.ReadULong();
            bundlesPtr = reader.ReadULong();

            reader.Position += 8;

            assetLookupCount = reader.ReadUInt();
            bundleRefCount = reader.ReadUInt();
            assetCount = reader.ReadUInt();

            reader.Position += 4;
            unkHash = reader.ReadUInt();
            reader.Position += 4;
            reader.Position += 4;
            reader.Position += 4;

            reader.Position = (long)bundleRefPtr;
            bundleRefs = new List<BundleRef>();
            for (int i = 0; i < bundleRefCount; i++)
            {
                bundleRefs.Add(new BundleRef(reader, bundlesPtr));
                bundleCount = Math.Max(bundleRefs[i].BundleIndex + 1, bundleCount);
            }

            reader.Position = (long)assetsPtr;
            assets = new List<Asset>();
            for (int i = 0; i < assetCount; i++)
            {
                assets.Add(new Asset(reader));
            }

            reader.Position = (long)assetLookupPtr;
            assetLookups = new List<AssetLookup>();
            for (int i = 0; i < assetLookupCount; i++)
            {
                assetLookups.Add(new AssetLookup(reader));
            }

            reader.Position = (long)bundlesPtr;
            bundles = new List<Bundle>();
            for (int i = 0; i < bundleCount; i++)
            {
                bundles.Add(new Bundle(reader, bundlesPtr));
            }
        }

        public override byte[] SaveBytes()
        {
            Dictionary<string, ulong> stringMap = new Dictionary<string, ulong>
            {
                { "", 0 },
                { Name, 0 }
            };

            for (int i = 0; i < bundleRefs.Count; i++)
            {
                if (!stringMap.ContainsKey(bundleRefs[i].Name.ToLower()))
                    stringMap.Add(bundleRefs[i].Name.ToLower(), 0);
                if (!stringMap.ContainsKey(bundleRefs[i].Directory.ToLower()))
                    stringMap.Add(bundleRefs[i].Directory.ToLower(), 0);
            }

            for (int i = 0; i < assets.Count; i++)
            {
                if (!stringMap.ContainsKey(assets[i].Name.ToLower()))
                    stringMap.Add(assets[i].Name.ToLower(), 0);
                if (!stringMap.ContainsKey(assets[i].Path.ToLower()))
                    stringMap.Add(assets[i].Path.ToLower(), 0);
            }

            for (int i = 0; i < bundles.Count; i++)
            {
                if (!stringMap.ContainsKey(bundles[i].Name.ToLower()))
                    stringMap.Add(bundles[i].Name.ToLower(), 0);
            }

            using (NativeWriter writer = new NativeWriter(new MemoryStream()))
            {
                writer.Write((ulong)0);
                writer.Write((ulong)0);
                writer.Write((ulong)0);
                writer.Write((ulong)0);
                writer.Write((ulong)0);
                writer.Write((ulong)0);

                writer.Write(assetLookups.Count);
                writer.Write(bundleRefs.Count);
                writer.Write(assets.Count);

                writer.Position += 4;
                writer.Write(unkHash);
                writer.Position += 4;
                writer.Write((uint)1);
                writer.Position += 4;

                writer.WritePadding(16);

                List<string> keys = new List<string>(stringMap.Keys);
                foreach (string data in keys)
                {
                    stringMap[data] = (ulong)writer.Position;
                    writer.WriteNullTerminatedString(data);
                }

                writer.WritePadding(16);

                int assetLookupEntrySize = BRTUtils.IsLegacyBrtFormat ? 12 : 16;
                ulong newBundleRefsOffset = (ulong)writer.Position;
                ulong newAssetOffset = newBundleRefsOffset + (ulong)(24 * bundleRefs.Count);
                ulong newAssetLookupOffset = newAssetOffset + (ulong)(16 * assets.Count);
                ulong assetLookupEnd = newAssetLookupOffset + (ulong)(assetLookupEntrySize * assetLookups.Count);
                ulong newBundleOffset = (assetLookupEnd + 15) & ~15UL;

                long curPos = writer.Position;
                writer.Position = 0;
                writer.Write(stringMap[Name.ToLower()]);
                writer.Write(newAssetLookupOffset);
                writer.Write(newBundleRefsOffset);
                writer.Write(newAssetOffset);
                writer.Write(newBundleOffset);
                writer.Write(stringMap[""]);
                writer.Position = curPos;

                for (int i = 0; i < bundleRefs.Count; i++)
                    bundleRefs[i].Write(writer, stringMap, (uint)newBundleOffset);

                for (int i = 0; i < assets.Count; i++)
                    assets[i].Write(writer, stringMap);

                assetLookups.Sort((a, b) => a.Hash.CompareTo(b.Hash));

                for (int i = 0; i < assetLookups.Count; i++)
                    assetLookups[i].Write(writer);

                writer.WritePadding(16);

                for (int i = 0; i < bundleCount; i++)
                    bundles[i].Write(writer, stringMap, (uint)newBundleOffset);

                ulong relocTableOffset = (ulong)writer.Position;

                List<uint> pointerLocations = new List<uint>();
                pointerLocations.Add(0x00);
                pointerLocations.Add(0x08);
                pointerLocations.Add(0x10);
                pointerLocations.Add(0x18);
                pointerLocations.Add(0x20);
                pointerLocations.Add(0x28);

                for (int i = 0; i < bundleRefs.Count; i++)
                {
                    pointerLocations.Add((uint)(newBundleRefsOffset + (ulong)(i * 24)));
                    pointerLocations.Add((uint)(newBundleRefsOffset + (ulong)(i * 24)) + 8);
                    pointerLocations.Add((uint)(newBundleRefsOffset + (ulong)(i * 24)) + 16);
                }

                for (int i = 0; i < assets.Count; i++)
                {
                    pointerLocations.Add((uint)(newAssetOffset + (ulong)(i * 16)));
                    pointerLocations.Add((uint)(newAssetOffset + (ulong)(i * 16)) + 8);
                }

                for (int i = 0; i < bundles.Count; i++)
                {
                    pointerLocations.Add((uint)(newBundleOffset + (ulong)(i * 16)));
                    pointerLocations.Add((uint)(newBundleOffset + (ulong)(i * 16)) + 8);
                }

                foreach (uint loc in pointerLocations)
                    writer.Write(loc);

                uint relocTableSize = (uint)(writer.Position - (long)relocTableOffset);

                byte[] relocOffBytes = System.BitConverter.GetBytes(relocTableOffset);
                byte[] relocSizeBytes = System.BitConverter.GetBytes(relocTableSize);
                relocOffBytes.CopyTo(resMeta, 0);
                relocSizeBytes.CopyTo(resMeta, 4);

                return writer.ToByteArray();
            }
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

        /// <summary>
        /// Checks whether an asset path (by its hash) exists in this BRT's lookup table.
        /// </summary>
        public bool ContainsAsset(string assetPath)
        {
            ulong hash = BRTUtils.HashAssetPath(assetPath);
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
            ulong oldHash = BRTUtils.HashAssetPath(existingAssetPath);
            ulong newHashFull = BRTUtils.HashAssetPath(newAssetPath);
            ulong newHashName = BRTUtils.HashAssetPath(newAssetPath.Substring(newAssetPath.LastIndexOf("/") + 1));

            // Remove existing entries with the new hashes (iterate in REVERSE)
            List<int> indicesToRemove = new List<int>();
            for (int i = 0; i < assetLookups.Count; i++)
            {
                if (assetLookups[i].Hash == newHashFull || assetLookups[i].Hash == newHashName)
                    indicesToRemove.Add(i);
            }
            for (int idx = indicesToRemove.Count - 1; idx >= 0; idx--)
            {
                assetLookups.RemoveAt(indicesToRemove[idx]);
            }

            for (int i = 0; i < assetLookups.Count; i++)
            {
                if (assetLookups[i].Hash == oldHash)
                {
                    AssetLookup newAssetLookup = new AssetLookup();
                    AssetLookup newAssetLookupNameOnly = new AssetLookup();
                    newAssetLookup.Hash = newHashFull;
                    newAssetLookupNameOnly.Hash = newHashName;
                    newAssetLookup.BundleRefIndex = assetLookups[i].BundleRefIndex;
                    newAssetLookupNameOnly.BundleRefIndex = assetLookups[i].BundleRefIndex;

                    Asset newAsset = new Asset();
                    newAsset.Name = newAssetPath.Substring(newAssetPath.LastIndexOf("/") + 1);
                    newAsset.Path = newAssetPath.Substring(0, newAssetPath.LastIndexOf("/")).Trim('/');

                    bool found = false;
                    for (int j = 0; j < assets.Count; j++)
                    {
                        if (assets[j].Name == newAsset.Name && assets[j].Path == newAsset.Path)
                        {
                            newAssetLookup.AssetIndex = (uint)j;
                            newAssetLookupNameOnly.AssetIndex = (uint)j;
                            found = true;
                            break;
                        }
                    }

                    if (!found)
                    {
                        assets.Add(newAsset);
                        newAssetLookup.AssetIndex = (uint)(assets.Count - 1);
                        newAssetLookupNameOnly.AssetIndex = (uint)(assets.Count - 1);
                    }

                    assetLookups.Add(newAssetLookup);
                    assetLookups.Add(newAssetLookupNameOnly);
                    return true;
                }
            }

            return false;
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
