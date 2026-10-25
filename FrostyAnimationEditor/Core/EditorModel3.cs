using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using FrostyAnimationEditor.Core.Anim;

namespace FrostyAnimationEditor.Core
{
    public sealed partial class EditorModel
    {
        // ------------------------------------------------------------------ sequences
        /// <summary>the Sequence view: the actors of a SequenceContainerAsset (or one ActorControllerAsset), their tracks
        /// and entries with the timing the game uses (Sequence)</summary>
        public JObject Seq(object key)
        {
            GdObj o = Root(key);
            if (!Sequence.IsSequenceType(o.Type.Name)) throw new ApiError(NameOf(o) + " is not a sequence (SequenceContainerAsset / ActorControllerAsset)");
            List<SeqActor> actors = Sequence.Actors(Db, o.Key);
            Dictionary<ulong, ClipTiming> timing = new Dictionary<ulong, ClipTiming>();
            JObject r = new JObject();
            r["key"] = H(o.Key);
            r["type"] = o.Type.Name;
            r["name"] = NameOf(o);
            List<object> ja = new List<object>();
            foreach (SeqActor a in actors) ja.Add(SeqActorJson(a, timing));
            r["actors"] = ja;
            List<object> containers = new List<object>();
            if (o.Type.Name != "SequenceContainerAsset")
            {
                // the containers that list this actor, directly or through a RelativeIkActorControllerAsset wrapping it
                List<ulong> holders = new List<ulong> { o.Key };
                foreach (SeqActor a in actors)
                {
                    if (!holders.Contains(a.Key)) holders.Add(a.Key);
                    foreach (int i in Db.RefsTo(a.Key))
                        if (!Db.Removed.Contains(i) && Db.TypeName(i) == "RelativeIkActorControllerAsset" && !holders.Contains(Db.Keys[i])) holders.Add(Db.Keys[i]);
                }
                HashSet<int> seenC = new HashSet<int>();
                foreach (ulong hk in holders)
                    foreach (int i in Db.RefsTo(hk))
                        if (!Db.Removed.Contains(i) && Db.TypeName(i) == "SequenceContainerAsset" && seenC.Add(i))
                        {
                            JObject c = new JObject();
                            c["key"] = H(Db.Keys[i]); c["name"] = Db.NameOf(i);
                            containers.Add(c);
                        }
            }
            r["containers"] = containers;
            r["ticks_per_second"] = 60L;
            return r;
        }

        /// <summary>the RelativeIkActorControllerAssets that wrap the actor, with their entries</summary>
        private List<object> RelativeIkOf(ulong actorKey)
        {
            List<object> r = new List<object>();
            foreach (int i in Db.RefsTo(actorKey))
            {
                if (Db.Removed.Contains(i) || Db.TypeName(i) != "RelativeIkActorControllerAsset") continue;
                GdObj w = Db.Obj(i);
                if (w.GetKey("ActorControllerAsset") != actorKey) continue;
                JObject jw = new JObject();
                jw["key"] = H(w.Key);
                jw["name"] = NameOf(w);
                List<object> es = new List<object>();
                IList l = w["Entries"] as IList;
                if (l != null)
                    foreach (object x in l)
                    {
                        GdObj e = Db.Get(Num.ToKey(x));
                        if (e == null) continue;
                        JObject je = new JObject();
                        je["key"] = H(e.Key);
                        je["effector"] = e.GetInt("IKJointEffectorID");
                        je["mirrored_effector"] = e.GetInt("MirroredIKJointEffectorID");
                        ulong tgt = e.GetKey("TargetAnimatableId");
                        je["target"] = tgt != 0 ? (object)H(tgt) : null;
                        je["target_name"] = tgt != 0 && Db.Get(tgt) != null ? NameOf(Db.Get(tgt)) : null;
                        IList tl = e["RelTranslationList"] as IList, rl = e["RelRotationList"] as IList;
                        je["translation_keys"] = (long)(tl != null ? tl.Count : 0);
                        je["translation_offset"] = e.GetInt("RelTranslationOffset");
                        je["rotation_keys"] = (long)(rl != null ? rl.Count : 0);
                        je["rotation_offset"] = e.GetInt("RelRotationOffset");
                        je["condition"] = e.GetInt("ActiveCondition");
                        es.Add(je);
                    }
                jw["entries"] = es;
                r.Add(jw);
            }
            return r;
        }

