using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;

namespace FrostyAnimationEditor.Core
{
    /// <summary>
    /// Blend spaces (BlendSpace / VoronoiDiagram): the authored items and the derived triangulation.
    /// Authored: VoronoiDiagram.Items[] = {Position (x, y), BlendAsset (key of what plays there), XAxis, Snap},
    /// plus BlendSpace.BlendMode / ClampMode / ValueType / SnapTolerance.
    /// Derived (Rebuild recomputes them from the items):
    ///   PosMin / PosMax   bounding box of the item positions
    ///   Triangles[]       a Delaunay triangulation, every triangle clockwise (y up): V = item indices,
    ///                     Positions, EdgeNormals[0] = unit perp(P1-P0), [1] = perp(P2-P1), [2] = perp(P0-P2),
    ///                     Area = |cross|/2, InverseArea = 1/Area (0 if Area = 0); collinear items: one
    ///                     degenerate triangle [a, b, a] per neighbouring pair
    ///   Edges[]           the hull, clockwise: PosBegin, PosEnd, Edge = unit direction, Normal = perp(Edge),
    ///                     Length, TriangleIndex, IsDegenerate
    ///   PosCentroid       area-weighted mean of the triangle centroids
    ///   NumColumns, NumRows, GridStepX/Y, GridItems[row*NumColumns+col] = triangles touching that cell
    /// The runtime reads these tables as stored; the game holds no builder, so how EA's
    /// tool orders the triangles and picks the grid size is not recoverable - Compare checks the geometry as sets.
    /// </summary>
    public static class BlendSpaces
    {
        public struct V2
        {
            public double X, Y;
            public V2(double x, double y) { X = x; Y = y; }
            public static V2 operator -(V2 a, V2 b) { return new V2(a.X - b.X, a.Y - b.Y); }
            public static V2 operator +(V2 a, V2 b) { return new V2(a.X + b.X, a.Y + b.Y); }
            public static V2 operator *(double s, V2 a) { return new V2(s * a.X, s * a.Y); }
            public static V2 operator /(V2 a, double s) { return new V2(a.X / s, a.Y / s); }
        }

        private static double F32(double x) { return (float)x; }
        private static double Cross(V2 a, V2 b) { return a.X * b.Y - a.Y * b.X; }
        private static V2 Perp(V2 e) { return new V2(-e.Y, e.X); }
        private static double Hypot(double x, double y) { return Math.Sqrt(x * x + y * y); }

        /// <summary>numpy's add.reduce over float64 (pairwise summation, verified against numpy 2.5)</summary>
        public static double NpSum(double[] a)
        {
            if (a.Length == 0) return 0.0;
            return Pairwise(a, 0, a.Length);
        }

        private static double Pairwise(double[] a, int s, int n)
        {
            if (n < 8)
            {
                double res = -0.0;
                for (int i = 0; i < n; i++) res += a[s + i];
                return res;
            }
            if (n <= 128)
            {
                double[] r = new double[8];
                for (int j = 0; j < 8; j++) r[j] = a[s + j];
                int i;
                for (i = 8; i < n - (n % 8); i += 8)
                    for (int j = 0; j < 8; j++) r[j] += a[s + i + j];
                double res = ((r[0] + r[1]) + (r[2] + r[3])) + ((r[4] + r[5]) + (r[6] + r[7]));
                for (; i < n; i++) res += a[s + i];
                return res;
            }
            int n2 = n / 2;
            n2 -= n2 % 8;
            return Pairwise(a, s, n2) + Pairwise(a, s + n2, n - n2);
        }

        private struct Tri
        {
            public int A, B, C;
            public Tri(int a, int b, int c) { A = a; B = b; C = c; }
            public int Max { get { return Math.Max(A, Math.Max(B, C)); } }
            public override bool Equals(object o) { return o is Tri && ((Tri)o).A == A && ((Tri)o).B == B && ((Tri)o).C == C; }
            public override int GetHashCode() { return (A * 73856093) ^ (B * 19349663) ^ (C * 83492791); }
        }

        private struct Circ { public double Ux, Uy, R2; }

