using System;
using System.Collections.Generic;

namespace AssetBankPlugin.Ant
{
    public class LayoutAsset : AntAsset
    {
        public override string Name { get; set; }
        public override Guid ID { get; set; }

        public List<Slot> Slots { get; set; }

        public override void SetData(Dictionary<string, object> data)
        {
            object v;
            Name = data.TryGetValue("__name", out v) ? Convert.ToString(v) : "";
            ID = data.TryGetValue("__guid", out v) && v is Guid ? (Guid)v : Guid.Empty;

            Slots = new List<Slot>();
            if (data.TryGetValue("Slots", out v) && v is Dictionary<string, object>[])
            {
                var slots = (Dictionary<string, object>[])v;
                foreach (var slot in slots)
                {
                    object nameObj, typeObj;
                    string slotName = slot.TryGetValue("Name", out nameObj) ? Convert.ToString(nameObj) : "";
                    BoneChannelType slotType = slot.TryGetValue("Type", out typeObj) ? (BoneChannelType)Convert.ToInt32(typeObj) : BoneChannelType.None;
                    Slots.Add(new Slot() { Name = slotName, Type = slotType });
                }
            }
        }
    }

    public struct Slot
    {
        public string Name;
        public BoneChannelType Type;

        public override string ToString()
        {
            return Name + ", " + Type.ToString();
        }
    }
}
