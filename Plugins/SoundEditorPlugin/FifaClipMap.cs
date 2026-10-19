using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace SoundEditorPlugin
{
    /// <summary>One clip of a mapped bank: who it belongs to and how the research derived it.</summary>
    public sealed class FifaClipAssignment
    {
        /// <summary>The clip's ordinal in the bank (the same number the editor lists).</summary>
        public int Ordinal;

        /// <summary>The clip's byte offset in the bank's chunk, which is what binds a row to a clip.</summary>
        public long SamplesOffset;

        /// <summary>The players-table row id, i.e. what the game DB and the name files key on.</summary>
        public int PlayerId;

        /// <summary>The stored bank key (<c>player_db_ID</c>): the player id offset by the bank's convention.</summary>
        public int PlayerDbId;

        /// <summary>The name the research recorded for this player, used when the game DB cannot answer.</summary>
        public string Name;

        /// <summary>
        /// <c>interval</c> when the research's stored range table owns this clip, <c>run</c> when it was
        /// extrapolated from a run of rows. The editor says which, so a derived name is not shown as a
        /// measured one.
        /// </summary>
        public string RowKind;

        /// <summary>
        /// The clip's length in seconds, from the map's <c>header_seconds</c> column - read out of the
        /// clip's own SNR header (its sample count divided by its sample rate), which is what the
        /// decoded audio agrees with exactly.
        ///
        /// Corrected 2026-10-16. This field used to be read from a <c>duration_100ns</c> column and
        /// divided by 1e7, which is why it disagreed with the decoded audio by a factor of 1.34-1.67 per
        /// row: the bank's <c>Segments.Duration</c> array is not a tick count at any fixed unit - it is
        /// relative to the bank's shortest clip and companded (see
        /// <c>notes/findings_duration_array.md</c>). The raw array element is still carried, undivided, in
        /// <see cref="DurationElement"/>, and a track's own duration still comes from decoding its clip.
        /// </summary>
        public double DurationSeconds;

        /// <summary>
        /// The raw <c>Segments.Duration</c> array element the map's <c>duration_elem</c> column states
        /// (the old <c>duration_100ns</c> column held the same number). Relative to the bank's shortest
        /// clip and companded, so it is not seconds and must not be divided by 1e7.
        /// </summary>
        public long DurationElement;

        public override string ToString()
        {
            return string.Format(CultureInfo.InvariantCulture, "clip {0} -> player {1} ({2})", Ordinal, PlayerId, Name);
        }
    }

    /// <summary>
    /// The clip to player map the sound research produced from the shipped bank, read at runtime and
    /// only trusted for the bank it was made from.
    ///
    /// A bank's clips do not carry their key: the <c>player_db_ID</c> record points at a pool of keys,
    /// a stored (lo,hi) range table maps a pool index to <c>Selection</c> rows, and a row's
    /// <c>VariationIndex</c> names the clip. That chain is not re-walked here; the research walked it
    /// for the shipped name bank and wrote one row per clip, keyed by the clip's byte offset.
    ///
    /// The byte offset is what makes this safe to use. Every answer is checked against the offset the
    /// bank itself reports for that clip, and the map is bound to a bank only when a sample of its rows
    /// agrees, so another language's bank - same structure, different bytes - cannot be given the
    /// English names.
    /// </summary>
    public static class FifaClipMap
    {
        public const string FileName = "player_name_map.csv";

        private static readonly object sync = new object();
        private static Dictionary<int, FifaClipAssignment> byOrdinal;
        private static Dictionary<int, List<int>> ordinalsByPlayer;
        private static string loadedFrom;
        private static string unavailableReason;
        private static bool loadAttempted;
        private static int rowCount;
        private static int boundSampleMatches;
        private static int boundSampleSize;
        private static string boundBank;

        /// <summary>True when the map file was found and parsed.</summary>
        public static bool Available
        {
            get
            {
                EnsureLoaded();
                return byOrdinal != null && byOrdinal.Count > 0;
            }
        }

        /// <summary>How many clips the map holds, for a summary line.</summary>
        public static int RowCount
        {
            get
            {
                EnsureLoaded();
                return rowCount;
            }
        }

        /// <summary>The file the map was read from, or null.</summary>
        public static string LoadedFrom
        {
            get
            {
                EnsureLoaded();
                return loadedFrom;
            }
        }

        /// <summary>True when the map has been bound to the bank currently being edited.</summary>
        public static bool IsBoundTo(string bankPath)
        {
            return boundBank != null && string.Equals(boundBank, bankPath ?? "", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Binds the map to one bank by checking that a sample of its offsets agrees with the offsets
        /// the bank itself reports. A bank that does not agree is never named from this map: another
        /// language's copy of the same bank has the same shape and different bytes, and its clips would
        /// otherwise be given the English players' names.
        /// </summary>
        /// <returns>True when the map may be used for this bank.</returns>
        public static bool BindBank(string bankPath, IList<long> clipOffsets)
        {
            EnsureLoaded();
            boundBank = null;
            boundSampleMatches = 0;
            boundSampleSize = 0;

            if (byOrdinal == null || clipOffsets == null || clipOffsets.Count == 0 || rowCount == 0)
                return false;

            boundBank = bankPath ?? "";

            // Rows whose ordinal the bank also has, up to a fixed sample, spread across the bank rather
            // than clustered at its start.
            int step = Math.Max(1, clipOffsets.Count / 40);
            for (int ordinal = 0; ordinal < clipOffsets.Count; ordinal += step)
            {
                FifaClipAssignment row;
                if (!byOrdinal.TryGetValue(ordinal, out row))
                    continue;

                boundSampleSize++;
                if (row.SamplesOffset == clipOffsets[ordinal])
                    boundSampleMatches++;
            }

            // Two thirds of the sampled rows is the bar: a bank that is not the mapped one matches by
            // chance on a handful of offsets at most, and a bank that is the mapped one matches on
            // essentially all of them.
            bool bound = boundSampleSize >= 8 && boundSampleMatches * 3 >= boundSampleSize * 2;

            if (!bound)
            {
                boundBank = null;
                unavailableReason = string.Format(CultureInfo.InvariantCulture,
                    "this bank is not the bank the name map was made from ({0} of {1} sampled clips matched its offsets)",
                    boundSampleMatches, boundSampleSize);
            }

            return bound;
        }

        /// <summary>
        /// The player a clip belongs to, or null. The bank's own offset for the clip has to match the
        /// row's, so a clip at a different offset is never named from a row that merely shares its
        /// ordinal.
        /// </summary>
        public static FifaClipAssignment Lookup(int ordinal, long samplesOffset)
        {
            EnsureLoaded();
            if (byOrdinal == null || boundBank == null)
                return null;

            FifaClipAssignment row;
            if (!byOrdinal.TryGetValue(ordinal, out row))
                return null;

            return row.SamplesOffset == samplesOffset ? row : null;
        }

        /// <summary>
        /// How many clips this player has in the bound bank and which of them this ordinal is, so a
        /// voice with several takes says <c>take 2 of 3</c> instead of looking like three duplicates.
        /// Numbering follows the bank's clip order, which is the order the list shows.
        /// </summary>
        public static bool TryTakes(int playerId, int ordinal, out int take, out int total)
        {
            take = 0;
            total = 0;

            EnsureLoaded();
            if (ordinalsByPlayer == null || boundBank == null)
                return false;

            List<int> ordinals;
            if (!ordinalsByPlayer.TryGetValue(playerId, out ordinals) || ordinals.Count == 0)
                return false;

            total = ordinals.Count;
            take = ordinals.IndexOf(ordinal) + 1;
            return take > 0;
        }

        /// <summary>One line naming the map's state, for the summary bar and the log.</summary>
        public static string Status()
        {
            EnsureLoaded();

            if (byOrdinal == null || rowCount == 0)
                return "name map: " + (unavailableReason ?? "not loaded");

            return string.Format(CultureInfo.InvariantCulture, "name map: {0} clips from {1}", rowCount, loadedFrom);
        }

        /// <summary>Where the map is looked for, in order, as absolute paths.</summary>
        public static IList<string> SearchPaths()
        {
            List<string> paths = new List<string>();

            foreach (string candidate in NativeAudioTools.SearchPaths(FileName))
                Add(paths, candidate);

            // The research's own output folder, which is where the file is written on the machine that
            // produced it, and the repository beside the editor.
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
                unavailableReason = "not found; it is looked for at: " + string.Join(", ", new List<string>(SearchPaths()).ToArray());
                return;
            }

            try
            {
                string[] lines = File.ReadAllLines(path);
                if (lines.Length < 2)
                {
                    unavailableReason = path + " holds no rows";
                    return;
                }

                string[] header = Split(lines[0]);
                int iPlayerId = IndexOf(header, "playerid");
                int iPlayerDbId = IndexOf(header, "pool_key");
                int iOrdinal = IndexOf(header, "clip_ordinal");
                int iOffset = IndexOf(header, "samples_offset");
                int iName = IndexOf(header, "name");
                int iRowKind = IndexOf(header, "row_kind");
                // `header_seconds` is the clip's own measured length; `duration_elem` is the raw
                // Segments.Duration element (the pre-2026-10-16 file named that column
                // `duration_100ns`, so it is still accepted - it is the same number, only relabelled).
                int iSeconds = IndexOf(header, "header_seconds");
                int iDuration = IndexOf(header, "duration_elem");
                if (iDuration < 0)
                    iDuration = IndexOf(header, "duration_100ns");

                if (iPlayerId < 0 || iOrdinal < 0 || iOffset < 0)
                {
                    unavailableReason = path + " is not the clip to player map: it has no playerid/clip_ordinal/samples_offset column";
                    return;
                }

                Dictionary<int, FifaClipAssignment> rows = new Dictionary<int, FifaClipAssignment>();
                Dictionary<int, List<int>> byPlayer = new Dictionary<int, List<int>>();
                List<int> order = new List<int>();

                for (int i = 1; i < lines.Length; i++)
                {
                    string line = lines[i];
                    if (string.IsNullOrEmpty(line) || line.Length < 4)
                        continue;

                    string[] parts = Split(line);
                    int ordinal;
                    long offset;
                    int playerId;

                    if (!TryInt(parts, iOrdinal, out ordinal) || !TryLong(parts, iOffset, out offset) || !TryInt(parts, iPlayerId, out playerId))
                        continue;

                    int playerDbId;
                    if (!TryInt(parts, iPlayerDbId, out playerDbId))
                        playerDbId = 0;

                    long durationElement;
                    if (!TryLong(parts, iDuration, out durationElement))
                        durationElement = 0;

                    double headerSeconds;
                    if (!TryDouble(parts, iSeconds, out headerSeconds) || headerSeconds < 0)
                        headerSeconds = 0.0;

                    FifaClipAssignment row = new FifaClipAssignment
                    {
                        Ordinal = ordinal,
                        SamplesOffset = offset,
                        PlayerId = playerId,
                        PlayerDbId = playerDbId,
                        Name = Value(parts, iName),
                        RowKind = Value(parts, iRowKind),
                        DurationSeconds = headerSeconds,
                        DurationElement = durationElement,
                    };

                    // A clip claimed twice keeps the first row: the research's ownership rule already
                    // reports the overlap, and picking one is a display decision rather than a reading.
                    if (!rows.ContainsKey(ordinal))
                        rows[ordinal] = row;

                    List<int> takes;
                    if (!byPlayer.TryGetValue(playerId, out takes))
                    {
                        takes = new List<int>();
                        byPlayer[playerId] = takes;
                    }

                    takes.Add(ordinal);
                    order.Add(ordinal);
                }

                if (rows.Count == 0)
                {
                    unavailableReason = path + " holds no usable rows";
                    return;
                }

                foreach (KeyValuePair<int, List<int>> pair in byPlayer)
                    pair.Value.Sort();

                byOrdinal = rows;
                ordinalsByPlayer = byPlayer;
                rowCount = rows.Count;
                loadedFrom = path;
                unavailableReason = null;
            }
            catch (Exception ex)
            {
                unavailableReason = "could not be read: " + ex.Message;
            }
        }

        private static string[] Split(string line)
        {
            // The field reader is deliberately a plain split: the file's names are ASCII player names and
            // a quoted separator has never been measured in it, so a parser that guessed at quoting would
            // be inventing cases rather than handling measured ones.
            return line.TrimEnd('\r', '\n').Split(',');
        }

        private static int IndexOf(string[] header, string column)
        {
            for (int i = 0; i < header.Length; i++)
            {
                if (string.Equals(header[i].Trim('"', ' '), column, StringComparison.OrdinalIgnoreCase))
                    return i;
            }

            return -1;
        }

        private static string Value(string[] parts, int index)
        {
            if (index < 0 || index >= parts.Length)
                return null;

            string value = parts[index].Trim('"', ' ');
            return value.Length == 0 ? null : value;
        }

        private static bool TryInt(string[] parts, int index, out int value)
        {
            value = 0;
            return index >= 0 && index < parts.Length && int.TryParse(parts[index].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
        }

        private static bool TryLong(string[] parts, int index, out long value)
        {
            value = 0;
            return index >= 0 && index < parts.Length && long.TryParse(parts[index].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
        }

        private static bool TryDouble(string[] parts, int index, out double value)
        {
            value = 0.0;
            return index >= 0 && index < parts.Length && double.TryParse(parts[index].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        }

        private static void Add(List<string> paths, string path)
        {
            if (string.IsNullOrEmpty(path))
                return;

            string full;
            try
            {
                full = Path.GetFullPath(path);
            }
            catch (Exception)
            {
                return;
            }

            if (!paths.Contains(full))
                paths.Add(full);
        }

        /// <summary>The map's own description of a row, for a detail line that has to say what it is.</summary>
        public static string DescribeRowKind(string rowKind)
        {
            if (string.Equals(rowKind, "interval", StringComparison.OrdinalIgnoreCase))
                return "from the bank's stored range table";

            if (string.Equals(rowKind, "run", StringComparison.OrdinalIgnoreCase))
                return "derived from a run of rows";

            return rowKind;
        }

        /// <summary>Only used by the harness: what the binding check measured for the current bank.</summary>
        public static string BindingNote()
        {
            if (boundBank == null)
                return unavailableReason ?? "not bound to a bank";

            StringBuilder text = new StringBuilder();
            text.Append(boundSampleMatches).Append(" of ").Append(boundSampleSize).Append(" sampled clips matched the map's offsets");
            return text.ToString();
        }
    }
}
