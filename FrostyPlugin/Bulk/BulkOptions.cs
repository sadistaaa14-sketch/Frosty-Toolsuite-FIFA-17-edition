using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Frosty.Core.Windows;
using FrostySdk.Managers;

namespace Frosty.Core.Bulk
{
    /// <summary>
    /// Which way a bulk operation moves data. Both directions share the same option set, only the
    /// wording of the dialog changes.
    /// </summary>
    public enum BulkOperation
    {
        Export,
        Import
    }

    /// <summary>
    /// Where a bulk command was raised from, so a dialog opened out of the Chunk/Res Editor only
    /// offers the categories that make sense there instead of the whole asset list.
    /// </summary>
    [Flags]
    public enum BulkScope
    {
        None = 0,
        /// <summary>Ebx assets, including meshes and textures.</summary>
        Assets = 1,
        /// <summary>Raw chunk files.</summary>
        Chunks = 2,
        /// <summary>Resource files.</summary>
        Res = 4,
        /// <summary>Everything - the folder commands in the Data Explorer.</summary>
        All = Assets | Chunks | Res
    }

    /// <summary>
    /// The set of things a bulk export or import should handle. Every category is opt-in, with the
    /// ebx xml form on by default because it is the only form that can be read back losslessly.
    /// </summary>
    public class BulkOptions
    {
        /// <summary>Ebx assets (the asset itself).</summary>
        public bool Ebx = true;

        /// <summary>Ebx assets written/read as raw binary (.bin). Off by default: raw ebx is opaque.</summary>
        public bool EbxBin = false;

        /// <summary>Ebx assets written/read as xml (.xml). On by default.</summary>
        public bool EbxXml = true;

        /// <summary>Mesh assets, exported as fbx/obj and imported from fbx.</summary>
        public bool Meshes = false;

        /// <summary>Texture assets, exported as an image and imported from an image.</summary>
        public bool Textures = false;

        /// <summary>Raw chunk files (named after the chunk id).</summary>
        public bool Chunks = false;

        /// <summary>Resource files (the 16 byte resource header followed by the resource bytes).</summary>
        public bool Res = false;

        /// <summary>
        /// "fbx" (binary), "fbx-ascii" or "obj". The ASCII form is worth having when the file is
        /// going to be read or diffed rather than loaded straight back into a game.
        /// </summary>
        public string MeshFormat = "fbx";

        /// <summary>
        /// Every texture format to write. "dds", "png", "tga" and "hdr" combine, so one run can
        /// produce a .dds to feed back in and a .png to look at.
        /// </summary>
        public List<string> TextureFormats = new List<string> { "dds" };

        /// <summary>The chosen texture formats as one string, e.g. "dds+png".</summary>
        public string TextureFormatList => (TextureFormats == null || TextureFormats.Count == 0)
            ? "dds"
            : string.Join("+", TextureFormats);

        /// <summary>The skeleton to use for skinned meshes, or "" when none is selected.</summary>
        public string Skeleton = "";

        /// <summary>
        /// When set (the default) the skeleton is worked out per asset from its folder, which is what
        /// makes a folder of player meshes export correctly without the user picking anything.
        /// </summary>
        public bool SkeletonAuto = true;

        /// <summary>True when at least one category is selected.</summary>
        public bool AnySelected => (Ebx && (EbxBin || EbxXml)) || Meshes || Textures || Chunks || Res;

        /// <summary>True when any ebx asset will be written.</summary>
        public bool AnyEbx => Ebx && (EbxBin || EbxXml);

        /// <summary>
        /// A one line description of the selection, used in the task window and the log so it is
        /// obvious afterwards what a run actually did.
        /// </summary>
        public string Describe(string operation)
        {
            List<string> parts = new List<string>();
            if (AnyEbx)
            {
                List<string> forms = new List<string>();
                if (EbxXml) forms.Add("xml");
                if (EbxBin) forms.Add("bin");
                parts.Add("ebx as " + string.Join("+", forms));
            }
            if (Meshes) parts.Add("meshes as " + MeshFormat);
            if (Textures) parts.Add("textures as " + TextureFormatList);
            if (Chunks) parts.Add("chunks");
            if (Res) parts.Add("res");
            return parts.Count == 0 ? "nothing selected" : operation + ": " + string.Join(", ", parts);
        }

