using System;
using System.Collections.Generic;
using FrostySdk.IO;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace SoundEditorPlugin
{
    /// <summary>
    /// Sample-domain editing for a decoded track.
    ///
    /// Every operation takes and returns interleaved 16-bit PCM (<c>short[]</c>, frame-major: sample
    /// <c>frame * channels + channel</c>), so it is independent of the asset type that carries the
    /// audio and of the UI that asked for the edit. <c>null</c> and empty input is answered in kind
    /// rather than thrown on, because that is what an unloaded or unreadable track looks like here.
    ///
    /// <see cref="EncodePcm16Big"/> writes the Pcm16Big layout these banks hold: a 12-byte SNR header,
    /// 0x2600-frame blocks and a 0x0045 end marker. It is what both an edit and an import write, so a
    /// track that was edited and a track that was imported cannot come out in two shapes, and a test
    /// round-trips it through <c>Pcm16b.Decode</c>, which is the decoder the editors read tracks with.
    /// </summary>
    public static class SoundEdits
    {
        /// <summary>Frames per block in the Pcm16Big stream (0x2600, the value the importer used).</summary>
        public const int FramesPerBlock = 0x2600;

        /// <summary>Silence threshold used when a caller does not name one, in dBFS.</summary>
        public const double DefaultSilenceThresholdDb = -50.0;

        // ─────────────────────────────────────────────────────────────
        //  Measurement
        // ─────────────────────────────────────────────────────────────

        public static int FrameCount(short[] samples, int channels)
        {
            if (samples == null || channels <= 0)
                return 0;
            return samples.Length / channels;
        }

        public static double DurationSeconds(short[] samples, int channels, int sampleRate)
        {
            if (sampleRate <= 0)
                return 0.0;
            return FrameCount(samples, channels) / (double)sampleRate;
        }

        /// <summary>Largest absolute sample value, or 0 for empty input.</summary>
        public static int Peak(short[] samples)
        {
            if (samples == null)
                return 0;

            int peak = 0;
            for (int i = 0; i < samples.Length; i++)
            {
                int v = samples[i];
                if (v == short.MinValue)
                    v = -short.MinValue - 1;   // |MinValue| does not fit in a short
                else if (v < 0)
                    v = -v;
                if (v > peak)
                    peak = v;
            }
            return peak;
        }

        /// <summary>Peak level in dBFS; <see cref="double.NegativeInfinity"/> for silence.</summary>
        public static double PeakDb(short[] samples)
        {
            int peak = Peak(samples);
            if (peak == 0)
                return double.NegativeInfinity;
            return 20.0 * Math.Log10(peak / 32768.0);
        }

        /// <summary>RMS level in dBFS; <see cref="double.NegativeInfinity"/> for silence.</summary>
        public static double RmsDb(short[] samples)
        {
            if (samples == null || samples.Length == 0)
                return double.NegativeInfinity;

            double sum = 0.0;
            for (int i = 0; i < samples.Length; i++)
            {
                double s = samples[i] / 32768.0;
                sum += s * s;
            }

            double rms = Math.Sqrt(sum / samples.Length);
            if (rms <= 0.0)
                return double.NegativeInfinity;
            return 20.0 * Math.Log10(rms);
        }

        // ─────────────────────────────────────────────────────────────
        //  Trim
        // ─────────────────────────────────────────────────────────────

        /// <summary>
        /// Keeps frames <paramref name="startFrame"/> (inclusive) to <paramref name="endFrame"/>
        /// (exclusive). Out-of-range bounds are clamped, and an empty result is legal (it is what
        /// trimming a track to nothing produces).
        /// </summary>
        public static short[] Trim(short[] samples, int channels, int startFrame, int endFrame)
        {
            if (samples == null || channels <= 0)
                return samples;

            int frames = samples.Length / channels;
            if (startFrame < 0) startFrame = 0;
            if (endFrame > frames) endFrame = frames;
            if (endFrame <= startFrame)
                return new short[0];

            short[] output = new short[(endFrame - startFrame) * channels];
            Array.Copy(samples, startFrame * channels, output, 0, output.Length);
            return output;
        }

        /// <summary>
        /// Keeps the seconds <paramref name="startSeconds"/> (inclusive) to <paramref name="endSeconds"/>
        /// (exclusive). Negative bounds mean "from the start" and "to the end", and a range that runs
        /// past the clip is clamped rather than refused, because a length the user types is a request
        /// about the clip they can see, not an assertion about its frame count.
        /// </summary>
        public static short[] TrimSeconds(short[] samples, int channels, int sampleRate, double startSeconds, double endSeconds)
        {
            if (samples == null || channels <= 0 || sampleRate <= 0)
                return samples;

            int frames = samples.Length / channels;
            int startFrame = startSeconds <= 0.0 ? 0 : (int)Math.Round(startSeconds * sampleRate);
            int endFrame = endSeconds <= 0.0 ? frames : (int)Math.Round(endSeconds * sampleRate);

            return Trim(samples, channels, startFrame, endFrame);
        }

        /// <summary>
        /// Removes leading and trailing silence (frames whose loudest channel stays below
        /// <paramref name="thresholdDb"/>), optionally leaving <paramref name="padMs"/> of it in place.
        /// Silence between sounds is untouched: only the two ends are trimmed.
        /// </summary>
        public static short[] TrimSilence(short[] samples, int channels, int sampleRate,
            double thresholdDb = DefaultSilenceThresholdDb, int padMs = 0)
        {
            if (samples == null || channels <= 0)
                return samples;

            int frames = samples.Length / channels;
            if (frames == 0)
                return samples;

            int threshold = DbToAmplitudeSample(thresholdDb);

            int first = 0;
            while (first < frames && FrameIsBelow(samples, first, channels, threshold))
                first++;

            if (first == frames)
                return new short[0];    // the whole track is silence

            int last = frames - 1;
            while (last > first && FrameIsBelow(samples, last, channels, threshold))
                last--;

            int pad = 0;
            if (padMs > 0 && sampleRate > 0)
                pad = (int)((long)padMs * sampleRate / 1000L);

            return Trim(samples, channels, Math.Max(0, first - pad), Math.Min(frames, last + 1 + pad));
        }

        // ─────────────────────────────────────────────────────────────
        //  Level
        // ─────────────────────────────────────────────────────────────

        /// <summary>Scales the track by <paramref name="gainDb"/>, clamping at full scale.</summary>
        public static short[] ApplyGain(short[] samples, double gainDb)
        {
            if (samples == null)
                return null;

            double factor = Math.Pow(10.0, gainDb / 20.0);
            return Scale(samples, factor);
        }

        /// <summary>
        /// Raises the peak to <paramref name="targetDb"/>, never amplifying by more than
        /// <paramref name="maxGainDb"/> and never attenuating a track that is already quieter than
        /// the target. Silence is returned unchanged rather than blown up.
        /// </summary>
        public static short[] Normalize(short[] samples, double targetDb = -0.3, double maxGainDb = 24.0)
        {
            if (samples == null)
                return null;

            double peakDb = PeakDb(samples);
            if (double.IsNegativeInfinity(peakDb))
                return (short[])samples.Clone();

            double gainDb = targetDb - peakDb;
            if (gainDb > maxGainDb)
                gainDb = maxGainDb;

            return ApplyGain(samples, gainDb);
        }

        // ─────────────────────────────────────────────────────────────
        //  Fades
        // ─────────────────────────────────────────────────────────────

        public static short[] FadeIn(short[] samples, int channels, int sampleRate, int ms)
        {
            return Fade(samples, channels, sampleRate, ms, true);
        }

        public static short[] FadeOut(short[] samples, int channels, int sampleRate, int ms)
        {
            return Fade(samples, channels, sampleRate, ms, false);
        }

        private static short[] Fade(short[] samples, int channels, int sampleRate, int ms, bool fadeIn)
        {
            if (samples == null || channels <= 0)
                return samples;

            int frames = samples.Length / channels;
            if (frames == 0 || ms <= 0 || sampleRate <= 0)
                return (short[])samples.Clone();

            int rampFrames = (int)((long)ms * sampleRate / 1000L);
            if (rampFrames > frames)
                rampFrames = frames;

            short[] output = (short[])samples.Clone();

            for (int frame = 0; frame < rampFrames; frame++)
            {
                // The ramp is measured from its own anchor: forward from the first frame for a fade in,
                // backward from the last frame for a fade out. Either way the frame at the anchor is
                // silent and the frame that ends the ramp is unattenuated, so neither fade leaves a step.
                double gain = rampFrames == 1 ? 1.0 : frame / (double)(rampFrames - 1);

                int target = fadeIn ? frame : frames - 1 - frame;
                for (int channel = 0; channel < channels; channel++)
                {
                    int index = target * channels + channel;
                    output[index] = ClampToShort(output[index] * gain);
                }
            }

            return output;
        }

        // ─────────────────────────────────────────────────────────────
        //  Reverse
        // ─────────────────────────────────────────────────────────────

        /// <summary>Reverses the frame order; channels stay in their own order within a frame.</summary>
        public static short[] Reverse(short[] samples, int channels)
        {
            if (samples == null || channels <= 0)
                return samples;

            int frames = samples.Length / channels;
            short[] output = new short[samples.Length];

            for (int frame = 0; frame < frames; frame++)
            {
                int source = frame * channels;
                int target = (frames - 1 - frame) * channels;
                for (int channel = 0; channel < channels; channel++)
                    output[target + channel] = samples[source + channel];
            }

            return output;
        }

        // ─────────────────────────────────────────────────────────────
        //  Chunk surgery
        // ─────────────────────────────────────────────────────────────

        /// <summary>
        /// Replaces chunk bytes <paramref name="start"/>..<paramref name="end"/> with one clip's
        /// encoded bytes and returns the whole chunk. <paramref name="sizeDiff"/> is what every offset
        /// after the clip has to move by, which is what the callers patch their own tables with.
        ///
        /// A range that does not lie inside the chunk is refused rather than clamped: an edit that
        /// cannot be placed is one whose offsets would silently address the wrong clip.
        /// </summary>
        public static byte[] SpliceClip(byte[] chunk, int start, int end, byte[] clip, out int sizeDiff)
        {
            if (chunk == null)
                throw new ArgumentNullException(nameof(chunk));
            if (clip == null)
                throw new ArgumentNullException(nameof(clip));
            if (start < 0)
                throw new ArgumentOutOfRangeException(nameof(start), "The clip cannot start before the chunk.");
            if (end > chunk.Length)
                throw new ArgumentOutOfRangeException(nameof(end), "The clip cannot end after the chunk (" + end + " > " + chunk.Length + ").");
            if (end < start)
                throw new ArgumentOutOfRangeException(nameof(end), "The clip's end cannot come before its start.");

            sizeDiff = clip.Length - (end - start);

            byte[] output = new byte[chunk.Length + sizeDiff];
            Array.Copy(chunk, 0, output, 0, start);
            Array.Copy(clip, 0, output, start, clip.Length);
            Array.Copy(chunk, end, output, start + clip.Length, chunk.Length - end);
            return output;
        }

        // ─────────────────────────────────────────────────────────────
        //  Encoding — the Pcm16Big stream the chunk/res writer expects
        // ─────────────────────────────────────────────────────────────

        /// <summary>
        /// Decodes a WAV or MP3 the user picked into samples, keeping the file's own channel count and
        /// sample rate.
        ///
        /// A mono file stays mono. The import path used to widen every mono file to stereo, which is
        /// wrong for the speech banks this editor is used on: a 48 kHz mono take came out as a twice as
        /// long stereo one, and the chunk it replaced grew to match. It is also why this lives here -
        /// an import and an edit now produce the same Pcm16Big stream through the same encoder.
        /// </summary>
        public static short[] DecodeAudioFileToSamples(string path, out int channels, out int sampleRate)
        {
            if (path == null)
                throw new ArgumentNullException(nameof(path));

            // A 16-bit PCM WAV - which is what these banks and their exports are - is read as bytes,
            // because it needs no conversion at all: every sample comes back exactly as it was. Going
            // through floats instead costs up to 1 LSB on some samples, which is audible in a
            // round-trip comparison even though it is inaudible in the audio itself.
            if (path.EndsWith(".wav", StringComparison.OrdinalIgnoreCase))
            {
                short[] exact;
                if (TryReadPcm16Wav(path, out exact, out channels, out sampleRate))
                    return exact;
            }

            using (AudioFileReader reader = new AudioFileReader(path))
            {
                channels = reader.WaveFormat.Channels;
                sampleRate = reader.WaveFormat.SampleRate;

                IWaveProvider pcm16 = new SampleToWaveProvider16(reader.ToSampleProvider());
                byte[] buffer = new byte[64 * 1024];
                List<byte> bytes = new List<byte>();
                int read;
                while ((read = pcm16.Read(buffer, 0, buffer.Length)) > 0)
                    bytes.AddRange(new ArraySegment<byte>(buffer, 0, read));

                short[] samples = new short[bytes.Count / 2];
                for (int i = 0; i < samples.Length; i++)
                    samples[i] = (short)(bytes[i * 2] | (bytes[i * 2 + 1] << 8));

                return samples;
            }
        }

        /// <summary>Reads a 16-bit PCM WAV without touching floats. Answers false - leaving the caller
        /// to the general decoder - when the file is not 16-bit PCM WAV or cannot be read as one.</summary>
        private static bool TryReadPcm16Wav(string path, out short[] samples, out int channels, out int sampleRate)
        {
            samples = null;
            channels = 0;
            sampleRate = 0;

            try
            {
                using (WaveFileReader reader = new WaveFileReader(path))
                {
                    if (reader.WaveFormat.Encoding != WaveFormatEncoding.Pcm || reader.WaveFormat.BitsPerSample != 16)
                        return false;

                    channels = reader.WaveFormat.Channels;
                    sampleRate = reader.WaveFormat.SampleRate;
                    if (channels <= 0 || sampleRate <= 0)
                        return false;

                    byte[] raw = new byte[reader.Length];
                    int total = 0;
                    while (total < raw.Length)
                    {
                        int read = reader.Read(raw, total, raw.Length - total);
                        if (read <= 0)
                            break;
                        total += read;
                    }

                    samples = new short[total / 2];
                    Buffer.BlockCopy(raw, 0, samples, 0, samples.Length * 2);
                    return true;
                }
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// The whole import conversion: an audio file in, the Pcm16Big stream a chunk or a resource
        /// takes out, encoded by the same code that writes an edit.
        /// </summary>
        public static byte[] EncodePcm16BigFromAudioFile(string path, out int channels, out int sampleRate)
        {
            long frames;
            return EncodePcm16BigFromAudioFile(path, out channels, out sampleRate, out frames);
        }

        /// <summary>The same, saying how many frames the file decoded to (the encoder stops at 0xFFFFFF frames).</summary>
        public static byte[] EncodePcm16BigFromAudioFile(string path, out int channels, out int sampleRate, out long sourceFrames)
        {
            short[] samples = DecodeAudioFileToSamples(path, out channels, out sampleRate);
            sourceFrames = samples == null || channels <= 0 ? 0 : samples.Length / channels;
            return EncodePcm16Big(samples, channels, sampleRate);
        }

        /// <summary>Wraps decoded samples in a WAV file so an edited track can be exported.</summary>
        public static byte[] EncodeWav(short[] samples, int channels, int sampleRate)
        {
            if (samples == null)
                samples = new short[0];
            if (channels <= 0) channels = 1;
            if (sampleRate <= 0) sampleRate = 48000;

            byte[] data = EncodePcm16LittleEndian(samples);

            using (System.IO.MemoryStream stream = new System.IO.MemoryStream())
            using (System.IO.BinaryWriter writer = new System.IO.BinaryWriter(stream))
            {
                int byteRate = sampleRate * channels * 2;

                writer.Write(new byte[] { 0x52, 0x49, 0x46, 0x46 });   // "RIFF"
                writer.Write(36 + data.Length);
                writer.Write(new byte[] { 0x57, 0x41, 0x56, 0x45 });   // "WAVE"
                writer.Write(new byte[] { 0x66, 0x6D, 0x74, 0x20 });   // "fmt "
                writer.Write(16);
                writer.Write((short)1);                                 // PCM
                writer.Write((short)channels);
                writer.Write(sampleRate);
                writer.Write(byteRate);
                writer.Write((short)(channels * 2));                    // block align
                writer.Write((short)16);                                // bits per sample
                writer.Write(new byte[] { 0x64, 0x61, 0x74, 0x61 });   // "data"
                writer.Write(data.Length);
                writer.Write(data);
                writer.Flush();
                return stream.ToArray();
            }
        }

        /// <summary>
        /// Builds a Pcm16Big sound buffer: a 12-byte SNR header (word 0x4800000C, codec 0x12,
        /// channel byte, sample rate, total frame count), then blocks of at most
        /// <see cref="FramesPerBlock"/> frames, each introduced by its own size word and frame
        /// count, and closed with the 0x45000004 end marker.
        /// </summary>
        public static byte[] EncodePcm16Big(short[] samples, int channels, int sampleRate)
        {
            if (samples == null)
                samples = new short[0];
            if (channels <= 0)
                throw new ArgumentOutOfRangeException(nameof(channels), "A sound needs at least one channel.");
            if (sampleRate <= 0)
                throw new ArgumentOutOfRangeException(nameof(sampleRate), "A sound needs a sample rate.");

            int frames = samples.Length / channels;

            using (System.IO.MemoryStream stream = new System.IO.MemoryStream())
            using (FrostySdk.IO.NativeWriter writer = new FrostySdk.IO.NativeWriter(stream))
            {
                writer.Write(0x4800000c, Endian.Big);
                writer.Write((byte)0x12);                           // codec: Pcm16Big
                writer.Write((byte)((channels - 1) << 2));
                writer.Write((ushort)sampleRate, Endian.Big);

                long countPosition = writer.Position;
                writer.Write(0x40000000, Endian.Big);               // frame count, patched below

                int totalFrames = 0;
                while (totalFrames < frames)
                {
                    if (totalFrames + FramesPerBlock > 0x00ffffff)
                        break;                                      // the count is 24 bits wide

                    int blockFrames = Math.Min(FramesPerBlock, frames - totalFrames);
                    int blockSamples = blockFrames * channels;
                    int blockBytes = blockSamples * 2;

                    writer.Write((blockBytes + 8) | 0x44000000, Endian.Big);
                    writer.Write((uint)blockFrames, Endian.Big);

                    for (int i = 0; i < blockSamples; i++)
                        writer.Write(samples[totalFrames * channels + i], Endian.Big);

                    totalFrames += blockFrames;
                }

                writer.Write(0x45000004, Endian.Big);               // end of stream

                writer.Position = countPosition;
                writer.Write(totalFrames | 0x40000000, Endian.Big);

                writer.Flush();
                return stream.ToArray();
            }
        }

        // ─────────────────────────────────────────────────────────────
        //  Helpers
        // ─────────────────────────────────────────────────────────────

        private static short[] Scale(short[] samples, double factor)
        {
            short[] output = new short[samples.Length];
            for (int i = 0; i < samples.Length; i++)
                output[i] = ClampToShort(samples[i] * factor);
            return output;
        }

        private static bool FrameIsBelow(short[] samples, int frame, int channels, int threshold)
        {
            int start = frame * channels;
            for (int channel = 0; channel < channels; channel++)
            {
                int v = samples[start + channel];
                if (v == short.MinValue) v = -short.MinValue - 1;
                else if (v < 0) v = -v;
                if (v >= threshold)
                    return false;
            }
            return true;
        }

        private static int DbToAmplitudeSample(double db)
        {
            if (double.IsNegativeInfinity(db))
                return 0;
            double amplitude = Math.Pow(10.0, db / 20.0) * 32768.0;
            if (amplitude <= 0.0)
                return 0;
            if (amplitude >= 32768.0)
                return 32768;
            return (int)amplitude;
        }

        private static short ClampToShort(double value)
        {
            if (value >= short.MaxValue) return short.MaxValue;
            if (value <= short.MinValue) return short.MinValue;
            return (short)(value < 0 ? value - 0.5 : value + 0.5);      // round half away from zero
        }

        private static byte[] EncodePcm16LittleEndian(short[] samples)
        {
            byte[] data = new byte[samples.Length * 2];
            for (int i = 0; i < samples.Length; i++)
            {
                data[i * 2] = (byte)(samples[i] & 0xff);
                data[i * 2 + 1] = (byte)((samples[i] >> 8) & 0xff);
            }
            return data;
        }
    }
}
