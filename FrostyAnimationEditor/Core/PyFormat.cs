using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using System.Text;

namespace FrostyAnimationEditor.Core
{
    /// <summary>Python-compatible number formatting and parsing (exact, the same on every .NET version)</summary>
    public static class PyFormat
    {
        /// <summary>exact decimal expansion of a finite non-zero |x|: value = digits * 10^exp10</summary>
        private static void Exact(double x, out BigInteger digits, out int exp10)
        {
            long bits = BitConverter.DoubleToInt64Bits(x);
            int be = (int)((bits >> 52) & 0x7FF);
            long frac = bits & 0xFFFFFFFFFFFFFL;
            long mant;
            int e2;
            if (be == 0) { mant = frac; e2 = -1074; }
            else { mant = frac | (1L << 52); e2 = be - 1075; }
            if (e2 >= 0)
            {
                digits = new BigInteger(mant) << e2;
                exp10 = 0;
            }
            else
            {
                digits = new BigInteger(mant) * BigInteger.Pow(5, -e2);
                exp10 = e2;
            }
            if (digits.IsZero) return;
            // strip trailing zeros
            while (digits % 10 == 0) { digits /= 10; exp10++; }
        }

        /// <summary>|x| rounded to p significant digits (round half even): digit string and exponent of its first digit</summary>
        private static void Round(double x, int p, out string sig, out int exp10)
        {
            BigInteger d;
            int e;
            Exact(Math.Abs(x), out d, out e);
            string ds = d.ToString(CultureInfo.InvariantCulture);
            exp10 = ds.Length - 1 + e;
            if (ds.Length <= p) { sig = ds.PadRight(p, '0'); return; }
            string keep = ds.Substring(0, p);
            char first = ds[p];
            bool up;
            if (first > '5') up = true;
            else if (first < '5') up = false;
            else
            {
                bool rest = false;
                for (int i = p + 1; i < ds.Length; i++) if (ds[i] != '0') { rest = true; break; }
                up = rest || ((keep[p - 1] - '0') % 2 == 1);
            }
            if (up)
            {
                BigInteger k = BigInteger.Parse(keep, CultureInfo.InvariantCulture) + 1;
                string ks = k.ToString(CultureInfo.InvariantCulture);
                if (ks.Length > p) { ks = ks.Substring(0, p); exp10++; }
                keep = ks;
            }
            sig = keep;
        }

        private static string StripZeros(string s)
        {
            if (s.IndexOf('.') < 0) return s;
            s = s.TrimEnd('0');
            if (s.EndsWith(".")) s = s.Substring(0, s.Length - 1);
            return s;
        }

        /// <summary>Python "%.{p}g" % x</summary>
        public static string G(double x, int p)
        {
            if (double.IsNaN(x)) return "nan";
            if (double.IsPositiveInfinity(x)) return "inf";
            if (double.IsNegativeInfinity(x)) return "-inf";
            if (p == 0) p = 1;
            bool neg = x < 0 || (x == 0 && BitConverter.DoubleToInt64Bits(x) < 0);
            if (x == 0) return neg ? "-0" : "0";
            string sig;
            int exp;
            Round(x, p, out sig, out exp);
            string body;
            if (-4 <= exp && exp < p)
            {
                if (exp >= 0)
                {
                    string ip = sig.Substring(0, exp + 1);
                    string fp = sig.Substring(exp + 1);
                    body = fp.Length > 0 ? ip + "." + fp : ip;
                }
                else body = "0." + new string('0', -exp - 1) + sig;
                body = StripZeros(body);
            }
            else
            {
                string m = sig.Length > 1 ? sig.Substring(0, 1) + "." + sig.Substring(1) : sig;
                m = StripZeros(m);
                body = m + "e" + (exp < 0 ? "-" : "+") + Math.Abs(exp).ToString("00", CultureInfo.InvariantCulture);
            }
            return neg ? "-" + body : body;
        }

