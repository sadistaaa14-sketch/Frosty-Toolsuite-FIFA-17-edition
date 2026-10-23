using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Frosty.Controls;
using FrostyAnimationEditor.Core;

namespace FrostyAnimationEditor.Windows
{
    /// <summary>
    /// A reference graph or a state machine: objects as boxes, Key fields / transitions as arrows. Drag the background
    /// to pan, wheel to zoom, drag a box to move it; click selects (the links show on the right), double-click opens the
    /// object in the editor. Save as HTML (the same interactive view in a browser) or Graphviz DOT.
    /// </summary>
    internal sealed class GraphWindow : FrostyWindow
    {
        private readonly EditorWindow editor;
        private readonly Graph g;
        private readonly GraphCanvas canvas;
        private readonly TextBox side = new TextBox { IsReadOnly = true, TextWrapping = TextWrapping.Wrap, FontFamily = Ui.Mono, FontSize = 11.5, BorderThickness = new Thickness(0),
                                                       VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Background = Brushes.Transparent, Padding = new Thickness(8) };
        private readonly TextBox find = Ui.Box("", 220, false, "find name / type / key (Enter)");
        private int hitIndex;

        public GraphWindow(EditorWindow owner, Graph graph)
        {
            editor = owner;
            g = graph;
            Title = "Graph: " + g.Title;
            Width = Math.Min(1500, SystemParameters.WorkArea.Width - 80);
            Height = Math.Min(900, SystemParameters.WorkArea.Height - 80);
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Background = Ui.Back;
            canvas = new GraphCanvas(g);
            canvas.Selected += n => side.Text = Describe(n);
            canvas.Opened += n => { if (n.State == "ok") { editor.Go(BE.KeyHex(n.Key)); editor.Activate(); } };
            side.Foreground = Ui.Fg;
            side.Text = "click a box";

            DockPanel bar = new DockPanel { Margin = new Thickness(8, 5, 8, 5) };
            CheckBox labels = Ui.Check("link labels", g.Edges.Count <= 120);
            labels.Click += (s, e) => { canvas.ShowLabels = Ui.On(labels); canvas.InvalidateVisual(); };
            canvas.ShowLabels = Ui.On(labels);
            StackPanel right = Ui.H(find, Ui.B("Fit", () => canvas.Fit()), labels, Ui.B("Save HTML…", () => SaveText("html")), Ui.B("Save DOT…", () => SaveText("dot")));
            DockPanel.SetDock(right, Dock.Right);
            bar.Children.Add(right);
            bar.Children.Add(Ui.Trim(Ui.T(g.Title + "  (" + g.Nodes.Count + " objects, " + g.Edges.Count + " links" + (g.Truncated ? ", truncated" : "") + ")", null, 12, true)));
            find.KeyDown += (s, e) => { if (e.Key == Key.Enter) { e.Handled = true; Find(); } };

            Grid main = new Grid();
            main.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            main.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            main.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(380) });
            Grid.SetColumn(canvas, 0);
            GridSplitter sp = new GridSplitter { Width = 4, HorizontalAlignment = HorizontalAlignment.Stretch, Background = Ui.Soft, ResizeBehavior = GridResizeBehavior.PreviousAndNext };
            Grid.SetColumn(sp, 1);
            Grid.SetColumn(side, 2);
            main.Children.Add(canvas);
            main.Children.Add(sp);
            main.Children.Add(side);
            DockPanel root = new DockPanel();
            Border barB = new Border { Child = bar, BorderBrush = Ui.Line, BorderThickness = new Thickness(0, 0, 0, 1) };
            DockPanel.SetDock(barB, Dock.Top);
            root.Children.Add(barB);
            root.Children.Add(main);
            Content = root;
            Loaded += (s, e) => canvas.Fit();
        }

        private string Describe(GraphNode n)
        {
            if (n == null) return "click a box";
            StringBuilder sb = new StringBuilder();
            sb.Append(n.Name).Append('\n').Append(n.Type).Append("  ").Append(n.KeyHex).Append("\n\n");
            List<GraphEdge> outs = g.Edges.Where(e => e.S == n.Key).ToList(), ins = g.Edges.Where(e => e.T == n.Key).ToList();
            if (outs.Count > 0)
            {
                sb.Append("links out:\n");
                foreach (GraphEdge e in outs) sb.Append("  ").Append(e.Label).Append(" -> ").Append(g.Nodes.ContainsKey(e.T) ? g.Nodes[e.T].Name : BE.KeyHex(e.T)).Append('\n');
            }
            if (ins.Count > 0)
            {
                sb.Append("links in:\n");
                foreach (GraphEdge e in ins) sb.Append("  ").Append(g.Nodes.ContainsKey(e.S) ? g.Nodes[e.S].Name : BE.KeyHex(e.S)).Append(" -> ").Append(e.Label).Append('\n');
            }
            if (n.Info.Count > 0) sb.Append("\nfields:\n").Append(string.Join("\n", n.Info));
            if (n.State == "ok") sb.Append("\n\n(double-click the box to open it in the editor)");
            return sb.ToString();
        }

        private void Find()
        {
            string q = find.Text.Trim().ToLowerInvariant();
            if (q.Length == 0) return;
            List<GraphNode> hits = g.NodeList.Where(n => ((n.Name ?? "") + " " + n.Type + " " + n.KeyHex).ToLowerInvariant().Contains(q)).ToList();
            if (hits.Count == 0) { side.Text = "nothing matches " + Ui.Quote(q); return; }
            GraphNode h = hits[hitIndex++ % hits.Count];
            canvas.CenterOn(h);
            canvas.Select(h);
        }

        private void SaveText(string fmt)
        {
            Microsoft.Win32.SaveFileDialog d = new Microsoft.Win32.SaveFileDialog
            {
                FileName = "graph." + fmt,
                Filter = fmt == "html" ? "Web page (*.html)|*.html" : "Graphviz DOT (*.dot)|*.dot",
                Title = "Save the graph"
            };
            if (d.ShowDialog(this) != true) return;
            try
            {
                File.WriteAllText(d.FileName, fmt == "html" ? g.ToHtml(editor.M.Db) : g.ToDot(), new UTF8Encoding(false));
                editor.Msg("saved the graph to " + d.FileName);
            }
            catch (Exception e) when (EditorWindow.IsUserError(e)) { FrostyMessageBox.Show(e.Message, "Save the graph"); }
        }
    }

    internal sealed class GraphCanvas : FrameworkElement
    {
        private readonly Graph g;
        private double tx = 20, ty = 20, sc = 1;
        private GraphNode sel, dragNode;
        private Point? pan, last;
        private bool moved;
        public bool ShowLabels = true;
        public event Action<GraphNode> Selected, Opened;

        public GraphCanvas(Graph graph)
        {
            g = graph;
            ClipToBounds = true;
            Focusable = true;
        }

        public void Fit()
        {
            if (g.Nodes.Count == 0 || ActualWidth <= 0) return;
            double x0 = g.NodeList.Min(n => n.X), y0 = g.NodeList.Min(n => n.Y), x1 = g.NodeList.Max(n => n.X + n.W), y1 = g.NodeList.Max(n => n.Y + n.H);
            sc = Math.Min(1.5, Math.Min(ActualWidth / (x1 - x0 + 40), ActualHeight / (y1 - y0 + 40)));
            tx = (ActualWidth - (x1 - x0) * sc) / 2 - x0 * sc;
            ty = (ActualHeight - (y1 - y0) * sc) / 2 - y0 * sc;
            InvalidateVisual();
        }

        public void CenterOn(GraphNode n)
        {
            tx = ActualWidth / 2 - (n.X + n.W / 2) * sc;
            ty = ActualHeight / 2 - (n.Y + n.H / 2) * sc;
            InvalidateVisual();
        }

        public void Select(GraphNode n)
        {
            sel = n;
            InvalidateVisual();
            if (Selected != null) Selected(n);
        }

        private Point ToWorld(Point p) { return new Point((p.X - tx) / sc, (p.Y - ty) / sc); }

        private GraphNode HitNode(Point p)
        {
            Point w = ToWorld(p);
            for (int i = g.Order.Count - 1; i >= 0; i--)
            {
                GraphNode n = g.Nodes[g.Order[i]];
                if (w.X >= n.X && w.X <= n.X + n.W && w.Y >= n.Y && w.Y <= n.Y + n.H) return n;
            }
            return null;
        }

        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            Focus();
            Point p = e.GetPosition(this);
            GraphNode n = HitNode(p);
            if (e.ClickCount == 2 && n != null)
            {
                if (Opened != null) Opened(n);
                return;
            }
            moved = false;
            last = p;
            if (n != null) dragNode = n;
            else pan = p;
            CaptureMouse();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (last == null) return;
            Point p = e.GetPosition(this);
            double dx = p.X - last.Value.X, dy = p.Y - last.Value.Y;
            if (Math.Abs(dx) + Math.Abs(dy) > 0.5) moved = true;
            if (dragNode != null)
            {
                dragNode.X += dx / sc;
                dragNode.Y += dy / sc;
            }
            else
            {
                tx += dx;
                ty += dy;
            }
            last = p;
            InvalidateVisual();
        }

        protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
        {
            if (!moved)
            {
                if (dragNode != null) Select(dragNode);
                else if (pan != null) Select(null);
            }
            dragNode = null;
            pan = null;
            last = null;
            ReleaseMouseCapture();
        }

        protected override void OnMouseWheel(MouseWheelEventArgs e)
        {
            Point m = e.GetPosition(this);
            double f = Math.Exp(e.Delta * 0.0015);
            tx = m.X - (m.X - tx) * f;
            ty = m.Y - (m.Y - ty) * f;
            sc *= f;
            InvalidateVisual();
            e.Handled = true;
        }

        private static string Cut(string s, int n) { s = s ?? ""; return s.Length > n ? s.Substring(0, n - 1) + "…" : s; }

        protected override void OnRender(DrawingContext dc)
        {
            dc.DrawRectangle(Ui.ListBack, null, new Rect(0, 0, ActualWidth, ActualHeight));
            dc.PushTransform(new MatrixTransform(sc, 0, 0, sc, tx, ty));
            HashSet<GraphEdge> hl = new HashSet<GraphEdge>(sel != null ? g.Edges.Where(e => e.S == sel.Key || e.T == sel.Key) : Enumerable.Empty<GraphEdge>());
            Pen ePen = Draw.Pen(Ui.Line, 1.2 / Math.Max(sc, 0.2)), tPen = Draw.Pen(Ui.Muted, 1.2 / Math.Max(sc, 0.2)), dPen = Draw.Pen(Ui.Line, 1.2 / Math.Max(sc, 0.2), true),
                hPen = Draw.Pen(Ui.Accent, 2 / Math.Max(sc, 0.2));
            foreach (GraphEdge e in g.Edges)
            {
                GraphNode a, b;
                if (!g.Nodes.TryGetValue(e.S, out a) || !g.Nodes.TryGetValue(e.T, out b)) continue;
                Pen pen = hl.Contains(e) ? hPen : e.Kind == "transition" ? tPen : e.Kind == "subject" ? dPen : ePen;
                Point p0, p1, c0, c1;
                if (a == b)
                {
                    double x = a.X + a.W, y = a.Y + a.H / 2;
                    p0 = new Point(x, y - 6); p1 = new Point(x, y + 6); c0 = new Point(x + 40, y - 40); c1 = new Point(x + 40, y + 40);
                }
                else if (b.X <= a.X)
                {
                    bool down = b.Y > a.Y;
                    p0 = new Point(a.X + a.W / 2, a.Y + (down ? a.H : 0));
                    p1 = new Point(b.X + b.W / 2, b.Y + (down ? 0 : b.H));
                    double d = Math.Max(60, Math.Abs(a.X - b.X) / 3);
                    c0 = new Point(p0.X + d, p0.Y + (down ? d : -d));
                    c1 = new Point(p1.X + d, p1.Y + (down ? -d : d));
                }
                else
                {
                    p0 = new Point(a.X + a.W, a.Y + a.H / 2);
                    p1 = new Point(b.X, b.Y + b.H / 2);
                    double d = Math.Max(30, (p1.X - p0.X) / 2);
                    c0 = new Point(p0.X + d, p0.Y);
                    c1 = new Point(p1.X - d, p1.Y);
                }
                StreamGeometry geo = new StreamGeometry();
                using (StreamGeometryContext c = geo.Open())
                {
                    c.BeginFigure(p0, false, false);
                    c.BezierTo(c0, c1, p1, true, true);
                }
                geo.Freeze();
                dc.DrawGeometry(null, pen, geo);
                // arrow head along the curve's end tangent
                Vector dir = p1 - c1;
                if (dir.Length < 1e-6) dir = p1 - p0;
                if (dir.Length > 1e-6)
                {
                    dir.Normalize();
                    Vector nrm = new Vector(-dir.Y, dir.X);
                    Point q1 = p1 - dir * 8 + nrm * 4, q2 = p1 - dir * 8 - nrm * 4;
                    StreamGeometry ah = new StreamGeometry();
                    using (StreamGeometryContext c = ah.Open())
                    {
                        c.BeginFigure(p1, true, true);
                        c.LineTo(q1, true, false);
                        c.LineTo(q2, true, false);
                    }
                    ah.Freeze();
                    dc.DrawGeometry(pen.Brush, null, ah);
                }
                if (ShowLabels && !string.IsNullOrEmpty(e.Label))
                    dc.DrawText(Draw.Text(Cut(e.Label, 48), 9, Ui.Muted, this), new Point((a.X + a.W + b.X) / 2 - 20, (a.Y + b.Y + a.H) / 2 - 12));
            }
            foreach (GraphNode n in g.NodeList)
            {
                bool strong = n.Role == "root" || n.Role == "start";
                bool warn = n.State == "missing" || n.State == "imported";
                Brush stroke = n == sel ? (Brush)Ui.Accent : warn ? Ui.Warn : strong ? Ui.Fg : Ui.Line;
                double w = n == sel ? 2.5 : strong ? 2 : 1;
                Pen p = Draw.Pen(stroke, w, n.Role == "subject" || warn);
                dc.DrawRoundedRectangle(Ui.Panel, p, new Rect(n.X, n.Y, n.W, n.H), 4, 4);
                dc.DrawText(Draw.Text(Cut(string.IsNullOrEmpty(n.Name) ? n.KeyHex : n.Name, 32), 11, Ui.Fg, this), new Point(n.X + 7, n.Y + 3));
                dc.DrawText(Draw.Text(Cut(n.Type, 36), 10, Ui.Muted, this), new Point(n.X + 7, n.Y + 19));
            }
            dc.Pop();
        }
    }
}
