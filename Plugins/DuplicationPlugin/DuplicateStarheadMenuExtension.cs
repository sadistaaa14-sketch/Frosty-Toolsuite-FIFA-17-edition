using BundleRefTablePlugin;
using DuplicationPlugin.Windows;
using Frosty.Controls;
using Frosty.Core;
using Frosty.Core.Viewport;
using Frosty.Core.Windows;
using FrostySdk;
using FrostySdk.Ebx;
using FrostySdk.IO;
using FrostySdk.Managers;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Windows.Media;

namespace DuplicationPlugin
{
    public class DuplicateStarheadMenuExtension : MenuExtension
    {
        private readonly Dictionary<string, DuplicationTool.DuplicateAssetExtension> extensions
            = new Dictionary<string, DuplicationTool.DuplicateAssetExtension>();

        private static readonly HashSet<string> BRT_TYPES = new HashSet<string>
        {
            "ObjectBlueprint",
            "ClothObjectBlueprint",
            "TextureAsset",
            "PSDWrapListAsset"
        };

        public DuplicateStarheadMenuExtension()
        {
            foreach (var type in Assembly.GetExecutingAssembly().GetTypes())
            {
                if (type.IsSubclassOf(typeof(DuplicationTool.DuplicateAssetExtension)))
                {
                    var ext = (DuplicationTool.DuplicateAssetExtension)Activator.CreateInstance(type);
                    if (ext.AssetType != null)
                        extensions[ext.AssetType] = ext;
                }
            }
            extensions["null"] = new DuplicationTool.DuplicateAssetExtension();
        }

        public override string TopLevelMenuName => "Tools";
        public override string SubLevelMenuName => null;
        public override string MenuItemName => "Duplicate Starhead";
        public override ImageSource Icon => null;

        public override RelayCommand MenuItemClicked => new RelayCommand((o) =>
        {
            EbxAssetEntry entry = App.SelectedAsset as EbxAssetEntry;
            if (entry == null)
            {
                FrostyMessageBox.Show(
                    "No asset selected. Select any asset inside the player head folder you want to duplicate.",
                    "Starhead Duplicator");
                return;
            }

            string sourceFolder = entry.Path.Replace('\\', '/');
            if (string.IsNullOrEmpty(sourceFolder))
            {
                FrostyMessageBox.Show("Selected asset has no folder path.", "Starhead Duplicator");
                return;
            }

            if (sourceFolder.EndsWith("_launch_starhead_brt", StringComparison.OrdinalIgnoreCase))
                sourceFolder = sourceFolder.Substring(0, sourceFolder.Length - "_launch_starhead_brt".Length);
            else if (sourceFolder.EndsWith("_starhead_brt", StringComparison.OrdinalIgnoreCase))
                sourceFolder = sourceFolder.Substring(0, sourceFolder.Length - "_starhead_brt".Length);

            string sourcePlayerName = sourceFolder.Substring(sourceFolder.LastIndexOf('/') + 1);
            string oldId = ExtractId(sourcePlayerName);
            if (string.IsNullOrEmpty(oldId))
            {
                FrostyMessageBox.Show(
                    "Could not extract a numeric player ID from folder name '" + sourcePlayerName + "'.\n" +
                    "Expected format: firstname_lastname_123456",
                    "Starhead Duplicator");
                return;
            }

            DuplicateStarheadWindow win = new DuplicateStarheadWindow(sourceFolder);
            if (win.ShowDialog() != true)
                return;

            string newPlayerName = win.NewPlayerName;
            string destPath = win.DestinationPath;

            FrostyTaskWindow.Show("Duplicating Starhead", "", (task) =>
            {
                try
                {
                    if (!MeshVariationDb.IsLoaded)
                        MeshVariationDb.LoadVariations(task);

                    DuplicateStarhead(task, sourceFolder, newPlayerName, destPath);
                }
                catch (Exception ex)
                {
                    App.Logger.Log("Error duplicating starhead: " + ex.ToString());
                }
            });

            App.EditorWindow.DataExplorer.RefreshAll();
        });

        public static string ExtractId(string playerFolderName)
        {
            int last = playerFolderName.LastIndexOf('_');
            if (last < 0) return null;
            string candidate = playerFolderName.Substring(last + 1);
            int dummy;
            return int.TryParse(candidate, out dummy) ? candidate : null;
        }