        private JObject SeqActorJson(SeqActor a, Dictionary<ulong, ClipTiming> timing)
        {
            JObject j = new JObject();
            j["key"] = H(a.Key);
            j["name"] = a.Name;
            j["relative_ik"] = RelativeIkOf(a.Key);
            j["length"] = (double)a.Length;
            j["loop_point"] = (double)a.LoopPoint;
            j["pass_tags"] = a.PassTags;
            j["snap"] = a.Snap;
            j["content_flags"] = a.ContentFlags;
            JObject init = new JObject();
            init["x"] = (double)a.X; init["y"] = (double)a.Y; init["z"] = (double)a.Z; init["facing"] = (double)a.Facing;
            init["scale"] = (double)a.Scale; init["mirror"] = a.Mirror; init["start_tick"] = (double)a.StartTick; init["present"] = a.HasInit;
            j["init"] = init;
            List<object> tracks = new List<object>();
            foreach (SeqTrack t in a.Tracks)
            {
                JObject jt = new JObject();
                jt["index"] = (long)t.Index;
                jt["target"] = t.Target != 0 ? (object)H(t.Target) : null;
                jt["warpers"] = (long)t.Warpers;
                jt["end"] = (long)t.End;
                List<object> es = new List<object>();
                foreach (SeqEntry e in t.Entries)
                {
                    JObject je = new JObject();
                    je["index"] = (long)e.Index;
                    je["controller"] = e.Controller != 0 ? (object)H(e.Controller) : null;
                    je["controller_type"] = e.ControllerType;
                    je["label"] = e.Controller != 0 ? (Alias(e.Controller) ?? NameOf(Db.Get(e.Controller))) : "(none)";
                    je["blend"] = e.Blend != 0 ? (object)H(e.Blend) : null;
                    je["blend_type"] = e.BlendType;
                    je["blend_time"] = (double)e.BlendTime;
                    je["curve"] = (long)e.CurveType;
                    je["sync_phase"] = e.SyncPhase;
                    je["blend_users"] = e.Blend != 0 ? (long)Db.RefsTo(e.Blend).Count(i => !Db.Removed.Contains(i)) : 0L;
                    je["init"] = e.InitParams != 0 ? (object)H(e.InitParams) : null;
                    je["start_tick"] = (double)e.StartTick;
                    je["flags"] = (long)e.Flags;
                    je["mirror"] = e.Mirror;
                    je["start_time"] = (long)e.StartTime;
                    je["start_in"] = (long)e.StartInTime;
                    je["end_in"] = (long)e.EndInTime;
                    je["scale"] = (double)e.Scale;
                    je["begin"] = (long)e.Begin;
                    je["end"] = (long)e.End;
                    ClipTiming ct;
                    if (!timing.TryGetValue(e.Controller, out ct)) { ct = e.Controller != 0 ? Sequence.Timing(Db, e.Controller) : null; timing[e.Controller] = ct; }
                    if (ct != null)
                    {
                        JObject c = new JObject();
                        c["key"] = ct.Clip != 0 ? (object)H(ct.Clip) : null;
                        c["frames"] = ct.EndFrame + 1;
                        c["trim_offset"] = (double)ct.TrimOffset;
                        c["trimmed_duration"] = (double)ct.TrimmedDuration;
                        c["time_scale"] = (double)ct.TimeScale;
                        c["num_ticks"] = ct.NumTicks;
                        c["looping"] = ct.Looping;
                        double t0 = e.StartTick + e.StartInTime * (double)e.Scale, t1 = e.StartTick + e.EndInTime * (double)e.Scale;
                        c["first_frame"] = ct.Frame(t0);
                        c["last_frame"] = ct.Frame(t1);
                        c["past_end"] = !ct.Looping && (t1 > ct.NumTicks + 0.5 || ct.Frame(t1) > ct.EndFrame + 0.5);
                        je["clip"] = c;
                    }
                    es.Add(je);
                }
                jt["entries"] = es;
                tracks.Add(jt);
            }
            j["tracks"] = tracks;
            return j;
        }

