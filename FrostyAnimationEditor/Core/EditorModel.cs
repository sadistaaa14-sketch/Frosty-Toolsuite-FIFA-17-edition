using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using FrostyAnimationEditor.Core.Anim;

namespace FrostyAnimationEditor.Core
{
    /// <summary>an error to show to the user (bad input, an object that is not there, ...)</summary>
    public sealed class ApiError : Exception { public ApiError(string m) : base(m) { } }

    /// <summary>
    /// The editor's operations on an open antstate container, independent of the UI: every view the windows show
    /// and every edit they make (each edit is one undoable change of the session). Results are JSON-like trees
    /// (JObject / List / numbers / strings), the same shapes the antstate web editor's API returns.
    /// </summary>
    public sealed partial class EditorModel
    {
        public static readonly string[] ClipTypes = { "VbrAnimationAsset", "DctAnimationAsset", "RawAnimationAsset", "FrameAnimationAsset" };
        public static readonly string[] SmTypes = { "StateFlowControllerAsset", "StateFlowNodeControllerAsset", "StateFlowTransitionAsset" };
        public static readonly string[] CurveTypes = { "DofWeightCurvesAsset", "FloatGameStateTag", "PiecewiseLinearAsset" };
        public const int ListPage = 100;

        public readonly Session S;
        public AntDb Db { get { return S.Db; } }
        public string SourceLabel;
        public readonly List<string> LogLines = new List<string>();
        public event Action<string> Said;
        private RigIndex ri;
        private HashSet<ulong> importedKeys;
        private ClipNames clipNames;

        public EditorModel(Session s, string sourceLabel)
        {
            S = s;
            SourceLabel = sourceLabel;
        }

        public void Say(string msg)
        {
            string line = DateTime.Now.ToString("HH:mm:ss ", CultureInfo.InvariantCulture) + msg;
            LogLines.Add(line);
            if (LogLines.Count > 300) LogLines.RemoveRange(0, LogLines.Count - 300);
            Action<string> a = Said;
            if (a != null) a(msg);
        }

        public RigIndex Rig()
        {
            if (ri == null) ri = new RigIndex(Db);
            return ri;
        }

        public void ResetRig() { ri = null; }

        /// <summary>readable clip names (controller names); the reverse index is dropped on every change</summary>
        public ClipNames Names
        {
            get
            {
                if (clipNames == null)
                {
                    clipNames = new ClipNames(Db);
                    S.Changed += () => clipNames.Reset();
                }
                return clipNames;
            }
        }

        /// <summary>readable name of a clip (its ClipControllerAsset's name), else null</summary>
        public string Alias(ulong k) { return Names.Of(k); }

        private void PutAlias(JObject j, ulong k)
        {
            string a = Names.Of(k);
            if (a != null) j["alias"] = a;
        }

        // ------------------------------------------------------------------ helpers
        public static string H(ulong k) { return BE.KeyHex(k); }

        public static ulong K(object s)
        {
            if (s == null) throw new ApiError("no key given");
            if (s is ulong) return (ulong)s;
            if (s is long) return unchecked((ulong)(long)s);
            if (s is int) return (ulong)(int)s;
            string t = s.ToString().Trim();
            try
            {
                if (t.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) return Convert.ToUInt64(t.Substring(2), 16);
                return ulong.Parse(t, CultureInfo.InvariantCulture);
            }
            catch (Exception) { throw new ApiError("not a key: " + t); }
        }

        private bool Imported(ulong k)
        {
            if (importedKeys == null) importedKeys = new HashSet<ulong>(Db.OrigA.Select(r => r.Key));
            return importedKeys.Contains(k);
        }

        /// <summary>{key, type, name, state} of a key: state is ok, imported (table A: supplied by another container
        /// at runtime) or missing; null for 0</summary>
        public JObject Info(ulong k)
        {
            if (k == 0) return null;
            int i = Db.BlockOf(k);
            JObject r = new JObject();
            r["key"] = H(k);
            if (i >= 0 && !Db.Removed.Contains(i)) { r["type"] = Db.TypeName(i); r["name"] = Db.NameOf(i); PutAlias(r, k); r["state"] = "ok"; return r; }
            if (Imported(k)) { r["type"] = "AnimationAsset"; r["name"] = "(imported from another container)"; r["state"] = "imported"; return r; }
            r["type"] = "?"; r["name"] = "(not in this file)"; r["state"] = "missing";
            return r;
        }

        public GdObj Root(object key)
        {
            GdObj o = Db.Get(K(key));
            if (o == null) throw new ApiError("no object " + key);
            return o;
        }

        private static string NameOf(GdObj o) { return o.Name ?? ""; }

        // ------------------------------------------------------------------ tree view model
        private static bool IsListValue(object v) { return v is IList && !(v is string); }

