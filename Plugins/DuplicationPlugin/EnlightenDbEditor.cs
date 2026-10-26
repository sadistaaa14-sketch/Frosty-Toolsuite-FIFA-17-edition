using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace DuplicationPlugin
{
    /// <summary>
    /// Edits a FIFA 17 EnlightenDatabase res (the stadium's Lighting/&lt;folder&gt; resource: dynamic Enlighten systems,
    /// lightmap chart records, probe sets). Layout proven on generic_158 (notes/findings_baked_lighting.md):
    /// <code>
    /// 0x00 u32 atlas width, u32 atlas height, ... 0x18 u64 instance count, 0x20 u64 instance table, 0x28 u64 uv records,
    /// 0x30 u32 system count, 0x34 u64 systems (0x70 each), 0x3c u32 probe set count, 0x40 u64 probe sets
    /// instance: {u32 key (FNV-1 of root instance GUID + placement), u32 1, u64 uv record, 16 x 0}, sorted by key
    /// uv record: {f32 sx, 0, 0, f32 sy, f32 tx, f32 ty, 0, 0}: atlas uv = RadiosityTexCoord * (sx, sy) + (tx, ty)
    /// system: f32[4] box min, f32[4] box max, u32 origin x, origin y, 0, material count,
    ///         u64 material table, u64 own materials, u64 RadSystemCore, 0, u64 InputWorkspace, u64 GEVS, u64 ClusterAlbedo, 0
    /// </code>
    /// Every pointer field is an offset listed in the relocation table (u32 offsets at the end of the data, size = res
    /// meta u32 #2); FIFA17.exe adds the data address to each (FUN_1453f3920). New objects are appended, the systems
    /// array and the material id table are rebuilt (old entries kept in order), the instance table is rebuilt when
    /// keys are added. New systems copy FIFA's output-only system layout (generic_158 system 13: input workspace without
    /// clusters, one material, visibility data without clusters) around the package's GESG radiosity block.
    /// </summary>
    public sealed class EnlightenDbEditor
    {
        public sealed class SystemInfo
        {
            public int Index; public Guid Guid; public int X, Y, W, H;
        }

        public sealed class InstanceInfo
        {
            public uint Key; public long UvOffset; public float Sx, Sy, Tx, Ty;
        }

        public sealed class NewSystem
        {
            public byte[] Guid;              // 16 bytes, .NET order
            public int X, Y, Width, Height;  // atlas origin and output size (texels)
            public float[] BoxMin, BoxMax;   // 3 floats each
            public byte[] Gesg;
            public int Budget, Workspace, Pixels, Segments;
        }

        private readonly byte[] _d;          // data without the relocation table
        private readonly List<uint> _reloc;
        public readonly int AtlasWidth, AtlasHeight;
        public readonly List<SystemInfo> Systems = new List<SystemInfo>();
        public readonly List<InstanceInfo> Instances = new List<InstanceInfo>();
        private readonly List<NewSystem> _newSystems = new List<NewSystem>();
        private readonly Dictionary<uint, float[]> _uvUpdates = new Dictionary<uint, float[]>();
        private readonly HashSet<int> _removed = new HashSet<int>();
        private readonly HashSet<uint> _ignoredKeys = new HashSet<uint>();
        private bool[] _base;   // systems kept + chart records not ignored
        private bool[] _used;   // _base + rectangles reserved by Place

        /// <summary>Drops the systems with these GUIDs (a previous import of the same package); returns how many. Their objects stay as dead data.</summary>
        public int RemoveSystems(IEnumerable<Guid> guids)
        {
            var set = new HashSet<Guid>(guids);
            int n = 0;
            foreach (var s in Systems) if (set.Contains(s.Guid) && _removed.Add(s.Index)) n++;
            _base = _used = null;
            return n;
        }

        /// <summary>Drops these systems (by index) from the database that Build writes; <see cref="RestoreSystemIndices"/> takes them back.</summary>
        public void RemoveSystemIndices(IEnumerable<int> indices) { foreach (int i in indices) _removed.Add(i); _base = _used = null; }

        public void RestoreSystemIndices(IEnumerable<int> indices) { foreach (int i in indices) _removed.Remove(i); _base = _used = null; }

        public bool IsRemoved(int index) { return _removed.Contains(index); }

        public int RemovedCount { get { return _removed.Count; } }

        /// <summary>The chart rectangle of this key does not count as used (the key gets a new one, or its mesh is gone).</summary>
        public void IgnoreInstance(uint key) { _ignoredKeys.Add(key); _base = _used = null; }

        /// <summary>Forgets the rectangles reserved by <see cref="Place"/>.</summary>
        public void ClearReservations() { _used = null; }

        private List<int[]> _blocked;

        /// <summary>Marks a rectangle as used for every later placement (offline tests: an atlas as full as allianz_137's).</summary>
        public void BlockForTests(int x, int y, int w, int h)
        {
            if (_blocked == null) _blocked = new List<int[]>();
            _blocked.Add(new[] { x, y, w, h });
            _base = _used = null;
        }

        /// <summary>The chart rectangle of an instance in atlas texels (x0, y0, x1, y1; fractional).</summary>
        public void ChartRect(InstanceInfo i, out double x0, out double y0, out double x1, out double y1)
        {
            x0 = (double)i.Tx * AtlasWidth; y0 = (double)i.Ty * AtlasHeight;
            x1 = x0 + (double)i.Sx * AtlasWidth; y1 = y0 + (double)i.Sy * AtlasHeight;
        }

        /// <summary>
        /// The chart records whose rectangle overlaps the system's output rectangle (by more than a quarter texel: FIFA's
        /// small charts sit half a texel off their system's edge, generic_158's 8x8 ones at x.5).
        /// </summary>
        public List<InstanceInfo> RecordsIn(SystemInfo s)
        {
            List<InstanceInfo> l = new List<InstanceInfo>();
            foreach (InstanceInfo i in Instances)
            {
                double x0, y0, x1, y1;
                ChartRect(i, out x0, out y0, out x1, out y1);
                double ox = Math.Min(x1, s.X + s.W) - Math.Max(x0, s.X), oy = Math.Min(y1, s.Y + s.H) - Math.Max(y0, s.Y);
                if (ox > 0 && oy > 0 && ox * oy > 0.25) l.Add(i);
            }
            return l;
        }

        /// <summary>
        /// Systems of the database (not removed) that light only meshes in <paramref name="keys"/>: at least one chart
        /// record lies in the system's rectangle and every record there has one of these keys. A system with no record
        /// (FIFA's small output-only systems) is never listed.
        /// </summary>
        public List<int> SystemsServingOnly(ICollection<uint> keys)
        {
            List<int> l = new List<int>();
            foreach (SystemInfo s in Systems)
            {
                if (_removed.Contains(s.Index)) continue;
                List<InstanceInfo> recs = RecordsIn(s);
                if (recs.Count > 0 && recs.All(r => keys.Contains(r.Key))) l.Add(s.Index);
            }
            return l;
        }

        public EnlightenDbEditor(byte[] resData, uint relocBytes)
        {
            if (relocBytes % 4 != 0 || relocBytes > resData.Length) throw new InvalidDataException("bad relocation table size");
            int dataLen = resData.Length - (int)relocBytes;
            _d = new byte[dataLen];
            Buffer.BlockCopy(resData, 0, _d, 0, dataLen);
            _reloc = new List<uint>();
            for (int o = dataLen; o < resData.Length; o += 4) _reloc.Add(BitConverter.ToUInt32(resData, o));
            AtlasWidth = (int)U32(0); AtlasHeight = (int)U32(4);
            int nInst = (int)U64(0x18); long tab = (long)U64(0x20);
            for (int i = 0; i < nInst; i++)
            {
                long e = tab + 32L * i;
                long uv = (long)U64(e + 8);
                Instances.Add(new InstanceInfo { Key = U32(e), UvOffset = uv, Sx = F32(uv), Sy = F32(uv + 12), Tx = F32(uv + 16), Ty = F32(uv + 20) });
            }
            int nSys = (int)U32(0x30); long sys = (long)U64(0x34);
            for (int i = 0; i < nSys; i++)
            {
                long r = sys + 0x70L * i;
                long rsc = (long)U64(r + 0x40);
                var g = new byte[16]; Buffer.BlockCopy(_d, (int)rsc, g, 0, 16);
                Systems.Add(new SystemInfo { Index = i, Guid = new Guid(g), X = (int)U32(r + 0x20), Y = (int)U32(r + 0x24), W = (int)U32(rsc + 0x10), H = (int)U32(rsc + 0x14) });
            }
        }

        private uint U32(long o) { return BitConverter.ToUInt32(_d, (int)o); }
        private ulong U64(long o) { return BitConverter.ToUInt64(_d, (int)o); }
        private float F32(long o) { return BitConverter.ToSingle(_d, (int)o); }

        /// <summary>Texels of the atlas used by a system's output or an instance's chart rectangle (or reserved by Place).</summary>
        public bool[] Occupancy()
        {
            if (_used != null) return _used;
            if (_base == null)
            {
                _base = new bool[AtlasWidth * AtlasHeight];
                foreach (var s in Systems) if (!_removed.Contains(s.Index)) Mark(_base, s.X, s.Y, s.W, s.H);
                foreach (var i in Instances)
                {
                    if (_ignoredKeys.Contains(i.Key)) continue;
                    int x0 = (int)Math.Floor(i.Tx * AtlasWidth), y0 = (int)Math.Floor(i.Ty * AtlasHeight);
                    int x1 = (int)Math.Ceiling((i.Tx + i.Sx) * AtlasWidth), y1 = (int)Math.Ceiling((i.Ty + i.Sy) * AtlasHeight);
                    Mark(_base, x0, y0, x1 - x0, y1 - y0);
                }
                if (_blocked != null) foreach (int[] r in _blocked) Mark(_base, r[0], r[1], r[2], r[3]);
            }
            _used = (bool[])_base.Clone();
            return _used;
        }

        private void Mark(bool[] map, int x, int y, int w, int h)
        {
            for (int yy = Math.Max(0, y); yy < Math.Min(AtlasHeight, y + h); yy++)
                for (int xx = Math.Max(0, x); xx < Math.Min(AtlasWidth, x + w); xx++)
                    map[yy * AtlasWidth + xx] = true;
        }

        /// <summary>
        /// Finds a free place for a w x h rectangle (origin a multiple of 4, <paramref name="margin"/> free texels around it,
        /// FIFA packs its systems 4 apart: SystemPackPadding) and reserves it. Lowest row first, then leftmost.
        /// </summary>
        public bool Place(int w, int h, int margin, out int px, out int py)
        {
            var used = Occupancy();
            int W = AtlasWidth, H = AtlasHeight;
            // summed area table of the occupancy
            var sat = new int[(W + 1) * (H + 1)];
            for (int y = 0; y < H; y++)
            {
                int row = 0;
                for (int x = 0; x < W; x++)
                {
                    row += used[y * W + x] ? 1 : 0;
                    sat[(y + 1) * (W + 1) + x + 1] = sat[y * (W + 1) + x + 1] + row;
                }
            }
            for (int y = 0; y + h <= H; y += 4)
                for (int x = 0; x + w <= W; x += 4)
                {
                    int x0 = Math.Max(0, x - margin), y0 = Math.Max(0, y - margin);
                    int x1 = Math.Min(W, x + w + margin), y1 = Math.Min(H, y + h + margin);
                    int n = sat[y1 * (W + 1) + x1] - sat[y0 * (W + 1) + x1] - sat[y1 * (W + 1) + x0] + sat[y0 * (W + 1) + x0];
                    if (n == 0) { px = x; py = y; Mark(_used, x, y, w, h); return true; }
                }
            px = py = -1;
            return false;
        }

        /// <summary>Sets (or adds) the chart rectangle of an instance key.</summary>
        public void SetInstanceUv(uint key, float sx, float sy, float tx, float ty)
        {
            _uvUpdates[key] = new[] { sx, sy, tx, ty };
        }

        public void AddSystem(NewSystem s)
        {
            if (s.Guid == null || s.Guid.Length != 16) throw new ArgumentException("system GUID");
            if (s.X < 0 || s.Y < 0 || s.X + s.Width > AtlasWidth || s.Y + s.Height > AtlasHeight) throw new ArgumentException("system outside the atlas");
            if (BitConverter.ToUInt32(s.Gesg, 0) != 0x47534547 || BitConverter.ToInt32(s.Gesg, 4) != 37) throw new ArgumentException("not a GESG v37 block");
            if (BitConverter.ToUInt16(s.Gesg, 16) != s.Width || BitConverter.ToUInt16(s.Gesg, 18) != s.Height || BitConverter.ToInt32(s.Gesg, 24) != s.Pixels)
                throw new ArgumentException("GESG does not match the system size / pixel count");
            if (Systems.Any(x => x.Guid == new Guid(s.Guid) && !_removed.Contains(x.Index)) || _newSystems.Any(x => x.Guid.SequenceEqual(s.Guid))) throw new ArgumentException("system GUID already in the database");
            _newSystems.Add(s);
        }

        // --------------------------------------------------------------------------------------------- build

        private sealed class Out
        {
            public readonly MemoryStream Ms = new MemoryStream();
            public readonly List<uint> Reloc = new List<uint>();
            public long Pos { get { return Ms.Position; } }
            public void Align(int a) { while (Ms.Position % a != 0) Ms.WriteByte(0); }
            public void Bytes(byte[] b) { Ms.Write(b, 0, b.Length); }
            public void U32(uint v) { Bytes(BitConverter.GetBytes(v)); }
            public void U16(ushort v) { Bytes(BitConverter.GetBytes(v)); }
            public void F32(float v) { Bytes(BitConverter.GetBytes(v)); }
            public void U64(ulong v) { Bytes(BitConverter.GetBytes(v)); }
            public void Ptr(long target) { if (target != 0) Reloc.Add((uint)Ms.Position); U64((ulong)target); }
            public void PatchU64(long at, ulong v) { var b = BitConverter.GetBytes(v); var buf = Ms.GetBuffer(); Buffer.BlockCopy(b, 0, buf, (int)at, 8); }
            public void PatchU32(long at, uint v) { var b = BitConverter.GetBytes(v); var buf = Ms.GetBuffer(); Buffer.BlockCopy(b, 0, buf, (int)at, 4); }
        }

        /// <summary>The edited res data (data + relocation table) and the new relocation table size for the res meta.</summary>
        public byte[] Build(out uint relocBytes)
        {
            var o = new Out();
            o.Bytes(_d);
            int nOld = Systems.Count;
            long sysOld = (long)U64(0x34);
            var kept = Enumerable.Range(0, nOld).Where(i => !_removed.Contains(i)).ToList();
            long instOld = (long)U64(0x20);
            int nInstOld = (int)U64(0x18);
            // dead regions after the rebuild: their pointer fields leave the relocation table
            long deadSys0 = sysOld, deadSys1 = sysOld + 0x70L * nOld;
            var newKeys = _uvUpdates.Keys.Where(k => !Instances.Any(i => i.Key == k)).ToList();
            bool rebuildInstances = newKeys.Count > 0;
            long deadInst0 = rebuildInstances ? instOld : 0, deadInst1 = rebuildInstances ? instOld + 32L * nInstOld : 0;
            foreach (uint r in _reloc)
            {
                if (r >= deadSys0 && r < deadSys1) continue;
                if (rebuildInstances && r >= deadInst0 && r < deadInst1) continue;
                o.Reloc.Add(r);
            }

            // ---- uv records of updated keys (new records; existing table entries are re-pointed)
            var uvAt = new Dictionary<uint, long>();
            foreach (var kv in _uvUpdates.OrderBy(k => k.Key))
            {
                o.Align(16);
                uvAt[kv.Key] = o.Pos;
                float[] u = kv.Value;
                o.F32(u[0]); o.F32(0); o.F32(0); o.F32(u[1]); o.F32(u[2]); o.F32(u[3]); o.F32(0); o.F32(0);
            }
            if (!rebuildInstances)
            {
                for (int i = 0; i < nInstOld; i++)
                {
                    long e = instOld + 32L * i;
                    long at;
                    if (uvAt.TryGetValue(U32(e), out at)) o.PatchU64(e + 8, (ulong)at);
                }
            }
            else
            {
                var entries = new List<KeyValuePair<uint, long>>();
                for (int i = 0; i < nInstOld; i++)
                {
                    long e = instOld + 32L * i; uint k = U32(e);
                    long at;
                    entries.Add(new KeyValuePair<uint, long>(k, uvAt.TryGetValue(k, out at) ? at : (long)U64(e + 8)));
                }
                foreach (uint k in newKeys) entries.Add(new KeyValuePair<uint, long>(k, uvAt[k]));
                entries.Sort((a, b) => a.Key.CompareTo(b.Key));
                o.Align(16);
                long tab = o.Pos;
                foreach (var kv in entries) { o.U32(kv.Key); o.U32(1); o.Ptr(kv.Value); o.U64(0); o.U64(0); }
                o.PatchU64(0x18, (ulong)entries.Count);
                o.PatchU64(0x20, (ulong)tab);
            }

            // ---- material id table: the kept systems' lists in system order + one entry per new system
            o.Align(16);
            long matNew = o.Pos;
            var keptMatAt = new Dictionary<int, long>();
            foreach (int i in kept)
            {
                long r = sysOld + 0x70L * i;
                long p1 = (long)U64(r + 0x38);
                int n = (int)U32(r + 0x2c);
                keptMatAt[i] = o.Pos;
                if (p1 != 0 && n > 0) { var b = new byte[16 * n]; Buffer.BlockCopy(_d, (int)p1, b, 0, b.Length); o.Bytes(b); }
            }
            var newMatAt = new List<long>();
            foreach (var s in _newSystems) { newMatAt.Add(o.Pos); o.Bytes(Template.Material); }

            // ---- objects of the new systems
            var rsc = new List<long>(); var iw = new List<long>(); var gevs = new List<long>(); var ca = new List<long>();
            foreach (var s in _newSystems)
            {
                o.Align(16); long gesgAt = o.Pos; o.Bytes(s.Gesg);
                o.Align(16); long gerdAt = o.Pos; byte[] gerd = Gerd(BitConverter.ToInt32(s.Gesg, 8)); o.Bytes(gerd);
                o.Align(16); long geiwAt = o.Pos; o.Bytes(Template.Geiw);
                o.Align(16); long b16At = o.Pos; o.Bytes(Template.Iw16);
                o.Align(16); long b17At = o.Pos; o.Bytes(Template.Iw17);
                o.Align(16); long ca6At = o.Pos; o.Bytes(Template.Ca6);
                o.Align(16); long ca8At = o.Pos; o.Bytes(Template.Ca8);
                o.Align(16); long gevsAt = o.Pos;
                var gv = (byte[])Template.Gevs.Clone(); Buffer.BlockCopy(s.Guid, 0, gv, 8, 16); o.Bytes(gv);
                // RadSystemCore: GUID, RadSystemMetaData (w, h, budget, workspace, pixels, env resolution 16, directional 1, 0), 3 data blocks
                o.Align(16); rsc.Add(o.Pos);
                o.Bytes(s.Guid);
                o.U32((uint)s.Width); o.U32((uint)s.Height); o.U32((uint)s.Budget); o.U32((uint)s.Workspace);
                o.U32((uint)s.Pixels); o.U32(16); o.U32(1); o.U32(0);
                Block(o, gesgAt, s.Gesg.Length, 1, 1);
                Block(o, gerdAt, gerd.Length, 0x1b, 1);
                Block(o, 0, 0, 0, 0);
                // InputWorkspace: GUID, precomp (GEIW), projected points (none), 0x16, 0x17
                o.Align(16); iw.Add(o.Pos);
                o.Bytes(s.Guid);
                Block(o, geiwAt, Template.Geiw.Length, 4, 1);
                Block(o, 0, 0, 0, 0);
                Block(o, b16At, Template.Iw16.Length, 0x16, 1);
                Block(o, b17At, Template.Iw17.Length, 0x17, 1);
                // ClusterAlbedoWorkspaceMaterialData: GUID, cluster materials, material GUIDs
                o.Align(16); ca.Add(o.Pos);
                o.Bytes(s.Guid);
                Block(o, ca6At, Template.Ca6.Length, 6, 0);
                Block(o, ca8At, Template.Ca8.Length, 8, 0);
                gevs.Add(gevsAt);
            }

            // ---- systems array: kept old records (material pointers moved to the new table) + new records
            o.Align(16);
            long sysNew = o.Pos;
            foreach (int i in kept)
            {
                long r = sysOld + 0x70L * i;
                var rec = new byte[0x30]; Buffer.BlockCopy(_d, (int)r, rec, 0, 0x30); o.Bytes(rec);
                long p0 = (long)U64(r + 0x30), p1 = (long)U64(r + 0x38);
                o.Ptr(p0 == 0 ? 0 : matNew);
                o.Ptr(p1 == 0 ? 0 : keptMatAt[i]);
                for (int k = 2; k < 8; k++) o.Ptr((long)U64(r + 0x30 + 8 * k));
            }
            for (int i = 0; i < _newSystems.Count; i++)
            {
                var s = _newSystems[i];
                o.F32(s.BoxMin[0]); o.F32(s.BoxMin[1]); o.F32(s.BoxMin[2]); o.F32(0);
                o.F32(s.BoxMax[0]); o.F32(s.BoxMax[1]); o.F32(s.BoxMax[2]); o.F32(0);
                o.U32((uint)s.X); o.U32((uint)s.Y); o.U32(0); o.U32(1);
                o.Ptr(matNew); o.Ptr(newMatAt[i]); o.Ptr(rsc[i]); o.Ptr(0); o.Ptr(iw[i]); o.Ptr(gevs[i]); o.Ptr(ca[i]); o.Ptr(0);
            }
            o.PatchU32(0x30, (uint)(kept.Count + _newSystems.Count));
            o.PatchU64(0x34, (ulong)sysNew);

            // ---- relocation table
            o.Align(16);
            long dataEnd = o.Pos;
            foreach (uint r in o.Reloc) o.U32(r);
            relocBytes = (uint)(o.Pos - dataEnd);
            return o.Ms.ToArray();
        }

        /// <summary>
        /// The res meta for a rebuilt database: FIFA's Enlighten database res metas are {u32 version (11 for
        /// EnlightenDatabase, the factory's 0x000b000b), u32 data size, u32 relocation table size, u32 4} with data size +
        /// relocation size = the res payload (generic_158 / test_499: 61,626,352 + 4,568 = 61,630,920; flux night:
        /// 195,520 + 8). Both sizes change when the database grows; version and the last field are kept.
        /// </summary>
        public static byte[] ResMeta(byte[] oldMeta, int payloadLength, uint relocBytes)
        {
            if (oldMeta == null || oldMeta.Length < 12) throw new InvalidDataException("the database res meta has " + (oldMeta == null ? 0 : oldMeta.Length) + " bytes, expected 16");
            if (relocBytes > payloadLength) throw new ArgumentException("relocation table larger than the res");
            byte[] meta = (byte[])oldMeta.Clone();
            Array.Copy(BitConverter.GetBytes((uint)(payloadLength - relocBytes)), 0, meta, 4, 4);
            Array.Copy(BitConverter.GetBytes(relocBytes), 0, meta, 8, 4);
            return meta;
        }

        /// <summary>True when the meta's data and relocation sizes add up to the payload (false after r6 / r7 imports: their data size was stale).</summary>
        public static bool ResMetaMatches(byte[] meta, int payloadLength)
        {
            if (meta == null || meta.Length < 12) return false;
            return (long)BitConverter.ToUInt32(meta, 4) + BitConverter.ToUInt32(meta, 8) == payloadLength;
        }

        private static void Block(Out o, long ptr, int len, ushort type, ushort solver)
        {
            o.Ptr(ptr); o.U32((uint)len); o.U16(type); o.U16(solver);
        }

        /// <summary>ResamplingData of a system without clusters: "GERD", 2, 0, segments + 1, that many zeros, padded (FIFA's systems 13/15/23).</summary>
        public static byte[] Gerd(int segments)
        {
            int n = segments + 1;
            int len = ((16 + 4 * n + 15) & ~15) + 16;
            var b = new byte[len];
            Encoding.ASCII.GetBytes("GERD").CopyTo(b, 0);
            BitConverter.GetBytes(2).CopyTo(b, 4);
            BitConverter.GetBytes(n).CopyTo(b, 12);
            return b;
        }

        /// <summary>Data blocks of generic_158's output-only system 13 (no clusters) that a new system copies.</summary>
        private static class Template
        {
            public static readonly byte[] Geiw = Convert.FromBase64String("//9/f///f3///39/AAAAAP//f////3////9//wAAAABHRUlXAAAAAAAAAAABAAAAUAAAAFAAAABQAAAAUAAAAFAAAABgAAAAAAAA"
            + "AARAAbAAAAAA/nHewKfsrN3fr4ioR0VERwsAAAAAAAAAAQAAAAAZQ1em7d2BAAAAAAAAAAA=");
            public static readonly byte[] Iw16 = Convert.FromBase64String("AAAAAAAAEAALAAAAY2FsXA==");
            public static readonly byte[] Iw17 = Convert.FromBase64String("AAAAAAAAAAwLAAAAMTdfbQ==");
            public static readonly byte[] Ca6 = Convert.FromBase64String("MAAAAAAAAAAAAAAAAAAAADAAAAAwAAAAMAAAADAAAAAwAAAAAAAAAAAAAAAAAAAA");
            public static readonly byte[] Ca8 = Convert.FromBase64String("gAAAAAEAAAAoAAAAAQAAAEAAAAABAAAASAAAAAEAAABoAAAAAQAAAP////////////////////8AAAAAAAAAAAEAAAAAAAAA////"
            + "/////////////////wAAAAABAAAAAAAAAAAAAADuFhO/4Uzx8QAAAAAAAAAAAAAAAAAAAAA=");
            public static readonly byte[] Gevs = Convert.FromBase64String("R0VWUwQAAAAactf6MDg12sWxPoTqj8mTGwAAAAQAAABsAwAAQAEAANsAAACsBAAA2wAAAAQAAAAbAAAAawMAAAAAAQAIABUAKABB"
            + "AF8AggCpANQAAwE0AWcBmwHQAQQCNwJoApcCwgLpAgwDKgNDA1YDYwNqAwAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA"
            + "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAQAHAA0AEwAZAB4AIwAnACsALwAxADMANAA1ADQAMwAx"
            + "AC8AKwAnACMAHgAZABMADQAHAAEAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA"
            + "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAQAAAAIAAAADAAAABAAAAAUAAAAGAAAABwAAAAgAAAAJAAAACgAAAAsAAAAMAAAADQAA"
            + "AA4AAAAPAAAAEAAAABEAAAASAAAAEwAAABQAAAAVAAAAFgAAABcAAAAYAAAAGQAAABoAAAAbAAAAHAAAAB0AAAAeAAAAHwAAACAA"
            + "AAAhAAAAIgAAACMAAAAkAAAAJQAAACYAAAAnAAAAKAAAACkAAAAqAAAAKwAAACwAAAAtAAAALgAAAC8AAAAwAAAAMQAAADIAAAAz"
            + "AAAANAAAADUAAAA2AAAANwAAADgAAAA5AAAAOgAAADsAAAA8AAAAPQAAAD4AAAA/AAAAQAAAAEEAAABCAAAAQwAAAEQAAABFAAAA"
            + "RgAAAEcAAABIAAAASQAAAEoAAABLAAAATAAAAE0AAABOAAAATwAAAFAAAABRAAAAUgAAAFMAAABUAAAAVQAAAFYAAABXAAAAWAAA"
            + "AFkAAABaAAAAWwAAAFwAAABdAAAAXgAAAF8AAABgAAAAYQAAAGIAAABjAAAAZAAAAGUAAABmAAAAZwAAAGgAAABpAAAAagAAAGsA"
            + "AABsAAAAbQAAAG4AAABvAAAAcAAAAHEAAAByAAAAcwAAAHQAAAB1AAAAdgAAAHcAAAB4AAAAeQAAAHoAAAB7AAAAfAAAAH0AAAB+"
            + "AAAAfwAAAIAAAACBAAAAggAAAIMAAACEAAAAhQAAAIYAAACHAAAAiAAAAIkAAACKAAAAiwAAAIwAAACNAAAAjgAAAI8AAACQAAAA"
            + "kQAAAJIAAACTAAAAlAAAAJUAAACWAAAAlwAAAJgAAACZAAAAmgAAAJsAAACcAAAAnQAAAJ4AAACfAAAAoAAAAKEAAACiAAAAowAA"
            + "AKQAAAClAAAApgAAAKcAAACoAAAAqQAAAKoAAACrAAAArAAAAK0AAACuAAAArwAAALAAAACxAAAAsgAAALMAAAC0AAAAtQAAALYA"
            + "AAC3AAAAuAAAALkAAAC6AAAAuwAAALwAAAC9AAAAvgAAAL8AAADAAAAAwQAAAMIAAADDAAAAxAAAAMUAAADGAAAAxwAAAMgAAADJ"
            + "AAAAygAAAMsAAADMAAAAzQAAAM4AAADPAAAA0AAAANEAAADSAAAA0wAAANQAAADVAAAA1gAAANcAAADYAAAA2QAAANoAAAAAAAAA"
            + "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA"
            + "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA"
            + "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=");
            public static readonly byte[] Material = Convert.FromBase64String("7hYTv+FM8fEAAAAAAAAAAA==");
        }

        // --------------------------------------------------------------------------------------------- keys

        /// <summary>
        /// Instance key of a mesh placement (FUN_146b18400): FNV-1 32 over the root instance GUID (.NET byte order) and
        /// (int)(v * scale + 0.4999) in single precision of the translation (x100), rotation quaternion (x10000) and axis
        /// lengths (x100). Identity placement: (0,0,0), (0,0,0,1), (1,1,1).
        /// </summary>
        public static uint InstanceKey(Guid rootInstance, float[] trans, float[] quat, float[] scale)
        {
            var buf = new List<byte>(56);
            buf.AddRange(rootInstance.ToByteArray());
            foreach (float v in trans) buf.AddRange(BitConverter.GetBytes(Q(v, 100f)));
            foreach (float v in quat) buf.AddRange(BitConverter.GetBytes(Q(v, 10000f)));
            foreach (float v in scale) buf.AddRange(BitConverter.GetBytes(Q(v, 100f)));
            uint h = 0x811c9dc5;
            foreach (byte b in buf) { h *= 0x01000193; h ^= b; }
            return h;
        }

        private static int Q(float v, float m) { float x = v * m; x = x + 0.4999f; return (int)x; }

        /// <summary>Key of a placement given as a row-vector 4x4 (right, up, forward, translation rows; StadiumBaseLookup.Matrix).</summary>
        public static uint InstanceKey(Guid rootInstance, float[] m)
        {
            double[,] r = new double[3, 3];
            float[] len = new float[3];
            for (int i = 0; i < 3; i++)
            {
                double l = Math.Sqrt((double)m[4 * i] * m[4 * i] + (double)m[4 * i + 1] * m[4 * i + 1] + (double)m[4 * i + 2] * m[4 * i + 2]);
                len[i] = (float)l;
                if (l == 0) l = 1;
                for (int j = 0; j < 3; j++) r[i, j] = m[4 * i + j] / l;
            }
            // quaternion of the normalised rows (the extraction whose signs matched 181 of 181 Amsterdam instances)
            double m00 = r[0, 0], m01 = r[0, 1], m02 = r[0, 2], m10 = r[1, 0], m11 = r[1, 1], m12 = r[1, 2], m20 = r[2, 0], m21 = r[2, 1], m22 = r[2, 2];
            double tr = m00 + m11 + m22, x, y, z, w;
            if (tr > 0) { double q = Math.Sqrt(tr + 1.0) * 2; w = 0.25 * q; x = (m12 - m21) / q; y = (m20 - m02) / q; z = (m01 - m10) / q; }
            else if (m00 > m11 && m00 > m22) { double q = Math.Sqrt(1.0 + m00 - m11 - m22) * 2; w = (m12 - m21) / q; x = 0.25 * q; y = (m01 + m10) / q; z = (m02 + m20) / q; }
            else if (m11 > m22) { double q = Math.Sqrt(1.0 + m11 - m00 - m22) * 2; w = (m20 - m02) / q; x = (m01 + m10) / q; y = 0.25 * q; z = (m12 + m21) / q; }
            else { double q = Math.Sqrt(1.0 + m22 - m00 - m11) * 2; w = (m01 - m10) / q; x = (m02 + m20) / q; y = (m12 + m21) / q; z = 0.25 * q; }
            return InstanceKey(rootInstance, new[] { m[12], m[13], m[14] }, new[] { (float)x, (float)y, (float)z, (float)w }, len);
        }
    }
}
