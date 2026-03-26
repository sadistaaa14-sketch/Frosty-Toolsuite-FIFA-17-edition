using AssetBankPlugin.Ant;
using AssetBankPlugin.Export;
using System;
using System.Collections.Generic;
using System.Numerics;
using System.Linq;

namespace AssetBankPlugin.Ant
{
    public partial class DctAnimationAsset : AnimationAsset
    {
        public ushort[] KeyTimes = new ushort[0];
        public byte[] Data = new byte[0];
        public ushort NumKeys;
        public ushort NumVec3;
        public ushort NumFloat;
        public int DataSize;
        public bool Cycle;

        public ushort NumQuats;
        public ushort NumFloatVec;
        public ushort QuantizeMultBlock;
        public byte QuantizeMultSubblock;
        public byte CatchAllBitCount;
        public byte[] DofTableDescBytes;
        public short[] DeltaBaseX;
        public short[] DeltaBaseY;
        public short[] DeltaBaseZ;
        public short[] DeltaBaseW;
        public ushort[] BitsPerSubblock;

        private List<Vector4> DecompressedData = new List<Vector4>();

        public DctAnimationAsset() { }

        public override void SetData(Dictionary<string, object> data)
        {
            object v;
            Name = data.TryGetValue("__name", out v) ? Convert.ToString(v) : "";
            ID = data.TryGetValue("__guid", out v) && v is Guid ? (Guid)v : Guid.Empty;

            KeyTimes = data.TryGetValue("KeyTimes", out v) ? v as ushort[] : new ushort[0];
            if (KeyTimes == null) KeyTimes = new ushort[0];
            Data = data.TryGetValue("Data", out v) ? v as byte[] : new byte[0];
            if (Data == null) Data = new byte[0];
            NumKeys = data.TryGetValue("NumKeys", out v) ? Convert.ToUInt16(v) : (ushort)0;
            NumVec3 = data.TryGetValue("NumVec3", out v) ? Convert.ToUInt16(v) : (ushort)0;
            NumFloat = data.TryGetValue("NumFloat", out v) ? Convert.ToUInt16(v) : (ushort)0;
            DataSize = data.TryGetValue("DataSize", out v) ? Convert.ToInt32(v) : 0;
            Cycle = data.TryGetValue("Cycle", out v) && v is bool ? (bool)v : false;

            NumQuats = data.TryGetValue("NumQuats", out v) ? Convert.ToUInt16(v) : (ushort)0;
            NumFloatVec = data.TryGetValue("NumFloatVec", out v) ? Convert.ToUInt16(v) : (ushort)0;
            QuantizeMultBlock = data.TryGetValue("QuantizeMultBlock", out v) ? Convert.ToUInt16(v) : (ushort)1;
            QuantizeMultSubblock = data.TryGetValue("QuantizeMultSubblock", out v) ? Convert.ToByte(v) : (byte)0;
            CatchAllBitCount = data.TryGetValue("CatchAllBitCount", out v) ? Convert.ToByte(v) : (byte)0;

            DofTableDescBytes = data.TryGetValue("DofTableDescBytes", out v) ? v as byte[] : new byte[0];
            if (DofTableDescBytes == null) DofTableDescBytes = new byte[0];

            DeltaBaseX = data.TryGetValue("DeltaBaseX", out v) ? v as short[] : new short[0];
            if (DeltaBaseX == null) DeltaBaseX = new short[0];
            DeltaBaseY = data.TryGetValue("DeltaBaseY", out v) ? v as short[] : new short[0];
            if (DeltaBaseY == null) DeltaBaseY = new short[0];
            DeltaBaseZ = data.TryGetValue("DeltaBaseZ", out v) ? v as short[] : new short[0];
            if (DeltaBaseZ == null) DeltaBaseZ = new short[0];
            DeltaBaseW = data.TryGetValue("DeltaBaseW", out v) ? v as short[] : new short[0];
            if (DeltaBaseW == null) DeltaBaseW = new short[0];
            BitsPerSubblock = data.TryGetValue("BitsPerSubblock", out v) ? v as ushort[] : new ushort[0];
            if (BitsPerSubblock == null) BitsPerSubblock = new ushort[0];

            // Base AnimationAsset fields
            CodecType = data.TryGetValue("CodecType", out v) ? Convert.ToInt32(v) : 0;
            AnimId = data.TryGetValue("AnimId", out v) ? Convert.ToInt32(v) : 0;
            TrimOffset = data.TryGetValue("TrimOffset", out v) ? Convert.ToSingle(v) : 0f;
            EndFrame = data.TryGetValue("EndFrame", out v) ? Convert.ToUInt16(v) : (ushort)0;
            if (data.TryGetValue("Additive", out v) && v is bool)
                Additive = (bool)v;
            ChannelToDofAsset = data.TryGetValue("ChannelToDofAsset", out v) && v is Guid ? (Guid)v : Guid.Empty;

            // Decompress the animation.
            if (Data.Length > 0 && NumKeys > 0)
            {
                try { DecompressedData = Decompress(); }
                catch { DecompressedData = new List<Vector4>(); }
            }
        }

