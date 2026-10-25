using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using FrostyAnimationEditor.Core;

namespace FrostyAnimationEditor.Windows
{
    /// <summary>
    /// A controller's tags on its timeline: a row per tag collection, a bar per tag from its start over its length
    /// (a diamond when the length is 0 or the tag has none), overlapping tags stacked in lanes. Click selects; drag a bar
    /// to move it, its right edge to change its length; double-click opens the tag. Shares the sequence view's gutter and
    /// scale so the two line up when stacked.
    /// </summary>
    internal sealed class TagView : FrameworkElement
    {
        public const double Gutter = 132;
        private const double RulerH = 24, LaneH = 20, RowPad = 4, EdgePx = 5;
        private readonly List<Row> rows = new List<Row>();
        private double length, maxEnd;
        private double scale = 2;
        private double playhead = -1;
        private bool ruler = true;
        private int selC = -1, selT = -1;

        private sealed class Row
        {
            public JObject Coll;
            public List<JObject> Tags;
            public List<int> Lane = new List<int>();
            public int Lanes = 1;
            public double Y, H;
        }

        private enum Mode { None, Move, Resize, Scrub }
        private Mode mode;
        private Point downAt;
        private double dragTicks;
        private bool dragging;
        private int dragC, dragT;

        public event Action<int, int> Selected;            // collection, tag (-1, -1: none)
        public event Action<int, int, double> Moved;       // collection, tag, delta ticks
        public event Action<int, int, double> Resized;     // collection, tag, new length (ticks)
        public event Action<int, int> Opened;
        public event Action<int, Point> CollectionMenu;    // collection, screen point
        public event Action<double> Scrubbed;

        public TagView()
        {
            ClipToBounds = true;
            Focusable = true;
            SnapsToDevicePixels = true;
        }

        public bool ShowRuler { get { return ruler; } set { ruler = value; Resize(); InvalidateVisual(); } }
        public double Scale { get { return scale; } }
        public int SelCollection { get { return selC; } }
        public int SelTag { get { return selT; } }

        public void SetTags(JObject view, double ownerLength, int keepC = -1, int keepT = -1)
        {
            rows.Clear();
            length = ownerLength;
            maxEnd = view.Dbl("max_end");
            foreach (JObject c in view.Arr("collections").Cast<JObject>())
            {
                Row r = new Row { Coll = c, Tags = c.Arr("tags").Cast<JObject>().ToList() };
                // lanes: greedy by start, a tag goes in the first lane whose last tag ended before it
                List<double> laneEnd = new List<double>();
                List<int> order = Enumerable.Range(0, r.Tags.Count).OrderBy(i => r.Tags[i].Dbl("start")).ToList();
                for (int i = 0; i < r.Tags.Count; i++) r.Lane.Add(0);
                foreach (int i in order)
                {
                    double s = r.Tags[i].Dbl("start"), e = s + Math.Max(r.Tags[i].Dbl("length"), 0);
                    double minPx = 70 / Math.Max(0.2, scale);      // room for a label
                    int lane = laneEnd.FindIndex(x => x <= s);
                    if (lane < 0) { lane = laneEnd.Count; laneEnd.Add(0); }
                    laneEnd[lane] = Math.Max(e, s + minPx);
                    r.Lane[i] = lane;
                }
                r.Lanes = Math.Max(1, laneEnd.Count);
                rows.Add(r);
            }
            selC = keepC; selT = keepT;
            if (selC >= rows.Count || (selC >= 0 && selT >= rows[selC].Tags.Count)) { selC = -1; selT = -1; }
            Resize();
            InvalidateVisual();
        }

        public double Span { get { return Math.Max(Math.Max(length, maxEnd), 30); } }

        public void SetScale(double s, double span = 0)
        {
            scale = Math.Max(0.2, Math.Min(16, s));
            spanOverride = span;
            Resize();
            InvalidateVisual();
        }

        private double spanOverride;

        public void FitTo(double width)
        {
            scale = Math.Max(0.2, Math.Min(12, (width - Gutter - 30) / Span));
            Resize();
            InvalidateVisual();
        }

        public void SetPlayhead(double t) { playhead = t; InvalidateVisual(); }

        private double Top { get { return ruler ? RulerH : 0; } }

