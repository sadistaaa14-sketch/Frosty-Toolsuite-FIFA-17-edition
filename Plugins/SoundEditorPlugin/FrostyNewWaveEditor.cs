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

        /// <summary>Those chunks end to end, which is the buffer the clips' offsets address.</summary>
        private byte[] _bankChunk;

        /// <summary>The bank's own index as read, and the sentences an add and a delete would be refused with.</summary>
        private byte[] _bankResData;
        private string _bankAddRefusal;
        private string _bankRemoveRefusal;

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
                if (!_isLocalizedWave || _bank == null || _bankResData == null || _bankChunk == null)
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
                    text.Append("NOT LOADABLE BY THE GAME: ").Append(_bank.ChainProblem).Append(" · ");

                int clipCount = _bank.Clips == null ? 0 : _bank.Clips.Count;
                text.Append(clipCount.ToString("N0", CultureInfo.InvariantCulture)).Append(clipCount == 1 ? " clip" : " clips");

                if (clipCount > 0)
                {
                    SoundBankClip first = _bank.Clips[0];
                    if (!string.IsNullOrEmpty(first.CodecName))
                        text.Append(" · ").Append(first.CodecName);
                    if (first.SampleRate > 0)
                        text.Append(" · ").Append(first.SampleRate.ToString("N0", CultureInfo.InvariantCulture)).Append(" Hz");
                }

                if (_bank.KeyParameters.Count > 0)
                {
                    text.Append(" · looked up by ")
                        .Append(string.Join(" + ", _bank.KeyParameters.Select(SoundBankParameterTable.NameOf).ToArray()));
                    if (_bank.Groups.Count > 1)
                        text.Append(" (").Append(_bank.Groups.Count).Append(" groups)");
                }
                else
                {
                    text.Append(" · no selection parameter (the clips are the bank's variations)");
                }

                text.Append(" · ").Append(_bankPath ?? "sound bank");
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
            return InitialLoadNewWave(task);
        }

        protected override void ImportSound(FrostyOpenFileDialog ofd, FrostyTaskWindow task)
        {
            if (_isLocalizedWave) { ImportSoundBank(ofd, task); return; }

            // A bank whose index could not be read still has one: writing a chunk under it would leave the .res
            // stating offsets and sizes for audio that is no longer there.
            if (_bankShaped && _bankPath != null && App.AssetManager.GetResEntry(_bankPath) != null)
            {
                App.Logger.LogWarning("This asset's clip index ({0}) could not be read ({1}), so importing over its chunk would leave "
                    + "the index describing audio that is no longer there; nothing was written.", _bankPath, _noIndexReason ?? "see above");
                return;
            }

            ImportSoundNewWave(ofd, task);
        }

        private void ImportSoundNewWave(FrostyOpenFileDialog ofd, FrostyTaskWindow task)
        {
            byte[] resultBuf = ConvertAudioFileToPcm16Big(ofd);

            int index = 0;
            Dispatcher?.Invoke(() => { index = SelectedTrackIndex; });

            dynamic newWave = RootObject;
            dynamic soundDataChunk = newWave.Chunks[index];
            ChunkAssetEntry chunkEntry = App.AssetManager.GetChunkEntry(soundDataChunk.ChunkId);

            if (!WriteChunk(chunkEntry, resultBuf, "clip's chunk"))
                return;

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
            });
        }

        private List<SoundDataTrack> InitialLoadNewWave(FrostyTaskWindow task)
        {
            List<SoundDataTrack> retVal = new List<SoundDataTrack>();
            dynamic newWave = RootObject;

            int index = 0;
            foreach (dynamic soundDataChunk in newWave.Chunks)
            {
                SoundDataTrack track = new SoundDataTrack {Name = "Track #" + ((index++) + 1)};
                ChunkAssetEntry chunkEntry = App.AssetManager.GetChunkEntry(soundDataChunk.ChunkId);
                if (chunkEntry == null)
                { App.Logger.LogWarning($"SoundChunk {soundDataChunk.ChunkId} doesn't exist. This could be because its a LocalizedChunk that is not loaded by your game."); }
                else
                {
                    using (NativeReader reader = new NativeReader(App.AssetManager.GetChunk(chunkEntry)))
                    {
                        List<short> decodedSoundBuf = new List<short>();
                        reader.Position = 0;
                        uint headerSize = reader.ReadUInt(Endian.Big) & 0x00ffffff;
                        byte codec = reader.ReadByte();
                        int channels = (reader.ReadByte() >> 2) + 1;
                        ushort sampleRate = reader.ReadUShort(Endian.Big);
                        uint sampleCount = reader.ReadUInt(Endian.Big) & 0x00ffffff;
                        switch (codec)
                        {
                            case 0x12: track.Codec = "Pcm16Big"; break;
                            case 0x14: track.Codec = "Xas1"; break;
                            case 0x15: track.Codec = "EaLayer31"; break;
                            case 0x16: track.Codec = "EaLayer32Pcm"; break;
                            case 0x1c: track.Codec = "EaOpus"; break;
                            case 0x19: track.Codec = "EaSpeex"; break;
                            default: track.Codec = "Unknown (" + codec.ToString("x2") + ")"; break;
                        }
                        reader.Position = 0;
                        byte[] soundBuf = reader.ReadToEnd();
                        double duration = 0.0;
                        // One chunk that cannot be decoded must not fail the whole load. The native EA
                        // Layer 3 decoder faults outright on streams it cannot take, and that exception
                        // used to leave InitialLoad - and so left the editor with an empty track list. It
                        // now costs this track's audio, and the reason goes to the log.
                        string decodeFailure = null;
                        short[] decodedSamples = null;

                        try
                        {
                            if (codec == 0x12)
                                decodedSamples = Pcm16b.Decode(soundBuf);
                            else if (codec == 0x14)
                                decodedSamples = XAS.Decode(soundBuf);
                            else if (codec == 0x15 || codec == 0x16)
                            {
                                decodedSamples = EALayer3.TryDecode(soundBuf);
                                if (decodedSamples == null)
                                    decodeFailure = EALayer3.DescribeFailure();
                            }
                            else if (codec == 0x19 || codec == 0x1c)
                                decodeFailure = "no decoder is installed for this codec (vgmstream-cli.exe)";
                            else
                                decodeFailure = "codec 0x" + codec.ToString("x2") + " is not supported by this editor";
                        }
                        catch (Exception ex)
                        {
                            decodeFailure = ex.Message;
                            decodedSamples = null;
                        }

                        if (decodedSamples == null || decodedSamples.Length == 0)
                        {
                            // EA Speex and EA Opus are decoded by vgmstream, and an EA Layer 3 stream the
                            // native decoder refuses is worth one attempt there too.
                            int vgmChannels, vgmSampleRate;
                            short[] viaVgmstream = DecodeWithVgmstream(soundBuf, out vgmChannels, out vgmSampleRate);
                            if (viaVgmstream != null && viaVgmstream.Length > 0)
                            {
                                decodedSamples = viaVgmstream;
                                channels = vgmChannels;
                                sampleRate = (ushort)vgmSampleRate;
                                decodeFailure = null;
                            }
                        }

                        if (decodedSamples != null && decodedSamples.Length > 0)
                        {
                            decodedSoundBuf.AddRange(decodedSamples);
                            duration += (decodedSamples.Length / channels) / (double)sampleRate;
                        }
                        else
                        {
                            App.Logger.LogWarning("Track #{0} ({1}): {2}. It is listed without audio.",
                                index, track.Codec, decodeFailure ?? "no decoder could read this chunk");
                        }

                        track.SampleRate = sampleRate; track.ChannelCount = channels; track.Duration += duration; track.Samples = decodedSoundBuf.ToArray();

                        var topSpacerColor = System.Drawing.Color.FromArgb(64, 83, 22, 3);
                        var wfSettings = new SoundCloudBlockWaveFormSettings(System.Drawing.Color.FromArgb(196, 197, 53, 0), topSpacerColor, System.Drawing.Color.FromArgb(196, 79, 26, 0), System.Drawing.Color.FromArgb(64, 79, 79, 79))
                        { Name = "SoundCloud Orange Transparent Blocks", PixelsPerPeak = 2, SpacerPixels = 1, TopSpacerGradientStartColor = topSpacerColor, BackgroundColor = System.Drawing.Color.Transparent, Width = 800, TopHeight = 50, BottomHeight = 30 };
                        try
                        {
                            var renderer = new WaveFormRenderer();
                            var image = renderer.Render(decodedSoundBuf.ToArray(), new MaxPeakProvider(), wfSettings);
                            using (var ms = new MemoryStream())
                            {
                                image.Save(ms, ImageFormat.Png); ms.Seek(0, SeekOrigin.Begin);
                                var bitmapImage = new BitmapImage(); bitmapImage.BeginInit(); bitmapImage.CacheOption = BitmapCacheOption.OnLoad; bitmapImage.StreamSource = ms; bitmapImage.EndInit();
                                var target = new RenderTargetBitmap(bitmapImage.PixelWidth, bitmapImage.PixelHeight, bitmapImage.DpiX, bitmapImage.DpiY, PixelFormats.Pbgra32);
                                var visual = new DrawingVisual(); using (var r = visual.RenderOpen()) { r.DrawImage(bitmapImage, new Rect(0, 0, bitmapImage.Width, bitmapImage.Height)); }
                                target.Render(visual); target.Freeze(); track.WaveForm = target;
                            }
                        }
                        catch (Exception) { }
                        track.SegmentCount = 1;
                    }
                }
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

            // The clips' offsets address the asset's chunk rows laid end to end. A row the game data does not hold
            // leaves a hole the offsets after it would be read across, so the bank is not read through its index.
            int missing = _bankChunkEntries.FindIndex(e => e == null);
            if (missing >= 0)
                return ListChunksWithoutIndex(task, "chunk row " + missing + " of this asset is not loaded from the game data, so "
                    + "the clip offsets cannot be placed");

            task.Update(status: "Reading the bank's clip index...");
            string refusal;
            SoundBankFile bank = ReadBankIndex(assetName, out refusal);

            if (bank == null || !bank.IsBank)
                return ListChunksWithoutIndex(task, refusal ?? "no clip index was found for it");

            string clipRefusal = bank.ResolveClips(_bankChunk);
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

            // Whether a clip can be added or removed, asked once per load: the answer is a property of the file.
            task.Update(status: "Checking which edits this bank can take...");
            _bankAddRefusal = SoundBankWriter.RefusalForAudio(_bankResData, _bankChunk, 0, "frosty-probe-" + Guid.NewGuid().ToString("N"));
            if (_bankAddRefusal != null)
                App.Logger.Log("Sound bank {0}: a clip cannot be added - {1}", assetName, _bankAddRefusal);

            _bankRemoveRefusal = SoundBankWriter.RefusalForRemove(_bankResData, _bankChunk, 0);
            if (_bankRemoveRefusal != null)
                App.Logger.Log("Sound bank {0}: a clip cannot be removed - {1}", assetName, _bankRemoveRefusal);

            App.Logger.Log("Sound bank {0}: {1}", assetName, bank.Describe());

            List<SoundDataTrack> tracks = new List<SoundDataTrack>(bank.Clips.Count);
            _bankClipSources = new List<BankClipSource>(bank.Clips.Count);

            for (int i = 0; i < bank.Clips.Count; i++)
            {
                SoundBankClip clip = bank.Clips[i];

                if (i % 250 == 0 || i == bank.Clips.Count - 1)
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

                long local;
                int chunkIndex = ChunkOf(clip.Offset, out local);

                track.ClipText = string.Format("0x{0:X} + {1:N0} B", clip.Offset, clip.Length);
                NameClip(track, clip, i);

                tracks.Add(track);
                _bankClipSources.Add(new BankClipSource
                {
                    ChunkIndex = chunkIndex,
                    ChunkOffset = local,
                    Offset = clip.Offset,
                    Length = clip.Length,
                });
            }

            App.Logger.Log("Sound bank {0}: {1} clips listed, nothing decoded yet.", assetName, tracks.Count);
            return tracks;
        }

        /// <summary>The list's words for one clip: its key in the game's words, which variation of its block it is, the raw key.</summary>
        private void NameClip(SoundDataTrack track, SoundBankClip clip, int ordinal)
        {
            string name, variation, key, detail;
            SoundTrackNaming.Label(_bank, _bankPath, ordinal, clip.CodecName, out name, out variation, out key, out detail);
            track.Name = name;
            track.Variation = variation;
            track.KeyText = key;
            track.Detail = detail;
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
                    Detail = "whole chunk · no clip index was readable",
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
            _bankChunk = null;

            try
            {
                dynamic localizedWave = RootObject;
                foreach (dynamic sdc in localizedWave.Chunks)
                {
                    ChunkAssetEntry entry = App.AssetManager.GetChunkEntry(sdc.ChunkId);
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
        protected override void AddTrack(FrostyOpenFileDialog ofd, FrostyTaskWindow task)
        {
            if (!_isLocalizedWave)
            {
                base.AddTrack(ofd, task);
                return;
            }

            int source = _pendingClipSource;
            string name = _pendingClipName;
            SoundBankClipKey key = _pendingClipKey;

            if (string.IsNullOrEmpty(name) || _bank == null || _bankResData == null || _bankChunk == null)
            {
                App.Logger.LogWarning("Adding a clip needs a name for it and a loaded bank; one of those was missing.");
                return;
            }

            ResAssetEntry resEntry = App.AssetManager.GetResEntry(_bankPath);
            if (resEntry == null)
            {
                App.Logger.LogWarning("This asset's .res clip index is not in the loaded game data, so a new clip cannot be written.");
                return;
            }

            task.Update(status: "Encoding the audio file...");
            byte[] clipBytes = ConvertAudioFileToPcm16Big(ofd);
            if (clipBytes == null || clipBytes.Length == 0)
            {
                App.Logger.LogWarning("The file chosen for the new clip decoded to no audio, so nothing was written.");
                return;
            }

            task.Update(status: "Adding the clip to the bank...");
            SoundBankAddResult added = SoundBankWriter.AddClipAudio(_bankResData, _bankChunk, clipBytes, source, name, key);
            if (!added.Ok)
            {
                App.Logger.LogWarning(added.Refusal);
                return;
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
                return;

            LogReport(added.Report);
            App.Logger.Log("Added clip {0} (\"{1}\") to {2}, filed under {3}; the bank now has {4} clips.",
                added.Ordinal + 1, name, _bankPath, added.Key ?? "no key", added.Ordinal + 1);
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
        /// The dialog: one box per parameter the bank's clips are looked up by, starting on the selected clip's own
        /// values, and the name whose engine hash becomes the clip's VariationId. The line under the boxes is the
        /// writer's own answer to exactly what is typed, so a key the bank cannot take is said before anything runs.
        /// </summary>
        private string AskForNewClip(int source, out SoundBankClipKey key)
        {
            key = null;

            IList<uint> parameters = _bank.KeyParameters;
            IList<SoundBankKeyValue> sourceKey = _bank.KeyOfClip(source);

            Window window = new Window
            {
                Title = "Add Clip",
                Width = 620,
                SizeToContent = SizeToContent.Height,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                ResizeMode = ResizeMode.NoResize,
                Owner = Window.GetWindow(this),
            };

            StackPanel panel = new StackPanel { Margin = new Thickness(12) };
            panel.Children.Add(new TextBlock
            {
                Text = parameters.Count == 0
                    ? "This bank has no selection parameter: the game plays its clips for an event that passes none, so a new "
                        + "clip is one more of its variations."
                    : "The game finds this bank's clips by " + string.Join(" + ", parameters.Select(SoundBankParameterTable.NameOf).ToArray())
                        + ". The new clip is played for the values below (they start on the selected clip's, so keeping them "
                        + "adds another variation of that clip). A value the bank does not have yet is added to its list.",
                TextWrapping = TextWrapping.Wrap,
            });

            List<TextBox> boxes = new List<TextBox>();
            List<TextBlock> notes = new List<TextBlock>();
            foreach (uint id in parameters)
            {
                SoundBankParameterTable.Entry entry = SoundBankParameterTable.Find(id);
                StringBuilder label = new StringBuilder(SoundBankParameterTable.NameOf(id));
                if (entry != null && entry.Values.Length > 0)
                {
                    label.Append("  (");
                    label.Append(string.Join(", ", entry.Values.Select((v, i) => v.ToString(CultureInfo.InvariantCulture) + " = " + entry.ValueNames[i]).ToArray()));
                    label.Append(")");
                }
                else if (id == BankNames.Language)
                {
                    label.Append("  (the game looks up its language index first and 0 when that finds nothing)");
                }

                panel.Children.Add(new TextBlock { Text = label.ToString(), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) });
                TextBox box = new TextBox { Margin = new Thickness(0, 3, 0, 0) };
                SoundBankKeyValue start = sourceKey == null ? null : sourceKey.FirstOrDefault(v => v.ParameterId == id);
                if (start != null)
                    box.Text = start.Value.ToString(CultureInfo.InvariantCulture);
                panel.Children.Add(box);
                boxes.Add(box);
                TextBlock note = new TextBlock { TextWrapping = TextWrapping.Wrap, Opacity = 0.75, Margin = new Thickness(0, 2, 0, 0) };
                panel.Children.Add(note);
                notes.Add(note);
            }

            panel.Children.Add(new TextBlock
            {
                Text = "Name (the clip's VariationId is the engine hash of it, so it has to be one no clip of this bank uses):",
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 10, 0, 0),
            });
            TextBox nameBox = new TextBox { Margin = new Thickness(0, 3, 0, 0), Text = FallbackClipName() };
            panel.Children.Add(nameBox);

            TextBlock preview = new TextBlock { TextWrapping = TextWrapping.Wrap, Opacity = 0.8, Margin = new Thickness(0, 10, 0, 0) };
            panel.Children.Add(preview);

            Button ok = new Button
            {
                Content = "Add Clip",
                Width = 90,
                Height = 24,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 12, 0, 0),
            };
            panel.Children.Add(ok);

            SoundBankClipKey typedKey = null;
            Action refresh = () =>
            {
                typedKey = new SoundBankClipKey();
                bool parsed = true;
                for (int i = 0; i < parameters.Count; i++)
                {
                    int value;
                    string text = boxes[i].Text == null ? "" : boxes[i].Text.Trim();
                    if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
                    {
                        notes[i].Text = text.Length == 0 ? "A value is needed." : "\"" + text + "\" is not a whole number.";
                        parsed = false;
                        continue;
                    }

                    typedKey.Set(parameters[i], value);
                    SoundBankKeyValue kv = SoundBankKeyValue.Of(parameters[i], value);
                    string dbSource;
                    string dbName = SoundTrackNaming.DatabaseName(kv, out dbSource);
                    notes[i].Text = kv.ValueName != null ? kv.ValueName
                        : (dbName != null ? dbName + " (game DB, " + dbSource + ")" : "");
                }

                string name = nameBox.Text == null ? "" : nameBox.Text.Trim();
                if (!parsed || name.Length == 0)
                {
                    preview.Text = name.Length == 0 ? "A name is needed." : "Every value has to be a whole number.";
                    ok.IsEnabled = false;
                    return;
                }

                string refusal = SoundBankWriter.RefusalForAudio(_bankResData, _bankChunk, source, name, typedKey);
                if (refusal != null)
                {
                    preview.Text = refusal;
                    ok.IsEnabled = false;
                    return;
                }

                int already = CountClipsWithKey(typedKey);
                preview.Text = parameters.Count == 0
                    ? "The bank has no selection parameter, so the new clip becomes row " + (_bank.Clips.Count + 1)
                        + " of its one block of rows."
                    : (already == 0
                        ? "No clip of this bank is filed under these values yet: the new clip is the only row they find."
                        : "These values find " + already + (already == 1 ? " clip" : " clips") + " now; with the new one their block holds "
                            + (already + 1) + " rows.");
                ok.IsEnabled = true;
            };

            foreach (TextBox box in boxes)
                box.TextChanged += (sender, e) => refresh();
            nameBox.TextChanged += (sender, e) => refresh();
            ok.Click += (sender, e) => { window.DialogResult = true; };

            window.Content = panel;
            refresh();
            if (boxes.Count > 0)
                boxes[0].Focus();
            else
                nameBox.Focus();

            if (window.ShowDialog() != true)
                return null;

            key = parameters.Count == 0 ? null : typedKey;
            string chosen = nameBox.Text == null ? "" : nameBox.Text.Trim();
            return chosen.Length == 0 ? null : chosen;
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

        /// <summary>A default name for a new clip: the bank's own name and the clip's number, unique in the bank.</summary>
        private string FallbackClipName()
        {
            string title = SoundTrackNaming.BankTitle(_bankPath);
            int number = _bank == null || _bank.Clips == null ? 1 : _bank.Clips.Count + 1;
            string baseName = title + " " + number.ToString(CultureInfo.InvariantCulture);
            if (!ClipNameTaken(baseName))
                return baseName;

            int suffix = 2;
            while (ClipNameTaken(baseName + " (" + suffix.ToString(CultureInfo.InvariantCulture) + ")"))
                suffix++;
            return baseName + " (" + suffix.ToString(CultureInfo.InvariantCulture) + ")";
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
            if (_bankClipSources == null || _bankChunk == null)
                return base.EnsureSamples(track, trackIndex);

            if (track == null || trackIndex < 0 || trackIndex >= _bankClipSources.Count)
                return false;

            if (track.SamplesLoaded && track.Samples != null && track.Samples.Length > 0)
                return true;

            BankClipSource source = _bankClipSources[trackIndex];
            if (source.Length <= 0 || source.Offset < 0 || source.Offset + source.Length > _bankChunk.Length)
            {
                App.Logger.LogWarning("Clip #{0} states a byte range outside the chunk that was loaded, so it cannot be decoded.",
                    track.Index);
                return false;
            }

            byte[] clipBytes = new byte[source.Length];
            Buffer.BlockCopy(_bankChunk, (int)source.Offset, clipBytes, 0, clipBytes.Length);

            int channels, sampleRate;
            string reason;
            short[] decoded = DecodeClipAudio(clipBytes, out channels, out sampleRate, out reason);

            if (decoded == null || decoded.Length == 0)
            {
                App.Logger.LogWarning("Clip #{0} ({1}) could not be decoded: {2}", track.Index, track.Codec,
                    reason ?? "no decoder produced audio");
                return false;
            }

            track.Samples = decoded;
            track.ChannelCount = channels;
            track.SampleRate = sampleRate;
            track.Duration = SoundEdits.DurationSeconds(decoded, channels, sampleRate);
            track.SegmentCount = 1;
            track.SamplesLoaded = true;
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

        /// <summary>
        /// Imports a file into the selected clip: the clip's audio is replaced by the file's, of any length.
        ///
        /// A bank's clip is a byte range of a shared chunk, so changing its length is a cut and an insert:
        /// `SoundBankWriter.ReplaceClipAudio` pads the new bytes to a 4-byte boundary, moves every clip after it, and
        /// states the row's `Segments.Duration` as the float32 bits of the new clip's length (FUN_147982530).
        /// </summary>
        private void ImportSoundBank(FrostyOpenFileDialog ofd, FrostyTaskWindow task)
        {
            int index = 0;
            Dispatcher?.Invoke(() => { index = SelectedTrackIndex; });

            if (index < 0 || _bankClipSources == null || index >= _bankClipSources.Count)
            {
                App.Logger.LogWarning("Select a clip to import into first.");
                return;
            }

            task.Update(status: "Encoding the audio file...");
            byte[] resultBuf = ConvertAudioFileToPcm16Big(ofd);
            if (resultBuf == null || resultBuf.Length == 0)
            {
                App.Logger.LogWarning("The file chosen decodes to no audio, so nothing was written.");
                return;
            }

            if (!ReplaceClip(index, _bankClipSources[index], resultBuf, task, true))
                return;

            App.Logger.Log("Imported {0} into clip #{1}: {2:N0} bytes of audio.",
                Path.GetFileName(ofd.FileName), index + 1, resultBuf.Length);
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