        private static Circ Circum(List<V2> allp, Tri t)
        {
            double ax = allp[t.A].X, ay = allp[t.A].Y, bx = allp[t.B].X, by = allp[t.B].Y, cx = allp[t.C].X, cy = allp[t.C].Y;
            double dd = 2 * (ax * (by - cy) + bx * (cy - ay) + cx * (ay - by));
            if (Math.Abs(dd) < 1e-18) return new Circ { Ux = 0, Uy = 0, R2 = double.PositiveInfinity };
            double ux = ((ax * ax + ay * ay) * (by - cy) + (bx * bx + by * by) * (cy - ay) + (cx * cx + cy * cy) * (ay - by)) / dd;
            double uy = ((ax * ax + ay * ay) * (cx - bx) + (bx * bx + by * by) * (ax - cx) + (cx * cx + cy * cy) * (bx - ax)) / dd;
            return new Circ { Ux = ux, Uy = uy, R2 = (ax - ux) * (ax - ux) + (ay - uy) * (ay - uy) };
        }

        /// <summary>Bowyer-Watson; -> triangles (i, j, k) clockwise (y up)</summary>
        public static List<int[]> Delaunay(List<V2> pts)
        {
            int n = pts.Count;
            double minx = pts.Min(p => p.X), maxx = pts.Max(p => p.X), miny = pts.Min(p => p.Y), maxy = pts.Max(p => p.Y);
            double mx = (minx + maxx) / 2, my = (miny + maxy) / 2;
            double d = Math.Max(Math.Max(maxx - minx, maxy - miny), 1e-6) * 64;
            List<V2> allp = new List<V2>(pts);
            allp.Add(new V2(mx - 2 * d, my - d));
            allp.Add(new V2(mx + 2 * d, my - d));
            allp.Add(new V2(mx, my + 2 * d));
            List<Tri> tris = new List<Tri> { new Tri(n, n + 1, n + 2) };
            Dictionary<Tri, Circ> cc = new Dictionary<Tri, Circ>();
            cc[tris[0]] = Circum(allp, tris[0]);
            const double shrink = 1 - 1e-12;
            for (int i = 0; i < n; i++)
            {
                V2 p = pts[i];
                List<Tri> bad = new List<Tri>();
                foreach (Tri t in tris)
                {
                    Circ c = cc[t];
                    if ((p.X - c.Ux) * (p.X - c.Ux) + (p.Y - c.Uy) * (p.Y - c.Uy) < c.R2 * shrink) bad.Add(t);
                }
                List<long> ekeys = new List<long>();
                Dictionary<long, int[]> edges = new Dictionary<long, int[]>();
                foreach (Tri t in bad)
                {
                    foreach (int[] e in new[] { new[] { t.A, t.B }, new[] { t.B, t.C }, new[] { t.C, t.A } })
                    {
                        long k = ((long)Math.Min(e[0], e[1]) << 32) | (uint)Math.Max(e[0], e[1]);
                        if (edges.ContainsKey(k)) edges[k] = null;
                        else { edges[k] = e; ekeys.Add(k); }
                    }
                }
                HashSet<Tri> badSet = new HashSet<Tri>(bad);
                tris = tris.Where(t => !badSet.Contains(t)).ToList();
                foreach (long k in ekeys)
                {
                    int[] e = edges[k];
                    if (e == null) continue;
                    Tri t = new Tri(e[0], e[1], i);
                    tris.Add(t);
                    cc[t] = Circum(allp, t);
                }
            }
            List<int[]> outp = new List<int[]>();
            foreach (Tri t in tris)
            {
                if (t.Max >= n) continue;
                V2 a = allp[t.A], b = allp[t.B], c = allp[t.C];
                double cr = Cross(b - a, c - a);
                outp.Add(cr > 0 ? new[] { t.A, t.C, t.B } : new[] { t.A, t.B, t.C });
            }
            return outp;
        }

        public sealed class TriOut
        {
            public int[] V;
            public V2[] Positions;
            public V2[] EdgeNormals;
            public double Area, InverseArea;
        }

        public sealed class EdgeOut
        {
            public V2 PosBegin, PosEnd, Edge, Normal;
            public double Length;
            public int TriangleIndex;
            public bool IsDegenerate;
        }

        public sealed class Diagram
        {
            public V2 PosMin, PosMax, PosCentroid;
            public List<TriOut> Triangles = new List<TriOut>();
            public List<EdgeOut> Edges = new List<EdgeOut>();
            public long NumColumns, NumRows;
            public double GridStepX, GridStepY;
            public List<List<int>> GridItems = new List<List<int>>();
        }

