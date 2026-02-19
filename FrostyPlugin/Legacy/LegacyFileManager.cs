using Frosty.Hash;
using FrostySdk.Interfaces;
using FrostySdk.IO;
using FrostySdk.Managers;
using System;
using System.Collections.Generic;
using System.IO;

namespace Frosty.Core.Legacy
{
    public sealed class LegacyFileManager : ICustomAssetManager
    {
        /* list of all legacy entries indexed by hash */
        private Dictionary<int, LegacyFileEntry> legacyEntries = new Dictionary<int, LegacyFileEntry>();
        private HashSet<int> addedEntryHashes = new HashSet<int>();

        /* 
         * list of cached legacy chunks when running in cache mode (this ensures that the same chunk
         * is not extracted over and over again)
         */
        private Dictionary<Guid, byte[]> cachedChunks = new Dictionary<Guid, byte[]>();
        private bool cacheMode = false;

        /* Load in legacy files */
        public void Initialize(ILogger logger)
        {
            logger.Log("Loading Legacy Files");
            foreach (EbxAssetEntry entry in App.AssetManager.EnumerateEbx("ChunkFileCollector"))
            {
                EbxAsset asset = App.AssetManager.GetEbx(entry);
                if (asset == null)
                    continue;

                dynamic obj = asset.RootObject;
                dynamic manifest = obj.Manifest;

                ChunkAssetEntry chunk = App.AssetManager.GetChunkEntry(manifest.ChunkId);
                if (chunk == null)
                    continue;

                Stream chunkStream = App.AssetManager.GetChunk(chunk);
                if (chunkStream == null)
                    continue;

                using (NativeReader reader = new NativeReader(chunkStream))
                {
                    uint numEntries = reader.ReadUInt();
                    long dataOffset = reader.ReadLong();

                    reader.Position = dataOffset;
                    for (uint i = 0; i < numEntries; i++)
                    {
                        long strOffset = reader.ReadLong();
                        long curPos = reader.Position;
                        reader.Position = strOffset;
                        string name = reader.ReadNullTerminatedString();
                        reader.Position = curPos;

                        int hash = Fnv1.HashString(name);
                        LegacyFileEntry legacyEntry = null;

                        if (!legacyEntries.ContainsKey(hash))
                        {
                            legacyEntry = new LegacyFileEntry {Name = name};
                            legacyEntries.Add(hash, legacyEntry);
                        }
                        else
                        {
                            legacyEntry = legacyEntries[hash];
                        }

                        LegacyFileEntry.ChunkCollectorInstance inst = new LegacyFileEntry.ChunkCollectorInstance
                        {
                            CompressedOffset = reader.ReadLong(),
                            CompressedSize = reader.ReadLong(),
                            Offset = reader.ReadLong(),
                            Size = reader.ReadLong(),
                            ChunkId = reader.ReadGuid(),
                            Entry = entry
                        };
                        legacyEntry.CollectorInstances.Add(inst);
                    }
                }
            }
        }

        /* Enable/Disable cache mode */
        public void SetCacheModeEnabled(bool enabled)
        {
            cacheMode = enabled;
        }

        /* Flush any loaded chunks */
        public void FlushCache()
        {
            cachedChunks.Clear();
        }

        /* Enumerate through all legacy files */
        public IEnumerable<AssetEntry> EnumerateAssets(bool modifiedOnly)
        {
            foreach (LegacyFileEntry entry in legacyEntries.Values)
            {
                if (modifiedOnly && !entry.IsModified)
                    continue;
                yield return entry;
            }
        }

        /* Obtain legacy file data */
        public AssetEntry GetAssetEntry(string key)
        {
            int hash = Fnv1.HashString(key);
            if (legacyEntries.ContainsKey(hash))
                return legacyEntries[hash];
            return null;
        }

        /* Obtain legacy file data */
        public Stream GetAsset(AssetEntry entry)
        {
            LegacyFileEntry lfe = entry as LegacyFileEntry;
            Stream chunkStream = GetChunkStream(lfe);
            if (chunkStream == null)
                return null;

            using (NativeReader reader = new NativeReader(chunkStream))
            {
                LegacyFileEntry.ChunkCollectorInstance inst = (lfe.IsModified)
                    ? lfe.CollectorInstances[0].ModifiedEntry
                    : lfe.CollectorInstances[0];

                reader.Position = inst.Offset;
                return new MemoryStream(reader.ReadBytes((int)inst.Size));
            }
        }

        public void ModifyAsset(string key, byte[] data)
        {
            int hash = Fnv1.HashString(key);
            if (!legacyEntries.ContainsKey(hash))
                return;

            LegacyFileEntry lfe = legacyEntries[hash];

            MemoryStream ms = new MemoryStream();
            using (NativeWriter writer = new NativeWriter(ms, true))
                writer.Write(data);

            App.AssetManager.RevertAsset(lfe);

            Guid guid = App.AssetManager.AddChunk(data, GenerateDeterministicGuid(lfe));
            foreach (LegacyFileEntry.ChunkCollectorInstance inst in lfe.CollectorInstances)
            {
                // add new chunk
                inst.ModifiedEntry = new LegacyFileEntry.ChunkCollectorInstance();
                ChunkAssetEntry assetChunkEntry = App.AssetManager.GetChunkEntry(guid);

                inst.ModifiedEntry.ChunkId = guid;
                inst.ModifiedEntry.Offset = 0;
                inst.ModifiedEntry.CompressedOffset = 0;
                inst.ModifiedEntry.Size = data.Length;
                inst.ModifiedEntry.CompressedSize = assetChunkEntry.ModifiedEntry.Data.Length;

                // @temp
                assetChunkEntry.ModifiedEntry.AddToChunkBundle = true;
                assetChunkEntry.ModifiedEntry.UserData = "legacy;" + lfe.Name;

                // link to main ebx
                lfe.LinkAsset(assetChunkEntry);
                inst.Entry.LinkAsset(lfe);
            }

            lfe.IsDirty = true;
            ms.Dispose();
        }

