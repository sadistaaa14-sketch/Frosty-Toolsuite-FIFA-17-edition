using AtlasTexturePlugin;
using BundleRefTablePlugin;
using DuplicationPlugin.Windows;
using Frosty.Controls;
using Frosty.Core;
using Frosty.Core.Windows;
using FrostySdk;
using FrostySdk.Ebx;
using FrostySdk.IO;
using FrostySdk.Managers;
using FrostySdk.Resources;
using MeshSetPlugin.Resources;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Media;

namespace DuplicationPlugin
{
    /// <summary>
    /// Duplicates an entire FIFA 17 stadium. Stadiums are not registered in the
    /// Bundle Ref Table; instead they are decomposed into sublevel bundles, one per
    /// SubWorldData / DetachedSubWorldData EBX asset.
    ///
    /// The three stadium-specific asset locations are:
    ///   1. content/worlds/stadiums/{stadiumId}          (e.g. allianz_137)
    ///   2. content/worlds/components/stadiums/{name}     (e.g. allianz)
    ///   3. content/effects/glares/{name}                 (e.g. allianz)
    ///
    /// References between assets in those three locations are rewired to the new
    /// duplicated assets; references to shared assets outside them are left alone.
    /// </summary>
    public class DuplicateStadiumMenuExtension : MenuExtension
    {
        private readonly Dictionary<string, DuplicationTool.DuplicateAssetExtension> extensions
            = new Dictionary<string, DuplicationTool.DuplicateAssetExtension>();

        // Debug capture of pointer remap lookups (cleared per run).
        private readonly List<string> pointerDebug = new List<string>();
        private readonly List<string> verificationReport = new List<string>();
        private readonly List<string> mirrorReport = new List<string>();
        private readonly List<string> brtReport = new List<string>();
        private readonly List<string> lightingReport = new List<string>();
        private int droppedForeignBundles;
        // Bundles are only ever appended, so every bundle id >= this one was created by the
        // current run; anything below it existed before (vanilla or added by earlier runs).
        private int firstNewBundleId;
        private const string L1_PREFIX = "content/worlds/stadiums/";
        private const string L2_PREFIX = "content/worlds/components/stadiums/";
        private const string L3_PREFIX = "content/effects/glares/";

        public DuplicateStadiumMenuExtension()
        {
            foreach (Type type in Assembly.GetExecutingAssembly().GetTypes())
            {
                if (type.IsSubclassOf(typeof(DuplicationTool.DuplicateAssetExtension)))
                {
                    DuplicationTool.DuplicateAssetExtension ext = (DuplicationTool.DuplicateAssetExtension)Activator.CreateInstance(type);
                    if (ext.AssetType != null)
                        extensions[ext.AssetType] = ext;
                }
            }
            extensions["null"] = new DuplicationTool.DuplicateAssetExtension();
        }

        public override string TopLevelMenuName => "Tools";
        public override string SubLevelMenuName => null;
        public override string MenuItemName => "Duplicate Stadium";
        public override ImageSource Icon => null;

        public override RelayCommand MenuItemClicked => new RelayCommand((o) =>
        {
            EbxAssetEntry entry = App.SelectedAsset as EbxAssetEntry;
            if (entry == null)
            {
                FrostyMessageBox.Show(
                    "No asset selected. Select any asset inside the stadium you want to duplicate " +
                    "(content/worlds/stadiums, content/worlds/components/stadiums or content/effects/glares).",
                    "Stadium Duplicator");
                return;
            }

            if (!TryDetectSourceStadium(entry, out string oldFolder, out string oldNameOnly))
            {
                FrostyMessageBox.Show(
                    "The selected asset is not inside a stadium folder.\n" +
                    "Expected one of:\n" +
                    "  content/worlds/stadiums/<stadium>\n" +
                    "  content/worlds/components/stadiums/<name>\n" +
                    "  content/effects/glares/<name>",
                    "Stadium Duplicator");
                return;
            }

            DuplicateStadiumWindow win = new DuplicateStadiumWindow(oldFolder, oldNameOnly);
            if (win.ShowDialog() != true)
                return;

            string newFolder = win.NewFolder;
            string newNameOnly = win.NewNameOnly;
            bool sharePitch = win.SharePitch;

            FrostyTaskWindow.Show("Duplicating Stadium", "", (task) =>
            {
                try
                {
                    DuplicateStadium(task, oldFolder, oldNameOnly, newFolder, newNameOnly, sharePitch);
                }
                catch (Exception ex)
                {
                    App.Logger.Log("Error duplicating stadium: " + ex.ToString());
                    FrostyMessageBox.Show("Failed to duplicate stadium: " + ex.Message, "Stadium Duplicator");
                }
            });

            App.EditorWindow.DataExplorer.RefreshAll();
        });

        // ─── Source detection ────────────────────────────────────────────────

        private static bool TryDetectSourceStadium(EbxAssetEntry entry, out string oldFolder, out string oldNameOnly)
        {
            oldFolder = null;
            oldNameOnly = null;

            string path = NormalizePath(entry.Name).ToLower();

            if (TryGetPathSegment(path, L1_PREFIX, out string folder))
            {
                oldFolder = folder;
                oldNameOnly = ExtractNameOnly(folder);
                return true;
            }

            string nameOnly = null;
            if (TryGetPathSegment(path, L2_PREFIX, out nameOnly) ||
                TryGetPathSegment(path, L3_PREFIX, out nameOnly))
            {
                oldNameOnly = nameOnly;
                oldFolder = ResolveStadiumFolder(nameOnly);
                return oldFolder != null;
            }

            return false;
        }

        /// <summary>
        /// Given a name-only stadium identifier (e.g. "allianz"), find the matching
        /// L1 stadium folder (e.g. "allianz_137") by scanning the stadiums location.
        /// </summary>
        private static string ResolveStadiumFolder(string nameOnly)
        {
            string prefix = L1_PREFIX + nameOnly.ToLower() + "_";
            foreach (EbxAssetEntry candidate in App.AssetManager.EnumerateEbx())
            {
                string p = NormalizePath(candidate.Name).ToLower();
                if (p.StartsWith(prefix) && TryGetPathSegment(p, L1_PREFIX, out string folder))
                    return folder;
            }
            return null;
        }

        /// <summary>
        /// Extract the name-only portion ("allianz") from a full stadium folder
        /// ("allianz_137"). Stadiums are always &lt;name&gt;_&lt;id&gt;.
        /// </summary>
        private static string ExtractNameOnly(string folder)
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

        // ─── Main duplication ────────────────────────────────────────────────

