using System;
using System.Collections.Generic;
using System.IO;

namespace FrostyAnimationEditor.Core
{
    /// <summary>
    /// The fifagame_win32_antstate.res container (file layout):
    ///   0x00  16 bytes before the container body (Frosty's res export writes the ResMeta there)
    ///   0x10  u32 BE version = 3   (what the game checks)
    ///   0x14  8 x u32 BE: H1 = 0x20+T*16+C*8+A*16+B*16 (bytes from 0x14 to GD.STRMb; Frosty's AssetBank
    ///         parser skips this many bytes after the version), H2 = version copy, H3..H6 = |T|,|C|,|A|,|B|,
    ///         H7 = reserve of the loader's pending-key waiter pool, H8 = AntLinearResourceAllocator size
    ///   T  |T| x {u64 key; u64 0}   LE, sorted signed: every root key
    ///   C  |C| x {u64 key}          LE, sorted signed: keys this container exports
    ///   A  |A| x {u64 key; u16 ownerSlot=0xFFFF; u16 nextA=0xFFFF; u16 firstB; u16 numB} LE, sorted signed: imports
    ///   B  |B| x {u64 waiter=-1; u32 classId; u32 flags}: one waiter slot per importing reference
    ///   GD.STRMb {u32 size (to EOF); u32 aux = loader scratch size (>= the largest DATA payload)}
    ///   GD.REFLb {size; aux} + type table
    ///   GD.DATAb {size; aux} + payload, one root object per block, until EOF.
    /// </summary>
    public sealed class Header
    {
        public byte[] Prefix = new byte[16];
        public uint Version = 3;
        public uint[] H = new uint[] { 0, 3, 0, 0, 0, 0, 0, 0 };

        public Header Copy()
        {
            Header h = new Header();
            h.Prefix = (byte[])Prefix.Clone();
            h.Version = Version;
            h.H = (uint[])H.Clone();
            return h;
        }

        public uint WaiterReserve { get { return H[6]; } }
        public uint HeapSize { get { return H[7]; } }
    }

    public struct ARow
    {
        public ulong Key;
        public ushort OwnerSlot, NextA, FirstB, NumB;
        public ARow(ulong key, ushort ownerSlot, ushort nextA, ushort firstB, ushort numB)
        {
            Key = key; OwnerSlot = ownerSlot; NextA = nextA; FirstB = firstB; NumB = numB;
        }
    }

    public struct BRow
    {
        public ulong Waiter;
        public uint ClassId, Flags;
        public BRow(ulong waiter, uint classId, uint flags) { Waiter = waiter; ClassId = classId; Flags = flags; }
    }

    /// <summary>one block of the output: bytes Data[Off .. Off+Len)</summary>
    public struct BlockSrc
    {
        public byte[] Data;
        public long Off;
        public long Len;
        public BlockSrc(byte[] data, long off, long len) { Data = data; Off = off; Len = len; }
        public BlockSrc(byte[] data) { Data = data; Off = 0; Len = data.Length; }
    }

    public sealed class Container
    {
        public const int HdrOff = 0x10;
        public const int TOff = 0x34;
        public static readonly byte[] StrmTag = Tag("GD.STRMb");
        public static readonly byte[] ReflTag = Tag("GD.REFLb");
        public static readonly byte[] DataTag = Tag("GD.DATAb");

        private static byte[] Tag(string s) { return BE.Latin1.GetBytes(s); }

        /// <summary>the whole file image: 16-byte prefix + container body</summary>
        public readonly byte[] File;
        public readonly long Size;
        public readonly Header Header = new Header();
        public readonly List<ulong> T = new List<ulong>();
        public readonly List<ulong> TTail = new List<ulong>();
        public readonly List<ulong> C = new List<ulong>();
        public readonly List<ARow> A = new List<ARow>();
        public readonly List<BRow> B = new List<BRow>();
        public long StrmOff;
        public uint StrmSize, StrmAux;
        public long ReflOff;
        public uint ReflSize, ReflAux;
        public TypeTable Types;
        public long DataOff;
        public long[] BlockOff;
        public uint[] BlockSize;

        public int BlockCount { get { return BlockOff.Length; } }

