using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using FrostyAnimationEditor.Core.Anim;

namespace FrostyAnimationEditor.Core
{
    /// <summary>
    /// An offline run of a state machine with the transition rules of the game's StateFlowNodeController
    ///:
    ///   - the node's FlattenedTransitions are tried as a tree (FirstChildIndex, SiblingIndex); an entry needs every
    ///     TrueConditionIndices condition true and every FalseConditionIndices one false (the indices go
    ///     into AllTransitionConditions and include the target's entry conditions);
    ///   - its fire time in this update by BranchTypeIndex: 0 now; 1 (BranchAtEnd) the subject's time left minus the
    ///     transition's BranchAtEndOffset; 2 (BranchAfterFirstUpdate) now, except in the node's first update; 3 and up
    ///     the time until a BranchOutPointTag window of the subject opens whose BranchTypeAsset is
    ///     AllBranchTypes[index] (BranchTypeAsset.ControllerFilterIdentifier must be 0 or the state
    ///     machine's Identifier); an entry with children fires at the latest of its own and the child's
    ///     time;
    ///   - the entry that fires earliest within the update wins, the earlier one in the table on a tie.
    /// A SequenceContainerAsset subject lasts its longest actor and its windows are its actors' BranchOutPointTags (an
    /// assumption: which timeline the game reads for a container is not known); a chooser subject has no known length
    /// unless the caller says what it plays.
    /// Gate nodes are open unless the caller closes them. Not modelled: branch tuning (BranchTuner /
    /// Start/EndTuningOffset: the untuned window is used), the gates' own check,
    /// nested state machines, branch-in windows and destination phase (the new node starts at 0), StateOps and signal
    /// deactivation.
    /// </summary>
    public sealed class StateFlowSim
    {
        public sealed class Cond
        {
            public ulong Key;
            public string Type = "", Name = "";
            public bool Value;
        }

        public sealed class Step
        {
            public double Time;              // simulation time (ticks) of the transition
            public ulong From, To, Transition, Via;
            public double SubjectTime;       // the from node's subject time when it fired
            public string Rule = "";         // what let it fire
            public ulong Blend;
        }

        public sealed class Segment
        {
            public ulong Node;
            public double Start, End;        // simulation ticks
            public double SubjectLength;     // 0: not known
        }

        public readonly List<Step> Log = new List<Step>();
        public readonly List<Segment> Segments = new List<Segment>();
        public readonly List<string> Notes = new List<string>();

        private readonly AntDb db;
        private readonly GdObj sfc;
        private readonly Dictionary<ulong, bool> values;
        private Dictionary<ulong, ulong> plays = new Dictionary<ulong, ulong>();
        private Dictionary<ulong, bool> gates = new Dictionary<ulong, bool>();

        /// <summary>the controller whose length and branch windows a node's run uses: the subject, or what the caller
        /// says a chooser subject plays</summary>
        private GdObj Timed(GdObj node)
        {
            ulong k;
            if (plays.TryGetValue(node.Key, out k) && k != 0) return db.Get(k);
            ulong sk = node.GetKey("SubjectController");
            return sk != 0 ? db.Get(sk) : null;
        }
        private readonly HashSet<string> noted = new HashSet<string>();

        private StateFlowSim(AntDb db, GdObj sfc, Dictionary<ulong, bool> values)
        {
            this.db = db;
            this.sfc = sfc;
            this.values = values;
        }

        /// <summary>the gate nodes the state machine's transitions go through (FlattenedTransitions entries with a
        /// GateNodeId: their TransitionNode)</summary>
        public static List<GdObj> Gates(AntDb db, ulong sfcKey)
        {
            List<GdObj> r = new List<GdObj>();
            HashSet<ulong> seen = new HashSet<ulong>();
            foreach (GdObj n in StateMachine.States(db, sfcKey))
            {
                IList ft = n["FlattenedTransitions"] as IList;
                if (ft == null) continue;
                foreach (GdStruct e in ft)
                {
                    if (e.GetInt("GateNodeId") == 255) continue;
                    ulong g = Num.ToKey(e["TransitionNode"]);
                    GdObj go = g != 0 ? db.Get(g) : null;
                    if (go != null && seen.Add(g)) r.Add(go);
                }
            }
            return r;
        }

