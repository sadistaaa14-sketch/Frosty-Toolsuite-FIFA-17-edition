// FIFA 17 VBR Animation Encoder — C# port of vbr_encoder.py
// Forward DCT-8, quantize, bitstream encode.
// Compatible with .NET Framework 4.8 / C# 7.3.

using System;
using System.Collections.Generic;

namespace AnimationBrowserPlugin
{
    public class VbrEncoderResult
    {
        public float Dct;
        public ushort QuaternionCount, Vector3Count, FloatCount;
        public ushort ConstQuaternionCount, ConstVector3Count, ConstFloatCount;
        public ushort NumKeys, KeyTimeSize, ConstChanMapSize, ConstPaletteSize;
        public ushort VectorOffsetSize, FloatOffsetSize, Flags;
        public float[] ConstantPalette;
        public ushort[] FrameBlockSizes;
        public byte[] Data;
    }

    public class VbrEncoderEngine
    {
        // ─── Forward DCT-8 ──────────────────────────────────
        public static float[] Dct8(float[] samples)
        {
            float[] coeffs = new float[8];
            for (int j = 0; j < 8; j++)
            {
                float s = 0;
                for (int k = 0; k < 8; k++)
                    s += samples[k] * (float)Math.Cos(Math.PI * (2 * k + 1) * j / 16.0);
                if (j == 0) s *= (float)(1.0 / Math.Sqrt(2));
                coeffs[j] = s;
            }
            return coeffs;
        }

        // ─── Inverse DCT-8 (decoder match) ──────────────────
        public static float[] Idct8(float[] coeffs)
        {
            float[] result = new float[8];
            for (int k = 0; k < 8; k++)
            {
                float s = 0;
                for (int j = 0; j < 8; j++)
                {
                    float c = (j == 0) ? (float)(1.0 / Math.Sqrt(2)) : 1.0f;
                    s += c * coeffs[j] * (float)Math.Cos(Math.PI * (2 * k + 1) * j / 16.0);
                }
                result[k] = s / 4.0f;
            }
            return result;
        }

        private static float ComputeDeq(int slot, float scale, float dct)
        {
            int j = slot + 2;
            return ((float)Math.Log(j) * scale + 1.0f) * (1.0f / 32768.0f) * dct;
        }

        // ─── Bitstream Writer ────────────────────────────────
        private class BitWriter
        {
            private List<int> _bits = new List<int>();
            public void Write(int value, int nBits)
            {
                for (int i = 0; i < nBits; i++)
                    _bits.Add((value >> i) & 1);
            }
            public byte[] ToBytes()
            {
                var result = new List<byte>();
                for (int i = 0; i < _bits.Count; i += 8)
                {
                    byte b = 0;
                    for (int j = 0; j < 8 && i + j < _bits.Count; j++)
                        b |= (byte)(_bits[i + j] << j);
                    result.Add(b);
                }
                return result.ToArray();
            }
        }

