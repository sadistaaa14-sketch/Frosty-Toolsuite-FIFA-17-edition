using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;

namespace FrostyAnimationEditor.Core.Fbx
{
    /// <summary>an FBX 'C' (char) property</summary>
    public struct FbxChar
    {
        public byte Value;
        public FbxChar(byte v) { Value = v; }
    }

    /// <summary>
    /// One element of an FBX document: a name, a list of properties and child elements.
    /// Property types: bool (B), FbxChar (C), short (Y), int (I), long (L), float (F), double (D), string (S),
    /// byte[] (R raw), bool[] (b), int[] (i), long[] (l), float[] (f), double[] (d).
    /// Object names in strings use the binary form "Name\0\x01Class" (the ASCII form "Class::Name" is converted on read).
    /// </summary>
    public sealed class FbxNode
    {
        public string Name;
        public readonly List<object> Props = new List<object>();
        public readonly List<FbxNode> Children = new List<FbxNode>();

        public FbxNode(string name, params object[] props)
        {
            Name = name;
            if (props != null) Props.AddRange(props);
        }

        public FbxNode Add(string name, params object[] props)
        {
            FbxNode n = new FbxNode(name, props);
            Children.Add(n);
            return n;
        }

        public FbxNode Add(FbxNode n) { Children.Add(n); return n; }

        public FbxNode Find(string name) { return Children.FirstOrDefault(c => c.Name == name); }
        public IEnumerable<FbxNode> All(string name) { return Children.Where(c => c.Name == name); }

        public object P(int i) { return i < Props.Count ? Props[i] : null; }

        public long Long(int i)
        {
            object v = P(i);
            if (v is long) return (long)v;
            if (v is int) return (int)v;
            if (v is short) return (short)v;
            if (v is bool) return (bool)v ? 1 : 0;
            if (v is FbxChar) return ((FbxChar)v).Value;
            if (v is double) return (long)(double)v;
            if (v is float) return (long)(float)v;
            return 0;
        }

        public double Double(int i)
        {
            object v = P(i);
            if (v is double) return (double)v;
            if (v is float) return (float)v;
            if (v is long) return (long)v;
            if (v is int) return (int)v;
            if (v is short) return (short)v;
            if (v is bool) return (bool)v ? 1 : 0;
            return 0;
        }

        public string Str(int i) { return P(i) as string; }

        /// <summary>the numeric array property i as doubles (any array type, or a run of scalars from ASCII)</summary>
        public double[] Doubles(int i = 0)
        {
            object v = P(i);
            if (v is double[]) return (double[])v;
            if (v is float[]) return ((float[])v).Select(x => (double)x).ToArray();
            if (v is int[]) return ((int[])v).Select(x => (double)x).ToArray();
            if (v is long[]) return ((long[])v).Select(x => (double)x).ToArray();
            if (v is bool[]) return ((bool[])v).Select(x => x ? 1.0 : 0.0).ToArray();
            return new double[0];
        }

        public long[] Longs(int i = 0)
        {
            object v = P(i);
            if (v is long[]) return (long[])v;
            if (v is int[]) return ((int[])v).Select(x => (long)x).ToArray();
            if (v is double[]) return ((double[])v).Select(x => (long)Math.Round(x)).ToArray();
            if (v is float[]) return ((float[])v).Select(x => (long)Math.Round(x)).ToArray();
            return new long[0];
        }

        public int[] Ints(int i = 0) { return Longs(i).Select(x => (int)x).ToArray(); }

        /// <summary>value of a child element's first property ("Version: 232" -> 232)</summary>
        public long ChildLong(string name, long dflt = 0) { FbxNode c = Find(name); return c != null && c.Props.Count > 0 ? c.Long(0) : dflt; }
        public double ChildDouble(string name, double dflt = 0) { FbxNode c = Find(name); return c != null && c.Props.Count > 0 ? c.Double(0) : dflt; }
        public string ChildStr(string name) { FbxNode c = Find(name); return c != null ? c.Str(0) : null; }

        /// <summary>"Name\0\x01Class" -> (Name, Class)</summary>
        public static void SplitName(string s, out string name, out string cls)
        {
            name = s ?? ""; cls = "";
            int k = name.IndexOf("\u0000\u0001", StringComparison.Ordinal);
            if (k >= 0) { cls = name.Substring(k + 2); name = name.Substring(0, k); }
        }

        public static string NameClass(string name, string cls) { return (name ?? "") + "\u0000\u0001" + cls; }
    }

