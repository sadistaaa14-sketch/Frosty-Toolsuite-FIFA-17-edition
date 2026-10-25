using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;

namespace FrostyAnimationEditor.Core.Anim
{
    /// <summary>One entry of a sequence track (SequenceAnimation), with its blend and init params resolved.</summary>
    public sealed class SeqEntry
    {
        public int Track, Index;
        public ulong Controller, Blend, InitParams;
        public string ControllerType = "", BlendType = "";
        public int Flags, StartTime, StartInTime, EndInTime;
        public float Scale = 1f;
        public float BlendTime;
        public int CurveType = 3;
        public bool SyncPhase;
        public float StartTick;

        /// <summary>sequence time where the entry becomes active</summary>
        public int Begin { get { return StartTime + StartInTime; } }
        /// <summary>sequence time where it ends</summary>
        public int End { get { return StartTime + EndInTime; } }
        public bool Mirror { get { return (Flags & 1) != 0; } }
    }

    public sealed class SeqTrack
    {
        public int Index;
        public ulong Target;
        public int Warpers;
        public List<SeqEntry> Entries = new List<SeqEntry>();
        /// <summary>the track ends at its last entry's StartTime + EndInTime</summary>
        public int End { get { return Entries.Count > 0 ? Entries[Entries.Count - 1].End : 0; } }
    }

    /// <summary>One actor of a sequence: an ActorControllerAsset and its default init params.</summary>
    public sealed class SeqActor
    {
        public ulong Key;
        public string Name = "";
        public float Length, LoopPoint = -1f;
        public bool PassTags, Snap;
        public long ContentFlags;
        public bool HasInit;
        public float X, Y, Z, Facing, Scale = 1f, StartTick;
        public long Mirror;
        public List<SeqTrack> Tracks = new List<SeqTrack>();
    }

    public struct SeqContribution
    {
        public SeqEntry Entry;
        public double Weight;   // 0..1, the contributions of one track sum to 1
        public double Ticks;    // the entry controller's time (ticks)
    }

    /// <summary>The playback window of a ClipControllerAsset.</summary>
    public sealed class ClipTiming
    {
        public ulong Controller, Clip;
        public float TimeScale = 1f, TrimOffset, TrimmedDuration, TickOffset;
        public long EndFrame, Modes;
        public double NumTicks;
        public bool Looping { get { return (Modes & 0x08) != 0; } }
        public bool Reversed { get { return (Modes & 0x10) != 0; } }

        /// <summary>clip frame at the controller's time: ticks x TimeScale + TrimOffset (the seek then clamps it to the
        /// clip's frames, or wraps it when the clip loops)</summary>
        public double Frame(double ticks)
        {
            if (Looping && NumTicks > 0) ticks = ticks - Math.Floor(ticks / NumTicks) * NumTicks;
            else ticks = Math.Max(0, Math.Min(NumTicks, ticks));
            return ticks * TimeScale + TrimOffset;
        }
    }

    /// <summary>
    /// Sequences (SequenceContainerAsset -> ActorControllerAsset -> tracks -> entries) as FIFA 17 plays them:
    /// <list type="bullet">
    /// <item>an entry is active on the sequence timeline from StartTime + StartInTime to StartTime + EndInTime; its
    /// controller runs at (sequence time - StartTime) x Scale;</item>
    /// <item>the next entry takes over at its own begin and blends in over its BlendAsset's BlendTime, the outgoing pose
    /// weighing curve(CurveType, elapsed / BlendTime); in the shipped
    /// data 82,948 of 83,063 overlapping entries overlap by exactly that BlendTime;</item>
    /// <item>the tracks are evaluated every update and merged, a later track writing the channels it has over the earlier
    /// ones;</item>
    /// <item>a ClipController samples its clip at ticks x TimeScale + TrimOffset and lasts ceil(TrimmedDuration / TimeScale)
    /// ticks.</item>
    /// </list>
    /// </summary>
    public static class Sequence
    {
        /// <summary>the curve table at DAT_1442cefc0 (types 0..2, 21 samples); type 3 is 1 - x</summary>
        private static readonly float[][] CurveTable =
        {
            new[] { 1.0f, 0.9969173073768616f, 0.9876883625984192f, 0.972369909286499f, 0.9510565400123596f, 0.9238795042037964f, 0.8910065293312073f, 0.8526401519775391f, 0.80901700258255f, 0.760405957698822f, 0.7071067690849304f, 0.649448037147522f, 0.5877852439880371f, 0.5224985480308533f, 0.45399048924446106f, 0.3826834261417389f, 0.30901700258255005f, 0.23344536125659943f, 0.15643446147441864f, 0.07845909893512726f, 0.0f },
            new[] { 1.0f, 0.9734029769897461f, 0.960834264755249f, 0.9426758289337158f, 0.9168273210525513f, 0.8807970881462097f, 0.8320183753967285f, 0.7685247659683228f, 0.6899744868278503f, 0.5986876487731934f, 0.5f, 0.40131235122680664f, 0.31002551317214966f, 0.23147521913051605f, 0.1679816097021103f, 0.11920291930437088f, 0.08317269384860992f, 0.05732417479157448f, 0.03916572406888008f, 0.02659699320793152f, 0.0f },
            new[] { 1.0f, 0.9215409159660339f, 0.8435655236244202f, 0.7665546536445618f, 0.69098299741745f, 0.6173165440559387f, 0.5460094809532166f, 0.47750142216682434f, 0.4122147560119629f, 0.350551962852478f, 0.2928932309150696f, 0.23959402740001678f, 0.19098301231861115f, 0.14735983312129974f, 0.10899347811937332f, 0.07612046599388123f, 0.04894348233938217f, 0.02763007953763008f, 0.012311658822000027f, 0.003082666080445051f, 0.0f },
        };

