using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace SoundEditorPlugin
{
    /// <summary>
    /// A FIFA 17 sound bank (.res) as the editor lists it: its clips (one per Variations row, in chunk order), the
    /// Selection row that plays each one, and the selection key every row is filed under.
    ///
    /// Everything is read the way FIFA17.exe reads it (see <see cref="BankImage"/> for the functions): a clip's bytes
    /// come from `Segments.SamplesOffset & ~3` (FUN_146210310), a Selection row plays the clip its VariationIndex
    /// names (FUN_146210710), and a row's key is the value tuple of the group block that holds it (FUN_147986690,
    /// FUN_147986f40). A parameter's name and its values' names come from the game's own event systems
    /// (<see cref="SoundBankParameterTable"/>); nothing here is derived from listening to a clip.
    /// </summary>
    public sealed class SoundBankFile
    {
        private static readonly Dictionary<byte, string> CodecNames = new Dictionary<byte, string>
        {
            { 0x12, "Pcm16Big" },
            { 0x14, "XAS" },
            { 0x15, "EALayer3 v5" },
            { 0x16, "EALayer3 v6" },
            { 0x19, "EaSpeex" },
            { 0x1C, "EaOpus" },
        };

        private BankImage image;

        /// <summary>Is this file a sound bank at all (it has the container header and readable datasets)?</summary>
        public bool IsBank { get; private set; }

        /// <summary>Null unless the file was refused, in which case this says why.</summary>
        public string Refusal { get; private set; }

        /// <summary>Where the SBle header starts (0x10 in shipped files).</summary>
        public int SbleOffset { get { return image == null ? 0 : image.Sble; } }

        /// <summary>
        /// Null when the relocation chain walks the way FUN_14798a760 walks it and carries every pointer the file
        /// holds; otherwise what is wrong, which is a bank the game would refuse to load or would read wrong.
        /// </summary>
        public string ChainProblem { get; private set; }

        /// <summary>The chunk's GUID as the bank's tail states it, or null when the bank has no tail (no chunk).</summary>
        public string TailGuid { get; private set; }

        /// <summary>True when the bank owns a chunk (its tail names one).</summary>
        public bool OwnsChunk { get; private set; }

        public string ChunkFileName { get; private set; }

        /// <summary>The bank's Chunks rows.</summary>
        public int ChunkRows { get; private set; }

        /// <summary>
        /// The GUID each Chunks row's ChunkId names, in row order: ChunkId is 1 + the GUID's byte offset in the bank's
        /// tail (FUN_147982a40 adds *(TESD + 0x18); its users read 16 bytes at that address minus 1). A row whose ChunkId
        /// does not land on 16 bytes inside the tail is null; empty when the bank has no tail or no Chunks dataset.
        /// </summary>
        public IList<string> ChunkGuids { get; private set; }

        /// <summary>Selection rows (one per clip in a bank this editor lists).</summary>
        public uint RowCount { get; private set; }

        /// <summary>The bank's datasets, by name and row count.</summary>
        public IList<SoundBankDataset> Datasets { get; private set; }

        public SoundBankDataset Selection { get { return Dataset("Selection"); } }
        public SoundBankDataset Variations { get { return Dataset("Variations"); } }
        public SoundBankDataset Segments { get { return Dataset("Segments"); } }
        public SoundBankDataset Chunks { get { return Dataset("Chunks"); } }

        /// <summary>The clips, in chunk order (their ordinals), after <see cref="ResolveClips"/>.</summary>
        public IList<SoundBankClip> Clips { get; private set; }

        /// <summary>A note about the clips that is not a refusal (they do not tile the chunk exactly, say).</summary>
        public string ClipNote { get; private set; }

        /// <summary>
        /// Where the game reads the clips from, by SamplesOffset bit 1 (FUN_146210310): "memory" when every clip is in
        /// the bank's memory chunk (loaded whole), "stream" when every clip is streamed from its stream chunk, null when
        /// the clips disagree or state no samples. Set by <see cref="ResolveClips"/>.
        /// </summary>
        public string ClipStore { get; private set; }

        /// <summary>The Selection groups, the way the game reads them.</summary>
        public IList<SoundBankGroup> Groups { get; private set; }

        /// <summary>The parameters a Selection row's key states, most specific group's order first.</summary>
        public IList<uint> KeyParameters { get; private set; }

        /// <summary>Why the rows' keys could not be stated, or null.</summary>
        public string KeyProblem { get; private set; }

        private int[] rowOfOrdinal;
        private int[] ordinalOfRow;
        private uint[] variationIds;
        private List<Dictionary<uint, int>> rowKeys;
        private List<SoundBankRowPlace> rowPlaces;

        public SoundBankDataset Dataset(string name)
        {
            if (Datasets == null)
                return null;
            foreach (SoundBankDataset dataset in Datasets)
            {
                if (string.Equals(dataset.Name, name, StringComparison.Ordinal))
                    return dataset;
            }

            return null;
        }

        public bool Has(string dataset) { return Dataset(dataset) != null; }

        /// <summary>The engine's name hash.</summary>
        public static uint EngineHash(string name)
        {
            return BankBytes.Hash(name);
        }

        /// <summary>Reads a .res. Never null: a file that is not a bank comes back with <see cref="IsBank"/> false and the reason.</summary>
        public static SoundBankFile Parse(byte[] res, out string error)
        {
            SoundBankFile bank = new SoundBankFile();
            bank.Datasets = new List<SoundBankDataset>();
            bank.Clips = new List<SoundBankClip>();
            bank.Groups = new List<SoundBankGroup>();
            bank.KeyParameters = new List<uint>();
            error = null;

            try
            {
                bank.image = BankImage.Read(res, out error);
                if (bank.image == null)
                {
                    bank.Refusal = error;
                    return bank;
                }

                bank.Read();
            }
            catch (Exception ex)
            {
                error = "the bank could not be read: " + ex.Message;
                bank.Refusal = error;
                bank.IsBank = false;
            }

            return bank;
        }

        private void Read()
        {
            IsBank = true;
            ChainProblem = image.ChainProblem != null
                ? "its relocation chain is broken (" + image.ChainProblem + "): FIFA17.exe's loader (FUN_14798a760) would refuse it or relocate the wrong words"
                : image.CheckChainIsKnown();

            foreach (BankDataset ds in image.Datasets)
                Datasets.Add(new SoundBankDataset { Name = ds.Name, NameHash = ds.NameHash, RowCount = (uint)ds.Rows, Offset = ds.Offset, Note = ds.LayoutProblem });

            OwnsChunk = image.HasTail;
            if (OwnsChunk)
            {
                TailGuid = Guid19(image.Data, image.TailOffset);
                ChunkFileName = TailGuid + ".chunk";
            }

            ChunkGuids = new List<string>();
            BankDataset chk = image.Dataset(BankNames.Chunks);
            ChunkRows = chk == null ? 0 : chk.Rows;
            if (OwnsChunk && chk != null)
            {
                BankField chunkId = chk.Field(BankNames.ChunkId);
                uint[] ids = chunkId == null ? null : chunkId.Decode(chk.Rows);
                for (int r = 0; ids != null && r < ids.Length; r++)
                {
                    long at = ids[r] == 0 ? -1 : image.TailOffset + (long)ids[r] - 1;
                    ChunkGuids.Add(at >= image.TailOffset && at + 16 <= image.Data.Length ? Guid19(image.Data, (int)at) : null);
                }
            }

            BankDataset sel = image.Dataset(BankNames.Selection);
            BankDataset var = image.Dataset(BankNames.Variations);
            RowCount = sel != null ? (uint)sel.Rows : 0;

            if (var != null)
            {
                BankField id = var.Field(BankNames.VariationId);
                variationIds = id == null ? null : id.Decode(var.Rows);
            }

            ReadOrder(sel, var);
            ReadKeys(sel);
        }

        /// <summary>Selection row to clip: the row's VariationIndex is the Variations row it plays (FUN_146210710).</summary>
        private void ReadOrder(BankDataset sel, BankDataset var)
        {
            if (sel == null || var == null)
                return;

            BankField index = sel.Field(BankNames.VariationIndex);
            uint[] values = index == null ? null : index.Decode(sel.Rows);
            if (values == null)
                return;

            int clips = var.Rows;
            rowOfOrdinal = Enumerable.Repeat(-1, clips).ToArray();
            ordinalOfRow = new int[sel.Rows];
            for (int row = 0; row < sel.Rows; row++)
            {
                uint ordinal = values[row];
                ordinalOfRow[row] = ordinal < clips ? (int)ordinal : -1;
                if (ordinal < clips && rowOfOrdinal[ordinal] < 0)
                    rowOfOrdinal[ordinal] = row;
            }
        }

        /// <summary>Every Selection group the way the game reads it, and each row's key and place in its block.</summary>
        private void ReadKeys(BankDataset sel)
        {
            if (sel == null)
                return;

            int rows = sel.Rows;
            rowKeys = new List<Dictionary<uint, int>>(rows);
            rowPlaces = new List<SoundBankRowPlace>(rows);
            for (int r = 0; r < rows; r++)
            {
                rowKeys.Add(new Dictionary<uint, int>());
                rowPlaces.Add(null);
            }

            List<uint> order = new List<uint>();
            BankGroup lead = sel.Groups.OrderByDescending(g => g.ParamCount).ThenBy(g => g.Index).FirstOrDefault();

            foreach (BankGroup g in sel.Groups.OrderByDescending(g => g.ParamCount).ThenBy(g => g.Index))
            {
                foreach (BankParam p in sel.ParamsOf(g))
                    if (!order.Contains(p.Id))
                        order.Add(p.Id);
            }

            KeyParameters = order;

            foreach (BankGroup g in sel.Groups)
            {
                List<BankParam> parameters = sel.ParamsOf(g);
                SoundBankGroup group = new SoundBankGroup
                {
                    Index = g.Index,
                    ParameterIds = parameters.Select(p => p.Id).ToList(),
                    ValueCounts = parameters.Select(p => p.Count).ToList(),
                    Uniform = g.Uniform,
                    BlockSize = g.Uniform ? g.TableWidth : 0,
                    TableWidth = g.Uniform ? 0 : g.TableWidth,
                };
                Groups.Add(group);

                int combos = sel.Combinations(g);
                int[][] values = parameters.Select(p => p.Values()).ToArray();
                int[] sizes = parameters.Select(p => p.Count).ToArray();

                for (int k = 0; k < combos; k++)
                {
                    long start, count;
                    if (g.Starts != null)
                    {
                        if (k + 1 >= g.Starts.Length)
                            break;
                        start = g.Starts[k];
                        count = (long)g.Starts[k + 1] - g.Starts[k];
                    }
                    else
                    {
                        count = g.TableWidth;
                        start = count * k;
                    }

                    if (count <= 0)
                    {
                        group.EmptyBlocks++;
                        continue;
                    }

                    if (start < 0 || start + count > rows)
                    {
                        group.Problem = group.Problem ?? "value index " + k + " owns rows " + start + "+" + count
                            + ", outside the " + rows + " rows";
                        continue;
                    }

                    int rest = k;
                    int[] index = new int[sizes.Length];
                    for (int p = sizes.Length - 1; p >= 0; p--)
                    {
                        index[p] = rest % sizes[p];
                        rest /= sizes[p];
                    }

                    group.Blocks++;
                    for (long r = start; r < start + count; r++)
                    {
                        for (int p = 0; p < parameters.Count; p++)
                        {
                            int value = values[p][index[p]];
                            int had;
                            if (rowKeys[(int)r].TryGetValue(parameters[p].Id, out had) && had != value)
                                KeyProblem = KeyProblem ?? "row " + r + " is " + SoundBankParameterTable.NameOf(parameters[p].Id) + "="
                                    + had + " in one group and " + value + " in another";
                            rowKeys[(int)r][parameters[p].Id] = value;
                        }

                        if (g == lead)
                        {
                            SoundBankRowPlace place = rowPlaces[(int)r];
                            if (place == null)
                                rowPlaces[(int)r] = new SoundBankRowPlace { Group = g.Index, Start = (int)start, Count = (int)count, Position = (int)(r - start) };
                            else
                                KeyProblem = KeyProblem ?? "row " + r + " is reached by two blocks of group " + g.Index;
                        }
                    }
                }
            }

            if (lead != null)
            {
                for (int r = 0; r < rows; r++)
                {
                    if (rowPlaces[r] == null)
                    {
                        KeyProblem = KeyProblem ?? "row " + r + " is in no block of group " + lead.Index + ", so no event plays it";
                        break;
                    }
                }
            }
        }

        // ─── clips ────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Maps every clip to its byte range of the chunk and reads the clip header of each. Null on success, or the
        /// sentence to show when the clip offsets could not be read.
        /// </summary>
        public string ResolveClips(byte[] chunk)
        {
            Clips = new List<SoundBankClip>();
            ClipNote = null;
            ClipStore = null;

            BankDataset seg = image == null ? null : image.Dataset(BankNames.Segments);
            if (seg == null)
                return "this bank states no Segments dataset, so its clip offsets are not in the file";

            if (seg.Rows == 0)
            {
                ClipNote = "this bank holds no clips: its row tables are empty";
                return null;
            }

            if (!OwnsChunk)
                return "this bank owns no chunk (it has no tail naming one)";

            if (chunk == null || chunk.Length == 0)
                return "no audio was found for this bank: the chunk is empty";

            BankField offsetsField = seg.Field(BankNames.SamplesOffset);
            if (offsetsField == null)
                return "this bank states no Segments.SamplesOffset";

            uint[] values = offsetsField.Decode(seg.Rows);
            if (values == null)
                return "Segments.SamplesOffset is stored in class " + offsetsField.Class + ", which this reader does not decode";

            List<long> starts = new List<long>();
            for (int i = 0; i < values.Length; i++)
            {
                // FUN_146210310: bit 0 says the segment has samples, which are at the memory chunk's address + offset
                // when bit 1 is clear and at that offset of the stream chunk when it is set; the offset is the value
                // with both bits cleared.
                uint flags = values[i] & 3u;
                if ((flags & 1u) == 0)
                    ClipNote = ClipNote ?? "clip " + i + "'s SamplesOffset states no samples (its low bits are " + flags + ")";
                else if (flags != (values[0] & 3u))
                    ClipNote = ClipNote ?? "clip " + i + "'s SamplesOffset is in a different chunk (memory or stream) from clip 0's";
                starts.Add(values[i] & ~3u);
            }

            uint firstFlags = values.Length > 0 ? values[0] & 3u : 0u;
            if (values.All(v => (v & 3u) == firstFlags))
                ClipStore = firstFlags == 1u ? "memory" : firstFlags == 3u ? "stream" : null;

            if (starts[0] != 0)
                return "the clip offsets do not start at the beginning of the chunk";

            for (int i = 0; i < starts.Count; i++)
            {
                long offset = starts[i];
                long end = i + 1 < starts.Count ? starts[i + 1] : chunk.Length;
                if (offset < 0 || offset >= chunk.Length)
                    return string.Format(CultureInfo.InvariantCulture, "clip {0} starts at {1}, outside the {2}-byte chunk", i, offset, chunk.Length);
                if (end < offset)
                    return string.Format(CultureInfo.InvariantCulture, "clip {0} ends before it starts ({1} to {2})", i, offset, end);

                SoundBankClip clip = new SoundBankClip { Ordinal = i, Offset = offset, Length = end - offset };
                ReadClipHeader(clip, chunk);
                Clips.Add(clip);
            }

            return null;
        }

        /// <summary>
        /// Maps every clip of a bank with more than one Chunks row to its row and its byte range there, the way the game
        /// finds it: the Variations row's MemoryChunkIndex (SamplesOffset bits 01) or StreamChunkIndex (bits 11) is
        /// (Chunks row &lt;&lt; 1) | present (FUN_146211360), and the clip starts at SamplesOffset &amp; ~3 of that row's
        /// chunk (FUN_146210310) and runs to the next clip in the same row, or to the row's end. <paramref name="rows"/>
        /// are the chunks in Chunks row order. Each clip's <see cref="SoundBankClip.ChunkRow"/> is its row and its
        /// <see cref="SoundBankClip.Offset"/> is inside that row. Null on success, or the sentence to show.
        /// </summary>
        public string ResolveClipRows(IList<byte[]> rows)
        {
            Clips = new List<SoundBankClip>();
            ClipNote = null;
            ClipStore = null;

            BankDataset var = image == null ? null : image.Dataset(BankNames.Variations);
            BankDataset seg = image == null ? null : image.Dataset(BankNames.Segments);
            BankDataset chk = image == null ? null : image.Dataset(BankNames.Chunks);
            if (var == null || seg == null || chk == null)
                return "this bank does not state Variations, Segments and Chunks, so it holds no clips of its own";
            if (rows == null || rows.Count != chk.Rows)
                return "this bank states " + chk.Rows + " chunks and " + (rows == null ? 0 : rows.Count) + " were found for it";

            uint[] memory = Column(var, BankNames.MemoryChunkIndex), stream = Column(var, BankNames.StreamChunkIndex);
            uint[] first = Column(var, BankNames.FirstSegmentIndex), offsets = Column(seg, BankNames.SamplesOffset);
            if (memory == null || stream == null || first == null || offsets == null)
                return "this bank's Variations or Segments columns cannot be read, so its clips cannot be placed in its chunks";

            int n = var.Rows;
            int[] rowOf = new int[n];
            long[] at = new long[n];
            uint store = n > 0 ? offsets[Math.Min((int)first[0], offsets.Length - 1)] & 3u : 0u;
            for (int o = 0; o < n; o++)
            {
                if (first[o] >= offsets.Length)
                    return "clip " + o + "'s FirstSegmentIndex " + first[o] + " is outside its " + offsets.Length + " Segments rows";
                uint word = offsets[first[o]];
                uint flags = word & 3u;
                if ((flags & 1u) == 0)
                    return "clip " + o + "'s SamplesOffset states no samples (its low bits are " + flags + ")";
                if (flags != store)
                    store = 0u;
                uint selector = flags == 1u ? memory[o] : stream[o];
                if ((selector & 1u) == 0 || (selector >> 1) >= rows.Count)
                    return "clip " + o + "'s " + (flags == 1u ? "MemoryChunkIndex" : "StreamChunkIndex") + " is " + selector
                        + ", which names no Chunks row of " + rows.Count;
                // A streamed clip's chunk is opened by the GUID of row MemoryChunkIndex >> 1 and its size is that of row
                // StreamChunkIndex >> 1 (FUN_146211360: +0x44 and +0x54); where the two differ, what plays is not settled.
                if (flags == 3u && (memory[o] >> 1) != (stream[o] >> 1))
                    return "clip " + o + " is streamed, and the game takes its chunk's GUID from Chunks row " + (memory[o] >> 1)
                        + " (MemoryChunkIndex) but its size from row " + (stream[o] >> 1) + " (StreamChunkIndex), so its bytes cannot be placed";
                rowOf[o] = (int)(selector >> 1);
                at[o] = word & ~3u;
                byte[] row = rows[rowOf[o]];
                if (row == null)
                    return "chunk row " + rowOf[o] + " of this bank is not loaded";
                if (at[o] >= row.LongLength)
                    return string.Format(CultureInfo.InvariantCulture, "clip {0} starts at {1}, outside the {2}-byte chunk of row {3}",
                        o, at[o], row.LongLength, rowOf[o]);
            }

            for (int o = 0; o < n; o++)
            {
                long end = rows[rowOf[o]].LongLength;
                for (int p = 0; p < n; p++)
                {
                    if (rowOf[p] == rowOf[o] && at[p] > at[o] && at[p] < end)
                        end = at[p];
                }

                SoundBankClip clip = new SoundBankClip { Ordinal = o, Offset = at[o], Length = end - at[o], ChunkRow = rowOf[o] };
                ReadClipHeader(clip, rows[rowOf[o]]);
                Clips.Add(clip);
            }

            ClipStore = store == 1u ? "memory" : store == 3u ? "stream" : null;
            return null;
        }

        private static uint[] Column(BankDataset ds, uint id)
        {
            BankField field = ds.Field(id);
            return field == null ? null : field.Decode(ds.Rows);
        }

        private static void ReadClipHeader(SoundBankClip clip, byte[] chunk)
        {
            long at = clip.Offset;
            if (!SoundDurationScale.IsClipHeader(chunk, at))
                return;

            clip.HeaderSize = chunk[at + 3];
            clip.Codec = chunk[at + 4];
            clip.ChannelCount = (chunk[at + 5] >> 2) + 1;
            clip.SampleRate = (chunk[at + 6] << 8) | chunk[at + 7];
            clip.HeaderOk = true;

            string name;
            clip.CodecName = CodecNames.TryGetValue(clip.Codec, out name) ? name : "Unknown (0x" + clip.Codec.ToString("X2") + ")";
        }

        // ─── what a clip is ──────────────────────────────────────────────────────────────────────────────

        /// <summary>The Selection row that plays clip <paramref name="ordinal"/>, or -1.</summary>
        public int RowOfClip(int ordinal)
        {
            if (rowOfOrdinal == null || ordinal < 0 || ordinal >= rowOfOrdinal.Length)
                return -1;
            return rowOfOrdinal[ordinal];
        }

        /// <summary>The clip a Selection row plays, or -1.</summary>
        public int ClipOfRow(int row)
        {
            if (ordinalOfRow == null || row < 0 || row >= ordinalOfRow.Length)
                return -1;
            return ordinalOfRow[row];
        }

        /// <summary>The clip's VariationId (Variations row <paramref name="ordinal"/>).</summary>
        public bool TryVariationId(int ordinal, out uint id)
        {
            id = 0;
            if (variationIds == null || ordinal < 0 || ordinal >= variationIds.Length)
                return false;
            id = variationIds[ordinal];
            return true;
        }

        /// <summary>True when a clip of this bank already carries this VariationId.</summary>
        public bool HasVariationId(uint id)
        {
            return variationIds != null && Array.IndexOf(variationIds, id) >= 0;
        }

        /// <summary>The key a clip's row is filed under: one value per <see cref="KeyParameters"/> entry, or null.</summary>
        public IList<SoundBankKeyValue> KeyOfClip(int ordinal)
        {
            int row = RowOfClip(ordinal);
            if (row < 0 || rowKeys == null || row >= rowKeys.Count)
                return null;

            List<SoundBankKeyValue> key = new List<SoundBankKeyValue>();
            foreach (uint id in KeyParameters)
            {
                int value;
                if (!rowKeys[row].TryGetValue(id, out value))
                    continue;
                key.Add(SoundBankKeyValue.Of(id, value));
            }

            return key;
        }

        /// <summary>
        /// The subtitles the game shows with clip <paramref name="ordinal"/>, in the bank's own words. FUN_1461dcc70
        /// (when the voice's +0xA8 names no other source, which this editor cannot see) reads Subtitles rows
        /// FirstSubtitleIndex + n for n below SubtitleCount - the Variations row's two fields, which FUN_146211360
        /// copies to the voice - through FUN_146210550: the row's Time (a float, seconds into the clip) and its
        /// StringId, read through FUN_147986e30 as value + *(TESD + 0x18) minus 1, i.e. the NUL-terminated string at
        /// byte StringId - 1 of the tail (0 names none). The game hands that string to its subtitle sink when the
        /// clip reaches Time. Empty when the bank has no Subtitles dataset or the clip names none.
        /// </summary>
        public IList<SoundBankSubtitle> SubtitlesOfClip(int ordinal)
        {
            List<SoundBankSubtitle> list = new List<SoundBankSubtitle>();
            BankDataset var = image == null ? null : image.Dataset(BankNames.Variations);
            BankDataset sub = image == null ? null : image.Dataset(BankNames.Subtitles);
            if (var == null || sub == null || ordinal < 0 || ordinal >= var.Rows)
                return list;

            uint[] first = Column(var, BankNames.FirstSubtitleIndex), count = Column(var, BankNames.SubtitleCount);
            if (first == null || count == null)
                return list;
            uint[] time = Column(sub, BankNames.SubtitleTime), text = Column(sub, BankNames.SubtitleStringId);

            for (long n = 0; n < count[ordinal] && n < 256; n++)
            {
                long row = first[ordinal] + n;
                SoundBankSubtitle item = new SoundBankSubtitle { Row = (int)row };
                if (row >= sub.Rows)
                {
                    item.Problem = "Subtitles row " + row + " is past the dataset's " + sub.Rows + " rows";
                }
                else
                {
                    if (time != null)
                        item.Time = BitConverter.ToSingle(BitConverter.GetBytes(time[row]), 0);
                    item.Text = text == null ? null : TailString(text[row]);
                    if (text != null && text[row] != 0 && item.Text == null)
                        item.Problem = "its StringId " + text[row] + " names no string in the bank's tail";
                }

                list.Add(item);
            }

            return list;
        }

        /// <summary>The NUL-terminated string at byte <paramref name="value"/> - 1 of the tail, or null (0, or outside it).</summary>
        private string TailString(uint value)
        {
            if (value == 0 || image == null || !image.HasTail)
                return null;
            long at = image.TailOffset + (long)value - 1;
            byte[] data = image.Data;
            if (at < image.TailOffset || at >= data.Length)
                return null;

            StringBuilder text = new StringBuilder();
            for (long i = at; i < data.Length && data[i] != 0; i++)
                text.Append(data[i] >= 0x20 && data[i] < 0x7F ? (char)data[i] : '?');
            return text.ToString();
        }

        /// <summary>Where the clip's row sits in its block of the most specific group: which of the block's variations it is.</summary>
        public SoundBankRowPlace PlaceOfClip(int ordinal)
        {
            int row = RowOfClip(ordinal);
            if (row < 0 || rowPlaces == null || row >= rowPlaces.Count)
                return null;
            return rowPlaces[row];
        }

        /// <summary>One line describing the bank, for the log.</summary>
        public string Describe()
        {
            if (!IsBank)
                return Refusal ?? "not a sound bank";

            StringBuilder text = new StringBuilder();
            text.Append(string.Join(", ", Datasets.Select(d => d.Name + " " + d.RowCount.ToString(CultureInfo.InvariantCulture)).ToArray()));
            if (KeyParameters.Count > 0)
                text.Append(" · keyed by ").Append(string.Join(" + ", KeyParameters.Select(SoundBankParameterTable.NameOf).ToArray()));
            foreach (SoundBankGroup group in Groups)
                text.Append(" · ").Append(group);
            if (ChainProblem != null)
                text.Append(" · ").Append(ChainProblem);
            if (KeyProblem != null)
                text.Append(" · keys: ").Append(KeyProblem);
            if (ChunkRows > 1)
                text.Append(" · ").Append(ChunkRows.ToString(CultureInfo.InvariantCulture)).Append(" chunks");
            if (ClipStore == "memory")
                text.Append(ChunkRows > 1 ? " · its clips are in memory chunks, which the game loads whole"
                    : " · its clips are in its memory chunk, which the game loads whole");
            else if (ClipStore == "stream")
                text.Append(" · its clips are streamed from its chunk");
            return text.ToString();
        }

        private static string Guid19(byte[] buffer, int at)
        {
            if (at < 0 || at + 16 > buffer.Length)
                return null;

            uint a = BankBytes.U32(buffer, at);
            ushort b = BankBytes.U16(buffer, at + 4);
            ushort c = BankBytes.U16(buffer, at + 6);
            return string.Format("{0:x8}-{1:x4}-{2:x4}-{3:x2}{4:x2}-{5:x2}{6:x2}{7:x2}{8:x2}{9:x2}{10:x2}",
                a, b, c, buffer[at + 8], buffer[at + 9], buffer[at + 10], buffer[at + 11],
                buffer[at + 12], buffer[at + 13], buffer[at + 14], buffer[at + 15]);
        }
    }

    /// <summary>One dataset's name and row count.</summary>
    public sealed class SoundBankDataset
    {
        public string Name;
        public uint NameHash;
        public uint RowCount;
        public long Offset;

        /// <summary>Why an edit could not lay this dataset out again, or null.</summary>
        public string Note;

        public string DisplayName { get { return Name; } }

        public override string ToString() { return Name + " (" + RowCount + " rows)"; }
    }

    /// <summary>One Selection group: the parameters it is matched by and how many blocks it states.</summary>
    public sealed class SoundBankGroup
    {
        public int Index;
        public IList<uint> ParameterIds;
        public IList<int> ValueCounts;
        public bool Uniform;
        public int BlockSize;
        public int TableWidth;
        public int Blocks;
        public int EmptyBlocks;
        public string Problem;

        public override string ToString()
        {
            string names = ParameterIds.Count == 0 ? "(no parameter)" : string.Join(" + ", ParameterIds.Select(SoundBankParameterTable.NameOf).ToArray());
            return "group " + Index + " by " + names + ": " + Blocks + " block(s)"
                + (EmptyBlocks > 0 ? ", " + EmptyBlocks + " value combination(s) with no rows" : "")
                + (Uniform ? ", " + BlockSize + " row(s) each" : "")
                + (Problem != null ? " (" + Problem + ")" : "");
        }
    }

    /// <summary>A row's place in its block: rows <see cref="Start"/>..+<see cref="Count"/>, this one at <see cref="Position"/>.</summary>
    public sealed class SoundBankRowPlace
    {
        public int Group;
        public int Start;
        public int Count;
        public int Position;
    }

    /// <summary>One value of a row's key, with the names the game's event systems give the parameter and the value.</summary>
    public sealed class SoundBankKeyValue
    {
        public uint ParameterId;
        public string Parameter;
        public int Value;

        /// <summary>The event system's name for the value (`home`, `HIGH`), or null when it names none.</summary>
        public string ValueName;

        public static SoundBankKeyValue Of(uint id, int value)
        {
            return new SoundBankKeyValue
            {
                ParameterId = id,
                Parameter = SoundBankParameterTable.NameOf(id),
                Value = value,
                ValueName = SoundBankParameterTable.ValueName(id, value),
            };
        }

        /// <summary><c>pa_team_type=1 (home)</c></summary>
        public override string ToString()
        {
            return Parameter + "=" + Value.ToString(CultureInfo.InvariantCulture) + (ValueName == null ? "" : " (" + ValueName + ")");
        }
    }

    /// <summary>One Subtitles row a clip shows (<see cref="SoundBankFile.SubtitlesOfClip"/>).</summary>
    public sealed class SoundBankSubtitle
    {
        public int Row;
        public float Time;
        public string Text;
        public string Problem;

        public override string ToString()
        {
            if (Problem != null)
                return "subtitle row " + Row + ": " + Problem;
            return "subtitle " + (Text == null ? "(no string)" : "'" + Text + "'") + " at "
                + Time.ToString("0.000", CultureInfo.InvariantCulture) + " s (Subtitles row " + Row + ")";
        }
    }

    /// <summary>One clip: a byte range of the chunk and the header it starts with.</summary>
    public sealed class SoundBankClip
    {
        public int Ordinal;

        /// <summary>The bank's Chunks row the clip is in when the bank has more than one (then <see cref="Offset"/> is
        /// inside that row's chunk), else -1.</summary>
        public int ChunkRow = -1;
        public long Offset;
        public long Length;
        public byte HeaderSize;
        public byte Codec;
        public string CodecName;
        public int ChannelCount;
        public int SampleRate;
        public bool HeaderOk;

        public long End { get { return Offset + Length; } }

        /// <summary>The clip's bytes out of the chunk, or null when the range is not inside it.</summary>
        public byte[] Slice(byte[] chunk)
        {
            if (chunk == null || Offset < 0 || Length <= 0 || Offset + Length > chunk.Length)
                return null;

            byte[] slice = new byte[Length];
            Buffer.BlockCopy(chunk, (int)Offset, slice, 0, (int)Length);
            return slice;
        }

        public override string ToString()
        {
            return "#" + Ordinal + " " + (CodecName ?? "?") + " " + SampleRate + " Hz " + Length + " B";
        }
    }
}