        /// <summary>view model of one field value</summary>
        public JObject Node(Field f, object v, string path, int depth, int offset = 0, int limit = ListPage)
        {
            JObject r = NodeOnly(f, v, path, depth, offset, limit);
            FieldSemantics.Entry fe = FieldSemantics.Get(f);
            if (fe != null)
            {
                if (fe.HasMeaning) r["d"] = fe.Meaning;
                if (fe.StatusText.Length > 0) r["ds"] = fe.StatusText;
            }
            return r;
        }

        private JObject NodeOnly(Field f, object v, string path, int depth, int offset, int limit)
        {
            int dot = path.LastIndexOf('.');
            string name = path.Length > 0 ? (dot >= 0 ? path.Substring(dot + 1) : path) : "";
            string tn = f != null ? f.TypeName : "?";
            JObject n = new JObject();
            n["p"] = path; n["n"] = name; n["t"] = tn;
            if (f != null && f.IsList && IsListValue(v))
            {
                IList l = (IList)v;
                n["k"] = "list";
                n["len"] = (long)l.Count;
                byte[] bv = v as byte[];
                if (bv != null)
                {
                    n["k"] = "bytes";
                    bool edit = bv.Length <= 4096;
                    n["edit"] = edit;
                    n["v"] = BE.Hex(edit ? bv : bv.Take(256).ToArray());
                    return n;
                }
                n["et"] = tn;
                if (depth > 0)
                {
                    List<object> items = new List<object>();
                    for (int j = offset; j < Math.Min(l.Count, offset + limit); j++) items.Add(Elem(f, l[j], path + "[" + j + "]", depth - 1));
                    n["c"] = items;
                    n["off"] = (long)offset;
                }
                return n;
            }
            return Elem(f, v, path, depth, true);
        }

        public JObject Elem(Field f, object v, string path, int depth, bool asField = false)
        {
            string name;
            if (path.EndsWith("]")) name = path.Substring(path.LastIndexOf('['));
            else { int dot = path.LastIndexOf('.'); name = dot >= 0 ? path.Substring(dot + 1) : path; }
            string tn = f != null ? f.TypeName : "?";
            JObject n = new JObject();
            n["p"] = path; n["n"] = name; n["t"] = tn;
            uint ft = f != null ? f.FType : 0;
            if (v == null)
            {
                n["k"] = "null";
                n["ref"] = ft == TypeIds.DataRef;
                n["str"] = ft == TypeIds.String;
                return n;
            }
            GdStruct st = v as GdStruct;
            if (st != null)
            {
                n["k"] = "struct";
                n["t"] = st.Type.Name;
                n["obj"] = st is GdObj;
                if (depth > 0)
                {
                    List<object> c = new List<object>();
                    foreach (Field g in st.Type.Fields)
                        if (g.Name != "__key" || path.Length == 0)
                            c.Add(Node(g, st.Values[g.Index], (path.Length > 0 ? path + "." : "") + g.Name, depth - 1));
                    n["c"] = c;
                }
                return n;
            }
            // fixed-size arrays (count > 1): numbers as one "array" value, keys / vectors / structs element by element
            object[] fixedArr = v as object[];
            if (f != null && f.Count > 1 && fixedArr != null && !path.EndsWith("]"))
            {
                if (ft != TypeIds.Key && fixedArr.All(x => x == null || x is bool || x is float || x is double || x is long || x is int))
                {
                    n["k"] = "array";
                    n["v"] = fixedArr.Select(x => x is float ? JsonIO.F((float)x) : x).ToList();
                    return n;
                }
                n["k"] = "fixed";
                n["len"] = (long)fixedArr.Length;
                if (depth > 0)
                {
                    List<object> items = new List<object>();
                    for (int j = 0; j < fixedArr.Length; j++) items.Add(Elem(f, fixedArr[j], path + "[" + j + "]", depth - 1));
                    n["c"] = items;
                }
                return n;
            }
            if (ft == TypeIds.Key)
            {
                ulong k = Num.ToKey(v);
                n["k"] = "key";
                n["v"] = H(k);
                n["r"] = Info(k);
                if (path == "__key") n["ro"] = true;
                return n;
            }
            if (ft == TypeIds.String)
            {
                n["k"] = "string";
                n["v"] = Str.Text(v);
                return n;
            }
            if (TypeIds.IsVector(ft) || ft == TypeIds.Matrix44)
            {
                n["k"] = "vector";
                n["v"] = ((IList)v).Cast<object>().Select(a => JsonIO.F(Num.ToFloat(a))).ToList();
                return n;
            }
            if (v is bool || ft == TypeIds.Bool)
            {
                n["k"] = "bool";
                n["v"] = Num.ToBool(v);
                return n;
            }
            if (v is float || v is double)
            {
                n["k"] = "float";
                n["v"] = JsonIO.F(Num.ToFloat(v));
                return n;
            }
            if (v is long || v is int || v is ulong || v is uint || v is short || v is ushort || v is byte || v is sbyte)
            {
                n["k"] = "int";
                n["v"] = v is ulong ? (object)(ulong)v : Num.ToLong(v);
                return n;
            }
            byte[] bytes = v as byte[];
            if (bytes != null)
            {
                n["k"] = "bytes";
                n["v"] = BE.Hex(bytes.Take(256).ToArray());
                n["len"] = (long)bytes.Length;
                return n;
            }
            IList arr = v as IList;
            if (arr != null && !(v is string))
            {
                n["k"] = "array";
                n["v"] = arr.Cast<object>().Select(x => x is GdStruct ? null : (x is float ? JsonIO.F((float)x) : x)).ToList();
                return n;
            }
            n["k"] = "other";
            n["v"] = Convert.ToString(v, CultureInfo.InvariantCulture);
            return n;
        }

