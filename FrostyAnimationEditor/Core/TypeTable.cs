using System;
using System.Collections.Generic;
using System.Text;

namespace FrostyAnimationEditor.Core
{
    /// <summary>
    /// The builtin type ids of the REFL table (the 17 primitives; constants, not CRCs).
    /// </summary>
    public static class TypeIds
    {
        public const uint Bool = 1, Int8 = 2, UInt8 = 3, Int16 = 4, UInt16 = 5, Int32 = 6, UInt32 = 7, Int64 = 8;
        public const uint Float = 0x0A;
        public const uint Vector2 = 0x0B, Vector3 = 0x0C, Vector4 = 0x0D, Quaternion = 0x0E, Matrix44 = 0x0F;
        public const uint String = 0x11, DataRef = 0x12, Key = 0x23;

        public static bool IsPrim(uint t)
        {
            return t == Bool || t == Int8 || t == UInt8 || t == Int16 || t == UInt16 || t == Int32 || t == UInt32 || t == Int64 || t == Float;
        }

        public static bool IsVector(uint t) { return t == Vector2 || t == Vector3 || t == Vector4 || t == Quaternion; }

        /// <summary>primitive list element types stored as big-endian arrays (bytes are separate)</summary>
        public static bool IsListFmt(uint t)
        {
            return t == Bool || t == Int16 || t == UInt16 || t == Int32 || t == UInt32 || t == Int64 || t == Float;
        }

        public static int PrimSize(uint t)
        {
            switch (t)
            {
                case Bool: case Int8: case UInt8: return 1;
                case Int16: case UInt16: return 2;
                case Int32: case UInt32: case Float: return 4;
                case Int64: return 8;
            }
            return 0;
        }

        public static string PrimName(uint t)
        {
            switch (t)
            {
                case Bool: return "Bool";
                case Int8: return "Int8";
                case UInt8: return "UInt8";
                case Int16: return "Int16";
                case UInt16: return "UInt16";
                case Int32: return "Int32";
                case UInt32: return "UInt32";
                case Int64: return "Int64";
                case Float: return "Float";
                case Vector2: return "Vector2";
                case Vector3: return "Vector3";
                case Vector4: return "Vector4";
                case Quaternion: return "Quaternion";
                case Matrix44: return "Matrix44";
                case String: return "String";
                case DataRef: return "DataRef";
                case Key: return "Key";
            }
            return null;
        }
    }

    /// <summary>A field record of a REFL descriptor (32 bytes in the file).</summary>
    public sealed class Field
    {
        public string Name;
        public uint FType;
        public uint Size;
        public uint Offset;
        public ushort Count;
        public ushort Attr;
        public ushort Align;
        public ushort Ordinal;
        public ulong TypePtr;
        public int Index;
        public byte[] RecBytes;
        public TypeDesc Type;          // resolved descriptor of the field's type (element type for lists)
        public TypeDesc Owner;         // the type that declares the field

        public bool IsList { get { return (Attr & 1) != 0; } }
        public bool IsImplicit { get { return Name == "__name" || Name == "__base" || Name == "__key"; } }

        /// <summary>bytes the field occupies in its owner (count * size; 16 for a list header)</summary>
        public long Slot { get { return (long)Count * Size; } }

        public string TypeName { get { return Type != null ? Type.Name : "0x" + FType.ToString("X8"); } }

        public string Kind()
        {
            if (IsList) return "list";
            if (FType == TypeIds.DataRef) return "ref";
            if (FType == TypeIds.Key) return "key";
            if (FType == TypeIds.String) return "string";
            if (TypeIds.IsPrim(FType)) return "prim";
            if (TypeIds.IsVector(FType) || FType == TypeIds.Matrix44) return "vector";
            if (Size == 0) return "void";
            return "struct";
        }

        /// <summary>true for a field whose value is a fixed array of Count values</summary>
        public bool IsFixedArray { get { return !IsList && Count != 1 && Size != 0; } }

        public override string ToString()
        {
            return Name + " " + TypeName + (IsList ? "[]" : "") + " size=" + Size + " off=0x" + Offset.ToString("x") + " count=" + Count;
        }
    }

