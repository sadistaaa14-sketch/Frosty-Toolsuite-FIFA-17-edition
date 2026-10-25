using Frosty.Core;
using FrostySdk;
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
    /// The exterior of a FIFA 17 stadium is thinner than it looks: generic_158's "city skirt" is a painted city
    /// wall 160-190 m out (24 triangles, y -7.6..35) plus two backdrop panels, and the only ground outside the
    /// stands is the plaza plate of exterior_facade_mesh at y -7.64 - there is no terrain. A PES package without
    /// an exterior layer (Pisa) brings trees, buildings and parked cars at y 0 but no ground under them, so with
    /// the facade and the skirt emptied the pre-match flyover shows them floating in the sky. Two measures:
    ///  - the backdrop meshes (city skirt, background / midground panels) are kept unless the package's own
    ///    exterior reaches out to them (Guadalajara's cover spans 3 km, Prostejov's 1.2 km: then they would
    ///    stand inside the PES landscape and are emptied as before);
    ///  - when no package slot fills the exterior, a flat ground plane at y -0.05 covering the backdrop's extent
    ///    is written into exterior_facade_mesh (its own concrete material, 1 repeat per 6 m), so the PES
    ///    surroundings stand on something and the horizon is the painted wall, as on the original.
    /// </summary>
    public partial class StadiumImporter
    {
        /// <summary>Beyond this distance from the centre (m) a package slot is taken to be a landscape of its own.</summary>
        private const float BackdropReach = 150f;
        private const float GroundPlaneY = -0.05f;
        private const float GroundPlaneTextureRepeat = 6f;

        private static readonly string[] BackdropStems = { "cityskirt", "exterior_background", "exterior_midground", "skyline", "exterior_far" };

        private static bool IsBackdropMesh(string stem)
        {
            string s = stem.ToLowerInvariant();
            foreach (string b in BackdropStems) if (s.Contains(b)) return true;
            return false;
        }

        /// <summary>The largest |x| / |z| any non-overlay package slot reaches (m), from the package's slot bounds.</summary>
        private float PackageReach()
        {
            float reach = 0;
            foreach (PesStadiumPackage.SlotInfo slot in pkg.Slots)
            {
                if (slot.Overlay || !settings.SlotTargets.ContainsKey(slot.Name) || string.IsNullOrEmpty(settings.SlotTargets[slot.Name])) continue;
                if (slot.Bounds == null || slot.Bounds.Length < 6) continue;
                reach = Math.Max(reach, Math.Max(Math.Abs(slot.Bounds[0]), Math.Abs(slot.Bounds[3])));
                reach = Math.Max(reach, Math.Max(Math.Abs(slot.Bounds[2]), Math.Abs(slot.Bounds[5])));
            }
            return reach;
        }

        private bool ExteriorSlotMapped()
        {
            foreach (KeyValuePair<string, string> kv in settings.SlotTargets)
                if (string.Equals(kv.Key, "exterior", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(kv.Value)) return true;
            return false;
        }

        private bool backdropCleared;

        /// <summary>Empties the backdrop meshes when asked or when the package's exterior reaches them; returns what was cleared.</summary>
        private List<string> ClearBackdrop()
        {
            List<string> cleared = new List<string>();
            float reach = PackageReach();
            bool clear = settings.ClearCitySkirt || reach > BackdropReach;
            List<EbxAssetEntry> backdrops = StadiumBaseLookup.ComponentMeshes(settings.StadiumNameOnly).Where(e => IsBackdropMesh(e.Filename)).ToList();
            if (backdrops.Count == 0)
            {
                Log("  backdrop: no city skirt / background mesh in {0}", settings.StadiumNameOnly);
                return cleared;
            }
            if (!clear)
            {
                Log("  backdrop kept: {0} (the package reaches {1:0} m from the centre, the backdrop starts beyond {2:0} m)", string.Join(", ", backdrops.Select(e => e.Filename)), reach, BackdropReach);
                return cleared;
            }
            Log("  backdrop emptied: {0} ({1})", string.Join(", ", backdrops.Select(e => e.Filename)), settings.ClearCitySkirt ? "option" : string.Format("the package's exterior reaches {0:0} m, the backdrop would stand inside it", reach));
            foreach (EbxAssetEntry e in backdrops)
                if (ClearComponentMeshEntry(e)) cleared.Add(e.Filename);
            backdropCleared = true;
            return cleared;
        }

        /// <summary>
        /// A flat ground plane under the PES surroundings, written into exterior_facade_mesh, when no package slot
        /// fills the exterior and the backdrop stays.
        /// </summary>
        private void BuildExteriorGroundPlane()
        {
            if (ExteriorSlotMapped())
            {
                Log("  ground plane: the package fills the exterior, none added");
                return;
            }
            if (backdropCleared)
            {
                Log("  ground plane: the package's exterior reaches beyond the backdrop, none added");
                return;
            }
            EbxAssetEntry facade = null;
            foreach (EbxAssetEntry e in StadiumBaseLookup.ComponentMeshes(settings.StadiumNameOnly))
            {
                string stem = e.Filename.ToLowerInvariant();
                if (!stem.StartsWith("exterior_facade")) continue;
                if (facade == null || e.Filename.Length < facade.Filename.Length) facade = e;
            }
            if (facade == null)
            {
                Log("  ground plane: no exterior_facade mesh in {0}, none added", settings.StadiumNameOnly);
                report.Add("Exterior ground: the stadium has no exterior_facade mesh to carry a ground plane; the PES surroundings have no ground under them.");
                return;
            }
            if (settings.SlotTargets.Values.Any(v => string.Equals(v, facade.Name, StringComparison.OrdinalIgnoreCase)))
            {
                Log("  ground plane: {0} receives package geometry, none added", facade.Filename);
                return;
            }

            // extent: the backdrop's footprint (the painted wall stands at its edge), else 220 m each way
            float minX = -220f, maxX = 220f, minZ = -220f, maxZ = 220f;
            EbxAssetEntry skirt = StadiumBaseLookup.ComponentMeshes(settings.StadiumNameOnly).FirstOrDefault(e => e.Filename.ToLowerInvariant().Contains("cityskirt"));
            if (skirt != null)
            {
                Box3? b = MeshBox(skirt, "backdrop", sectionName => true, 0f);
                if (b.HasValue && b.Value.MaxX - b.Value.MinX > 100f && b.Value.MaxZ - b.Value.MinZ > 100f)
                {
                    minX = b.Value.MinX; maxX = b.Value.MaxX; minZ = b.Value.MinZ; maxZ = b.Value.MaxZ;
                }
            }

            // the facade's own material name keeps its material (and the exterior variation database entry)
            string materialName = "exterior_ground";
            try
            {
                EbxAsset facadeAsset = App.AssetManager.GetEbx(facade);
                ulong rid = ((dynamic)facadeAsset.RootObject).MeshSetResource;
                ResAssetEntry resEntry = App.AssetManager.GetResEntry(rid);
                MeshSet ms = resEntry != null ? App.AssetManager.GetResAs<MeshSet>(resEntry) : null;
                if (ms != null && ms.Lods.Count > 0)
                {
                    MeshSetSection first = ms.Lods[0].Sections.FirstOrDefault(s => s.Name != "");
                    if (first != null) materialName = first.Name;
                }
            }
            catch (Exception) { }

            GeometryImportSection plane = GroundPlaneSection(materialName, minX, maxX, minZ, maxZ, GroundPlaneY, 8);
            List<GeometryImportSection> sections = new List<GeometryImportSection> { plane };

            // into the mesh's own space when the level places the facade with a transform
            string placedBy;
            float[] placement = StadiumBaseLookup.Placement(settings.StadiumFolder, facade, out placedBy);
            if (placement != null && !StadiumBaseLookup.IsIdentity(placement))
            {
                float[] inv = StadiumBaseLookup.InverseRigid(placement);
                for (int v = 0; v < plane.VertexCount; v++)
                {
                    StadiumBaseLookup.TransformPoint(inv, ref plane.Positions[v * 3], ref plane.Positions[v * 3 + 1], ref plane.Positions[v * 3 + 2]);
                    StadiumBaseLookup.TransformDir(inv, ref plane.Normals[v * 3], ref plane.Normals[v * 3 + 1], ref plane.Normals[v * 3 + 2]);
                }
            }

            try
            {
                EbxAsset meshAsset = App.AssetManager.GetEbx(facade);
                ulong rid = ((dynamic)meshAsset.RootObject).MeshSetResource;
                ResAssetEntry resEntry = App.AssetManager.GetResEntry(rid);
                MeshSet meshSet = resEntry != null ? App.AssetManager.GetResAs<MeshSet>(resEntry) : null;
                if (meshSet == null) throw new InvalidOperationException("mesh set not readable");
                GeometryImportOptions options = new GeometryImportOptions { ForceDynamicLighting = true, KeepDedicatedDepthSection = false, SyncStaticModelGroups = true };
                GeometryImportResult result = new FBXImporter(logger).ImportGeometry(sections, meshSet, meshAsset, facade, options);
                clearedMeshes.Remove(facade.Name);
                Log("  ground plane: {0} <- plane y {1:0.00} x {2:0}..{3:0} z {4:0}..{5:0}, material '{6}', {7} triangles", facade.Filename, GroundPlaneY, minX, maxX, minZ, maxZ, materialName, result.Triangles);
                report.Add(string.Format("Exterior ground: a plane at y {0:0.00} over x {1:0}..{2:0}, z {3:0}..{4:0} in {5} under the PES surroundings (the base stadium's backdrop wall stays).", GroundPlaneY, minX, maxX, minZ, maxZ, facade.Filename));
            }
            catch (Exception ex)
            {
                Log("  WARNING: ground plane into {0} failed: {1}", facade.Filename, ex.Message);
                report.Add("Exterior ground: writing the plane failed (" + ex.Message + ").");
            }
        }

        /// <summary>A flat plane of n x n cells, normals up, texture repeating every GroundPlaneTextureRepeat metres, triangles wound like the package's (cross(b-a, c-a) up).</summary>
        private static GeometryImportSection GroundPlaneSection(string name, float minX, float maxX, float minZ, float maxZ, float y, int n)
        {
            int nv = (n + 1) * (n + 1);
            float[] pos = new float[nv * 3], nrm = new float[nv * 3], uv = new float[nv * 2];
            for (int iz = 0; iz <= n; iz++)
                for (int ix = 0; ix <= n; ix++)
                {
                    int v = iz * (n + 1) + ix;
                    float x = minX + (maxX - minX) * ix / n, z = minZ + (maxZ - minZ) * iz / n;
                    pos[v * 3] = x; pos[v * 3 + 1] = y; pos[v * 3 + 2] = z;
                    nrm[v * 3] = 0; nrm[v * 3 + 1] = 1; nrm[v * 3 + 2] = 0;
                    uv[v * 2] = x / GroundPlaneTextureRepeat; uv[v * 2 + 1] = z / GroundPlaneTextureRepeat;
                }
            List<uint> idx = new List<uint>(n * n * 6);
            for (int iz = 0; iz < n; iz++)
                for (int ix = 0; ix < n; ix++)
                {
                    uint a = (uint)(iz * (n + 1) + ix), b = a + 1, c = (uint)((iz + 1) * (n + 1) + ix + 1), d = c - 1;
                    // a=(x0,z0) b=(x1,z0) c=(x1,z1) d=(x0,z1): cross(c-a, b-a) and cross(d-a, c-a) point up (+y)
                    idx.Add(a); idx.Add(c); idx.Add(b);
                    idx.Add(a); idx.Add(d); idx.Add(c);
                }
            return new GeometryImportSection
            {
                Name = name, Part = 0, Positions = pos, Normals = nrm, TexCoord0 = uv, Indices = idx.ToArray(),
                Categories = MeshSubsetCategoryFlags.MeshSubsetCategoryFlags_Opaque | MeshSubsetCategoryFlags.MeshSubsetCategoryFlags_ZOnly | MeshSubsetCategoryFlags.MeshSubsetCategoryFlags_Shadow
            };
        }
    }
}
