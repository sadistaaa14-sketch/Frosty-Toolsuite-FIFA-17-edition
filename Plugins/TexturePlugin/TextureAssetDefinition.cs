using Frosty.Core;
using Frosty.Core.Controls;
using Frosty.Core.Windows;
using FrostySdk.Interfaces;
using FrostySdk.IO;
using FrostySdk.Managers;
using FrostySdk.Resources;
using System.Collections.Generic;
using System.Windows.Media;

namespace TexturePlugin
{
    public class TextureAssetDefinition : AssetDefinition
    {
        private static ImageSource imageSource = new ImageSourceConverter().ConvertFromString("pack://application:,,,/FrostyCore;component/Images/Assets/ImageFileType.png") as ImageSource;

        public TextureAssetDefinition()
        {
        }

        public override void GetSupportedExportTypes(List<AssetExportType> exportTypes)
        {
            exportTypes.Add(new AssetExportType("png", "Portable Network Graphics"));
            exportTypes.Add(new AssetExportType("tga", "Truevision TGA"));
            exportTypes.Add(new AssetExportType("hdr", "High Dynamic Range"));
            exportTypes.Add(new AssetExportType("dds", "Direct Draw Surface"));

            base.GetSupportedExportTypes(exportTypes);
        }

        public override ImageSource GetIcon()
        {
            return imageSource;
        }

        public override bool Export(EbxAssetEntry entry, string path, string filterType)
        {
            // The base class writes the xml and bin forms, and its answer is the real answer - it
            // used to be dropped here, which made a perfectly good xml export look like a failure.
            if (base.Export(entry, path, filterType))
                return true;

            if (filterType == "png" || filterType == "tga" || filterType == "hdr" || filterType == "dds")
            {
                EbxAsset asset = App.AssetManager.GetEbx(entry);
                dynamic textureAsset = (dynamic)asset.RootObject;

                ResAssetEntry resEntry = App.AssetManager.GetResEntry(textureAsset.Resource);
                Texture texture = App.AssetManager.GetResAs<Texture>(resEntry);

                TextureExporter exporter = new TextureExporter();
                exporter.Export(texture, path, "*." + filterType);
                return true;
            }

            return false;
        }

        /// <summary>
        /// Imports an image into a texture without asking the user anything, which is what a bulk
        /// import needs. Every image format the texture types support is handled by the texture
        /// editor's importer - the same code the single asset Import button runs - so a bulk import
        /// replaces the texture's bytes in exactly the way a manual import does. The bin/xml forms
        /// still go through the base implementation.
        /// </summary>
        public override bool ImportSilent(EbxAssetEntry entry, string path, string filterType, FrostyTaskWindow task)
        {
            if (filterType != "png" && filterType != "tga" && filterType != "hdr" && filterType != "dds")
                return base.ImportSilent(entry, path, filterType, task);

            if (!FrostyTextureEditor.ImportTexture2D(entry, path, App.Logger, out string error))
            {
                App.Logger.LogError("Could not import {0} into {1}: {2}",
                    System.IO.Path.GetFileName(path), entry.Name, error);
                return false;
            }

            return true;
        }

        public override FrostyAssetEditor GetEditor(ILogger logger)
        {
            return new FrostyTextureEditor(logger);
        }
    }
}