        /// <summary>the derived VoronoiDiagram values for item positions; grid = (cols, rows) keeps a grid size</summary>
        public static Diagram Build(List<V2> positions, long[] grid = null)
        {
            List<V2> P = positions.Select(p => new V2(F32(p.X), F32(p.Y))).ToList();
            int n = P.Count;
            V2 mn = new V2(P.Min(p => p.X), P.Min(p => p.Y)), mx = new V2(P.Max(p => p.X), P.Max(p => p.Y));
            double w = mx.X - mn.X, h = mx.Y - mn.Y;
            List<int[]> tris = new List<int[]>();
            List<Tuple<V2, V2, int>> edges = new List<Tuple<V2, V2, int>>();
            bool collinear = n < 3;
            if (!collinear)
            {
                collinear = true;
                for (int j = 2; j < n; j++)
                    if (!(Math.Abs(Cross(P[j] - P[0], P[1] - P[0])) < 1e-12)) { collinear = false; break; }
            }
            if (collinear)
            {
                int axis = w >= h ? 0 : 1;
                List<int> order = Enumerable.Range(0, n)
                    .OrderBy(j => axis == 0 ? P[j].X : P[j].Y).ThenBy(j => axis == 0 ? P[j].Y : P[j].X).ToList();
                for (int k = 0; k + 1 < order.Count; k++) tris.Add(new[] { order[k], order[k + 1], order[k] });
                List<Tuple<int, int, int>> fwd = new List<Tuple<int, int, int>>();
                for (int k = 0; k + 1 < order.Count; k++) fwd.Add(Tuple.Create(order[k], order[k + 1], k));
                List<Tuple<int, int, int>> all = new List<Tuple<int, int, int>>(fwd);
                for (int k = fwd.Count - 1; k >= 0; k--) all.Add(Tuple.Create(fwd[k].Item2, fwd[k].Item1, fwd[k].Item3));
                foreach (Tuple<int, int, int> e in all) edges.Add(Tuple.Create(P[e.Item1], P[e.Item2], e.Item3));
            }
            else
            {
                tris = Delaunay(P);
                // hull = the triangles' boundary edges (directed as in their clockwise triangle), starting with the
                // first one in triangle order and followed as a chain
                List<long> dkeys = new List<long>();
                Dictionary<long, int> directed = new Dictionary<long, int>();
                for (int k = 0; k < tris.Count; k++)
                {
                    int[] t = tris[k];
                    foreach (int[] e in new[] { new[] { t[0], t[1] }, new[] { t[1], t[2] }, new[] { t[2], t[0] } })
                    {
                        long key = ((long)e[0] << 32) | (uint)e[1];
                        if (!directed.ContainsKey(key)) dkeys.Add(key);
                        directed[key] = k;
                    }
                }
                List<int[]> boundary = new List<int[]>();
                foreach (long key in dkeys)
                {
                    int a = (int)(key >> 32), b = (int)(uint)key;
                    if (!directed.ContainsKey(((long)b << 32) | (uint)a)) boundary.Add(new[] { a, b, directed[key] });
                }
                Dictionary<int, int[]> nxt = new Dictionary<int, int[]>();
                foreach (int[] e in boundary) nxt[e[0]] = e;
                Func<int[], int> edgePos = e =>
                {
                    int[] t = tris[e[2]];
                    if (t[0] == e[0] && t[1] == e[1]) return 0;
                    if (t[1] == e[0] && t[2] == e[1]) return 1;
                    return 2;
                };
                int[] first = null;
                foreach (int[] e in boundary)
                    if (first == null || e[2] < first[2] || (e[2] == first[2] && edgePos(e) < edgePos(first))) first = e;
                int[] cur = first;
                for (int s = 0; s < boundary.Count && cur != null; s++)
                {
                    edges.Add(Tuple.Create(P[cur[0]], P[cur[1]], cur[2]));
                    int[] next;
                    if (!nxt.TryGetValue(cur[1], out next)) break;
                    cur = next;
                    if (cur == first) break;
                }
            }
            Diagram r = new Diagram();
            foreach (int[] t in tris)
            {
                V2 p0 = P[t[0]], p1 = P[t[1]], p2 = P[t[2]];
                V2 e0 = p1 - p0, e1 = p2 - p1, e2 = p0 - p2;
                V2 n0 = Perp(e0);
                double ln = Hypot(n0.X, n0.Y);
                if (ln > 0) n0 = n0 / ln;
                double area = Math.Abs(Cross(e0, p2 - p0)) / 2.0;
                r.Triangles.Add(new TriOut
                {
                    V = (int[])t.Clone(), Positions = new[] { p0, p1, p2 }, EdgeNormals = new[] { n0, Perp(e1), Perp(e2) },
                    Area = area, InverseArea = area > 0 ? 1.0 / area : 0.0
                });
            }
            foreach (Tuple<V2, V2, int> e in edges)
            {
                V2 d = e.Item2 - e.Item1;
                double L = Hypot(d.X, d.Y);
                V2 u = L > 0 ? d / L : new V2(0, 0);
                r.Edges.Add(new EdgeOut { PosBegin = e.Item1, PosEnd = e.Item2, Edge = u, Normal = Perp(u), Length = L, TriangleIndex = e.Item3, IsDegenerate = L == 0 });
            }
            double[] areas = r.Triangles.Select(t => t.Area).ToArray();
            double asum = NpSum(areas);
            V2 cen = new V2(0, 0);
            if (asum > 0)
            {
                V2 acc = new V2(0, 0);
                bool firstTerm = true;
                for (int k = 0; k < r.Triangles.Count; k++)
                {
                    TriOut t = r.Triangles[k];
                    V2 s = (t.Positions[0] + t.Positions[1]) + t.Positions[2];
                    V2 term = (areas[k] * s) / 3;
                    acc = firstTerm ? new V2(0 + term.X, 0 + term.Y) : acc + term;
                    firstTerm = false;
                }
                cen = acc / asum;
            }
            long cols, rows;
            if (grid != null) { cols = grid[0]; rows = grid[1]; }
            else GridSize(w, h, r.Triangles.Count, out cols, out rows);
            double sx = cols != 0 ? w / cols : 0.0;
            double sy = rows != 0 ? h / rows : 0.0;
            for (long rr = 0; rr < rows; rr++)
                for (long q = 0; q < cols; q++)
                {
                    double x0 = mn.X + q * sx, y0 = mn.Y + rr * sy;
                    List<int> cell = new List<int>();
                    for (int k = 0; k < r.Triangles.Count; k++)
                        if (TriRect(r.Triangles[k].Positions, x0, y0, x0 + sx, y0 + sy)) cell.Add(k);
                    r.GridItems.Add(cell);
                }
            r.PosMin = mn;
            r.PosMax = mx;
            r.PosCentroid = cen;
            r.NumColumns = cols;
            r.NumRows = rows;
            r.GridStepX = sx;
            r.GridStepY = sy;
            return r;
        }

