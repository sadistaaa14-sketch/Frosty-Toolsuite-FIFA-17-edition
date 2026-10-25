using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using FrostyAnimationEditor.Core.Anim;

namespace FrostyAnimationEditor.Core
{
    /// <summary>state machine runs (StateFlowSim)</summary>
    public sealed partial class EditorModel
    {
        /// <summary>the gate nodes a state machine's transitions go through</summary>
        public List<object> SmGates(object key)
        {
            ulong ck = SmController(key);
            return StateFlowSim.Gates(Db, ck).Select(g =>
            {
                JObject j = new JObject();
                j["key"] = H(g.Key); j["name"] = NameOf(g);
                return (object)j;
            }).ToList();
        }

        /// <summary>the conditions of a state machine with their defaults</summary>
        public List<object> SmConditions(object key)
        {
            ulong ck = SmController(key);
            return StateFlowSim.Conditions(Db, ck).Select(c =>
            {
                JObject j = new JObject();
                j["key"] = H(c.Key); j["type"] = c.Type; j["name"] = c.Name; j["default"] = c.Value;
                return (object)j;
            }).ToList();
        }

        /// <summary>
        /// run the state machine: a (all optional): conditions {key: bool}, duration (ticks, 600), step (ticks, 2),
        /// start (node key)
        /// </summary>
        public JObject SmSim(object key, JObject a)
        {
            ulong ck = SmController(key);
            Dictionary<ulong, bool> conds = new Dictionary<ulong, bool>();
            JObject cj = a != null ? a.Obj("conditions") : null;
            if (cj != null) foreach (KeyValuePair<string, object> kv in cj) conds[K(kv.Key)] = Num.ToBool(kv.Value);
            double duration = a != null && a["duration"] != null ? a.Dbl("duration") : 600;
            double step = a != null && a["step"] != null ? a.Dbl("step") : 2;
            ulong start = a != null && a.Str("start") != null ? K(a.Str("start")) : 0;
            Dictionary<ulong, ulong> plays = new Dictionary<ulong, ulong>();
            JObject pj = a != null ? a.Obj("plays") : null;
            if (pj != null) foreach (KeyValuePair<string, object> kv in pj) plays[K(kv.Key)] = K(kv.Value);
            Dictionary<ulong, bool> gates = new Dictionary<ulong, bool>();
            JObject gj = a != null ? a.Obj("gates") : null;
            if (gj != null) foreach (KeyValuePair<string, object> kv in gj) gates[K(kv.Key)] = Num.ToBool(kv.Value);
            if (duration <= 0 || duration > 1e6) throw new ApiError("duration: 1 to 1,000,000 ticks");
            if (step <= 0 || step > 600) throw new ApiError("step: more than 0, at most 600 ticks");
            StateFlowSim s;
            try { s = StateFlowSim.Run(Db, ck, conds, duration, step, start, 500, plays, gates); }
            catch (ArgumentException e) { throw new ApiError(e.Message); }
            Func<ulong, string> nm = k => { if (k == 0) return ""; GdObj o = Db.Get(k); return o != null ? (Alias(k) ?? NameOf(o)) : H(k); };
            JObject r = new JObject();
            r["controller"] = H(ck);
            r["log"] = s.Log.Select(x =>
            {
                JObject j = new JObject();
                j["time"] = x.Time; j["subject_time"] = x.SubjectTime;
                j["from"] = H(x.From); j["from_name"] = nm(x.From);
                j["to"] = H(x.To); j["to_name"] = nm(x.To);
                j["transition"] = H(x.Transition); j["transition_name"] = nm(x.Transition);
                j["via"] = x.Via != 0 && x.Via != x.From ? (object)H(x.Via) : null; j["via_name"] = x.Via != 0 && x.Via != x.From ? nm(x.Via) : null;
                j["rule"] = x.Rule;
                j["blend"] = x.Blend != 0 ? (object)H(x.Blend) : null; j["blend_name"] = x.Blend != 0 ? nm(x.Blend) : null;
                return (object)j;
            }).ToList();
            r["segments"] = s.Segments.Select(x =>
            {
                JObject j = new JObject();
                j["node"] = H(x.Node); j["name"] = nm(x.Node); j["start"] = x.Start; j["end"] = x.End; j["subject_length"] = x.SubjectLength;
                GdObj n = Db.Get(x.Node);
                ulong sk = n != null ? n.GetKey("SubjectController") : 0;
                j["subject"] = sk != 0 ? (object)H(sk) : null; j["subject_name"] = nm(sk);
                return (object)j;
            }).ToList();
            r["notes"] = s.Notes.Cast<object>().ToList();
            r["duration"] = duration;
            r["step"] = step;
            return r;
        }

