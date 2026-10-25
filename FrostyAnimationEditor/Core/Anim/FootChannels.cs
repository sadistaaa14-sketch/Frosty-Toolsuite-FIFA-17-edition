using System;
using System.Collections.Generic;
using System.Linq;

namespace FrostyAnimationEditor.Core.Anim
{
    /// <summary>
    /// Channels of the player clips that are derived from the motion: the foot IK targets and the foot contact signals.
    /// The 37- and 78-channel player layouts (93% of the clips) carry them. Checked against 1,147 shipped clips at their
    /// VBR keys (where the decoder returns stored values; all in rig units, feet):
    ///  * Left/RightAnkleEffectorAux.t (joint parented to AITrajectory) = the Left/RightFoot joint's position in
    ///    AITrajectory space: largest difference per clip 0.024 ft median, 0.087 ft at the 99th percentile (mean
    ///    difference 0.005 ft median);
    ///  * &lt;joint&gt;.height = the joint's height (world Y): largest difference per clip 0.006 ft median, 0.070 ft at the
    ///    99th percentile; three tackle / stop clips store something else in ToeBase.height (6-15 ft off);
    ///  * &lt;joint&gt;.vel = the distance the joint moves to the next frame: mean difference per clip 0.019 median (the best
    ///    of six definitions tried: forward, backward, central, trajectory space, ground plane, fps-scaled); single keys
    ///    of short gameplay clips differ by up to a few feet (not known why).
    /// When new motion replaces a clip's joints these must follow it: the game's leg IK pulls the ankles toward the
    /// effector targets, so the targets of another motion bend the legs.
    /// </summary>
    public static class FootChannels
    {
        public static bool IsDerived(string channel)
        {
            if (channel == null) return false;
            return channel.EndsWith("AnkleEffectorAux.t", StringComparison.Ordinal) || channel.EndsWith(".height", StringComparison.Ordinal) ||
                   channel.EndsWith(".vel", StringComparison.Ordinal);
        }

        /// <summary>
        /// recompute the derived channels of cd from its joint channels; defaults: the local transform (meters) of every
        /// skeleton bone a channel does not drive. Returns the names of the rebuilt channels.
        /// </summary>
        public static List<string> Rebuild(ClipData cd, IList<Channel> channels, PlayerSkeleton sk, IList<Affine> defaults)
        {
            List<string> done = new List<string>();
            int nf = cd.NKeys, nb = sk.Count;
            if (nf == 0 || nb == 0) return done;
            List<int> derived = new List<int>();
            for (int i = 0; i < channels.Count; i++) if (IsDerived(channels[i].Name)) derived.Add(i);
            if (derived.Count == 0) return done;

            // channel indices per bone
            int[] qi = Enumerable.Repeat(-1, nb).ToArray(), ti = Enumerable.Repeat(-1, nb).ToArray(), si = Enumerable.Repeat(-1, nb).ToArray();
            for (int i = 0; i < channels.Count; i++)
            {
                Channel ch = channels[i];
                if (ch.Kind == "float") continue;
                int dot = ch.Name.LastIndexOf('.');
                if (dot <= 0) continue;
                int b = sk.Find(ch.Name.Substring(0, dot));
                if (b < 0) continue;
                string comp = ch.Name.Substring(dot + 1);
                if (comp == "q" && ch.Kind == "quat") qi[b] = i;
                else if (comp == "t" && ch.Kind == "vec3") ti[b] = i;
                else if (comp == "s" && ch.Kind == "vec3") si[b] = i;
            }
            double k = 1.0 / PlayerSkeleton.RigToMeters;
            V3[] dt = new V3[nb], ds = new V3[nb];
            Q4[] dq = new Q4[nb];
            for (int b = 0; b < nb; b++)
            {
                V3 t, s;
                Q4 q;
                (b < defaults.Count ? defaults[b] : sk.Local[b]).Decompose(out t, out q, out s);
                dt[b] = t * k; dq[b] = q; ds[b] = s;
            }
            int[] order = sk.TopoOrder();
            // world positions (rig units) of every bone, and the AITrajectory transform, per frame
            V3[][] pos = new V3[nf][];
            Affine[] traj = new Affine[nf];
            int trajBone = sk.Find("AITrajectory");
            Affine[] w = new Affine[nb];
            for (int f = 0; f < nf; f++)
            {
                foreach (int b in order)
                {
                    V3 t = ti[b] >= 0 ? V(cd.Values[ti[b]][f]) : dt[b];
                    Q4 q = qi[b] >= 0 ? Q(cd.Values[qi[b]][f]) : dq[b];
                    V3 s = si[b] >= 0 ? V(cd.Values[si[b]][f]) : ds[b];
                    Affine l = Affine.FromTRS(t, q, s);
                    int p = sk.Parents[b];
                    w[b] = p >= 0 ? w[p] * l : l;
                }
                pos[f] = new V3[nb];
                for (int b = 0; b < nb; b++) pos[f][b] = w[b].Translation;
                traj[f] = trajBone >= 0 ? w[trajBone] : Affine.Identity;
            }
            foreach (int i in derived)
            {
                string name = channels[i].Name;
                double[][] v = cd.Values[i];
                if (name.EndsWith("AnkleEffectorAux.t", StringComparison.Ordinal) && channels[i].Kind == "vec3")
                {
                    string side = name.Substring(0, name.Length - "AnkleEffectorAux.t".Length);
                    int foot = sk.Find(side + "Foot");
                    if (foot < 0 || trajBone < 0) continue;
                    for (int f = 0; f < nf; f++)
                    {
                        V3 l = traj[f].Inverse().Apply(pos[f][foot]);
                        v[f] = new[] { l.X, l.Y, l.Z };
                    }
                    done.Add(name);
                }
                else if (channels[i].Kind == "float")
                {
                    bool height = name.EndsWith(".height", StringComparison.Ordinal);
                    int b = sk.Find(name.Substring(0, name.LastIndexOf('.')));
                    if (b < 0) continue;
                    for (int f = 0; f < nf; f++)
                    {
                        double x;
                        if (height) x = pos[f][b].Y;
                        else if (nf == 1) x = 0;
                        else if (f + 1 < nf) x = (pos[f + 1][b] - pos[f][b]).Length;
                        else x = (pos[f][b] - pos[f - 1][b]).Length;
                        v[f] = new[] { x };
                    }
                    done.Add(name);
                }
            }
            return done;
        }

        private static V3 V(double[] a) { return new V3(a[0], a[1], a[2]); }
        private static Q4 Q(double[] a) { return new Q4(a[0], a[1], a[2], a[3]).Normalized(); }
    }
}