        /// <summary>a located value: Parent is the struct (Key = field name) or the list (Key = index)</summary>
        public sealed class Located
        {
            public object Parent;
            public object Key;
            public Field Field;
            public object Value;
            public bool InList { get { return Parent is IList; } }
            public int Index { get { return (int)Key; } }
            public string Name { get { return (string)Key; } }

            public void Set(object v)
            {
                if (InList) ((IList)Parent)[Index] = v;
                else ((GdStruct)Parent)[Name] = v;
            }
        }

        private static readonly Regex PathTok = new Regex(@"[^.\[\]]+|\[\d+\]");

        public Located Locate(GdObj o, string path)
        {
            object cur = o, parent = null, pkey = null;
            Field f = null;
            foreach (Match m in PathTok.Matches(path))
            {
                string p = m.Value;
                if (p.StartsWith("["))
                {
                    int idx = int.Parse(p.Substring(1, p.Length - 2), CultureInfo.InvariantCulture);
                    IList l = cur as IList;
                    if (l == null || cur is string) throw new ApiError("path " + PyFormat.Repr(path) + ": " + PyFormat.Repr(p) + " is not a list element");
                    if (idx < 0 || idx >= l.Count) throw new ApiError("path " + PyFormat.Repr(path) + ": index " + idx + " out of range");
                    parent = cur;
                    pkey = idx;
                    cur = l[idx];
                }
                else
                {
                    GdStruct st = cur as GdStruct;
                    if (st == null) throw new ApiError("path " + PyFormat.Repr(path) + ": " + PyFormat.Repr(p) + " is not a struct");
                    if (!st.Type.ByName.ContainsKey(p))
                    {
                        GdObj ob = st as GdObj;
                        GdObj owner = ob != null ? ob.FindOwner(p) : null;
                        if (owner == null) throw new ApiError(st.Type.Name + " has no field " + PyFormat.Repr(p));
                        st = owner;
                    }
                    f = st.Type.ByName[p];
                    parent = st;
                    pkey = p;
                    cur = st[p];
                }
            }
            return new Located { Parent = parent, Key = pkey, Field = f, Value = cur };
        }

        public JObject ObjView(object key)
        {
            GdObj o = Root(key);
            int i = Db.BlockOf(o.Key);
            List<object> panels = new List<object>();
            string tn = o.Type.Name;
            if (ClipTypes.Contains(tn) || tn == "ClipControllerAsset") panels.Add("clip");
            if (Sequence.IsSequenceType(tn)) panels.Add("seq");
            if (tn == "ChooserControllerAsset") panels.Add("chooser");
            if (tn == "LayersControllerAsset") panels.Add("layers");
            if (tn == "BlendMaskAsset" || tn == "BlendMaskListAsset") panels.Add("mask");
            if (tn == "SteeringControllerAsset") panels.Add("steer");
            if (HasTagPanel(o)) { panels.Add("tags"); panels.Add("tree"); }
            if (SmTypes.Contains(tn)) panels.Add("sm");
            if (tn == "BlendSpace") panels.Add("blend");
            if (tn == "ExpressionAsset") panels.Add("expr");
            if (CurveTypes.Contains(tn)) panels.Add("curves");
            if (HasCdbPanel(o)) panels.Add("cdb");
            JObject tree = Elem(null, o, "", 2);
            tree["t"] = tn;
            JObject r = new JObject();
            r["key"] = H(o.Key); r["type"] = tn; r["name"] = NameOf(o); r["tree"] = tree; r["panels"] = panels;
            PutAlias(r, o.Key);
            r["exported"] = Db.Exports.Contains(o.Key); r["new"] = i >= Db.N0;
            r["chain"] = o.Chain().Select(x => (object)x.Type.Name).ToList();
            r["about"] = About(o);
            if (Tags.Part(o, "TimelinePointTag") != null) r["tag_owners"] = TagOwners(o.Key).Take(40).ToList();
            return r;
        }