        // ------------------------------------------------------------------ controller tree
        /// <summary>fields that point back up the tree (a node's state machine, a transition's From, ...): not followed</summary>
        private static readonly HashSet<string> BackFields = new HashSet<string>(StringComparer.Ordinal)
        {
            "StateFlowController", "From", "To", "TransitionNode", "TagCollectionSet", "MirroredSignal", "BlendTag", "GameStateEnum", "InitializeFromEnum",
        };

        /// <summary>
        /// the controllers an object plays, one level: references to objects with a ControllerAsset part, also through
        /// one non-controller object in between (a chooser entry, a transition, a sequence entry's init params, ...)
        /// </summary>
        public List<object> ControllerChildren(object key)
        {
            GdObj o = Root(key);
            List<object> r = new List<object>();
            HashSet<ulong> seen = new HashSet<ulong> { o.Key };
            // a state machine plays its states; a state plays its subject (its transitions lead to other states)
            if (o.Type.Name == StateMachine.SFC)
            {
                ulong startKey = o.GetKey("StartNode");
                foreach (GdObj n in StateMachine.States(Db, o.Key).OrderBy(n => n.Key != startKey))
                {
                    JObject j = new JObject();
                    j["key"] = H(n.Key); j["type"] = n.Type.Name; j["name"] = NameOf(n); j["via"] = n.Key == startKey ? "start state" : "state";
                    r.Add(j);
                }
                return r;
            }
            if (o.Type.Name == StateMachine.NODE)
            {
                ulong sk = o.GetKey("SubjectController");
                GdObj so = sk != 0 ? Db.Get(sk) : null;
                if (so == null) return r;
                if (Tags.ControllerPart(so) != null)
                {
                    JObject j = new JObject();
                    j["key"] = H(so.Key); j["type"] = so.Type.Name; j["name"] = Alias(so.Key) ?? NameOf(so); j["via"] = "SubjectController";
                    r.Add(j);
                    return r;
                }
                o = so;   // a wrapper: what it holds
                seen.Add(so.Key);
            }
            Action<GdObj, string, int> walk = null;
            walk = (x, prefix, depth) =>
            {
                foreach (KeyRef kr in Core.Refs.From(x))
                {
                    if (kr.Key == 0 || seen.Contains(kr.Key)) continue;
                    string path = kr.Path.Substring(kr.Path.IndexOf('.') + 1);
                    string last = path;
                    int dot = last.LastIndexOf('.');
                    if (dot >= 0) last = last.Substring(dot + 1);
                    int br = last.IndexOf('[');
                    if (br >= 0) last = last.Substring(0, br);
                    if (BackFields.Contains(last)) continue;
                    GdObj t = Db.Get(kr.Key);
                    if (t == null) continue;
                    if (Tags.ControllerPart(t) != null)
                    {
                        seen.Add(kr.Key);
                        JObject j = new JObject();
                        j["key"] = H(t.Key); j["type"] = t.Type.Name; j["name"] = Alias(t.Key) ?? NameOf(t); j["via"] = prefix + path;
                        r.Add(j);
                        if (r.Count >= 400) return;
                    }
                    else if (depth < 1 && !(t.Type.Name.EndsWith("Tag", StringComparison.Ordinal) || t.Type.Name.Contains("TagCollection")))
                    {
                        seen.Add(kr.Key);
                        walk(t, prefix + path + " > " + t.Type.Name + ".", depth + 1);
                    }
                }
            };
            walk(o, "", 0);
            return r;
        }

        /// <summary>controllers with a known length (clip controllers, sequences) that a state's subject can play,
        /// found through its controller tree (for the run's "what it plays")</summary>
        public List<object> PlayCandidates(object nodeKey, int max = 60)
        {
            GdObj n = Root(nodeKey);
            List<object> r = new List<object>();
            HashSet<string> seen = new HashSet<string>();
            Queue<Tuple<string, int>> q = new Queue<Tuple<string, int>>();
            q.Enqueue(Tuple.Create(H(n.Key), 0));
            while (q.Count > 0 && r.Count < max)
            {
                Tuple<string, int> cur = q.Dequeue();
                if (!seen.Add(cur.Item1)) continue;
                foreach (JObject c in ControllerChildren(cur.Item1).Cast<JObject>())
                {
                    string t = c.Str("type");
                    if (t == "ClipControllerAsset" || t == "SequenceContainerAsset" || (t == "ActorControllerAsset" && cur.Item2 == 0))
                    {
                        if (seen.Add(c.Str("key"))) r.Add(c);
                        if (r.Count >= max) break;
                    }
                    else if (cur.Item2 < 5 && t != StateMachine.SFC) q.Enqueue(Tuple.Create(c.Str("key"), cur.Item2 + 1));
                }
            }
            return r;
        }

