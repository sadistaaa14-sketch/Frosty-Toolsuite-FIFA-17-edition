using DuplicationPlugin.Windows;
using Frosty.Controls;
using Frosty.Core;
using Frosty.Core.Viewport;
using Frosty.Core.Windows;
using FrostySdk;
using FrostySdk.IO;
using FrostySdk.Managers;
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
    /// Shared engine for the "Bulk Import ..." tools. The user points at a local root
    /// folder containing one subfolder per asset (each subfolder named the same way as
    /// its in-game folder, e.g. "lionel_messi_158023", and containing .fbx / texture files).
    ///
    /// For every subfolder we:
    ///   1. extract the trailing numeric ID from its name,
    ///   2. look up whether that ID already exists anywhere in the game data,
    ///   3. if not, duplicate the chosen base folder into a new folder of that name, then
    ///   4. import each local file onto the matching asset (texture -> TextureAsset,
    ///      .fbx -> mesh asset).
    /// </summary>
    internal static class BulkAssetImportRunner
    {
        private static readonly string[] ImageExtensions = { ".dds", ".png", ".tga", ".hdr" };

        public static void Run(
            string assetTypeName,
            string brtSuffix,          // e.g. "_starhead_brt" — "_launch..." variant is derived
            string preferredBaseLeaf,  // e.g. "lionel_messi_158023", or null for "first found"
            string skeletonLeafName,   // "player_skeleton" / "ball_skeleton", or null (per-folder / none)
            bool skeletonPerFolder,    // trophies: each folder carries its own skeleton
            bool importMeshes,
            Func<string, string> resolveSkeleton,               // per-folder skeleton (trophies), else null
            Func<string, string, string> resolveTargetParent,   // (id, baseParent) -> senior folder, or null
            Func<string, string> resolveIdentity,               // folder path -> identity key, or null (default: trailing id)
            Action<FrostyTaskWindow, string, string, string> duplicateOne)
        {
            string defaultBase = FindDefaultBase(brtSuffix, preferredBaseLeaf);
            string defaultSkeleton = string.IsNullOrEmpty(skeletonLeafName)
                ? null
                : FindSkeletonByName(skeletonLeafName);

            BulkAssetImportWindow win = new BulkAssetImportWindow(assetTypeName, defaultBase, defaultSkeleton, skeletonPerFolder);
            if (win.ShowDialog() != true)
                return;

            string rootFolder = win.RootFolder;
            string baseFolder = win.BaseFolder;
            string overrideSkeleton = win.Skeleton;

            List<string> items = new List<string>();
            CollectLeafFolders(rootFolder, items);
            if (items.Count == 0)
            {
                FrostyMessageBox.Show("No asset folders (with files) found in " + rootFolder, "Bulk Import " + assetTypeName);
                return;
            }

            Dictionary<string, string> existingById = BuildIdIndex(brtSuffix, resolveIdentity);
            string baseParent = ParentPath(baseFolder);

            BulkImportSummary summary = new BulkImportSummary();

            FrostyTaskWindow.Show("Bulk Importing " + assetTypeName, "", (task) =>
            {
                if (!MeshVariationDb.IsLoaded)
                    MeshVariationDb.LoadVariations(task);

                for (int i = 0; i < items.Count; i++)
                {
                    string localFolder = items[i];
                    string leaf = LeafName(localFolder);
                    string seniorParent = RelativePath(rootFolder, ParentPath(localFolder));
                    task.Update(leaf, (i / (double)items.Count) * 100.0);

                    try
                    {
                        ProcessOne(task, localFolder, leaf, seniorParent, baseFolder, baseParent,
                            existingById, importMeshes, resolveSkeleton, resolveTargetParent,
                            resolveIdentity, defaultSkeleton, overrideSkeleton, skeletonPerFolder,
                            duplicateOne, summary);
                    }
                    catch (Exception ex)
                    {
                        summary.Failed++;
                        summary.Messages.Add(leaf + ": " + ex.Message);
                        App.Logger.Log("Bulk import " + assetTypeName + " error on " + leaf + ": " + ex);
                    }
                }
            });

            App.EditorWindow.DataExplorer.RefreshAll();

            string report =
                "Created (new): " + summary.Created + "\n" +
                "Updated (existing): " + summary.Updated + "\n" +
                "Files imported: " + summary.Imported + "\n" +
                "Skipped: " + summary.Skipped + "\n" +
                "Failed: " + summary.Failed;

            App.Logger.Log("Bulk import " + assetTypeName + " complete. " + report.Replace("\n", " | "));
            foreach (string msg in summary.Messages)
                App.Logger.Log("  " + msg);

            FrostyMessageBox.Show(report, "Bulk Import " + assetTypeName);
        }

        private static void ProcessOne(FrostyTaskWindow task, string localFolder, string leaf,
            string seniorParent, string baseFolder, string baseParent, Dictionary<string, string> existingById,
            bool importMeshes, Func<string, string> resolveSkeleton,
            Func<string, string, string> resolveTargetParent,
            Func<string, string> resolveIdentity,
            string defaultSkeleton, string overrideSkeleton, bool skeletonPerFolder,
            Action<FrostyTaskWindow, string, string, string> duplicateOne,
            BulkImportSummary summary)
        {
            string id = resolveIdentity != null
                ? resolveIdentity(string.IsNullOrEmpty(seniorParent) ? leaf : seniorParent + "/" + leaf)
                : ExtractId(leaf);
            if (string.IsNullOrEmpty(id))
            {
                summary.Skipped++;
                summary.Messages.Add(leaf + ": skipped, folder name doesn't end in a numeric ID (expected name_123456)");
                return;
            }

            // Preserve a nested source layout (e.g. "player_235000/achraf_hakimi_235212");
            // otherwise derive the senior folder from the ID so a new face lands in the
            // correct senior folder instead of the template's parent.
            string targetParent;
            if (!string.IsNullOrEmpty(seniorParent))
                targetParent = ResolveTargetParent(baseParent, seniorParent);
            else if (resolveTargetParent != null)
                targetParent = resolveTargetParent(id, baseParent);
            else
                targetParent = baseParent;

            string targetFolder;
            if (existingById.TryGetValue(id, out targetFolder))
            {
                summary.Updated++;
            }
            else
            {
                task.Update("Duplicating base to " + leaf + "...");
                duplicateOne(task, baseFolder, leaf, targetParent);
                targetFolder = targetParent.TrimEnd('/') + "/" + leaf;
                summary.Created++;
            }

            string skeleton;
            if (!string.IsNullOrEmpty(overrideSkeleton))
                skeleton = overrideSkeleton;
            else if (skeletonPerFolder && resolveSkeleton != null)
                skeleton = resolveSkeleton(targetFolder);
            else
                skeleton = defaultSkeleton;

            ImportFiles(task, targetFolder, localFolder, importMeshes, skeleton, leaf, summary);
        }

        private static void ImportFiles(FrostyTaskWindow task, string targetFolder, string localFolder,
            bool importMeshes, string skeleton, string leaf, BulkImportSummary summary)
        {
            List<EbxAssetEntry> textures = new List<EbxAssetEntry>();
            List<EbxAssetEntry> meshes = new List<EbxAssetEntry>();

            foreach (EbxAssetEntry e in App.AssetManager.EnumerateEbx())
            {
                if (!e.Path.Replace('\\', '/').Equals(targetFolder, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (e.Type == "TextureAsset" || TypeLibrary.IsSubClassOf(e.Type, "TextureAsset"))
                    textures.Add(e);
                else if (importMeshes && (e.Type == "SkinnedMeshAsset" || e.Type == "RigidMeshAsset" || e.Type == "CompositeMeshAsset"))
                    meshes.Add(e);
            }

            string[] files = Directory.GetFiles(localFolder);

            foreach (string img in files.Where(f => ImageExtensions.Contains(Path.GetExtension(f).ToLowerInvariant())))
            {
                EbxAssetEntry match = MatchEntry(img, textures);
                if (match == null)
                {
                    summary.Messages.Add(leaf + ": no texture matches " + Path.GetFileName(img) + " (left unimported)");
                    continue;
                }

                task.Update("Importing texture " + Path.GetFileName(img) + "...");
                bool ok = TexturePlugin.FrostyTextureEditor.ImportTexture2D(match, img, App.Logger, out string err);
                if (ok)
                    summary.Imported++;
                else
                {
                    summary.Failed++;
                    summary.Messages.Add(leaf + ": " + Path.GetFileName(img) + " -> " + match.Name + " failed: " + err);
                }
            }

            if (importMeshes)
            {
                foreach (string fbx in files.Where(f => Path.GetExtension(f).Equals(".fbx", StringComparison.OrdinalIgnoreCase)))
                {
                    EbxAssetEntry match = MatchEntry(fbx, meshes);
                    if (match == null)
                    {
                        summary.Messages.Add(leaf + ": no mesh matches " + Path.GetFileName(fbx) + " (left unimported)");
                        continue;
                    }

                    task.Update("Importing mesh " + Path.GetFileName(fbx) + "...");
                    try
                    {
                        ImportMesh(fbx, match, skeleton);
                        summary.Imported++;
                    }
                    catch (Exception ex)
                    {
                        summary.Failed++;
                        summary.Messages.Add(leaf + ": " + Path.GetFileName(fbx) + " -> " + match.Name + " failed: " + ex.Message);
                    }
                }
            }
        }

        private static void ImportMesh(string fbx, EbxAssetEntry entry, string skeleton)
        {
            EbxAsset asset = App.AssetManager.GetEbx(entry);
            ulong resRid = ((dynamic)asset.RootObject).MeshSetResource;
            ResAssetEntry resEntry = App.AssetManager.GetResEntry(resRid);
            MeshSet meshSet = App.AssetManager.GetResAs<MeshSet>(resEntry);
            if (meshSet == null)
                throw new Exception("could not load the MeshSet res for " + entry.Name);

            FrostyMeshImportSettings settings = null;
            if (meshSet.Type == MeshType.MeshType_Skinned)
            {
                if (string.IsNullOrEmpty(skeleton))
                    throw new Exception("skinned mesh has no skeleton available");
                settings = new FrostyMeshImportSettings { SkeletonAsset = skeleton };
            }

            FBXImporter importer = new FBXImporter(App.Logger);
            importer.ImportFBX(fbx, meshSet, asset, entry, settings);
        }

        private static EbxAssetEntry MatchEntry(string filePath, List<EbxAssetEntry> entries)
        {
            if (entries.Count == 0)
                return null;

            string fileKey = Path.GetFileNameWithoutExtension(filePath);

            EbxAssetEntry match = entries.FirstOrDefault(e => string.Equals(e.Filename, fileKey, StringComparison.OrdinalIgnoreCase));
            if (match != null)
                return match;

            match = entries.FirstOrDefault(e =>
                e.Filename.ToLowerInvariant().Contains(fileKey.ToLowerInvariant()) ||
                fileKey.ToLowerInvariant().Contains(e.Filename.ToLowerInvariant()));
            if (match != null)
                return match;

            return entries.Count == 1 ? entries[0] : null;
        }

        // ─── shared helpers (also used by the bulk export runner) ───────────────

        public static string ExtractId(string leaf)
        {
            int last = leaf.LastIndexOf('_');
            if (last < 0)
                return null;
            string candidate = leaf.Substring(last + 1);
            int dummy;
            return int.TryParse(candidate, out dummy) ? candidate : null;
        }

        /// <summary>
        /// Kit identity key: team id + kit type + variant. Two teams can share the same
        /// kit-type folder name (e.g. both have "home_0_0"), and the kit type digit
        /// (first number) plus the variant (second number) both matter, so the trailing
        /// id alone is not unique. The full triple is.
        /// </summary>
        internal static string KitIdentityFromPath(string path)
        {
            string normalized = (path ?? "").Replace('\\', '/').TrimEnd('/');
            string leaf = LeafName(normalized);
            string team = LeafName(ParentPath(normalized));
            return KitIdentity(team, leaf);
        }

        internal static string KitIdentity(string teamFolder, string kitTypeFolder)
        {
            string teamId = LastNumericToken(teamFolder);
            string kitType = FirstNumericToken(kitTypeFolder);
            string variant = LastNumericToken(kitTypeFolder);
            if (string.IsNullOrEmpty(teamId) || string.IsNullOrEmpty(kitType) || string.IsNullOrEmpty(variant))
                return null;
            return teamId + "_" + kitType + "_" + variant;
        }

        private static string FirstNumericToken(string value)
        {
            if (string.IsNullOrEmpty(value))
                return null;
            foreach (string t in value.Split('_'))
                if (int.TryParse(t, out _))
                    return t;
            return null;
        }

        private static string LastNumericToken(string value)
        {
            if (string.IsNullOrEmpty(value))
                return null;
            string[] parts = value.Split('_');
            for (int i = parts.Length - 1; i >= 0; i--)
                if (int.TryParse(parts[i], out _))
                    return parts[i];
            return null;
        }

        internal static string LeafName(string path)
        {
            int idx = path.Replace('\\', '/').LastIndexOf('/');
            return idx < 0 ? path : path.Substring(idx + 1);
        }

        internal static string ParentPath(string path)
        {
            string normalized = path.Replace('\\', '/').TrimEnd('/');
            int idx = normalized.LastIndexOf('/');
            return idx < 0 ? "" : normalized.Substring(0, idx);
        }

        private static void CollectLeafFolders(string dir, List<string> items)
        {
            foreach (string sub in Directory.GetDirectories(dir))
                CollectLeafFolders(sub, items);

            if (Directory.GetFiles(dir).Length > 0)
                items.Add(dir);
        }

        private static string RelativePath(string root, string path)
        {
            string r = root.Replace('\\', '/').TrimEnd('/');
            string p = path.Replace('\\', '/').TrimEnd('/');
            if (p.Length <= r.Length)
                return "";
            return p.Substring(r.Length).Trim('/');
        }

        private static string ResolveTargetParent(string baseParent, string seniorParent)
        {
            if (string.IsNullOrEmpty(seniorParent))
                return baseParent;

            string[] relParts = seniorParent.Split('/');
            string[] baseParts = baseParent.TrimEnd('/').Split('/');
            if (baseParts.Length < relParts.Length)
                return baseParent;

            string[] newParts = (string[])baseParts.Clone();
            int offset = baseParts.Length - relParts.Length;
            for (int i = 0; i < relParts.Length; i++)
                newParts[offset + i] = relParts[i];
            return string.Join("/", newParts);
        }

        internal static bool IsTexture(EbxAssetEntry e)
        {
            return e.Type == "TextureAsset" || TypeLibrary.IsSubClassOf(e.Type, "TextureAsset");
        }

        internal static bool IsMesh(EbxAssetEntry e)
        {
            return e.Type == "SkinnedMeshAsset" || e.Type == "RigidMeshAsset" || e.Type == "CompositeMeshAsset";
        }

        internal static string FindSkeletonByName(string leafName)
        {
            if (string.IsNullOrEmpty(leafName))
                return null;

            // The game names these "skeleton_player", "skeleton_ball", ... while users
            // describe them as "player_skeleton". Accept both orders.
            List<string> candidates = new List<string> { leafName };
            if (leafName.EndsWith("_skeleton", StringComparison.OrdinalIgnoreCase))
                candidates.Add("skeleton_" + leafName.Substring(0, leafName.Length - "_skeleton".Length));
            else if (leafName.StartsWith("skeleton_", StringComparison.OrdinalIgnoreCase))
                candidates.Add(leafName.Substring("skeleton_".Length) + "_skeleton");

            foreach (EbxAssetEntry e in App.AssetManager.EnumerateEbx("SkeletonAsset"))
            {
                foreach (string candidate in candidates)
                {
                    if (e.Filename.Equals(candidate, StringComparison.OrdinalIgnoreCase))
                        return e.Name;
                }
            }

            // Loose fallback: filename contains the type token and the word "skeleton".
            string token = leafName.Replace("_skeleton", "").Replace("skeleton_", "");
            if (!string.IsNullOrEmpty(token))
            {
                foreach (EbxAssetEntry e in App.AssetManager.EnumerateEbx("SkeletonAsset"))
                {
                    if (e.Filename.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0 &&
                        e.Filename.IndexOf("skeleton", StringComparison.OrdinalIgnoreCase) >= 0)
                        return e.Name;
                }
            }

            return null;
        }

        internal static string FindSkeletonInFolder(string folder)
        {
            if (string.IsNullOrEmpty(folder))
                return null;

            // Preferred: a SkeletonAsset in the folder.
            foreach (EbxAssetEntry e in App.AssetManager.EnumerateEbx("SkeletonAsset"))
            {
                if (e.Path.Replace('\\', '/').Equals(folder, StringComparison.OrdinalIgnoreCase))
                    return e.Name;
            }

            // Fallback: any ebx in the folder whose leaf contains "skeleton".
            foreach (EbxAssetEntry e in App.AssetManager.EnumerateEbx())
            {
                if (e.Path.Replace('\\', '/').Equals(folder, StringComparison.OrdinalIgnoreCase) &&
                    e.Filename.ToLowerInvariant().Contains("skeleton"))
                    return e.Name;
            }

            return null;
        }

        /// <summary>
        /// Given a path that ends in a blueprint-bundle suffix (e.g. "..._starhead_brt" or
        /// "..._launch_starhead_brt"), return the canonical folder path with the suffix
        /// stripped. Returns null if the path does not carry either suffix.
        /// </summary>
        private static string ResolveCanonical(string candidate, string v1, string v2)
        {
            if (string.IsNullOrEmpty(candidate))
                return null;
            // Check the longer "_launch..." variant first so a name like
            // "..._launch_starhead_brt" isn't truncated to "..._launch".
            if (candidate.EndsWith(v2, StringComparison.OrdinalIgnoreCase))
                return candidate.Substring(0, candidate.Length - v2.Length);
            if (candidate.EndsWith(v1, StringComparison.OrdinalIgnoreCase))
                return candidate.Substring(0, candidate.Length - v1.Length);
            return null;
        }

        internal static HashSet<string> GetCanonicalFolders(string brtSuffix)
        {
            string v1 = brtSuffix;
            string v2 = "_launch" + brtSuffix;
            HashSet<string> folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (EbxAssetEntry e in App.AssetManager.EnumerateEbx())
            {
                foreach (string candidate in new[] { e.Path.Replace('\\', '/'), e.Name.Replace('\\', '/') })
                {
                    string canonical = ResolveCanonical(candidate, v1, v2);
                    if (!string.IsNullOrEmpty(canonical))
                        folders.Add(canonical);
                }
            }

            return folders;
        }

        /// <summary>
        /// Returns every folder under <paramref name="scopeRoot"/> that contains at least one
        /// texture (and, when <paramref name="includeMeshes"/> is set, a mesh). This is used by
        /// the export so legacy starheads without a blueprint-bundle BRT entry are still found.
        /// </summary>
        internal static List<string> GetAssetFolders(string scopeRoot, bool includeMeshes)
        {
            HashSet<string> folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (EbxAssetEntry e in App.AssetManager.EnumerateEbx())
            {
                if (!IsTexture(e) && !(includeMeshes && IsMesh(e)))
                    continue;

                string path = e.Path.Replace('\\', '/').TrimEnd('/');
                if (string.IsNullOrEmpty(path))
                    continue;
                folders.Add(path);
            }

            string root = (scopeRoot ?? "").Replace('\\', '/').TrimEnd('/');
            return folders
                .Where(p => p.Equals(root, StringComparison.OrdinalIgnoreCase) ||
                            p.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase))
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        internal static Dictionary<string, string> BuildIdIndex(string brtSuffix, Func<string, string> resolveIdentity = null)
        {
            Dictionary<string, string> byId = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (string canonical in GetCanonicalFolders(brtSuffix))
            {
                string id = resolveIdentity != null ? resolveIdentity(canonical) : ExtractId(LeafName(canonical));
                if (string.IsNullOrEmpty(id))
                    continue;

                if (!byId.ContainsKey(id))
                    byId[id] = canonical;
            }

            return byId;
        }

        internal static string FindDefaultBase(string brtSuffix, string preferredLeaf)
        {
            string v1 = brtSuffix;
            string v2 = "_launch" + brtSuffix;

            foreach (EbxAssetEntry e in App.AssetManager.EnumerateEbx())
            {
                foreach (string candidate in new[] { e.Path.Replace('\\', '/'), e.Name.Replace('\\', '/') })
                {
                    string canonical = ResolveCanonical(candidate, v1, v2);
                    if (string.IsNullOrEmpty(canonical))
                        continue;

                    if (!string.IsNullOrEmpty(preferredLeaf) &&
                        LeafName(canonical).Equals(preferredLeaf, StringComparison.OrdinalIgnoreCase))
                        return canonical;
                }
            }

            if (string.IsNullOrEmpty(preferredLeaf))
            {
                foreach (string canonical in GetCanonicalFolders(brtSuffix))
                    return canonical;
            }

            return null;
        }

        private class BulkImportSummary
        {
            public int Created;
            public int Updated;
            public int Imported;
            public int Skipped;
            public int Failed;
            public List<string> Messages = new List<string>();
        }
    }

    // ─── Menu items ────────────────────────────────────────────────────────────────

    public class BulkImportStarheadsMenuExtension : MenuExtension
    {
        public override string TopLevelMenuName => "Tools";
        public override string SubLevelMenuName => "Bulk Import";
        public override string MenuItemName => "Bulk Import Starheads...";
        public override ImageSource Icon => null;

        public override RelayCommand MenuItemClicked => new RelayCommand((o) =>
        {
            DuplicateStarheadMenuExtension runner = new DuplicateStarheadMenuExtension();
            BulkAssetImportRunner.Run("Starheads", "_starhead_brt", "lionel_messi_158023",
                "player_skeleton", false, true,
                null,
                (id, baseParent) =>
                {
                    if (int.TryParse(id, out int n))
                        return BulkAssetImportRunner.ParentPath(baseParent).TrimEnd('/') + "/player_" + (n - n % 500);
                    return baseParent;
                },
                null,
                (task, src, name, dest) => runner.DuplicateStarhead(task, src, name, dest));
        });
    }

    public class BulkImportBallsMenuExtension : MenuExtension
    {
        public override string TopLevelMenuName => "Tools";
        public override string SubLevelMenuName => "Bulk Import";
        public override string MenuItemName => "Bulk Import Balls...";
        public override ImageSource Icon => null;

        public override RelayCommand MenuItemClicked => new RelayCommand((o) =>
        {
            DuplicateBallMenuExtension runner = new DuplicateBallMenuExtension();
            BulkAssetImportRunner.Run("Balls", "_ball_brt", null,
                "ball_skeleton", false, true,
                null, null, null,
                (task, src, name, dest) => runner.DuplicateBall(task, src, name, dest));
        });
    }

    public class BulkImportKitsMenuExtension : MenuExtension
    {
        public override string TopLevelMenuName => "Tools";
        public override string SubLevelMenuName => "Bulk Import";
        public override string MenuItemName => "Bulk Import Kits...";
        public override ImageSource Icon => null;

        public override RelayCommand MenuItemClicked => new RelayCommand((o) =>
        {
            DuplicateKitMenuExtension runner = new DuplicateKitMenuExtension();
            BulkAssetImportRunner.Run("Kits", "_kit_brt", null,
                null, false, false,
                null, null,
                BulkAssetImportRunner.KitIdentityFromPath,
                (task, src, name, dest) => runner.DuplicateKit(task, src, name, dest));
        });
    }

    public class BulkImportTrophiesMenuExtension : MenuExtension
    {
        public override string TopLevelMenuName => "Tools";
        public override string SubLevelMenuName => "Bulk Import";
        public override string MenuItemName => "Bulk Import Trophies...";
        public override ImageSource Icon => null;

        public override RelayCommand MenuItemClicked => new RelayCommand((o) =>
        {
            DuplicateTrophyMenuExtension runner = new DuplicateTrophyMenuExtension();
            BulkAssetImportRunner.Run("Trophies", "_trophy_brt", null,
                null, true, true,
                folder => BulkAssetImportRunner.FindSkeletonInFolder(folder),
                null, null,
                (task, src, name, dest) => runner.DuplicateTrophy(task, src, name, dest));
        });
    }
}
