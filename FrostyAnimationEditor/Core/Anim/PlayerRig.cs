using System;
using System.Collections.Generic;
using System.Linq;

namespace FrostyAnimationEditor.Core.Anim
{
    /// <summary>
    /// A render skeleton (the FIFA 17 player: EBX SkeletonAsset content/character/rig/skeleton/player/skeleton_player,
    /// 438 bones). Transforms are in meters, column-vector convention (World = ParentWorld * Local).
    ///
    /// How antstate clips drive it (checked against fifagame_win32_antstate and the EBX):
    ///  * player_rig_17_rendering's skeleton (antrig_fifa17_player_male_a) has the same 438 joints, in the same order,
    ///    with the same parents. player_rig_17 (82 joints) and player_rig_17_story (88) use the same names and parents;
    ///    only Left/RightAnkleEffectorAux (player_rig_17) are not in the EBX skeleton.
    ///  * translations: EBX LocalPose.trans = rig default translation / 3.281 for all 437 joints but Hips (max error
    ///    2.9e-7 m): the rig is in feet, the EBX in meters.
    ///  * rotations: equal (within 1e-3) except Hips (player_rig_17 and _rendering face +X, the EBX and _story face +Z),
    ///    Trajectory, TrajectoryEnd, Prop, and 32 shoulder / arm / hand / finger joints whose rig default is another
    ///    rest pose than the EBX bind pose.
    ///  * a clip's channels hold absolute local transforms (the editor's preview and export already use them so).
    /// So a clip poses this skeleton by name: channel values for the joints it animates (translations / 3.281), the
    /// rig defaults for the others (the clip's rig, then the rig whose skeleton matches this one best), the bind pose
    /// for bones no rig knows.
    /// </summary>
    public sealed class PlayerSkeleton
    {
        /// <summary>meters per rig unit (feet): EBX translation = rig translation / 3.281</summary>
        public const double RigToMeters = 1.0 / 3.281;
        public const string PlayerSkeletonAsset = "content/character/rig/skeleton/player/skeleton_player";

        public string Name = "";
        public string Source = "";
        public List<string> Bones = new List<string>();
        public List<int> Parents = new List<int>();
        public List<Affine> Local = new List<Affine>();   // bind pose, parent-relative
        public List<Affine> Model = new List<Affine>();   // bind pose, world
        /// <summary>bones added for clip joints the skeleton does not have (e.g. LeftAnkleEffectorAux)</summary>
        public HashSet<int> Extra = new HashSet<int>();
        private Dictionary<string, int> index;

        public int Count { get { return Bones.Count; } }

        public int Find(string name)
        {
            if (index == null)
            {
                index = new Dictionary<string, int>(StringComparer.Ordinal);
                for (int i = 0; i < Bones.Count; i++) if (!index.ContainsKey(Bones[i])) index[Bones[i]] = i;
            }
            int r;
            return name != null && index.TryGetValue(name, out r) ? r : -1;
        }

        /// <summary>world bind transforms from the local ones (parents come before children, as in the EBX)</summary>
        public void ComputeModel()
        {
            Model = new List<Affine>(Local.Count);
            for (int i = 0; i < Local.Count; i++)
            {
                int p = Parents[i];
                Model.Add(p >= 0 && p < i ? Model[p] * Local[i] : Local[i]);
            }
            index = null;
        }

        /// <summary>bones in an order where every parent comes before its children</summary>
        public int[] TopoOrder()
        {
            int n = Count;
            int[] depth = new int[n];
            for (int i = 0; i < n; i++)
            {
                int d = 0, j = i;
                while (Parents[j] >= 0 && Parents[j] < n && d < 1024) { j = Parents[j]; d++; }
                depth[i] = d;
            }
            return Enumerable.Range(0, n).OrderBy(i => depth[i]).ThenBy(i => i).ToArray();
        }

        /// <summary>
        /// from EBX data: names, Hierarchy (parent index, -1 for the root) and LocalPose as 12 numbers per bone
        /// (right, up, forward, trans).
        /// </summary>
        public static PlayerSkeleton FromLinearTransforms(string name, string source, IList<string> names, IList<int> parents, IList<double[]> localPose)
        {
            if (names.Count != parents.Count || names.Count != localPose.Count) throw new ArgumentException("bone names, hierarchy and local pose differ in length");
            PlayerSkeleton s = new PlayerSkeleton { Name = name, Source = source };
            for (int i = 0; i < names.Count; i++)
            {
                double[] r = localPose[i];
                s.Bones.Add(names[i]);
                s.Parents.Add(parents[i] >= names.Count ? -1 : parents[i]);
                s.Local.Add(Affine.FromLinearTransform(r[0], r[1], r[2], r[3], r[4], r[5], r[6], r[7], r[8], r[9], r[10], r[11]));
            }
            s.ComputeModel();
            return s;
        }

