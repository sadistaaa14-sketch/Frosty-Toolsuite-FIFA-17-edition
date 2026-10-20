using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using Frosty.Core;
using Frosty.Core.Legacy;

namespace SoundEditorPlugin
{
    /// <summary>
    /// The names behind the ids a FIFA 17 sound bank carries: the commentary selection parameters the
    /// research named, the player or team behind a player or team id, the commentary name behind a
    /// <c>surname_ID</c>, and the song behind an EA Trax songid.
    ///
    /// A person is resolved from the game's own DB (<c>data/db/fifa_ng_db.db</c>, read through the FIFA
    /// database plugin's reader when that plugin is loaded) and from the eng_us strings
    /// (<see cref="LocalizedStringDatabase.Current"/>) when a name column holds a string id rather than
    /// text. If neither is reachable - no game loaded, the DB plugin absent, a profile without the file -
    /// the lookup says so and returns false instead of inventing a name.
    ///
    /// Nothing in here throws and nothing blocks: it runs inside an editor that may have no game at all,
    /// so every route is guarded and every answer is cached.
    /// </summary>
    public static class FifaNameLookup
    {
        /// <summary>
        /// The 253 hash-shaped selection parameters, as named by the research
        /// (<c>FIFA17soundsystem\notes\extra_id_names.tsv</c>). A name is handed out only when it
        /// re-hashes to the id it was stored with, so a wrong entry degrades to the raw id rather than
        /// to a plausible-looking lie.
        /// </summary>
        private static readonly Dictionary<uint, string> parameterCandidates = new Dictionary<uint, string>
        {
            { 0x4CF5D8BBu, "cup_name" }, { 0x9780BF58u, "ssm_objective" }, { 0x9F3B82C1u, "ta_scenario" },
            { 0xA21B4453u, "hr_origin_type" }, { 0xF14A0FEEu, "ttp_team_a_type" }, { 0x69A871C9u, "sm_broadcast_ID" },
            { 0x65BFC61Fu, "ttp_team_a_result" }, { 0x0D5207E7u, "hr_shot_type" }, { 0xA0275425u, "sm_cinematic_ID" },
            { 0x7F5EE0C2u, "ptw_team_result" }, { 0x6C5C9DEAu, "ttp_team_a_home_or_away" }, { 0x6AD527AAu, "ta_clinched_type" },
            { 0x38764AB8u, "cup_tmnt_type" }, { 0x23A0D151u, "ptw_player_db_ID" }, { 0x2FD61BBFu, "team_type" },
            { 0xE54E3C75u, "winning_losing_margin" }, { 0x3B6F0A24u, "tr_type" }, { 0x3AEA33C4u, "stadium_ID" },
            { 0x7DF46F5Fu, "ssm_score" }, { 0xF185A38Cu, "ptw_player_b_db_ID" }, { 0x0676DA62u, "ptw_player_a_contextual_stats" },
            { 0x0BAB782Du, "score" }, { 0x32C3721Bu, "ttp_team_a_ID" }, { 0x2B519587u, "hr_goal_location" },
            { 0x20C95E4Cu, "ao_type" }, { 0xF0F1D4A7u, "ptw_player_a_contextual_stats_extra" }, { 0x1139FD25u, "cup_tmnt_var" },
            { 0x406A7E92u, "ssm_ft_score" }, { 0x5EC05387u, "new_signing_ID" }, { 0x049C5658u, "online_ID" },
            { 0x0EE24497u, "ssm_position_check" }, { 0x0A9AB9F6u, "hi_ID" }, { 0xD4213702u, "ptw_player_rating" },
            { 0xF2D95E93u, "lb_prev_result" }, { 0x12ACB8B8u, "ta_time_in_season" }, { 0x01A6A5F5u, "fut_ms_num_of_goals" },
            { 0x101C7D18u, "home_ID" }, { 0xF8B2227Bu, "keeper_ID" }, { 0xEF3FC47Fu, "sig_type" },
            { 0xD3DBA66Cu, "ptw_player_result" }, { 0xFE2994F0u, "possessor_ID" }, { 0x66851D82u, "hr_goal_type" },
            { 0x244E2386u, "challenger_ID" }, { 0xA40253A5u, "ptw_dual_player_a_trait_type" }, { 0x14DE84EBu, "pa_time_of_day" },
            { 0x043B4795u, "hr_woodwork_type" }, { 0xD985F26Bu, "kinect_tactic_type" }, { 0x87417168u, "league_ID" },
            { 0xBE0D4541u, "ptw_player_b_contextual_stats" }, { 0x94BCF9A7u, "tr_currency_amount_in_mil" }, { 0x4664DE86u, "common_notes_ID" },
            { 0x4A0A8C8Fu, "pass_from_ID" }, { 0x45D3883Bu, "lae_competition_1_ID" }, { 0x6DC3C5B4u, "ssm_clinch_requirement" },
            { 0x524119B7u, "receiver_db_ID" }, { 0x71D3843Au, "storyline_ID" }, { 0xEA6B9D8Du, "ttp_team_b_type" },
            { 0xFEE4E113u, "cpu_team_ID" }, { 0x530FB75Bu, "hr_nearmiss_type" }, { 0xFCA529DAu, "fut_champions_match_type" },
            { 0x352B4299u, "away_ID" }, { 0xE52CE292u, "kinect_user_type" }, { 0x0A537034u, "lo_ID" },
            { 0xA3F4C46Eu, "tr_team_b_ID" }, { 0x0E8DE996u, "heat_replay_type" }, { 0xA121158Eu, "fut_ms_num_of_appearances" },
            { 0x17856BEBu, "replay_goal_type" }, { 0xC50CB857u, "ttp_team_a_result_goal_diff" }, { 0x5D67649Fu, "hr_fk_attempt" },
            { 0x24AE527Eu, "ssm_clinch_time" }, { 0x1E3E71ECu, "lal_state" }, { 0xEF8CCA91u, "ptw_player_b_result" },
            { 0x46D2449Eu, "fut_ms_still_waiting_for_the_goal" }, { 0x3218EF02u, "fut_ms_chance_of_scoring_today" }, { 0xA192DFA1u, "ao_player_position" },
            { 0x263628ABu, "point_in_season" }, { 0x48DD4D62u, "ta_focus_team1_ID" }, { 0x6802B2F3u, "lae_focus_team_1_home_away" },
            { 0xD8A81B5Du, "ao_player_analysis_score" }, { 0x64D6A98Bu, "lae_against_team_1_ID" }, { 0x89861A60u, "lae_focus_team_1_ID" },
            { 0x7E9E17C6u, "ref_ID" }, { 0x3F28CD08u, "storymode_sub_speech_margin" }, { 0x2EF354AFu, "replay_shot_origin_of_play" },
            { 0x2E121CF2u, "lb_prev_score" }, { 0x9D5083C1u, "shot_taker_ID" }, { 0xDBF82F0Cu, "ssm_clinch_best_type" },
            { 0x0530ECFFu, "storymode_sub_speech_result" }, { 0xD1C94D72u, "highlight_type" }, { 0x0F5BC759u, "home_or_away" },
            { 0x45D40C58u, "lae_competition_2_ID" }, { 0x62A04CF6u, "ts_formation" }, { 0x875A41F8u, "miss_type_a" },
            { 0x43A9A2FCu, "tr_currency_type" }, { 0x474CBD41u, "ts_num_changes" }, { 0x465E9C59u, "ts_formation_repeat_type" },
            { 0x1C5424D0u, "cpu_ai_tactics" }, { 0x4E747A21u, "delayed_param" }, { 0xE3CCD561u, "intercept_location" },
            { 0xA0857BB1u, "intercept_quality" }, { 0x168C5E8Fu, "pa_half_type" }, { 0x9F70B7CAu, "kinect_instance_type" },
            { 0xDE0403C6u, "ptw_dual_player_b_trait_type" }, { 0xB1AE565Fu, "pa_match_type" }, { 0xC5585DBFu, "player_goals" },
            { 0xB5387FF4u, "emotion_time" }, { 0x8EF6A0AAu, "ptw_player_a_contextual_num_of_goals" }, { 0x59270382u, "ssm_time" },
            { 0x84CFABC0u, "ptw_player_a_contextual_num_of_assists" }, { 0x4168439Fu, "lae_cup_final_competition_ID" }, { 0xB390D363u, "sub_off_ID" },
            { 0x5E521CDFu, "score_diff" }, { 0xD7BBD902u, "ptw_player_a_position" }, { 0x4B9FE83Fu, "SubtitleIndex" },
            { 0xC43CA154u, "hr_owngoal_type" }, { 0x8CC41575u, "fut_seasons_clinch_scenarios" }, { 0xDF883DA4u, "tr_player_db_ID" },
            { 0xEFAAD056u, "sm_big_signing_ID" }, { 0x42079E28u, "pa_league_or_cup_type" }, { 0x67CC2C58u, "sfx_team_id" },
            { 0x22F17E95u, "player_assists" }, { 0xACAB31A4u, "fut_ms_player_db_ID" }, { 0xDF6D46A9u, "sm_journal_events_win_loss_draw" },
            { 0x374D8247u, "team_type_ts" }, { 0xD5C72E18u, "fut_champions_tier_clinch" }, { 0x8AC3896Du, "ts_formation_upfront" },
            { 0xF8CF4AC4u, "ptw_player_b_contextual_stats_extra" }, { 0x1E876D48u, "attacker_ID" }, { 0xFE7BB7C1u, "lb_prediction" },
            { 0x32C1D538u, "ttp_team_b_ID" }, { 0xAB7F7785u, "pa_jersey_number" }, { 0x07C6CF03u, "pickup_ID" },
            { 0xFECE85F2u, "offsides_team_ID" }, { 0x4EE9B1CAu, "target_player_ID" }, { 0x3C13764Cu, "scoreline_first_at" },
            { 0xBA0288A9u, "offside_trap_team_ID" }, { 0x57076343u, "ptw_player_b_contextual_num_of_assists" }, { 0x50AF4A5Bu, "lae_focus_team_2_prediction" },
            { 0x6532BCFBu, "FUT_T_ID" }, { 0x82FF31FDu, "debut_goal" }, { 0xDFE3B7F9u, "marker_player_ID" },
            { 0xA43E0777u, "lal_summary" }, { 0x626C1217u, "mins_left_reg" }, { 0xFC61C6B9u, "emotion_match_importance" },
            { 0x4B2A2BA9u, "ptw_player_result_extra" }, { 0x851BF1B4u, "ttp_team_a_records_wins" }, { 0xAA1006EAu, "pa_player_id" },
            { 0x056F4916u, "user_game_winning_by_3plus" }, { 0x29B77A4Au, "ttp_team_a_records_games" }, { 0xAC50D6BDu, "mins_left_extra" },
            { 0xC4ECA0DDu, "sfx_region_id" }, { 0x9326281Eu, "sc_injury_type" }, { 0xBFA2EBEDu, "sub_on_ID" },
            { 0xBF00DC69u, "ptw_player_b_contextual_num_of_goals" }, { 0xB186EF0Au, "pa_stadium_id" }, { 0xD1C3AAFDu, "lae_cup_tmt_type_1_ID" },
            { 0xD1C21E9Eu, "lae_cup_tmt_type_2_ID" }, { 0xA1503E23u, "lb_against_team_ID" }, { 0x32269BBFu, "ssm_var_elsewhere" },
            { 0xBCCC69E1u, "ptw_player_b_position" }, { 0xAE203460u, "ssm_goal_diff" }, { 0x7B6CA2C6u, "ta_both_home_away_check" },
            { 0xDD282618u, "fut_champions_rank" }, { 0xDD24CCA4u, "fut_champions_tier" }, { 0x48DCB8C1u, "ta_focus_team2_ID" },
            { 0x9C5D3C79u, "emotion_match_status" }, { 0xC9C62684u, "scoreline_type" }, { 0xA3F42FCDu, "tr_team_a_ID" },
            { 0xEEEA6B63u, "player_db_ID_A" }, { 0x7F3C52C4u, "pa_team_id" }, { 0x2AA79C16u, "pa_home_id" },
            { 0x3FAA7531u, "pa_team_type" }, { 0x64D50CA8u, "lae_against_team_2_ID" }, { 0xE3C072EFu, "lae_cup_final_against_team_ID" },
            { 0x33D56E44u, "lae_focus_cup_final_team_ID" }, { 0x89837643u, "lae_focus_team_2_ID" }, { 0x63F2718Cu, "lal_against_team_ID" },
            { 0x272F31A7u, "lal_focus_team_ID" }, { 0xE5A4C47Du, "sm_sub_speech_time" }, { 0x2E8D2ACDu, "sm_parent_club_ID" },
            { 0xEEEA6B60u, "player_db_ID_B" }, { 0xE9245EC8u, "ssm_clinch_failed" }, { 0x569F736Cu, "pen_outcome_type" },
            { 0x7B7BC771u, "pres_weather_type" }, { 0x3529483Cu, "storymode_champion_nis_ID" }, { 0x48FF8318u, "team_venue_ID" },
            { 0x13D56B4Au, "replay_finisher_player_db_ID" }, { 0xC9EF70BCu, "away_score" }, { 0xB01E3F5Du, "home_score" },
            { 0x80186154u, "hr_red_type" }, { 0xFBACDFD7u, "pa_away_id" }, { 0x302188B0u, "home_inflection_ID" },
            { 0xEE429B91u, "away_inflection_ID" }, { 0xDC572A8Fu, "ts_def_A_db_ID" }, { 0xEDA0305Du, "ts_fwd_A_db_ID" },
            { 0x43544B48u, "ts_mid_A_db_ID" }, { 0x938A722Du, "pa_injury_time_added" }, { 0x5BB1A957u, "emotion_level" },
            { 0xFB72A4BFu, "number_of_goals_for_team" }, { 0x2174DB57u, "hr_foul_type" }, { 0xC3977E48u, "pa_sub_type" },
            { 0x4ADDEB93u, "pa_lineup_pos" }, { 0x6030F53Fu, "pa_goal_minute" }, { 0x2AAE8ECCu, "team_position_ID" },
            { 0x8CF59C8Au, "corner_cnotes_clock" }, { 0x3F3742FFu, "num_shots" }, { 0x05706604u, "ts_gk_A_db_ID" },
            { 0xAB4E5276u, "sfx_player_id" }, { 0x0A8B652Du, "clock" }, { 0x4EEE3107u, "shot_accuracy" },
            { 0xA31573A4u, "emotion_same_players" }, { 0x64A58F29u, "men_down_type" }, { 0x9A8F5151u, "number_of_goals_for_player" },
            { 0xA94FAD86u, "ptw_team_ID" }, { 0xA2557470u, "hr_foul_context" }, { 0x3331D488u, "pa_ref_id" },
            { 0x6A73ED66u, "pa_league_id" }, { 0x1A043286u, "penalty_win_score" }, { 0xBDAE5A3Au, "position_top_or_bottom" },
            { 0xAF08A268u, "common_stitch_notes_ID" }, { 0x8E10578Au, "corner_cnotes_score" }, { 0xF9F5FE60u, "news_recap_team_ID" },
            { 0x11BF353Du, "num_target" }, { 0xB3A3016Cu, "pa_attendance" }, { 0x7B73784Bu, "sfx_away_id" },
            { 0xA0F8218Au, "sfx_home_id" }, { 0xE046AF0Cu, "ts_def_B_db_ID" }, { 0x97D3928Du, "ts_def_C_db_ID" },
            { 0xA0394FEEu, "ts_fwd_A_gen_db_ID" }, { 0xD04BC5DEu, "ts_fwd_B_db_ID" }, { 0x87D8A95Fu, "ts_fwd_C_db_ID" },
            { 0x0180E187u, "ts_gk_B_db_ID" }, { 0x9519C8CBu, "ts_mid_B_db_ID" }, { 0xDD8CE54Au, "ts_mid_C_db_ID" },
            { 0x3593D11Bu, "screen_ID" }, { 0x6CE28F4Au, "team_ID" }, { 0xE5DD8D31u, "league_cup_ID" },
            { 0xCA08225Au, "tmnt_stage_type" }, { 0xD98A1AF9u, "winning_team" }, { 0xFA871947u, "extra_time_ID" },
            { 0xBF8FB3C3u, "penalty_lose_score" }, { 0xBFFC0363u, "team_movement_type" }, { 0x86D8230Du, "additional_info" },
            { 0x880E3998u, "team_points" }, { 0x277E268Au, "ts_gk_reason" }, { 0xF4712F08u, "goal_cnotes_score" },
            { 0x00DB3F34u, "ts_def_last_time_out" }, { 0x66A51321u, "ts_def_reason" }, { 0x0C1D8866u, "ts_fwd_last_time_out" },
            { 0xD7ADE633u, "ts_fwd_reason" }, { 0x83E05733u, "ts_mid_last_time_out" }, { 0x0B45E0C6u, "ts_mid_reason" },
            { 0x77115A82u, "user_game_outcome_ssm" }
        };

