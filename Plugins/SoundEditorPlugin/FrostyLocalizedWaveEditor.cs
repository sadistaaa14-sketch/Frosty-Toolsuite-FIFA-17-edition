using System;
using System.Collections.Generic;
using System.Drawing.Imaging;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Frosty.Core;
using Frosty.Core.Controls;
using Frosty.Core.Windows;
using FrostySdk.Interfaces;
using FrostySdk.IO;
using FrostySdk.Managers;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using WaveFormRendererLib;

namespace SoundEditorPlugin
{
    /// <summary>
    /// Editor for LocalizedWaveAsset.
    ///
    /// LocalizedWaveAsset stores variation/segment metadata in a NewWaveResource RES file
    /// (SBle container with TESD datasets). Audio data lives in chunk assets from EBX Chunks[].
    ///
    /// The RES contains a direct chunk byte offset table (Table J) located via:
    ///   - Scan TESD blocks for a block with nameHash 0xE8E591DD
    ///   - data[3] of that block = SBle-relative pointer to 7123 ascending uint32 offsets
    ///   - Clip i spans chunk[offset[i] .. offset[i+1]) (last clip ends at ChunkSize)
    ///
    /// Fallback: SNR header scanning (0x48 00 00 {0C|14}) if RES is unavailable.
    ///
    /// On import, both the chunk data and the RES offset table (Table J) are updated.
    /// </summary>
    public class FrostyLocalizedWaveEditor : FrostySoundDataEditor
    {
        // Well-known block nameHashes in NewWaveResource
        private const uint HASH_CHUNK_OFFSETS = 0xE8E591DD;  // Table J: per-variation chunk byte offsets
        private const uint HASH_CHUNK_SIZE    = 0xDC19107B;  // ChunkSize field

        // Cached state from InitialLoad for import
        private List<int>  _trackChunkIndices;   // which Chunks[] index each track lives in
        private List<int>  _trackStartOffsets;   // byte offset within chunk
        private List<int>  _trackEndOffsets;     // byte offset of clip end
        private bool       _usedResOffsets;      // true if offsets came from RES Table J
        private ResAssetEntry _resEntry;         // RES asset entry (for patching on import)
        private int        _resTableJFileOffset; // file offset of Table J within RES
        private int        _resTableJCopyFileOffset; // file offset of the duplicate Table J
        private uint       _resChunkSize;        // ChunkSize from TESD#4

        public FrostyLocalizedWaveEditor() : base(null) { }
        public FrostyLocalizedWaveEditor(ILogger inLogger) : base(inLogger) { }

        // ─────────────────────────────────────────────────────────────
        //  InitialLoad
        // ─────────────────────────────────────────────────────────────

