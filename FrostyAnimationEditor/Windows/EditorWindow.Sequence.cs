using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using FrostyAnimationEditor.Core;
using FrostyAnimationEditor.Core.Anim;

namespace FrostyAnimationEditor.Windows
{
    internal sealed partial class EditorWindow
    {
        // ================================================================== sequence
        private string seqFor;
        private int seqActor, seqSelT = -1, seqSelI = -1;
        private SequenceView seqView;
        private string seqViewKey;      // the object the timeline shows (container or actor)

        /// <summary>the sequence's key for the player view: the container when this is one of its actors</summary>
        private string SeqPreviewKey(JObject s)
        {
            if (s.Str("type") == "SequenceContainerAsset") return s.Str("key");
            List<object> cs = s.Arr("containers");
            return cs != null && cs.Count == 1 ? ((JObject)cs[0]).Str("key") : s.Str("key");
        }

        private void RenderSeq()
        {
            JObject s = M.Seq(ObjKey);
            if (seqFor != ObjKey) { seqActor = 0; seqSelT = -1; seqSelI = -1; seqFor = ObjKey; }
            List<JObject> actors = s.Arr("actors").Cast<JObject>().ToList();
            if (actors.Count == 0) { body.Children.Add(Ui.Wrap("This sequence has no actor (ActorControllerAsset).", Ui.Muted)); return; }
            seqActor = Math.Max(0, Math.Min(actors.Count - 1, seqActor));
            JObject a = actors[seqActor];
            string actorKey = a.Str("key");

            // ---- top row
            WrapPanel top = Ui.Flow();
            if (actors.Count > 1)
            {
                ComboBox pick = Ui.Combo(actors.Select((x, i) => Ui.KV(i.ToString(CultureInfo.InvariantCulture), x.Str("name"))), seqActor.ToString(CultureInfo.InvariantCulture), 320);
                pick.SelectionChanged += (o, e) =>
                {
                    int v;
                    if (int.TryParse(Ui.Value(pick), out v) && v != seqActor) { seqActor = v; seqSelT = -1; seqSelI = -1; SetTab("seq", true); }
                };
                top.Children.Add(Ui.H(Ui.T("actor ", Ui.Muted), pick, Ui.Pad(Ui.T(actors.Count + " actors", Ui.Muted, 11), 8, 0, 8, 0)));
            }
            string previewKey = SeqPreviewKey(s);
            top.Children.Add(Ui.B("Player view…", () => PlayerWindow.OpenSequence(this, previewKey),
                actors.Count > 1 || previewKey != ObjKey ? "play every actor of the sequence on the player skeleton" : "play the sequence on the player skeleton"));
            top.Children.Add(Ui.B("Add track", () => SeqDo("track_add", actorKey, null)));
            top.Children.Add(Ui.B("Fit length", () => SeqDo("fit", actorKey, null), "set Length to the end of the longest track"));
            if (s.Str("type") != "SequenceContainerAsset")
                foreach (JObject c in s.Arr("containers").Cast<JObject>())
                {
                    string ck = c.Str("key");
                    top.Children.Add(Ui.Pad(Ui.H(Ui.T("in ", Ui.Muted, 11), Ui.Link(c.Str("name"), () => Go(ck))), 8, 4, 0, 0));
                }
            body.Children.Add(top);

            // ---- actor fields
            Ui.Form af = new Ui.Form(150);
            af.Row("length (ticks)", SeqNumBox(a.Dbl("length"), v => SeqDo("actor_set", actorKey, new JObject { ["field"] = "Length", ["value"] = v }),
                   "the sequence's length: the actor's time stops there (60 ticks a second at FPS 30 × TimeScale 0.5)"));
            af.Row("loop point", SeqNumBox(a.Dbl("loop_point"), v => SeqDo("actor_set", actorKey, new JObject { ["field"] = "LoopPoint", ["value"] = v }),
                   "-1: no loop; 0 or more: time past Length goes to the controller's loop handler"));
            JObject init = a.Obj("init");
            if (init != null && init.Bool("present"))
            {
                WrapPanel place = Ui.Flow(
                    Ui.T("x ", Ui.Muted, 11), SeqNumBox(init.Dbl("x"), v => SeqDo("init_set", actorKey, new JObject { ["field"] = "X", ["value"] = v }), "rig units (feet)", 70),
                    Ui.T(" y ", Ui.Muted, 11), SeqNumBox(init.Dbl("y"), v => SeqDo("init_set", actorKey, new JObject { ["field"] = "Y", ["value"] = v }), "rig units (feet)", 70),
                    Ui.T(" z ", Ui.Muted, 11), SeqNumBox(init.Dbl("z"), v => SeqDo("init_set", actorKey, new JObject { ["field"] = "Z", ["value"] = v }), "rig units (feet)", 70),
                    Ui.T(" facing ", Ui.Muted, 11), SeqNumBox(init.Dbl("facing"), v => SeqDo("init_set", actorKey, new JObject { ["field"] = "FacingAngle", ["value"] = v }), "radians", 70),
                    Ui.T(" start tick ", Ui.Muted, 11), SeqNumBox(init.Dbl("start_tick"), v => SeqDo("init_set", actorKey, new JObject { ["field"] = "StartTick", ["value"] = v }), null, 60));
                af.Row("placement", place);
            }
            body.Children.Add(af.Grid);

            // ---- timeline, with the actor's tags under the tracks on the same time axis
            SequenceView view = new SequenceView();
            seqView = view;
            seqViewKey = previewKey;
            view.SetActor(a, seqSelT, seqSelI);
            JObject tags = M.TagsView(actorKey);
            TagParts tp = TagPanel(actorKey, tags, false);
            StackPanel lanes = new StackPanel();
            lanes.Children.Add(view);
            lanes.Children.Add(tp.View);
            ScrollViewer sv = new ScrollViewer { HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, Content = lanes, Margin = new Thickness(0, 6, 0, 4) };
            Action<double> sync = null;
            sync = sc =>
            {
                double span = Math.Max(view.Span, tp.View.Span);
                view.SetScale(sc, span);
                tp.View.SetScale(sc, span);
            };
            sv.Loaded += (o, e) =>
            {
                double span = Math.Max(view.Span, tp.View.Span);
                sync(Math.Max(0.2, Math.Min(12, (Math.Max(400, sv.ActualWidth) - TagView.Gutter - 30) / span)));
            };
            view.ScaleChanged += sc => sync(sc);
            tp.View.ScaleChanged += sc => sync(sc);
            tp.View.Scrubbed += ticks => { view.SetPlayhead(ticks); PlayerWindow.ShowSequenceTime(previewKey, ticks); };
            view.Scrubbed += ticks => tp.View.SetPlayhead(ticks);
            body.Children.Add(sv);
            body.Children.Add(Ui.T("drag a bar to move it, its edges to trim it · double-click: open the controller · click the ruler: show that time in the player view · " +
                                   "Ctrl+wheel: zoom · right-click a track name: track menu", Ui.Muted, 11));
            StackPanel insp = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
            body.Children.Add(insp);
            Action<int, int> showSel = (t, i) =>
            {
                seqSelT = t; seqSelI = i;
                insp.Children.Clear();
                if (t < 0 || i < 0) { insp.Children.Add(Ui.T("select an entry", Ui.Muted)); return; }
                JObject tr = (JObject)a.Arr("tracks")[t];
                JObject en = (JObject)tr.Arr("entries")[i];
                insp.Children.Add(SeqInspector(actorKey, t, i, en, tr.Arr("entries").Count));
            };
            view.Selected += (t, i) => showSel(t, i);
            view.Moved += (t, i, d) => SeqDo("entry_move", actorKey, new JObject { ["track"] = (long)t, ["index"] = (long)i, ["delta"] = (long)d }, t, i);
            view.Trimmed += (t, i, si, ei) => SeqDo("entry_trim", actorKey, new JObject { ["track"] = (long)t, ["index"] = (long)i, ["start_in"] = (long)si, ["end_in"] = (long)ei }, t, i);
            view.Opened += (t, i) =>
            {
                JObject en = (JObject)((JObject)a.Arr("tracks")[t]).Arr("entries")[i];
                if (en.Str("controller") != null) Go(en.Str("controller"));
            };
            view.Scrubbed += ticks => PlayerWindow.ShowSequenceTime(previewKey, ticks);
            view.TrackMenu += (t, pt) =>
            {
                ContextMenu cm = new ContextMenu();
                MenuItem add = new MenuItem { Header = "Add an entry at the end…" };
                add.Click += (o, e) => SeqAddEntry(actorKey, t, ((JObject)a.Arr("tracks")[t]).Arr("entries").Count - 1);
                MenuItem rm = new MenuItem { Header = "Remove track " + t };
                rm.Click += (o, e) => { if (Dialog.Confirm(this, "Remove track", "Remove track " + t + " and its entries?")) SeqDo("track_remove", actorKey, new JObject { ["track"] = (long)t }); };
                cm.Items.Add(add);
                cm.Items.Add(rm);
                cm.IsOpen = true;
            };
            showSel(seqSelT, seqSelI);

            // ---- tags of the actor
            body.Children.Add(Ui.H3("Tags"));
            if (tags.Long("set_users") > 1) body.Children.Add(Ui.T("the tag collection set is shared by " + tags.Long("set_users") + " controllers: edits change them all", Ui.Warn, 11));
            body.Children.Add(tp.Buttons);
            body.Children.Add(tp.Hint);
            body.Children.Add(tp.Inspector);

            // ---- relative IK wrappers of this actor
            List<JObject> riks = (a.Arr("relative_ik") ?? new List<object>()).Cast<JObject>().ToList();
            if (riks.Count > 0)
            {
                body.Children.Add(Ui.H3("Relative IK"));
                foreach (JObject w in riks)
                {
                    string wk = w.Str("key");
                    body.Children.Add(Ui.H(Ui.Link(w.Str("name"), () => Go(wk)), Ui.Pad(Ui.T(Ui.Plural(w.Arr("entries").Count, "entry", "entries"), Ui.Muted, 11), 8, 0, 0, 0)));
                    List<object[]> rows = new List<object[]>();
                    foreach (JObject e in w.Arr("entries").Cast<JObject>())
                    {
                        string ek = e.Str("key"), tk = e.Str("target");
                        rows.Add(new object[]
                        {
                            Ui.Link(e.Long("effector") + " / " + e.Long("mirrored_effector"), () => Go(ek), null, "IKJointEffectorID / MirroredIKJointEffectorID"),
                            tk != null ? (object)Ui.Link(e.Str("target_name") ?? tk, () => Go(tk)) : "—",
                            e.Long("translation_keys") + " from " + e.Long("translation_offset"),
                            e.Long("rotation_keys") + " from " + e.Long("rotation_offset"),
                            e.Long("condition").ToString(CultureInfo.InvariantCulture),
                        });
                    }
                    body.Children.Add(Ui.Table(new[] { "effector", "target animatable", "translation keys", "rotation keys", "ActiveCondition" }, rows));
                }
            }

            // ---- what the timing means
            body.Children.Add(Ui.Pad(Ui.Wrap("How the game plays it: an entry is active from StartTime + StartInTime to StartTime + EndInTime and runs its " +
                "controller at (time − StartTime) × Scale; a ClipController shows clip frame = its ticks × TimeScale + the clip's TrimOffset and lasts " +
                "ceil(TrimmedDuration / TimeScale) ticks. The next entry blends in over its blend's BlendTime from its begin (the game's own sequences overlap by " +
                "exactly that much). Tracks are merged, later tracks over earlier ones for the channels they have (the hand and face tracks).", Ui.Muted, 11), 0, 10, 0, 0));
        }

