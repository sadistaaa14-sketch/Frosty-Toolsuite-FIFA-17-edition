using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using FrostyAnimationEditor.Core.Anim;

namespace FrostyAnimationEditor.Core
{
    public sealed partial class EditorModel
    {
        // ------------------------------------------------------------------ animation
        /// <summary>(clip key, controller key or 0) for a clip or a ClipControllerAsset</summary>
        public ulong ClipKey(object key, out ulong controller)
        {
            GdObj o = Root(key);
            controller = 0;
            if (o.Type.Name == "ClipControllerAsset")
            {
                ulong k = o.GetKey("Anim");
                if (!Db.Contains(k)) throw new ApiError("this controller's animation is not in this file (it is imported from another container)");
                controller = o.Key;
                return k;
            }
            if (!ClipTypes.Contains(o.Type.Name)) throw new ApiError("not an animation clip");
            return o.Key;
        }

        public JObject Clip(object key)
        {
            ulong ctrl;
            ulong k = ClipKey(key, out ctrl);
            AnimClip c = AnimClip.Load(Db, k, Rig());
            List<GdObj> ctrls = AnimOps.FindControllers(Db, k);
            GdObj rig = Rig().BestRig(c.LayoutKey, c.DofIds);
            JObject r = new JObject();
            r["clip"] = H(k); r["name"] = NameOf(Db.Get(k)); r["codec"] = c.Codec; r["frames"] = (long)c.FrameCount();
            r["channels"] = c.Channels.Select(ch => { JObject j = new JObject(); j["name"] = ch.Name; j["kind"] = ch.Kind; return (object)j; }).ToList();
            r["controllers"] = ctrls.Select(x =>
            {
                JObject j = new JObject();
                j["key"] = H(x.Key); j["name"] = NameOf(x); j["fps"] = JsonIO.F(x.GetFloat("FPS")); j["timescale"] = JsonIO.F(x.GetFloat("TimeScale"));
                return (object)j;
            }).ToList();
            r["rig"] = rig != null ? (object)NameOf(rig) : null;
            return r;
        }

        public Pose.World ClipPreview(object key, int maxFrames = 300)
        {
            ulong ctrl;
            ulong k = ClipKey(key, out ctrl);
            return Pose.WorldPositions(Db, k, Rig(), null, maxFrames);
        }

        private static readonly Regex Unsafe = new Regex(@"[^A-Za-z0-9_.-]+");

        /// <summary>(file name, bytes) of an exported clip: fmt "antanim" (.antanim.json for Blender) or "bvh"</summary>
        public Tuple<string, byte[]> ClipExport(object key, string fmt = "antanim", double scale = 1.0, bool resample = false)
        {
            ulong ctrl;
            ulong k = ClipKey(key, out ctrl);
            GdObj o = Db.Get(k);
            string name = Unsafe.Replace(string.IsNullOrEmpty(o.Name) ? H(k) : o.Name, "_");
            if (fmt == "bvh") return Tuple.Create(name + ".bvh", new UTF8Encoding(false).GetBytes(Bvh.Export(Db, k, Rig(), scale)));
            JObject doc = AntAnim.ExportClip(Db, k, Rig(), resample);
            return Tuple.Create(name + ".antanim.json", new UTF8Encoding(false).GetBytes(MiniJson.Serialize(doc, 0)));
        }

        private ClipData ClipDataFromUpload(string fmt, byte[] data, IList<Channel> channels, IList<float[][]> fallback)
        {
            string text = new UTF8Encoding(false).GetString(data);
            if (text.Length > 0 && text[0] == '﻿') text = text.Substring(1);
            try
            {
                if (fmt == "bvh") return Bvh.ToClipData(text, channels, 1.0, fallback);
                JObject doc = MiniJson.Parse(text) as JObject;
                if (doc == null) throw new ApiError("the file is not an .antanim.json document");
                return AntAnim.ToClipData(doc, channels.Select(c => c.Name).ToList());
            }
            catch (ApiError) { throw; }
            catch (Exception e) when (!(e is OutOfMemoryException)) { throw new ApiError("the file does not read: " + e.Message); }
        }

