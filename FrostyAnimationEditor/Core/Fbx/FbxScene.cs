using System;
using System.Collections.Generic;
using System.Linq;
using FrostyAnimationEditor.Core.Anim;

namespace FrostyAnimationEditor.Core.Fbx
{
    /// <summary>a "P" property of Properties70: name, type, label, flags, values</summary>
    public sealed class FbxProp
    {
        public string Name, Type, Label, Flags;
        public List<object> Values = new List<object>();

        public double D(int i = 0)
        {
            if (i >= Values.Count) return 0;
            object v = Values[i];
            if (v is double) return (double)v;
            if (v is float) return (float)v;
            if (v is long) return (long)v;
            if (v is int) return (int)v;
            if (v is bool) return (bool)v ? 1 : 0;
            return 0;
        }

        public V3 Vec { get { return new V3(D(0), D(1), D(2)); } }
        public string S { get { return Values.Count > 0 ? Values[0] as string : null; } }

        public static Dictionary<string, FbxProp> Read(FbxNode props70, Dictionary<string, FbxProp> into = null)
        {
            Dictionary<string, FbxProp> r = into ?? new Dictionary<string, FbxProp>(StringComparer.Ordinal);
            if (props70 == null) return r;
            foreach (FbxNode p in props70.All("P"))
            {
                if (p.Props.Count < 4) continue;
                FbxProp fp = new FbxProp { Name = p.Str(0) ?? "", Type = p.Str(1) ?? "", Label = p.Str(2) ?? "", Flags = p.Str(3) ?? "" };
                for (int i = 4; i < p.Props.Count; i++) fp.Values.Add(p.Props[i]);
                r[fp.Name] = fp;
            }
            // FBX 6 style "Property: name, type, flags, values"
            foreach (FbxNode p in props70.All("Property"))
            {
                if (p.Props.Count < 3) continue;
                FbxProp fp = new FbxProp { Name = p.Str(0) ?? "", Type = p.Str(1) ?? "", Label = "", Flags = p.Str(2) ?? "" };
                for (int i = 3; i < p.Props.Count; i++) fp.Values.Add(p.Props[i]);
                r[fp.Name] = fp;
            }
            return r;
        }
    }

    public sealed class FbxObject
    {
        public long Id;
        public string Kind;      // element name: Model, Geometry, NodeAttribute, AnimationCurve, ...
        public string Name;      // object name without the class part
        public string SubType;   // third property: LimbNode, Mesh, Null, Skin, Cluster, BindPose, ...
        public FbxNode Node;
        public Dictionary<string, FbxProp> Props = new Dictionary<string, FbxProp>(StringComparer.Ordinal);

        public FbxProp Prop(string n) { FbxProp p; return Props.TryGetValue(n, out p) ? p : null; }
        public double PD(string n, double d = 0) { FbxProp p = Prop(n); return p != null && p.Values.Count > 0 ? p.D(0) : d; }
        public V3 PV(string n, V3 d) { FbxProp p = Prop(n); return p != null && p.Values.Count >= 3 ? p.Vec : d; }
    }

    public struct FbxConn
    {
        public string Kind;   // OO (object-object) or OP (object-property)
        public long Src, Dst;
        public string Prop;
    }

    /// <summary>An FBX document as objects and connections, with transform and animation-curve evaluation.</summary>
    public sealed class FbxScene
    {
        public const long KTimePerSecond = 46186158000L;

        public int Version;
        public readonly Dictionary<long, FbxObject> Objects = new Dictionary<long, FbxObject>();
        public readonly List<FbxConn> Connections = new List<FbxConn>();
        public Dictionary<string, FbxProp> Global = new Dictionary<string, FbxProp>(StringComparer.Ordinal);
        private readonly Dictionary<long, List<FbxConn>> bySrc = new Dictionary<long, List<FbxConn>>();
        private readonly Dictionary<long, List<FbxConn>> byDst = new Dictionary<long, List<FbxConn>>();

        public static FbxScene Load(byte[] data)
        {
            int ver;
            List<FbxNode> top = FbxIO.Read(data, out ver);
            FbxScene s = new FbxScene { Version = ver };
            s.Build(top);
            return s;
        }