        private EbxAssetEntry DuplicateWithExtension(EbxAssetEntry entry, string newName)
        {
            try
            {
                string key = "null";
                foreach (string typekey in extensions.Keys)
                {
                    if (typekey != "null" && TypeLibrary.IsSubClassOf(entry.Type, typekey))
                    {
                        key = typekey;
                        break;
                    }
                }
                return extensions[key].DuplicateAsset(entry, newName, false, null);
            }
            catch (Exception ex)
            {
                App.Logger.Log("Failed to duplicate " + entry.Name + ": " + ex.Message);
                return null;
            }
        }

        /// <summary>
        /// Duplicates the EAClothEntityData .res referenced by a ClothObjectBlueprint's
        /// nested ClothEntityData and points the duplicated blueprint at the new res.
        /// </summary>
        private static void DuplicateClothEntityResource(EbxAssetEntry newEntry)
        {
            try
            {
                EbxAsset newAsset = App.AssetManager.GetEbx(newEntry);
                dynamic root = newAsset.RootObject;
                dynamic entity = root.Object.Internal;

                // ResRid of the source EAClothEntityData res
                ResAssetEntry resEntry = App.AssetManager.GetResEntry(entity.ClothEntityResource);
                if (resEntry == null)
                {
                    App.Logger.Log("  " + newEntry.Filename + ": no ClothEntityResource res found; skipping");
                    return;
                }

                ResAssetEntry newResEntry = DuplicationTool.DuplicateRes(resEntry, newEntry.Name, ResourceType.EAClothEntityData);
                if (newResEntry == null)
                    return;

                entity.ClothEntityResource = newResEntry.ResRid;
                newEntry.LinkAsset(newResEntry);
                App.AssetManager.ModifyEbx(newEntry.Name, newAsset);
                App.Logger.Log("  " + newEntry.Filename + ": ClothEntityResource res -> " + newResEntry.Name);
            }
            catch (Exception ex)
            {
                App.Logger.Log("  " + newEntry.Filename + ": Failed to duplicate ClothEntityResource res: " + ex.Message);
            }
        }

        private static PointerRef MakeRef(EbxAsset targetAsset)
        {
            EbxImportReference r = new EbxImportReference();
            r.FileGuid = targetAsset.FileGuid;
            r.ClassGuid = targetAsset.RootInstanceGuid;
            return new PointerRef(r);
        }

        private static PointerRef MakeRef(EbxAsset targetAsset, Guid classGuid)
        {
            EbxImportReference r = new EbxImportReference();
            r.FileGuid = targetAsset.FileGuid;
            r.ClassGuid = classGuid;
            return new PointerRef(r);
        }

