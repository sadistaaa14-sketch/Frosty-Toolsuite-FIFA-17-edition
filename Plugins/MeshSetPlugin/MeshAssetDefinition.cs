using FrostySdk.Attributes;
using FrostySdk.IO;
using FrostySdk.Managers;
using System;
using System.Collections.Generic;
using System.Windows;
using Frosty.Core;
using Frosty.Core.Bulk;
using System.Windows.Media;
using Frosty.Core.Windows;
using Frosty.Core.Controls;
using FrostySdk.Interfaces;
using MeshSetPlugin.Resources;
using MeshSetPlugin.Editors;

namespace MeshSetPlugin
{
    public enum MeshExportVersion
    {
        FBX_2012,
        FBX_2013,
        FBX_2014,
        FBX_2015,
        FBX_2016,
        FBX_2017
    }

    public enum MeshExportScale
    {
        Millimeters,
        Centimeters,
        Meters,
        Kilometers
    }

    public class MeshExportSettings
    {
        public MeshExportVersion Version { get; set; }

        public MeshExportScale Scale { get; set; }

        [DisplayName("Flatten Hierarchy (Blender)")]
        public bool FlattenHierarchy { get; set; }
        public bool ExportSingleLod { get; set; }
        [DisplayName("Export Additional Meshes")]
        public bool ExportAdditionalMeshes { get; set; }
    }

    public class SkinnedMeshExportSettings : MeshExportSettings
    {
        [DisplayName("Skeleton")]
        [Editor(typeof(FrostySkeletonEditor))]
        public string SkeletonAsset { get => skeletonAsset; set => skeletonAsset = value; }
        private string skeletonAsset = "";
    }

    public class MeshAssetDefinition : AssetDefinition
    {
        protected static ImageSource rigidMeshSource = new ImageSourceConverter().ConvertFromString("pack://application:,,,/MeshSetPlugin;component/Images/RigidMeshFileType.png") as ImageSource;
        protected static ImageSource skinnedMeshSource = new ImageSourceConverter().ConvertFromString("pack://application:,,,/MeshSetPlugin;component/Images/SkinnedMeshFileType.png") as ImageSource;
        protected static ImageSource compositeMeshSource = new ImageSourceConverter().ConvertFromString("pack://application:,,,/MeshSetPlugin;component/Images/CompositeMeshFileType.png") as ImageSource;

        public MeshAssetDefinition()
        {
        }

        public override void GetSupportedExportTypes(List<AssetExportType> exportTypes)
        {
            exportTypes.Add(new AssetExportType("fbx", "Autodesk FBX"));
            //exportTypes.Add(new AssetExportType("fbx", "Autodesk FBX (ASCII)"));
            exportTypes.Add(new AssetExportType("obj", "Wavefront OBJ"));

            base.GetSupportedExportTypes(exportTypes);
        }

        public override FrostyAssetEditor GetEditor(ILogger logger)
        {
            return new FrostyMeshSetEditor(logger);
        }

