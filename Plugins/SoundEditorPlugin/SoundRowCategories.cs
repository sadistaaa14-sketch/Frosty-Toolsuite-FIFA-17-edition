using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace SoundEditorPlugin
{
    /// <summary>
    /// Which row of a bank is which take, read from `row_categories.tsv` - a table of
    /// `bank, ordinal, take, category, detail` rows that ships beside the plugin when it is present.
    ///
    /// It exists for one shape of bank: several rows that the file itself calls the same thing. `bpa_goal_team_gen_eng`
    /// holds three takes of the home line and one of the visitors' line, and its selection parameter (`pa_team_type`)
    /// names no row, so without this table the list read four identical "Goal announcement" rows. The category is the
    /// row's identity - `Home`, `Visitors`, `Number 27`, `Fourth official · 1 minute` - and is shown as the row's name.
    ///
    /// It is optional. A bank the file does not cover is named from its own keys, and a bank with no table beside the
    /// plugin is named the same way; nothing here is required for a bank to open, and an unreadable line skips itself
    /// rather than failing the load.
    /// </summary>
    public static class SoundRowCategories
    {
        /// <summary>The file the categories are read from.</summary>
        public const string FileName = "row_categories.tsv";

        /// <summary>What one row of a bank is: <c>Home</c>, <c>Visitors</c>, <c>Fourth official</c>, <c>Number 27</c>.</summary>
        public sealed class CategoryRead
        {
            /// <summary>The category name, e.g. <c>Home</c>. Never empty when a row is returned.</summary>
            public string Category;

            /// <summary>Which take of that category it is, or the figure it carries, e.g. <c>take 2 of 3</c>.</summary>
            public string Detail;
        }

        private static readonly object sync = new object();
        private static Dictionary<string, Dictionary<int, CategoryRead>> categories;
        private static string loadedFrom;
        private static bool loadAttempted;

        /// <summary>The file the categories were read from, or null when there is none.</summary>
        public static string CategoryFile
        {
            get
            {
                EnsureLoaded();
                return loadedFrom;
            }
        }

        /// <summary>How many rows the category file covers, or 0.</summary>
        public static int CategoryCount
        {
            get
            {
                EnsureLoaded();
                if (categories == null)
                    return 0;

                int total = 0;
                foreach (Dictionary<int, CategoryRead> rows in categories.Values)
                    total += rows.Count;
                return total;
            }
        }

        /// <summary>
        /// Which category one clip belongs to, when the bank's rows are categories - null when they are not.
        ///
        /// This is not what the clip says: it is the row's identity, so that three takes of one line read as three
        /// rows rather than one row three times. A clip this session added is in the table only through
        /// <see cref="RememberAdded"/>: the file itself has no row for audio that did not exist when it was written.
        /// </summary>
        public static CategoryRead CategoryOf(string bankPath, int ordinal)
        {
            EnsureLoaded();

            if (string.IsNullOrEmpty(bankPath))
                return null;

            string key = Normalize(bankPath);

            CategoryRead added = Lookup(addedNow, key, ordinal);
            if (added != null)
                return added;

            if (categories == null || categories.Count == 0)
                return null;

            return Lookup(categories, key, ordinal);
        }

        /// <summary>
        /// The categories of the clips this session added, keyed the way the table is: a take added from a home row
        /// is a home take, and the row tables say so by its place in the block while the table has no words for audio
        /// that did not exist when it was written. Session state only - nothing here is written to the file or
        /// survives a reload of it.
        /// </summary>
        private static readonly Dictionary<string, Dictionary<int, CategoryRead>> addedNow =
            new Dictionary<string, Dictionary<int, CategoryRead>>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// States what a clip just added to a bank is, so the list can name it: the category of the row it was made
        /// from, with a detail that says it was added rather than listed.
        /// </summary>
        public static void RememberAdded(string bankPath, int ordinal, CategoryRead from)
        {
            if (string.IsNullOrEmpty(bankPath) || ordinal < 0 || from == null || string.IsNullOrEmpty(from.Category))
                return;

            string key = Normalize(bankPath);
            if (key.Length == 0)
                return;

            CategoryRead read = new CategoryRead();
            read.Category = from.Category;
            read.Detail = "added take";

            lock (sync)
            {
                Dictionary<int, CategoryRead> rows;
                if (!addedNow.TryGetValue(key, out rows))
                {
                    rows = new Dictionary<int, CategoryRead>();
                    addedNow[key] = rows;
                }

                rows[ordinal] = read;
            }
        }

        private static CategoryRead Lookup(Dictionary<string, Dictionary<int, CategoryRead>> table, string key, int ordinal)
        {
            if (table == null)
                return null;

            Dictionary<int, CategoryRead> rows;
            if (table.TryGetValue(key, out rows))
                return RowOf(rows, ordinal);

            foreach (string tail in Tails(key))
            {
                if (table.TryGetValue(tail, out rows))
                    return RowOf(rows, ordinal);
            }

            return null;
        }

        private static CategoryRead RowOf(Dictionary<int, CategoryRead> rows, int ordinal)
        {
            CategoryRead found;
            return rows.TryGetValue(ordinal, out found) ? found : null;
        }

        private static void EnsureLoaded()
        {
            lock (sync)
            {
                if (loadAttempted)
                    return;

                loadAttempted = true;
                Read();
            }
        }

        private static void Read()
        {
            categories = new Dictionary<string, Dictionary<int, CategoryRead>>(StringComparer.OrdinalIgnoreCase);

            string path = null;
            foreach (string candidate in SearchPaths())
            {
                try
                {
                    if (File.Exists(candidate))
                    {
                        path = candidate;
                        break;
                    }
                }
                catch (Exception)
                {
                }
            }

            if (path == null)
                return;

            Dictionary<string, Dictionary<int, CategoryRead>> read =
                new Dictionary<string, Dictionary<int, CategoryRead>>(StringComparer.OrdinalIgnoreCase);

            try
            {
                foreach (string raw in File.ReadLines(path))
                {
                    if (raw.Length == 0 || raw[0] == '#')
                        continue;

                    string[] parts = raw.Split('\t');
                    if (parts.Length < 4 || string.Equals(parts[0], "bank", StringComparison.OrdinalIgnoreCase))
                        continue;

                    int ordinal;
                    if (!int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out ordinal))
                        continue;

                    string key = Normalize(parts[0]);
                    if (key.Length == 0 || parts[3].Trim().Length == 0)
                        continue;

                    Dictionary<int, CategoryRead> rows;
                    if (!read.TryGetValue(key, out rows))
                    {
                        rows = new Dictionary<int, CategoryRead>();
                        read[key] = rows;
                    }

                    CategoryRead row = new CategoryRead();
                    row.Category = parts[3].Trim();
                    row.Detail = parts.Length > 4 && parts[4].Trim().Length > 0 ? parts[4].Trim() : null;
                    rows[ordinal] = row;
                }

                categories = read;
                loadedFrom = path;
            }
            catch (Exception)
            {
                categories = new Dictionary<string, Dictionary<int, CategoryRead>>(StringComparer.OrdinalIgnoreCase);
                loadedFrom = null;
            }
        }

        private static IEnumerable<string> Tails(string key)
        {
            string[] parts = key.Split('/');
            for (int take = Math.Min(5, parts.Length - 1); take >= 2; take--)
                yield return string.Join("/", parts, parts.Length - take, take);
        }

        /// <summary>A path as the table keys it: no drive, no extension, forward slashes, lower case.</summary>
        private static string Normalize(string path)
        {
            string text = path.Replace('\\', '/').Trim();

            int sound = text.IndexOf("/sound/", StringComparison.OrdinalIgnoreCase);
            if (sound >= 0)
                text = text.Substring(sound + "/sound/".Length);
            else if (text.StartsWith("sound/", StringComparison.OrdinalIgnoreCase))
                text = text.Substring("sound/".Length);

            if (text.EndsWith(".res", StringComparison.OrdinalIgnoreCase))
                text = text.Substring(0, text.Length - 4);

            return text.Trim('/').ToLowerInvariant();
        }

        /// <summary>Where the table is looked for, in order, as absolute paths.</summary>
        public static IList<string> SearchPaths()
        {
            List<string> paths = new List<string>();

            foreach (string candidate in NativeAudioTools.SearchPaths(FileName))
                Add(paths, candidate);

            string profile = null;
            try
            {
                profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            }
            catch (Exception)
            {
            }

            string pluginDir = NativeAudioTools.PluginDirectory;
            string[] roots = new string[]
            {
                Path.Combine(pluginDir, "research"),
                Path.Combine(pluginDir, "..", "research"),
                Path.Combine(pluginDir, "..", "..", "FIFA17soundsystem", "notes"),
                Path.Combine(pluginDir, "..", "..", "..", "FIFA17soundsystem", "notes"),
                profile == null ? null : Path.Combine(profile, "Downloads", "FIFA17soundsystem", "notes"),
            };

            foreach (string root in roots)
            {
                if (!string.IsNullOrEmpty(root))
                    Add(paths, Path.Combine(root, FileName));
            }

            return paths;
        }

        private static void Add(IList<string> paths, string path)
        {
            if (!string.IsNullOrEmpty(path) && !paths.Contains(path))
                paths.Add(path);
        }

        /// <summary>Forgets the table, so an edit to it is picked up without a restart.</summary>
        public static void Reset()
        {
            lock (sync)
            {
                loadAttempted = false;
                categories = null;
                loadedFrom = null;
            }
        }
    }
}
