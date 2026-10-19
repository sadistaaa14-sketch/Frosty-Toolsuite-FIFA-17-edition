using Frosty.Core;
using Frosty.Core.Controls;
using FrostySdk;
using FrostySdk.Interfaces;
using System.Windows.Media;

namespace SoundEditorPlugin
{
    public class SoundWaveAssetOverride : BaseTypeOverride
    {
//#if FROSTY_DEVELOPER
        public BaseFieldOverride Chunks { get; set; }
        public BaseFieldOverride RuntimeVariations { get; set; }
        public BaseFieldOverride Segments { get; set; }
        public BaseFieldOverride Localization { get; set; }
        public BaseFieldOverride SubtitleStringIds { get; set; }
        public BaseFieldOverride Subtitles { get; set; }
//#else
        //[IsHidden]
        //public BaseFieldOverride Chunks { get; set; }
        //[IsHidden]
        //public BaseFieldOverride RuntimeVariations { get; set; }
        //[IsHidden]
        //public BaseFieldOverride Segments { get; set; }
        //[IsHidden]
        //public BaseFieldOverride Localization { get; set; }
        //[IsHidden]
        //public BaseFieldOverride SubtitleStringIds { get; set; }
        //[IsHidden]
        //public BaseFieldOverride Subtitles { get; set; }
//#endif
    }

    public class SoundAssetDefinition : AssetDefinition
    {
        protected static ImageSource imageSource = new ImageSourceConverter().ConvertFromString("pack://application:,,,/SoundEditorPlugin;component/Images/SoundFileType.png") as ImageSource;
        public override ImageSource GetIcon()
        {
            return imageSource;
        }
    }

    public class SoundWaveAssetDefinition : SoundAssetDefinition
    {
        public override FrostyAssetEditor GetEditor(ILogger logger)
        {
            return new FrostySoundWaveEditor(logger);
        }
    }
    public class NewWaveAssetDefinition : SoundAssetDefinition
    {
        public override FrostyAssetEditor GetEditor(ILogger logger)
        {
            return new FrostyNewWaveEditor(logger);
        }
    }
    public class HarmonySampleBankAssetDefinition : SoundAssetDefinition
    {
        public override FrostyAssetEditor GetEditor(ILogger logger)
        {
            return new FrostyHarmonySampleBankEditor(logger);
        }
    }
    /// <summary>
    /// A localized wave is a sound bank: a .res clip index over a handful of shared chunks.
    ///
    /// It is edited by <see cref="FrostyNewWaveEditor"/> rather than by an editor of its own. There used
    /// to be a second, separate reader of these files - one that looked for a fixed table, and scanned
    /// the chunk for clip headers when it did not find one - and two readers of one format is how the
    /// wrong one ends up being the one that writes to a file.
    /// </summary>
    public class LocalizedWaveAssetDefinition : SoundAssetDefinition
    {
        public override FrostyAssetEditor GetEditor(ILogger logger)
        {
            return new FrostyNewWaveEditor(logger);
        }
    }
    public class OctaneAssetDefinition : SoundAssetDefinition
    {
        public override FrostyAssetEditor GetEditor(ILogger logger)
        {
            return new FrostyOctaneSoundEditor(logger);
        }
    }
    public class ImpulseResponseAssetDefinition : SoundAssetDefinition
    {
        public override FrostyAssetEditor GetEditor(ILogger logger)
        {
            return new FrostyImpulseResponseEditor(logger);
        }
    }
}
