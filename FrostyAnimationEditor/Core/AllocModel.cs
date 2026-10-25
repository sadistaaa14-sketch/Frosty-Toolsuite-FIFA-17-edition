using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;

namespace FrostyAnimationEditor.Core
{
    public struct Alloc
    {
        public long Size;
        public long Align;
        public Alloc(long size, long align) { Size = size; Align = align; }
    }

    /// <summary>embedded data files (Core/Data/*.json)</summary>
    public static class CoreData
    {
        private static readonly Dictionary<string, object> cache = new Dictionary<string, object>();

        public static string Text(string name)
        {
            Assembly asm = typeof(CoreData).Assembly;
            string res = "FrostyAnimationEditor.Data." + name;
            using (Stream s = asm.GetManifestResourceStream(res))
            {
                if (s == null) throw new FileNotFoundException("embedded resource " + res + " is missing");
                using (StreamReader r = new StreamReader(s, Encoding.UTF8)) return r.ReadToEnd();
            }
        }

        public static object Json(string name)
        {
            lock (cache)
            {
                object v;
                if (!cache.TryGetValue(name, out v))
                {
                    v = MiniJson.Parse(Text(name));
                    cache[name] = v;
                }
                return v;
            }
        }
    }

    /// <summary>
    /// The AntLinearResourceAllocator cost of every object (what header H8 has to cover).
    /// The game sizes its loading memory from H8 (version-3 files get +0xC0080 + (H8+0xC0000)/0xA00 extra)
    /// and every object takes its share while it loads (align, then add; never freed).  For an object this gives the exact ordered
    /// list of (size, align) requests the game makes while loading it (checked on all 458,349 objects of the game's file):
    ///   * alloc_table.json - classes with a fixed allocation shape (constants, or sizes linear in
    ///     list/string lengths); "opt" slots are skipped when their size is 0;
    ///   * Custom below    - classes whose allocations depend on the content's structure;
    ///   * Post below      - the two type handlers that allocate in the post-load step.
    /// </summary>
    public static class AllocModel
    {
        public sealed class Rec
        {
            public readonly List<Alloc> Seq = new List<Alloc>();
            public void Add(long size, long align) { Seq.Add(new Alloc(size, align)); }
            /// <summary>an allocation the loader skips when its size is 0 (empty list)</summary>
            public void Opt(long size, long align) { if (size != 0) Seq.Add(new Alloc(size, align)); }
        }

        private delegate void Model(GdObj o, Rec A);
        private delegate void PostModel(GdObj o, Rec A, Func<ulong, GdObj> resolve);

        private static Dictionary<string, Model> custom;
        private static Dictionary<string, PostModel> post;
        private static JObject table;
        private static JObject dataIfs;
        private static readonly object initLock = new object();

        public static JObject Table { get { Init(); return table; } }

        private static void Init()
        {
            if (custom != null) return;
            lock (initLock)
            {
                if (custom != null) return;
                table = (JObject)CoreData.Json("alloc_table.json");
                dataIfs = (JObject)CoreData.Json("data_interfaces.json");
                Dictionary<string, PostModel> p = new Dictionary<string, PostModel>();
                p["LayoutHierarchyAsset"] = PostLha;
                p["LocoControllerAsset"] = PostLoco;
                post = p;
                custom = BuildCustom();
            }
        }

        public static long AlignUp(long x, long a) { return (x + a - 1) & ~(a - 1); }

        // ---------------------------------------------------------------------------- helpers
        /// <summary>Python _get: inherited lookup for objects, own field for structs</summary>
        public static object G(GdStruct o, string name)
        {
            if (o == null) return null;
            GdObj ob = o as GdObj;
            if (ob != null) return ob.GetInherited(name);
            return o[name];
        }

        public static long GL(GdStruct o, string name) { object v = G(o, name); return v == null ? 0 : Num.ToLong(v); }

        /// <summary>Python len() of a list/bytes/str value, 0 for anything else</summary>
        public static long LenOf(object v)
        {
            if (v == null) return 0;
            byte[] b = v as byte[];
            if (b != null) return b.Length;
            if (v is float[]) return 0;
            string s = v as string;
            if (s != null) return s.Length;
            RawStr r = v as RawStr;
            if (r != null) return r.Raw.Length;
            ICollection c = v as ICollection;
            if (c != null) return c.Count;
            return 0;
        }

        public static long Len(GdStruct o, string name) { return LenOf(G(o, name)); }

        /// <summary>AssetString: the characters plus the terminator (nothing for a null string)</summary>
        public static long StrSz(object s)
        {
            if (s == null) return 0;
            return Str.Utf8Len(s) + 1;
        }

        private static IList ListOf(object v) { return v as IList ?? new object[0]; }

        private static void Lists(GdObj o, Rec A, params object[] spec)
        {
            for (int i = 0; i < spec.Length; i += 3)
                A.Opt(Convert.ToInt64(spec[i + 1]) * Len(o, (string)spec[i]), Convert.ToInt64(spec[i + 2]));
        }

        // ---------------------------------------------------------------------------- features
        /// <summary>numeric features at every field-path pattern ([] = any element): len: list lengths
        /// (summed over parents), slen:/snn: string bytes / non-null strings, blen: byte-blob lengths,
        /// val: top-level integer values, nzc: non-zero counts, nn: present structs</summary>
        public static Dictionary<string, long> Features(GdStruct o)
        {
            Dictionary<string, long> f = new Dictionary<string, long>();
            FWalk(o, "", true, f);
            return f;
        }

        private static void Inc(Dictionary<string, long> f, string k, long v)
        {
            long c;
            f.TryGetValue(k, out c);
            f[k] = c + v;
        }

