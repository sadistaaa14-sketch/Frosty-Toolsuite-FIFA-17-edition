using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using FrostyAnimationEditor.Core.Anim;

namespace FrostyAnimationEditor.Windows
{
    /// <summary>
    /// 3D view of a skinned player: WPF Viewport3D, meshes skinned on the CPU each frame, bones as thin
    /// octahedra, a 1 m ground grid. Left drag orbits, right / middle drag pans, the wheel zooms, double-click resets.
    /// </summary>
    internal sealed class PlayerView : Border
    {
        private sealed class Part
        {
            public MeshPart Mesh;
            public MeshGeometry3D Geo;
            public GeometryModel3D Model;
            public DiffuseMaterial Diffuse;
            public float[] Pos, Nrm;
        }

        private readonly Viewport3D vp = new Viewport3D { ClipToBounds = true, IsHitTestVisible = false };
        private readonly PerspectiveCamera cam = new PerspectiveCamera { FieldOfView = 40, NearPlaneDistance = 0.03, FarPlaneDistance = 400 };
        private readonly Model3DGroup scene = new Model3DGroup();
        private readonly Model3DGroup partsGroup = new Model3DGroup();
        private readonly DirectionalLight keyLight = new DirectionalLight(Color.FromRgb(0xDC, 0xDC, 0xDC), new Vector3D(-0.4, -0.7, -1));
        private readonly DirectionalLight fillLight = new DirectionalLight(Color.FromRgb(0x46, 0x48, 0x50), new Vector3D(0.6, 0.3, 0.8));
        private readonly AmbientLight ambient = new AmbientLight(Color.FromRgb(0x44, 0x44, 0x4A));
        private readonly GeometryModel3D bones = new GeometryModel3D();
        private readonly GeometryModel3D ground = new GeometryModel3D();
        private readonly TextBlock overlay = Ui.T("", Ui.Muted, 11);
        private readonly List<Part> parts = new List<Part>();
        private float[] mats;

        public double Yaw = Math.PI * 0.82, Pitch = 0.18, Dist = 4.2;
        public Point3D Target = new Point3D(0, 1.0, 0);
        public bool ShowMesh = true, ShowBones = false;
        private Point last;
        private MouseButton? dragging;

        public PlayerView()
        {
            Background = new SolidColorBrush(Color.FromRgb(0x23, 0x24, 0x28));
            BorderBrush = Ui.Line;
            BorderThickness = new Thickness(1);
            ClipToBounds = true;
            vp.Camera = cam;
            scene.Children.Add(ambient);
            scene.Children.Add(keyLight);
            scene.Children.Add(fillLight);
            scene.Children.Add(ground);
            scene.Children.Add(partsGroup);
            scene.Children.Add(bones);
            vp.Children.Add(new ModelVisual3D { Content = scene });
            Grid g = new Grid();
            g.Children.Add(vp);
            overlay.Margin = new Thickness(8, 6, 8, 6);
            overlay.VerticalAlignment = VerticalAlignment.Top;
            overlay.HorizontalAlignment = HorizontalAlignment.Left;
            overlay.IsHitTestVisible = false;
            g.Children.Add(overlay);
            Child = g;
            MaterialGroup bm = new MaterialGroup();
            bm.Children.Add(new DiffuseMaterial(new SolidColorBrush(Color.FromRgb(0x7A, 0xA7, 0xFF))));
            bm.Children.Add(new EmissiveMaterial(new SolidColorBrush(Color.FromRgb(0x30, 0x48, 0x80))));
            bones.Material = bm;
            bones.BackMaterial = bm;
            BuildGround(0, 0);
            UpdateCamera();

            MouseDown += (s, e) =>
            {
                if (e.ClickCount == 2 && e.ChangedButton == MouseButton.Left) { ResetView(); return; }
                dragging = e.ChangedButton;
                last = e.GetPosition(this);
                CaptureMouse();
            };
            MouseUp += (s, e) => { dragging = null; ReleaseMouseCapture(); };
            MouseMove += (s, e) =>
            {
                if (dragging == null) return;
                Point p = e.GetPosition(this);
                double dx = p.X - last.X, dy = p.Y - last.Y;
                last = p;
                if (dragging == MouseButton.Left)
                {
                    Yaw -= dx * 0.01;
                    Pitch = Math.Max(-1.4, Math.Min(1.45, Pitch + dy * 0.01));
                }
                else
                {
                    Vector3D look = cam.LookDirection;
                    look.Normalize();
                    Vector3D right = Vector3D.CrossProduct(look, new Vector3D(0, 1, 0));
                    if (right.Length < 1e-6) right = new Vector3D(1, 0, 0);
                    right.Normalize();
                    Vector3D up = Vector3D.CrossProduct(right, look);
                    double k = Dist * 0.0018;
                    Target = Target - right * dx * k + up * dy * k;
                    PanOffset = PanOffset - right * dx * k + up * dy * k;
                }
                UpdateCamera();
            };
            MouseWheel += (s, e) =>
            {
                Dist = Math.Max(0.4, Math.Min(60, Dist * Math.Pow(0.9, e.Delta / 120.0)));
                UpdateCamera();
            };
        }

