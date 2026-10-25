using System;
using System.Collections.Generic;
using System.Linq;
using FrostyAnimationEditor.Core.Anim;

namespace FrostyAnimationEditor.Core.Fbx
{
    /// <summary>
    /// Writes a skeleton, a clip on it and optionally skinned meshes as a binary FBX 7.4 file (the layout Blender's
    /// exporter writes): Y up, right-handed (X coord, Z front), 1 unit = 1 m (UnitScaleFactor 100), Euler XYZ in
    /// degrees, one key per frame, linear interpolation. The bind pose (Pose "BindPose" and the clusters) is the
    /// skeleton's bind pose; the node values (Lcl) are frame 0.
    /// </summary>
    public static class FbxExport
    {
        private sealed class Ids
        {
            private long next = 1000000;
            public long New() { return next++; }
        }

        private static FbxNode P(string name, string type, string label, string flags, params object[] vals)
        {
            FbxNode p = new FbxNode("P", name, type, label, flags);
            if (vals != null) p.Props.AddRange(vals);
            return p;
        }

        private static FbxNode Header(string creator)
        {
            FbxNode h = new FbxNode("FBXHeaderExtension");
            h.Add("FBXHeaderVersion", 1003);
            h.Add("FBXVersion", 7400);
            h.Add("EncryptionType", 0);
            FbxNode ts = h.Add("CreationTimeStamp");
            DateTime now = DateTime.Now;
            ts.Add("Version", 1000);
            ts.Add("Year", now.Year); ts.Add("Month", now.Month); ts.Add("Day", now.Day);
            ts.Add("Hour", now.Hour); ts.Add("Minute", now.Minute); ts.Add("Second", now.Second); ts.Add("Millisecond", now.Millisecond);
            h.Add("Creator", creator);
            FbxNode si = h.Add("SceneInfo", FbxNode.NameClass("GlobalInfo", "SceneInfo"), "UserData");
            si.Add("Type", "UserData");
            si.Add("Version", 100);
            FbxNode md = si.Add("MetaData");
            md.Add("Version", 100);
            foreach (string k in new[] { "Title", "Subject", "Author", "Keywords", "Revision", "Comment" }) md.Add(k, "");
            FbxNode pr = si.Add("Properties70");
            pr.Add(P("DocumentUrl", "KString", "Url", "", "/foobar.fbx"));
            pr.Add(P("SrcDocumentUrl", "KString", "Url", "", "/foobar.fbx"));
            pr.Add(P("Original", "Compound", "", ""));
            pr.Add(P("Original|ApplicationVendor", "KString", "", "", "FrostyAnimationEditor"));
            pr.Add(P("Original|ApplicationName", "KString", "", "", creator));
            pr.Add(P("Original|ApplicationVersion", "KString", "", "", "1.1"));
            pr.Add(P("Original|DateTime_GMT", "DateTime", "", "", "01/01/1970 00:00:00.000"));
            pr.Add(P("Original|FileName", "KString", "", "", "/foobar.fbx"));
            pr.Add(P("LastSaved", "Compound", "", ""));
            pr.Add(P("LastSaved|ApplicationVendor", "KString", "", "", "FrostyAnimationEditor"));
            pr.Add(P("LastSaved|ApplicationName", "KString", "", "", creator));
            pr.Add(P("LastSaved|ApplicationVersion", "KString", "", "", "1.1"));
            pr.Add(P("LastSaved|DateTime_GMT", "DateTime", "", "", "01/01/1970 00:00:00.000"));
            return h;
        }

        private static int TimeMode(double fps)
        {
            double[] f = { 120, 100, 60, 50, 48, 30, 25, 24, 96, 72 };
            int[] m = { 1, 2, 3, 4, 5, 6, 10, 11, 15, 16 };
            for (int i = 0; i < f.Length; i++) if (Math.Abs(fps - f[i]) < 1e-6) return m[i];
            return 14;
        }

