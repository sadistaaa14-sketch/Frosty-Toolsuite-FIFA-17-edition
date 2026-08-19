using BundleRefTablePlugin;
using DuplicationPlugin.Windows;
using Frosty.Controls;
using Frosty.Core;
using Frosty.Core.Windows;
using FrostySdk;
using FrostySdk.IO;
using FrostySdk.Managers;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Windows.Media;

namespace DuplicationPlugin
{
    /// <summary>
    /// Duplicates a kit number set (kitnumber/&lt;string_number&gt;). A subfolder holds the
    /// ten digit textures (numbers_&lt;id&gt;_0_color .. numbers_&lt;id&gt;_9_color) and the main
    /// kitnumber folder holds the blueprint-bundle EBX (&lt;folder&gt;_launch_kit_brt / _kit_brt).
    /// The kit_brt / launch_kit_brt table registers these as a folder (Style A) ref, so
    /// DupeAssetToNewBundle creates the matching new folder ref automatically.
    /// </summary>
    public class DuplicateKitNumberMenuExtension : MenuExtension
    {
        private readonly Dictionary<string, DuplicationTool.DuplicateAssetExtension> extensions
            = new Dictionary<string, DuplicationTool.DuplicateAssetExtension>();

        private static readonly string[] BRT_SUFFIXES = { "_launch_kit_brt", "_kit_brt" };

        public DuplicateKitNumberMenuExtension()
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
        public override string MenuItemName => "Duplicate Kit Number";
        public override ImageSource Icon => null;

        public override RelayCommand MenuItemClicked => new RelayCommand((o) =>
        {
            EbxAssetEntry entry = App.SelectedAsset as EbxAssetEntry;
            if (entry == null)
            {
                FrostyMessageBox.Show(
                    "No asset selected. Select a kit number texture or its _kit_brt blueprint bundle to duplicate.",
                    "Kit Number Duplicator");
                return;
            }

            string sourceFolder = DeriveKitNumberSourceFolder(entry);
            if (string.IsNullOrEmpty(sourceFolder))
            {
                FrostyMessageBox.Show("Could not determine the kit number folder from the selection.", "Kit Number Duplicator");
                return;
            }

            DuplicateKitNumberWindow win = new DuplicateKitNumberWindow(sourceFolder);
            if (win.ShowDialog() != true)
                return;

            FrostyTaskWindow.Show("Duplicating Kit Number", "", (task) =>
            {
                try
                {
                    DuplicateKitNumber(task, sourceFolder, win.NewFolderName);
                }
                catch (Exception ex)
                {
                    App.Logger.Log("Error duplicating kit number: " + ex.ToString());
                }
            });

            App.EditorWindow.DataExplorer.RefreshAll();
        });

        // ─── Detection / helpers ───────────────────────────────────────────

