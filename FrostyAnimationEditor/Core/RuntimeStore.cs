using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace FrostyAnimationEditor.Core
{
    /// <summary>
    /// Per-block runtime data of the original file (resolved key references, linear allocations, raw key
    /// references for the reverse index) plus the loader profile of the whole file.  Built once over all
    /// blocks and written to the cache file, so later sessions skip the full decode.
    /// </summary>
    public sealed class RuntimeStore
    {
        public const string Magic = "FAECACHE";
        public const int FormatVersion = 3;

        public readonly int N;
        public RuntimeProfile Base;

        // flattened data of all blocks (null until built or read)
        private int[] refStart;
        private ulong[] refKey;
        private byte[] refTimes;
        private int[] allocStart;
        private long[] allocSize;
        private byte[] allocAlign;
        private bool[] modelled;
        private int[] keyStart;
        private ulong[] keyVals;

        private readonly Dictionary<int, BlockRuntime> extra = new Dictionary<int, BlockRuntime>();

        public RuntimeStore(int n) { N = n; }

        public bool IsComplete { get { return refStart != null; } }
        public bool HasKeyRefs { get { return keyStart != null; } }

        public BlockRuntime Get(int i)
        {
            BlockRuntime r;
            if (extra.TryGetValue(i, out r)) return r;
            if (refStart == null) return null;
            r = new BlockRuntime();
            int a = refStart[i], b = refStart[i + 1];
            r.Refs = new List<ResolvedRef>(b - a);
            for (int k = a; k < b; k++) r.Refs.Add(new ResolvedRef(refKey[k], refTimes[k]));
            a = allocStart[i]; b = allocStart[i + 1];
            r.Allocs = new List<Alloc>(b - a);
            for (int k = a; k < b; k++) r.Allocs.Add(new Alloc(allocSize[k], allocAlign[k]));
            r.Modelled = modelled[i];
            return r;
        }

        public void Set(int i, BlockRuntime r) { extra[i] = r; }

        public IEnumerable<ulong> KeyRefs(int i)
        {
            for (int k = keyStart[i]; k < keyStart[i + 1]; k++) yield return keyVals[k];
        }

        /// <summary>decode every block once and collect its runtime data and key references</summary>
        public static RuntimeStore Build(AntDb db, Action<string, double> progress)
        {
            int n = db.N0;
            RuntimeStore s = new RuntimeStore(n);
            List<ulong> rk = new List<ulong>(1500000);
            List<byte> rt = new List<byte>(1500000);
            List<long> asz = new List<long>(1100000);
            List<byte> aal = new List<byte>(1100000);
            List<ulong> kv = new List<ulong>(2500000);
            s.refStart = new int[n + 1];
            s.allocStart = new int[n + 1];
            s.keyStart = new int[n + 1];
            s.modelled = new bool[n];
            List<KeyRef> refs;
            for (int i = 0; i < n; i++)
            {
                GdObj o = db.Original(i);
                refs = Refs.From(o);
                List<ResolvedRef> rr = Runtime.ResolvedRefs(o, refs);
                s.refStart[i] = rk.Count;
                foreach (ResolvedRef r in rr)
                {
                    rk.Add(r.Key);
                    rt.Add(TimesByte(r.Times));
                }
                List<Alloc> al = AllocModel.Allocations(o);
                s.modelled[i] = al != null;
                s.allocStart[i] = asz.Count;
                if (al != null)
                    foreach (Alloc a in al)
                    {
                        asz.Add(a.Size);
                        aal.Add((byte)a.Align);
                    }
                s.keyStart[i] = kv.Count;
                foreach (KeyRef r in refs) if (r.Key != 0) kv.Add(r.Key);
                if (progress != null && i % 10000 == 0) progress("decoding blocks " + i + " / " + n, 0.9 * i / n);
            }
            s.refStart[n] = rk.Count;
            s.allocStart[n] = asz.Count;
            s.keyStart[n] = kv.Count;
            s.refKey = rk.ToArray();
            s.refTimes = rt.ToArray();
            s.allocSize = asz.ToArray();
            s.allocAlign = aal.ToArray();
            s.keyVals = kv.ToArray();
            db.Store = s;
            if (progress != null) progress("measuring the loader profile", 0.92);
            s.Base = db.Profile(null, true);
            return s;
        }

        /// <summary>
        /// The store of a container the session just wrote (ids = the blocks in output order), without decoding
        /// it again: unchanged blocks take their data from this session's store, edited and new blocks are
        /// measured from their objects; profile = the loader profile of the output (null: measured here).
        /// </summary>
        public static RuntimeStore Derive(AntDb db, List<int> ids, RuntimeProfile profile)
        {
            int n = ids.Count;
            RuntimeStore s = new RuntimeStore(n);
            List<ulong> rk = new List<ulong>(1500000);
            List<byte> rt = new List<byte>(1500000);
            List<long> asz = new List<long>(1100000);
            List<byte> aal = new List<byte>(1100000);
            List<ulong> kv = new List<ulong>(2500000);
            s.refStart = new int[n + 1];
            s.allocStart = new int[n + 1];
            s.keyStart = new int[n + 1];
            s.modelled = new bool[n];
            RuntimeStore src = db.Store != null && db.Store.HasKeyRefs ? db.Store : null;
            for (int k = 0; k < n; k++)
            {
                int i = ids[k];
                BlockRuntime r = db.RuntimeOf(i);
                s.refStart[k] = rk.Count;
                foreach (ResolvedRef x in r.Refs)
                {
                    rk.Add(x.Key);
                    rt.Add(TimesByte(x.Times));
                }
                s.modelled[k] = r.Modelled;
                s.allocStart[k] = asz.Count;
                if (r.Modelled)
                    foreach (Alloc a in r.Allocs)
                    {
                        asz.Add(a.Size);
                        aal.Add((byte)a.Align);
                    }
                s.keyStart[k] = kv.Count;
                if (src != null && !db.IsLoaded(i)) kv.AddRange(src.KeyRefs(i));
                else foreach (KeyRef x in Refs.From(db.Obj(i))) if (x.Key != 0) kv.Add(x.Key);
            }
            s.refStart[n] = rk.Count;
            s.allocStart[n] = asz.Count;
            s.keyStart[n] = kv.Count;
            s.refKey = rk.ToArray();
            s.refTimes = rt.ToArray();
            s.allocSize = asz.ToArray();
            s.allocAlign = aal.ToArray();
            s.keyVals = kv.ToArray();
            s.Base = profile ?? db.Profile(ids);
            return s;
        }

        private static byte TimesByte(int t)
        {
            if (t < 0 || t > 255) throw new GdException("a key is resolved " + t + " times by one object; the cache format keeps up to 255");
            return (byte)t;
        }

        /// <summary>null when both stores hold the same data, else what differs first</summary>
        public string Difference(RuntimeStore o)
        {
            if (N != o.N) return "block count " + N + " vs " + o.N;
            if (Base.WaiterPeak != o.Base.WaiterPeak || Base.LinearBytes != o.Base.LinearBytes || Base.PostLoadBytes != o.Base.PostLoadBytes)
                return "profile " + Base.WaiterPeak + "/" + Base.LinearBytes + "/" + Base.PostLoadBytes + " vs " + o.Base.WaiterPeak + "/" + o.Base.LinearBytes + "/" + o.Base.PostLoadBytes;
            if (string.Join(",", Base.Unmodelled) != string.Join(",", o.Base.Unmodelled)) return "unmodelled classes differ";
            Func<string, Array, Array, string> cmp = (name, a, b) =>
            {
                if (a.Length != b.Length) return name + " length " + a.Length + " vs " + b.Length;
                for (int i = 0; i < a.Length; i++) if (!Equals(a.GetValue(i), b.GetValue(i))) return name + "[" + i + "] " + a.GetValue(i) + " vs " + b.GetValue(i);
                return null;
            };
            return cmp("refStart", refStart, o.refStart) ?? cmp("refKey", refKey, o.refKey) ?? cmp("refTimes", refTimes, o.refTimes) ??
                   cmp("allocStart", allocStart, o.allocStart) ?? cmp("allocSize", allocSize, o.allocSize) ?? cmp("allocAlign", allocAlign, o.allocAlign) ??
                   cmp("modelled", modelled, o.modelled) ?? cmp("keyStart", keyStart, o.keyStart) ?? cmp("keyVals", keyVals, o.keyVals);
        }

        // ------------------------------------------------------------------ file
        public void Write(Stream outStream, long sourceLength, ulong sourceHash)
        {
            BinaryWriter hw = new BinaryWriter(outStream, Encoding.UTF8);
            hw.Write(Encoding.ASCII.GetBytes(Magic));
            hw.Write(FormatVersion);
            hw.Write(sourceLength);
            hw.Write(sourceHash);
            hw.Write(N);
            hw.Flush();
            using (DeflateStream z = new DeflateStream(outStream, CompressionLevel.Fastest, true))
            using (BufferedStream bs = new BufferedStream(z, 1 << 20))
            using (BinaryWriter w = new BinaryWriter(bs, Encoding.UTF8))
            {
                w.Write(Base.WaiterPeak);
                w.Write(Base.LinearBytes);
                w.Write(Base.PostLoadBytes);
                w.Write(Base.Unmodelled.Count);
                foreach (string u in Base.Unmodelled) w.Write(u);
                WriteInts(w, refStart);
                w.Write(refKey.Length);
                foreach (ulong k in refKey) w.Write(k);
                w.Write(refTimes.Length);
                w.Write(refTimes);
                WriteInts(w, allocStart);
                w.Write(allocSize.Length);
                foreach (long a in allocSize) w.Write(a);
                w.Write(allocAlign.Length);
                w.Write(allocAlign);
                w.Write(modelled.Length);
                foreach (bool b in modelled) w.Write(b);
                WriteInts(w, keyStart);
                w.Write(keyVals.Length);
                foreach (ulong k in keyVals) w.Write(k);
                w.Write(0x454E44u);
            }
        }

        private static void WriteInts(BinaryWriter w, int[] a)
        {
            w.Write(a.Length);
            foreach (int x in a) w.Write(x);
        }

        private static int[] ReadInts(BinaryReader r)
        {
            int n = r.ReadInt32();
            int[] a = new int[n];
            for (int i = 0; i < n; i++) a[i] = r.ReadInt32();
            return a;
        }

        /// <summary>null when the file is not a cache of this source</summary>
        public static RuntimeStore Read(Stream inStream, int n, long sourceLength, ulong sourceHash)
        {
            BinaryReader hr = new BinaryReader(inStream, Encoding.UTF8);
            byte[] magic = hr.ReadBytes(8);
            if (Encoding.ASCII.GetString(magic) != Magic) return null;
            if (hr.ReadInt32() != FormatVersion) return null;
            if (hr.ReadInt64() != sourceLength) return null;
            if (hr.ReadUInt64() != sourceHash) return null;
            if (hr.ReadInt32() != n) return null;
            RuntimeStore s = new RuntimeStore(n);
            using (DeflateStream z = new DeflateStream(inStream, CompressionMode.Decompress, true))
            using (BufferedStream bs = new BufferedStream(z, 1 << 20))
            using (BinaryReader r = new BinaryReader(bs, Encoding.UTF8))
            {
                RuntimeProfile p = new RuntimeProfile();
                p.WaiterPeak = r.ReadInt64();
                p.LinearBytes = r.ReadInt64();
                p.PostLoadBytes = r.ReadInt64();
                int nu = r.ReadInt32();
                for (int i = 0; i < nu; i++) p.Unmodelled.Add(r.ReadString());
                s.Base = p;
                s.refStart = ReadInts(r);
                int nk = r.ReadInt32();
                s.refKey = new ulong[nk];
                for (int i = 0; i < nk; i++) s.refKey[i] = r.ReadUInt64();
                s.refTimes = r.ReadBytes(r.ReadInt32());
                s.allocStart = ReadInts(r);
                int na = r.ReadInt32();
                s.allocSize = new long[na];
                for (int i = 0; i < na; i++) s.allocSize[i] = r.ReadInt64();
                s.allocAlign = r.ReadBytes(r.ReadInt32());
                int nm = r.ReadInt32();
                s.modelled = new bool[nm];
                for (int i = 0; i < nm; i++) s.modelled[i] = r.ReadBoolean();
                s.keyStart = ReadInts(r);
                int nv = r.ReadInt32();
                s.keyVals = new ulong[nv];
                for (int i = 0; i < nv; i++) s.keyVals[i] = r.ReadUInt64();
                if (r.ReadUInt32() != 0x454E44u) return null;
            }
            if (s.refStart.Length != n + 1 || s.allocStart.Length != n + 1 || s.keyStart.Length != n + 1 || s.modelled.Length != n) return null;
            return s;
        }

        /// <summary>fast 64-bit content hash (identifies the data a cache was built from)</summary>
        public static ulong Hash(byte[] b, long off, long len)
        {
            ulong h = 0x9E3779B97F4A7C15UL ^ (ulong)len;
            long i = off, end = off + len;
            for (; i + 8 <= end; i += 8)
            {
                ulong v = BitConverter.ToUInt64(b, (int)i);
                h ^= v * 0xC2B2AE3D27D4EB4FUL;
                h = ((h << 31) | (h >> 33)) * 0x9E3779B185EBCA87UL;
            }
            for (; i < end; i++)
            {
                h ^= b[i] * 0x165667B19E3779F9UL;
                h = ((h << 23) | (h >> 41)) * 0x9E3779B185EBCA87UL;
            }
            h ^= h >> 33;
            h *= 0xFF51AFD7ED558CCDUL;
            h ^= h >> 33;
            return h;
        }
    }
}