        private GdObj SeqActorObj(object actorKey)
        {
            GdObj a = Root(actorKey);
            if (!a.Has("Tracks")) throw new ApiError(NameOf(a) + " is not an ActorControllerAsset");
            return a;
        }

        private static IList TrackList(GdObj a)
        {
            IList l = a["Tracks"] as IList;
            if (l == null) throw new ApiError("the actor has no tracks");
            return l;
        }

        private static GdStruct TrackAt(GdObj a, int track)
        {
            IList l = TrackList(a);
            if (track < 0 || track >= l.Count) throw new ApiError("track " + track + " out of range");
            GdStruct t = l[track] as GdStruct;
            if (t == null) throw new ApiError("track " + track + " is empty");
            return t;
        }

        private static GdStruct EntryAt(GdObj a, int track, int index)
        {
            GdStruct t = TrackAt(a, track);
            IList l = t["Anims"] as IList;
            if (l == null || index < 0 || index >= l.Count) throw new ApiError("entry " + index + " of track " + track + " out of range");
            GdStruct e = l[index] as GdStruct;
            if (e == null) throw new ApiError("entry " + index + " is empty");
            return e;
        }

        private static void SetField(GdStruct s, string field, object value)
        {
            Field f = s.Type.GetField(field);
            if (f == null) throw new ApiError(s.Type.Name + " has no field " + field);
            s[field] = Coerce.ToField(f, value);
        }

        private static void SetList(GdStruct s, string field, List<object> items, string countField)
        {
            Field f = s.Type.GetField(field);
            s[field] = Coerce.ToList(f, items);
            if (countField != null && s.Has(countField)) SetField(s, countField, (long)items.Count);
        }

        private static readonly HashSet<string> EntryFields = new HashSet<string> { "StartTime", "StartInTime", "EndInTime", "Scale", "Flags", "Asset", "BlendAsset", "InitParams" };
        private static readonly HashSet<string> ActorFields = new HashSet<string> { "Length", "LoopPoint", "PassTagsToChildren", "SnapToStartLocation", "ActorContentFlags" };
        private static readonly HashSet<string> InitFields = new HashSet<string> { "X", "Y", "Z", "FacingAngle", "Scale", "Mirror", "StartTick" };

