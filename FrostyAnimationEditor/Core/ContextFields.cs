using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace FrostyAnimationEditor.Core
{
    /// <summary>
    /// The engine's context-field registry (Core/Data/context_fields.tsv and context_interfaces.tsv, read out of
    /// the game): the columns a ContextDatabaseAsset can use, with their type, range, unit and value names. A
    /// RegistryFieldAsset of a database names one of them by ClassGuid + FieldId.
    /// </summary>
    public static class ContextFields
    {
        public const uint GameStateGuid = 0x15a9ae49;      // the interface id the engine gives a custom column
        public const uint MetadataGuid = 0x1c491a10;       // ... and a metadata column

        public sealed class Entry
        {
            public uint Guid, FieldId;
            public string Name = "", Unit = "";
            public int Type;
            public long? IntDefault, IntMax;
            public float? Min, Max;
            public List<string> Values = new List<string>();
            /// <summary>bool / enum / int / bitmap: the cell's Int0 holds the value; float kinds: Float0..Float2</summary>
            public bool IsInt { get { return Type == 0 || Type == 1 || Type == 2 || Type == 3; } }
            public string TypeName { get { return KindName(Type); } }
        }

        public static string KindName(int t)
        {
            switch (t)
            {
                case 0: return "bool";
                case 1: return "enum";
                case 2: return "int";
                case 3: return "bitmap";
                case 4: return "float";
                case 5: return "angle";
                case 6: return "float";
                case 7: return "angle";
                case 8: return "three floats";
            }
            return "type " + t;
        }

        private static Dictionary<ulong, Entry> byKey;
        private static Dictionary<uint, string> ifaces;
        private static readonly object gate = new object();

        private static float? Fl(string s)
        {
            float v;
            return s.Length > 0 && float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v) ? (float?)v : null;
        }

        private static long? In(string s)
        {
            long v;
            return s.Length > 0 && long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out v) ? (long?)v : null;
        }

        private static void Load()
        {
            lock (gate)
            {
                if (byKey != null) return;
                Dictionary<ulong, Entry> m = new Dictionary<ulong, Entry>();
                Dictionary<uint, string> f = new Dictionary<uint, string>();
                string text = "", itext = "";
                try { text = CoreData.Text("context_fields.tsv"); } catch (System.IO.FileNotFoundException) { }
                try { itext = CoreData.Text("context_interfaces.tsv"); } catch (System.IO.FileNotFoundException) { }
                foreach (string raw in itext.Split('\n'))
                {
                    string line = raw.TrimEnd('\r');
                    if (line.Length == 0 || line[0] == '#') continue;
                    string[] c = line.Split('\t');
                    if (c.Length < 2) continue;
                    uint g;
                    if (uint.TryParse(c[0].Trim(), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out g)) f[g] = c[1].Trim();
                }
                foreach (string raw in text.Split('\n'))
                {
                    string line = raw.TrimEnd('\r');
                    if (line.Length == 0 || line[0] == '#') continue;
                    string[] c = line.Split('\t');
                    if (c.Length < 4) continue;
                    uint g, fid;
                    int ty;
                    if (!uint.TryParse(c[0].Trim(), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out g)) continue;
                    if (!uint.TryParse(c[1].Trim(), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out fid)) continue;
                    if (!int.TryParse(c[3].Trim(), out ty)) continue;
                    Entry e = new Entry { Guid = g, FieldId = fid, Name = c[2].Trim(), Type = ty };
                    if (c.Length > 4) e.IntDefault = In(c[4].Trim());
                    if (c.Length > 5) e.IntMax = In(c[5].Trim());
                    if (c.Length > 6) e.Min = Fl(c[6].Trim());
                    if (c.Length > 7) e.Max = Fl(c[7].Trim());
                    if (c.Length > 8) e.Unit = c[8].Trim();
                    if (c.Length > 9 && c[9].Trim().Length > 0) e.Values = c[9].Trim().Split('|').ToList();
                    m[((ulong)g << 32) | fid] = e;
                }
                ifaces = f;
                byKey = m;
            }
        }

        public static Entry Get(uint guid, uint fieldId)
        {
            Load();
            Entry e;
            return byKey.TryGetValue(((ulong)guid << 32) | fieldId, out e) ? e : null;
        }

        /// <summary>the interface (data class) the guid belongs to, or "" </summary>
        public static string Interface(uint guid)
        {
            Load();
            if (guid == GameStateGuid) return "GameState";
            if (guid == MetadataGuid) return "metadata";
            string s;
            return ifaces.TryGetValue(guid, out s) ? s : "";
        }

        public static IList<Entry> All { get { Load(); return byKey.Values.ToList(); } }

        /// <summary>the value names of an enum or bitmap column, or null</summary>
        public static List<string> Values(uint guid, uint fieldId)
        {
            Entry e = Get(guid, fieldId);
            return e != null && e.Values.Count > 0 ? e.Values : null;
        }
    }
}
