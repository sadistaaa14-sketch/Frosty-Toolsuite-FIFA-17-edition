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
    public class DuplicateBodyScaleMenuExtension : MenuExtension
    {
        private readonly Dictionary<string, DuplicationTool.DuplicateAssetExtension> extensions
            = new Dictionary<string, DuplicationTool.DuplicateAssetExtension>();

        public DuplicateBodyScaleMenuExtension()
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
        public override string MenuItemName => "Duplicate Body Scale";
        public override ImageSource Icon => null;

        public override RelayCommand MenuItemClicked => new RelayCommand((o) =>
        {
            EbxAssetEntry entry = App.SelectedAsset as EbxAssetEntry;
            if (entry == null)
            {
                FrostyMessageBox.Show(
                    "No asset selected. Select any bbscale asset to duplicate.",
                    "Body Scale Duplicator");
                return;
            }

            // Derive source bbscale name from selected asset
            // If user clicked the BB, strip the _bodyscale_brt suffix
            string sourceName = entry.Filename;
            if (sourceName.EndsWith("_bodyscale_brt", StringComparison.OrdinalIgnoreCase))
                sourceName = sourceName.Substring(0, sourceName.Length - "_bodyscale_brt".Length);

            string sourceFolder = entry.Path.Replace('\\', '/');
            // If selected from the BB subfolder, go up to the main folder
            if (sourceFolder.EndsWith("_bodyscale_brt", StringComparison.OrdinalIgnoreCase))
            {
                // BB is in a subfolder like .../bbscales/bbscale_0_0_0_bodyscale_brt
                // but from the screenshot they're all flat in bbscales — the Path IS the folder
                // Just use the parent folder
                sourceFolder = sourceFolder.Substring(0, sourceFolder.LastIndexOf('/'));
            }

            if (!sourceName.StartsWith("bbscale_", StringComparison.OrdinalIgnoreCase))
            {
                FrostyMessageBox.Show(
                    "Selected asset '" + sourceName + "' does not look like a bbscale.\n" +
                    "Expected format: bbscale_0_0_0",
                    "Body Scale Duplicator");
                return;
            }

            DuplicateBodyScaleWindow win = new DuplicateBodyScaleWindow(sourceFolder, sourceName);
            if (win.ShowDialog() != true)
                return;

            string newName = win.NewBodyScaleName;

            FrostyTaskWindow.Show("Duplicating Body Scale", "", (task) =>
            {
                try
                {
                    DuplicateBodyScale(task, sourceFolder, sourceName, newName);
                }
                catch (Exception ex)
                {
                    App.Logger.Log("Error duplicating body scale: " + ex.ToString());
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

        private void DuplicateBodyScale(FrostyTaskWindow task, string sourceFolder,
            string sourceName, string newName)
        {
            App.Logger.Log("Body Scale source: " + sourceName);
            App.Logger.Log("Body Scale target: " + newName);
            App.Logger.Log("Folder: " + sourceFolder);

            // ── Phase 1: Find source assets ─────────────────────────────────────
            task.Update("Finding source assets...");

            string sourceFullPath = sourceFolder + "/" + sourceName;
            string sourceBbName = sourceName + "_bodyscale_brt";
            string sourceBbFullPath = sourceFolder + "/" + sourceBbName;

            EbxAssetEntry sourceMain = null;
            EbxAssetEntry sourceBb = null;

            foreach (EbxAssetEntry e in App.AssetManager.EnumerateEbx())
            {
                if (e.Name.Equals(sourceFullPath, StringComparison.OrdinalIgnoreCase))
                    sourceMain = e;
                else if (e.Name.Equals(sourceBbFullPath, StringComparison.OrdinalIgnoreCase))
                    sourceBb = e;

                if (sourceMain != null && sourceBb != null)
                    break;
            }

            App.Logger.Log("Found main: " + (sourceMain != null ? sourceMain.Name : "NOT FOUND"));
            App.Logger.Log("Found BB:   " + (sourceBb != null ? sourceBb.Name : "NOT FOUND"));

            if (sourceMain == null)
            {
                App.Logger.Log("Source BodyBuilderDataAsset not found: " + sourceFullPath);
                return;
            }

            // ── Phase 2: Duplicate ──────────────────────────────────────────────
            string newMainPath = sourceFolder + "/" + newName;
            string newBbPath = sourceFolder + "/" + newName + "_bodyscale_brt";

            Dictionary<string, string> oldToNewNames = new Dictionary<string, string>();
            List<EbxAssetEntry> allNew = new List<EbxAssetEntry>();

            // Duplicate main asset (BodyBuilderDataAsset — plain EBX, no res/chunk)
            task.Update("Duplicating " + sourceName + "...");
            EbxAssetEntry newMain = DuplicateWithExtension(sourceMain, newMainPath);
            if (newMain != null)
            {
                oldToNewNames[sourceMain.Name] = newMain.Name;
                allNew.Add(newMain);
                App.Logger.Log("  Duplicated: " + sourceMain.Name + " -> " + newMain.Name);
            }

            // Duplicate BlueprintBundle (creates new Frosty bundle automatically
            // via BlueprintBundleExtension since type is BundleRefTableBlueprintBundle)
            if (sourceBb != null)
            {
                task.Update("Duplicating " + sourceBbName + "...");
                EbxAssetEntry newBb = DuplicateWithExtension(sourceBb, newBbPath);
                if (newBb != null)
                {
                    allNew.Add(newBb);
                    App.Logger.Log("  Duplicated: " + sourceBb.Name + " -> " + newBb.Name);
                }
            }
            else
            {
                App.Logger.Log("  WARNING: No BlueprintBundle found for " + sourceBbName);
            }

            // ── Phase 3: BRT injection ──────────────────────────────────────────
            if (!Config.Get<bool>("SkipBrtAdd", false) && oldToNewNames.Count > 0)
            {
                task.Update("Updating BRT entries...");
                InjectBrtEntries(sourceMain, oldToNewNames);
            }

            App.Logger.Log("Body scale duplication complete (" + allNew.Count + " assets)");
        }

        // ─── BRT Injection ──────────────────────────────────────────────────────

        private void InjectBrtEntries(EbxAssetEntry sourceMain,
            Dictionary<string, string> oldToNewNames)
        {
            // Only the main BodyBuilderDataAsset is in the BRT
            Dictionary<string, string> brtPairs = new Dictionary<string, string>();
            if (oldToNewNames.ContainsKey(sourceMain.Name))
            {
                brtPairs[sourceMain.Name.ToLower()] = oldToNewNames[sourceMain.Name].ToLower();
            }

            if (brtPairs.Count == 0)
            {
                App.Logger.Log("  No BRT-eligible assets to inject.");
                return;
            }

            App.Logger.Log("  BRT-eligible assets: " + brtPairs.Count);

            List<ResAssetEntry> allBrts = App.AssetManager.EnumerateRes((uint)ResourceType.BundleRefTableResource).ToList();

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
                }
            }
        }
    }
}
