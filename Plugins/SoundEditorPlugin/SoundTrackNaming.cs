using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace SoundEditorPlugin
{
    /// <summary>
    /// What a bank's clip list shows for a clip: the values of the selection key its row is filed under, in the
    /// game's own words, and nothing else.
    ///
    /// The words come from three places:
    ///  * the bank: the key values are the ones FIFA17.exe's lookup gives the row (see <see cref="SoundBankFile"/>);
    ///  * the game's event systems: a value's name (`home`, `CONTACT_BOTTOM`), from the EBX dump's
    ///    `BoxOfficeParameterEnumValue` records (<see cref="SoundBankParameterTable"/>);
    ///  * the game's database, when one is loaded: the player, team or commentary name an id names
    ///    (<see cref="DatabaseSpace"/>).
    /// Nothing is derived from listening to a clip.
    /// </summary>
    public static class SoundTrackNaming
    {
        private const string Dot = " · ";

        /// <summary>
        /// The game DB table a parameter's values are ids of, or null: <c>player</c> (players.playerid), <c>team</c>
        /// (teams.teamid), <c>commentary</c> (the commentary ids commentarynames names), <c>stadium</c>
        /// (stadiums.stadiumid) or <c>league</c> (leagues.leagueid).
        ///
        /// The answer is the trace's (<see cref="SoundParameterFacts"/>): a parameter has a space only when every
        /// value FIFA17.exe's code sets it to was followed to that column (or is a 0, -1 or -2 sentinel), apart from the
        /// message-fed sources its record lists as not traced. In short:
        ///  * player: set from +8 of a match player (FUN_146faab90), which the team-sheet code fills from
        ///    players.playerid (FUN_1478a03a0/FUN_14789fc50, FUN_146d6db40), or from the per-player table the commentary
        ///    copies out of those match players (FUN_14785c6d0), or a story's player the exe looks up among them
        ///    (FUN_14783fb30, FUN_14783f9b0: FUN_146fe49e0);
        ///  * team: +4 of a match team, or the match setup's teamids (+0x7c/+0x80), which the match builders fill with
        ///    the teamid they query leagueteamlinks by (FUN_147aea0b0); the table-analysis record's +0x20, which the
        ///    exe compares with those teamids (FUN_1478400e0, FUN_147851060);
        ///  * commentary: the player's commentary id, FUN_147843950 (commonnameid's or lastnameid's commentaryid);
        ///  * stadium: the match setup's stadium id (+0xa4), which the exe looks audiostadium up by (FUN_147850760);
        ///  * league: the first of the match setup's two leagueids (+0x84), which the match builder queries leagues by.
        /// Any other id - story fields the exe ties to no match player or team, the radio messages, the parameters
        /// FIFA17.exe never names (sfx_team_id, the ts_*_db_ID set) - is shown as its number.
        /// </summary>
        public static string DatabaseSpace(string parameter)
        {
            if (string.IsNullOrEmpty(parameter))
                return null;

            return SoundParameterFacts.SpaceOf(parameter);
        }

        /// <summary>The DB column a space's ids are: <c>players.playerid</c> for "player".</summary>
        public static string SpaceColumn(string space)
        {
            switch (space)
            {
                case "player": return "players.playerid";
                case "team": return "teams.teamid";
                case "commentary": return "commentarynames.commentaryid";
                case "stadium": return "stadiums.stadiumid";
                case "league": return "leagues.leagueid";
                default: return null;
            }
        }

        /// <summary>
        /// What the Add Clip window says under an id field as it is typed: the game DB's name for the id, that the DB
        /// holds no such id, or that the DB is not read. Empty for a parameter that is not a DB id.
        /// </summary>
        public static string DescribeTypedId(string parameter, string typed)
        {
            string space = DatabaseSpace(parameter);
            string column = SpaceColumn(space);
            if (column == null)
                return "";

            int value;
            if (!int.TryParse((typed ?? "").Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value) || value <= 0)
                return "a " + column;
            if (!FifaNameLookup.DatabaseAvailable)
                return "a " + column + " (the game's DB is not read: " + FifaNameLookup.DatabaseNote + ")";

            string source;
            string name = DatabaseName(new SoundBankKeyValue { Parameter = parameter, Value = value }, out source);
            return name ?? "no " + column + " " + value.ToString(CultureInfo.InvariantCulture) + " in the game's DB";
        }

        /// <summary>
        /// The values a bank already files clips under for one parameter, for the Add Clip window: every value when there
        /// are a few, else the range and the count.
        /// </summary>
        public static string DescribeBankValues(ICollection<int> values)
        {
            if (values == null || values.Count == 0)
                return "no clip of this bank has a value for it yet";

            List<int> sorted = values.OrderBy(v => v).ToList();
            string count = sorted.Count.ToString(CultureInfo.InvariantCulture) + (sorted.Count == 1 ? " value" : " values");
            if (sorted.Count <= 8)
                return "this bank: " + string.Join(", ", sorted.Select(v => v.ToString(CultureInfo.InvariantCulture)).ToArray());

            return "this bank: " + sorted[0].ToString(CultureInfo.InvariantCulture) + ".."
                + sorted[sorted.Count - 1].ToString(CultureInfo.InvariantCulture) + " (" + count + ")";
        }

        /// <summary>The DB's name for a key value, or null; <paramref name="source"/> says what answered.</summary>
        public static string DatabaseName(SoundBankKeyValue value, out string source)
        {
            source = null;
            if (value == null)
                return null;

            string space = DatabaseSpace(value.Parameter);
            if (space == null || value.Value <= 0)
                return null;

            string name;
            string from;
            bool found;
            switch (space)
            {
                case "player": found = FifaNameLookup.TryPlayerName(value.Value, out name, out from); break;
                case "team": found = FifaNameLookup.TryTeamName(value.Value, out name, out from); break;
                case "commentary": found = FifaNameLookup.TryCommentaryName(value.Value, out name, out from); break;
                case "stadium": found = FifaNameLookup.TryStadiumName(value.Value, out name, out from); break;
                case "league": found = FifaNameLookup.TryLeagueName(value.Value, out name, out from); break;
                default: name = null; from = null; found = false; break;
            }

            if (!found || string.IsNullOrWhiteSpace(name))
                return null;

            source = from ?? "db";
            return name;
        }

        /// <summary>
        /// The words for one key value: an id with the DB's name after it (`158023 Lionel Messi`), a value's own
        /// name (`home`), or the parameter and its value. Language 0 says nothing: FUN_146213800 looks a clip up with
        /// the game's language index first and with 0 when that finds nothing, so 0 is the row every language reads.
        /// </summary>
        public static string ValueWords(SoundBankKeyValue value)
        {
            if (value == null)
                return null;

            string number = value.Value.ToString(CultureInfo.InvariantCulture);
            if (DatabaseSpace(value.Parameter) != null)
            {
                string source;
                string dbName = DatabaseName(value, out source);
                return dbName == null ? number : number + " " + dbName;
            }

            if (value.ParameterId == BankNames.Language)
                return value.Value == 0 ? null : "language " + number;

            string words = value.ValueName ?? FlagNames(value.ParameterId, value.Value);
            return words ?? value.Parameter + " " + number;
        }

        /// <summary>
        /// The names of a bit-flag value's bits (`CROSSBAR_IN + POST_IN`), when the parameter's named values are all
        /// single bits and they cover the value exactly; otherwise null.
        /// </summary>
        private static string FlagNames(uint parameterId, int value)
        {
            SoundBankParameterTable.Entry entry = SoundBankParameterTable.Find(parameterId);
            if (entry == null || value <= 0 || entry.Values.Length == 0)
                return null;

            List<string> names = new List<string>();
            int covered = 0;
            for (int i = 0; i < entry.Values.Length; i++)
            {
                int bit = entry.Values[i];
                if (bit == 0)
                    continue;
                if ((bit & (bit - 1)) != 0)
                    return null;
                if ((value & bit) != 0)
                {
                    names.Add(entry.ValueNames[i]);
                    covered |= bit;
                }
            }

            return covered == value && names.Count > 1 ? string.Join(" + ", names.ToArray()) : null;
        }

        /// <summary>
        /// Names one clip of a bank: <paramref name="name"/> for the list (its key's values in words, or the bank's
        /// own name and the clip's number when the bank is looked up by nothing), <paramref name="key"/> the raw key
        /// (`pa_player_id=158023 · pa_team_type=1`), and <paramref name="detail"/> the tooltip: the raw key and the
        /// clip's subtitles.
        /// </summary>
        public static void Label(SoundBankFile bank, string bankPath, int ordinal,
            out string name, out string key, out string detail)
        {
            name = Title(bankPath) + " " + (ordinal + 1).ToString(CultureInfo.InvariantCulture);
            key = null;

            List<string> about = new List<string>();
            IList<SoundBankKeyValue> values = bank == null ? null : bank.KeyOfClip(ordinal);
            if (values != null && values.Count > 0)
            {
                key = string.Join(Dot, values.Select(v => v.Parameter + "=" + v.Value.ToString(CultureInfo.InvariantCulture)).ToArray());
                List<string> words = values.Select(ValueWords).Where(w => !string.IsNullOrEmpty(w)).ToList();

                // Language 0 says nothing beside other words; a clip filed under Language alone is named by it.
                SoundBankKeyValue language = values.FirstOrDefault(v => v.ParameterId == BankNames.Language);
                if (words.Count == 0 && language != null)
                    words.Add("language " + language.Value.ToString(CultureInfo.InvariantCulture));

                if (words.Count > 0)
                    name = string.Join(Dot, words.ToArray());
                about.Add(key);
            }

            if (bank != null)
            {
                foreach (SoundBankSubtitle subtitle in bank.SubtitlesOfClip(ordinal))
                    about.Add(subtitle.ToString());
            }

            detail = about.Count == 0 ? null : string.Join(Dot, about.ToArray());
        }

        /// <summary>
        /// Tells clips with one name apart: the second and later get ` (2)`, ` (3)`... in list order, the first keeps
        /// the name. A bank files several takes under one key (FUN_147986f40 gives a key a block of rows), and those
        /// takes have the same words.
        /// </summary>
        public static void NumberRepeats(IList<string> names)
        {
            if (names == null)
                return;

            Dictionary<string, int> seen = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int i = 0; i < names.Count; i++)
            {
                string name = names[i] ?? "";
                int count;
                seen.TryGetValue(name, out count);
                count++;
                seen[name] = count;
                if (count > 1)
                    names[i] = name + " (" + count.ToString(CultureInfo.InvariantCulture) + ")";
            }
        }

        /// <summary>The bank's own name, from its path: <c>bpa_goal_team_gen_eng</c> for <c>.../bpa_goal_team_gen_eng.res</c>.</summary>
        public static string Title(string bankPath)
        {
            if (string.IsNullOrEmpty(bankPath))
                return "Clip";

            string normalized = bankPath.Replace('\\', '/').TrimEnd('/');
            int slash = normalized.LastIndexOf('/');
            string stem = slash < 0 ? normalized : normalized.Substring(slash + 1);
            if (stem.EndsWith(".res", StringComparison.OrdinalIgnoreCase))
                stem = stem.Substring(0, stem.Length - 4);
            return stem.Length == 0 ? "Clip" : stem;
        }

        /// <summary>The bank's own name, for the summary and for a new clip's default name.</summary>
        public static string BankTitle(string bankPath)
        {
            return Title(bankPath);
        }
    }
}
