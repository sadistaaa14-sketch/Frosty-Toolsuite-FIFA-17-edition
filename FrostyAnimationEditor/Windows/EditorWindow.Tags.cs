using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using FrostyAnimationEditor.Core;

namespace FrostyAnimationEditor.Windows
{
    internal sealed partial class EditorWindow
    {
        // ================================================================== tags
        private string tagsFor;
        private int tagSelC = -1, tagSelT = -1;
        private TagView tagView;
        private string tagViewKey;

        private void RenderTags()
        {
            JObject tv = M.TagsView(ObjKey);
            if (tagsFor != ObjKey) { tagSelC = -1; tagSelT = -1; tagsFor = ObjKey; }
            string owner = tv.Str("key");
            if (tv.Str("set") == null)
            {
                body.Children.Add(Ui.Wrap("No tags: this controller has no tag collection set (ControllerAsset.TagCollectionSet is 0).", Ui.Muted));
                body.Children.Add(Ui.Pad(Ui.Flow(Ui.B("Add a tag collection set", () => TagDo("set_add", owner, new JObject { ["type"] = "DefaultTagCollectionAsset" }),
                    "a new TagCollectionSetAsset with one DefaultTagCollectionAsset")), 0, 6, 0, 0));
                body.Children.Add(Ui.Pad(TagHelp(tv), 0, 10, 0, 0));
                return;
            }
            WrapPanel top = Ui.Flow();
            string sk = tv.Str("set");
            top.Children.Add(Ui.H(Ui.T("set ", Ui.Muted, 11), Ui.Link(sk, () => Go(sk))));
            if (tv.Long("set_users") > 1)
                top.Children.Add(Ui.Pad(Ui.T("shared by " + tv.Long("set_users") + " controllers: edits change them all", Ui.Warn, 11), 10, 0, 0, 0));
            top.Children.Add(Ui.Pad(Ui.T(tv.Bool("length_known") ? "length " + Ui.Num(tv.Dbl("length")) + " ticks" : "the owner's length is not known for " + tv.Str("type"), Ui.Muted, 11), 10, 0, 0, 0));
            body.Children.Add(top);
            TagParts tp = TagPanel(owner, tv, true);
            body.Children.Add(Ui.Pad(tp.Buttons, 0, 6, 0, 0));
            ScrollViewer sv = new ScrollViewer { HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, Content = tp.View, Margin = new Thickness(0, 4, 0, 4) };
            sv.Loaded += (o, e) => tp.View.FitTo(Math.Max(400, sv.ActualWidth));
            body.Children.Add(sv);
            body.Children.Add(tp.Hint);
            body.Children.Add(tp.Inspector);
            body.Children.Add(Ui.Pad(TagHelp(tv), 0, 10, 0, 0));
        }

        private sealed class TagParts
        {
            public TagView View;
            public FrameworkElement Buttons, Hint, Inspector;
        }

        private FrameworkElement TagHelp(JObject tv)
        {
            return Ui.Wrap("How the game reads a tag's window: it starts at StartLinkOffset (at 0 when LinkFlags has 0x80) and lasts EndLinkOffset; " +
                           "with LinkFlags 0x40 both are fractions of the owner's length. Times are the owner controller's ticks: a sequence's ticks for an " +
                           "ActorControllerAsset, the controller's ticks (NumTicks long) for a ClipControllerAsset. LinkFlags 0x10, 0x20 and StartLinkUpdates: " +
                           "what these change is not known.", Ui.Muted, 11);
        }

