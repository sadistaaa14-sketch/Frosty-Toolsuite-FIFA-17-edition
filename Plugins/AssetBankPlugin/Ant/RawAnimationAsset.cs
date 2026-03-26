using AssetBankPlugin.Export;
using System;
using System.Collections.Generic;
using System.Numerics;

namespace AssetBankPlugin.Ant
{
    public class RawAnimationAsset : AnimationAsset
    {
        public int NumKeys;
        public int FloatCount;
        public int Vec3Count;
        public int QuatCount;
        public ushort[] KeyTimes;
        public float[] Data;
        public bool Cycle;

        public RawAnimationAsset() { }

        public override void SetData(Dictionary<string, object> data)
        {
            object v;
            Name = data.TryGetValue("__name", out v) ? Convert.ToString(v) : "";
            ID = data.TryGetValue("__guid", out v) && v is Guid ? (Guid)v : Guid.Empty;
            NumKeys = data.TryGetValue("NumKeys", out v) ? Convert.ToInt32(v) : 0;
            FloatCount = data.TryGetValue("FloatCount", out v) ? Convert.ToInt32(v) : 0;
            Vec3Count = data.TryGetValue("Vec3Count", out v) ? Convert.ToInt32(v) : 0;
            QuatCount = data.TryGetValue("QuatCount", out v) ? Convert.ToInt32(v) : 0;
            KeyTimes = data.TryGetValue("KeyTimes", out v) ? v as ushort[] : new ushort[0];
            if (KeyTimes == null) KeyTimes = new ushort[0];
            Data = data.TryGetValue("Data", out v) ? v as float[] : new float[0];
            if (Data == null) Data = new float[0];
            Cycle = data.TryGetValue("Cycle", out v) && v is bool ? (bool)v : false;

            // Base fields
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

            int dataIndex = 0;
            for (int frameIndex = 0; frameIndex < KeyTimes.Length; frameIndex++)
            {
                var frame = new Frame();
                List<Vector3> positions = new List<Vector3>();
                List<Quaternion> rotations = new List<Quaternion>();

                for (int i = 0; i < QuatCount; i++)
                {
                    if (dataIndex + 3 < Data.Length)
                        rotations.Add(new Quaternion(Data[dataIndex++], Data[dataIndex++], Data[dataIndex++], Data[dataIndex++]));
                    else { rotations.Add(Quaternion.Identity); dataIndex += 4; }
                }
                for (int i = 0; i < Vec3Count; i++)
                {
                    if (dataIndex + 2 < Data.Length)
                        positions.Add(new Vector3(Data[dataIndex++], Data[dataIndex++], Data[dataIndex++]));
                    else { positions.Add(Vector3.Zero); dataIndex += 3; }
                }

                frame.FrameIndex = KeyTimes[frameIndex];
                frame.Positions = positions;
                frame.Rotations = rotations;
                ret.Frames.Add(frame);
            }

            ret.Name = Name;
            ret.PositionChannels = posChannels;
            ret.RotationChannels = rotChannels;
            ret.Additive = Additive;
            return ret;
        }
    }
}
