using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Frosty.Core;
using Frosty.Core.Controls;
using Frosty.Core.Windows;
using FrostySdk;
using FrostySdk.Interfaces;
using FrostySdk.IO;
using FrostySdk.Managers;
using NAudio.Wave;
using WaveFormRendererLib;

namespace SoundEditorPlugin
{
    // ═══════════════════════════════════════════════════════════════
    //  FrostyNewWaveEditor — handles NewWaveAsset + LocalizedWaveAsset
    //  LocalizedWaveAsset is auto-detected (has Chunks but no RuntimeVariations)
    // ═══════════════════════════════════════════════════════════════

    public class FrostyNewWaveEditor : FrostySoundDataEditor
    {
        /// <summary>
        /// True once the bank's own clip index was read, so the bank paths below are in play. An asset with chunks
        /// and no runtime variations over them is a bank; when its index cannot be read the chunks are listed as
        /// they are and this stays false.
        /// </summary>
        private bool _isLocalizedWave;

        /// <summary>
        /// True for an asset shaped like a bank (chunks, no runtime variations over them), whether or not its index
        /// could be read; <see cref="_noIndexReason"/> says why not when it could not.
        /// </summary>
        private bool _bankShaped;
        private string _noIndexReason;

        private SoundBankFile _bank;
        private string _bankPath;

        /// <summary>Every Chunks row of the asset, in order, and the entries they came from.</summary>
        private readonly List<byte[]> _bankChunkDatas = new List<byte[]>();
        private readonly List<ChunkAssetEntry> _bankChunkEntries = new List<ChunkAssetEntry>();

        /// <summary>Every Chunks row's GUID as the asset states it, parallel to <see cref="_bankChunkDatas"/>.</summary>
        private readonly List<Guid> _bankChunkGuids = new List<Guid>();

        /// <summary>
        /// For a bank that keeps each clip in its own chunk (more than one Chunks row in its .res): the asset's chunk row
        /// (an index into <see cref="_bankChunkDatas"/>) of each of the bank's Chunks rows, matched by GUID. Null for a
        /// bank with one chunk.
        /// </summary>
        private int[] _bankResRowToAsset;

        /// <summary>Those chunks end to end, which is the buffer the clips' offsets address.</summary>
        private byte[] _bankChunk;

        /// <summary>The bank's own index as read, and the sentences an add and a delete would be refused with.</summary>
        private byte[] _bankResData;
        private string _bankAddRefusal;
        private string _bankRemoveRefusal;

        /// <summary>
        /// True while the writer is being asked, in the background, whether this bank can take an add and a remove.
        /// Asking runs the whole edit in memory (a few hundred milliseconds on a bank of 7,000 clips), so it is not
        /// part of opening the tab; the two buttons say "checking" until it answers. <see cref="_loadGeneration"/>
        /// keeps an answer about a bank that has since been reloaded from being applied.
        /// </summary>
        private volatile bool _bankProbing;
        private int _loadGeneration;

        /// <summary>
        /// What `Add Clip` was answered with on the UI thread: which clip the key was suggested from, the name the
        /// new clip's VariationId is the hash of, and the key. `AddTrack` runs on the task window's thread, where the
        /// question cannot be asked.
        /// </summary>
        private int _pendingClipSource = -1;
        private string _pendingClipName;
        private SoundBankClipKey _pendingClipKey;

        /// <summary>Where each track's bytes are, parallel to the track list.</summary>
        private List<BankClipSource> _bankClipSources;

        /// <summary>One clip's place: which Chunks row holds it, and where inside that row.</summary>
        private sealed class BankClipSource
        {
            public int ChunkIndex;
            public long ChunkOffset;
            public long Offset;
            public long Length;
        }

        public FrostyNewWaveEditor() : base(null) { }
        public FrostyNewWaveEditor(ILogger inLogger) : base(inLogger) { }

        /// <summary>True when this bank can take a new clip right now: the writer's own checks pass on the file.</summary>
        protected override bool CanAddClip
        {
            get
            {
                if (!_isLocalizedWave || _bank == null || _bankResData == null || _bankChunk == null || _bankProbing)
                    return false;
                return _bankAddRefusal == null && _bankClipSources != null;
            }
        }

        /// <summary>A bank's new clip comes from an audio file, like every other add in this editor.</summary>
        protected override bool AddClipNeedsAudioFile
        {
            get { return true; }
        }

        protected override string AddClipRefusal
        {
            get
            {
                if (!_isLocalizedWave)
                    return base.AddClipRefusal;
                if (_bankResData == null || _bankChunk == null)
                    return "This bank's own clip index or its audio is not loaded, so a new clip has nothing to be written into.";
                if (_bankProbing)
                    return "Checking whether this bank can take a new clip\u2026";
                if (_bankAddRefusal != null)
                    return _bankAddRefusal;
                return "Add a clip from an audio file.";
            }
        }

        protected override string RemoveClipRefusal
        {
            get
            {
                if (!_isLocalizedWave && _bankShaped)
                    return "This asset's clip index could not be read (" + (_noIndexReason ?? "see the log")
                        + "), so a clip cannot be removed from it.";
                if (!_isLocalizedWave || _bank == null || _bankResData == null || _bankChunk == null)
                    return base.RemoveClipRefusal;
                if (_bankProbing)
                    return "Checking whether a clip can be removed from this bank\u2026";
                if (_bankRemoveRefusal != null)
                    return _bankRemoveRefusal;
                if (SelectedTrackIndex < 0)
                    return "Select a clip first: a delete removes the selected clip, its Selection row and its audio.";
                return null;
            }
        }

        /// <summary>
        /// The line above the list: how many clips, what they are encoded in, and what the bank's clips are looked up
        /// by - and, first, a bank the game would not load.
        /// </summary>
        protected override string BankSummary
        {
            get
            {
                if (!_isLocalizedWave || _bank == null)
                    return null;

                StringBuilder text = new StringBuilder();
                if (_bank.ChainProblem != null)
                    text.Append("NOT LOADABLE BY THE GAME: ").Append(_bank.ChainProblem).Append(" \u00b7 ");

                int clipCount = _bank.Clips == null ? 0 : _bank.Clips.Count;
                text.Append(clipCount.ToString("N0", CultureInfo.InvariantCulture)).Append(clipCount == 1 ? " clip" : " clips");

                if (clipCount > 0)
                {
                    SoundBankClip first = _bank.Clips[0];
                    if (!string.IsNullOrEmpty(first.CodecName))
                        text.Append(" \u00b7 ").Append(first.CodecName);
                    if (first.SampleRate > 0)
                        text.Append(" ").Append(first.SampleRate.ToString("N0", CultureInfo.InvariantCulture)).Append(" Hz");
                }

                List<string> keys = _bank.KeyParameters.Where(id => id != BankNames.Language).Select(SoundBankParameterTable.NameOf).ToList();
                if (keys.Count > 0)
                    text.Append(" \u00b7 keyed by ").Append(string.Join(" + ", keys.ToArray()));

                return text.ToString();
            }
        }

        /// <summary>
        /// The summary's tooltip: the summary, then what each of the bank's key parameters is - the DB column its values
        /// are, what FIFA17.exe sets it to and the events whose code sets it (<see cref="SoundParameterFacts"/>).
        /// </summary>
        protected override string BankSummaryToolTip
        {
            get
            {
                string summary = BankSummary;
                if (summary == null || _bank == null || _bank.KeyParameters == null || _bank.KeyParameters.Count == 0)
                    return summary;

                StringBuilder text = new StringBuilder(summary);
                foreach (uint id in _bank.KeyParameters)
                {
                    string description = SoundParameterFacts.Describe(SoundBankParameterTable.NameOf(id));
                    if (!string.IsNullOrEmpty(description))
                        text.AppendLine().AppendLine().Append(description);
                }

                return text.ToString();
            }
        }

        protected override List<SoundDataTrack> InitialLoad(FrostyTaskWindow task)
        {
            dynamic rootObj = RootObject;
            bool hasRuntimeVariations = false;
            try { hasRuntimeVariations = rootObj.RuntimeVariations != null && rootObj.RuntimeVariations.Count > 0; } catch { }
            bool hasChunks = false;
            try { hasChunks = rootObj.Chunks != null && rootObj.Chunks.Count > 0; } catch { }

            // A bank is the shape that carries its own clip index: chunks, and no runtime variations over them.
            _bankShaped = hasChunks && !hasRuntimeVariations;
            if (_bankShaped)
                return InitialLoadBank(task);

            _isLocalizedWave = false;
            _bank = null;
            _bankClipSources = null;
            _bankResRowToAsset = null;
            return InitialLoadNewWave(task);
        }

        /// <summary>
        /// Replaces clip <paramref name="index"/>'s audio with an audio file: for a bank, the writer's replace (the clip's
        /// bytes leave its chunk, the new ones take their place, and the .res states the clip's new length); for a
        /// NewWaveAsset that is not a bank, the chunk itself.
        /// </summary>
        protected override bool ImportFileInto(int index, string fileName, FrostyTaskWindow task)
        {
            if (_isLocalizedWave)
            {
                if (index < 0 || _bankClipSources == null || index >= _bankClipSources.Count)
                {
                    App.Logger.LogWarning("There is no clip #{0} in this bank to import into.", index + 1);
                    return false;
                }

                task.Update(status: "Encoding the audio file...");
                byte[] clip = ConvertAudioFileToPcm16Big(fileName);
                if (clip == null || clip.Length == 0)
                {
                    App.Logger.LogWarning("The file chosen decodes to no audio, so nothing was written.");
                    return false;
                }

                if (!ReplaceClip(index, _bankClipSources[index], clip, task, true))
                    return false;

                App.Logger.Log("Imported {0} into clip #{1}: {2:N0} bytes of audio.", Path.GetFileName(fileName), index + 1, clip.Length);
                return true;
            }

            // A bank whose index could not be read still has one: writing a chunk under it would leave the .res
            // stating offsets and sizes for audio that is no longer there.
            if (_bankShaped && _bankPath != null && App.AssetManager.GetResEntry(_bankPath) != null)
            {
                App.Logger.LogWarning("This asset's clip index ({0}) could not be read ({1}), so importing over its chunk would leave "
                    + "the index describing audio that is no longer there; nothing was written.", _bankPath, _noIndexReason ?? "see above");
                return false;
            }

            return ImportSoundNewWave(index, fileName, task);
        }

