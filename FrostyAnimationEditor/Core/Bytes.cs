using System;
using System.Runtime.InteropServices;
using System.Text;

namespace FrostyAnimationEditor.Core
{
    /// <summary>Big-endian (the GD stream) and little-endian (the key tables) byte access.</summary>
    public static class BE
    {
        [StructLayout(LayoutKind.Explicit)]
        private struct Bits32
        {
            [FieldOffset(0)] public float F;
            [FieldOffset(0)] public uint U;
        }

        [StructLayout(LayoutKind.Explicit)]
        private struct Bits64
        {
            [FieldOffset(0)] public double D;
            [FieldOffset(0)] public ulong U;
        }

        public static uint FloatBits(float f) { Bits32 b = default(Bits32); b.F = f; return b.U; }
        public static float BitsFloat(uint u) { Bits32 b = default(Bits32); b.U = u; return b.F; }
        public static ulong DoubleBits(double d) { Bits64 b = default(Bits64); b.D = d; return b.U; }
        public static double BitsDouble(ulong u) { Bits64 b = default(Bits64); b.U = u; return b.D; }

        public static byte U8(byte[] b, long o) { return b[o]; }
        public static ushort U16(byte[] b, long o) { return (ushort)((b[o] << 8) | b[o + 1]); }
        public static short S16(byte[] b, long o) { return (short)U16(b, o); }
        public static uint U32(byte[] b, long o) { return ((uint)b[o] << 24) | ((uint)b[o + 1] << 16) | ((uint)b[o + 2] << 8) | b[o + 3]; }
        public static int S32(byte[] b, long o) { return (int)U32(b, o); }
        public static ulong U64(byte[] b, long o) { return ((ulong)U32(b, o) << 32) | U32(b, o + 4); }
        public static long S64(byte[] b, long o) { return (long)U64(b, o); }
        public static float F32(byte[] b, long o) { return BitsFloat(U32(b, o)); }

        public static void W8(byte[] b, long o, byte v) { b[o] = v; }
        public static void W16(byte[] b, long o, ushort v) { b[o] = (byte)(v >> 8); b[o + 1] = (byte)v; }
        public static void W32(byte[] b, long o, uint v)
        {
            b[o] = (byte)(v >> 24); b[o + 1] = (byte)(v >> 16); b[o + 2] = (byte)(v >> 8); b[o + 3] = (byte)v;
        }
        public static void W64(byte[] b, long o, ulong v) { W32(b, o, (uint)(v >> 32)); W32(b, o + 4, (uint)v); }
        public static void WF32(byte[] b, long o, float v) { W32(b, o, FloatBits(v)); }

        public static ushort U16LE(byte[] b, long o) { return (ushort)(b[o] | (b[o + 1] << 8)); }
        public static uint U32LE(byte[] b, long o) { return b[o] | ((uint)b[o + 1] << 8) | ((uint)b[o + 2] << 16) | ((uint)b[o + 3] << 24); }
        public static ulong U64LE(byte[] b, long o) { return U32LE(b, o) | ((ulong)U32LE(b, o + 4) << 32); }
        public static float F32LE(byte[] b, long o) { return BitsFloat(U32LE(b, o)); }
        public static void W16LE(byte[] b, long o, ushort v) { b[o] = (byte)v; b[o + 1] = (byte)(v >> 8); }
        public static void W32LE(byte[] b, long o, uint v)
        {
            b[o] = (byte)v; b[o + 1] = (byte)(v >> 8); b[o + 2] = (byte)(v >> 16); b[o + 3] = (byte)(v >> 24);
        }
        public static void W64LE(byte[] b, long o, ulong v) { W32LE(b, o, (uint)v); W32LE(b, o + 4, (uint)(v >> 32)); }

        /// <summary>The byte strings in the file are Latin-1 (Python's decode("latin1")).</summary>
        public static readonly Encoding Latin1 = Encoding.GetEncoding(28591);

        public static string Hex(byte[] b)
        {
            if (b == null) return "";
            StringBuilder sb = new StringBuilder(b.Length * 2);
            foreach (byte x in b) sb.Append(x.ToString("x2"));
            return sb.ToString();
        }

        public static byte[] FromHex(string s)
        {
            s = s.Replace(" ", "");
            if (s.Length % 2 != 0) throw new FormatException("odd number of hex digits");
            byte[] r = new byte[s.Length / 2];
            for (int i = 0; i < r.Length; i++) r[i] = Convert.ToByte(s.Substring(2 * i, 2), 16);
            return r;
        }

        public static bool Equal(byte[] a, long ao, byte[] b, long bo, long n)
        {
            for (long i = 0; i < n; i++) if (a[ao + i] != b[bo + i]) return false;
            return true;
        }

        public static long Align(long x, long a) { return a <= 1 ? x : (x + a - 1) / a * a; }

        public static string KeyHex(ulong k) { return "0x" + k.ToString("x16"); }

        public static ulong ParseKey(string s)
        {
            s = s.Trim();
            if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) return Convert.ToUInt64(s.Substring(2), 16);
            if (s.StartsWith("-")) return (ulong)long.Parse(s);
            return ulong.Parse(s);
        }

        /// <summary>Signed 64-bit order of keys (tables T, C, A are sorted that way).</summary>
        public static int CompareSigned(ulong a, ulong b) { return ((long)a).CompareTo((long)b); }
    }

    /// <summary>A growable byte buffer with big-endian writers (the packer's output).</summary>
    public sealed class ByteBuf
    {
        public byte[] Data = new byte[4096];
        public int Length;

        public void Ensure(long n)
        {
            if (n > Data.Length)
            {
                long cap = Data.Length;
                while (cap < n) cap *= 2;
                Array.Resize(ref Data, (int)cap);
            }
            if (n > Length) Length = (int)n;
        }

        public byte[] ToArray(int n)
        {
            byte[] r = new byte[n];
            Buffer.BlockCopy(Data, 0, r, 0, Math.Min(n, Length));
            return r;
        }
    }
}
