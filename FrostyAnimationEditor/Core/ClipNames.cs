using System;
using System.Collections.Generic;
using System.Globalization;

namespace FrostyAnimationEditor.Core
{
    /// <summary>
    /// Readable names of animation clips.
    /// In fifagame_win32_antstate every AnimationAsset (13,426 of 13,426) is named "&lt;16 hex digits&gt;.chan", and those
    /// digits are the key of the one ClipControllerAsset whose Anim field points at the clip. The controller carries
    /// the readable name (e.g. "12C0311_UCC_Violin_Gilardino"), so that is the clip's display name.
    /// Clips that do not follow the pattern (added or renamed in the editor) are matched through the Anim field.
    /// </summary>
    public sealed class ClipNames
    {
        private readonly AntDb db;
        private Dictionary<ulong, ulong> byAnim;   // anim key -> controller key (reverse scan, built on demand)

        public ClipNames(AntDb db) { this.db = db; }

        /// <summary>drop the reverse index (after an edit)</summary>
        public void Reset() { byAnim = null; }

        private static bool IsClipType(string t)
        {
            return Array.IndexOf(EditorModel.ClipTypes, t) >= 0 || t == "AnimationAsset";
        }

        /// <summary>the ClipControllerAsset that plays the clip, or 0</summary>
        public ulong Controller(ulong anim)
        {
            int i = db.BlockOf(anim);
            if (i < 0 || db.Removed.Contains(i)) return 0;
            string n = db.NameOf(i);
            ulong ck;
            if (n != null && n.Length == 21 && n.EndsWith(".chan", StringComparison.Ordinal) &&
                ulong.TryParse(n.Substring(0, 16), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out ck))
            {
                int ci = db.BlockOf(ck);
                if (ci >= 0 && !db.Removed.Contains(ci) && db.TypeName(ci) == "ClipControllerAsset" &&
                    Num.ToKey(db.ScalarField(ci, "Anim")) == anim)
                    return ck;
            }
            if (byAnim == null)
            {
                Dictionary<ulong, ulong> m = new Dictionary<ulong, ulong>();
                foreach (int ci in db.BlockIds("ClipControllerAsset"))
                {
                    ulong a = Num.ToKey(db.ScalarField(ci, "Anim"));
                    if (a != 0 && !m.ContainsKey(a)) m[a] = db.Keys[ci];
                }
                byAnim = m;
            }
            ulong r;
            return byAnim.TryGetValue(anim, out r) ? r : 0;
        }

        /// <summary>readable name of a clip (its controller's name), or null when the key is not a clip or has none</summary>
        public string Of(ulong key)
        {
            int i = db.BlockOf(key);
            if (i < 0 || db.Removed.Contains(i) || !IsClipType(db.TypeName(i))) return null;
            ulong ck = Controller(key);
            if (ck == 0) return null;
            string n = db.NameOfKey(ck);
            return string.IsNullOrEmpty(n) ? null : n;
        }

        /// <summary>readable name of a block when it is a clip, else null</summary>
        public string OfBlock(int i)
        {
            if (i < 0 || i >= db.Keys.Count || !IsClipType(db.TypeName(i))) return null;
            return Of(db.Keys[i]);
        }
    }
}