        private static bool IsInt(object x)
        {
            return x is long || x is ulong || x is int || x is uint || x is short || x is ushort || x is byte || x is sbyte;
        }

        private static bool NonZero(object x)
        {
            if (x is ulong) return (ulong)x != 0;
            return Num.ToLong(x) != 0;
        }

        private static void FWalk(GdStruct v, string path, bool top, Dictionary<string, long> f)
        {
            TypeDesc t = v.Type;
            for (int i = 0; i < t.Fields.Count; i++)
            {
                object x = v.Values[i];
                string p = (path.Length > 0 ? path + "." : "") + t.Fields[i].Name;
                if (x == null) continue;
                IList l = x as IList;
                if (l != null && !(x is byte[]) && !(x is float[]))
                {
                    Inc(f, "len:" + p, l.Count);
                    foreach (object e in l)
                    {
                        if (e == null) continue;
                        if (e is GdStruct) FWalk((GdStruct)e, p + "[]", false, f);
                        else if (e is string) { Inc(f, "slen:" + p + "[]", Str.Utf8Len(e)); Inc(f, "snn:" + p + "[]", 1); }
                        else if (e is byte[] || e is RawStr) Inc(f, "blen:" + p + "[]", LenOf(e));
                        else if (IsInt(e) && NonZero(e)) Inc(f, "nzc:" + p + "[]", 1);
                    }
                }
                else if (x is GdStruct)
                {
                    Inc(f, "nn:" + p, 1);
                    FWalk((GdStruct)x, p, top, f);
                }
                else if (x is string) { Inc(f, "slen:" + p, Str.Utf8Len(x)); Inc(f, "snn:" + p, 1); }
                else if (x is byte[] || x is RawStr) Inc(f, "blen:" + p, LenOf(x));
                else if (x is bool) { if (top) Inc(f, "val:" + p, (bool)x ? 1 : 0); }
                else if (IsInt(x))
                {
                    if (top)
                    {
                        long add;
                        if (x is ulong) add = (ulong)x < (1UL << 31) ? (long)(ulong)x : 0;
                        else { long lv = Num.ToLong(x); add = (lv < (1L << 31) && lv > -(1L << 31)) ? lv : 0; }
                        Inc(f, "val:" + p, add);
                    }
                    if (NonZero(x)) Inc(f, "nzc:" + p, 1);
                }
            }
        }

        private static long SlotSize(JObject slot, Dictionary<string, long> f)
        {
            if (slot.ContainsKey("const")) return slot.Long("const");
            long v = slot.Long("b");
            foreach (object term in slot.Arr("terms"))
            {
                List<object> tl = (List<object>)term;
                long c;
                f.TryGetValue((string)tl[0], out c);
                v += Num.ToLong(tl[1]) * c;
            }
            if (slot.ContainsKey("round")) v = AlignUp(v, slot.Long("round"));
            return v;
        }

        // ---------------------------------------------------------------------------- entry points
        public static bool HasModel(string typeName)
        {
            Init();
            return custom.ContainsKey(typeName) || table.ContainsKey(typeName);
        }

        /// <summary>ordered requests while the blob loop loads o (null if the class has no model)</summary>
        public static List<Alloc> Allocations(GdObj o)
        {
            Init();
            string t = o.Type.Name;
            Model fn;
            if (custom.TryGetValue(t, out fn))
            {
                Rec A = new Rec();
                fn(o, A);
                return A.Seq;
            }
            JObject ent = table.Obj(t);
            if (ent == null) return null;
            Dictionary<string, long> f = null;
            List<Alloc> outp = new List<Alloc>();
            List<object> shape = ent.Arr("shape");
            List<object> slots = ent.Arr("slots");
            int n = Math.Min(shape.Count, slots.Count);
            for (int i = 0; i < n; i++)
            {
                long al = Num.ToLong(((List<object>)shape[i])[1]);
                JObject slot = (JObject)slots[i];
                long size;
                if (slot.ContainsKey("const")) size = slot.Long("const");
                else
                {
                    if (f == null) f = Features(o);
                    size = SlotSize(slot, f);
                }
                if (size != 0 || !slot.Bool("opt")) outp.Add(new Alloc(size, al));
            }
            return outp;
        }

        public static bool HasPost(string typeName) { Init(); return post.ContainsKey(typeName); }
        public static IEnumerable<string> PostClasses { get { Init(); return post.Keys; } }

        /// <summary>ordered requests o's type handler makes in the post-load step (empty for most classes)</summary>
        public static List<Alloc> PostAllocations(GdObj o, Func<ulong, GdObj> resolve)
        {
            Init();
            PostModel fn;
            if (!post.TryGetValue(o.Type.Name, out fn)) return new List<Alloc>();
            Rec A = new Rec();
            fn(o, A, resolve);
            return A.Seq;
        }

        public static long RunUsage(IEnumerable<Alloc> seq, long start = 0)
        {
            long p = start;
            foreach (Alloc a in seq) p = ((p + a.Align - 1) & ~(a.Align - 1)) + a.Size;
            return p;
        }

        // ---------------------------------------------------------------------------- post-load
        private static void PostLha(GdObj o, Rec A, Func<ulong, GdObj> db)
        {
            // one 16-byte (key, index) map entry per LayoutAssets element
            A.Opt(16 * Len(o, "LayoutAssets"), 8);
        }