        private void Resize()
        {
            double y = Top;
            foreach (Row r in rows)
            {
                r.Y = y;
                r.H = r.Lanes * LaneH + 2 * RowPad;
                y += r.H;
            }
            Width = Gutter + Math.Max(Span, spanOverride) * scale + 40;
            Height = y + (rows.Count == 0 ? 30 : 6);
        }

        private double X(double ticks) { return Gutter + ticks * scale; }
        private double Ticks(double x) { return (x - Gutter) / scale; }

        private static string Short(string type)
        {
            string s = type;
            if (s.EndsWith("Asset", StringComparison.Ordinal)) s = s.Substring(0, s.Length - 5);
            if (s.EndsWith("Tag", StringComparison.Ordinal) && s.Length > 3) s = s.Substring(0, s.Length - 3);
            return s;
        }

        private static string CollLabel(JObject c)
        {
            string t = c.Str("type") ?? "";
            if (t.EndsWith("TagCollectionAsset", StringComparison.Ordinal)) t = t.Substring(0, t.Length - "TagCollectionAsset".Length);
            return (t.Length == 0 ? "Tags" : t) + " (" + c.Arr("tags").Count + ")";
        }

        // category colours (the bar's edge): branch windows, audio, touch / IK, game state, the rest
        private static readonly SolidColorBrush CBranch = Ui.Frozen(Ui.C(0x7A, 0xA7, 0xFF)), CAudio = Ui.Frozen(Ui.C(0x81, 0xC7, 0x84)),
            CTouch = Ui.Frozen(Ui.C(0xE0, 0xA0, 0x40)), CState = Ui.Frozen(Ui.C(0xC0, 0x90, 0xE0)), COther = Ui.Frozen(Ui.C(0x9A, 0x9A, 0xA0)),
            Bar = Ui.Frozen(Ui.C(0x33, 0x3A, 0x48)), BarSel = Ui.Frozen(Ui.C(0x2F, 0x4A, 0x78)), RowAlt = Ui.Frozen(Ui.C(0x22, 0x22, 0x26));

        private static Brush Category(string type)
        {
            if (type.Contains("Branch")) return CBranch;
            if (type.Contains("Audio") || type == "PATag") return CAudio;
            if (type.Contains("Touch") || type.Contains("IK") || type.Contains("Collision") || type.Contains("Tackle") || type.Contains("Ball")) return CTouch;
            if (type.Contains("GameState") || type.Contains("Query") || type.Contains("Result")) return CState;
            return COther;
        }

        private Rect TagRect(Row r, int i)
        {
            JObject t = r.Tags[i];
            double s = t.Dbl("start"), l = t.Bool("window") ? t.Dbl("length") : 0;
            if (dragging && rows.IndexOf(r) == dragC && i == dragT)
            {
                if (mode == Mode.Move) s = Math.Max(0, s + dragTicks);
                else if (mode == Mode.Resize) l = Math.Max(0, l + dragTicks);
            }
            double y = r.Y + RowPad + r.Lane[i] * LaneH;
            if (l * scale < 3) return new Rect(X(s) - 5, y + 2, 10, LaneH - 4);     // a point: drawn as a diamond
            return new Rect(X(s), y + 1, l * scale, LaneH - 3);
        }

