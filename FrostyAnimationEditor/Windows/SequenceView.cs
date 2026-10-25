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
    /// <summary>
    /// The timeline of one sequence actor: a row per track, a bar per entry from its begin (StartTime + StartInTime) to
    /// its end (StartTime + EndInTime), the blend-in at the start of each later entry, the sequence length and loop point.
    /// Click selects; drag the bar to move it (StartTime), its left / right edge to trim it (StartInTime / EndInTime);
    /// double-click opens the entry's controller. Ctrl+wheel zooms.
    /// </summary>
    internal sealed class SequenceView : FrameworkElement
    {
        private const double Gutter = 132, RulerH = 24, RowH = 38, EdgePx = 6;
        private JObject actor;
        private List<JObject> tracks = new List<JObject>();
        private double length, loopPoint, maxEnd;
        private double scale = 2;               // px per tick
        private int selTrack = -1, selIndex = -1;
        private double playhead = -1;

        // drag state
        private enum Mode { None, Move, TrimStart, TrimEnd, Scrub }
        private Mode mode;
        private Point downAt;
        private double dragTicks;
        private bool dragging;
        private int dragTrack, dragIndex;

        public event Action<int, int> Selected;              // track, index (-1, -1: none)
        public event Action<int, int, int> Moved;            // track, index, delta ticks
        public event Action<int, int, int, int> Trimmed;     // track, index, new start in, new end in
        public event Action<int, int> Opened;
        public event Action<double> Scrubbed;                // sequence time clicked or dragged on the ruler
        public event Action<int, Point> TrackMenu;           // track, screen point (right click on the gutter)

        public SequenceView()
        {
            ClipToBounds = true;
            Focusable = true;
            SnapsToDevicePixels = true;
        }

        public int SelTrack { get { return selTrack; } }
        public int SelIndex { get { return selIndex; } }

        public void SetActor(JObject a, int keepTrack = -1, int keepIndex = -1)
        {
            actor = a;
            tracks = a.Arr("tracks").Cast<JObject>().ToList();
            length = a.Dbl("length");
            loopPoint = a.Dbl("loop_point", -1);
            maxEnd = 0;
            foreach (JObject t in tracks)
                foreach (JObject e in t.Arr("entries").Cast<JObject>()) maxEnd = Math.Max(maxEnd, e.Long("end"));
            selTrack = keepTrack; selIndex = keepIndex;
            if (selTrack >= tracks.Count || (selTrack >= 0 && selIndex >= Entries(selTrack).Count)) { selTrack = -1; selIndex = -1; }
            Resize();
            InvalidateVisual();
        }

        public void FitTo(double width)
        {
            scale = Math.Max(0.2, Math.Min(12, (width - Gutter - 30) / Math.Max(Span, spanOverride)));
            Resize();
            InvalidateVisual();
        }

        /// <summary>the ticks the view spans (length, last entry end, at least 30)</summary>
        public double Span { get { return Math.Max(Math.Max(length, maxEnd), 30); } }
        public double Scale { get { return scale; } }
        private double spanOverride;

        /// <summary>px per tick, and the span to cover at least (to line up with another view)</summary>
        public void SetScale(double s, double span = 0)
        {
            scale = Math.Max(0.2, Math.Min(16, s));
            spanOverride = span;
            Resize();
            InvalidateVisual();
        }

        public event Action<double> ScaleChanged;

        public void SetPlayhead(double t) { playhead = t; InvalidateVisual(); }

        private void Scrub(double x)
        {
            double t = Math.Max(0, Math.Round(Ticks(x)));
            if (t == playhead) return;
            playhead = t;
            InvalidateVisual();
            if (Scrubbed != null) Scrubbed(t);
        }

        private List<JObject> Entries(int t) { return t >= 0 && t < tracks.Count ? tracks[t].Arr("entries").Cast<JObject>().ToList() : new List<JObject>(); }

        private void Resize()
        {
            double span = Math.Max(Span, spanOverride);
            Width = Gutter + span * scale + 40;
            Height = RulerH + Math.Max(1, tracks.Count) * RowH + 8;
        }

        private double X(double ticks) { return Gutter + ticks * scale; }
        private double Ticks(double x) { return (x - Gutter) / scale; }

        private static readonly SolidColorBrush Bar = Ui.Frozen(Ui.C(0x33, 0x3A, 0x48)), BarSel = Ui.Frozen(Ui.C(0x2F, 0x4A, 0x78)),
            BlendFill = Ui.Frozen(Color.FromArgb(0x70, 0x7A, 0xA7, 0xFF)), RowAlt = Ui.Frozen(Ui.C(0x22, 0x22, 0x26)), Ghost = Ui.Frozen(Color.FromArgb(0x60, 0xFF, 0xFF, 0xFF));

        protected override void OnRender(DrawingContext dc)
        {
            double w = ActualWidth, h = ActualHeight;
            dc.DrawRectangle(Ui.ListBack, null, new Rect(0, 0, w, h));
            if (actor == null) return;
            // rows
            for (int t = 0; t < tracks.Count; t++)
            {
                double y = RulerH + t * RowH;
                if (t % 2 == 1) dc.DrawRectangle(RowAlt, null, new Rect(0, y, w, RowH));
                List<JObject> es = Entries(t);
                string first = es.Count > 0 ? es[0].Str("label") : "empty";
                dc.DrawText(Draw.Text("Track " + t, 12, Ui.Fg, this), new Point(8, y + 4));
                FormattedText sub = Draw.Text(first, 10, Ui.Muted, this);
                sub.MaxTextWidth = Gutter - 14;
                sub.MaxLineCount = 1;
                sub.Trimming = TextTrimming.CharacterEllipsis;
                dc.DrawText(sub, new Point(8, y + 20));
            }
            dc.DrawLine(Draw.Pen(Ui.Line, 1), new Point(Gutter - 0.5, 0), new Point(Gutter - 0.5, h));
            // ruler: ticks and seconds (60 ticks a second)
            double span = Math.Max(Span, spanOverride);
            double step = new[] { 6.0, 12, 30, 60, 120, 300, 600 }.First(s => s * scale >= 46 || s == 600);
            Pen tick = Draw.Pen(Ui.Line, 1);
            for (double tk = 0; tk <= span + step; tk += step)
            {
                double x = X(tk);
                dc.DrawLine(tick, new Point(x, RulerH - 6), new Point(x, h));
                string lab = tk.ToString("0", CultureInfo.InvariantCulture) + (tk % 60 == 0 ? "  " + (tk / 60).ToString("0.#", CultureInfo.InvariantCulture) + "s" : "");
                dc.DrawText(Draw.Text(lab, 10, Ui.Muted, this), new Point(x + 2, 4));
            }
            // entries
            for (int t = 0; t < tracks.Count; t++)
            {
                List<JObject> es = Entries(t);
                double y = RulerH + t * RowH + 4, bh = RowH - 8;
                for (int i = 0; i < es.Count; i++)
                {
                    JObject e = es[i];
                    double b = e.Long("begin"), en = e.Long("end");
                    if (dragging && t == dragTrack && i == dragIndex)
                    {
                        if (mode == Mode.Move) { b += dragTicks; en += dragTicks; }
                        else if (mode == Mode.TrimStart) b = Math.Min(en, b + dragTicks);
                        else if (mode == Mode.TrimEnd) en = Math.Max(b, en + dragTicks);
                    }
                    bool sel = t == selTrack && i == selIndex;
                    JObject clip = e.Obj("clip");
                    bool past = clip != null && clip.Bool("past_end");
                    Rect r = new Rect(X(b), y, Math.Max(2, (en - b) * scale), bh);
                    dc.DrawRoundedRectangle(sel ? BarSel : Bar, Draw.Pen(past ? Ui.Warn : (sel ? Ui.Accent : Ui.Line), sel || past ? 1.5 : 1), r, 3, 3);
                    double bt = e.Dbl("blend_time");
                    if (i > 0 && bt > 0)
                    {
                        Rect br = new Rect(X(b), y, Math.Min(bt, en - b) * scale, bh);
                        LinearGradientBrush g = new LinearGradientBrush(Color.FromArgb(0x90, 0x7A, 0xA7, 0xFF), Color.FromArgb(0x10, 0x7A, 0xA7, 0xFF), 0);
                        g.Freeze();
                        dc.DrawRectangle(g, null, br);
                    }
                    if (e.Bool("mirror")) dc.DrawText(Draw.Text("M", 9, Ui.Warn, this), new Point(r.Right - 10, r.Top + 1));
                    FormattedText ft = Draw.Text(e.Str("label"), 11, Ui.Fg, this);
                    ft.MaxTextWidth = Math.Max(1, r.Width - 8);
                    ft.MaxLineCount = 1;
                    ft.Trimming = TextTrimming.CharacterEllipsis;
                    dc.PushClip(new RectangleGeometry(r));
                    dc.DrawText(ft, new Point(r.X + 4, r.Y + 2));
                    if (clip != null)
                    {
                        string fr = "frames " + clip.Dbl("first_frame").ToString("0.#", CultureInfo.InvariantCulture) + "–" + clip.Dbl("last_frame").ToString("0.#", CultureInfo.InvariantCulture) +
                                    (past ? "  past the end" : "");
                        FormattedText f2 = Draw.Text(fr, 10, past ? Ui.Warn : Ui.Muted, this);
                        f2.MaxTextWidth = Math.Max(1, r.Width - 8);
                        f2.MaxLineCount = 1;
                        f2.Trimming = TextTrimming.CharacterEllipsis;
                        dc.DrawText(f2, new Point(r.X + 4, r.Y + 16));
                    }
                    dc.Pop();
                }
            }
            // length, loop point, playhead
            Pen lp = Draw.Pen(Ui.Good, 1.5);
            dc.DrawLine(lp, new Point(X(length), RulerH - 8), new Point(X(length), h));
            dc.DrawText(Draw.Text("length " + length.ToString("0", CultureInfo.InvariantCulture), 10, Ui.Good, this), new Point(X(length) + 3, RulerH - 12));
            if (loopPoint >= 0)
            {
                Pen lo = Draw.Pen(Ui.Warn, 1.2, true);
                dc.DrawLine(lo, new Point(X(loopPoint), RulerH - 8), new Point(X(loopPoint), h));
                dc.DrawText(Draw.Text("loop", 10, Ui.Warn, this), new Point(X(loopPoint) + 3, RulerH - 12));
            }
            if (playhead >= 0) dc.DrawLine(Draw.Pen(Ui.Bad, 1.5), new Point(X(playhead), 0), new Point(X(playhead), h));
            if (dragging)
            {
                FormattedText d = Draw.Text((dragTicks >= 0 ? "+" : "") + dragTicks.ToString("0", CultureInfo.InvariantCulture) + " ticks", 11, Ui.Fg, this);
                dc.DrawRectangle(Ui.Panel, null, new Rect(downAt.X + 10, downAt.Y - 22, d.Width + 8, d.Height + 2));
                dc.DrawText(d, new Point(downAt.X + 14, downAt.Y - 21));
            }
        }

        private bool Hit(Point p, out int track, out int index, out Mode zone)
        {
            track = -1; index = -1; zone = Mode.None;
            if (p.Y < RulerH || p.X < Gutter) return false;
            int t = (int)((p.Y - RulerH) / RowH);
            if (t < 0 || t >= tracks.Count) return false;
            List<JObject> es = Entries(t);
            // later entries are drawn on top: test them first
            for (int i = es.Count - 1; i >= 0; i--)
            {
                double x0 = X(es[i].Long("begin")), x1 = X(es[i].Long("end"));
                if (p.X < x0 - 2 || p.X > x1 + 2) continue;
                track = t; index = i;
                zone = p.X - x0 <= EdgePx ? Mode.TrimStart : (x1 - p.X <= EdgePx ? Mode.TrimEnd : Mode.Move);
                return true;
            }
            track = t;
            return false;
        }

        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            Focus();
            Point p = e.GetPosition(this);
            if (p.Y < RulerH && p.X >= Gutter)
            {
                mode = Mode.Scrub;
                CaptureMouse();
                Scrub(p.X);
                e.Handled = true;
                return;
            }
            int t, i;
            Mode z;
            if (Hit(p, out t, out i, out z))
            {
                selTrack = t; selIndex = i;
                if (Selected != null) Selected(t, i);
                if (e.ClickCount == 2) { if (Opened != null) Opened(t, i); e.Handled = true; return; }
                mode = z; downAt = p; dragTicks = 0; dragTrack = t; dragIndex = i; dragging = false;
                CaptureMouse();
            }
            else
            {
                selTrack = -1; selIndex = -1;
                if (Selected != null) Selected(-1, -1);
            }
            InvalidateVisual();
            e.Handled = true;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            Point p = e.GetPosition(this);
            if (mode == Mode.Scrub && IsMouseCaptured) { Scrub(p.X); return; }
            if (mode != Mode.None && IsMouseCaptured)
            {
                double d = (p.X - downAt.X) / scale;
                if (!dragging && Math.Abs(p.X - downAt.X) < 3) return;
                dragging = true;
                dragTicks = Math.Round(d);
                InvalidateVisual();
                return;
            }
            int t, i;
            Mode z;
            Cursor = Hit(p, out t, out i, out z) ? (z == Mode.Move ? Cursors.SizeAll : Cursors.SizeWE) : (p.Y < RulerH && p.X >= Gutter ? Cursors.Hand : Cursors.Arrow);
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
            int delta = (int)dragTicks;
            List<JObject> es = Entries(dragTrack);
            if (dragIndex >= es.Count) return;
            JObject en = es[dragIndex];
            if (m == Mode.Move) { if (Moved != null) Moved(dragTrack, dragIndex, delta); }
            else
            {
                int si = (int)en.Long("start_in"), ei = (int)en.Long("end_in");
                if (m == Mode.TrimStart) si = Math.Min(ei, si + delta);
                else ei = Math.Max(si, ei + delta);
                if (Trimmed != null) Trimmed(dragTrack, dragIndex, si, ei);
            }
            InvalidateVisual();
        }

        protected override void OnMouseRightButtonUp(MouseButtonEventArgs e)
        {
            Point p = e.GetPosition(this);
            if (p.X < Gutter && p.Y >= RulerH)
            {
                int t = (int)((p.Y - RulerH) / RowH);
                if (t >= 0 && t < tracks.Count && TrackMenu != null) TrackMenu(t, PointToScreen(p));
                e.Handled = true;
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
    }
}
