using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace DuplicationPlugin
{
    /// <summary>
    /// One lighting/&lt;slot&gt;.fblight file of a package (PesFifa.Core/Package/LightingWriter.cs): the mesh's baked
    /// lighting at one or more sizes ("levels", finest first). FBLIGHT1 (converter r6) has one level, FBLIGHT2 (r7) the
    /// lightmap rectangle at about 1/1, 1/2 and 1/4 the side, so stadiums whose atlas has less room take a coarser one.
    /// No Frosty types here (the offline test compiles this file as it is).
    /// </summary>
    public sealed class FbLight
    {
        public sealed class Level
        {
            /// <summary>Lightmap rectangle = Enlighten system output (1x texels, multiples of 4).</summary>
            public int Width, Height;
            public float MetersPerTexel;
            public int Budget, Workspace, Pixels, Segments, Reps;
            public byte[] Gesg;
            public int SkyW, SkyH, IrrW, IrrH, DirW, DirH;
            public byte[] SkyBc7, IrrBc6h, DirBc1;
            public long Area { get { return (long)Width * Height; } }
        }

        public float[] BoxMin = new float[3], BoxMax = new float[3];
        public byte[] SystemGuid;
        public readonly List<Level> Levels = new List<Level>();

        public static FbLight Read(string path)
        {
            string name = Path.GetFileName(path);
            using (BinaryReader r = new BinaryReader(File.OpenRead(path)))
            {
                string magic = Encoding.ASCII.GetString(r.ReadBytes(8));
                FbLight l = new FbLight();
                if (magic == "FBLIGHT1")
                {
                    uint version = r.ReadUInt32();
                    if (version != 1) throw new InvalidDataException(name + ": version " + version);
                    int w = (int)r.ReadUInt32(), h = (int)r.ReadUInt32();
                    float mpt = r.ReadSingle();
                    ReadBox(r, l);
                    l.Levels.Add(ReadLevel(r, name, w, h, mpt));
                }
                else if (magic == "FBLIGHT2")
                {
                    uint version = r.ReadUInt32();
                    if (version != 2) throw new InvalidDataException(name + ": version " + version);
                    uint count = r.ReadUInt32();
                    if (count < 1 || count > 8) throw new InvalidDataException(name + ": " + count + " levels");
                    ReadBox(r, l);
                    for (int i = 0; i < count; i++)
                    {
                        int w = (int)r.ReadUInt32(), h = (int)r.ReadUInt32();
                        float mpt = r.ReadSingle();
                        l.Levels.Add(ReadLevel(r, name, w, h, mpt));
                    }
                }
                else
                    throw new InvalidDataException(name + ": not an FBLIGHT1/FBLIGHT2 file");
                if (r.BaseStream.Position != r.BaseStream.Length)
                    throw new InvalidDataException(name + ": " + (r.BaseStream.Length - r.BaseStream.Position) + " bytes after the last level");
                for (int i = 1; i < l.Levels.Count; i++)
                    if (l.Levels[i].Area >= l.Levels[i - 1].Area)
                        throw new InvalidDataException(name + ": level " + i + " is not smaller than level " + (i - 1));
                return l;
            }
        }

        private static void ReadBox(BinaryReader r, FbLight l)
        {
            for (int i = 0; i < 3; i++) l.BoxMin[i] = r.ReadSingle();
            for (int i = 0; i < 3; i++) l.BoxMax[i] = r.ReadSingle();
            l.SystemGuid = r.ReadBytes(16);
        }

        private static Level ReadLevel(BinaryReader r, string name, int w, int h, float mpt)
        {
            Level v = new Level { Width = w, Height = h, MetersPerTexel = mpt };
            v.Budget = (int)r.ReadUInt32(); v.Workspace = (int)r.ReadUInt32(); v.Pixels = (int)r.ReadUInt32(); v.Segments = (int)r.ReadUInt32(); v.Reps = (int)r.ReadUInt32();
            v.Gesg = r.ReadBytes((int)r.ReadUInt32());
            v.SkyW = (int)r.ReadUInt32(); v.SkyH = (int)r.ReadUInt32(); v.SkyBc7 = r.ReadBytes((int)r.ReadUInt32());
            v.IrrW = (int)r.ReadUInt32(); v.IrrH = (int)r.ReadUInt32(); v.IrrBc6h = r.ReadBytes((int)r.ReadUInt32());
            v.DirW = (int)r.ReadUInt32(); v.DirH = (int)r.ReadUInt32(); v.DirBc1 = r.ReadBytes((int)r.ReadUInt32());
            if (w <= 0 || h <= 0 || w % 4 != 0 || h % 4 != 0 || v.SkyW != 4 * w || v.SkyH != 4 * h || v.IrrW != 5 * w || v.IrrH != 5 * h || v.DirW != 5 * w || v.DirH != 5 * h)
                throw new InvalidDataException(name + ": unexpected sizes (" + w + "x" + h + ")");
            if (v.SkyBc7.Length != v.SkyW / 4 * (v.SkyH / 4) * 16 || v.IrrBc6h.Length != v.IrrW / 4 * (v.IrrH / 4) * 16 || v.DirBc1.Length != v.DirW / 4 * (v.DirH / 4) * 8)
                throw new InvalidDataException(name + ": texture block counts do not match " + w + "x" + h);
            if (v.Gesg.Length < 32 || BitConverter.ToUInt32(v.Gesg, 0) != 0x47534547 || BitConverter.ToUInt16(v.Gesg, 16) != w || BitConverter.ToUInt16(v.Gesg, 18) != h || BitConverter.ToInt32(v.Gesg, 24) != v.Pixels)
                throw new InvalidDataException(name + ": GESG does not match " + w + "x" + h);
            return v;
        }
    }
}
