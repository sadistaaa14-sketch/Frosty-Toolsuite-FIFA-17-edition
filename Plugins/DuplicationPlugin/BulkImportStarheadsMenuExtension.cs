using Frosty.Controls;
using Frosty.Core;
using Frosty.Core.Viewport;
using Frosty.Core.Windows;
using FrostySdk;
using FrostySdk.Managers;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using System.Windows.Media;

namespace DuplicationPlugin
{
    /// <summary>
    /// "Tools > Bulk Import Starheads": given a local folder containing one subfolder per
    /// player (each subfolder named the same way as an in-game starhead folder, e.g.
    /// "firstname_lastname_123456"), this either:
    ///   - imports the subfolder's texture file(s) directly onto the matching in-game
    ///     starhead, if one already exists for that player ID, or
    ///   - duplicates a single "template" starhead (whichever asset is selected in the
    ///     Data Explorer when this is run -- the same selection the interactive
    ///     "Duplicate Starhead" tool uses) into a new folder for that player ID, via
    ///     DuplicateStarheadMenuExtension.DuplicateStarhead, and then imports onto the
    ///     freshly duplicated copy.
    ///
    /// Only textures (TextureAsset ebx entries, imported via TexturePlugin's
    /// FrostyTextureEditor.ImportTexture2D) are handled. Meshes/hair/cloth are duplicated
    /// along with everything else in the "doesn't exist" case (DuplicateStarhead copies the
    /// whole folder), but this tool does not import mesh files onto them -- that would need
    /// MeshSetPlugin's mesh importer, which is a separate, more involved pipeline.
    /// </summary>
    public class BulkImportStarheadsMenuExtension : MenuExtension
    {
        private static readonly string[] SupportedImageExtensions = { ".dds", ".png", ".tga", ".hdr" };

        public override string TopLevelMenuName => "Tools";
        public override string SubLevelMenuName => null;
        public override string MenuItemName => "Bulk Import Starheads";
        public override ImageSource Icon => null;

        public override RelayCommand MenuItemClicked => new RelayCommand((o) =>
        {
            EbxAssetEntry templateEntry = App.SelectedAsset as EbxAssetEntry;
            if (templateEntry == null)
            {
                FrostyMessageBox.Show(
                    "No asset selected. Select any asset inside an existing player head folder to use " +
                    "as the template for any players that don't exist yet, then run this again.",
                    "Bulk Starhead Importer");
                return;
            }

            string templateFolder = ResolveStarheadFolder(templateEntry);
            if (string.IsNullOrEmpty(templateFolder))
            {
                FrostyMessageBox.Show("Selected asset has no folder path.", "Bulk Starhead Importer");
                return;
            }

            string templateId = DuplicateStarheadMenuExtension.ExtractId(LeafName(templateFolder));
            if (string.IsNullOrEmpty(templateId))
            {
                FrostyMessageBox.Show(
                    "Could not extract a numeric player ID from folder name '" + LeafName(templateFolder) + "'.\n" +
                    "Expected format: firstname_lastname_123456",
                    "Bulk Starhead Importer");
                return;
            }

            FolderBrowserDialog fbd = new FolderBrowserDialog
            {
                Description = "Select the folder containing one subfolder per player " +
                              "(each named e.g. firstname_lastname_123456)."
            };
            if (fbd.ShowDialog() != DialogResult.OK)
                return;

            string[] playerFolders = Directory.GetDirectories(fbd.SelectedPath);
            if (playerFolders.Length == 0)
            {
                FrostyMessageBox.Show("No subfolders found in " + fbd.SelectedPath, "Bulk Starhead Importer");
                return;
            }

            string categoryParent = GetParentPath(templateFolder);
            BulkImportSummary summary = new BulkImportSummary();

            FrostyTaskWindow.Show("Bulk Importing Starheads", "", (task) =>
            {
                if (!MeshVariationDb.IsLoaded)
                    MeshVariationDb.LoadVariations(task);

                Dictionary<string, string> existingByIdInCategory = BuildExistingFolderIndex(categoryParent);

                int current = 0;
                foreach (string localPlayerFolder in playerFolders)
                {
                    current++;
                    string playerFolderName = LeafName(localPlayerFolder);
                    task.Update(playerFolderName, (current / (double)playerFolders.Length) * 100.0);

                    try
                    {
                        ProcessOnePlayer(task, localPlayerFolder, playerFolderName, templateId, templateFolder,
                            categoryParent, existingByIdInCategory, summary);
                    }
                    catch (Exception ex)
                    {
                        summary.Failed++;
                        summary.Messages.Add(playerFolderName + ": " + ex.Message);
                        App.Logger.Log("Bulk starhead import: error on " + playerFolderName + ": " + ex);
                    }
                }
            });

            App.EditorWindow.DataExplorer.RefreshAll();

            string report =
                "Duplicated (new): " + summary.NewlyDuplicated + "\n" +
                "Updated (existing): " + summary.ExistingUpdated + "\n" +
                "Textures imported: " + summary.TexturesImported + "\n" +
                "Skipped: " + summary.Skipped + "\n" +
                "Failed: " + summary.Failed;

            App.Logger.Log("Bulk starhead import complete. " + report.Replace("\n", " | "));
            foreach (string msg in summary.Messages)
                App.Logger.Log("  " + msg);

            FrostyMessageBox.Show(report, "Bulk Starhead Importer");
        });