        protected override void OnRender(DrawingContext dc)
        {
            double w = ActualWidth, h = ActualHeight;
            dc.DrawRectangle(Ui.ListBack, null, new Rect(0, 0, w, h));
            if (rows.Count == 0)
            {
                dc.DrawText(Draw.Text("no tags", 11, Ui.Muted, this), new Point(8, Top + 6));
                return;
            }
            for (int c = 0; c < rows.Count; c++)
            {
                Row r = rows[c];
                if (c % 2 == 1) dc.DrawRectangle(RowAlt, null, new Rect(0, r.Y, w, r.H));
                FormattedText ft = Draw.Text(CollLabel(r.Coll), 11, Ui.Fg, this);
                ft.MaxTextWidth = Gutter - 12;
                ft.MaxLineCount = 1;
                ft.Trimming = TextTrimming.CharacterEllipsis;
                dc.DrawText(ft, new Point(8, r.Y + RowPad + 2));
            }
            dc.DrawLine(Draw.Pen(Ui.Line, 1), new Point(Gutter - 0.5, 0), new Point(Gutter - 0.5, h));
            double span = Math.Max(Span, spanOverride);
            double step = new[] { 6.0, 12, 30, 60, 120, 300, 600 }.First(s => s * scale >= 46 || s == 600);
            Pen tick = Draw.Pen(Ui.Soft, 1);
            for (double tk = 0; tk <= span + step; tk += step)
            {
                double x = X(tk);
                dc.DrawLine(tick, new Point(x, ruler ? RulerH - 6 : 0), new Point(x, h));
                if (ruler)
                {
                    string lab = tk.ToString("0", CultureInfo.InvariantCulture) + (tk % 60 == 0 ? "  " + (tk / 60).ToString("0.#", CultureInfo.InvariantCulture) + "s" : "");
                    dc.DrawText(Draw.Text(lab, 10, Ui.Muted, this), new Point(x + 2, 4));
                }
            }
            if (length > 0)
            {
                Pen lp = Draw.Pen(Ui.Good, 1.2, true);
                dc.DrawLine(lp, new Point(X(length), 0), new Point(X(length), h));
            }
            for (int c = 0; c < rows.Count; c++)
            {
                Row r = rows[c];
                for (int i = 0; i < r.Tags.Count; i++)
                {
                    JObject t = r.Tags[i];
                    Rect rc = TagRect(r, i);
                    bool sel = c == selC && i == selT;
                    Brush cat = Category(t.Str("type") ?? "");
                    bool point = rc.Width <= 10.5 && (!t.Bool("window") || t.Dbl("length") * scale < 3);
                    if (point)
                    {
                        double cx = rc.X + rc.Width / 2, cy = rc.Y + rc.Height / 2, rr = rc.Height / 2;
                        StreamGeometry g = new StreamGeometry();
                        using (StreamGeometryContext gc = g.Open())
                        {
                            gc.BeginFigure(new Point(cx, cy - rr), true, true);
                            gc.LineTo(new Point(cx + rr * 0.8, cy), true, false);
                            gc.LineTo(new Point(cx, cy + rr), true, false);
                            gc.LineTo(new Point(cx - rr * 0.8, cy), true, false);
                        }
                        g.Freeze();
                        dc.DrawGeometry(sel ? Ui.Accent : cat, sel ? Draw.Pen(Ui.Fg, 1) : null, g);
                        FormattedText lt = Draw.Text(Short(t.Str("type") ?? ""), 10, sel ? Ui.Fg : Ui.Muted, this);
                        dc.DrawText(lt, new Point(cx + 7, rc.Y + 1));
                    }
                    else
                    {
                        dc.DrawRoundedRectangle(sel ? BarSel : Bar, Draw.Pen(sel ? Ui.Accent : cat, sel ? 1.5 : 1), rc, 2, 2);
                        dc.DrawRectangle(cat, null, new Rect(rc.X, rc.Y, Math.Min(3, rc.Width), rc.Height));
                        FormattedText lt = Draw.Text(Short(t.Str("type") ?? "") + (string.IsNullOrEmpty(t.Str("summary")) ? "" : "  " + t.Str("summary")), 10, Ui.Fg, this);
                        lt.MaxTextWidth = Math.Max(1, rc.Width - 8);
                        lt.MaxLineCount = 1;
                        lt.Trimming = TextTrimming.CharacterEllipsis;
                        dc.PushClip(new RectangleGeometry(rc));
                        dc.DrawText(lt, new Point(rc.X + 5, rc.Y + 2));
                        dc.Pop();
                    }
                    if (t.Long("listed") > 1) dc.DrawText(Draw.Text("×" + t.Long("listed"), 9, Ui.Warn, this), new Point(rc.Right + 2, rc.Y + 3));
                }
            }
            if (playhead >= 0) dc.DrawLine(Draw.Pen(Ui.Bad, 1.5), new Point(X(playhead), 0), new Point(X(playhead), h));
            if (dragging)
            {
                string txt = mode == Mode.Move ? (dragTicks >= 0 ? "+" : "") + dragTicks.ToString("0", CultureInfo.InvariantCulture) + " ticks"
                                               : "length " + Math.Max(0, rows[dragC].Tags[dragT].Dbl("length") + dragTicks).ToString("0", CultureInfo.InvariantCulture);
                FormattedText d = Draw.Text(txt, 11, Ui.Fg, this);
                dc.DrawRectangle(Ui.Panel, null, new Rect(downAt.X + 10, downAt.Y - 22, d.Width + 8, d.Height + 2));
                dc.DrawText(d, new Point(downAt.X + 14, downAt.Y - 21));
            }
        }

