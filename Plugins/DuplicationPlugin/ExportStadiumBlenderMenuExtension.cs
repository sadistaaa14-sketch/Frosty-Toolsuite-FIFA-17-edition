using Frosty.Controls;
using Frosty.Core;
using Frosty.Core.Windows;
using FrostySdk;
using FrostySdk.Ebx;
using FrostySdk.IO;
using FrostySdk.Managers;
using MeshSetPlugin.Resources;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Media;

namespace DuplicationPlugin
{
    /// <summary>
    /// Tools > Export Stadium for Blender: writes the selected stadium (every static mesh instance of its level,
    /// sublevels, layers, prefabs and static model groups, LOD 0) as a package that the Blender add-on
    /// io_import_fbstadium.py reads:
    ///   [out]\[stadium]\scene.json         level, meshes, instances (Frostbite 4x4, row vectors), notes
    ///   [out]\[stadium]\meshes\[name].fbm  FBMESH01: per section material, flags, positions, normals, UV sets, colours, indices
    /// The same package is produced by tools/blender/fbstadium_export.py (python) from Frosty's bulk exports.
    /// </summary>
    public class ExportStadiumBlenderMenuExtension : MenuExtension
    {
        public override string TopLevelMenuName => "Tools";
        public override string SubLevelMenuName => null;
        public override string MenuItemName => "Export Stadium for Blender";
        public override ImageSource Icon => null;

        public override RelayCommand MenuItemClicked => new RelayCommand((o) =>
        {
            EbxAssetEntry entry = App.SelectedAsset as EbxAssetEntry;
            string levelName = entry != null ? StadiumBlenderExporter.LevelNameFor(entry.Name) : null;
            if (levelName == null)
            {
                FrostyMessageBox.Show(
                    "Select an asset of the stadium first (anything under content/worlds/stadiums/<stadium>, " +
                    "content/worlds/components/stadiums/<name> or content/effects/glares/<name>).",
                    "Export Stadium for Blender");
                return;
            }
            if (App.AssetManager.GetEbxEntry(levelName) == null)
            {
                FrostyMessageBox.Show("The level asset " + levelName + " does not exist.", "Export Stadium for Blender");
                return;
            }

            string outDir;
            using (System.Windows.Forms.FolderBrowserDialog dlg = new System.Windows.Forms.FolderBrowserDialog())
            {
                dlg.Description = "Folder for the Blender package of " + levelName.Substring(levelName.LastIndexOf('/') + 1);
                dlg.ShowNewFolderButton = true;
                if (dlg.ShowDialog() != System.Windows.Forms.DialogResult.OK)
                    return;
                outDir = dlg.SelectedPath;
            }

            string report = null;
            FrostyTaskWindow.Show("Exporting stadium for Blender", "", (task) =>
            {
                try
                {
                    StadiumBlenderExporter exporter = new StadiumBlenderExporter(task);
                    report = exporter.Export(levelName, outDir);
                }
                catch (Exception ex)
                {
                    App.Logger.Log("Export Stadium for Blender failed: " + ex);
                    report = "Export failed: " + ex.Message;
                }
            });
            if (report != null)
                FrostyMessageBox.Show(report, "Export Stadium for Blender");
        });
    }

    /// <summary>
    /// Walks a stadium level the way the game places it and writes the package. The walk mirrors
    /// tools/blender/fbscene.py: containers (DetachedSubWorldData, SubWorldData, WorldPartData, LayerData,
    /// SpatialPrefabBlueprint, PrefabBlueprint) list Objects; reference objects place another asset with their
    /// BlueprintTransform (SubWorldReferenceObjectData without Blueprint: the sublevel named by BundleName);
    /// ObjectBlueprint has one Object; StaticModelEntityData is one mesh instance at Transform;
    /// StaticModelGroupEntityData places MemberDatas[i].MeshAsset at every InstanceTransforms[j].
    /// Transforms compose as row vectors: world = local * parent.
    /// </summary>
    internal class StadiumBlenderExporter
    {
        private const string L1_PREFIX = "content/worlds/stadiums/";
        private const string L2_PREFIX = "content/worlds/components/stadiums/";
        private const string L3_PREFIX = "content/effects/glares/";
        private static readonly string[] SkipSublevels = { "crowd", "slc", "effects", "lighting_flux", "lighting" };
        private static readonly HashSet<string> ContainerTypes = new HashSet<string>
        {
            "DetachedSubWorldData", "SubWorldData", "WorldPartData", "LayerData", "SpatialPrefabBlueprint",
            "PrefabBlueprint", "LogicPrefabBlueprint", "BlueprintBundle"
        };
        private static readonly HashSet<string> ReferenceTypes = new HashSet<string>
        {
            "WorldPartReferenceObjectData", "LayerReferenceObjectData", "SubWorldReferenceObjectData",
            "SpatialPrefabReferenceObjectData", "ObjectReferenceObjectData", "LogicPrefabReferenceObjectData",
            "PrefabReferenceObjectData"
        };

        private class Instance
        {
            public string Mesh;
            public Guid MeshRootGuid;
            public double[,] Matrix;
            public string Layer;
            public string Source;
            public string Kind;
            public float[] Lightmap;   // sx, sy, tx, ty in the lightmap atlas, or null
            public uint LightmapKey;
        }

        private class LightingInfo
        {
            public string Database;
            public uint AtlasWidth, AtlasHeight, Count;
            public int Matched;
            public int FluxScale;
            public string OutputFormat;
            public Dictionary<string, string> Textures = new Dictionary<string, string>();
            public List<string> Notes = new List<string>();
            public string ProbesFile;
            public int ProbeCount, ProbeSets;
        }

        private class MeshInfo
        {
            public string Name;
            public string File;
            public string Reason;
            public string MeshType;
            public float[] BoxMin, BoxMax;
            public List<string> Sections = new List<string>();
        }

        private class MaterialInfo
        {
            public string Key;                 // "<mesh name>|<material index>"
            public string Name;                // the material's Id (the section name)
            public string Mesh;
            public int Index;
            public string Shader;              // the base SurfaceShader at the end of the preset chain
            public List<string> Presets = new List<string>();
            public Dictionary<string, string> Textures = new Dictionary<string, string>();      // parameter -> file
            public Dictionary<string, string> TextureAssets = new Dictionary<string, string>(); // parameter -> asset
            public Dictionary<string, float[]> Vectors = new Dictionary<string, float[]>();
            public Dictionary<string, bool> Bools = new Dictionary<string, bool>();
            public bool FromVariationDb;
        }

        private readonly Dictionary<string, MaterialInfo> materials = new Dictionary<string, MaterialInfo>();
        private readonly Dictionary<string, string> textureFiles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); // asset name -> file
        private readonly List<string> textureFailures = new List<string>();
        private string textureDir;

        private readonly FrostyTaskWindow task;
        private readonly List<Instance> instances = new List<Instance>();
        private readonly List<string> missingAssets = new List<string>();
        private readonly Dictionary<string, int> skippedTypes = new Dictionary<string, int>();
        private readonly Dictionary<string, EbxAsset> loaded = new Dictionary<string, EbxAsset>();
        private readonly List<string> notes = new List<string>();

        public StadiumBlenderExporter(FrostyTaskWindow task)
        {
            this.task = task;
        }

        /// <summary>content/worlds/stadiums/[folder]/[folder] for an asset of the stadium, or null.</summary>
        public static string LevelNameFor(string assetName)
        {
            string path = assetName.Replace('\\', '/').Trim('/').ToLower();
            string folder = null;
            if (path.StartsWith(L1_PREFIX))
            {
                folder = Segment(path, L1_PREFIX);
            }
            else
            {
                string nameOnly = path.StartsWith(L2_PREFIX) ? Segment(path, L2_PREFIX)
                    : path.StartsWith(L3_PREFIX) ? Segment(path, L3_PREFIX) : null;
                if (nameOnly != null)
                {
                    string prefix = L1_PREFIX + nameOnly + "_";
                    foreach (EbxAssetEntry e in App.AssetManager.EnumerateEbx())
                    {
                        string p = e.Name.Replace('\\', '/').ToLower();
                        if (p.StartsWith(prefix))
                        {
                            folder = Segment(p, L1_PREFIX);
                            break;
                        }
                    }
                }
            }
            return folder == null ? null : L1_PREFIX + folder + "/" + folder;
        }

