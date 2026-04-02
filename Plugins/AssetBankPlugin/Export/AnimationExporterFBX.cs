using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Numerics;
using System.Text;

namespace AssetBankPlugin.Export
{
    public class AnimationExporterFBX : IAnimationExporter
    {
        private static string _blenderPath;
        private static string _helperScript;
        private static string _meshFbx;
        private static bool _searched;

        private static void FindPaths()
        {
            if (_searched) return;
            _searched = true;

            string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            string bf = Path.Combine(pf, "Blender Foundation");
            if (Directory.Exists(bf))
            {
                foreach (var dir in Directory.GetDirectories(bf))
                {
                    string exe = Path.Combine(dir, "blender.exe");
                    if (File.Exists(exe)) { _blenderPath = exe; break; }
                }
            }

            string pluginDir = Path.GetDirectoryName(
                System.Reflection.Assembly.GetExecutingAssembly().Location) ?? "";
            
            foreach (string d in new[] { pluginDir, Path.Combine(pluginDir, "..") })
            {
                if (_helperScript == null)
                { string p = Path.Combine(d, "fbx_helper.py"); if (File.Exists(p)) _helperScript = Path.GetFullPath(p); }
                if (_meshFbx == null)
                { string p = Path.Combine(d, "Untitled.fbx"); if (File.Exists(p)) _meshFbx = Path.GetFullPath(p); }
            }
        }