        /// <summary>one cell for small diagrams, otherwise about two triangles per cell shaped like the diagram</summary>
        public static void GridSize(double w, double h, int ntri, out long cols, out long rows)
        {
            if (ntri <= 6 || w <= 0 || h <= 0) { cols = 1; rows = 1; return; }
            double target = Math.Max(1.0, ntri / 2.0);
            long c = Math.Max(1, (long)Math.Round(Math.Sqrt(target * w / h)));
            long r = Math.Max(1, (long)Math.Round(target / c));
            cols = Math.Min(c, 8);
            rows = Math.Min(r, 8);
        }

        private static bool TriRect(V2[] T, double x0, double y0, double x1, double y1, double eps = 1e-6)
        {
            double maxX = Math.Max(T[0].X, Math.Max(T[1].X, T[2].X)), minX = Math.Min(T[0].X, Math.Min(T[1].X, T[2].X));
            double maxY = Math.Max(T[0].Y, Math.Max(T[1].Y, T[2].Y)), minY = Math.Min(T[0].Y, Math.Min(T[1].Y, T[2].Y));
            if (maxX < x0 - eps || minX > x1 + eps || maxY < y0 - eps || minY > y1 + eps) return false;
            V2[] R = { new V2(x0, y0), new V2(x1, y0), new V2(x1, y1), new V2(x0, y1) };
            for (int i = 0; i < 3; i++)
            {
                V2 e = T[(i + 1) % 3] - T[i];
                V2 nrm = new V2(-e.Y, e.X);
                if (nrm.X == 0 && nrm.Y == 0) continue;
                double ptMax = double.NegativeInfinity, ptMin = double.PositiveInfinity, prMax = double.NegativeInfinity, prMin = double.PositiveInfinity;
                foreach (V2 p in T) { double v = p.X * nrm.X + p.Y * nrm.Y; ptMax = Math.Max(ptMax, v); ptMin = Math.Min(ptMin, v); }
                foreach (V2 p in R) { double v = p.X * nrm.X + p.Y * nrm.Y; prMax = Math.Max(prMax, v); prMin = Math.Min(prMin, v); }
                if (ptMax < prMin - eps || prMax < ptMin - eps) return false;
            }
            return true;
        }