        public BulkOptions Clone()
        {
            BulkOptions copy = (BulkOptions)MemberwiseClone();
            // The format list is mutable, so a copy has to get its own.
            copy.TextureFormats = new List<string>(TextureFormats ?? new List<string>());
            return copy;
        }
    }

    /// <summary>
    /// The options each bulk dialog starts from. There is one set per direction, shared by every
    /// bulk command (the asset tree, the Chunk/Res Editor), so a run that was set up once is repeated
    /// with two clicks instead of being set up again - the dialogs write the user's choice straight
    /// back into these objects.
    /// </summary>
    public static class BulkOptionDefaults
    {
        /// <summary>The options the bulk export dialogs start from and write back to.</summary>
        public static readonly BulkOptions Export = new BulkOptions();

        /// <summary>The options the bulk import dialogs start from and write back to.</summary>
        public static readonly BulkOptions Import = new BulkOptions();

        /// <summary>
        /// The options for a run whose scope already says exactly what to write.
        ///
        /// The Chunk/Res Editor's chunk list and res tree name their category themselves, so the
        /// options dialog there would open with a single check box - the one the user has just
        /// clicked - and nothing else to decide. A command in that position runs with only its own
        /// category switched on instead of putting a window in the way, which is why "Bulk Export
        /// Chunks" and "Bulk Export Res" go straight to choosing the output folder.
        /// </summary>
        public static BulkOptions ForScope(BulkScope scope)
        {
            BulkOptions options = new BulkOptions
            {
                Ebx = false,
                EbxXml = false,
                EbxBin = false,
                Meshes = false,
                Textures = false,
                Chunks = false,
                Res = false,
                SkeletonAuto = false,
            };

            if ((scope & BulkScope.Assets) != 0)
            {
                options.Ebx = true;
                options.EbxXml = true;
            }

            options.Chunks = (scope & BulkScope.Chunks) != 0;
            options.Res = (scope & BulkScope.Res) != 0;
            return options;
        }
    }

    /// <summary>
    /// Works out which skeleton a mesh asset belongs to.
    ///
    /// The pure helpers (<see cref="GuessToken"/>, <see cref="PickByName"/>,
    /// <see cref="PickInFolder"/>) take their inputs as arguments so the rules can be tested without
    /// a loaded game; the <c>AutoDetect</c>/<c>Resolve</c> helpers below feed them from the asset
    /// manager.
    /// </summary>
    public static class BulkSkeletons
    {
        /// <summary>
        /// The tokens that appear in asset paths, in the order they are tested. The first is the
        /// most specific: a trophy mesh lives under a folder whose name carries the trophy name, so
        /// it has to win over the generic "character" fallback.
        /// </summary>
        private static readonly string[] Tokens =
        {
            "trophy", "goalpost", "crowd", "ball", "player"
        };

        /// <summary>
        /// Maps a folder or asset path onto the kind of skeleton it needs. Returns "" when nothing
        /// in the path tells us, and null when the caller should look inside the folder itself.
        /// </summary>
        public static string GuessToken(string pathOrName)
        {
            if (string.IsNullOrEmpty(pathOrName))
                return "";

            // Normalise separators once so the checks below do not have to care.
            string value = pathOrName.Replace('\\', '/').ToLowerInvariant();

            // A trophy folder is named after the trophy, so the only reliable skeleton is the one
            // sitting in the folder itself. "" means "no name to go on" to the callers.
            foreach (string token in Tokens)
            {
                if (token == "trophy")
                    continue;
                if (ContainsSegment(value, token))
                    return token;
            }

            if (ContainsSegment(value, "trophy"))
                return "trophy";

            // Kits, managers, kits numbers and logos are all worn by the player model, so they use
            // the player skeleton even though their folder says nothing about a player.
            string[] playerRidden = { "kit", "kitcmn", "kitnumber", "jerseyfont", "leaguelogo", "generichead",
                                      "genheadtexlayers", "manager", "accessory", "body", "shoe", "head", "hair" };
            foreach (string token in playerRidden)
            {
                if (ContainsSegment(value, token))
                    return "player";
            }

            return "";
        }

