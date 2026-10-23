using System.Collections;
using System.Collections.Generic;

namespace FrostyAnimationEditor.Core
{
    public struct KeyRef
    {
        public string Path;
        public ulong Key;
        public KeyRef(string path, ulong key) { Path = path; Key = key; }
        public override string ToString() { return Path + " -> " + BE.KeyHex(Key); }
    }

    /// <summary>Key references inside an object (incl. nested objects and structs).</summary>
    public static class Refs
    {
        /// <summary>[(path, key)] of every Key value of o (paths start with the class name)</summary>
        public static List<KeyRef> From(GdStruct o)
        {
            List<KeyRef> r = new List<KeyRef>();
            Walk(o, o.Type.Name, r, true);
            return r;
        }

        /// <summary>the keys only (no path strings; for indexes)</summary>
        public static void Keys(GdStruct o, List<ulong> outKeys)
        {
            WalkKeys(o, outKeys);
        }

        private static void Walk(GdStruct v, string path, List<KeyRef> r, bool withPath)
        {
            TypeDesc t = v.Type;
            for (int i = 0; i < t.Fields.Count; i++)
            {
                object x = v.Values[i];
                if (x == null) continue;
                Field f = t.Fields[i];
                string p = path + "." + f.Name;
                if (f.FType == TypeIds.Key)
                {
                    if (f.Name == "__key") continue;
                    IList l = x as IList;
                    if (l != null)
                    {
                        for (int j = 0; j < l.Count; j++) r.Add(new KeyRef(p + "[" + j + "]", Num.ToKey(l[j])));
                    }
                    else r.Add(new KeyRef(p, (ulong)x));
                }
                else if (f.FType == TypeIds.DataRef)
                {
                    if (f.IsList)
                    {
                        IList l = (IList)x;
                        for (int j = 0; j < l.Count; j++)
                        {
                            GdStruct o = l[j] as GdStruct;
                            if (o != null) Walk(o, p + "[" + j + "]", r, withPath);
                        }
                    }
                    else Walk((GdStruct)x, p, r, withPath);
                }
                else if (f.IsList)
                {
                    IList l = x as IList;
                    if (l != null && l.Count > 0 && l[0] is GdStruct)
                        for (int j = 0; j < l.Count; j++)
                        {
                            GdStruct e = l[j] as GdStruct;
                            if (e != null) Walk(e, p + "[" + j + "]", r, withPath);
                        }
                }
                else if (x is GdStruct) Walk((GdStruct)x, p, r, withPath);
            }
        }

        private static void WalkKeys(GdStruct v, List<ulong> r)
        {
            TypeDesc t = v.Type;
            for (int i = 0; i < t.Fields.Count; i++)
            {
                object x = v.Values[i];
                if (x == null) continue;
                Field f = t.Fields[i];
                if (f.FType == TypeIds.Key)
                {
                    if (f.Name == "__key") continue;
                    IList l = x as IList;
                    if (l != null) { for (int j = 0; j < l.Count; j++) r.Add(Num.ToKey(l[j])); }
                    else r.Add((ulong)x);
                }
                else if (f.FType == TypeIds.DataRef)
                {
                    if (f.IsList)
                    {
                        IList l = (IList)x;
                        for (int j = 0; j < l.Count; j++) { GdStruct o = l[j] as GdStruct; if (o != null) WalkKeys(o, r); }
                    }
                    else WalkKeys((GdStruct)x, r);
                }
                else if (f.IsList)
                {
                    IList l = x as IList;
                    if (l != null && l.Count > 0 && l[0] is GdStruct)
                        for (int j = 0; j < l.Count; j++) { GdStruct e = l[j] as GdStruct; if (e != null) WalkKeys(e, r); }
                }
                else if (x is GdStruct) WalkKeys((GdStruct)x, r);
            }
        }

        /// <summary>replace [n] indexes with [] (path patterns)</summary>
        public static string Pattern(string path)
        {
            System.Text.StringBuilder sb = new System.Text.StringBuilder(path.Length);
            int i = 0;
            while (i < path.Length)
            {
                char c = path[i];
                if (c == '[')
                {
                    int j = i + 1;
                    while (j < path.Length && path[j] >= '0' && path[j] <= '9') j++;
                    if (j > i + 1 && j < path.Length && path[j] == ']')
                    {
                        sb.Append("[]");
                        i = j + 1;
                        continue;
                    }
                }
                sb.Append(c);
                i++;
            }
            return sb.ToString();
        }
    }
}