        private void Build(List<FbxNode> top)
        {
            FbxNode gs = top.FirstOrDefault(n => n.Name == "GlobalSettings");
            if (gs != null) Global = FbxProp.Read(gs.Find("Properties70"));
            Dictionary<string, Dictionary<string, FbxProp>> templates = new Dictionary<string, Dictionary<string, FbxProp>>(StringComparer.Ordinal);
            FbxNode defs = top.FirstOrDefault(n => n.Name == "Definitions");
            if (defs != null)
                foreach (FbxNode ot in defs.All("ObjectType"))
                {
                    string kind = ot.Str(0) ?? "";
                    foreach (FbxNode pt in ot.All("PropertyTemplate"))
                    {
                        Dictionary<string, FbxProp> d;
                        if (!templates.TryGetValue(kind, out d)) { d = new Dictionary<string, FbxProp>(StringComparer.Ordinal); templates[kind] = d; }
                        FbxProp.Read(pt.Find("Properties70"), d);
                    }
                }
            FbxNode objs = top.FirstOrDefault(n => n.Name == "Objects");
            if (objs != null)
                foreach (FbxNode o in objs.Children)
                {
                    if (o.Props.Count < 1) continue;
                    long id;
                    string nm, sub;
                    if (o.P(0) is string)
                    {
                        // FBX 6: Model: "Model::Name", "Type" (no ids; the name is the id)
                        nm = o.Str(0);
                        id = NameId(nm);
                        sub = o.Str(1) ?? "";
                    }
                    else
                    {
                        id = o.Long(0);
                        nm = o.Str(1) ?? "";
                        sub = o.Str(2) ?? "";
                    }
                    string name, cls;
                    FbxNode.SplitName(nm, out name, out cls);
                    FbxObject fo = new FbxObject { Id = id, Kind = o.Name, Name = name, SubType = sub, Node = o };
                    Dictionary<string, FbxProp> t;
                    if (templates.TryGetValue(o.Name, out t)) foreach (KeyValuePair<string, FbxProp> kv in t) fo.Props[kv.Key] = kv.Value;
                    FbxProp.Read(o.Find("Properties70"), fo.Props);
                    FbxProp.Read(o.Find("Properties60"), fo.Props);
                    Objects[id] = fo;
                }
            FbxNode conns = top.FirstOrDefault(n => n.Name == "Connections");
            if (conns != null)
                foreach (FbxNode c in conns.Children)
                {
                    if (c.Name != "C" && c.Name != "Connect") continue;
                    FbxConn fc = new FbxConn { Kind = c.Str(0) ?? "OO" };
                    if (c.P(1) is string) { fc.Src = NameId(c.Str(1)); fc.Dst = c.P(2) is string ? NameId(c.Str(2)) : c.Long(2); }
                    else { fc.Src = c.Long(1); fc.Dst = c.Long(2); }
                    fc.Prop = c.Props.Count > 3 ? c.Str(3) : null;
                    Connections.Add(fc);
                    List<FbxConn> l;
                    if (!bySrc.TryGetValue(fc.Src, out l)) { l = new List<FbxConn>(); bySrc[fc.Src] = l; }
                    l.Add(fc);
                    if (!byDst.TryGetValue(fc.Dst, out l)) { l = new List<FbxConn>(); byDst[fc.Dst] = l; }
                    l.Add(fc);
                }
        }

        private static long NameId(string s)
        {
            if (string.IsNullOrEmpty(s) || s == "Scene" || s == "Model::Scene") return 0;
            unchecked
            {
                long h = 1469598103934665603L;
                foreach (char c in s) { h ^= c; h *= 1099511628211L; }
                return h == 0 ? 1 : h;
            }
        }

        public IEnumerable<FbxConn> From(long id) { List<FbxConn> l; return bySrc.TryGetValue(id, out l) ? l : Enumerable.Empty<FbxConn>(); }
        public IEnumerable<FbxConn> To(long id) { List<FbxConn> l; return byDst.TryGetValue(id, out l) ? l : Enumerable.Empty<FbxConn>(); }

        public FbxObject Get(long id) { FbxObject o; return Objects.TryGetValue(id, out o) ? o : null; }

        /// <summary>the Model a Model is parented to (null for the scene root)</summary>
        public FbxObject ParentModel(FbxObject m)
        {
            foreach (FbxConn c in From(m.Id))
            {
                if (c.Kind != "OO") continue;
                FbxObject p = Get(c.Dst);
                if (p != null && p.Kind == "Model") return p;
            }
            return null;
        }

        public IEnumerable<FbxObject> Models { get { return Objects.Values.Where(o => o.Kind == "Model"); } }