        private static string Codec4(string codec) { return ((codec ?? "VBR") + " ").Substring(0, 4); }

        public JObject ClipReplace(object key, string fmt, byte[] data, string codec = "VBR", double tolerance = Encode.TolDefault)
        {
            ulong ctrl;
            ulong k = ClipKey(key, out ctrl);
            AnimClip c = AnimClip.Load(Db, k, Rig());
            ClipData cd = ClipDataFromUpload(fmt, data, c.Channels, fmt == "bvh" ? c.Sample() : null);
            EncodeReport rep = null;
            Change("replace animation " + NameOf(Db.Get(k)), new[] { k }, () => rep = AnimOps.Replace(Db, k, cd, Codec4(codec), tolerance));
            ResetRig();
            Say("replaced animation " + NameOf(Db.Get(k)) + ": " + rep);
            JObject r = new JObject();
            r["report"] = rep.ToString();
            r["key"] = H(k);
            return r;
        }

        public JObject ClipAdd(object template, string fmt, byte[] data, string codec = "VBR", double tolerance = Encode.TolDefault, string name = null,
                               object controller = null, bool makeController = true, string ctrlName = null)
        {
            ulong ctrl;
            ulong tk = ClipKey(template, out ctrl);
            AnimClip tpl = AnimClip.Load(Db, tk, Rig());
            ClipData cd = ClipDataFromUpload(fmt, data, tpl.Channels, fmt == "bvh" ? tpl.Sample() : null);
            ulong k = 0, ck = 0;
            EncodeReport rep = null;
            Change("add animation " + (name ?? ""), new ulong[0], () =>
            {
                k = AnimOps.Add(Db, cd, tk, string.IsNullOrEmpty(name) ? null : name, 0, 0, Codec4(codec), tolerance, null, out rep);
                ulong tc = controller != null && !(controller is string && ((string)controller).Length == 0) ? K(controller) : ctrl;
                if (tc == 0)
                {
                    List<GdObj> cs = AnimOps.FindControllers(Db, tk);
                    tc = cs.Count > 0 ? cs[0].Key : 0;
                }
                if (tc != 0 && makeController) ck = AnimOps.AddClipController(Db, tc, k, string.IsNullOrEmpty(ctrlName) ? null : ctrlName);
            });
            ResetRig();
            Say("added animation " + H(k) + " " + rep);
            JObject r = new JObject();
            r["key"] = H(k);
            r["controller"] = ck != 0 ? (object)H(ck) : null;
            r["report"] = rep.ToString();
            return r;
        }

        // ------------------------------------------------------------------ state machines
        public ulong SmController(object key)
        {
            GdObj o = Root(key);
            if (o.Type.Name == "StateFlowControllerAsset") return o.Key;
            if (o.Type.Name == "StateFlowNodeControllerAsset") return o.GetKey("StateFlowController");
            if (o.Type.Name == "StateFlowTransitionAsset")
            {
                GdObj n = Db.Get(o.GetKey("From"));
                return n != null ? n.GetKey("StateFlowController") : 0;
            }
            throw new ApiError("not a state machine object");
        }

        private List<object> InfoList(GdStruct o, string field)
        {
            IList l = o[field] as IList;
            return l == null ? new List<object>() : l.Cast<object>().Select(x => (object)Info(Num.ToKey(x))).ToList();
        }