        // ─── Encode ──────────────────────────────────────────
        /// <summary>
        /// Encode animation curves to VBR format.
        /// quatCurves[qi][frame] = (w,x,y,z)
        /// vec3Curves[vi][frame] = (x,y,z)
        /// floatCurves[fi][frame] = value
        /// </summary>
        public static VbrEncoderResult Encode(
            float[][][] quatCurves,   // [Q][frame][4]
            float[][][] vec3Curves,   // [V3][frame][3]
            float[][] floatCurves,    // [F][frame]
            float dctParam = 4.0f,
            byte b0 = 127, byte b1 = 127, byte b2 = 127)
        {
            int Q = quatCurves != null ? quatCurves.Length : 0;
            int V3 = vec3Curves != null ? vec3Curves.Length : 0;
            int F = floatCurves != null ? floatCurves.Length : 0;
            int animDofs = Q * 4 + V3 * 3 + F;

            // Determine frame count
            int numKeys = 0;
            if (Q > 0) numKeys = Math.Max(numKeys, quatCurves[0].Length);
            else if (V3 > 0) numKeys = Math.Max(numKeys, vec3Curves[0].Length);
            else if (F > 0) numKeys = Math.Max(numKeys, floatCurves[0].Length);
            int numBlocks = (numKeys + 7) / 8;

            // Flatten to per-DOF curves
            var dofCurves = new float[animDofs][];
            var dofTypes = new char[animDofs]; // 'q', 'v', 'f'
            int d = 0;
            for (int qi = 0; qi < Q; qi++)
                for (int c = 0; c < 4; c++)
                {
                    dofCurves[d] = new float[numKeys];
                    for (int f = 0; f < numKeys; f++)
                        dofCurves[d][f] = quatCurves[qi][f][c];
                    dofTypes[d] = 'q'; d++;
                }
            for (int vi = 0; vi < V3; vi++)
                for (int c = 0; c < 3; c++)
                {
                    dofCurves[d] = new float[numKeys];
                    for (int f = 0; f < numKeys; f++)
                        dofCurves[d][f] = vec3Curves[vi][f][c];
                    dofTypes[d] = 'v'; d++;
                }
            for (int fi = 0; fi < F; fi++)
            {
                dofCurves[d] = new float[numKeys];
                Array.Copy(floatCurves[fi], dofCurves[d], numKeys);
                dofTypes[d] = 'f'; d++;
            }

            float D = dctParam;
            float scaleQ = (b0 + 1.0f) * D / 5.0f;
            float scaleV = (b1 + 1.0f) * D / 5.0f;
            float scaleF = (b2 + 1.0f) * D / 5.0f;

            // Pass 1: find max nibble sizes
            var maxNibs = new int[animDofs][];
            for (int i = 0; i < animDofs; i++) maxNibs[i] = new int[8];

            var allInts = new int[numBlocks][][];
            var allSigns = new int[numBlocks][][];

            for (int bi = 0; bi < numBlocks; bi++)
            {
                int fs = bi * 8;
                allInts[bi] = new int[animDofs][];
                allSigns[bi] = new int[animDofs][];

                for (int dd = 0; dd < animDofs; dd++)
                {
                    float[] samples = new float[8];
                    for (int i = 0; i < 8; i++)
                    {
                        int fi = fs + i;
                        samples[i] = fi < numKeys ? dofCurves[dd][fi] :
                            (numKeys > 0 ? dofCurves[dd][numKeys - 1] : 0);
                    }

                    float[] coeffs = Dct8(samples);
                    float sc = dofTypes[dd] == 'q' ? scaleQ : (dofTypes[dd] == 'v' ? scaleV : scaleF);

                    allInts[bi][dd] = new int[8];
                    allSigns[bi][dd] = new int[8];

                    for (int s = 0; s < 8; s++)
                    {
                        float deq = ComputeDeq(s, sc, D);
                        if (Math.Abs(deq) < 1e-30f) continue;
                        float raw = coeffs[s] / deq;
                        int iv = (int)Math.Round(Math.Abs(raw));
                        int sg = raw < 0 ? 1 : 0;
                        if (iv == 0) sg = 0;
                        allInts[bi][dd][s] = iv;
                        allSigns[bi][dd][s] = sg;
                        if (iv > 0)
                        {
                            int nb = 0; int tmp = iv;
                            while (tmp > 0) { nb++; tmp >>= 1; }
                            if (nb > maxNibs[dd][s]) maxNibs[dd][s] = nb;
                        }
                    }
                }
            }

            // Cap at 15
            for (int dd = 0; dd < animDofs; dd++)
                for (int s = 0; s < 8; s++)
                    if (maxNibs[dd][s] > 15) maxNibs[dd][s] = 15;

            // Pass 2: encode with fixed nibble table
            var frameBlocks = new List<byte[]>();
            for (int bi = 0; bi < numBlocks; bi++)
            {
                var bw = new BitWriter();
                for (int dd = 0; dd < animDofs; dd++)
                {
                    var nibs = maxNibs[dd];
                    var ivs = allInts[bi][dd];
                    var sgs = allSigns[bi][dd];

                    // Clamp
                    for (int s = 0; s < 8; s++)
                        if (nibs[s] > 0) ivs[s] = Math.Min(ivs[s], (1 << nibs[s]) - 1);

                    // Flags
                    for (int s = 0; s < 8; s++)
                        if (nibs[s] > 0) bw.Write(ivs[s] != 0 ? 1 : 0, 1);
                    // Signs
                    for (int s = 0; s < 8; s++)
                        if (nibs[s] > 0 && ivs[s] != 0) bw.Write(sgs[s], 1);
                    // Values
                    for (int s = 0; s < 8; s++)
                        if (nibs[s] > 0 && ivs[s] != 0) bw.Write(ivs[s], nibs[s]);
                }
                byte[] bs = bw.ToBytes();
                Array.Reverse(bs);
                byte[] block = new byte[3 + bs.Length];
                block[0] = b0; block[1] = b1; block[2] = b2;
                Array.Copy(bs, 0, block, 3, bs.Length);
                frameBlocks.Add(block);
            }

            // Build nibble table
            byte[] nibTable = new byte[animDofs * 4];
            for (int dd = 0; dd < animDofs; dd++)
            {
                uint dw = 0;
                for (int i = 0; i < 8; i++)
                    dw |= (uint)(maxNibs[dd][i] & 0xF) << (i * 4);
                nibTable[dd * 4] = (byte)(dw & 0xFF);
                nibTable[dd * 4 + 1] = (byte)((dw >> 8) & 0xFF);
                nibTable[dd * 4 + 2] = (byte)((dw >> 16) & 0xFF);
                nibTable[dd * 4 + 3] = (byte)((dw >> 24) & 0xFF);
            }

            // Assemble Data blob
            int totalSize = nibTable.Length;
            foreach (var fb in frameBlocks) totalSize += fb.Length;
            byte[] data = new byte[totalSize];
            Array.Copy(nibTable, 0, data, 0, nibTable.Length);
            int offset = nibTable.Length;
            var fbs = new ushort[frameBlocks.Count];
            for (int i = 0; i < frameBlocks.Count; i++)
            {
                Array.Copy(frameBlocks[i], 0, data, offset, frameBlocks[i].Length);
                fbs[i] = (ushort)frameBlocks[i].Length;
                offset += frameBlocks[i].Length;
            }

            return new VbrEncoderResult
            {
                Dct = dctParam,
                QuaternionCount = (ushort)Q,
                Vector3Count = (ushort)V3,
                FloatCount = (ushort)F,
                ConstQuaternionCount = 0, ConstVector3Count = 0, ConstFloatCount = 0,
                NumKeys = (ushort)numKeys,
                KeyTimeSize = 0, ConstChanMapSize = 0, ConstPaletteSize = 0,
                VectorOffsetSize = 0, FloatOffsetSize = 0, Flags = 0,
                ConstantPalette = new float[0],
                FrameBlockSizes = fbs,
                Data = data,
            };
        }
    }
}