        public override bool Export(EbxAssetEntry entry, string path, string filterType)
        {
            // The base class writes the xml and bin forms, and its answer is the real answer - it
            // used to be dropped here, which made a perfectly good xml export look like a failure.
            if (base.Export(entry, path, filterType))
                return true;

            if (IsMeshFormat(filterType))
            {
                if (PreExport(entry, out MeshExportSettings settings))
                {
                    // export
                    ExportInternal(entry, path, settings, filterType);

                    // save out settings to config
                    PostExport(settings);
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Exports a mesh without asking the user anything, so a bulk export over a whole folder does
        /// not raise one settings dialog per mesh. The values come from exactly the same configuration
        /// the settings dialog is filled from, and the skeleton is taken from the bulk run (which
        /// works it out from the asset's folder) when one is available.
        /// </summary>
        public override bool ExportSilent(EbxAssetEntry entry, string path, string filterType, FrostyTaskWindow task)
        {
            if (!IsMeshFormat(filterType))
                return base.ExportSilent(entry, path, filterType, task);

            MeshExportSettings settings = CreateSettings(entry);
            if (settings is SkinnedMeshExportSettings skinned)
            {
                string skeleton = GetSkeletonForBulk();
                if (!string.IsNullOrEmpty(skeleton))
                    skinned.SkeletonAsset = skeleton;
            }

            // No config is written back here on purpose: a bulk run must not overwrite the user's
            // per-asset export defaults.
            ExportWithSettings(entry, path, settings, filterType, task);
            return true;
        }

        /// <summary>
        /// Imports a mesh from an fbx/obj file. A skinned mesh needs a skeleton, which the bulk run
        /// resolves from the asset's folder; the last skeleton used for an import is the fallback.
        /// </summary>
        public override bool ImportSilent(EbxAssetEntry entry, string path, string filterType, FrostyTaskWindow task)
        {
            // Only .fbx comes back in: the FBX SDK reads its own format, not Wavefront OBJ, which is
            // why the mesh editor's own Import command offers .fbx alone. An .obj is refused honestly
            // (it falls through to the base class, which writes nothing) rather than half imported.
            if (filterType != "fbx")
                return base.ImportSilent(entry, path, filterType, task);

            EbxAsset asset = App.AssetManager.GetEbx(entry);
            dynamic meshAsset = (dynamic)asset.RootObject;
            ulong resRid = meshAsset.MeshSetResource;
            ResAssetEntry resEntry = App.AssetManager.GetResEntry(resRid);
            MeshSet meshSet = App.AssetManager.GetResAs<MeshSet>(resEntry);
            if (meshSet == null)
                throw new Exception("could not load the MeshSet res for " + entry.Name);

            FrostyMeshImportSettings settings = null;
            if (meshSet.Type == MeshType.MeshType_Skinned)
            {
                string skeleton = GetSkeletonForBulk();
                if (string.IsNullOrEmpty(skeleton))
                    skeleton = Config.Get<string>("MeshSetImportSkeleton", "", ConfigScope.Game);
                if (string.IsNullOrEmpty(skeleton))
                    throw new Exception("this is a skinned mesh and no skeleton is available for it");

                settings = new FrostyMeshImportSettings { SkeletonAsset = skeleton };
            }

            FBXImporter importer = new FBXImporter(App.Logger);
            importer.ImportFBX(path, meshSet, asset, entry, settings);
            return true;
        }

        /// <summary>
        /// The skeleton a bulk run resolved for the asset being processed, or the exported/imported
        /// default from the configuration when no bulk run is in progress.
        /// </summary>
        private static string GetSkeletonForBulk()
        {
            string skeleton = BulkContext.Skeleton;
            if (string.IsNullOrEmpty(skeleton))
                skeleton = Config.Get<string>("MeshSetExportSkeleton", "", ConfigScope.Game);
            return skeleton;
        }

        /// <summary>
        /// Builds the export settings from the configuration. This is the same source the settings
        /// dialog is filled from, so a bulk export and a single asset export of the same mesh agree.
        /// </summary>
        private MeshExportSettings CreateSettings(EbxAssetEntry entry)
        {
            MeshExportSettings settings = (entry.Type == "SkinnedMeshAsset")
                ? new SkinnedMeshExportSettings()
                : new MeshExportSettings();

            string Version = Config.Get<string>("MeshSetExportVersion", "FBX_2012", ConfigScope.Game);
            string Scale = Config.Get<string>("MeshSetExportScale", "Centimeters", ConfigScope.Game);
            bool flattenHierarchy = Config.Get<bool>("MeshSetExportFlattenHierarchy", false, ConfigScope.Game);
            bool exportSingleLod = Config.Get<bool>("MeshSetExportExportSingleLod", false, ConfigScope.Game);
            bool exportAdditionalMeshes = Config.Get<bool>("MeshSetExportExportAdditionalMeshes", false, ConfigScope.Game);
            string skeleton = Config.Get<string>("MeshSetExportSkeleton", "", ConfigScope.Game);

            //string Version = Config.Get<string>("MeshSetExport", "Version", "FBX_2012");
            //string Scale = Config.Get<string>("MeshSetExport", "Scale", "Centimeters");
            //bool flattenHierarchy = Config.Get<bool>("MeshSetExport", "FlattenHierarchy", false);
            //bool exportAdditionalMeshes = Config.Get<bool>("MeshSetExport", "ExportAdditionalMeshes", false);
            //string skeleton = Config.Get<string>("MeshSetExport", "Skeleton", "");

            settings.Version = (MeshExportVersion)Enum.Parse(typeof(MeshExportVersion), Version);
            settings.Scale = (MeshExportScale)Enum.Parse(typeof(MeshExportScale), Scale);
            settings.FlattenHierarchy = flattenHierarchy;
            settings.ExportSingleLod = exportSingleLod;
            settings.ExportAdditionalMeshes = exportAdditionalMeshes;

            if (settings is SkinnedMeshExportSettings exportSettings)
                exportSettings.SkeletonAsset = skeleton;

            return settings;
        }

        private bool PreExport(EbxAssetEntry entry, out MeshExportSettings outSettings)
        {
            outSettings = CreateSettings(entry);
            return FrostyImportExportBox.Show<MeshExportSettings>("Mesh Export Settings", FrostyImportExportType.Export, outSettings) == MessageBoxResult.OK;
        }

        private void ExportInternal(EbxAssetEntry entry, string path, MeshExportSettings settings, string filterType)
        {
            // The single asset path shows its own task window; a bulk run passes its own task instead
            // so a folder of meshes reports through the one progress window the user already sees.
            FrostyTaskWindow.Show("Exporting MeshSet", "", (task) =>
            {
                ExportWithSettings(entry, path, settings, filterType, task);
            });
        }

        private void ExportWithSettings(EbxAssetEntry entry, string path, MeshExportSettings settings, string filterType, FrostyTaskWindow task)
        {
            // get ebx
            EbxAsset asset = App.AssetManager.GetEbx(entry);
            dynamic meshAsset = (dynamic)asset.RootObject;

            // get mesh res
            ulong resRid = meshAsset.MeshSetResource;
            ResAssetEntry rEntry = App.AssetManager.GetResEntry(resRid);
            MeshSet meshSet = App.AssetManager.GetResAs<MeshSet>(rEntry);

            // get skeleton (if required)
            string skeleton = "";
            if (meshSet.Type == MeshType.MeshType_Skinned)
                skeleton = ((SkinnedMeshExportSettings)settings).SkeletonAsset;

            FBXExporter exporter = new FBXExporter(task);
            exporter.ExportFBX(meshAsset, path, settings.Version.ToString().Replace("FBX_", ""), settings.Scale.ToString(), settings.FlattenHierarchy, settings.ExportSingleLod, skeleton, MeshFileType(filterType), meshSet);
        }

        /// <summary>The formats this definition can write a mesh as.</summary>
        private static bool IsMeshFormat(string filterType)
        {
            return filterType == "fbx" || filterType == "fbx-ascii" || filterType == "obj";
        }

        /// <summary>
        /// The form the FBX exporter is asked for: the ASCII and binary FBX forms are the same file
        /// type, and OBJ is its own.
        /// </summary>
        private static string MeshFileType(string filterType)
        {
            if (filterType == "obj")
                return "obj";
            return filterType == "fbx-ascii" ? "ascii" : "binary";
        }

        private void PostExport(MeshExportSettings settings)
        {
            Config.Add("MeshSetExportVersion", settings.Version.ToString(), ConfigScope.Game);
            Config.Add("MeshSetExportScale", settings.Scale.ToString(), ConfigScope.Game);
            Config.Add("MeshSetExportFlattenHierarchy", settings.FlattenHierarchy, ConfigScope.Game);
            Config.Add("MeshSetExportExportSingleLod", settings.ExportSingleLod, ConfigScope.Game);
            Config.Add("MeshSetExportExportAdditionalMeshes", settings.ExportAdditionalMeshes, ConfigScope.Game);

            //Config.Add("MeshSetExport", "Version", settings.Version.ToString());
            //Config.Add("MeshSetExport", "Scale", settings.Scale.ToString());
            //Config.Add("MeshSetExport", "FlattenHierarchy", settings.FlattenHierarchy);
            //Config.Add("MeshSetExport", "ExportAdditionalMeshes", settings.ExportAdditionalMeshes);

            if (settings is SkinnedMeshExportSettings exportSettings)
                Config.Add("MeshSetExportSkeleton", exportSettings.SkeletonAsset, ConfigScope.Game);
            //Config.Add("MeshSetExport", "Skeleton", exportSettings.SkeletonAsset);

            Config.Save();
        }
    }

    public class RigidMeshAssetDefinition : MeshAssetDefinition
    {
        public override ImageSource GetIcon()
        {
            return rigidMeshSource;
        }
    }
    public class SkinnedMeshAssetDefinition : MeshAssetDefinition
    {
        public override ImageSource GetIcon()
        {
            return skinnedMeshSource;
        }
    }
    public class CompositeMeshAssetDefinition : MeshAssetDefinition
    {
        public override ImageSource GetIcon()
        {
            return compositeMeshSource;
        }
    }
}