        public override void Export(InternalAnimation animation, InternalSkeleton skeleton, string path)
        {
            if (animation == null || animation.Frames.Count == 0 || skeleton == null) return;
            FindPaths();
            Directory.CreateDirectory(path);

            string safeName = animation.Name;
            foreach (char c in Path.GetInvalidFileNameChars())
                safeName = safeName.Replace(c, '_');
            safeName = safeName.Replace(' ', '_').Replace("[", "").Replace("]", "");
            if (string.IsNullOrEmpty(safeName)) safeName = "anim_" + DateTime.Now.Ticks;

            string jsonPath = Path.GetFullPath(Path.Combine(path, safeName + ".anim.json"));
            string fbxPath = Path.GetFullPath(Path.Combine(path, safeName + ".fbx"));
            string logPath = Path.GetFullPath(Path.Combine(path, "_fbx_export.log"));

            try { File.AppendAllText(logPath, DateTime.Now.ToString("HH:mm:ss")
                + " " + safeName
                + " FBX=" + fbxPath
                + " Blender=" + (_blenderPath ?? "NOT FOUND")
                + " Helper=" + (_helperScript ?? "NOT FOUND")
                + " Mesh=" + (_meshFbx ?? "NOT FOUND") + "\n"); } catch {}

            // Build JSON
            var ci = CultureInfo.InvariantCulture;
            int numFrames = animation.Frames.Count;
            var rotMap = new Dictionary<string, int>();
            for (int i = 0; i < animation.RotationChannels.Count; i++)
                rotMap[animation.RotationChannels[i]] = i;
            var posMap = new Dictionary<string, int>();
            for (int i = 0; i < animation.PositionChannels.Count; i++)
                posMap[animation.PositionChannels[i]] = i;

            var sb = new StringBuilder(numFrames * 100);
            sb.Append("{\n");
            sb.AppendFormat("  \"name\": \"{0}\",\n", safeName);
            sb.Append("  \"fps\": 30,\n");
            sb.AppendFormat("  \"num_frames\": {0},\n", numFrames);

            // ─── Write skeleton rest pose ───────────────────────────
            // This gives the Python helper the EXACT game rest quaternions
            // so it doesn't need to derive them from the mesh FBX (which may differ).
            sb.Append("  \"skeleton\": {\n");
            for (int i = 0; i < skeleton.BoneNames.Count; i++)
            {
                string bn = skeleton.BoneNames[i];
                Transform lt = skeleton.LocalTransforms[i];
                Quaternion rq = lt.Rotation;
                Vector3 rp = lt.Position;
                int parent = skeleton.BoneParents[i];

                sb.AppendFormat("    \"{0}\": {{", bn);
                sb.AppendFormat(ci, "\"r\":[{0},{1},{2},{3}]", rq.W, rq.X, rq.Y, rq.Z);
                sb.AppendFormat(ci, ",\"t\":[{0},{1},{2}]", rp.X, rp.Y, rp.Z);
                sb.AppendFormat(",\"p\":{0}", parent);
                sb.Append(i < skeleton.BoneNames.Count - 1 ? "},\n" : "}\n");
            }
            sb.Append("  },\n");

            // ─── Write rotation channels ────────────────────────────
            sb.Append("  \"bones\": {\n");

            int boneIdx = 0, totalBones = rotMap.Count;
            foreach (var kvp in rotMap)
            {
                int chIdx = kvp.Value;
                sb.AppendFormat("    \"{0}\": [", kvp.Key);
                for (int f = 0; f < numFrames; f++)
                {
                    if (f > 0) sb.Append(",");
                    if (chIdx < animation.Frames[f].Rotations.Count)
                    {
                        Quaternion q = animation.Frames[f].Rotations[chIdx];
                        // Output order: [q.W, q.X, q.Y, q.Z]
                        // Due to the double-swap in VbrAnimationAsset.ConvertToInternal,
                        // q.W = X_vbr, q.X = Y_vbr, q.Y = Z_vbr, q.Z = W_vbr
                        // So this writes [X_vbr, Y_vbr, Z_vbr, W_vbr] — correct VBR DOF order.
                        sb.AppendFormat(ci, "[{0},{1},{2},{3}]", q.W, q.X, q.Y, q.Z);
                    }
                    else sb.Append("[1,0,0,0]");
                }
                boneIdx++;
                sb.Append(boneIdx < totalBones ? "],\n" : "]\n");
            }
            sb.Append("  },\n");

            // ─── Write position channels ────────────────────────────
            sb.Append("  \"positions\": {\n");
            int posIdx = 0, totalPos = posMap.Count;
            foreach (var kvp in posMap)
            {
                int chIdx = kvp.Value;
                sb.AppendFormat("    \"{0}\": [", kvp.Key);
                for (int f = 0; f < numFrames; f++)
                {
                    if (f > 0) sb.Append(",");
                    if (chIdx < animation.Frames[f].Positions.Count)
                    {
                        Vector3 p = animation.Frames[f].Positions[chIdx];
                        sb.AppendFormat(ci, "[{0},{1},{2}]", p.X, p.Y, p.Z);
                    }
                    else sb.Append("[0,0,0]");
                }
                posIdx++;
                sb.Append(posIdx < totalPos ? "],\n" : "]\n");
            }
            sb.Append("  }\n");

            sb.Append("}\n");
            File.WriteAllText(jsonPath, sb.ToString());

            // Call Blender
            if (_blenderPath != null && _helperScript != null)
            {
                try
                {
                    string args = string.Format("--background --python \"{0}\" -- \"{1}\" \"{2}\"",
                        _helperScript, jsonPath, fbxPath);
                    if (_meshFbx != null) args += string.Format(" \"{0}\"", _meshFbx);

                    var psi = new ProcessStartInfo
                    {
                        FileName = _blenderPath,
                        Arguments = args,
                        UseShellExecute = false,
                        RedirectStandardOutput = false,
                        RedirectStandardError = false,
                        CreateNoWindow = true,
                    };

                    var proc = Process.Start(psi);
                    proc.WaitForExit(120000);

                    try { File.AppendAllText(logPath, "  Exit=" + proc.ExitCode
                        + " FBX=" + File.Exists(fbxPath) + "\n"); } catch {}

                    if (File.Exists(fbxPath))
                        try { File.Delete(jsonPath); } catch { }
                }
                catch (Exception ex)
                {
                    try { File.AppendAllText(logPath, "  ERROR: " + ex.Message + "\n"); } catch {}
                }
            }
        }
    }
}