        private void DuplicateStadium(FrostyTaskWindow task, string oldFolder, string oldNameOnly, string newFolder, string newNameOnly, bool sharePitch)
        {
            pointerDebug.Clear();
            verificationReport.Clear();
            mirrorReport.Clear();
            brtReport.Clear();
            lightingReport.Clear();
            droppedForeignBundles = 0;
            firstNewBundleId = App.AssetManager.EnumerateBundles().Count();
            App.Logger.Log($"Duplicating stadium: folder {oldFolder} -> {newFolder}, name {oldNameOnly} -> {newNameOnly}" +
                           (sharePitch ? " (keeping the source pitch)" : ""));

            // 1. Enumerate every source asset in the three stadium locations.
            //    With "keep the source pitch" the pitch layer, pitch blueprint and pitch
            //    mesh are left out: the copies keep pointing at the source's, and
            //    MirrorSourceBundles puts those originals into the new bundles.
            List<EbxAssetEntry> sourceAssets = new List<EbxAssetEntry>();
            List<EbxAssetEntry> keptPitch = new List<EbxAssetEntry>();
            foreach (EbxAssetEntry entry in App.AssetManager.EnumerateEbx())
            {
                if (!IsInStadiumLocations(entry.Name, oldFolder, oldNameOnly))
                    continue;
                if (sharePitch && IsPitchAsset(entry.Name, oldFolder, oldNameOnly))
                {
                    keptPitch.Add(entry);
                    continue;
                }
                sourceAssets.Add(entry);
            }
            App.Logger.Log($"  Found {sourceAssets.Count} source assets");

            // Stadium-local source assets by the FileGuid in their EBX data (what imports
            // reference; EbxAssetEntry.Guid can be empty for sublevel bundle assets).
            Dictionary<Guid, EbxAssetEntry> localByFileGuid = new Dictionary<Guid, EbxAssetEntry>();
            foreach (EbxAssetEntry kept in keptPitch)
            {
                App.Logger.Log($"  Keeping source pitch asset {kept.Name} ({kept.Type})");
                mirrorReport.Add($"source pitch kept (not copied): {kept.Name} ({kept.Type})");
                try
                {
                    EbxAsset keptAsset = App.AssetManager.GetEbx(kept);
                    if (keptAsset != null)
                        localByFileGuid[keptAsset.FileGuid] = kept;
                }
                catch (Exception ex)
                {
                    App.Logger.Log($"  Could not read {kept.Name}: {ex.Message}");
                }
            }

            // SubWorldData / DetachedSubWorldData assets first: they create the new
            // sublevel bundles that everything else is then placed into.
            List<EbxAssetEntry> ordered = sourceAssets
                .OrderByDescending(e => IsSubWorldData(e.Type) ? 1 : 0)
                .ThenBy(e => NormalizePath(e.Name), StringComparer.OrdinalIgnoreCase)
                .ToList();

            // 2. Duplicate everything.
            Dictionary<Guid, Guid> guidMap = new Dictionary<Guid, Guid>();
            Dictionary<Guid, Guid> classGuidMap = new Dictionary<Guid, Guid>();
            List<Tuple<EbxAssetEntry, EbxAssetEntry>> pairs = new List<Tuple<EbxAssetEntry, EbxAssetEntry>>();
            List<EbxAssetEntry> duplicated = new List<EbxAssetEntry>();
            List<string> failures = new List<string>();
            HashSet<EbxAssetEntry> duplicatedSources = new HashSet<EbxAssetEntry>();

            int index = 0;
            foreach (EbxAssetEntry src in ordered)
            {
                index++;
                task.Update($"Duplicating {index}/{ordered.Count}: {src.Filename}");

                try
                {
                    EbxAsset oldAsset = App.AssetManager.GetEbx(src);
                    localByFileGuid[oldAsset.FileGuid] = src;
                    string newName = RewriteStadiumName(src.Name, oldFolder, oldNameOnly, newFolder, newNameOnly);
                    EbxAssetEntry newEntry = DuplicateStadiumAsset(src, newName);

                    if (newEntry == null)
                    {
                        App.Logger.Log($"  FAILED to duplicate {src.Name}");
                        failures.Add($"{src.Name} ({src.Type}) -> null");
                        continue;
                    }

                    // Meshes keep the source's root instance GUID. FIFA17.exe keys the Enlighten lighting of
                    // every static-lit mesh instance by FNV-1 over the mesh object's instance GUID and its
                    // placement (FUN_146b18400; used by both the dynamic and the static lookup), and the
                    // stadium's lighting databases are copied byte for byte. A copy with a new root GUID
                    // gets a key the databases do not contain and is drawn without its lighting (black
                    // at night). The copy keeps its own file GUID, MeshSet res and chunks.
                    if (TypeLibrary.IsSubClassOf(src.Type, "MeshAsset"))
                        RestoreRootInstanceGuid(newEntry, oldAsset.RootInstanceGuid);

                    // Key on the FileGuid read from the actual EBX data: PointerRef.External.FileGuid
                    // references that guid, whereas EbxAssetEntry.Guid can be Guid.Empty for sublevel
                    // bundle assets that were never indexed.
                    guidMap[oldAsset.FileGuid] = newEntry.Guid;
                    classGuidMap[oldAsset.RootInstanceGuid] = App.AssetManager.GetEbx(newEntry).RootInstanceGuid;
                    pairs.Add(new Tuple<EbxAssetEntry, EbxAssetEntry>(src, newEntry));
                    duplicated.Add(newEntry);
                    duplicatedSources.Add(src);

                    App.Logger.Log($"  {src.Name} -> {newEntry.Name}");
                }
                catch (Exception ex)
                {
                    App.Logger.Log($"  Failed to duplicate {src.Name}: {ex.Message}");
                    failures.Add($"{src.Name} ({src.Type}) -> {ex.Message}");
                }
            }

            // Diagnostic: any stadium-local source not in guidMap could not be
            // duplicated, so references to it cannot be remapped.
            List<EbxAssetEntry> notDuplicated = sourceAssets.Where(s => !duplicatedSources.Contains(s)).ToList();
            if (notDuplicated.Count > 0)
            {
                App.Logger.Log($"  WARNING: {notDuplicated.Count}/{sourceAssets.Count} stadium assets were NOT duplicated:");
                foreach (EbxAssetEntry s in notDuplicated)
                    App.Logger.Log($"    - {s.Name} ({s.Type})");
            }

            // 2b. Composite meshes keep their MeshSet res sections but the copy
            // round-trip leaves the embedded MeshMaterial "__Id" names generic.
            // Restore them from the res sections (the same thing opening the mesh
            // in the editor does), so the mesh-variation dropdown and the game can
            // resolve material variations without a manual open/save pass.
            foreach (EbxAssetEntry newEntry in duplicated)
            {
                if (newEntry.Type != "CompositeMeshAsset")
                    continue;
                try
                {
                    FixCompositeMeshMaterialNames(newEntry);
                }
                catch (Exception ex)
                {
                    App.Logger.Log($"  Failed to sync materials for {newEntry.Name}: {ex.Message}");
                }
            }

            // 3. Rewire stadium-local references in every duplicated asset.
            index = 0;
            foreach (EbxAssetEntry newEntry in duplicated)
            {
                index++;
                task.Update($"Fixing references {index}/{duplicated.Count}: {newEntry.Filename}");
                try
                {
                    RewriteReferences(newEntry, guidMap, classGuidMap, oldFolder, oldNameOnly, newFolder, newNameOnly);
                }
                catch (Exception ex)
                {
                    App.Logger.Log($"  Failed to fix references in {newEntry.Name}: {ex.Message}");
                }
            }

            // 3b. Verify that no stadium-local pointer still resolves to a source asset.
            task.Update("Verifying references...");
            HashSet<Guid> sourceFileGuids = new HashSet<Guid>(guidMap.Keys);
            int unresolved = 0;
            foreach (EbxAssetEntry newEntry in duplicated)
            {
                try
                {
                    EbxAsset check = App.AssetManager.GetEbx(newEntry);
                    if (check == null)
                        continue;
                    HashSet<object> seen = new HashSet<object>();
                    foreach (object obj in check.Objects)
                        CountUnrewiredPointers(obj, newEntry.Name, sourceFileGuids, seen, ref unresolved);
                }
                catch (Exception ex)
                {
                    App.Logger.Log($"  Verify failed for {newEntry.Name}: {ex.Message}");
                }
            }
            App.Logger.Log($"  Verification: {unresolved} stadium-local pointers still reference source assets");

            // 4. Place each duplicated asset (and its duplicated res/chunk linked
            //    assets) into the new bundle that corresponds to the source bundle.
            task.Update("Remapping bundle membership...");
            HashSet<AssetEntry> remappedSet = new HashSet<AssetEntry>();
            foreach (EbxAssetEntry newEntry in duplicated)
            {
                if (IsSubWorldData(newEntry.Type))
                    continue; // SubWorldDataExtension already assigned the fresh bundle
                RemapEntryBundlesDeep(newEntry, oldFolder, oldNameOnly, newFolder, newNameOnly, remappedSet);
            }

            // 5. Mirror every source bundle of the stadium (sublevel bundles and the
            //    blueprint bundles under components/stadiums/<name>) into its new
            //    bundle: shared EBX, and every res / chunk the source bundle carries
            //    that was not replaced by a duplicate. This covers data no EBX of the
            //    duplicate names directly (e.g. Bernabeu's standslower occluder mesh
            //    res, the per-bundle Ant state banks, IES profiles, BRT res).
            task.Update("Mirroring source bundles...");
            List<EbxAssetEntry> keptLocal = keptPitch.Concat(notDuplicated).ToList();
            MirrorSourceBundles(pairs, duplicatedSources, keptLocal, localByFileGuid, oldFolder, oldNameOnly, newFolder, newNameOnly);

            // 5b. Assets that a Bundle Ref Table resolves by name (Bernabeu's
            //     tournament_main through tournament_brt) get an entry for their
            //     duplicate, pointing at the duplicated blueprint bundle.
            if (!Config.Get<bool>("SkipBrtAdd", false))
            {
                task.Update("Updating BRT entries...");
                InjectBrtEntries(pairs, oldFolder, oldNameOnly, newFolder, newNameOnly);
            }
            else
            {
                brtReport.Add("SkipBrtAdd is set: no BRT entries added.");
            }

            // 6. Add the new stadium as a sublevel entry in the StadiumCollectionSLM
            //    sublevel map so the game knows about it at runtime.
            task.Update("Registering in SLM map...");
            AddToSLM(newFolder);

            // 7. Add the new stadium to the contentmanager prefab so it appears in
            //    the game's stadium selection.
            task.Update("Adding to contentmanager...");
            AddToContentManager(newFolder, oldFolder);

            App.Logger.Log($"Stadium duplication complete: {duplicated.Count} assets duplicated");

            // Written last so it includes the pointer-remap debug captured in phase 3.
            WriteDiagnosticReport(oldFolder, newFolder, sourceAssets, guidMap, notDuplicated, failures);
        }

        private void WriteDiagnosticReport(string oldFolder, string newFolder, List<EbxAssetEntry> sourceAssets,
            Dictionary<Guid, Guid> guidMap, List<EbxAssetEntry> notDuplicated, List<string> failures)
        {
            try
            {
                StringBuilder sb = new StringBuilder();
                sb.AppendLine($"Stadium duplication report: {oldFolder} -> {newFolder}");
                sb.AppendLine($"Found {sourceAssets.Count} source assets; duplicated {guidMap.Count}; failed {notDuplicated.Count}");
                sb.AppendLine();
                sb.AppendLine("Source assets by type:");
                foreach (IGrouping<string, EbxAssetEntry> g in sourceAssets.GroupBy(a => a.Type).OrderByDescending(g => g.Count()))
                    sb.AppendLine($"  {g.Key}: {g.Count()}");
                sb.AppendLine();
                sb.AppendLine("Assets NOT duplicated (references to these cannot be remapped):");
                foreach (EbxAssetEntry s in notDuplicated.OrderBy(s => s.Name))
                    sb.AppendLine($"  - {s.Name} ({s.Type}) guid={s.Guid}");
                sb.AppendLine();
                sb.AppendLine("Failures:");
                foreach (string f in failures)
                    sb.AppendLine($"  {f}");
                sb.AppendLine();
                sb.AppendLine("Sample source guids (first 20): entry.Guid vs actual FileGuid");
                foreach (EbxAssetEntry s in sourceAssets.Take(20))
                {
                    EbxAsset a = App.AssetManager.GetEbx(s);
                    sb.AppendLine($"  entry={s.Guid}  file={(a != null ? a.FileGuid.ToString() : "<null>")}  {s.Name}");
                }

                sb.AppendLine();
                sb.AppendLine($"Bundle mirror ({mirrorReport.Count} lines):");
                foreach (string line in mirrorReport)
                    sb.AppendLine($"  {line}");

                sb.AppendLine();
                sb.AppendLine($"BRT ({brtReport.Count} lines):");
                foreach (string line in brtReport)
                    sb.AppendLine($"  {line}");

                sb.AppendLine();
                sb.AppendLine($"Lighting keys: {lightingReport.Count} mesh copies keep their source's root instance GUID:");
                foreach (string line in lightingReport)
                    sb.AppendLine($"  {line}");

                sb.AppendLine();
                sb.AppendLine($"Verification: {verificationReport.Count} unrewired stadium-local pointers (first 200):");
                foreach (string line in verificationReport)
                    sb.AppendLine($"  {line}");

                sb.AppendLine();
                sb.AppendLine($"Pointer remap debug ({pointerDebug.Count} captured):");
                foreach (string line in pointerDebug)
                    sb.AppendLine($"  {line}");

                sb.AppendLine();
                sb.AppendLine("guidMap keys (first 20):");
                foreach (Guid g in guidMap.Keys.Take(20))
                    sb.AppendLine($"  {g}");

                string path = Path.Combine(Path.GetTempPath(), "stadium_dup_report.txt");
                File.WriteAllText(path, sb.ToString());
                App.Logger.Log($"  Diagnostic report written to: {path}");
            }
            catch (Exception ex)
            {
                App.Logger.Log($"  Failed to write diagnostic report: {ex.Message}");
            }
        }