        /// <summary>meters per file unit (GlobalSettings UnitScaleFactor is centimeters per unit; default 1 cm)</summary>
        public double MetersPerUnit
        {
            get
            {
                FbxProp p;
                double u = Global.TryGetValue("UnitScaleFactor", out p) ? p.D(0) : 1.0;
                return (u > 0 ? u : 1.0) / 100.0;
            }
        }

        private int GI(string n, int d) { FbxProp p; return Global.TryGetValue(n, out p) && p.Values.Count > 0 ? (int)p.D(0) : d; }

        /// <summary>rotation taking the file's axes to Y up, Z front, X coord (right-handed, the editor's frame)</summary>
        public Affine AxisToYUp()
        {
            int up = GI("UpAxis", 1), upS = GI("UpAxisSign", 1), fr = GI("FrontAxis", 2), frS = GI("FrontAxisSign", 1), co = GI("CoordAxis", 0), coS = GI("CoordAxisSign", 1);
            if (up < 0 || up > 2 || fr < 0 || fr > 2 || co < 0 || co > 2 || up == fr || up == co || fr == co) return Affine.Identity;
            double[] m = new double[9];
            m[0 * 3 + co] = coS >= 0 ? 1 : -1;
            m[1 * 3 + up] = upS >= 0 ? 1 : -1;
            m[2 * 3 + fr] = frS >= 0 ? 1 : -1;
            return Affine.FromRot3(m, V3.Zero);
        }

        /// <summary>frames per second of the file (GlobalSettings TimeMode / CustomFrameRate), 0 when unknown</summary>
        public double FileFps
        {
            get
            {
                int mode = GI("TimeMode", 0);
                switch (mode)
                {
                    case 1: return 120; case 2: return 100; case 3: return 60; case 4: return 50; case 5: return 48;
                    case 6: case 7: return 30; case 8: case 9: return 30 / 1.001; case 10: return 25; case 11: return 24;
                    case 13: return 24 / 1.001; case 15: return 96; case 16: return 72; case 17: return 60 / 1.001; case 18: return 120 / 1.001;
                }
                FbxProp p;
                if (Global.TryGetValue("CustomFrameRate", out p) && p.D(0) > 0) return p.D(0);
                return 0;
            }
        }

        // ------------------------------------------------------------------ animation
        public sealed class Curve
        {
            public long[] Times;
            public float[] Values;
            public int[] Flags;       // per key
            public float[] Data;      // per key: 4 floats (right slope, next left slope, weights, velocity)
            public double Default;

            public double Eval(long t)
            {
                int n = Times.Length;
                if (n == 0) return Default;
                if (t <= Times[0]) return Values[0];
                if (t >= Times[n - 1]) return Values[n - 1];
                int lo = 0, hi = n - 1;
                while (hi - lo > 1) { int mid = (lo + hi) / 2; if (Times[mid] <= t) lo = mid; else hi = mid; }
                double p0 = Values[lo], p1 = Values[hi];
                int fl = Flags[lo];
                if ((fl & 0x2) != 0) return (fl & 0x100) != 0 ? p1 : p0;     // constant (0x100: constant next)
                double dt = (Times[hi] - Times[lo]) / (double)KTimePerSecond;
                double u = (t - Times[lo]) / (double)(Times[hi] - Times[lo]);
                if ((fl & 0x8) == 0 || dt <= 0) return p0 + (p1 - p0) * u;  // linear
                double m0 = Data[lo * 4], m1 = Data[lo * 4 + 1];              // slopes in value per second
                double u2 = u * u, u3 = u2 * u;
                return (2 * u3 - 3 * u2 + 1) * p0 + (u3 - 2 * u2 + u) * dt * m0 + (-2 * u3 + 3 * u2) * p1 + (u3 - u2) * dt * m1;
            }
        }