        public static readonly string[] CurveNames = { "cosine (0)", "S-curve (1)", "1 - sine (2)", "linear (3)" };

        /// <summary>the outgoing pose's weight at blend phase x in [0, 1]</summary>
        public static double Curve(int type, double x)
        {
            if (type == 3) return 1.0 - x;
            if (type < 0 || type > 2) return 1.0 - Math.Max(0, Math.Min(1, x));   // outside the table: not a shipped value
            int i = (int)(x * 20.0);
            if (i >= 20) return 0.0;
            if (i < 0) i = 0;
            double f = x * 20.0 - i;
            if (f < 0) f = 0;
            if (f > 1) f = 1;
            return (1.0 - f) * CurveTable[type][i] + f * CurveTable[type][i + 1];
        }

        // ------------------------------------------------------------------ loading
        public static bool IsSequenceType(string typeName)
        {
            return typeName == "SequenceContainerAsset" || typeName == "ActorControllerAsset" || typeName == "RelativeIkActorControllerAsset";
        }

        /// <summary>the actors of a SequenceContainerAsset, or the ActorControllerAsset itself</summary>
        public static List<SeqActor> Actors(AntDb db, ulong key)
        {
            GdObj o = db.Get(key);
            if (o == null) throw new KeyNotFoundException(BE.KeyHex(key));
            List<SeqActor> r = new List<SeqActor>();
            if (o.Type.Name == "SequenceContainerAsset")
            {
                IList l = o["ActorAssets"] as IList;
                if (l != null)
                    foreach (object x in l)
                    {
                        ulong k = Num.ToKey(x);
                        GdObj a = ActorOf(db, k != 0 ? db.Get(k) : null);
                        if (a != null) r.Add(LoadActor(db, a));
                    }
            }
            else
            {
                GdObj a = ActorOf(db, o);
                if (a != null) r.Add(LoadActor(db, a));
            }
            return r;
        }

        /// <summary>the ActorControllerAsset of o: o itself, or the actor a RelativeIkActorControllerAsset wraps</summary>
        public static GdObj ActorOf(AntDb db, GdObj o)
        {
            for (int depth = 0; o != null && depth < 4; depth++)
            {
                if (o.Has("Tracks")) return o;
                if (o.Type.Name != "RelativeIkActorControllerAsset") return null;
                ulong k = o.GetKey("ActorControllerAsset");
                o = k != 0 ? db.Get(k) : null;
            }
            return null;
        }

