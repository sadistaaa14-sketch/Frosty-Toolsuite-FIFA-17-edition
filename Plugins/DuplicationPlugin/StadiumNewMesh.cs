using Frosty.Core;
using Frosty.Core.Viewport;
using FrostySdk;
using FrostySdk.Attributes;
using FrostySdk.Ebx;
using FrostySdk.IO;
using FrostySdk.Managers;
using MeshSetPlugin.Resources;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace DuplicationPlugin
{
    /// <summary>
    /// New component meshes for package slots the base stadium has no mesh for (the PES weather and time-of-day
    /// layers: rain, snow, night). A new mesh is a copy of one of the stadium's component meshes (mesh asset, mesh
    /// set, chunks) with its own ObjectBlueprint, placed by a new member of the StaticModelGroup of the sublevel
    /// that shows it (rain, snow) or of the main level, listed in that sublevel's mesh variation database and put
    /// into the sublevel's bundles. The member's MemberType is the blueprint's StaticModelEntityData (with a guid of
    /// its own), not the ObjectBlueprint. The package geometry is then imported into it like into any other slot.
    /// </summary>
    public partial class StadiumImporter
    {
        private const string NewTargetPrefix = "(new) ";
        /// <summary>New mesh -> the level asset whose group places it (its member's part count follows the imported geometry).</summary>
        private readonly Dictionary<Guid, EbxAssetEntry> newMeshLevels = new Dictionary<Guid, EbxAssetEntry>();

        /// <summary>
        /// The bundles a new mesh of the sublevel belongs to: the sublevel asset's bundles plus the stadium bundles of the
        /// meshes its StaticModelGroups already place (the sublevel's own bundle holds its meshes).
        /// </summary>
        private List<int> SublevelBundles(EbxAssetEntry levelEntry)
        {
            List<int> bundles = levelEntry.EnumerateBundles().ToList();
            string folderMark = "/stadiums/" + settings.StadiumFolder.ToLower() + "/";
            string folderEnd = "/stadiums/" + settings.StadiumFolder.ToLower();
            try
            {
                EbxAsset levelAsset = App.AssetManager.GetEbx(levelEntry);
                foreach (object obj in levelAsset.Objects)
                {
                    if (obj == null || obj.GetType().Name != "StaticModelGroupEntityData")
                        continue;
                    foreach (dynamic m in ((dynamic)obj).MemberDatas)
                    {
                        PointerRef meshRef = m.MeshAsset;
                        EbxAssetEntry e = meshRef.Type == PointerRefType.External ? App.AssetManager.GetEbxEntry(meshRef.External.FileGuid) : null;
                        if (e == null)
                            continue;
                        foreach (int b in e.EnumerateBundles())
                        {
                            if (bundles.Contains(b))
                                continue;
                            BundleEntry be = App.AssetManager.GetBundleEntry(b);
                            string bn = be != null ? be.Name.Replace('\\', '/').ToLower() : "";
                            if (bn.Contains(folderMark) || bn.EndsWith(folderEnd))
                                bundles.Add(b);
                        }
                    }
                }
            }
            catch (Exception) { }
            return bundles;
        }

        /// <summary>After the geometry import: the group members of the new meshes carry the imported part counts.</summary>
        private void FixNewMeshPartCounts()
        {
            foreach (KeyValuePair<Guid, EbxAssetEntry> kv in newMeshLevels)
            {
                EbxAssetEntry meshEntry = App.AssetManager.GetEbxEntry(kv.Key);
                if (meshEntry == null)
                    continue;
                uint parts = (uint)Math.Max(1, PartCountOf(meshEntry));
                EbxAsset levelAsset;
                try { levelAsset = App.AssetManager.GetEbx(kv.Value); } catch (Exception) { continue; }
                bool changed = false;
                foreach (object obj in levelAsset.Objects)
                {
                    if (obj == null || obj.GetType().Name != "StaticModelGroupEntityData")
                        continue;
                    dynamic members = ((dynamic)obj).MemberDatas;
                    for (int i = 0; i < members.Count; i++)
                    {
                        dynamic member = members[i];
                        PointerRef meshRef = member.MeshAsset;
                        if (meshRef.Type != PointerRefType.External || meshRef.External.FileGuid != kv.Key)
                            continue;
                        uint current = (uint)member.PartComponentCount;
                        if (current == parts)
                            continue;
                        member.PartComponentCount = parts;
                        members[i] = member;
                        changed = true;
                        Log("  {0}: member of {1} PartComponentCount {2} -> {3}", kv.Value.Name, meshEntry.Filename, current, parts);
                    }
                }
                if (changed)
                    App.AssetManager.ModifyEbx(kv.Value.Name, levelAsset);
            }
        }

        /// <summary>Resolves the "(new) &lt;mesh&gt;" slot targets: the mesh is created (or re-used when it exists) and the target becomes its name.</summary>
        private void CreateNewSlotMeshes(List<PesStadiumPackage.SlotInfo> slots)
        {
            foreach (PesStadiumPackage.SlotInfo slot in slots)
            {
                string target = settings.SlotTargets[slot.Name];
                if (!target.StartsWith(NewTargetPrefix, StringComparison.OrdinalIgnoreCase))
                    continue;
                string meshName = target.Substring(NewTargetPrefix.Length).Trim().Replace('\\', '/').ToLowerInvariant();
                // a mesh left by an earlier import is completed rather than skipped: that import may have stopped after
                // copying it (no blueprint, no group member, no database entry - the rain mesh of the allianz import)
                EbxAssetEntry existing = App.AssetManager.GetEbxEntry(meshName);
                task.Update(string.Format(existing != null ? "Checking {0}" : "Creating {0}", meshName));
                EbxAssetEntry created = null;
                try
                {
                    created = CreateComponentMesh(slot, meshName, existing);
                }
                catch (Exception ex)
                {
                    // the other slots still import; the layer is reported as skipped
                    Log("  WARNING: {0} could not be created: {1}", meshName, ex.ToString());
                    report.Add(string.Format("{0} layer skipped: the new mesh {1} could not be created ({2}; see the log).", slot.Name, meshName, ex.Message));
                }
                settings.SlotTargets[slot.Name] = created != null ? created.Name : "";
            }
            slots.RemoveAll(s => string.IsNullOrEmpty(settings.SlotTargets[s.Name]));
            if (slots.Count == 0)
                throw new InvalidOperationException("No slot is mapped to a mesh");
        }

        /// <summary>
        /// Creates the component mesh, its blueprint, its group member and its variation database entry. With
        /// <paramref name="existing"/> (a mesh of an earlier import) only the missing parts are added.
        /// </summary>
        private EbxAssetEntry CreateComponentMesh(PesStadiumPackage.SlotInfo slot, string meshName, EbxAssetEntry existing)
        {
            string folder = settings.StadiumNameOnly.ToLower();
            string componentPrefix = "content/worlds/components/stadiums/" + folder + "/";
            string levelPrefix = "content/worlds/stadiums/" + settings.StadiumFolder.ToLower() + "/";
            Log(existing != null ? "--- {0}: {1} exists (earlier import): checking its blueprint, placement and database entry" : "--- {0}: new component mesh {1}", slot.Name, meshName);

            // the sublevel that shows the mesh (its group, bundles and variation database); the main level otherwise
            string sublevel = (slot.HostSublevel ?? "").Trim().ToLower();
            EbxAssetEntry levelEntry = null;
            if (sublevel != "")
            {
                levelEntry = App.AssetManager.GetEbxEntry(levelPrefix + sublevel);
                if (levelEntry == null && slot.Overlay)
                {
                    // a weather layer without its weather sublevel would be shown in every match
                    Log("  {0} has no '{1}' sublevel: the {2} layer is not imported", settings.StadiumFolder, sublevel, slot.Name);
                    report.Add(string.Format("{0} layer skipped: {1} has no {2} sublevel.", slot.Name, settings.StadiumFolder, sublevel));
                    return null;
                }
                if (levelEntry == null)
                    Log("  {0} has no '{1}' sublevel; the mesh goes into the main level and is always shown", settings.StadiumFolder, sublevel);
            }
            if (levelEntry == null)
            {
                sublevel = "";
                levelEntry = App.AssetManager.GetEbxEntry(levelPrefix + settings.StadiumFolder.ToLower());
                if (levelEntry == null)
                    throw new InvalidOperationException("level asset not found: " + levelPrefix + settings.StadiumFolder.ToLower());
            }
            // the bundles of the sublevel: those of its asset plus the stadium bundles of the meshes its group already places
            List<int> bundles = SublevelBundles(levelEntry);
            if (bundles.Count == 0)
                Log("  WARNING: {0} is in no bundle; the new mesh is put into the template's bundles", levelEntry.Name);

            // template: the slot's template mesh, else the stand mesh, else any component mesh with a blueprint
            EbxAssetEntry templateMesh = null, templateBlueprint = null;
            foreach (string candidate in new[] { slot.TemplateMesh, "snow_main_mesh", "standslower_mesh", "roof_mesh", "standsupper_mesh" })
            {
                if (string.IsNullOrEmpty(candidate))
                    continue;
                EbxAssetEntry m = App.AssetManager.GetEbxEntry(componentPrefix + candidate.ToLower());
                if (m == null)
                    continue;
                EbxAssetEntry bp = BlueprintOfMesh(m);
                if (bp == null)
                    continue;
                templateMesh = m; templateBlueprint = bp;
                break;
            }
            if (templateMesh == null)
            {
                foreach (EbxAssetEntry m in StadiumBaseLookup.ComponentMeshes(folder))
                {
                    EbxAssetEntry bp = BlueprintOfMesh(m);
                    if (bp == null)
                        continue;
                    templateMesh = m; templateBlueprint = bp;
                    break;
                }
            }
            if (templateMesh == null)
                throw new InvalidOperationException("no component mesh with a blueprint found under " + componentPrefix + " to copy");
            Log("  copy of {0} (blueprint {1}), shown by {2}, {3} bundle(s)", templateMesh.Name, templateBlueprint.Name, sublevel == "" ? "the main level" : "the " + sublevel + " sublevel", bundles.Count);

            // the mesh (ebx + mesh set + chunks)
            EbxAssetEntry meshEntry = existing;
            if (meshEntry == null)
            {
                DuplicationTool.MeshExtension meshExt = new DuplicationTool.MeshExtension();
                meshEntry = meshExt.DuplicateAsset(templateMesh, meshName, false, null);
                if (meshEntry == null)
                    throw new InvalidOperationException("duplication of " + templateMesh.Name + " failed");
            }
            EbxAsset meshAsset = App.AssetManager.GetEbx(meshEntry);
            if (bundles.Count > 0)
                SetMeshBundles(meshEntry, bundles);

            // the blueprint (ObjectBlueprint -> StaticModelEntityData -> Mesh)
            string blueprintName = meshName.EndsWith("_mesh") ? meshName.Substring(0, meshName.Length - 5) : meshName + "_blueprint";
            EbxAssetEntry bpEntry = App.AssetManager.GetEbxEntry(blueprintName);
            if (bpEntry == null)
            {
                DuplicationTool.DuplicateAssetExtension plainExt = new DuplicationTool.DuplicateAssetExtension();
                bpEntry = plainExt.DuplicateAsset(templateBlueprint, blueprintName, false, null);
                if (bpEntry == null)
                    throw new InvalidOperationException("duplication of " + templateBlueprint.Name + " failed");
            }
            EbxAsset bpAsset = App.AssetManager.GetEbx(bpEntry);
            int meshRefs = 0;
            foreach (object obj in bpAsset.Objects)
            {
                if (obj == null || obj.GetType().Name != "StaticModelEntityData")
                    continue;
                dynamic entity = obj;
                entity.Mesh = new PointerRef(new EbxImportReference { FileGuid = meshEntry.Guid, ClassGuid = meshAsset.RootInstanceGuid });
                meshRefs++;
            }
            if (meshRefs == 0)
                throw new InvalidOperationException(templateBlueprint.Name + " has no StaticModelEntityData");
            // the group member's MemberType is the blueprint's StaticModelEntityData, under a guid of its own
            object bpEntity = BlueprintEntity(bpAsset);
            Guid entityGuid = OwnEntityGuid(bpEntry, bpAsset, bpEntity, componentPrefix);
            bpAsset.AddDependency(meshEntry.Guid);
            App.AssetManager.ModifyEbx(bpEntry.Name, bpAsset);
            if (bundles.Count > 0)
                SetBundles(bpEntry, bundles, true);
            Log("  blueprint {0} -> {1} (StaticModelEntityData {2})", bpEntry.Name, meshEntry.Name, entityGuid);

            // the group member that places it (identity transform), unless a group of the stadium already does
            EbxAssetEntry placedIn;
            if (FindGroupMember(meshEntry.Guid, out placedIn) != null)
            {
                Log("  already placed by a group member in {0}", placedIn.Name);
                newMeshLevels[meshEntry.Guid] = placedIn;
                // a member written by b37/b38 points at the ObjectBlueprint: re-pointed at the entity
                PointMembersAtEntity(meshEntry, bpEntry, entityGuid);
            }
            else
            {
                AddGroupMember(levelEntry, templateMesh, templateBlueprint, meshEntry, meshAsset, bpEntry, entityGuid);
                newMeshLevels[meshEntry.Guid] = levelEntry;
            }

            // the variation database entry of the sublevel (kept when there is one)
            AddVariationDatabaseEntry(levelPrefix, sublevel, templateMesh, meshEntry, meshAsset);

            report.Add(string.Format(existing != null ? "Component mesh {0} of an earlier import completed and re-used ({1})." : "New component mesh {0} created ({1}).",
                meshEntry.Name, sublevel == "" ? "always shown" : "shown by the " + sublevel + " sublevel"));
            return meshEntry;
        }

        /// <summary>The ObjectBlueprint "&lt;mesh&gt;" of "&lt;mesh&gt;_mesh", else any blueprint of the folder whose StaticModelEntityData places the mesh.</summary>
        private static EbxAssetEntry BlueprintOfMesh(EbxAssetEntry meshEntry)
        {
            List<EbxAssetEntry> candidates = new List<EbxAssetEntry>();
            if (meshEntry.Name.EndsWith("_mesh", StringComparison.OrdinalIgnoreCase))
            {
                EbxAssetEntry byName = App.AssetManager.GetEbxEntry(meshEntry.Name.Substring(0, meshEntry.Name.Length - 5));
                if (byName != null)
                    candidates.Add(byName);
            }
            string folder = meshEntry.Name.Contains("/") ? meshEntry.Name.Substring(0, meshEntry.Name.LastIndexOf('/') + 1) : "";
            foreach (EbxAssetEntry bp in App.AssetManager.EnumerateEbx("ObjectBlueprint").ToList())
            {
                if (bp.Name.StartsWith(folder, StringComparison.OrdinalIgnoreCase) && !candidates.Contains(bp))
                    candidates.Add(bp);
            }
            foreach (EbxAssetEntry bp in candidates)
            {
                EbxAsset asset;
                try { asset = App.AssetManager.GetEbx(bp); } catch (Exception) { continue; }
                if (asset == null)
                    continue;
                foreach (object obj in asset.Objects)
                {
                    if (obj == null || obj.GetType().Name != "StaticModelEntityData")
                        continue;
                    PointerRef meshRef;
                    try { meshRef = ((dynamic)obj).Mesh; } catch (Exception) { continue; }
                    if (meshRef.Type == PointerRefType.External && meshRef.External.FileGuid == meshEntry.Guid)
                        return bp;
                }
            }
            return null;
        }

        /// <summary>The mesh asset, its mesh set and its lod chunks go into exactly these bundles.</summary>
        private static void SetMeshBundles(EbxAssetEntry meshEntry, List<int> bundles)
        {
            SetBundles(meshEntry, bundles, true);
            try
            {
                EbxAsset meshAsset = App.AssetManager.GetEbx(meshEntry);
                ulong rid = ((dynamic)meshAsset.RootObject).MeshSetResource;
                ResAssetEntry resEntry = App.AssetManager.GetResEntry(rid);
                if (resEntry == null)
                    return;
                SetBundles(resEntry, bundles, true);
                MeshSet meshSet = App.AssetManager.GetResAs<MeshSet>(resEntry);
                if (meshSet == null)
                    return;
                foreach (MeshSetLod lod in meshSet.Lods)
                {
                    if (lod.ChunkId == Guid.Empty)
                        continue;
                    ChunkAssetEntry chunk = App.AssetManager.GetChunkEntry(lod.ChunkId);
                    if (chunk != null)
                        SetBundles(chunk, bundles, true);
                }
            }
            catch (Exception) { }
        }

        /// <summary>
        /// Adds a StaticModelGroupMemberData for the new mesh to the level's group: a copy of the member that places
        /// the template mesh (any group of the stadium), else of the group's first member; one identity instance.
        /// Its MemberType is the blueprint's StaticModelEntityData (<paramref name="entityGuid"/>), as for all 18
        /// resolvable members of FIFA's levels in three projects: FIFA17.exe reads the member type as that entity (its
        /// part components at +0x60, one per PartComponentCount). b37/b38 pointed it at the ObjectBlueprint and the game
        /// crashed at 0x1463c7448 reading the guid stored after the blueprint as the part array (Camp Nou into
        /// allianz, 2026-10-25).
        /// </summary>
        private void AddGroupMember(EbxAssetEntry levelEntry, EbxAssetEntry templateMesh, EbxAssetEntry templateBlueprint, EbxAssetEntry meshEntry, EbxAsset meshAsset, EbxAssetEntry bpEntry, Guid entityGuid)
        {
            EbxAsset levelAsset = App.AssetManager.GetEbx(levelEntry);
            if (levelAsset == null)
                throw new InvalidOperationException("level asset not readable: " + levelEntry.Name);

            // the group: the one with the most component meshes of the stadium (the main level has one for its props and one for the components)
            dynamic group = null;
            int bestScore = -1;
            foreach (object obj in levelAsset.Objects)
            {
                if (obj == null || obj.GetType().Name != "StaticModelGroupEntityData")
                    continue;
                dynamic g = obj;
                int score = 0;
                try
                {
                    foreach (dynamic m in g.MemberDatas)
                    {
                        PointerRef meshRef = m.MeshAsset;
                        EbxAssetEntry e = meshRef.Type == PointerRefType.External ? App.AssetManager.GetEbxEntry(meshRef.External.FileGuid) : null;
                        if (e != null && e.Name.IndexOf("/components/stadiums/", StringComparison.OrdinalIgnoreCase) >= 0)
                            score += 100;
                        score++;
                    }
                }
                catch (Exception) { }
                if (score > bestScore) { bestScore = score; group = g; }
            }
            if (group == null)
                throw new InvalidOperationException(levelEntry.Name + " has no StaticModelGroupEntityData to place the mesh with");

            // the member to copy: the template mesh's member in any level asset of the stadium, else the group's first member
            object templateMember = FindGroupMember(templateMesh.Guid) ?? (group.MemberDatas.Count > 0 ? (object)group.MemberDatas[0] : null);
            dynamic member = templateMember != null ? CloneEbxObject(templateMember) : null;
            if (member == null)
                member = TypeLibrary.CreateObject("StaticModelGroupMemberData");
            if (member == null)
                throw new InvalidOperationException("StaticModelGroupMemberData could not be created");

            member.MemberType = new PointerRef(new EbxImportReference { FileGuid = bpEntry.Guid, ClassGuid = entityGuid });
            member.MeshAsset = new PointerRef(new EbxImportReference { FileGuid = meshEntry.Guid, ClassGuid = meshAsset.RootInstanceGuid });
            member.InstanceCount = (uint)1;
            dynamic transforms = member.InstanceTransforms;
            transforms.Clear();
            transforms.Add(IdentityTransform());
            foreach (string listName in new[] { "InstanceObjectVariation", "InstanceRenderingOverrides", "InstanceRadiosityTypeOverride", "InstanceTerrainShaderNodesEnable" })
            {
                try { ((dynamic)GetProperty(member, listName)).Clear(); } catch (Exception) { }
            }
            try { member.PartComponentCount = (uint)Math.Max(1, PartCountOf(meshEntry)); } catch (Exception) { }
            // the ids are sequential within the group (the main level numbers its second group from 0x10000)
            uint nextId = 0;
            try
            {
                foreach (dynamic m in group.MemberDatas)
                    nextId = Math.Max(nextId, (uint)m.HealthStateEntityManagerId + 1);
                member.HealthStateEntityManagerId = nextId;
            }
            catch (Exception) { }
            group.MemberDatas.Add(member);
            levelAsset.AddDependency(meshEntry.Guid);
            levelAsset.AddDependency(bpEntry.Guid);
            App.AssetManager.ModifyEbx(levelEntry.Name, levelAsset);
            Log("  {0}: group member added ({1} members, id {2})", levelEntry.Name, (int)group.MemberDatas.Count, nextId);
        }

        private static int PartCountOf(EbxAssetEntry meshEntry)
        {
            try
            {
                EbxAsset meshAsset = App.AssetManager.GetEbx(meshEntry);
                ulong rid = ((dynamic)meshAsset.RootObject).MeshSetResource;
                ResAssetEntry resEntry = App.AssetManager.GetResEntry(rid);
                MeshSet meshSet = resEntry != null ? App.AssetManager.GetResAs<MeshSet>(resEntry) : null;
                return meshSet != null ? meshSet.PartCount : 1;
            }
            catch (Exception) { return 1; }
        }

        // ─── the member type: the blueprint's StaticModelEntityData ────────────

        /// <summary>The StaticModelEntityData the blueprint's Object points at, else its first StaticModelEntityData.</summary>
        private static object BlueprintEntity(EbxAsset bpAsset)
        {
            object entity = null;
            try
            {
                PointerRef obj = ((dynamic)bpAsset.RootObject).Object;
                if (obj.Type == PointerRefType.Internal)
                    entity = obj.Internal;
            }
            catch (Exception) { }
            if (entity == null || entity.GetType().Name != "StaticModelEntityData")
                entity = bpAsset.Objects.FirstOrDefault(o => o != null && o.GetType().Name == "StaticModelEntityData");
            return entity;
        }

        /// <summary>
        /// The PartComponentData entries at the start of the entity's Components (the parts come first; components such
        /// as RuntimeTextureOverrideComponentData follow them - pitch, scoreclock). FIFA's members have exactly this
        /// many as their PartComponentCount; the only ones above it in three projects were roof 14/7 and standsupper
        /// 43/30 of the 2026-10-24 test_499 import, the members of the crash at 0x1463c744f.
        /// </summary>
        private static int LeadingPartComponents(object entity)
        {
            dynamic components = ((dynamic)entity).Components;
            int n = 0;
            for (int i = 0; i < components.Count; i++)
            {
                PointerRef r = components[i];
                object c = r.Type == PointerRefType.Internal ? r.Internal : null;
                if (c == null || c.GetType().Name != "PartComponentData")
                    break;
                n++;
            }
            return n;
        }

        /// <summary>The exported object of the asset with this instance guid, null when there is none.</summary>
        private static object ExportedObject(EbxAsset asset, Guid instanceGuid)
        {
            foreach (object obj in asset.Objects)
            {
                if (obj == null)
                    continue;
                AssetClassGuid g = ((dynamic)obj).GetInstanceGuid();
                if (g.IsExported && g.ExportedGuid == instanceGuid)
                    return obj;
            }
            return null;
        }

        /// <summary>
        /// The exported guid of the new blueprint's StaticModelEntityData. Frosty's duplication renews only the root
        /// guid, so a copy still exports the template's entity guid (pes_night and pes_rain of the allianz import both
        /// exported standslower's 2402b755-daac-a489-...); the copy gets a deterministic guid of its own, since the
        /// template and the copy are loaded together. A guid no other blueprint of the folder exports is kept.
        /// </summary>
        private Guid OwnEntityGuid(EbxAssetEntry bpEntry, EbxAsset bpAsset, object entity, string folderPrefix)
        {
            AssetClassGuid current = ((dynamic)entity).GetInstanceGuid();
            string sharedWith = current.IsExported ? BlueprintExportingEntity(current.ExportedGuid, bpEntry, folderPrefix) : null;
            if (current.IsExported && sharedWith == null)
                return current.ExportedGuid;
            Guid fresh = FrostySdk.Utils.GenerateDeterministicGuid(bpAsset.Objects, entity.GetType(), bpEntry.Guid);
            ((dynamic)entity).SetInstanceGuid(new AssetClassGuid(fresh, current.InternalId));
            Log("  {0}: StaticModelEntityData guid {1} -> {2} ({3})", bpEntry.Name, current, fresh,
                current.IsExported ? "the guid of " + sharedWith : "it was not exported");
            return fresh;
        }

        /// <summary>The other ObjectBlueprint under the folder whose StaticModelEntityData exports this guid, null when none does.</summary>
        private static string BlueprintExportingEntity(Guid guid, EbxAssetEntry self, string folderPrefix)
        {
            foreach (EbxAssetEntry bp in App.AssetManager.EnumerateEbx("ObjectBlueprint").ToList())
            {
                if (bp.Guid == self.Guid || !bp.Name.StartsWith(folderPrefix, StringComparison.OrdinalIgnoreCase))
                    continue;
                EbxAsset asset;
                try { asset = App.AssetManager.GetEbx(bp); } catch (Exception) { continue; }
                if (asset == null)
                    continue;
                object obj = ExportedObject(asset, guid);
                if (obj != null && obj.GetType().Name == "StaticModelEntityData")
                    return bp.Name;
            }
            return null;
        }

        /// <summary>Every group member of the stadium's levels that places the mesh gets the blueprint's entity as its MemberType.</summary>
        private void PointMembersAtEntity(EbxAssetEntry meshEntry, EbxAssetEntry bpEntry, Guid entityGuid)
        {
            string prefix = "content/worlds/stadiums/" + settings.StadiumFolder.ToLower() + "/";
            foreach (EbxAssetEntry e in App.AssetManager.EnumerateEbx().ToList())
            {
                if (!e.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (e.Type != "SubWorldData" && e.Type != "DetachedSubWorldData" && e.Type != "WorldPartData" && e.Type != "LayerData")
                    continue;
                EbxAsset asset;
                try { asset = App.AssetManager.GetEbx(e); } catch (Exception) { continue; }
                if (asset == null)
                    continue;
                bool changed = false;
                foreach (object obj in asset.Objects)
                {
                    if (obj == null || obj.GetType().Name != "StaticModelGroupEntityData")
                        continue;
                    dynamic members = ((dynamic)obj).MemberDatas;
                    for (int i = 0; i < members.Count; i++)
                    {
                        dynamic member = members[i];
                        PointerRef meshRef = member.MeshAsset;
                        if (meshRef.Type != PointerRefType.External || meshRef.External.FileGuid != meshEntry.Guid)
                            continue;
                        PointerRef typeRef = member.MemberType;
                        if (typeRef.Type == PointerRefType.External && typeRef.External.FileGuid == bpEntry.Guid && typeRef.External.ClassGuid == entityGuid)
                            continue;
                        member.MemberType = new PointerRef(new EbxImportReference { FileGuid = bpEntry.Guid, ClassGuid = entityGuid });
                        members[i] = member;
                        changed = true;
                        Log("  {0}: member of {1}: MemberType {2} -> {3}/{4} (StaticModelEntityData)", e.Name, meshEntry.Filename,
                            typeRef.Type == PointerRefType.External ? typeRef.External.FileGuid + "/" + typeRef.External.ClassGuid : typeRef.Type.ToString(), bpEntry.Guid, entityGuid);
                    }
                }
                if (changed)
                {
                    asset.AddDependency(bpEntry.Guid);
                    App.AssetManager.ModifyEbx(e.Name, asset);
                }
            }
        }

        /// <summary>
        /// Last check of every StaticModelGroupMemberData in the stadium's level assets for the two member errors that
        /// crash FIFA17.exe in FUN_1463c7370 (it loops PartComponentCount entries of the part components at +0x60 of the
        /// member type): a MemberType that is an ObjectBlueprint (re-pointed at the blueprint's StaticModelEntityData,
        /// what every member of FIFA's levels points at) and a PartComponentCount above the entity's part components
        /// (lowered to it). Both are written to the log; correct members are not touched.
        /// </summary>
        private void CheckGroupMembers()
        {
            string prefix = "content/worlds/stadiums/" + settings.StadiumFolder.ToLower() + "/";
            int checkedMembers = 0, fixedTypes = 0, fixedCounts = 0, unread = 0;
            Dictionary<Guid, EbxAsset> types = new Dictionary<Guid, EbxAsset>();
            foreach (EbxAssetEntry levelEntry in App.AssetManager.EnumerateEbx().ToList())
            {
                if (!levelEntry.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (levelEntry.Type != "SubWorldData" && levelEntry.Type != "DetachedSubWorldData" && levelEntry.Type != "WorldPartData" && levelEntry.Type != "LayerData")
                    continue;
                EbxAsset levelAsset;
                try { levelAsset = App.AssetManager.GetEbx(levelEntry); } catch (Exception) { continue; }
                if (levelAsset == null)
                    continue;
                bool changed = false;
                foreach (object obj in levelAsset.Objects)
                {
                    if (obj == null || obj.GetType().Name != "StaticModelGroupEntityData")
                        continue;
                    dynamic members = ((dynamic)obj).MemberDatas;
                    for (int i = 0; i < members.Count; i++)
                    {
                        dynamic member = members[i];
                        checkedMembers++;
                        PointerRef typeRef = member.MemberType;
                        if (typeRef.Type != PointerRefType.External)
                            continue;
                        EbxAssetEntry typeEntry = App.AssetManager.GetEbxEntry(typeRef.External.FileGuid);
                        EbxAsset typeAsset = null;
                        if (typeEntry != null && !types.TryGetValue(typeEntry.Guid, out typeAsset))
                        {
                            try { typeAsset = App.AssetManager.GetEbx(typeEntry); } catch (Exception) { typeAsset = null; }
                            types[typeEntry.Guid] = typeAsset;
                        }
                        object target = typeAsset != null ? ExportedObject(typeAsset, typeRef.External.ClassGuid) : null;
                        if (target == null)
                        {
                            unread++;
                            Log("  WARNING: {0} member {1}: MemberType {2}/{3} not found", levelEntry.Name, i, typeRef.External.FileGuid, typeRef.External.ClassGuid);
                            continue;
                        }
                        if (target.GetType().Name == "ObjectBlueprint")
                        {
                            object entity = BlueprintEntity(typeAsset);
                            AssetClassGuid eg = entity != null ? (AssetClassGuid)((dynamic)entity).GetInstanceGuid() : new AssetClassGuid(-1);
                            if (entity == null || !eg.IsExported)
                            {
                                unread++;
                                Log("  WARNING: {0} member {1}: MemberType is the ObjectBlueprint {2}, whose StaticModelEntityData is not exported", levelEntry.Name, i, typeEntry.Name);
                                continue;
                            }
                            member.MemberType = new PointerRef(new EbxImportReference { FileGuid = typeEntry.Guid, ClassGuid = eg.ExportedGuid });
                            members[i] = member;
                            changed = true;
                            fixedTypes++;
                            Log("  {0} member {1}: MemberType was the ObjectBlueprint {2}; now its StaticModelEntityData {3}", levelEntry.Name, i, typeEntry.Name, eg.ExportedGuid);
                            target = entity;
                        }
                        if (target.GetType().Name != "StaticModelEntityData")
                            continue;
                        int parts = LeadingPartComponents(target);
                        uint count = (uint)member.PartComponentCount;
                        if (count > parts)
                        {
                            member.PartComponentCount = (uint)parts;
                            members[i] = member;
                            changed = true;
                            fixedCounts++;
                            Log("  {0} member {1} ({2}): PartComponentCount {3} -> {4} (the entity's part components)", levelEntry.Name, i, typeEntry.Name, count, parts);
                        }
                    }
                }
                if (changed)
                    App.AssetManager.ModifyEbx(levelEntry.Name, levelAsset);
            }
            Log("--- group members: {0} checked in the stadium's levels; {1} member type(s) and {2} part count(s) corrected{3}", checkedMembers, fixedTypes, fixedCounts,
                unread > 0 ? ", " + unread + " not readable (see the warnings)" : "");
            if (fixedTypes + fixedCounts > 0)
                report.Add(string.Format("Group members corrected: {0} member type(s), {1} part count(s) (see the log).", fixedTypes, fixedCounts));
        }

        /// <summary>The StaticModelGroupMemberData that places the mesh in one of the stadium's level assets, null when none does.</summary>
        private object FindGroupMember(Guid meshGuid)
        {
            EbxAssetEntry levelEntry;
            return FindGroupMember(meshGuid, out levelEntry);
        }

        private object FindGroupMember(Guid meshGuid, out EbxAssetEntry levelEntry)
        {
            levelEntry = null;
            string prefix = "content/worlds/stadiums/" + settings.StadiumFolder.ToLower() + "/";
            foreach (EbxAssetEntry e in App.AssetManager.EnumerateEbx().ToList())
            {
                if (!e.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (e.Type != "SubWorldData" && e.Type != "DetachedSubWorldData" && e.Type != "WorldPartData" && e.Type != "LayerData")
                    continue;
                EbxAsset asset;
                try { asset = App.AssetManager.GetEbx(e); } catch (Exception) { continue; }
                if (asset == null)
                    continue;
                foreach (object obj in asset.Objects)
                {
                    if (obj == null || obj.GetType().Name != "StaticModelGroupEntityData")
                        continue;
                    dynamic members;
                    try { members = ((dynamic)obj).MemberDatas; } catch (Exception) { continue; }
                    foreach (object m in members)
                    {
                        PointerRef meshRef;
                        try { meshRef = ((dynamic)m).MeshAsset; } catch (Exception) { continue; }
                        if (meshRef.Type == PointerRefType.External && meshRef.External.FileGuid == meshGuid)
                        {
                            levelEntry = e;
                            return m;
                        }
                    }
                }
            }
            return null;
        }

        // dynamic return types: the binder then converts by the runtime type (EBX LinearTransform / Vec3). With
        // `object` the assignment `lt.right = Vec3(...)` failed: "Cannot implicitly convert type 'object' to
        // 'FrostySdk.Ebx.Vec3'" (allianz import, rain layer).
        private static dynamic IdentityTransform()
        {
            dynamic lt = TypeLibrary.CreateObject("LinearTransform");
            lt.right = Vec3(1, 0, 0);
            lt.up = Vec3(0, 1, 0);
            lt.forward = Vec3(0, 0, 1);
            lt.trans = Vec3(0, 0, 0);
            return lt;
        }

        private static dynamic Vec3(float x, float y, float z)
        {
            dynamic v = TypeLibrary.CreateObject("Vec3");
            v.x = x; v.y = y; v.z = z;
            return v;
        }

        private static object GetProperty(object obj, string name)
        {
            PropertyInfo p = obj.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
            return p != null ? p.GetValue(obj) : null;
        }

        /// <summary>
        /// The sublevel's MeshVariationDb_Win32 gets an entry for the new mesh: a copy of the template mesh's entry
        /// (same materials, since the mesh is a copy) pointing at the new mesh.
        /// </summary>
        private void AddVariationDatabaseEntry(string levelPrefix, string sublevel, EbxAssetEntry templateMesh, EbxAssetEntry meshEntry, EbxAsset meshAsset)
        {
            string dbName = levelPrefix + (sublevel == "" ? settings.StadiumFolder.ToLower() : sublevel) + "/MeshVariationDb_Win32";
            EbxAssetEntry dbEntry = App.AssetManager.GetEbxEntry(dbName);
            if (dbEntry == null)
            {
                // any database of the stadium that lists the template
                dbEntry = FindVariationDatabases(templateMesh).FirstOrDefault();
                if (dbEntry == null)
                {
                    Log("  WARNING: no mesh variation database for {0}; the materials of {1} are set on the mesh only", sublevel == "" ? "the main level" : sublevel, meshEntry.Name);
                    return;
                }
                Log("  {0} not found; the entry goes into {1}", dbName, dbEntry.Name);
            }
            EbxAsset dbAsset = App.AssetManager.GetEbx(dbEntry);
            dynamic db = dbAsset.RootObject;
            foreach (dynamic existing in db.Entries)
            {
                PointerRef r = existing.Mesh;
                if (r.Type == PointerRefType.External && r.External.FileGuid == meshEntry.Guid)
                {
                    Log("  {0} already lists {1}", dbEntry.Name, meshEntry.Name);
                    return;
                }
            }

            // the template's entry (from any database that lists it)
            object templateEntry = null;
            foreach (EbxAssetEntry tdb in FindVariationDatabases(templateMesh))
            {
                EbxAsset tAsset = App.AssetManager.GetEbx(tdb);
                if (tAsset == null)
                    continue;
                foreach (dynamic e in ((dynamic)tAsset.RootObject).Entries)
                {
                    PointerRef r = e.Mesh;
                    if (r.Type == PointerRefType.External && r.External.FileGuid == templateMesh.Guid)
                    {
                        templateEntry = e;
                        break;
                    }
                }
                if (templateEntry != null)
                    break;
            }

            dynamic entry = templateEntry != null ? CloneEbxObject(templateEntry) : null;
            if (entry == null)
            {
                entry = TypeLibrary.CreateObject("MeshVariationDatabaseEntry");
                if (entry == null)
                {
                    Log("  WARNING: MeshVariationDatabaseEntry could not be created; {0} is not listed in {1}", meshEntry.Name, dbEntry.Name);
                    return;
                }
                Log("  the template {0} has no variation database entry; a bare entry is written", templateMesh.Name);
            }
            entry.Mesh = new PointerRef(new EbxImportReference { FileGuid = meshEntry.Guid, ClassGuid = meshAsset.RootInstanceGuid });
            try { entry.VariationAssetNameHash = (uint)0; } catch (Exception) { }

            // the materials: the copy keeps the template's material guids
            dynamic materials = entry.Materials;
            HashSet<Guid> meshMaterials = new HashSet<Guid>();
            foreach (dynamic matRef in ((dynamic)meshAsset.RootObject).Materials)
            {
                dynamic material = matRef.Internal;
                if (material == null)
                    continue;
                AssetClassGuid g = material.GetInstanceGuid();
                meshMaterials.Add(g.ExportedGuid);
            }
            for (int i = materials.Count - 1; i >= 0; i--)
            {
                PointerRef r = materials[i].Material;
                if (r.Type != PointerRefType.External || !meshMaterials.Contains(r.External.ClassGuid))
                {
                    materials.RemoveAt(i);
                    continue;
                }
                dynamic dbMaterial = materials[i];
                dbMaterial.Material = new PointerRef(new EbxImportReference { FileGuid = meshEntry.Guid, ClassGuid = r.External.ClassGuid });
                materials[i] = dbMaterial;
            }
            if (materials.Count == 0)
            {
                dynamic template = TypeLibrary.CreateObject("MeshVariationDatabaseMaterial");
                foreach (Guid g in meshMaterials)
                {
                    dynamic m = CloneEbxObject(template);
                    m.Material = new PointerRef(new EbxImportReference { FileGuid = meshEntry.Guid, ClassGuid = g });
                    materials.Add(m);
                }
            }
            db.Entries.Add(entry);
            dbAsset.AddDependency(meshEntry.Guid);
            App.AssetManager.ModifyEbx(dbEntry.Name, dbAsset);
            try { MeshVariationDb.LoadModifiedVariations(); } catch (Exception) { }
            Log("  {0}: entry added ({1} materials)", dbEntry.Name, (int)materials.Count);
        }

        // ─── ebx object copies ────────────────────────────────────────────────

        private static object CloneEbxObject(object source)
        {
            if (source == null)
                return null;
            Type type = source.GetType();
            object copy = null;
            try { copy = TypeLibrary.CreateObject(type.Name); } catch (Exception) { }
            if (copy == null || copy.GetType() != type)
            {
                if (TypeLibrary.GetType(type.Name) != type)
                    return null;
                try { copy = Activator.CreateInstance(type); } catch (Exception) { return null; }
            }
            foreach (PropertyInfo prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (!prop.CanRead || prop.GetIndexParameters().Length != 0)
                    continue;
                if (prop.GetCustomAttribute<IsTransientAttribute>() != null)
                    continue;
                object value = prop.GetValue(source);
                if (value == null)
                    continue;
                if (prop.CanWrite)
                    prop.SetValue(copy, CloneEbxValue(value));
                else
                {
                    System.Collections.IList srcList = value as System.Collections.IList;
                    System.Collections.IList dstList = prop.GetValue(copy) as System.Collections.IList;
                    if (srcList != null && dstList != null)
                    {
                        dstList.Clear();
                        foreach (object item in srcList)
                            dstList.Add(CloneEbxValue(item));
                    }
                }
            }
            return copy;
        }

        private static object CloneEbxValue(object value)
        {
            if (value == null)
                return null;
            Type type = value.GetType();
            if (type.IsValueType || type.IsEnum || value is string)
                return value;
            System.Collections.IList srcList = value as System.Collections.IList;
            if (srcList != null)
            {
                System.Collections.IList dstList = (System.Collections.IList)Activator.CreateInstance(type);
                foreach (object item in srcList)
                    dstList.Add(CloneEbxValue(item));
                return dstList;
            }
            if (value is Array)
                return ((Array)value).Clone();
            if (TypeLibrary.GetType(type.Name) != type)
                return value;
            object copy = CloneEbxObject(value);
            return copy ?? value;
        }
    }
}
