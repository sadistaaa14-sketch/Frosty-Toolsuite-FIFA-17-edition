using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace DuplicationPlugin
{
    /// <summary>
    /// A stadium package written by the PES 2021 -> FIFA 17 converter (PesStadiumConverter): stadium.json next
    /// to meshes/&lt;slot&gt;.fbm (FBMESH01) and textures/*.dds.
    /// </summary>
    public class PesStadiumPackage
    {
        public const string Format = "pes2fifa17-stadium";

        public class SectionInfo
        {
            public int Index;
            public string Material = "";
            public string Class = "opaque";      // opaque, alphatest, transparent, emissive
            public bool TwoSided;
            /// <summary>The colour texture carries an alpha cut-out (an emissive section with alpha gets the alpha-test preset).</summary>
            public bool Alpha;
            public string ColorTexture = "";     // package relative paths (textures/x.dds) or ""
            public string NormalTexture = "";
            public string CoefficientTexture = "";
            public int Vertices;
            public int Triangles;
            /// <summary>"" (plain), "dugout" (PES furniture in the FIFA bench zone), "runoff" (PES grass around the FIFA pitch), "ground" (flat apron sheet).</summary>
            public string Role = "";
            /// <summary>The PES material instance names merged into the section and the PES model groups it came from (stadium.json pes_instances / sources).</summary>
            public List<string> PesInstances = new List<string>();
            public List<string> Sources = new List<string>();
        }

        public class CrowdInfo
        {
            public string File = "";             // crowd.bin
            public int Count;
            public float SeatPitch, Setback;
        }

        public class PhysicsInfo
        {
            public string Material = "";         // concrete, ...
            public string File = "";             // physics/<material>.bin
            public int Triangles;
        }

        public class SlotInfo
        {
            public string Name = "";             // standslower, standsupper, roof, exterior, pitch, snow, rain, night
            public string File = "";             // meshes/<slot>.fbm
            public List<string> FifaMeshCandidates = new List<string>();
            /// <summary>A weather / time-of-day layer: shown by a sublevel, no part of the walkable surface.</summary>
            public bool Overlay;
            /// <summary>Where a new component mesh goes when the stadium has none of the candidates: the sublevel ("" = main level), the new mesh's name, the component mesh to copy.</summary>
            public string HostSublevel = "";
            public string NewMeshName = "";
            public string TemplateMesh = "";
            public bool CanCreateMesh => !string.IsNullOrEmpty(NewMeshName);
            public List<SectionInfo> Sections = new List<SectionInfo>();
            public int Vertices;
            public int Triangles;
            /// <summary>World bounds of the slot's geometry (min x y z, max x y z) from stadium.json; null when absent.</summary>
            public float[] Bounds;
        }

        public class TextureInfo
        {
            public string Path = "";
            public string Format = "";
            public bool Srgb;
            public int Width, Height, Mips;
        }

        public string Directory = "";
        public string StadiumId = "";
        public string StadiumName = "";
        public string Game = "";
        public DateTime Created;
        public List<SlotInfo> Slots = new List<SlotInfo>();
        public Dictionary<string, TextureInfo> Textures = new Dictionary<string, TextureInfo>(StringComparer.OrdinalIgnoreCase);
        public List<string> Warnings = new List<string>();
        public CrowdInfo Crowd;                  // null when the package has no crowd
        public List<PhysicsInfo> Physics = new List<PhysicsInfo>();
        /// <summary>Floodlight lamp heads of the PES stadium (FIFA space, x y z per lamp); empty when none were found.</summary>
        public List<float[]> Lamps = new List<float[]>();
        public int Version;

        public static PesStadiumPackage Load(string stadiumJsonPath)
        {
            string text = File.ReadAllText(stadiumJsonPath, Encoding.UTF8);
            object root = MiniJson.Parse(text);
            Dictionary<string, object> doc = root as Dictionary<string, object>;
            if (doc == null)
                throw new InvalidDataException("stadium.json: not a JSON object");
            if (Str(doc, "format") != Format)
                throw new InvalidDataException("stadium.json: format is '" + Str(doc, "format") + "', expected '" + Format + "'");

            PesStadiumPackage pkg = new PesStadiumPackage { Directory = Path.GetDirectoryName(Path.GetFullPath(stadiumJsonPath)) };
            Dictionary<string, object> source = Get(doc, "source") as Dictionary<string, object>;
            if (source != null)
            {
                pkg.StadiumId = Str(source, "stadium_id");
                pkg.StadiumName = Str(source, "name");
                pkg.Game = Str(source, "game");
            }
            DateTime created;
            if (DateTime.TryParse(Str(doc, "created"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out created))
                pkg.Created = created;

            Dictionary<string, object> slots = Get(doc, "slots") as Dictionary<string, object>;
            if (slots != null)
            {
                foreach (KeyValuePair<string, object> kv in slots)
                {
                    Dictionary<string, object> so = kv.Value as Dictionary<string, object>;
                    if (so == null)
                        continue;
                    SlotInfo slot = new SlotInfo { Name = kv.Key, File = Str(so, "file"), Vertices = Int(so, "vertices"), Triangles = Int(so, "triangles") };
                    List<object> cand = Get(so, "fifa_mesh_candidates") as List<object>;
                    if (cand != null)
                        foreach (object c in cand)
                            slot.FifaMeshCandidates.Add(Convert.ToString(c));
                    slot.Overlay = Bool(so, "overlay");
                    List<object> bounds = Get(so, "bounds") as List<object>;
                    if (bounds != null && bounds.Count == 2)
                    {
                        List<object> bmin = bounds[0] as List<object>, bmax = bounds[1] as List<object>;
                        if (bmin != null && bmax != null && bmin.Count == 3 && bmax.Count == 3)
                        {
                            slot.Bounds = new float[6];
                            for (int i = 0; i < 3; i++)
                            {
                                slot.Bounds[i] = Convert.ToSingle(bmin[i], CultureInfo.InvariantCulture);
                                slot.Bounds[3 + i] = Convert.ToSingle(bmax[i], CultureInfo.InvariantCulture);
                            }
                        }
                    }
                    Dictionary<string, object> host = Get(so, "fifa_host") as Dictionary<string, object>;
                    if (host != null)
                    {
                        slot.HostSublevel = Str(host, "sublevel");
                        slot.NewMeshName = Str(host, "new_mesh");
                        slot.TemplateMesh = Str(host, "template");
                    }
                    List<object> sections = Get(so, "sections") as List<object>;
                    if (sections != null)
                    {
                        foreach (object s in sections)
                        {
                            Dictionary<string, object> sd = s as Dictionary<string, object>;
                            if (sd == null)
                                continue;
                            SectionInfo si = new SectionInfo
                            {
                                Index = Int(sd, "index"),
                                Material = Str(sd, "material"),
                                Class = Str(sd, "class"),
                                TwoSided = Bool(sd, "two_sided"),
                                Alpha = Bool(sd, "alpha"),
                                Vertices = Int(sd, "vertices"),
                                Triangles = Int(sd, "triangles"),
                                Role = Str(sd, "role")
                            };
                            List<object> inst = Get(sd, "pes_instances") as List<object>;
                            if (inst != null) foreach (object o in inst) si.PesInstances.Add(Convert.ToString(o, CultureInfo.InvariantCulture));
                            List<object> srcs = Get(sd, "sources") as List<object>;
                            if (srcs != null) foreach (object o in srcs) si.Sources.Add(Convert.ToString(o, CultureInfo.InvariantCulture));
                            Dictionary<string, object> tex = Get(sd, "textures") as Dictionary<string, object>;
                            if (tex != null)
                            {
                                si.ColorTexture = Str(tex, "color");
                                si.NormalTexture = Str(tex, "normal");
                                si.CoefficientTexture = Str(tex, "coefficient");
                            }
                            slot.Sections.Add(si);
                        }
                    }
                    pkg.Slots.Add(slot);
                }
            }

            Dictionary<string, object> textures = Get(doc, "textures") as Dictionary<string, object>;
            if (textures != null)
            {
                foreach (KeyValuePair<string, object> kv in textures)
                {
                    Dictionary<string, object> td = kv.Value as Dictionary<string, object>;
                    if (td == null)
                        continue;
                    pkg.Textures[kv.Key] = new TextureInfo
                    {
                        Path = kv.Key, Format = Str(td, "format"), Srgb = Bool(td, "srgb"),
                        Width = Int(td, "width"), Height = Int(td, "height"), Mips = Int(td, "mips")
                    };
                }
            }

            List<object> warnings = Get(doc, "warnings") as List<object>;
            if (warnings != null)
                foreach (object w in warnings)
                    pkg.Warnings.Add(Convert.ToString(w));

            pkg.Version = Int(doc, "version");
            Dictionary<string, object> crowd = Get(doc, "crowd") as Dictionary<string, object>;
            if (crowd != null && Int(crowd, "count") > 0)
                pkg.Crowd = new CrowdInfo { File = Str(crowd, "file"), Count = Int(crowd, "count"), SeatPitch = Flt(crowd, "seat_pitch"), Setback = Flt(crowd, "setback") };
            Dictionary<string, object> lamps = Get(doc, "lamps") as Dictionary<string, object>;
            if (lamps != null)
            {
                List<object> positions = Get(lamps, "positions") as List<object>;
                if (positions != null)
                {
                    foreach (object o in positions)
                    {
                        List<object> xyz = o as List<object>;
                        if (xyz == null || xyz.Count < 3)
                            continue;
                        pkg.Lamps.Add(new float[] { Convert.ToSingle(xyz[0], CultureInfo.InvariantCulture), Convert.ToSingle(xyz[1], CultureInfo.InvariantCulture), Convert.ToSingle(xyz[2], CultureInfo.InvariantCulture) });
                    }
                }
            }
            Dictionary<string, object> physics = Get(doc, "physics") as Dictionary<string, object>;
            if (physics != null)
            {
                foreach (KeyValuePair<string, object> kv in physics)
                {
                    Dictionary<string, object> pd = kv.Value as Dictionary<string, object>;
                    if (pd == null)
                        continue;
                    pkg.Physics.Add(new PhysicsInfo { Material = kv.Key, File = Str(pd, "file"), Triangles = Int(pd, "triangles") });
                }
            }

            return pkg;
        }

        public string FullPath(string packageRelative)
        {
            return Path.Combine(Directory, packageRelative.Replace('/', Path.DirectorySeparatorChar));
        }

        private static object Get(Dictionary<string, object> d, string key)
        {
            object v;
            return d.TryGetValue(key, out v) ? v : null;
        }
        private static string Str(Dictionary<string, object> d, string key)
        {
            object v = Get(d, key);
            return v == null ? "" : Convert.ToString(v, CultureInfo.InvariantCulture);
        }
        private static int Int(Dictionary<string, object> d, string key)
        {
            object v = Get(d, key);
            if (v == null) return 0;
            try { return Convert.ToInt32(v, CultureInfo.InvariantCulture); } catch (Exception) { return 0; }
        }
        private static bool Bool(Dictionary<string, object> d, string key)
        {
            object v = Get(d, key);
            return v is bool && (bool)v;
        }
        private static float Flt(Dictionary<string, object> d, string key)
        {
            object v = Get(d, key);
            if (v == null) return 0f;
            try { return Convert.ToSingle(v, CultureInfo.InvariantCulture); } catch (Exception) { return 0f; }
        }
    }

    /// <summary>One spectator of crowd.bin (FBCROWD1), FIFA world space.</summary>
    public struct CrowdPointRecord
    {
        public float X, Y, Z, OrientationDeg, R, G, B;
        public ushort Tier;
        public byte Row, Source;     // source: 0 terrace, 1 seat mesh, 2 PES audience area
        /// <summary>PES home/away mix of the point's area (0.1 ultra away .. 0.5 neutral .. 1.0 ultra home); 0.5 for FBCROWD1 files.</summary>
        public float HomeMix;
    }

    /// <summary>
    /// crowd.bin: "FBCROWD2" (or the older "FBCROWD1"), u32 count, f32 seat pitch, f32 setback; per point 36 (32) bytes: f32 x y z, f32 orientation
    /// (yaw about +Y, 0 = +X, facing (cos a, 0, -sin a)), f32 r g b (SeatColor sRGB), u16 tier, u8 row, u8 source.
    /// </summary>
    public static class CrowdReader
    {
        public static List<CrowdPointRecord> Read(string path, out float seatPitch, out float setback)
        {
            using (BinaryReader r = new BinaryReader(File.OpenRead(path)))
            {
                string magic = Encoding.ASCII.GetString(r.ReadBytes(8));
                if (magic != "FBCROWD1" && magic != "FBCROWD2")
                    throw new InvalidDataException(path + ": not a crowd.bin (" + magic + ")");
                bool v2 = magic == "FBCROWD2";   // 36-byte points: + f32 home mix
                uint n = r.ReadUInt32();
                seatPitch = r.ReadSingle();
                setback = r.ReadSingle();
                List<CrowdPointRecord> list = new List<CrowdPointRecord>((int)n);
                for (uint i = 0; i < n; i++)
                {
                    CrowdPointRecord p = new CrowdPointRecord();
                    p.X = r.ReadSingle(); p.Y = r.ReadSingle(); p.Z = r.ReadSingle();
                    p.OrientationDeg = r.ReadSingle();
                    p.R = r.ReadSingle(); p.G = r.ReadSingle(); p.B = r.ReadSingle();
                    p.Tier = r.ReadUInt16(); p.Row = r.ReadByte(); p.Source = r.ReadByte();
                    p.HomeMix = v2 ? r.ReadSingle() : 0.5f;
                    list.Add(p);
                }
                return list;
            }
        }
    }

    /// <summary>One section of an FBMESH01 file.</summary>
    public class FbmSection
    {
        public string Name = "";
        public int MaterialId;
        public uint Flags;              // bit0 opaque, bit1 transparent, bit2 transparent decal, bit3 z-only, bit4 shadow
        public float[] Positions;       // x y z per vertex
        public float[] Normals;         // may be null
        public Dictionary<string, float[]> UvSets = new Dictionary<string, float[]>();   // u v per vertex, in file order
        public List<string> UvSetOrder = new List<string>();
        public Dictionary<string, byte[]> ColorSets = new Dictionary<string, byte[]>();
        public uint[] Indices;
        public int VertexCount => Positions.Length / 3;
    }

    /// <summary>
    /// Reader of the FBMESH01 mesh file (the format of the FIFA 17 stadium tools: Blender add-on, Frosty stadium
    /// exporter, PES converter). Little endian: "FBMESH01", u32 section count, per section: u32 name length +
    /// utf-8 name, i32 material id, u32 flags, u32 vertex count, u32 index count, u32 has normals, u32 uv set
    /// count, u32 colour set count, f32 positions, [f32 normals], per uv set (u32 name length + name, f32 x2 per
    /// vertex), per colour set (name, 4 bytes per vertex), u32 indices.
    /// </summary>
    public static class FbmReader
    {
        public static List<FbmSection> Read(string path)
        {
            using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read))
            using (BinaryReader r = new BinaryReader(fs))
            {
                byte[] magic = r.ReadBytes(8);
                if (Encoding.ASCII.GetString(magic) != "FBMESH01")
                    throw new InvalidDataException(Path.GetFileName(path) + ": not an FBMESH01 file");
                uint count = r.ReadUInt32();
                if (count > 4096)
                    throw new InvalidDataException(Path.GetFileName(path) + ": implausible section count " + count);
                List<FbmSection> sections = new List<FbmSection>();
                for (uint s = 0; s < count; s++)
                {
                    FbmSection sec = new FbmSection();
                    sec.Name = ReadString(r);
                    sec.MaterialId = r.ReadInt32();
                    sec.Flags = r.ReadUInt32();
                    uint nv = r.ReadUInt32();
                    uint ni = r.ReadUInt32();
                    uint hasNormals = r.ReadUInt32();
                    uint nUv = r.ReadUInt32();
                    uint nCol = r.ReadUInt32();
                    if (nv > 50_000_000 || ni > 150_000_000 || nUv > 16 || nCol > 16)
                        throw new InvalidDataException(Path.GetFileName(path) + ": implausible counts in section '" + sec.Name + "'");
                    sec.Positions = ReadFloats(r, (int)nv * 3);
                    if (hasNormals != 0)
                        sec.Normals = ReadFloats(r, (int)nv * 3);
                    for (uint u = 0; u < nUv; u++)
                    {
                        string name = ReadString(r);
                        sec.UvSets[name] = ReadFloats(r, (int)nv * 2);
                        sec.UvSetOrder.Add(name);
                    }
                    for (uint c = 0; c < nCol; c++)
                    {
                        string name = ReadString(r);
                        sec.ColorSets[name] = r.ReadBytes((int)nv * 4);
                    }
                    sec.Indices = new uint[ni];
                    byte[] raw = r.ReadBytes((int)ni * 4);
                    if (raw.Length != ni * 4)
                        throw new EndOfStreamException(Path.GetFileName(path) + ": truncated index data in section '" + sec.Name + "'");
                    Buffer.BlockCopy(raw, 0, sec.Indices, 0, raw.Length);
                    sections.Add(sec);
                }
                return sections;
            }
        }

        private static string ReadString(BinaryReader r)
        {
            uint len = r.ReadUInt32();
            if (len > 4096)
                throw new InvalidDataException("implausible string length " + len);
            return Encoding.UTF8.GetString(r.ReadBytes((int)len));
        }

        private static float[] ReadFloats(BinaryReader r, int count)
        {
            float[] result = new float[count];
            byte[] raw = r.ReadBytes(count * 4);
            if (raw.Length != count * 4)
                throw new EndOfStreamException("truncated float data");
            Buffer.BlockCopy(raw, 0, result, 0, raw.Length);
            return result;
        }
    }

    /// <summary>Small JSON reader: objects -> Dictionary&lt;string, object&gt;, arrays -> List&lt;object&gt;, numbers -> double, plus string/bool/null.</summary>
    public static class MiniJson
    {
        public static object Parse(string text)
        {
            int pos = 0;
            object value = ParseValue(text, ref pos);
            SkipWs(text, ref pos);
            if (pos != text.Length)
                throw new FormatException("JSON: trailing characters at " + pos);
            return value;
        }

        private static void SkipWs(string s, ref int i)
        {
            while (i < s.Length && (s[i] == ' ' || s[i] == '\t' || s[i] == '\r' || s[i] == '\n'))
                i++;
        }

        private static object ParseValue(string s, ref int i)
        {
            SkipWs(s, ref i);
            if (i >= s.Length)
                throw new FormatException("JSON: unexpected end");
            char c = s[i];
            if (c == '{')
            {
                i++;
                Dictionary<string, object> obj = new Dictionary<string, object>();
                SkipWs(s, ref i);
                if (i < s.Length && s[i] == '}') { i++; return obj; }
                while (true)
                {
                    SkipWs(s, ref i);
                    if (i >= s.Length || s[i] != '"')
                        throw new FormatException("JSON: expected a key at " + i);
                    string key = ParseString(s, ref i);
                    SkipWs(s, ref i);
                    if (i >= s.Length || s[i] != ':')
                        throw new FormatException("JSON: expected ':' at " + i);
                    i++;
                    obj[key] = ParseValue(s, ref i);
                    SkipWs(s, ref i);
                    if (i < s.Length && s[i] == ',') { i++; continue; }
                    if (i < s.Length && s[i] == '}') { i++; return obj; }
                    throw new FormatException("JSON: expected ',' or '}' at " + i);
                }
            }
            if (c == '[')
            {
                i++;
                List<object> list = new List<object>();
                SkipWs(s, ref i);
                if (i < s.Length && s[i] == ']') { i++; return list; }
                while (true)
                {
                    list.Add(ParseValue(s, ref i));
                    SkipWs(s, ref i);
                    if (i < s.Length && s[i] == ',') { i++; continue; }
                    if (i < s.Length && s[i] == ']') { i++; return list; }
                    throw new FormatException("JSON: expected ',' or ']' at " + i);
                }
            }
            if (c == '"')
                return ParseString(s, ref i);
            if (s.Length - i >= 4 && string.CompareOrdinal(s, i, "true", 0, 4) == 0) { i += 4; return true; }
            if (s.Length - i >= 5 && string.CompareOrdinal(s, i, "false", 0, 5) == 0) { i += 5; return false; }
            if (s.Length - i >= 4 && string.CompareOrdinal(s, i, "null", 0, 4) == 0) { i += 4; return null; }
            int start = i;
            while (i < s.Length && (char.IsDigit(s[i]) || s[i] == '-' || s[i] == '+' || s[i] == '.' || s[i] == 'e' || s[i] == 'E'))
                i++;
            if (start == i)
                throw new FormatException("JSON: unexpected character '" + c + "' at " + i);
            double d;
            if (!double.TryParse(s.Substring(start, i - start), NumberStyles.Float, CultureInfo.InvariantCulture, out d))
                throw new FormatException("JSON: bad number at " + start);
            return d;
        }

        private static string ParseString(string s, ref int i)
        {
            StringBuilder sb = new StringBuilder();
            i++; // opening quote
            while (i < s.Length)
            {
                char c = s[i++];
                if (c == '"')
                    return sb.ToString();
                if (c == '\\')
                {
                    if (i >= s.Length)
                        break;
                    char e = s[i++];
                    switch (e)
                    {
                        case '"': sb.Append('"'); break;
                        case '\\': sb.Append('\\'); break;
                        case '/': sb.Append('/'); break;
                        case 'b': sb.Append('\b'); break;
                        case 'f': sb.Append('\f'); break;
                        case 'n': sb.Append('\n'); break;
                        case 'r': sb.Append('\r'); break;
                        case 't': sb.Append('\t'); break;
                        case 'u':
                            if (i + 4 > s.Length)
                                throw new FormatException("JSON: bad \\u escape");
                            sb.Append((char)int.Parse(s.Substring(i, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                            i += 4;
                            break;
                        default: throw new FormatException("JSON: bad escape '\\" + e + "'");
                    }
                }
                else
                    sb.Append(c);
            }
            throw new FormatException("JSON: unterminated string");
        }
    }
}
