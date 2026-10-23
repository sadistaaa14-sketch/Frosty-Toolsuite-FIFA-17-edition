using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using FrostyAnimationEditor.Core;

namespace FrostyAnimationEditor.Windows
{
    /// <summary>small builders for the code-built UI; colours come from Frosty's theme where it has them</summary>
    internal static class Ui
    {
        private static Brush Res(string key, Color fallback)
        {
            object r = null;
            try { r = Application.Current != null ? Application.Current.TryFindResource(key) : null; }
            catch (Exception) { }
            if (r is Brush) return (Brush)r;
            if (r is Color) return Frozen((Color)r);
            return Frozen(fallback);
        }

        public static SolidColorBrush Frozen(Color c)
        {
            SolidColorBrush b = new SolidColorBrush(c);
            b.Freeze();
            return b;
        }

        public static Color C(byte r, byte g, byte b) { return Color.FromRgb(r, g, b); }

        public static Brush Fg { get { return Res("FontColor", C(0xF0, 0xF0, 0xF0)); } }
        public static Brush Back { get { return Res("WindowBackground", C(0x1C, 0x1C, 0x1E)); } }
        public static Brush Panel { get { return Res("ControlBackground", C(0x26, 0x26, 0x2A)); } }
        public static Brush ListBack { get { return Res("ListBackground", C(0x20, 0x20, 0x23)); } }
        public static readonly SolidColorBrush Muted = Frozen(C(0x9A, 0x9A, 0xA0));
        public static readonly SolidColorBrush Line = Frozen(C(0x44, 0x44, 0x4A));
        public static readonly SolidColorBrush Soft = Frozen(C(0x2E, 0x2E, 0x33));
        public static readonly SolidColorBrush Accent = Frozen(C(0x7A, 0xA7, 0xFF));
        public static readonly SolidColorBrush Bad = Frozen(C(0xFF, 0x8A, 0x80));
        public static readonly SolidColorBrush Good = Frozen(C(0x81, 0xC7, 0x84));
        public static readonly SolidColorBrush Warn = Frozen(C(0xE0, 0xA0, 0x40));
        public static readonly SolidColorBrush Sel = Frozen(C(0x24, 0x35, 0x52));
        public static readonly SolidColorBrush Hover = Frozen(C(0x2A, 0x2D, 0x34));
        public static readonly FontFamily Mono = new FontFamily("Consolas");

        // ------------------------------------------------------------------ text
        public static TextBlock T(string text, Brush fg = null, double size = 12, bool bold = false, bool mono = false, string tip = null)
        {
            TextBlock t = new TextBlock { Text = text ?? "", Foreground = fg ?? Fg, FontSize = size, VerticalAlignment = VerticalAlignment.Center };
            if (bold) t.FontWeight = FontWeights.SemiBold;
            if (mono) t.FontFamily = Mono;
            if (tip != null) t.ToolTip = tip;
            return t;
        }

        public static TextBlock Wrap(string text, Brush fg = null, double size = 12, bool mono = false)
        {
            TextBlock t = T(text, fg, size, false, mono);
            t.TextWrapping = TextWrapping.Wrap;
            t.Margin = new Thickness(0, 2, 0, 2);
            return t;
        }

        public static TextBlock Trim(TextBlock t)
        {
            t.TextTrimming = TextTrimming.CharacterEllipsis;
            return t;
        }

        public static TextBlock Muted11(string text) { return T(text, Muted, 11); }

        public static TextBlock H3(string text)
        {
            TextBlock t = T(text, null, 13, true);
            t.Margin = new Thickness(0, 12, 0, 6);
            return t;
        }

        public static TextBlock Link(string text, Action click, Brush fg = null, string tip = null)
        {
            TextBlock t = T(text, fg ?? Accent, 12, false, false, tip);
            t.Cursor = Cursors.Hand;
            t.MouseEnter += (s, e) => t.TextDecorations = TextDecorations.Underline;
            t.MouseLeave += (s, e) => t.TextDecorations = null;
            t.MouseLeftButtonUp += (s, e) => { e.Handled = true; if (click != null) click(); };
            return t;
        }

        // ------------------------------------------------------------------ controls
        public static Button B(string text, Action click, string tip = null, bool small = false)
        {
            Button b = new Button { Content = text, Margin = new Thickness(2), Padding = small ? new Thickness(5, 0, 5, 0) : new Thickness(8, 2, 8, 2), MinWidth = small ? 0 : 24 };
            if (small) b.FontSize = 11;
            if (tip != null) b.ToolTip = tip;
            if (click != null) b.Click += (s, e) => click();
            return b;
        }