        private static readonly object sync = new object();
        private static readonly Dictionary<uint, string> verifiedParameters = new Dictionary<uint, string>();
        private static readonly Dictionary<uint, string> localizedCache = new Dictionary<uint, string>();

        private static bool databaseTried;
        private static Dictionary<int, string> teamNames;
        private static Dictionary<int, string> playerNames;
        private static Dictionary<int, string> commentaryNames;
        private static Dictionary<int, string> stadiumNames;
        private static Dictionary<int, string> leagueNames;
        private static string databaseNote = "not read yet";

        private static bool playerClubsTried;
        private static Dictionary<int, int> playerClubs;
        private static string playerClubNote = "not read yet";

        private static int verifiedParameterCount = -1;

        /// <summary>How many pieces a selection parameter id can be named with.</summary>
        public static int ParameterNameCount => parameterCandidates.Count;

        /// <summary>
        /// The game's name hash, case preserving: <c>h = 5381; h = h*33 ^ c</c>. It is the hash behind
        /// every dataset, field and parameter id in a bank, and it is deliberately not the localised
        /// string id hash (<c>result = c + 33*result</c>).
        /// </summary>
        public static uint EngineHash(string text)
        {
            if (string.IsNullOrEmpty(text))
                return 0;

            unchecked
            {
                uint h = 5381;
                for (int i = 0; i < text.Length; i++)
                {
                    char c = text[i];
                    if (c > 0xFF)
                        return 0;
                    h = (h * 33) ^ (byte)c;
                }
                return h;
            }
        }

