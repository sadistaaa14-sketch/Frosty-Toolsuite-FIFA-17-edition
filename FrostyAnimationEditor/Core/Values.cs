using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;

namespace FrostyAnimationEditor.Core
{
    /// <summary>
    /// A value of an embedded (non-object) struct type, e.g. a list element or a struct field.
    /// Field values (by field index):
    ///   Key -> ulong; Bool -> bool; integer primitives -> long; Float -> float;
    ///   Vector2/3/4, Quaternion -> float[4]; Matrix44 -> float[16];
    ///   String -> string | RawStr | null; DataRef -> GdObj | null; struct -> GdStruct;
    ///   fixed arrays (count != 1) -> object[]; blobs of unknown type -> byte[];
    ///   lists: UInt8/Int8 -> byte[]; Bool -> List&lt;bool&gt;; Int16..Int64 -> List&lt;long&gt;;
    ///   Float -> List&lt;float&gt;; Key -> List&lt;ulong&gt;; DataRef -> List&lt;GdObj&gt;;
    ///   String -> List&lt;object&gt;; vectors/matrices -> List&lt;float[]&gt;; structs -> List&lt;GdStruct&gt;;
    ///   an empty list whose blob has a non-zero cap/pointer -> RawList.
    /// </summary>
    public class GdStruct
    {
        public TypeDesc Type;
        public object[] Values;

        public GdStruct(TypeDesc type)
        {
            Type = type;
            Values = new object[type.Fields.Count];
        }

        public object this[string name]
        {
            get
            {
                int i;
                return Type.IndexOf.TryGetValue(name, out i) ? Values[i] : null;
            }
            set
            {
                int i;
                if (!Type.IndexOf.TryGetValue(name, out i))
                    throw new KeyNotFoundException(Type.Name + " has no field " + name);
                Values[i] = value;
            }
        }

        public object Get(string name, object def = null)
        {
            int i;
            if (!Type.IndexOf.TryGetValue(name, out i)) return def;
            return Values[i] ?? def;
        }

        public bool Has(string name) { return Type.IndexOf.ContainsKey(name); }

        public IEnumerable<KeyValuePair<string, object>> Items()
        {
            for (int i = 0; i < Values.Length; i++)
                yield return new KeyValuePair<string, object>(Type.Fields[i].Name, Values[i]);
        }

        // ---- typed accessors (null/absent -> default) ----
        public ulong GetKey(string name) { return Num.ToKey(this[name]); }
        public long GetInt(string name, long def = 0) { object v = this[name]; return v == null ? def : Num.ToLong(v); }
        public float GetFloat(string name, float def = 0f) { object v = this[name]; return v == null ? def : Num.ToFloat(v); }
        public bool GetBool(string name) { object v = this[name]; return v != null && Num.ToBool(v); }
        public string GetString(string name) { return Str.Text(this[name]); }
        public GdObj GetRef(string name) { return this[name] as GdObj; }
        public GdStruct GetStruct(string name) { return this[name] as GdStruct; }
        public IList GetList(string name) { return this[name] as IList; }
        public byte[] GetBytes(string name) { return this[name] as byte[]; }

        public List<T> GetList<T>(string name)
        {
            object v = this[name];
            List<T> l = v as List<T>;
            if (l != null) return l;
            return new List<T>();
        }

        public int ListLen(string name)
        {
            object v = this[name];
            if (v is byte[]) return ((byte[])v).Length;
            if (v is ICollection) return ((ICollection)v).Count;
            if (v is string) return ((string)v).Length;
            return 0;
        }

        public override string ToString() { return "<" + Type.Name + ">"; }
    }

    /// <summary>A GD object (root or nested): a struct with the 0x20-byte object header.</summary>
    public sealed class GdObj : GdStruct
    {
        /// <summary>raw header if it is not the standard one (never seen in the shipped file)</summary>
        public byte[] HdrExtra;

        public GdObj(TypeDesc type, byte[] hdrExtra = null) : base(type) { HdrExtra = hdrExtra; }

        public ulong Key
        {
            get { return Num.ToKey(this["__key"]); }
            set { this["__key"] = value; }
        }

        public string Name
        {
            get
            {
                object n = this["__name"];
                if (n is string) return (string)n;
                RawStr r = n as RawStr;
                if (r != null) return BE.Latin1.GetString(r.Raw);
                return "";
            }
        }

        public GdObj Base { get { return this["__base"] as GdObj; } }

        /// <summary>this object and its __base chain (derived first)</summary>
        public IEnumerable<GdObj> Chain()
        {
            GdObj o = this;
            while (o != null)
            {
                yield return o;
                o = o["__base"] as GdObj;
            }
        }

        /// <summary>looks a field up through the __base chain; returns the owner (null if absent)</summary>
        public GdObj FindOwner(string field)
        {
            foreach (GdObj o in Chain())
                if (o.Type.ByName.ContainsKey(field) && field != "__name" && field != "__key" && field != "__base")
                    return o;
            return null;
        }

        public object GetInherited(string field, object def = null)
        {
            GdObj o = FindOwner(field);
            return o == null ? def : o[field];
        }

        public void SetInherited(string field, object value)
        {
            GdObj o = FindOwner(field);
            if (o == null) throw new KeyNotFoundException(field);
            o[field] = value;
        }

        public bool HasInherited(string field) { return FindOwner(field) != null; }

