using System;

namespace SoundEditorPlugin
{
    /// <summary>
    /// A clip's length, read from its own header, and the form `Segments.Duration` states it in.
    ///
    /// FIFA17.exe reads the column through FUN_147982530 (class 2) as element + value word, added as 32-bit integers,
    /// and the result is the duration's float32 bit pattern (the field's data type is 5). So the column is not a
    /// scale to fit: a row's duration is `float32(seconds)` as bits, the value word is the smallest of them, and an
    /// element is the difference. Checked on every row of nine shipped banks against the clips' own headers
    /// (samples / rate): within one 48 kHz frame everywhere, the one-frame cases being 32 kHz banks whose build
    /// rounded the length to the 48 kHz frame grid.
    ///
    /// A clip's header (EA SNR, 12 or 20 bytes) states the sample rate at +0x06 (u16, big-endian) and the sample count
    /// at +0x09 (u24, big-endian); `0x48 0x00 0x00` and a header size of 0x0C or 0x14 open it.
    /// </summary>
    public static class SoundDurationScale
    {
        /// <summary>True when a clip header starts at <paramref name="offset"/>.</summary>
        public static bool IsClipHeader(byte[] chunk, long offset)
        {
            if (chunk == null || offset < 0 || offset + 12 > chunk.Length)
                return false;

            int at = (int)offset;
            return chunk[at] == 0x48 && chunk[at + 1] == 0x00 && chunk[at + 2] == 0x00
                && (chunk[at + 3] == 0x0C || chunk[at + 3] == 0x14);
        }

        /// <summary>The clip's length from its header: samples / rate.</summary>
        public static bool TryClipSeconds(byte[] chunk, long offset, out double seconds, out int samples, out int sampleRate)
        {
            seconds = 0.0;
            samples = 0;
            sampleRate = 0;

            if (!IsClipHeader(chunk, offset))
                return false;

            int at = (int)offset;
            sampleRate = (chunk[at + 6] << 8) | chunk[at + 7];
            samples = (chunk[at + 9] << 16) | (chunk[at + 10] << 8) | chunk[at + 11];
            if (sampleRate <= 0)
                return false;

            seconds = samples / (double)sampleRate;
            return true;
        }

        /// <summary>
        /// A clip's sample count, rate and channel count from its header, with the sample count read in full: the low
        /// 29 bits of the big-endian word at +0x08 (the top three are the header type and the loop flag), which is what
        /// a song longer than 2^24 samples needs. For the list's Time column; the writer keeps <see cref="TryClipSeconds"/>.
        /// </summary>
        public static bool TryHeader(byte[] data, long offset, out long samples, out int sampleRate, out int channels)
        {
            samples = 0;
            sampleRate = 0;
            channels = 0;

            if (!IsClipHeader(data, offset))
                return false;

            int at = (int)offset;
            sampleRate = ((data[at + 5] & 0x03) << 16) | (data[at + 6] << 8) | data[at + 7];
            channels = (data[at + 5] >> 2) + 1;
            samples = ((long)(data[at + 8] & 0x1F) << 24) | ((long)data[at + 9] << 16) | ((long)data[at + 10] << 8) | data[at + 11];
            return sampleRate > 0;
        }

        /// <summary>A clip's length in seconds from its header (<see cref="TryHeader"/>), or 0.</summary>
        public static double HeaderSeconds(byte[] data, long offset)
        {
            long samples;
            int rate, channels;
            return TryHeader(data, offset, out samples, out rate, out channels) ? samples / (double)rate : 0.0;
        }

        /// <summary>A length as the float32 bit pattern `Segments.Duration` states.</summary>
        public static uint Float32Bits(double seconds)
        {
            return BitConverter.ToUInt32(BitConverter.GetBytes((float)seconds), 0);
        }

        /// <summary>The seconds a float32 bit pattern states.</summary>
        public static double SecondsFromWord(uint word)
        {
            return BitConverter.ToSingle(BitConverter.GetBytes(word), 0);
        }
    }
}
