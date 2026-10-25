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
        private FrameworkElement RefLink(JObject r, string none = "—")
        {
            if (r == null) return Ui.T(none, Ui.Muted, 11);
            string k = r.Str("key");
            return Ui.H(Ui.Link(r.Str("name"), () => Go(k), null, r.Str("type") + " " + k), Ui.Pad(Ui.T(r.Str("type"), Ui.Muted, 10), 6, 0, 0, 0));
        }

        // ================================================================== chooser
        private void RenderChooser()
        {
            JObject c = M.Chooser(ObjKey);
            JObject pol = c.Obj("policy");
            string ck = c.Str("key");
            Ui.Form f = new Ui.Form(150);
            string pk = pol.Str("key");
            f.Row("policy", pk != null ? (FrameworkElement)Ui.H(Ui.Link(pol.Str("name"), () => Go(pk)), Ui.Pad(Ui.T(pol.Str("type"), Ui.Muted, 11), 6, 0, 0, 0)) : Ui.T("none (no entry is chosen)", Ui.Warn));
            string ptype = pol.Str("type");
            if (ptype == "RandomChooserPolicyAsset")
                f.Row("how it picks", Ui.Wrap("at random by Weight: a random number from the game state of " + (pol.Obj("random_value") != null ? pol.Obj("random_value").Str("name") : "its RandomValueAsset") +
                                              " scaled to the sum of the weights, then the entries are counted off in order", null, 12));
            else if (ptype == "EnumChooserPolicyAsset")
            {
                f.Row("how it picks", Ui.Wrap("the entry whose enumeration value is the value of the game state; entries without a value are the default", null, 12));
                f.Row("game state", RefLink(pol.Obj("game_state")));
                f.Row("enumeration", RefLink(pol.Obj("enumeration")));
            }
            else if (ptype == "SignalChooserPolicyAsset")
                f.Row("how it picks", Ui.Wrap("the first entry whose true signals are all on and false signals all off" + (pol.Bool("cache_last") ? "; it keeps the previous entry while that one still holds (CacheLastChoice)" : ""), null, 12));
            else if (pk != null)
                f.Row("how it picks", Ui.T("how " + ptype + " picks is not known", Ui.Warn, 11));
            JObject fl = c.Obj("flags");
            List<string> on = fl.Keys.Where(k => fl.Bool(k)).ToList();
            f.Row("when", Ui.Wrap(c.Bool("once") ? "once, when the controller is made (it falls back to the last entry when the policy picks none)"
                                               : "re-chooses: " + string.Join(", ", on), null, 12));
            if (c.Obj("current_choice") != null) f.Row("CurrentChoice", Ui.H(RefLink(c.Obj("current_choice")), Ui.Pad(Ui.T("receives the chosen index", Ui.Muted, 11), 8, 0, 0, 0)));
            if (c.Obj("blend_tag") != null) f.Row("BlendTag", RefLink(c.Obj("blend_tag")));
            body.Children.Add(f.Grid);

            List<JObject> es = c.Arr("entries").Cast<JObject>().ToList();
            List<object[]> rows = new List<object[]>();
            foreach (JObject e in es)
            {
                int i = (int)e.Long("index");
                string ek = e.Str("key");
                object extra = "";
                if (e["weight"] != null)
                {
                    TextBox wb = Ui.Box(Ui.Num(e.Dbl("weight")), 60, true, "Weight · Enter to apply");
                    wb.KeyDown += (o, ev) =>
                    {
                        if (ev.Key != Key.Enter) return;
                        ev.Handled = true;
                        double v;
                        if (!double.TryParse(wb.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out v)) { Msg("not a number: " + wb.Text, true); return; }
                        if (Guard(() => M.Set(ek, "Weight", v))) { AfterEdit(); Msg("weight set"); }
                    };
                    extra = Ui.H(wb, Ui.Pad(Ui.T((e.Dbl("probability") * 100).ToString("0.#", CultureInfo.InvariantCulture) + " %", Ui.Muted, 11), 6, 3, 0, 0));
                }
                else if (e["enum_value"] != null)
                {
                    JObject ev = e.Obj("enum_value");
                    extra = Ui.H(RefLink(ev), Ui.Pad(Ui.T(ev["value"] != null ? "= " + ev.Long("value") : "", Ui.Muted, 11), 6, 0, 0, 0));
                }
                else if (e.Str("type") == "EnumChooserEntryAsset") extra = Ui.T("no value: the default", Ui.Muted, 11);
                else if (e["true_signals"] != null)
                {
                    List<string> parts = e.Arr("true_signals").Cast<JObject>().Where(x => x != null).Select(x => x.Str("name"))
                        .Concat(e.Arr("false_signals").Cast<JObject>().Where(x => x != null).Select(x => "!" + x.Str("name"))).ToList();
                    extra = Ui.Wrap(parts.Count > 0 ? string.Join(" & ", parts) : "always", Ui.Muted, 11);
                }
                StackPanel order = Ui.H(Ui.B("↑", () => ChooserMove(ck, i, -1), "earlier", true), Ui.B("↓", () => ChooserMove(ck, i, 1), "later", true));
                ((Button)order.Children[0]).IsEnabled = i > 0;
                ((Button)order.Children[1]).IsEnabled = i < es.Count - 1;
                rows.Add(new object[]
                {
                    Ui.T(i.ToString(CultureInfo.InvariantCulture), Ui.Muted),
                    ek != null ? (object)Ui.Link(e.Str("type"), () => Go(ek)) : Ui.T("(missing)", Ui.Bad),
                    RefLink(e.Obj("controller"), "no controller"),
                    extra,
                    order,
                });
            }
            body.Children.Add(Ui.H3("Entries"));
            body.Children.Add(Ui.Table(new[] { "#", "entry", "plays", ptype == "RandomChooserPolicyAsset" ? "weight" : ptype == "EnumChooserPolicyAsset" ? "value" : "signals", "" }, rows));
            body.Children.Add(Ui.Wrap("The policy picks the entry (by weight, by enumeration value or by signals) and the game stores the choice in CurrentChoice. " +
                                      "A chooser with none of ChooseOnReset, ChooseEveryUpdate, ChooseFirstUpdate and Looping chooses once, when it starts.", Ui.Muted, 11));
        }

        private void ChooserMove(string chooserKey, int index, int by)
        {
            if (Guard(() => M.ListOp(chooserKey, "EntryAssetList", by < 0 ? "up" : "down", index))) { AfterEdit(); Msg("entry moved"); }
        }

        // ================================================================== layers
        private void RenderLayers()
        {
            JObject l = M.Layers(ObjKey);
            string lk = l.Str("key");
            List<JObject> ls = l.Arr("layers").Cast<JObject>().ToList();
            body.Children.Add(Ui.Wrap(ls.Count + " layers, combined in order. Each update a layer runs when its UpdateMode allows it; " +
                                      "its time moves as its time mode (UpdateRates) says; phase = time / length.", Ui.Muted, 11));
            List<object[]> rows = new List<object[]>();
            foreach (JObject e in ls)
            {
                int i = (int)e.Long("index");
                Func<string, long, string[], FrameworkElement> modeBox = (field, val, names) =>
                {
                    ComboBox cb = Ui.Combo(Enumerable.Range(0, Math.Max(names.Length, (int)val + 1)).Select(v => Ui.KV(v.ToString(CultureInfo.InvariantCulture), v < names.Length ? names[v] : v + ": not known")),
                                           val.ToString(CultureInfo.InvariantCulture), 250);
                    cb.SelectionChanged += (o, ev) =>
                    {
                        long nv;
                        if (!long.TryParse(Ui.Value(cb), out nv) || nv == val) return;
                        if (Guard(() => M.Set(lk, field + "[" + i + "]", nv))) { AfterEdit(); Msg(field + "[" + i + "] = " + nv); }
                    };
                    return cb;
                };
                rows.Add(new object[]
                {
                    Ui.T(i + (i == l.Long("master") ? " (master)" : ""), Ui.Muted),
                    RefLink(e.Obj("controller"), "none"),
                    e.Obj("signal") != null ? (object)Ui.H(RefLink(e.Obj("signal")), Ui.Pad(Ui.T(e["signal_default"] != null ? "default " + Ui.Num(e["signal_default"]) : "", Ui.Muted, 11), 6, 0, 0, 0))
                                            : Ui.T("none", Ui.Muted, 11),
                    RefLink(e.Obj("mask"), "none"),
                    modeBox("UpdateModes", e.Long("update_mode"), EditorModel.LayerUpdateModes),
                    modeBox("UpdateRates", e.Long("time_mode"), EditorModel.LayerTimeModes),
                    Ui.T(e.Long("blend_mode") + " / " + e.Long("branch_in_mode"), Ui.Muted, 11),
                });
            }
            body.Children.Add(Ui.Table(new[] { "#", "controller", "weight (SignalAssets)", "blend mask", "UpdateModes", "time (UpdateRates)", "Blend / BranchIn modes" }, rows));
            body.Children.Add(Ui.Wrap("A layer whose weight is 1.5e-5 or less is not updated unless its UpdateMode is 1. BlendModes, BranchInModes and MasterEntryIndex are read " +
                                      "by the game but what they change is not known (they are 0 in every layer controller in the game). How a layer's blend mask is applied is not known.", Ui.Muted, 11));
        }

        // ================================================================== blend masks
        private void RenderMask()
        {
            JObject m = M.BlendMask(ObjKey);
            string mk = m.Str("key");
            if (m.Str("type") == "BlendMaskAsset")
            {
                body.Children.Add(Ui.H(Ui.T("weights of the slots of ", Ui.Muted, 11), RefLink(m.Obj("layout"), "no layout")));
                List<object[]> rows = new List<object[]>();
                List<JObject> slots = m.Arr("slots").Cast<JObject>().ToList();
                for (int i = 0; i < slots.Count; i++)
                {
                    int ix = i;
                    JObject sl = slots[i];
                    double v = sl.Dbl("value");
                    TextBox b = Ui.Box(Ui.Num(v), 60, true, "Enter to apply");
                    b.KeyDown += (o, e) =>
                    {
                        if (e.Key != Key.Enter) return;
                        e.Handled = true;
                        double nv;
                        if (!double.TryParse(b.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out nv)) { Msg("not a number: " + b.Text, true); return; }
                        if (Guard(() => M.Set(mk, "Values[" + ix + "]", nv))) { AfterEdit(); Msg("Values[" + ix + "] = " + Ui.Num(nv)); }
                    };
                    Border bar = new Border { Width = Math.Max(0, Math.Min(1, v)) * 160, Height = 8, Background = Ui.Accent, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center };
                    Border track = new Border { Width = 160, Height = 8, Background = Ui.Soft, Child = bar, VerticalAlignment = VerticalAlignment.Center };
                    rows.Add(new object[] { Ui.T(i.ToString(CultureInfo.InvariantCulture), Ui.Muted), Ui.T(sl.Str("name"), null, 12, false, true), b, track });
                }
                body.Children.Add(Ui.Table(new[] { "#", "slot", "value", "" }, rows));
                if (m.Long("values") > slots.Count)
                    body.Children.Add(Ui.Wrap("Values has " + m.Long("values") + " entries for " + slots.Count + " slots (the rest pad the list).", Ui.Muted, 11));
            }
            else
            {
                body.Children.Add(Ui.H(Ui.T("the layouts of ", Ui.Muted, 11), RefLink(m.Obj("hierarchy"), "no hierarchy")));
                List<object[]> rows = new List<object[]>();
                foreach (JObject l in m.Arr("layouts").Cast<JObject>())
                    rows.Add(new object[]
                    {
                        RefLink(l.Obj("layout")),
                        Ui.T(l.Long("slots") + " slots", Ui.Muted, 11),
                        l.Obj("mask") != null ? (object)RefLink(l.Obj("mask")) : (l["default"] != null ? (object)Ui.T("all " + Ui.Num(l["default"]), null, 12) : Ui.T("none", Ui.Muted, 11)),
                    });
                body.Children.Add(Ui.Table(new[] { "layout", "", "mask or default value" }, rows));
            }
            body.Children.Add(Ui.Wrap("A mask gives a weight per slot (channel) of the layout it names. How the game applies a mask when it blends is not known.", Ui.Muted, 11));
        }

        // ================================================================== steering
        private void RenderSteering()
        {
            JObject st = M.Steering(ObjKey);
            Ui.Form f = new Ui.Form(150);
            f.Row("subject", RefLink(st.Obj("subject"), "none"));
            f.Row("subject init", RefLink(st.Obj("subject_init"), "none"));
            f.Row("enabled by", RefLink(st.Obj("enabled"), "always"));
            f.Row("steering joint", RefLink(st.Obj("joint"), "none"));
            body.Children.Add(f.Grid);
            List<JObject> axes = st.Arr("axes").Cast<JObject>().ToList();
            List<object[]> rows = new List<object[]>();
            rows.Add(new object[] { Ui.T("asset", Ui.Muted, 11) }.Concat(axes.Select(a => (object)RefLink(a.Obj("asset"), "none"))).ToArray());
            foreach (string fn in st.Arr("fields").Cast<string>())
            {
                List<object> row = new List<object> { Ui.T(fn, null, 12, false, true) };
                foreach (JObject a in axes)
                {
                    object v = a.Obj("values") != null && a.Obj("values").ContainsKey(fn) ? a.Obj("values")[fn] : null;
                    JObject rj = v as JObject;
                    row.Add(rj != null ? (object)RefLink(rj) : Ui.T(v == null ? "—" : Ui.Num(v), v == null ? Ui.Muted : null, 12));
                }
                rows.Add(row.ToArray());
            }
            body.Children.Add(Ui.Table(new[] { "SteeringControllerAxisAsset" }.Concat(axes.Select(a => a.Str("axis"))).ToList(), rows));
            body.Children.Add(Ui.Wrap("Edit an axis in its own Fields tab (click its name). What the game does with the axis fields is not known yet.", Ui.Muted, 11));
        }
    }
}