        /// <summary>offset added to the followed point by panning</summary>
        public Vector3D PanOffset;

        public string Overlay { set { overlay.Text = value ?? ""; } }

        public void ResetView()
        {
            Yaw = Math.PI * 0.82; Pitch = 0.18; Dist = 4.2;
            PanOffset = new Vector3D();
            Reset?.Invoke();
            UpdateCamera();
        }

        public event Action Reset;

        public void UpdateCamera()
        {
            Vector3D dir = new Vector3D(Math.Cos(Pitch) * Math.Sin(Yaw), Math.Sin(Pitch), Math.Cos(Pitch) * Math.Cos(Yaw));
            cam.Position = Target + dir * Dist;
            cam.LookDirection = -dir * Dist;
            cam.UpDirection = new Vector3D(0, 1, 0);
            Vector3D kd = -dir + new Vector3D(-0.35 * Math.Cos(Yaw), -0.55, 0.35 * Math.Sin(Yaw));
            keyLight.Direction = kd;
        }

        /// <summary>a 30 m square grid (1 m cells) centered at (x, z)</summary>
        public void BuildGround(double x, double z)
        {
            const double size = 30;
            MeshGeometry3D m = new MeshGeometry3D();
            double h = size / 2;
            m.Positions = new Point3DCollection { new Point3D(x - h, 0, z - h), new Point3D(x + h, 0, z - h), new Point3D(x + h, 0, z + h), new Point3D(x - h, 0, z + h) };
            m.TextureCoordinates = new PointCollection { new Point(0, 0), new Point(1, 0), new Point(1, 1), new Point(0, 1) };
            m.TriangleIndices = new Int32Collection { 0, 2, 1, 0, 3, 2 };
            m.Normals = new Vector3DCollection { new Vector3D(0, 1, 0), new Vector3D(0, 1, 0), new Vector3D(0, 1, 0), new Vector3D(0, 1, 0) };
            GeometryGroup cell = new GeometryGroup();
            cell.Children.Add(new RectangleGeometry(new Rect(0, 0, 100, 100)));
            DrawingGroup dg = new DrawingGroup();
            dg.Children.Add(new GeometryDrawing(new SolidColorBrush(Color.FromRgb(0x2B, 0x2C, 0x31)), null, new RectangleGeometry(new Rect(0, 0, 100, 100))));
            dg.Children.Add(new GeometryDrawing(null, new Pen(new SolidColorBrush(Color.FromRgb(0x3E, 0x40, 0x48)), 2), new RectangleGeometry(new Rect(0, 0, 100, 100))));
            DrawingBrush brush = new DrawingBrush(dg)
            {
                TileMode = TileMode.Tile,
                Viewport = new Rect(0, 0, 1.0 / size, 1.0 / size),
                ViewportUnits = BrushMappingMode.RelativeToBoundingBox,
                Stretch = Stretch.Fill
            };
            ground.Geometry = m;
            ground.Material = new EmissiveMaterial(brush);
            ground.BackMaterial = new EmissiveMaterial(brush);
        }

        private static Color PartColor(string name)
        {
            string n = (name ?? "").ToLowerInvariant();
            if (n.Contains("jersey") || n.Contains("shirt")) return Color.FromRgb(0x3E, 0x6F, 0xB8);
            if (n.Contains("short")) return Color.FromRgb(0xE6, 0xE6, 0xE6);
            if (n.Contains("sock")) return Color.FromRgb(0x3E, 0x6F, 0xB8);
            if (n.Contains("shoe") || n.Contains("boot")) return Color.FromRgb(0x30, 0x30, 0x34);
            if (n.Contains("hair")) return Color.FromRgb(0x3A, 0x2A, 0x20);
            if (n.Contains("glove")) return Color.FromRgb(0x50, 0x50, 0x55);
            return Color.FromRgb(0xC9, 0x9E, 0x7C);
        }