        protected override List<SoundDataTrack> InitialLoad(FrostyTaskWindow task)
        {
            List<SoundDataTrack> retVal = new List<SoundDataTrack>();
            _trackChunkIndices = new List<int>();
            _trackStartOffsets = new List<int>();
            _trackEndOffsets = new List<int>();
            _usedResOffsets = false;
            _resEntry = null;
            _resTableJFileOffset = -1;
            _resTableJCopyFileOffset = -1;

            try
            {
                dynamic localizedWave = RootObject;

                // ── 1. Read all chunks ──
                List<byte[]> chunkDataList = new List<byte[]>();
                List<ChunkAssetEntry> chunkEntries = new List<ChunkAssetEntry>();

                if (localizedWave.Chunks != null)
                {
                    foreach (dynamic soundDataChunk in localizedWave.Chunks)
                    {
                        ChunkAssetEntry chunkEntry = App.AssetManager.GetChunkEntry(soundDataChunk.ChunkId);
                        if (chunkEntry != null)
                        {
                            using (NativeReader reader = new NativeReader(App.AssetManager.GetChunk(chunkEntry)))
                            {
                                chunkDataList.Add(reader.ReadToEnd());
                                chunkEntries.Add(chunkEntry);
                            }
                        }
                        else
                        {
                            App.Logger.LogWarning($"LocalizedWave chunk {soundDataChunk.ChunkId} not found. " +
                                "May be a localized chunk not loaded by your game.");
                            chunkDataList.Add(null);
                            chunkEntries.Add(null);
                        }
                    }
                }

                // ── 2. Try RES-based offset table (Table J) ──
                List<ClipLocation> clips = TryParseResOffsets(chunkDataList);

                // ── 3. Fallback to SNR scan ──
                if (clips == null || clips.Count == 0)
                {
                    App.Logger.Log("LocalizedWave: RES offsets not available, falling back to SNR scan.");
                    clips = ScanChunksForSnr(chunkDataList);
                }

                int totalClips = clips.Count;
                App.Logger.Log("LocalizedWave: {0} audio clips found across {1} chunk(s) [method: {2}]",
                    totalClips, chunkDataList.Count, _usedResOffsets ? "RES Table J" : "SNR scan");

                // ── 4. Decode each clip ──
                for (int s = 0; s < totalClips; s++)
                {
                    if (s % 200 == 0 || s == totalClips - 1)
                    {
                        task.Update(
                            status: "Loading Track " + (s + 1) + " / " + totalClips,
                            progress: ((s + 1) / (double)totalClips) * 90.0d);
                    }

                    ClipLocation loc = clips[s];
                    byte[] chunkData = chunkDataList[loc.ChunkIndex];
                    if (chunkData == null) continue;

                    int size = loc.EndOffset - loc.StartOffset;
                    if (size <= 12) continue;

                    byte[] soundBuf = new byte[size];
                    Array.Copy(chunkData, loc.StartOffset, soundBuf, 0, size);

                    SoundDataTrack track = DecodeSnrBuffer(soundBuf, s);
                    if (track != null)
                    {
                        retVal.Add(track);
                        _trackChunkIndices.Add(loc.ChunkIndex);
                        _trackStartOffsets.Add(loc.StartOffset);
                        _trackEndOffsets.Add(loc.EndOffset);
                    }
                }

                // ── 5. Render waveforms ──
                task.Update(status: "Rendering waveforms...", progress: 95.0d);
                RenderWaveforms(retVal);
            }
            catch (Exception ex)
            {
                App.Logger.LogError("LocalizedWaveAsset load failed: " + ex.ToString());
            }

            return retVal;
        }

        // ─────────────────────────────────────────────────────────────
        //  RES Parsing — locate and read Table J
        // ─────────────────────────────────────────────────────────────

