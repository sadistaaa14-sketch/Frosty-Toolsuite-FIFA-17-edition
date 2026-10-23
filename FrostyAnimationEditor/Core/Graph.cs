using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace FrostyAnimationEditor.Core
{
    public sealed class GraphNode
    {
        public ulong Key;
        public string Type, Name, State, Role;
        public int Depth, Layer;
        public double X, Y, W, H;
        public bool Placed;
        public List<string> Info = new List<string>();
        public string KeyHex { get { return BE.KeyHex(Key); } }
    }

    public sealed class GraphEdge
    {
        public ulong S, T;
        public string Label, Kind;
        public ulong Transition;
    }

    /// <summary>
    /// Reference graphs of the file's objects ("blueprints"): every connection in the file is a Key field (an object's
    /// 64-bit __key stored in another object), so a graph is a walk over Key values.
    /// </summary>
    public sealed class Graph
    {
        public string Title;
        public readonly List<ulong> Order = new List<ulong>();
        public readonly Dictionary<ulong, GraphNode> Nodes = new Dictionary<ulong, GraphNode>();
        public readonly List<GraphEdge> Edges = new List<GraphEdge>();
        public bool Truncated;
        public string LayoutKind = "";

        /// <summary>classes that are referenced by almost everything and only add noise to a neighbourhood graph</summary>
        public static readonly HashSet<string> NoiseTypes = new HashSet<string> { "TagCollectionSetAsset" };

        public IEnumerable<GraphNode> NodeList { get { return Order.Select(k => Nodes[k]); } }

        /// <summary>(type name, object name, state) of a key: state is "ok", "imported" (in table A: supplied by another
        /// container at runtime) or "missing"</summary>
        public static void NodeInfo(AntDb db, ulong key, out string type, out string name, out string state)
        {
            int i = db.BlockOf(key);
            if (i >= 0 && !db.Removed.Contains(i)) { type = db.TypeName(i); name = db.NameOf(i); state = "ok"; return; }
            if (db.OrigA.Any(r => r.Key == key)) { type = "AnimationAsset"; name = "(imported from another container)"; state = "imported"; return; }
            type = "?"; name = "(not in this file)"; state = "missing";
        }

        public GraphNode AddNode(AntDb db, ulong key, int depth, string role = null)
        {
            GraphNode n;
            if (!Nodes.TryGetValue(key, out n))
            {
                string tn, nm, st;
                NodeInfo(db, key, out tn, out nm, out st);
                n = new GraphNode { Key = key, Type = tn, Name = nm, State = st, Depth = depth, Role = role };
                Nodes[key] = n;
                Order.Add(key);
            }
            else
            {
                n.Depth = Math.Min(n.Depth, depth);
                if (role != null && n.Role == null) n.Role = role;
            }
            return n;
        }

        private void AddEdge(ulong s, ulong t, string label, string kind, HashSet<string> seen)
        {
            string sig = s.ToString("x16") + "|" + t.ToString("x16") + "|" + label;
            if (seen != null)
            {
                if (seen.Contains(sig)) return;
                seen.Add(sig);
            }
            Edges.Add(new GraphEdge { S = s, T = t, Label = label, Kind = kind });
        }

        /// <summary>'ActorControllerAsset.Tracks[0].Anims[3].Asset' -> 'Tracks[0].Anims[3].Asset'</summary>
        private static string GenericLabel(string path)
        {
            int d = path.IndexOf('.');
            return d >= 0 ? path.Substring(d + 1) : path;
        }

        private static string Describe(AntDb db, ulong k)
        {
            string tn, nm, st;
            NodeInfo(db, k, out tn, out nm, out st);
            return tn + " " + PyFormat.Repr(nm);
        }

        /// <summary>breadth-first walk over Key references from the roots.
        /// depth: reference hops to follow; incoming: also follow references to each visited object; maxNodes: stop adding
        /// nodes after this many (Truncated is set); skipTypes: classes not added; followTypes: if given, only nodes of these
        /// classes are expanded further</summary>
        public static Graph Build(AntDb db, IList<ulong> roots, int depth = 2, bool incoming = false, int maxNodes = 600,
                                  ICollection<string> skipTypes = null, ICollection<string> followTypes = null, Action<int, int> progress = null)
        {
            if (skipTypes == null) skipTypes = NoiseTypes;
            List<ulong> rs = roots.Where(k => k != 0).ToList();
            Graph g = new Graph { Title = "references of " + string.Join(", ", rs.Take(3).Select(k => Describe(db, k))) };
            HashSet<string> seen = new HashSet<string>();
            Queue<Tuple<ulong, int>> q = new Queue<Tuple<ulong, int>>();
            foreach (ulong k in rs)
            {
                g.AddNode(db, k, 0, "root");
                q.Enqueue(Tuple.Create(k, 0));
            }
            HashSet<ulong> expanded = new HashSet<ulong>();
            if (incoming && progress != null) db.BuildRefIndex(progress);
            while (q.Count > 0)
            {
                Tuple<ulong, int> it = q.Dequeue();
                ulong k = it.Item1;
                int d = it.Item2;
                if (expanded.Contains(k) || d >= depth) continue;
                expanded.Add(k);
                int i = db.BlockOf(k);
                if (i < 0 || db.Removed.Contains(i)) continue;
                GraphNode n = g.Nodes[k];
                if (followTypes != null && d > 0 && !followTypes.Contains(n.Type)) continue;
                GdObj o = db.Obj(i);
                foreach (KeyRef r in db.RefsFrom(o))
                {
                    if (r.Key == 0) continue;
                    string tn, nm, st;
                    NodeInfo(db, r.Key, out tn, out nm, out st);
                    if (skipTypes.Contains(tn)) continue;
                    if (!g.Nodes.ContainsKey(r.Key))
                    {
                        if (g.Nodes.Count >= maxNodes) { g.Truncated = true; continue; }
                        g.AddNode(db, r.Key, d + 1);
                        q.Enqueue(Tuple.Create(r.Key, d + 1));
                    }
                    g.AddEdge(k, r.Key, GenericLabel(r.Path), "ref", seen);
                }
                if (incoming)
                {
                    foreach (int j in db.RefsTo(k))
                    {
                        if (db.Removed.Contains(j)) continue;
                        ulong src = db.Keys[j];
                        string tn = db.TypeName(j);
                        if (skipTypes.Contains(tn)) continue;
                        foreach (KeyRef r in db.RefsFrom(db.Obj(j)))
                        {
                            if (r.Key != k) continue;
                            if (!g.Nodes.ContainsKey(src))
                            {
                                if (g.Nodes.Count >= maxNodes) { g.Truncated = true; continue; }
                                g.AddNode(db, src, d + 1);
                                q.Enqueue(Tuple.Create(src, d + 1));
                            }
                            g.AddEdge(src, k, GenericLabel(r.Path), "ref", seen);
                        }
                    }
                }
            }
            return g;
        }

        /// <summary>keys of the StateFlowNodeControllerAsset objects whose StateFlowController is sfcKey</summary>
        public static List<ulong> StateMachineNodes(AntDb db, ulong sfcKey)
        {
            List<ulong> r = new List<ulong>();
            foreach (int i in db.BlockIds("StateFlowNodeControllerAsset"))
            {
                object v = db.ScalarField(i, "StateFlowController");
                if (v != null && Num.ToKey(v) == sfcKey) r.Add(db.Keys[i]);
            }
            return r;
        }

        /// <summary>the state machine of one StateFlowControllerAsset: the controller, its StateFlowNodeControllerAssets (and
        /// each node's SubjectController); edges From -> To of the transitions (label = name, flags, conditions), controller
        /// -> StartNode ("start"), node -> SubjectController ("subject")</summary>
        public static Graph StateMachine(AntDb db, ulong sfcKey, bool subjects = true, bool conditions = true)
        {
            GdObj sfc = db.Get(sfcKey);
            if (sfc == null) throw new KeyNotFoundException(BE.KeyHex(sfcKey));
            Graph g = new Graph { Title = "state machine " + PyFormat.Repr(sfc.Name ?? ""), LayoutKind = "lr" };
            g.AddNode(db, sfcKey, 0, "root");
            ulong start = sfc.Has("StartNode") ? sfc.GetKey("StartNode") : 0;
            List<ulong> members = StateMachineNodes(db, sfcKey);
            foreach (ulong k in members) g.AddNode(db, k, 1, "state");
            if (start != 0)
            {
                g.AddNode(db, start, 1).Role = "start";
                g.AddEdge(sfcKey, start, "start", "start", null);
            }
            SortedSet<ulong> trans = new SortedSet<ulong>();
            foreach (ulong k in members)
            {
                GdObj o = db.Get(k);
                foreach (string f in new[] { "Transitions", "TransitionLinks" })
                    if (o.Has(f)) foreach (ulong t in o.GetList<ulong>(f) ?? new List<ulong>()) trans.Add(t);
                ulong subj = o.Has("SubjectController") ? o.GetKey("SubjectController") : 0;
                if (subjects && subj != 0)
                {
                    g.AddNode(db, subj, 2, "subject");
                    g.AddEdge(k, subj, "subject", "subject", null);
                }
            }
            HashSet<string> seen = new HashSet<string>();
            foreach (ulong t in trans)
            {
                GdObj tr = db.Get(t);
                if (tr == null) continue;
                ulong a = tr.GetKey("From"), b = tr.GetKey("To");
                if (a == 0 || b == 0) continue;
                foreach (ulong x in new[] { a, b }) if (!g.Nodes.ContainsKey(x)) g.AddNode(db, x, 1, "state");
                List<string> bits = new List<string> { string.IsNullOrEmpty(tr.Name) ? BE.KeyHex(t) : tr.Name };
                List<string> flags = new[] { "BranchAtEnd", "BranchInAny", "BranchAfterFirstUpdate" }.Where(n => tr.Has(n) && tr.GetBool(n)).ToList();
                if (flags.Count > 0) bits.Add(string.Join("+", flags));
                if (conditions)
                {
                    foreach (Tuple<string, string> fs in new[] { Tuple.Create("ConditionsRequiredTrue", ""), Tuple.Create("ConditionsRequiredFalse", "!") })
                    {
                        List<ulong> cs = tr.Has(fs.Item1) ? (tr.GetList<ulong>(fs.Item1) ?? new List<ulong>()) : new List<ulong>();
                        foreach (ulong c in cs.Take(4))
                        {
                            string tn, nm, st;
                            NodeInfo(db, c, out tn, out nm, out st);
                            bits.Add(fs.Item2 + nm);
                        }
                    }
                }
                string label = string.Join(" | ", bits);
                string sig = a.ToString("x16") + "|" + b.ToString("x16") + "|" + label;
                if (seen.Contains(sig)) continue;
                seen.Add(sig);
                g.Edges.Add(new GraphEdge { S = a, T = b, Label = label, Kind = "transition", Transition = t });
            }
            return g;
        }

        /// <summary>short text lines describing an object's own (and inherited) fields</summary>
        public static List<string> Summarize(AntDb db, GdStruct o, int maxLines = 60, int maxList = 12)
        {
            List<string> lines = new List<string>();
            Action<string, GdStruct, int> emit = null;
            emit = (prefix, st, depth) =>
            {
                foreach (Field f in st.Type.Fields)
                {
                    if (lines.Count >= maxLines) return;
                    if (f.Name == "__key" || f.Name == "__name") continue;
                    object v = st.Values[f.Index];
                    string name = prefix + f.Name;
                    if (f.Name == "__base" && v is GdStruct) { emit(prefix, (GdStruct)v, depth); continue; }
                    if (v is GdStruct)
                    {
                        GdStruct sv = (GdStruct)v;
                        if (depth < 2)
                        {
                            lines.Add(name + ": <" + sv.Type.Name + ">");
                            emit(name + ".", sv, depth + 1);
                        }
                        else lines.Add(name + ": <" + sv.Type.Name + " ...>");
                        continue;
                    }
                    if (f.FType == TypeIds.Key)
                    {
                        IList l = v as IList;
                        List<ulong> ks = l != null ? l.Cast<object>().Select(Num.ToKey).ToList() : new List<ulong> { v == null ? 0UL : Num.ToKey(v) };
                        if (l != null) lines.Add(name + ": " + l.Count + " keys");
                        for (int j = 0; j < ks.Count && j < maxList; j++)
                        {
                            if (ks[j] == 0)
                            {
                                lines.Add(l == null ? name + ": null" : "  [" + j + "] null");
                                continue;
                            }
                            string tn, nm, stt;
                            NodeInfo(db, ks[j], out tn, out nm, out stt);
                            string s = BE.KeyHex(ks[j]) + " " + tn + " " + PyFormat.Repr(nm);
                            lines.Add(l == null ? name + ": " + s : "  [" + j + "] " + s);
                        }
                        if (l != null && l.Count > maxList) lines.Add("  ... " + (l.Count - maxList) + " more");
                        continue;
                    }
                    IList vl = v as IList;
                    if (vl != null && vl.Count > 0 && vl[0] is GdStruct)
                    {
                        lines.Add(name + ": " + vl.Count + " x " + ((GdStruct)vl[0]).Type.Name);
                        continue;
                    }
                    string js = MiniJson.Serialize(JsonIO.FieldToJson(f, v), 0);
                    if (js.Length > 120) js = js.Substring(0, 117) + "...";
                    lines.Add(name + ": " + js);
                }
            };
            emit("", o, 0);
            if (lines.Count >= maxLines) lines.Add("...");
            return lines;
        }

        /// <summary>add the field lines of every node (shown by the graph views)</summary>
        public void AttachDetails(AntDb db, int maxLines = 60)
        {
            foreach (GraphNode n in NodeList)
            {
                GdObj o = db.Get(n.Key);
                n.Info = o != null ? Summarize(db, o, maxLines) : new List<string>();
            }
        }

        private static int CmpOrd(string a, string b) { return string.CompareOrdinal(a ?? "", b ?? ""); }

        /// <summary>layered left-to-right layout: layer = BFS distance from the roots along edges, order inside a layer by
        /// the barycenter heuristic; sets X, Y, W, H</summary>
        public Graph Layout(double nodeW = 210, double nodeH = 38, double gapX = 90, double gapY = 14)
        {
            List<ulong> keys = new List<ulong>(Order);
            Dictionary<ulong, List<ulong>> outE = keys.ToDictionary(k => k, k => new List<ulong>());
            Dictionary<ulong, List<ulong>> inE = keys.ToDictionary(k => k, k => new List<ulong>());
            foreach (GraphEdge e in Edges)
                if (Nodes.ContainsKey(e.S) && Nodes.ContainsKey(e.T) && e.S != e.T)
                {
                    outE[e.S].Add(e.T);
                    inE[e.T].Add(e.S);
                }
            Dictionary<ulong, int> layer = new Dictionary<ulong, int>();
            List<ulong> roots = keys.Where(k => Nodes[k].Role == "root").ToList();
            if (roots.Count == 0) roots = keys.Take(1).ToList();
            Queue<ulong> q = new Queue<ulong>();
            foreach (ulong r in roots) { layer[r] = 0; q.Enqueue(r); }
            while (q.Count > 0)
            {
                ulong k = q.Dequeue();
                foreach (ulong t in outE[k])
                    if (!layer.ContainsKey(t)) { layer[t] = layer[k] + 1; q.Enqueue(t); }
            }
            foreach (ulong k in keys)          // nodes only reachable backwards: place left of their target
            {
                if (layer.ContainsKey(k)) continue;
                Queue<ulong> q2 = new Queue<ulong>();
                q2.Enqueue(k);
                HashSet<ulong> seen = new HashSet<ulong> { k };
                int? found = null;
                while (q2.Count > 0 && !found.HasValue)
                {
                    ulong x = q2.Dequeue();
                    foreach (ulong t in outE[x].Concat(inE[x]))
                    {
                        if (layer.ContainsKey(t))
                        {
                            found = layer[t] + (inE[x].Contains(t) ? 1 : -1);
                            break;
                        }
                        if (!seen.Contains(t)) { seen.Add(t); q2.Enqueue(t); }
                    }
                }
                layer[k] = Math.Max(0, found.HasValue ? found.Value : (layer.Count > 0 ? layer.Values.Max() : 0) + 1);
            }
            int lo = layer.Count > 0 ? layer.Values.Min() : 0;
            Dictionary<int, List<ulong>> cols = new Dictionary<int, List<ulong>>();
            List<int> colOrder = new List<int>();
            foreach (ulong k in keys)
            {
                layer[k] -= lo;
                List<ulong> l;
                if (!cols.TryGetValue(layer[k], out l)) { l = new List<ulong>(); cols[layer[k]] = l; colOrder.Add(layer[k]); }
                l.Add(k);
            }
            Dictionary<ulong, int> pos = new Dictionary<ulong, int>();
            foreach (int c in cols.Keys.OrderBy(x => x).ToList())
            {
                cols[c] = cols[c].OrderBy(k => Nodes[k].Type, Comparer<string>.Create(CmpOrd)).ThenBy(k => Nodes[k].Name, Comparer<string>.Create(CmpOrd)).ToList();
                for (int n = 0; n < cols[c].Count; n++) pos[cols[c][n]] = n;
            }
            for (int it = 0; it < 6; it++)
            {
                List<int> rng = it % 2 == 0 ? cols.Keys.OrderBy(x => x).ToList() : cols.Keys.OrderByDescending(x => x).ToList();
                foreach (int c in rng)
                {
                    Dictionary<ulong, double> key = new Dictionary<ulong, double>();
                    foreach (ulong k in cols[c])
                    {
                        List<double> nb = new List<double>();
                        foreach (ulong t in (it % 2 == 0 ? inE[k] : outE[k])) if (pos.ContainsKey(t) && layer[t] != c) nb.Add(pos[t]);
                        double s = 0;
                        foreach (double x in nb) s += x;
                        key[k] = nb.Count > 0 ? s / nb.Count : pos[k];
                    }
                    cols[c] = cols[c].OrderBy(k => key[k]).ToList();
                    for (int n = 0; n < cols[c].Count; n++) pos[cols[c][n]] = n;
                }
            }
            int tallest = cols.Count > 0 ? cols.Values.Max(v => v.Count) : 1;
            foreach (int c in colOrder)
            {
                List<ulong> ks = cols[c];
                double off = (tallest - ks.Count) * (nodeH + gapY) / 2.0;
                for (int n = 0; n < ks.Count; n++)
                {
                    GraphNode nd = Nodes[ks[n]];
                    nd.X = c * (nodeW + gapX);
                    nd.Y = off + n * (nodeH + gapY);
                    nd.W = nodeW;
                    nd.H = nodeH;
                    nd.Layer = c;
                    nd.Placed = true;
                }
            }
            return this;
        }

        // ------------------------------------------------------------------ writers
        private static string DotId(ulong k) { return "n" + k.ToString("x16"); }

        public string ToDot()
        {
            Func<string, string> q = s => "\"" + (s ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n") + "\"";
            List<string> lines = new List<string> { "digraph antstate {", "  rankdir=LR;", "  node [shape=box, fontname=Helvetica, fontsize=10];",
                                                    "  edge [fontname=Helvetica, fontsize=8];", "  label=" + q(Title) + ";" };
            foreach (GraphNode n in NodeList)
            {
                string style = n.Role == "root" || n.Role == "start" ? "bold" : n.Role == "subject" ? "dashed" : "solid";
                if (n.State != "ok") style = "dotted";
                lines.Add("  " + DotId(n.Key) + " [label=" + q(n.Name + "\n" + n.Type) + ", style=" + style + "];");
            }
            foreach (GraphEdge e in Edges)
            {
                string attrs = "label=" + q(e.Label.Length > 80 ? e.Label.Substring(0, 80) : e.Label);
                if (e.Kind == "subject") attrs += ", style=dashed";
                lines.Add("  " + DotId(e.S) + " -> " + DotId(e.T) + " [" + attrs + "];");
            }
            lines.Add("}");
            return string.Join("\n", lines) + "\n";
        }

        /// <summary>self-contained page: drag the background to pan, wheel to zoom, drag nodes, click a node for its fields,
        /// type in the search box to find nodes</summary>
        public string ToHtml(AntDb db = null)
        {
            if (db != null) AttachDetails(db);
            if (!NodeList.All(n => n.Placed)) Layout();
            Dictionary<ulong, int> index = new Dictionary<ulong, int>();
            List<object> nodes = new List<object>();
            int ni = 0;
            foreach (GraphNode n in NodeList)
            {
                index[n.Key] = ni++;
                JObject j = new JObject();
                j["k"] = n.KeyHex; j["t"] = n.Type; j["n"] = n.Name; j["s"] = n.State; j["r"] = n.Role ?? "";
                j["x"] = Math.Round(n.X, 1); j["y"] = Math.Round(n.Y, 1); j["w"] = n.W; j["h"] = n.H;
                j["i"] = n.Info.Cast<object>().ToList();
                nodes.Add(j);
            }
            List<object> edges = new List<object>();
            foreach (GraphEdge e in Edges)
            {
                if (!index.ContainsKey(e.S) || !index.ContainsKey(e.T)) continue;
                JObject j = new JObject();
                j["s"] = (long)index[e.S]; j["t"] = (long)index[e.T]; j["l"] = e.Label; j["k"] = e.Kind;
                edges.Add(j);
            }
            JObject d = new JObject();
            d["title"] = Title; d["nodes"] = nodes; d["edges"] = edges; d["truncated"] = Truncated;
            string data = MiniJson.Serialize(d, 0).Replace("</", "<\\/");
            return HtmlTemplate.Replace("@@TITLE@@", System.Net.WebUtility.HtmlEncode(Title)).Replace("@@DATA@@", data);
        }

        private const string HtmlTemplate = @"<!doctype html>
<html lang=""en""><head><meta charset=""utf-8""><meta name=""viewport"" content=""width=device-width, initial-scale=1"">
<title>@@TITLE@@</title>
<style>
:root{--bg:#fff;--fg:#1d1d1f;--muted:#6b6b70;--line:#c9c9ce;--box:#f6f6f7;--sel:#2160c4;--warn:#a15c00}
@media (prefers-color-scheme: dark){:root{--bg:#1c1c1e;--fg:#e8e8ea;--muted:#9a9aa0;--line:#4a4a50;--box:#2a2a2d;--sel:#6ea0ff;--warn:#e0a040}}
*{box-sizing:border-box}
html,body{margin:0;height:100%;background:var(--bg);color:var(--fg);font:13px/1.35 system-ui,-apple-system,""Segoe UI"",sans-serif}
#top{display:flex;gap:8px;align-items:center;padding:6px 10px;border-bottom:1px solid var(--line)}
#top h1{font-size:13px;font-weight:600;margin:0;flex:1;white-space:nowrap;overflow:hidden;text-overflow:ellipsis}
#top input{font:inherit;padding:3px 6px;border:1px solid var(--line);background:var(--bg);color:var(--fg);width:220px}
#top button{font:inherit;padding:3px 8px;border:1px solid var(--line);background:var(--box);color:var(--fg);cursor:pointer}
#main{display:flex;height:calc(100% - 37px)}
#view{flex:1;overflow:hidden;cursor:grab;position:relative}
#side{width:380px;border-left:1px solid var(--line);overflow:auto;padding:8px 10px;font-family:ui-monospace,Consolas,monospace;font-size:12px;white-space:pre-wrap}
svg{width:100%;height:100%;display:block}
.node rect{fill:var(--box);stroke:var(--line);stroke-width:1}
.node.root rect,.node.start rect{stroke:var(--fg);stroke-width:2}
.node.subject rect{stroke-dasharray:4 3}
.node.missing rect,.node.imported rect{stroke-dasharray:2 2;stroke:var(--warn)}
.node.sel rect{stroke:var(--sel);stroke-width:2.5}
.node.hit rect{stroke:var(--sel);stroke-width:2}
.node text{fill:var(--fg);font-size:11px;pointer-events:none}
.node text.ty{fill:var(--muted);font-size:10px}
.edge{fill:none;stroke:var(--line);stroke-width:1.2}
.edge.transition{stroke:var(--muted)}
.edge.subject{stroke-dasharray:4 3}
.edge.hl{stroke:var(--sel);stroke-width:2}
.elabel{fill:var(--muted);font-size:9px}
#side .k{color:var(--muted)}
@media (max-width:700px){#side{display:none}#top input{width:120px}}
</style></head><body>
<div id=""top""><h1 id=""title""></h1><input id=""q"" placeholder=""find name / type / key""><button id=""fit"">fit</button><button id=""lbl"">labels</button></div>
<div id=""main""><div id=""view""><svg id=""svg""><defs><marker id=""ar"" viewBox=""0 0 10 10"" refX=""10"" refY=""5"" markerWidth=""7"" markerHeight=""7"" orient=""auto-start-reverse""><path d=""M0,0L10,5L0,10z"" fill=""currentColor""/></marker></defs><g id=""vp""><g id=""E""></g><g id=""L""></g><g id=""N""></g></g></svg></div>
<div id=""side"">click a node</div></div>
<script>
const G=@@DATA@@;
const NS=""http://www.w3.org/2000/svg"";
const svg=document.getElementById(""svg""),vp=document.getElementById(""vp""),E=document.getElementById(""E""),L=document.getElementById(""L""),N=document.getElementById(""N""),side=document.getElementById(""side"");
document.getElementById(""title"").textContent=G.title+""  (""+G.nodes.length+"" objects, ""+G.edges.length+"" links""+(G.truncated?"", truncated"":"""")+"")"";
svg.style.color=getComputedStyle(document.documentElement).getPropertyValue(""--line"");
let tx=20,ty=20,sc=1,showLabels=G.edges.length<=120,sel=-1;
function el(t,a,p){const e=document.createElementNS(NS,t);for(const k in a)e.setAttribute(k,a[k]);if(p)p.appendChild(e);return e}
function cut(s,n){return s.length>n?s.slice(0,n-1)+""…"":s}
const adj=G.nodes.map(()=>[]);
G.edges.forEach((e,i)=>{adj[e.s].push(i);adj[e.t].push(i)});
function path(e){const a=G.nodes[e.s],b=G.nodes[e.t];
 if(e.s===e.t){const x=a.x+a.w,y=a.y+a.h/2;return `M${x},${y-6}C${x+40},${y-40} ${x+40},${y+40} ${x},${y+6}`}
 let x1=a.x+a.w,y1=a.y+a.h/2,x2=b.x,y2=b.y+b.h/2;
 if(b.x<=a.x){x1=a.x+a.w/2;y1=a.y+(b.y>a.y?a.h:0);x2=b.x+b.w/2;y2=b.y+(b.y>a.y?0:b.h);const d=Math.max(60,Math.abs(a.x-b.x)/3);return `M${x1},${y1}C${x1+d},${y1+(b.y>a.y?d:-d)} ${x2+d},${y2+(b.y>a.y?-d:d)} ${x2},${y2}`}
 const d=Math.max(30,(x2-x1)/2);return `M${x1},${y1}C${x1+d},${y1} ${x2-d},${y2} ${x2},${y2}`}
const ep=G.edges.map(e=>{const p=el(""path"",{class:""edge ""+e.k,d:path(e),""marker-end"":""url(#ar)""},E);const t=el(""title"",{},p);t.textContent=e.l;return p});
const el_=G.edges.map(e=>{const t=el(""text"",{class:""elabel""},L);t.textContent=cut(e.l,48);return t});
function placeLabel(i){const e=G.edges[i],a=G.nodes[e.s],b=G.nodes[e.t];const t=el_[i];t.setAttribute(""x"",(a.x+a.w+b.x)/2-20);t.setAttribute(""y"",(a.y+b.y+a.h)/2-3);t.style.display=showLabels?"""":""none""}
G.edges.forEach((e,i)=>placeLabel(i));
const ng=G.nodes.map((n,i)=>{const g=el(""g"",{class:""node ""+n.r+"" ""+n.s,transform:`translate(${n.x},${n.y})`},N);
 el(""rect"",{width:n.w,height:n.h,rx:4},g);const a=el(""text"",{x:7,y:15},g);a.textContent=cut(n.n||n.k,32);const b=el(""text"",{x:7,y:29,class:""ty""},g);b.textContent=cut(n.t,36);
 const tt=el(""title"",{},g);tt.textContent=n.n+""\n""+n.t+""\n""+n.k;g.addEventListener(""mousedown"",ev=>startDrag(ev,i));return g});
function apply(){vp.setAttribute(""transform"",`translate(${tx},${ty}) scale(${sc})`)}
function fit(){const r=svg.getBoundingClientRect();let x0=1e9,y0=1e9,x1=-1e9,y1=-1e9;G.nodes.forEach(n=>{x0=Math.min(x0,n.x);y0=Math.min(y0,n.y);x1=Math.max(x1,n.x+n.w);y1=Math.max(y1,n.y+n.h)});
 if(!G.nodes.length)return;sc=Math.min(1.5,Math.min(r.width/(x1-x0+40),r.height/(y1-y0+40)));tx=(r.width-(x1-x0)*sc)/2-x0*sc;ty=(r.height-(y1-y0)*sc)/2-y0*sc;apply()}
function select(i){if(sel>=0){ng[sel].classList.remove(""sel"");adj[sel].forEach(j=>ep[j].classList.remove(""hl""))}sel=i;if(i<0)return;ng[i].classList.add(""sel"");adj[i].forEach(j=>ep[j].classList.add(""hl""));
 const n=G.nodes[i];let s=n.n+""\n""+n.t+""  ""+n.k+""\n\n"";const out=adj[i].filter(j=>G.edges[j].s===i),inn=adj[i].filter(j=>G.edges[j].t===i);
 if(out.length){s+=""links out:\n"";out.forEach(j=>{s+=""  ""+G.edges[j].l+"" -> ""+G.nodes[G.edges[j].t].n+""\n""})}
 if(inn.length){s+=""links in:\n"";inn.forEach(j=>{s+=""  ""+G.nodes[G.edges[j].s].n+"" -> ""+G.edges[j].l+""\n""})}
 if(n.i.length)s+=""\nfields:\n""+n.i.join(""\n"");side.textContent=s}
let drag=null,pan=null,moved=false;
function startDrag(ev,i){ev.stopPropagation();drag={i,x:ev.clientX,y:ev.clientY};moved=false}
document.getElementById(""view"").addEventListener(""mousedown"",ev=>{pan={x:ev.clientX,y:ev.clientY,tx,ty};moved=false});
window.addEventListener(""mousemove"",ev=>{if(drag){const n=G.nodes[drag.i];const dx=(ev.clientX-drag.x)/sc,dy=(ev.clientY-drag.y)/sc;if(Math.abs(dx)+Math.abs(dy)>0)moved=true;n.x+=dx;n.y+=dy;drag.x=ev.clientX;drag.y=ev.clientY;
 ng[drag.i].setAttribute(""transform"",`translate(${n.x},${n.y})`);adj[drag.i].forEach(j=>{ep[j].setAttribute(""d"",path(G.edges[j]));placeLabel(j)})}
 else if(pan){tx=pan.tx+ev.clientX-pan.x;ty=pan.ty+ev.clientY-pan.y;if(Math.abs(ev.clientX-pan.x)+Math.abs(ev.clientY-pan.y)>2)moved=true;apply()}});
window.addEventListener(""mouseup"",ev=>{if(drag&&!moved)select(drag.i);else if(pan&&!moved)select(-1);drag=null;pan=null});
svg.addEventListener(""wheel"",ev=>{ev.preventDefault();const r=svg.getBoundingClientRect(),mx=ev.clientX-r.left,my=ev.clientY-r.top;const f=Math.exp(-ev.deltaY*0.0015);tx=mx-(mx-tx)*f;ty=my-(my-ty)*f;sc*=f;apply()},{passive:false});
document.getElementById(""fit"").onclick=fit;
document.getElementById(""lbl"").onclick=()=>{showLabels=!showLabels;G.edges.forEach((e,i)=>placeLabel(i))};
let hits=[],hp=0;
document.getElementById(""q"").addEventListener(""keydown"",ev=>{if(ev.key!==""Enter"")return;const q=ev.target.value.toLowerCase();ng.forEach(g=>g.classList.remove(""hit""));
 if(!q)return;hits=[];G.nodes.forEach((n,i)=>{if((n.n+"" ""+n.t+"" ""+n.k).toLowerCase().includes(q)){hits.push(i);ng[i].classList.add(""hit"")}});if(!hits.length)return;
 const i=hits[hp++%hits.length],n=G.nodes[i],r=svg.getBoundingClientRect();tx=r.width/2-(n.x+n.w/2)*sc;ty=r.height/2-(n.y+n.h/2)*sc;apply();select(i)});
fit();
</script></body></html>
";
    }
}