        // ------------------------------------------------------------------ choosers
        private JObject Ref(ulong k)
        {
            if (k == 0) return null;
            GdObj o = Db.Get(k);
            JObject j = new JObject();
            j["key"] = H(k);
            j["type"] = o != null ? o.Type.Name : "(missing)";
            j["name"] = o != null ? (Alias(k) ?? NameOf(o)) : H(k);
            return j;
        }

        /// <summary>
        /// a ChooserControllerAsset as the game uses it: the policy picks an entry :
        /// random by Weight, the entry whose enumeration value is the game state's (entries
        /// without a value are the default), or the first entry whose true signals are all on and false signals all off
        ///; with none of ChooseOnReset / ChooseEveryUpdate / ChooseFirstUpdate / Looping set the choice is
        /// made once when the controller is created, falling back to the last entry
        /// </summary>
        public JObject Chooser(object key)
        {
            GdObj o = Root(key);
            if (o.Type.Name != "ChooserControllerAsset") throw new ApiError(NameOf(o) + " is not a ChooserControllerAsset");
            JObject r = new JObject();
            r["key"] = H(o.Key);
            r["name"] = NameOf(o);
            GdObj pol = Db.Get(o.GetKey("PolicyAsset"));
            JObject pj = new JObject();
            string kind = pol == null ? "none" : pol.Type.Name;
            pj["key"] = pol != null ? (object)H(pol.Key) : null;
            pj["type"] = kind;
            pj["name"] = pol != null ? NameOf(pol) : "";
            if (pol != null)
            {
                if (pol.Has("RandomValueAsset")) pj["random_value"] = Ref(pol.GetKey("RandomValueAsset"));
                if (pol.Has("GameStateAsset")) pj["game_state"] = Ref(pol.GetKey("GameStateAsset"));
                if (pol.Has("EnumerationAsset")) pj["enumeration"] = Ref(pol.GetKey("EnumerationAsset"));
                if (pol.Has("CacheLastChoice")) pj["cache_last"] = pol.GetBool("CacheLastChoice");
            }
            r["policy"] = pj;
            string[] flags = { "ChooseOnReset", "ChooseEveryUpdate", "ChooseFirstUpdate", "Looping", "ChooseOnLoop", "PhaseMatchPrevious", "LoopPreviousController" };
            JObject fj = new JObject();
            foreach (string f in flags) if (o.Has(f)) fj[f] = o.GetBool(f);
            r["flags"] = fj;
            bool once = !o.GetBool("ChooseOnReset") && !o.GetBool("ChooseEveryUpdate") && !o.GetBool("ChooseFirstUpdate") && !o.GetBool("Looping");
            r["once"] = once;
            r["current_choice"] = Ref(o.GetKey("CurrentChoice"));
            r["blend_tag"] = Ref(o.GetKey("BlendTag"));
            List<object> es = new List<object>();
            IList el = o["EntryAssetList"] as IList;
            double wsum = 0;
            List<GdObj> entries = new List<GdObj>();
            if (el != null) foreach (object x in el) entries.Add(Db.Get(Num.ToKey(x)));
            foreach (GdObj e in entries) if (e != null && e.Has("Weight")) wsum += Math.Max(0, e.GetFloat("Weight"));
            for (int i = 0; i < entries.Count; i++)
            {
                GdObj e = entries[i];
                JObject j = new JObject();
                j["index"] = (long)i;
                if (e == null) { j["type"] = "(missing)"; es.Add(j); continue; }
                j["key"] = H(e.Key);
                j["type"] = e.Type.Name;
                GdObj cb = Tags.Part(e, "ChooserEntryAsset");
                j["controller"] = cb != null ? Ref(cb.GetKey("ControllerAsset")) : null;
                if (e.Has("Weight"))
                {
                    double w = e.GetFloat("Weight");
                    j["weight"] = w;
                    j["probability"] = wsum > 0 ? Math.Max(0, w) / wsum : 0;
                }
                if (e.Has("EnumerationValueAsset"))
                {
                    GdObj ev = Db.Get(e.GetKey("EnumerationValueAsset"));
                    JObject vj = Ref(e.GetKey("EnumerationValueAsset"));
                    if (vj != null && ev != null && ev.Has("Value")) vj["value"] = ev.GetInt("Value");
                    j["enum_value"] = vj;
                }
                if (e.Has("TrueSignals"))
                {
                    j["true_signals"] = ((IList)e["TrueSignals"]).Cast<object>().Select(x => (object)Ref(Num.ToKey(x))).ToList();
                    j["false_signals"] = ((IList)e["FalseSignals"]).Cast<object>().Select(x => (object)Ref(Num.ToKey(x))).ToList();
                }
                es.Add(j);
            }
            r["entries"] = es;
            r["weight_sum"] = wsum;
            return r;
        }

