using Frosty.Core;
using FrostySdk;
using FrostySdk.Ebx;
using FrostySdk.IO;
using FrostySdk.Managers;
using MeshSetPlugin;
using MeshSetPlugin.Resources;
using System;
using System.Collections.Generic;
using System.Linq;

namespace DuplicationPlugin
{
    /// <summary>
    /// How the base stadium is read, following what a census of all 92 FIFA 17 stadium levels showed: the component
    /// folder comes from the MeshAsset paths of the main level (not from the folder name - the training levels use
    /// another folder), composites are placed either as StaticModelGroup members of the main level or through the
    /// WorldPart / Layer sublevel of their name (ObjectReferenceObjectData -> blueprint -> StaticModelEntityData),
    /// usually at identity but not always (26 levels translate playerbenches_mesh, mestalla_10 its roof), and the
    /// crowd point cloud is whatever the crowd schematic references (bernabeu_2: "bernabeau_crowd",
    /// boleynground_325: two clouds).
    /// </summary>
    public static class StadiumBaseLookup
    {
        public const string ComponentRoot = "content/worlds/components/stadiums/";

        /// <summary>Row-vector 4x4 (right, up, forward, trans) of a LinearTransform; identity when it cannot be read.</summary>
        public static float[] Matrix(dynamic lt)
        {
            float[] m = Identity();
            try
            {
                dynamic r = lt.right, u = lt.up, f = lt.forward, t = lt.trans;
                m[0] = (float)r.x; m[1] = (float)r.y; m[2] = (float)r.z;
                m[4] = (float)u.x; m[5] = (float)u.y; m[6] = (float)u.z;
                m[8] = (float)f.x; m[9] = (float)f.y; m[10] = (float)f.z;
                m[12] = (float)t.x; m[13] = (float)t.y; m[14] = (float)t.z;
            }
            catch (Exception) { }
            return m;
        }

        public static float[] Identity() => new float[] { 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1 };

        public static bool IsIdentity(float[] m, float eps = 1e-4f)
        {
            float[] id = Identity();
            for (int i = 0; i < 16; i++) if (Math.Abs(m[i] - id[i]) > eps) return false;
            return true;
        }

        /// <summary>Inverse of a rigid row-vector transform (rotation transposed, translation negated).</summary>
        public static float[] InverseRigid(float[] m)
        {
            float[] r = Identity();
            r[0] = m[0]; r[1] = m[4]; r[2] = m[8];
            r[4] = m[1]; r[5] = m[5]; r[6] = m[9];
            r[8] = m[2]; r[9] = m[6]; r[10] = m[10];
            float tx = m[12], ty = m[13], tz = m[14];
            r[12] = -(tx * r[0] + ty * r[4] + tz * r[8]);
            r[13] = -(tx * r[1] + ty * r[5] + tz * r[9]);
            r[14] = -(tx * r[2] + ty * r[6] + tz * r[10]);
            return r;
        }

        public static void TransformPoint(float[] m, ref float x, ref float y, ref float z)
        {
            float nx = x * m[0] + y * m[4] + z * m[8] + m[12];
            float ny = x * m[1] + y * m[5] + z * m[9] + m[13];
            float nz = x * m[2] + y * m[6] + z * m[10] + m[14];
            x = nx; y = ny; z = nz;
        }

        public static void TransformDir(float[] m, ref float x, ref float y, ref float z)
        {
            float nx = x * m[0] + y * m[4] + z * m[8];
            float ny = x * m[1] + y * m[5] + z * m[9];
            float nz = x * m[2] + y * m[6] + z * m[10];
            x = nx; y = ny; z = nz;
        }

        public static string Describe(float[] m)
        {
            bool rot = Math.Abs(m[0] - 1) > 1e-4f || Math.Abs(m[5] - 1) > 1e-4f || Math.Abs(m[10] - 1) > 1e-4f;
            return string.Format("trans ({0:0.000}, {1:0.000}, {2:0.000}){3}", m[12], m[13], m[14], rot ? " + rotation" : "");
        }

