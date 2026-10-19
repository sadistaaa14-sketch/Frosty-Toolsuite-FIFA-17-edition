using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace SoundEditorPlugin
{
    /// <summary>
    /// What a bank's clip list shows for a clip: the selection key its row is filed under, said in the game's own
    /// words.
    ///
    /// Every word comes from one of three places, and the tooltip says which:
    ///  * the bank: the key values are the ones FIFA17.exe's lookup gives the row (see <see cref="SoundBankFile"/>);
    ///  * the game's event systems: a parameter's name and its values' names (<see cref="SoundBankParameterTable"/>,
    ///    from the EBX dump's `BoxOfficeParameterDescription` and `BoxOfficeParameterEnumValue` records);
    ///  * the game's database, when one is loaded: the player, team or surname an id names. That a parameter
    ///    carries such an id is read from its name (`pa_player_id`, `sfx_team_id`, `surname_ID`), not traced
    ///    through FIFA17.exe, so the name shows the parameter and its value first and the DB's word after it,
    ///    marked `(DB: ...)`, and the tooltip says what the link rests on.
    /// Nothing is derived from listening to a clip.
    /// </summary>
    public static class SoundTrackNaming
    {
        private const string Dot = " · ";

        /// <summary>
        /// The game DB table a parameter's name says its values are ids of, or null. Only names that say it outright:
        /// a player id ends in `player_id` or `db_id` (`pa_player_id`, `player_db_ID`, `ts_def_A_db_ID`), a team id in
        /// `team_id` or is a home/away id (`sfx_team_id`, `asset_team_ID`, `pa_home_id`, `away_ID`), and `surname_ID`
        /// is a surname. `team_venue_ID` or `team_position_ID` name something else and are left alone.
        /// </summary>
        public static string DatabaseSpace(string parameter)
        {
            if (string.IsNullOrEmpty(parameter))
                return null;

            string p = parameter.ToLowerInvariant();
            if (p == "surname_id")
                return "surname";
            if (p.EndsWith("player_id", StringComparison.Ordinal) || p.EndsWith("db_id", StringComparison.Ordinal))
                return "player";
            if (p.EndsWith("team_id", StringComparison.Ordinal) || p == "home_id" || p == "away_id"
                || p.EndsWith("_home_id", StringComparison.Ordinal) || p.EndsWith("_away_id", StringComparison.Ordinal))
                return "team";
            return null;
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
            if (space == "player")
                found = FifaNameLookup.TryPlayerName(value.Value, out name, out from);
            else if (space == "team")
                found = FifaNameLookup.TryTeamName(value.Value, out name, out from);
            else
                found = FifaNameLookup.TrySurname(value.Value, out name, out from);

            if (!found || string.IsNullOrWhiteSpace(name))
                return null;

            source = from ?? "db";
            return name;
        }

        /// <summary>
        /// Names one clip of a bank: <paramref name="name"/> for the list, <paramref name="variation"/> for the
        /// Variation column (which of its block's rows it is), <paramref name="key"/> the raw key, and
        /// <paramref name="detail"/> the tooltip.
        /// </summary>
        public static void Label(SoundBankFile bank, string bankPath, int ordinal, string codecName,
            out string name, out string variation, out string key, out string detail)
        {
            int clipCount = bank == null || bank.Clips == null ? 0 : bank.Clips.Count;
            name = "Clip " + (ordinal + 1).ToString(CultureInfo.InvariantCulture);
            variation = null;
            key = null;

            StringBuilder about = new StringBuilder();
            about.Append("clip ").Append((ordinal + 1).ToString(CultureInfo.InvariantCulture)).Append(" of ")
                .Append(clipCount.ToString(CultureInfo.InvariantCulture));
            if (!string.IsNullOrEmpty(codecName))
                about.Append(Dot).Append(codecName);

            int row = bank == null ? -1 : bank.RowOfClip(ordinal);
            if (row < 0)
            {
                about.Append(Dot).Append("no Selection row plays this clip");
                detail = about.ToString();
                return;
            }

            about.Append(Dot).Append("Selection row ").Append(row.ToString(CultureInfo.InvariantCulture));

            IList<SoundBankKeyValue> values = bank.KeyOfClip(ordinal);
            if (values == null || values.Count == 0)
            {
                // A bank with no selection parameter plays its rows for an event that passes none: the rows are the
                // bank's variations, and that is what the clip is.
                about.Append(Dot).Append("the bank has no selection parameter, so every clip is one of its variations");
                name = Title(bankPath) + " " + (ordinal + 1).ToString(CultureInfo.InvariantCulture);
                detail = about.ToString();
                return;
            }

            List<string> words = new List<string>();
            List<string> raw = new List<string>();
            List<string> sources = new List<string>();
            foreach (SoundBankKeyValue value in values)
            {
                raw.Add(value.Parameter + "=" + value.Value.ToString(CultureInfo.InvariantCulture));

                string dbSource;
                string dbName = DatabaseName(value, out dbSource);
                if (dbName != null)
                {
                    words.Add(value.Parameter + " " + value.Value.ToString(CultureInfo.InvariantCulture) + " (DB: " + dbName + ")");
                    sources.Add(value.Parameter + " " + value.Value.ToString(CultureInfo.InvariantCulture) + " is " + dbName
                        + " in the game's " + DatabaseSpace(value.Parameter) + " table (" + dbSource
                        + "; read as a DB id from the parameter's name, not traced in FIFA17.exe)");
                }
                else if (value.ValueName != null)
                {
                    words.Add(value.ValueName);
                }
                else if (value.ParameterId == BankNames.Language)
                {
                    // FUN_146213800 looks a clip up with the game's language index first and with 0 when that finds
                    // nothing, so a row stated for 0 is the one every language falls back to.
                    if (value.Value != 0)
                        words.Add("language " + value.Value.ToString(CultureInfo.InvariantCulture));
                }
                else
                {
                    words.Add(value.Parameter + " " + value.Value.ToString(CultureInfo.InvariantCulture));
                }
            }

            key = string.Join(Dot, raw.ToArray());
            name = words.Count > 0 ? string.Join(Dot, words.ToArray()) : key;

            SoundBankRowPlace place = bank.PlaceOfClip(ordinal);
            if (place != null && place.Count > 1)
            {
                variation = (place.Position + 1).ToString(CultureInfo.InvariantCulture) + " of "
                    + place.Count.ToString(CultureInfo.InvariantCulture);
            }

            about.Append(Dot).Append("key ").Append(string.Join(", ", values.Select(v => v.ToString()).ToArray()));
            if (place != null)
            {
                about.Append(Dot).Append("its block in group ").Append(place.Group.ToString(CultureInfo.InvariantCulture))
                    .Append(" is rows ").Append(place.Start.ToString(CultureInfo.InvariantCulture)).Append("..")
                    .Append((place.Start + place.Count - 1).ToString(CultureInfo.InvariantCulture))
                    .Append(place.Count > 1 ? " (" + place.Count.ToString(CultureInfo.InvariantCulture) + " clips share this key)" : "");
            }

            foreach (string line in sources)
                about.Append(Dot).Append(line);

            detail = about.ToString();
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
