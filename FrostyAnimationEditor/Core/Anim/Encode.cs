using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;

namespace FrostyAnimationEditor.Core.Anim
{
    /// <summary>codec-independent animation keys: channels in rig channel order, values [nkeys][comps] per channel</summary>
    public sealed class ClipData
    {
        public List<long> KeyFrames;
        public List<string> Kinds;
        public List<double[][]> Values;
        public bool Loop;
        public long EndFrame;

        public ClipData(IList<long> keyFrames, IList<string> kinds, IList<double[][]> values, bool loop = false, long? endFrame = null)
        {
            KeyFrames = new List<long>(keyFrames);
            Kinds = new List<string>(kinds);
            Values = new List<double[][]>(values);
            Loop = loop;
            EndFrame = endFrame.HasValue ? endFrame.Value : (KeyFrames.Count > 0 ? KeyFrames[KeyFrames.Count - 1] : 0);
            if (Kinds.Count != Values.Count) throw new ArgumentException("kinds/values length mismatch");
            for (int i = 0; i < Values.Count; i++)
                if (Values[i].Length != KeyFrames.Count) throw new ArgumentException("channel " + i + " has " + Values[i].Length + " keys, expected " + KeyFrames.Count);
        }

        public int NKeys { get { return KeyFrames.Count; } }

        public static double[][] ToDouble(float[][] v)
        {
            double[][] r = new double[v.Length][];
            for (int i = 0; i < v.Length; i++)
            {
                r[i] = new double[v[i].Length];
                for (int c = 0; c < v[i].Length; c++) r[i][c] = v[i][c];
            }
            return r;
        }
    }

    public sealed class VbrError
    {
        public double Quat, Vec3, Float;
        public override string ToString() { return "quat " + Quat.ToString("G5") + ", vec3 " + Vec3.ToString("G5") + ", float " + Float.ToString("G5"); }
    }

    /// <summary>
    /// Encoders: VbrAnimationAsset / RawAnimationAsset field values from animation keys.
    /// VBR follows the asset tool's own algorithm: normalise per range
    /// group, 8-point DCT-II per 8-key block, Dct = max |coefficient|, per-block quantiser rows (w0 quats, w1 scalars
    /// [4Q, 4Q+4], w2 the rest) by bisection against the error tolerance, width nibbles = bit length of the largest
    /// |q|, LSB-first flag/sign/magnitude bit stream, block bodies byte-reversed, constant channels through a float
    /// palette, the channel map and the key-time table run-length coded.  Every encoded clip can be decoded again with
    /// the runtime port (VbrClip) to report the error.
    /// </summary>
    public static class Encode
    {
        public const double TolDefault = 0.00105;   // the tool's per-scalar tolerance (0.105 % of the channel range)

        private static readonly double[,] Cos = BuildCos();
        private static readonly double[,] Inv = BuildInv();

        private static double[,] BuildCos()
        {
            double[,] c = new double[8, 8];
            for (int r = 0; r < 8; r++) for (int k = 0; k < 8; k++) c[r, k] = Math.Cos(Math.PI * (2 * r + 1) * k / 16.0);
            return c;
        }

        private static double[,] BuildInv()
        {
            double[,] c = new double[8, 8];
            for (int r = 0; r < 8; r++) for (int k = 0; k < 8; k++) c[r, k] = k == 0 ? 0.125 : Math.Cos(Math.PI * (2 * r + 1) * k / 16.0) / 4.0;
            return c;
        }

        // ------------------------------------------------------------------ key-time table / channel map
        /// <summary>[run][gap-1] byte pairs over runs of consecutive frames, the last run alone</summary>
        public static byte[] KeyTimeTable(IList<long> frames)
        {
            if (frames.Count == 0) return new byte[0];
            if (frames[0] != 0) throw new ArgumentException("the first key must be at frame 0");
            List<long[]> runs = new List<long[]>();
            long run = 1;
            for (int i = 0; i + 1 < frames.Count; i++)
            {
                long g = frames[i + 1] - frames[i];
                if (g <= 0) throw new ArgumentException("key frames must increase");
                if (g == 1) run++;
                else { runs.Add(new[] { run, g }); run = 1; }
            }
            List<byte> outp = new List<byte>();
            foreach (long[] rg in runs)
            {
                long r = rg[0], g = rg[1];
                while (r > 255) { outp.Add(255); outp.Add(0); r -= 255; }
                if (g - 1 > 255) throw new ArgumentException("key gap " + g + " > 256 frames is not representable");
                outp.Add((byte)r);
                outp.Add((byte)(g - 1));
            }
            while (run > 255) { outp.Add(255); outp.Add(0); run -= 255; }
            outp.Add((byte)run);
            return outp.ToArray();
        }

