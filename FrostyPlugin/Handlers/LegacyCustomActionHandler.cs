using Frosty.Core.IO;
using Frosty.Core.Legacy;
using Frosty.Core.Mod;
using Frosty.Hash;
using FrostySdk;
using FrostySdk.IO;
using FrostySdk.Managers;
using System;
using System.Collections.Generic;
using System.IO;

namespace Frosty.Core.Handlers
{
    public sealed class LegacyCustomActionHandler : ILegacyCustomActionHandler
    {
        public HandlerUsage Usage => HandlerUsage.Merge;

        private class ModLegacyFileEntry
        {
            public int Hash { get; set; }
            public string Name { get; set; }
            public Guid ChunkId { get; set; }
            public long Offset { get; set; }
            public long CompressedOffset { get; set; }
            public long CompressedSize { get; set; }
            public long Size { get; set; }
        }

        public static uint Hash => 0xBD9BFB65;

        private class LegacyResource : EditorModResource
        {
            public override ModResourceType Type => ModResourceType.Chunk;
            public LegacyResource(string inName, string ebxName, byte[] data, IEnumerable<int> bundles, FrostyModWriter.Manifest manifest)
            {
                name = inName;
                sha1 = Utils.GenerateSha1(data);
                resourceIndex = manifest.Add(data);
                size = data.Length;
                flags = 2;
                handlerHash = (int)Hash;
                userData = "legacy;Collector (" + ebxName + ")";
            }

            public override void Write(NativeWriter writer)
            {
                base.Write(writer);

                writer.Write(0);
                writer.Write(0);
                writer.Write(0);
                writer.Write(0);
                writer.Write(0);
                writer.Write(0);
            }
        }

        public void SaveToMod(FrostyModWriter writer)
        {
            Dictionary<EbxAssetEntry, List<Tuple<int, LegacyFileEntry.ChunkCollectorInstance, string>>> manifests =
                new Dictionary<EbxAssetEntry, List<Tuple<int, LegacyFileEntry.ChunkCollectorInstance, string>>>();

            foreach (LegacyFileEntry lfe in App.AssetManager.EnumerateCustomAssets("legacy", modifiedOnly: true))
            {
                foreach (LegacyFileEntry.ChunkCollectorInstance inst in lfe.CollectorInstances)
                {
                    if (!manifests.ContainsKey(inst.Entry))
                        manifests.Add(inst.Entry, new List<Tuple<int, LegacyFileEntry.ChunkCollectorInstance, string>>());
                    manifests[inst.Entry].Add(new Tuple<int, LegacyFileEntry.ChunkCollectorInstance, string>(
                        lfe.NameHash, inst.ModifiedEntry, lfe.Name));
                }
            }

            foreach (EbxAssetEntry entry in manifests.Keys)
            {
                dynamic obj = App.AssetManager.GetEbx(entry).RootObject;
                dynamic manifest = obj.Manifest;

                ChunkAssetEntry collectorChunkEntry = App.AssetManager.GetChunkEntry(manifest.ChunkId);

                MemoryStream ms = new MemoryStream();
                using (NativeWriter chunkWriter = new NativeWriter(ms))
                {
                    foreach (Tuple<int, LegacyFileEntry.ChunkCollectorInstance, string> inst in manifests[entry])
                    {
                        chunkWriter.Write(inst.Item1);
                        chunkWriter.WriteNullTerminatedString(inst.Item3);
                        chunkWriter.Write(inst.Item2.ChunkId);
                        chunkWriter.Write(inst.Item2.Offset);
                        chunkWriter.Write(inst.Item2.CompressedOffset);
                        chunkWriter.Write(inst.Item2.CompressedSize);
                        chunkWriter.Write(inst.Item2.Size);
                    }

                    writer.AddResource(new LegacyResource(
                        collectorChunkEntry.Name, entry.Name, ms.ToArray(),
                        collectorChunkEntry.EnumerateBundles(), writer.ResourceManifest));
                }
            }
        }