        public override InternalAnimation ConvertToInternal()
        {
            var ret = new InternalAnimation();

            List<string> posChannels = new List<string>();
            List<string> rotChannels = new List<string>();
            List<string> scaleChannels = new List<string>();

            if (Channels == null)
            {
                ret.Name = Name;
                return ret;
            }

            foreach (var channel in Channels)
            {
                if (channel.Value == BoneChannelType.Rotation)
                    rotChannels.Add(channel.Key);
                else if (channel.Value == BoneChannelType.Position)
                    posChannels.Add(channel.Key);
                else if (channel.Value == BoneChannelType.Scale)
                    scaleChannels.Add(channel.Key);
            }

            var dofCount = NumQuats + NumVec3 + NumFloatVec;

            for (int i = 0; i < KeyTimes.Length; i++)
            {
                Frame frame = new Frame();

                var rotations = new List<Quaternion>();
                var positions = new List<Vector3>();

                for (int channelIdx = 0; channelIdx < NumQuats; channelIdx++)
                {
                    int pos = (int)(i * dofCount + channelIdx);
                    if (pos < DecompressedData.Count)
                    {
                        Vector4 element = DecompressedData[pos];
                        rotations.Add(Quaternion.Normalize(new Quaternion(element.X, element.Y, element.Z, element.W)));
                    }
                    else
                    {
                        rotations.Add(Quaternion.Identity);
                    }
                }
                for (int channelIdx = 0; channelIdx < NumVec3; channelIdx++)
                {
                    if (Channels.ElementAt(NumQuats + channelIdx).Value == BoneChannelType.Position)
                    {
                        int pos = (int)(i * dofCount + NumQuats + channelIdx);
                        if (pos < DecompressedData.Count)
                        {
                            Vector4 element = DecompressedData[pos];
                            positions.Add(new Vector3(element.X, element.Y, element.Z));
                        }
                        else
                        {
                            positions.Add(Vector3.Zero);
                        }
                    }
                }

                frame.Rotations = rotations;
                frame.Positions = positions;

                ret.Frames.Add(frame);
            }

            for (int i = 0; i < KeyTimes.Length; i++)
            {
                Frame f = ret.Frames[i];
                f.FrameIndex = KeyTimes[i];
                ret.Frames[i] = f;
            }

            for (int r = 0; r < rotChannels.Count; r++)
                rotChannels[r] = rotChannels[r].Replace(".q", "");
            for (int r = 0; r < posChannels.Count; r++)
                posChannels[r] = posChannels[r].Replace(".t", "");
            for (int r = 0; r < scaleChannels.Count; r++)
                scaleChannels[r] = scaleChannels[r].Replace(".s", "");

            ret.Name = Name;
            ret.PositionChannels = posChannels;
            ret.RotationChannels = rotChannels;
            ret.Additive = Additive;
            return ret;
        }
    }
}