        /// <summary>
        /// Sequence edits (one undoable change each): entry_set {actor, track, index, field, value}; entry_move {…, delta}
        /// (StartTime += delta); entry_trim {…, start_in, end_in}; entry_add {actor, track, index (insert after; -1 = first),
        /// controller}; entry_remove; entry_order {…, to}; track_add; track_remove {track}; actor_set {field, value};
        /// init_set {field, value}; fit {actor} (Length = the longest track); blend_set {…, time, curve, own}.
        /// </summary>
        public JObject SeqOp(string op, JObject a)
        {
            string actor = a.Str("actor");
            GdObj ac = SeqActorObj(actor);
            int track = (int)a.Long("track", 0), index = (int)a.Long("index", 0);
            string label = op.Replace('_', ' ') + " in " + NameOf(ac);
            List<ulong> keys = new List<ulong> { ac.Key };
            JObject result = new JObject();
            if (op == "blend_set")
            {
                GdStruct e0 = EntryAt(ac, track, index);
                ulong bk = Num.ToKey(e0["BlendAsset"]);
                bool own = a.Bool("own", false);
                if (bk != 0 && !own) keys.Add(bk);
                Change("blend of " + NameOf(ac) + " track " + track + " entry " + index, keys, () =>
                {
                    GdObj b = bk != 0 ? Db.Get(bk) : null;
                    if (b == null || own)
                    {
                        // a new BlendCurveAsset for this entry only (a copy of the current blend when it is one)
                        GdObj nb;
                        if (b != null && b.Type.Name == "BlendCurveAsset") nb = Db.Clone(b, true, "Blend Curve");
                        else
                        {
                            nb = BlankObjWithChain(TypeOrError("BlendCurveAsset"));
                            nb["__name"] = "Blend Curve";
                            SetField(nb, "CurveType", 3L);
                            Db.Add(nb, null);
                        }
                        b = nb;
                        SetField(e0, "BlendAsset", b.Key);
                    }
                    GdObj owner = b;
                    while (owner != null && !owner.Has("BlendTime")) owner = owner["__base"] as GdObj;
                    if (a["time"] != null && owner != null) SetField(owner, "BlendTime", a.Dbl("time"));
                    if (a["curve"] != null)
                    {
                        if (!b.Has("CurveType")) throw new ApiError(b.Type.Name + " has no curve type");
                        long ct = a.Long("curve");
                        if (ct < 0 || ct > 3) throw new ApiError("the curve type is 0 to 3");
                        SetField(b, "CurveType", ct);
                    }
                    result["blend"] = H(b.Key);
                });
                return Seq(a.Str("view") ?? actor);
            }
            Change(label, keys, () =>
            {
                switch (op)
                {
                    case "entry_set":
                        {
                            string field = a.Str("field");
                            if (!EntryFields.Contains(field)) throw new ApiError("not an entry field: " + field);
                            GdStruct e = EntryAt(ac, track, index);
                            object v = a["value"];
                            if (field == "Asset" || field == "BlendAsset" || field == "InitParams") v = v == null || (v is string && ((string)v).Length == 0) ? 0UL : (object)K(v);
                            SetField(e, field, v);
                            break;
                        }
                    case "entry_move":
                        {
                            GdStruct e = EntryAt(ac, track, index);
                            SetField(e, "StartTime", Num.ToLong(e["StartTime"]) + a.Long("delta"));
                            break;
                        }
                    case "entry_trim":
                        {
                            GdStruct e = EntryAt(ac, track, index);
                            long si = a["start_in"] != null ? a.Long("start_in") : Num.ToLong(e["StartInTime"]);
                            long ei = a["end_in"] != null ? a.Long("end_in") : Num.ToLong(e["EndInTime"]);
                            if (ei < si) throw new ApiError("the end in-time is before the start in-time");
                            SetField(e, "StartInTime", si);
                            SetField(e, "EndInTime", ei);
                            break;
                        }
                    case "entry_add":
                        {
                            GdStruct t = TrackAt(ac, track);
                            List<object> l = ((t["Anims"] as IList) ?? new List<object>()).Cast<object>().ToList();
                            ulong ctrl = a["controller"] != null ? K(a["controller"]) : 0;
                            if (ctrl == 0) throw new ApiError("choose the controller the entry plays");
                            int after = Math.Max(-1, Math.Min(l.Count - 1, index));
                            GdStruct tpl = l.Count > 0 ? l[after >= 0 ? after : 0] as GdStruct : null;
                            GdStruct ne = tpl != null ? (GdStruct)DeepCopy.Value(tpl) : BlankObjWithChain(TypeOrError("SequenceAnimation"));
                            SetField(ne, "Asset", ctrl);
                            SetField(ne, "Flags", 0L);
                            SetField(ne, "Scale", 1.0);
                            ClipTiming ct = Sequence.Timing(Db, ctrl);
                            long len = ct != null ? (long)Math.Ceiling(ct.NumTicks) : 60;
                            float bt = 0;
                            ulong bk = Num.ToKey(ne["BlendAsset"]);
                            GdObj b = bk != 0 ? Db.Get(bk) : null;
                            while (b != null && !b.Has("BlendTime")) b = b["__base"] as GdObj;
                            if (b != null) bt = b.GetFloat("BlendTime");
                            // begin where the previous entry ends minus the blend (as the shipped sequences are cut)
                            long begin = 0;
                            if (after >= 0 && l[after] is GdStruct)
                            {
                                GdStruct prev = (GdStruct)l[after];
                                begin = Num.ToLong(prev["StartTime"]) + Num.ToLong(prev["EndInTime"]) - (long)Math.Round(bt);
                                if (begin < 0) begin = 0;
                            }
                            SetField(ne, "StartInTime", 0L);
                            SetField(ne, "EndInTime", len);
                            SetField(ne, "StartTime", begin);
                            l.Insert(after + 1, ne);
                            SetList(t, "Anims", l, "NumAnims");
                            result["index"] = (long)(after + 1);
                            break;
                        }
                    case "entry_remove":
                        {
                            GdStruct t = TrackAt(ac, track);
                            List<object> l = ((t["Anims"] as IList) ?? new List<object>()).Cast<object>().ToList();
                            if (index < 0 || index >= l.Count) throw new ApiError("entry " + index + " out of range");
                            l.RemoveAt(index);
                            SetList(t, "Anims", l, "NumAnims");
                            break;
                        }
                    case "entry_order":
                        {
                            GdStruct t = TrackAt(ac, track);
                            List<object> l = ((t["Anims"] as IList) ?? new List<object>()).Cast<object>().ToList();
                            int to = (int)a.Long("to");
                            if (index < 0 || index >= l.Count || to < 0 || to >= l.Count) throw new ApiError("index out of range");
                            object x = l[index];
                            l.RemoveAt(index);
                            l.Insert(to, x);
                            SetList(t, "Anims", l, "NumAnims");
                            break;
                        }
                    case "track_add":
                        {
                            List<object> l = TrackList(ac).Cast<object>().ToList();
                            GdStruct nt = BlankObjWithChain(TypeOrError("SequenceAnimTrack"));
                            Field af = nt.Type.GetField("Anims"), wf = nt.Type.GetField("WarpersParam");
                            nt["Anims"] = ObjCodec.EmptyList(af);
                            nt["WarpersParam"] = ObjCodec.EmptyList(wf);
                            SetField(nt, "NumAnims", 0L);
                            SetField(nt, "NumWarpers", 0L);
                            l.Add(nt);
                            SetList(ac, "Tracks", l, "NumTracks");
                            result["track"] = (long)(l.Count - 1);
                            break;
                        }
                    case "track_remove":
                        {
                            List<object> l = TrackList(ac).Cast<object>().ToList();
                            if (track < 0 || track >= l.Count) throw new ApiError("track " + track + " out of range");
                            l.RemoveAt(track);
                            SetList(ac, "Tracks", l, "NumTracks");
                            break;
                        }
                    case "actor_set":
                        {
                            string field = a.Str("field");
                            if (!ActorFields.Contains(field)) throw new ApiError("not an actor field: " + field);
                            SetField(ac, field, a["value"]);
                            break;
                        }
                    case "init_set":
                        {
                            string field = a.Str("field");
                            if (!InitFields.Contains(field)) throw new ApiError("not an init field: " + field);
                            GdStruct ip = ac["DefaultInitParam"] as GdStruct;
                            if (ip == null) throw new ApiError("the actor has no DefaultInitParam");
                            SetField(ip, field, a["value"]);
                            break;
                        }
                    case "fit":
                        {
                            SeqActor sa = Sequence.LoadActor(Db, ac);
                            int len = sa.Tracks.Count > 0 ? sa.Tracks.Max(t => t.End) : 0;
                            SetField(ac, "Length", (double)len);
                            result["length"] = (long)len;
                            break;
                        }
                    default:
                        throw new ApiError("unknown sequence operation " + op);
                }
            });
            JObject v2 = Seq(a.Str("view") ?? actor);
            foreach (KeyValuePair<string, object> kv in result) v2["op_" + kv.Key] = kv.Value;
            return v2;
        }

