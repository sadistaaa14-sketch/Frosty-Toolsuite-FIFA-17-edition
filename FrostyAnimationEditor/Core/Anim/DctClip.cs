using System;
using System.Collections;
using System.Collections.Generic;

namespace FrostyAnimationEditor.Core.Anim
{
    /// <summary>
    /// DctAnimationAsset ("DCT " codec), decoded the way the game decodes it:
    ///   blocks of 8 keys (block = key>>3; block 0 stores no coefficient 0); fields read MSB-first with nibble
    ///   widths (0xF = CatchAllBitCount); coefficient 0 += DeltaBase; value = sum_k coef_k*(k*QuantizeMultSubblock*0.1 + 1)/
    ///   QuantizeMultBlock*B[r][k] (B[r][0] = 0.25, B[r][k] = cos((2r+1)k pi/16)/2); quats normalised;
    ///   seek over KeyTimes (Cycle = loop), then blend.
    /// </summary>
    public sealed class DctClip
    {
        public const uint Tag = 0x44435420;   // "DCT "
        public static readonly float[,] B = BuildB();

        private static float[,] BuildB()
        {
            float[,] b = new float[8, 8];
            for (int r = 0; r < 8; r++)
                for (int k = 0; k < 8; k++)
                    b[r, k] = (float)(k == 0 ? 0.25 : Math.Cos(Math.PI * (2 * r + 1) * k / 16.0) / 2.0);
            return b;
        }

        public int NumKeys, NumQuats, NumVec3, NumFloat, NumFloatVec;
        public bool Cycle;
        public long QuantizeMultBlock, QuantizeMultSubblock, CatchAllBitCount;
        public List<long> KeyTimes;
        public byte[] Desc;
        public List<long>[] Delta;
        public List<long> BitsWords;
        public long DataSize;
        public byte[] Data;
        public long? EndFrame;
        public int Nch;
        public List<List<long>> ChanWords = new List<List<long>>();
        public long BitsPerBlock, BitsCoef0;
        public List<int[][]> ReadWidths = new List<int[][]>();
        private float[][][] keys;

        private static List<long> Longs(object v)
        {
            List<long> r = new List<long>();
            if (v is byte[]) { foreach (byte b in (byte[])v) r.Add(b); return r; }
            IList l = v as IList;
            if (l != null) foreach (object x in l) r.Add(Num.ToLong(x));
            return r;
        }

        public DctClip(GdStruct o)
        {
            NumKeys = (int)o.GetInt("NumKeys");
            NumQuats = (int)o.GetInt("NumQuats");
            NumVec3 = (int)o.GetInt("NumVec3");
            NumFloat = (int)o.GetInt("NumFloat");
            NumFloatVec = (int)o.GetInt("NumFloatVec");
            Cycle = o.GetBool("Cycle");
            QuantizeMultBlock = o.GetInt("QuantizeMultBlock");
            QuantizeMultSubblock = o.GetInt("QuantizeMultSubblock");
            CatchAllBitCount = o.GetInt("CatchAllBitCount");
            KeyTimes = Longs(o["KeyTimes"]);
            object d = o["DofTableDescBytes"];
            Desc = d as byte[] ?? Longs(d).ConvertAll(x => (byte)x).ToArray();
            Delta = new[] { Longs(o["DeltaBaseX"]), Longs(o["DeltaBaseY"]), Longs(o["DeltaBaseZ"]), Longs(o["DeltaBaseW"]) };
            BitsWords = Longs(o["BitsPerSubblock"]);
            DataSize = o.GetInt("DataSize");
            Data = o["Data"] as byte[] ?? new byte[0];
            GdStruct b = o["__base"] as GdStruct;
            EndFrame = b != null && b["EndFrame"] != null ? (long?)b.GetInt("EndFrame") : null;
            Nch = NumQuats + NumVec3 + NumFloatVec;
            Chan();
        }

        private int[] WidthsOf(long word, bool real)
        {
            int[] nib = { (int)((word >> 12) & 15), (int)((word >> 8) & 15), (int)((word >> 4) & 15), (int)(word & 15) };
            if (real) for (int i = 0; i < 4; i++) if (nib[i] == 15) nib[i] = (int)CatchAllBitCount;
            return nib;
        }

