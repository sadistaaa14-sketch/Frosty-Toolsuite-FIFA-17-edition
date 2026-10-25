using System;
using System.Collections;
using System.Collections.Generic;

namespace FrostyAnimationEditor.Core.Anim
{
    /// <summary>
    /// VbrAnimationAsset ("VBR " codec), decoded the way the game decodes it:
    ///   every block body [o+3, o+size-1] is stored byte-reversed;
    ///   seek: Flags bit0 loop, bit1 one key per frame, key-time RLE at the start of Data;
    ///   blocks: (per animated scalar a u32 of 8 nibble widths; per coefficient with
    ///   width != 0 one flag bit; per set flag one sign bit (1 = +); then magnitudes, LSB-first; scalar s
    ///   goes to lane s (quats), s + (s-4Q)/3 (vectors, 4 lanes each), s + V (floats)),
    ///   then dequantise + 8-point IDCT + range map per 4-lane group, offset ramps, and the pose write.
    /// </summary>
    public sealed class VbrClip
    {
        public const uint Tag = 0x56425220;   // "VBR "

        public static readonly double[] LN = BuildLn();
        public static readonly float[,] BasisF = BuildBasis();

        private static double[] BuildLn()
        {
            double[] r = new double[8];
            for (int k = 0; k < 8; k++) r[k] = Math.Log(k + 2);
            return r;
        }

        private static float[,] BuildBasis()
        {
            float[,] b = new float[8, 8];
            for (int r = 0; r < 8; r++)
                for (int k = 0; k < 8; k++)
                    b[r, k] = (float)(k == 0 ? 0.125 : Math.Cos(Math.PI * (2 * r + 1) * k / 16.0) / 4.0);
            return b;
        }

        public float QuatMin, TrajMin, Vec3Min, FloatMin, QuatMax, TrajMax, Vec3Max, FloatMax;
        public float VectorOffsetScale, FloatOffsetScale, Dct;
        public int QuaternionCount, Vector3Count, FloatCount, ConstQuaternionCount, ConstVector3Count, ConstFloatCount;
        public int KeyTimeSize, NumKeys, ConstChanMapSize, ConstPaletteSize, VectorOffsetSize, FloatOffsetSize;
        public long Flags;
        public float[] Palette;
        public int[] BlockSizes;
        public byte[] Data;
        public long? EndFrame;
        public float TrimOffset;
        public bool Additive;

        public int Q, V, F, CQ, CV, CF, NScal, Groups, Lanes;
        public int KtsOff, ConstOff, CcmOff, WidthOff, VosOff, FosOff, BlocksOff;
        public uint[] Widths;
        public int[] BlockStarts;
        private float[,] keys;

        private static long L(Func<string, object> g, string n) { object v = g(n); return v == null ? 0 : Num.ToLong(v); }
        private static float Fl(Func<string, object> g, string n) { object v = g(n); return v == null ? 0f : Num.ToFloat(v); }

        public VbrClip(GdStruct o) : this(n => o[n]) { }