        public bool SaveToProject(NativeWriter writer)
        {
            writer.WriteNullTerminatedString("legacy");

            long sizePosition = writer.Position;
            writer.Write(0xDEADBEEF);

            int count = 0;
            foreach (LegacyFileEntry lfe in App.AssetManager.EnumerateCustomAssets("legacy", modifiedOnly: true))
            {
                LegacyFileEntry.ChunkCollectorInstance inst = lfe.CollectorInstances[0].ModifiedEntry;
                writer.WriteNullTerminatedString(lfe.Name);
                FrostyProject.SaveLinkedAssets(lfe, writer);

                writer.Write(lfe.ChunkId);
                writer.Write(inst.Offset);
                writer.Write(inst.CompressedOffset);
                writer.Write(inst.CompressedSize);
                writer.Write(inst.Size);

                count++;
            }

            writer.Position = sizePosition;
            writer.Write(count);
            writer.Position = writer.Length;
            return true;
        }

        public void LoadFromProject(DbObject project)
        {
            uint version = project.GetValue<uint>("version");
            DbObject modifiedObj = project.GetValue<DbObject>("modified");

            if (!modifiedObj.HasValue("legacy"))
                return;

            foreach (DbObject legacyObj in modifiedObj.GetValue<DbObject>("legacy"))
            {
                LegacyFileEntry entry = App.AssetManager.GetCustomAssetEntry<LegacyFileEntry>("legacy", legacyObj.GetValue<string>("name"));
                if (entry != null)
                {
                    FrostyProject.LoadLinkedAssets(legacyObj, entry, version);
                    foreach (LegacyFileEntry.ChunkCollectorInstance inst in entry.CollectorInstances)
                    {
                        inst.ModifiedEntry = new LegacyFileEntry.ChunkCollectorInstance
                        {
                            ChunkId = legacyObj.GetValue<Guid>("chunkId"),
                            Offset = legacyObj.GetValue<long>("offset"),
                            CompressedOffset = legacyObj.GetValue<long>("compressedOffset"),
                            CompressedSize = legacyObj.GetValue<long>("compressedSize"),
                            Size = legacyObj.GetValue<long>("size")
                        };
                    }
                }
            }
        }

        public void LoadFromProject(uint version, NativeReader reader, string type)
        {
            if (type != "legacy")
                return;

            int numItems = reader.ReadInt();
            for (int i = 0; i < numItems; i++)
            {
                string name = reader.ReadNullTerminatedString();
                List<AssetEntry> linkedEntries = FrostyProject.LoadLinkedAssets(reader);
                Guid chunkId = reader.ReadGuid();
                long offset = reader.ReadLong();
                long compressedOffset = reader.ReadLong();
                long compressedSize = reader.ReadLong();
                long size = reader.ReadLong();

                LegacyFileEntry entry = App.AssetManager.GetCustomAssetEntry<LegacyFileEntry>("legacy", name);

                if (version < 12)
                {
                    // retroactively change guid to a determinstic guid
                    ChunkAssetEntry oldEntry = App.AssetManager.GetChunkEntry(chunkId);
                    Stream stream = App.AssetManager.GetChunk(oldEntry);

                    chunkId = LegacyFileManager.GenerateDeterministicGuid(entry);

                    // remove old chunk
                    App.AssetManager.RevertAsset(oldEntry);
                    App.AssetManager.AddChunk(NativeReader.ReadInStream(stream), chunkId);

                    // and add new chunk
                    ChunkAssetEntry newEntry = App.AssetManager.GetChunkEntry(chunkId);
                    newEntry.ModifiedEntry.IsDirty = false;
                    newEntry.IsDirty = false;
                    newEntry.ModifiedEntry.UserData = "legacy;" + entry.Name;
                    newEntry.ModifiedEntry.AddToChunkBundle = true;

                    linkedEntries.Clear();
                    entry.LinkAsset(newEntry);
                }

                if (entry != null)
                {
                    entry.LinkedAssets.AddRange(linkedEntries);
                    foreach (LegacyFileEntry.ChunkCollectorInstance inst in entry.CollectorInstances)
                    {
                        inst.ModifiedEntry = new LegacyFileEntry.ChunkCollectorInstance
                        {
                            ChunkId = chunkId,
                            Offset = offset,
                            CompressedOffset = compressedOffset,
                            CompressedSize = compressedSize,
                            Size = size
                        };
                    }
                }
            }
        }

        /// <summary>
        /// Handles the loading and merging of the custom data
        /// </summary>
        public object Load(object existing, byte[] newData)
        {
            List<ModLegacyFileEntry> entries = (List<ModLegacyFileEntry>)existing ?? new List<ModLegacyFileEntry>();

