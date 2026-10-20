using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace SoundEditorPlugin
{
    /// <summary>
    /// What a Harmony sample bank (EA Trax, crowd, anthems) says about its own samples, read from its RAM and debug
    /// chunks: where each sample starts, the value it is filed under, and the source file it was built from.
    ///
    /// Both chunks are SBle files with one TESD dataset and one row per sample entry, read with the same field classes
    /// as a sound bank's (0 one value, 1 a step, 2 an array, 3 a palette). The RAM chunk's `.OFF` field (0x2E4F4646)
    /// is a sample's offset in the stream chunk; the debug chunk's `SRC.` field (0x2E535243) is the offset of the
    /// sample's source path in the debug chunk's data section (SBle + 0x20), and its rows are the RAM chunk's rows in
    /// the same order.
    ///
    /// In EATrax_01 and EATrax_02 the RAM rows are also filed under parameter 0xE8770F66 (its name is not in FIFA17.exe
    /// or the EBX dump), and its values are the songids of the game DB's `eatrax` table: 51 of 51 songs, each source
    /// path naming the DB's artist or title (measured 2026-10-19). FIFA17.exe reads that table by songid
    /// (FUN_14789d6a0), which is where a song's name comes from when the DB is loaded.
    /// </summary>
    internal static class HarmonyBankNames
    {
        public const uint OffsetField = 0x2E4F4646;   // ".OFF" read little-endian
        public const uint SourceField = 0x2E535243;   // "SRC."
        public const uint SongKey = 0xE8770F66;

        /// <summary>One sample entry's name, as the bank and the DB state it.</summary>
        public sealed class Sample
        {
            public long StreamOffset;
            public int SongId;
            public string SourcePath;

            /// <summary>The source file's name without its folder and extension: `Bayonne-Appeals_CLEAN_FINAL`.</summary>
            public string SourceStem
            {
                get
                {
                    if (string.IsNullOrEmpty(SourcePath))
                        return null;
                    string name = SourcePath.Replace('\\', '/');
                    int slash = name.LastIndexOf('/');
                    if (slash >= 0)
                        name = name.Substring(slash + 1);
                    int dot = name.LastIndexOf('.');
                    return dot > 0 ? name.Substring(0, dot) : name;
                }
            }
        }

        /// <summary>
        /// The bank's samples by stream offset (the first row that states an offset names it). Empty when the RAM
        /// chunk states no `.OFF` field; <paramref name="debug"/> may be null.
        /// </summary>
        public static Dictionary<long, Sample> ByStreamOffset(byte[] ram, byte[] debug)
        {
            Dictionary<long, Sample> result = new Dictionary<long, Sample>();

            Dictionary<uint, uint[]> ramColumns = ReadColumns(ram);
            uint[] offsets;
            if (ramColumns == null || !ramColumns.TryGetValue(OffsetField, out offsets))
                return result;

            uint[] songs;
            ramColumns.TryGetValue(SongKey, out songs);

            Dictionary<uint, uint[]> debugColumns = debug == null ? null : ReadColumns(debug);
            uint[] sources = null;
            if (debugColumns != null)
                debugColumns.TryGetValue(SourceField, out sources);
            int dataSection = debug != null && debug.Length >= 0x24 ? (int)BitConverter.ToUInt32(debug, 0x20) : -1;

            for (int row = 0; row < offsets.Length; row++)
            {
                long offset = offsets[row];
                if (result.ContainsKey(offset))
                    continue;

                Sample sample = new Sample { StreamOffset = offset };
                if (songs != null && row < songs.Length)
                    sample.SongId = (int)songs[row];
                if (sources != null && row < sources.Length && dataSection >= 0)
                    sample.SourcePath = CString(debug, dataSection + (long)sources[row]);
                result[offset] = sample;
            }

            return result;
        }

        /// <summary>
        /// The name the list shows for a sample: the DB's `artist - songname` for an EA Trax songid when the DB is
        /// loaded, else the source file's name, else null.
        /// </summary>
        public static string DisplayName(Sample sample)
        {
            if (sample == null)
                return null;

            string song;
            if (sample.SongId > 0 && FifaNameLookup.TrySongName(sample.SongId, out song) && !string.IsNullOrWhiteSpace(song))
                return song;

            return sample.SourceStem;
        }

        /// <summary>The tooltip: the songid the bank files the sample under, and its source file.</summary>
        public static string Describe(Sample sample)
        {
            if (sample == null)
                return null;

            List<string> parts = new List<string>();
            if (sample.SongId > 0)
                parts.Add("songid " + sample.SongId.ToString(CultureInfo.InvariantCulture));
            if (!string.IsNullOrEmpty(sample.SourcePath))
                parts.Add(sample.SourcePath);
            return parts.Count == 0 ? null : string.Join(" · ", parts.ToArray());
        }

        /// <summary>Every field of the first TESD dataset, decoded per row; null when the file is not one.</summary>
        internal static Dictionary<uint, uint[]> ReadColumns(byte[] b)
        {
            try
            {
                if (b == null || b.Length < 0x60 || b[0] != 'S' || b[1] != 'B' || b[2] != 'l' || b[3] != 'e')
                    return null;

                int count = BitConverter.ToUInt16(b, 0x0A);
                int toc = (int)BitConverter.ToUInt32(b, 0x18);
                if (count < 1 || toc <= 0 || toc + 8 > b.Length)
                    return null;

                int at = (int)BitConverter.ToUInt32(b, toc);
                if (at < 0 || at + 0x48 > b.Length || b[at] != 'T' || b[at + 1] != 'E' || b[at + 2] != 'S' || b[at + 3] != 'D')
                    return null;

                int rows = (int)BitConverter.ToUInt32(b, at + 0x38);
                int fields = BitConverter.ToUInt16(b, at + 0x3C);
                int fieldTable = BitConverter.ToUInt16(b, at + 0x40);
                if (rows < 0 || rows > 1000000)
                    return null;

                Dictionary<uint, uint[]> columns = new Dictionary<uint, uint[]>();
                for (int f = 0; f < fields; f++)
                {
                    int r = at + fieldTable + 24 * f;
                    if (r + 24 > b.Length)
                        return null;

                    uint id = BitConverter.ToUInt32(b, r);
                    uint type = BitConverter.ToUInt32(b, r + 4);
                    uint value = BitConverter.ToUInt32(b, r + 8);
                    uint ptr = BitConverter.ToUInt32(b, r + 16);
                    uint[] decoded = Decode(b, rows, type, value, ptr);
                    if (decoded != null)
                        columns[id] = decoded;
                }

                return columns;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static uint[] Decode(byte[] b, int rows, uint type, uint value, uint ptr)
        {
            int cls = (int)((type >> 8) & 0xFF);
            int width = (int)(type >> 24);
            int shift = (int)((type >> 16) & 0xFF);
            uint[] values = new uint[rows];

            switch (cls)
            {
                case 0:
                    for (int i = 0; i < rows; i++)
                        values[i] = value;
                    return values;
                case 1:
                    int step = unchecked((short)(type >> 16));
                    for (int i = 0; i < rows; i++)
                        values[i] = unchecked(value + (uint)(step * i));
                    return values;
                case 2:
                    if (width != 1 && width != 2 && width != 4)
                        return null;
                    if (ptr + (long)rows * width > b.Length)
                        return null;
                    for (int i = 0; i < rows; i++)
                    {
                        int e = (int)ptr + i * width;
                        uint element = width == 1 ? b[e] : width == 2 ? BitConverter.ToUInt16(b, e) : BitConverter.ToUInt32(b, e);
                        values[i] = unchecked((element << shift) + value);
                    }
                    return values;
                case 3:
                    int bits = shift, size = width;
                    long entries = value;
                    if ((bits != 1 && bits != 2 && bits != 4 && bits != 8) || (size != 1 && size != 2 && size != 4 && size != 8)
                        || entries < 1 || entries > (1L << bits))
                        return null;
                    int perByte = 8 / bits;
                    if (ptr + entries * size + (rows + perByte - 1) / perByte > b.Length)
                        return null;
                    uint[] palette = new uint[entries];
                    for (int i = 0; i < entries; i++)
                    {
                        int e = (int)ptr + i * size;
                        palette[i] = size == 1 ? unchecked((uint)(sbyte)b[e])
                            : size == 2 ? unchecked((uint)BitConverter.ToInt16(b, e)) : BitConverter.ToUInt32(b, e);
                    }
                    int packed = (int)ptr + (int)entries * size;
                    for (int i = 0; i < rows; i++)
                    {
                        int index = (b[packed + i / perByte] >> ((i % perByte) * bits)) & ((1 << bits) - 1);
                        if (index >= entries)
                            return null;
                        values[i] = palette[index];
                    }
                    return values;
                default:
                    return null;
            }
        }

        private static string CString(byte[] b, long at)
        {
            if (b == null || at < 0 || at >= b.Length)
                return null;

            StringBuilder text = new StringBuilder();
            for (long i = at; i < b.Length && b[i] != 0; i++)
                text.Append((char)b[i]);
            return text.Length == 0 ? null : text.ToString();
        }
    }
}