        /// <summary>the conditions the state machine's nodes use, with their defaults (BoolAsset.Default,
        /// SignalAsset.DefaultBool; an enumeration pair starts false)</summary>
        public static List<Cond> Conditions(AntDb db, ulong sfcKey)
        {
            Dictionary<ulong, Cond> r = new Dictionary<ulong, Cond>();
            List<ulong> order = new List<ulong>();
            foreach (GdObj n in StateMachine.States(db, sfcKey))
            {
                IList all = n["AllTransitionConditions"] as IList;
                if (all == null) continue;
                foreach (object x in all)
                {
                    ulong k = Num.ToKey(x);
                    if (k == 0 || r.ContainsKey(k)) continue;
                    GdObj c = db.Get(k);
                    Cond cd = new Cond { Key = k };
                    if (c != null)
                    {
                        cd.Type = c.Type.Name;
                        cd.Name = c.Name ?? "";
                        if (c.Has("Default")) cd.Value = c.GetBool("Default");
                        else if (c.Has("DefaultBool")) cd.Value = c.GetBool("DefaultBool");
                    }
                    r[k] = cd;
                    order.Add(k);
                }
            }
            return order.Select(k => r[k]).ToList();
        }

        private void Note(string s) { if (noted.Add(s)) Notes.Add(s); }

        /// <summary>the subject controller's length in its ticks (0: not known for the subject's class) and whether
        /// it loops (a ClipController with Modes bit 3)</summary>
        private double SubjectLength(GdObj node, out bool loops)
        {
            loops = false;
            GdObj s = Timed(node);
            if (s == null) return 0;
            if (s.Type.Name == "ClipControllerAsset")
            {
                ClipTiming ct = Sequence.Timing(db, s.Key);
                if (ct != null) loops = ct.Looping;
            }
            if (s.Type.Name == "SequenceContainerAsset")
            {
                List<SeqActor> actors = Sequence.Actors(db, s.Key);
                return actors.Count > 0 ? actors.Max(a => (double)a.Length) : 0;
            }
            double len = Tags.OwnerLength(db, s);
            if (len <= 0) Note("a subject of class " + s.Type.Name + " has no length the run can know (it picks or makes what it plays at run time): " +
                               "BranchAtEnd and branch windows do not fire for it unless you choose what it plays");
            return len;
        }

        private sealed class Window { public double Start, Length; public ulong Type; }

        /// <summary>the BranchOutPointTag windows on the subject's timeline, with their BranchTypeAsset</summary>
        private List<Window> Windows(GdObj node, double len)
        {
            List<Window> r = new List<Window>();
            GdObj s = Timed(node);
            if (s == null || Tags.ControllerPart(s) == null) return r;
            List<GdObj> owners = new List<GdObj> { s };
            // a sequence container's branch windows are on its actors' timelines
            if (s.Type.Name == "SequenceContainerAsset")
                foreach (SeqActor a in Sequence.Actors(db, s.Key)) { GdObj ao = db.Get(a.Key); if (ao != null) owners.Add(ao); }
            foreach (GdObj ow in owners)
            foreach (TagCollection c in Tags.Load(db, ow))
                foreach (TagEntry e in c.Tags)
                {
                    GdObj t = db.Get(e.Key);
                    GdObj bo = t != null ? Tags.Part(t, "BranchOutPointTag") : null;
                    if (bo == null) continue;
                    r.Add(new Window { Start = e.StartTicks(len), Length = e.LengthTicks(len), Type = bo.GetKey("BranchTypeAsset") });
                }
            return r;
        }

        private bool CondsHold(GdObj node, IList idx, bool want)
        {
            if (idx == null) return true;
            IList all = node["AllTransitionConditions"] as IList;
            foreach (object x in idx)
            {
                int i = (int)Num.ToLong(x);
                ulong k = all != null && i >= 0 && i < all.Count ? Num.ToKey(all[i]) : 0;
                bool v;
                if (!values.TryGetValue(k, out v)) v = false;
                if (v != want) return false;
            }
            return true;
        }

