using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Frosty.Core.Controls;
using Frosty.Core.Windows;
using FrostySdk;
using FrostySdk.Interfaces;
using FrostySdk.IO;
using FrostySdk.Managers;

namespace Frosty.Core.Bulk
{
    /// <summary>What a bulk run did, in the numbers the log and the summary report use.</summary>
    public sealed class BulkResult
    {
        /// <summary>Items written or imported successfully.</summary>
        public int Done;

        /// <summary>Meshes written or imported, so "did my meshes go out?" is answered in one line.</summary>
        public int Meshes;

        /// <summary>Textures written or imported.</summary>
        public int Textures;

        /// <summary>
        /// Chunks written or imported. Kept apart from <see cref="Done"/> so that a run that was
        /// asked for chunks and wrote none says so in one line instead of being counted in with the
        /// resources it did write.
        /// </summary>
        public int Chunks;

        /// <summary>Items that had nothing to do (no matching asset, unsupported type, ...).</summary>
        public int Skipped;

        /// <summary>Items that threw or whose result did not land.</summary>
        public int Failed;

        /// <summary>
        /// True when the run was stopped from its task window before it got through its scope. What
        /// was written before that is on disk; the numbers above only cover the part that ran.
        /// </summary>
        public bool Cancelled;

        /// <summary>Per item notes, bounded so a huge run cannot flood the log.</summary>
        public readonly List<string> Messages = new List<string>();

        /// <summary>
        /// Per item detail kept for the run report rather than the log window: what the chunk lookup
        /// found for every asset it looked at, and the ids it could not find. The log only shows the
        /// first few notes and is gone when the editor closes, so the answers a run without chunks
        /// has to give are written to a file beside the output instead.
        /// </summary>
        public readonly List<string> Details = new List<string>();

        /// <summary>Records one line of per item detail, bounded like the notes are.</summary>
        internal void Detail(string line)
        {
            if (Details.Count < 5000)
                Details.Add(line);
            else if (Details.Count == 5000)
                Details.Add("... further detail suppressed");
        }

        internal void Note(string message)
        {
            if (Messages.Count < 200)
                Messages.Add(message);
            else if (Messages.Count == 200)
                Messages.Add("... further notes suppressed");
        }

        /// <summary>
        /// A one line summary, e.g. "142 done, 3 skipped, 0 failed". Meshes and textures are called
        /// out separately, because a run that writes no mesh or texture is the thing most likely to
        /// look like it did nothing at all.
        /// </summary>
        public string Describe()
        {
            string summary = Done + " done, " + Skipped + " skipped, " + Failed + " failed";
            if (Meshes != 0)
                summary += ", " + Meshes + " meshes";
            if (Textures != 0)
                summary += ", " + Textures + " textures";
            if (Chunks != 0)
                summary += ", " + Chunks + " chunks";
            if (Cancelled)
                summary += ", cancelled";
            return summary;
        }
    }

    /// <summary>
    /// The shared bulk export engine. It is driven by a <see cref="BulkOptions"/> selection and works
    /// on one folder of the asset tree at a time, writing ebx (as xml and/or bin), meshes, textures,
    /// chunks and resources while preserving the folder layout.
    /// </summary>
    public static class AssetBulkExporter
    {
        // The type names are only a fallback: what a category means is decided by the formats an
        // asset type can actually write, so a mesh or texture type from any plugin - or any subclass
        // of one - is picked up without a list of names that has to be kept in step with the game.
        private static readonly string[] MeshTypes = { "SkinnedMeshAsset", "RigidMeshAsset", "CompositeMeshAsset" };
        private static readonly string[] TextureTypes = { "TextureAsset", "AtlasTextureAsset" };

        /// <summary>What makes an asset type a mesh: it can write one of these.</summary>
        private static readonly string[] MeshFileFormats = { "fbx", "obj" };

        /// <summary>What makes an asset type a texture, and the formats a run offers to write.</summary>
        private static readonly string[] TextureFileFormats = { "png", "tga", "dds", "hdr" };

        /// <summary>The mesh formats the dialog offers: "fbx" and "fbx-ascii" are both .fbx files.</summary>
        private static readonly string[] MeshFormatChoices = { "fbx", "fbx-ascii", "obj" };

