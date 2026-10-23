using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;

namespace FrostyAnimationEditor.Core
{
    /// <summary>
    /// ContinuousContactAsset: the shape the game builds from it and the lookup it runs, as FIFA17.exe does
    /// (verified in the emulator on all 203 assets).
    /// Loader FUN_146c7ce90: nothing is built when Quads has fewer than 32 bytes; each axis keeps its first 16 values.
    /// FUN_146ca69e0 / FUN_146c628e0 build a "ContinuousContactShape": InAngles / OutAngles / MoveAngles as 16 int8
    /// (round(127/pi a), padded with the last value; MoveAngles with 127), Speeds as 16 int8 (round(100 v)),
    /// round(x) = floorf(x + 0.5) clamped to [-128, 127] (NaN -> 127); QuadMap one u32 per cell (0xFFFFFFFF = no quad,
    /// else low 24 bits = quad index, top byte = signed time offset).
    /// Lookup FUN_146cc5df0 + FUN_146cadf70 + FUN_146caeca0 (cell search FUN_146cd8ff0, corners FUN_146cb5260,
    /// renormalisation FUN_146ca8d40: invalid when less than 0.4 of the weight falls on existing entries).
    /// The meaning of a quad's 32 bytes (read by FUN_146c6c9b0 and its callers) is UNIDENTIFIED.
    /// </summary>
    public static class Contacts
    {
        public static readonly float KAngle = BE.BitsFloat(0x4221B390);   // 40.425354 = 127/pi
        public const float KSpeed = 100f;
        public static readonly float Eps = BE.BitsFloat(0x37800000);      // 2^-16
        public static readonly float Valid = BE.BitsFloat(0x3ECCCCCD);    // 0.4
        public static readonly string[] Axes = { "InAngles", "OutAngles", "MoveAngles", "Speeds" };

        private static int S8(long x) { x &= 0xFF; return x >= 128 ? (int)x - 256 : (int)x; }

        private static float FloorMagic(float v)
        {
            float m = v < 0f ? 8388608f : -8388608f;
            float r = (float)(v - m) + m;
            if (v < r) r = r - 1f;
            return r;
        }

        /// <summary>FUN_146c84520: an angle (radians) as the int8 the lookup compares</summary>
        public static int QuantizeAngle(double x)
        {
            float v = (float)(KAngle * (float)x) + 0.5f;
            float r = FloorMagic(v);
            r = r > -128f ? r : -128f;
            r = r < 127f ? r : 127f;
            return (int)r;
        }

        /// <summary>FUN_146c84640: a speed as the unsigned byte the lookup uses (compared as signed later)</summary>
        public static int QuantizeSpeed(double x)
        {
            float v = (float)(KSpeed * (float)x) + 0.5f;
            float r = FloorMagic(v);
            r = r > 0f ? r : 0f;
            r = r < 255f ? r : 255f;
            return (int)r;
        }

        /// <summary>FUN_146c628e0: floorf(x * k + 0.5) clamped to [-128, 127] (NaN -> 127)</summary>
        public static int AxisByte(double x, float k)
        {
            float v = (float)((float)x * k) + 0.5f;
            if (!float.IsNaN(v) && !float.IsInfinity(v)) v = (float)Math.Floor(v);
            if (-128f > v) v = -128f;
            else if (!(127f > v)) v = 127f;
            return (int)v;
        }

        public sealed class Shape
        {
            public int[] N = new int[4];
            public int[][] AxesB = new int[4][];
            public long[] Strides;
            public List<long> QuadMap;
            public byte[] Quads;
            public int NQuads;
            public float[] Offset;
        }

        private static List<double> Vals(GdStruct o, string name)
        {
            List<double> r = new List<double>();
            foreach (object x in o[name] as IList ?? new object[0]) r.Add(Num.ToDouble(x));
            return r;
        }