    /// <summary>Binary FBX (7.x) writer and binary / ASCII reader.</summary>
    public static class FbxIO
    {
        private static readonly byte[] HeadMagic = Encoding.ASCII.GetBytes("Kaydara FBX Binary  \0\u001a\0");
        // fixed FileId / CreationTime / footer id (the values Blender writes; FBX SDK readers accept them)
        public static readonly byte[] FileId = { 0x28, 0xb3, 0x2a, 0xeb, 0xb6, 0x24, 0xcc, 0xc2, 0xbf, 0xc8, 0xb0, 0x2a, 0xa9, 0x2b, 0xfc, 0xf1 };
        public const string CreationTime = "1970-01-01 10:00:00:000";
        private static readonly byte[] FootId = { 0xfa, 0xbc, 0xab, 0x09, 0xd0, 0xc8, 0xd4, 0x66, 0xb1, 0x76, 0xfb, 0x83, 0x1c, 0xf7, 0x26, 0x7e };
        private static readonly byte[] FootMagic = { 0xf8, 0x5a, 0x8c, 0x6a, 0xde, 0xf5, 0xd9, 0x7e, 0xec, 0xe9, 0x0c, 0xe3, 0x75, 0x8f, 0x29, 0x0b };
        private static readonly HashSet<string> AlwaysSentinel = new HashSet<string> { "AnimationStack", "AnimationLayer" };

        // ------------------------------------------------------------------ write
        public static byte[] WriteBinary(IList<FbxNode> top, int version = 7400)
        {
            using (MemoryStream ms = new MemoryStream())
            {
                BinaryWriter w = new BinaryWriter(ms);
                w.Write(HeadMagic);
                w.Write((uint)version);
                for (int i = 0; i < top.Count; i++) WriteNode(w, top[i], version, i == top.Count - 1);
                w.Write(new byte[version >= 7500 ? 25 : 13]);
                w.Write(FootId);
                w.Write(new byte[4]);
                long ofs = ms.Position;
                long pad = ((ofs + 15) & ~15L) - ofs;
                if (pad == 0) pad = 16;
                w.Write(new byte[pad]);
                w.Write((uint)version);
                w.Write(new byte[120]);
                w.Write(FootMagic);
                w.Flush();
                return ms.ToArray();
            }
        }

        private static void WriteNode(BinaryWriter w, FbxNode n, int version, bool isLast)
        {
            bool wide = version >= 7500;
            long start = w.BaseStream.Position;
            if (wide) { w.Write(0UL); w.Write((ulong)n.Props.Count); w.Write(0UL); }
            else { w.Write(0U); w.Write((uint)n.Props.Count); w.Write(0U); }
            byte[] name = Encoding.ASCII.GetBytes(n.Name);
            w.Write((byte)name.Length);
            w.Write(name);
            long p0 = w.BaseStream.Position;
            foreach (object p in n.Props) WriteProp(w, p);
            long plen = w.BaseStream.Position - p0;
            if (n.Children.Count > 0)
            {
                for (int i = 0; i < n.Children.Count; i++) WriteNode(w, n.Children[i], version, i == n.Children.Count - 1);
                w.Write(new byte[wide ? 25 : 13]);
            }
            else if ((n.Props.Count == 0 && !isLast) || AlwaysSentinel.Contains(n.Name))
                w.Write(new byte[wide ? 25 : 13]);
            long end = w.BaseStream.Position;
            w.BaseStream.Position = start;
            if (wide) { w.Write((ulong)end); w.BaseStream.Position += 8; w.Write((ulong)plen); }
            else { w.Write((uint)end); w.BaseStream.Position += 4; w.Write((uint)plen); }
            w.BaseStream.Position = end;
        }

        private static void WriteArray(BinaryWriter w, char code, int count, byte[] raw)
        {
            w.Write((byte)code);
            w.Write((uint)count);
            if (raw.Length <= 128)
            {
                w.Write(0U);
                w.Write((uint)raw.Length);
                w.Write(raw);
                return;
            }
            byte[] z = Zlib(raw);
            w.Write(1U);
            w.Write((uint)z.Length);
            w.Write(z);
        }

