// BVH → VBR Importer for FIFA 17
// Reads a BVH file, converts euler→quaternion, encodes to VBR.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Numerics;

namespace AnimationBrowserPlugin
{
    public class BvhImporter
    {
        public class BvhBone
        {
            public string Name;
            public int Parent;
            public float[] Offset;
            public string[] Channels;
            public int ChannelStart;
        }

        public List<BvhBone> Bones;
        public List<float[]> FrameData;
        public int NumFrames;
        public float Fps;

        public void Load(string path)
        {
            Bones = new List<BvhBone>();
            FrameData = new List<float[]>();
            var parentStack = new Stack<int>();
            parentStack.Push(-1);
            int channelCount = 0;
            var ci = CultureInfo.InvariantCulture;

            var lines = File.ReadAllLines(path);
            int i = 0;

            // Parse hierarchy
            while (i < lines.Length)
            {
                string line = lines[i].Trim();

                if (line.StartsWith("ROOT") || line.StartsWith("JOINT"))
                {
                    string name = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries)[1];
                    int parent = parentStack.Count > 0 ? parentStack.Peek() : -1;
                    Bones.Add(new BvhBone { Name = name, Parent = parent, Offset = new float[3], Channels = new string[0], ChannelStart = channelCount });
                }
                else if (line.StartsWith("OFFSET") && Bones.Count > 0)
                {
                    var parts = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                    Bones[Bones.Count - 1].Offset = new float[] {
                        float.Parse(parts[1], ci), float.Parse(parts[2], ci), float.Parse(parts[3], ci) };
                }
                else if (line.StartsWith("CHANNELS") && Bones.Count > 0)
                {
                    var parts = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                    int n = int.Parse(parts[1]);
                    string[] chs = new string[n];
                    for (int j = 0; j < n; j++) chs[j] = parts[2 + j];
                    Bones[Bones.Count - 1].Channels = chs;
                    channelCount += n;
                }
                else if (line == "{") { if (Bones.Count > 0) parentStack.Push(Bones.Count - 1); }
                else if (line == "}") { if (parentStack.Count > 0) parentStack.Pop(); }
                else if (line == "MOTION") { i++; break; }
                i++;
            }

            // Parse motion
            NumFrames = int.Parse(lines[i].Split(new[] { ' ', '\t', ':' }, StringSplitOptions.RemoveEmptyEntries)[1]);
            i++;
            float ft = float.Parse(lines[i].Split(new[] { ' ', '\t', ':' }, StringSplitOptions.RemoveEmptyEntries)[2], ci);
            Fps = ft > 0 ? 1.0f / ft : 30f;
            i++;

            for (int f = 0; f < NumFrames && i < lines.Length; f++, i++)
            {
                var parts = lines[i].Trim().Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                float[] vals = new float[parts.Length];
                for (int j = 0; j < parts.Length; j++)
                    vals[j] = float.Parse(parts[j], ci);
                FrameData.Add(vals);
            }
        }

        // ─── Euler → Quaternion ──────────────────────────────
        private static Quaternion EulerToQuatZYX(float rz, float ry, float rx)
        {
            float rz2 = rz * 0.5f * (float)(Math.PI / 180.0);
            float ry2 = ry * 0.5f * (float)(Math.PI / 180.0);
            float rx2 = rx * 0.5f * (float)(Math.PI / 180.0);
            float cx = (float)Math.Cos(rx2), sx = (float)Math.Sin(rx2);
            float cy = (float)Math.Cos(ry2), sy = (float)Math.Sin(ry2);
            float cz = (float)Math.Cos(rz2), sz = (float)Math.Sin(rz2);
            return new Quaternion(
                cz * cy * sx - sz * sy * cx,
                cz * sy * cx + sz * cy * sx,
                sz * cy * cx - cz * sy * sx,
                cz * cy * cx + sz * sy * sx);
        }

        private static Quaternion EulerToQuatZXY(float rz, float rx, float ry)
        {
            float rz2 = rz * 0.5f * (float)(Math.PI / 180.0);
            float rx2 = rx * 0.5f * (float)(Math.PI / 180.0);
            float ry2 = ry * 0.5f * (float)(Math.PI / 180.0);
            float cx = (float)Math.Cos(rx2), sx = (float)Math.Sin(rx2);
            float cy = (float)Math.Cos(ry2), sy = (float)Math.Sin(ry2);
            float cz = (float)Math.Cos(rz2), sz = (float)Math.Sin(rz2);
            return new Quaternion(
                cy * sx * cz + sy * cx * sz,
                sy * cx * cz - cy * sx * sz,
                cy * cx * sz - sy * sx * cz,
                cy * cx * cz + sy * sx * sz);
        }