        private static string Segment(string path, string prefix)
        {
            string rest = path.Substring(prefix.Length);
            int slash = rest.IndexOf('/');
            string s = slash == -1 ? rest : rest.Substring(0, slash);
            return string.IsNullOrEmpty(s) ? null : s;
        }

        public string Export(string levelName, string outRoot)
        {
            string stadium = levelName.Substring(levelName.LastIndexOf('/') + 1);
            string outDir = Path.Combine(outRoot, stadium);
            string meshDir = Path.Combine(outDir, "meshes");
            Directory.CreateDirectory(meshDir);
            textureDir = Path.Combine(outDir, "textures");

            task.Update("Reading the level");
            WalkAsset(levelName, Identity(), levelName, levelName, 0);
            App.Logger.Log($"Export Stadium for Blender: {instances.Count} instances in {levelName}");

            // meshes
            Dictionary<string, MeshInfo> meshes = new Dictionary<string, MeshInfo>(StringComparer.OrdinalIgnoreCase);
            List<string> missingMeshes = new List<string>();
            int done = 0;
            foreach (Instance inst in instances)
            {
                if (meshes.ContainsKey(inst.Mesh))
                    continue;
                MeshInfo info = new MeshInfo { Name = inst.Mesh };
                meshes[inst.Mesh] = info;
                task.Update($"Writing mesh {++done}: {inst.Mesh.Substring(inst.Mesh.LastIndexOf('/') + 1)}");
                try
                {
                    WriteMesh(inst.Mesh, meshDir, info);
                }
                catch (Exception ex)
                {
                    info.Reason = "failed: " + ex.Message;
                    App.Logger.Log($"  mesh {inst.Mesh}: {ex}");
                }
                if (info.File == null)
                    missingMeshes.Add(inst.Mesh);
            }

            LightingInfo lighting = null;
            try
            {
                task.Update("Baked lighting");
                lighting = ExportLighting(levelName, outDir);
            }
            catch (Exception ex)
            {
                notes.Add("lighting export failed: " + ex.Message);
                App.Logger.Log("  lighting: " + ex);
            }

            task.Update("Writing scene.json");
            WriteSceneJson(Path.Combine(outDir, "scene.json"), levelName, stadium, meshes, missingMeshes, lighting);

            string report = $"{stadium}: {instances.Count} instances, {meshes.Count} meshes ({missingMeshes.Count} without geometry), " +
                            $"{materials.Count} materials, {textureFiles.Count} textures ({textureFailures.Count} failed), " +
                            $"{missingAssets.Count} referenced assets not found" +
                            (lighting != null ? $", {lighting.Matched} instances placed in the {lighting.AtlasWidth}x{lighting.AtlasHeight} lightmap atlas" : ", no lighting database") +
                            $".\nWritten to {outDir}\n" +
                            "Import in Blender with File > Import > FIFA 17 Stadium (scene.json).";
            App.Logger.Log("Export Stadium for Blender: " + report.Replace('\n', ' '));
            return report;
        }

        // ─── level walk ─────────────────────────────────────────────────────

        private EbxAsset Load(string name)
        {
            string key = name.ToLower();
            if (loaded.TryGetValue(key, out EbxAsset cached))
                return cached;
            EbxAssetEntry entry = App.AssetManager.GetEbxEntry(name);
            EbxAsset asset = entry != null ? App.AssetManager.GetEbx(entry) : null;
            if (asset == null)
                missingAssets.Add(name);
            loaded[key] = asset;
            return asset;
        }

        private void WalkAsset(string name, double[,] matrix, string layer, string source, int depth)
        {
            if (depth > 12)
            {
                notes.Add("reference depth > 12 at " + name);
                return;
            }
            EbxAsset asset = Load(name);
            if (asset == null)
                return;
            WalkObject(asset, asset.RootObject, matrix, layer, source, depth);
        }

        private void WalkObject(EbxAsset asset, object obj, double[,] matrix, string layer, string source, int depth)
        {
            if (obj == null)
                return;
            string type = obj.GetType().Name;

            if (ContainerTypes.Contains(type))
            {
                object enabled = Field(obj, "Enabled");
                if (enabled is bool && !(bool)enabled)
                    return;
                if (type == "WorldPartData" || type == "LayerData" || type == "SubWorldData" || type == "DetachedSubWorldData")
                {
                    string n = Field(obj, "Name") as string ?? (Field(obj, "Name")?.ToString());
                    if (!string.IsNullOrEmpty(n))
                        layer = n;
                }
                foreach (PointerRef pr in PointerList(Field(obj, "Objects")))
                {
                    if (pr.Type == PointerRefType.Internal)
                        WalkObject(asset, pr.Internal, matrix, layer, source, depth);
                }
                return;
            }

            if (type == "ObjectBlueprint")
            {
                object o = Field(obj, "Object");
                if (o is PointerRef pr && pr.Type == PointerRefType.Internal)
                    WalkObject(asset, pr.Internal, matrix, layer, source, depth);
                return;
            }

            if (ReferenceTypes.Contains(type))
            {
                object excluded = Field(obj, "Excluded");
                if (excluded is bool && (bool)excluded)
                    return;
                double[,] m = Mul(Transform(Field(obj, "BlueprintTransform")), matrix);
                object bp = Field(obj, "Blueprint");
                if (bp is PointerRef bpr && bpr.Type == PointerRefType.External)
                {
                    EbxAssetEntry target = App.AssetManager.GetEbxEntry(bpr.External.FileGuid);
                    if (target != null)
                        WalkAsset(target.Name, m, layer, target.Name, depth + 1);
                    else
                        missingAssets.Add("file guid " + bpr.External.FileGuid);
                }
                else if (type == "SubWorldReferenceObjectData")
                {
                    string bundle = Field(obj, "BundleName")?.ToString();
                    if (!string.IsNullOrEmpty(bundle))
                    {
                        string leaf = bundle.Substring(bundle.LastIndexOf('/') + 1).ToLower();
                        if (SkipSublevels.Contains(leaf))
                            return;
                        WalkAsset(bundle, m, layer, bundle, depth + 1);
                    }
                }
                return;
            }

            if (type == "StaticModelEntityData")
            {
                if (Field(obj, "Mesh") is PointerRef mr && mr.Type == PointerRefType.External)
                {
                    EbxAssetEntry meshEntry = App.AssetManager.GetEbxEntry(mr.External.FileGuid);
                    if (meshEntry != null)
                    {
                        instances.Add(new Instance
                        {
                            Mesh = meshEntry.Name, MeshRootGuid = mr.External.ClassGuid,
                            Matrix = Mul(Transform(Field(obj, "Transform")), matrix),
                            Layer = layer, Source = source, Kind = "static_model"
                        });
                    }
                }
                return;
            }

            if (type == "StaticModelGroupEntityData")
            {
                double[,] m0 = Mul(Transform(Field(obj, "Transform")), matrix);
                System.Collections.IEnumerable members = Field(obj, "MemberDatas") as System.Collections.IEnumerable;
                if (members == null)
                    return;
                foreach (object member in members)
                {
                    if (!(Field(member, "MeshAsset") is PointerRef mr) || mr.Type != PointerRefType.External)
                        continue;
                    EbxAssetEntry meshEntry = App.AssetManager.GetEbxEntry(mr.External.FileGuid);
                    if (meshEntry == null)
                        continue;
                    string memberSource = source;
                    if (Field(member, "MemberType") is PointerRef tr && tr.Type == PointerRefType.External)
                    {
                        EbxAssetEntry te = App.AssetManager.GetEbxEntry(tr.External.FileGuid);
                        if (te != null)
                            memberSource = te.Name;
                    }
                    System.Collections.IEnumerable transforms = Field(member, "InstanceTransforms") as System.Collections.IEnumerable;
                    if (transforms == null)
                        continue;
                    foreach (object lt in transforms)
                    {
                        instances.Add(new Instance
                        {
                            Mesh = meshEntry.Name, MeshRootGuid = mr.External.ClassGuid, Matrix = Mul(Transform(lt), m0),
                            Layer = layer, Source = memberSource, Kind = "group"
                        });
                    }
                }
                return;
            }

            skippedTypes[type] = skippedTypes.TryGetValue(type, out int c) ? c + 1 : 1;
        }