        // ------------------------------------------------------------------ layers
        public static readonly string[] LayerUpdateModes =
        {
            "0: not updated", "1: always updated, even at weight 0", "2: updated while its weight is above 0", "3: as 2; at weight 0 the game still does something with it (not known what)",
        };
        public static readonly string[] LayerTimeModes =
        {
            "0: its own time", "1: follows the first layer's phase", "2: follows the previous layer's phase", "3: moves by the first layer's phase change",
            "4: moves by the previous layer's phase change",
        };

        /// <summary>a LayersControllerAsset's layers as the game reads them</summary>
        public JObject Layers(object key)
        {
            GdObj o = Root(key);
            if (o.Type.Name != "LayersControllerAsset") throw new ApiError(NameOf(o) + " is not a LayersControllerAsset");
            JObject r = new JObject();
            r["key"] = H(o.Key); r["name"] = NameOf(o);
            r["master"] = o.GetInt("MasterEntryIndex");
            r["nested_mirroring"] = o.GetBool("SupportNestedMirroring");
            IList cs = (IList)o["ControllerAssets"], ss = (IList)o["SignalAssets"], ms = (IList)o["BlendMaskControllers"];
            IList um = (IList)o["UpdateModes"], ur = (IList)o["UpdateRates"], bm = (IList)o["BlendModes"], bi = (IList)o["BranchInModes"];
            List<object> ls = new List<object>();
            for (int i = 0; i < cs.Count; i++)
            {
                JObject j = new JObject();
                j["index"] = (long)i;
                j["controller"] = Ref(Num.ToKey(cs[i]));
                j["signal"] = i < ss.Count ? Ref(Num.ToKey(ss[i])) : null;
                GdObj sg = i < ss.Count ? Db.Get(Num.ToKey(ss[i])) : null;
                if (sg != null && sg.Has("Default")) j["signal_default"] = (double)sg.GetFloat("Default");
                j["mask"] = i < ms.Count ? Ref(Num.ToKey(ms[i])) : null;
                j["update_mode"] = i < um.Count ? Num.ToLong(um[i]) : 0;
                j["time_mode"] = i < ur.Count ? Num.ToLong(ur[i]) : 0;
                j["blend_mode"] = i < bm.Count ? Num.ToLong(bm[i]) : 0;
                j["branch_in_mode"] = i < bi.Count ? Num.ToLong(bi[i]) : 0;
                ls.Add(j);
            }
            r["layers"] = ls;
            return r;
        }

        // ------------------------------------------------------------------ blend masks
        private List<object> LayoutSlots(ulong layoutKey)
        {
            List<object> r = new List<object>();
            GdObj lay = layoutKey != 0 ? Db.Get(layoutKey) : null;
            IList slots = lay != null ? lay["Slots"] as IList : null;
            if (slots == null) return r;
            foreach (object x in slots)
            {
                GdStruct st = x as GdStruct;
                JObject j = new JObject();
                j["name"] = st != null && st.Has("Name") ? (object)Convert.ToString(st["Name"], CultureInfo.InvariantCulture) : "";
                j["slot_type"] = st != null && st.Has("Type") ? st.GetInt("Type") : 0;
                r.Add(j);
            }
            return r;
        }