        private void Chan()
        {
            int o = 0;
            for (int c = 0; c < Nch; c++)
            {
                int n = Desc[c] >> 4;
                ChanWords.Add(BitsWords.GetRange(o, Math.Min(n, Math.Max(0, BitsWords.Count - o))));
                o += n;
            }
            if (o != BitsWords.Count) throw new GdException("BitsPerSubblock length " + BitsWords.Count + " != sum of desc>>4 " + o);
            // sizes use raw nibbles when CatchAll < 16
            bool real = CatchAllBitCount >= 16;
            BitsPerBlock = 0;
            BitsCoef0 = 0;
            foreach (List<long> ws in ChanWords)
            {
                foreach (long w in ws) foreach (int x in WidthsOf(w, real)) BitsPerBlock += x;
                if (ws.Count > 0) foreach (int x in WidthsOf(ws[0], real)) BitsCoef0 += x;
                int[][] rw = new int[ws.Count][];
                for (int j = 0; j < ws.Count; j++) rw[j] = WidthsOf(ws[j], true);
                ReadWidths.Add(rw);
            }
        }

        public long BlockBitStart(int b) { return b == 0 ? 0 : BitsPerBlock * b - BitsCoef0; }

        /// <summary>n (&lt;= 40) bits MSB-first starting at bit pos (big-endian)</summary>
        private static ulong Read(byte[] data, long pos, int n)
        {
            if (n == 0) return 0;
            long o = pos >> 3;
            ulong w = 0;
            for (int i = 0; i < 8; i++) w = (w << 8) | (o + i < data.Length ? data[o + i] : (byte)0);
            return (w >> (64 - (int)(pos & 7) - n)) & ((1UL << n) - 1);
        }

        private static long Signed(ulong v, int n)
        {
            if (n == 0) return 0;
            v &= (1UL << n) - 1;
            return (v >> (n - 1)) != 0 ? (long)v - (1L << n) : (long)v;
        }

        /// <summary>-> integer coefficients [nch][8][4] for block b</summary>
        public long[][][] DecodeBlock(int b)
        {
            long pos = BlockBitStart(b);
            long[][][] coef = new long[Nch][][];
            for (int c = 0; c < Nch; c++)
            {
                coef[c] = new long[8][];
                for (int j = 0; j < 8; j++) coef[c][j] = new long[4];
                int[][] ws = ReadWidths[c];
                int j0 = b == 0 ? 1 : 0;
                for (int j = j0; j < ws.Length; j++)
                {
                    int[] w = ws[j];
                    int tot = w[0] + w[1] + w[2] + w[3];
                    if (tot < 0x21)
                    {
                        ulong v = Read(Data, pos, tot);
                        pos += tot;
                        int sh = tot;
                        for (int k = 0; k < 4; k++)
                        {
                            sh -= w[k];
                            coef[c][j][k] = Signed(v >> sh, w[k]);
                        }
                    }
                    else
                    {
                        for (int k = 0; k < 4; k++)
                        {
                            ulong v = Read(Data, pos, w[k]);
                            pos += w[k];
                            coef[c][j][k] = Signed(v, w[k]);
                        }
                    }
                }
                for (int comp = 0; comp < 4; comp++)
                {
                    long dv = c < Delta[comp].Count ? Delta[comp][c] : 0;
                    long v = (coef[c][0][comp] + dv) & 0xFFFF;
                    coef[c][0][comp] = (v & 0x8000) != 0 ? v - 0x10000 : v;
                }
            }
            return coef;
        }

        /// <summary>float32 [NumKeys][nch][4] (per key)</summary>
        public float[][][] DecodeAllKeys()
        {
            if (keys != null) return keys;
            int nk = NumKeys;
            int nb = (nk + 7) >> 3;
            float qsub = (float)QuantizeMultSubblock * 0.1f;
            float qmb = (float)QuantizeMultBlock;
            float[] steps = new float[8];
            for (int k = 0; k < 8; k++) steps[k] = (float)((float)((float)k * qsub) + 1.0f) / qmb;
            float[][][] all = new float[nb * 8][][];
            for (int bi = 0; bi < nb; bi++)
            {
                long[][][] coef = DecodeBlock(bi);
                for (int r = 0; r < 8; r++)
                {
                    float[][] row = new float[Nch][];
                    for (int c = 0; c < Nch; c++)
                    {
                        float[] e = new float[4];
                        for (int l = 0; l < 4; l++)
                        {
                            float acc = 0f;
                            for (int k = 0; k < 8; k++) acc = (float)(acc + (float)(B[r, k] * (float)((float)coef[c][k][l] * steps[k])));
                            e[l] = acc;
                        }
                        row[c] = e;
                    }
                    all[bi * 8 + r] = row;
                }
            }
            float[][][] outp = new float[nk][][];
            for (int r = 0; r < nk; r++)
            {
                outp[r] = all[r];
                for (int c = 0; c < NumQuats; c++) VbrClip.Normalize4(outp[r][c]);
            }
            keys = outp;
            return keys;
        }

