using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;

namespace FrostyAnimationEditor.Core
{
    /// <summary>
    /// Lossless JSON view of objects (edit any object and its nested objects as text):
    ///   {"$type": "ClipControllerAsset", "__key": "0x7775f2d7ca271d73", "__name": "F17_D_404St005_None_02",
    ///    "__base": {"$type": "ControllerAsset", ...}, "Anim": "0xbe6d2afd06ef2473", "FPS": 30.0, ...}
    ///   * Key fields -> "0x%016x" strings; Bool/ints/floats -> JSON scalars (NaN/Inf as "nan"/"inf"/"-inf")
    ///   * Strings -> JSON strings (null = no string; odd blobs {"$raw": hex}); UInt8/Int8 lists -> {"$bytes": hex}
    ///   * embedded structs / list elements -> objects with "$type"; DataRef -> nested object or null
    ///   * vectors/quaternions -> 4 floats, Matrix44 -> 16 floats
    /// </summary>
    public static class JsonIO
    {
        public static object F(float x)
        {
            if (float.IsNaN(x)) return "nan";
            if (float.IsPositiveInfinity(x)) return "inf";
            if (float.IsNegativeInfinity(x)) return "-inf";
            return x;
        }

        private static float UF(object x)
        {
            string s = x as string;
            if (s != null)
            {
                if (s == "nan") return Num.NaNf;
                if (s == "inf") return float.PositiveInfinity;
                if (s == "-inf") return float.NegativeInfinity;
                return (float)PyFormat.ParseFloat(s);
            }
            return Num.ToFloat(x);
        }

        public static JObject ToJson(GdStruct v)
        {
            JObject d = new JObject();
            d["$type"] = v.Type.Name;
            GdObj o = v as GdObj;
            if (o != null && o.HdrExtra != null) d["$hdr"] = BE.Hex(o.HdrExtra);
            for (int i = 0; i < v.Type.Fields.Count; i++)
            {
                Field f = v.Type.Fields[i];
                d[f.Name] = FieldToJson(f, v.Values[i]);
            }
            return d;
        }

        private static List<object> Floats(object v)
        {
            List<object> r = new List<object>();
            foreach (object a in (IList)v) r.Add(F(Num.ToFloat(a)));
            return r;
        }

        public static object FieldToJson(Field f, object x)
        {
            if (x == null) return null;
            uint ft = f.FType;
            if (f.IsList)
            {
                byte[] bytes = x as byte[];
                if (bytes != null) { JObject j = new JObject(); j["$bytes"] = BE.Hex(bytes); return j; }
                IList l = (IList)x;
                List<object> r = new List<object>(l.Count);
                if (ft == TypeIds.Key) { foreach (object k in l) r.Add(BE.KeyHex(Num.ToKey(k))); return r; }
                if (ft == TypeIds.DataRef) { foreach (object o in l) r.Add(o != null ? ToJson((GdStruct)o) : null); return r; }
                if (ft == TypeIds.String) { foreach (object s in l) r.Add(StrJson(s)); return r; }
                if (TypeIds.IsVector(ft) || ft == TypeIds.Matrix44) { foreach (object e in l) r.Add(Floats(e)); return r; }
                if (l.Count > 0 && l[0] is GdStruct) { foreach (object e in l) r.Add(e != null ? ToJson((GdStruct)e) : null); return r; }
                foreach (object e in l) r.Add(e is float ? F((float)e) : e);
                return r;
            }
            if (f.Count != 1 && f.Size != 0 && x is IList && !(x is float[]) && !(x is byte[]))
            {
                List<object> r = new List<object>();
                foreach (object e in (IList)x) r.Add(FieldScalar(f, e));
                return r;
            }
            return FieldScalar(f, x);
        }

        private static object StrJson(object s)
        {
            if (s == null) return null;
            if (s is string) return s;
            RawStr r = s as RawStr;
            JObject j = new JObject();
            j["$raw"] = BE.Hex(r != null ? r.Raw : (byte[])s);
            return j;
        }

