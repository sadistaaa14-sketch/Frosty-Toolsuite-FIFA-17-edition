using AssetBankPlugin.Export;
using AssetBankPlugin.GenericData;
using AnimationBrowserPlugin;
using System;
using System.Collections.Generic;
using System.Numerics;

namespace AssetBankPlugin.Ant
{
    public class VbrAnimationAsset : AnimationAsset
    {
        private static readonly Dictionary<int, string> DofBoneMap = new Dictionary<int, string>
        {
            {854,"AITrajectory"},{727,"Spine"},{728,"Spine1"},{729,"Spine2"},{730,"Spine3"},
            {423,"Neck"},{424,"Neck1"},{425,"Head"},{419,"Face"},
            {703,"LeftShoulder"},{704,"LeftArm"},{705,"LeftForeArm"},{706,"LeftHand"},
            {723,"RightShoulder"},{724,"RightArm"},{725,"RightForeArm"},{726,"RightHand"},
            {651,"LeftUpLeg"},{652,"LeftLeg"},{653,"LeftFoot"},{654,"LeftToeBase"},
            {197,"RightUpLeg"},{198,"RightLeg"},{199,"RightFoot"},{200,"RightToeBase"},
            {673,"Prop"},{855,"AITrajectory"},{674,"Prop"},
            {101,"LeftFoot_vel"},{102,"LeftFoot_height"},{103,"LeftToeBase_vel"},{104,"LeftToeBase_height"},
            {201,"RightFoot_vel"},{202,"RightFoot_height"},{203,"RightToeBase_vel"},{204,"RightToeBase_height"},
            {675,"Prop_scale"},{750,"IKEffector"},{792,"IKEffector2"},{421,"AnkleEffectorAux"},
            {731,"LeftHandThumb1"},{732,"LeftHandThumb2"},{733,"LeftHandThumb3"},
            {734,"LeftInHandIndex"},{735,"LeftHandIndex1"},{736,"LeftHandIndex2"},{737,"LeftHandIndex3"},
            {738,"LeftInHandMiddle"},{739,"LeftHandMiddle1"},{740,"LeftHandMiddle2"},{741,"LeftHandMiddle3"},
            {742,"LeftInHandRing"},{743,"LeftHandRing1"},{744,"LeftHandRing2"},{745,"LeftHandRing3"},
            {746,"LeftInHandPinky"},{747,"LeftHandPinky1"},{748,"LeftHandPinky2"},{749,"LeftHandPinky3"},
            {762,"RightHandThumb1"},{763,"RightHandThumb2"},{764,"RightHandThumb3"},
            {765,"RightInHandIndex"},{766,"RightHandIndex1"},{767,"RightHandIndex2"},{768,"RightHandIndex3"},
            {769,"RightInHandMiddle"},{770,"RightHandMiddle1"},{771,"RightHandMiddle2"},{772,"RightHandMiddle3"},
            {773,"RightInHandRing"},{774,"RightHandRing1"},{775,"RightHandRing2"},{776,"RightHandRing3"},
            {777,"RightInHandPinky"},{778,"RightHandPinky1"},{779,"RightHandPinky2"},{780,"RightHandPinky3"},
        };

        private byte[] _data;
        private ushort[] _frameBlockSizes;
        private float[] _constantPalette;
        private float _quatMin, _quatMax, _vec3Min, _vec3Max, _floatMin, _floatMax;
        private float _vecOffScale, _floatOffScale, _dct;
        private ushort _qCount, _v3Count, _fCount, _cqCount, _cv3Count, _cfCount;
        private ushort _ktsSize, _numKeys, _ccmsSize, _cpsSize, _vosSize, _fosSize, _flags;
        private long _ctdKeyInt64; // stored for deferred resolution

        public VbrAnimationAsset() { }

