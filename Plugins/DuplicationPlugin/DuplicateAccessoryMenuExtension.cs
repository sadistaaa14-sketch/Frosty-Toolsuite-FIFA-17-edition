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
    public class DuplicateAccessoryMenuExtension : MenuExtension
    {
        private readonly Dictionary<string, DuplicationTool.DuplicateAssetExtension> extensions
            = new Dictionary<string, DuplicationTool.DuplicateAssetExtension>();

        public DuplicateAccessoryMenuExtension()
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
        public override string MenuItemName => "Duplicate Accessory";
        public override ImageSource Icon => null;

        public override RelayCommand MenuItemClicked => new RelayCommand((o) =>
        {
            EbxAssetEntry entry = App.SelectedAsset as EbxAssetEntry;
            if (entry == null)
            {
                FrostyMessageBox.Show(
                    "No asset selected. Select any asset inside the accessory folder you want to duplicate.",
                    "Accessory Duplicator");
                return;
            }

            string sourceFolder = entry.Path.Replace('\\', '/');
            if (string.IsNullOrEmpty(sourceFolder))
            {
                FrostyMessageBox.Show("Selected asset has no folder path.", "Accessory Duplicator");
                return;
            }

            if (sourceFolder.EndsWith("_launch_accessory_brt", StringComparison.OrdinalIgnoreCase))
                sourceFolder = sourceFolder.Substring(0, sourceFolder.Length - "_launch_accessory_brt".Length);
            else if (sourceFolder.EndsWith("_accessory_brt", StringComparison.OrdinalIgnoreCase))
                sourceFolder = sourceFolder.Substring(0, sourceFolder.Length - "_accessory_brt".Length);

            string sourceAccessoryName = sourceFolder.Substring(sourceFolder.LastIndexOf('/') + 1);
            string oldId = ExtractId(sourceAccessoryName);
            if (string.IsNullOrEmpty(oldId))
            {
                FrostyMessageBox.Show(
                    "Could not extract a numeric ID from folder name '" + sourceAccessoryName + "'.\n" +
                    "Expected format: ankle_tapes_999",
                    "Accessory Duplicator");
                return;
            }

            DuplicateAccessoryWindow win = new DuplicateAccessoryWindow(sourceFolder);
            if (win.ShowDialog() != true)
                return;

            string newName = win.NewAccessoryName;
            string destPath = win.DestinationPath;

            FrostyTaskWindow.Show("Duplicating Accessory", "", (task) =>
            {
                try
                {
                    if (!MeshVariationDb.IsLoaded)
                        MeshVariationDb.LoadVariations(task);

                    DuplicateAccessory(task, sourceFolder, newName, destPath);
                }
                catch (Exception ex)
                {
                    App.Logger.Log("Error duplicating accessory: " + ex.ToString());
                }
            });

            App.EditorWindow.DataExplorer.RefreshAll();
        });

        public static string ExtractId(string folderName)
        {
            int last = folderName.LastIndexOf('_');
            if (last < 0) return null;
            string candidate = folderName.Substring(last + 1);
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

        private void DuplicateAccessory(FrostyTaskWindow task, string sourceFolder,
            string newAccessoryName, string destPath)
        {
            string sourceAccessoryName = sourceFolder.Substring(sourceFolder.LastIndexOf('/') + 1);
            string newFolder = destPath.TrimEnd('/') + "/" + newAccessoryName;

            string oldId = ExtractId(sourceAccessoryName);
            string newId = ExtractId(newAccessoryName);

            if (string.IsNullOrEmpty(oldId) || string.IsNullOrEmpty(newId))
            {
                App.Logger.Log("Could not extract IDs. Aborting.");
                return;
            }

            App.Logger.Log("Source: " + sourceAccessoryName + " (ID " + oldId + ")");
            App.Logger.Log("Target: " + newAccessoryName + " (ID " + newId + ")");

            // ── Phase 1: Enumerate ──────────────────────────────────────────────
            task.Update("Finding source assets...");

            string sourceBrtFolder1 = sourceFolder + "_accessory_brt";
            string sourceBrtFolder2 = sourceFolder + "_launch_accessory_brt";

            List<EbxAssetEntry> mainAssets = new List<EbxAssetEntry>();
            List<EbxAssetEntry> brtAssets = new List<EbxAssetEntry>();
            string sourceBrtFolder = null;

            foreach (EbxAssetEntry e in App.AssetManager.EnumerateEbx())
            {
                string path = e.Path.Replace('\\', '/');
                if (path.Equals(sourceFolder, StringComparison.OrdinalIgnoreCase))
                    mainAssets.Add(e);
                else if (path.Equals(sourceBrtFolder1, StringComparison.OrdinalIgnoreCase))
                {
                    brtAssets.Add(e);
                    sourceBrtFolder = sourceBrtFolder1;
                }
                else if (path.Equals(sourceBrtFolder2, StringComparison.OrdinalIgnoreCase))
                {
                    brtAssets.Add(e);
                    sourceBrtFolder = sourceBrtFolder2;
                }
            }

            string brtSuffix = "_accessory_brt";
            if (sourceBrtFolder != null && sourceBrtFolder.Equals(sourceBrtFolder2, StringComparison.OrdinalIgnoreCase))
                brtSuffix = "_launch_accessory_brt";
            string newBrtFolder = newFolder + brtSuffix;

            App.Logger.Log("Found " + mainAssets.Count + " main assets, " + brtAssets.Count + " BRT assets");

            if (mainAssets.Count == 0)
            {
                App.Logger.Log("No assets found in: " + sourceFolder);
                return;
            }

            // ── Phase 2: Duplicate ──────────────────────────────────────────────
            Dictionary<Guid, EbxAssetEntry> oldToNew = new Dictionary<Guid, EbxAssetEntry>();
            Dictionary<string, string> oldToNewNames = new Dictionary<string, string>();
            List<EbxAssetEntry> allNew = new List<EbxAssetEntry>();

            int current = 0;
            int total = mainAssets.Count + brtAssets.Count;

            foreach (EbxAssetEntry src in mainAssets)
            {
                current++;
                string newFilename = src.Filename.Replace(oldId, newId);
                string newAssetName = newFolder + "/" + newFilename;
                task.Update("Duplicating " + src.Filename + " (" + current + "/" + total + ")...");

                EbxAssetEntry newEntry = DuplicateWithExtension(src, newAssetName);
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
                string newFilename = src.Filename.Replace(oldId, newId);
                string newAssetName = newBrtFolder + "/" + newFilename;
                task.Update("Duplicating " + src.Filename + " (" + current + "/" + total + ")...");

                EbxAssetEntry newEntry = DuplicateWithExtension(src, newAssetName);
                if (newEntry != null)
                {
                    oldToNew[src.Guid] = newEntry;
                    oldToNewNames[src.Name] = newEntry.Name;
                    allNew.Add(newEntry);
                    App.Logger.Log("  Duplicated: " + src.Name + " -> " + newEntry.Name);
                }
            }

            // ── Phase 3: Fix references ─────────────────────────────────────────
            task.Update("Fixing cross-references...");
            FixCrossReferences(oldToNew, allNew);

            // ── Phase 4: BRT injection ──────────────────────────────────────────
            if (!Config.Get<bool>("SkipBrtAdd", false))
            {
                task.Update("Updating BRT entries...");
                InjectBrtEntries(mainAssets, oldToNewNames);
            }

            App.Logger.Log("Accessory duplication complete (" + allNew.Count + " assets)");
        }

        // ─── BRT Injection ──────────────────────────────────────────────────────

        private void InjectBrtEntries(List<EbxAssetEntry> sourceAssets,
            Dictionary<string, string> oldToNewNames)
        {
            // No type filtering — check every asset against ContainsAsset
            Dictionary<string, string> brtPairs = new Dictionary<string, string>();
            foreach (EbxAssetEntry src in sourceAssets)
            {
                if (oldToNewNames.ContainsKey(src.Name))
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

                        brt.ApplyModifiedResource(brt.SaveModifiedResource());

                        string debugPath = System.IO.Path.Combine(debugDir, safeName + "_modified.bin");
                        byte[] meta = brt.ResourceMeta;
                        byte[] body = brt.SaveBytes();
                        if (meta != null && meta.Length > 0)
                        {
                            byte[] bytes = new byte[meta.Length + body.Length];
                            Array.Copy(meta, 0, bytes, 0, meta.Length);
                            Array.Copy(body, 0, bytes, meta.Length, body.Length);
                            System.IO.File.WriteAllBytes(debugPath, bytes);
                            App.Logger.Log("  DEBUG: exported " + bytes.Length + " bytes -> " + debugPath);
                        }
                        else
                        {
                            System.IO.File.WriteAllBytes(debugPath, body);
                            App.Logger.Log("  DEBUG: exported " + body.Length + " bytes (no meta) -> " + debugPath);
                        }

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

        // ─── Cross-Reference Fixup ──────────────────────────────────────────────

        private void FixCrossReferences(Dictionary<Guid, EbxAssetEntry> oldToNew,
            List<EbxAssetEntry> newAssets)
        {
            foreach (EbxAssetEntry newEntry in newAssets)
            {
                try
                {
                    if (newEntry.Type == "TextureAsset"
                        || newEntry.Type == "SkinnedMeshAsset")
                        continue;

                    if (newEntry.Type == "MeshVariationDatabase")
                        FixMVDB(newEntry, oldToNew);
                    else if (newEntry.Type == "ObjectBlueprint")
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