        public static object FieldScalar(Field f, object x)
        {
            if (x == null) return null;
            uint ft = f.FType;
            if (ft == TypeIds.Key) return BE.KeyHex(Num.ToKey(x));
            if (ft == TypeIds.DataRef) return ToJson((GdStruct)x);
            if (ft == TypeIds.String) return StrJson(x);
            if (TypeIds.IsVector(ft) || ft == TypeIds.Matrix44) return Floats(x);
            if (x is GdStruct) return ToJson((GdStruct)x);
            if (x is byte[]) { JObject j = new JObject(); j["$raw"] = BE.Hex((byte[])x); return j; }
            if (x is float) return F((float)x);
            return x;
        }

        // ------------------------------------------------------------------------------ from JSON
        public static GdObj FromJson(JObject d, TypeTable types)
        {
            return (GdObj)Struct(d, types, true, null);
        }

        private static GdStruct Struct(JObject d, TypeTable types, bool top, bool? isObj)
        {
            string tn = d.Str("$type");
            TypeDesc t = types.Get(tn);
            if (t == null) throw new FormatException("unknown type " + (tn ?? "(no $type)"));
            bool io = isObj ?? (top || t.IsClass);
            GdStruct o;
            if (io)
            {
                string hdr = d.Str("$hdr");
                o = new GdObj(t, string.IsNullOrEmpty(hdr) ? null : BE.FromHex(hdr));
            }
            else o = new GdStruct(t);
            for (int i = 0; i < t.Fields.Count; i++)
            {
                Field f = t.Fields[i];
                if (d.ContainsKey(f.Name)) o.Values[i] = FieldFromJson(f, d[f.Name], types);
                else o.Values[i] = Blank.Value(types, f);
            }
            return o;
        }

        private static object StrFrom(object e)
        {
            if (e == null) return null;
            JObject j = e as JObject;
            if (j != null && j.ContainsKey("$raw"))
            {
                byte[] raw = BE.FromHex(j.Str("$raw"));
                return new RawStr(raw, (uint)raw.Length, null);
            }
            if (e is string) return e;
            return Convert.ToString(e, CultureInfo.InvariantCulture);
        }

        private static float[] FloatsFrom(object x, int n)
        {
            IList l = (IList)x;
            float[] r = new float[n];
            for (int i = 0; i < n && i < l.Count; i++) r[i] = UF(l[i]);
            return r;
        }

        private static long IntFrom(object x)
        {
            if (x is double)
            {
                double d = (double)x;
                if (d != Math.Floor(d)) throw new FormatException("expected an integer, got " + d.ToString(CultureInfo.InvariantCulture));
                return (long)d;
            }
            if (x is bool) return (bool)x ? 1 : 0;
            return Num.ToLong(x);
        }

        public static object FieldFromJson(Field f, object x, TypeTable types)
        {
            uint ft = f.FType;
            if (x == null) return f.IsList ? ObjCodec.EmptyList(f) : null;
            if (f.IsList)
            {
                JObject jb = x as JObject;
                if (jb != null && jb.ContainsKey("$bytes")) return BE.FromHex(jb.Str("$bytes"));
                IList l = x as IList;
                if (l == null) throw new FormatException(f.Name + ": expected a list");
                if (ft == TypeIds.Key) { List<ulong> r = new List<ulong>(); foreach (object k in l) r.Add(Num.ToKey(k)); return r; }
                if (ft == TypeIds.DataRef)
                {
                    List<GdObj> r = new List<GdObj>();
                    foreach (object e in l) r.Add(e != null ? (GdObj)Struct((JObject)e, types, false, true) : null);
                    return r;
                }
                if (ft == TypeIds.String) { List<object> r = new List<object>(); foreach (object e in l) r.Add(StrFrom(e)); return r; }
                if (TypeIds.IsVector(ft) || ft == TypeIds.Matrix44)
                {
                    List<float[]> r = new List<float[]>();
                    foreach (object e in l) r.Add(FloatsFrom(e, ft == TypeIds.Matrix44 ? 16 : 4));
                    return r;
                }
                if ((ft == TypeIds.UInt8 || ft == TypeIds.Int8) && !(f.Type != null && f.Type.Fields.Count != 0))
                {
                    byte[] r = new byte[l.Count];
                    for (int k = 0; k < l.Count; k++) r[k] = (byte)(IntFrom(l[k]) & 0xFF);
                    return r;
                }
                if (l.Count > 0 && l[0] is JObject)
                {
                    List<GdStruct> r = new List<GdStruct>();
                    foreach (object e in l) r.Add(Struct((JObject)e, types, false, false));
                    return r;
                }
                if (ft == TypeIds.Float) { List<float> r = new List<float>(); foreach (object e in l) r.Add(UF(e)); return r; }
                if (ft == TypeIds.Bool) { List<bool> r = new List<bool>(); foreach (object e in l) r.Add(Num.ToBool(e)); return r; }
                if (TypeIds.IsPrim(ft)) { List<long> r = new List<long>(); foreach (object e in l) r.Add(IntFrom(e)); return r; }
                if (l.Count == 0) return ObjCodec.EmptyList(f);
                throw new FormatException(f.Name + ": cannot read list elements");
            }
            if (f.Count != 1 && f.Size != 0 && x is IList)
            {
                IList l = (IList)x;
                object[] r = new object[l.Count];
                for (int k = 0; k < l.Count; k++) r[k] = ScalarFromJson(f, l[k], types);
                return r;
            }
            return ScalarFromJson(f, x, types);
        }

