using DuplicationPlugin.Windows;
using Frosty.Controls;
using Frosty.Core;
using Frosty.Core.Windows;
using FrostySdk;
using FrostySdk.IO;
using FrostySdk.Managers;
using FrostySdk.Resources;
using MeshSetPlugin;
using MeshSetPlugin.Resources;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Media;

namespace DuplicationPlugin
{
    /// <summary>
    /// Shared engine for the "Bulk Export ..." tools. Exports every folder of the chosen
    /// type under the selected base folder, preserving the original folder layout (senior
    /// folders such as "player_158000" are kept as intermediate directories). The output
    /// is exactly what "Bulk Import" expects, so it can be round-tripped back in.
    /// </summary>
    internal static class BulkExportRunner
    {
        public static void Run(
            string assetTypeName,
            string brtSuffix,
            string preferredBaseLeaf,
            bool exportMeshes,
            string skeletonLeafName,   // "player_skeleton" / "ball_skeleton" / null (per-folder)
            bool skeletonPerFolder)
        {
            string defaultBase = BulkAssetImportRunner.FindDefaultBase(brtSuffix, preferredBaseLeaf);
            string defaultSkeleton = string.IsNullOrEmpty(skeletonLeafName)
                ? null
                : BulkAssetImportRunner.FindSkeletonByName(skeletonLeafName);

            BulkExportWindow win = new BulkExportWindow(assetTypeName, defaultBase, defaultSkeleton, skeletonPerFolder);
            if (win.ShowDialog() != true)
                return;

            string outputRoot = win.OutputFolder;
            string scopeRoot = win.BaseFolder;
            string overrideSkeleton = win.Skeleton;

            List<string> toExport = BulkAssetImportRunner.GetAssetFolders(scopeRoot, exportMeshes);

            if (toExport.Count == 0)
            {
                FrostyMessageBox.Show("No folders with " + (exportMeshes ? "meshes/textures" : "textures") + " found under " + scopeRoot, "Bulk Export " + assetTypeName);
                return;
            }

            int exported = 0;
            int failed = 0;
            List<string> messages = new List<string>();

            FrostyTaskWindow.Show("Bulk Exporting " + assetTypeName, "", (task) =>
            {
                for (int i = 0; i < toExport.Count; i++)
                {
                    string folder = toExport[i];
                    string leaf = BulkAssetImportRunner.LeafName(folder);
                    task.Update(leaf, (i / (double)toExport.Count) * 100.0);

                    // Preserve the folder layout: keep the path relative to the scope root.
                    string rel = folder.Substring(scopeRoot.Length).Trim('/');
                    if (string.IsNullOrEmpty(rel))
                        rel = leaf;
                    string outDir = Path.Combine(outputRoot, rel);

                    string skeleton = ResolveSkeleton(folder, overrideSkeleton, defaultSkeleton, skeletonPerFolder);

                    try
                    {
                        Directory.CreateDirectory(outDir);
                    }
                    catch (Exception ex)
                    {
                        failed++;
                        messages.Add(leaf + ": " + ex.Message);
                        App.Logger.Log("Bulk export " + assetTypeName + " error on " + leaf + ": " + ex);
                        continue;
                    }

                    ExportFolder(task, folder, outDir, exportMeshes, skeleton, assetTypeName, ref exported, ref failed, messages);
                }
            });

            string report = "Files exported: " + exported + "\n" + "Files failed: " + failed;
            if (messages.Count > 0)
            {
                report += "\n\nFailures:\n" + string.Join("\n", messages.Take(12));
                if (messages.Count > 12)
                    report += "\n... (" + (messages.Count - 12) + " more in the log)";
            }
            App.Logger.Log("Bulk export " + assetTypeName + " complete. " + report.Replace("\n", " | "));
            foreach (string msg in messages)
                App.Logger.Log("  " + msg);

            FrostyMessageBox.Show(report, "Bulk Export " + assetTypeName);
        }

        private static string ResolveSkeleton(string folder, string overrideSkeleton, string defaultSkeleton, bool skeletonPerFolder)
        {
            if (!string.IsNullOrEmpty(overrideSkeleton))
                return overrideSkeleton;
            if (skeletonPerFolder)
                return BulkAssetImportRunner.FindSkeletonInFolder(folder);
            return defaultSkeleton;
        }

