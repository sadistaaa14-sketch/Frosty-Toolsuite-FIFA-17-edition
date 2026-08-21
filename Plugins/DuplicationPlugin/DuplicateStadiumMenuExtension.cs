using AtlasTexturePlugin;
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

            FrostyTaskWindow.Show("Duplicating Stadium", "", (task) =>
            {
                try
                {
                    DuplicateStadium(task, oldFolder, oldNameOnly, newFolder, newNameOnly);
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

        private void DuplicateStadium(FrostyTaskWindow task, string oldFolder, string oldNameOnly, string newFolder, string newNameOnly)
        {
            pointerDebug.Clear();
            verificationReport.Clear();
            App.Logger.Log($"Duplicating stadium: folder {oldFolder} -> {newFolder}, name {oldNameOnly} -> {newNameOnly}");

            // 1. Enumerate every source asset in the three stadium locations.
            List<EbxAssetEntry> sourceAssets = new List<EbxAssetEntry>();
            foreach (EbxAssetEntry entry in App.AssetManager.EnumerateEbx())
            {
                if (IsInStadiumLocations(entry.Name, oldFolder, oldNameOnly))
                    sourceAssets.Add(entry);
            }
            App.Logger.Log($"  Found {sourceAssets.Count} source assets");

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
                    string newName = RewriteStadiumName(src.Name, oldFolder, oldNameOnly, newFolder, newNameOnly);
                    EbxAssetEntry newEntry = DuplicateStadiumAsset(src, newName);

                    if (newEntry == null)
                    {
                        App.Logger.Log($"  FAILED to duplicate {src.Name}");
                        failures.Add($"{src.Name} ({src.Type}) -> null");
                        continue;
                    }

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

            // 5. Shared assets (outside the three locations) that lived inside the
            //    original sublevel bundles must also be added to the new bundles.
            task.Update("Adding shared assets to new bundles...");
            AddSharedAssetsToNewBundles(pairs, oldFolder, oldNameOnly);

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

            foreach (int bundleId in entry.AddedBundles)
            {
                BundleEntry bundle = App.AssetManager.GetBundleEntry(bundleId);
                if (bundle != null && IsStadiumLocalBundle(bundle, oldFolder, oldNameOnly))
                {
                    int newBundleId = GetOrCreateNewBundle(bundle, oldFolder, oldNameOnly, newFolder, newNameOnly);
                    if (newBundleId >= 0)
                    {
                        remapped.Add(newBundleId);
                        changed = true;
                        continue;
                    }
                }
                remapped.Add(bundleId);
            }

            if (!changed)
                return;

            entry.AddedBundles.Clear();
            foreach (int bundleId in remapped.Distinct())
            {
                if (!entry.Bundles.Contains(bundleId) && !entry.AddedBundles.Contains(bundleId))
                    entry.AddedBundles.Add(bundleId);
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
        /// Copies shared assets that lived in the original sublevel bundles into the
        /// corresponding new sublevel bundles, so streaming the new stadium can find
        /// everything it needs.
        /// </summary>
        private void AddSharedAssetsToNewBundles(List<Tuple<EbxAssetEntry, EbxAssetEntry>> pairs, string oldFolder, string oldNameOnly)
        {
            foreach (Tuple<EbxAssetEntry, EbxAssetEntry> pair in pairs)
            {
                EbxAssetEntry src = pair.Item1;
                EbxAssetEntry dst = pair.Item2;

                if (!IsSubWorldData(src.Type))
                    continue;
                if (dst.AddedBundles == null || dst.AddedBundles.Count == 0)
                    continue;

                int newBundleId = dst.AddedBundles[0];

                foreach (BundleEntry oldBundle in App.AssetManager.EnumerateBundles())
                {
                    if (oldBundle.Blueprint == null || oldBundle.Blueprint.Guid != src.Guid)
                        continue;

                    foreach (EbxAssetEntry member in App.AssetManager.EnumerateEbx(oldBundle))
                    {
                        if (ReferenceEquals(member, src))
                            continue;
                        if (IsInStadiumLocations(member.Name, oldFolder, oldNameOnly))
                            continue; // already duplicated and remapped

                        if (member.AddToBundle(newBundleId))
                        {
                            App.Logger.Log($"  shared asset {member.Name} -> new bundle");
                            AddLinkedAssetsToBundle(member, newBundleId);
                        }
                    }
                }
            }
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
        /// Replace the longer token first ("allianz_137") then the shorter one
        /// ("allianz"), case-insensitively.
        /// </summary>
        private static string RewriteStadiumName(string value, string oldFolder, string oldNameOnly, string newFolder, string newNameOnly)
        {
            string result = ReplaceToken(value, oldFolder, newFolder);
            result = ReplaceToken(result, oldNameOnly, newNameOnly);
            return result;
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

        private static string ReplaceToken(string input, string oldToken, string newToken)
        {
            if (string.IsNullOrEmpty(oldToken) || string.Equals(oldToken, newToken, StringComparison.OrdinalIgnoreCase))
                return input;

            return Regex.Replace(input, Regex.Escape(oldToken), newToken.Replace("$", "$$"), RegexOptions.IgnoreCase);
        }
    }
}
