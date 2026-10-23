using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using FrostyAnimationEditor.Core;
using FrostyAnimationEditor.Core.Anim;

namespace FrostyAnimationEditor.Windows
{
    /// <summary>the References, Animation, State machine, Blend space, Expression and Curves tabs</summary>
    internal sealed partial class EditorWindow
    {
        private SkeletonView preview;

        private void StopPreview()
        {
            if (preview != null) preview.Stop();
            preview = null;
        }

        private UIElement KeyLink(JObject info, string fallback = "-")
        {
            if (info == null) return Ui.T(fallback, Ui.Muted);
            string k = info.Str("key");
            string name = string.IsNullOrEmpty(info.Str("name")) ? k : info.Str("name");
            if (info.Str("state") != "ok") return Ui.T(name, info.Str("state") == "imported" ? (Brush)Ui.Warn : Ui.Bad, 12, false, false, info.Str("type") + " " + k);
            return Ui.Link(name, () => Go(k), null, info.Str("type") + " " + k);
        }

        // ================================================================== references
        private void RenderRefs()
        {
            JObject r = M.Refs(ObjKey);
            List<object> outs = r.Arr("out");
            body.Children.Add(Ui.H3("This object references (" + outs.Count + ")"));
            body.Children.Add(RefTable(outs));
            body.Children.Add(Ui.H3("Referenced by"));
            StackPanel inBox = new StackPanel();
            Button find = null;
            find = Ui.B("Find objects that reference this one", () =>
            {
                string key = ObjKey;
                JObject r2;
                try { r2 = Busy("indexing references", p => M.Refs(key, true, (a, b) => p("indexing references " + a + " / " + b, b > 0 ? (double)a / b : 0))); }
                catch (Exception e) when (IsUserError(e)) { Report(e); return; }
                find.Visibility = Visibility.Collapsed;
                List<object> inc = r2.Arr("in");
                inBox.Children.Add(Ui.T(r2.Long("in_total") + " reference" + (r2.Long("in_total") == 1 ? "" : "s") + (r2.Long("in_total") > inc.Count ? " (first " + inc.Count + " listed)" : ""), Ui.Muted));
                inBox.Children.Add(RefTable(inc));
            });
            find.HorizontalAlignment = HorizontalAlignment.Left;
            inBox.Children.Add(find);
            body.Children.Add(inBox);
        }

        private Grid RefTable(List<object> rows)
        {
            return Ui.Table(new[] { "field", "object", "class" }, rows.Cast<JObject>().Select(x => new object[]
            {
                Ui.T(x.Str("path"), null, 11, false, true),
                (x.Str("state") ?? "ok") == "ok" ? (object)Ui.Link(string.IsNullOrEmpty(x.Str("name")) ? x.Str("key") : x.Str("name"), () => Go(x.Str("key")), null, x.Str("key"))
                                                 : Ui.T(x.Str("name"), x.Str("state") == "imported" ? (Brush)Ui.Warn : Ui.Bad, 12, false, false, x.Str("key")),
                Ui.T(x.Str("type"), Ui.Muted)
            }));
        }

        // ================================================================== animation
        private void RenderClip()
        {
            JObject c = M.Clip(ObjKey);
            List<object> ch = c.Arr("channels"), ctrls = c.Arr("controllers");
            string clipKey = c.Str("clip");
            WrapPanel ctrlLinks = new WrapPanel();
            for (int i = 0; i < ctrls.Count; i++)
            {
                JObject x = (JObject)ctrls[i];
                string ck = x.Str("key");
                if (i > 0) ctrlLinks.Children.Add(Ui.T(", "));
                ctrlLinks.Children.Add(Ui.Link(x.Str("name"), () => Go(ck)));
                ctrlLinks.Children.Add(Ui.T(" (" + Ui.Num(x["fps"]) + " fps)", Ui.Muted));
            }
            if (ctrls.Count == 0) ctrlLinks.Children.Add(Ui.T("none", Ui.Muted));
            Ui.Form f = new Ui.Form(110);
            f.Row("clip", Ui.H(Ui.Link(c.Str("name"), () => Go(clipKey)), Ui.Pad(Ui.T(clipKey, Ui.Muted, 11, false, true), 8, 0, 0, 0)));
            f.Row("codec", Ui.T((c.Str("codec") ?? "").Trim()));
            f.Row("frames", Ui.T(Ui.Num(c["frames"])));
            f.Row("channels", Ui.Wrap(ch.Count + " (" + string.Join(", ", ch.Take(8).Cast<JObject>().Select(x => x.Str("name"))) + (ch.Count > 8 ? ", …" : "") + ")"));
            f.Row("rig", Ui.T(c.Str("rig") ?? "—"));
            f.Row("controllers", ctrlLinks);
            body.Children.Add(f.Grid);

            body.Children.Add(Ui.H3("Export"));
            body.Children.Add(Ui.Flow(Ui.B("For Blender (.antanim.json)…", () => ExportClip("antanim")), Ui.B("BVH (.bvh)…", () => ExportClip("bvh")),
                                      Ui.B("Save the Blender add-on…", SaveAddon)));
            body.Children.Add(Ui.Wrap("In Blender install the add-on io_fifa17_antanim.py (Edit › Preferences › Add-ons › Install), then File › Import › FIFA 17 ANT animation " +
                                      "builds the bind-posed armature and the action. Export it back with File › Export › FIFA 17 ANT animation and use Replace or Add below.", Ui.Muted, 11));
            body.Children.Add(Ui.H3("Import"));
            body.Children.Add(Ui.Flow(Ui.B("Replace this animation from a file…", () => ClipImport(c, false)), Ui.B("Add a new animation from a file…", () => ClipImport(c, true))));

            body.Children.Add(Ui.H3("Preview"));
            Pose.World w;
            try { w = M.ClipPreview(ObjKey); }
            catch (Exception e) when (IsUserError(e)) { body.Children.Add(Ui.Wrap("preview: " + e.Message, Ui.Bad)); return; }
            SkeletonView sv = new SkeletonView(w) { Width = 640, HorizontalAlignment = HorizontalAlignment.Left };
            Slider slider = new Slider { Minimum = 0, Maximum = Math.Max(0, sv.FrameCount - 1), Width = 420, VerticalAlignment = VerticalAlignment.Center, IsSnapToTickEnabled = true, TickFrequency = 1 };
            TextBlock fl = Ui.T("", Ui.Muted, 11);
            Button play = Ui.B("Pause", null);
            bool sliding = false;
            sv.FrameShown += f2 =>
            {
                fl.Text = "frame " + w.Frames[f2] + " / " + w.FrameCount;
                sliding = true;
                slider.Value = f2;
                sliding = false;
            };
            slider.ValueChanged += (s, e) => { if (!sliding) { sv.Play(false); play.Content = "Play"; sv.Show((int)slider.Value); } };
            play.Click += (s, e) => { bool on = !sv.Playing; sv.Play(on); play.Content = on ? "Pause" : "Play"; };
            body.Children.Add(sv);
            body.Children.Add(Ui.H(play, slider, Ui.Pad(fl, 8, 0, 0, 0)));
            body.Children.Add(Ui.T("drag to orbit · wheel to zoom", Ui.Muted, 11));
            preview = sv;
            sv.Show(0);
            sv.Play(true);
        }