        /// <summary>
        /// Duplicates an entire starhead folder: every ebx asset directly inside
        /// <paramref name="sourceFolder"/>, plus its "_starhead_brt"/"_launch_starhead_brt"
        /// sibling folder if one exists, cross-reference fixups (MVDB/blueprint pointers)
        /// and BRT injection.
        ///
        /// Public (rather than private) so it can be invoked programmatically by other
        /// tooling -- e.g. BulkImportStarheadsMenuExtension -- and not only from the
        /// interactive "Duplicate Starhead" menu item above. Behaviour is identical either
        /// way; this is the same method the menu item calls.
        /// </summary>
        public void DuplicateStarhead(FrostyTaskWindow task, string sourceFolder,
            string newPlayerName, string destPath)
        {
            string sourcePlayerName = sourceFolder.Substring(sourceFolder.LastIndexOf('/') + 1);
            string newFolder = destPath.TrimEnd('/') + "/" + newPlayerName;

            string oldId = ExtractId(sourcePlayerName);
            string newId = ExtractId(newPlayerName);

            if (string.IsNullOrEmpty(oldId) || string.IsNullOrEmpty(newId))
            {
                App.Logger.Log("Could not extract player IDs. Aborting.");
                return;
            }

            App.Logger.Log("Source: " + sourcePlayerName + " (ID " + oldId + ")");
            App.Logger.Log("Target: " + newPlayerName + " (ID " + newId + ")");

            // ── Phase 1: Enumerate ──────────────────────────────────────────────
            task.Update("Finding source assets...");

            // Check both possible BRT subfolder suffixes
            string sourceBrtFolder1 = sourceFolder + "_starhead_brt";
            string sourceBrtFolder2 = sourceFolder + "_launch_starhead_brt";

            List<EbxAssetEntry> mainAssets = new List<EbxAssetEntry>();
            List<EbxAssetEntry> brtAssets = new List<EbxAssetEntry>();   // contents of the _starhead_brt folder (mesh variation DB)
            EbxAssetEntry sourceBrtEbxl = null;   // the BundleRefTableBlueprintBundle EBX
            string sourceBrtFolder = null;

            foreach (EbxAssetEntry e in App.AssetManager.EnumerateEbx())
            {
                string path = e.Path.Replace('\\', '/');
                string name = e.Name.Replace('\\', '/');
                if (path.Equals(sourceFolder, StringComparison.OrdinalIgnoreCase))
                {
                    mainAssets.Add(e);
                }
                else if (path.Equals(sourceBrtFolder1, StringComparison.OrdinalIgnoreCase))
                {
                    brtAssets.Add(e);
                    sourceBrtFolder = sourceBrtFolder1;
                }
                else if (name.Equals(sourceBrtFolder1, StringComparison.OrdinalIgnoreCase))
                {
                    sourceBrtEbxl = e;
                    sourceBrtFolder = sourceBrtFolder1;
                }
                else if (path.Equals(sourceBrtFolder2, StringComparison.OrdinalIgnoreCase))
                {
                    brtAssets.Add(e);
                    sourceBrtFolder = sourceBrtFolder2;
                }
                else if (name.Equals(sourceBrtFolder2, StringComparison.OrdinalIgnoreCase))
                {
                    sourceBrtEbxl = e;
                    sourceBrtFolder = sourceBrtFolder2;
                }
            }

            // Derive the BRT suffix used and apply it to the new folder
            string brtSuffix = "_starhead_brt";
            if (sourceBrtFolder != null && sourceBrtFolder.Equals(sourceBrtFolder2, StringComparison.OrdinalIgnoreCase))
                brtSuffix = "_launch_starhead_brt";
            string newBrtFolder = newFolder + brtSuffix;

            App.Logger.Log("Found " + mainAssets.Count + " main assets, " + brtAssets.Count + " BRT-folder assets, " +
                (sourceBrtEbxl != null ? "1 BRT blueprint bundle" : "no BRT blueprint bundle"));

            if (mainAssets.Count == 0)
            {
                App.Logger.Log("No assets found in: " + sourceFolder);
                return;
            }

            // ── Phase 2: Duplicate ──────────────────────────────────────────────
            Dictionary<Guid, EbxAssetEntry> oldToNew = new Dictionary<Guid, EbxAssetEntry>();
            Dictionary<string, string> oldToNewNames = new Dictionary<string, string>();
            List<EbxAssetEntry> allNew = new List<EbxAssetEntry>();

            // Per-asset blueprint bundles (PSD). Some players carry an extra
            // BundleRefTableBlueprintBundle named "<asset>_psd_brt" (or
            // "_launch_psd_brt") inside the head folder; the asset it wraps must
            // also live in that extra bundle.
            List<EbxAssetEntry> psdBbs = new List<EbxAssetEntry>();
            Dictionary<EbxAssetEntry, int> psdBundleIdByBb = new Dictionary<EbxAssetEntry, int>();
            Dictionary<EbxAssetEntry, int> psdSrcBundleIdByBb = new Dictionary<EbxAssetEntry, int>();

            int current = 0;
            int total = mainAssets.Count + brtAssets.Count + (sourceBrtEbxl != null ? 1 : 0);

            foreach (EbxAssetEntry src in mainAssets)
            {
                current++;
                string newFilename = src.Filename.Replace(oldId, newId);
                string newName = newFolder + "/" + newFilename;
                task.Update("Duplicating " + src.Filename + " (" + current + "/" + total + ")...");

                EbxAssetEntry newEntry = DuplicateWithExtension(src, newName);
                if (newEntry != null)
                {
                    oldToNew[src.Guid] = newEntry;
                    oldToNewNames[src.Name] = newEntry.Name;
                    allNew.Add(newEntry);
                    App.Logger.Log("  Duplicated: " + src.Name + " -> " + newEntry.Name);

                    // A ClothObjectBlueprint's nested ClothEntityData holds a
                    // ClothEntityResource (ResRid) pointing at an EAClothEntityData .res.
                    // Base duplication only copies the EBX, so this res is left behind and
                    // the duplicated blueprint still references the source res. Duplicate
                    // the res here (before Phase 2.5 moves linked assets into the new
                    // bundle) and point the field at the new ResRid.
                    if (newEntry.Type == "ClothObjectBlueprint")
                        DuplicateClothEntityResource(newEntry);

                    // PSD blueprint bundle: "<asset>_psd_brt" / "_launch_psd_brt" EBX.
                    if (IsPsdBb(src))
                    {
                        int psdBundleId = newEntry.AddedBundles.Count > 0 ? newEntry.AddedBundles[0] : -1;
                        psdBbs.Add(newEntry);
                        psdBundleIdByBb[newEntry] = psdBundleId;
                        psdSrcBundleIdByBb[newEntry] = GetSourceBundleIdForBb(src);

                        DuplicationTool.FixBlueprintBundleName(newEntry, newName);
                        App.Logger.Log("  PSD blueprint bundle: " + src.Name + " -> " + newEntry.Name +
                            " (bundle id " + psdBundleId + ", source bundle id " + psdSrcBundleIdByBb[newEntry] + ")");
                    }
                }
            }

            // Duplicate the contents of the _starhead_brt folder (the mesh variation DB)
            foreach (EbxAssetEntry src in brtAssets)
            {
                current++;
                string newFilename = src.Filename.Replace(oldId, newId);
                string newName = newBrtFolder + "/" + newFilename;
                task.Update("Duplicating " + src.Filename + " (" + current + "/" + total + ")...");

                EbxAssetEntry newEntry = DuplicateWithExtension(src, newName);
                if (newEntry != null)
                {
                    oldToNew[src.Guid] = newEntry;
                    oldToNewNames[src.Name] = newEntry.Name;
                    allNew.Add(newEntry);
                    App.Logger.Log("  Duplicated: " + src.Name + " -> " + newEntry.Name);
                }
            }

            // Duplicate the (empty) BundleRefTableBlueprintBundle EBX. This creates a
            // brand new blueprint bundle via BlueprintBundleExtension, whose id ends up
            // in newBbEntry.AddedBundles[0]. The BB lives directly in the player's parent
            // folder (e.g. player_158000), named "<player>_starhead_brt".
            EbxAssetEntry newBbEntry = null;
            int newBundleId = -1;
            if (sourceBrtEbxl != null)
            {
                current++;
                string newBbName = newBrtFolder;
                task.Update("Duplicating " + sourceBrtEbxl.Filename + " (" + current + "/" + total + ")...");

                newBbEntry = DuplicateWithExtension(sourceBrtEbxl, newBbName);
                if (newBbEntry != null)
                {
                    allNew.Add(newBbEntry);
                    if (newBbEntry.AddedBundles.Count > 0)
                        newBundleId = newBbEntry.AddedBundles[0];

                    // The BB EBX holds a nested BundleRefTableBlueprint (reached through
                    // the root's Blueprint pointer) whose Name is "<root name>_blueprint".
                    // Base duplication only renames the root object, so this nested name
                    // still carries the source player and the new bundle would be
                    // registered under Messi's blueprint name. Fix it to the new player.
                    EbxAsset newBbAsset = App.AssetManager.GetEbx(newBbEntry);
                    dynamic newBbRoot = newBbAsset.RootObject;
                    if (newBbRoot.Blueprint.Type == PointerRefType.Internal && newBbRoot.Blueprint.Internal != null)
                    {
                        dynamic bp = newBbRoot.Blueprint.Internal;
                        bp.Name = newBbName + "_blueprint";
                        App.AssetManager.ModifyEbx(newBbEntry.Name, newBbAsset);
                        App.Logger.Log("  Blueprint name -> " + bp.Name);
                    }

                    App.Logger.Log("  Duplicated: " + sourceBrtEbxl.Name + " -> " + newBbEntry.Name +
                        " (bundle id " + newBundleId + ")");
                }
            }

            // ── Phase 2.5: Move duplicates into the new blueprint bundle ────────
            if (newBundleId >= 0)
            {
                task.Update("Moving duplicated assets into the new bundle...");
                MoveAssetsToBundle(allNew, newBundleId);
            }

            // PSD bundles: put each psd BB back in its own bundle (MoveAssetsToBundle
            // moved it into the starhead bundle) and replicate the FULL membership of the
            // source psd bundle. The source psd bundle contains more than just the wrapped
            // asset -- e.g. Ronaldo's hair_20801_0_0_psdwrap bundle also physically
            // contains hair_20801_0_0_mesh, even though the mesh has no BRT lookup. Every
            // duplicated asset whose source lived in the source psd bundle must therefore
            // also live in the new psd bundle (it ends up in BOTH the starhead and psd bundles).
            foreach (EbxAssetEntry bb in psdBbs)
            {
                int psdBundleId = psdBundleIdByBb[bb];
                if (psdBundleId < 0)
                    continue;

                bb.AddedBundles.Clear();
                bb.AddedBundles.Add(psdBundleId);

                int srcPsdBundleId = psdSrcBundleIdByBb[bb];
                if (srcPsdBundleId < 0)
                {
                    App.Logger.Log("  PSD bundle " + bb.Name + ": source bundle id unknown; skipping membership copy");
                    continue;
                }

                int copied = 0;
                foreach (EbxAssetEntry src in mainAssets)
                {
                    if (IsPsdBb(src))
                        continue; // the BB itself is handled above

                    if (!src.Bundles.Contains(srcPsdBundleId))
                        continue;

                    if (!oldToNew.TryGetValue(src.Guid, out EbxAssetEntry newEntry))
                        continue;

                    AddToBundleRecursive(newEntry, psdBundleId, new HashSet<AssetEntry>());
                    App.Logger.Log("  " + newEntry.Filename + ": added to psd bundle " + psdBundleId);
                    copied++;
                }

                if (copied == 0)
                    App.Logger.Log("  PSD bundle " + bb.Name + ": no source assets found in source bundle " + srcPsdBundleId);
            }

            // ── Phase 3: Fix references ─────────────────────────────────────────
            task.Update("Fixing cross-references...");
            FixCrossReferences(oldToNew, allNew);

            // ── Phase 4: BRT injection ──────────────────────────────────────────
            if (!Config.Get<bool>("SkipBrtAdd", false))
            {
                task.Update("Updating BRT entries...");
                string newBundleRefName = (newBbEntry != null) ? newFolder.ToLower() : null;
                InjectBrtEntries(mainAssets, oldToNewNames, newBundleRefName);
            }

            App.Logger.Log("Starhead duplication complete (" + allNew.Count + " assets)");
        }

