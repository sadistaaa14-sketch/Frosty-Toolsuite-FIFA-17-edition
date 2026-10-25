using System;

namespace FrostyAnimationEditor.Core.Anim
{
    /// <summary>3-component vector (double)</summary>
    public struct V3
    {
        public double X, Y, Z;
        public V3(double x, double y, double z) { X = x; Y = y; Z = z; }
        public static readonly V3 Zero = new V3(0, 0, 0);
        public static readonly V3 One = new V3(1, 1, 1);
        public static V3 operator +(V3 a, V3 b) { return new V3(a.X + b.X, a.Y + b.Y, a.Z + b.Z); }
        public static V3 operator -(V3 a, V3 b) { return new V3(a.X - b.X, a.Y - b.Y, a.Z - b.Z); }
        public static V3 operator -(V3 a) { return new V3(-a.X, -a.Y, -a.Z); }
        public static V3 operator *(V3 a, double s) { return new V3(a.X * s, a.Y * s, a.Z * s); }
        public static V3 operator *(double s, V3 a) { return new V3(a.X * s, a.Y * s, a.Z * s); }
        public double Dot(V3 b) { return X * b.X + Y * b.Y + Z * b.Z; }
        public V3 Cross(V3 b) { return new V3(Y * b.Z - Z * b.Y, Z * b.X - X * b.Z, X * b.Y - Y * b.X); }
        public double Length { get { return Math.Sqrt(X * X + Y * Y + Z * Z); } }
        public V3 Normalized() { double l = Length; return l > 0 ? this * (1.0 / l) : this; }
        public double this[int i] { get { return i == 0 ? X : i == 1 ? Y : Z; } }
        public static V3 Of(float[] v, double scale = 1.0) { return new V3(v[0] * scale, v[1] * scale, v[2] * scale); }
        public override string ToString() { return X.ToString("G6") + ", " + Y.ToString("G6") + ", " + Z.ToString("G6"); }
    }

    /// <summary>quaternion (x, y, z, w); a * b rotates by b first, then by a</summary>
    public struct Q4
    {
        public double X, Y, Z, W;
        public Q4(double x, double y, double z, double w) { X = x; Y = y; Z = z; W = w; }
        public static readonly Q4 Identity = new Q4(0, 0, 0, 1);

        public static Q4 operator *(Q4 a, Q4 b)
        {
            return new Q4(a.W * b.X + a.X * b.W + a.Y * b.Z - a.Z * b.Y,
                          a.W * b.Y - a.X * b.Z + a.Y * b.W + a.Z * b.X,
                          a.W * b.Z + a.X * b.Y - a.Y * b.X + a.Z * b.W,
                          a.W * b.W - a.X * b.X - a.Y * b.Y - a.Z * b.Z);
        }

        public double Dot(Q4 b) { return X * b.X + Y * b.Y + Z * b.Z + W * b.W; }
        public Q4 Neg() { return new Q4(-X, -Y, -Z, -W); }
        public Q4 Conj() { return new Q4(-X, -Y, -Z, W); }

        public Q4 Normalized()
        {
            double n = Math.Sqrt(X * X + Y * Y + Z * Z + W * W);
            return n > 0 ? new Q4(X / n, Y / n, Z / n, W / n) : Identity;
        }

        public static Q4 Of(float[] q) { return new Q4(q[0], q[1], q[2], q[3]); }
        public static Q4 Of(double[] q) { return new Q4(q[0], q[1], q[2], q[3]); }

        public static Q4 AxisAngle(V3 axis, double radians)
        {
            V3 a = axis.Normalized();
            double s = Math.Sin(radians / 2);
            return new Q4(a.X * s, a.Y * s, a.Z * s, Math.Cos(radians / 2));
        }