        public static object ScalarFromJson(Field f, object x, TypeTable types)
        {
            uint ft = f.FType;
            if (x == null) return null;
            if (ft == TypeIds.Key) return Num.ToKey(x);
            if (ft == TypeIds.DataRef) return Struct((JObject)x, types, false, true);
            if (ft == TypeIds.String) return StrFrom(x);
            if (TypeIds.IsVector(ft)) return FloatsFrom(x, 4);
            if (ft == TypeIds.Matrix44) return FloatsFrom(x, 16);
            JObject j = x as JObject;
            if (j != null)
            {
                if (j.ContainsKey("$raw")) return BE.FromHex(j.Str("$raw"));
                return Struct(j, types, false, false);
            }
            if (ft == TypeIds.Float) return UF(x);
            if (ft == TypeIds.Bool) return Num.ToBool(x);
            if (TypeIds.IsPrim(ft))
            {
                long v = IntFrom(x);
                if (!Num.InRange(ft, v)) throw new FormatException(f.Name + ": " + v + " is out of range for " + TypeIds.PrimName(ft));
                return v;
            }
            return x;
        }

        // ------------------------------------------------------------------------------ paths
        /// <summary>a field of a struct (Index &lt; 0) or an element of a list</summary>
        public sealed class Loc
        {
            public GdStruct Owner;     // struct holding the field (for elements: the struct holding the list)
            public string Name;        // field name
            public IList List;         // the list, for elements
            public int Index = -1;
            public Field Field;        // declaring field (the list field for elements)

            public object Get()
            {
                if (Index >= 0) return List[Index];
                return Owner[Name];
            }

            public void Set(object v)
            {
                if (Index >= 0) List[Index] = v;
                else Owner[Name] = v;
            }
        }

        public static List<string> Tokens(string path)
        {
            List<string> r = new List<string>();
            int i = 0;
            while (i < path.Length)
            {
                char c = path[i];
                if (c == '.') { i++; continue; }
                if (c == '[')
                {
                    int j = path.IndexOf(']', i);
                    if (j < 0) throw new FormatException("bad path " + path);
                    string num = path.Substring(i + 1, j - i - 1);
                    int dummy;
                    if (!int.TryParse(num, NumberStyles.None, CultureInfo.InvariantCulture, out dummy)) throw new FormatException("bad index in " + path);
                    r.Add("[" + num + "]");
                    i = j + 1;
                    continue;
                }
                int k = i;
                while (k < path.Length && path[k] != '.' && path[k] != '[' && path[k] != ']') k++;
                r.Add(path.Substring(i, k - i));
                i = k;
            }
            return r;
        }

