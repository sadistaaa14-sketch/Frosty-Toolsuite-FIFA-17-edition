using Frosty.Core.Legacy;
using FrostySdk.Ebx;
using FrostySdk.Managers;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

namespace Frosty.Core
{
    /// <summary>
    /// Resolves the identifiers that link an <see cref="AssetEntry"/> back to the raw data it
    /// is built from - the chunk id(s) that hold its bytes and, for resources, the resource id.
    ///
    /// These are used by the various "Copy Chunk Id" / "Copy Res Id" context menu commands so
    /// that the same lookup logic is available from every explorer (EBX Explorer, Legacy
    /// Explorer and the Chunk/Res Editor) instead of being re-implemented per window.
    /// </summary>
    public static class AssetReferenceUtils
    {
        /// <summary>
        /// How many bytes of a resource's data stream we are willing to scan for a chunk
        /// reference. A resource's own metadata (which is where the chunk guid lives for
        /// textures, atlases, sounds, meshes, ...) sits at the very start of the res stream,
        /// so a bounded scan keeps this cheap even for multi-megabyte resources while still
        /// covering every real-world layout we have seen.
        /// </summary>
        private const int MaxResourceScanBytes = 0x10000;

        /// <summary>
        /// Field names ebx assets use to point at the resource that holds their data. A
        /// TextureAsset points at a Texture res through "Resource", and the mesh assets point at
        /// their MeshSet res through "MeshSetResource" - the same fields the mesh and texture
        /// plugins read, so looking for them covers every asset type that references a res.
        /// </summary>
        private static readonly string[] ResRidMemberNames =
        {
            "Resource",
            "MeshSetResource",
            "TextureResource",
            "ResRid",
        };

        /// <summary>
        /// How deep and how wide an ebx object graph is walked when looking for chunk ids. The ids
        /// we are after sit on the asset's own objects - a sound wave keeps one on every entry of
        /// <c>Chunks</c> - so a shallow, bounded walk finds them without paying for a whole asset.
        /// </summary>
        private const int MaxGraphDepth = 10;

        /// <summary>
        /// How many values the walk above may look at before it gives up. A folder export runs it
        /// once per asset, so the bound is what keeps a pathological asset from stalling a run; the
        /// resulting list stays cached per asset as well.
        /// </summary>
        private const int MaxGraphNodes = 20000;

        /// <summary>
        /// The members of one type, reflected once and then reused. The walk asks for the members of
        /// an object on every visit, and an export walks the graph of every asset under a folder; the
        /// reflection itself - not the reads - is what made that expensive, because a folder holds
        /// thousands of objects of a few dozen types.
        /// </summary>
        private sealed class MemberList
        {
            public PropertyInfo[] Properties;
            public FieldInfo[] Fields;
        }

        private static readonly Dictionary<Type, MemberList> memberCache = new Dictionary<Type, MemberList>();
        private static readonly object memberCacheLock = new object();

        /// <summary>
        /// Reference equality for the walked objects. An ebx graph points back at itself (a pointer
        /// reference to an object that refers to the one holding it), so identity - not Equals - is
        /// what stops the walk from going round in circles.
        /// </summary>
        private sealed class ReferenceComparer : IEqualityComparer<object>
        {
            public static readonly ReferenceComparer Instance = new ReferenceComparer();

            bool IEqualityComparer<object>.Equals(object x, object y) => ReferenceEquals(x, y);

            int IEqualityComparer<object>.GetHashCode(object obj)
                => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
        }

        /// <summary>
        /// Cached results per asset. Resource chunk ids can only be found by reading the
        /// resource, and reading a resource fully decompresses it - far too expensive to repeat
        /// every time a context menu is opened. The cache entry remembers which
        /// <see cref="AssetEntry.ModifiedEntry"/> the ids were resolved from, so the moment the
        /// asset is modified (a texture import assigns a brand new chunk id) the stale ids are
        /// discarded automatically.
        /// </summary>
        private sealed class ChunkIdCacheEntry
        {
            public ModifiedAssetEntry Modified;
            public List<Guid> Ids;

            /// <summary>Set when the asset could not be read at all, which is not the same as naming
            /// no chunk: one means look at the game, the other means look at the asset.</summary>
            public bool Unreadable;
        }

