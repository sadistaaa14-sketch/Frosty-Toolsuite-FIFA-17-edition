using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Frosty.Core.Controls;
using Frosty.Core.Windows;
using FrostySdk;
using FrostySdk.IO;
using FrostySdk.Managers;

namespace Frosty.Core.Bulk
{
    /// <summary>
    /// The shared bulk import engine: the mirror of <see cref="AssetBulkExporter"/>. It reads the files
    /// a run produced (and anything else the user points it at), matches each one to an existing asset
    /// inside the folder being imported into - preferring a name match in that folder over one
    /// elsewhere in the project - and writes into that asset. Nothing is ever created.
    /// </summary>
    public static class AssetBulkImporter
    {
        private static readonly string[] ImageExtensions = { ".dds", ".png", ".tga", ".hdr" };

        /// <summary>
        /// Imports everything the options ask for from <paramref name="inputRoot"/> into the assets
        /// under <paramref name="folderPrefix"/>.
        /// </summary>
        public static BulkResult Run(string folderPrefix, BulkOptions options, string inputRoot, FrostyTaskWindow task)
        {
            BulkResult result = new BulkResult();
            if (options == null || string.IsNullOrEmpty(inputRoot) || !options.AnySelected)
                return result;

            if (!Directory.Exists(inputRoot))
            {
                result.Note("input folder does not exist: " + inputRoot);
                return result;
            }

            string[] files;
            try
            {
                files = Directory.GetFiles(inputRoot, "*", SearchOption.AllDirectories);
                Array.Sort(files, StringComparer.OrdinalIgnoreCase);
            }
            catch (Exception ex)
            {
                result.Note("could not read " + inputRoot + ": " + ex.Message);
                return result;
            }

            if (files.Length == 0)
            {
                result.Note("no files in " + inputRoot);
                return result;
            }

            string match = string.IsNullOrEmpty(folderPrefix) ? "" : folderPrefix + "/";

            // Index the assets of the target folder. A file's bare filename is the key, because that
            // is all an exported file carries; the full name is used first when it matches.
            //
            // Walking the whole asset table is the expensive part of a run, so it is only done when a
            // selected category can match an asset: importing a folder of chunks, for instance, only
            // needs the chunk entries it is handed by name.
            Dictionary<string, EbxAssetEntry> byFullName = new Dictionary<string, EbxAssetEntry>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, EbxAssetEntry> byBareName = new Dictionary<string, EbxAssetEntry>(StringComparer.OrdinalIgnoreCase);
            if (options.AnyEbx || options.Meshes || options.Textures)
            {
                foreach (EbxAssetEntry entry in App.AssetManager.EnumerateEbx())
                {
                    if (!entry.Name.StartsWith(match, StringComparison.OrdinalIgnoreCase))
                        continue;

                    string bare = entry.Filename;
                    if (!string.IsNullOrEmpty(bare) && !byBareName.ContainsKey(bare))
                        byBareName[bare] = entry;

                    if (!byFullName.ContainsKey(entry.Name))
                        byFullName[entry.Name] = entry;
                }

                App.Logger.Log("{0} assets indexed under {1}", byFullName.Count, folderPrefix.Length == 0 ? "[root]" : folderPrefix);
            }

            Dictionary<string, ResAssetEntry> resByName = new Dictionary<string, ResAssetEntry>(StringComparer.OrdinalIgnoreCase);
            if (options.Res)
            {
                foreach (ResAssetEntry entry in App.AssetManager.EnumerateRes())
                {
                    if (!entry.Name.StartsWith(match, StringComparison.OrdinalIgnoreCase))
                        continue;

                    string bare = entry.Filename;
                    if (!string.IsNullOrEmpty(bare) && !resByName.ContainsKey(bare))
                        resByName[bare] = entry;
                }
            }

            int step = 0;
            bool cancelled = false;
            BulkContext.Begin(options);
            try
            {
                foreach (string file in files)
                {
                    // A cancelled run stops between files, so the file being imported is always
                    // finished: an import never leaves an asset half written.
                    if (BulkContext.Cancelled)
                        break;

                    if (task != null)
                        task.Update(file, (step / (double)files.Length) * 100.0);
                    step++;

                    string extension = Path.GetExtension(file).ToLowerInvariant();

                    if (options.AnyEbx && (extension == ".xml" || extension == ".bin"))
                    {
                        bool xml = extension == ".xml";
                        if ((xml && !options.EbxXml) || (!xml && !options.EbxBin))
                            continue;

                        EbxAssetEntry entry = MatchEbx(file, inputRoot, folderPrefix, byFullName, byBareName);
                        if (entry == null)
                        {
                            result.Note("skip " + Path.GetFileName(file) + " (no asset with that name in the folder)");
                            result.Skipped++;
                            continue;
                        }

                        ImportEbx(entry, file, xml ? "xml" : "bin", task, result, mustChangeEbx: true);
                        continue;
                    }

                    if (options.Meshes && extension == ".fbx")
                    {
                        EbxAssetEntry entry = MatchEbx(file, inputRoot, folderPrefix, byFullName, byBareName, IsMeshAsset);
                        if (entry == null)
                        {
                            result.Note("skip " + Path.GetFileName(file) + " (no mesh asset with that name in the folder)");
                            result.Skipped++;
                            continue;
                        }

                        BulkContext.Skeleton = BulkSkeletons.Resolve(options, entry.Path, entry.Name);
                        if (ImportEbx(entry, file, "fbx", task, result, mustChangeEbx: false))
                            result.Meshes++;
                        continue;
                    }

                    if (options.Meshes && extension == ".obj")
                    {
                        // "Export meshes" can write .obj, but only the FBX SDK's own format can be read
                        // back, so say so instead of leaving the file unexplained.
                        result.Note("skip " + Path.GetFileName(file) + " (.obj cannot be imported - the mesh importer reads .fbx)");
                        result.Skipped++;
                        continue;
                    }

                    if (options.Textures && ImageExtensions.Contains(extension))
                    {
                        EbxAssetEntry entry = MatchEbx(file, inputRoot, folderPrefix, byFullName, byBareName, IsTextureAsset);
                        if (entry == null)
                        {
                            result.Note("skip " + Path.GetFileName(file) + " (no texture asset with that name in the folder)");
                            result.Skipped++;
                            continue;
                        }

                        if (ImportEbx(entry, file, extension.Substring(1), task, result, mustChangeEbx: false))
                            result.Textures++;
                        continue;
                    }

                    if (options.Chunks && extension == ".chunk")
                    {
                        ImportChunk(file, result);
                        continue;
                    }

                    if (options.Res && extension == ".res")
                    {
                        ImportRes(file, resByName, result);
                        continue;
                    }
                }
            }
            finally
            {
                // Read before End(): the flag belongs to the run, and End() takes the run down.
                cancelled = BulkContext.Cancelled;
                BulkContext.End();
            }

            if (cancelled)
            {
                result.Cancelled = true;
                result.Note("cancelled - the assets imported before that are already in the project");
            }

            return result;
        }

