using AssetBankPlugin.Export;
using System;
using System.Collections.Generic;
using System.Numerics;

namespace AssetBankPlugin.Ant
{
    public class FrameAnimationAsset : AnimationAsset
    {
        public int FloatCount = 0;
        public int Vec3Count = 0;
        public int QuatCount = 0;
        public float[] Data = new float[0];

        public FrameAnimationAsset() { }

        public override void SetData(Dictionary<string, object> data)
        {
            object v;
            Name = data.TryGetValue("__name", out v) ? Convert.ToString(v) : "";
            ID = data.TryGetValue("__guid", out v) && v is Guid ? (Guid)v : Guid.Empty;
            Data = data.TryGetValue("Data", out v) ? v as float[] : new float[0];
            if (Data == null) Data = new float[0];
            FloatCount = data.TryGetValue("FloatCount", out v) ? Convert.ToInt32(v) : 0;
            Vec3Count = data.TryGetValue("Vec3Count", out v) ? Convert.ToInt32(v) : 0;
            QuatCount = data.TryGetValue("QuatCount", out v) ? Convert.ToInt32(v) : 0;

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
            List<Vector3> positions = new List<Vector3>();
            List<Quaternion> rotations = new List<Quaternion>();
            List<string> posChannels = new List<string>();
            List<string> rotChannels = new List<string>();

            if (Channels == null) { ret.Name = Name; return ret; }

            int dataIndex = 0;
            foreach (var channel in Channels)
            {
                if (channel.Value == BoneChannelType.Rotation)
                    rotChannels.Add(channel.Key.Replace(".q", ""));
                else if (channel.Value == BoneChannelType.Position)
                    posChannels.Add(channel.Key.Replace(".t", ""));
            }
            foreach (var channel in Channels)
            {
                if (channel.Value == BoneChannelType.Rotation)
                {
                    if (dataIndex + 3 < Data.Length)
                        rotations.Add(new Quaternion(Data[dataIndex++], Data[dataIndex++], Data[dataIndex++], Data[dataIndex++]));
                    else { rotations.Add(Quaternion.Identity); dataIndex += 4; }
                }
                else if (channel.Value == BoneChannelType.Position)
                {
                    if (dataIndex + 3 < Data.Length)
                    {
                        positions.Add(new Vector3(Data[dataIndex++], Data[dataIndex++], Data[dataIndex++]));
                        dataIndex++;
                    }
                    else { positions.Add(Vector3.Zero); dataIndex += 4; }
                }
            }

            ret.Name = Name;
            var frame = new Frame();
            frame.FrameIndex = 0;
            frame.Positions = positions;
            frame.Rotations = rotations;
            ret.Frames.Add(frame);
            ret.PositionChannels = posChannels;
            ret.RotationChannels = rotChannels;
            ret.Additive = Additive;
            return ret;
        }
    }
}