        /// <summary>the shape FUN_146c628e0 builds (null when the game builds none)</summary>
        public static Shape BuildShape(GdStruct o)
        {
            byte[] quads = o["Quads"] as byte[] ?? new byte[0];
            int nq = quads.Length >> 5;
            if (nq == 0) return null;
            Shape s = new Shape();
            for (int k = 0; k < 4; k++)
            {
                List<double> vals = Vals(o, Axes[k]).Take(16).ToList();
                s.N[k] = vals.Count;
                float scale = Axes[k] == "Speeds" ? KSpeed : KAngle;
                List<int> b = vals.Select(x => AxisByte(x, scale)).ToList();
                if (b.Count < 16)
                {
                    int pad = Axes[k] == "MoveAngles" ? 127 : (b.Count > 0 ? b[b.Count - 1] : 0);   // no values: an undefined stack byte
                    while (b.Count < 16) b.Add(pad);
                }
                s.AxesB[k] = b.ToArray();
            }
            long cells = (long)s.N[0] * s.N[1] * s.N[2] * s.N[3];
            s.QuadMap = (o["QuadMap"] as IList ?? new object[0]).Cast<object>().Select(x => Num.ToLong(x) & 0xFFFFFFFF).Take((int)Math.Min(cells, int.MaxValue)).ToList();
            float[] off = o["OffsetPos"] as float[] ?? new float[4];
            s.Offset = new[] { off[0], off[1], off[2], o.GetFloat("OffsetTouchT") };
            s.Strides = new long[] { (long)s.N[1] * s.N[2] * s.N[3], (long)s.N[2] * s.N[3], s.N[3], 1 };
            s.Quads = quads.Take(nq * 32).ToArray();
            s.NQuads = nq;
            return s;
        }

        /// <summary>FUN_146cd8ff0 for one axis: (segment index, fraction) for the int8 table a and query byte q</summary>
        public static void CellAxis(int[] a0, int q0, out int i, out float f)
        {
            int[] a = a0.Select(v => S8(v)).ToArray();
            int q = S8(q0);
            int lo = a[0], hi = a[15];
            int m = q > hi ? hi : q;
            int lo1 = S8(lo + 1);
            int xp = m > lo1 ? m : lo1;
            int x = m > lo ? m : lo;
            bool[] mask = new bool[17];
            for (int j = 0; j < 16; j++) mask[j] = xp > a[j];
            i = 0;
            for (int j = 0; j < 16; j++) if (mask[j] && !mask[j + 1]) i |= j;
            int nxt = i < 15 ? a[i + 1] : 0;
            int num = (x - a[i]) & 0xFF;
            int den = (nxt - a[i]) & 0xFF;
            f = (float)num / (float)den;
            f = f > 0f ? f : 0f;
            f = f < 1f ? f : 1f;
        }

        private static readonly int[][] Offsets = { new[] { 0, 0, 0 }, new[] { 0, 0, 1 }, new[] { 0, 1, 0 }, new[] { 0, 1, 1 }, new[] { 1, 0, 0 }, new[] { 1, 0, 1 }, new[] { 1, 1, 0 }, new[] { 1, 1, 1 } };

        /// <summary>FUN_146cb5260: the QuadMap index of the 8 corners in A, B, C (D at the cell's index)</summary>
        public static long[] CornerCells(Shape s, int[] cell)
        {
            Func<long, long, long> w = (v, k) => v == k ? 0 : v;
            long[] r = new long[8];
            for (int c = 0; c < 8; c++)
                r[c] = w(cell[0] + Offsets[c][0], s.N[0]) * s.Strides[0] + w(cell[1] + Offsets[c][1], s.N[1]) * s.Strides[1]
                       + w(cell[2] + Offsets[c][2], s.N[2]) * s.Strides[2] + w(cell[3], s.N[3]);
            return r;
        }

        /// <summary>the game's horizontal add: (x3 + x1) + (x2 + x0)</summary>
        private static float HSum(float x0, float x1, float x2, float x3) { return (float)(x3 + x1) + (float)(x2 + x0); }

        private static float C01(float x) { x = x > 0f ? x : 0f; return x < 1f ? x : 1f; }

