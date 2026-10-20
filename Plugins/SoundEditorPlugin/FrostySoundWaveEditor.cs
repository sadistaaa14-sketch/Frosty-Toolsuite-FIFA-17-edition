using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using FrostySdk.Interfaces;
using System.Windows;
using System.Windows.Controls;
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

        /// <summary>
        /// A `SoundWaveAsset` states its audio as a runtime-variation list over a chunk list, which is the one
        /// shape this plugin can grow: a new chunk, one segment and one variation, all appended. The base used
        /// to answer false for every asset and leave the command disabled with "not supported for this asset
        /// type", so the one asset type that can take a clip was the one told it could not.
        /// </summary>
        protected override bool CanAddClip
        {
            get
            {
                dynamic soundWave;
                return TryReadVariationShape(out soundWave);
            }
        }

        protected override string AddClipRefusal
        {
            get { return "This sound wave does not state its audio as runtime variations, so no clip can be added to it."; }
        }

        /// <summary>
        /// Lists one track per runtime variation from its segments' clip headers - codec, rate, channels and length - and
        /// decodes none of them: a variation is decoded when it is selected, played or exported. Every codec goes
        /// through the plugin's one decode route (<see cref="FrostyNewWaveEditor.DecodeClipAudio"/>), so EA Speex and
        /// EA Opus segments decode here too; this loader used to refuse them ("codec 0x1c is not supported by this
        /// editor") and list the variation without audio.
        /// </summary>
        protected override List<SoundDataTrack> InitialLoad(FrostyTaskWindow task)
        {
            List<SoundDataTrack> retVal = new List<SoundDataTrack>();
            dynamic soundWave = RootObject;

            int index = 0;
            int totalCount = soundWave.RuntimeVariations.Count;
            Dictionary<Guid, byte[]> headersOf = new Dictionary<Guid, byte[]>();

            foreach (dynamic runtimeVariation in soundWave.RuntimeVariations)
            {
                if (index % 50 == 0)
                    task.Update(status: "Reading track " + (index + 1) + " of " + totalCount, progress: ((index + 1) / (double)totalCount) * 100.0d);

                index++;
                SoundDataTrack track = new SoundDataTrack { Index = index, Name = "Track #" + index };

                dynamic soundDataChunk = soundWave.Chunks[runtimeVariation.ChunkIndex];
                Guid chunkId = soundDataChunk.ChunkId;
                ChunkAssetEntry chunkEntry = App.AssetManager.GetChunkEntry(chunkId);

                if (chunkEntry == null)
                {
                    App.Logger.LogWarning($"SoundChunk {chunkId} doesn't exist. This could be because its a LocalizedChunk that is not loaded by your game.");
                    track.DecodeFailure = "its chunk is not in the loaded game data";
                    retVal.Add(track);
                    continue;
                }

                // The segments' byte ranges in the chunk: each runs to the next segment's start, or to the chunk's end.
                int first = (int)runtimeVariation.FirstSegmentIndex;
                int count = (int)runtimeVariation.SegmentCount;
                int loopFirst = (int)runtimeVariation.FirstLoopSegmentIndex;
                int loopLast = (int)runtimeVariation.LastLoopSegmentIndex;
                int segmentTotal = (int)soundWave.Segments.Count;
                List<long> starts = new List<long>();
                List<long> nexts = new List<long>();
                for (int i = 0; i < count; i++)
                {
                    long at = (long)soundWave.Segments[first + i].SamplesOffset;
                    long next = -1;
                    if (first + i + 1 < segmentTotal && (long)soundWave.Segments[first + i + 1].SamplesOffset > at)
                        next = (long)soundWave.Segments[first + i + 1].SamplesOffset;
                    starts.Add(at);
                    nexts.Add(next);
                }

                // Lengths from the segments' own headers; the chunk is read once for all its variations.
                byte[] data;
                if (!headersOf.TryGetValue(chunkId, out data))
                {
                    using (NativeReader reader = new NativeReader(App.AssetManager.GetChunk(chunkEntry)))
                        data = reader.ReadToEnd();
                    headersOf[chunkId] = data;
                }

                double seconds = 0.0;
                for (int i = 0; i < starts.Count; i++)
                {
                    long frames;
                    int rate, channels;
                    if (SoundDurationScale.TryHeader(data, starts[i], out frames, out rate, out channels))
                    {
                        seconds += frames / (double)rate;
                        track.SampleRate = rate;
                        track.ChannelCount = channels;
                        if (track.Codec == null)
                            track.Codec = FrostyNewWaveEditor.CodecName(data[starts[i] + 4]);
                    }
                }

                track.Duration = seconds;
                track.SegmentCount = count;

                ChunkAssetEntry entry = chunkEntry;
                track.Decoder = t => DecodeVariation(t, entry, starts, nexts, loopFirst, loopLast);
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

        /// <summary>
        /// Decodes one variation: each segment in turn, joined, with the loop region (segments FirstLoopSegmentIndex to
        /// LastLoopSegmentIndex) in samples. Returns null on success or why it failed. Runs on a background thread.
        /// </summary>
        private static string DecodeVariation(SoundDataTrack track, ChunkAssetEntry entry, List<long> starts, List<long> nexts,
            int loopFirst, int loopLast)
        {
            byte[] data;
            using (NativeReader reader = new NativeReader(App.AssetManager.GetChunk(entry)))
                data = reader.ReadToEnd();

            List<short> all = new List<short>();
            int channels = 1, rate = 48000;
            uint loopStart = 0, loopLength = 0;
            string failure = null;

            for (int i = 0; i < starts.Count; i++)
            {
                long at = starts[i];
                long end = nexts[i] > at ? nexts[i] : data.Length;
                if (at < 0 || end > data.Length || end <= at)
                {
                    failure = "segment " + (i + 1) + " states a byte range outside its chunk";
                    continue;
                }

                byte[] segment = new byte[end - at];
                Buffer.BlockCopy(data, (int)at, segment, 0, segment.Length);

                int segmentChannels, segmentRate;
                string reason;
                short[] decoded = FrostyNewWaveEditor.DecodeClipAudio(segment, out segmentChannels, out segmentRate, out reason);
                if (decoded == null || decoded.Length == 0)
                {
                    failure = "segment " + (i + 1) + ": " + (reason ?? "no decoder produced audio");
                    continue;
                }

                channels = segmentChannels;
                rate = segmentRate;
                if (starts.Count > 1)
                {
                    if (i < loopFirst)
                        loopStart += (uint)decoded.Length;
                    if (i >= loopFirst && i <= loopLast)
                        loopLength += (uint)decoded.Length;
                }

                all.AddRange(decoded);
            }

            if (all.Count == 0)
                return failure ?? "no decoder produced audio";

            short[] samples = all.ToArray();
            track.ChannelCount = channels;
            track.SampleRate = rate;
            track.LoopStart = loopStart;
            track.LoopEnd = loopLength > 0 ? loopStart + loopLength : 0;
            track.Duration = SoundEdits.DurationSeconds(samples, channels, rate);
            track.Samples = samples;
            return null;
        }
    }

     public class FrostyHarmonySampleBankEditor : FrostySoundDataEditor
    {
        public FrostyHarmonySampleBankEditor()
            : base(null)
        {
        }

        /// <summary>
        /// This format can take a new clip: its index lives inside the chunk the editor already rewrites
        /// (<see cref="AddTrack"/> → `ModifyChunk`), which is the one shape in this plugin where a row can be
        /// added today. Every other editor leaves the button disabled and says why instead.
        /// </summary>
        protected override bool CanAddClip
        {
            get { return true; }
        }

        public FrostyHarmonySampleBankEditor(ILogger inLogger)
            : base(inLogger)
        {
        }

        protected override bool ImportFileInto(int index, string fileName, FrostyTaskWindow task)
        {
            return ReplaceHarmonyClip(index, ConvertAudioFileToPcm16Big(fileName), task);
        }

        /// <summary>
        /// Writes one clip into the Harmony bank's audio and answers whether it reached the file.
        ///
        /// The import command and the sample edits (normalize, gain, fades, trim, undo) write exactly the same
        /// thing - one clip's encoded bytes in place of the bytes that were there - so they share this method
        /// rather than one of them refusing what the other does. The clip list is rebuilt from the same tables
        /// `InitialLoad` reads, so a track's index means the same row in both.
        /// </summary>
        private bool ReplaceHarmonyClip(int selectedIndex, byte[] resultBuf, FrostyTaskWindow task)
        {
            if (resultBuf == null || resultBuf.Length == 0)
            {
                App.Logger.LogError("The new clip carries no audio, so nothing was written.");
                return false;
            }

            dynamic soundWave = RootObject;

            // The same samples, in the same order, as the list shows (DiscoverTracks): an index means one sample in both.
            ChunkAssetEntry ramChunkEntry = App.AssetManager.GetChunkEntry(soundWave.Chunks[soundWave.RamChunkIndex].ChunkId);
            ChunkAssetEntry streamChunkEntry = null;
            byte[] ramBytes, streamBytes = null;
            using (NativeReader ramReader = new NativeReader(App.AssetManager.GetChunk(ramChunkEntry)))
                ramBytes = ramReader.ReadToEnd();
            if (soundWave.StreamChunkIndex != 255)
            {
                dynamic streamChunkObj = soundWave.Chunks[soundWave.StreamChunkIndex];
                streamChunkEntry = App.AssetManager.GetChunkEntry(streamChunkObj.ChunkId);
                if (streamChunkEntry != null)
                {
                    using (NativeReader streamReader = new NativeReader(App.AssetManager.GetChunk(streamChunkEntry)))
                        streamBytes = streamReader.ReadToEnd();
                }
            }

            List<int> trackOffsets;
            List<bool> trackIsStreaming;
            int ramDataEnd;
            DiscoverTracks(ramBytes, streamBytes, out trackOffsets, out trackIsStreaming, out ramDataEnd);

            if (selectedIndex < 0 || selectedIndex >= trackOffsets.Count)
            {
                App.Logger.LogError("There is no clip #{0} in this Harmony bank (it lists {1}), so nothing was written.",
                    selectedIndex + 1, trackOffsets.Count);
                return false;
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

            byte[] chunkData = isStreaming ? streamBytes : ramBytes;

            // The sample ends where its own blocks end (StreamEnd); what follows up to the next sample is the padding that
            // keeps the next one on its boundary (EATrax streams start every song on a 32-byte boundary). Searching the
            // bytes for an end marker, as this used to, stopped inside the audio of every EATrax_01 song.
            int nextStart = chunkData.Length;
            for (int i = 0; i < trackOffsets.Count; i++)
            {
                if (trackIsStreaming[i] == isStreaming && trackOffsets[i] > trackStart && trackOffsets[i] < nextStart)
                    nextStart = trackOffsets[i];
            }

            int trackEnd = FrostyHarmonySampleBankEditor.StreamEnd(chunkData, trackStart);
            if (trackEnd < 0 || trackEnd > nextStart)
                trackEnd = nextStart;

            bool hasNext = nextStart < chunkData.Length;
            int newLength = resultBuf.Length;
            int padded = hasNext ? ((trackStart + newLength + 31) & ~31) - trackStart : newLength;
            int delta = (trackStart + padded) - (hasNext ? nextStart : trackEnd);

            // A streamed sample's later neighbours move by delta, so the RAM chunk's `.OFF` palette, which states every
            // streamed sample's offset, moves with them. Only the palette form (class 3, the one the add and delete
            // commands also rewrite) is rewritten; anything else is refused before anything is written.
            byte[] newRam = null;
            if (isStreaming && hasNext && delta != 0)
            {
                int toff = FindTESDOffset(ramBytes);
                if (toff < 0 || ramBytes[toff + 0x4D] != 0x03 || ramBytes[toff + 0x4F] != 4)
                {
                    App.Logger.LogError("This bank does not state its stream offsets as the 4-byte palette this editor rewrites, so a "
                        + "replace that changes the sample's length cannot be written; nothing was changed.");
                    return false;
                }

                int entries = (int)BitConverter.ToUInt32(ramBytes, toff + 0x50);
                int table = (int)BitConverter.ToUInt32(ramBytes, toff + 0x58);
                newRam = (byte[])ramBytes.Clone();
                for (int i = 0; i < entries; i++)
                {
                    uint value = BitConverter.ToUInt32(newRam, table + 4 * i);
                    if (value > trackStart)
                        BitConverter.GetBytes((uint)(value + delta)).CopyTo(newRam, table + 4 * i);
                }
            }

            // Splice: [before the sample] + [new audio, padded to the boundary] + [from the next sample on]
            int tailFrom = hasNext ? nextStart : trackEnd;
            byte[] newChunkData = new byte[trackStart + padded + (chunkData.Length - tailFrom)];
            Array.Copy(chunkData, 0, newChunkData, 0, trackStart);
            Array.Copy(resultBuf, 0, newChunkData, trackStart, resultBuf.Length);
            Array.Copy(chunkData, tailFrom, newChunkData, trackStart + padded, chunkData.Length - tailFrom);

            if (!WriteChunk(targetChunkEntry, newChunkData, "clip's chunk"))
                return false;

            if (newRam != null)
            {
                if (!WriteChunk(ramChunkEntry, newRam, "ram chunk"))
                {
                    WriteChunk(targetChunkEntry, chunkData, "clip's chunk (putting its old bytes back)");
                    return false;
                }

                soundWave.Chunks[soundWave.RamChunkIndex].ChunkSize = (uint)newRam.Length;
                App.Logger.Log("Moved the stream offsets after the replaced sample by {0:N0} bytes in the RAM chunk.", delta);
            }

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
                if (newRam != null)
                    assetEntry.LinkAsset(ramChunkEntry);

                SetTrackList(tracks);
                RefreshTrackListChrome();
            });

            return true;
        }

        /// <summary>
        /// A Harmony bank's audio is not a runtime-variation list - it is one RAM chunk and (for a streamed
        /// bank) one stream chunk, with the clip boundaries stated in TESD tables - so the base class's rule
        /// does not apply and used to answer here with "editing is not supported for this asset type" while the
        /// log above it said the edit was done. The bytes go where an import puts them: over the clip's own
        /// byte range, moving every clip after it.
        /// </summary>
        protected override bool CommitTrackSamples(SoundDataTrack track, int trackIndex, short[] samples, FrostyTaskWindow task)
        {
            // The track is handed in: this runs on the task window's thread, where the track list (a dependency
            // property) cannot be read. ReplaceHarmonyClip checks the index against the bank's own tables.
            if (track == null)
            {
                App.Logger.LogError("There is no clip #{0} in this Harmony bank, so nothing was written.", trackIndex + 1);
                return false;
            }

            byte[] clip = EncodeTrack(track, samples);
            return ReplaceHarmonyClip(trackIndex, clip, task);
        }

        private byte[] ConvertAudioFile(FrostyOpenFileDialog ofd)
        {
            return ConvertAudioFileToPcm16Big(ofd);
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
                SetTrackList(tracks);
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

            if (!WriteChunk(dbgEntry, dbgData, "debug chunk"))
                return;

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

            // Append audio to stream chunk, on the 32-byte boundary every shipped song starts on (EATrax_01: 8 of 8).
            int newOffset = (streamData.Length + 31) & ~31;
            byte[] newStream = new byte[newOffset + newAudio.Length];
            Array.Copy(streamData, 0, newStream, 0, streamData.Length);
            Array.Copy(newAudio, 0, newStream, newOffset, newAudio.Length);

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

            if (!WriteChunk(ramEntry, newRam, "ram chunk") || !WriteChunk(strmEntry, newStream, "stream chunk"))
                return;

            ramDyn.ChunkSize = (uint)newRam.Length;
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

            if (!WriteChunk(ramEntry, newRam, "ram chunk") || !WriteChunk(strmEntry, newStream, "stream chunk"))
                return;

            ramDyn.ChunkSize = (uint)newRam.Length;
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

            if (!WriteChunk(ramEntry, newRam, "ram chunk") || !WriteChunk(strmEntry, streamData, "stream chunk"))
                return;

            ramDyn.ChunkSize = (uint)newRam.Length;
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
                if (!WriteChunk(ramEntry, ramData, "ram chunk") || !WriteChunk(strmEntry, streamData, "stream chunk"))
                    return;

                ramDyn.ChunkSize = (uint)ramData.Length;
                strmDyn.ChunkSize = (uint)streamData.Length;

                int daToff = FindTESDOffset(ramData);
                if (daToff >= 0)
                    PatchDebugChunk(soundWave, GetSongCount(ramData, daToff), (int)BitConverter.ToUInt32(ramData, daToff + 0x38));

                // Adjust keepIndex if we deleted before it
                if (i < currentKeep) currentKeep--;
            }

            ReloadAfterModify(task, new List<ChunkAssetEntry> { ramEntry, strmEntry });
        }

        /// <summary>
        /// Where the bank's samples start, in the order the list shows them and every command addresses them: the RAM
        /// chunk's TESD records first (a record whose `.OFF` field marks a streamed sample is dropped when the bank has
        /// a stream chunk), then every SNR header the stream chunk holds, all sorted by offset. One method, so the list
        /// and the commands that rewrite a sample can never disagree on which sample an index is. For EATrax_01 the
        /// stream scan finds exactly the RAM chunk's 8 `.OFF` offsets (measured 2026-10-19).
        /// </summary>
        private static void DiscoverTracks(byte[] ramData, byte[] streamData, out List<int> offsets, out List<bool> streaming,
            out int dataEndOffset)
        {
            offsets = new List<int>();
            streaming = new List<bool>();

            int datasetCount = BitConverter.ToUInt16(ramData, 0x0a);
            int dataOffset = BitConverter.ToInt32(ramData, 0x20);

            List<int> tesd = new List<int>();
            for (int i = 0; i < datasetCount; i++)
                tesd.Add(BitConverter.ToInt32(ramData, 0x50 + 8 * i));

            dataEndOffset = tesd.Count > 0 ? tesd[0] : ramData.Length;

            foreach (int offset in tesd)
            {
                int at = offset + 0x3c;
                int blockCount = BitConverter.ToUInt16(ramData, at);
                at += 2 + 0x0a;

                int fileOffset = -1;
                bool isStream = false;
                for (int i = 0; i < blockCount; i++)
                {
                    uint blockType = BitConverter.ToUInt32(ramData, at);
                    if (blockType == 0x2e4f4646)
                    {
                        fileOffset = BitConverter.ToInt32(ramData, at + 8);
                        isStream = true;
                    }
                    else if (blockType == 0x2e52414d)
                    {
                        fileOffset = BitConverter.ToInt32(ramData, at + 8) + dataOffset;
                    }

                    at += 24;
                }

                if (fileOffset != -1)
                {
                    offsets.Add(fileOffset);
                    streaming.Add(isStream);
                }
            }

            if (streamData != null)
            {
                // For streaming banks the TESD records' values are not the offsets (a palette states its entry count
                // there), so the stream chunk is scanned for its SNR headers instead.
                for (int i = offsets.Count - 1; i >= 0; i--)
                {
                    if (streaming[i])
                    {
                        offsets.RemoveAt(i);
                        streaming.RemoveAt(i);
                    }
                }

                for (int pos = 0; pos <= streamData.Length - 16; pos++)
                {
                    if (streamData[pos] != 0x48 || streamData[pos + 1] != 0x00 || streamData[pos + 2] != 0x00
                        || (streamData[pos + 3] != 0x0C && streamData[pos + 3] != 0x14))
                        continue;

                    int headerPayload = streamData[pos + 3];
                    byte possibleCodec = streamData[pos + 4];
                    if (possibleCodec != 0x12 && possibleCodec != 0x14 && possibleCodec != 0x15 &&
                        possibleCodec != 0x16 && possibleCodec != 0x19 && possibleCodec != 0x1c)
                        continue;

                    ushort sr = (ushort)((streamData[pos + 6] << 8) | streamData[pos + 7]);
                    if (sr != 8000 && sr != 11025 && sr != 12000 && sr != 16000 &&
                        sr != 22050 && sr != 24000 && sr != 32000 && sr != 44100 && sr != 48000)
                        continue;

                    int dataBlockPos = pos + headerPayload;
                    if (dataBlockPos >= streamData.Length || streamData[dataBlockPos] != 0x44)
                        continue;

                    offsets.Add(pos);
                    streaming.Add(true);

                    // Skip the stream by walking its blocks to its end block. Searching the bytes for `45 00 00` (what
                    // this used to do) stops inside the audio: in EATrax_01 it does in all 8 songs, the first at 921,545
                    // where the song ends at 7,138,804.
                    int end = StreamEnd(streamData, pos);
                    if (end > pos)
                        pos = end - 1;
                }
            }

            int[] order = new int[offsets.Count];
            for (int i = 0; i < order.Length; i++)
                order[i] = i;
            List<int> unsorted = offsets;
            Array.Sort(order, (a, b) => unsorted[a].CompareTo(unsorted[b]));
            List<int> sortedOffsets = new List<int>();
            List<bool> sortedStreaming = new List<bool>();
            foreach (int i in order)
            {
                sortedOffsets.Add(offsets[i]);
                sortedStreaming.Add(streaming[i]);
            }

            offsets = sortedOffsets;
            streaming = sortedStreaming;
        }

        /// <summary>
        /// Where the SPS stream starting at <paramref name="start"/> ends: its blocks are walked by their sizes (one byte
        /// of type, three of big-endian size) to the end block (`E`, 0x45) and past it. -1 when a block's size runs off
        /// the data or is zero. For EATrax_01 this gives exactly the debug chunk's `SIZ.` size of all 8 songs.
        /// </summary>
        internal static int StreamEnd(byte[] data, int start)
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

        /// <summary>The RAM, debug and stream chunks as last read, for decoding a sample when it is asked for.</summary>
        private byte[] harmonyRam, harmonyStream;

        /// <summary>
        /// Lists the bank's samples from their headers and its own tables, and decodes none of them: a sample is decoded
        /// when it is selected, played or exported. An EA Trax bank's songs are minutes of EA Layer 3 each, and decoding
        /// all of them (and drawing their waveforms) to open the tab is what used to take minutes.
        ///
        /// A sample is named by what its bank states (<see cref="HarmonyBankNames"/>): an EA Trax song by the game DB's
        /// `artist - songname` for the songid it is filed under, else by its source file's name in the debug chunk.
        /// </summary>
        protected override List<SoundDataTrack> InitialLoad(FrostyTaskWindow task)
        {
            List<SoundDataTrack> retVal = new List<SoundDataTrack>();

            try
            {
                dynamic soundWave = RootObject;
                dynamic ramChunk = soundWave.Chunks[soundWave.RamChunkIndex];
                ChunkAssetEntry ramChunkEntry = App.AssetManager.GetChunkEntry(ramChunk.ChunkId);

                task.Update(status: "Reading the bank's chunks...");
                byte[] ram, stream = null, debug = null;
                using (NativeReader reader = new NativeReader(App.AssetManager.GetChunk(ramChunkEntry)))
                    ram = reader.ReadToEnd();

                if (soundWave.StreamChunkIndex != 255)
                {
                    dynamic streamChunk = soundWave.Chunks[soundWave.StreamChunkIndex];
                    ChunkAssetEntry streamChunkEntry = App.AssetManager.GetChunkEntry(streamChunk.ChunkId);
                    if (streamChunkEntry != null)
                    {
                        using (NativeReader reader = new NativeReader(App.AssetManager.GetChunk(streamChunkEntry)))
                            stream = reader.ReadToEnd();
                    }
                }

                try
                {
                    if (soundWave.DebugChunkIndex != 255 && soundWave.DebugChunkIndex < soundWave.Chunks.Count)
                    {
                        dynamic debugChunk = soundWave.Chunks[soundWave.DebugChunkIndex];
                        ChunkAssetEntry debugEntry = App.AssetManager.GetChunkEntry(debugChunk.ChunkId);
                        if (debugEntry != null)
                        {
                            using (NativeReader reader = new NativeReader(App.AssetManager.GetChunk(debugEntry)))
                                debug = reader.ReadToEnd();
                        }
                    }
                }
                catch (Exception)
                {
                    debug = null;
                }

                harmonyRam = ram;
                harmonyStream = stream;

                task.Update(status: "Finding the bank's samples...");
                List<int> offsets;
                List<bool> streaming;
                int dataEndOffset;
                DiscoverTracks(ram, stream, out offsets, out streaming, out dataEndOffset);

                task.Update(status: "Reading the game database for names...");
                FifaNameLookup.Preload();
                Dictionary<long, HarmonyBankNames.Sample> samples = HarmonyBankNames.ByStreamOffset(ram, debug);

                for (int s = 0; s < offsets.Count; s++)
                {
                    int fileOffset = offsets[s];
                    bool isStream = streaming[s];
                    byte[] source = isStream ? stream : ram;

                    int nextBoundary;
                    if (isStream)
                        nextBoundary = (s + 1 < offsets.Count && streaming[s + 1]) ? offsets[s + 1] : source.Length;
                    else
                        nextBoundary = (s + 1 < offsets.Count && !streaming[s + 1]) ? offsets[s + 1] : dataEndOffset;
                    int readSize = Math.Max(0, Math.Min(nextBoundary, source.Length) - fileOffset);

                    SoundDataTrack track = new SoundDataTrack { Index = s + 1, Name = "Track " + (s + 1), SegmentCount = 1 };
                    if (fileOffset >= 0 && fileOffset + 8 <= source.Length)
                    {
                        track.Codec = FrostyNewWaveEditor.CodecName(source[fileOffset + 4]);
                        long frames;
                        int rate, channels;
                        if (SoundDurationScale.TryHeader(source, fileOffset, out frames, out rate, out channels))
                        {
                            track.SampleRate = rate;
                            track.ChannelCount = channels;
                            track.Duration = frames / (double)rate;
                        }
                    }

                    HarmonyBankNames.Sample sample;
                    if (isStream && samples.TryGetValue(fileOffset, out sample))
                    {
                        string name = HarmonyBankNames.DisplayName(sample);
                        if (!string.IsNullOrEmpty(name))
                            track.Name = name;
                        track.Detail = HarmonyBankNames.Describe(sample);
                        track.KeyText = sample.SongId > 0 ? "songid=" + sample.SongId.ToString(CultureInfo.InvariantCulture) : null;
                    }

                    int start = fileOffset, length = readSize;
                    track.Decoder = t =>
                    {
                        byte[] from = isStream ? harmonyStream : harmonyRam;
                        if (from == null || start < 0 || length <= 0 || start + length > from.Length)
                            return "its byte range is outside the chunk that was loaded";

                        byte[] soundBuf = new byte[length];
                        Buffer.BlockCopy(from, start, soundBuf, 0, length);

                        int decodedChannels, decodedRate;
                        string reason;
                        short[] decoded = FrostyNewWaveEditor.DecodeClipAudio(soundBuf, out decodedChannels, out decodedRate, out reason);
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
            }
            catch (Exception ex)
            {
                App.Logger.LogError("HarmonySampleBank load failed: " + ex.ToString());
            }

            return retVal;
        }

        public override void Closed()
        {
            harmonyRam = null;
            harmonyStream = null;
            base.Closed();
        }
    }

    /// <summary>
    /// An Octane asset is a `SoundWaveAsset` variant: the same chunk list, runtime variations and segments, with
    /// the asset's own policy on top. It used to derive straight from the editor base without overriding the
    /// loader or a single command, which meant it opened as an empty list and answered every command with a
    /// warning - an editor that looked broken for an asset that is an ordinary sound wave. It is the wave editor.
    /// </summary>
    public class FrostyOctaneSoundEditor : FrostySoundWaveEditor
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

    /// <summary>
    /// An `ImpulseResponseAsset` is one chunk holding an `irf1` blob, not a wave: it has no runtime variations,
    /// segments or clip headers. This editor used to derive from the base with no loader at all, so it listed
    /// nothing and every command answered with a warning - the asset looked empty rather than undecoded.
    ///
    /// What can be stated is stated: each chunk is listed with the container's own header read out (the magic,
    /// the sample rate at `+0x10` and the payload length), and what cannot is refused with that reason instead of
    /// being written as if it were PCM. The payload encoding has not been decoded - the bytes are neither a clip
    /// header nor a WAV, the sample data does not correlate like 16-bit PCM at the stated rate, and the game's
    /// executable contains no reader for the magic - so writing audio into it would be a guess, and a wrong guess
    /// is a reverb that plays noise.
    /// </summary>
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

        protected override List<SoundDataTrack> InitialLoad(FrostyTaskWindow task)
        {
            List<SoundDataTrack> tracks = new List<SoundDataTrack>();
            dynamic asset = RootObject;

            int index = 0;
            foreach (dynamic chunk in asset.Chunks)
            {
                index++;

                ChunkAssetEntry entry = App.AssetManager.GetChunkEntry(chunk.ChunkId);
                SoundDataTrack track = new SoundDataTrack
                {
                    Index = index,
                    Name = "Impulse response " + index,
                    Codec = "irf1",
                    Detail = DescribeImpulseResponse(entry, (uint)chunk.ChunkSize),
                    DecodeFailure = "an irf1 impulse response, whose payload encoding is not decoded",
                };

                tracks.Add(track);
            }

            return tracks;
        }

        /// <summary>
        /// The one line the list can honestly carry about an `irf1` chunk: the container header as it stands, and
        /// the fact that the payload is not decoded.
        /// </summary>
        private static string DescribeImpulseResponse(ChunkAssetEntry entry, uint statedSize)
        {
            if (entry == null)
                return "irf1: the chunk is not in the loaded game data";

            try
            {
                byte[] data;
                using (NativeReader reader = new NativeReader(App.AssetManager.GetChunk(entry)))
                    data = reader.ReadToEnd();

                if (data.Length < 0x30 || data[0] != 'i' || data[1] != 'r' || data[2] != 'f' || data[3] != '1')
                    return "this chunk does not start an irf1 container";

                // The header is big-endian: 48000 is 00 00 BB 80 at +0x10.
                int rate = (data[0x10] << 24) | (data[0x11] << 16) | (data[0x12] << 8) | data[0x13];
                int payload = data.Length - 0x30;

                return "irf1 " + rate + " Hz, " + payload.ToString("N0") + " payload bytes"
                    + (statedSize != data.Length ? " (the asset states " + statedSize.ToString("N0") + ")" : "")
                    + "; the payload encoding is not decoded, so it cannot be played or edited";
            }
            catch (Exception ex)
            {
                return "irf1: the chunk could not be read (" + ex.Message + ")";
            }
        }

        /// <summary>
        /// An audio file cannot be written into this container: the payload is not PCM this plugin can encode.
        /// The command says so before the task window opens rather than writing a clip header into the blob.
        /// </summary>
        protected override void ImportSound(FrostyOpenFileDialog ofd, FrostyTaskWindow task)
        {
            App.Logger.LogError("This asset's audio is an `irf1` impulse response whose payload encoding is not " +
                "decoded, so an audio file cannot be written into it; nothing was changed.");
        }

        protected override bool CommitTrackSamples(SoundDataTrack track, int trackIndex, short[] samples, FrostyTaskWindow task)
        {
            App.Logger.LogError("This asset's audio is an `irf1` impulse response whose payload encoding is not " +
                "decoded, so the edit cannot be written; nothing was changed.");
            return false;
        }

        protected override bool CanAddClip
        {
            get { return false; }
        }

        protected override string AddClipRefusal
        {
            get { return "An impulse response is one irf1 blob, not a clip list; its payload encoding is not decoded."; }
        }

        /// <summary>
        /// Delete Clip is greyed out with the same reason, rather than offered and answered with an error: the
        /// chunk is the whole asset, and there is no clip row to remove from it.
        /// </summary>
        protected override string RemoveClipRefusal
        {
            get { return "An impulse response is one irf1 blob, not a clip list; its payload encoding is not decoded."; }
        }
    }
}