        private static void ExportFolder(FrostyTaskWindow task, string folder, string outDir,
            bool exportMeshes, string skeleton, string assetTypeName,
            ref int exported, ref int failed, List<string> messages)
        {
            foreach (EbxAssetEntry e in App.AssetManager.EnumerateEbx())
            {
                if (!e.Path.Replace('\\', '/').Equals(folder, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (BulkAssetImportRunner.IsTexture(e))
                {
                    try
                    {
                        task.Update("Exporting texture " + e.Filename + "...");
                        ExportTexture(e, Path.Combine(outDir, e.Filename + ".png"));
                        exported++;
                    }
                    catch (Exception ex)
                    {
                        failed++;
                        messages.Add(e.Filename + ": " + ex.Message);
                        App.Logger.Log("Bulk export " + assetTypeName + " texture " + e.Name + " failed: " + ex);
                    }
                }
                else if (exportMeshes && BulkAssetImportRunner.IsMesh(e))
                {
                    try
                    {
                        task.Update("Exporting mesh " + e.Filename + "...");
                        ExportMesh(task, e, Path.Combine(outDir, e.Filename + ".fbx"), skeleton);
                        exported++;
                    }
                    catch (Exception ex)
                    {
                        failed++;
                        messages.Add(e.Filename + ": " + ex.Message);
                        App.Logger.Log("Bulk export " + assetTypeName + " mesh " + e.Name + " failed: " + ex);
                    }
                }
            }
        }

        private static void ExportTexture(EbxAssetEntry entry, string outPath)
        {
            EbxAsset asset = App.AssetManager.GetEbx(entry);
            ulong resRid = ((dynamic)asset.RootObject).Resource;
            ResAssetEntry resEntry = App.AssetManager.GetResEntry(resRid);
            if (resEntry == null)
                throw new Exception("no res entry for Resource rid 0x" + resRid.ToString("X"));
            Texture texture = App.AssetManager.GetResAs<Texture>(resEntry);
            if (texture == null)
                throw new Exception("could not load the Texture res for " + entry.Name);

            TexturePlugin.TextureExporter exporter = new TexturePlugin.TextureExporter();
            exporter.Export(texture, outPath, "*.png");
        }

        private static void ExportMesh(FrostyTaskWindow task, EbxAssetEntry entry, string outPath, string skeleton)
        {
            EbxAsset asset = App.AssetManager.GetEbx(entry);
            dynamic root = asset.RootObject;
            ulong resRid = ((dynamic)root).MeshSetResource;
            ResAssetEntry resEntry = App.AssetManager.GetResEntry(resRid);
            if (resEntry == null)
                throw new Exception("no res entry for MeshSetResource rid 0x" + resRid.ToString("X"));
            MeshSet meshSet = App.AssetManager.GetResAs<MeshSet>(resEntry);
            if (meshSet == null)
                throw new Exception("could not load the MeshSet res for " + entry.Name);

            string skel = "";
            if (meshSet.Type == MeshType.MeshType_Skinned)
            {
                skel = skeleton ?? "";
                if (string.IsNullOrEmpty(skel))
                    throw new Exception("skinned mesh has no skeleton selected (pick one in the export window)");
            }

            FBXExporter exporter = new FBXExporter(task);
            exporter.ExportFBX(root, outPath, "2012", "Centimeters", false, false, skel, "binary", meshSet);
        }
    }

    // ─── Menu items ────────────────────────────────────────────────────────────────

    public class BulkExportStarheadsMenuExtension : MenuExtension
    {
        public override string TopLevelMenuName => "Tools";
        public override string SubLevelMenuName => "Bulk Export";
        public override string MenuItemName => "Bulk Export Starheads...";
        public override ImageSource Icon => null;

        public override RelayCommand MenuItemClicked => new RelayCommand((o) =>
        {
            BulkExportRunner.Run("Starheads", "_starhead_brt", "lionel_messi_158023", true, "player_skeleton", false);
        });
    }

    public class BulkExportBallsMenuExtension : MenuExtension
    {
        public override string TopLevelMenuName => "Tools";
        public override string SubLevelMenuName => "Bulk Export";
        public override string MenuItemName => "Bulk Export Balls...";
        public override ImageSource Icon => null;

        public override RelayCommand MenuItemClicked => new RelayCommand((o) =>
        {
            BulkExportRunner.Run("Balls", "_ball_brt", null, true, "ball_skeleton", false);
        });
    }

    public class BulkExportKitsMenuExtension : MenuExtension
    {
        public override string TopLevelMenuName => "Tools";
        public override string SubLevelMenuName => "Bulk Export";
        public override string MenuItemName => "Bulk Export Kits...";
        public override ImageSource Icon => null;

        public override RelayCommand MenuItemClicked => new RelayCommand((o) =>
        {
            BulkExportRunner.Run("Kits", "_kit_brt", null, false, null, false);
        });
    }

    public class BulkExportTrophiesMenuExtension : MenuExtension
    {
        public override string TopLevelMenuName => "Tools";
        public override string SubLevelMenuName => "Bulk Export";
        public override string MenuItemName => "Bulk Export Trophies...";
        public override ImageSource Icon => null;

        public override RelayCommand MenuItemClicked => new RelayCommand((o) =>
        {
            BulkExportRunner.Run("Trophies", "_trophy_brt", null, true, null, true);
        });
    }
}