        // ─── DofId Map ──────────────────────────────────────
        private static readonly Dictionary<string, int> BoneToDofId = new Dictionary<string, int>
        {
            {"AITrajectory",854},{"Hips",854},
            {"Spine",727},{"Spine1",728},{"Spine2",729},{"Spine3",730},
            {"Neck",423},{"Neck1",424},{"Head",425},{"Face",419},
            {"LeftShoulder",703},{"LeftArm",704},{"LeftForeArm",705},{"LeftHand",706},
            {"RightShoulder",723},{"RightArm",724},{"RightForeArm",725},{"RightHand",726},
            {"LeftUpLeg",651},{"LeftLeg",652},{"LeftFoot",653},{"LeftToeBase",654},
            {"RightUpLeg",197},{"RightLeg",198},{"RightFoot",199},{"RightToeBase",200},
            {"LeftHandThumb1",731},{"LeftHandThumb2",732},{"LeftHandThumb3",733},
            {"LeftInHandIndex",734},{"LeftHandIndex1",735},{"LeftHandIndex2",736},{"LeftHandIndex3",737},
            {"LeftInHandMiddle",738},{"LeftHandMiddle1",739},{"LeftHandMiddle2",740},{"LeftHandMiddle3",741},
            {"LeftInHandRing",742},{"LeftHandRing1",743},{"LeftHandRing2",744},{"LeftHandRing3",745},
            {"LeftInHandPinky",746},{"LeftHandPinky1",747},{"LeftHandPinky2",748},{"LeftHandPinky3",749},
            {"RightHandThumb1",762},{"RightHandThumb2",763},{"RightHandThumb3",764},
            {"RightInHandIndex",765},{"RightHandIndex1",766},{"RightHandIndex2",767},{"RightHandIndex3",768},
            {"RightInHandMiddle",769},{"RightHandMiddle1",770},{"RightHandMiddle2",771},{"RightHandMiddle3",772},
            {"RightInHandRing",773},{"RightHandRing1",774},{"RightHandRing2",775},{"RightHandRing3",776},
            {"RightInHandPinky",777},{"RightHandPinky1",778},{"RightHandPinky2",779},{"RightHandPinky3",780},
        };

        // ─── Extract Curves ──────────────────────────────────
        /// <summary>
        /// Extract quaternion and vec3 curves from loaded BVH.
        /// bindQuats: optional bind-pose quaternions per bone (to extract delta).
        /// Returns (quatCurves[Q][frame][4], vec3Curves[V3][frame][3], boneNames, dofIds)
        /// </summary>
        public void ExtractCurves(
            Dictionary<string, Quaternion> bindQuats,
            out float[][][] quatCurves, out float[][][] vec3Curves,
            out List<string> boneNames, out List<int> dofIds)
        {
            var qList = new List<float[][]>();
            var vList = new List<float[][]>();
            boneNames = new List<string>();
            dofIds = new List<int>();

            foreach (var bone in Bones)
            {
                if (bone.Name == "Reference") continue;
                if (!BoneToDofId.ContainsKey(bone.Name)) continue;

                // Find rotation channels
                var rotIndices = new List<int>();
                var rotOrder = new List<char>();
                for (int ci = 0; ci < bone.Channels.Length; ci++)
                {
                    string ch = bone.Channels[ci].ToLower();
                    if (ch.Contains("rotation"))
                    {
                        rotIndices.Add(bone.ChannelStart + ci);
                        rotOrder.Add(ch[0]); // 'x','y','z'
                    }
                }

                if (rotIndices.Count == 3)
                {
                    string order = new string(rotOrder.ToArray()).ToUpper();
                    var frames = new float[NumFrames][];

                    for (int f = 0; f < NumFrames; f++)
                    {
                        float r0 = FrameData[f][rotIndices[0]];
                        float r1 = FrameData[f][rotIndices[1]];
                        float r2 = FrameData[f][rotIndices[2]];

                        Quaternion q;
                        if (order == "ZYX") q = EulerToQuatZYX(r0, r1, r2);
                        else if (order == "ZXY") q = EulerToQuatZXY(r0, r1, r2);
                        else q = EulerToQuatZYX(r0, r1, r2); // fallback

                        // Extract delta if bind quat provided
                        if (bindQuats != null && bindQuats.ContainsKey(bone.Name))
                        {
                            Quaternion bq = bindQuats[bone.Name];
                            Quaternion bqInv = Quaternion.Conjugate(bq);
                            q = bqInv * q;
                        }

                        q = Quaternion.Normalize(q);
                        frames[f] = new float[] { q.W, q.X, q.Y, q.Z };
                    }

                    qList.Add(frames);
                    boneNames.Add(bone.Name);
                    dofIds.Add(BoneToDofId[bone.Name]);
                }
            }

            quatCurves = qList.ToArray();
            vec3Curves = vList.ToArray();
        }
    }
}