        /// <summary>
        /// Imports one file into one asset.
        /// </summary>
        /// <param name="entry">The asset to write into.</param>
        /// <param name="file">The file to read.</param>
        /// <param name="filterType">The form the file is in, e.g. "xml", "fbx", "png".</param>
        /// <param name="task">The task window of the run, handed on to the importer (may be null).</param>
        /// <param name="result">What the run has to say about it.</param>
        /// <param name="mustChangeEbx">
        /// True when the data belongs in the ebx itself (the xml and bin forms). An importer can
        /// report success without anything having changed - the xml applier, for instance, says the
        /// file does not describe this asset by logging an error, not by throwing - so for those two
        /// forms an unmodified entry is treated as a failure. Mesh and texture importers write into
        /// the resource or chunk behind the asset instead and only report success once the bytes are
        /// in, so their word is taken as it is.
        /// </param>
        /// <returns>True when the asset was written into.</returns>
        private static bool ImportEbx(EbxAssetEntry entry, string file, string filterType,
            FrostyTaskWindow task, BulkResult result, bool mustChangeEbx)
        {
            try
            {
                AssetDefinition definition = DefinitionFor(entry);
                bool ok = definition.ImportSilent(entry, file, filterType, task);

                if (!ok || (mustChangeEbx && !entry.IsModified))
                {
                    result.Note("failed " + Path.GetFileName(file) + " -> " + entry.Name);
                    result.Failed++;
                    return false;
                }

                result.Done++;
                return true;
            }
            catch (Exception ex)
            {
                result.Note("failed " + Path.GetFileName(file) + " -> " + entry.Name + ": " + ex.Message);
                result.Failed++;
                return false;
            }
        }

        private static AssetDefinition DefinitionFor(EbxAssetEntry entry)
        {
            return App.PluginManager.GetAssetDefinition(entry.Type) ?? new AssetDefinition();
        }

        /// <summary>
        /// True when the asset is a mesh: the same test the exporter uses, so a file is only matched to
        /// an asset that can really read it.
        /// </summary>
        private static bool IsMeshAsset(EbxAssetEntry entry)
        {
            return AssetBulkExporter.CanWriteMesh(DefinitionFor(entry), entry.Type);
        }

