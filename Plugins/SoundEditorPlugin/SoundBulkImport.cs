using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace SoundEditorPlugin
{
    /// <summary>One line of a mapping.txt: an audio file and the values it is filed under, in the order written.</summary>
    public sealed class MappingLine
    {
        public int Number;
        public string File;
        public List<KeyValuePair<string, string>> Values = new List<KeyValuePair<string, string>>();
        public string Problem;

        public string Value(string name)
        {
            foreach (KeyValuePair<string, string> pair in Values)
            {
                if (string.Equals(pair.Key, name, StringComparison.OrdinalIgnoreCase))
                    return pair.Value;
            }

            return null;
        }
    }

    /// <summary>
    /// mapping.txt: which audio file of a folder goes into which clip of the open asset. One line per file:
    ///
    ///   file name.wav = parameter=value, parameter=value
    ///
    /// A value is a number (decimal, or 0x hex) or a name the game's event system gives that value (home, HIGH); a
    /// value with a comma in it is written in double quotes. Lines starting with # are notes. The same file can also be
    /// a table: a first line "file", TAB, then the parameter names, and one row per file.
    /// </summary>
    public static class MappingFile
    {
        public const string Name = "mapping.txt";

        public static readonly string[] AudioExtensions = { ".wav", ".mp3", ".ealayer3" };

        /// <summary>The file's text, from its byte order mark (UTF-8 when it has none).</summary>
        public static string Read(string path)
        {
            using (StreamReader reader = new StreamReader(path, new UTF8Encoding(false), true))
                return reader.ReadToEnd();
        }

        public static bool IsAudioFile(string name)
        {
            return name != null && AudioExtensions.Any(e => name.EndsWith(e, StringComparison.OrdinalIgnoreCase));
        }

        public static List<MappingLine> Parse(string text)
        {
            List<MappingLine> lines = new List<MappingLine>();
            string[] header = null;
            string[] raw = (text ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

            for (int i = 0; i < raw.Length; i++)
            {
                string line = raw[i].Trim();
                if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal) || line.StartsWith("//", StringComparison.Ordinal))
                    continue;

                MappingLine parsed = new MappingLine { Number = i + 1 };

                // A table: "file<TAB>param<TAB>param", then one row per file.
                if (header == null && lines.Count == 0 && line.IndexOf('\t') > 0)
                {
                    string first = line.Split('\t')[0].Trim();
                    if (string.Equals(first, "file", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(first, "filename", StringComparison.OrdinalIgnoreCase))
                    {
                        header = line.Split('\t').Select(c => c.Trim()).ToArray();
                        continue;
                    }
                }

                if (header != null)
                {
                    string[] cells = raw[i].Split('\t');
                    parsed.File = Unquote(cells[0].Trim());
                    for (int c = 1; c < cells.Length && c < header.Length; c++)
                    {
                        string cell = Unquote(cells[c].Trim());
                        if (cell.Length > 0 && header[c].Length > 0)
                            parsed.Values.Add(new KeyValuePair<string, string>(header[c], cell));
                    }

                    if (cells.Length > header.Length && cells.Skip(header.Length).Any(c => c.Trim().Length > 0))
                        parsed.Problem = "line " + parsed.Number + " has more cells than the header has names";
                }
                else
                {
                    string file, rest;
                    if (!SplitFile(line, out file, out rest))
                    {
                        parsed.File = line;
                        parsed.Problem = "line " + parsed.Number + " has no '=' after its file name (write it as: file.wav = parameter=value)";
                        lines.Add(parsed);
                        continue;
                    }

                    parsed.File = Unquote(file);
                    string problem = ParsePairs(rest, parsed.Values);
                    if (problem != null)
                        parsed.Problem = "line " + parsed.Number + ": " + problem;
                }

                if (parsed.Problem == null && string.IsNullOrWhiteSpace(parsed.File))
                    parsed.Problem = "line " + parsed.Number + " names no file";
                else if (parsed.Problem == null && !IsAudioFile(parsed.File))
                    parsed.Problem = "line " + parsed.Number + ": " + parsed.File + " is not a .wav, .mp3 or .ealayer3 file";
                lines.Add(parsed);
            }

            return lines;
        }

        /// <summary>
        /// The file name ends at the first '=' that follows an audio extension (so a name may hold spaces and '='), else
        /// at the first " = ", else at the first tab.
        /// </summary>
        private static bool SplitFile(string line, out string file, out string rest)
        {
            file = rest = null;
            bool quoted = false;
            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];
                if (c == '"')
                    quoted = !quoted;
                if (quoted || c != '=')
                    continue;
                string before = line.Substring(0, i).Trim().Trim('"');
                if (IsAudioFile(before))
                {
                    file = line.Substring(0, i).Trim();
                    rest = line.Substring(i + 1);
                    return true;
                }
            }

            int spaced = line.IndexOf(" = ", StringComparison.Ordinal);
            if (spaced > 0)
            {
                file = line.Substring(0, spaced).Trim();
                rest = line.Substring(spaced + 3);
                return true;
            }

            int tab = line.IndexOf('\t');
            if (tab > 0)
            {
                file = line.Substring(0, tab).Trim();
                rest = line.Substring(tab + 1);
                return true;
            }

            return false;
        }

        /// <summary>name=value pairs separated by commas or semicolons; a value may be in double quotes ("" is a quote).</summary>
        private static string ParsePairs(string text, List<KeyValuePair<string, string>> pairs)
        {
            int i = 0;
            while (i < text.Length)
            {
                while (i < text.Length && (char.IsWhiteSpace(text[i]) || text[i] == ',' || text[i] == ';'))
                    i++;
                if (i >= text.Length)
                    break;

                int eq = text.IndexOf('=', i);
                int stop = IndexOfSeparator(text, i);
                if (eq < 0 || (stop >= 0 && stop < eq))
                {
                    string bad = (stop < 0 ? text.Substring(i) : text.Substring(i, stop - i)).Trim();
                    return "'" + bad + "' is not written as name=value";
                }

                string name = text.Substring(i, eq - i).Trim();
                if (name.Length == 0)
                    return "a value has no name before its '='";

                i = eq + 1;
                while (i < text.Length && text[i] == ' ')
                    i++;

                string value;
                if (i < text.Length && text[i] == '"')
                {
                    StringBuilder quoted = new StringBuilder();
                    i++;
                    bool closed = false;
                    while (i < text.Length)
                    {
                        if (text[i] == '"')
                        {
                            if (i + 1 < text.Length && text[i + 1] == '"')
                            {
                                quoted.Append('"');
                                i += 2;
                                continue;
                            }

                            closed = true;
                            i++;
                            break;
                        }

                        quoted.Append(text[i]);
                        i++;
                    }

                    if (!closed)
                        return "the value of " + name + " opens a quote it does not close";
                    value = quoted.ToString();
                }
                else
                {
                    int end = IndexOfSeparator(text, i);
                    value = (end < 0 ? text.Substring(i) : text.Substring(i, end - i)).Trim();
                    i = end < 0 ? text.Length : end;
                }

                pairs.Add(new KeyValuePair<string, string>(name, value));
            }

            return null;
        }

        private static int IndexOfSeparator(string text, int from)
        {
            for (int i = from; i < text.Length; i++)
            {
                if (text[i] == ',' || text[i] == ';')
                    return i;
            }

            return -1;
        }

        private static string Unquote(string text)
        {
            text = (text ?? "").Trim();
            if (text.Length >= 2 && text[0] == '"' && text[text.Length - 1] == '"')
                return text.Substring(1, text.Length - 2).Replace("\"\"", "\"");
            return text;
        }

        /// <summary>A value as the template writes it: quoted when it holds a separator, a quote or edge spaces.</summary>
        public static string QuoteValue(string value)
        {
            value = value ?? "";
            bool needs = value.Length == 0 || value.IndexOfAny(new[] { ',', ';', '"', '=', '#' }) >= 0 || value.Trim() != value;
            return needs ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;
        }
    }

    /// <summary>An EA Trax song's DB metadata: the game DB's `eatrax` and `songplaylistlinks` columns for it.</summary>
    public sealed class EaTraxSongInfo
    {
        public int SongId;
        public string Title;       // eatrax.songname
        public string Artist;      // eatrax.artist
        public string Album;       // eatrax.album
        public int Intro = 1;      // eatrax.intro (every shipped song: 1)
        public int PlaylistId;     // songplaylistlinks.playlistid (every shipped song: 0)
        public int Unlocked = 1;   // songplaylistlinks.unlocked (every shipped song: 1)

        /// <summary>`artist - songname`, the way the editor names a song from the DB.</summary>
        public string DisplayName
        {
            get
            {
                string title = (Title ?? "").Trim();
                string artist = (Artist ?? "").Trim();
                return artist.Length == 0 ? title : artist + " - " + title;
            }
        }
    }

    /// <summary>How the open asset files its clips, which is what a mapping line is read against.</summary>
    public enum BulkKind
    {
        /// <summary>A sound bank: every clip is filed under a key (a value per selection parameter).</summary>
        Bank,
        /// <summary>An asset whose clips are filed under nothing: a line names a clip by its number.</summary>
        Clips,
        /// <summary>An EA Trax bank: a song is filed under its songid, and carries the DB's metadata.</summary>
        Songs,
    }

    /// <summary>What the open asset is: its kind, its clips (in the list's order) and what each is filed under.</summary>
    public sealed class BulkTarget
    {
        public string AssetName;
        public BulkKind Kind;

        /// <summary>The bank's key parameters, in the order its keys are stated.</summary>
        public IList<uint> KeyParameters = new List<uint>();

        /// <summary>Per clip of the list: the key it is filed under (Bank), null when it is not known.</summary>
        public IList<SoundBankClipKey> ClipKeys = new List<SoundBankClipKey>();

        /// <summary>Per clip of the list: the name the list shows.</summary>
        public IList<string> ClipNames = new List<string>();

        /// <summary>Per clip of the list: the songid it plays (Songs), 0 when none.</summary>
        public IList<int> ClipSongIds = new List<int>();

        /// <summary>Per clip of the list: its DB metadata when the DB (or this session) has it (Songs).</summary>
        public IList<EaTraxSongInfo> ClipSongs = new List<EaTraxSongInfo>();

        public bool CanReplace = true;
        public string ReplaceRefusal;
        public bool CanAdd;
        public string AddRefusal;

        public int ClipCount { get { return ClipNames.Count; } }
    }

    /// <summary>One thing a bulk import does: an audio file into a clip, or a new clip.</summary>
    public sealed class BulkStep
    {
        public int Line;
        public string File;
        public string Path;

        /// <summary>True for a new clip (or song); false for a replace of <see cref="Clip"/>.</summary>
        public bool Add;

        /// <summary>The clip a replace writes, as an index into the editor's list.</summary>
        public int Clip = -1;

        /// <summary>Bank: the key the clip is (or a new clip will be) filed under.</summary>
        public SoundBankClipKey Key;

        /// <summary>Songs: the songid, and the metadata the line gives (null when it gives none).</summary>
        public int SongId;
        public EaTraxSongInfo Song;

        public string What;

        public override string ToString()
        {
            return What;
        }
    }

    /// <summary>What a mapping.txt asks of the open asset, checked before anything is read or written.</summary>
    public sealed class BulkPlan
    {
        public string MappingPath;
        public string Folder;
        public int Lines;
        public List<BulkStep> Steps = new List<BulkStep>();
        public List<string> Problems = new List<string>();

        public int Replaces { get { return Steps.Count(s => !s.Add); } }
        public int Adds { get { return Steps.Count(s => s.Add); } }

        public string Summary()
        {
            StringBuilder text = new StringBuilder();
            text.AppendFormat(CultureInfo.InvariantCulture, "{0} line(s) in {1}: {2} replace an existing clip, {3} add a new one",
                Lines, MappingPath ?? MappingFile.Name, Replaces, Adds);
            if (Problems.Count > 0)
                text.AppendFormat(CultureInfo.InvariantCulture, "; {0} line(s) cannot be imported", Problems.Count);
            return text.ToString();
        }
    }

    /// <summary>Reads a mapping against the open asset: which clip each file replaces, or the clip it adds.</summary>
    public static class BulkPlanner
    {
        public static BulkPlan Plan(BulkTarget target, string mappingPath)
        {
            List<MappingLine> lines = MappingFile.Parse(MappingFile.Read(mappingPath));
            return Plan(target, lines, System.IO.Path.GetDirectoryName(mappingPath), File.Exists, mappingPath);
        }

        public static BulkPlan Plan(BulkTarget target, IList<MappingLine> lines, string folder, Func<string, bool> fileExists,
            string mappingPath = null)
        {
            BulkPlan plan = new BulkPlan { Folder = folder, MappingPath = mappingPath, Lines = lines.Count };
            HashSet<int> replaced = new HashSet<int>();
            Dictionary<string, int> takesUsed = new Dictionary<string, int>(StringComparer.Ordinal);
            Dictionary<string, int> addsPerKey = new Dictionary<string, int>(StringComparer.Ordinal);
            HashSet<int> songsAdded = new HashSet<int>();

            // A parameter every clip of the bank has one value of (the Language of a commentary bank) is that value when
            // a line does not state it, as the Add Clip window does.
            Dictionary<uint, HashSet<int>> seen = new Dictionary<uint, HashSet<int>>();
            foreach (uint id in target.KeyParameters)
                seen[id] = new HashSet<int>();
            foreach (SoundBankClipKey key in target.ClipKeys)
            {
                if (key == null)
                    continue;
                foreach (KeyValuePair<uint, int> pair in key.Values)
                {
                    HashSet<int> set;
                    if (seen.TryGetValue(pair.Key, out set))
                        set.Add(pair.Value);
                }
            }

            foreach (MappingLine line in lines)
            {
                if (line.Problem != null)
                {
                    plan.Problems.Add(line.Problem);
                    continue;
                }

                string path = System.IO.Path.Combine(folder ?? "", line.File);
                if (fileExists != null && !fileExists(path))
                {
                    plan.Problems.Add("line " + line.Number + ": " + line.File + " is not in the folder");
                    continue;
                }

                string problem;
                BulkStep step = new BulkStep { Line = line.Number, File = line.File, Path = path };
                switch (target.Kind)
                {
                    case BulkKind.Bank:
                        problem = PlanBankLine(target, line, step, seen, replaced, takesUsed, addsPerKey);
                        break;
                    case BulkKind.Songs:
                        problem = PlanSongLine(target, line, step, replaced, songsAdded);
                        break;
                    default:
                        problem = PlanClipLine(target, line, step, replaced);
                        break;
                }

                if (problem != null)
                {
                    plan.Problems.Add("line " + line.Number + " (" + line.File + "): " + problem);
                    continue;
                }

                if (step.Add && !target.CanAdd)
                {
                    plan.Problems.Add("line " + line.Number + " (" + line.File + ") would add a clip, and "
                        + (target.AddRefusal ?? "this asset cannot take a new clip"));
                    continue;
                }

                if (!step.Add && !target.CanReplace)
                {
                    plan.Problems.Add("line " + line.Number + " (" + line.File + "): " + (target.ReplaceRefusal ?? "this asset's clips cannot be replaced"));
                    continue;
                }

                plan.Steps.Add(step);
            }

            return plan;
        }

        private static string PlanClipLine(BulkTarget target, MappingLine line, BulkStep step, HashSet<int> replaced)
        {
            foreach (KeyValuePair<string, string> pair in line.Values)
            {
                if (!string.Equals(pair.Key, "clip", StringComparison.OrdinalIgnoreCase))
                    return "this asset's clips are not filed under parameters, so " + pair.Key + " means nothing to it (name a clip: clip=3, or clip=new)";
            }

            string clip = line.Value("clip");
            if (clip == null)
                return "this asset's clips are not filed under parameters: say which clip (clip=3) or add one (clip=new)";
            return PlanClipNumber(target, clip, step, replaced);
        }

        private static string PlanClipNumber(BulkTarget target, string clip, BulkStep step, HashSet<int> replaced)
        {
            if (string.Equals(clip.Trim(), "new", StringComparison.OrdinalIgnoreCase))
            {
                step.Add = true;
                step.What = "adds a clip";
                return null;
            }

            int number;
            if (!TryInt(clip, out number) || number < 1 || number > target.ClipCount)
                return "clip=" + clip + " is not a clip of this asset (1.." + target.ClipCount + ")";
            if (!replaced.Add(number - 1))
                return "clip " + number + " is already replaced by an earlier line";
            step.Clip = number - 1;
            step.What = "replaces clip " + number + " (" + target.ClipNames[number - 1] + ")";
            return null;
        }

        private static string PlanBankLine(BulkTarget target, MappingLine line, BulkStep step, Dictionary<uint, HashSet<int>> seen,
            HashSet<int> replaced, Dictionary<string, int> takesUsed, Dictionary<string, int> addsPerKey)
        {
            SoundBankClipKey key = new SoundBankClipKey();
            string clip = null, take = null;
            foreach (KeyValuePair<string, string> pair in line.Values)
            {
                if (string.Equals(pair.Key, "clip", StringComparison.OrdinalIgnoreCase)) { clip = pair.Value; continue; }
                if (string.Equals(pair.Key, "take", StringComparison.OrdinalIgnoreCase)) { take = pair.Value; continue; }

                uint id = ParameterOf(target, pair.Key);
                if (id == 0)
                    return "this bank is not looked up by " + pair.Key + "; it is looked up by "
                        + (target.KeyParameters.Count == 0 ? "nothing (name a clip: clip=3)" : string.Join(", ", target.KeyParameters.Select(SoundBankParameterTable.NameOf).ToArray()));
                int value;
                string why = ParseValue(id, pair.Value, out value);
                if (why != null)
                    return why;
                if (key.Values.ContainsKey(id))
                    return SoundBankParameterTable.NameOf(id) + " is given twice";
                key.Set(id, value);
            }

            if (clip != null)
            {
                if (string.Equals(clip.Trim(), "new", StringComparison.OrdinalIgnoreCase))
                {
                    string missing = FillKey(target, key, seen);
                    if (missing != null)
                        return missing;
                    step.Add = true;
                    step.Key = key;
                    step.What = "adds a clip filed under " + Describe(target, key);
                    return null;
                }

                string clipProblem = PlanClipNumber(target, clip, step, replaced);
                if (clipProblem != null)
                    return clipProblem;
                SoundBankClipKey own = target.ClipKeys.Count > step.Clip ? target.ClipKeys[step.Clip] : null;
                foreach (KeyValuePair<uint, int> pair in key.Values)
                {
                    int has;
                    if (own != null && own.Values.TryGetValue(pair.Key, out has) && has != pair.Value)
                        return "clip " + (step.Clip + 1) + " is filed under " + SoundBankParameterTable.NameOf(pair.Key) + "=" + has
                            + ", not " + pair.Value + " (a replace keeps the clip's key; leave clip= out to add a clip under the new key)";
                }

                step.Key = own;
                return null;
            }

            if (target.KeyParameters.Count == 0)
                return "this bank's clips are not filed under parameters: say which clip (clip=3) or add one (clip=new)";

            string fill = FillKey(target, key, seen);
            if (fill != null)
                return fill;

            string text = KeyText(target, key);
            List<int> holders = new List<int>();
            for (int i = 0; i < target.ClipKeys.Count; i++)
            {
                if (target.ClipKeys[i] != null && KeyText(target, target.ClipKeys[i]) == text)
                    holders.Add(i);
            }

            int takeNumber = -1;
            if (take != null && (!TryInt(take, out takeNumber) || takeNumber < 1))
                return "take=" + take + " is not a take number (1, 2, ...)";

            int index;
            if (takeNumber > 0)
            {
                index = takeNumber - 1;
            }
            else
            {
                int used;
                takesUsed.TryGetValue(text, out used);
                index = used;
                takesUsed[text] = used + 1;
            }

            step.Key = key;
            if (index < holders.Count)
            {
                int ordinal = holders[index];
                if (!replaced.Add(ordinal))
                    return "clip " + (ordinal + 1) + " (" + Describe(target, key) + ", take " + (index + 1) + ") is already replaced by an earlier line";
                step.Clip = ordinal;
                step.What = "replaces clip " + (ordinal + 1) + " (" + target.ClipNames[ordinal] + ")"
                    + (holders.Count > 1 ? ", take " + (index + 1) + " of " + holders.Count : "");
                return null;
            }

            int adds;
            addsPerKey.TryGetValue(text, out adds);
            addsPerKey[text] = adds + 1;
            step.Add = true;
            step.What = "adds a clip filed under " + Describe(target, key)
                + (holders.Count > 0 ? " (take " + (holders.Count + adds + 1) + ")" : "");
            return null;
        }

        private static string PlanSongLine(BulkTarget target, MappingLine line, BulkStep step, HashSet<int> replaced, HashSet<int> songsAdded)
        {
            EaTraxSongInfo info = new EaTraxSongInfo();
            bool hasInfo = false;
            string clip = null, songText = null;
            foreach (KeyValuePair<string, string> pair in line.Values)
            {
                string name = pair.Key.Trim().ToLowerInvariant();
                int number;
                switch (name)
                {
                    case "clip": clip = pair.Value; break;
                    case "songid": songText = pair.Value; break;
                    case "title": case "songname": info.Title = pair.Value; hasInfo = true; break;
                    case "artist": info.Artist = pair.Value; hasInfo = true; break;
                    case "album": info.Album = pair.Value; hasInfo = true; break;
                    case "intro":
                        if (!TryInt(pair.Value, out number)) return "intro=" + pair.Value + " is not a number";
                        info.Intro = number; hasInfo = true; break;
                    case "playlistid":
                        if (!TryInt(pair.Value, out number)) return "playlistid=" + pair.Value + " is not a number";
                        info.PlaylistId = number; hasInfo = true; break;
                    case "unlocked":
                        if (!TryInt(pair.Value, out number)) return "unlocked=" + pair.Value + " is not a number";
                        info.Unlocked = number; hasInfo = true; break;
                    default:
                        return "an EA Trax line takes songid, title, artist, album, intro, playlistid, unlocked or clip, not " + pair.Key;
                }
            }

            if (clip != null && songText == null)
            {
                string problem = PlanClipNumber(target, clip, step, replaced);
                if (problem != null)
                    return problem;
                if (step.Add)
                    return "a new EA Trax song needs its songid (songid=58)";
                step.SongId = target.ClipSongIds.Count > step.Clip ? target.ClipSongIds[step.Clip] : 0;
                info.SongId = step.SongId;
                step.Song = hasInfo ? info : null;
                return null;
            }

            int songId;
            if (songText == null)
                return "an EA Trax line names its song by songid (songid=58), or a track by its number (clip=3)";
            if (!TryInt(songText, out songId) || songId < 1)
                return "songid=" + songText + " is not a songid (a whole number from 1)";
            info.SongId = songId;
            step.SongId = songId;
            step.Song = hasInfo ? info : null;

            int at = target.ClipSongIds.IndexOf(songId);
            if (at >= 0)
            {
                if (!replaced.Add(at))
                    return "songid " + songId + " is already replaced by an earlier line";
                step.Clip = at;
                step.What = "replaces songid " + songId + " (" + target.ClipNames[at] + ")" + (hasInfo ? " and states its metadata" : "");
                return null;
            }

            if (!songsAdded.Add(songId))
                return "songid " + songId + " is added by an earlier line";
            if (string.IsNullOrWhiteSpace(info.Title))
                info.Title = System.IO.Path.GetFileNameWithoutExtension(line.File);
            step.Song = info;
            step.Add = true;
            string known;
            step.What = "adds songid " + songId + " (" + info.DisplayName + ")"
                + (FifaNameLookup.TrySongName(songId, out known) ? " - note: the DB already names songid " + songId + " " + known : "");
            return null;
        }

        /// <summary>A key parameter of the bank by its name or its 0x id, or 0.</summary>
        private static uint ParameterOf(BulkTarget target, string name)
        {
            string trimmed = (name ?? "").Trim();
            foreach (uint id in target.KeyParameters)
            {
                if (string.Equals(SoundBankParameterTable.NameOf(id), trimmed, StringComparison.OrdinalIgnoreCase))
                    return id;
            }

            uint hex;
            if (trimmed.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                && uint.TryParse(trimmed.Substring(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out hex)
                && target.KeyParameters.Contains(hex))
                return hex;
            return 0;
        }

        /// <summary>A value: a number, a value name of the parameter, or named bits joined with +.</summary>
        public static string ParseValue(uint id, string text, out int value)
        {
            value = 0;
            string trimmed = (text ?? "").Trim();
            if (TryInt(trimmed, out value))
                return null;

            SoundBankParameterTable.Entry entry = SoundBankParameterTable.Find(id);
            string name = SoundBankParameterTable.NameOf(id);
            if (entry == null || entry.ValueNames.Length == 0)
                return name + "=" + trimmed + " is not a number (" + name + " has no named values)";

            int total = 0;
            foreach (string part in trimmed.Split('+', '|'))
            {
                string word = part.Trim();
                int at = Array.FindIndex(entry.ValueNames, n => string.Equals(n, word, StringComparison.OrdinalIgnoreCase));
                if (at < 0)
                    return name + "=" + trimmed + ": " + word + " is not one of its values ("
                        + string.Join(", ", entry.ValueNames.Take(12).ToArray()) + (entry.ValueNames.Length > 12 ? ", ..." : "") + ")";
                total |= entry.Values[at];
            }

            value = total;
            return null;
        }

        private static string FillKey(BulkTarget target, SoundBankClipKey key, Dictionary<uint, HashSet<int>> seen)
        {
            foreach (uint id in target.KeyParameters)
            {
                if (key.Values.ContainsKey(id))
                    continue;
                HashSet<int> values;
                if (seen.TryGetValue(id, out values) && values.Count == 1)
                {
                    key.Set(id, values.First());
                    continue;
                }

                return "give a value for " + SoundBankParameterTable.NameOf(id) + " ("
                    + (values == null || values.Count == 0 ? "no clip of this bank has one yet" : "values in " + SoundTrackNaming.DescribeBankValues(values)) + ")";
            }

            return null;
        }

        /// <summary>A key in the bank's own parameter order, for matching clips.</summary>
        internal static string KeyText(BulkTarget target, SoundBankClipKey key)
        {
            StringBuilder text = new StringBuilder();
            foreach (uint id in target.KeyParameters)
            {
                int value;
                text.Append(key.Values.TryGetValue(id, out value) ? value.ToString(CultureInfo.InvariantCulture) : "?").Append('|');
            }

            return text.ToString();
        }

        internal static string Describe(BulkTarget target, SoundBankClipKey key)
        {
            List<string> parts = new List<string>();
            foreach (uint id in target.KeyParameters)
            {
                int value;
                if (!key.Values.TryGetValue(id, out value))
                    continue;
                string valueName = SoundBankParameterTable.ValueName(id, value);
                parts.Add(SoundBankParameterTable.NameOf(id) + "=" + value.ToString(CultureInfo.InvariantCulture)
                    + (valueName == null ? "" : " (" + valueName + ")"));
            }

            return parts.Count == 0 ? "no key" : string.Join(", ", parts.ToArray());
        }

        public static bool TryInt(string text, out int value)
        {
            text = (text ?? "").Trim();
            if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                uint hex;
                bool ok = uint.TryParse(text.Substring(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out hex);
                value = unchecked((int)hex);
                return ok;
            }

            return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
        }
    }

    /// <summary>The names Export All gives clip files, which is what a mapping written for the asset names them.</summary>
    public static class SoundFileNames
    {
        /// <summary>The clip's name with what a Windows file name cannot hold replaced, or clip_00012 when it has none.</summary>
        public static string Safe(string name, int index)
        {
            if (string.IsNullOrWhiteSpace(name))
                return string.Format(CultureInfo.InvariantCulture, "clip_{0:D5}", index);

            StringBuilder builder = new StringBuilder();
            foreach (char c in name)
            {
                bool bad = c < ' ' || c == '<' || c == '>' || c == ':' || c == '"' || c == '/' || c == '\\'
                    || c == '|' || c == '?' || c == '*';
                builder.Append(bad ? '_' : c);
            }

            string cleaned = builder.ToString().Trim().TrimEnd('.');
            if (cleaned.Length == 0)
                return string.Format(CultureInfo.InvariantCulture, "clip_{0:D5}", index);

            return cleaned.Length > 120 ? cleaned.Substring(0, 120) : cleaned;
        }

        /// <summary>
        /// Every clip's WAV file name as Export All writes them into an empty folder: the safe name, and " (2)", " (3)"
        /// for a name an earlier clip already took (compared the way Windows compares names, ignoring case).
        /// </summary>
        public static List<string> ForClips(IList<string> clipNames)
        {
            List<string> files = new List<string>(clipNames.Count);
            HashSet<string> taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < clipNames.Count; i++)
            {
                string stem = Safe(clipNames[i], i + 1);
                string file = stem + ".wav";
                for (int suffix = 2; taken.Contains(file); suffix++)
                    file = stem + " (" + suffix.ToString(CultureInfo.InvariantCulture) + ").wav";
                taken.Add(file);
                files.Add(file);
            }

            return files;
        }
    }

    /// <summary>The mapping.txt the editor writes for the open asset: how to fill it in, then one line per clip it has.</summary>
    public static class MappingTemplate
    {
        public static string For(BulkTarget target, IList<string> fileNames)
        {
            StringBuilder text = new StringBuilder();
            text.AppendLine("# mapping.txt for " + (target.AssetName ?? "this sound asset"));
            text.AppendLine("#");
            text.AppendLine("# Bulk Import (right-click a clip) reads this file and the audio files beside it. One line per file:");
            text.AppendLine("#     file name.wav = parameter=value, parameter=value");
            text.AppendLine("# Files are .wav, .mp3 or .ealayer3, named relative to this folder. Lines starting with # are notes.");
            text.AppendLine("# A value with a comma in it goes in double quotes. Every line is checked before anything is written,");
            text.AppendLine("# and the import is one edit of the asset.");
            text.AppendLine("#");

            switch (target.Kind)
            {
                case BulkKind.Bank:
                    WriteBankHelp(text, target);
                    break;
                case BulkKind.Songs:
                    WriteSongHelp(text);
                    break;
                default:
                    text.AppendLine("# This asset's clips are not filed under parameters, so a line names a clip by its number in the list:");
                    text.AppendLine("#     clip=3       replaces clip 3's audio");
                    text.AppendLine("#     clip=new     adds a clip" + (target.CanAdd ? "" : " (this asset cannot take one: " + (target.AddRefusal ?? "adding is not supported") + ")"));
                    text.AppendLine("#");
                    break;
            }

            if (!target.CanReplace)
                text.AppendLine("# Note: " + (target.ReplaceRefusal ?? "this asset's clips cannot be replaced") + ".");
            if (target.Kind != BulkKind.Clips && !target.CanAdd)
                text.AppendLine("# Note: a line that would add a clip is refused: " + (target.AddRefusal ?? "this asset cannot take a new clip") + ".");

            text.AppendLine("# Every clip the asset has now, named the way Export All Clips names its file:");
            for (int i = 0; i < target.ClipCount && i < fileNames.Count; i++)
            {
                if (fileNames[i] != null)
                    text.AppendLine(Line(target, i, fileNames[i]));
            }

            return text.ToString();
        }

        private static string Line(BulkTarget target, int i, string file)
        {
            string name = file.IndexOf('=') >= 0 || file.IndexOf('#') >= 0 ? "\"" + file + "\"" : file;
            switch (target.Kind)
            {
                case BulkKind.Bank:
                    SoundBankClipKey key = i < target.ClipKeys.Count ? target.ClipKeys[i] : null;
                    if (key == null || target.KeyParameters.Count == 0)
                        return name + " = clip=" + (i + 1).ToString(CultureInfo.InvariantCulture);
                    return name + " = " + string.Join(", ", target.KeyParameters.Select(id =>
                    {
                        int value;
                        key.Values.TryGetValue(id, out value);
                        return SoundBankParameterTable.NameOf(id) + "=" + value.ToString(CultureInfo.InvariantCulture);
                    }).ToArray());
                case BulkKind.Songs:
                    int songId = i < target.ClipSongIds.Count ? target.ClipSongIds[i] : 0;
                    EaTraxSongInfo song = i < target.ClipSongs.Count ? target.ClipSongs[i] : null;
                    if (songId <= 0)
                        return name + " = clip=" + (i + 1).ToString(CultureInfo.InvariantCulture);
                    StringBuilder line = new StringBuilder(name).Append(" = songid=").Append(songId.ToString(CultureInfo.InvariantCulture));
                    if (song != null)
                    {
                        line.Append(", title=").Append(MappingFile.QuoteValue(song.Title))
                            .Append(", artist=").Append(MappingFile.QuoteValue(song.Artist))
                            .Append(", album=").Append(MappingFile.QuoteValue(song.Album));
                    }

                    return line.ToString();
                default:
                    return name + " = clip=" + (i + 1).ToString(CultureInfo.InvariantCulture);
            }
        }

        private static void WriteBankHelp(StringBuilder text, BulkTarget target)
        {
            if (target.KeyParameters.Count == 0)
            {
                text.AppendLine("# This bank's clips are filed under no parameter, so a line names a clip by its number:");
                text.AppendLine("#     clip=3       replaces clip 3's audio");
                text.AppendLine("#     clip=new     adds a clip");
                text.AppendLine("#");
                return;
            }

            text.AppendLine("# The game finds a clip of this bank by: " + string.Join(" + ", target.KeyParameters.Select(SoundBankParameterTable.NameOf).ToArray()));
            text.AppendLine("# A line whose values are a clip's key replaces that clip's audio; a key the bank does not have adds a new");
            text.AppendLine("# clip filed under it. Two lines with one key are its first and second take (take=2 names one; a take the");
            text.AppendLine("# bank does not have yet is added). clip=3 replaces clip 3 whatever its key; clip=new with values adds one.");
            text.AppendLine("# A parameter every clip shares one value of may be left out: the line takes that value.");
            text.AppendLine("#");
            foreach (uint id in target.KeyParameters)
            {
                HashSet<int> values = new HashSet<int>();
                foreach (SoundBankClipKey key in target.ClipKeys)
                {
                    int value;
                    if (key != null && key.Values.TryGetValue(id, out value))
                        values.Add(value);
                }

                WriteParameter(text, SoundBankParameterTable.NameOf(id), id, values);
            }
        }

        internal static void WriteParameter(StringBuilder text, string name, uint id, ICollection<int> values)
        {
            string about = SoundParameterFacts.Describe(name);
            if (string.IsNullOrEmpty(about))
                about = name;
            foreach (string line in about.Replace("\r\n", "\n").Split('\n'))
                text.AppendLine("# " + line);

            SoundBankParameterTable.Entry entry = SoundBankParameterTable.Find(id);
            if (entry != null && entry.ValueNames.Length > 0)
            {
                List<string> named = new List<string>();
                for (int v = 0; v < entry.Values.Length; v++)
                    named.Add(entry.ValueNames[v] + "=" + entry.Values[v].ToString(CultureInfo.InvariantCulture));
                foreach (string chunk in Wrap("#   value names: ", named, 110))
                    text.AppendLine(chunk);
            }

            if (values != null && values.Count > 0)
            {
                // A long list (a surname bank's thousands of ids) is cut: every clip's own line states its key anyway.
                List<string> listed = values.OrderBy(v => v).Take(200).Select(v => v.ToString(CultureInfo.InvariantCulture)).ToList();
                if (values.Count > 200)
                    listed.Add("... and " + (values.Count - 200).ToString(CultureInfo.InvariantCulture) + " more");
                foreach (string chunk in Wrap("#   values in this asset: ", listed, 110))
                    text.AppendLine(chunk);
            }

            text.AppendLine("#");
        }

        private static void WriteSongHelp(StringBuilder text)
        {
            text.AppendLine("# EA Trax: a song is filed under its songid, the game DB's eatrax.songid.");
            text.AppendLine("#     songid=<a songid this bank has>    replaces that song's audio");
            text.AppendLine("#     songid=<a new songid>              adds a song");
            text.AppendLine("#     title, artist, album               the DB's eatrax.songname, artist and album");
            text.AppendLine("#     intro, playlistid, unlocked        the DB columns eatrax.intro, songplaylistlinks.playlistid and");
            text.AppendLine("#                                        .unlocked (every shipped song: intro=1, playlistid=0, unlocked=1)");
            text.AppendLine("#     clip=3                             replaces track 3 whatever its songid");
            text.AppendLine("# FIFA17.exe lists EA Trax songs from the DB (FUN_14789d6a0: songplaylistlinks joined with eatrax), so a");
            text.AppendLine("# new song plays in the game once the DB has its rows: the import writes them next to this file as");
            text.AppendLine("# " + EaTraxDbRows.EatraxFile + " and " + EaTraxDbRows.LinksFile + ", in the format the DB's text export uses.");
            text.AppendLine("#");
        }

        internal static IEnumerable<string> Wrap(string prefix, IEnumerable<string> items, int width)
        {
            StringBuilder line = new StringBuilder(prefix);
            bool any = false;
            foreach (string item in items)
            {
                if (any && line.Length + item.Length + 2 > width)
                {
                    yield return line.ToString().TrimEnd(',', ' ') + ",";
                    line = new StringBuilder("#     ");
                    any = false;
                }

                line.Append(item).Append(", ");
                any = true;
            }

            if (any)
                yield return line.ToString().TrimEnd(',', ' ');
        }
    }

    /// <summary>
    /// The rows a new EA Trax song needs in the game DB, written in the format of the DB's text export (UTF-16 with a
    /// byte order mark, tab separated, a header line, CRLF): `eatrax` (album, songname, artist, songid, intro) and
    /// `songplaylistlinks` (songid, playlistid, unlocked, artificialkey - artificialkey = songid, as in all 51 shipped
    /// rows). A file that is already there keeps its rows; a row for the same songid is replaced.
    /// </summary>
    public static class EaTraxDbRows
    {
        public const string EatraxFile = "eatrax_rows.txt";
        public const string LinksFile = "songplaylistlinks_rows.txt";
        public const string EatraxHeader = "album\tsongname\tartist\tsongid\tintro";
        public const string LinksHeader = "songid\tplaylistid\tunlocked\tartificialkey";

        public static string[] Write(string folder, IEnumerable<EaTraxSongInfo> songs)
        {
            List<EaTraxSongInfo> list = songs.Where(s => s != null).ToList();
            string eatrax = Path.Combine(folder, EatraxFile), links = Path.Combine(folder, LinksFile);
            File.WriteAllText(eatrax, Merge(ReadIfThere(eatrax), EatraxHeader, list.Select(EatraxRow), 3), Encoding.Unicode);
            File.WriteAllText(links, Merge(ReadIfThere(links), LinksHeader, list.Select(LinksRow), 0), Encoding.Unicode);
            return new[] { eatrax, links };
        }

        public static string[] EatraxRow(EaTraxSongInfo s)
        {
            return new[] { Clean(s.Album), Clean(s.Title), Clean(s.Artist), s.SongId.ToString(CultureInfo.InvariantCulture),
                s.Intro.ToString(CultureInfo.InvariantCulture) };
        }

        public static string[] LinksRow(EaTraxSongInfo s)
        {
            string id = s.SongId.ToString(CultureInfo.InvariantCulture);
            return new[] { id, s.PlaylistId.ToString(CultureInfo.InvariantCulture), s.Unlocked.ToString(CultureInfo.InvariantCulture), id };
        }

        /// <summary>The text with the header, the existing rows (one replaced per songid given) and the new rows, CRLF.</summary>
        public static string Merge(string existing, string header, IEnumerable<string[]> rows, int songIdColumn)
        {
            List<string[]> kept = new List<string[]>();
            if (!string.IsNullOrEmpty(existing))
            {
                string[] lines = existing.Replace("\r\n", "\n").Split('\n');
                for (int i = 1; i < lines.Length; i++)
                {
                    if (lines[i].Length > 0)
                        kept.Add(lines[i].Split('\t'));
                }
            }

            foreach (string[] row in rows)
            {
                kept.RemoveAll(k => k.Length > songIdColumn && k[songIdColumn] == row[songIdColumn]);
                kept.Add(row);
            }

            StringBuilder text = new StringBuilder(header).Append("\r\n");
            foreach (string[] row in kept)
                text.Append(string.Join("\t", row)).Append("\r\n");
            return text.ToString();
        }

        private static string ReadIfThere(string path)
        {
            if (!File.Exists(path))
                return null;
            using (StreamReader reader = new StreamReader(path, Encoding.Unicode, true))
                return reader.ReadToEnd();
        }

        private static string Clean(string text)
        {
            return (text ?? "").Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ').Trim();
        }
    }
}