        public static TextBox Box(string text = "", double width = double.NaN, bool mono = false, string tip = null)
        {
            TextBox t = new TextBox { Text = text ?? "", Margin = new Thickness(2), Padding = new Thickness(3, 1, 3, 1), VerticalContentAlignment = VerticalAlignment.Center };
            if (!double.IsNaN(width)) t.Width = width;
            if (mono) t.FontFamily = Mono;
            if (tip != null) t.ToolTip = tip;
            return t;
        }

        public static TextBox Area(string text, int rows, bool mono = true)
        {
            TextBox t = new TextBox
            {
                Text = text ?? "", AcceptsReturn = true, AcceptsTab = true, TextWrapping = TextWrapping.NoWrap,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                Height = rows * 15 + 8, Margin = new Thickness(2), Padding = new Thickness(3)
            };
            if (mono) { t.FontFamily = Mono; t.FontSize = 12; }
            return t;
        }

        public static CheckBox Check(string text, bool on, string tip = null)
        {
            CheckBox c = new CheckBox { Content = text, IsChecked = on, Margin = new Thickness(4, 2, 4, 2), VerticalAlignment = VerticalAlignment.Center, Foreground = Fg };
            if (tip != null) c.ToolTip = tip;
            return c;
        }

        public static bool On(CheckBox c) { return c.IsChecked == true; }

        /// <summary>a drop-down of (value, label) pairs</summary>
        public static ComboBox Combo(IEnumerable<KeyValuePair<string, string>> items, string selected, double width = double.NaN)
        {
            ComboBox c = new ComboBox { Margin = new Thickness(2), MinWidth = 80 };
            if (!double.IsNaN(width)) c.Width = width;
            foreach (KeyValuePair<string, string> kv in items)
            {
                ComboBoxItem it = new ComboBoxItem { Content = kv.Value, Tag = kv.Key };
                c.Items.Add(it);
                if (kv.Key == selected) c.SelectedItem = it;
            }
            if (c.SelectedItem == null && c.Items.Count > 0) c.SelectedIndex = 0;
            return c;
        }

        public static KeyValuePair<string, string> KV(string k, string v) { return new KeyValuePair<string, string>(k, v); }

        public static string Value(ComboBox c)
        {
            ComboBoxItem it = c.SelectedItem as ComboBoxItem;
            return it != null ? it.Tag as string : null;
        }

        public static void Select(ComboBox c, string value)
        {
            foreach (object o in c.Items)
            {
                ComboBoxItem it = o as ComboBoxItem;
                if (it != null && (string)it.Tag == value) { c.SelectedItem = it; return; }
            }
        }

        // ------------------------------------------------------------------ layout
        public static StackPanel H(params object[] kids)
        {
            StackPanel p = new StackPanel { Orientation = Orientation.Horizontal };
            Add(p, kids);
            return p;
        }

        public static StackPanel V(params object[] kids)
        {
            StackPanel p = new StackPanel { Orientation = Orientation.Vertical };
            Add(p, kids);
            return p;
        }

        public static WrapPanel Flow(params object[] kids)
        {
            WrapPanel p = new WrapPanel { Orientation = Orientation.Horizontal };
            foreach (object k in kids)
            {
                UIElement e = Elem(k);
                if (e != null) p.Children.Add(e);
            }
            return p;
        }

        public static void Add(Panel p, params object[] kids)
        {
            foreach (object k in kids)
            {
                IEnumerable many = k as IEnumerable;
                if (many != null && !(k is string))
                {
                    foreach (object x in many) { UIElement e = Elem(x); if (e != null) p.Children.Add(e); }
                    continue;
                }
                UIElement el = Elem(k);
                if (el != null) p.Children.Add(el);
            }
        }

        private static UIElement Elem(object k)
        {
            if (k == null) return null;
            UIElement e = k as UIElement;
            if (e != null) return e;
            TextBlock t = T(Convert.ToString(k, CultureInfo.InvariantCulture));
            t.Margin = new Thickness(2, 0, 2, 0);
            return t;
        }

        public static FrameworkElement Pad(FrameworkElement e, double l, double t, double r, double b)
        {
            e.Margin = new Thickness(l, t, r, b);
            return e;
        }

        public static Border Card(UIElement child, Brush border = null)
        {
            return new Border
            {
                Child = child, BorderBrush = border ?? Line, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(3),
                Padding = new Thickness(8, 5, 8, 5), Margin = new Thickness(0, 4, 0, 4)
            };
        }