        // ─── Duplication helpers ──────────────────────────────────────────────

        /// <summary>
        /// Gives a duplicated mesh its source's root instance GUID back (DuplicateAssetExtension sets a new,
        /// deterministic one). The engine identifies a static-lit mesh instance to the Enlighten databases by a
        /// hash of that GUID and the instance's placement, so the copy then matches the copied lighting data.
        /// References to the copy are rewired with classGuidMap, which then maps that root GUID to itself.
        /// </summary>
        private void RestoreRootInstanceGuid(EbxAssetEntry newEntry, Guid sourceRootGuid)
        {
            EbxAsset newAsset = App.AssetManager.GetEbx(newEntry);
            if (newAsset == null || sourceRootGuid == Guid.Empty)
                return;
            dynamic root = newAsset.RootObject;
            AssetClassGuid current = root.GetInstanceGuid();
            if (current.ExportedGuid == sourceRootGuid)
                return;
            root.SetInstanceGuid(new AssetClassGuid(sourceRootGuid, -1));
            App.AssetManager.ModifyEbx(newEntry.Name, newAsset);
            lightingReport.Add($"{newEntry.Name}: root {current.ExportedGuid} -> {sourceRootGuid} (source's)");
        }

        private EbxAssetEntry DuplicateStadiumAsset(EbxAssetEntry entry, string newName)
        {
            if (IsSubWorldData(entry.Type))
            {
                // Create the new sublevel bundle explicitly (covers SubWorldData and
                // DetachedSubWorldData) rather than relying on dispatch inheritance.
                return new DuplicationTool.SubWorldDataExtension().DuplicateAsset(entry, newName, false, null);
            }
            return DuplicateWithExtension(entry, newName);
        }

        private EbxAssetEntry DuplicateWithExtension(EbxAssetEntry entry, string newName)
        {
            try
            {
                string key = "null";
                foreach (string typeKey in extensions.Keys)
                {
                    if (typeKey != "null" && TypeLibrary.IsSubClassOf(entry.Type, typeKey))
                    {
                        key = typeKey;
                        break;
                    }
                }
                return extensions[key].DuplicateAsset(entry, newName, false, null);
            }
            catch (Exception ex)
            {
                App.Logger.Log($"  Failed to duplicate {entry.Name}: {ex.Message}");
                return null;
            }
        }

        // Only DetachedSubWorldData and SubWorldData create a new sublevel bundle.
        // WorldPartData / LayerData are bundle *contents* (referenced as blueprints of
        // WorldPartReferenceObjectData / LayerReferenceObjectData); they must NOT each
        // mint their own bundle, or SubWorldDataExtension would throw on their empty
        // bundle lists and they would never make it into guidMap.
        private static bool IsSubWorldData(string type)
            => type == "SubWorldData"
               || type == "DetachedSubWorldData";

        /// <summary>
        /// Restore the embedded material names of a duplicated composite mesh from
        /// its MeshSet res sections. Frosty's MeshSet editor does exactly this in
        /// UpdateMeshSettings (material.__Id = section.Name) when the mesh is opened;
        /// a fresh duplicate otherwise keeps generic "MeshMaterial" names, which is
        /// why the mesh-variation dropdown is empty and the game can't resolve the
        /// material variations until the mesh has been opened and the project saved.
        /// </summary>
        private void FixCompositeMeshMaterialNames(EbxAssetEntry entry)
        {
            EbxAsset asset = App.AssetManager.GetEbx(entry);
            if (asset == null)
                return;

            dynamic root = asset.RootObject;
            ResAssetEntry resEntry = App.AssetManager.GetResEntry(root.MeshSetResource);
            if (resEntry == null)
                return;

            MeshSet meshSet = App.AssetManager.GetResAs<MeshSet>(resEntry);
            if (meshSet == null || meshSet.Lods == null || meshSet.Lods.Count == 0)
                return;

            dynamic materials = root.Materials;
            if (materials == null || materials.Count == 0)
                return;

            bool changed = false;
            foreach (MeshSetLod lod in meshSet.Lods)
            {
                foreach (MeshSetSection section in lod.Sections)
                {
                    if (!lod.IsSectionRenderable(section))
                        continue;
                    if (section.MaterialId < 0 || section.MaterialId >= materials.Count)
                        continue;

                    dynamic material = materials[section.MaterialId].Internal;
                    if (material == null)
                        continue;

                    string name = section.Name;
                    if (string.IsNullOrEmpty(name))
                        continue;

                    material.__Id = name;
                    changed = true;
                }
            }

            if (changed)
            {
                App.AssetManager.ModifyEbx(entry.Name, asset);
                App.Logger.Log($"  Synced composite mesh material names: {entry.Name}");
            }
        }

        // ─── Reference rewiring ───────────────────────────────────────────────

        private void RewriteReferences(EbxAssetEntry newEntry, Dictionary<Guid, Guid> guidMap, Dictionary<Guid, Guid> classGuidMap,
            string oldFolder, string oldNameOnly, string newFolder, string newNameOnly)
        {
            EbxAsset asset = App.AssetManager.GetEbx(newEntry);
            if (asset == null)
            {
                App.Logger.Log($"  RewriteReferences: GetEbx returned null for {newEntry.Name}");
                return;
            }

            int pointerCount = 0;
            int stringCount = 0;

            // EBX class instances live in asset.Objects, but EBX "structs" are nested
            // inline and modelled as .NET reference types. We visit every class instance
            // from the top level and also descend into inline struct fields, because
            // pointer/string fields live on both.
            HashSet<object> visited = new HashSet<object>();
            foreach (object obj in asset.Objects)
            {
                try
                {
                    RewriteObject(obj, guidMap, classGuidMap, oldFolder, oldNameOnly, newFolder, newNameOnly, ref pointerCount, ref stringCount, visited);
                }
                catch (Exception ex)
                {
                    App.Logger.Log($"  RewriteReferences: {newEntry.Name}: error walking {obj?.GetType().Name}: {ex.Message}");
                }
            }

            // Persist even if some objects failed.
            App.AssetManager.ModifyEbx(newEntry.Name, asset);

            // The References tab resolves dependencies from the asset's import list
            // (ModifiedEntry.DependentAssets), which ModifyEbx just copied verbatim from
            // asset.Dependencies. Remap it so stadium-local dependencies point at the
            // new duplicated assets instead of the old stadium.
            List<Guid> correctedDependencies = new List<Guid>();
            foreach (Guid dep in asset.Dependencies)
                correctedDependencies.Add(guidMap.TryGetValue(dep, out Guid mapped) ? mapped : dep);
            if (newEntry.ModifiedEntry != null)
            {
                newEntry.ModifiedEntry.DependentAssets.Clear();
                newEntry.ModifiedEntry.DependentAssets.AddRange(correctedDependencies);
            }

            if (pointerCount > 0 || stringCount > 0)
                App.Logger.Log($"  Rewrote {newEntry.Name}: {pointerCount} pointer refs, {stringCount} strings");
        }

