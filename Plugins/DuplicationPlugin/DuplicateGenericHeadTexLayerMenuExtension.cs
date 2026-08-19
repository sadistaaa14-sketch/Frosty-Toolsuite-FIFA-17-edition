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
    /// Duplicates a "generic head tex layer" texture (genheadtexlayers/skintone_N).
    /// Each texture has its own BundleRefTableBlueprintBundle EBX named
    /// "&lt;texture&gt;_launch_generic_head_brt" (or "_generic_head_brt"). We duplicate the
    /// texture, duplicate its blueprint bundle EBX (which creates the new per-texture
    /// bundle), re-home the texture into the new bundle, and register the new asset in
    /// the BRT with a new Style-B bundle ref.
    /// </summary>
    public class DuplicateGenericHeadTexLayerMenuExtension : MenuExtension
    {
        private readonly Dictionary<string, DuplicationTool.DuplicateAssetExtension> extensions
            = new Dictionary<string, DuplicationTool.DuplicateAssetExtension>();

        // Ordered longest-first so _launch_generic_head_brt wins over _generic_head_brt.
        private static readonly string[] BRT_SUFFIXES = { "_launch_generic_head_brt", "_generic_head_brt" };

        public DuplicateGenericHeadTexLayerMenuExtension()
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
        public override string MenuItemName => "Duplicate Generic Head Tex Layer";
        public override ImageSource Icon => null;

        public override RelayCommand MenuItemClicked => new RelayCommand((o) =>
        {
            EbxAssetEntry entry = App.SelectedAsset as EbxAssetEntry;
            if (entry == null)
            {
                FrostyMessageBox.Show(
                    "No asset selected. Select a genheadtexlayers texture (or its _generic_head_brt blueprint bundle) to duplicate.",
                    "Generic Head Tex Layer Duplicator");
                return;
            }

            string sourceFull = DeriveSourceTexture(entry);
            if (string.IsNullOrEmpty(sourceFull))
            {
                FrostyMessageBox.Show(
                    "Could not determine the texture from the selection.",
                    "Generic Head Tex Layer Duplicator");
                return;
            }

            DuplicateGenericHeadTexLayerWindow win = new DuplicateGenericHeadTexLayerWindow(sourceFull);
            if (win.ShowDialog() != true)
                return;

            string newName = win.NewName;
            string destPath = win.DestinationPath;

            FrostyTaskWindow.Show("Duplicating Generic Head Tex Layer", "", (task) =>
            {
                try
                {
                    DuplicateGenericHeadTexLayer(task, sourceFull, newName, destPath);
                }
                catch (Exception ex)
                {
                    App.Logger.Log("Error duplicating generic head tex layer: " + ex.ToString());
                }
            });

            App.EditorWindow.DataExplorer.RefreshAll();
        });

        // ─── Detection / helpers ───────────────────────────────────────────

        private static string DeriveSourceTexture(EbxAssetEntry entry)
        {
            string full = entry.Name.Replace('\\', '/');

            // If a blueprint bundle EBX was selected, strip its BRT suffix to get the
            // underlying texture path.
            foreach (string suffix in BRT_SUFFIXES)
            {
                if (full.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                    return full.Substring(0, full.Length - suffix.Length);
            }

            return full;
        }

        private static bool IsBlueprintBundle(EbxAssetEntry e)
        {
            return TypeLibrary.IsSubClassOf(e.Type, "BlueprintBundle");
        }

        private static string DetectSuffix(string basePath, List<EbxAssetEntry> allEbx, out string detectedPath)
        {
            foreach (string suffix in BRT_SUFFIXES)
            {
                string candidate = basePath + suffix;
                foreach (EbxAssetEntry e in allEbx)
                {
                    string n = e.Name.Replace('\\', '/');
                    if (n.Equals(candidate, StringComparison.OrdinalIgnoreCase))
                    {
                        detectedPath = candidate;
                        return suffix;
                    }
                }
            }
            detectedPath = null;
            return null;
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

        private void DuplicateGenericHeadTexLayer(FrostyTaskWindow task, string sourceFull,
            string newName, string destPath)
        {
            string newFull = destPath.TrimEnd('/') + "/" + newName;

            App.Logger.Log("Generic head tex layer source: " + sourceFull);
            App.Logger.Log("Generic head tex layer target: " + newFull);

            task.Update("Finding source assets...");

            List<EbxAssetEntry> allEbx = App.AssetManager.EnumerateEbx().ToList();

            EbxAssetEntry sourceTexture = allEbx.FirstOrDefault(e =>
                e.Name.Replace('\\', '/').Equals(sourceFull, StringComparison.OrdinalIgnoreCase));

            string bbSuffix = DetectSuffix(sourceFull, allEbx, out string bbName);
            EbxAssetEntry sourceBb = bbName != null ? allEbx.FirstOrDefault(e =>
                IsBlueprintBundle(e)
                && e.Name.Replace('\\', '/').Equals(bbName, StringComparison.OrdinalIgnoreCase)) : null;

            App.Logger.Log("Found " + (sourceTexture != null ? "texture" : "NO texture") +
                ", " + (sourceBb != null ? "1 blueprint bundle" : "NO blueprint bundle"));

            if (sourceTexture == null)
            {
                App.Logger.Log("Source texture not found: " + sourceFull);
                return;
            }

            int current = 0;
            int total = 1 + (sourceBb != null ? 1 : 0);

            // Duplicate the texture itself (also duplicates its linked res/chunk).
            current++;
            task.Update("Duplicating " + sourceTexture.Filename + " (" + current + "/" + total + ")...");
            EbxAssetEntry newTexture = DuplicateWithExtension(sourceTexture, newFull);
            if (newTexture != null)
                App.Logger.Log("  Duplicated: " + sourceTexture.Name + " -> " + newTexture.Name);

            // Duplicate the blueprint-bundle EBX (creates the new per-texture bundle).
            EbxAssetEntry newBb = null;
            int newBundleId = -1;
            if (sourceBb != null)
            {
                current++;
                string newBbName = newFull + bbSuffix;
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

            // Re-home the duplicated texture into the new bundle. Its inherited source
            // bundle membership is replaced with the new bundle (a texture belongs to
            // exactly one per-texture blueprint bundle here).
            if (newTexture != null && newBundleId >= 0)
            {
                ClearBundlesRecursive(newTexture, new HashSet<AssetEntry>());
                AddToBundleRecursive(newTexture, newBundleId, new HashSet<AssetEntry>());
                App.Logger.Log("  " + newTexture.Filename + ": added to new bundle " + newBundleId);
            }

            // Register the new texture in the BRT with a new bundle ref.
            if (!Config.Get<bool>("SkipBrtAdd", false) && newTexture != null)
            {
                task.Update("Updating BRT entries...");
                Dictionary<string, string> brtPairs = new Dictionary<string, string>();
                brtPairs[sourceTexture.Name.ToLower()] = newTexture.Name.ToLower();
                InjectBrtPairs(brtPairs, destPath.TrimEnd('/').ToLower());
            }

            App.Logger.Log("Generic head tex layer duplication complete");
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