        /// <summary>
        /// The name of a selection parameter, or null when the id is not one this build can name. Each
        /// answer is checked against the hash it was stored with before it is returned.
        /// </summary>
        public static string ParameterName(uint id)
        {
            lock (sync)
            {
                string known;
                if (verifiedParameters.TryGetValue(id, out known))
                    return known;

                string candidate;
                string result = null;
                if (parameterCandidates.TryGetValue(id, out candidate) && EngineHash(candidate) == id)
                    result = candidate;

                verifiedParameters[id] = result;
                return result;
            }
        }

        /// <summary>
        /// The eng_us text for a string id, or an empty string when there is no string database or the
        /// id is not in it. The chosen language is the editor's own (<c>Language</c> in the game's config).
        /// </summary>
        public static string Localized(uint stringId)
        {
            lock (sync)
            {
                string known;
                if (localizedCache.TryGetValue(stringId, out known))
                    return known;
            }

            string text = null;
            try
            {
                ILocalizedStringDatabase database = LocalizedStringDatabase.Current;
                if (database != null)
                    text = database.GetString(stringId);
            }
            catch (Exception)
            {
                text = null;
            }

            text = text == null ? "" : text.Trim();
            if (text.Length > 0)
            {
                lock (sync)
                    localizedCache[stringId] = text;
            }
            return text;
        }

