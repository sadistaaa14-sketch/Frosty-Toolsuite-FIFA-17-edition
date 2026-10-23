using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;

namespace FrostyAnimationEditor.Core.Anim
{
    public sealed class EncodeReport
    {
        public VbrError MaxError;
        public int DataBytes;
        public int DataFloats;
        public override string ToString()
        {
            return MaxError != null ? "VBR, " + DataBytes + " bytes, max error " + MaxError : "RAW, " + DataFloats + " floats";
        }
    }

    /// <summary>High-level animation edits: export a clip's keys, replace a clip's animation, add new clips and
    /// clip controllers, convert codecs.</summary>
    public static class AnimOps
    {
        public static readonly Dictionary<string, string> ClassForCodec = new Dictionary<string, string>
        {
            { "VBR ", "VbrAnimationAsset" }, { "RAW ", "RawAnimationAsset" },
        };

        private static readonly Random rng = new Random();

        /// <summary>optional source of random bits (tests, scripted edits): bits -> value; System.Random when null</summary>
        public static Func<int, ulong> RandomBits;

        private static ulong Bits(int n)
        {
            if (RandomBits != null) return RandomBits(n);
            byte[] rb = new byte[8];
            lock (rng) rng.NextBytes(rb);
            ulong v = BitConverter.ToUInt64(rb, 0);
            return n >= 64 ? v : v & ((1UL << n) - 1);
        }

        /// <summary>ClipData holding the clip's stored keys (no resampling)</summary>
        public static ClipData Data(AntDb db, GdObj clipObj, RigIndex ri, out AnimClip clip)
        {
            clip = new AnimClip(db, clipObj, ri);
            List<long> frames;
            List<float[][]> vals = clip.KeysOnly(out frames);
            return new ClipData(frames, clip.Kinds, vals.Select(ClipData.ToDouble).ToList(), clip.Loops, clip.EndFrame);
        }

        private static GdObj AnimationBase(AntDb db, string codec, long endFrame, ulong c2dKey, ulong layoutKey, bool additive, long? animId, float trimOffset = 0f)
        {
            GdObj b = db.NewObject("AnimationAsset");
            b["CodecType"] = (long)AnimClip.CodecTags[codec];
            b["AnimId"] = animId.HasValue ? animId.Value : (long)Bits(32);
            b["TrimOffset"] = trimOffset;
            b["TrimmedDuration"] = (float)endFrame;
            b["EndFrame"] = endFrame;
            b["ChannelToDofAsset"] = c2dKey;
            b["DofSetList"] = layoutKey;
            b["Additive"] = additive;
            b["__key"] = 0UL;
            b["__name"] = null;
            b["__base"] = null;
            return b;
        }

        /// <summary>-> a VbrAnimationAsset / RawAnimationAsset object and a report</summary>
        public static GdObj BuildCodecObject(AntDb db, ClipData cd, string codec, string name, ulong c2dKey, ulong layoutKey, bool additive,
                                             long? animId, double tolerance, long flagsExtra, bool check, out EncodeReport report)
        {
            string cls;
            if (!ClassForCodec.TryGetValue(codec, out cls)) throw new ArgumentException("can only encode VBR or RAW (got '" + codec + "')");
            GdObj o = db.NewObject(cls);
            report = new EncodeReport();
            Dictionary<string, object> f;
            if (codec == "VBR ")
            {
                f = Encode.EncodeVbr(cd, tolerance, flagsExtra);
                if (check) report.MaxError = Encode.VerifyVbr(f, cd);
                report.DataBytes = ((byte[])f["Data"]).Length;
            }
            else
            {
                f = Encode.EncodeRaw(cd);
                report.DataFloats = ((IList)f["Data"]).Count;
            }
            Coerce.Apply(o, f);
            o["__name"] = name;
            o["__key"] = 0UL;
            o["__base"] = AnimationBase(db, codec, cd.EndFrame, c2dKey, layoutKey, additive, animId);
            return o;
        }