        /// <summary>g = field getter (a VbrAnimationAsset object, or the encoder's field dictionary)</summary>
        public VbrClip(Func<string, object> g)
        {
            QuatMin = Fl(g, "QuatMin"); TrajMin = Fl(g, "TrajMin"); Vec3Min = Fl(g, "Vec3Min"); FloatMin = Fl(g, "FloatMin");
            QuatMax = Fl(g, "QuatMax"); TrajMax = Fl(g, "TrajMax"); Vec3Max = Fl(g, "Vec3Max"); FloatMax = Fl(g, "FloatMax");
            VectorOffsetScale = Fl(g, "VectorOffsetScale"); FloatOffsetScale = Fl(g, "FloatOffsetScale"); Dct = Fl(g, "Dct");
            QuaternionCount = (int)L(g, "QuaternionCount"); Vector3Count = (int)L(g, "Vector3Count"); FloatCount = (int)L(g, "FloatCount");
            ConstQuaternionCount = (int)L(g, "ConstQuaternionCount"); ConstVector3Count = (int)L(g, "ConstVector3Count"); ConstFloatCount = (int)L(g, "ConstFloatCount");
            KeyTimeSize = (int)L(g, "KeyTimeSize"); NumKeys = (int)L(g, "NumKeys"); ConstChanMapSize = (int)L(g, "ConstChanMapSize");
            ConstPaletteSize = (int)L(g, "ConstPaletteSize"); VectorOffsetSize = (int)L(g, "VectorOffsetSize"); FloatOffsetSize = (int)L(g, "FloatOffsetSize");
            Flags = L(g, "Flags");
            IList pal = g("ConstantPalette") as IList;
            Palette = new float[pal != null ? pal.Count : 0];
            for (int i = 0; i < Palette.Length; i++) Palette[i] = Num.ToFloat(pal[i]);
            IList bs = g("FrameBlockSizes") as IList;
            BlockSizes = new int[bs != null ? bs.Count : 0];
            for (int i = 0; i < BlockSizes.Length; i++) BlockSizes[i] = (int)Num.ToLong(bs[i]);
            Data = g("Data") as byte[] ?? new byte[0];
            object bo = g("__base");
            GdStruct b = bo as GdStruct;
            IDictionary bd = bo as IDictionary;
            if (b != null)
            {
                EndFrame = b["EndFrame"] != null ? (long?)Num.ToLong(b["EndFrame"]) : null;
                TrimOffset = b.GetFloat("TrimOffset");
                Additive = b.GetBool("Additive");
            }
            else if (bd != null)
            {
                EndFrame = bd.Contains("EndFrame") && bd["EndFrame"] != null ? (long?)Num.ToLong(bd["EndFrame"]) : null;
            }
            Layout();
        }

        private void Layout()
        {
            Q = QuaternionCount; V = Vector3Count; F = FloatCount;
            CQ = ConstQuaternionCount; CV = ConstVector3Count; CF = ConstFloatCount;
            NScal = 4 * Q + 3 * V + F;
            Groups = Q + V + (F + 3) / 4;
            Lanes = 4 * Groups;
            int o = 0;
            KtsOff = o; o += KeyTimeSize;
            ConstOff = o; o += 4 * CQ + 3 * CV + CF;
            CcmOff = o; o += ConstChanMapSize;
            WidthOff = o; o += 4 * NScal;
            VosOff = o; o += VectorOffsetSize;
            FosOff = o; o += FloatOffsetSize;
            BlocksOff = o;
            long total = o;
            foreach (int s in BlockSizes) total += s;
            if (total != Data.Length)
                throw new GdException("VBR Data size mismatch: prefix " + o + " + blocks " + (total - o) + " != " + Data.Length);
            Widths = new uint[NScal];
            for (int s = 0; s < NScal; s++) Widths[s] = BE.U32LE(Data, WidthOff + 4 * s);
            BlockStarts = new int[BlockSizes.Length];
            int p = BlocksOff;
            for (int i = 0; i < BlockSizes.Length; i++) { BlockStarts[i] = p; p += BlockSizes[i]; }
        }

        public bool Loops { get { return (Flags & 1) != 0; } }
        public bool OneKeyPerFrame { get { return (Flags & 2) != 0; } }

        /// <summary>over all rig channels (quats, then vec3, then floats): true = constant</summary>
        public bool[] ChannelMap()
        {
            int total = Q + CQ + V + CV + F + CF;
            List<bool> bits = new List<bool>(total);
            bool val = false;
            for (int i = 0; i < ConstChanMapSize; i++)
            {
                int n = Data[CcmOff + i];
                for (int k = 0; k < n; k++) bits.Add(val);
                val = !val;
            }
            while (bits.Count < total) bits.Add(val);
            bool[] r = new bool[total];
            for (int i = 0; i < total; i++) r[i] = bits[i];
            return r;
        }