        public byte[] GetAssetData(string key)
        {
            int hash = Fnv1.HashString(key);
            if (!legacyEntries.ContainsKey(hash))
                return null;

            LegacyFileEntry lfe = legacyEntries[hash];
            ChunkAssetEntry chunkEntry = App.AssetManager.GetChunkEntry(lfe.ChunkId);

            if (chunkEntry == null)
                return null;

            if (chunkEntry.ModifiedEntry != null && chunkEntry.ModifiedEntry.Data != null)
                return chunkEntry.ModifiedEntry.Data;

            using (NativeReader reader = new NativeReader(App.AssetManager.GetChunk(chunkEntry)))
                return reader.ReadToEnd();
        }

        public void DuplicateAsset(string sourceKey, string newKey)
        {
            int sourceHash = Fnv1.HashString(sourceKey);
            int newHash = Fnv1.HashString(newKey);

            if (!legacyEntries.ContainsKey(sourceHash))
                return;
            if (legacyEntries.ContainsKey(newHash))
                return;

            LegacyFileEntry source = legacyEntries[sourceHash];

            byte[] data = GetAssetData(sourceKey);
            if (data == null)
                return;

            // create new entry
            LegacyFileEntry newEntry = new LegacyFileEntry
            {
                Name = newKey,
            };

            // register new chunk
            Guid guid = App.AssetManager.AddChunk(data, GenerateDeterministicGuid(newEntry));
            ChunkAssetEntry chunkEntry = App.AssetManager.GetChunkEntry(guid);
            chunkEntry.ModifiedEntry.AddToChunkBundle = true;
            chunkEntry.ModifiedEntry.UserData = "legacy;" + newKey;

            newEntry.LinkAsset(chunkEntry);

            // copy collector instance references from source
            // so the handler knows which manifest chunks to update at apply time
            foreach (LegacyFileEntry.ChunkCollectorInstance inst in source.CollectorInstances)
            {
                LegacyFileEntry.ChunkCollectorInstance newInst = new LegacyFileEntry.ChunkCollectorInstance

                {
                    Entry = inst.Entry,
                    ModifiedEntry = new LegacyFileEntry.ChunkCollectorInstance
                    {
                        ChunkId = guid,
                        Offset = 0,
                        CompressedOffset = 0,
                        Size = data.Length,
                        CompressedSize = chunkEntry.ModifiedEntry.Data.Length
                    }
                };

                newEntry.CollectorInstances.Add(newInst);
                inst.Entry.LinkAsset(newEntry);
                EbxAsset collectorAsset = App.AssetManager.GetEbx(inst.Entry);
                dynamic root = collectorAsset.RootObject;
                dynamic manifest = root.Manifest;

                uint nameBytes = (uint)System.Text.Encoding.UTF8.GetByteCount(newKey) + 1;

                uint dataSize = (uint)manifest.DataSize;
                uint fixupSize = (uint)manifest.FixupSize;

                manifest.DataSize = dataSize + 56u + nameBytes;
                manifest.FixupSize = fixupSize + 4u;

                App.AssetManager.ModifyEbx(inst.Entry.Name, collectorAsset);
            }
            legacyEntries.Add(newHash, newEntry);
            addedEntryHashes.Add(newHash);
        }

        private void ClearAddedEntries()
        {
            foreach (int hash in addedEntryHashes)
                legacyEntries.Remove(hash);
            addedEntryHashes.Clear();
        }


        /* Obtain data from cache or directly from chunk */
        private Stream GetChunkStream(LegacyFileEntry lfe)
        {
            if (cacheMode)
            {
                if (!cachedChunks.ContainsKey(lfe.ChunkId))
                {
                    using (Stream chunkStream = App.AssetManager.GetChunk(App.AssetManager.GetChunkEntry(lfe.ChunkId)))
                    {
                        if (chunkStream == null)
                            return null;
                        cachedChunks.Add(lfe.ChunkId, ((MemoryStream)chunkStream).ToArray());
                    }
                }
                return new MemoryStream(cachedChunks[lfe.ChunkId]);
            }

            return App.AssetManager.GetChunk(App.AssetManager.GetChunkEntry(lfe.ChunkId));
        }

        public void OnCommand(string command, params object[] value)
        {
            switch (command)
            {
                case "SetCacheModeEnabled": SetCacheModeEnabled((bool)value[0]); break;
                case "FlushCache": FlushCache(); break;
                case "DuplicateAsset": DuplicateAsset((string)value[0], (string)value[1]); break;
                case "ClearAddedEntries": ClearAddedEntries(); break;
            }
        }

        /// <summary>
        /// Will return a unique but determinstic guid based on the filename of the asset. This will
        /// ensure to minimize random conflicts between unassociated objects
        /// </summary>
        public static Guid GenerateDeterministicGuid(LegacyFileEntry lfe)
        {
            ulong nameHash = Murmur2.HashString64(lfe.Filename, 0x4864);
            ulong dirHash = Murmur2.HashString64(lfe.Path, 0x4864);
            int index = 1;

            Guid guid = Guid.Empty;
            do
            {
                using (NativeWriter writer = new NativeWriter(new MemoryStream()))
                {
                    writer.Write(dirHash);
                    writer.Write(((nameHash ^ (ulong)index)));

                    byte[] b = writer.ToByteArray();;
                    b[15] = 0x01;

                    guid = new Guid(b);
                }
                index++;
            }
            while (App.AssetManager.GetChunkEntry(guid) != null);

            return guid;
        }
    }
}