        /// <summary>a skeleton from an antstate rig: its SkeletonAsset joints and its default pose (feet -> meters)</summary>
        public static PlayerSkeleton FromRig(RigIndex ri, GdObj rig)
        {
            RigIndex.Skeleton sk = ri.GetSkeleton(rig);
            if (sk == null) throw new GdException("the rig " + rig.Name + " has no skeleton");
            Dictionary<string, Dictionary<string, float[]>> bind = ri.BindPose(rig);
            PlayerSkeleton s = new PlayerSkeleton { Name = sk.Name, Source = "antstate rig " + rig.Name + " (default pose)" };
            foreach (RigIndex.Joint j in sk.Joints)
            {
                s.Bones.Add(j.Name);
                s.Parents.Add(j.Parent);
                s.Local.Add(DefaultLocal(bind, j.Name, Affine.Identity));
            }
            s.ComputeModel();
            return s;
        }

        internal static Affine DefaultLocal(Dictionary<string, Dictionary<string, float[]>> bind, string joint, Affine fallback)
        {
            Dictionary<string, float[]> b;
            if (bind == null || !bind.TryGetValue(joint, out b)) return fallback;
            V3 ft, fs;
            Q4 fr;
            fallback.Decompose(out ft, out fr, out fs);
            float[] v;
            V3 t = b.TryGetValue("t", out v) && v.Length >= 3 ? V3.Of(v, RigToMeters) : ft;
            Q4 q = b.TryGetValue("q", out v) && v.Length >= 4 ? Q4.Of(v).Normalized() : fr;
            V3 sc = b.TryGetValue("s", out v) && v.Length >= 3 ? V3.Of(v) : fs;
            return Affine.FromTRS(t, q, sc);
        }

        public PlayerSkeleton Clone()
        {
            PlayerSkeleton s = new PlayerSkeleton { Name = Name, Source = Source };
            s.Bones.AddRange(Bones);
            s.Parents.AddRange(Parents);
            s.Local.AddRange(Local);
            s.Model.AddRange(Model);
            s.Extra.UnionWith(Extra);
            return s;
        }
    }

    /// <summary>A clip evaluated on a skeleton: local transforms of every bone at every frame (meters).</summary>
    public sealed class PlayerAnimation
    {
        public PlayerSkeleton Skeleton;
        public string ClipName = "";
        public ulong ClipKey;
        public string RigName = "";
        public string DefaultsRig = "";
        public double Fps = 30;
        public int FrameCount;
        public Affine[][] Local;          // [frame][bone]
        public bool[] Driven;             // the clip animates the bone (rotation, translation or scale)
        public bool[] Posed;              // the bone's pose differs from the bind pose at some frame
        public int ChannelJoints;         // joints the clip animates
        public int MappedJoints;          // ... that are in the skeleton (or were added as extra bones)
        public Affine[] Defaults;         // local transform (meters) of each bone where the clip has no channel
        public List<string> Notes = new List<string>();

        public Affine[] World(int frame)
        {
            int n = Skeleton.Count;
            Affine[] w = new Affine[n];
            Affine[] l = Local[Math.Max(0, Math.Min(FrameCount - 1, frame))];
            foreach (int i in Skeleton.TopoOrderCached())
            {
                int p = Skeleton.Parents[i];
                w[i] = p >= 0 ? w[p] * l[i] : l[i];
            }
            return w;
        }

        /// <summary>
        /// Evaluate clip key on the skeleton. rendering: the rig that holds defaults for bones the clip's own rig does not
        /// have (null: the rig whose skeleton shares the most joint names with sk).
        /// </summary>
        public static PlayerAnimation FromClip(AntDb db, ulong key, RigIndex ri, PlayerSkeleton sk, double fps = 30, GdObj rendering = null)
        {
            AnimClip c = AnimClip.Load(db, key, ri);
            GdObj rig = ri.BestRig(c.LayoutKey, c.DofIds);
            if (rendering == null) rendering = BestMatchingRig(ri, sk);
            Dictionary<string, Dictionary<string, float[]>> clipBind = rig != null ? ri.BindPose(rig) : null;
            Dictionary<string, Dictionary<string, float[]>> rendBind = rendering != null ? ri.BindPose(rendering) : null;
            RigIndex.Skeleton rigSk = rig != null ? ri.GetSkeleton(rig) : null;

            int n = c.FrameCount();
            List<int> frames = Enumerable.Range(0, n).ToList();
            List<float[][]> vals = c.Sample(frames);
            Dictionary<string, float[][]> track = new Dictionary<string, float[][]>(StringComparer.Ordinal);
            HashSet<string> joints = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < c.Channels.Count; i++)
            {
                Channel ch = c.Channels[i];
                track[ch.Name] = vals[i];
                int dot = ch.Name.LastIndexOf('.');
                string comp = dot >= 0 ? ch.Name.Substring(dot + 1) : "";
                if (dot > 0 && (comp == "q" || comp == "t" || comp == "s") && ch.Kind != "float") joints.Add(ch.Name.Substring(0, dot));
            }