        public JObject Sm(object key)
        {
            ulong ck = SmController(key);
            GdObj c = Db.Get(ck);
            if (c == null) throw new ApiError("the state machine controller " + H(ck) + " is not in this file");
            ulong start = c.GetKey("StartNode");
            List<GdObj> members = StateMachine.States(Db, ck)
                .OrderBy(n => n.Key != start)
                .ThenBy(n => NameOf(n).ToLowerInvariant(), StringComparer.Ordinal).ToList();
            List<object> states = new List<object>();
            foreach (GdObj n in members)
            {
                List<object> trs = new List<object>();
                foreach (object t in n["Transitions"] as IList ?? new object[0])
                {
                    ulong tk = Num.ToKey(t);
                    GdObj tr = Db.Get(tk);
                    if (tr == null) continue;
                    JObject j = new JObject();
                    j["key"] = H(tk); j["name"] = NameOf(tr); j["to"] = Info(tr.GetKey("To"));
                    j["true"] = InfoList(tr, "ConditionsRequiredTrue"); j["false"] = InfoList(tr, "ConditionsRequiredFalse");
                    j["flags"] = new[] { "BranchAtEnd", "BranchInAny", "BranchAfterFirstUpdate" }.Where(f => tr.Has(f) && tr.GetBool(f)).Cast<object>().ToList();
                    j["window"] = Info(tr.GetKey("BranchWindowTypeAsset"));
                    trs.Add(j);
                }
                JObject s = new JObject();
                s["key"] = H(n.Key); s["name"] = NameOf(n); s["node_type"] = n.GetInt("NodeType"); s["subject"] = Info(n.GetKey("SubjectController"));
                s["transitions"] = trs; s["flattened"] = (long)(n["FlattenedTransitions"] is IList ? ((IList)n["FlattenedTransitions"]).Count : 0);
                states.Add(s);
            }
            JObject cj = new JObject();
            cj["key"] = H(ck); cj["name"] = NameOf(c); cj["start"] = H(start); cj["uses_gates"] = c.GetBool("UsesGateNodes");
            JObject r = new JObject();
            r["controller"] = cj;
            r["states"] = states;
            return r;
        }

        private List<ulong> SmKeys() { return SmTypes.SelectMany(t => Db.BlockIds(t).Select(i => Db.Keys[i])).ToList(); }

        private static object Arg(JObject a, string k) { object v; return a != null && a.TryGetValue(k, out v) ? v : null; }
        private static bool ArgBool(JObject a, string k) { object v = Arg(a, k); return v != null && Num.ToBool(v); }
        private static string ArgStr(JObject a, string k) { object v = Arg(a, k); return v == null ? null : Convert.ToString(v, CultureInfo.InvariantCulture); }
        private static List<ulong> ArgKeys(JObject a, string k) { IList l = Arg(a, k) as IList; return l == null ? new List<ulong>() : l.Cast<object>().Select(K).ToList(); }

        /// <summary>state machine edits: add_state, add_transition, remove_transition, remove_state, set_start,
        /// move_transition, retarget, new, rebuild</summary>
        public JObject SmOp(string op, JObject a)
        {
            JObject res = new JObject();
            Change("state machine: " + op, SmKeys(), () =>
            {
                switch (op)
                {
                    case "add_state":
                        {
                            object subj = Arg(a, "subject");
                            res["key"] = H(StateMachine.AddState(Db, K(Arg(a, "controller")), ArgStr(a, "name"),
                                subj != null && !(subj is string && ((string)subj).Length == 0) ? K(subj) : 0, 0,
                                Arg(a, "node_type") != null ? Num.ToLong(Arg(a, "node_type")) : 0));
                            break;
                        }
                    case "add_transition":
                        res["key"] = H(StateMachine.AddTransition(Db, K(Arg(a, "from")), K(Arg(a, "to")), new StateMachine.TransitionSpec
                        {
                            Name = string.IsNullOrEmpty(ArgStr(a, "name")) ? null : ArgStr(a, "name"),
                            True = ArgKeys(a, "true"), False = ArgKeys(a, "false"),
                            BranchAtEnd = ArgBool(a, "at_end"), BranchAfterFirstUpdate = ArgBool(a, "after_first_update"), BranchInAny = ArgBool(a, "in_any"),
                        }));
                        break;
                    case "remove_transition": StateMachine.RemoveTransition(Db, K(Arg(a, "key"))); break;
                    case "remove_state": StateMachine.RemoveState(Db, K(Arg(a, "key"))); break;
                    case "set_start": StateMachine.SetStart(Db, K(Arg(a, "controller")), K(Arg(a, "key"))); break;
                    case "move_transition": StateMachine.MoveTransition(Db, K(Arg(a, "key")), (int)Num.ToLong(Arg(a, "position"))); break;
                    case "retarget": StateMachine.RetargetTransition(Db, K(Arg(a, "key")), K(Arg(a, "to"))); break;
                    case "new":
                        {
                            Tuple<ulong, ulong> cs = StateMachine.NewController(Db, ArgStr(a, "name"));
                            res["key"] = H(cs.Item1);
                            break;
                        }
                    case "rebuild": res["changed"] = (long)StateMachine.Rebuild(Db); break;
                    default: throw new ApiError("unknown state machine op " + PyFormat.Repr(op));
                }
            });
            Say("state machine: " + op + " " + (res.Str("key") ?? ""));
            return res;
        }

