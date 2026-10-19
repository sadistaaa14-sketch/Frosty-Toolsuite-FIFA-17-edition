using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace SoundEditorPlugin
{
    /// <summary>
    /// What a bank's own key resolves to, clip by clip: `clip_to_db.tsv`, written by the sound research
    /// repo and found the same way <see cref="SoundRowCategories"/> finds its own TSV.
    ///
    /// Why the editor wants it. A bank's bytes say which key a clip answers to - the pool and its row
    /// blocks, the per-row `surname_ID` - and <see cref="SoundTrackNaming"/> already shows that. What the
    /// bytes cannot say is which club or player the number stands for, because the number is the game's own
    /// id **plus an anchor the bank declares itself**:
    ///
    /// <code>
    /// db_id(row) = bank key(row) + anchor(bank)
    /// anchor(bank) = the id of the entity the bank's own key-0 row is about
    /// </code>
    ///
    /// Measured, never fitted: the same file name in two voice sets resolves as `key + 1` and as
    /// `key + 1318`, 13 of 13 rows each named by their own clips, which is what rules a per-voice constant
    /// out (`notes/findings_team_id_space.md`, correction 2026-10-15). The research walks each bank, picks
    /// the anchor by consensus over that bank's own rows and writes one row per clip here, carrying both
    /// the name and the reading that produced it.
    ///
    /// Three things it will not do, each of them the reason it is safe to show:
    ///
    /// * a clip in a bank this file does not cover carries nothing, and the editor shows the bare key -
    ///   a bank that was never anchored is not a bank keyed by nothing;
    /// * a row with no name carries the file's own sentence saying why (`no anchor for <bank>: the bank's
    ///   key-0 clip names nobody in the table`, `no table for surname_ID`), because that sentence is the
    ///   difference between "unknown" and "wrong";
    /// * no name is ever derived here from a bare constant or a DB lookup - the research's whole point is
    ///   that `key 6` is Everton in one voice set and Manchester United in another.
    ///
    /// The file is read lazily once and is allowed to be absent: a diagnostic must never be the reason a
    /// bank stops opening.
    /// </summary>
    public static class FifaKeyTable
    {
        public const string FileName = "clip_to_db.tsv";

        /// <summary>One clip's key as the research resolved it.</summary>
        public sealed class KeyRow
        {
            public int Ordinal;

            /// <summary>The `Selection` row the clip is stored on, as the file states it.</summary>
            public int Row;

            /// <summary>The clip's decoded length in seconds, as the file measured it.</summary>
            public double Seconds;

            /// <summary>The parameter the clip is keyed by (<c>asset_team_ID</c>, <c>player_db_ID</c>), or null.</summary>
            public string KeyParam;

            /// <summary>The key's own value, as text (it is an id and is shown as one).</summary>
            public string KeyId;

            /// <summary>The club or player the key resolves to, or null when nothing names it.</summary>
            public string Name;

            /// <summary>The reading that resolved it: <c>teams.txt (key + 5, the anchor Chelsea)</c>.</summary>
            public string Space;

            /// <summary>The file's own sentence for a key it does not name. Null when it named it.</summary>
            public string Why;

            /// <summary>True when the file names this clip's key.</summary>
            public bool Named
            {
                get { return !string.IsNullOrEmpty(Name); }
            }

            /// <summary><c>asset_team_ID=0 (Chelsea)</c>, or null for a clip with no key.</summary>
            public string Label()
            {
                if (string.IsNullOrEmpty(KeyParam))
                    return null;

                string text = KeyParam + "=" + (KeyId ?? "?");
                return Named ? text + " (" + Name + ")" : text;
            }
        }

        /// <summary>What the file knows about one bank.</summary>
        public sealed class BankKeys
        {
            public string Bank;
            public Dictionary<int, KeyRow> Clips = new Dictionary<int, KeyRow>();
            public int Named;
            public int Keyed;

            /// <summary>"13 of 13 keyed clip(s) named", or null when the bank has no rows.</summary>
            public string Coverage()
            {
                if (Clips.Count == 0)
                    return null;

                if (Keyed == 0)
                    return Clips.Count.ToString("N0", CultureInfo.InvariantCulture) + " clip(s), no key";

                return string.Format(CultureInfo.InvariantCulture, "{0:N0} of {1:N0} keyed clip(s) named",
                    Named, Keyed);
            }
        }

        private static readonly object sync = new object();
        private static Dictionary<string, BankKeys> byFull;
        private static Dictionary<string, BankKeys> byTail;
        private static string loadedFrom;
        private static string unavailableReason;
        private static bool loadAttempted;
        private static int rowCount;
        private static int namedCount;

        /// <summary>True when the file was found and parsed.</summary>
        public static bool Available
        {
            get
            {
                EnsureLoaded();
                return byFull != null && byFull.Count > 0;
            }
        }

        /// <summary>How many clip rows the file holds.</summary>
        public static int RowCount
        {
            get
            {
                EnsureLoaded();
                return rowCount;
            }
        }

        /// <summary>How many of those rows have a name.</summary>
        public static int NamedCount
        {
            get
            {
                EnsureLoaded();
                return namedCount;
            }
        }

        /// <summary>The file it was read from, or null.</summary>
        public static string LoadedFrom
        {
            get
            {
                EnsureLoaded();
                return loadedFrom;
            }
        }

        /// <summary>Why there is nothing, when there is nothing.</summary>
        public static string UnavailableReason
        {
            get
            {
                EnsureLoaded();
                return unavailableReason;
            }
        }

        /// <summary>One line about the file as a whole, for a log line.</summary>
        public static string Status()
        {
            EnsureLoaded();

            if (byFull == null || byFull.Count == 0)
                return "key table: none (" + (unavailableReason ?? "not loaded") + ")";

            return string.Format(CultureInfo.InvariantCulture,
                "key table: {0:N0} named keyed clip(s) of {1:N0} in {2:N0} bank(s) from {3}",
                namedCount, rowCount, byFull.Count, loadedFrom);
        }

        /// <summary>
        /// The row for one clip of one bank, or null when this file says nothing about that clip.
        ///
        /// Null means "not covered"; a row with <see cref="KeyRow.Named"/> false means "covered and not
        /// named", and the two are different statements that the caller shows differently.
        /// </summary>
        public static KeyRow Lookup(string bankPath, int ordinal)
        {
            EnsureLoaded();

            BankKeys bank = Find(bankPath);
            if (bank == null)
                return null;

            KeyRow row;
            return bank.Clips.TryGetValue(ordinal, out row) ? row : null;
        }

        /// <summary>The bank this file knows by this path, or null.</summary>
        public static BankKeys Find(string bankPath)
        {
            EnsureLoaded();

            if (byFull == null || string.IsNullOrEmpty(bankPath))
                return null;

            string key = Normalize(bankPath);

            BankKeys bank;
            if (byFull.TryGetValue(key, out bank))
                return bank;

            // The editor holds an asset name that can carry a folder prefix the research's corpus-relative
            // key does not ("Sound/chants/.../team" against "chants/newwaves/chants_full/team"), so the tail
            // is tried too - the same rule SoundRowCategories uses, and for the same reason.
            foreach (string tail in Tails(key))
            {
                if (byTail.TryGetValue(tail, out bank))
                    return bank;
            }

            return null;
        }

        /// <summary>One line naming this bank's state, for the summary bar.</summary>
        public static string Coverage(string bankPath)
        {
            BankKeys bank = Find(bankPath);
            return bank == null ? null : bank.Coverage();
        }

        private static IEnumerable<string> Tails(string key)
        {
            string[] parts = key.Split('/');
            for (int take = Math.Min(5, parts.Length - 1); take >= 2; take--)
                yield return string.Join("/", parts, parts.Length - take, take);
        }

        /// <summary>A path as the file keys it: no drive, no extension, forward slashes, lower case.</summary>
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

        /// <summary>Where the file is looked for, in order, as absolute paths.</summary>
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
            byFull = new Dictionary<string, BankKeys>(StringComparer.OrdinalIgnoreCase);
            byTail = new Dictionary<string, BankKeys>(StringComparer.OrdinalIgnoreCase);
            rowCount = 0;
            namedCount = 0;

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
            {
                unavailableReason = FileName + " was not found next to the plugin or in the research notes";
                return;
            }

            try
            {
                foreach (string raw in File.ReadLines(path))
                {
                    if (raw.Length == 0)
                        continue;

                    string[] parts = raw.TrimEnd('\r', '\n').Split('\t');
                    if (parts.Length < 7)
                        continue;

                    if (string.Equals(parts[0], "bank", StringComparison.OrdinalIgnoreCase))
                        continue;                       // the header row

                    int ordinal;
                    if (!int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out ordinal))
                        continue;

                    string key = Normalize(parts[0]);
                    if (key.Length == 0)
                        continue;

                    BankKeys bank;
                    if (!byFull.TryGetValue(key, out bank))
                    {
                        bank = new BankKeys();
                        bank.Bank = key;
                        byFull[key] = bank;
                        AddTail(bank);
                    }

                    KeyRow row = new KeyRow();
                    row.Ordinal = ordinal;

                    int parsed;
                    if (int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed))
                        row.Row = parsed;

                    double seconds;
                    if (double.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out seconds))
                        row.Seconds = seconds;

                    // A "-" in any of these columns is the file saying "nothing here", not a name.
                    row.KeyParam = Blank(parts[4]);
                    row.KeyId = Blank(parts[5]);
                    row.Name = Blank(parts[6]);
                    row.Space = parts.Length > 7 ? Blank(parts[7]) : null;
                    row.Why = parts.Length > 10 ? Blank(parts[10]) : null;

                    bank.Clips[ordinal] = row;
                    rowCount++;

                    if (!string.IsNullOrEmpty(row.KeyParam))
                        bank.Keyed++;

                    if (row.Named)
                    {
                        namedCount++;
                        bank.Named++;
                    }
                }

                loadedFrom = path;
                unavailableReason = null;
            }
            catch (Exception ex)
            {
                byFull.Clear();
                byTail.Clear();
                rowCount = 0;
                namedCount = 0;
                loadedFrom = null;
                unavailableReason = "the key table could not be read: " + ex.Message;
            }
        }

        private static void AddTail(BankKeys bank)
        {
            string[] parts = bank.Bank.Split('/');
            for (int take = Math.Min(5, parts.Length - 1); take >= 2; take--)
            {
                string tail = string.Join("/", parts, parts.Length - take, take);
                if (!byTail.ContainsKey(tail))
                    byTail[tail] = bank;
            }
        }

        /// <summary>A column that holds "-" holds nothing: the file's own spelling for "unknown".</summary>
        private static string Blank(string value)
        {
            if (value == null)
                return null;

            string text = value.Trim().Trim('"');
            return text.Length == 0 || text == "-" ? null : text;
        }

        /// <summary>Forgets the file, so a re-run of the research tool is picked up without a restart.</summary>
        public static void Reset()
        {
            lock (sync)
            {
                loadAttempted = false;
                byFull = null;
                byTail = null;
                loadedFrom = null;
                unavailableReason = null;
                rowCount = 0;
                namedCount = 0;
            }
        }
    }
}
