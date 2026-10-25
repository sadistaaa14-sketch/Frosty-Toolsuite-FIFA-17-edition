using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using FrostyAnimationEditor.Core;

namespace FrostyAnimationEditor.Windows
{
    internal sealed partial class EditorWindow
    {
        // ================================================================== state machine runs
        private string simFor;
        private readonly Dictionary<string, bool> simCond = new Dictionary<string, bool>();
        private readonly Dictionary<string, string> simPlays = new Dictionary<string, string>();
        private readonly Dictionary<string, bool> simGates = new Dictionary<string, bool>();
        private double simDuration = 600, simStep = 2;
        private string simStart;
        private JObject simResult;

        private void RenderSmRun(string ck, List<JObject> states)
        {
            if (simFor != ck) { simFor = ck; simCond.Clear(); simPlays.Clear(); simGates.Clear(); simStart = null; simResult = null; }
            body.Children.Add(Ui.H3("Run"));
            body.Children.Add(Ui.Wrap("Plays the state machine step by step, following the game's transition rules: in each update the transition that can fire " +
                                      "earliest wins (the earlier one in the list on a tie); it needs its true conditions on and its false ones off, and its " +
                                      "timing: any time, at the end of the state's controller (minus BranchAtEndOffset), after the first update, or inside a " +
                                      "branch window (a BranchOutPointTag of that branch type on the controller's timeline). The conditions stay as set below.",
                                      Ui.Muted, 11));
            List<JObject> conds;
            try { conds = M.SmConditions(ck).Cast<JObject>().ToList(); }
            catch (Exception e) when (IsUserError(e)) { body.Children.Add(Ui.Wrap(e.Message, Ui.Bad)); return; }
            WrapPanel cp = new WrapPanel();
            foreach (JObject c in conds)
            {
                string k = c.Str("key");
                bool on;
                if (!simCond.TryGetValue(k, out on)) on = c.Bool("default");
                CheckBox cb = Ui.Check(string.IsNullOrEmpty(c.Str("name")) ? k : c.Str("name"), on, c.Str("type") + " " + k + (c.Bool("default") ? " · default on" : ""));
                cb.Margin = new Thickness(0, 0, 14, 2);
                cb.Click += (o, e) => simCond[k] = Ui.On(cb);
                cp.Children.Add(cb);
            }
            if (conds.Count == 0) cp.Children.Add(Ui.T("no conditions", Ui.Muted, 11));
            body.Children.Add(Ui.Pad(cp, 0, 4, 0, 4));
            List<JObject> gates = M.SmGates(ck).Cast<JObject>().ToList();
            if (gates.Count > 0)
            {
                WrapPanel gp = new WrapPanel();
                foreach (JObject g in gates)
                {
                    string gk = g.Str("key");
                    bool open;
                    if (!simGates.TryGetValue(gk, out open)) open = true;
                    CheckBox cb = Ui.Check(g.Str("name"), open, "a gate node: in the game its controller decides whether the transitions through it are allowed");
                    cb.Margin = new Thickness(0, 0, 14, 2);
                    cb.Click += (o, e) => simGates[gk] = Ui.On(cb);
                    gp.Children.Add(cb);
                }
                int closed = gates.Count(g => simGates.ContainsKey(g.Str("key")) && !simGates[g.Str("key")]);
                StackPanel gbox = new StackPanel();
                gbox.Children.Add(Ui.Flow(Ui.B("All open", () => { simGates.Clear(); SetTab("sm", true); }, null, true),
                                          Ui.B("All closed", () => { foreach (JObject g in gates) simGates[g.Str("key")] = false; SetTab("sm", true); }, null, true)));
                gbox.Children.Add(gp);
                Expander ex = new Expander
                {
                    Header = Ui.T("gate nodes: " + (gates.Count - closed) + " of " + gates.Count + " open", Ui.Fg, 12),
                    Content = gbox, IsExpanded = gates.Count <= 12, Margin = new Thickness(0, 0, 0, 4), Foreground = Ui.Fg,
                };
                body.Children.Add(ex);
            }
            ComboBox start = Ui.Combo(states.Select(s => Ui.KV(s.Str("key"), s.Str("name"))), simStart ?? (states.Count > 0 ? states[0].Str("key") : ""), 280);
            TextBox dur = Ui.Box(Ui.Num(simDuration), 70, true, "ticks (60 a second)");
            TextBox step = Ui.Box(Ui.Num(simStep), 50, true, "ticks per update");
            StackPanel results = new StackPanel();
            Action run = () =>
            {
                double d, st;
                if (!double.TryParse(dur.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out d) ||
                    !double.TryParse(step.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out st)) { Msg("duration and step are numbers of ticks", true); return; }
                simDuration = d; simStep = st; simStart = Ui.Value(start);
                JObject a = new JObject { ["duration"] = d, ["step"] = st, ["start"] = simStart };
                JObject cj = new JObject();
                foreach (JObject c in conds) { bool on; if (!simCond.TryGetValue(c.Str("key"), out on)) on = c.Bool("default"); cj[c.Str("key")] = on; }
                a["conditions"] = cj;
                JObject pj = new JObject();
                foreach (KeyValuePair<string, string> kv in simPlays) pj[kv.Key] = kv.Value;
                a["plays"] = pj;
                JObject gj = new JObject();
                foreach (KeyValuePair<string, bool> kv in simGates) gj[kv.Key] = kv.Value;
                a["gates"] = gj;
                JObject r = null;
                if (!Guard(() => r = M.SmSim(ck, a))) return;
                simResult = r;
                ShowSimResult(results, r, ck, states);
            };
            body.Children.Add(Ui.Flow(Ui.T("from ", Ui.Muted, 11), start, Ui.Pad(Ui.T("for ", Ui.Muted, 11), 10, 0, 0, 0), dur, Ui.T(" ticks, updates of ", Ui.Muted, 11), step,
                                      Ui.T(" ticks", Ui.Muted, 11), Ui.Pad(Ui.B("Run", run, null, false), 10, 0, 0, 0),
                                      Ui.B("Defaults", () => { simCond.Clear(); SetTab("sm", true); }, "conditions back to their defaults"),
                                      Ui.B("All off", () => { foreach (JObject c in conds) simCond[c.Str("key")] = false; SetTab("sm", true); })));
            body.Children.Add(results);
            if (simResult != null) ShowSimResult(results, simResult, ck, states);
        }

