using FrostySdk;
using FrostySdk.IO;
using System.Text;

namespace BundleRefTablePlugin
{
    public static class BRTUtils
    {
        /// <summary>
        /// FIFA 17 always uses the legacy BRT format with 32-bit FNV1 hashes.
        /// </summary>
        public static bool IsLegacyBrtFormat
        {
            get { return ProfilesLibrary.DataVersion <= (int)ProfileVersion.Fifa17; }
        }

        public static string ReadString(NativeReader reader, ulong stringPos)
        {
            long currentPos = reader.Position;
            reader.Position = (long)stringPos;
            string stringData = reader.ReadNullTerminatedString();
            reader.Position = currentPos;
            return stringData;
        }

        /// <summary>
        /// Standard FNV-1 32-bit hash used by FIFA 17 BRT asset lookups.
        /// </summary>
        public static uint Fnv1Hash32(string s)
        {
            uint h = 0x811c9dc5;
            byte[] bytes = Encoding.ASCII.GetBytes(s.ToLower());
            foreach (byte b in bytes)
            {
                h = unchecked(h * 0x01000193);
                h ^= b;
            }
            return h;
        }

        /// <summary>
        /// Returns the appropriate hash for an asset path based on the current game profile.
        /// For FIFA 17 this always returns a 32-bit FNV1 hash (cast to ulong).
        /// </summary>
        public static ulong HashAssetPath(string path)
        {
            return Fnv1Hash32(path);
        }
    }
}