        /// <summary>
        /// Recursively walks an EBX object graph and rewrites external pointer
        /// references (FileGuid/ClassGuid) plus path-embedded string fields
        /// (BundleName, PreloadedBundleNames, nested Name fields) that point at
        /// stadium-local data. Both CString (EbxFieldType.CString) and plain string
        /// (EbxFieldType.String) fields are handled.
        ///
        /// EBX class instances live in asset.Objects and are reached from the top
        /// level; EBX structs are nested inline and modelled as .NET reference types
        /// (classes). We descend into any FrostySdk.Ebx type that is not one of the
        /// known leaf wrappers, so pointer/string fields on both are rewritten.
        /// </summary>
        private void RewriteObject(object obj, Dictionary<Guid, Guid> guidMap, Dictionary<Guid, Guid> classGuidMap,
            string oldFolder, string oldNameOnly, string newFolder, string newNameOnly,
            ref int pointerCount, ref int stringCount, HashSet<object> visited)
        {
            if (obj == null || !visited.Add(obj))
                return;

            Type objType = obj.GetType();
            if (objType.IsPrimitive || objType.IsEnum || objType == typeof(string))
                return;

            foreach (PropertyInfo pi in objType.GetProperties(BindingFlags.Instance | BindingFlags.Public))
            {
                if (pi.GetIndexParameters().Length != 0)
                    continue;

                Type pt = pi.PropertyType;

                if (pt == typeof(PointerRef))
                {
                    PointerRef pr = (PointerRef)pi.GetValue(obj);
                    PointerRef rewritten = RewritePointer(pr, guidMap, classGuidMap, ref pointerCount);
                    if (!rewritten.Equals(pr))
                    {
                        pi.SetValue(obj, rewritten);
                        PointerRef after = (PointerRef)pi.GetValue(obj);
                        if (!after.Equals(rewritten) && pointerDebug.Count < 140)
                            pointerDebug.Add($"SETFAIL {objType.Name}.{pi.Name}: set={rewritten.External.FileGuid} after={after.External.FileGuid}");
                    }
                }
                else if (pt == typeof(CString))
                {
                    string old = (string)(CString)pi.GetValue(obj);
                    string rewritten = RewriteStadiumString(old, oldFolder, oldNameOnly, newFolder, newNameOnly);
                    if (!string.Equals(old, rewritten, StringComparison.Ordinal))
                    {
                        pi.SetValue(obj, new CString(rewritten));
                        stringCount++;
                    }
                }
                else if (pt == typeof(string))
                {
                    string old = (string)pi.GetValue(obj);
                    string rewritten = RewriteStadiumString(old, oldFolder, oldNameOnly, newFolder, newNameOnly);
                    if (!string.Equals(old, rewritten, StringComparison.Ordinal))
                    {
                        pi.SetValue(obj, rewritten);
                        stringCount++;
                    }
                }
                else if (pt == typeof(BoxedValueRef))
                {
                    BoxedValueRef boxed = pi.GetValue(obj) as BoxedValueRef;
                    if (boxed != null)
                        RewriteBoxedValue(boxed, guidMap, classGuidMap, oldFolder, oldNameOnly, newFolder, newNameOnly, ref pointerCount, ref stringCount, visited);
                }
                else if (IsListType(pt))
                {
                    System.Collections.IList list = pi.GetValue(obj) as System.Collections.IList;
                    if (list == null)
                        continue;

                    Type elem = pt.GetGenericArguments()[0];
                    for (int i = 0; i < list.Count; i++)
                    {
                        if (elem == typeof(PointerRef))
                        {
                            PointerRef pr = (PointerRef)list[i];
                            PointerRef rewritten = RewritePointer(pr, guidMap, classGuidMap, ref pointerCount);
                            if (!rewritten.Equals(pr))
                            {
                                list[i] = rewritten;
                                PointerRef after = (PointerRef)list[i];
                                if (!after.Equals(rewritten) && pointerDebug.Count < 140)
                                    pointerDebug.Add($"SETFAIL list {objType.Name}.{pi.Name}[{i}]: set={rewritten.External.FileGuid} after={after.External.FileGuid}");
                            }
                        }
                        else if (elem == typeof(CString))
                        {
                            string old = (string)(CString)list[i];
                            string rewritten = RewriteStadiumString(old, oldFolder, oldNameOnly, newFolder, newNameOnly);
                            if (!string.Equals(old, rewritten, StringComparison.Ordinal))
                            {
                                list[i] = new CString(rewritten);
                                stringCount++;
                            }
                        }
                        else if (elem == typeof(string))
                        {
                            string old = (string)list[i];
                            string rewritten = RewriteStadiumString(old, oldFolder, oldNameOnly, newFolder, newNameOnly);
                            if (!string.Equals(old, rewritten, StringComparison.Ordinal))
                            {
                                list[i] = rewritten;
                                stringCount++;
                            }
                        }
                        else if (elem == typeof(BoxedValueRef))
                        {
                            BoxedValueRef boxed = list[i] as BoxedValueRef;
                            if (boxed != null)
                                RewriteBoxedValue(boxed, guidMap, classGuidMap, oldFolder, oldNameOnly, newFolder, newNameOnly, ref pointerCount, ref stringCount, visited);
                        }
                        else if (IsEbxStruct(elem))
                        {
                            // Inline EBX struct element: a .NET reference type, so the
                            // instance can be mutated in place.
                            if (list[i] != null)
                                RewriteObject(list[i], guidMap, classGuidMap, oldFolder, oldNameOnly, newFolder, newNameOnly, ref pointerCount, ref stringCount, visited);
                        }
                        // Primitive / Guid / enum elements carry no nested pointers.
                    }
                }
                else if (IsEbxStruct(pt))
                {
                    // Inline EBX struct field: a .NET reference type, mutated in place.
                    object child = pi.GetValue(obj);
                    if (child != null)
                        RewriteObject(child, guidMap, classGuidMap, oldFolder, oldNameOnly, newFolder, newNameOnly, ref pointerCount, ref stringCount, visited);
                }
                // Primitive / Guid / enum fields carry no nested pointers.
            }
        }

        private static bool IsListType(Type type)
            => type.IsGenericType && typeof(System.Collections.IList).IsAssignableFrom(type);

        /// <summary>
        /// EBX structs are modelled as .NET reference types in the FrostySdk.Ebx
        /// namespace. This excludes the leaf wrappers (PointerRef, CString, ...) that
        /// need no further recursion.
        /// </summary>
        private static bool IsEbxStruct(Type type)
        {
            if (type.Namespace != "FrostySdk.Ebx")
                return false;
            if (type.BaseType == typeof(Enum))
                return false;
            return type != typeof(PointerRef)
                && type != typeof(CString)
                && type != typeof(BoxedValueRef)
                && type != typeof(ResourceRef)
                && type != typeof(FileRef)
                && type != typeof(TypeRef);
        }

        /// <summary>
        /// Mirrors the RewriteObject traversal and counts external pointers whose
        /// FileGuid is still one of the source stadium guids, i.e. pointers that the
        /// rewrite pass failed to remap. These are the references that would still
        /// show the old stadium name in the editor.
        /// </summary>
        private void CountUnrewiredPointers(object obj, string assetName, HashSet<Guid> sourceFileGuids,
            HashSet<object> seen, ref int unresolved)
        {
            if (obj == null || !seen.Add(obj))
                return;

            Type objType = obj.GetType();
            if (objType.IsPrimitive || objType.IsEnum || objType == typeof(string))
                return;

            foreach (PropertyInfo pi in objType.GetProperties(BindingFlags.Instance | BindingFlags.Public))
            {
                if (pi.GetIndexParameters().Length != 0)
                    continue;

                Type pt = pi.PropertyType;

                if (pt == typeof(PointerRef))
                {
                    PointerRef pr = (PointerRef)pi.GetValue(obj);
                    if (pr.Type == PointerRefType.External && sourceFileGuids.Contains(pr.External.FileGuid))
                    {
                        unresolved++;
                        if (verificationReport.Count < 200)
                            verificationReport.Add($"{assetName}: {objType.Name}.{pi.Name} -> {pr.External.FileGuid}");
                    }
                }
                else if (IsListType(pt))
                {
                    System.Collections.IList list = pi.GetValue(obj) as System.Collections.IList;
                    if (list == null)
                        continue;

                    Type elem = pt.GetGenericArguments()[0];
                    for (int i = 0; i < list.Count; i++)
                    {
                        if (elem == typeof(PointerRef))
                        {
                            PointerRef pr = (PointerRef)list[i];
                            if (pr.Type == PointerRefType.External && sourceFileGuids.Contains(pr.External.FileGuid))
                            {
                                unresolved++;
                                if (verificationReport.Count < 200)
                                    verificationReport.Add($"{assetName}: {objType.Name}.{pi.Name}[{i}] -> {pr.External.FileGuid}");
                            }
                        }
                        else if (IsEbxStruct(elem) && list[i] != null)
                        {
                            CountUnrewiredPointers(list[i], assetName, sourceFileGuids, seen, ref unresolved);
                        }
                    }
                }
                else if (IsEbxStruct(pt))
                {
                    object child = pi.GetValue(obj);
                    if (child != null)
                        CountUnrewiredPointers(child, assetName, sourceFileGuids, seen, ref unresolved);
                }
            }
        }

        private PointerRef RewritePointer(PointerRef pr, Dictionary<Guid, Guid> guidMap, Dictionary<Guid, Guid> classGuidMap, ref int pointerCount)
        {
            if (pr.Type != PointerRefType.External)
                return pr;

            if (!guidMap.TryGetValue(pr.External.FileGuid, out Guid newFileGuid))
            {
                if (pointerDebug.Count < 100)
                    pointerDebug.Add($"MISS file={pr.External.FileGuid} class={pr.External.ClassGuid}");
                return pr;
            }

            if (pointerDebug.Count < 100)
                pointerDebug.Add($"HIT  file={pr.External.FileGuid} -> {newFileGuid}");

            Guid newClassGuid = classGuidMap.TryGetValue(pr.External.ClassGuid, out Guid mappedClass)
                ? mappedClass
                : pr.External.ClassGuid;

            pointerCount++;
            return new PointerRef(new EbxImportReference { FileGuid = newFileGuid, ClassGuid = newClassGuid });
        }