        private void ShowSimResult(StackPanel p, JObject r, string ck, List<JObject> states)
        {
            p.Children.Clear();
            List<JObject> log = r.Arr("log").Cast<JObject>().ToList();
            List<JObject> segs = r.Arr("segments").Cast<JObject>().ToList();
            SimView sv = new SimView(segs, log, r.Dbl("duration"));
            ScrollViewer sc = new ScrollViewer { HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, Content = sv, Margin = new Thickness(0, 6, 0, 4) };
            sc.Loaded += (o, e) => sv.FitTo(Math.Max(400, sc.ActualWidth));
            p.Children.Add(sc);
            p.Children.Add(Ui.T(log.Count == 0 ? "no transition fired" : log.Count + " transition" + (log.Count == 1 ? "" : "s"), Ui.Muted, 11));
            if (log.Count > 0)
            {
                List<object[]> rows = new List<object[]>();
                foreach (JObject l in log.Take(300))
                {
                    string tk = l.Str("transition"), fk = l.Str("from"), tok = l.Str("to"), bk = l.Str("blend");
                    rows.Add(new object[]
                    {
                        Ui.T(Ui.Num(Math.Round(l.Dbl("time"), 2)) + "  (" + Ui.Num(Math.Round(l.Dbl("time") / 60.0, 2)) + " s)", null, 12, false, true),
                        Ui.H(Ui.Link(l.Str("from_name"), () => Go(fk)), Ui.T(" → "), Ui.Link(l.Str("to_name"), () => Go(tok))),
                        Ui.Link(string.IsNullOrEmpty(l.Str("transition_name")) ? tk : l.Str("transition_name"), () => Go(tk)),
                        Ui.Wrap(l.Str("rule") + (l.Str("via_name") != null ? " (via " + l.Str("via_name") + ")" : "") + " · state time " + Ui.Num(Math.Round(l.Dbl("subject_time"), 2)), Ui.Muted, 11),
                        bk != null ? (object)Ui.Link(l.Str("blend_name") ?? bk, () => Go(bk)) : "",
                    });
                }
                p.Children.Add(Ui.Table(new[] { "time (ticks)", "from → to", "transition", "why", "OverrideBlend" }, rows));
            }
            // states the run passed through, with what their controller plays
            List<object[]> srows = new List<object[]>();
            HashSet<string> seen = new HashSet<string>();
            foreach (JObject s in segs)
            {
                string nk = s.Str("node");
                if (!seen.Add(nk)) continue;
                string sk = s.Str("subject");
                bool known = s.Dbl("subject_length") > 0;
                string playing;
                simPlays.TryGetValue(nk, out playing);
                FrameworkElement pick = Ui.B(playing != null ? "plays…" : "choose what it plays…", () =>
                {
                    Dialog d = new Dialog(this, "What " + s.Str("name") + " plays", 700);
                    ObjectPicker pk = new ObjectPicker(M, "SequenceContainerAsset");
                    string chosen = null;
                    pk.Picked += o => { chosen = o.Str("key"); d.Accepted = true; d.Close(); };
                    d.Add(Ui.Wrap("The run uses this controller's length and branch windows for the state (its own controller picks what it plays at run time).", Ui.Muted, 11));
                    List<object> cands = new List<object>();
                    try { cands = M.PlayCandidates(nk); } catch (Exception ex) when (IsUserError(ex)) { }
                    if (cands.Count > 0)
                    {
                        WrapPanel cw = new WrapPanel { Margin = new Thickness(0, 4, 0, 6) };
                        foreach (JObject cand in cands.Cast<JObject>())
                        {
                            string ck2 = cand.Str("key");
                            cw.Children.Add(Ui.Pad(Ui.Link(cand.Str("name"), () => { chosen = ck2; d.Accepted = true; d.Close(); }, null, cand.Str("type") + " (" + cand.Str("via") + ")"), 0, 0, 12, 2));
                        }
                        d.Add(Ui.T("found under the state's controller:", Ui.Muted, 11), cw, Ui.T("or any other:", Ui.Muted, 11));
                    }
                    d.Add(pk.Panel);
                    d.Button("Clear", () => { simPlays.Remove(nk); return true; });
                    d.Button("Cancel");
                    d.ShowDialog();
                    if (chosen != null) simPlays[nk] = chosen;
                    SetTab("sm", true);
                }, "for the run: the controller whose length and branch windows this state uses", true);
                srows.Add(new object[]
                {
                    Ui.Link(s.Str("name"), () => Go(nk)),
                    sk != null ? (object)Ui.Link(s.Str("subject_name"), () => Go(sk)) : "—",
                    known ? Ui.Num(s.Dbl("subject_length")) + " ticks" : (object)Ui.T("not known", Ui.Warn, 11),
                    (playing != null ? (object)Ui.H(Ui.Link(M.Info(EditorModel.K(playing)) != null ? Ui.Label(M.Info(EditorModel.K(playing))) : playing, () => Go(playing)), Ui.Pad(pick, 6, 0, 0, 0)) : pick),
                });
            }
            p.Children.Add(Ui.Pad(Ui.T("states of the run", Ui.Muted, 11), 0, 6, 0, 0));
            p.Children.Add(Ui.Table(new[] { "state", "controller", "length", "for the run" }, srows));
            foreach (object n in r.Arr("notes")) p.Children.Add(Ui.Wrap(n.ToString(), Ui.Warn, 11));
            p.Children.Add(Ui.Wrap("Not modelled: branch tuning (the untuned windows are used), the gates' own check (they are open or closed as set above), nested state machines, branch-in " +
                                   "windows and destination phase (a state starts at 0), StateOps, and conditions changing by themselves.", Ui.Muted, 11));
        }
    }

