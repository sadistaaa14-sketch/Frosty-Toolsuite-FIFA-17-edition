using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Numerics;

namespace AssetBankPlugin.Export
{
    /// <summary>
    /// BVH exporter using ZYX intrinsic euler order (avoids gimbal lock on Hips bind rotation).
    /// Writes bind*delta rotations with local skeleton offsets.
    /// Channel order: Zrotation Yrotation Xrotation.
    /// </summary>
    public class AnimationExporterBVH : IAnimationExporter
    {
        private static readonly HashSet<string> MainBones = new HashSet<string>
        {
            "Reference", "AITrajectory", "Hips",
            "Spine", "Spine1", "Spine2", "Spine3",
            "Neck", "Neck1", "Head", "HeadEnd",
            "LeftShoulder", "LeftArm", "LeftForeArm", "LeftHand",
            "RightShoulder", "RightArm", "RightForeArm", "RightHand",
            "LeftUpLeg", "LeftLeg", "LeftFoot", "LeftToeBase", "LeftFootEnd",
            "RightUpLeg", "RightLeg", "RightFoot", "RightToeBase", "RightFootEnd",
            "LeftHandThumb1", "LeftHandThumb2", "LeftHandThumb3", "LeftHandThumbEnd",
            "LeftInHandIndex", "LeftHandIndex1", "LeftHandIndex2", "LeftHandIndex3", "LeftHandIndexEnd",
            "LeftInHandMiddle", "LeftHandMiddle1", "LeftHandMiddle2", "LeftHandMiddle3", "LeftHandMiddleEnd",
            "LeftInHandRing", "LeftHandRing1", "LeftHandRing2", "LeftHandRing3", "LeftHandRingEnd",
            "LeftInHandPinky", "LeftHandPinky1", "LeftHandPinky2", "LeftHandPinky3", "LeftHandPinkyEnd",
            "RightHandThumb1", "RightHandThumb2", "RightHandThumb3", "RightHandThumbEnd",
            "RightInHandIndex", "RightHandIndex1", "RightHandIndex2", "RightHandIndex3", "RightHandIndexEnd",
            "RightInHandMiddle", "RightHandMiddle1", "RightHandMiddle2", "RightHandMiddle3", "RightHandMiddleEnd",
            "RightInHandRing", "RightHandRing1", "RightHandRing2", "RightHandRing3", "RightHandRingEnd",
            "RightInHandPinky", "RightHandPinky1", "RightHandPinky2", "RightHandPinky3", "RightHandPinkyEnd",
            "Face", "Prop", "Trajectory", "TrajectoryEnd",
        };

        public override void Export(InternalAnimation animation, InternalSkeleton skeleton, string path)
        {
            if (animation == null || animation.Frames.Count == 0 || skeleton == null) return;
            Directory.CreateDirectory(path);
            string filePath = Path.Combine(path, animation.Name + ".bvh");
            int boneCount = skeleton.BoneNames.Count;
            int numFrames = animation.Frames.Count;

            // Build inclusion set
            var included = new HashSet<int>();
            for (int i = 0; i < boneCount; i++)
                if (MainBones.Contains(skeleton.BoneNames[i])) included.Add(i);
            for (int i = 0; i < boneCount; i++)
            {
                if (!included.Contains(i)) continue;
                int p = skeleton.BoneParents[i];
                while (p >= 0 && !included.Contains(p)) { included.Add(p); p = skeleton.BoneParents[p]; }
            }

            // Children
            var children = new List<int>[boneCount];
            for (int i = 0; i < boneCount; i++) children[i] = new List<int>();
            int rootIdx = 0;
            for (int i = 0; i < boneCount; i++)
            {
                if (!included.Contains(i)) continue;
                int p = skeleton.BoneParents[i];
                if (p >= 0 && included.Contains(p)) children[p].Add(i);
                else if (p < 0) rootIdx = i;
            }

            // Channel maps
            var rotMap = new Dictionary<string, int>();
            for (int i = 0; i < animation.RotationChannels.Count; i++) rotMap[animation.RotationChannels[i]] = i;
            var posMap = new Dictionary<string, int>();
            for (int i = 0; i < animation.PositionChannels.Count; i++) posMap[animation.PositionChannels[i]] = i;

            // BVH order (depth-first)
            var bvhOrder = new List<int>();
            var stack = new Stack<int>(); stack.Push(rootIdx);
            while (stack.Count > 0)
            {
                int idx = stack.Pop();
                if (!included.Contains(idx)) continue;
                bvhOrder.Add(idx);
                for (int i = children[idx].Count - 1; i >= 0; i--) stack.Push(children[idx][i]);
            }

            var ci = CultureInfo.InvariantCulture;
            using (var w = new StreamWriter(filePath))
            {
                // HIERARCHY
                w.WriteLine("HIERARCHY");
                WriteJoint(w, skeleton, children, included, rootIdx, 0, true);

                // MOTION
                w.WriteLine("MOTION");
                w.WriteLine("Frames: " + numFrames);
                w.WriteLine(string.Format(ci, "Frame Time: {0:F6}", 1.0f / 30f));

                for (int frame = 0; frame < numFrames; frame++)
                {
                    Frame f = animation.Frames[frame];
                    foreach (int boneIdx in bvhOrder)
                    {
                        string bn = skeleton.BoneNames[boneIdx];

                        // Root position
                        if (boneIdx == rootIdx)
                        {
                            float px = 0, py = 0, pz = 0;
                            int pi;
                            if (posMap.TryGetValue(bn, out pi) && pi < f.Positions.Count)
                            { px = f.Positions[pi].X; py = f.Positions[pi].Y; pz = f.Positions[pi].Z; }
                            w.Write(string.Format(ci, "{0:F4} {1:F4} {2:F4} ", px, py, pz));
                        }

                        // Rotation: bind * delta
                        Quaternion bindQ = skeleton.LocalTransforms[boneIdx].Rotation;
                        Quaternion finalQ = bindQ; // rest pose = bind only

                        int ri;
                        if (rotMap.TryGetValue(bn, out ri) && ri < f.Rotations.Count)
                        {
                            Quaternion delta = f.Rotations[ri];
                            float n = delta.Length();
                            if (n > 1e-10f) delta = Quaternion.Normalize(delta);
                            else delta = Quaternion.Identity;
                            finalQ = bindQ * delta;
                        }

                        // Decompose as ZYX intrinsic: M = Rz * Ry * Rx
                        float rz, ry, rx;
                        QuatToZYX(finalQ, out rz, out ry, out rx);
                        w.Write(string.Format(ci, "{0:F4} {1:F4} {2:F4} ", rz, ry, rx));
                    }
                    w.WriteLine();
                }
            }
        }