        /// <summary>what the game does with the object's types (its chain, then the struct types in it): FieldSemantics</summary>
        public List<object> About(GdObj o)
        {
            List<string> types = new List<string>();
            foreach (GdObj x in o.Chain()) if (!types.Contains(x.Type.Name)) types.Add(x.Type.Name);
            HashSet<string> known = FieldSemantics.Types();
            Stack<TypeDesc> todo = new Stack<TypeDesc>();
            HashSet<TypeDesc> seen = new HashSet<TypeDesc>();
            foreach (GdObj x in o.Chain()) todo.Push(x.Type);
            while (todo.Count > 0)
            {
                TypeDesc t = todo.Pop();
                if (!seen.Add(t)) continue;
                foreach (Field g in t.Fields)
                {
                    if (g.Type == null || g.FType == TypeIds.Key || g.Name == "__base") continue;
                    if (g.Type.Fields.Count > 0 && seen.Count < 64 && g.FType != TypeIds.String && g.Type.Name != "String")
                    {
                        if (known.Contains(g.Type.Name) && !types.Contains(g.Type.Name)) types.Add(g.Type.Name);
                        todo.Push(g.Type);
                    }
                }
            }
            List<object> r = new List<object>();
            foreach (string tn in types)
            {
                List<FieldSemantics.Entry> es = FieldSemantics.OfType(tn);
                if (es.Count == 0) continue;
                JObject j = new JObject();
                j["type"] = tn;
                j["entries"] = es.Select(e =>
                {
                    JObject x = new JObject();
                    x["field"] = e.Field; x["meaning"] = e.Meaning; x["status"] = e.StatusText; x["unidentified"] = e.Unidentified;
                    return (object)x;
                }).ToList();
                r.Add(j);
            }
            return r;
        }

        // ------------------------------------------------------------------ value conversion
        public object ConvertValue(Field f, object value, bool element = false)
        {
            TypeTable types = Db.Types;
            try
            {
                if (element)
                {
                    if ((f.FType == TypeIds.UInt8 || f.FType == TypeIds.Int8) && f.IsList) return (byte)Num.ToLong(value);
                    return ((IList)JsonIO.FieldFromJson(f, new List<object> { value }, types))[0];
                }
                return JsonIO.FieldFromJson(f, value, types);
            }
            catch (ApiError) { throw; }
            catch (Exception e) when (e is FormatException || e is InvalidCastException || e is OverflowException || e is ArgumentException || e is KeyNotFoundException)
            {
                throw new ApiError(f.Name + ": " + e.Message);
            }
        }

        /// <summary>a blank object of a class with its __base chain (blank base-class parts, as the shipped objects have)</summary>
        public GdObj BlankObjWithChain(TypeDesc t)
        {
            GdObj o = Blank.Obj(Db.Types, t);
            Field b = t.GetField("__base");
            if (b != null && b.Type != null && b.Type.Fields.Count > 0) o["__base"] = BlankObjWithChain(b.Type);
            return o;
        }

        public object BlankElement(Field f, string typeName = null)
        {
            TypeTable types = Db.Types;
            if (f.FType == TypeIds.DataRef)
            {
                TypeDesc t = !string.IsNullOrEmpty(typeName) ? TypeOrError(typeName) : f.Type;
                if (t == null) return null;
                return BlankObjWithChain(t);
            }
            if (!string.IsNullOrEmpty(typeName) && f.Type != null && typeName != f.Type.Name) return Blank.Obj(types, TypeOrError(typeName));
            return Blank.Scalar(types, f);
        }

        private TypeDesc TypeOrError(string name)
        {
            TypeDesc t = Db.Types.Get(name);
            if (t == null) throw new ApiError("no class " + PyFormat.Repr(name) + " in the file's type table");
            return t;
        }

        // ================================================================== API
        public JObject State()
        {
            AntDb db = Db;
            uint[] h = db.Header.H;
            JObject r = new JObject();
            r["path"] = SourceLabel;
            r["objects"] = (long)(db.Order.Count - db.Removed.Count);
            r["dirty"] = S.Dirty;
            r["undo"] = S.UndoStack.Skip(Math.Max(0, S.UndoStack.Count - 20)).Select(e => (object)e.Label).ToList();
            r["redo"] = S.RedoStack.Skip(Math.Max(0, S.RedoStack.Count - 20)).Select(e => (object)e.Label).ToList();
            JObject hd = new JObject();
            hd["version"] = (long)db.Header.Version; hd["T"] = (long)(db.Order.Count - db.Removed.Count); hd["C"] = (long)db.Exports.Count;
            hd["A"] = (long)h[4]; hd["H7"] = (long)h[6]; hd["H8"] = (long)h[7]; hd["strm_aux"] = (long)db.StrmAux;
            r["header"] = hd;
            r["changes"] = (long)S.UndoStack.Count;
            r["added"] = (long)db.NewObjs.Keys.Count(i => !db.Removed.Contains(i));
            r["removed"] = (long)db.Removed.Count(i => i < db.N0);
            return r;
        }