        public override void SetData(Dictionary<string, object> data)
        {
            object v;
            Name = data.TryGetValue("__name", out v) ? Convert.ToString(v) : "";
            ID = data.TryGetValue("__guid", out v) && v is Guid ? (Guid)v : Guid.Empty;
            CodecType = data.TryGetValue("CodecType", out v) ? Convert.ToInt32(v) : 0;
            AnimId = data.TryGetValue("AnimId", out v) ? Convert.ToInt32(v) : 0;
            TrimOffset = data.TryGetValue("TrimOffset", out v) ? Convert.ToSingle(v) : 0f;
            EndFrame = data.TryGetValue("EndFrame", out v) ? Convert.ToUInt16(v) : (ushort)0;
            if (data.TryGetValue("Additive", out v) && v is bool) Additive = (bool)v;
            ChannelToDofAsset = Guid.Empty;

            _data = data.TryGetValue("Data", out v) ? v as byte[] : new byte[0]; if (_data == null) _data = new byte[0];
            _frameBlockSizes = data.TryGetValue("FrameBlockSizes", out v) ? v as ushort[] : new ushort[0]; if (_frameBlockSizes == null) _frameBlockSizes = new ushort[0];
            _constantPalette = data.TryGetValue("ConstantPalette", out v) ? v as float[] : new float[0]; if (_constantPalette == null) _constantPalette = new float[0];

            _quatMin = GF(data,"QuatMin"); _quatMax = GF(data,"QuatMax");
            _vec3Min = GF(data,"Vec3Min"); _vec3Max = GF(data,"Vec3Max");
            _floatMin = GF(data,"FloatMin"); _floatMax = GF(data,"FloatMax");
            _vecOffScale = GF(data,"VectorOffsetScale"); _floatOffScale = GF(data,"FloatOffsetScale");
            _dct = GF(data,"Dct");
            _qCount = GU(data,"QuaternionCount"); _v3Count = GU(data,"Vector3Count"); _fCount = GU(data,"FloatCount");
            _cqCount = GU(data,"ConstQuaternionCount"); _cv3Count = GU(data,"ConstVector3Count"); _cfCount = GU(data,"ConstFloatCount");
            _ktsSize = GU(data,"KeyTimeSize"); _numKeys = GU(data,"NumKeys");
            _ccmsSize = GU(data,"ConstChanMapSize"); _cpsSize = GU(data,"ConstPaletteSize");
            _vosSize = GU(data,"VectorOffsetSize"); _fosSize = GU(data,"FloatOffsetSize");
            _flags = GU(data,"Flags");

            // Store CTD Int64 key for deferred resolution
            _ctdKeyInt64 = 0;
            if (data.TryGetValue("ChannelToDofAsset", out v) && v is Dictionary<string, object>)
            {
                var d = (Dictionary<string, object>)v;
                object d1; if (d.TryGetValue("Data1", out d1)) try { _ctdKeyInt64 = Convert.ToInt64(d1); } catch { }
            }
        }

        private ushort[] ResolveDofIds()
        {
            if (_ctdKeyInt64 == 0) return null;
            try
            {
                AntAsset ctdAsset;
                if (Bank.AssetsByKey.TryGetValue(_ctdKeyInt64, out ctdAsset) && ctdAsset is ChannelToDofAsset ctd && ctd.IndexData != null)
                {
                    ushort[] ids = new ushort[ctd.IndexData.Length];
                    for (int i = 0; i < ctd.IndexData.Length; i++) ids[i] = (ushort)ctd.IndexData[i];
                    return ids;
                }
            }
            catch { }
            return null;
        }

        public override InternalAnimation ConvertToInternal()
        {
            if (_data.Length == 0 || _frameBlockSizes.Length == 0 || _numKeys == 0)
                return new InternalAnimation() { Name = Name };

            VbrAnimation vbr = new VbrAnimation();
            vbr.DataBlob=_data; vbr.FrameBlockSizes=_frameBlockSizes; vbr.ConstPalette=_constantPalette;
            vbr.QuatMin=_quatMin; vbr.QuatMax=_quatMax; vbr.Vec3Min=_vec3Min; vbr.Vec3Max=_vec3Max;
            vbr.FloatMin=_floatMin; vbr.FloatMax=_floatMax;
            vbr.VecOffScale=_vecOffScale; vbr.FloatOffScale=_floatOffScale; vbr.Dct=_dct;
            vbr.Q=_qCount; vbr.V3=_v3Count; vbr.F=_fCount;
            vbr.CQ=_cqCount; vbr.CV3=_cv3Count; vbr.CF=_cfCount;
            vbr.KtsSize=_ktsSize; vbr.NumKeys=_numKeys;
            vbr.CcmsSize=_ccmsSize; vbr.ConstPaletteSize=_cpsSize;
            vbr.VosSize=_vosSize; vbr.FosSize=_fosSize; vbr.Flags=_flags;
            vbr.Initialize();

            float[][] raw = vbr.DecodeAll();
            int nk = _numKeys;
            ushort[] dofIds = ResolveDofIds(); // deferred resolution — Bank fully parsed now

            List<string> rotCh = new List<string>();
            List<string> posCh = new List<string>();
            int di = 0;
            for (int qi = 0; qi < _qCount + _cqCount; qi++)
            {
                string bone = "Quat_" + qi;
                if (dofIds != null && di < dofIds.Length)
                { string m; if (DofBoneMap.TryGetValue(dofIds[di], out m)) bone = m; else bone = "DofId_" + dofIds[di]; }
                rotCh.Add(bone); di++;
            }
            for (int vi = 0; vi < _v3Count + _cv3Count; vi++)
            {
                string bone = "Vec3_" + vi;
                if (dofIds != null && di < dofIds.Length)
                { string m; if (DofBoneMap.TryGetValue(dofIds[di], out m)) bone = m; else bone = "DofId_" + dofIds[di]; }
                posCh.Add(bone); di++;
            }

            InternalAnimation ret = new InternalAnimation();
            ret.Name=Name; ret.RotationChannels=rotCh; ret.PositionChannels=posCh; ret.Additive=Additive;

            bool rs = vbr.FramePositions!=null && vbr.TotalFrames>nk;
            // Trim to actual animation range (skip idle frames before first KTS position)
            int frameStart = 0;
            int frameEnd = vbr.TotalFrames;
            if (rs && vbr.FramePositions != null && vbr.FramePositions.Length > 0)
            { frameStart = vbr.FramePositions[0]; frameEnd = vbr.FramePositions[vbr.FramePositions.Length - 1] + 1; }

            try { System.IO.File.AppendAllText(
                System.IO.Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.Desktop), "frosty_anim_export.log"),
                DateTime.Now.ToString("HH:mm:ss.fff") + " |   [VBR] " + Name + " NK=" + nk + " TotalF=" + vbr.TotalFrames
                + " KTS=" + (vbr.FramePositions != null ? vbr.FramePositions.Length.ToString() : "null")
                + " fp0=" + (vbr.FramePositions != null && vbr.FramePositions.Length > 0 ? vbr.FramePositions[0].ToString() : "?")
                + " fpLast=" + (vbr.FramePositions != null && vbr.FramePositions.Length > 0 ? vbr.FramePositions[vbr.FramePositions.Length-1].ToString() : "?")
                + " → export " + frameStart + ".." + frameEnd + " (" + (frameEnd - frameStart) + "f)"
                + " rotCh=" + rotCh.Count + " posCh=" + posCh.Count
                + System.Environment.NewLine); } catch { }

