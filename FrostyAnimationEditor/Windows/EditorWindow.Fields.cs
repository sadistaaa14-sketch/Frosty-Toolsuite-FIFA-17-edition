using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using FrostyAnimationEditor.Core;

namespace FrostyAnimationEditor.Windows
{
    /// <summary>the Fields tab: the object's fields as an expandable tree with inline editing and list operations</summary>
    internal sealed partial class EditorWindow
    {
        private void RenderFields()
        {
            StackPanel box = new StackPanel();
            JObject tree = View.Obj("tree");
            foreach (object c in tree.Arr("c") ?? new List<object>()) box.Children.Add(FieldRow((JObject)c, 0, false));
            body.Children.Add(box);
            body.Children.Add(Ui.Pad(Ui.Wrap("Click a value to edit it (Enter applies, Esc cancels); ▸ opens structs and lists. Keys link to the object they name.", Ui.Muted, 11), 0, 10, 0, 0));
        }

        private static bool Openable(JObject n)
        {
            string k = n.Str("k");
            return k == "struct" || k == "list" || k == "fixed";
        }

        private static bool Editable(JObject n)
        {
            if (n.Bool("ro")) return false;
            switch (n.Str("k"))
            {
                case "key": case "string": case "float": case "int": case "bool": case "vector": case "array": return true;
                case "bytes": return n.Bool("edit");
                case "null": return n.Bool("str");
                default: return false;
            }
        }

        private static string Joined(JObject n)
        {
            List<object> v = n.Arr("v");
            return v == null ? "" : string.Join(", ", v.Select(Ui.Num));
        }

        private UIElement FmtVal(JObject n)
        {
            string k = n.Str("k");
            switch (k)
            {
                case "null": return Ui.T("null", Ui.Muted);
                case "struct": return Ui.T("<" + n.Str("t") + ">", Ui.Muted);
                case "list": return Ui.T(Ui.Plural(n.Long("len"), "item"), Ui.Muted);
                case "fixed": return Ui.T(Ui.Plural(n.Long("len"), "element"), Ui.Muted);
                case "bytes":
                    {
                        string hx = n.Str("v") ?? "";
                        return Ui.Trim(Ui.T(n.Long("len") + " bytes " + (hx.Length > 48 ? hx.Substring(0, 48) + "…" : hx), Ui.Muted, 12, false, true));
                    }
                case "key":
                    {
                        StackPanel p = Ui.H(Ui.T(n.Str("v"), null, 12, false, true));
                        JObject r = n.Obj("r");
                        if (r != null)
                        {
                            bool ok = r.Str("state") == "ok";
                            string target = r.Str("key");
                            TextBlock l = ok ? Ui.Link(r.Str("type") + " " + Ui.Quote(r.Str("name")), () => Go(target), null, r.Str("type"))
                                             : Ui.T(r.Str("type") + " " + Ui.Quote(r.Str("name")), r.Str("state") == "imported" ? (Brush)Ui.Warn : Ui.Bad);
                            l.Margin = new Thickness(8, 0, 0, 0);
                            p.Children.Add(l);
                        }
                        return p;
                    }
                case "string": return Ui.Trim(Ui.T(Ui.Quote(n.Str("v")), null, 12, false, true));
                case "vector": case "array": return Ui.Trim(Ui.T(Joined(n), null, 12, false, true));
                default: return Ui.Trim(Ui.T(Ui.Num(n["v"]), null, 12, false, true));
            }
        }

        private static readonly Regex KeyRe = new Regex("^0x[0-9a-f]{1,16}$", RegexOptions.IgnoreCase);
        private static readonly Regex IntRe = new Regex("^-?(0x[0-9a-f]+|\\d+)$", RegexOptions.IgnoreCase);
        private static readonly Regex TrueRe = new Regex("^(1|true|yes|on)$", RegexOptions.IgnoreCase);