        private static IEnumerable<PointerRef> PointerList(object list)
        {
            if (list is System.Collections.IEnumerable e)
                foreach (object o in e)
                    if (o is PointerRef pr)
                        yield return pr;
        }

        private static object Field(object o, string name)
        {
            if (o == null) return null;
            System.Reflection.PropertyInfo p = o.GetType().GetProperty(name);
            return p?.GetValue(o);
        }

        // ─── transforms (row vectors, Frostbite LinearTransform) ────────────

        private static double[,] Identity()
        {
            return new double[4, 4] { { 1, 0, 0, 0 }, { 0, 1, 0, 0 }, { 0, 0, 1, 0 }, { 0, 0, 0, 1 } };
        }

        private static double[,] Transform(object lt)
        {
            if (lt == null)
                return Identity();
            double[] r = Vec(Field(lt, "right")), u = Vec(Field(lt, "up")), f = Vec(Field(lt, "forward")), t = Vec(Field(lt, "trans"));
            return new double[4, 4]
            {
                { r[0], r[1], r[2], 0 }, { u[0], u[1], u[2], 0 }, { f[0], f[1], f[2], 0 }, { t[0], t[1], t[2], 1 }
            };
        }

        private static double[] Vec(object v)
        {
            if (v == null) return new double[] { 0, 0, 0 };
            return new[] { Convert.ToDouble(Field(v, "x")), Convert.ToDouble(Field(v, "y")), Convert.ToDouble(Field(v, "z")) };
        }

        private static double[,] Mul(double[,] a, double[,] b)
        {
            double[,] m = new double[4, 4];
            for (int i = 0; i < 4; i++)
                for (int j = 0; j < 4; j++)
                {
                    double s = 0;
                    for (int k = 0; k < 4; k++) s += a[i, k] * b[k, j];
                    m[i, j] = s;
                }
            return m;
        }

        // ─── mesh geometry ──────────────────────────────────────────────────

        private void WriteMesh(string meshName, string meshDir, MeshInfo info)
        {
            EbxAssetEntry entry = App.AssetManager.GetEbxEntry(meshName);
            if (entry == null) { info.Reason = "mesh asset not found"; return; }
            EbxAsset asset = App.AssetManager.GetEbx(entry);
            if (asset == null) { info.Reason = "mesh asset could not be read"; return; }
            ulong rid = ((dynamic)asset.RootObject).MeshSetResource;
            ResAssetEntry resEntry = App.AssetManager.GetResEntry(rid);
            if (resEntry == null) { info.Reason = "no MeshSet res"; return; }
            MeshSet meshSet = App.AssetManager.GetResAs<MeshSet>(resEntry);
            if (meshSet == null || meshSet.Lods.Count == 0) { info.Reason = "MeshSet res could not be read"; return; }

            MeshSetLod lod = meshSet.Lods[0];
            byte[] chunk = lod.InlineData;
            ChunkAssetEntry chunkEntry = App.AssetManager.GetChunkEntry(lod.ChunkId);
            if (chunkEntry != null)
            {
                using (NativeReader reader = new NativeReader(App.AssetManager.GetChunk(chunkEntry)))
                    chunk = reader.ReadToEnd();
            }
            if (chunk == null || chunk.Length == 0) { info.Reason = "chunk " + lod.ChunkId + " not available"; return; }

            string fileName = SafeName(meshName) + ".fbm";
            string path = Path.Combine(meshDir, fileName);
            using (BinaryWriter w = new BinaryWriter(new FileStream(path, FileMode.Create)))
            {
                w.Write(Encoding.ASCII.GetBytes("FBMESH01"));
                w.Write((uint)lod.Sections.Count);
                foreach (MeshSetSection section in lod.Sections)
                {
                    SectionData sd = DecodeSection(lod, section, chunk);
                    byte[] mat = Encoding.UTF8.GetBytes(section.Name ?? "");
                    w.Write((uint)mat.Length); w.Write(mat);
                    w.Write(section.MaterialId);
                    uint flags = 0;
                    for (int cat = 0; cat < 5; cat++)
                        if (lod.IsSectionInCategory(section, (MeshSetPlugin.Resources.MeshSubsetCategory)cat))
                            flags |= 1u << cat;
                    w.Write(flags);
                    int nv = sd.VertexCount;
                    w.Write((uint)nv); w.Write((uint)sd.Indices.Length);
                    w.Write(sd.Normals != null ? 1u : 0u);
                    w.Write((uint)sd.Uvs.Count); w.Write((uint)sd.Colors.Count);
                    WriteFloats(w, sd.Positions);
                    if (sd.Normals != null) WriteFloats(w, sd.Normals);
                    foreach (KeyValuePair<string, float[]> uv in sd.Uvs)
                    {
                        byte[] nm = Encoding.UTF8.GetBytes(uv.Key);
                        w.Write((uint)nm.Length); w.Write(nm);
                        WriteFloats(w, uv.Value);
                    }
                    foreach (KeyValuePair<string, byte[]> col in sd.Colors)
                    {
                        byte[] nm = Encoding.UTF8.GetBytes(col.Key);
                        w.Write((uint)nm.Length); w.Write(nm);
                        w.Write(col.Value);
                    }
                    foreach (uint idx in sd.Indices) w.Write(idx);
                    info.Sections.Add(JsonObject(
                        ("material", section.Name ?? ""), ("material_id", section.MaterialId), ("vertices", nv),
                        ("triangles", sd.Indices.Length / 3), ("flags", (int)flags),
                        ("lightmap_uv_mapping_index", (int)section.LightMapUvMappingIndex),
                        ("material_key", string.IsNullOrEmpty(section.Name) ? "" : meshName + "|" + section.MaterialId)));
                }
            }
            try
            {
                ExportMaterials(entry, asset, meshName, lod);
            }
            catch (Exception ex)
            {
                notes.Add("materials of " + meshName + ": " + ex.Message);
                App.Logger.Log($"  materials {meshName}: {ex}");
            }
            info.File = "meshes/" + fileName;
            info.MeshType = meshSet.Type.ToString().Replace("MeshType_", "");
            info.BoxMin = new[] { meshSet.BoundingBox.min.x, meshSet.BoundingBox.min.y, meshSet.BoundingBox.min.z };
            info.BoxMax = new[] { meshSet.BoundingBox.max.x, meshSet.BoundingBox.max.y, meshSet.BoundingBox.max.z };
        }

        // ─── materials and textures ────────────────────────────────────────