        private const double Never = 1e35;

        /// <summary>the entry's own fire time from now (Never: not in reach)</summary>
        private double EntryTime(GdObj node, GdStruct e, double t, double len, bool firstUpdate, List<Window> wins, out string rule)
        {
            rule = "";
            long bti = e.GetInt("BranchTypeIndex");
            if (bti == 0) { rule = "any time"; return 0; }
            if (bti == 1)
            {
                GdObj tr = db.Get(Num.ToKey(e["Transition"]));
                double off = tr != null && tr.Has("BranchAtEndOffset") ? tr.GetInt("BranchAtEndOffset") : 0;
                if (len <= 0) { Note("BranchAtEnd needs the subject's length, not known for some subjects: those never reach the end here"); return Never; }
                rule = "at the end" + (off != 0 ? " - " + off + " ticks" : "");
                return Math.Max(0, (len - t) - off);
            }
            if (bti == 2)
            {
                rule = "after the first update";
                return firstUpdate ? Never : 0;
            }
            IList types = node["AllBranchTypes"] as IList;
            ulong bt = types != null && bti < types.Count ? Num.ToKey(types[(int)bti]) : 0;
            GdObj bta = bt != 0 ? db.Get(bt) : null;
            if (bta != null && bta.Has("ControllerFilterIdentifier"))
            {
                long f = bta.GetInt("ControllerFilterIdentifier");
                if (f != 0 && f != sfc.GetInt("Identifier")) return Never;
            }
            double best = Never;
            foreach (Window w in wins)
            {
                if (w.Type != bt) continue;
                double dt = t < w.Start ? w.Start - t : (t <= w.Start + w.Length ? 0 : Never);
                if (dt < best) { best = dt; rule = "branch window " + (bta != null ? (bta.Name ?? "") : "") + " " + Fmt(w.Start) + "-" + Fmt(w.Start + w.Length); }
            }
            return best;
        }

        private static string Fmt(double v) { return Math.Round(v, 2).ToString(System.Globalization.CultureInfo.InvariantCulture); }

        private sealed class Pick { public int Index = -1; public double Time = Never; public string Rule = ""; }

        /// <summary>siblings from 'first', the earliest firing entry (ties: the first)</summary>
        private Pick Evaluate(GdObj node, IList ft, int first, double t, double len, bool firstUpdate, List<Window> wins, double limit, int depth)
        {
            Pick best = new Pick();
            int n = ft.Count;
            for (int i = first, guard = 0; i >= 0 && i < n && guard < 256; guard++)
            {
                GdStruct e = (GdStruct)ft[i];
                int next = (int)e.GetInt("SiblingIndex");
                if (e.GetBool("IsNested")) { Note("nested state machines are not followed (IsNested entries are skipped)"); i = next; continue; }
                if (e.GetInt("GateNodeId") != 255)
                {
                    // the entry goes through a gate node: the game asks the gate; the run takes the gate
                    // as open or closed as the caller says (open by default)
                    bool open;
                    ulong g = Num.ToKey(e["TransitionNode"]);
                    if (gates.TryGetValue(g, out open) && !open) { i = next; continue; }
                    Note("gates are taken as open unless closed below (the game's gate check is not modelled)");
                }
                if (CondsHold(node, e["TrueConditionIndices"] as IList, true) && CondsHold(node, e["FalseConditionIndices"] as IList, false))
                {
                    string rule;
                    double own = EntryTime(node, e, t, len, firstUpdate, wins, out rule);
                    int child = (int)e.GetInt("FirstChildIndex");
                    Pick p = new Pick { Index = i, Time = own, Rule = rule };
                    if (own < Never && child >= 0 && child < n && depth < 16)
                    {
                        Pick c = Evaluate(node, ft, child, t, len, firstUpdate, wins, limit, depth + 1);
                        if (c.Index < 0) p.Time = Never;
                        else { p.Index = c.Index; p.Time = Math.Max(own, c.Time); p.Rule = rule + (c.Rule.Length > 0 ? ", then " + c.Rule : ""); }
                    }
                    if (p.Time < best.Time && p.Time < limit) best = p;
                    if (best.Time <= 1.5258789e-05) break;
                }
                i = next;
            }
            return best;
        }