        public static Border Badge(string text)
        {
            return new Border
            {
                Child = T(text, Muted, 11), BorderBrush = Line, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(3),
                Padding = new Thickness(4, 0, 4, 0), Margin = new Thickness(4, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center
            };
        }

        /// <summary>a two-column form: label | control</summary>
        public sealed class Form
        {
            public readonly Grid Grid = new Grid { Margin = new Thickness(0, 2, 0, 2) };
            private readonly double labelWidth;

            public Form(double labelWidth = 150)
            {
                this.labelWidth = labelWidth;
                Grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(labelWidth) });
                Grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            }

            public UIElement Row(string label, UIElement control)
            {
                int r = Grid.RowDefinitions.Count;
                Grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                TextBlock l = T(label, Muted);
                l.Margin = new Thickness(0, 3, 8, 3);
                l.TextWrapping = TextWrapping.Wrap;
                Grid.SetRow(l, r);
                Grid.Children.Add(l);
                FrameworkElement fe = control as FrameworkElement;
                if (fe != null && fe.Margin == default(Thickness)) fe.Margin = new Thickness(0, 2, 0, 2);
                Grid.SetRow(control, r);
                Grid.SetColumn(control, 1);
                Grid.Children.Add(control);
                return l;
            }
        }

        /// <summary>a table with a header row; cells are strings or elements</summary>
        public static Grid Table(IList<string> header, IEnumerable<object[]> rows, IList<GridLength> widths = null)
        {
            Grid g = new Grid { Margin = new Thickness(0, 2, 0, 6) };
            int nc = header.Count;
            for (int c = 0; c < nc; c++)
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = widths != null && c < widths.Count ? widths[c] : (c == nc - 1 ? new GridLength(1, GridUnitType.Star) : GridLength.Auto) });
            int r = 0;
            g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            for (int c = 0; c < nc; c++)
            {
                TextBlock h = T(header[c], Muted, 11);
                h.Margin = new Thickness(0, 0, 12, 3);
                Grid.SetColumn(h, c);
                g.Children.Add(h);
            }
            foreach (object[] row in rows)
            {
                r++;
                g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                Border line = new Border { BorderBrush = Soft, BorderThickness = new Thickness(0, 1, 0, 0) };
                Grid.SetRow(line, r);
                Grid.SetColumnSpan(line, nc);
                g.Children.Add(line);
                for (int c = 0; c < nc && c < row.Length; c++)
                {
                    UIElement e = Elem(row[c]);
                    if (e == null) continue;
                    FrameworkElement fe = e as FrameworkElement;
                    if (fe != null) { fe.Margin = new Thickness(0, 3, 12, 3); fe.VerticalAlignment = VerticalAlignment.Top; }
                    Grid.SetRow(e, r);
                    Grid.SetColumn(e, c);
                    g.Children.Add(e);
                }
            }
            return g;
        }

        // ------------------------------------------------------------------ values
        /// <summary>number text as the editor shows it (shortest exact text for float32 values)</summary>
        public static string Num(object v)
        {
            if (v == null) return "null";
            if (v is float) return MiniJson.FloatText((float)v).Replace("Infinity", "inf").Replace("NaN", "nan");
            if (v is double)
            {
                double d = (double)v;
                if (double.IsNaN(d)) return "nan";
                if (double.IsInfinity(d)) return d > 0 ? "inf" : "-inf";
                return MiniJson.DoubleText(d);
            }
            if (v is bool) return (bool)v ? "true" : "false";
            return Convert.ToString(v, CultureInfo.InvariantCulture);
        }

        public static string Thousands(long v) { return v.ToString("#,0", CultureInfo.InvariantCulture); }

        public static string Quote(string s) { return PyFormat.JsonStr(s ?? ""); }

        public static string Plural(long n, string one, string many = null) { return n + " " + (n == 1 ? one : (many ?? one + "s")); }

        /// <summary>numbers separated by commas / spaces / semicolons</summary>
        public static List<double> Numbers(string text)
        {
            List<double> r = new List<double>();
            foreach (string part in (text ?? "").Split(new[] { ',', ' ', ';', '\t', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries))
            {
                double d;
                if (!double.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out d)) throw new FormatException("not a number: " + part);
                r.Add(d);
            }
            return r;
        }

        public static double ParseDouble(string s, string what)
        {
            double d;
            if (!double.TryParse((s ?? "").Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out d)) throw new FormatException(what + ": not a number");
            return d;
        }

        public static int ParseInt(string s, string what)
        {
            int d;
            if (!int.TryParse((s ?? "").Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out d)) throw new FormatException(what + ": not a whole number");
            return d;
        }
    }
}