        private void RewriteBoxedValue(BoxedValueRef boxed, Dictionary<Guid, Guid> guidMap, Dictionary<Guid, Guid> classGuidMap,
            string oldFolder, string oldNameOnly, string newFolder, string newNameOnly,
            ref int pointerCount, ref int stringCount, HashSet<object> visited)
        {
            if (boxed.Value == null)
                return;

            if (boxed.Value is PointerRef pr)
            {
                PointerRef rewritten = RewritePointer(pr, guidMap, classGuidMap, ref pointerCount);
                if (!rewritten.Equals(pr))
                    boxed.SetValue(rewritten);
            }
            else if (boxed.Value is CString cstr)
            {
                string old = (string)cstr;
                string rewritten = RewriteStadiumString(old, oldFolder, oldNameOnly, newFolder, newNameOnly);
                if (!string.Equals(old, rewritten, StringComparison.Ordinal))
                {
                    boxed.SetValue(new CString(rewritten));
                    stringCount++;
                }
            }
            else if (boxed.Value is string s)
            {
                string rewritten = RewriteStadiumString(s, oldFolder, oldNameOnly, newFolder, newNameOnly);
                if (!string.Equals(s, rewritten, StringComparison.Ordinal))
                {
                    boxed.SetValue(rewritten);
                    stringCount++;
                }
            }
            else
            {
                RewriteObject(boxed.Value, guidMap, classGuidMap, oldFolder, oldNameOnly, newFolder, newNameOnly, ref pointerCount, ref stringCount, visited);
            }
        }

        // ─── Bundle remapping ─────────────────────────────────────────────────

        private void RemapEntryBundlesDeep(AssetEntry entry, string oldFolder, string oldNameOnly, string newFolder, string newNameOnly, HashSet<AssetEntry> visited)
        {
            if (entry == null || !visited.Add(entry))
                return;

            RemapEntryBundles(entry, oldFolder, oldNameOnly, newFolder, newNameOnly);
            foreach (AssetEntry linked in entry.LinkedAssets)
                RemapEntryBundlesDeep(linked, oldFolder, oldNameOnly, newFolder, newNameOnly, visited);
        }

        private void RemapEntryBundles(AssetEntry entry, string oldFolder, string oldNameOnly, string newFolder, string newNameOnly)
        {
            if (entry.AddedBundles == null || entry.AddedBundles.Count == 0)
                return;

            bool changed = false;
            List<int> remapped = new List<int>();
            List<int> foreign = new List<int>();

            foreach (int bundleId in entry.AddedBundles)
            {
                BundleEntry bundle = App.AssetManager.GetBundleEntry(bundleId);
                bool preexisting = bundle != null && bundleId < firstNewBundleId;
                if (preexisting && IsStadiumLocalBundle(bundle, oldFolder, oldNameOnly))
                {
                    int newBundleId = GetOrCreateNewBundle(bundle, oldFolder, oldNameOnly, newFolder, newNameOnly);
                    if (newBundleId >= 0)
                    {
                        remapped.Add(newBundleId);
                        changed = true;
                        continue;
                    }
                }

                if (preexisting)
                    foreign.Add(bundleId);   // a bundle of another level
                else
                    remapped.Add(bundleId);  // a bundle this run created
            }

            if (!changed)
                return;

            // A source that also sits in other levels' bundles (bernabeu/pitch is in 93:
            // every stadium that uses pitch_grass_prefab lists it in PitchGeo, plus the
            // story sets) must not drag its duplicate into them: nothing there references
            // the duplicate. Once one of its bundles was a stadium bundle, the duplicate keeps
            // only the new stadium's bundles; a source found only in other levels' bundles
            // (none in the vanilla game) keeps them, so its copy is never left in no bundle.
            entry.AddedBundles.Clear();
            foreach (int bundleId in remapped.Distinct())
            {
                if (!entry.Bundles.Contains(bundleId) && !entry.AddedBundles.Contains(bundleId))
                    entry.AddedBundles.Add(bundleId);
            }

            if (foreign.Count > 0)
            {
                droppedForeignBundles += foreign.Count;
                if (mirrorReport.Count < 400)
                    mirrorReport.Add($"{entry.Name}: kept out of {foreign.Count} bundle(s) of other levels the source is also in");
            }
        }

        private int GetOrCreateNewBundle(BundleEntry oldBundle, string oldFolder, string oldNameOnly, string newFolder, string newNameOnly)
        {
            string name = oldBundle.Name;
            string prefix = "";
            if (name.ToLower().StartsWith("win32/"))
            {
                prefix = name.Substring(0, 6);
                name = name.Substring(6);
            }

            string newName = RewriteStadiumName(name, oldFolder, oldNameOnly, newFolder, newNameOnly).ToLower();
            BundleEntry newBundle = App.AssetManager.AddBundle(prefix + newName, oldBundle.Type, oldBundle.SuperBundleId);
            return App.AssetManager.GetBundleId(newBundle);
        }

        /// <summary>
        /// Mirrors every original bundle of the source stadium into its new bundle.
        ///
        /// The duplicates themselves are placed by RemapEntryBundlesDeep. What is left
        /// is everything else the source bundle carries:
        ///   - shared EBX (outside the three stadium locations) and, for the types
        ///     AddLinkedAssetsToBundle knows, their res and chunks;
        ///   - stadium-local EBX that could not be duplicated (the duplicate still points
        ///     at them, so they must travel with it);
        ///   - every res and chunk of the source bundle that no duplicate replaced. No EBX
        ///     names some of these, so a per-EBX walk never finds them: Bernabeu's
        ///     standslower_mesh_occludermesh res (standslower_mesh.OccluderMeshResource
        ///     points at it and the duplicate keeps that RID), the per-bundle Ant state
        ///     banks, IES light profiles, the tournament BRT res.
        /// Blueprint bundles under components/stadiums/&lt;name&gt; (the BRT bundle
        /// tournament_main_tournament_brt) are mirrored the same way as sublevel bundles.
        /// </summary>
        private void MirrorSourceBundles(List<Tuple<EbxAssetEntry, EbxAssetEntry>> pairs, HashSet<EbxAssetEntry> duplicatedSources,
            List<EbxAssetEntry> keptLocal, Dictionary<Guid, EbxAssetEntry> localByFileGuid,
            string oldFolder, string oldNameOnly, string newFolder, string newNameOnly)
        {
            // Source assets that travel with the new stadium as they are: stadium-local assets that
            // were not copied (Keep the source pitch, failed copies) and every stadium-local asset
            // they reference, because their references were not rewired to the copies.
            HashSet<EbxAssetEntry> requiredOriginals = CollectRequiredOriginals(keptLocal, localByFileGuid, oldFolder, oldNameOnly);
            foreach (EbxAssetEntry req in requiredOriginals.Where(r => duplicatedSources.Contains(r)))
                mirrorReport.Add($"source kept as well as copied (a kept asset references it): {req.Name}");

            HashSet<EbxAssetEntry> copies = new HashSet<EbxAssetEntry>(pairs.Select(p => p.Item2));

            // Res / chunks of the source whose content was copied into a new res / chunk.
            HashSet<ulong> replacedRes = new HashSet<ulong>();
            HashSet<Guid> replacedChunks = new HashSet<Guid>();
            foreach (Tuple<EbxAssetEntry, EbxAssetEntry> pair in pairs)
            {
                if (!requiredOriginals.Contains(pair.Item1))
                    CollectReplacedData(pair.Item1, pair.Item2, replacedRes, replacedChunks);
            }
            mirrorReport.Add($"replaced by duplicates: {replacedRes.Count} res, {replacedChunks.Count} chunks");

            // Source bundles: the stadium's bundles that existed before this run. Their new
            // counterparts were created by the SubWorldData / BlueprintBundle extensions or by
            // RemapEntryBundles; GetOrCreateNewBundle returns those by name.
            Dictionary<int, int> bundleMap = new Dictionary<int, int>();
            for (int id = 0; id < firstNewBundleId; id++)
            {
                BundleEntry oldBundle = App.AssetManager.GetBundleEntry(id);
                if (oldBundle == null || !IsStadiumLocalBundle(oldBundle, oldFolder, oldNameOnly))
                    continue;
                int newId = GetOrCreateNewBundle(oldBundle, oldFolder, oldNameOnly, newFolder, newNameOnly);
                if (newId < 0 || newId == id)
                {
                    mirrorReport.Add($"{oldBundle.Name}: no new bundle (new id {newId}), skipped");
                    continue;
                }
                bundleMap[id] = newId;
            }

            // Res and chunks per source bundle. EnumerateRes/EnumerateChunks(BundleEntry) only see
            // original membership; EnumerateBundles() also covers bundles a project added them to
            // (a stadium made by an earlier duplication).
            Dictionary<int, List<ResAssetEntry>> resByBundle = new Dictionary<int, List<ResAssetEntry>>();
            foreach (ResAssetEntry res in App.AssetManager.EnumerateRes())
            {
                if (replacedRes.Contains(res.ResRid))
                    continue;
                foreach (int bid in res.EnumerateBundles())
                {
                    if (!bundleMap.ContainsKey(bid))
                        continue;
                    if (!resByBundle.TryGetValue(bid, out List<ResAssetEntry> list))
                        resByBundle[bid] = list = new List<ResAssetEntry>();
                    list.Add(res);
                }
            }
            Dictionary<int, List<ChunkAssetEntry>> chunksByBundle = new Dictionary<int, List<ChunkAssetEntry>>();
            foreach (ChunkAssetEntry chunk in App.AssetManager.EnumerateChunks())
            {
                if (replacedChunks.Contains(chunk.Id))
                    continue;
                foreach (int bid in chunk.EnumerateBundles())
                {
                    if (!bundleMap.ContainsKey(bid))
                        continue;
                    if (!chunksByBundle.TryGetValue(bid, out List<ChunkAssetEntry> list))
                        chunksByBundle[bid] = list = new List<ChunkAssetEntry>();
                    list.Add(chunk);
                }
            }

            int totalEbx = 0, totalRes = 0, totalChunks = 0;
            foreach (KeyValuePair<int, int> mapping in bundleMap)
            {
                int oldId = mapping.Key;
                int newId = mapping.Value;
                BundleEntry oldBundle = App.AssetManager.GetBundleEntry(oldId);

                int ebxAdded = 0, localKept = 0, resAdded = 0, chunksAdded = 0;
                List<string> resNames = new List<string>();

                foreach (EbxAssetEntry member in App.AssetManager.EnumerateEbx(oldBundle).ToList())
                {
                    if (copies.Contains(member) || IsInStadiumLocations(member.Name, newFolder, newNameOnly))
                        continue; // made by this run (a finished copy, or one an extension gave up on)

                    bool local = IsInStadiumLocations(member.Name, oldFolder, oldNameOnly);
                    if (local && duplicatedSources.Contains(member) && !requiredOriginals.Contains(member))
                        continue; // replaced by its copy

                    if (member.AddToBundle(newId))
                    {
                        ebxAdded++;
                        if (local)
                        {
                            localKept++;
                            mirrorReport.Add($"{oldBundle.Name}: source asset kept in the new bundle: {member.Name}");
                        }
                    }
                    AddLinkedAssetsToBundle(member, newId);
                }

                if (resByBundle.TryGetValue(oldId, out List<ResAssetEntry> bundleRes))
                {
                    foreach (ResAssetEntry res in bundleRes)
                    {
                        if (res.AddToBundle(newId))
                        {
                            resAdded++;
                            resNames.Add($"{res.Name} (0x{res.ResType:X8} {res.ResRid:X16})");
                        }
                    }
                }

                if (chunksByBundle.TryGetValue(oldId, out List<ChunkAssetEntry> bundleChunks))
                {
                    foreach (ChunkAssetEntry chunk in bundleChunks)
                    {
                        if (chunk.AddToBundle(newId))
                            chunksAdded++;
                    }
                }

                totalEbx += ebxAdded;
                totalRes += resAdded;
                totalChunks += chunksAdded;
                string newBundleName = App.AssetManager.GetBundleEntry(newId)?.Name ?? newId.ToString();
                mirrorReport.Add($"{oldBundle.Name} -> {newBundleName}: +{ebxAdded} ebx ({localKept} source assets), +{resAdded} res, +{chunksAdded} chunks");
                foreach (string rn in resNames)
                    mirrorReport.Add($"    res {rn}");
                App.Logger.Log($"  mirrored {oldBundle.Name}: +{ebxAdded} ebx, +{resAdded} res, +{chunksAdded} chunks");
            }

            mirrorReport.Add($"total: {bundleMap.Count} source bundles, +{totalEbx} ebx, +{totalRes} res, +{totalChunks} chunks; " +
                             $"{droppedForeignBundles} bundle refs of other levels dropped from duplicates");
            App.Logger.Log($"  Bundle mirror: {bundleMap.Count} bundles, +{totalEbx} ebx, +{totalRes} res, +{totalChunks} chunks");
        }

