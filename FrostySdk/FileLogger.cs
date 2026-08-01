using System;
using System.IO;
using System.Text;

namespace FrostySdk
{
    /// <summary>
    /// Simple file logger that writes to the user's Desktop.
    /// Used for debugging the FrostyModExecutor pipeline.
    /// Log file location: %USERPROFILE%\Desktop\FrostyModExecutor.log
    /// </summary>
    public static class FileLogger
    {
        private static readonly object _lock = new object();
        private static string _logPath;

        public static string LogPath
        {
            get
            {
                if (_logPath == null)
                {
                    try
                    {
                        string desktop = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
                        _logPath = Path.Combine(desktop, "FrostyModExecutor.log");
                    }
                    catch
                    {
                        _logPath = "FrostyModExecutor.log";
                    }
                }
                return _logPath;
            }
        }

        public static void Log(string message)
        {
            try
            {
                lock (_lock)
                {
                    File.AppendAllText(LogPath,
                        $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}{Environment.NewLine}");
                }
            }
            catch
            {
                // Swallow — logging must never crash the app
            }
        }

        public static void Log(string format, params object[] args)
        {
            try
            {
                Log(string.Format(format, args));
            }
            catch
            {
            }
        }

        /// <summary>
        /// Logs an exception with full detail, including inner exceptions
        /// and AggregateException unwrapping.
        /// </summary>
        public static void LogException(string context, Exception ex)
        {
            try
            {
                Log("════════════════════════════════════════════════════════════");
                Log("EXCEPTION in: {0}", context);
                Log("  Type: {0}", ex?.GetType()?.FullName ?? "null");
                Log("  Message: {0}", ex?.Message ?? "null");
                Log("  StackTrace:");
                Log(ex?.StackTrace ?? "<null>");

                if (ex is AggregateException ae)
                {
                    Log("  InnerExceptions ({0}):", ae.InnerExceptions.Count);
                    for (int i = 0; i < ae.InnerExceptions.Count; i++)
                    {
                        Exception inner = ae.InnerExceptions[i];
                        Log("    [{0}] Type: {1}", i, inner?.GetType()?.FullName ?? "null");
                        Log("    [{0}] Message: {1}", i, inner?.Message ?? "null");
                        Log("    [{0}] StackTrace:", i);
                        Log("    " + (inner?.StackTrace ?? "<null>"));
                    }
                }
                else if (ex?.InnerException != null)
                {
                    Log("  InnerException:");
                    Log("    Type: {0}", ex.InnerException.GetType()?.FullName);
                    Log("    Message: {0}", ex.InnerException.Message);
                    Log("    StackTrace:");
                    Log("    " + ex.InnerException.StackTrace);
                }

                Log("════════════════════════════════════════════════════════════");
            }
            catch
            {
            }
        }

        /// <summary>
        /// Clears the log file. Call at the start of a launch attempt
        /// so each launch produces a clean log.
        /// </summary>
        public static void Clear()
        {
            try
            {
                lock (_lock)
                {
                    if (File.Exists(LogPath))
                        File.Delete(LogPath);
                }
            }
            catch
            {
            }
        }
    }
}