        private static readonly Dictionary<AssetEntry, ChunkIdCacheEntry> chunkIdCache =
            new Dictionary<AssetEntry, ChunkIdCacheEntry>();

        /// <summary>
        /// The guids an asset names whether or not the chunk behind them is loaded. Kept apart from the
        /// cache above because it answers a different question - what does this asset refer to - and it
        /// is what a run reports when it wrote no chunk file at all.
        /// </summary>
        private static readonly Dictionary<AssetEntry, ChunkIdCacheEntry> namedChunkIdCache =
            new Dictionary<AssetEntry, ChunkIdCacheEntry>();

        private static readonly object chunkIdCacheLock = new object();

        /// <summary>
        /// Returns every chunk guid that is associated with <paramref name="entry"/>.
        /// The list is empty when the asset has no associated chunk (for example a plain
        /// ebx asset, or a resource type that does not stream from a chunk).
        /// </summary>
        public static List<Guid> GetChunkIds(AssetEntry entry)
        {
            List<Guid> results = new List<Guid>();
            if (entry == null)
                return results;

            HashSet<Guid> seen = new HashSet<Guid>();

            // Chunks that were linked to this asset, which happens whenever the asset is imported
            // or modified during this session (a sound wave, texture or atlas that now owns a chunk
            // records it here). This is free - it is just a list already held in memory - and it is
            // the only way an ebx asset can report a chunk, since ebx assets have no chunk of their
            // own until one is linked to them.
            foreach (AssetEntry linked in entry.LinkedAssets)
            {
                if (linked is ChunkAssetEntry linkedChunk && linkedChunk.Id != Guid.Empty && seen.Add(linkedChunk.Id))
                    results.Add(linkedChunk.Id);
            }

            // A chunk entry is its own id.
            if (entry is ChunkAssetEntry chunkEntry)
            {
                if (chunkEntry.Id != Guid.Empty && seen.Add(chunkEntry.Id))
                    results.Add(chunkEntry.Id);
                return results;
            }

            // Legacy files are a thin wrapper around a chunk - the id is known up front.
            if (entry is LegacyFileEntry legacyEntry)
            {
                Guid id = legacyEntry.ChunkId;
                if (id != Guid.Empty && seen.Add(id))
                    results.Add(id);
                return results;
            }

            // Resources keep the chunk reference inside their own data, so this is the one case that
            // has to read the asset (and is therefore cached).
            if (entry is ResAssetEntry resEntry)
            {
                foreach (Guid id in GetResChunkIds(resEntry))
                {
                    if (seen.Add(id))
                        results.Add(id);
                }
                return results;
            }

            // An ebx asset holds no data of its own: a texture or mesh asset points at the resource
            // that does. So follow that reference - the chunk that streams the asset's bytes belongs
            // to the resource, and without this step "Copy Chunk Id" would stay greyed out for every
            // texture and mesh in the EBX Explorer.
            if (entry is EbxAssetEntry ebxEntry)
            {
                // Read once: every rule below starts at the asset's root object, and reading an ebx
                // means decompressing it.
                object root = ReadRootObject(ebxEntry, "Copy Chunk Id");

                ResAssetEntry referenced = GetReferencedResEntry(ebxEntry, root);
                if (referenced != null)
                    AddAll(GetResChunkIds(referenced), seen, results);

                // Sounds are the case the rid fields do not cover. A SoundWaveAsset - and a
                // LocalizedWaveAsset, which keeps its samples in variation chunks - carries its
                // resource under its own name rather than behind a rid member, so an asset that
                // names no resource still has one to scan here.
                ResAssetEntry named = GetNamedResEntry(ebxEntry);
                if (named != null && !ReferenceEquals(named, referenced))
                    AddAll(GetResChunkIds(named), seen, results);

                // And the last place a chunk can be named is the asset itself: a sound wave writes
                // the id of the chunk holding its audio into its own objects - a SoundWaveAsset keeps
                // a SoundDataChunk (with its ChunkId) per chunk in Chunks, and points at no resource
                // at all, which is why its chunks used to be invisible.
                //
                // The walk is the expensive part of a folder export, so it is only run when it can
                // tell us something: an asset whose resource already gave up its chunks, and whose own
                // members mention no chunk at all - a texture or a mesh - is left alone.
                if (root != null && (results.Count == 0 || NamesChunks(root)))
                    AddAll(GetEbxChunkIds(ebxEntry, root), seen, results);
            }

            return results;
        }