        /// <summary>
        /// Reads the NewWaveResource RES file and extracts the chunk byte offset table (Table J).
        /// Returns clip locations, or null if RES is unavailable or unparseable.
        ///
        /// RES layout:
        ///   [0x00] 16-byte GUID
        ///   [0x10] SBle container
        ///     +0x0A  uint16  datasetCount
        ///     +0x50  datasetCount × 8B  dataset offset table
        ///     Each dataset = TESD block with blocks at +0x48
        ///     Block with nameHash 0xE8E591DD → data[3] = SBle ptr to Table J
        ///     Table J = variationCount × uint32 ascending chunk byte offsets
        /// </summary>
        private List<ClipLocation> TryParseResOffsets(List<byte[]> chunkDataList)
        {
            byte[] resData = TryReadResData();
            if (resData == null || resData.Length < 0x60)
                return null;

            try
            {
                // Verify SBle magic at offset 0x10
                int sble = 0x10;
                if (resData[sble] != 0x53 || resData[sble + 1] != 0x42 ||
                    resData[sble + 2] != 0x6C || resData[sble + 3] != 0x65)
                {
                    App.Logger.LogWarning("LocalizedWave RES: SBle magic not found at offset 0x10.");
                    return null;
                }

                int datasetCount = BitConverter.ToUInt16(resData, sble + 0x0A);

                // Scan datasets for target block hashes
                int tableJOffset = -1;       // file offset
                int tableJCopyOffset = -1;
                int variationCount = -1;
                uint chunkSizeFromRes = 0;

                for (int di = 0; di < datasetCount; di++)
                {
                    int dsOff = (int)BitConverter.ToUInt32(resData, sble + 0x50 + di * 8);
                    int absOff = sble + dsOff;
                    if (absOff + 0x48 > resData.Length) continue;

                    // Verify TESD magic
                    if (resData[absOff] != 0x54 || resData[absOff + 1] != 0x45 ||
                        resData[absOff + 2] != 0x53 || resData[absOff + 3] != 0x44)
                        continue;

                    int varCount = (int)BitConverter.ToUInt32(resData, absOff + 0x38);
                    int blockCount = BitConverter.ToUInt16(resData, absOff + 0x3C);
                    int blockStart = absOff + 0x48;

                    for (int bi = 0; bi < blockCount; bi++)
                    {
                        int boff = blockStart + bi * 24;
                        if (boff + 24 > resData.Length) break;

                        uint btype = BitConverter.ToUInt32(resData, boff);

                        if (btype == HASH_CHUNK_OFFSETS)
                        {
                            uint dataPtr = BitConverter.ToUInt32(resData, boff + 16); // data[3]
                            uint subCount = BitConverter.ToUInt32(resData, boff + 8); // data[1]
                            tableJOffset = sble + (int)dataPtr;
                            variationCount = varCount;

                            // The duplicate Table J follows immediately after
                            tableJCopyOffset = tableJOffset + varCount * 4;

                            App.Logger.Log("LocalizedWave RES: Table J at file offset 0x{0:X}, " +
                                "{1} variations, subCount={2}",
                                tableJOffset, varCount, subCount);
                        }
                        else if (btype == HASH_CHUNK_SIZE)
                        {
                            chunkSizeFromRes = BitConverter.ToUInt32(resData, boff + 8); // data[1]
                        }
                    }
                }

                if (tableJOffset < 0 || variationCount <= 0)
                {
                    App.Logger.Log("LocalizedWave RES: Table J block (0xE8E591DD) not found.");
                    return null;
                }

                if (tableJOffset + variationCount * 4 > resData.Length)
                {
                    App.Logger.LogWarning("LocalizedWave RES: Table J extends past file end.");
                    return null;
                }

                // Determine chunk size
                uint chunkSize = chunkSizeFromRes;
                if (chunkSize == 0 && chunkDataList.Count > 0 && chunkDataList[0] != null)
                    chunkSize = (uint)chunkDataList[0].Length;

                // Read Table J: variationCount ascending uint32 chunk byte offsets
                List<ClipLocation> clips = new List<ClipLocation>();
                uint[] offsets = new uint[variationCount];
                for (int i = 0; i < variationCount; i++)
                    offsets[i] = BitConverter.ToUInt32(resData, tableJOffset + i * 4);

                // Currently only single-chunk assets are handled
                int chunkIdx = 0;
                for (int i = 0; i < variationCount; i++)
                {
                    int start = (int)offsets[i];
                    int end = (i + 1 < variationCount) ? (int)offsets[i + 1] : (int)chunkSize;

                    if (end <= start || start < 0) continue;

                    clips.Add(new ClipLocation
                    {
                        ChunkIndex = chunkIdx,
                        StartOffset = start,
                        EndOffset = end,
                    });
                }

                _usedResOffsets = true;
                _resTableJFileOffset = tableJOffset;
                _resTableJCopyFileOffset = tableJCopyOffset;
                _resChunkSize = chunkSize;
                return clips;
            }
            catch (Exception ex)
            {
                App.Logger.LogWarning("LocalizedWave RES parse failed: " + ex.Message);
                return null;
            }
        }

        /// <summary>
        /// Attempts to read the NewWaveResource RES associated with this asset.
        /// Strategy: look for a RES entry sharing the EBX asset name.
        /// </summary>
        private byte[] TryReadResData()
        {
            try
            {
                EbxAssetEntry ebxEntry = AssetEntry as EbxAssetEntry;
                if (ebxEntry == null) return null;

                ResAssetEntry resEntry = App.AssetManager.GetResEntry(ebxEntry.Name);
                if (resEntry == null) return null;

                using (NativeReader reader = new NativeReader(App.AssetManager.GetRes(resEntry)))
                {
                    byte[] resData = reader.ReadToEnd();
                    if (resData != null && resData.Length > 0)
                    {
                        _resEntry = resEntry;
                        App.Logger.Log("LocalizedWave: found RES \"{0}\" ({1} bytes)",
                            ebxEntry.Name, resData.Length);
                        return resData;
                    }
                }
            }
            catch (Exception ex)
            {
                App.Logger.LogWarning("LocalizedWave: RES lookup failed: " + ex.Message);
            }
            return null;
        }