        /// <summary>classes with their object counts (most objects first)</summary>
        public List<object> Types(bool all = false)
        {
            AntDb db = Db;
            List<JObject> r = new List<JObject>();
            foreach (TypeDesc t in db.Types.Order)
            {
                List<int> l;
                long n = db.ByType.TryGetValue(t.Name, out l) ? l.Count(i => !db.Removed.Contains(i)) : 0;
                if (n != 0 || all)
                {
                    JObject j = new JObject();
                    j["name"] = t.Name; j["count"] = n; j["tid"] = "0x" + t.Tid.ToString("X8");
                    r.Add(j);
                }
            }
            return r.OrderBy(j => -(long)j["count"]).ThenBy(j => (string)j["name"], StringComparer.Ordinal).Cast<object>().ToList();
        }

        /// <summary>objects of a class (or of all classes) whose name contains q, or whose key starts with q ("0x...")</summary>
        public JObject Objects(string type, string q, int offset = 0, int limit = 500)
        {
            AntDb db = Db;
            string tname = string.IsNullOrEmpty(type) ? null : type;
            string text = (q ?? "").ToLowerInvariant();
            string keyq = text.StartsWith("0x") && text.Length > 4 ? text : null;
            List<object> items = new List<object>();
            long total = 0;
            foreach (int i in db.BlockIds(tname))
            {
                if (keyq != null)
                {
                    if (!H(db.Keys[i]).StartsWith(keyq, StringComparison.Ordinal)) continue;
                }
                else if (text.Length > 0)
                {
                    if (db.NameOf(i).ToLowerInvariant().IndexOf(text, StringComparison.Ordinal) < 0)
                    {
                        string al = Names.OfBlock(i);
                        if (al == null || al.ToLowerInvariant().IndexOf(text, StringComparison.Ordinal) < 0) continue;
                    }
                }
                if (offset <= total && total < offset + limit)
                {
                    JObject j = new JObject();
                    j["key"] = H(db.Keys[i]); j["type"] = db.TypeName(i); j["name"] = db.NameOf(i);
                    string a = Names.OfBlock(i);
                    if (a != null) j["alias"] = a;
                    items.Add(j);
                }
                total++;
                if (tname == null && total >= offset + limit && text.Length == 0) break;
            }
            JObject r = new JObject();
            r["total"] = total;
            r["items"] = items;
            return r;
        }

        public JObject NodeView(object key, string path, int offset = 0)
        {
            GdObj o = Root(key);
            if (string.IsNullOrEmpty(path)) return Elem(null, o, "", 1);
            Located l = Locate(o, path);
            if (l.InList) return Elem(l.Field, l.Value, path, 1);
            return Node(l.Field, l.Value, path, 1, offset);
        }

        public JObject Fields(string type)
        {
            TypeDesc t = TypeOrError(type);
            JObject r = new JObject();
            r["name"] = t.Name; r["size"] = (long)t.Size; r["align"] = (long)t.Align; r["tid"] = "0x" + t.Tid.ToString("X8");
            r["fields"] = t.Fields.Select(f =>
            {
                JObject j = new JObject();
                j["name"] = f.Name; j["type"] = f.TypeName; j["list"] = f.IsList; j["count"] = (long)f.Count; j["offset"] = (long)f.Offset;
                return (object)j;
            }).ToList();
            return r;
        }

        /// <summary>runs an edit as one undoable change (rolled back if it throws)</summary>
        public void Change(string label, IEnumerable<ulong> keys, Action edit)
        {
            using (Session.Scope c = S.Change(label, keys))
            {
                edit();
                c.Complete();
            }
        }

        public JObject Set(object key, string path, object value)
        {
            GdObj o = Root(key);
            Change("set " + NameOf(o) + "." + path, new[] { o.Key }, () =>
            {
                Located l = Locate(o, path);
                if (l.InList)
                {
                    if (l.Parent is byte[]) throw new ApiError("edit byte lists as hex");
                    if (l.Parent is float[]) { ((float[])l.Parent)[l.Index] = Num.ToFloat(value is string ? (object)PyFormat.ParseFloat((string)value) : value); return; }
                    IList lst = (IList)l.Parent;
                    lst[l.Index] = ConvertValue(l.Field, value, true);
                }
                else
                {
                    Field f = l.Field;
                    if (f.IsList && (f.FType == TypeIds.UInt8 || f.FType == TypeIds.Int8) && value is string)
                    {
                        try { l.Set(BE.FromHex(((string)value).Replace(" ", ""))); }
                        catch (FormatException e) { throw new ApiError(e.Message); }
                    }
                    else l.Set(ConvertValue(f, value));
                }
            });
            return ObjView(key);
        }