        /// <summary>
        /// Exports everything the options ask for from <paramref name="folderPrefix"/> downwards.
        /// </summary>
        /// <param name="folderPrefix">Folder to export, "" for the whole tree, null when not folder scoped.</param>
        /// <param name="options">What to export.</param>
        /// <param name="outputRoot">Where to write.</param>
        /// <param name="task">The task window to report progress to (may be null).</param>
        /// <param name="chunkScope">
        /// Chunks to export instead of the ones belonging to the folder. Used by the Chunk/Res Editor,
        /// where the scope is the filtered chunk list rather than a folder.
        /// </param>
        /// <param name="resScope">Resources to export instead of the ones under the folder.</param>
        public static BulkResult Run(string folderPrefix, BulkOptions options, string outputRoot,
            FrostyTaskWindow task, IEnumerable<ChunkAssetEntry> chunkScope = null, IEnumerable<ResAssetEntry> resScope = null)
        {
            BulkResult result = new BulkResult();
            if (options == null || outputRoot == null || !options.AnySelected)
                return result;

            string match = string.IsNullOrEmpty(folderPrefix) ? "" : folderPrefix + "/";

            // Walking the whole asset table is the expensive part of a run, so it is only done when
            // one of the selected categories needs it: a chunk list export, for instance, already
            // knows its chunks and never touches the assets.
            List<EbxAssetEntry> assets = new List<EbxAssetEntry>();
            if (options.AnyEbx || options.Meshes || options.Textures || (options.Chunks && chunkScope == null))
            {
                foreach (EbxAssetEntry entry in App.AssetManager.EnumerateEbx())
                {
                    if (entry.Name.StartsWith(match, StringComparison.OrdinalIgnoreCase))
                        assets.Add(entry);
                }
            }

            int total = assets.Count;
            int step = 0;
            int meshCandidates = 0;
            int textureCandidates = 0;
            bool cancelled = false;

            BulkContext.Begin(options);
            try
            {
                foreach (EbxAssetEntry entry in assets)
                {
                    // A cancelled run stops between assets, so the asset being worked on is always
                    // finished - a half written file is never left for the next import to pick up.
                    if (BulkContext.Cancelled)
                        break;

                    if (task != null)
                        task.Update(entry.Name, total == 0 ? 0.0 : (step / (double)total) * 100.0);
                    step++;

                    string relative = RelativeName(entry.Name, match);
                    AssetDefinition definition = App.PluginManager.GetAssetDefinition(entry.Type) ?? new AssetDefinition();

                    // Meshes and textures are separate categories: a mesh asset can be exported as
                    // xml *and* as fbx in the same run, which is what the two check boxes describe.
                    bool isMesh = options.Meshes && CanWriteMesh(definition, entry.Type);
                    bool isTexture = options.Textures && CanWriteTexture(definition, entry.Type);

                    if (isMesh)
                        meshCandidates++;
                    if (isTexture)
                        textureCandidates++;

                    // The skeleton is resolved per asset so that a folder of player meshes exports
                    // with the player skeleton even though it is stated nowhere in the options.
                    BulkContext.Skeleton = isMesh ? BulkSkeletons.Resolve(options, entry.Path, entry.Name) : null;

                    if (options.AnyEbx)
                    {
                        if (options.EbxXml)
                            ExportOne(entry, definition, Relative(outputRoot, relative, ".xml"), "xml", task, result, allowCustomHandler: true);

                        if (options.EbxBin)
                            ExportOne(entry, definition, Relative(outputRoot, relative, ".bin"), "bin", task, result, allowCustomHandler: false);
                    }

                    if (isMesh)
                    {
                        if (string.IsNullOrEmpty(BulkContext.Skeleton) && entry.Type == "SkinnedMeshAsset")
                            result.Note("mesh " + entry.Name + ": no skeleton could be worked out for this folder");

                        // A type that only offers one mesh format is written as that format rather than
                        // skipped, so "export meshes" does something for every mesh it finds.
                        string meshFormat = ChooseFormat(SupportedFormats(definition), MeshFormatChoices, options.MeshFormat);
                        if (ExportOne(entry, definition, Relative(outputRoot, relative, "." + FormatExtension(meshFormat)),
                            meshFormat, task, result, allowCustomHandler: true))
                        {
                            result.Meshes++;
                        }
                    }

                    if (isTexture)
                    {
                        // Every format the user ticked is written, so one run can produce a .dds to
                        // feed back in and a .png to look at.
                        foreach (string textureFormat in ChosenTextureFormats(options, definition))
                        {
                            if (ExportOne(entry, definition, Relative(outputRoot, relative, "." + textureFormat),
                                textureFormat, task, result, allowCustomHandler: true))
                            {
                                result.Textures++;
                            }
                        }
                    }
                }

                // Say so when a ticked category had nothing to work on, instead of leaving the user to
                // wonder why the folder came out without any meshes or textures in it. A cancelled run
                // says nothing here: it never got through the folder, so "no meshes at all" would be a
                // statement about the part it happened to reach.
                if (!BulkContext.Cancelled)
                {
                    string scope = folderPrefix.Length == 0 ? "[root]" : folderPrefix;
                    if (options.Meshes && meshCandidates == 0)
                        result.Note("no mesh assets at all under " + scope + " - nothing was written as a mesh");
                    if (options.Textures && textureCandidates == 0)
                        result.Note("no texture assets at all under " + scope + " - nothing was written as a texture");
                }

                if (options.Res)
                    ExportRes(folderPrefix, match, options, outputRoot, task, resScope, result);

                if (options.Chunks)
                    ExportChunks(folderPrefix, match, options, outputRoot, task, assets, chunkScope, result);
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
                result.Note("cancelled - what was written before that is on disk");
            }

            return result;
        }

