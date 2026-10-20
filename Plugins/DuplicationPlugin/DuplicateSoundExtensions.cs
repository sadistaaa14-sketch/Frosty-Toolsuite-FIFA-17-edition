using BundleRefTablePlugin;
using Frosty.Core;
using FrostySdk;
using FrostySdk.Ebx;
using FrostySdk.IO;
using FrostySdk.Managers;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;

namespace DuplicationPlugin
{
    // ─────────────────────────────────────────────────────────────────────────────────────────────────────────────
    //  Duplicating FIFA 17 sound assets.
    //
    //  A NewWaveAsset (LocalizedWaveAsset derives from it) is three things: the EBX, the chunks its Chunks rows name
    //  (the audio, by GUID), and the NewWaveResource .res named after the EBX (the bank: clip index, selection keys,
    //  where each clip sits in the chunks). The bank states its owner and its chunks itself:
    //
    //  * its res meta (16 bytes) is the GUID of the asset's root object, the GUID other assets reference it by
    //    (measured: equal in 42,239 of 42,239 voice-tree banks, FIFA17soundsystem notes/findings_res_identity.md);
    //  * SBle+0x0C and each dataset's TESD+0x0C hold the engine hash (h = 5381; h = h * 33 ^ byte) of that GUID's
    //    string - except the Selection dataset of a wave in a SentencePlayerWaveCollection, whose word is the hash
    //    of another asset's GUID (chants_FULL/TEAM's names Sound/chants/NewWaves/TEAM). Over the 4,344 banks staged
    //    on 2026-10-19 the own hash occurs at those places only, and the GUID's own bytes never occur in the data;
    //  * its tail (SBle+0x20) holds the 16-byte GUID of each Chunks row's chunk: Chunks.ChunkId is 1 + its offset in
    //    the tail, which is where FIFA17.exe reads it (FUN_147982a40), and the voice loads the chunk by that GUID
    //    (FUN_146211360, FUN_1461dcc70).
    //
    //  A copy that only renames the EBX shares its source's chunks (an audio edit to either changes both) and keeps
    //  a bank that names the source as its owner. The copy made here gets chunks of its own (new GUIDs, same bytes)
    //  and a bank that names it: the meta is the copy's root GUID, every word that hashed the source's GUID hashes
    //  the copy's, and the tail names the new chunks. No other byte of the bank changes, and no offset moves.
    //
    //  BundleRefTables list wave collections and other top-level sound assets, not waves: the 19 sound tables in
    //  the staged corpus list 114 assets, none under NewWaves/ or SoundWaves/ (Sound/chants/Chants_FULL_brt lists
    //  sound/chants/chants_full, a SentencePlayerWaveCollection). The copy is put in every bundle its source is in,
    //  and each BundleRefTable that lists the source, or an asset that references the source (its wave collection),
    //  gets an entry for the copy that resolves to the same bundle ref (BundleRefTablePlugin's DupeAsset, applied
    //  when the mod is launched). "Skip BRT Adding" in the BundleRefTable options turns this off.
    //
    //  The copy is not added to any collection. Its Selection word names what its source's names, and in the
    //  collections checked (chants_DEMO 13 of 13 waves, chants_FULL 40 of 40, the 141 staged of eng_us_MALE's 861 and
    //  4 of eng_us_LEGENDS_DLC's 9) no two waves' Selection words name the same asset.
    // ─────────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The byte-level part of copying a sound bank. It touches no editor state, so it can be tested alone.</summary>
    public static class SoundBankCopy
    {
        public sealed class Result
        {
            /// <summary>The copy's bank data (a copy of the input; the input is never changed).</summary>
            public byte[] Data;
            /// <summary>The copy's res meta (the input's, with the owner GUID replaced when it named the owner).</summary>
            public byte[] Meta;
            /// <summary>The owner GUID the bank stated (from the meta, or from the data's first 16 bytes).</summary>
            public Guid StatedOwner;
            /// <summary>True when the bank named <c>sourceOwner</c> and its SBle+0x0C was that GUID's hash.</summary>
            public bool OwnerRewritten;
            /// <summary>Identity words (SBle+0x0C, TESD+0x0C) rewritten to the copy's hash.</summary>
            public int OwnWords;
            /// <summary>Dataset words that hash another asset's GUID, kept as they are.</summary>
            public int ForeignWords;
            /// <summary>16-byte chunk GUIDs rewritten in the tail.</summary>
            public int TailGuids;
            /// <summary>Chunks the map renames that the tail does not name.</summary>
            public List<Guid> NotInTail = new List<Guid>();
            /// <summary>Why something was left as it was, or null.</summary>
            public string Note;
        }