        private static object CopyElem(object e)
        {
            GdStruct st = e as GdStruct;
            if (st != null) return DeepCopy.Value(st);
            float[] fa = e as float[];
            if (fa != null) return fa.Clone();
            return e;
        }

        /// <summary>list operations: append (a value, a copy of the last element or a blank one), insert (a copy before
        /// index), remove, up, down, clear</summary>
        public JObject ListOp(object key, string path, string op, int? index = null, object value = null, string type = null, bool copyLast = true)
        {
            GdObj o = Root(key);
            Change(op + " " + NameOf(o) + "." + path, new[] { o.Key }, () =>
            {
                Located l = Locate(o, path);
                Field f = l.Field;
                if (f == null || !f.IsList) throw new ApiError(path + " is not a list");
                bool asBytes = l.Value is byte[];
                List<object> lst = l.Value == null ? new List<object>() : ((IList)l.Value).Cast<object>().ToList();
                int idx = index ?? lst.Count;
                if (op == "append")
                {
                    if (value != null) lst.Add(ConvertValue(f, value, true));
                    else if (f.FType == TypeIds.Key) lst.Add(0UL);
                    else if (lst.Count > 0 && copyLast && string.IsNullOrEmpty(type)) lst.Add(CopyElem(lst[lst.Count - 1]));
                    else lst.Add(asBytes ? (object)(byte)0 : BlankElement(f, type));
                }
                else if (op == "insert")
                {
                    object src = idx >= 0 && idx < lst.Count ? lst[idx] : null;
                    int at = Math.Max(0, Math.Min(lst.Count, idx));
                    lst.Insert(at, src != null ? CopyElem(src) : (asBytes ? (object)(byte)0 : BlankElement(f)));
                }
                else if (op == "remove")
                {
                    if (idx < 0 || idx >= lst.Count) throw new ApiError("index " + idx + " out of range");
                    lst.RemoveAt(idx);
                }
                else if (op == "up" && idx > 0 && idx < lst.Count) { object t = lst[idx - 1]; lst[idx - 1] = lst[idx]; lst[idx] = t; }
                else if (op == "down" && idx >= 0 && idx < lst.Count - 1) { object t = lst[idx + 1]; lst[idx + 1] = lst[idx]; lst[idx] = t; }
                else if (op == "clear") lst.Clear();
                object nv = asBytes ? lst.Select(x => (byte)Num.ToLong(x)).ToArray() : Coerce.ToList(f, lst);
                l.Set(nv);
                // ContextDatabaseAsset: the last NumAuxiliaryEntryAsset entries are continuation-only
                GdStruct ps = l.Parent as GdStruct;
                if (!l.InList && l.Name == "EntryAssets" && ps != null && ps.Type.Has("NumAuxiliaryEntryAsset"))
                {
                    long aux = ps["NumAuxiliaryEntryAsset"] == null ? 0 : Num.ToLong(ps["NumAuxiliaryEntryAsset"]);
                    int nOld = lst.Count - (op == "append" || op == "insert" ? 1 : op == "remove" ? -1 : 0);
                    long firstAux = nOld - aux;
                    if (op == "append" && aux > 0) aux += 1;
                    else if (op == "insert" && idx >= firstAux && aux > 0) aux += 1;
                    else if (op == "remove" && idx >= firstAux) aux = Math.Max(0, aux - 1);
                    else if (op == "clear") aux = 0;
                    ps["NumAuxiliaryEntryAsset"] = Coerce.ToField(ps.Type.GetField("NumAuxiliaryEntryAsset"), aux);
                }
            });
            return ObjView(key);
        }

        public JObject SetNull(object key, string path)
        {
            GdObj o = Root(key);
            Change("clear " + NameOf(o) + "." + path, new[] { o.Key }, () =>
            {
                Located l = Locate(o, path);
                if (l.InList)
                {
                    IList lst = (IList)l.Parent;
                    if (lst is List<GdObj> || lst is List<GdStruct> || lst is List<object>) lst[l.Index] = null;
                    else throw new ApiError("elements of " + path.Substring(0, path.LastIndexOf('[')) + " cannot be null");
                }
                else l.Set(l.Field.IsList ? ObjCodec.EmptyList(l.Field) : null);
            });
            return ObjView(key);
        }

        /// <summary>create an embedded object at a null DataRef field / list element</summary>
        public JObject NewRef(object key, string path, string type)
        {
            GdObj o = Root(key);
            Change("new " + (type ?? "") + " at " + NameOf(o) + "." + path, new[] { o.Key }, () =>
            {
                Located l = Locate(o, path);
                TypeDesc t = !string.IsNullOrEmpty(type) ? TypeOrError(type) : l.Field.Type;
                if (t == null) throw new ApiError("the field's class is unknown; type a class name");
                l.Set(BlankObjWithChain(t));
            });
            return ObjView(key);
        }