        private static string DeriveKitNumberSourceFolder(EbxAssetEntry entry)
        {
            string name = entry.Name.Replace('\\', '/');
            string path = entry.Path.Replace('\\', '/');

            // A blueprint-bundle EBX was selected: its Name is "<folder><suffix>".
            foreach (string suffix in BRT_SUFFIXES)
            {
                if (name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                    return name.Substring(0, name.Length - suffix.Length);
            }

            return path;
        }

        private static bool IsBlueprintBundle(EbxAssetEntry e)
        {
            return TypeLibrary.IsSubClassOf(e.Type, "BlueprintBundle");
        }

        private static string ExtractTrailingId(string name)
        {
            int last = name.LastIndexOf('_');
            if (last < 0) return null;
            string candidate = name.Substring(last + 1);
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

        // ─── Main duplication ──────────────────────────────────────────────

        private void DuplicateKitNumber(FrostyTaskWindow task, string sourceFolder, string newFolderName)
        {
            const string KITNUMBER_ROOT = "content/character/kitnumber";

            string oldId = ExtractTrailingId(sourceFolder);
            string newId = ExtractTrailingId(newFolderName);
            if (string.IsNullOrEmpty(oldId) || string.IsNullOrEmpty(newId))
            {
                App.Logger.Log("Could not extract numeric ids (source='" + sourceFolder + "' new='" + newFolderName + "')");
                return;
            }

            string newFolder = KITNUMBER_ROOT + "/" + newFolderName;

            App.Logger.Log("Kit number source folder: " + sourceFolder + " (id " + oldId + ")");
            App.Logger.Log("Kit number target folder: " + newFolder + " (id " + newId + ")");

            task.Update("Finding source assets...");

            List<EbxAssetEntry> allEbx = App.AssetManager.EnumerateEbx().ToList();

            // The ten digit textures live inside the subfolder.
            List<EbxAssetEntry> sourceTextures = allEbx
                .Where(e => e.Path.Replace('\\', '/').Equals(sourceFolder, StringComparison.OrdinalIgnoreCase))
                .ToList();

            // The blueprint-bundle EBX lives in the main kitnumber folder.
            EbxAssetEntry sourceBb = null;
            string bbSuffix = null;
            foreach (string suffix in BRT_SUFFIXES)
            {
                string bbName = sourceFolder + suffix;
                EbxAssetEntry bb = allEbx.FirstOrDefault(e =>
                    IsBlueprintBundle(e)
                    && e.Name.Replace('\\', '/').Equals(bbName, StringComparison.OrdinalIgnoreCase));
                if (bb != null)
                {
                    sourceBb = bb;
                    bbSuffix = suffix;
                    break;
                }
            }

            App.Logger.Log("Found " + sourceTextures.Count + " digit textures, " +
                (sourceBb != null ? "1 blueprint bundle" : "NO blueprint bundle"));

            if (sourceTextures.Count == 0)
            {
                App.Logger.Log("No digit textures found in: " + sourceFolder);
                return;
            }

            int current = 0;
            int total = sourceTextures.Count + (sourceBb != null ? 1 : 0);

            // 1. Duplicate the digit textures, remapping the old id to the new id.
            Dictionary<Guid, EbxAssetEntry> oldToNew = new Dictionary<Guid, EbxAssetEntry>();
            Dictionary<string, string> oldToNewNames = new Dictionary<string, string>();
            List<EbxAssetEntry> allNew = new List<EbxAssetEntry>();

            foreach (EbxAssetEntry src in sourceTextures)
            {
                current++;
                string newFilename = RenameTexture(src.Filename, oldId, newId);
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

            // 2. Duplicate the blueprint-bundle EBX (creates the new bundle).
            EbxAssetEntry newBb = null;
            int newBundleId = -1;
            if (sourceBb != null)
            {
                current++;
                string newBbName = newFolder + bbSuffix;
                task.Update("Duplicating " + sourceBb.Filename + " (" + current + "/" + total + ")...");

                newBb = DuplicateWithExtension(sourceBb, newBbName);
                if (newBb != null)
                {
                    if (newBb.AddedBundles.Count > 0)
                        newBundleId = newBb.AddedBundles[0];

                    DuplicationTool.FixBlueprintBundleName(newBb, newBbName);
                    App.Logger.Log("  Duplicated: " + sourceBb.Name + " -> " + newBb.Name +
                        " (bundle id " + newBundleId + ")");
                }
            }

            // 3. Re-home the duplicated textures into the new bundle.
            if (newBundleId >= 0)
            {
                task.Update("Moving duplicated textures into the new bundle...");
                foreach (EbxAssetEntry e in allNew)
                {
                    ClearBundlesRecursive(e, new HashSet<AssetEntry>());
                    AddToBundleRecursive(e, newBundleId, new HashSet<AssetEntry>());
                }
            }

            // 4. Register the new textures in kit_brt / launch_kit_brt (folder ref).
            if (!Config.Get<bool>("SkipBrtAdd", false) && oldToNewNames.Count > 0)
            {
                task.Update("Updating BRT entries...");
                Dictionary<string, string> brtPairs = new Dictionary<string, string>();
                foreach (EbxAssetEntry src in sourceTextures)
                {
                    if (oldToNewNames.ContainsKey(src.Name))
                        brtPairs[src.Name.ToLower()] = oldToNewNames[src.Name].ToLower();
                }
                InjectBrtPairs(brtPairs, newFolder.ToLower());
            }

            App.Logger.Log("Kit number duplication complete (" + allNew.Count + " assets)");
        }

        private static string RenameTexture(string filename, string oldId, string newId)
        {
            // numbers_<oldId>_<digit>_color -> numbers_<newId>_<digit>_color
            string token = "numbers_" + oldId + "_";
            if (filename.StartsWith(token, StringComparison.OrdinalIgnoreCase))
                return "numbers_" + newId + "_" + filename.Substring(token.Length);

            return filename.Replace(oldId, newId);
        }

        // ─── BRT injection ──────────────────────────────────────────────────

        private void InjectBrtPairs(Dictionary<string, string> brtPairs, string newBundleRefName)
        {
            if (brtPairs.Count == 0)
            {
                App.Logger.Log("  No BRT-eligible assets to inject.");
                return;
            }

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

        private void AddToBundleRecursive(AssetEntry entry, int bundleId, HashSet<AssetEntry> visited)
        {
            if (entry == null || !visited.Add(entry))
                return;

            if (!entry.AddedBundles.Contains(bundleId))
                entry.AddedBundles.Add(bundleId);

            foreach (AssetEntry linked in entry.LinkedAssets)
                AddToBundleRecursive(linked, bundleId, visited);
        }

        private void ClearBundlesRecursive(AssetEntry entry, HashSet<AssetEntry> visited)
        {
            if (entry == null || !visited.Add(entry))
                return;

            entry.AddedBundles.Clear();

            foreach (AssetEntry linked in entry.LinkedAssets)
                ClearBundlesRecursive(linked, visited);
        }
    }
}