        /// <summary>The engine's name hash: h = 5381; h = (h * 33) ^ byte, over the text as given.</summary>
        public static uint EngineHash(string text)
        {
            uint h = 5381;
            foreach (byte b in Encoding.ASCII.GetBytes(text))
                h = unchecked(h * 33) ^ b;
            return h;
        }

        private static uint U32(byte[] b, int at) { return (uint)(b[at] | (b[at + 1] << 8) | (b[at + 2] << 16) | (b[at + 3] << 24)); }
        private static ushort U16(byte[] b, int at) { return (ushort)(b[at] | (b[at + 1] << 8)); }

        private static void W32(byte[] b, int at, uint v)
        {
            b[at] = (byte)v; b[at + 1] = (byte)(v >> 8); b[at + 2] = (byte)(v >> 16); b[at + 3] = (byte)(v >> 24);
        }

        private static bool Magic(byte[] b, int at, string magic)
        {
            if (at < 0 || at + 4 > b.Length)
                return false;
            for (int i = 0; i < 4; i++)
            {
                if (b[at + i] != magic[i])
                    return false;
            }
            return true;
        }

        /// <summary>
        /// The copy of a bank whose owner is <paramref name="sourceOwner"/>, owned by <paramref name="newOwner"/>, and
        /// naming the chunks <paramref name="chunkMap"/> renames. <paramref name="data"/> is the res data as Frosty holds
        /// it (SBle at 0), or as Frosty exports it (16-byte meta, then SBle at 0x10); <paramref name="meta"/> is the res
        /// meta (may be null for the exported form). Neither input is changed.
        /// </summary>
        public static Result Copy(byte[] data, byte[] meta, Guid sourceOwner, Guid newOwner, IDictionary<Guid, Guid> chunkMap)
        {
            Result r = new Result
            {
                Data = (byte[])data.Clone(),
                Meta = meta == null ? null : (byte[])meta.Clone()
            };
            byte[] d = r.Data;

            int sble = Magic(d, 0x10, "SBle") ? 0x10 : (Magic(d, 0, "SBle") ? 0 : -1);
            if (sble < 0 || d.Length < sble + 0x28)
            {
                r.Note = "the res is not a sound bank (no SBle header), so it was copied byte for byte";
                return r;
            }

            // The owner GUID: the 16 bytes in front of SBle (exported form), else the res meta.
            byte[] head = null;
            int headAt = -1;
            if (sble == 0x10)
            {
                head = new byte[16];
                Buffer.BlockCopy(d, 0, head, 0, 16);
                headAt = 0;
            }
            else if (r.Meta != null && r.Meta.Length >= 16)
            {
                head = new byte[16];
                Buffer.BlockCopy(r.Meta, 0, head, 0, 16);
            }

            if (head != null)
            {
                r.StatedOwner = new Guid(head);
                uint oldHash = EngineHash(r.StatedOwner.ToString());
                if (r.StatedOwner != sourceOwner)
                {
                    r.Note = "the bank names " + r.StatedOwner + " as its owner, not the source's root object " + sourceOwner
                        + ", so its owner and identity words were kept";
                }
                else if (U32(d, sble + 0x0C) != oldHash)
                {
                    r.Note = "the bank's SBle+0x0C is not the hash of its owner's GUID, so its owner and identity words were kept";
                }
                else
                {
                    uint newHash = EngineHash(newOwner.ToString());
                    byte[] owner = newOwner.ToByteArray();
                    if (headAt >= 0)
                        Buffer.BlockCopy(owner, 0, d, headAt, 16);
                    else
                        Buffer.BlockCopy(owner, 0, r.Meta, 0, 16);
                    r.OwnerRewritten = true;

                    W32(d, sble + 0x0C, newHash);
                    r.OwnWords++;

                    int count = U16(d, sble + 0x0A);
                    int toc = sble + (int)U32(d, sble + 0x18);
                    for (int i = 0; i < count && toc + 8 * i + 4 <= d.Length; i++)
                    {
                        int at = sble + (int)U32(d, toc + 8 * i);
                        if (!Magic(d, at, "TESD") || at + 0x10 > d.Length)
                            continue;
                        if (U32(d, at + 0x0C) == oldHash)
                        {
                            W32(d, at + 0x0C, newHash);
                            r.OwnWords++;
                        }
                        else
                        {
                            r.ForeignWords++;
                        }
                    }
                }
            }
            else
            {
                r.Note = "the res has no meta to name its owner, so its identity words were kept";
            }

            // The tail: the Chunks rows' GUIDs (then, in banks with subtitles, their strings).
            uint tail = U32(d, sble + 0x20);
            bool hasTail = tail != 0 && tail != 0xFFFFFFFF && sble + (long)tail <= d.Length - 16;
            if (chunkMap != null)
            {
                foreach (KeyValuePair<Guid, Guid> pair in chunkMap)
                {
                    int found = 0;
                    if (hasTail)
                    {
                        byte[] from = pair.Key.ToByteArray();
                        byte[] to = pair.Value.ToByteArray();
                        for (int at = sble + (int)tail; at + 16 <= d.Length; at++)
                        {
                            int k = 0;
                            while (k < 16 && d[at + k] == from[k])
                                k++;
                            if (k == 16)
                            {
                                Buffer.BlockCopy(to, 0, d, at, 16);
                                found++;
                                at += 15;
                            }
                        }
                    }
                    if (found == 0)
                        r.NotInTail.Add(pair.Key);
                    r.TailGuids += found;
                }
            }

            return r;
        }
    }

