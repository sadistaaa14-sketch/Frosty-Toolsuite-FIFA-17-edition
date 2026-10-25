using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace FrostyAnimationEditor.Core
{
    /// <summary>
    /// The dataflow of an expression kernel as a Graph: a box per instruction that reads or writes registers (calls of
    /// natives, moves, copies, branches), a box per input (game state bindings, the time registers, constants) and per
    /// output (the DOF bindings), and a link from each register's last writer (in code order) to its readers. With
    /// branches the last writer in code order is an approximation of the real one.
    /// </summary>
    public static class KernelGraph
    {
        private const ulong Base = 0x4B00000000000000UL;     // synthetic node keys (not objects of the file)

        private static string OpName(Ins i)
        {
            switch (i.Op)
            {
                case Kernel.OP_SWITCH: return "switch";
                case Kernel.OP_MEMCPY: return "memcpy";
                case Kernel.OP_BF: return "branch if false";
                case Kernel.OP_EMIT: return "emit";
                case Kernel.OP_SUB: return "subscribe";
                case Kernel.OP_UNSUB: return "unsubscribe";
                case Kernel.OP_JMPI: return "jump";
                case Kernel.OP_JSR: return "call sub";
                case Kernel.OP_RET: return "return";
                case Kernel.OP_HALT: return "halt";
            }
            if (Kernel.Moves.ContainsKey(i.Op)) return "move " + Kernel.Moves[i.Op] * 4 + "B";
            if (Kernel.IsCall(i.Op))
            {
                Native n = Kernel.NativeOf(i.Hash);
                return n != null && !string.IsNullOrEmpty(n.Name) ? n.Name : "native 0x" + i.Hash.ToString("x8");
            }
            return "op 0x" + i.Op.ToString("x2");
        }

        private static string Reg(uint x)
        {
            string seg;
            if (!Kernel.Segs.TryGetValue((int)(x >> 24), out seg)) seg = "?";
            return seg + ":" + (x & 0xFFFFFF).ToString("x4");
        }

        private static bool IsRead(string role) { return role == "in" || role == "src" || role == "cond" || role == "sel" || role == "value" || role == "data" || role == "state"; }
        private static bool IsWrite(string role) { return role == "out" || role == "dst" || role == "state"; }

        public static Graph Build(Kernel k, KernelBindings b, Dictionary<ulong, string> gsNames, string title)
        {
            Graph g = new Graph { Title = title, LayoutKind = "dataflow" };
            Dictionary<uint, string> syms = b != null ? b.Symbols() : new Dictionary<uint, string>();
            Dictionary<uint, ulong> lastWriter = new Dictionary<uint, ulong>();
            Dictionary<uint, ulong> inputs = new Dictionary<uint, ulong>();
            ulong next = 0;
            Func<string, string, string, ulong> add = (type, name, role) =>
            {
                ulong key = Base | ++next;
                g.Nodes[key] = new GraphNode { Key = key, Type = type, Name = name, State = "synthetic", Role = role };
                g.Order.Add(key);
                return key;
            };
            Func<uint, string> label = x =>
            {
                string s;
                if (!syms.TryGetValue(x, out s)) return Reg(x);
                if (b != null && s.StartsWith("gs", StringComparison.Ordinal))
                {
                    int j;
                    if (int.TryParse(s.Substring(2), NumberStyles.Integer, CultureInfo.InvariantCulture, out j) && j < b.Gs.Count && gsNames != null)
                    {
                        string nm;
                        if (gsNames.TryGetValue(b.Gs[j].Item1, out nm)) return s + " " + nm;
                    }
                }
                return s + " (" + Reg(x) + ")";
            };
            Func<uint, ulong> source = x =>
            {
                ulong w;
                if (lastWriter.TryGetValue(x, out w)) return w;
                if (!inputs.TryGetValue(x, out w))
                {
                    string seg = Kernel.Segs.ContainsKey((int)(x >> 24)) ? Kernel.Segs[(int)(x >> 24)] : "?";
                    w = add(seg == "c" ? "constant" : syms.ContainsKey(x) ? "input" : "register", label(x), "root");
                    inputs[x] = w;
                }
                return w;
            };
            for (int n = 0; n < k.Code.Count; n++)
            {
                Ins i = k.Code[n];
                List<Ins.Operand> ops = i.Operands().Where(o => o.X != Kernel.NONE).ToList();
                if (ops.Count == 0) continue;
                ulong node = add(OpName(i), "#" + n + " " + OpName(i), "");
                GraphNode gn = g.Nodes[node];
                foreach (Ins.Operand o in ops) gn.Info.Add(o.Role + " " + label(o.X));
                foreach (Ins.Operand o in ops.Where(o => IsRead(o.Role)))
                    g.Edges.Add(new GraphEdge { S = source(o.X), T = node, Label = label(o.X), Kind = "data" });
                foreach (Ins.Operand o in ops.Where(o => IsWrite(o.Role))) lastWriter[o.X] = node;
            }
            // outputs: the DOF bindings
            if (b != null)
                for (int j = 0; j < b.Dofs.Count; j++)
                {
                    Tuple<string, string, uint, uint> d = b.Dofs[j];
                    if (d.Item3 == Kernel.NONE) continue;
                    ulong w;
                    if (!lastWriter.TryGetValue(d.Item3, out w)) continue;
                    ulong outN = add("output", "dof" + j + " " + d.Item1 + "." + d.Item2, "");
                    g.Edges.Add(new GraphEdge { S = w, T = outN, Label = Reg(d.Item3), Kind = "data" });
                }
            if (g.Nodes.Count > 600) g.Truncated = true;
            return g.Layout();
        }
    }
}