        // ─── BRT Injection ──────────────────────────────────────────────────────

        private void InjectBrtEntries(List<EbxAssetEntry> sourceAssets,
            Dictionary<string, string> oldToNewNames,
            string newBundleRefName)
        {
            bool useNewBundle = !string.IsNullOrEmpty(newBundleRefName);
            Dictionary<string, string> brtPairs = new Dictionary<string, string>();
            foreach (EbxAssetEntry src in sourceAssets)
            {
                if (BRT_TYPES.Contains(src.Type) && oldToNewNames.ContainsKey(src.Name))
                {
                    brtPairs[src.Name.ToLower()] = oldToNewNames[src.Name].ToLower();
                }
            }

            if (brtPairs.Count == 0)
            {
                App.Logger.Log("  No BRT-eligible assets to inject.");
                return;
            }

            App.Logger.Log("  BRT-eligible assets: " + brtPairs.Count);

            List<ResAssetEntry> allBrts = App.AssetManager.EnumerateRes((uint)ResourceType.BundleRefTableResource).ToList();
            App.Logger.Log("  Found " + allBrts.Count + " BRT res entries total");

            foreach (ResAssetEntry brtRes in allBrts)
            {
                BundleRefTableResource brt = App.AssetManager.GetResAs<BundleRefTableResource>(brtRes);
                if (brt == null)
                    continue;

                bool brtModified = false;

                foreach (KeyValuePair<string, string> kvp in brtPairs)
                {
                    if (brt.ContainsAsset(kvp.Key))
                    {
                        bool added = useNewBundle
                            ? brt.DupeAssetToNewBundle(kvp.Value, kvp.Key, newBundleRefName)
                            : brt.DupeAsset(kvp.Value, kvp.Key);

                        if (added)
                        {
                            brtModified = true;
                            App.Logger.Log("  BRT " + brtRes.Filename + ": " + kvp.Value);
                        }
                    }
                }

                if (brtModified)
                {
                    App.AssetManager.ModifyRes(brtRes.ResRid, brt);
                    App.Logger.Log("  Saved BRT: " + brtRes.Name);
                }
            }
        }

