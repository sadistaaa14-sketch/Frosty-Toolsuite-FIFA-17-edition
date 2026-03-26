// FIFA 17 VBR Animation Decoder — C# port of vbr_decoder_v2.py
// Pure decoder: bitstream → float curves. No Frosty dependencies.
// Compatible with .NET Framework 4.8 / C# 7.3.

using System;
using System.Collections.Generic;

namespace AnimationBrowserPlugin
{
    public class VbrAnimation
    {
        // Scalar fields
        public float QuatMin, QuatMax, Vec3Min, Vec3Max, FloatMin, FloatMax;
        public float VecOffScale, FloatOffScale, Dct;
        public ushort Q, V3, F, CQ, CV3, CF;
        public ushort KtsSize, NumKeys, CcmsSize, ConstPaletteSize, VosSize, FosSize, Flags;

        // Array data
        public byte[] DataBlob;
        public ushort[] FrameBlockSizes;
        public float[] ConstPalette;

        // Derived
        public int AnimatedDofs { get { return Q * 4 + V3 * 3 + F; } }
        public int NumBlocks { get { return FrameBlockSizes != null ? FrameBlockSizes.Length : 0; } }
        public int HeaderSize { get; private set; }

        // KTS timing
        public int[] FramePositions;
        public int TotalFrames;

        // Nibble table
        private int[][] _dofNibbles;

        // Constant channels: [index][component] — quats are 4 floats, vec3s are 3, floats are 1
        public float[][] ConstQuats;   // [CQ][4] = w,x,y,z
        public float[][] ConstVec3s;   // [CV3][3] = x,y,z
        public float[] ConstFloatsArr; // [CF]

        public void Initialize()
        {
            int fbTotal = 0;
            for (int i = 0; i < FrameBlockSizes.Length; i++) fbTotal += FrameBlockSizes[i];
            HeaderSize = DataBlob.Length - fbTotal;
            ParseKts();
            ParseNibbleTable();
            ParseConstants();
        }

        // ─── KTS Timing ─────────────────────────────────────────
        private void ParseKts()
        {
            TotalFrames = NumKeys;
            FramePositions = null;
            if (KtsSize == 0) return;

            int ktsOff = CcmsSize + CQ * 4 + CV3 * 3 + CF;
            int copyLen = Math.Min((int)KtsSize, DataBlob.Length - ktsOff);
            if (copyLen <= 0) return;
            byte[] kts = new byte[copyLen];
            Array.Copy(DataBlob, ktsOff, kts, 0, copyLen);

            int countSum = 0;
            for (int i = 0; i < kts.Length; i += 2) countSum += kts[i];

            byte[] tb = null;
            if (countSum == NumKeys)
            {
                tb = kts;
            }
            else
            {
                int acc = 0;
                for (int i = 0; i < kts.Length; i++)
                {
                    if (i % 2 == 0)
                    {
                        acc += kts[i];
                        if (acc == NumKeys) { tb = new byte[i + 1]; Array.Copy(kts, tb, i + 1); break; }
                        else if (acc > NumKeys) break;
                    }
                }
            }
            if (tb == null) return;

            List<int> frames = new List<int>();
            int pos = 0;
            for (int i = 0; i < tb.Length; i += 2)
            {
                int c = tb[i];
                int g = (i + 1 < tb.Length) ? tb[i + 1] : 0;
                for (int j = 0; j < c; j++) frames.Add(pos + j);
                if (frames.Count > 0) pos = frames[frames.Count - 1] + g;
            }
            if (frames.Count > 0)
            {
                FramePositions = frames.ToArray();
                TotalFrames = FramePositions[FramePositions.Length - 1] + 1;
            }
        }

        // ─── Nibble Table ────────────────────────────────────────
        private void ParseNibbleTable()
        {
            int ad = AnimatedDofs;
            int nibOff = HeaderSize - VosSize - FosSize - ad * 4;
            _dofNibbles = new int[ad][];
            for (int d = 0; d < ad; d++)
            {
                int o = nibOff + d * 4;
                uint dw = (uint)(DataBlob[o] | (DataBlob[o + 1] << 8) |
                                 (DataBlob[o + 2] << 16) | (DataBlob[o + 3] << 24));
                _dofNibbles[d] = new int[8];
                for (int i = 0; i < 8; i++) _dofNibbles[d][i] = (int)((dw >> (i * 4)) & 0xF);
            }
        }