        private static void PostLoco(GdObj o, Rec A, Func<ulong, GdObj> db)
        {
            // a 64-byte BumbleBerryControllerAsset, then, when
            // PriorityDatabase resolves, 8 bytes per entry of its ContextDB (requested even for 0 entries)
            A.Add(64, 8);
            ulong k = Num.ToKey(G(o, "PriorityDatabase"));
            GdObj p = (k != 0 && db != null) ? db(k) : null;
            if (p != null)
            {
                GdStruct cdb = p["ContextDB"] as GdStruct;
                A.Add(8 * (cdb != null ? LenOf(cdb["EntryAssets"]) : 0), 8);
            }
        }

        // ---------------------------------------------------------------------------- data interfaces
        /// <summary>the 53 game-side data interfaces: guid -> {name, size, members}</summary>
        public static JObject DataInterfaces { get { Init(); return dataIfs; } }

        public static JObject DataInterface(GdObj o)
        {
            Init();
            long g = GL(o, "GuidHash");
            return dataIfs.Obj(((uint)g).ToString("x8"));
        }

        // ---------------------------------------------------------------------------- misc helpers
        private static float F32(double x) { return (float)x; }

        /// <summary>floor((TrajEndTick - TrajStartTick) / RecordDeltaT) + 1 in float32, as int16</summary>
        public static long TrajSamples(GdStruct ts)
        {
            long a = Num.ToLong(ts["TrajStartTick"]), b = Num.ToLong(ts["TrajEndTick"]);
            if (a > b) return 0;
            float dt = Num.ToFloat(ts["RecordDeltaT"]);
            if (dt == 0) return 0;
            float q = (float)((double)(float)(double)(b - a) / (double)dt);
            if (float.IsNaN(q) || Math.Abs((double)q) >= 2147483648.0) return 0;
            long n = ((long)Math.Floor((double)q) + 1) & 0xFFFF;
            return n >= 0x8000 ? n - 0x10000 : n;
        }

        /// <summary>align16((slots + 1) * 32 + strlen(name) + 2 + sum(strlen(slot) + 1))</summary>
        public static long LayoutSize(object name, IList slotNames)
        {
            long n = slotNames.Count;
            long size = (n + 1) * 32 + (name == null ? 0 : Str.Utf8Len(name)) + 2;
            foreach (object sn in slotNames)
                if (sn != null) size += Str.Utf8Len(sn) + 1;
            return AlignUp(size, 16);
        }

        /// <summary>~CRC32(MSB-first) of the name seeded with 0x811C9DC5</summary>
        public static uint RigopHash(object name)
        {
            byte[] b;
            if (name == null) b = new byte[0];
            else if (name is string) b = Encoding.UTF8.GetBytes((string)name);
            else if (name is RawStr) b = ((RawStr)name).Raw;
            else if (name is byte[]) b = (byte[])name;
            else b = Encoding.UTF8.GetBytes(name.ToString());
            return ~TypeTable.Crc32Msb(b, 0x811C9DC5u);
        }

        private static long SizedAlign(long n) { return n < 4 ? 2 : n < 8 ? 4 : n < 16 ? 8 : 16; }

        private static bool Truthy(object v)
        {
            if (v == null) return false;
            if (v is bool) return (bool)v;
            if (v is string) return ((string)v).Length != 0;
            if (v is RawStr) return ((RawStr)v).Raw.Length != 0;
            if (v is ulong) return (ulong)v != 0;
            if (v is float) return (float)v != 0;
            if (v is double) return (double)v != 0;
            ICollection c = v as ICollection;
            if (c != null) return c.Count != 0;
            if (v is byte[]) return ((byte[])v).Length != 0;
            return Num.ToLong(v) != 0;
        }

        // ---------------------------------------------------------------------------- context databases
        private static long S32(long x)
        {
            x &= 0xFFFFFFFF;
            return x >= (1L << 31) ? x - (1L << 32) : x;
        }

        public struct CdbField
        {
            public long Type, Flag, Value, Guid;
            public CdbField(long t, long f, long v, long g) { Type = t; Flag = f; Value = v; Guid = g; }
        }

        /// <summary>the runtime field records the game builds from FieldDescriptionAssets</summary>
        public static List<CdbField> CdbFields(GdStruct cdb)
        {
            Init();
            JObject reg = table.Obj("__cdb_registry__");
            List<CdbField> outp = new List<CdbField>();
            foreach (object e in ListOf(cdb["FieldDescriptionAssets"]))
            {
                GdStruct fd = e as GdStruct;
                if (fd == null) continue;
                if (fd.Type.Name == "RegistryFieldAsset")
                {
                    long guid = Num.ToLong(fd["ClassGuid"]) & 0xFFFFFFFF;
                    long fid = Num.ToLong(fd["FieldId"]) & 0xFFFFFFFF;
                    List<object> r = reg[guid.ToString("x8") + ":" + fid.ToString("x8")] as List<object>;
                    if (r == null) outp.Add(new CdbField(-1, 0, 0, guid));
                    else outp.Add(new CdbField(Num.ToLong(r[0]), Num.ToLong(r[1]), S32(Num.ToLong(r[2])), guid));
                }
                else
                {
                    long typ = Num.ToLong(fd["FieldType"]);
                    long guid = Truthy(fd["IsCustomField"]) ? 0x1C491A10 : 0x15A9AE49;
                    long val = (typ == 1 || typ == 2 || typ == 3) ? S32(Num.ToLong(fd["IntMax"])) : 0;
                    outp.Add(new CdbField(typ, Truthy(fd["IsMetadata"]) ? 1 : 0, val, guid));
                }
            }
            return outp;
        }

        private static long IntWidth(long v) { return v < 0x100 ? 2 : v < 0x10000 ? 4 : (v > 0xFFFFFF ? 8 : 6); }