    /// <summary>What the sound duplications share: the chunks, the BundleRefTable entries, the summary.</summary>
    internal static class SoundDuplication
    {
        /// <summary>
        /// Gives every Chunks row of <paramref name="root"/> (a SoundDataAsset copy) a chunk of its own: same bytes,
        /// same bundles, new GUID, linked to <paramref name="owner"/>. A row whose chunk the game data does not carry
        /// (a language that is not installed) keeps its GUID. Returns old GUID -> new GUID.
        /// </summary>
        public static Dictionary<Guid, Guid> CopyChunks(EbxAssetEntry owner, object rootObject, List<string> notes)
        {
            dynamic root = rootObject;
            Dictionary<Guid, Guid> map = new Dictionary<Guid, Guid>();
            int kept = 0;
            foreach (dynamic row in root.Chunks)
            {
                Guid old = row.ChunkId;
                Guid mapped;
                if (map.TryGetValue(old, out mapped))
                {
                    row.ChunkId = mapped;
                    continue;
                }

                ChunkAssetEntry chunk = App.AssetManager.GetChunkEntry(old);
                if (chunk == null)
                {
                    kept++;
                    continue;
                }

                ChunkAssetEntry copy = DuplicationTool.DuplicateChunk(chunk);
                if (copy == null)
                {
                    kept++;
                    continue;
                }

                map[old] = copy.Id;
                row.ChunkId = copy.Id;
                owner.LinkAsset(copy);
            }

            if (kept > 0)
                notes.Add(kept + " chunk(s) are not in the loaded game data (a language that is not installed?), so the copy shares them with its source");
            return map;
        }

        /// <summary>Every EBX that references <paramref name="source"/> (by its file GUID), collections first.</summary>
        public static List<EbxAssetEntry> Referrers(EbxAssetEntry source)
        {
            List<EbxAssetEntry> found = new List<EbxAssetEntry>();
            foreach (EbxAssetEntry e in App.AssetManager.EnumerateEbx())
            {
                if (e != source && e.ContainsDependency(source.Guid))
                    found.Add(e);
            }

            return found.OrderBy(e => IsCollection(e) ? 0 : 1).ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }

        public static bool IsCollection(EbxAssetEntry e)
        {
            return e.Type != null && TypeLibrary.IsSubClassOf(e.Type, "SentencePlayerWaveCollection");
        }