        // ------------------------------------------------------------------ str helpers (Python semantics)
        /// <summary>Python str.isspace() for one character</summary>
        public static bool IsSpace(char c) { return char.IsWhiteSpace(c) || (c >= '\x1c' && c <= '\x1f'); }

        public static string Strip(string s) { return RStrip(LStrip(s)); }

        public static string LStrip(string s)
        {
            int i = 0;
            while (i < s.Length && IsSpace(s[i])) i++;
            return i == 0 ? s : s.Substring(i);
        }

        public static string RStrip(string s)
        {
            int e = s.Length;
            while (e > 0 && IsSpace(s[e - 1])) e--;
            return e == s.Length ? s : s.Substring(0, e);
        }

        /// <summary>Python str.split() / str.split(None, maxsplit)</summary>
        public static List<string> SplitWs(string s, int maxsplit = -1)
        {
            List<string> r = new List<string>();
            int i = 0, n = s.Length;
            while (true)
            {
                while (i < n && IsSpace(s[i])) i++;
                if (i >= n) break;
                if (maxsplit >= 0 && r.Count == maxsplit)
                {
                    r.Add(s.Substring(i));
                    break;
                }
                int j = i;
                while (j < n && !IsSpace(s[j])) j++;
                r.Add(s.Substring(i, j - i));
                i = j;
            }
            return r;
        }

        /// <summary>Python str.splitlines()</summary>
        public static List<string> SplitLines(string s)
        {
            List<string> r = new List<string>();
            int i = 0, st = 0, n = s.Length;
            while (i < n)
            {
                char c = s[i];
                bool br = c == '\n' || c == '\r' || c == '\x0b' || c == '\x0c' || c == '\x1c' || c == '\x1d' || c == '\x1e' || c == '\x85' || c == '\u2028' || c == '\u2029';
                if (!br) { i++; continue; }
                r.Add(s.Substring(st, i - st));
                i += (c == '\r' && i + 1 < n && s[i + 1] == '\n') ? 2 : 1;
                st = i;
            }
            if (st < n) r.Add(s.Substring(st));
            return r;
        }

        private static bool Printable(int cp)
        {
            if (cp == ' ') return true;
            UnicodeCategory cat;
            if (cp > 0xFFFF) cat = CharUnicodeInfo.GetUnicodeCategory(char.ConvertFromUtf32(cp), 0);
            else cat = CharUnicodeInfo.GetUnicodeCategory((char)cp);
            switch (cat)
            {
                case UnicodeCategory.Control:
                case UnicodeCategory.Format:
                case UnicodeCategory.Surrogate:
                case UnicodeCategory.PrivateUse:
                case UnicodeCategory.OtherNotAssigned:
                case UnicodeCategory.LineSeparator:
                case UnicodeCategory.ParagraphSeparator:
                case UnicodeCategory.SpaceSeparator:
                    return false;
            }
            return true;
        }