        private void ExportClip(string fmt)
        {
            string key = ObjKey;
            Tuple<string, byte[]> file;
            try { file = Busy("exporting", p => M.ClipExport(key, fmt)); }
            catch (Exception e) when (IsUserError(e)) { Report(e); return; }
            Microsoft.Win32.SaveFileDialog d = new Microsoft.Win32.SaveFileDialog
            {
                FileName = file.Item1,
                Filter = fmt == "bvh" ? "BVH motion (*.bvh)|*.bvh|All files|*.*" : "ANT animation for Blender (*.antanim.json)|*.antanim.json|All files|*.*",
                Title = "Export the animation"
            };
            if (d.ShowDialog(this) != true) return;
            try
            {
                File.WriteAllBytes(d.FileName, file.Item2);
                Msg("exported " + d.FileName);
            }
            catch (Exception e) when (IsUserError(e)) { Report(e); }
        }

        private void SaveAddon()
        {
            Microsoft.Win32.SaveFileDialog d = new Microsoft.Win32.SaveFileDialog
            {
                FileName = "io_fifa17_antanim.py",
                Filter = "Blender add-on (*.py)|*.py",
                Title = "Save the Blender add-on"
            };
            if (d.ShowDialog(this) != true) return;
            try
            {
                using (Stream s = typeof(EditorWindow).Assembly.GetManifestResourceStream("FrostyAnimationEditor.Resources.io_fifa17_antanim.py"))
                using (FileStream o = File.Create(d.FileName))
                    s.CopyTo(o);
                Msg("saved the Blender add-on to " + d.FileName);
            }
            catch (Exception e) when (IsUserError(e)) { Report(e); }
        }