    /// <summary>A REFL type descriptor.</summary>
    public sealed class TypeDesc
    {
        public uint Tid;
        public string Name;
        public uint Size;
        public uint Align;
        public uint Pad0;
        public uint LastIndex;
        public uint Flags;
        public uint Nbo;
        public uint Nbs;
        public List<Field> Fields = new List<Field>();
        public Dictionary<string, Field> ByName = new Dictionary<string, Field>();
        public Dictionary<string, int> IndexOf = new Dictionary<string, int>();
        public byte[] Raw;
        public long RelOffset;
        public int Index;
        public bool IsClass;

        public bool IsPrimitive { get { return Fields.Count == 0; } }

        public Field GetField(string name)
        {
            Field f;
            return ByName.TryGetValue(name, out f) ? f : null;
        }

        public bool Has(string name) { return ByName.ContainsKey(name); }

        public IEnumerable<Field> OwnFields()
        {
            foreach (Field f in Fields) if (!f.IsImplicit) yield return f;
        }

        public long Stride()
        {
            long a = Align == 0 ? 1 : Align;
            return (Size + a - 1) / a * a;
        }

        public override string ToString()
        {
            return Name + " 0x" + Tid.ToString("X8") + " size=" + Size + " align=" + Align + " fields=" + Fields.Count;
        }
    }

    /// <summary>
    /// The parsed GD.REFLb blob (the file's own runtime type descriptors).
    ///   payload: u64 count; u64 offsets[count]; descriptor records...; reloc list
    ///   descriptor: u32 pad0, lastFieldIndex, size, align, nameBlockOffset, nameBlockLength, flags, typeId;
    ///               (nameBlockOffset-0x20)/32 field records {u32 type, size, offset, nameOffset;
    ///               u16 count, attr (bit0 list), align, ordinal; u64 descriptor pointer}; name block.
    /// The raw payload is kept so the table is written back verbatim.
    /// </summary>
    public sealed class TypeTable
    {
        public readonly byte[] Raw;
        public readonly uint Aux;
        public readonly Dictionary<uint, TypeDesc> Types = new Dictionary<uint, TypeDesc>();
        public readonly Dictionary<string, TypeDesc> ByName = new Dictionary<string, TypeDesc>();
        public readonly List<TypeDesc> Order = new List<TypeDesc>();

        public TypeTable(byte[] payload, uint aux)
        {
            Raw = payload;
            Aux = aux;
            Parse();
        }

        private static int IndexOfZero(byte[] b, long from, long end)
        {
            for (long i = from; i < end; i++) if (b[i] == 0) return (int)i;
            return -1;
        }

        private void Parse()
        {
            byte[] m = Raw;
            long cnt = (long)BE.U64(m, 0);
            long[] offs = new long[cnt];
            for (long i = 0; i < cnt; i++) offs[i] = (long)BE.U64(m, 8 + 8 * i);
            for (int i = 0; i < cnt; i++)
            {
                long d = offs[i];
                TypeDesc t = new TypeDesc();
                t.Pad0 = BE.U32(m, d);
                t.LastIndex = BE.U32(m, d + 4);
                t.Size = BE.U32(m, d + 8);
                t.Align = BE.U32(m, d + 12);
                t.Nbo = BE.U32(m, d + 16);
                t.Nbs = BE.U32(m, d + 20);
                t.Flags = BE.U32(m, d + 24);
                t.Tid = BE.U32(m, d + 28);
                t.RelOffset = d;
                t.Index = i;
                long end = i + 1 < cnt ? offs[i + 1] : d + t.Nbo + t.Nbs;
                long blk = d + t.Nbo;
                long blkEnd = blk + t.Nbs;
                int z = IndexOfZero(m, blk + 1, blkEnd);
                t.Name = BE.Latin1.GetString(m, (int)(blk + 1), z - (int)(blk + 1));
                long nf = (t.Nbo - 0x20) / 32;
                for (int k = 0; k < nf; k++)
                {
                    long r = d + 0x20 + 32 * k;
                    Field f = new Field();
                    f.FType = BE.U32(m, r);
                    f.Size = BE.U32(m, r + 4);
                    f.Offset = BE.U32(m, r + 8);
                    uint noff = BE.U32(m, r + 12);
                    f.Count = BE.U16(m, r + 16);
                    f.Attr = BE.U16(m, r + 18);
                    f.Align = BE.U16(m, r + 20);
                    f.Ordinal = BE.U16(m, r + 22);
                    f.TypePtr = BE.U64(m, r + 0x18);
                    int fz = IndexOfZero(m, blk + noff, blkEnd);
                    f.Name = BE.Latin1.GetString(m, (int)(blk + noff), fz - (int)(blk + noff));
                    f.Index = k;
                    f.RecBytes = new byte[32];
                    Buffer.BlockCopy(m, (int)r, f.RecBytes, 0, 32);
                    f.Owner = t;
                    t.Fields.Add(f);
                }
                for (int k = 0; k < t.Fields.Count; k++)
                {
                    t.ByName[t.Fields[k].Name] = t.Fields[k];
                    t.IndexOf[t.Fields[k].Name] = k;
                }
                t.IsClass = t.ByName.ContainsKey("__key") || t.ByName.ContainsKey("__base");
                t.Raw = new byte[end - d];
                Buffer.BlockCopy(m, (int)d, t.Raw, 0, (int)(end - d));
                Types[t.Tid] = t;
                ByName[t.Name] = t;
                Order.Add(t);
            }
            foreach (TypeDesc t in Order)
                foreach (Field f in t.Fields)
                {
                    TypeDesc ft;
                    f.Type = Types.TryGetValue(f.FType, out ft) ? ft : null;
                }
        }