        private bool Hit(Point p, out int c, out int t, out Mode zone)
        {
            c = -1; t = -1; zone = Mode.None;
            for (int ci = 0; ci < rows.Count; ci++)
            {
                Row r = rows[ci];
                if (p.Y < r.Y || p.Y >= r.Y + r.H) continue;
                c = ci;
                if (p.X < Gutter) return false;
                for (int i = r.Tags.Count - 1; i >= 0; i--)
                {
                    Rect rc = TagRect(r, i);
                    Rect hit = rc.Width < 10 ? new Rect(rc.X - 3, rc.Y, rc.Width + 6, rc.Height) : rc;
                    if (!hit.Contains(p)) continue;
                    t = i;
                    zone = r.Tags[i].Bool("window") && rc.Width >= 10 && rc.Right - p.X <= EdgePx ? Mode.Resize : Mode.Move;
                    return true;
                }
                return false;
            }
            return false;
        }

        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            Focus();
            Point p = e.GetPosition(this);
            if (ruler && p.Y < RulerH && p.X >= Gutter)
            {
                mode = Mode.Scrub;
                CaptureMouse();
                Scrub(p.X);
                e.Handled = true;
                return;
            }
            int c, t;
            Mode z;
            if (Hit(p, out c, out t, out z))
            {
                selC = c; selT = t;
                if (Selected != null) Selected(c, t);
                if (e.ClickCount == 2) { if (Opened != null) Opened(c, t); e.Handled = true; return; }
                mode = z; downAt = p; dragTicks = 0; dragC = c; dragT = t; dragging = false;
                CaptureMouse();
            }
            else
            {
                selC = -1; selT = -1;
                if (Selected != null) Selected(-1, -1);
            }
            InvalidateVisual();
            e.Handled = true;
        }

        private void Scrub(double x)
        {
            double t = Math.Max(0, Math.Round(Ticks(x)));
            if (t == playhead) return;
            playhead = t;
            InvalidateVisual();
            if (Scrubbed != null) Scrubbed(t);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            Point p = e.GetPosition(this);
            if (mode == Mode.Scrub && IsMouseCaptured) { Scrub(p.X); return; }
            if (mode != Mode.None && IsMouseCaptured)
            {
                if (!dragging && Math.Abs(p.X - downAt.X) < 3) return;
                dragging = true;
                dragTicks = Math.Round((p.X - downAt.X) / scale);
                InvalidateVisual();
                return;
            }
            int c, t;
            Mode z;
            Cursor = Hit(p, out c, out t, out z) ? (z == Mode.Resize ? Cursors.SizeWE : Cursors.SizeAll) : (ruler && p.Y < RulerH && p.X >= Gutter ? Cursors.Hand : Cursors.Arrow);
        }

        protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
        {
            if (!IsMouseCaptured) return;
            ReleaseMouseCapture();
            Mode m = mode;
            mode = Mode.None;
            if (m == Mode.Scrub) return;
            if (!dragging || dragTicks == 0) { dragging = false; InvalidateVisual(); return; }
            dragging = false;
            if (dragC >= rows.Count || dragT >= rows[dragC].Tags.Count) return;
            if (m == Mode.Move) { if (Moved != null) Moved(dragC, dragT, dragTicks); }
            else if (m == Mode.Resize)
            {
                double nl = Math.Max(0, rows[dragC].Tags[dragT].Dbl("length") + dragTicks);
                if (Resized != null) Resized(dragC, dragT, nl);
            }
            InvalidateVisual();
        }

        protected override void OnMouseRightButtonUp(MouseButtonEventArgs e)
        {
            Point p = e.GetPosition(this);
            if (p.X >= Gutter) return;
            for (int c = 0; c < rows.Count; c++)
                if (p.Y >= rows[c].Y && p.Y < rows[c].Y + rows[c].H)
                {
                    if (CollectionMenu != null) CollectionMenu(c, PointToScreen(p));
                    e.Handled = true;
                    return;
                }
        }

        protected override void OnMouseWheel(MouseWheelEventArgs e)
        {
            if ((Keyboard.Modifiers & ModifierKeys.Control) == 0) return;
            scale = Math.Max(0.2, Math.Min(16, scale * (e.Delta > 0 ? 1.25 : 0.8)));
            Resize();
            InvalidateVisual();
            if (ScaleChanged != null) ScaleChanged(scale);
            e.Handled = true;
        }

        public event Action<double> ScaleChanged;
    }
}