        // ─────────────────────────────────────────────────────────────
        //  SNR Header Scanning (fallback)
        // ─────────────────────────────────────────────────────────────

        private struct ClipLocation
        {
            public int ChunkIndex;
            public int StartOffset;
            public int EndOffset;
        }

        private List<ClipLocation> ScanChunksForSnr(List<byte[]> chunkDataList)
        {
            List<ClipLocation> results = new List<ClipLocation>();

            for (int c = 0; c < chunkDataList.Count; c++)
            {
                byte[] chunkData = chunkDataList[c];
                if (chunkData == null || chunkData.Length < 16) continue;
                ScanForSnrHeaders(chunkData, c, results);
            }

            // Fallback: treat each chunk as a single stream
            if (results.Count == 0)
            {
                for (int c = 0; c < chunkDataList.Count; c++)
                {
                    byte[] d = chunkDataList[c];
                    if (d != null && d.Length > 12)
                        results.Add(new ClipLocation { ChunkIndex = c, StartOffset = 0, EndOffset = d.Length });
                }
            }

            return results;
        }

        /// <summary>
        /// Scans for EA SNR audio block headers: 0x48 00 00 {0C|14}.
        /// Validates codec byte, sample rate, and 0x44 data block.
        /// Ends at 0x45 00 00 xx end marker.
        /// </summary>
        private static void ScanForSnrHeaders(byte[] data, int chunkIndex, List<ClipLocation> results)
        {
            for (int pos = 0; pos <= data.Length - 16; pos++)
            {
                if (data[pos] != 0x48 || data[pos + 1] != 0x00 || data[pos + 2] != 0x00)
                    continue;

                int headerPayload = data[pos + 3];
                if (headerPayload != 0x0C && headerPayload != 0x14)
                    continue;

                if (pos + 4 >= data.Length) continue;
                byte codec = data[pos + 4];
                if (codec != 0x12 && codec != 0x14 && codec != 0x15 &&
                    codec != 0x16 && codec != 0x19 && codec != 0x1c)
                    continue;

                if (pos + 7 >= data.Length) continue;
                ushort sr = (ushort)((data[pos + 6] << 8) | data[pos + 7]);
                if (sr != 8000 && sr != 11025 && sr != 12000 && sr != 16000 &&
                    sr != 22050 && sr != 24000 && sr != 32000 && sr != 44100 && sr != 48000)
                    continue;

                int dataBlockPos = pos + 4 + headerPayload;
                if (dataBlockPos >= data.Length || data[dataBlockPos] != 0x44)
                    continue;

                int endOffset = data.Length;
                for (int ep = dataBlockPos; ep <= data.Length - 4; ep++)
                {
                    if (data[ep] == 0x45 && data[ep + 1] == 0x00 && data[ep + 2] == 0x00)
                    {
                        endOffset = ep + 4 + data[ep + 3];
                        break;
                    }
                }
                endOffset = Math.Min(endOffset, data.Length);

                results.Add(new ClipLocation
                {
                    ChunkIndex = chunkIndex,
                    StartOffset = pos,
                    EndOffset = endOffset,
                });

                pos = Math.Max(pos, endOffset - 1);
            }
        }

        // ─────────────────────────────────────────────────────────────
        //  ImportSound — splice chunk + patch RES Table J
        // ─────────────────────────────────────────────────────────────

