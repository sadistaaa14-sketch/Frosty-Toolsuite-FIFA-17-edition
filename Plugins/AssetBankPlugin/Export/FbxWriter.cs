// FBX 7.4 Binary Writer — matches Blender's exact output structure.
// Pure C#, zero external dependencies.

using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Text;

namespace AssetBankPlugin.Export
{
    public static class FbxWriter
    {
        #region Node tree
        private class N
        {
            public string Name;
            public List<object> Props = new List<object>();
            public List<N> Kids = new List<N>();
            public N(string n) { Name = n; }
            public N Kid(string n) { var c = new N(n); Kids.Add(c); return c; }
            public N P(params object[] v) { foreach (var x in v) Props.Add(x); return this; }
        }
        #endregion

        #region Binary write
        private static void WriteNode(BinaryWriter w, N node)
        {
            long start = w.BaseStream.Position;
            w.Write((uint)0); w.Write((uint)0); w.Write((uint)0); // placeholders
            byte[] nb = Encoding.ASCII.GetBytes(node.Name);
            w.Write((byte)nb.Length); w.Write(nb);

            long pstart = w.BaseStream.Position;
            foreach (var p in node.Props) WriteProp(w, p);
            long pend = w.BaseStream.Position;

            foreach (var k in node.Kids) WriteNode(w, k);
            if (node.Kids.Count > 0) WriteNull(w);

            long end = w.BaseStream.Position;
            w.BaseStream.Position = start;
            w.Write((uint)end); w.Write((uint)node.Props.Count); w.Write((uint)(pend - pstart));
            w.BaseStream.Position = end;
        }

        private static void WriteNull(BinaryWriter w)
        { w.Write((uint)0); w.Write((uint)0); w.Write((uint)0); w.Write((byte)0); }

        private static void WriteProp(BinaryWriter w, object v)
        {
            if (v is int) { w.Write((byte)'I'); w.Write((int)v); }
            else if (v is short) { w.Write((byte)'Y'); w.Write((short)v); }
            else if (v is long) { w.Write((byte)'L'); w.Write((long)v); }
            else if (v is float) { w.Write((byte)'F'); w.Write((float)v); }
            else if (v is double) { w.Write((byte)'D'); w.Write((double)v); }
            else if (v is string) { byte[] b = Enc((string)v); w.Write((byte)'S'); w.Write(b.Length); w.Write(b); }
            else if (v is RawStr) { byte[] b = ((RawStr)v).Data; w.Write((byte)'S'); w.Write(b.Length); w.Write(b); }
            else if (v is byte[]) { byte[] b = (byte[])v; w.Write((byte)'R'); w.Write(b.Length); w.Write(b); }
            else if (v is int[]) { var a=(int[])v; w.Write((byte)'i'); w.Write(a.Length); w.Write(0); w.Write(a.Length*4); foreach(var x in a) w.Write(x); }
            else if (v is long[]) { var a=(long[])v; w.Write((byte)'l'); w.Write(a.Length); w.Write(0); w.Write(a.Length*8); foreach(var x in a) w.Write(x); }
            else if (v is float[]) { var a=(float[])v; w.Write((byte)'f'); w.Write(a.Length); w.Write(0); w.Write(a.Length*4); foreach(var x in a) w.Write(x); }
            else if (v is double[]) { var a=(double[])v; w.Write((byte)'d'); w.Write(a.Length); w.Write(0); w.Write(a.Length*8); foreach(var x in a) w.Write(x); }
            else if (v is bool) { w.Write((byte)'C'); w.Write((byte)((bool)v ? 1 : 0)); }
        }

        private static byte[] Enc(string s)
        {
            return Encoding.ASCII.GetBytes(s);
        }

        /// <summary>Build FBX name: "instance\x00\x01class" as raw bytes.</summary>
        private static byte[] FbxName(string instance, string cls)
        {
            byte[] a = Encoding.ASCII.GetBytes(instance);
            byte[] b = Encoding.ASCII.GetBytes(cls);
            byte[] result = new byte[a.Length + 2 + b.Length];
            Array.Copy(a, 0, result, 0, a.Length);
            result[a.Length] = 0x00;
            result[a.Length + 1] = 0x01;
            Array.Copy(b, 0, result, a.Length + 2, b.Length);
            return result;
        }

        /// <summary>Wrapper to write raw bytes as FBX string property type 'S'.</summary>
        private class RawStr { public byte[] Data; public RawStr(byte[] d) { Data = d; } }
        #endregion

        #region Helpers
        private static long _id = 200000;
        private static long Id() { return ++_id; }

