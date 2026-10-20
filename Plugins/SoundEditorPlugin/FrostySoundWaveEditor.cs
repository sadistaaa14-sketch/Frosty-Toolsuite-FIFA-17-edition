using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
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

    /// <summary>
    /// A HarmonySampleBankAsset: a RAM chunk whose tables say where each sample is and what it is filed under, a stream
    /// chunk holding the samples, and a debug chunk naming each sample's source file.
    ///
    /// A replace (Import, the sample edits) rewrites the stream and moves every `.OFF` value after the sample, in every
    /// table of the RAM chunk, in place. Adding and removing songs is done for the EA Trax shape (EATrax_01, EATrax_02;
    /// <see cref="HarmonySongTable"/>): a new song is filed under the songid the user enters, with the metadata they
    /// enter, and the bank can be emptied. The other Harmony banks (intro and highlights music) key their tables
    /// differently and keep their samples; they can be replaced.
    /// </summary>
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

        /// <summary>Why this bank's songs cannot be added or removed, or null for an EA Trax bank that can.</summary>
        private volatile string songRefusal = "the bank has not been read yet";

        /// <summary>The song the Add Song window was answered with (UI thread), for AddTrackFile on the task thread.</summary>
        private EaTraxSongInfo pendingSong;

        /// <summary>Per track of the list: its stream offset, and the songid it plays (0 when none).</summary>
        private List<int> trackStarts = new List<int>();
        private List<int> trackSongIds = new List<int>();

        /// <summary>The RAM and stream chunks as last read, for decoding a sample when it is asked for.</summary>
        private byte[] harmonyRam, harmonyStream;

        protected override bool CanAddClip
        {
            get { return songRefusal == null; }
        }

        protected override string AddClipRefusal
        {
            get { return songRefusal == null ? "Add a song from an audio file." : "A song cannot be added to this bank: " + songRefusal + "."; }
        }

        protected override string RemoveClipRefusal
        {
            get
            {
                if (songRefusal != null)
                    return "A song cannot be removed from this bank: " + songRefusal + ".";
                if (SelectedTrackIndex < 0)
                    return "Select a clip first: a delete removes the selected song, its two rows and its audio.";
                return null;
            }
        }

        // ─── the bank's chunks ─────────────────────────────────────────────────────────────────────────────────────────

        private sealed class HarmonyChunks
        {
            public dynamic RamRow, DebugRow, StreamRow;
            public ChunkAssetEntry RamEntry, DebugEntry, StreamEntry;
            public byte[] Ram, Debug, Stream;
        }

        private static byte[] ReadChunkBytes(ChunkAssetEntry entry)
        {
            using (NativeReader reader = new NativeReader(App.AssetManager.GetChunk(entry)))
                return reader.ReadToEnd();
        }

        /// <summary>The bank's RAM, debug and stream chunks as the asset manager holds them now; null with the reason.</summary>
        private HarmonyChunks ReadChunks(out string why)
        {
            why = null;
            dynamic asset = RootObject;
            int rows = (int)asset.Chunks.Count;
            int ram = (int)asset.RamChunkIndex, debug = (int)asset.DebugChunkIndex, stream = (int)asset.StreamChunkIndex;
            if (ram < 0 || ram >= rows)
            {
                why = "it names no RAM chunk";
                return null;
            }

            HarmonyChunks c = new HarmonyChunks { RamRow = asset.Chunks[ram] };
            c.RamEntry = App.AssetManager.GetChunkEntry((Guid)c.RamRow.ChunkId);
            if (c.RamEntry == null)
            {
                why = "its RAM chunk is not in the loaded game data";
                return null;
            }

            c.Ram = ReadChunkBytes(c.RamEntry);
            if (stream != 255 && stream < rows)
            {
                c.StreamRow = asset.Chunks[stream];
                c.StreamEntry = App.AssetManager.GetChunkEntry((Guid)c.StreamRow.ChunkId);
                if (c.StreamEntry != null)
                    c.Stream = ReadChunkBytes(c.StreamEntry);
            }

            if (debug != 255 && debug < rows)
            {
                c.DebugRow = asset.Chunks[debug];
                c.DebugEntry = App.AssetManager.GetChunkEntry((Guid)c.DebugRow.ChunkId);
                if (c.DebugEntry != null)
                    c.Debug = ReadChunkBytes(c.DebugEntry);
            }

            return c;
        }

        /// <summary>
        /// Writes the chunks an edit changed (null leaves one as it is), stream first; a refused write gives the chunks
        /// already written their old bytes back. Then, on the UI thread, the asset's ChunkSize words follow and the asset
        /// is marked modified, and the chunks are linked to it so its Revert takes them back together.
        /// </summary>
        private bool WriteChunks(HarmonyChunks c, byte[] ram, byte[] debug, byte[] stream)
        {
            List<Tuple<ChunkAssetEntry, byte[], byte[], string>> plan = new List<Tuple<ChunkAssetEntry, byte[], byte[], string>>();
            List<dynamic> rows = new List<dynamic>();
            if (stream != null) { plan.Add(Tuple.Create(c.StreamEntry, stream, c.Stream, "stream chunk")); rows.Add(c.StreamRow); }
            if (ram != null) { plan.Add(Tuple.Create(c.RamEntry, ram, c.Ram, "RAM chunk")); rows.Add(c.RamRow); }
            if (debug != null) { plan.Add(Tuple.Create(c.DebugEntry, debug, c.Debug, "debug chunk")); rows.Add(c.DebugRow); }

            for (int i = 0; i < plan.Count; i++)
            {
                if (plan[i].Item1 == null)
                {
                    App.Logger.LogError("This bank's {0} is not in the loaded game data, so nothing was written.", plan[i].Item4);
                    return false;
                }
            }

            for (int i = 0; i < plan.Count; i++)
            {
                if (WriteChunk(plan[i].Item1, plan[i].Item2, "bank's " + plan[i].Item4))
                    continue;

                for (int j = 0; j < i; j++)
                {
                    if (!WriteChunk(plan[j].Item1, plan[j].Item3, "bank's " + plan[j].Item4 + " (putting its old bytes back)"))
                        App.Logger.LogError("This bank's {0} could not be given its old bytes back; revert the asset before saving.", plan[j].Item4);
                }

                return false;
            }

            RunOnUiThread(() =>
            {
                for (int i = 0; i < plan.Count; i++)
                    rows[i].ChunkSize = (uint)plan[i].Item2.Length;
                AssetModified = true;
                InvokeOnAssetModified();
            });
            LinkToAssetOnUiThread(plan.Select(p => (AssetEntry)p.Item1));
            return true;
        }

        private void Reload(FrostyTaskWindow task, int select)
        {
            audioPlayer.Dispose();
            audioPlayer = new AudioPlayer();
            task.Update(status: "Reading the bank back...");
            List<SoundDataTrack> tracks = InitialLoad(task);
            ShowReloadedTracks(tracks, Math.Max(0, Math.Min(select, tracks.Count - 1)));
        }

        // ─── replace ───────────────────────────────────────────────────────────────────────────────────────────────────

        protected override bool ImportFileInto(int index, string fileName, FrostyTaskWindow task)
        {
            task.Update(status: "Encoding the audio file...");
            if (!ReplaceHarmonyClip(index, ConvertAudioFileToPcm16Big(fileName)))
                return false;
            Reload(task, index);
            return true;
        }

        /// <summary>
        /// The sample edits (normalize, gain, fades, trim, undo) write the same thing an import does - one sample's bytes
        /// in place of the bytes that were there - so they share <see cref="ReplaceHarmonyClip"/>; the list is read again
        /// by the edit command (<see cref="FrostySoundDataEditor.RequireTrackReload"/>).
        /// </summary>
        protected override bool CommitTrackSamples(SoundDataTrack track, int trackIndex, short[] samples, FrostyTaskWindow task)
        {
            if (track == null)
            {
                App.Logger.LogError("There is no clip #{0} in this Harmony bank, so nothing was written.", trackIndex + 1);
                return false;
            }

            if (!ReplaceHarmonyClip(trackIndex, EncodeTrack(track, samples)))
                return false;
            RequireTrackReload();
            return true;
        }

        /// <summary>
        /// Writes one sample's audio over track <paramref name="index"/> and answers whether it reached the asset. In an EA
        /// Trax bank the song table is stated again (the song's `.OFF`, `.ZIS`, every later song's `.OFF`); in any other
        /// Harmony bank every `.OFF` value after the sample, in every table of the RAM chunk, moves by what the stream
        /// moved, in place (<see cref="EaTraxEdit.ShiftOffsets"/>).
        /// </summary>
        private bool ReplaceHarmonyClip(int index, byte[] audio)
        {
            if (audio == null || audio.Length == 0)
            {
                App.Logger.LogError("The new clip carries no audio, so nothing was written.");
                return false;
            }

            if (songRefusal == null)
                return EditSongs(new Dictionary<int, byte[]> { { index, audio } }, null, null, null, out string _);

            string why;
            HarmonyChunks c = ReadChunks(out why);
            if (c == null)
            {
                App.Logger.LogError("This Harmony bank cannot be written: {0}.", why);
                return false;
            }

            List<int> offsets;
            List<bool> streaming;
            int ramDataEnd;
            DiscoverTracks(c.Ram, c.Stream, out offsets, out streaming, out ramDataEnd);
            if (index < 0 || index >= offsets.Count)
            {
                App.Logger.LogError("There is no clip #{0} in this Harmony bank (it lists {1}), so nothing was written.", index + 1, offsets.Count);
                return false;
            }

            if (!streaming[index] || c.Stream == null)
            {
                App.Logger.LogError("Clip #{0} lives in the RAM chunk's own data, which this editor does not rewrite; nothing was written.", index + 1);
                return false;
            }

            List<int> samples = HarmonyStream.Samples(c.Stream);
            int start = offsets[index];
            List<HarmonyStream.Piece> pieces = samples.Select(s => s == start ? new HarmonyStream.Piece { Audio = audio }
                : new HarmonyStream.Piece { From = s }).ToList();
            long[] at;
            byte[] stream = HarmonyStream.Build(c.Stream, samples, pieces, out at, out why);
            if (stream == null)
            {
                App.Logger.LogError("Clip #{0} was not replaced: {1}.", index + 1, why);
                return false;
            }

            Dictionary<long, long> moved = new Dictionary<long, long>();
            for (int i = 0; i < samples.Count; i++)
                moved[samples[i]] = at[i];
            byte[] ram = (byte[])c.Ram.Clone();
            string shift = EaTraxEdit.ShiftOffsets(ram, moved);
            if (shift != null)
            {
                App.Logger.LogError("Clip #{0} was not replaced: {1}.", index + 1, shift);
                return false;
            }

            if (!WriteChunks(c, ram, null, stream))
                return false;
            App.Logger.Log("Replaced clip #{0}'s audio: {1:N0} bytes; the stream chunk is now {2:N0} bytes, and every .OFF after it moved with it. "
                + "This bank's tables are not the EA Trax shape, so its debug chunk's sizes and paths are left as they were.", index + 1, audio.Length, stream.Length);
            return true;
        }

        // ─── EA Trax songs: add, remove, replace in one edit ──────────────────────────────────────────────────────────

        /// <summary>
        /// One edit of an EA Trax bank (<see cref="EaTraxEdit"/>): <paramref name="replace"/> writes new audio over tracks (by
        /// list index), <paramref name="add"/> adds songs, <paramref name="remove"/> removes tracks with their songs, and
        /// <paramref name="metadata"/> states new metadata for the songs of tracks that stay. The three chunks are written
        /// together, and the songs' names are remembered for the session.
        /// </summary>
        private bool EditSongs(IDictionary<int, byte[]> replace, IList<KeyValuePair<EaTraxSongInfo, byte[]>> add,
            ICollection<int> remove, IDictionary<int, EaTraxSongInfo> metadata, out string report)
        {
            report = null;
            string why;
            HarmonyChunks c = ReadChunks(out why);
            if (c == null)
            {
                App.Logger.LogError("This EA Trax bank cannot be written: {0}.", why);
                return false;
            }

            EaTraxEdit edit = new EaTraxEdit();
            if (replace != null)
                foreach (KeyValuePair<int, byte[]> pair in replace)
                    edit.Replace[pair.Key] = pair.Value;
            if (add != null)
                edit.Add.AddRange(add);
            if (remove != null)
                foreach (int i in remove)
                    edit.Remove.Add(i);
            if (metadata != null)
                foreach (KeyValuePair<int, EaTraxSongInfo> pair in metadata)
                    edit.Metadata[pair.Key] = pair.Value;
            if (edit.Add.Count > 0)
                edit.FirstNewSampleId = NextSampleId();

            why = edit.Apply(c.Ram, c.Debug, c.Stream);
            if (why != null)
            {
                App.Logger.LogError("Nothing was written to this EA Trax bank: {0}.", why);
                return false;
            }

            if (!WriteChunks(c, edit.Ram, edit.Debug, edit.Stream))
                return false;

            foreach (KeyValuePair<EaTraxSongInfo, byte[]> song in edit.Add)
                FifaNameLookup.RememberSong(song.Key.SongId, song.Key.DisplayName);
            foreach (KeyValuePair<int, EaTraxSongInfo> info in edit.Metadata)
            {
                if (info.Value != null && !edit.Remove.Contains(info.Key) && info.Value.SongId > 0)
                    FifaNameLookup.RememberSong(info.Value.SongId, info.Value.DisplayName);
            }

            foreach (HarmonySong song in edit.NewSongs)
                App.Logger.Log("EA Trax: added {0}", song);
            report = edit.Report;
            App.Logger.Log("EA Trax: {0}", report);
            return true;
        }


        /// <summary>
        /// The `.SID` the first new song takes: one past the largest any HarmonySampleBankAsset's RAM chunk states (every
        /// table of all of them, read through the asset manager, so a bank edited in this project counts as edited);
        /// <see cref="EaTraxEdit"/> numbers further new songs on from it. FIFA17.exe hands a row's `.SID` to every listener
        /// of the bank when the sample starts (FUN_147985380 -> FUN_147987e90 -> FUN_147987c90, the listener's +0x50) and
        /// when it stops (FUN_147987b70 -> FUN_147987d30 -> FUN_147987c00, +0x58) - bridge n1-n3, 2026-10-20; what the
        /// listeners do with it is not traced - so a new song does not take a value another bank's sample has.
        /// </summary>
        private uint NextSampleId()
        {
            uint max = 0;
            try
            {
                foreach (EbxAssetEntry entry in App.AssetManager.EnumerateEbx(type: "HarmonySampleBankAsset"))
                {
                    try
                    {
                        dynamic root = App.AssetManager.GetEbx(entry).RootObject;
                        int ramIndex = (int)root.RamChunkIndex;
                        if (ramIndex < 0 || ramIndex >= (int)root.Chunks.Count)
                            continue;
                        ChunkAssetEntry chunk = App.AssetManager.GetChunkEntry((Guid)root.Chunks[ramIndex].ChunkId);
                        if (chunk == null)
                            continue;
                        string error;
                        BankImage image = BankImage.Read(ReadChunkBytes(chunk), out error);
                        if (image == null)
                            continue;
                        foreach (BankDataset ds in image.Datasets)
                        {
                            BankField sid = ds.Field(HarmonySongTable.SampleIdField);
                            uint[] values = sid == null ? null : sid.Decode(ds.Rows);
                            if (values != null && values.Length > 0)
                                max = Math.Max(max, values.Max());
                        }
                    }
                    catch (Exception)
                    {
                        // A bank that does not read is skipped; this one's own values are already counted.
                    }
                }
            }
            catch (Exception ex)
            {
                App.Logger.LogWarning("The Harmony banks could not be read for their .SID values ({0}); a new song's .SID is one past this bank's.", ex.Message);
            }

            return max + 1;
        }

        // ─── add ───────────────────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>The song's songid and metadata, asked on the UI thread before the file picker; false abandons the add.</summary>
        protected override bool PrepareAddClip()
        {
            pendingSong = null;
            if (songRefusal != null)
            {
                App.Logger.LogWarning("A song cannot be added to this bank: {0}.", songRefusal);
                return false;
            }

            pendingSong = AskForSong(null);
            return pendingSong != null;
        }

        protected override bool AddTrackFile(string fileName, FrostyTaskWindow task)
        {
            EaTraxSongInfo info = pendingSong;
            pendingSong = null;
            if (info == null)
            {
                App.Logger.LogWarning("Adding a song needs its songid and metadata; the Add Song window was not answered.");
                return false;
            }

            task.Update(status: "Encoding the audio file...");
            byte[] audio = ConvertAudioFileToPcm16Big(fileName);
            if (audio == null || audio.Length == 0)
            {
                App.Logger.LogWarning("The file chosen for the new song decoded to no audio, so nothing was written.");
                return false;
            }

            task.Update(status: "Adding the song...");
            string report;
            if (!EditSongs(null, new List<KeyValuePair<EaTraxSongInfo, byte[]>> { new KeyValuePair<EaTraxSongInfo, byte[]>(info, audio) },
                    null, null, out report))
                return false;

            WriteDbRows(Path.GetDirectoryName(fileName), new[] { info });
            Reload(task, int.MaxValue);
            return true;
        }

        /// <summary>The rows the game DB needs for new songs, beside the audio they came from (see <see cref="EaTraxDbRows"/>).</summary>
        private static void WriteDbRows(string folder, IEnumerable<EaTraxSongInfo> songs)
        {
            List<EaTraxSongInfo> list = songs.Where(s => s != null).ToList();
            if (list.Count == 0 || string.IsNullOrEmpty(folder))
                return;
            try
            {
                string[] files = EaTraxDbRows.Write(folder, list);
                App.Logger.Log("The game lists EA Trax songs from its DB (FUN_14789d6a0 joins songplaylistlinks with eatrax): the rows for "
                    + "{0} song(s) are in {1} and {2}; import them into those tables for the song(s) to play in the game.",
                    list.Count, files[0], files[1]);
            }
            catch (Exception ex)
            {
                App.Logger.LogWarning("The DB rows for the new song(s) could not be written to {0}: {1}", folder, ex.Message);
            }
        }

        /// <summary>
        /// The Add Song window: the songid (the next free one is offered) and the DB columns a song has - title (songname),
        /// artist, album, intro, playlistid, unlocked - each captioned with the column it is. Null when cancelled.
        /// </summary>
        private EaTraxSongInfo AskForSong(EaTraxSongInfo start)
        {
            HashSet<int> taken = new HashSet<int>(trackSongIds.Where(i => i > 0));
            foreach (int id in FifaNameLookup.SongIds())
                taken.Add(id);
            int next = 1;
            while (taken.Contains(next))
                next++;

            Window window = new Window
            {
                Title = "Add EA Trax Song",
                Width = 480,
                SizeToContent = SizeToContent.Height,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                ResizeMode = ResizeMode.NoResize,
                ShowInTaskbar = false,
                Owner = Window.GetWindow(this),
            };

            Grid grid = new Grid { Margin = new Thickness(12) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            int row = 0;
            Func<string, string, string, TextBox> field = (caption, tip, text) =>
            {
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                TextBlock label = new TextBlock { Text = caption, Margin = new Thickness(0, 6, 10, 3), ToolTip = tip };
                Grid.SetRow(label, row);
                grid.Children.Add(label);
                TextBox box = new TextBox { Text = text ?? "", Margin = new Thickness(0, 3, 0, 3), ToolTip = tip };
                Grid.SetRow(box, row);
                Grid.SetColumn(box, 1);
                grid.Children.Add(box);
                row++;
                return box;
            };

            TextBox songId = field("songid", "eatrax.songid: the value the bank files the song under, and the DB's key for it",
                (start != null ? start.SongId : next).ToString(CultureInfo.InvariantCulture));

            // Under the songid, as it is typed: whether the DB (or this session) already names it.
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            TextBlock idSays = new TextBlock { Foreground = Brushes.Gray, Margin = new Thickness(0, 0, 0, 3), TextTrimming = TextTrimming.CharacterEllipsis };
            Grid.SetRow(idSays, row);
            Grid.SetColumn(idSays, 1);
            grid.Children.Add(idSays);
            row++;
            Action describe = () =>
            {
                int typed;
                string known;
                if (!BulkPlanner.TryInt(songId.Text, out typed) || typed < 1)
                    idSays.Text = "a whole number from 1";
                else if (trackSongIds.Contains(typed))
                    idSays.Text = "already a song of this bank";
                else if (FifaNameLookup.TrySongName(typed, out known))
                    idSays.Text = "the DB already names it: " + known + " (another song, unless you are moving it here)";
                else
                    idSays.Text = "free: no song of this bank or the DB has it";
            };
            songId.TextChanged += (s, e) => describe();
            describe();
            TextBox title = field("title", "eatrax.songname", start?.Title);
            TextBox artist = field("artist", "eatrax.artist", start?.Artist);
            TextBox album = field("album", "eatrax.album", start?.Album);
            TextBox intro = field("intro", "eatrax.intro (every shipped song: 1)", (start?.Intro ?? 1).ToString(CultureInfo.InvariantCulture));
            TextBox playlist = field("playlistid", "songplaylistlinks.playlistid (every shipped song: 0)", (start?.PlaylistId ?? 0).ToString(CultureInfo.InvariantCulture));
            TextBox unlocked = field("unlocked", "songplaylistlinks.unlocked (every shipped song: 1)", (start?.Unlocked ?? 1).ToString(CultureInfo.InvariantCulture));

            TextBlock note = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                Opacity = 0.7,
                Margin = new Thickness(0, 8, 0, 0),
                Text = "The game lists EA Trax songs from its DB, so the eatrax and songplaylistlinks rows for this song are written "
                    + "next to the audio file (" + EaTraxDbRows.EatraxFile + ", " + EaTraxDbRows.LinksFile + ") for you to import. "
                    + "Songids in use here or in the DB: " + SoundTrackNaming.DescribeBankValues(taken) + ".",
            };
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Grid.SetRow(note, row);
            Grid.SetColumnSpan(note, 2);
            grid.Children.Add(note);
            row++;

            TextBlock problem = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = Brushes.IndianRed, Margin = new Thickness(0, 6, 0, 0), Visibility = Visibility.Collapsed };
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Grid.SetRow(problem, row);
            Grid.SetColumnSpan(problem, 2);
            grid.Children.Add(problem);
            row++;

            StackPanel buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
            Button ok = new Button { Content = "Add", Width = 80, Height = 24, IsDefault = true };
            Button cancel = new Button { Content = "Cancel", Width = 80, Height = 24, Margin = new Thickness(6, 0, 0, 0), IsCancel = true };
            buttons.Children.Add(ok);
            buttons.Children.Add(cancel);
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Grid.SetRow(buttons, row);
            Grid.SetColumnSpan(buttons, 2);
            grid.Children.Add(buttons);

            EaTraxSongInfo chosen = null;
            ok.Click += (s, e) =>
            {
                int id, introValue, playlistValue, unlockedValue;
                string say = null;
                if (!BulkPlanner.TryInt(songId.Text, out id) || id < 1)
                    say = "The songid is a whole number from 1.";
                else if (trackSongIds.Contains(id))
                    say = "Songid " + id + " is already a song of this bank (replace its audio with Import instead).";
                else if (string.IsNullOrWhiteSpace(title.Text))
                    say = "A song needs a title.";
                else if (!BulkPlanner.TryInt(intro.Text, out introValue) || !BulkPlanner.TryInt(playlist.Text, out playlistValue)
                         || !BulkPlanner.TryInt(unlocked.Text, out unlockedValue))
                    say = "intro, playlistid and unlocked are whole numbers.";
                else
                {
                    chosen = new EaTraxSongInfo
                    {
                        SongId = id, Title = title.Text.Trim(), Artist = artist.Text.Trim(), Album = album.Text.Trim(),
                        Intro = introValue, PlaylistId = playlistValue, Unlocked = unlockedValue,
                    };
                    window.DialogResult = true;
                    return;
                }

                problem.Text = say;
                problem.Visibility = Visibility.Visible;
            };

            window.Content = grid;
            window.Loaded += (s, e) => title.Focus();
            return window.ShowDialog() == true ? chosen : null;
        }

        // ─── remove ────────────────────────────────────────────────────────────────────────────────────────────────────

        protected override void DeleteTrack(int trackIndex, FrostyTaskWindow task)
        {
            RemoveTracks(new[] { trackIndex }, task);
        }

        protected override void DeleteTracks(IList<int> indices, FrostyTaskWindow task)
        {
            RemoveTracks(indices, task);
        }

        /// <summary>Keep First N Clips; 0 empties the bank.</summary>
        protected override void KeepFirstNTracks(int keepCount, FrostyTaskWindow task)
        {
            int count = trackStarts.Count;
            if (keepCount < 0 || keepCount >= count)
            {
                App.Logger.Log("Keep First N Clips: the bank has {0} track(s), so nothing was removed.", count);
                return;
            }

            RemoveTracks(Enumerable.Range(keepCount, count - keepCount).ToList(), task);
        }

        protected override void DeleteAllExcept(int keepIndex, FrostyTaskWindow task)
        {
            RemoveTracks(Enumerable.Range(0, trackStarts.Count).Where(i => i != keepIndex).ToList(), task);
        }

        /// <summary>
        /// Removes tracks and the songs filed under them in one edit - every track of the bank too: an EA Trax bank with no
        /// song left is written so that no songid finds a row (<see cref="HarmonySongTable"/>), and songs can be added to it.
        /// </summary>
        private void RemoveTracks(IList<int> indices, FrostyTaskWindow task)
        {
            if (songRefusal != null)
            {
                App.Logger.LogWarning("A song cannot be removed from this bank: {0}.", songRefusal);
                return;
            }

            List<int> gone = indices.Distinct().ToList();
            if (gone.Count == 0)
                return;

            task.Update(status: "Removing " + gone.Count + " track(s)...");
            string report;
            if (EditSongs(null, null, gone, null, out report))
                Reload(task, gone.Min());
        }

        // ─── bulk import ──────────────────────────────────────────────────────────────────────────────────────────────

        protected override BulkTarget DescribeBulkTarget(out string refusal)
        {
            refusal = null;
            BulkTarget target = base.DescribeBulkTarget(out refusal);
            if (target == null)
                return null;

            if (songRefusal != null)
            {
                target.Kind = BulkKind.Clips;
                target.CanAdd = false;
                target.AddRefusal = "a song cannot be added to this bank: " + songRefusal;
                return target;
            }

            target.Kind = BulkKind.Songs;
            target.CanAdd = true;
            target.ClipSongIds = trackSongIds.ToList();
            target.ClipSongs = trackSongIds.Select(id => FifaNameLookup.TrySongInfo(id)).ToList();
            return target;
        }

        protected override void RunBulkImport(BulkPlan plan, FrostyTaskWindow task)
        {
            if (songRefusal != null)
            {
                base.RunBulkImport(plan, task);
                return;
            }

            Dictionary<string, byte[]> encoded = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
            Dictionary<int, byte[]> replace = new Dictionary<int, byte[]>();
            Dictionary<int, EaTraxSongInfo> metadata = new Dictionary<int, EaTraxSongInfo>();
            List<KeyValuePair<EaTraxSongInfo, byte[]>> add = new List<KeyValuePair<EaTraxSongInfo, byte[]>>();
            for (int n = 0; n < plan.Steps.Count; n++)
            {
                BulkStep step = plan.Steps[n];
                task.Update(status: "Encoding " + (n + 1) + " / " + plan.Steps.Count + ": " + step.File, progress: (n + 1) * 80.0 / plan.Steps.Count);
                byte[] audio;
                if (!encoded.TryGetValue(step.Path, out audio))
                {
                    audio = ConvertAudioFileToPcm16Big(step.Path);
                    encoded[step.Path] = audio;
                }

                if (audio == null || audio.Length == 0)
                {
                    App.Logger.LogWarning("{0} decodes to no audio, so nothing was imported.", step.File);
                    return;
                }

                if (step.Add)
                {
                    add.Add(new KeyValuePair<EaTraxSongInfo, byte[]>(step.Song, audio));
                }
                else
                {
                    replace[step.Clip] = audio;
                    if (step.Song != null)
                        metadata[step.Clip] = step.Song;
                }
            }

            task.Update(status: "Writing the bank...", progress: 90);
            string report;
            if (!EditSongs(replace, add, null, metadata, out report))
                return;

            WriteDbRows(plan.Folder, add.Select(a => a.Key).Concat(metadata.Values));
            Reload(task, plan.Steps.Count > 0 && !plan.Steps[0].Add ? plan.Steps[0].Clip : int.MaxValue);
        }

        // ─── reading the bank ─────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Where the bank's samples start, in the order the list shows them and every command addresses them: the RAM
        /// chunk's records that state a sample in the chunk itself (`.RAM`), and every SPS stream the stream chunk holds
        /// (<see cref="HarmonyStream.Samples"/>), all sorted by offset.
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

            if (streamData == null)
            {
                foreach (int offset in tesd)
                {
                    int at = offset + 0x3c;
                    int blockCount = BitConverter.ToUInt16(ramData, at);
                    at += 2 + 0x0a;

                    for (int i = 0; i < blockCount; i++)
                    {
                        uint blockType = BitConverter.ToUInt32(ramData, at);
                        if (blockType == 0x2e52414d)
                        {
                            offsets.Add(BitConverter.ToInt32(ramData, at + 8) + dataOffset);
                            streaming.Add(false);
                        }

                        at += 24;
                    }
                }
            }
            else
            {
                foreach (int start in HarmonyStream.Samples(streamData))
                {
                    offsets.Add(start);
                    streaming.Add(true);
                }
            }

            int[] order = Enumerable.Range(0, offsets.Count).ToArray();
            List<int> unsorted = offsets;
            Array.Sort(order, (a, b) => unsorted[a].CompareTo(unsorted[b]));
            List<int> sortedOffsets = order.Select(i => unsorted[i]).ToList();
            List<bool> unsortedStreaming = streaming;
            streaming = order.Select(i => unsortedStreaming[i]).ToList();
            offsets = sortedOffsets;
        }

        /// <summary>Where the SPS stream starting at <paramref name="start"/> ends (see <see cref="HarmonyStream.StreamEnd"/>).</summary>
        internal static int StreamEnd(byte[] data, int start)
        {
            return HarmonyStream.StreamEnd(data, start);
        }

        /// <summary>
        /// Lists the bank's samples from their headers and its own tables, and decodes none of them: a sample is decoded
        /// when it is selected, played or exported. An EA Trax bank's songs are minutes of EA Layer 3 each, and decoding
        /// all of them (and drawing their waveforms) to open the tab is what used to take minutes.
        ///
        /// A sample is named by what its bank states (<see cref="HarmonyBankNames"/>): an EA Trax song by the game DB's
        /// `artist - songname` for the songid it is filed under (or what this session's Add Song or bulk import entered
        /// for it), else by its source file's name in the debug chunk.
        /// </summary>
        protected override List<SoundDataTrack> InitialLoad(FrostyTaskWindow task)
        {
            List<SoundDataTrack> retVal = new List<SoundDataTrack>();
            List<int> starts = new List<int>();
            List<int> songIds = new List<int>();

            try
            {
                task.Update(status: "Reading the bank's chunks...");
                string why;
                HarmonyChunks c = ReadChunks(out why);
                if (c == null)
                {
                    App.Logger.LogError("This Harmony bank cannot be read: {0}.", why);
                    songRefusal = why;
                    return retVal;
                }

                byte[] ram = c.Ram, stream = c.Stream, debug = c.Debug;
                harmonyRam = ram;
                harmonyStream = stream;

                // Adding and removing songs is the EA Trax shape's; the reason it is not is shown on the buttons.
                HarmonySongTable table = HarmonySongTable.Open(ram, debug, out why);
                songRefusal = stream == null ? "it has no stream chunk" : (table == null ? why : null);

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

                    int songId = 0;
                    HarmonyBankNames.Sample sample;
                    if (isStream && samples.TryGetValue(fileOffset, out sample))
                    {
                        string name = HarmonyBankNames.DisplayName(sample);
                        if (!string.IsNullOrEmpty(name))
                            track.Name = name;
                        track.Detail = HarmonyBankNames.Describe(sample);
                        track.KeyText = sample.SongId > 0 ? "songid=" + sample.SongId.ToString(CultureInfo.InvariantCulture) : null;
                        songId = sample.SongId;
                    }

                    starts.Add(fileOffset);
                    songIds.Add(songId);

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

                if (songRefusal == null)
                    App.Logger.Log("EA Trax bank: {0} song(s) in its tables, {1} track(s) in its stream.", table.Songs.Count, offsets.Count);
            }
            catch (Exception ex)
            {
                App.Logger.LogError("HarmonySampleBank load failed: " + ex.ToString());

                // The list is partial, so no song is added or removed by its positions until the bank reads whole.
                songRefusal = "the bank did not read completely (" + ex.Message + ")";
            }

            trackStarts = starts;
            trackSongIds = songIds;
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

        /// <summary>The same answer for an import into checked clips and a bulk import, which come through here.</summary>
        protected override bool ImportFileInto(int index, string fileName, FrostyTaskWindow task)
        {
            App.Logger.LogError("This asset's audio is an `irf1` impulse response whose payload encoding is not " +
                "decoded, so an audio file cannot be written into it; nothing was changed.");
            return false;
        }

        protected override BulkTarget DescribeBulkTarget(out string refusal)
        {
            BulkTarget target = base.DescribeBulkTarget(out refusal);
            if (target != null)
            {
                target.CanReplace = false;
                target.ReplaceRefusal = "this asset's audio is an irf1 impulse response whose payload encoding is not decoded, so an audio file cannot be written into it";
            }

            return target;
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