using Frosty.Core;
using FrostySdk.Attributes;

namespace BundleRefTablePlugin
{
    [DisplayName("BundleRefTable Options")]
    public class BRTOptions : OptionsExtension
    {
        [Category("Duplication")]
        [DisplayName("Skip BRT Adding")]
        [Description("When checked, assets made by the Duplication plugin's tools (Duplicate Starhead, Kit, Ball and the other Duplicate tools, and sound assets duplicated with Duplicate) will not be added to the relevant BRTs.")]
        public bool SkipBrtAdd { get; set; }

        public override void Load()
        {
            SkipBrtAdd = Config.Get<bool>("SkipBrtAdd", false);
        }

        public override void Save()
        {
            Config.Add("SkipBrtAdd", SkipBrtAdd);
            Config.Save();
        }
    }
}