        /// <summary>channel map: alternating run lengths starting with the run of false (animated)</summary>
        public static byte[] RleBits(IList<bool> bits)
        {
            List<byte> outp = new List<byte>();
            bool cur = false;
            int i = 0, n = bits.Count;
            while (i < n)
            {
                int j = i;
                while (j < n && bits[j] == cur) j++;
                int r = j - i;
                while (r > 255) { outp.Add(255); outp.Add(0); r -= 255; }
                outp.Add((byte)r);
                cur = !cur;
                i = j;
            }
            return outp.ToArray();
        }

        // ------------------------------------------------------------------ VBR
        private static void Rng(double lo, double hi, out float flo, out float fhi)
        {
            if (double.IsNaN(lo) || double.IsInfinity(lo) || double.IsNaN(hi) || double.IsInfinity(hi)) { lo = -1.0; hi = 1.0; }
            if (hi - lo < 1.53e-5 * 4)
            {
                double mid = 0.5 * (lo + hi);
                lo = mid - 1e-4;
                hi = mid + 1e-4;
            }
            flo = (float)lo;
            fhi = (float)hi;
        }

        private static double[] StepRow(double dct, int w)
        {
            float d = (float)dct;
            float fw = (float)((float)w + 1.0f) * (float)(d * (float)0.2);
            float bas = d * (float)3.0517578e-05;
            double[] r = new double[8];
            for (int k = 0; k < 8; k++) r[k] = (float)((float)((float)(fw * (float)VbrClip.LN[k]) + 1.0f) * bas);
            return r;
        }

        private static double MaxAbsDiff(double[][] v)
        {
            double m = 0;
            for (int k = 0; k < v.Length; k++)
                for (int c = 0; c < v[k].Length; c++)
                {
                    double d = Math.Abs(v[k][c] - v[0][c]);
                    if (d > m || double.IsNaN(d)) m = d;
                }
            return m;
        }

        private sealed class FloatKey : IEqualityComparer<float>
        {
            public bool Equals(float a, float b) { return a == b; }
            public int GetHashCode(float f) { return f == 0 ? 0 : BitConverter.ToInt32(BitConverter.GetBytes(f), 0); }
        }

        private static long BitLength(long x)
        {
            long n = 0;
            while (x > 0) { n++; x >>= 1; }
            return n;
        }

