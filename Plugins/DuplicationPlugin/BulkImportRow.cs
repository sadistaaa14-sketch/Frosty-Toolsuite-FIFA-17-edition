using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace DuplicationPlugin
{
    /// <summary>
    /// A single row of a bulk-duplication job: an existing source asset/folder,
    /// the new name to duplicate it to, and (for the folder-based duplicators)
    /// the destination folder to place the new copy in.
    /// </summary>
    public class BulkImportRow
    {
        public string SourcePath { get; set; }
        public string NewName { get; set; }
        public string DestPath { get; set; }

        /// <summary>Row number in the original import text/file (1-based), used for error reporting.</summary>
        public int LineNumber { get; set; }

        public override string ToString()
        {
            return string.IsNullOrEmpty(DestPath)
                ? $"{SourcePath} -> {NewName}"
                : $"{SourcePath} -> {DestPath}/{NewName}";
        }
    }

    /// <summary>
    /// Parses bulk-import text (pasted or loaded from a .csv/.txt file) into <see cref="BulkImportRow"/> entries.
    /// Accepted format, one entry per line:
    ///     SourcePath,NewName[,DestPath]
    /// - Blank lines and lines starting with '#' are ignored.
    /// - Fields may optionally be wrapped in double quotes if they contain commas.
    /// - If DestPath is omitted, it defaults to the parent folder of SourcePath (i.e. duplicate in place).
    /// </summary>
    public static class BulkImportParser
    {
        public static List<BulkImportRow> Parse(string text, out List<string> errors)
        {
            errors = new List<string>();
            List<BulkImportRow> rows = new List<BulkImportRow>();

            if (string.IsNullOrWhiteSpace(text))
                return rows;

            string[] lines = text.Replace("\r\n", "\n").Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                string rawLine = lines[i];
                string line = rawLine.Trim();
                int lineNumber = i + 1;

                if (line.Length == 0 || line.StartsWith("#"))
                    continue;

                string[] fields = SplitCsvLine(line);

                if (fields.Length < 2)
                {
                    errors.Add($"Line {lineNumber}: expected at least 'SourcePath,NewName' — got '{rawLine}'");
                    continue;
                }

                string source = fields[0].Trim().Replace('\\', '/');
                string newName = fields[1].Trim().Replace('\\', '/');
                string dest = fields.Length >= 3 ? fields[2].Trim().Replace('\\', '/') : null;

                if (string.IsNullOrEmpty(source))
                {
                    errors.Add($"Line {lineNumber}: SourcePath is empty.");
                    continue;
                }
                if (string.IsNullOrEmpty(newName))
                {
                    errors.Add($"Line {lineNumber}: NewName is empty.");
                    continue;
                }

                if (string.IsNullOrEmpty(dest))
                {
                    int idx = source.LastIndexOf('/');
                    dest = idx >= 0 ? source.Substring(0, idx) : source;
                }

                rows.Add(new BulkImportRow
                {
                    SourcePath = source.TrimEnd('/'),
                    NewName = newName.TrimEnd('/'),
                    DestPath = dest.TrimEnd('/'),
                    LineNumber = lineNumber
                });
            }

            return rows;
        }

        public static List<BulkImportRow> ParseFile(string path, out List<string> errors)
        {
            return Parse(File.ReadAllText(path), out errors);
        }

        /// <summary>Minimal CSV split supporting quoted fields containing commas.</summary>
        private static string[] SplitCsvLine(string line)
        {
            List<string> fields = new List<string>();
            bool inQuotes = false;
            System.Text.StringBuilder current = new System.Text.StringBuilder();

            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];
                if (c == '"')
                {
                    inQuotes = !inQuotes;
                }
                else if (c == ',' && !inQuotes)
                {
                    fields.Add(current.ToString());
                    current.Clear();
                }
                else
                {
                    current.Append(c);
                }
            }
            fields.Add(current.ToString());
            return fields.Select(f => f.Trim().Trim('"')).ToArray();
        }
    }
}
