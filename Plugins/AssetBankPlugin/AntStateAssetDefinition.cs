using AssetBankPlugin.Ant;
using AssetBankPlugin.Export;
using AssetBankPlugin.GenericData;
using Frosty.Controls;
using Frosty.Core;
using FrostySdk;
using FrostySdk.IO;
using FrostySdk.Managers;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace AssetBankPlugin
{
    public class AntStateAssetDefinition : AssetDefinition
    {
        public static Bank DefaultAntState;
        private static string _logPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "frosty_anim_export.log");
        private static void Log(string msg)
        { try { File.AppendAllText(_logPath, DateTime.Now.ToString("HH:mm:ss.fff") + " | " + msg + Environment.NewLine); } catch { } }

        public override void GetSupportedExportTypes(List<AssetExportType> exportTypes)
        {
            exportTypes.Add(new AssetExportType("bvh", "BVH Motion Capture"));
            base.GetSupportedExportTypes(exportTypes);
        }

        public override bool Export(EbxAssetEntry entry, string path, string filterType)
        {
            try { File.WriteAllText(_logPath, "=== ANIMATION EXPORT LOG ===" + Environment.NewLine); } catch { }
            Log("START: " + entry.Name);

            var opt = new AnimationOptions(); opt.Load();
            EbxAsset asset = App.AssetManager.GetEbx(entry);
            dynamic antStateAsset = asset.RootObject;
            Stream s; int bundleId = 0;

            if (antStateAsset.StreamingGuid == Guid.Empty)
            { var res = App.AssetManager.GetResEntry(entry.Name); bundleId = res.Bundles[0]; s = App.AssetManager.GetRes(res); }
            else
            { var chunk = App.AssetManager.GetChunkEntry(antStateAsset.StreamingGuid); bundleId = chunk.Bundles[0]; s = App.AssetManager.GetChunk(chunk); }

            string cachePath = "Caches/" + ProfilesLibrary.ProfileName + "_antstate.cache";
            string internalCachePath = "Caches/" + ProfilesLibrary.ProfileName + "_antref.cache";
            if (opt.UseCache && File.Exists(cachePath) && File.Exists(internalCachePath))
            { Cache.ReadState(cachePath); Cache.ReadMap(internalCachePath); }
            else
            {
                Log("Loading bundles...");
                int bc = 0, errs = 0;
                foreach (var bundle in App.AssetManager.EnumerateBundles())
                { try { LoadAntStateFromBundle(bundle); } catch { errs++; } bc++; if (bc%500==0) Log("  "+bc); }
                Log("Done: " + bc + " bundles");
                if (opt.UseCache) { Cache.WriteState(cachePath); Cache.WriteMap(internalCachePath); }
            }

            Log("Parsing Bank...");
            using (var r = new NativeReader(s))
            {
                Bank bank;
                try { bank = new Bank(r, bundleId); }
                catch (Exception ex) { Log("FATAL: " + ex.Message); FrostyMessageBox.Show("Bank failed", "Error"); return false; }
                Log("Bank: " + bank.AssetsByName.Count + " assets");

                var skelEbx = App.AssetManager.GetEbx(opt.ExportSkeletonAsset);
                var skeleton = SkeletonAsset.ConvertToInternal(skelEbx.RootObject);
                Log("Skeleton: " + skeleton.BoneNames.Count + " bones");

                string outDir = Path.GetDirectoryName(path);

                // Show the browser window
                var browser = new AnimationBrowserWindow(bank, skeleton, outDir);
                browser.ShowDialog();

                Log("Browser closed.");
            }
            return true;
        }

        public static void LoadAntStateFromBundle(BundleEntry bundle)
        {
            var resources = App.AssetManager.EnumerateRes(bundle).Where(x => x.Type == "AssetBank");
            foreach (var res in resources)
            {
                var antBank = App.AssetManager.GetRes(res);
                var antReader = new NativeReader(antBank);
                try { _ = new Bank(antReader, App.AssetManager.GetBundleId(bundle)); } catch { }
                antBank.Dispose(); antReader.Dispose();
            }
        }
    }
}
