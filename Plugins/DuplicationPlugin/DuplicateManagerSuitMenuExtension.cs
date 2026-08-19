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
    public class DuplicateManagerSuitMenuExtension : MenuExtension
    {
        private readonly Dictionary<string, DuplicationTool.DuplicateAssetExtension> extensions
            = new Dictionary<string, DuplicationTool.DuplicateAssetExtension>();

        private static readonly HashSet<string> BRT_TYPES = new HashSet<string>
        {
            "ObjectBlueprint",
            "ClothObjectBlueprint"
        };

        // Ordered longest-first so _launch_manager_brt wins over _manager_brt.
        private static readonly string[] MANAGER_BRT_SUFFIXES = { "_launch_manager_brt", "_manager_brt" };

        public DuplicateManagerSuitMenuExtension()
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
        public override string MenuItemName => "Duplicate Manager Suit";
        public override ImageSource Icon => null;

        public override RelayCommand MenuItemClicked => new RelayCommand((o) =>
        {
            EbxAssetEntry entry = App.SelectedAsset as EbxAssetEntry;
            if (entry == null)
            {
                FrostyMessageBox.Show(
                    "No asset selected. Select any asset inside the managersuit variation you want to duplicate.",
                    "Manager Suit Duplicator");
                return;
            }

            string sourceFolder = DeriveManagerSuitSourceFolder(entry);
            if (string.IsNullOrEmpty(sourceFolder))
            {
                FrostyMessageBox.Show(
                    "Could not determine the managersuit variation folder from the selection.",
                    "Manager Suit Duplicator");
                return;
            }

            DuplicateManagerSuitWindow win = new DuplicateManagerSuitWindow(sourceFolder);
            if (win.ShowDialog() != true)
                return;

            string newName = win.NewName;
            string destPath = win.DestinationPath;

            FrostyTaskWindow.Show("Duplicating Manager Suit", "", (task) =>
            {
                try
                {
                    DuplicateManagerSuit(task, sourceFolder, newName, destPath);
                }
                catch (Exception ex)
                {
                    App.Logger.Log("Error duplicating managersuit: " + ex.ToString());
                }
            });

            App.EditorWindow.DataExplorer.RefreshAll();
        });

        // ─── Detection / naming helpers ─────────────────────────────────────

        private static string DeriveManagerSuitSourceFolder(EbxAssetEntry entry)
        {
            string path = entry.Path.Replace('\\', '/');
            string name = entry.Name.Replace('\\', '/');

            foreach (string suffix in MANAGER_BRT_SUFFIXES)
            {
                if (path.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                    return path.Substring(0, path.Length - suffix.Length);
                if (name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                    return name.Substring(0, name.Length - suffix.Length);
            }

            return path;
        }

        private static string ExtractTrailingNumber(string name)
        {
            int last = name.LastIndexOf('_');
            if (last < 0) return null;
            string candidate = name.Substring(last + 1);
            int dummy;
            return int.TryParse(candidate, out dummy) ? candidate : null;
        }

        /// <summary>
        /// Replaces the last underscore-delimited segment equal to oldWeather with
        /// newWeather. This targets the weather id (the trailing digit of the
        /// variation name) inside asset names like "managerbody_0_0_1" or
        /// "managerbody_0_0_1_mesh".
        /// </summary>
        private static string ReplaceWeatherDigit(string filename, string oldWeather, string newWeather)
        {
            if (string.IsNullOrEmpty(oldWeather) || oldWeather == newWeather)
                return filename;

            string oldSeg = "_" + oldWeather;
            string newSeg = "_" + newWeather;

            int lastIdx = -1;
            int search = 0;
            while (true)
            {
                int idx = filename.IndexOf(oldSeg, search, StringComparison.Ordinal);
                if (idx < 0) break;
                int after = idx + oldSeg.Length;
                if (after == filename.Length || filename[after] == '_')
                    lastIdx = idx;
                search = idx + oldSeg.Length;
            }

            if (lastIdx < 0)
                return filename;

            return filename.Substring(0, lastIdx) + newSeg + filename.Substring(lastIdx + oldSeg.Length);
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

        // ─── Main duplication ───────────────────────────────────────────────

        internal void DuplicateManagerSuit(FrostyTaskWindow task, string sourceFolder,
            string newVariationName, string destPath)
        {
            string sourceVariationName = sourceFolder.Substring(sourceFolder.LastIndexOf('/') + 1);
            string newFolder = destPath.TrimEnd('/') + "/" + newVariationName;

            string oldWeather = ExtractTrailingNumber(sourceVariationName);
            string newWeather = ExtractTrailingNumber(newVariationName);

            App.Logger.Log("Manager suit source: " + sourceFolder);
            App.Logger.Log("Manager suit target: " + newFolder + " (weather " + oldWeather + " -> " + newWeather + ")");

            // ── Phase 1: Enumerate ──────────────────────────────────────────
            task.Update("Finding source assets...");

            string brtSuffix = null;
            string sourceBrtFolder = null;
            foreach (string suffix in MANAGER_BRT_SUFFIXES)
            {
                string candidate = sourceFolder + suffix;
                bool found = App.AssetManager.EnumerateEbx().Any(e =>
                    e.Path.Replace('\\', '/').Equals(candidate, StringComparison.OrdinalIgnoreCase)
                    || e.Name.Replace('\\', '/').Equals(candidate, StringComparison.OrdinalIgnoreCase));
                if (found)
                {
                    brtSuffix = suffix;
                    sourceBrtFolder = candidate;
                    break;
                }
            }

            if (brtSuffix == null)
            {
                App.Logger.Log("No _manager_brt / _launch_manager_brt folder found for: " + sourceFolder);
                return;
            }

            List<EbxAssetEntry> mainAssets = new List<EbxAssetEntry>();
            List<EbxAssetEntry> brtAssets = new List<EbxAssetEntry>();
            EbxAssetEntry sourceBb = null;

            foreach (EbxAssetEntry e in App.AssetManager.EnumerateEbx())
            {
                string path = e.Path.Replace('\\', '/');
                string name = e.Name.Replace('\\', '/');
                if (path.Equals(sourceFolder, StringComparison.OrdinalIgnoreCase))
                {
                    mainAssets.Add(e);
                }
                else if (path.Equals(sourceBrtFolder, StringComparison.OrdinalIgnoreCase))
                {
                    brtAssets.Add(e);
                }
                else if (name.Equals(sourceBrtFolder, StringComparison.OrdinalIgnoreCase))
                {
                    sourceBb = e;
                }
            }

            App.Logger.Log("Found " + mainAssets.Count + " main assets, " + brtAssets.Count +
                " mesh-variation assets, " + (sourceBb != null ? "1 blueprint bundle" : "no blueprint bundle"));

            if (mainAssets.Count == 0)
            {
                App.Logger.Log("No assets found in: " + sourceFolder);
                return;
            }

            string newBrtFolder = newFolder + brtSuffix;

            // ── Phase 2: Duplicate ──────────────────────────────────────────
            Dictionary<Guid, EbxAssetEntry> oldToNew = new Dictionary<Guid, EbxAssetEntry>();
            Dictionary<string, string> oldToNewNames = new Dictionary<string, string>();
            List<EbxAssetEntry> allNew = new List<EbxAssetEntry>();

            int current = 0;
            int total = mainAssets.Count + brtAssets.Count + (sourceBb != null ? 1 : 0);

            foreach (EbxAssetEntry src in mainAssets)
            {
                current++;
                string newFilename = ReplaceWeatherDigit(src.Filename, oldWeather, newWeather);
                string newName = newFolder + "/" + newFilename;
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

            foreach (EbxAssetEntry src in brtAssets)
            {
                current++;
                string newName = newBrtFolder + "/" + src.Filename;
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

            // Duplicate the blueprint-bundle EBX (creates the new bundle).
            EbxAssetEntry newBb = null;
            int newBundleId = -1;
            if (sourceBb != null)
            {
                current++;
                string newBbName = newBrtFolder;
                task.Update("Duplicating " + sourceBb.Filename + " (" + current + "/" + total + ")...");

                newBb = DuplicateWithExtension(sourceBb, newBbName);
                if (newBb != null)
                {
                    allNew.Add(newBb);
                    if (newBb.AddedBundles.Count > 0)
                        newBundleId = newBb.AddedBundles[0];

                    DuplicationTool.FixBlueprintBundleName(newBb, newBbName);
                    App.Logger.Log("  Duplicated: " + sourceBb.Name + " -> " + newBb.Name +
                        " (bundle id " + newBundleId + ")");
                }
            }

            // ── Phase 2.5: Move duplicates into the new blueprint bundle ────
            if (newBundleId >= 0)
            {
                task.Update("Moving duplicated assets into the new bundle...");
                MoveAssetsToBundle(allNew, newBundleId);
            }

            // ── Phase 3: Fix references ─────────────────────────────────────
            task.Update("Fixing cross-references...");
            FixCrossReferences(oldToNew, allNew);

            // ── Phase 4: BRT injection ──────────────────────────────────────
            if (!Config.Get<bool>("SkipBrtAdd", false))
            {
                task.Update("Updating BRT entries...");
                Dictionary<string, string> brtPairs = new Dictionary<string, string>();
                foreach (EbxAssetEntry src in mainAssets)
                {
                    if (BRT_TYPES.Contains(src.Type) && oldToNewNames.ContainsKey(src.Name))
                        brtPairs[src.Name.ToLower()] = oldToNewNames[src.Name].ToLower();
                }
                InjectBrtPairs(brtPairs, newFolder.ToLower());
            }

            App.Logger.Log("Manager suit duplication complete (" + allNew.Count + " assets)");
        }

        // ─── BRT injection ──────────────────────────────────────────────────

        private void InjectBrtPairs(Dictionary<string, string> brtPairs, string newBundleRefName)
        {
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
                        bool added = brt.DupeAssetToNewBundle(kvp.Value, kvp.Key, newBundleRefName);
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

        // ─── Bundle re-pointing ────────────────────────────────────────────

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

            entry.AddedBundles.Clear();
            entry.AddedBundles.Add(newBundleId);

            foreach (AssetEntry linked in entry.LinkedAssets)
                MoveToBundleRecursive(linked, newBundleId, visited);
        }

        // ─── Cross-reference fixup ──────────────────────────────────────────

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
