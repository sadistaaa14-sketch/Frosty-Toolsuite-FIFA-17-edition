using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace SoundEditorPlugin
{
    /// <summary>
    /// Lookups over the generated table (<c>SoundParameterFacts.cs</c>): for a sound-bank key parameter, whether
    /// FIFA17.exe's code sets it, what it sets it to, the DB column its values are when that is shown, and the events
    /// whose code sets it. The editor says this beside a parameter wherever it shows one: the bank summary's tooltip,
    /// the Add Clip window.
    /// </summary>
    internal static partial class SoundParameterFacts
    {
        internal sealed class Fact
        {
            public uint Id;
            public string Name;

            /// <summary><c>exe</c> (the exe's code sets it by name), <c>data</c> (the exe never names it), <c>engine</c>.</summary>
            public string By;

            /// <summary>The DB space the values are (<c>player</c>, <c>team</c>...), or null.</summary>
            public string Space;

            /// <summary>What the exe sets it to, in the editor's words; for an engine key, its use.</summary>
            public string Value;

            /// <summary>The sources the trace does not follow to the end, or null.</summary>
            public string Note;

            /// <summary>The event system the game's data declares it in, or null.</summary>
            public string System;

            public string[] Events;
        }

        private static readonly string[] NoEvents = new string[0];
        private static readonly Dictionary<string, Fact> byName = new Dictionary<string, Fact>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<uint, Fact> byId = new Dictionary<uint, Fact>();

        private static void Add(uint id, string name, string by, string space, string value, string note, string system, string[] events)
        {
            Fact fact = new Fact { Id = id, Name = name, By = by, Space = space, Value = value, Note = note, System = system, Events = events ?? NoEvents };
            byName[name] = fact;
            byId[id] = fact;
        }

        /// <summary>The parameter's record, or null when the trace has none.</summary>
        public static Fact Find(string name)
        {
            Fact fact;
            return !string.IsNullOrEmpty(name) && byName.TryGetValue(name, out fact) ? fact : null;
        }

        /// <summary>The parameter's record by its id, or null.</summary>
        public static Fact Find(uint id)
        {
            Fact fact;
            return byId.TryGetValue(id, out fact) ? fact : null;
        }

        /// <summary>The DB space a parameter's values are, or null (see <see cref="SoundTrackNaming.DatabaseSpace"/>).</summary>
        public static string SpaceOf(string name)
        {
            Fact fact = Find(name);
            return fact == null ? null : fact.Space;
        }

        /// <summary>How many parameters the table has.</summary>
        public static int Count
        {
            get { return byName.Count; }
        }

        /// <summary>
        /// What a parameter is, for a tooltip: the DB column when the values are one, what FIFA17.exe sets it to (or
        /// that it never names it), what the trace does not follow, and the events whose code sets it.
        /// </summary>
        public static string Describe(string name, int maxEvents = 8)
        {
            if (string.IsNullOrEmpty(name))
                return null;

            Fact fact = Find(name);
            SoundBankParameterTable.Entry entry = SoundBankParameterTable.Find(SoundBankParameterTable.IdOf(name));
            StringBuilder text = new StringBuilder(name);

            string column = SoundTrackNaming.SpaceColumn(fact == null ? null : fact.Space);
            if (column != null)
                text.Append(": ").Append(column);

            if (fact == null)
            {
                if (entry != null && entry.EngineKey)
                    text.AppendLine().Append(entry.Source);
                return text.ToString();
            }

            if (fact.By == "data")
            {
                text.AppendLine().Append("FIFA17.exe's code never names it (neither the name nor its id 0x")
                    .Append(fact.Id.ToString("X8", CultureInfo.InvariantCulture)).Append(" occurs in the exe)");
                if (!string.IsNullOrEmpty(fact.System))
                    text.Append("; the game's ").Append(fact.System).Append(" data declares it");
                text.Append('.');
                return text.ToString();
            }

            if (fact.By == "engine")
            {
                string use = fact.Value ?? "";
                if (use.Length > 0)
                    use = char.ToUpperInvariant(use[0]) + use.Substring(1);
                text.AppendLine().Append(use).Append(use.EndsWith(".", StringComparison.Ordinal) ? "" : ".");
                return text.ToString();
            }

            text.AppendLine().Append("FIFA17.exe sets it to ").Append(fact.Value).Append('.');
            if (!string.IsNullOrEmpty(fact.Note))
                text.AppendLine().Append("Not traced: ").Append(fact.Note).Append('.');

            if (fact.Events.Length > 0)
            {
                text.AppendLine().Append("Set by the code of ")
                    .Append(string.Join(", ", fact.Events.Take(maxEvents).ToArray()));
                if (fact.Events.Length > maxEvents)
                    text.Append(" and ").Append((fact.Events.Length - maxEvents).ToString(CultureInfo.InvariantCulture)).Append(" more");
                text.Append('.');
            }

            return text.ToString();
        }
    }
}
