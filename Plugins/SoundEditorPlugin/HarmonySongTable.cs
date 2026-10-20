using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace SoundEditorPlugin
{
    /// <summary>One song of an EA Trax bank, as its RAM chunk and debug chunk state it.</summary>
    public sealed class HarmonySong
    {
        /// <summary>The value the RAM rows are filed under (parameter 0xE8770F66): the game DB's `eatrax.songid`.</summary>
        public int SongId;

        /// <summary>The RAM chunk's `.SID` column.</summary>
        public uint SampleId;

        /// <summary>The RAM chunk's `.OFF` column: where the song's audio starts in the stream chunk.</summary>
        public uint StreamOffset;

        /// <summary>The debug chunk's `.ZIS` column: the song's stream length in bytes (its blocks to the end block).</summary>
        public uint StreamSize;

        /// <summary>The debug chunk's `SRC.` string: the source file the song was built from.</summary>
        public string SourcePath;

        public HarmonySong Clone()
        {
            return (HarmonySong)MemberwiseClone();
        }

        public override string ToString()
        {
            return string.Format(CultureInfo.InvariantCulture, "songid {0}, .SID {1}, stream {2:N0} + {3:N0}, {4}", SongId, SampleId,
                StreamOffset, StreamSize, SourcePath ?? "(no source path)");
        }
    }

    /// <summary>What a commit of an EA Trax song table wrote, or why it wrote nothing.</summary>
    public sealed class HarmonySongWrite
    {
        public bool Ok;
        public string Refusal;
        public byte[] Ram;
        public byte[] Debug;
        public string Report;
    }

    /// <summary>
    /// The song list of an EA Trax Harmony sample bank (EATrax_01, EATrax_02), read from its RAM and debug chunks and
    /// written back with the bank format core (<see cref="BankImage"/>), which reads and writes both chunks byte for byte
    /// (all 12 chunks of the six shipped HarmonySampleBankAssets, measured 2026-10-20).
    ///
    /// The shape this edits is the one both shipped EA Trax banks state, and nothing else is accepted:
    ///   RAM chunk, one dataset: `.OFF` (the stream offset), `.SBS` (one value), `.SID` and 0xE8770F66 (the songid), one
    ///   group keyed by 0xE8770F66 (its values the songids, ascending) and 0x858F0492 (values 0 and 1), uniform blocks of
    ///   one row - so FUN_147986690 turns (songid, 0|1) into row 2 x (the songid's index) + (0|1), and every song is two
    ///   rows that state the same values.
    ///   Debug chunk, one dataset with the same rows: `.ZIS` (the song's stream length), `SRC.` (its source path, an offset
    ///   into the chunk's leading "data" block) and `.SX` (one value); no group.
    /// A song is added in songid order, with its two rows; the parameter's value list, count, min and max follow; every
    /// column is stated again in the smallest form that holds it (a palette, an array, or one value - ties to an array),
    /// which is the form all ten columns of the two shipped banks have. The written chunks are read back and every song
    /// is looked up the way FUN_147986690 / FUN_147986f40 look it up before anything is handed over.
    /// </summary>
    public sealed class HarmonySongTable
    {
        public const uint OffsetField = 0x2E4F4646;    // ".OFF"
        public const uint SbsField = 0x2E534253;       // ".SBS"
        public const uint SampleIdField = 0x2E534944;  // ".SID"
        public const uint SongKey = 0xE8770F66;
        public const uint VariantKey = 0x858F0492;
        public const uint SizeField = 0x2E53495A;      // ".ZIS"
        public const uint SourceField = 0x2E535243;    // "SRC."
        public const uint SettingsField = 0x2E535800;  // ".SX"

        /// <summary>Strings in the debug chunk are bytes; Latin-1 keeps a title's accented letters one byte each.</summary>
        private static readonly Encoding Latin1 = Encoding.GetEncoding(28591);

        private BankImage ram, debug;
        private BankDataset ramSet, debugSet;
        private byte[] debugBlock;
        private int pathsFrom;

        /// <summary>The songs, in the order the bank's rows state them (songid ascending).</summary>
        public List<HarmonySong> Songs { get; private set; }

        /// <summary>True when the bank came with a debug chunk, which is written with the RAM chunk.</summary>
        public bool HasDebug { get { return debug != null; } }

        /// <summary>The folder the shipped source paths are under, for a new song's path ("" when there is no song).</summary>
        public string SourceFolder
        {
            get
            {
                foreach (HarmonySong song in Songs)
                {
                    if (string.IsNullOrEmpty(song.SourcePath))
                        continue;
                    int slash = song.SourcePath.LastIndexOf('\\');
                    if (slash > 0)
                        return song.SourcePath.Substring(0, slash);
                }

                return "";
            }
        }

        private HarmonySongTable() { }

        /// <summary>
        /// Reads an EA Trax bank's song list; null with the sentence when the chunks are not that shape (every other
        /// Harmony bank: the crowd, intro and highlights banks are keyed and laid out differently).
        /// </summary>
        public static HarmonySongTable Open(byte[] ramData, byte[] debugData, out string why)
        {
            why = null;
            HarmonySongTable table = new HarmonySongTable();
            why = table.Load(ramData, debugData);
            return why == null ? table : null;
        }

        private string Load(byte[] ramData, byte[] debugData)
        {
            string error;
            ram = BankImage.Read(ramData, out error);
            if (ram == null)
                return "its RAM chunk does not read as a Harmony table (" + error + ")";
            string shape = CheckImage(ram, "RAM");
            if (shape != null)
                return shape;

            ramSet = ram.Datasets[0];
            BankField off = ramSet.Field(OffsetField), sbs = ramSet.Field(SbsField), sid = ramSet.Field(SampleIdField),
                key = ramSet.Field(SongKey);
            if (off == null || sbs == null || sid == null || key == null || ramSet.Fields.Count != 4)
                return "its RAM table is not the EA Trax one (.OFF, .SBS, .SID and the songid 0xE8770F66, and nothing else)";
            if (sbs.Class != 0)
                return "its RAM table's .SBS is not one value for every row";

            if (ramSet.Groups.Count != 1 || ramSet.Params.Count != 2)
                return "its RAM table is not found by one group of two parameters";
            BankGroup group = ramSet.Groups[0];
            if (!group.Uniform || group.TableWidth != 1 || group.CountPtr != 0 || group.RemapPtr != 0 || group.FirstParam != 0
                || group.ParamCount != 2)
                return "its RAM table's group is not uniform blocks of one row over two parameters";
            BankParam songs = ramSet.Params[0], variant = ramSet.Params[1];
            if (songs.Id != SongKey || variant.Id != VariantKey)
                return "its RAM table's group is not keyed by the songid (0xE8770F66) and then 0x858F0492";
            if (variant.Count != 2 || variant.Min != 0 || variant.Max != 1 || variant.Elements != null)
                return "its RAM table's 0x858F0492 does not take the two values 0 and 1";

            int rows = ramSet.Rows;
            if (rows % 2 != 0)
                return "its RAM table has " + rows + " rows; an EA Trax song is two";
            int count = rows / 2;
            if (songs.Count != count)
                return "its RAM table states " + songs.Count + " songids for " + count + " songs";

            uint[] offsets = off.Decode(rows), sampleIds = sid.Decode(rows), songIds = key.Decode(rows);
            if (offsets == null || sampleIds == null || songIds == null)
                return "its RAM table's columns cannot be read";

            int[] values = songs.Values();
            Songs = new List<HarmonySong>(count);
            for (int k = 0; k < count; k++)
            {
                int a = 2 * k, b = a + 1;
                if (offsets[a] != offsets[b] || sampleIds[a] != sampleIds[b] || songIds[a] != songIds[b])
                    return "rows " + a + " and " + b + " of its RAM table do not state the same song";
                if ((int)songIds[a] != values[k])
                    return "row " + a + " of its RAM table is songid " + songIds[a] + " where the group's value list has " + values[k];
                if (k > 0 && values[k] <= values[k - 1])
                    return "its songids are not in ascending order";
                Songs.Add(new HarmonySong { SongId = (int)songIds[a], SampleId = sampleIds[a], StreamOffset = offsets[a] });
            }

            if (debugData == null)
                return null;

            debug = BankImage.Read(debugData, out error);
            if (debug == null)
                return "its debug chunk does not read as a Harmony table (" + error + ")";
            shape = CheckImage(debug, "debug");
            if (shape != null)
                return shape;

            debugSet = debug.Datasets[0];
            BankField size = debugSet.Field(SizeField), source = debugSet.Field(SourceField), settings = debugSet.Field(SettingsField);
            if (size == null || source == null || settings == null || debugSet.Fields.Count != 3 || debugSet.Groups.Count != 0)
                return "its debug table is not the EA Trax one (.ZIS, SRC. and .SX, no group)";
            if (settings.Class != 0)
                return "its debug table's .SX is not one value for every row";
            if (debugSet.Rows != rows)
                return "its debug table has " + debugSet.Rows + " rows for the RAM table's " + rows;

            uint[] sizes = size.Decode(rows), sources = source.Decode(rows);
            if (sizes == null || sources == null)
                return "its debug table's columns cannot be read";

            debugBlock = new byte[debug.LeadingLength];
            Buffer.BlockCopy(debugData, debug.LeadingOffset, debugBlock, 0, debugBlock.Length);

            for (int k = 0; k < count; k++)
            {
                int a = 2 * k, b = a + 1;
                if (sizes[a] != sizes[b] || sources[a] != sources[b])
                    return "rows " + a + " and " + b + " of its debug table do not state the same song";
                string path = CString(debugBlock, sources[a]);
                if (path == null)
                    return "row " + a + " of its debug table names no source path";
                Songs[k].StreamSize = sizes[a];
                Songs[k].SourcePath = path;
            }

            // Where the source paths start in the block: the lowest SRC. offset; with no song left (a chunk this editor
            // wrote), the block's used length - the strings before the paths ("data", the encoder settings) are kept.
            pathsFrom = count > 0 ? (int)sources.Min() : UsedLength(debugBlock);
            return null;
        }

        private static string CheckImage(BankImage image, string which)
        {
            if (!image.HasLeadingBlock)
                return "its " + which + " chunk has no leading data block, so it is not a Harmony table";
            string chain = image.CheckChainIsKnown();
            if (chain != null)
                return "its " + which + " chunk: " + chain;
            if (image.Datasets.Count != 1)
                return "its " + which + " chunk holds " + image.Datasets.Count + " tables; an EA Trax bank's holds one";
            if (image.Datasets[0].LayoutProblem != null)
                return "its " + which + " table cannot be laid out again (" + image.Datasets[0].LayoutProblem + ")";
            return null;
        }

        // ─── edits ─────────────────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>The song filed under <paramref name="songId"/>, or null.</summary>
        public HarmonySong Find(int songId)
        {
            return Songs.FirstOrDefault(s => s.SongId == songId);
        }

        /// <summary>The songs whose audio starts at <paramref name="streamOffset"/> (one in a shipped bank).</summary>
        public List<HarmonySong> AtOffset(long streamOffset)
        {
            return Songs.Where(s => s.StreamOffset == streamOffset).ToList();
        }

        /// <summary>Adds a song in songid order; null, or why not.</summary>
        public string Add(HarmonySong song)
        {
            if (song == null)
                return "no song was given";
            if (Find(song.SongId) != null)
                return "songid " + song.SongId.ToString(CultureInfo.InvariantCulture) + " is already a song of this bank";
            if (Songs.Any(s => s.SampleId == song.SampleId))
                return ".SID " + song.SampleId.ToString(CultureInfo.InvariantCulture) + " is already a song's of this bank";

            int at = 0;
            while (at < Songs.Count && Songs[at].SongId < song.SongId)
                at++;
            Songs.Insert(at, song.Clone());
            return null;
        }

        /// <summary>Removes the song filed under <paramref name="songId"/>; null, or why not.</summary>
        public string Remove(int songId)
        {
            HarmonySong song = Find(songId);
            if (song == null)
                return "songid " + songId.ToString(CultureInfo.InvariantCulture) + " is not a song of this bank";
            Songs.Remove(song);
            return null;
        }

        /// <summary>
        /// Moves every song whose audio starts after <paramref name="from"/> by <paramref name="delta"/> bytes: what a
        /// stream that grew or shrank there does to the songs after it.
        /// </summary>
        public void ShiftAfter(long from, long delta)
        {
            foreach (HarmonySong song in Songs)
            {
                if (song.StreamOffset > from)
                    song.StreamOffset = (uint)(song.StreamOffset + delta);
            }
        }

        // ─── writing ───────────────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>States the songs in both chunks, reads them back, and hands the bytes over; nothing is changed in place.</summary>
        public HarmonySongWrite Commit()
        {
            List<string> report = new List<string>();
            try
            {
                List<HarmonySong> planned = Songs.Select(s => s.Clone()).ToList();
                int count = planned.Count, rows = 2 * count;

                // RAM: two rows per song.
                uint[] offsets = Rows(planned, s => s.StreamOffset), sampleIds = Rows(planned, s => s.SampleId),
                    songIds = Rows(planned, s => (uint)s.SongId);
                string issue = EncodeColumn(ramSet.Field(OffsetField), offsets, 4)
                    ?? EncodeColumn(ramSet.Field(SampleIdField), sampleIds, 4)
                    ?? EncodeColumn(ramSet.Field(SongKey), songIds, 4);
                if (issue != null)
                    return Refuse(issue, report);

                EncodeSongParameter(ramSet.Params[0], planned.Select(s => s.SongId).ToList());
                ramSet.Rows = rows;
                ramSet.Relay = true;

                string error;
                byte[] ramBytes = ram.Write(out error);
                if (ramBytes == null)
                    return Refuse("the RAM chunk could not be written: " + error, report);

                byte[] debugBytes = null;
                if (debug != null)
                {
                    // The source paths: the strings before them as they were, then each distinct path once, ordered by
                    // their lower-case text (the order both shipped debug chunks state theirs in: EATrax_02's puts
                    // "Beaty_Heart" before "BECK" - checked by the round trip).
                    List<string> paths = planned.Select(s => s.SourcePath ?? "").Distinct(StringComparer.Ordinal)
                        .OrderBy(p => p.ToLowerInvariant(), StringComparer.Ordinal).ThenBy(p => p, StringComparer.Ordinal).ToList();
                    Dictionary<string, uint> at = new Dictionary<string, uint>(StringComparer.Ordinal);
                    List<byte> block = new List<byte>();
                    for (int i = 0; i < pathsFrom && i < debugBlock.Length; i++)
                        block.Add(debugBlock[i]);
                    foreach (string path in paths)
                    {
                        at[path] = (uint)block.Count;
                        block.AddRange(Latin1.GetBytes(path));
                        block.Add(0);
                    }

                    uint[] sizes = Rows(planned, s => s.StreamSize);
                    uint[] sources = Rows(planned, s => at[s.SourcePath ?? ""]);
                    issue = EncodeColumn(debugSet.Field(SizeField), sizes, 4)
                        ?? EncodeColumn(debugSet.Field(SourceField), sources, sources.All(v => v <= 0x7FFF) ? 2 : 4);
                    if (issue != null)
                        return Refuse(issue, report);

                    debugSet.Rows = rows;
                    debugSet.Relay = true;
                    debug.LeadingReplacement = block.ToArray();
                    debugBytes = debug.Write(out error);
                    if (debugBytes == null)
                        return Refuse("the debug chunk could not be written: " + error, report);
                }

                // Read back the way the game reads it.
                string back;
                HarmonySongTable again = Open(ramBytes, debugBytes, out back);
                if (again == null)
                    return Refuse("refusing the written tables: they do not read back (" + back + ")", report);
                int checks = 0;
                List<string> failures = new List<string>();
                Expect(failures, ref checks, again.Songs.Count == count, "they read back " + again.Songs.Count + " songs, not " + count);
                for (int k = 0; k < count && k < again.Songs.Count; k++)
                {
                    HarmonySong want = planned[k], got = again.Songs[k];
                    Expect(failures, ref checks, got.SongId == want.SongId && got.SampleId == want.SampleId
                        && got.StreamOffset == want.StreamOffset, "song " + k + " reads back as " + got + ", not " + want);
                    if (debug != null)
                        Expect(failures, ref checks, got.StreamSize == want.StreamSize
                            && string.Equals(got.SourcePath, want.SourcePath ?? "", StringComparison.Ordinal),
                            "song " + k + "'s debug row reads back as " + got + ", not " + want);
                }

                // Every song, looked up the way FUN_147986690 (the values to an index, the songid's then 0x858F0492's) and
                // FUN_147986f40 (uniform blocks of the group's +0x18 width) look it up, plays its own stream offset.
                BankDataset set = again.ramSet;
                BankParam songParam = set.Params[0], variantParam = set.Params[1];
                uint[] backOffsets = set.Field(OffsetField).Decode(set.Rows);
                int width = set.Groups[0].TableWidth;
                foreach (HarmonySong song in planned)
                {
                    for (int v = 0; v <= 1; v++)
                    {
                        int i = songParam.IndexOf(song.SongId), j = variantParam.IndexOf(v);
                        long row = i < 0 || j < 0 ? -1 : (long)(i * variantParam.Count + j) * width;
                        Expect(failures, ref checks, row >= 0 && row < set.Rows && backOffsets[row] == song.StreamOffset,
                            "songid " + song.SongId + " (0x858F0492 = " + v + ") is not found at its own stream offset");
                    }
                }

                // With no song left, no value finds a row (FUN_147986690 tests min <= value <= max first).
                if (count == 0)
                {
                    foreach (int probe in new[] { 0, 1, 2, int.MaxValue, -1 })
                        Expect(failures, ref checks, songParam.IndexOf(probe) < 0, "an empty bank still finds a row for songid " + probe);
                }

                if (failures.Count > 0)
                    return Refuse("refusing the written tables: " + failures[0]
                        + (failures.Count > 1 ? " (and " + (failures.Count - 1) + " more)" : ""), report);

                report.Add(string.Format(CultureInfo.InvariantCulture,
                    "EA Trax tables: {0} song(s), RAM chunk {1:N0} -> {2:N0} bytes{3}; read back as the game reads them ({4} checks)",
                    count, ram.Data.Length, ramBytes.Length,
                    debugBytes == null ? "" : string.Format(CultureInfo.InvariantCulture, ", debug chunk {0:N0} -> {1:N0} bytes",
                        debug.Data.Length, debugBytes.Length), checks));
                return new HarmonySongWrite { Ok = true, Ram = ramBytes, Debug = debugBytes, Report = string.Join(Environment.NewLine, report.ToArray()) };
            }
            catch (Exception ex)
            {
                return Refuse("the EA Trax writer stopped on an internal error (" + ex.GetType().Name + ": " + ex.Message
                    + "); that is a fault in this writer, and nothing was written", report);
            }
        }

        private static HarmonySongWrite Refuse(string why, List<string> report)
        {
            return new HarmonySongWrite { Ok = false, Refusal = why, Report = string.Join(Environment.NewLine, report.ToArray()) };
        }

        private static void Expect(List<string> failures, ref int checks, bool ok, string what)
        {
            checks++;
            if (!ok)
                failures.Add(what);
        }

        private static uint[] Rows(List<HarmonySong> songs, Func<HarmonySong, uint> value)
        {
            uint[] rows = new uint[songs.Count * 2];
            for (int k = 0; k < songs.Count; k++)
                rows[2 * k] = rows[2 * k + 1] = value(songs[k]);
            return rows;
        }

        /// <summary>
        /// States a column in the smallest form that holds it: one value when every row has it, else a palette (class 3:
        /// the distinct values ascending in entries of <paramref name="paletteSize"/> bytes, the fewest bits per row) or
        /// an array (class 2: value - the smallest, no shift, the narrowest width), the palette only when it is smaller.
        /// That is the form of all ten columns of the two shipped EA Trax banks (measured 2026-10-20).
        /// </summary>
        internal static string EncodeColumn(BankField field, uint[] values, int paletteSize)
        {
            uint typeLow = field.Type & 0xFF;
            int n = values.Length;
            field.Palette = null;
            field.Indices = null;
            field.Elements = null;

            if (n == 0 || values.All(v => v == values[0]))
            {
                field.Type = typeLow;
                field.Value = n == 0 ? 0u : values[0];
                return null;
            }

            uint min = values.Min();
            int width = BankBytes.Narrowest(values.Max() - min);
            long arrayBytes = (long)n * width;

            uint[] palette = values.Distinct().OrderBy(v => v).ToArray();
            int bits = BankField.PaletteBits(palette.Length);
            long paletteBytes = long.MaxValue;
            if (bits > 0 && palette.All(v => BankField.PaletteHolds(v, paletteSize)))
                paletteBytes = (long)palette.Length * paletteSize + (n + (8 / bits) - 1) / (8 / bits);

            if (paletteBytes < arrayBytes)
            {
                Dictionary<uint, byte> index = new Dictionary<uint, byte>();
                for (int i = 0; i < palette.Length; i++)
                    index[palette[i]] = (byte)i;
                field.Type = typeLow | (3u << 8) | ((uint)bits << 16) | ((uint)paletteSize << 24);
                field.Value = (uint)palette.Length;
                field.Palette = palette;
                field.Indices = values.Select(v => index[v]).ToArray();
                return null;
            }

            field.Type = typeLow | (2u << 8) | ((uint)width << 24);
            field.Value = min;
            field.Elements = values.Select(v => v - min).ToArray();
            return null;
        }

        /// <summary>
        /// The songid parameter's value list: count, min and max, and - when the songids are not evenly spaced, or the
        /// record already carried one - the array of songid - min. With no song the count is 0 and min is above max, so
        /// FUN_147986690's first test (min &lt;= value &lt;= max) fails for every songid and no row is ever read.
        /// </summary>
        private static void EncodeSongParameter(BankParam param, List<int> songIds)
        {
            uint top = param.W4 & 0xF0000000u;
            int count = songIds.Count;
            if (count == 0)
            {
                param.W4 = top;
                param.Min = 1;
                param.Max = 0;
                param.Elements = null;
                return;
            }

            param.Min = songIds[0];
            param.Max = songIds[count - 1];
            bool even = count <= 2;
            if (!even)
            {
                long step = ((long)param.Max - param.Min) / (count - 1);
                even = step > 0 && ((long)param.Max - param.Min) % (count - 1) == 0;
                for (int i = 0; even && i < count; i++)
                    even = songIds[i] == param.Min + step * i;
            }

            if (even && param.Elements == null)
            {
                param.W4 = top | (uint)count;
                return;
            }

            uint[] elements = songIds.Select(v => unchecked((uint)(v - param.Min))).ToArray();
            int width = BankBytes.Narrowest(elements[count - 1]);
            param.Elements = elements;
            param.W4 = top | ((uint)width << 24) | (uint)count;
        }

        private static string CString(byte[] block, uint at)
        {
            if (block == null || at >= block.Length)
                return null;
            int end = (int)at;
            while (end < block.Length && block[end] != 0)
                end++;
            if (end >= block.Length)
                return null;
            return Latin1.GetString(block, (int)at, end - (int)at);
        }

        /// <summary>A block's length up to and including the NUL after its last non-zero byte.</summary>
        private static int UsedLength(byte[] block)
        {
            int last = block.Length - 1;
            while (last >= 0 && block[last] == 0)
                last--;
            return Math.Min(block.Length, last + 2);
        }
    }

    /// <summary>
    /// A Harmony bank's stream chunk: a 32-byte "data" header, then one SPS stream per sample (blocks of one type byte and
    /// a 24-bit big-endian size: H, D..., E), each on a 32-byte boundary, the chunk padded to 16 bytes after the last
    /// (EATrax_01: 8 songs at the RAM chunk's 8 `.OFF` offsets, each exactly its debug `.ZIS` size, measured 2026-10-20).
    /// </summary>
    public static class HarmonyStream
    {
        /// <summary>One sample of a rebuilt stream: an existing sample's bytes (From, its start) or new audio.</summary>
        public sealed class Piece
        {
            public long From = -1;
            public byte[] Audio;
        }

        /// <summary>Where the SPS stream at <paramref name="start"/> ends (past its end block), or -1.</summary>
        public static int StreamEnd(byte[] data, int start)
        {
            long at = start;
            while (at + 4 <= data.Length)
            {
                byte type = data[at];
                int size = (data[at + 1] << 16) | (data[at + 2] << 8) | data[at + 3];
                if (size < 4 || at + size > data.Length)
                    return -1;
                at += size;
                if (type == 0x45)
                    return (int)at;
            }

            return -1;
        }

        /// <summary>
        /// Every sample the stream holds, by start: an SNR header word (0x48 0 0 0x0C or 0x14), a codec the plugin
        /// decodes, a sample rate, and a data block after the header; the scan skips each stream by its blocks.
        /// </summary>
        public static List<int> Samples(byte[] stream)
        {
            List<int> offsets = new List<int>();
            if (stream == null)
                return offsets;

            for (int pos = 0; pos <= stream.Length - 16; pos++)
            {
                if (stream[pos] != 0x48 || stream[pos + 1] != 0x00 || stream[pos + 2] != 0x00
                    || (stream[pos + 3] != 0x0C && stream[pos + 3] != 0x14))
                    continue;

                int headerPayload = stream[pos + 3];
                byte codec = stream[pos + 4];
                if (codec != 0x12 && codec != 0x14 && codec != 0x15 && codec != 0x16 && codec != 0x19 && codec != 0x1c)
                    continue;

                ushort rate = (ushort)((stream[pos + 6] << 8) | stream[pos + 7]);
                if (rate != 8000 && rate != 11025 && rate != 12000 && rate != 16000 &&
                    rate != 22050 && rate != 24000 && rate != 32000 && rate != 44100 && rate != 48000)
                    continue;

                int dataBlock = pos + headerPayload;
                if (dataBlock >= stream.Length || stream[dataBlock] != 0x44)
                    continue;

                offsets.Add(pos);
                int end = StreamEnd(stream, pos);
                if (end > pos)
                    pos = end - 1;
            }

            return offsets;
        }

        /// <summary>The bytes of the sample starting at <paramref name="start"/>: to its end block, else to the next sample.</summary>
        public static int SampleEnd(byte[] stream, IList<int> samples, int start)
        {
            int next = stream.Length;
            foreach (int s in samples)
            {
                if (s > start && s < next)
                    next = s;
            }

            int end = StreamEnd(stream, start);
            return end > start && end <= next ? end : next;
        }

        /// <summary>
        /// The stream laid out again: the bytes before the first sample as they were (the "data" header; the whole chunk
        /// when it holds no sample), then every piece at the next 32-byte boundary, then zeros to a 16-byte boundary.
        /// <paramref name="at"/> receives each piece's new start. Null with <paramref name="why"/> when a piece names a
        /// sample the stream does not hold or the stream would outgrow 32 bits.
        /// </summary>
        public static byte[] Build(byte[] stream, IList<int> samples, IList<Piece> pieces, out long[] at, out string why)
        {
            why = null;
            at = new long[pieces.Count];
            int head = samples.Count > 0 ? samples.Min() : stream.Length;

            long cursor = head;
            long[] length = new long[pieces.Count];
            for (int i = 0; i < pieces.Count; i++)
            {
                Piece piece = pieces[i];
                if (piece.Audio == null)
                {
                    if (piece.From < 0 || piece.From > int.MaxValue || !samples.Contains((int)piece.From))
                    {
                        why = "the stream holds no sample at " + piece.From.ToString(CultureInfo.InvariantCulture);
                        return null;
                    }

                    length[i] = SampleEnd(stream, samples, (int)piece.From) - piece.From;
                }
                else
                {
                    length[i] = piece.Audio.Length;
                }

                cursor = (cursor + 31) & ~31L;
                at[i] = cursor;
                cursor += length[i];
            }

            long total = (cursor + 15) & ~15L;
            if (total > uint.MaxValue || total > int.MaxValue)
            {
                why = "the stream chunk would be " + total.ToString("N0", CultureInfo.InvariantCulture) + " bytes, past what a chunk size states";
                return null;
            }

            byte[] result = new byte[total];
            Buffer.BlockCopy(stream, 0, result, 0, head);
            for (int i = 0; i < pieces.Count; i++)
            {
                if (pieces[i].Audio == null)
                    Buffer.BlockCopy(stream, (int)pieces[i].From, result, (int)at[i], (int)length[i]);
                else
                    Buffer.BlockCopy(pieces[i].Audio, 0, result, (int)at[i], pieces[i].Audio.Length);
            }

            return result;
        }
    }

    /// <summary>
    /// One edit of an EA Trax bank, worked out on its three chunks' bytes: tracks (by their place in the stream, the
    /// list's order) whose audio is replaced, songs added, tracks removed with their songs, and new metadata for songs
    /// that stay. The stream is laid out once (<see cref="HarmonyStream.Build"/>) and the RAM and debug tables are stated
    /// once (<see cref="HarmonySongTable.Commit"/>); nothing is written anywhere - the caller writes the three results.
    /// </summary>
    public sealed class EaTraxEdit
    {
        public Dictionary<int, byte[]> Replace = new Dictionary<int, byte[]>();
        public List<KeyValuePair<EaTraxSongInfo, byte[]>> Add = new List<KeyValuePair<EaTraxSongInfo, byte[]>>();
        public HashSet<int> Remove = new HashSet<int>();
        public Dictionary<int, EaTraxSongInfo> Metadata = new Dictionary<int, EaTraxSongInfo>();

        /// <summary>The `.SID` the first new song takes at least (one past every Harmony bank's, as the caller found them).</summary>
        public uint FirstNewSampleId;

        public byte[] Ram, Debug, Stream;
        public int SongsRemoved, SongsAfter;
        public List<HarmonySong> NewSongs = new List<HarmonySong>();
        public string Report;

        /// <summary>The folder both shipped EA Trax banks' source paths are under, for a song added to a bank with none left.</summary>
        public const string ShippedSourceFolder = "Samples\\fifa17\\asset\\music\\ea_trax_music_masters";

        /// <summary>Works the edit out; null, or why nothing can be written.</summary>
        public string Apply(byte[] ram, byte[] debug, byte[] stream)
        {
            if (stream == null)
                return "its stream chunk is not loaded";

            string why;
            HarmonySongTable table = HarmonySongTable.Open(ram, debug, out why);
            if (table == null)
                return "its song table cannot be edited: " + why;

            List<int> samples = HarmonyStream.Samples(stream);
            foreach (HarmonySong song in table.Songs)
            {
                if (!samples.Contains((int)song.StreamOffset))
                    return "songid " + song.SongId + "'s audio is at " + song.StreamOffset.ToString("N0", CultureInfo.InvariantCulture)
                        + " of the stream, where the stream holds no sample";
            }

            foreach (int i in Replace.Keys.Concat(Remove).Concat(Metadata.Keys))
            {
                if (i < 0 || i >= samples.Count)
                    return "there is no track #" + (i + 1) + " in this bank (it has " + samples.Count + ")";
            }

            string folder = table.SourceFolder.Length > 0 ? table.SourceFolder : ShippedSourceFolder;

            // The new stream: every track that stays (its new audio or its own bytes), then every new song.
            List<HarmonyStream.Piece> pieces = new List<HarmonyStream.Piece>();
            List<int> pieceTrack = new List<int>();
            for (int i = 0; i < samples.Count; i++)
            {
                if (Remove.Contains(i))
                    continue;
                byte[] audio;
                pieces.Add(Replace.TryGetValue(i, out audio) ? new HarmonyStream.Piece { Audio = audio } : new HarmonyStream.Piece { From = samples[i] });
                pieceTrack.Add(i);
            }

            foreach (KeyValuePair<EaTraxSongInfo, byte[]> song in Add)
            {
                if (song.Key == null || song.Value == null || song.Value.Length == 0)
                    return "a new song has no songid or no audio";
                pieces.Add(new HarmonyStream.Piece { Audio = song.Value });
                pieceTrack.Add(-1);
            }

            long[] at;
            byte[] built = HarmonyStream.Build(stream, samples, pieces, out at, out why);
            if (built == null)
                return why;

            // The table: songs of removed tracks leave, every other song moves to its track's new start (a replaced song
            // takes its new stream's length, a song given metadata its new source path), every new song is filed under
            // its songid.
            Dictionary<long, int> pieceOf = new Dictionary<long, int>();
            for (int p = 0; p < pieces.Count; p++)
            {
                if (pieceTrack[p] >= 0)
                    pieceOf[samples[pieceTrack[p]]] = p;
            }

            List<HarmonySong> keep = new List<HarmonySong>();
            SongsRemoved = 0;
            foreach (HarmonySong song in table.Songs)
            {
                int p;
                if (!pieceOf.TryGetValue(song.StreamOffset, out p))
                {
                    SongsRemoved++;
                    continue;
                }

                HarmonySong moved = song.Clone();
                moved.StreamOffset = (uint)at[p];
                if (pieces[p].Audio != null)
                    moved.StreamSize = (uint)StreamLength(pieces[p].Audio);
                EaTraxSongInfo info;
                if (Metadata.TryGetValue(pieceTrack[p], out info) && info != null)
                    moved.SourcePath = SourcePath(folder, info);
                keep.Add(moved);
            }

            uint nextSampleId = Math.Max(FirstNewSampleId, table.Songs.Count == 0 ? 1u : table.Songs.Max(s => s.SampleId) + 1u);
            foreach (int id in table.Songs.Select(s => s.SongId).ToList())
                table.Remove(id);
            foreach (HarmonySong song in keep)
            {
                why = table.Add(song);
                if (why != null)
                    return why;
            }

            NewSongs.Clear();
            for (int k = 0; k < Add.Count; k++)
            {
                EaTraxSongInfo info = Add[k].Key;
                int p = pieces.Count - Add.Count + k;
                HarmonySong song = new HarmonySong
                {
                    SongId = info.SongId,
                    SampleId = nextSampleId++,
                    StreamOffset = (uint)at[p],
                    StreamSize = (uint)StreamLength(Add[k].Value),
                    SourcePath = SourcePath(folder, info),
                };
                why = table.Add(song);
                if (why != null)
                    return "songid " + info.SongId + " was not added: " + why;
                NewSongs.Add(song);
            }

            HarmonySongWrite written = table.Commit();
            if (!written.Ok)
                return written.Refusal;

            Ram = written.Ram;
            Debug = written.Debug;
            Stream = built;
            SongsAfter = table.Songs.Count;
            Report = string.Format(CultureInfo.InvariantCulture,
                "{0} replaced, {1} added, {2} track(s) removed ({3} song(s)); the bank now has {4} song(s) and a {5:N0}-byte stream. {6}",
                Replace.Count, Add.Count, Remove.Count, SongsRemoved, SongsAfter, built.Length, written.Report);
            return null;
        }

        /// <summary>A stream's length to its end block (what the debug chunk's `.ZIS` states), else its byte count.</summary>
        public static int StreamLength(byte[] audio)
        {
            int end = HarmonyStream.StreamEnd(audio, 0);
            return end > 0 ? end : audio.Length;
        }

        /// <summary>
        /// A song's source path in the debug chunk: the bank's songs' folder and "artist - title.wav", so the list names the
        /// song by what the user entered when the game DB does not have it (yet).
        /// </summary>
        public static string SourcePath(string folder, EaTraxSongInfo info)
        {
            StringBuilder name = new StringBuilder();
            string display = info.DisplayName;
            foreach (char ch in display.Length > 0 ? display : "song " + info.SongId.ToString(CultureInfo.InvariantCulture))
                name.Append(ch < ' ' || "<>:\"/\\|?*".IndexOf(ch) >= 0 ? '_' : ch);
            return (string.IsNullOrEmpty(folder) ? "" : folder + "\\") + name.ToString().Trim() + ".wav";
        }

        /// <summary>
        /// Moves every `.OFF` value of every table in a Harmony RAM chunk from where a sample started to where it starts now,
        /// in place - the value word (one value), the start of a start and a step (when every row moves alike), each element
        /// of an array (that still fits its width and shift), each palette entry. Null, or why not; a value that is no
        /// sample's start is refused rather than guessed. For the Harmony banks that are not the EA Trax shape, whose
        /// tables share their data areas (IntroMusic_02's arrays sit after the next table's records) and cannot be re-laid.
        /// </summary>
        public static string ShiftOffsets(byte[] ram, IDictionary<long, long> moved)
        {
            string error;
            BankImage image = BankImage.Read(ram, out error);
            if (image == null)
                return "the RAM chunk does not read (" + error + ")";

            foreach (BankDataset ds in image.Datasets)
            {
                BankField off = ds.Field(HarmonySongTable.OffsetField);
                if (off == null)
                    continue;
                uint[] values = off.Decode(ds.Rows);
                if (values == null)
                    return "the " + ds.Name + " table's .OFF cannot be read";

                Func<uint, long> to = v => moved.ContainsKey(v) ? moved[v] : -1;
                int record = ds.Offset + off.RecordOffset;
                switch (off.Class)
                {
                    case 0:
                    case 1:
                        if (ds.Rows == 0)
                            break;
                        long first = to(values[0]);
                        if (first < 0)
                            return "the " + ds.Name + " table's .OFF " + values[0] + " is no sample's start";
                        for (int r = 0; r < values.Length; r++)
                        {
                            long target = to(values[r]);
                            if (target < 0 || target - values[r] != first - values[0])
                                return "the " + ds.Name + " table states .OFF as a start and a step, and its samples do not all move alike";
                        }

                        BankBytes.W32(ram, record + 8, (uint)first);
                        break;
                    case 2:
                        int width = off.Width, shiftBits = off.Shift;
                        for (int r = 0; r < values.Length; r++)
                        {
                            long target = to(values[r]);
                            if (target < 0)
                                return "the " + ds.Name + " table's .OFF " + values[r] + " is no sample's start";
                            long element = target - off.Value;
                            if (element < 0 || (element & ((1L << shiftBits) - 1)) != 0 || !BankBytes.Fits((uint)(element >> shiftBits), width))
                                return "the " + ds.Name + " table's .OFF array cannot state " + target + " (" + width + "-byte elements)";
                            BankBytes.WElem(ram, off.ArrayOffset + r * width, width, (uint)(element >> shiftBits));
                        }

                        break;
                    case 3:
                        for (int e = 0; e < off.Palette.Length; e++)
                        {
                            long target = to(off.Palette[e]);
                            if (target < 0)
                                return "the " + ds.Name + " table's .OFF palette entry " + off.Palette[e] + " is no sample's start";
                            if (!BankField.PaletteHolds((uint)target, off.Width))
                                return "the " + ds.Name + " table's .OFF palette cannot state " + target;
                            if (off.Width == 8)
                                BankBytes.W32(ram, off.ArrayOffset + e * 8, (uint)target);
                            else
                                BankBytes.WElem(ram, off.ArrayOffset + e * off.Width, off.Width,
                                    off.Width == 4 ? (uint)target : (uint)target & ((1u << (8 * off.Width)) - 1u));
                        }

                        break;
                    default:
                        return "the " + ds.Name + " table states .OFF in class " + off.Class + ", which this editor does not write";
                }
            }

            return null;
        }
    }
}
