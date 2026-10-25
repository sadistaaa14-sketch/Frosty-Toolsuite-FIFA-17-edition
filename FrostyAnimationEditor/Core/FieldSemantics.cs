using System;
using System.Collections.Generic;
using System.Linq;

namespace FrostyAnimationEditor.Core
{
    /// <summary>
    /// What the game does with each asset type and field, from Core/Data/field_semantics.tsv
    /// (type, field, what it does, status; an empty field describes the type).
    /// </summary>
    public static class FieldSemantics
    {
        public sealed class Entry
        {
            public string Type, Field, Meaning = "", Status = "";
            /// <summary>the game reads it, but what it changes is not known</summary>
            public bool Unidentified { get { return Status == "unknown"; } }
            /// <summary>the game never reads it</summary>
            public bool NoEffect { get { return Status == "noeffect"; } }
            /// <summary>the editor recomputes it when saving</summary>
            public bool Derived { get { return Status == "derived"; } }
            public bool HasMeaning { get { return Meaning.Length > 0; } }
            /// <summary>one short line for the status, or "" when there is nothing to say</summary>
            public string StatusText
            {
                get
                {
                    if (NoEffect) return "Not used by the game";
                    if (Derived) return "Recomputed by the editor when you save";
                    if (Unidentified) return "Effect not known";
                    return "";
                }
            }
        }

        private static Dictionary<string, Entry> map;
        private static List<Entry> all;
        private static readonly object gate = new object();

        private static void Load()
        {
            lock (gate)
            {
                if (map != null) return;
                Dictionary<string, Entry> m = new Dictionary<string, Entry>(StringComparer.Ordinal);
                List<Entry> l = new List<Entry>();
                string text;
                try { text = CoreData.Text("field_semantics.tsv"); }
                catch (System.IO.FileNotFoundException) { text = ""; }
                foreach (string raw in text.Split('\n'))
                {
                    string line = raw.TrimEnd('\r');
                    if (line.Length == 0 || line[0] == '#') continue;
                    string[] c = line.Split('\t');
                    if (c.Length < 3) continue;
                    string type = c[0].Trim(), field = c[1].Trim(), meaning = c[2].Trim(), status = c.Length > 3 ? c[3].Trim() : "";
                    if (type.Length == 0 || meaning.Length == 0) continue;
                    string k = type + "\t" + field;
                    Entry prev;
                    if (m.TryGetValue(k, out prev))
                    {
                        // a later row adds to the earlier one
                        prev.Meaning = prev.Meaning.Length > 0 ? prev.Meaning + " " + meaning : meaning;
                        if (status.Length > 0 && prev.Status.Length == 0) prev.Status = status;
                        continue;
                    }
                    Entry e = new Entry { Type = type, Field = field, Meaning = meaning, Status = status };
                    m[k] = e;
                    l.Add(e);
                }
                all = l;
                map = m;
            }
        }

        public static IList<Entry> All { get { Load(); return all; } }

        /// <summary>the field's entry (field "" = the type itself), or null</summary>
        public static Entry Get(string type, string field)
        {
            Load();
            Entry e;
            return type != null && map.TryGetValue(type + "\t" + (field ?? ""), out e) ? e : null;
        }

        public static Entry Get(Field f) { return f != null && f.Owner != null ? Get(f.Owner.Name, f.Name) : null; }

        /// <summary>the type's entries: its own description first, then its fields in table order</summary>
        public static List<Entry> OfType(string type)
        {
            Load();
            return all.Where(e => e.Type == type).OrderBy(e => e.Field.Length == 0 ? 0 : 1).ToList();
        }

        /// <summary>types with at least one entry</summary>
        public static HashSet<string> Types()
        {
            Load();
            return new HashSet<string>(all.Select(e => e.Type), StringComparer.Ordinal);
        }
    }
}