    internal sealed partial class EditorWindow
    {
        // ================================================================== controller tree
        private void RenderTree()
        {
            body.Children.Add(Ui.Wrap("The controllers this one plays, as a tree (references to controller objects, also through one object in between such as a " +
                                      "chooser entry or a transition). Expand ▸ to go deeper; double-click to open.", Ui.Muted, 11));
            TreeView tv = new TreeView { Margin = new Thickness(0, 6, 0, 0), MinHeight = 200, Background = Ui.ListBack, BorderBrush = Ui.Line };
            TreeViewItem root = TreeItem(new JObject { ["key"] = View.Str("key"), ["type"] = View.Str("type"), ["name"] = Ui.Label(View), ["via"] = "" }, 0);
            tv.Items.Add(root);
            root.IsExpanded = true;
            body.Children.Add(tv);
        }

        private TreeViewItem TreeItem(JObject c, int depth)
        {
            string key = c.Str("key");
            StackPanel head = Ui.H(Ui.T(c.Str("type"), Ui.Muted, 11), Ui.Pad(Ui.T(c.Str("name") ?? key), 6, 0, 0, 0));
            if (!string.IsNullOrEmpty(c.Str("via"))) head.Children.Add(Ui.Pad(Ui.T(c.Str("via"), Ui.Muted, 10), 8, 0, 0, 0));
            TreeViewItem it = new TreeViewItem { Header = head, Tag = key, Foreground = Ui.Fg };
            bool loaded = false;
            it.Items.Add(new TreeViewItem { Header = "…" });
            it.Expanded += (o, e) =>
            {
                if (!ReferenceEquals(e.OriginalSource, it) || loaded) return;
                loaded = true;
                it.Items.Clear();
                List<object> kids;
                try { kids = M.ControllerChildren(key); }
                catch (Exception ex) when (IsUserError(ex)) { it.Items.Add(new TreeViewItem { Header = ex.Message }); return; }
                if (depth > 40) { it.Items.Add(new TreeViewItem { Header = "(deeper levels not shown)" }); return; }
                foreach (JObject k in kids.Cast<JObject>()) it.Items.Add(TreeItem(k, depth + 1));
                if (kids.Count == 0) it.Items.Add(new TreeViewItem { Header = Ui.T("plays no other controller", Ui.Muted, 11) });
            };
            it.MouseDoubleClick += (o, e) =>
            {
                if (!it.IsSelected) return;
                e.Handled = true;
                Go(key);
            };
            return it;
        }
    }