        /// <summary>Replace the animation data of an existing clip (any codec) in place: same __key, __name,
        /// ChannelToDof/DofSetList/AnimId; the clip may change codec class.</summary>
        public static EncodeReport Replace(AntDb db, ulong key, ClipData cd, string codec = "VBR ", double tolerance = Encode.TolDefault, bool keepFlags = true)
        {
            int i = db.BlockOf(key);
            if (i < 0) throw new KeyNotFoundException(BE.KeyHex(key));
            GdObj old = db.Obj(i);
            GdObj bs = old["__base"] as GdObj;
            long extra = 0x100;
            if (keepFlags && old.Type.Name == "VbrAnimationAsset") extra = old.GetInt("Flags") & ~0xFL;
            EncodeReport rep;
            GdObj nw = BuildCodecObject(db, cd, codec, old.Name, bs.GetKey("ChannelToDofAsset"), bs.GetKey("DofSetList"),
                                        bs.GetBool("Additive"), bs.GetInt("AnimId"), tolerance, extra, true, out rep);
            nw["__key"] = key;
            ReplaceRoot(db, i, nw);
            return rep;
        }

        /// <summary>Add a brand-new animation clip (root object); channel map / dof set from a template clip or given.</summary>
        public static ulong Add(AntDb db, ClipData cd, ulong templateKey, string name, ulong c2dKey, ulong layoutKey, string codec, double tolerance,
                                bool? additive, out EncodeReport rep)
        {
            GdObj tpl = templateKey != 0 ? db.Get(templateKey) : null;
            if (tpl != null)
            {
                GdObj b = (GdObj)tpl["__base"];
                if (c2dKey == 0) c2dKey = b.GetKey("ChannelToDofAsset");
                if (layoutKey == 0) layoutKey = b.GetKey("DofSetList");
                if (!additive.HasValue) additive = b.GetBool("Additive");
            }
            if (c2dKey == 0 || layoutKey == 0) throw new ArgumentException("need a template clip or ChannelToDofAsset + LayoutHierarchyAsset keys");
            if (string.IsNullOrEmpty(name))
            {
                name = Bits(64).ToString("x16") + ".chan";
            }
            long extra = tpl != null && tpl.Type.Name == "VbrAnimationAsset" ? tpl.GetInt("Flags") & ~0xFL : 0x100;
            GdObj o = BuildCodecObject(db, cd, codec, name, c2dKey, layoutKey, additive ?? false, null, tolerance, extra, true, out rep);
            db.Add(o, templateKey != 0 ? (ulong?)templateKey : null);
            return o.Key;
        }

        /// <summary>Clone a ClipControllerAsset (and its nested base), point it at animKey.</summary>
        public static ulong AddClipController(AntDb db, ulong templateControllerKey, ulong animKey, string name, IDictionary<string, object> fields = null)
        {
            GdObj tpl = db.Get(templateControllerKey);
            if (tpl == null || tpl.Type.Name != "ClipControllerAsset") throw new ArgumentException("template must be a ClipControllerAsset");
            GdObj c = db.Clone(tpl, true, name);
            c["Anim"] = animKey;
            if (fields != null)
                foreach (KeyValuePair<string, object> kv in fields)
                {
                    if (c.Has(kv.Key)) c[kv.Key] = Coerce.ToField(c.Type.GetField(kv.Key), kv.Value);
                    else
                    {
                        GdObj owner = c.FindOwner(kv.Key);
                        if (owner == null) throw new KeyNotFoundException(kv.Key);
                        owner[kv.Key] = Coerce.ToField(owner.Type.GetField(kv.Key), kv.Value);
                    }
                }
            return c.Key;
        }

        /// <summary>swap the decoded root object of block i (the class may change)</summary>
        public static void ReplaceRoot(AntDb db, int i, GdObj nw)
        {
            if (i >= db.N0) db.NewObjs[i] = nw;
            else db.Cache[i] = nw;
            uint oldT = db.Tid[i];
            if (oldT != nw.Type.Tid)
            {
                string oldName = db.Types[oldT].Name;
                List<int> l;
                if (db.ByType.TryGetValue(oldName, out l)) l.Remove(i);
                if (!db.ByType.TryGetValue(nw.Type.Name, out l)) { l = new List<int>(); db.ByType[nw.Type.Name] = l; }
                l.Add(i);
                db.Tid[i] = nw.Type.Tid;
            }
        }

        /// <summary>ClipControllerAssets whose Anim is animKey (scans the raw field; decodes only the hits)</summary>
        public static List<GdObj> FindControllers(AntDb db, ulong animKey)
        {
            List<GdObj> r = new List<GdObj>();
            foreach (int i in db.BlockIds("ClipControllerAsset"))
                if (Num.ToKey(db.ScalarField(i, "Anim")) == animKey) r.Add(db.Obj(i));
            return r;
        }
    }
}