        // ------------------------------------------------------------------ expression kernels
        private GdObj ExprObj(object key)
        {
            GdObj o = Root(key);
            if (o.Type.Name != "ExpressionAsset") throw new ApiError("not an ExpressionAsset");
            return o;
        }

        private static List<object> Issues(IEnumerable<Tuple<string, string>> l)
        {
            return l.Select(t => { JObject j = new JObject(); j["level"] = t.Item1; j["message"] = t.Item2; return (object)j; }).ToList();
        }

        public JObject Expr(object key, bool dbl = false)
        {
            GdObj o = ExprObj(key);
            string text;
            Kernel k;
            try
            {
                text = Kernel.ExportText(Db, o, dbl);
                k = Kernel.Parse(Kernel.BlobOf(o, "ExpressionKernel" + (dbl ? "DBL" : "")));
            }
            catch (KernelException e) { throw new ApiError(e.Message); }
            KernelBindings b = KernelBindings.FromObj(o, dbl);
            Dictionary<ulong, string> names = Kernel.GsLabels(Db, b);
            Tuple<int, Dictionary<string, int>, int> sum = Kernel.Summary(o);
            bool same = Kernel.BlobOf(o, "ExpressionKernel").SequenceEqual(Kernel.BlobOf(o, "ExpressionKernelDBL"));
            JObject r = new JObject();
            r["text"] = text; r["instructions"] = (long)sum.Item1; r["code_bytes"] = (long)sum.Item3;
            JObject nat = new JObject();
            foreach (KeyValuePair<string, int> kv in sum.Item2) nat[kv.Key] = (long)kv.Value;
            r["natives"] = nat;
            r["dbl_same"] = same;
            r["gs"] = b.Gs.Select((g, j) =>
            {
                JObject x = new JObject();
                string iface;
                if (!Kernel.Ifaces.TryGetValue(g.Item3, out iface)) iface = "0x" + g.Item3.ToString("x8");
                string lab;
                x["name"] = "gs" + j; x["kind"] = iface; x["asset"] = names.TryGetValue(g.Item1, out lab) ? lab : "";
                return (object)x;
            }).ToList();
            r["dofs"] = b.Dofs.Select(d => (object)(d.Item1 + "." + d.Item2)).ToList();
            r["issues"] = Issues(Kernel.Check(k, b));
            return r;
        }

        public JObject ExprCheck(string text)
        {
            JObject r = new JObject();
            Kernel k;
            KernelBindings b;
            List<string> notes;
            try { k = KernelText.Assemble(text, out b, out notes); }
            catch (KernelException e) { r["ok"] = false; r["problem"] = e.Message; r["notes"] = new List<object>(); r["issues"] = new List<object>(); return r; }
            catch (Exception e) when (e is FormatException || e is OverflowException || e is ArgumentException || e is InvalidOperationException)
            {
                r["ok"] = false; r["problem"] = e.Message; r["notes"] = new List<object>(); r["issues"] = new List<object>(); return r;
            }
            List<Tuple<string, string>> iss = Kernel.Check(k, b);
            r["ok"] = !iss.Any(t => t.Item1 == "error");
            r["notes"] = notes.Cast<object>().ToList();
            r["issues"] = Issues(iss);
            r["instructions"] = (long)k.Code.Count;
            r["bytes"] = (long)k.Serialize().Length;
            return r;
        }