        /// <summary>
        /// Adds <paramref name="ids"/> to <paramref name="results"/>, skipping empties and anything
        /// that has already been reported, so a chunk named twice (through the res and through the
        /// asset) is still listed once.
        /// </summary>
        private static void AddAll(List<Guid> ids, HashSet<Guid> seen, List<Guid> results)
        {
            if (ids == null)
                return;

            foreach (Guid id in ids)
            {
                if (id != Guid.Empty && seen.Add(id))
                    results.Add(id);
            }
        }

        /// <summary>
        /// Returns the resource id (ResRid) of <paramref name="entry"/>, or 0 when the entry has
        /// no resource behind it. A resource entry is its own rid; an ebx asset reports either the
        /// rid it references (a TextureAsset yields the Texture resource it draws from) or, when it
        /// references none, the resource that carries the asset's own name - which is how a sound
        /// finds its resource.
        /// </summary>
        public static ulong GetResId(AssetEntry entry)
        {
            if (entry is ResAssetEntry resEntry)
                return resEntry.ResRid;

            if (entry is EbxAssetEntry ebxEntry)
                return GetReferencedResRid(ebxEntry);

            return 0;
        }

        /// <summary>
        /// Reads the resource id an ebx asset points at, by looking for the well known rid fields
        /// on its root object. When an asset has none of those fields, the resource that carries the
        /// asset's own name is the one that belongs to it: the sound editors already resolve a sound
        /// that way, and for those assets it is the only reference that exists. Returns 0 when there
        /// is no resource either way, or when the root object cannot be read.
        /// </summary>
        private static ulong GetReferencedResRid(EbxAssetEntry entry)
            => GetRootResRid(entry, ReadRootObject(entry, "Copy Res Id"));

        /// <summary>
        /// The resource id an ebx root object points at, falling back to the resource that carries
        /// the asset's own name. Split out from <see cref="GetReferencedResRid"/> so a caller that
        /// has already read the asset does not decompress it a second time.
        /// </summary>
        private static ulong GetRootResRid(EbxAssetEntry entry, object root)
        {
            ulong rid = ReadRidMember(root);
            if (rid != 0)
                return rid;

            ResAssetEntry named = GetNamedResEntry(entry);
            return named == null ? 0 : named.ResRid;
        }

        /// <summary>
        /// The root object of an ebx asset, or null when the asset cannot be read. A shared helper
        /// because both the rid lookup and the chunk walk start there.
        /// </summary>
        private static object ReadRootObject(EbxAssetEntry entry, string what)
        {
            try
            {
                return App.AssetManager.GetEbx(entry)?.RootObject;
            }
            catch (Exception ex)
            {
                App.Logger?.Log(what + ": could not load asset '{0}': {1}", entry?.Name, ex.Message);
                return null;
            }
        }

        /// <summary>
        /// The resource that carries the same name as <paramref name="entry"/> itself, or null when
        /// there is none. A sound asset keeps its samples, offsets and chunk table in a resource
        /// named exactly like the asset, which is the reference the sound editors use; nothing on
        /// the asset points at it.
        /// </summary>
        private static ResAssetEntry GetNamedResEntry(EbxAssetEntry entry)
        {
            if (entry == null || string.IsNullOrEmpty(entry.Name))
                return null;

            try
            {
                // The lookup is case insensitive by construction: resource names are lowercased
                // when they are loaded, as is the name looked up here.
                return App.AssetManager.GetResEntry(entry.Name);
            }
            catch (Exception ex)
            {
                App.Logger?.Log("Could not look up a res named '{0}': {1}", entry.Name, ex.Message);
                return null;
            }
        }