        /// <summary>
        /// One MaterialInfo per material the lod 0 sections use: the shader parameters are collected along the
        /// preset chain (base shader first, the mesh's own MeshMaterial last), then the mesh variation
        /// database's entry for the mesh (default variation) overrides the textures - that is what the game
        /// renders with. Every texture is exported once to textures/ as dds.
        /// </summary>
        private void ExportMaterials(EbxAssetEntry meshEntry, EbxAsset meshAsset, string meshName, MeshSetLod lod)
        {
            object meshMaterials = Field(meshAsset.RootObject, "Materials");
            List<PointerRef> materialRefs = PointerList(meshMaterials).ToList();

            // the variation database entry (default variation) of this mesh: material class guid -> texture parameters
            Dictionary<Guid, object> dbTextures = new Dictionary<Guid, object>();
            try
            {
                if (!Frosty.Core.Viewport.MeshVariationDb.IsLoaded)
                    Frosty.Core.Viewport.MeshVariationDb.LoadVariations(task);
                Frosty.Core.Viewport.MeshVariationDbEntry dbEntry = Frosty.Core.Viewport.MeshVariationDb.GetVariations(meshEntry.Guid);
                if (dbEntry != null)
                {
                    Frosty.Core.Viewport.MeshVariation variation = dbEntry.GetVariation(0);
                    if (variation == null && dbEntry.Variations.Count > 0)
                        variation = dbEntry.Variations.Values.First();
                    if (variation != null)
                    {
                        foreach (Frosty.Core.Viewport.MeshVariationMaterial m in variation.Materials)
                            dbTextures[m.MaterialGuid] = m.TextureParameters;
                    }
                }
            }
            catch (Exception ex)
            {
                notes.Add("mesh variation database for " + meshName + ": " + ex.Message);
            }

            HashSet<int> used = new HashSet<int>();
            foreach (MeshSetSection section in lod.Sections)
            {
                if (string.IsNullOrEmpty(section.Name))
                    continue;
                used.Add(section.MaterialId);
            }

            foreach (int index in used)
            {
                string key = meshName + "|" + index;
                if (materials.ContainsKey(key))
                    continue;
                MaterialInfo mi = new MaterialInfo { Key = key, Mesh = meshName, Index = index };
                materials[key] = mi;
                if (index < 0 || index >= materialRefs.Count)
                {
                    notes.Add(key + ": no material object (the mesh has " + materialRefs.Count + ")");
                    continue;
                }
                object material = materialRefs[index].Internal;
                if (material == null)
                    continue;
                mi.Name = Convert.ToString(Field(material, "__Id"));

                // the preset chain, innermost first
                List<object> chain = new List<object>(); // SurfaceShaderInstanceDataStruct objects
                object inst = Field(material, "Shader");
                int guard = 0;
                while (inst != null && guard++ < 16)
                {
                    chain.Add(inst);
                    object shaderRef = Field(inst, "Shader");
                    if (!(shaderRef is PointerRef pr) || pr.Type != PointerRefType.External)
                        break;
                    EbxAssetEntry presetEntry = App.AssetManager.GetEbxEntry(pr.External.FileGuid);
                    if (presetEntry == null)
                    {
                        missingAssets.Add(pr.External.FileGuid.ToString());
                        break;
                    }
                    if (presetEntry.Type == "SurfaceShaderPreset")
                    {
                        mi.Presets.Add(presetEntry.Name);
                        EbxAsset preset = Load(presetEntry.Name);
                        inst = preset != null ? Field(preset.RootObject, "ShaderPreset") : null;
                    }
                    else
                    {
                        mi.Shader = presetEntry.Name;
                        break;
                    }
                }

                // base first so the inner levels override
                for (int c = chain.Count - 1; c >= 0; c--)
                    CollectShaderParameters(chain[c], mi);

                // the variation database wins
                AssetClassGuid guid = ((dynamic)material).GetInstanceGuid();
                if (dbTextures.TryGetValue(guid.ExportedGuid, out object dbParams) && dbParams != null)
                {
                    CollectTextureParameters(dbParams, mi);
                    mi.FromVariationDb = true;
                }

                foreach (KeyValuePair<string, string> kv in mi.TextureAssets.ToList())
                {
                    string file = ExportMaterialTexture(kv.Value);
                    if (file != null)
                        mi.Textures[kv.Key] = file;
                }
            }
        }

        private void CollectShaderParameters(object inst, MaterialInfo mi)
        {
            CollectTextureParameters(Field(inst, "TextureParameters"), mi);
            object vectors = Field(inst, "VectorParameters");
            if (vectors is System.Collections.IEnumerable ve)
            {
                foreach (object p in ve)
                {
                    string name = Convert.ToString(Field(p, "ParameterName"));
                    object v = Field(p, "Value");
                    if (string.IsNullOrEmpty(name) || v == null)
                        continue;
                    mi.Vectors[name] = new[]
                    {
                        Convert.ToSingle(Field(v, "x") ?? 0f), Convert.ToSingle(Field(v, "y") ?? 0f),
                        Convert.ToSingle(Field(v, "z") ?? 0f), Convert.ToSingle(Field(v, "w") ?? 0f)
                    };
                }
            }
            object bools = Field(inst, "BoolParameters");
            if (bools is System.Collections.IEnumerable be)
            {
                foreach (object p in be)
                {
                    string name = Convert.ToString(Field(p, "ParameterName"));
                    object v = Field(p, "Value");
                    if (!string.IsNullOrEmpty(name) && v != null)
                        mi.Bools[name] = Convert.ToBoolean(v);
                }
            }
        }

        private void CollectTextureParameters(object list, MaterialInfo mi)
        {
            if (!(list is System.Collections.IEnumerable e))
                return;
            foreach (object p in e)
            {
                string name = Convert.ToString(Field(p, "ParameterName"));
                if (string.IsNullOrEmpty(name))
                    continue;
                if (Field(p, "Value") is PointerRef pr && pr.Type == PointerRefType.External)
                {
                    EbxAssetEntry tex = App.AssetManager.GetEbxEntry(pr.External.FileGuid);
                    if (tex != null)
                        mi.TextureAssets[name] = tex.Name;
                    else
                        missingAssets.Add(pr.External.FileGuid.ToString());
                }
            }
        }

        /// <summary>Exports a texture asset once as textures/&lt;safe name&gt;.dds; returns the package path or null.</summary>
        private string ExportMaterialTexture(string assetName)
        {
            if (textureFiles.TryGetValue(assetName, out string known))
                return known;
            string result = null;
            try
            {
                EbxAssetEntry entry = App.AssetManager.GetEbxEntry(assetName);
                AssetDefinition definition = entry != null ? App.PluginManager.GetAssetDefinition(entry.Type) : null;
                if (entry == null || definition == null)
                {
                    textureFailures.Add(assetName + ": " + (entry == null ? "asset not found" : "no exporter for " + entry.Type));
                }
                else
                {
                    Directory.CreateDirectory(textureDir);
                    string file = SafeName(entry.Name) + ".dds";
                    string path = Path.Combine(textureDir, file);
                    if (!File.Exists(path))
                        definition.ExportSilent(entry, path, "dds", task);
                    if (File.Exists(path))
                        result = "textures/" + file;
                    else
                        textureFailures.Add(assetName + ": not written as dds");
                }
            }
            catch (Exception ex)
            {
                textureFailures.Add(assetName + ": " + ex.Message);
            }
            textureFiles[assetName] = result;
            return result;
        }

        internal class SectionData
        {
            public int VertexCount;
            public float[] Positions;
            public float[] Normals;
            public Dictionary<string, float[]> Uvs = new Dictionary<string, float[]>();
            public Dictionary<string, byte[]> Colors = new Dictionary<string, byte[]>();
            public uint[] Indices;
        }