        public string JsonGet(object key)
        {
            return MiniJson.Serialize(JsonIO.ToJson(Root(key)), 1);
        }

        public JObject JsonPut(object key, string json)
        {
            GdObj o = Root(key);
            object d;
            try { d = MiniJson.Parse(json); }
            catch (Exception e) { throw new ApiError("the JSON does not parse: " + e.Message); }
            JObject jd = d as JObject;
            if (jd == null) throw new ApiError("the JSON must be an object");
            GdObj nw;
            try { nw = JsonIO.FromJson(jd, Db.Types); }
            catch (Exception e) when (!(e is ApiError)) { throw new ApiError(e.Message); }
            if (nw.Key != o.Key) throw new ApiError("__key must stay " + H(o.Key) + " (use Clone to make a copy)");
            Change("edit " + NameOf(o) + " as JSON", new[] { o.Key }, () => AnimOps.ReplaceRoot(Db, Db.BlockOf(o.Key), nw));
            return ObjView(key);
        }

        /// <summary>a new root object: a copy of the template, or a blank object of the class (with its base-class parts)</summary>
        public JObject Create(string type, string name = null, object template = null, string baseType = null)
        {
            GdObj made = null;
            Change("new " + type, new ulong[0], () =>
            {
                if (template != null && !(template is string && ((string)template).Length == 0))
                {
                    GdObj src = Root(template);
                    made = Db.Clone(src, true, !string.IsNullOrEmpty(name) ? name : NameOf(src) + "_copy");
                }
                else
                {
                    TypeDesc tt = TypeOrError(type);
                    if (!string.IsNullOrEmpty(baseType) && tt.Has("__base"))
                    {
                        made = Blank.Obj(Db.Types, tt);
                        made["__base"] = BlankObjWithChain(TypeOrError(baseType));
                    }
                    else made = BlankObjWithChain(tt);
                    if (tt.Has("__name")) made["__name"] = !string.IsNullOrEmpty(name) ? name : "New" + type;
                    Db.Add(made);
                }
            });
            Say("created " + made.Type.Name + " " + PyFormat.Repr(NameOf(made)) + " " + H(made.Key));
            JObject r = new JObject();
            r["key"] = H(made.Key);
            return r;
        }

        public JObject Clone(object key, string name = null, int count = 1)
        {
            GdObj o = Root(key);
            int n = Math.Max(1, count);
            List<object> keys = new List<object>();
            Change("clone " + NameOf(o) + " x" + n, new ulong[0], () =>
            {
                for (int j = 0; j < n; j++)
                {
                    string nm = !string.IsNullOrEmpty(name) ? name : NameOf(o) + "_copy";
                    if (n > 1) nm = nm + "_" + (j + 1);
                    GdObj c = Db.Clone(o, true, nm);
                    keys.Add(H(c.Key));
                }
            });
            Say("cloned " + PyFormat.Repr(NameOf(o)) + " -> " + string.Join(", ", keys));
            JObject r = new JObject();
            r["keys"] = keys;
            return r;
        }

        public void Delete(IEnumerable<object> keys)
        {
            List<ulong> ks = keys.Select(K).ToList();
            Change("delete " + ks.Count + " object(s)", new ulong[0], () =>
            {
                foreach (ulong k in ks)
                {
                    try { Db.Remove(k); }
                    catch (KeyNotFoundException) { throw new ApiError("no object " + H(k)); }
                }
            });
            Say("deleted " + ks.Count + " object(s)");
        }

        public JObject ExportFlag(object key, bool on)
        {
            ulong k = K(key);
            Change("exported flag " + H(k), new ulong[0], () =>
            {
                if (on) { if (!Db.Exports.Contains(k)) Db.Exports.Add(k); }
                else Db.Exports.Remove(k);
            });
            return ObjView(key);
        }

        private static string Tail(string p) { int d = p.IndexOf('.'); return d >= 0 ? p.Substring(d + 1) : p; }