        /// <summary>
        /// Many imports into a bank in one edit: every replace is made in memory, one after the other, and the bank is
        /// written and read back once. Any refusal stops the whole import before anything is written. A bank whose
        /// asset splits one chunk over several rows takes them one at a time instead.
        /// </summary>
        protected override void ImportFilesInto(IList<KeyValuePair<int, string>> plan, FrostyTaskWindow task)
        {
            bool oneRow = _isLocalizedWave && _bankResRowToAsset == null && _bankChunkDatas.Count == 1;
            bool rows = _isLocalizedWave && _bankResRowToAsset != null;
            if (!oneRow && !rows)
            {
                base.ImportFilesInto(plan, task);
                return;
            }

            ResAssetEntry resEntry = App.AssetManager.GetResEntry(_bankPath);
            if (resEntry == null)
            {
                App.Logger.LogWarning("This asset's .res clip index is not in the loaded game data, so nothing was imported.");
                return;
            }

            Dictionary<string, byte[]> encoded = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
            byte[] res = _bankResData;
            byte[] chunk = _bankChunk;
            List<byte[]> resRows = rows ? BankRowsInResOrder() : null;
            HashSet<int> touchedResRows = new HashSet<int>();
            StringBuilder report = new StringBuilder();

            for (int n = 0; n < plan.Count; n++)
            {
                int ordinal = plan[n].Key;
                string file = plan[n].Value;
                task.Update(status: "Importing " + (n + 1) + " / " + plan.Count, progress: ((n + 1) / (double)plan.Count) * 90.0d);

                byte[] clip;
                if (!encoded.TryGetValue(file, out clip))
                {
                    clip = ConvertAudioFileToPcm16Big(file);
                    encoded[file] = clip;
                }

                if (clip == null || clip.Length == 0)
                {
                    App.Logger.LogWarning("{0} decodes to no audio, so nothing was imported.", Path.GetFileName(file));
                    return;
                }

                SoundBankAddResult replaced = rows
                    ? SoundBankWriter.ReplaceClipAudioInRows(res, resRows, ordinal, clip)
                    : SoundBankWriter.ReplaceClipAudio(res, chunk, ordinal, clip);
                if (!replaced.Ok)
                {
                    App.Logger.LogWarning("Clip #{0}: {1} Nothing was imported.", ordinal + 1, replaced.Refusal);
                    return;
                }

                res = replaced.Res;
                if (rows)
                {
                    resRows[replaced.ChunkRow] = replaced.RowBytes;
                    touchedResRows.Add(replaced.ChunkRow);
                }
                else
                {
                    chunk = replaced.Chunk;
                }

                if (!string.IsNullOrEmpty(replaced.Report))
                    report.AppendLine(replaced.Report);
            }

            Dictionary<int, byte[]> changed = new Dictionary<int, byte[]>();
            if (rows)
            {
                foreach (int r in touchedResRows)
                    changed[_bankResRowToAsset[r]] = resRows[r];
            }
            else
            {
                changed[0] = chunk;
            }

            SoundBankAddResult edit = new SoundBankAddResult { Ok = true, Res = res, Chunk = rows ? null : chunk };
            int select = plan.Count > 0 ? plan[0].Key : 0;
            if (!WriteBankEdit(resEntry, edit, changed, task, select))
                return;

            if (rows && _bankChunk == null)
                RebuildBankChunk();
            LogReport(report.ToString());
            App.Logger.Log("Imported audio into {0} clip(s) of {1} in one edit.", plan.Count, _bankPath);
        }

        /// <summary>
        /// What a bulk import reads a mapping against: for a bank, its key parameters and every clip's key, and whether the
        /// writer can add to it and replace in it (asked of the writer, on the file as it is).
        /// </summary>
        protected override BulkTarget DescribeBulkTarget(out string refusal)
        {
            BulkTarget target = base.DescribeBulkTarget(out refusal);
            if (target == null)
                return null;

            if (!_isLocalizedWave || _bank == null || _bank.Clips == null)
            {
                if (_bankShaped && !_isLocalizedWave)
                {
                    target.CanReplace = false;
                    target.ReplaceRefusal = "this asset's clip index could not be read (" + (_noIndexReason ?? "see the log")
                        + "), so importing over its chunks would leave the index describing audio that is no longer there";
                }

                return target;
            }

            target.Kind = BulkKind.Bank;
            target.KeyParameters = _bank.KeyParameters.ToList();
            target.ClipKeys = new List<SoundBankClipKey>();
            for (int i = 0; i < _bank.Clips.Count; i++)
            {
                IList<SoundBankKeyValue> key = _bank.KeyOfClip(i);
                SoundBankClipKey clipKey = null;
                if (key != null)
                {
                    clipKey = new SoundBankClipKey();
                    foreach (SoundBankKeyValue value in key)
                        clipKey.Set(value.ParameterId, value.Value);
                }

                target.ClipKeys.Add(clipKey);
            }

            // A bulk add is one writer batch over the bank's one chunk (RunBulkImport); a bank whose audio is in several
            // chunks takes new clips one at a time, through Add Clip, which asks for each one's key.
            bool oneChunk = _bankResRowToAsset == null && _bankChunkDatas.Count == 1;
            target.CanAdd = CanAddClip && oneChunk;
            target.AddRefusal = !CanAddClip ? AddClipRefusal
                : (oneChunk ? null : (_bankResRowToAsset != null ? "this bank keeps a chunk per clip" : "this bank's audio is in " + _bankChunkDatas.Count + " chunks")
                    + ", and a bulk import adds clips only to a bank whose clips are in one chunk (Add Clip adds one at a time)");
            string replace = _bankResRowToAsset != null
                ? SoundBankWriter.RefusalForReplaceInRows(_bankResData, BankRowsInResOrder(), 0)
                : SoundBankWriter.RefusalForReplace(_bankResData, _bankChunk, 0);
            target.CanReplace = replace == null;
            target.ReplaceRefusal = replace == null ? null : "a clip's audio cannot be replaced in this bank: " + replace;
            return target;
        }

        /// <summary>
        /// A bulk import into a bank with one chunk is one edit: every file encoded, the writer's batch (every replace, then
        /// every add, <see cref="SoundBankWriter.ApplyBatch"/>), one write of the chunk and the .res, one read-back. A bank
        /// with a chunk per clip takes its replaces in one edit too (<see cref="ImportFilesInto"/>); anything else goes
        /// file by file.
        /// </summary>
        protected override void RunBulkImport(BulkPlan plan, FrostyTaskWindow task)
        {
            bool oneRow = _isLocalizedWave && _bankResRowToAsset == null && _bankChunkDatas.Count == 1;
            bool rows = _isLocalizedWave && _bankResRowToAsset != null;
            if (rows && plan.Steps.All(s => !s.Add))
            {
                ImportFilesInto(plan.Steps.Select(s => new KeyValuePair<int, string>(s.Clip, s.Path)).ToList(), task);
                return;
            }

            if (!oneRow)
            {
                base.RunBulkImport(plan, task);
                return;
            }

            ResAssetEntry resEntry = App.AssetManager.GetResEntry(_bankPath);
            if (resEntry == null)
            {
                App.Logger.LogWarning("This asset's .res clip index is not in the loaded game data, so nothing was imported.");
                return;
            }

            Dictionary<string, byte[]> encoded = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
            List<SoundBankBatchStep> steps = new List<SoundBankBatchStep>();
            for (int n = 0; n < plan.Steps.Count; n++)
            {
                BulkStep step = plan.Steps[n];
                task.Update(status: "Encoding " + (n + 1) + " / " + plan.Steps.Count + ": " + step.File, progress: (n + 1) * 80.0 / plan.Steps.Count);
                byte[] clip;
                if (!encoded.TryGetValue(step.Path, out clip))
                {
                    clip = ConvertAudioFileToPcm16Big(step.Path);
                    encoded[step.Path] = clip;
                }

                if (clip == null || clip.Length == 0)
                {
                    App.Logger.LogWarning("{0} decodes to no audio, so nothing was imported.", step.File);
                    return;
                }

                steps.Add(step.Add
                    ? new SoundBankBatchStep { Clip = clip, Key = step.Key, Name = NewClipName(step.Key) }
                    : new SoundBankBatchStep { Ordinal = step.Clip, Clip = clip });
            }

            task.Update(status: "Writing " + plan.Steps.Count + " clip(s) into the bank...", progress: 85);
            SoundBankAddResult result = SoundBankWriter.ApplyBatch(_bankResData, _bankChunk, steps);
            if (!result.Ok)
            {
                App.Logger.LogWarning("Bulk import: nothing was written - {0}", result.Refusal);
                return;
            }

            int clipsBefore = _bank.Clips == null ? 0 : _bank.Clips.Count;
            BulkStep first = plan.Steps.FirstOrDefault(s => !s.Add);
            int select = first != null ? first.Clip : clipsBefore;
            if (!WriteBankEdit(resEntry, result, new Dictionary<int, byte[]> { { 0, result.Chunk } }, task, select))
                return;

            LogReport(result.Report);
            App.Logger.Log("Bulk import into {0}: {1} clip(s) replaced and {2} added in one edit; the bank now has {3} clips.",
                _bankPath, plan.Replaces, plan.Adds, clipsBefore + plan.Adds);
        }

