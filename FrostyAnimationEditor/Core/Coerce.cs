using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;

namespace FrostyAnimationEditor.Core
{
    /// <summary>converts plain values (numbers, lists, byte arrays) into the representation the decoder produces for a field</summary>
    public static class Coerce
    {
        public static object ToField(Field f, object v)
        {
            if (v == null) return f.IsList ? ObjCodec.EmptyList(f) : null;
            if (f.IsList) return ToList(f, v);
            if (f.IsFixedArray && v is IList && !(v is byte[]) && !(v is float[]))
            {
                IList l = (IList)v;
                object[] r = new object[f.Count];
                for (int i = 0; i < r.Length; i++) r[i] = i < l.Count ? ToScalar(f, l[i]) : Blank.Scalar(null, f);
                return r;
            }
            return ToScalar(f, v);
        }

        public static object ToScalar(Field f, object v)
        {
            if (v == null) return null;
            uint ft = f.FType;
            switch (ft)
            {
                case TypeIds.Key: return Num.ToKey(v);
                case TypeIds.Bool: return Num.ToBool(v);
                case TypeIds.Float: return Num.ToFloat(v);
                case TypeIds.Int8: case TypeIds.UInt8: case TypeIds.Int16: case TypeIds.UInt16:
                case TypeIds.Int32: case TypeIds.UInt32: case TypeIds.Int64:
                    return Num.ToLong(v);
                case TypeIds.String: return v is string || v is RawStr ? v : Str.Text(v);
                case TypeIds.Vector2: case TypeIds.Vector3: case TypeIds.Vector4: case TypeIds.Quaternion:
                    return Floats(v, 4);
                case TypeIds.Matrix44: return Floats(v, 16);
            }
            return v;
        }

        public static float[] Floats(object v, int n)
        {
            IList l = (IList)v;
            float[] r = new float[n];
            for (int i = 0; i < n && i < l.Count; i++) r[i] = Num.ToFloat(l[i]);
            return r;
        }

        public static object ToList(Field f, object v)
        {
            object empty = ObjCodec.EmptyList(f);
            if (empty is byte[])
            {
                if (v is byte[]) return v;
                IList bl = (IList)v;
                byte[] r = new byte[bl.Count];
                for (int i = 0; i < r.Length; i++) r[i] = (byte)Num.ToLong(bl[i]);
                return r;
            }
            IList l = v as IList;
            if (l == null) throw new ArgumentException(f.Name + ": expected a list");
            if (v.GetType() == empty.GetType()) return v;
            IList outp = (IList)empty;
            uint ft = f.FType;
            foreach (object e in l)
            {
                if (empty is List<long>) outp.Add(Num.ToLong(e));
                else if (empty is List<float>) outp.Add(Num.ToFloat(e));
                else if (empty is List<bool>) outp.Add(Num.ToBool(e));
                else if (empty is List<ulong>) outp.Add(Num.ToKey(e));
                else if (empty is List<float[]>) outp.Add(Floats(e, ft == TypeIds.Matrix44 ? 16 : 4));
                else outp.Add(e);
            }
            return outp;
        }

        /// <summary>set field values from a dictionary (encoder output) with conversion</summary>
        public static void Apply(GdStruct o, IDictionary<string, object> values)
        {
            foreach (KeyValuePair<string, object> kv in values)
            {
                Field f = o.Type.GetField(kv.Key);
                if (f == null) throw new KeyNotFoundException(o.Type.Name + " has no field " + kv.Key);
                o[kv.Key] = ToField(f, kv.Value);
            }
        }
    }
}