        /// <summary>file = 16-byte prefix + body (the bytes from the version dword on)</summary>
        public Container(byte[] file)
        {
            File = file;
            Size = file.Length;
            byte[] m = file;
            Buffer.BlockCopy(m, 0, Header.Prefix, 0, 16);
            uint version = BE.U32(m, HdrOff);
            if (version != 3) throw new GdException("unsupported container version " + version + " (expected 3)");
            Header.Version = version;
            for (int i = 0; i < 8; i++) Header.H[i] = BE.U32(m, 0x14 + 4 * i);
            uint nT = Header.H[2], nC = Header.H[3], nA = Header.H[4], nB = Header.H[5];
            long o = TOff;
            for (long i = 0; i < nT; i++) { T.Add(BE.U64LE(m, o + 16 * i)); TTail.Add(BE.U64LE(m, o + 16 * i + 8)); }
            o += 16L * nT;
            for (long i = 0; i < nC; i++) C.Add(BE.U64LE(m, o + 8 * i));
            o += 8L * nC;
            for (long i = 0; i < nA; i++)
            {
                long r = o + 16 * i;
                A.Add(new ARow(BE.U64LE(m, r), BE.U16LE(m, r + 8), BE.U16LE(m, r + 10), BE.U16LE(m, r + 12), BE.U16LE(m, r + 14)));
            }
            o += 16L * nA;
            for (long i = 0; i < nB; i++)
            {
                long r = o + 16 * i;
                B.Add(new BRow(BE.U64LE(m, r), BE.U32LE(m, r + 8), BE.U32LE(m, r + 12)));
            }
            o += 16L * nB;
            StrmOff = o;
            if (!BE.Equal(m, o, StrmTag, 0, 8)) throw new GdException("GD.STRMb not found at 0x" + o.ToString("X"));
            StrmSize = BE.U32(m, o + 8);
            StrmAux = BE.U32(m, o + 12);
            long ro = o + 16;
            if (!BE.Equal(m, ro, ReflTag, 0, 8)) throw new GdException("GD.REFLb not found at 0x" + ro.ToString("X"));
            ReflOff = ro;
            ReflSize = BE.U32(m, ro + 8);
            ReflAux = BE.U32(m, ro + 12);
            byte[] refl = new byte[ReflSize - 16];
            Buffer.BlockCopy(m, (int)(ro + 16), refl, 0, refl.Length);
            Types = new TypeTable(refl, ReflAux);
            DataOff = ro + ReflSize;
            ScanBlocks();
        }

        private void ScanBlocks()
        {
            byte[] m = File;
            List<long> offs = new List<long>(500000);
            List<uint> sizes = new List<uint>(500000);
            long off = DataOff, end = Size;
            while (off < end)
            {
                if (!BE.Equal(m, off, DataTag, 0, 8)) throw new GdException("expected GD.DATAb at 0x" + off.ToString("X"));
                uint s = BE.U32(m, off + 8);
                if (s < 16) throw new GdException("bad block size at 0x" + off.ToString("X"));
                offs.Add(off);
                sizes.Add(s);
                off += s;
            }
            if (off != end) throw new GdException("block walk ended at 0x" + off.ToString("X") + ", file size 0x" + end.ToString("X"));
            BlockOff = offs.ToArray();
            BlockSize = sizes.ToArray();
        }

        /// <summary>file offset of block i's payload</summary>
        public long PayloadOff(int i) { return BlockOff[i] + 16; }

        public BlockSrc Block(int i) { return new BlockSrc(File, BlockOff[i], BlockSize[i]); }

        public byte[] BlockBytes(int i)
        {
            byte[] r = new byte[BlockSize[i]];
            Buffer.BlockCopy(File, (int)BlockOff[i], r, 0, r.Length);
            return r;
        }

        public bool BlockEquals(int i, byte[] blk)
        {
            if (blk.Length != BlockSize[i]) return false;
            return BE.Equal(File, BlockOff[i], blk, 0, blk.Length);
        }