        /// <summary>Many deletes from a bank in one edit (the writer's RemoveClips), written and read back once.</summary>
        protected override void DeleteTracks(IList<int> indices, FrostyTaskWindow task)
        {
            if (_bank == null || _bankResData == null || _bankChunk == null)
            {
                base.DeleteTracks(indices, task);
                return;
            }

            ResAssetEntry resEntry = App.AssetManager.GetResEntry(_bankPath);
            if (resEntry == null)
            {
                App.Logger.LogWarning("This asset's .res clip index is not in the loaded game data, so no clip was removed.");
                return;
            }

            List<int> gone = indices.Distinct().OrderBy(i => i).ToList();
            task.Update(status: "Removing " + gone.Count + " clips...");
            SoundBankAddResult removed = SoundBankWriter.RemoveClips(_bankResData, _bankChunk, gone);
            if (!removed.Ok)
            {
                App.Logger.LogWarning(removed.Refusal);
                return;
            }

            int clipsAfter = (_bank.Clips == null ? gone.Count : _bank.Clips.Count) - gone.Count;
            if (!WriteBankEdit(resEntry, removed, CutChunkRows(removed.CutSpans), task, Math.Max(0, Math.Min(gone[0], clipsAfter - 1))))
                return;

            LogReport(removed.Report);
            App.Logger.Log("Removed {0} clip(s) from {1}: {2:N0} bytes left the chunk, and the bank now has {3} clips.",
                gone.Count, _bankPath, removed.ClipBytes, clipsAfter);
        }

        private bool ImportSoundNewWave(int index, string fileName, FrostyTaskWindow task)
        {
            byte[] resultBuf = ConvertAudioFileToPcm16Big(fileName);

            dynamic newWave = RootObject;
            if (index < 0 || index >= (int)newWave.Chunks.Count)
            {
                App.Logger.LogWarning("There is no clip #{0} in this asset to import into.", index + 1);
                return false;
            }

            dynamic soundDataChunk = newWave.Chunks[index];
            ChunkAssetEntry chunkEntry = App.AssetManager.GetChunkEntry(soundDataChunk.ChunkId);

            if (!WriteChunk(chunkEntry, resultBuf, "clip's chunk"))
                return false;

            soundDataChunk.ChunkSize = (uint)resultBuf.Length;

            audioPlayer.Dispose();
            audioPlayer = new AudioPlayer();

            List<SoundDataTrack> tracks = InitialLoad(task);

            Dispatcher?.Invoke(() =>
            {
                AssetModified = true;
                InvokeOnAssetModified();
                EbxAssetEntry assetEntry = AssetEntry as EbxAssetEntry;
                assetEntry.LinkAsset(chunkEntry);
                SetTrackList(tracks);
                RefreshTrackListChrome();
            });
            return true;
        }

        /// <summary>
        /// One track per chunk of a NewWaveAsset that is not a bank, listed from each chunk's clip header: codec, rate,
        /// channels and length are read there, and the audio is decoded when the clip is selected, played or exported
        /// (<see cref="SoundDataTrack.Decoder"/>), not all of it to open the tab.
        /// </summary>
        private List<SoundDataTrack> InitialLoadNewWave(FrostyTaskWindow task)
        {
            List<SoundDataTrack> retVal = new List<SoundDataTrack>();
            dynamic newWave = RootObject;

            int index = 0;
            foreach (dynamic soundDataChunk in newWave.Chunks)
            {
                index++;
                SoundDataTrack track = new SoundDataTrack { Index = index, Name = "Track #" + index, SegmentCount = 1 };
                ChunkAssetEntry chunkEntry = App.AssetManager.GetChunkEntry(soundDataChunk.ChunkId);
                if (chunkEntry == null)
                {
                    App.Logger.LogWarning($"SoundChunk {soundDataChunk.ChunkId} doesn't exist. This could be because its a LocalizedChunk that is not loaded by your game.");
                    track.DecodeFailure = "its chunk is not in the loaded game data";
                    retVal.Add(track);
                    continue;
                }

                byte[] header = new byte[20];
                using (NativeReader reader = new NativeReader(App.AssetManager.GetChunk(chunkEntry)))
                {
                    int got = (int)Math.Min(header.Length, reader.Length);
                    byte[] head = reader.ReadBytes(got);
                    Buffer.BlockCopy(head, 0, header, 0, head.Length);
                }

                long samples;
                int rate, channels;
                if (SoundDurationScale.TryHeader(header, 0, out samples, out rate, out channels))
                {
                    track.SampleRate = rate;
                    track.ChannelCount = channels;
                    track.Duration = samples / (double)rate;
                }

                track.Codec = CodecName(header[4]);
                ChunkAssetEntry entry = chunkEntry;
                track.Decoder = t =>
                {
                    byte[] soundBuf;
                    using (NativeReader reader = new NativeReader(App.AssetManager.GetChunk(entry)))
                        soundBuf = reader.ReadToEnd();

                    int decodedChannels, decodedRate;
                    string reason;
                    short[] decoded = DecodeClipAudio(soundBuf, out decodedChannels, out decodedRate, out reason);
                    if (decoded == null || decoded.Length == 0)
                        return reason ?? "no decoder produced audio";

                    t.ChannelCount = decodedChannels;
                    t.SampleRate = decodedRate;
                    t.Duration = SoundEdits.DurationSeconds(decoded, decodedChannels, decodedRate);
                    t.Samples = decoded;
                    return null;
                };

                retVal.Add(track);
            }

            return retVal;
        }

        // ═══════════════════════════════════════════════════════════
        //  The bank: its clip index, its clips, and its keys
        // ═══════════════════════════════════════════════════════════

        /// <summary>
        /// Reads a bank and lists one track per clip, named by the selection key the game looks it up by.
        ///
        /// A clip is decoded when it is asked for: a bank can hold thousands of clips behind one shared chunk, and
        /// decoding all of them to open a tab is minutes of work for audio nobody asked to hear.
        /// </summary>
        private List<SoundDataTrack> InitialLoadBank(FrostyTaskWindow task)
        {
            _isLocalizedWave = false;
            _noIndexReason = null;
            _bank = null;
            _bankPath = null;
            _bankResData = null;
            _bankAddRefusal = null;
            _bankRemoveRefusal = null;
            _bankClipSources = null;
            _bankResRowToAsset = null;

            string assetName = AssetNameOnUiThread();
            _bankPath = assetName;

            task.Update(status: "Reading the bank's chunks...");
            ReadBankChunks();

            if (_bankChunk == null)
            {
                App.Logger.LogWarning("This sound asset's audio is not loaded: its chunks are missing from the game data " +
                    "or empty, so there is nothing to list.");
                return new List<SoundDataTrack>();
            }

            // A row the game data does not hold leaves a hole: in a bank with one chunk the clips' offsets address the
            // rows laid end to end and would be read across it, and in a bank with a chunk per clip that clip has no
            // bytes. Either way the bank is not read through its index.
            int missing = _bankChunkEntries.FindIndex(e => e == null);
            if (missing >= 0)
                return ListChunksWithoutIndex(task, "chunk row " + missing + " of this asset is not loaded from the game data, so "
                    + "the clip offsets cannot be placed");

            task.Update(status: "Reading the bank's clip index...");
            string refusal;
            SoundBankFile bank = ReadBankIndex(assetName, out refusal);

            if (bank == null || !bank.IsBank)
                return ListChunksWithoutIndex(task, refusal ?? "no clip index was found for it");

            // A bank that keeps each clip in its own chunk is read the way the game reads it: per clip the chunk its
            // MemoryChunkIndex (or StreamChunkIndex) names and the clip's offset there (SoundBankFile.ResolveClipRows).
            _bankResRowToAsset = null;
            string clipRefusal;
            if (bank.ChunkRows > 1)
                clipRefusal = MapBankRows(bank) ?? bank.ResolveClipRows(BankRowsInResOrder());
            else
                clipRefusal = bank.ResolveClips(_bankChunk);
            if (clipRefusal != null || bank.Clips == null || bank.Clips.Count == 0)
                return ListChunksWithoutIndex(task, clipRefusal ?? "it states no clips");

            _bank = bank;
            _isLocalizedWave = true;

            if (bank.ChainProblem != null)
            {
                App.Logger.LogWarning("Sound bank {0}: {1}. A bank written by an earlier version of this editor can be in this "
                    + "state; revert the asset to its original .res to repair it.", assetName, bank.ChainProblem);
            }

            if (bank.KeyProblem != null)
                App.Logger.LogWarning("Sound bank {0}: its Selection groups do not state one key per row ({1}).", assetName, bank.KeyProblem);

            App.Logger.Log("Sound bank {0}: {1}", assetName, bank.Describe());

            // The names need the game's DB; it is read once per session, and reading it here says so in the task
            // window instead of stalling the first name.
            task.Update(status: "Reading the game database for names...");
            FifaNameLookup.Preload();

            List<SoundDataTrack> tracks = new List<SoundDataTrack>(bank.Clips.Count);
            _bankClipSources = new List<BankClipSource>(bank.Clips.Count);
            List<string> names = new List<string>(bank.Clips.Count);

            for (int i = 0; i < bank.Clips.Count; i++)
            {
                SoundBankClip clip = bank.Clips[i];

                if (i % 500 == 0 || i == bank.Clips.Count - 1)
                    task.Update(status: "Reading clip " + (i + 1) + " / " + bank.Clips.Count,
                        progress: ((i + 1) / (double)bank.Clips.Count) * 100.0d);

                SoundDataTrack track = new SoundDataTrack
                {
                    Index = i + 1,
                    Codec = clip.CodecName ?? "unknown",
                    ChannelCount = clip.ChannelCount,
                    SampleRate = clip.SampleRate,
                    SegmentCount = 1,
                };

                long local, offset;
                int chunkIndex;
                if (clip.ChunkRow >= 0 && _bankResRowToAsset != null)
                {
                    // The clip's offset is inside its own chunk; its place in the rows laid end to end follows from it.
                    chunkIndex = _bankResRowToAsset[clip.ChunkRow];
                    local = clip.Offset;
                    offset = RowStart(chunkIndex) + local;
                    track.ClipText = string.Format("chunk {0} · 0x{1:X} + {2:N0} B", clip.ChunkRow, clip.Offset, clip.Length);
                }
                else
                {
                    chunkIndex = ChunkOf(clip.Offset, out local);
                    offset = clip.Offset;
                    track.ClipText = string.Format("0x{0:X} + {1:N0} B", clip.Offset, clip.Length);
                }

                // Its length from its own header, so the list shows every clip's time before anything is decoded.
                track.Duration = SoundDurationScale.HeaderSeconds(_bankChunk, offset);

                string name, key, detail;
                SoundTrackNaming.Label(bank, _bankPath, i, out name, out key, out detail);
                names.Add(name);
                track.KeyText = key;
                track.Detail = detail;

                tracks.Add(track);
                _bankClipSources.Add(new BankClipSource
                {
                    ChunkIndex = chunkIndex,
                    ChunkOffset = local,
                    Offset = offset,
                    Length = clip.Length,
                });
            }

            // Several takes under one key have the same words; the second and later are numbered.
            SoundTrackNaming.NumberRepeats(names);
            for (int i = 0; i < tracks.Count; i++)
                tracks[i].Name = names[i];

            StartEditProbe();

            App.Logger.Log("Sound bank {0}: {1} clips listed, nothing decoded yet.", assetName, tracks.Count);
            return tracks;
        }