        // ─── Constant Channels ───────────────────────────────────
        private void ParseConstants()
        {
            float[] pal = ConstPalette != null ? ConstPalette : new float[0];
            byte[] pit = DataBlob;
            int idx = CcmsSize;

            // Constant quaternions
            ConstQuats = new float[CQ][];
            float qR = QuatMax - QuatMin;
            for (int qi = 0; qi < CQ; qi++)
            {
                ConstQuats[qi] = new float[4];
                if (idx + 3 < pit.Length && pal.Length > 0)
                {
                    float w = PalGet(pit, pal, idx) * qR + QuatMin;
                    float x = PalGet(pit, pal, idx + 1) * qR + QuatMin;
                    float y = PalGet(pit, pal, idx + 2) * qR + QuatMin;
                    float z = PalGet(pit, pal, idx + 3) * qR + QuatMin;
                    float n = (float)Math.Sqrt(w * w + x * x + y * y + z * z);
                    if (n > 0) { w /= n; x /= n; y /= n; z /= n; }
                    ConstQuats[qi][0] = w; ConstQuats[qi][1] = x;
                    ConstQuats[qi][2] = y; ConstQuats[qi][3] = z;
                }
                else
                {
                    ConstQuats[qi][0] = 1; ConstQuats[qi][1] = 0;
                    ConstQuats[qi][2] = 0; ConstQuats[qi][3] = 0;
                }
                idx += 4;
            }

            // Constant vec3s
            ConstVec3s = new float[CV3][];
            float vR = Vec3Max - Vec3Min;
            for (int vi = 0; vi < CV3; vi++)
            {
                ConstVec3s[vi] = new float[3];
                if (idx + 2 < pit.Length && pal.Length > 0)
                {
                    ConstVec3s[vi][0] = PalGet(pit, pal, idx) * vR + Vec3Min;
                    ConstVec3s[vi][1] = PalGet(pit, pal, idx + 1) * vR + Vec3Min;
                    ConstVec3s[vi][2] = PalGet(pit, pal, idx + 2) * vR + Vec3Min;
                }
                idx += 3;
            }

            // Constant floats
            ConstFloatsArr = new float[CF];
            float fR = FloatMax - FloatMin;
            for (int fi = 0; fi < CF; fi++)
            {
                if (idx < pit.Length && pal.Length > 0)
                    ConstFloatsArr[fi] = PalGet(pit, pal, idx) * fR + FloatMin;
                idx++;
            }
        }

        private static float PalGet(byte[] pit, float[] pal, int i)
        {
            return (i < pit.Length && pit[i] < pal.Length) ? pal[pit[i]] : 0f;
        }

        // ─── Decode One Frame Block ──────────────────────────────
        public float[][] DecodeBlock(int blockIdx)
        {
            int offset = HeaderSize;
            for (int i = 0; i < blockIdx; i++) offset += FrameBlockSizes[i];

            byte b0 = DataBlob[offset], b1 = DataBlob[offset + 1], b2 = DataBlob[offset + 2];
            int bsLen = FrameBlockSizes[blockIdx] - 3;
            byte[] bs = new byte[bsLen];
            for (int i = 0; i < bsLen; i++) bs[i] = DataBlob[offset + 3 + (bsLen - 1 - i)];
            int bp = 0;

            float D = Dct;
            float sQ = (b0 + 1f) * D / 5f;
            float sF = (b2 + 1f) * D / 5f;
            float sV = (b1 + 1f) * D / 5f;
            float tiny = (float)Math.Pow(2.0, -15.0);
            int qDofs = Q * 4, vDofs = V3 * 3;

            float[][] results = new float[AnimatedDofs][];
            for (int d = 0; d < AnimatedDofs; d++)
            {
                int[] n = _dofNibbles[d];
                bool isQ = d < qDofs;
                bool isV = d >= qDofs && d < qDofs + vDofs;

                int[] fl = new int[8]; int[] sg = new int[8]; int[] vl = new int[8];
                for (int s = 0; s < 8; s++) if (n[s] > 0) fl[s] = ReadBits(bs, ref bp, 1);
                for (int s = 0; s < 8; s++) if (fl[s] != 0) sg[s] = ReadBits(bs, ref bp, 1);
                for (int s = 0; s < 8; s++) if (fl[s] != 0) vl[s] = ReadBits(bs, ref bp, n[s]);

                float sp = isQ ? sQ : isV ? sV : sF;
                float[] co = new float[8];
                for (int s = 0; s < 8; s++)
                {
                    float dq = ((float)Math.Log(s + 2) * sp + 1f) * tiny * D;
                    co[s] = (sg[s] != 0 ? -vl[s] : vl[s]) * dq;
                }
                results[d] = Idct8(co);
            }
            return results;
        }

        // ─── Decode All Blocks ───────────────────────────────────
        public float[][] DecodeAll()
        {
            int ad = AnimatedDofs;
            float[][] curves = new float[ad][];
            for (int d = 0; d < ad; d++) curves[d] = new float[NumKeys];
            int wp = 0;
            for (int bi = 0; bi < NumBlocks; bi++)
            {
                float[][] block = DecodeBlock(bi);
                int n = Math.Min(8, NumKeys - wp);
                for (int d = 0; d < ad; d++) Array.Copy(block[d], 0, curves[d], wp, n);
                wp += n;
            }
            return curves;
        }

        // ─── Bit Reader (LE LSB-first) ──────────────────────────
        private static int ReadBits(byte[] buf, ref int bp, int n)
        {
            if (n == 0) return 0;
            int bo = bp >> 3, bi = bp & 7;
            uint dw = 0;
            for (int i = 0; i < 4 && bo + i < buf.Length; i++)
                dw |= (uint)(buf[bo + i] << (i * 8));
            bp += n;
            return (int)((dw >> bi) & ((1u << n) - 1));
        }

        // ─── IDCT-II/8 ──────────────────────────────────────────
        private static readonly float[] _idctTab;
        static VbrAnimation()
        {
            _idctTab = new float[64];
            for (int k = 0; k < 8; k++)
                for (int j = 0; j < 8; j++)
                    _idctTab[k * 8 + j] = (float)Math.Cos(Math.PI * (2 * k + 1) * j / 16.0);
        }

        private static float[] Idct8(float[] c)
        {
            float[] r = new float[8];
            float iv = 1f / (float)Math.Sqrt(2.0);
            for (int k = 0; k < 8; k++)
            {
                float s = 0;
                for (int j = 0; j < 8; j++)
                    s += (j == 0 ? iv : 1f) * c[j] * _idctTab[k * 8 + j];
                r[k] = s / 4f;
            }
            return r;
        }
    }
}