        public static SeqActor LoadActor(AntDb db, GdObj o)
        {
            SeqActor a = new SeqActor { Key = o.Key, Name = o.Name ?? "" };
            a.Length = o.GetFloat("Length");
            if (o.Has("LoopPoint")) a.LoopPoint = o.GetFloat("LoopPoint");
            if (o.Has("PassTagsToChildren")) a.PassTags = o.GetBool("PassTagsToChildren");
            if (o.Has("SnapToStartLocation")) a.Snap = o.GetBool("SnapToStartLocation");
            if (o.Has("ActorContentFlags")) a.ContentFlags = o.GetInt("ActorContentFlags");
            GdStruct ip = o["DefaultInitParam"] as GdStruct;
            if (ip != null)
            {
                a.HasInit = true;
                a.X = F(ip, "X"); a.Y = F(ip, "Y"); a.Z = F(ip, "Z");
                a.Facing = F(ip, "FacingAngle");
                a.Scale = ip.Has("Scale") ? F(ip, "Scale") : 1f;
                if (a.Scale == 0) a.Scale = 1f;
                a.Mirror = ip.Has("Mirror") ? Num.ToLong(ip["Mirror"]) : 0;
                a.StartTick = F(ip, "StartTick");
            }
            IList tracks = o["Tracks"] as IList;
            if (tracks != null)
                for (int ti = 0; ti < tracks.Count; ti++)
                {
                    GdStruct t = tracks[ti] as GdStruct;
                    if (t == null) continue;
                    SeqTrack tr = new SeqTrack { Index = ti, Target = Num.ToKey(t["Target"]) };
                    IList ws = t["WarpersParam"] as IList;
                    tr.Warpers = ws != null ? ws.Count : 0;
                    IList an = t["Anims"] as IList;
                    if (an != null)
                        for (int ei = 0; ei < an.Count; ei++)
                        {
                            GdStruct e = an[ei] as GdStruct;
                            if (e == null) continue;
                            tr.Entries.Add(LoadEntry(db, e, ti, ei));
                        }
                    a.Tracks.Add(tr);
                }
            return a;
        }

        private static float F(GdStruct s, string f) { return s.Has(f) ? (float)Num.ToDouble(s[f]) : 0f; }

        public static SeqEntry LoadEntry(AntDb db, GdStruct e, int track, int index)
        {
            SeqEntry s = new SeqEntry { Track = track, Index = index };
            s.Controller = Num.ToKey(e["Asset"]);
            s.Blend = Num.ToKey(e["BlendAsset"]);
            s.InitParams = Num.ToKey(e["InitParams"]);
            s.Flags = (int)Num.ToLong(e["Flags"]);
            s.StartTime = (int)Num.ToLong(e["StartTime"]);
            s.StartInTime = (int)Num.ToLong(e["StartInTime"]);
            s.EndInTime = (int)Num.ToLong(e["EndInTime"]);
            s.Scale = (float)Num.ToDouble(e["Scale"]);
            GdObj c = s.Controller != 0 ? db.Get(s.Controller) : null;
            s.ControllerType = c != null ? c.Type.Name : "";
            GdObj b = s.Blend != 0 ? db.Get(s.Blend) : null;
            if (b != null)
            {
                s.BlendType = b.Type.Name;
                GdObj bb = b;
                while (bb != null && !bb.Has("BlendTime")) bb = bb["__base"] as GdObj;
                if (bb != null) s.BlendTime = bb.GetFloat("BlendTime");
                if (b.Has("CurveType")) s.CurveType = (int)b.GetInt("CurveType");
                if (b.Has("ShouldSyncPhase")) s.SyncPhase = b.GetBool("ShouldSyncPhase");
            }
            GdObj ip = s.InitParams != 0 ? db.Get(s.InitParams) : null;
            if (ip != null && ip.Has("StartTick")) s.StartTick = ip.GetFloat("StartTick");
            return s;
        }

        public static ClipTiming Timing(AntDb db, ulong controller)
        {
            GdObj c = db.Get(controller);
            if (c == null || c.Type.Name != "ClipControllerAsset") return null;
            ClipTiming t = new ClipTiming { Controller = controller, Clip = c.GetKey("Anim") };
            t.TimeScale = c.GetFloat("TimeScale");
            t.TickOffset = c.GetFloat("TickOffset");
            t.Modes = c.GetInt("Modes");
            GdObj a = t.Clip != 0 ? db.Get(t.Clip) : null;
            GdObj b = a != null ? a["__base"] as GdObj : null;
            if (b != null)
            {
                t.TrimOffset = b.GetFloat("TrimOffset");
                t.TrimmedDuration = b.GetFloat("TrimmedDuration");
                t.EndFrame = b.GetInt("EndFrame");
            }
            t.NumTicks = t.Clip == 0 ? 1.0 : (t.TimeScale > 0 ? Math.Ceiling(t.TrimmedDuration / t.TimeScale) : 1.0);
            return t;
        }

