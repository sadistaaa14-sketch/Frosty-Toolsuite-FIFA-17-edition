using DuplicationPlugin.Windows;
using Frosty.Controls;
using Frosty.Core;
using Frosty.Core.Viewport;
using Frosty.Core.Windows;
using FrostySdk;
using FrostySdk.Ebx;
using FrostySdk.Interfaces;
using FrostySdk.IO;
using FrostySdk.Managers;
using MeshSetPlugin;
using MeshSetPlugin.Resources;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Media;
using TexturePlugin;

namespace DuplicationPlugin
{
    /// <summary>What the import window collects.</summary>
    public class StadiumImportSettings
    {
        public string PackagePath = "";          // .../stadium.json
        public string StadiumFolder = "";        // generic_158
        public string StadiumNameOnly = "";      // generic
        /// <summary>package slot (standslower, ...) -> mesh asset name; slots not listed are skipped.</summary>
        public Dictionary<string, string> SlotTargets = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public bool ImportTextures = true;
        public bool DynamicLighting = true;
        /// <summary>
        /// When the mesh would need a 32-bit index buffer because its dedicated depth/shadow section holds a copy of
        /// every vertex, drop that section and let the named sections render the depth and shadow passes.
        /// </summary>
        public bool AvoidLargeIndexBuffers = true;
        public string TextureFolder = "";        // content/worlds/textures/pes/<name>
        public string OpaquePreset = "";
        public string AlphaTestPreset = "";
        public string GlassPreset = "";
        public string EmissivePreset = "";

        /// <summary>Rewrite the stadium's crowd point cloud with the package's spectator positions (crowd.bin).</summary>
        public bool ImportCrowd = true;
        /// <summary>Replace the stadium's ball-collision soups with the package's (physics/*.bin).</summary>
        public bool ImportPhysics = true;
        /// <summary>
        /// Keep the FIFA benches: the package sections tagged "dugout" (PES dugouts, chairs, 4th official desk) are
        /// not imported and the package geometry inside the FIFA bench box is removed. Off (default): the PES
        /// dugouts are imported, the FIFA playerbenches mesh is emptied and the stadium's bench areas (the
        /// stadium_&lt;id&gt; broadcasting record) are moved in front of the PES dugouts (StadiumBenches.cs).
        /// </summary>
        public bool KeepFifaBenches = false;
        /// <summary>Remove the package geometry inside the FIFA tunnel box (playerbenches_mesh) so the tunnel opens into the PES stand.</summary>
        public bool CarveTunnel = true;
        /// <summary>
        /// Move the base stadium's pitch-side characters and props (stewards, police, photographers, ball boys,
        /// camera crews, sideline props, weather dressing) that end up inside the PES stands onto open ground.
        /// </summary>
        public bool RefitPlacements = true;
        /// <summary>Move the crest boards that hang on the base stadium's stand walls out of sight.</summary>
        public bool SinkCrests = true;
        public bool ClearRailings = true;        // railings_mesh (pitch-side barriers, tier fronts) + its plastic collision
        public bool ClearCrowdNetting = true;    // crowdnetting_*_mesh (nets behind the goals)
        /// <summary>
        /// Empty the base stadium's backdrop meshes (exterior_cityskirt: the painted city wall 160-190 m out,
        /// exterior_background / _midground panels). Off: kept, unless the package's own exterior reaches out to
        /// them (then they are emptied anyway, StadiumExterior.cs).
        /// </summary>
        public bool ClearCitySkirt = false;
        /// <summary>A flat ground plane under the PES surroundings (into exterior_facade_mesh) when the package has no exterior layer and the backdrop stays.</summary>
        public bool ExteriorGroundPlane = true;
        public bool ClearScoreboard = true;      // scoreclock_mesh (the jumbotrons hang in the base stadium's stands)
        public bool ClearAdboardPlinth = true;   // adboards_mesh (concrete plinth of the base stadium's boards)
        public bool SinkBanners = true;          // banners sublevel: fan banners on the base stadium's stand fronts
        public bool SinkStaticAdboards = true;   // adboards sublevel: printed boards around the base stadium's apron
        public bool SinkLightFixtures = true;    // light fixture props on the base stadium's roof
        /// <summary>Move the base stadium's lens flares and fog cones onto the PES lamp heads (out of sight when the package has none).</summary>
        public bool RefitLensFlares = true;
        /// <summary>Empty every other mesh of the component folder (not a slot target, not the pitch / benches / entrance tunnel).</summary>
        public bool ClearOtherComponents = true;
    }

    /// <summary>
    /// Tools > Import PES Stadium: replaces the geometry of a FIFA 17 stadium's component meshes (stands, roof,
    /// exterior ...) with the meshes of a package written by the PES 2021 -> FIFA 17 converter and imports the
    /// package's textures as new texture assets wired into the meshes' materials and the stadium's mesh
    /// variation databases.
    /// </summary>
    public class ImportStadiumMenuExtension : MenuExtension
    {
        private const string L1_PREFIX = "content/worlds/stadiums/";
        private const string L2_PREFIX = "content/worlds/components/stadiums/";
        private const string L3_PREFIX = "content/effects/glares/";

        public override string TopLevelMenuName => "Tools";
        public override string SubLevelMenuName => null;
        public override string MenuItemName => "Import PES Stadium";
        public override ImageSource Icon => null;

        public override RelayCommand MenuItemClicked => new RelayCommand((o) =>
        {
            EbxAssetEntry entry = App.SelectedAsset as EbxAssetEntry;
            string folder = null, nameOnly = null;
            if (entry != null)
                TryDetectStadium(entry, out folder, out nameOnly);

            ImportStadiumWindow win = new ImportStadiumWindow(folder, nameOnly);
            if (win.ShowDialog() != true)
                return;

            StadiumImportSettings settings = win.Settings;
            StadiumImporter importer = new StadiumImporter(settings);
            FrostyTaskWindow.Show("Importing PES Stadium", "", (task) =>
            {
                try
                {
                    importer.Run(task);
                }
                catch (Exception ex)
                {
                    App.Logger.Log("[PES Import] ERROR: {0}", ex.ToString());
                    importer.Failure = ex.Message;
                }
            });

            App.EditorWindow.DataExplorer.RefreshAll();
            if (importer.Failure != null)
                FrostyMessageBox.Show("The import failed: " + importer.Failure + "\n\nSee the log for details.", "Import PES Stadium");
            else
                FrostyMessageBox.Show(importer.Summary, "Import PES Stadium");
        });

