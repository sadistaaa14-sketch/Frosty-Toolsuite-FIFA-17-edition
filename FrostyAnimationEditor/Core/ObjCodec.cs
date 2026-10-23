using System;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace FrostyAnimationEditor.Core
{
    public sealed class GdException : Exception
    {
        public GdException(string msg) : base(msg) { }
    }

    /// <summary>reference equality comparer (shared nested objects are placed once by identity)</summary>
    public sealed class RefEq<T> : IEqualityComparer<T> where T : class
    {
        public static readonly RefEq<T> Instance = new RefEq<T>();
        public bool Equals(T a, T b) { return ReferenceEquals(a, b); }
        public int GetHashCode(T o) { return RuntimeHelpers.GetHashCode(o); }
    }

    /// <summary>
    /// The exact GD.DATAb payload codec (notes.md section 28, proved on every block):
    ///   * object = 0x20-byte header {0, marker 0x030E6205, 0, 0, 0, typeId, 0, u16 dataOffset, 0} + field slots;
    ///   * fields are walked in descriptor record order (pass A): strings/lists -> data at align_up(cursor,
    ///     element align) (string chars: align 1); embedded-struct arrays: every element slot first, then each
    ///     element's variable data; embedded struct -> its own variable data immediately (its own pass B);
    ///     DataRef / DataRef list cells -> queued;
    ///   * pass B: every queued DataRef target, in record order, at align_up(cursor, 8);
    ///   * tail zero-pad to 4, then {u32 count; u32 offsets[]} = every non-null pointer slot;
    ///   * block = "GD.DATAb" + u32 size + u32 aux + payload, aux = reloc offset + 0x10,
    ///     size = aux + 4*count + 4 (all big-endian).
    /// </summary>
    public static class ObjCodec
    {
        public const uint Marker = 0x030E6205;

        /// <summary>The packer starts an object's data at the class alignment (payload-relative).</summary>
        public static long DataOffset(long at, TypeDesc t)
        {
            long a = t.Align > 1 ? t.Align : 1;
            return ((at + 0x20 + a - 1) / a * a) - at;
        }

        public static void WriteStdHeader(byte[] b, long at, uint tid, long doff)
        {
            for (long i = 0; i < doff; i++) b[at + i] = 0;
            BE.W32(b, at + 4, Marker);
            BE.W32(b, at + 0x14, tid);
            BE.W16(b, at + 0x1C, (ushort)doff);
        }

        public static bool IsStdHeader(byte[] b, long at, uint tid, long doff)
        {
            for (long i = 0; i < doff; i++)
            {
                byte e = 0;
                if (i >= 4 && i < 8) e = (byte)(Marker >> (int)(8 * (7 - i)));
                else if (i >= 0x14 && i < 0x18) e = (byte)(tid >> (int)(8 * (0x17 - i)));
                else if (i == 0x1C) e = (byte)(doff >> 8);
                else if (i == 0x1D) e = (byte)doff;
                if (b[at + i] != e) return false;
            }
            return true;
        }

        /// <summary>decode the root object of a payload that starts at b[p0]</summary>
        public static GdObj DecodeBlock(byte[] b, long p0, TypeTable types)
        {
            return new Decoder(b, p0, types).Object(0);
        }

        /// <summary>-> payload bytes (incl. tail pad and reloc list) and aux</summary>
        public static byte[] EncodePayload(GdObj root, TypeTable types, out uint aux)
        {
            Encoder e = new Encoder(types);
            long cursor = e.Object(root, 0);
            cursor = BE.Align(cursor, 4);
            e.Ensure(cursor);
            e.Relocs.Sort();
            List<long> rel = new List<long>(e.Relocs.Count);
            long last = -1;
            foreach (long r in e.Relocs) { if (r != last) rel.Add(r); last = r; }
            byte[] outp = new byte[cursor + 4 + 4 * rel.Count];
            Buffer.BlockCopy(e.Buf.Data, 0, outp, 0, (int)cursor);
            BE.W32(outp, cursor, (uint)rel.Count);
            for (int i = 0; i < rel.Count; i++) BE.W32(outp, cursor + 4 + 4 * i, (uint)rel[i]);
            aux = (uint)(cursor + 0x10);
            return outp;
        }

        public static readonly byte[] DataTag = { (byte)'G', (byte)'D', (byte)'.', (byte)'D', (byte)'A', (byte)'T', (byte)'A', (byte)'b' };

        public static byte[] EncodeBlock(GdObj root, TypeTable types)
        {
            uint aux;
            byte[] payload = EncodePayload(root, types, out aux);
            byte[] blk = new byte[payload.Length + 16];
            Buffer.BlockCopy(DataTag, 0, blk, 0, 8);
            BE.W32(blk, 8, (uint)blk.Length);
            BE.W32(blk, 12, aux);
            Buffer.BlockCopy(payload, 0, blk, 16, payload.Length);
            return blk;
        }

        // =========================================================================================
        public sealed class Decoder
        {
            private readonly byte[] b;
            private readonly long p0;
            private readonly TypeTable types;
            private readonly Dictionary<long, GdObj> objs = new Dictionary<long, GdObj>();

            public Decoder(byte[] buf, long payloadStart, TypeTable types)
            {
                b = buf;
                p0 = payloadStart;
                this.types = types;
            }

            public GdObj Object(long off)
            {
                GdObj o;
                if (objs.TryGetValue(off, out o)) return o;
                long a = p0 + off;
                if (BE.U32(b, a + 4) != Marker)
                    throw new GdException("no object marker at payload+0x" + off.ToString("x"));
                uint tid = BE.U32(b, a + 0x14);
                TypeDesc t = types.Get(tid);
                if (t == null)
                    throw new GdException("unknown type id 0x" + tid.ToString("X8") + " at payload+0x" + off.ToString("x"));
                long doff = BE.U16(b, a + 0x1C);
                byte[] extra = null;
                if (doff != DataOffset(off, t) || !IsStdHeader(b, a, tid, doff))
                {
                    extra = new byte[doff];
                    Buffer.BlockCopy(b, (int)a, extra, 0, (int)doff);
                }
                o = new GdObj(t, extra);
                objs[off] = o;
                Fields(o, off + doff);
                return o;
            }

            private void Fields(GdStruct st, long slot0)
            {
                TypeDesc t = st.Type;
                for (int i = 0; i < t.Fields.Count; i++)
                {
                    Field f = t.Fields[i];
                    long s = slot0 + f.Offset;
                    if (f.IsList)
                        st.Values[i] = List(f, s);
                    else if (f.Count != 1 && f.Size != 0)
                    {
                        object[] arr = new object[f.Count];
                        for (int k = 0; k < f.Count; k++) arr[k] = Value(f, s + (long)k * f.Size);
                        st.Values[i] = arr;
                    }
                    else if (f.Size != 0 || TypeIds.IsPrim(f.FType) || (f.Type != null && f.Type.Size != 0))
                        st.Values[i] = Value(f, s);
                }
            }

            private object Value(Field f, long s)
            {
                uint ft = f.FType;
                long a = p0 + s;
                switch (ft)
                {
                    case TypeIds.Key: return BE.U64(b, a);
                    case TypeIds.Bool: return b[a] != 0;
                    case TypeIds.Int8: return (long)(sbyte)b[a];
                    case TypeIds.UInt8: return (long)b[a];
                    case TypeIds.Int16: return (long)BE.S16(b, a);
                    case TypeIds.UInt16: return (long)BE.U16(b, a);
                    case TypeIds.Int32: return (long)BE.S32(b, a);
                    case TypeIds.UInt32: return (long)BE.U32(b, a);
                    case TypeIds.Int64: return BE.S64(b, a);
                    case TypeIds.Float: return BE.F32(b, a);
                    case TypeIds.DataRef:
                        {
                            ulong p = BE.U64(b, a);
                            return p != 0 ? Object((long)p) : null;
                        }
                    case TypeIds.String: return String(s);
                    case TypeIds.Vector2:
                    case TypeIds.Vector3:
                    case TypeIds.Vector4:
                    case TypeIds.Quaternion: return Floats(a, 4);
                    case TypeIds.Matrix44: return Floats(a, 16);
                }
                TypeDesc t = f.Type;
                if (t == null)
                {
                    byte[] raw = new byte[f.Size];
                    Buffer.BlockCopy(b, (int)a, raw, 0, (int)f.Size);
                    return raw;
                }
                GdStruct st = new GdStruct(t);
                Fields(st, s);
                return st;
            }

            private float[] Floats(long a, int n)
            {
                float[] r = new float[n];
                for (int i = 0; i < n; i++) r[i] = BE.F32(b, a + 4 * i);
                return r;
            }

            private object String(long s)
            {
                long a = p0 + s;
                uint cnt = BE.U32(b, a);
                uint cap = BE.U32(b, a + 4);
                ulong ptr = BE.U64(b, a + 8);
                if (cnt == 0 && ptr == 0 && cap == 0) return null;
                long pa = p0 + (long)ptr;
                bool plain = cap == cnt && cnt != 0 && b[pa + cnt - 1] == 0;
                if (plain)
                    for (long i = 0; i < cnt - 1; i++)
                        if (b[pa + i] == 0) { plain = false; break; }
                if (plain) return BE.Latin1.GetString(b, (int)pa, (int)cnt - 1);
                byte[] raw = new byte[cnt];
                if (cnt != 0) Buffer.BlockCopy(b, (int)pa, raw, 0, (int)cnt);
                return new RawStr(raw, cap, cnt == 0 ? (ulong?)ptr : null);
            }

            private object List(Field f, long s)
            {
                long a = p0 + s;
                uint cnt = BE.U32(b, a);
                uint cap = BE.U32(b, a + 4);
                ulong ptr = BE.U64(b, a + 8);
                TypeDesc et = f.Type;
                uint ft = f.FType;
                if (cnt == 0)
                {
                    if (ptr != 0 || cap != 0) return new RawList(cap, ptr);
                    return EmptyList(f);
                }
                if (cap != cnt) throw new GdException("list capacity " + cap + " != count " + cnt);
                long pa = p0 + (long)ptr;
                int n = (int)cnt;
                if ((ft == TypeIds.UInt8 || ft == TypeIds.Int8) && !(et != null && et.Fields.Count != 0))
                {
                    byte[] r = new byte[n];
                    Buffer.BlockCopy(b, (int)pa, r, 0, n);
                    return r;
                }
                switch (ft)
                {
                    case TypeIds.Bool:
                        {
                            List<bool> r = new List<bool>(n);
                            for (int k = 0; k < n; k++) r.Add(b[pa + k] != 0);
                            return r;
                        }
                    case TypeIds.Int16:
                        {
                            List<long> r = new List<long>(n);
                            for (int k = 0; k < n; k++) r.Add(BE.S16(b, pa + 2 * k));
                            return r;
                        }
                    case TypeIds.UInt16:
                        {
                            List<long> r = new List<long>(n);
                            for (int k = 0; k < n; k++) r.Add(BE.U16(b, pa + 2 * k));
                            return r;
                        }
                    case TypeIds.Int32:
                        {
                            List<long> r = new List<long>(n);
                            for (int k = 0; k < n; k++) r.Add(BE.S32(b, pa + 4 * k));
                            return r;
                        }
                    case TypeIds.UInt32:
                        {
                            List<long> r = new List<long>(n);
                            for (int k = 0; k < n; k++) r.Add(BE.U32(b, pa + 4 * k));
                            return r;
                        }
                    case TypeIds.Int64:
                        {
                            List<long> r = new List<long>(n);
                            for (int k = 0; k < n; k++) r.Add(BE.S64(b, pa + 8 * k));
                            return r;
                        }
                    case TypeIds.Float:
                        {
                            List<float> r = new List<float>(n);
                            for (int k = 0; k < n; k++) r.Add(BE.F32(b, pa + 4 * k));
                            return r;
                        }
                    case TypeIds.Key:
                        {
                            List<ulong> r = new List<ulong>(n);
                            for (int k = 0; k < n; k++) r.Add(BE.U64(b, pa + 8 * k));
                            return r;
                        }
                    case TypeIds.DataRef:
                        {
                            List<GdObj> r = new List<GdObj>(n);
                            for (int k = 0; k < n; k++)
                            {
                                ulong p = BE.U64(b, pa + 8 * k);
                                r.Add(p != 0 ? Object((long)p) : null);
                            }
                            return r;
                        }
                    case TypeIds.String:
                        {
                            List<object> r = new List<object>(n);
                            for (int k = 0; k < n; k++) r.Add(String((long)ptr + 16 * k));
                            return r;
                        }
                    case TypeIds.Vector2:
                    case TypeIds.Vector3:
                    case TypeIds.Vector4:
                    case TypeIds.Quaternion:
                        {
                            List<float[]> r = new List<float[]>(n);
                            for (int k = 0; k < n; k++) r.Add(Floats(pa + 16 * k, 4));
                            return r;
                        }
                    case TypeIds.Matrix44:
                        {
                            List<float[]> r = new List<float[]>(n);
                            for (int k = 0; k < n; k++) r.Add(Floats(pa + 64 * k, 16));
                            return r;
                        }
                }
                if (et == null) throw new GdException("list of unknown type 0x" + ft.ToString("X8"));
                long stride = et.Stride();
                List<GdStruct> rs = new List<GdStruct>(n);
                for (int k = 0; k < n; k++)
                {
                    GdStruct st = new GdStruct(et);
                    Fields(st, (long)ptr + k * stride);
                    rs.Add(st);
                }
                return rs;
            }
        }

        /// <summary>a new empty list of the representation a list field uses</summary>
        public static object EmptyList(Field f)
        {
            uint ft = f.FType;
            TypeDesc et = f.Type;
            if ((ft == TypeIds.UInt8 || ft == TypeIds.Int8) && !(et != null && et.Fields.Count != 0)) return new byte[0];
            switch (ft)
            {
                case TypeIds.Bool: return new List<bool>();
                case TypeIds.Int16: case TypeIds.UInt16: case TypeIds.Int32: case TypeIds.UInt32: case TypeIds.Int64: return new List<long>();
                case TypeIds.Float: return new List<float>();
                case TypeIds.Key: return new List<ulong>();
                case TypeIds.DataRef: return new List<GdObj>();
                case TypeIds.String: return new List<object>();
                case TypeIds.Vector2: case TypeIds.Vector3: case TypeIds.Vector4: case TypeIds.Quaternion: case TypeIds.Matrix44: return new List<float[]>();
            }
            return new List<GdStruct>();
        }

        // =========================================================================================
        public sealed class Encoder
        {
            private readonly TypeTable types;
            public readonly ByteBuf Buf = new ByteBuf();
            public readonly List<long> Relocs = new List<long>();
            private readonly Dictionary<GdObj, long> placed = new Dictionary<GdObj, long>(RefEq<GdObj>.Instance);

            public Encoder(TypeTable types) { this.types = types; }

            public void Ensure(long n) { Buf.Ensure(n); }

            public long Object(GdObj o, long at)
            {
                TypeDesc t = o.Type;
                if (!types.Contains(t.Tid)) throw new GdException("type " + t.Name + " not in the type table");
                long doff = o.HdrExtra == null ? DataOffset(at, t) : o.HdrExtra.Length;
                Ensure(at + doff + t.Size);
                if (o.HdrExtra == null) WriteStdHeader(Buf.Data, at, t.Tid, doff);
                else Buffer.BlockCopy(o.HdrExtra, 0, Buf.Data, (int)at, o.HdrExtra.Length);
                placed[o] = at;
                long cursor = at + doff + t.Size;
                return Fields(o, at + doff, cursor);
            }

            private struct Pending
            {
                public long Slot;
                public GdObj Obj;
                public Pending(long s, GdObj o) { Slot = s; Obj = o; }
            }

            private long Fields(GdStruct st, long slot0, long cursor)
            {
                TypeDesc t = st.Type;
                List<Pending> items = null;
                for (int i = 0; i < t.Fields.Count; i++)
                {
                    Field f = t.Fields[i];
                    long s = slot0 + f.Offset;
                    object val = st.Values[i];
                    if (f.IsList)
                        cursor = List(f, s, val, cursor, ref items);
                    else if (f.Count != 1 && f.Size != 0)
                    {
                        IList vals = val as IList;
                        for (int k = 0; k < f.Count; k++)
                            cursor = Value(f, s + (long)k * f.Size, vals != null && k < vals.Count ? vals[k] : null, cursor, ref items);
                    }
                    else
                        cursor = Value(f, s, val, cursor, ref items);
                }
                if (items != null)
                    foreach (Pending it in items)
                    {
                        long p;
                        if (!placed.TryGetValue(it.Obj, out p))
                        {
                            cursor = BE.Align(cursor, 8);
                            p = cursor;
                            cursor = Object(it.Obj, cursor);
                        }
                        BE.W64(Buf.Data, it.Slot, (ulong)p);
                        Relocs.Add(it.Slot);
                    }
                return cursor;
            }

            private long Value(Field f, long s, object val, long cursor, ref List<Pending> items)
            {
                uint ft = f.FType;
                byte[] b = Buf.Data;
                switch (ft)
                {
                    case TypeIds.Key: BE.W64(b, s, Num.ToKey(val)); return cursor;
                    case TypeIds.Bool: if (val != null) b[s] = (byte)(Num.ToBool(val) ? 1 : 0); return cursor;
                    case TypeIds.Int8:
                    case TypeIds.UInt8: if (val != null) b[s] = (byte)Num.ToLong(val); return cursor;
                    case TypeIds.Int16:
                    case TypeIds.UInt16: if (val != null) BE.W16(b, s, (ushort)Num.ToLong(val)); return cursor;
                    case TypeIds.Int32:
                    case TypeIds.UInt32: if (val != null) BE.W32(b, s, (uint)Num.ToLong(val)); return cursor;
                    case TypeIds.Int64: if (val != null) BE.W64(b, s, (ulong)Num.ToLong(val)); return cursor;
                    case TypeIds.Float: if (val != null) BE.WF32(b, s, Num.ToFloat(val)); return cursor;
                    case TypeIds.DataRef:
                        if (val != null)
                        {
                            if (items == null) items = new List<Pending>();
                            items.Add(new Pending(s, (GdObj)val));
                        }
                        return cursor;
                    case TypeIds.String: return String(s, val, cursor);
                    case TypeIds.Vector2:
                    case TypeIds.Vector3:
                    case TypeIds.Vector4:
                    case TypeIds.Quaternion:
                        if (val != null) WriteFloats(s, val, 4);
                        return cursor;
                    case TypeIds.Matrix44:
                        if (val != null) WriteFloats(s, val, 16);
                        return cursor;
                }
                if (f.Type != null && f.Type.Fields.Count != 0)
                {
                    if (val != null) cursor = Fields((GdStruct)val, s, cursor);
                }
                else if (f.Size != 0 && val != null)
                {
                    byte[] raw = (byte[])val;
                    Buffer.BlockCopy(raw, 0, b, (int)s, Math.Min(raw.Length, (int)f.Size));
                }
                return cursor;
            }

            private void WriteFloats(long s, object val, int n)
            {
                IList l = (IList)val;
                for (int i = 0; i < n; i++)
                    BE.WF32(Buf.Data, s + 4 * i, i < l.Count ? Num.ToFloat(l[i]) : 0f);
            }

            private long String(long s, object val, long cursor)
            {
                if (val == null) return cursor;
                byte[] raw;
                uint cap;
                RawStr rs = val as RawStr;
                if (rs != null)
                {
                    raw = rs.Raw;
                    cap = rs.Cap;
                    if (raw.Length == 0)
                    {
                        BE.W32(Buf.Data, s, 0);
                        BE.W32(Buf.Data, s + 4, cap);
                        BE.W64(Buf.Data, s + 8, rs.Ptr0 ?? 0);
                        return cursor;
                    }
                }
                else
                {
                    byte[] txt = val is byte[] ? (byte[])val : BE.Latin1.GetBytes(Str.Text(val));
                    raw = new byte[txt.Length + 1];
                    Buffer.BlockCopy(txt, 0, raw, 0, txt.Length);
                    cap = (uint)raw.Length;
                }
                int n = raw.Length;
                Ensure(cursor + n);
                Buffer.BlockCopy(raw, 0, Buf.Data, (int)cursor, n);
                BE.W32(Buf.Data, s, (uint)n);
                BE.W32(Buf.Data, s + 4, cap);
                BE.W64(Buf.Data, s + 8, (ulong)cursor);
                Relocs.Add(s + 8);
                return cursor + n;
            }

            private void Blob(long s, uint n, uint cap, ulong ptr)
            {
                BE.W32(Buf.Data, s, n);
                BE.W32(Buf.Data, s + 4, cap);
                BE.W64(Buf.Data, s + 8, ptr);
            }

            private long List(Field f, long s, object val, long cursor, ref List<Pending> items)
            {
                int n;
                if (val == null) n = 0;
                else if (val is byte[]) n = ((byte[])val).Length;
                else n = ((ICollection)val).Count;
                if (n == 0)
                {
                    RawList rl = val as RawList;
                    if (rl != null)
                    {
                        Blob(s, 0, rl.Cap, rl.Ptr0);
                        if (rl.Ptr0 != 0) Relocs.Add(s + 8);
                    }
                    return cursor;
                }
                TypeDesc et = f.Type;
                uint ft = f.FType;
                long ea = et != null ? et.Align : 1;
                if (ea == 0) ea = 1;
                cursor = BE.Align(cursor, ea);
                Blob(s, (uint)n, (uint)n, (ulong)cursor);
                Relocs.Add(s + 8);
                long arr = cursor;
                if ((ft == TypeIds.UInt8 || ft == TypeIds.Int8) && !(et != null && et.Fields.Count != 0))
                {
                    Ensure(arr + n);
                    byte[] bytes = val as byte[];
                    if (bytes != null) Buffer.BlockCopy(bytes, 0, Buf.Data, (int)arr, n);
                    else
                    {
                        IList l = (IList)val;
                        for (int k = 0; k < n; k++) Buf.Data[arr + k] = (byte)Num.ToLong(l[k]);
                    }
                    return arr + n;
                }
                if (TypeIds.IsListFmt(ft))
                {
                    long sz = et.Size;
                    Ensure(arr + n * sz);
                    byte[] b = Buf.Data;
                    IList l = (IList)val;
                    switch (ft)
                    {
                        case TypeIds.Bool:
                            for (int k = 0; k < n; k++) b[arr + k] = (byte)(Num.ToBool(l[k]) ? 1 : 0);
                            break;
                        case TypeIds.Int16:
                        case TypeIds.UInt16:
                            {
                                List<long> ll = val as List<long>;
                                for (int k = 0; k < n; k++) BE.W16(b, arr + 2 * k, (ushort)(ll != null ? ll[k] : Num.ToLong(l[k])));
                            }
                            break;
                        case TypeIds.Int32:
                        case TypeIds.UInt32:
                            {
                                List<long> ll = val as List<long>;
                                for (int k = 0; k < n; k++) BE.W32(b, arr + 4 * k, (uint)(ll != null ? ll[k] : Num.ToLong(l[k])));
                            }
                            break;
                        case TypeIds.Int64:
                            {
                                List<long> ll = val as List<long>;
                                for (int k = 0; k < n; k++) BE.W64(b, arr + 8 * k, (ulong)(ll != null ? ll[k] : Num.ToLong(l[k])));
                            }
                            break;
                        case TypeIds.Float:
                            {
                                List<float> lf = val as List<float>;
                                for (int k = 0; k < n; k++) BE.WF32(b, arr + 4 * k, lf != null ? lf[k] : Num.ToFloat(l[k]));
                            }
                            break;
                    }
                    return arr + n * sz;
                }
                if (ft == TypeIds.Key)
                {
                    Ensure(arr + 8 * n);
                    IList l = (IList)val;
                    List<ulong> lk = val as List<ulong>;
                    for (int k = 0; k < n; k++) BE.W64(Buf.Data, arr + 8 * k, lk != null ? lk[k] : Num.ToKey(l[k]));
                    return arr + 8 * n;
                }
                if (ft == TypeIds.DataRef)
                {
                    Ensure(arr + 8 * n);
                    IList l = (IList)val;
                    for (int k = 0; k < n; k++)
                    {
                        GdObj o = l[k] as GdObj;
                        if (o != null)
                        {
                            if (items == null) items = new List<Pending>();
                            items.Add(new Pending(arr + 8 * k, o));
                        }
                    }
                    return arr + 8 * n;
                }
                if (ft == TypeIds.String)
                {
                    Ensure(arr + 16 * n);
                    IList l = (IList)val;
                    cursor = arr + 16 * n;
                    for (int k = 0; k < n; k++) cursor = String(arr + 16 * k, l[k], cursor);
                    return cursor;
                }
                if (TypeIds.IsVector(ft))
                {
                    Ensure(arr + 16 * n);
                    IList l = (IList)val;
                    for (int k = 0; k < n; k++) WriteFloats(arr + 16 * k, l[k], 4);
                    return arr + 16 * (n - 1) + et.Size;
                }
                if (ft == TypeIds.Matrix44)
                {
                    Ensure(arr + 64 * n);
                    IList l = (IList)val;
                    for (int k = 0; k < n; k++) WriteFloats(arr + 64 * k, l[k], 16);
                    return arr + 64 * n;
                }
                long stride = et.Stride();
                Ensure(arr + stride * n);
                IList ls = (IList)val;
                cursor = arr + (n - 1) * stride + et.Size;
                for (int k = 0; k < n; k++)
                    cursor = Fields((GdStruct)ls[k], arr + k * stride, cursor);
                return cursor;
            }
        }
    }
}