        private static void WriteProp(BinaryWriter w, object p)
        {
            if (p is bool) { w.Write((byte)'B'); w.Write((bool)p ? (byte)1 : (byte)0); }
            else if (p is FbxChar) { w.Write((byte)'C'); w.Write(((FbxChar)p).Value); }
            else if (p is short) { w.Write((byte)'Y'); w.Write((short)p); }
            else if (p is int) { w.Write((byte)'I'); w.Write((int)p); }
            else if (p is long) { w.Write((byte)'L'); w.Write((long)p); }
            else if (p is float) { w.Write((byte)'F'); w.Write((float)p); }
            else if (p is double) { w.Write((byte)'D'); w.Write((double)p); }
            else if (p is string) { byte[] b = Encoding.UTF8.GetBytes((string)p); w.Write((byte)'S'); w.Write((uint)b.Length); w.Write(b); }
            else if (p is byte[]) { byte[] b = (byte[])p; w.Write((byte)'R'); w.Write((uint)b.Length); w.Write(b); }
            else if (p is int[]) { int[] a = (int[])p; byte[] raw = new byte[a.Length * 4]; Buffer.BlockCopy(a, 0, raw, 0, raw.Length); WriteArray(w, 'i', a.Length, raw); }
            else if (p is long[]) { long[] a = (long[])p; byte[] raw = new byte[a.Length * 8]; Buffer.BlockCopy(a, 0, raw, 0, raw.Length); WriteArray(w, 'l', a.Length, raw); }
            else if (p is float[]) { float[] a = (float[])p; byte[] raw = new byte[a.Length * 4]; Buffer.BlockCopy(a, 0, raw, 0, raw.Length); WriteArray(w, 'f', a.Length, raw); }
            else if (p is double[]) { double[] a = (double[])p; byte[] raw = new byte[a.Length * 8]; Buffer.BlockCopy(a, 0, raw, 0, raw.Length); WriteArray(w, 'd', a.Length, raw); }
            else if (p is bool[]) { bool[] a = (bool[])p; WriteArray(w, 'b', a.Length, a.Select(x => x ? (byte)1 : (byte)0).ToArray()); }
            else throw new ArgumentException("FBX property of type " + (p == null ? "null" : p.GetType().Name));
        }

        /// <summary>zlib stream (RFC 1950): header, deflate data, Adler-32</summary>
        public static byte[] Zlib(byte[] raw)
        {
            using (MemoryStream ms = new MemoryStream())
            {
                ms.WriteByte(0x78);
                ms.WriteByte(0x01);
                using (DeflateStream d = new DeflateStream(ms, CompressionLevel.Fastest, true)) d.Write(raw, 0, raw.Length);
                uint a = 1, b = 0;
                foreach (byte x in raw) { a = (a + x) % 65521; b = (b + a) % 65521; }
                uint adler = (b << 16) | a;
                ms.WriteByte((byte)(adler >> 24)); ms.WriteByte((byte)(adler >> 16)); ms.WriteByte((byte)(adler >> 8)); ms.WriteByte((byte)adler);
                return ms.ToArray();
            }
        }

        private static byte[] Unzlib(byte[] z, int expected)
        {
            using (MemoryStream src = new MemoryStream(z, 2, z.Length - 2))
            using (DeflateStream d = new DeflateStream(src, CompressionMode.Decompress))
            {
                byte[] r = new byte[expected];
                int got = 0;
                while (got < expected)
                {
                    int k = d.Read(r, got, expected - got);
                    if (k <= 0) break;
                    got += k;
                }
                if (got != expected) throw new FormatException("FBX: a compressed array is shorter than its declared size");
                return r;
            }
        }

        // ------------------------------------------------------------------ read
        public static bool IsBinary(byte[] data)
        {
            if (data.Length < 27) return false;
            for (int i = 0; i < 18; i++) if (data[i] != HeadMagic[i]) return false;
            return true;
        }

        /// <summary>top-level elements of an FBX file (binary or ASCII); version receives the file version</summary>
        public static List<FbxNode> Read(byte[] data, out int version)
        {
            if (IsBinary(data)) return ReadBinary(data, out version);
            return ReadAscii(Encoding.UTF8.GetString(data), out version);
        }

        public static List<FbxNode> ReadBinary(byte[] data, out int version)
        {
            version = (int)BitConverter.ToUInt32(data, 23);
            List<FbxNode> top = new List<FbxNode>();
            int pos = 27;
            bool wide = version >= 7500;
            while (pos < data.Length)
            {
                FbxNode n = ReadNode(data, ref pos, wide);
                if (n == null) break;
                top.Add(n);
            }
            return top;
        }

