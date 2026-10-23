using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace FrostyAnimationEditor.Core
{
    /// <summary>A JSON object that keeps its key order.</summary>
    public sealed class JObject : IEnumerable<KeyValuePair<string, object>>
    {
        private readonly List<string> keys = new List<string>();
        private readonly Dictionary<string, object> map = new Dictionary<string, object>();

        public int Count { get { return keys.Count; } }
        public IList<string> Keys { get { return keys; } }

        public object this[string k]
        {
            get { object v; return map.TryGetValue(k, out v) ? v : null; }
            set
            {
                if (!map.ContainsKey(k)) keys.Add(k);
                map[k] = value;
            }
        }

        public bool ContainsKey(string k) { return map.ContainsKey(k); }
        public bool TryGetValue(string k, out object v) { return map.TryGetValue(k, out v); }

        public bool Remove(string k)
        {
            if (!map.Remove(k)) return false;
            keys.Remove(k);
            return true;
        }

        public IEnumerator<KeyValuePair<string, object>> GetEnumerator()
        {
            foreach (string k in keys) yield return new KeyValuePair<string, object>(k, map[k]);
        }

        IEnumerator IEnumerable.GetEnumerator() { return GetEnumerator(); }

        public string Str(string k) { return this[k] as string; }
        public long Long(string k, long def = 0) { object v = this[k]; return v == null ? def : Num.ToLong(v); }
        public double Dbl(string k, double def = 0) { object v = this[k]; return v == null ? def : Num.ToDouble(v); }
        public bool Bool(string k, bool def = false) { object v = this[k]; return v == null ? def : Num.ToBool(v); }
        public List<object> Arr(string k) { return this[k] as List<object>; }
        public JObject Obj(string k) { return this[k] as JObject; }
    }

    /// <summary>
    /// Minimal JSON reader/writer.  Numbers: integers -> long (ulong above long.MaxValue), others -> double.
    /// Objects -> JObject (ordered), arrays -> List&lt;object&gt;.
    /// </summary>
    public static class MiniJson
    {
        public static object Parse(string s)
        {
            int i = 0;
            object v = Value(s, ref i);
            Ws(s, ref i);
            if (i != s.Length) throw Err(s, i, "trailing characters");
            return v;
        }

        public static object ParseFile(string path) { return Parse(File.ReadAllText(path, Encoding.UTF8)); }

        private static FormatException Err(string s, int i, string msg)
        {
            int line = 1, col = 1;
            for (int k = 0; k < i && k < s.Length; k++) { if (s[k] == '\n') { line++; col = 1; } else col++; }
            return new FormatException("JSON: " + msg + " at line " + line + " column " + col);
        }

        private static void Ws(string s, ref int i)
        {
            while (i < s.Length && (s[i] == ' ' || s[i] == '\t' || s[i] == '\n' || s[i] == '\r')) i++;
        }

        private static object Value(string s, ref int i)
        {
            Ws(s, ref i);
            if (i >= s.Length) throw Err(s, i, "unexpected end");
            char c = s[i];
            if (c == '{')
            {
                JObject o = new JObject();
                i++;
                Ws(s, ref i);
                if (i < s.Length && s[i] == '}') { i++; return o; }
                while (true)
                {
                    Ws(s, ref i);
                    if (i >= s.Length || s[i] != '"') throw Err(s, i, "expected a key");
                    string k = String(s, ref i);
                    Ws(s, ref i);
                    if (i >= s.Length || s[i] != ':') throw Err(s, i, "expected ':'");
                    i++;
                    o[k] = Value(s, ref i);
                    Ws(s, ref i);
                    if (i < s.Length && s[i] == ',') { i++; continue; }
                    if (i < s.Length && s[i] == '}') { i++; return o; }
                    throw Err(s, i, "expected ',' or '}'");
                }
            }
            if (c == '[')
            {
                List<object> a = new List<object>();
                i++;
                Ws(s, ref i);
                if (i < s.Length && s[i] == ']') { i++; return a; }
                while (true)
                {
                    a.Add(Value(s, ref i));
                    Ws(s, ref i);
                    if (i < s.Length && s[i] == ',') { i++; continue; }
                    if (i < s.Length && s[i] == ']') { i++; return a; }
                    throw Err(s, i, "expected ',' or ']'");
                }
            }
            if (c == '"') return String(s, ref i);
            if (Lit(s, ref i, "true")) return true;
            if (Lit(s, ref i, "false")) return false;
            if (Lit(s, ref i, "null")) return null;
            if (Lit(s, ref i, "NaN")) return Num.NaN;
            if (Lit(s, ref i, "Infinity")) return double.PositiveInfinity;
            if (Lit(s, ref i, "-Infinity")) return double.NegativeInfinity;
            return Number(s, ref i);
        }

        private static bool Lit(string s, ref int i, string lit)
        {
            if (string.CompareOrdinal(s, i, lit, 0, lit.Length) == 0)
            {
                i += lit.Length;
                return true;
            }
            return false;
        }

        private static object Number(string s, ref int i)
        {
            int st = i;
            if (i < s.Length && (s[i] == '-' || s[i] == '+')) i++;
            bool isInt = true;
            while (i < s.Length)
            {
                char c = s[i];
                if (c >= '0' && c <= '9') { i++; continue; }
                if (c == '.' || c == 'e' || c == 'E' || c == '+' || c == '-') { isInt = false; i++; continue; }
                break;
            }
            string t = s.Substring(st, i - st);
            if (t.Length == 0 || t == "-") throw Err(s, st, "bad value");
            if (isInt && t[0] == '-' && t.TrimStart('-', '0').Length == 0) return -0.0;   // keep the sign of zero
            if (isInt)
            {
                long l;
                if (long.TryParse(t, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out l)) return l;
                ulong u;
                if (ulong.TryParse(t, NumberStyles.None, CultureInfo.InvariantCulture, out u)) return u;
            }
            double d;
            if (!double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out d)) throw Err(s, st, "bad number " + t);
            if (d == 0 && t[0] == '-') return -0.0;      // .NET Framework parses "-0.0" as +0
            return d;
        }

        private static string String(string s, ref int i)
        {
            i++;
            StringBuilder sb = new StringBuilder();
            while (true)
            {
                if (i >= s.Length) throw Err(s, i, "unterminated string");
                char c = s[i++];
                if (c == '"') break;
                if (c != '\\') { sb.Append(c); continue; }
                if (i >= s.Length) throw Err(s, i, "bad escape");
                char e = s[i++];
                switch (e)
                {
                    case '"': sb.Append('"'); break;
                    case '\\': sb.Append('\\'); break;
                    case '/': sb.Append('/'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    case 'u':
                        if (i + 4 > s.Length) throw Err(s, i, "bad \\u escape");
                        sb.Append((char)Convert.ToInt32(s.Substring(i, 4), 16));
                        i += 4;
                        break;
                    default: throw Err(s, i, "bad escape \\" + e);
                }
            }
            return sb.ToString();
        }

        // ------------------------------------------------------------------------------ writer
        public static string Serialize(object v, int indent = 1)
        {
            StringBuilder sb = new StringBuilder();
            Write(sb, v, indent, 0);
            return sb.ToString();
        }

        /// <summary>shortest text that reads back to the same float32, in Python repr style ("16.0", "1e-05")</summary>
        public static string FloatText(float f)
        {
            if (float.IsNaN(f) || float.IsInfinity(f)) return DoubleText(f);
            if (f == 0) return BitConverter.DoubleToInt64Bits(f) < 0 ? "-0.0" : "0.0";
            string s = null;
            for (int p = Math.Abs(f) < 1.17549435e-38f ? 1 : 6; p <= 9; p++)       // subnormals have fewer significant digits
            {
                s = f.ToString("G" + p, CultureInfo.InvariantCulture);
                float back;
                if (float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out back) && back == f) break;
            }
            return PyStyle(s);
        }

        /// <summary>shortest text that reads back to the same double, in Python repr style</summary>
        public static string DoubleText(double d)
        {
            if (double.IsNaN(d)) return "NaN";
            if (double.IsPositiveInfinity(d)) return "Infinity";
            if (double.IsNegativeInfinity(d)) return "-Infinity";
            if (d == 0) return BitConverter.DoubleToInt64Bits(d) < 0 ? "-0.0" : "0.0";
            string s = null;
            for (int p = 15; p <= 17; p++)
            {
                s = d.ToString("G" + p, CultureInfo.InvariantCulture);
                double back;
                if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out back) && back == d) break;
            }
            return PyStyle(s);
        }

        /// <summary>re-lays a .NET "G" number text the way Python's float repr does: positional for decimal exponents
        /// -4..15 (always with a fraction, "16.0"), otherwise d.ddde+XX</summary>
        public static string PyStyle(string g)
        {
            bool neg = g.StartsWith("-");
            if (neg) g = g.Substring(1);
            int e = 0;
            int ei = g.IndexOfAny(new[] { 'E', 'e' });
            if (ei >= 0)
            {
                e = int.Parse(g.Substring(ei + 1), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
                g = g.Substring(0, ei);
            }
            int dot = g.IndexOf('.');
            string ip = dot >= 0 ? g.Substring(0, dot) : g, fp = dot >= 0 ? g.Substring(dot + 1) : "";
            string digits = ip + fp;
            int exp10 = ip.Length - 1 + e;               // decimal exponent of digits[0]
            int lead = 0;
            while (lead < digits.Length - 1 && digits[lead] == '0') { lead++; exp10--; }
            digits = digits.Substring(lead).TrimEnd('0');
            if (digits.Length == 0) digits = "0";
            StringBuilder sb = new StringBuilder(24);
            if (neg) sb.Append('-');
            if (exp10 >= -4 && exp10 < 16)
            {
                if (exp10 >= 0)
                {
                    if (digits.Length <= exp10 + 1) sb.Append(digits).Append('0', exp10 + 1 - digits.Length).Append(".0");
                    else sb.Append(digits, 0, exp10 + 1).Append('.').Append(digits, exp10 + 1, digits.Length - exp10 - 1);
                }
                else sb.Append("0.").Append('0', -exp10 - 1).Append(digits);
            }
            else
            {
                sb.Append(digits[0]);
                if (digits.Length > 1) sb.Append('.').Append(digits, 1, digits.Length - 1);
                sb.Append('e').Append(exp10 < 0 ? '-' : '+').Append(Math.Abs(exp10).ToString("00", CultureInfo.InvariantCulture));
            }
            return sb.ToString();
        }

        private static void Nl(StringBuilder sb, int indent, int depth)
        {
            if (indent <= 0) return;
            sb.Append('\n');
            sb.Append(' ', indent * depth);
        }

        private static bool IsScalarList(IList l)
        {
            foreach (object e in l) if (e is JObject || (e is IList && !(e is string)) || e is IDictionary) return false;
            return true;
        }

        public static void Write(StringBuilder sb, object v, int indent, int depth)
        {
            if (v == null) { sb.Append("null"); return; }
            if (v is string) { Quote(sb, (string)v); return; }
            if (v is bool) { sb.Append((bool)v ? "true" : "false"); return; }
            if (v is float) { float f = (float)v; sb.Append(float.IsNaN(f) || float.IsInfinity(f) ? DoubleText(f) : FloatText(f)); return; }
            if (v is double) { sb.Append(DoubleText((double)v)); return; }
            if (v is long || v is int || v is short || v is byte || v is sbyte || v is ushort || v is uint || v is ulong)
            {
                sb.Append(Convert.ToString(v, CultureInfo.InvariantCulture));
                return;
            }
            JObject o = v as JObject;
            if (o != null)
            {
                if (o.Count == 0) { sb.Append("{}"); return; }
                sb.Append('{');
                bool first = true;
                foreach (KeyValuePair<string, object> kv in o)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    Nl(sb, indent, depth + 1);
                    Quote(sb, kv.Key);
                    sb.Append(indent > 0 ? ": " : ":");
                    Write(sb, kv.Value, indent, depth + 1);
                }
                Nl(sb, indent, depth);
                sb.Append('}');
                return;
            }
            IDictionary dict = v as IDictionary;
            if (dict != null)
            {
                JObject j = new JObject();
                foreach (DictionaryEntry de in dict) j[Convert.ToString(de.Key, CultureInfo.InvariantCulture)] = de.Value;
                Write(sb, j, indent, depth);
                return;
            }
            IEnumerable en = v as IEnumerable;
            if (en != null)
            {
                IList l = en as IList ?? new List<object>(Cast(en));
                if (l.Count == 0) { sb.Append("[]"); return; }
                bool flat = IsScalarList(l);
                sb.Append('[');
                for (int k = 0; k < l.Count; k++)
                {
                    if (k > 0) sb.Append(flat && indent > 0 ? ", " : ",");
                    if (!flat) Nl(sb, indent, depth + 1);
                    Write(sb, l[k], indent, depth + 1);
                }
                if (!flat) Nl(sb, indent, depth);
                sb.Append(']');
                return;
            }
            Quote(sb, v.ToString());
        }

        private static IEnumerable<object> Cast(IEnumerable e) { foreach (object x in e) yield return x; }

        public static void Quote(StringBuilder sb, string s)
        {
            sb.Append('"');
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
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }
    }
}