        /// <summary>True when the asset is a texture, by the same test the exporter uses.</summary>
        private static bool IsTextureAsset(EbxAssetEntry entry)
        {
            return AssetBulkExporter.CanWriteTexture(DefinitionFor(entry), entry.Type);
        }

        private static void ImportChunk(string file, BulkResult result)
        {
            try
            {
                string bare = Path.GetFileNameWithoutExtension(file);
                if (!Guid.TryParse(bare, out Guid chunkId))
                {
                    result.Note("skip " + Path.GetFileName(file) + " (filename is not a chunk id)");
                    result.Skipped++;
                    return;
                }

                ChunkAssetEntry entry = App.AssetManager.GetChunkEntry(chunkId);
                if (entry == null)
                {
                    result.Note("skip " + Path.GetFileName(file) + " (chunk is not loaded)");
                    result.Skipped++;
                    return;
                }

                byte[] buffer = File.ReadAllBytes(file);
                if (buffer.Length == 0)
                {
                    result.Note("skip " + Path.GetFileName(file) + " (file is empty)");
                    result.Skipped++;
                    return;
                }

                App.AssetManager.ModifyChunk(chunkId, buffer);
                if (!entry.IsModified)
                {
                    result.Note("failed chunk " + chunkId + " (the chunk was not modified)");
                    result.Failed++;
                    return;
                }

                result.Done++;
            }
            catch (Exception ex)
            {
                result.Note("failed " + Path.GetFileName(file) + ": " + ex.Message);
                result.Failed++;
            }
        }

        private static void ImportRes(string file, Dictionary<string, ResAssetEntry> resByName, BulkResult result)
        {
            try
            {
                string bare = Path.GetFileNameWithoutExtension(file);
                if (string.IsNullOrEmpty(bare) || !resByName.TryGetValue(bare, out ResAssetEntry entry))
                {
                    result.Note("skip " + Path.GetFileName(file) + " (no resource with that filename in the folder)");
                    result.Skipped++;
                    return;
                }

                byte[] bytes = File.ReadAllBytes(file);
                if (bytes.Length <= 0x10)
                {
                    // Every .res file starts with a 16 byte header; a shorter file cannot be one, and
                    // writing a truncated header into a live resource would corrupt it.
                    result.Note("skip " + Path.GetFileName(file) + " (too small to be a .res file)");
                    result.Skipped++;
                    return;
                }

                byte[] meta = new byte[0x10];
                Array.Copy(bytes, meta, 0x10);
                byte[] data = new byte[bytes.Length - 0x10];
                Array.Copy(bytes, 0x10, data, 0, data.Length);

                App.AssetManager.ModifyRes(entry.Name, data, meta);
                if (!entry.IsModified)
                {
                    result.Note("failed " + Path.GetFileName(file) + " -> " + entry.Name + " (the resource was not modified)");
                    result.Failed++;
                    return;
                }

                result.Done++;
            }
            catch (Exception ex)
            {
                result.Note("failed " + Path.GetFileName(file) + ": " + ex.Message);
                result.Failed++;
            }
        }

        /// <summary>
        /// Finds the asset a file belongs to. The file's path relative to the input folder is tried as
        /// a full asset name first, so importing into a folder cannot quietly modify an asset that
        /// lives somewhere else, then the bare filename is used.
        /// </summary>
        private static EbxAssetEntry MatchEbx(string file, string inputRoot, string folderPrefix,
            Dictionary<string, EbxAssetEntry> byFullName, Dictionary<string, EbxAssetEntry> byBareName,
            Func<EbxAssetEntry, bool> allowed = null)
        {
            string relative = file;
            if (!string.IsNullOrEmpty(inputRoot) && file.StartsWith(inputRoot, StringComparison.OrdinalIgnoreCase))
                relative = file.Substring(inputRoot.Length).TrimStart('\\', '/');

            string relativeNoExt = relative.Replace('\\', '/');
            int dot = relativeNoExt.LastIndexOf('.');
            if (dot > 0)
                relativeNoExt = relativeNoExt.Substring(0, dot);

            string prefixed = string.IsNullOrEmpty(folderPrefix) ? relativeNoExt : folderPrefix + "/" + relativeNoExt;

            if (byFullName.TryGetValue(prefixed, out EbxAssetEntry entry) && IsAllowed(entry, allowed))
                return entry;

            string bare = Path.GetFileNameWithoutExtension(file);
            if (byBareName.TryGetValue(bare, out entry) && IsAllowed(entry, allowed))
                return entry;

            return null;
        }

        private static bool IsAllowed(EbxAssetEntry entry, Func<EbxAssetEntry, bool> allowed)
        {
            return allowed == null || allowed(entry);
        }
    }
}
