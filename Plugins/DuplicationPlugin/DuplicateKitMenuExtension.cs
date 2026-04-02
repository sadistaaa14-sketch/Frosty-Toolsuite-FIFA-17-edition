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

        /// <summary>
        /// Finds the bundle ID for launch_sba by scanning all bundles.
        /// Returns -1 if not found.
        /// </summary>
        private static int FindLaunchSbaBundleId()
        {
            foreach (BundleEntry be in App.AssetManager.EnumerateBundles())
            {
                if (be.Name.Equals("win32/content/common/configs/bundles/launch_sba",
                    StringComparison.OrdinalIgnoreCase))
                {
                    return App.AssetManager.GetBundleId(be);
                }
            }
            return -1;
        }

        private void DuplicateKit(FrostyTaskWindow task, string sourceFolder,
            string newFolderName, string destPath)
        {
            string newFolder = destPath.TrimEnd('/') + "/" + newFolderName;

            App.Logger.Log("Kit source: " + sourceFolder);
            App.Logger.Log("Kit target: " + newFolder);

            // ── Detect cross-team duplication ───────────────────────────────────
            // Source: .../1_fc_nurnberg_171/home_0_0 → parent = .../1_fc_nurnberg_171
            // Dest:   .../new_team_9999/third_3_0   → parent = .../new_team_9999
            string sourceParent = sourceFolder.Substring(0, sourceFolder.LastIndexOf('/'));
            string destParent = newFolder.Substring(0, newFolder.LastIndexOf('/'));
            bool isCrossTeam = !sourceParent.Equals(destParent, StringComparison.OrdinalIgnoreCase);

            int launchSbaBundleId = -1;
            if (isCrossTeam)
            {
                App.Logger.Log("Cross-team duplication detected");
                App.Logger.Log("  Source team folder: " + sourceParent);
                App.Logger.Log("  Dest team folder:   " + destParent);

                launchSbaBundleId = FindLaunchSbaBundleId();
                if (launchSbaBundleId < 0)
                {
                    App.Logger.Log("ERROR: Could not find launch_sba bundle. Aborting cross-team duplication.");
                    return;
                }
                App.Logger.Log("  launch_sba bundle ID: " + launchSbaBundleId);
            }

            // ── Phase 1: Enumerate ──────────────────────────────────────────────
            task.Update("Finding kit assets...");

            List<EbxAssetEntry> sourceAssets = new List<EbxAssetEntry>();

            foreach (EbxAssetEntry e in App.AssetManager.EnumerateEbx())
            {
                string path = e.Path.Replace('\\', '/');
                if (path.Equals(sourceFolder, StringComparison.OrdinalIgnoreCase))
                    sourceAssets.Add(e);
            }

            App.Logger.Log("Found " + sourceAssets.Count + " assets in kit folder");

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
                    // Cross-team: move assets into launch_sba so they are always loaded
                    if (isCrossTeam)
                    {
                        newEntry.AddedBundles.Clear();
                        newEntry.AddedBundles.Add(launchSbaBundleId);
                    }

                    oldToNewNames[src.Name] = newEntry.Name;
                    allNew.Add(newEntry);
                    App.Logger.Log("  Duplicated: " + src.Name + " -> " + newEntry.Name
                        + (isCrossTeam ? " [launch_sba]" : ""));
                }
            }

            // ── Phase 3: BRT injection ──────────────────────────────────────────
            if (!Config.Get<bool>("SkipBrtAdd", false))
            {
                task.Update("Updating BRT entries...");
                InjectBrtEntries(sourceAssets, oldToNewNames);
            }

            App.Logger.Log("Kit duplication complete (" + allNew.Count + " assets)"
                + (isCrossTeam ? " [cross-team]" : ""));
        }

        private void InjectBrtEntries(List<EbxAssetEntry> sourceAssets,
            Dictionary<string, string> oldToNewNames)
        {
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
                        if (brt.DupeAsset(kvp.Value, kvp.Key))
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
