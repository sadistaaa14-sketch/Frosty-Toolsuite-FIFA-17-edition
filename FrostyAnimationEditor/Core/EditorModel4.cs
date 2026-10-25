using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using FrostyAnimationEditor.Core.Anim;

namespace FrostyAnimationEditor.Core
{
    /// <summary>tags on a controller's timeline (Tags)</summary>
    public sealed partial class EditorModel
    {
        public static bool HasTagPanel(GdObj o) { return Tags.ControllerPart(o) != null; }

        /// <summary>the controller whose tags an object shows: itself, or the actor a RelativeIk wrapper plays</summary>
        private GdObj TagOwner(object key)
        {
            GdObj o = Root(key);
            if (Tags.ControllerPart(o) == null) throw new ApiError(NameOf(o) + " is not a controller (no ControllerAsset part)");
            return o;
        }

        public JObject TagsView(object key)
        {
            GdObj o = TagOwner(key);
            GdObj cp = Tags.ControllerPart(o);
            double len = Tags.OwnerLength(Db, o);
            JObject r = new JObject();
            r["key"] = H(o.Key);
            r["type"] = o.Type.Name;
            r["name"] = NameOf(o);
            ulong sk = cp.GetKey("TagCollectionSet");
            r["set"] = sk != 0 ? (object)H(sk) : null;
            if (sk != 0)
            {
                int users = Db.RefsTo(sk).Count(i => Tags.ControllerPart(Db.Obj(i)) != null);
                r["set_users"] = (long)users;
            }
            r["length"] = len;
            r["length_known"] = len > 0;
            List<object> cs = new List<object>();
            double maxEnd = 0;
            foreach (TagCollection c in Tags.Load(Db, o))
            {
                JObject jc = new JObject();
                jc["key"] = H(c.Key);
                jc["type"] = c.Type;
                jc["name"] = c.Name;
                jc["has_exit_tags"] = c.HasExitTags;
                jc["monitored"] = (long)c.Monitored;
                List<object> ts = new List<object>();
                foreach (TagEntry e in c.Tags)
                {
                    JObject j = new JObject();
                    j["key"] = H(e.Key);
                    j["type"] = e.Type;
                    j["name"] = e.Name;
                    j["timeline"] = e.Timeline;
                    j["window"] = e.Window;
                    j["start_raw"] = (double)e.Start;
                    j["length_raw"] = (double)e.Length;
                    j["flags"] = (long)e.Flags;
                    j["updates"] = (long)e.Updates;
                    j["normalized"] = e.Normalized;
                    j["starts_at_zero"] = e.StartsAtZero;
                    double s = e.StartTicks(len), l = e.LengthTicks(len);
                    j["start"] = s;
                    j["length"] = l;
                    j["listed"] = (long)e.Listed;
                    j["summary"] = TagSummary(e.Key);
                    if (e.Timeline) maxEnd = Math.Max(maxEnd, s + l);
                    ts.Add(j);
                }
                jc["tags"] = ts;
                cs.Add(jc);
            }
            r["collections"] = cs;
            r["max_end"] = maxEnd;
            return r;
        }

        /// <summary>the tag's own fields (past the timeline parts) as "Field value" text</summary>
        private string TagSummary(ulong key)
        {
            GdObj t = key != 0 ? Db.Get(key) : null;
            if (t == null) return "";
            List<string> parts = new List<string>();
            for (GdObj x = t; x != null; x = x["__base"] as GdObj)
            {
                string tn = x.Type.Name;
                if (tn == "TimelinePointTag" || tn == "TimelineTag") break;
                foreach (Field f in x.Type.Fields)
                {
                    if (f.IsImplicit) continue;
                    object v = x.Values[f.Index];
                    string s;
                    if (v == null) continue;
                    if (f.FType == TypeIds.Key) { ulong k = Num.ToKey(v); if (k == 0) continue; s = Alias(k) ?? (Db.Get(k) != null ? NameOf(Db.Get(k)) : H(k)); }
                    else if (v is IList && !(v is byte[])) { int n = ((IList)v).Count; if (n == 0) continue; s = "[" + n + "]"; }
                    else if (v is GdStruct) continue;
                    else if (v is float) { if ((float)v == 0f) continue; s = Convert.ToString(JsonIO.F((float)v), CultureInfo.InvariantCulture); }
                    else if (v is bool) { if (!(bool)v) continue; s = "true"; }
                    else if (v is string) { if (((string)v).Length == 0) continue; s = (string)v; }
                    else s = Convert.ToString(v, CultureInfo.InvariantCulture);
                    if (s == "0") continue;
                    parts.Add(f.Name + " " + s);
                    if (parts.Count >= 4) break;
                }
                if (parts.Count >= 4) break;
            }
            return string.Join(" · ", parts);
        }

