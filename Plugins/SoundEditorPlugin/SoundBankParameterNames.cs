using System;
using System.Collections.Generic;
using System.Globalization;

namespace SoundEditorPlugin
{
    /// <summary>
    /// Lookups over the generated table (<c>SoundBankParameterTable.cs</c>): a selection parameter's name and the
    /// names of its values, as the game's own event systems state them.
    ///
    /// A bank names nothing itself. A group record lists parameter ids, each id is the engine hash of the
    /// parameter's name (h = 5381; h = (h * 33) ^ byte), and a value is an integer. Every name here is one the EBX
    /// dump states beside an id that hashes from it, or one of the engine's own keys whose use is in FIFA17.exe.
    /// </summary>
    internal static partial class SoundBankParameterTable
    {
        internal sealed class Entry
        {
            public uint Id;
            public string Name;

            /// <summary>The event system the value names come from, or the exe function for an engine key.</summary>
            public string Source;

            /// <summary>True for an id FIFA17.exe uses itself (Language, VariationIndex...).</summary>
            public bool EngineKey;

            public string[] ValueNames = new string[0];
            public int[] Values = new int[0];
        }

        private static readonly Dictionary<uint, Entry> byId = new Dictionary<uint, Entry>();

        private static void Add(uint id, string name, string system, params object[] enums)
        {
            Entry entry = new Entry { Id = id, Name = name, Source = system };
            int pairs = enums == null ? 0 : enums.Length / 2;
            entry.ValueNames = new string[pairs];
            entry.Values = new int[pairs];
            for (int i = 0; i < pairs; i++)
            {
                entry.ValueNames[i] = (string)enums[2 * i];
                entry.Values[i] = Convert.ToInt32(enums[2 * i + 1], CultureInfo.InvariantCulture);
            }

            byId[id] = entry;
        }

        private static void Engine(uint id, string name, string proof)
        {
            byId[id] = new Entry { Id = id, Name = name, Source = proof, EngineKey = true };
        }

        /// <summary>The parameter's record, or null when the game's data names no parameter with this id.</summary>
        public static Entry Find(uint id)
        {
            Entry entry;
            return byId.TryGetValue(id, out entry) ? entry : null;
        }

        /// <summary>The parameter's name, or <c>0x........</c> when nothing names it.</summary>
        public static string NameOf(uint id)
        {
            Entry entry = Find(id);
            return entry != null ? entry.Name : "0x" + id.ToString("X8", CultureInfo.InvariantCulture);
        }

        /// <summary>True when the game's data names this parameter.</summary>
        public static bool IsNamed(uint id)
        {
            return Find(id) != null;
        }

        /// <summary>The name the event system gives a value of this parameter, or null.</summary>
        public static string ValueName(uint id, int value)
        {
            Entry entry = Find(id);
            if (entry == null)
                return null;

            for (int i = 0; i < entry.Values.Length; i++)
            {
                if (entry.Values[i] == value)
                    return entry.ValueNames[i];
            }

            return null;
        }

        /// <summary>The parameter's id for a name, or 0.</summary>
        public static uint IdOf(string name)
        {
            if (string.IsNullOrEmpty(name))
                return 0;

            foreach (KeyValuePair<uint, Entry> pair in byId)
            {
                if (string.Equals(pair.Value.Name, name, StringComparison.OrdinalIgnoreCase))
                    return pair.Key;
            }

            return 0;
        }

        /// <summary>How many parameters the table names.</summary>
        public static int Count
        {
            get { return byId.Count; }
        }
    }
}