        /// <summary>FUN_146ca8d40: the fractions after missing entries are taken out, and the existing-entry weight total</summary>
        public static void AdjustFractions(long[] entries, float fA, float fB, float fC, float fD,
                                           out float[] vd0, out float[] vd1, out float[] vc, out float[] vb, out float total)
        {
            bool[] miss = entries.Select(e => e == 0xFFFFFFFF).ToArray();
            float[] dNew = new float[8];
            for (int k = 0; k < 8; k++)
            {
                bool lo = miss[2 * k], hi = miss[2 * k + 1];
                dNew[k] = C01(lo ? 1f : (hi ? 0f : fD));
            }
            float[] Q = new float[4], N = new float[4];
            for (int g = 0; g < 4; g++)
            {
                float[] t = new float[4];
                int ti = 0;
                for (int oc = 0; oc < 2; oc++)
                    for (int od = 0; od < 2; od++)
                    {
                        int e = 4 * g + 2 * oc + od;
                        float dfac = od != 0 ? fD : 1f - fD;
                        float cfac = oc != 0 ? fC : 1f - fC;
                        t[ti++] = miss[e] ? 0f : dfac * cfac;
                    }
                Q[g] = HSum(t[0], t[1], t[2], t[3]);
                N[g] = HSum(0f, 0f, t[2], t[3]);
            }
            float[] cNew = new float[4];
            for (int g = 0; g < 4; g++) cNew[g] = C01(Eps <= Q[g] ? N[g] / Q[g] : 0f);
            float[] afac = { 1f - fA, 1f - fA, fA, fA };
            float[] bfac = { 1f - fB, fB, 1f - fB, fB };
            float[] s = new float[4];
            for (int g = 0; g < 4; g++) s[g] = (float)(afac[g] * bfac[g]) * Q[g];
            float T = HSum(s[0], s[1], s[2], s[3]);
            float NA = HSum(0f, 0f, s[2], s[3]);
            float[] num = { s[1], s[3], NA };
            float[] den = { s[1] + s[0], s[3] + s[2], T };
            float[] bNew = new float[3];
            for (int j = 0; j < 3; j++) bNew[j] = C01(Eps <= den[j] ? num[j] / den[j] : 0f);
            float tl = C01(T);
            vd0 = dNew.Take(4).ToArray();
            vd1 = dNew.Skip(4).ToArray();
            vc = cNew;
            vb = new[] { bNew[0], bNew[1], bNew[2], tl };
            total = T;
        }

        /// <summary>FUN_146cadf70: the weight of each of the 16 entries from the adjusted fractions</summary>
        public static float[] EntryWeights(float[] vd0, float[] vd1, float[] vc, float[] vb)
        {
            float[] d = vd0.Concat(vd1).ToArray();
            float[] r = new float[16];
            for (int e = 0; e < 16; e++)
            {
                int oa = e >> 3, ob = (e >> 2) & 1, oc = (e >> 1) & 1, od = e & 1;
                int k = e >> 1, g = e >> 2;
                float dfac = od != 0 ? d[k] : 1f - d[k];
                float cfac = oc != 0 ? vc[g] : 1f - vc[g];
                float fb = vb[oa];
                float bfac = ob != 0 ? fb : 1f - fb;
                float afac = oa != 0 ? vb[2] : 1f - vb[2];
                r[e] = (float)((float)(dfac * cfac) * bfac) * afac;
            }
            return r;
        }

        public sealed class LookupResult
        {
            public int[] QueryBytes;
            public int[] Cell;
            public float[] Fractions;
            public long[] Corners;
            public long?[] Entries;
            public bool IsValid;
            public float WeightTotal;
            public bool OutsideMap;
            public float[] Weights;
            public float TouchTime;
            public long[] Quads;
            public int[] TimeOffsets;
            public float[] AdjD, AdjC, AdjB;
            public float AdjA;
        }

