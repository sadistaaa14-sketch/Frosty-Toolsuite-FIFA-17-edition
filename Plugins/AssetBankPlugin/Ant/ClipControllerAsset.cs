using System;
using System.Collections.Generic;

namespace AssetBankPlugin.Ant
{
    public class ClipControllerAsset : AntAsset
    {
        public override string Name { get; set; }
        public override Guid ID { get; set; }

        public Guid[] Anims { get; set; }
        public Guid Target { get; set; }
        public float NumTicks { get; set; }
        public float TickOffset { get; set; }
        public float FPS { get; set; }
        public float TimeScale { get; set; }
        public float Distance { get; set; }
        public int TrajectoryAnimIndex { get; set; }
        public int Modes { get; set; }
        public Guid TagCollectionSet { get; set; }
        public long AnimRefKey { get; set; }

        public override void SetData(Dictionary<string, object> data)
        {
            object v;
            Name = data.TryGetValue("__name", out v) ? Convert.ToString(v) : "";
            ID = data.TryGetValue("__guid", out v) && v is Guid ? (Guid)v : Guid.Empty;
            Anims = data.TryGetValue("Anims", out v) ? v as Guid[] : new Guid[0];
            if (Anims == null) Anims = new Guid[0];
            Target = data.TryGetValue("Target", out v) && v is Guid ? (Guid)v : Guid.Empty;
            NumTicks = data.TryGetValue("NumTicks", out v) ? Convert.ToSingle(v) : 0f;
            TickOffset = data.TryGetValue("TickOffset", out v) ? Convert.ToSingle(v) : 0f;
            FPS = data.TryGetValue("FPS", out v) ? Convert.ToSingle(v) : 30f;
            TimeScale = data.TryGetValue("TimeScale", out v) ? Convert.ToSingle(v) : 1f;
            Distance = data.TryGetValue("Distance", out v) ? Convert.ToSingle(v) : 0f;
            TrajectoryAnimIndex = data.TryGetValue("TrajectoryAnimIndex", out v) ? Convert.ToInt32(v) : 0;
            Modes = data.TryGetValue("Modes", out v) ? Convert.ToInt32(v) : 0;
            TagCollectionSet = data.TryGetValue("TagCollectionSet", out v) && v is Guid ? (Guid)v : Guid.Empty;

            // FIFA 17: "Anim" is a DataRef — could be long, int, or Dict{Data1:Int64}
            AnimRefKey = 0;
            if (data.TryGetValue("Anim", out v))
            {
                if (v is long) AnimRefKey = (long)v;
                else if (v is int) AnimRefKey = (int)v;
                else if (v is Dictionary<string, object>)
                {
                    var d = (Dictionary<string, object>)v;
                    object d1;
                    if (d.TryGetValue("Data1", out d1))
                        try { AnimRefKey = Convert.ToInt64(d1); } catch { }
                }
                else try { AnimRefKey = Convert.ToInt64(v); } catch { }
            }
        }
    }
}