        internal static SectionData DecodeSection(MeshSetLod lod, MeshSetSection section, byte[] chunk)
        {
            int nv = (int)section.VertexCount;
            SectionData sd = new SectionData { VertexCount = nv, Positions = new float[nv * 3] };
            GeometryDeclarationDesc decl = section.GeometryDeclDesc[0];
            long streamBase = section.VertexOffset;
            for (int si = 0; si < decl.Streams.Length; si++)
            {
                GeometryDeclarationDesc.Stream stream = decl.Streams[si];
                if (stream.VertexStride == 0)
                    continue;
                foreach (GeometryDeclarationDesc.Element el in decl.Elements)
                {
                    if (el.StreamIndex != si || el.Usage == VertexElementUsage.Unknown)
                        continue;
                    float[] target = null;
                    int comps = 0;
                    byte[] bytesTarget = null;
                    string usage = el.Usage.ToString();
                    if (el.Usage == VertexElementUsage.Pos) { target = sd.Positions; comps = 3; }
                    else if (el.Usage == VertexElementUsage.Normal) { sd.Normals = target = new float[nv * 3]; comps = 3; }
                    else if (usage.StartsWith("TexCoord")) { target = new float[nv * 2]; comps = 2; sd.Uvs[usage] = target; }
                    else if (el.Usage == VertexElementUsage.RadiosityTexCoord) { target = new float[nv * 2]; comps = 2; sd.Uvs["Lightmap"] = target; }
                    else if (usage.StartsWith("Color")) { bytesTarget = new byte[nv * 4]; sd.Colors[usage] = bytesTarget; }
                    else continue;

                    for (int v = 0; v < nv; v++)
                    {
                        long off = streamBase + (long)v * stream.VertexStride + el.Offset;
                        if (bytesTarget != null)
                        {
                            float[] c = Decode(el.Format, chunk, off);
                            for (int k = 0; k < 4; k++)
                                bytesTarget[v * 4 + k] = (byte)Math.Max(0, Math.Min(255, Math.Round((k < c.Length ? c[k] : 1.0f) * 255.0f)));
                        }
                        else
                        {
                            float[] c = Decode(el.Format, chunk, off);
                            for (int k = 0; k < comps; k++)
                                target[v * comps + k] = k < c.Length ? c[k] : 0.0f;
                        }
                    }
                }
                streamBase += (long)stream.VertexStride * nv;
            }

            // indices (section-local), triangle lists; strips are unrolled
            int unit = lod.IndexUnitSize / 8;
            long ib = lod.VertexBufferSize + (long)section.StartIndex * unit;
            bool strip = section.PrimitiveType == MeshSetPlugin.Resources.PrimitiveType.PrimitiveType_TriangleStrip;
            int count = strip ? (int)section.PrimitiveCount + 2 : (int)section.PrimitiveCount * 3;
            uint[] raw = new uint[count];
            for (int i = 0; i < count; i++)
                raw[i] = unit == 2 ? BitConverter.ToUInt16(chunk, (int)(ib + i * 2)) : BitConverter.ToUInt32(chunk, (int)(ib + i * 4));
            if (strip)
            {
                List<uint> tris = new List<uint>();
                for (int i = 0; i + 2 < raw.Length; i++)
                {
                    uint a = raw[i], b = raw[i + 1], c = raw[i + 2];
                    if (a == b || b == c || a == c) continue;
                    if (i % 2 == 0) { tris.Add(a); tris.Add(b); tris.Add(c); }
                    else { tris.Add(b); tris.Add(a); tris.Add(c); }
                }
                raw = tris.ToArray();
            }
            sd.Indices = raw;
            return sd;
        }

        internal static float[] Decode(VertexElementFormat fmt, byte[] b, long off)
        {
            int o = (int)off;
            switch (fmt)
            {
                case VertexElementFormat.Float: return new[] { BitConverter.ToSingle(b, o) };
                case VertexElementFormat.Float2: return new[] { BitConverter.ToSingle(b, o), BitConverter.ToSingle(b, o + 4) };
                case VertexElementFormat.Float3: return new[] { BitConverter.ToSingle(b, o), BitConverter.ToSingle(b, o + 4), BitConverter.ToSingle(b, o + 8) };
                case VertexElementFormat.Float4: return new[] { BitConverter.ToSingle(b, o), BitConverter.ToSingle(b, o + 4), BitConverter.ToSingle(b, o + 8), BitConverter.ToSingle(b, o + 12) };
                case VertexElementFormat.Half: return new[] { HalfUtils.Unpack(BitConverter.ToUInt16(b, o)) };
                case VertexElementFormat.Half2: return new[] { HalfUtils.Unpack(BitConverter.ToUInt16(b, o)), HalfUtils.Unpack(BitConverter.ToUInt16(b, o + 2)) };
                case VertexElementFormat.Half3: return new[] { HalfUtils.Unpack(BitConverter.ToUInt16(b, o)), HalfUtils.Unpack(BitConverter.ToUInt16(b, o + 2)), HalfUtils.Unpack(BitConverter.ToUInt16(b, o + 4)) };
                case VertexElementFormat.Half4: return new[] { HalfUtils.Unpack(BitConverter.ToUInt16(b, o)), HalfUtils.Unpack(BitConverter.ToUInt16(b, o + 2)), HalfUtils.Unpack(BitConverter.ToUInt16(b, o + 4)), HalfUtils.Unpack(BitConverter.ToUInt16(b, o + 6)) };
                case VertexElementFormat.UByte4N: return new[] { b[o] / 255f, b[o + 1] / 255f, b[o + 2] / 255f, b[o + 3] / 255f };
                case VertexElementFormat.UByte4: return new float[] { b[o], b[o + 1], b[o + 2], b[o + 3] };
                case VertexElementFormat.Byte4N: return new[] { Math.Max((sbyte)b[o] / 127f, -1f), Math.Max((sbyte)b[o + 1] / 127f, -1f), Math.Max((sbyte)b[o + 2] / 127f, -1f), Math.Max((sbyte)b[o + 3] / 127f, -1f) };
                case VertexElementFormat.UShort2N: return new[] { BitConverter.ToUInt16(b, o) / 65535f, BitConverter.ToUInt16(b, o + 2) / 65535f };
                case VertexElementFormat.UShort4N: return new[] { BitConverter.ToUInt16(b, o) / 65535f, BitConverter.ToUInt16(b, o + 2) / 65535f, BitConverter.ToUInt16(b, o + 4) / 65535f, BitConverter.ToUInt16(b, o + 6) / 65535f };
                case VertexElementFormat.Short2N: return new[] { Math.Max(BitConverter.ToInt16(b, o) / 32767f, -1f), Math.Max(BitConverter.ToInt16(b, o + 2) / 32767f, -1f) };
                case VertexElementFormat.Short4N: return new[] { Math.Max(BitConverter.ToInt16(b, o) / 32767f, -1f), Math.Max(BitConverter.ToInt16(b, o + 2) / 32767f, -1f), Math.Max(BitConverter.ToInt16(b, o + 4) / 32767f, -1f), Math.Max(BitConverter.ToInt16(b, o + 6) / 32767f, -1f) };
                case VertexElementFormat.UShort2: return new float[] { BitConverter.ToUInt16(b, o), BitConverter.ToUInt16(b, o + 2) };
                case VertexElementFormat.UShort4: return new float[] { BitConverter.ToUInt16(b, o), BitConverter.ToUInt16(b, o + 2), BitConverter.ToUInt16(b, o + 4), BitConverter.ToUInt16(b, o + 6) };
                case VertexElementFormat.UComp4N_10_10_10_2:
                case VertexElementFormat.UComp3N_10_10_10:
                {
                    uint v = BitConverter.ToUInt32(b, o);
                    return new[] { (v & 0x3ff) / 1023f, ((v >> 10) & 0x3ff) / 1023f, ((v >> 20) & 0x3ff) / 1023f, (v >> 30) / 3f };
                }
                case VertexElementFormat.Comp4N_10_10_10_2:
                case VertexElementFormat.Comp3N_10_10_10:
                {
                    uint v = BitConverter.ToUInt32(b, o);
                    return new[] { Sn10(v & 0x3ff), Sn10((v >> 10) & 0x3ff), Sn10((v >> 20) & 0x3ff), (float)(v >> 30) };
                }
                default:
                    return new float[0];
            }
        }

        private static float Sn10(uint a)
        {
            int s = (a & 0x200) != 0 ? (int)a - 1024 : (int)a;
            return Math.Max(s / 511f, -1f);
        }

        private static void WriteFloats(BinaryWriter w, float[] values)
        {
            byte[] buf = new byte[values.Length * 4];
            Buffer.BlockCopy(values, 0, buf, 0, buf.Length);
            w.Write(buf);
        }

