using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;

namespace FrostyAnimationEditor.Core
{
    /// <summary>
    /// Curves stored as bytes or sample lists, evaluated bit for bit the way FIFA17.exe does (verified in the
    /// emulator: DofWeightCurves scalar + SIMD paths, FloatGameStateTag classes and modes).
    ///
    /// DofWeightCurvesAsset (FUN_145cd6d90 loads it, FUN_145cd7960 builds the per-DOF table, FUN_145cd9c90 /
    /// FUN_145cd81f0 evaluate it): every curve is 4 bytes [start, length, in, out]; T = 255 t.  length 0: weight 1
    /// when T &gt; start (or T is NaN), else 0.  Otherwise u = clamp((T - start) / length, 0, 1), a = in*6/255 - 1.5,
    /// b = out*6/255 - 1.5 (a DOF whose inherited-mask byte is set uses a = 1, b = 2) and
    /// weight = ((a - b) + 1) u^3 + (b - 2a) u^2 + a u.  The game evaluates DOFs in groups of four with SIMD code and
    /// the remaining count % 4 with scalar code; the two differ in the order of operations.
    ///
    /// FloatGameStateTag (FUN_145d1e090 picks the class from SplineStorageFlags, FUN_145d1d0c0 loads it):
    ///   0x10 Spline samples (linear), 0x40 SplineRaw bytes (value/255, linear), 0x20|n cubic Bezier y(x) after n
    ///   steps u += (t - x(u))/2.  Mode (FUN_145d1eb20): 0 nothing, 1 v f, 2 v f cur, 3 (1-f) cur + v f, 4 v f + cur,
    ///   5 cur - v f, 6 v f at t + cur, 7 v f + cur; other values write 0.
    /// </summary>
    public static class Curves
    {
        public static float BitsF(uint u) { return BE.BitsFloat(u); }
        public static readonly float K6 = BitsF(0x3CC0C0C1);          // 6/255 as stored in the exe
        public static readonly float INV255 = (float)0.003921568859368563;
        public static readonly float TOL = BitsF(0x3A83126F);         // 0.001
        private const float ZERO = 0f, ONE = 1f, TWO = 2f, MTWO = -2f, MHALF3 = -1.5f;

        // ---------------------------------------------------------------------------- weight curves
        public sealed class CurveParams
        {
            public double Start, Length, SlopeIn, SlopeOut;
            public bool Step;
            public string Hex;
        }

        public static CurveParams DecodeCurve(byte[] b)
        {
            if (b.Length != 4) throw new ArgumentException("a weight curve is 4 bytes");
            float a = (float)((float)b[2] * K6) - 1.5f;
            float bb = (float)((float)b[3] * K6) - 1.5f;
            return new CurveParams { Start = b[0] / 255.0, Length = b[1] / 255.0, SlopeIn = a, SlopeOut = 3.0 - bb, Step = b[1] == 0, Hex = BE.Hex(b) };
        }

        private static int CurveByte(double x, double lo, double hi, double scale, double off)
        {
            if (!(lo - 1e-9 <= x && x <= hi + 1e-9)) throw new ArgumentException(x + " outside [" + lo + ", " + hi + "]");
            return Math.Max(0, Math.Min(255, (int)Math.Round((x + off) * scale)));
        }

        /// <summary>parameters -> the nearest 4 bytes (start and length in [0, 1], slopes in [-1.5, 4.5])</summary>
        public static byte[] EncodeCurve(double start, double length, double slopeIn = 1.0, double slopeOut = 1.0)
        {
            return new[] { (byte)CurveByte(start, 0, 1, 255.0, 0), (byte)CurveByte(length, 0, 1, 255.0, 0),
                           (byte)CurveByte(slopeIn, -1.5, 4.5, 255.0 / 6.0, 1.5), (byte)CurveByte(3.0 - slopeOut, -1.5, 4.5, 255.0 / 6.0, 1.5) };
        }

        /// <summary>SIMD lanes: the mask byte replicated into the lane and used as a bit mask</summary>
        private static float MaskSelect(int mask, float computed, float cst)
        {
            uint m = (uint)(mask & 0xFF) * 0x01010101u;
            return BitsF((BE.FloatBits(computed) & ~m) | (BE.FloatBits(cst) & m));
        }