        protected override void ImportSound(FrostyOpenFileDialog ofd, FrostyTaskWindow task)
        {
            byte[] resultBuf = ConvertInputFile(ofd);

            int selectedIndex = 0;
            Dispatcher?.Invoke(() => { selectedIndex = tracksListBox.SelectedIndex; });

            if (_trackChunkIndices == null || selectedIndex < 0 || selectedIndex >= _trackChunkIndices.Count)
            {
                App.Logger.LogWarning("Invalid track selection for import.");
                return;
            }

            int chunkIdx = _trackChunkIndices[selectedIndex];
            int trackStart = _trackStartOffsets[selectedIndex];
            int trackEnd = _trackEndOffsets[selectedIndex];

            dynamic localizedWave = RootObject;
            dynamic soundDataChunkDyn = localizedWave.Chunks[chunkIdx];
            ChunkAssetEntry chunkEntry = App.AssetManager.GetChunkEntry(soundDataChunkDyn.ChunkId);

            if (chunkEntry == null)
            {
                App.Logger.LogWarning("Target chunk not available for import.");
                return;
            }

            byte[] chunkData;
            using (NativeReader reader = new NativeReader(App.AssetManager.GetChunk(chunkEntry)))
                chunkData = reader.ReadToEnd();

            trackEnd = Math.Min(trackEnd, chunkData.Length);
            int oldClipSize = trackEnd - trackStart;
            int sizeDiff = resultBuf.Length - oldClipSize;

            // Splice: [before track] + [new audio] + [after track]
            byte[] newChunkData = new byte[chunkData.Length + sizeDiff];
            Array.Copy(chunkData, 0, newChunkData, 0, trackStart);
            Array.Copy(resultBuf, 0, newChunkData, trackStart, resultBuf.Length);
            Array.Copy(chunkData, trackEnd, newChunkData, trackStart + resultBuf.Length,
                chunkData.Length - trackEnd);

            App.AssetManager.ModifyChunk(chunkEntry.Id, newChunkData);
            soundDataChunkDyn.ChunkSize = (uint)newChunkData.Length;

            // ── Patch RES Table J if we have it ──
            if (_usedResOffsets && _resEntry != null && _resTableJFileOffset >= 0 && sizeDiff != 0)
            {
                PatchResOffsets(selectedIndex, sizeDiff, (uint)newChunkData.Length);
            }

            audioPlayer.Dispose();
            audioPlayer = new AudioPlayer();

            List<SoundDataTrack> tracks = InitialLoad(task);

            Dispatcher?.Invoke(() =>
            {
                AssetModified = true;
                InvokeOnAssetModified();
                EbxAssetEntry assetEntry = AssetEntry as EbxAssetEntry;
                assetEntry.LinkAsset(chunkEntry);

                if (_resEntry != null)
                    assetEntry.LinkAsset(_resEntry);

                TracksList.Clear();
                foreach (var track in tracks)
                    TracksList.Add(track);
            });
        }

        /// <summary>
        /// After splicing a clip in the chunk, update both copies of Table J in the RES
        /// so that all offsets after the modified clip are shifted by sizeDiff.
        /// Also patches the ChunkSize field in TESD#4.
        /// </summary>
        private void PatchResOffsets(int modifiedTrackIndex, int sizeDiff, uint newChunkSize)
        {
            try
            {
                byte[] resData;
                using (NativeReader reader = new NativeReader(App.AssetManager.GetRes(_resEntry)))
                    resData = reader.ReadToEnd();

                int variationCount = _trackStartOffsets.Count;

                // Patch Table J: shift offsets after the modified track
                PatchOneTableJ(resData, _resTableJFileOffset, variationCount, modifiedTrackIndex, sizeDiff);

                // Patch the duplicate Table J (immediately follows)
                if (_resTableJCopyFileOffset >= 0 &&
                    _resTableJCopyFileOffset + variationCount * 4 <= resData.Length)
                {
                    PatchOneTableJ(resData, _resTableJCopyFileOffset, variationCount, modifiedTrackIndex, sizeDiff);
                }

                // Patch ChunkSize in TESD#4 (scan for block hash 0xDC19107B)
                PatchChunkSizeInRes(resData, newChunkSize);

                // Patch SBle totalSize header if the RES file size didn't change
                // (it shouldn't — we only modified values in-place)

                App.AssetManager.ModifyRes(_resEntry.ResRid, resData);

                App.Logger.Log("LocalizedWave: patched RES Table J (sizeDiff={0}, newChunkSize=0x{1:X})",
                    sizeDiff, newChunkSize);
            }
            catch (Exception ex)
            {
                App.Logger.LogWarning("LocalizedWave: RES patch failed (non-fatal): " + ex.Message);
            }
        }