        private static string SafeName(string assetName)
        {
            string s = assetName.Replace('\\', '/');
            if (s.StartsWith("content/worlds/", StringComparison.OrdinalIgnoreCase))
                s = s.Substring("content/worlds/".Length);
            StringBuilder sb = new StringBuilder();
            bool sep = false;
            foreach (char c in s)
            {
                if (char.IsLetterOrDigit(c) || c == '_' || c == '.' || c == '-') { sb.Append(c); sep = false; }
                else if (!sep) { sb.Append('_'); sep = true; }
            }
            return sb.ToString();
        }

        // ─── baked (night) lighting ──────────────────────────────────────────

        /// <summary>
        /// content/worlds/stadiums/[folder]/Lighting/[folder] (EnlightenDataAsset) names the Enlighten database
        /// res: a header (atlas width/height ...) and a sorted table of instance keys -> 32-byte records
        /// {sx, 0, 0, sy, tx, ty, 0, 0}: an instance's radiosity UVs times (sx, sy) plus (tx, ty) are its chart in the
        /// lightmap atlases. The key is FNV-1 over the mesh's root instance GUID and its placement (FUN_146b18400).
        /// The night atlases (StaticEnlightenData [folder]_flux_night) are written as DDS beside the meshes.
        /// </summary>
        private LightingInfo ExportLighting(string levelName, string outDir)
        {
            string folder = levelName.Substring(0, levelName.LastIndexOf('/'));
            folder = folder.Substring(folder.LastIndexOf('/') + 1);
            string baseName = "content/worlds/stadiums/" + folder + "/Lighting/" + folder;
            EbxAssetEntry dataEntry = App.AssetManager.GetEbxEntry(baseName);
            if (dataEntry == null)
            {
                notes.Add("no lighting database asset " + baseName);
                return null;
            }
            EbxAsset dataAsset = App.AssetManager.GetEbx(dataEntry);
            ulong rid = ((dynamic)dataAsset.RootObject).DatabaseResource;
            ResAssetEntry resEntry = App.AssetManager.GetResEntry(rid);
            if (resEntry == null)
            {
                notes.Add("lighting database res not found for " + baseName);
                return null;
            }
            byte[] db;
            using (NativeReader reader = new NativeReader(App.AssetManager.GetRes(resEntry)))
                db = reader.ReadToEnd();

            LightingInfo info = new LightingInfo { Database = baseName };
            info.AtlasWidth = BitConverter.ToUInt32(db, 0);
            info.AtlasHeight = BitConverter.ToUInt32(db, 4);
            info.Count = (uint)BitConverter.ToUInt64(db, 0x18);
            long tableOffset = BitConverter.ToInt64(db, 0x20);
            Dictionary<uint, float[]> charts = new Dictionary<uint, float[]>();
            for (uint i = 0; i < info.Count; i++)
            {
                long rec = tableOffset + 32 * i;
                uint key = BitConverter.ToUInt32(db, (int)rec);
                long uvOffset = BitConverter.ToInt64(db, (int)rec + 8);
                charts[key] = new[]
                {
                    BitConverter.ToSingle(db, (int)uvOffset), BitConverter.ToSingle(db, (int)uvOffset + 12),
                    BitConverter.ToSingle(db, (int)uvOffset + 16), BitConverter.ToSingle(db, (int)uvOffset + 20)
                };
            }
            foreach (Instance inst in instances)
            {
                uint key = InstanceKey(inst.MeshRootGuid, inst.Matrix);
                if (charts.TryGetValue(key, out float[] chart))
                {
                    inst.Lightmap = chart;
                    inst.LightmapKey = key;
                    info.Matched++;
                }
            }
            object fluxScale = Field(dataAsset.RootObject, "FluxLightmapScale");
            info.FluxScale = fluxScale != null ? Convert.ToInt32(fluxScale) : 0;
            info.OutputFormat = Field(dataAsset.RootObject, "OutputFormat")?.ToString();

            string lightingDir = Path.Combine(outDir, "lighting");
            ExportTexture(Field(dataAsset.RootObject, "SkyVisibilityTexture"), "sky_visibility", lightingDir, info);
            EbxAssetEntry fluxEntry = App.AssetManager.GetEbxEntry(baseName + "_flux_night");
            if (fluxEntry != null)
            {
                EbxAsset flux = App.AssetManager.GetEbx(fluxEntry);
                ExportTexture(Field(flux.RootObject, "StaticIrradianceTexture"), "irradiance", lightingDir, info);
                ExportTexture(Field(flux.RootObject, "StaticDirectionTexture"), "direction", lightingDir, info);
                try
                {
                    ExportProbes(db, flux, lightingDir, info);
                }
                catch (Exception ex)
                {
                    info.Notes.Add("light probes not exported: " + ex.Message);
                }
            }
            else
            {
                info.Notes.Add("no " + baseName + "_flux_night (no baked night lighting)");
            }
            App.Logger.Log($"  lighting: {info.Matched} of {instances.Count} instances in the {info.AtlasWidth}x{info.AtlasHeight} atlas, {info.Count} keys in the database");
            return info;
        }

        /// <summary>
        /// The light probes: positions from the dynamic database's probe sets (0xf0-byte records at the offset
        /// stored at 0x40: set GUID, transform, inverse, unit box, spacing, grid, type, first probe, flags, four
        /// per-probe arrays - the second one holds the world positions), the night lighting from the static
        /// database (header {w, h, count, probes*, pairCount, pairs*}; one 36-float record per probe: 9 spherical
        /// harmonics coefficients x (r, g, b, w)) -> lighting/probes.json.
        /// </summary>
        private void ExportProbes(byte[] dynamicDb, EbxAsset flux, string lightingDir, LightingInfo info)
        {
            ulong rid = ((dynamic)flux.RootObject).DatabaseResource;
            ResAssetEntry resEntry = App.AssetManager.GetResEntry(rid);
            if (resEntry == null)
            {
                info.Notes.Add("static lighting database res not found");
                return;
            }
            byte[] st;
            using (NativeReader reader = new NativeReader(App.AssetManager.GetRes(resEntry)))
                st = reader.ReadToEnd();

            uint probeCount = BitConverter.ToUInt32(st, 8);
            long probesOffset = BitConverter.ToInt64(st, 0x0c);
            uint setCount = BitConverter.ToUInt32(dynamicDb, 0x3c);
            long setsOffset = BitConverter.ToInt64(dynamicDb, 0x40);
            uint dynamicProbeCount = 0;
            for (uint i = 0; i < setCount; i++)
                dynamicProbeCount += BitConverter.ToUInt32(dynamicDb, (int)(setsOffset + i * 0xf0 + 0xc4));
            if (dynamicProbeCount != probeCount)
            {
                info.Notes.Add($"static database has {probeCount} probes, dynamic database {dynamicProbeCount}; probes not exported");
                return;
            }

            Directory.CreateDirectory(lightingDir);
            string path = Path.Combine(lightingDir, "probes.json");
            using (StreamWriter w = new StreamWriter(path, false, new UTF8Encoding(false)))
            {
                w.Write("{\"count\": " + probeCount + ", \"sh\": \"per probe 9 coefficients x (r, g, b, w): L2 spherical harmonics of the night lighting, w = L1 extra (DC in [0,1])\", \"coordinate_system\": \"Frostbite: Y up\", \"sets\": [");
                for (uint i = 0; i < setCount; i++)
                {
                    int o = (int)(setsOffset + i * 0xf0);
                    Guid guid = new Guid(SubArray(dynamicDb, o, 16));
                    float spacing = BitConverter.ToSingle(dynamicDb, o + 0xb0);
                    uint nx = BitConverter.ToUInt32(dynamicDb, o + 0xb4), ny = BitConverter.ToUInt32(dynamicDb, o + 0xb8), nz = BitConverter.ToUInt32(dynamicDb, o + 0xbc);
                    uint type = BitConverter.ToUInt32(dynamicDb, o + 0xc0), count = BitConverter.ToUInt32(dynamicDb, o + 0xc4);
                    uint first = BitConverter.ToUInt32(dynamicDb, o + 0xc8);
                    long positions = BitConverter.ToInt64(dynamicDb, o + 0xd8);
                    if (i > 0) w.Write(", ");
                    w.Write("{\"guid\": " + JsonString(guid.ToString()) + ", \"type\": " + type + ", \"grid\": [" + nx + ", " + ny + ", " + nz + "], \"spacing\": " + spacing.ToString("R", CultureInfo.InvariantCulture)
                        + ", \"first\": " + first + ", \"count\": " + count + ", \"matrix\": [");
                    for (int r = 0; r < 4; r++)
                    {
                        if (r > 0) w.Write(", ");
                        w.Write("[" + JsonFloats(new[] { BitConverter.ToSingle(dynamicDb, o + 0x10 + r * 16), BitConverter.ToSingle(dynamicDb, o + 0x14 + r * 16), BitConverter.ToSingle(dynamicDb, o + 0x18 + r * 16), BitConverter.ToSingle(dynamicDb, o + 0x1c + r * 16) }) + "]");
                    }
                    w.Write("], \"positions\": [");
                    for (uint k = 0; k < count; k++)
                    {
                        int po = (int)(positions + k * 16);
                        if (k > 0) w.Write(", ");
                        w.Write("[" + JsonFloats(new[] { BitConverter.ToSingle(dynamicDb, po), BitConverter.ToSingle(dynamicDb, po + 4), BitConverter.ToSingle(dynamicDb, po + 8) }) + "]");
                    }
                    w.Write("], \"sh\": [");
                    for (uint k = 0; k < count; k++)
                    {
                        int so = (int)(probesOffset + (first + k) * 144);
                        float[] sh = new float[36];
                        for (int c = 0; c < 36; c++)
                            sh[c] = BitConverter.ToSingle(st, so + c * 4);
                        if (k > 0) w.Write(", ");
                        w.Write("[" + JsonFloats(sh) + "]");
                    }
                    w.Write("]}");
                }
                w.Write("]}");
            }
            info.ProbesFile = "lighting/probes.json";
            info.ProbeCount = (int)probeCount;
            info.ProbeSets = (int)setCount;
            App.Logger.Log($"  lighting: {probeCount} light probes in {setCount} sets -> lighting/probes.json");
        }

