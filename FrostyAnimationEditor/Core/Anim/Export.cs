using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace FrostyAnimationEditor.Core.Anim
{
    /// <summary>
    /// The .antanim.json interchange format (read by the Blender add-on io_fifa17_antanim.py):
    /// {"format": "fifa17-antanim", "version": 1,
    ///  "clip": {"key", "name", "codec", "end_frame", "fps", "loop", "additive", "channel_map", "dof_set", "controllers"},
    ///  "rig": {"rig", "rig_key", "skeleton", "traj_joint", "units_to_meters": 0.3048,
    ///          "joints": [{"name", "parent", "type", "t": [x,y,z], "q": [x,y,z,w], "s": [1,1,1]}]},
    ///  "channels": [{"dof", "name", "kind"}],   (rig channel order)
    ///  "key_frames": [...], "keys": [[per channel values], ...]}
    /// Values: quat [x,y,z,w], vec3 [x,y,z], float [v].  Joint transforms are local (parent-relative), game units.
    /// </summary>
    public static class AntAnim
    {
        public const string Format = "fifa17-antanim";

        private static List<object> FL(IEnumerable<float> v) { return v.Select(x => (object)(double)x).ToList(); }

        public static JObject ExportClip(AntDb db, ulong key, RigIndex ri = null, bool resample = false)
        {
            if (ri == null) ri = new RigIndex(db);
            AnimClip c;
            ClipData cd = AnimOps.Data(db, db.Get(key), ri, out c);
            double fps = 30.0;
            List<GdObj> ctrls = AnimOps.FindControllers(db, c.Key);
            if (ctrls.Count > 0) { double f = ctrls[0].GetFloat("FPS"); fps = f != 0 ? f : 30.0; }
            GdObj rig = ri.BestRig(c.LayoutKey, c.DofIds);
            JObject rigInfo = null;
            if (rig != null)
            {
                RigIndex.Skeleton sk = ri.GetSkeleton(rig);
                Dictionary<string, Dictionary<string, float[]>> bind = ri.BindPose(rig);
                if (sk != null)
                {
                    List<object> joints = new List<object>();
                    foreach (RigIndex.Joint j in sk.Joints)
                    {
                        Dictionary<string, float[]> b;
                        bind.TryGetValue(j.Name, out b);
                        float[] t = b != null && b.ContainsKey("t") ? b["t"] : new[] { 0f, 0f, 0f };
                        float[] q = b != null && b.ContainsKey("q") ? b["q"] : new[] { 0f, 0f, 0f, 1f };
                        float[] s = b != null && b.ContainsKey("s") ? b["s"] : new[] { 1f, 1f, 1f };
                        JObject jo = new JObject();
                        jo["name"] = j.Name;
                        jo["parent"] = (long)j.Parent;
                        jo["type"] = j.Type;
                        jo["t"] = FL(t.Take(3));
                        jo["q"] = FL(q.Take(4));
                        jo["s"] = FL(s.Take(3));
                        joints.Add(jo);
                    }
                    rigInfo = new JObject();
                    rigInfo["rig"] = rig.Name;
                    rigInfo["rig_key"] = BE.KeyHex(rig.Key);
                    rigInfo["skeleton"] = sk.Name;
                    rigInfo["traj_joint"] = sk.TrajJoint;
                    rigInfo["units_to_meters"] = 0.3048;
                    rigInfo["joints"] = joints;
                }
            }
            List<long> keyFrames;
            List<double[][]> vals;
            if (resample)
            {
                List<int> frames = Enumerable.Range(0, c.FrameCount()).ToList();
                vals = c.Sample(frames).Select(ClipData.ToDouble).ToList();
                keyFrames = frames.Select(x => (long)x).ToList();
            }
            else
            {
                keyFrames = cd.KeyFrames;
                vals = cd.Values;
            }
            List<object> keys = new List<object>();
            for (int k = 0; k < keyFrames.Count; k++)
            {
                List<object> row = new List<object>();
                foreach (double[][] v in vals) row.Add(v[k].Select(x => (object)x).ToList());
                keys.Add(row);
            }
            JObject clip = new JObject();
            clip["key"] = BE.KeyHex(c.Key);
            clip["name"] = c.Name;
            clip["codec"] = c.Codec;
            clip["end_frame"] = c.EndFrame;
            clip["fps"] = fps;
            clip["loop"] = cd.Loop;
            clip["additive"] = c.Additive;
            clip["channel_map"] = BE.KeyHex(c.C2d != null ? c.C2d.Key : 0);
            clip["dof_set"] = BE.KeyHex(c.LayoutKey);
            clip["controllers"] = ctrls.Select(o => (object)BE.KeyHex(o.Key)).ToList();
            JObject doc = new JObject();
            doc["format"] = Format;
            doc["version"] = 1L;
            doc["clip"] = clip;
            doc["rig"] = rigInfo;
            doc["channels"] = c.Channels.Select(ch =>
            {
                JObject j = new JObject();
                j["dof"] = ch.Dof.HasValue ? (object)ch.Dof.Value : null;
                j["name"] = ch.Name;
                j["kind"] = ch.Kind;
                return (object)j;
            }).ToList();
            doc["key_frames"] = keyFrames.Select(x => (object)x).ToList();
            doc["keys"] = keys;
            return doc;
        }

        public static void Save(JObject doc, string path) { File.WriteAllText(path, MiniJson.Serialize(doc, 0), new UTF8Encoding(false)); }

        public static JObject Load(string path)
        {
            JObject doc = MiniJson.ParseFile(path) as JObject;
            if (doc == null || doc.Str("format") != Format) throw new FormatException("not a " + Format + " file");
            return doc;
        }

        /// <summary>-> ClipData in the doc's channel order (or re-ordered to channelOrder = names)</summary>
        public static ClipData ToClipData(JObject doc, IList<string> channelOrder = null)
        {
            List<object> chans = doc.Arr("channels");
            List<string> names = chans.Select(c => ((JObject)c).Str("name")).ToList();
            List<string> kinds = chans.Select(c => ((JObject)c).Str("kind")).ToList();
            List<long> kf = doc.Arr("key_frames").Select(x => Num.ToLong(x)).ToList();
            int K = kf.Count;
            List<double[][]> vals = new List<double[][]>();
            for (int j = 0; j < chans.Count; j++) vals.Add(new double[K][]);
            List<object> rows = doc.Arr("keys");
            for (int k = 0; k < K; k++)
            {
                List<object> row = (List<object>)rows[k];
                for (int j = 0; j < chans.Count; j++)
                {
                    object cell = row[j];
                    IList l = cell as IList;
                    vals[j][k] = l != null ? l.Cast<object>().Select(Num.ToDouble).ToArray() : new[] { Num.ToDouble(cell) };
                }
            }
            if (channelOrder != null)
            {
                Dictionary<string, int> idx = new Dictionary<string, int>();
                for (int j = 0; j < names.Count; j++) if (!idx.ContainsKey(names[j])) idx[names[j]] = j;
                List<string> missing = channelOrder.Where(n => !idx.ContainsKey(n)).ToList();
                if (missing.Count > 0) throw new FormatException("channels missing from the antanim file: " + string.Join(", ", missing.Take(10)));
                vals = channelOrder.Select(n => vals[idx[n]]).ToList();
                kinds = channelOrder.Select(n => kinds[idx[n]]).ToList();
            }
            JObject c0 = doc.Obj("clip");
            bool loop = c0 != null && c0.Bool("loop");
            long end = c0 != null && c0["end_frame"] != null ? c0.Long("end_frame") : (K > 0 ? kf[K - 1] : 0);
            return new ClipData(kf, kinds, vals, loop, end);
        }
    }

    /// <summary>
    /// BVH export (any clip -> .bvh for Blender/MotionBuilder) and BVH import.  The skeleton is the clip's rig
    /// skeleton; OFFSET = the joint's bind translation; every joint gets ZXY Euler rotation channels (degrees) and
    /// joints with an animated translation also get position channels.  The BVH rest pose is all-zero rotations,
    /// so the animation is exact while the rest pose BVH tools show is not the game's bind pose (use the
    /// .antanim.json + Blender add-on for a bind-posed armature).
    /// </summary>
    public static class Bvh
    {
        public static double[,] QuatToMat(double x, double y, double z, double w)
        {
            return new double[,]
            {
                { 1 - 2 * (y * y + z * z), 2 * (x * y - z * w), 2 * (x * z + y * w) },
                { 2 * (x * y + z * w), 1 - 2 * (x * x + z * z), 2 * (y * z - x * w) },
                { 2 * (x * z - y * w), 2 * (y * z + x * w), 1 - 2 * (x * x + y * y) },
            };
        }

        public static double[] MatToQuat(double[,] m)
        {
            double t = m[0, 0] + m[1, 1] + m[2, 2];
            double x, y, z, w, s;
            if (t > 0)
            {
                s = Math.Sqrt(t + 1.0) * 2;
                w = 0.25 * s; x = (m[2, 1] - m[1, 2]) / s; y = (m[0, 2] - m[2, 0]) / s; z = (m[1, 0] - m[0, 1]) / s;
            }
            else if (m[0, 0] > m[1, 1] && m[0, 0] > m[2, 2])
            {
                s = Math.Sqrt(1.0 + m[0, 0] - m[1, 1] - m[2, 2]) * 2;
                w = (m[2, 1] - m[1, 2]) / s; x = 0.25 * s; y = (m[0, 1] + m[1, 0]) / s; z = (m[0, 2] + m[2, 0]) / s;
            }
            else if (m[1, 1] > m[2, 2])
            {
                s = Math.Sqrt(1.0 + m[1, 1] - m[0, 0] - m[2, 2]) * 2;
                w = (m[0, 2] - m[2, 0]) / s; x = (m[0, 1] + m[1, 0]) / s; y = 0.25 * s; z = (m[1, 2] + m[2, 1]) / s;
            }
            else
            {
                s = Math.Sqrt(1.0 + m[2, 2] - m[0, 0] - m[1, 1]) * 2;
                w = (m[1, 0] - m[0, 1]) / s; x = (m[0, 2] + m[2, 0]) / s; y = (m[1, 2] + m[2, 1]) / s; z = 0.25 * s;
            }
            double n = Math.Sqrt(x * x + y * y + z * z + w * w);
            return new[] { x / n, y / n, z / n, w / n };
        }

        private const double Deg = 180.0 / Math.PI;

        /// <summary>R = Rz * Rx * Ry -> (z, x, y) degrees</summary>
        public static double[] MatToEulerZxy(double[,] m)
        {
            double x = Math.Asin(Math.Max(-1.0, Math.Min(1.0, m[2, 1])));
            double z, y;
            if (Math.Abs(m[2, 1]) < 0.9999999)
            {
                z = Math.Atan2(-m[0, 1], m[1, 1]);
                y = Math.Atan2(-m[2, 0], m[2, 2]);
            }
            else
            {
                z = Math.Atan2(m[1, 0], m[0, 0]);
                y = 0.0;
            }
            return new[] { z * Deg, x * Deg, y * Deg };
        }

        public static double[,] EulerZxyToMat(double zd, double xd, double yd)
        {
            double z = zd / Deg, x = xd / Deg, y = yd / Deg;
            double cz = Math.Cos(z), sz = Math.Sin(z), cx = Math.Cos(x), sx = Math.Sin(x), cy = Math.Cos(y), sy = Math.Sin(y);
            double[,] Rz = { { cz, -sz, 0 }, { sz, cz, 0 }, { 0, 0, 1 } };
            double[,] Rx = { { 1, 0, 0 }, { 0, cx, -sx }, { 0, sx, cx } };
            double[,] Ry = { { cy, 0, sy }, { 0, 1, 0 }, { -sy, 0, cy } };
            return Mul(Mul(Rz, Rx), Ry);
        }

        public static double[,] Mul(double[,] a, double[,] b)
        {
            double[,] r = new double[3, 3];
            for (int i = 0; i < 3; i++)
                for (int j = 0; j < 3; j++)
                {
                    double s = 0;
                    for (int k = 0; k < 3; k++) s += a[i, k] * b[k, j];
                    r[i, j] = s;
                }
            return r;
        }

        /// <summary>Python "%.6f" (the sign is kept for values that round to zero, on every .NET version)</summary>
        private static string F6(double v)
        {
            string s = Math.Abs(v).ToString("F6", CultureInfo.InvariantCulture);
            return v < 0 || (v == 0 && BitConverter.DoubleToInt64Bits(v) < 0) ? "-" + s : s;
        }

        /// <summary>quat_to_mat on float32 components (numpy float32 scalars: every operation rounds to float)</summary>
        public static double[,] QuatToMatF(float x, float y, float z, float w)
        {
            float m00 = (float)(1f - (float)(2f * (float)((float)(y * y) + (float)(z * z))));
            float m01 = (float)(2f * (float)((float)(x * y) - (float)(z * w)));
            float m02 = (float)(2f * (float)((float)(x * z) + (float)(y * w)));
            float m10 = (float)(2f * (float)((float)(x * y) + (float)(z * w)));
            float m11 = (float)(1f - (float)(2f * (float)((float)(x * x) + (float)(z * z))));
            float m12 = (float)(2f * (float)((float)(y * z) - (float)(x * w)));
            float m20 = (float)(2f * (float)((float)(x * z) - (float)(y * w)));
            float m21 = (float)(2f * (float)((float)(y * z) + (float)(x * w)));
            float m22 = (float)(1f - (float)(2f * (float)((float)(x * x) + (float)(y * y))));
            return new double[,] { { m00, m01, m02 }, { m10, m11, m12 }, { m20, m21, m22 } };
        }

        public static string Export(AntDb db, ulong key, RigIndex ri = null, double scale = 1.0, double? fps = null)
        {
            if (ri == null) ri = new RigIndex(db);
            AnimClip c = AnimClip.Load(db, key, ri);
            GdObj rig = ri.BestRig(c.LayoutKey, c.DofIds);
            if (rig == null) throw new GdException("no rig/skeleton found for this clip");
            RigIndex.Skeleton sk = ri.GetSkeleton(rig);
            if (sk == null) throw new GdException("the clip's rig has no skeleton");
            Dictionary<string, Dictionary<string, float[]>> bind = ri.BindPose(rig);
            List<RigIndex.Joint> joints = sk.Joints;
            List<int> frames = Enumerable.Range(0, c.FrameCount()).ToList();
            List<float[][]> vals = c.Sample(frames);
            Dictionary<string, float[][]> track = new Dictionary<string, float[][]>();
            for (int i = 0; i < c.Channels.Count; i++) track[c.Channels[i].Name] = vals[i];
            if (!fps.HasValue)
            {
                List<GdObj> ct = AnimOps.FindControllers(db, c.Key);
                fps = ct.Count > 0 && ct[0].GetFloat("FPS") != 0 ? ct[0].GetFloat("FPS") : 30.0;
            }
            Dictionary<int, List<int>> children = new Dictionary<int, List<int>>();
            List<int> roots = new List<int>();
            for (int i = 0; i < joints.Count; i++) children[i] = new List<int>();
            for (int i = 0; i < joints.Count; i++)
            {
                if (joints[i].Parent >= 0 && children.ContainsKey(joints[i].Parent)) children[joints[i].Parent].Add(i);
                else roots.Add(i);
            }
            bool[] hasPos = joints.Select(j => track.ContainsKey(j.Name + ".t")).ToArray();
            List<Tuple<int, bool>> order = new List<Tuple<int, bool>>();
            StringBuilder sb = new StringBuilder();
            sb.Append("HIERARCHY\n");
            Func<string, string, float[]> bindOf = (nm, comp) =>
            {
                Dictionary<string, float[]> b;
                if (bind.TryGetValue(nm, out b) && b.ContainsKey(comp)) return b[comp];
                return comp == "q" ? new[] { 0f, 0f, 0f, 1f } : (comp == "s" ? new[] { 1f, 1f, 1f } : new[] { 0f, 0f, 0f });
            };
            Action<int, int> emit = null;
            emit = (i, depth) =>
            {
                string nm = joints[i].Name;
                string ind = new string(' ', 2 * depth);
                float[] t = bindOf(nm, "t");
                sb.Append(ind).Append(depth == 0 ? "ROOT " : "JOINT ").Append(nm).Append('\n');
                sb.Append(ind).Append("{\n");
                sb.Append(ind).Append("  OFFSET ").Append(F6(t[0] * scale)).Append(' ').Append(F6(t[1] * scale)).Append(' ').Append(F6(t[2] * scale)).Append('\n');
                bool pos = hasPos[i] || depth == 0;
                sb.Append(ind).Append(pos ? "  CHANNELS 6 Xposition Yposition Zposition Zrotation Xrotation Yrotation\n" : "  CHANNELS 3 Zrotation Xrotation Yrotation\n");
                order.Add(Tuple.Create(i, pos));
                if (children[i].Count > 0) foreach (int ch in children[i]) emit(ch, depth + 1);
                else
                {
                    sb.Append(ind).Append("  End Site\n");
                    sb.Append(ind).Append("  {\n");
                    sb.Append(ind).Append("    OFFSET 0.0 0.0 0.0\n");
                    sb.Append(ind).Append("  }\n");
                }
                sb.Append(ind).Append("}\n");
            };
            foreach (int r in roots) emit(r, 0);
            sb.Append("MOTION\n");
            sb.Append("Frames: ").Append(frames.Count).Append('\n');
            sb.Append("Frame Time: ").Append((1.0 / fps.Value).ToString("F8", CultureInfo.InvariantCulture)).Append('\n');
            for (int fi = 0; fi < frames.Count; fi++)
            {
                List<string> row = new List<string>();
                foreach (Tuple<int, bool> op in order)
                {
                    string nm = joints[op.Item1].Name;
                    if (op.Item2)
                    {
                        float[][] tv;
                        if (track.TryGetValue(nm + ".t", out tv))
                        {
                            float[] t = tv[fi];      // float32 track values stay float32 (numpy)
                            row.Add(F6((float)(t[0] * scale))); row.Add(F6((float)(t[1] * scale))); row.Add(F6((float)(t[2] * scale)));
                        }
                        else
                        {
                            float[] t = bindOf(nm, "t");
                            row.Add(F6(t[0] * scale)); row.Add(F6(t[1] * scale)); row.Add(F6(t[2] * scale));
                        }
                    }
                    float[][] qv;
                    double[] e;
                    if (track.TryGetValue(nm + ".q", out qv))
                    {
                        float[] q = qv[fi];
                        e = MatToEulerZxy(QuatToMatF(q[0], q[1], q[2], q[3]));
                    }
                    else
                    {
                        float[] q = bindOf(nm, "q");
                        e = MatToEulerZxy(QuatToMat(q[0], q[1], q[2], q[3]));
                    }
                    row.Add(F6(e[0])); row.Add(F6(e[1])); row.Add(F6(e[2]));
                }
                sb.Append(string.Join(" ", row)).Append('\n');
            }
            return sb.ToString();
        }

        public sealed class BvhJoint
        {
            public string Name;
            public int Parent;
            public double[] Offset;
            public List<string> Channels = new List<string>();
        }

        public static void Read(string text, out List<BvhJoint> joints, out double frameTime, out List<double[]> frames)
        {
            string[] tok = text.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            joints = new List<BvhJoint>();
            List<int> stack = new List<int>();
            int i = 0;
            while (i < tok.Length)
            {
                string t = tok[i];
                if (t == "ROOT" || t == "JOINT")
                {
                    joints.Add(new BvhJoint { Name = tok[i + 1], Parent = stack.Count > 0 ? stack[stack.Count - 1] : -1 });
                    stack.Add(joints.Count - 1);
                    i += 2;
                }
                else if (t == "End")
                {
                    int depth = 0;
                    i += 2;
                    while (true)
                    {
                        if (tok[i] == "{") depth++;
                        else if (tok[i] == "}") { depth--; if (depth == 0) { i++; break; } }
                        i++;
                    }
                }
                else if (t == "OFFSET")
                {
                    joints[stack[stack.Count - 1]].Offset = new[] { P(tok[i + 1]), P(tok[i + 2]), P(tok[i + 3]) };
                    i += 4;
                }
                else if (t == "CHANNELS")
                {
                    int n = int.Parse(tok[i + 1], CultureInfo.InvariantCulture);
                    joints[stack[stack.Count - 1]].Channels = tok.Skip(i + 2).Take(n).ToList();
                    i += 2 + n;
                }
                else if (t == "}") { stack.RemoveAt(stack.Count - 1); i++; }
                else if (t == "MOTION")
                {
                    int nf = int.Parse(tok[i + 2], CultureInfo.InvariantCulture);
                    frameTime = P(tok[i + 5]);
                    double[] vals = tok.Skip(i + 6).Select(P).ToArray();
                    int per = joints.Sum(j => j.Channels.Count);
                    frames = new List<double[]>();
                    for (int k = 0; k < nf; k++)
                    {
                        double[] fr = new double[per];
                        Array.Copy(vals, k * per, fr, 0, per);
                        frames.Add(fr);
                    }
                    return;
                }
                else i++;
            }
            throw new FormatException("no MOTION section");
        }

        private static double P(string s) { return double.Parse(s, CultureInfo.InvariantCulture); }

        /// <summary>map a BVH (joint names = game joint names) onto a clip's channels -> ClipData (one key per BVH
        /// frame).  Channels whose joint is not in the BVH take their values from fallback (per channel [frames][comps],
        /// e.g. AnimClip.Sample() of the clip being replaced; the last frame is held) or fail.</summary>
        public static ClipData ToClipData(string text, IList<Channel> channels, double scale = 1.0, IList<float[][]> fallback = null)
        {
            List<BvhJoint> joints;
            double ft;
            List<double[]> frames;
            Read(text, out joints, out ft, out frames);
            Dictionary<string, Tuple<int, List<string>, double[]>> perJoint = new Dictionary<string, Tuple<int, List<string>, double[]>>();
            int col = 0;
            foreach (BvhJoint j in joints)
            {
                perJoint[j.Name] = Tuple.Create(col, j.Channels, j.Offset ?? new double[3]);
                col += j.Channels.Count;
            }
            int nfr = frames.Count;
            List<double[][]> vals = new List<double[][]>();
            List<string> kinds = new List<string>();
            for (int ci = 0; ci < channels.Count; ci++)
            {
                Channel ch = channels[ci];
                string name = ch.Name;
                int dot = name.LastIndexOf('.');
                string jn = dot >= 0 ? name.Substring(0, dot) : name;
                string comp = dot >= 0 ? name.Substring(dot + 1) : "";
                if (!perJoint.ContainsKey(jn) || ch.Kind == "float")
                {
                    if (fallback == null) throw new FormatException("BVH has no joint '" + jn + "' and no fallback values were given");
                    float[][] fb = fallback[ci];
                    double[][] v = new double[nfr][];
                    for (int k = 0; k < nfr; k++) v[k] = fb[Math.Min(k, fb.Length - 1)].Select(x => (double)x).ToArray();
                    vals.Add(v);
                    kinds.Add(ch.Kind);
                    continue;
                }
                Tuple<int, List<string>, double[]> pj = perJoint[jn];
                int c0 = pj.Item1;
                List<string> chs = pj.Item2;
                double[][] rows = new double[nfr][];
                for (int k = 0; k < nfr; k++)
                {
                    Dictionary<string, double> cv = new Dictionary<string, double>();
                    for (int x = 0; x < chs.Count; x++) cv[chs[x]] = frames[k][c0 + x];
                    if (ch.Kind == "quat")
                    {
                        double[,] m = { { 1, 0, 0 }, { 0, 1, 0 }, { 0, 0, 1 } };
                        foreach (string c in chs.Where(c => c.EndsWith("rotation")))
                        {
                            double a = cv[c];
                            char axis = c[0];
                            if (axis == 'X') m = Mul(m, EulerZxyToMat(0, a, 0));
                            else if (axis == 'Y') m = Mul(m, EulerZxyToMat(0, 0, a));
                            else m = Mul(m, EulerZxyToMat(a, 0, 0));
                        }
                        rows[k] = MatToQuat(m);
                    }
                    else if (ch.Kind == "vec3" && comp == "t")
                    {
                        if (cv.ContainsKey("Xposition")) rows[k] = new[] { cv["Xposition"] / scale, cv["Yposition"] / scale, cv["Zposition"] / scale };
                        else rows[k] = pj.Item3.Select(x => x / scale).ToArray();
                    }
                    else if (ch.Kind == "vec3") rows[k] = new[] { 1.0, 1.0, 1.0 };
                    else rows[k] = new[] { 0.0 };
                }
                vals.Add(rows);
                kinds.Add(ch.Kind);
            }
            for (int ci = 0; ci < vals.Count; ci++)
            {
                if (kinds[ci] != "quat") continue;
                double[][] v = vals[ci];
                for (int r = 1; r < v.Length; r++)
                {
                    double d = 0;
                    for (int c = 0; c < 4; c++) d += v[r][c] * v[r - 1][c];
                    if (d < 0) for (int c = 0; c < 4; c++) v[r][c] = -v[r][c];
                }
            }
            return new ClipData(Enumerable.Range(0, nfr).Select(x => (long)x).ToList(), kinds, vals, false, nfr - 1);
        }
    }

    /// <summary>World-space joint positions of a clip (the editor's skeleton preview)</summary>
    public static class Pose
    {
        public sealed class World
        {
            public List<string> Joints;
            public List<int> Parents;
            public List<int> Frames;
            public float[][][] Pos;      // [frame][joint][3]
            public int FrameCount;
        }

        private static double[,] QuatMat(float[] q)
        {
            double x = q[0], y = q[1], z = q[2], w = q[3];
            double n = Math.Sqrt(x * x + y * y + z * z + w * w);
            if (n == 0) n = 1;
            return Bvh.QuatToMat(x / n, y / n, z / n, w / n);
        }

        public static World WorldPositions(AntDb db, ulong key, RigIndex ri = null, IList<int> frames = null, int maxFrames = 400)
        {
            if (ri == null) ri = new RigIndex(db);
            AnimClip c = AnimClip.Load(db, key, ri);
            GdObj rig = ri.BestRig(c.LayoutKey, c.DofIds);
            if (rig == null) throw new GdException("no rig/skeleton found for this clip");
            RigIndex.Skeleton sk = ri.GetSkeleton(rig);
            if (sk == null) throw new GdException("the clip's rig has no skeleton");
            Dictionary<string, Dictionary<string, float[]>> bind = ri.BindPose(rig);
            List<RigIndex.Joint> joints = sk.Joints;
            int n = c.FrameCount();
            if (frames == null)
            {
                int step = Math.Max(1, (int)Math.Ceiling(n / (double)maxFrames));
                List<int> fr = new List<int>();
                for (int i = 0; i < n; i += step) fr.Add(i);
                frames = fr;
            }
            List<float[][]> vals = c.Sample(frames);
            Dictionary<string, float[][]> track = new Dictionary<string, float[][]>();
            for (int i = 0; i < c.Channels.Count; i++) track[c.Channels[i].Name] = vals[i];
            int F = frames.Count, J = joints.Count;
            float[][][] pos = new float[F][][];
            int[] order = Enumerable.Range(0, J).OrderBy(j => Depth(joints, j)).ToArray();
            for (int f = 0; f < F; f++)
            {
                double[][,] WR = new double[J][,];
                double[][] WT = new double[J][];
                pos[f] = new float[J][];
                foreach (int j in order)
                {
                    string nm = joints[j].Name;
                    Dictionary<string, float[]> b;
                    bind.TryGetValue(nm, out b);
                    float[][] tq, tt, ts;
                    float[] q = track.TryGetValue(nm + ".q", out tq) ? tq[f] : (b != null && b.ContainsKey("q") ? b["q"] : new[] { 0f, 0f, 0f, 1f });
                    float[] t = track.TryGetValue(nm + ".t", out tt) ? tt[f] : (b != null && b.ContainsKey("t") ? b["t"] : new[] { 0f, 0f, 0f });
                    float[] s = track.TryGetValue(nm + ".s", out ts) ? ts[f] : (b != null && b.ContainsKey("s") ? b["s"] : new[] { 1f, 1f, 1f });
                    double[,] R = QuatMat(q);
                    double[,] local = new double[3, 3];
                    for (int r = 0; r < 3; r++) for (int cc = 0; cc < 3; cc++) local[r, cc] = R[r, cc] * s[cc];
                    int par = joints[j].Parent;
                    if (par < 0 || par >= J || WR[par] == null)
                    {
                        WR[j] = local;
                        WT[j] = new double[] { t[0], t[1], t[2] };
                    }
                    else
                    {
                        WR[j] = Bvh.Mul(WR[par], local);
                        double[] wt = new double[3];
                        for (int r = 0; r < 3; r++) wt[r] = WT[par][r] + WR[par][r, 0] * t[0] + WR[par][r, 1] * t[1] + WR[par][r, 2] * t[2];
                        WT[j] = wt;
                    }
                    pos[f][j] = new[] { (float)WT[j][0], (float)WT[j][1], (float)WT[j][2] };
                }
            }
            return new World { Joints = joints.Select(j => j.Name).ToList(), Parents = joints.Select(j => j.Parent).ToList(), Frames = frames.ToList(), Pos = pos, FrameCount = n };
        }

        private static int Depth(List<RigIndex.Joint> joints, int j)
        {
            int d = 0;
            while (joints[j].Parent >= 0 && joints[j].Parent < joints.Count && d < 512) { j = joints[j].Parent; d++; }
            return d;
        }
    }
}