        // ------------------------------------------------------------------ timing
        /// <summary>the entries a track plays at sequence time t, with their weights and controller times</summary>
        public static List<SeqContribution> Evaluate(SeqTrack tr, double t)
        {
            List<SeqContribution> res = new List<SeqContribution>();
            int n = tr.Entries.Count;
            if (n == 0) return res;
            double end = tr.End;
            if (t > end) t = end;           // the PlaybackTrack's time stops at the track's end
            if (t < 0) t = 0;
            List<KeyValuePair<int, double>> w = new List<KeyValuePair<int, double>> { new KeyValuePair<int, double>(0, 1.0) };
            for (int i = 1; i < n; i++)
            {
                SeqEntry e = tr.Entries[i];
                double at = Math.Max(0, e.Begin);
                if (t < at) break;
                double x = e.BlendTime > 0 ? (t - at) / e.BlendTime : 1.0;
                double wout = x >= 1.0 ? 0.0 : Curve(e.CurveType, Math.Max(0.0, x));
                for (int j = 0; j < w.Count; j++) w[j] = new KeyValuePair<int, double>(w[j].Key, w[j].Value * wout);
                w.Add(new KeyValuePair<int, double>(i, 1.0 - wout));
            }
            foreach (KeyValuePair<int, double> kv in w)
            {
                if (kv.Value <= 1e-6) continue;
                SeqEntry e = tr.Entries[kv.Key];
                res.Add(new SeqContribution { Entry = e, Weight = kv.Value, Ticks = EntryTicks(e, kv.Key == 0, t) });
            }
            return res;
        }

        /// <summary>the element's clock starts at max(0, StartTime) (the first entry) or at its begin (the
        /// others); its controller starts at StartInTime x Scale and only advances between begin and end</summary>
        public static double EntryTicks(SeqEntry e, bool first, double t)
        {
            double clock = first ? t + Math.Max(0, e.StartTime) : t;
            double b = Math.Max(0, e.Begin);
            double span = Math.Max(0, e.End - b);
            double adv = Math.Min(Math.Max(0, clock - b), span);
            return (e.StartInTime + adv) * e.Scale;
        }

        // ------------------------------------------------------------------ baking for the player view
        public sealed class Baked
        {
            public PlayerAnimation Anim;
            public List<string> Notes = new List<string>();
            public double TicksPerFrame;
            public int Actors;
        }