        // ─── stadium detection (as the duplicator) ────────────────────────────

        public static bool TryDetectStadium(EbxAssetEntry entry, out string folder, out string nameOnly)
        {
            folder = null;
            nameOnly = null;
            string path = entry.Name.Replace('\\', '/').ToLower();

            string seg;
            if (TryGetPathSegment(path, L1_PREFIX, out seg))
            {
                folder = seg;
                nameOnly = ExtractNameOnly(seg);
                return true;
            }
            if (TryGetPathSegment(path, L2_PREFIX, out seg) || TryGetPathSegment(path, L3_PREFIX, out seg))
            {
                nameOnly = seg;
                folder = ResolveStadiumFolder(seg);
                return folder != null;
            }
            return false;
        }

        public static string ResolveStadiumFolder(string nameOnly)
        {
            string prefix = L1_PREFIX + nameOnly.ToLower() + "_";
            foreach (EbxAssetEntry candidate in App.AssetManager.EnumerateEbx())
            {
                string p = candidate.Name.Replace('\\', '/').ToLower();
                string f;
                if (p.StartsWith(prefix) && TryGetPathSegment(p, L1_PREFIX, out f))
                    return f;
            }
            return null;
        }

        public static string ExtractNameOnly(string folder)
        {
            int underscore = folder.LastIndexOf('_');
            return underscore > 0 ? folder.Substring(0, underscore) : folder;
        }

        private static bool TryGetPathSegment(string path, string prefix, out string segment)
        {
            segment = null;
            if (!path.StartsWith(prefix))
                return false;
            string rest = path.Substring(prefix.Length);
            int slash = rest.IndexOf('/');
            segment = slash == -1 ? rest : rest.Substring(0, slash);
            return !string.IsNullOrEmpty(segment);
        }

        /// <summary>All stadiums (folder, name part) found in the asset manager, sorted.</summary>
        public static List<KeyValuePair<string, string>> ListStadiums()
        {
            SortedDictionary<string, string> found = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (EbxAssetEntry e in App.AssetManager.EnumerateEbx())
            {
                string p = e.Name.Replace('\\', '/').ToLower();
                string seg;
                if (TryGetPathSegment(p, L1_PREFIX, out seg) && !found.ContainsKey(seg))
                    found[seg] = ExtractNameOnly(seg);
            }
            return found.ToList();
        }

        /// <summary>The composite / rigid mesh assets of a stadium's component folder, sorted by name.</summary>
        public static List<EbxAssetEntry> ListComponentMeshes(string nameOnly)
        {
            string prefix = L2_PREFIX + nameOnly.ToLower() + "/";
            List<EbxAssetEntry> result = new List<EbxAssetEntry>();
            foreach (EbxAssetEntry e in App.AssetManager.EnumerateEbx())
            {
                if (!e.Name.Replace('\\', '/').ToLower().StartsWith(prefix))
                    continue;
                if (e.Type == "CompositeMeshAsset" || e.Type == "RigidMeshAsset")
                    result.Add(e);
            }
            result.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
            return result;
        }

        /// <summary>
        /// Shader presets the stadium's own component meshes use (they are in the stadium's bundles): the first
        /// opaque (worlds_default_preset), alpha tested (worlds_alphatest_preset) and glass/ preset found.
        /// </summary>
        public static void DiscoverPresets(string nameOnly, out string opaque, out string alphaTest, out string glass)
        {
            opaque = null; alphaTest = null; glass = null;
            foreach (EbxAssetEntry meshEntry in ListComponentMeshes(nameOnly))
            {
                EbxAsset meshAsset = null;
                try { meshAsset = App.AssetManager.GetEbx(meshEntry); } catch (Exception) { }
                if (meshAsset == null)
                    continue;
                dynamic materials;
                try { materials = ((dynamic)meshAsset.RootObject).Materials; } catch (Exception) { continue; }
                foreach (dynamic matRef in materials)
                {
                    dynamic material = matRef.Internal;
                    if (material == null)
                        continue;
                    string presetName, shaderName;
                    if (!PresetOfMaterial(material, out presetName, out shaderName))
                        continue;
                    string pl = presetName.ToLower(), sl = shaderName.ToLower();
                    if (glass == null && pl.Contains("/glass/"))
                        glass = presetName;
                    else if (alphaTest == null && sl.Contains("alphatest"))
                        alphaTest = presetName;
                    else if (opaque == null && sl.Contains("default_preset") && pl.Contains("diffuseopaque"))
                        opaque = presetName;
                }
                if (opaque != null && alphaTest != null && glass != null)
                    break;
            }
        }