        private static byte[] SubArray(byte[] data, int offset, int length)
        {
            byte[] result = new byte[length];
            Array.Copy(data, offset, result, 0, length);
            return result;
        }

        private void ExportTexture(object pointer, string label, string lightingDir, LightingInfo info)
        {
            if (!(pointer is PointerRef pr) || pr.Type != PointerRefType.External)
                return;
            EbxAssetEntry entry = App.AssetManager.GetEbxEntry(pr.External.FileGuid);
            if (entry == null)
            {
                info.Notes.Add(label + ": texture asset not found");
                return;
            }
            AssetDefinition definition = App.PluginManager.GetAssetDefinition(entry.Type);
            if (definition == null)
            {
                info.Notes.Add(label + ": no exporter for " + entry.Type);
                return;
            }
            Directory.CreateDirectory(lightingDir);
            string file = SafeName(entry.Name) + ".dds";
            string path = Path.Combine(lightingDir, file);
            try
            {
                definition.ExportSilent(entry, path, "dds", task);
            }
            catch (Exception ex)
            {
                info.Notes.Add(label + ": export failed: " + ex.Message);
                return;
            }
            if (File.Exists(path))
                info.Textures[label] = "lighting/" + file;
            else
                info.Notes.Add(label + ": " + entry.Name + " was not written as dds");
        }

        /// <summary>FNV-1 (0x811c9dc5, multiply then xor) over the 56 bytes FIFA17.exe hashes for a mesh instance.</summary>
        private static uint InstanceKey(Guid rootGuid, double[,] m)
        {
            byte[] buf = new byte[56];
            Array.Copy(rootGuid.ToByteArray(), buf, 16);
            double[] q = QuatFromMatrix(m);
            int[] ints = new int[10];
            for (int i = 0; i < 3; i++) ints[i] = ScaledInt(m[3, i], 100f);
            for (int i = 0; i < 4; i++) ints[3 + i] = ScaledInt(q[i], 10000f);
            for (int i = 0; i < 3; i++)
                ints[7 + i] = ScaledInt(Math.Sqrt(m[i, 0] * m[i, 0] + m[i, 1] * m[i, 1] + m[i, 2] * m[i, 2]), 100f);
            for (int i = 0; i < 10; i++)
                Array.Copy(BitConverter.GetBytes(ints[i]), 0, buf, 16 + 4 * i, 4);
            uint h = 0x811c9dc5;
            foreach (byte b in buf)
            {
                h = unchecked(h * 0x01000193);
                h ^= b;
            }
            return h;
        }

        /// <summary>(int)(v * scale + 0.4999f) in single precision, as the exe does it.</summary>
        private static int ScaledInt(double v, float scale)
        {
            float f = (float)v * scale + 0.4999f;
            return (int)f;
        }

        /// <summary>Rotation quaternion (x, y, z, w) of the row-vector matrix (rows right/up/forward, normalised).</summary>
        private static double[] QuatFromMatrix(double[,] m)
        {
            double[,] r = new double[3, 3];
            for (int i = 0; i < 3; i++)
            {
                double l = Math.Sqrt(m[i, 0] * m[i, 0] + m[i, 1] * m[i, 1] + m[i, 2] * m[i, 2]);
                if (l == 0) l = 1;
                for (int j = 0; j < 3; j++) r[i, j] = m[i, j] / l;
            }
            double m00 = r[0, 0], m01 = r[0, 1], m02 = r[0, 2], m10 = r[1, 0], m11 = r[1, 1], m12 = r[1, 2], m20 = r[2, 0], m21 = r[2, 1], m22 = r[2, 2];
            double tr = m00 + m11 + m22, x, y, z, w;
            if (tr > 0)
            {
                double s = Math.Sqrt(tr + 1.0) * 2;
                w = 0.25 * s; x = (m12 - m21) / s; y = (m20 - m02) / s; z = (m01 - m10) / s;
            }
            else if (m00 > m11 && m00 > m22)
            {
                double s = Math.Sqrt(1.0 + m00 - m11 - m22) * 2;
                w = (m12 - m21) / s; x = 0.25 * s; y = (m01 + m10) / s; z = (m02 + m20) / s;
            }
            else if (m11 > m22)
            {
                double s = Math.Sqrt(1.0 + m11 - m00 - m22) * 2;
                w = (m20 - m02) / s; x = (m01 + m10) / s; y = 0.25 * s; z = (m12 + m21) / s;
            }
            else
            {
                double s = Math.Sqrt(1.0 + m22 - m00 - m11) * 2;
                w = (m01 - m10) / s; x = (m02 + m20) / s; y = (m12 + m21) / s; z = 0.25 * s;
            }
            return new[] { x, y, z, w };
        }

        // ─── scene.json ─────────────────────────────────────────────────────