        private static N Prop70(string name, string t1, string t2, string flags, params object[] vals)
        {
            var n = new N("P"); n.P(name, t1, t2, flags);
            foreach (var v in vals) n.Props.Add(v);
            return n;
        }

        private static void QuatToEulerXYZ(Quaternion q, out double ex, out double ey, out double ez)
        {
            double sinY = 2.0 * (q.W * q.Y - q.X * q.Z);
            sinY = Math.Max(-1.0, Math.Min(1.0, sinY));
            ey = Math.Asin(sinY);
            double cosY = Math.Cos(ey);
            if (Math.Abs(cosY) > 1e-6)
            {
                ex = Math.Atan2(2.0 * (q.Y * q.Z + q.W * q.X), 1.0 - 2.0 * (q.X * q.X + q.Y * q.Y));
                ez = Math.Atan2(2.0 * (q.X * q.Y + q.W * q.Z), 1.0 - 2.0 * (q.Y * q.Y + q.Z * q.Z));
            }
            else { ex = Math.Atan2(-2.0 * (q.Y * q.Z - q.W * q.X), 1.0 - 2.0 * (q.X * q.X + q.Z * q.Z)); ez = 0; }
            ex *= 180.0 / Math.PI; ey *= 180.0 / Math.PI; ez *= 180.0 / Math.PI;
        }
        #endregion