        /// <summary>3x3 rotation matrix, row-major, column-vector convention (p' = R p)</summary>
        public double[] ToMatrix()
        {
            Q4 q = Normalized();
            double x = q.X, y = q.Y, z = q.Z, w = q.W;
            return new[]
            {
                1 - 2 * (y * y + z * z), 2 * (x * y - w * z), 2 * (x * z + w * y),
                2 * (x * y + w * z), 1 - 2 * (x * x + z * z), 2 * (y * z - w * x),
                2 * (x * z - w * y), 2 * (y * z + w * x), 1 - 2 * (x * x + y * y)
            };
        }

        /// <summary>quaternion of a rotation matrix (row-major, column-vector convention; must be orthonormal)</summary>
        public static Q4 FromMatrix(double[] m)
        {
            double t = m[0] + m[4] + m[8];
            double x, y, z, w;
            if (t > 0)
            {
                double s = Math.Sqrt(t + 1.0) * 2;
                w = 0.25 * s; x = (m[7] - m[5]) / s; y = (m[2] - m[6]) / s; z = (m[3] - m[1]) / s;
            }
            else if (m[0] > m[4] && m[0] > m[8])
            {
                double s = Math.Sqrt(1.0 + m[0] - m[4] - m[8]) * 2;
                w = (m[7] - m[5]) / s; x = 0.25 * s; y = (m[1] + m[3]) / s; z = (m[2] + m[6]) / s;
            }
            else if (m[4] > m[8])
            {
                double s = Math.Sqrt(1.0 + m[4] - m[0] - m[8]) * 2;
                w = (m[2] - m[6]) / s; x = (m[1] + m[3]) / s; y = 0.25 * s; z = (m[5] + m[7]) / s;
            }
            else
            {
                double s = Math.Sqrt(1.0 + m[8] - m[0] - m[4]) * 2;
                w = (m[3] - m[1]) / s; x = (m[2] + m[6]) / s; y = (m[5] + m[7]) / s; z = 0.25 * s;
            }
            return new Q4(x, y, z, w).Normalized();
        }

        public V3 Rotate(V3 v)
        {
            double[] m = ToMatrix();
            return new V3(m[0] * v.X + m[1] * v.Y + m[2] * v.Z, m[3] * v.X + m[4] * v.Y + m[5] * v.Z, m[6] * v.X + m[7] * v.Y + m[8] * v.Z);
        }

        /// <summary>angle in radians between two orientations</summary>
        public static double Angle(Q4 a, Q4 b)
        {
            double d = Math.Abs(a.Normalized().Dot(b.Normalized()));
            return 2 * Math.Acos(Math.Min(1.0, d));
        }
    }

    /// <summary>
    /// Affine transform in the column-vector convention (p' = M p), stored as the top 3 rows of the 4x4 matrix:
    /// M[r, c] for r &lt; 3, c &lt; 3 is the linear part, M[r, 3] the translation. A * B applies B first.
    /// </summary>
    public struct Affine
    {
        public double M00, M01, M02, M03, M10, M11, M12, M13, M20, M21, M22, M23;

        public static readonly Affine Identity = new Affine { M00 = 1, M11 = 1, M22 = 1 };

        public V3 Translation
        {
            get { return new V3(M03, M13, M23); }
            set { M03 = value.X; M13 = value.Y; M23 = value.Z; }
        }

        public static Affine operator *(Affine a, Affine b)
        {
            Affine r;
            r.M00 = a.M00 * b.M00 + a.M01 * b.M10 + a.M02 * b.M20;
            r.M01 = a.M00 * b.M01 + a.M01 * b.M11 + a.M02 * b.M21;
            r.M02 = a.M00 * b.M02 + a.M01 * b.M12 + a.M02 * b.M22;
            r.M03 = a.M00 * b.M03 + a.M01 * b.M13 + a.M02 * b.M23 + a.M03;
            r.M10 = a.M10 * b.M00 + a.M11 * b.M10 + a.M12 * b.M20;
            r.M11 = a.M10 * b.M01 + a.M11 * b.M11 + a.M12 * b.M21;
            r.M12 = a.M10 * b.M02 + a.M11 * b.M12 + a.M12 * b.M22;
            r.M13 = a.M10 * b.M03 + a.M11 * b.M13 + a.M12 * b.M23 + a.M13;
            r.M20 = a.M20 * b.M00 + a.M21 * b.M10 + a.M22 * b.M20;
            r.M21 = a.M20 * b.M01 + a.M21 * b.M11 + a.M22 * b.M21;
            r.M22 = a.M20 * b.M02 + a.M21 * b.M12 + a.M22 * b.M22;
            r.M23 = a.M20 * b.M03 + a.M21 * b.M13 + a.M22 * b.M23 + a.M23;
            return r;
        }