        /// <summary>the sequence (all its actors) evaluated on the player skeleton for the player view</summary>
        public Sequence.Baked SeqOnSkeleton(object key, double ticksPerFrame = 2.0)
        {
            GdObj o = Root(key);
            List<SeqActor> actors = Sequence.Actors(Db, o.Key);
            if (actors.Count == 0) throw new ApiError(NameOf(o) + " has no actor");
            PlayerSkeleton sk = RenderSkeleton();
            if (sk == null)
            {
                // no project skeleton: the skeleton of the first clip the sequence plays
                foreach (SeqActor a in actors)
                    foreach (SeqTrack t in a.Tracks)
                        foreach (SeqEntry e in t.Entries)
                        {
                            ClipTiming ct = Sequence.Timing(Db, e.Controller);
                            if (sk == null && ct != null && ct.Clip != 0) { string why; sk = SkeletonFor(H(ct.Clip), out why); }
                        }
                if (sk == null) throw new ApiError("no skeleton to show the sequence on");
            }
            PlayerSkeleton skel = sk;
            Sequence.Baked b = Sequence.Bake(Db, skel, actors, ticksPerFrame, clip =>
            {
                try { return PlayerAnimation.FromClip(Db, clip, Rig(), skel, ClipFps(clip)); }
                catch (Exception ex) when (!(ex is OutOfMemoryException)) { return null; }
            });
            b.Anim.ClipKey = o.Key;
            b.Anim.ClipName = Alias(o.Key) ?? NameOf(o);
            return b;
        }