        /// <summary>the tag timeline of a controller, its buttons and the selected tag's inspector (the caller places them)</summary>
        private TagParts TagPanel(string owner, JObject tv, bool ruler)
        {
            double len = tv.Dbl("length");
            TagView view = new TagView { ShowRuler = ruler };
            tagView = view;
            tagViewKey = owner;
            view.SetTags(tv, len, tagSelC, tagSelT);
            TagParts parts = new TagParts { View = view };
            parts.Buttons = tv.Str("set") == null
                ? (FrameworkElement)Ui.Flow(Ui.B("Add a tag collection set", () => TagDo("set_add", owner, new JObject { ["type"] = "DefaultTagCollectionAsset" }), "a new TagCollectionSetAsset with one DefaultTagCollectionAsset"))
                : Ui.Flow(Ui.B("Add tag…", () => TagAddDialog(owner, tv, -1, 0)), Ui.B("Add collection…", () => TagAddCollection(owner)));
            parts.Hint = Ui.T("drag a tag to move it, its right edge to change its length · double-click: open the tag · right-click a collection: its menu · ×N: listed by N collections",
                              Ui.Muted, 11);
            StackPanel insp = new StackPanel { Margin = new Thickness(0, 6, 0, 0) };
            parts.Inspector = insp;
            List<JObject> colls = tv.Arr("collections").Cast<JObject>().ToList();
            Func<int, int, JObject> tagAt = (c, t) => c >= 0 && c < colls.Count && t >= 0 && t < colls[c].Arr("tags").Count ? (JObject)colls[c].Arr("tags")[t] : null;
            Action<int, int> show = (c, t) =>
            {
                tagSelC = c; tagSelT = t;
                insp.Children.Clear();
                JObject tg = tagAt(c, t);
                if (tg == null) { insp.Children.Add(Ui.T("select a tag", Ui.Muted)); return; }
                insp.Children.Add(TagInspector(owner, tv, colls[c], c, t, tg));
            };
            view.Selected += (c, t) => show(c, t);
            view.Moved += (c, t, d) =>
            {
                JObject tg = tagAt(c, t);
                if (tg != null) TagDo("tag_move", owner, new JObject { ["tag"] = tg.Str("key"), ["delta"] = d }, c, t);
            };
            view.Resized += (c, t, l) =>
            {
                JObject tg = tagAt(c, t);
                if (tg != null) TagDo("tag_set", owner, new JObject { ["tag"] = tg.Str("key"), ["length"] = l }, c, t);
            };
            view.Opened += (c, t) => { JObject tg = tagAt(c, t); if (tg != null) Go(tg.Str("key")); };
            view.Scrubbed += ticks => { PlayerWindow.ShowSequenceTime(owner, ticks); PlayerWindow.ShowControllerTicks(owner, ticks); };
            view.CollectionMenu += (c, pt) =>
            {
                ContextMenu cm = new ContextMenu();
                MenuItem add = new MenuItem { Header = "Add a tag here…" };
                add.Click += (o, e) => TagAddDialog(owner, tv, c, 0);
                MenuItem open = new MenuItem { Header = "Open " + colls[c].Str("type") };
                string ck = colls[c].Str("key");
                open.Click += (o, e) => Go(ck);
                cm.Items.Add(add);
                cm.Items.Add(open);
                cm.IsOpen = true;
            };
            show(tagSelC, tagSelT);
            return parts;
        }