        public V3 Apply(V3 p)
        {
            return new V3(M00 * p.X + M01 * p.Y + M02 * p.Z + M03, M10 * p.X + M11 * p.Y + M12 * p.Z + M13, M20 * p.X + M21 * p.Y + M22 * p.Z + M23);
        }

        public V3 ApplyDir(V3 d)
        {
            return new V3(M00 * d.X + M01 * d.Y + M02 * d.Z, M10 * d.X + M11 * d.Y + M12 * d.Z, M20 * d.X + M21 * d.Y + M22 * d.Z);
        }

        public double Det3 { get { return M00 * (M11 * M22 - M12 * M21) - M01 * (M10 * M22 - M12 * M20) + M02 * (M10 * M21 - M11 * M20); } }

        public Affine Inverse()
        {
            double d = Det3;
            if (d == 0) return Identity;
            double id = 1.0 / d;
            Affine r;
            r.M00 = (M11 * M22 - M12 * M21) * id;
            r.M01 = (M02 * M21 - M01 * M22) * id;
            r.M02 = (M01 * M12 - M02 * M11) * id;
            r.M10 = (M12 * M20 - M10 * M22) * id;
            r.M11 = (M00 * M22 - M02 * M20) * id;
            r.M12 = (M02 * M10 - M00 * M12) * id;
            r.M20 = (M10 * M21 - M11 * M20) * id;
            r.M21 = (M01 * M20 - M00 * M21) * id;
            r.M22 = (M00 * M11 - M01 * M10) * id;
            r.M03 = -(r.M00 * M03 + r.M01 * M13 + r.M02 * M23);
            r.M13 = -(r.M10 * M03 + r.M11 * M13 + r.M12 * M23);
            r.M23 = -(r.M20 * M03 + r.M21 * M13 + r.M22 * M23);
            return r;
        }

        public static Affine Translate(V3 t) { Affine a = Identity; a.M03 = t.X; a.M13 = t.Y; a.M23 = t.Z; return a; }
        public static Affine Scale(V3 s) { return new Affine { M00 = s.X, M11 = s.Y, M22 = s.Z }; }
        public static Affine Scale(double s) { return new Affine { M00 = s, M11 = s, M22 = s }; }

        public static Affine Rotate(Q4 q) { return FromRot3(q.ToMatrix(), V3.Zero); }

        public static Affine FromRot3(double[] m, V3 t)
        {
            return new Affine { M00 = m[0], M01 = m[1], M02 = m[2], M03 = t.X, M10 = m[3], M11 = m[4], M12 = m[5], M13 = t.Y, M20 = m[6], M21 = m[7], M22 = m[8], M23 = t.Z };
        }

        /// <summary>T * R * S</summary>
        public static Affine FromTRS(V3 t, Q4 r, V3 s)
        {
            double[] m = r.ToMatrix();
            return new Affine
            {
                M00 = m[0] * s.X, M01 = m[1] * s.Y, M02 = m[2] * s.Z, M03 = t.X,
                M10 = m[3] * s.X, M11 = m[4] * s.Y, M12 = m[5] * s.Z, M13 = t.Y,
                M20 = m[6] * s.X, M21 = m[7] * s.Y, M22 = m[8] * s.Z, M23 = t.Z
            };
        }

