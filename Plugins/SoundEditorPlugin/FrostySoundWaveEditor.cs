using System;
using System.Collections.Generic;
using FrostySdk.Interfaces;
using System.Windows;
using FrostySdk.IO;
using System.IO;
using FrostySdk;
using FrostySdk.Managers;
using FrostySdk.Ebx;
using WaveFormRendererLib;
using System.Drawing.Imaging;
using System.Windows.Media.Imaging;
using System.Windows.Media;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using System.Diagnostics;
using Frosty.Core.Controls;
using Frosty.Core;
using Frosty.Core.Windows;

namespace SoundEditorPlugin
{
    public class FrostySoundWaveEditor : FrostySoundDataEditor
    {
        public FrostySoundWaveEditor()
            : base(null)
        {
        }

        public FrostySoundWaveEditor(ILogger inLogger) 
            : base(inLogger)
        {
        }

        protected override List<SoundDataTrack> InitialLoad(FrostyTaskWindow task)
        {
            List<SoundDataTrack> retVal = new List<SoundDataTrack>();
            dynamic soundWave = RootObject;

            int index = 0;
            int totalCount = soundWave.RuntimeVariations.Count;

            foreach (dynamic runtimeVariation in soundWave.RuntimeVariations)
            {
                task.Update(status: "Loading Track #" + (index + 1), progress: ((index + 1) / (double)totalCount) * 100.0d);

                SoundDataTrack track = new SoundDataTrack {Name = "Track #" + ((index++) + 1)};

                dynamic soundDataChunk = soundWave.Chunks[runtimeVariation.ChunkIndex];
                ChunkAssetEntry chunkEntry = App.AssetManager.GetChunkEntry(soundDataChunk.ChunkId);

                if (chunkEntry == null)
                {
                    App.Logger.LogWarning($"SoundChunk {soundDataChunk.ChunkId} doesn't exist. This could be because its a LocalizedChunk that is not loaded by your game.");
                }
                else
                {
                    using (NativeReader reader = new NativeReader(App.AssetManager.GetChunk(chunkEntry)))
                    {
                        List<short> decodedSoundBuf = new List<short>();
                        double startLoopingTime = 0.0;
                        double loopingDuration = 0.0;

                        for (int i = 0; i < runtimeVariation.SegmentCount; i++)
                        {
                            var segment = soundWave.Segments[runtimeVariation.FirstSegmentIndex + i];
                            reader.Position = segment.SamplesOffset;

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
                                default: track.Codec = "Unknown (" + codec.ToString("x2") + ")"; break;
                            }

                            reader.Position = segment.SamplesOffset;
                            long size = reader.Length - reader.Position;
                            if ((runtimeVariation.FirstSegmentIndex + i + 1 < soundWave.Segments.Count) && ((soundWave.Segments[runtimeVariation.FirstSegmentIndex + i + 1].SamplesOffset > segment.SamplesOffset)))
                            {
                                size = soundWave.Segments[runtimeVariation.FirstSegmentIndex + i + 1].SamplesOffset - reader.Position;
                            }
                            byte[] soundBuf = reader.ReadBytes((int)size);
                            double duration = 0.0;

                            if (codec == 0x12)
                            {
                                short[] data = Pcm16b.Decode(soundBuf);
                                decodedSoundBuf.AddRange(data);
                                duration += (data.Length / channels) / (double)sampleRate;
                                sampleCount = (uint)data.Length;
                            }
                            else if (codec == 0x14)
                            {
                                short[] data = XAS.Decode(soundBuf);
                                decodedSoundBuf.AddRange(data);
                                duration += (data.Length / channels) / (double)sampleRate;
                                sampleCount = (uint)data.Length;
                            }
                            else if (codec == 0x15 || codec == 0x16)
                            {
                                sampleCount = 0;
                                EALayer3.Decode(soundBuf, soundBuf.Length, (short[] data, int count, EALayer3.StreamInfo info) =>
                                {
                                    if (info.streamIndex == -1)
                                        return;
                                    sampleCount += (uint)data.Length;
                                    decodedSoundBuf.AddRange(data);
                                });
                                duration += (sampleCount / channels) / (double)sampleRate;
                            }

                            if (runtimeVariation.SegmentCount > 1)
                            {
                                if (i < runtimeVariation.FirstLoopSegmentIndex)
                                {
                                    startLoopingTime += duration;
                                    track.LoopStart += sampleCount;
                                }
                                if (i >= runtimeVariation.FirstLoopSegmentIndex && i <= runtimeVariation.LastLoopSegmentIndex)
                                {
                                    loopingDuration += duration;
                                    track.LoopEnd += sampleCount;
                                }
                            }

                            track.SampleRate = sampleRate;
                            track.ChannelCount = channels;
                            track.Duration += duration;
                        }

                        track.LoopEnd += track.LoopStart;
                        track.Samples = decodedSoundBuf.ToArray();

                        var maxPeakProvider = new MaxPeakProvider();
                        var rmsPeakProvider = new RmsPeakProvider(200); // e.g. 200
                        var samplingPeakProvider = new SamplingPeakProvider(200); // e.g. 200
                        var averagePeakProvider = new AveragePeakProvider(4); // e.g. 4

                        var topSpacerColor = System.Drawing.Color.FromArgb(64, 83, 22, 3);
                        var soundCloudOrangeTransparentBlocks = new SoundCloudBlockWaveFormSettings(System.Drawing.Color.FromArgb(196, 197, 53, 0), topSpacerColor, System.Drawing.Color.FromArgb(196, 79, 26, 0),
                                                                                                    System.Drawing.Color.FromArgb(64, 79, 79, 79))
                        {
                            Name = "SoundCloud Orange Transparent Blocks",
                            PixelsPerPeak = 2,
                            SpacerPixels = 1,
                            TopSpacerGradientStartColor = topSpacerColor,
                            BackgroundColor = System.Drawing.Color.Transparent,
                            Width = 800,
                            TopHeight = 50,
                            BottomHeight = 30,
                        };

                        try
                        {
                            var renderer = new WaveFormRenderer();
                            var image = renderer.Render(track.Samples, maxPeakProvider, soundCloudOrangeTransparentBlocks);

                            using (var ms = new MemoryStream())
                            {
                                image.Save(ms, ImageFormat.Png);
                                ms.Seek(0, SeekOrigin.Begin);

                                var bitmapImage = new BitmapImage();
                                bitmapImage.BeginInit();
                                bitmapImage.CacheOption = BitmapCacheOption.OnLoad;
                                bitmapImage.StreamSource = ms;
                                bitmapImage.EndInit();

                                var target = new RenderTargetBitmap(bitmapImage.PixelWidth, bitmapImage.PixelHeight, bitmapImage.DpiX, bitmapImage.DpiY, PixelFormats.Pbgra32);
                                var visual = new DrawingVisual();

                                using (var r = visual.RenderOpen())
                                {
                                    visual.SetValue(RenderOptions.EdgeModeProperty, EdgeMode.Aliased);
                                    r.DrawImage(bitmapImage, new Rect(0, 0, bitmapImage.Width, bitmapImage.Height));

                                    if (loopingDuration > 0)
                                    {
                                        r.DrawLine(new Pen(Brushes.White, 1.0),
                                            new Point((int)((startLoopingTime / track.Duration) * soundCloudOrangeTransparentBlocks.Width), soundCloudOrangeTransparentBlocks.TopHeight),
                                            new Point((int)((startLoopingTime / track.Duration) * soundCloudOrangeTransparentBlocks.Width), (int)bitmapImage.Height));
                                        r.DrawLine(new Pen(Brushes.White, 1.0),
                                            new Point((int)(((startLoopingTime + loopingDuration) / track.Duration) * soundCloudOrangeTransparentBlocks.Width), soundCloudOrangeTransparentBlocks.TopHeight),
                                            new Point((int)(((startLoopingTime + loopingDuration) / track.Duration) * soundCloudOrangeTransparentBlocks.Width), (int)bitmapImage.Height));
                                        r.DrawLine(new Pen(Brushes.White, 1.0),
                                            new Point((int)((startLoopingTime / track.Duration) * soundCloudOrangeTransparentBlocks.Width), (int)bitmapImage.Height),
                                            new Point((int)(((startLoopingTime + loopingDuration) / track.Duration) * soundCloudOrangeTransparentBlocks.Width), (int)bitmapImage.Height));
                                    }
                                }

                                target.Render(visual);
                                target.Freeze();
                                track.WaveForm = target;
                            }
                        }
                        catch (Exception e)
                        {
                        }

                        track.SegmentCount = runtimeVariation.SegmentCount;
                    }
                }

                retVal.Add(track);
            }

            foreach (dynamic localization in soundWave.Localization)
            {
                for (int i = 0; i < localization.VariationCount; i++)
                {
                    SoundDataTrack track = retVal[i + localization.FirstVariationIndex];

                    PointerRef pr = localization.Language;
                    EbxAsset asset = App.AssetManager.GetEbx(App.AssetManager.GetEbxEntry(pr.External.FileGuid));
                    dynamic obj = asset.GetObject(pr.External.ClassGuid);

                    track.Language = obj.__Id;
                }
            }