        /// <summary>which quads the game blends for a query, their weights and the touch time</summary>
        public static LookupResult Lookup(Shape s, double inAngle, double outAngle, double moveAngle, double speed)
        {
            int[] q = { QuantizeAngle(inAngle), QuantizeAngle(outAngle), QuantizeAngle(moveAngle), QuantizeSpeed(speed) };
            int[] cell = new int[4];
            float[] frac = new float[4];
            for (int k = 0; k < 4; k++) CellAxis(s.AxesB[k], q[k], out cell[k], out frac[k]);
            long[] corners = CornerCells(s, cell);
            List<long> qm = s.QuadMap;
            Func<long, long?> entry = ix => ix >= 0 && ix < qm.Count ? (long?)qm[(int)ix] : null;
            long?[] entries = new long?[16];
            for (int c = 0; c < 8; c++) { entries[2 * c] = entry(corners[c]); entries[2 * c + 1] = entry(corners[c] + 1); }
            long[] known = entries.Select(x => x ?? 0xFFFFFFFFL).ToArray();
            float[] vd0, vd1, vc, vb;
            float total;
            AdjustFractions(known, frac[0], frac[1], frac[2], frac[3], out vd0, out vd1, out vc, out vb, out total);
            LookupResult r = new LookupResult
            {
                QueryBytes = q, Cell = cell, Fractions = frac, Corners = corners, Entries = entries,
                IsValid = Valid <= total, WeightTotal = total, OutsideMap = entries.Any(x => !x.HasValue)
            };
            if (!r.IsValid) return r;
            float[] w = EntryWeights(vd0, vd1, vc, vb);
            int[] off = known.Select(x => S8(x >> 24)).ToArray();      // a missing entry reads -1 (weight 0)
            float[] groups = new float[4];
            for (int gi = 0; gi < 4; gi++)
                groups[gi] = HSum((float)off[4 * gi] * w[4 * gi], (float)off[4 * gi + 1] * w[4 * gi + 1], (float)off[4 * gi + 2] * w[4 * gi + 2], (float)off[4 * gi + 3] * w[4 * gi + 3]);
            float t = (float)((float)((float)(groups[1] + groups[0]) + groups[2]) + groups[3]) + s.Offset[3];
            long first = known.FirstOrDefault(x => x != 0xFFFFFFFF);
            bool hasFirst = known.Any(x => x != 0xFFFFFFFF);
            r.Weights = w;
            r.TouchTime = t;
            r.Quads = known.Select(x => ((x != 0xFFFFFFFF ? x : (hasFirst ? first : 0xFFFFFFFF)) & 0xFFFFFF)).ToArray();
            r.TimeOffsets = off;
            r.AdjD = vd0.Concat(vd1).ToArray();
            r.AdjC = vc;
            r.AdjB = vb.Take(2).ToArray();
            r.AdjA = vb[2];
            return r;
        }

        /// <summary>the QuadMap as {(iA, iB, iC, iD): (quad index, time offset)} (null for empty cells)</summary>
        public static Dictionary<int[], Tuple<long, int>> QuadMapGrid(GdStruct o)
        {
            int[] n = Axes.Select(a => (int)Math.Min(16, AllocModel.LenOf(o[a]))).ToArray();
            List<long> qm = (o["QuadMap"] as IList ?? new object[0]).Cast<object>().Select(x => Num.ToLong(x) & 0xFFFFFFFF).ToList();
            Dictionary<int[], Tuple<long, int>> r = new Dictionary<int[], Tuple<long, int>>();
            long[] s = { (long)n[1] * n[2] * n[3], (long)n[2] * n[3], n[3], 1 };
            for (int ia = 0; ia < n[0]; ia++)
                for (int ib = 0; ib < n[1]; ib++)
                    for (int ic = 0; ic < n[2]; ic++)
                        for (int id = 0; id < n[3]; id++)
                        {
                            long ix = ia * s[0] + ib * s[1] + ic * s[2] + id;
                            long x = ix < qm.Count ? qm[(int)ix] : 0xFFFFFFFF;
                            r[new[] { ia, ib, ic, id }] = x == 0xFFFFFFFF ? null : Tuple.Create(x & 0xFFFFFF, S8(x >> 24));
                        }
            return r;
        }

        /// <summary>what the game would read wrongly in this ContinuousContactAsset</summary>
        public static List<string> Issues(GdStruct o)
        {
            List<string> r = new List<string>();
            foreach (string a in Axes)
            {
                List<double> vals = Vals(o, a);
                if (vals.Count == 0) r.Add(a + " is empty: the shape pads that axis with an undefined byte");
                if (vals.Count > 16) r.Add(a + " has " + vals.Count + " values; the game uses the first 16");
                float k = a == "Speeds" ? KSpeed : KAngle;
                List<int> b = vals.Take(16).Select(x => AxisByte(x, k)).ToList();
                bool desc = false;
                for (int j = 0; j + 1 < b.Count; j++) if (b[j] > b[j + 1]) desc = true;
                if (desc) r.Add(a + " is not ascending after quantization ([" + string.Join(", ", b) + "]); the cell search assumes it is");
            }
            return r;
        }
    }
}