        /// <summary>
        /// Picks a skeleton out of <paramref name="skeletonNames"/> for the given kind token. The
        /// game names these "skeleton_player" while users write "player_skeleton"; both are accepted.
        /// </summary>
        public static string PickByName(IEnumerable<string> skeletonNames, string token)
        {
            if (skeletonNames == null || string.IsNullOrEmpty(token))
                return null;

            List<string> candidates = new List<string> { token, "skeleton_" + token, token + "_skeleton" };
            foreach (string candidate in candidates)
            {
                foreach (string name in skeletonNames)
                {
                    if (LeafName(name).Equals(candidate, StringComparison.OrdinalIgnoreCase))
                        return name;
                }
            }

            // Loose fallback: a leaf that mentions both the token and "skeleton".
            foreach (string name in skeletonNames)
            {
                string leaf = LeafName(name);
                if (leaf.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0 &&
                    leaf.IndexOf("skeleton", StringComparison.OrdinalIgnoreCase) >= 0)
                    return name;
            }

            return null;
        }

        /// <summary>
        /// Picks a skeleton that lives in <paramref name="folder"/> itself (trophies work this way).
        /// <paramref name="skeletonPaths"/> holds one entry per skeleton asset: its name and its folder.
        /// </summary>
        public static string PickInFolder(IEnumerable<KeyValuePair<string, string>> skeletonPaths, string folder)
        {
            if (skeletonPaths == null || string.IsNullOrEmpty(folder))
                return null;

            string wanted = folder.Replace('\\', '/').TrimEnd('/');
            foreach (KeyValuePair<string, string> entry in skeletonPaths)
            {
                string path = (entry.Value ?? "").Replace('\\', '/').TrimEnd('/');
                if (path.Equals(wanted, StringComparison.OrdinalIgnoreCase))
                    return entry.Key;
            }

            return null;
        }

        /// <summary>All skeleton asset names currently loaded.</summary>
        public static List<string> AllNames()
        {
            List<string> names = new List<string>();
            foreach (EbxAssetEntry entry in App.AssetManager.EnumerateEbx("SkeletonAsset"))
                names.Add(entry.Name);
            return names;
        }

        /// <summary>
        /// Resolves the skeleton for an asset: an explicit choice wins, otherwise the folder decides,
        /// otherwise the asset's own folder is searched for a skeleton sitting next to it.
        /// </summary>
        public static string Resolve(BulkOptions options, string assetPath, string assetName)
        {
            if (options == null)
                return null;

            if (!options.SkeletonAuto && !string.IsNullOrEmpty(options.Skeleton))
                return options.Skeleton;

            if (!string.IsNullOrEmpty(options.Skeleton) && options.SkeletonAuto)
            {
                // An explicit pick still wins when the folder gives us nothing to go on.
                string auto = AutoDetect(assetPath, assetName);
                return string.IsNullOrEmpty(auto) ? options.Skeleton : auto;
            }

            return AutoDetect(assetPath, assetName);
        }