        /// <summary>assemble text into the ExpressionAsset; mode "both" (ExpressionKernel and the DBL copy), "main" or "dbl"</summary>
        public JObject ExprApply(object key, string text, string mode = "both", bool force = false)
        {
            GdObj o = ExprObj(key);
            Kernel.ImportResult res = null;
            Change("expression kernel of " + H(o.Key), new[] { o.Key }, () =>
            {
                try { res = Kernel.ImportText(Db.Get(o.Key), text, mode, force); }
                catch (KernelException e) { throw new ApiError(e.Message); }
                catch (Exception e) when (e is FormatException || e is OverflowException || e is ArgumentException || e is InvalidOperationException) { throw new ApiError(e.Message); }
            });
            Say("expression " + H(o.Key) + ": " + (res.Changed.Count > 0 ? string.Join(", ", res.Changed) : "no change"));
            JObject r = new JObject();
            r["changed"] = res.Changed.Cast<object>().ToList();
            r["notes"] = res.Notes.Cast<object>().ToList();
            r["issues"] = Issues(res.Issues);
            return r;
        }

        private static object GsInput(object v)
        {
            IList l = v as IList;
            if (l != null && !(v is string)) return l.Cast<object>().Select(Num.ToDouble).ToArray();
            if (v is bool) return v;
            if (v is long || v is int) return Convert.ToInt64(v);
            if (v is string)
            {
                string s = ((string)v).Trim();
                if (s == "true" || s == "false") return s == "true";
                return PyFormat.ParseFloat(s);
            }
            return Num.ToDouble(v);
        }

        /// <summary>run the kernel (the given text, or the object's ExpressionKernel) for frames evaluations with game-state
        /// values gs {gsN: value} and DOF values dof {"Group.Dof": [x, y, z, w]}</summary>
        public JObject ExprRun(object key, string text, IEnumerable<KeyValuePair<string, object>> gs, IEnumerable<KeyValuePair<string, object>> dof, int frames = 1, long deltaTicks = 1)
        {
            GdObj o = ExprObj(key);
            Kernel k;
            KernelBindings b;
            try
            {
                if (!string.IsNullOrEmpty(text))
                {
                    List<string> notes;
                    k = KernelText.Assemble(text, out b, out notes);
                    if (b == null) b = KernelBindings.FromObj(o);
                }
                else
                {
                    k = Kernel.Parse(Kernel.BlobOf(o, "ExpressionKernel"));
                    b = KernelBindings.FromObj(o);
                }
            }
            catch (KernelException e) { throw new ApiError(e.Message); }
            catch (Exception e) when (e is FormatException || e is OverflowException || e is ArgumentException) { throw new ApiError(e.Message); }
            Dictionary<string, object> gv = new Dictionary<string, object>();
            if (gs != null)
                foreach (KeyValuePair<string, object> kv in gs)
                {
                    if (kv.Value == null || (kv.Value is string && ((string)kv.Value).Trim().Length == 0)) continue;
                    gv[kv.Key] = GsInput(kv.Value);
                }
            Dictionary<string, float[]> pose = new Dictionary<string, float[]>();
            if (dof != null)
                foreach (KeyValuePair<string, object> kv in dof)
                {
                    if (kv.Value == null || (kv.Value is string && ((string)kv.Value).Trim().Length == 0)) continue;
                    IList l = kv.Value as IList;
                    pose[kv.Key] = l != null && !(kv.Value is string) ? l.Cast<object>().Select(Num.ToFloat).ToArray() : new[] { Num.ToFloat(kv.Value) };
                }
            Machine m;
            try { m = new Machine(k, b, gv, new VmPose(pose), null, deltaTicks); }
            catch (VMError e) { throw new ApiError(e.Message); }
            List<object> outp = new List<object>();
            int nf = Math.Max(1, Math.Min(1000, frames));
            for (int f = 0; f < nf; f++)
            {
                int n0 = m.Writes.Count;
                m.Steps = 0;
                JObject fr = new JObject();
                try { m.Run(); }
                catch (Exception e) when (e is VMError || e is NotSimulated || e is InvalidOperationException || e is ArgumentOutOfRangeException)
                {
                    fr["error"] = e.Message;
                    outp.Add(fr);
                    break;
                }
                fr["instructions"] = (long)m.Steps;
                fr["writes"] = m.Writes.Skip(n0).Select(w =>
                {
                    JObject j = new JObject();
                    j["kind"] = w.Kind; j["target"] = w.Target;
                    j["value"] = w.Value is double[] ? ((double[])w.Value).Cast<object>().ToList() : w.Value;
                    return (object)j;
                }).ToList();
                outp.Add(fr);
            }
            JObject r = new JObject();
            r["frames"] = outp;
            return r;
        }

