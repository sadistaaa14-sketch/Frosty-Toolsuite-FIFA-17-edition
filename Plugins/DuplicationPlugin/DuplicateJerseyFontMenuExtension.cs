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
    /// Duplicates a jersey font (jerseyfonts/&lt;team&gt;): the TrueTypeFontAsset EBX
    /// (plus its UITtfFontFile res) and its &lt;font&gt;_font_brt / _launch_font_brt
    /// blueprint-bundle EBX. The font is registered in font_brt / launch_font_brt as a
    /// per-asset (Style B) ref, so DupeAssetToNewBundle derives the new ref automatically.
    /// </summary>
    public class DuplicateJerseyFontMenuExtension : MenuExtension
    {
        private readonly Dictionary<string, DuplicationTool.DuplicateAssetExtension> extensions
            = new Dictionary<string, DuplicationTool.DuplicateAssetExtension>();

        private static readonly string[] BRT_SUFFIXES = { "_launch_font_brt", "_font_brt" };

        public DuplicateJerseyFontMenuExtension()
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
        public override string MenuItemName => "Duplicate Jersey Font";
        public override ImageSource Icon => null;

        public override RelayCommand MenuItemClicked => new RelayCommand((o) =>
        {
            EbxAssetEntry entry = App.SelectedAsset as EbxAssetEntry;
            if (entry == null)
            {
                FrostyMessageBox.Show(
                    "No asset selected. Select a jersey font (or its _font_brt blueprint bundle) to duplicate.",
                    "Jersey Font Duplicator");
                return;
            }

            string sourceFolder = entry.Path.Replace('\\', '/');
            if (string.IsNullOrEmpty(sourceFolder))
            {
                FrostyMessageBox.Show("Selected asset has no folder path.", "Jersey Font Duplicator");
                return;
            }

            DuplicateJerseyFontWindow win = new DuplicateJerseyFontWindow(sourceFolder);
            if (win.ShowDialog() != true)
                return;

            FrostyTaskWindow.Show("Duplicating Jersey Font", "", (task) =>
            {
                try
                {
                    DuplicateJerseyFont(task, sourceFolder, win.NewFolderName);
                }
                catch (Exception ex)
                {
                    App.Logger.Log("Error duplicating jersey font: " + ex.ToString());
                }
            });

            App.EditorWindow.DataExplorer.RefreshAll();
        });

        // ─── Helpers ───────────────────────────────────────────────────────

        private static bool IsBlueprintBundle(EbxAssetEntry e)
        {
            return TypeLibrary.IsSubClassOf(e.Type, "BlueprintBundle");
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

        private static string ExtractTrailingId(string name)
        {
            int last = name.LastIndexOf('_');
            if (last < 0) return null;
            string candidate = name.Substring(last + 1);
            int dummy;
            return int.TryParse(candidate, out dummy) ? candidate : null;
        }

        /// <summary>
        /// Duplicates the TrueTypeFontAsset EBX and re-points its Data ResourceRef at a
        /// duplicated UITtfFontFile res.
        /// </summary>
        private EbxAssetEntry DuplicateFontWithRes(EbxAssetEntry entry, string newName)
        {
            EbxAssetEntry newEntry = DuplicateWithExtension(entry, newName);
            if (newEntry == null)
                return null;

            try
            {
                EbxAsset newAsset = App.AssetManager.GetEbx(newEntry);
                dynamic root = newAsset.RootObject;

                ResAssetEntry resEntry = App.AssetManager.GetResEntry(root.Data);
                if (resEntry == null)
                {
                    App.Logger.Log("  " + newEntry.Filename + ": no font Data res found; keeping original reference");
                    return newEntry;
                }

                ResAssetEntry newResEntry = DuplicationTool.DuplicateRes(resEntry, newEntry.Name, ResourceType.UITtfFontFile);
                if (newResEntry == null)
                    return newEntry;

                root.Data = newResEntry.ResRid;
                newEntry.LinkAsset(newResEntry);
                App.AssetManager.ModifyEbx(newEntry.Name, newAsset);
                App.Logger.Log("  " + newEntry.Filename + ": font res -> " + newResEntry.Name);
            }
            catch (Exception ex)
            {
                App.Logger.Log("  " + newEntry.Filename + ": failed to duplicate font res: " + ex.Message);
            }

            return newEntry;
        }

        // ─── Main duplication ──────────────────────────────────────────────

        private void DuplicateJerseyFont(FrostyTaskWindow task, string sourceFolder, string newFolderName)
        {
            const string JERSEY_FONTS_ROOT = "content/character/jerseyfonts";

            string newId = ExtractTrailingId(newFolderName);
            if (string.IsNullOrEmpty(newId))
            {
                App.Logger.Log("New folder name has no trailing numeric id: " + newFolderName);
                return;
            }

            string newFolder = JERSEY_FONTS_ROOT + "/" + newFolderName;
            string newFontName = "font_" + newId;
            string newFontFull = newFolder + "/" + newFontName;

            App.Logger.Log("Jersey font source folder: " + sourceFolder);
            App.Logger.Log("Jersey font target:         " + newFontFull);

            task.Update("Finding source assets...");

            List<EbxAssetEntry> allEbx = App.AssetManager.EnumerateEbx().ToList();
            List<EbxAssetEntry> folderAssets = allEbx
                .Where(e => e.Path.Replace('\\', '/').Equals(sourceFolder, StringComparison.OrdinalIgnoreCase))
                .ToList();

            EbxAssetEntry sourceFont = folderAssets.FirstOrDefault(e =>
                e.Type == "TrueTypeFontAsset" || TypeLibrary.IsSubClassOf(e.Type, "TrueTypeFontAsset"));
            EbxAssetEntry sourceBb = folderAssets.FirstOrDefault(e => IsBlueprintBundle(e));

            App.Logger.Log("Found " + (sourceFont != null ? "font" : "NO font") +
                ", " + (sourceBb != null ? "1 blueprint bundle" : "NO blueprint bundle"));

            if (sourceFont == null)
            {
                App.Logger.Log("No TrueTypeFontAsset found in: " + sourceFolder);
                return;
            }

            // Determine the bundle suffix from the source BB name (_font_brt vs _launch_font_brt).
            string bbSuffix = null;
            if (sourceBb != null)
            {
                string bbName = sourceBb.Name.Replace('\\', '/');
                foreach (string suffix in BRT_SUFFIXES)
                {
                    if (bbName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                    {
                        bbSuffix = suffix;
                        break;
                    }
                }
            }
            if (bbSuffix == null)
                bbSuffix = "_font_brt";

            int current = 0;
            int total = 1 + (sourceBb != null ? 1 : 0);

            // 1. Duplicate the font + its res.
            current++;
            task.Update("Duplicating " + sourceFont.Filename + " (" + current + "/" + total + ")...");
            EbxAssetEntry newFont = DuplicateFontWithRes(sourceFont, newFontFull);
            if (newFont != null)
                App.Logger.Log("  Duplicated: " + sourceFont.Name + " -> " + newFont.Name);

            // 2. Duplicate the blueprint-bundle EBX (creates the new per-font bundle).
            EbxAssetEntry newBb = null;
            int newBundleId = -1;
            if (sourceBb != null)
            {
                current++;
                string newBbName = newFontFull + bbSuffix;
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

            // 3. Re-home the font (+ its res) into the new bundle.
            if (newFont != null && newBundleId >= 0)
            {
                ClearBundlesRecursive(newFont, new HashSet<AssetEntry>());
                AddToBundleRecursive(newFont, newBundleId, new HashSet<AssetEntry>());
                App.Logger.Log("  " + newFont.Filename + ": added to new bundle " + newBundleId);
            }

            // 4. Register the new font in font_brt / launch_font_brt.
            if (!Config.Get<bool>("SkipBrtAdd", false) && newFont != null)
            {
                task.Update("Updating BRT entries...");
                Dictionary<string, string> brtPairs = new Dictionary<string, string>();
                brtPairs[sourceFont.Name.ToLower()] = newFont.Name.ToLower();
                InjectBrtPairs(brtPairs, newFolder.ToLower());
            }

            App.Logger.Log("Jersey font duplication complete");
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