        /// <summary>
        /// Exports one asset in one format and reports the outcome in <paramref name="result"/>.
        /// </summary>
        /// <param name="entry">The asset to export.</param>
        /// <param name="definition">The definition that knows how to write it.</param>
        /// <param name="outPath">Where the file should land.</param>
        /// <param name="filterType">The format to write, e.g. "xml", "fbx-ascii", "png".</param>
        /// <param name="task">
        /// The task window of the run. It is handed on to the exporter: the mesh exporter reports its
        /// progress through it and throws on a null, which is what used to make a whole folder of
        /// meshes come out empty.
        /// </param>
        /// <param name="result">What the run has to say about it.</param>
        /// <param name="allowCustomHandler">
        /// False only for raw ebx, which cannot represent a type the game reads through a handler.
        /// </param>
        /// <returns>True when a file was written.</returns>
        private static bool ExportOne(EbxAssetEntry entry, AssetDefinition definition, string outPath, string filterType,
            FrostyTaskWindow task, BulkResult result, bool allowCustomHandler)
        {
            try
            {
                if (!allowCustomHandler && App.PluginManager.GetCustomHandler(entry.Type) != null)
                {
                    // Raw ebx cannot represent a type the game reads through a handler.
                    result.Note("skip " + entry.Name + " (" + entry.Type + " cannot be written as .bin)");
                    result.Skipped++;
                    return false;
                }

                string directory = Path.GetDirectoryName(outPath);
                if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                    Directory.CreateDirectory(directory);

                bool reported = definition.ExportSilent(entry, outPath, filterType, task);

                // The files on disk are the evidence, not the return value: some exporters report
                // success without writing anything and a few report failure after writing a perfectly
                // good file, so the answer only colours the log line.
                if (!WroteSomething(outPath))
                {
                    result.Note("failed " + entry.Name + " as " + filterType
                        + (reported ? " (the exporter reported success but wrote nothing)" : ""));
                    result.Failed++;
                    TryDelete(outPath);
                    return false;
                }

                if (!reported)
                    result.Note("exported " + entry.Name + " as " + filterType + " (the exporter reported failure, but the file is there)");

                result.Done++;
                return true;
            }
            catch (Exception ex)
            {
                result.Note("failed " + entry.Name + " as " + filterType + ": " + ex.Message);
                result.Failed++;
                TryDelete(outPath);
                return false;
            }
        }

        /// <summary>
        /// True when an export left something behind: the file that was asked for, or one of the files
        /// some exporters write beside it - cube and array textures write "&lt;name&gt;_px.png",
        /// "&lt;name&gt;_000.png" and so on instead of the target itself.
        ///
        /// The suffix is matched against the two shapes an exporter actually writes, not against any
        /// name that merely starts the same way: assets called "kit_0" and "kit_0_normal" are ordinary
        /// names, and treating the second one's file as evidence for the first would report a failed
        /// export as done. Only the texture exporters add such files, and only as faces or numbered
        /// slices.
        /// </summary>
        public static bool WroteSomething(string outPath)
        {
            if (Wrote(outPath))
                return true;

            string directory = Path.GetDirectoryName(outPath);
            string stem = Path.GetFileNameWithoutExtension(outPath);
            string extension = Path.GetExtension(outPath);
            if (string.IsNullOrEmpty(directory) || string.IsNullOrEmpty(stem) || !Directory.Exists(directory))
                return false;

            foreach (string candidate in Directory.GetFiles(directory, stem + "_*" + extension))
            {
                if (IsSiblingOutput(candidate, stem, extension) && Wrote(candidate))
                    return true;
            }

            return false;
        }