        /// <summary>
        /// Returns the resource entry an ebx asset points at, or null when it references none (or
        /// the referenced rid is not loaded, e.g. it was overridden by a mod).
        /// </summary>
        private static ResAssetEntry GetReferencedResEntry(EbxAssetEntry entry, object root)
        {
            ulong rid = GetRootResRid(entry, root);
            if (rid == 0)
                return null;

            try
            {
                return App.AssetManager.GetResEntry(rid);
            }
            catch (Exception ex)
            {
                App.Logger?.Log("Copy Chunk Id: could not look up res 0x{0} for '{1}': {2}",
                    rid.ToString("X16"), entry.Name, ex.Message);
                return null;
            }
        }

        /// <summary>
        /// Finds the first of the known rid members on an ebx root object and returns its value as
        /// a ulong, or 0 when none of them exists. Reflection is used rather than the dynamic binder
        /// because most asset types simply do not have these fields, and a missing member must be a
        /// normal outcome rather than an exception.
        /// </summary>
        private static ulong ReadRidMember(object root)
        {
            if (root == null)
                return 0;

            Type type = root.GetType();
            foreach (string name in ResRidMemberNames)
            {
                object value;
                try
                {
                    System.Reflection.PropertyInfo property = type.GetProperty(name);
                    if (property != null)
                    {
                        value = property.GetValue(root);
                    }
                    else
                    {
                        System.Reflection.FieldInfo field = type.GetField(name);
                        if (field == null)
                            continue;
                        value = field.GetValue(root);
                    }
                }
                catch
                {
                    continue;
                }

                if (value == null)
                    continue;

                if (TryConvertToRid(value, out ulong rid))
                    return rid;
            }

            return 0;
        }

        /// <summary>
        /// Converts a member value into a resource id.
        ///
        /// The rid is stored in one of two shapes. A plain unsigned number converts directly. A
        /// <see cref="ResourceRef"/> - which is what the type library uses for members such as a
        /// texture's "Resource" and a mesh's "MeshSetResource" - is a struct whose value is only
        /// reachable through its implicit conversion to ulong, and Convert.ToUInt64 cannot see that
        /// conversion (it throws InvalidCastException). Unwrapping it here is what stops every res
        /// referencing asset from looking like it has no resource at all.
        /// </summary>
        private static bool TryConvertToRid(object value, out ulong rid)
        {
            rid = 0;
            if (value == null)
                return false;

            if (value is ResourceRef resourceRef)
            {
                rid = resourceRef;
                return true;
            }

            try
            {
                rid = Convert.ToUInt64(value);
                return true;
            }
            catch
            {
                // Not a numeric rid (some fields share a name but hold something else).
                return false;
            }
        }

        /// <summary>
        /// Formats a chunk id the same way the rest of the editor does (lower case, dash separated).
        /// </summary>
        public static string FormatChunkId(Guid id) => id.ToString();

        /// <summary>
        /// Formats a resource id in the hexadecimal form used by Frosty's res rid fields.
        /// </summary>
        public static string FormatResId(ulong rid) => "0x" + rid.ToString("X16");

        /// <summary>
        /// Finds the chunk(s) a resource streams from by scanning the start of its data. The result
        /// is cached against the entry's current modification state so that repeated context menu
        /// openings do not re-read (and re-decompress) the resource.
        /// </summary>
        private static List<Guid> GetResChunkIds(ResAssetEntry entry)
            => CachedChunkIds(entry, () => ScanResForChunkIds(entry));

        /// <summary>
        /// Finds the chunk ids written into an ebx asset's own object graph. Cached the same way the
        /// resource scan is - the walk reads every member of every reachable object, which is cheap
        /// once but is not worth repeating for each context menu opening.
        /// </summary>
        private static List<Guid> GetEbxChunkIds(EbxAssetEntry entry, object root)
        {
            // The "what does it name" answer is primed from the same read: a report asks for it for
            // exactly the assets the walk runs on, and reading an ebx twice is the one thing in here
            // that is not worth doing.
            CachedNamedChunkIds(entry, () => root == null ? null : CollectChunkGuidsFromMembers(root, "chunk"), out _);
            return CachedChunkIds(entry, () => CollectChunkGuids(root));
        }

        /// <summary>
        /// The chunk ids an asset names, cached. The asset is read on a cache miss, which happens only
        /// where the walk did not run (the asset found its chunks elsewhere).
        /// </summary>
        private static List<Guid> GetNamedChunkIds(EbxAssetEntry entry)
            => CachedNamedChunkIds(entry, () => ResolveNamedChunkIds(entry), out _);

