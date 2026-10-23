using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;

namespace FrostyAnimationEditor.Core.Anim
{
    public sealed class Channel
    {
        public long? Dof;
        public string Name;
        public string Kind;     // "quat" (x,y,z,w), "vec3" (x,y,z), "float" (1 value)
        public Channel(long? dof, string name, string kind) { Dof = dof; Name = name; Kind = kind; }
        public int Comps { get { return Kind == "quat" ? 4 : Kind == "vec3" ? 3 : 1; } }
    }

    /// <summary>Unified access to any animation clip of the database (VBR / DCT / RAW / FRAM).</summary>
    public sealed class AnimClip
    {
        public static readonly Dictionary<string, string> CodecClasses = new Dictionary<string, string>
        {
            { "VbrAnimationAsset", "VBR " }, { "DctAnimationAsset", "DCT " }, { "RawAnimationAsset", "RAW " }, { "FrameAnimationAsset", "FRAM" },
        };

        public static readonly Dictionary<string, uint> CodecTags = new Dictionary<string, uint>
        {
            { "VBR ", 0x56425220 }, { "DCT ", 0x44435420 }, { "RAW ", 0x52415720 }, { "FRAM", 0x4652414D },
        };

        public static bool IsClipClass(string cls) { return CodecClasses.ContainsKey(cls); }

        public readonly AntDb Db;
        public readonly GdObj Obj;
        public readonly string Codec;
        public readonly GdObj Base;
        public readonly ulong Key;
        public readonly string Name;
        public readonly long EndFrame;
        public readonly bool Additive;
        public readonly GdObj C2d;
        public readonly List<long> DofIds = new List<long>();
        public readonly ulong LayoutKey;
        public readonly RigIndex Ri;
        public readonly VbrClip Vbr;
        public readonly DctClip Dct;
        public readonly RawClip Raw;
        public readonly FrameClip Fram;
        public readonly List<string> Kinds;
        public readonly List<Channel> Channels = new List<Channel>();
        private readonly List<int> order;

        public AnimClip(AntDb db, GdObj obj, RigIndex rigIndex = null)
        {
            Db = db;
            Obj = obj;
            string codec;
            if (!CodecClasses.TryGetValue(obj.Type.Name, out codec)) throw new GdException(obj.Type.Name + " is not an animation clip class");
            Codec = codec;
            Base = obj["__base"] as GdObj;
            Key = obj.Key;
            Name = obj.Name;
            EndFrame = Base != null ? Base.GetInt("EndFrame") : 0;
            Additive = Base != null && Base.GetBool("Additive");
            C2d = Base != null && db != null ? db.Get(Base.GetKey("ChannelToDofAsset")) : null;
            if (C2d != null)
            {
                object ids = C2d["DofIds"];
                if (ids is byte[]) foreach (byte b in (byte[])ids) DofIds.Add(b);
                else foreach (object x in ids as IList ?? new object[0]) DofIds.Add(Num.ToLong(x));
            }
            LayoutKey = Base != null ? Base.GetKey("DofSetList") : 0;
            Ri = rigIndex;
            List<string> kinds;
            if (Codec == "VBR ")
            {
                Vbr = new VbrClip(obj);
                kinds = Rep("quat", Vbr.Q + Vbr.CQ).Concat(Rep("vec3", Vbr.V + Vbr.CV)).Concat(Rep("float", Vbr.F + Vbr.CF)).ToList();
                order = Enumerable.Range(0, kinds.Count).ToList();
            }
            else if (Codec == "DCT ")
            {
                Dct = new DctClip(obj);
                kinds = Rep("quat", Dct.NumQuats).Concat(Rep("vec3", Dct.NumVec3)).Concat(Rep("float", Dct.NumFloat)).ToList();
                order = Enumerable.Range(0, kinds.Count).ToList();
            }
            else if (Codec == "RAW ")
            {
                Raw = new RawClip(obj);
                List<string> ks = Raw.StoredKinds();
                kinds = Enumerable.Repeat<string>(null, ks.Count).ToList();
                for (int s = 0; s < Raw.MappingIndices.Count && s < ks.Count; s++)
                {
                    int r = (int)Raw.MappingIndices[s];
                    if (r >= 0 && r < kinds.Count) kinds[r] = ks[s];
                }
                order = Raw.MappingIndices.Select(x => (int)x).ToList();
            }
            else
            {
                Fram = new FrameClip(obj);
                kinds = Fram.StoredKinds();
                order = Enumerable.Range(0, kinds.Count).ToList();
            }
            Kinds = kinds;
            for (int j = 0; j < kinds.Count; j++)
            {
                long? d = j < DofIds.Count ? (long?)DofIds[j] : null;
                string nm = rigIndex != null && d.HasValue ? rigIndex.DofName(d.Value) : (d.HasValue ? "dof_" + d.Value : "channel_" + j);
                Channels.Add(new Channel(d, nm, kinds[j]));
            }
        }

        private static IEnumerable<string> Rep(string s, int n) { return Enumerable.Repeat(s, Math.Max(0, n)); }

        public int FrameCount() { return (int)EndFrame + 1; }

        public bool Loops
        {
            get
            {
                if (Vbr != null) return Vbr.Loops;
                if (Dct != null) return Dct.Cycle;
                if (Raw != null) return Raw.Cycle;
                return false;
            }
        }