        public void KeyChannels(out float[][][] quats, out float[][][] vecs, out float[][] fl)
        {
            float[][][] k = DecodeAllKeys();
            int K = k.Length;
            quats = new float[K][][];
            vecs = new float[K][][];
            fl = new float[K][];
            for (int r = 0; r < K; r++)
            {
                quats[r] = new float[NumQuats][];
                for (int c = 0; c < NumQuats; c++) quats[r][c] = (float[])k[r][c].Clone();
                vecs[r] = new float[NumVec3][];
                for (int c = 0; c < NumVec3; c++) vecs[r][c] = new[] { k[r][NumQuats + c][0], k[r][NumQuats + c][1], k[r][NumQuats + c][2] };
                List<float> flat = new List<float>();
                for (int c = NumQuats + NumVec3; c < Nch; c++) flat.AddRange(k[r][c]);
                fl[r] = new float[Math.Min(NumFloat, flat.Count)];
                for (int j = 0; j < fl[r].Length; j++) fl[r][j] = flat[j];
            }
        }

        public List<long> KeyFrames()
        {
            if (KeyTimes.Count > 0) return new List<long>(KeyTimes);
            List<long> r = new List<long>();
            for (int i = 0; i < NumKeys; i++) r.Add(i);
            return r;
        }

        public int FrameCount()
        {
            if (EndFrame.HasValue) return (int)EndFrame.Value + 1;
            List<long> kt = KeyFrames();
            return kt.Count > 0 ? (int)kt[kt.Count - 1] + 1 : 0;
        }

        /// <summary>frame -> (keyA, keyB, alpha) like the game</summary>
        public void Seek(double t, out int a, out int b, out double al)
        {
            List<long> kt = KeyFrames();
            int n = kt.Count;
            a = 0; b = 0; al = 0;
            if (n == 0) return;
            if (Cycle)
            {
                double period = (EndFrame.HasValue ? EndFrame.Value : kt[n - 1]) + 1;
                t = PyFormat.FMod(t, period);
            }
            if (t <= kt[0]) return;
            if (t >= kt[n - 1]) { a = n - 1; b = n - 1; return; }
            int lo = 0;
            while (lo + 1 < n && kt[lo + 1] <= PyFormat.Floor(t)) lo++;
            int hi = Math.Min(lo + 1, n - 1);
            long span = kt[hi] - kt[lo];
            al = span != 0 ? (t - kt[lo]) / span : 0.0;
            a = lo;
            b = hi;
        }

        public void SampleFrames(IList<int> frames, out float[][][] qo, out float[][][] vo, out float[][] fo)
        {
            float[][][] quats, vecs;
            float[][] fl;
            KeyChannels(out quats, out vecs, out fl);
            if (frames == null) { List<int> fr = new List<int>(); for (int i = 0; i < FrameCount(); i++) fr.Add(i); frames = fr; }
            int nf = frames.Count;
            qo = new float[nf][][];
            vo = new float[nf][][];
            fo = new float[nf][];
            for (int n = 0; n < nf; n++)
            {
                int a, b;
                double al;
                Seek(frames[n], out a, out b, out al);
                if (quats.Length == 0) { qo[n] = new float[0][]; vo[n] = new float[0][]; fo[n] = new float[0]; continue; }
                float af = (float)al;
                qo[n] = VbrClip.Lerp2(quats[a], quats[b], af, true);
                vo[n] = VbrClip.Lerp2(vecs[a], vecs[b], af, false);
                float[] f = new float[fl[a].Length];
                for (int j = 0; j < f.Length; j++) f[j] = (float)(fl[a][j] + (float)((float)(fl[b][j] - fl[a][j]) * af));
                fo[n] = f;
            }
        }
    }
}