        /// <summary>a BlendMaskAsset (a weight per slot of its layout) or a BlendMaskListAsset (per layout of its hierarchy:
        /// a mask, or a default value)</summary>
        public JObject BlendMask(object key)
        {
            GdObj o = Root(key);
            JObject r = new JObject();
            r["key"] = H(o.Key); r["type"] = o.Type.Name; r["name"] = NameOf(o);
            if (o.Type.Name == "BlendMaskAsset")
            {
                ulong lk = o.GetKey("DofSetKey");
                r["layout"] = Ref(lk);
                List<object> slots = LayoutSlots(lk);
                IList vals = o["Values"] as IList;
                for (int i = 0; i < slots.Count; i++) ((JObject)slots[i])["value"] = vals != null && i < vals.Count ? (double)Num.ToFloat(vals[i]) : 0.0;
                r["slots"] = slots;
                r["values"] = (long)(vals != null ? vals.Count : 0);
                return r;
            }
            if (o.Type.Name == "BlendMaskListAsset")
            {
                ulong hk = o.GetKey("DofSetListKey");
                r["hierarchy"] = Ref(hk);
                GdObj h = hk != 0 ? Db.Get(hk) : null;
                Dictionary<ulong, GdObj> masks = new Dictionary<ulong, GdObj>();
                foreach (object m in (IList)o["MaskKeys"]) { GdObj mo = Db.Get(Num.ToKey(m)); if (mo != null) masks[mo.GetKey("DofSetKey")] = mo; }
                Dictionary<ulong, double> defs = new Dictionary<ulong, double>();
                IList dk = (IList)o["DefaultDofSetKeys"], dv = (IList)o["DefaultValues"];
                for (int i = 0; i < dk.Count; i++) defs[Num.ToKey(dk[i])] = i < dv.Count ? Num.ToFloat(dv[i]) : 0;
                List<object> ls = new List<object>();
                IList la = h != null ? h["LayoutAssets"] as IList : null;
                if (la != null)
                    foreach (object x in la)
                    {
                        ulong k = Num.ToKey(x);
                        JObject j = new JObject();
                        j["layout"] = Ref(k);
                        GdObj m;
                        if (masks.TryGetValue(k, out m)) j["mask"] = Ref(m.Key);
                        double dvv;
                        if (defs.TryGetValue(k, out dvv)) j["default"] = dvv;
                        j["slots"] = (long)LayoutSlots(k).Count;
                        ls.Add(j);
                    }
                r["layouts"] = ls;
                return r;
            }
            throw new ApiError(NameOf(o) + " is not a blend mask");
        }

        // ------------------------------------------------------------------ expression dataflow
        /// <summary>the expression kernel's dataflow as a graph (KernelGraph)</summary>
        public Graph ExprGraph(object key, bool dbl = false)
        {
            GdObj o = Root(key);
            if (o.Type.Name != "ExpressionAsset") throw new ApiError(NameOf(o) + " is not an ExpressionAsset");
            Kernel k;
            try { k = Kernel.Parse(Kernel.BlobOf(o, "ExpressionKernel" + (dbl ? "DBL" : ""))); }
            catch (KernelException e) { throw new ApiError(e.Message); }
            KernelBindings b = KernelBindings.FromObj(o, dbl);
            return KernelGraph.Build(k, b, Kernel.GsLabels(Db, b), "dataflow of " + NameOf(o) + (dbl ? " (DBL)" : ""));
        }

        // ------------------------------------------------------------------ steering
        /// <summary>a SteeringControllerAsset with its three axes side by side</summary>
        public JObject Steering(object key)
        {
            GdObj o = Root(key);
            if (o.Type.Name != "SteeringControllerAsset") throw new ApiError(NameOf(o) + " is not a SteeringControllerAsset");
            JObject r = new JObject();
            r["key"] = H(o.Key); r["name"] = NameOf(o);
            r["subject"] = Ref(o.GetKey("SubjectControllerAsset"));
            r["subject_init"] = Ref(o.GetKey("SubjectControllerInit"));
            r["enabled"] = Ref(o.GetKey("EnabledGameState"));
            r["joint"] = Ref(o.GetKey("SteeringJointId"));
            List<object> axes = new List<object>();
            List<string> fields = new List<string>();
            foreach (string ax in new[] { "Yaw", "Pitch", "Roll" })
            {
                JObject j = new JObject();
                j["axis"] = ax;
                GdObj a = Db.Get(o.GetKey(ax));
                j["asset"] = Ref(o.GetKey(ax));
                JObject vals = new JObject();
                if (a != null)
                    foreach (Field f in a.Type.Fields)
                    {
                        if (f.IsImplicit) continue;
                        if (!fields.Contains(f.Name)) fields.Add(f.Name);
                        object v = a.Values[f.Index];
                        if (f.FType == TypeIds.Key) { ulong k = Num.ToKey(v); vals[f.Name] = k != 0 ? (object)Ref(k) : null; }
                        else if (v is float[]) vals[f.Name] = string.Join(", ", ((float[])v).Select(x => JsonIO.F(x)));
                        else if (v is float) vals[f.Name] = JsonIO.F((float)v);
                        else vals[f.Name] = v is GdStruct || v is IList ? (object)"…" : v;
                    }
                j["values"] = vals;
                axes.Add(j);
            }
            r["axes"] = axes;
            r["fields"] = fields.Cast<object>().ToList();
            return r;
        }
    }
}
