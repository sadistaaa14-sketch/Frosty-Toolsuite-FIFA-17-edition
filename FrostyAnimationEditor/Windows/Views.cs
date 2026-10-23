using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using FrostyAnimationEditor.Core;
using FrostyAnimationEditor.Core.Anim;

namespace FrostyAnimationEditor.Windows
{
    internal static class Draw
    {
        public static Typeface Face = new Typeface("Segoe UI");

        public static FormattedText Text(string s, double size, Brush b, Visual v)
        {
            double dpi = 1.0;
            try { dpi = VisualTreeHelper.GetDpi(v).PixelsPerDip; } catch (Exception) { }
            return new FormattedText(s ?? "", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Face, size, b, dpi);
        }

        public static Pen Pen(Brush b, double w, bool dashed = false)
        {
            Pen p = new Pen(b, w) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
            if (dashed) p.DashStyle = new DashStyle(new double[] { 4, 3 }, 0);
            p.Freeze();
            return p;
        }
    }

    /// <summary>
    /// The clip preview: the rig's joints in world space for each sampled frame, drawn as bones over a ground grid
    /// that shows root motion; the camera follows the hips. Drag to orbit, wheel to zoom.
    /// </summary>
    internal sealed class SkeletonView : FrameworkElement
    {
        private readonly Pose.World w;
        private readonly bool[] helper;
        private readonly double[] mn = new double[3], mx = new double[3];
        private readonly int hip;
        private readonly double ext;
        private double yaw = 0.6, pitch = 0.15, zoom = 1;
        private int frame;
        private Point? drag;
        private readonly DispatcherTimer timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(1000.0 / 30) };
        public event Action<int> FrameShown;
        private static readonly Regex Helper = new Regex("^(Reference|AITrajectory|Trajectory|Prop)");

        public int FrameCount { get { return w.Pos.Length; } }
        public bool Playing { get { return timer.IsEnabled; } }

        public SkeletonView(Pose.World world)
        {
            w = world;
            Height = 360;
            MinWidth = 300;
            ClipToBounds = true;
            Cursor = Cursors.SizeAll;
            int J = w.Joints.Count;
            helper = new bool[J];
            for (int j = 0; j < J; j++) helper[j] = w.Parents[j] < 0 || Helper.IsMatch(w.Joints[j]);
            for (int a = 0; a < 3; a++) { mn[a] = 1e9; mx[a] = -1e9; }
            foreach (float[][] fr in w.Pos)
                for (int j = 0; j < J; j++)
                    if (!helper[j])
                        for (int a = 0; a < 3; a++)
                        {
                            double v = fr[j][a];
                            if (double.IsNaN(v)) continue;
                            mn[a] = Math.Min(mn[a], v);
                            mx[a] = Math.Max(mx[a], v);
                        }
            if (mn[0] > mx[0]) { mn[0] = -1; mn[1] = 0; mn[2] = -1; mx[0] = 1; mx[1] = 2; mx[2] = 1; }
            hip = Math.Max(0, Array.IndexOf(helper, false));
            ext = Math.Max((mx[1] - mn[1]) * 1.5, 1);
            timer.Tick += (s, e) =>
            {
                if (FrameCount == 0) return;
                frame = (frame + 1) % FrameCount;
                InvalidateVisual();
                if (FrameShown != null) FrameShown(frame);
            };
            Unloaded += (s, e) => timer.Stop();
        }

        public void Play(bool on)
        {
            if (on && FrameCount > 1) timer.Start();
            else timer.Stop();
        }

        public void Stop() { timer.Stop(); }