        /// <summary>
        /// The given stadium-local source assets plus every stadium-local asset they reference,
        /// transitively (EBX imports).
        /// </summary>
        private static HashSet<EbxAssetEntry> CollectRequiredOriginals(List<EbxAssetEntry> kept, Dictionary<Guid, EbxAssetEntry> localByFileGuid,
            string oldFolder, string oldNameOnly)
        {
            HashSet<EbxAssetEntry> required = new HashSet<EbxAssetEntry>();
            Queue<EbxAssetEntry> queue = new Queue<EbxAssetEntry>(kept);
            while (queue.Count > 0)
            {
                EbxAssetEntry entry = queue.Dequeue();
                if (entry == null || !required.Add(entry))
                    continue;

                EbxAsset asset;
                try
                {
                    asset = App.AssetManager.GetEbx(entry);
                }
                catch (Exception ex)
                {
                    App.Logger.Log($"  CollectRequiredOriginals {entry.Name}: {ex.Message}");
                    continue;
                }
                if (asset == null)
                    continue;

                foreach (Guid dep in asset.Dependencies)
                {
                    if (!localByFileGuid.TryGetValue(dep, out EbxAssetEntry depEntry))
                        depEntry = App.AssetManager.GetEbxEntry(dep);
                    if (depEntry != null && !required.Contains(depEntry) && IsInStadiumLocations(depEntry.Name, oldFolder, oldNameOnly))
                        queue.Enqueue(depEntry);
                }
            }
            return required;
        }

        /// <summary>
        /// Records the res (and their chunks) of a source asset that the duplicate no
        /// longer uses because its duplication extension copied them: the duplicate's
        /// resource field holds a different RID than the source's. Anything not listed
        /// here is mirrored into the new bundles.
        /// </summary>
        private static void CollectReplacedData(EbxAssetEntry src, EbxAssetEntry dst, HashSet<ulong> replacedRes, HashSet<Guid> replacedChunks)
        {
            try
            {
                EbxAsset srcAsset = App.AssetManager.GetEbx(src);
                EbxAsset dstAsset = App.AssetManager.GetEbx(dst);
                if (srcAsset == null || dstAsset == null)
                    return;

                object srcRoot = srcAsset.RootObject;
                object dstRoot = dstAsset.RootObject;

                foreach (string field in new[] { "MeshSetResource", "Resource", "PhysicsData", "DatabaseResource" })
                {
                    ulong srcRid = GetResourceRefValue(srcRoot, field);
                    ulong dstRid = GetResourceRefValue(dstRoot, field);
                    if (srcRid == 0 || srcRid == dstRid)
                        continue;

                    replacedRes.Add(srcRid);

                    ResAssetEntry res = App.AssetManager.GetResEntry(srcRid);
                    if (res == null)
                        continue;

                    if (res.ResType == (uint)ResourceType.MeshSet)
                    {
                        MeshSet meshSet = App.AssetManager.GetResAs<MeshSet>(res);
                        if (meshSet != null && meshSet.Lods != null)
                        {
                            foreach (MeshSetLod lod in meshSet.Lods)
                            {
                                if (lod.ChunkId != Guid.Empty)
                                    replacedChunks.Add(lod.ChunkId);
                            }
                        }
                    }
                    else if (res.ResType == (uint)ResourceType.Texture)
                    {
                        Texture texture = App.AssetManager.GetResAs<Texture>(res);
                        if (texture != null && texture.ChunkId != Guid.Empty)
                            replacedChunks.Add(texture.ChunkId);
                    }
                    else if (res.ResType == (uint)ResourceType.AtlasTexture)
                    {
                        AtlasTexture atlas = App.AssetManager.GetResAs<AtlasTexture>(res);
                        if (atlas != null && atlas.ChunkId != Guid.Empty)
                            replacedChunks.Add(atlas.ChunkId);
                    }
                }
            }
            catch (Exception ex)
            {
                App.Logger.Log($"  CollectReplacedData {src.Name}: {ex.Message}");
            }
        }

        /// <summary>Reads a ResourceRef property by name; 0 when absent.</summary>
        private static ulong GetResourceRefValue(object obj, string property)
        {
            if (obj == null)
                return 0;
            PropertyInfo pi = obj.GetType().GetProperty(property, BindingFlags.Instance | BindingFlags.Public);
            if (pi == null || pi.PropertyType != typeof(ResourceRef))
                return 0;
            return (ResourceRef)pi.GetValue(obj);
        }

        // ─── Bundle Ref Table entries ────────────────────────────────────────

        /// <summary>
        /// For every duplicated asset that a Bundle Ref Table lists, add an entry for the
        /// duplicate. Bernabeu is the stadium this matters for: tournament_brt lists
        /// content/worlds/components/stadiums/bernabeu/tournament_main and the game keeps
        /// it in its own blueprint bundle tournament_main_tournament_brt (a
        /// BundleRefTableBlueprintBundle EBX in components/stadiums/bernabeu, which this
        /// duplication copies to components/stadiums/&lt;new&gt;/tournament_main_tournament_brt).
        ///
        /// The entry is pointed at a new bundle ref only when this duplication created the
        /// blueprint bundle that ref resolves to ("&lt;ref&gt;_&lt;table&gt;"); otherwise it reuses the
        /// source's ref, as the other Duplicate tools do without a blueprint bundle. (When the
        /// mod is applied, BundleRefTableResource.ApplyModifiedResource points such a fallback
        /// entry at a new ref of the same folder if one was added; the report says which.)
        /// </summary>
        private void InjectBrtEntries(List<Tuple<EbxAssetEntry, EbxAssetEntry>> pairs,
            string oldFolder, string oldNameOnly, string newFolder, string newNameOnly)
        {
            HashSet<string> newBlueprintBundles = new HashSet<string>(pairs
                .Where(p => TypeLibrary.IsSubClassOf(p.Item2.Type, "BundleRefTableBlueprintBundle"))
                .Select(p => NormalizePath(p.Item2.Name).ToLower()));
            foreach (string bb in newBlueprintBundles)
                brtReport.Add($"new BRT blueprint bundle: {bb}");

            List<ResAssetEntry> allBrts = App.AssetManager.EnumerateRes((uint)ResourceType.BundleRefTableResource).ToList();
            brtReport.Add($"{allBrts.Count} BRT res checked");

            int hits = 0;
            foreach (ResAssetEntry brtRes in allBrts)
            {
                BundleRefTableResource brt;
                try
                {
                    brt = App.AssetManager.GetResAs<BundleRefTableResource>(brtRes);
                }
                catch (Exception ex)
                {
                    brtReport.Add($"{brtRes.Name}: could not be read ({ex.Message})");
                    continue;
                }
                if (brt == null)
                    continue;

                // A table's blueprint bundles are named "<ref>_<table name>", e.g.
                // components/stadiums/bernabeu/tournament_main + "_" + tournament_brt.
                string tableName = brtRes.Name.Substring(brtRes.Name.LastIndexOf('/') + 1).ToLower();

                bool modified = false;
                foreach (Tuple<EbxAssetEntry, EbxAssetEntry> pair in pairs)
                {
                    string oldPath = NormalizePath(pair.Item1.Name).ToLower();
                    string newPath = NormalizePath(pair.Item2.Name).ToLower();
                    if (!brt.ContainsAsset(oldPath))
                        continue;

                    hits++;
                    brt.GetSourceBundleIndex(oldPath, out string refName, out string refPath);
                    bool perAssetRef = !string.IsNullOrEmpty(refPath);

                    // Per-asset ref (Name = asset name, Path = folder): its bundle is
                    // "<path>/<name>_<table>", i.e. the new asset path plus a suffix.
                    // Folder ref (Name = folder, Path empty): "<folder>_<table>".
                    string newRefKey = perAssetRef
                        ? newPath
                        : RewriteStadiumName(refName ?? string.Empty, oldFolder, oldNameOnly, newFolder, newNameOnly).ToLower();
                    bool newBundleExists = newBlueprintBundles.Contains(newRefKey + "_" + tableName);

                    bool added = newBundleExists
                        ? brt.DupeAssetToNewBundle(newPath, oldPath, perAssetRef ? null : newRefKey)
                        : brt.DupeAsset(newPath, oldPath);

                    modified |= added;
                    string line = $"{brtRes.Name}: {oldPath} -> {newPath} | source ref name='{refName}' path='{refPath}' " +
                                  $"({(perAssetRef ? "per-asset" : "folder")}) | " +
                                  (newBundleExists ? "new bundle ref" : "source bundle ref (no copied blueprint bundle " + newRefKey + "_" + tableName + "; at mod apply a new ref of the same folder can take it over)") +
                                  (added ? "" : " | already present");
                    brtReport.Add(line);
                    App.Logger.Log("  BRT " + line);
                }

                if (modified)
                {
                    App.AssetManager.ModifyRes(brtRes.ResRid, brt);
                    App.Logger.Log($"  Saved BRT: {brtRes.Name}");
                }
            }

            brtReport.Add($"{hits} duplicated asset(s) found in a BRT");
            App.Logger.Log($"  BRT: {hits} duplicated asset(s) found in a BRT");
        }