        private static float CubicScalar(float u, float a, float b)
        {
            float u2 = u * u;
            float u3 = u2 * u;
            return (float)((float)((float)((float)((a - b) + ONE) * u3) + (float)((float)(b - (float)(a * TWO)) * u2)) + (float)(u * a));
        }

        private static float CubicSimd(float u, float a, float b)
        {
            float u2 = u * u;
            float u3 = u2 * u;
            return (float)((float)((float)((float)((float)(MTWO * a) + b) * u2) + (float)(u * a)) + (float)((float)((float)(a - b) + ONE) * u3));
        }

        private static float ClampScalar(float u) { return ZERO > u ? ZERO : (ONE > u ? u : ONE); }       // NaN -> 1

        private static float ClampSimd(float u)
        {
            u = u > ZERO ? u : ZERO;                                                                    // NaN -> 0
            return u < ONE ? u : ONE;
        }

        private static float Slope(int b) { return (float)((float)b * K6) + MHALF3; }

        /// <summary>FUN_145cd9c90 for one DOF: weight at normalised time t of curve bytes b with mask byte mask;
        /// simd selects the order of operations used for DOFs in a group of four</summary>
        public static float Weight(byte[] b, float t, int mask = 0, bool simd = false)
        {
            mask &= 0xFF;
            float T = t * 255f;
            float start = b[0];
            if (b[1] == 0) return !(start >= T) ? 1f : 0f;
            float length = b[1];
            if (simd)
            {
                float a = MaskSelect(mask, Slope(b[2]), ONE);
                float bb = MaskSelect(mask, Slope(b[3]), TWO);
                float u = ClampSimd((float)(T - start) * (float)(ONE / length));
                return CubicSimd(u, a, bb);
            }
            float a2, b2;
            if (mask != 0) { a2 = ONE; b2 = TWO; }
            else { a2 = Slope(b[2]); b2 = Slope(b[3]); }
            float u2 = ClampScalar((float)(T - start) * (float)(ONE / length));
            return CubicScalar(u2, a2, b2);
        }

        /// <summary>FUN_145cd81f0 for one DOF (TimeScaleBlend with WeightCurvesOut): parameters interpolated from
        /// bIn to bOut by blend; mask is bIn's table byte</summary>
        public static float WeightBlend(byte[] bIn, byte[] bOut, float t, float blend, int mask = 0, bool simd = false)
        {
            mask &= 0xFF;
            float w = blend;
            float T = t * 255f;
            Func<float, float, float> lerp = (x, y) => (float)((float)(y - x) * w) + x;
            float start = lerp(bIn[0], bOut[0]);
            float length = lerp(bIn[1], bOut[1]);
            if (simd)
            {
                float a = MaskSelect(mask, (float)(lerp(bIn[2], bOut[2]) * K6) + MHALF3, ONE);
                float bb = MaskSelect(mask, (float)(lerp(bIn[3], bOut[3]) * K6) + MHALF3, TWO);
                float u = ClampSimd((float)(T - start) * (float)(ONE / length));
                if (length == ZERO) return !(T <= start) ? 1f : 0f;
                return CubicSimd(u, a, bb);
            }
            if (bIn[1] == 0) return !(start >= T) ? 1f : 0f;
            float a2, b2;
            if (mask != 0) { a2 = ONE; b2 = TWO; }
            else
            {
                a2 = lerp(Slope(bIn[2]), Slope(bOut[2]));
                b2 = lerp(Slope(bIn[3]), Slope(bOut[3]));
            }
            float u2 = ClampScalar((float)(T - start) * (float)(ONE / length));
            return CubicScalar(u2, a2, b2);
        }

        /// <summary>FUN_145cd9c90 over a whole per-DOF table (complete groups of four use the SIMD order)</summary>
        public static List<float> TableWeights(IList<byte[]> curves, IList<int> masks, float t)
        {
            int n = curves.Count, g = 4 * (n / 4);
            List<float> r = new List<float>(n);
            for (int i = 0; i < n; i++) r.Add(Weight(curves[i], t, masks[i], i < g));
            return r;
        }