        // ------------------------------------------------------------------ objects
        private static float[] V4(V2 p) { return new[] { (float)p.X, (float)p.Y, 0f, 0f }; }

        private static GdStruct NewStruct(TypeTable types, string name) { return Blank.Struct(types, types[name]); }

        private static List<V2> Positions(GdStruct vd)
        {
            List<V2> r = new List<V2>();
            foreach (object it in vd["Items"] as IList ?? new object[0])
            {
                float[] p = ((GdStruct)it)["Position"] as float[] ?? new float[4];
                r.Add(new V2(p[0], p[1]));
            }
            return r;
        }

        /// <summary>recompute a BlendSpace's derived diagram from its items (in place)</summary>
        public static GdObj Rebuild(AntDb db, GdObj bs, bool keepGrid = true)
        {
            GdStruct v = bs["VoronoiDiagram"] as GdStruct;
            if (v == null) throw new GdException("the BlendSpace has no VoronoiDiagram");
            List<V2> pos = Positions(v);
            if (pos.Count < 2) throw new GdException("a blend space needs at least two items");
            long nc = v.GetInt("NumColumns"), nr = v.GetInt("NumRows");
            long[] grid = keepGrid && nc != 0 && nr != 0 ? new[] { nc, nr } : null;
            Diagram r = Build(pos, grid);
            TypeTable types = db.Types;
            v["PosMin"] = V4(r.PosMin);
            v["PosMax"] = V4(r.PosMax);
            v["PosCentroid"] = V4(r.PosCentroid);
            List<GdStruct> tl = new List<GdStruct>();
            foreach (TriOut t in r.Triangles)
            {
                GdStruct s = NewStruct(types, "VoronoiTriangle");
                s["Positions"] = t.Positions.Select(V4).ToList();
                s["EdgeNormals"] = t.EdgeNormals.Select(V4).ToList();
                s["V"] = t.V.Select(x => (long)x).ToList();
                s["Area"] = (float)t.Area;
                s["InverseArea"] = (float)t.InverseArea;
                tl.Add(s);
            }
            v["Triangles"] = tl;
            List<GdStruct> el = new List<GdStruct>();
            foreach (EdgeOut e in r.Edges)
            {
                GdStruct s = NewStruct(types, "VoronoiEdge");
                s["PosBegin"] = V4(e.PosBegin);
                s["PosEnd"] = V4(e.PosEnd);
                s["Edge"] = V4(e.Edge);
                s["Normal"] = V4(e.Normal);
                s["Length"] = (float)e.Length;
                s["TriangleIndex"] = (long)e.TriangleIndex;
                s["IsDegenerate"] = e.IsDegenerate;
                el.Add(s);
            }
            v["Edges"] = el;
            List<GdStruct> gl = new List<GdStruct>();
            foreach (List<int> g in r.GridItems)
            {
                GdStruct s = NewStruct(types, "VoronoiGridItem");
                s["TriangleIndices"] = g.Select(x => (long)x).ToList();
                gl.Add(s);
            }
            v["GridItems"] = gl;
            v["NumColumns"] = r.NumColumns;
            v["NumRows"] = r.NumRows;
            v["GridStepX"] = (float)r.GridStepX;
            v["GridStepY"] = (float)r.GridStepY;
            return bs;
        }

