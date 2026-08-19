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
    public class DuplicateKitMenuExtension : MenuExtension
    {
        private readonly Dictionary<string, DuplicationTool.DuplicateAssetExtension> extensions
            = new Dictionary<string, DuplicationTool.DuplicateAssetExtension>();

        private static readonly HashSet<string> BRT_TYPES = new HashSet<string>
        {
            "TextureAsset"
        };

        public DuplicateKitMenuExtension()
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
        public override string MenuItemName => "Duplicate Kit";
        public override ImageSource Icon => null;

        public override RelayCommand MenuItemClicked => new RelayCommand((o) =>
        {
            EbxAssetEntry entry = App.SelectedAsset as EbxAssetEntry;
            if (entry == null)
            {
                FrostyMessageBox.Show(
                    "No asset selected. Select any asset inside the kit folder you want to duplicate.",
                    "Kit Duplicator");
                return;
            }

            string sourceFolder = entry.Path.Replace('\\', '/');
            if (string.IsNullOrEmpty(sourceFolder))
            {
                FrostyMessageBox.Show("Selected asset has no folder path.", "Kit Duplicator");
                return;
            }

            DuplicateKitWindow win = new DuplicateKitWindow(sourceFolder);
            if (win.ShowDialog() != true)
                return;

            string newFolderName = win.NewFolderName;
            string destPath = win.DestinationPath;

            FrostyTaskWindow.Show("Duplicating Kit", "", (task) =>
            {
                try
                {
                    if (!MeshVariationDb.IsLoaded)
                        MeshVariationDb.LoadVariations(task);

                    DuplicateKit(task, sourceFolder, newFolderName, destPath);
                }
                catch (Exception ex)
                {
                    App.Logger.Log("Error duplicating kit: " + ex.ToString());
                }
            });

            App.EditorWindow.DataExplorer.RefreshAll();
        });

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
        /// Extracts the trailing numeric portion of a folder name.
        /// "home_0_1940" → "1940", "away_1_0" → "0"
        /// </summary>
        private static string ExtractTrailingNumber(string folderName)
        {
            int last = folderName.LastIndexOf('_');
            if (last < 0) return null;
            string candidate = folderName.Substring(last + 1);
            int dummy;
            return int.TryParse(candidate, out dummy) ? candidate : null;
        }

        /// <summary>
        /// Extracts the second-to-last numeric portion (kit type) from a kit subfolder name.
        /// "home_0_1940" → "0", "third_3_0" → "3", "goalie_home_2_0" → "2"
        /// </summary>
        private static string ExtractKitType(string folderName)
        {
            int lastSep = folderName.LastIndexOf('_');
            if (lastSep <= 0) return null;
            string withoutYear = folderName.Substring(0, lastSep);
            int prevSep = withoutYear.LastIndexOf('_');
            if (prevSep < 0) return null;
            string candidate = withoutYear.Substring(prevSep + 1);
            int dummy;
            return int.TryParse(candidate, out dummy) ? candidate : null;
        }

        internal void DuplicateKit(FrostyTaskWindow task, string sourceFolder,
            string newFolderName, string destPath)
        {
            string newFolder = destPath.TrimEnd('/') + "/" + newFolderName;

            App.Logger.Log("Kit source: " + sourceFolder);
            App.Logger.Log("Kit target: " + newFolder);

            // Source: .../1_fc_nurnberg_171/home_0_0 → parent = .../1_fc_nurnberg_171
            // Dest:   .../new_team_9999/third_3_0   → parent = .../new_team_9999
            string sourceParent = sourceFolder.Substring(0, sourceFolder.LastIndexOf('/'));
            string destParent = newFolder.Substring(0, newFolder.LastIndexOf('/'));

            // ── Phase 1: Enumerate ──────────────────────────────────────────────
            task.Update("Finding kit assets...");

            List<EbxAssetEntry> sourceAssets = new List<EbxAssetEntry>();
            EbxAssetEntry sourceBrtEbxl = null;   // the BundleRefTableBlueprintBundle EBX for this kit type
            string sourceBrtEbxlName = sourceFolder + "_kit_brt";

            foreach (EbxAssetEntry e in App.AssetManager.EnumerateEbx())
            {
                string path = e.Path.Replace('\\', '/');
                string name = e.Name.Replace('\\', '/');
                if (path.Equals(sourceFolder, StringComparison.OrdinalIgnoreCase))
                    sourceAssets.Add(e);
                else if (name.Equals(sourceBrtEbxlName, StringComparison.OrdinalIgnoreCase))
                    sourceBrtEbxl = e;
            }

            App.Logger.Log("Found " + sourceAssets.Count + " assets in kit folder, " +
                (sourceBrtEbxl != null ? "1 BB EBX" : "no BB EBX"));

            if (sourceAssets.Count == 0)
            {
                App.Logger.Log("No assets found in: " + sourceFolder);
                return;
            }

            // ── Phase 2: Duplicate ──────────────────────────────────────────────
            string sourceParentName = sourceParent.Substring(sourceParent.LastIndexOf('/') + 1);
            string newParentName = destParent.Substring(destParent.LastIndexOf('/') + 1);

            string oldTeamId = ExtractTrailingNumber(sourceParentName);
            string newTeamId = ExtractTrailingNumber(newParentName);

            string sourceFolderName = sourceFolder.Substring(sourceFolder.LastIndexOf('/') + 1);
            string oldKitType = ExtractKitType(sourceFolderName);
            string newKitType = ExtractKitType(newFolderName);
            string oldYear = ExtractTrailingNumber(sourceFolderName);
            string newYear = ExtractTrailingNumber(newFolderName);

            // Build the full pattern: _teamid_kittype_year_
            string oldPattern = null;
            string newPattern = null;
            if (!string.IsNullOrEmpty(oldTeamId) && !string.IsNullOrEmpty(newTeamId)
                && !string.IsNullOrEmpty(oldKitType) && !string.IsNullOrEmpty(newKitType)
                && !string.IsNullOrEmpty(oldYear) && !string.IsNullOrEmpty(newYear))
            {
                oldPattern = "_" + oldTeamId + "_" + oldKitType + "_" + oldYear + "_";
                newPattern = "_" + newTeamId + "_" + newKitType + "_" + newYear + "_";
                App.Logger.Log("  Rename pattern: " + oldPattern + " -> " + newPattern);
            }

            Dictionary<string, string> oldToNewNames = new Dictionary<string, string>();
            List<EbxAssetEntry> allNew = new List<EbxAssetEntry>();
            Dictionary<string, EbxAssetEntry> newEntriesByName = new Dictionary<string, EbxAssetEntry>(StringComparer.OrdinalIgnoreCase);

            // Per-texture blueprint bundles (actor). Kits like Arsenal carry an extra
            // BundleRefTableBlueprintBundle named "<texture>_actor_brt" inside the kit
            // folder; the texture it wraps must also live in that extra bundle.
            List<EbxAssetEntry> actorBbs = new List<EbxAssetEntry>();
            Dictionary<EbxAssetEntry, int> actorBundleIdByBb = new Dictionary<EbxAssetEntry, int>();
            Dictionary<EbxAssetEntry, string> actorWrappedSrcByBb = new Dictionary<EbxAssetEntry, string>();

            int current = 0;
            int total = sourceAssets.Count;

            foreach (EbxAssetEntry src in sourceAssets)
            {
                current++;
                string newFilename = src.Filename;

                if (oldPattern != null && newPattern != null && oldPattern != newPattern)
                {
                    // Replace mid-string: "jersey_171_0_0_color" → "jersey_171_3_0_color"
                    newFilename = newFilename.Replace(oldPattern, newPattern);

                    // Handle end-of-filename: "hotspots_171_0_0" (no trailing _textype)
                    string oldEnd = "_" + oldTeamId + "_" + oldKitType + "_" + oldYear;
                    string newEnd = "_" + newTeamId + "_" + newKitType + "_" + newYear;
                    if (newFilename.EndsWith(oldEnd))
                        newFilename = newFilename.Substring(0, newFilename.Length - oldEnd.Length) + newEnd;
                }

                string newName = newFolder + "/" + newFilename;
                task.Update("Duplicating " + src.Filename + " (" + current + "/" + total + ")...");

                EbxAssetEntry newEntry = DuplicateWithExtension(src, newName);
                if (newEntry != null)
                {
                    oldToNewNames[src.Name] = newEntry.Name;
                    newEntriesByName[newEntry.Name] = newEntry;
                    allNew.Add(newEntry);
                    App.Logger.Log("  Duplicated: " + src.Name + " -> " + newEntry.Name);

                    // Actor blueprint bundle: "<texture>_actor_brt" EBX inside the kit folder.
                    if (IsActorBb(src))
                    {
                        int actorBundleId = newEntry.AddedBundles.Count > 0 ? newEntry.AddedBundles[0] : -1;
                        actorBbs.Add(newEntry);
                        actorBundleIdByBb[newEntry] = actorBundleId;
                        actorWrappedSrcByBb[newEntry] = StripActorSuffix(src.Name);

                        DuplicationTool.FixBlueprintBundleName(newEntry, newName);
                        App.Logger.Log("  Actor blueprint bundle: " + src.Name + " -> " + newEntry.Name +
                            " (bundle id " + actorBundleId + ")");
                    }
                }
            }

            // ── Phase 2.5: Duplicate the kit type's BB EBX → new blueprint bundle ──
            EbxAssetEntry newBbEntry = null;
            int newBundleId = -1;
            if (sourceBrtEbxl != null)
            {
                string newBbName = newFolder + "_kit_brt";
                task.Update("Duplicating " + sourceBrtEbxl.Filename + "...");

                newBbEntry = DuplicateWithExtension(sourceBrtEbxl, newBbName);
                if (newBbEntry != null)
                {
                    allNew.Add(newBbEntry);
                    if (newBbEntry.AddedBundles.Count > 0)
                        newBundleId = newBbEntry.AddedBundles[0];

                    // The BB EBX holds a nested BundleRefTableBlueprint whose Name is
                    // "<root name>_blueprint"; base duplication only renames the root.
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

            // ── Phase 2.6: Move duplicates into the new blueprint bundle ────────
            if (newBundleId >= 0)
            {
                task.Update("Moving duplicated assets into the new bundle...");
                MoveAssetsToBundle(allNew, newBundleId);
            }

            // Actor bundles: put each actor BB back in its own bundle and register
            // the wrapped texture in that bundle too (it lives in BOTH the kit and
            // the per-texture actor bundle).
            foreach (EbxAssetEntry bb in actorBbs)
            {
                int actorBundleId = actorBundleIdByBb[bb];
                if (actorBundleId < 0)
                    continue;

                bb.AddedBundles.Clear();
                bb.AddedBundles.Add(actorBundleId);

                string wrappedSrc = actorWrappedSrcByBb[bb];
                if (oldToNewNames.TryGetValue(wrappedSrc, out string wrappedNewName)
                    && newEntriesByName.TryGetValue(wrappedNewName, out EbxAssetEntry wrappedEntry))
                {
                    AddToBundleRecursive(wrappedEntry, actorBundleId, new HashSet<AssetEntry>());
                    App.Logger.Log("  " + wrappedEntry.Filename + ": added to actor bundle " + actorBundleId);
                }
                else
                {
                    App.Logger.Log("  Actor bundle " + bb.Name + ": wrapped asset not found for '" + wrappedSrc + "'");
                }
            }

            // ── Phase 3: BRT injection ──────────────────────────────────────────
            if (!Config.Get<bool>("SkipBrtAdd", false))
            {
                task.Update("Updating BRT entries...");
                string newBundleRefName = (newBbEntry != null) ? newFolder.ToLower() : null;
                InjectBrtEntries(sourceAssets, oldToNewNames, newBundleRefName);
            }

            App.Logger.Log("Kit duplication complete (" + allNew.Count + " assets)");
        }

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

        private void AddToBundleRecursive(AssetEntry entry, int bundleId, HashSet<AssetEntry> visited)
        {
            if (entry == null || !visited.Add(entry))
                return;

            if (!entry.AddedBundles.Contains(bundleId))
                entry.AddedBundles.Add(bundleId);

            foreach (AssetEntry linked in entry.LinkedAssets)
                AddToBundleRecursive(linked, bundleId, visited);
        }

        private static bool IsActorBb(EbxAssetEntry e)
        {
            return e.Name.EndsWith("_actor_brt", StringComparison.OrdinalIgnoreCase);
        }

        private static string StripActorSuffix(string name)
        {
            if (name.EndsWith("_actor_brt", StringComparison.OrdinalIgnoreCase))
                return name.Substring(0, name.Length - "_actor_brt".Length);
            return name;
        }

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

                    // ── DEBUG: export modified BRT to disk ──
                    try
                    {
                        string debugDir = System.IO.Path.Combine(
                            Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                            "BRT_Debug");
                        System.IO.Directory.CreateDirectory(debugDir);

                        string safeName = brtRes.Filename.Replace('/', '_').Replace('\\', '_');

                        // DupeAsset only writes to the mod layer, not the in-memory arrays.
                        // Apply pending changes so SaveBytes reflects them.
                        brt.ApplyModifiedResource(brt.SaveModifiedResource());

                        string debugPath = System.IO.Path.Combine(debugDir, safeName + "_modified.bin");
                        byte[] meta = brt.ResourceMeta;
                        byte[] body = brt.SaveBytes();
                        byte[] bytes = new byte[meta.Length + body.Length];
                        Array.Copy(meta, 0, bytes, 0, meta.Length);
                        Array.Copy(body, 0, bytes, meta.Length, body.Length);
                        System.IO.File.WriteAllBytes(debugPath, bytes);
                        App.Logger.Log("  DEBUG: exported " + bytes.Length + " bytes -> " + debugPath);

                        // Also dump the mapping as text for easy inspection
                        string txtPath = System.IO.Path.Combine(debugDir, safeName + "_mapping.txt");
                        var lines = new List<string>();
                        lines.Add("BRT: " + brtRes.Name);
                        lines.Add("Assets: " + brt.assets.Count + "  Lookups: " + brt.assetLookups.Count);
                        lines.Add("");
                        foreach (var kvp2 in brtPairs)
                        {
                            lines.Add(kvp2.Key + " -> " + kvp2.Value);
                        }
                        lines.Add("");
                        lines.Add("--- Last 10 assets ---");
                        int start = Math.Max(0, brt.assets.Count - 10);
                        for (int a = start; a < brt.assets.Count; a++)
                        {
                            lines.Add("[" + a + "] " + brt.assets[a].Path + "/" + brt.assets[a].Name);
                        }
                        System.IO.File.WriteAllLines(txtPath, lines);
                        App.Logger.Log("  DEBUG: mapping -> " + txtPath);
                    }
                    catch (Exception ex)
                    {
                        App.Logger.Log("  DEBUG export failed: " + ex.Message);
                    }
                }
            }
        }
    }
}