        public static List<float> TableWeightsBlend(IList<byte[]> cIn, IList<int> mIn, IList<byte[]> cOut, float t, float blend)
        {
            int n = cIn.Count, g = 4 * (n / 4);
            List<float> r = new List<float>(n);
            for (int i = 0; i < n; i++) r.Add(WeightBlend(cIn[i], cOut[i], t, blend, mIn[i], i < g));
            return r;
        }

        public sealed class WeightSet
        {
            public string Name;
            public bool ForEachDof;
            public List<Tuple<byte[], int>> Curves = new List<Tuple<byte[], int>>();
        }

        private static byte[] Bytes(object v)
        {
            if (v == null) return new byte[0];
            if (v is byte[]) return (byte[])v;
            IList l = v as IList;
            if (l == null) return new byte[0];
            byte[] r = new byte[l.Count];
            for (int i = 0; i < r.Length; i++) r[i] = (byte)Num.ToLong(l[i]);
            return r;
        }

        /// <summary>the curve sets of a DofWeightCurvesAsset, the default first</summary>
        public static List<WeightSet> WeightSets(AntDb db, GdStruct o)
        {
            byte[] mask = Bytes(o["DefaultInheritedMaskBytes"]);
            if (mask.Length == 0) mask = new byte[1];
            byte[] dc = Bytes(o["DefaultCurveBytes"]);
            if (dc.Length == 0) dc = new byte[4];
            List<WeightSet> r = new List<WeightSet>();
            WeightSet d = new WeightSet { Name = "<default>" };
            d.Curves.Add(Tuple.Create(dc, (int)mask[0]));
            r.Add(d);
            List<string> names = new List<string>();
            ulong lk = o.GetKey("DofSetList");
            GdObj lst = lk != 0 && db != null ? db.Get(lk) : null;
            if (lst != null)
                foreach (object k in lst["LayoutAssets"] as IList ?? new object[0])
                {
                    GdObj x = db.Get(Num.ToKey(k));
                    names.Add(x != null ? Str.Text(x["__name"]) : BE.KeyHex(Num.ToKey(k)));
                }
            IList sets = o["DofSetCurves"] as IList ?? new object[0];
            for (int j = 0; j < sets.Count; j++)
            {
                GdStruct s = sets[j] as GdStruct;
                if (s == null) continue;
                byte[] cb = Bytes(s["CurveBytes"]), mb = Bytes(s["InheritedMaskBytes"]);
                WeightSet ws = new WeightSet { Name = j < names.Count ? names[j] : "set " + j, ForEachDof = s.GetBool("ForEachDof") };
                for (int i = 0; i < mb.Length; i++)
                {
                    byte[] c = new byte[4];
                    for (int q = 0; q < 4; q++) c[q] = 4 * i + q < cb.Length ? cb[4 * i + q] : (byte)0;
                    ws.Curves.Add(Tuple.Create(c, (int)mb[i]));
                }
                r.Add(ws);
            }
            return r;
        }

        // ---------------------------------------------------------------------------- game-state tags
        private static long Cvtt(float x)
        {
            double d = x;
            if (d == d && d >= -2147483648.0 && d < 2147483648.0) return (long)d;
            return -0x80000000L;
        }

        private static float[] FloatsOf(object v)
        {
            IList l = v as IList;
            if (l == null) return new float[0];
            float[] r = new float[l.Count];
            for (int i = 0; i < r.Length; i++) r[i] = Num.ToFloat(l[i]);
            return r;
        }

        private static float[] Rot(float[] q) { return new[] { q[1], q[2], q[3], q[0] }; }