        public static long KTime(int frame, double fps) { return (long)Math.Round(frame * FbxScene.KTimePerSecond / fps); }

        private static FbxNode ModelTemplate()
        {
            FbxNode p = new FbxNode("Properties70");
            p.Add(P("QuaternionInterpolate", "enum", "", "", 0));
            foreach (string v in new[] { "RotationOffset", "RotationPivot", "ScalingOffset", "ScalingPivot" }) p.Add(P(v, "Vector3D", "Vector", "", 0.0, 0.0, 0.0));
            p.Add(P("TranslationActive", "bool", "", "", 0));
            p.Add(P("TranslationMin", "Vector3D", "Vector", "", 0.0, 0.0, 0.0));
            p.Add(P("TranslationMax", "Vector3D", "Vector", "", 0.0, 0.0, 0.0));
            foreach (string v in new[] { "TranslationMinX", "TranslationMinY", "TranslationMinZ", "TranslationMaxX", "TranslationMaxY", "TranslationMaxZ" }) p.Add(P(v, "bool", "", "", 0));
            p.Add(P("RotationOrder", "enum", "", "", 0));
            p.Add(P("RotationSpaceForLimitOnly", "bool", "", "", 0));
            foreach (string v in new[] { "RotationStiffnessX", "RotationStiffnessY", "RotationStiffnessZ" }) p.Add(P(v, "double", "Number", "", 0.0));
            p.Add(P("AxisLen", "double", "Number", "", 10.0));
            p.Add(P("PreRotation", "Vector3D", "Vector", "", 0.0, 0.0, 0.0));
            p.Add(P("PostRotation", "Vector3D", "Vector", "", 0.0, 0.0, 0.0));
            p.Add(P("RotationActive", "bool", "", "", 0));
            p.Add(P("RotationMin", "Vector3D", "Vector", "", 0.0, 0.0, 0.0));
            p.Add(P("RotationMax", "Vector3D", "Vector", "", 0.0, 0.0, 0.0));
            foreach (string v in new[] { "RotationMinX", "RotationMinY", "RotationMinZ", "RotationMaxX", "RotationMaxY", "RotationMaxZ" }) p.Add(P(v, "bool", "", "", 0));
            p.Add(P("InheritType", "enum", "", "", 0));
            p.Add(P("ScalingActive", "bool", "", "", 0));
            p.Add(P("ScalingMin", "Vector3D", "Vector", "", 0.0, 0.0, 0.0));
            p.Add(P("ScalingMax", "Vector3D", "Vector", "", 1.0, 1.0, 1.0));
            foreach (string v in new[] { "ScalingMinX", "ScalingMinY", "ScalingMinZ", "ScalingMaxX", "ScalingMaxY", "ScalingMaxZ" }) p.Add(P(v, "bool", "", "", 0));
            p.Add(P("GeometricTranslation", "Vector3D", "Vector", "", 0.0, 0.0, 0.0));
            p.Add(P("GeometricRotation", "Vector3D", "Vector", "", 0.0, 0.0, 0.0));
            p.Add(P("GeometricScaling", "Vector3D", "Vector", "", 1.0, 1.0, 1.0));
            foreach (string a in new[] { "MinDampRange", "MaxDampRange", "MinDampStrength", "MaxDampStrength", "PreferedAngle" })
                foreach (string x in new[] { "X", "Y", "Z" }) p.Add(P(a + x, "double", "Number", "", 0.0));
            p.Add(P("LookAtProperty", "object", "", ""));
            p.Add(P("UpVectorProperty", "object", "", ""));
            p.Add(P("Show", "bool", "", "", 1));
            p.Add(P("NegativePercentShapeSupport", "bool", "", "", 1));
            p.Add(P("DefaultAttributeIndex", "int", "Integer", "", -1));
            p.Add(P("Freeze", "bool", "", "", 0));
            p.Add(P("LODBox", "bool", "", "", 0));
            p.Add(P("Lcl Translation", "Lcl Translation", "", "A", 0.0, 0.0, 0.0));
            p.Add(P("Lcl Rotation", "Lcl Rotation", "", "A", 0.0, 0.0, 0.0));
            p.Add(P("Lcl Scaling", "Lcl Scaling", "", "A", 1.0, 1.0, 1.0));
            p.Add(P("Visibility", "Visibility", "", "A", 1.0));
            p.Add(P("Visibility Inheritance", "Visibility Inheritance", "", "", 1));
            return p;
        }