        /// <summary>
        /// Asks the writer, off the UI thread, whether this bank can take an add and a remove, and says so on the
        /// buttons when it answers. The answer is a property of the file; it is asked once per load.
        /// </summary>
        private void StartEditProbe()
        {
            int generation = System.Threading.Interlocked.Increment(ref _loadGeneration);
            byte[] res = _bankResData, chunk = _bankChunk;
            string assetName = _bankPath;
            _bankAddRefusal = null;
            _bankRemoveRefusal = null;
            _bankProbing = true;

            System.Threading.Tasks.Task.Run(() =>
            {
                string add = null, remove = null;
                try
                {
                    add = SoundBankWriter.RefusalForAudio(res, chunk, 0, "frosty-probe-" + Guid.NewGuid().ToString("N"));
                    remove = SoundBankWriter.RefusalForRemove(res, chunk, 0);
                }
                catch (Exception ex)
                {
                    add = add ?? "the writer stopped on an error (" + ex.Message + ")";
                    remove = remove ?? add;
                }

                RunOnUiThread(() =>
                {
                    if (generation != _loadGeneration)
                        return;

                    _bankAddRefusal = add;
                    _bankRemoveRefusal = remove;
                    _bankProbing = false;
                    if (add != null)
                        App.Logger.Log("Sound bank {0}: a clip cannot be added - {1}", assetName, add);
                    if (remove != null)
                        App.Logger.Log("Sound bank {0}: a clip cannot be removed - {1}", assetName, remove);
                    UpdateCommandState();
                });
            });
        }

        /// <summary>
        /// Lists one track per chunk, for an asset that is shaped like a bank but whose clip index could
        /// not be read. The chunks and their headers are real, so each is a clip; nothing says where one
        /// clip ends inside a shared chunk, and nothing names them, and both of those are said rather
        /// than papered over with a scan.
        /// </summary>
        private List<SoundDataTrack> ListChunksWithoutIndex(FrostyTaskWindow task, string reason)
        {
            List<SoundDataTrack> tracks = new List<SoundDataTrack>();
            _bankClipSources = new List<BankClipSource>();
            _bankResRowToAsset = null;
            _noIndexReason = reason;

            App.Logger.LogWarning("This sound asset's clip index could not be read ({0}), so each of its {1} chunk(s) is listed " +
                "as one clip and no clip is named.", reason, _bankChunkDatas.Count);

            long running = 0;
            int ordinal = 0;

            for (int i = 0; i < _bankChunkDatas.Count; i++)
            {
                byte[] data = _bankChunkDatas[i];
                if (data == null || data.Length < 12)
                {
                    running += data?.Length ?? 0;
                    continue;
                }

                byte codec = data[4];
                SoundDataTrack track = new SoundDataTrack
                {
                    Index = ++ordinal,
                    Name = "Chunk " + (i + 1),
                    Codec = CodecName(codec),
                    ChannelCount = (data[5] >> 2) + 1,
                    SampleRate = (data[6] << 8) | data[7],
                    SegmentCount = 1,
                    Duration = SoundDurationScale.HeaderSeconds(data, 0),
                };

                track.ClipText = string.Format("0x0 + {0:N0} B", data.Length);

                tracks.Add(track);
                _bankClipSources.Add(new BankClipSource { ChunkIndex = i, ChunkOffset = 0, Offset = running, Length = data.Length });
                running += data.Length;
            }

            return tracks;
        }

        /// <summary>Every Chunks row of the asset, in order, and all of them end to end.</summary>
        private void ReadBankChunks()
        {
            _bankChunkDatas.Clear();
            _bankChunkEntries.Clear();
            _bankChunkGuids.Clear();
            _bankChunk = null;

            try
            {
                dynamic localizedWave = RootObject;
                foreach (dynamic sdc in localizedWave.Chunks)
                {
                    Guid chunkGuid = (Guid)sdc.ChunkId;
                    _bankChunkGuids.Add(chunkGuid);
                    ChunkAssetEntry entry = App.AssetManager.GetChunkEntry(chunkGuid);
                    if (entry == null)
                    {
                        App.Logger.LogWarning("A sound chunk of this asset is not loaded from the game data, so the clips " +
                            "that live in it cannot be read or written.");
                        _bankChunkDatas.Add(null);
                        _bankChunkEntries.Add(null);
                        continue;
                    }

                    byte[] data;
                    using (NativeReader reader = new NativeReader(App.AssetManager.GetChunk(entry)))
                        data = reader.ReadToEnd();

                    _bankChunkDatas.Add(data);
                    _bankChunkEntries.Add(entry);
                }
            }
            catch (Exception ex)
            {
                App.Logger.LogError("This sound asset's chunks could not be read: " + ex.Message);
            }

            RebuildBankChunk();
        }

        /// <summary><see cref="_bankChunk"/> from <see cref="_bankChunkDatas"/>: the rows end to end, or null when they hold nothing.</summary>
        private void RebuildBankChunk()
        {
            _bankChunk = null;

            // One row is the chunk itself; copying it (44 MB for the biggest name bank) bought nothing. Every edit
            // makes new arrays, so the row and the chunk never change under each other.
            if (_bankChunkDatas.Count == 1)
            {
                _bankChunk = _bankChunkDatas[0] != null && _bankChunkDatas[0].Length > 0 ? _bankChunkDatas[0] : null;
                return;
            }

            int total = 0;
            foreach (byte[] data in _bankChunkDatas)
                total += data?.Length ?? 0;

            if (total == 0)
                return;

            byte[] all = new byte[total];
            int at = 0;
            foreach (byte[] data in _bankChunkDatas)
            {
                if (data == null)
                    continue;

                Buffer.BlockCopy(data, 0, all, at, data.Length);
                at += data.Length;
            }

            _bankChunk = all;
        }

        /// <summary>Where asset chunk row <paramref name="row"/> starts in <see cref="_bankChunk"/>.</summary>
        private long RowStart(int row)
        {
            long at = 0;
            for (int i = 0; i < row && i < _bankChunkDatas.Count; i++)
                at += _bankChunkDatas[i]?.Length ?? 0;
            return at;
        }

        /// <summary>
        /// Matches the bank's Chunks rows to the asset's chunk rows by GUID (<see cref="_bankResRowToAsset"/>); null on
        /// success, or why the clips cannot be placed.
        /// </summary>
        private string MapBankRows(SoundBankFile bank)
        {
            _bankResRowToAsset = null;
            int[] map = new int[bank.ChunkRows];
            for (int r = 0; r < bank.ChunkRows; r++)
            {
                Guid guid;
                string stated = r < bank.ChunkGuids.Count ? bank.ChunkGuids[r] : null;
                if (stated == null || !Guid.TryParse(stated, out guid))
                    return "the bank's Chunks row " + r + " names no GUID in its .res tail";
                map[r] = _bankChunkGuids.IndexOf(guid);
                if (map[r] < 0)
                    return "the bank's Chunks row " + r + " names chunk " + stated + ", which is not one of this asset's chunks";
                if (_bankChunkDatas[map[r]] == null)
                    return "chunk " + stated + " (the bank's Chunks row " + r + ") is not loaded from the game data";
            }

            _bankResRowToAsset = map;
            return null;
        }

        /// <summary>The asset's chunks in the bank's Chunks row order (<see cref="_bankResRowToAsset"/> must be set).</summary>
        private List<byte[]> BankRowsInResOrder()
        {
            return _bankResRowToAsset.Select(i => _bankChunkDatas[i]).ToList();
        }

        /// <summary>Which Chunks row a byte offset falls in, and where inside that row it is.</summary>
        private int ChunkOf(long offset, out long local)
        {
            long at = 0;

            for (int i = 0; i < _bankChunkDatas.Count; i++)
            {
                int length = _bankChunkDatas[i]?.Length ?? 0;
                if (offset < at + length)
                {
                    local = offset - at;
                    return i;
                }

                at += length;
            }

            local = offset - at;
            return _bankChunkDatas.Count - 1;
        }

