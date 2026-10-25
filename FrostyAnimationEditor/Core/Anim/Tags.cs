using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;

namespace FrostyAnimationEditor.Core.Anim
{
    /// <summary>a tag (or driver) listed by a tag collection, with its window on the owner's timeline</summary>
    public sealed class TagEntry
    {
        public ulong Key;
        public string Type = "", Name = "";
        public bool Timeline;          // has a TimelinePointTag part (a start)
        public bool Window;            // has a TimelineTag part (a length)
        public float Start, Length;    // StartLinkOffset, EndLinkOffset as stored
        public int Flags;              // LinkFlags
        public int Updates;            // StartLinkUpdates
        public int Listed;             // collections listing the tag (in the whole file)
        public bool Normalized { get { return (Flags & 0x40) != 0; } }
        public bool StartsAtZero { get { return (Flags & 0x80) != 0; } }

        /// <summary>the window in owner ticks: start = 0 with flag 0x80, else
        /// StartLinkOffset; both × the owner's length with flag 0x40</summary>
        public double StartTicks(double ownerLength)
        {
            double s = StartsAtZero ? 0 : Start;
            return Normalized ? s * ownerLength : s;
        }

        public double LengthTicks(double ownerLength) { return Normalized ? Length * ownerLength : Length; }
    }

    public sealed class TagCollection
    {
        public ulong Key;
        public string Type = "", Name = "";
        public bool HasExitTags;
        public List<TagEntry> Tags = new List<TagEntry>();
        public int Monitored;          // DefaultTagCollectionAsset.MonitoredTags count
    }

    /// <summary>
    /// The tags of a controller: ControllerAsset.TagCollectionSet -> TagCollectionSetAsset.TagCollectionKeys -> tag
    /// collections (TagCollectionAsset.TagKeys; DefaultTagCollectionAsset also lists the MonitoredTag ones in
    /// MonitoredTags, in TagKeys order) -> tags. A tag's window: start 0 when LinkFlags has
    /// 0x80, else StartLinkOffset; length EndLinkOffset, both scaled by the owner's length when
    /// LinkFlags has 0x40. Times are the owner controller's ticks.
    /// </summary>
    public static class Tags
    {
        /// <summary>the ControllerAsset part of an object's __base chain (null: not a controller)</summary>
        public static GdObj ControllerPart(GdObj o)
        {
            for (GdObj x = o; x != null; x = x["__base"] as GdObj)
                if (x.Type.Name == "ControllerAsset") return x;
            return null;
        }

        /// <summary>the part of type t in the object's __base chain</summary>
        public static GdObj Part(GdObj o, string t)
        {
            for (GdObj x = o; x != null; x = x["__base"] as GdObj)
                if (x.Type.Name == t) return x;
            return null;
        }

        public static bool IsA(GdObj o, string t) { return Part(o, t) != null; }

        /// <summary>the list of tag keys of a collection: TagCollectionAsset.TagKeys, or ImmediateTagKeys</summary>
        public static IList KeyList(GdObj coll, out GdObj owner, out string field)
        {
            owner = Part(coll, "TagCollectionAsset");
            field = "TagKeys";
            if (owner == null && coll.Has("ImmediateTagKeys")) { owner = coll; field = "ImmediateTagKeys"; }
            return owner != null ? owner[field] as IList : null;
        }

        public static TagEntry Entry(AntDb db, ulong key)
        {
            GdObj t = key != 0 ? db.Get(key) : null;
            TagEntry e = new TagEntry { Key = key };
            if (t == null) { e.Type = "(missing)"; return e; }
            e.Type = t.Type.Name;
            e.Name = t.Name ?? "";
            GdObj p = Part(t, "TimelinePointTag");
            if (p != null)
            {
                e.Timeline = true;
                e.Start = p.GetFloat("StartLinkOffset");
                e.Flags = (int)p.GetInt("LinkFlags");
                e.Updates = (int)p.GetInt("StartLinkUpdates");
            }
            GdObj w = Part(t, "TimelineTag");
            if (w != null) { e.Window = true; e.Length = w.GetFloat("EndLinkOffset"); }
            return e;
        }