        /// <summary>(quats [CQ][4], vecs [CV][3], floats [CF]) exactly as the game writes them</summary>
        public void Constants(out float[][] q, out float[][] v, out float[] f)
        {
            int nq = 4 * CQ, nv = 3 * CV;
            q = new float[CQ][];
            v = new float[CV][];
            f = new float[CF];
            float qs = (float)((double)QuatMax - QuatMin), vs = (float)((double)Vec3Max - Vec3Min), fs = (float)((double)FloatMax - FloatMin);
            Func<int, float, float, float> val;
            if (ConstPaletteSize == 0)
            {
                float k = (float)0.003921569;
                val = (i, s, mn) => { float sk = s * k; return (float)((float)(Data[ConstOff + i] * sk) + mn); };
            }
            else
                val = (i, s, mn) => (float)((float)(Palette[Data[ConstOff + i]] * s) + mn);
            for (int j = 0; j < CQ; j++)
            {
                float[] e = new float[4];
                for (int c = 0; c < 4; c++) e[c] = val(4 * j + c, qs, QuatMin);
                float n = (float)Math.Sqrt((float)((float)((float)(e[0] * e[0]) + (float)(e[1] * e[1])) + (float)(e[2] * e[2])) + (float)(e[3] * e[3]));
                if (n == 0) n = 1;
                for (int c = 0; c < 4; c++) e[c] = e[c] / n;
                q[j] = e;
            }
            for (int j = 0; j < CV; j++)
            {
                float[] e = new float[3];
                for (int c = 0; c < 3; c++) e[c] = val(nq + 3 * j + c, vs, Vec3Min);
                v[j] = e;
            }
            for (int j = 0; j < CF; j++) f[j] = val(nq + nv + j, fs, FloatMin);
        }

        /// <summary>-> coefficients [8 coef, lanes] and the three row weights of block bi</summary>
        public int[,] UnpackBlock(int bi, out int w0, out int w1, out int w2)
        {
            int st = BlockStarts[bi];
            int size = BlockSizes[bi];
            w0 = Data[st]; w1 = Data[st + 1]; w2 = Data[st + 2];
            int blen = size - 3;
            byte[] body = new byte[blen + 8];
            for (int i = 0; i < blen; i++) body[i] = Data[st + size - 1 - i];        // un-reverse
            long pos = 0;
            int[,] coeff = new int[8, Lanes];
            int q4 = 4 * Q, v3 = 3 * V;
            int[] nib = new int[8];
            bool[] flags = new bool[8];
            bool[] signs = new bool[8];
            for (int s = 0; s < NScal; s++)
            {
                uint w = Widths[s];
                for (int c = 0; c < 8; c++) nib[c] = (int)((w >> (4 * c)) & 0xF);
                for (int c = 0; c < 8; c++)
                {
                    flags[c] = false;
                    if (nib[c] != 0) { flags[c] = Bit(body, pos) != 0; pos++; }
                }
                for (int c = 0; c < 8; c++)
                {
                    signs[c] = false;
                    if (flags[c]) { signs[c] = Bit(body, pos) != 0; pos++; }
                }
                int lane = s < q4 ? s : (s < q4 + v3 ? s + (s - q4) / 3 : s + V);
                for (int c = 0; c < 8; c++)
                {
                    if (!flags[c]) continue;
                    int n = nib[c];
                    int m = (int)Bits(body, pos, n);
                    pos += n;
                    coeff[c, lane] = signs[c] ? m : -m;
                }
            }
            return coeff;
        }

        private static int Bit(byte[] b, long pos) { return (b[pos >> 3] >> (int)(pos & 7)) & 1; }

        private static long Bits(byte[] b, long pos, int n)
        {
            long r = 0;
            for (int i = 0; i < n; i++) r |= (long)Bit(b, pos + i) << i;
            return r;
        }

        private float[] StepRow(int w)
        {
            float d = Dct;
            float bas = d * (float)3.0517578e-05;
            float fw = (float)((float)w + 1.0f) * (float)(d * (float)0.2);
            float[] r = new float[8];
            for (int k = 0; k < 8; k++) r[k] = (float)((float)((float)(fw * (float)LN[k]) + 1.0f) * bas);
            return r;
        }