        // Each BundleRefTable's lookup hashes as the game data states them, read once per loaded game: entering a copy
        // never changes them (DupeAsset keeps its additions apart until the mod is launched), so a second duplication
        // reads only the tables that list what it looks for.
        private static readonly object tablesGate = new object();
        private static AssetManager tablesOwner;
        private static List<KeyValuePair<ResAssetEntry, HashSet<uint>>> tables;

        private static List<KeyValuePair<ResAssetEntry, HashSet<uint>>> BundleRefTables(List<string> notes)
        {
            lock (tablesGate)
            {
                if (tables != null && tablesOwner == App.AssetManager)
                    return tables;

                Stopwatch watch = Stopwatch.StartNew();
                List<KeyValuePair<ResAssetEntry, HashSet<uint>>> read = new List<KeyValuePair<ResAssetEntry, HashSet<uint>>>();
                int unreadable = 0;
                foreach (ResAssetEntry brtRes in App.AssetManager.EnumerateRes((uint)ResourceType.BundleRefTableResource).ToList())
                {
                    try
                    {
                        BundleRefTableResource brt = App.AssetManager.GetResAs<BundleRefTableResource>(brtRes);
                        if (brt != null)
                            read.Add(new KeyValuePair<ResAssetEntry, HashSet<uint>>(brtRes, new HashSet<uint>(brt.assetLookups.Select(l => l.Hash))));
                    }
                    catch (Exception ex)
                    {
                        unreadable++;
                        App.Logger.LogWarning("BundleRefTable {0} could not be read ({1}); it is left out.", brtRes.Name, ex.Message);
                    }
                }

                tables = read;
                tablesOwner = App.AssetManager;
                notes.Add(read.Count + " BundleRefTables read in " + watch.ElapsedMilliseconds + " ms"
                    + (unreadable > 0 ? " (" + unreadable + " could not be read)" : ""));
                return tables;
            }
        }

        /// <summary>
        /// Adds <paramref name="copy"/> to every BundleRefTable that lists <paramref name="source"/>, or else one of the
        /// assets in <paramref name="referrers"/>, resolving to the bundle ref of the asset it found. One entry per
        /// table; the source itself is preferred, then the referrers in order. Honours "Skip BRT Adding".
        /// </summary>
        public static void AddToBundleRefTables(EbxAssetEntry source, EbxAssetEntry copy, List<EbxAssetEntry> referrers, List<string> notes)
        {
            if (Config.Get<bool>("SkipBrtAdd", false))
            {
                notes.Add("BundleRefTables were not changed (\"Skip BRT Adding\" is on in the BundleRefTable options)");
                return;
            }

            List<string> candidates = new List<string> { source.Name.ToLower() };
            foreach (EbxAssetEntry r in referrers)
            {
                string n = r.Name.ToLower();
                if (!candidates.Contains(n))
                    candidates.Add(n);
            }
            HashSet<uint> wanted = new HashSet<uint>(candidates.Select(BRTUtils.Fnv1Hash32));

            string newPath = copy.Name.ToLower();
            string leaf = newPath.Substring(newPath.LastIndexOf('/') + 1);
            List<KeyValuePair<ResAssetEntry, HashSet<uint>>> all = BundleRefTables(notes);
            int listed = 0;
            foreach (KeyValuePair<ResAssetEntry, HashSet<uint>> table in all)
            {
                if (!table.Value.Overlaps(wanted))
                    continue;

                ResAssetEntry brtRes = table.Key;
                BundleRefTableResource brt = App.AssetManager.GetResAs<BundleRefTableResource>(brtRes);
                if (brt == null)
                    continue;

                foreach (string existing in candidates)
                {
                    if (!brt.ContainsAsset(existing))
                        continue;
                    listed++;

                    // A table resolves each asset by its full path and by its name alone, and entering a copy first
                    // removes whatever already resolves the copy's name: a name another asset (or another copy) has
                    // would lose that asset its lookup.
                    if (brt.ContainsAsset(leaf) || (brt.modResource != null && brt.modResource.DuplicationDict.Keys.Any(
                        k => k != newPath && string.Equals(k.Substring(k.LastIndexOf('/') + 1), leaf, StringComparison.OrdinalIgnoreCase))))
                    {
                        notes.Add("BundleRefTable " + brtRes.Name + " already resolves the name " + leaf
                            + " to another asset, so the copy was not entered there (a copy with another name would be)");
                        break;
                    }

                    if (brt.DupeAsset(newPath, existing))
                    {
                        App.AssetManager.ModifyRes(brtRes.ResRid, brt);
                        notes.Add("BundleRefTable " + brtRes.Name + ": " + newPath + " resolves like " + existing);
                    }
                    else
                    {
                        notes.Add("BundleRefTable " + brtRes.Name + " already has an entry for " + newPath);
                    }
                    break;
                }
            }

            if (listed == 0)
                notes.Add("no BundleRefTable lists the source or an asset that references it (" + all.Count
                    + " tables), so none was changed; the copy is in its source's bundles");
        }