        public static void Write(string path, InternalSkeleton skel, InternalAnimation anim)
        {
            _id = 200000;
            int bc = skel.BoneNames.Count, fc = anim.Frames.Count;
            long tps = 46186158000L; // FBX ticks/sec
            double fps = 30.0;
            long duration = (long)((fc - 1) / fps * tps);

            // Assign IDs
            long[] modelIds = new long[bc], naIds = new long[bc];
            for (int i = 0; i < bc; i++) { modelIds[i] = Id(); naIds[i] = Id(); }
            long stackId = Id(), layerId = Id();

            // Rotation channel map
            var rotMap = new Dictionary<string, int>();
            for (int i = 0; i < anim.RotationChannels.Count; i++)
                rotMap[anim.RotationChannels[i]] = i;

            // Per-bone anim IDs
            var animBones = new List<int>();
            var cnIds = new Dictionary<int, long>();
            var cxIds = new Dictionary<int, long>();
            var cyIds = new Dictionary<int, long>();
            var czIds = new Dictionary<int, long>();
            for (int i = 0; i < bc; i++)
            {
                if (!rotMap.ContainsKey(skel.BoneNames[i])) continue;
                animBones.Add(i);
                cnIds[i] = Id(); cxIds[i] = Id(); cyIds[i] = Id(); czIds[i] = Id();
            }

            // Count objects
            int nNA = bc, nModel = bc, nCN = animBones.Count, nCurve = animBones.Count * 3;
            int totalObj = nNA + nModel + 2 + nCN + nCurve; // +2 = stack + layer

            // Build FBX tree
            var root = new N("");

            // FBXHeaderExtension
            var hdr = root.Kid("FBXHeaderExtension");
            hdr.Kid("FBXHeaderVersion").P(1003);
            hdr.Kid("FBXVersion").P(7400);
            hdr.Kid("EncryptionType").P(0);
            hdr.Kid("Creator").P("FrostyEditor FBX Exporter");

            // FileId + Creator
            root.Kid("FileId").P(new byte[16]);
            root.Kid("CreationTime").P("1970-01-01 10:00:00:000");
            root.Kid("Creator").P("FrostyEditor FBX Exporter");

            // GlobalSettings
            var gs = root.Kid("GlobalSettings");
            gs.Kid("Version").P(1000);
            var gp = gs.Kid("Properties70");
            gp.Kids.Add(Prop70("UpAxis", "int", "Integer", "", 1));
            gp.Kids.Add(Prop70("UpAxisSign", "int", "Integer", "", 1));
            gp.Kids.Add(Prop70("FrontAxis", "int", "Integer", "", 2));
            gp.Kids.Add(Prop70("FrontAxisSign", "int", "Integer", "", 1));
            gp.Kids.Add(Prop70("CoordAxis", "int", "Integer", "", 0));
            gp.Kids.Add(Prop70("CoordAxisSign", "int", "Integer", "", 1));
            gp.Kids.Add(Prop70("UnitScaleFactor", "double", "Number", "", 1.0));
            gp.Kids.Add(Prop70("TimeSpanStart", "KTime", "Time", "", (long)0));
            gp.Kids.Add(Prop70("TimeSpanStop", "KTime", "Time", "", duration));
            gp.Kids.Add(Prop70("CustomFrameRate", "double", "Number", "", fps));

            // Documents
            var docs = root.Kid("Documents");
            docs.Kid("Count").P(1);
            var doc = docs.Kid("Document"); doc.P((long)1000000, "Scene", "Scene");
            var dp = doc.Kid("Properties70");
            dp.Kids.Add(Prop70("SourceObject", "object", "", ""));
            dp.Kids.Add(Prop70("ActiveAnimStackName", "KString", "", "", "Take 001"));
            doc.Kid("RootNode").P((long)0);

            // References (empty)
            root.Kid("References");

            // Definitions
            var defs = root.Kid("Definitions");
            defs.Kid("Version").P(100);
            defs.Kid("Count").P(totalObj + 1); // +1 for GlobalSettings
            var dGS = defs.Kid("ObjectType"); dGS.P("GlobalSettings"); dGS.Kid("Count").P(1);
            var dNA = defs.Kid("ObjectType"); dNA.P("NodeAttribute"); dNA.Kid("Count").P(nNA);
            var dM = defs.Kid("ObjectType"); dM.P("Model"); dM.Kid("Count").P(nModel);
            var dAS = defs.Kid("ObjectType"); dAS.P("AnimationStack"); dAS.Kid("Count").P(1);
            var dAL = defs.Kid("ObjectType"); dAL.P("AnimationLayer"); dAL.Kid("Count").P(1);
            if (nCN > 0) { var dCN = defs.Kid("ObjectType"); dCN.P("AnimationCurveNode"); dCN.Kid("Count").P(nCN); }
            if (nCurve > 0) { var dC = defs.Kid("ObjectType"); dC.P("AnimationCurve"); dC.Kid("Count").P(nCurve); }

            // Objects
            var objects = root.Kid("Objects");

            for (int i = 0; i < bc; i++)
            {
                string bn = skel.BoneNames[i];
                var tr = skel.LocalTransforms[i];
                double ex, ey, ez;
                QuatToEulerXYZ(tr.Rotation, out ex, out ey, out ez);

                // NodeAttribute (TypeFlags: "Skeleton")
                var na = new N("NodeAttribute");
                na.P(naIds[i], new RawStr(FbxName(bn, "NodeAttribute")), "LimbNode");
                na.Kid("TypeFlags").P("Skeleton");
                objects.Kids.Add(na);

                // Model (LimbNode)
                var m = new N("Model");
                m.P(modelIds[i], new RawStr(FbxName(bn, "Model")), "LimbNode");
                m.Kid("Version").P(232);
                var mp = m.Kid("Properties70");
                mp.Kids.Add(Prop70("Lcl Translation", "Lcl Translation", "", "A",
                    (double)tr.Position.X, (double)tr.Position.Y, (double)tr.Position.Z));
                mp.Kids.Add(Prop70("PreRotation", "Vector3D", "Vector", "", ex, ey, ez));
                mp.Kids.Add(Prop70("RotationActive", "bool", "", "", 1));
                mp.Kids.Add(Prop70("InheritType", "enum", "", "", 1));
                mp.Kids.Add(Prop70("DefaultAttributeIndex", "int", "Integer", "", 0));
                m.Kid("MultiLayer").P(0);
                m.Kid("MultiTake").P(0);
                m.Kid("Shading").P(1); // C type = char
                m.Kid("Culling").P("CullingOff");
                objects.Kids.Add(m);
            }

            // AnimationStack
            var stk = new N("AnimationStack");
            stk.P(stackId, new RawStr(FbxName("Take 001", "AnimStack")), "");
            var sp = stk.Kid("Properties70");
            sp.Kids.Add(Prop70("LocalStop", "KTime", "Time", "", duration));
            sp.Kids.Add(Prop70("ReferenceStop", "KTime", "Time", "", duration));
            objects.Kids.Add(stk);

            // AnimationLayer
            var lyr = new N("AnimationLayer");
            lyr.P(layerId, new RawStr(FbxName("BaseLayer", "AnimLayer")), "");
            objects.Kids.Add(lyr);

            // Animation curves per bone
            foreach (int bi in animBones)
            {
                int chIdx = rotMap[skel.BoneNames[bi]];
                Quaternion restQ = Quaternion.Normalize(skel.LocalTransforms[bi].Rotation);
                Quaternion restInv = Quaternion.Conjugate(restQ);

                var times = new long[fc];
                var xv = new float[fc]; var yv = new float[fc]; var zv = new float[fc];
                Quaternion prev = Quaternion.Identity;

                for (int f = 0; f < fc; f++)
                {
                    times[f] = (long)(f / fps * tps);
                    Quaternion q = (chIdx < anim.Frames[f].Rotations.Count)
                        ? Quaternion.Normalize(anim.Frames[f].Rotations[chIdx]) : Quaternion.Identity;
                    Quaternion delta = Quaternion.Multiply(restInv, q);
                    if (Quaternion.Dot(prev, delta) < 0)
                        delta = new Quaternion(-delta.X, -delta.Y, -delta.Z, -delta.W);
                    prev = delta;
                    double dex, dey, dez;
                    QuatToEulerXYZ(delta, out dex, out dey, out dez);
                    xv[f] = (float)dex; yv[f] = (float)dey; zv[f] = (float)dez;
                }

                // AnimationCurveNode
                var cn = new N("AnimationCurveNode");
                cn.P(cnIds[bi], new RawStr(FbxName("R", "AnimCurveNode")), "");
                var cnp = cn.Kid("Properties70");
                cnp.Kids.Add(Prop70("d|X", "Number", "", "A", (double)xv[0]));
                cnp.Kids.Add(Prop70("d|Y", "Number", "", "A", (double)yv[0]));
                cnp.Kids.Add(Prop70("d|Z", "Number", "", "A", (double)zv[0]));
                objects.Kids.Add(cn);

                // 3 AnimationCurves
                MakeCurve(objects, cxIds[bi], times, xv);
                MakeCurve(objects, cyIds[bi], times, yv);
                MakeCurve(objects, czIds[bi], times, zv);
            }

            // Connections
            var conns = root.Kid("Connections");
            // Root bone → scene
            Conn(conns, "OO", modelIds[0], 0);
            // Child bones → parent
            for (int i = 1; i < bc; i++)
            {
                int p = skel.BoneParents[i];
                Conn(conns, "OO", modelIds[i], p >= 0 ? modelIds[p] : 0);
            }
            // NodeAttribute → Model
            for (int i = 0; i < bc; i++)
                Conn(conns, "OO", naIds[i], modelIds[i]);
            // Layer → Stack
            Conn(conns, "OO", layerId, stackId);
            // CurveNode → Layer, CurveNode → Model, Curves → CurveNode
            foreach (int bi in animBones)
            {
                Conn(conns, "OO", cnIds[bi], layerId);
                ConnP(conns, "OP", cnIds[bi], modelIds[bi], "Lcl Rotation");
                ConnP(conns, "OP", cxIds[bi], cnIds[bi], "d|X");
                ConnP(conns, "OP", cyIds[bi], cnIds[bi], "d|Y");
                ConnP(conns, "OP", czIds[bi], cnIds[bi], "d|Z");
            }

            // Takes
            var takes = root.Kid("Takes");
            takes.Kid("Current").P("");
            var take = takes.Kid("Take"); take.P("Take 001");
            take.Kid("FileName").P("Take_001.tak");
            take.Kid("LocalTime").P((long)0, duration);
            take.Kid("ReferenceTime").P((long)0, duration);

            // Write binary
            using (var fs = new FileStream(path, FileMode.Create))
            using (var w = new BinaryWriter(fs))
            {
                // FBX binary header
                w.Write(Encoding.ASCII.GetBytes("Kaydara FBX Binary  "));
                w.Write((byte)0); w.Write((byte)0x1A); w.Write((byte)0);
                w.Write((uint)7400);

                foreach (var kid in root.Kids)
                    WriteNode(w, kid);
                WriteNull(w); // end sentinel

                // FBX footer
                byte[] pad = new byte[4]; w.Write(pad);
                w.Write((uint)7400);
                w.Write(new byte[120]);
                w.Write(new byte[] { 0xf8, 0x5a, 0x8c, 0x6a, 0xde, 0xf5, 0xd9, 0x7e,
                    0xec, 0xe9, 0x0c, 0xe3, 0x75, 0x8f, 0x29, 0x0b });
            }
        }

        private static void MakeCurve(N parent, long id, long[] times, float[] vals)
        {
            var c = new N("AnimationCurve");
            c.P(id, new RawStr(FbxName("", "AnimCurve")), "");
            c.Kid("Default").P((double)vals[0]);
            c.Kid("KeyVer").P(4008);
            c.Kid("KeyTime").P(times);
            c.Kid("KeyValueFloat").P(vals);
            c.Kid("KeyAttrFlags").P(new int[] { 24836 });
            c.Kid("KeyAttrDataFloat").P(new float[] { 0f, 0f, 218434821.1f, 0f });
            c.Kid("KeyAttrRefCount").P(new int[] { times.Length });
            parent.Kids.Add(c);
        }

        private static void Conn(N conns, string type, long child, long parent)
        { var c = conns.Kid("C"); c.P(type, child, parent); }
        private static void ConnP(N conns, string type, long child, long parent, string prop)
        { var c = conns.Kid("C"); c.P(type, child, parent, prop); }
    }
}