        /// <summary>
        /// `Keep First N Clips` for a bank: every clip after the first <paramref name="keepCount"/> leaves in one edit
        /// (the rows that play them, their Variations and Segments rows and their bytes), written and read back once.
        /// A bank keeps at least one clip: the writer does not write a bank with no rows.
        /// </summary>
        protected override void KeepFirstNTracks(int keepCount, FrostyTaskWindow task)
        {
            if (_bank == null || _bankResData == null || _bankChunk == null)
            {
                base.KeepFirstNTracks(keepCount, task);
                return;
            }

            int clips = _bank.Clips == null ? 0 : _bank.Clips.Count;
            if (keepCount < 1 || keepCount > clips)
            {
                App.Logger.LogError("Keep First N Clips: this bank has {0} clip(s) and the count given was {1}; a bank keeps at "
                    + "least one clip, so nothing was removed.", clips, keepCount);
                return;
            }

            if (keepCount == clips)
            {
                App.Logger.Log("Keep First N Clips: {0} already has {1} clip(s), so nothing was removed.", _bankPath, clips);
                return;
            }

            ResAssetEntry resEntry = App.AssetManager.GetResEntry(_bankPath);
            if (resEntry == null)
            {
                App.Logger.LogWarning("This asset's .res clip index is not in the loaded game data, so clips cannot be removed.");
                return;
            }

            task.Update(status: "Removing clips " + (keepCount + 1) + " to " + clips + "...");
            List<int> gone = Enumerable.Range(keepCount, clips - keepCount).ToList();
            SoundBankAddResult removed = SoundBankWriter.RemoveClips(_bankResData, _bankChunk, gone);
            if (!removed.Ok)
            {
                App.Logger.LogWarning("Keep First N Clips: {0}", removed.Refusal);
                return;
            }

            if (!WriteBankEdit(resEntry, removed, CutChunkRows(removed.CutSpans), task, keepCount - 1))
                return;

            LogReport(removed.Report);
            App.Logger.Log("Kept the first {0} clip(s) of {1}: {2:N0} bytes left the chunk.", keepCount, _bankPath, removed.ClipBytes);
        }

        /// <summary>Delete Clip, which for a bank is a clip, its Selection row and its bytes.</summary>
        protected override void DeleteTrack(int trackIndex, FrostyTaskWindow task)
        {
            if (RemoveBankClip(trackIndex, task))
                return;

            base.DeleteTrack(trackIndex, task);
        }

        /// <summary>
        /// Removes a clip from a bank: its rows leave the `.res` (`ModifyRes`) and its bytes leave the Chunks row(s)
        /// that hold them (`ModifyChunk`). True when this asset is a bank and the delete was handled (or refused with a
        /// sentence), false when it is not one.
        /// </summary>
        private bool RemoveBankClip(int ordinal, FrostyTaskWindow task)
        {
            if (_bank == null || _bankResData == null || _bankChunk == null)
                return false;

            ResAssetEntry resEntry = App.AssetManager.GetResEntry(_bankPath);
            if (resEntry == null)
            {
                App.Logger.LogWarning("This asset's .res clip index is not in the loaded game data, so a clip cannot be removed.");
                return true;
            }

            task.Update(status: "Removing the clip from the bank...");
            SoundBankAddResult removed = SoundBankWriter.RemoveClip(_bankResData, _bankChunk, ordinal);
            if (!removed.Ok)
            {
                App.Logger.LogWarning(removed.Refusal);
                return true;
            }

            int clipsAfter = (_bank.Clips == null ? 1 : _bank.Clips.Count) - 1;
            if (!WriteBankEdit(resEntry, removed, CutChunkRows(removed.CutSpans), task, Math.Min(ordinal, clipsAfter - 1)))
                return true;

            LogReport(removed.Report);
            App.Logger.Log("Removed clip {0} of {1}: {2:N0} bytes left the chunk, and the bank now has {3} clips.",
                ordinal + 1, _bankPath, removed.ClipBytes, clipsAfter);
            return true;
        }

        /// <summary>
        /// The asset's chunk rows after the ranges a delete took out, cut in one pass, by row: only the rows a range
        /// touched. Null when the writer did not state the ranges, which the caller refuses on.
        /// </summary>
        private Dictionary<int, byte[]> CutChunkRows(IList<long[]> cuts)
        {
            if (cuts == null)
            {
                App.Logger.LogWarning("The writer did not state which bytes the delete took out of the chunk, so nothing was written.");
                return null;
            }

            byte[][] rows = SoundBankWriter.CutRows(_bankChunkDatas, cuts);

            Dictionary<int, byte[]> changed = new Dictionary<int, byte[]>();
            for (int i = 0; i < rows.Length; i++)
            {
                if (!ReferenceEquals(rows[i], _bankChunkDatas[i]))
                    changed[i] = rows[i];
            }

            return changed;
        }

        /// <summary>
        /// Writes one edit of the bank into the asset: the chunk rows that changed, then the `.res`. When a write is
        /// refused, the rows already written get their old bytes back, so the asset never holds a chunk and an index
        /// that disagree. Then, on the UI thread, the EBX's `Chunks[].ChunkSize` take the rows' new lengths and the
        /// asset is marked modified (which writes the EBX through `ModifyEbx`); the chunk rows and the `.res` are
        /// linked to the asset, so its Revert takes all three back together; and the list is read again from what
        /// the asset manager now holds, which is also the check that the writes landed.
        /// </summary>
        private bool WriteBankEdit(ResAssetEntry resEntry, SoundBankAddResult edit, IDictionary<int, byte[]> rows,
            FrostyTaskWindow task, int select)
        {
            if (rows == null)
                return false;

            foreach (int i in rows.Keys)
            {
                if (i < 0 || i >= _bankChunkEntries.Count || _bankChunkEntries[i] == null)
                {
                    App.Logger.LogWarning("A chunk row this edit changes is not loaded from the game data, so nothing was written.");
                    return false;
                }
            }

            task.Update(status: "Writing the bank...");
            List<int> written = new List<int>();
            foreach (int i in rows.Keys.OrderBy(k => k))
            {
                if (!WriteChunk(_bankChunkEntries[i], rows[i], "bank's chunk row"))
                {
                    PutBack(written);
                    return false;
                }

                written.Add(i);
            }

            if (!WriteRes(resEntry, edit.Res, "bank's .res clip index"))
            {
                PutBack(written);
                return false;
            }

            foreach (KeyValuePair<int, byte[]> row in rows)
                _bankChunkDatas[row.Key] = row.Value;
            _bankChunk = edit.Chunk;
            _bankResData = edit.Res;

            List<AssetEntry> touched = rows.Keys.Select(i => (AssetEntry)_bankChunkEntries[i]).ToList();
            touched.Add(resEntry);

            RunOnUiThread(() =>
            {
                dynamic chunks = ((dynamic)RootObject).Chunks;
                foreach (KeyValuePair<int, byte[]> row in rows)
                    chunks[row.Key].ChunkSize = (uint)row.Value.Length;

                AssetModified = true;
                InvokeOnAssetModified();
            });
            LinkToAssetOnUiThread(touched);

            task.Update(status: "Reading the bank back...");
            List<SoundDataTrack> tracks = InitialLoad(task);
            ShowReloadedTracks(tracks, select);
            return true;
        }

        /// <summary>The rows an edit wrote before a later write was refused, given their old bytes back.</summary>
        private void PutBack(List<int> written)
        {
            foreach (int i in written)
            {
                if (!WriteChunk(_bankChunkEntries[i], _bankChunkDatas[i], "bank's chunk row (putting its old bytes back)"))
                    App.Logger.LogError("Chunk row {0} of this bank could not be given its old bytes back; revert the asset "
                        + "before saving, because its chunk and its .res now disagree.", i);
            }
        }

        /// <summary>A writer's report in the log, shortened when an edit removed many clips.</summary>
        private static void LogReport(string report)
        {
            if (string.IsNullOrEmpty(report))
                return;

            string[] lines = report.Split(new[] { Environment.NewLine }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < lines.Length; i++)
            {
                if (lines.Length > 12 && i == 5)
                {
                    App.Logger.Log("... {0} more line(s) ...", lines.Length - 7);
                    i = lines.Length - 3;
                    continue;
                }

                App.Logger.Log("{0}", lines[i].Trim());
            }
        }

