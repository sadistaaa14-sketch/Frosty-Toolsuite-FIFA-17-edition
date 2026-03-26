using AssetBankPlugin.Export;
using System;
using System.Collections.Generic;
using System.Numerics;

namespace AssetBankPlugin.Ant
{
    public class CurveAnimationAsset : AnimationAsset
    {
        public ushort NumRotations;
        public ushort NumVectors;
        public ushort NumFloats;
        public float[] Values;
        public ushort[] Keys;
        public ushort[] ChannelOffsets;

        public CurveAnimationAsset() { }

        public override void SetData(Dictionary<string, object> data)
        {
            object v;
            Name = data.TryGetValue("__name", out v) ? Convert.ToString(v) : "";
            ID = data.TryGetValue("__guid", out v) && v is Guid ? (Guid)v : Guid.Empty;
            FPS = data.TryGetValue("FPS", out v) ? Convert.ToSingle(v) : 30f;
            NumRotations = data.TryGetValue("NumRotations", out v) ? Convert.ToUInt16(v) : (ushort)0;
            NumVectors = data.TryGetValue("NumVectors", out v) ? Convert.ToUInt16(v) : (ushort)0;
            NumFloats = data.TryGetValue("NumFloats", out v) ? Convert.ToUInt16(v) : (ushort)0;
            Values = data.TryGetValue("Values", out v) ? v as float[] : new float[0];
            if (Values == null) Values = new float[0];
            Keys = data.TryGetValue("Keys", out v) ? v as ushort[] : new ushort[0];
            if (Keys == null) Keys = new ushort[0];
            ChannelOffsets = data.TryGetValue("ChannelOffsets", out v) ? v as ushort[] : new ushort[0];
            if (ChannelOffsets == null) ChannelOffsets = new ushort[0];

            CodecType = data.TryGetValue("CodecType", out v) ? Convert.ToInt32(v) : 0;
            AnimId = data.TryGetValue("AnimId", out v) ? Convert.ToInt32(v) : 0;
            TrimOffset = data.TryGetValue("TrimOffset", out v) ? Convert.ToSingle(v) : 0f;
            EndFrame = data.TryGetValue("EndFrame", out v) ? Convert.ToUInt16(v) : (ushort)0;
            if (data.TryGetValue("Additive", out v) && v is bool) Additive = (bool)v;
            ChannelToDofAsset = data.TryGetValue("ChannelToDofAsset", out v) && v is Guid ? (Guid)v : Guid.Empty;
        }

        public override InternalAnimation ConvertToInternal()
        {
            InternalAnimation ret = new InternalAnimation();
            List<string> posChannels = new List<string>();
            List<string> rotChannels = new List<string>();

            if (Channels == null) { ret.Name = Name; return ret; }

            foreach (var channel in Channels)
            {
                if (channel.Value == BoneChannelType.Rotation)
                    rotChannels.Add(channel.Key.Replace(".q", ""));
                else if (channel.Value == BoneChannelType.Position)
                    posChannels.Add(channel.Key.Replace(".t", ""));
            }

            ret.Name = Name;
            ret.PositionChannels = posChannels;
            ret.RotationChannels = rotChannels;
            ret.Additive = Additive;
            return ret;
        }
    }
}