        /// <summary>
        /// When a shared EBX asset is placed into a new sublevel bundle, its backing
        /// res/chunk assets must be placed into the same bundle too, otherwise the game
        /// crashes at stream time. This mirrors the BundleEditor plugin's per-type
        /// AddToBundle logic for the single-resource and chunk-backed asset types found
        /// in stadium bundles (NewWaveAsset, FifaPhysicsResourceAsset, Enlighten data,
        /// SoundWave, etc.).
        /// </summary>
        private void AddLinkedAssetsToBundle(EbxAssetEntry member, int bundleId)
        {
            try
            {
                EbxAsset asset = App.AssetManager.GetEbx(member);
                if (asset == null)
                    return;
                dynamic root = asset.RootObject;

                if (TypeLibrary.IsSubClassOf(member.Type, "MeshAsset"))
                {
                    // Meshes need their MeshSet res and every LOD chunk in the same
                    // bundle too, otherwise the geometry can't be streamed at runtime.
                    // This mirrors the BundleEditor plugin's MeshExtension.AddToBundle.
                    ResAssetEntry res = App.AssetManager.GetResEntry(root.MeshSetResource);
                    if (res == null)
                        return; // dummy/placeholder mesh has no backing res

                    res.AddToBundle(bundleId);
                    member.LinkAsset(res);

                    MeshSet meshSet = App.AssetManager.GetResAs<MeshSet>(res);
                    if (meshSet != null && meshSet.Lods != null && meshSet.Lods.Count > 0)
                    {
                        foreach (MeshSetLod lod in meshSet.Lods)
                        {
                            if (lod.ChunkId != Guid.Empty)
                            {
                                ChunkAssetEntry chunk = App.AssetManager.GetChunkEntry(lod.ChunkId);
                                if (chunk != null)
                                {
                                    chunk.AddToBundle(bundleId);
                                    res.LinkAsset(chunk);
                                }
                            }
                        }
                    }
                }
                else if (TypeLibrary.IsSubClassOf(member.Type, "AtlasTextureAsset"))
                {
                    ResAssetEntry res = App.AssetManager.GetResEntry(root.Resource);
                    if (res != null)
                    {
                        res.AddToBundle(bundleId);
                        member.LinkAsset(res);
                        AtlasTexture atlas = App.AssetManager.GetResAs<AtlasTexture>(res);
                        if (atlas != null)
                        {
                            ChunkAssetEntry chunk = App.AssetManager.GetChunkEntry(atlas.ChunkId);
                            if (chunk != null)
                            {
                                chunk.AddToBundle(bundleId);
                                res.LinkAsset(chunk);
                            }
                        }
                    }
                }
                else if (TypeLibrary.IsSubClassOf(member.Type, "TextureBaseAsset"))
                {
                    ResAssetEntry res = App.AssetManager.GetResEntry(root.Resource);
                    if (res != null)
                    {
                        res.AddToBundle(bundleId);
                        member.LinkAsset(res);
                        Texture texture = App.AssetManager.GetResAs<Texture>(res);
                        if (texture != null)
                        {
                            ChunkAssetEntry chunk = App.AssetManager.GetChunkEntry(texture.ChunkId);
                            if (chunk != null)
                            {
                                chunk.AddToBundle(bundleId);
                                chunk.FirstMip = texture.FirstMip;
                                res.LinkAsset(chunk);
                            }
                        }
                    }
                }
                else if (TypeLibrary.IsSubClassOf(member.Type, "NewWaveAsset"))
                {
                    // Backing .res is matched by name (same path as the EBX).
                    ResAssetEntry res = App.AssetManager.GetResEntry(member.Name);
                    if (res != null)
                    {
                        res.AddToBundle(bundleId);
                        member.LinkAsset(res);
                    }
                }
                else if (TypeLibrary.IsSubClassOf(member.Type, "FifaPhysicsResourceAsset"))
                {
                    ResAssetEntry res = App.AssetManager.GetResEntry(root.PhysicsData);
                    if (res != null)
                    {
                        res.AddToBundle(bundleId);
                        member.LinkAsset(res);
                    }
                }
                else if (TypeLibrary.IsSubClassOf(member.Type, "StaticEnlightenData")
                         || TypeLibrary.IsSubClassOf(member.Type, "EnlightenDataAsset"))
                {
                    ResAssetEntry res = App.AssetManager.GetResEntry(root.DatabaseResource);
                    if (res != null)
                    {
                        res.AddToBundle(bundleId);
                        member.LinkAsset(res);
                    }
                }
                else if (TypeLibrary.IsSubClassOf(member.Type, "SvgImage"))
                {
                    ResAssetEntry res = App.AssetManager.GetResEntry(root.Resource);
                    if (res != null)
                    {
                        res.AddToBundle(bundleId);
                        member.LinkAsset(res);
                    }
                }
                else if (TypeLibrary.IsSubClassOf(member.Type, "SoundWaveAsset"))
                {
                    foreach (dynamic soundDataChunk in root.Chunks)
                    {
                        ChunkAssetEntry chunk = App.AssetManager.GetChunkEntry(soundDataChunk.ChunkId);
                        if (chunk != null)
                        {
                            chunk.AddToBundle(bundleId);
                            member.LinkAsset(chunk);
                        }
                    }
                }
                else if (TypeLibrary.IsSubClassOf(member.Type, "MovieTextureBaseAsset"))
                {
                    ChunkAssetEntry chunk = App.AssetManager.GetChunkEntry(root.ChunkGuid);
                    if (chunk != null)
                    {
                        chunk.AddToBundle(bundleId);
                        member.LinkAsset(chunk);
                    }

                    chunk = App.AssetManager.GetChunkEntry(root.SubtitleChunkGuid);
                    if (chunk != null)
                    {
                        chunk.AddToBundle(bundleId);
                        member.LinkAsset(chunk);
                    }
                }
                else if (TypeLibrary.IsSubClassOf(member.Type, "PathfindingBlobAsset"))
                {
                    foreach (dynamic blob in root.Blobs)
                    {
                        ChunkAssetEntry chunk = App.AssetManager.GetChunkEntry(blob.BlobId);
                        if (chunk != null)
                        {
                            chunk.AddToBundle(bundleId);
                            member.LinkAsset(chunk);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                App.Logger.Log($"  Failed to add linked assets for {member.Name}: {ex.Message}");
            }
        }

        // ─── SLM map registration ──────────────────────────────────────────

        /// <summary>
        /// Adds a new SubLevelDefItem to the StadiumCollectionSLM sublevel map
        /// so the game engine knows about the new stadium at runtime.
        /// </summary>
        private static void AddToSLM(string newFolder)
        {
            const string slmPath = "content/common/Logic/DSub/StadiumCollectionSLM";

            EbxAssetEntry slmEntry = App.AssetManager.GetEbxEntry(slmPath);
            if (slmEntry == null)
            {
                App.Logger.Log($"SLM entry not found: {slmPath}");
                return;
            }

            EbxAsset slmAsset = App.AssetManager.GetEbx(slmEntry);
            dynamic root = slmAsset.RootObject;

            // Extract numeric stadium ID from folder name (e.g. "aaaaa_499" -> 499).
            int underscore = newFolder.LastIndexOf('_');
            if (underscore <= 0 || !int.TryParse(newFolder.Substring(underscore + 1), out int stadiumId))
            {
                App.Logger.Log($"Failed to extract numeric stadium ID from folder name: {newFolder}");
                return;
            }

            string subWorldPath = $"content/worlds/stadiums/{newFolder}/{newFolder}";

            // Check if an entry for this stadium already exists.
            foreach (dynamic item in root.SublevelItems)
            {
                string existing = (string)item.SubWorldName;
                if (existing != null && existing.Equals(subWorldPath, StringComparison.OrdinalIgnoreCase))
                {
                    App.Logger.Log($"SLM already has entry: {subWorldPath}");
                    return;
                }
            }

            dynamic newItem = TypeLibrary.CreateObject("SubLevelDefItem");
            newItem.KeyId = stadiumId;
            newItem.SubWorldName = new CString(subWorldPath);
            // SubWorldData stays as Null pointer (the default).

            root.SublevelItems.Add(newItem);
            App.AssetManager.ModifyEbx(slmPath, slmAsset);

            App.Logger.Log($"Added SLM entry: {subWorldPath} (KeyId=0x{stadiumId:X8})");
        }

        // ─── ContentManager registration ─────────────────────────────────────

        /// <summary>
        /// Clones the source stadium's SubWorldReferenceObjectData from the
        /// contentmanager prefab, rewires the BundleName to the new stadium,
        /// and adds it to the prefab so the new stadium appears in-game.
        /// </summary>
        private static void AddToContentManager(string newFolder, string oldFolder)
        {
            const string cmPath = "fifa/fifagame/fifagame_contentmanager_genprefab";

            EbxAssetEntry cmEntry = App.AssetManager.GetEbxEntry(cmPath);
            if (cmEntry == null)
            {
                App.Logger.Log($"ContentManager entry not found: {cmPath}");
                return;
            }

            EbxAsset cmAsset = App.AssetManager.GetEbx(cmEntry);
            dynamic root = cmAsset.RootObject;

            string oldBundleName = $"content/worlds/stadiums/{oldFolder}/{oldFolder}";
            string newBundleName = $"content/worlds/stadiums/{newFolder}/{newFolder}";

            // PrefabBlueprint.Objects is a list of internal PointerRefs, so walk
            // the asset's raw object list and match SubWorldReferenceObjectData by
            // its BundleName field.
            object sourceObj = null;
            foreach (object obj in cmAsset.Objects)
            {
                if (obj.GetType().Name != "SubWorldReferenceObjectData")
                    continue;
                string bn = (string)((dynamic)obj).BundleName;
                if (bn != null && bn.Equals(oldBundleName, StringComparison.OrdinalIgnoreCase))
                {
                    sourceObj = obj;
                    break;
                }
            }

            if (sourceObj == null)
            {
                App.Logger.Log($"ContentManager: source stadium not found in prefab ({oldBundleName})");
                return;
            }

            // Check if an entry for the new stadium already exists.
            foreach (object obj in cmAsset.Objects)
            {
                if (obj.GetType().Name != "SubWorldReferenceObjectData")
                    continue;
                string bn = (string)((dynamic)obj).BundleName;
                if (bn != null && bn.Equals(newBundleName, StringComparison.OrdinalIgnoreCase))
                {
                    App.Logger.Log($"ContentManager already has entry: {newBundleName}");
                    return;
                }
            }

            // Deep-clone the source object by serializing the entire contentmanager
            // asset and deserializing it to obtain a full copy, then extracting the
            // matching object from the copy.  This is simpler than property-by-
            // property cloning and handles all nested structs (Parents, Transform,
            // HeapInfo, etc.) automatically.
            object clone = null;
            try
            {
                AssetClassGuid sourceGuid = ((dynamic)sourceObj).GetInstanceGuid();

                byte[] buf;
                using (EbxBaseWriter writer = EbxBaseWriter.CreateWriter(new MemoryStream(), EbxWriteFlags.DoNotSort))
                {
                    writer.WriteAsset(cmAsset);
                    buf = writer.ToByteArray();
                }

                using (EbxReader reader = EbxReader.CreateReader(new MemoryStream(buf)))
                {
                    EbxAsset copy = reader.ReadAsset<EbxAsset>();
                    foreach (object o in copy.Objects)
                    {
                        if (((dynamic)o).GetInstanceGuid() == sourceGuid)
                        {
                            clone = o;
                            break;
                        }
                    }
                }

                if (clone == null)
                {
                    App.Logger.Log("ContentManager: failed to locate cloned object in deserialized copy");
                    return;
                }

                // Assign a fresh instance GUID so it does not collide with the
                // source object; AssetManager.AddObject will fill in InternalId.
                ((dynamic)clone).SetInstanceGuid(new AssetClassGuid(Guid.NewGuid(), -1));

                // Rewrite the BundleName to point at the new stadium.
                ((dynamic)clone).BundleName = new CString(newBundleName);
            }
            catch (Exception ex)
            {
                App.Logger.Log($"ContentManager: clone failed: {ex.Message}");
                return;
            }

            // Register the cloned object in the contentmanager asset, then append an
            // internal PointerRef to the PrefabBlueprint.Objects array.
            cmAsset.AddObject(clone);
            root.Objects.Add(new PointerRef(clone));

            App.AssetManager.ModifyEbx(cmPath, cmAsset);

            App.Logger.Log($"Added contentmanager entry: {newBundleName}");
        }

        // ─── Path / name utilities ───────────────────────────────────────────

        private static string NormalizePath(string path)
            => (path ?? string.Empty).Replace('\\', '/').Trim('/');

        private static bool IsInStadiumLocations(string assetName, string oldFolder, string oldNameOnly)
        {
            string n = NormalizePath(assetName).ToLower();
            string f = oldFolder.ToLower();
            string o = oldNameOnly.ToLower();

            return n.StartsWith(L1_PREFIX + f + "/") || n.Equals(L1_PREFIX + f)
                || n.StartsWith(L2_PREFIX + o + "/") || n.Equals(L2_PREFIX + o)
                || n.StartsWith(L3_PREFIX + o + "/") || n.Equals(L3_PREFIX + o);
        }

        /// <summary>
        /// The pitch layer (stadiums/&lt;folder&gt;/pitch, a WorldPartData or LayerData), the
        /// stadium's pitch blueprint and its mesh (components/stadiums/&lt;name&gt;/pitch and
        /// pitch_mesh). 88 stadiums have the blueprint; on the 27 that place
        /// pitch_grass_prefab_merged the playing surface is part of that mesh.
        /// </summary>
        private static bool IsPitchAsset(string assetName, string oldFolder, string oldNameOnly)
        {
            string n = NormalizePath(assetName).ToLower();
            string f = oldFolder.ToLower();
            string o = oldNameOnly.ToLower();
            return n == L1_PREFIX + f + "/pitch"
                || n == L2_PREFIX + o + "/pitch"
                || n == L2_PREFIX + o + "/pitch_mesh";
        }

        private static bool IsStadiumLocalBundle(BundleEntry bundle, string oldFolder, string oldNameOnly)
        {
            if (bundle == null || string.IsNullOrEmpty(bundle.Name))
                return false;

            string n = bundle.Name.ToLower();
            if (n.StartsWith("win32/"))
                n = n.Substring(6);

            string f = oldFolder.ToLower();
            string o = oldNameOnly.ToLower();

            return n.StartsWith(L1_PREFIX + f + "/") || n.Equals(L1_PREFIX + f)
                || n.StartsWith(L2_PREFIX + o + "/") || n.Equals(L2_PREFIX + o)
                || n.StartsWith(L3_PREFIX + o + "/") || n.Equals(L3_PREFIX + o);
        }

        /// <summary>
        /// Replace the folder token ("allianz_137") and the name token ("allianz"),
        /// case-insensitively, in ONE pass with the longer token tried first. Two
        /// sequential passes rewrote text the first pass had just written whenever the
        /// new name contains the old one: bernabeu_2 -> bernabeu2_903 became
        /// "bernabeu22_903" once the second pass replaced "bernabeu" again.
        /// </summary>
        private static string RewriteStadiumName(string value, string oldFolder, string oldNameOnly, string newFolder, string newNameOnly)
        {
            if (string.IsNullOrEmpty(value))
                return value;

            bool folderChanges = !string.IsNullOrEmpty(oldFolder) && !string.Equals(oldFolder, newFolder, StringComparison.OrdinalIgnoreCase);
            bool nameChanges = !string.IsNullOrEmpty(oldNameOnly) && !string.Equals(oldNameOnly, newNameOnly, StringComparison.OrdinalIgnoreCase);
            if (!folderChanges && !nameChanges)
                return value;

            List<string> tokens = new List<string>();
            if (folderChanges)
                tokens.Add(oldFolder);
            if (nameChanges)
                tokens.Add(oldNameOnly);
            string pattern = string.Join("|", tokens.OrderByDescending(t => t.Length).Select(t => Regex.Escape(t)));

            return Regex.Replace(value, pattern, m =>
                folderChanges && m.Value.Equals(oldFolder, StringComparison.OrdinalIgnoreCase) ? newFolder : newNameOnly,
                RegexOptions.IgnoreCase);
        }

        private static string RewriteStadiumString(string value, string oldFolder, string oldNameOnly, string newFolder, string newNameOnly)
        {
            if (string.IsNullOrEmpty(value))
                return value;

            string lower = value.ToLower();
            bool relevant = lower.Contains(L1_PREFIX + oldFolder.ToLower())
                            || lower.Contains(L2_PREFIX + oldNameOnly.ToLower())
                            || lower.Contains(L3_PREFIX + oldNameOnly.ToLower());

            return relevant ? RewriteStadiumName(value, oldFolder, oldNameOnly, newFolder, newNameOnly) : value;
        }
    }
}