        /// <summary>-> VbrAnimationAsset field values (Data/palette/blocks ready to store)</summary>
        public static Dictionary<string, object> EncodeVbr(ClipData clip, double tolerance = TolDefault, long flagsExtra = 0x100, double constEps = 1e-7)
        {
            int K = clip.NKeys;
            List<string> kinds = clip.Kinds;
            int nQuat = kinds.Count(k => k == "quat"), nVec = kinds.Count(k => k == "vec3");
            for (int i = 0; i < kinds.Count; i++)
            {
                bool ok = i < nQuat ? kinds[i] == "quat" : (i < nQuat + nVec ? kinds[i] == "vec3" : kinds[i] == "float");
                if (!ok) throw new ArgumentException("VBR needs rig channels ordered quats, vec3s, floats");
            }
            List<double[][]> vals = new List<double[][]>();
            for (int ch = 0; ch < kinds.Count; ch++)
            {
                string kd = kinds[ch];
                double[][] src = clip.Values[ch];
                int nc = kd == "quat" ? 4 : kd == "vec3" ? 3 : 1;
                double[][] v = new double[K][];
                for (int k = 0; k < K; k++)
                {
                    v[k] = new double[nc];
                    for (int c = 0; c < nc; c++) v[k][c] = c < src[k].Length ? src[k][c] : 0.0;
                }
                if (kd == "quat")
                {
                    for (int k = 0; k < K; k++)
                    {
                        double s = ((v[k][0] * v[k][0] + v[k][1] * v[k][1]) + v[k][2] * v[k][2]) + v[k][3] * v[k][3];
                        double n = Math.Sqrt(s);
                        if (n == 0) n = 1;
                        for (int c = 0; c < 4; c++) v[k][c] = v[k][c] / n;
                    }
                    // keep consecutive keys on the same hemisphere (the runtime nlerps without a sign test)
                    for (int k = 1; k < K; k++)
                    {
                        double dot = v[k][0] * v[k - 1][0] + v[k][1] * v[k - 1][1] + v[k][2] * v[k - 1][2] + v[k][3] * v[k - 1][3];
                        if (dot < 0) for (int c = 0; c < 4; c++) v[k][c] = -v[k][c];
                    }
                }
                vals.Add(v);
            }
            bool[] cst = vals.Select(v => K <= 1 || MaxAbsDiff(v) <= constEps).ToArray();
            List<int> qi = Enumerable.Range(0, kinds.Count).Where(i => kinds[i] == "quat").ToList();
            List<int> vi = Enumerable.Range(0, kinds.Count).Where(i => kinds[i] == "vec3").ToList();
            List<int> fi = Enumerable.Range(0, kinds.Count).Where(i => kinds[i] == "float").ToList();
            List<int> aq = qi.Where(i => !cst[i]).ToList(), av = vi.Where(i => !cst[i]).ToList(), af = fi.Where(i => !cst[i]).ToList();
            List<int> cq = qi.Where(i => cst[i]).ToList(), cv = vi.Where(i => cst[i]).ToList(), cf = fi.Where(i => cst[i]).ToList();
            int Q = aq.Count, V = av.Count, F = af.Count;
            Func<IList<int>, double[]> mm = idx =>
            {
                if (idx.Count == 0) return null;
                double lo = double.PositiveInfinity, hi = double.NegativeInfinity;
                bool nan = false;
                foreach (int i in idx) foreach (double[] row in vals[i]) foreach (double x in row)
                        {
                            if (double.IsNaN(x)) nan = true;
                            if (x < lo) lo = x;
                            if (x > hi) hi = x;
                        }
                if (nan) return new[] { double.NaN, double.NaN };
                return new[] { lo, hi };
            };
            double[] rq = mm(aq) ?? new[] { -1.0, 1.0 };
            double[] traj = V > 0 ? mm(av.Take(1).ToList()) : null;
            double[] restV = V > 1 ? mm(av.Skip(1).ToList()) : null;
            double[] rv = restV ?? traj ?? mm(cv) ?? new[] { -1.0, 1.0 };
            double[] rt = traj ?? rv;
            double[] rf = mm(af) ?? mm(cf) ?? new[] { 0.0, 1.0 };
            float qmin, qmax, tmin, tmax, vmin, vmax, fmin, fmax;
            Rng(rq[0], rq[1], out qmin, out qmax);
            Rng(rt[0], rt[1], out tmin, out tmax);
            Rng(rv[0], rv[1], out vmin, out vmax);
            Rng(rf[0], rf[1], out fmin, out fmax);
            // animated scalars in VBR order + their (min, max)
            List<double[]> cols = new List<double[]>();
            List<double> cmin = new List<double>(), cmax = new List<double>();
            foreach (int i in aq)
                for (int c = 0; c < 4; c++) { cols.Add(vals[i].Select(r => r[c]).ToArray()); cmin.Add(qmin); cmax.Add(qmax); }
            for (int n_ = 0; n_ < av.Count; n_++)
            {
                double lo = n_ == 0 ? tmin : vmin, hi = n_ == 0 ? tmax : vmax;
                for (int c = 0; c < 3; c++) { cols.Add(vals[av[n_]].Select(r => r[c]).ToArray()); cmin.Add(lo); cmax.Add(hi); }
            }
            foreach (int i in af) { cols.Add(vals[i].Select(r => r[0]).ToArray()); cmin.Add(fmin); cmax.Add(fmax); }
            int S = cols.Count;
            int NB = (K + 7) >> 3;
            List<byte[]> blocks = new List<byte[]>();
            byte[] widthWords;
            int flagBit2;
            double dct;
            if (S > 0)
            {
                // normalised [NB*8][S], padded with the last key
                double[,] N = new double[NB * 8, S];
                for (int s = 0; s < S; s++)
                    for (int k = 0; k < NB * 8; k++)
                    {
                        int kk = Math.Min(k, K - 1);
                        N[k, s] = (cols[s][kk] - cmin[s]) / (cmax[s] - cmin[s]);
                    }
                // C[b][k][s] = sum_r COS[r][k] * (N[8b+r][s] - 0.5)
                double[][,] C = new double[NB][,];
                double dmax = 0;
                for (int b = 0; b < NB; b++)
                {
                    C[b] = new double[8, S];
                    for (int k = 0; k < 8; k++)
                        for (int s = 0; s < S; s++)
                        {
                            double acc = 0;
                            for (int r = 0; r < 8; r++) acc += Cos[r, k] * (N[8 * b + r, s] - 0.5);
                            C[b][k, s] = acc;
                            double a = Math.Abs(acc);
                            if (a > dmax || double.IsNaN(a)) dmax = a;
                        }
                }
                dct = dmax;
                if (dct == 0.0) dct = 1e-6;
                dct = (float)dct;
                Func<int, int> lane = sc => sc < 4 * Q ? sc : (sc < 4 * Q + 3 * V ? sc + (sc - 4 * Q) / 3 : sc + V);
                List<int>[] grp = new List<int>[3];
                grp[0] = Enumerable.Range(0, S).Where(sc => lane(sc) < 4 * Q).ToList();
                grp[1] = Enumerable.Range(0, S).Where(sc => 4 * Q <= lane(sc) && lane(sc) <= 4 * Q + 4).ToList();
                grp[2] = Enumerable.Range(0, S).Where(sc => lane(sc) > 4 * Q + 4).ToList();
                double[][] steps = new double[256][];
                for (int w = 0; w < 256; w++) steps[w] = StepRow(dct, w);
                int[,] W = new int[NB, 3];
                long[][,] Qint = new long[NB][,];
                for (int b = 0; b < NB; b++)
                {
                    Qint[b] = new long[8, S];
                    int nk = Math.Min(8, K - 8 * b);
                    for (int gi = 0; gi < 3; gi++)
                    {
                        List<int> idx = grp[gi];
                        if (idx.Count == 0) continue;
                        Func<int, Tuple<double[,], double>> quant = w =>
                        {
                            double[] st = steps[w];
                            double[,] q = new double[8, idx.Count];
                            for (int k = 0; k < 8; k++)
                                for (int j = 0; j < idx.Count; j++) q[k, j] = Math.Round(C[b][k, idx[j]] / st[k], MidpointRounding.ToEven);
                            double err = 0;
                            for (int r = 0; r < nk; r++)
                                for (int j = 0; j < idx.Count; j++)
                                {
                                    double acc = 0;
                                    for (int k = 0; k < 8; k++) acc += Inv[r, k] * (q[k, j] * st[k]);
                                    double rec = 0.5 + acc;
                                    double e = Math.Abs(rec - N[8 * b + r, idx[j]]);
                                    if (e > err || double.IsNaN(e)) err = e;
                                }
                            return Tuple.Create(q, err);
                        };
                        int loW = 0, hiW = 255;
                        Tuple<double[,], double> q0 = quant(0);
                        int best = 0;
                        double[,] bq = q0.Item1;
                        if (q0.Item2 <= tolerance)
                            while (loW < hiW)
                            {
                                int mid = (loW + hiW + 1) / 2;
                                Tuple<double[,], double> qm = quant(mid);
                                if (qm.Item2 <= tolerance) { loW = mid; best = mid; bq = qm.Item1; }
                                else hiW = mid - 1;
                            }
                        W[b, gi] = best;
                        for (int k = 0; k < 8; k++)
                            for (int j = 0; j < idx.Count; j++) Qint[b][k, idx[j]] = (long)bq[k, j];
                    }
                }
                // widths: bit length of the largest |q| per (scalar, coefficient)
                long[,] widths = new long[8, S];
                long wmax = 0;
                for (int k = 0; k < 8; k++)
                    for (int s = 0; s < S; s++)
                    {
                        long mx = 0;
                        for (int b = 0; b < NB; b++) mx = Math.Max(mx, Math.Abs(Qint[b][k, s]));
                        widths[k, s] = mx > 0 ? BitLength(mx) : 0;
                        wmax = Math.Max(wmax, widths[k, s]);
                    }
                if (wmax > 15) throw new GdException("a coefficient needs more than 15 bits");
                long cost = 0;
                for (int s = 0; s < S; s++)
                {
                    long c = 0;
                    for (int k = 0; k < 8; k++) if (widths[k, s] > 0) c += widths[k, s] + 2;
                    cost = Math.Max(cost, c);
                }
                flagBit2 = cost < 0x81 ? 4 : 0;
                for (int b = 0; b < NB; b++)
                {
                    List<byte> body = new List<byte>();
                    long nbits = 0;
                    Action<long, int> put = (v, n) =>
                    {
                        for (int i = 0; i < n; i++)
                        {
                            long bit = (v >> i) & 1;
                            long p = nbits + i;
                            while (body.Count <= (p >> 3)) body.Add(0);
                            if (bit != 0) body[(int)(p >> 3)] |= (byte)(1 << (int)(p & 7));
                        }
                        nbits += n;
                    };
                    for (int s = 0; s < S; s++)
                    {
                        for (int k = 0; k < 8; k++) if (widths[k, s] != 0) put(Qint[b][k, s] != 0 ? 1 : 0, 1);
                        for (int k = 0; k < 8; k++) if (widths[k, s] != 0 && Qint[b][k, s] != 0) put(Qint[b][k, s] > 0 ? 1 : 0, 1);
                        for (int k = 0; k < 8; k++) if (widths[k, s] != 0 && Qint[b][k, s] != 0) put(Math.Abs(Qint[b][k, s]), (int)widths[k, s]);
                    }
                    int nbytes = (int)((nbits + 7) / 8);
                    while (body.Count < nbytes) body.Add(0);
                    byte[] blk = new byte[3 + nbytes];
                    blk[0] = (byte)W[b, 0]; blk[1] = (byte)W[b, 1]; blk[2] = (byte)W[b, 2];
                    for (int i = 0; i < nbytes; i++) blk[3 + i] = body[nbytes - 1 - i];
                    blocks.Add(blk);
                }
                widthWords = new byte[4 * S];
                for (int s = 0; s < S; s++)
                {
                    uint w = 0;
                    for (int k = 0; k < 8; k++) w |= (uint)widths[k, s] << (4 * k);
                    BE.W32LE(widthWords, 4 * s, w);
                }
            }
            else
            {
                dct = 1e-6;
                for (int b = 0; b < NB; b++) blocks.Add(new byte[3]);
                widthWords = new byte[0];
                flagBit2 = 4;
            }
            // constants + palette
            List<double> cvals = new List<double>();
            foreach (int i in cq) for (int c = 0; c < 4; c++) cvals.Add((vals[i][0][c] - qmin) / ((double)qmax - qmin));
            foreach (int i in cv) for (int c = 0; c < 3; c++) cvals.Add((vals[i][0][c] - vmin) / ((double)vmax - vmin));
            foreach (int i in cf) cvals.Add((vals[i][0][0] - fmin) / ((double)fmax - fmin));
            List<float> palette = new List<float>();
            List<byte> idxBytes = new List<byte>();
            Dictionary<float, int> pos = new Dictionary<float, int>(new FloatKey());
            foreach (double v in cvals)
            {
                float f32 = (float)v;
                int k;
                if (float.IsNaN(f32) || !pos.TryGetValue(f32, out k))
                {
                    if (palette.Count >= 256)
                    {
                        k = 0;
                        double bestd = double.PositiveInfinity;
                        for (int p = 0; p < palette.Count; p++)
                        {
                            double d = Math.Abs((double)palette[p] - f32);
                            if (d < bestd) { bestd = d; k = p; }
                        }
                    }
                    else
                    {
                        k = palette.Count;
                        palette.Add(f32);
                        if (!float.IsNaN(f32)) pos[f32] = k;
                    }
                }
                idxBytes.Add((byte)k);
            }
            List<bool> cmap = qi.Select(i => cst[i]).Concat(vi.Select(i => cst[i])).Concat(fi.Select(i => cst[i])).ToList();
            bool anyConst = cq.Count + cv.Count + cf.Count > 0;
            byte[] ccm = anyConst ? RleBits(cmap) : new byte[0];
            bool onePerFrame = true;
            for (int i = 0; i < K; i++) if (clip.KeyFrames[i] != i) { onePerFrame = false; break; }
            byte[] kts = onePerFrame ? new byte[0] : KeyTimeTable(clip.KeyFrames);
            List<byte> data = new List<byte>();
            data.AddRange(kts);
            data.AddRange(idxBytes);
            data.AddRange(ccm);
            data.AddRange(widthWords);
            foreach (byte[] b in blocks) data.AddRange(b);
            long flags = (clip.Loop ? 1 : 0) | (onePerFrame ? 2 : 0) | flagBit2 | (!anyConst ? 8 : 0) | (flagsExtra & ~0xFL);
            if (ccm.Length > 65535) throw new GdException("clip too large");
            Dictionary<string, object> fields = new Dictionary<string, object>();
            fields["QuatMin"] = qmin; fields["TrajMin"] = tmin; fields["Vec3Min"] = vmin; fields["FloatMin"] = fmin;
            fields["QuatMax"] = qmax; fields["TrajMax"] = tmax; fields["Vec3Max"] = vmax; fields["FloatMax"] = fmax;
            fields["VectorOffsetScale"] = 0f; fields["FloatOffsetScale"] = 0f; fields["Dct"] = (float)dct;
            fields["QuaternionCount"] = (long)Q; fields["Vector3Count"] = (long)V; fields["FloatCount"] = (long)F;
            fields["ConstQuaternionCount"] = (long)cq.Count; fields["ConstVector3Count"] = (long)cv.Count; fields["ConstFloatCount"] = (long)cf.Count;
            fields["KeyTimeSize"] = (long)kts.Length; fields["NumKeys"] = (long)K; fields["ConstChanMapSize"] = (long)ccm.Length;
            fields["ConstPaletteSize"] = (long)palette.Count; fields["VectorOffsetSize"] = 0L; fields["FloatOffsetSize"] = 0L;
            fields["Flags"] = flags;
            fields["ConstantPalette"] = palette;
            fields["FrameBlockSizes"] = blocks.Select(b => (long)b.Length).ToList();
            fields["Data"] = data.ToArray();
            return fields;
        }