        private static List<Guid> ResolveNamedChunkIds(EbxAssetEntry entry)
        {
            object root = ReadRootObject(entry, "Chunk report");
            return root == null ? null : CollectChunkGuidsFromMembers(root, "chunk");
        }

        /// <summary>
        /// Remembers the ids an asset names until the asset is modified, and reports whether it could
        /// be read at all (<paramref name="unreadable"/>).
        /// </summary>
        private static List<Guid> CachedNamedChunkIds(AssetEntry entry, Func<List<Guid>> resolve, out bool unreadable)
        {
            lock (chunkIdCacheLock)
            {
                ModifiedAssetEntry currentModified = entry.ModifiedEntry;
                if (namedChunkIdCache.TryGetValue(entry, out ChunkIdCacheEntry cached)
                    && ReferenceEquals(cached.Modified, currentModified))
                {
                    unreadable = cached.Unreadable;
                    return new List<Guid>(cached.Ids);
                }

                List<Guid> ids = resolve();
                unreadable = ids == null;
                namedChunkIdCache[entry] = new ChunkIdCacheEntry
                {
                    Modified = currentModified,
                    Ids = ids ?? new List<Guid>(),
                    Unreadable = unreadable,
                };

                return new List<Guid>(ids ?? new List<Guid>());
            }
        }

        /// <summary>
        /// True when the root object has a member whose name mentions a chunk. That is the shape
        /// every sound asset has - Chunks, and a ChunkId inside each entry - and it is the cheap way
        /// to tell one from a texture or a mesh without walking the whole graph.
        /// </summary>
        private static bool NamesChunks(object root)
        {
            MemberList members = Members(root.GetType());
            foreach (PropertyInfo property in members.Properties)
            {
                if (NamesChunk(property.Name))
                    return true;
            }

            foreach (FieldInfo field in members.Fields)
            {
                if (NamesChunk(field.Name))
                    return true;
            }

            return false;
        }

        private static bool NamesChunk(string memberName)
            => !string.IsNullOrEmpty(memberName) && memberName.IndexOf("chunk", StringComparison.OrdinalIgnoreCase) >= 0;

        private static MemberList Members(Type type)
        {
            lock (memberCacheLock)
            {
                if (memberCache.TryGetValue(type, out MemberList cached))
                    return cached;

                MemberList members = new MemberList
                {
                    Properties = type.GetProperties(BindingFlags.Public | BindingFlags.Instance),
                    Fields = type.GetFields(BindingFlags.Public | BindingFlags.Instance),
                };

                memberCache[type] = members;
                return members;
            }
        }

        /// <summary>
        /// Why an ebx asset contributed no chunk to a run, in one sentence, or null when it did not
        /// fail to. Two very different situations look identical from the outside - an export folder
        /// that contains no .chunk file - so a run reports which one it hit: the asset names no chunk
        /// at all, or it names chunks this profile does not have (localized commentary lives in
        /// per-language bundles, and a profile that did not load one has no bytes to write).
        /// </summary>
        public static string ExplainNoChunks(AssetEntry entry)
        {
            EbxAssetEntry ebxEntry = entry as EbxAssetEntry;
            if (ebxEntry == null)
            {
                // A resource holds its chunk reference inside its own data, so there is only one way
                // for it to have none: nothing in that data named a chunk this profile has.
                return entry is ResAssetEntry ? "its data holds no chunk id" : null;
            }

            List<Guid> named = CachedNamedChunkIds(ebxEntry, () => ResolveNamedChunkIds(ebxEntry), out bool unreadable);
            if (unreadable)
                return "the asset could not be read";

            if (named.Count == 0)
                return "it names no chunk";

            foreach (Guid id in named)
            {
                if (IsLoadedChunk(id))
                    return null;
            }

            return "it names " + named.Count + " chunk id(s) this profile does not have";
        }