        private static void PatchOneTableJ(byte[] resData, int tableOffset, int count,
            int modifiedIndex, int sizeDiff)
        {
            // All offsets after modifiedIndex need to be shifted
            for (int i = modifiedIndex + 1; i < count; i++)
            {
                int pos = tableOffset + i * 4;
                uint oldVal = BitConverter.ToUInt32(resData, pos);
                uint newVal = (uint)((int)oldVal + sizeDiff);
                BitConverter.GetBytes(newVal).CopyTo(resData, pos);
            }
        }

        private static void PatchChunkSizeInRes(byte[] resData, uint newChunkSize)
        {
            int sble = 0x10;
            if (sble + 0x0A + 2 > resData.Length) return;

            int datasetCount = BitConverter.ToUInt16(resData, sble + 0x0A);

            for (int di = 0; di < datasetCount; di++)
            {
                int dsOff = (int)BitConverter.ToUInt32(resData, sble + 0x50 + di * 8);
                int absOff = sble + dsOff;
                if (absOff + 0x48 > resData.Length) continue;

                if (resData[absOff] != 0x54 || resData[absOff + 1] != 0x45 ||
                    resData[absOff + 2] != 0x53 || resData[absOff + 3] != 0x44)
                    continue;

                int blockCount = BitConverter.ToUInt16(resData, absOff + 0x3C);
                int blockStart = absOff + 0x48;

                for (int bi = 0; bi < blockCount; bi++)
                {
                    int boff = blockStart + bi * 24;
                    if (boff + 24 > resData.Length) break;

                    uint btype = BitConverter.ToUInt32(resData, boff);
                    if (btype == HASH_CHUNK_SIZE)
                    {
                        BitConverter.GetBytes(newChunkSize).CopyTo(resData, boff + 8);
                        return;
                    }
                }
            }
        }

        // ─────────────────────────────────────────────────────────────
        //  Audio Decoding
        // ─────────────────────────────────────────────────────────────

        private static SoundDataTrack DecodeSnrBuffer(byte[] soundBuf, int trackIndex)
        {
            if (soundBuf == null || soundBuf.Length < 12)
                return null;

            byte codec;
            int channels;
            ushort sampleRate;
            using (NativeReader hdr = new NativeReader(new MemoryStream(soundBuf)))
            {
                hdr.ReadUInt(Endian.Big);
                codec = hdr.ReadByte();
                channels = (hdr.ReadByte() >> 2) + 1;
                sampleRate = hdr.ReadUShort(Endian.Big);
            }

            SoundDataTrack track = new SoundDataTrack { Name = "Track #" + (trackIndex + 1) };

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

            List<short> decoded = new List<short>();
            double duration = 0.0;

            if (codec == 0x12)
            {
                short[] pcm = Pcm16b.Decode(soundBuf);
                decoded.AddRange(pcm);
                duration = (pcm.Length / channels) / (double)sampleRate;
            }
            else if (codec == 0x14)
            {
                short[] xas = XAS.Decode(soundBuf);
                decoded.AddRange(xas);
                duration = (xas.Length / channels) / (double)sampleRate;
            }
            else if (codec == 0x15 || codec == 0x16)
            {
                bool ok = false;
                try
                {
                    uint sc = 0;
                    EALayer3.Decode(soundBuf, soundBuf.Length,
                        (short[] d, int count, EALayer3.StreamInfo info) =>
                        {
                            if (info.streamIndex == -1) return;
                            sc += (uint)d.Length;
                            decoded.AddRange(d);
                        });
                    duration = (sc / channels) / (double)sampleRate;
                    ok = decoded.Count > 0;
                }
                catch { decoded.Clear(); }

                if (!ok)
                {
                    int vCh, vSr;
                    short[] vd = FrostyNewWaveEditor.DecodeWithVgmstream(soundBuf, out vCh, out vSr);
                    if (vd != null && vd.Length > 0)
                    {
                        decoded.AddRange(vd);
                        channels = vCh;
                        sampleRate = (ushort)vSr;
                        duration = (vd.Length / channels) / (double)sampleRate;
                    }
                }
            }
            else if (codec == 0x19 || codec == 0x1c)
            {
                int vCh, vSr;
                short[] vd = FrostyNewWaveEditor.DecodeWithVgmstream(soundBuf, out vCh, out vSr);
                if (vd != null && vd.Length > 0)
                {
                    decoded.AddRange(vd);
                    channels = vCh;
                    sampleRate = (ushort)vSr;
                    duration = (vd.Length / channels) / (double)sampleRate;
                }
            }

            if (decoded.Count == 0)
                return null;

            track.SampleRate = sampleRate;
            track.ChannelCount = channels;
            track.Duration = duration;
            track.Samples = decoded.ToArray();
            track.SegmentCount = 1;
            return track;
        }