        private static long CDiv8(long x) { return x / 8; }     // C truncation toward zero

        /// <summary>the size of a context database, its fields sorted with the metadata ones first</summary>
        public static long CdbSize(GdStruct cdb)
        {
            List<CdbField> fl = CdbFields(cdb);
            long n = fl.Count;
            long p10 = LenOf(cdb["EntryAssets"]);
            long p24 = S32(Num.ToLong(cdb["NumDeduplicatedListAssets"]));
            long p28 = S32(Num.ToLong(cdb["NumEntryAssetsTotal"]));
            bool p38 = Truthy(cdb["FieldInputSourceIndices"]);
            long i3 = 0, i6 = 0, i2 = 0;
            foreach (CdbField c in fl)
            {
                if (c.Type == 0) { if (c.Flag != 0) i2++; else i6++; }
                else if (c.Type == 1 || c.Type == 2) i3 += IntWidth(c.Value);
                else if (c.Type == 3) i3 += CDiv8(c.Value + 7);
                else if (c.Type >= 4 && c.Type <= 7) i3 += 2;
                else if (c.Type == 8) i3 += 3;
            }
            long row = i3 + (CDiv8(i2 + 7) + CDiv8(i6 + 7)) * 2;
            long i4 = 0;
            i2 = 0;
            foreach (CdbField c in fl)
            {
                if (c.Flag != 0) continue;
                if (c.Type == 0) i2++;
                else if (c.Type == 1 || c.Type == 2) i4 += IntWidth(c.Value);
                else if (c.Type == 3) i4 += CDiv8(c.Value + 7);
                else if (c.Type >= 4 && c.Type <= 7) i4 += 2;
                else if (c.Type == 8) i4 += 1;
            }
            long lead = CDiv8(i2 + 7) + i4;
            HashSet<long> classes = new HashSet<long>();
            foreach (CdbField c in fl) classes.Add(c.Guid);
            long u7 = p28 - p10 > 0 ? ((p10 + 31) / 32) * 4 : 0;
            long u8 = p38 ? AlignUp(n * 4, 16) : 0;
            return AlignUp(lead, 16) + classes.Count * 0x40 + u7 + ((p10 + 15) / 16 * row) * 16
                   + ((p24 * 5 - p10) + p28) * 8 + (n * 0x30 + lead) * 2 + 0x110 + AlignUp(p10 * 8, 16) + u8;
        }

        private static long GameStateFields(GdStruct cdb)
        {
            long n = 0;
            foreach (object e in ListOf(cdb["FieldDescriptionAssets"]))
            {
                GdStruct fd = e as GdStruct;
                if (fd != null && fd.Type.Name != "RegistryFieldAsset" && !Truthy(fd["IsCustomField"])) n++;
            }
            return n;
        }

        private static void CdbTail(GdStruct cdb, Rec A)
        {
            A.Opt(8 * LenOf(cdb["EntryAssets"]), 8);
            long k = GameStateFields(cdb);
            if (k > 0) A.Add(0x18 * k, 16);                                   //
        }

        // ---------------------------------------------------------------------------- custom models
        private static readonly string[] DeltaTrajSlots = { "deltaRotation", "angularVelocity", "angularAcceleration", "deltaTranslation",
                                                            "velocity", "acceleration", "lastTranslation", "lastRotation" };

        private static object NameOrNull(GdObj o) { return o["__name"]; }

        private static void AStr(object s, Rec A)
        {
            // an AssetString copy of len+1 bytes, nothing for a null or empty string
            string str = s as string;
            if (str != null && str.Length != 0) A.Add(Encoding.UTF8.GetByteCount(str) + 1, 1);
        }