        public TypeDesc this[uint tid] { get { return Types[tid]; } }
        public TypeDesc this[string name] { get { return ByName[name]; } }

        public TypeDesc Get(uint tid) { TypeDesc t; return Types.TryGetValue(tid, out t) ? t : null; }
        public TypeDesc Get(string name) { TypeDesc t; return name != null && ByName.TryGetValue(name, out t) ? t : null; }
        public bool Contains(uint tid) { return Types.ContainsKey(tid); }
        public bool Contains(string name) { return ByName.ContainsKey(name); }
        public int Count { get { return Order.Count; } }

        public byte[] ToBytes() { return Raw; }

        // -------------------------------------------------------------------------------------
        // CRC-32 type id: CRC-32 MSB-first, poly 0x04C11DB7, init 0, fed with every field record's (+0x00 u32, +0x0C u32, +0x10 u16, +0x12 u16, +0x08 u32,
        // +0x16 u16) little-endian, then the name block, then u32 pad0, u32 lastFieldIndex, u32 align.
        // -------------------------------------------------------------------------------------
        private static readonly uint[] CrcTab = BuildCrcTable();

        private static uint[] BuildCrcTable()
        {
            uint[] tab = new uint[256];
            for (uint i = 0; i < 256; i++)
            {
                uint c = i << 24;
                for (int k = 0; k < 8; k++) c = (c & 0x80000000u) != 0 ? (c << 1) ^ 0x04C11DB7u : (c << 1);
                tab[i] = c;
            }
            return tab;
        }

        public static uint Crc32Msb(byte[] data, uint crc = 0)
        {
            return Crc32Msb(data, 0, data.Length, crc);
        }

        public static uint Crc32Msb(byte[] data, int start, int count, uint crc)
        {
            for (int i = start; i < start + count; i++)
                crc = (crc << 8) ^ CrcTab[((crc >> 24) ^ data[i]) & 0xFF];
            return crc;
        }

        public static uint TypeIdOf(TypeDesc t)
        {
            List<byte> s = new List<byte>();
            byte[] tmp = new byte[4];
            foreach (Field f in t.Fields)
            {
                uint noff = BE.U32(f.RecBytes, 0x0C);
                AddLE32(s, f.FType); AddLE32(s, noff);
                AddLE16(s, f.Count); AddLE16(s, f.Attr);
                AddLE32(s, f.Offset); AddLE16(s, f.Ordinal);
            }
            for (long i = t.Nbo; i < t.Nbo + t.Nbs; i++) s.Add(t.Raw[i]);
            AddLE32(s, t.Pad0); AddLE32(s, t.LastIndex); AddLE32(s, t.Align);
            return Crc32Msb(s.ToArray());
        }

        private static void AddLE32(List<byte> s, uint v) { s.Add((byte)v); s.Add((byte)(v >> 8)); s.Add((byte)(v >> 16)); s.Add((byte)(v >> 24)); }
        private static void AddLE16(List<byte> s, ushort v) { s.Add((byte)v); s.Add((byte)(v >> 8)); }
    }
}
