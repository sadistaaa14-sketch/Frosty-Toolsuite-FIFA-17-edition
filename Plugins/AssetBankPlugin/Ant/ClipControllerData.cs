using System;
using System.Collections.Generic;

namespace AssetBankPlugin.Ant
{
    public class ClipControllerData : AntAsset
    {
        public override string Name { get; set; }
        public override Guid ID { get; set; }

        public Guid Anim { get; set; }
        public Guid Target { get; set; }
        public Guid ChannelToDofAsset { get; set; }
        public float FPS { get; set; }
        public float FPSScale { get; set; }
        public float TrimOffset { get; set; }
        public float NumTicks { get; set; }
        public float TickOffset { get; set; }
        public float Distance { get; set; }
        public int CodecType { get; set; }

        public override void SetData(Dictionary<string, object> data)
        {
            object v;
            Name = data.TryGetValue("__name", out v) ? Convert.ToString(v) : "";
            ID = data.TryGetValue("__guid", out v) && v is Guid ? (Guid)v : Guid.Empty;
            Anim = data.TryGetValue("Anim", out v) && v is Guid ? (Guid)v : Guid.Empty;
            Target = data.TryGetValue("Target", out v) && v is Guid ? (Guid)v : Guid.Empty;
            ChannelToDofAsset = data.TryGetValue("ChannelToDofAsset", out v) && v is Guid ? (Guid)v : Guid.Empty;
            FPS = data.TryGetValue("FPS", out v) ? Convert.ToSingle(v) : 30f;
            CodecType = data.TryGetValue("CodecType", out v) ? Convert.ToInt32(v) : 0;
            FPSScale = data.TryGetValue("FPSScale", out v) ? Convert.ToSingle(v) : 1f;
            TickOffset = data.TryGetValue("TickOffset", out v) ? Convert.ToSingle(v) : 0f;
            NumTicks = data.TryGetValue("NumTicks", out v) ? Convert.ToSingle(v) : 0f;
            TrimOffset = data.TryGetValue("TrimOffset", out v) ? Convert.ToSingle(v) : 0f;
            Distance = data.TryGetValue("Distance", out v) ? Convert.ToSingle(v) : 0f;
        }
    }
}