        private void WriteSceneJson(string path, string levelName, string stadium, Dictionary<string, MeshInfo> meshes, List<string> missingMeshes,
            LightingInfo lighting)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("{\n");
            sb.Append(" \"format\": \"fbstadium\",\n \"version\": 1,\n");
            sb.Append(" \"level\": ").Append(JsonString(levelName)).Append(",\n");
            sb.Append(" \"stadium\": ").Append(JsonString(stadium)).Append(",\n");
            sb.Append(" \"units\": \"metres\",\n");
            sb.Append(" \"coordinate_system\": \"Frostbite: Y up, row-vector LinearTransform rows right/up/forward/trans; Blender = (x, -z, y)\",\n");
            sb.Append(" \"meshes\": {\n");
            bool first = true;
            foreach (KeyValuePair<string, MeshInfo> kv in meshes)
            {
                MeshInfo mi = kv.Value;
                if (!first) sb.Append(",\n");
                first = false;
                sb.Append("  ").Append(JsonString(kv.Key.ToLower())).Append(": {");
                sb.Append("\"name\": ").Append(JsonString(mi.Name));
                sb.Append(", \"file\": ").Append(mi.File == null ? "null" : JsonString(mi.File));
                sb.Append(", \"missing\": ").Append(mi.File == null ? "true" : "false");
                sb.Append(", \"reason\": ").Append(mi.Reason == null ? "null" : JsonString(mi.Reason));
                if (mi.File != null)
                {
                    sb.Append(", \"lod\": 0, \"mesh_type\": ").Append(JsonString(mi.MeshType ?? ""));
                    sb.Append(", \"bbox\": [").Append(JsonFloats(mi.BoxMin)).Append(", ").Append(JsonFloats(mi.BoxMax)).Append("]");
                    sb.Append(", \"sections\": [").Append(string.Join(", ", mi.Sections)).Append("]");
                }
                sb.Append("}");
            }
            sb.Append("\n },\n \"materials\": {\n");
            first = true;
            foreach (MaterialInfo mi in materials.Values)
            {
                if (!first) sb.Append(",\n");
                first = false;
                sb.Append("  ").Append(JsonString(mi.Key)).Append(": {");
                sb.Append("\"name\": ").Append(JsonString(mi.Name ?? ""));
                sb.Append(", \"mesh\": ").Append(JsonString(mi.Mesh)).Append(", \"index\": ").Append(mi.Index);
                sb.Append(", \"shader\": ").Append(JsonString(mi.Shader));
                sb.Append(", \"presets\": [").Append(string.Join(", ", mi.Presets.Select(JsonString))).Append("]");
                sb.Append(", \"textures\": {").Append(string.Join(", ", mi.Textures.Select(kv => JsonString(kv.Key) + ": " + JsonString(kv.Value)))).Append("}");
                sb.Append(", \"texture_assets\": {").Append(string.Join(", ", mi.TextureAssets.Select(kv => JsonString(kv.Key) + ": " + JsonString(kv.Value)))).Append("}");
                sb.Append(", \"vectors\": {").Append(string.Join(", ", mi.Vectors.Select(kv => JsonString(kv.Key) + ": " + JsonFloats(kv.Value)))).Append("}");
                sb.Append(", \"bools\": {").Append(string.Join(", ", mi.Bools.Select(kv => JsonString(kv.Key) + ": " + (kv.Value ? "true" : "false")))).Append("}");
                sb.Append(", \"from_variation_db\": ").Append(mi.FromVariationDb ? "true" : "false");
                sb.Append("}");
            }
            sb.Append("\n },\n \"texture_failures\": [").Append(string.Join(", ", textureFailures.Select(JsonString))).Append("],\n");
            sb.Append(" \"instances\": [\n");
            for (int i = 0; i < instances.Count; i++)
            {
                Instance inst = instances[i];
                string shortName = inst.Mesh.Substring(inst.Mesh.LastIndexOf('/') + 1);
                if (i > 0) sb.Append(",\n");
                sb.Append("  {\"mesh\": ").Append(JsonString(inst.Mesh));
                sb.Append(", \"name\": ").Append(JsonString(shortName + "." + i.ToString("000")));
                sb.Append(", \"matrix\": [");
                for (int r = 0; r < 4; r++)
                {
                    if (r > 0) sb.Append(", ");
                    sb.Append("[");
                    for (int c = 0; c < 4; c++)
                    {
                        if (c > 0) sb.Append(", ");
                        sb.Append(inst.Matrix[r, c].ToString("R", CultureInfo.InvariantCulture));
                    }
                    sb.Append("]");
                }
                sb.Append("], \"layer\": ").Append(JsonString(inst.Layer ?? ""));
                sb.Append(", \"source\": ").Append(JsonString(inst.Source ?? ""));
                sb.Append(", \"kind\": ").Append(JsonString(inst.Kind));
                if (inst.Lightmap != null)
                {
                    sb.Append(", \"lightmap\": {\"sx\": ").Append(inst.Lightmap[0].ToString("R", CultureInfo.InvariantCulture));
                    sb.Append(", \"sy\": ").Append(inst.Lightmap[1].ToString("R", CultureInfo.InvariantCulture));
                    sb.Append(", \"tx\": ").Append(inst.Lightmap[2].ToString("R", CultureInfo.InvariantCulture));
                    sb.Append(", \"ty\": ").Append(inst.Lightmap[3].ToString("R", CultureInfo.InvariantCulture));
                    sb.Append(", \"key\": \"").Append(inst.LightmapKey.ToString("x8")).Append("\"}");
                }
                sb.Append("}");
            }
            sb.Append("\n ],\n");
            if (lighting == null)
            {
                sb.Append(" \"lighting\": null,\n");
            }
            else
            {
                sb.Append(" \"lighting\": {\"database\": ").Append(JsonString(lighting.Database));
                sb.Append(", \"atlas_width\": ").Append(lighting.AtlasWidth).Append(", \"atlas_height\": ").Append(lighting.AtlasHeight);
                sb.Append(", \"instances_in_database\": ").Append(lighting.Count);
                sb.Append(", \"instances_matched\": ").Append(lighting.Matched);
                sb.Append(", \"instances_unmatched\": ").Append(instances.Count - lighting.Matched);
                sb.Append(", \"flux_lightmap_scale\": ").Append(lighting.FluxScale);
                sb.Append(", \"output_format\": ").Append(JsonString(lighting.OutputFormat));
                sb.Append(", \"textures\": {").Append(string.Join(", ", lighting.Textures.Select(kv => JsonString(kv.Key) + ": " + JsonString(kv.Value)))).Append("}");
                if (lighting.ProbesFile != null)
                {
                    sb.Append(", \"probes\": ").Append(JsonString(lighting.ProbesFile));
                    sb.Append(", \"probe_count\": ").Append(lighting.ProbeCount);
                    sb.Append(", \"probe_sets\": ").Append(lighting.ProbeSets);
                }
                sb.Append(", \"direction_texture\": \"rgb * 2 - 1 = dominant light direction (world space, Y up, towards the light) times its directionality (length <= 1)\"");
                sb.Append(", \"notes\": [").Append(string.Join(", ", lighting.Notes.Select(JsonString))).Append("]},\n");
            }
            sb.Append(" \"lights\": [],\n");
            sb.Append(" \"missing_meshes\": [").Append(string.Join(", ", missingMeshes.Select(JsonString))).Append("],\n");
            sb.Append(" \"missing_assets\": [").Append(string.Join(", ", missingAssets.Distinct().Select(JsonString))).Append("],\n");
            sb.Append(" \"skipped_entity_types\": {").Append(string.Join(", ", skippedTypes.Select(kv => JsonString(kv.Key) + ": " + kv.Value))).Append("},\n");
            sb.Append(" \"notes\": [").Append(string.Join(", ", notes.Select(JsonString))).Append("],\n");
            sb.Append(" \"exporter\": \"Frosty DuplicationPlugin ExportStadiumBlenderMenuExtension\"\n}\n");
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
        }

        private static string JsonObject(params (string key, object value)[] fields)
        {
            StringBuilder sb = new StringBuilder("{");
            for (int i = 0; i < fields.Length; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append(JsonString(fields[i].key)).Append(": ");
                object v = fields[i].value;
                if (v is string s) sb.Append(JsonString(s));
                else if (v is bool bo) sb.Append(bo ? "true" : "false");
                else sb.Append(Convert.ToString(v, CultureInfo.InvariantCulture));
            }
            return sb.Append("}").ToString();
        }

        private static string JsonFloats(float[] v)
        {
            return "[" + string.Join(", ", v.Select(f => f.ToString("R", CultureInfo.InvariantCulture))) + "]";
        }

        private static string JsonString(string s)
        {
            if (s == null) return "null";
            StringBuilder sb = new StringBuilder("\"");
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            return sb.Append("\"").ToString();
        }
    }
}