        private void ClipImport(JObject c, bool asNew)
        {
            Dialog d = new Dialog(this, asNew ? "Add a new animation" : "Replace " + c.Str("name"), 640);
            TextBox file = Ui.Box("", double.NaN, true);
            Button browse = Ui.B("Browse…", () =>
            {
                Microsoft.Win32.OpenFileDialog od = new Microsoft.Win32.OpenFileDialog { Filter = "Animations (*.antanim.json;*.json;*.bvh)|*.antanim.json;*.json;*.bvh|All files|*.*" };
                if (od.ShowDialog(d) == true) file.Text = od.FileName;
            });
            Grid fileRow = new Grid();
            fileRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            fileRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(browse, 1);
            fileRow.Children.Add(file);
            fileRow.Children.Add(browse);
            ComboBox codec = Ui.Combo(new[] { Ui.KV("VBR", "VBR (compressed, like the shipped clips)"), Ui.KV("RAW", "RAW (uncompressed)") }, "VBR");
            TextBox tol = Ui.Box("0.00105", 120);
            TextBox name = Ui.Box("");
            TextBox cname = Ui.Box("");
            CheckBox mk = Ui.Check("", true);
            Ui.Form f = new Ui.Form(190);
            f.Row("file (.antanim.json or .bvh)", fileRow);
            f.Row("codec", codec);
            f.Row("VBR tolerance", tol);
            if (asNew)
            {
                f.Row("clip name", name);
                f.Row("make a ClipControllerAsset", mk);
                f.Row("controller name", cname);
            }
            d.Add(f.Grid);
            List<object> ctrls = c.Arr("controllers");
            if (asNew) d.Add(Ui.Wrap("The new clip copies this clip's skeleton channels and rig; the new controller copies " +
                                     (ctrls.Count > 0 ? ((JObject)ctrls[0]).Str("name") : "a controller of this clip") + ".", Ui.Muted, 11));
            d.Add(Ui.Wrap("An .antanim.json must have this clip's channels (export one first to see them); a .bvh is matched to the rig's joints by name.", Ui.Muted, 11));
            JObject result = null;
            d.Button("Cancel");
            d.Button(asNew ? "Add" : "Replace", () =>
            {
                string path = file.Text.Trim().Trim('"');
                if (path.Length == 0) throw new ApiError("choose a file");
                byte[] data = File.ReadAllBytes(path);
                string fmt = path.EndsWith(".bvh", StringComparison.OrdinalIgnoreCase) ? "bvh" : "antanim";
                double t = Ui.ParseDouble(tol.Text, "tolerance");
                string cd = Ui.Value(codec);
                string key = ObjKey;
                if (asNew)
                {
                    string nm = name.Text.Trim(), cn = cname.Text.Trim();
                    bool make = Ui.On(mk);
                    result = Busy("encoding the animation", p => M.ClipAdd(key, fmt, data, cd, t, nm, null, make, cn));
                }
                else result = Busy("encoding the animation", p => M.ClipReplace(key, fmt, data, cd, t));
                return true;
            }, true);
            if (!d.Run() || result == null) return;
            if (asNew)
            {
                string ck = result.Str("controller");
                LoadTypes();
                Go(ck ?? result.Str("key"));
                RefreshState();
                Msg("added " + result.Str("key") + (ck != null ? ", controller " + ck : "") + " · " + result.Str("report"));
            }
            else
            {
                AfterEdit();
                Msg("replaced · " + result.Str("report"));
            }
        }

        // ================================================================== state machine
        private static readonly Dictionary<long, string> NodeKinds = new Dictionary<long, string> { { 0, "state" }, { 1, "conduit" }, { 3, "gate" } };

        private void SmOp(string op, JObject args)
        {
            JObject r = null;
            if (!Guard(() => r = M.SmOp(op, args))) return;
            AfterEdit();
            Msg("state machine: " + op + (r != null && r.Str("key") != null ? " " + r.Str("key") : ""));
        }

        private static JObject Args(params object[] kv)
        {
            JObject j = new JObject();
            for (int i = 0; i + 1 < kv.Length; i += 2) j[(string)kv[i]] = kv[i + 1];
            return j;
        }

        private void RenderSm()
        {
            JObject r = M.Sm(ObjKey);
            JObject c = r.Obj("controller");
            List<object> states = r.Arr("states");
            string ck = c.Str("key");
            body.Children.Add(Ui.Flow(Ui.T("state machine ", null, 12, true), Ui.Link(c.Str("name"), () => Go(ck)),
                                      Ui.Pad(Ui.T(states.Count + " states" + (c.Bool("uses_gates") ? " · uses gate nodes" : ""), Ui.Muted, 11), 8, 0, 8, 0),
                                      Ui.B("Graph", () => ShowSmGraph(ck)), Ui.B("+ state", () => AddStateDialog(c))));
            foreach (JObject s in states.Cast<JObject>())
            {
                bool isStart = s.Str("key") == c.Str("start");
                string sk = s.Str("key");
                StackPanel box = new StackPanel();
                WrapPanel hd = Ui.Flow(Ui.Link(s.Str("name"), () => Go(sk)));
                string kind;
                hd.Children.Add(Ui.Badge(NodeKinds.TryGetValue(s.Long("node_type"), out kind) ? kind : Ui.Num(s["node_type"])));
                if (isStart) hd.Children.Add(Ui.Badge("start"));
                JObject subj = s.Obj("subject");
                if (subj != null) hd.Children.Add(Ui.H(Ui.T("runs ", Ui.Muted, 11), KeyLink(subj), Ui.Pad(Ui.T(subj.Str("type"), Ui.Muted, 11), 6, 0, 0, 0)));
                hd.Children.Add(Ui.Pad(new Border { Width = 12 }, 0, 0, 0, 0));
                hd.Children.Add(Ui.B("+ transition", () => AddTransitionDialog(s, states.Cast<JObject>().ToList()), null, true));
                if (!isStart)
                {
                    hd.Children.Add(Ui.B("set start", () => SmOp("set_start", Args("controller", ck, "key", sk)), null, true));
                    hd.Children.Add(Ui.B("×", () => { if (Dialog.Confirm(this, "Delete state", "Delete state " + s.Str("name") + " and its transitions?")) SmOp("remove_state", Args("key", sk)); }, "delete the state", true));
                }
                box.Children.Add(hd);
                List<object> trs = s.Arr("transitions");
                for (int i = 0; i < trs.Count; i++)
                {
                    JObject t = (JObject)trs[i];
                    int pos = i;
                    string tk = t.Str("key");
                    List<string> conds = t.Arr("true").Cast<JObject>().Select(x => x != null ? x.Str("name") : "?")
                        .Concat(t.Arr("false").Cast<JObject>().Select(x => "!" + (x != null ? x.Str("name") : "?"))).ToList();
                    List<object> flags = t.Arr("flags");
                    DockPanel line = new DockPanel { Margin = new Thickness(18, 2, 0, 0) };
                    StackPanel right = Ui.H(Ui.Link("edit", () => Go(tk), null, "open the transition object"),
                                            Ui.B("↑", () => SmOp("move_transition", Args("key", tk, "position", (long)(pos - 1))), "higher priority", true),
                                            Ui.B("↓", () => SmOp("move_transition", Args("key", tk, "position", (long)(pos + 1))), "lower priority", true),
                                            Ui.B("×", () => SmOp("remove_transition", Args("key", tk)), "remove the transition", true));
                    DockPanel.SetDock(right, Dock.Right);
                    line.Children.Add(right);
                    TextBlock cond = Ui.Trim(Ui.T((conds.Count > 0 ? string.Join(" & ", conds) : "always") + (flags.Count > 0 ? "  [" + string.Join(", ", flags) + "]" : ""), Ui.Muted, 12, false, false, t.Str("name")));
                    cond.Margin = new Thickness(8, 0, 8, 0);
                    line.Children.Add(Ui.H(Ui.T((i + 1) + ".", Ui.Muted), Ui.T(" → "), KeyLink(t.Obj("to")), cond));
                    box.Children.Add(line);
                }
                body.Children.Add(Ui.Card(box, sk == ObjKey ? Ui.Accent : null));
            }
            body.Children.Add(Ui.Wrap("Transitions are evaluated in order. The runtime tables (FlattenedTransitions, AllTransitionConditions, AllBranchTypes, Identifier, " +
                                      "UsesGateNodes) are rebuilt after every change.", Ui.Muted, 11));
        }