        /// <summary>
        /// Evaluate the actors on the skeleton, one frame every ticksPerFrame ticks. clipAnim: a clip evaluated on sk
        /// (local transforms per frame, Driven flags). Several actors are put side by side in one skeleton, each under a
        /// bone placed by its default init params (X, Y, Z in rig units, FacingAngle in radians about the up axis).
        /// </summary>
        public static Baked Bake(AntDb db, PlayerSkeleton sk, IList<SeqActor> actors, double ticksPerFrame, Func<ulong, PlayerAnimation> clipAnim)
        {
            Baked res = new Baked { TicksPerFrame = ticksPerFrame, Actors = actors.Count };
            if (actors.Count == 0) throw new ArgumentException("the sequence has no actor");
            double len = actors.Max(a => Math.Max(a.Length, a.Tracks.Count > 0 ? a.Tracks.Max(t => t.End) : 0));
            int nf = Math.Max(1, (int)Math.Ceiling(len / ticksPerFrame) + 1);
            Dictionary<ulong, ClipTiming> timing = new Dictionary<ulong, ClipTiming>();
            Dictionary<ulong, PlayerAnimation> clips = new Dictionary<ulong, PlayerAnimation>();
            HashSet<string> skipped = new HashSet<string>();
            int mirrored = 0, synced = 0;
            Func<ulong, ClipTiming> tm = k =>
            {
                ClipTiming t;
                if (!timing.TryGetValue(k, out t)) { t = Timing(db, k); timing[k] = t; }
                return t;
            };
            Func<ulong, PlayerAnimation> pa = clip =>
            {
                PlayerAnimation p;
                if (!clips.TryGetValue(clip, out p)) { p = clipAnim(clip); clips[clip] = p; }
                return p;
            };
            int nb = sk.Count;
            List<Affine[][]> perActor = new List<Affine[][]>();
            foreach (SeqActor actor in actors)
            {
                Affine[] defaults = sk.Local.ToArray();
                // the defaults of the first clip the actor plays (the rig defaults of the bones no channel drives)
                foreach (SeqTrack tr in actor.Tracks)
                {
                    SeqEntry first = tr.Entries.FirstOrDefault(e => tm(e.Controller) != null && tm(e.Controller).Clip != 0);
                    if (first == null) continue;
                    PlayerAnimation p0 = pa(tm(first.Controller).Clip);
                    if (p0 != null && p0.Defaults != null) for (int b = 0; b < nb && b < p0.Defaults.Length; b++) defaults[b] = p0.Defaults[b];
                    break;
                }
                foreach (SeqTrack tr in actor.Tracks)
                    foreach (SeqEntry e in tr.Entries)
                    {
                        if (e.Mirror) mirrored++;
                        if (e.SyncPhase) synced++;
                        if (tm(e.Controller) == null) skipped.Add(e.ControllerType.Length > 0 ? e.ControllerType : "(no controller)");
                    }
                Affine[][] frames = new Affine[nf][];
                for (int f = 0; f < nf; f++)
                {
                    double t = Math.Min(f * ticksPerFrame, actor.Length > 0 ? actor.Length : f * ticksPerFrame);
                    Affine[] pose = (Affine[])defaults.Clone();
                    foreach (SeqTrack tr in actor.Tracks)
                    {
                        List<SeqContribution> cs = Evaluate(tr, t);
                        List<Tuple<PlayerAnimation, double, double>> parts = new List<Tuple<PlayerAnimation, double, double>>();
                        foreach (SeqContribution c in cs)
                        {
                            ClipTiming ct = tm(c.Entry.Controller);
                            if (ct == null || ct.Clip == 0) continue;
                            PlayerAnimation p = pa(ct.Clip);
                            if (p == null || p.FrameCount == 0) continue;
                            parts.Add(Tuple.Create(p, c.Weight, ct.Frame(c.Entry.StartTick + c.Ticks)));
                        }
                        if (parts.Count == 0) continue;
                        double wsum = parts.Sum(x => x.Item2);
                        if (wsum <= 1e-9) continue;
                        for (int b = 0; b < nb; b++)
                        {
                            bool driven = false;
                            foreach (var x in parts) if (b < x.Item1.Driven.Length && x.Item1.Driven[b]) { driven = true; break; }
                            if (!driven) continue;
                            V3 tsum = V3.Zero, ssum = V3.Zero;
                            double qx = 0, qy = 0, qz = 0, qw = 0;
                            Q4 refq = Q4.Identity;
                            bool haveRef = false;
                            foreach (var x in parts)
                            {
                                PlayerAnimation p = x.Item1;
                                Affine l = p.Driven.Length > b && p.Driven[b] ? LocalAt(p, b, x.Item3) : (p.Defaults != null && b < p.Defaults.Length ? p.Defaults[b] : defaults[b]);
                                V3 lt, ls;
                                Q4 lq;
                                l.Decompose(out lt, out lq, out ls);
                                double wi = x.Item2 / wsum;
                                if (!haveRef) { refq = lq; haveRef = true; }
                                else if (refq.X * lq.X + refq.Y * lq.Y + refq.Z * lq.Z + refq.W * lq.W < 0) lq = lq.Neg();
                                tsum = tsum + lt * wi;
                                ssum = ssum + ls * wi;
                                qx += lq.X * wi; qy += lq.Y * wi; qz += lq.Z * wi; qw += lq.W * wi;
                            }
                            pose[b] = Affine.FromTRS(tsum, new Q4(qx, qy, qz, qw).Normalized(), ssum);
                        }
                    }
                    frames[f] = pose;
                }
                perActor.Add(frames);
            }
            if (skipped.Count > 0) res.Notes.Add("not previewed (not a ClipControllerAsset): " + string.Join(", ", skipped));
            if (mirrored > 0) res.Notes.Add(mirrored + " entr" + (mirrored == 1 ? "y is" : "ies are") + " mirrored in the game (Flags bit 0); the preview shows them unmirrored");
            if (synced > 0) res.Notes.Add(synced + " blend" + (synced == 1 ? "" : "s") + " sync phases in the game (ShouldSyncPhase); the preview does not");
            res.Notes.Add("clips: " + clips.Count + "; " + nf + " frames, one every " + ticksPerFrame + " ticks");

            PlayerAnimation outp = new PlayerAnimation { Fps = 60.0 / ticksPerFrame, FrameCount = nf, ClipName = actors.Count == 1 ? actors[0].Name : actors.Count + " actors" };
            if (actors.Count == 1)
            {
                outp.Skeleton = sk;
                outp.Local = perActor[0];
                outp.Driven = new bool[nb];
            }
            else
            {
                // side by side: per actor a placement bone, then the actor's copy of the skeleton
                PlayerSkeleton ms = new PlayerSkeleton { Name = "sequence (" + actors.Count + " actors)", Source = "sequence" };
                List<int> rootOf = new List<int>();
                for (int ai = 0; ai < actors.Count; ai++)
                {
                    SeqActor a = actors[ai];
                    int root = ms.Bones.Count;
                    rootOf.Add(root);
                    ms.Bones.Add(a.Name.Length > 0 ? a.Name : "actor " + ai);
                    ms.Parents.Add(-1);
                    ms.Local.Add(Placement(a));
                    for (int b = 0; b < nb; b++)
                    {
                        ms.Bones.Add(ai == 0 ? sk.Bones[b] : sk.Bones[b] + "#" + ai);
                        ms.Parents.Add(sk.Parents[b] >= 0 ? root + 1 + sk.Parents[b] : root);
                        ms.Local.Add(sk.Local[b]);
                    }
                }
                ms.ComputeModel();
                outp.Skeleton = ms;
                outp.Local = new Affine[nf][];
                for (int f = 0; f < nf; f++)
                {
                    Affine[] row = new Affine[ms.Count];
                    for (int ai = 0; ai < actors.Count; ai++)
                    {
                        row[rootOf[ai]] = ms.Local[rootOf[ai]];
                        Array.Copy(perActor[ai][f], 0, row, rootOf[ai] + 1, nb);
                    }
                    outp.Local[f] = row;
                }
                outp.Driven = new bool[ms.Count];
                res.Notes.Add("actors placed by their DefaultInitParam (X, Y, Z in rig units; FacingAngle as a rotation about the up axis: its direction in the game is not established)");
            }
            outp.Posed = new bool[outp.Skeleton.Count];
            outp.Defaults = outp.Skeleton.Local.ToArray();
            res.Anim = outp;
            return res;
        }

