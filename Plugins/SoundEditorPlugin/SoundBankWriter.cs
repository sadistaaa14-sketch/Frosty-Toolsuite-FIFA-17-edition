using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace SoundEditorPlugin
{
    /// <summary>What one edit did, or the sentence saying why it did nothing.</summary>
    public sealed class SoundBankAddResult
    {
        public bool Ok;
        public string Refusal;

        /// <summary>The bank's new bytes, and the chunk's, when the edit is complete.</summary>
        public byte[] Res;
        public byte[] Chunk;

        /// <summary>The Selection row the edit added, removed or replaced the clip of.</summary>
        public int NewRow;

        /// <summary>The clip's ordinal (its Variations row, its place in the chunk) after an add, before a remove.</summary>
        public int Ordinal;

        public uint VariationId;
        public int SampleId;
        public long ClipOffset;
        public int ClipBytes;
        public string Name;

        /// <summary>The selection key the clip is filed under, as "parameter=value" pairs.</summary>
        public string Key;

        /// <summary>
        /// The byte ranges a delete took out of the chunk, each as { offset, length } in the chunk as it was before
        /// the edit, ascending and not overlapping: cutting them all from the asset's chunk rows at once
        /// (<see cref="SoundBankWriter.CutRows(IList{byte[]}, IList{long[]})"/>) gives the rows of the new chunk. Null
        /// for an edit that is not only deletes.
        /// </summary>
        public IList<long[]> CutSpans;

        /// <summary>The lines the caller logs: what the edit changed and what the file reads back as.</summary>
        public string Report;
    }

    /// <summary>
    /// The selection key a new clip is filed under: a value for every parameter the bank's Selection groups are
    /// keyed by. The game finds the clip by exactly these values (FUN_147986980 matches a group by its parameter
    /// ids, FUN_147986690 turns the values into the group's index, FUN_147986f40 the index into rows), so a clip
    /// filed under them is one the event passing them plays.
    /// </summary>
    public sealed class SoundBankClipKey
    {
        private readonly Dictionary<uint, int> values = new Dictionary<uint, int>();

        /// <summary>The value for each parameter id.</summary>
        public IDictionary<uint, int> Values { get { return values; } }

        public void Set(uint parameterId, int value) { values[parameterId] = value; }

        public override string ToString()
        {
            return string.Join(", ", values.Select(p => SoundBankParameterTable.NameOf(p.Key) + "="
                + p.Value.ToString(CultureInfo.InvariantCulture)).ToArray());
        }
    }

    /// <summary>The writer's entry points: what the editor's clip commands call, and what it asks before it enables them.</summary>
    public static class SoundBankWriter
    {
        private static string Fault(string what, Exception ex)
        {
            return what + " stopped on an internal error (" + ex.GetType().Name + ": " + ex.Message
                + "); that is a fault in this writer, not a property of the bank, and nothing was written";
        }

        private static SoundBankAddResult Refused(string why)
        {
            return new SoundBankAddResult { Ok = false, Refusal = why };
        }

        /// <summary>
        /// Appends a byte-exact copy of clip <paramref name="sourceOrdinal"/>'s audio as a new clip filed under the
        /// same selection key as that clip's row (another take of the same line).
        /// </summary>
        public static SoundBankAddResult AddClipCopy(byte[] res, byte[] chunk, int sourceOrdinal, string name)
        {
            return Run(res, chunk, false, "the edit", editor => editor.AddCopy(sourceOrdinal, name, null));
        }

        /// <summary>
        /// Appends a clip carrying <paramref name="clip"/>'s own audio (an encoded clip with its SNR header), filed
        /// under <paramref name="key"/>, or under clip <paramref name="sourceOrdinal"/>'s key when no key is given.
        /// </summary>
        public static SoundBankAddResult AddClipAudio(byte[] res, byte[] chunk, byte[] clip, int sourceOrdinal, string name,
            SoundBankClipKey key = null)
        {
            if (clip == null || clip.Length == 0)
                return Refused("the clip's audio is empty");
            return Run(res, chunk, false, "the edit", editor => editor.Add(clip, sourceOrdinal, name, key));
        }

        /// <summary>Removes clip <paramref name="ordinal"/>: its Selection row, its Variations and Segments rows and its bytes.</summary>
        public static SoundBankAddResult RemoveClip(byte[] res, byte[] chunk, int ordinal)
        {
            return Run(res, chunk, false, "the edit", editor => editor.Remove(ordinal));
        }

        /// <summary>
        /// Removes several clips in one edit (Keep First N Clips): every clip named leaves the way
        /// <see cref="RemoveClip"/> removes one, and the bank is written and read back once.
        /// </summary>
        public static SoundBankAddResult RemoveClips(byte[] res, byte[] chunk, ICollection<int> ordinals)
        {
            if (ordinals == null || ordinals.Count == 0)
                return Refused("no clip was named to remove");
            return Run(res, chunk, false, "the edit", editor => editor.RemoveMany(ordinals));
        }

        /// <summary>Replaces clip <paramref name="ordinal"/>'s audio with a clip of any length.</summary>
        public static SoundBankAddResult ReplaceClipAudio(byte[] res, byte[] chunk, int ordinal, byte[] clip)
        {
            if (clip == null || clip.Length == 0)
                return Refused("the clip's audio is empty");
            return Run(res, chunk, false, "the edit", editor => editor.Replace(ordinal, clip));
        }

        /// <summary>The sentence a copy-add would be refused with, or null when it would go through.</summary>
        public static string Refusal(byte[] res, byte[] chunk, int sourceOrdinal, string name)
        {
            return Ask(res, chunk, editor => editor.AddCopy(sourceOrdinal, name, null));
        }

        /// <summary>The sentence an add from an audio file would be refused with, asked with a stand-in clip.</summary>
        public static string RefusalForAudio(byte[] res, byte[] chunk, int sourceOrdinal, string name, SoundBankClipKey key = null)
        {
            return Ask(res, chunk, editor => editor.Add(StandInClip(), sourceOrdinal, name, key));
        }

        /// <summary>The sentence a delete would be refused with, or null.</summary>
        public static string RefusalForRemove(byte[] res, byte[] chunk, int ordinal)
        {
            return Ask(res, chunk, editor => editor.Remove(ordinal));
        }

        /// <summary>The sentence a replace would be refused with, asked with a stand-in clip, or null.</summary>
        public static string RefusalForReplace(byte[] res, byte[] chunk, int ordinal)
        {
            return Ask(res, chunk, editor => editor.Replace(ordinal, StandInClip()));
        }

        /// <summary>
        /// The bank's selection parameters and, for clip <paramref name="ordinal"/>, the key its row is filed under -
        /// what an add dialog asks for and pre-fills. Null with <paramref name="why"/> when the bank cannot be edited.
        /// </summary>
        public static SoundBankClipKey KeyOfClip(byte[] res, byte[] chunk, int ordinal, out IList<uint> parameters, out string why)
        {
            parameters = new List<uint>();
            why = null;
            try
            {
                BankEditor editor = BankEditor.Open(res, chunk, true, out why);
                if (editor == null)
                    return null;
                parameters = editor.KeyParameters();
                return editor.KeyOf(ordinal);
            }
            catch (Exception ex)
            {
                why = Fault("reading the bank's keys", ex);
                return null;
            }
        }

        /// <summary>The key of every clip, by ordinal, or null with <paramref name="why"/> when the bank cannot be edited.</summary>
        public static IList<SoundBankClipKey> KeysOfAllClips(byte[] res, byte[] chunk, out IList<uint> parameters, out string why)
        {
            parameters = new List<uint>();
            why = null;
            try
            {
                BankEditor editor = BankEditor.Open(res, chunk, true, out why);
                if (editor == null)
                    return null;
                parameters = editor.KeyParameters();
                List<SoundBankClipKey> keys = new List<SoundBankClipKey>();
                for (int i = 0; i < editor.ClipCount; i++)
                    keys.Add(editor.KeyOf(i));
                return keys;
            }
            catch (Exception ex)
            {
                why = Fault("reading the bank's keys", ex);
                return null;
            }
        }

        /// <summary>A twelve-byte clip header (EA SNR, one channel, 48 kHz, 1 sample) for the questions asked before a file is picked.</summary>
        private static byte[] StandInClip()
        {
            byte[] stand = new byte[12];
            stand[0] = 0x48; stand[3] = 0x0C; stand[4] = 0x12; stand[6] = 0xBB; stand[7] = 0x80; stand[11] = 0x01;
            return stand;
        }

        private static string Ask(byte[] res, byte[] chunk, Func<BankEditor, string> edit)
        {
            if (res == null || res.Length == 0)
                return "this asset's clip index has not been read";
            if (chunk == null)
                return "this asset's audio chunk is not loaded";

            try
            {
                string why;
                BankEditor editor = BankEditor.Open(res, chunk, true, out why);
                if (editor == null)
                    return why;
                why = edit(editor);
                if (why != null)
                    return why;
                SoundBankAddResult result = editor.Commit();
                return result.Ok ? null : result.Refusal;
            }
            catch (Exception ex)
            {
                return Fault("checking the edit", ex);
            }
        }

        private static SoundBankAddResult Run(byte[] res, byte[] chunk, bool askOnly, string what, Func<BankEditor, string> edit)
        {
            if (res == null || res.Length == 0)
                return Refused("this asset's clip index has not been read");
            if (chunk == null)
                return Refused("this asset's audio chunk is not loaded");

            try
            {
                string why;
                BankEditor editor = BankEditor.Open(res, chunk, askOnly, out why);
                if (editor == null)
                    return Refused(why);
                why = edit(editor);
                if (why != null)
                    return Refused(why);
                return editor.Commit();
            }
            catch (Exception ex)
            {
                return Refused(Fault(what, ex));
            }
        }

        /// <summary>
        /// The chunk rows several removed byte ranges leave behind, in one pass: <paramref name="spans"/> are
        /// { offset, length } pairs in the rows laid end to end (a result's <see cref="SoundBankAddResult.CutSpans"/>).
        /// Each row a range overlaps comes back with those parts cut out, every other row as the same array. One pass
        /// matters: a delete of thousands of clips cut range by range copies the whole chunk once per clip.
        /// </summary>
        public static byte[][] CutRows(IList<byte[]> rows, IList<long[]> spans)
        {
            if (rows == null)
                return null;

            List<long[]> cuts = spans == null ? new List<long[]>()
                : spans.Where(c => c != null && c.Length == 2 && c[1] > 0).OrderBy(c => c[0]).ToList();
            for (int c = 1; c < cuts.Count; c++)
            {
                if (cuts[c][0] < cuts[c - 1][0] + cuts[c - 1][1])
                    throw new ArgumentException("the ranges to cut overlap at byte " + cuts[c][0]);
            }

            byte[][] result = new byte[rows.Count][];
            long at = 0;
            int first = 0;
            for (int i = 0; i < rows.Count; i++)
            {
                byte[] data = rows[i] ?? new byte[0];
                long rowStart = at, rowEnd = at + data.Length;
                at = rowEnd;

                while (first < cuts.Count && cuts[first][0] + cuts[first][1] <= rowStart)
                    first++;

                List<long[]> inRow = new List<long[]>();
                long gone = 0;
                for (int c = first; c < cuts.Count && cuts[c][0] < rowEnd; c++)
                {
                    long from = Math.Max(cuts[c][0], rowStart);
                    long to = Math.Min(cuts[c][0] + cuts[c][1], rowEnd);
                    if (to <= from)
                        continue;
                    inRow.Add(new[] { from - rowStart, to - from });
                    gone += to - from;
                }

                if (gone == 0)
                {
                    result[i] = data;
                    continue;
                }

                byte[] shrunk = new byte[data.Length - gone];
                long source = 0, target = 0;
                foreach (long[] piece in inRow)
                {
                    long keep = piece[0] - source;
                    Buffer.BlockCopy(data, (int)source, shrunk, (int)target, (int)keep);
                    target += keep;
                    source = piece[0] + piece[1];
                }

                Buffer.BlockCopy(data, (int)source, shrunk, (int)target, (int)(data.Length - source));
                result[i] = shrunk;
            }

            return result;
        }

        /// <summary>
        /// The chunk rows a removed byte range leaves behind, given the rows laid end to end: each row the range
        /// overlaps comes back with that part cut out of it, every other row comes back as the same array.
        /// </summary>
        public static byte[][] CutRows(IList<byte[]> rows, long start, int length)
        {
            if (rows == null)
                return null;

            byte[][] cut = new byte[rows.Count][];
            long at = 0;
            long end = start + length;

            for (int i = 0; i < rows.Count; i++)
            {
                byte[] data = rows[i] ?? new byte[0];
                long from = start > at ? start : at;
                long to = end < at + data.Length ? end : at + data.Length;

                if (to <= from)
                {
                    cut[i] = data;
                }
                else
                {
                    int head = (int)(from - at);
                    int gone = (int)(to - from);
                    int tail = data.Length - head - gone;
                    byte[] shrunk = new byte[head + tail];
                    Buffer.BlockCopy(data, 0, shrunk, 0, head);
                    Buffer.BlockCopy(data, head + gone, shrunk, head, tail);
                    cut[i] = shrunk;
                }

                at += data.Length;
            }

            return cut;
        }
    }

    /// <summary>
    /// An edit of one bank, planned on the bank's values and written by <see cref="BankImage.Write"/>.
    ///
    /// The bank is read into what the game reads: per Selection row its VariationIndex, SampleId, VariationId and
    /// selection key (one value per parameter of its groups); per clip (Variations/Segments row, chunk order) its
    /// VariationId, its duration (the float pattern FUN_147982530 yields) and its byte offset (SamplesOffset & ~3,
    /// FUN_146210310). An edit changes those lists; the commit turns them back into columns, group start tables
    /// and parameter records, lays the changed datasets out the way shipped banks are laid out, rebuilds the
    /// relocation chain, and then reads the result back the way the game does before it hands anything over.
    /// </summary>
    internal sealed class BankEditor
    {
        private BankImage image;
        private byte[] chunk;
        private readonly bool askOnly;
        private long chunkLength;
        private readonly List<string> report = new List<string>();

        private BankDataset sel, var, seg, chk;
        private BankField fVariationIndex, fSampleId, fSelVariationId, fVarVariationId, fDuration, fOffset, fChunkSize;

        // Per Selection row.
        private List<int> rowOrdinal;                 // VariationIndex
        private List<uint> rowSampleId;               // null when the bank states no SampleId
        private List<uint> rowVariationId;
        private List<Dictionary<uint, int>> rowKey;

        // Per clip (ordinal).
        private List<uint> clipVariationId;
        private List<uint> clipDuration;              // float32 bit pattern
        private List<long> clipOffset;                // byte offset in the chunk
        private uint offsetFlags;                     // SamplesOffset's low bits (3: present, in the chunk)

        // The selection parameters: their sorted value lists, by id, and the order a key is stated in.
        private Dictionary<uint, List<int>> paramValues;
        private List<uint> keyOrder;

        // What the commit reports and checks.
        private int editedRow = -1;
        private int editedOrdinal = -1;
        private uint editedVariationId;
        private int editedSampleId;
        private long editedOffset;
        private int editedBytes;
        private string editedName;
        private string kind;
        private byte[] originalChunk;
        private List<long> originalOffsets;
        private List<long> originalLengths;
        private List<int> survivingFrom;              // new ordinal -> old ordinal, -1 for a new clip
        private readonly List<long[]> cutSpans = new List<long[]>();   // in the chunk as it was opened
        private bool onlyRemovals = true;
        private bool cutsKnown = true;

        private BankEditor(bool askOnly)
        {
            this.askOnly = askOnly;
        }

        private static string Ordinal(int n) { return n.ToString(CultureInfo.InvariantCulture); }

        // ─── opening a bank ──────────────────────────────────────────────────────────────────────────────────

        /// <summary>Reads and checks a bank; null with the sentence when this editor cannot keep it true.</summary>
        public static BankEditor Open(byte[] res, byte[] chunk, bool askOnly, out string why)
        {
            why = null;
            BankEditor editor = new BankEditor(askOnly);
            editor.chunk = chunk;
            editor.chunkLength = chunk.Length;
            why = editor.Load(res);
            return why == null ? editor : null;
        }

        private string Load(byte[] res)
        {
            string error;
            image = BankImage.Read(res, out error);
            if (image == null)
                return error;

            string chain = image.CheckChainIsKnown();
            if (chain != null)
                return "this bank cannot be edited: " + chain;

            sel = image.Dataset(BankNames.Selection);
            var = image.Dataset(BankNames.Variations);
            seg = image.Dataset(BankNames.Segments);
            chk = image.Dataset(BankNames.Chunks);
            if (sel == null || var == null || seg == null || chk == null)
                return "this bank does not carry all of Selection, Variations, Segments and Chunks, so it holds no clips of its own";

            BankDataset subtitles = image.Dataset(BankNames.Subtitles);
            if (subtitles != null && subtitles.Rows > 0)
                return "this bank carries subtitles, and an edit that moves clips cannot keep their rows true yet";

            foreach (BankDataset ds in new[] { sel, var, seg, chk })
            {
                if (ds.LayoutProblem != null)
                    return "this bank's " + ds.Name + " dataset cannot be laid out again: " + ds.LayoutProblem;
            }

            int n = sel.Rows;
            if (var.Rows != n || seg.Rows != n)
                return "this bank states " + n + " Selection rows, " + var.Rows + " Variations rows and " + seg.Rows
                    + " Segments rows; an edit needs one clip per row in each";
            if (chk.Rows != 1)
                return "this bank spreads its audio over " + chk.Rows + " chunks, and an edit keeps one chunk true";

            string issue = ReadSelection(n) ?? ReadVariations(n) ?? ReadSegments(n) ?? ReadChunks();
            if (issue != null)
                return issue;

            issue = ReadGroups(n);
            if (issue != null)
                return issue;

            originalChunk = chunk;
            originalOffsets = new List<long>(clipOffset);
            originalLengths = new List<long>();
            for (int i = 0; i < n; i++)
                originalLengths.Add((i + 1 < n ? clipOffset[i + 1] : chunk.LongLength) - clipOffset[i]);
            survivingFrom = Enumerable.Range(0, n).ToList();
            return null;
        }

        private static string ColumnIssue(BankDataset ds, BankField field)
        {
            if (field.Class > 2)
                return ds.Name + "." + field.Name + " is stored in class " + field.Class
                    + " (a palette or a lookup), which this editor does not re-encode";
            return null;
        }

        private string ReadSelection(int n)
        {
            fVariationIndex = sel.Field(BankNames.VariationIndex);
            fSelVariationId = sel.Field(BankNames.VariationId);
            fSampleId = sel.Field(BankNames.SampleId);
            if (fVariationIndex == null || fSelVariationId == null)
                return "this bank's Selection dataset does not state VariationIndex and VariationId";

            foreach (BankField field in sel.Fields)
            {
                if (field != fVariationIndex && field != fSelVariationId && field != fSampleId)
                    return "this bank's Selection dataset states a column this editor does not maintain (" + field.Name + ")";
                string issue = ColumnIssue(sel, field);
                if (issue != null)
                    return issue;
            }

            uint[] vi = fVariationIndex.Decode(n);
            uint[] vid = fSelVariationId.Decode(n);
            uint[] sid = fSampleId == null ? null : fSampleId.Decode(n);
            if (vi == null || vid == null || (fSampleId != null && sid == null))
                return "this bank's Selection columns cannot be read";

            bool[] seen = new bool[n];
            foreach (uint v in vi)
            {
                if (v >= n || seen[v])
                    return "this bank's Selection.VariationIndex is not one clip per row (a permutation of 0.." + (n - 1) + ")";
                seen[v] = true;
            }

            rowOrdinal = vi.Select(v => (int)v).ToList();
            rowVariationId = vid.ToList();
            rowSampleId = sid == null ? null : sid.ToList();
            if (rowSampleId != null && rowSampleId.Distinct().Count() != n)
                return "two of this bank's Selection rows share a SampleId";
            return null;
        }

        private string ReadVariations(int n)
        {
            fVarVariationId = var.Field(BankNames.VariationId);
            if (fVarVariationId == null)
                return "this bank's Variations dataset does not state VariationId";

            foreach (BankField field in var.Fields)
            {
                string issue = ColumnIssue(var, field);
                if (issue != null)
                    return issue;
                if (field == fVarVariationId)
                    continue;

                uint[] values = field.Decode(n);
                if (field.Id == BankNames.FirstSegmentIndex)
                {
                    for (int i = 0; i < n; i++)
                        if (values[i] != i)
                            return "this bank's Variations.FirstSegmentIndex is not the clip's own row, so its clips are not one segment each";
                    continue;
                }

                if (field.Class != 0)
                    return "this bank's Variations." + field.Name + " differs from clip to clip, which this editor does not maintain";
                if (field.Id == BankNames.SegmentCount && field.Value != 1)
                    return "this bank's clips have " + field.Value + " segments each, and an edit keeps one segment per clip";
            }

            uint[] ids = fVarVariationId.Decode(n);
            if (ids == null)
                return "this bank's Variations.VariationId cannot be read";
            clipVariationId = ids.ToList();
            if (clipVariationId.Distinct().Count() != n)
                return "two of this bank's clips share a VariationId";

            for (int r = 0; r < n; r++)
            {
                if (rowVariationId[r] != clipVariationId[rowOrdinal[r]])
                    return "Selection row " + r + "'s VariationId is not the VariationId of the clip it plays";
            }

            return CheckIdentityGroup(var, BankNames.VariationIndex);
        }

        private string ReadSegments(int n)
        {
            fDuration = seg.Field(BankNames.Duration);
            fOffset = seg.Field(BankNames.SamplesOffset);
            if (fDuration == null || fOffset == null)
                return "this bank's Segments dataset does not state Duration and SamplesOffset";

            foreach (BankField field in seg.Fields)
            {
                string issue = ColumnIssue(seg, field);
                if (issue != null)
                    return issue;
                if (field == fDuration || field == fOffset)
                    continue;
                if (field.Class != 0)
                    return "this bank's Segments." + field.Name + " differs from clip to clip, which this editor does not maintain";
                if (field.Id == BankNames.SeekTableOffset && (field.Value & 1) != 0)
                    return "this bank's clips carry seek tables, which an edit that moves clips does not move yet";
            }

            uint[] durations = fDuration.Decode(n);
            uint[] offsets = fOffset.Decode(n);
            if (durations == null || offsets == null)
                return "this bank's Segments columns cannot be read";

            clipDuration = durations.ToList();
            clipOffset = new List<long>();
            offsetFlags = n > 0 ? offsets[0] & 3u : 3u;
            for (int i = 0; i < n; i++)
            {
                if ((offsets[i] & 3u) != offsetFlags || offsetFlags != 3u)
                    return "clip " + i + "'s SamplesOffset does not state a clip in this bank's own chunk (its flags are "
                        + (offsets[i] & 3u) + ", FUN_146210310 reads 3 as present and in the chunk)";
                clipOffset.Add(offsets[i] & ~3u);
            }

            for (int i = 0; i < n; i++)
            {
                long start = clipOffset[i];
                long end = i + 1 < n ? clipOffset[i + 1] : chunk.LongLength;
                if ((i == 0 && start != 0) || end <= start || end > chunk.LongLength)
                    return "this bank's clips do not tile its chunk (clip " + i + " runs from " + start + " to " + end
                        + " in a " + chunk.LongLength + "-byte chunk)";
                if (!SoundDurationScale.IsClipHeader(chunk, start))
                    return "clip " + i + " does not start with a clip header at byte " + start + " of the chunk";
            }

            return CheckIdentityGroup(seg, BankNames.SegmentIndex);
        }

        private string ReadChunks()
        {
            fChunkSize = chk.Field(BankNames.ChunkSize);
            if (fChunkSize == null)
                return "this bank's Chunks dataset does not state ChunkSize";
            foreach (BankField field in chk.Fields)
            {
                if (field.Class != 0)
                    return "this bank's Chunks." + field.Name + " is not one value, which this editor does not maintain";
            }

            if (fChunkSize.Value != chunk.Length)
                return "this bank's Chunks.ChunkSize says " + fChunkSize.Value.ToString("N0", CultureInfo.InvariantCulture)
                    + " bytes and the chunk it was handed holds " + chunk.Length.ToString("N0", CultureInfo.InvariantCulture);

            return CheckIdentityGroup(chk, BankNames.ChunkIndex);
        }

        /// <summary>A dataset whose rows are found by their own index: one group, one parameter 0..rows-1, blocks of one.</summary>
        private static string CheckIdentityGroup(BankDataset ds, uint key)
        {
            bool ok = ds.Groups.Count == 1 && ds.Params.Count == 1 && ds.Params[0].Id == key
                && ds.Params[0].Count == ds.Rows && ds.Params[0].Min == 0 && ds.Params[0].Max == ds.Rows - 1
                && ds.Params[0].Elements == null && ds.Groups[0].Uniform && ds.Groups[0].TableWidth == 1
                && ds.Groups[0].ParamCount == 1 && ds.Groups[0].FirstParam == 0;
            return ok ? null : "this bank's " + ds.Name + " rows are not found by their own index the way shipped banks state it";
        }

        /// <summary>
        /// Reads every Selection group the way the game does, and states each row's key from them: the value of each
        /// parameter whose block holds the row. A row two groups disagree about, a row no group reaches, or a group
        /// whose blocks are not in row order is a bank whose keys an edit could not keep.
        /// </summary>
        private string ReadGroups(int n)
        {
            paramValues = new Dictionary<uint, List<int>>();
            rowKey = new List<Dictionary<uint, int>>();
            for (int r = 0; r < n; r++)
                rowKey.Add(new Dictionary<uint, int>());

            foreach (BankParam param in sel.Params)
            {
                List<int> values = param.Values().ToList();
                for (int i = 1; i < values.Count; i++)
                {
                    if (values[i] <= values[i - 1])
                        return "this bank's " + param.Name + " values are not in ascending order, which FUN_147986690's search needs";
                }

                List<int> known;
                if (paramValues.TryGetValue(param.Id, out known))
                {
                    if (!known.SequenceEqual(values))
                        return "two of this bank's groups state different values for " + param.Name;
                }
                else
                {
                    paramValues[param.Id] = values;
                }
            }

            keyOrder = new List<uint>();
            foreach (BankGroup group in sel.Groups.OrderByDescending(g => g.ParamCount).ThenBy(g => g.Index))
            {
                foreach (BankParam param in sel.ParamsOf(group))
                {
                    if (!keyOrder.Contains(param.Id))
                        keyOrder.Add(param.Id);
                }
            }

            foreach (BankGroup group in sel.Groups)
            {
                List<BankParam> parameters = sel.ParamsOf(group);
                if (parameters.Count != group.ParamCount)
                    return "Selection group " + group.Index + " names parameter records the bank does not have";

                int combos = sel.Combinations(group);
                int[] sizes = parameters.Select(p => p.Count).ToArray();
                int[][] valueLists = parameters.Select(p => p.Values()).ToArray();
                int[] owner = new int[n];
                for (int r = 0; r < n; r++)
                    owner[r] = -1;

                long previousEnd = 0;
                for (int k = 0; k < combos; k++)
                {
                    long start, count;
                    if (group.Starts != null)
                    {
                        start = group.Starts[k];
                        count = (long)group.Starts[k + 1] - group.Starts[k];
                    }
                    else
                    {
                        count = group.TableWidth;
                        start = count * k;
                    }

                    if (count < 0 || start + count > n || start != previousEnd)
                        return "Selection group " + group.Index + "'s blocks are not in row order (value index " + k
                            + " owns rows " + start + "+" + count + " of " + n + ")";
                    previousEnd = start + count;

                    int rest = k;
                    int[] index = new int[sizes.Length];
                    for (int p = sizes.Length - 1; p >= 0; p--)
                    {
                        index[p] = rest % sizes[p];
                        rest /= sizes[p];
                    }

                    for (long r = start; r < start + count; r++)
                    {
                        owner[r] = k;
                        for (int p = 0; p < parameters.Count; p++)
                        {
                            int value = valueLists[p][index[p]];
                            int had;
                            if (rowKey[(int)r].TryGetValue(parameters[p].Id, out had) && had != value)
                                return "Selection row " + r + " is " + parameters[p].Name + "=" + had + " in one group and "
                                    + value + " in another";
                            rowKey[(int)r][parameters[p].Id] = value;
                        }
                    }
                }

                if (previousEnd != n)
                    return "Selection group " + group.Index + " reaches " + previousEnd + " of the bank's " + n + " rows";
            }

            for (int r = 0; r < n; r++)
            {
                foreach (uint id in keyOrder)
                {
                    if (!rowKey[r].ContainsKey(id))
                        return "Selection row " + r + " has no " + SoundBankParameterTable.NameOf(id) + " value";
                }
            }

            return null;
        }

        // ─── the key ─────────────────────────────────────────────────────────────────────────────────────────

        public int ClipCount { get { return rowOrdinal.Count; } }

        public IList<uint> KeyParameters()
        {
            return new List<uint>(keyOrder);
        }

        public SoundBankClipKey KeyOf(int ordinal)
        {
            int row = RowOf(ordinal);
            if (row < 0)
                return null;
            SoundBankClipKey key = new SoundBankClipKey();
            foreach (uint id in keyOrder)
                key.Set(id, rowKey[row][id]);
            return key;
        }

        private int RowOf(int ordinal)
        {
            return rowOrdinal.IndexOf(ordinal);
        }

        private string Describe(Dictionary<uint, int> key)
        {
            List<string> parts = new List<string>();
            foreach (uint id in keyOrder)
            {
                int value;
                if (!key.TryGetValue(id, out value))
                    continue;
                string name = SoundBankParameterTable.ValueName(id, value);
                parts.Add(SoundBankParameterTable.NameOf(id) + "=" + value.ToString(CultureInfo.InvariantCulture)
                    + (name == null ? "" : " (" + name + ")"));
            }

            return parts.Count == 0 ? "(no key: the bank has no selection parameters)" : string.Join(", ", parts.ToArray());
        }

        // ─── the edits ───────────────────────────────────────────────────────────────────────────────────────

        public string AddCopy(int sourceOrdinal, string name, SoundBankClipKey key)
        {
            if (sourceOrdinal < 0 || sourceOrdinal >= clipOffset.Count)
                return "clip " + sourceOrdinal + " is outside 0.." + (clipOffset.Count - 1) + ", and a copy needs one that exists";

            long start = originalOffsets[sourceOrdinal];
            long length = originalLengths[sourceOrdinal];
            byte[] copy = new byte[length];
            Buffer.BlockCopy(chunk, (int)start, copy, 0, (int)length);
            uint duration = clipDuration[sourceOrdinal];
            return AddClip(copy, sourceOrdinal, name, key, duration);
        }

        public string Add(byte[] clip, int sourceOrdinal, string name, SoundBankClipKey key)
        {
            double seconds;
            int samples, rate;
            if (!SoundDurationScale.TryClipSeconds(clip, 0, out seconds, out samples, out rate))
                return "the audio handed in does not start a clip header (an SNR word, a codec and a sample rate), so its length cannot be stated";

            // FUN_147982530 adds element and value word as integers and the result is read as a float32: the
            // duration is the clip's own length as a float's bit pattern.
            return AddClip(clip, sourceOrdinal, name, key, SoundDurationScale.Float32Bits(seconds));
        }

        private string AddClip(byte[] clip, int sourceOrdinal, string name, SoundBankClipKey key, uint duration)
        {
            onlyRemovals = false;
            int n = rowOrdinal.Count;
            if (string.IsNullOrEmpty(name))
                return "a new clip needs a name: its VariationId is the engine hash of it";

            uint id = BankBytes.Hash(name);
            if (clipVariationId.Contains(id))
                return "\"" + name + "\" hashes to " + BankBytes.Hex(id) + ", a VariationId this bank already uses";

            // The key: the one given, or the source clip's own.
            Dictionary<uint, int> newKey = new Dictionary<uint, int>();
            if (key != null)
            {
                foreach (KeyValuePair<uint, int> pair in key.Values)
                {
                    if (!keyOrder.Contains(pair.Key))
                        return "this bank is not keyed by " + SoundBankParameterTable.NameOf(pair.Key) + "; its key is "
                            + string.Join(", ", keyOrder.Select(SoundBankParameterTable.NameOf).ToArray());
                    newKey[pair.Key] = pair.Value;
                }
            }

            int sourceRow = sourceOrdinal >= 0 && sourceOrdinal < n ? RowOf(sourceOrdinal) : -1;
            foreach (uint pid in keyOrder)
            {
                if (newKey.ContainsKey(pid))
                    continue;
                if (sourceRow < 0)
                    return "a value for " + SoundBankParameterTable.NameOf(pid) + " is needed: the game finds a clip by it";
                newKey[pid] = rowKey[sourceRow][pid];
            }

            // A value a parameter does not have yet joins its list, in order, so the game's search finds it.
            List<string> grown = new List<string>();
            foreach (uint pid in keyOrder)
            {
                List<int> values = paramValues[pid];
                int value = newKey[pid];
                int at = values.BinarySearch(value);
                if (at < 0)
                {
                    values.Insert(~at, value);
                    grown.Add(SoundBankParameterTable.NameOf(pid) + " " + value.ToString(CultureInfo.InvariantCulture));
                }
            }

            // Where the row goes: after every row whose key sorts at or before it in the most specific group, which
            // keeps every group's blocks contiguous (checked again below for every group, and at the commit).
            int position = InsertPosition(newKey);
            if (position < 0)
                return "no row position keeps every Selection group's blocks contiguous for " + Describe(newKey);

            // The audio goes at the end of the chunk, on a 4-byte boundary (FUN_146210310 reads offset & ~3).
            long start = Align4(chunkLength);
            int padded = (int)Align4(clip.Length);
            long newLength = start + padded;
            if (newLength > uint.MaxValue)
                return "the chunk would outgrow the 32-bit size the bank states";

            if (!askOnly)
            {
                byte[] grownChunk = new byte[newLength];
                Buffer.BlockCopy(chunk, 0, grownChunk, 0, chunk.Length);
                Buffer.BlockCopy(clip, 0, grownChunk, (int)start, clip.Length);
                chunk = grownChunk;
            }

            chunkLength = newLength;

            int ordinal = n;
            rowOrdinal.Insert(position, ordinal);
            rowVariationId.Insert(position, id);
            uint sampleId = 0;
            if (rowSampleId != null)
            {
                sampleId = rowSampleId.Count == 0 ? fSampleId.Value : unchecked(rowSampleId.Max() + 1u);
                if (rowSampleId.Contains(sampleId))
                    return "no free SampleId follows this bank's highest";
                rowSampleId.Insert(position, sampleId);
            }

            rowKey.Insert(position, newKey);
            clipVariationId.Add(id);
            clipDuration.Add(duration);
            clipOffset.Add(start);
            survivingFrom.Add(-1);

            kind = "added";
            editedRow = position;
            editedOrdinal = ordinal;
            editedVariationId = id;
            editedSampleId = unchecked((int)sampleId);
            editedOffset = start;
            editedBytes = clip.Length;
            editedName = name;

            report.Add(string.Format(CultureInfo.InvariantCulture,
                "added clip {0} as Selection row {1}, filed under {2}; VariationId {3} (hash of \"{4}\"){5}; {6:N0} bytes at chunk offset {7:N0}",
                ordinal, position, Describe(newKey), BankBytes.Hex(id), name,
                rowSampleId != null ? ", SampleId " + sampleId.ToString(CultureInfo.InvariantCulture) : "",
                clip.Length, start));
            if (grown.Count > 0)
                report.Add("new parameter value(s), added to the bank's lists so the game's search finds them: " + string.Join(", ", grown.ToArray()));
            report.Add(string.Format(CultureInfo.InvariantCulture, "duration {0:F6} s (float pattern {1})",
                SoundDurationScale.SecondsFromWord(duration), BankBytes.Hex(duration)));
            return null;
        }

        private static long Align4(long value)
        {
            return (value + 3) & ~3L;
        }

        /// <summary>
        /// The row the key's block ends at in the group with the most parameters, or -1 when inserting there would
        /// leave some group's rows out of order.
        /// </summary>
        private int InsertPosition(Dictionary<uint, int> key)
        {
            int n = rowKey.Count;
            if (sel.Groups.Count == 0)
                return n;

            BankGroup lead = sel.Groups.OrderByDescending(g => g.ParamCount).ThenBy(g => g.Index).First();
            long target = Combo(lead, key);
            int position = 0;
            while (position < n && Combo(lead, rowKey[position]) <= target)
                position++;

            foreach (BankGroup group in sel.Groups)
            {
                long mine = Combo(group, key);
                if (position > 0 && Combo(group, rowKey[position - 1]) > mine)
                    return -1;
                if (position < n && Combo(group, rowKey[position]) < mine)
                    return -1;
            }

            return position;
        }

        /// <summary>A key's index in a group: FUN_147986690's mixed radix over the current value lists.</summary>
        private long Combo(BankGroup group, Dictionary<uint, int> key)
        {
            long index = 0;
            foreach (BankParam param in sel.ParamsOf(group))
            {
                List<int> values = paramValues[param.Id];
                int at = values.BinarySearch(key[param.Id]);
                if (at < 0)
                    return -1;
                index = index * values.Count + at;
            }

            return index;
        }

        public string Remove(int ordinal)
        {
            int n = rowOrdinal.Count;
            if (ordinal < 0 || ordinal >= n)
                return "clip " + ordinal + " is outside 0.." + (n - 1);
            if (n == 1)
                return "this is the bank's only clip; a bank with no rows is not one this editor writes (replace its audio instead)";

            int row = RowOf(ordinal);
            long start = clipOffset[ordinal];
            long end = ordinal + 1 < n ? clipOffset[ordinal + 1] : chunkLength;
            long bytes = end - start;

            kind = "removed";
            editedRow = row;
            editedOrdinal = ordinal;
            editedVariationId = clipVariationId[ordinal];
            editedSampleId = rowSampleId == null ? 0 : unchecked((int)rowSampleId[row]);
            editedOffset = start;
            editedBytes = (int)bytes;
            string key = Describe(rowKey[row]);

            if (!askOnly)
            {
                byte[] shrunk = new byte[chunkLength - bytes];
                Buffer.BlockCopy(chunk, 0, shrunk, 0, (int)start);
                Buffer.BlockCopy(chunk, (int)end, shrunk, (int)start, (int)(chunkLength - end));
                chunk = shrunk;
            }

            chunkLength -= bytes;
            NoteCut(ordinal, bytes);

            rowOrdinal.RemoveAt(row);
            rowVariationId.RemoveAt(row);
            if (rowSampleId != null)
                rowSampleId.RemoveAt(row);
            rowKey.RemoveAt(row);
            for (int r = 0; r < rowOrdinal.Count; r++)
            {
                if (rowOrdinal[r] > ordinal)
                    rowOrdinal[r]--;
            }

            clipVariationId.RemoveAt(ordinal);
            clipDuration.RemoveAt(ordinal);
            clipOffset.RemoveAt(ordinal);
            survivingFrom.RemoveAt(ordinal);
            for (int i = ordinal; i < clipOffset.Count; i++)
                clipOffset[i] -= bytes;

            report.Add(string.Format(CultureInfo.InvariantCulture,
                "removed clip {0} (Selection row {1}, {2}): {3:N0} bytes left the chunk at offset {4:N0}",
                ordinal, row, key, bytes, start));
            return null;
        }

        /// <summary>
        /// Removes every clip in <paramref name="ordinals"/> at once: the rows that play them, their Variations and
        /// Segments rows and their bytes, with one pass over the chunk however many leave. The clips that stay keep
        /// their order, so their ordinals close up and their offsets move down by the bytes removed before them.
        /// </summary>
        public string RemoveMany(ICollection<int> ordinals)
        {
            int n = rowOrdinal.Count;
            HashSet<int> gone = new HashSet<int>(ordinals);
            foreach (int o in gone)
            {
                if (o < 0 || o >= n)
                    return "clip " + o + " is outside 0.." + (n - 1);
            }

            if (gone.Count == 0)
                return "no clip was named to remove";
            if (gone.Count >= n)
                return "that removes every clip of the bank; a bank with no rows is not one this editor writes (keep at least one)";

            // Each clip's span, in the chunk as it stands: from its offset to the next clip's, or to the end.
            long[] spanStart = new long[n];
            long[] spanBytes = new long[n];
            for (int i = 0; i < n; i++)
            {
                spanStart[i] = clipOffset[i];
                spanBytes[i] = (i + 1 < n ? clipOffset[i + 1] : chunkLength) - clipOffset[i];
            }

            int[] newOrdinal = new int[n];
            long[] removedBefore = new long[n];
            long removed = 0;
            int kept = 0;
            for (int i = 0; i < n; i++)
            {
                removedBefore[i] = removed;
                if (gone.Contains(i))
                {
                    newOrdinal[i] = -1;
                    removed += spanBytes[i];
                }
                else
                {
                    newOrdinal[i] = kept++;
                }
            }

            if (!askOnly)
            {
                byte[] shrunk = new byte[chunkLength - removed];
                int at = 0;
                for (int i = 0; i < n; i++)
                {
                    if (newOrdinal[i] < 0)
                        continue;
                    Buffer.BlockCopy(chunk, (int)spanStart[i], shrunk, at, (int)spanBytes[i]);
                    at += (int)spanBytes[i];
                }

                chunk = shrunk;
            }

            for (int i = 0; i < n; i++)
            {
                if (newOrdinal[i] < 0)
                    NoteCut(i, spanBytes[i]);
            }

            chunkLength -= removed;

            List<uint> variationIds = new List<uint>(kept);
            List<uint> durations = new List<uint>(kept);
            List<long> offsets = new List<long>(kept);
            List<int> from = new List<int>(kept);
            for (int i = 0; i < n; i++)
            {
                if (newOrdinal[i] < 0)
                    continue;
                variationIds.Add(clipVariationId[i]);
                durations.Add(clipDuration[i]);
                offsets.Add(spanStart[i] - removedBefore[i]);
                from.Add(survivingFrom[i]);
            }

            clipVariationId = variationIds;
            clipDuration = durations;
            clipOffset = offsets;
            survivingFrom = from;

            List<int> rows = new List<int>(kept);
            List<uint> rowIds = new List<uint>(kept);
            List<uint> sampleIds = rowSampleId == null ? null : new List<uint>(kept);
            List<Dictionary<uint, int>> keys = new List<Dictionary<uint, int>>(kept);
            for (int r = 0; r < rowOrdinal.Count; r++)
            {
                int to = newOrdinal[rowOrdinal[r]];
                if (to < 0)
                    continue;
                rows.Add(to);
                rowIds.Add(rowVariationId[r]);
                if (sampleIds != null)
                    sampleIds.Add(rowSampleId[r]);
                keys.Add(rowKey[r]);
            }

            rowOrdinal = rows;
            rowVariationId = rowIds;
            rowSampleId = sampleIds;
            rowKey = keys;

            int first = gone.Min();
            kind = "removed";
            editedRow = -1;
            editedOrdinal = first;
            editedVariationId = 0;
            editedSampleId = 0;
            editedOffset = spanStart[first];
            editedBytes = (int)removed;

            report.Add(string.Format(CultureInfo.InvariantCulture,
                "removed {0} clip(s) (clips {1}..{2} before the edit): {3:N0} bytes left the chunk, {4} clip(s) stay",
                gone.Count, first, gone.Max(), removed, kept));
            return null;
        }

        /// <summary>
        /// Records the range a delete takes out, in the chunk as it was opened: the clip's own range there, when it is
        /// a clip of the opened bank that still spans what it spanned. Anything else (a clip this session added or
        /// replaced) leaves the cuts unstated rather than stated wrong.
        /// </summary>
        private void NoteCut(int ordinal, long bytes)
        {
            int from = survivingFrom[ordinal];
            if (from < 0 || originalLengths[from] != bytes)
            {
                cutsKnown = false;
                return;
            }

            cutSpans.Add(new[] { originalOffsets[from], originalLengths[from] });
        }

        public string Replace(int ordinal, byte[] clip)
        {
            onlyRemovals = false;
            int n = rowOrdinal.Count;
            if (ordinal < 0 || ordinal >= n)
                return "clip " + ordinal + " is outside 0.." + (n - 1);

            double seconds;
            int samples, rate;
            if (!SoundDurationScale.TryClipSeconds(clip, 0, out seconds, out samples, out rate))
                return "the audio handed in does not start a clip header (an SNR word, a codec and a sample rate), so its length cannot be stated";

            long start = clipOffset[ordinal];
            long end = ordinal + 1 < n ? clipOffset[ordinal + 1] : chunkLength;
            long padded = Align4(clip.Length);
            long delta = padded - (end - start);
            if (chunkLength + delta > uint.MaxValue)
                return "the chunk would outgrow the 32-bit size the bank states";

            if (!askOnly)
            {
                byte[] spliced = new byte[chunkLength + delta];
                Buffer.BlockCopy(chunk, 0, spliced, 0, (int)start);
                Buffer.BlockCopy(clip, 0, spliced, (int)start, clip.Length);
                Buffer.BlockCopy(chunk, (int)end, spliced, (int)(start + padded), (int)(chunkLength - end));
                chunk = spliced;
            }

            chunkLength += delta;
            for (int i = ordinal + 1; i < n; i++)
                clipOffset[i] += delta;

            uint duration = SoundDurationScale.Float32Bits(seconds);
            clipDuration[ordinal] = duration;
            survivingFrom[ordinal] = -1;

            int row = RowOf(ordinal);
            kind = "replaced";
            editedRow = row;
            editedOrdinal = ordinal;
            editedVariationId = clipVariationId[ordinal];
            editedSampleId = rowSampleId == null ? 0 : unchecked((int)rowSampleId[row]);
            editedOffset = start;
            editedBytes = clip.Length;

            report.Add(string.Format(CultureInfo.InvariantCulture,
                "replaced clip {0}'s audio (Selection row {1}, {2}): {3:N0} -> {4:N0} bytes, {5:F6} s",
                ordinal, row, Describe(rowKey[row]), end - start, clip.Length, seconds));
            return null;
        }

        // ─── the commit ──────────────────────────────────────────────────────────────────────────────────────

        public SoundBankAddResult Commit()
        {
            int n = rowOrdinal.Count;

            string issue = EncodeColumn(sel, fVariationIndex, rowOrdinal.Select(v => (uint)v).ToArray(), "VariationIndex")
                ?? EncodeColumn(sel, fSelVariationId, rowVariationId.ToArray(), "VariationId")
                ?? (fSampleId != null ? EncodeColumn(sel, fSampleId, rowSampleId.ToArray(), "SampleId") : null)
                ?? EncodeColumn(var, fVarVariationId, clipVariationId.ToArray(), "VariationId")
                ?? EncodeColumn(seg, fDuration, clipDuration.ToArray(), "Duration")
                ?? EncodeColumn(seg, fOffset, clipOffset.Select(o => (uint)o | offsetFlags).ToArray(), "SamplesOffset")
                ?? EncodeConstants(var, n) ?? EncodeConstants(seg, n);
            if (issue != null)
                return Refuse(issue);

            sel.Rows = n;
            var.Rows = n;
            seg.Rows = n;
            SetIdentity(var, n);
            SetIdentity(seg, n);
            fChunkSize.Value = (uint)chunkLength;

            issue = EncodeParams() ?? EncodeGroups(n);
            if (issue != null)
                return Refuse(issue);

            foreach (BankDataset ds in new[] { sel, var, seg })
                ds.Relay = true;

            string error;
            byte[] written = image.Write(out error);
            if (written == null)
                return Refuse(error);

            List<string> failures = ReadBack(written);
            if (failures.Count > 0)
                return Refuse("refusing to keep the edit: the file does not read back the way the game reads it - "
                    + failures[0] + (failures.Count > 1 ? " (and " + (failures.Count - 1) + " more)" : ""));

            report.Add(string.Format(CultureInfo.InvariantCulture, "bank {0:N0} -> {1:N0} bytes, chunk {2:N0} bytes, {3} clips",
                image.Data.Length, written.Length, chunkLength, n));
            report.Add("read back as the game reads it: the relocation chain (FUN_14798a760), every row's key through its "
                + "groups (FUN_147986690/FUN_147986f40), every column, and every clip's place in the chunk (" + checks + " checks)");

            return new SoundBankAddResult
            {
                Ok = true,
                Res = written,
                Chunk = askOnly ? null : chunk,
                NewRow = editedRow,
                Ordinal = editedOrdinal,
                VariationId = editedVariationId,
                SampleId = editedSampleId,
                ClipOffset = editedOffset,
                ClipBytes = editedBytes,
                Name = editedName,
                Key = editedRow >= 0 && editedRow < rowKey.Count && kind != "removed" ? Describe(rowKey[editedRow]) : null,
                CutSpans = onlyRemovals && cutsKnown && cutSpans.Count > 0 ? cutSpans.OrderBy(c => c[0]).ToList() : null,
                Report = string.Join(Environment.NewLine, report.ToArray()),
            };
        }

        private SoundBankAddResult Refuse(string why)
        {
            return new SoundBankAddResult { Ok = false, Refusal = why, Report = string.Join(Environment.NewLine, report.ToArray()) };
        }

        /// <summary>
        /// States a column's values in the class the field already uses when that class can hold them, otherwise in
        /// the cheapest one that can: one value for every row (class 0), a start and a step (class 1, the step a
        /// signed 16-bit word), an array (class 2, element = (value - bias) >> shift, bias the value word).
        /// </summary>
        private static string EncodeColumn(BankDataset ds, BankField field, uint[] values, string what)
        {
            int n = values.Length;
            bool allEqual = n == 0 || values.All(v => v == values[0]);
            int step = 0;
            bool linear = n >= 2;
            if (linear)
            {
                long s = (long)values[1] - values[0];
                linear = s >= short.MinValue && s <= short.MaxValue && s != 0;
                step = (int)s;
                for (int i = 2; linear && i < n; i++)
                    linear = values[i] == unchecked(values[0] + (uint)(step * i));
            }

            // A float column (data type 5) is read through FUN_147981ce0, whose start-and-step path this editor has
            // not traced, so it is only ever stated as one value or as an array - which is also all shipped banks do.
            // A VariationId is a hash; shipped banks state it as an array whenever it is not one value.
            bool mayStep = field.DataType != 5 && field.Id != BankNames.VariationId;
            linear = linear && mayStep;

            int target;
            if (n <= 1 || (allEqual && field.Class == 0))
                target = 0;
            else if (field.Class == 1 && linear)
                target = 1;
            else if (field.Class == 2)
                target = 2;
            else if (allEqual)
                target = 0;
            else if (linear)
                target = 1;
            else
                target = 2;

            uint typeLow = field.Type & 0xFF;
            switch (target)
            {
                case 0:
                    field.Type = typeLow;
                    field.Value = n == 0 ? field.Value : values[0];
                    field.Elements = null;
                    return null;
                case 1:
                    field.Type = typeLow | (1u << 8) | ((uint)(ushort)(short)step << 16);
                    field.Value = values[0];
                    field.Elements = null;
                    return null;
            }

            // Class 2: keep the bias and shift the field states when every value still sits on them.
            uint bias = field.Class == 2 ? field.Value : values.Min();
            int shift = field.Class == 2 ? field.Shift : 0;
            if (values.Any(v => v < bias))
                bias = values.Min();
            uint mask = shift == 0 ? 0u : (1u << shift) - 1u;
            if (values.Any(v => ((v - bias) & mask) != 0))
                shift = 0;

            uint[] elements = values.Select(v => unchecked(v - bias) >> shift).ToArray();
            uint largest = elements.Length == 0 ? 0u : elements.Max();
            int width = field.Class == 2 ? field.Width : BankBytes.Narrowest(largest);
            if (!BankBytes.Fits(largest, width))
                width = BankBytes.Narrowest(largest);

            field.Type = typeLow | (2u << 8) | ((uint)shift << 16) | ((uint)width << 24);
            field.Value = bias;
            field.Elements = elements;
            return null;
        }

        /// <summary>Every column of a clip table that is one value for all rows stays one, at any row count.</summary>
        private static string EncodeConstants(BankDataset ds, int n)
        {
            foreach (BankField field in ds.Fields)
            {
                if (field.Id == BankNames.FirstSegmentIndex)
                {
                    // Each clip is its own segment: the start-and-step form 0, 1, 2... (one row: the value 0).
                    uint typeLow = field.Type & 0xFF;
                    field.Type = n <= 1 ? typeLow : typeLow | (1u << 8) | (1u << 16);
                    field.Value = 0;
                    field.Elements = null;
                }
            }

            return null;
        }

        /// <summary>The index parameter a clip table's rows are found by: rows 0..n-1, one row each.</summary>
        private static void SetIdentity(BankDataset ds, int n)
        {
            BankParam param = ds.Params[0];
            param.W4 = (param.W4 & 0xFF000000u) | (uint)n;
            param.Min = 0;
            param.Max = n - 1;
        }

        /// <summary>
        /// Every Selection parameter record states its list: count, min, max and - where the record holds an array,
        /// or the values are not evenly spaced - the array of value - min. Records that shared one array still do.
        /// </summary>
        private string EncodeParams()
        {
            foreach (BankParam param in sel.Params)
            {
                List<int> values = paramValues[param.Id];
                int count = values.Count;
                param.Min = values[0];
                param.Max = values[count - 1];

                bool even = count == 1;
                if (!even && param.Elements == null)
                {
                    long spread = (long)param.Max - param.Min;
                    long step = spread / (count - 1);
                    even = step > 0 && spread % (count - 1) == 0;
                    for (int i = 0; even && i < count; i++)
                        even = values[i] == param.Min + step * i;
                }

                uint top = param.W4 & 0xF0000000u;
                if (param.Elements == null && even)
                {
                    param.W4 = (param.W4 & 0xFF000000u) | (uint)count;
                    continue;
                }

                uint[] elements = values.Select(v => unchecked((uint)(v - param.Min))).ToArray();
                uint largest = elements[count - 1];
                int width = param.Elements != null ? param.Width : BankBytes.Narrowest(largest);
                if (!BankBytes.Fits(largest, width))
                    width = BankBytes.Narrowest(largest);

                param.Elements = elements;
                param.W4 = top | ((uint)width << 24) | (uint)count;
            }

            // A record that shared another's array keeps sharing it only while the two state the same array.
            foreach (BankParam param in sel.Params)
            {
                if (param.SharesArrayOf < 0)
                    continue;
                BankParam owner = sel.Params[param.SharesArrayOf];
                if (owner.Elements == null || param.Elements == null || owner.Width != param.Width
                    || !owner.Elements.SequenceEqual(param.Elements))
                    param.SharesArrayOf = -1;
            }

            return null;
        }

        /// <summary>
        /// Every Selection group's start table, from the rows' keys: entry k is how many rows sort before index k.
        /// A group of uniform blocks stays one only while every block is still the same length; otherwise it gets a
        /// table of its own (its record's +0x18 width becomes the table's element width).
        /// </summary>
        private string EncodeGroups(int n)
        {
            foreach (BankGroup group in sel.Groups)
            {
                long combos = 1;
                foreach (BankParam param in sel.ParamsOf(group))
                    combos *= paramValues[param.Id].Count;
                if (combos > 0x7FFFFF)
                    return "Selection group " + group.Index + " would have " + combos + " value combinations";

                uint[] starts = new uint[combos + 1];
                long previous = -1;
                int[] perCombo = new int[combos];
                for (int r = 0; r < n; r++)
                {
                    long combo = Combo(group, rowKey[r]);
                    if (combo < 0 || combo < previous)
                        return "Selection row " + r + " is out of order for group " + group.Index;
                    previous = combo;
                    perCombo[combo]++;
                }

                uint running = 0;
                for (long k = 0; k < combos; k++)
                {
                    starts[k] = running;
                    running += (uint)perCombo[k];
                }

                starts[combos] = running;

                if (group.Starts == null)
                {
                    int block = group.TableWidth;
                    bool uniform = block > 0;
                    for (long k = 0; uniform && k <= combos; k++)
                        uniform = starts[k] == (uint)(block * k);
                    if (uniform)
                        continue;

                    int width = BankBytes.Narrowest((uint)n);
                    group.W18 = (group.W18 & 0xFF000000u) | (uint)width;
                    group.Starts = starts;
                    report.Add("Selection group " + group.Index + " no longer has blocks of one length, so it now states a start table");
                    continue;
                }

                int tableWidth = group.TableWidth;
                if (!BankBytes.Fits((uint)n, tableWidth))
                    tableWidth = BankBytes.Narrowest((uint)n);
                group.W18 = (group.W18 & 0xFF000000u) | (uint)tableWidth;
                group.Starts = starts;
            }

            return null;
        }

        // ─── reading the result back ─────────────────────────────────────────────────────────────────────────

        private int checks;

        private void Expect(List<string> failures, bool ok, string what)
        {
            checks++;
            if (!ok)
                failures.Add(what);
        }

        /// <summary>
        /// The written bank, read the way the game reads it: the chain walks, every Selection row's planned key finds
        /// a block holding that row in every group, every column decodes to the planned values, and the clips tile
        /// the chunk with every surviving clip's bytes unchanged.
        /// </summary>
        private List<string> ReadBack(byte[] written)
        {
            List<string> failures = new List<string>();
            string error;
            BankImage back = BankImage.Read(written, out error);
            if (back == null)
            {
                failures.Add("it does not parse: " + error);
                return failures;
            }

            Expect(failures, back.ChainProblem == null, "the relocation chain: " + back.ChainProblem);
            Expect(failures, back.CheckChainIsKnown() == null, "the relocation chain does not carry every pointer");

            BankDataset s = back.Dataset(BankNames.Selection), v = back.Dataset(BankNames.Variations),
                g = back.Dataset(BankNames.Segments), c = back.Dataset(BankNames.Chunks);
            int n = rowOrdinal.Count;
            Expect(failures, s != null && v != null && g != null && c != null, "a row table is missing");
            if (failures.Count > 0)
                return failures;

            Expect(failures, s.Rows == n && v.Rows == n && g.Rows == n, "the row counts are not " + n);
            Expect(failures, Same(s.Field(BankNames.VariationIndex).Decode(n), rowOrdinal.Select(x => (uint)x)), "Selection.VariationIndex");
            Expect(failures, Same(s.Field(BankNames.VariationId).Decode(n), rowVariationId), "Selection.VariationId");
            if (rowSampleId != null)
                Expect(failures, Same(s.Field(BankNames.SampleId).Decode(n), rowSampleId), "Selection.SampleId");
            Expect(failures, Same(v.Field(BankNames.VariationId).Decode(n), clipVariationId), "Variations.VariationId");
            Expect(failures, Same(g.Field(BankNames.Duration).Decode(n), clipDuration), "Segments.Duration");
            Expect(failures, Same(g.Field(BankNames.SamplesOffset).Decode(n), clipOffset.Select(o => (uint)o | offsetFlags)), "Segments.SamplesOffset");
            Expect(failures, c.Field(BankNames.ChunkSize).Value == chunkLength, "Chunks.ChunkSize is not the chunk's length");

            // Each clip table finds row i by index i.
            foreach (BankDataset ds in new[] { v, g })
            {
                BankParam p = ds.Params[0];
                for (int i = 0; i < n; i += Math.Max(1, n / 64))
                    Expect(failures, p.IndexOf(i) == i, ds.Name + " does not find row " + i + " by its index");
            }

            // Every row's key, looked up the way FUN_147986980/FUN_147986690/FUN_147986f40 look it up.
            for (int r = 0; r < n; r++)
            {
                foreach (BankGroup grp in s.Groups)
                {
                    List<BankParam> ps = s.ParamsOf(grp);
                    long index = 0;
                    bool found = true;
                    foreach (BankParam p in ps)
                    {
                        int i = p.IndexOf(rowKey[r][p.Id]);
                        if (i < 0) { found = false; break; }
                        index = index * p.Count + i;
                    }

                    long start, count;
                    if (!found)
                    {
                        start = 0; count = 0;
                    }
                    else if (grp.Starts == null)
                    {
                        count = grp.TableWidth;
                        start = count * index;
                    }
                    else
                    {
                        start = grp.Starts[index];
                        count = (long)grp.Starts[index + 1] - start;
                    }

                    Expect(failures, found && r >= start && r < start + count,
                        "row " + r + " (" + Describe(rowKey[r]) + ") is not in the rows group " + grp.Index + " gives its key");
                }
            }

            // The clips tile the chunk, every one starts a clip header, and the ones that survived are unchanged.
            List<long> offsets = g.Field(BankNames.SamplesOffset).Decode(n).Select(x => (long)(x & ~3u)).ToList();
            for (int i = 0; i < n; i++)
            {
                long start = offsets[i];
                long end = i + 1 < n ? offsets[i + 1] : chunkLength;
                Expect(failures, (i > 0 || start == 0) && end > start && end <= chunkLength, "clip " + i + " does not tile the chunk");
                if (askOnly)
                    continue;
                Expect(failures, SoundDurationScale.IsClipHeader(chunk, start), "clip " + i + " does not start a clip header");
                int from = survivingFrom[i];
                if (from >= 0)
                {
                    long length = originalLengths[from];
                    bool same = end - start >= length;
                    for (long b = 0; same && b < length; b++)
                        same = chunk[start + b] == originalChunk[originalOffsets[from] + b];
                    Expect(failures, same, "clip " + i + " (clip " + from + " before the edit) is not byte for byte the audio it was");
                }
            }

            return failures;
        }

        private static bool Same(uint[] decoded, IEnumerable<uint> planned)
        {
            return decoded != null && decoded.SequenceEqual(planned);
        }
    }
}