        /// <summary>
        /// The rest of copying a sound asset whose audio is only its Chunks (no bank .res): its chunks become the copy's
        /// own, and the BundleRefTables get the copy.
        /// </summary>
        public static void FinishChunkSound(EbxAssetEntry source, EbxAssetEntry copy, string note)
        {
            List<string> notes = new List<string>();
            EbxAsset copyAsset = App.AssetManager.GetEbx(copy);
            Dictionary<Guid, Guid> chunkMap = CopyChunks(copy, copyAsset.RootObject, notes);
            notes.Add(chunkMap.Count + " chunk(s) copied under new GUIDs");
            if (note != null)
                notes.Add(note);
            App.AssetManager.ModifyEbx(copy.Name, copyAsset);

            AddToBundleRefTables(source, copy, Referrers(source), notes);
            Report(source.Type, source, copy, notes);
        }

        public static void Report(string what, EbxAssetEntry source, EbxAssetEntry copy, List<string> notes)
        {
            App.Logger.Log("Duplicated {0} {1} -> {2}", what, source.Name, copy.Name);
            foreach (string n in notes)
                App.Logger.Log("  {0}", n);
        }
    }

    /// <summary>NewWaveAsset and LocalizedWaveAsset: the EBX, its chunks and its bank, each the copy's own.</summary>
    public class NewWaveDuplicateExtension : DuplicationTool.DuplicateAssetExtension
    {
        public override string AssetType => "NewWaveAsset";

        public override EbxAssetEntry DuplicateAsset(EbxAssetEntry entry, string newName, bool createNew, Type newType)
        {
            if (createNew)
                return base.DuplicateAsset(entry, newName, createNew, newType);

            List<string> notes = new List<string>();
            EbxAsset sourceAsset = App.AssetManager.GetEbx(entry);
            Guid sourceRoot = sourceAsset.RootInstanceGuid;

            ResAssetEntry resEntry = App.AssetManager.GetResEntry(entry.Name);
            if (resEntry != null && App.AssetManager.GetResEntry(newName) != null)
                throw new InvalidOperationException("a res named " + newName.ToLower() + " already exists, so the bank cannot be copied there");

            EbxAssetEntry newEntry = base.DuplicateAsset(entry, newName, createNew, newType);
            EbxAsset newAsset = App.AssetManager.GetEbx(newEntry);
            Guid newRoot = newAsset.RootInstanceGuid;

            Dictionary<Guid, Guid> chunkMap = SoundDuplication.CopyChunks(newEntry, newAsset.RootObject, notes);
            notes.Add(chunkMap.Count + " chunk(s) copied under new GUIDs");

            if (resEntry == null)
            {
                notes.Add("no .res is named after the source, so the copy has no bank (as its source)");
            }
            else
            {
                byte[] data;
                using (NativeReader reader = new NativeReader(App.AssetManager.GetRes(resEntry)))
                    data = reader.ReadToEnd();

                SoundBankCopy.Result bank = SoundBankCopy.Copy(data, resEntry.ResMeta, sourceRoot, newRoot, chunkMap);
                ResAssetEntry newRes = App.AssetManager.AddRes(newName, (ResourceType)resEntry.ResType, bank.Meta, bank.Data,
                    resEntry.EnumerateBundles().ToArray());
                newEntry.LinkAsset(newRes);

                if (bank.OwnerRewritten)
                    notes.Add("bank " + newRes.Name + ": owner " + newRoot + " (was " + sourceRoot + "), " + bank.OwnWords
                        + " identity word(s) rehashed, " + bank.ForeignWords + " word(s) naming another asset kept, "
                        + bank.TailGuids + " chunk GUID(s) renamed in its tail");
                else
                    notes.Add("bank " + newRes.Name + ": " + bank.TailGuids + " chunk GUID(s) renamed in its tail");
                if (bank.Note != null)
                    notes.Add("bank: " + bank.Note);
                if (bank.NotInTail.Count > 0)
                    notes.Add("bank: its tail does not name " + bank.NotInTail.Count + " of the copied chunks ("
                        + string.Join(", ", bank.NotInTail) + ")");
            }

            App.AssetManager.ModifyEbx(newEntry.Name, newAsset);

            List<EbxAssetEntry> referrers = SoundDuplication.Referrers(entry);
            List<EbxAssetEntry> collections = referrers.Where(SoundDuplication.IsCollection).ToList();
            if (collections.Count > 0)
                notes.Add("the source is in " + string.Join(", ", collections.Select(c => c.Name))
                    + "; the copy was not added to a collection");
            SoundDuplication.AddToBundleRefTables(entry, newEntry, referrers, notes);

            SoundDuplication.Report(entry.Type, entry, newEntry, notes);
            return newEntry;
        }
    }