        /// <summary>
        /// run from the start node (or 'start') for 'duration' ticks in steps of 'step' ticks, with the conditions
        /// held at the values given (missing ones: their defaults)
        /// </summary>
        public static StateFlowSim Run(AntDb db, ulong sfcKey, IDictionary<ulong, bool> conds, double duration, double step, ulong start = 0, int maxTransitions = 500,
                                       IDictionary<ulong, ulong> plays = null, IDictionary<ulong, bool> gates = null)
        {
            GdObj sfc = db.Get(sfcKey);
            if (sfc == null || sfc.Type.Name != StateMachine.SFC) throw new ArgumentException("not a StateFlowControllerAsset");
            Dictionary<ulong, bool> v = Conditions(db, sfcKey).ToDictionary(c => c.Key, c => c.Value);
            if (conds != null) foreach (KeyValuePair<ulong, bool> kv in conds) v[kv.Key] = kv.Value;
            StateFlowSim s = new StateFlowSim(db, sfc, v);
            if (plays != null) s.plays = new Dictionary<ulong, ulong>(plays);
            if (gates != null) s.gates = new Dictionary<ulong, bool>(gates);
            ulong cur = start != 0 ? start : sfc.GetKey("StartNode");
            GdObj node = db.Get(cur);
            if (node == null) throw new ArgumentException("the state machine has no start node");
            double now = 0, t = 0;
            bool firstUpdate = true, loops;
            double len = s.SubjectLength(node, out loops);
            List<Window> wins = s.Windows(node, len);
            Segment seg = new Segment { Node = cur, Start = 0, SubjectLength = len };
            step = Math.Max(0.01, step);
            int transitions = 0, sameTime = 0;
            while (now < duration - 1e-9 && transitions < maxTransitions)
            {
                double dt = Math.Min(step, duration - now);
                IList ft = node["FlattenedTransitions"] as IList;
                Pick p = ft != null && ft.Count > 0 ? s.Evaluate(node, ft, 0, t, len, firstUpdate, wins, Never, 0) : new Pick();
                if (p.Index >= 0 && p.Time <= dt)
                {
                    GdStruct e = (GdStruct)ft[p.Index];
                    ulong trk = Num.ToKey(e["Transition"]);
                    GdObj tr = db.Get(trk);
                    ulong to = tr != null ? tr.GetKey("To") : 0;
                    GdObj next = to != 0 ? db.Get(to) : null;
                    double at = now + p.Time;
                    s.Log.Add(new Step
                    {
                        Time = at, From = cur, To = to, Transition = trk, Via = Num.ToKey(e["TransitionNode"]),
                        SubjectTime = t + p.Time, Rule = p.Rule, Blend = tr != null ? tr.GetKey("OverrideBlend") : 0,
                    });
                    seg.End = at;
                    s.Segments.Add(seg);
                    transitions++;
                    sameTime = p.Time <= 1e-9 ? sameTime + 1 : 0;
                    if (next == null) { s.Note("a transition goes to a node that is not in the file; the run stops there"); return s; }
                    cur = to;
                    node = next;
                    t = 0;
                    now = at;
                    firstUpdate = true;
                    len = s.SubjectLength(node, out loops);
                    wins = s.Windows(node, len);
                    seg = new Segment { Node = cur, Start = now, SubjectLength = len };
                    if (sameTime > 64) { s.Note("the state machine keeps transitioning without time passing; the run stops"); return s; }
                    continue;
                }
                now += dt;
                t += dt;
                if (len > 0 && t >= len) t = loops ? t % len : len;   // a looping clip wraps, others hold the last tick
                firstUpdate = false;
                sameTime = 0;
            }
            seg.End = now;
            s.Segments.Add(seg);
            if (transitions >= maxTransitions) s.Note("stopped after " + maxTransitions + " transitions");
            return s;
        }
    }
}