        // ─── Bundle re-pointing ────────────────────────────────────────────────

        private void MoveAssetsToBundle(List<EbxAssetEntry> newEntries, int newBundleId)
        {
            HashSet<AssetEntry> visited = new HashSet<AssetEntry>();
            foreach (EbxAssetEntry e in newEntries)
                MoveToBundleRecursive(e, newBundleId, visited);
        }

        private void MoveToBundleRecursive(AssetEntry entry, int newBundleId, HashSet<AssetEntry> visited)
        {
            if (entry == null || !visited.Add(entry))
                return;

            if (BundleRefTableResource.A_B_TEST_LOOKUPS_AT_SOURCE_REF)
            {
                // TEMPORARY A/B TEST: keep the source bundles AND add the new bundle,
                // so the duplicated assets exist in both.
                if (!entry.AddedBundles.Contains(newBundleId))
                    entry.AddedBundles.Add(newBundleId);
            }
            else
            {
                entry.AddedBundles.Clear();
                entry.AddedBundles.Add(newBundleId);
            }

            foreach (AssetEntry linked in entry.LinkedAssets)
                MoveToBundleRecursive(linked, newBundleId, visited);
        }

        private void AddToBundleRecursive(AssetEntry entry, int bundleId, HashSet<AssetEntry> visited)
        {
            if (entry == null || !visited.Add(entry))
                return;

            if (!entry.AddedBundles.Contains(bundleId))
                entry.AddedBundles.Add(bundleId);

            foreach (AssetEntry linked in entry.LinkedAssets)
                AddToBundleRecursive(linked, bundleId, visited);
        }