        // ─── Per-player processing ──────────────────────────────────────────────

        private void ProcessOnePlayer(FrostyTaskWindow task, string localPlayerFolder, string playerFolderName,
            string templateId, string templateFolder, string categoryParent,
            Dictionary<string, string> existingByIdInCategory, BulkImportSummary summary)
        {
            string targetId = DuplicateStarheadMenuExtension.ExtractId(playerFolderName);
            if (string.IsNullOrEmpty(targetId))
            {
                summary.Skipped++;
                summary.Messages.Add(playerFolderName + ": skipped, folder name doesn't end in a numeric ID (expected firstname_lastname_123456)");
                return;
            }

            if (targetId == templateId)
            {
                summary.Skipped++;
                summary.Messages.Add(playerFolderName + ": skipped, same ID as the template selected in the Data Explorer");
                return;
            }

            string targetFolder;
            bool didDuplicate = false;

            if (existingByIdInCategory.TryGetValue(targetId, out string foundFolder))
            {
                targetFolder = foundFolder;
            }
            else
            {
                task.Update("Duplicating " + playerFolderName + "...");
                new DuplicateStarheadMenuExtension().DuplicateStarhead(task, templateFolder, playerFolderName, categoryParent);
                targetFolder = categoryParent.TrimEnd('/') + "/" + playerFolderName;
                didDuplicate = true;
            }

            List<EbxAssetEntry> textureEntries = FindTextureEntries(targetFolder);
            List<string> localImages = Directory.GetFiles(localPlayerFolder)
                .Where(f => SupportedImageExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
                .ToList();

            if (didDuplicate)
                summary.NewlyDuplicated++;
            else
                summary.ExistingUpdated++;

            if (textureEntries.Count == 0)
            {
                summary.Messages.Add(playerFolderName + ": no TextureAsset found in " + targetFolder + " to import onto");
                return;
            }

            if (localImages.Count == 0)
            {
                summary.Messages.Add(playerFolderName + ": no .dds/.png/.tga/.hdr files found in " + localPlayerFolder);
                return;
            }

            List<Tuple<EbxAssetEntry, string>> pairs = MatchFilesToTextures(localImages, textureEntries, playerFolderName, summary);

            foreach (Tuple<EbxAssetEntry, string> pair in pairs)
            {
                bool ok = TexturePlugin.FrostyTextureEditor.ImportTexture2D(pair.Item1, pair.Item2, App.Logger, out string err);
                if (ok)
                    summary.TexturesImported++;
                else
                {
                    summary.Failed++;
                    summary.Messages.Add(playerFolderName + ": " + Path.GetFileName(pair.Item2) + " -> " + pair.Item1.Name + " failed: " + err);
                }
            }
        }

        // ─── Matching local files to TextureAsset entries ──────────────────────

        private List<Tuple<EbxAssetEntry, string>> MatchFilesToTextures(List<string> localImages,
            List<EbxAssetEntry> textureEntries, string playerFolderName, BulkImportSummary summary)
        {
            List<Tuple<EbxAssetEntry, string>> result = new List<Tuple<EbxAssetEntry, string>>();
            List<string> remainingFiles = new List<string>(localImages);
            List<EbxAssetEntry> remainingEntries = new List<EbxAssetEntry>(textureEntries);

            // Pass 1: exact match on filename (no extension), case-insensitive
            for (int i = remainingFiles.Count - 1; i >= 0; i--)
            {
                string fileKey = Path.GetFileNameWithoutExtension(remainingFiles[i]);
                EbxAssetEntry match = remainingEntries.FirstOrDefault(
                    e => string.Equals(e.Filename, fileKey, StringComparison.OrdinalIgnoreCase));

                if (match != null)
                {
                    result.Add(Tuple.Create(match, remainingFiles[i]));
                    remainingEntries.Remove(match);
                    remainingFiles.RemoveAt(i);
                }
            }

            // Pass 2: one name contains the other, case-insensitive
            for (int i = remainingFiles.Count - 1; i >= 0; i--)
            {
                string fileKey = Path.GetFileNameWithoutExtension(remainingFiles[i]).ToLowerInvariant();
                EbxAssetEntry match = remainingEntries.FirstOrDefault(e =>
                    e.Filename.ToLowerInvariant().Contains(fileKey) || fileKey.Contains(e.Filename.ToLowerInvariant()));

                if (match != null)
                {
                    result.Add(Tuple.Create(match, remainingFiles[i]));
                    remainingEntries.Remove(match);
                    remainingFiles.RemoveAt(i);
                }
            }

            // Pass 3: exactly one of each left over -> pair positionally
            if (remainingFiles.Count == 1 && remainingEntries.Count == 1)
            {
                result.Add(Tuple.Create(remainingEntries[0], remainingFiles[0]));
                remainingEntries.Clear();
                remainingFiles.Clear();
            }

            foreach (string leftover in remainingFiles)
                summary.Messages.Add(playerFolderName + ": couldn't match file '" + Path.GetFileName(leftover) + "' to a texture (imported nothing for it)");
            foreach (EbxAssetEntry leftover in remainingEntries)
                summary.Messages.Add(playerFolderName + ": couldn't match texture '" + leftover.Filename + "' to a file (left unchanged)");

            return result;
        }

        // ─── Folder / index helpers ─────────────────────────────────────────────

        private static string ResolveStarheadFolder(EbxAssetEntry entry)
        {
            string folder = entry.Path.Replace('\\', '/');
            if (string.IsNullOrEmpty(folder))
                return null;

            if (folder.EndsWith("_launch_starhead_brt", StringComparison.OrdinalIgnoreCase))
                folder = folder.Substring(0, folder.Length - "_launch_starhead_brt".Length);
            else if (folder.EndsWith("_starhead_brt", StringComparison.OrdinalIgnoreCase))
                folder = folder.Substring(0, folder.Length - "_starhead_brt".Length);

            return folder;
        }

        private static string LeafName(string path)
        {
            int idx = path.Replace('\\', '/').LastIndexOf('/');
            return idx < 0 ? path : path.Substring(idx + 1);
        }

        private static string GetParentPath(string path)
        {
            string normalized = path.Replace('\\', '/').TrimEnd('/');
            int idx = normalized.LastIndexOf('/');
            return idx < 0 ? "" : normalized.Substring(0, idx);
        }

        /// <summary>
        /// One pass over every ebx asset, mapping each existing starhead's numeric ID to
        /// its canonical folder path, scoped to direct children of categoryParent (i.e.
        /// siblings of the chosen template) so an unrelated folder elsewhere that happens
        /// to end in the same number can't be mistaken for an existing starhead.
        /// </summary>
        private static Dictionary<string, string> BuildExistingFolderIndex(string categoryParent)
        {
            Dictionary<string, string> byId = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (EbxAssetEntry e in App.AssetManager.EnumerateEbx())
            {
                string canonical = ResolveStarheadFolder(e);
                if (string.IsNullOrEmpty(canonical))
                    continue;

                string parent = GetParentPath(canonical);
                if (!parent.Equals(categoryParent, StringComparison.OrdinalIgnoreCase))
                    continue;

                string leaf = LeafName(canonical);
                string id = DuplicateStarheadMenuExtension.ExtractId(leaf);
                if (string.IsNullOrEmpty(id))
                    continue;

                if (!byId.ContainsKey(id))
                    byId[id] = canonical;
            }

            return byId;
        }

        /// <summary>
        /// Finds every TextureAsset ebx entry directly inside targetFolder or its
        /// "_starhead_brt"/"_launch_starhead_brt" sibling.
        /// </summary>
        private static List<EbxAssetEntry> FindTextureEntries(string targetFolder)
        {
            HashSet<string> candidateFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                targetFolder,
                targetFolder + "_starhead_brt",
                targetFolder + "_launch_starhead_brt"
            };

            List<EbxAssetEntry> result = new List<EbxAssetEntry>();
            foreach (EbxAssetEntry e in App.AssetManager.EnumerateEbx())
            {
                string path = e.Path.Replace('\\', '/');
                if (!candidateFolders.Contains(path))
                    continue;

                if (e.Type == "TextureAsset" || TypeLibrary.IsSubClassOf(e.Type, "TextureAsset"))
                    result.Add(e);
            }
            return result;
        }

        private class BulkImportSummary
        {
            public int NewlyDuplicated;
            public int ExistingUpdated;
            public int TexturesImported;
            public int Skipped;
            public int Failed;
            public List<string> Messages = new List<string>();
        }
    }
}