        /// <summary>references from the object, and (incoming = true) the objects that reference it</summary>
        public JObject Refs(object key, bool incoming = false, Action<int, int> progress = null)
        {
            AntDb db = Db;
            GdObj o = Root(key);
            List<object> outp = new List<object>();
            foreach (KeyRef r in db.RefsFrom(o))
            {
                if (r.Key == 0) continue;
                JObject j = Info(r.Key);
                j["path"] = Tail(r.Path);
                outp.Add(j);
            }
            JObject res = new JObject();
            res["out"] = outp;
            if (incoming)
            {
                if (progress != null) db.BuildRefIndex(progress);
                List<object> inc = new List<object>();
                foreach (int i in db.RefsTo(o.Key))
                {
                    if (db.Removed.Contains(i)) continue;
                    foreach (KeyRef r in db.RefsFrom(db.Obj(i)))
                    {
                        if (r.Key != o.Key) continue;
                        JObject j = new JObject();
                        j["key"] = H(db.Keys[i]); j["type"] = db.TypeName(i); j["name"] = db.NameOf(i); j["path"] = Tail(r.Path);
                        PutAlias(j, db.Keys[i]);
                        inc.Add(j);
                    }
                }
                res["in"] = inc.Take(5000).ToList();
                res["in_total"] = (long)inc.Count;
            }
            return res;
        }

        public const string CheckNote = "count fields, two-way links and state machine tables are fixed automatically on save";

        /// <summary>the consistency check: state machine tables, count fields, two-way links, engine limits, dangling keys</summary>
        public JObject Check()
        {
            AntDb db = Db;
            List<object> sms = StateMachine.Verify(db).Select(t => { JObject j = new JObject(); j["key"] = H(t.Item1); j["name"] = t.Item2; return (object)j; }).ToList();
            List<object> counts = new List<object>();
            foreach (string cls in Consistency.CheckedClasses(db))
                foreach (int i in db.BlockIds(cls))
                    if (db.Cache.ContainsKey(i) || i >= db.N0)
                        foreach (CountIssue c in Consistency.CheckCounts(db.Obj(i)))
                        {
                            JObject j = new JObject();
                            j["key"] = H(db.Keys[i]); j["path"] = c.Path; j["value"] = c.Have; j["list"] = c.Want;
                            counts.Add(j);
                        }
            List<object> dang = new List<object>();
            foreach (int i in db.Cache.Keys.Concat(db.NewObjs.Keys).ToList())
            {
                if (db.Removed.Contains(i)) continue;
                foreach (KeyRef r in db.RefsFrom(db.Obj(i)))
                    if (r.Key != 0 && !db.Contains(r.Key) && !Imported(r.Key))
                    {
                        JObject j = new JObject();
                        j["key"] = H(db.Keys[i]); j["name"] = db.NameOf(i); j["path"] = r.Path; j["target"] = H(r.Key);
                        dang.Add(j);
                    }
            }
            List<object> links = Consistency.CheckLinks(db).Select(t =>
            {
                JObject inf = Info(t.Item1);
                JObject j = new JObject();
                j["key"] = H(t.Item1); j["name"] = inf != null ? inf.Str("name") : ""; j["problem"] = t.Item2;
                return (object)j;
            }).ToList();
            List<object> lim = Limits.Check(db).Select(x =>
            {
                GdObj g = db.Get(x.Key);
                JObject j = new JObject();
                j["key"] = H(x.Key); j["name"] = g != null ? NameOf(g) : ""; j["class"] = x.Class; j["path"] = x.Path; j["problem"] = x.Message;
                return (object)j;
            }).ToList();
            JObject r2 = new JObject();
            r2["state_machines"] = sms; r2["counts"] = counts; r2["dangling"] = dang.Take(500).ToList(); r2["links"] = links.Take(500).ToList();
            r2["limits"] = lim.Take(500).ToList(); r2["note"] = CheckNote;
            return r2;
        }

        /// <summary>the loader sizing a save writes: key-waiter peak (H7) and linear-allocator use (H8)</summary>
        public JObject Runtime(Action<int, int> progress = null)
        {
            AntDb db = Db;
            RuntimeProfile bas = db.BaseProfile(progress);
            List<int> ids = db.LiveIds();
            RuntimeProfile cur = db.Profile(ids, false, false, progress);
            HeaderPlan hp = db.PlanHeader(ids);
            long cap = db.Header.Version == 3 ? Core.Runtime.LinearSlack(hp.H8) : hp.H8;
            JObject r = new JObject();
            r["h7"] = (long)db.Header.H[6]; r["h8"] = (long)db.Header.H[7]; r["h7_save"] = (long)hp.H7; r["h8_save"] = (long)hp.H8;
            r["waiter_peak"] = cur.WaiterPeak; r["waiter_peak_original"] = bas.WaiterPeak;
            r["linear_bytes"] = cur.LinearBytes; r["linear_bytes_original"] = bas.LinearBytes;
            r["post_load_bytes"] = cur.PostLoadBytes; r["capacity"] = cap; r["unmodelled"] = cur.Unmodelled.Cast<object>().ToList();
            return r;
        }

        public string Undo()
        {
            string lab = S.Undo();
            Say(lab != null ? "undo: " + lab : "nothing to undo");
            return lab;
        }

        public string Redo()
        {
            string lab = S.Redo();
            Say(lab != null ? "redo: " + lab : "nothing to redo");
            return lab;
        }
    }
}