        /// <summary>
        /// A Frostbite LinearTransform (right, up, forward, trans), which is a row-vector matrix (p' = p M): its rows are
        /// the columns of the column-vector form.
        /// </summary>
        public static Affine FromLinearTransform(double rx, double ry, double rz, double ux, double uy, double uz,
                                                 double fx, double fy, double fz, double tx, double ty, double tz)
        {
            return new Affine { M00 = rx, M10 = ry, M20 = rz, M01 = ux, M11 = uy, M21 = uz, M02 = fx, M12 = fy, M22 = fz, M03 = tx, M13 = ty, M23 = tz };
        }

        public double[] Rot3()
        {
            return new[] { M00, M01, M02, M10, M11, M12, M20, M21, M22 };
        }

        /// <summary>translation, rotation, scale (scale per column; a negative determinant flips X)</summary>
        public void Decompose(out V3 t, out Q4 r, out V3 s)
        {
            t = Translation;
            V3 c0 = new V3(M00, M10, M20), c1 = new V3(M01, M11, M21), c2 = new V3(M02, M12, M22);
            double sx = c0.Length, sy = c1.Length, sz = c2.Length;
            if (Det3 < 0) sx = -sx;
            s = new V3(sx, sy, sz);
            if (sx == 0 || sy == 0 || sz == 0) { r = Q4.Identity; return; }
            c0 = c0 * (1 / sx); c1 = c1 * (1 / sy); c2 = c2 * (1 / sz);
            r = Q4.FromMatrix(new[] { c0.X, c1.X, c2.X, c0.Y, c1.Y, c2.Y, c0.Z, c1.Z, c2.Z });
        }

        /// <summary>the 16 doubles of an FBX matrix (column-major: translation in elements 12..14)</summary>
        public double[] ToFbx()
        {
            return new[] { M00, M10, M20, 0, M01, M11, M21, 0, M02, M12, M22, 0, M03, M13, M23, 1 };
        }

        public static Affine FromFbx(double[] m)
        {
            return new Affine { M00 = m[0], M10 = m[1], M20 = m[2], M01 = m[4], M11 = m[5], M21 = m[6], M02 = m[8], M12 = m[9], M22 = m[10], M03 = m[12], M13 = m[13], M23 = m[14] };
        }

        public double MaxDiff(Affine b)
        {
            double[] x = { M00 - b.M00, M01 - b.M01, M02 - b.M02, M03 - b.M03, M10 - b.M10, M11 - b.M11, M12 - b.M12, M13 - b.M13, M20 - b.M20, M21 - b.M21, M22 - b.M22, M23 - b.M23 };
            double m = 0;
            foreach (double v in x) m = Math.Max(m, Math.Abs(v));
            return m;
        }
    }

    /// <summary>Euler angles in degrees with FBX's rotation orders</summary>
    public static class Euler
    {
        /// <summary>FBX RotationOrder: 0 XYZ, 1 XZY, 2 YZX, 3 YXZ, 4 ZXY, 5 ZYX, 6 spheric (treated as XYZ).
        /// "XYZ" rotates about X first: R = Rz * Ry * Rx.</summary>
        public static readonly string[] Orders = { "XYZ", "XZY", "YZX", "YXZ", "ZXY", "ZYX", "XYZ" };

        private static double[] Axis(char a, double deg)
        {
            double r = deg * Math.PI / 180.0, c = Math.Cos(r), s = Math.Sin(r);
            if (a == 'X') return new[] { 1, 0, 0, 0, c, -s, 0, s, c };
            if (a == 'Y') return new[] { c, 0, s, 0, 1, 0, -s, 0, c };
            return new[] { c, -s, 0, s, c, 0, 0, 0, 1 };
        }

        public static double[] Mul3(double[] a, double[] b)
        {
            double[] r = new double[9];
            for (int i = 0; i < 3; i++)
                for (int j = 0; j < 3; j++)
                    r[i * 3 + j] = a[i * 3] * b[j] + a[i * 3 + 1] * b[3 + j] + a[i * 3 + 2] * b[6 + j];
            return r;
        }