    /// <summary>SoundWaveAsset (the older wave type): the EBX and its chunks, each the copy's own.</summary>
    public class SoundWaveDuplicateExtension : DuplicationTool.DuplicateAssetExtension
    {
        public override string AssetType => "SoundWaveAsset";

        public override EbxAssetEntry DuplicateAsset(EbxAssetEntry entry, string newName, bool createNew, Type newType)
        {
            EbxAssetEntry newEntry = base.DuplicateAsset(entry, newName, createNew, newType);
            if (!createNew)
                SoundDuplication.FinishChunkSound(entry, newEntry, null);
            return newEntry;
        }
    }

    /// <summary>
    /// HarmonySampleBankAsset (EA Trax, intro and highlights music): the EBX and its RAM, debug and stream chunks, each
    /// the copy's own. The RAM and debug chunks are banks themselves, but they name no chunk GUID (their tail holds
    /// strings) and their SBle+0x0C is not the hash of the asset's GUID (EATrax_01's two state 0x4B152E13, its GUID
    /// hashes to 0xE3EF7185), so their bytes are copied unchanged.
    /// </summary>
    public class HarmonySampleBankDuplicateExtension : DuplicationTool.DuplicateAssetExtension
    {
        public override string AssetType => "HarmonySampleBankAsset";

        public override EbxAssetEntry DuplicateAsset(EbxAssetEntry entry, string newName, bool createNew, Type newType)
        {
            EbxAssetEntry newEntry = base.DuplicateAsset(entry, newName, createNew, newType);
            if (!createNew)
                SoundDuplication.FinishChunkSound(entry, newEntry, "the RAM and debug chunks are copied byte for byte (they name no chunk GUID)");
            return newEntry;
        }
    }

    /// <summary>
    /// SentencePlayerWaveCollection: the EBX (its NewWaves still name the same waves). Every BundleRefTable that lists
    /// the source collection gets an entry for the copy resolving to the source's bundle ref - the bundle the copy is in.
    /// </summary>
    public class SentencePlayerWaveCollectionDuplicateExtension : DuplicationTool.DuplicateAssetExtension
    {
        public override string AssetType => "SentencePlayerWaveCollection";

        public override EbxAssetEntry DuplicateAsset(EbxAssetEntry entry, string newName, bool createNew, Type newType)
        {
            if (createNew)
                return base.DuplicateAsset(entry, newName, createNew, newType);

            List<string> notes = new List<string>();
            EbxAssetEntry newEntry = base.DuplicateAsset(entry, newName, createNew, newType);
            dynamic root = App.AssetManager.GetEbx(newEntry).RootObject;
            int waves = root.NewWaves.Count;
            notes.Add("the copy lists the same " + waves + " wave(s) as its source");

            SoundDuplication.AddToBundleRefTables(entry, newEntry, new List<EbxAssetEntry>(), notes);
            SoundDuplication.Report(entry.Type, entry, newEntry, notes);
            return newEntry;
        }
    }
}