        /// <summary>
        /// A complete container image.  base16 = true leaves out the 16-byte prefix (the array then starts at
        /// the version dword, the layout Frosty stores in the res/chunk).
        /// </summary>
        public static byte[] BuildFile(Header header, IList<ulong> T, IList<ulong> C, IList<ARow> A, IList<BRow> B,
                                       uint strmAux, byte[] reflPayload, uint reflAux, IList<BlockSrc> blocks, bool withoutPrefix,
                                       out long strmPos)
        {
            uint[] h = (uint[])header.H.Clone();
            h[1] = header.Version;
            h[2] = (uint)T.Count; h[3] = (uint)C.Count; h[4] = (uint)A.Count; h[5] = (uint)B.Count;
            h[0] = (uint)(0x20 + 16L * T.Count + 8L * C.Count + 16L * A.Count + 16L * B.Count);
            long total = TOff + 16L * T.Count + 8L * C.Count + 16L * A.Count + 16L * B.Count + 16 + 16 + reflPayload.Length;
            foreach (BlockSrc bs in blocks) total += bs.Len;
            long bas = withoutPrefix ? 16 : 0;
            if (total - bas > int.MaxValue) throw new GdException("the container would exceed 2 GB");
            byte[] o = new byte[total - bas];
            if (!withoutPrefix) Buffer.BlockCopy(header.Prefix, 0, o, 0, 16);
            BE.W32(o, HdrOff - bas, header.Version);
            for (int i = 0; i < 8; i++) BE.W32(o, 0x14 + 4 * i - bas, h[i]);
            long p = TOff - bas;
            foreach (ulong k in T) { BE.W64LE(o, p, k); BE.W64LE(o, p + 8, 0); p += 16; }
            foreach (ulong k in C) { BE.W64LE(o, p, k); p += 8; }
            foreach (ARow r in A)
            {
                BE.W64LE(o, p, r.Key); BE.W16LE(o, p + 8, r.OwnerSlot); BE.W16LE(o, p + 10, r.NextA);
                BE.W16LE(o, p + 12, r.FirstB); BE.W16LE(o, p + 14, r.NumB); p += 16;
            }
            foreach (BRow r in B) { BE.W64LE(o, p, r.Waiter); BE.W32LE(o, p + 8, r.ClassId); BE.W32LE(o, p + 12, r.Flags); p += 16; }
            strmPos = p + bas;
            Buffer.BlockCopy(StrmTag, 0, o, (int)p, 8);
            BE.W32(o, p + 8, (uint)(total - (p + bas)));
            BE.W32(o, p + 12, strmAux);
            p += 16;
            Buffer.BlockCopy(ReflTag, 0, o, (int)p, 8);
            BE.W32(o, p + 8, (uint)(reflPayload.Length + 16));
            BE.W32(o, p + 12, reflAux);
            p += 16;
            Buffer.BlockCopy(reflPayload, 0, o, (int)p, reflPayload.Length);
            p += reflPayload.Length;
            foreach (BlockSrc bs in blocks)
            {
                Buffer.BlockCopy(bs.Data, (int)bs.Off, o, (int)p, (int)bs.Len);
                p += bs.Len;
            }
            return o;
        }

        /// <summary>reads a whole .res file (prefix + body)</summary>
        public static byte[] ReadFile(string path)
        {
            return System.IO.File.ReadAllBytes(path);
        }

        /// <summary>a file image from a body that starts at the version dword</summary>
        public static byte[] FromBody(Stream s, byte[] prefix)
        {
            long n = s.Length - s.Position;
            if (n + 16 > int.MaxValue) throw new GdException("the antstate data is larger than 2 GB");
            byte[] f = new byte[n + 16];
            if (prefix != null) Buffer.BlockCopy(prefix, 0, f, 0, Math.Min(16, prefix.Length));
            int got = 0;
            while (got < n)
            {
                int r = s.Read(f, 16 + got, (int)Math.Min(n - got, 1 << 24));
                if (r <= 0) throw new EndOfStreamException();
                got += r;
            }
            return f;
        }

        /// <summary>true if the bytes start at the version dword of a container (00 00 00 03 + H1 + GD.STRMb at H1+4)</summary>
        public static bool LooksLikeBody(byte[] b, long at)
        {
            if (b.Length < at + 0x40) return false;
            if (BE.U32(b, at) != 3) return false;
            long h1 = BE.U32(b, at + 4);
            long strm = at + 4 + h1;
            return strm + 8 <= b.Length && BE.Equal(b, strm, StrmTag, 0, 8);
        }
    }
}