        private void ShowSmGraph(string controllerKey)
        {
            try
            {
                Graph g = Busy("building the graph", p => M.GraphOf(controllerKey, true));
                new GraphWindow(this, g).Show();
            }
            catch (Exception e) when (IsUserError(e)) { Report(e); }
        }

        private void AddStateDialog(JObject c)
        {
            Dialog d = new Dialog(this, "New state in " + c.Str("name"), 680);
            TextBox name = Ui.Box("");
            ComboBox kind = Ui.Combo(new[] { Ui.KV("0", "state"), Ui.KV("1", "conduit"), Ui.KV("3", "gate") }, "0");
            JObject subj = null;
            TextBlock sv = Ui.T("subject: none", Ui.Muted, 11);
            ObjectPicker pk = new ObjectPicker(M, null);
            pk.Picked += o => { subj = o; sv.Text = "subject: " + o.Str("name") + " (" + o.Str("type") + ")"; sv.Foreground = Ui.Fg; };
            Ui.Form f = new Ui.Form(80);
            f.Row("name", name);
            f.Row("kind", kind);
            d.Add(f.Grid, Ui.Wrap("Subject controller (what plays in this state):", Ui.Muted, 11), pk.Panel, sv);
            d.Button("Cancel");
            JObject args = null;
            d.Button("Add", () =>
            {
                if (name.Text.Trim().Length == 0) throw new ApiError("name the state");
                args = Args("controller", c.Str("key"), "name", name.Text.Trim(), "subject", subj != null ? subj.Str("key") : null, "node_type", long.Parse(Ui.Value(kind), CultureInfo.InvariantCulture));
                return true;
            }, true);
            if (d.Run() && args != null) SmOp("add_state", args);
        }

        private void AddTransitionDialog(JObject s, List<JObject> states)
        {
            Dialog d = new Dialog(this, "New transition from " + s.Str("name"), 680);
            ComboBox to = Ui.Combo(states.Select(x => Ui.KV(x.Str("key"), x.Str("name"))), null);
            CheckBox atEnd = Ui.Check("branch at end", false), first = Ui.Check("after first update", false), any = Ui.Check("branch in any", false);
            CheckBox neg = Ui.Check("the next picked condition must be false", false);
            List<Tuple<JObject, bool>> conds = new List<Tuple<JObject, bool>>();
            TextBlock cv = Ui.Wrap("no conditions (always)", Ui.Muted, 11);
            Action show = () =>
            {
                cv.Text = conds.Count > 0 ? string.Join(" & ", conds.Select(x => (x.Item2 ? "!" : "") + x.Item1.Str("name"))) : "no conditions (always)";
                cv.Foreground = conds.Count > 0 ? Ui.Fg : Ui.Muted;
            };
            ObjectPicker pk = new ObjectPicker(M, "BoolAsset");
            pk.Picked += o => { conds.Add(Tuple.Create(o, Ui.On(neg))); show(); };
            Ui.Form f = new Ui.Form(80);
            f.Row("to state", to);
            d.Add(f.Grid, Ui.Flow(atEnd, first, any),
                  Ui.Wrap("Conditions (BoolAsset / SignalAsset / EnumerationEnumeratorPair): pick to add.", Ui.Muted, 11), neg, pk.Panel, cv);
            d.Button("Cancel");
            d.Button("Clear conditions", () => { conds.Clear(); show(); return false; });
            JObject args = null;
            d.Button("Add", () =>
            {
                args = Args("from", s.Str("key"), "to", Ui.Value(to), "at_end", Ui.On(atEnd), "after_first_update", Ui.On(first), "in_any", Ui.On(any),
                            "true", conds.Where(x => !x.Item2).Select(x => (object)x.Item1.Str("key")).ToList(),
                            "false", conds.Where(x => x.Item2).Select(x => (object)x.Item1.Str("key")).ToList());
                return true;
            }, true);
            if (d.Run() && args != null) SmOp("add_transition", args);
        }