        /// <summary>the FloatGameStateTag's curve at t; null when the result depends on memory outside the samples
        /// or the flags select no class</summary>
        public static float? TagCurve(GdStruct o, float t)
        {
            long fl = o.GetInt("SplineStorageFlags");
            if (fl == 0x10)
            {
                float[] s = FloatsOf(o["Spline"]);
                int n = s.Length;
                if (n <= 1) return n > 0 ? s[0] : 0f;
                float x = (float)(n - 1) * t;
                long k = Cvtt(x);
                if (((k + 1) & 0xFFFFFFFF) >= n) return s[n - 1];
                if (k < 0) return null;
                return (float)((float)(s[k + 1] - s[k]) * (float)(x - (float)k)) + s[k];
            }
            if (fl == 0x40)
            {
                byte[] r = Bytes(o["SplineRaw"]);
                int n = r.Length;
                if (n <= 1) return n > 0 ? (float)r[0] / 255f : 0f;
                float x = (float)(n - 1) * t;
                long k = Cvtt(x);
                if ((k & 0xFFFFFFFF) >= n) return (float)r[n - 1] * INV255;
                int b0 = r[k];
                float f = x - (float)k;
                int b1;
                if (k + 1 < n) b1 = r[k + 1];
                else if (n <= 4 || f == 0f) b1 = n <= 4 ? 0 : b0;
                else return null;
                return (float)((float)((float)(b1 - b0) * f) + (float)b0) * INV255;
            }
            if ((fl & 0x20) != 0)
            {
                float[] s0 = FloatsOf(o["Spline"]);
                float[] s = new float[Math.Max(8, s0.Length + 8)];
                Array.Copy(s0, s, s0.Length);
                float[] P = { s[0], s[1], s[2], s[3] };
                float[] Q = { s[4], s[5], s[6], s[7] };
                float t2 = t < 1f ? t : 1f;
                float[] u = { t2, t2, t2, t2 };
                float[] rp = Rot(P);
                float[] D = new float[4];
                for (int i = 0; i < 4; i++) D[i] = rp[i] - P[i];
                for (long it = 0; it < (fl & 0xF); it++)
                {
                    float[] l1 = new float[4], l2 = new float[4], l3 = new float[4];
                    for (int i = 0; i < 4; i++) l1[i] = (float)(D[i] * u[i]) + P[i];
                    float[] r1 = Rot(l1);
                    for (int i = 0; i < 4; i++) l2[i] = (float)((float)(r1[i] - l1[i]) * u[i]) + l1[i];
                    float[] r2 = Rot(l2);
                    for (int i = 0; i < 4; i++) l3[i] = (float)((float)(r2[i] - l2[i]) * u[i]) + l2[i];
                    float dd = (float)(t2 - l3[0]) * 0.5f;
                    for (int i = 0; i < 4; i++) u[i] = u[i] + dd;
                }
                float[] rq = Rot(Q);
                float[] m1 = new float[4], m2 = new float[4], m3 = new float[4];
                for (int i = 0; i < 4; i++) m1[i] = (float)((float)(rq[i] - Q[i]) * u[i]) + Q[i];
                float[] q1 = Rot(m1);
                for (int i = 0; i < 4; i++) m2[i] = (float)((float)(q1[i] - m1[i]) * u[i]) + m1[i];
                float[] q2 = Rot(m2);
                for (int i = 0; i < 4; i++) m3[i] = (float)((float)(q2[i] - m2[i]) * u[i]) + m2[i];
                return m3[0];
            }
            return null;
        }

        public static readonly Dictionary<int, string> TagModes = new Dictionary<int, string>
        {
            { 0, "off" }, { 1, "set" }, { 2, "multiply" }, { 3, "blend" }, { 4, "add (from the value at entry)" },
            { 5, "subtract" }, { 6, "set, playing on from the current value" }, { 7, "add (accumulating)" },
        };

        public static readonly Dictionary<int, string> ResetOnLeaveNames = new Dictionary<int, string>
        {
            { 0, "apply the tag once more at the leave time" }, { 1, "reset to the game state's default" }, { 2, "restore the value saved at entry" },
        };

        /// <summary>FUN_145d1eb20: the value the tag writes at t given current; null when the curve depends on
        /// memory outside the samples</summary>
        public static float? TagApply(GdStruct o, float t, float current)
        {
            long mode = o.GetInt("Mode");
            float v = o.GetFloat("Value");
            float cur = current;
            if (mode == 6)
            {
                t = t + cur;
                if (t > ONE) t = ONE;
            }
            float? fv = TagCurve(o, t);
            if (!(1 <= mode && mode <= 7)) return 0f;
            if (!fv.HasValue) return null;
            float f = fv.Value;
            if (mode == 1 || mode == 6) return f * v;
            if (mode == 2) return (float)(f * v) * cur;
            if (mode == 3) return (float)((float)(ONE - f) * cur) + (float)(f * v);
            if (mode == 4 || mode == 7) return (float)(f * v) + cur;
            return cur - (float)(f * v);
        }