        private static FbxNode Definitions(int models, int attrs, int geoms, int deformers, int poses, int curveNodes, int curves)
        {
            FbxNode d = new FbxNode("Definitions");
            d.Add("Version", 100);
            int count = 1 + models + attrs + geoms + deformers + poses + 2 + curveNodes + curves;
            d.Add("Count", count);
            d.Add("ObjectType", "GlobalSettings").Add("Count", 1);
            d.Add("ObjectType", "NodeAttribute").Add("Count", attrs);
            if (geoms > 0)
            {
                FbxNode g = d.Add("ObjectType", "Geometry");
                g.Add("Count", geoms);
                FbxNode t = g.Add("PropertyTemplate", "FbxMesh").Add("Properties70");
                t.Add(P("Color", "ColorRGB", "Color", "", 0.8, 0.8, 0.8));
                t.Add(P("BBoxMin", "Vector3D", "Vector", "", 0.0, 0.0, 0.0));
                t.Add(P("BBoxMax", "Vector3D", "Vector", "", 0.0, 0.0, 0.0));
                t.Add(P("Primary Visibility", "bool", "", "", 1));
                t.Add(P("Casts Shadows", "bool", "", "", 1));
                t.Add(P("Receive Shadows", "bool", "", "", 1));
            }
            FbxNode m = d.Add("ObjectType", "Model");
            m.Add("Count", models);
            m.Add("PropertyTemplate", "FbxNode").Add(ModelTemplate());
            if (poses > 0) d.Add("ObjectType", "Pose").Add("Count", poses);
            if (deformers > 0) d.Add("ObjectType", "Deformer").Add("Count", deformers);
            FbxNode s = d.Add("ObjectType", "AnimationStack");
            s.Add("Count", 1);
            FbxNode st = s.Add("PropertyTemplate", "FbxAnimStack").Add("Properties70");
            st.Add(P("Description", "KString", "", "", ""));
            foreach (string v in new[] { "LocalStart", "LocalStop", "ReferenceStart", "ReferenceStop" }) st.Add(P(v, "KTime", "Time", "", 0L));
            FbxNode l = d.Add("ObjectType", "AnimationLayer");
            l.Add("Count", 1);
            FbxNode lt = l.Add("PropertyTemplate", "FbxAnimLayer").Add("Properties70");
            lt.Add(P("Weight", "Number", "", "A", 100.0));
            lt.Add(P("Mute", "bool", "", "", 0)); lt.Add(P("Solo", "bool", "", "", 0)); lt.Add(P("Lock", "bool", "", "", 0));
            lt.Add(P("Color", "ColorRGB", "Color", "", 0.8, 0.8, 0.8));
            lt.Add(P("BlendMode", "enum", "", "", 0));
            lt.Add(P("RotationAccumulationMode", "enum", "", "", 0));
            lt.Add(P("ScaleAccumulationMode", "enum", "", "", 0));
            lt.Add(P("BlendModeBypass", "ULongLong", "", "", 0L));
            FbxNode cn = d.Add("ObjectType", "AnimationCurveNode");
            cn.Add("Count", curveNodes);
            cn.Add("PropertyTemplate", "FbxAnimCurveNode").Add("Properties70").Add(P("d", "Compound", "", ""));
            d.Add("ObjectType", "AnimationCurve").Add("Count", curves);
            return d;
        }