        private GdObj TagColl(object key)
        {
            GdObj c = Root(key);
            GdObj lo;
            string lf;
            if (Tags.KeyList(c, out lo, out lf) == null) throw new ApiError(NameOf(c) + " is not a tag collection");
            return c;
        }

        /// <summary>set a tag's timeline fields: start / length in owner ticks (stored normalized when the tag is)</summary>
        private void SetTagWindow(GdObj tag, double len, JObject a)
        {
            GdObj p = Tags.Part(tag, "TimelinePointTag");
            GdObj w = Tags.Part(tag, "TimelineTag");
            if (p == null) throw new ApiError(NameOf(tag) + " has no timeline part (TimelinePointTag)");
            if (a["flags"] != null)
            {
                long fl = a.Long("flags");
                if (fl < 0 || fl > 0xFFFF) throw new ApiError("LinkFlags is 16 bits");
                p["LinkFlags"] = Coerce.ToField(p.Type.GetField("LinkFlags"), fl);
            }
            bool norm = (p.GetInt("LinkFlags") & 0x40) != 0;
            if (norm && len <= 0 && (a["start"] != null || a["length"] != null))
                throw new ApiError("the tag's times are fractions of the owner's length, which is not known for " + tag.Type.Name + "'s owner: edit start_raw / length_raw");
            if (a["start"] != null)
            {
                double s = Math.Max(0, a.Dbl("start"));
                p["StartLinkOffset"] = (float)(norm ? s / len : s);
            }
            if (a["start_raw"] != null) p["StartLinkOffset"] = (float)a.Dbl("start_raw");
            if (a["length"] != null || a["length_raw"] != null)
            {
                if (w == null) throw new ApiError(tag.Type.Name + " has no length (no TimelineTag part)");
                if (a["length"] != null) { double l = Math.Max(0, a.Dbl("length")); w["EndLinkOffset"] = (float)(norm ? l / len : l); }
                else w["EndLinkOffset"] = (float)a.Dbl("length_raw");
            }
        }

        private void SyncMonitored(GdObj coll)
        {
            if (!coll.Has("MonitoredTags")) return;
            List<ulong> mon = Tags.MonitoredOf(Db, coll);
            Field f = coll.Type.GetField("MonitoredTags");
            coll["MonitoredTags"] = Coerce.ToList(f, mon.Cast<object>().ToList());
        }