        /// <summary>-> float32 [8 keys, lanes] (before the offset ramps)</summary>
        public float[,] DecodeBlock(int bi)
        {
            int w0, w1, w2;
            int[,] coeff = UnpackBlock(bi, out w0, out w1, out w2);
            float[] r0 = StepRow(w0), r1 = StepRow(w1), r2 = StepRow(w2);
            float[,] outp = new float[8, Lanes];
            float[] mins = { QuatMin, TrajMin, Vec3Min, FloatMin };
            float[] maxs = { QuatMax, TrajMax, Vec3Max, FloatMax };
            float[,] c = new float[8, 4];
            for (int g = 0; g < Groups; g++)
            {
                int rs = g < Q ? 0 : g == Q ? 1 : g == Q + 1 ? 2 : 3;
                int rng = g < Q ? 0 : (g == Q && V != 0) ? 1 : (g < Q + V ? 2 : 3);
                for (int k = 0; k < 8; k++)
                    for (int l = 0; l < 4; l++)
                    {
                        float step;
                        switch (rs)
                        {
                            case 0: step = r0[k]; break;
                            case 1: step = r1[k]; break;
                            case 2: step = l == 0 ? r1[k] : r2[k]; break;
                            default: step = r2[k]; break;
                        }
                        c[k, l] = (float)coeff[k, 4 * g + l] * step;
                    }
                float scale = maxs[rng] - mins[rng];
                for (int l = 0; l < 4; l++)
                {
                    float dc = (float)((float)(c[0, l] * 0.125f) + 0.5f);
                    for (int r = 0; r < 8; r++)
                    {
                        // the reference port's float32 matmul accumulates with fused multiply-adds (OpenBLAS); the
                        // product of two floats is exact in double, so this is one rounding per step
                        float m = 0f;
                        for (int k = 1; k < 8; k++) m = (float)((double)BasisF[r, k] * c[k, l] + m);
                        float acc = dc + m;
                        outp[r, 4 * g + l] = (float)((float)(acc * scale) + mins[rng]);
                    }
                }
            }
            return outp;
        }

        // ------------------------------------------------------------------ offset ramps
        private void Ramps(float[,] k)
        {
            if (VectorOffsetSize != 0) ApplyRamp(k, 4 * Q, VectorOffsetScale, VosOff, VectorOffsetSize);
            if (FloatOffsetSize != 0) ApplyRamp(k, 4 * (Q + V), FloatOffsetScale, FosOff, FloatOffsetSize);
        }

        private static int I8(int b) { return b > 127 ? b - 256 : b; }

        /// <summary>an offset ramp applied to every block of the clip (per block it touches keys 8b..8b+7)</summary>
        private void ApplyRamp(float[,] keys, int lane0, float scale, int off, int size)
        {
            Func<int, int> tab = i => i < size ? Data[off + i] : 0;
            int nk = NumKeys;
            float sc = scale / 127.0f;
            int nblocks = (nk + 7) >> 3;
            float[] vals = new float[8];
            for (int bi = 0; bi < nblocks; bi++)
            {
                int key = bi * 8;
                int blk = key >> 3;
                int u13 = 0;
                if (blk != 0 && blk == (nk >> 3)) { blk -= 1; u13 = nk & 7; }
                int ch = 0, p = 1;
                for (int gi = 0; gi < tab(0); gi++)
                {
                    int runs = tab(p + 1);
                    ch += tab(p);
                    int r = 0, rem = blk;
                    if (p + 3 < size && tab(p + 3) <= blk)
                    {
                        int k = 1;
                        while (true)
                        {
                            if (runs - 1 <= r) break;
                            r++;
                            rem -= tab(p + k + 2);
                            k += 2;
                            if (p + k + 2 >= size || !(tab(p + k + 2) <= rem)) break;
                        }
                    }
                    float v0 = (float)I8(tab(p + 2 * r + 2)) * sc;
                    if (r == runs - 1)
                        for (int j = 0; j < 8; j++) vals[j] = v0;
                    else
                    {
                        float v1 = (float)I8(tab(p + 2 * r + 4)) * sc;
                        float d = (v1 - v0) / (float)(tab(p + 2 * r + 3) << 3);
                        float x = (float)((float)(u13 + rem * 8) * d) + v0;
                        for (int j = 0; j < 8; j++) { vals[j] = x; x = x + d; }
                    }
                    for (int j = 0; j < 8; j++)
                    {
                        int kk = bi * 8 + j;
                        if (kk < keys.GetLength(0)) keys[kk, lane0 + ch] = keys[kk, lane0 + ch] + vals[j];
                    }
                    p += runs + (runs - 1) + 2;
                }
            }
        }