        /// <summary>
        /// Adds a clip to the bank from the user's audio file: the writer files it under the key the dialog stated,
        /// appends its bytes to the chunk (on a 4-byte boundary, as FUN_146210310 reads offsets), and the asset's last
        /// Chunks row grows by exactly what the chunk grew.
        /// </summary>
        protected override bool AddTrackFile(string fileName, FrostyTaskWindow task)
        {
            if (!_isLocalizedWave)
            {
                return base.AddTrackFile(fileName, task);
            }

            int source = _pendingClipSource;
            string name = _pendingClipName;
            SoundBankClipKey key = _pendingClipKey;

            if (string.IsNullOrEmpty(name) || _bank == null || _bankResData == null || _bankChunk == null)
            {
                App.Logger.LogWarning("Adding a clip needs a name for it and a loaded bank; one of those was missing.");
                return false;
            }

            ResAssetEntry resEntry = App.AssetManager.GetResEntry(_bankPath);
            if (resEntry == null)
            {
                App.Logger.LogWarning("This asset's .res clip index is not in the loaded game data, so a new clip cannot be written.");
                return false;
            }

            task.Update(status: "Encoding the audio file...");
            byte[] clipBytes = ConvertAudioFileToPcm16Big(fileName);
            if (clipBytes == null || clipBytes.Length == 0)
            {
                App.Logger.LogWarning("The file chosen for the new clip decoded to no audio, so nothing was written.");
                return false;
            }

            task.Update(status: "Adding the clip to the bank...");
            SoundBankAddResult added = SoundBankWriter.AddClipAudio(_bankResData, _bankChunk, clipBytes, source, name, key);
            if (!added.Ok)
            {
                App.Logger.LogWarning(added.Refusal);
                return false;
            }

            // The writer appended the clip (after zero padding to a 4-byte boundary) to the whole chunk; those bytes
            // belong to the asset's last row.
            int last = _bankChunkEntries.Count - 1;
            int growth = added.Chunk.Length - _bankChunk.Length;
            byte[] rowData = _bankChunkDatas[last];
            byte[] grownRow = new byte[rowData.Length + growth];
            Buffer.BlockCopy(rowData, 0, grownRow, 0, rowData.Length);
            Buffer.BlockCopy(added.Chunk, _bankChunk.Length, grownRow, rowData.Length, growth);

            _pendingClipSource = -1;
            _pendingClipName = null;
            _pendingClipKey = null;

            if (!WriteBankEdit(resEntry, added, new Dictionary<int, byte[]> { { last, grownRow } }, task, added.Ordinal))
                return false;

            LogReport(added.Report);
            App.Logger.Log("Added clip {0} (\"{1}\") to {2}, filed under {3}; the bank now has {4} clips.",
                added.Ordinal + 1, name, _bankPath, added.Key ?? "no key", added.Ordinal + 1);
            return true;
        }

        /// <summary>
        /// The key and name of the new clip, asked on the UI thread before the task window moves the edit onto a
        /// background thread (a WPF window cannot be built there). False abandons the command.
        /// </summary>
        protected override bool PrepareAddClip()
        {
            _pendingClipSource = -1;
            _pendingClipName = null;
            _pendingClipKey = null;

            if (!_isLocalizedWave)
                return true;

            if (_bank == null || _bankResData == null || _bankChunk == null)
            {
                App.Logger.LogWarning("Adding a clip is implemented for a bank whose own clip index was read, and this asset is not one.");
                return false;
            }

            int source = SelectedTrackIndex;
            if (source < 0)
                source = 0;

            SoundBankClipKey key;
            string name = AskForNewClip(source, out key);
            if (name == null)
                return false;

            _pendingClipSource = source;
            _pendingClipName = name;
            _pendingClipKey = key;
            return true;
        }

        /// <summary>
        /// The Add Clip window, shaped by what the bank's clips are filed under: one field per selection parameter whose
        /// value differs between the bank's clips or that is a player or team id, and nothing else. A parameter every
        /// clip shares a value of (the Language of a commentary bank) takes that value without asking, so a bank of
        /// player lines asks for the player id and that is all. A value with names (`home`, `away`) is picked from them; an id or a number is typed.
        /// The clip's VariationId is the engine hash of a name made here, unique in the bank. Null when cancelled.
        /// </summary>
        private string AskForNewClip(int source, out SoundBankClipKey key)
        {
            key = null;

            IList<uint> parameters = _bank.KeyParameters;
            IList<SoundBankKeyValue> sourceKey = _bank.KeyOfClip(source);

            // Which values each parameter takes across the bank's clips.
            Dictionary<uint, HashSet<int>> seen = parameters.ToDictionary(id => id, id => new HashSet<int>());
            for (int i = 0; _bank.Clips != null && i < _bank.Clips.Count; i++)
            {
                IList<SoundBankKeyValue> clipKey = _bank.KeyOfClip(i);
                if (clipKey == null)
                    continue;
                foreach (SoundBankKeyValue kv in clipKey)
                {
                    HashSet<int> set;
                    if (seen.TryGetValue(kv.ParameterId, out set))
                        set.Add(kv.Value);
                }
            }

            SoundBankClipKey fixedKey = new SoundBankClipKey();
            List<uint> asked = new List<uint>();
            foreach (uint id in parameters)
            {
                // An id is always asked, even when every clip of the bank has the same one: adding a clip for another
                // player or team is what a new clip in such a bank is usually for.
                string name = SoundBankParameterTable.NameOf(id) ?? "";
                bool isId = SoundTrackNaming.DatabaseSpace(name) != null
                    || name.EndsWith("_id", StringComparison.OrdinalIgnoreCase);
                if (seen[id].Count == 1 && !isId)
                    fixedKey.Set(id, seen[id].First());
                else
                    asked.Add(id);
            }

            Window window = new Window
            {
                Title = "Add Clip",
                Width = 460,
                SizeToContent = SizeToContent.Height,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                ResizeMode = ResizeMode.NoResize,
                ShowInTaskbar = false,
                Owner = Window.GetWindow(this),
            };

            Grid grid = new Grid { Margin = new Thickness(12) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            List<Func<int?>> readers = new List<Func<int?>>();
            Control firstInput = null;
            for (int i = 0; i < asked.Count; i++)
            {
                uint id = asked[i];
                string parameter = SoundBankParameterTable.NameOf(id);
                string space = SoundTrackNaming.DatabaseSpace(parameter);
                SoundBankKeyValue start = sourceKey == null ? null : sourceKey.FirstOrDefault(v => v.ParameterId == id);

                // The caption is the parameter's own name; hovering it says what FIFA17.exe sets it to.
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                TextBlock caption = new TextBlock { Text = parameter, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 6, 10, 3) };
                string about = SoundParameterFacts.Describe(parameter);
                if (!string.IsNullOrEmpty(about))
                {
                    caption.ToolTip = new TextBlock { Text = about, TextWrapping = TextWrapping.Wrap, MaxWidth = 520 };
                    ToolTipService.SetShowDuration(caption, 60000);
                }
                Grid.SetRow(caption, i);
                grid.Children.Add(caption);

                SoundBankParameterTable.Entry entry = SoundBankParameterTable.Find(id);
                Control input;
                if (entry != null && entry.Values.Length > 0)
                {
                    ComboBox combo = new ComboBox { Margin = new Thickness(0, 3, 0, 3) };
                    List<int> values = new List<int>();
                    for (int v = 0; v < entry.Values.Length; v++)
                    {
                        combo.Items.Add(entry.ValueNames[v]);
                        values.Add(entry.Values[v]);
                    }
                    // A value the bank files clips under that has no name of its own is shown as its bits' names when
                    // it is a combination of named bits (CROSSBAR_IN + POST_IN), else as its number.
                    foreach (int other in seen[id].OrderBy(v => v))
                    {
                        if (!values.Contains(other))
                        {
                            string words = SoundTrackNaming.ValueWords(new SoundBankKeyValue { ParameterId = id, Parameter = parameter, Value = other });
                            string number = other.ToString(CultureInfo.InvariantCulture);
                            combo.Items.Add(string.IsNullOrEmpty(words) || words == parameter + " " + number ? number : words + " (" + number + ")");
                            values.Add(other);
                        }
                    }

                    int startAt = start == null ? -1 : values.IndexOf(start.Value);
                    combo.SelectedIndex = startAt >= 0 ? startAt : 0;
                    readers.Add(() => combo.SelectedIndex < 0 ? (int?)null : values[combo.SelectedIndex]);
                    input = combo;
                }
                else
                {
                    // An id is typed fresh: the point of adding is usually a player or team the bank does not have yet.
                    TextBox box = new TextBox { Margin = new Thickness(0, 3, 0, 3) };
                    if (space == null && start != null)
                        box.Text = start.Value.ToString(CultureInfo.InvariantCulture);
                    readers.Add(() =>
                    {
                        int value;
                        return int.TryParse((box.Text ?? "").Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value)
                            ? value : (int?)null;
                    });
                    input = box;
                }

                Grid.SetRow(input, i);
                Grid.SetColumn(input, 1);
                grid.Children.Add(input);
                if (firstInput == null)
                    firstInput = input;

                // Under a typed field: for a DB id, what the game's DB calls the id typed, as it is typed; for any other
                // number, the values the bank already files clips under.
                TextBox idBox = input as TextBox;
                if (idBox != null)
                {
                    TextBlock said = new TextBlock
                    {
                        Margin = new Thickness(0, 0, 0, 3),
                        Foreground = Brushes.Gray,
                        TextTrimming = TextTrimming.CharacterEllipsis,
                    };
                    string spaceName = parameter;
                    if (space != null)
                    {
                        idBox.TextChanged += (sender, e) => said.Text = SoundTrackNaming.DescribeTypedId(spaceName, idBox.Text);
                        said.Text = SoundTrackNaming.DescribeTypedId(spaceName, idBox.Text);
                    }
                    else
                    {
                        said.Text = SoundTrackNaming.DescribeBankValues(seen[id]);
                        said.ToolTip = said.Text;
                    }

                    StackPanel stack = new StackPanel();
                    grid.Children.Remove(input);
                    stack.Children.Add(input);
                    stack.Children.Add(said);
                    Grid.SetRow(stack, i);
                    Grid.SetColumn(stack, 1);
                    grid.Children.Add(stack);
                }
            }

            TextBlock problem = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                Foreground = Brushes.IndianRed,
                Margin = new Thickness(0, 6, 0, 0),
                Visibility = Visibility.Collapsed,
            };
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Grid.SetRow(problem, asked.Count);
            Grid.SetColumnSpan(problem, 2);
            grid.Children.Add(problem);

            StackPanel buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
            Button ok = new Button { Content = "Add", Width = 80, Height = 24, IsDefault = true };
            Button cancel = new Button { Content = "Cancel", Width = 80, Height = 24, Margin = new Thickness(6, 0, 0, 0), IsCancel = true };
            buttons.Children.Add(ok);
            buttons.Children.Add(cancel);
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Grid.SetRow(buttons, asked.Count + 1);
            Grid.SetColumnSpan(buttons, 2);
            grid.Children.Add(buttons);