        /// <summary>the sequence as an FBX file (skeleton, one key per frame; the meshes when given)</summary>
        public Tuple<string, byte[]> SeqFbx(object key, PlayerAnimation baked = null, IList<MeshPart> meshes = null)
        {
            GdObj o = Root(key);
            PlayerAnimation a = baked ?? SeqOnSkeleton(key).Anim;
            string alias = Alias(o.Key);
            string name = Unsafe.Replace(alias ?? (string.IsNullOrEmpty(o.Name) ? H(o.Key) : o.Name), "_");
            byte[] fbx = Fbx.FbxExport.Write(a, meshes, alias ?? name);
            return Tuple.Create(name + ".fbx", fbx);
        }

        /// <summary>the sequence entries that play the controller (or the clip's controllers)</summary>
        public List<object> SeqUses(object key)
        {
            ulong ctrl;
            ulong k = Root(key).Key;
            List<ulong> ctrls = new List<ulong>();
            GdObj o = Db.Get(k);
            if (o.Type.Name == "ClipControllerAsset") ctrls.Add(k);
            else
            {
                ulong ck = ClipKey(key, out ctrl);
                ctrls.AddRange(AnimOps.FindControllers(Db, ck).Select(c => c.Key));
            }
            List<object> r = new List<object>();
            foreach (ulong c in ctrls)
            {
                ClipTiming ct = Sequence.Timing(Db, c);
                foreach (Sequence.Use u in Sequence.UsesOf(Db, c))
                {
                    JObject j = new JObject();
                    j["actor"] = H(u.Actor.Key);
                    j["actor_name"] = u.Actor.Name;
                    j["track"] = (long)u.Entry.Track;
                    j["index"] = (long)u.Entry.Index;
                    j["begin"] = (long)u.Entry.Begin;
                    j["end"] = (long)u.Entry.End;
                    j["start_in"] = (long)u.Entry.StartInTime;
                    j["end_in"] = (long)u.Entry.EndInTime;
                    if (ct != null)
                    {
                        double t0 = u.Entry.StartTick + u.Entry.StartInTime * (double)u.Entry.Scale, t1 = u.Entry.StartTick + u.Entry.EndInTime * (double)u.Entry.Scale;
                        j["first_frame"] = ct.Frame(t0);
                        j["last_frame"] = ct.Frame(t1);
                        j["past_end"] = !ct.Looping && ct.Frame(t1) > ct.EndFrame + 0.5;
                    }
                    r.Add(j);
                }
            }
            return r;
        }

        private static string F1(double v) { return v.ToString("0.#", CultureInfo.InvariantCulture); }

        /// <summary>a line per sequence entry that plays one of the clip's controllers (for import notes)</summary>
        public List<string> SeqUseNotes(ulong clipKey)
        {
            List<string> r = new List<string>();
            foreach (JObject j in SeqUses(H(clipKey)).Cast<JObject>())
                r.Add("played by " + j.Str("actor_name") + " track " + j.Long("track") + ": clip frames " + F1(j.Dbl("first_frame")) + "–" + F1(j.Dbl("last_frame")) +
                      (j.Bool("past_end") ? " (past the clip's last frame: the last frame is held)" : ""));
            return r;
        }
    }
}