        public static Affine Placement(SeqActor a)
        {
            return Affine.FromTRS(new V3(a.X, a.Y, a.Z) * PlayerSkeleton.RigToMeters, Q4.AxisAngle(new V3(0, 1, 0), a.Facing), V3.One * (a.Scale != 0 ? a.Scale : 1));
        }

        /// <summary>the clip's local transform of bone b at a fractional frame (clamped to the clip's frames)</summary>
        private static Affine LocalAt(PlayerAnimation p, int b, double frame)
        {
            int n = p.FrameCount;
            if (frame <= 0 || n == 1) return p.Local[0][b];
            if (frame >= n - 1) return p.Local[n - 1][b];
            int i = (int)Math.Floor(frame);
            double f = frame - i;
            if (f < 1e-6) return p.Local[i][b];
            Affine a0 = p.Local[i][b], a1 = p.Local[i + 1][b];
            V3 t0, t1, s0, s1;
            Q4 q0, q1;
            a0.Decompose(out t0, out q0, out s0);
            a1.Decompose(out t1, out q1, out s1);
            if (q0.X * q1.X + q0.Y * q1.Y + q0.Z * q1.Z + q0.W * q1.W < 0) q1 = q1.Neg();
            Q4 q = new Q4(q0.X + (q1.X - q0.X) * f, q0.Y + (q1.Y - q0.Y) * f, q0.Z + (q1.Z - q0.Z) * f, q0.W + (q1.W - q0.W) * f).Normalized();
            return Affine.FromTRS(t0 + (t1 - t0) * f, q, s0 + (s1 - s0) * f);
        }

        // ------------------------------------------------------------------ where a controller is used
        public sealed class Use
        {
            public SeqActor Actor;
            public SeqEntry Entry;
        }

        /// <summary>the sequence entries that play the controller</summary>
        public static List<Use> UsesOf(AntDb db, ulong controller)
        {
            List<Use> r = new List<Use>();
            foreach (int i in db.RefsTo(controller))
            {
                if (db.Removed.Contains(i)) continue;
                string tn = db.TypeName(i);
                if (tn != "ActorControllerAsset") continue;
                SeqActor a = LoadActor(db, db.Obj(i));
                foreach (SeqTrack t in a.Tracks)
                    foreach (SeqEntry e in t.Entries)
                        if (e.Controller == controller) r.Add(new Use { Actor = a, Entry = e });
            }
            return r;
        }
    }
}