        /// <summary>True when an eng_us string database is loaded and answers with text.</summary>
        public static bool StringsAvailable()
        {
            ILocalizedStringDatabase database = LocalizedStringDatabase.Current;
            if (database == null)
                return false;

            try
            {
                foreach (uint id in database.EnumerateStrings())
                    return true;
            }
            catch (Exception)
            {
            }

            return false;
        }

        /// <summary>
        /// True when the game's DB answered with tables, so a team, player or commentary id has a name here.
        ///
        /// A dialog that asks for an id has to be able to say what it will do with a number it cannot name,
        /// and "the DB is not in this profile" is a different answer from "the DB holds no such id" - one of
        /// them the user can fix before pressing the button.
        /// </summary>
        public static bool DatabaseAvailable
        {
            get
            {
                EnsureDatabase();
                return CountOf(teamNames) > 0 || CountOf(playerNames) > 0 || CountOf(commentaryNames) > 0
                    || CountOf(stadiumNames) > 0 || CountOf(leagueNames) > 0;
            }
        }

        /// <summary>What the DB lookup can answer with, or why it cannot - one clause, for a dialog.</summary>
        public static string DatabaseNote
        {
            get
            {
                EnsureDatabase();
                return databaseNote;
            }
        }

        /// <summary>A team name from the game DB, or false when the DB is not reachable.</summary>
        public static bool TryTeamName(int teamId, out string name)
        {
            string source;
            return TryTeamName(teamId, out name, out source);
        }

        /// <summary>A team name and where it came from (<c>db</c>), or false.</summary>
        public static bool TryTeamName(int teamId, out string name, out string source)
        {
            EnsureDatabase();
            return FromTable(teamNames, teamId, "db", out name, out source);
        }

        /// <summary>A player name from the game DB, or false.</summary>
        public static bool TryPlayerName(int playerId, out string name)
        {
            string source;
            return TryPlayerName(playerId, out name, out source);
        }

        /// <summary>
        /// A player name and where it came from (<c>db</c>), or false. Only the game's own DB answers: the research's
        /// player CSVs (<c>fifa17_players_with_recordings.csv</c>, <c>player_name_map.csv</c>) were built partly by
        /// listening to clips and from web lookups, and this plugin names nothing from either.
        /// </summary>
        public static bool TryPlayerName(int playerId, out string name, out string source)
        {
            EnsureDatabase();
            return FromTable(playerNames, playerId, "db", out name, out source);
        }