        private static bool IsPsdBb(EbxAssetEntry e)
        {
            return e.Name.EndsWith("_launch_psd_brt", StringComparison.OrdinalIgnoreCase)
                || e.Name.EndsWith("_psd_brt", StringComparison.OrdinalIgnoreCase);
        }

        private static int GetSourceBundleIdForBb(EbxAssetEntry sourceBb)
        {
            foreach (BundleEntry be in App.AssetManager.EnumerateBundles())
            {
                if (be.Blueprint != null && be.Blueprint.Guid == sourceBb.Guid)
                    return App.AssetManager.GetBundleId(be);
            }

            return sourceBb.Bundles.Count > 0 ? sourceBb.Bundles[0] : -1;
        }

        // ─── Cross-Reference Fixup ──────────────────────────────────────────────

        private void FixCrossReferences(Dictionary<Guid, EbxAssetEntry> oldToNew,
            List<EbxAssetEntry> newAssets)
        {
            foreach (EbxAssetEntry newEntry in newAssets)
            {
                try
                {
                    if (newEntry.Type == "TextureAsset"
                        || newEntry.Type == "SkinnedMeshAsset"
                        || newEntry.Type == "ClothAsset"
                        || newEntry.Type == "PSDWrapListAsset")
                    {
                        continue;
                    }

                    if (newEntry.Type == "MeshVariationDatabase")
                        FixMVDB(newEntry, oldToNew);
                    else if (newEntry.Type == "ObjectBlueprint" || newEntry.Type == "ClothObjectBlueprint")
                        FixBlueprint(newEntry, oldToNew);
                }
                catch (Exception ex)
                {
                    App.Logger.Log("Failed to fix refs in " + newEntry.Name + ": " + ex.Message);
                }
            }
        }

