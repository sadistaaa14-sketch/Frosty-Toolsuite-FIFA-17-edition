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
    public class DuplicateBodyMenuExtension : MenuExtension
    {
        private readonly Dictionary<string, DuplicationTool.DuplicateAssetExtension> extensions
            = new Dictionary<string, DuplicationTool.DuplicateAssetExtension>();

        // Body blueprints (ObjectBlueprint / ClothObjectBlueprint) are registered in
        // the _body_brt / _launch_body_brt and (for cloth) _cloth_brt / _launch_cloth_brt
        // tables. The mesh is pulled in via the MVDB and the cloth asset via the
        // ClothObjectBlueprint's Cloth field, so neither is a BRT lookup of its own.
        private static readonly HashSet<string> BRT_TYPES = new HashSet<string>
        {
            "ObjectBlueprint",
            "ClothObjectBlueprint"
        };

        // Ordered longest-first so _launch_*_brt wins over _*_brt (which is its suffix).
        private static readonly string[] BODY_BRT_SUFFIXES = { "_launch_body_brt", "_body_brt" };
        private static readonly string[] CLOTH_BRT_SUFFIXES = { "_launch_cloth_brt", "_cloth_brt" };
        private static readonly string[] ACTOR_BRT_SUFFIXES = { "_launch_actor_brt", "_actor_brt" };

        public DuplicateBodyMenuExtension()
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
        public override string MenuItemName => "Duplicate Body";
        public override ImageSource Icon => null;

        public override RelayCommand MenuItemClicked => new RelayCommand((o) =>
        {
            EbxAssetEntry entry = App.SelectedAsset as EbxAssetEntry;
            if (entry == null)
            {
                FrostyMessageBox.Show(
                    "No asset selected. Select a body asset (object blueprint, mesh, mesh variation, or blueprint bundle) or a common texture to duplicate.",
                    "Body Duplicator");
                return;
            }

            string selPath = entry.Path.Replace('\\', '/');
            string selName = entry.Name.Replace('\\', '/');

            // ── Common-folder texture case ────────────────────────────────
            if (IsCommonFolder(selPath) || IsCommonFolder(selName))
            {
                DuplicateCommonEntry(entry);
                return;
            }

            // ── Body mesh case ────────────────────────────────────────────
            string sourceFolder = DeriveBodySourceFolder(entry);
            if (string.IsNullOrEmpty(sourceFolder))
            {
                FrostyMessageBox.Show(
                    "Could not determine the body asset folder from the selection.",
                    "Body Duplicator");
                return;
            }

            string sourceFolderName = sourceFolder.Substring(sourceFolder.LastIndexOf('/') + 1);
            string sourceBaseName = FindBodySourceBaseName(sourceFolder) ?? sourceFolderName;

            DuplicateBodyWindow win = new DuplicateBodyWindow(sourceFolder, null,
                sourceDisplay: null, defaultNewName: sourceBaseName, defaultNewFolder: sourceFolderName);
            if (win.ShowDialog() != true)
                return;

            string newName = win.NewName;
            string newFolderName = win.NewFolderName;
            string destPath = win.DestinationPath;
            string clothPrefix = win.ClothPrefix;

            FrostyTaskWindow.Show("Duplicating Body", "", (task) =>
            {
                try
                {
                    DuplicateBody(task, sourceFolder, newName, newFolderName, destPath, clothPrefix);
                }
                catch (Exception ex)
                {
                    App.Logger.Log("Error duplicating body: " + ex.ToString());
                }
            });

            App.EditorWindow.DataExplorer.RefreshAll();
        });

        // ─── Detection helpers ─────────────────────────────────────────────

        private static bool IsCommonFolder(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            string lower = path.ToLower();
            return lower.Contains("/body/common") || lower.EndsWith("/body/common");
        }

        private static bool IsBlueprintBundle(EbxAssetEntry e)
        {
            return TypeLibrary.IsSubClassOf(e.Type, "BlueprintBundle");
        }

        private static bool IsClothBlueprint(EbxAssetEntry e)
        {
            string n = e.Name.ToLower();
            return n.EndsWith("_launch_cloth_brt") || n.EndsWith("_cloth_brt");
        }

        private static string FindBodySourceBaseName(string sourceFolder)
        {
            foreach (EbxAssetEntry e in App.AssetManager.EnumerateEbx())
            {
                if (e.Path.Replace('\\', '/').Equals(sourceFolder, StringComparison.OrdinalIgnoreCase)
                    && (e.Type == "ObjectBlueprint" || e.Type == "ClothObjectBlueprint"))
                {
                    return e.Filename;
                }
            }
            return null;
        }

        private static string DeriveBodySourceFolder(EbxAssetEntry entry)
        {
            string path = entry.Path.Replace('\\', '/');
            string name = entry.Name.Replace('\\', '/');

            // A BRT subfolder (mesh variation database folder) was selected: the
            // object-blueprint folder is its parent (cloth) or the folder without the
            // suffix (body).
            foreach (string suffix in BODY_BRT_SUFFIXES.Concat(CLOTH_BRT_SUFFIXES))
            {
                if (path.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                {
                    string folder = path.Substring(0, path.Length - suffix.Length);
                    if (CLOTH_BRT_SUFFIXES.Contains(suffix))
                    {
                        int slash = folder.LastIndexOf('/');
                        if (slash > 0) folder = folder.Substring(0, slash);
                    }
                    return folder;
                }
            }

            // A blueprint-bundle EBX was selected (its Name carries the folder+suffix).
            foreach (string suffix in BODY_BRT_SUFFIXES.Concat(CLOTH_BRT_SUFFIXES))
            {
                if (name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                {
                    string folder = name.Substring(0, name.Length - suffix.Length);
                    if (CLOTH_BRT_SUFFIXES.Contains(suffix))
                    {
                        int slash = folder.LastIndexOf('/');
                        if (slash > 0) folder = folder.Substring(0, slash);
                    }
                    return folder;
                }
            }

            // Otherwise the selected asset lives directly inside the object folder.
            return path;
        }

        private static string DetectSuffix(string basePath, string[] suffixes,
            List<EbxAssetEntry> allEbx, out string detectedPath)
        {
            foreach (string suffix in suffixes)
            {
                string candidate = basePath + suffix;
                foreach (EbxAssetEntry e in allEbx)
                {
                    string p = e.Path.Replace('\\', '/');
                    string n = e.Name.Replace('\\', '/');
                    if (p.Equals(candidate, StringComparison.OrdinalIgnoreCase)
                        || n.Equals(candidate, StringComparison.OrdinalIgnoreCase))
                    {
                        detectedPath = candidate;
                        return suffix;
                    }
                }
            }
            detectedPath = null;
            return null;
        }

        // ─── Duplication helpers ───────────────────────────────────────────

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

        /// <summary>
        /// Duplicates the EAClothEntityData .res referenced by a ClothObjectBlueprint's
        /// nested ClothEntityData and points the duplicated blueprint at the new res.
        /// (Same fix as the starhead ClothObjectBlueprint handling.)
        /// </summary>
        private static void DuplicateClothEntityResource(EbxAssetEntry newEntry)
        {
            try
            {
                EbxAsset newAsset = App.AssetManager.GetEbx(newEntry);
                dynamic root = newAsset.RootObject;
                dynamic entity = root.Object.Internal;

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

        private static EbxAssetEntry FindClothAsset(EbxAssetEntry objBlueprint)
        {
            try
            {
                EbxAsset asset = App.AssetManager.GetEbx(objBlueprint);
                dynamic root = asset.RootObject;
                dynamic entity = root.Object.Internal;
                if (entity.Cloth.Type == PointerRefType.External)
                    return App.AssetManager.GetEbxEntry(entity.Cloth.External.FileGuid);
            }
            catch (Exception ex)
            {
                App.Logger.Log("  Could not resolve Cloth asset for " + objBlueprint.Name + ": " + ex.Message);
            }
            return null;
        }

        private static string ExtractNumbers(string name)
        {
            int i = name.IndexOfAny(new char[] { '0', '1', '2', '3', '4', '5', '6', '7', '8', '9' });
            return i < 0 ? name : name.Substring(i);
        }

        private static string ExtractClothPrefix(string clothName)
        {
            string stem = clothName.EndsWith("_cloth", StringComparison.OrdinalIgnoreCase)
                ? clothName.Substring(0, clothName.Length - "_cloth".Length)
                : clothName;
            int i = stem.IndexOfAny(new char[] { '0', '1', '2', '3', '4', '5', '6', '7', '8', '9' });
            string prefix = i < 0 ? stem : stem.Substring(0, i);
            return prefix.TrimEnd('_');
        }

        // ─── Main body duplication ─────────────────────────────────────────

        public void DuplicateBody(FrostyTaskWindow task, string sourceFolder,
            string newBaseName, string newFolderName, string destPath, string clothPrefix)
        {
            string folderName = string.IsNullOrEmpty(newFolderName) ? newBaseName : newFolderName;
            string newFolder = destPath.TrimEnd('/') + "/" + folderName;

            App.Logger.Log("Body source folder: " + sourceFolder);
            App.Logger.Log("Body target name:   " + newBaseName);
            App.Logger.Log("Body target folder: " + newFolder);

            // ── Phase 1: Enumerate ──────────────────────────────────────────
            task.Update("Finding source assets...");

            List<EbxAssetEntry> allEbx = App.AssetManager.EnumerateEbx().ToList();

            List<EbxAssetEntry> mainAssets = allEbx
                .Where(e => e.Path.Replace('\\', '/').Equals(sourceFolder, StringComparison.OrdinalIgnoreCase))
                .ToList();

            // The object blueprint carries the source base name (e.g. warmup_2_0_0_0).
            EbxAssetEntry sourceObj = mainAssets.FirstOrDefault(e =>
                e.Type == "ObjectBlueprint" || e.Type == "ClothObjectBlueprint");
            if (sourceObj == null)
            {
                App.Logger.Log("No ObjectBlueprint / ClothObjectBlueprint found in: " + sourceFolder);
                return;
            }
            string sourceBaseName = sourceObj.Filename;

            // Body blueprint bundle + mesh-variation folder.
            string bodyBrtSuffix = DetectSuffix(sourceFolder, BODY_BRT_SUFFIXES, allEbx, out string bodyBrtFolder);
            if (bodyBrtSuffix == null)
            {
                App.Logger.Log("No _body_brt / _launch_body_brt blueprint bundle found for: " + sourceFolder);
                return;
            }

            EbxAssetEntry sourceBodyBb = allEbx.FirstOrDefault(e =>
                IsBlueprintBundle(e)
                && e.Name.Replace('\\', '/').Equals(bodyBrtFolder, StringComparison.OrdinalIgnoreCase));
            List<EbxAssetEntry> bodyMvdbs = allEbx
                .Where(e => e.Path.Replace('\\', '/').Equals(bodyBrtFolder, StringComparison.OrdinalIgnoreCase))
                .ToList();

            // Optional cloth bundle (only ClothObjectBlueprint bodies carry one).
            EbxAssetEntry sourceClothBb = null;
            EbxAssetEntry sourceClothMvdb = null;
            EbxAssetEntry sourceClothAsset = null;
            string clothBrtSuffix = null;
            string clothBrtFolder = null;

            if (sourceObj.Type == "ClothObjectBlueprint")
            {
                foreach (string suffix in CLOTH_BRT_SUFFIXES)
                {
                    string candidate = sourceFolder + "/" + sourceBaseName + suffix;
                    EbxAssetEntry bb = allEbx.FirstOrDefault(e =>
                        IsBlueprintBundle(e)
                        && e.Name.Replace('\\', '/').Equals(candidate, StringComparison.OrdinalIgnoreCase));
                    if (bb != null)
                    {
                        sourceClothBb = bb;
                        clothBrtSuffix = suffix;
                        clothBrtFolder = candidate;
                        break;
                    }
                }

                if (sourceClothBb != null)
                {
                    sourceClothMvdb = allEbx.FirstOrDefault(e =>
                        e.Type == "MeshVariationDatabase"
                        && e.Path.Replace('\\', '/').Equals(clothBrtFolder, StringComparison.OrdinalIgnoreCase));
                    sourceClothAsset = FindClothAsset(sourceObj);
                }
            }

            App.Logger.Log("Found " + mainAssets.Count + " main assets, " + bodyMvdbs.Count +
                " body mesh-variation assets" +
                (sourceBodyBb != null ? ", 1 body blueprint bundle" : ", NO body blueprint bundle") +
                (sourceClothBb != null ? ", 1 cloth blueprint bundle" : "") +
                (sourceClothAsset != null ? ", 1 cloth asset" : ""));

            string newBodyBrtFolder = newFolder + bodyBrtSuffix;

            // ── Phase 2: Duplicate ──────────────────────────────────────────
            Dictionary<Guid, EbxAssetEntry> oldToNew = new Dictionary<Guid, EbxAssetEntry>();
            Dictionary<string, string> oldToNewNames = new Dictionary<string, string>();
            HashSet<Guid> duplicatedSrcGuids = new HashSet<Guid>();
            List<EbxAssetEntry> allNew = new List<EbxAssetEntry>();

            List<EbxAssetEntry> mainNew = new List<EbxAssetEntry>();   // obj blueprint, mesh, cloth asset

            int current = 0;
            int total = mainAssets.Count + bodyMvdbs.Count + 1 + (sourceClothBb != null ? 2 : 0)
                + (sourceClothAsset != null ? 1 : 0);

            // Duplicate the object blueprint, the mesh, and (if present) the cloth
            // blueprint-bundle EBX that lives inside the object folder.
            foreach (EbxAssetEntry src in mainAssets)
            {
                current++;

                // The cloth blueprint bundle is handled separately (its bundle is
                // distinct from the body bundle).
                if (IsBlueprintBundle(src) && IsClothBlueprint(src))
                    continue;

                string newFilename = src.Filename.Replace(sourceBaseName, newBaseName);
                string newName = newFolder + "/" + newFilename;
                task.Update("Duplicating " + src.Filename + " (" + current + "/" + total + ")...");

                EbxAssetEntry newEntry = DuplicateWithExtension(src, newName);
                if (newEntry == null)
                    continue;

                oldToNew[src.Guid] = newEntry;
                oldToNewNames[src.Name] = newEntry.Name;
                duplicatedSrcGuids.Add(src.Guid);
                allNew.Add(newEntry);
                mainNew.Add(newEntry);
                App.Logger.Log("  Duplicated: " + src.Name + " -> " + newEntry.Name);

                if (newEntry.Type == "ClothObjectBlueprint")
                    DuplicateClothEntityResource(newEntry);
            }

            // Duplicate the body mesh-variation database (MeshVariationDatabase).
            foreach (EbxAssetEntry src in bodyMvdbs)
            {
                current++;
                string newName = newBodyBrtFolder + "/" + src.Filename;
                task.Update("Duplicating " + src.Filename + " (" + current + "/" + total + ")...");

                EbxAssetEntry newEntry = DuplicateWithExtension(src, newName);
                if (newEntry == null)
                    continue;

                oldToNew[src.Guid] = newEntry;
                oldToNewNames[src.Name] = newEntry.Name;
                duplicatedSrcGuids.Add(src.Guid);
                allNew.Add(newEntry);
                App.Logger.Log("  Duplicated: " + src.Name + " -> " + newEntry.Name);
            }

            // Duplicate the body blueprint-bundle EBX (creates the new body bundle).
            EbxAssetEntry newBodyBb = null;
            int newBodyBundleId = -1;
            if (sourceBodyBb != null)
            {
                current++;
                string newBbName = newBodyBrtFolder;
                task.Update("Duplicating " + sourceBodyBb.Filename + " (" + current + "/" + total + ")...");

                newBodyBb = DuplicateWithExtension(sourceBodyBb, newBbName);
                if (newBodyBb != null)
                {
                    duplicatedSrcGuids.Add(sourceBodyBb.Guid);
                    allNew.Add(newBodyBb);
                    if (newBodyBb.AddedBundles.Count > 0)
                        newBodyBundleId = newBodyBb.AddedBundles[0];

                    DuplicationTool.FixBlueprintBundleName(newBodyBb, newBbName);
                    App.Logger.Log("  Duplicated: " + sourceBodyBb.Name + " -> " + newBodyBb.Name +
                        " (body bundle id " + newBodyBundleId + ")");
                }
            }

            // Duplicate the cloth blueprint bundle + its mesh variation database, and
            // the ClothAsset referenced by the ClothObjectBlueprint.
            EbxAssetEntry newClothBb = null;
            EbxAssetEntry newClothMvdb = null;
            EbxAssetEntry newClothAsset = null;
            int newClothBundleId = -1;
            string newClothBrtFolder = null;

            if (sourceClothBb != null)
            {
                newClothBrtFolder = newFolder + "/" + newBaseName + clothBrtSuffix;

                current++;
                string newBbName = newClothBrtFolder;
                task.Update("Duplicating " + sourceClothBb.Filename + " (" + current + "/" + total + ")...");

                newClothBb = DuplicateWithExtension(sourceClothBb, newBbName);
                if (newClothBb != null)
                {
                    duplicatedSrcGuids.Add(sourceClothBb.Guid);
                    allNew.Add(newClothBb);
                    if (newClothBb.AddedBundles.Count > 0)
                        newClothBundleId = newClothBb.AddedBundles[0];

                    DuplicationTool.FixBlueprintBundleName(newClothBb, newBbName);
                    App.Logger.Log("  Duplicated: " + sourceClothBb.Name + " -> " + newClothBb.Name +
                        " (cloth bundle id " + newClothBundleId + ")");
                }

                if (sourceClothMvdb != null)
                {
                    current++;
                    string newMvdbName = newClothBrtFolder + "/" + sourceClothMvdb.Filename;
                    task.Update("Duplicating " + sourceClothMvdb.Filename + " (" + current + "/" + total + ")...");

                    newClothMvdb = DuplicateWithExtension(sourceClothMvdb, newMvdbName);
                    if (newClothMvdb != null)
                    {
                        oldToNew[sourceClothMvdb.Guid] = newClothMvdb;
                        oldToNewNames[sourceClothMvdb.Name] = newClothMvdb.Name;
                        duplicatedSrcGuids.Add(sourceClothMvdb.Guid);
                        allNew.Add(newClothMvdb);
                        App.Logger.Log("  Duplicated: " + sourceClothMvdb.Name + " -> " + newClothMvdb.Name);
                    }
                }

                if (sourceClothAsset != null)
                {
                    current++;
                    string prefix = string.IsNullOrEmpty(clothPrefix)
                        ? ExtractClothPrefix(sourceClothAsset.Filename)
                        : clothPrefix;
                    string newClothName = prefix + "_" + ExtractNumbers(newBaseName) + "_cloth";
                    string newClothFull = sourceClothAsset.Path.Replace('\\', '/') + "/" + newClothName;

                    task.Update("Duplicating " + sourceClothAsset.Filename + " (" + current + "/" + total + ")...");

                    newClothAsset = DuplicateWithExtension(sourceClothAsset, newClothFull);
                    if (newClothAsset != null)
                    {
                        oldToNew[sourceClothAsset.Guid] = newClothAsset;
                        oldToNewNames[sourceClothAsset.Name] = newClothAsset.Name;
                        duplicatedSrcGuids.Add(sourceClothAsset.Guid);
                        allNew.Add(newClothAsset);
                        mainNew.Add(newClothAsset);
                        App.Logger.Log("  Duplicated: " + sourceClothAsset.Name + " -> " + newClothAsset.Name);
                    }
                }
            }

            // ── Phase 2.5: Move duplicates into the new body bundle ────────
            if (newBodyBundleId >= 0)
            {
                task.Update("Moving duplicated assets into the new body bundle...");
                MoveAssetsToBundle(allNew, newBodyBundleId);
            }

            // ── Phase 2.6: Cloth bundle membership ──────────────────────────
            // The cloth blueprint bundle and its mesh variation live ONLY in the cloth
            // bundle; the object blueprint, mesh and cloth asset live in BOTH the body
            // bundle and the cloth bundle.
            if (newClothBundleId >= 0)
            {
                if (newClothBb != null)
                {
                    newClothBb.AddedBundles.Clear();
                    newClothBb.AddedBundles.Add(newClothBundleId);
                }
                if (newClothMvdb != null)
                {
                    newClothMvdb.AddedBundles.Clear();
                    newClothMvdb.AddedBundles.Add(newClothBundleId);
                }
                foreach (EbxAssetEntry e in mainNew)
                {
                    AddToBundleRecursive(e, newClothBundleId, new HashSet<AssetEntry>());
                    App.Logger.Log("  " + e.Filename + ": added to cloth bundle " + newClothBundleId);
                }
            }

            // ── Phase 2.7: Copy shared body-bundle members ──────────────────
            // Textures (and any other shared assets) that physically live in the
            // source body bundle but are not duplicated must also exist in the new
            // body bundle so the MVDB's texture references resolve.
            if (newBodyBundleId >= 0 && sourceBodyBb != null)
            {
                task.Update("Copying shared body bundle members...");
                int srcBodyBundleId = GetSourceBundleIdForBb(sourceBodyBb);
                if (srcBodyBundleId >= 0)
                    CopyBundleMembership(srcBodyBundleId, newBodyBundleId, duplicatedSrcGuids);
            }

            // ── Phase 3: Fix references ─────────────────────────────────────
            task.Update("Fixing cross-references...");
            FixCrossReferences(oldToNew, allNew);

            // ── Phase 4: BRT injection ──────────────────────────────────────
            if (!Config.Get<bool>("SkipBrtAdd", false))
            {
                task.Update("Updating BRT entries...");
                Dictionary<string, string> brtPairs = new Dictionary<string, string>();
                if (oldToNewNames.ContainsKey(sourceObj.Name))
                    brtPairs[sourceObj.Name.ToLower()] = oldToNewNames[sourceObj.Name].ToLower();
                InjectBrtPairs(brtPairs, newFolder.ToLower());
            }

            App.Logger.Log("Body duplication complete (" + allNew.Count + " assets)");
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

        // ─── Common-folder texture case ─────────────────────────────────────

        private void DuplicateCommonEntry(EbxAssetEntry entry)
        {
            string sourceFolder = "content/character/body/common";
            string sourceTextureName = entry.Filename;

            // Strip a BRT suffix if the user selected a blueprint-bundle EBX.
            foreach (string suffix in BODY_BRT_SUFFIXES.Concat(ACTOR_BRT_SUFFIXES))
            {
                if (sourceTextureName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                {
                    sourceTextureName = sourceTextureName.Substring(0, sourceTextureName.Length - suffix.Length);
                    break;
                }
            }

            DuplicateBodyWindow win = new DuplicateBodyWindow(sourceFolder, null,
                sourceFolder + "/" + sourceTextureName, sourceTextureName);
            if (win.ShowDialog() != true)
                return;

            string newName = win.NewName;
            string destPath = win.DestinationPath;

            FrostyTaskWindow.Show("Duplicating Body Texture", "", (task) =>
            {
                try
                {
                    DuplicateCommon(task, sourceFolder, sourceTextureName, newName, destPath);
                }
                catch (Exception ex)
                {
                    App.Logger.Log("Error duplicating body texture: " + ex.ToString());
                }
            });

            App.EditorWindow.DataExplorer.RefreshAll();
        }

        private void DuplicateCommon(FrostyTaskWindow task, string sourceFolder,
            string sourceTextureName, string newName, string destPath)
        {
            App.Logger.Log("Common texture source: " + sourceTextureName);
            App.Logger.Log("Common texture target: " + newName);

            task.Update("Finding source assets...");

            List<EbxAssetEntry> allEbx = App.AssetManager.EnumerateEbx().ToList();

            string sourceFull = sourceFolder + "/" + sourceTextureName;
            EbxAssetEntry sourceTexture = allEbx.FirstOrDefault(e =>
                e.Name.Replace('\\', '/').Equals(sourceFull, StringComparison.OrdinalIgnoreCase));

            // Each common texture has two blueprint bundles: <texture>_<body>_brt and
            // <texture>_<actor>_brt.
            string bodySuffix = DetectSuffix(sourceFull, BODY_BRT_SUFFIXES, allEbx, out string bodyBbName);
            string actorSuffix = DetectSuffix(sourceFull, ACTOR_BRT_SUFFIXES, allEbx, out string actorBbName);

            EbxAssetEntry sourceBodyBb = bodyBbName != null ? allEbx.FirstOrDefault(e =>
                IsBlueprintBundle(e)
                && e.Name.Replace('\\', '/').Equals(bodyBbName, StringComparison.OrdinalIgnoreCase)) : null;
            EbxAssetEntry sourceActorBb = actorBbName != null ? allEbx.FirstOrDefault(e =>
                IsBlueprintBundle(e)
                && e.Name.Replace('\\', '/').Equals(actorBbName, StringComparison.OrdinalIgnoreCase)) : null;

            App.Logger.Log("Found " + (sourceTexture != null ? "texture" : "NO texture") +
                ", " + (sourceBodyBb != null ? "1 body bundle" : "NO body bundle") +
                ", " + (sourceActorBb != null ? "1 actor bundle" : "NO actor bundle"));

            if (sourceTexture == null)
            {
                App.Logger.Log("Source texture not found: " + sourceFull);
                return;
            }

            string newFull = destPath.TrimEnd('/') + "/" + newName;

            int current = 0;
            int total = 1 + (sourceBodyBb != null ? 1 : 0) + (sourceActorBb != null ? 1 : 0);

            // Duplicate the texture itself. TextureExtension / AtlasTextureExtension
            // also duplicate its linked res + chunk.
            current++;
            task.Update("Duplicating " + sourceTexture.Filename + " (" + current + "/" + total + ")...");
            EbxAssetEntry newTexture = DuplicateWithExtension(sourceTexture, newFull);
            if (newTexture != null)
                App.Logger.Log("  Duplicated: " + sourceTexture.Name + " -> " + newTexture.Name);

            // Duplicate the two blueprint-bundle EBX files (body + actor).
            EbxAssetEntry newBodyBb = null;
            int newBodyBundleId = -1;
            if (sourceBodyBb != null)
            {
                current++;
                string newBbName = newFull + bodySuffix;
                task.Update("Duplicating " + sourceBodyBb.Filename + " (" + current + "/" + total + ")...");

                newBodyBb = DuplicateWithExtension(sourceBodyBb, newBbName);
                if (newBodyBb != null)
                {
                    if (newBodyBb.AddedBundles.Count > 0)
                        newBodyBundleId = newBodyBb.AddedBundles[0];
                    DuplicationTool.FixBlueprintBundleName(newBodyBb, newBbName);
                    App.Logger.Log("  Duplicated: " + sourceBodyBb.Name + " -> " + newBodyBb.Name);
                }
            }

            EbxAssetEntry newActorBb = null;
            int newActorBundleId = -1;
            if (sourceActorBb != null)
            {
                current++;
                string newBbName = newFull + actorSuffix;
                task.Update("Duplicating " + sourceActorBb.Filename + " (" + current + "/" + total + ")...");

                newActorBb = DuplicateWithExtension(sourceActorBb, newBbName);
                if (newActorBb != null)
                {
                    if (newActorBb.AddedBundles.Count > 0)
                        newActorBundleId = newActorBb.AddedBundles[0];
                    DuplicationTool.FixBlueprintBundleName(newActorBb, newBbName);
                    App.Logger.Log("  Duplicated: " + sourceActorBb.Name + " -> " + newActorBb.Name);
                }
            }

            // The duplicated texture (and its res/chunk) replaces the source texture's
            // membership: put it in the new body/actor bundles when the source texture
            // was a member of the corresponding source bundle.
            if (newTexture != null)
            {
                bool srcInBody = false;
                bool srcInActor = false;
                if (sourceBodyBb != null)
                {
                    int sbb = GetSourceBundleIdForBb(sourceBodyBb);
                    srcInBody = sbb >= 0 && sourceTexture.Bundles.Contains(sbb);
                }
                if (sourceActorBb != null)
                {
                    int sba = GetSourceBundleIdForBb(sourceActorBb);
                    srcInActor = sba >= 0 && sourceTexture.Bundles.Contains(sba);
                }

                ClearBundlesRecursive(newTexture, new HashSet<AssetEntry>());

                bool added = false;
                if (srcInBody && newBodyBundleId >= 0)
                {
                    AddToBundleRecursive(newTexture, newBodyBundleId, new HashSet<AssetEntry>());
                    App.Logger.Log("  " + newTexture.Filename + ": added to new body bundle " + newBodyBundleId);
                    added = true;
                }
                if (srcInActor && newActorBundleId >= 0)
                {
                    AddToBundleRecursive(newTexture, newActorBundleId, new HashSet<AssetEntry>());
                    App.Logger.Log("  " + newTexture.Filename + ": added to new actor bundle " + newActorBundleId);
                    added = true;
                }

                // If the source texture wasn't a member of either blueprint bundle,
                // fall back to both new bundles so the new texture isn't orphaned.
                if (!added)
                {
                    if (newBodyBundleId >= 0)
                        AddToBundleRecursive(newTexture, newBodyBundleId, new HashSet<AssetEntry>());
                    if (newActorBundleId >= 0)
                        AddToBundleRecursive(newTexture, newActorBundleId, new HashSet<AssetEntry>());
                }
            }

            // Register the new texture in the body/actor BRT tables pointing at the
            // new bundles.
            if (!Config.Get<bool>("SkipBrtAdd", false) && newTexture != null)
            {
                task.Update("Updating BRT entries...");
                Dictionary<string, string> brtPairs = new Dictionary<string, string>();
                brtPairs[sourceTexture.Name.ToLower()] = newTexture.Name.ToLower();
                InjectBrtPairs(brtPairs, destPath.TrimEnd('/').ToLower());
            }

            App.Logger.Log("Common texture duplication complete");
        }

        // ─── Bundle membership / re-pointing ───────────────────────────────

        private static int GetSourceBundleIdForBb(EbxAssetEntry sourceBb)
        {
            foreach (BundleEntry be in App.AssetManager.EnumerateBundles())
            {
                if (be.Blueprint != null && be.Blueprint.Guid == sourceBb.Guid)
                    return App.AssetManager.GetBundleId(be);
            }

            return sourceBb.Bundles.Count > 0 ? sourceBb.Bundles[0] : -1;
        }

        private void CopyBundleMembership(int srcBundleId, int dstBundleId,
            HashSet<Guid> duplicatedSrcGuids)
        {
            BundleEntry srcBundle = App.AssetManager.GetBundleEntry(srcBundleId);
            if (srcBundle == null)
            {
                App.Logger.Log("  Could not resolve source bundle id " + srcBundleId + " for membership copy.");
                return;
            }

            HashSet<AssetEntry> visited = new HashSet<AssetEntry>();
            int copied = 0;

            foreach (EbxAssetEntry e in App.AssetManager.EnumerateEbx(srcBundle))
            {
                if (duplicatedSrcGuids.Contains(e.Guid))
                    continue; // it was duplicated; the new copy is already moved
                AddToBundleRecursive(e, dstBundleId, visited);
                copied++;
                App.Logger.Log("  shared member -> new bundle: " + e.Name);
            }

            App.Logger.Log("  Copied " + copied + " shared EBX members into bundle " + dstBundleId);
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

            if (BundleRefTableResource.A_B_TEST_LOOKUPS_AT_SOURCE_REF)
            {
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

        private void ClearBundlesRecursive(AssetEntry entry, HashSet<AssetEntry> visited)
        {
            if (entry == null || !visited.Add(entry))
                return;

            entry.AddedBundles.Clear();

            foreach (AssetEntry linked in entry.LinkedAssets)
                ClearBundlesRecursive(linked, visited);
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