        /// <summary>
        /// tag edits. a: owner (the controller), and per op: tag_set (tag, start / length in owner ticks, start_raw /
        /// length_raw, flags), tag_move (tag, delta ticks), tag_add (collection, type or template, start, length),
        /// tag_copy (collection, tag: an own copy in place of a shared tag), tag_remove (collection, tag),
        /// tag_order (collection, tag, to), collection_add (type), set_add (a new set for a controller with none)
        /// </summary>
        public JObject TagOp(string op, JObject a)
        {
            GdObj owner = TagOwner(a.Str("owner"));
            double len = Tags.OwnerLength(Db, owner);
            JObject result = new JObject();
            string label = op.Replace('_', ' ') + " in " + NameOf(owner);
            switch (op)
            {
                case "tag_set":
                case "tag_move":
                    {
                        GdObj tag = Root(a.Str("tag"));
                        Change(label, new[] { tag.Key }, () =>
                        {
                            if (op == "tag_move")
                            {
                                TagEntry e = Tags.Entry(Db, tag.Key);
                                JObject b = new JObject();
                                b["start"] = Math.Max(0, e.StartTicks(len) + a.Dbl("delta"));
                                if (e.StartsAtZero) b["flags"] = (long)(e.Flags & ~0x80);
                                SetTagWindow(tag, len, b);
                            }
                            else SetTagWindow(tag, len, a);
                        });
                        break;
                    }
                case "tag_add":
                    {
                        GdObj coll = TagColl(a.Str("collection"));
                        ulong tplKey = a.Str("template") != null ? K(a.Str("template")) : 0;
                        string type = a.Str("type");
                        if (tplKey == 0 && string.IsNullOrEmpty(type)) throw new ApiError("give a tag type or a tag to copy");
                        // the file's type table types every __base as a plain DataRef: a tag's base chain (MonitoredTag ->
                        // TimelineTag -> TimelinePointTag, ...) is only in the shipped objects, so a new tag copies one
                        if (tplKey == 0)
                        {
                            string first = FirstOfType(type);
                            if (first == null) throw new ApiError("the file has no " + type + " to copy (a new tag is copied from one already in the file)");
                            tplKey = K(first);
                        }
                        ulong newKey = 0;
                        Change(label, new[] { coll.Key }, () =>
                        {
                            GdObj tpl = Db.Get(tplKey);
                            if (tpl == null) throw new ApiError("no object " + H(tplKey));
                            GdObj t = Db.Clone(tpl, true, tpl.Name, true, true);
                            if (Tags.Part(t, "TimelinePointTag") == null) throw new ApiError(t.Type.Name + " is not a timeline tag");
                            JObject w = new JObject();
                            if (a["start"] != null) w["start"] = a.Dbl("start");
                            if (a["length"] != null && Tags.Part(t, "TimelineTag") != null) w["length"] = a.Dbl("length");
                            SetTagWindow(t, len, w);
                            GdObj lo;
                            string lf;
                            IList keys = Tags.KeyList(coll, out lo, out lf);
                            List<object> nk = keys.Cast<object>().ToList();
                            nk.Add(t.Key);
                            lo[lf] = Coerce.ToList(lo.Type.GetField(lf), nk);
                            SyncMonitored(coll);
                            newKey = t.Key;
                        });
                        result["tag"] = H(newKey);
                        break;
                    }
                case "tag_copy":
                    {
                        GdObj coll = TagColl(a.Str("collection"));
                        ulong tk = K(a.Str("tag"));
                        ulong newKey = 0;
                        Change(label, new[] { coll.Key }, () =>
                        {
                            GdObj tpl = Db.Get(tk);
                            GdObj t = Db.Clone(tpl, true, tpl.Name, true, true);
                            GdObj lo;
                            string lf;
                            IList keys = Tags.KeyList(coll, out lo, out lf);
                            List<object> nk = keys.Cast<object>().Select(x => Num.ToKey(x) == tk ? (object)t.Key : x).ToList();
                            lo[lf] = Coerce.ToList(lo.Type.GetField(lf), nk);
                            SyncMonitored(coll);
                            newKey = t.Key;
                        });
                        result["tag"] = H(newKey);
                        break;
                    }
                case "tag_remove":
                    {
                        GdObj coll = TagColl(a.Str("collection"));
                        ulong tk = K(a.Str("tag"));
                        bool deleted = false;
                        Change(label, new[] { coll.Key }, () =>
                        {
                            GdObj lo;
                            string lf;
                            IList keys = Tags.KeyList(coll, out lo, out lf);
                            List<object> nk = keys.Cast<object>().Where(x => Num.ToKey(x) != tk).ToList();
                            if (nk.Count == keys.Count) throw new ApiError("the collection does not list " + H(tk));
                            lo[lf] = Coerce.ToList(lo.Type.GetField(lf), nk);
                            SyncMonitored(coll);
                            // the tag object goes too when nothing else lists it
                            if (Db.RefsTo(tk).Count == 0 && Db.BlockOf(tk) >= 0) { Db.Remove(tk); deleted = true; }
                        });
                        result["deleted"] = deleted;
                        break;
                    }
                case "tag_order":
                    {
                        GdObj coll = TagColl(a.Str("collection"));
                        ulong tk = K(a.Str("tag"));
                        int to = (int)a.Long("to");
                        Change(label, new[] { coll.Key }, () =>
                        {
                            GdObj lo;
                            string lf;
                            IList keys = Tags.KeyList(coll, out lo, out lf);
                            List<object> nk = keys.Cast<object>().ToList();
                            int from = nk.FindIndex(x => Num.ToKey(x) == tk);
                            if (from < 0) throw new ApiError("the collection does not list " + H(tk));
                            object v = nk[from];
                            nk.RemoveAt(from);
                            nk.Insert(Math.Max(0, Math.Min(nk.Count, to)), v);
                            lo[lf] = Coerce.ToList(lo.Type.GetField(lf), nk);
                            SyncMonitored(coll);
                        });
                        break;
                    }
                case "collection_add":
                case "set_add":
                    {
                        GdObj cp = Tags.ControllerPart(owner);
                        ulong sk = cp.GetKey("TagCollectionSet");
                        string ctype = a.Str("type") ?? "DefaultTagCollectionAsset";
                        if (op == "set_add" && sk != 0) throw new ApiError(NameOf(owner) + " already has a tag collection set");
                        if (op == "collection_add" && sk == 0) throw new ApiError(NameOf(owner) + " has no tag collection set");
                        List<ulong> keys = new List<ulong> { owner.Key };
                        if (sk != 0) keys.Add(sk);
                        ulong newColl = 0;
                        Change(label, keys, () =>
                        {
                            GdObj set;
                            if (sk == 0)
                            {
                                set = BlankLikeShipped("TagCollectionSetAsset");
                                if (set.Has("__name")) set["__name"] = "Tag Collection Set";
                                Db.Add(set, owner.Key);
                                cp["TagCollectionSet"] = set.Key;
                            }
                            else set = Db.Get(sk);
                            GdObj coll = BlankLikeShipped(ctype);
                            if (Tags.Part(coll, "TagCollectionAsset") == null && !coll.Has("ImmediateTagKeys")) throw new ApiError(ctype + " is not a tag collection");
                            if (coll.Has("__name")) coll["__name"] = ctype.Replace("Asset", "");
                            Db.Add(coll, set.Key);
                            List<object> cl = ((IList)set["TagCollectionKeys"]).Cast<object>().ToList();
                            cl.Add(coll.Key);
                            set["TagCollectionKeys"] = Coerce.ToList(set.Type.GetField("TagCollectionKeys"), cl);
                            newColl = coll.Key;
                        });
                        result["collection"] = H(newColl);
                        break;
                    }
                default:
                    throw new ApiError("unknown tag op " + op);
            }
            JObject view = TagsView(owner.Key);
            foreach (KeyValuePair<string, object> kv in result) view["op_" + kv.Key] = kv.Value;
            return view;
        }