        public void SetParts(IList<MeshPart> list)
        {
            partsGroup.Children.Clear();
            parts.Clear();
            foreach (MeshPart m in list)
            {
                if (m == null || m.VertexCount == 0 || m.TriangleCount == 0) continue;
                MeshGeometry3D geo = new MeshGeometry3D { TriangleIndices = new Int32Collection(m.Triangles) };
                geo.TriangleIndices.Freeze();
                DiffuseMaterial dm = new DiffuseMaterial(new SolidColorBrush(PartColor(m.Source + " " + m.Name)));
                MaterialGroup mg = new MaterialGroup();
                mg.Children.Add(dm);
                mg.Children.Add(new SpecularMaterial(new SolidColorBrush(Color.FromArgb(0x50, 0xFF, 0xFF, 0xFF)), 40));
                GeometryModel3D gm = new GeometryModel3D(geo, mg) { BackMaterial = dm };
                partsGroup.Children.Add(gm);
                parts.Add(new Part { Mesh = m, Geo = geo, Model = gm, Diffuse = dm, Pos = new float[m.Positions.Length], Nrm = new float[m.Positions.Length] });
            }
            SetVisibility(ShowMesh, ShowBones);
        }

        public int PartCount { get { return parts.Count; } }

        public void SetVisibility(bool mesh, bool bonesOn)
        {
            ShowMesh = mesh;
            ShowBones = bonesOn;
            foreach (Part p in parts)
            {
                if (!partsGroup.Children.Contains(p.Model) && mesh) partsGroup.Children.Add(p.Model);
                if (partsGroup.Children.Contains(p.Model) && !mesh) partsGroup.Children.Remove(p.Model);
                p.Diffuse.Brush.Opacity = bonesOn ? 0.35 : 1.0;
            }
            if (!bonesOn) bones.Geometry = null;
        }

        /// <summary>skin the meshes and place the bones for the world transforms of the skeleton's bones</summary>
        public void SetPose(PlayerSkeleton sk, Affine[] world)
        {
            if (ShowMesh && parts.Count > 0)
            {
                mats = Skinning.Matrices(sk, world, mats);
                foreach (Part p in parts)
                {
                    Skinning.Apply(mats, p.Mesh, p.Pos, p.Nrm);
                    int nv = p.Mesh.VertexCount;
                    Point3DCollection pc = new Point3DCollection(nv);
                    Vector3DCollection nc = new Vector3DCollection(nv);
                    float[] a = p.Pos, n = p.Nrm;
                    for (int v = 0; v < nv; v++)
                    {
                        pc.Add(new Point3D(a[v * 3], a[v * 3 + 1], a[v * 3 + 2]));
                        nc.Add(new Vector3D(n[v * 3], n[v * 3 + 1], n[v * 3 + 2]));
                    }
                    pc.Freeze();
                    nc.Freeze();
                    p.Geo.Positions = pc;
                    p.Geo.Normals = nc;
                }
            }
            if (ShowBones || parts.Count == 0 || !ShowMesh) bones.Geometry = BoneMesh(sk, world);
            else bones.Geometry = null;
        }

        private static MeshGeometry3D BoneMesh(PlayerSkeleton sk, Affine[] world)
        {
            Point3DCollection pts = new Point3DCollection();
            Int32Collection idx = new Int32Collection();
            for (int b = 0; b < sk.Count; b++)
            {
                int p = sk.Parents[b];
                if (p < 0) continue;
                V3 a = world[p].Translation, c = world[b].Translation;
                V3 d = c - a;
                double len = d.Length;
                if (len < 1e-4 || len > 3) continue;
                V3 dir = d * (1 / len);
                V3 any = Math.Abs(dir.Y) < 0.9 ? new V3(0, 1, 0) : new V3(1, 0, 0);
                V3 u = dir.Cross(any).Normalized(), v = dir.Cross(u).Normalized();
                double r = Math.Min(0.018, Math.Max(0.003, len * 0.12));
                V3 m = a + d * 0.2;
                int i0 = pts.Count;
                pts.Add(new Point3D(a.X, a.Y, a.Z));
                pts.Add(new Point3D(c.X, c.Y, c.Z));
                V3[] ring = { m + u * r, m + v * r, m - u * r, m - v * r };
                foreach (V3 q in ring) pts.Add(new Point3D(q.X, q.Y, q.Z));
                for (int k = 0; k < 4; k++)
                {
                    int k1 = 2 + k, k2 = 2 + (k + 1) % 4;
                    idx.Add(i0); idx.Add(i0 + k2); idx.Add(i0 + k1);
                    idx.Add(i0 + 1); idx.Add(i0 + k1); idx.Add(i0 + k2);
                }
            }
            MeshGeometry3D g = new MeshGeometry3D { Positions = pts, TriangleIndices = idx };
            g.Freeze();
            return g;
        }
    }
}
