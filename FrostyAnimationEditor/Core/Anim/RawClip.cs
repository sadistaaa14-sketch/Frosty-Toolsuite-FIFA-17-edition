using System;
using System.Collections;
using System.Collections.Generic;

namespace FrostyAnimationEditor.Core.Anim
{
    /// <summary>
    /// RawAnimationAsset ("RAW ") and FrameAnimationAsset ("FRAM") - ports of the runtime.
    /// RAW (loader FUN_145d26dc0, sampler FUN_145d2b310/FUN_145d2b570): Data = NumKeys rows of
    /// align4(4*QuatCount + 4*Vec3Count + FloatCount) floats [quats x4][vec3 x4][floats][pad]; ConstData
    /// [ConstQuat x4][ConstVec3 x4][ConstFloat]; MappingIndices stored channel -> rig channel; ChannelIndices
    /// the inverse; KeyTimes i16 frame of every key; seek FUN_145d2d160 (Cycle = loop); quats blended
    /// shortest-arc + normalised, vec/float lerp.
    /// FRAM (loader FUN_145d26cd0): one static pose [QuatCount x4][Vec3Count x4][FloatCount].
    /// </summary>
    public static class KeyTime
    {
        /// <summary>FUN_145d2d160 / FUN_145d3bdf0: frame -> (keyA, keyB, alpha) over a key-time table</summary>
        public static void Seek(IList<long> kt, double t, bool cycle, long endFrame, out int a, out int b, out double al)
        {
            int n = kt.Count;
            if (n == 0)
            {
                double f = PyFormat.Floor(t);
                long last = endFrame;
                if (cycle)
                {
                    long m = last + 1;
                    long fi0 = PyMod((long)f, m);
                    a = (int)fi0;
                    b = (int)PyMod(fi0 + 1, m);
                    al = t - f;
                    return;
                }
                if (t < 0) { a = 0; b = 0; al = 0; return; }
                if (t >= last) { a = (int)last; b = (int)last; al = 0; return; }
                a = (int)f; b = (int)f + 1; al = t - f;
                return;
            }
            if (!cycle)
            {
                if (t < 0) { a = 0; b = 0; al = 0; return; }
                if (t >= kt[n - 1]) { a = n - 1; b = n - 1; al = 0; return; }
                double fi = PyFormat.Floor(t);
                int i = 0;
                while (i + 1 < n - 1 && kt[i + 1] <= fi) i++;
                a = i;
                b = i + 1;
                long span = kt[b] - kt[a];
                al = span != 0 ? (t - kt[a]) / span : 0.0;
                return;
            }
            long period = endFrame + 1;
            double fl = PyFormat.Floor(t);
            if (t < 0)
            {
                t += (1 - FloorDiv((long)fl, period)) * period;
                fl = PyFormat.Floor(t);
            }
            if (fl >= period)
            {
                long r = PyMod((long)fl, period);
                t = r + (t - fl);
                fl = r;
            }
            int j = 0;
            while (j + 1 < n && kt[j + 1] <= fl) j++;
            a = j;
            b = j + 1;
            if (b >= n) { b = 0; al = t - fl; return; }
            long sp = kt[b] - kt[a];
            al = sp != 0 ? (t - kt[a]) / sp : 0.0;
        }

        internal static long PyMod(long x, long m) { long r = x % m; return r < 0 ? r + m : r; }
        internal static long FloorDiv(long x, long m) { long q = x / m; if ((x % m != 0) && ((x < 0) != (m < 0))) q--; return q; }
    }

    public sealed class RawClip
    {
        public const uint Tag = 0x52415720;   // "RAW "

        public int NumKeys, Q, V, F, CQ, CV, CF;
        public List<long> KeyTimes, MappingIndices, ChannelIndices;
        public float[] Data, Const;
        public bool Cycle;
        public long EndFrame;
        public int Stride;

        private static List<long> Longs(object v)
        {
            List<long> r = new List<long>();
            if (v is byte[]) { foreach (byte b in (byte[])v) r.Add(b); return r; }
            IList l = v as IList;
            if (l != null) foreach (object x in l) r.Add(Num.ToLong(x));
            return r;
        }

        private static float[] Floats(object v)
        {
            IList l = v as IList;
            if (l == null) return new float[0];
            float[] r = new float[l.Count];
            for (int i = 0; i < r.Length; i++) r[i] = Num.ToFloat(l[i]);
            return r;
        }

        public RawClip(GdStruct o)
        {
            NumKeys = (int)o.GetInt("NumKeys");
            Q = (int)o.GetInt("QuatCount"); V = (int)o.GetInt("Vec3Count"); F = (int)o.GetInt("FloatCount");
            CQ = (int)o.GetInt("ConstQuatCount"); CV = (int)o.GetInt("ConstVec3Count"); CF = (int)o.GetInt("ConstFloatCount");
            KeyTimes = Longs(o["KeyTimes"]);
            MappingIndices = Longs(o["MappingIndices"]);
            ChannelIndices = Longs(o["ChannelIndices"]);
            Data = Floats(o["Data"]);
            Const = Floats(o["ConstData"]);
            Cycle = o.GetBool("Cycle");
            GdStruct b = o["__base"] as GdStruct;
            EndFrame = b != null && b["EndFrame"] != null ? b.GetInt("EndFrame") : NumKeys - 1;
            Stride = (4 * Q + 4 * V + F + 3) & ~3;
            if (NumKeys != 0 && Stride != 0 && Data.Length != NumKeys * Stride)
                throw new GdException("RAW Data length " + Data.Length + " != NumKeys*stride " + NumKeys * Stride);
        }

