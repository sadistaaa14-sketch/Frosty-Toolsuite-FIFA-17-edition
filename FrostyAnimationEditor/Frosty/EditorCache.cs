using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Frosty.Core;
using FrostyAnimationEditor.Core;

namespace FrostyAnimationEditor.Plugin
{
    /// <summary>
    /// The editor's cache: per-block runtime data of a container (key references, loader allocations) and its loader
    /// profile, so a session does not decode all ~460,000 objects again. One file per container content, named
    /// after the asset and the content's hash, in [Frosty]\Caches\AnimationEditor.
    /// </summary>
    internal static class EditorCache
    {
        private const int KeepPerAsset = 3;
        private static string folder;

        public static string Folder
        {
            get
            {
                if (folder != null) return folder;
                foreach (string f in new[]
                {
                    Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Caches", "AnimationEditor"),
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Frosty", "AnimationEditor")
                })
                {
                    try
                    {
                        Directory.CreateDirectory(f);
                        string probe = Path.Combine(f, ".write_test");
                        File.WriteAllText(probe, "");
                        File.Delete(probe);
                        folder = f;
                        return f;
                    }
                    catch (Exception) { }
                }
                folder = Path.GetTempPath();
                return folder;
            }
        }

        private static string Safe(string s)
        {
            StringBuilder b = new StringBuilder();
            foreach (char c in s ?? "") b.Append(char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '_');
            string r = b.ToString().Trim('_');
            return r.Length > 80 ? r.Substring(r.Length - 80) : (r.Length == 0 ? "antstate" : r);
        }

        public static string Prefix(string source)
        {
            string profile = "";
            try { profile = FrostySdk.ProfilesLibrary.ProfileName ?? ""; } catch (Exception) { }
            return Safe(profile) + "_" + Safe(source) + "_";
        }

        public static string PathFor(string source, ulong hash)
        {
            return Path.Combine(Folder, Prefix(source) + hash.ToString("x16") + ".faecache");
        }

        /// <summary>the store saved for this content, or null</summary>
        public static RuntimeStore TryRead(string source, int blocks, long length, ulong hash)
        {
            string p = PathFor(source, hash);
            if (!File.Exists(p)) return null;
            try
            {
                using (FileStream f = File.OpenRead(p))
                {
                    RuntimeStore s = RuntimeStore.Read(f, blocks, length, hash);
                    if (s != null) File.SetLastWriteTimeUtc(p, DateTime.UtcNow);
                    return s;
                }
            }
            catch (Exception e)
            {
                Log("the animation editor cache " + p + " could not be read (" + e.Message + "); it is built again");
                return null;
            }
        }

        public static void Write(string source, RuntimeStore s, long length, ulong hash)
        {
            string p = PathFor(source, hash);
            string tmp = p + ".tmp";
            try
            {
                using (FileStream f = File.Create(tmp)) s.Write(f, length, hash);
                if (File.Exists(p)) File.Delete(p);
                File.Move(tmp, p);
            }
            catch (Exception e)
            {
                Log("the animation editor cache could not be written to " + p + ": " + e.Message);
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch (Exception) { }
                return;
            }
            // keep the newest few per asset (each content of the asset gets its own file)
            try
            {
                List<FileInfo> old = new DirectoryInfo(Folder).GetFiles(Prefix(source) + "*.faecache")
                    .OrderByDescending(x => x.LastWriteTimeUtc).Skip(KeepPerAsset).ToList();
                foreach (FileInfo x in old) x.Delete();
            }
            catch (Exception) { }
        }

        public static void Log(string text)
        {
            try { if (App.Logger != null) App.Logger.Log(text); } catch (Exception) { }
        }
    }
}
