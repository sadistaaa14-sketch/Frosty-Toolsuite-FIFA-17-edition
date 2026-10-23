using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace FrostyAnimationEditor.Core
{
    /// <summary>
    /// The 153 native functions of FIFA17.exe's expression VM, from the game's code: single-precision arithmetic in the
    /// game's order of operations and lane layout, the game's SSE helpers (polynomial sine/cosine, DirectXMath-style
    /// atan/atan2, reciprocal square roots with two Newton steps, the 4x4 inverse) and its branch conditions as
    /// COMISS/JA/JBE evaluate them, NaNs included. Natives that call the C library use CrtMath.
    /// </summary>
    public static partial class KernelNatives
    {
        public delegate void Fn(Machine M, List<uint> i, List<uint> o, List<uint> s);

        private static readonly Dictionary<long, Fn> natives = new Dictionary<long, Fn>();

        public static Fn Get(long fn) { Fn f; return natives.TryGetValue(fn, out f) ? f : null; }

        public static int Count { get { return natives.Count; } }

        private static void Reg(long a, Fn f) { natives[a] = f; }

        // ------------------------------------------------------------------ float32 helpers (numpy semantics)
        private const float TWO_PI = (float)6.2831855, INV_2PI = (float)0.15915494, EPS = (float)1.5258789e-05;
        private const float FLT_EPS = (float)1.1920929e-07;

        private static bool Finite(float x) { return !float.IsNaN(x) && !float.IsInfinity(x); }

        /// <summary>floorf as modelled (Python's math.floor: an integer, so a zero result is +0); inf and NaN come back unchanged</summary>
        public static float Floorf(float x) { if (!Finite(x)) return x; float r = (float)Math.Floor(x); return r == 0f ? 0f : r; }

        public static float Ceilf(float x) { if (!Finite(x)) return x; float r = (float)Math.Ceiling(x); return r == 0f ? 0f : r; }

        /// <summary>np.abs: the sign bit cleared</summary>
        public static float Fabs(float x) { return BE.BitsFloat(BE.FloatBits(x) & 0x7FFFFFFF); }

        /// <summary>SQRTSS: correctly rounded, NaN for negative arguments</summary>
        public static float Sqrtf(float x) { return (float)Math.Sqrt(x); }

        /// <summary>1 / dt unless |dt - 0| &lt; 1.5258789e-05 (then 1); COMISS eps, |dt|; JA, so a NaN dt gives NaN</summary>
        private static float InvDt(float dt) { return EPS > Fabs(dt - 0f) ? 1f : 1f / dt; }

        private static float FlipSign(float x) { return BE.BitsFloat(BE.FloatBits(x) ^ 0x80000000); }

        private static long F2I(float x) { return Finite(x) && x >= -2147483648f && x < 2147483648f ? (long)(int)x : -0x80000000L; }

        private static F4 Abs(F4 a) { return new F4(Fabs(a.X), Fabs(a.Y), Fabs(a.Z), Fabs(a.W)); }

        private static F4 Map(F4 a, F4 b, Func<float, float, float> f) { return new F4(f(a.X, b.X), f(a.Y, b.Y), f(a.Z, b.Z), f(a.W, b.W)); }

        /// <summary>np.where(a OP b, a, b) style lane selects</summary>
        private static F4 WhereGt(F4 c1, F4 c2, F4 x, F4 y)
        {
            return new F4(c1.X > c2.X ? x.X : y.X, c1.Y > c2.Y ? x.Y : y.Y, c1.Z > c2.Z ? x.Z : y.Z, c1.W > c2.W ? x.W : y.W);
        }

        private static F4 WhereLt(F4 c1, F4 c2, F4 x, F4 y)
        {
            return new F4(c1.X < c2.X ? x.X : y.X, c1.Y < c2.Y ? x.Y : y.Y, c1.Z < c2.Z ? x.Z : y.Z, c1.W < c2.W ? x.W : y.W);
        }

        private static F4 V(params float[] v) { return F4.Of(v); }

        /// <summary>the game's angle wrap: t = x / 2pi - floor(x / 2pi), t -= 1 if t &gt; 0.5, result t * 2pi</summary>
        private static float Wrap(float x)
        {
            float t = x * INV_2PI;
            t = t - Floorf(t);
            if (t > 0.5f) t = t + -1f;
            return t * TWO_PI;
        }

        private static float WrapT(float x)
        {
            float t = x * INV_2PI;
            t = t - Floorf(t);
            if (t > 0.5f) t = t + -1f;
            return t;
        }

        private static float Acos(float x) { return (float)CrtMath.Acosf(x); }
        private static float Asin(float x) { return (float)CrtMath.Asinf(x); }
        private static float Atan2(float y, float x) { return (float)CrtMath.Atan2f(y, x); }

        /// <summary>horizontal sums as the game's SSE code does them (PSHUFD + ADDPS)</summary>
        private static float Sum(F4 p, int n)
        {
            if (n == 2) return p.Y + p.X;
            if (n == 3) return (p.Z + p.Y) + p.X;
            return (p.W + p.Y) + (p.Z + p.X);
        }

        private static float Dot(F4 a, F4 b, int n) { return Sum(a * b, n); }

        private static long CDiv(long a, long b)
        {
            if (b == 0) throw new VMError("integer division by zero (the game would crash)");
            long q = Math.Abs(a) / Math.Abs(b);
            return (a < 0) == (b < 0) ? q : -q;
        }

        private static F4 Qmul(F4 a, F4 b)
        {
            float x1 = a.X, y1 = a.Y, z1 = a.Z, w1 = a.W, x2 = b.X, y2 = b.Y, z2 = b.Z, w2 = b.W;
            return new F4(((z2 * y1 - y2 * z1) + w1 * x2) + w2 * x1,
                          ((x2 * z1 - z2 * x1) + w1 * y2) + w2 * y1,
                          ((y2 * x1 - x2 * y1) + w1 * z2) + w2 * z1,
                          w2 * w1 - ((z2 * z1 + y2 * y1) + x2 * x1));
        }

        // the game's polynomial cosine (FUN_146b7b2b0, FUN_146b83da0, ...)
        private static readonly float[] Cc = { (float)6.2831855, (float)-41.3417, (float)81.60525, (float)-76.70586, (float)42.058693, (float)-15.094643,
                                               (float)3.8199525, (float)-0.7181223, (float)0.10422916, (float)-0.012031586 };
        private const float MAGIC = (float)12582912.0;

        /// <summary>the game's polynomial cos(a) for a &gt;= 0, clamped to [-1, 1]</summary>
        private static float GcosAbs(float a)
        {
            float t = a * (float)-0.15915494;
            float f = (((t - MAGIC) + MAGIC) - t) - 0.5f;
            float nf = f * -1f;
            float g = (nf > f ? nf : f) - 0.25f;
            float g2 = g * g;
            float p5 = g * g2 * g2;
            float p7 = p5 * g2;
            float p9 = p7 * g2;
            float p11 = p9 * g2;
            float p13 = p11 * g2;
            float p15 = p13 * g2;
            float p17 = p15 * g2;
            float r = g * Cc[0] + g * g2 * Cc[1] + p5 * Cc[2] + p7 * Cc[3] + p9 * Cc[4] + p11 * Cc[5] + p13 * Cc[6] + p15 * Cc[7] + p17 * Cc[8] + g2 * p17 * Cc[9];
            r = 1f < r ? 1f : r;              // minps(1, r)
            return -1f > r ? -1f : r;         // maxps(-1, r)
        }

        /// <summary>(sin h, cos h) the way the game evaluates them</summary>
        private static void Gsincos(float h, out float sn, out float cs)
        {
            float u = h - (float)1.5707964;
            float nu = u * -1f;
            float nh = h * -1f;
            sn = GcosAbs(nu > u ? nu : u);
            cs = GcosAbs(nh > h ? nh : h);
        }

        private static F4 AxisQuat(int axis, float angle)
        {
            float sn, cs;
            Gsincos(angle * 0.5f, out sn, out cs);
            F4 q = new F4(sn * 0f, sn * 0f, sn * 0f, cs);
            q[axis] = sn * 1f;
            return q;
        }

        /// <summary>FUN_146b85b20's rotation of v by q (the 0 / 1 factors kept, they decide signed zeros)</summary>
        private static F4 Rot(F4 q, float vx, float vy, float vz)
        {
            float x = q.X, y = q.Y, z = q.Z, w = q.W;
            float a = (y * vz - z * vy) + w * vx;
            float b = (z * vx - x * vz) + w * vy;
            float c = (x * vy - y * vx) + w * vz;
            return new F4((c * y - b * z) * 2f + vx, (a * z - c * x) * 2f + vy, (b * x - a * y) * 2f + vz, (a * x - a * x) * 2f + 0f);
        }

        // ------------------------------------------------------------------ registrations
        static KernelNatives()
        {
            Scalars();
            Integers();
            Vectors();
            Quaternions();
            Matrices();
            GameState();
            Filters();
            Transforms();
            More();
            Simd();
        }

        private static void Scalars()
        {
            Reg(0x146B83160, (M, i, o, s) => M.Mem.WF(o[0], M.Mem.F(i[1]) + M.Mem.F(i[0])));
            Reg(0x146B86210, (M, i, o, s) => M.Mem.WF(o[0], M.Mem.F(i[0]) - M.Mem.F(i[1])));
            Reg(0x146B854C0, (M, i, o, s) => M.Mem.WF(o[0], M.Mem.F(i[1]) * M.Mem.F(i[0])));
            Reg(0x146B83B30, (M, i, o, s) => M.Mem.WF(o[0], M.Mem.F(i[0]) / M.Mem.F(i[1])));
            Reg(0x146B83060, (M, i, o, s) => M.Mem.WI(o[0], M.Mem.U32(i[0]) & 0x7FFFFFFF));
            Reg(0x146B84FB0, (M, i, o, s) => { float a = M.Mem.F(i[0]), b = M.Mem.F(i[1]); M.Mem.WF(o[0], b < a ? a : b); });
            Reg(0x146B85010, (M, i, o, s) => { float a = M.Mem.F(i[0]), b = M.Mem.F(i[1]); M.Mem.WF(o[0], b < a ? b : a); });
            Reg(0x146B83830, (M, i, o, s) =>
            {
                // the bounds in either order (COMISS / JA as the game branches: a NaN x gives the upper bound)
                float x = M.Mem.F(i[0]), a = M.Mem.F(i[1]), b = M.Mem.F(i[2]);
                float hi = a > b ? a : b;
                float lo = a > b ? b : a;
                float r = lo > x ? lo : x;
                M.Mem.WF(o[0], hi > r ? r : hi);
            });
            Reg(0x146681F30, (M, i, o, s) => { float a = M.Mem.F(i[0]), b = M.Mem.F(i[1]), t = M.Mem.F(i[2]); M.Mem.WF(o[0], (b - a) * t + a); });
            Reg(0x146B85FA0, (M, i, o, s) =>
            {
                VmMem m = M.Mem;
                float x = m.F(i[0]), a = m.F(i[1]), b = m.F(i[2]), c = m.F(i[3]), d = m.F(i[4]);
                float r = b - a;
                if (Fabs(r) <= EPS) { m.WF(o[0], c); return; }
                float t = (x - a) / r;
                float u = 0f > t ? 0f : (1f > t ? t : 1f);
                m.WF(o[0], (d - c) * u + c);
            });
            Reg(0x146B85040, (M, i, o, s) =>
            {
                float a = M.Mem.F(i[0]), b = M.Mem.F(i[1]);
                float q = a / b;
                M.Mem.WF(o[0], a - (float)F2I(q) * b);
            });
            Reg(0x146B86120, (M, i, o, s) => M.Mem.WF(o[0], (float)CrtMath.Sqrtf(M.Mem.F(i[0]))));
            Reg(0x146B84290, (M, i, o, s) => M.Mem.WF(o[0], (float)CrtMath.Expf(M.Mem.F(i[0]))));
            Reg(0x146B84690, (M, i, o, s) => M.Mem.WF(o[0], (float)CrtMath.Logf(M.Mem.F(i[0]))));
            Reg(0x146B85CA0, (M, i, o, s) => M.Mem.WF(o[0], (float)CrtMath.Powf(M.Mem.F(i[0]), M.Mem.F(i[1]))));
            Reg(0x146B836C0, (M, i, o, s) => M.Mem.WF(o[0], Acos(M.Mem.F(i[0]))));
            Reg(0x146B83930, (M, i, o, s) => M.Mem.WF(o[0], M.Mem.F(i[0]) * (float)57.29578));
            Reg(0x146B85E70, (M, i, o, s) => M.Mem.WF(o[0], M.Mem.F(i[0]) * (float)0.017453292));
            Reg(0x146B855B0, (M, i, o, s) => M.Mem.WF(o[0], Wrap(M.Mem.F(i[0]))));
            Reg(0x146B85610, (M, i, o, s) =>
            {
                float r = Wrap(M.Mem.F(i[0]));
                if (r < 0f) r = r + TWO_PI;
                M.Mem.WF(o[0], r);
            });
            Reg(0x146B83470, (M, i, o, s) =>
            {
                float a = M.Mem.F(i[0]), b = M.Mem.F(i[1]);
                float t = (WrapT(a) * TWO_PI - WrapT(b) * TWO_PI) * INV_2PI;
                t = t - Floorf(t);
                if (t > 0.5f) t = t + -1f;
                M.Mem.WF(o[0], t * TWO_PI);
            });
            Reg(0x146B83430, (M, i, o, s) =>
            {
                float a = M.Mem.F(i[0]) * (float)0.017453292 * INV_2PI;
                a = a - Floorf(a);
                if (a > 0.5f) a = a + -1f;
                float b = M.Mem.F(i[1]) * (float)0.017453292 * INV_2PI;
                b = b - Floorf(b);
                if (b > 0.5f) b = b + -1f;
                float t = (a * TWO_PI - b * TWO_PI) * INV_2PI;
                t = t - Floorf(t);
                if (t > 0.5f) t = t + -1f;
                M.Mem.WF(o[0], t * TWO_PI * (float)57.29578);
            });
            Reg(0x146CDA3A0, (M, i, o, s) =>
            {
                float step = M.Mem.F(i[1]);
                step = step > (float)1e-08 ? step : (float)1e-08;
                float x = M.Mem.F(i[0]);
                float t = x * INV_2PI;
                t = t - Floorf(t);
                if (t > 0.5f) t = t + -1f;
                float sg = x >= 0f ? 1f : -1f;        // COMISS x, 0; JC: NaN counts as negative
                float n = Floorf(Fabs(t * TWO_PI) / step + 0.5f);
                float v = n * sg * step * INV_2PI;
                v = v - Floorf(v);
                if (v > 0.5f) v = v + -1f;
                M.Mem.WF(o[0], v * TWO_PI);
            });
            Reg(0x146CDA3E0, (M, i, o, s) =>
            {
                F4 v = M.Mem.V(i[0]);
                float r = (float)CrtMath.Atan2f(0f - v.Z, v.X);
                if (0f <= r - (float)3.1415927) r = (float)-3.1415927;
                M.Mem.WF(o[0], r);
            });
            Reg(0x146B83710, (M, i, o, s) =>
            {
                float a = M.Mem.F(i[0]), b = M.Mem.F(i[1]);
                float l2 = b * b + a * a;
                if (Fabs(l2 - 0f) < EPS) { M.Mem.WF(o[0], 0f); return; }
                float r = Sqrtf(l2);
                float inv = 1f / r;
                M.Mem.WF(o[0], (float)CrtMath.Atan2f(inv * a, inv * b));
            });
            Reg(0x146B781B0, (M, i, o, s) =>
            {
                float a = M.Mem.F(i[0]), b = M.Mem.F(i[1]);
                float c = (float)0.1 > a ? (float)0.1 : ((float)179.0 > a ? a : (float)179.0);
                float t = (float)CrtMath.Tanf((c * (float)3.1415927 * 0.5f) / 180f);
                M.Mem.WF(o[0], b / (t * 2f));
            });
            Reg(0x146B78230, (M, i, o, s) =>
            {
                float a = M.Mem.F(i[0]);
                a = a > 1f ? a : 1f;
                float r = (float)CrtMath.Atanf(M.Mem.F(i[1]) / (a * 2f));
                M.Mem.WF(o[0], r * 360f * (float)0.31830987);
            });
        }

        private static void Integers()
        {
            Reg(0x146B83180, (M, i, o, s) => M.Mem.WI(o[0], (long)M.Mem.I(i[1]) + M.Mem.I(i[0])));
            Reg(0x146B86230, (M, i, o, s) => M.Mem.WI(o[0], (long)M.Mem.I(i[0]) - M.Mem.I(i[1])));
            Reg(0x146B854E0, (M, i, o, s) => M.Mem.WI(o[0], (long)M.Mem.I(i[1]) * M.Mem.I(i[0])));
            Reg(0x146B83B70, (M, i, o, s) => M.Mem.WI(o[0], CDiv(M.Mem.I(i[0]), M.Mem.I(i[1]))));
            Reg(0x146B85090, (M, i, o, s) => { long a = M.Mem.I(i[0]), b = M.Mem.I(i[1]); M.Mem.WI(o[0], a - CDiv(a, b) * b); });
            Reg(0x146B83090, (M, i, o, s) => M.Mem.WI(o[0], Math.Abs((long)M.Mem.I(i[0]))));
            Reg(0x146B83890, (M, i, o, s) =>
            {
                int x = M.Mem.I(i[0]), lo = M.Mem.I(i[1]), hi = M.Mem.I(i[2]);
                if (x < lo) { M.Mem.WI(o[0], lo); return; }
                M.Mem.WI(o[0], hi < x ? hi : x);
            });
            Reg(0x146B86040, (M, i, o, s) => M.Mem.WI(o[0], F2I(Floorf(M.Mem.F(i[0]) + 0.5f))));
            Reg(0x146B842C0, (M, i, o, s) => M.Mem.WI(o[0], F2I(Floorf(M.Mem.F(i[0])))));
            Reg(0x146B837C0, (M, i, o, s) => M.Mem.WI(o[0], F2I(Ceilf(M.Mem.F(i[0])))));

            // comparisons
            Func<Func<float, float, bool>, Fn> cf = fn => (M, i, o, s) => M.Mem.WB(o[0], fn(M.Mem.F(i[0]), M.Mem.F(i[1])));
            Func<Func<int, int, bool>, Fn> ci = fn => (M, i, o, s) => M.Mem.WB(o[0], fn(M.Mem.I(i[0]), M.Mem.I(i[1])));
            Func<Func<byte, byte, bool>, Fn> cb = fn => (M, i, o, s) => M.Mem.WB(o[0], fn(M.Mem.B(i[0]), M.Mem.B(i[1])));
            Reg(0x146B84600, cf((a, b) => a < b));
            Reg(0x146B84640, cf((a, b) => a <= b));
            Reg(0x146B84310, cf((a, b) => a > b));
            Reg(0x146B84370, cf((a, b) => a >= b));
            Reg(0x146B84620, ci((a, b) => a < b));
            Reg(0x146B84670, ci((a, b) => a <= b));
            Reg(0x146B84340, ci((a, b) => a > b));
            Reg(0x146B843A0, ci((a, b) => a >= b));
            Reg(0x146B83D60, ci((a, b) => a == b));
            Reg(0x146B85A80, ci((a, b) => a != b));
            Reg(0x146B83C40, cb((a, b) => a == b));
            Reg(0x146B85980, cb((a, b) => a != b));
            Reg(0x146B83D30, cf((a, b) => Fabs(a - b) < EPS));
            Reg(0x146B85A30, cf((a, b) => !(EPS > Fabs(a - b))));      // SETBE

            Reg(0x146B86310, (M, i, o, s) => M.Mem.Write(o[0], new[] { (byte)(M.Mem.B(i[0]) ^ M.Mem.B(i[1])) }));
            Reg(0x146B831A0, (M, i, o, s) => M.Mem.WB(o[0], M.Mem.B(i[0]) != 0 && M.Mem.B(i[1]) != 0));
            Reg(0x146B85AF0, (M, i, o, s) => M.Mem.WB(o[0], !(M.Mem.B(i[0]) == 0 && M.Mem.B(i[1]) == 0)));
            Reg(0x146B85AE0, (M, i, o, s) => M.Mem.WB(o[0], M.Mem.B(i[0]) == 0));
        }

        private static float Nr2(float r, float q)
        {
            r = (1f - r * r * q) * r * 0.5f + r;
            r = (1f - r * r * q) * r * 0.5f + r;
            return r;
        }

        private static void Vectors()
        {
            Func<Func<F4, F4, F4>, Fn> vbin = fn => (M, i, o, s) => M.Mem.WV(o[0], fn(M.Mem.V(i[0]), M.Mem.V(i[1])));
            Reg(0x146B830E0, vbin((a, b) => b + a));
            Reg(0x146B86150, vbin((a, b) => a - b));
            Reg(0x146B851F0, vbin((a, b) => b * a));
            Reg(0x146B84F90, vbin((a, b) => WhereGt(a, b, a, b)));      // maxps(a, b): b unless a > b
            Reg(0x146B84FF0, vbin((a, b) => WhereLt(a, b, a, b)));      // minps(a, b)
            Reg(0x146B83AA0, (M, i, o, s) => M.Mem.WV(o[0], M.Mem.V(i[0]) / M.Mem.V(i[1])));
            Reg(0x146B83AC0, (M, i, o, s) => M.Mem.WV(o[0], M.Mem.V(i[0]) / M.Mem.F(i[1])));
            Reg(0x146B83B00, (M, i, o, s) => { float r = 1f / M.Mem.F(i[1]); M.Mem.WV(o[0], r * M.Mem.V(i[0])); });
            Reg(0x146B85470, (M, i, o, s) => M.Mem.WV(o[0], M.Mem.F(i[1]) * M.Mem.V(i[0])));
            Reg(0x146B85490, (M, i, o, s) => M.Mem.WV(o[0], M.Mem.F(i[0]) * M.Mem.V(i[1])));
            Reg(0x146681F00, (M, i, o, s) => { F4 a = M.Mem.V(i[0]), b = M.Mem.V(i[1]); float t = M.Mem.F(i[2]); M.Mem.WV(o[0], (b - a) * t + a); });
            Reg(0x146B83020, (M, i, o, s) => { F4 a = M.Mem.V(i[0]); F4 n = 0f - a; M.Mem.WV(o[0], WhereGt(a, n, a, n)); });
            Reg(0x146B83040, (M, i, o, s) => { F4 a = M.Mem.V(i[0]); F4 n = a * -1f; M.Mem.WV(o[0], WhereGt(n, a, n, a)); });
            Reg(0x146B837F0, (M, i, o, s) =>
            {
                F4 x = M.Mem.V(i[0]), a = M.Mem.V(i[1]), b = M.Mem.V(i[2]);
                F4 lo = WhereLt(a, b, a, b);
                F4 hi = WhereGt(a, b, a, b);
                F4 r = WhereGt(x, lo, x, lo);
                M.Mem.WV(o[0], WhereLt(r, hi, r, hi));
            });
            Reg(0x146B83B90, (M, i, o, s) => M.Mem.WF(o[0], Dot(M.Mem.V(i[1]), M.Mem.V(i[0]), 2)));
            Reg(0x146B83BC0, (M, i, o, s) => M.Mem.WF(o[0], Dot(M.Mem.V(i[1]), M.Mem.V(i[0]), 3)));
            Reg(0x146B83C00, (M, i, o, s) => M.Mem.WF(o[0], Dot(M.Mem.V(i[1]), M.Mem.V(i[0]), 4)));
            Func<int, Fn> dist = n => (M, i, o, s) =>
            {
                F4 a = M.Mem.V(i[0]), b = M.Mem.V(i[1]);
                F4 d = b - a;
                float q = Sum(d * d, n);
                M.Mem.WF(o[0], Fabs(q - 0f) < EPS ? 0f : Sqrtf(q));
            };
            Reg(0x146B83950, dist(2));
            Reg(0x146B839C0, dist(3));
            Reg(0x146B83A30, dist(4));
            Func<int, Fn> len = n => (M, i, o, s) =>
            {
                F4 a = M.Mem.V(i[0]);
                bool small = true;
                for (int j = 0; j < n; j++) if (!(Fabs(a[j]) <= FLT_EPS)) small = false;
                if (small) { M.Mem.WF(o[0], 0f); return; }
                float q = Sum(a * a, n);
                M.Mem.WF(o[0], Sqrtf(q));
            };
            Reg(0x146B846D0, len(2));
            Reg(0x146B84720, len(3));
            Reg(0x146B84780, len(4));
            Func<int, bool, Fn> normalize = (n, check) => (M, i, o, s) =>
            {
                F4 a = M.Mem.V(i[0]);
                if (check)
                {
                    bool small = true;
                    for (int j = 0; j < n; j++) if (!(Fabs(a[j]) <= FLT_EPS)) small = false;
                    if (small) { M.Mem.WV(o[0], new F4(0f, 0f, 0f, 0f)); return; }
                }
                float q = Sum(a * a, n);
                float r = 1f / Sqrtf(q);
                r = Nr2(r, q);
                M.Mem.WV(o[0], a * r);
            };
            Reg(0x146B85690, normalize(2, true));
            Reg(0x146B85750, normalize(3, true));
            Reg(0x146B85810, normalize(4, true));
            Reg(0x146B858E0, normalize(4, false));
            Func<int, bool, Fn> approx = (n, eq) => (M, i, o, s) =>
            {
                F4 d = Abs(M.Mem.V(i[0]) - M.Mem.V(i[1]));
                bool ok = true;
                for (int j = 0; j < n; j++) if (!(d[j] <= EPS)) ok = false;
                M.Mem.WB(o[0], eq ? ok : !ok);
            };
            Reg(0x146B83C60, approx(2, true));
            Reg(0x146B83CA0, approx(3, true));
            Reg(0x146B83CE0, approx(4, true));
            Reg(0x146B859A0, approx(2, false));
            Reg(0x146B859F0, approx(3, false));
            Reg(0x146B85AA0, approx(4, false));
            Reg(0x146B83900, (M, i, o, s) =>
            {
                F4 a = M.Mem.V(i[0]), b = M.Mem.V(i[1]);
                M.Mem.WV(o[0], new F4(b.Z * a.Y - b.Y * a.Z, b.X * a.Z - b.Z * a.X, b.Y * a.X - b.X * a.Y, b.X * a.X - b.X * a.X));
            });
            Reg(0x146B831D0, (M, i, o, s) =>
            {
                F4 a = M.Mem.V(i[0]), b = M.Mem.V(i[1]), c = M.Mem.V(i[2]);
                F4 u = c - a, w = c - b;
                Func<F4, F4> nrm = x =>
                {
                    float q = Sum(x * x, 3);
                    float r = 1f / Sqrtf(q);
                    r = Nr2(r, q);
                    return x * r;
                };
                F4 nu = nrm(u), nw = nrm(w);
                float d = (nu.Z * nw.Z + nu.Y * nw.Y) + nu.X * nw.X;
                M.Mem.WF(o[0], Acos(d));
            });
            Func<int, Fn> vacos = n => (M, i, o, s) =>
            {
                F4 v = M.Mem.V(i[0]);
                F4 a = new F4(Acos(v.X), Acos(v.Y), Acos(v.Z), Acos(v.W));
                if (n == 2) a = new F4(a.X, a.Y, a.X, a.X);
                else if (n == 3) a = new F4(a.X, a.Y, a.Z, a.X);
                M.Mem.WV(o[0], a);
            };
            Reg(0x146B83540, vacos(2));
            Reg(0x146B835A0, vacos(3));
            Reg(0x146B83630, vacos(4));
            Reg(0x146B86070, (M, i, o, s) =>
            {
                double x = M.Mem.F(i[0]);
                float sn = (float)CrtMath.Sinf(x), cs = (float)CrtMath.Cosf(x);
                M.Mem.WF(o[0], sn);
                M.Mem.WF(o[1], cs);
                M.Mem.WV(o[2], new F4(cs, sn, cs, cs));
            });
            Reg(0x146B84F50, (M, i, o, s) =>
            {
                // FUN_146b84c70: rotation matrix about Y (rows (c, 0, -s, 0), (0, 1, 0, 0), (s, 0, c, 0), (0, 0, 0, 1))
                float x = M.Mem.F(i[0]);
                float sn, cs;
                Gsincos(x, out sn, out cs);
                M.Mem.WM(o[0], new[] { cs, 0f, 0f - sn, 0f, 0f, 1f, 0f, 0f, sn, 0f, cs, 0f, 0f, 0f, 0f, 1f });
            });
        }

        private static void Quaternions()
        {
            Reg(0x146B85510, (M, i, o, s) => M.Mem.WV(o[0], Qmul(M.Mem.V(i[0]), M.Mem.V(i[1]))));
            Reg(0x146B838D0, (M, i, o, s) => { F4 q = M.Mem.V(i[0]); M.Mem.WV(o[0], new F4(-q.X, -q.Y, -q.Z, q.W)); });
            Reg(0x146B84590, (M, i, o, s) =>
            {
                F4 q = M.Mem.V(i[0]);
                float n = Sum(q * q, 4);
                float r = 1f / n;
                r = (2f - r * n) * r;
                r = (2f - r * n) * r;
                M.Mem.WV(o[0], new F4(r * -q.X, r * -q.Y, r * -q.Z, r * q.W));
            });
            Reg(0x146B86290, (M, i, o, s) =>
            {
                F4 v = M.Mem.V(i[0]), q = M.Mem.V(i[1]);
                float x = v.X, y = v.Y, z = v.Z, w = v.W, qx = q.X, qy = q.Y, qz = q.Z, qw = q.W;
                float a = (z * qy - y * qz) + qw * x;
                float b = (x * qz - z * qx) + qw * y;
                float c = (y * qx - x * qy) + qw * z;
                M.Mem.WV(o[0], new F4((c * qy - b * qz) * 2f + x, (a * qz - c * qx) * 2f + y, (b * qx - a * qy) * 2f + z, (a * qx - a * qx) * 2f + w));
            });
            Func<int, Fn> qaxis = axis => (M, i, o, s) =>
            {
                float x = M.Mem.F(i[0]);
                float sn, cs;
                Gsincos(x * 0.5f, out sn, out cs);
                F4 q = new F4(sn * 0f, sn * 0f, sn * 0f, cs);
                q[axis] = sn * 1f;
                M.Mem.WV(o[0], q);
                M.Mem.WF(o[1], Wrap(x));
            };
            Reg(0x146B7B280, qaxis(0));
            Reg(0x146B7B610, qaxis(1));
            Reg(0x146B7B9A0, qaxis(2));
            Reg(0x146B84130, (M, i, o, s) =>
            {
                F4 v = M.Mem.V(i[0]);
                float sx, cx, sy, cy, sz, cz;
                Gsincos(v.X * 0.5f, out sx, out cx);
                Gsincos(v.Y * 0.5f, out sy, out cy);
                Gsincos(v.Z * 0.5f, out sz, out cz);
                M.Mem.WV(o[0], new F4(cy * (cz * sx) - sy * (sz * cx), sy * (cz * cx) + cy * (sz * sx), cy * (sz * cx) - sy * (cz * sx), cy * (cz * cx) + sy * (sz * sx)));
            });
            Reg(0x146B85C30, (M, i, o, s) =>
            {
                F4 q = M.Mem.V(i[0]);
                M.Mem.WV(o[0], Rot(q, 1f, 0f, 0f));
                M.Mem.WV(o[1], Rot(q, 0f, 0f, 1f));
                M.Mem.WV(o[2], Rot(q, 0f, 1f, 0f));
            });
        }

        private static float[] Add(float[] a, float[] b) { float[] r = new float[a.Length]; for (int j = 0; j < a.Length; j++) r[j] = a[j] + b[j]; return r; }
        private static float[] Sub(float[] a, float[] b) { float[] r = new float[a.Length]; for (int j = 0; j < a.Length; j++) r[j] = a[j] - b[j]; return r; }

        private static void Matrices()
        {
            Reg(0x146B830B0, (M, i, o, s) => M.Mem.WM(o[0], Add(M.Mem.M(i[1], 12), M.Mem.M(i[0], 12))));
            Reg(0x146B83110, (M, i, o, s) => M.Mem.WM(o[0], Add(M.Mem.M(i[1], 16), M.Mem.M(i[0], 16))));
            Reg(0x146B86180, (M, i, o, s) => M.Mem.WM(o[0], Sub(M.Mem.M(i[0], 12), M.Mem.M(i[1], 12))));
            Reg(0x146B861C0, (M, i, o, s) => M.Mem.WM(o[0], Sub(M.Mem.M(i[0], 16), M.Mem.M(i[1], 16))));
            Reg(0x146B86250, (M, i, o, s) =>
            {
                F4 p = M.Mem.V(i[0]);
                float[] m = M.Mem.M(i[1], 16);
                F4 r = new F4();
                for (int c = 0; c < 4; c++) r[c] = ((p.X * m[c] + m[12 + c]) + p.Y * m[4 + c]) + m[8 + c] * p.Z;
                M.Mem.WV(o[0], r);
            });
            Reg(0x146B860E0, (M, i, o, s) =>
            {
                byte[] b = M.Mem.Read(i[0], 64);
                for (int j = 0; j < 4; j++)
                {
                    byte[] row = new byte[16];
                    Buffer.BlockCopy(b, 16 * j, row, 0, 16);
                    M.Mem.Write(o[j], row);
                }
            });
            Reg(0x146B850C0, (M, i, o, s) =>
            {
                float[] a = M.Mem.M(i[0], 12), b = M.Mem.M(i[1], 12);
                float[] r = new float[12];
                for (int rr = 0; rr < 3; rr++)
                    for (int c = 0; c < 4; c++)
                        r[4 * rr + c] = (a[4 * rr + 1] * b[4 + c] + a[4 * rr] * b[c]) + b[8 + c] * a[4 * rr + 2];
                M.Mem.WM(o[0], r);
            });
            Reg(0x146B85400, (M, i, o, s) =>
            {
                // FUN_146b85220: out[r][c] = ((b[1][c] a[r][1] + b[0][c] a[r][0]) + b[2][c] a[r][2]) + b[3][c] a[r][3]
                float[] a = M.Mem.M(i[0], 16), b = M.Mem.M(i[1], 16);
                float[] r = new float[16];
                for (int rr = 0; rr < 4; rr++)
                    for (int c = 0; c < 4; c++)
                        r[4 * rr + c] = ((b[4 + c] * a[4 * rr + 1] + b[c] * a[4 * rr]) + b[8 + c] * a[4 * rr + 2]) + b[12 + c] * a[4 * rr + 3];
                M.Mem.WM(o[0], r);
            });
            Reg(0x146B843F0, (M, i, o, s) =>
            {
                // FUN_146b843f0 (SSE): cofactors by cross products, divided by the determinant
                float[] m = M.Mem.M(i[0], 12);
                F4 r0 = new F4(m[0], m[1], m[2], m[3]), r1 = new F4(m[4], m[5], m[6], m[7]), r2 = new F4(m[8], m[9], m[10], m[11]);
                Func<F4, F4> yzx = v => v.Sw(1, 2, 0, 0), zxy = v => v.Sw(2, 0, 1, 0);
                F4 c0 = zxy(r2) * yzx(r1) - yzx(r2) * zxy(r1);
                F4 c2 = zxy(r1) * yzx(r0) - yzx(r1) * zxy(r0);
                F4 c1 = zxy(r0) * yzx(r2) - yzx(r0) * zxy(r2);
                F4 p = c0 * r0;
                float det = (p.Z + p.Y) + p.X;
                float[] r = { c0.X, c1.X, c2.X, c1.X, c0.Y, c1.Y, c2.Y, c1.Y, c0.Z, c1.Z, c2.Z, c1.Z };
                for (int j = 0; j < 12; j++) r[j] = r[j] / det;
                M.Mem.WM(o[0], r);
            });
            Reg(0x146B844F0, (M, i, o, s) => M.Mem.WM(o[0], Inv44(M.Mem.M(i[0], 16))));
        }

        /// <summary>FUN_1457972e0: SIMD cofactors (lane rotations PSHUFD 0x39), sign masks, the determinant (p1+p3)+(p0+p2)
        /// from row 0, an SHUFPS transpose and a DIVPS by the determinant</summary>
        private static float[] Inv44(float[] m)
        {
            F4[] R = { new F4(m[0], m[1], m[2], m[3]), new F4(m[4], m[5], m[6], m[7]), new F4(m[8], m[9], m[10], m[11]), new F4(m[12], m[13], m[14], m[15]) };
            Func<F4, int, F4> rot = (v, k) => v.Sw(k & 3, (1 + k) & 3, (2 + k) & 3, (3 + k) & 3);
            F4 a1 = rot(R[0], 1), a2 = rot(R[0], 2), a3 = rot(R[0], 3);
            F4 b1 = rot(R[1], 1), b2 = rot(R[1], 2), b3 = rot(R[1], 3);
            F4 c1 = rot(R[2], 1), c2 = rot(R[2], 2), c3 = rot(R[2], 3);
            F4 d1 = rot(R[3], 1), d2 = rot(R[3], 2), d3 = rot(R[3], 3);
            uint[] odd = { 0, 0x80000000, 0, 0x80000000 }, even = { 0x80000000, 0, 0x80000000, 0 };
            Func<F4, uint[], F4> flip = (v, mask) => new F4(BE.BitsFloat(BE.FloatBits(v.X) ^ mask[0]), BE.BitsFloat(BE.FloatBits(v.Y) ^ mask[1]),
                                                            BE.BitsFloat(BE.FloatBits(v.Z) ^ mask[2]), BE.BitsFloat(BE.FloatBits(v.W) ^ mask[3]));
            F4 k0 = flip(((d3 * c2 - c3 * d2) * b1 + (c3 * d1 - d3 * c1) * b2) + (d2 * c1 - c2 * d1) * b3, odd);
            F4 p = R[0] * k0;
            float det = (p.Y + p.W) + (p.X + p.Z);
            F4 k1 = flip(((a3 * d2 - d3 * a2) * c1 + (d3 * a1 - a3 * d1) * c2) + (a2 * d1 - d2 * a1) * c3, even);
            F4 k2 = flip(((b3 * a2 - a3 * b2) * d1 + (a3 * b1 - b3 * a1) * d2) + (b2 * a1 - a2 * b1) * d3, odd);
            F4 k3 = flip(((c3 * b2 - b3 * c2) * a1 + (b3 * c1 - c3 * b1) * a2) + (c2 * b1 - b2 * c1) * a3, even);
            float[] r = new float[16];
            for (int j = 0; j < 4; j++)
            {
                r[4 * j] = k0[j] / det;
                r[4 * j + 1] = k1[j] / det;
                r[4 * j + 2] = k2[j] / det;
                r[4 * j + 3] = k3[j] / det;
            }
            return r;
        }
    }
}
