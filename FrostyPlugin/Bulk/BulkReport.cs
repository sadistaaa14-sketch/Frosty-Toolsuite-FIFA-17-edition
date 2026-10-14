using FrostySdk.Managers;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Frosty.Core.Bulk
{
    /// <summary>
    /// The file a bulk run leaves next to its output.
    ///
    /// A run's notes only reach the log window: that is gone once the editor closes, and while the run
    /// is going on it is the wrong place to look. A chunk that an asset names but this profile does not
    /// have produces no file and no other trace at all, so a run that wrote no chunk has to be able to
    /// say what it looked at - and the person diagnosing it may well not be the person who ran it. A
    /// report outlives the run and can be read, sent on, or compared with the next one.
    /// </summary>
    public static class BulkReport
    {
        /// <summary>The file written into a run's output folder.</summary>
        public const string FileName = "bulk-export-report.txt";

        /// <summary>Where the report of a run that wrote to <paramref name="outputRoot"/> goes.</summary>
        public static string PathFor(string outputRoot) => Path.Combine(outputRoot, FileName);

        /// <summary>
        /// The report's text: what was asked for, what came of it, every note the run made, and the
        /// per item detail it collected (the chunk lookup, for an export that asked for chunks).
        /// </summary>
        public static string Text(string operation, string folder, string outputRoot, string description,
            BulkResult result, IList<string> details)
        {
            int noteCount = result == null ? 0 : result.Messages.Count;
            int detailCount = details == null ? 0 : details.Count;

            StringBuilder text = new StringBuilder();
            text.AppendLine(operation + " report");
            text.AppendLine("written: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            text.AppendLine("folder: " + (string.IsNullOrEmpty(folder) ? "[root]" : folder));
            text.AppendLine("output: " + outputRoot);
            text.AppendLine("asked for: " + description);
            text.AppendLine("result: " + (result == null ? "nothing" : result.Describe()));
            text.AppendLine();

            text.AppendLine("notes (" + noteCount + "):");
            if (result != null)
            {
                foreach (string message in result.Messages)
                    text.AppendLine("  " + message);
            }

            text.AppendLine();
            text.AppendLine("chunk lookup (" + detailCount + "):");
            if (details != null)
            {
                foreach (string line in details)
                    text.AppendLine("  " + line);
            }

            return text.ToString();
        }

        /// <summary>
        /// Writes the report. A report that cannot be written is logged and then ignored: it is there to
        /// explain a run, never to fail one.
        /// </summary>
        public static bool Write(string path, string text)
        {
            try
            {
                File.WriteAllText(path, text);
                return true;
            }
            catch (Exception ex)
            {
                App.Logger?.Log("could not write the run report to {0}: {1}", path, ex.Message);
                return false;
            }
        }
    }
}