        public Curve ReadCurve(FbxObject o)
        {
            FbxNode n = o.Node;
            Curve c = new Curve { Default = n.ChildDouble("Default", 0) };
            FbxNode kt = n.Find("KeyTime"), kv = n.Find("KeyValueFloat") ?? n.Find("KeyValueDouble");
            c.Times = kt != null ? kt.Longs(0) : new long[0];
            double[] vals = kv != null ? kv.Doubles(0) : new double[0];
            int k = Math.Min(c.Times.Length, vals.Length);
            if (k < c.Times.Length) c.Times = c.Times.Take(k).ToArray();
            c.Values = vals.Take(k).Select(x => (float)x).ToArray();
            int[] flags = n.Find("KeyAttrFlags") != null ? n.Find("KeyAttrFlags").Ints(0) : new int[0];
            double[] data = n.Find("KeyAttrDataFloat") != null ? n.Find("KeyAttrDataFloat").Doubles(0) : new double[0];
            int[] refc = n.Find("KeyAttrRefCount") != null ? n.Find("KeyAttrRefCount").Ints(0) : new int[0];
            c.Flags = new int[k];
            c.Data = new float[k * 4];
            int key = 0;
            for (int a = 0; a < refc.Length && key < k; a++)
                for (int r = 0; r < refc[a] && key < k; r++, key++)
                {
                    c.Flags[key] = a < flags.Length ? flags[a] : 0x4;
                    for (int j = 0; j < 4; j++) c.Data[key * 4 + j] = a * 4 + j < data.Length ? (float)data[a * 4 + j] : 0f;
                }
            for (; key < k; key++) c.Flags[key] = 0x4;
            return c;
        }

        /// <summary>animated property of a model: ("Lcl Translation" etc.) -> curves for X, Y, Z (null where absent) and
        /// the curve node's default values</summary>
        public sealed class AnimProp
        {
            public Curve[] Curves = new Curve[3];
            public double[] Defaults = new double[3];
            public bool Any { get { return Curves.Any(c => c != null); } }

            public V3 Eval(long t, V3 stat)
            {
                double[] r = { stat.X, stat.Y, stat.Z };
                for (int i = 0; i < 3; i++) if (Curves[i] != null) r[i] = Curves[i].Eval(t);
                return new V3(r[0], r[1], r[2]);
            }
        }

        public sealed class Take
        {
            public string Name;
            public long Start, Stop;
            public Dictionary<long, Dictionary<string, AnimProp>> Props = new Dictionary<long, Dictionary<string, AnimProp>>();
        }

        public List<string> StackNames() { return Objects.Values.Where(o => o.Kind == "AnimationStack").Select(o => o.Name).ToList(); }

        /// <summary>the animation of a stack (null name: the first stack); only the stack's first layer is read</summary>
        public Take ReadTake(string stackName = null)
        {
            List<FbxObject> stacks = Objects.Values.Where(o => o.Kind == "AnimationStack").OrderBy(o => o.Id == 0 ? 0 : 1).ToList();
            FbxObject stack = stackName != null ? stacks.FirstOrDefault(s => s.Name == stackName) : null;
            if (stack == null) stack = stacks.FirstOrDefault();
            Take take = new Take { Name = stack != null ? stack.Name : "" };
            List<FbxObject> layers = new List<FbxObject>();
            if (stack != null)
                foreach (FbxConn c in To(stack.Id))
                {
                    FbxObject l = Get(c.Src);
                    if (l != null && l.Kind == "AnimationLayer") layers.Add(l);
                }
            if (stack == null) layers.AddRange(Objects.Values.Where(o => o.Kind == "AnimationLayer").Take(1));
            FbxObject layer = layers.FirstOrDefault();
            List<FbxObject> nodes = new List<FbxObject>();
            if (layer != null)
                foreach (FbxConn c in To(layer.Id))
                {
                    FbxObject cn = Get(c.Src);
                    if (cn != null && cn.Kind == "AnimationCurveNode") nodes.Add(cn);
                }
            else nodes.AddRange(Objects.Values.Where(o => o.Kind == "AnimationCurveNode"));
            long tmin = long.MaxValue, tmax = long.MinValue;
            foreach (FbxObject cn in nodes)
            {
                AnimProp ap = new AnimProp();
                string[] comps = { "d|X", "d|Y", "d|Z" };
                for (int i = 0; i < 3; i++) ap.Defaults[i] = cn.PD(comps[i], 0);
                foreach (FbxConn c in To(cn.Id))
                {
                    FbxObject cu = Get(c.Src);
                    if (cu == null || cu.Kind != "AnimationCurve") continue;
                    int ix = Array.IndexOf(comps, c.Prop ?? "");
                    if (ix < 0) continue;
                    Curve curve = ReadCurve(cu);
                    ap.Curves[ix] = curve;
                    if (curve.Times.Length > 0) { tmin = Math.Min(tmin, curve.Times[0]); tmax = Math.Max(tmax, curve.Times[curve.Times.Length - 1]); }
                }
                foreach (FbxConn c in From(cn.Id))
                {
                    if (c.Kind != "OP" || c.Prop == null) continue;
                    FbxObject m = Get(c.Dst);
                    if (m == null || m.Kind != "Model") continue;
                    Dictionary<string, AnimProp> d;
                    if (!take.Props.TryGetValue(m.Id, out d)) { d = new Dictionary<string, AnimProp>(StringComparer.Ordinal); take.Props[m.Id] = d; }
                    d[c.Prop] = ap;
                }
            }
            long ls = 0, le = 0;
            if (stack != null)
            {
                FbxProp a = stack.Prop("LocalStart"), b = stack.Prop("LocalStop");
                if (a != null) ls = (long)a.D(0);
                if (b != null) le = (long)b.D(0);
            }
            if (le > ls) { take.Start = ls; take.Stop = le; }
            else if (tmax >= tmin && tmin != long.MaxValue) { take.Start = tmin; take.Stop = tmax; }
            return take;
        }