        /// <summary>[(owner type, field, value)] of this object and its bases</summary>
        public List<Tuple<string, string, object>> FlatFields()
        {
            List<Tuple<string, string, object>> r = new List<Tuple<string, string, object>>();
            foreach (GdObj o in Chain())
                for (int i = 0; i < o.Type.Fields.Count; i++)
                {
                    Field f = o.Type.Fields[i];
                    if (f.IsImplicit) continue;
                    r.Add(Tuple.Create(o.Type.Name, f.Name, o.Values[i]));
                }
            return r;
        }

        public override string ToString() { return "<Obj " + Type.Name + " '" + Name + "' key=" + Key.ToString("x16") + ">"; }
    }

    /// <summary>A string blob that is not plain 'text + NUL' (kept byte-exact).</summary>
    public sealed class RawStr
    {
        public byte[] Raw;
        public uint Cap;
        public ulong? Ptr0;

        public RawStr(byte[] raw, uint cap, ulong? ptr0) { Raw = raw; Cap = cap; Ptr0 = ptr0; }
        public override string ToString() { return BE.Latin1.GetString(Raw); }
    }

    /// <summary>An empty list whose blob has a non-zero cap/pointer (kept byte-exact).</summary>
    public sealed class RawList : List<object>
    {
        public uint Cap;
        public ulong Ptr0;
        public RawList(uint cap, ulong ptr0) { Cap = cap; Ptr0 = ptr0; }
    }

    /// <summary>numeric conversions with Python-like leniency</summary>
    public static class Num
    {
        /// <summary>the NaN Python's float('nan') / json "NaN" give (positive quiet NaN); .NET's double.NaN has the sign bit set</summary>
        public static readonly double NaN = BitConverter.Int64BitsToDouble(0x7FF8000000000000);
        public static readonly float NaNf = BitConverter.ToSingle(BitConverter.GetBytes(0x7FC00000), 0);

        public static ulong ToKey(object v)
        {
            if (v == null) return 0;
            if (v is ulong) return (ulong)v;
            if (v is long) return (ulong)(long)v;
            if (v is int) return (ulong)(long)(int)v;
            if (v is uint) return (uint)v;
            if (v is string) return BE.ParseKey((string)v);
            return Convert.ToUInt64(v, CultureInfo.InvariantCulture);
        }

        public static long ToLong(object v)
        {
            if (v == null) return 0;
            if (v is long) return (long)v;
            if (v is int) return (int)v;
            if (v is ulong) return (long)(ulong)v;
            if (v is uint) return (uint)v;
            if (v is short) return (short)v;
            if (v is ushort) return (ushort)v;
            if (v is byte) return (byte)v;
            if (v is sbyte) return (sbyte)v;
            if (v is bool) return (bool)v ? 1 : 0;
            if (v is float) return (long)(float)v;
            if (v is double) return (long)(double)v;
            if (v is string)
            {
                string s = ((string)v).Trim();
                if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) return (long)Convert.ToUInt64(s.Substring(2), 16);
                return long.Parse(s, CultureInfo.InvariantCulture);
            }
            return Convert.ToInt64(v, CultureInfo.InvariantCulture);
        }

        public static float ToFloat(object v)
        {
            if (v == null) return 0f;
            if (v is float) return (float)v;
            if (v is double) return (float)(double)v;
            if (v is string) return (float)double.Parse((string)v, CultureInfo.InvariantCulture);
            if (v is bool) return (bool)v ? 1f : 0f;
            return (float)Convert.ToDouble(v, CultureInfo.InvariantCulture);
        }

        public static double ToDouble(object v)
        {
            if (v == null) return 0.0;
            if (v is float) return (float)v;
            if (v is double) return (double)v;
            if (v is string) return double.Parse((string)v, CultureInfo.InvariantCulture);
            if (v is bool) return (bool)v ? 1.0 : 0.0;
            return Convert.ToDouble(v, CultureInfo.InvariantCulture);
        }

        public static bool ToBool(object v)
        {
            if (v == null) return false;
            if (v is bool) return (bool)v;
            if (v is string)
            {
                string s = ((string)v).Trim().ToLowerInvariant();
                return s == "true" || s == "1" || s == "yes";
            }
            return ToLong(v) != 0;
        }

        /// <summary>wrap an integer to the range of a primitive type (Python's struct.pack would raise; the UI checks first)</summary>
        public static bool InRange(uint ftype, long v)
        {
            switch (ftype)
            {
                case TypeIds.Int8: return v >= sbyte.MinValue && v <= sbyte.MaxValue;
                case TypeIds.UInt8: return v >= 0 && v <= byte.MaxValue;
                case TypeIds.Int16: return v >= short.MinValue && v <= short.MaxValue;
                case TypeIds.UInt16: return v >= 0 && v <= ushort.MaxValue;
                case TypeIds.Int32: return v >= int.MinValue && v <= int.MaxValue;
                case TypeIds.UInt32: return v >= 0 && v <= uint.MaxValue;
            }
            return true;
        }
    }

    public static class Str
    {
        /// <summary>text of a string value (string, RawStr, byte[]) or null</summary>
        public static string Text(object v)
        {
            if (v == null) return null;
            if (v is string) return (string)v;
            RawStr r = v as RawStr;
            if (r != null) return BE.Latin1.GetString(r.Raw);
            byte[] b = v as byte[];
            if (b != null) return BE.Latin1.GetString(b);
            return v.ToString();
        }

        /// <summary>Python's len(s.encode("utf-8", "replace")) for a string value</summary>
        public static int Utf8Len(object v)
        {
            if (v == null) return 0;
            if (v is string) return System.Text.Encoding.UTF8.GetByteCount((string)v);
            RawStr r = v as RawStr;
            if (r != null) return r.Raw.Length;
            byte[] b = v as byte[];
            if (b != null) return b.Length;
            return 0;
        }
    }
}