            SoundBankClipKey chosen = null;
            string chosenName = null;
            ok.Click += (sender, e) =>
            {
                SoundBankClipKey typed = new SoundBankClipKey();
                foreach (KeyValuePair<uint, int> pair in fixedKey.Values)
                    typed.Set(pair.Key, pair.Value);
                for (int i = 0; i < asked.Count; i++)
                {
                    int? value = readers[i]();
                    if (value == null)
                    {
                        problem.Text = "Enter a whole number.";
                        problem.Visibility = Visibility.Visible;
                        return;
                    }
                    typed.Set(asked[i], value.Value);
                }

                string name = NewClipName(typed);
                string refusal = SoundBankWriter.RefusalForAudio(_bankResData, _bankChunk, source, name, parameters.Count == 0 ? null : typed);
                if (refusal != null)
                {
                    problem.Text = refusal;
                    problem.Visibility = Visibility.Visible;
                    return;
                }

                chosen = typed;
                chosenName = name;
                window.DialogResult = true;
            };

            window.Content = grid;
            window.Loaded += (sender, e) => (firstInput ?? (Control)ok).Focus();

            // A bank whose clips all share one key, or that is looked up by nothing, has nothing to ask.
            if (asked.Count == 0)
            {
                key = parameters.Count == 0 ? null : fixedKey;
                return NewClipName(fixedKey);
            }

            if (window.ShowDialog() != true || chosen == null)
                return null;

            key = parameters.Count == 0 ? null : chosen;
            return chosenName;
        }

        /// <summary>
        /// The name whose engine hash becomes the new clip's VariationId: the bank's name, the key's values and a number,
        /// the first of those no clip of the bank already carries the hash of.
        /// </summary>
        private string NewClipName(SoundBankClipKey key)
        {
            string stem = SoundTrackNaming.BankTitle(_bankPath);
            if (key != null && key.Values.Count > 0)
                stem += " " + string.Join(" ", key.Values.Where(p => p.Key != BankNames.Language)
                    .Select(p => p.Value.ToString(CultureInfo.InvariantCulture)).ToArray());

            for (int n = 1; n < 100000; n++)
            {
                string name = stem.Trim() + " added " + n.ToString(CultureInfo.InvariantCulture);
                if (!ClipNameTaken(name))
                    return name;
            }

            return stem + " " + Guid.NewGuid().ToString("N");
        }

        /// <summary>How many of the bank's clips are filed under exactly this key.</summary>
        private int CountClipsWithKey(SoundBankClipKey key)
        {
            if (_bank == null || _bank.Clips == null || key == null)
                return 0;

            int count = 0;
            for (int i = 0; i < _bank.Clips.Count; i++)
            {
                IList<SoundBankKeyValue> clipKey = _bank.KeyOfClip(i);
                if (clipKey == null)
                    continue;
                bool same = clipKey.Count == key.Values.Count;
                foreach (SoundBankKeyValue kv in clipKey)
                {
                    int value;
                    if (!key.Values.TryGetValue(kv.ParameterId, out value) || value != kv.Value)
                        same = false;
                }

                if (same)
                    count++;
            }

            return count;
        }

        /// <summary>True when a clip of this bank already carries the engine hash of this name as its VariationId.</summary>
        private bool ClipNameTaken(string name)
        {
            return _bank != null && !string.IsNullOrEmpty(name) && _bank.HasVariationId(SoundBankFile.EngineHash(name));
        }

        /// <summary>
        /// The bank's clip index: the .res resource named after the asset. Returns null and sets
        /// <paramref name="refusal"/> when there is no index, which is a state the caller reports rather
        /// than one it guesses around.
        /// </summary>
        private SoundBankFile ReadBankIndex(string assetName, out string refusal)
        {
            refusal = null;

            if (string.IsNullOrEmpty(assetName))
            {
                refusal = "the asset has no name to look its clip index up by";
                return null;
            }

            try
            {
                ResAssetEntry resEntry = App.AssetManager.GetResEntry(assetName);
                if (resEntry == null)
                {
                    refusal = "no .res clip index is named after this asset (" + assetName + ")";
                    return null;
                }

                byte[] resData;
                using (NativeReader reader = new NativeReader(App.AssetManager.GetRes(resEntry)))
                    resData = reader.ReadToEnd();

                if (resData == null || resData.Length == 0)
                {
                    refusal = "its .res clip index is empty";
                    return null;
                }

                App.Logger.Log("Sound bank index: {0} ({1:N0} bytes)", resEntry.Name, resData.Length);
                _bankResData = resData;

                string error;
                SoundBankFile bank = SoundBankFile.Parse(resData, out error);

                if (error != null)
                    refusal = error;

                return bank;
            }
            catch (Exception ex)
            {
                refusal = "its .res clip index could not be read: " + ex.Message;
                return null;
            }
        }

        /// <summary>
        /// Decodes one clip out of the bank's shared chunk into <paramref name="track"/>, the first time it is asked
        /// for. Safe on any thread: it is handed the track rather than reading the list (a dependency property).
        /// A bank whose index could not be read lists its chunk rows, and a row is decoded from its first byte.
        /// </summary>
        protected override bool EnsureSamples(SoundDataTrack track, int trackIndex)
        {
            // Read once: an edit on the task window's thread can replace these while a decode runs here.
            List<BankClipSource> sources = _bankClipSources;
            byte[] chunk = _bankChunk;
            if (sources == null || chunk == null)
                return base.EnsureSamples(track, trackIndex);

            if (track == null || trackIndex < 0 || trackIndex >= sources.Count)
                return false;

            if (track.HasSamples)
                return true;

            BankClipSource source = sources[trackIndex];
            if (source.Length <= 0 || source.Offset < 0 || source.Offset + source.Length > chunk.Length)
            {
                track.DecodeFailure = "its byte range is outside the chunk that was loaded";
                App.Logger.LogWarning("Clip #{0} states a byte range outside the chunk that was loaded, so it cannot be decoded.",
                    track.Index);
                return false;
            }

            byte[] clipBytes = new byte[source.Length];
            Buffer.BlockCopy(chunk, (int)source.Offset, clipBytes, 0, clipBytes.Length);

            int channels, sampleRate;
            string reason;
            short[] decoded = DecodeClipAudio(clipBytes, out channels, out sampleRate, out reason);

            if (decoded == null || decoded.Length == 0)
            {
                track.DecodeFailure = reason ?? "no decoder produced audio";
                App.Logger.LogWarning("Clip #{0} ({1}) could not be decoded: {2}", track.Index, track.Codec, track.DecodeFailure);
                return false;
            }

            track.ChannelCount = channels;
            track.SampleRate = sampleRate;
            track.Duration = SoundEdits.DurationSeconds(decoded, channels, sampleRate);
            track.SegmentCount = 1;
            track.DecodeFailure = null;
            track.Samples = decoded;
            return true;
        }

        /// <summary>
        /// Decodes a clip's bytes. This is the codec routing the whole plugin reads sound with: PCM and
        /// XAS in managed code, EA Layer 3 through the native decoder with vgmstream behind it, and
        /// everything else (Opus, Speex) through vgmstream.
        /// </summary>
        public static short[] DecodeClipAudio(byte[] soundBuf, out int channels, out int sampleRate, out string reason)
        {
            channels = 1;
            sampleRate = 48000;
            reason = null;

            if (soundBuf == null || soundBuf.Length < 12)
            {
                reason = "its header is too short to read";
                return null;
            }

            byte codec = soundBuf[4];
            channels = Math.Max(1, (soundBuf[5] >> 2) + 1);
            sampleRate = Math.Max(1, (soundBuf[6] << 8) | soundBuf[7]);

            try
            {
                if (codec == 0x12)
                    return Pcm16b.Decode(soundBuf);

                if (codec == 0x14)
                    return XAS.Decode(soundBuf);

                if (codec == 0x15 || codec == 0x16)
                {
                    // Through TryDecode, which loads the native decoder before the first call. Calling the
                    // DllImport directly here is what made every EA Layer 3 clip fail with "Unable to load DLL
                    // 'ealayer3.dll'" in a process that had not touched EALayer3.IsAvailable first - the editor's
                    // own decode path never did, and the census over the exported corpus found 471 banks' first
                    // clips refused for it (measured 2026-10-17, the decoder deployed beside the plugin the whole
                    // time).
                    short[] decoded = EALayer3.TryDecode(soundBuf);
                    if (decoded != null && decoded.Length > 0)
                        return decoded;

                    if (!EALayer3.IsAvailable)
                        reason = EALayer3.DescribeFailure();
                    else if (EALayer3.LastFailure != null)
                        reason = "the EA Layer 3 decoder refused it: " + EALayer3.LastFailure;
                }

                int vgmChannels, vgmRate;
                short[] viaVgmstream = DecodeWithVgmstream(soundBuf, out vgmChannels, out vgmRate);
                if (viaVgmstream != null && viaVgmstream.Length > 0)
                {
                    channels = vgmChannels > 0 ? vgmChannels : channels;
                    sampleRate = vgmRate > 0 ? vgmRate : sampleRate;
                    return viaVgmstream;
                }

                if (reason == null)
                    reason = "nothing decoded codec 0x" + codec.ToString("x2") + " (vgmstream-cli.exe produced no audio)";

                return null;
            }
            catch (Exception ex)
            {
                reason = ex.Message;
                return null;
            }
        }