        /// <summary>rotation matrix (row-major, column vectors) of Euler angles (x, y, z in degrees) applied in order</summary>
        public static double[] ToMatrix(V3 deg, int order)
        {
            string o = Orders[order < 0 || order >= Orders.Length ? 0 : order];
            double[] m = { 1, 0, 0, 0, 1, 0, 0, 0, 1 };
            foreach (char a in o) m = Mul3(Axis(a, a == 'X' ? deg.X : a == 'Y' ? deg.Y : deg.Z), m);
            return m;
        }

        public static Q4 ToQuat(V3 deg, int order) { return Q4.FromMatrix(ToMatrix(deg, order)); }

        /// <summary>XYZ Euler angles (degrees) of a rotation matrix; near gimbal lock z is set to 0</summary>
        public static V3 FromMatrixXYZ(double[] m)
        {
            double sy = -m[6];
            sy = Math.Max(-1.0, Math.Min(1.0, sy));
            double cy = Math.Sqrt(m[0] * m[0] + m[3] * m[3]);
            double y = Math.Atan2(sy, cy), x, z;
            if (cy > 1e-9)
            {
                x = Math.Atan2(m[7], m[8]);
                z = Math.Atan2(m[3], m[0]);
            }
            else
            {
                z = 0;
                x = sy > 0 ? Math.Atan2(m[1], m[4]) : Math.Atan2(-m[1], m[4]);
            }
            double k = 180.0 / Math.PI;
            return new V3(x * k, y * k, z * k);
        }

        private static double Wrap(double a, double near)
        {
            while (a - near > 180) a -= 360;
            while (a - near < -180) a += 360;
            return a;
        }

        /// <summary>the XYZ Euler triple equal to e (same rotation) that is closest to prev (continuous curves)</summary>
        public static V3 Closest(V3 e, V3 prev)
        {
            V3 a = new V3(Wrap(e.X, prev.X), Wrap(e.Y, prev.Y), Wrap(e.Z, prev.Z));
            V3 b = new V3(Wrap(e.X + 180, prev.X), Wrap(180 - e.Y, prev.Y), Wrap(e.Z + 180, prev.Z));
            double da = Math.Abs(a.X - prev.X) + Math.Abs(a.Y - prev.Y) + Math.Abs(a.Z - prev.Z);
            double db = Math.Abs(b.X - prev.X) + Math.Abs(b.Y - prev.Y) + Math.Abs(b.Z - prev.Z);
            return db < da ? b : a;
        }

        public static V3 FromQuatXYZ(Q4 q) { return FromMatrixXYZ(q.ToMatrix()); }

        /// <summary>Euler angles (degrees, x / y / z) of a rotation matrix for an FBX rotation order (0..5)</summary>
        public static V3 FromMatrix(double[] m, int order)
        {
            string o = Orders[order < 0 || order >= Orders.Length ? 0 : order];
            int i = o[0] - 'X', j = o[1] - 'X', k = o[2] - 'X';
            // R = R_k(c) R_j(b) R_i(a); even permutations of XYZ have the signs of the XYZ case
            bool even = (i == 0 && j == 1) || (i == 1 && j == 2) || (i == 2 && j == 0);
            double sg = even ? 1 : -1;
            double sb = Math.Max(-1.0, Math.Min(1.0, -sg * m[k * 3 + i]));
            double cb = Math.Sqrt(m[i * 3 + i] * m[i * 3 + i] + m[j * 3 + i] * m[j * 3 + i]);
            double b = Math.Atan2(sb, cb), a, c;
            if (cb > 1e-9)
            {
                a = Math.Atan2(sg * m[k * 3 + j], m[k * 3 + k]);
                c = Math.Atan2(sg * m[j * 3 + i], m[i * 3 + i]);
            }
            else
            {
                c = 0;
                a = Math.Atan2(-sg * m[j * 3 + k], m[j * 3 + j]);
            }
            double[] r = new double[3];
            r[i] = a * 180 / Math.PI; r[j] = b * 180 / Math.PI; r[k] = c * 180 / Math.PI;
            return new V3(r[0], r[1], r[2]);
        }
    }
}