        /// <summary>'__base.TagCollectionSet', 'Slots[2].Name', 'Tracks[0].Anims[1]' -> location
        /// (fields not in the struct are looked up through the __base chain)</summary>
        public static Loc Locate(GdStruct o, string path)
        {
            List<string> parts = Tokens(path);
            object cur = o;
            Loc loc = null;
            Field f = null;
            foreach (string p in parts)
            {
                if (p.StartsWith("["))
                {
                    int idx = int.Parse(p.Substring(1, p.Length - 2), CultureInfo.InvariantCulture);
                    IList l = cur as IList;
                    if (l == null) throw new KeyNotFoundException("path " + path + ": not a list at " + p);
                    if (idx < 0 || idx >= l.Count) throw new KeyNotFoundException("path " + path + ": index " + idx + " out of range");
                    loc = new Loc { Owner = loc != null ? loc.Owner : null, Name = loc != null ? loc.Name : null, List = l, Index = idx, Field = f };
                    cur = l[idx];
                }
                else
                {
                    GdStruct st = cur as GdStruct;
                    if (st == null) throw new KeyNotFoundException("path " + path + ": " + p + " is not inside a struct");
                    if (!st.Type.ByName.ContainsKey(p))
                    {
                        GdObj ob = st as GdObj;
                        GdObj owner = ob != null ? ob.FindOwner(p) : null;
                        if (owner == null) throw new KeyNotFoundException(st.Type.Name + " has no field " + p);
                        st = owner;
                    }
                    f = st.Type.ByName[p];
                    loc = new Loc { Owner = st, Name = p, Field = f };
                    cur = st[p];
                }
            }
            if (loc == null) throw new KeyNotFoundException(path);
            return loc;
        }

        public static object GetPath(GdStruct o, string path)
        {
            return Locate(o, path).Get();
        }

        /// <summary>value at a path, or null when the path does not resolve</summary>
        public static object TryGetPath(GdStruct o, string path)
        {
            try { return Locate(o, path).Get(); }
            catch (KeyNotFoundException) { return null; }
            catch (ArgumentOutOfRangeException) { return null; }
            catch (InvalidCastException) { return null; }
            catch (FormatException) { return null; }
        }

        /// <summary>set a field (or a list element) from a JSON value (same encoding as ToJson)</summary>
        public static void SetPath(GdStruct o, string path, object valueJson, TypeTable types)
        {
            Loc l = Locate(o, path);
            if (l.Index >= 0)
            {
                if (l.List is byte[]) throw new FormatException("byte lists are set as a whole ({\"$bytes\": hex})");
                if (l.List is float[]) { ((float[])l.List)[l.Index] = UF(valueJson); return; }
                object v = FieldFromJson(l.Field, new List<object> { valueJson }, types);
                l.List[l.Index] = ((IList)v)[0];
                return;
            }
            l.Owner[l.Name] = FieldFromJson(l.Field, valueJson, types);
        }
    }

    /// <summary>blank values of fields (all zero/empty, nested structs created)</summary>
    public static class Blank
    {
        public static object Value(TypeTable types, Field f)
        {
            if (f.IsList) return ObjCodec.EmptyList(f);
            if (f.Count != 1 && f.Size != 0)
            {
                object[] r = new object[f.Count];
                for (int i = 0; i < r.Length; i++) r[i] = Scalar(types, f);
                return r;
            }
            return Scalar(types, f);
        }

        public static object Scalar(TypeTable types, Field f)
        {
            uint ft = f.FType;
            if (ft == TypeIds.Key) return 0UL;
            if (ft == TypeIds.DataRef || ft == TypeIds.String) return null;
            if (ft == TypeIds.Float) return 0f;
            if (ft == TypeIds.Bool) return false;
            if (TypeIds.IsPrim(ft)) return 0L;
            if (TypeIds.IsVector(ft)) return new float[4];
            if (ft == TypeIds.Matrix44) return new float[16];
            if (f.Type != null && f.Type.Fields.Count != 0) return Struct(types, f.Type);
            return null;
        }

        public static GdStruct Struct(TypeTable types, TypeDesc t)
        {
            GdStruct s = new GdStruct(t);
            for (int i = 0; i < t.Fields.Count; i++) s.Values[i] = Value(types, t.Fields[i]);
            return s;
        }

        public static GdObj Obj(TypeTable types, TypeDesc t)
        {
            GdObj o = new GdObj(t);
            for (int i = 0; i < t.Fields.Count; i++) o.Values[i] = Value(types, t.Fields[i]);
            return o;
        }
    }
}
