using System;
using System.Collections.Generic;

namespace AssetBankPlugin.Ant
{
    public class LayoutHierarchyAsset : AntAsset
    {
        public override string Name { get; set; }
        public override Guid ID { get; set; }

        public Guid[] LayoutAssets { get; set; }
        public Guid[] Children { get; set; }

        public override void SetData(Dictionary<string, object> data)
        {
            object v;
            Name = data.TryGetValue("__name", out v) ? Convert.ToString(v) : "";
            ID = data.TryGetValue("__guid", out v) && v is Guid ? (Guid)v : Guid.Empty;

            LayoutAssets = data.TryGetValue("LayoutAssets", out v) ? v as Guid[] : new Guid[0];
            if (LayoutAssets == null) LayoutAssets = new Guid[0];

            Children = data.TryGetValue("Children", out v) ? v as Guid[] : new Guid[0];
            if (Children == null) Children = new Guid[0];
        }
    }
}