        /// <summary>The level asset of a stadium folder.</summary>
        public static EbxAssetEntry LevelEntry(string folder)
        {
            return App.AssetManager.GetEbxEntry("content/worlds/stadiums/" + folder + "/" + folder);
        }

        /// <summary>
        /// The component folder name (the "x" of content/worlds/components/stadiums/x/): the most frequent one among
        /// the StaticModelGroup members of the main level and the composite blueprints of the standslower / roof /
        /// railings sublevels; <paramref name="fallback"/> when nothing is found.
        /// </summary>
        public static string ResolveComponentFolder(string folder, string fallback)
        {
            Dictionary<string, int> votes = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            void Vote(string assetName)
            {
                string n = (assetName ?? "").Replace('\\', '/');
                if (!n.StartsWith(ComponentRoot, StringComparison.OrdinalIgnoreCase)) return;
                string rest = n.Substring(ComponentRoot.Length);
                int slash = rest.IndexOf('/');
                if (slash <= 0) return;
                string key = rest.Substring(0, slash);
                int c;
                votes.TryGetValue(key, out c);
                votes[key] = c + 1;
            }
            EbxAssetEntry level = LevelEntry(folder);
            if (level != null)
            {
                try
                {
                    EbxAsset asset = App.AssetManager.GetEbx(level);
                    foreach (object obj in asset.Objects)
                    {
                        if (obj == null || obj.GetType().Name != "StaticModelGroupEntityData") continue;
                        dynamic members = ((dynamic)obj).MemberDatas;
                        foreach (dynamic member in members)
                        {
                            PointerRef pr = member.MeshAsset;
                            if (pr.Type != PointerRefType.External) continue;
                            EbxAssetEntry e = App.AssetManager.GetEbxEntry(pr.External.FileGuid);
                            if (e != null) Vote(e.Name);
                        }
                    }
                }
                catch (Exception) { }
            }
            foreach (string stem in new[] { "standslower", "roof", "railings", "standsupper", "pitch" })
            {
                EbxAssetEntry sub = App.AssetManager.GetEbxEntry("content/worlds/stadiums/" + folder + "/" + stem);
                if (sub == null) continue;
                try
                {
                    EbxAsset asset = App.AssetManager.GetEbx(sub);
                    foreach (object obj in asset.Objects)
                    {
                        if (obj == null || obj.GetType().Name != "ObjectReferenceObjectData") continue;
                        PointerRef pr = ((dynamic)obj).Blueprint;
                        if (pr.Type != PointerRefType.External) continue;
                        EbxAssetEntry e = App.AssetManager.GetEbxEntry(pr.External.FileGuid);
                        if (e != null) Vote(e.Name);
                    }
                }
                catch (Exception) { }
            }
            if (votes.Count == 0) return fallback;
            return votes.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key).First().Key;
        }