        /// <summary>FUN_145d1e980 (Mode 6 at entry): the time at which the curve reaches value</summary>
        public static float? TagInvert(GdStruct o, float value)
        {
            float v = value;
            float? f0 = TagCurve(o, 0f);
            if (!f0.HasValue) return null;
            float d0 = f0.Value - v;
            if (TOL >= Math.Abs(d0)) return 0f;
            float? f1 = TagCurve(o, 1f);
            if (!f1.HasValue) return null;
            float d1 = f1.Value - v;
            if (TOL >= Math.Abs(d1)) return 1f;
            if ((float)(d1 * d0) > ZERO) return 0f;
            float step, bas;
            if (ZERO > d0) { step = ONE; bas = ZERO; }
            else { step = -1f; bas = ONE; }
            float x = bas;
            for (int i = 0; i < 10; i++)
            {
                step = step * 0.5f;
                x = bas + step;
                float? f = TagCurve(o, x);
                if (!f.HasValue) return null;
                float fd = f.Value - v;
                if (ZERO >= fd) bas = x;
                if (TOL >= Math.Abs(fd)) break;
            }
            return x;
        }

        private static object BaseValue(GdStruct o, string name)
        {
            GdStruct b = o["__base"] as GdStruct;
            while (b != null)
            {
                if (b.Has(name)) return b[name];
                b = b["__base"] as GdStruct;
            }
            return null;
        }

        /// <summary>the game-state value through one stay in the tagged state: enter (FUN_145d20000), one update
        /// per time (FUN_145d20e30), leave at leaveT (FUN_145d20760); -> (values after each update, value after leaving)</summary>
        public static List<float?> TagRun(GdStruct o, IList<float> times, float value, out float? end, float dflt = 0f, int? resetOnLeave = null, float? leaveT = null)
        {
            long mode = o.GetInt("Mode");
            int rol = resetOnLeave ?? (int)(BaseValue(o, "ResetOnLeave") == null ? 0 : Num.ToLong(BaseValue(o, "ResetOnLeave")));
            float? live = value;
            List<float?> outp = new List<float?>();
            if (mode == 0)
            {
                foreach (float t in times) outp.Add(live);
                end = live;
                return outp;
            }
            float? saved = mode == 6 ? TagInvert(o, live.Value) : live;
            foreach (float t in times)
            {
                float? cur = mode == 7 ? live : saved;
                float? r = cur.HasValue ? TagApply(o, t, cur.Value) : null;
                live = r.HasValue ? r : Num.NaNf;
                outp.Add(r);
            }
            float lt = leaveT ?? (times.Count > 0 ? times[times.Count - 1] : 0f);
            if (rol == 1) end = dflt;
            else if (rol == 2) end = saved;
            else end = saved.HasValue ? TagApply(o, lt, saved.Value) : null;
            return outp;
        }

        public sealed class TagInfo
        {
            public string Storage;
            public long Flags;
            public long? Iterations;
            public List<double> Data = new List<double>();
            public long Mode;
            public string ModeName;
            public float Value;
            public long ResetOnLeave;
            public string ResetName;
            public object ValueAsset;
        }

        public static TagInfo GetTagInfo(GdStruct o)
        {
            TagInfo ti = new TagInfo();
            long fl = o.GetInt("SplineStorageFlags");
            ti.Flags = fl;
            if (fl == 0x10) { ti.Storage = "samples"; ti.Data = FloatsOf(o["Spline"]).Select(x => (double)x).ToList(); }
            else if (fl == 0x40) { ti.Storage = "bytes"; ti.Data = Bytes(o["SplineRaw"]).Select(x => (double)x).ToList(); }
            else if ((fl & 0x20) != 0) { ti.Storage = "bezier"; ti.Data = FloatsOf(o["Spline"]).Select(x => (double)x).ToList(); ti.Iterations = fl & 0xF; }
            else ti.Storage = "none";
            ti.Mode = o.GetInt("Mode");
            string mn;
            ti.ModeName = TagModes.TryGetValue((int)ti.Mode, out mn) ? mn : "writes 0";
            ti.Value = o.GetFloat("Value");
            object rol = BaseValue(o, "ResetOnLeave");
            ti.ResetOnLeave = rol == null ? 0 : Num.ToLong(rol);
            string rn;
            ti.ResetName = ResetOnLeaveNames.TryGetValue((int)ti.ResetOnLeave, out rn) ? rn : ResetOnLeaveNames[0];
            ti.ValueAsset = BaseValue(o, "ValueAsset");
            return ti;
        }