        // ------------------------------------------------------------------ blend spaces
        private static List<object> Xy(object v) { IList l = v as IList; return l == null ? new List<object> { 0f, 0f } : new List<object> { Num.ToFloat(l[0]), Num.ToFloat(l[1]) }; }

        public JObject Blend(object key)
        {
            GdObj o = Root(key);
            if (o.Type.Name != "BlendSpace") throw new ApiError("not a BlendSpace");
            GdStruct v = o["VoronoiDiagram"] as GdStruct;
            if (v == null) throw new ApiError("the BlendSpace has no VoronoiDiagram");
            int i = Db.BlockOf(o.Key);
            JObject r = new JObject();
            r["items"] = ((IList)v["Items"] ?? new object[0]).Cast<GdStruct>().Select(it =>
            {
                JObject j = new JObject();
                IList p = (IList)it["Position"];
                j["x"] = Num.ToFloat(p[0]); j["y"] = Num.ToFloat(p[1]); j["asset"] = Info(it.GetKey("BlendAsset"));
                j["xaxis"] = it.GetBool("XAxis"); j["snap"] = it.GetBool("Snap");
                return (object)j;
            }).ToList();
            r["triangles"] = ((IList)v["Triangles"] ?? new object[0]).Cast<GdStruct>().Select(t => (object)((IList)t["V"]).Cast<object>().Select(Num.ToLong).Cast<object>().ToList()).ToList();
            r["edges"] = ((IList)v["Edges"] ?? new object[0]).Cast<GdStruct>().Select(e => (object)new List<object> { Xy(e["PosBegin"]), Xy(e["PosEnd"]) }).ToList();
            r["min"] = Xy(v["PosMin"]); r["max"] = Xy(v["PosMax"]); r["centroid"] = Xy(v["PosCentroid"]);
            r["grid"] = new List<object> { Num.ToLong(v["NumColumns"]), Num.ToLong(v["NumRows"]) };
            r["changed"] = BlendSpaces.ItemsChanged(Db, i);
            JObject mode = new JObject();
            mode["BlendMode"] = o["BlendMode"]; mode["ClampMode"] = o["ClampMode"]; mode["ValueType"] = o["ValueType"]; mode["SnapTolerance"] = o["SnapTolerance"];
            r["mode"] = mode;
            return r;
        }

        public JObject BlendRebuild(object key, bool newGrid = false)
        {
            GdObj o = Root(key);
            Change("rebuild blend space " + NameOf(o), new[] { o.Key }, () => BlendSpaces.Rebuild(Db, Db.Get(o.Key), !newGrid));
            return Blend(key);
        }