        /// <summary>decode the encoded fields with the runtime port; -> max abs error per kind</summary>
        public static VbrError VerifyVbr(Dictionary<string, object> fields, ClipData clip)
        {
            Dictionary<string, object> d = new Dictionary<string, object>(fields);
            Hashtable bas = new Hashtable();
            bas["EndFrame"] = clip.EndFrame;
            d["__base"] = bas;
            VbrClip c = new VbrClip(n => { object v; return d.TryGetValue(n, out v) ? v : null; });
            float[][][] q, v3;
            float[][] f;
            c.KeyChannels(out q, out v3, out f);
            VbrError err = new VbrError();
            int qi = 0, vi = 0, fi = 0;
            for (int ch = 0; ch < clip.Kinds.Count; ch++)
            {
                string kd = clip.Kinds[ch];
                double[][] val = clip.Values[ch];
                double e = 0;
                if (kd == "quat")
                {
                    for (int k = 0; k < q.Length; k++)
                    {
                        double[] b = val[k];
                        double n = Math.Sqrt(((b[0] * b[0] + b[1] * b[1]) + b[2] * b[2]) + b[3] * b[3]);
                        n = Math.Max(n, 1e-12);
                        double em = 0, ep = 0;
                        for (int cc = 0; cc < 4; cc++)
                        {
                            em = Math.Max(em, Math.Abs(q[k][qi][cc] - b[cc] / n));
                            ep = Math.Max(ep, Math.Abs(q[k][qi][cc] + b[cc] / n));
                        }
                        e = Math.Max(e, Math.Min(em, ep));
                    }
                    qi++;
                    err.Quat = Math.Max(err.Quat, e);
                }
                else if (kd == "vec3")
                {
                    for (int k = 0; k < v3.Length; k++) for (int cc = 0; cc < 3; cc++) e = Math.Max(e, Math.Abs(v3[k][vi][cc] - val[k][cc]));
                    vi++;
                    err.Vec3 = Math.Max(err.Vec3, e);
                }
                else
                {
                    for (int k = 0; k < f.Length; k++) e = Math.Max(e, Math.Abs(f[k][fi] - val[k][0]));
                    fi++;
                    err.Float = Math.Max(err.Float, e);
                }
            }
            return err;
        }