            using (NativeReader reader = new NativeReader(new MemoryStream(newData)))
            {
                while (reader.Position < reader.Length)
                {
                    int hash = reader.ReadInt();
                    string name = reader.ReadNullTerminatedString();

                    int idx = entries.FindIndex((ModLegacyFileEntry a) => a.Hash == hash);
                    if (idx != -1)
                        entries.RemoveAt(idx);

                    ModLegacyFileEntry newEntry = new ModLegacyFileEntry
                    {
                        Hash = hash,
                        Name = name,
                        ChunkId = reader.ReadGuid(),
                        Offset = reader.ReadLong(),
                        CompressedOffset = reader.ReadLong(),
                        CompressedSize = reader.ReadLong(),
                        Size = reader.ReadLong()
                    };
                    entries.Add(newEntry);
                }
            }
            return entries;
        }

        /// <summary>
        /// Handles the actual modification of the base data with the custom data
        /// </summary>
        public void Modify(AssetEntry origEntry, AssetManager am, RuntimeResources runtimeResources, object data, out byte[] outData)
        {
            ChunkAssetEntry chunkEntry = origEntry as ChunkAssetEntry;
            List<ModLegacyFileEntry> modEntries = (List<ModLegacyFileEntry>)data;
            App.Logger.Log("LegacyHandler.Modify called for chunk {0}", chunkEntry.Id);
            App.Logger.Log("Mod entries count: {0}", modEntries.Count);

            foreach (ModLegacyFileEntry modEntry in modEntries)
                App.Logger.Log("  name={0} hash={1}", modEntry.Name, modEntry.Hash);

            // build lookup by hash for quick matching
            Dictionary<int, ModLegacyFileEntry> modLookup = new Dictionary<int, ModLegacyFileEntry>();
            foreach (ModLegacyFileEntry e in modEntries)
                modLookup[e.Hash] = e;

            using (NativeReader reader = new NativeReader(am.GetChunk(am.GetChunkEntry(chunkEntry.Id))))
            {
                // --- read header (48 bytes) ---
                uint numEntries = reader.ReadUInt();
                uint headerSize = reader.ReadUInt();   // always 48
                uint unk0 = reader.ReadUInt();
                uint block1Count = reader.ReadUInt();
                uint stringSectionOff = reader.ReadUInt();
                uint block1Unk = reader.ReadUInt();
                uint block2Count = reader.ReadUInt();
                uint stringsStartOff = reader.ReadUInt();
                uint block2Unk = reader.ReadUInt();
                byte[] headerTail = reader.ReadBytes(12);
                App.Logger.Log("LegacyHandler.Modify: numEntries={0} headerSize={1} stringSectionOff={2} stringsStartOff={3}",
    numEntries, headerSize, stringSectionOff, stringsStartOff);

                // 32-byte prefix between entry table end and strings section
                reader.Position = stringSectionOff;
                byte[] stringPrefix = reader.ReadBytes(32);

                // 48-byte subheader before actual strings
                byte[] stringsSubheader = reader.ReadBytes(48);

                // --- read all entry records ---
                reader.Position = headerSize;
                var parsedEntries = new List<(long strOff, long compOff, long compSize, long offset, long size, Guid guid)>();
                for (int i = 0; i < numEntries; i++)
                {
                    long strOff = reader.ReadLong();
                    long compOff = reader.ReadLong();
                    long compSize = reader.ReadLong();
                    long off = reader.ReadLong();
                    long sz = reader.ReadLong();
                    Guid guid = reader.ReadGuid();
                    parsedEntries.Add((strOff, compOff, compSize, off, sz, guid));
                }

                // --- read all entry names ---
                var entryNames = new List<string>();
                foreach (var e in parsedEntries)
                {
                    reader.Position = e.strOff;
                    entryNames.Add(reader.ReadNullTerminatedString());
                }

                // find end of last referenced string
                long maxStrOff = 0;
                foreach (var e in parsedEntries)
                    if (e.strOff > maxStrOff) maxStrOff = e.strOff;
                reader.Position = maxStrOff;
                reader.ReadNullTerminatedString();
                long lastStrEnd = reader.Position;

                // calculate index table position and read unreferenced strings
                int origIndexTableSize = 12 + ((int)numEntries + 2) * 4;
                long origTotalSize = reader.Length;
                long indexTableOff = origTotalSize - origIndexTableSize;
                App.Logger.Log("LegacyHandler.Modify: origIndexTableSize={0} origTotalSize={1} indexTableOff={2}",
    origIndexTableSize, origTotalSize, indexTableOff);

                byte[] unreferencedStrings = reader.ReadBytes((int)(indexTableOff - lastStrEnd));

                // read index table header (12 bytes, preserved verbatim)
                reader.Position = indexTableOff;
                byte[] indexTableHeader = reader.ReadBytes(12);

                // --- build new entry list ---
                var newEntries = new List<(string name, long compOff, long compSize, long offset, long size, Guid guid)>();
                HashSet<int> matched = new HashSet<int>();

                // patch existing entries
                for (int i = 0; i < parsedEntries.Count; i++)
                {
                    var e = parsedEntries[i];
                    string name = entryNames[i];
                    int hash = Fnv1.HashString(name);

                    if (modLookup.TryGetValue(hash, out ModLegacyFileEntry mod))
                    {
                        matched.Add(hash);
                        newEntries.Add((name, mod.CompressedOffset, mod.CompressedSize, mod.Offset, mod.Size, mod.ChunkId));
                    }
                    else
                    {
                        newEntries.Add((name, e.compOff, e.compSize, e.offset, e.size, e.guid));
                    }
                }

                // add new entries (duplicates - hashes not found in original)
                foreach (ModLegacyFileEntry mod in modEntries)
                {
                    if (!matched.Contains(mod.Hash))
                        newEntries.Add((mod.Name, mod.CompressedOffset, mod.CompressedSize, mod.Offset, mod.Size, mod.ChunkId));
                }

                // sort alphabetically — game uses binary search by name
                newEntries.Sort((a, b) => string.Compare(
                    a.name.ToLowerInvariant(),
                    b.name.ToLowerInvariant(),
                    StringComparison.Ordinal));

                // --- recalculate layout ---
                uint newNumEntries = (uint)newEntries.Count;
                uint newStringSectionOff = headerSize + newNumEntries * 56;
                uint newStringsStartOff = newStringSectionOff + 32;
                uint newActualStringsOff = newStringsStartOff + 48;

                // build string blob and record str offsets
                var strOffsets = new List<long>();
                byte[] stringBlob;
                using (MemoryStream ms = new MemoryStream())
                {
                    foreach (var e in newEntries)
                    {
                        strOffsets.Add(newActualStringsOff + ms.Length);
                        byte[] nameBytes = System.Text.Encoding.UTF8.GetBytes(e.name + "\0");
                        ms.Write(nameBytes, 0, nameBytes.Length);
                    }
                    if (unreferencedStrings.Length > 0)
                        ms.Write(unreferencedStrings, 0, unreferencedStrings.Length);
                    stringBlob = ms.ToArray();
                }

                // rebuild index table
                byte[] newIndexTable;
                using (NativeWriter idxWriter = new NativeWriter(new MemoryStream()))
                {
                    idxWriter.Write(indexTableHeader);
                    for (int i = 0; i < newEntries.Count; i++)
                        idxWriter.Write((uint)(headerSize + i * 56));
                    idxWriter.Write(newStringSectionOff);
                    idxWriter.Write(newStringSectionOff + 16);
                    newIndexTable = idxWriter.ToByteArray();
                }

               

                // --- write output ---
                using (NativeWriter writer = new NativeWriter(new MemoryStream()))
                {
                    // header
                    writer.Write(newNumEntries);
                    writer.Write(headerSize);
                    writer.Write(unk0);
                    writer.Write(block1Count);
                    writer.Write(newStringSectionOff);
                    writer.Write(block1Unk);
                    writer.Write(block2Count);
                    writer.Write(newStringsStartOff);
                    writer.Write(block2Unk);
                    writer.Write(headerTail);

                    // entry table
                    for (int i = 0; i < newEntries.Count; i++)
                    {
                        var e = newEntries[i];
                        writer.Write(strOffsets[i]);
                        writer.Write(e.compOff);
                        writer.Write(e.compSize);
                        writer.Write(e.offset);
                        writer.Write(e.size);
                        writer.Write(e.guid);
                    }

                    // string section
                    writer.Write(stringPrefix);
                    writer.Write(stringsSubheader);
                    writer.Write(stringBlob);
                    writer.Write(newIndexTable);

                    outData = Utils.CompressFile(writer.ToByteArray());
                    chunkEntry.Sha1 = Utils.GenerateSha1(outData);
                    chunkEntry.Size = outData.Length;
                    chunkEntry.IsTocChunk = true;
                }
            }
        }

        public IEnumerable<string> GetResourceActions(string name, byte[] data)
        {
            return new List<string>();
        }
    }
}
