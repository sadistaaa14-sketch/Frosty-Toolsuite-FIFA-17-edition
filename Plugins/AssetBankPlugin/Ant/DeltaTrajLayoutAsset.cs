using System;
using System.Collections.Generic;

namespace AssetBankPlugin.Ant
{
    public class DeltaTrajLayoutAsset : AntAsset
    {
        public override string Name { get; set; }
        public override Guid ID { get; set; }

        public override void SetData(Dictionary<string, object> data)
        {
            object v;
            Name = data.TryGetValue("__name", out v) ? Convert.ToString(v) : "";
            ID = data.TryGetValue("__guid", out v) && v is Guid ? (Guid)v : Guid.Empty;
        }
    }
}