        /// <summary>
        /// Remembers what <paramref name="resolve"/> found for an entry until the entry is modified.
        /// Every resolution here reads the asset (fully decompressing a resource, or walking an
        /// object graph), so none of them may run twice for the same unmodified asset.
        /// </summary>
        private static List<Guid> CachedChunkIds(AssetEntry entry, Func<List<Guid>> resolve)
        {
            lock (chunkIdCacheLock)
            {
                ModifiedAssetEntry currentModified = entry.ModifiedEntry;
                if (chunkIdCache.TryGetValue(entry, out ChunkIdCacheEntry cached)
                    && ReferenceEquals(cached.Modified, currentModified))
                {
                    return new List<Guid>(cached.Ids);
                }

                List<Guid> ids = resolve() ?? new List<Guid>();
                chunkIdCache[entry] = new ChunkIdCacheEntry { Modified = currentModified, Ids = ids };
                return new List<Guid>(ids);
            }
        }

        /// <summary>
        /// Collects every chunk id named inside an ebx object graph. This exists because of sounds:
        /// a SoundWaveAsset keeps the ids of the chunks holding its audio in its own objects (one
        /// <c>ChunkId</c> per entry of <c>Chunks</c>) and points at no resource that could be
        /// scanned for them, so without this walk the audio chunks of a sound are reported as "has
        /// no associated chunk" - and a bulk export of the folder writes no audio at all.
        ///
        /// A guid is accepted only when the asset manager knows it as a chunk, which is what stops
        /// an unrelated guid (a file guid, an instance guid) from being mistaken for one.
        /// </summary>
        public static List<Guid> CollectChunkGuids(object root) => CollectChunkGuids(root, IsLoadedChunk);

        /// <summary>
        /// The same walk with the "is this a chunk" test supplied by the caller, so that the rule can
        /// be exercised without a loaded game.
        /// </summary>
        public static List<Guid> CollectChunkGuids(object root, Func<Guid, bool> isChunk)
            => WalkForGuids(root, (member, id) => isChunk != null && isChunk(id));