        /// <summary>decode the encoded fields with the runtime port and sample them at every key frame of full (the clip
        /// before key reduction); -> max abs error per kind over those frames</summary>
        public static VbrError VerifyVbrFrames(Dictionary<string, object> fields, ClipData full)
        {
            Dictionary<string, object> d = new Dictionary<string, object>(fields);
            Hashtable bas = new Hashtable();
            bas["EndFrame"] = full.EndFrame;
            d["__base"] = bas;
            VbrClip c = new VbrClip(n => { object v; return d.TryGetValue(n, out v) ? v : null; });
            float[][][] q, v3;
            float[][] f;
            c.SampleFrames(full.KeyFrames.Select(x => (int)x).ToList(), out q, out v3, out f);
            VbrError err = new VbrError();
            int qi = 0, vi = 0, fi = 0;
            for (int ch = 0; ch < full.Kinds.Count; ch++)
            {
                string kd = full.Kinds[ch];
                double[][] val = full.Values[ch];
                double e = 0;
                for (int k = 0; k < full.NKeys; k++)
                {
                    double[] b = val[k];
                    if (kd == "quat")
                    {
                        double n = Math.Max(Math.Sqrt(((b[0] * b[0] + b[1] * b[1]) + b[2] * b[2]) + b[3] * b[3]), 1e-12);
                        double em = 0, ep = 0;
                        for (int cc = 0; cc < 4; cc++)
                        {
                            em = Math.Max(em, Math.Abs(q[k][qi][cc] - b[cc] / n));
                            ep = Math.Max(ep, Math.Abs(q[k][qi][cc] + b[cc] / n));
                        }
                        e = Math.Max(e, Math.Min(em, ep));
                    }
                    else if (kd == "vec3") for (int cc = 0; cc < 3; cc++) e = Math.Max(e, Math.Abs(v3[k][vi][cc] - b[cc]));
                    else e = Math.Max(e, Math.Abs(f[k][fi] - b[0]));
                }
                if (kd == "quat") { qi++; err.Quat = Math.Max(err.Quat, e); }
                else if (kd == "vec3") { vi++; err.Vec3 = Math.Max(err.Vec3, e); }
                else { fi++; err.Float = Math.Max(err.Float, e); }
            }
            return err;
        }