        // ================================================================== blend space
        private void RenderBlend()
        {
            JObject r = M.Blend(ObjKey);
            List<object> items = r.Arr("items");
            BlendView bv = new BlendView(r);
            bv.ItemClicked += i =>
            {
                JObject a = ((JObject)items[i]).Obj("asset");
                if (a != null && a.Str("state") == "ok") Go(a.Str("key"));
            };
            body.Children.Add(bv);
            body.Children.Add(Ui.Table(new[] { "#", "position", "plays", "" }, items.Select((o, i) =>
            {
                JObject it = (JObject)o;
                return new object[]
                {
                    Ui.T(i.ToString(CultureInfo.InvariantCulture)),
                    Ui.T(Num.ToDouble(it["x"]).ToString("0.0000", CultureInfo.InvariantCulture) + ", " + Num.ToDouble(it["y"]).ToString("0.0000", CultureInfo.InvariantCulture), null, 12, false, true),
                    KeyLink(it.Obj("asset")),
                    Ui.T((it.Bool("xaxis") ? "XAxis " : "") + (it.Bool("snap") ? "Snap" : ""), Ui.Muted, 11)
                };
            })));
            List<object> grid = r.Arr("grid");
            body.Children.Add(Ui.Wrap(r.Arr("triangles").Count + " triangles, " + r.Arr("edges").Count + " hull edges, grid " + Ui.Num(grid[0]) + "×" + Ui.Num(grid[1]) + ". " +
                "Edit item positions or add items in Fields (VoronoiDiagram › Items); the triangulation, hull, centroid and grid are rebuilt on save " +
                (r.Bool("changed") ? "(the positions differ from the file: this diagram will be rebuilt)." : "when positions change."), Ui.Muted, 11));
            body.Children.Add(Ui.Flow(Ui.B("Rebuild diagram now", () => { if (Guard(() => M.BlendRebuild(ObjKey))) { AfterEdit(); Msg("rebuilt the diagram"); } }),
                                      Ui.B("Rebuild with a new grid", () => { if (Guard(() => M.BlendRebuild(ObjKey, true))) { AfterEdit(); Msg("rebuilt the diagram and its grid"); } })));
        }