            PlayerSkeleton s = sk;
            List<string> unmapped = joints.Where(j => sk.Find(j) < 0).ToList();
            if (unmapped.Count > 0 && rigSk != null)
            {
                s = sk.Clone();
                bool added = true;
                while (added)
                {
                    added = false;
                    foreach (string j in unmapped.ToList())
                    {
                        RigIndex.Joint rj = rigSk.Joints.FirstOrDefault(x => x.Name == j);
                        if (rj == null) continue;
                        string parent = rj.Parent >= 0 && rj.Parent < rigSk.Joints.Count ? rigSk.Joints[rj.Parent].Name : null;
                        int pi = parent != null ? s.Find(parent) : -1;
                        if (parent != null && pi < 0) continue;
                        s.Bones.Add(j);
                        s.Parents.Add(pi);
                        s.Local.Add(PlayerSkeleton.DefaultLocal(clipBind, j, Affine.Identity));
                        s.Extra.Add(s.Bones.Count - 1);
                        unmapped.Remove(j);
                        added = true;
                    }
                }
                s.ComputeModel();
            }

            PlayerAnimation a = new PlayerAnimation
            {
                Skeleton = s, ClipKey = key, ClipName = c.Name, RigName = rig != null ? rig.Name : "", DefaultsRig = rendering != null ? rendering.Name : "",
                Fps = fps > 0 ? fps : 30, FrameCount = n, ChannelJoints = joints.Count, MappedJoints = joints.Count - unmapped.Count
            };
            if (unmapped.Count > 0) a.Notes.Add("joints of the clip that are not in " + s.Name + ": " + string.Join(", ", unmapped));
            if (c.Additive) a.Notes.Add("additive clip: shown as absolute values");

            int nb = s.Count;
            Affine[] defaults = new Affine[nb];
            for (int b = 0; b < nb; b++)
            {
                string bn = s.Bones[b];
                Affine d = s.Local[b];
                if (rendBind != null && rendBind.ContainsKey(bn)) d = PlayerSkeleton.DefaultLocal(rendBind, bn, d);
                if (clipBind != null && clipBind.ContainsKey(bn)) d = PlayerSkeleton.DefaultLocal(clipBind, bn, d);
                defaults[b] = d;
            }
            a.Defaults = defaults;
            float[][][] tq = new float[nb][][], tt = new float[nb][][], ts = new float[nb][][];
            a.Driven = new bool[nb];
            a.Posed = new bool[nb];
            for (int b = 0; b < nb; b++)
            {
                string bn = s.Bones[b];
                float[][] x;
                if (track.TryGetValue(bn + ".q", out x)) tq[b] = x;
                if (track.TryGetValue(bn + ".t", out x)) tt[b] = x;
                if (track.TryGetValue(bn + ".s", out x)) ts[b] = x;
                a.Driven[b] = tq[b] != null || tt[b] != null || ts[b] != null;
            }
            a.Local = new Affine[n][];
            for (int f = 0; f < n; f++)
            {
                Affine[] row = new Affine[nb];
                for (int b = 0; b < nb; b++)
                {
                    if (!a.Driven[b]) { row[b] = defaults[b]; continue; }
                    V3 t0, s0;
                    Q4 q0;
                    defaults[b].Decompose(out t0, out q0, out s0);
                    V3 t = tt[b] != null ? V3.Of(tt[b][f], PlayerSkeleton.RigToMeters) : t0;
                    Q4 q = tq[b] != null ? Q4.Of(tq[b][f]).Normalized() : q0;
                    V3 sc = ts[b] != null ? V3.Of(ts[b][f]) : s0;
                    row[b] = Affine.FromTRS(t, q, sc);
                }
                a.Local[f] = row;
            }
            for (int b = 0; b < nb; b++)
            {
                if (a.Driven[b]) { a.Posed[b] = true; continue; }
                a.Posed[b] = defaults[b].MaxDiff(s.Local[b]) > 1e-5;
            }
            return a;
        }

        /// <summary>the rig whose skeleton shares the most joint names with sk (for the FIFA 17 player:
        /// player_rig_17_rendering, all 438)</summary>
        public static GdObj BestMatchingRig(RigIndex ri, PlayerSkeleton sk)
        {
            GdObj best = null;
            int bestN = 0;
            foreach (ulong k in ri.RigOrder)
            {
                GdObj r = ri.Rigs[k];
                RigIndex.Skeleton rs = ri.GetSkeleton(r);
                if (rs == null) continue;
                int m = rs.Joints.Count(j => sk.Find(j.Name) >= 0);
                if (m > bestN) { bestN = m; best = r; }
            }
            return best;
        }
    }

    internal static class SkeletonOrderCache
    {
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<PlayerSkeleton, int[]> cache =
            new System.Runtime.CompilerServices.ConditionalWeakTable<PlayerSkeleton, int[]>();

        public static int[] TopoOrderCached(this PlayerSkeleton s)
        {
            int[] o;
            if (cache.TryGetValue(s, out o) && o.Length == s.Count) return o;
            o = s.TopoOrder();
            cache.Remove(s);
            cache.Add(s, o);
            return o;
        }
    }
}