        private void FixBlueprint(EbxAssetEntry newEntry,
            Dictionary<Guid, EbxAssetEntry> oldToNew)
        {
            EbxAsset ebx = App.AssetManager.GetEbx(newEntry);
            dynamic root = ebx.RootObject;
            dynamic entity = root.Object.Internal;
            bool modified = false;

            if (entity.Mesh.Type == PointerRefType.External)
            {
                Guid oldGuid = entity.Mesh.External.FileGuid;
                if (oldToNew.ContainsKey(oldGuid))
                {
                    EbxAsset newMesh = App.AssetManager.GetEbx(oldToNew[oldGuid]);
                    entity.Mesh = MakeRef(newMesh);
                    modified = true;
                    App.Logger.Log("  " + newEntry.Filename + ": Mesh -> " + oldToNew[oldGuid].Name);
                }
            }

            if (newEntry.Type == "ClothObjectBlueprint")
            {
                try
                {
                    if (entity.Cloth.Type == PointerRefType.External)
                    {
                        Guid oldGuid = entity.Cloth.External.FileGuid;
                        if (oldToNew.ContainsKey(oldGuid))
                        {
                            EbxAsset newCloth = App.AssetManager.GetEbx(oldToNew[oldGuid]);
                            entity.Cloth = MakeRef(newCloth);
                            modified = true;
                            App.Logger.Log("  " + newEntry.Filename + ": Cloth -> " + oldToNew[oldGuid].Name);
                        }
                    }
                }
                catch { }

                try
                {
                    dynamic extraLods = entity.ExtraLodMeshes;
                    for (int i = 0; i < extraLods.Count; i++)
                    {
                        PointerRef lodRef = extraLods[i];
                        if (lodRef.Type == PointerRefType.External)
                        {
                            Guid oldGuid = lodRef.External.FileGuid;
                            if (oldToNew.ContainsKey(oldGuid))
                            {
                                EbxAsset newLod = App.AssetManager.GetEbx(oldToNew[oldGuid]);
                                extraLods[i] = MakeRef(newLod);
                                modified = true;
                                App.Logger.Log("  " + newEntry.Filename + ": ExtraLodMeshes[" + i + "] -> " + oldToNew[oldGuid].Name);
                            }
                        }
                    }
                }
                catch { }

            }

            if (modified)
            {
                ebx.Update();
                App.AssetManager.ModifyEbx(newEntry.Name, ebx);
            }
        }

        private void FixMVDB(EbxAssetEntry mvdbEntry,
            Dictionary<Guid, EbxAssetEntry> oldToNew)
        {
            EbxAsset mvdbAsset = App.AssetManager.GetEbx(mvdbEntry);
            dynamic mvdbRoot = mvdbAsset.RootObject;
            bool modified = false;

            foreach (dynamic entry in mvdbRoot.Entries)
            {
                if (entry.Mesh.Type != PointerRefType.External)
                    continue;

                Guid oldMeshGuid = entry.Mesh.External.FileGuid;
                if (!oldToNew.ContainsKey(oldMeshGuid))
                    continue;

                EbxAssetEntry newMeshEntry = oldToNew[oldMeshGuid];
                EbxAsset newMeshAsset = App.AssetManager.GetEbx(newMeshEntry);

                entry.Mesh = MakeRef(newMeshAsset);
                modified = true;
                App.Logger.Log("  MVDB: Mesh -> " + newMeshEntry.Name);

                foreach (dynamic mat in entry.Materials)
                {
                    if (mat.Material.Type == PointerRefType.External)
                    {
                        Guid matFileGuid = mat.Material.External.FileGuid;
                        if (oldToNew.ContainsKey(matFileGuid))
                        {
                            Guid classGuid = mat.Material.External.ClassGuid;
                            mat.Material = MakeRef(newMeshAsset, classGuid);
                            modified = true;
                        }
                    }

                    foreach (dynamic texParam in mat.TextureParameters)
                    {
                        if (texParam.Value.Type != PointerRefType.External)
                            continue;

                        Guid oldTexGuid = texParam.Value.External.FileGuid;
                        if (!oldToNew.ContainsKey(oldTexGuid))
                            continue;

                        EbxAssetEntry newTexEntry = oldToNew[oldTexGuid];
                        EbxAsset newTexAsset = App.AssetManager.GetEbx(newTexEntry);
                        texParam.Value = MakeRef(newTexAsset);
                        modified = true;

                        string paramName = "";
                        try { paramName = texParam.ParameterName; } catch { }
                        App.Logger.Log("  MVDB: " + paramName + " -> " + newTexEntry.Name);
                    }
                }
            }

            if (modified)
            {
                mvdbAsset.Update();
                App.AssetManager.ModifyEbx(mvdbEntry.Name, mvdbAsset);
                App.Logger.Log("  Saved MVDB: " + mvdbEntry.Name);
            }
        }
    }
}