        /// <summary>Python repr() of a str</summary>
        public static string Repr(string s)
        {
            char q = s.IndexOf('\'') >= 0 && s.IndexOf('"') < 0 ? '"' : '\'';
            StringBuilder sb = new StringBuilder(s.Length + 2);
            sb.Append(q);
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                int cp = c;
                if (char.IsHighSurrogate(c) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1])) cp = char.ConvertToUtf32(c, s[i + 1]);
                if (c == q || c == '\\') sb.Append('\\').Append(c);
                else if (c == '\n') sb.Append("\\n");
                else if (c == '\r') sb.Append("\\r");
                else if (c == '\t') sb.Append("\\t");
                else if (cp < 0x20 || cp == 0x7F) sb.Append("\\x").Append(cp.ToString("x2"));
                else if (cp < 0x7F) sb.Append(c);
                else if (Printable(cp)) { sb.Append(c); if (cp > 0xFFFF) sb.Append(s[++i]); }
                else if (cp <= 0xFF) sb.Append("\\x").Append(cp.ToString("x2"));
                else if (cp <= 0xFFFF) sb.Append("\\u").Append(cp.ToString("x4"));
                else { sb.Append("\\U").Append(cp.ToString("x8")); i++; }
            }
            sb.Append(q);
            return sb.ToString();
        }

        /// <summary>Python "%x" % v (a minus sign for negative values)</summary>
        public static string Hex(BigInteger v)
        {
            if (v.Sign < 0) return "-" + Hex(-v);
            if (v.IsZero) return "0";
            string h = v.ToString("x", CultureInfo.InvariantCulture).TrimStart('0');
            return h.Length == 0 ? "0" : h;
        }

        // ------------------------------------------------------------------ numbers (Python semantics)
        private static int DigitValue(char c)
        {
            if (c >= '0' && c <= '9') return c - '0';
            if (c >= 'a' && c <= 'z') return c - 'a' + 10;
            if (c >= 'A' && c <= 'Z') return c - 'A' + 10;
            if (char.IsDigit(c)) return (int)char.GetNumericValue(c);    // other Unicode decimal digits (Python accepts them)
            return 99;
        }

        private static FormatException IntError(string s, int b)
        {
            string r = Repr(s);
            if (r.Length > 200) r = r.Substring(0, 200);
            return new FormatException("invalid literal for int() with base " + b + ": " + r);
        }

        /// <summary>Python int(text, base) for base 0, 2, 8, 10 or 16 (exact, any size)</summary>
        public static BigInteger Int(string s, int b = 10)
        {
            string t = Strip(s);
            int i = 0;
            bool neg = false;
            if (i < t.Length && (t[i] == '+' || t[i] == '-')) { neg = t[i] == '-'; i++; }
            int bas = b;
            bool prefixed = false;
            if (i + 1 < t.Length && t[i] == '0')
            {
                char p = char.ToLowerInvariant(t[i + 1]);
                if ((b == 0 || b == 16) && p == 'x') { bas = 16; prefixed = true; }
                else if ((b == 0 || b == 8) && p == 'o') { bas = 8; prefixed = true; }
                else if ((b == 0 || b == 2) && p == 'b') { bas = 2; prefixed = true; }
            }
            if (prefixed) i += 2;
            if (bas == 0) bas = 10;
            BigInteger v = BigInteger.Zero;
            int ndig = 0, firstDigit = -1;
            bool lastUnderscore = false;
            for (; i < t.Length; i++)
            {
                char c = t[i];
                if (c == '_')
                {
                    // one underscore between digits, or right after a base prefix
                    if (lastUnderscore || (ndig == 0 && !prefixed)) throw IntError(s, b);
                    lastUnderscore = true;
                    continue;
                }
                int d = DigitValue(c);
                if (d >= bas) throw IntError(s, b);
                if (ndig == 0) firstDigit = d;
                v = v * bas + d;
                ndig++;
                lastUnderscore = false;
            }
            if (ndig == 0 || lastUnderscore) throw IntError(s, b);
            // base 0: a decimal number may not start with 0 unless it is zero
            if (b == 0 && !prefixed && firstDigit == 0 && !v.IsZero) throw IntError(s, b);
            return neg ? -v : v;
        }

        /// <summary>Python math.floor(x) as a double (an integer in Python, so a zero result is +0)</summary>
        public static double Floor(double x) { double r = Math.Floor(x); return r == 0 ? 0.0 : r; }

        /// <summary>Python a % b for floats: the remainder has the sign of b (CPython float_rem)</summary>
        public static double FMod(double a, double b)
        {
            if (b == 0) throw new DivideByZeroException("float modulo");
            double mod = a % b;          // C# % on doubles is C fmod (exact)
            if (mod != 0)
            {
                if ((b < 0) != (mod < 0)) mod += b;
            }
            else mod = b < 0 || (b == 0 && BitConverter.DoubleToInt64Bits(b) < 0) ? -0.0 : 0.0;
            return mod;
        }

        public static uint Mask32(BigInteger v) { return (uint)(v & uint.MaxValue); }
        public static ulong Mask64(BigInteger v) { return (ulong)(v & ulong.MaxValue); }

        /// <summary>Python int(text, 0) that must fit a long (OverflowException otherwise)</summary>
        public static long ParseInt(string s)
        {
            BigInteger v = Int(s, 0);
            if (v < long.MinValue || v > long.MaxValue) throw new OverflowException("Python int too large to convert to C long");
            return (long)v;
        }

        private static readonly System.Text.RegularExpressions.Regex FloatRe = new System.Text.RegularExpressions.Regex(
            @"^[+-]?(?:(?:[0-9](?:_?[0-9])*)?\.[0-9](?:_?[0-9])*|[0-9](?:_?[0-9])*\.?)(?:[eE][+-]?[0-9](?:_?[0-9])*)?$",
            System.Text.RegularExpressions.RegexOptions.CultureInvariant);

        /// <summary>Python float(text)</summary>
        public static double ParseFloat(string s)
        {
            string t = Strip(s);
            StringBuilder norm = null;
            for (int i = 0; i < t.Length; i++)
            {
                char c = t[i];
                if (c > 0x7F && char.IsDigit(c))
                {
                    if (norm == null) norm = new StringBuilder(t.Substring(0, i));
                    norm.Append((char)('0' + (int)char.GetNumericValue(c)));
                }
                else if (norm != null) norm.Append(c);
            }
            if (norm != null) t = norm.ToString();
            string l = t.ToLowerInvariant();
            if (l == "nan" || l == "+nan") return Num.NaN;
            if (l == "-nan") return -Num.NaN;
            if (l == "inf" || l == "+inf" || l == "infinity" || l == "+infinity") return double.PositiveInfinity;
            if (l == "-inf" || l == "-infinity") return double.NegativeInfinity;
            if (!FloatRe.IsMatch(t)) throw new FormatException("could not convert string to float: " + Repr(s));
            t = t.Replace("_", "");
            double d = double.Parse(t, NumberStyles.Float, CultureInfo.InvariantCulture);
            if (d == 0 && t.StartsWith("-")) return -0.0;
            return d;
        }

        /// <summary>struct.pack("&lt;f", x): the float32 bits (OverflowException when a finite x is too large, like Python)</summary>
        public static uint PackF32(double x)
        {
            float f = (float)x;
            if (float.IsInfinity(f) && !double.IsInfinity(x)) throw new OverflowException("float too large to pack with f format");
            return BE.FloatBits(f);
        }

        /// <summary>Python bytes.fromhex(text)</summary>
        public static byte[] FromHex(string s)
        {
            List<byte> r = new List<byte>();
            int i = 0;
            while (i < s.Length)
            {
                char c = s[i];
                if (c == ' ' || c == '\t' || c == '\n' || c == '\r' || c == '\x0b' || c == '\x0c') { i++; continue; }
                int hi = DigitValue(c);
                if (hi >= 16 || c > 0x7F) throw new FormatException("non-hexadecimal number found in fromhex() arg at position " + i);
                if (i + 1 >= s.Length) throw new FormatException("non-hexadecimal number found in fromhex() arg at position " + (i + 1));
                int lo = DigitValue(s[i + 1]);
                if (lo >= 16 || s[i + 1] > 0x7F) throw new FormatException("non-hexadecimal number found in fromhex() arg at position " + (i + 1));
                r.Add((byte)(hi * 16 + lo));
                i += 2;
            }
            return r.ToArray();
        }

        /// <summary>Python json.dumps(str) (ensure_ascii)</summary>
        public static string JsonStr(string s)
        {
            StringBuilder sb = new StringBuilder("\"");
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    default:
                        if (c < 0x20 || c > 0x7E && c != 0x7F) sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            return sb.Append('"').ToString();
        }

        /// <summary>Python "%-Ns" % s</summary>
        public static string L(string s, int n) { return s.Length >= n ? s : s.PadRight(n); }
    }
}