        // ------------------------------------------------------------------ key reduction
        /// <summary>
        /// Drop the keys the runtime's interpolation reproduces. The VBR sampler fills a frame between
        /// two keys by nlerp (quats) / lerp (vec3, float) at alpha = the frame's place between the two key times, and the
        /// shipped clips rely on it: the Drogba celebration stores 37 keys for its 118 frames, the violin one 37 for 155
        /// (all VBR clips together: 87.7 % of their frames are keys). The rule EA's pipeline used to choose the keys is
        /// not in the game: its compressor takes the key times from the source .chan.
        /// This one is greedy: from a kept key, the farthest later key such that every frame in between is reproduced
        /// within tolerance, measured like the quantiser measures (per scalar, as a fraction of the range of its group:
        /// quats; the trajectory vector; the other vectors; floats). The first and last keys stay; a gap is at most 256
        /// frames (the key-time table's limit). Constant channels do not take part (the encoder stores them once).
        /// </summary>
        public static ClipData ReduceKeys(ClipData clip, double tolerance, double constEps = 1e-7, IList<bool> skip = null)
        {
            int K = clip.NKeys;
            if (K <= 2 || !(tolerance > 0)) return clip;
            List<string> kinds = clip.Kinds;
            int nch = kinds.Count;
            List<double[][]> vals = new List<double[][]>();
            for (int ch = 0; ch < nch; ch++)
            {
                int nc = kinds[ch] == "quat" ? 4 : kinds[ch] == "vec3" ? 3 : 1;
                double[][] src = clip.Values[ch];
                double[][] v = new double[K][];
                for (int k = 0; k < K; k++)
                {
                    v[k] = new double[nc];
                    for (int c = 0; c < nc; c++) v[k][c] = c < src[k].Length ? src[k][c] : 0.0;
                }
                if (kinds[ch] == "quat")
                {
                    // as the encoder sees them: unit length, consecutive keys on one hemisphere
                    for (int k = 0; k < K; k++)
                    {
                        double n = Math.Sqrt(((v[k][0] * v[k][0] + v[k][1] * v[k][1]) + v[k][2] * v[k][2]) + v[k][3] * v[k][3]);
                        if (n == 0) n = 1;
                        for (int c = 0; c < 4; c++) v[k][c] = v[k][c] / n;
                    }
                    for (int k = 1; k < K; k++)
                    {
                        double dot = v[k][0] * v[k - 1][0] + v[k][1] * v[k - 1][1] + v[k][2] * v[k - 1][2] + v[k][3] * v[k - 1][3];
                        if (dot < 0) for (int c = 0; c < 4; c++) v[k][c] = -v[k][c];
                    }
                }
                vals.Add(v);
            }
            bool[] cst = vals.Select(v => MaxAbsDiff(v) <= constEps).ToArray();
            List<int> aq = Enumerable.Range(0, nch).Where(i => kinds[i] == "quat" && !cst[i]).ToList();
            List<int> av = Enumerable.Range(0, nch).Where(i => kinds[i] == "vec3" && !cst[i]).ToList();
            List<int> af = Enumerable.Range(0, nch).Where(i => kinds[i] == "float" && !cst[i]).ToList();
            if (aq.Count + av.Count + af.Count == 0)
                return new ClipData(new[] { clip.KeyFrames[0], clip.KeyFrames[K - 1] }, kinds, clip.Values.Select(v => new[] { v[0], v[K - 1] }).ToList(), clip.Loop, clip.EndFrame);
            Func<IList<int>, double> width = idx =>
            {
                double lo = double.PositiveInfinity, hi = double.NegativeInfinity;
                foreach (int i in idx) foreach (double[] row in vals[i]) foreach (double x in row) { if (x < lo) lo = x; if (x > hi) hi = x; }
                float flo, fhi;
                Rng(lo, hi, out flo, out fhi);
                return (double)fhi - flo;
            };
            // per-channel limit in value units (the encoder's group ranges)
            double[] lim = new double[nch];
            if (aq.Count > 0) { double w = width(aq); foreach (int i in aq) lim[i] = tolerance * w; }
            if (av.Count > 0)
            {
                double wt = width(av.Take(1).ToList());
                double wv = av.Count > 1 ? width(av.Skip(1).ToList()) : wt;
                lim[av[0]] = tolerance * wt;
                foreach (int i in av.Skip(1)) lim[i] = tolerance * wv;
            }
            if (af.Count > 0) { double w = width(af); foreach (int i in af) lim[i] = tolerance * w; }
            List<int> live = aq.Concat(av).Concat(af).Where(i => skip == null || i >= skip.Count || !skip[i]).ToList();
            List<long> F = clip.KeyFrames;
            double[] tmp = new double[4];
            Func<int, int, bool> fits = (a, b) =>
            {
                double span = F[b] - F[a];
                foreach (int ch in live)
                {
                    double[][] v = vals[ch];
                    double[] va = v[a], vb = v[b];
                    bool quat = kinds[ch] == "quat";
                    if (quat && va[0] * vb[0] + va[1] * vb[1] + va[2] * vb[2] + va[3] * vb[3] < 0) return false;
                    double L = lim[ch];
                    for (int k = a + 1; k < b; k++)
                    {
                        double al = (F[k] - F[a]) / span;
                        double[] vk = v[k];
                        int nc = va.Length;
                        double s = 0;
                        for (int c = 0; c < nc; c++) { tmp[c] = va[c] + (vb[c] - va[c]) * al; s += tmp[c] * tmp[c]; }
                        double n = quat ? Math.Sqrt(s) : 1.0;
                        if (n == 0) n = 1;
                        for (int c = 0; c < nc; c++) if (Math.Abs(tmp[c] / n - vk[c]) > L) return false;
                    }
                }
                return true;
            };
            List<int> keep = new List<int> { 0 };
            int at = 0;
            while (at < K - 1)
            {
                int best = at + 1;
                for (int b = at + 2; b < K && F[b] - F[at] <= 256; b++)
                {
                    if (fits(at, b)) best = b;
                    else if (b - best > 4) break;
                }
                keep.Add(best);
                at = best;
            }
            if (keep.Count == K) return clip;
            return new ClipData(keep.Select(k => F[k]).ToList(), kinds, clip.Values.Select(v => keep.Select(k => v[k]).ToArray()).ToList(), clip.Loop, clip.EndFrame);
        }

