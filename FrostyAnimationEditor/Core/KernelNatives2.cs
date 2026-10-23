using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace FrostyAnimationEditor.Core
{
    public static partial class KernelNatives
    {
        // ------------------------------------------------------------------ game-state values (FUN_146b73a30 ...)
        private static GSValue GsOf(Machine M, uint x, string what)
        {
            GSValue g = M.Obj(x) as GSValue;
            if (g == null) throw new VMError(what + ": " + KernelText.FmtOp(x) + " is not a game-state value");
            return g;
        }

        private static double GsDouble(object v)
        {
            if (v is double) return (double)v;
            if (v is float) return (float)v;
            if (v is long) return (long)v;
            if (v is int) return (int)v;
            if (v is bool) return (bool)v ? 1.0 : 0.0;
            throw new InvalidOperationException("the game-state value is not a number");
        }

        private static long GsInt(object v)
        {
            if (v is long) return (long)v;
            if (v is int) return (int)v;
            if (v is bool) return (bool)v ? 1 : 0;
            double d = GsDouble(v);
            if (double.IsNaN(d) || double.IsInfinity(d)) throw new InvalidOperationException("cannot convert " + d + " to an integer");
            return (long)PyFormat.Mask32(new BigInteger(Math.Truncate(d)));
        }

        private static bool GsBool(object v)
        {
            if (v is bool) return (bool)v;
            if (v is double[]) return ((double[])v).Length > 0;
            if (v is float[]) return ((float[])v).Length > 0;
            if (v is long) return (long)v != 0;
            if (v is int) return (int)v != 0;
            double d = GsDouble(v);
            return d != 0 || double.IsNaN(d);
        }

        private static double[] GsVec(object v)
        {
            if (v is double[]) return (double[])v;
            if (v is float[]) return ((float[])v).Select(x => (double)x).ToArray();
            throw new InvalidOperationException("the game-state value is not a vector");
        }

        private static void GameState()
        {
            Func<string, Fn> get = kind => (M, i, o, s) =>
            {
                GSValue g = GsOf(M, i[1], "gs get");
                object v = g.Value;
                if (kind == "f") M.Mem.WF(o[0], (float)GsDouble(v));
                else if (kind == "i") M.Mem.WI(o[0], GsInt(v));
                else if (kind == "b") M.Mem.WB(o[0], GsBool(v));
                else
                {
                    double[] d = GsVec(v);
                    float[] r = new float[4];
                    for (int j = 0; j < 4 && j < d.Length; j++) r[j] = (float)d[j];
                    M.Mem.WV(o[0], F4.Of(r));
                }
            };
            Func<string, Fn> set = kind => (M, i, o, s) =>
            {
                GSValue g = GsOf(M, i[2], "gs set");
                object v;
                if (kind == "f") v = (double)M.Mem.F(i[0]);
                else if (kind == "i") v = (long)M.Mem.I(i[0]);
                else if (kind == "b") v = M.Mem.B(i[0]) != 0;
                else
                {
                    F4 x = M.Mem.V(i[0]);
                    v = new double[] { x.X, x.Y, x.Z, x.W };
                }
                g.Value = v;
                M.Log("gs", g.Name, v);
            };
            Reg(0x146B73AA0, get("f"));
            Reg(0x146B73AF0, get("i"));
            Reg(0x146B73A30, get("b"));
            Reg(0x146B73BD0, get("v"));
            Reg(0x146B73AD0, set("f"));
            Reg(0x146B73B20, set("i"));
            Reg(0x146B73A60, set("b"));
            Reg(0x146B73C30, set("v"));
            Reg(0x146B73B40, (M, i, o, s) =>
            {
                GSValue g = GsOf(M, i[1], "gs get");
                double[] d = GsVec(g.Value);
                float[] r = new float[16];
                for (int j = 0; j < 16 && j < d.Length; j++) r[j] = (float)d[j];
                M.Mem.WM(o[0], r);
            });

            // DOFs (FUN_146b73640, FUN_146b73360, FUN_146b733f0)
            Reg(0x146B73640, (M, i, o, s) =>
            {
                VmPose p; int tr; uint off; int n;
                Dof(M, i, out p, out tr, out off, out n);
                if (tr == Machine.INVALID_TRACK) M.Mem.Write(o[0], M.Mem.Read(i[2], n));
                else M.Mem.Write(o[0], Slice(p.Data, off, n));
            });
            Reg(0x146B73360, (M, i, o, s) =>
            {
                VmPose p; int tr; uint off; int n;
                Dof(M, i, out p, out tr, out off, out n);
                if (tr != Machine.INVALID_TRACK && p.Flag(tr) != 0) M.Mem.Write(o[0], Slice(p.Data, off, n));
                else M.Mem.Write(o[0], M.Mem.Read(i[2], n));
            });
            Reg(0x146B733F0, (M, i, o, s) =>
            {
                VmPose p; int tr; uint off; int n;
                Dof(M, i, out p, out tr, out off, out n);
                if (tr == Machine.INVALID_TRACK) throw new VMError("dof.write through an unbound DOF handle (the game writes at pose+0xFFFFFFFF)");
                byte[] v = M.Mem.Read(i[2], n);
                if ((long)off + n > p.Data.Length) throw new VMError("dof.write past the end of the pose");
                Buffer.BlockCopy(v, 0, p.Data, (int)off, n);
                p.SetFlag(tr);
                double[] vals = new double[v.Length / 4];
                for (int j = 0; j < vals.Length; j++) vals[j] = BE.BitsFloat(BE.U32LE(v, 4 * j));
                string nm;
                M.Log("dof", p.Names.TryGetValue(tr, out nm) ? nm : "track" + tr, vals);
            });
        }

        private static byte[] Slice(byte[] d, long off, long n)
        {
            long e = Math.Min(d.Length, off + Math.Max(0, n));
            if (off >= d.Length || e <= off) return new byte[0];
            byte[] r = new byte[e - off];
            Buffer.BlockCopy(d, (int)off, r, 0, r.Length);
            return r;
        }

        private static void Dof(Machine M, List<uint> i, out VmPose p, out int tr, out uint off, out int n)
        {
            p = M.Obj(i[0]) as VmPose;
            if (p == null) throw new VMError(KernelText.FmtOp(i[0]) + " is not a pose register");
            byte[] h = M.Mem.Read(i[1], 8);
            tr = (int)BE.U32LE(h, 0);
            off = BE.U32LE(h, 4);
            n = M.Mem.B(i[3]);
        }

        // ------------------------------------------------------------------ stateful filters
        private static void Filters()
        {
            Reg(0x146B78310, (M, i, o, s) =>
            {
                VmMem m = M.Mem;
                float x = m.F(i[0]), k = m.F(i[1]), d = m.F(i[2]), dt = m.F(i[3]);
                if (m.B(s[2]) == 0) { m.WF(s[0], x); m.WF(s[1], 0f); m.WB(s[2], true); }
                float inv = InvDt(dt);
                if (!(0f >= k))
                {
                    float pos = m.F(s[0]), vel = m.F(s[1]);
                    x = (((x - pos) * k - vel * d) * dt + vel) * dt + pos;
                    float nv = (x - pos) * inv;
                    m.WF(s[1], !Finite(nv) ? 0f : nv);
                    if (!Finite(x)) x = 0f;
                }
                m.WF(s[0], x);
                m.WF(o[0], x);
            });
            Reg(0x146B79F30, (M, i, o, s) =>
            {
                VmMem m = M.Mem;
                F4 x = m.V(i[0]);
                float k = m.F(i[1]), d = m.F(i[2]), dt = m.F(i[3]);
                float inv = InvDt(dt);
                if (m.B(s[0]) == 0) { m.WV(s[1], x); m.WB(s[0], true); }
                F4 pos = m.V(s[1]), vel = m.V(s[2]);
                if (0f < k) x = (((x - pos) * k - d * vel) * dt + vel) * dt + pos;
                m.WV(s[2], (x - pos) * inv);
                m.WV(s[1], x);
                m.WV(o[0], x);
            });
            Reg(0x146B7C760, (M, i, o, s) =>
            {
                VmMem m = M.Mem;
                float x = m.F(i[0]), k = m.F(i[1]), d = m.F(i[2]), dt = m.F(i[3]);
                if (m.B(s[2]) == 0) { m.WF(s[0], x); m.WF(s[1], 0f); m.WB(s[2], true); }
                float inv = InvDt(dt);
                float val = x;
                if (!(0f >= k))                      // COMISS 0, k; JNC
                {
                    float pos = m.F(s[0]), vel = m.F(s[1]);
                    float t = WrapT(x - pos);
                    float u = (((t * TWO_PI * k - vel * d) * dt + vel) * dt + pos) * INV_2PI;
                    u = u - Floorf(u);
                    if (u > 0.5f) u = u + -1f;
                    val = u * TWO_PI;
                    float w = (val - pos) * inv * INV_2PI;
                    w = Finite(w) ? w - Floorf(w) : w;
                    if (Finite(w) && w > 0.5f) w = w + -1f;
                    float nv = w * TWO_PI;
                    m.WF(s[1], Finite(nv) ? nv : 0f);
                    if (!Finite(val)) val = 0f;
                }
                m.WF(s[0], val);
                m.WF(o[0], val);
            });
        }

        // ------------------------------------------------------------------ transforms (FUN_146b74800, FUN_146b76530, FUN_146b847e0)
        private static int[] H(Machine M, uint x)
        {
            byte[] b = M.Mem.Read(x, 8);
            return new[] { (int)BE.U32LE(b, 0), (int)BE.U32LE(b, 4) };
        }

        private static uint HOff(int[] h) { return (uint)h[1]; }

        private static F4 PoseVec(VmPose p, int[] h)
        {
            byte[] b = Slice(p.Data, HOff(h), 16);
            byte[] full = new byte[16];
            Buffer.BlockCopy(b, 0, full, 0, b.Length);
            return new F4(BE.BitsFloat(BE.U32LE(full, 0)), BE.BitsFloat(BE.U32LE(full, 4)), BE.BitsFloat(BE.U32LE(full, 8)), BE.BitsFloat(BE.U32LE(full, 12)));
        }

        private const float R2 = (float)1.4142135;

        /// <summary>the game's scale / rotation / translation matrix (rows), q normalised</summary>
        private static float[] Compose(F4 sc, F4 q, F4 t)
        {
            float x = q.X * R2, y = q.Y * R2, z = q.Z * R2, w = q.W * R2;
            float a = (0f - x) * x + 0.5f;
            float b = (0f - y) * y + 0.5f;
            float c = (0f - z) * z + 0.5f;
            return new[]
            {
                (c + b) * sc.X, (w * z + y * x) * sc.X, (x * z - w * y) * sc.X, 0f,
                (y * x - w * z) * sc.Y, (a + c) * sc.Y, (w * x + z * y) * sc.Y, 0f,
                (w * y + x * z) * sc.Z, (z * y - w * x) * sc.Z, (b + a) * sc.Z, 0f,
                t.X, t.Y, t.Z, 1f,
            };
        }

        private static F4 Normq(F4 q)
        {
            float n = Sum(q * q, 4);
            float r = n > 0 ? 1f / Sqrtf(n) : float.PositiveInfinity;
            r = Nr2(r, n);
            return q * r;
        }

        private static F4 Row(float[] m, int r) { return new F4(m[4 * r], m[4 * r + 1], m[4 * r + 2], m[4 * r + 3]); }

        /// <summary>FUN_146b76530's tail (and FUN_146b76c00's): row lengths, the rotation of the normalised rows, the
        /// translation row</summary>
        private static void Decompose(F4[] rows, out F4 sc, out F4 q, out F4 t)
        {
            F4 r0 = rows[0], r1 = rows[1], r2 = rows[2];
            float l0 = Sqrtf((r0.Z * r0.Z + r0.Y * r0.Y) + r0.X * r0.X);
            float l1 = Sqrtf((r1.Z * r1.Z + r1.Y * r1.Y) + r1.X * r1.X);
            float l2 = Sqrtf((r2.Y * r2.Y + r2.Z * r2.Z) + r2.X * r2.X);
            sc = new F4(l0, l1, l2, l2);
            q = RowsToQuat(r0 / l0, r1 / l1, r2 / l2);
            t = rows[3];
        }

        /// <summary>the rotation of the rows n, u, s (trace method, the largest diagonal term otherwise) with the game's
        /// comparisons (COMISS / JBE: NaN takes the last branch)</summary>
        private static F4 RowsToQuat(F4 n, F4 u, F4 s)
        {
            float h = 0.5f, one = 1f;
            float tr = (u.Y + n.X) + s.Z;
            if (tr > 0f)
            {
                float r = Sqrtf(one + tr);
                float k = h / r;
                return new F4((u.Z - s.Y) * k, (s.X - n.Z) * k, (n.Y - u.X) * k, r * h);
            }
            if (n.X > u.Y && n.X > s.Z)
            {
                float r = Sqrtf((n.X - (s.Z + u.Y)) + one);
                float k = h / r;
                return new F4(r * h, (n.Y + u.X) * k, (s.X + n.Z) * k, (u.Z - s.Y) * k);
            }
            if (u.Y > s.Z)
            {
                float r = Sqrtf((u.Y - (n.X + s.Z)) + one);
                float k = h / r;
                return new F4((n.Y + u.X) * k, r * h, (u.Z + s.Y) * k, (s.X - n.Z) * k);
            }
            {
                float r = Sqrtf((s.Z - (u.Y + n.X)) + one);
                float k = h / r;
                return new F4((s.X + n.Z) * k, (u.Z + s.Y) * k, r * h, (n.Y - u.X) * k);
            }
        }

        private static VmPose PoseOf(Machine M, uint x)
        {
            object o = M.Obj(x);
            VmPose p = o as VmPose;
            if (p == null) throw new InvalidOperationException(KernelText.FmtOp(x) + " is not a pose");
            return p;
        }

        private static void Transforms()
        {
            Reg(0x146B74800, (M, i, o, s) =>
            {
                VmMem m = M.Mem;
                VmPose pa = PoseOf(M, i[0]), pb = PoseOf(M, i[1]);
                int[] h2 = H(M, i[2]);
                F4 sc = h2[0] == Machine.INVALID_TRACK ? m.V(i[3]) : PoseVec(pb, h2);
                m.WV(o[0], sc);
                F4 q4 = PoseVec(pb, H(M, i[4])), t5 = PoseVec(pb, H(M, i[5]));
                F4 q6 = PoseVec(pa, H(M, i[6])), t7 = PoseVec(pa, H(M, i[7]));
                float x = t7.X, y = t7.Y, z = t7.Z, w = t7.W;
                float qx = q4.X, qy = q4.Y, qz = q4.Z, qw = q4.W;
                float a = (z * qy - y * qz) + x * qw;
                float b = (x * qz - z * qx) + y * qw;
                float c = (y * qx - x * qy) + z * qw;
                F4 t = new F4(((c * qy - b * qz) * 2f + x) + t5.X, ((a * qz - c * qx) * 2f + y) + t5.Y,
                              ((b * qx - a * qy) * 2f + z) + t5.Z, ((a * qx - a * qx) * 2f + w) + t5.W);
                m.WV(o[2], t);
                F4 q = Normq(Qmul(q4, q6));
                m.WV(o[1], q);
                m.WM(o[3], Compose(sc, q, t));
            });
            Reg(0x146B847E0, (M, i, o, s) =>
            {
                VmMem m = M.Mem;
                m.WM(o[0], Compose(m.V(i[0]), m.V(i[1]), m.V(i[2])));
            });
            Reg(0x146B76530, (M, i, o, s) =>
            {
                VmMem m = M.Mem;
                object po = M.Obj(i[0]);
                float[] rm = m.M(i[1], 16);
                F4[] rows = { Row(rm, 0), Row(rm, 1), Row(rm, 2), Row(rm, 3) };
                int n = m.U16(i[2]);
                if (i.Count < 3 + 6 * n) throw new VMError("pose.op_3af85f0e: " + n + " entries need " + (3 + 6 * n) + " inputs, " + i.Count + " given");
                Func<uint, uint, F4> pick = (hx, dx) =>
                {
                    int[] hh = H(M, hx);
                    if (hh[0] != Machine.INVALID_TRACK)
                    {
                        VmPose pose = po as VmPose;
                        if (pose == null) throw new InvalidOperationException(KernelText.FmtOp(i[0]) + " is not a pose");
                        if (pose.Flag(hh[0]) != 0) return PoseVec(pose, hh);
                    }
                    return m.V(dx);
                };
                float z0 = 0f;
                for (int e = 0; e < n; e++)
                {
                    int b = 3 + 6 * e;
                    F4 sc = pick(i[b], i[b + 3]), q = pick(i[b + 1], i[b + 4]), t = pick(i[b + 2], i[b + 5]);
                    float[] L = Compose(sc, q, t);
                    F4 M0 = rows[0], M1 = rows[1], M2 = rows[2], M3 = rows[3];
                    F4 n0 = ((M1 * L[1] + M0 * L[0]) + M2 * L[2]) + M3 * z0;
                    F4 n1 = ((M1 * L[5] + M0 * L[4]) + M2 * L[6]) + M3 * z0;
                    F4 n2 = ((M1 * L[9] + M0 * L[8]) + M2 * L[10]) + M3 * z0;
                    F4 n3 = ((M1 * L[13] + M0 * L[12]) + M2 * L[14]) + M3 * 1f;
                    rows = new[] { n0, n1, n2, n3 };
                }
                m.WM(o[3], rows.SelectMany(r => r.ToArray()).ToArray());
                F4 dsc, dq, dt;
                Decompose(rows, out dsc, out dq, out dt);
                m.WV(o[0], dsc);
                m.WV(o[1], dq);
                m.WV(o[2], dt);
            });
            Reg(0x146B84BF0, (M, i, o, s) =>
            {
                float[] rm = M.Mem.M(i[0], 16);
                F4 sc, q, t;
                Decompose(new[] { Row(rm, 0), Row(rm, 1), Row(rm, 2), Row(rm, 3) }, out sc, out q, out t);
                M.Mem.WV(o[0], sc);
                M.Mem.WV(o[1], q);
                M.Mem.WV(o[2], t);
            });
            Reg(0x146B78430, (M, i, o, s) =>
            {
                // FUN_146b78430 / FUN_146b7a010: qY(atan2(-n.z, n.x)) * qZ(asin(n.y)) * qX(roll), n the direction in1 - in0
                // normalised; identity when the points are closer than 1.5258789e-05 or the result is not finite
                F4 a = M.Mem.V(i[0]), b = M.Mem.V(i[1]);
                float roll = M.Mem.F(i[2]);
                float d0 = b.X - a.X, d1 = b.Y - a.Y, d2 = b.Z - a.Z;
                float l2 = (d2 * d2 + d1 * d1) + d0 * d0;
                F4 outq = new F4(0f, 0f, 0f, 1f);
                if (Sqrtf(l2) >= EPS)
                {
                    float r = 1f / Sqrtf(l2);
                    r = Nr2(r, l2);
                    float n0 = d0 * r, n1 = d1 * r, n2 = d2 * r;
                    float pitch = Asin(n1);
                    float yaw = Atan2(0f - n2, n0);
                    F4 q = Qmul(Qmul(AxisQuat(1, yaw), AxisQuat(2, pitch)), AxisQuat(0, roll));
                    bool ok = true;
                    for (int j = 0; j < 4; j++) if (!(Finite(q[j]) && Math.Abs((double)q[j]) <= 3.4028235e+38)) ok = false;
                    if (ok) outq = q;
                }
                M.Mem.WV(o[0], outq);
            });
            Reg(0x146B7C510, (M, i, o, s) =>
            {
                // FUN_146b7c300: (about Y, about Z, about X)
                F4 v = M.Mem.V(i[0]);
                float x = v.X, y = v.Y, z = v.Z, w = v.W;
                float t = y * x + w * z;
                float two = 2f;
                float r0, r1, r2;
                if (Fabs(t - 0.5f) < EPS) { r0 = Atan2(x, w) * two; r1 = (float)1.5707964; r2 = 0f; }
                else if (Fabs(t - -0.5f) < EPS) { r0 = Atan2(x, w) * -2f; r1 = (float)-1.5707964; r2 = 0f; }
                else
                {
                    r0 = Atan2(y * two * w - x * two * z, (1f - y * two * y) - z * two * z);
                    r1 = Asin(t * two);
                    r2 = Atan2(x * two * w - y * two * z, (1f - x * two * x) - z * two * z);
                }
                M.Mem.WF(o[0], r0);
                M.Mem.WF(o[1], r1);
                M.Mem.WF(o[2], r2);
            });
            Reg(0x146B7C2A0, (M, i, o, s) =>
            {
                // FUN_146b7b9d0: (qY(a) * qZ(b)) * qX(c) in quat.mul's convention
                float a = M.Mem.F(i[0]), b = M.Mem.F(i[1]), c = M.Mem.F(i[2]);
                M.Mem.WV(o[0], Qmul(Qmul(AxisQuat(1, a), AxisQuat(2, b)), AxisQuat(0, c)));
            });
        }

        // ------------------------------------------------------------------ more natives
        /// <summary>((uint)x &amp; 0x7f800000) == 0x7f800000: inf or NaN</summary>
        private static bool Bad(float x) { return (BE.FloatBits(x) & 0x7F800000) == 0x7F800000; }

        /// <summary>t - floor(t), minus 1 above 0.5 (the game's wrap, before the multiplication by 2 pi)</summary>
        private static float FracWrap(float t)
        {
            t = t - Floorf(t);
            if (0.5f < t) t = t + -1f;
            return t;
        }

        private static float Clamp01(float f) { return 0f > f ? 0f : (1f > f ? f : 1f); }

        private static ulong Rng(Machine M, List<uint> i, List<uint> s)
        {
            VmMem m = M.Mem;
            int seed = m.I(i[0]);
            if (m.B(s[0]) == 0 || m.I(s[2]) != seed)
            {
                m.WB(s[0], true);
                m.WI(s[2], seed);
                m.WI(s[1], seed);
            }
            long x = m.I(s[1]);
            if (x == -1) throw new NotSimulated("random natives with state -1 read the CPU time stamp counter (rdtsc)");
            if (x == 0) x = -0x55555556;
            ulong nxt = unchecked((ulong)(x & 0xFFFFFFFF) * 0x41C64E6DUL + 0x3039UL);
            m.WI(s[1], (long)(nxt & 0xFFFFFFFF));
            return nxt;
        }

        private static void More()
        {
            Reg(0x1465734F0, (M, i, o, s) => { });
            Reg(0x146B7AE60, (M, i, o, s) =>
            {
                // FUN_146b7aa30, transcribed
                VmMem m = M.Mem;
                float f4 = m.F(i[0]), f3 = m.F(i[2]), f5 = m.F(i[4]), f2 = m.F(i[3]);
                float f8 = f2;
                if (f2 < f3) { f8 = f3; f3 = f2; }
                if (f8 < f3 + EPS) f8 = f3 + EPS;
                if (Bad(f4))
                {
                    f4 = 0f;
                    if (m.B(s[0]) != 0) f4 = m.F(s[1]);
                }
                f2 = m.F(i[1]);
                if (Bad(f2)) f2 = f4;
                if (m.B(s[0]) == 0) { m.WF(s[1], f4); m.WB(s[0], true); }
                float f1 = FracWrap(f4 * INV_2PI) * TWO_PI;
                f4 = FracWrap(f2 * INV_2PI) * TWO_PI;
                float f7 = FracWrap((f4 - f1) * INV_2PI) * TWO_PI;
                f2 = 0f;
                bool skip = false;
                float f6;
                if (!(-f3 >= f7))                      // COMISS -lo, D; JC (NaN takes this way)
                {
                    f6 = f1;
                    if (!(f7 >= f3)) skip = true;      // COMISS D, lo; JC
                    else f6 = FracWrap((f4 - f3) * INV_2PI);
                }
                else
                {
                    f6 = FracWrap(f3 * INV_2PI + f4 * INV_2PI);
                    f7 = -f7;
                }
                if (!skip)
                {
                    f6 = f6 * TWO_PI;
                    f2 = (f7 - f3) / (f8 - f3);
                    f2 = Clamp01(f2);
                }
                float st2 = m.F(s[2]);
                if (0f < f5 * f2 || EPS < Fabs(st2))
                {
                    f3 = m.F(s[1]);
                    f7 = FracWrap((f6 - f3) * INV_2PI);
                    float k = m.F(i[5]), dt = m.F(i[6]);
                    f1 = (((f7 * TWO_PI * (f5 * f2) - st2 * k) * dt + st2) * dt + f3) * INV_2PI;    // f5 * f2 reused (XMM12)
                    f1 = FracWrap(f1) * TWO_PI;
                    m.WF(s[2], FracWrap((f1 - f3) * INV_2PI) * TWO_PI);
                    m.WF(s[1], f1);
                }
                else
                {
                    m.WF(s[1], f1);
                    m.WF(s[2], 0f);
                }
                float r = FracWrap((f1 - f4) * INV_2PI) * TWO_PI;
                f3 = -f8 > r ? -f8 : r;
                f3 = f8 > f3 ? f3 : f8;
                m.WF(o[0], FracWrap(f4 * INV_2PI + f3 * INV_2PI) * TWO_PI);
            });
            Reg(0x146B77A00, (M, i, o, s) =>
            {
                // FUN_146b77a00 (inputs a, b, c, d, e)
                VmMem m = M.Mem;
                float a = m.F(i[0]), b = m.F(i[1]), c = m.F(i[2]), d = m.F(i[3]), e = m.F(i[4]);
                float eps = (float)9.999999717180685e-10, big = (float)3.4028234663852886e+38, z = 0f;
                float ca2 = (c * a) * a;
                float cma = c - a;
                float a2 = a * a;
                float te = (e * b) * cma;
                float den = a2 + te;
                float r;
                if (eps > den) r = c;
                else
                {
                    r = ca2 / den;
                    r = z > r ? z : r;
                    r = c > r ? r : c;
                }
                m.WF(o[0], r);
                float td = (d * b) * cma;
                den = td + a2;
                if (eps > den) r = c;
                else
                {
                    float q = ca2 / den;
                    r = z > q ? z : q;
                    r = c > r ? r : c;
                }
                m.WF(o[1], r);
                den = a2 - td * 1f;
                if (eps > den) r = big;
                else
                {
                    float q = ca2 / den;
                    q = c > q ? c : q;
                    r = big > q ? q : big;
                }
                m.WF(o[2], r);
                den = a2 - te * 1f;
                if (eps > den) r = big;
                else
                {
                    float q = ca2 / den;
                    q = c > q ? c : q;
                    r = big > q ? q : big;
                }
                m.WF(o[3], r);
            });
            Reg(0x146B7C800, (M, i, o, s) =>
            {
                VmMem m = M.Mem;
                F4 a = m.V(i[0]), b = m.V(i[1]);
                float ang = m.F(i[2]);
                float f8 = -89f > ang ? -89f : (89f > ang ? ang : 89f);
                float dz = b.Z - a.Z;
                float d2 = (dz * dz + 0f) + (b.X - a.X) * (b.X - a.X);
                float d = Sqrtf(d2);
                float y = a.Y;
                if (FLT_EPS < d)
                {
                    double rad = f8 * (float)0.017453292;
                    y = ((float)CrtMath.Sinf(rad) * d) / (float)CrtMath.Cosf(rad) + b.Y;
                }
                m.WV(o[0], new F4(a.X, y, a.Z, a.X));
            });
            Reg(0x146B78290, (M, i, o, s) =>
            {
                // FUN_146b78290: once initialised (or when the input moves farther than the distance) the point is put on
                // the line to the input at exactly the distance
                VmMem m = M.Mem;
                F4 t = m.V(i[0]);
                float dist = m.F(i[1]);
                F4 pnt = m.V(s[1]);
                F4 d = t - pnt;
                float d2 = (d.Z * d.Z + d.Y * d.Y) + d.X * d.X;
                float ln = Sqrtf(d2);
                if ((m.B(s[0]) != 0 || !(dist >= ln)) && ln > FLT_EPS)      // JNC / JBE as the game branches
                {
                    float k = dist / ln;
                    pnt = new F4(t.X - k * d.X, t.Y - k * d.Y, t.Z - k * d.Z, t.W - k * (t.W - pnt.W));
                    m.WV(s[1], pnt);
                    m.WB(s[0], true);
                }
                m.WV(o[0], pnt);
            });
            Reg(0x146B85C70, (M, i, o, s) =>
            {
                // FUN_146b84160: piecewise linear lookup in the 16-float table of operand 0 (x0..x7, y0..y7)
                float[] p = M.Mem.M(i[0], 16);
                float t = M.Mem.F(i[1]);
                Func<float, float, float, float, float> seg = (x0, x1, y0, y1) => ((t - x0) / (x1 - x0)) * (y1 - y0) + y0;
                float r;
                // COMISS x_k, t / JBE throughout: a NaN t ends at y7
                if (p[3] > t)
                {
                    if (p[1] > t) r = p[0] > t ? p[8] : seg(p[0], p[1], p[8], p[9]);
                    else r = seg(p[1], p[2], p[9], p[10]);      // also for x2 <= t < x3 (no test of x2)
                }
                else if (p[5] > t) r = p[4] > t ? seg(p[3], p[4], p[11], p[12]) : seg(p[4], p[5], p[12], p[13]);
                else if (p[7] > t)
                {
                    float u = p[6] > t ? (t - p[5]) / (p[6] - p[5]) : (t - p[6]) / (p[7] - p[6]);
                    r = u * (p[14] - p[13]) + p[13];            // the last segment reuses y5..y6
                }
                else r = p[15];
                M.Mem.WF(o[0], r);
            });
            Reg(0x146B85EA0, (M, i, o, s) =>
            {
                ulong nxt = Rng(M, i, s);
                M.Mem.WF(o[0], (float)(uint)((nxt >> 16) & 0xFFFFFFFF) * (float)2.3283064e-10 * 2f - 1f);
            });
            Reg(0x146B85F30, (M, i, o, s) =>
            {
                ulong nxt = Rng(M, i, s);
                M.Mem.WI(o[0], (long)((nxt >> 16) & 0xFFFFFFFF));
            });
            Reg(0x146B78120, (M, i, o, s) =>
            {
                // FUN_146b77ed0 (inputs x, rate, lo, hi, dt; state value, velocity, initialised)
                VmMem m = M.Mem;
                float x = m.F(i[0]), rate = m.F(i[1]), lo = m.F(i[2]), hi = m.F(i[3]), dt = m.F(i[4]);
                if (m.B(s[2]) == 0)
                {
                    m.Write(s[0], m.Read(i[0], 4));
                    m.WB(s[2], true);
                }
                float rng = hi - lo;
                bool ranged = !(EPS > Fabs(rng - 0f));          // SETBE: a NaN range counts as a range
                float xx = x;
                if (ranged)
                {
                    xx = lo > xx ? lo : xx;
                    xx = hi > xx ? xx : hi;
                }
                float dt60 = dt / 60f;
                float f7 = (float)CrtMath.Expf(FlipSign(rate * dt60));
                float v = m.F(s[0]);
                bool wrapped = false;
                float d = v - xx;
                if (ranged)
                {
                    float lo_ = v > xx ? xx : v;
                    float hi_ = v > xx ? v : xx;
                    float w = (lo_ - lo) + (hi - hi_);
                    if (Fabs(d) > Fabs(w))
                    {
                        wrapped = true;
                        d = xx > v ? w : -w;
                        xx = v - d;
                    }
                }
                float vel = m.F(s[1]);
                float a = rate * d + vel;
                float b = a * dt60 + d;
                m.WF(s[1], (a - b * rate) * f7);
                float y = b * f7 + xx;
                if (ranged)
                {
                    if (wrapped)
                    {
                        y = (y + rng) - lo;
                        y = (y - (float)F2I(y / rng) * rng) + lo;
                    }
                    y = lo > y ? lo : y;
                    y = hi > y ? y : hi;
                }
                m.WF(s[0], y);
                m.WF(o[0], y);
            });
        }

        // ------------------------------------------------------------------ SIMD atan2 and the SSE natives
        private static readonly float[] ATAN0 = { (float)-0.33333146572113037, (float)0.19993551075458527, (float)-0.14208899438381195, (float)0.10656263679265976 };
        private static readonly float[] ATAN1 = { (float)-0.07528963685035706, (float)0.042909614741802216, (float)-0.016165737062692642, (float)0.0028662257827818394 };
        private const float HALF_PI = (float)1.5707963705062866;

        /// <summary>FUN_14543f8f0, four lanes: x = v when |v| &lt;= 1 else 1 / v, the polynomial in x^2 times x, and
        /// sign * pi/2 - p for the reciprocal lanes</summary>
        private static F4 XmAtan(F4 v)
        {
            F4 r = new F4();
            for (int j = 0; j < 4; j++)
            {
                float one = 1f, zero = 0f;
                float vj = v[j];
                float sign = one < vj ? one : -1f;
                float nv = zero - vj;
                float av = nv > vj ? nv : vj;                      // MAXPS(0 - v, v)
                bool small = av <= one;
                sign = small ? zero : sign;
                float inv = one / vj;
                float x = small ? vj : inv;
                float x2 = x * x;
                float p = ATAN1[3] * x2;
                foreach (float c in new[] { ATAN1[2], ATAN1[1], ATAN1[0], ATAN0[3], ATAN0[2], ATAN0[1], ATAN0[0] }) p = (p + c) * x2;
                p = (p + one) * x;
                float r1 = HALF_PI * sign - p;
                r[j] = zero == sign ? p : r1;
            }
            return r;
        }

        private static readonly float[] ATAN2C = { (float)3.1415927410125732, (float)1.5707963705062866, (float)0.7853981852531433, (float)2.356194496154785 };

        /// <summary>FUN_14543f690, four lanes: the special cases of zeros and infinities with y's sign, else atan(y / x) plus
        /// pi (with y's sign) when x's sign bit is set</summary>
        private static F4 XmAtan2(F4 y, F4 x)
        {
            F4 q = y / x;
            F4 a = XmAtan(q);
            F4 r = new F4();
            for (int j = 0; j < 4; j++)
            {
                uint yu = BE.FloatBits(y[j]), xu = BE.FloatBits(x[j]);
                uint ysign = yu & 0x80000000;
                bool xpos = (xu & 0x80000000) == 0;
                bool yzero = 0f == y[j];
                bool xzero = 0f == x[j];
                bool xinf = (xu & 0x7FFFFFFF) == 0x7F800000;
                bool yinf = (yu & 0x7FFFFFFF) == 0x7F800000;
                uint pi = BE.FloatBits(ATAN2C[0]) | ysign, pi2 = BE.FloatBits(ATAN2C[1]) | ysign, pi4 = BE.FloatBits(ATAN2C[2]) | ysign, pi34 = BE.FloatBits(ATAN2C[3]) | ysign;
                const uint TRUE = 0xFFFFFFFF;
                uint r1 = xpos ? ysign : pi;
                uint r2 = xzero ? pi2 : TRUE;
                uint r3 = yzero ? r1 : r2;
                uint r4 = xpos ? pi4 : pi34;
                uint r5 = xinf ? r4 : pi2;
                uint res = yinf ? r5 : r3;
                bool valid = res == TRUE;
                float rr = a[j] + BE.BitsFloat(xpos ? 0u : pi);
                r[j] = valid ? rr : BE.BitsFloat(res);
            }
            return r;
        }

        /// <summary>1 / sqrt(d) and two Newton steps, (1 - y y d) (y / 2) + y, as FUN_146b78540 / FUN_146b78a80 do them</summary>
        private static float RsqrtNr(float d)
        {
            float y = 1f / Sqrtf(d);
            y = (1f - (y * y) * d) * (y * 0.5f) + y;
            y = (1f - (y * y) * d) * (y * 0.5f) + y;
            return y;
        }

        /// <summary>a[0x12] * b[0x09] - a[0x09] * b[0x12]: cross(b, a) in x, y, z (w = a0 b0 - a0 b0)</summary>
        private static F4 CrossSse(F4 a, F4 b) { return a.Pshufd(0x12) * b.Pshufd(0x09) - a.Pshufd(0x09) * b.Pshufd(0x12); }

        /// <summary>MAXPS(-1 * v, v) &lt;= 1.1920929e-07 for x, y and z</summary>
        private static bool Tiny3(F4 v)
        {
            F4 nv = -1f * v;
            F4 a = WhereGt(nv, v, nv, v);
            for (int j = 0; j < 3; j++) if (!(a[j] <= (float)1.1920928955078125e-07)) return false;
            return true;
        }

        private static readonly F4 UP = new F4(0f, 1f, 0f, 0f);

        private static F4 ConjX(F4 q) { return new F4(FlipSign(q.X), FlipSign(q.Y), FlipSign(q.Z), q.W); }

        /// <summary>+X rotated by q: t = cross(q, X) + q.w X, r = 2 cross(q, t) + X (lane order of the SSE code)</summary>
        private static F4 RotXAxis(F4 q)
        {
            F4 X = new F4(1f, 0f, 0f, 0f);
            F4 t = (X.Pshufd(0x12) * q.Pshufd(0x09) - X.Pshufd(0x09) * q.Pshufd(0x12)) + X * q.W;
            return (t.Pshufd(0x12) * q.Pshufd(0x09) - t.Pshufd(0x09) * q.Pshufd(0x12)) * 2f + X;
        }

        private static float WrapTurns(float t)
        {
            t = t - Floorf(t);
            if (t > 0.5f) t = t + -1f;
            return t;
        }

        private static F4 PoseGet(VmPose P, int[] h)
        {
            if ((long)HOff(h) + 16 > P.Data.Length) throw new VMError("DOF value at 0x" + HOff(h).ToString("x") + " outside the pose");
            return PoseVec(P, h);
        }

        /// <summary>16 bytes at the DOF's offset (MOVUPS); the game does not check the handle</summary>
        private static void PosePut(Machine M, VmPose P, int[] h, F4 vec)
        {
            if (h[0] == Machine.INVALID_TRACK || (long)HOff(h) + 16 > P.Data.Length)
                throw new VMError("pose write through an unbound DOF handle (the game writes at pose+0x" + HOff(h).ToString("x") + ")");
            byte[] b = VmMem.Floats(vec.X, vec.Y, vec.Z, vec.W);
            Buffer.BlockCopy(b, 0, P.Data, (int)HOff(h), 16);
            string nm;
            M.Log("dof", P.Names.TryGetValue(h[0], out nm) ? nm : "track" + h[0], new double[] { vec.X, vec.Y, vec.Z, vec.W });
        }

        private static void Simd()
        {
            Reg(0x146B85E30, (M, i, o, s) =>
            {
                // FUN_146b85ce0: s = q * sqrt(2); the rotation's matrix terms; (atan2 about X, about Y, about Z, about Z) with the
                // SIMD atan2, or (.., .., 0, ..) from the other terms at the pole (cos <= 0.001)
                F4 q = M.Mem.V(i[0]);
                float zero = 0f;
                F4 sq = (float)1.4142135381698608 * q;
                F4 h = (zero - sq) * sq + 0.5f;
                F4 p = sq.Sw(1, 2, 0, 0) * sq;
                F4 w = sq.Sw(3, 3, 3, 3) * sq.Sw(2, 0, 1, 0);
                F4 sm = h.Sw(1, 2, 0, 0) + h;
                F4 add = w + p;
                F4 diff = p - w;
                float r = Sqrtf(sm.Y * sm.Y + add.X * add.X);
                F4 outv;
                if (r > (float)0.0010000000474974513)
                    outv = XmAtan2(new F4(add.Y, zero - diff.Z, add.X, add.X), new F4(sm.X, r, sm.Y, sm.Y));
                else
                {
                    F4 res = XmAtan2(new F4(zero - diff.Y, zero - diff.Z, zero - diff.Y, zero - diff.Y), new F4(sm.Z, r, sm.Z, sm.Z));
                    outv = new F4(res.X, res.Y, zero, res.W);
                }
                M.Mem.WV(o[0], outv);
            });
            Reg(0x146B77E60, (M, i, o, s) =>
            {
                // FUN_146b77b70: a + Ry(angle) b with the game's polynomial sine/cosine; w comes out as a.w + 1
                F4 a = M.Mem.V(i[0]), b = M.Mem.V(i[1]);
                float sn, cs;
                Gsincos(M.Mem.F(i[2]), out sn, out cs);
                float z = 0f, one = 1f;
                F4 r0 = new F4(cs, z, z - sn, z), r1 = new F4(z, one, z, z), r2 = new F4(sn, z, cs, z), r3 = new F4(z, z, z, one);
                M.Mem.WV(o[0], (((r0 * b.X + r3) + r1 * b.Y) + r2 * b.Z) + a);
            });
            Reg(0x146B78A20, (M, i, o, s) =>
            {
                // FUN_146b78540: the rotation whose X axis is v normalised, Z axis normalize(cross(v, up)) and Y axis cross(Z, X)
                F4 q = M.Mem.V(i[0]), v = M.Mem.V(i[1]);
                float mn = M.Mem.F(i[2]);
                F4 outq = q;
                float d = Sum(v * v, 3);
                float ln = Sqrtf(d);
                if (ln > mn && !Tiny3(v))
                {
                    F4 n = v * RsqrtNr(d);
                    F4 c = CrossSse(UP, n);
                    if (!Tiny3(c))
                    {
                        float sd = Sum(c * c, 3);
                        F4 sv = c * RsqrtNr(sd);
                        F4 u = CrossSse(n, sv);
                        F4 r = RowsToQuat(n, u, sv);
                        outq = r;
                        if (mn * 2f > ln)
                        {
                            float t = (ln - mn) / mn;
                            float dot = Sum(q * r, 4);
                            F4 b = 0f < dot ? (r - q) * t + q : q - (q + r) * t;
                            outq = b * RsqrtNr(Sum(b * b, 4));
                        }
                    }
                }
                M.Mem.WV(o[0], outq);
            });
            Reg(0x146B78DB0, (M, i, o, s) =>
            {
                // FUN_146b78a80: m = (a - b) / 2 + b; p = m + d * normalize(cross(n, up)) (+Z when n . up > 0.99998474); the
                // angle at p between a and b. Outputs p, m, angle; p = m and angle = 0 when |a - b| <= 1.5258789e-05
                F4 a = M.Mem.V(i[0]), b = M.Mem.V(i[1]);
                float dist = M.Mem.F(i[2]);
                F4 dv = a - b;
                F4 mid = dv * 0.5f + b;
                M.Mem.WV(o[1], mid);
                float d = Sum(dv * dv, 3);
                if ((float)1.52587890625e-05 >= Sqrtf(d))           // COMISS eps, |a - b|; JC: NaN goes on
                {
                    M.Mem.WV(o[0], mid);
                    M.Mem.WF(o[2], 0f);
                    return;
                }
                F4 n = dv * RsqrtNr(d);
                F4 side;
                if (Sum(n * UP, 3) > (float)0.9999847412109375) side = new F4(0f, 0f, 1f, 0f);
                else
                {
                    F4 c = CrossSse(UP, n);
                    side = c * RsqrtNr(Sum(c * c, 3));
                }
                F4 p = side * dist + mid;
                M.Mem.WV(o[0], p);
                F4 pa = p - a, pb = p - b;
                F4 na = pa * RsqrtNr(Sum(pa * pa, 3));
                F4 nb = pb * RsqrtNr(Sum(pb * pb, 3));
                M.Mem.WF(o[2], Acos(Sum(nb * na, 3)));
            });
            Reg(0x146B79E60, (M, i, o, s) =>
            {
                // FUN_146b78e20: the yaw and pitch of q's X axis follow damped springs
                VmMem m = M.Mem;
                F4 q = m.V(i[0]);
                float kp = m.F(i[1]), dp = m.F(i[2]), ky = m.F(i[3]), dy = m.F(i[4]), dt = m.F(i[5]);
                float inv = 1f;
                if (!((float)1.52587890625e-05 > Fabs(dt - 0f))) inv = 1f / dt;
                F4 r = RotXAxis(q);
                float yaw = Atan2(0f - r.Z, r.X);
                float pitch = Asin(r.Y);
                F4 rest = Qmul(Qmul(ConjX(AxisQuat(2, pitch)), ConjX(AxisQuat(1, yaw))), q);
                if (m.B(s[4]) == 0)
                {
                    m.WB(s[4], true);
                    m.WF(s[0], pitch);
                    m.WI(s[1], 0);
                    m.WF(s[2], yaw);
                    m.WI(s[3], 0);
                }
                float two_pi = (float)6.2831854820251465, inv_2pi = (float)0.15915493667125702;
                if (!(0f >= kp))                                     // COMISS 0, k; JC
                {
                    float p0 = m.F(s[0]), v = m.F(s[1]);
                    float t = WrapTurns((pitch - p0) * inv_2pi);
                    float n = ((t * two_pi * kp - v * dp) * dt + v) * dt + p0;
                    float lo = (float)-1.5707963705062866, hi = (float)1.5707963705062866;
                    float c = lo > n ? lo : (hi > n ? n : hi);
                    m.WF(s[1], WrapTurns((c - p0) * inv * inv_2pi) * two_pi);
                    m.WF(s[0], c);
                }
                else
                {
                    m.WF(s[0], pitch);
                    m.WI(s[1], 0);
                }
                if (!(0f >= ky))
                {
                    float y0 = m.F(s[2]), v = m.F(s[3]);
                    float t = WrapTurns((yaw - y0) * inv_2pi);
                    float n = WrapTurns((((t * two_pi * ky - v * dy) * dt + v) * dt + y0) * inv_2pi) * two_pi;
                    m.WF(s[3], WrapTurns((n - y0) * inv * inv_2pi) * two_pi);
                    m.WF(s[2], n);
                }
                else
                {
                    m.WF(s[2], yaw);
                    m.WI(s[3], 0);
                }
                F4 e = Qmul(AxisQuat(1, m.F(s[2])), AxisQuat(2, m.F(s[0])));
                m.WV(o[0], Qmul(e, rest));
            });
            Reg(0x146B7CCB0, (M, i, o, s) =>
            {
                // FUN_146b7c950: a point that stays put while the input is inside the ellipsoid around it and follows it once it
                // has left. State: initialised, following, value, last input, velocity
                VmMem m = M.Mem;
                F4 x = m.V(i[0]), ext = m.V(i[1]), q = m.V(i[2]);
                float k = m.F(i[3]), damp = m.F(i[4]), thr = m.F(i[5]), dt = m.F(i[6]);
                float inv = 1f;
                if (!((float)1.52587890625e-05 > Fabs(dt - 0f))) inv = 1f / dt;
                if (m.B(s[0]) == 0)
                {
                    m.WV(s[2], x);
                    m.WV(s[3], x);
                    m.WB(s[0], true);
                }
                F4 v = m.V(s[2]);
                F4 outv = v;
                bool check = true;
                if (m.B(s[1]) != 0)
                {
                    F4 vel = m.V(s[4]);
                    if (k > 0f) outv = (((((x - v) * k - damp * vel) * dt) + vel) * dt) + v;
                    else outv = v + (x - m.V(s[3]));
                    if (!(0f >= k))
                    {
                        check = false;
                        F4 d = (x - v) * inv;
                        if (thr >= Sqrtf(Sum(d * d, 3)))                 // COMISS thr, |v|; JC: NaN keeps following
                            if (thr >= Sqrtf(Sum(vel * vel, 3))) m.WB(s[1], false);
                    }
                }
                if (check)
                {
                    F4 d = x - v;
                    float n2 = Sum(q * q, 4);
                    float r = 1f / n2;
                    r = (2f - r * n2) * r;
                    r = (2f - r * n2) * r;
                    F4 qi = r * ConjX(q);
                    F4 t = CrossSse(d, qi) + d * qi.W;
                    F4 loc = CrossSse(t, qi) * 2f + d;
                    F4 ne = -1f * ext;
                    F4 a = WhereGt(ne, ext, ne, ext);
                    float e0 = (float)1.52587890625e-05;
                    F4 e = new F4(e0 > a.X ? e0 : a.X, e0 > a.Y ? e0 : a.Y, e0 > a.Z ? e0 : a.Z, e0 > a.W ? e0 : a.W);
                    loc = loc / e;
                    if (Sum(loc * loc, 3) >= 1f) m.WB(s[1], true);      // COMISS |loc|^2, 1; JC: NaN is inside
                    else if (0f >= k) m.WB(s[1], false);
                }
                m.WV(s[4], (outv - v) * inv);
                m.WV(s[2], outv);
                m.WV(s[3], x);
                m.WV(o[0], outv);
            });
            Reg(0x146B74B30, (M, i, o, s) =>
            {
                // FUN_146b74b30 (inputs pose A, pose B, scale DOF of A, rotation and translation DOFs of B, rotation and translation
                // DOFs of A, matrix M)
                VmMem m = M.Mem;
                VmPose pa = PoseOf(M, i[0]), pb = PoseOf(M, i[1]);
                int[] hs = H(M, i[2]), hrb = H(M, i[3]), htb = H(M, i[4]), hra = H(M, i[5]), hta = H(M, i[6]);
                float[] rm = m.M(i[7], 16);
                F4[] rows = { Row(rm, 0), Row(rm, 1), Row(rm, 2), Row(rm, 3) };
                float[] l = new float[3];
                for (int j = 0; j < 3; j++) l[j] = Sqrtf(Sum(rows[j] * rows[j], 3));
                F4 sc = new F4(l[0], l[1], l[2], l[2]);
                F4 qm = RowsToQuat(rows[0] / l[0], rows[1] / l[1], rows[2] / l[2]);
                F4 d = rows[3] - PoseGet(pb, htb);
                F4 c = ConjX(PoseGet(pb, hrb));
                F4 qn = c * RsqrtNr(Sum(c * c, 4));
                F4 t = CrossSse(d, qn) + d * qn.W;
                F4 rr = CrossSse(t, qn) * 2f + d;
                PosePut(M, pa, hta, rr + PoseGet(pa, hta));
                F4 p = Qmul(c, qm);
                F4 pn = p * RsqrtNr(Sum(p * p, 4));
                PosePut(M, pa, hra, Qmul(pn, PoseGet(pa, hra)));
                pa.SetFlag(hra[0]);
                pa.SetFlag(hta[0]);
                if (hs[0] != Machine.INVALID_TRACK)
                {
                    PosePut(M, pa, hs, sc);
                    pa.SetFlag(hs[0]);
                }
            });
            Reg(0x146B76C00, (M, i, o, s) =>
            {
                // FUN_146b76c00 (inputs pose, matrices P and W, scale, rotation and translation DOFs): the scale, rotation and
                // translation of W x inverse(P) written into the pose with the tracks' 'set' flags
                VmMem m = M.Mem;
                VmPose pa = PoseOf(M, i[0]);
                float[] inv = Inv44(m.M(i[1], 16));
                float[] w = m.M(i[2], 16);
                F4[] ir = { Row(inv, 0), Row(inv, 1), Row(inv, 2), Row(inv, 3) };
                F4[] rows = new F4[4];
                for (int k = 0; k < 4; k++) rows[k] = ((ir[1] * w[4 * k + 1] + ir[0] * w[4 * k]) + ir[2] * w[4 * k + 2]) + ir[3] * w[4 * k + 3];
                F4 sc, q, t;
                Decompose(rows, out sc, out q, out t);
                int[] h3 = H(M, i[3]), h4 = H(M, i[4]), h5 = H(M, i[5]);
                PosePut(M, pa, h3, sc); pa.SetFlag(h3[0]);
                PosePut(M, pa, h4, q); pa.SetFlag(h4[0]);
                PosePut(M, pa, h5, t); pa.SetFlag(h5[0]);
            });
        }
    }
}