        /// <summary>The codec's name, by the same numbering the rest of the plugin uses.</summary>
        internal static string CodecName(byte codec)
        {
            switch (codec)
            {
                case 0x12: return "Pcm16Big";
                case 0x14: return "Xas1";
                case 0x15: return "EaLayer31";
                case 0x16: return "EaLayer32Pcm";
                case 0x19: return "EaSpeex";
                case 0x1c: return "EaOpus";
                default: return "Unknown (" + codec.ToString("x2") + ")";
            }
        }

        /// <summary>
        /// Writes an edited clip back: a bank clip is a byte range of a shared chunk, so an edit that changes its length
        /// moves every clip after it - which is the writer's replace. The edit command rebuilds the list itself (it
        /// keeps the edited clip selected and records the undo step after the rebuild), so this asks it to.
        /// </summary>
        protected override bool CommitTrackSamples(SoundDataTrack track, int trackIndex, short[] samples, FrostyTaskWindow task)
        {
            if (!_isLocalizedWave || _bankClipSources == null || trackIndex < 0 || trackIndex >= _bankClipSources.Count)
                return base.CommitTrackSamples(track, trackIndex, samples, task);

            byte[] clip = EncodeTrack(track, samples);
            if (!ReplaceClip(trackIndex, _bankClipSources[trackIndex], clip, task, false))
                return false;

            RequireTrackReload();
            return true;
        }

        /// <summary>
        /// Writes the new clip into the bank: the clip's old bytes leave the chunk row it starts in, the new bytes
        /// (padded to a 4-byte boundary) take their place, and every clip after it moves by the difference.
        /// <paramref name="reloadList"/> false leaves rebuilding the list to the caller.
        /// </summary>
        private bool ReplaceClip(int ordinal, BankClipSource source, byte[] bytes, FrostyTaskWindow task, bool reloadList)
        {
            if (bytes == null || bytes.Length == 0)
            {
                App.Logger.LogWarning("The new clip carries no audio, so nothing was written.");
                return false;
            }

            if (_bankResRowToAsset != null)
                return ReplaceClipInOwnChunk(ordinal, bytes, task, reloadList);

            if (source.ChunkIndex < 0 || source.ChunkIndex >= _bankChunkDatas.Count || _bankChunkEntries[source.ChunkIndex] == null)
            {
                App.Logger.LogWarning("This clip's chunk is not loaded from the game data, so the edit cannot be written.");
                return false;
            }

            long rowStart = 0;
            for (int i = 0; i < source.ChunkIndex; i++)
                rowStart += _bankChunkDatas[i] == null ? 0 : _bankChunkDatas[i].Length;

            long rowLength = _bankChunkDatas[source.ChunkIndex] == null ? 0 : _bankChunkDatas[source.ChunkIndex].Length;
            if (source.ChunkOffset < 0 || source.ChunkOffset + source.Length > rowLength)
            {
                App.Logger.LogWarning("This clip's bytes run past the end of the chunk row they start in, so it cannot be replaced.");
                return false;
            }

            ResAssetEntry resEntry = App.AssetManager.GetResEntry(_bankPath);
            if (resEntry == null)
            {
                App.Logger.LogWarning("This asset's .res clip index is not in the loaded game data, so the clip cannot be replaced.");
                return false;
            }

            task.Update(status: "Replacing the clip in the bank...");
            SoundBankAddResult replaced = SoundBankWriter.ReplaceClipAudio(_bankResData, _bankChunk, ordinal, bytes);
            if (!replaced.Ok)
            {
                App.Logger.LogWarning(replaced.Refusal);
                return false;
            }

            // Only this row changes: the bytes before it stay, and the row is as long as it was plus what the chunk grew.
            long delta = replaced.Chunk.LongLength - _bankChunk.LongLength;
            int newRowLength = (int)(rowLength + delta);
            byte[] newRow = new byte[newRowLength];
            Buffer.BlockCopy(replaced.Chunk, (int)rowStart, newRow, 0, newRowLength);

            if (reloadList)
            {
                if (!WriteBankEdit(resEntry, replaced, new Dictionary<int, byte[]> { { source.ChunkIndex, newRow } }, task, ordinal))
                    return false;
            }
            else if (!WriteBankEditWithoutReload(resEntry, replaced, source.ChunkIndex, newRow))
            {
                return false;
            }

            LogReport(replaced.Report);
            return true;
        }

        /// <summary>
        /// Replaces a clip's audio in a bank that keeps each clip in its own chunk: only that chunk changes - the bytes
        /// before the clip stay, the new clip (padded to 4 bytes) takes the rest - and the bank's .res states the row's
        /// new ChunkSize and the clip's new Duration (<see cref="SoundBankWriter.ReplaceClipAudioInRows"/>).
        /// </summary>
        private bool ReplaceClipInOwnChunk(int ordinal, byte[] bytes, FrostyTaskWindow task, bool reloadList)
        {
            ResAssetEntry resEntry = App.AssetManager.GetResEntry(_bankPath);
            if (resEntry == null)
            {
                App.Logger.LogWarning("This asset's .res clip index is not in the loaded game data, so the clip cannot be replaced.");
                return false;
            }

            task.Update(status: "Replacing the clip in its chunk...");
            SoundBankAddResult replaced = SoundBankWriter.ReplaceClipAudioInRows(_bankResData, BankRowsInResOrder(), ordinal, bytes);
            if (!replaced.Ok)
            {
                App.Logger.LogWarning(replaced.Refusal);
                return false;
            }

            int assetRow = _bankResRowToAsset[replaced.ChunkRow];
            if (reloadList)
            {
                if (!WriteBankEdit(resEntry, replaced, new Dictionary<int, byte[]> { { assetRow, replaced.RowBytes } }, task, ordinal))
                    return false;
            }
            else if (!WriteBankEditWithoutReload(resEntry, replaced, assetRow, replaced.RowBytes))
            {
                return false;
            }

            if (_bankChunk == null)
                RebuildBankChunk();
            LogReport(replaced.Report);
            return true;
        }

        /// <summary><see cref="WriteBankEdit"/> for one row, leaving the list to the caller.</summary>
        private bool WriteBankEditWithoutReload(ResAssetEntry resEntry, SoundBankAddResult edit, int row, byte[] data)
        {
            ChunkAssetEntry entry = _bankChunkEntries[row];
            if (!WriteChunk(entry, data, "clip's chunk row"))
                return false;
            if (!WriteRes(resEntry, edit.Res, "bank's .res clip index"))
            {
                PutBack(new List<int> { row });
                return false;
            }

            _bankChunkDatas[row] = data;
            _bankChunk = edit.Chunk;
            _bankResData = edit.Res;

            RunOnUiThread(() =>
            {
                dynamic chunks = ((dynamic)RootObject).Chunks;
                chunks[row].ChunkSize = (uint)data.Length;
                AssetModified = true;
                InvokeOnAssetModified();
            });
            LinkToAssetOnUiThread(new AssetEntry[] { entry, resEntry });
            return true;
        }

        // ═══════════════════════════════════════════════════════════
        //  vgmstream decode (shared utility)
        // ═══════════════════════════════════════════════════════════

        internal static short[] DecodeWithVgmstream(byte[] soundBuf, out int channels, out int sampleRate)
        {
            channels = 0; sampleRate = 0;
            string tempDir = Path.GetTempPath();
            // A name of this call's own, not a fixed one. Two decodes that ran at the same time - the track list's
            // task, a second bank's tab, an export while a preview decodes - wrote over each other's input and
            // output here, and vgmstream then read a stream another call was still writing and produced no audio.
            // Measured 2026-10-17 on the census over the exported corpus: at eight threads 3,319 banks' first clips
            // came back undecoded, where the same clips decoded alone.
            string stem = "frosty_vgmstream_" + Guid.NewGuid().ToString("N");
            string tempInput = Path.Combine(tempDir, stem + ".sps");
            string tempOutput = Path.Combine(tempDir, stem + ".wav");
            try
            {
                File.WriteAllBytes(tempInput, soundBuf);
                // Located through the same deployment independent lookup as the EA Layer 3 decoder, so the
                // tool is found wherever it is deployed instead of in the one folder the fixed relative
                // path assumed.
                string vgmstreamPath = NativeAudioTools.Find("vgmstream-cli.exe");
                // The lookup is reported through the helper that tolerates a missing editor: this path is
                // also reached from a harness or a test where Frosty.Core.App has never been set, and
                // logging through App there threw a NullReferenceException out of the decode instead of
                // returning the reason the audio was not decoded.
                if (vgmstreamPath == null) { NativeAudioTools.LogWarning(NativeAudioTools.NotFoundMessage("vgmstream-cli.exe")); return null; }
                if (File.Exists(tempOutput)) File.Delete(tempOutput);
                ProcessStartInfo psi = new ProcessStartInfo { FileName = vgmstreamPath, Arguments = string.Format("-o \"{0}\" \"{1}\"", tempOutput, tempInput), UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                using (Process proc = Process.Start(psi)) { proc.WaitForExit(30000); if (proc.ExitCode != 0) { NativeAudioTools.LogWarning("vgmstream-cli.exe failed: exit code " + proc.ExitCode); return null; } }
                if (!File.Exists(tempOutput)) return null;
                using (var wavReader = new WaveFileReader(tempOutput))
                { channels = wavReader.WaveFormat.Channels; sampleRate = wavReader.WaveFormat.SampleRate; byte[] buffer = new byte[wavReader.Length]; int bytesRead = wavReader.Read(buffer, 0, buffer.Length); short[] samples = new short[bytesRead / 2]; Buffer.BlockCopy(buffer, 0, samples, 0, bytesRead); return samples; }
            }
            catch (Exception ex) { NativeAudioTools.LogWarning("vgmstream decode failed: " + ex.Message); return null; }
            finally { try { if (File.Exists(tempInput)) File.Delete(tempInput); } catch { } try { if (File.Exists(tempOutput)) File.Delete(tempOutput); } catch { } }
        }
    }
}