        public void Show(int f)
        {
            if (FrameCount == 0) return;
            frame = Math.Max(0, Math.Min(FrameCount - 1, f));
            InvalidateVisual();
            if (FrameShown != null) FrameShown(frame);
        }

        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            drag = e.GetPosition(this);
            CaptureMouse();
            e.Handled = true;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (drag == null) return;
            Point p = e.GetPosition(this);
            yaw += (p.X - drag.Value.X) * 0.01;
            pitch = Math.Max(-1.5, Math.Min(1.5, pitch + (p.Y - drag.Value.Y) * 0.01));
            drag = p;
            InvalidateVisual();
        }

        protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
        {
            drag = null;
            ReleaseMouseCapture();
        }

        protected override void OnMouseWheel(MouseWheelEventArgs e)
        {
            zoom *= Math.Exp(e.Delta * 0.001);
            InvalidateVisual();
            e.Handled = true;
        }

        protected override void OnRender(DrawingContext dc)
        {
            double W = ActualWidth, Hh = ActualHeight;
            dc.DrawRectangle(Ui.ListBack, Draw.Pen(Ui.Line, 1), new Rect(0, 0, W, Hh));
            if (FrameCount == 0) return;
            float[][] fr = w.Pos[frame];
            double[] ctr = { fr[hip][0], (mn[1] + mx[1]) / 2, fr[hip][2] };
            double cy = Math.Cos(yaw), sy = Math.Sin(yaw), cp = Math.Cos(pitch), sp = Math.Sin(pitch);
            double sc = Math.Min(W, Hh) * 0.8 / ext * zoom;
            Func<double, double, double, Point> proj = (x, y, z) =>
            {
                x -= ctr[0]; y -= ctr[1]; z -= ctr[2];
                double x1 = cy * x + sy * z, z1 = -sy * x + cy * z;
                double y1 = cp * y - sp * z1;
                return new Point(W / 2 + x1 * sc, Hh / 2 - y1 * sc);
            };
            dc.PushClip(new RectangleGeometry(new Rect(0, 0, W, Hh)));
            // ground grid at the lowest joint height
            Pen grid = Draw.Pen(Ui.Line, 1);
            double gs = Math.Pow(10, Math.Floor(Math.Log10(ext / 4)));
            double gx0 = Math.Floor((ctr[0] - ext) / gs) * gs, gz0 = Math.Floor((ctr[2] - ext) / gs) * gs;
            for (int g = 0; g * gs <= 2 * ext + gs && g < 200; g++)
            {
                double x = gx0 + g * gs, z = gz0 + g * gs;
                dc.DrawLine(grid, proj(x, mn[1], ctr[2] - ext), proj(x, mn[1], ctr[2] + ext));
                dc.DrawLine(grid, proj(ctr[0] - ext, mn[1], z), proj(ctr[0] + ext, mn[1], z));
            }
            Pen bone = Draw.Pen(Ui.Fg, 3);
            for (int j = 0; j < w.Joints.Count; j++)
            {
                int par = w.Parents[j];
                if (par < 0 || par >= w.Joints.Count || helper[j] || helper[par]) continue;
                dc.DrawLine(bone, proj(fr[par][0], fr[par][1], fr[par][2]), proj(fr[j][0], fr[j][1], fr[j][2]));
            }
            dc.Pop();
        }
    }

    /// <summary>a line chart over x = 0..1 (curve views); the highlighted series is drawn last, in the accent colour</summary>
    internal sealed class CurvePlot : FrameworkElement
    {
        public sealed class Series
        {
            public List<double> X = new List<double>();
            public List<double?> Y = new List<double?>();
            public bool On;
            public string Label;
        }

        public readonly List<Series> All = new List<Series>();
        private readonly double fy0, fy1;

        public CurvePlot(double y0 = 0, double y1 = 1, double height = 260)
        {
            fy0 = y0;
            fy1 = y1;
            Height = height;
            MinWidth = 300;
            MaxWidth = 900;
            HorizontalAlignment = HorizontalAlignment.Left;
            Width = 640;
        }

        protected override void OnRender(DrawingContext dc)
        {
            double W = ActualWidth, Hh = ActualHeight, pad = 34;
            dc.DrawRectangle(Ui.ListBack, Draw.Pen(Ui.Line, 1), new Rect(0, 0, W, Hh));
            double y0 = fy0, y1 = fy1;
            foreach (Series s in All)
                foreach (double? y in s.Y)
                    if (y.HasValue && !double.IsNaN(y.Value) && !double.IsInfinity(y.Value)) { y0 = Math.Min(y0, y.Value); y1 = Math.Max(y1, y.Value); }
            if (y1 - y0 < 1e-9) { y0 -= 0.5; y1 += 0.5; }
            Func<double, double> X = x => pad + x * (W - 2 * pad);
            Func<double, double> Y = y => Hh - pad - (y - y0) / (y1 - y0) * (Hh - 2 * pad);
            Pen soft = Draw.Pen(Ui.Soft, 1);
            foreach (double gx in new[] { 0, 0.25, 0.5, 0.75, 1 })
            {
                dc.DrawLine(soft, new Point(X(gx), Y(y0)), new Point(X(gx), Y(y1)));
                FormattedText t = Draw.Text(gx.ToString(CultureInfo.InvariantCulture), 11, Ui.Muted, this);
                dc.DrawText(t, new Point(X(gx) - t.Width / 2, Hh - pad + 4));
            }
            foreach (double gy in new[] { y0, (y0 + y1) / 2, y1 })
            {
                dc.DrawLine(soft, new Point(X(0), Y(gy)), new Point(X(1), Y(gy)));
                FormattedText t = Draw.Text(Math.Round(gy, 3).ToString(CultureInfo.InvariantCulture), 11, Ui.Muted, this);
                dc.DrawText(t, new Point(pad - 4 - t.Width, Y(gy) - t.Height / 2));
            }
            foreach (Series s in All.Where(x => !x.On).Concat(All.Where(x => x.On)))
            {
                Pen pen = Draw.Pen(s.On ? (Brush)Ui.Accent : Ui.Line, s.On ? 2.2 : 1.2);
                StreamGeometry g = new StreamGeometry();
                using (StreamGeometryContext c = g.Open())
                {
                    bool penDown = false;
                    for (int i = 0; i < s.X.Count && i < s.Y.Count; i++)
                    {
                        double? y = s.Y[i];
                        if (!y.HasValue || double.IsNaN(y.Value) || double.IsInfinity(y.Value)) { penDown = false; continue; }
                        Point p = new Point(X(s.X[i]), Y(y.Value));
                        if (!penDown) c.BeginFigure(p, false, false);
                        else c.LineTo(p, true, true);
                        penDown = true;
                    }
                }
                g.Freeze();
                dc.DrawGeometry(null, pen, g);
            }
        }
    }

    /// <summary>a blend space: items (numbered), the triangulation and the hull edges</summary>
    internal sealed class BlendView : FrameworkElement
    {
        private readonly JObject r;
        private readonly List<Point> pts = new List<Point>();
        private double x0, x1, y0, y1;
        private readonly ToolTip tip = new ToolTip();
        public event Action<int> ItemClicked;

        public BlendView(JObject blend)
        {
            r = blend;
            Width = 640;
            Height = 420;
            HorizontalAlignment = HorizontalAlignment.Left;
            List<object> items = r.Arr("items");
            List<double> xs = items.Select(i => Num.ToDouble(((JObject)i)["x"])).ToList(), ys = items.Select(i => Num.ToDouble(((JObject)i)["y"])).ToList();
            x0 = xs.Count > 0 ? xs.Min() : 0; x1 = xs.Count > 0 ? xs.Max() : 1;
            y0 = ys.Count > 0 ? ys.Min() : 0; y1 = ys.Count > 0 ? ys.Max() : 1;
            if (x1 - x0 < 1e-6) { x0 -= 1; x1 += 1; }
            if (y1 - y0 < 1e-6) { y0 -= 1; y1 += 1; }
            for (int i = 0; i < xs.Count; i++) pts.Add(new Point(xs[i], ys[i]));
            ToolTip = tip;
            ToolTipService.SetInitialShowDelay(this, 0);
        }

        private double Sc { get { const double pad = 40; return Math.Min((ActualWidth - 2 * pad) / (x1 - x0), (ActualHeight - 2 * pad) / (y1 - y0)); } }
        private Point Map(double x, double y) { const double pad = 40; return new Point(pad + (x - x0) * Sc, ActualHeight - pad - (y - y0) * Sc); }

        private int Hit(Point m)
        {
            for (int i = 0; i < pts.Count; i++)
            {
                Point p = Map(pts[i].X, pts[i].Y);
                if ((p - m).Length < 7) return i;
            }
            return -1;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            int i = Hit(e.GetPosition(this));
            if (i < 0) { tip.IsOpen = false; Cursor = null; return; }
            JObject it = (JObject)r.Arr("items")[i];
            JObject a = it.Obj("asset");
            tip.Content = "#" + i + "  " + Ui.Num(it["x"]) + ", " + Ui.Num(it["y"]) + "\n" + (a != null ? a.Str("type") + " " + a.Str("name") : "-");
            tip.IsOpen = true;
            Cursor = Cursors.Hand;
        }

        protected override void OnMouseLeave(MouseEventArgs e) { tip.IsOpen = false; }

        protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
        {
            int i = Hit(e.GetPosition(this));
            if (i >= 0 && ItemClicked != null) ItemClicked(i);
        }

        protected override void OnRender(DrawingContext dc)
        {
            dc.DrawRectangle(Ui.ListBack, Draw.Pen(Ui.Line, 1), new Rect(0, 0, ActualWidth, ActualHeight));
            Pen tri = Draw.Pen(Ui.Line, 1);
            foreach (object t in r.Arr("triangles"))
            {
                List<object> v = (List<object>)t;
                if (v.Count < 3) continue;
                Point[] p = v.Take(3).Select(x => { int k = (int)Num.ToLong(x); return k >= 0 && k < pts.Count ? Map(pts[k].X, pts[k].Y) : new Point(0, 0); }).ToArray();
                dc.DrawLine(tri, p[0], p[1]);
                dc.DrawLine(tri, p[1], p[2]);
                dc.DrawLine(tri, p[2], p[0]);
            }
            Pen edge = Draw.Pen(Ui.Muted, 1.5);
            foreach (object e in r.Arr("edges"))
            {
                List<object> ab = (List<object>)e;
                List<object> a = (List<object>)ab[0], b = (List<object>)ab[1];
                dc.DrawLine(edge, Map(Num.ToDouble(a[0]), Num.ToDouble(a[1])), Map(Num.ToDouble(b[0]), Num.ToDouble(b[1])));
            }
            for (int i = 0; i < pts.Count; i++)
            {
                Point p = Map(pts[i].X, pts[i].Y);
                dc.DrawEllipse(Ui.Accent, null, p, 4, 4);
                dc.DrawText(Draw.Text(i.ToString(CultureInfo.InvariantCulture), 12, Ui.Fg, this), new Point(p.X + 6, p.Y - 18));
            }
        }
    }
}