        // ================================================================== expression
        private void RenderExpr(bool dbl)
        {
            JObject r = M.Expr(ObjKey, dbl);
            JObject nat = r.Obj("natives");
            string used = string.Join(", ", nat.OrderByDescending(kv => Num.ToLong(kv.Value)).Select(kv => kv.Key + " ×" + Ui.Num(kv.Value)));
            body.Children.Add(Ui.Wrap(Ui.Num(r["instructions"]) + " instructions, " + Ui.Num(r["code_bytes"]) + " code bytes. Natives: " + (used.Length > 0 ? used : "none") + "." +
                (r.Bool("dbl_same") ? " The ExpressionKernelDBL copy (not loaded by the game) is identical." : " The ExpressionKernelDBL copy (not loaded by the game) differs.") +
                (dbl ? "  Showing the DBL copy." : ""), Ui.Muted, 11));
            TextBox ta = Ui.Area(r.Str("text"), 30);
            body.Children.Add(ta);
            StackPanel outp = new StackPanel();
            ComboBox mode = Ui.Combo(new[] { Ui.KV("both", "write ExpressionKernel and its DBL copy"), Ui.KV("main", "write ExpressionKernel only"), Ui.KV("dbl", "write the DBL copy only") }, dbl ? "dbl" : "both", 300);
            CheckBox force = Ui.Check("write even if the game would break", false);
            Action<JObject> show = res =>
            {
                outp.Children.Clear();
                if (res.Str("problem") != null) outp.Children.Add(Ui.Wrap(res.Str("problem"), Ui.Bad, 12, true));
                foreach (object n in res.Arr("notes") ?? new List<object>()) outp.Children.Add(Ui.Wrap("note: " + n, Ui.Muted, 12, true));
                foreach (JObject i in (res.Arr("issues") ?? new List<object>()).Cast<JObject>())
                    outp.Children.Add(Ui.Wrap(i.Str("level") + ": " + i.Str("message"), i.Str("level") == "error" ? (Brush)Ui.Bad : Ui.Fg, 12, true));
                if (res.Arr("changed") != null) outp.Children.Add(Ui.Wrap("changed: " + (res.Arr("changed").Count > 0 ? string.Join(", ", res.Arr("changed")) : "nothing"), Ui.Good));
                if (res.ContainsKey("ok") && res.Bool("ok") && (res.Arr("issues") ?? new List<object>()).Count == 0)
                    outp.Children.Add(Ui.Wrap("assembles: " + Ui.Num(res["instructions"]) + " instructions, " + Ui.Num(res["bytes"]) + " bytes, no problems found", Ui.Good));
            };
            body.Children.Add(Ui.Flow(
                Ui.B("Check", () => show(M.ExprCheck(ta.Text))),
                Ui.B("Apply to object", () =>
                {
                    JObject res = null;
                    if (!Guard(() => res = M.ExprApply(ObjKey, ta.Text, Ui.Value(mode), Ui.On(force)))) return;
                    show(res);
                    RefreshState();
                    Msg("expression: " + (res.Arr("changed").Count > 0 ? "wrote " + string.Join(", ", res.Arr("changed")) : "no change"));
                }),
                mode, force,
                Ui.B("Reload", () => { body.Children.Clear(); Guard(() => RenderExpr(dbl)); }),
                Ui.B(dbl ? "Show ExpressionKernel" : "Show DBL copy", () => { body.Children.Clear(); Guard(() => RenderExpr(!dbl)); })));
            body.Children.Add(outp);
            show(Args("issues", r.Arr("issues")));

            // simulator
            body.Children.Add(Ui.H3("Try it (the editor's model of the game's expression VM)"));
            body.Children.Add(Ui.Wrap("Runs the text above. Game-state values: a number, true/false or x,y,z. DOFs not given read identity (.q), 1,1,1 (.s) or zeros; values as x,y,z,w.", Ui.Muted, 11));
            List<Tuple<string, string, TextBox>> ins = new List<Tuple<string, string, TextBox>>();
            List<object[]> rows = new List<object[]>();
            foreach (JObject g in r.Arr("gs").Cast<JObject>())
            {
                string kind = g.Str("kind");
                TextBox t = Ui.Box("", 180, true, kind == "vec3" ? "x,y,z" : (kind == "bool" ? "true / false" : "0"));
                ins.Add(Tuple.Create(g.Str("name"), kind, t));
                rows.Add(new object[] { Ui.T(g.Str("name"), null, 12, false, true), Ui.T(kind + " " + g.Str("asset")), t });
            }
            List<Tuple<string, TextBox>> dofs = new List<Tuple<string, TextBox>>();
            foreach (object dn in r.Arr("dofs"))
            {
                TextBox t = Ui.Box("", 180, true, "x,y,z,w");
                dofs.Add(Tuple.Create((string)dn, t));
                rows.Add(new object[] { Ui.T("DOF", Ui.Muted), Ui.T((string)dn), t });
            }
            if (rows.Count > 0) body.Children.Add(Ui.Table(new[] { "input", "bound to", "value" }, rows));
            TextBox frames = Ui.Box("3", 60), ticks = Ui.Box("1", 60);
            StackPanel res2 = new StackPanel();
            body.Children.Add(Ui.Flow(Ui.T("frames"), frames, Ui.T("delta ticks"), ticks, Ui.B("Run", () =>
            {
                Guard(() =>
                {
                    JObject gs = new JObject();
                    foreach (Tuple<string, string, TextBox> i in ins) { object v = ParseSim(i.Item3.Text, i.Item2); if (v != null) gs[i.Item1] = v; }
                    JObject dof = new JObject();
                    foreach (Tuple<string, TextBox> i in dofs)
                    {
                        object v = ParseSim(i.Item2.Text, null);
                        if (v == null) continue;
                        dof[i.Item1] = v is List<object> ? v : new List<object> { v };
                    }
                    JObject run = M.ExprRun(ObjKey, ta.Text, gs, dof, Ui.ParseInt(frames.Text, "frames"), Ui.ParseInt(ticks.Text, "delta ticks"));
                    res2.Children.Clear();
                    List<object> fr = run.Arr("frames");
                    for (int n = 0; n < fr.Count; n++)
                    {
                        JObject f = (JObject)fr[n];
                        if (f.Str("error") != null) { res2.Children.Add(Ui.Wrap("frame " + n + ": " + f.Str("error"), Ui.Bad, 12, true)); break; }
                        res2.Children.Add(Ui.T("frame " + n + ": " + Ui.Num(f["instructions"]) + " instructions", Ui.Muted, 12, false, true));
                        List<object> ws = f.Arr("writes");
                        foreach (JObject wr in ws.Cast<JObject>())
                            res2.Children.Add(Ui.T("  " + wr.Str("kind") + " " + Ui.Num(wr["target"]) + " = " + SimValue(wr["value"]), null, 12, false, true));
                        if (ws.Count == 0) res2.Children.Add(Ui.T("  (no writes)", Ui.Muted, 12, false, true));
                    }
                });
            })));
            body.Children.Add(res2);
        }

        /// <summary>a simulator input as the web editor read it: bool for bool inputs, otherwise a number or a list of numbers
        /// (whole numbers are integers, as JSON gives them)</summary>
        private static object ParseSim(string text, string kind)
        {
            string s = (text ?? "").Trim();
            if (s.Length == 0) return null;
            if (kind == "bool") return s.ToLowerInvariant() == "true" || s == "1";
            List<object> v = new List<object>();
            foreach (string part in s.Split(','))
            {
                double d;
                if (!double.TryParse(part.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out d)) throw new FormatException("not a number: " + part.Trim());
                v.Add(d == Math.Floor(d) && Math.Abs(d) < 9007199254740992.0 ? (object)(long)d : d);
            }
            return v.Count == 1 ? v[0] : v;
        }

        private static string SimValue(object v)
        {
            IList l = v as IList;
            if (l != null) return "(" + string.Join(", ", l.Cast<object>().Select(SimNum)) + ")";
            return SimNum(v);
        }