        private static Dictionary<string, Model> BuildCustom()
        {
            Dictionary<string, Model> m = new Dictionary<string, Model>();

            m["VbrAnimationAsset"] = (o, A) =>
            {
                A.Add(176, 16);
                long n24 = Len(o, "ConstantPalette"), n25 = Len(o, "FrameBlockSizes"), n26 = Len(o, "Data");
                A.Add(AlignUp(AlignUp(n26, 16) + (n25 + n24 * 2) * 2, 16) + 16, 16);
            };
            m["StateFlowTransitionAsset"] = (o, A) =>
            {
                A.Add(96, 8);
                A.Opt(40 * (Len(o, "ConditionsRequiredTrue") + Len(o, "ConditionsRequiredFalse")), 8);
            };
            m["FeatureCollectionAsset"] = (o, A) => { A.Add(32, 8); Lists(o, A, "FeatureAssets", 8, 8); };
            m["OverrideGameStatesCollectionAsset"] = (o, A) =>
            {
                A.Add(128, 16);
                Lists(o, A, "OverrideTargets", 8, 8, "OverrideAssets", 8, 8, "OverrideVec3s", 16, 16,
                      "OverrideFloats", 4, 4, "OverrideInts", 4, 4, "OverrideBools", 1, 1);
            };
            m["ModelFeatureAsset"] = (o, A) =>
            {
                A.Add(40, 8);
                A.Opt(StrSz(G(o, "Model")), 1);
                A.Opt(StrSz(G(o, "ModelPath")), 1);
            };
            m["DefaultTagCollectionAsset"] = (o, A) => { A.Add(56, 8); Lists(o, A, "TagKeys", 8, 8, "MonitoredTags", 8, 8); };
            m["RelativeIkActorControllerAsset"] = (o, A) => { A.Add(56, 8); Lists(o, A, "Entries", 8, 8); };
            m["CharacterInteractionControllerAsset"] = (o, A) =>
            {
                A.Add(200, 8);
                Lists(o, A, "QueryTasks", 8, 8, "SlaveControllerAsset", 8, 8, "SlaveControllerInitParams", 8, 8);
            };
            m["QueryTaskAsset"] = (o, A) => { A.Add(120, 8); Lists(o, A, "Filters", 8, 8, "Processors", 8, 8); };
            m["GKCoverageMapDecisionTreeAsset"] = (o, A) =>
            {
                A.Add(160, 16);
                A.Opt(8 * Len(o, "Nodes"), 16);
                Lists(o, A, "AttributeTouchPosX", 4, 4, "AttributeTouchPosY", 4, 4, "AttributeTouchPosZ", 4, 4, "ReturnValues", 12, 16);
            };
            m["InteractionFeatureAsset"] = (o, A) => { A.Add(128, 8); Lists(o, A, "ContextBundles", 8, 8, "QueryTasks", 8, 8); };
            m["FloatGameStateTag"] = (o, A) =>
            {
                long fl = GL(o, "SplineStorageFlags");
                if (fl == 0x10)
                {
                    A.Add(0x50, 8);
                    long n = Len(o, "Spline");
                    if (n > 1) A.Add(4 * n, 4);
                }
                else if ((fl & 0x20) != 0) A.Add(0x80, 16);
                else if (fl == 0x40)
                {
                    A.Add(0x50, 8);
                    long mm = Len(o, "SplineRaw");
                    if (mm > 4) A.Add(mm, 1);
                }
            };
            m["DofWeightCurvesAsset"] = (o, A) =>
            {
                A.Add(152, 8);
                List<GdStruct> sets = new List<GdStruct>();
                foreach (object s in ListOf(G(o, "DofSetCurves"))) if (s is GdStruct) sets.Add((GdStruct)s);
                A.Opt(8 * sets.Count, 4);
                long n = 0;
                foreach (GdStruct s in sets) n += LenOf(s["InheritedMaskBytes"]);
                A.Opt(4 * n, 4);
                A.Opt(n, 16);
            };
            m["AudioEventTag"] = (o, A) =>
            {
                A.Add(320, 8);
                AStr(G(o, "EventName"), A);
                Lists(o, A, "ParamTypes", 4, 4, "IntIDs", 4, 4, "IntVals", 4, 4, "IntGSIDs", 4, 4,
                      "IntGSs", 8, 8, "FloatIDs", 4, 4, "FloatVals", 4, 4, "FloatGSIDs", 4, 4,
                      "FloatGSs", 8, 8, "BoolIDs", 4, 4, "BoolVals", 1, 1, "BoolGSIDs", 4, 4,
                      "BoolGSs", 8, 8, "StringIDs", 4, 4);
                IList sv = ListOf(G(o, "StringVals"));
                A.Opt(8 * sv.Count, 8);
                foreach (object s in sv) AStr(s, A);
            };
            m["BallOffsetsAsset"] = (o, A) => { A.Add(64, 16); Lists(o, A, "Rotations", 16, 16, "Translations", 16, 16); };
            m["CollisionDetectionSceneOpAsset"] = (o, A) => { A.Add(224, 16); Lists(o, A, "VolumeSetting", 40, 16, "DebugSelections", 16, 16); };
            m["ConstCollectionAsset"] = (o, A) =>
            {
                A.Add(32, 8);
                GdStruct mv = G(o, "MemberValues") as GdStruct;
                if (mv != null) A.Add(mv.Type.Size, 1);
            };
            m["ContactPointFeatureAsset"] = (o, A) =>
            {
                A.Add(152, 8);
                Lists(o, A, "JointIDs", 8, 8, "ExtraJointIDs", 8, 8, "DeltaDofIndices", 4, 4,
                      "ReachDofIndices", 4, 4, "PullFloats", 8, 8, "ResistFloats", 8, 8);
            };
            m["CullingTableAsset"] = (o, A) =>
            {
                A.Add(0x18, 8);
                long words = (GL(o, "RowCount") * GL(o, "ColumnCount") + 31) >> 5;
                A.Add((words * 4 + 0x1f) & ~0xfL, 16);
            };
            m["DatasetPreSaveAsset"] = (o, A) => { A.Add(88, 8); Lists(o, A, "Min", 36, 16, "Max", 36, 16, "Data", 36, 16); };
            m["GKBridgeControllerAsset"] = (o, A) =>
            {
                A.Add(240, 16);
                Lists(o, A, "BlendTime", 4, 4, "PositionCache", 16, 16, "AngleCache", 4, 4,
                      "MinimumFaceAngle", 4, 4, "MaximumFaceAngle", 4, 4, "MinimumDistance", 4, 4,
                      "MaximumDistance", 4, 4, "DirectionCenter", 4, 4, "DirectionWidth", 4, 4);
            };
            m["GamepadConfigAsset"] = (o, A) =>
            {
                A.Add(40, 8);
                IList maps = ListOf(G(o, "GamepadMappingList"));
                if (maps.Count != 0) A.Add(32 * maps.Count + 8, 8);
                foreach (object mo in maps)
                {
                    GdStruct ms = mo as GdStruct;
                    A.Opt(8 * (ms != null ? LenOf(ms["SignalDriverList"]) : 0), 8);
                }
            };
            m["GestureToSignalMappingAsset"] = (o, A) => { A.Add(72, 8); Lists(o, A, "DeviceList", 8, 8, "Gesture", 8, 8); };
            m["InteractionScaleFeatureAsset"] = (o, A) =>
            {
                A.Add(224, 8);
                Lists(o, A, "ScaledEffectors", 1, 1, "JointWeightDofIndices", 4, 4,
                      "JointScaleAdjustDofIndices", 4, 4, "JointIDs", 8, 8, "ChildEffector1", 4, 4,
                      "ChildEffector2", 4, 4, "ChildEffector3", 4, 4, "ChildEffector4", 4, 4,
                      "ChildEffector5", 4, 4, "ChildEffector6", 4, 4);
            };
            m["JointToEffectorFeatureAsset"] = (o, A) => { A.Add(80, 8); Lists(o, A, "EffectorJoints", 4, 4, "ReachFloats", 8, 8, "CanCopyJoint", 8, 8); };
            m["ParticleIKFeatureAsset"] = (o, A) =>
            {
                A.Add(0xb8, 8);
                A.Add(26 * 8, 4);
                A.Add(26 * 4, 4);
                A.Add(4 * (GL(o, "NumTotalJoints") + 1), 4);
                long n = 0;
                foreach (object x in ListOf(G(o, "MappedJoints"))) if ((Num.ToLong(x) & 0xFFFFFFFF) != 0xFFFFFFFF) n++;
                long cap = 26;
                while (n > cap)
                {
                    cap *= 2;
                    A.Add(cap * 8, 4);
                }
            };
            m["ParticleIKSceneOpAsset"] = (o, A) => { A.Add(224, 16); Lists(o, A, "AngleConstraints", 80, 16, "DebugOptions", 16, 16, "HingeConstraints", 64, 16); };
            m["PhysicsAssemblyAsset"] = (o, A) => { A.Add(88, 8); Lists(o, A, "PartList", 8, 8, "DriveList", 8, 8, "JointList", 8, 8); };
            m["UCCSignalsSceneOpAsset"] = (o, A) =>
            {
                A.Add(112, 8);
                Lists(o, A, "RunStyleInputSignals", 8, 8, "InputDoubleTapSignal", 8, 8,
                      "InputHoldSignal", 8, 8, "InputBufferDoubleTapHold", 4, 4, "OutputDoubleTapHoldSignal", 8, 8);
            };
            m["StructAsset"] = (o, A) =>
            {
                GdStruct md = G(o, "MemberDefaults") as GdStruct;
                int n = md != null ? md.Type.Fields.Count : 0;
                A.Opt(4 * n, 4);
                A.Add(LayoutSize(NameOrNull(o) != null ? (object)o.Name : null, new object[n]), 16);
                A.Add(md != null ? md.Type.Size : 0, 16);
                A.Add(0x78, 8);
            };
            m["DeltaTrajLayoutAsset"] = (o, A) =>
            {
                A.Add(0x18, 8);
                A.Add(LayoutSize(NameOrNull(o) != null ? (object)o.Name : null, DeltaTrajSlots), 16);
            };
            m["Vector3GameStateTag"] = (o, A) => { A.Add(112, 16); A.Opt(16 * Len(o, "Splines"), 16); };
            m["LayoutHierarchyAsset"] = (o, A) =>
            {
                A.Add(160, 8);
                A.Opt(StrSz(NameOrNull(o) != null ? (object)o.Name : null), 1);
                Lists(o, A, "LayoutAssets", 8, 8, "Children", 8, 8);
            };
            m["BlendMaskListAsset"] = (o, A) => { A.Add(64, 8); Lists(o, A, "MaskKeys", 8, 8, "DefaultDofSetKeys", 8, 8, "DefaultValues", 4, 4); };
            m["ChannelToDofAsset"] = (o, A) => { A.Add(40, 8); Lists(o, A, "DofIds", 2, 2); };
            m["ContextBundleAsset"] = (o, A) => { A.Add(64, 8); Lists(o, A, "ElementAssets", 8, 8, "PreDriverAssets", 8, 8, "DriverAssets", 8, 8); };
            m["SignalChooserEntryAsset"] = (o, A) => { A.Add(56, 8); Lists(o, A, "TrueSignals", 8, 8, "FalseSignals", 8, 8); };
            m["QueryGroupEnumAsset"] = (o, A) =>
            {
                A.Add(64, 8);
                Lists(o, A, "EnumerationValues", 8, 8);
                GdObj bs = o.Base;
                A.Opt(StrSz(bs != null ? bs["__name"] : null), 1);
            };
            m["ITKResultTagResetGameStateTag"] = (o, A) => { A.Add(56, 8); Lists(o, A, "ITKGameStates", 8, 8); };
            m["RelativeIkFeatureAsset"] = (o, A) => { A.Add(72, 8); Lists(o, A, "RelativeIkRegions", 8, 8); };
            m["TwoBoneIKFeatureAsset"] = (o, A) => { A.Add(48, 8); Lists(o, A, "ChainLists", 8, 8); };
            m["JointMappingFeature"] = (o, A) =>
            {
                A.Add(48, 8);
                IList maps = ListOf(G(o, "JointMappings"));
                A.Opt(32 * maps.Count, 8);
                foreach (object mo in maps) A.Opt(4 * Len(mo as GdStruct, "JointIndices"), 4);
            };
            m["LayoutAsset"] = (o, A) =>
            {
                A.Add(24, 8);
                List<object> names = new List<object>();
                foreach (object e in ListOf(G(o, "Slots"))) names.Add(((GdStruct)e)["Name"]);
                A.Add(LayoutSize(o.Name, names), 16);
            };
            m["EffectorLayoutAsset"] = (o, A) =>
            {
                A.Add(24, 8);
                A.Add(LayoutSize(o.Name, new object[] { "Rotation", "Translation", "ReachT", "ReachR", "Pull", "Resist" }), 16);
            };
            m["WarpControllerAsset"] = (o, A) =>
            {
                A.Add(448, 16);
                GdStruct w = G(o, "WarpAsset") as GdStruct;
                if (w == null) return;
                long lp = Num.ToLong(w["LenPosition"]);
                A.Add((lp + 2) * (Truthy(w["StoreLeanPose"]) ? 0x16 : 0x12), 16);
                long n = Num.ToLong(w["NumTouchRegionSpeeds"]);
                if (n > 0) A.Add(16 * n, 16);
            };
            m["ActorControllerAsset"] = (o, A) =>
            {
                A.Add(96, 8);
                if (G(o, "DefaultInitParam") != null) A.Add(0x38, 8);
                long nt = GL(o, "NumTracks");
                if (nt != 0) A.Add(8 + 32 * nt, 8);
                GdStruct ts = G(o, "TrajectoryState") as GdStruct;
                if (ts != null)
                {
                    A.Add(0x40, 16);
                    long n = TrajSamples(ts);
                    if (n > 0) A.Add(n * 6 + 10, 16);
                }
                IList tracks = ListOf(G(o, "Tracks"));
                long lim = nt < 0 ? Math.Max(0, tracks.Count + nt) : Math.Min(nt, tracks.Count);
                for (int i = 0; i < lim; i++)
                {
                    GdStruct t = (GdStruct)tracks[i];
                    long nw = Num.ToLong(t["NumWarpers"]);
                    if (nw != 0) A.Add(0x50 * nw, 8);
                    long na = Num.ToLong(t["NumAnims"]);
                    if (na != 0)
                    {
                        A.Add(8 * na, 16);
                        for (long k = 0; k < na; k++) A.Add(0x28, 8);
                    }
                }
            };
            m["StateFlowNodeControllerAsset"] = (o, A) =>
            {
                A.Add(152, 8);
                A.Opt(8 * Len(o, "Transitions"), 8);
                A.Opt(0x28 * (Len(o, "EntryConditionsRequiredTrue") + Len(o, "EntryConditionsRequiredFalse")), 8);
                A.Opt(8 * Len(o, "AllTransitionConditions"), 8);
                A.Opt(8 * Len(o, "AllBranchTypes"), 8);
                IList ft = ListOf(G(o, "FlattenedTransitions"));
                if (ft.Count != 0)
                {
                    A.Add(0x30 * ft.Count, 8);
                    foreach (object e in ft)
                    {
                        GdStruct es = e as GdStruct;
                        A.Opt(2 * (Len(es, "TrueConditionIndices") & 0xFF), 2);
                        A.Opt(2 * (Len(es, "FalseConditionIndices") & 0xFF), 2);
                    }
                }
                long n = Len(o, "ActiveSignals");
                if (n != 0) { A.Add(8 * n, 8); A.Add(8 * n, 8); A.Add(8 * n, 8); }
            };
            m["BlendSpace"] = (o, A) =>
            {
                A.Add(0xC0, 16);
                GdStruct vd = G(o, "VoronoiDiagram") as GdStruct;
                if (vd == null) return;
                A.Opt(32 * Len(vd, "Items"), 16);
                A.Opt(128 * Len(vd, "Triangles"), 16);
                A.Opt(0x50 * Len(vd, "Edges"), 16);
                long cols = Num.ToLong(vd["NumColumns"]), rows = Num.ToLong(vd["NumRows"]);
                if (cols != 0 && rows != 0) A.Add(16 * cols * rows, 16);
                foreach (object g in ListOf(vd["GridItems"])) A.Opt(4 * Len(g as GdStruct, "TriangleIndices"), 4);
            };
            m["RawAnimationAsset"] = (o, A) =>
            {
                A.Add(144, 16);
                A.Opt(2 * Len(o, "KeyTimes"), 2);
                A.Opt(2 * Len(o, "MappingIndices"), 16);
                A.Opt(2 * Len(o, "ChannelIndices"), 16);
                A.Opt(4 * Len(o, "Data"), 16);
                A.Opt(4 * Len(o, "ConstData"), 16);
            };
            m["RigAsset"] = (o, A) =>
            {
                A.Add(0x120, 8);
                A.Opt(8 * Len(o, "DofSetLists"), 8);
                A.Opt(8 * Len(o, "FeatureDofSets"), 8);
                A.Opt(8 * Len(o, "RigDofSets"), 8);
                A.Add(2 * Len(o, "DofIds"), 2);
                A.Add(2 * Len(o, "RigDofSets"), 2);
                object nm = o["__name"];
                A.Opt(Truthy(nm) ? StrSz(nm) : 0, 1);
                long ni = Len(o, "DefaultIntValues"), nf = Len(o, "DefaultFloatValues");
                long n3 = Len(o, "DefaultVector3Values"), n4 = Len(o, "DefaultVector4Values");
                if (ni != 0 || nf != 0 || n3 != 0 || n4 != 0)
                {
                    A.Add(0x88, 8);
                    A.Add(22 * (n3 + n4) + 10 * (ni + nf), 16);
                }
            };
            m["PhysicsFeatureAsset"] = (o, A) =>
            {
                A.Add(192, 8);
                Lists(o, A, "JointToPartMap", 4, 4, "PartToJointMap", 4, 4, "AnimatedAggregateMap", 12, 16,
                      "UpdateAnimationBoneRotation", 1, 1, "UpdateAnimationBoneTranslation", 1, 1, "SimplifiedRegions", 0x18, 16);
                foreach (object r in ListOf(G(o, "SimplifiedRegions")))
                {
                    long n = 4 * Len(r as GdStruct, "JointInfluences");
                    if (n != 0) A.Add(n, n < 4 ? 2 : n < 8 ? 4 : n < 16 ? 8 : 16);
                }
            };
            m["RigOpListAsset"] = (o, A) =>
            {
                A.Add(56, 8);
                IList ops = ListOf(G(o, "DataRigOps"));
                if (ops.Count == 0) return;
                A.Add(0x38 * ops.Count, 8);
                JObject reg = table.Obj("__rigop_params__");
                foreach (object opo in ops)
                {
                    GdStruct op = (GdStruct)opo;
                    List<object> names = reg[RigopHash(op["DataRigOpType"] ?? "").ToString("x8")] as List<object>;
                    if (names == null) continue;
                    if (names.Count != 0) A.Add(0x30 * names.Count, 8);
                    foreach (object po in ListOf(op["DataParam"]))
                    {
                        GdStruct p = (GdStruct)po;
                        string pn = p["DataParameterName"] as string;
                        if (pn != null && names.Contains(pn))
                        {
                            if (!(Truthy(p["DataDSLKey"]) || Truthy(p["DataDSKey"]) || Truthy(p["DataValueKey"])))
                                A.Opt(Truthy(p["DataName"]) ? StrSz(p["DataName"]) : 0, 1);
                        }
                    }
                }
            };
            m["SceneOpMatrixAsset"] = (o, A) =>
            {
                A.Add(104, 8);
                long s_ = Len(o, "SceneOpAssets"), a_ = Len(o, "AnimatableAssets");
                if (s_ != 0)
                {
                    A.Add(0x30 * s_, 8);
                    if (a_ != 0) for (long i = 0; i < s_; i++) A.Add(a_, 16);
                }
                A.Opt(8 * a_, 8);
            };
            m["GKBridgeSequenceControllerAsset"] = (o, A) =>
            {
                A.Add(272, 16);
                A.Opt(Len(o, "SelectedSequence"), 1);
                object dg = G(o, "Diagonostics");
                A.Opt(Truthy(dg) ? StrSz(dg) : 0, 1);
                GdStruct ql = G(o, "QueryMapAssetList") as GdStruct;
                IList items = ql != null ? ListOf(ql["AssetList"]) : new object[0];
                if (items.Count != 0)
                {
                    A.Add(0xC0 * items.Count, 16);
                    foreach (object ito in items)
                    {
                        GdStruct it = ito as GdStruct;
                        foreach (string fld in new[] { "HoldBallExtends", "LeftHandPositions", "RightHandPositions" })
                        {
                            long n = 16 * Len(it, fld);
                            if (n != 0) A.Add(n, SizedAlign(n));
                        }
                        A.Opt(32 * Len(it, "TouchInnerSpheroidList"), 16);
                    }
                }
                A.Opt(8 * Len(o, "TouchTag"), 8);
            };
            m["DataInterfaceAsset"] = (o, A) =>
            {
                A.Add(32, 8);
                JObject di = DataInterface(o);
                if (di != null && di.Arr("members").Count != 0) A.Add(di.Long("size"), 8);
            };
            m["ExpressionAsset"] = (o, A) =>
            {
                A.Add(AlignUp(Len(o, "ExpressionKernel"), 16) + 0x50 + Len(o, "DofNames") + 4 * Len(o, "DofMapping"), 16);
            };
            m["ContextDatabaseAsset"] = (o, A) =>
            {
                A.Add(56, 8);
                A.Add(CdbSize(o), 16);
                CdbTail(o, A);
            };
            m["ContinuousContactAsset"] = (o, A) =>
            {
                A.Add(64, 8);
                GdObj cdb = null;
                foreach (GdObj x in o.Chain()) if (x.Type.Name == "ContextDatabaseAsset") { cdb = x; break; }
                if (cdb != null)
                {
                    A.Add(CdbSize(cdb), 16);
                    CdbTail(cdb, A);
                }
                long nq = Len(o, "Quads") >> 5;
                if (nq != 0)
                {
                    long cells = 1;
                    foreach (string ax in new[] { "InAngles", "OutAngles", "MoveAngles", "Speeds" }) cells *= Math.Min(16, Len(o, ax));
                    A.Add((cells + (nq + 6) * 8) * 4, 16);
                }
            };
            m["PriorityDatabaseAsset"] = (o, A) =>
            {
                A.Add(64, 8);
                GdStruct cdb = G(o, "ContextDB") as GdStruct;
                if (cdb == null) return;
                long nl = Len(o, "Layers");
                long p10 = LenOf(cdb["EntryAssets"]);
                A.Add(AlignUp(nl * 4, 16) + 0x20 + CdbSize(cdb) + (p10 + 15) / 16 * nl * 16, 16);
                CdbTail(cdb, A);
            };
            m["RigamateAsset"] = (o, A) =>
            {
                A.Add(392, 8);
                long ec = GL(o, "EntryCount"), ext = GL(o, "ExternalDataSize");
                if (ec != 0) A.Add(16 * ec, 16);
                if (ext != 0) A.Add(16 * ext, 16);
                long vj = Len(o, "VirtualJointList");
                A.Add(0x30 * vj, 16);
                A.Add(4 * vj, 16);
                GdStruct st = G(o, "StringTable") as GdStruct;
                if (st != null)
                {
                    long n = 5 * LenOf(st["Strings"]) + Num.ToLong(st["TotalLength"]);
                    if (n != 0) A.Add(n, 16);
                }
                A.Opt(8 * Len(o, "DofValueList"), 16);
            };
            return m;
        }
    }
}