        private TextBox SeqNumBox(double value, Action<double> set, string tip = null, double width = 90)
        {
            TextBox b = Ui.Box(Ui.Num(value), width, true, (tip != null ? tip + " · " : "") + "Enter to apply");
            b.KeyDown += (o, e) =>
            {
                if (e.Key != Key.Enter) return;
                e.Handled = true;
                double v;
                if (!double.TryParse(b.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out v)) { Msg("not a number: " + b.Text, true); return; }
                set(v);
            };
            return b;
        }

        /// <summary>runs a sequence edit; keeps the selected entry</summary>
        private void SeqDo(string op, string actorKey, JObject args, int keepT = -2, int keepI = -2)
        {
            JObject a = args ?? new JObject();
            a["actor"] = actorKey;
            a["view"] = ObjKey;
            JObject r = null;
            if (!Guard(() => r = M.SeqOp(op, a))) return;
            if (keepT != -2) { seqSelT = keepT; seqSelI = keepI; }
            if (r != null && r["op_index"] != null && a["track"] != null) { seqSelT = (int)a.Long("track"); seqSelI = (int)r.Long("op_index"); }
            if (op == "entry_remove" || op == "track_remove") { seqSelT = -1; seqSelI = -1; }
            AfterEdit();
            PlayerWindow.Refresh();
            Msg("sequence: " + op.Replace('_', ' '));
        }