        private UIElement TagInspector(string owner, JObject tv, JObject coll, int c, int t, JObject tg)
        {
            StackPanel p = new StackPanel();
            string key = tg.Str("key"), ck = coll.Str("key");
            p.Children.Add(Ui.H(Ui.Link(tg.Str("type") + (string.IsNullOrEmpty(tg.Str("name")) || tg.Str("name") == tg.Str("type") ? "" : " " + Ui.Quote(tg.Str("name"))), () => Go(key)),
                                Ui.Pad(Ui.T("in " + coll.Str("type"), Ui.Muted, 11), 8, 0, 0, 0)));
            FieldSemantics.Entry about = FieldSemantics.Get(tg.Str("type"), "");
            if (about != null && about.HasMeaning) p.Children.Add(Ui.Wrap(about.Meaning, about.Unidentified ? Ui.Warn : Ui.Muted, 11));
            if (!string.IsNullOrEmpty(tg.Str("summary"))) p.Children.Add(Ui.Wrap(tg.Str("summary"), Ui.Muted, 11));
            Ui.Form f = new Ui.Form(150);
            bool known = tv.Bool("length_known");
            bool norm = tg.Bool("normalized");
            if (!tg.Bool("timeline")) f.Row("timeline", Ui.T("this object has no TimelinePointTag part (no window)", Ui.Muted, 11));
            else
            {
                Func<string, Action<double>> set = field => v => TagDo("tag_set", owner, new JObject { ["tag"] = key, [field] = v }, c, t);
                if (norm && !known)
                {
                    f.Row("start (fraction)", SeqNumBox(tg.Dbl("start_raw"), set("start_raw"), "StartLinkOffset as stored: a fraction of the owner's length"));
                    if (tg.Bool("window")) f.Row("length (fraction)", SeqNumBox(tg.Dbl("length_raw"), set("length_raw"), "EndLinkOffset as stored"));
                }
                else
                {
                    f.Row("start (ticks)", SeqNumBox(tg.Dbl("start"), set("start"), norm ? "stored as a fraction of the length: " + Ui.Num(tg.Dbl("start_raw")) : "StartLinkOffset"));
                    if (tg.Bool("window")) f.Row("length (ticks)", SeqNumBox(tg.Dbl("length"), set("length"), norm ? "stored as a fraction of the length: " + Ui.Num(tg.Dbl("length_raw")) : "EndLinkOffset"));
                    double secs = tg.Dbl("start") / 60.0;
                    f.Row("", Ui.T(Ui.Num(Math.Round(secs, 3)) + " s" + (tg.Bool("window") ? " – " + Ui.Num(Math.Round((tg.Dbl("start") + tg.Dbl("length")) / 60.0, 3)) + " s" : "") + " at 60 ticks a second", Ui.Muted, 11));
                }
                long flags = tg.Long("flags");
                CheckBox zero = Ui.Check("starts at 0 (0x80)", tg.Bool("starts_at_zero"), "the game ignores StartLinkOffset and starts the window at 0");
                zero.Click += (o, e) => TagDo("tag_set", owner, new JObject { ["tag"] = key, ["flags"] = (flags & ~0x80L) | (Ui.On(zero) ? 0x80L : 0) }, c, t);
                CheckBox nrm = Ui.Check("normalized (0x40)", norm, "start and length are fractions of the owner's length (the stored values are converted)");
                nrm.IsEnabled = known;
                nrm.Click += (o, e) =>
                {
                    // keep the window where it is: convert the stored values
                    double L = tv.Dbl("length");
                    bool on = Ui.On(nrm);
                    JObject a = new JObject { ["tag"] = key, ["flags"] = (flags & ~0x40L) | (on ? 0x40L : 0) };
                    a["start_raw"] = on ? tg.Dbl("start") / L : tg.Dbl("start");
                    if (tg.Bool("window")) a["length_raw"] = on ? tg.Dbl("length") / L : tg.Dbl("length");
                    TagDo("tag_set", owner, a, c, t);
                };
                f.Row("LinkFlags", Ui.Flow(zero, Ui.Pad(nrm, 10, 0, 0, 0),
                    Ui.Pad(SeqNumBox(flags, v => TagDo("tag_set", owner, new JObject { ["tag"] = key, ["flags"] = (long)v }, c, t), "the whole 16-bit value; 0x10 / 0x20: not read by the game's code found so far", 60), 10, 0, 0, 0)));
                if (tg.Long("updates") != 0) f.Row("StartLinkUpdates", Ui.T(tg.Long("updates").ToString(CultureInfo.InvariantCulture), Ui.Muted));
            }
            p.Children.Add(f.Grid);
            if (tg.Long("listed") > 1)
                p.Children.Add(Ui.Flow(Ui.T("listed by " + tg.Long("listed") + " collections: edits change it for all of them ", Ui.Warn, 11),
                                       Ui.B("Own copy", () => TagDo("tag_copy", owner, new JObject { ["collection"] = ck, ["tag"] = key }, c, t), "replace it here with a copy only this collection lists")));
            int count = coll.Arr("tags").Count;
            WrapPanel acts = Ui.Flow(
                Ui.B("Open", () => Go(key), "the tag's own fields"),
                Ui.B("Copy", () => TagDo("tag_add", owner, new JObject { ["collection"] = ck, ["template"] = key, ["start"] = tg.Dbl("start"), ["length"] = tg.Dbl("length") }, c, count),
                     "add a copy at the same time"),
                Ui.B("Earlier", () => TagDo("tag_order", owner, new JObject { ["collection"] = ck, ["tag"] = key, ["to"] = (long)(t - 1) }, c, t - 1), "move up in the collection's list"),
                Ui.B("Later", () => TagDo("tag_order", owner, new JObject { ["collection"] = ck, ["tag"] = key, ["to"] = (long)(t + 1) }, c, t + 1)),
                Ui.B("Remove", () => TagDo("tag_remove", owner, new JObject { ["collection"] = ck, ["tag"] = key }), "take it out of the collection (the object goes too when nothing else lists it)"));
            ((Button)acts.Children[2]).IsEnabled = t > 0;
            ((Button)acts.Children[3]).IsEnabled = t < count - 1;
            p.Children.Add(Ui.Pad(acts, 0, 6, 0, 0));
            return Ui.Card(p);
        }