        private static FbxNode ReadNode(byte[] d, ref int pos, bool wide)
        {
            long end, nprops, plen;
            if (wide)
            {
                if (pos + 25 > d.Length) return null;
                end = (long)BitConverter.ToUInt64(d, pos); nprops = (long)BitConverter.ToUInt64(d, pos + 8); plen = (long)BitConverter.ToUInt64(d, pos + 16);
                pos += 24;
            }
            else
            {
                if (pos + 13 > d.Length) return null;
                end = BitConverter.ToUInt32(d, pos); nprops = BitConverter.ToUInt32(d, pos + 4); plen = BitConverter.ToUInt32(d, pos + 8);
                pos += 12;
            }
            int nameLen = d[pos++];
            if (end == 0 && nprops == 0 && plen == 0 && nameLen == 0) return null;
            if (end > d.Length || end < pos) throw new FormatException("FBX: an element runs past the end of the file");
            FbxNode n = new FbxNode(Encoding.ASCII.GetString(d, pos, nameLen));
            pos += nameLen;
            int propsEnd = (int)(pos + plen);
            for (long i = 0; i < nprops; i++) n.Props.Add(ReadProp(d, ref pos));
            pos = propsEnd;
            while (pos < end)
            {
                FbxNode c = ReadNode(d, ref pos, wide);
                if (c == null) break;
                n.Children.Add(c);
            }
            pos = (int)end;
            return n;
        }

        private static object ReadProp(byte[] d, ref int pos)
        {
            char t = (char)d[pos++];
            switch (t)
            {
                case 'B': return d[pos++] != 0;
                case 'C': return new FbxChar(d[pos++]);
                case 'Y': { short v = BitConverter.ToInt16(d, pos); pos += 2; return v; }
                case 'I': { int v = BitConverter.ToInt32(d, pos); pos += 4; return v; }
                case 'L': { long v = BitConverter.ToInt64(d, pos); pos += 8; return v; }
                case 'F': { float v = BitConverter.ToSingle(d, pos); pos += 4; return v; }
                case 'D': { double v = BitConverter.ToDouble(d, pos); pos += 8; return v; }
                case 'S': { int len = (int)BitConverter.ToUInt32(d, pos); pos += 4; string s = Encoding.UTF8.GetString(d, pos, len); pos += len; return s; }
                case 'R': { int len = (int)BitConverter.ToUInt32(d, pos); pos += 4; byte[] b = new byte[len]; Buffer.BlockCopy(d, pos, b, 0, len); pos += len; return b; }
                case 'b': case 'c': case 'i': case 'l': case 'f': case 'd':
                {
                    int count = (int)BitConverter.ToUInt32(d, pos);
                    uint enc = BitConverter.ToUInt32(d, pos + 4);
                    int clen = (int)BitConverter.ToUInt32(d, pos + 8);
                    pos += 12;
                    int size = t == 'l' || t == 'd' ? 8 : t == 'b' || t == 'c' ? 1 : 4;
                    byte[] raw;
                    if (enc == 1)
                    {
                        byte[] z = new byte[clen];
                        Buffer.BlockCopy(d, pos, z, 0, clen);
                        raw = Unzlib(z, count * size);
                    }
                    else
                    {
                        raw = new byte[count * size];
                        Buffer.BlockCopy(d, pos, raw, 0, raw.Length);
                    }
                    pos += clen;
                    switch (t)
                    {
                        case 'b': return raw.Select(x => x != 0).ToArray();
                        case 'c': return raw;
                        case 'i': { int[] a = new int[count]; Buffer.BlockCopy(raw, 0, a, 0, raw.Length); return a; }
                        case 'l': { long[] a = new long[count]; Buffer.BlockCopy(raw, 0, a, 0, raw.Length); return a; }
                        case 'f': { float[] a = new float[count]; Buffer.BlockCopy(raw, 0, a, 0, raw.Length); return a; }
                        default: { double[] a = new double[count]; Buffer.BlockCopy(raw, 0, a, 0, raw.Length); return a; }
                    }
                }
                default: throw new FormatException("FBX: unknown property type '" + t + "'");
            }
        }

        // ------------------------------------------------------------------ ASCII
        private sealed class Lexer
        {
            private readonly string s;
            public int Pos;
            public Lexer(string s) { this.s = s; }
            public int Length { get { return s.Length; } }
            public char At(int i) { return i < s.Length ? s[i] : '\0'; }

            /// <summary>skip blanks and comments; newlines too when lines is true</summary>
            public void Skip(bool lines)
            {
                while (Pos < s.Length)
                {
                    char c = s[Pos];
                    if (c == ';') { while (Pos < s.Length && s[Pos] != '\n') Pos++; }
                    else if (c == '\n' || c == '\r') { if (!lines) return; Pos++; }
                    else if (char.IsWhiteSpace(c)) Pos++;
                    else return;
                }
            }