        /// <summary>
        /// Where the base stadium places a component mesh: the first InstanceTransforms of the StaticModelGroup member
        /// that uses it (main level, then every sublevel of the folder), else the BlueprintTransform of the
        /// ObjectReferenceObjectData whose blueprint is the mesh's blueprint (mesh "x_mesh" -> blueprint "x").
        /// Null when the mesh is not placed by the stadium at all.
        /// </summary>
        public static float[] Placement(string folder, EbxAssetEntry meshEntry, out string placedBy)
        {
            placedBy = null;
            string prefix = "content/worlds/stadiums/" + folder + "/";
            List<EbxAssetEntry> worlds = new List<EbxAssetEntry>();
            EbxAssetEntry level = LevelEntry(folder);
            if (level != null) worlds.Add(level);
            foreach (EbxAssetEntry e in App.AssetManager.EnumerateEbx())
            {
                if (!e.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || e == level) continue;
                if (e.Type != "WorldPartData" && e.Type != "LayerData" && e.Type != "SubWorldData") continue;
                string stem = e.Name.Substring(prefix.Length);
                if (stem.Contains("/") || stem.IndexOf("crowd", StringComparison.OrdinalIgnoreCase) >= 0 || stem.IndexOf("lighting", StringComparison.OrdinalIgnoreCase) >= 0 || stem.IndexOf("effects", StringComparison.OrdinalIgnoreCase) >= 0 || stem.IndexOf("schematic", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                worlds.Add(e);
            }
            string meshName = meshEntry.Name.Replace('\\', '/');
            string blueprintName = meshName.EndsWith("_mesh", StringComparison.OrdinalIgnoreCase) ? meshName.Substring(0, meshName.Length - 5) : null;
            foreach (EbxAssetEntry w in worlds)
            {
                EbxAsset asset;
                try { asset = App.AssetManager.GetEbx(w); } catch (Exception) { continue; }
                if (asset == null) continue;
                foreach (object obj in asset.Objects)
                {
                    if (obj == null) continue;
                    string type = obj.GetType().Name;
                    if (type == "StaticModelGroupEntityData")
                    {
                        dynamic members;
                        try { members = ((dynamic)obj).MemberDatas; } catch (Exception) { continue; }
                        foreach (dynamic member in members)
                        {
                            PointerRef pr = member.MeshAsset;
                            if (pr.Type != PointerRefType.External || pr.External.FileGuid != meshEntry.Guid) continue;
                            dynamic transforms = member.InstanceTransforms;
                            if (transforms.Count == 0) continue;
                            placedBy = w.Name + " (group member)";
                            return Matrix(transforms[0]);
                        }
                    }
                    else if (type == "ObjectReferenceObjectData" && blueprintName != null)
                    {
                        PointerRef bp;
                        try { bp = ((dynamic)obj).Blueprint; } catch (Exception) { continue; }
                        if (bp.Type != PointerRefType.External) continue;
                        EbxAssetEntry bpEntry = App.AssetManager.GetEbxEntry(bp.External.FileGuid);
                        if (bpEntry == null || !bpEntry.Name.Replace('\\', '/').Equals(blueprintName, StringComparison.OrdinalIgnoreCase)) continue;
                        placedBy = w.Name + " (blueprint reference)";
                        return Matrix(((dynamic)obj).BlueprintTransform);
                    }
                }
            }
            return null;
        }

        /// <summary>Every CrowdPlacementPointCloudDataAsset the stadium's crowd placement entities reference, in order.</summary>
        public static List<EbxAssetEntry> CrowdPointClouds(string folder)
        {
            List<EbxAssetEntry> result = new List<EbxAssetEntry>();
            string prefix = "content/worlds/stadiums/" + folder + "/";
            foreach (EbxAssetEntry e in App.AssetManager.EnumerateEbx())
            {
                if (!e.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                if (e.Type != "LayerData" && e.Type != "SubWorldData" && e.Type != "WorldPartData" && e.Type != "DetachedSubWorldData") continue;
                if (e.Name.IndexOf("crowd", StringComparison.OrdinalIgnoreCase) < 0) continue;
                EbxAsset asset;
                try { asset = App.AssetManager.GetEbx(e); } catch (Exception) { continue; }
                if (asset == null) continue;
                foreach (object obj in asset.Objects)
                {
                    if (obj == null || obj.GetType().Name != "StadiumCrowdPlacementEntityData") continue;
                    PointerRef pr;
                    try { pr = ((dynamic)obj).PointCloudAsset; } catch (Exception) { continue; }
                    if (pr.Type != PointerRefType.External) continue;
                    EbxAssetEntry cloud = App.AssetManager.GetEbxEntry(pr.External.FileGuid);
                    if (cloud != null && !result.Contains(cloud)) result.Add(cloud);
                }
            }
            return result;
        }

        /// <summary>The composite / rigid mesh assets under a component folder.</summary>
        public static List<EbxAssetEntry> ComponentMeshes(string componentFolder)
        {
            string prefix = ComponentRoot + componentFolder.ToLower() + "/";
            List<EbxAssetEntry> result = new List<EbxAssetEntry>();
            foreach (EbxAssetEntry e in App.AssetManager.EnumerateEbx())
            {
                if (!e.Name.Replace('\\', '/').ToLower().StartsWith(prefix)) continue;
                if (e.Type == "CompositeMeshAsset" || e.Type == "RigidMeshAsset" || e.Type == "StaticMeshAsset")
                    result.Add(e);
            }
            result.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
            return result;
        }
    }
}