        /// <summary>
        /// The club a player is at, from the game's own <c>teamplayerlinks</c> table: its id and, when the teams
        /// table names it, the club's name. False when the DB is not reachable, the table is not there, or the
        /// player is linked to no club.
        ///
        /// This is the answer a team-keyed bank needs: its pool states a club per clip, so a clip added for a
        /// player has to be filed under the club the game links that player to rather than under the player.
        /// </summary>
        public static bool TryPlayerTeam(int playerId, out int teamId, out string teamName)
        {
            teamId = 0;
            teamName = null;

            EnsureDatabase();
            EnsurePlayerClubs();

            int club;
            if (playerClubs == null || playerId <= 0 || !playerClubs.TryGetValue(playerId, out club))
                return false;

            teamId = club;
            TryTeamName(club, out teamName);
            return true;
        }

        /// <summary>What the player-club lookup can answer with, or why it cannot - one clause, for a dialog.</summary>
        public static string PlayerClubNote
        {
            get
            {
                EnsurePlayerClubs();
                return playerClubNote;
            }
        }

        /// <summary>
        /// The name the commentator says for a commentary id - the value a <c>surname_ID</c> key carries - from the
        /// game's DB, or false. The DB's <c>commentarynames</c> table states it (<c>commentaryid</c> -&gt;
        /// <c>commentarystring</c>); an id only <c>playernames</c> holds is named from there when one name alone holds it
        /// (900000, which 25,049 of its names share, names none).
        /// </summary>
        public static bool TryCommentaryName(int commentaryId, out string name, out string source)
        {
            EnsureDatabase();
            return FromTable(commentaryNames, commentaryId, "db", out name, out source);
        }

        /// <summary>
        /// A stadium's name from the game's DB (<c>stadiums.stadiumid</c> -&gt; <c>stadiums.name</c>), or false. The value a
        /// <c>stadium_ID</c> or <c>pa_stadium_id</c> key carries is the match setup's stadium id, which FIFA17.exe looks the
        /// <c>audiostadium</c> table up by (FUN_147850760).
        /// </summary>
        public static bool TryStadiumName(int stadiumId, out string name, out string source)
        {
            EnsureDatabase();
            return FromTable(stadiumNames, stadiumId, "db", out name, out source);
        }

        /// <summary>
        /// A league's name from the game's DB (<c>leagues.leagueid</c> -&gt; <c>leagues.leaguename</c>), or false. The value a
        /// <c>pa_league_id</c> key carries is the first of the match setup's two leagueids (+0x84), which FIFA17.exe's match builder
        /// queries the <c>leagues</c> table by (FUN_147aea0b0).
        /// </summary>
        public static bool TryLeagueName(int leagueId, out string name, out string source)
        {
            EnsureDatabase();
            return FromTable(leagueNames, leagueId, "db", out name, out source);
        }

        /// <summary>One line naming what this lookup can currently answer with, for a log or a UI.</summary>
        public static string Status()
        {
            EnsureDatabase();

            return string.Format(CultureInfo.InvariantCulture,
                "parameters {0}/{1} verified | strings {2} | {3}",
                VerifiedParameterCount(), parameterCandidates.Count,
                StringsAvailable() ? "loaded" : "not loaded",
                databaseNote);
        }

        /// <summary>
        /// Re-hashes every embedded parameter name against the id it was stored with. This is the check
        /// that keeps the table honest: a name that does not reproduce its id is not used at runtime.
        /// </summary>
        public static string SelfCheck()
        {
            List<string> bad = new List<string>();
            foreach (KeyValuePair<uint, string> pair in parameterCandidates)
            {
                if (EngineHash(pair.Value) != pair.Key)
                    bad.Add(string.Format(CultureInfo.InvariantCulture, "0x{0:X8}={1}", pair.Key, pair.Value));
            }

            if (bad.Count == 0)
                return string.Format(CultureInfo.InvariantCulture, "{0} parameter names, all {0} reproduce their id",
                    parameterCandidates.Count);

            return string.Format(CultureInfo.InvariantCulture,
                "{0} parameter names, {1} do not reproduce their id: {2}",
                parameterCandidates.Count, bad.Count, string.Join(", ", bad.ToArray()));
        }

        private static int VerifiedParameterCount()
        {
            if (verifiedParameterCount >= 0)
                return verifiedParameterCount;

            int count = 0;
            foreach (KeyValuePair<uint, string> pair in parameterCandidates)
            {
                if (EngineHash(pair.Value) == pair.Key)
                    count++;
            }

            verifiedParameterCount = count;
            return count;
        }

        private static bool FromTable(Dictionary<int, string> table, int id, string source, out string name, out string source2)
        {
            name = null;
            source2 = null;

            string found;
            if (table != null && id != 0 && table.TryGetValue(id, out found) && !string.IsNullOrEmpty(found))
            {
                name = found;
                source2 = source;
                return true;
            }

            return false;
        }

        // ─────────────────────────────────────────────────────────────────────────────────────────
        //  The game's DB
        // ─────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Reads the DB now, when it has not been read yet, so the first clip list that names players does not pay
        /// for it in the middle of being built. Safe on any thread; the DB is read once per session.
        /// </summary>
        public static void Preload()
        {
            EnsureDatabase();
        }

        /// <summary>
        /// An EA Trax song as the DB's `eatrax` table states it, `artist - songname`, by its `songid`; false when the
        /// DB is not reachable or holds no such song. FIFA17.exe reads the same table by the same id
        /// (FUN_14789d6a0: `SELECT eatrax.songid, unlocked, songname, artist, album, intro FROM songplaylistlinks
        /// INNER JOIN eatrax ...`).
        /// </summary>
        public static bool TrySongName(int songId, out string name)
        {
            string source;
            EnsureDatabase();
            return FromTable(songNames, songId, "db", out name, out source);
        }

        private static void EnsureDatabase()
        {
            lock (databaseGate)
            {
                if (databaseTried)
                    return;

                databaseTried = true;
                ReadDatabase();
            }
        }

        private static readonly object databaseGate = new object();
        private static Dictionary<int, string> songNames;