            for (int frame = frameStart; frame < frameEnd; frame++)
            {
                Frame f = new Frame(); f.FrameIndex = frame - frameStart;
                float kf = rs ? MFK(frame,vbr.FramePositions) : frame;
                int kL=Math.Max(0,Math.Min((int)kf,nk-1)), kH=Math.Min(kL+1,nk-1); float t=kf-kL;

                for (int qi=0;qi<_qCount;qi++)
                { int b=qi*4; float w=L(raw[b][kL],raw[b][kH],t),x=L(raw[b+1][kL],raw[b+1][kH],t),y=L(raw[b+2][kL],raw[b+2][kH],t),z=L(raw[b+3][kL],raw[b+3][kH],t);
                  float n=(float)Math.Sqrt(w*w+x*x+y*y+z*z);if(n>1e-10f){w/=n;x/=n;y/=n;z/=n;}else{w=1;x=y=z=0;}
                  f.Rotations.Add(new Quaternion(x,y,z,w)); }
                for (int qi=0;qi<_cqCount;qi++)
                { float[] c=(qi<vbr.ConstQuats.Length)?vbr.ConstQuats[qi]:new float[]{1,0,0,0}; f.Rotations.Add(new Quaternion(c[1],c[2],c[3],c[0])); }

                int vB=_qCount*4;
                for (int vi=0;vi<_v3Count;vi++)
                { int b=vB+vi*3; f.Positions.Add(new Vector3(L(raw[b][kL],raw[b][kH],t),L(raw[b+1][kL],raw[b+1][kH],t),L(raw[b+2][kL],raw[b+2][kH],t))); }
                for (int vi=0;vi<_cv3Count;vi++)
                { float[] c=(vi<vbr.ConstVec3s.Length)?vbr.ConstVec3s[vi]:new float[]{0,0,0}; f.Positions.Add(new Vector3(c[0],c[1],c[2])); }

                ret.Frames.Add(f);
            }
            return ret;
        }

        static float MFK(int f,int[] fp){if(fp==null||fp.Length==0)return f;if(f<=fp[0])return 0;if(f>=fp[fp.Length-1])return fp.Length-1;int lo=0,hi=fp.Length-1;while(lo<hi-1){int m=(lo+hi)/2;if(fp[m]<=f)lo=m;else hi=m;}float s=fp[hi]-fp[lo];return s>0?lo+(float)(f-fp[lo])/s:lo;}
        static float L(float a,float b,float t){return a+t*(b-a);}
        static float GF(Dictionary<string,object> d,string k){object v;if(d.TryGetValue(k,out v)){if(v is float)return(float)v;try{return Convert.ToSingle(v);}catch{}}return 0f;}
        static ushort GU(Dictionary<string,object> d,string k){object v;if(d.TryGetValue(k,out v)){if(v is ushort)return(ushort)v;try{return Convert.ToUInt16(v);}catch{}}return 0;}
    }
}