        // ─────────────────────────────────────────────────────────────
        //  Input File Conversion
        // ─────────────────────────────────────────────────────────────

        private byte[] ConvertInputFile(FrostyOpenFileDialog ofd)
        {
            MemoryStream ms = new MemoryStream();

            if (ofd.FileName.EndsWith(".wav", StringComparison.OrdinalIgnoreCase))
            {
                using (var reader = new AudioFileReader(ofd.FileName))
                {
                    if (reader.WaveFormat.Channels == 1)
                    {
                        var stereo = new MonoToStereoSampleProvider(reader)
                            { LeftVolume = 1.0f, RightVolume = 1.0f };
                        WaveFileWriter.WriteWavFileToStream(ms, new SampleToWaveProvider16(stereo));
                    }
                    else
                    {
                        WaveFileWriter.WriteWavFileToStream(ms, reader);
                    }
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
            {
                return File.ReadAllBytes(ofd.FileName);
            }
            else
            {
                throw new FileFormatException();
            }
        }

        // ─────────────────────────────────────────────────────────────
        //  Waveform Rendering
        // ─────────────────────────────────────────────────────────────

        private static void RenderWaveforms(List<SoundDataTrack> tracks)
        {
            var peakProvider = new MaxPeakProvider();
            var topSpacer = System.Drawing.Color.FromArgb(64, 83, 22, 3);
            var settings = new SoundCloudBlockWaveFormSettings(
                System.Drawing.Color.FromArgb(196, 197, 53, 0), topSpacer,
                System.Drawing.Color.FromArgb(196, 79, 26, 0),
                System.Drawing.Color.FromArgb(64, 79, 79, 79))
            {
                Name = "SoundCloud Orange Transparent Blocks",
                PixelsPerPeak = 2, SpacerPixels = 1,
                TopSpacerGradientStartColor = topSpacer,
                BackgroundColor = System.Drawing.Color.Transparent,
                Width = 800, TopHeight = 50, BottomHeight = 30,
            };
            var renderer = new WaveFormRenderer();

            foreach (SoundDataTrack track in tracks)
            {
                if (track.Samples == null || track.Samples.Length == 0)
                    continue;
                try
                {
                    var image = renderer.Render(track.Samples, peakProvider, settings);
                    using (var ms = new MemoryStream())
                    {
                        image.Save(ms, ImageFormat.Png);
                        ms.Seek(0, SeekOrigin.Begin);
                        var bmp = new BitmapImage();
                        bmp.BeginInit();
                        bmp.CacheOption = BitmapCacheOption.OnLoad;
                        bmp.StreamSource = ms;
                        bmp.EndInit();
                        var target = new RenderTargetBitmap(bmp.PixelWidth, bmp.PixelHeight,
                            bmp.DpiX, bmp.DpiY, PixelFormats.Pbgra32);
                        var visual = new DrawingVisual();
                        using (var dc = visual.RenderOpen())
                            dc.DrawImage(bmp, new Rect(0, 0, bmp.Width, bmp.Height));
                        target.Render(visual);
                        target.Freeze();
                        track.WaveForm = target;
                    }
                }
                catch (Exception) { }
            }
        }
    }
}