        private void SeqAddEntry(string actorKey, int track, int after)
        {
            Dialog d = new Dialog(this, "Add an entry", 700);
            ObjectPicker pk = new ObjectPicker(M, "ClipControllerAsset");
            string chosen = null;
            pk.Picked += o => { chosen = o.Str("key"); d.Accepted = true; d.Close(); };
            d.Add(Ui.Wrap("The controller the entry plays (a ClipControllerAsset). The entry copies its neighbour's blend and init params, starts where the " +
                          "previous entry ends minus the blend time and lasts the controller's length.", Ui.Muted, 11), pk.Panel);
            d.Button("Cancel");
            d.ShowDialog();
            if (chosen == null) return;
            SeqDo("entry_add", actorKey, new JObject { ["track"] = (long)track, ["index"] = (long)after, ["controller"] = chosen });
        }

        private UIElement SeqInspector(string actorKey, int t, int i, JObject en, int count)
        {
            StackPanel p = new StackPanel();
            JObject clip = en.Obj("clip");
            string ctrl = en.Str("controller");
            StackPanel head = Ui.H(Ui.T("Track " + t + " · entry " + i + "  ", Ui.Muted), ctrl != null ? (UIElement)Ui.Link(en.Str("label"), () => Go(ctrl)) : Ui.T("(no controller)", Ui.Bad),
                                   Ui.Pad(Ui.T(en.Str("controller_type"), Ui.Muted, 11), 8, 0, 0, 0));
            p.Children.Add(head);
            Ui.Form f = new Ui.Form(150);
            Func<string, Action<double>> setEntry = field => v => SeqDo("entry_set", actorKey, new JObject { ["track"] = (long)t, ["index"] = (long)i, ["field"] = field, ["value"] = v }, t, i);
            double secs = (en.Long("end") - en.Long("begin")) / 60.0;
            f.Row("active", Ui.T(en.Long("begin") + " – " + en.Long("end") + " ticks (" + secs.ToString("0.##", CultureInfo.InvariantCulture) + " s)", null, 12, false, true));
            f.Row("StartTime", SeqNumBox(en.Long("start_time"), setEntry("StartTime"), "sequence time of in-time 0"));
            f.Row("StartInTime", SeqNumBox(en.Long("start_in"), setEntry("StartInTime"), "in-time where the entry starts; its controller starts at StartInTime × Scale"));
            f.Row("EndInTime", SeqNumBox(en.Long("end_in"), setEntry("EndInTime"), "in-time where it ends"));
            f.Row("Scale", SeqNumBox(en.Dbl("scale"), setEntry("Scale"), "controller ticks per sequence tick"));
            CheckBox mir = Ui.Check("mirrored (Flags bit 0)", en.Bool("mirror"), "the game mirrors this entry's pose (XOR with the actor's mirror flag); the preview does not");
            mir.Click += (o, e) => SeqDo("entry_set", actorKey, new JObject { ["track"] = (long)t, ["index"] = (long)i, ["field"] = "Flags", ["value"] = (en.Long("flags") & ~1L) | (Ui.On(mir) ? 1L : 0L) }, t, i);
            f.Row("flags", mir);
            if (clip != null)
            {
                string txt = "frames " + Ui.Num(clip.Dbl("first_frame")) + " – " + Ui.Num(clip.Dbl("last_frame")) + " of " + clip.Long("frames") +
                             " · TimeScale " + Ui.Num(clip.Dbl("time_scale")) + " · TrimOffset " + Ui.Num(clip.Dbl("trim_offset")) + " · TrimmedDuration " + Ui.Num(clip.Dbl("trimmed_duration")) +
                             " (" + Ui.Num(clip.Dbl("num_ticks")) + " ticks)" + (clip.Bool("looping") ? " · looping" : "");
                f.Row("clip", Ui.Wrap(txt, clip.Bool("past_end") ? Ui.Warn : null, 12));
                if (clip.Bool("past_end")) f.Row("", Ui.Wrap("The entry runs past the clip's last frame: the game holds that frame.", Ui.Warn, 11));
            }
            if (en.Long("start_tick") != 0) f.Row("init StartTick", Ui.T(Ui.Num(en.Dbl("start_tick")), Ui.Muted));
            p.Children.Add(f.Grid);

            // blend in
            if (i > 0 || en.Str("blend") != null)
            {
                Ui.Form bf = new Ui.Form(150);
                TextBox bt = Ui.Box(Ui.Num(en.Dbl("blend_time")), 80, true, "ticks");
                ComboBox curve = Ui.Combo(Enumerable.Range(0, 4).Select(c => Ui.KV(c.ToString(CultureInfo.InvariantCulture), Sequence.CurveNames[c])), en.Long("curve").ToString(CultureInfo.InvariantCulture), 160);
                long users = en.Long("blend_users");
                CheckBox own = Ui.Check(users > 1 ? "own copy for this entry (the blend is shared by " + users + " objects)" : "own copy", users > 1,
                                        "change a new BlendCurveAsset for this entry instead of the shared one");
                Button apply = Ui.B("Apply", () =>
                {
                    double v;
                    if (!double.TryParse(bt.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out v)) { Msg("not a number: " + bt.Text, true); return; }
                    long cv;
                    long.TryParse(Ui.Value(curve) ?? "3", out cv);
                    SeqDo("blend_set", actorKey, new JObject { ["track"] = (long)t, ["index"] = (long)i, ["time"] = v, ["curve"] = cv, ["own"] = Ui.On(own) }, t, i);
                }, null, true);
                bf.Row("blend in", Ui.Flow(bt, Ui.Pad(Ui.T("ticks", Ui.Muted, 11), 4, 4, 10, 0), curve, Ui.Pad(apply, 8, 0, 0, 0)));
                bf.Row("", own);
                string bk = en.Str("blend");
                if (bk != null) bf.Row("blend asset", Ui.H(Ui.Link(bk, () => Go(bk)), Ui.Pad(Ui.T(en.Str("blend_type") + (en.Bool("sync_phase") ? " · syncs phase" : ""), Ui.Muted, 11), 8, 0, 0, 0)));
                p.Children.Add(bf.Grid);
            }

            WrapPanel acts = Ui.Flow(
                Ui.B("Add entry after…", () => SeqAddEntry(actorKey, t, i)),
                Ui.B("Change controller…", () =>
                {
                    Dialog d = new Dialog(this, "Change the controller", 700);
                    ObjectPicker pk = new ObjectPicker(M, "ClipControllerAsset");
                    string chosen = null;
                    pk.Picked += o => { chosen = o.Str("key"); d.Accepted = true; d.Close(); };
                    d.Add(pk.Panel);
                    d.Button("Cancel");
                    d.ShowDialog();
                    if (chosen != null) SeqDo("entry_set", actorKey, new JObject { ["track"] = (long)t, ["index"] = (long)i, ["field"] = "Asset", ["value"] = chosen }, t, i);
                }),
                Ui.B("Earlier", () => SeqDo("entry_order", actorKey, new JObject { ["track"] = (long)t, ["index"] = (long)i, ["to"] = (long)(i - 1) }, t, i - 1)),
                Ui.B("Later", () => SeqDo("entry_order", actorKey, new JObject { ["track"] = (long)t, ["index"] = (long)i, ["to"] = (long)(i + 1) }, t, i + 1)),
                Ui.B("Remove", () => SeqDo("entry_remove", actorKey, new JObject { ["track"] = (long)t, ["index"] = (long)i })));
            ((Button)acts.Children[2]).IsEnabled = i > 0;
            ((Button)acts.Children[3]).IsEnabled = i < count - 1;
            p.Children.Add(Ui.Pad(acts, 0, 6, 0, 0));
            return Ui.Card(p);
        }

        /// <summary>the player view shows a sequence time: move the timeline's playhead</summary>
        public void SequenceTimeShown(string key, double ticks)
        {
            if (seqView != null && seqViewKey == key && tab == "seq") seqView.SetPlayhead(ticks);
            TagTimeShown(key, ticks);
        }
    }
}