        /// <summary>
        /// a blank object of a class with the base-class chain its shipped objects have (the type table types every
        /// __base as a plain DataRef, so the chain comes from the first object of the class); BlankObjWithChain when the
        /// file has none
        /// </summary>
        public GdObj BlankLikeShipped(string type)
        {
            string first = FirstOfType(type);
            if (first == null) return BlankObjWithChain(TypeOrError(type));
            return BlankChain(Db.Get(K(first)));
        }

        private GdObj BlankChain(GdObj shape)
        {
            GdObj o = Blank.Obj(Db.Types, shape.Type);
            foreach (Field f in shape.Type.Fields)
                if (f.Name == "__name" || f.Name == "__key") o.Values[f.Index] = f.Name == "__key" ? (object)0UL : (shape.Values[f.Index] == null ? null : (object)"");
            GdObj b = shape["__base"] as GdObj;
            if (shape.Has("__base")) o["__base"] = b != null ? BlankChain(b) : null;
            return o;
        }

        /// <summary>the clip frame a ClipController shows at its tick (false: not a clip controller of that clip)</summary>
        public bool ControllerFrame(string controllerKey, string shownKey, double ticks, out double frame)
        {
            frame = 0;
            GdObj c = Db.Get(K(controllerKey));
            if (c == null || c.Type.Name != "ClipControllerAsset") return false;
            ClipTiming t = Sequence.Timing(Db, c.Key);
            if (t == null) return false;
            ulong ctrl;
            ulong shownClip = ClipKey(shownKey, out ctrl);
            if (shownClip != t.Clip) return false;
            frame = t.Frame(ticks);
            return true;
        }

        /// <summary>the key of the first object of a class (null: none)</summary>
        public string FirstOfType(string type)
        {
            foreach (int i in Db.BlockIds(type))
                if (!Db.Removed.Contains(i)) return H(Db.Keys[i]);
            return null;
        }

        /// <summary>the tag classes that can go on a timeline, with how many the file has</summary>
        public List<object> TagTypes()
        {
            List<object> r = new List<object>();
            foreach (string t in Tags.TimelineTypes(Db))
            {
                JObject j = new JObject();
                j["type"] = t;
                j["count"] = (long)Db.BlockIds(t).Count();
                string first = FirstOfType(t);
                j["window"] = first != null && Tags.Part(Db.Peek(Db.BlockOf(K(first))), "TimelineTag") != null;
                r.Add(j);
            }
            return r;
        }

        private static bool TypeChainHas(TypeDesc t, string name)
        {
            for (TypeDesc x = t; x != null; )
            {
                if (x.Name == name) return true;
                Field b = x.GetField("__base");
                x = b != null ? b.Type : null;
            }
            return false;
        }

        /// <summary>the controllers whose tag collections list a tag (for a tag object's view)</summary>
        public List<object> TagOwners(object key)
        {
            GdObj t = Root(key);
            List<object> r = new List<object>();
            foreach (int ci in Db.RefsTo(t.Key))
            {
                if (!Db.TypeName(ci).EndsWith("TagCollectionAsset", StringComparison.Ordinal)) continue;
                foreach (int si in Db.RefsTo(Db.Keys[ci]))
                {
                    if (Db.TypeName(si) != "TagCollectionSetAsset") continue;
                    foreach (int oi in Db.RefsTo(Db.Keys[si]))
                    {
                        GdObj o = Db.Obj(oi);
                        if (Tags.ControllerPart(o) == null) continue;
                        JObject j = new JObject();
                        j["key"] = H(o.Key);
                        j["name"] = NameOf(o);
                        j["type"] = o.Type.Name;
                        j["collection"] = H(Db.Keys[ci]);
                        r.Add(j);
                    }
                }
            }
            return r;
        }
    }
}