        private static string SimNum(object v)
        {
            if (v is double || v is float)
            {
                double d = Convert.ToDouble(v, CultureInfo.InvariantCulture);
                if (double.IsNaN(d)) return "nan";
                if (double.IsInfinity(d)) return d > 0 ? "inf" : "-inf";
                return d.ToString("G6", CultureInfo.InvariantCulture);
            }
            return Ui.Num(v);
        }

        // ================================================================== curves
        private void RenderCurves()
        {
            JObject r = M.Curves(ObjKey);
            Action<JObject> apply = payload =>
            {
                if (Guard(() => M.CurvesSet(ObjKey, payload))) { AfterEdit(); Msg("curve updated"); }
            };
            if (r.Str("kind") == "weights") RenderWeightCurves(r, apply);
            else RenderTagCurve(r, apply);
            List<object> issues = r.Arr("issues");
            if (issues != null && issues.Count > 0)
            {
                body.Children.Add(Ui.H3("Issues"));
                foreach (object m in issues) body.Children.Add(Ui.Wrap(Convert.ToString(m, CultureInfo.InvariantCulture), Ui.Bad, 11));
            }
        }

        private void RenderWeightCurves(JObject r, Action<JObject> apply)
        {
            List<double> ts = r.Arr("t").Select(Num.ToDouble).ToList();
            CurvePlot plot = new CurvePlot(0, 1);
            Dictionary<JObject, CurvePlot.Series> byCurve = new Dictionary<JObject, CurvePlot.Series>();
            foreach (JObject st in r.Arr("sets").Cast<JObject>())
                foreach (JObject c in st.Arr("curves").Cast<JObject>())
                {
                    CurvePlot.Series s = new CurvePlot.Series { X = ts, Y = c.Arr("w").Select(x => x == null || x is string ? (double?)null : Num.ToDouble(x)).ToList(),
                                                                Label = st.Str("name") + (st.Bool("each") ? " DOF " + Ui.Num(c["dof"]) : "") };
                    plot.All.Add(s);
                    byCurve[c] = s;
                }
            body.Children.Add(Ui.Wrap("Weight of each DOF set (y) over the blend's normalised time (x), computed the way the game does; click a row to highlight its curve.", Ui.Muted, 11));
            body.Children.Add(plot);
            List<object[]> rows = new List<object[]>();
            List<Border> rowMarks = new List<Border>();
            foreach (JObject st in r.Arr("sets").Cast<JObject>())
                foreach (JObject c in st.Arr("curves").Cast<JObject>())
                {
                    TextBox iS = Ui.Box(Num.ToDouble(c["start"]).ToString("0.0000", CultureInfo.InvariantCulture), 70);
                    TextBox iL = Ui.Box(Num.ToDouble(c["length"]).ToString("0.0000", CultureInfo.InvariantCulture), 70);
                    TextBox iA = Ui.Box(Num.ToDouble(c["slope_in"]).ToString("0.000", CultureInfo.InvariantCulture), 70);
                    TextBox iB = Ui.Box(Num.ToDouble(c["slope_out"]).ToString("0.000", CultureInfo.InvariantCulture), 70);
                    CheckBox iM = Ui.Check("", Num.ToLong(c["mask"]) != 0);
                    JObject cc = c;
                    JObject set = st;
                    Border mark = new Border { Child = Ui.Link(set.Str("name") + (set.Bool("each") ? " · DOF " + Ui.Num(cc["dof"]) : ""), () =>
                    {
                        foreach (CurvePlot.Series s in plot.All) s.On = false;
                        byCurve[cc].On = true;
                        plot.InvalidateVisual();
                    }, null, "highlight this curve") };
                    rowMarks.Add(mark);
                    rows.Add(new object[]
                    {
                        mark, Ui.T(c.Str("bytes"), null, 12, false, true), iS, iL, iA, iB, iM,
                        Ui.B("Apply", () =>
                        {
                            try
                            {
                                apply(Args("which", set["which"], "dof", cc["dof"], "start", Ui.ParseDouble(iS.Text, "start"), "length", Ui.ParseDouble(iL.Text, "length"),
                                           "slope_in", Ui.ParseDouble(iA.Text, "slope in"), "slope_out", Ui.ParseDouble(iB.Text, "slope out"), "mask", Ui.On(iM)));
                            }
                            catch (FormatException e) { Msg(e.Message, true); }
                        }, null, true)
                    });
                }
            body.Children.Add(Ui.H3("Curves"));
            body.Children.Add(Ui.Table(new[] { "set", "bytes", "start", "length", "slope in", "slope out", "inherited", "" }, rows));
            body.Children.Add(Ui.Wrap("start and length are fractions of the blend (stored as bytes, steps of 1/255); length 0 makes a step at start. " +
                "Slopes are the curve's gradient at the start and the end (stored in steps of 6/255 between -1.5 and 4.5). An inherited DOF ignores the slopes and blends linearly.", Ui.Muted, 11));
        }

