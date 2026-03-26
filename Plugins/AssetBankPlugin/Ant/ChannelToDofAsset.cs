using AssetBankPlugin.Enums;
using FrostySdk;
using System;
using System.Collections.Generic;

namespace AssetBankPlugin.Ant
{
    public class ChannelToDofAsset : AntAsset
    {
        public override string Name { get; set; }
        public override Guid ID { get; set; }

        public StorageType StorageType { get; set; } = StorageType.Overwrite;
        public uint[] IndexData { get; set; }

        public override void SetData(Dictionary<string, object> data)
        {
            object v;
            Name = data.TryGetValue("__name", out v) ? Convert.ToString(v) : "";
            ID = data.TryGetValue("__guid", out v) && v is Guid ? (Guid)v : Guid.Empty;

            IndexData = new uint[0];

            switch ((ProfileVersion)ProfilesLibrary.DataVersion)
            {
                case ProfileVersion.PlantsVsZombiesGardenWarfare2:
                case ProfileVersion.Battlefield1:
                {
                    if (data.TryGetValue("DofIds", out v) && v is ushort[])
                    {
                        ushort[] dofIds = (ushort[])v;
                        IndexData = Array.ConvertAll(dofIds, val => checked((uint)val));
                    }
                } break;
                case ProfileVersion.Battlefield4:
                {
                    if (data.TryGetValue("StorageType", out v))
                        StorageType = (StorageType)Convert.ToInt32(v);
                    if (data.TryGetValue("IndexData", out v) && v is byte[])
                    {
                        byte[] dofIds = (byte[])v;
                        IndexData = Array.ConvertAll(dofIds, val => checked((uint)val));
                    }
                } break;

                default:
                {
                    // FIFA 17 path — try DofIds first, then IndexData
                    if (data.TryGetValue("DofIds", out v) && v is ushort[])
                    {
                        ushort[] dofIds = (ushort[])v;
                        IndexData = Array.ConvertAll(dofIds, val => checked((uint)val));
                    }
                    else if (data.TryGetValue("StorageType", out v))
                    {
                        StorageType = (StorageType)Convert.ToInt32(v);
                        if (data.TryGetValue("IndexData", out var idv) && idv is byte[])
                        {
                            byte[] dofIds = (byte[])idv;
                            IndexData = Array.ConvertAll(dofIds, val => checked((uint)val));
                        }
                    }
                } break;
            }
        }
    }
}