        /// <summary>The faces a cubemap is written as, in the order the exporter names them.</summary>
        private static readonly string[] CubeFaces = { "px", "nx", "py", "ny", "pz", "nz" };

        /// <summary>
        /// True when a file beside the target is really part of this export: a cube face, or a numbered
        /// array slice (the exporter writes at least three digits - `<c>i:D3</c> - so "name_0.png" is a
        /// different asset, not a slice of "name").
        /// </summary>
        private static bool IsSiblingOutput(string path, string stem, string extension)
        {
            string name = Path.GetFileName(path);
            string prefix = stem + "_";
            if (name.Length <= prefix.Length + extension.Length ||
                !name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
                !name.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            string suffix = name.Substring(prefix.Length, name.Length - prefix.Length - extension.Length);
            if (CubeFaces.Contains(suffix, StringComparer.OrdinalIgnoreCase))
                return true;

            return suffix.Length >= 3 && suffix.All(char.IsDigit);
        }

        private static bool Wrote(string path)
        {
            return File.Exists(path) && new FileInfo(path).Length != 0;
        }

        /// <summary>
        /// The formats an asset type can be written as, straight from its own definition - which is
        /// what decides whether it counts as a mesh or a texture, instead of a list of type names to
        /// keep in step with the game.
        /// </summary>
        public static List<AssetExportType> SupportedFormats(AssetDefinition definition)
        {
            List<AssetExportType> formats = new List<AssetExportType>();
            try
            {
                definition.GetSupportedExportTypes(formats);
            }
            catch (Exception ex)
            {
                App.Logger.LogError("Could not ask {0} what it can export: {1}", definition.GetType().Name, ex.Message);
            }

            return formats;
        }

        /// <summary>True when this asset type can be written as a model file (fbx or obj).</summary>
        public static bool CanWriteMesh(AssetDefinition definition, string typeName)
        {
            List<AssetExportType> formats = SupportedFormats(definition);
            if (formats.Any(f => MeshFileFormats.Contains(f.Extension, StringComparer.OrdinalIgnoreCase)))
                return true;

            // A type the game reads through a handler can still be a mesh: the type name is a fallback
            // so those are attempted and reported instead of being passed over in silence.
            if (string.IsNullOrEmpty(typeName))
                return false;

            return MeshTypes.Contains(typeName) || IsTypeOrSubclass(typeName, "MeshAsset");
        }

        /// <summary>
        /// True when the type is (or derives from) the named type. The type library can only be read
        /// once a game is loaded, so an answer it cannot give is treated as "no": the formats an asset
        /// type declares are what decides the common case, and this is only the fallback.
        /// </summary>
        private static bool IsTypeOrSubclass(string typeName, string name)
        {
            try
            {
                return TypeLibrary.IsSubClassOf(typeName, name);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>True when this asset type can be written as an image file.</summary>
        public static bool CanWriteTexture(AssetDefinition definition, string typeName)
        {
            List<AssetExportType> formats = SupportedFormats(definition);
            if (formats.Any(f => TextureFileFormats.Contains(f.Extension, StringComparer.OrdinalIgnoreCase)))
                return true;

            if (string.IsNullOrEmpty(typeName))
                return false;

            return TextureTypes.Contains(typeName)
                || IsTypeOrSubclass(typeName, "TextureBaseAsset")
                || IsTypeOrSubclass(typeName, "TextureAsset")
                || IsTypeOrSubclass(typeName, "AtlasTextureAsset");
        }

        /// <summary>
        /// The file extension a format writes. "fbx-ascii" is still an .fbx file; every other format
        /// is its own name.
        /// </summary>
        public static string FormatExtension(string format)
        {
            if (string.IsNullOrEmpty(format))
                return "";

            int dash = format.IndexOf('-');
            return dash < 0 ? format : format.Substring(0, dash);
        }

        /// <summary>
        /// Picks the format to write: the one the user asked for when the asset type can write that
        /// kind of file, otherwise the first one of that kind it can, so a type that offers only one
        /// form is written as that form rather than skipped.
        /// </summary>
        public static string ChooseFormat(List<AssetExportType> formats, string[] choices, string wanted)
        {
            if (string.IsNullOrEmpty(wanted))
                return choices[0];

            if (formats.Any(f => f.Extension.Equals(FormatExtension(wanted), StringComparison.OrdinalIgnoreCase)))
                return wanted;

            foreach (string choice in choices)
            {
                if (formats.Any(f => f.Extension.Equals(FormatExtension(choice), StringComparison.OrdinalIgnoreCase)))
                    return choice;
            }

            return wanted;
        }

        /// <summary>
        /// The texture formats to write for one asset: every format the user ticked that this asset
        /// type can actually write. When it can write none of them the first image format it does
        /// support is used - and failing that the user's own first choice - so the attempt is still
        /// made and reported instead of the texture being quietly left out.
        /// </summary>
        public static List<string> ChosenTextureFormats(BulkOptions options, AssetDefinition definition)
        {
            List<string> requested = new List<string>();
            if (options != null && options.TextureFormats != null)
            {
                foreach (string format in options.TextureFormats)
                {
                    if (!string.IsNullOrEmpty(format))
                        requested.Add(format.ToLowerInvariant());
                }
            }

            if (requested.Count == 0)
                requested.Add("dds");

            List<AssetExportType> formats = SupportedFormats(definition);
            List<string> writable = new List<string>();
            foreach (string format in requested)
            {
                if (!writable.Contains(format) &&
                    formats.Any(f => f.Extension.Equals(format, StringComparison.OrdinalIgnoreCase)))
                {
                    writable.Add(format);
                }
            }

            if (writable.Count != 0)
                return writable;

            foreach (string candidate in TextureFileFormats)
            {
                if (formats.Any(f => f.Extension.Equals(candidate, StringComparison.OrdinalIgnoreCase)))
                    return new List<string> { candidate };
            }

            return new List<string> { requested[0] };
        }

        private static void ExportRes(string folderPrefix, string match, BulkOptions options, string outputRoot,
            FrostyTaskWindow task, IEnumerable<ResAssetEntry> resScope, BulkResult result)
        {
            List<ResAssetEntry> entries = resScope != null
                ? resScope.ToList()
                : App.AssetManager.EnumerateRes()
                    .Where(e => e.Name.StartsWith(match, StringComparison.OrdinalIgnoreCase))
                    .ToList();

            int step = 0;
            foreach (ResAssetEntry entry in entries)
            {
                if (BulkContext.Cancelled)
                    break;

                if (task != null)
                    task.Update(entry.Name, entries.Count == 0 ? 0.0 : (step / (double)entries.Count) * 100.0);
                step++;

                try
                {
                    Stream resStream = App.AssetManager.GetRes(entry);
                    if (resStream == null)
                    {
                        result.Note("skip res " + entry.Name + " (could not be read)");
                        result.Skipped++;
                        continue;
                    }

                    string relative = RelativeName(entry.Name, match);
                    string outPath = Relative(outputRoot, relative, ".res");
                    string directory = Path.GetDirectoryName(outPath);
                    if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                        Directory.CreateDirectory(directory);

                    using (NativeWriter writer = new NativeWriter(new FileStream(outPath, FileMode.Create)))
                    {
                        // 16 bytes of resource header followed by the data - the same layout the
                        // Chunk/Res Editor writes for a single resource, so bulk import can read it.
                        byte[] meta = entry.ResMeta ?? new byte[0x10];
                        byte[] header = new byte[0x10];
                        Array.Copy(meta, header, Math.Min(meta.Length, header.Length));
                        writer.Write(header);
                        writer.Write(new NativeReader(resStream).ReadToEnd());
                    }

                    result.Done++;
                }
                catch (Exception ex)
                {
                    result.Note("failed res " + entry.Name + ": " + ex.Message);
                    result.Failed++;
                }
            }
        }

        private static void ExportChunks(string folderPrefix, string match, BulkOptions options, string outputRoot,
            FrostyTaskWindow task, List<EbxAssetEntry> assets, IEnumerable<ChunkAssetEntry> chunkScope, BulkResult result)
        {
            List<ChunkAssetEntry> chunks = new List<ChunkAssetEntry>();
            HashSet<Guid> seen = new HashSet<Guid>();

            if (chunkScope != null)
            {
                // The Chunk/Res Editor hands over the chunks the user is looking at.
                foreach (ChunkAssetEntry chunk in chunkScope)
                {
                    if (chunk != null && seen.Add(chunk.Id))
                        chunks.Add(chunk);
                }
            }
            else
            {
                // A folder of assets is exported together with the chunks that hold their bytes:
                // an ebx asset keeps its payload in the resource it points at, not in a chunk of
                // its own, so the ids have to be resolved through the asset references.
                int resolved = 0;
                int unexplained = 0;
                foreach (EbxAssetEntry entry in assets)
                {
                    // Resolving an asset's chunks walks its object graph, so this is where a cancelled
                    // run is most worth noticing: it can be the slowest part of a folder export.
                    if (BulkContext.Cancelled)
                        break;

                    // ... and it is also the part that writes nothing while it works, so the window
                    // names what it is doing instead of sitting on the last asset of the loop above.
                    if (task != null)
                        task.Update("resolving chunks: " + entry.Name);

                    List<Guid> ids = AssetReferenceUtils.GetChunkIds(entry);
                    if (ids.Count == 0)
                    {
                        // An asset that contributes nothing is worth explaining, because the two
                        // reasons behind it look identical in the output folder: the asset names no
                        // chunk at all, or it names chunks this profile does not have. Only the first
                        // 20 are named, so the summary below always fits in the log.
                        string reason = AssetReferenceUtils.ExplainNoChunks(entry);
                        if (reason != null)
                        {
                            result.Detail(entry.Name + ": " + reason);
                            if (unexplained < 20)
                                result.Note("no chunk for " + entry.Name + " - " + reason);
                            unexplained++;
                        }

                        continue;
                    }

                    // An id this profile does not have is the one thing a run cannot write out, so it
                    // is named in the report with its guid: that is what tells "the asset names
                    // nothing" apart from "the bundle holding this is not loaded", and the guid is
                    // what lets someone go and find the bundle it does live in.
                    int missing = 0;
                    foreach (Guid id in ids)
                    {
                        ChunkAssetEntry chunk = App.AssetManager.GetChunkEntry(id);
                        if (chunk == null)
                        {
                            missing++;
                            result.Detail("  " + AssetReferenceUtils.FormatChunkId(id) + " is not in this profile");
                            continue;
                        }

                        if (seen.Add(id))
                            chunks.Add(chunk);
                    }

                    result.Detail(entry.Name + ": " + (ids.Count - missing) + " of " + ids.Count
                        + " chunk id(s) are in this profile");
                    resolved++;
                }

                if (assets.Count != 0)
                {
                    result.Note(chunks.Count + " chunk(s) from " + resolved + " of " + assets.Count
                        + " asset(s) under " + (folderPrefix.Length == 0 ? "[root]" : folderPrefix));
                    if (unexplained != 0)
                        result.Note(unexplained + " asset(s) named no chunk this profile has (the first "
                            + Math.Min(unexplained, 20) + " are listed above)");
                }
            }

            int step = 0;
            foreach (ChunkAssetEntry chunk in chunks)
            {
                if (BulkContext.Cancelled)
                    break;

                if (task != null)
                    task.Update(chunk.Name, chunks.Count == 0 ? 0.0 : (step / (double)chunks.Count) * 100.0);
                step++;

                try
                {
                    Stream chunkStream = App.AssetManager.GetChunk(chunk);
                    if (chunkStream == null)
                    {
                        result.Note("skip chunk " + chunk.Id + " (could not be read)");
                        result.Skipped++;
                        continue;
                    }

                    string outPath = Path.Combine(outputRoot, AssetReferenceUtils.FormatChunkId(chunk.Id) + ".chunk");
                    using (NativeWriter writer = new NativeWriter(new FileStream(outPath, FileMode.Create)))
                        writer.Write(new NativeReader(chunkStream).ReadToEnd());

                    result.Done++;
                    result.Chunks++;
                }
                catch (Exception ex)
                {
                    result.Note("failed chunk " + chunk.Id + ": " + ex.Message);
                    result.Failed++;
                }
            }
        }

        /// <summary>Turns an asset name into the path to write for it, keeping the folder layout.</summary>
        private static string Relative(string outputRoot, string relativeName, string extension)
        {
            return Path.Combine(outputRoot, relativeName.Replace('/', '\\') + extension);
        }

        private static string RelativeName(string name, string match)
        {
            return match.Length == 0 ? name : name.Substring(match.Length);
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch
            {
                // An export that failed is reported; a leftover empty file is not worth failing over.
            }
        }
    }
}
