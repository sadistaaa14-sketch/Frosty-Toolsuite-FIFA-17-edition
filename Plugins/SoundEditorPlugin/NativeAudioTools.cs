using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;

namespace SoundEditorPlugin
{
    /// <summary>
    /// Finds the plugin's native helpers - the EA Layer 3 decoder and the vgmstream command line tool
    /// - where they are deployed rather than where one machine happens to start the editor.
    ///
    /// The decoder used to be declared as <c>"../thirdparty/ealayer3.dll"</c>, a path the loader
    /// resolves against the process's working directory. That resolved only when the editor was
    /// started with its own output folder as the working directory; launched any other way, the
    /// decoders were simply not found and the sound editor listed no audio with nothing to explain
    /// it. These paths are anchored to the folder the plugin is loaded from and to the editor's own
    /// folder instead, and the working directory relative form is kept last so an existing deployment
    /// that relies on it keeps working.
    /// </summary>
    public static class NativeAudioTools
    {
        /// <summary>The folder the plugin assembly was loaded from.</summary>
        public static string PluginDirectory
        {
            get { return Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location); }
        }

        /// <summary>
        /// Every place a native helper is looked for, in order, as absolute paths. The plugin's own
        /// folder comes first, then the editor's, then the working directory relative form the old
        /// code used. Passing <paramref name="pluginDirectory"/> is for tests that check a deployment
        /// layout other than the running one.
        /// </summary>
        public static IList<string> SearchPaths(string fileName, string pluginDirectory = null)
        {
            string pluginDir = pluginDirectory ?? PluginDirectory;
            string appDir = AppDomain.CurrentDomain.BaseDirectory;
            string cwd = Directory.GetCurrentDirectory();

            List<string> found = new List<string>();

            Add(found, pluginDir);
            Add(found, Path.Combine(pluginDir, "thirdparty"));
            Add(found, Path.Combine(pluginDir, "..", "thirdparty"));
            Add(found, Path.Combine(pluginDir, "..", "..", "thirdparty"));
            Add(found, appDir);
            Add(found, Path.Combine(appDir, "thirdparty"));
            Add(found, Path.Combine(appDir, "..", "thirdparty"));
            Add(found, cwd);
            Add(found, Path.Combine(cwd, "..", "thirdparty"));

            return found.ConvertAll(dir => Path.Combine(dir, fileName));
        }

        /// <summary>The first existing location for a helper, or null when it is not deployed at all.</summary>
        public static string Find(string fileName, string pluginDirectory = null)
        {
            foreach (string candidate in SearchPaths(fileName, pluginDirectory))
            {
                try
                {
                    if (File.Exists(candidate))
                        return candidate;
                }
                catch (Exception)
                {
                    // An unreadable candidate is not a location that exists.
                }
            }

            return null;
        }

        /// <summary>
        /// The editor's log, when there is one. A diagnostic must never be the reason a load fails, and
        /// these paths are exercised outside the editor too, where <c>Frosty.Core.App.Logger</c> has not
        /// been set: reporting a missing decoder then threw a NullReferenceException out of the lookup.
        /// </summary>
        public static void LogWarning(string text, params object[] vars)
        {
            try
            {
                Frosty.Core.App.Logger?.LogWarning(text, vars);
            }
            catch (Exception)
            {
            }
        }

        /// <summary>What to tell the user when a helper is missing, naming every place that was checked.</summary>
        public static string NotFoundMessage(string fileName)
        {
            return string.Format("{0} was not found; it is looked for at: {1}",
                fileName, string.Join(", ", SearchPaths(fileName)));
        }

        /// <summary>
        /// Loads a native library from an explicit path. A later lookup of the same library by name -
        /// which is what the compiler's DllImport emits - then binds to this same loaded module instead
        /// of searching again.
        /// </summary>
        public static bool Preload(string path)
        {
            return LoadLibrary(path) != IntPtr.Zero;
        }

        [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr LoadLibrary(string fileName);

        private static void Add(List<string> paths, string directory)
        {
            if (string.IsNullOrEmpty(directory))
                return;

            string full;
            try
            {
                full = Path.GetFullPath(directory);
            }
            catch (Exception)
            {
                return;
            }

            if (!paths.Contains(full))
                paths.Add(full);
        }
    }
}
