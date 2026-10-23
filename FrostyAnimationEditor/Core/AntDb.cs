using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace FrostyAnimationEditor.Core
{
    /// <summary>runtime data of one block: key references as the loader resolves them, linear allocations</summary>
    public sealed class BlockRuntime
    {
        public List<ResolvedRef> Refs;
        public List<Alloc> Allocs;     // empty for classes without a cost model
        public bool Modelled;
    }

    public sealed class ClassCost
    {
        public long Objects, LinearBytes, Requests, Resolves;
    }

    public sealed class RuntimeProfile
    {
        public long WaiterPeak;
        public long LinearBytes;
        public long PostLoadBytes;
        public List<string> Unmodelled = new List<string>();
        public Dictionary<string, ClassCost> Classes;
    }

    public sealed class HeaderPlan
    {
        public uint H7, H8;
        public long WaiterPeakOriginal = -1, WaiterPeakNew = -1, LinearOriginal = -1, LinearNew = -1, PostLoadNew = -1;
        public List<string> Unmodelled = new List<string>();
        public bool Measured;
        public RuntimeProfile NewProfile;     // the loader profile of the output order (when measured)

        public override string ToString()
        {
            if (!Measured) return "unchanged";
            return "waiter peak " + WaiterPeakOriginal + " -> " + WaiterPeakNew + ", linear bytes " + LinearOriginal + " -> " + LinearNew +
                   ", post-load " + PostLoadNew + (Unmodelled.Count > 0 ? ", classes without cost model: " + string.Join(", ", Unmodelled) : "");
        }
    }

    public sealed class SaveResult
    {
        public byte[] Image;          // the written container (with or without the 16-byte prefix)
        public bool WithoutPrefix;
        public int Blocks;
        public int Modified;
        public uint H7, H8, StrmAux;
        public HeaderPlan Plan;
        public List<int> Ids;                 // block ids in output order
        public List<string> Log = new List<string>();
        public List<string> Warnings = new List<string>();
    }

    /// <summary>
    /// The editable database view of a fifagame_win32_antstate.res container.
    ///   Obj(i) / Get(key)  decoded root objects (cached; edits are kept and written on save)
    ///   Add / Remove / Clone / NewObject
    ///   Save               re-encodes decoded objects, copies the rest verbatim, recomputes T/C/A/B,
    ///                      H1..H8 and GD.STRMb.  Unmodified files round-trip byte for byte.
    /// </summary>
    public sealed class AntDb
    {
        public const uint AnimationAssetClass = 0xB3A0A282;   // engine id of AnimationAsset (FUN_145ca2c50)

        public readonly Container C;
        public readonly TypeTable Types;
        public Header Header;
        public uint StrmAux;
        public List<ulong> Exports;            // table C
        public readonly List<ARow> OrigA;
        public readonly List<BRow> OrigB;
        public readonly Dictionary<int, GdObj> Cache = new Dictionary<int, GdObj>();
        public readonly List<int> Order;
        public readonly int N0;
        public readonly Dictionary<int, GdObj> NewObjs = new Dictionary<int, GdObj>();
        public readonly HashSet<int> Removed = new HashSet<int>();
        public readonly List<uint> Tid = new List<uint>();
        public readonly List<ulong> Keys = new List<ulong>();
        public readonly ulong[] OrigKeys;
        public readonly Dictionary<ulong, int> Key2Blk = new Dictionary<ulong, int>();
        private readonly Dictionary<ulong, int> origKey2Blk = new Dictionary<ulong, int>();
        public readonly Dictionary<string, List<int>> ByType = new Dictionary<string, List<int>>();
        private readonly Dictionary<string, List<string>> chains = new Dictionary<string, List<string>>();
        private HashSet<int> modified;
        private Random rng = new Random();

        /// <summary>runtime data of the original blocks (filled from the cache file or computed on demand)</summary>
        public RuntimeStore Store;
        private RuntimeProfile baseProfile;

        public AntDb(Container c)
        {
            C = c;
            Types = c.Types;
            Header = c.Header.Copy();
            StrmAux = c.StrmAux;
            Exports = new List<ulong>(c.C);
            OrigA = new List<ARow>(c.A);
            OrigB = new List<BRow>(c.B);
            N0 = c.BlockCount;
            Order = new List<int>(N0);
            for (int i = 0; i < N0; i++) Order.Add(i);
            OrigKeys = new ulong[N0];
            Index();
        }

        public static AntDb Open(byte[] file) { return new AntDb(new Container(file)); }

        public void SetSeed(int seed) { rng = new Random(seed); }

        private void Index()
        {
            byte[] m = C.File;
            Dictionary<uint, long> keyoff = new Dictionary<uint, long>();
            for (int i = 0; i < N0; i++)
            {
                long p = C.BlockOff[i] + 16;
                uint t = BE.U32(m, p + 0x14);
                long ko;
                if (!keyoff.TryGetValue(t, out ko))
                {
                    TypeDesc ty = Types[t];
                    Field f = ty.GetField("__key");
                    ko = f != null ? (long)f.Offset : -1L;
                    keyoff[t] = ko;
                }
                long doff = BE.U16(m, p + 0x1C);
                ulong k = ko >= 0 ? BE.U64(m, p + doff + ko) : 0;
                Tid.Add(t);
                Keys.Add(k);
                OrigKeys[i] = k;
                Key2Blk[k] = i;
                origKey2Blk[k] = i;
                string tn = Types[t].Name;
                List<int> l;
                if (!ByType.TryGetValue(tn, out l)) { l = new List<int>(); ByType[tn] = l; }
                l.Add(i);
            }
        }

        // ------------------------------------------------------------------ access
        public int Count { get { return Order.Count - Removed.Count; } }

        public string TypeName(int i)
        {
            if (i >= N0) return NewObjs[i].Type.Name;
            GdObj o;
            if (Cache.TryGetValue(i, out o)) return o.Type.Name;
            return Types[Tid[i]].Name;
        }

        /// <summary>decoded root object of block id i (cached; edits are kept and written on save)</summary>
        public GdObj Obj(int i)
        {
            if (i >= N0) return NewObjs[i];
            GdObj o;
            if (!Cache.TryGetValue(i, out o))
            {
                o = ObjCodec.DecodeBlock(C.File, C.PayloadOff(i), Types);
                Cache[i] = o;
            }
            return o;
        }

        /// <summary>a fresh decode of the original bytes of block i (not cached)</summary>
        public GdObj Original(int i)
        {
            return ObjCodec.DecodeBlock(C.File, C.PayloadOff(i), Types);
        }

        /// <summary>current object without caching it (for scans)</summary>
        public GdObj Peek(int i)
        {
            if (i >= N0) return NewObjs[i];
            GdObj o;
            if (Cache.TryGetValue(i, out o)) return o;
            return Original(i);
        }

        public bool IsLoaded(int i) { return i >= N0 || Cache.ContainsKey(i); }

        public GdObj Get(ulong key)
        {
            int i;
            if (!Key2Blk.TryGetValue(key, out i) || Removed.Contains(i)) return null;
            return Obj(i);
        }

        public GdObj PeekKey(ulong key)
        {
            int i;
            if (!Key2Blk.TryGetValue(key, out i) || Removed.Contains(i)) return null;
            return Peek(i);
        }

        public bool Contains(ulong key)
        {
            int i;
            return Key2Blk.TryGetValue(key, out i) && !Removed.Contains(i);
        }

        public int BlockOf(ulong key)
        {
            int i;
            return Key2Blk.TryGetValue(key, out i) ? i : -1;
        }

        public IEnumerable<GdObj> Objects(string typeName = null)
        {
            foreach (int i in BlockIds(typeName)) yield return Obj(i);
        }

        public List<int> BlockIds(string typeName = null)
        {
            List<int> ids;
            if (typeName == null) ids = Order;
            else if (!ByType.TryGetValue(typeName, out ids)) return new List<int>();
            List<int> r = new List<int>(ids.Count);
            foreach (int i in ids) if (!Removed.Contains(i)) r.Add(i);
            return r;
        }

        /// <summary>__name of block i without a full decode</summary>
        public string NameOf(int i)
        {
            if (IsLoaded(i)) return Obj(i).Name;
            byte[] m = C.File;
            long p = C.BlockOff[i] + 16;
            TypeDesc t = Types[Tid[i]];
            Field f = t.GetField("__name");
            if (f == null) return "";
            long doff = BE.U16(m, p + 0x1C);
            long s = p + doff + f.Offset;
            uint cnt = BE.U32(m, s);
            ulong ptr = BE.U64(m, s + 8);
            if (cnt == 0) return "";
            return BE.Latin1.GetString(m, (int)(p + (long)ptr), (int)cnt - 1);
        }

        public string NameOfKey(ulong key)
        {
            int i = BlockOf(key);
            return i >= 0 && !Removed.Contains(i) ? NameOf(i) : null;
        }

        /// <summary>value of a top-level Key/integer field of block i, read from the raw bytes when the
        /// object was never decoded (fast scans over a whole class)</summary>
        public object ScalarField(int i, string field)
        {
            if (IsLoaded(i)) return Obj(i)[field];
            TypeDesc t = Types[Tid[i]];
            Field f = t.GetField(field);
            if (f == null || f.IsList) return Peek(i)[field];
            byte[] m = C.File;
            long p = C.BlockOff[i] + 16;
            long doff = BE.U16(m, p + 0x1C);
            long a = p + doff + f.Offset;
            if (f.FType == TypeIds.Key) return BE.U64(m, a);
            switch (f.Size)
            {
                case 8: return (long)BE.U64(m, a);
                case 4: return (long)BE.U32(m, a);
                case 2: return (long)BE.U16(m, a);
                case 1: return (long)m[a];
            }
            return Peek(i)[field];
        }

        /// <summary>[type, base, base-of-base, ...] of a root class (read from one instance)</summary>
        public List<string> ClassChain(string typeName)
        {
            List<string> r;
            if (chains.TryGetValue(typeName, out r)) return r;
            List<int> ids = BlockIds(typeName);
            r = new List<string>();
            if (ids.Count > 0) foreach (GdObj x in Peek(ids[0]).Chain()) r.Add(x.Type.Name);
            else r.Add(typeName);
            chains[typeName] = r;
            return r;
        }

        public List<string> ClassesDeriving(string baseName)
        {
            List<string> r = new List<string>();
            foreach (string t in ByType.Keys) if (ClassChain(t).Contains(baseName)) r.Add(t);
            return r;
        }

        public List<int> FindByName(string text, string typeName = null, bool exact = false)
        {
            List<int> r = new List<int>();
            string low = text.ToLowerInvariant();
            foreach (int i in BlockIds(typeName))
            {
                string n = NameOf(i);
                if (exact ? n == text : n.ToLowerInvariant().Contains(low)) r.Add(i);
            }
            return r;
        }

        // ------------------------------------------------------------------ editing
        /// <summary>optional source of new keys (tests, scripted edits); random when null</summary>
        public Func<ulong> KeyGenerator;

        /// <summary>a fresh random 64-bit key, unique in T, A and C</summary>
        public ulong NewKey()
        {
            if (KeyGenerator != null) return KeyGenerator();
            HashSet<ulong> usedA = new HashSet<ulong>();
            foreach (ARow r in OrigA) usedA.Add(r.Key);
            byte[] b = new byte[8];
            while (true)
            {
                rng.NextBytes(b);
                ulong k = BitConverter.ToUInt64(b, 0);
                if (k != 0 && !Key2Blk.ContainsKey(k) && !usedA.Contains(k) && !Exports.Contains(k)) return k;
            }
        }

        /// <summary>append a new root object (or insert it after the block of afterKey)</summary>
        public GdObj Add(GdObj o, ulong? afterKey = null)
        {
            if (!Types.Contains(o.Type.Tid)) throw new GdException("type " + o.Type.Name + " is not in the file's type table");
            ulong k = o.Has("__key") ? o.Key : 0;
            int cur;
            if (k == 0 || (Key2Blk.TryGetValue(k, out cur) && !Removed.Contains(cur)))
            {
                k = NewKey();
                if (o.Has("__key")) o.Key = k;
            }
            int i = N0 + NewObjs.Count;
            while (NewObjs.ContainsKey(i)) i++;
            NewObjs[i] = o;
            Key2Blk[k] = i;
            while (Tid.Count <= i) { Tid.Add(0); Keys.Add(0); }
            Tid[i] = o.Type.Tid;
            Keys[i] = k;
            List<int> l;
            if (!ByType.TryGetValue(o.Type.Name, out l)) { l = new List<int>(); ByType[o.Type.Name] = l; }
            l.Add(i);
            int ab;
            if (afterKey.HasValue && Key2Blk.TryGetValue(afterKey.Value, out ab))
            {
                int pos = Order.IndexOf(ab) + 1;
                Order.Insert(pos, i);
            }
            else Order.Add(i);
            return o;
        }

        public void Remove(ulong key)
        {
            int i;
            if (!Key2Blk.TryGetValue(key, out i)) throw new KeyNotFoundException(BE.KeyHex(key));
            Removed.Add(i);
            Key2Blk.Remove(key);
            Exports.Remove(key);
        }

        /// <summary>deep copy of a root object (nested objects copied too); gets a fresh __key</summary>
        public GdObj Clone(GdObj o, bool newKey = true, string name = null, bool add = true, bool after = true)
        {
            GdObj c = DeepCopy.Obj(o);
            if (newKey && c.Has("__key")) c.Key = 0;
            if (name != null) c["__name"] = name;
            if (add) Add(c, after ? (ulong?)o.Key : null);
            return c;
        }

        /// <summary>a blank object of a class (all fields zero/empty)</summary>
        public GdObj NewObject(string typeName)
        {
            TypeDesc t = Types.Get(typeName);
            if (t == null) throw new GdException("no type " + typeName + " in the file");
            return Blank.Obj(Types, t);
        }

        /// <summary>update Keys/Key2Blk after a decoded object's __key was edited</summary>
        public void SyncKeys()
        {
            List<int> ids = new List<int>(Cache.Keys);
            ids.AddRange(NewObjs.Keys);
            foreach (int i in ids)
            {
                if (Removed.Contains(i)) continue;
                GdObj o = Obj(i);
                if (!o.Has("__key")) continue;
                ulong k = o.Key;
                if (k == Keys[i]) continue;
                int cur;
                if (Key2Blk.TryGetValue(Keys[i], out cur) && cur == i) Key2Blk.Remove(Keys[i]);
                Keys[i] = k;
                Key2Blk[k] = i;
            }
        }

        // ------------------------------------------------------------------ references
        public List<KeyRef> RefsFrom(GdStruct o) { return Refs.From(o); }

        private Dictionary<ulong, List<int>> rev;

        /// <summary>blocks that reference a key (built over the whole file once; edits update it on demand)</summary>
        public List<int> RefsTo(ulong key)
        {
            if (rev == null) BuildRefIndex(null);
            List<int> r = new List<int>();
            List<int> l;
            if (rev.TryGetValue(key, out l))
                foreach (int i in l)
                {
                    if (Removed.Contains(i)) continue;
                    if (IsLoaded(i))
                    {
                        // re-check the current content of loaded blocks
                        List<ulong> ks = new List<ulong>();
                        Refs.Keys(Obj(i), ks);
                        if (!ks.Contains(key)) continue;
                    }
                    if (!r.Contains(i)) r.Add(i);
                }
            // loaded/new blocks may reference it now
            foreach (int i in Cache.Keys.Concat(NewObjs.Keys))
            {
                if (Removed.Contains(i) || r.Contains(i)) continue;
                List<ulong> ks = new List<ulong>();
                Refs.Keys(Obj(i), ks);
                if (ks.Contains(key)) r.Add(i);
            }
            r.Sort();
            return r;
        }

        /// <summary>the reverse index key -> referencing blocks of the original file (from the store when present)</summary>
        public void BuildRefIndex(Action<int, int> progress)
        {
            Dictionary<ulong, List<int>> d = new Dictionary<ulong, List<int>>();
            if (Store != null && Store.HasKeyRefs)
            {
                for (int i = 0; i < N0; i++)
                {
                    foreach (ulong k in Store.KeyRefs(i)) AddRev(d, k, i);
                    if (progress != null && i % 20000 == 0) progress(i, N0);
                }
            }
            else
            {
                List<ulong> ks = new List<ulong>();
                for (int i = 0; i < N0; i++)
                {
                    ks.Clear();
                    Refs.Keys(Original(i), ks);
                    foreach (ulong k in ks) AddRev(d, k, i);
                    if (progress != null && i % 20000 == 0) progress(i, N0);
                }
            }
            rev = d;
        }

        private static void AddRev(Dictionary<ulong, List<int>> d, ulong k, int i)
        {
            if (k == 0) return;
            List<int> l;
            if (!d.TryGetValue(k, out l)) { l = new List<int>(2); d[k] = l; }
            if (l.Count == 0 || l[l.Count - 1] != i) l.Add(i);
        }

        // ------------------------------------------------------------------ imports (tables A/B)
        /// <summary>A/B: every ClipControllerAsset.Anim key that is not a root key of this file</summary>
        public void ComputeImports(List<int> ids, out List<ARow> A, out List<BRow> B)
        {
            HashSet<ulong> present = new HashSet<ulong>();
            foreach (int i in ids) present.Add(Keys[i]);
            Dictionary<ulong, List<uint>> need = new Dictionary<ulong, List<uint>>();
            List<ulong> order = new List<ulong>();
            foreach (int i in ids)
            {
                if (TypeName(i) != "ClipControllerAsset") continue;
                ulong k;
                if (!IsLoaded(i))
                {
                    TypeDesc t = Types[Tid[i]];
                    Field f = t.GetField("Anim");
                    if (f == null) continue;
                    long p = C.BlockOff[i] + 16;
                    long doff = BE.U16(C.File, p + 0x1C);
                    k = BE.U64(C.File, p + doff + f.Offset);
                }
                else k = Num.ToKey(Obj(i)["Anim"]);
                if (k != 0 && !present.Contains(k))
                {
                    List<uint> l;
                    if (!need.TryGetValue(k, out l)) { l = new List<uint>(); need[k] = l; order.Add(k); }
                    l.Add(AnimationAssetClass);
                }
            }
            order.Sort(BE.CompareSigned);
            A = new List<ARow>();
            B = new List<BRow>();
            foreach (ulong k in order)
            {
                A.Add(new ARow(k, 0xFFFF, 0xFFFF, (ushort)B.Count, (ushort)need[k].Count));
                foreach (uint cls in need[k]) B.Add(new BRow(0xFFFFFFFFFFFFFFFFUL, cls, 0));
            }
        }

        // ------------------------------------------------------------------ runtime sizing (H7 / H8)
        /// <summary>runtime data of the ORIGINAL block i (from the store, else decoded once)</summary>
        public BlockRuntime OrigRuntime(int i)
        {
            if (Store == null) Store = new RuntimeStore(N0);
            BlockRuntime r = Store.Get(i);
            if (r != null) return r;
            r = ComputeRuntime(Original(i));
            Store.Set(i, r);
            return r;
        }

        public static BlockRuntime ComputeRuntime(GdObj o)
        {
            BlockRuntime r = new BlockRuntime();
            r.Refs = Runtime.ResolvedRefs(o, Refs.From(o));
            List<Alloc> a = AllocModel.Allocations(o);
            r.Modelled = a != null;
            r.Allocs = a ?? new List<Alloc>();
            return r;
        }

        /// <summary>runtime data of block i as it is now (edited, new or original)</summary>
        public BlockRuntime RuntimeOf(int i) { return CurrentRuntime(i); }

        private BlockRuntime CurrentRuntime(int i)
        {
            if (!IsLoaded(i)) return OrigRuntime(i);
            return ComputeRuntime(Obj(i));
        }

        /// <summary>the file as it would be loaded (ids = output order; original = the file as opened)</summary>
        public RuntimeProfile Profile(List<int> ids = null, bool original = false, bool byClass = false, Action<int, int> progress = null)
        {
            Func<int, BlockRuntime> get;
            Func<int, ulong> key;
            if (original)
            {
                ids = new List<int>(N0);
                for (int i = 0; i < N0; i++) ids.Add(i);
                get = OrigRuntime;
                key = i => OrigKeys[i];
            }
            else
            {
                if (ids == null) ids = LiveIds();
                get = CurrentRuntime;
                key = i => Keys[i];
            }
            BlockRuntime[] data = new BlockRuntime[ids.Count];
            HashSet<string> unmod = new HashSet<string>();
            for (int n = 0; n < ids.Count; n++)
            {
                data[n] = get(ids[n]);
                if (!data[n].Modelled) unmod.Add(original ? Types[Tid[ids[n]]].Name : TypeName(ids[n]));
                if (progress != null && n % 50000 == 0) progress(n, ids.Count);
            }
            List<ulong> keys = new List<ulong>(ids.Count);
            foreach (int i in ids) keys.Add(key(i));
            RuntimeProfile p = new RuntimeProfile();
            p.WaiterPeak = Runtime.WaiterPeak(keys, n => data[n].Refs);
            p.LinearBytes = Runtime.Run(data.Select(d => d.Allocs));
            long post = Runtime.Run(PostSeqs(ids, original), p.LinearBytes);
            p.PostLoadBytes = post - p.LinearBytes;
            p.Unmodelled = unmod.OrderBy(x => x, StringComparer.Ordinal).ToList();
            if (byClass)
            {
                p.Classes = new Dictionary<string, ClassCost>();
                long pos = 0;
                for (int n = 0; n < ids.Count; n++)
                {
                    long q = Runtime.Run(data[n].Allocs, pos);
                    string tn = original ? Types[Tid[ids[n]]].Name : TypeName(ids[n]);
                    ClassCost c;
                    if (!p.Classes.TryGetValue(tn, out c)) { c = new ClassCost(); p.Classes[tn] = c; }
                    c.Objects++;
                    c.LinearBytes += q - pos;
                    c.Requests += data[n].Allocs.Count;
                    foreach (ResolvedRef r in data[n].Refs) c.Resolves += r.Times;
                    pos = q;
                }
            }
            return p;
        }

        public List<int> LiveIds()
        {
            List<int> r = new List<int>(Order.Count);
            foreach (int i in Order) if (!Removed.Contains(i)) r.Add(i);
            return r;
        }

        /// <summary>the post-load step's request lists in table-T order</summary>
        private List<List<Alloc>> PostSeqs(List<int> ids, bool original)
        {
            List<int> sel = new List<int>();
            foreach (int i in ids)
            {
                string tn = original ? Types[Tid[i]].Name : TypeName(i);
                if (AllocModel.HasPost(tn)) sel.Add(i);
            }
            if (sel.Count == 0) return new List<List<Alloc>>();
            Func<ulong, GdObj> res;
            Func<int, GdObj> get;
            if (original)
            {
                res = k => { int j; return origKey2Blk.TryGetValue(k, out j) ? Original(j) : null; };
                get = Original;
                sel.Sort((a, b) => BE.CompareSigned(OrigKeys[a], OrigKeys[b]));
            }
            else
            {
                res = k => Get(k);
                get = Obj;
                sel.Sort((a, b) => BE.CompareSigned(Keys[a], Keys[b]));
            }
            List<List<Alloc>> r = new List<List<Alloc>>();
            foreach (int i in sel) r.Add(AllocModel.PostAllocations(get(i), res));
            return r;
        }

        public RuntimeProfile BaseProfile(Action<int, int> progress = null)
        {
            if (baseProfile == null)
            {
                if (Store != null && Store.Base != null) baseProfile = Store.Base;
                else
                {
                    baseProfile = Profile(null, true, false, progress);
                    if (Store != null) Store.Base = baseProfile;
                }
            }
            return baseProfile;
        }

        /// <summary>block ids whose content differs from the original file (exact after a save)</summary>
        public HashSet<int> ChangedBlocks()
        {
            if (modified != null) return new HashSet<int>(modified);
            HashSet<int> r = new HashSet<int>(NewObjs.Keys);
            foreach (int i in Cache.Keys) r.Add(i);
            return r;
        }

        /// <summary>(H7, H8) for output order ids: the original values plus the exact change in the loader's
        /// waiter peak and linear-allocator use (never lowered)</summary>
        public HeaderPlan PlanHeader(List<int> ids, Action<string> log = null)
        {
            HeaderPlan hp = new HeaderPlan();
            uint h7 = Header.H[6], h8 = Header.H[7];
            hp.H7 = h7;
            hp.H8 = h8;
            bool changed = ChangedBlocks().Any(i => !Removed.Contains(i));
            bool structural = NewObjs.Count > 0 || Removed.Count > 0 || ids.Count != N0;
            if (!structural) for (int n = 0; n < ids.Count; n++) if (ids[n] != n) { structural = true; break; }
            if (!changed && !structural) return hp;
            if (log != null) log("measuring the loader's waiter peak (H7) and linear allocator use (H8)...");
            RuntimeProfile bp = BaseProfile();
            RuntimeProfile np = Profile(ids);
            hp.Measured = true;
            hp.NewProfile = np;
            hp.WaiterPeakOriginal = bp.WaiterPeak;
            hp.WaiterPeakNew = np.WaiterPeak;
            hp.LinearOriginal = bp.LinearBytes;
            hp.LinearNew = np.LinearBytes;
            hp.PostLoadNew = np.PostLoadBytes;
            hp.Unmodelled = np.Unmodelled;
            long H7 = h7, H8 = h8;
            if (np.WaiterPeak > bp.WaiterPeak) H7 = h7 + (np.WaiterPeak - bp.WaiterPeak);
            long grow = np.LinearBytes - bp.LinearBytes;
            if (grow > 0) H8 = (H8 + grow + 15) & ~15L;
            // the post-load step allocates from the same allocator after the blob loop; the version-3 slack
            // covers it (EA's H8 does not include it), but never let the total exceed the capacity
            Func<long, long> cap = h => Header.Version == 3 ? Runtime.LinearSlack(h) : h;
            long need = np.LinearBytes + np.PostLoadBytes;
            while (cap(H8) < need) H8 = (H8 + need - cap(H8) + 15) & ~15L;
            if (H7 > uint.MaxValue || H8 > uint.MaxValue) throw new GdException("header values overflow 32 bits");
            hp.H7 = (uint)H7;
            hp.H8 = (uint)H8;
            return hp;
        }

        // ------------------------------------------------------------------ save
        /// <summary>
        /// Builds the edited container.  derive = true first brings redundant data in line with the edits
        /// (count fields, two-way links, blend space diagrams, state machine tables).  Objects that were only
        /// looked at (decoded but equal to the original bytes) are written verbatim and do not count as changes.
        /// withoutPrefix = true leaves out the 16 bytes before the version dword (the layout Frosty stores).
        /// </summary>
        public SaveResult Save(bool withoutPrefix, Action<string> log = null, bool autoHeader = true, bool derive = true,
                               uint? h7 = null, uint? h8 = null, Action<string, double> progress = null)
        {
            SaveResult res = new SaveResult();
            res.WithoutPrefix = withoutPrefix;
            Action<string> L = s => { res.Log.Add(s); if (log != null) log(s); };
            if (derive)
            {
                if (progress != null) progress("deriving redundant data", 0.02);
                Consistency.FixAll(this, L);
                if (StateMachine.Touched(this))
                {
                    int n = StateMachine.Rebuild(this);
                    if (n > 0) L("state machine runtime tables rebuilt on " + n + " objects");
                }
            }
            SyncKeys();
            List<int> ids = LiveIds();
            HashSet<ulong> seen = new HashSet<ulong>();
            foreach (int i in ids)
                if (!seen.Add(Keys[i])) throw new GdException("duplicate __key 0x" + Keys[i].ToString("x16") + " among root objects");
            List<ulong> T = ids.Select(i => Keys[i]).ToList();
            T.Sort(BE.CompareSigned);
            List<ulong> Cx = Exports.Where(k => seen.Contains(k)).ToList();
            Cx.Sort(BE.CompareSigned);
            List<ARow> A;
            List<BRow> B;
            ComputeImports(ids, out A, out B);
            modified = new HashSet<int>();
            List<BlockSrc> blocks = new List<BlockSrc>(ids.Count);
            long maxPayload = 0;
            int done = 0;
            foreach (int i in ids)
            {
                BlockSrc bs;
                if (i >= N0)
                {
                    bs = new BlockSrc(ObjCodec.EncodeBlock(NewObjs[i], Types));
                    modified.Add(i);
                }
                else if (Cache.ContainsKey(i))
                {
                    byte[] b = ObjCodec.EncodeBlock(Cache[i], Types);
                    if (!C.BlockEquals(i, b)) { modified.Add(i); bs = new BlockSrc(b); }
                    else bs = C.Block(i);
                }
                else bs = C.Block(i);
                if (bs.Len - 16 > maxPayload) maxPayload = bs.Len - 16;
                blocks.Add(bs);
                if (progress != null && ++done % 20000 == 0) progress("encoding blocks", 0.05 + 0.25 * done / ids.Count);
            }
            HeaderPlan plan = null;
            uint H7 = h7 ?? Header.H[6], H8 = h8 ?? Header.H[7];
            if (autoHeader && (!h7.HasValue || !h8.HasValue))
            {
                if (progress != null) progress("measuring the loader's use of the header values", 0.35);
                plan = PlanHeader(ids, L);
                if (!h7.HasValue) H7 = plan.H7;
                if (!h8.HasValue) H8 = plan.H8;
                if (H7 != Header.H[6] || H8 != Header.H[7])
                    L("header: H7 " + Header.H[6] + " -> " + H7 + ", H8 0x" + Header.H[7].ToString("X") + " -> 0x" + H8.ToString("X") + "  (" + plan + ")");
            }
            if (progress != null) progress("writing the container", 0.7);
            Header hdr = Header.Copy();
            hdr.H[6] = H7;
            hdr.H[7] = H8;
            long strmPos;
            uint strmAux = StrmAux;
            long needAux = (maxPayload + 15) & ~15L;
            if (needAux > strmAux)
            {
                L("GD.STRMb aux raised 0x" + strmAux.ToString("X") + " -> 0x" + needAux.ToString("X") + " (largest payload " + maxPayload + " bytes)");
                strmAux = (uint)needAux;
            }
            res.Image = Container.BuildFile(hdr, T, Cx, A, B, strmAux, Types.ToBytes(), Types.Aux, blocks, withoutPrefix, out strmPos);
            res.Blocks = ids.Count;
            res.Modified = modified.Count(i => !Removed.Contains(i));
            res.H7 = H7;
            res.H8 = H8;
            res.StrmAux = strmAux;
            res.Plan = plan;
            res.Ids = ids;
            if (modified.Count > 0)
            {
                if (progress != null) progress("checking engine limits", 0.9);
                foreach (LimitIssue w in Limits.Check(this, modified.Where(i => !Removed.Contains(i)).ToList()))
                    res.Warnings.Add(w.ToString());
                foreach (string w in res.Warnings) L("WARNING " + w);
            }
            return res;
        }

        public SaveResult SaveToFile(string path, Action<string> log = null)
        {
            SaveResult r = Save(false, log);
            string tmp = path + ".tmp";
            File.WriteAllBytes(tmp, r.Image);
            if (File.Exists(path)) File.Delete(path);
            File.Move(tmp, path);
            return r;
        }
    }

    /// <summary>deep copies of decoded values (shared nested objects stay shared within the copy)</summary>
    public static class DeepCopy
    {
        public static GdObj Obj(GdObj o)
        {
            Dictionary<GdStruct, GdStruct> memo = new Dictionary<GdStruct, GdStruct>(RefEq<GdStruct>.Instance);
            return (GdObj)Value(o, memo);
        }

        public static object Value(object v) { return Value(v, new Dictionary<GdStruct, GdStruct>(RefEq<GdStruct>.Instance)); }

        private static object Value(object v, Dictionary<GdStruct, GdStruct> memo)
        {
            if (v == null) return null;
            GdStruct s = v as GdStruct;
            if (s != null)
            {
                GdStruct r;
                if (memo.TryGetValue(s, out r)) return r;
                GdObj so = s as GdObj;
                r = so != null ? new GdObj(s.Type, so.HdrExtra) : new GdStruct(s.Type);
                memo[s] = r;
                for (int i = 0; i < s.Values.Length; i++) r.Values[i] = Value(s.Values[i], memo);
                return r;
            }
            if (v is byte[]) return v;                 // immutable by convention (replaced whole)
            if (v is float[]) return ((float[])v).Clone();
            if (v is object[])
            {
                object[] a = (object[])v;
                object[] r = new object[a.Length];
                for (int i = 0; i < a.Length; i++) r[i] = Value(a[i], memo);
                return r;
            }
            RawList rl = v as RawList;
            if (rl != null) return new RawList(rl.Cap, rl.Ptr0);
            if (v is List<long>) return new List<long>((List<long>)v);
            if (v is List<ulong>) return new List<ulong>((List<ulong>)v);
            if (v is List<float>) return new List<float>((List<float>)v);
            if (v is List<bool>) return new List<bool>((List<bool>)v);
            List<GdObj> lo = v as List<GdObj>;
            if (lo != null) { List<GdObj> r = new List<GdObj>(lo.Count); foreach (GdObj e in lo) r.Add((GdObj)Value(e, memo)); return r; }
            List<GdStruct> ls = v as List<GdStruct>;
            if (ls != null) { List<GdStruct> r = new List<GdStruct>(ls.Count); foreach (GdStruct e in ls) r.Add((GdStruct)Value(e, memo)); return r; }
            List<float[]> lv = v as List<float[]>;
            if (lv != null) { List<float[]> r = new List<float[]>(lv.Count); foreach (float[] e in lv) r.Add((float[])e.Clone()); return r; }
            List<object> lob = v as List<object>;
            if (lob != null) { List<object> r = new List<object>(lob.Count); foreach (object e in lob) r.Add(Value(e, memo)); return r; }
            RawStr rs = v as RawStr;
            if (rs != null) return new RawStr(rs.Raw, rs.Cap, rs.Ptr0);
            return v;   // string, numbers, bool
        }
    }
}