        // ------------------------------------------------------------------ RAW
        public static Dictionary<string, object> EncodeRaw(ClipData clip, double constEps = 1e-7)
        {
            int K = clip.NKeys;
            List<string> kinds = clip.Kinds;
            List<float[][]> vals = new List<float[][]>();
            for (int ch = 0; ch < kinds.Count; ch++)
            {
                int nc = kinds[ch] == "quat" ? 4 : kinds[ch] == "vec3" ? 3 : 1;
                float[][] v = new float[K][];
                for (int k = 0; k < K; k++)
                {
                    v[k] = new float[nc];
                    for (int c = 0; c < nc; c++) v[k][c] = c < clip.Values[ch][k].Length ? (float)clip.Values[ch][k][c] : 0f;
                }
                vals.Add(v);
            }
            bool[] cst = new bool[vals.Count];
            for (int i = 0; i < vals.Count; i++)
            {
                double m = 0;
                for (int k = 0; k < K; k++) for (int c = 0; c < vals[i][k].Length; c++) m = Math.Max(m, Math.Abs((double)(float)(vals[i][k][c] - vals[i][0][c])));
                cst[i] = K <= 1 || m <= (double)(float)constEps;     // numpy compares the float32 maximum in float32
            }
            List<int> stored = new List<int>();
            foreach (bool wantConst in new[] { false, true })
                foreach (string kd in new[] { "quat", "vec3", "float" })
                    for (int i = 0; i < kinds.Count; i++) if (kinds[i] == kd && cst[i] == wantConst) stored.Add(i);
            int Q = stored.Count(i => kinds[i] == "quat" && !cst[i]);
            int V = stored.Count(i => kinds[i] == "vec3" && !cst[i]);
            int F = stored.Count(i => kinds[i] == "float" && !cst[i]);
            int CQ = stored.Count(i => kinds[i] == "quat" && cst[i]);
            int CV = stored.Count(i => kinds[i] == "vec3" && cst[i]);
            int CF = stored.Count(i => kinds[i] == "float" && cst[i]);
            int stride = (4 * Q + 4 * V + F + 3) & ~3;
            float[] data = new float[K * stride];
            int o = 0;
            foreach (int i in stored.Where(i => !cst[i]))
            {
                int n = kinds[i] == "quat" || kinds[i] == "vec3" ? 4 : 1;
                int w = vals[i].Length > 0 ? vals[i][0].Length : 0;
                for (int k = 0; k < K; k++) for (int c = 0; c < w; c++) data[k * stride + o + c] = vals[i][k][c];
                o += n;
            }
            List<float> cdata = new List<float>();
            foreach (int i in stored)
            {
                if (!cst[i]) continue;
                if (kinds[i] == "quat") cdata.AddRange(vals[i][0].Take(4));
                else if (kinds[i] == "vec3") { cdata.AddRange(vals[i][0].Take(3)); cdata.Add(0f); }
                else cdata.Add(vals[i][0][0]);
            }
            List<long> mapping = stored.Select(x => (long)x).ToList();
            long[] inverse = new long[stored.Count];
            for (int s = 0; s < stored.Count; s++) inverse[stored[s]] = s;
            Dictionary<string, object> fields = new Dictionary<string, object>();
            fields["KeyTimes"] = new List<long>(clip.KeyFrames);
            fields["MappingIndices"] = mapping;
            fields["ChannelIndices"] = inverse.ToList();
            fields["Data"] = data.ToList();
            fields["ConstData"] = cdata;
            fields["FloatCount"] = (long)F; fields["Vec3Count"] = (long)V; fields["QuatCount"] = (long)Q;
            fields["ConstFloatCount"] = (long)CF; fields["ConstVec3Count"] = (long)CV; fields["ConstQuatCount"] = (long)CQ;
            fields["NumKeys"] = (long)K;
            fields["Cycle"] = clip.Loop;
            return fields;
        }
    }
}