        public sealed class Comparison
        {
            public bool Triangles, Hull, BBox, Centroid, Grid;
            public override string ToString()
            {
                return "triangles " + (Triangles ? "same" : "differ") + ", hull " + (Hull ? "same" : "differ") + ", bbox " + (BBox ? "same" : "differs") +
                       ", centroid " + (Centroid ? "same" : "differs") + ", grid " + (Grid ? "same" : "differs");
            }
        }

        private static string Canon(IList v)
        {
            int[] t = new int[3];
            for (int i = 0; i < 3; i++) t[i] = (int)Num.ToLong(v[i]);
            int k = 0;
            for (int j = 1; j < 3; j++) if (t[j] < t[k]) k = j;
            return t[k] + "," + t[(k + 1) % 3] + "," + t[(k + 2) % 3];
        }

        private static string E5(double x, double y) { return Math.Round(x, 5).ToString("R") + ";" + Math.Round(y, 5).ToString("R"); }

        private static bool Close(double a, double b, double atol = 1e-8) { return Math.Abs(a - b) <= atol + 1e-5 * Math.Abs(b); }

        /// <summary>differences between a stored diagram and a fresh build (geometry compared as sets)</summary>
        public static Comparison Compare(GdObj bs)
        {
            GdStruct v = (GdStruct)bs["VoronoiDiagram"];
            Diagram r = Build(Positions(v));
            Comparison c = new Comparison();
            List<string> st = (v["Triangles"] as IList ?? new object[0]).Cast<GdStruct>().Select(t => Canon((IList)t["V"])).OrderBy(s => s, StringComparer.Ordinal).ToList();
            List<string> bt = r.Triangles.Select(t => Canon(t.V)).OrderBy(s => s, StringComparer.Ordinal).ToList();
            c.Triangles = st.SequenceEqual(bt);
            List<string> se = (v["Edges"] as IList ?? new object[0]).Cast<GdStruct>().Select(e =>
            {
                float[] a = (float[])e["PosBegin"], b = (float[])e["PosEnd"];
                return E5(a[0], a[1]) + "|" + E5(b[0], b[1]);
            }).OrderBy(s => s, StringComparer.Ordinal).ToList();
            List<string> be = r.Edges.Select(e => E5(e.PosBegin.X, e.PosBegin.Y) + "|" + E5(e.PosEnd.X, e.PosEnd.Y)).OrderBy(s => s, StringComparer.Ordinal).ToList();
            c.Hull = se.SequenceEqual(be);
            float[] pmin = (float[])v["PosMin"], pmax = (float[])v["PosMax"], pc = (float[])v["PosCentroid"];
            c.BBox = Close(pmin[0], r.PosMin.X) && Close(pmin[1], r.PosMin.Y) && Close(pmax[0], r.PosMax.X) && Close(pmax[1], r.PosMax.Y);
            c.Centroid = Close(pc[0], r.PosCentroid.X, 1e-5) && Close(pc[1], r.PosCentroid.Y, 1e-5);
            c.Grid = v.GetInt("NumColumns") == r.NumColumns && v.GetInt("NumRows") == r.NumRows;
            return c;
        }

        private static List<double> RoundedPositions(GdObj bs)
        {
            List<double> r = new List<double>();
            foreach (V2 p in Positions((GdStruct)bs["VoronoiDiagram"])) { r.Add(Math.Round(p.X, 7)); r.Add(Math.Round(p.Y, 7)); }
            return r;
        }

        /// <summary>true when block i's item positions differ from the original file (new blocks: true)</summary>
        public static bool ItemsChanged(AntDb db, int i)
        {
            if (i >= db.N0) return true;
            return !RoundedPositions(db.Original(i)).SequenceEqual(RoundedPositions(db.Obj(i)));
        }

        /// <summary>rebuild the diagram of every BlendSpace whose item positions were edited (Save runs this)</summary>
        public static int Fix(AntDb db, Action<string> log = null)
        {
            int n = 0;
            foreach (int i in db.Cache.Keys.ToList().Concat(db.NewObjs.Keys.ToList()))
            {
                if (db.Removed.Contains(i) || db.TypeName(i) != "BlendSpace") continue;
                if (ItemsChanged(db, i))
                {
                    Rebuild(db, db.Obj(i));
                    n++;
                    if (log != null) log("blend space diagram rebuilt: " + db.Obj(i).Name);
                }
            }
            return n;
        }
    }
}