        /// <summary>float32 [NumKeys, lanes]: every key of the clip (block decode + ramps)</summary>
        public float[,] DecodeAllKeys()
        {
            if (keys != null) return keys;
            int nk = NumKeys;
            int nb = (nk + 7) >> 3;
            float[,] all = new float[nb * 8, Lanes];
            if (NScal != 0)
                for (int bi = 0; bi < nb; bi++)
                {
                    float[,] b = DecodeBlock(bi);
                    for (int r = 0; r < 8; r++)
                        for (int l = 0; l < Lanes; l++) all[bi * 8 + r, l] = b[r, l];
                }
            Ramps(all);
            float[,] k2 = new float[nk, Lanes];
            for (int r = 0; r < nk; r++)
                for (int l = 0; l < Lanes; l++) k2[r, l] = all[r, l];
            keys = k2;
            return keys;
        }

        /// <summary>per key, all rig channels: quats [K][Q+CQ][4], vecs [K][V+CV][3], floats [K][F+CF]</summary>
        public void KeyChannels(out float[][][] quats, out float[][][] vecs, out float[][] flts)
        {
            float[,] k = DecodeAllKeys();
            int K = k.GetLength(0);
            float[][] cq, cv;
            float[] cf;
            Constants(out cq, out cv, out cf);
            bool[] cmap = ChannelMap();
            int nq = Q + CQ, nv = V + CV, nf = F + CF;
            quats = new float[K][][];
            vecs = new float[K][][];
            flts = new float[K][];
            for (int r = 0; r < K; r++) { quats[r] = new float[nq][]; vecs[r] = new float[nv][]; flts[r] = new float[nf]; }
            int ia = 0, ic = 0;
            for (int j = 0; j < nq; j++)
            {
                if (cmap[j]) { for (int r = 0; r < K; r++) quats[r][j] = (float[])cq[ic].Clone(); ic++; }
                else { for (int r = 0; r < K; r++) quats[r][j] = new[] { k[r, 4 * ia], k[r, 4 * ia + 1], k[r, 4 * ia + 2], k[r, 4 * ia + 3] }; ia++; }
            }
            ia = 0; ic = 0;
            for (int j = 0; j < nv; j++)
            {
                if (cmap[nq + j]) { for (int r = 0; r < K; r++) vecs[r][j] = (float[])cv[ic].Clone(); ic++; }
                else
                {
                    int bs = 4 * Q + 4 * ia;
                    for (int r = 0; r < K; r++) vecs[r][j] = new[] { k[r, bs], k[r, bs + 1], k[r, bs + 2] };
                    ia++;
                }
            }
            ia = 0; ic = 0;
            for (int j = 0; j < nf; j++)
            {
                if (cmap[nq + nv + j]) { for (int r = 0; r < K; r++) flts[r][j] = cf[ic]; ic++; }
                else { for (int r = 0; r < K; r++) flts[r][j] = k[r, 4 * (Q + V) + ia]; ia++; }
            }
        }

        /// <summary>time (frames) -> (keyA, keyB, alpha)</summary>
        public void Seek(double t, out int keyA, out int keyB, out double frac)
        {
            double f = PyFormat.Floor(t);
            frac = t - f;
            long u9 = (long)f;
            if (u9 < 0) u9 = 0;
            long u13 = u9 + 1;
            long last = EndFrame.HasValue ? EndFrame.Value : Math.Max(NumKeys - 1, 0);
            long u10;
            if ((Flags & 1) == 0)
            {
                if (last <= u9) u9 = last;
                u10 = u9;
                if (last <= u13) u13 = last;
            }
            else
            {
                long m = last + 1;
                u10 = PyMod(u9, m);
                u13 = PyMod(u13, m);
            }
            u9 = u10;
            if ((Flags & 2) != 0)
            {
                keyA = (int)u9;
                keyB = (int)u13;
                return;
            }
            byte[] p = Data;
            long u8 = 0, i11 = 0, i12 = 0, pb = 0;
            if (p[0] <= u9)
            {
                while (true)
                {
                    long i7 = u8;
                    u8 = i7 + 1;
                    i12 = i11 + (p[pb] - 1);
                    u9 = u10 + (1 - p[pb]);
                    u10 = u9;
                    if (u9 < p[u8] + 1) break;
                    u8 = i7 + 2;
                    i12 = i12 + 1;
                    u9 = u9 + (-1 - p[u8 - 1]);
                    u10 = u9;
                    pb = u8;
                    i11 = i12;
                    if (!(p[u8] <= u9)) break;
                }
            }
            u9 = u10;
            bool b4 = (u8 & 1) == 0 && u9 < p[u8] - 1;
            bool b3 = (u8 & 1) == 0 && u9 <= p[u8];
            int nk = NumKeys;
            long u5 = (b3 ? u9 : 0) + i12;
            if (b3) u10 = 0;
            long ka = u5;
            if (nk <= u5) { ka = nk - 1; frac = 0.0; }
            if (u13 != 0)
            {
                if (!b4) frac = ((double)u10 + frac) / (double)(p[((u8 - 1) & 1) + u8] + 1);
                u13 = u5 + 1;
                if (nk <= u13) { u13 = nk - 1; frac = 0.0; }
            }
            keyA = (int)ka;
            keyB = (int)u13;
        }

