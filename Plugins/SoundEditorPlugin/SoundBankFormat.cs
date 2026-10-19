using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace SoundEditorPlugin
{
    // ─────────────────────────────────────────────────────────────────────────────────────────────────────────────
    //  A FIFA 17 sound bank (.res), read and written the way FIFA17.exe reads it.
    //
    //  Every rule below is one of the game's own functions (decompiled through the Ghidra bridge, the raw responses
    //  are in FIFA17soundsystem/bridge/resp/), or a layout the shipped files state and this code reproduces byte for
    //  byte before it is allowed to write anything:
    //
    //  * FUN_14798a760 relocates the file: from the 8-byte slot at SBle+0x18 it follows a chain, each slot holding
    //    (u32 offset, u32 link), both relative to the SBle header; offset 0xFFFFFFFF is a null pointer, link
    //    0xFFFFFFFF ends the chain, a slot must be 8-aligned and inside the buffer, and a pointer must land inside
    //    it. A pointer that is not on the chain is never relocated, so the game reads it as a raw offset.
    //  * A dataset (TESD) states its rows at +0x38, its field, group and parameter record counts and tables at
    //    +0x3C..+0x44, and where its records end at +0x04.
    //  * A field record (0x18 bytes) is read by FUN_147981bc0/FUN_147981ce0: the type word's byte 1 is the storage
    //    class - 0 the value word for every row, 1 value + step*row with the step the signed short at +0x06, 2 an
    //    array behind the pointer, element (width = byte 3) shifted left by byte 2 and added to the value word, 3 a
    //    bit-packed palette, 4 a table lookup. Byte 0 is the data type (5 is a float read as its 32-bit pattern).
    //  * A group record (0x20) and its parameter records (0x18) are read by FUN_147986980 (match by count and ids),
    //    FUN_147986690 (values to one index, mixed radix, last parameter least significant) and FUN_147986f40
    //    (index to a start and a count: uniform blocks of the +0x18 width, or a start table of that element width,
    //    or a count table at +0x08). FUN_147987300 maps the start through a u32 table at +0x10 when there is one.
    //  * FUN_146210710 reads a Selection row's VariationIndex and uses it as a Variations row directly.
    //  * FUN_146210310 reads Segments.SamplesOffset as (byte offset & ~3) | flags (bit 0: present, bit 1: in the
    //    bank's chunk) and Segments.Duration through FUN_147982530, element + value word as integers.
    //
    //  The layout of a dataset's data area is the shipped one: field arrays in record order, then the parameter
    //  value arrays (one array shared by several records written once), then the value dictionaries (each a sorted,
    //  de-duplicated copy of a column's values minus its minimum, at the column's width, ordered by data type then
    //  record), then the group start tables, every block aligned to its element width and the region padded to 16
    //  bytes. Nothing points at a dictionary, so the game never reads one; they are kept so an edited bank has the
    //  shape a shipped one has. A dataset whose data area this rule does not reproduce exactly is never re-laid.
    // ─────────────────────────────────────────────────────────────────────────────────────────────────────────────

    internal static class BankBytes
    {
        public static ushort U16(byte[] b, int at) { return (ushort)(b[at] | (b[at + 1] << 8)); }
        public static uint U32(byte[] b, int at) { return (uint)(b[at] | (b[at + 1] << 8) | (b[at + 2] << 16) | (b[at + 3] << 24)); }
        public static int I32(byte[] b, int at) { return unchecked((int)U32(b, at)); }

        public static void W16(byte[] b, int at, int v) { b[at] = (byte)v; b[at + 1] = (byte)(v >> 8); }

        public static void W32(byte[] b, int at, uint v)
        {
            b[at] = (byte)v; b[at + 1] = (byte)(v >> 8); b[at + 2] = (byte)(v >> 16); b[at + 3] = (byte)(v >> 24);
        }

        public static uint Elem(byte[] b, int at, int width)
        {
            switch (width)
            {
                case 1: return b[at];
                case 2: return U16(b, at);
                case 4: return U32(b, at);
                default: throw new InvalidOperationException("element width " + width);
            }
        }

        public static void WElem(byte[] b, int at, int width, uint v)
        {
            switch (width)
            {
                case 1: b[at] = (byte)v; break;
                case 2: W16(b, at, (int)v); break;
                case 4: W32(b, at, v); break;
                default: throw new InvalidOperationException("element width " + width);
            }
        }

        /// <summary>The narrowest of 1, 2 and 4 bytes that holds <paramref name="value"/>.</summary>
        public static int Narrowest(uint value)
        {
            return value <= 0xFF ? 1 : (value <= 0xFFFF ? 2 : 4);
        }

        public static bool Fits(uint value, int width)
        {
            return width >= 4 || value <= (width == 2 ? 0xFFFFu : 0xFFu);
        }

        public static int Align(int at, int align)
        {
            return align <= 1 ? at : (at + align - 1) / align * align;
        }

        /// <summary>The engine's name hash: h = 5381; h = (h * 33) ^ byte, case kept.</summary>
        public static uint Hash(string text)
        {
            uint h = 5381;
            foreach (byte c in Encoding.UTF8.GetBytes(text ?? ""))
                h = unchecked((h * 33) ^ c);
            return h;
        }

        public static string Hex(uint v) { return "0x" + v.ToString("X8", CultureInfo.InvariantCulture); }
        public static string Hex(int v) { return "0x" + v.ToString("X", CultureInfo.InvariantCulture); }
    }

    /// <summary>The names the engine hash reproduces for datasets and fields.</summary>
    internal static class BankNames
    {
        public const uint SelectionParameters = 0x131A919F, Selection = 0x11D5B185, Variations = 0xA29AF127,
            Segments = 0x3FE2AFD5, Chunks = 0xA28D4A0D, Subtitles = 0x2384D4B2;

        public const uint VariationIndex = 0x6AC4E4EA, SampleId = 0x80268F2E, VariationId = 0xF5F914D9,
            Duration = 0x6CFCCE5B, SamplesOffset = 0xE8E591DD, SeekTableOffset = 0xD506D74E, ChunkSize = 0xDC19107B,
            ChunkId = 0xF4369173, SegmentIndex = 0xD06D5A58, ChunkIndex = 0x603CFF80, FirstSegmentIndex = 0xE4660A62,
            SegmentCount = 0xD00A6005, Language = 0xE6CEA7B7, ParameterId = 0x8D21F9A1, ParameterIndex = 0x0E6AC212;

        private static readonly Dictionary<uint, string> Tables = new Dictionary<uint, string>
        {
            { SelectionParameters, "SelectionParameters" }, { Selection, "Selection" }, { Variations, "Variations" },
            { Segments, "Segments" }, { Chunks, "Chunks" }, { Subtitles, "Subtitles" },
        };

        private static readonly Dictionary<uint, string> Fields = new Dictionary<uint, string>
        {
            { ParameterIndex, "ParameterIndex" }, { ParameterId, "ParameterId" }, { VariationIndex, "VariationIndex" },
            { SampleId, "SampleId" }, { VariationId, "VariationId" }, { 0x678C1CBC, "StreamChunkIndex" },
            { SegmentCount, "SegmentCount" }, { SegmentIndex, "SegmentIndex" }, { Duration, "Duration" },
            { SamplesOffset, "SamplesOffset" }, { ChunkId, "ChunkId" }, { ChunkSize, "ChunkSize" },
            { SeekTableOffset, "SeekTableOffset" }, { FirstSegmentIndex, "FirstSegmentIndex" },
            { 0x03DA5B4E, "LastLoopSegmentIndex" }, { 0x6B89F83E, "FirstLoopSegmentIndex" }, { ChunkIndex, "ChunkIndex" },
            { Language, "Language" }, { 0x4AF36C62, "SubtitleCount" }, { 0xD7C152C5, "FirstSubtitleIndex" },
            { 0xF5EBA192, "Persistence" },
        };

        public static string Table(uint hash)
        {
            string name;
            return Tables.TryGetValue(hash, out name) ? name : null;
        }

        public static string Field(uint hash)
        {
            string name;
            if (Fields.TryGetValue(hash, out name))
                return name;
            return SoundBankParameterTable.IsNamed(hash) ? SoundBankParameterTable.NameOf(hash) : null;
        }
    }

    /// <summary>One field record (0x18 bytes) and, for a class 2 field, the elements behind its pointer.</summary>
    internal sealed class BankField
    {
        public int Index;
        public int RecordOffset;        // relative to the dataset
        public uint Id;
        public uint Type;               // +0x04
        public uint Value;              // +0x08
        public uint Pad;                // +0x0C, kept as read
        public uint Ptr;                // +0x10, SBle-relative, as read
        public uint[] Elements;         // class 2: the raw elements
        public int ArrayOffset;         // class 2: where the elements were read (file offset), 0 when none

        public string Name { get { return BankNames.Field(Id) ?? BankBytes.Hex(Id); } }
        public int DataType { get { return (int)(Type & 0xFF); } }
        public int Class { get { return (int)((Type >> 8) & 0xFF); } }
        public int Shift { get { return (int)((Type >> 16) & 0xFF); } }
        public int Width { get { return (int)(Type >> 24); } }
        public int Step { get { return unchecked((short)(Type >> 16)); } }

        /// <summary>The value of every row, the way FUN_147981bc0 reads them (arithmetic modulo 2^32).</summary>
        public uint[] Decode(int rows)
        {
            uint[] values = new uint[rows];
            switch (Class)
            {
                case 0:
                    for (int i = 0; i < rows; i++)
                        values[i] = Value;
                    return values;
                case 1:
                    for (int i = 0; i < rows; i++)
                        values[i] = unchecked(Value + (uint)(Step * i));
                    return values;
                case 2:
                    if (Elements == null || Elements.Length < rows)
                        return null;
                    for (int i = 0; i < rows; i++)
                        values[i] = unchecked((Elements[i] << Shift) + Value);
                    return values;
                default:
                    return null;
            }
        }

        public BankField Clone()
        {
            BankField copy = (BankField)MemberwiseClone();
            copy.Elements = Elements == null ? null : (uint[])Elements.Clone();
            return copy;
        }
    }

    /// <summary>One parameter record (0x18 bytes) of a dataset's group-parameter table.</summary>
    internal sealed class BankParam
    {
        public int Index;
        public int RecordOffset;        // relative to the dataset
        public uint Id;
        public uint W4;                 // count (bits 0..23), array element width (24..27), bits 28..31 kept
        public int Min;                 // +0x08
        public int Max;                 // +0x0C
        public uint Ptr;                // +0x10, SBle-relative, as read
        public uint[] Elements;         // the array behind the pointer (value - min), or null
        public int ArrayOffset;         // where it was read, 0 when none

        /// <summary>The record whose array this one shares (the same pointer in the file), or -1.</summary>
        public int SharesArrayOf = -1;

        public int Count { get { return (int)(W4 & 0xFFFFFF); } }
        public int Width { get { return (int)((W4 >> 24) & 0xF); } }
        public string Name { get { return SoundBankParameterTable.NameOf(Id); } }

        /// <summary>
        /// The value each index stands for: the inverse of FUN_147986690. Index 0 is min and index count-1 is max
        /// without the array being read; between them an array gives min + element, no array gives min + i*step with
        /// step = (max - min) / (count - 1).
        /// </summary>
        public int[] Values()
        {
            int count = Count;
            int[] values = new int[Math.Max(count, 0)];
            if (count <= 0)
                return values;
            if (count == 1)
            {
                values[0] = Min;
                return values;
            }

            if (Elements != null)
            {
                for (int i = 0; i < count; i++)
                    values[i] = unchecked(Min + (int)Elements[i]);
            }
            else
            {
                int step = (Max - Min) / (count - 1);
                for (int i = 0; i < count; i++)
                    values[i] = Min + i * step;
            }

            values[0] = Min;
            values[count - 1] = Max;
            return values;
        }

        /// <summary>FUN_147986690 for one parameter: the index of a value, or -1 when the game finds none.</summary>
        public int IndexOf(int value)
        {
            int count = Count;
            if (count <= 0 || value < Min || value > Max)
                return -1;
            if (value == Min)
                return 0;
            if (value == Max)
                return count - 1;

            uint relative = unchecked((uint)(value - Min));
            if (Elements == null)
            {
                if (count < 2)
                    return -1;
                int step = (Max - Min) / (count - 1);
                if (step == 0 || (int)relative % step != 0)
                    return -1;
                return (int)relative / step;
            }

            // A lower bound over the array, as the exe does it; a miss is "no index".
            int lo = 0, hi = Math.Min(count, Elements.Length);
            while (lo < hi)
            {
                int mid = (lo + hi) / 2;
                if (Elements[mid] < relative)
                    lo = mid + 1;
                else
                    hi = mid;
            }

            return lo < Math.Min(count, Elements.Length) && Elements[lo] == relative ? lo : -1;
        }

        public BankParam Clone()
        {
            BankParam copy = (BankParam)MemberwiseClone();
            copy.Elements = Elements == null ? null : (uint[])Elements.Clone();
            return copy;
        }
    }

    /// <summary>One group record (0x20 bytes): the parameters it is selected by and the rows each combination owns.</summary>
    internal sealed class BankGroup
    {
        public int Index;
        public int RecordOffset;        // relative to the dataset
        public uint StartPtr;           // +0x00, SBle-relative
        public uint CountPtr;           // +0x08
        public uint RemapPtr;           // +0x10
        public uint W18;                // low 24 bits: start-table width, or the uniform block size
        public int FirstParam;          // +0x1C
        public int CountBits;           // +0x1E
        public int ParamCount;          // +0x1F
        public uint[] Starts;           // the start table (combinations + 1 entries) when StartPtr != 0
        public int StartsOffset;        // where it was read

        public int TableWidth { get { return (int)(W18 & 0xFFFFFF); } }
        public bool Uniform { get { return StartPtr == 0; } }

        public BankGroup Clone()
        {
            BankGroup copy = (BankGroup)MemberwiseClone();
            copy.Starts = Starts == null ? null : (uint[])Starts.Clone();
            return copy;
        }
    }

    /// <summary>A value dictionary a dataset carries for one of its columns (see the layout notes at the top).</summary>
    internal sealed class BankDictionary
    {
        public int FieldIndex;
        public int Offset;              // where it was found
        public int Width;
    }

    internal sealed class BankDataset
    {
        public int TocIndex;
        public int Offset;              // file offset (as read)
        public int End;                 // the region's end (as read)
        public uint NameHash;
        public byte[] Header;           // the 0x48 header bytes
        public int Rows;
        public int RecordsEnd;          // relative, TESD+0x04
        public int FieldTable, GroupTable, ParamTable;
        public byte[] Records;          // bytes [0x48, RecordsEnd)
        public List<BankField> Fields = new List<BankField>();
        public List<BankGroup> Groups = new List<BankGroup>();
        public List<BankParam> Params = new List<BankParam>();
        public List<BankDictionary> Dictionaries = new List<BankDictionary>();

        /// <summary>The bytes after the records, as read.</summary>
        public byte[] DataArea;

        /// <summary>Null when this dataset's data area is reproduced exactly by the shipped layout, else why not.</summary>
        public string LayoutProblem;

        /// <summary>Which of the header's two slots the file's chain carries (a bank with no chunk has no tail slot).</summary>
        public bool ChainsHeader10, ChainsHeader18;

        /// <summary>Set by an edit: lay this dataset's data area out again instead of copying it.</summary>
        public bool Relay;

        public string Name { get { return BankNames.Table(NameHash) ?? BankBytes.Hex(NameHash); } }

        public BankField Field(uint id)
        {
            foreach (BankField field in Fields)
            {
                if (field.Id == id)
                    return field;
            }

            return null;
        }

        public List<BankParam> ParamsOf(BankGroup group)
        {
            List<BankParam> list = new List<BankParam>();
            for (int i = group.FirstParam; i < group.FirstParam + group.ParamCount && i < Params.Count; i++)
                list.Add(Params[i]);
            return list;
        }

        public int Combinations(BankGroup group)
        {
            long total = 1;
            foreach (BankParam param in ParamsOf(group))
                total *= Math.Max(param.Count, 0);
            return total > int.MaxValue ? int.MaxValue : (int)total;
        }
    }

    /// <summary>A whole bank: header, table of contents, datasets, tail, and the relocation chain as read.</summary>
    internal sealed class BankImage
    {
        public byte[] Data;
        public int Sble;
        public int TocRel;              // SBle-relative
        public List<BankDataset> Datasets = new List<BankDataset>();
        public bool HasTail;            // the SBle+0x20 slot points at a 16-byte tail (the chunk's GUID)
        public int TailOffset;

        /// <summary>The slots the file's chain carries, as walked; null with <see cref="ChainProblem"/> when broken.</summary>
        public List<int> Chain;
        public string ChainProblem;

        public BankDataset Dataset(uint nameHash)
        {
            foreach (BankDataset dataset in Datasets)
            {
                if (dataset.NameHash == nameHash)
                    return dataset;
            }

            return null;
        }

        // ── the relocation chain ─────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Walks the chain the way FUN_14798a760 does. Null when it reaches its end, otherwise the first rule the file
        /// breaks - which is a file the game refuses to load (or one it relocates wrong).
        /// </summary>
        public static string WalkChain(byte[] data, int sble, out List<int> slots)
        {
            slots = new List<int>();
            if (data == null || sble < 0 || sble + 0x20 > data.Length)
                return "the file is too small to hold an SBle header";

            long size = data.Length - sble;
            HashSet<int> seen = new HashSet<int>();
            int at = sble + 0x18;
            while (true)
            {
                if (!seen.Add(at))
                    return "the relocation chain loops back to " + BankBytes.Hex(at - sble);
                if (at < sble || at + 8 > data.Length)
                    return "a relocation slot at " + BankBytes.Hex(at - sble) + " is outside the file";
                if (((at - sble) & 7) != 0)
                    return "the relocation slot at " + BankBytes.Hex(at - sble) + " is not 8-aligned";

                uint ptr = BankBytes.U32(data, at);
                uint link = BankBytes.U32(data, at + 4);
                if (ptr != 0xFFFFFFFF && ptr >= size)
                    return "the relocation slot at " + BankBytes.Hex(at - sble) + " points at " + BankBytes.Hex(ptr)
                        + ", outside the " + size.ToString("N0", CultureInfo.InvariantCulture) + "-byte bank";

                slots.Add(at);
                if (link == 0xFFFFFFFF)
                    return null;
                at = sble + (int)link;
            }
        }

        // ── reading ───────────────────────────────────────────────────────────────────────────────────────────

        public static BankImage Read(byte[] data, out string error)
        {
            error = null;
            if (data == null || data.Length < 0x60)
            {
                error = "the file is too small to be a sound bank";
                return null;
            }

            BankImage image = new BankImage { Data = data };
            if (Magic(data, 0x10, "SBle"))
                image.Sble = 0x10;
            else if (Magic(data, 0, "SBle"))
                image.Sble = 0;
            else
            {
                error = "no SBle header: not a sound bank";
                return null;
            }

            int sble = image.Sble;
            List<int> chain;
            image.ChainProblem = WalkChain(data, sble, out chain);
            image.Chain = image.ChainProblem == null ? chain : null;
            HashSet<int> slotSet = new HashSet<int>(chain);

            int count = BankBytes.U16(data, sble + 0x0A);
            image.TocRel = (int)BankBytes.U32(data, sble + 0x18);
            if (count <= 0 || count > 32 || image.TocRel <= 0 || sble + image.TocRel + 8 * count > data.Length)
            {
                error = "the bank's table of contents is not readable (" + count + " datasets at "
                    + BankBytes.Hex(image.TocRel) + ")";
                return null;
            }

            uint tail = BankBytes.U32(data, sble + 0x20);
            image.HasTail = tail != 0 && tail != 0xFFFFFFFF && sble + (long)tail == data.Length - 16;
            image.TailOffset = image.HasTail ? sble + (int)tail : data.Length;

            List<int> offsets = new List<int>();
            for (int i = 0; i < count; i++)
            {
                int at = sble + (int)BankBytes.U32(data, sble + image.TocRel + 8 * i);
                if (at < 0 || at + 0x48 > data.Length || !Magic(data, at, "TESD"))
                {
                    error = "dataset " + i + " at " + BankBytes.Hex(at) + " is not a TESD block";
                    return null;
                }

                offsets.Add(at);
            }

            List<int> sorted = offsets.OrderBy(o => o).ToList();
            for (int i = 0; i < count; i++)
            {
                int at = offsets[i];
                int rank = sorted.IndexOf(at);
                int end = rank + 1 < sorted.Count ? sorted[rank + 1] : image.TailOffset;
                string problem;
                BankDataset dataset = ReadDataset(image, i, at, end, slotSet, out problem);
                if (dataset == null)
                {
                    error = problem;
                    return null;
                }

                image.Datasets.Add(dataset);
            }

            return image;
        }

        private static bool Magic(byte[] data, int at, string magic)
        {
            if (at < 0 || at + 4 > data.Length)
                return false;
            for (int i = 0; i < 4; i++)
            {
                if (data[at + i] != magic[i])
                    return false;
            }

            return true;
        }

        private static BankDataset ReadDataset(BankImage image, int tocIndex, int at, int end, HashSet<int> slots,
            out string problem)
        {
            problem = null;
            byte[] data = image.Data;

            BankDataset ds = new BankDataset { TocIndex = tocIndex, Offset = at, End = end };
            ds.Header = new byte[0x48];
            Buffer.BlockCopy(data, at, ds.Header, 0, 0x48);
            ds.NameHash = BankBytes.U32(data, at + 0x08);
            ds.RecordsEnd = (int)BankBytes.U32(data, at + 0x04);
            ds.Rows = (int)BankBytes.U32(data, at + 0x38);
            int fieldCount = BankBytes.U16(data, at + 0x3C);
            int groupCount = BankBytes.U16(data, at + 0x3E);
            ds.FieldTable = BankBytes.U16(data, at + 0x40);
            ds.GroupTable = BankBytes.U16(data, at + 0x42);
            ds.ParamTable = BankBytes.U16(data, at + 0x44);
            ds.ChainsHeader10 = slots.Contains(at + 0x10);
            ds.ChainsHeader18 = slots.Contains(at + 0x18);

            if (ds.RecordsEnd < 0x48 || at + ds.RecordsEnd > end || ds.Rows < 0)
            {
                problem = ds.Name + " states its records end at " + BankBytes.Hex(ds.RecordsEnd) + ", outside its region";
                return null;
            }

            ds.Records = new byte[ds.RecordsEnd - 0x48];
            Buffer.BlockCopy(data, at + 0x48, ds.Records, 0, ds.Records.Length);
            ds.DataArea = new byte[end - (at + ds.RecordsEnd)];
            Buffer.BlockCopy(data, at + ds.RecordsEnd, ds.DataArea, 0, ds.DataArea.Length);

            // The records: fields from +0x40, groups from +0x42, parameters from +0x44 up to the records' end.
            if (ds.FieldTable != 0x48)
                ds.LayoutProblem = "its field records start at " + BankBytes.Hex(ds.FieldTable) + " rather than 0x48";

            for (int i = 0; i < fieldCount; i++)
            {
                int r = at + ds.FieldTable + 24 * i;
                if (r + 24 > at + ds.RecordsEnd)
                {
                    problem = ds.Name + "'s field records run past its records' end";
                    return null;
                }

                BankField field = new BankField
                {
                    Index = i,
                    RecordOffset = r - at,
                    Id = BankBytes.U32(data, r),
                    Type = BankBytes.U32(data, r + 4),
                    Value = BankBytes.U32(data, r + 8),
                    Pad = BankBytes.U32(data, r + 12),
                    Ptr = BankBytes.U32(data, r + 16),
                };
                ds.Fields.Add(field);
            }

            int paramCount = 0;
            if (groupCount > 0)
            {
                if (ds.GroupTable != ds.FieldTable + 24 * fieldCount)
                    ds.LayoutProblem = ds.LayoutProblem ?? "its group records do not follow its field records";
                for (int i = 0; i < groupCount; i++)
                {
                    int r = at + ds.GroupTable + 0x20 * i;
                    if (r + 0x20 > at + ds.RecordsEnd)
                    {
                        problem = ds.Name + "'s group records run past its records' end";
                        return null;
                    }

                    BankGroup group = new BankGroup
                    {
                        Index = i,
                        RecordOffset = r - at,
                        StartPtr = BankBytes.U32(data, r),
                        CountPtr = BankBytes.U32(data, r + 8),
                        RemapPtr = BankBytes.U32(data, r + 0x10),
                        W18 = BankBytes.U32(data, r + 0x18),
                        FirstParam = BankBytes.U16(data, r + 0x1C),
                        CountBits = data[r + 0x1E],
                        ParamCount = data[r + 0x1F],
                    };
                    ds.Groups.Add(group);
                    paramCount = Math.Max(paramCount, group.FirstParam + group.ParamCount);
                }

                if (ds.ParamTable != ds.GroupTable + 0x20 * groupCount && paramCount > 0)
                    ds.LayoutProblem = ds.LayoutProblem ?? "its parameter records do not follow its group records";
                int fromEnd = ds.ParamTable > 0 ? (ds.RecordsEnd - ds.ParamTable) / 0x18 : 0;
                paramCount = Math.Max(paramCount, fromEnd);
            }

            for (int i = 0; i < paramCount; i++)
            {
                int r = at + ds.ParamTable + 0x18 * i;
                if (r + 0x18 > at + ds.RecordsEnd)
                {
                    problem = ds.Name + "'s parameter records run past its records' end";
                    return null;
                }

                BankParam param = new BankParam
                {
                    Index = i,
                    RecordOffset = r - at,
                    Id = BankBytes.U32(data, r),
                    W4 = BankBytes.U32(data, r + 4),
                    Min = BankBytes.I32(data, r + 8),
                    Max = BankBytes.I32(data, r + 12),
                    Ptr = BankBytes.U32(data, r + 16),
                };
                ds.Params.Add(param);
            }

            int recordsEnd = ds.ParamTable > 0 && paramCount > 0 ? ds.ParamTable + 0x18 * paramCount
                : (groupCount > 0 ? ds.GroupTable + 0x20 * groupCount : ds.FieldTable + 24 * fieldCount);
            if (recordsEnd != ds.RecordsEnd)
                ds.LayoutProblem = ds.LayoutProblem ?? "its records end at " + BankBytes.Hex(recordsEnd)
                    + " where its header says " + BankBytes.Hex(ds.RecordsEnd);

            ReadBlocks(image, ds);
            CheckLayout(image, ds);
            return ds;
        }

        /// <summary>Reads every array a record points at; a pointer outside the region marks the dataset unreadable.</summary>
        private static void ReadBlocks(BankImage image, BankDataset ds)
        {
            byte[] data = image.Data;
            int sble = image.Sble;
            int dataStart = ds.Offset + ds.RecordsEnd;

            foreach (BankField field in ds.Fields)
            {
                if (field.Ptr == 0)
                    continue;

                int at = sble + (int)field.Ptr;
                if (field.Class != 2)
                {
                    ds.LayoutProblem = ds.LayoutProblem ?? field.Name + " is stored in class " + field.Class
                        + " (a palette or a lookup), which this editor reads but does not write";
                    continue;
                }

                int width = field.Width;
                if (width != 1 && width != 2 && width != 4)
                {
                    ds.LayoutProblem = ds.LayoutProblem ?? field.Name + " states element width " + width;
                    continue;
                }

                if (at < dataStart || (long)at + (long)ds.Rows * width > ds.End)
                {
                    ds.LayoutProblem = ds.LayoutProblem ?? field.Name + "'s array is outside " + ds.Name + "'s data area";
                    continue;
                }

                field.ArrayOffset = at;
                field.Elements = new uint[ds.Rows];
                for (int i = 0; i < ds.Rows; i++)
                    field.Elements[i] = BankBytes.Elem(data, at + i * width, width);
            }

            Dictionary<uint, int> arrayOwner = new Dictionary<uint, int>();
            foreach (BankParam param in ds.Params)
            {
                if (param.Ptr == 0)
                    continue;

                int width = param.Width;
                int at = sble + (int)param.Ptr;
                if (width != 1 && width != 2 && width != 4)
                {
                    ds.LayoutProblem = ds.LayoutProblem ?? param.Name + "'s value array states width " + width;
                    continue;
                }

                if (at < dataStart || (long)at + (long)param.Count * width > ds.End)
                {
                    ds.LayoutProblem = ds.LayoutProblem ?? param.Name + "'s value array is outside " + ds.Name + "'s data area";
                    continue;
                }

                int owner;
                if (arrayOwner.TryGetValue(param.Ptr, out owner))
                {
                    param.SharesArrayOf = owner;
                    if (ds.Params[owner].Count != param.Count || ds.Params[owner].Width != width)
                        ds.LayoutProblem = ds.LayoutProblem ?? "two parameter records share one value array with different sizes";
                }
                else
                {
                    arrayOwner[param.Ptr] = param.Index;
                }

                param.ArrayOffset = at;
                param.Elements = new uint[param.Count];
                for (int i = 0; i < param.Count; i++)
                    param.Elements[i] = BankBytes.Elem(data, at + i * width, width);
            }

            foreach (BankGroup group in ds.Groups)
            {
                if (group.CountPtr != 0)
                    ds.LayoutProblem = ds.LayoutProblem ?? "group " + group.Index + " has a count table, which this editor does not write";
                if (group.RemapPtr != 0)
                    ds.LayoutProblem = ds.LayoutProblem ?? "group " + group.Index + " has a start remap table (+0x10), which this editor does not write";
                if (group.StartPtr == 0)
                    continue;

                int width = group.TableWidth;
                int at = sble + (int)group.StartPtr;
                int entries = ds.Combinations(group) + 1;
                if (width != 1 && width != 2 && width != 4)
                {
                    ds.LayoutProblem = ds.LayoutProblem ?? "group " + group.Index + "'s start table states width " + width;
                    continue;
                }

                if (at < dataStart || (long)at + (long)entries * width > ds.End)
                {
                    ds.LayoutProblem = ds.LayoutProblem ?? "group " + group.Index + "'s start table is outside " + ds.Name + "'s data area";
                    continue;
                }

                group.StartsOffset = at;
                group.Starts = new uint[entries];
                for (int i = 0; i < entries; i++)
                    group.Starts[i] = BankBytes.Elem(data, at + i * width, width);
            }
        }

        // ── the layout ────────────────────────────────────────────────────────────────────────────────────────

        private sealed class Piece
        {
            public string Kind;
            public int Align;
            public byte[] Bytes;
            public object Owner;        // the field, parameter or group the piece belongs to
        }

        /// <summary>The value dictionary of a column: sorted distinct (value - min), at <paramref name="width"/>.</summary>
        public static byte[] DictionaryBytes(uint[] values, int width)
        {
            if (values == null || values.Length == 0)
                return new byte[0];

            uint min = values.Min();
            List<uint> distinct = values.Select(v => unchecked(v - min)).Distinct().OrderBy(v => v).ToList();
            byte[] bytes = new byte[distinct.Count * width];
            for (int i = 0; i < distinct.Count; i++)
                BankBytes.WElem(bytes, i * width, width, distinct[i]);
            return bytes;
        }

        /// <summary>The width a column's dictionary is written at: its array's, or the narrowest holding its range.</summary>
        public static int DictionaryWidth(BankField field, uint[] values)
        {
            if (field.Class == 2)
                return field.Width;
            if (values == null || values.Length == 0)
                return 1;
            return BankBytes.Narrowest(unchecked(values.Max() - values.Min()));
        }

        /// <summary>
        /// The pieces of a dataset's data area in the shipped order. <paramref name="dictionaries"/> says which
        /// columns carry a dictionary (by field index).
        /// </summary>
        private static List<Piece> Pieces(BankDataset ds, IEnumerable<int> dictionaries)
        {
            List<Piece> pieces = new List<Piece>();
            foreach (BankField field in ds.Fields)
            {
                if (field.Class != 2 || field.Elements == null)
                    continue;

                byte[] bytes = new byte[field.Elements.Length * field.Width];
                for (int i = 0; i < field.Elements.Length; i++)
                    BankBytes.WElem(bytes, i * field.Width, field.Width, field.Elements[i]);
                pieces.Add(new Piece { Kind = "field", Align = field.Width, Bytes = bytes, Owner = field });
            }

            foreach (BankParam param in ds.Params)
            {
                if (param.Elements == null || param.SharesArrayOf >= 0)
                    continue;

                byte[] bytes = new byte[param.Elements.Length * param.Width];
                for (int i = 0; i < param.Elements.Length; i++)
                    BankBytes.WElem(bytes, i * param.Width, param.Width, param.Elements[i]);
                pieces.Add(new Piece { Kind = "values", Align = param.Width, Bytes = bytes, Owner = param });
            }

            List<BankField> withDictionary = new List<BankField>();
            foreach (int index in dictionaries)
            {
                if (index >= 0 && index < ds.Fields.Count)
                    withDictionary.Add(ds.Fields[index]);
            }

            foreach (BankField field in withDictionary.OrderBy(f => f.DataType).ThenBy(f => f.Index))
            {
                uint[] values = field.Decode(ds.Rows);
                if (values == null || values.Length == 0)
                    continue;
                int width = DictionaryWidth(field, values);
                pieces.Add(new Piece { Kind = "dictionary", Align = width, Bytes = DictionaryBytes(values, width), Owner = field });
            }

            foreach (BankGroup group in ds.Groups)
            {
                if (group.Starts == null)
                    continue;

                int width = group.TableWidth;
                byte[] bytes = new byte[group.Starts.Length * width];
                for (int i = 0; i < group.Starts.Length; i++)
                    BankBytes.WElem(bytes, i * width, width, group.Starts[i]);
                pieces.Add(new Piece { Kind = "starts", Align = width, Bytes = bytes, Owner = group });
            }

            return pieces;
        }

        /// <summary>
        /// Finds the dictionaries this dataset carries, then lays its data area out by the shipped rule and compares
        /// it with the bytes that are there. Any difference makes the dataset one an edit may not re-lay.
        /// </summary>
        private static void CheckLayout(BankImage image, BankDataset ds)
        {
            if (ds.LayoutProblem != null)
                return;

            byte[] data = image.Data;
            int dataStart = ds.Offset + ds.RecordsEnd;

            // What is known to sit in the data area before the dictionaries are looked for.
            List<KeyValuePair<int, int>> taken = new List<KeyValuePair<int, int>>();
            foreach (BankField field in ds.Fields)
                if (field.ArrayOffset > 0)
                    taken.Add(new KeyValuePair<int, int>(field.ArrayOffset, field.ArrayOffset + ds.Rows * field.Width));
            foreach (BankParam param in ds.Params)
                if (param.ArrayOffset > 0)
                    taken.Add(new KeyValuePair<int, int>(param.ArrayOffset, param.ArrayOffset + param.Count * param.Width));
            foreach (BankGroup group in ds.Groups)
                if (group.StartsOffset > 0)
                    taken.Add(new KeyValuePair<int, int>(group.StartsOffset, group.StartsOffset + group.Starts.Length * group.TableWidth));

            if (ds.Rows > 0)
            {
                foreach (BankField field in ds.Fields)
                {
                    if ((field.Class != 1 && field.Class != 2) || field.Id == BankNames.VariationIndex)
                        continue;

                    uint[] values = field.Decode(ds.Rows);
                    if (values == null)
                        continue;
                    int width = DictionaryWidth(field, values);
                    byte[] pattern = DictionaryBytes(values, width);
                    int found = Find(data, dataStart, ds.End, pattern, width, taken);
                    if (found < 0)
                        continue;

                    ds.Dictionaries.Add(new BankDictionary { FieldIndex = field.Index, Offset = found, Width = width });
                    taken.Add(new KeyValuePair<int, int>(found, found + pattern.Length));
                }
            }

            // Lay it out and compare.
            List<Piece> pieces = Pieces(ds, ds.Dictionaries.Select(d => d.FieldIndex));
            byte[] laid = Lay(pieces, dataStart, ds.End - dataStart, null);
            if (laid == null)
            {
                ds.LayoutProblem = "its arrays do not fit its region when laid out in the shipped order";
                return;
            }

            for (int i = 0; i < ds.DataArea.Length; i++)
            {
                if (i >= laid.Length || laid[i] != ds.DataArea[i])
                {
                    ds.LayoutProblem = "its data area holds bytes the shipped layout does not explain (first at "
                        + BankBytes.Hex(dataStart + i) + ")";
                    return;
                }
            }
        }

        private static int Find(byte[] data, int from, int to, byte[] pattern, int align, List<KeyValuePair<int, int>> taken)
        {
            if (pattern.Length == 0)
                return -1;

            for (int at = BankBytes.Align(from, align); at + pattern.Length <= to; at += Math.Max(align, 1))
            {
                bool same = true;
                for (int i = 0; i < pattern.Length && same; i++)
                    same = data[at + i] == pattern[i];
                if (!same)
                    continue;

                bool overlaps = false;
                foreach (KeyValuePair<int, int> range in taken)
                {
                    if (at < range.Value && range.Key < at + pattern.Length)
                        overlaps = true;
                }

                if (!overlaps)
                    return at;
            }

            return -1;
        }

        /// <summary>
        /// Lays pieces out from <paramref name="start"/> (a file offset, which is what alignment is measured against)
        /// and returns the bytes up to <paramref name="length"/> (or the natural length padded to 16 when it is -1).
        /// <paramref name="placed"/> receives each piece's file offset.
        /// </summary>
        private static byte[] Lay(List<Piece> pieces, int start, int length, Dictionary<Piece, int> placed)
        {
            int cursor = start;
            foreach (Piece piece in pieces)
            {
                cursor = BankBytes.Align(cursor, piece.Align);
                if (placed != null)
                    placed[piece] = cursor;
                cursor += piece.Bytes.Length;
            }

            int natural = cursor - start;
            if (length >= 0 && natural > length)
                return null;

            byte[] bytes = new byte[length >= 0 ? length : natural];
            cursor = start;
            foreach (Piece piece in pieces)
            {
                cursor = BankBytes.Align(cursor, piece.Align);
                Buffer.BlockCopy(piece.Bytes, 0, bytes, cursor - start, piece.Bytes.Length);
                cursor += piece.Bytes.Length;
            }

            return bytes;
        }

        // ── writing ───────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Writes the bank: datasets an edit marked <see cref="BankDataset.Relay"/> are laid out again from their
        /// records and arrays, every other dataset is copied with its pointers moved by the distance it moved, and the
        /// relocation chain is rebuilt over every pointer the file now has. Null with the reason when it cannot.
        /// </summary>
        public byte[] Write(out string error)
        {
            error = null;
            int sble = Sble;
            int count = Datasets.Count;

            // Where each dataset goes: after the TOC, 16-aligned; each region padded to 16 except the last one of a
            // bank with no tail, which ends at its last byte (both as the shipped files state them).
            List<BankDataset> order = Datasets.OrderBy(d => d.Offset).ToList();
            Dictionary<BankDataset, int> newOffset = new Dictionary<BankDataset, int>();
            Dictionary<BankDataset, byte[]> area = new Dictionary<BankDataset, byte[]>();
            Dictionary<BankDataset, Dictionary<object, int>> pieceAt = new Dictionary<BankDataset, Dictionary<object, int>>();

            // The first dataset stays where it is: nothing before it changes size.
            int cursor = order.Count > 0 ? order[0].Offset : BankBytes.Align(sble + TocRel + 8 * count, 16);

            for (int k = 0; k < order.Count; k++)
            {
                BankDataset ds = order[k];
                bool last = k + 1 == order.Count;
                newOffset[ds] = cursor;
                int dataStart = cursor + ds.RecordsEnd;

                byte[] bytes;
                if (ds.Relay)
                {
                    if (ds.LayoutProblem != null)
                    {
                        error = "refusing to lay out " + ds.Name + " again: " + ds.LayoutProblem;
                        return null;
                    }

                    List<Piece> pieces = Pieces(ds, ds.Dictionaries.Select(d => d.FieldIndex));
                    Dictionary<Piece, int> placed = new Dictionary<Piece, int>();
                    byte[] natural = Lay(pieces, dataStart, -1, placed);
                    int end = dataStart + natural.Length;
                    int padded = last && !HasTail ? end : BankBytes.Align(end, 16);
                    bytes = new byte[padded - dataStart];
                    Buffer.BlockCopy(natural, 0, bytes, 0, natural.Length);

                    Dictionary<object, int> owners = new Dictionary<object, int>();
                    foreach (KeyValuePair<Piece, int> pair in placed)
                    {
                        if (pair.Key.Kind != "dictionary")
                            owners[pair.Key.Owner] = pair.Value;
                    }

                    pieceAt[ds] = owners;
                }
                else
                {
                    bytes = ds.DataArea;
                }

                area[ds] = bytes;
                cursor = dataStart + bytes.Length;
            }

            int length = cursor + (HasTail ? 16 : 0);
            byte[] output = new byte[length];

            // Everything before the first dataset is copied: the 16 bytes before the SBle, the header, the TOC.
            int head = Math.Min(order.Count > 0 ? order[0].Offset : Data.Length, Data.Length);
            Buffer.BlockCopy(Data, 0, output, 0, head);

            // The SBle's size word moves by exactly what the file grew; its tail slot is restated.
            uint sizeWord = BankBytes.U32(Data, sble + 4);
            BankBytes.W32(output, sble + 4, unchecked(sizeWord + (uint)(length - Data.Length)));
            if (HasTail)
                BankBytes.W32(output, sble + 0x20, (uint)(cursor - sble));

            foreach (BankDataset ds in Datasets)
            {
                int at = newOffset[ds];
                int delta = at - ds.Offset;
                BankBytes.W32(output, sble + TocRel + 8 * ds.TocIndex, (uint)(at - sble));

                Buffer.BlockCopy(ds.Header, 0, output, at, 0x48);
                BankBytes.W32(output, at + 0x38, (uint)ds.Rows);
                if (ds.ChainsHeader18 && HasTail)
                    BankBytes.W32(output, at + 0x18, (uint)(cursor - sble));
                Buffer.BlockCopy(ds.Records, 0, output, at + 0x48, ds.Records.Length);
                byte[] bytes = area[ds];
                Buffer.BlockCopy(bytes, 0, output, at + ds.RecordsEnd, bytes.Length);

                Dictionary<object, int> owners;
                pieceAt.TryGetValue(ds, out owners);

                foreach (BankField field in ds.Fields)
                {
                    int r = at + field.RecordOffset;
                    BankBytes.W32(output, r, field.Id);
                    BankBytes.W32(output, r + 4, field.Type);
                    BankBytes.W32(output, r + 8, field.Value);
                    BankBytes.W32(output, r + 12, field.Pad);
                    uint ptr;
                    if (ds.Relay)
                    {
                        int placedAt;
                        ptr = owners != null && owners.TryGetValue(field, out placedAt) ? (uint)(placedAt - sble) : 0u;
                    }
                    else
                    {
                        ptr = field.Ptr == 0 ? 0u : (uint)(field.Ptr + delta);
                    }

                    BankBytes.W32(output, r + 16, ptr);
                    if (ptr == 0)
                        BankBytes.W32(output, r + 20, 0);
                }

                foreach (BankGroup group in ds.Groups)
                {
                    int r = at + group.RecordOffset;
                    uint start;
                    if (ds.Relay)
                    {
                        int placedAt;
                        start = owners != null && group.Starts != null && owners.TryGetValue(group, out placedAt)
                            ? (uint)(placedAt - sble) : 0u;
                    }
                    else
                    {
                        start = group.StartPtr == 0 ? 0u : (uint)(group.StartPtr + delta);
                    }

                    BankBytes.W32(output, r, start);
                    if (start == 0)
                        BankBytes.W32(output, r + 4, 0);
                    BankBytes.W32(output, r + 8, group.CountPtr == 0 ? 0u : (uint)(group.CountPtr + delta));
                    BankBytes.W32(output, r + 0x10, group.RemapPtr == 0 ? 0u : (uint)(group.RemapPtr + delta));
                    BankBytes.W32(output, r + 0x18, group.W18);
                    BankBytes.W16(output, r + 0x1C, group.FirstParam);
                    output[r + 0x1E] = (byte)group.CountBits;
                    output[r + 0x1F] = (byte)group.ParamCount;
                }

                foreach (BankParam param in ds.Params)
                {
                    int r = at + param.RecordOffset;
                    BankBytes.W32(output, r, param.Id);
                    BankBytes.W32(output, r + 4, param.W4);
                    BankBytes.W32(output, r + 8, unchecked((uint)param.Min));
                    BankBytes.W32(output, r + 12, unchecked((uint)param.Max));
                    uint ptr;
                    if (ds.Relay)
                    {
                        object owner = param.SharesArrayOf >= 0 ? (object)ds.Params[param.SharesArrayOf] : param;
                        int placedAt;
                        ptr = param.Elements != null && owners != null && owners.TryGetValue(owner, out placedAt)
                            ? (uint)(placedAt - sble) : 0u;
                    }
                    else
                    {
                        ptr = param.Ptr == 0 ? 0u : (uint)(param.Ptr + delta);
                    }

                    BankBytes.W32(output, r + 16, ptr);
                    if (ptr == 0)
                        BankBytes.W32(output, r + 20, 0);
                }
            }

            if (HasTail)
                Buffer.BlockCopy(Data, TailOffset, output, cursor, 16);

            // The chain: every pointer slot the file now has, ascending, each linked to the next.
            List<int> slots = ExpectedSlots(output, newOffset);
            for (int i = 0; i < slots.Count; i++)
            {
                uint link = i + 1 < slots.Count ? (uint)(slots[i + 1] - sble) : 0xFFFFFFFFu;
                BankBytes.W32(output, slots[i] + 4, link);
            }

            List<int> walked;
            string problem = WalkChain(output, sble, out walked);
            if (problem != null)
            {
                error = "refusing the written bank: " + problem;
                return null;
            }

            if (walked.Count != slots.Count || !walked.SequenceEqual(slots))
            {
                error = "refusing the written bank: its relocation chain does not carry every pointer it holds";
                return null;
            }

            return output;
        }

        /// <summary>
        /// The slots a written file's chain carries, in the order the shipped files chain them (ascending): the SBle's
        /// TOC and tail slots, each TOC entry, and per dataset its two header slots (where the file chained them)
        /// followed by every non-zero pointer in its field, group and parameter records.
        /// </summary>
        private List<int> ExpectedSlots(byte[] output, Dictionary<BankDataset, int> newOffset)
        {
            int sble = Sble;
            List<int> slots = new List<int>();
            slots.Add(sble + 0x18);
            if (HasTail)
                slots.Add(sble + 0x20);
            for (int i = 0; i < Datasets.Count; i++)
                slots.Add(sble + TocRel + 8 * i);

            foreach (BankDataset ds in Datasets)
            {
                int at = newOffset[ds];
                if (ds.ChainsHeader10)
                    slots.Add(at + 0x10);
                if (ds.ChainsHeader18)
                    slots.Add(at + 0x18);
                foreach (BankField field in ds.Fields)
                    if (BankBytes.U32(output, at + field.RecordOffset + 16) != 0)
                        slots.Add(at + field.RecordOffset + 16);
                foreach (BankGroup group in ds.Groups)
                {
                    int r = at + group.RecordOffset;
                    if (BankBytes.U32(output, r) != 0) slots.Add(r);
                    if (BankBytes.U32(output, r + 8) != 0) slots.Add(r + 8);
                    if (BankBytes.U32(output, r + 0x10) != 0) slots.Add(r + 0x10);
                }

                foreach (BankParam param in ds.Params)
                    if (BankBytes.U32(output, at + param.RecordOffset + 16) != 0)
                        slots.Add(at + param.RecordOffset + 16);
            }

            slots.Sort();
            return slots;
        }

        /// <summary>
        /// The pointer slots the shipped chain is expected to carry for the file as read. A bank whose chain differs
        /// from this carries a pointer this editor does not know, and is not one it writes.
        /// </summary>
        public string CheckChainIsKnown()
        {
            if (ChainProblem != null)
                return "its relocation chain is broken (" + ChainProblem + "), so the game would not load it as it is";

            Dictionary<BankDataset, int> offsets = Datasets.ToDictionary(d => d, d => d.Offset);
            List<int> expected = ExpectedSlots(Data, offsets);
            if (!expected.SequenceEqual(Chain))
            {
                int first = 0;
                while (first < expected.Count && first < Chain.Count && expected[first] == Chain[first])
                    first++;
                return "its relocation chain carries pointers this editor does not know (first difference at "
                    + BankBytes.Hex((first < Chain.Count ? Chain[first] : (first < expected.Count ? expected[first] : 0)) - Sble) + ")";
            }

            return null;
        }
    }

}