        private void TagDo(string op, string owner, JObject args, int keepC = -2, int keepT = -2)
        {
            JObject a = args ?? new JObject();
            a["owner"] = owner;
            JObject r = null;
            if (!Guard(() => r = M.TagOp(op, a))) return;
            if (keepC != -2) { tagSelC = keepC; tagSelT = keepT; }
            if (op == "tag_remove") { tagSelC = -1; tagSelT = -1; }
            AfterEdit();
            Msg("tags: " + op.Replace('_', ' '));
        }

        private void TagAddDialog(string owner, JObject tv, int collection, double at)
        {
            List<JObject> colls = tv.Arr("collections").Cast<JObject>().ToList();
            if (colls.Count == 0) { Msg("add a collection first", true); return; }
            Dialog d = new Dialog(this, "Add a tag", 640);
            ComboBox cb = Ui.Combo(colls.Select((x, i) => Ui.KV(i.ToString(CultureInfo.InvariantCulture), x.Str("type") + " (" + x.Arr("tags").Count + ")")),
                                   Math.Max(0, collection).ToString(CultureInfo.InvariantCulture), 320);
            List<object> types = M.TagTypes();
            ComboBox tb = Ui.Combo(types.Cast<JObject>().OrderByDescending(x => x.Long("count")).Select(x => Ui.KV(x.Str("type"), x.Str("type") + " (" + x.Long("count") + " in the file)")),
                                   "FootPlantResetTag", 420);
            TextBox start = Ui.Box(Ui.Num(at), 80, true, "ticks");
            TextBox length = Ui.Box("0", 80, true, "ticks");
            d.Add(Ui.Wrap("A new tag of the class, copied from the first tag of that class in the file (so its own fields start from a value the game uses), at the time given.", Ui.Muted, 11));
            Ui.Form f = new Ui.Form(110);
            f.Row("collection", cb);
            f.Row("class", tb);
            f.Row("start", start);
            f.Row("length", length);
            d.Add(f.Grid);
            d.Button("Cancel");
            d.Button("Add", () => true, true);
            if (!d.Run()) return;
            int ci;
            int.TryParse(Ui.Value(cb) ?? "0", out ci);
            double s, l;
            if (!double.TryParse(start.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out s)) s = 0;
            if (!double.TryParse(length.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out l)) l = 0;
            string type = Ui.Value(tb);
            JObject a = new JObject { ["collection"] = colls[ci].Str("key"), ["start"] = s, ["length"] = l };
            string tpl = M.FirstOfType(type);
            if (tpl != null) a["template"] = tpl; else a["type"] = type;
            TagDo("tag_add", owner, a, ci, colls[ci].Arr("tags").Count);
        }

        private void TagAddCollection(string owner)
        {
            Dialog d = new Dialog(this, "Add a tag collection", 520);
            ComboBox tb = Ui.Combo(new[] { Ui.KV("DefaultTagCollectionAsset", "DefaultTagCollectionAsset (most tags)"), Ui.KV("BranchOutTagCollectionAsset", "BranchOutTagCollectionAsset (branch-out windows)"),
                                           Ui.KV("BranchInTagCollectionAsset", "BranchInTagCollectionAsset (branch-in points)"), Ui.KV("ImmediateTagCollectionAsset", "ImmediateTagCollectionAsset (expression tags)") },
                                   "DefaultTagCollectionAsset", 420);
            d.Add(tb);
            d.Button("Cancel");
            d.Button("Add", () => true, true);
            if (!d.Run()) return;
            TagDo("collection_add", owner, new JObject { ["type"] = Ui.Value(tb) });
        }

        /// <summary>the player view shows a time of the sequence or controller: move the tag timeline's playhead too</summary>
        private void TagTimeShown(string key, double ticks)
        {
            if (tagView != null && (tab == "seq" || (tab == "tags" && tagViewKey == key))) tagView.SetPlayhead(ticks);
        }
    }
}