        public List<string> StoredKinds()
        {
            List<string> r = new List<string>();
            for (int i = 0; i < Q; i++) r.Add("quat");
            for (int i = 0; i < V; i++) r.Add("vec3");
            for (int i = 0; i < F; i++) r.Add("float");
            for (int i = 0; i < CQ; i++) r.Add("quat");
            for (int i = 0; i < CV; i++) r.Add("vec3");
            for (int i = 0; i < CF; i++) r.Add("float");
            return r;
        }

        /// <summary>per stored channel, every key's value [NumKeys][comps]</summary>
        public List<float[][]> KeyRows()
        {
            int K = NumKeys;
            List<float[][]> outp = new List<float[][]>();
            Func<int, int, float[][]> slice = (o, n) =>
            {
                float[][] r = new float[K][];
                for (int k = 0; k < K; k++)
                {
                    r[k] = new float[n];
                    for (int c = 0; c < n; c++) r[k][c] = Data[k * Stride + o + c];
                }
                return r;
            };
            for (int i = 0; i < Q; i++) outp.Add(slice(4 * i, 4));
            int ofs = 4 * Q;
            for (int i = 0; i < V; i++) outp.Add(slice(ofs + 4 * i, 3));
            ofs += 4 * V;
            for (int i = 0; i < F; i++) outp.Add(slice(ofs + i, 1));
            int p = 0;
            Func<int, int, float[][]> rep = (o, n) =>
            {
                float[][] r = new float[K][];
                for (int k = 0; k < K; k++)
                {
                    r[k] = new float[n];
                    for (int c = 0; c < n; c++) r[k][c] = o + c < Const.Length ? Const[o + c] : 0f;
                }
                return r;
            };
            for (int i = 0; i < CQ; i++) { outp.Add(rep(p, 4)); p += 4; }
            for (int i = 0; i < CV; i++) { outp.Add(rep(p, 3)); p += 4; }
            for (int i = 0; i < CF; i++) { outp.Add(rep(p, 1)); p += 1; }
            return outp;
        }

        public int FrameCount() { return (int)EndFrame + 1; }

        /// <summary>-> per stored channel [nframes][comps] (runtime blend rules)</summary>
        public List<float[][]> SampleFrames(IList<int> frames)
        {
            List<float[][]> keys = KeyRows();
            List<string> kinds = StoredKinds();
            if (frames == null) { List<int> fr = new List<int>(); for (int i = 0; i < FrameCount(); i++) fr.Add(i); frames = fr; }
            List<float[][]> res = new List<float[][]>();
            for (int j = 0; j < keys.Count; j++) res.Add(new float[frames.Count][]);
            for (int n = 0; n < frames.Count; n++)
            {
                int a, b;
                double ald;
                KeyTime.Seek(KeyTimes, frames[n], Cycle, EndFrame, out a, out b, out ald);
                float al = (float)ald;
                for (int j = 0; j < keys.Count; j++)
                {
                    float[][] arr = keys[j];
                    if (arr.Length == 0) { res[j][n] = new float[0]; continue; }
                    float[] va = arr[Math.Min(a, arr.Length - 1)], vb = arr[Math.Min(b, arr.Length - 1)];
                    float[] q = new float[va.Length];
                    if (kinds[j] == "quat")
                    {
                        float dot = 0f;
                        for (int c = 0; c < 4; c++) dot = (float)(dot + (float)(va[c] * vb[c]));
                        if (dot > 0f)
                            for (int c = 0; c < q.Length; c++) q[c] = (float)((float)((float)(vb[c] - va[c]) * al) + va[c]);
                        else
                            for (int c = 0; c < q.Length; c++) q[c] = (float)(va[c] - (float)((float)(va[c] + vb[c]) * al));
                        float s = 0f;
                        for (int c = 0; c < q.Length; c++) s = (float)(s + (float)(q[c] * q[c]));
                        float nn = (float)Math.Sqrt(s);
                        if (nn == 0) nn = 1f;
                        for (int c = 0; c < q.Length; c++) q[c] = q[c] / nn;
                    }
                    else
                        for (int c = 0; c < q.Length; c++) q[c] = (float)((float)((float)(vb[c] - va[c]) * al) + va[c]);
                    res[j][n] = q;
                }
            }
            return res;
        }
    }

    public sealed class FrameClip
    {
        public const uint Tag = 0x4652414D;   // "FRAM"
        public int Q, V, F;
        public float[] Data;

        public FrameClip(GdStruct o)
        {
            Q = (int)o.GetInt("QuatCount"); V = (int)o.GetInt("Vec3Count"); F = (int)o.GetInt("FloatCount");
            IList l = o["Data"] as IList;
            Data = new float[l != null ? l.Count : 0];
            for (int i = 0; i < Data.Length; i++) Data[i] = Num.ToFloat(l[i]);
        }

        public List<string> StoredKinds()
        {
            List<string> r = new List<string>();
            for (int i = 0; i < Q; i++) r.Add("quat");
            for (int i = 0; i < V; i++) r.Add("vec3");
            for (int i = 0; i < F; i++) r.Add("float");
            return r;
        }

        public List<float[]> Pose()
        {
            List<float[]> r = new List<float[]>();
            Func<int, int, float[]> sl = (o, n) =>
            {
                float[] e = new float[n];
                for (int c = 0; c < n; c++) e[c] = o + c < Data.Length ? Data[o + c] : 0f;
                return e;
            };
            for (int i = 0; i < Q; i++) r.Add(sl(4 * i, 4));
            int ofs = 4 * Q;
            for (int i = 0; i < V; i++) r.Add(sl(ofs + 4 * i, 3));
            ofs += 4 * V;
            for (int i = 0; i < F; i++) r.Add(sl(ofs + i, 1));
            return r;
        }
    }
}