        // ------------------------------------------------------------------ curves
        public JObject Curves(object key)
        {
            GdObj o = Root(key);
            JObject r = new JObject();
            if (o.Type.Name == "DofWeightCurvesAsset")
            {
                List<double> ts = Enumerable.Range(0, 51).Select(x => x / 50.0).ToList();
                List<object> sets = new List<object>();
                List<Curves.WeightSet> ws = Core.Curves.WeightSets(Db, o);
                for (int j = 0; j < ws.Count; j++)
                {
                    List<object> cs = new List<object>();
                    for (int i = 0; i < ws[j].Curves.Count; i++)
                    {
                        byte[] cb = ws[j].Curves[i].Item1;
                        int m = ws[j].Curves[i].Item2;
                        if (cb.Length != 4) continue;
                        Curves.CurveParams d = Core.Curves.DecodeCurve(cb);
                        JObject c = new JObject();
                        c["start"] = d.Start; c["length"] = d.Length; c["slope_in"] = d.SlopeIn; c["slope_out"] = d.SlopeOut; c["step"] = d.Step; c["bytes"] = d.Hex;
                        c["mask"] = (long)m; c["dof"] = (long)i;
                        c["w"] = ts.Select(t => (object)JsonIO.F(Core.Curves.Weight(cb, (float)t, m))).ToList();
                        cs.Add(c);
                    }
                    JObject sj = new JObject();
                    sj["which"] = j == 0 ? (object)"default" : (long)(j - 1); sj["name"] = ws[j].Name; sj["each"] = ws[j].ForEachDof; sj["curves"] = cs;
                    sets.Add(sj);
                }
                r["kind"] = "weights"; r["t"] = ts.Cast<object>().ToList(); r["sets"] = sets;
                r["issues"] = Core.Curves.WeightIssues(o).Cast<object>().ToList();
                return r;
            }
            if (o.Type.Name == "FloatGameStateTag")
            {
                List<double> ts = Enumerable.Range(0, 101).Select(x => x / 100.0).ToList();
                Curves.TagInfo ti = Core.Curves.GetTagInfo(o);
                r["storage"] = ti.Storage; r["flags"] = ti.Flags; r["iterations"] = ti.Iterations.HasValue ? (object)ti.Iterations.Value : null;
                r["data"] = ti.Data.Cast<object>().ToList(); r["mode"] = ti.Mode; r["mode_name"] = ti.ModeName; r["value"] = (double)ti.Value;
                r["reset_on_leave"] = ti.ResetOnLeave; r["reset_name"] = ti.ResetName;
                r["value_asset"] = ti.ValueAsset != null ? (object)Info(Num.ToKey(ti.ValueAsset)) : null;
                r["kind"] = "tag"; r["t"] = ts.Cast<object>().ToList();
                r["curve"] = ts.Select(t => { float? c = Core.Curves.TagCurve(o, (float)t); return c.HasValue ? JsonIO.F(c.Value) : null; }).ToList();
                r["issues"] = Core.Curves.TagIssues(o).Cast<object>().ToList();
                JObject modes = new JObject();
                foreach (KeyValuePair<int, string> kv in Core.Curves.TagModes) modes[kv.Key.ToString(CultureInfo.InvariantCulture)] = kv.Value;
                JObject resets = new JObject();
                foreach (KeyValuePair<int, string> kv in Core.Curves.ResetOnLeaveNames) resets[kv.Key.ToString(CultureInfo.InvariantCulture)] = kv.Value;
                r["modes"] = modes; r["resets"] = resets;
                return r;
            }
            throw new ApiError(o.Type.Name + " has no curve view");
        }