        /// <summary>the typed text as the value the model takes for this field</summary>
        private static object ParseInput(JObject n, string text)
        {
            text = text.Trim();
            switch (n.Str("k"))
            {
                case "key":
                    if (text.Length == 0 || text == "0") return "0x0000000000000000";
                    if (!KeyRe.IsMatch(text)) throw new FormatException("a key is 0x + up to 16 hex digits");
                    return text;
                case "string": case "null": return text;
                case "float":
                    {
                        string l = text.ToLowerInvariant();
                        if (l == "nan" || l == "inf" || l == "-inf") return l;
                        double v;
                        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out v) || double.IsInfinity(v) || double.IsNaN(v)) throw new FormatException("not a number");
                        return v;
                    }
                case "int":
                    {
                        if (!IntRe.IsMatch(text)) throw new FormatException("not an integer");
                        bool neg = text.StartsWith("-");
                        string t = neg ? text.Substring(1) : text;
                        decimal d = t.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? (decimal)Convert.ToUInt64(t.Substring(2), 16) : decimal.Parse(t, CultureInfo.InvariantCulture);
                        if (neg) d = -d;
                        if (d > ulong.MaxValue || d < long.MinValue) throw new FormatException("the number is out of range");
                        if (d > long.MaxValue) return (ulong)d;
                        return (long)d;
                    }
                case "bool": return TrueRe.IsMatch(text);
                case "vector": case "array":
                    return Ui.Numbers(text).Cast<object>().ToList();
                case "bytes": return Regex.Replace(text, "\\s+", "");
                default: return text;
            }
        }

        private string EditText(JObject n)
        {
            switch (n.Str("k"))
            {
                case "null": return "";
                case "vector": case "array": return Joined(n);
                case "string": return n.Str("v") ?? "";
                case "bytes": return n.Str("v") ?? "";
                default: return Ui.Num(n["v"]);
            }
        }

        private void SetVal(JObject n, object v)
        {
            string path = n.Str("p");
            if (Guard(() => M.Set(ObjKey, path, v)))
            {
                AfterEdit();
                Msg("set " + path);
            }
        }

        private void ListOp(string path, string op, int? index = null)
        {
            if (Guard(() => M.ListOp(ObjKey, path, op, index)))
            {
                AfterEdit();
                Msg(op + " " + path + (index.HasValue ? "[" + index.Value + "]" : ""));
            }
        }

        private UIElement FieldRow(JObject n, int depth, bool inFixed)
        {
            StackPanel wrap = new StackPanel();
            string path = n.Str("p");
            string kind = n.Str("k");
            bool openable = Openable(n);
            string openKey = ObjKey + "|" + path;
            bool isOpen = open.Contains(openKey);

            Grid r = new Grid { MinHeight = 22, Background = Brushes.Transparent };
            r.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(depth * 16 + 16) });
            r.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(190) });
            r.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            r.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
            r.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            TextBlock ex = Ui.T(openable ? (isOpen ? "▾" : "▸") : "", Ui.Muted);
            ex.HorizontalAlignment = HorizontalAlignment.Right;
            ex.Margin = new Thickness(0, 0, 4, 0);
            TextBlock name = Ui.Trim(Ui.T(n.Str("n")));
            name.ToolTip = path;
            Border val = new Border { Child = FmtVal(n), Padding = new Thickness(0, 1, 6, 1), VerticalAlignment = VerticalAlignment.Center, Background = Brushes.Transparent };
            bool editable = Editable(n);
            if (editable) val.Cursor = Cursors.IBeam;
            TextBlock ty = Ui.Trim(Ui.T(n.Str("t"), Ui.Muted, 11));
            ty.HorizontalAlignment = HorizontalAlignment.Right;
            ty.ToolTip = n.Str("t");
            ty.Margin = new Thickness(0, 0, 6, 0);
            StackPanel acts = new StackPanel { Orientation = Orientation.Horizontal, Visibility = Visibility.Hidden };
            Grid.SetColumn(ex, 0);
            Grid.SetColumn(name, 1);
            Grid.SetColumn(val, 2);
            Grid.SetColumn(ty, 3);
            Grid.SetColumn(acts, 4);
            r.Children.Add(ex);
            r.Children.Add(name);
            r.Children.Add(val);
            r.Children.Add(ty);
            r.Children.Add(acts);
            Border line = new Border { Child = r, BorderBrush = Ui.Soft, BorderThickness = new Thickness(0, 0, 0, 1) };
            line.MouseEnter += (s, e) => { acts.Visibility = Visibility.Visible; r.Background = Ui.Hover; };
            line.MouseLeave += (s, e) => { acts.Visibility = Visibility.Hidden; r.Background = Brushes.Transparent; };
            wrap.Children.Add(line);
            StackPanel sub = new StackPanel();
            wrap.Children.Add(sub);

            Action toggle = () =>
            {
                if (open.Contains(openKey))
                {
                    open.Remove(openKey);
                    sub.Children.Clear();
                    ex.Text = "▸";
                    return;
                }
                open.Add(openKey);
                ex.Text = "▾";
                FillNode(sub, n, depth, 0);
            };
            if (openable)
            {
                ex.Cursor = Cursors.Hand;
                name.Cursor = Cursors.Hand;
                ex.MouseLeftButtonUp += (s, e) => toggle();
                name.MouseLeftButtonUp += (s, e) => toggle();
                if (isOpen) FillNode(sub, n, depth, 0);
            }

            // inline edit
            if (editable)
                val.MouseLeftButtonUp += (s, e) =>
                {
                    if (val.Child is TextBox) return;
                    if (kind == "bool") { SetVal(n, !n.Bool("v")); return; }
                    TextBox inp = Ui.Box(EditText(n), double.NaN, true);
                    inp.Margin = new Thickness(0);
                    UIElement shown = val.Child;
                    val.Child = inp;
                    bool done = false;
                    Action restore = () => { if (done) return; done = true; val.Child = FmtVal(n); };
                    inp.KeyDown += (s2, e2) =>
                    {
                        if (e2.Key == Key.Escape) { e2.Handled = true; restore(); }
                        else if (e2.Key == Key.Enter)
                        {
                            e2.Handled = true;
                            object v;
                            try { v = ParseInput(n, inp.Text); }
                            catch (FormatException fe) { Msg(fe.Message, true); return; }
                            done = true;
                            SetVal(n, v);
                        }
                    };
                    inp.LostKeyboardFocus += (s2, e2) => restore();
                    inp.Loaded += (s2, e2) => { inp.Focus(); inp.SelectAll(); };
                };

            // actions
            bool inList = path.EndsWith("]") && !inFixed;
            if (kind == "key" && !n.Bool("ro")) acts.Children.Add(Ui.B("choose…", () => PickKey(n), "choose the object", true));
            if (kind == "list" || (kind == "bytes" && n.Bool("edit")))
            {
                acts.Children.Add(Ui.B("+ add", () => ListOp(path, "append"), "append an element (a copy of the last one)", true));
                if (n.Long("len") > 0)
                    acts.Children.Add(Ui.B("clear", () => { if (Dialog.Confirm(this, "Clear", "Remove all " + n.Long("len") + " elements of " + path + "?")) ListOp(path, "clear"); }, "remove all elements", true));
            }
            if (inList)
            {
                int lb = path.LastIndexOf('[');
                string lp = path.Substring(0, lb);
                int idx = int.Parse(path.Substring(lb + 1, path.Length - lb - 2), CultureInfo.InvariantCulture);
                acts.Children.Add(Ui.B("↑", () => ListOp(lp, "up", idx), "move up", true));
                acts.Children.Add(Ui.B("↓", () => ListOp(lp, "down", idx), "move down", true));
                acts.Children.Add(Ui.B("dup", () => ListOp(lp, "insert", idx), "insert a copy before this one", true));
                acts.Children.Add(Ui.B("×", () => ListOp(lp, "remove", idx), "remove", true));
            }
            if (kind == "null" && n.Bool("ref")) acts.Children.Add(Ui.B("+ new", () => NewRefDialog(n), "create an embedded object here", true));
            if (kind == "struct" && n.Bool("obj") && path.Length > 0 && !inList)
                acts.Children.Add(Ui.B("null", () => { if (Guard(() => M.SetNull(ObjKey, path))) { AfterEdit(); Msg("cleared " + path); } }, "set to null", true));
            return wrap;
        }

        private void FillNode(StackPanel sub, JObject n, int depth, int offset)
        {
            JObject r;
            try { r = M.NodeView(ObjKey, n.Str("p"), offset); }
            catch (Exception e) when (IsUserError(e)) { Report(e); return; }
            if (offset == 0) sub.Children.Clear();
            else if (sub.Children.Count > 0 && (sub.Children[sub.Children.Count - 1] as FrameworkElement).Tag as string == "more") sub.Children.RemoveAt(sub.Children.Count - 1);
            bool fixedArr = r.Str("k") == "fixed";
            foreach (object c in r.Arr("c") ?? new List<object>()) sub.Children.Add(FieldRow((JObject)c, depth + 1, fixedArr));
            List<object> items = r.Arr("c") ?? new List<object>();
            if (r.Str("k") == "list" && r.Long("len") > r.Long("off") + items.Count)
            {
                int next = (int)r.Long("off") + items.Count;
                long rest = r.Long("len") - next;
                TextBlock l = Ui.Link("show " + Math.Min(100, rest) + " more of " + rest + "…", () => FillNode(sub, n, depth, next));
                l.Margin = new Thickness((depth + 1) * 16 + 16, 3, 0, 3);
                l.Tag = "more";
                sub.Children.Add(l);
            }
        }

        private void PickKey(JObject n)
        {
            JObject cur = n.Obj("r");
            string curType = cur != null && cur.Str("state") == "ok" ? cur.Str("type") : null;
            Dialog d = new Dialog(this, "Choose the object for " + n.Str("n"), 700);
            TextBox manual = Ui.Box(n.Str("v"), double.NaN, true);
            Ui.Form f = new Ui.Form(60);
            f.Row("key", manual);
            ObjectPicker pk = new ObjectPicker(M, curType);
            object chosen = null;
            pk.Picked += o =>
            {
                chosen = o.Str("key");
                d.Accepted = true;
                d.Close();
            };
            d.Add(f.Grid, Ui.Wrap("or pick an object:", Ui.Muted, 11), pk.Panel);
            d.Button("Cancel");
            d.Button("Set key", () => { chosen = ParseInput(n, manual.Text); return true; }, true);
            d.ShowDialog();
            if (chosen != null) SetVal(n, chosen);
        }

        private void NewRefDialog(JObject n)
        {
            Dialog d = new Dialog(this, "New embedded object at " + n.Str("p"), 560);
            ComboBox cls = new ComboBox { IsEditable = true, Margin = new Thickness(2) };
            foreach (JObject t in M.Types(true).Cast<JObject>().OrderBy(x => x.Str("name"), StringComparer.OrdinalIgnoreCase)) cls.Items.Add(t.Str("name"));
            cls.Text = n.Str("t");
            Ui.Form f = new Ui.Form(60);
            f.Row("class", cls);
            d.Add(f.Grid, Ui.Wrap("The declared type is " + n.Str("t") + "; a derived class can be chosen instead. The new object's fields are zero / empty, with its base-class parts.", Ui.Muted, 11));
            string type = null;
            d.Button("Cancel");
            d.Button("Create", () => { type = cls.Text.Trim(); return true; }, true);
            if (!d.Run()) return;
            string path = n.Str("p");
            if (Guard(() => M.NewRef(ObjKey, path, type == n.Str("t") ? "" : type)))
            {
                open.Add(ObjKey + "|" + path);
                AfterEdit();
                Msg("new " + (string.IsNullOrEmpty(type) ? n.Str("t") : type) + " at " + path);
            }
        }
    }
}
