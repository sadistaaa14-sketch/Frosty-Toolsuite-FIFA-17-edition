// FIFA 17 Animation Browser Plugin — AssetBank Parser
// Reads fifagame_win32_antstate.res to build the full animation catalog.
// Two-pass approach: first index all keyed sections, then parse ClipControllers.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace AnimationBrowserPlugin
{
    public class AssetBankParser
    {
        // Section type hashes
        const uint TYPE_VBR           = 0xa741267c;  // VbrAnimationAsset
        const uint TYPE_CLIP          = 0x638256e9;  // ClipControllerAsset
        const uint TYPE_CTD           = 0x2119b0e2;  // ChannelToDofAsset
        const uint TYPE_LAYOUT_HIER   = 0xd8cf1f97;  // LayoutHierarchyAsset
        const uint TYPE_LAYOUT        = 0xbb267d6a;  // LayoutAsset
        const uint TYPE_SKELETON      = 0x1c0ccfd6;  // SkeletonAsset

        // Section index: key → (fileOffset, typeHash) for quick lookup
        private Dictionary<long, (long offset, uint typeHash)> _keyIndex;
        // Type index: typeHash → list of (fileOffset, key)
        private Dictionary<uint, List<(long offset, long key)>> _typeIndex;

        public List<AnimationEntry> Entries { get; private set; }
        public CategoryNode RootCategory { get; private set; }
        public int TotalSections { get; private set; }

        /// <summary>Parse the AssetBank and build the animation catalog.</summary>
        public void Parse(string resPath, Action<string> log = null)
        {
            _keyIndex = new Dictionary<long, (long, uint)>();
            _typeIndex = new Dictionary<uint, List<(long, long)>>();
            Entries = new List<AnimationEntry>();

            log?.Invoke("Pass 1: Indexing sections...");
            IndexSections(resPath, log);

            log?.Invoke($"Pass 2: Parsing {CountType(TYPE_CLIP)} ClipControllerAssets...");
            ParseClipControllers(resPath, log);

            log?.Invoke("Building category tree...");
            RootCategory = BuildCategoryTree();

            log?.Invoke($"Done: {Entries.Count} animations ({Entries.Count(e => e.IsLocal)} local)");
        }

        private int CountType(uint th) =>
            _typeIndex.ContainsKey(th) ? _typeIndex[th].Count : 0;

        // ─── Pass 1: Scan all GD.DATAb sections ─────────────────
        private void IndexSections(string resPath, Action<string> log)
        {
            // Key types we need to index for cross-referencing
            var keyTypes = new HashSet<uint> { TYPE_VBR, TYPE_CLIP, TYPE_CTD, TYPE_LAYOUT_HIER, TYPE_LAYOUT, TYPE_SKELETON };

            // Key offsets per type (where the 8-byte __key lives relative to object start)
            // Determined from REFL field descriptors
            var keyOffsets = new Dictionary<uint, int>
            {
                { TYPE_VBR,         0x48 },  // VbrAnimationAsset.__key
                { TYPE_CLIP,        0x10 },  // ClipControllerAsset.__key
                { TYPE_CTD,         0x28 },  // ChannelToDofAsset.__key
                { TYPE_LAYOUT_HIER, 0x38 },  // LayoutHierarchyAsset.__key
                { TYPE_LAYOUT,      0x28 },  // LayoutAsset.__key
                { TYPE_SKELETON,    0x28 },  // SkeletonAsset.__key
            };

            using (var fs = new FileStream(resPath, FileMode.Open, FileAccess.Read))
            using (var br = new BinaryReaderBE(fs))
            {
                // Find first GD.DATAb section
                long dataStart = FindFirstSection(fs);
                fs.Position = dataStart;

                int count = 0;
                var magic = Encoding.ASCII.GetBytes("GD.DATAb");

                while (fs.Position < fs.Length - 16)
                {
                    long pos = fs.Position;
                    var tag = br.ReadBytes(8);
                    if (!tag.SequenceEqual(magic)) break;

                    uint sectionSize = br.ReadUInt32BE();
                    uint dataSize = br.ReadUInt32BE();
                    long dataOffset = fs.Position; // do

                    // Read type hash at do+0x14
                    fs.Position = dataOffset + 0x14;
                    uint typeHash = br.ReadUInt32BE();

                    // Index keyed types
                    if (keyTypes.Contains(typeHash) && keyOffsets.ContainsKey(typeHash))
                    {
                        // Object offset at do+0x1C (always 0x0020)
                        fs.Position = dataOffset + 0x1C;
                        ushort objOff = br.ReadUInt16BE();
                        long objStart = dataOffset + objOff;

                        // Read __key at the type-specific offset
                        int keyOff = keyOffsets[typeHash];
                        if (keyOff + 8 <= sectionSize - 16)
                        {
                            fs.Position = objStart + keyOff;
                            long key = br.ReadInt64BE();
                            if (key != 0)
                            {
                                _keyIndex[key] = (pos, typeHash);
                            }
                        }

                        if (!_typeIndex.ContainsKey(typeHash))
                            _typeIndex[typeHash] = new List<(long, long)>();
                        _typeIndex[typeHash].Add((pos, 0)); // key stored in _keyIndex
                    }
                    else
                    {
                        // Still track counts for non-keyed types
                        if (!_typeIndex.ContainsKey(typeHash))
                            _typeIndex[typeHash] = new List<(long, long)>();
                        _typeIndex[typeHash].Add((pos, 0));
                    }

                    count++;
                    fs.Position = pos + sectionSize;
                }

                TotalSections = count;
                log?.Invoke($"  Indexed {count} sections, {_keyIndex.Count} keyed objects, {_typeIndex.Count} types");
            }
        }

        // ─── Pass 2: Parse ClipControllerAssets ─────────────────
        private void ParseClipControllers(string resPath, Action<string> log)
        {
            if (!_typeIndex.ContainsKey(TYPE_CLIP)) return;

            using (var fs = new FileStream(resPath, FileMode.Open, FileAccess.Read))
            using (var br = new BinaryReaderBE(fs))
            {
                int processed = 0;
                foreach (var (ccaPos, _) in _typeIndex[TYPE_CLIP])
                {
                    var entry = ParseOneClip(fs, br, ccaPos);
                    if (entry != null)
                        Entries.Add(entry);

                    processed++;
                    if (processed % 5000 == 0)
                        log?.Invoke($"  Parsed {processed} clips...");
                }
            }
        }

        private AnimationEntry ParseOneClip(FileStream fs, BinaryReaderBE br, long ccaPos)
        {
            // Read section header
            fs.Position = ccaPos + 8;
            uint ds = br.ReadUInt32BE();
            br.ReadUInt32BE(); // data_size
            long dataOff = fs.Position;

            // Object offset
            fs.Position = dataOff + 0x1C;
            ushort oo = br.ReadUInt16BE();
            long obj = dataOff + oo;

            // __name string ref: [count:4][size:4][offset:8]
            fs.Position = obj;
            uint nameCount = br.ReadUInt32BE();
            uint nameSize = br.ReadUInt32BE();
            long nameOffset = br.ReadInt64BE();

            string name = "";
            if (nameCount == nameSize && nameSize > 0 && nameSize < 256)
            {
                fs.Position = dataOff + nameOffset;
                var nameBytes = br.ReadBytes((int)nameSize);
                name = Encoding.ASCII.GetString(nameBytes).TrimEnd('\0');
            }

            // Anim key at obj+0x20
            fs.Position = obj + 0x20;
            long animKey = br.ReadInt64BE();

            // FPS at obj+0x2C
            fs.Position = obj + 0x2C;
            float fps = br.ReadSingleBE();

            // TimeScale at obj+0x30
            fs.Position = obj + 0x30;
            float timeScale = br.ReadSingleBE();

            // Distance at obj+0x34
            fs.Position = obj + 0x34;
            float distance = br.ReadSingleBE();

            // Modes at obj+0x38
            fs.Position = obj + 0x38;
            uint modes = br.ReadUInt32BE();

            // Resolve VBR animation
            long vbrOffset = 0;
            VbrConfig config = default;
            int sectionSize = 0;

            if (_keyIndex.TryGetValue(animKey, out var target) && target.typeHash == TYPE_VBR)
            {
                vbrOffset = target.offset;

                // Read VBR config
                fs.Position = vbrOffset + 8;
                sectionSize = (int)br.ReadUInt32BE();
                br.ReadUInt32BE(); // data_size
                long vbrDo = fs.Position;

                fs.Position = vbrDo + 0x1C;
                ushort voo = br.ReadUInt16BE();
                long vobj = vbrDo + voo;

                fs.Position = vobj + 0x7C;
                config.Q   = br.ReadUInt16BE();
                config.V3  = br.ReadUInt16BE();
                config.F   = br.ReadUInt16BE();
                config.CQ  = br.ReadUInt16BE();
                config.CV3 = br.ReadUInt16BE();
                config.CF  = br.ReadUInt16BE();
                config.KtsSize  = br.ReadUInt16BE();
                config.NumKeys  = br.ReadUInt16BE();
                // skip CCMS, CPS, VOS, FOS
                fs.Position = vobj + 0x94;
                config.Flags = br.ReadUInt16BE();
            }

            // Categorize
            var (cat, sub) = Categorize(name);

            return new AnimationEntry
            {
                Name = name,
                Category = cat,
                Subcategory = sub,
                CcaOffset = ccaPos,
                VbrOffset = vbrOffset,
                FPS = fps,
                TimeScale = timeScale,
                Distance = distance,
                Modes = modes,
                Config = config,
                SectionSize = sectionSize,
            };
        }

        // ─── Find first GD.DATAb section ────────────────────────
        private long FindFirstSection(FileStream fs)
        {
            // Known location from file structure analysis
            // Header(0x40) + IndexTable(458349*16) + SecondaryIndex + GD.STRMb + GD.REFLb
            // First GD.DATAb at 0x0078E630
            // But scan to be safe
            var magic = Encoding.ASCII.GetBytes("GD.DATAb");
            byte[] buf = new byte[8];

            // Try known offset first
            fs.Position = 0x0078E630;
            fs.Read(buf, 0, 8);
            if (buf.SequenceEqual(magic)) return 0x0078E630;

            // Fallback: scan from ~7MB mark
            fs.Position = 0x00700000;
            var chunk = new byte[0x100000];
            int read = fs.Read(chunk, 0, chunk.Length);
            for (int i = 0; i < read - 8; i++)
            {
                bool match = true;
                for (int j = 0; j < 8; j++)
                    if (chunk[i + j] != magic[j]) { match = false; break; }
                if (match) return 0x00700000 + i;
            }

            throw new InvalidDataException("Could not find GD.DATAb sections in AssetBank");
        }

        // ─── Animation Categorizer ──────────────────────────────
        public static (string category, string subcategory) Categorize(string name)
        {
            string nl = name.ToLower();

            if (ContainsAny(nl, "face", "_face", "fp_", "faceposer", "facial"))
                return ("Facial", "General");

            if (ContainsAny(nl, "crowd", "crw_"))
                return ("Crowd", "General");

            if (Regex.IsMatch(nl, @"sc_\d{4}") || nl.Contains("ss_"))
            {
                var m = Regex.Match(name, @"SC_(\d{4})");
                if (m.Success) return ("Cutscene", $"Scene_{m.Groups[1].Value}");
                if (nl.Contains("ss_")) return ("Cutscene", "StoryScene");
                return ("Cutscene", "Other");
            }

            if (ContainsAny(nl, "gk_", "keeper", "save_", "deflect", "dive"))
            {
                if (ContainsAny(nl, "save", "deflect", "dive", "catch", "punch")) return ("Goalkeeper", "Saves");
                if (ContainsAny(nl, "pk_", "penalty")) return ("Goalkeeper", "PenaltyKick");
                if (ContainsAny(nl, "idle", "stance", "jog", "sidestep")) return ("Goalkeeper", "Movement");
                return ("Goalkeeper", "Other");
            }

            // Gameplay subcategories
            var cats = new (string name, string[] keywords)[]
            {
                ("Celebrations", new[] { "celeb", "ucc_end", "knee_slide", "somersault", "dance", "fist_pump", "backflip", "cradle", "windmill" }),
                ("Shooting", new[] { "shot_", "shoot", "volley", "bicycle", "chip_shot", "sidefoot", "finesse" }),
                ("Passing", new[] { "pass_", "stretchpass", "throughball", "cross_", "layoff", "backheel_pass" }),
                ("Trapping", new[] { "trap_", "trapping", "chest_trap", "first_touch" }),
                ("Headers", new[] { "header", "heading" }),
                ("Dribbling", new[] { "dribble", "touch_cycle", "shield", "knock_on", "drag", "feint" }),
                ("Tackles", new[] { "tackle", "slide_t", "slidet", "block_", "intercept" }),
                ("SkillMoves", new[] { "skill", "stepover", "roulette", "rainbow", "elastico", "flip_flap", "heel_chop", "ball_roll", "tatw" }),
                ("Locomotion", new[] { "jog", "sprint", "run_", "walk_", "sidestep", "backpedal", "turn_", "decel", "accel", "loco_", "cycle_" }),
                ("Idle", new[] { "idle", "stand_", "ready", "stance", "breathing" }),
                ("SetPieces", new[] { "freekick", "fk_", "corner", "penalty", "pk_", "throw_in", "kickoff" }),
                ("PhysicalPlay", new[] { "jostle", "push", "charge", "collision", "foul", "physical" }),
                ("Reactions", new[] { "react", "emotion", "ee_", "disappoint", "angry", "injury" }),
                ("Falls_Getups", new[] { "getup", "fall", "landing", "tumble" }),
                ("Officials", new[] { "ref_", "referee", "linesman", "official", "manager", "bench", "sideline" }),
                ("Presentation", new[] { "kit_select", "preview", "entrance", "tunnel" }),
            };

            foreach (var (catName, keywords) in cats)
                if (ContainsAny(nl, keywords))
                    return ("Gameplay", catName);

            return ("Gameplay", "Other");
        }

        private static bool ContainsAny(string s, params string[] terms)
        {
            foreach (var t in terms)
                if (s.Contains(t)) return true;
            return false;
        }

        // ─── Build Category Tree ────────────────────────────────
        private CategoryNode BuildCategoryTree()
        {
            var root = new CategoryNode { Name = "Animations" };
            var catMap = new Dictionary<string, CategoryNode>();

            foreach (var entry in Entries)
            {
                string catKey = entry.Category;
                string subKey = $"{catKey}/{entry.Subcategory}";

                if (!catMap.TryGetValue(catKey, out var catNode))
                {
                    catNode = new CategoryNode { Name = catKey };
                    catMap[catKey] = catNode;
                    root.Children.Add(catNode);
                }

                if (!catMap.TryGetValue(subKey, out var subNode))
                {
                    subNode = new CategoryNode { Name = entry.Subcategory };
                    catMap[subKey] = subNode;
                    catNode.Children.Add(subNode);
                }

                subNode.Entries.Add(entry);
                subNode.TotalCount++;
                if (entry.IsLocal) subNode.LocalCount++;

                catNode.TotalCount++;
                if (entry.IsLocal) catNode.LocalCount++;

                root.TotalCount++;
                if (entry.IsLocal) root.LocalCount++;
            }

            // Sort children
            root.Children = root.Children.OrderBy(c => c.Name).ToList();
            foreach (var c in root.Children)
                c.Children = c.Children.OrderBy(s => s.Name).ToList();

            return root;
        }

        // ─── Resolve ChannelToDof for a VBR section ─────────────
        public ushort[] ResolveDofIds(string resPath, long vbrOffset)
        {
            using (var fs = new FileStream(resPath, FileMode.Open, FileAccess.Read))
            using (var br = new BinaryReaderBE(fs))
            {
                // Read VBR section
                fs.Position = vbrOffset + 8;
                uint ds = br.ReadUInt32BE();
                br.ReadUInt32BE();
                long dataOff = fs.Position;

                fs.Position = dataOff + 0x1C;
                ushort oo = br.ReadUInt16BE();
                long obj = dataOff + oo;

                // __base at obj+0x40 → embedded AnimationAsset offset
                fs.Position = obj + 0x40;
                long baseRef = br.ReadInt64BE();
                if (baseRef == 0) return null;

                // Embedded AnimationAsset at dataOff + baseRef
                long embDo = dataOff + baseRef;
                fs.Position = embDo + 0x1C;
                ushort embOo = br.ReadUInt16BE();
                long embObj = embDo + embOo;

                // ChannelToDofAsset key at embObj+0x20
                fs.Position = embObj + 0x20;
                long ctdKey = br.ReadInt64BE();

                if (!_keyIndex.TryGetValue(ctdKey, out var ctdTarget))
                    return null;

                // Read CTD DofIds array
                long ctdPos = ctdTarget.offset;
                fs.Position = ctdPos + 8;
                uint ctdDs = br.ReadUInt32BE();
                br.ReadUInt32BE();
                long ctdDo = fs.Position;

                fs.Position = ctdDo + 0x1C;
                ushort ctdOo = br.ReadUInt16BE();
                long ctdObj = ctdDo + ctdOo;

                // Array ref at ctdObj+0: [cap:4][size:4][offset:8]
                fs.Position = ctdObj;
                uint cap = br.ReadUInt32BE();
                uint size = br.ReadUInt32BE();
                long arrOff = br.ReadInt64BE();

                fs.Position = ctdDo + arrOff;
                var dofIds = new ushort[size];
                for (int i = 0; i < size; i++)
                    dofIds[i] = br.ReadUInt16BE();

                return dofIds;
            }
        }
    }

    /// <summary>Big-endian binary reader (Frostbite uses BE for AssetBank data).</summary>
    public class BinaryReaderBE : IDisposable
    {
        private readonly Stream _s;
        private readonly byte[] _buf = new byte[8];

        public BinaryReaderBE(Stream stream) { _s = stream; }

        public byte[] ReadBytes(int count)
        {
            var buf = new byte[count];
            _s.Read(buf, 0, count);
            return buf;
        }

        public ushort ReadUInt16BE()
        {
            _s.Read(_buf, 0, 2);
            return (ushort)((_buf[0] << 8) | _buf[1]);
        }

        public uint ReadUInt32BE()
        {
            _s.Read(_buf, 0, 4);
            return (uint)((_buf[0] << 24) | (_buf[1] << 16) | (_buf[2] << 8) | _buf[3]);
        }

        public long ReadInt64BE()
        {
            _s.Read(_buf, 0, 8);
            return ((long)_buf[0] << 56) | ((long)_buf[1] << 48) | ((long)_buf[2] << 40) |
                   ((long)_buf[3] << 32) | ((long)_buf[4] << 24) | ((long)_buf[5] << 16) |
                   ((long)_buf[6] << 8) | _buf[7];
        }

        public float ReadSingleBE()
        {
            _s.Read(_buf, 0, 4);
            if (BitConverter.IsLittleEndian)
                Array.Reverse(_buf, 0, 4);
            return BitConverter.ToSingle(_buf, 0);
        }

        public void Dispose() { }
    }
}