    /// <summary>the states a run passed through, as bars on a time axis, with a mark at each transition</summary>
    internal sealed class SimView : FrameworkElement
    {
        private const double Gutter = 8, RulerH = 22, BarH = 26;
        private readonly List<JObject> segs, log;
        private readonly double duration;
        private double scale = 1;

        public SimView(List<JObject> segs, List<JObject> log, double duration)
        {
            this.segs = segs;
            this.log = log;
            this.duration = Math.Max(1, duration);
            Height = RulerH + BarH + 10;
            ClipToBounds = true;
            ToolTipService.SetInitialShowDelay(this, 200);
        }

        public void FitTo(double width)
        {
            scale = Math.Max(0.05, Math.Min(12, (width - Gutter - 20) / duration));
            Width = Gutter + duration * scale + 20;
            InvalidateVisual();
        }

        private double X(double t) { return Gutter + t * scale; }

        private static readonly SolidColorBrush[] Fill =
        {
            Ui.Frozen(Ui.C(0x2F, 0x4A, 0x78)), Ui.Frozen(Ui.C(0x3A, 0x5A, 0x3A)), Ui.Frozen(Ui.C(0x5A, 0x48, 0x2A)), Ui.Frozen(Ui.C(0x4A, 0x36, 0x5A)), Ui.Frozen(Ui.C(0x2E, 0x52, 0x55)),
        };

        protected override void OnRender(DrawingContext dc)
        {
            double w = ActualWidth, h = ActualHeight;
            dc.DrawRectangle(Ui.ListBack, null, new Rect(0, 0, w, h));
            double step = new[] { 6.0, 12, 30, 60, 120, 300, 600, 1200, 3000, 6000 }.First(s => s * scale >= 50 || s == 6000);
            Pen tick = Draw.Pen(Ui.Soft, 1);
            for (double tk = 0; tk <= duration + step; tk += step)
            {
                double x = X(tk);
                dc.DrawLine(tick, new Point(x, RulerH - 5), new Point(x, h));
                dc.DrawText(Draw.Text(tk.ToString("0", CultureInfo.InvariantCulture) + (tk % 60 == 0 ? "  " + (tk / 60).ToString("0.#", CultureInfo.InvariantCulture) + "s" : ""), 10, Ui.Muted, this), new Point(x + 2, 4));
            }
            Dictionary<string, int> colour = new Dictionary<string, int>();
            foreach (JObject s in segs)
            {
                string k = s.Str("node");
                int ci;
                if (!colour.TryGetValue(k, out ci)) { ci = colour.Count % Fill.Length; colour[k] = ci; }
                double x0 = X(s.Dbl("start")), x1 = Math.Max(x0 + 2, X(s.Dbl("end")));
                Rect r = new Rect(x0, RulerH + 2, x1 - x0, BarH);
                dc.DrawRoundedRectangle(Fill[ci], Draw.Pen(Ui.Line, 1), r, 2, 2);
                FormattedText ft = Draw.Text(s.Str("name"), 11, Ui.Fg, this);
                ft.MaxTextWidth = Math.Max(1, r.Width - 6);
                ft.MaxLineCount = 1;
                ft.Trimming = TextTrimming.CharacterEllipsis;
                dc.PushClip(new RectangleGeometry(r));
                dc.DrawText(ft, new Point(r.X + 3, r.Y + 5));
                dc.Pop();
            }
            Pen mark = Draw.Pen(Ui.Warn, 1.5);
            foreach (JObject l in log) { double x = X(l.Dbl("time")); dc.DrawLine(mark, new Point(x, RulerH - 2), new Point(x, RulerH + BarH + 4)); }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            double t = (e.GetPosition(this).X - Gutter) / scale;
            JObject s = segs.LastOrDefault(x => x.Dbl("start") <= t && t <= x.Dbl("end"));
            ToolTip = s == null ? null : s.Str("name") + "\n" + Ui.Num(Math.Round(s.Dbl("start"), 2)) + " – " + Ui.Num(Math.Round(s.Dbl("end"), 2)) + " ticks" +
                                          (s.Str("subject_name") != null ? "\nplays " + s.Str("subject_name") : "");
        }
    }
}