        /// <summary>problems the game would have with this FloatGameStateTag</summary>
        public static List<string> TagIssues(GdStruct o)
        {
            List<string> r = new List<string>();
            long fl = o.GetInt("SplineStorageFlags");
            if (fl == 0x10) { if (AllocModel.LenOf(o["Spline"]) == 0) r.Add("SplineStorageFlags 0x10 with an empty Spline: the curve is 0"); }
            else if (fl == 0x40) { if (AllocModel.LenOf(o["SplineRaw"]) == 0) r.Add("SplineStorageFlags 0x40 with an empty SplineRaw: the curve is 0"); }
            else if ((fl & 0x20) != 0)
            {
                long n = AllocModel.LenOf(o["Spline"]);
                if (n < 8) r.Add("a Bezier tag (SplineStorageFlags 0x2n) reads 8 Spline values; this one has " + n + ", the game reads past the array");
                if ((fl & 0xF) == 0) r.Add("a Bezier tag with 0 iterations evaluates the y curve at u = t (no x fit)");
            }
            else r.Add("SplineStorageFlags 0x" + fl.ToString("x2") + " selects no tag class: the game creates no tag");
            long mode = o.GetInt("Mode");
            if (!(0 <= mode && mode <= 7)) r.Add("Mode " + mode + " is outside 0..7: the tag writes 0");
            return r;
        }

        /// <summary>make the tag a sampled curve: floats in Spline (0x10), or quantized bytes in SplineRaw (0x40)</summary>
        public static void SetTagSamples(GdStruct o, IList<double> values, bool quantized = false)
        {
            if (values.Count == 0) throw new ArgumentException("a sampled tag needs at least one value");
            Field fs = o.Type.GetField("Spline"), fr = o.Type.GetField("SplineRaw");
            if (quantized)
            {
                o["SplineRaw"] = Coerce.ToField(fr, values.Select(x => (byte)Math.Max(0, Math.Min(255, (int)Math.Round(Math.Min(1.0, Math.Max(0.0, x)) * 255.0)))).ToArray());
                o["Spline"] = ObjCodec.EmptyList(fs);
                o["SplineStorageFlags"] = 0x40L;
            }
            else
            {
                o["Spline"] = Coerce.ToField(fs, values.Select(x => (float)x).ToList());
                o["SplineRaw"] = ObjCodec.EmptyList(fr);
                o["SplineStorageFlags"] = 0x10L;
            }
        }

        /// <summary>make the tag a cubic Bezier y(x) with control x values xs and y values ys (4 each)</summary>
        public static void SetTagBezier(GdStruct o, IList<double> xs, IList<double> ys, int iterations = 4)
        {
            if (xs.Count != 4 || ys.Count != 4) throw new ArgumentException("a Bezier tag has 4 x and 4 y control values");
            if (iterations < 0 || iterations > 15) throw new ArgumentException("iterations must be 0..15");
            o["Spline"] = Coerce.ToField(o.Type.GetField("Spline"), xs.Concat(ys).Select(x => (float)x).ToList());
            o["SplineRaw"] = ObjCodec.EmptyList(o.Type.GetField("SplineRaw"));
            o["SplineStorageFlags"] = (long)(0x20 | iterations);
        }

