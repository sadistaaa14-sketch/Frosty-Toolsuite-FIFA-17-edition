using System;
using System.Collections.Generic;
using System.Linq;
using FrostyAnimationEditor.Core.Anim;

namespace FrostyAnimationEditor.Core.Fbx
{
    /// <summary>bone animation read from an FBX file, as global transforms in the editor's frame (meters, Y up,
    /// right-handed, X coord, Z front)</summary>
    public sealed class FbxAnimationData
    {
        public string Take = "";
        public List<string> Takes = new List<string>();
        public double Fps;
        public int FrameCount;
        /// <summary>per bone and frame: rotation and position (meters) of the bone, without scale</summary>
        public Dictionary<string, Affine[]> Globals = new Dictionary<string, Affine[]>(StringComparer.Ordinal);
        /// <summary>per bone and frame: the accumulated scale that was taken out of Globals</summary>
        public Dictionary<string, V3[]> Scales = new Dictionary<string, V3[]>(StringComparer.Ordinal);
        public List<string> Notes = new List<string>();
    }

    public static class FbxImport
    {
        /// <summary>name without namespace or path ("mixamorig:Hips", "Armature|Hips" -> "Hips")</summary>
        public static string BareName(string n)
        {
            if (n == null) return "";
            int k = Math.Max(n.LastIndexOf(':'), n.LastIndexOf('|'));
            return k >= 0 ? n.Substring(k + 1) : n;
        }

        /// <summary>
        /// Sample the animation of the models named like wanted (exact name first, then without namespace) at fps from
        /// the take's start to its end. takeName null: the first animation stack.
        /// </summary>
        public static FbxAnimationData ReadAnimation(byte[] file, double fps, ICollection<string> wanted, string takeName = null)
        {
            FbxScene s = FbxScene.Load(file);
            FbxAnimationData r = new FbxAnimationData();
            r.Takes = s.StackNames();
            FbxScene.Take take = s.ReadTake(takeName);
            r.Take = take.Name;
            if (fps <= 0) fps = s.FileFps > 0 ? s.FileFps : 30;
            r.Fps = fps;
            Dictionary<string, FbxObject> byName = new Dictionary<string, FbxObject>(StringComparer.Ordinal);
            Dictionary<string, FbxObject> byBare = new Dictionary<string, FbxObject>(StringComparer.Ordinal);
            foreach (FbxObject m in s.Models)
            {
                if (!byName.ContainsKey(m.Name)) byName[m.Name] = m;
                string b = BareName(m.Name);
                if (!byBare.ContainsKey(b)) byBare[b] = m;
            }
            Dictionary<string, FbxObject> pick = new Dictionary<string, FbxObject>(StringComparer.Ordinal);
            foreach (string w in wanted)
            {
                FbxObject m;
                if (byName.TryGetValue(w, out m) || byBare.TryGetValue(w, out m)) pick[w] = m;
            }
            if (pick.Count == 0) throw new FormatException("the FBX file has no node named like a bone of the skeleton (it has " +
                                                           string.Join(", ", s.Models.Take(8).Select(m => m.Name)) + (s.Models.Count() > 8 ? ", …" : "") + ")");
            long span = take.Stop - take.Start;
            int nf = span > 0 ? (int)Math.Floor(span * fps / FbxScene.KTimePerSecond + 1e-6) + 1 : 1;
            if (nf > 100000) throw new FormatException("the take is too long (" + nf + " frames)");
            r.FrameCount = nf;
            double u = s.MetersPerUnit;
            Affine C = Affine.Scale(u) * s.AxisToYUp();
            foreach (string w in pick.Keys) { r.Globals[w] = new Affine[nf]; r.Scales[w] = new V3[nf]; }
            List<FbxObject> models = pick.Values.Distinct().ToList();
            for (int f = 0; f < nf; f++)
            {
                long t = take.Start + (long)Math.Round(f * FbxScene.KTimePerSecond / fps);
                Dictionary<long, Affine> g = s.Globals(models, take, t);
                foreach (KeyValuePair<string, FbxObject> kv in pick)
                {
                    // into the editor's frame: the conversion changes world coordinates only (bone axes are kept, as
                    // an FBX SDK axis conversion does on the root nodes); scale (units, or a unit conversion on a
                    // parent node such as Blender's armature at 100) is taken out so that positions stay in meters
                    Affine w = C * g[kv.Value.Id];
                    V3 pt, ps;
                    Q4 pr;
                    w.Decompose(out pt, out pr, out ps);
                    r.Globals[kv.Key][f] = Affine.FromTRS(pt, pr, V3.One);
                    r.Scales[kv.Key][f] = ps;
                }
            }
            r.Notes.Add("take \"" + take.Name + "\": " + nf + " frames at " + fps.ToString("0.###") + " fps; " + pick.Count + " of " + wanted.Count + " bones found" +
                        (Math.Abs(u - 1) > 1e-9 ? "; units: " + u.ToString("0.#####") + " m" : ""));
            List<string> missing = wanted.Where(w => !pick.ContainsKey(w)).ToList();
            if (missing.Count > 0 && missing.Count <= 12) r.Notes.Add("not in the file: " + string.Join(", ", missing));
            else if (missing.Count > 12) r.Notes.Add(missing.Count + " bones are not in the file (e.g. " + string.Join(", ", missing.Take(6)) + ")");
            return r;
        }

