using Frosty.Core;
using FrostySdk.Attributes;

namespace BundleRefTablePlugin
{
    [DisplayName("BundleRefTable Options")]
    public class BRTOptions : OptionsExtension
    {
        [Category("Duplication")]
        [DisplayName("Skip BRT Adding")]
        [Description("When checked, assets duplicated through Duplicate Starhead will not be added to the relevant BRTs.")]
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