        // ------------------------------------------------------------------ transforms
        /// <summary>local transform of a model at time t (FBX: T Roff Rp Rpre R Rpost^-1 Rp^-1 Soff Sp S Sp^-1)</summary>
        public Affine LocalMatrix(FbxObject m, Take take, long t)
        {
            Dictionary<string, AnimProp> anim = null;
            if (take != null) take.Props.TryGetValue(m.Id, out anim);
            V3 tr = m.PV("Lcl Translation", V3.Zero), rot = m.PV("Lcl Rotation", V3.Zero), sc = m.PV("Lcl Scaling", V3.One);
            AnimProp ap;
            if (anim != null)
            {
                if (anim.TryGetValue("Lcl Translation", out ap)) tr = ap.Eval(t, tr);
                if (anim.TryGetValue("Lcl Rotation", out ap)) rot = ap.Eval(t, rot);
                if (anim.TryGetValue("Lcl Scaling", out ap)) sc = ap.Eval(t, sc);
            }
            // as the FBX SDK (and Blender): rotation order and pre / post rotation apply when RotationActive is set
            bool active = m.PD("RotationActive", 0) != 0;
            int order = active ? (int)m.PD("RotationOrder", 0) : 0;
            V3 pre = active ? m.PV("PreRotation", V3.Zero) : V3.Zero, post = active ? m.PV("PostRotation", V3.Zero) : V3.Zero;
            V3 roff = m.PV("RotationOffset", V3.Zero), rpiv = m.PV("RotationPivot", V3.Zero), soff = m.PV("ScalingOffset", V3.Zero), spiv = m.PV("ScalingPivot", V3.Zero);
            Affine R = Affine.FromRot3(Euler.ToMatrix(rot, order), V3.Zero);
            Affine Rpre = Affine.FromRot3(Euler.ToMatrix(pre, 0), V3.Zero);
            Affine Rpost = Affine.FromRot3(Euler.ToMatrix(post, 0), V3.Zero);
            return Affine.Translate(tr) * Affine.Translate(roff) * Affine.Translate(rpiv) * Rpre * R * Rpost.Inverse() * Affine.Translate(-rpiv)
                   * Affine.Translate(soff) * Affine.Translate(spiv) * Affine.Scale(sc) * Affine.Translate(-spiv);
        }

        /// <summary>global transforms of the given models at time t (file space), parents included as needed</summary>
        public Dictionary<long, Affine> Globals(IEnumerable<FbxObject> models, Take take, long t, Dictionary<long, Affine> cache = null)
        {
            Dictionary<long, Affine> r = cache ?? new Dictionary<long, Affine>();
            foreach (FbxObject m in models) GlobalOf(m, take, t, r, 0);
            return r;
        }

        private Affine GlobalOf(FbxObject m, Take take, long t, Dictionary<long, Affine> memo, int depth)
        {
            Affine g;
            if (memo.TryGetValue(m.Id, out g)) return g;
            Affine l = LocalMatrix(m, take, t);
            FbxObject p = depth < 1024 ? ParentModel(m) : null;
            if (p != null)
            {
                Affine pg = GlobalOf(p, take, t, memo, depth + 1);
                int inherit = (int)m.PD("InheritType", 1);
                if (inherit == 2)
                {
                    // Rrs: the parent's scale is not inherited
                    V3 pt, ps;
                    Q4 pr;
                    pg.Decompose(out pt, out pr, out ps);
                    pg = Affine.FromTRS(pt, pr, V3.One);
                }
                g = pg * l;
            }
            else g = l;
            memo[m.Id] = g;
            return g;
        }
    }
}