        private static void ReadDatabase()
        {
            try
            {
                if (App.AssetManager == null)
                {
                    databaseNote = "no game is loaded";
                    return;
                }

                Assembly pluginAssembly = FindLoadedAssembly("LegacyDatabasePlugin");
                if (pluginAssembly == null)
                {
                    databaseNote = "the FIFA database plugin is not loaded";
                    return;
                }

                Type readerType = pluginAssembly.GetType("LegacyDatabasePlugin.IO.LegacyDbReader");
                if (readerType == null)
                {
                    databaseNote = "LegacyDbReader is not in the database plugin";
                    return;
                }

                Stream meta = OpenLegacyFile("data/db/fifa_ng_db-meta.xml");
                Stream data = OpenLegacyFile("data/db/fifa_ng_db.db");
                if (data == null)
                {
                    databaseNote = "data/db/fifa_ng_db.db is not in this profile";
                    return;
                }

                object database = null;
                object reader = null;
                try
                {
                    reader = Activator.CreateInstance(readerType, new object[] { meta, data });
                    MethodInfo read = readerType.GetMethod("ReadDb", Type.EmptyTypes);
                    if (read == null)
                    {
                        databaseNote = "LegacyDbReader.ReadDb was not found";
                        return;
                    }

                    database = read.Invoke(reader, null);
                }
                finally
                {
                    IDisposable disposable = reader as IDisposable;
                    if (disposable != null)
                    {
                        try { disposable.Dispose(); } catch (Exception) { }
                    }
                }

                if (database == null)
                {
                    databaseNote = "the DB did not read";
                    return;
                }

                teamNames = ReadPeople(database, new[] { "teams", "team" }, new[] { "teamid", "id", "team_id" },
                    new[] { "teamname", "name", "longname", "shortname", "team_name" }, true);

                // FIFA 17's players table holds no name text: `firstnameid`, `lastnameid` and `commonnameid` are ids into
                // `playernames` (nameid -> name), which is what FIFA17.exe reads its names from (FUN_147463a30:
                // "SELECT nameid, name FROM playernames ORDER BY nameid ASC;"). Measured on the shipped DB's export:
                // 158023 = firstnameid 15448 "Lionel" + lastnameid 17518 "Messi", 20801 = commonnameid 5696
                // "Cristiano Ronaldo". A DB whose players table carries the names as text is still read the old way.
                Dictionary<int, string> nameIds = ReadPeople(database, new[] { "playernames" }, new[] { "nameid" },
                    new[] { "name" }, true);
                playerNames = ReadPlayersByNameIds(database, nameIds)
                    ?? ReadPeople(database, new[] { "players", "player" }, new[] { "playerid", "id", "player_id" },
                        new[] { "commonname", "playername", "lastname", "surname", "name", "firstname" }, false);
                commentaryNames = ReadCommentaryNames(database);
                songNames = ReadSongs(database);
                stadiumNames = ReadPeople(database, new[] { "stadiums" }, new[] { "stadiumid" }, new[] { "name" }, true);
                leagueNames = ReadPeople(database, new[] { "leagues" }, new[] { "leagueid" }, new[] { "leaguename" }, true);

                // The player-club links are read here with the name tables and the database is let go: the table is
                // ~899,000 rows and the map built from it is ~18,000 entries, so holding the whole DB for it would
                // keep tens of megabytes the editor does not otherwise need. Measured at 37 ms on the shipped DB.
                playerClubsTried = true;
                try
                {
                    object links = FindTable(database, new[] { "teamplayerlinks", "teamplayerlink" });
                    playerClubs = links == null ? null : ReadPlayerClubs(links);
                    playerClubNote = playerClubs != null && playerClubs.Count > 0
                        ? "teamplayerlinks names a club for " + playerClubs.Count.ToString("N0", CultureInfo.InvariantCulture) + " players"
                        : "teamplayerlinks links no player to a club";
                }
                catch (Exception ex)
                {
                    playerClubNote = "the player-club links could not be read: " + ex.Message;
                }

                databaseNote = string.Format(CultureInfo.InvariantCulture,
                    "db teams {0}, players {1}, commentary names {2}, songs {3}, stadiums {4}, leagues {5}", CountOf(teamNames),
                    CountOf(playerNames), CountOf(commentaryNames), CountOf(songNames), CountOf(stadiumNames), CountOf(leagueNames));
            }
            catch (Exception ex)
            {
                databaseNote = "the DB could not be read: " + ex.Message;
            }
        }

        private static int CountOf(Dictionary<int, string> table)
        {
            return table == null ? 0 : table.Count;
        }

        // ─────────────────────────────────────────────────────────────────────────────────────────
        //  The player-club links
        // ─────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Makes sure the player-club map was read with the DB, and says why when it was not: the DB itself is
        /// read once (<see cref="EnsureDatabase"/>), which is where <c>teamplayerlinks</c> is turned into the map.
        /// </summary>
        private static void EnsurePlayerClubs()
        {
            if (playerClubsTried)
                return;

            playerClubsTried = true;
            EnsureDatabase();

            if (playerClubs == null && playerClubNote == "not read yet")
                playerClubNote = databaseNote;
        }