        /// <summary>The SurfaceShaderPreset a MeshMaterial references and the shader (preset) that one is built on.</summary>
        private static bool PresetOfMaterial(dynamic material, out string presetName, out string shaderName)
        {
            presetName = null; shaderName = null;
            try
            {
                PointerRef presetRef = material.Shader.Shader;
                if (presetRef.Type != PointerRefType.External)
                    return false;
                EbxAssetEntry presetEntry = App.AssetManager.GetEbxEntry(presetRef.External.FileGuid);
                if (presetEntry == null || presetEntry.Type != "SurfaceShaderPreset")
                    return false;
                presetName = presetEntry.Name;
                EbxAsset preset = App.AssetManager.GetEbx(presetEntry);
                PointerRef shaderRef = ((dynamic)preset.RootObject).ShaderPreset.Shader;
                if (shaderRef.Type == PointerRefType.External)
                {
                    EbxAssetEntry shaderEntry = App.AssetManager.GetEbxEntry(shaderRef.External.FileGuid);
                    shaderName = shaderEntry != null ? shaderEntry.Name : "";
                }
                else
                    shaderName = "";
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }

    /// <summary>Runs one import (the work of the task window).</summary>
    public partial class StadiumImporter
    {
        public const string DefaultOpaquePreset = "content/worlds/materials/legacy/diffuseopaque/diffuseopaque_winf";
        public const string DefaultAlphaTestPreset = "content/worlds/materials/legacy/diffuse1bitalpha/diffuse1bitalpha_metal_railing_white_a";
        public const string DefaultGlassPreset = "content/worlds/materials/legacy/glass/glass_glass_windows";
        public const string TemplateColorTexture = "content/worlds/textures/default/default_base_color";
        public const string TemplateNormalTexture = "content/worlds/textures/default/default_base_normal";
        public const string TemplateCoefficientTexture = "content/worlds/textures/default/default_base_coeff";

        private readonly StadiumImportSettings settings;
        private readonly ILogger logger;
        private FrostyTaskWindow task;
        private PesStadiumPackage pkg;

        // package texture path -> imported texture asset (shared between the slots)
        private readonly Dictionary<string, EbxAssetEntry> importedTextures = new Dictionary<string, EbxAssetEntry>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> textureFailures = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, EbxAssetEntry> presetCache = new Dictionary<string, EbxAssetEntry>(StringComparer.OrdinalIgnoreCase);
        private readonly List<string> report = new List<string>();
        private int meshesImported;

        public string Failure;
        public string Summary => string.Join("\n", report);

        public StadiumImporter(StadiumImportSettings inSettings)
        {
            settings = inSettings;
            logger = App.Logger;
        }

        public void Run(FrostyTaskWindow inTask)
        {
            task = inTask;
            Log("Importing package {0} into stadium {1} ({2})", settings.PackagePath, settings.StadiumFolder, settings.StadiumNameOnly);
            task.Update("Reading the package");
            pkg = PesStadiumPackage.Load(settings.PackagePath);
            Log("Package: {0} {1} ({2}), {3} slots, {4} textures, written {5}", pkg.Game, pkg.StadiumName, pkg.StadiumId, pkg.Slots.Count, pkg.Textures.Count, pkg.Created.ToString("u"));
            foreach (string w in pkg.Warnings)
                Log("  package warning: {0}", w);

            if (!MeshVariationDb.IsLoaded)
            {
                task.Update("Loading the mesh variation databases");
                MeshVariationDb.LoadVariations(task);
            }

            List<PesStadiumPackage.SlotInfo> slots = pkg.Slots.Where(s => settings.SlotTargets.ContainsKey(s.Name) && !string.IsNullOrEmpty(settings.SlotTargets[s.Name])).ToList();
            if (slots.Count == 0)
                throw new InvalidOperationException("No slot is mapped to a mesh");

            // slots the stadium has no mesh for (weather / night layers) get a new component mesh first
            CreateNewSlotMeshes(slots);

            int done = 0;
            foreach (PesStadiumPackage.SlotInfo slot in slots)
            {
                string meshName = settings.SlotTargets[slot.Name];
                task.Update(string.Format("{0} -> {1}", slot.Name, meshName), 100.0 * done / slots.Count);
                ImportSlot(slot, meshName);
                done++;
            }
            FixNewMeshPartCounts();

            ImportStadiumWide();
            if (!settings.KeepFifaBenches)
            {
                EbxAssetEntry benches = BenchMeshEntry();
                if (benches != null)
                {
                    Log("--- FIFA bench furniture (the PES dugouts are used; the players' tunnel stays)");
                    try
                    {
                        List<string> keptSections;
                        List<string> clearedSections = ClearBenchFurniture(benches, out keptSections);
                        if (clearedSections.Count > 0)
                            report.Add(string.Format("FIFA benches: {0} furniture section(s) of {1} emptied ({2}); {3}.", clearedSections.Count, benches.Filename,
                                string.Join(", ", clearedSections), keptSections.Count > 0 ? "kept as the players' tunnel: " + string.Join(", ", keptSections) : "no tunnel section in the mesh"));
                    }
                    catch (Exception ex) { Log("  WARNING: bench furniture: {0}", ex.Message); }
                }
                task.Update("Bench areas");
                Log("--- bench areas (stadium_<id> broadcasting record) vs the PES dugouts");
                try { RewriteBenchAreas(); }
                catch (Exception ex) { Log("  WARNING: bench areas: {0}", ex.Message); }
            }

            report.Insert(0, string.Format("{0} mesh(es) imported from {1} ({2}).", meshesImported, pkg.StadiumName, pkg.StadiumId));
            if (settings.ImportTextures)
                report.Add(string.Format("{0} texture(s) imported into {1}{2}.", importedTextures.Count, settings.TextureFolder,
                    textureFailures.Count > 0 ? ", " + textureFailures.Count + " failed (see the log)" : ""));
            report.Add("Save the project and apply the mod with the Mod Manager; the imported meshes use dynamic lighting" +
                       (settings.DynamicLighting ? "." : " only where no lightmap UVs exist."));
            task.Update("Done", 100.0);
        }

        // ─── one slot -> one mesh ──────────────────────────────────────────────

        /// <summary>A sheet lying at ground level (every vertex within y -0.35..0.35, no triangle steeper than 20 degrees): the
        /// bench / tunnel lanes never cut it, a hole in the ground is worse than a floor under a tunnel.</summary>
        private static bool IsFlatGroundSection(GeometryImportSection gs)
        {
            if (gs.VertexCount == 0 || gs.Indices == null) return false;
            float[] p = gs.Positions;
            for (int v = 1; v < p.Length; v += 3)
                if (p[v] < -0.35f || p[v] > 0.35f) return false;
            uint[] idx = gs.Indices;
            for (int t = 0; t + 2 < idx.Length; t += 3)
            {
                int a = (int)idx[t] * 3, b = (int)idx[t + 1] * 3, c = (int)idx[t + 2] * 3;
                float ux = p[b] - p[a], uy = p[b + 1] - p[a + 1], uz = p[b + 2] - p[a + 2], vx = p[c] - p[a], vy = p[c + 1] - p[a + 1], vz = p[c + 2] - p[a + 2];
                float nx = uy * vz - uz * vy, ny = uz * vx - ux * vz, nz = ux * vy - uy * vx;
                float len = (float)Math.Sqrt(nx * nx + ny * ny + nz * nz);
                if (len > 1e-6f && Math.Abs(ny) / len < 0.94f) return false;
            }
            return true;
        }

        private void ImportSlot(PesStadiumPackage.SlotInfo slot, string meshName)
        {
            EbxAssetEntry meshEntry = App.AssetManager.GetEbxEntry(meshName);
            if (meshEntry == null)
                throw new InvalidOperationException("Mesh asset not found: " + meshName);

            string fbmPath = pkg.FullPath(slot.File);
            if (!File.Exists(fbmPath))
                throw new FileNotFoundException("Mesh file of slot " + slot.Name + " not found", fbmPath);

            Log("--- {0}: {1} -> {2}", slot.Name, slot.File, meshName);
            List<FbmSection> fbm = FbmReader.Read(fbmPath);
            if (fbm.Count == 0)
                throw new InvalidDataException(slot.File + " has no sections");

            // package sections (by name) carry the material class and textures
            Dictionary<string, PesStadiumPackage.SectionInfo> infoByName = new Dictionary<string, PesStadiumPackage.SectionInfo>(StringComparer.OrdinalIgnoreCase);
            foreach (PesStadiumPackage.SectionInfo si in slot.Sections)
                infoByName[si.Material] = si;

            List<GeometryImportSection> sections = new List<GeometryImportSection>();
            List<PesStadiumPackage.SectionInfo> sectionInfos = new List<PesStadiumPackage.SectionInfo>();   // parallel to sections (null = unknown)
            HashSet<string> usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int totalVerts = 0, totalTris = 0, dugoutSkipped = 0, carved = 0;
            List<Box3> boxes = CarveBoxes();
            for (int i = 0; i < fbm.Count; i++)
            {
                FbmSection fs = fbm[i];
                if (fs.VertexCount == 0 || fs.Indices.Length < 3)
                {
                    Log("  section '{0}' is empty, skipped", fs.Name);
                    continue;
                }
                PesStadiumPackage.SectionInfo info;
                if (!infoByName.TryGetValue(fs.Name, out info))
                {
                    info = (i < slot.Sections.Count && slot.Sections[i].Index == i) ? slot.Sections[i] : null;
                    if (info == null)
                        Log("  section '{0}' has no entry in stadium.json: opaque, no textures", fs.Name);
                }
                if (settings.KeepFifaBenches && info != null && info.Role == "dugout")
                {
                    Log("  section '{0}': PES dugout furniture, skipped (the FIFA benches are kept)", fs.Name);
                    dugoutSkipped++;
                    continue;
                }
                string name = SafeSectionName(fs.Name);
                int k = 2;
                while (!usedNames.Add(name))
                    name = SafeSectionName(fs.Name) + "_" + (k++);
                GeometryImportSection gs = ToGeometrySection(fs, name, sections.Count);
                if (info != null && info.Role == "dugout")
                    dugoutSections.Add(gs);      // world space; the bench areas are measured on these (StadiumBenches.cs)
                bool emptied = false;
                // flat ground sheets and the grass run-off stay whole (they obstruct nothing and hide the FIFA apron seam)
                bool flatGround = (info != null && (info.Role == "ground" || info.Role == "runoff")) || IsFlatGroundSection(gs);
                foreach (Box3 box in boxes)
                {
                    if (flatGround) break;
                    int cut = BoxClip.CarveSection(gs, box.MinX, box.MaxX, box.MinY, box.MaxY, box.MinZ, box.MaxZ);
                    if (cut == 0) continue;
                    carved += cut;
                    Log("  section '{0}': {1} triangle(s) cut along the box {2}", fs.Name, cut, box);
                    if (gs.TriangleCount == 0) { emptied = true; break; }
                }
                if (emptied)
                {
                    usedNames.Remove(name);
                    continue;
                }
                sections.Add(gs);
                sectionInfos.Add(info);
                totalVerts += gs.VertexCount;
                totalTris += gs.TriangleCount;
            }
            if (dugoutSkipped > 0 || carved > 0)
                Log("  bench area: {0} dugout section(s) skipped, {1} triangle(s) cut or removed along the FIFA bench / tunnel boxes", dugoutSkipped, carved);
            if (sections.Count == 0)
                throw new InvalidDataException(slot.File + ": every section is empty");
            Log("  {0} sections, {1} vertices, {2} triangles", sections.Count, totalVerts, totalTris);
            if (!slot.Overlay)
            {
                importedGeometry[slot.Name] = sections;   // the re-fit's walkable surface; weather / night layers are no obstacles
                importedSectionInfos[slot.Name] = sectionInfos;
            }

            // the package is world space; a mesh the stadium places with a transform gets the inverse applied
            string placedBy;
            float[] placement = StadiumBaseLookup.Placement(settings.StadiumFolder, meshEntry, out placedBy);
            if (placement != null && !StadiumBaseLookup.IsIdentity(placement))
            {
                float[] inv = StadiumBaseLookup.InverseRigid(placement);
                Log("  {0} is placed by {1} with {2}: the package geometry is moved into the mesh's own space", meshName, placedBy, StadiumBaseLookup.Describe(placement));
                List<GeometryImportSection> local = new List<GeometryImportSection>();
                foreach (GeometryImportSection gs in sections)
                {
                    GeometryImportSection c = new GeometryImportSection
                    {
                        Name = gs.Name, Part = gs.Part, Positions = (float[])gs.Positions.Clone(), Normals = gs.Normals != null ? (float[])gs.Normals.Clone() : null,
                        TexCoord0 = gs.TexCoord0, TexCoord1 = gs.TexCoord1, LightmapUv = gs.LightmapUv, Colors = gs.Colors, Indices = gs.Indices, Categories = gs.Categories
                    };
                    for (int v = 0; v < c.VertexCount; v++)
                    {
                        StadiumBaseLookup.TransformPoint(inv, ref c.Positions[v * 3], ref c.Positions[v * 3 + 1], ref c.Positions[v * 3 + 2]);
                        if (c.Normals != null) StadiumBaseLookup.TransformDir(inv, ref c.Normals[v * 3], ref c.Normals[v * 3 + 1], ref c.Normals[v * 3 + 2]);
                    }
                    local.Add(c);
                }
                sections = local;
            }
            else if (placement == null)
                Log("  note: {0} is not placed by {1}'s level data (imported as is)", meshName, settings.StadiumFolder);

            EbxAsset meshAsset = App.AssetManager.GetEbx(meshEntry);
            dynamic root = meshAsset.RootObject;
            ulong resRid = root.MeshSetResource;
            ResAssetEntry resEntry = App.AssetManager.GetResEntry(resRid);
            if (resEntry == null)
                throw new InvalidOperationException(meshName + ": the mesh set resource is missing");
            MeshSet meshSet = App.AssetManager.GetResAs<MeshSet>(resEntry);
            if (meshSet == null)
                throw new InvalidOperationException(meshName + ": the mesh set could not be read");
            Log("  target: {0} mesh, {1} lods, {2} sections in lod 0, {3} materials", meshSet.Type, meshSet.Lods.Count,
                meshSet.Lods.Count > 0 ? meshSet.Lods[0].Sections.Count : 0, (int)root.Materials.Count);

            // textures the mesh used before (template for the colour texture, and a fallback)
            List<int> meshBundles = meshEntry.EnumerateBundles().ToList();

            int largestSection = 0;
            foreach (GeometryImportSection gs in sections)
                largestSection = Math.Max(largestSection, gs.VertexCount);
            GeometryImportOptions importOptions = new GeometryImportOptions
            {
                ForceDynamicLighting = settings.DynamicLighting,
                // Never a dedicated depth/shadow copy: that unnamed section has no texture coordinates, so the
                // depth pre-pass and the shadow pass draw its triangles solid. PES seat rows, fences, nets and
                // lamp frames are alpha-tested sheets; drawn solid in the pre-pass they hide the stand structure
                // behind them from any camera that looks at them (Pisa: the main stand vanished from behind the
                // goal). The game's own meshes leave alpha materials out of that copy or have none at all
                // (61 of the 89 standslower meshes); the named sections then render the depth and shadow
                // passes with their own materials, alpha test included - the layout proven in game.
                KeepDedicatedDepthSection = false,
                SyncStaticModelGroups = true
            };
            if (largestSection > 65535)
                Log("  WARNING: a section has {0} vertices; the mesh needs a 32-bit index buffer (convert with sections split at 65535 vertices)", largestSection);

            GeometryImportResult result;
            try
            {
                FBXImporter importer = new FBXImporter(logger);
                result = importer.ImportGeometry(sections, meshSet, meshAsset, meshEntry, importOptions);
            }
            catch (Exception)
            {
                // the lod chunks written so far hang off the res entry, the materials off the ebx
                if (!meshEntry.IsAdded)
                {
                    try { App.AssetManager.RevertAsset(resEntry); } catch (Exception) { }
                    try { App.AssetManager.RevertAsset(meshEntry); } catch (Exception) { }
                }
                throw;
            }
            meshesImported++;
            Log("  geometry: {0} lods, {1} parts, {2} sections, {3} vertices, {4} triangles; materials +{5}/-{6}; {7}; {8}-bit indices; {9} StaticModelGroup member(s) updated",
                result.Lods, result.Parts, result.Sections, result.Vertices, result.Triangles,
                result.AddedMaterials.Count, result.RemovedMaterials.Count,
                result.EnlightenSetToDynamic ? "EnlightenType -> Dynamic" : "EnlightenType kept",
                result.LargeIndexBuffer ? 32 : 16, result.StaticModelGroupMembersUpdated);

            // materials: shader preset by class, textures
            meshAsset = App.AssetManager.GetEbx(meshEntry);
            root = meshAsset.RootObject;
            dynamic materials = root.Materials;
            int materialCount = materials.Count;
            if (materialCount != sections.Count)
                Log("  WARNING: {0} materials for {1} sections", materialCount, sections.Count);

            Dictionary<Guid, MaterialTextures> texturesByMaterial = new Dictionary<Guid, MaterialTextures>();
            bool meshChanged = false;
            for (int i = 0; i < Math.Min(materialCount, sections.Count); i++)
            {
                dynamic material = materials[i].Internal;
                if (material == null)
                    continue;
                PesStadiumPackage.SectionInfo info = sectionInfos[i];
                string cls = info != null ? info.Class : "opaque";

                // preset
                string presetName = PresetForClass(cls, info != null && info.Alpha);
                EbxAssetEntry presetEntry = ResolvePreset(presetName, meshBundles);
                if (presetEntry != null)
                {
                    try
                    {
                        EbxAsset presetAsset = App.AssetManager.GetEbx(presetEntry);
                        dynamic shader = material.Shader;
                        shader.Shader = new PointerRef(new EbxImportReference { FileGuid = presetEntry.Guid, ClassGuid = presetAsset.RootInstanceGuid });
                        material.Shader = shader;
                        meshAsset.AddDependency(presetEntry.Guid);
                        meshChanged = true;
                    }
                    catch (Exception ex)
                    {
                        Log("  WARNING: could not set the preset of material {0}: {1}", i, ex.Message);
                    }
                }

                // textures
                MaterialTextures mt = new MaterialTextures();
                if (settings.ImportTextures && info != null)
                {
                    mt.Color = EnsureTexture(info.ColorTexture, "color", meshBundles);
                    mt.Normal = EnsureTexture(info.NormalTexture, "normal", meshBundles);
                    mt.Coefficient = EnsureTexture(info.CoefficientTexture, "coeff", meshBundles);
                }
                if (mt.Any)
                {
                    try
                    {
                        dynamic shader = material.Shader;
                        SetTextureParameters(shader.TextureParameters, mt, meshAsset);
                        material.Shader = shader;
                        meshChanged = true;
                    }
                    catch (Exception ex)
                    {
                        Log("  WARNING: could not set the textures of material {0}: {1}", i, ex.Message);
                    }
                    AssetClassGuid guid = material.GetInstanceGuid();
                    texturesByMaterial[guid.ExportedGuid] = mt;
                }
            }
            if (meshChanged)
                App.AssetManager.ModifyEbx(meshEntry.Name, meshAsset);

            if (texturesByMaterial.Count > 0)
                UpdateVariationDatabases(meshEntry, texturesByMaterial);
        }

        private class MaterialTextures
        {
            public EbxAssetEntry Color, Normal, Coefficient;
            public bool Any => Color != null || Normal != null || Coefficient != null;
        }

        private string PresetForClass(string cls, bool alpha)
        {
            switch ((cls ?? "").ToLower())
            {
                case "alphatest": return Or(settings.AlphaTestPreset, DefaultAlphaTestPreset);
                case "transparent": return Or(settings.GlassPreset, DefaultGlassPreset);
                // PES constant (self-lit) materials: the emissive preset when one is given, else the preset the texture needs
                case "emissive": return Or(settings.EmissivePreset, alpha ? Or(settings.AlphaTestPreset, DefaultAlphaTestPreset) : Or(settings.OpaquePreset, DefaultOpaquePreset));
                default: return Or(settings.OpaquePreset, DefaultOpaquePreset);
            }
        }

        private static string Or(string a, string b) => string.IsNullOrWhiteSpace(a) ? b : a.Trim();

        /// <summary>The preset asset; it (and the textures it references) are added to the mesh's bundles when they are in none of them.</summary>
        private EbxAssetEntry ResolvePreset(string name, List<int> meshBundles)
        {
            EbxAssetEntry cached;
            if (presetCache.TryGetValue(name, out cached))
            {
                if (cached != null)
                    AddToBundlesIfMissing(cached, meshBundles, "preset");
                return cached;
            }
            EbxAssetEntry entry = App.AssetManager.GetEbxEntry(name);
            if (entry == null)
            {
                Log("  WARNING: shader preset '{0}' not found; the material keeps its preset", name);
                presetCache[name] = null;
                return null;
            }
            AddToBundlesIfMissing(entry, meshBundles, "preset");
            try
            {
                EbxAsset preset = App.AssetManager.GetEbx(entry);
                dynamic texParams = ((dynamic)preset.RootObject).ShaderPreset.TextureParameters;
                foreach (dynamic tp in texParams)
                {
                    PointerRef pr = tp.Value;
                    if (pr.Type != PointerRefType.External)
                        continue;
                    EbxAssetEntry texEntry = App.AssetManager.GetEbxEntry(pr.External.FileGuid);
                    if (texEntry != null)
                        AddToBundlesIfMissing(texEntry, meshBundles, "preset texture");
                }
            }
            catch (Exception ex)
            {
                Log("  note: preset '{0}': {1}", name, ex.Message);
            }
            presetCache[name] = entry;
            return entry;
        }

        private void AddToBundlesIfMissing(AssetEntry entry, List<int> meshBundles, string what)
        {
            if (meshBundles.Count == 0)
                return;
            foreach (int b in entry.EnumerateBundles())
            {
                if (meshBundles.Contains(b))
                    return;
            }
            entry.AddToBundles(meshBundles);
            Log("  {0} '{1}' added to the mesh's {2} bundle(s)", what, entry.Name, meshBundles.Count);
        }

        // ─── textures ─────────────────────────────────────────────────────────

        /// <summary>Imports a package texture once as a new texture asset (a copy of the default texture of its role); null when it cannot be imported.</summary>
        private EbxAssetEntry EnsureTexture(string packagePath, string role, List<int> meshBundles)
        {
            if (string.IsNullOrEmpty(packagePath))
                return null;
            EbxAssetEntry known;
            if (importedTextures.TryGetValue(packagePath, out known))
            {
                // a texture shared by several meshes must be in every mesh's bundles
                AddTextureToBundles(known, meshBundles);
                return known;
            }
            if (textureFailures.ContainsKey(packagePath))
                return null;

            string file = pkg.FullPath(packagePath);
            if (!File.Exists(file))
            {
                textureFailures[packagePath] = "file not found";
                Log("  WARNING: texture file not found: {0}", file);
                return null;
            }

            // the template decides sRGB-ness (ImportTexture2D wants an sRGB DDS for an sRGB template) and the 2D type;
            // the pixel format comes from the DDS header
            PesStadiumPackage.TextureInfo texInfo;
            bool srgb = pkg.Textures.TryGetValue(packagePath, out texInfo) ? texInfo.Srgb : role == "color";
            string templateName = srgb ? TemplateColorTexture : role == "normal" ? TemplateNormalTexture : TemplateCoefficientTexture;
            EbxAssetEntry template = App.AssetManager.GetEbxEntry(templateName);
            if (template == null)
            {
                textureFailures[packagePath] = "template texture " + templateName + " not found";
                Log("  WARNING: template texture {0} not found", templateName);
                return null;
            }

            string stem = Path.GetFileNameWithoutExtension(packagePath);
            string assetName = settings.TextureFolder.TrimEnd('/') + "/" + UniqueAssetStem(SafeAssetName(stem), packagePath);
            task.Update("Texture " + stem);
            try
            {
                EbxAssetEntry texEntry = App.AssetManager.GetEbxEntry(assetName);
                if (texEntry == null)
                {
                    DuplicationTool.TextureExtension ext = new DuplicationTool.TextureExtension();
                    texEntry = ext.DuplicateAsset(template, assetName, false, null);
                    if (texEntry == null)
                        throw new InvalidOperationException("duplication of " + templateName + " failed");
                }
                else
                    Log("  texture asset {0} exists, re-importing", assetName);

                // the texture lives where the mesh lives
                SetTextureBundles(texEntry, meshBundles, true);

                string error;
                if (!FrostyTextureEditor.ImportTexture2D(texEntry, file, logger, out error))
                    throw new InvalidOperationException(error ?? "import failed");

                importedTextures[packagePath] = texEntry;
                return texEntry;
            }
            catch (Exception ex)
            {
                textureFailures[packagePath] = ex.Message;
                Log("  WARNING: texture {0} not imported: {1}", packagePath, ex.Message);
                return null;
            }
        }

        private readonly Dictionary<string, string> assetStemByPath = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Two package textures with the same file name (different folders) must not share one asset.</summary>
        private string UniqueAssetStem(string stem, string packagePath)
        {
            string known;
            if (assetStemByPath.TryGetValue(packagePath, out known))
                return known;
            string candidate = stem;
            if (assetStemByPath.ContainsValue(candidate))
            {
                uint h = (uint)Frosty.Hash.Fnv1.HashString(packagePath.ToLowerInvariant());
                candidate = stem + "_" + h.ToString("x8");
            }
            assetStemByPath[packagePath] = candidate;
            return candidate;
        }

        private static void AddTextureToBundles(EbxAssetEntry texEntry, List<int> meshBundles)
        {
            SetTextureBundles(texEntry, meshBundles, false);
        }

        /// <summary>Puts a texture asset, its res and its chunk into the mesh's bundles (replace: the added bundles become exactly those).</summary>
        private static void SetTextureBundles(EbxAssetEntry texEntry, List<int> meshBundles, bool replace)
        {
            if (meshBundles.Count == 0)
                return;
            SetBundles(texEntry, meshBundles, replace);
            try
            {
                EbxAsset texAsset = App.AssetManager.GetEbx(texEntry);
                ulong texRid = ((dynamic)texAsset.RootObject).Resource;
                ResAssetEntry texRes = App.AssetManager.GetResEntry(texRid);
                if (texRes == null)
                    return;
                SetBundles(texRes, meshBundles, replace);
                FrostySdk.Resources.Texture tex = App.AssetManager.GetResAs<FrostySdk.Resources.Texture>(texRes);
                ChunkAssetEntry chunk = tex != null ? App.AssetManager.GetChunkEntry(tex.ChunkId) : null;
                if (chunk != null)
                    SetBundles(chunk, meshBundles, replace);
            }
            catch (Exception) { }
        }

        private static void SetBundles(AssetEntry entry, List<int> bundles, bool replace)
        {
            if (!entry.IsAdded || bundles.Count == 0)
                return;
            if (replace)
            {
                entry.AddedBundles.Clear();
                entry.AddedBundles.AddRange(bundles);
            }
            else
                entry.AddToBundles(bundles);
        }

        /// <summary>Replaces a TextureParameters list (MeshMaterial or variation database material) with the imported textures.</summary>
        private void SetTextureParameters(dynamic texParams, MaterialTextures mt, EbxAsset owner)
        {
            texParams.Clear();
            AddTextureParameter(texParams, "colorTexture", mt.Color, owner);
            AddTextureParameter(texParams, "coefficientTexture", mt.Coefficient, owner);
            AddTextureParameter(texParams, "normalTexture", mt.Normal, owner);
        }

        private void AddTextureParameter(dynamic texParams, string parameterName, EbxAssetEntry texEntry, EbxAsset owner)
        {
            if (texEntry == null)
                return;
            EbxAsset texAsset = App.AssetManager.GetEbx(texEntry);
            dynamic tp = TypeLibrary.CreateObject("TextureShaderParameter");
            tp.ParameterName = new CString(parameterName);
            tp.Value = new PointerRef(new EbxImportReference { FileGuid = texEntry.Guid, ClassGuid = texAsset.RootInstanceGuid });
            texParams.Add(tp);
            owner.AddDependency(texEntry.Guid);
        }

        // ─── mesh variation databases ─────────────────────────────────────────

        /// <summary>Every variation database entry of the mesh gets the imported textures for the materials in <paramref name="texturesByMaterial"/>.</summary>
        private void UpdateVariationDatabases(EbxAssetEntry meshEntry, Dictionary<Guid, MaterialTextures> texturesByMaterial)
        {
            List<EbxAssetEntry> dbs = FindVariationDatabases(meshEntry);
            if (dbs.Count == 0)
            {
                Log("  WARNING: no mesh variation database lists {0}; the textures are only set on the mesh's materials", meshEntry.Name);
                return;
            }
            int entriesUpdated = 0;
            foreach (EbxAssetEntry dbEntry in dbs)
            {
                EbxAsset dbAsset = App.AssetManager.GetEbx(dbEntry);
                if (dbAsset == null)
                    continue;
                dynamic db = dbAsset.RootObject;
                bool changed = false;
                foreach (dynamic dbMeshEntry in db.Entries)
                {
                    PointerRef meshRef = dbMeshEntry.Mesh;
                    if (meshRef.Type != PointerRefType.External || meshRef.External.FileGuid != meshEntry.Guid)
                        continue;
                    dynamic dbMaterials = dbMeshEntry.Materials;
                    int matched = 0;
                    foreach (dynamic dbMaterial in dbMaterials)
                    {
                        PointerRef matRef = dbMaterial.Material;
                        if (matRef.Type != PointerRefType.External)
                            continue;
                        MaterialTextures mt;
                        if (!texturesByMaterial.TryGetValue(matRef.External.ClassGuid, out mt))
                            continue;
                        SetTextureParameters(dbMaterial.TextureParameters, mt, dbAsset);
                        matched++;
                        changed = true;
                    }
                    entriesUpdated++;
                    if (matched != texturesByMaterial.Count)
                        Log("  variation database {0}: {1} of {2} materials matched", dbEntry.Name, matched, texturesByMaterial.Count);
                }
                if (changed)
                {
                    App.AssetManager.ModifyEbx(dbEntry.Name, dbAsset);
                    Log("  variation database updated: {0}", dbEntry.Name);
                }
            }
            if (entriesUpdated > 0)
            {
                try { MeshVariationDb.LoadModifiedVariations(); } catch (Exception) { }
            }
        }

        /// <summary>The databases that list the mesh: the cache's locations, the stadium's own databases, else every database.</summary>
        private List<EbxAssetEntry> FindVariationDatabases(EbxAssetEntry meshEntry)
        {
            List<EbxAssetEntry> result = new List<EbxAssetEntry>();
            HashSet<Guid> seen = new HashSet<Guid>();

            MeshVariationDbEntry mvEntry = null;
            try { mvEntry = MeshVariationDb.GetVariations(meshEntry.Guid); } catch (Exception) { }
            if (mvEntry != null)
            {
                foreach (MeshVariation variation in mvEntry.Variations.Values)
                {
                    foreach (Tuple<EbxImportReference, int> loc in variation.DbLocations)
                    {
                        EbxAssetEntry dbEntry = App.AssetManager.GetEbxEntry(loc.Item1.FileGuid);
                        if (dbEntry != null && seen.Add(dbEntry.Guid))
                            result.Add(dbEntry);
                    }
                }
            }

            // the stadium's own databases (added ones are not in the cache)
            string prefix = "content/worlds/stadiums/" + settings.StadiumFolder.ToLower() + "/";
            foreach (EbxAssetEntry dbEntry in App.AssetManager.EnumerateEbx("MeshVariationDatabase").ToList())
            {
                if (seen.Contains(dbEntry.Guid) || !dbEntry.Name.Replace('\\', '/').ToLower().StartsWith(prefix))
                    continue;
                if (DatabaseListsMesh(dbEntry, meshEntry.Guid) && seen.Add(dbEntry.Guid))
                    result.Add(dbEntry);
            }

            if (result.Count == 0)
            {
                task.Update("Searching the mesh variation databases");
                foreach (EbxAssetEntry dbEntry in App.AssetManager.EnumerateEbx("MeshVariationDatabase").ToList())
                {
                    if (seen.Contains(dbEntry.Guid))
                        continue;
                    if (DatabaseListsMesh(dbEntry, meshEntry.Guid) && seen.Add(dbEntry.Guid))
                        result.Add(dbEntry);
                }
            }
            return result;
        }

        private static bool DatabaseListsMesh(EbxAssetEntry dbEntry, Guid meshGuid)
        {
            try
            {
                EbxAsset dbAsset = App.AssetManager.GetEbx(dbEntry);
                if (dbAsset == null)
                    return false;
                foreach (dynamic dbMeshEntry in ((dynamic)dbAsset.RootObject).Entries)
                {
                    PointerRef meshRef = dbMeshEntry.Mesh;
                    if (meshRef.Type == PointerRefType.External && meshRef.External.FileGuid == meshGuid)
                        return true;
                }
            }
            catch (Exception) { }
            return false;
        }

        // ─── geometry ─────────────────────────────────────────────────────────

        private static GeometryImportSection ToGeometrySection(FbmSection fs, string name, int part)
        {
            int n = fs.VertexCount;
            GeometryImportSection gs = new GeometryImportSection { Name = name, Part = part };
            gs.Positions = fs.Positions;
            if (fs.Normals != null && fs.Normals.Length == n * 3)
                gs.Normals = fs.Normals;
            string uv0Name = fs.UvSetOrder.Count > 0 ? fs.UvSetOrder[0] : null;
            gs.TexCoord0 = uv0Name != null ? fs.UvSets[uv0Name] : new float[n * 2];
            if (fs.UvSetOrder.Count > 1)
                gs.TexCoord1 = fs.UvSets[fs.UvSetOrder[1]];
            foreach (KeyValuePair<string, float[]> kv in fs.UvSets)
            {
                string k = kv.Key.ToLower();
                if ((k == "lightmap" || k == "radiositytexcoord") && kv.Value.Length == n * 2)
                    gs.LightmapUv = kv.Value;
            }
            gs.Indices = fs.Indices;

            // flags: bit0 opaque, bit1 transparent, bit2 transparent decal, bit3 z-only, bit4 shadow
            MeshSubsetCategoryFlags cats = 0;
            if ((fs.Flags & 1) != 0) cats |= MeshSubsetCategoryFlags.MeshSubsetCategoryFlags_Opaque;
            if ((fs.Flags & 2) != 0) cats |= MeshSubsetCategoryFlags.MeshSubsetCategoryFlags_Transparent;
            if ((fs.Flags & 4) != 0) cats |= MeshSubsetCategoryFlags.MeshSubsetCategoryFlags_TransparentDecal;
            if ((fs.Flags & 8) != 0) cats |= MeshSubsetCategoryFlags.MeshSubsetCategoryFlags_ZOnly;
            if ((fs.Flags & 16) != 0) cats |= MeshSubsetCategoryFlags.MeshSubsetCategoryFlags_Shadow;
            // FIFA 17 stadium glass sits in the opaque pass with a glass preset; a pure transparent flag would
            // leave the section out of the depth and shadow passes, and an opaque section is in all three
            if ((cats & MeshSubsetCategoryFlags.MeshSubsetCategoryFlags_Opaque) == 0)
                cats = MeshSubsetCategoryFlags.MeshSubsetCategoryFlags_Opaque;
            cats |= MeshSubsetCategoryFlags.MeshSubsetCategoryFlags_ZOnly | MeshSubsetCategoryFlags.MeshSubsetCategoryFlags_Shadow;
            gs.Categories = cats;

            // first colour set (r g b a per vertex), if the file has one
            foreach (KeyValuePair<string, byte[]> kv in fs.ColorSets)
            {
                if (kv.Value.Length == n * 4)
                {
                    gs.Colors = kv.Value;
                    break;
                }
            }
            return gs;
        }

        private static string SafeSectionName(string name)
        {
            StringBuilder sb = new StringBuilder();
            foreach (char c in name ?? "")
            {
                if (char.IsLetterOrDigit(c) || c == '_' || c == '-' || c == '.')
                    sb.Append(c);
                else
                    sb.Append('_');
            }
            if (sb.Length == 0)
                sb.Append("section");
            return sb.ToString();
        }

        private static string SafeAssetName(string name)
        {
            StringBuilder sb = new StringBuilder();
            foreach (char c in (name ?? "").ToLowerInvariant())
            {
                if ((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '_' || c == '-')
                    sb.Append(c);
                else
                    sb.Append('_');
            }
            if (sb.Length == 0)
                sb.Append("texture");
            return sb.ToString();
        }

        private void Log(string format, params object[] args)
        {
            string line = args.Length > 0 ? string.Format(format, args) : format;
            logger.Log("[PES Import] {0}", line);
            if (line.StartsWith("  WARNING") || line.StartsWith("---"))
                report.Add(line.Trim());
        }
    }
}
