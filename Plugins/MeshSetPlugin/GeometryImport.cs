using Frosty.Core;
using FrostySdk;
using FrostySdk.Ebx;
using FrostySdk.IO;
using FrostySdk.Managers;
using MeshSetPlugin.Resources;
using SharpDX;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace MeshSetPlugin
{
    /// <summary>
    /// Geometry of one renderable section handed to <see cref="FBXImporter.ImportGeometry"/>: an indexed
    /// triangle list in Frostbite space (metres, Y up, right handed, counter-clockwise front faces) with the
    /// texture coordinates as the game stores them (v down, like the chunk; no FBX style flip).
    /// </summary>
    public class GeometryImportSection
    {
        /// <summary>Section (material) name; unique within the mesh, not empty.</summary>
        public string Name;
        /// <summary>Composite part of every vertex of this section (0 .. MeshSetFbx.MaxCompositeParts - 1).</summary>
        public int Part;
        /// <summary>x y z per vertex.</summary>
        public float[] Positions;
        /// <summary>x y z per vertex, unit length; computed from the faces when null.</summary>
        public float[] Normals;
        /// <summary>u v per vertex; required.</summary>
        public float[] TexCoord0;
        /// <summary>Optional second UV set (TexCoord1 element), u v per vertex.</summary>
        public float[] TexCoord1;
        /// <summary>Optional lightmap UVs (RadiosityTexCoord element, 0..1), u v per vertex. Zero when null.</summary>
        public float[] LightmapUv;
        /// <summary>Optional Color0, r g b a bytes per vertex (white when null).</summary>
        public byte[] Colors;
        /// <summary>Triangle list, counter-clockwise front faces, section local.</summary>
        public uint[] Indices;
        /// <summary>
        /// Render categories of the section. Only honoured when the mesh has no dedicated depth/shadow sections;
        /// a mesh with those keeps its named sections in Opaque (or Transparent when this has no Opaque flag).
        /// </summary>
        public MeshSubsetCategoryFlags Categories = MeshSubsetCategoryFlags.MeshSubsetCategoryFlags_Opaque
            | MeshSubsetCategoryFlags.MeshSubsetCategoryFlags_ZOnly | MeshSubsetCategoryFlags.MeshSubsetCategoryFlags_Shadow;

        public int VertexCount => Positions == null ? 0 : Positions.Length / 3;
        public int TriangleCount => Indices == null ? 0 : Indices.Length / 3;
    }

    public class GeometryImportOptions
    {
        /// <summary>Set the asset's EnlightenType to Dynamic even if lightmap UVs were given.</summary>
        public bool ForceDynamicLighting = true;
        /// <summary>
        /// Keep a mesh's dedicated unnamed depth/shadow section (it then holds a copy of every vertex). Off: that
        /// section is removed and the named sections are put in the ZOnly and Shadow categories themselves (the
        /// layout of roof_mesh, pitch_mesh ...), which keeps the index buffers 16-bit when no single section has
        /// more than 65535 vertices.
        /// </summary>
        public bool KeepDedicatedDepthSection = true;
        /// <summary>Also set PartComponentCount of the StaticModelGroup members that place the mesh (level data).</summary>
        public bool SyncStaticModelGroups = true;
    }

    public class GeometryImportResult
    {
        public int Lods;
        public int Sections;
        public int Parts;
        public int Vertices;
        public int Triangles;
        public bool LightmapUvsImported;
        public bool EnlightenSetToDynamic;
        public bool LargeIndexBuffer;
        public int StaticModelGroupMembersUpdated;
        public List<Guid> AddedMaterials = new List<Guid>();
        public List<Guid> RemovedMaterials = new List<Guid>();
    }

    /// <summary>
    /// Import of in-memory geometry into a composite (or rigid) mesh, what <see cref="FBXImporter.ImportFBX"/>
    /// does for an FBX file: the named sections of every lod are rebuilt from the given sections, the vertices
    /// written in the mesh's own vertex format, the composite part data, bounds, materials, mesh variation
    /// databases and the blueprint synced the same way. Every lod gets the same geometry.
    /// </summary>
    public partial class FBXImporter
    {
        private sealed class PreparedSection
        {
            public GeometryImportSection Source;
            public Vector3[] Positions;
            public Vector3[] Normals;
            public Vector3[] Tangents;   // FBX convention (dP/du)
            public Vector3[] Binormals;  // FBX convention (dP/dv with v up)
            public Vector2[] TexCoord0;
            public Vector2[] TexCoord1;  // null when absent
            public Vector2[] LightmapUv; // null when absent
        }

        /// <summary>
        /// Replaces the geometry of a composite mesh with <paramref name="sections"/> (every lod gets the same
        /// sections) and syncs materials, variation databases and the blueprint.
        /// </summary>
        /// <param name="forceDynamicLighting">Set the asset's EnlightenType to Dynamic even if lightmap UVs were given.</param>
        public GeometryImportResult ImportGeometry(IList<GeometryImportSection> sections, MeshSet inMeshSet, EbxAsset asset, EbxAssetEntry entry, bool forceDynamicLighting)
        {
            return ImportGeometry(sections, inMeshSet, asset, entry, new GeometryImportOptions { ForceDynamicLighting = forceDynamicLighting });
        }

        private GeometryImportOptions importOptions = new GeometryImportOptions();
        private bool anyLargeIndexBuffer;

        public GeometryImportResult ImportGeometry(IList<GeometryImportSection> sections, MeshSet inMeshSet, EbxAsset asset, EbxAssetEntry entry, GeometryImportOptions options)
        {
            importOptions = options ?? new GeometryImportOptions();
            bool forceDynamicLighting = importOptions.ForceDynamicLighting;
            anyLargeIndexBuffer = false;
            if (sections == null || sections.Count == 0)
                throw new ArgumentException("No sections to import");
            if (inMeshSet.Type != MeshType.MeshType_Composite && inMeshSet.Type != MeshType.MeshType_Rigid)
                throw new FBXImportInvalidMeshTypeException();
            bool isComposite = inMeshSet.Type == MeshType.MeshType_Composite;
            // section indices are bytes in the category lists (0xFF = none) and the unnamed depth/shadow sections follow
            if (sections.Count > 250)
                throw new FBXImportInvalidSectionCountException(0);

            HashSet<string> names = new HashSet<string>();
            int maxPart = -1;
            foreach (GeometryImportSection s in sections)
            {
                if (string.IsNullOrEmpty(s.Name))
                    throw new ArgumentException("A section has no name");
                if (!names.Add(s.Name))
                    throw new ArgumentException("Duplicate section name '" + s.Name + "'");
                if (s.Positions == null || s.Positions.Length < 3 || s.Positions.Length % 3 != 0 || s.Indices == null || s.Indices.Length < 3)
                    throw new ArgumentException("Section '" + s.Name + "' has no geometry");
                int vertexCount = s.Positions.Length / 3;
                if (s.TexCoord0 == null || s.TexCoord0.Length != vertexCount * 2)
                    throw new ArgumentException("Section '" + s.Name + "' has no TexCoord0 (u v per vertex)");
                if (s.Normals != null && s.Normals.Length != vertexCount * 3)
                    throw new ArgumentException("Section '" + s.Name + "': normal count differs from the vertex count");
                if (s.Indices.Length % 3 != 0)
                    throw new ArgumentException("Section '" + s.Name + "': the index count is not a multiple of 3");
                foreach (uint idx in s.Indices)
                {
                    if (idx >= (uint)vertexCount)
                        throw new ArgumentException("Section '" + s.Name + "': index out of range");
                }
                if (s.Part < 0)
                    throw new ArgumentException("Section '" + s.Name + "': negative part index");
                maxPart = Math.Max(maxPart, s.Part);
            }
            if (isComposite && maxPart + 1 > MeshSetFbx.MaxCompositeParts)
                throw new FBXImportTooManyPartsException(maxPart + 1, MeshSetFbx.MaxCompositeParts);

            ulong resRid = ((dynamic)asset.RootObject).MeshSetResource;
            resEntry = App.AssetManager.GetResEntry(resRid);
            settings = null;
            meshSet = inMeshSet;

            try
            {
                meshSet.TangentSpaceCompressionType = (TangentSpaceCompressionType)((dynamic)asset.RootObject).TangentSpaceCompressionType;
            }
            catch (Exception) { /* not every mesh asset type has it */ }

            lightmapUvsImported = false;
            lightmapUvsNeeded = false;
            lightmapUvsMissingSections = 0;
            compositeNoSkinWarned = false;
            meshBoundsValid = false;
            partBoneTransforms.Clear();
            shaderBlockDepots = new List<ShaderBlockDepot>();

            // @hack (as ImportFBX)
            entry.LinkedAssets.Clear();
            resEntry.LinkedAssets.Clear();

            List<Guid> materialsBefore = new List<Guid>();
            try
            {
                foreach (dynamic mat in ((dynamic)asset.RootObject).Materials)
                {
                    dynamic matObj = mat.Internal;
                    if (matObj != null)
                        materialsBefore.Add(((AssetClassGuid)matObj.GetInstanceGuid()).ExportedGuid);
                }
            }
            catch (Exception) { }

            List<PreparedSection> prepared = new List<PreparedSection>();
            foreach (GeometryImportSection s in sections)
                prepared.Add(Prepare(s));

            if (isComposite)
                meshSet.ClearPartData();
            List<BoundingBox> partBbox = new List<BoundingBox>();
            List<LinearTransform> transforms = new List<LinearTransform>();
            for (int i = 0; i < meshSet.Lods.Count; i++)
                ProcessGeometryLod(prepared, i, ref partBbox, ref transforms);

            if (isComposite)
            {
                if (MeshSet.HasNewPartBoneLayout)
                    meshSet.SetParts(ToAxisAlignedBoundingBoxes(partBbox), new List<LinearTransform>());
                else
                    meshSet.SetParts(ToAxisAlignedBoundingBoxes(partBbox), transforms);
                logger.Log("[Geometry Import] {0} parts, {1} sections, {2} lods", meshSet.PartCount, sections.Count, meshSet.Lods.Count);
            }
            else
                logger.Log("[Geometry Import] rigid mesh: {0} sections, {1} lods", sections.Count, meshSet.Lods.Count);

            meshSet.FullName = resEntry.Name;
            if (meshBoundsValid)
                meshSet.SetBoundingBox(ToAxisAlignedBox(meshBounds));

            GeometryImportResult result = new GeometryImportResult
            {
                Lods = meshSet.Lods.Count,
                Sections = sections.Count,
                Parts = isComposite ? meshSet.PartCount : 0,
                LightmapUvsImported = lightmapUvsImported && lightmapUvsMissingSections == 0,
                LargeIndexBuffer = anyLargeIndexBuffer
            };
            foreach (GeometryImportSection s in sections)
            {
                result.Vertices += s.VertexCount;
                result.Triangles += s.TriangleCount;
            }

            // lighting: without lightmap UVs a static mesh would sample one lightmap texel per section
            bool wantDynamic = forceDynamicLighting || (lightmapUvsNeeded && !(lightmapUvsImported && lightmapUvsMissingSections == 0));
            if (wantDynamic)
            {
                try
                {
                    dynamic root = asset.RootObject;
                    int currentType = (int)root.EnlightenType;
                    if (currentType != 0)
                    {
                        root.EnlightenType = 0; // EnlightenType_Dynamic
                        result.EnlightenSetToDynamic = true;
                        logger.Log("[Geometry Import] EnlightenType set to Dynamic (was {0})", currentType);
                    }
                }
                catch (Exception ex)
                {
                    logger.Log("[Geometry Import] WARNING: could not change EnlightenType: {0}", ex.Message);
                }
            }
            else if (lightmapUvsNeeded)
            {
                logger.Log("[Geometry Import] Lightmap UVs written, EnlightenType kept");
            }

            // occlusion: a few meshes (Bernabeu's lower stand) carry a separate occluder mesh resource, a coarse
            // copy of the old shape the renderer culls other objects behind; with new geometry it would hide
            // things behind a stand that is no longer there, so it is switched off
            try
            {
                dynamic root = asset.RootObject;
                bool occluderOn = false;
                try { occluderOn = (bool)root.OccluderMeshEnable; } catch (Exception) { }
                ulong occluderRid = 0;
                try { occluderRid = (ulong)root.OccluderMeshResource; } catch (Exception) { }
                if (occluderOn || occluderRid != 0)
                {
                    root.OccluderMeshEnable = false;
                    root.OccluderMeshResource = new FrostySdk.Ebx.ResourceRef(0);
                    logger.Log("[Geometry Import] occluder mesh switched off (was {0}, res {1:X16}): it described the old geometry", occluderOn, occluderRid);
                }
            }
            catch (Exception ex)
            {
                logger.Log("[Geometry Import] WARNING: could not clear the occluder mesh: {0}", ex.Message);
            }

            try
            {
                SyncMaterials(asset, entry, materialsBefore, result.AddedMaterials, result.RemovedMaterials);
            }
            catch (Exception ex)
            {
                logger.Log("[Geometry Import] WARNING: Material sync failed: {0}", ex.Message);
            }
            App.AssetManager.ModifyEbx(entry.Name, asset);

            App.AssetManager.ModifyRes(resRid, meshSet);
            entry.LinkAsset(resEntry);

            if (result.AddedMaterials.Count > 0 || result.RemovedMaterials.Count > 0)
            {
                try
                {
                    SyncVariationDatabases(asset, entry, result.AddedMaterials, result.RemovedMaterials);
                }
                catch (Exception ex)
                {
                    logger.Log("[Geometry Import] WARNING: Mesh variation database sync failed: {0}", ex.Message);
                }
            }

            if (isComposite && meshSet.Lods.Count > 0)
            {
                try
                {
                    SyncBlueprintPartData(entry, asset, meshSet);
                }
                catch (Exception ex)
                {
                    logger.Log("[Geometry Import] WARNING: Blueprint sync failed: {0}", ex.Message);
                }
                if (importOptions.SyncStaticModelGroups)
                {
                    try
                    {
                        result.StaticModelGroupMembersUpdated = SyncStaticModelGroupMembers(entry, meshSet.PartCount);
                    }
                    catch (Exception ex)
                    {
                        logger.Log("[Geometry Import] WARNING: StaticModelGroup sync failed: {0}", ex.Message);
                    }
                }
            }

            return result;
        }

        // ---- static model groups -------------------------------------------------------------------------

        /// <summary>
        /// A stadium level places most component meshes through a StaticModelGroupEntityData whose
        /// StaticModelGroupMemberData carries the mesh's part count (PartComponentCount). The game walks that many
        /// PartComponentData of the blueprint, so after a part count change every member that places the mesh
        /// has to follow (generic_158: roof_mesh 14 -> reading part 7 of 7 crashed FIFA17.exe at 1463c744f).
        /// Looks at the world data assets in the mesh's bundles; returns the number of members updated.
        /// </summary>
        public int SyncStaticModelGroupMembers(EbxAssetEntry meshEntry, int partCount)
        {
            HashSet<int> meshBundles = new HashSet<int>(meshEntry.EnumerateBundles());
            List<EbxAssetEntry> candidates = new List<EbxAssetEntry>();
            foreach (EbxAssetEntry e in App.AssetManager.EnumerateEbx())
            {
                if (!IsWorldDataType(e.Type))
                    continue;
                bool shared = false;
                foreach (int b in e.EnumerateBundles())
                {
                    if (meshBundles.Contains(b))
                    {
                        shared = true;
                        break;
                    }
                }
                if (shared)
                    candidates.Add(e);
            }
            if (candidates.Count == 0)
            {
                // a mesh in no bundle (added asset): the world data of the mesh's stadium folder
                string folder = meshEntry.Name.Contains("/") ? meshEntry.Name.Substring(0, meshEntry.Name.LastIndexOf('/') + 1) : "";
                foreach (EbxAssetEntry e in App.AssetManager.EnumerateEbx())
                {
                    if (IsWorldDataType(e.Type) && e.Name.StartsWith(folder, StringComparison.OrdinalIgnoreCase))
                        candidates.Add(e);
                }
            }

            int updated = 0;
            foreach (EbxAssetEntry worldEntry in candidates)
            {
                EbxAsset worldAsset;
                try { worldAsset = App.AssetManager.GetEbx(worldEntry); }
                catch (Exception) { continue; }
                if (worldAsset == null)
                    continue;
                bool changed = false;
                foreach (object obj in worldAsset.Objects.ToList())
                {
                    if (obj == null || obj.GetType().Name != "StaticModelGroupEntityData")
                        continue;
                    dynamic group = obj;
                    dynamic members;
                    try { members = group.MemberDatas; }
                    catch (Exception) { continue; }
                    for (int i = 0; i < members.Count; i++)
                    {
                        dynamic member = members[i];
                        PointerRef meshRef;
                        try { meshRef = member.MeshAsset; }
                        catch (Exception) { continue; }
                        if (meshRef.Type != PointerRefType.External || meshRef.External.FileGuid != meshEntry.Guid)
                            continue;
                        uint current = 0;
                        try { current = (uint)member.PartComponentCount; } catch (Exception) { }
                        if (current == (uint)partCount)
                            continue;
                        member.PartComponentCount = (uint)partCount;
                        members[i] = member; // a struct instance is written back
                        changed = true;
                        updated++;
                        logger.Log("[Composite Import] StaticModelGroup member of '{0}' in '{1}': PartComponentCount {2} -> {3}", meshEntry.Filename, worldEntry.Name, current, partCount);
                    }
                }
                if (changed)
                    App.AssetManager.ModifyEbx(worldEntry.Name, worldAsset);
            }
            if (updated == 0)
                logger.Log("[Composite Import] No StaticModelGroup member with a different PartComponentCount places '{0}' ({1} world data asset(s) checked)", meshEntry.Filename, candidates.Count);
            return updated;
        }

        private static bool IsWorldDataType(string type)
        {
            switch (type)
            {
                case "SubWorldData":
                case "DetachedSubWorldData":
                case "WorldPartData":
                case "LevelData":
                case "LayerData":
                case "PrefabBlueprint":
                case "SpatialPrefabBlueprint":
                case "ObjectBlueprint":
                    return true;
                default:
                    return false;
            }
        }

        // ---- lods ----------------------------------------------------------------------------------------

        private void ProcessGeometryLod(List<PreparedSection> inputs, int lodIndex, ref List<BoundingBox> partBbox, ref List<LinearTransform> transforms)
        {
            MeshSetLod meshLod = meshSet.Lods[lodIndex];

            List<MeshSetSection> meshSections = new List<MeshSetSection>();
            List<MeshSetSection> depthSections = new List<MeshSetSection>();
            List<MeshSetSection> shadowSections = new List<MeshSetSection>();
            foreach (MeshSetSection meshSection in meshLod.Sections)
            {
                if (meshSection.Name != "")
                    meshSections.Add(meshSection);
                else
                {
                    bool isDepth = meshLod.IsSectionInCategory(meshSection, MeshSubsetCategory.MeshSubsetCategory_ZOnly);
                    bool isShadow = meshLod.IsSectionInCategory(meshSection, MeshSubsetCategory.MeshSubsetCategory_Shadow);
                    if (isDepth)
                        depthSections.Add(meshSection);
                    if (isShadow)
                        shadowSections.Add(meshSection);
                    if (!isDepth && !isShadow)
                        shadowSections.Add(meshSection);
                }
            }

            // the template for new sections: this lod's last named section, else lod 0's first named one
            MeshSetSection template = meshSections.Count > 0 ? meshSections[meshSections.Count - 1] : null;
            if (template == null)
            {
                foreach (MeshSetLod lod in meshSet.Lods)
                {
                    template = lod.Sections.FirstOrDefault(s => s.Name != "");
                    if (template != null)
                        break;
                }
            }
            if (template == null)
                throw new FBXImportInvalidSectionCountException(lodIndex);

            // rebuild the named sections: one per input, in order (section i uses material i)
            int renameCount = Math.Min(inputs.Count, meshSections.Count);
            for (int i = 0; i < renameCount; i++)
                meshSections[i].SetMaterialName(inputs[i].Source.Name, i);
            for (int i = meshSections.Count; i < inputs.Count; i++)
                meshSections.Add(MeshSetSection.CreateFromTemplate(template, inputs[i].Source.Name, i, i));
            while (meshSections.Count > inputs.Count)
                meshSections.RemoveAt(meshSections.Count - 1);

            bool hasDedicatedDepthShadow = depthSections.Count > 0 || shadowSections.Count > 0;
            if (hasDedicatedDepthShadow && !importOptions.KeepDedicatedDepthSection)
            {
                // the named sections take over the depth and shadow passes (roof_mesh style layout)
                depthSections.Clear();
                shadowSections.Clear();
                hasDedicatedDepthShadow = false;
                if (lodIndex == 0)
                    logger.Log("[Geometry Import] dedicated depth/shadow section dropped; the named sections render the depth and shadow passes");
            }

            meshLod.Sections.Clear();
            meshLod.Sections.AddRange(meshSections);
            if (hasDedicatedDepthShadow)
            {
                foreach (MeshSetSection ds in depthSections)
                {
                    if (!meshLod.Sections.Contains(ds))
                        meshLod.Sections.Add(ds);
                }
                foreach (MeshSetSection ss in shadowSections)
                {
                    if (!meshLod.Sections.Contains(ss))
                        meshLod.Sections.Add(ss);
                }
            }
            meshLod.SectionCount = meshLod.Sections.Count;

            // categories
            for (int cat = 0; cat < meshLod.MaxCategories; cat++)
            {
                if (cat < meshLod.CategorySubsetIndices.Count)
                    meshLod.CategorySubsetIndices[cat].Clear();
            }
            for (int i = 0; i < meshSections.Count; i++)
            {
                MeshSubsetCategoryFlags flags = inputs[i].Source.Categories;
                bool transparent = (flags & MeshSubsetCategoryFlags.MeshSubsetCategoryFlags_Opaque) == 0
                    && (flags & (MeshSubsetCategoryFlags.MeshSubsetCategoryFlags_Transparent | MeshSubsetCategoryFlags.MeshSubsetCategoryFlags_TransparentDecal)) != 0;
                if (hasDedicatedDepthShadow)
                {
                    meshLod.SetSectionCategory(meshSections[i], transparent ? MeshSubsetCategory.MeshSubsetCategory_Transparent : MeshSubsetCategory.MeshSubsetCategory_Opaque);
                }
                else
                {
                    if ((flags & MeshSubsetCategoryFlags.MeshSubsetCategoryFlags_Opaque) != 0)
                        meshLod.SetSectionCategory(meshSections[i], MeshSubsetCategory.MeshSubsetCategory_Opaque);
                    if ((flags & MeshSubsetCategoryFlags.MeshSubsetCategoryFlags_Transparent) != 0)
                        meshLod.SetSectionCategory(meshSections[i], MeshSubsetCategory.MeshSubsetCategory_Transparent);
                    if ((flags & MeshSubsetCategoryFlags.MeshSubsetCategoryFlags_TransparentDecal) != 0)
                        meshLod.SetSectionCategory(meshSections[i], MeshSubsetCategory.MeshSubsetCategory_TransparentDecal);
                    if ((flags & MeshSubsetCategoryFlags.MeshSubsetCategoryFlags_ZOnly) != 0)
                        meshLod.SetSectionCategory(meshSections[i], MeshSubsetCategory.MeshSubsetCategory_ZOnly);
                    if ((flags & MeshSubsetCategoryFlags.MeshSubsetCategoryFlags_Shadow) != 0)
                        meshLod.SetSectionCategory(meshSections[i], MeshSubsetCategory.MeshSubsetCategory_Shadow);
                }
            }
            if (hasDedicatedDepthShadow)
            {
                foreach (MeshSetSection ds in depthSections)
                    meshLod.SetSectionCategory(ds, MeshSubsetCategory.MeshSubsetCategory_ZOnly);
                foreach (MeshSetSection ss in shadowSections)
                    meshLod.SetSectionCategory(ss, MeshSubsetCategory.MeshSubsetCategory_Shadow);
            }

            List<byte[]> sectionsVertices = new List<byte[]>();
            List<List<uint>> sectionsIndices = new List<List<uint>>();
            uint vertexBufferSize = 0;
            uint totalIndices = 0;

            meshLod.ClearBones();
            lodSectionParts.Clear();
            lodPartPoints.Clear();

            // named sections
            for (int i = 0; i < meshSections.Count; i++)
            {
                int sectionIndex = meshLod.Sections.IndexOf(meshSections[i]);
                MemoryStream vertices = new MemoryStream();
                List<uint> indices = new List<uint>();
                WriteGeometrySections(new List<PreparedSection> { inputs[i] }, meshLod, sectionIndex, vertices, indices, vertexBufferSize, ref totalIndices, true, lodIndex == 0);
                sectionsVertices.Add(vertices.ToArray());
                sectionsIndices.Add(indices);
                vertexBufferSize += (uint)vertices.Length;
                vertices.Dispose();
            }

            // dedicated depth / shadow sections: the first one of each kind holds everything, the others are
            // emptied; a section that is both depth and shadow is written once
            MeshSetSection writtenDepth = null;
            for (int i = 0; i < depthSections.Count; i++)
            {
                if (writtenDepth == null)
                {
                    int sectionIndex = meshLod.Sections.IndexOf(depthSections[i]);
                    MemoryStream vertices = new MemoryStream();
                    List<uint> indices = new List<uint>();
                    WriteGeometrySections(inputs, meshLod, sectionIndex, vertices, indices, vertexBufferSize, ref totalIndices, false, false);
                    sectionsVertices.Add(vertices.ToArray());
                    sectionsIndices.Add(indices);
                    vertexBufferSize += (uint)vertices.Length;
                    vertices.Dispose();
                    writtenDepth = depthSections[i];
                }
                else
                    KillSection(depthSections[i]);
            }
            MeshSetSection writtenShadow = null;
            for (int i = 0; i < shadowSections.Count; i++)
            {
                if (shadowSections[i] == writtenDepth)
                {
                    writtenShadow = shadowSections[i]; // already holds everything
                    continue;
                }
                if (writtenShadow == null)
                {
                    int sectionIndex = meshLod.Sections.IndexOf(shadowSections[i]);
                    MemoryStream vertices = new MemoryStream();
                    List<uint> indices = new List<uint>();
                    WriteGeometrySections(inputs, meshLod, sectionIndex, vertices, indices, vertexBufferSize, ref totalIndices, false, false);
                    sectionsVertices.Add(vertices.ToArray());
                    sectionsIndices.Add(indices);
                    vertexBufferSize += (uint)vertices.Length;
                    vertices.Dispose();
                    writtenShadow = shadowSections[i];
                }
                else
                    KillSection(shadowSections[i]);
            }

            if (meshSet.Type == MeshType.MeshType_Composite)
                BuildCompositePartData(meshLod, ref partBbox, ref transforms);

            bool largeIndexBuffer = false;
            foreach (MeshSetSection section in meshLod.Sections)
            {
                if (section.VertexCount > 65535)
                {
                    largeIndexBuffer = true;
                    break;
                }
            }
            if (largeIndexBuffer)
            {
                anyLargeIndexBuffer = true;
                logger.Log("[Geometry Import] lod {0}: a section has more than 65535 vertices, 32-bit index buffer", lodIndex);
            }

            using (NativeWriter writer = new NativeWriter(new MemoryStream()))
            {
                foreach (byte[] buffer in sectionsVertices)
                    writer.Write(buffer);
                writer.WritePadding(0x10);
                meshLod.VertexBufferSize = (uint)writer.Position;
                foreach (List<uint> buffer in sectionsIndices)
                {
                    foreach (uint index in buffer)
                    {
                        if (largeIndexBuffer)
                            writer.Write(index);
                        else
                            writer.Write((ushort)index);
                    }
                }
                writer.WritePadding(0x10);
                meshLod.IndexBufferSize = (uint)(writer.Position - meshLod.VertexBufferSize);
                meshLod.SetIndexBufferFormatSize(largeIndexBuffer ? 4 : 2);

                if (meshLod.ChunkId != Guid.Empty)
                {
                    App.AssetManager.ModifyChunk(meshLod.ChunkId, writer.ToByteArray());
                    ChunkAssetEntry chunkEntry = App.AssetManager.GetChunkEntry(meshLod.ChunkId);
                    resEntry.LinkAsset(chunkEntry);
                }
                else
                {
                    meshLod.SetInlineData(writer.ToByteArray());
                }
            }
        }

        private static void KillSection(MeshSetSection section)
        {
            section.VertexCount = 0;
            section.PrimitiveCount = 0;
            section.VertexOffset = 0;
            section.StartIndex = 0;
        }

        // ---- vertices ------------------------------------------------------------------------------------

        /// <summary>Normals (when missing) and the tangent frame of a section, in the FBX importer's convention.</summary>
        private static PreparedSection Prepare(GeometryImportSection s)
        {
            int n = s.Positions.Length / 3;
            Vector3[] positions = new Vector3[n];
            for (int i = 0; i < n; i++)
                positions[i] = new Vector3(s.Positions[i * 3], s.Positions[i * 3 + 1], s.Positions[i * 3 + 2]);
            Vector2[] uv0 = new Vector2[n];
            for (int i = 0; i < n; i++)
                uv0[i] = new Vector2(s.TexCoord0[i * 2], s.TexCoord0[i * 2 + 1]);
            Vector2[] uv1 = null;
            if (s.TexCoord1 != null && s.TexCoord1.Length == n * 2)
            {
                uv1 = new Vector2[n];
                for (int i = 0; i < n; i++)
                    uv1[i] = new Vector2(s.TexCoord1[i * 2], s.TexCoord1[i * 2 + 1]);
            }
            Vector2[] lightmap = null;
            if (s.LightmapUv != null && s.LightmapUv.Length == n * 2)
            {
                lightmap = new Vector2[n];
                for (int i = 0; i < n; i++)
                    lightmap[i] = new Vector2(s.LightmapUv[i * 2], s.LightmapUv[i * 2 + 1]);
            }

            Vector3[] normals = new Vector3[n];
            if (s.Normals != null && s.Normals.Length == n * 3)
            {
                for (int i = 0; i < n; i++)
                {
                    Vector3 v = new Vector3(s.Normals[i * 3], s.Normals[i * 3 + 1], s.Normals[i * 3 + 2]);
                    if (v.LengthSquared() > 1e-20f)
                        v.Normalize();
                    else
                        v = Vector3.UnitY;
                    normals[i] = v;
                }
            }
            else
            {
                for (int t = 0; t + 2 < s.Indices.Length; t += 3)
                {
                    uint a = s.Indices[t], b = s.Indices[t + 1], c = s.Indices[t + 2];
                    Vector3 fn = Vector3.Cross(positions[b] - positions[a], positions[c] - positions[a]); // area weighted
                    normals[a] += fn; normals[b] += fn; normals[c] += fn;
                }
                for (int i = 0; i < n; i++)
                {
                    if (normals[i].LengthSquared() > 1e-20f)
                        normals[i].Normalize();
                    else
                        normals[i] = Vector3.UnitY;
                }
            }

            // per-vertex dP/du and dP/dv (stored uv, v down) accumulated over the triangles
            Vector3[] dpdu = new Vector3[n];
            Vector3[] dpdv = new Vector3[n];
            for (int t = 0; t + 2 < s.Indices.Length; t += 3)
            {
                uint a = s.Indices[t], b = s.Indices[t + 1], c = s.Indices[t + 2];
                Vector3 e1 = positions[b] - positions[a];
                Vector3 e2 = positions[c] - positions[a];
                float du1 = uv0[b].X - uv0[a].X, dv1 = uv0[b].Y - uv0[a].Y;
                float du2 = uv0[c].X - uv0[a].X, dv2 = uv0[c].Y - uv0[a].Y;
                float det = du1 * dv2 - du2 * dv1;
                if (Math.Abs(det) < 1e-12f)
                    continue;
                float r = 1.0f / det;
                Vector3 tu = (e1 * dv2 - e2 * dv1) * r;
                Vector3 tv = (e2 * du1 - e1 * du2) * r;
                dpdu[a] += tu; dpdu[b] += tu; dpdu[c] += tu;
                dpdv[a] += tv; dpdv[b] += tv; dpdv[c] += tv;
            }

            Vector3[] tangents = new Vector3[n];
            Vector3[] binormals = new Vector3[n];
            for (int i = 0; i < n; i++)
            {
                Vector3 nrm = normals[i];
                Vector3 t = dpdu[i] - nrm * Vector3.Dot(nrm, dpdu[i]);
                if (t.LengthSquared() < 1e-20f)
                {
                    // no usable uv gradient: any direction perpendicular to the normal
                    Vector3 axis = Math.Abs(nrm.Y) < 0.9f ? Vector3.UnitY : Vector3.UnitX;
                    t = Vector3.Cross(axis, nrm);
                }
                t.Normalize();
                Vector3 nt = Vector3.Cross(nrm, t);
                // the FBX convention has v up: dP/dv_fbx = -dP/dv_stored
                float handed = Vector3.Dot(nt, -dpdv[i]) < 0.0f ? -1.0f : 1.0f;
                tangents[i] = t;
                binormals[i] = nt * handed;
            }

            return new PreparedSection { Source = s, Positions = positions, Normals = normals, Tangents = tangents, Binormals = binormals, TexCoord0 = uv0, TexCoord1 = uv1, LightmapUv = lightmap };
        }

        /// <summary>
        /// Writes the vertices and indices of <paramref name="inputs"/> into the section at
        /// <paramref name="sectionIndex"/> in that section's vertex format (the FBX importer's layout rules).
        /// </summary>
        private void WriteGeometrySections(List<PreparedSection> inputs, MeshSetLod meshLod, int sectionIndex, MemoryStream verticesBuffer, List<uint> indicesBuffer, uint vertexOffset, ref uint startIndex, bool recordParts, bool accumulateBounds)
        {
            MeshSetSection meshSection = meshLod.Sections[sectionIndex];
            uint indexOffset = 0;

            meshSection.VertexOffset = vertexOffset;
            meshSection.StartIndex = startIndex;
            meshSection.VertexCount = 0;
            meshSection.PrimitiveCount = 0;
            meshSection.ClearSmoothingData();

            bool sectionHasLightmap = false;
            foreach (GeometryDeclarationDesc.Element elem in meshSection.GeometryDeclDesc[0].Elements)
            {
                if (elem.Usage == VertexElementUsage.RadiosityTexCoord)
                    sectionHasLightmap = true;
            }
            if (sectionHasLightmap)
                lightmapUvsNeeded = true;

            HashSet<int> usedParts = new HashSet<int>();

            using (NativeWriter chunkWriter = new NativeWriter(verticesBuffer, true))
            {
                chunkWriter.Position = chunkWriter.Length;

                foreach (PreparedSection ps in inputs)
                {
                    GeometryImportSection src = ps.Source;
                    int n = ps.Positions.Length;
                    int part = src.Part;
                    usedParts.Add(part);

                    if (sectionHasLightmap)
                    {
                        if (ps.LightmapUv != null)
                            lightmapUvsImported = true;
                        else if (recordParts)
                            lightmapUvsMissingSections++;
                    }

                    if (recordParts)
                    {
                        List<int> sectionParts;
                        if (!lodSectionParts.TryGetValue(sectionIndex, out sectionParts))
                            lodSectionParts[sectionIndex] = sectionParts = new List<int>();
                        if (!sectionParts.Contains(part))
                            sectionParts.Add(part);

                        List<Vector3> points;
                        if (!lodPartPoints.TryGetValue(part, out points))
                            lodPartPoints[part] = points = new List<Vector3>();
                        points.AddRange(ps.Positions);

                        if (accumulateBounds)
                        {
                            foreach (Vector3 p in ps.Positions)
                                AccumulateBounds(p);
                        }
                    }

                    // composite: the part index in every lane; rigid: zeros (as the FBX importer writes for a skinless mesh)
                    ushort[] boneIndices = new ushort[8];
                    byte[] boneWeights = new byte[8];
                    if (meshSet.Type == MeshType.MeshType_Composite)
                    {
                        for (int k = 0; k < 8; k++)
                            boneIndices[k] = (ushort)part;
                    }
                    boneWeights[7] = 255;

                    for (int i = 0; i < meshSection.GeometryDeclDesc[0].Streams.Length; i++)
                    {
                        GeometryDeclarationDesc.Stream stream = meshSection.GeometryDeclDesc[0].Streams[i];
                        if (stream.VertexStride == 0)
                            continue;

                        for (int v = 0; v < n; v++)
                        {
                            Vector3 position = ps.Positions[v];
                            Vector3 normal = ps.Normals[v];
                            Vector3 tangent = ps.Tangents[v];
                            Vector3 binormal = ps.Binormals[v];

                            // the tangent is stored inverted (see ImportFBX)
                            tangent *= -1.0f;

                            int currentStride = 0;
                            foreach (GeometryDeclarationDesc.Element elem in meshSection.GeometryDeclDesc[0].Elements)
                            {
                                if (elem.Usage == VertexElementUsage.Unknown)
                                    continue;
                                if (elem.StreamIndex != i || currentStride >= stream.VertexStride)
                                    continue;

                                switch (elem.Usage)
                                {
                                    case VertexElementUsage.Pos:
                                        if (elem.Format == VertexElementFormat.Float3 || elem.Format == VertexElementFormat.Float4)
                                        {
                                            chunkWriter.Write(position.X);
                                            chunkWriter.Write(position.Y);
                                            chunkWriter.Write(position.Z);
                                            if (elem.Format == VertexElementFormat.Float4)
                                                chunkWriter.Write(1.0f);
                                        }
                                        else if (elem.Format == VertexElementFormat.Half3 || elem.Format == VertexElementFormat.Half4)
                                        {
                                            chunkWriter.Write(HalfUtils.Pack(position.X));
                                            chunkWriter.Write(HalfUtils.Pack(position.Y));
                                            chunkWriter.Write(HalfUtils.Pack(position.Z));
                                            if (elem.Format == VertexElementFormat.Half4)
                                                chunkWriter.Write(HalfUtils.Pack(1.0f));
                                        }
                                        else
                                            WriteZeros(chunkWriter, elem.Size);
                                        break;

                                    case VertexElementUsage.BinormalSign:
                                        {
                                            float sign = Vector3.Dot(binormal, Vector3.Cross(normal, tangent)) < 0.0f ? 1.0f : -1.0f;
                                            if (elem.Format == VertexElementFormat.Half)
                                                chunkWriter.Write(HalfUtils.Pack(sign));
                                            else if (elem.Format == VertexElementFormat.Half4)
                                            {
                                                chunkWriter.Write(HalfUtils.Pack(tangent.X));
                                                chunkWriter.Write(HalfUtils.Pack(tangent.Y));
                                                chunkWriter.Write(HalfUtils.Pack(tangent.Z));
                                                chunkWriter.Write(HalfUtils.Pack(sign));
                                            }
                                            else if (elem.Format == VertexElementFormat.Float4)
                                            {
                                                chunkWriter.Write(tangent.X);
                                                chunkWriter.Write(tangent.Y);
                                                chunkWriter.Write(tangent.Z);
                                                chunkWriter.Write(sign);
                                            }
                                            else
                                                chunkWriter.Write(sign);
                                        }
                                        break;

                                    case VertexElementUsage.BoneIndices:
                                        if (elem.Format == VertexElementFormat.UShort4)
                                        {
                                            chunkWriter.Write(boneIndices[4]); chunkWriter.Write(boneIndices[5]);
                                            chunkWriter.Write(boneIndices[6]); chunkWriter.Write(boneIndices[7]);
                                        }
                                        else
                                        {
                                            chunkWriter.Write((byte)boneIndices[4]); chunkWriter.Write((byte)boneIndices[5]);
                                            chunkWriter.Write((byte)boneIndices[6]); chunkWriter.Write((byte)boneIndices[7]);
                                        }
                                        break;

                                    case VertexElementUsage.BoneWeights:
                                        chunkWriter.Write(boneWeights[4]); chunkWriter.Write(boneWeights[5]);
                                        chunkWriter.Write(boneWeights[6]); chunkWriter.Write(boneWeights[7]);
                                        break;

                                    case VertexElementUsage.BoneIndices2:
                                        if (elem.Format == VertexElementFormat.UShort4)
                                        {
                                            chunkWriter.Write(boneIndices[0]); chunkWriter.Write(boneIndices[1]);
                                            chunkWriter.Write(boneIndices[2]); chunkWriter.Write(boneIndices[3]);
                                        }
                                        else
                                        {
                                            chunkWriter.Write((byte)boneIndices[0]); chunkWriter.Write((byte)boneIndices[1]);
                                            chunkWriter.Write((byte)boneIndices[2]); chunkWriter.Write((byte)boneIndices[3]);
                                        }
                                        break;

                                    case VertexElementUsage.BoneWeights2:
                                        chunkWriter.Write(boneWeights[0]); chunkWriter.Write(boneWeights[1]);
                                        chunkWriter.Write(boneWeights[2]); chunkWriter.Write(boneWeights[3]);
                                        break;

                                    case VertexElementUsage.Binormal:
                                        chunkWriter.Write(HalfUtils.Pack(binormal.X));
                                        chunkWriter.Write(HalfUtils.Pack(binormal.Y));
                                        chunkWriter.Write(HalfUtils.Pack(binormal.Z));
                                        chunkWriter.Write(HalfUtils.Pack(1.0f));
                                        break;

                                    case VertexElementUsage.Normal:
                                        chunkWriter.Write(HalfUtils.Pack(normal.X));
                                        chunkWriter.Write(HalfUtils.Pack(normal.Y));
                                        chunkWriter.Write(HalfUtils.Pack(normal.Z));
                                        chunkWriter.Write(HalfUtils.Pack(1.0f));
                                        break;

                                    case VertexElementUsage.Tangent:
                                        chunkWriter.Write(HalfUtils.Pack(tangent.X));
                                        chunkWriter.Write(HalfUtils.Pack(tangent.Y));
                                        chunkWriter.Write(HalfUtils.Pack(tangent.Z));
                                        chunkWriter.Write(HalfUtils.Pack(1.0f));
                                        break;

                                    case VertexElementUsage.TangentSpace:
                                        if (elem.Format == VertexElementFormat.UInt)
                                        {
                                            Quaternion quat = new Quaternion
                                            {
                                                X = normal.Y - binormal.Z,
                                                Y = tangent.Z - normal.X,
                                                Z = binormal.X - tangent.Y,
                                                W = 1.0f + tangent.X + binormal.Y + normal.Z
                                            };
                                            quat.Normalize();
                                            int idx = FindGreatestComponent(quat);
                                            if (idx == 0) quat = new Quaternion(quat.W, quat.X, quat.Y, quat.Z);
                                            if (idx == 1) quat = new Quaternion(quat.X, quat.W, quat.Y, quat.Z);
                                            if (idx == 2) quat = new Quaternion(quat.X, quat.Y, quat.W, quat.Z);
                                            Vector3 tmpQuat = new Vector3(quat.X, quat.Y, quat.Z) * Math.Sign(quat.W) * (float)Math.Sqrt(0.5f) + 0.5f;
                                            Vector4 packedQuat = new Vector4(tmpQuat.X, tmpQuat.Y, tmpQuat.Z, idx / 3.0f);
                                            uint ts = 0;
                                            ts |= ((uint)(packedQuat.X * 1023.0f) << 22);
                                            ts |= ((uint)(packedQuat.Y * 511.0f) << 13);
                                            ts |= ((uint)(packedQuat.Z * 1023.0f) << 3);
                                            ts |= ((uint)(packedQuat.W * 3.0f) << 1);
                                            ts |= (uint)((Vector3.Dot(Vector3.Cross(normal, tangent), binormal)) < 0.0f ? 1 : 0);
                                            chunkWriter.Write(ts);
                                        }
                                        else
                                            WriteZeros(chunkWriter, elem.Size);
                                        break;

                                    case VertexElementUsage.RadiosityTexCoord:
                                        {
                                            Vector2 uv = ps.LightmapUv != null ? ps.LightmapUv[v] : Vector2.Zero;
                                            if (elem.Format == VertexElementFormat.Float2)
                                            {
                                                chunkWriter.Write(uv.X);
                                                chunkWriter.Write(uv.Y);
                                            }
                                            else if (elem.Format == VertexElementFormat.Half2)
                                            {
                                                chunkWriter.Write(HalfUtils.Pack(uv.X));
                                                chunkWriter.Write(HalfUtils.Pack(uv.Y));
                                            }
                                            else
                                                WriteZeros(chunkWriter, elem.Size);
                                        }
                                        break;

                                    case VertexElementUsage.Color0:
                                    case VertexElementUsage.Color1:
                                        if (src.Colors != null && src.Colors.Length == n * 4)
                                        {
                                            chunkWriter.Write(src.Colors[v * 4]);
                                            chunkWriter.Write(src.Colors[v * 4 + 1]);
                                            chunkWriter.Write(src.Colors[v * 4 + 2]);
                                            chunkWriter.Write(src.Colors[v * 4 + 3]);
                                        }
                                        else
                                        {
                                            // the original stadium meshes carry white here
                                            chunkWriter.Write((byte)255); chunkWriter.Write((byte)255);
                                            chunkWriter.Write((byte)255); chunkWriter.Write((byte)255);
                                        }
                                        break;

                                    case VertexElementUsage.MaskUv:
                                        chunkWriter.Write((ushort)0);
                                        chunkWriter.Write((ushort)0);
                                        break;

                                    case VertexElementUsage.Delta:
                                        chunkWriter.Write(HalfUtils.Pack(0));
                                        chunkWriter.Write(HalfUtils.Pack(0));
                                        chunkWriter.Write(HalfUtils.Pack(0));
                                        chunkWriter.Write(HalfUtils.Pack(0));
                                        break;

                                    case VertexElementUsage.BlendWeights:
                                        chunkWriter.Write(0.0f);
                                        break;

                                    case VertexElementUsage.RegionIds:
                                        chunkWriter.Write(0);
                                        break;

                                    case VertexElementUsage.SubMaterialIndex:
                                    case VertexElementUsage.DisplacementMapTexCoord:
                                        WriteZeros(chunkWriter, elem.Size);
                                        break;

                                    default:
                                        if (elem.Usage >= VertexElementUsage.TexCoord0 && elem.Usage <= VertexElementUsage.TexCoord7)
                                        {
                                            int set = (int)(elem.Usage - VertexElementUsage.TexCoord0);
                                            Vector2 uv;
                                            if (set == 1 && ps.TexCoord1 != null)
                                                uv = ps.TexCoord1[v];
                                            else
                                                uv = ps.TexCoord0[v];
                                            if (elem.Format == VertexElementFormat.Float2)
                                            {
                                                chunkWriter.Write(uv.X);
                                                chunkWriter.Write(uv.Y);
                                            }
                                            else if (elem.Format == VertexElementFormat.Half2)
                                            {
                                                chunkWriter.Write(HalfUtils.Pack(uv.X));
                                                chunkWriter.Write(HalfUtils.Pack(uv.Y));
                                            }
                                            else
                                                WriteZeros(chunkWriter, elem.Size);
                                        }
                                        else
                                            throw new FBXImportUnimplementDataTypeException(elem.Usage);
                                        break;
                                }

                                currentStride += elem.Size;
                            }

                            if (currentStride != stream.VertexStride)
                                chunkWriter.Position += stream.VertexStride - currentStride;
                        }
                    }

                    meshSection.VertexCount += (uint)n;

                    int numIndices = src.Indices.Length - (src.Indices.Length % 3);
                    for (int i = 0; i < numIndices; i++)
                        indicesBuffer.Add(indexOffset + src.Indices[i]);
                    meshSection.PrimitiveCount += (uint)(numIndices / 3);
                    startIndex += (uint)numIndices;
                    indexOffset += (uint)n;
                }
            }

            if (meshSet.Type == MeshType.MeshType_Composite)
            {
                List<ushort> parts = usedParts.Select(p => (ushort)p).ToList();
                if (parts.Count == 0)
                    parts.Add(0);
                parts.Sort();
                meshSection.SetBones(parts);
                meshLod.AddBones(parts, parts.Select(p => MeshSetFbx.PartBoneName(p)));
            }
            else
                meshSection.SetBones(new List<ushort>());
        }

        private static void WriteZeros(NativeWriter writer, int count)
        {
            for (int b = 0; b < count; b++)
                writer.Write((byte)0);
        }
    }
}