        /// <summary>
        /// One row per player-club link as <c>playerid -&gt; teamid</c>. A player with more than one link (a
        /// loan, say) keeps the first the table states, which is the order the game's own data is written in.
        /// </summary>
        private static Dictionary<int, int> ReadPlayerClubs(object table)
        {
            int playerColumn = -1;
            int teamColumn = -1;

            System.Collections.IEnumerable columns = ReadProperty(table, "Columns") as System.Collections.IEnumerable;
            if (columns != null)
            {
                int index = 0;
                foreach (object column in columns)
                {
                    string name = AsText(ReadProperty(column, "Name"));
                    if (name != null)
                    {
                        if (playerColumn < 0 && string.Equals(name, "playerid", StringComparison.OrdinalIgnoreCase))
                            playerColumn = index;
                        if (teamColumn < 0 && string.Equals(name, "teamid", StringComparison.OrdinalIgnoreCase))
                            teamColumn = index;
                    }

                    index++;
                }
            }

            System.Collections.IEnumerable rows = RowsOf(table);
            if (rows == null || playerColumn < 0 || teamColumn < 0)
                return null;

            Dictionary<int, int> result = new Dictionary<int, int>();
            PropertyInfo byIndex = null;

            foreach (object row in rows)
            {
                if (byIndex == null)
                    byIndex = row.GetType().GetProperty("Item", new[] { typeof(int) });
                if (byIndex == null)
                    return null;

                int player = ToInt(byIndex.GetValue(row, new object[] { playerColumn }));
                if (player == 0 || result.ContainsKey(player))
                    continue;

                int team = ToInt(byIndex.GetValue(row, new object[] { teamColumn }));
                if (team != 0)
                    result[player] = team;
            }

            return result.Count > 0 ? result : null;
        }

