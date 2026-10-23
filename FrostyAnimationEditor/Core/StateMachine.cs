using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace FrostyAnimationEditor.Core
{
    /// <summary>
    /// State machines ("state flow"): StateFlowControllerAsset, StateFlowNodeControllerAsset and
    /// StateFlowTransitionAsset - the authored graph, the derived runtime tables and graph edits.
    ///
    /// Derived data (Rebuild; the rule reproduces every one of the 1,622 nodes of the shipped file):
    ///   controller.Identifier = (key &amp; 0xFFFFFFFF) ^ (key >> 32); controller.UsesGateNodes = any node has NodeType 3;
    ///   node.FlattenedTransitions = a pre-order tree of FlattenedTransitionData (one top-level entry per transition;
    ///   conduits (NodeType 1) expand to their transitions, once per node; a node whose subject is a state machine
    ///   gets one nested child {its StartNode, Transition 0, IsNested}); FirstChildIndex/SiblingIndex = entry index,
    ///   or the entry count for none; GateNodeId = order of first appearance of the gate node, else 255;
    ///   BranchTypeIndex = index of BranchWindowTypeAsset in AllBranchTypes, else 1 (BranchAtEnd), 2
    ///   (BranchAfterFirstUpdate), 0; True/FalseConditionIndices = transition conditions then the target's entry
    ///   conditions, as indices into AllTransitionConditions (numbered in entry pre-order, Bool/Signal group first,
    ///   then EnumerationEnumeratorPair); AllBranchTypes = [0, 0, 0] + distinct BranchWindowTypeAssets.
    ///   A conduit that is no controller's StartNode has empty tables.
    /// Runtime: FUN_145fe1c50 loads FlattenedTransitions into 0x30-byte entries; FUN_145fdf690 loads conditions.
    /// </summary>
    public static class StateMachine
    {
        public const string SFC = "StateFlowControllerAsset";
        public const string NODE = "StateFlowNodeControllerAsset";
        public const string TRANS = "StateFlowTransitionAsset";
        public const string FTD = "FlattenedTransitionData";
        public const long NodeState = 0, NodeConduit = 1, NodeGate = 3;

        private static readonly HashSet<string>[] Groups =
        {
            new HashSet<string> { "BoolAsset", "SignalAsset" },
            new HashSet<string> { "EnumerationEnumeratorPair" },
        };

        public sealed class Index
        {
            public readonly AntDb Db;
            public readonly Dictionary<ulong, GdObj> Nodes = new Dictionary<ulong, GdObj>();
            public readonly List<ulong> NodeOrder = new List<ulong>();
            public readonly Dictionary<ulong, GdObj> Sfcs = new Dictionary<ulong, GdObj>();
            public readonly List<ulong> SfcOrder = new List<ulong>();
            public readonly Dictionary<ulong, GdObj> Trans = new Dictionary<ulong, GdObj>();
            public readonly HashSet<ulong> Starts = new HashSet<ulong>();
            private readonly Dictionary<ulong, string> tn = new Dictionary<ulong, string>();

            public Index(AntDb db)
            {
                Db = db;
                foreach (GdObj o in db.Objects(NODE)) { if (!Nodes.ContainsKey(o.Key)) NodeOrder.Add(o.Key); Nodes[o.Key] = o; }
                foreach (GdObj o in db.Objects(SFC)) { if (!Sfcs.ContainsKey(o.Key)) SfcOrder.Add(o.Key); Sfcs[o.Key] = o; }
                foreach (GdObj o in db.Objects(TRANS)) Trans[o.Key] = o;
                foreach (GdObj s in Sfcs.Values)
                {
                    ulong st = s.GetKey("StartNode");
                    if (st != 0) Starts.Add(st);
                }
            }

            public string TypeName(ulong key)
            {
                string t;
                if (!tn.TryGetValue(key, out t))
                {
                    int i = Db.BlockOf(key);
                    t = i >= 0 && !Db.Removed.Contains(i) ? Db.TypeName(i) : "-";
                    tn[key] = t;
                }
                return t;
            }
        }

        public sealed class Entry
        {
            public ulong TransitionNode, Transition, GateNodeParams;
            public bool IsNested;
            public long GateNodeId = 255;
            public long? FirstChildIndex, SiblingIndex;
            public long BranchTypeIndex;
            public List<long> TrueConditionIndices = new List<long>();
            public List<long> FalseConditionIndices = new List<long>();
            public List<int> Kids = new List<int>();
        }

        public sealed class Derived
        {
            public List<ulong> Conds = new List<ulong>();
            public List<ulong> BranchTypes = new List<ulong>();
            public List<Entry> Entries = new List<Entry>();
        }

        private static List<ulong> Keys(object v)
        {
            List<ulong> r = new List<ulong>();
            IList l = v as IList;
            if (l != null) foreach (object x in l) r.Add(Num.ToKey(x));
            return r;
        }

        private static long NodeType(GdObj n) { return n.GetInt("NodeType"); }

        /// <summary>-> (AllTransitionConditions, AllBranchTypes, entries) for one StateFlowNodeControllerAsset</summary>
        public static Derived Derive(AntDb db, GdObj node, Index ix = null)
        {
            if (ix == null) ix = new Index(db);
            Derived d = new Derived();
            Dictionary<ulong, int> cidx = new Dictionary<ulong, int>();
            Dictionary<ulong, int> bidx = new Dictionary<ulong, int>();
            Dictionary<ulong, int> gates = new Dictionary<ulong, int>();
            Dictionary<ulong, long> memo = new Dictionary<ulong, long>();
            d.BranchTypes.AddRange(new ulong[] { 0, 0, 0 });
            List<Entry> entries = d.Entries;

            Func<ulong, int> ci = c =>
            {
                int k;
                if (!cidx.TryGetValue(c, out k)) { k = d.Conds.Count; cidx[c] = k; d.Conds.Add(c); }
                return k;
            };
            Func<ulong, int> bi = b =>
            {
                int k;
                if (!bidx.TryGetValue(b, out k)) { k = d.BranchTypes.Count; bidx[b] = k; d.BranchTypes.Add(b); }
                return k;
            };

            Func<GdObj, bool, List<Tuple<ulong, ulong, bool>>> childrenOf = (n, nested) =>
            {
                List<Tuple<ulong, ulong, bool>> r = new List<Tuple<ulong, ulong, bool>>();
                if (n == null) return r;
                if (NodeType(n) == NodeConduit)
                {
                    foreach (ulong t2 in Keys(n["Transitions"]))
                    {
                        GdObj tr;
                        if (ix.Trans.TryGetValue(t2, out tr)) r.Add(Tuple.Create(tr.GetKey("To"), t2, nested));
                    }
                    return r;
                }
                GdObj s;
                if (ix.Sfcs.TryGetValue(n.GetKey("SubjectController"), out s) && s.GetKey("StartNode") != 0)
                    r.Add(Tuple.Create(s.GetKey("StartNode"), 0UL, true));
                return r;
            };

            Func<ulong, ulong, bool, int> add = null;
            add = (target, trKey, nested) =>
            {
                Entry e = new Entry { TransitionNode = target, Transition = trKey, IsNested = nested };
                int idx = entries.Count;
                entries.Add(e);
                GdObj n;
                ix.Nodes.TryGetValue(target, out n);
                List<Tuple<List<ulong>, List<ulong>>> sources = new List<Tuple<List<ulong>, List<ulong>>>();
                if (trKey != 0)
                {
                    GdObj t;
                    if (ix.Trans.TryGetValue(trKey, out t))
                    {
                        sources.Add(Tuple.Create(Keys(t["ConditionsRequiredFalse"]), Keys(t["ConditionsRequiredTrue"])));
                        if (t.GetKey("BranchWindowTypeAsset") != 0) e.BranchTypeIndex = bi(t.GetKey("BranchWindowTypeAsset"));
                        else if (t.GetBool("BranchAtEnd")) e.BranchTypeIndex = 1;
                        else if (t.GetBool("BranchAfterFirstUpdate")) e.BranchTypeIndex = 2;
                    }
                }
                if (n != null) sources.Add(Tuple.Create(Keys(n["EntryConditionsRequiredFalse"]), Keys(n["EntryConditionsRequiredTrue"])));
                List<ulong> cf = new List<ulong>(), ct = new List<ulong>();
                foreach (Tuple<List<ulong>, List<ulong>> src in sources)
                {
                    foreach (HashSet<string> grp in Groups)
                    {
                        foreach (ulong c in src.Item1) if (grp.Contains(ix.TypeName(c))) ci(c);
                        foreach (ulong c in src.Item2) if (grp.Contains(ix.TypeName(c))) ci(c);
                    }
                    cf.AddRange(src.Item1);
                    ct.AddRange(src.Item2);
                }
                foreach (ulong c in cf) e.FalseConditionIndices.Add(ci(c));
                foreach (ulong c in ct) e.TrueConditionIndices.Add(ci(c));
                if (n != null && NodeType(n) == NodeGate)
                {
                    int g;
                    if (!gates.TryGetValue(target, out g)) { g = gates.Count; gates[target] = g; }
                    e.GateNodeId = g;
                }
                List<Tuple<ulong, ulong, bool>> kids = childrenOf(n, nested);
                if (kids.Count > 0)
                {
                    bool conduit = NodeType(n) == NodeConduit;
                    long m;
                    if (conduit && memo.TryGetValue(target, out m)) e.FirstChildIndex = m;
                    else
                    {
                        if (conduit) memo[target] = entries.Count;
                        foreach (Tuple<ulong, ulong, bool> k in kids) e.Kids.Add(add(k.Item1, k.Item2, k.Item3));
                        e.FirstChildIndex = e.Kids[0];
                    }
                }
                return idx;
            };

            if (NodeType(node) == NodeConduit && !ix.Starts.Contains(node.Key))
            {
                d.BranchTypes.Clear();
                return d;
            }
            List<int> top = new List<int>();
            foreach (ulong t in Keys(node["Transitions"]))
            {
                GdObj tr;
                if (ix.Trans.TryGetValue(t, out tr)) top.Add(add(tr.GetKey("To"), t, false));
            }
            int nAll = entries.Count;
            if (nAll > 254)
                throw new GdException("node '" + node.Name + "': " + nAll + " flattened transitions (the runtime stores indices as bytes, max 254)");
            Action<List<int>> link = lst =>
            {
                for (int k = 0; k < lst.Count; k++) entries[lst[k]].SiblingIndex = k + 1 < lst.Count ? lst[k + 1] : nAll;
            };
            link(top);
            foreach (Entry e in entries)
            {
                if (!e.FirstChildIndex.HasValue) e.FirstChildIndex = nAll;
                if (e.Kids.Count > 0) link(e.Kids);
            }
            return d;
        }

        private static List<long> Longs(object v)
        {
            List<long> r = new List<long>();
            if (v is byte[]) { foreach (byte b in (byte[])v) r.Add(b); return r; }
            IList l = v as IList;
            if (l != null) foreach (object x in l) r.Add(Num.ToLong(x));
            return r;
        }

        public static bool Same(GdObj node, Derived d)
        {
            if (!Keys(node["AllTransitionConditions"]).SequenceEqual(d.Conds)) return false;
            if (!Keys(node["AllBranchTypes"]).SequenceEqual(d.BranchTypes)) return false;
            IList ft = node["FlattenedTransitions"] as IList ?? new object[0];
            if (ft.Count != d.Entries.Count) return false;
            for (int i = 0; i < ft.Count; i++)
            {
                GdStruct f = ft[i] as GdStruct;
                Entry e = d.Entries[i];
                if (f == null) return false;
                if (f.GetKey("TransitionNode") != e.TransitionNode || f.GetKey("Transition") != e.Transition || f.GetKey("GateNodeParams") != e.GateNodeParams) return false;
                if (f.GetBool("IsNested") != e.IsNested) return false;
                if (f["GateNodeId"] == null || f.GetInt("GateNodeId") != e.GateNodeId) return false;
                if (f["FirstChildIndex"] == null || f.GetInt("FirstChildIndex") != e.FirstChildIndex.Value) return false;
                if (f["SiblingIndex"] == null || f.GetInt("SiblingIndex") != e.SiblingIndex.Value) return false;
                if (f["BranchTypeIndex"] == null || f.GetInt("BranchTypeIndex") != e.BranchTypeIndex) return false;
                if (!Longs(f["TrueConditionIndices"]).SequenceEqual(e.TrueConditionIndices)) return false;
                if (!Longs(f["FalseConditionIndices"]).SequenceEqual(e.FalseConditionIndices)) return false;
            }
            return true;
        }

        private static uint Ident(ulong k) { return (uint)((k & 0xFFFFFFFF) ^ (k >> 32)); }

        /// <summary>[(key, name)] of nodes / controllers whose derived data differs from the rule</summary>
        public static List<Tuple<ulong, string>> Verify(AntDb db)
        {
            Index ix = new Index(db);
            List<Tuple<ulong, string>> bad = new List<Tuple<ulong, string>>();
            foreach (ulong k in ix.NodeOrder)
            {
                GdObj n = ix.Nodes[k];
                if (!Same(n, Derive(db, n, ix))) bad.Add(Tuple.Create(k, n.Name));
            }
            HashSet<ulong> gates = GateControllers(ix);
            foreach (ulong k in ix.SfcOrder)
            {
                GdObj s = ix.Sfcs[k];
                if (s["Identifier"] == null || s.GetInt("Identifier") != Ident(k) || s.GetBool("UsesGateNodes") != gates.Contains(k))
                    bad.Add(Tuple.Create(k, s.Name));
            }
            return bad;
        }

        private static HashSet<ulong> GateControllers(Index ix)
        {
            HashSet<ulong> g = new HashSet<ulong>();
            foreach (GdObj n in ix.Nodes.Values) if (NodeType(n) == NodeGate) g.Add(n.GetKey("StateFlowController"));
            return g;
        }

        /// <summary>recompute every derived state machine field; only objects whose value changes are touched</summary>
        public static int Rebuild(AntDb db, Action<string> log = null)
        {
            Index ix = new Index(db);
            TypeDesc ftd = db.Types[FTD];
            int changed = 0;
            foreach (ulong k in ix.NodeOrder)
            {
                GdObj n = ix.Nodes[k];
                Derived d = Derive(db, n, ix);
                if (Same(n, d)) continue;
                n["AllTransitionConditions"] = new List<ulong>(d.Conds);
                n["AllBranchTypes"] = new List<ulong>(d.BranchTypes);
                List<GdStruct> fl = new List<GdStruct>();
                foreach (Entry e in d.Entries)
                {
                    GdStruct s = new GdStruct(ftd);
                    s["TransitionNode"] = e.TransitionNode;
                    s["Transition"] = e.Transition;
                    s["GateNodeParams"] = e.GateNodeParams;
                    s["IsNested"] = e.IsNested;
                    s["GateNodeId"] = e.GateNodeId;
                    s["FirstChildIndex"] = e.FirstChildIndex.Value;
                    s["SiblingIndex"] = e.SiblingIndex.Value;
                    s["BranchTypeIndex"] = e.BranchTypeIndex;
                    s["TrueConditionIndices"] = ListFor(ftd.GetField("TrueConditionIndices"), e.TrueConditionIndices);
                    s["FalseConditionIndices"] = ListFor(ftd.GetField("FalseConditionIndices"), e.FalseConditionIndices);
                    fl.Add(s);
                }
                n["FlattenedTransitions"] = fl;
                changed++;
                if (log != null) log("state machine tables rebuilt: " + n.Name);
            }
            HashSet<ulong> gates = GateControllers(ix);
            foreach (ulong k in ix.SfcOrder)
            {
                GdObj s = ix.Sfcs[k];
                uint ident = Ident(k);
                if (s["Identifier"] == null || s.GetInt("Identifier") != ident || s.GetBool("UsesGateNodes") != gates.Contains(k))
                {
                    s["Identifier"] = (long)ident;
                    s["UsesGateNodes"] = gates.Contains(k);
                    changed++;
                }
            }
            return changed;
        }

        private static object ListFor(Field f, List<long> v)
        {
            object e = ObjCodec.EmptyList(f);
            if (e is byte[]) return v.Select(x => (byte)x).ToArray();
            return new List<long>(v);
        }

        /// <summary>true when any state machine object was decoded/edited/added/removed (Save then rebuilds)</summary>
        public static bool Touched(AntDb db)
        {
            foreach (int i in db.Cache.Keys.Concat(db.NewObjs.Keys).Concat(db.Removed))
            {
                string t = db.TypeName(i);
                if (t == SFC || t == NODE || t == TRANS) return true;
            }
            return false;
        }

        // ------------------------------------------------------------------ queries
        public static ulong ControllerOf(AntDb db, ulong nodeKey)
        {
            GdObj n = db.Get(nodeKey);
            return n != null ? n.GetKey("StateFlowController") : 0;
        }

        public static List<GdObj> States(AntDb db, ulong sfcKey)
        {
            List<GdObj> r = new List<GdObj>();
            foreach (int i in db.BlockIds(NODE))
                if (Num.ToKey(db.ScalarField(i, "StateFlowController")) == sfcKey) r.Add(db.Obj(i));
            return r;
        }

        public static List<GdObj> Transitions(AntDb db, ulong sfcKey)
        {
            HashSet<ulong> keys = new HashSet<ulong>(States(db, sfcKey).Select(o => o.Key));
            List<GdObj> r = new List<GdObj>();
            foreach (int i in db.BlockIds(TRANS))
                if (keys.Contains(Num.ToKey(db.ScalarField(i, "From")))) r.Add(db.Obj(i));
            return r;
        }

        private static string Nm(AntDb db, ulong k)
        {
            if (k == 0) return "-";
            int i = db.BlockOf(k);
            return i >= 0 ? "'" + db.NameOf(i) + "'" : BE.KeyHex(k);
        }

        public static string NodeKind(long t)
        {
            return t == NodeState ? "state" : t == NodeConduit ? "conduit" : t == NodeGate ? "gate" : t.ToString();
        }

        /// <summary>text outline of a state machine</summary>
        public static string Describe(AntDb db, ulong sfcKey)
        {
            GdObj s = db.Get(sfcKey);
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("state machine '" + s.Name + "' (key " + BE.KeyHex(sfcKey) + "), start node " + Nm(db, s.GetKey("StartNode")));
            foreach (GdObj n in States(db, sfcKey))
            {
                sb.AppendLine("  [" + NodeKind(NodeType(n)) + "] " + n.Name + "  (" + BE.KeyHex(n.Key) + ")  subject: " + Nm(db, n.GetKey("SubjectController")));
                foreach (ulong t in Keys(n["Transitions"]))
                {
                    GdObj tr = db.Get(t);
                    if (tr == null) continue;
                    List<string> cond = Keys(tr["ConditionsRequiredTrue"]).Select(c => Nm(db, c)).ToList();
                    cond.AddRange(Keys(tr["ConditionsRequiredFalse"]).Select(c => "!" + Nm(db, c)));
                    List<string> flags = new[] { "BranchAtEnd", "BranchInAny", "BranchAfterFirstUpdate" }.Where(f => tr.GetBool(f)).ToList();
                    sb.AppendLine("      -> " + Nm(db, tr.GetKey("To")) + "   " + (cond.Count > 0 ? string.Join(" & ", cond) : "always") +
                                  (flags.Count > 0 ? "  [" + string.Join(",", flags) + "]" : ""));
                }
            }
            return sb.ToString();
        }

        // ------------------------------------------------------------------ edits
        private static GdObj Template(AntDb db, string typeName, ulong near)
        {
            if (near != 0)
            {
                GdObj o = db.Get(near);
                if (o != null && o.Type.Name == typeName) return o;
            }
            foreach (int i in db.BlockIds(typeName)) return db.Obj(i);
            throw new GdException("no " + typeName + " in the file to copy defaults from");
        }

        /// <summary>new StateFlowNodeControllerAsset in controller sfcKey; returns its key</summary>
        public static ulong AddState(AntDb db, ulong sfcKey, string name, ulong subject = 0, ulong initParams = 0, long nodeType = NodeState,
                                     ulong template = 0, IList<ulong> entryTrue = null, IList<ulong> entryFalse = null, IList<ulong> activeSignals = null)
        {
            GdObj sfc = db.Get(sfcKey);
            if (sfc == null || sfc.Type.Name != SFC) throw new GdException(BE.KeyHex(sfcKey) + " is not a " + SFC);
            if (template == 0)
            {
                template = sfc.GetKey("StartNode");
                if (template == 0)
                    foreach (int i in db.BlockIds(NODE))
                        if (Num.ToKey(db.ScalarField(i, "StateFlowController")) == sfcKey) { template = db.Keys[i]; break; }
            }
            GdObj tpl = Template(db, NODE, template);
            GdObj n = db.Clone(tpl, true, name, false);
            n["StateFlowController"] = sfcKey;
            n["SubjectController"] = subject;
            n["SubjectControllerInitParams"] = initParams;
            n["NodeType"] = nodeType;
            n["Transitions"] = new List<ulong>();
            n["TransitionLinks"] = new List<ulong>();
            n["EntryConditionsRequiredTrue"] = new List<ulong>(entryTrue ?? new ulong[0]);
            n["EntryConditionsRequiredFalse"] = new List<ulong>(entryFalse ?? new ulong[0]);
            n["ActiveSignals"] = new List<ulong>(activeSignals ?? new ulong[0]);
            n["AllTransitionConditions"] = new List<ulong>();
            n["AllBranchTypes"] = new List<ulong>();
            n["FlattenedTransitions"] = new List<GdStruct>();
            n["EnumValue"] = 0L;
            n["StateOpAsset"] = 0UL;
            db.Add(n, tpl.Key);
            Rebuild(db);
            return n.Key;
        }

        public sealed class TransitionSpec
        {
            public string Name;
            public IList<ulong> True, False;
            public bool BranchAtEnd, BranchInAny, BranchAfterFirstUpdate;
            public ulong BranchWindow, BranchInWindow, OverrideBlend, Template, Link;
            public float DestinationPhase;
            public int? Position;
        }

        /// <summary>new StateFlowTransitionAsset From -> To, inserted in the source node's Transitions (order = priority)</summary>
        public static ulong AddTransition(AntDb db, ulong fromKey, ulong toKey, TransitionSpec spec = null)
        {
            if (spec == null) spec = new TransitionSpec();
            GdObj a = db.Get(fromKey), b = db.Get(toKey);
            if (a == null || a.Type.Name != NODE) throw new GdException(BE.KeyHex(fromKey) + " is not a " + NODE);
            if (b == null || b.Type.Name != NODE) throw new GdException(BE.KeyHex(toKey) + " is not a " + NODE);
            bool userTemplate = spec.Template != 0;
            ulong template = spec.Template;
            if (!userTemplate) { List<ulong> tl = Keys(a["Transitions"]); template = tl.Count > 0 ? tl[0] : 0; }
            GdObj tpl = Template(db, TRANS, template);
            GdObj t = db.Clone(tpl, true, spec.Name ?? ("Transition " + a.Name + " to " + b.Name), false);
            t["From"] = fromKey;
            t["To"] = toKey;
            t["ConditionsRequiredTrue"] = new List<ulong>(spec.True ?? new ulong[0]);
            t["ConditionsRequiredFalse"] = new List<ulong>(spec.False ?? new ulong[0]);
            t["BranchAtEnd"] = spec.BranchAtEnd;
            t["BranchInAny"] = spec.BranchInAny;
            t["BranchAfterFirstUpdate"] = spec.BranchAfterFirstUpdate;
            t["BranchWindowTypeAsset"] = spec.BranchWindow;
            t["BranchInWindowTypeAsset"] = spec.BranchInWindow;
            t["OverrideBlend"] = spec.OverrideBlend;
            t["DestinationPhase"] = spec.DestinationPhase;
            if (!userTemplate)
            {
                t["TagCollectionSet"] = 0UL;
                t["DestinationPhaseSignal"] = 0UL;
                t["BranchInPhase"] = 0L;
                t["BranchAtEndOffset"] = 0L;
            }
            db.Add(t, tpl.Key);
            List<ulong> tr = Keys(a["Transitions"]);
            List<ulong> links = Keys(a["TransitionLinks"]);
            if (links.Count == 0) links = tr.Select(x => 0UL).ToList();
            int pos = spec.Position.HasValue ? Math.Max(0, Math.Min(spec.Position.Value, tr.Count)) : tr.Count;
            tr.Insert(pos, t.Key);
            links.Insert(Math.Min(pos, links.Count), spec.Link);
            a["Transitions"] = tr;
            a["TransitionLinks"] = links;
            Rebuild(db);
            return t.Key;
        }

        public static void RemoveTransition(AntDb db, ulong trKey, bool deleteObject = true, bool rebuild = true)
        {
            if (db.Get(trKey) == null) throw new KeyNotFoundException(BE.KeyHex(trKey));
            foreach (int i in db.BlockIds(NODE))
            {
                GdObj n = db.Obj(i);
                List<ulong> tr = Keys(n["Transitions"]);
                int j = tr.IndexOf(trKey);
                if (j < 0) continue;
                List<ulong> links = Keys(n["TransitionLinks"]);
                if (links.Count == 0) links = tr.Select(x => 0UL).ToList();
                tr.RemoveAt(j);
                if (j < links.Count) links.RemoveAt(j);
                links = links.Select(l => l == trKey ? 0UL : l).ToList();
                n["Transitions"] = tr;
                n["TransitionLinks"] = links;
            }
            if (deleteObject) db.Remove(trKey);
            if (rebuild) Rebuild(db);
        }

        /// <summary>change a transition's priority (its index in the source node's Transitions)</summary>
        public static void MoveTransition(AntDb db, ulong trKey, int position)
        {
            GdObj t = db.Get(trKey);
            GdObj n = db.Get(t.GetKey("From"));
            List<ulong> tr = Keys(n["Transitions"]);
            List<ulong> links = Keys(n["TransitionLinks"]);
            if (links.Count == 0) links = tr.Select(x => 0UL).ToList();
            int j = tr.IndexOf(trKey);
            ulong k = tr[j], l = links[j];
            tr.RemoveAt(j);
            links.RemoveAt(j);
            position = Math.Max(0, Math.Min(position, tr.Count));
            tr.Insert(position, k);
            links.Insert(position, l);
            n["Transitions"] = tr;
            n["TransitionLinks"] = links;
            Rebuild(db);
        }

        public static void RetargetTransition(AntDb db, ulong trKey, ulong? toKey = null, ulong? fromKey = null)
        {
            GdObj t = db.Get(trKey);
            if (toKey.HasValue) t["To"] = toKey.Value;
            if (fromKey.HasValue && fromKey.Value != t.GetKey("From"))
            {
                GdObj old = db.Get(t.GetKey("From"));
                List<ulong> tr = Keys(old["Transitions"]);
                List<ulong> links = Keys(old["TransitionLinks"]);
                if (links.Count == 0) links = tr.Select(x => 0UL).ToList();
                int j = tr.IndexOf(trKey);
                tr.RemoveAt(j);
                links.RemoveAt(j);
                old["Transitions"] = tr;
                old["TransitionLinks"] = links;
                GdObj nw = db.Get(fromKey.Value);
                List<ulong> ntr = Keys(nw["Transitions"]);
                ntr.Add(trKey);
                List<ulong> nl = Keys(nw["TransitionLinks"]);
                nl.Add(0);
                nw["Transitions"] = ntr;
                nw["TransitionLinks"] = nl;
                t["From"] = fromKey.Value;
            }
            Rebuild(db);
        }

        /// <summary>delete a node and every transition from or to it</summary>
        public static List<ulong> RemoveState(AntDb db, ulong nodeKey, bool deleteTransitions = true)
        {
            GdObj n = db.Get(nodeKey);
            if (n == null) throw new KeyNotFoundException(BE.KeyHex(nodeKey));
            foreach (GdObj s in db.Objects(SFC))
                if (s.GetKey("StartNode") == nodeKey)
                    throw new GdException("'" + n.Name + "' is the start node of '" + s.Name + "': set another start node first");
            List<ulong> dead = new List<ulong>();
            foreach (GdObj t in db.Objects(TRANS))
                if (t.GetKey("From") == nodeKey || t.GetKey("To") == nodeKey) dead.Add(t.Key);
            foreach (ulong k in dead) RemoveTransition(db, k, deleteTransitions, false);
            db.Remove(nodeKey);
            Rebuild(db);
            return dead;
        }

        public static void SetStart(AntDb db, ulong sfcKey, ulong nodeKey)
        {
            GdObj s = db.Get(sfcKey);
            GdObj n = db.Get(nodeKey);
            if (n == null || n.GetKey("StateFlowController") != sfcKey) throw new GdException("the start node must belong to the controller");
            s["StartNode"] = nodeKey;
            Rebuild(db);
        }

        /// <summary>a new empty state machine (controller + one start state); returns (controller key, start key)</summary>
        public static Tuple<ulong, ulong> NewController(AntDb db, string name, ulong template = 0, string startName = "Start", ulong startSubject = 0)
        {
            GdObj tpl = Template(db, SFC, template);
            GdObj s = db.Clone(tpl, true, name, false);
            s["StartNode"] = 0UL;
            db.Add(s, tpl.Key);
            ulong start = AddState(db, s.Key, startName, startSubject);
            s["StartNode"] = start;
            Rebuild(db);
            return Tuple.Create(s.Key, start);
        }
    }
}