        private void WriteJoint(StreamWriter w, InternalSkeleton s, List<int>[] ch,
            HashSet<int> inc, int idx, int d, bool root)
        {
            if (!inc.Contains(idx)) return;
            var ci = CultureInfo.InvariantCulture;
            string t = new string('\t', d);
            Vector3 o = s.LocalTransforms[idx].Position;

            w.WriteLine(t + (root ? "ROOT" : "JOINT") + " " + s.BoneNames[idx]);
            w.WriteLine(t + "{");
            w.WriteLine(string.Format(ci, "{0}\tOFFSET {1:F6} {2:F6} {3:F6}", t, o.X, o.Y, o.Z));
            // ZYX channel order to avoid gimbal lock on Hips bind rotation
            w.WriteLine(t + "\tCHANNELS " + (root ? "6 Xposition Yposition Zposition " : "3 ")
                + "Zrotation Yrotation Xrotation");

            bool hasCh = false;
            foreach (int c in ch[idx]) if (inc.Contains(c)) { hasCh = true; break; }
            if (hasCh) { foreach (int c in ch[idx]) WriteJoint(w, s, ch, inc, c, d + 1, false); }
            else
            {
                w.WriteLine(t + "\tEnd Site");
                w.WriteLine(t + "\t{");
                w.WriteLine(t + "\t\tOFFSET 0.010000 0.000000 0.000000");
                w.WriteLine(t + "\t}");
            }
            w.WriteLine(t + "}");
        }

        /// <summary>
        /// Quaternion to ZYX intrinsic euler (degrees).
        /// Decomposition for M = Rz * Ry * Rx.
        /// Gimbal lock at Ry = ±90° (rare for body bones).
        /// </summary>
        private static void QuatToZYX(Quaternion q, out float rz, out float ry, out float rx)
        {
            // M[2][0] = 2(xz - wy), sin(ry) = -M[2][0]
            float sinY = 2f * (q.W * q.Y - q.X * q.Z);
            if (sinY > 1f) sinY = 1f;
            if (sinY < -1f) sinY = -1f;
            ry = (float)Math.Asin(sinY);
            float cosY = (float)Math.Cos(ry);

            if (Math.Abs(cosY) > 1e-6f)
            {
                // rx = atan2(M[2][1], M[2][2])
                rx = (float)Math.Atan2(2f * (q.Y * q.Z + q.W * q.X),
                                       1f - 2f * (q.X * q.X + q.Y * q.Y));
                // rz = atan2(M[1][0], M[0][0])
                rz = (float)Math.Atan2(2f * (q.X * q.Y + q.W * q.Z),
                                       1f - 2f * (q.Y * q.Y + q.Z * q.Z));
            }
            else
            {
                // Gimbal lock
                rx = (float)Math.Atan2(-2f * (q.Y * q.Z - q.W * q.X),
                                        1f - 2f * (q.X * q.X + q.Z * q.Z));
                rz = 0f;
            }

            const float Rad2Deg = 180f / (float)Math.PI;
            rz *= Rad2Deg;
            ry *= Rad2Deg;
            rx *= Rad2Deg;
        }
    }
}
