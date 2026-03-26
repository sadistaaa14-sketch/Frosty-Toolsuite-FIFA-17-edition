using System;
using System.Collections.Generic;
using System.Linq;
using AssetBankPlugin.Enums;
using AssetBankPlugin.Export;
using FrostySdk;

namespace AssetBankPlugin.Ant
{
    public class AnimationAsset : AntAsset
    {
        public override string Name { get; set; }
        public override Guid ID { get; set; }

        public int CodecType;
        public int AnimId;
        public float TrimOffset;
        public ushort EndFrame;
        public bool Additive;
        public Guid ChannelToDofAsset;
        public Dictionary<string, BoneChannelType> Channels;
        public float FPS;

        public StorageType StorageType;

        public AnimationAsset() { }

        public override void SetData(Dictionary<string, object> data)
        {
            object v;
            Name = data.TryGetValue("__name", out v) ? Convert.ToString(v) : "";
            ID = data.TryGetValue("__guid", out v) && v is Guid ? (Guid)v : Guid.Empty;
            CodecType = data.TryGetValue("CodecType", out v) ? Convert.ToInt32(v) : 0;
            AnimId = data.TryGetValue("AnimId", out v) ? Convert.ToInt32(v) : 0;
            TrimOffset = data.TryGetValue("TrimOffset", out v) ? Convert.ToSingle(v) : 0f;
            EndFrame = data.TryGetValue("EndFrame", out v) ? Convert.ToUInt16(v) : (ushort)0;
            if (data.TryGetValue("Additive", out v) && v is bool)
                Additive = (bool)v;
            ChannelToDofAsset = data.TryGetValue("ChannelToDofAsset", out v) && v is Guid ? (Guid)v : Guid.Empty;
        }

        public Dictionary<string, BoneChannelType> GetChannels(Guid channelToDofAsset)
        {
            LayoutHierarchyAsset hierarchy = null;
            ChannelToDofAsset dof = null;

            try
            {
                switch ((ProfileVersion)ProfilesLibrary.DataVersion)
                {
                    case ProfileVersion.PlantsVsZombiesGardenWarfare2:
                    case ProfileVersion.Battlefield1:
                    {
                        var dofAsset = AntRefTable.Get(ChannelToDofAsset);
                        if (dofAsset is ChannelToDofAsset ctd)
                        {
                            dof = ctd;
                            foreach (var c in AntRefTable.Refs)
                            {
                                if (c.Value is ClipControllerAsset cl && cl.Anims != null && cl.Anims.Contains(ID))
                                {
                                    FPS = cl.FPS;
                                    var target = AntRefTable.Get(cl.Target);
                                    if (target is LayoutHierarchyAsset lha) hierarchy = lha;
                                    break;
                                }
                            }
                        }
                    } break;
                    case ProfileVersion.Battlefield4:
                    default:
                    {
                        var dofAsset = AntRefTable.Get(channelToDofAsset);
                        if (dofAsset is ChannelToDofAsset ctd)
                        {
                            dof = ctd;
                            StorageType = dof.StorageType;
                        }
                        else
                        {
                            // FIFA 17: ChannelToDofAsset ref might not resolve via this path
                            return null;
                        }

                        foreach (var c in AntRefTable.Refs)
                        {
                            if (c.Value is ClipControllerData cl)
                            {
                                bool match = cl.Anim == ID;
                                if (!match && AntRefTable.InternalRefs.ContainsKey(ID))
                                    match = cl.Anim == AntRefTable.InternalRefs[ID];
                                if (match)
                                {
                                    FPS = cl.FPS;
                                    var target = AntRefTable.Get(cl.Target);
                                    if (target is LayoutHierarchyAsset lha) hierarchy = lha;
                                    break;
                                }
                            }
                        }
                    } break;
                }
            }
            catch
            {
                return null;
            }

            if (dof == null || hierarchy == null) return null;

            // Build channel names from LayoutAssets
            var channelNames = new Dictionary<string, BoneChannelType>();
            try
            {
                for (int i = 0; i < hierarchy.LayoutAssets.Length; i++)
                {
                    AntAsset layoutAsset = AntRefTable.Get(hierarchy.LayoutAssets[i]);
                    if (layoutAsset is LayoutAsset la)
                    {
                        for (int x = 0; x < la.Slots.Count; x++)
                            channelNames[la.Slots[x].Name] = la.Slots[x].Type;
                    }
                }
            }
            catch { return null; }

            if (channelNames.Count == 0) return null;

            uint[] indexData = dof.IndexData;
            var channels = new List<string>();

            try
            {
                switch (StorageType)
                {
                    case StorageType.Overwrite:
                    {
                        for (int i = 0; i < indexData.Length; i++) channels.Add("");
                        for (int i = 0; i < indexData.Length; i++)
                        {
                            int channelId = (int)indexData[i];
                            if (channelId < channelNames.Count)
                                channels[i] = channelNames.ElementAt(channelId).Key;
                        }
                    } break;
                    case StorageType.Append:
                    {
                        var offsets = new Dictionary<int, int>();
                        int offset = 0;
                        for (int i = 0; i < indexData.Length; i += 2)
                        {
                            int appendTo = (int)indexData[i];
                            int channelId = (int)indexData[i + 1];
                            offsets[appendTo] = offset;
                            offset++;
                            if (channelId < channelNames.Count)
                                channels.Insert(offsets[appendTo], channelNames.ElementAt(channelId).Key);
                        }
                    } break;
                }
            }
            catch { return null; }

            // Reorder
            var output = new Dictionary<string, BoneChannelType>();
            for (int i = 0; i < channels.Count; i++)
            {
                if (!string.IsNullOrEmpty(channels[i]) && channelNames.ContainsKey(channels[i]))
                    output[channels[i]] = channelNames[channels[i]];
            }

            return output.Count > 0 ? output : null;
        }

        public virtual InternalAnimation ConvertToInternal() { return null; }
    }

    public enum BoneChannelType
    {
        None = 0,
        Rotation = 14,
        Position = 2049856663,
        Scale = 2049856454,
    }
}