            public char PeekLine() { Skip(false); return At(Pos); }
            public char PeekAny() { Skip(true); return At(Pos); }

            public string Word()
            {
                int a = Pos;
                while (Pos < s.Length && (char.IsLetterOrDigit(s[Pos]) || s[Pos] == '_' || s[Pos] == '|' || s[Pos] == '-' || s[Pos] == '+' || s[Pos] == '.')) Pos++;
                return s.Substring(a, Pos - a);
            }

            public string Quoted()
            {
                Pos++;
                StringBuilder sb = new StringBuilder();
                while (Pos < s.Length && s[Pos] != '"')
                {
                    if (s[Pos] == '&' && string.CompareOrdinal(s, Pos, "&quot;", 0, 6) == 0) { sb.Append('"'); Pos += 6; continue; }
                    sb.Append(s[Pos++]);
                }
                Pos++;
                return sb.ToString();
            }

            public void Expect(char c)
            {
                Skip(true);
                if (Pos >= s.Length || s[Pos] != c) throw new FormatException("FBX (ASCII): expected '" + c + "' at character " + Pos);
                Pos++;
            }

            public string Number()
            {
                int a = Pos;
                while (Pos < s.Length && "0123456789+-.eE".IndexOf(s[Pos]) >= 0) Pos++;
                return s.Substring(a, Pos - a);
            }
        }

        private static object AsciiNumber(string t)
        {
            long l;
            if (long.TryParse(t, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out l)) return l;
            double d;
            if (double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out d)) return d;
            throw new FormatException("FBX (ASCII): not a number: " + t);
        }

        /// <summary>"Model::Hips" (ASCII) -> "Hips\0\x01Model" (binary form)</summary>
        private static string AsciiName(string s)
        {
            int k = s.IndexOf("::", StringComparison.Ordinal);
            if (k < 0) return s;
            return s.Substring(k + 2) + "\u0000\u0001" + s.Substring(0, k);
        }

        public static List<FbxNode> ReadAscii(string text, out int version)
        {
            Lexer lx = new Lexer(text);
            List<FbxNode> top = new List<FbxNode>();
            while (lx.PeekAny() != '\0') top.Add(ReadAsciiNode(lx));
            version = 7400;
            FbxNode he = top.FirstOrDefault(n => n.Name == "FBXHeaderExtension");
            if (he != null) version = (int)he.ChildLong("FBXVersion", 7400);
            return top;
        }

        private static object AsciiValue(Lexer lx)
        {
            char c = lx.PeekLine();
            if (c == '"') return AsciiName(lx.Quoted());
            if (c == '*')
            {
                lx.Pos++;
                lx.Number();
                lx.Expect('{');
                lx.Skip(true);
                lx.Word();
                lx.Expect(':');
                List<object> vals = new List<object>();
                while (true)
                {
                    char d = lx.PeekAny();
                    if (d == '}' || d == '\0') break;
                    if (d == ',') { lx.Pos++; continue; }
                    vals.Add(AsciiNumber(lx.Number()));
                }
                lx.Expect('}');
                if (vals.All(v => v is long)) return vals.Select(v => (long)v).ToArray();
                return vals.Select(v => v is long ? (double)(long)v : (double)v).ToArray();
            }
            if ("0123456789+-.".IndexOf(c) >= 0) return AsciiNumber(lx.Number());
            string w = lx.Word();
            if (w.Length == 0) throw new FormatException("FBX (ASCII): unexpected '" + c + "' at character " + lx.Pos);
            if (w == "T" || w == "Y") return true;
            if (w == "F" || w == "N") return false;
            return w;
        }

        private static FbxNode ReadAsciiNode(Lexer lx)
        {
            lx.Skip(true);
            string name = lx.Word();
            if (name.Length == 0) throw new FormatException("FBX (ASCII): expected an element name at character " + lx.Pos);
            lx.Expect(':');
            FbxNode n = new FbxNode(name);
            char c = lx.PeekLine();
            if (c != '{' && c != '\n' && c != '\r' && c != '\0' && c != '}')
            {
                while (true)
                {
                    n.Props.Add(AsciiValue(lx));
                    if (lx.PeekLine() == ',') { lx.Pos++; lx.Skip(true); continue; }
                    break;
                }
            }
            if (lx.PeekLine() == '{')
            {
                lx.Pos++;
                while (true)
                {
                    char d = lx.PeekAny();
                    if (d == '}' || d == '\0') break;
                    n.Children.Add(ReadAsciiNode(lx));
                }
                lx.Expect('}');
            }
            return n;
        }
    }
}
