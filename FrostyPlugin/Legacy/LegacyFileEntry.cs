using Frosty.Hash;
using FrostySdk.Managers;
using System;
using System.Collections.Generic;

namespace Frosty.Core.Legacy
{
    public sealed class LegacyFileEntry : AssetEntry
    {
        public class ChunkCollectorInstance
        {
            public ChunkCollectorInstance ModifiedEntry { get; set; }
            public bool IsModified => ModifiedEntry != null;

            public EbxAssetEntry Entry;
            public Guid ChunkId;
            public long Offset;
            public long CompressedOffset;
            public long CompressedSize;
            public long Size;
        }

        public Guid ChunkId
        {
            get
            {
                if (CollectorInstances.Count == 0)
                    return Guid.Empty;

                return CollectorInstances[0].IsModified ? CollectorInstances[0].ModifiedEntry.ChunkId : CollectorInstances[0].ChunkId;
            }
        }

        public int NameHash => Fnv1.HashString(Name);

        public override string AssetType => "legacy";

        /// <summary>
        /// True if this entry was created via DuplicateAsset (does not exist in base game data).
        /// Used by SaveToProject / LoadFromProject to persist and restore duplicated entries.
        /// </summary>
        public bool IsAdded { get; set; } = false;

        // Cached derived-from-Name fields. Name is only ever set at construction
        // time (see LegacyFileManager — both the loader path and the DuplicateAsset
        // path use object-initializer syntax `new LegacyFileEntry { Name = ... }`,
        // there is no code path that re-assigns .Name after creation). So we can
        // safely compute Filename / Path / Type once and cache them.
        //
        // Without these caches, every sort comparison in the Legacy Explorer
        // would re-walk Name.LastIndexOf('/') + Substring multiple times per
        // comparison — for a folder with ~20k entries that means hundreds of
        // thousands of redundant string allocations per folder click. The
        // cache converts each per-comparison call into a single field read.
        private string cachedFilename;
        private string cachedPath;
        private string cachedType;

        public override string Type
        {
            get
            {
                if (cachedType == null)
                {
                    int lastPeriodIndex = Name.LastIndexOf('.');
                    cachedType = lastPeriodIndex == -1 ? "" : Name.Substring(lastPeriodIndex + 1).ToUpper();
                }
                return cachedType;
            }
            set { /* no-op — Type is derived from Name and cannot be set */ }
        }

        public override string Filename
        {
            get
            {
                if (cachedFilename == null)
                {
                    // base.Filename extracts the part of Name after the last '/',
                    // then we strip the extension. Both involve string allocations,
                    // so we cache the result.
                    string baseFilename = base.Filename;
                    int lastPeriodIndex = baseFilename.LastIndexOf('.');
                    cachedFilename = lastPeriodIndex == -1 ? baseFilename : baseFilename.Substring(0, lastPeriodIndex);
                }
                return cachedFilename;
            }
        }

        public override string Path
        {
            get
            {
                if (cachedPath == null)
                {
                    int id = Name.LastIndexOf('/');
                    cachedPath = id == -1 ? "" : Name.Substring(0, id);
                }
                return cachedPath;
            }
        }

        public override bool IsModified => CollectorInstances.Count != 0 && CollectorInstances[0].IsModified;

        public override bool IsDirty => App.AssetManager.GetChunkEntry(ChunkId).IsDirty;

        public override void ClearModifications()
        {
            foreach (ChunkCollectorInstance inst in CollectorInstances)
                inst.ModifiedEntry = null;
        }

        public List<ChunkCollectorInstance> CollectorInstances = new List<ChunkCollectorInstance>();
    }
}