        private static Stream OpenLegacyFile(string name)
        {
            try
            {
                LegacyFileEntry entry = App.AssetManager.GetCustomAssetEntry<LegacyFileEntry>("legacy", name);
                if (entry == null)
                    return null;
                return App.AssetManager.GetCustomAsset("legacy", entry);
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static Assembly FindLoadedAssembly(string simpleName)
        {
            try
            {
                foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (string.Equals(assembly.GetName().Name, simpleName, StringComparison.OrdinalIgnoreCase))
                        return assembly;
                }
            }
            catch (Exception)
            {
            }

            return null;
        }

        /// <summary>
        /// Reads one table into <c>id -&gt; text</c>, resolving a numeric name through the eng_us strings
        /// (the FIFA NG DB stores some names as plain text and others as string ids). A name column that
        /// only yields an unresolvable id contributes nothing, so the caller falls back honestly.
        /// </summary>
        private static Dictionary<int, string> ReadPeople(object database, string[] tableNames, string[] idColumns,
            string[] nameColumns, bool preferFirstNonEmpty)
        {
            Dictionary<int, string> result = new Dictionary<int, string>();

            object table = FindTable(database, tableNames);
            if (table == null)
                return null;

            System.Collections.IEnumerable rows = RowsOf(table);
            if (rows == null)
                return null;

            foreach (object row in rows)
            {
                int id = ToInt(ReadColumn(row, idColumns));
                if (id == 0)
                    continue;

                string text = null;
                if (preferFirstNonEmpty)
                {
                    foreach (string column in nameColumns)
                    {
                        text = AsText(ReadColumn(row, new[] { column }));
                        if (!string.IsNullOrEmpty(text))
                            break;
                    }
                }
                else
                {
                    // A person is best named by their common name, else first plus last.
                    text = AsText(ReadColumn(row, new[] { "commonname" }));
                    if (string.IsNullOrEmpty(text))
                        text = AsText(ReadColumn(row, new[] { "playername" }));
                    if (string.IsNullOrEmpty(text))
                    {
                        string first = AsText(ReadColumn(row, new[] { "firstname", "first_name" }));
                        string last = AsText(ReadColumn(row, new[] { "lastname", "surname", "last_name" }));
                        if (!string.IsNullOrEmpty(first) && !string.IsNullOrEmpty(last))
                            text = first + " " + last;
                        else
                            text = !string.IsNullOrEmpty(last) ? last : first;
                    }

                    if (string.IsNullOrEmpty(text))
                        text = AsText(ReadColumn(row, new[] { "name" }));
                }

                if (!string.IsNullOrEmpty(text) && !result.ContainsKey(id))
                    result[id] = text;
            }

            return result.Count > 0 ? result : null;
        }

        /// <summary>
        /// `players` read through `playernames`: a player is named by his common name when he has one (Neymar,
        /// Cristiano Ronaldo), else by first and last name. Null when the table has no name-id columns, so the caller
        /// can fall back to a DB that states the names as text.
        /// </summary>
        private static Dictionary<int, string> ReadPlayersByNameIds(object database, Dictionary<int, string> nameIds)
        {
            if (nameIds == null || nameIds.Count == 0)
                return null;

            object table = FindTable(database, new[] { "players", "player" });
            System.Collections.IEnumerable rows = table == null ? null : RowsOf(table);
            if (rows == null)
                return null;

            Dictionary<int, string> result = new Dictionary<int, string>();
            foreach (object row in rows)
            {
                int id = ToInt(ReadColumn(row, new[] { "playerid" }));
                if (id == 0 || result.ContainsKey(id))
                    continue;

                string common = NameOf(nameIds, ReadColumn(row, new[] { "commonnameid" }));
                string text = common;
                if (string.IsNullOrEmpty(text))
                {
                    string first = NameOf(nameIds, ReadColumn(row, new[] { "firstnameid" }));
                    string last = NameOf(nameIds, ReadColumn(row, new[] { "lastnameid" }));
                    text = !string.IsNullOrEmpty(first) && !string.IsNullOrEmpty(last) ? first + " " + last
                        : (!string.IsNullOrEmpty(last) ? last : first);
                }

                if (!string.IsNullOrEmpty(text))
                    result[id] = text;
            }

            return result.Count > 0 ? result : null;
        }

        private static string NameOf(Dictionary<int, string> nameIds, object id)
        {
            string name;
            int key = ToInt(id);
            return key != 0 && nameIds.TryGetValue(key, out name) && !string.IsNullOrWhiteSpace(name) ? name.Trim() : null;
        }

        /// <summary>
        /// `commentaryid -&gt; name` for the commentary names: `commentarynames` (`commentarystring`) first, then the ids
        /// that only `playernames` states, each kept only when every row holding it has the same name. Null when neither
        /// table is there.
        /// </summary>
        private static Dictionary<int, string> ReadCommentaryNames(object database)
        {
            Dictionary<int, string> result = ReadPeople(database, new[] { "commentarynames" }, new[] { "commentaryid" },
                new[] { "commentarystring" }, true) ?? new Dictionary<int, string>();

            object table = FindTable(database, new[] { "playernames" });
            System.Collections.IEnumerable rows = table == null ? null : RowsOf(table);
            if (rows != null)
            {
                Dictionary<int, string> single = new Dictionary<int, string>();
                HashSet<int> shared = new HashSet<int>();
                foreach (object row in rows)
                {
                    int id = ToInt(ReadColumn(row, new[] { "commentaryid" }));
                    string text = AsText(ReadColumn(row, new[] { "name" }));
                    if (id == 0 || string.IsNullOrWhiteSpace(text) || result.ContainsKey(id) || shared.Contains(id))
                        continue;

                    string known;
                    if (!single.TryGetValue(id, out known))
                        single[id] = text.Trim();
                    else if (!string.Equals(known, text.Trim(), StringComparison.OrdinalIgnoreCase))
                    {
                        single.Remove(id);
                        shared.Add(id);
                    }
                }

                foreach (KeyValuePair<int, string> pair in single)
                    result[pair.Key] = pair.Value;
            }

            return result.Count > 0 ? result : null;
        }

        /// <summary>The `eatrax` table as `songid -> artist - songname`, or null when the DB has no such table.</summary>
        private static Dictionary<int, string> ReadSongs(object database)
        {
            object table = FindTable(database, new[] { "eatrax" });
            System.Collections.IEnumerable rows = table == null ? null : RowsOf(table);
            if (rows == null)
                return null;

            Dictionary<int, string> result = new Dictionary<int, string>();
            foreach (object row in rows)
            {
                int id = ToInt(ReadColumn(row, new[] { "songid" }));
                string song = AsText(ReadColumn(row, new[] { "songname" }));
                string artist = AsText(ReadColumn(row, new[] { "artist" }));
                if (id == 0 || result.ContainsKey(id) || string.IsNullOrEmpty(song))
                    continue;
                result[id] = string.IsNullOrEmpty(artist) ? song : artist + " - " + song;
            }

            return result.Count > 0 ? result : null;
        }

        private static object FindTable(object database, string[] tableNames)
        {
            if (database == null)
                return null;

            Type databaseType = database.GetType();
            PropertyInfo indexer = databaseType.GetProperty("Item", new[] { typeof(string) });

            if (indexer != null)
            {
                foreach (string name in tableNames)
                {
                    try
                    {
                        object table = indexer.GetValue(database, new object[] { name });
                        if (table != null)
                            return table;
                    }
                    catch (Exception)
                    {
                    }
                }
            }

            PropertyInfo tablesProperty = databaseType.GetProperty("Tables");
            if (tablesProperty == null)
                return null;

            System.Collections.IEnumerable tables = tablesProperty.GetValue(database, null) as System.Collections.IEnumerable;
            if (tables == null)
                return null;

            foreach (object table in tables)
            {
                string name = AsText(ReadProperty(table, "Name")) ?? AsText(ReadProperty(table, "ShortName"));
                if (string.IsNullOrEmpty(name))
                    continue;

                foreach (string wanted in tableNames)
                {
                    if (string.Equals(name, wanted, StringComparison.OrdinalIgnoreCase))
                        return table;
                }
            }

            return null;
        }

        private static System.Collections.IEnumerable RowsOf(object table)
        {
            PropertyInfo rowsProperty = table.GetType().GetProperty("Rows");
            if (rowsProperty == null)
                return null;
            return rowsProperty.GetValue(table, null) as System.Collections.IEnumerable;
        }

        private static object ReadProperty(object target, string name)
        {
            try
            {
                PropertyInfo property = target.GetType().GetProperty(name);
                return property == null ? null : property.GetValue(target, null);
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>Reads the first column of a row that exists and has a value.</summary>
        private static object ReadColumn(object row, string[] columns)
        {
            PropertyInfo indexer = row.GetType().GetProperty("Item", new[] { typeof(string) });
            if (indexer == null)
                return null;

            foreach (string column in columns)
            {
                try
                {
                    object value = indexer.GetValue(row, new object[] { column });
                    if (value != null)
                        return value;
                }
                catch (Exception)
                {
                }
            }

            return null;
        }

        private static string AsText(object value)
        {
            if (value == null)
                return null;

            string text = value as string;
            if (text != null)
            {
                text = text.Trim();
                return text.Length == 0 ? null : text;
            }

            // A numeric name column is a string id: resolve it through the eng_us strings, and say
            // nothing when they are not loaded rather than showing the raw id as a name.
            if (value is int || value is uint || value is short || value is ushort || value is long)
            {
                uint id;
                try
                {
                    id = Convert.ToUInt32(value, CultureInfo.InvariantCulture);
                }
                catch (Exception)
                {
                    return null;
                }

                string localized = id == 0 ? "" : Localized(id);
                return localized.Length == 0 ? null : localized;
            }

            return null;
        }

        private static int ToInt(object value)
        {
            if (value == null)
                return 0;

            string text = value as string;
            if (text != null)
            {
                int parsed;
                return int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed) ? parsed : 0;
            }

            try
            {
                return Convert.ToInt32(value, CultureInfo.InvariantCulture);
            }
            catch (Exception)
            {
                return 0;
            }
        }
    }
}