        private void RenderTagCurve(JObject r, Action<JObject> apply)
        {
            List<double> ts = r.Arr("t").Select(Num.ToDouble).ToList();
            string storage = r.Str("storage");
            List<object> data = r.Arr("data") ?? new List<object>();
            body.Children.Add(Ui.Wrap(storage + " curve (SplineStorageFlags 0x" + Num.ToLong(r["flags"]).ToString("x", CultureInfo.InvariantCulture) +
                (r["iterations"] != null ? ", " + Ui.Num(r["iterations"]) + " iterations" : "") + "). x is the time t the tag is updated with (the curve spans t = 0…1); " +
                "y is the curve before Mode and Value are applied.", Ui.Muted, 11));
            CurvePlot plot = new CurvePlot(0, 1);
            plot.All.Add(new CurvePlot.Series { X = ts, Y = r.Arr("curve").Select(x => x == null || x is string ? (double?)null : Num.ToDouble(x)).ToList(), On = true });
            body.Children.Add(plot);
            ComboBox stor = Ui.Combo(new[] { Ui.KV("samples", "samples (Spline, floats)"), Ui.KV("bytes", "samples (SplineRaw, bytes 0..1)"), Ui.KV("bezier", "cubic Bezier (Spline x0..x3, y0..y3)") },
                                     storage == "none" ? "samples" : storage, 320);
            Func<IEnumerable<object>, string> list = xs => string.Join(", ", xs.Select(Ui.Num));
            TextBox vals = Ui.Box(storage == "bytes" ? string.Join(", ", data.Select(b => Math.Round(Num.ToDouble(b) / 255.0, 4).ToString(CultureInfo.InvariantCulture))) : (storage == "bezier" ? "" : list(data)), double.NaN, true);
            TextBox xs0 = Ui.Box(storage == "bezier" ? list(data.Take(4)) : "0, 0.333, 0.667, 1", double.NaN, true);
            TextBox ys0 = Ui.Box(storage == "bezier" ? list(data.Skip(4).Take(4)) : "0, 0, 1, 1", double.NaN, true);
            TextBox it = Ui.Box(r["iterations"] != null ? Ui.Num(r["iterations"]) : "4", 70);
            JObject modes = r.Obj("modes"), resets = r.Obj("resets");
            ComboBox mode = Ui.Combo(modes.Select(kv => Ui.KV(kv.Key, kv.Key + " — " + kv.Value)), Ui.Num(r["mode"]), 320);
            TextBox value = Ui.Box(Ui.Num(r["value"]), 120);
            string rol = Ui.Num(r["reset_on_leave"]);
            ComboBox reset = Ui.Combo(resets.Select(kv => Ui.KV(kv.Key, kv.Key + " — " + kv.Value)), resets.ContainsKey(rol) ? rol : "0", 320);
            Ui.Form f = new Ui.Form(130);
            f.Row("storage", stor);
            UIElement lS = f.Row("values", vals);
            UIElement lX = f.Row("x control", xs0);
            UIElement lY = f.Row("y control", ys0);
            UIElement lI = f.Row("iterations", it);
            f.Row("Mode", mode);
            f.Row("Value", value);
            f.Row("ResetOnLeave", reset);
            JObject va = r.Obj("value_asset");
            f.Row("game state", va != null ? KeyLink(va) : (UIElement)Ui.T("-", Ui.Muted));
            Action sync = () =>
            {
                bool bz = Ui.Value(stor) == "bezier";
                Visibility vs = bz ? Visibility.Collapsed : Visibility.Visible, vb = bz ? Visibility.Visible : Visibility.Collapsed;
                lS.Visibility = vals.Visibility = vs;
                lX.Visibility = xs0.Visibility = lY.Visibility = ys0.Visibility = lI.Visibility = it.Visibility = vb;
            };
            stor.SelectionChanged += (s, e) => sync();
            sync();
            body.Children.Add(Ui.H3("Edit"));
            body.Children.Add(f.Grid);
            body.Children.Add(Ui.Flow(Ui.B("Apply", () =>
            {
                try
                {
                    apply(Args("storage", Ui.Value(stor), "values", Ui.Numbers(vals.Text).Cast<object>().ToList(), "xs", Ui.Numbers(xs0.Text).Cast<object>().ToList(),
                               "ys", Ui.Numbers(ys0.Text).Cast<object>().ToList(), "iterations", (long)Ui.ParseInt(it.Text, "iterations"),
                               "mode", long.Parse(Ui.Value(mode), CultureInfo.InvariantCulture), "value", Ui.ParseDouble(value.Text, "Value"),
                               "reset_on_leave", long.Parse(Ui.Value(reset), CultureInfo.InvariantCulture)));
                }
                catch (FormatException e) { Msg(e.Message, true); }
            })));
            // simulation
            TextBox v0 = Ui.Box("0", 100);
            TextBlock outp = Ui.Wrap("", null, 12, true);
            body.Children.Add(Ui.H3("Simulate one stay in the state"));
            body.Children.Add(Ui.Wrap("Enter with the game-state value below, update at t = 0, 0.1 … 1, then leave (default value 0 for ResetOnLeave 1).", Ui.Muted, 11));
            body.Children.Add(Ui.Flow(Ui.T("value at entry"), v0, Ui.B("Run", () =>
            {
                Guard(() =>
                {
                    JObject s = M.CurvesRun(ObjKey, null, Ui.ParseDouble(v0.Text, "value"));
                    List<object> tt = s.Arr("t"), vv = s.Arr("values");
                    outp.Text = string.Join("   ", tt.Select((x, i) => "t=" + Ui.Num(x) + ": " + (vv[i] == null ? "?" : SimNum(vv[i])))) +
                                "   leave: " + (s["leave"] == null ? "?" : SimNum(s["leave"]));
                });
            })));
            body.Children.Add(outp);
        }
    }
}