        /// <summary>(curve bytes, mask byte) of "default" or DofSetCurves[which] (DOF dof of a ForEachDof set)</summary>
        public static Tuple<byte[], int> WeightCurveGet(GdStruct o, string which, int dof = 0)
        {
            if (which == "default")
            {
                byte[] m = Bytes(o["DefaultInheritedMaskBytes"]);
                byte[] c = Bytes(o["DefaultCurveBytes"]);
                return Tuple.Create(c.Length > 0 ? c : new byte[4], m.Length > 0 ? (int)m[0] : 0);
            }
            GdStruct s = (GdStruct)((IList)o["DofSetCurves"])[int.Parse(which)];
            byte[] cb = Bytes(s["CurveBytes"]), mb = Bytes(s["InheritedMaskBytes"]);
            if (dof < 0 || dof >= mb.Length || cb.Length < 4 * dof + 4) throw new IndexOutOfRangeException("set " + which + " has " + mb.Length + " curves");
            return Tuple.Create(cb.Skip(4 * dof).Take(4).ToArray(), (int)mb[dof]);
        }

        /// <summary>replace one curve (4 bytes) and optionally its inherited-mask byte (0 or 0xFF)</summary>
        public static void WeightCurveSet(GdStruct o, string which, byte[] curve, int? mask = null, int dof = 0)
        {
            if (curve.Length != 4) throw new ArgumentException("a weight curve is 4 bytes");
            if (which == "default")
            {
                o["DefaultCurveBytes"] = Coerce.ToField(o.Type.GetField("DefaultCurveBytes"), (byte[])curve.Clone());
                if (mask.HasValue)
                {
                    byte[] m = (byte[])Bytes(o["DefaultInheritedMaskBytes"]).Clone();
                    if (m.Length == 0) m = new byte[1];
                    m[0] = (byte)(mask.Value & 0xFF);
                    o["DefaultInheritedMaskBytes"] = Coerce.ToField(o.Type.GetField("DefaultInheritedMaskBytes"), m);
                }
                return;
            }
            GdStruct s = (GdStruct)((IList)o["DofSetCurves"])[int.Parse(which)];
            byte[] cb = (byte[])Bytes(s["CurveBytes"]).Clone(), mb = (byte[])Bytes(s["InheritedMaskBytes"]).Clone();
            if (dof < 0 || dof >= mb.Length || cb.Length < 4 * dof + 4) throw new IndexOutOfRangeException("set " + which + " has " + mb.Length + " curves");
            Array.Copy(curve, 0, cb, 4 * dof, 4);
            s["CurveBytes"] = Coerce.ToField(s.Type.GetField("CurveBytes"), cb);
            if (mask.HasValue)
            {
                mb[dof] = (byte)(mask.Value & 0xFF);
                s["InheritedMaskBytes"] = Coerce.ToField(s.Type.GetField("InheritedMaskBytes"), mb);
            }
        }

        /// <summary>problems the game would have with this DofWeightCurvesAsset</summary>
        public static List<string> WeightIssues(GdStruct o)
        {
            List<string> r = new List<string>();
            if (Bytes(o["DefaultCurveBytes"]).Length != 4) r.Add("DefaultCurveBytes must be 4 bytes");
            if (Bytes(o["DefaultInheritedMaskBytes"]).Length == 0) r.Add("DefaultInheritedMaskBytes is empty");
            IList sets = o["DofSetCurves"] as IList ?? new object[0];
            for (int j = 0; j < sets.Count; j++)
            {
                GdStruct s = sets[j] as GdStruct;
                if (s == null) continue;
                byte[] cb = Bytes(s["CurveBytes"]), mb = Bytes(s["InheritedMaskBytes"]);
                if (cb.Length != 4 * mb.Length) r.Add("DofSetCurves[" + j + "]: " + cb.Length + " CurveBytes for " + mb.Length + " InheritedMaskBytes (4 per mask byte)");
                if (!s.GetBool("ForEachDof") && mb.Length != 1) r.Add("DofSetCurves[" + j + "]: ForEachDof is false but it has " + mb.Length + " curves");
                for (int i = 0; i < mb.Length; i++)
                    if (mb[i] != 0 && mb[i] != 0xFF)
                        r.Add("DofSetCurves[" + j + "] DOF " + i + ": mask byte 0x" + mb[i].ToString("x2") + " (the game's SIMD path uses it as a bit mask; use 0 or 0xFF)");
            }
            return r;
        }
    }
}