        private static FbxNode ModelNode(long id, string name, string sub, V3 t, V3 r, V3 s, bool animated)
        {
            FbxNode m = new FbxNode("Model", id, FbxNode.NameClass(name, "Model"), sub);
            m.Add("Version", 232);
            FbxNode p = m.Add("Properties70");
            string fl = animated ? "A+" : "A";
            p.Add(P("Lcl Translation", "Lcl Translation", "", fl, t.X, t.Y, t.Z));
            p.Add(P("Lcl Rotation", "Lcl Rotation", "", fl, r.X, r.Y, r.Z));
            p.Add(P("Lcl Scaling", "Lcl Scaling", "", fl, s.X, s.Y, s.Z));
            p.Add(P("DefaultAttributeIndex", "int", "Integer", "", 0));
            p.Add(P("InheritType", "enum", "", "", 1));
            m.Add("MultiLayer", 0);
            m.Add("MultiTake", 0);
            m.Add("Shading", new FbxChar(1));
            m.Add("Culling", "CullingOff");
            return m;
        }

        private static void Conn(FbxNode conns, long src, long dst, string prop = null)
        {
            if (prop == null) conns.Add("C", "OO", src, dst);
            else conns.Add("C", "OP", src, dst, prop);
        }

        /// <summary>the FBX file: the animation's skeleton (bind pose), every bone the clip poses keyed at every frame,
        /// and the meshes (may be null or empty)</summary>
        public static byte[] Write(PlayerAnimation a, IList<MeshPart> meshes, string takeName, string creator = "FIFA 17 Animation Editor")
        {
            PlayerSkeleton sk = a.Skeleton;
            int nb = sk.Count, nf = Math.Max(1, a.FrameCount);
            double fps = a.Fps > 0 ? a.Fps : 30;
            meshes = meshes ?? new List<MeshPart>();
            Ids ids = new Ids();
            long[] boneId = new long[nb], attrId = new long[nb];
            for (int b = 0; b < nb; b++) { boneId[b] = ids.New(); attrId[b] = ids.New(); }

            // Euler curves per bone (continuous)
            bool[] keyed = new bool[nb];
            for (int b = 0; b < nb; b++) keyed[b] = a.Posed != null && b < a.Posed.Length && a.Posed[b];
            V3[][] tr = new V3[nb][], ro = new V3[nb][], sc = new V3[nb][];
            for (int b = 0; b < nb; b++)
            {
                tr[b] = new V3[nf]; ro[b] = new V3[nf]; sc[b] = new V3[nf];
                V3 prev = V3.Zero;
                for (int f = 0; f < nf; f++)
                {
                    Affine l = a.Local != null && f < a.Local.Length ? a.Local[f][b] : sk.Local[b];
                    V3 t, s;
                    Q4 q;
                    l.Decompose(out t, out q, out s);
                    V3 e = Euler.FromQuatXYZ(q);
                    if (f > 0) e = Euler.Closest(e, prev);
                    prev = e;
                    tr[b][f] = t; ro[b][f] = e; sc[b][f] = s;
                }
            }

            List<FbxNode> top = new List<FbxNode>();
            top.Add(Header(creator));
            top.Add(new FbxNode("FileId", (object)FbxIO.FileId.Clone()));
            top.Add(new FbxNode("CreationTime", FbxIO.CreationTime));
            top.Add(new FbxNode("Creator", creator));
            FbxNode gs = new FbxNode("GlobalSettings");
            gs.Add("Version", 1000);
            FbxNode gp = gs.Add("Properties70");
            gp.Add(P("UpAxis", "int", "Integer", "", 1)); gp.Add(P("UpAxisSign", "int", "Integer", "", 1));
            gp.Add(P("FrontAxis", "int", "Integer", "", 2)); gp.Add(P("FrontAxisSign", "int", "Integer", "", 1));
            gp.Add(P("CoordAxis", "int", "Integer", "", 0)); gp.Add(P("CoordAxisSign", "int", "Integer", "", 1));
            gp.Add(P("OriginalUpAxis", "int", "Integer", "", 1)); gp.Add(P("OriginalUpAxisSign", "int", "Integer", "", 1));
            gp.Add(P("UnitScaleFactor", "double", "Number", "", 100.0));
            gp.Add(P("OriginalUnitScaleFactor", "double", "Number", "", 100.0));
            gp.Add(P("AmbientColor", "ColorRGB", "Color", "", 0.0, 0.0, 0.0));
            gp.Add(P("DefaultCamera", "KString", "", "", "Producer Perspective"));
            gp.Add(P("TimeMode", "enum", "", "", TimeMode(fps)));
            gp.Add(P("TimeSpanStart", "KTime", "Time", "", 0L));
            gp.Add(P("TimeSpanStop", "KTime", "Time", "", KTime(nf - 1, fps)));
            gp.Add(P("CustomFrameRate", "double", "Number", "", fps));
            top.Add(gs);
            FbxNode docs = new FbxNode("Documents");
            docs.Add("Count", 1);
            FbxNode doc = docs.Add("Document", ids.New(), "Scene", "Scene");
            FbxNode dp = doc.Add("Properties70");
            dp.Add(P("SourceObject", "object", "", ""));
            dp.Add(P("ActiveAnimStackName", "KString", "", "", takeName));
            doc.Add("RootNode", 0L);
            top.Add(docs);
            top.Add(new FbxNode("References"));

            FbxNode objs = new FbxNode("Objects");
            FbxNode conns = new FbxNode("Connections");
            int nModels = 0, nAttrs = 0, nGeoms = 0, nDeformers = 0, nPoses = 0, nCurveNodes = 0, nCurves = 0;

            // bones
            for (int b = 0; b < nb; b++)
            {
                FbxNode na = objs.Add("NodeAttribute", attrId[b], FbxNode.NameClass(sk.Bones[b], "NodeAttribute"), "LimbNode");
                na.Add("TypeFlags", "Skeleton");
                na.Add("Properties70").Add(P("Size", "double", "Number", "", 3.3));
                nAttrs++;
            }
            for (int b = 0; b < nb; b++)
            {
                objs.Add(ModelNode(boneId[b], sk.Bones[b], "LimbNode", tr[b][0], ro[b][0], sc[b][0], keyed[b]));
                nModels++;
            }
            foreach (int b in sk.TopoOrder())
            {
                int p = sk.Parents[b];
                Conn(conns, boneId[b], p >= 0 ? boneId[p] : 0L);
                Conn(conns, attrId[b], boneId[b]);
            }

            // meshes: geometry, model, skin, clusters
            List<Tuple<long, Affine>> poseNodes = new List<Tuple<long, Affine>>();
            for (int b = 0; b < nb; b++) poseNodes.Add(Tuple.Create(boneId[b], sk.Model[b]));
            foreach (MeshPart mp in meshes)
            {
                int nv = mp.VertexCount;
                if (nv == 0 || mp.Triangles == null || mp.Triangles.Length < 3) continue;
                long gid = ids.New(), mid = ids.New(), skinId = ids.New();
                FbxNode g = objs.Add("Geometry", gid, FbxNode.NameClass(mp.Name, "Geometry"), "Mesh");
                g.Add("Properties70");
                g.Add("GeometryVersion", 124);
                g.Add("Vertices", (object)mp.Positions.Select(x => (double)x).ToArray());
                int nt = mp.Triangles.Length / 3;
                int[] pvi = new int[nt * 3];
                // FBX front faces are counter-clockwise: keep the winding that agrees with the normals
                bool flip = !mp.WindingMatchesNormals();
                for (int i = 0; i < nt; i++)
                {
                    int ta = mp.Triangles[i * 3], tb = mp.Triangles[i * 3 + 1], tc = mp.Triangles[i * 3 + 2];
                    if (flip) { int x = tb; tb = tc; tc = x; }
                    pvi[i * 3] = ta;
                    pvi[i * 3 + 1] = tb;
                    pvi[i * 3 + 2] = ~tc;
                }
                g.Add("PolygonVertexIndex", (object)pvi);
                FbxNode layer = new FbxNode("Layer", 0);
                layer.Add("Version", 100);
                if (mp.Normals != null && mp.Normals.Length == nv * 3)
                {
                    FbxNode ln = g.Add("LayerElementNormal", 0);
                    ln.Add("Version", 101);
                    ln.Add("Name", "");
                    ln.Add("MappingInformationType", "ByVertice");
                    ln.Add("ReferenceInformationType", "Direct");
                    ln.Add("Normals", (object)mp.Normals.Select(x => (double)x).ToArray());
                    FbxNode le = layer.Add("LayerElement");
                    le.Add("Type", "LayerElementNormal");
                    le.Add("TypedIndex", 0);
                }
                if (mp.Uvs != null && mp.Uvs.Length == nv * 2)
                {
                    FbxNode lu = g.Add("LayerElementUV", 0);
                    lu.Add("Version", 101);
                    lu.Add("Name", "UVMap");
                    lu.Add("MappingInformationType", "ByVertice");
                    lu.Add("ReferenceInformationType", "Direct");
                    double[] uv = new double[nv * 2];
                    for (int i = 0; i < nv; i++) { uv[i * 2] = mp.Uvs[i * 2]; uv[i * 2 + 1] = 1.0 - mp.Uvs[i * 2 + 1]; }
                    lu.Add("UV", (object)uv);
                    FbxNode le = layer.Add("LayerElement");
                    le.Add("Type", "LayerElementUV");
                    le.Add("TypedIndex", 0);
                }
                g.Add(layer);
                nGeoms++;
                objs.Add(ModelNode(mid, mp.Name, "Mesh", V3.Zero, V3.Zero, V3.One, false));
                nModels++;
                Conn(conns, mid, 0L);
                Conn(conns, gid, mid);
                poseNodes.Add(Tuple.Create(mid, Affine.Identity));

                // weights per bone
                Dictionary<int, List<Tuple<int, double>>> perBone = new Dictionary<int, List<Tuple<int, double>>>();
                int inf = mp.Influences;
                for (int v = 0; v < nv && inf > 0; v++)
                    for (int k = 0; k < inf; k++)
                    {
                        int bi = mp.BoneIndices[v * inf + k];
                        float w = mp.BoneWeights[v * inf + k];
                        if (bi < 0 || bi >= nb || w <= 0) continue;
                        List<Tuple<int, double>> l;
                        if (!perBone.TryGetValue(bi, out l)) { l = new List<Tuple<int, double>>(); perBone[bi] = l; }
                        l.Add(Tuple.Create(v, (double)w));
                    }
                if (perBone.Count == 0) continue;
                FbxNode skin = objs.Add("Deformer", skinId, FbxNode.NameClass(mp.Name, "Deformer"), "Skin");
                skin.Add("Version", 101);
                skin.Add("Link_DeformAcuracy", 50.0);
                nDeformers++;
                Conn(conns, skinId, gid);
                foreach (KeyValuePair<int, List<Tuple<int, double>>> kv in perBone.OrderBy(x => x.Key))
                {
                    long cid = ids.New();
                    FbxNode cl = objs.Add("Deformer", cid, FbxNode.NameClass(sk.Bones[kv.Key], "SubDeformer"), "Cluster");
                    cl.Add("Version", 100);
                    cl.Add("UserData", "", "");
                    cl.Add("Indexes", (object)kv.Value.Select(x => x.Item1).ToArray());
                    cl.Add("Weights", (object)kv.Value.Select(x => x.Item2).ToArray());
                    Affine link = sk.Model[kv.Key];
                    cl.Add("Transform", (object)link.Inverse().ToFbx());
                    cl.Add("TransformLink", (object)link.ToFbx());
                    nDeformers++;
                    Conn(conns, cid, skinId);
                    Conn(conns, boneId[kv.Key], cid);
                }
            }

            // bind pose
            FbxNode pose = objs.Add("Pose", ids.New(), FbxNode.NameClass("BindPose", "Pose"), "BindPose");
            pose.Add("Type", "BindPose");
            pose.Add("Version", 100);
            pose.Add("NbPoseNodes", poseNodes.Count);
            foreach (Tuple<long, Affine> pn in poseNodes)
            {
                FbxNode n = pose.Add("PoseNode");
                n.Add("Node", pn.Item1);
                n.Add("Matrix", (object)pn.Item2.ToFbx());
            }
            nPoses++;

            // animation
            long stop = KTime(nf - 1, fps);
            long stackId = ids.New(), layerId = ids.New();
            FbxNode stack = objs.Add("AnimationStack", stackId, FbxNode.NameClass(takeName, "AnimStack"), "");
            FbxNode sp = stack.Add("Properties70");
            sp.Add(P("LocalStop", "KTime", "Time", "", stop));
            sp.Add(P("ReferenceStop", "KTime", "Time", "", stop));
            objs.Add("AnimationLayer", layerId, FbxNode.NameClass("BaseLayer", "AnimLayer"), "");
            Conn(conns, layerId, stackId);
            long[] times = Enumerable.Range(0, nf).Select(f => KTime(f, fps)).ToArray();
            int flags = (1 << 2) | (1 << 8) | (1 << 13) | (1 << 14);
            float attr2 = BitConverter.ToSingle(BitConverter.GetBytes(0x0F3F0F3F), 0);
            for (int b = 0; b < nb; b++)
            {
                if (!keyed[b]) continue;
                string[] props = { "Lcl Translation", "Lcl Rotation", "Lcl Scaling" };
                string[] short_ = { "T", "R", "S" };
                V3[][] series = { tr[b], ro[b], sc[b] };
                for (int k = 0; k < 3; k++)
                {
                    long cnId = ids.New();
                    V3 d0 = series[k][0];
                    FbxNode cn = objs.Add("AnimationCurveNode", cnId, FbxNode.NameClass(short_[k], "AnimCurveNode"), "");
                    FbxNode cp = cn.Add("Properties70");
                    cp.Add(P("d|X", "Number", "", "A", d0.X));
                    cp.Add(P("d|Y", "Number", "", "A", d0.Y));
                    cp.Add(P("d|Z", "Number", "", "A", d0.Z));
                    nCurveNodes++;
                    Conn(conns, cnId, layerId);
                    Conn(conns, cnId, boneId[b], props[k]);
                    for (int c = 0; c < 3; c++)
                    {
                        long cuId = ids.New();
                        float[] vals = series[k].Select(v => (float)v[c]).ToArray();
                        FbxNode cu = objs.Add("AnimationCurve", cuId, FbxNode.NameClass("", "AnimCurve"), "");
                        cu.Add("Default", (double)vals[0]);
                        cu.Add("KeyVer", 4008);
                        cu.Add("KeyTime", (object)times);
                        cu.Add("KeyValueFloat", (object)vals);
                        cu.Add("KeyAttrFlags", (object)new[] { flags });
                        cu.Add("KeyAttrDataFloat", (object)new[] { 0f, 0f, attr2, 0f });
                        cu.Add("KeyAttrRefCount", (object)new[] { nf });
                        nCurves++;
                        Conn(conns, cuId, cnId, c == 0 ? "d|X" : c == 1 ? "d|Y" : "d|Z");
                    }
                }
            }

            top.Add(Definitions(nModels, nAttrs, nGeoms, nDeformers, nPoses, nCurveNodes, nCurves));
            top.Add(objs);
            top.Add(conns);
            FbxNode takes = new FbxNode("Takes");
            takes.Add("Current", takeName);
            FbxNode take = takes.Add("Take", takeName);
            take.Add("FileName", takeName + ".tak");
            take.Add("LocalTime", 0L, stop);
            take.Add("ReferenceTime", 0L, stop);
            top.Add(takes);
            return FbxIO.WriteBinary(top, 7400);
        }
    }
}
