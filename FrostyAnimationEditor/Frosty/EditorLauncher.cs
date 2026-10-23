using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Windows;
using Frosty.Controls;
using Frosty.Core;
using Frosty.Core.Windows;
using FrostyAnimationEditor.Core;
using FrostyAnimationEditor.Windows;
using FrostySdk.Managers;

namespace FrostyAnimationEditor.Plugin
{
    /// <summary>Tools &gt; Animation Editor</summary>
    public class AnimationEditorMenuExtension : MenuExtension
    {
        public override string TopLevelMenuName { get { return "Tools"; } }
        public override string SubLevelMenuName { get { return null; } }
        public override string MenuItemName { get { return "Animation Editor"; } }
        public override System.Windows.Media.ImageSource Icon { get { return null; } }

        public override RelayCommand MenuItemClicked
        {
            get { return new RelayCommand(o => EditorLauncher.Open()); }
        }
    }

    /// <summary>a loaded container: the editing model and where it came from</summary>
    internal sealed class LoadedSource
    {
        public EditorModel Model;
        public FrostyAntState Frosty;      // null for a .res file opened from disk
        public string FilePath;            // the .res file, or null
        public string CacheNote;
    }

    /// <summary>long jobs run in Frosty's task window (a background thread); errors come back on the caller's thread</summary>
    internal static class Work
    {
        public static T Run<T>(Window owner, string title, string status, Func<Action<string, double>, T> job)
        {
            T result = default(T);
            Exception error = null;
            Window o = owner != null && owner.IsVisible ? owner : Application.Current.MainWindow;
            FrostyTaskWindow.Show(o, title, status, task =>
            {
                try
                {
                    result = job((text, fraction) =>
                    {
                        double p = double.IsNaN(fraction) ? 0 : Math.Max(0, Math.Min(1, fraction)) * 100.0;
                        task.Update(text, p);
                    });
                }
                catch (Exception e) { error = e; }
            });
            if (error != null) ExceptionDispatchInfo.Capture(error).Throw();
            return result;
        }
    }

    internal static class EditorLauncher
    {
        private static EditorWindow window;

        public static void Open()
        {
            if (window != null)
            {
                if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
                window.Activate();
                return;
            }
            if (App.AssetManager == null)
            {
                FrostyMessageBox.Show("Load the game (open or create a project) first.", "Animation Editor");
                return;
            }
            List<EbxAssetEntry> found;
            try { found = FrostyAntState.Find(); }
            catch (Exception e) { FrostyMessageBox.Show("Could not list the AntStateAsset assets: " + e.Message, "Animation Editor"); return; }
            if (found.Count == 0)
            {
                FrostyMessageBox.Show("This game has no AntStateAsset. The Animation Editor edits FIFA 17's fifagame_win32_antstate.", "Animation Editor");
                return;
            }
            EbxAssetEntry pick = found.Count == 1 ? found[0] : Dialog.Pick(Application.Current.MainWindow, "Animation Editor",
                "This game has several AntStateAsset assets. Which one?", found, e => e.Name + (e.IsModified ? "  (modified)" : ""));
            if (pick == null) return;
            LoadedSource src;
            try { src = Load(Application.Current.MainWindow, pick); }
            catch (Exception e)
            {
                EditorCache.Log("Animation Editor: loading " + pick.Name + " failed: " + e);
                FrostyMessageBox.Show("Could not load " + pick.Name + ":\n" + e.Message, "Animation Editor");
                return;
            }
            Show(src);
        }

        public static void Show(LoadedSource src)
        {
            window = new EditorWindow(src);
            window.Closed += (s, e) =>
            {
                window = null;
                GC.Collect();
            };
            window.Show();
        }

        /// <summary>reads the asset's container from Frosty and prepares the model (with the cache; built on the first run)</summary>
        public static LoadedSource Load(Window owner, EbxAssetEntry entry)
        {
            return Work.Run(owner, "Animation Editor", "reading " + entry.Name, progress =>
            {
                progress("finding the data of " + entry.Name, 0.01);
                FrostyAntState fs = FrostyAntState.Locate(entry);
                progress("reading " + fs.Where, 0.03);
                byte[] image = fs.ReadImage();
                LoadedSource src = Open(image, entry.Name, progress);
                src.Frosty = fs;
                src.Model.SourceLabel = entry.Name + (fs.IsModified ? " (modified in this project)" : "");
                return src;
            });
        }

        /// <summary>a .res file from disk (16-byte ResMeta + container)</summary>
        public static LoadedSource LoadFile(Window owner, string path)
        {
            return Work.Run(owner, "Animation Editor", "reading " + path, progress =>
            {
                progress("reading " + Path.GetFileName(path), 0.02);
                byte[] image = File.ReadAllBytes(path);
                LoadedSource src = Open(image, "file_" + Path.GetFileNameWithoutExtension(path), progress);
                src.FilePath = path;
                src.Model.SourceLabel = path;
                return src;
            });
        }

        private static LoadedSource Open(byte[] image, string cacheName, Action<string, double> progress)
        {
            progress("scanning the container (" + (image.Length >> 20) + " MB)", 0.06);
            Container c = new Container(image);
            AntDb db = new AntDb(c);
            progress("looking for the editor cache", 0.08);
            long len = image.Length - 16;
            ulong hash = RuntimeStore.Hash(image, 16, len);
            RuntimeStore store = EditorCache.TryRead(cacheName, db.N0, len, hash);
            string note;
            if (store != null)
            {
                db.Store = store;
                note = "cache " + Path.GetFileName(EditorCache.PathFor(cacheName, hash));
            }
            else
            {
                Stopwatch sw = Stopwatch.StartNew();
                progress("building the editor cache (first run for this data): decoding " + db.N0 + " objects", 0.1);
                RuntimeStore.Build(db, (text, f) => progress("building the editor cache (first run for this data): " + text, 0.1 + 0.85 * f));
                progress("writing the editor cache", 0.96);
                EditorCache.Write(cacheName, db.Store, len, hash);
                note = "cache built in " + (sw.ElapsedMilliseconds / 1000.0).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " s";
            }
            EditorCache.Log("Animation Editor: " + cacheName + ", " + db.N0 + " objects, " + note);
            return new LoadedSource { Model = new EditorModel(new Session(db), cacheName), CacheNote = note };
        }

        /// <summary>writes the cache for a container this session produced (so the next session starts without the first-run build)</summary>
        public static void CacheSaved(AntDb db, SaveResult r, string cacheName, Action<string, double> progress)
        {
            if (r.Ids == null) return;
            progress("updating the editor cache", 0.97);
            RuntimeStore s = RuntimeStore.Derive(db, r.Ids, r.Plan != null && r.Plan.Measured ? r.Plan.NewProfile : null);
            long off = r.WithoutPrefix ? 0 : 16;
            long len = r.Image.Length - off;
            EditorCache.Write(cacheName, s, len, RuntimeStore.Hash(r.Image, off, len));
        }
    }
}