        private static long PyMod(long a, long m) { long r = a % m; return r < 0 ? r + m : r; }

        public int FrameCount() { return (int)((EndFrame.HasValue ? EndFrame.Value : NumKeys - 1) + 1); }

        /// <summary>runtime-faithful pose at each frame: quats nlerp'd, vecs/floats lerp'd</summary>
        public void SampleFrames(IList<int> frames, out float[][][] qo, out float[][][] vo, out float[][] fo)
        {
            float[][][] quats, vecs;
            float[][] flts;
            KeyChannels(out quats, out vecs, out flts);
            if (frames == null) { List<int> fr = new List<int>(); for (int i = 0; i < FrameCount(); i++) fr.Add(i); frames = fr; }
            int nf = frames.Count;
            qo = new float[nf][][];
            vo = new float[nf][][];
            fo = new float[nf][];
            int K = quats.Length;
            for (int n = 0; n < nf; n++)
            {
                int a, b;
                double al;
                Seek(frames[n], out a, out b, out al);
                a = Math.Min(a, K - 1);
                b = Math.Min(b, K - 1);
                if (a < 0) a = 0;
                if (b < 0) b = 0;
                if (K == 0)
                {
                    qo[n] = new float[0][]; vo[n] = new float[0][]; fo[n] = new float[0];
                    continue;
                }
                if (Math.Abs(al) < 1.5258789e-05)
                {
                    qo[n] = Clone2(quats[a]); vo[n] = Clone2(vecs[a]); fo[n] = (float[])flts[a].Clone();
                }
                else
                {
                    float af = (float)al;
                    qo[n] = Lerp2(quats[a], quats[b], af, true);
                    vo[n] = Lerp2(vecs[a], vecs[b], af, false);
                    float[] f = new float[flts[a].Length];
                    for (int j = 0; j < f.Length; j++) f[j] = (float)(flts[a][j] + (float)((float)(flts[b][j] - flts[a][j]) * af));
                    fo[n] = f;
                }
            }
        }

        internal static float[][] Clone2(float[][] a)
        {
            float[][] r = new float[a.Length][];
            for (int i = 0; i < a.Length; i++) r[i] = (float[])a[i].Clone();
            return r;
        }

        internal static float[][] Lerp2(float[][] a, float[][] b, float al, bool normalize)
        {
            float[][] r = new float[a.Length][];
            for (int i = 0; i < a.Length; i++)
            {
                float[] e = new float[a[i].Length];
                for (int c = 0; c < e.Length; c++) e[c] = (float)(a[i][c] + (float)((float)(b[i][c] - a[i][c]) * al));
                if (normalize) Normalize4(e);
                r[i] = e;
            }
            return r;
        }

        internal static void Normalize4(float[] e)
        {
            float s = 0f;
            for (int c = 0; c < e.Length; c++) s = (float)(s + (float)(e[c] * e[c]));
            float n = (float)Math.Sqrt(s);
            if (n == 0) n = 1;
            for (int c = 0; c < e.Length; c++) e[c] = e[c] / n;
        }
    }
}
