using BundleRefTablePlugin;
using DuplicationPlugin.Windows;
using Frosty.Controls;
using Frosty.Core;
using Frosty.Core.Windows;
using FrostySdk;
using FrostySdk.Ebx;
using FrostySdk.IO;
using FrostySdk.Managers;
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Windows.Media;

namespace DuplicationPlugin
{
    /// <summary>
    /// Duplicates a single "base" asset (a texture or a SurfaceShaderGraph) and injects
    /// it into the base_brt EBX asset (content/common/Configs/bundlereftable/base_brt).
    /// base_brt is a BundleRefTable EBX whose Assets array lists every referenced asset
    /// and whose AssetLookups array maps the FNV1 hash of the asset's filename to that
    /// asset. No per-asset bundle exists, so no bundle work is needed.
    /// </summary>
    public class DuplicateBaseMenuExtension : MenuExtension
    {
        private readonly Dictionary<string, DuplicationTool.DuplicateAssetExtension> extensions
            = new Dictionary<string, DuplicationTool.DuplicateAssetExtension>();

        private const string BASE_BRT_NAME = "content/common/Configs/bundlereftable/base_brt";

        public DuplicateBaseMenuExtension()
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
        public override string MenuItemName => "Duplicate Base";
        public override ImageSource Icon => null;

        public override RelayCommand MenuItemClicked => new RelayCommand((o) =>
        {
            EbxAssetEntry entry = App.SelectedAsset as EbxAssetEntry;
            if (entry == null)
            {
                FrostyMessageBox.Show(
                    "No asset selected. Select a texture or SurfaceShaderGraph to duplicate.",
                    "Base Duplicator");
                return;
            }

            DuplicateBaseWindow win = new DuplicateBaseWindow(entry.Name.Replace('\\', '/'));
            if (win.ShowDialog() != true)
                return;

            FrostyTaskWindow.Show("Duplicating Base Asset", "", (task) =>
            {
                try
                {
                    DuplicateBase(task, entry, win.NewName);
                }
                catch (Exception ex)
                {
                    App.Logger.Log("Error duplicating base asset: " + ex.ToString());
                }
            });

            App.EditorWindow.DataExplorer.RefreshAll();
        });

        // ─── Helpers ───────────────────────────────────────────────────────

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

        // ─── Main duplication ──────────────────────────────────────────────

        private void DuplicateBase(FrostyTaskWindow task, EbxAssetEntry entry, string newName)
        {
            string sourceFolder = entry.Path.Replace('\\', '/');
            string newFull = sourceFolder.TrimEnd('/') + "/" + newName;

            App.Logger.Log("Base source: " + entry.Name);
            App.Logger.Log("Base target: " + newFull);

            task.Update("Duplicating " + entry.Filename + "...");

            EbxAssetEntry newEntry = DuplicateWithExtension(entry, newFull);
            if (newEntry == null)
            {
                App.Logger.Log("Duplication failed for: " + entry.Name);
                return;
            }
            App.Logger.Log("  Duplicated: " + entry.Name + " -> " + newEntry.Name);

            task.Update("Injecting into base_brt...");
            InjectBaseBrt(newEntry, newName);
        }

        // ─── base_brt injection ────────────────────────────────────────────

        private void InjectBaseBrt(EbxAssetEntry newEntry, string newFilename)
        {
            EbxAssetEntry baseBrtEntry = App.AssetManager.GetEbxEntry(BASE_BRT_NAME);
            if (baseBrtEntry == null)
            {
                App.Logger.Log("base_brt EBX not found: " + BASE_BRT_NAME);
                return;
            }

            EbxAsset brt = App.AssetManager.GetEbx(baseBrtEntry);
            dynamic root = brt.RootObject;

            EbxAsset newAsset = App.AssetManager.GetEbx(newEntry);
            PointerRef assetRef = MakeRef(newAsset);
            uint hash = BRTUtils.Fnv1Hash32(newFilename);

            // 1. Assets reference list (keeps the asset in the base superbundle).
            root.Assets.Add(assetRef);

            // 2. AssetLookups hash table.
            dynamic lookup = TypeLibrary.CreateObject("AssetLookup");
            if (lookup == null)
            {
                App.Logger.Log("  Failed to create AssetLookup type; base_brt injection incomplete");
                App.AssetManager.ModifyEbx(baseBrtEntry.Name, brt);
                return;
            }
            lookup.Hash = hash;
            lookup.Asset = assetRef;
            root.AssetLookups.Add(lookup);

            // 3. Keep the lookups sorted by hash, matching the original layout.
            SortAssetLookups(root.AssetLookups);

            App.AssetManager.ModifyEbx(baseBrtEntry.Name, brt);
            App.Logger.Log("  base_brt: injected " + newEntry.Name + " (hash 0x" + hash.ToString("X8") + ")");
        }

        private static void SortAssetLookups(dynamic lookupsList)
        {
            try
            {
                System.Collections.IList list = (System.Collections.IList)lookupsList;
                List<object> items = new List<object>(list.Count);
                foreach (object item in list)
                    items.Add(item);

                items.Sort(delegate (object a, object b)
                {
                    uint ha = Convert.ToUInt32(((dynamic)a).Hash);
                    uint hb = Convert.ToUInt32(((dynamic)b).Hash);
                    return ha.CompareTo(hb);
                });

                list.Clear();
                foreach (object item in items)
                    list.Add(item);
            }
            catch (Exception ex)
            {
                App.Logger.Log("  Warning: could not sort base_brt AssetLookups: " + ex.Message);
            }
        }
    }
}