        /// <summary>
        /// Every guid held by a member whose name mentions <paramref name="memberToken"/>, whether or
        /// not the chunk behind it is loaded. The game's own sound classes name their ids this way
        /// (a SoundDataChunk in Chunks, with a ChunkId), so ask for "chunk" to hear what an asset
        /// refers to rather than what this profile happens to have - which is how a run can tell
        /// "this asset has no audio chunks" apart from "your profile does not include them".
        /// </summary>
        public static List<Guid> CollectChunkGuidsFromMembers(object root, string memberToken)
        {
            if (string.IsNullOrEmpty(memberToken))
                return new List<Guid>();

            return WalkForGuids(root, (member, id) =>
                member != null && member.IndexOf(memberToken, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        /// <summary>
        /// The shared walk: every guid the graph names is offered to <paramref name="accept"/> along
        /// with the name of the member that held it.
        /// </summary>
        private static List<Guid> WalkForGuids(object root, Func<string, Guid, bool> accept)
        {
            List<Guid> results = new List<Guid>();
            if (root == null || accept == null)
                return results;

            HashSet<Guid> seen = new HashSet<Guid>();
            HashSet<object> visited = new HashSet<object>(ReferenceComparer.Instance);
            int budget = MaxGraphNodes;
            WalkGraph(root, "", accept, seen, results, visited, 0, ref budget);
            return results;
        }

        /// <summary>
        /// Walks one value of the graph: guids are tested, collections are walked member by member,
        /// and every other object contributes its members. Primitives and strings are skipped - a
        /// guid can only be stored as a guid - and objects already visited are not walked twice,
        /// which is what makes a self referencing graph terminate.
        /// </summary>
        private static void WalkGraph(object node, string memberName, Func<string, Guid, bool> accept,
            HashSet<Guid> seen, List<Guid> results, HashSet<object> visited, int depth, ref int budget)
        {
            if (node == null || node is string || budget <= 0)
                return;

            if (node is Guid guid)
            {
                budget--;
                if (guid != Guid.Empty && !seen.Contains(guid) && accept(memberName, guid))
                {
                    seen.Add(guid);
                    results.Add(guid);
                }

                return;
            }

            if (node is IEnumerable items)
            {
                foreach (object item in items)
                {
                    if (budget <= 0)
                        break;
                    WalkGraph(item, memberName, accept, seen, results, visited, depth, ref budget);
                }

                return;
            }

            Type type = node.GetType();
            if (type.IsPrimitive || type.IsEnum || node is decimal || node is DateTime)
                return;

            if (depth >= MaxGraphDepth || !visited.Add(node))
                return;

            budget--;

            MemberList members = Members(type);

            foreach (PropertyInfo property in members.Properties)
            {
                if (!property.CanRead || property.GetIndexParameters().Length != 0)
                    continue;

                object value;
                try
                {
                    value = property.GetValue(node);
                }
                catch
                {
                    // A member that will not read is not a chunk id; most ebx types simply do not
                    // have the fields we are looking for.
                    continue;
                }

                WalkGraph(value, property.Name, accept, seen, results, visited, depth + 1, ref budget);
            }

            foreach (FieldInfo field in members.Fields)
            {
                object value;
                try
                {
                    value = field.GetValue(node);
                }
                catch
                {
                    continue;
                }

                WalkGraph(value, field.Name, accept, seen, results, visited, depth + 1, ref budget);
            }
        }

        /// <summary>True when the asset manager knows <paramref name="id"/> as a loaded chunk.</summary>
        private static bool IsLoadedChunk(Guid id)
        {
            try
            {
                return App.AssetManager.GetChunkEntry(id) != null;
            }
            catch
            {
                // No game loaded yet: nothing is a chunk then.
                return false;
            }
        }

        private static List<Guid> ScanResForChunkIds(ResAssetEntry entry)
        {
            List<Guid> results = new List<Guid>();
            HashSet<Guid> seen = new HashSet<Guid>();

            // Some profiles keep the guid in the resource header.
            ScanForChunkIds(entry.ResMeta, seen, results);

            Stream resStream = null;
            try
            {
                resStream = App.AssetManager.GetRes(entry);
                if (resStream != null)
                {
                    byte[] header = ReadAtMost(resStream, MaxResourceScanBytes);
                    ScanForChunkIds(header, seen, results);
                }
            }
            catch (Exception ex)
            {
                // A resource we cannot read simply has no chunk ids we can report - the context
                // menu entry is disabled by the caller in that case.
                App.Logger?.Log("Copy Chunk Id: could not read resource '{0}': {1}", entry.Name, ex.Message);
            }
            finally
            {
                resStream?.Dispose();
            }

            return results;
        }

        /// <summary>
        /// Reads at most <paramref name="maxBytes"/> from the start of <paramref name="stream"/>
        /// without permanently consuming the caller's stream position.
        /// </summary>
        private static byte[] ReadAtMost(Stream stream, int maxBytes)
        {
            if (stream.CanSeek)
                stream.Position = 0;

            byte[] buffer = new byte[maxBytes];
            int total = 0;
            while (total < maxBytes)
            {
                int read = stream.Read(buffer, total, maxBytes - total);
                if (read <= 0)
                    break;
                total += read;
            }

            if (total == maxBytes)
                return buffer;

            byte[] trimmed = new byte[total];
            Array.Copy(buffer, trimmed, total);
            return trimmed;
        }

        /// <summary>
        /// Walks <paramref name="buffer"/> byte by byte and collects every 16 byte window that
        /// matches a chunk that is actually loaded. Chunk guids are looked up in the asset
        /// manager's O(1) chunk dictionary, so spurious matches inside binary data are
        /// essentially impossible.
        /// </summary>
        private static void ScanForChunkIds(byte[] buffer, HashSet<Guid> seen, List<Guid> results)
        {
            if (buffer == null || buffer.Length < 16)
                return;

            for (int i = 0; i + 16 <= buffer.Length; i++)
            {
                // Same byte order as NativeWriter.Write(Guid) / Guid.ToByteArray().
                Guid candidate = new Guid(
                    BitConverter.ToInt32(buffer, i),
                    BitConverter.ToInt16(buffer, i + 4),
                    BitConverter.ToInt16(buffer, i + 6),
                    buffer[i + 8], buffer[i + 9], buffer[i + 10], buffer[i + 11],
                    buffer[i + 12], buffer[i + 13], buffer[i + 14], buffer[i + 15]);

                if (candidate == Guid.Empty || seen.Contains(candidate))
                    continue;

                if (App.AssetManager.GetChunkEntry(candidate) != null)
                {
                    seen.Add(candidate);
                    results.Add(candidate);
                }
            }
        }
    }
}