        /// <summary>-> per rig channel [nframes][comps]</summary>
        public List<float[][]> Sample(IList<int> frames = null)
        {
            int n = Kinds.Count;
            float[][][] outp = new float[n][][];
            if (Vbr != null || Dct != null)
            {
                float[][][] q, v;
                float[][] f;
                if (Vbr != null) Vbr.SampleFrames(frames, out q, out v, out f);
                else Dct.SampleFrames(frames, out q, out v, out f);
                int nf = q.Length;
                int nq = nf > 0 ? q[0].Length : 0, nv = nf > 0 ? v[0].Length : 0, nfl = nf > 0 ? f[0].Length : 0;
                if (nf == 0)
                {
                    for (int j = 0; j < n; j++) outp[j] = new float[0][];
                    return outp.ToList();
                }
                int jj = 0;
                for (int i = 0; i < nq; i++, jj++) { outp[jj] = new float[nf][]; for (int r = 0; r < nf; r++) outp[jj][r] = q[r][i]; }
                for (int i = 0; i < nv; i++, jj++) { outp[jj] = new float[nf][]; for (int r = 0; r < nf; r++) outp[jj][r] = v[r][i]; }
                for (int i = 0; i < nfl && jj < n; i++, jj++) { outp[jj] = new float[nf][]; for (int r = 0; r < nf; r++) outp[jj][r] = new[] { f[r][i] }; }
            }
            else if (Raw != null)
            {
                List<float[][]> vals = Raw.SampleFrames(frames);
                for (int s = 0; s < vals.Count && s < order.Count; s++) outp[order[s]] = vals[s];
            }
            else
            {
                List<float[]> pose = Fram.Pose();
                int nf = frames != null ? frames.Count : FrameCount();
                for (int s = 0; s < pose.Count; s++)
                {
                    outp[s] = new float[nf][];
                    for (int r = 0; r < nf; r++) outp[s][r] = (float[])pose[s].Clone();
                }
            }
            return outp.ToList();
        }

        /// <summary>the clip's stored keys without interpolation: key frames and per channel [nkeys][comps]</summary>
        public List<float[][]> KeysOnly(out List<long> keyFrames)
        {
            if (Vbr != null || Dct != null)
            {
                float[][][] q, v;
                float[][] f;
                if (Vbr != null) { Vbr.KeyChannels(out q, out v, out f); keyFrames = VbrKeyFrames(); }
                else { Dct.KeyChannels(out q, out v, out f); keyFrames = Dct.KeyFrames(); }
                int K = q.Length;
                List<float[][]> vals = new List<float[][]>();
                int nq = Vbr != null ? Vbr.Q + Vbr.CQ : Dct.NumQuats;
                int nv = Vbr != null ? Vbr.V + Vbr.CV : Dct.NumVec3;
                int nfl = Vbr != null ? Vbr.F + Vbr.CF : (K > 0 ? f[0].Length : 0);
                for (int i = 0; i < nq; i++) { float[][] a = new float[K][]; for (int r = 0; r < K; r++) a[r] = q[r][i]; vals.Add(a); }
                for (int i = 0; i < nv; i++) { float[][] a = new float[K][]; for (int r = 0; r < K; r++) a[r] = v[r][i]; vals.Add(a); }
                for (int i = 0; i < nfl; i++) { float[][] a = new float[K][]; for (int r = 0; r < K; r++) a[r] = new[] { f[r][i] }; vals.Add(a); }
                return vals;
            }
            if (Raw != null)
            {
                List<float[][]> rows = Raw.KeyRows();
                float[][][] vals = new float[rows.Count][][];
                for (int s = 0; s < rows.Count && s < order.Count; s++) vals[order[s]] = rows[s];
                keyFrames = Raw.KeyTimes.Count > 0 ? new List<long>(Raw.KeyTimes) : Enumerable.Range(0, Raw.NumKeys).Select(x => (long)x).ToList();
                return vals.ToList();
            }
            keyFrames = new List<long> { 0 };
            return Fram.Pose().Select(p => new[] { p }).ToList();
        }

        /// <summary>frame of every VBR key (inverts the seek)</summary>
        public List<long> VbrKeyFrames()
        {
            VbrClip c = Vbr;
            if (c.OneKeyPerFrame) return Enumerable.Range(0, c.NumKeys).Select(x => (long)x).ToList();
            long?[] frames = new long?[c.NumKeys];
            for (int fr = 0; fr < FrameCount(); fr++)
            {
                int a, b;
                double al;
                c.Seek(fr, out a, out b, out al);
                if (al == 0.0 && a >= 0 && a < frames.Length && !frames[a].HasValue) frames[a] = fr;
            }
            long last = 0;
            List<long> r = new List<long>(frames.Length);
            for (int k = 0; k < frames.Length; k++)
            {
                if (!frames[k].HasValue) frames[k] = last;
                last = frames[k].Value;
                r.Add(last);
            }
            return r;
        }

        public static AnimClip Load(AntDb db, ulong key, RigIndex ri = null)
        {
            GdObj o = db.Get(key);
            if (o == null) throw new KeyNotFoundException("no object with key " + BE.KeyHex(key));
            return new AnimClip(db, o, ri);
        }
    }
}