        /// <summary>curve edits. DofWeightCurvesAsset: which ("default" or the set index), dof, bytes (hex) or start,
        /// length, slope_in, slope_out, mask. FloatGameStateTag: storage (samples / bytes / bezier), values or xs, ys,
        /// iterations, mode, value, reset_on_leave.</summary>
        public JObject CurvesSet(object key, JObject a)
        {
            GdObj o = Root(key);
            try
            {
                Change("edit curve of " + NameOf(o), new[] { o.Key }, () =>
                {
                    GdObj cur = Db.Get(o.Key);
                    if (cur.Type.Name == "DofWeightCurvesAsset")
                    {
                        string which = ArgStr(a, "which") ?? "default";
                        byte[] cb;
                        string hex = ArgStr(a, "bytes");
                        if (!string.IsNullOrEmpty(hex)) cb = BE.FromHex(hex.Replace(" ", ""));
                        else
                        {
                            Func<string, double, double> num = (n, d) => Arg(a, n) != null ? Num.ToDouble(Arg(a, n)) : d;
                            cb = Core.Curves.EncodeCurve(num("start", 0), num("length", 0), num("slope_in", 1.0), num("slope_out", 1.0));
                        }
                        object mask = Arg(a, "mask");
                        Core.Curves.WeightCurveSet(cur, which, cb, mask == null ? (int?)null : (Num.ToBool(mask) ? 0xFF : 0),
                                                   Arg(a, "dof") != null ? (int)Num.ToLong(Arg(a, "dof")) : 0);
                    }
                    else if (cur.Type.Name == "FloatGameStateTag")
                    {
                        string st = ArgStr(a, "storage");
                        Func<string, List<double>> nums = n => { IList l = Arg(a, n) as IList; return l == null ? new List<double>() : l.Cast<object>().Select(Num.ToDouble).ToList(); };
                        if (st == "samples") Core.Curves.SetTagSamples(cur, nums("values"));
                        else if (st == "bytes") Core.Curves.SetTagSamples(cur, nums("values"), true);
                        else if (st == "bezier") Core.Curves.SetTagBezier(cur, nums("xs"), nums("ys"), Arg(a, "iterations") != null ? (int)Num.ToLong(Arg(a, "iterations")) : 4);
                        if (Arg(a, "mode") != null) cur["Mode"] = Coerce.ToField(cur.Type.GetField("Mode"), Num.ToLong(Arg(a, "mode")));
                        if (Arg(a, "value") != null) cur["Value"] = Num.ToFloat(Arg(a, "value"));
                        if (Arg(a, "reset_on_leave") != null)
                        {
                            GdObj b = cur["__base"] as GdObj;
                            if (b == null || !b.Has("ResetOnLeave")) throw new ApiError("this tag has no GameStateTagAsset base");
                            b["ResetOnLeave"] = Coerce.ToField(b.Type.GetField("ResetOnLeave"), Num.ToLong(Arg(a, "reset_on_leave")));
                        }
                    }
                    else throw new ApiError(cur.Type.Name + " has no curve view");
                });
            }
            catch (Exception e) when (e is ArgumentException || e is IndexOutOfRangeException || e is ArgumentOutOfRangeException || e is FormatException)
            {
                throw new ApiError(e.Message);
            }
            return Curves(key);
        }

        public JObject CurvesRun(object key, IList<double> t = null, double value = 0.0, double dflt = 0.0)
        {
            GdObj o = Root(key);
            if (o.Type.Name != "FloatGameStateTag") throw new ApiError("not a FloatGameStateTag");
            List<double> ts = t != null && t.Count > 0 ? t.ToList() : Enumerable.Range(0, 11).Select(x => x / 10.0).ToList();
            float? end;
            List<float?> vals = Core.Curves.TagRun(o, ts.Select(x => (float)x).ToList(), (float)value, out end, (float)dflt);
            JObject r = new JObject();
            r["t"] = ts.Cast<object>().ToList();
            r["values"] = vals.Select(x => x.HasValue ? JsonIO.F(x.Value) : null).ToList();
            r["leave"] = end.HasValue ? JsonIO.F(end.Value) : null;
            return r;
        }

        // ------------------------------------------------------------------ graphs
        public Graph GraphOf(object key, bool stateMachine, int depth = 2, bool incoming = false, int maxNodes = 600, Action<int, int> progress = null)
        {
            ulong k = K(key);
            int i = Db.BlockOf(k);
            if (i < 0) throw new ApiError("no object " + H(k));
            Graph g;
            if (stateMachine)
            {
                ulong sfc = Db.TypeName(i) == "StateFlowControllerAsset" ? k : SmController(H(k));
                g = Graph.StateMachine(Db, sfc);
            }
            else g = Graph.Build(Db, new[] { k }, depth, incoming, maxNodes, null, null, progress);
            g.AttachDetails(Db);
            g.Layout();
            return g;
        }

        // ------------------------------------------------------------------ save
        /// <summary>the edited container: withoutPrefix = true gives the body Frosty stores (from the version dword)</summary>
        public SaveResult Save(bool withoutPrefix, uint? h7 = null, uint? h8 = null, Action<string, double> progress = null)
        {
            SaveResult r = Db.Save(withoutPrefix, Say, true, true, h7, h8, progress);
            S.Dirty = false;
            return r;
        }
    }
}