        /// <summary>scale above the skeleton's root (the smallest accumulated scale of any bone at the frame, per axis): a
        /// uniform unit scale on a parent node, which is not a bone scale</summary>
        private static V3 RootScale(FbxAnimationData fa, int f)
        {
            double x = double.MaxValue, y = double.MaxValue, z = double.MaxValue;
            foreach (V3[] v in fa.Scales.Values) { x = Math.Min(x, v[f].X); y = Math.Min(y, v[f].Y); z = Math.Min(z, v[f].Z); }
            return x == double.MaxValue ? V3.One : new V3(x, y, z);
        }

        /// <summary>
        /// The FBX animation as values for a clip's channels (one key per frame). Joints the file has: local transforms
        /// relative to the parent bone of the skeleton (translations in rig units, meters * 3.281). Joints it does not
        /// have and float channels keep the clip's current values (the last frame is held when the new clip is longer).
        /// </summary>
        public static ClipData ToClipData(FbxAnimationData fa, PlayerSkeleton sk, AnimClip clip, out List<string> report)
        {
            report = new List<string>(fa.Notes);
            int nf = fa.FrameCount, nb = sk.Count;
            int[] order = sk.TopoOrder();
            Affine[][] local = new Affine[nf][];
            for (int f = 0; f < nf; f++)
            {
                Affine[] w = new Affine[nb];
                Affine[] l = new Affine[nb];
                V3[] sc = new V3[nb];
                foreach (int b in order)
                {
                    int p = sk.Parents[b];
                    Affine[] g;
                    V3[] gs;
                    if (fa.Globals.TryGetValue(sk.Bones[b], out g)) { w[b] = g[f]; sc[b] = fa.Scales.TryGetValue(sk.Bones[b], out gs) ? gs[f] : V3.One; }
                    else { w[b] = p >= 0 ? w[p] * sk.Local[b] : sk.Local[b]; sc[b] = p >= 0 ? sc[p] : V3.One; }
                    l[b] = p >= 0 ? w[p].Inverse() * w[b] : w[b];
                    // local scale: ratio to the parent's accumulated scale
                    V3 ps = p >= 0 ? sc[p] : (fa.Scales.Count > 0 ? RootScale(fa, f) : V3.One);
                    V3 ls = new V3(ps.X != 0 ? sc[b].X / ps.X : 1, ps.Y != 0 ? sc[b].Y / ps.Y : 1, ps.Z != 0 ? sc[b].Z / ps.Z : 1);
                    if (Math.Abs(ls.X - 1) > 1e-6 || Math.Abs(ls.Y - 1) > 1e-6 || Math.Abs(ls.Z - 1) > 1e-6) l[b] = l[b] * Affine.Scale(ls);
                }
                local[f] = l;
            }
            List<float[][]> old = clip.Sample(Enumerable.Range(0, clip.FrameCount()).ToList());
            List<string> kinds = new List<string>();
            List<double[][]> vals = new List<double[][]>();
            int fromFile = 0, kept = 0;
            double maxScaleDev = 0;
            for (int ci = 0; ci < clip.Channels.Count; ci++)
            {
                Channel ch = clip.Channels[ci];
                int dot = ch.Name.LastIndexOf('.');
                string jn = dot >= 0 ? ch.Name.Substring(0, dot) : ch.Name, comp = dot >= 0 ? ch.Name.Substring(dot + 1) : "";
                int b = sk.Find(jn);
                double[][] v = new double[nf][];
                bool have = b >= 0 && fa.Globals.ContainsKey(jn) && ch.Kind != "float" && (comp == "q" || comp == "t" || comp == "s");
                if (!have)
                {
                    float[][] fb = old[ci];
                    for (int f = 0; f < nf; f++) v[f] = fb[Math.Min(f, fb.Length - 1)].Select(x => (double)x).ToArray();
                    kept++;
                }
                else
                {
                    for (int f = 0; f < nf; f++)
                    {
                        V3 t, s;
                        Q4 q;
                        local[f][b].Decompose(out t, out q, out s);
                        if (comp == "q") v[f] = new[] { q.X, q.Y, q.Z, q.W };
                        else if (comp == "t") v[f] = new[] { t.X / PlayerSkeleton.RigToMeters, t.Y / PlayerSkeleton.RigToMeters, t.Z / PlayerSkeleton.RigToMeters };
                        else v[f] = new[] { s.X, s.Y, s.Z };
                        if (comp != "s") maxScaleDev = Math.Max(maxScaleDev, Math.Max(Math.Abs(s.X - 1), Math.Max(Math.Abs(s.Y - 1), Math.Abs(s.Z - 1))));
                    }
                    if (comp == "q")
                    {
                        // hemisphere: follow the clip's first key, then stay continuous
                        float[] o0 = old[ci].Length > 0 ? old[ci][0] : null;
                        if (o0 != null && v[0][0] * o0[0] + v[0][1] * o0[1] + v[0][2] * o0[2] + v[0][3] * o0[3] < 0) for (int c = 0; c < 4; c++) v[0][c] = -v[0][c];
                        for (int f = 1; f < nf; f++)
                        {
                            double d = 0;
                            for (int c = 0; c < 4; c++) d += v[f][c] * v[f - 1][c];
                            if (d < 0) for (int c = 0; c < 4; c++) v[f][c] = -v[f][c];
                        }
                    }
                    fromFile++;
                }
                kinds.Add(ch.Kind);
                vals.Add(v);
            }
            report.Add(fromFile + " channels from the file, " + kept + " kept from the clip");
            if (maxScaleDev > 1e-3) report.Add("bones are scaled in the file (up to " + (maxScaleDev * 100).ToString("0.#") + "%): the clip's scale channels, if any, carry it; other joints lose it");
            return new ClipData(Enumerable.Range(0, nf).Select(x => (long)x).ToList(), kinds, vals, clip.Loops, nf - 1);
        }
    }
}