            return retVal;
        }
    }

    // ═══════════════════════════════════════════════════════════════
    //  FrostyNewWaveEditor — handles NewWaveAsset + LocalizedWaveAsset
    //  LocalizedWaveAsset is auto-detected (has Chunks but no RuntimeVariations)
    // ═══════════════════════════════════════════════════════════════

    public class FrostyNewWaveEditor : FrostySoundDataEditor
    {
        // ── LocalizedWave state ──
        private bool _isLocalizedWave;
        private List<int> _lwTrackChunkIndices;
        private List<int> _lwTrackStartOffsets;
        private List<int> _lwTrackEndOffsets;
        private bool _lwUsedResOffsets;
        private ResAssetEntry _lwResEntry;
        private int _lwResTableJFileOffset = -1;
        private int _lwResTableJCopyFileOffset = -1;
        private int[] _lwCommentaryIds; // Table A: commentary IDs per variation (plain integers)

        private const uint HASH_CHUNK_OFFSETS = 0xE8E591DD;
        private const uint HASH_CHUNK_SIZE    = 0xDC19107B;
        private const uint HASH_TABLE_A       = 0x6AC4E4EA;

        public FrostyNewWaveEditor() : base(null) { }
        public FrostyNewWaveEditor(ILogger inLogger) : base(inLogger) { }

        public override void OnApplyTemplate()
        {
            base.OnApplyTemplate();
            // On-demand waveform rendering for LocalizedWaveAsset tracks
            tracksListBox.SelectionChanged += (s, e) =>
            {
                if (!_isLocalizedWave) return;
                if (tracksListBox.SelectedItem is SoundDataTrack track &&
                    track.WaveForm == null && track.Samples != null && track.Samples.Length > 0)
                    LwRenderSingleWaveform(track);
            };
        }

        // ── Dispatchers: detect asset type and route to correct path ──

        protected override List<SoundDataTrack> InitialLoad(FrostyTaskWindow task)
        {
            dynamic rootObj = RootObject;
            bool hasRuntimeVariations = false;
            try { hasRuntimeVariations = rootObj.RuntimeVariations != null && rootObj.RuntimeVariations.Count > 0; } catch { }
            bool hasChunks = false;
            try { hasChunks = rootObj.Chunks != null && rootObj.Chunks.Count > 0; } catch { }

            if (hasChunks && !hasRuntimeVariations)
            {
                _isLocalizedWave = true;
                App.Logger.Log("NewWaveEditor: detected LocalizedWaveAsset, using RES/SNR approach.");
                return InitialLoadLocalized(task);
            }
            _isLocalizedWave = false;
            return InitialLoadNewWave(task);
        }

        protected override void ImportSound(FrostyOpenFileDialog ofd, FrostyTaskWindow task)
        {
            if (_isLocalizedWave) { ImportSoundLocalized(ofd, task); return; }
            ImportSoundNewWave(ofd, task);
        }

        // ═══════════════════════════════════════════════════════════
        //  Original NewWaveAsset code (unchanged logic)
        // ═══════════════════════════════════════════════════════════

        private void ImportSoundNewWave(FrostyOpenFileDialog ofd, FrostyTaskWindow task)
        {
            MemoryStream ms = new MemoryStream();
            byte[] resultBuf;
            if (ofd.FileName.EndsWith(".wav", StringComparison.OrdinalIgnoreCase))
            {
                using (var reader = new AudioFileReader(ofd.FileName))
                {
                    if (reader.WaveFormat.Channels == 1)
                    { var stereo = new MonoToStereoSampleProvider(reader) { LeftVolume = 1.0f, RightVolume = 1.0f }; WaveFileWriter.WriteWavFileToStream(ms, new SampleToWaveProvider16(stereo)); }
                    else { WaveFileWriter.WriteWavFileToStream(ms, reader); }
                }
                resultBuf = CreatePcm16BigSound(ms);
            }
            else if (ofd.FileName.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase))
            { using (var reader = new MediaFoundationReader(ofd.FileName)) { WaveFileWriter.WriteWavFileToStream(ms, reader); } resultBuf = CreatePcm16BigSound(ms); }
            else if (ofd.FileName.EndsWith(".ealayer3"))
            { resultBuf = File.ReadAllBytes(ofd.FileName); }
            else { throw new FileFormatException(); }

            int index = 0;
            Dispatcher?.Invoke(() => { index = tracksListBox.SelectedIndex; });

            dynamic newWave = RootObject;
            dynamic soundDataChunk = newWave.Chunks[index];
            ChunkAssetEntry chunkEntry = App.AssetManager.GetChunkEntry(soundDataChunk.ChunkId);

            App.AssetManager.ModifyChunk(chunkEntry.Id, resultBuf);
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
                TracksList.Clear();
                foreach (var track in tracks) TracksList.Add(track);
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
                        if (codec == 0x12) { short[] data = Pcm16b.Decode(soundBuf); decodedSoundBuf.AddRange(data); duration += (data.Length / channels) / (double)sampleRate; }
                        else if (codec == 0x14) { short[] data = XAS.Decode(soundBuf); decodedSoundBuf.AddRange(data); duration += (data.Length / channels) / (double)sampleRate; }
                        else if (codec == 0x15 || codec == 0x16 || codec == 0x1c)
                        { sampleCount = 0; EALayer3.Decode(soundBuf, soundBuf.Length, (short[] data, int count, EALayer3.StreamInfo info) => { if (info.streamIndex == -1) return; sampleCount += (uint)data.Length; decodedSoundBuf.AddRange(data); }); duration += (sampleCount / channels) / (double)sampleRate; }
                        else if (codec == 0x19)
                        { int vgmChannels, vgmSampleRate; short[] data = DecodeWithVgmstream(soundBuf, out vgmChannels, out vgmSampleRate); if (data != null && data.Length > 0) { decodedSoundBuf.AddRange(data); channels = vgmChannels; sampleRate = (ushort)vgmSampleRate; duration += (data.Length / channels) / (double)sampleRate; } }

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
        //  LocalizedWaveAsset support
        //  Audio offsets from RES Table J, commentary IDs from Table A
        // ═══════════════════════════════════════════════════════════

        private struct LwClipLocation { public int ChunkIndex, StartOffset, EndOffset; }

        private List<SoundDataTrack> InitialLoadLocalized(FrostyTaskWindow task)
        {
            List<SoundDataTrack> retVal = new List<SoundDataTrack>();
            _lwTrackChunkIndices = new List<int>(); _lwTrackStartOffsets = new List<int>(); _lwTrackEndOffsets = new List<int>();
            _lwUsedResOffsets = false; _lwResEntry = null; _lwResTableJFileOffset = -1; _lwResTableJCopyFileOffset = -1; _lwCommentaryIds = null;

            try
            {
                dynamic localizedWave = RootObject;
                List<byte[]> chunkDataList = new List<byte[]>();
                foreach (dynamic sdc in localizedWave.Chunks)
                {
                    ChunkAssetEntry ce = App.AssetManager.GetChunkEntry(sdc.ChunkId);
                    if (ce != null) { using (NativeReader r = new NativeReader(App.AssetManager.GetChunk(ce))) chunkDataList.Add(r.ReadToEnd()); }
                    else { App.Logger.LogWarning("LocalizedWave chunk not found."); chunkDataList.Add(null); }
                }

                List<LwClipLocation> clips = LwTryParseResOffsets(chunkDataList);
                if (clips == null || clips.Count == 0) { App.Logger.Log("LocalizedWave: RES not available, using SNR scan."); clips = LwScanChunksForSnr(chunkDataList); }
                if (clips.Count == 0) { for (int c = 0; c < chunkDataList.Count; c++) if (chunkDataList[c] != null && chunkDataList[c].Length > 12) clips.Add(new LwClipLocation { ChunkIndex = c, StartOffset = 0, EndOffset = chunkDataList[c].Length }); }

                int totalClips = clips.Count;
                int maxDisplay = 200;
                int decodeCount = Math.Min(totalClips, maxDisplay);
                App.Logger.Log("LocalizedWave: {0} total clips, displaying first {1} [{2}]", totalClips, decodeCount, _lwUsedResOffsets ? "RES Table J" : "SNR scan");

                for (int s = 0; s < decodeCount; s++)
                {
                    if (s % 50 == 0 || s == decodeCount - 1) task.Update(status: "Loading Track " + (s + 1) + " / " + decodeCount, progress: ((s + 1) / (double)decodeCount) * 90.0d);
                    LwClipLocation loc = clips[s]; byte[] chunkData = chunkDataList[loc.ChunkIndex]; if (chunkData == null) continue;
                    int size = loc.EndOffset - loc.StartOffset; if (size <= 12) continue;
                    byte[] soundBuf = new byte[size]; Array.Copy(chunkData, loc.StartOffset, soundBuf, 0, size);
                    SoundDataTrack track = LwDecodeClipBuffer(soundBuf, s);
                    if (track != null)
                    {
                        if (_lwCommentaryIds != null && s < _lwCommentaryIds.Length)
                            track.Name = "Track #" + (s + 1) + " [CID: " + _lwCommentaryIds[s] + "]";
                        retVal.Add(track); _lwTrackChunkIndices.Add(loc.ChunkIndex); _lwTrackStartOffsets.Add(loc.StartOffset); _lwTrackEndOffsets.Add(loc.EndOffset);
                    }
                }
                App.Logger.Log("LocalizedWave: loaded {0} tracks (waveforms render on demand).", retVal.Count);
            }
            catch (Exception ex) { App.Logger.LogError("LocalizedWaveAsset load failed: " + ex.ToString()); }
            return retVal;
        }

        private List<LwClipLocation> LwTryParseResOffsets(List<byte[]> chunkDataList)
        {
            byte[] resData = LwTryReadResData(); if (resData == null || resData.Length < 0x60) return null;
            try
            {
                int sble = 0x00; // GetRes() strips the 16-byte GUID prefix
                if (resData[sble] != 0x53 || resData[sble + 1] != 0x42 || resData[sble + 2] != 0x6C || resData[sble + 3] != 0x65) return null;
                int datasetCount = BitConverter.ToUInt16(resData, sble + 0x0A);
                int tableJOffset = -1, tableAOffset = -1, variationCount = -1, tableAVarCount = -1; uint chunkSizeFromRes = 0;

                for (int di = 0; di < datasetCount; di++)
                {
                    int dsOff = (int)BitConverter.ToUInt32(resData, sble + 0x50 + di * 8);
                    int absOff = sble + dsOff; if (absOff + 0x48 > resData.Length) continue;
                    if (resData[absOff] != 0x54 || resData[absOff + 1] != 0x45 || resData[absOff + 2] != 0x53 || resData[absOff + 3] != 0x44) continue;
                    int varCount = (int)BitConverter.ToUInt32(resData, absOff + 0x38);
                    int blockCount = BitConverter.ToUInt16(resData, absOff + 0x3C);
                    int blockStart = absOff + 0x48;
                    for (int bi = 0; bi < blockCount; bi++)
                    {
                        int boff = blockStart + bi * 24; if (boff + 24 > resData.Length) break;
                        uint btype = BitConverter.ToUInt32(resData, boff);
                        App.Logger.Log("  TESD#{0} Block{1}: hash=0x{2:X8} data[2]=0x{3:X} data[3]=0x{4:X} data[4]=0x{5:X}",
                            di, bi, btype, BitConverter.ToUInt32(resData, boff + 8), BitConverter.ToUInt32(resData, boff + 12), BitConverter.ToUInt32(resData, boff + 16));
                        if (btype == HASH_CHUNK_OFFSETS) { uint dp = BitConverter.ToUInt32(resData, boff + 16); tableJOffset = sble + (int)dp; variationCount = varCount; _lwResTableJCopyFileOffset = tableJOffset + varCount * 4; App.Logger.Log("LocalizedWave RES: Table J at 0x{0:X}, {1} variations", tableJOffset, varCount); }
                        else if (btype == HASH_CHUNK_SIZE) { chunkSizeFromRes = BitConverter.ToUInt32(resData, boff + 8); }
                        else if (btype == HASH_TABLE_A) { uint dp = BitConverter.ToUInt32(resData, boff + 16); if (dp > 0) { tableAOffset = sble + (int)dp; tableAVarCount = varCount; App.Logger.Log("LocalizedWave RES: Table A at 0x{0:X}, varCount={1}", tableAOffset, tableAVarCount); } }
                    }
                }

                if (tableJOffset < 0 || variationCount <= 0) { App.Logger.LogWarning("LocalizedWave RES: Table J not found (scanned {0} datasets)", datasetCount); return null; }
                App.Logger.Log("LocalizedWave RES: scan complete. tableJ=0x{0:X} tableA=0x{1:X} chunkSize=0x{2:X}", tableJOffset, tableAOffset, chunkSizeFromRes);
                if (tableJOffset + variationCount * 4 > resData.Length) return null;
                uint chunkSize = chunkSizeFromRes; if (chunkSize == 0 && chunkDataList.Count > 0 && chunkDataList[0] != null) chunkSize = (uint)chunkDataList[0].Length;

                // Read Table A (commentary IDs — plain integers, NOT hashed)
                _lwCommentaryIds = null;
                if (tableAOffset >= 0 && tableAVarCount > 0)
                {
                    int tableADataStart = tableAOffset + 8; // skip 8-byte sub-header
                    if (tableADataStart + tableAVarCount * 2 <= resData.Length)
                    {
                        _lwCommentaryIds = new int[tableAVarCount];
                        for (int i = 0; i < tableAVarCount; i++) _lwCommentaryIds[i] = BitConverter.ToUInt16(resData, tableADataStart + i * 2);
                        App.Logger.Log("LocalizedWave RES: read {0} commentary IDs", tableAVarCount);

                        // Log multi-variation commentary IDs so user can find all clips for a player
                        Dictionary<int, List<int>> cidToVars = new Dictionary<int, List<int>>();
                        for (int i = 0; i < tableAVarCount; i++) { int cid = _lwCommentaryIds[i]; if (!cidToVars.ContainsKey(cid)) cidToVars[cid] = new List<int>(); cidToVars[cid].Add(i); }
                        int multiCount = 0;
                        foreach (var kvp in cidToVars) if (kvp.Value.Count > 1) { multiCount++; App.Logger.Log("  CID {0}: variations {1}", kvp.Key, string.Join(", ", kvp.Value)); }
                        App.Logger.Log("LocalizedWave RES: {0} unique CIDs, {1} with multiple variations", cidToVars.Count, multiCount);
                    }
                }
                else
                {
                    App.Logger.LogWarning("LocalizedWave RES: Table A NOT found (tableAOffset={0}, tableAVarCount={1})", tableAOffset, tableAVarCount);
                }

                // Read Table J (chunk byte offsets)
                List<LwClipLocation> clips = new List<LwClipLocation>();
                uint[] offsets = new uint[variationCount];
                for (int i = 0; i < variationCount; i++) offsets[i] = BitConverter.ToUInt32(resData, tableJOffset + i * 4);
                for (int i = 0; i < variationCount; i++)
                { int start = (int)offsets[i]; int end = (i + 1 < variationCount) ? (int)offsets[i + 1] : (int)chunkSize; if (end <= start || start < 0) continue;
                  clips.Add(new LwClipLocation { ChunkIndex = 0, StartOffset = start, EndOffset = end }); }
                _lwUsedResOffsets = true; _lwResTableJFileOffset = tableJOffset; return clips;
            }
            catch (Exception ex) { App.Logger.LogWarning("LocalizedWave RES parse failed: " + ex.Message); return null; }
        }

        private byte[] LwTryReadResData()
        {
            try
            {
                string assetName = null;
                Dispatcher?.Invoke(() => { assetName = (AssetEntry as EbxAssetEntry)?.Name; });
                if (string.IsNullOrEmpty(assetName)) return null;
                ResAssetEntry resEntry = App.AssetManager.GetResEntry(assetName); if (resEntry == null) return null;
                using (NativeReader reader = new NativeReader(App.AssetManager.GetRes(resEntry)))
                { byte[] d = reader.ReadToEnd(); if (d != null && d.Length > 0) { _lwResEntry = resEntry; App.Logger.Log("LocalizedWave: found RES ({0} bytes)", d.Length); return d; } }
            }
            catch (Exception ex) { App.Logger.LogWarning("LocalizedWave RES lookup failed: " + ex.Message); }
            return null;
        }

        private List<LwClipLocation> LwScanChunksForSnr(List<byte[]> chunkDataList)
        {
            List<LwClipLocation> results = new List<LwClipLocation>();
            for (int c = 0; c < chunkDataList.Count; c++)
            {
                byte[] cd = chunkDataList[c]; if (cd == null || cd.Length < 16) continue;
                for (int pos = 0; pos <= cd.Length - 16; pos++)
                {
                    if (cd[pos] != 0x48 || cd[pos + 1] != 0x00 || cd[pos + 2] != 0x00) continue;
                    int hp = cd[pos + 3]; if (hp != 0x0C && hp != 0x14) continue;
                    if (pos + 4 >= cd.Length) continue; byte codec = cd[pos + 4];
                    if (codec != 0x12 && codec != 0x14 && codec != 0x15 && codec != 0x16 && codec != 0x19 && codec != 0x1c) continue;
                    if (pos + 7 >= cd.Length) continue; ushort sr = (ushort)((cd[pos + 6] << 8) | cd[pos + 7]);
                    if (sr != 8000 && sr != 11025 && sr != 12000 && sr != 16000 && sr != 22050 && sr != 24000 && sr != 32000 && sr != 44100 && sr != 48000) continue;
                    int dbp = pos + 4 + hp; if (dbp >= cd.Length || cd[dbp] != 0x44) continue;
                    int endOff = cd.Length; for (int ep = dbp; ep <= cd.Length - 4; ep++) { if (cd[ep] == 0x45 && cd[ep + 1] == 0x00 && cd[ep + 2] == 0x00) { endOff = ep + 4 + cd[ep + 3]; break; } }
                    endOff = Math.Min(endOff, cd.Length); results.Add(new LwClipLocation { ChunkIndex = c, StartOffset = pos, EndOffset = endOff }); pos = Math.Max(pos, endOff - 1);
                }
            }
            return results;
        }

        private static SoundDataTrack LwDecodeClipBuffer(byte[] soundBuf, int trackIndex)
        {
            if (soundBuf == null || soundBuf.Length < 12) return null;
            byte codec; int channels; ushort sampleRate;
            using (NativeReader hdr = new NativeReader(new MemoryStream(soundBuf))) { hdr.ReadUInt(Endian.Big); codec = hdr.ReadByte(); channels = (hdr.ReadByte() >> 2) + 1; sampleRate = hdr.ReadUShort(Endian.Big); }
            SoundDataTrack track = new SoundDataTrack { Name = "Track #" + (trackIndex + 1) };
            switch (codec) { case 0x12: track.Codec = "Pcm16Big"; break; case 0x14: track.Codec = "XAS"; break; case 0x15: track.Codec = "EALayer3 v5"; break; case 0x16: track.Codec = "EALayer3 v6"; break; case 0x19: track.Codec = "EaSpeex"; break; case 0x1c: track.Codec = "EaOpus"; break; default: track.Codec = "Unknown (" + codec.ToString("x2") + ")"; break; }
            List<short> decoded = new List<short>(); double duration = 0.0;
            if (codec == 0x12) { short[] pcm = Pcm16b.Decode(soundBuf); decoded.AddRange(pcm); duration = (pcm.Length / channels) / (double)sampleRate; }
            else if (codec == 0x14) { short[] xas = XAS.Decode(soundBuf); decoded.AddRange(xas); duration = (xas.Length / channels) / (double)sampleRate; }
            else if (codec == 0x15 || codec == 0x16) { bool ok = false; try { uint sc = 0; EALayer3.Decode(soundBuf, soundBuf.Length, (short[] d, int count, EALayer3.StreamInfo info) => { if (info.streamIndex != -1) { sc += (uint)d.Length; decoded.AddRange(d); } }); duration = (sc / channels) / (double)sampleRate; ok = decoded.Count > 0; } catch { decoded.Clear(); } if (!ok) { int vCh, vSr; short[] vd = DecodeWithVgmstream(soundBuf, out vCh, out vSr); if (vd != null && vd.Length > 0) { decoded.AddRange(vd); channels = vCh; sampleRate = (ushort)vSr; duration = (vd.Length / channels) / (double)sampleRate; } } }
            else if (codec == 0x19 || codec == 0x1c) { int vCh, vSr; short[] vd = DecodeWithVgmstream(soundBuf, out vCh, out vSr); if (vd != null && vd.Length > 0) { decoded.AddRange(vd); channels = vCh; sampleRate = (ushort)vSr; duration = (vd.Length / channels) / (double)sampleRate; } }
            if (decoded.Count == 0) return null;
            track.SampleRate = sampleRate; track.ChannelCount = channels; track.Duration = duration; track.Samples = decoded.ToArray(); track.SegmentCount = 1; return track;
        }

        private static void LwRenderSingleWaveform(SoundDataTrack track)
        {
            if (track.Samples == null || track.Samples.Length == 0) return;
            try
            {
                var topSpacer = System.Drawing.Color.FromArgb(64, 83, 22, 3);
                var settings = new SoundCloudBlockWaveFormSettings(System.Drawing.Color.FromArgb(196, 197, 53, 0), topSpacer, System.Drawing.Color.FromArgb(196, 79, 26, 0), System.Drawing.Color.FromArgb(64, 79, 79, 79))
                { Name = "SoundCloud Orange Transparent Blocks", PixelsPerPeak = 2, SpacerPixels = 1, TopSpacerGradientStartColor = topSpacer, BackgroundColor = System.Drawing.Color.Transparent, Width = 800, TopHeight = 50, BottomHeight = 30 };
                var image = new WaveFormRenderer().Render(track.Samples, new MaxPeakProvider(), settings);
                using (var ms = new MemoryStream()) { image.Save(ms, ImageFormat.Png); ms.Seek(0, SeekOrigin.Begin); var bmp = new BitmapImage(); bmp.BeginInit(); bmp.CacheOption = BitmapCacheOption.OnLoad; bmp.StreamSource = ms; bmp.EndInit(); var target = new RenderTargetBitmap(bmp.PixelWidth, bmp.PixelHeight, bmp.DpiX, bmp.DpiY, PixelFormats.Pbgra32); var visual = new DrawingVisual(); using (var dc = visual.RenderOpen()) dc.DrawImage(bmp, new Rect(0, 0, bmp.Width, bmp.Height)); target.Render(visual); target.Freeze(); track.WaveForm = target; }
            }
            catch (Exception) { }
        }

        private void ImportSoundLocalized(FrostyOpenFileDialog ofd, FrostyTaskWindow task)
        {
            byte[] resultBuf = LwConvertAudioFile(ofd);
            int selectedIndex = 0; Dispatcher?.Invoke(() => { selectedIndex = tracksListBox.SelectedIndex; });
            if (_lwTrackChunkIndices == null || selectedIndex < 0 || selectedIndex >= _lwTrackChunkIndices.Count) { App.Logger.LogWarning("Invalid track selection."); return; }
            int chunkIdx = _lwTrackChunkIndices[selectedIndex]; int trackStart = _lwTrackStartOffsets[selectedIndex]; int trackEnd = _lwTrackEndOffsets[selectedIndex];
            dynamic localizedWave = RootObject; dynamic chunkDyn = localizedWave.Chunks[chunkIdx];
            ChunkAssetEntry chunkEntry = App.AssetManager.GetChunkEntry(chunkDyn.ChunkId); if (chunkEntry == null) { App.Logger.LogWarning("Chunk not available."); return; }
            byte[] chunkData; using (NativeReader reader = new NativeReader(App.AssetManager.GetChunk(chunkEntry))) chunkData = reader.ReadToEnd();
            trackEnd = Math.Min(trackEnd, chunkData.Length); int sizeDiff = resultBuf.Length - (trackEnd - trackStart);
            byte[] newChunk = new byte[chunkData.Length + sizeDiff];
            Array.Copy(chunkData, 0, newChunk, 0, trackStart); Array.Copy(resultBuf, 0, newChunk, trackStart, resultBuf.Length); Array.Copy(chunkData, trackEnd, newChunk, trackStart + resultBuf.Length, chunkData.Length - trackEnd);
            App.AssetManager.ModifyChunk(chunkEntry.Id, newChunk); chunkDyn.ChunkSize = (uint)newChunk.Length;
            if (_lwUsedResOffsets && _lwResEntry != null && _lwResTableJFileOffset >= 0 && sizeDiff != 0) LwPatchResTableJ(selectedIndex, sizeDiff, (uint)newChunk.Length);
            audioPlayer.Dispose(); audioPlayer = new AudioPlayer();
            List<SoundDataTrack> tracks = InitialLoad(task);
            Dispatcher?.Invoke(() => { AssetModified = true; InvokeOnAssetModified(); EbxAssetEntry ae = AssetEntry as EbxAssetEntry; ae.LinkAsset(chunkEntry); if (_lwResEntry != null) ae.LinkAsset(_lwResEntry); TracksList.Clear(); foreach (var t in tracks) TracksList.Add(t); });
        }

        private void LwPatchResTableJ(int modifiedIndex, int sizeDiff, uint newChunkSize)
        {
            try
            {
                byte[] resData; using (NativeReader reader = new NativeReader(App.AssetManager.GetRes(_lwResEntry))) resData = reader.ReadToEnd();
                int count = _lwTrackStartOffsets.Count;
                for (int i = modifiedIndex + 1; i < count; i++) { int pos = _lwResTableJFileOffset + i * 4; uint v = BitConverter.ToUInt32(resData, pos); BitConverter.GetBytes((uint)((int)v + sizeDiff)).CopyTo(resData, pos); }
                if (_lwResTableJCopyFileOffset >= 0 && _lwResTableJCopyFileOffset + count * 4 <= resData.Length)
                    for (int i = modifiedIndex + 1; i < count; i++) { int pos = _lwResTableJCopyFileOffset + i * 4; uint v = BitConverter.ToUInt32(resData, pos); BitConverter.GetBytes((uint)((int)v + sizeDiff)).CopyTo(resData, pos); }
                int sble = 0x00; int dsc = BitConverter.ToUInt16(resData, sble + 0x0A);
                for (int di = 0; di < dsc; di++) { int dsOff = (int)BitConverter.ToUInt32(resData, sble + 0x50 + di * 8); int absOff = sble + dsOff; if (absOff + 0x48 > resData.Length) continue; if (resData[absOff] != 0x54 || resData[absOff + 1] != 0x45) continue; int bc = BitConverter.ToUInt16(resData, absOff + 0x3C); for (int bi = 0; bi < bc; bi++) { int boff = absOff + 0x48 + bi * 24; if (boff + 24 > resData.Length) break; if (BitConverter.ToUInt32(resData, boff) == HASH_CHUNK_SIZE) { BitConverter.GetBytes(newChunkSize).CopyTo(resData, boff + 8); break; } } }
                App.AssetManager.ModifyRes(_lwResEntry.ResRid, resData);
                App.Logger.Log("LocalizedWave: patched RES (sizeDiff={0}, newChunkSize=0x{1:X})", sizeDiff, newChunkSize);
            }
            catch (Exception ex) { App.Logger.LogWarning("LocalizedWave RES patch failed: " + ex.Message); }
        }

        private byte[] LwConvertAudioFile(FrostyOpenFileDialog ofd)
        {
            MemoryStream ms = new MemoryStream();
            if (ofd.FileName.EndsWith(".wav", StringComparison.OrdinalIgnoreCase))
            { using (var r = new AudioFileReader(ofd.FileName)) { if (r.WaveFormat.Channels == 1) { var st = new MonoToStereoSampleProvider(r) { LeftVolume = 1.0f, RightVolume = 1.0f }; WaveFileWriter.WriteWavFileToStream(ms, new SampleToWaveProvider16(st)); } else WaveFileWriter.WriteWavFileToStream(ms, r); } return CreatePcm16BigSound(ms); }
            else if (ofd.FileName.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase))
            { using (var r = new MediaFoundationReader(ofd.FileName)) WaveFileWriter.WriteWavFileToStream(ms, r); return CreatePcm16BigSound(ms); }
            else if (ofd.FileName.EndsWith(".ealayer3")) return File.ReadAllBytes(ofd.FileName);
            else throw new FileFormatException();
        }

        // ═══════════════════════════════════════════════════════════
        //  vgmstream decode (shared utility)
        // ═══════════════════════════════════════════════════════════

        internal static short[] DecodeWithVgmstream(byte[] soundBuf, out int channels, out int sampleRate)
        {
            channels = 0; sampleRate = 0;
            string tempDir = Path.GetTempPath();
            string tempInput = Path.Combine(tempDir, "frosty_vgmstream_temp.sps");
            string tempOutput = Path.Combine(tempDir, "frosty_vgmstream_temp.wav");
            try
            {
                File.WriteAllBytes(tempInput, soundBuf);
                string assemblyDir = Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location);
                string vgmstreamPath = Path.Combine(assemblyDir, "..", "thirdparty", "vgmstream-cli.exe");
                if (!File.Exists(vgmstreamPath)) { App.Logger.LogWarning("vgmstream-cli.exe not found at: " + vgmstreamPath); return null; }
                if (File.Exists(tempOutput)) File.Delete(tempOutput);
                ProcessStartInfo psi = new ProcessStartInfo { FileName = vgmstreamPath, Arguments = string.Format("-o \"{0}\" \"{1}\"", tempOutput, tempInput), UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                using (Process proc = Process.Start(psi)) { proc.WaitForExit(30000); if (proc.ExitCode != 0) { App.Logger.LogWarning("vgmstream-cli.exe failed: exit code " + proc.ExitCode); return null; } }
                if (!File.Exists(tempOutput)) return null;
                using (var wavReader = new WaveFileReader(tempOutput))
                { channels = wavReader.WaveFormat.Channels; sampleRate = wavReader.WaveFormat.SampleRate; byte[] buffer = new byte[wavReader.Length]; int bytesRead = wavReader.Read(buffer, 0, buffer.Length); short[] samples = new short[bytesRead / 2]; Buffer.BlockCopy(buffer, 0, samples, 0, bytesRead); return samples; }
            }
            catch (Exception ex) { App.Logger.LogWarning("vgmstream decode failed: " + ex.Message); return null; }
            finally { try { if (File.Exists(tempInput)) File.Delete(tempInput); } catch { } try { if (File.Exists(tempOutput)) File.Delete(tempOutput); } catch { } }
        }
    }

     public class FrostyHarmonySampleBankEditor : FrostySoundDataEditor
    {
        public FrostyHarmonySampleBankEditor()
            : base(null)
        {
        }

        public FrostyHarmonySampleBankEditor(ILogger inLogger)
            : base(inLogger)
        {
        }

        protected override void ImportSound(FrostyOpenFileDialog ofd, FrostyTaskWindow task)
        {
            MemoryStream ms = new MemoryStream();
            byte[] resultBuf;
            if (ofd.FileName.EndsWith(".wav", StringComparison.OrdinalIgnoreCase))
            {
                using (var reader = new AudioFileReader(ofd.FileName))
                {
                    if (reader.WaveFormat.Channels == 1)
                    {
                        var stereo = new MonoToStereoSampleProvider(reader) { LeftVolume = 1.0f, RightVolume = 1.0f };
                        WaveFileWriter.WriteWavFileToStream(ms, new SampleToWaveProvider16(stereo));
                    }
                    else
                    {
                        WaveFileWriter.WriteWavFileToStream(ms, reader);
                    }
                }
                resultBuf = CreatePcm16BigSound(ms);
            }
            else if (ofd.FileName.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase))
            {
                using (var reader = new MediaFoundationReader(ofd.FileName))
                {
                    WaveFileWriter.WriteWavFileToStream(ms, reader);
                }
                resultBuf = CreatePcm16BigSound(ms);
            }
            else if (ofd.FileName.EndsWith(".ealayer3"))
            {
                resultBuf = File.ReadAllBytes(ofd.FileName);
            }
            else
            {
                throw new FileFormatException();
            }

            int selectedIndex = 0;
            Dispatcher?.Invoke(() => { selectedIndex = tracksListBox.SelectedIndex; });

            dynamic soundWave = RootObject;

            // Rebuild the track offset list using the same logic as InitialLoad
            dynamic ramChunk = soundWave.Chunks[soundWave.RamChunkIndex];
            ChunkAssetEntry ramChunkEntry = App.AssetManager.GetChunkEntry(ramChunk.ChunkId);

            NativeReader streamChunkReader = null;
            ChunkAssetEntry streamChunkEntry = null;
            if (soundWave.StreamChunkIndex != 255)
            {
                dynamic streamChunkObj = soundWave.Chunks[soundWave.StreamChunkIndex];
                streamChunkEntry = App.AssetManager.GetChunkEntry(streamChunkObj.ChunkId);
                streamChunkReader = new NativeReader(App.AssetManager.GetChunk(streamChunkEntry));
            }

            List<int> trackOffsets = new List<int>();
            List<bool> trackIsStreaming = new List<bool>();

            using (NativeReader ramReader = new NativeReader(App.AssetManager.GetChunk(ramChunkEntry)))
            {
                ramReader.Position = 0x0a;
                int datasetCount = ramReader.ReadUShort();
                ramReader.Position = 0x20;
                int dataOffset = ramReader.ReadInt();
                ramReader.Position = 0x50;

                List<int> tesdOffsets = new List<int>();
                for (int i = 0; i < datasetCount; i++)
                {
                    tesdOffsets.Add(ramReader.ReadInt());
                    ramReader.Position += 4;
                }

                foreach (int tesdOff in tesdOffsets)
                {
                    ramReader.Position = tesdOff + 0x3c;
                    int blockCount = ramReader.ReadUShort();
                    ramReader.Position += 0x0a;

                    int fileOffset = -1;
                    bool streaming = false;

                    for (int i = 0; i < blockCount; i++)
                    {
                        uint blockType = ramReader.ReadUInt();
                        if (blockType == 0x2e4f4646)
                        {
                            ramReader.Position += 4;
                            fileOffset = ramReader.ReadInt();
                            ramReader.Position += 0x0c;
                            streaming = true;
                        }
                        else if (blockType == 0x2e52414d)
                        {
                            ramReader.Position += 4;
                            fileOffset = ramReader.ReadInt() + dataOffset;
                            ramReader.Position += 0x0c;
                        }
                        else
                        {
                            ramReader.Position += 0x14;
                        }
                    }

                    if (fileOffset != -1 && !streaming)
                    {
                        trackOffsets.Add(fileOffset);
                        trackIsStreaming.Add(false);
                    }
                }

                // Scan stream chunk if present
                if (streamChunkReader != null)
                {
                    streamChunkReader.Position = 0;
                    byte[] streamData = streamChunkReader.ReadToEnd();

                    for (int pos = 0; pos <= streamData.Length - 16; pos++)
                    {
                        if (streamData[pos] == 0x48 && streamData[pos + 1] == 0x00 &&
                            streamData[pos + 2] == 0x00 && (streamData[pos + 3] == 0x0C || streamData[pos + 3] == 0x14))
                        {
                            int headerPayload = streamData[pos + 3];
                            byte possibleCodec = streamData[pos + 4];
                            if (possibleCodec == 0x12 || possibleCodec == 0x14 || possibleCodec == 0x15 ||
                                possibleCodec == 0x16 || possibleCodec == 0x19 || possibleCodec == 0x1c)
                            {
                                ushort sr = (ushort)((streamData[pos + 6] << 8) | streamData[pos + 7]);
                                if (sr != 8000 && sr != 11025 && sr != 12000 && sr != 16000 &&
                                    sr != 22050 && sr != 24000 && sr != 32000 && sr != 44100 && sr != 48000)
                                    continue;

                                int dataBlockPos = pos + headerPayload;
                                if (dataBlockPos >= streamData.Length || streamData[dataBlockPos] != 0x44)
                                    continue;

                                trackOffsets.Add(pos);
                                trackIsStreaming.Add(true);

                                for (int ep = dataBlockPos; ep <= streamData.Length - 4; ep++)
                                {
                                    if (streamData[ep] == 0x45 && streamData[ep + 1] == 0x00 &&
                                        streamData[ep + 2] == 0x00)
                                    {
                                        int endPayload = streamData[ep + 3];
                                        pos = ep + 4 + endPayload - 1;
                                        break;
                                    }
                                }
                            }
                        }
                    }
                }
            }

            if (selectedIndex < 0 || selectedIndex >= trackOffsets.Count)
            {
                App.Logger.LogWarning("Invalid track selection for import.");
                return;
            }

            bool isStreaming = trackIsStreaming[selectedIndex];
            int trackStart = trackOffsets[selectedIndex];

            // Determine which chunk to modify
            ChunkAssetEntry targetChunkEntry;
            dynamic targetSoundDataChunk;
            if (isStreaming)
            {
                targetChunkEntry = streamChunkEntry;
                targetSoundDataChunk = soundWave.Chunks[soundWave.StreamChunkIndex];
            }
            else
            {
                targetChunkEntry = ramChunkEntry;
                targetSoundDataChunk = soundWave.Chunks[soundWave.RamChunkIndex];
            }

            // Read the full chunk data
            byte[] chunkData;
            using (NativeReader chunkReader = new NativeReader(App.AssetManager.GetChunk(targetChunkEntry)))
            {
                chunkData = chunkReader.ReadToEnd();
            }

            // Find the end of the selected track (after its 0x45 end marker)
            int trackEnd = chunkData.Length;
            for (int ep = trackStart + 12; ep <= chunkData.Length - 4; ep++)
            {
                if (chunkData[ep] == 0x45 && chunkData[ep + 1] == 0x00 && chunkData[ep + 2] == 0x00)
                {
                    int endPayload = chunkData[ep + 3];
                    trackEnd = ep + 4 + endPayload;
                    break;
                }
            }

            // Splice: [before track] + [new audio] + [after track]
            byte[] newChunkData = new byte[trackStart + resultBuf.Length + (chunkData.Length - trackEnd)];
            Array.Copy(chunkData, 0, newChunkData, 0, trackStart);
            Array.Copy(resultBuf, 0, newChunkData, trackStart, resultBuf.Length);
            Array.Copy(chunkData, trackEnd, newChunkData, trackStart + resultBuf.Length, chunkData.Length - trackEnd);

            App.AssetManager.ModifyChunk(targetChunkEntry.Id, newChunkData);
            targetSoundDataChunk.ChunkSize = (uint)newChunkData.Length;

            audioPlayer.Dispose();
            audioPlayer = new AudioPlayer();

            List<SoundDataTrack> tracks = InitialLoad(task);

            Dispatcher?.Invoke(() =>
            {
                AssetModified = true;
                InvokeOnAssetModified();
                EbxAssetEntry assetEntry = AssetEntry as EbxAssetEntry;
                assetEntry.LinkAsset(targetChunkEntry);

                TracksList.Clear();
                foreach (var track in tracks)
                    TracksList.Add(track);
            });
        }

        private byte[] ConvertAudioFile(FrostyOpenFileDialog ofd)
        {
            MemoryStream ms = new MemoryStream();
            if (ofd.FileName.EndsWith(".wav", StringComparison.OrdinalIgnoreCase))
            {
                using (var reader = new AudioFileReader(ofd.FileName))
                {
                    if (reader.WaveFormat.Channels == 1)
                    {
                        var stereo = new MonoToStereoSampleProvider(reader) { LeftVolume = 1.0f, RightVolume = 1.0f };
                        WaveFileWriter.WriteWavFileToStream(ms, new SampleToWaveProvider16(stereo));
                    }
                    else
                        WaveFileWriter.WriteWavFileToStream(ms, reader);
                }
                return CreatePcm16BigSound(ms);
            }
            else if (ofd.FileName.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase))
            {
                using (var reader = new MediaFoundationReader(ofd.FileName))
                    WaveFileWriter.WriteWavFileToStream(ms, reader);
                return CreatePcm16BigSound(ms);
            }
            else if (ofd.FileName.EndsWith(".ealayer3"))
                return File.ReadAllBytes(ofd.FileName);
            else
                throw new FileFormatException();
        }

        private int FindTESDOffset(byte[] data)
        {
            for (int i = 0; i <= data.Length - 0x100; i++)
            {
                if (data[i] == 0x54 && data[i + 1] == 0x45 && data[i + 2] == 0x53 && data[i + 3] == 0x44)
                {
                    if (i + 0x4C <= data.Length && data[i + 0x48] == 0x46 && data[i + 0x49] == 0x46 &&
                        data[i + 0x4A] == 0x4F && data[i + 0x4B] == 0x2E)
                        return i;
                }
            }
            return -1;
        }

        private int GetSongCount(byte[] data, int toff)
        {
            if (data[toff + 0x4D] == 0x03)
                return (int)BitConverter.ToUInt32(data, toff + 0x50);
            return (int)BitConverter.ToUInt32(data, toff + 0x38);
        }

        private void GetTableLayout(byte[] data, int toff, int N, out int t1es, out int stp,
            out int t1, out int t2, out int t3, out int t4, out int t4e)
        {
            t1es = (data[toff + 0x4E] > 0x04) ? 2 : 1;
            stp = (int)BitConverter.ToUInt32(data, toff + 0x58);
            t1 = stp + N * 4;
            t2 = t1 + N * t1es;
            t3 = t2 + N * 2;
            t4 = t3 + N * 2;
            t4e = t4 + N;
        }

        private void ReloadAfterModify(FrostyTaskWindow task, List<ChunkAssetEntry> chunks)
        {
            audioPlayer.Dispose();
            audioPlayer = new AudioPlayer();
            List<SoundDataTrack> tracks = InitialLoad(task);
            Dispatcher?.Invoke(() =>
            {
                AssetModified = true;
                InvokeOnAssetModified();
                EbxAssetEntry assetEntry = AssetEntry as EbxAssetEntry;
                foreach (var ce in chunks) assetEntry.LinkAsset(ce);
                TracksList.Clear();
                foreach (var track in tracks) TracksList.Add(track);
            });
        }

        private byte[] RebuildRamChunk(byte[] ramData, int toff, int oldN, int newN,
            byte[] newSongOffsets, byte[] newTable1, byte[] newTable2, byte[] newTable3, byte[] newTable4,
            int t1es, int songTablePtr)
        {
            int newT1 = songTablePtr + newN * 4;
            int newT2 = newT1 + newN * t1es;
            int newT3 = newT2 + newN * 2;
            int newT4 = newT3 + newN * 2;
            int newEnd = (newT4 + newN + 15) & ~15;

            byte[] result = new byte[newEnd];
            Array.Copy(ramData, 0, result, 0, songTablePtr);
            Array.Copy(newSongOffsets, 0, result, songTablePtr, newSongOffsets.Length);
            Array.Copy(newTable1, 0, result, newT1, newTable1.Length);
            Array.Copy(newTable2, 0, result, newT2, newTable2.Length);
            Array.Copy(newTable3, 0, result, newT3, newTable3.Length);
            Array.Copy(newTable4, 0, result, newT4, newTable4.Length);

            // Patch SBle totalSize
            BitConverter.GetBytes((uint)newEnd).CopyTo(result, 4);
            // TESD payload is fixed-size, do NOT modify it
            // Patch field38
            int oldF38 = (int)BitConverter.ToUInt32(ramData, toff + 0x38);
            int f38ps = (oldN > 0) ? oldF38 / oldN : 2;
            BitConverter.GetBytes((uint)(newN * f38ps)).CopyTo(result, toff + 0x38);
            // Patch .OFF songCount
            if (ramData[toff + 0x4D] == 0x03)
                BitConverter.GetBytes((uint)newN).CopyTo(result, toff + 0x50);
            // Patch area songCount
            result[toff + 0xCC] = (byte)newN;
            // Patch Table2 ptr
            BitConverter.GetBytes((uint)newT2).CopyTo(result, toff + 0x88);
            // Patch Table3 ptr
            BitConverter.GetBytes((uint)newT3).CopyTo(result, toff + 0xA0);
            // Patch Table4 ptr
            BitConverter.GetBytes((uint)newT4).CopyTo(result, toff + 0xD8);

            return result;
        }

        private void PatchDebugChunk(dynamic soundWave, int newN, int newField38)
        {
            if (soundWave.DebugChunkIndex == 255 || soundWave.DebugChunkIndex >= soundWave.Chunks.Count)
                return;

            dynamic dbgChunkDyn = soundWave.Chunks[soundWave.DebugChunkIndex];
            ChunkAssetEntry dbgEntry = App.AssetManager.GetChunkEntry(dbgChunkDyn.ChunkId);
            if (dbgEntry == null) return;

            byte[] dbgData;
            using (var r = new NativeReader(App.AssetManager.GetChunk(dbgEntry)))
                dbgData = r.ReadToEnd();

            // Find TESD with .ZIS block in debug chunk
            int dt = -1;
            for (int i = 0; i <= dbgData.Length - 0x60; i++)
            {
                if (dbgData[i] == 0x54 && dbgData[i + 1] == 0x45 && dbgData[i + 2] == 0x53 && dbgData[i + 3] == 0x44)
                {
                    if (i + 0x4C <= dbgData.Length && dbgData[i + 0x48] == 0x5A && dbgData[i + 0x49] == 0x49 &&
                        dbgData[i + 0x4A] == 0x53 && dbgData[i + 0x4B] == 0x2E)
                    {
                        dt = i;
                        break;
                    }
                }
            }

            if (dt < 0) return;

            // Patch field38
            BitConverter.GetBytes((uint)newField38).CopyTo(dbgData, dt + 0x38);
            // Patch .ZIS songCount
            BitConverter.GetBytes((uint)newN).CopyTo(dbgData, dt + 0x50);

            App.AssetManager.ModifyChunk(dbgEntry.Id, dbgData);
            dbgChunkDyn.ChunkSize = (uint)dbgData.Length;
        }

        protected override void AddTrack(FrostyOpenFileDialog ofd, FrostyTaskWindow task)
        {
            byte[] newAudio = ConvertAudioFile(ofd);
            dynamic soundWave = RootObject;

            if (soundWave.StreamChunkIndex == 255)
            { App.Logger.LogWarning("Add Track only supported for streaming HarmonySampleBanks."); return; }

            dynamic ramDyn = soundWave.Chunks[soundWave.RamChunkIndex];
            ChunkAssetEntry ramEntry = App.AssetManager.GetChunkEntry(ramDyn.ChunkId);
            dynamic strmDyn = soundWave.Chunks[soundWave.StreamChunkIndex];
            ChunkAssetEntry strmEntry = App.AssetManager.GetChunkEntry(strmDyn.ChunkId);

            byte[] ramData, streamData;
            using (var r = new NativeReader(App.AssetManager.GetChunk(ramEntry))) ramData = r.ReadToEnd();
            using (var r = new NativeReader(App.AssetManager.GetChunk(strmEntry))) streamData = r.ReadToEnd();

            int toff = FindTESDOffset(ramData);
            if (toff < 0) { App.Logger.LogWarning("Could not find streaming TESD."); return; }

            int oldN = GetSongCount(ramData, toff);
            int t1es, stp, t1s, t2s, t3s, t4s, t4e;
            GetTableLayout(ramData, toff, oldN, out t1es, out stp, out t1s, out t2s, out t3s, out t4s, out t4e);

            // Read existing tables
            byte[] so = new byte[oldN * 4]; Array.Copy(ramData, stp, so, 0, so.Length);
            byte[] tb1 = new byte[oldN * t1es]; Array.Copy(ramData, t1s, tb1, 0, tb1.Length);
            byte[] tb2 = new byte[oldN * 2]; Array.Copy(ramData, t2s, tb2, 0, tb2.Length);
            byte[] tb3 = new byte[oldN * 2]; Array.Copy(ramData, t3s, tb3, 0, tb3.Length);
            byte[] tb4 = new byte[oldN]; Array.Copy(ramData, t4s, tb4, 0, tb4.Length);

            // Append audio to stream chunk
            int newOffset = streamData.Length;
            byte[] newStream = new byte[streamData.Length + newAudio.Length];
            Array.Copy(streamData, 0, newStream, 0, streamData.Length);
            Array.Copy(newAudio, 0, newStream, streamData.Length, newAudio.Length);

            int newN = oldN + 1;

            // Extend tables
            byte[] nso = new byte[newN * 4];
            Array.Copy(so, 0, nso, 0, so.Length);
            BitConverter.GetBytes((uint)newOffset).CopyTo(nso, oldN * 4);

            byte[] nt1 = new byte[newN * t1es];
            Array.Copy(tb1, 0, nt1, 0, tb1.Length);
            if (t1es == 1) nt1[oldN] = (byte)((oldN * 0x11) & 0xFF);
            else { nt1[oldN * 2] = (byte)oldN; nt1[oldN * 2 + 1] = (byte)oldN; }

            byte[] nt2 = new byte[newN * 2];
            Array.Copy(tb2, 0, nt2, 0, tb2.Length);
            if (oldN > 0) { nt2[oldN * 2] = tb2[(oldN - 1) * 2]; nt2[oldN * 2 + 1] = tb2[(oldN - 1) * 2 + 1]; }

            byte[] nt3 = new byte[newN * 2];
            Array.Copy(tb3, 0, nt3, 0, tb3.Length);
            if (oldN > 0) { nt3[oldN * 2] = tb3[(oldN - 1) * 2]; nt3[oldN * 2 + 1] = tb3[(oldN - 1) * 2 + 1]; }

            byte[] nt4 = new byte[newN];
            Array.Copy(tb4, 0, nt4, 0, tb4.Length);
            if (oldN > 0) nt4[oldN] = tb4[oldN - 1];

            byte[] newRam = RebuildRamChunk(ramData, toff, oldN, newN, nso, nt1, nt2, nt3, nt4, t1es, stp);

            App.AssetManager.ModifyChunk(ramEntry.Id, newRam);
            ramDyn.ChunkSize = (uint)newRam.Length;
            App.AssetManager.ModifyChunk(strmEntry.Id, newStream);
            strmDyn.ChunkSize = (uint)newStream.Length;

            int dbgToff = FindTESDOffset(newRam);
            if (dbgToff >= 0)
                PatchDebugChunk(soundWave, GetSongCount(newRam, dbgToff), (int)BitConverter.ToUInt32(newRam, dbgToff + 0x38));

            ReloadAfterModify(task, new List<ChunkAssetEntry> { ramEntry, strmEntry });
        }

        protected override void DeleteTrack(int trackIndex, FrostyTaskWindow task)
        {
            dynamic soundWave = RootObject;

            if (soundWave.StreamChunkIndex == 255)
            { App.Logger.LogWarning("Delete Track only supported for streaming HarmonySampleBanks."); return; }

            dynamic ramDyn = soundWave.Chunks[soundWave.RamChunkIndex];
            ChunkAssetEntry ramEntry = App.AssetManager.GetChunkEntry(ramDyn.ChunkId);
            dynamic strmDyn = soundWave.Chunks[soundWave.StreamChunkIndex];
            ChunkAssetEntry strmEntry = App.AssetManager.GetChunkEntry(strmDyn.ChunkId);

            byte[] ramData, streamData;
            using (var r = new NativeReader(App.AssetManager.GetChunk(ramEntry))) ramData = r.ReadToEnd();
            using (var r = new NativeReader(App.AssetManager.GetChunk(strmEntry))) streamData = r.ReadToEnd();

            int toff = FindTESDOffset(ramData);
            if (toff < 0) { App.Logger.LogWarning("Could not find streaming TESD."); return; }

            int oldN = GetSongCount(ramData, toff);
            if (trackIndex < 0 || trackIndex >= oldN) { App.Logger.LogWarning("Invalid track index."); return; }
            if (oldN <= 1) { App.Logger.LogWarning("Cannot delete the last remaining track."); return; }

            int t1es, stp, t1s, t2s, t3s, t4s, t4e;
            GetTableLayout(ramData, toff, oldN, out t1es, out stp, out t1s, out t2s, out t3s, out t4s, out t4e);

            // Read song offsets
            List<uint> offsets = new List<uint>();
            for (int i = 0; i < oldN; i++)
                offsets.Add(BitConverter.ToUInt32(ramData, stp + i * 4));

            // Calculate stream byte range to delete
            uint delStart = offsets[trackIndex];
            List<uint> sorted = new List<uint>(offsets);
            sorted.Sort();
            int si = sorted.IndexOf(delStart);
            uint delEnd = (si + 1 < sorted.Count) ? sorted[si + 1] : (uint)streamData.Length;
            int delSize = (int)(delEnd - delStart);

            // Remove audio from stream
            byte[] newStream = new byte[streamData.Length - delSize];
            Array.Copy(streamData, 0, newStream, 0, (int)delStart);
            if (delEnd < streamData.Length)
                Array.Copy(streamData, (int)delEnd, newStream, (int)delStart, streamData.Length - (int)delEnd);

            // Adjust offsets
            for (int i = 0; i < oldN; i++)
                if (offsets[i] > delStart) offsets[i] -= (uint)delSize;

            int newN = oldN - 1;

            // Remove entry from song offsets
            byte[] nso = new byte[newN * 4];
            int d = 0;
            for (int i = 0; i < oldN; i++)
            { if (i == trackIndex) continue; BitConverter.GetBytes(offsets[i]).CopyTo(nso, d * 4); d++; }

            // Remove from Table1 and re-sequence
            byte[] nt1 = new byte[newN * t1es];
            for (int i = 0; i < newN; i++)
            {
                if (t1es == 1) nt1[i] = (byte)((i * 0x11) & 0xFF);
                else { nt1[i * 2] = (byte)i; nt1[i * 2 + 1] = (byte)i; }
            }

            // Remove from Table2
            byte[] tb2 = new byte[oldN * 2]; Array.Copy(ramData, t2s, tb2, 0, tb2.Length);
            byte[] nt2 = new byte[newN * 2]; d = 0;
            for (int i = 0; i < oldN; i++)
            { if (i == trackIndex) continue; nt2[d * 2] = tb2[i * 2]; nt2[d * 2 + 1] = tb2[i * 2 + 1]; d++; }

            // Remove from Table3
            byte[] tb3 = new byte[oldN * 2]; Array.Copy(ramData, t3s, tb3, 0, tb3.Length);
            byte[] nt3 = new byte[newN * 2]; d = 0;
            for (int i = 0; i < oldN; i++)
            { if (i == trackIndex) continue; nt3[d * 2] = tb3[i * 2]; nt3[d * 2 + 1] = tb3[i * 2 + 1]; d++; }

            // Remove from Table4
            byte[] tb4 = new byte[oldN]; Array.Copy(ramData, t4s, tb4, 0, tb4.Length);
            byte[] nt4 = new byte[newN]; d = 0;
            for (int i = 0; i < oldN; i++)
            { if (i == trackIndex) continue; nt4[d] = tb4[i]; d++; }

            byte[] newRam = RebuildRamChunk(ramData, toff, oldN, newN, nso, nt1, nt2, nt3, nt4, t1es, stp);

            App.AssetManager.ModifyChunk(ramEntry.Id, newRam);
            ramDyn.ChunkSize = (uint)newRam.Length;
            App.AssetManager.ModifyChunk(strmEntry.Id, newStream);
            strmDyn.ChunkSize = (uint)newStream.Length;

            int dbgToff2 = FindTESDOffset(newRam);
            if (dbgToff2 >= 0)
                PatchDebugChunk(soundWave, GetSongCount(newRam, dbgToff2), (int)BitConverter.ToUInt32(newRam, dbgToff2 + 0x38));

            ReloadAfterModify(task, new List<ChunkAssetEntry> { ramEntry, strmEntry });
        }

        protected override void KeepFirstNTracks(int keepCount, FrostyTaskWindow task)
        {
            dynamic soundWave = RootObject;

            if (soundWave.StreamChunkIndex == 255)
            { App.Logger.LogWarning("Keep First N only supported for streaming HarmonySampleBanks."); return; }

            dynamic ramDyn = soundWave.Chunks[soundWave.RamChunkIndex];
            ChunkAssetEntry ramEntry = App.AssetManager.GetChunkEntry(ramDyn.ChunkId);
            dynamic strmDyn = soundWave.Chunks[soundWave.StreamChunkIndex];
            ChunkAssetEntry strmEntry = App.AssetManager.GetChunkEntry(strmDyn.ChunkId);

            byte[] ramData, streamData;
            using (var r = new NativeReader(App.AssetManager.GetChunk(ramEntry))) ramData = r.ReadToEnd();
            using (var r = new NativeReader(App.AssetManager.GetChunk(strmEntry))) streamData = r.ReadToEnd();

            int toff = FindTESDOffset(ramData);
            if (toff < 0) { App.Logger.LogWarning("Could not find streaming TESD."); return; }

            int oldN = GetSongCount(ramData, toff);
            if (keepCount >= oldN) { App.Logger.LogWarning("Nothing to delete, track count already <= " + keepCount); return; }
            if (keepCount < 1) { App.Logger.LogWarning("Must keep at least 1 track."); return; }

            int t1es, stp, t1s, t2s, t3s, t4s, t4e;
            GetTableLayout(ramData, toff, oldN, out t1es, out stp, out t1s, out t2s, out t3s, out t4s, out t4e);

            // Read song offsets
            List<uint> offsets = new List<uint>();
            for (int i = 0; i < oldN; i++)
                offsets.Add(BitConverter.ToUInt32(ramData, stp + i * 4));

            // Delete tracks from the end down to keepCount, one at a time in the binary data
            // (reverse order so offset adjustments stay valid)
            for (int i = oldN - 1; i >= keepCount; i--)
            {
                int N = offsets.Count;
                task.Update("Deleting track " + (oldN - i) + " of " + (oldN - keepCount));

                uint delStart = offsets[i];
                List<uint> sorted = new List<uint>(offsets);
                sorted.Sort();
                int si = sorted.IndexOf(delStart);
                uint delEnd = (si + 1 < sorted.Count) ? sorted[si + 1] : (uint)streamData.Length;
                int delSize = (int)(delEnd - delStart);

                // Remove audio from stream
                byte[] newStream = new byte[streamData.Length - delSize];
                Array.Copy(streamData, 0, newStream, 0, (int)delStart);
                if (delEnd < streamData.Length)
                    Array.Copy(streamData, (int)delEnd, newStream, (int)delStart, streamData.Length - (int)delEnd);
                streamData = newStream;

                // Adjust offsets for remaining tracks
                for (int j = 0; j < N; j++)
                    if (offsets[j] > delStart) offsets[j] -= (uint)delSize;

                offsets.RemoveAt(i);
            }

            int newN = keepCount;

            // Rebuild tables keeping only the first N entries
            byte[] nso = new byte[newN * 4];
            for (int i = 0; i < newN; i++)
                BitConverter.GetBytes(offsets[i]).CopyTo(nso, i * 4);

            byte[] nt1 = new byte[newN * t1es];
            for (int i = 0; i < newN; i++)
            {
                if (t1es == 1) nt1[i] = (byte)((i * 0x11) & 0xFF);
                else { nt1[i * 2] = (byte)i; nt1[i * 2 + 1] = (byte)i; }
            }

            byte[] tb2 = new byte[oldN * 2]; Array.Copy(ramData, t2s, tb2, 0, tb2.Length);
            byte[] nt2 = new byte[newN * 2]; Array.Copy(tb2, 0, nt2, 0, newN * 2);

            byte[] tb3 = new byte[oldN * 2]; Array.Copy(ramData, t3s, tb3, 0, tb3.Length);
            byte[] nt3 = new byte[newN * 2]; Array.Copy(tb3, 0, nt3, 0, newN * 2);

            byte[] tb4 = new byte[oldN]; Array.Copy(ramData, t4s, tb4, 0, tb4.Length);
            byte[] nt4 = new byte[newN]; Array.Copy(tb4, 0, nt4, 0, newN);

            byte[] newRam = RebuildRamChunk(ramData, toff, oldN, newN, nso, nt1, nt2, nt3, nt4, t1es, stp);

            App.AssetManager.ModifyChunk(ramEntry.Id, newRam);
            ramDyn.ChunkSize = (uint)newRam.Length;
            App.AssetManager.ModifyChunk(strmEntry.Id, streamData);
            strmDyn.ChunkSize = (uint)streamData.Length;

            int dbgToff = FindTESDOffset(newRam);
            if (dbgToff >= 0)
                PatchDebugChunk(soundWave, GetSongCount(newRam, dbgToff), (int)BitConverter.ToUInt32(newRam, dbgToff + 0x38));

            ReloadAfterModify(task, new List<ChunkAssetEntry> { ramEntry, strmEntry });
        }

        protected override void DeleteAllExcept(int keepIndex, FrostyTaskWindow task)
        {
            dynamic soundWave = RootObject;

            if (soundWave.StreamChunkIndex == 255)
            { App.Logger.LogWarning("Delete All Except only supported for streaming HarmonySampleBanks."); return; }

            dynamic ramDyn = soundWave.Chunks[soundWave.RamChunkIndex];
            ChunkAssetEntry ramEntry = App.AssetManager.GetChunkEntry(ramDyn.ChunkId);
            dynamic strmDyn = soundWave.Chunks[soundWave.StreamChunkIndex];
            ChunkAssetEntry strmEntry = App.AssetManager.GetChunkEntry(strmDyn.ChunkId);

            byte[] ramData, streamData;
            using (var r = new NativeReader(App.AssetManager.GetChunk(ramEntry))) ramData = r.ReadToEnd();
            using (var r = new NativeReader(App.AssetManager.GetChunk(strmEntry))) streamData = r.ReadToEnd();

            int toff = FindTESDOffset(ramData);
            if (toff < 0) { App.Logger.LogWarning("Could not find streaming TESD."); return; }

            int currentN = GetSongCount(ramData, toff);
            if (keepIndex < 0 || keepIndex >= currentN) { App.Logger.LogWarning("Invalid track index."); return; }

            // Delete tracks one at a time from the end, skipping the kept track
            // This mirrors the single-delete path which is proven to work in-game
            int currentKeep = keepIndex;
            for (int i = currentN - 1; i >= 0; i--)
            {
                if (i == currentKeep) continue;

                task.Update("Deleting track " + (i + 1) + " of " + currentN);

                int t1es, stp, t1s, t2s, t3s, t4s, t4e;
                int N = GetSongCount(ramData, toff);
                GetTableLayout(ramData, toff, N, out t1es, out stp, out t1s, out t2s, out t3s, out t4s, out t4e);

                // Read song offsets
                List<uint> offsets = new List<uint>();
                for (int j = 0; j < N; j++)
                    offsets.Add(BitConverter.ToUInt32(ramData, stp + j * 4));

                // Calculate stream byte range to delete
                uint delStart = offsets[i];
                List<uint> sorted = new List<uint>(offsets);
                sorted.Sort();
                int si = sorted.IndexOf(delStart);
                uint delEnd = (si + 1 < sorted.Count) ? sorted[si + 1] : (uint)streamData.Length;
                int delSize = (int)(delEnd - delStart);

                // Remove audio from stream
                byte[] newStream = new byte[streamData.Length - delSize];
                Array.Copy(streamData, 0, newStream, 0, (int)delStart);
                if (delEnd < streamData.Length)
                    Array.Copy(streamData, (int)delEnd, newStream, (int)delStart, streamData.Length - (int)delEnd);
                streamData = newStream;

                // Adjust offsets
                for (int j = 0; j < N; j++)
                    if (offsets[j] > delStart) offsets[j] -= (uint)delSize;

                int newN = N - 1;

                // Remove from song offsets
                byte[] nso = new byte[newN * 4];
                int d = 0;
                for (int j = 0; j < N; j++)
                { if (j == i) continue; BitConverter.GetBytes(offsets[j]).CopyTo(nso, d * 4); d++; }

                // Table1: re-sequence
                byte[] nt1 = new byte[newN * t1es];
                for (int j = 0; j < newN; j++)
                {
                    if (t1es == 1) nt1[j] = (byte)((j * 0x11) & 0xFF);
                    else { nt1[j * 2] = (byte)j; nt1[j * 2 + 1] = (byte)j; }
                }

                // Table2: remove entry
                byte[] tb2 = new byte[N * 2]; Array.Copy(ramData, t2s, tb2, 0, tb2.Length);
                byte[] nt2 = new byte[newN * 2]; d = 0;
                for (int j = 0; j < N; j++)
                { if (j == i) continue; nt2[d * 2] = tb2[j * 2]; nt2[d * 2 + 1] = tb2[j * 2 + 1]; d++; }

                // Table3: remove entry
                byte[] tb3 = new byte[N * 2]; Array.Copy(ramData, t3s, tb3, 0, tb3.Length);
                byte[] nt3 = new byte[newN * 2]; d = 0;
                for (int j = 0; j < N; j++)
                { if (j == i) continue; nt3[d * 2] = tb3[j * 2]; nt3[d * 2 + 1] = tb3[j * 2 + 1]; d++; }

                // Table4: remove entry
                byte[] tb4 = new byte[N]; Array.Copy(ramData, t4s, tb4, 0, tb4.Length);
                byte[] nt4 = new byte[newN]; d = 0;
                for (int j = 0; j < N; j++)
                { if (j == i) continue; nt4[d] = tb4[j]; d++; }

                ramData = RebuildRamChunk(ramData, toff, N, newN, nso, nt1, nt2, nt3, nt4, t1es, stp);

                // Commit each iteration to Frosty's asset manager
                App.AssetManager.ModifyChunk(ramEntry.Id, ramData);
                ramDyn.ChunkSize = (uint)ramData.Length;
                App.AssetManager.ModifyChunk(strmEntry.Id, streamData);
                strmDyn.ChunkSize = (uint)streamData.Length;

                int daToff = FindTESDOffset(ramData);
                if (daToff >= 0)
                    PatchDebugChunk(soundWave, GetSongCount(ramData, daToff), (int)BitConverter.ToUInt32(ramData, daToff + 0x38));

                // Adjust keepIndex if we deleted before it
                if (i < currentKeep) currentKeep--;
            }

            ReloadAfterModify(task, new List<ChunkAssetEntry> { ramEntry, strmEntry });
        }

        protected override List<SoundDataTrack> InitialLoad(FrostyTaskWindow task)
        {
            List<SoundDataTrack> retVal = new List<SoundDataTrack>();

            try
            {
            dynamic soundWave = RootObject;
            dynamic ramChunk = soundWave.Chunks[soundWave.RamChunkIndex];

            int index = 0;

            ChunkAssetEntry ramChunkEntry = App.AssetManager.GetChunkEntry(ramChunk.ChunkId);

            NativeReader streamChunkReader = null;
            if (soundWave.StreamChunkIndex != 255)
            {
                dynamic streamChunk = soundWave.Chunks[soundWave.StreamChunkIndex];
                ChunkAssetEntry streamChunkEntry = App.AssetManager.GetChunkEntry(streamChunk.ChunkId);
                streamChunkReader = new NativeReader(App.AssetManager.GetChunk(streamChunkEntry));
            }

            using (NativeReader reader = new NativeReader(App.AssetManager.GetChunk(ramChunkEntry)))
            {
                reader.Position = 0x0a;
                int datasetCount = reader.ReadUShort();

                reader.Position = 0x20;
                int dataOffset = reader.ReadInt();

                reader.Position = 0x50;
                List<int> offsets = new List<int>();
                for (int i = 0; i < datasetCount; i++)
                {
                    offsets.Add(reader.ReadInt());
                    reader.Position += 4;
                }

                // First pass: parse all TESD blocks to collect fileOffsets and streaming flags
                int dataEndOffset = offsets.Count > 0 ? offsets[0] : (int)reader.Length;
                List<int> streamOffsets = new List<int>();
                List<bool> streamIsStreaming = new List<bool>();

                foreach (int offset in offsets)
                {
                    reader.Position = offset + 0x3c;
                    int blockCount = reader.ReadUShort();
                    reader.Position += 0x0a;

                    int fileOffset = -1;
                    bool streaming = false;

                    for (int i = 0; i < blockCount; i++)
                    {
                        uint blockType = reader.ReadUInt();
                        if (blockType == 0x2e4f4646)
                        {
                            reader.Position += 4;
                            fileOffset = reader.ReadInt();
                            reader.Position += 0x0c;
                            streaming = true;
                        }
                        else if (blockType == 0x2e52414d)
                        {
                            reader.Position += 4;
                            fileOffset = reader.ReadInt() + dataOffset;
                            reader.Position += 0x0c;
                        }
                        else
                        {
                            reader.Position += 0x14;
                        }
                    }

                    if (fileOffset != -1)
                    {
                        streamOffsets.Add(fileOffset);
                        streamIsStreaming.Add(streaming);
                    }
                }

                // For streaming banks, the TESD offsets can be unreliable.
                // Scan the stream chunk directly for SNR audio blocks instead.
                if (streamChunkReader != null)
                {
                    // Remove unreliable streaming entries parsed from TESD
                    for (int i = streamOffsets.Count - 1; i >= 0; i--)
                    {
                        if (streamIsStreaming[i])
                        {
                            streamOffsets.RemoveAt(i);
                            streamIsStreaming.RemoveAt(i);
                        }
                    }

                    // Scan stream chunk for SNR header blocks
                    streamChunkReader.Position = 0;
                    byte[] streamData = streamChunkReader.ReadToEnd();

                    for (int pos = 0; pos <= streamData.Length - 16; pos++)
                    {
                        if (streamData[pos] == 0x48 && streamData[pos + 1] == 0x00 &&
                            streamData[pos + 2] == 0x00 && (streamData[pos + 3] == 0x0C || streamData[pos + 3] == 0x14))
                        {
                            int headerPayload = streamData[pos + 3];
                            byte possibleCodec = streamData[pos + 4];
                            if (possibleCodec == 0x12 || possibleCodec == 0x14 || possibleCodec == 0x15 ||
                                possibleCodec == 0x16 || possibleCodec == 0x19 || possibleCodec == 0x1c)
                            {
                                // Validate sample rate (big-endian at pos+6)
                                ushort sr = (ushort)((streamData[pos + 6] << 8) | streamData[pos + 7]);
                                if (sr != 8000 && sr != 11025 && sr != 12000 && sr != 16000 &&
                                    sr != 22050 && sr != 24000 && sr != 32000 && sr != 44100 && sr != 48000)
                                    continue;

                                // Validate first block after header is a data block (0x44)
                                int dataBlockPos = pos + headerPayload;
                                if (dataBlockPos >= streamData.Length || streamData[dataBlockPos] != 0x44)
                                    continue;

                                streamOffsets.Add(pos);
                                streamIsStreaming.Add(true);

                                // Skip past this stream's end marker (45 00 00 xx) to avoid re-detecting
                                for (int ep = dataBlockPos; ep <= streamData.Length - 4; ep++)
                                {
                                    if (streamData[ep] == 0x45 && streamData[ep + 1] == 0x00 &&
                                        streamData[ep + 2] == 0x00)
                                    {
                                        int endPayload = streamData[ep + 3];
                                        pos = ep + 4 + endPayload - 1;
                                        break;
                                    }
                                }
                            }
                        }
                    }
                }

                // Sort by offset
                // Simple parallel sort: build index array, sort by offset, reorder both lists
                int[] sortIdx = new int[streamOffsets.Count];
                for (int i = 0; i < sortIdx.Length; i++) sortIdx[i] = i;
                Array.Sort(sortIdx, (a, b) => streamOffsets[a].CompareTo(streamOffsets[b]));
                List<int> sortedOffsets = new List<int>();
                List<bool> sortedStreaming = new List<bool>();
                for (int i = 0; i < sortIdx.Length; i++)
                {
                    sortedOffsets.Add(streamOffsets[sortIdx[i]]);
                    sortedStreaming.Add(streamIsStreaming[sortIdx[i]]);
                }
                streamOffsets = sortedOffsets;
                streamIsStreaming = sortedStreaming;

                // Second pass: decode each audio stream with proper size bounds
                int totalStreams = streamOffsets.Count;
                for (int s = 0; s < totalStreams; s++)
                {
                    task.Update(status: "Loading Track #" + (s + 1) + " of " + totalStreams, progress: ((s + 1) / (double)totalStreams) * 100.0d);

                    int fileOffset = streamOffsets[s];
                    bool streaming = streamIsStreaming[s];

                    NativeReader actualReader = reader;
                    if (streaming)
                        actualReader = streamChunkReader;

                    // Calculate how much audio data to read for this stream
                    int nextBoundary;
                    if (streaming)
                    {
                        nextBoundary = (s + 1 < totalStreams && streamIsStreaming[s + 1])
                            ? streamOffsets[s + 1]
                            : (int)actualReader.Length;
                    }
                    else
                    {
                        nextBoundary = (s + 1 < totalStreams && !streamIsStreaming[s + 1])
                            ? streamOffsets[s + 1]
                            : dataEndOffset;
                    }
                    int readSize = nextBoundary - fileOffset;

                    SoundDataTrack track = new SoundDataTrack { Name = "Track #" + (index++) };

                    actualReader.Position = fileOffset;
                    List<short> decodedSoundBuf = new List<short>();

                    uint headerSize = actualReader.ReadUInt(Endian.Big) & 0x00ffffff;
                    byte codec = actualReader.ReadByte();
                    int channels = (actualReader.ReadByte() >> 2) + 1;
                    ushort sampleRate = actualReader.ReadUShort(Endian.Big);
                    uint sampleCount = actualReader.ReadUInt(Endian.Big) & 0x00ffffff;

                    switch (codec)
                    {
                        case 0x12: track.Codec = "Pcm16Big"; break;
                        case 0x14: track.Codec = "XAS"; break;
                        case 0x15: track.Codec = "EALayer3 v5"; break;
                        case 0x16: track.Codec = "EALayer3 v6"; break;
                        case 0x19: track.Codec = "EaSpeex"; break;
                        case 0x1c: track.Codec = "EaOpus"; break;
                        default: track.Codec = "Unknown (" + codec.ToString("x2") + ")"; break;
                    }

                    actualReader.Position = fileOffset;
                    byte[] soundBuf = actualReader.ReadBytes(readSize);
                    double duration = 0.0;

                    if (codec == 0x12)
                    {
                        short[] data = Pcm16b.Decode(soundBuf);
                        decodedSoundBuf.AddRange(data);
                        duration += (data.Length / channels) / (double)sampleRate;
                    }
                    else if (codec == 0x14)
                    {
                        short[] data = XAS.Decode(soundBuf);
                        decodedSoundBuf.AddRange(data);
                        duration += (data.Length / channels) / (double)sampleRate;
                    }
                    else if (codec == 0x15 || codec == 0x16)
                    {
                        bool decoded = false;
                        try
                        {
                            sampleCount = 0;
                            EALayer3.Decode(soundBuf, soundBuf.Length, (short[] data, int count, EALayer3.StreamInfo info) =>
                            {
                                if (info.streamIndex == -1)
                                    return;
                                sampleCount += (uint)data.Length;
                                decodedSoundBuf.AddRange(data);
                            });
                            duration += (sampleCount / channels) / (double)sampleRate;
                            decoded = decodedSoundBuf.Count > 0;
                        }
                        catch
                        {
                            decodedSoundBuf.Clear();
                        }

                        // Fallback to vgmstream if EALayer3 failed
                        if (!decoded)
                        {
                            int vgmChannels, vgmSampleRate;
                            short[] data = FrostyNewWaveEditor.DecodeWithVgmstream(soundBuf, out vgmChannels, out vgmSampleRate);
                            if (data != null && data.Length > 0)
                            {
                                decodedSoundBuf.AddRange(data);
                                channels = vgmChannels;
                                sampleRate = (ushort)vgmSampleRate;
                                duration += (data.Length / channels) / (double)sampleRate;
                            }
                        }
                    }
                    else if (codec == 0x19 || codec == 0x1c)
                    {
                        int vgmChannels, vgmSampleRate;
                        short[] data = FrostyNewWaveEditor.DecodeWithVgmstream(soundBuf, out vgmChannels, out vgmSampleRate);
                        if (data != null && data.Length > 0)
                        {
                            decodedSoundBuf.AddRange(data);
                            channels = vgmChannels;
                            sampleRate = (ushort)vgmSampleRate;
                            duration += (data.Length / channels) / (double)sampleRate;
                        }
                    }

                    track.SampleRate = sampleRate;
                    track.ChannelCount = channels;
                    track.Duration += duration;
                    track.Samples = decodedSoundBuf.ToArray();

                    // Waveform rendering deferred to batch loop below (avoids per-track allocations)
                    track.SegmentCount = 1;
                    retVal.Add(track);
                }

                // Render waveforms in batch — renderer + settings created once
                task.Update(status: "Rendering waveforms...", progress: 95.0d);
                var maxPeakProvider = new MaxPeakProvider();
                var topSpacerColor = System.Drawing.Color.FromArgb(64, 83, 22, 3);
                var waveSettings = new SoundCloudBlockWaveFormSettings(System.Drawing.Color.FromArgb(196, 197, 53, 0), topSpacerColor, System.Drawing.Color.FromArgb(196, 79, 26, 0),
                                                                                            System.Drawing.Color.FromArgb(64, 79, 79, 79))
                {
                    Name = "SoundCloud Orange Transparent Blocks",
                    PixelsPerPeak = 2,
                    SpacerPixels = 1,
                    TopSpacerGradientStartColor = topSpacerColor,
                    BackgroundColor = System.Drawing.Color.Transparent,
                    Width = 800,
                    TopHeight = 50,
                    BottomHeight = 30,
                };
                var waveRenderer = new WaveFormRenderer();

                for (int s = 0; s < retVal.Count; s++)
                {
                    SoundDataTrack track = retVal[s];
                    if (track.Samples == null || track.Samples.Length == 0)
                        continue;

                    try
                    {
                        var image = waveRenderer.Render(track.Samples, maxPeakProvider, waveSettings);

                        using (var ms = new MemoryStream())
                        {
                            image.Save(ms, ImageFormat.Png);
                            ms.Seek(0, SeekOrigin.Begin);

                            var bitmapImage = new BitmapImage();
                            bitmapImage.BeginInit();
                            bitmapImage.CacheOption = BitmapCacheOption.OnLoad;
                            bitmapImage.StreamSource = ms;
                            bitmapImage.EndInit();

                            var target = new RenderTargetBitmap(bitmapImage.PixelWidth, bitmapImage.PixelHeight, bitmapImage.DpiX, bitmapImage.DpiY, PixelFormats.Pbgra32);
                            var visual = new DrawingVisual();

                            using (var r = visual.RenderOpen())
                            {
                                r.DrawImage(bitmapImage, new Rect(0, 0, bitmapImage.Width, bitmapImage.Height));
                            }

                            target.Render(visual);
                            target.Freeze();
                            track.WaveForm = target;
                        }
                    }
                    catch (Exception)
                    {
                    }
                }
            }

            }
            catch (Exception ex)
            {
                App.Logger.LogError("HarmonySampleBank load failed: " + ex.ToString());
            }

            return retVal;
        }
    }

    public class FrostyOctaneSoundEditor : FrostySoundDataEditor
    {
        public FrostyOctaneSoundEditor()
            : base(null)
        {
        }

        public FrostyOctaneSoundEditor(ILogger inLogger)
            : base(inLogger)
        {
        }
    }

    public class FrostyImpulseResponseEditor : FrostySoundDataEditor
    {
        public FrostyImpulseResponseEditor()
            : base(null)
        {
        }

        public FrostyImpulseResponseEditor(ILogger inLogger)
            : base(inLogger)
        {
        }
    }
}