        public static List<TagCollection> Load(AntDb db, GdObj owner)
        {
            List<TagCollection> r = new List<TagCollection>();
            GdObj cp = ControllerPart(owner);
            ulong sk = cp != null ? cp.GetKey("TagCollectionSet") : 0;
            GdObj set = sk != 0 ? db.Get(sk) : null;
            if (set == null) return r;
            IList cl = set["TagCollectionKeys"] as IList;
            if (cl == null) return r;
            foreach (object ck in cl)
            {
                ulong k = Num.ToKey(ck);
                GdObj coll = k != 0 ? db.Get(k) : null;
                TagCollection c = new TagCollection { Key = k };
                if (coll == null) { c.Type = "(missing)"; r.Add(c); continue; }
                c.Type = coll.Type.Name;
                c.Name = coll.Name ?? "";
                if (coll.Has("HasExitTags")) c.HasExitTags = coll.GetBool("HasExitTags");
                IList mon = coll.Has("MonitoredTags") ? coll["MonitoredTags"] as IList : null;
                c.Monitored = mon != null ? mon.Count : 0;
                GdObj lo;
                string lf;
                IList keys = KeyList(coll, out lo, out lf);
                if (keys != null)
                    foreach (object tk in keys)
                    {
                        TagEntry e = Entry(db, Num.ToKey(tk));
                        e.Listed = Listed(db, e.Key);
                        c.Tags.Add(e);
                    }
                r.Add(c);
            }
            return r;
        }

        /// <summary>tag collections that list the tag</summary>
        public static int Listed(AntDb db, ulong tag)
        {
            int n = 0;
            foreach (int i in db.RefsTo(tag))
            {
                string tn = db.TypeName(i);
                if (tn.EndsWith("TagCollectionAsset", StringComparison.Ordinal)) n++;
            }
            return n;
        }

        /// <summary>
        /// the owner's length in its ticks: a ClipController's NumTicks, an ActorController's Length; 0 when not
        /// established for the owner type
        /// </summary>
        public static double OwnerLength(AntDb db, GdObj owner)
        {
            if (owner.Type.Name == "ClipControllerAsset")
            {
                ClipTiming t = Sequence.Timing(db, owner.Key);
                return t != null ? t.NumTicks : 0;
            }
            GdObj a = Sequence.ActorOf(db, owner);
            if (a != null) return a.GetFloat("Length");
            return 0;
        }

        /// <summary>DefaultTagCollectionAsset.MonitoredTags = the MonitoredTag tags of TagKeys, in order (as in 18,337
        /// of the 18,354 shipped collections; the rest list them in another order)</summary>
        public static List<ulong> MonitoredOf(AntDb db, GdObj coll)
        {
            List<ulong> r = new List<ulong>();
            GdObj lo;
            string lf;
            IList keys = KeyList(coll, out lo, out lf);
            if (keys == null) return r;
            foreach (object x in keys)
            {
                ulong k = Num.ToKey(x);
                GdObj t = k != 0 ? db.Get(k) : null;
                if (t != null && IsA(t, "MonitoredTag")) r.Add(k);
            }
            return r;
        }

        /// <summary>
        /// the tag classes of the file that have a timeline window: TimelinePointTag in the base chain of their shipped
        /// objects (the type table types every __base as a plain DataRef, so the chain is read from the first object)
        /// </summary>
        public static List<string> TimelineTypes(AntDb db)
        {
            List<string> r = new List<string>();
            foreach (string tn in db.ByType.Keys.ToList())
            {
                int first = -1;
                foreach (int i in db.BlockIds(tn)) { if (!db.Removed.Contains(i)) { first = i; break; } }
                if (first < 0) continue;
                GdObj o = db.Peek(first);
                if (Part(o, "TimelinePointTag") != null) r.Add(tn);
            }
            r.Sort(StringComparer.Ordinal);
            return r;
        }
    }
}