        /// <summary>
        /// Works the skeleton out from the asset's folder/name alone.
        /// </summary>
        public static string AutoDetect(string assetPath, string assetName)
        {
            string combined = (assetPath ?? "") + "/" + (assetName ?? "");
            string token = GuessToken(combined);
            if (string.IsNullOrEmpty(token))
                return null;

            if (token == "trophy")
            {
                List<KeyValuePair<string, string>> paths = new List<KeyValuePair<string, string>>();
                foreach (EbxAssetEntry entry in App.AssetManager.EnumerateEbx("SkeletonAsset"))
                    paths.Add(new KeyValuePair<string, string>(entry.Name, entry.Path));
                return PickInFolder(paths, assetPath);
            }

            string byName = PickByName(AllNames(), token);
            if (!string.IsNullOrEmpty(byName))
                return byName;

            // Some groups keep their skeleton in a folder of their own (goalposts do).
            List<KeyValuePair<string, string>> all = new List<KeyValuePair<string, string>>();
            foreach (EbxAssetEntry entry in App.AssetManager.EnumerateEbx("SkeletonAsset"))
                all.Add(new KeyValuePair<string, string>(entry.Name, entry.Path));

            string inGroupFolder = PickInFolder(all, "content/character/rig/skeleton/" + token);
            return string.IsNullOrEmpty(inGroupFolder) ? null : inGroupFolder;
        }

        /// <summary>True when <paramref name="segment"/> appears as a whole path segment.</summary>
        private static bool ContainsSegment(string lowerPath, string segment)
        {
            if (lowerPath.IndexOf(segment, StringComparison.Ordinal) < 0)
                return false;

            int index = 0;
            while ((index = lowerPath.IndexOf(segment, index, StringComparison.Ordinal)) >= 0)
            {
                bool startOkay = index == 0 || lowerPath[index - 1] == '/';
                int end = index + segment.Length;
                bool endOkay = end >= lowerPath.Length || lowerPath[end] == '/'
                               || lowerPath[end] == '_' || lowerPath[end] == '.';

                if (startOkay && endOkay)
                    return true;

                index = end;
            }

            return false;
        }

        /// <summary>The part of an asset name after its last slash.</summary>
        public static string LeafName(string name)
        {
            if (string.IsNullOrEmpty(name))
                return "";
            int index = name.LastIndexOfAny(new[] { '/', '\\' });
            return index < 0 ? name : name.Substring(index + 1);
        }
    }

    /// <summary>
    /// Ambient state for the duration of a bulk run. Asset definitions consult this so that a bulk
    /// export/import never raises a per-asset settings dialog or a per-asset task window, and so
    /// that a mesh export knows which skeleton to use for the asset it is working on. It also carries
    /// the cancellation request, so a run can be stopped from its task window.
    /// </summary>
    public static class BulkContext
    {
        /// <summary>True while a bulk run is in progress.</summary>
        public static bool Silent { get; private set; }

        /// <summary>The options of the run that is in progress.</summary>
        public static BulkOptions Options { get; private set; }

        /// <summary>The skeleton resolved for the asset currently being processed, or null.</summary>
        public static string Skeleton { get; set; }

        /// <summary>
        /// True once the cancel button of the run in progress was pressed. A run checks it between
        /// items, so a long export or import can be stopped from the task window instead of having to
        /// run to the end or be killed: everything already written stays written, and the summary
        /// says the run was cancelled rather than pretending it finished. Cleared by
        /// <see cref="Begin"/>, so it always describes the run that is in progress now.
        /// </summary>
        public static bool Cancelled { get; private set; }

        /// <summary>
        /// Asks the run in progress to stop after the item it is working on. The cancel button lives
        /// on the UI thread while the run works on a thread pool thread, and a bool is all the
        /// handover that needs: a late read means one more item is finished, never a torn one.
        /// </summary>
        public static void Cancel() => Cancelled = true;

        /// <summary>
        /// The cancel wiring for a bulk run's task window. Pass it with <c>showCancelButton: true</c>
        /// to <c>FrostyTaskWindow.Show</c> so that the button - and Esc - ask the run to stop.
        /// </summary>
        public static readonly FrostyTaskCancelCallback TaskCancel = task => Cancel();

        public static void Begin(BulkOptions options)
        {
            Options = options;
            Skeleton = options != null && !string.IsNullOrEmpty(options.Skeleton) ? options.Skeleton : null;
            Cancelled = false;
            Silent = true;
        }

        public static void End()
        {
            Silent = false;
            Options = null;
            Skeleton = null;
        }
    }
}
