using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Text.RegularExpressions;

namespace FrostyAnimationEditor.Core
{
    /// <summary>Kernel disassembler and assembler (the text round-trips to the same blob and bindings).</summary>
    public static class KernelText
    {
        private const uint NONE = Kernel.NONE;

        public static string FmtF32(uint bits)
        {
            float f = BE.BitsFloat(bits);
            double x = f;
            if (double.IsNaN(x) || double.IsInfinity(x)) return "0x" + bits.ToString("x8");
            for (int p = 1; p < 10; p++)
            {
                string s = PyFormat.G(x, p);
                if (BE.FloatBits((float)PyFormat.ParseFloat(s)) == bits) return s;
            }
            return "0x" + bits.ToString("x8");
        }

        public static uint ParseF32(string tok)
        {
            tok = PyFormat.Strip(tok);
            if (tok.ToLowerInvariant().StartsWith("0x")) return PyFormat.Mask32(PyFormat.Int(tok, 16));
            return PyFormat.PackF32(PyFormat.ParseFloat(tok));
        }

        public static string FmtOp(uint x, Dictionary<uint, string> syms = null)
        {
            if (x == NONE) return "-";
            string n;
            if (syms != null && syms.TryGetValue(x, out n)) return n;
            int seg = (int)(x >> 24);
            uint off = x & 0xFFFFFF;
            string sn;
            if (Kernel.Segs.TryGetValue(seg, out sn)) return sn + ":" + off.ToString("x4");
            return "seg" + seg + ":" + off.ToString("x4");
        }

        /// <summary>display type for a register of native type t (a size for untyped copies)</summary>
        private static string TypeItem(object t)
        {
            if (t is int || t is long)
            {
                switch (Convert.ToInt64(t)) { case 1: return "u8"; case 2: return "u16"; case 4: return "w32"; case 8: return "u64"; case 16: return "w128"; }
                return null;
            }
            switch ((string)t)
            {
                case "b": return "b8";
                case "i": return "i32";
                case "f": return "f32";
                case "v": case "q": return "vec";
                case "m34": return "f32x12";
                case "m44": return "f32x16";
                case "dof": return "dof";
                case "pose": case "obj": case "ctx": return "u64";
                case "u8": return "u8";
                case "u16": return "u16";
            }
            return null;
        }

        public static readonly Dictionary<string, int> ItemSize = new Dictionary<string, int>
        {
            { "b8", 1 }, { "u8", 1 }, { "u16", 2 }, { "i32", 4 }, { "u32", 4 }, { "f32", 4 }, { "u64", 8 }, { "dof", 8 }, { "vec", 16 },
            { "f32x12", 48 }, { "f32x16", 64 }, { "w32", 4 }, { "w128", 16 },
        };

        private static readonly Dictionary<string, int> ItemRank = new Dictionary<string, int> { { "w32", 1 }, { "w128", 1 }, { "u8", 1 }, { "u64", 1 } };

        private static string Guess(string kind, byte[] b)
        {
            if (kind == "w32")
            {
                uint v = BE.U32LE(b, 0);
                uint e = (v >> 23) & 0xFF;
                return v == 0 || (e != 0 && e != 0xFF) ? "f32" : "u32";
            }
            if (kind == "w128")
            {
                for (int i = 0; i < 4; i++)
                {
                    uint w = BE.U32LE(b, 4 * i);
                    uint e = (w >> 23) & 0xFF;
                    if (!(w == 0 || (e != 0 && e != 0xFF))) return "bytes";
                }
                return "vec";
            }
            return kind;
        }

        public static string FmtItem(string kind, byte[] b)
        {
            switch (kind)
            {
                case "b8": case "u8": return b[0].ToString(CultureInfo.InvariantCulture);
                case "u16": return BE.U16LE(b, 0).ToString(CultureInfo.InvariantCulture);
                case "i32": return ((int)BE.U32LE(b, 0)).ToString(CultureInfo.InvariantCulture);
                case "u32": return "0x" + BE.U32LE(b, 0).ToString("x");
                case "u64": return "0x" + BE.U64LE(b, 0).ToString("x");
                case "dof": return ((int)BE.U32LE(b, 0)).ToString(CultureInfo.InvariantCulture) + ", 0x" + BE.U32LE(b, 4).ToString("x");
                case "f32": case "vec": case "f32x12": case "f32x16":
                    {
                        List<string> r = new List<string>();
                        for (int i = 0; i + 4 <= b.Length; i += 4) r.Add(FmtF32(BE.U32LE(b, i)));
                        return string.Join(", ", r);
                    }
            }
            return string.Join(" ", b.Select(c => c.ToString("x2")));
        }

        /// <summary>bytes of a data item (FormatException / KernelException on bad text, like Python's ValueError)</summary>
        public static byte[] ParseItem(string kind, string text)
        {
            text = PyFormat.Strip(text);
            byte[] r;
            switch (kind)
            {
                case "bytes":
                    {
                        List<byte> l = new List<byte>();
                        foreach (string t in PyFormat.SplitWs(text))
                        {
                            BigInteger v = PyFormat.Int(t, 16);
                            if (v.Sign < 0 || v > 255) throw new FormatException("bytes must be in range(0, 256)");
                            l.Add((byte)v);
                        }
                        return l.ToArray();
                    }
                case "b8": case "u8": return new[] { (byte)(PyFormat.Int(text, 0) & 0xFF) };
                case "u16": r = new byte[2]; BE.W16LE(r, 0, (ushort)(PyFormat.Int(text, 0) & 0xFFFF)); return r;
                case "i32": case "u32": r = new byte[4]; BE.W32LE(r, 0, PyFormat.Mask32(PyFormat.Int(text, 0))); return r;
                case "u64": r = new byte[8]; BE.W64LE(r, 0, PyFormat.Mask64(PyFormat.Int(text, 0))); return r;
                case "dof":
                    {
                        string[] ab = text.Split(',').Select(PyFormat.Strip).ToArray();
                        if (ab.Length < 2) throw new FormatException("not enough values to unpack (expected 2, got " + ab.Length + ")");
                        if (ab.Length > 2) throw new FormatException("too many values to unpack (expected 2)");
                        r = new byte[8];
                        BE.W32LE(r, 0, PyFormat.Mask32(PyFormat.Int(ab[0], 0)));
                        BE.W32LE(r, 4, PyFormat.Mask32(PyFormat.Int(ab[1], 0)));
                        return r;
                    }
                case "f32": case "vec": case "f32x12": case "f32x16":
                    {
                        uint[] vals = text.Split(',').Select(ParseF32).ToArray();
                        int n = kind == "f32" ? 1 : kind == "vec" ? 4 : kind == "f32x12" ? 12 : 16;
                        if (vals.Length != n) throw new KernelException(kind + " needs " + n + " values, got " + vals.Length);
                        r = new byte[4 * n];
                        for (int i = 0; i < n; i++) BE.W32LE(r, 4 * i, vals[i]);
                        return r;
                    }
            }
            throw new KernelException("unknown item type " + PyFormat.Repr(kind));
        }

        private sealed class Item
        {
            public int Off;
            public string Kind;
            public byte[] Bytes;
        }

        /// <summary>[(offset, kind, bytes)] covering data: typed where an operand of known type starts</summary>
        private static List<Item> Items(byte[] data, Dictionary<int, HashSet<string>> uses)
        {
            List<Item> outp = new List<Item>();
            int p = 0, n = data.Length;
            List<int> starts = uses.Keys.OrderBy(x => x).ToList();
            int k = 0;
            while (p < n)
            {
                while (k < starts.Count && starts[k] < p) k++;
                int nxt = k < starts.Count ? starts[k] : n;
                if (nxt > p)
                {
                    int q = Math.Min(nxt, (p / 16 + 1) * 16);
                    outp.Add(new Item { Off = p, Kind = "bytes", Bytes = Sub(data, p, q - p) });
                    p = q;
                    continue;
                }
                List<string> kinds = uses[p].OrderBy(t => -(ItemSize.ContainsKey(t) ? ItemSize[t] : 0))
                                            .ThenBy(t => ItemRank.ContainsKey(t) ? ItemRank[t] : 0)
                                            .ThenBy(t => t, StringComparer.Ordinal).ToList();
                string kind = null;
                foreach (string t in kinds) if (p + ItemSize[t] <= n) { kind = t; break; }
                if (kind == null)
                {
                    int q = Math.Min(n, (p / 16 + 1) * 16);
                    outp.Add(new Item { Off = p, Kind = "bytes", Bytes = Sub(data, p, q - p) });
                    p = q;
                    k++;
                    continue;
                }
                int size = ItemSize[kind];
                byte[] b = Sub(data, p, size);
                outp.Add(new Item { Off = p, Kind = Guess(kind, b), Bytes = b });
                p += size;
            }
            return outp;
        }

        private static byte[] Sub(byte[] d, int o, int n)
        {
            byte[] r = new byte[Math.Max(0, Math.Min(n, d.Length - o))];
            Buffer.BlockCopy(d, o, r, 0, r.Length);
            return r;
        }

        private static void Use(Dictionary<int, HashSet<string>> u, int off, string kind)
        {
            HashSet<string> s;
            if (!u.TryGetValue(off, out s)) { s = new HashSet<string>(); u[off] = s; }
            s.Add(kind);
        }

        /// <summary>text that Assemble turns back into the same blob (and bindings)</summary>
        public static string Disassemble(Kernel k, KernelBindings bindings = null, string title = null, Dictionary<ulong, string> gsNames = null)
        {
            Dictionary<uint, Native> nat = Kernel.Natives;
            if (k.Code.Any(i => i.Off < 0)) k.Layout();
            Dictionary<uint, string> syms = bindings != null ? bindings.Symbols() : new Dictionary<uint, string>();
            byte[] consts = k.Consts.ToArray();
            HashSet<long> offs = new HashSet<long>(k.Code.Select(i => (long)i.Off));
            HashSet<long> tg = new HashSet<long>();
            for (int n = 0; n < k.Code.Count; n++)
            {
                Ins i = k.Code[n];
                foreach (uint t in i.Targets()) tg.Add(t);
                if (i.FallsThrough() && i.Next != k.DefaultNext(n)) tg.Add(i.Next);
            }
            Dictionary<long, string> lab = new Dictionary<long, string>();
            foreach (long t in tg) if (offs.Contains(t)) lab[t] = "L" + t.ToString("x4");
            Func<long, string> L = t => { string s; return lab.TryGetValue(t, out s) ? s : "@0x" + t.ToString("x"); };
            Dictionary<int, HashSet<string>> cuse = new Dictionary<int, HashSet<string>>(), suse = new Dictionary<int, HashSet<string>>();
            foreach (Ins i in k.Code)
                foreach (Ins.Operand o in i.Operands())
                {
                    if (o.X == NONE) continue;
                    object t = o.Type;
                    string kind = TypeItem((t is string && ((string)t == "mem" || (string)t == "?")) ? (object)Kernel.OperandSize(i, o.Role, t, consts) : t);
                    if (kind == null) continue;
                    int seg = (int)(o.X >> 24), off = (int)(o.X & 0xFFFFFF);
                    if (seg == 0) Use(cuse, off, kind);
                    else if (seg == 1) Use(suse, off, kind);
                }
            if (bindings != null)
                foreach (Tuple<uint, int, string> r in bindings.Registers())
                    if (r.Item1 >> 24 == 1) Use(suse, (int)(r.Item1 & 0xFFFFFF), r.Item3.StartsWith("dof") ? "dof" : (r.Item2 == 4 ? "u32" : "u64"));
            foreach (uint[] r in k.RelocC) Use(cuse, (int)r[0], "u64");
            foreach (uint[] r in k.RelocS) Use(suse, (int)r[0], "u64");

            List<string> o_ = new List<string>();
            Action<string> w = o_.Add;
            w("; FIFA 17 expression kernel (ExpressionAsset.ExpressionKernel) - antstate disassembly");
            if (!string.IsNullOrEmpty(title)) w("; " + title);
            w("; operands: c:OFF constants, s:OFF state registers, t:OFF temporaries, - none;");
            w("; calls: OUTPUTS = native INPUTS [| STATE]; '-> L' gives a non-sequential next instruction");
            List<string> extra = new List<string>();
            if (k.Raw0 != 0) extra.Add("raw0=0x" + k.Raw0.ToString("x"));
            if (!k.Unk.All(u => u == NONE)) extra.Add("unk=" + string.Join(",", k.Unk.Select(u => "0x" + u.ToString("x"))));
            if (k.Unk2c != NONE) extra.Add("unk2c=0x" + k.Unk2c.ToString("x"));
            w(".kernel id=0x" + k.Kid.ToString("x8") + (extra.Count > 0 ? " " + string.Join(" ", extra) : ""));
            Kernel.Needs need = k.GetNeeds();
            w(".temps 0x" + k.Temps.ToString("x") + (need.Temps == k.Temps ? "" : "            ; the code uses 0x" + need.Temps.ToString("x")));
            w(".limits in=" + k.MaxIn + " out=" + k.MaxOut + " state=" + k.MaxState);
            w(".callstack " + k.Callstack);
            if (k.Channels != 0 || k.Nodes != 0) w(".events channels=" + k.Channels + " nodes=" + k.Nodes);
            if (bindings != null)
            {
                w("");
                foreach (string[] rf in KernelBindings.RegFields) w(".bind " + PyFormat.L(rf[0], 12) + " " + FmtOp(bindings.Regs[rf[0]]));
                for (int j = 0; j < bindings.Gs.Count; j++)
                {
                    Tuple<ulong, uint, uint> g = bindings.Gs[j];
                    string note = gsNames != null && gsNames.ContainsKey(g.Item1) ? "   ; gs" + j + ": " + gsNames[g.Item1] : "";
                    string iface;
                    if (!Kernel.Ifaces.TryGetValue(g.Item3, out iface)) iface = "0x" + g.Item3.ToString("x8");
                    w(".bind gs     " + FmtOp(g.Item2) + " 0x" + g.Item1.ToString("x16") + " " + iface + note);
                }
                for (int j = 0; j < bindings.Dofs.Count; j++)
                {
                    Tuple<string, string, uint, uint> d = bindings.Dofs[j];
                    w(".bind dof    " + FmtOp(d.Item3) + " " + FmtOp(d.Item4) + " " + PyFormat.JsonStr(d.Item1) + " " + PyFormat.JsonStr(d.Item2) + "   ; dof" + j);
                }
            }
            w("");
            w(".const 0x" + consts.Length.ToString("x"));
            foreach (Item it in Items(consts, cuse)) w("c:" + it.Off.ToString("x4") + " " + PyFormat.L(it.Kind, 6) + " " + FmtItem(it.Kind, it.Bytes));
            w("");
            int bas = k.StatePrefix.Count;
            w(".state 0x" + k.Regs.Count.ToString("x") + "            ; register bytes (template size 0x" + (bas + k.Regs.Count).ToString("x") + ")");
            if (k.StatePrefix.Any(b => b != 0)) w(".state_prefix " + BE.Hex(k.StatePrefix.ToArray()));
            foreach (Item it in Items(k.Regs.ToArray(), suse))
            {
                string nm;
                syms.TryGetValue(0x01000000u | (uint)it.Off, out nm);
                w("s:" + it.Off.ToString("x4") + " " + PyFormat.L(it.Kind, 6) + " " + FmtItem(it.Kind, it.Bytes) + (nm != null ? "   ; " + nm : ""));
            }
            foreach (uint[] r in k.RelocC) w(".reloc c:" + r[0].ToString("x4") + " c:" + r[1].ToString("x4"));
            foreach (uint[] r in k.RelocS) w(".reloc s:" + r[0].ToString("x4") + " s:" + r[1].ToString("x4"));
            w("");
            w(".code");
            for (int n = 0; n < k.Code.Count; n++)
            {
                Ins i = k.Code[n];
                if (lab.ContainsKey(i.Off)) w(lab[i.Off] + ":");
                string notes;
                string text = FmtIns(i, syms, L, nat, consts, out notes);
                if (i.Next != k.DefaultNext(n)) text += " -> " + L(i.Next);
                if (!string.IsNullOrEmpty(notes)) text = PyFormat.L(text, 60) + " ; " + notes;
                w("    " + text);
            }
            return string.Join("\n", o_) + "\n";
        }

        private static string CNote(uint x, byte[] consts, object t)
        {
            if (x == NONE || x >> 24 != 0 || t == null) return null;
            int off = (int)(x & 0xFFFFFF);
            int size;
            if (t is string) { if (!Kernel.TypeSize.TryGetValue((string)t, out size)) size = 4; }
            else size = (int)t;
            byte[] b = Sub(consts, off, size);
            string ts = t as string;
            if (ts == "f" && b.Length == 4) return FmtF32(BE.U32LE(b, 0));
            if (ts == "i" && b.Length == 4) return ((int)BE.U32LE(b, 0)).ToString(CultureInfo.InvariantCulture);
            if ((ts == "b" || ts == "u8") && b.Length >= 1) return b[0].ToString(CultureInfo.InvariantCulture);
            if ((ts == "v" || ts == "q") && b.Length == 16) return "(" + string.Join(", ", Enumerable.Range(0, 4).Select(j => FmtF32(BE.U32LE(b, 4 * j)))) + ")";
            if (t is int && (int)t == 4 && b.Length == 4) return FmtF32(BE.U32LE(b, 0));
            return null;
        }

        private static string FmtIns(Ins i, Dictionary<uint, string> syms, Func<long, string> L, Dictionary<uint, Native> nat, byte[] consts, out string notes)
        {
            int op = i.Op;
            Func<uint, string> F = x => FmtOp(x, syms);
            List<string> nl = new List<string>();
            notes = "";
            if (Kernel.IsCall(op))
            {
                Native e;
                nat.TryGetValue(i.Hash, out e);
                string name;
                if (e == null) name = "native[" + i.Hash.ToString("x8") + "]";
                else
                {
                    List<uint> hs;
                    bool many = Kernel.NativeByName().TryGetValue(e.Name, out hs) && hs.Count > 1;
                    name = e.Name + (many ? "[" + i.Hash.ToString("x8") + "]" : "");
                }
                int? auto = i.StateOps.Count == 0 ? Kernel.FixedOf(i.InOps.Count, i.Outs.Count) : null;
                if (op == Kernel.OP_CALL && auto.HasValue) name = "callv " + name;
                string s = "";
                if (i.Outs.Count > 0) s = string.Join(", ", i.Outs.Select(F)) + " = ";
                s += name;
                if (i.InOps.Count > 0) s += " " + string.Join(", ", i.InOps.Select(F));
                if (i.StateOps.Count > 0) s += " | " + string.Join(", ", i.StateOps.Select(F));
                if (i.SlotFn != 0 || i.SlotX != 0) s += " slot=0x" + i.SlotFn.ToString("x") + ",0x" + i.SlotX.ToString("x");
                if (e != null)
                {
                    for (int j = 0; j < i.InOps.Count; j++)
                    {
                        object t = j < e.In.Count ? e.In[j] : null;
                        string v = CNote(i.InOps[j], consts, t);
                        if (v != null) nl.Add(F(i.InOps[j]) + "=" + v);
                    }
                    if (!Kernel.SigOk(e, i.InOps.Count, i.Outs.Count, i.StateOps.Count))
                        nl.Add("signature " + e.In.Count + "/" + e.Out.Count + "/" + e.State.Count + " expected");
                }
                notes = string.Join(", ", nl);
                return s;
            }
            if (Kernel.Moves.ContainsKey(op))
            {
                int sz = Kernel.Moves[op];
                string v = (sz == 1 || sz == 4 || sz == 16) ? CNote(i.Src, consts, sz) : null;
                notes = v != null ? F(i.Src) + "=" + v : "";
                return "mov" + sz + " " + F(i.Dst) + " = " + F(i.Src);
            }
            switch (op)
            {
                case Kernel.OP_BF: return "ifnot " + F(i.Cond) + " goto " + L(i.Target);
                case Kernel.OP_SWITCH: return "switch " + F(i.Sel) + " {" + string.Join(", ", i.Cases.Select(c => ((int)c[0]).ToString(CultureInfo.InvariantCulture) + ": " + L(c[1]))) + "}";
                case Kernel.OP_JSR: return "jsr " + L(i.Target);
                case Kernel.OP_RET: return "ret";
                case Kernel.OP_HALT: return "halt";
                case Kernel.OP_JMPI: return "jmpi " + F(i.Src);
                case Kernel.OP_MEMCPY: return "memcpy " + F(i.Dst) + " = " + F(i.Src) + ", " + i.Size;
                case Kernel.OP_SUB: return "subscribe 0x" + i.Chan.ToString("x") + ", " + i.Node;
                case Kernel.OP_UNSUB: return "unsubscribe " + i.Node;
                case Kernel.OP_EMIT: return "emit 0x" + i.Chan.ToString("x") + ", " + F(i.Value) + ", " + F(i.Data);
            }
            return "op 0x" + op.ToString("x2");
        }

        // =================================================================================== assembler
        public static Kernel Assemble(string text, out KernelBindings bindings, out List<string> notes)
        {
            Asm a = new Asm(text);
            Kernel k = a.Run();
            bindings = a.HaveBind ? a.B : null;
            notes = a.Notes;
            return k;
        }

        private static readonly Regex TokOp = new Regex(@"^(c|s|t|seg\d+):([0-9a-fA-F]+)$");

        private sealed class Asm
        {
            private readonly string text;
            private readonly Dictionary<uint, Native> nat = Kernel.Natives;
            private readonly Dictionary<string, List<uint>> byname = Kernel.NativeByName();
            public readonly Kernel K = new Kernel();
            public readonly KernelBindings B = new KernelBindings();
            public bool HaveBind;
            public readonly List<string> Notes = new List<string>();
            private readonly Dictionary<string, BigInteger?> explicitH = new Dictionary<string, BigInteger?>();
            private byte[] explicitPrefix;
            private readonly Dictionary<string, object> labels = new Dictionary<string, object>();
            private readonly List<Tuple<Ins, string, string, int>> pending = new List<Tuple<Ins, string, string, int>>();
            private readonly List<string> litOrder = new List<string>();
            private readonly Dictionary<string, Tuple<string, byte[]>> lits = new Dictionary<string, Tuple<string, byte[]>>();
            private readonly Dictionary<string, uint> litOff = new Dictionary<string, uint>();
            private readonly List<Tuple<BigInteger, byte[], int>> constItems = new List<Tuple<BigInteger, byte[], int>>();
            private readonly List<Tuple<BigInteger, byte[], int>> stateItems = new List<Tuple<BigInteger, byte[], int>>();
            private BigInteger? constSize, stateSize;
            private readonly Dictionary<string, uint> syms = new Dictionary<string, uint>();
            private int codeEnd;

            public Asm(string text) { this.text = text; }

            private KernelException Err(int ln, string msg) { return new KernelException("line " + ln + ": " + msg); }

            /// <summary>int(text, 0) that must fit an unsigned field of the given width (the editor refuses what would not save)</summary>
            private BigInteger Ranged(string text, int bits, int ln, string what)
            {
                BigInteger v = PyFormat.Int(text, 0);
                if (v.Sign < 0 || v >= BigInteger.One << bits) throw Err(ln, what + " " + PyFormat.Strip(text) + " does not fit in " + bits + " bits");
                return v;
            }

            /// <summary>-> (operand, literal key or null)</summary>
            private Tuple<uint, string> Operand(string tok, int ln, object hint = null)
            {
                tok = PyFormat.Strip(tok);
                if (tok == "-") return Tuple.Create(NONE, (string)null);
                uint sv;
                if (syms.TryGetValue(tok, out sv)) return Tuple.Create(sv, (string)null);
                Match m = TokOp.Match(tok);
                if (m.Success)
                {
                    string sg = m.Groups[1].Value;
                    BigInteger seg = sg == "c" ? 0 : sg == "s" ? 1 : sg == "t" ? 2 : PyFormat.Int(sg.Substring(3), 10);
                    BigInteger off = BigInteger.Parse("0" + m.Groups[2].Value, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture);
                    if (off > 0xFFFFFF || seg > 0xFF) throw Err(ln, "operand out of range: " + tok);
                    return Tuple.Create((uint)seg << 24 | (uint)off, (string)null);
                }
                if (tok.StartsWith("raw:")) return Tuple.Create(PyFormat.Mask32(PyFormat.Int(tok.Substring(4), 0)), (string)null);
                if (tok.StartsWith("#")) return Tuple.Create(0u, Literal(tok.Substring(1), ln, hint));
                throw Err(ln, "unknown operand " + PyFormat.Repr(tok));
            }

            private uint SetOp(Ins ins, string field, string tok, int ln, object hint = null)
            {
                Tuple<uint, string> r = Operand(tok, ln, hint);
                if (r.Item2 != null)
                {
                    if (ins.Lits == null) ins.Lits = new Dictionary<string, object>();
                    ins.Lits[field] = r.Item2;
                }
                return r.Item1;
            }

            private uint PlainOp(string tok, int ln)
            {
                Tuple<uint, string> r = Operand(tok, ln);
                if (r.Item2 != null) throw Err(ln, "a literal cannot be used here");
                return r.Item1;
            }

            private static readonly string[] LitKinds = { "f32", "i32", "b8", "u8", "u16", "u32", "vec", "u64" };

            private string Literal(string s, int ln, object hint)
            {
                string kind, val;
                int c = s.IndexOf(':');
                if (c >= 0 && LitKinds.Contains(s.Substring(0, c))) { kind = s.Substring(0, c); val = s.Substring(c + 1); }
                else
                {
                    string hs = hint as string;
                    kind = hs == "f" ? "f32" : hs == "i" ? "i32" : hs == "b" ? "b8" : hs == "u8" ? "u8" : hs == "u16" ? "u16" : (hs == "v" || hs == "q") ? "vec" : "f32";
                    val = s;
                }
                if (kind == "vec")
                {
                    List<string> vals = val.Trim('(', ')').Split(',').Select(PyFormat.Strip).ToList();
                    while (vals.Count < 4) vals.Add("0");
                    val = string.Join(", ", vals);
                }
                byte[] b;
                try { b = ParseItem(kind, val); }
                catch (Exception e) when (e is FormatException || e is KernelException)
                { throw Err(ln, "bad literal " + PyFormat.Repr(s) + ": " + e.Message); }
                string key = kind + ":" + BE.Hex(b);
                if (!lits.ContainsKey(key)) { lits[key] = Tuple.Create(kind, b); litOrder.Add(key); }
                return key;
            }

            private static int Find(List<byte> hay, byte[] needle, int from)
            {
                for (int p = from; p + needle.Length <= hay.Count; p++)
                {
                    bool ok = true;
                    for (int j = 0; j < needle.Length; j++) if (hay[p + j] != needle[j]) { ok = false; break; }
                    if (ok) return p;
                }
                return -1;
            }

            /// <summary>place literal constants after the constants (aligned to their size, max 16)</summary>
            private List<byte> ResolveLits(List<byte> consts)
            {
                foreach (string key in litOrder)
                {
                    byte[] b = lits[key].Item2;
                    int a = Math.Min(16, Math.Max(1, b.Length));
                    int found = -1;
                    int p = 0;
                    while (true)
                    {
                        p = Find(consts, b, p);
                        if (p < 0) break;
                        if (p % a == 0) { found = p; break; }
                        p++;
                    }
                    if (found < 0)
                    {
                        p = Kernel.Align(consts.Count, a);
                        while (consts.Count < p) consts.Add(0);
                        consts.AddRange(b);
                        found = p;
                    }
                    litOff[key] = (uint)found;
                }
                return consts;
            }

            private static readonly Regex LabelRe = new Regex(@"^([A-Za-z_][\w.]*):(?!\w)\s*(.*)$");

            public Kernel Run()
            {
                List<string> lines = PyFormat.SplitLines(text);
                string mode = null;
                List<Tuple<int, List<Tuple<string, int>>, string>> code = new List<Tuple<int, List<Tuple<string, int>>, string>>();
                List<Tuple<string, int>> pendLabels = new List<Tuple<string, int>>();
                for (int idx = 0; idx < lines.Count; idx++)
                {
                    int ln = idx + 1;
                    string raw = lines[idx];
                    int sc = raw.IndexOf(';');
                    string line = PyFormat.RStrip(sc >= 0 ? raw.Substring(0, sc) : raw);
                    if (PyFormat.Strip(line).Length == 0) continue;
                    string s = PyFormat.Strip(line);
                    if (s.StartsWith("."))
                    {
                        int sp = s.IndexOf(' ');
                        string d = sp >= 0 ? s.Substring(0, sp) : s;
                        string rest = sp >= 0 ? PyFormat.Strip(s.Substring(sp + 1)) : "";
                        if (d == ".code") { mode = "code"; continue; }
                        if (d == ".const") { mode = "const"; if (rest.Length > 0) constSize = PyFormat.Int(rest, 0); continue; }
                        if (d == ".state") { mode = "state"; if (rest.Length > 0) stateSize = PyFormat.Int(PyFormat.SplitWs(rest)[0], 0); continue; }
                        Directive(d, rest, ln);
                        continue;
                    }
                    if (mode == "const" || mode == "state") { ItemLine(mode, s, ln); continue; }
                    if (mode != "code") throw Err(ln, "instruction outside .code: " + PyFormat.Repr(s));
                    while (true)
                    {
                        Match m = LabelRe.Match(s);
                        string firstTok = PyFormat.SplitWs(s)[0];
                        if (!m.Success || TokOp.IsMatch(firstTok)) break;
                        pendLabels.Add(Tuple.Create(m.Groups[1].Value, ln));
                        s = PyFormat.Strip(m.Groups[2].Value);
                        if (s.Length == 0) break;
                    }
                    if (s.Length == 0) continue;
                    code.Add(Tuple.Create(ln, pendLabels, s));
                    pendLabels = new List<Tuple<string, int>>();
                }
                if (pendLabels.Count > 0) code.Add(Tuple.Create(pendLabels[0].Item2, pendLabels, "__end__"));
                FinishData();
                foreach (Tuple<int, List<Tuple<string, int>>, string> c in code)
                {
                    if (c.Item3 == "__end__")
                    {
                        foreach (Tuple<string, int> l in c.Item2) labels[l.Item1] = "end";
                        continue;
                    }
                    Ins ins = Instruction(c.Item3, c.Item1);
                    foreach (Tuple<string, int> l in c.Item2)
                    {
                        if (labels.ContainsKey(l.Item1)) throw Err(l.Item2, "label " + l.Item1 + " defined twice");
                        labels[l.Item1] = ins;
                    }
                    K.Code.Add(ins);
                }
                return Finish();
            }

            private static List<string> SplitArgs(string s)
            {
                List<string> r = new List<string>();
                foreach (Match m in Regex.Matches(s, "\"((?:[^\"\\\\]|\\\\.)*)\"|(\\S+)")) r.Add(m.Value);
                return r;
            }

            private static string Unq(string s)
            {
                if (s.StartsWith("\"")) return (string)MiniJson.Parse(s);
                return s;
            }

            private static string[] KeyVal(string kv)
            {
                int e = kv.IndexOf('=');
                return e >= 0 ? new[] { kv.Substring(0, e), kv.Substring(e + 1) } : new[] { kv, "" };
            }

            private void Directive(string d, string rest, int ln)
            {
                Kernel k = K;
                List<string> ws = PyFormat.SplitWs(rest);
                if (d == ".kernel")
                {
                    foreach (string kv in ws)
                    {
                        string[] p = KeyVal(kv);
                        string key = p[0], val = p[1];
                        if (key == "id") k.Kid = PyFormat.Mask32(PyFormat.Int(val, 0));
                        else if (key == "raw0") k.Raw0 = (ulong)Ranged(val, 64, ln, "raw0");
                        else if (key == "unk")
                        {
                            k.Unk = val.Split(',').Select(x => PyFormat.Mask32(PyFormat.Int(x, 0))).ToArray();
                            if (k.Unk.Length != 4) throw Err(ln, "unk needs 4 values");
                        }
                        else if (key == "unk2c") k.Unk2c = PyFormat.Mask32(PyFormat.Int(val, 0));
                        else throw Err(ln, "unknown .kernel field " + PyFormat.Repr(key));
                    }
                }
                else if (d == ".temps") explicitH["temps"] = rest == "auto" ? (BigInteger?)null : PyFormat.Int(rest, 0);
                else if (d == ".callstack") explicitH["callstack"] = rest == "auto" ? (BigInteger?)null : PyFormat.Int(rest, 0);
                else if (d == ".limits")
                {
                    if (rest == "auto") return;
                    foreach (string kv in ws)
                    {
                        string[] p = KeyVal(kv);
                        string f = p[0] == "in" ? "max_in" : p[0] == "out" ? "max_out" : p[0] == "state" ? "max_state" : null;
                        if (f == null) throw Err(ln, "unknown .limits field " + PyFormat.Repr(p[0]));
                        explicitH[f] = PyFormat.Int(p[1], 0);
                    }
                }
                else if (d == ".events")
                {
                    foreach (string kv in ws)
                    {
                        string[] p = KeyVal(kv);
                        if (p[0] != "channels" && p[0] != "nodes") throw Err(ln, "unknown .events field " + PyFormat.Repr(p[0]));
                        uint v = (uint)Ranged(p[1], 32, ln, p[0]);
                        if (p[0] == "channels") k.Channels = v; else k.Nodes = v;
                    }
                }
                else if (d == ".state_prefix") explicitPrefix = PyFormat.FromHex(rest);
                else if (d == ".reloc")
                {
                    if (ws.Count != 2) throw Err(ln, ".reloc needs two operands");
                    uint sa = PlainOp(ws[0], ln), sb = PlainOp(ws[1], ln);
                    if (sa >> 24 != sb >> 24 || (sa >> 24 != 0 && sa >> 24 != 1)) throw Err(ln, ".reloc needs two c: or two s: operands");
                    (sa >> 24 == 0 ? k.RelocC : k.RelocS).Add(new[] { sa & 0xFFFFFF, sb & 0xFFFFFF });
                }
                else if (d == ".bind")
                {
                    HaveBind = true;
                    List<string> parts = SplitArgs(rest);
                    string what = parts.Count > 0 ? parts[0] : "";
                    if (B.Regs.ContainsKey(what))
                    {
                        if (parts.Count != 2) throw Err(ln, ".bind " + what + " needs one operand");
                        B.Regs[what] = PlainOp(parts[1], ln);
                        if (B.Regs[what] != NONE) syms[what] = B.Regs[what];
                    }
                    else if (what == "gs")
                    {
                        if (parts.Count < 4) throw Err(ln, ".bind gs OPERAND KEY INTERFACE");
                        uint x = PlainOp(parts[1], ln);
                        ulong key = PyFormat.Mask64(PyFormat.Int(parts[2], 0));      // keys are stored as u64 (the packer masks them)
                        uint? it = Kernel.IfaceOf(parts[3]);
                        if (!it.HasValue) it = PyFormat.Mask32(PyFormat.Int(parts[3], 0));
                        if (x != NONE) syms["gs" + B.Gs.Count] = x;
                        B.Gs.Add(Tuple.Create(key, x, it.Value));
                    }
                    else if (what == "dof")
                    {
                        if (parts.Count != 5) throw Err(ln, ".bind dof HANDLE DEFAULT \"group\" \"dof\"");
                        uint h = PlainOp(parts[1], ln), df = PlainOp(parts[2], ln);
                        int j = B.Dofs.Count;
                        if (h != NONE) syms["dof" + j] = h;
                        if (df != NONE) syms["dof" + j + ".def"] = df;
                        B.Dofs.Add(Tuple.Create(Unq(parts[3]), Unq(parts[4]), h, df));
                    }
                    else throw Err(ln, "unknown .bind " + PyFormat.Repr(what));
                }
                else throw Err(ln, "unknown directive " + d);
            }

            private static readonly Regex ItemRe = new Regex(@"^([cs]):([0-9a-fA-F]+)\s+(\w+)\s*(.*)$");

            private void ItemLine(string mode, string s, int ln)
            {
                Match m = ItemRe.Match(s);
                if (!m.Success) throw Err(ln, "expected 'c:OFF type value' or 's:OFF type value'");
                if ((m.Groups[1].Value == "c") != (mode == "const")) throw Err(ln, m.Groups[1].Value + ": item in the ." + mode + " section");
                BigInteger off = BigInteger.Parse("0" + m.Groups[2].Value, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture);
                string kind = m.Groups[3].Value;
                byte[] b;
                try { b = ParseItem(kind, m.Groups[4].Value); }
                catch (Exception e) when (e is FormatException || e is KernelException)
                { throw Err(ln, "bad " + kind + " value: " + e.Message); }
                (mode == "const" ? constItems : stateItems).Add(Tuple.Create(off, b, ln));
            }

            private static int CompareBytes(byte[] a, byte[] b)
            {
                for (int i = 0; i < Math.Min(a.Length, b.Length); i++) if (a[i] != b[i]) return a[i].CompareTo(b[i]);
                return a.Length.CompareTo(b.Length);
            }

            private List<byte> Build(List<Tuple<BigInteger, byte[], int>> items, BigInteger? size, string what)
            {
                BigInteger end = items.Count > 0 ? items.Select(t => t.Item1 + t.Item2.Length).Max() : BigInteger.Zero;
                BigInteger n = size ?? end;
                if (end > n) throw new KernelException(what + " items end at 0x" + PyFormat.Hex(end) + ", past the declared size 0x" + PyFormat.Hex(n));
                if (n > Kernel.MaxSegment) throw new KernelException(what + " size 0x" + PyFormat.Hex(n) + " is too large (operands reach 0x" + Kernel.MaxSegment.ToString("x") + " bytes)");
                int nn = (int)n;
                byte[] buf = new byte[nn];
                bool[] done = new bool[nn];
                List<Tuple<BigInteger, byte[], int>> sorted = new List<Tuple<BigInteger, byte[], int>>(items);
                sorted.Sort((a, b) => a.Item1 != b.Item1 ? a.Item1.CompareTo(b.Item1) : (CompareBytes(a.Item2, b.Item2) != 0 ? CompareBytes(a.Item2, b.Item2) : a.Item3.CompareTo(b.Item3)));
                foreach (Tuple<BigInteger, byte[], int> it in sorted)
                {
                    int o = (int)it.Item1;
                    for (int j = 0; j < it.Item2.Length; j++)
                        if (done[o + j]) throw Err(it.Item3, what + " item at 0x" + PyFormat.Hex(it.Item1) + " overlaps the previous one");
                    for (int j = 0; j < it.Item2.Length; j++) { buf[o + j] = it.Item2[j]; done[o + j] = true; }
                }
                return new List<byte>(buf);
            }

            private void FinishData()
            {
                K.Consts = Build(constItems, constSize, "constant");
                K.Regs = Build(stateItems, stateSize, "state");
            }

            private static readonly Regex NextRe = new Regex(@"^(.*?)\s*->\s*(\S+)$");
            private static readonly Regex SwitchNextRe = new Regex(@"\}\s*->\s*(\S+)$");

            private Ins Instruction(string s, int ln)
            {
                string nxt = null;
                Match m = NextRe.Match(s);
                if (m.Success && !s.StartsWith("switch")) { s = m.Groups[1].Value; nxt = m.Groups[2].Value; }
                else if (s.StartsWith("switch") && SwitchNextRe.IsMatch(s))
                {
                    Match m2 = SwitchNextRe.Match(s);
                    nxt = m2.Groups[1].Value;
                    s = s.Substring(0, m2.Index + 1);
                }
                Ins ins = ParseIns(PyFormat.Strip(s), ln);
                if (nxt != null)
                {
                    ins.NextDefault = false;
                    pending.Add(Tuple.Create(ins, "next", nxt, ln));
                }
                return ins;
            }

            private static readonly Regex MovRe = new Regex(@"^mov(1|2|4|8|16)$");
            private static readonly Regex AssignRe = new Regex(@"^(\S+)\s*=\s*(\S+)$");
            private static readonly Regex IfnotRe = new Regex(@"^(\S+)\s+goto\s+(\S+)$");
            private static readonly Regex SwitchRe = new Regex(@"^(\S+)\s*\{(.*)\}$");
            private static readonly Regex MemcpyRe = new Regex(@"^(\S+)\s*=\s*(\S+)\s*,\s*(\S+)$");

            private Ins ParseIns(string s, int ln)
            {
                List<string> parts = PyFormat.SplitWs(s, 1);
                string head = parts[0];
                string rest = parts.Count > 1 ? parts[1] : "";
                if (MovRe.IsMatch(head))
                {
                    int size = int.Parse(head.Substring(3), CultureInfo.InvariantCulture);
                    Match m = AssignRe.Match(rest);
                    if (!m.Success) throw Err(ln, "mov" + size + " DST = SRC");
                    Ins i = new Ins(Kernel.MoveOp(size));
                    i.Src = SetOp(i, "src", m.Groups[2].Value, ln, size);
                    i.Dst = SetOp(i, "dst", m.Groups[1].Value, ln);
                    return i;
                }
                if (head == "ifnot")
                {
                    Match m = IfnotRe.Match(rest);
                    if (!m.Success) throw Err(ln, "ifnot COND goto LABEL");
                    Ins i = new Ins(Kernel.OP_BF);
                    i.Cond = SetOp(i, "cond", m.Groups[1].Value, ln, "b");
                    pending.Add(Tuple.Create(i, "target", m.Groups[2].Value, ln));
                    return i;
                }
                if (head == "switch")
                {
                    Match m = SwitchRe.Match(rest);
                    if (!m.Success) throw Err(ln, "switch SEL {VALUE: LABEL, ...}");
                    Ins i = new Ins(Kernel.OP_SWITCH);
                    i.Sel = SetOp(i, "sel", m.Groups[1].Value, ln, "i");
                    int j = 0;
                    foreach (string c in m.Groups[2].Value.Split(',').Where(c => PyFormat.Strip(c).Length > 0))
                    {
                        int colon = c.IndexOf(':');
                        string v = colon >= 0 ? c.Substring(0, colon) : c, t = colon >= 0 ? c.Substring(colon + 1) : "";
                        i.Cases.Add(new[] { PyFormat.Mask32(PyFormat.Int(PyFormat.Strip(v), 0)), 0u });
                        pending.Add(Tuple.Create(i, "case:" + j, PyFormat.Strip(t), ln));
                        j++;
                    }
                    return i;
                }
                if (head == "jsr")
                {
                    Ins i = new Ins(Kernel.OP_JSR);
                    pending.Add(Tuple.Create(i, "target", PyFormat.Strip(rest), ln));
                    return i;
                }
                if ((head == "ret" || head == "halt" || head == "nop") && rest.Length == 0)
                    return new Ins(head == "ret" ? Kernel.OP_RET : head == "halt" ? Kernel.OP_HALT : 0);
                if (head == "op")
                {
                    BigInteger bv = PyFormat.Int(rest, 0);
                    if (bv >= 0 && bv < 0x100)
                    {
                        int v = (int)bv;
                        if (Kernel.IsCall(v) || Kernel.Moves.ContainsKey(v) || new[] { Kernel.OP_SWITCH, Kernel.OP_SUB, Kernel.OP_UNSUB, Kernel.OP_EMIT, Kernel.OP_MEMCPY, Kernel.OP_BF, Kernel.OP_JSR, Kernel.OP_JMPI }.Contains(v))
                            throw Err(ln, "op 0x" + v.ToString("x2") + " has operands; use its mnemonic");
                    }
                    return new Ins((int)(bv & 0xFF));
                }
                if (head == "jmpi")
                {
                    Ins i = new Ins(Kernel.OP_JMPI);
                    i.Src = SetOp(i, "src", rest, ln, "i");
                    return i;
                }
                if (head == "memcpy")
                {
                    Match m = MemcpyRe.Match(rest);
                    if (!m.Success) throw Err(ln, "memcpy DST = SRC, SIZE");
                    Ins i = new Ins(Kernel.OP_MEMCPY);
                    i.Src = SetOp(i, "src", m.Groups[2].Value, ln);
                    i.Dst = SetOp(i, "dst", m.Groups[1].Value, ln);
                    i.Size = (uint)Ranged(m.Groups[3].Value, 32, ln, "memcpy size");
                    return i;
                }
                if (head == "subscribe")
                {
                    List<string> a = SplitArgs(rest.Replace(",", " "));
                    ulong chan = (ulong)Ranged(a[0], 64, ln, "channel");
                    return new Ins(Kernel.OP_SUB) { Chan = chan, Node = (uint)Ranged(a[1], 32, ln, "node") };
                }
                if (head == "unsubscribe") return new Ins(Kernel.OP_UNSUB) { Node = (uint)Ranged(rest, 32, ln, "node") };
                if (head == "emit")
                {
                    string[] a = rest.Split(',').Select(PyFormat.Strip).ToArray();
                    if (a.Length != 3) throw Err(ln, "emit CHANNEL, VALUE, DATA");
                    Ins i = new Ins(Kernel.OP_EMIT) { Chan = (ulong)Ranged(a[0], 64, ln, "channel") };
                    i.Value = SetOp(i, "value", a[1], ln, "i");
                    i.Data = SetOp(i, "data", a[2], ln);
                    return i;
                }
                return Call(s, ln);
            }

            private static readonly Regex OutsRe = new Regex(@"^([^=]+?)\s*=\s*(.+)$");
            private static readonly Regex SlotRe = new Regex(@"\s+slot=(\S+),(\S+)$");
            private static readonly Regex HashNameRe = new Regex(@"^([\w.]*)\[([0-9a-fA-F]{1,8})\]$");
            private static readonly Regex OpNameRe = new Regex(@"^\w+\.op_([0-9a-fA-F]{8})$");

            private Ins Call(string s, int ln)
            {
                List<string> outs = new List<string>();
                Match m = OutsRe.Match(s);
                if (m.Success)
                {
                    string g1 = PyFormat.Strip(m.Groups[1].Value);
                    if (!g1.StartsWith("callv") && !g1.StartsWith("native") && !g1.Replace(", ", ",").Contains(" "))
                    {
                        outs = m.Groups[1].Value.Split(',').Select(PyFormat.Strip).Where(x => x.Length > 0).ToList();
                        s = PyFormat.Strip(m.Groups[2].Value);
                    }
                }
                bool generic = false;
                if (s.StartsWith("callv ")) { generic = true; s = PyFormat.Strip(s.Substring(6)); }
                int sp = s.IndexOf(' ');
                string name = sp >= 0 ? s.Substring(0, sp) : s;
                string args = sp >= 0 ? s.Substring(sp + 1) : "";
                ulong slotFn = 0;
                uint slotX = 0;
                Match ms = SlotRe.Match(args);
                if (ms.Success)
                {
                    BigInteger f = PyFormat.Int(ms.Groups[1].Value, 0), x = PyFormat.Int(ms.Groups[2].Value, 0);
                    if (f.Sign < 0 || f > ulong.MaxValue) throw Err(ln, "slot " + ms.Groups[1].Value + " does not fit in 64 bits");
                    if (x.Sign < 0 || x > uint.MaxValue) throw Err(ln, "slot " + ms.Groups[2].Value + " does not fit in 32 bits");
                    slotFn = (ulong)f;
                    slotX = (uint)x;
                    args = args.Substring(0, ms.Index);
                }
                List<string> st = new List<string>();
                int bar = args.IndexOf('|');
                if (bar >= 0)
                {
                    st = args.Substring(bar + 1).Split(',').Select(PyFormat.Strip).Where(x => x.Length > 0).ToList();
                    args = args.Substring(0, bar);
                }
                List<string> insl = args.Split(',').Select(PyFormat.Strip).Where(x => x.Length > 0).ToList();
                uint h = NativeHash(name, ln);
                Native e;
                nat.TryGetValue(h, out e);
                Ins i = new Ins(Kernel.OP_CALL) { Hash = h, Generic = generic, SlotFn = slotFn, SlotX = slotX };
                List<string> sigIn = Kernel.SigTypes(e, "in", insl.Count);
                for (int j = 0; j < insl.Count; j++) i.InOps.Add(SetOp(i, "in:" + j, insl[j], ln, sigIn[j]));
                List<string> sigOut = Kernel.SigTypes(e, "out", outs.Count);
                for (int j = 0; j < outs.Count; j++) i.Outs.Add(SetOp(i, "out:" + j, outs[j], ln, sigOut[j]));
                List<string> sigSt = Kernel.SigTypes(e, "state", st.Count);
                for (int j = 0; j < st.Count; j++) i.StateOps.Add(SetOp(i, "state:" + j, st[j], ln, sigSt[j]));
                int? fx = i.StateOps.Count == 0 ? Kernel.FixedOf(i.InOps.Count, i.Outs.Count) : null;
                if (!generic && fx.HasValue) { i.Op = fx.Value; i.Generic = false; }
                if (e != null && !Kernel.SigOk(e, i.InOps.Count, i.Outs.Count, i.StateOps.Count))
                    Notes.Add("line " + ln + ": " + e.Name + " takes " + e.In.Count + " inputs, " + e.Out.Count + " outputs, " + e.State.Count + " state operands; " +
                              i.InOps.Count + "/" + i.Outs.Count + "/" + i.StateOps.Count + " given");
                return i;
            }

            private uint NativeHash(string name, int ln)
            {
                Match m = HashNameRe.Match(name);
                if (m.Success)
                {
                    uint h = Convert.ToUInt32(m.Groups[2].Value, 16);
                    string nm = m.Groups[1].Value;
                    if (nm != "native" && nm != "" && nat.ContainsKey(h) && nat[h].Name != nm)
                        throw Err(ln, "hash " + h.ToString("x8") + " is " + nat[h].Name + ", not " + nm);
                    return h;
                }
                List<uint> hs;
                if (!byname.TryGetValue(name, out hs) || hs.Count == 0)
                {
                    Match m2 = OpNameRe.Match(name);
                    if (m2.Success && nat.ContainsKey(Convert.ToUInt32(m2.Groups[1].Value, 16)))
                    {
                        uint h = Convert.ToUInt32(m2.Groups[1].Value, 16);
                        if (nat[h].Name != name) Notes.Add("line " + ln + ": " + name + " is now called " + nat[h].Name);
                        return h;
                    }
                    throw Err(ln, "unknown native " + PyFormat.Repr(name));
                }
                uint pref;
                if (hs.Count > 1) return Kernel.Preferred.TryGetValue(name, out pref) ? pref : hs[0];
                return hs[0];
            }

            private uint Target(string lab, int ln)
            {
                if (lab.StartsWith("@")) return (uint)Ranged(lab.Substring(1), 32, ln, "code offset");
                object t;
                if (!labels.TryGetValue(lab, out t)) throw Err(ln, "unknown label " + PyFormat.Repr(lab));
                Ins ins = t as Ins;
                return ins != null ? (uint)ins.Off : (uint)codeEnd;
            }

            private uint Lit(Ins i, string field, uint cur)
            {
                object key;
                if (i.Lits != null && i.Lits.TryGetValue(field, out key)) return litOff[(string)key];
                return cur;
            }

            private Kernel Finish()
            {
                Kernel k = K;
                List<byte> consts = litOrder.Count > 0 ? ResolveLits(new List<byte>(k.Consts)) : k.Consts;
                if (constSize.HasValue && consts.Count > constSize.Value && litOrder.Count > 0)
                    Notes.Add("constants grew to 0x" + consts.Count.ToString("x") + " bytes for the literals");
                k.Consts = new List<byte>(consts);
                foreach (Ins i in k.Code)
                {
                    if (i.Lits == null) continue;
                    for (int j = 0; j < i.InOps.Count; j++) i.InOps[j] = Lit(i, "in:" + j, i.InOps[j]);
                    for (int j = 0; j < i.Outs.Count; j++) i.Outs[j] = Lit(i, "out:" + j, i.Outs[j]);
                    for (int j = 0; j < i.StateOps.Count; j++) i.StateOps[j] = Lit(i, "state:" + j, i.StateOps[j]);
                    i.Src = Lit(i, "src", i.Src);
                    i.Dst = Lit(i, "dst", i.Dst);
                    i.Cond = Lit(i, "cond", i.Cond);
                    i.Sel = Lit(i, "sel", i.Sel);
                    i.Value = Lit(i, "value", i.Value);
                    i.Data = Lit(i, "data", i.Data);
                    i.Lits = null;
                }
                if (explicitPrefix != null) k.StatePrefix = new List<byte>(explicitPrefix);
                codeEnd = k.Layout();
                for (int n = 0; n < k.Code.Count; n++)
                    if (k.Code[n].NextDefault) k.Code[n].Next = k.DefaultNext(n);
                foreach (Tuple<Ins, string, string, int> p in pending)
                {
                    uint t = Target(p.Item3, p.Item4);
                    if (p.Item2 == "next") p.Item1.Next = t;
                    else if (p.Item2 == "target") p.Item1.Target = t;
                    else
                    {
                        int j = int.Parse(p.Item2.Substring(5), CultureInfo.InvariantCulture);
                        p.Item1.Cases[j] = new[] { p.Item1.Cases[j][0], t };
                    }
                }
                Dictionary<string, Func<uint>> getf = new Dictionary<string, Func<uint>>
                {
                    { "temps", () => k.Temps }, { "max_in", () => k.MaxIn }, { "max_out", () => k.MaxOut }, { "max_state", () => k.MaxState }, { "callstack", () => k.Callstack },
                };
                Dictionary<string, Action<uint>> setf = new Dictionary<string, Action<uint>>
                {
                    { "temps", v => k.Temps = v }, { "max_in", v => k.MaxIn = v }, { "max_out", v => k.MaxOut = v }, { "max_state", v => k.MaxState = v }, { "callstack", v => k.Callstack = v },
                };
                string[] hf = { "temps", "max_in", "max_out", "max_state", "callstack" };
                foreach (string f in hf)
                {
                    BigInteger? v;
                    if (!explicitH.TryGetValue(f, out v) || !v.HasValue) continue;
                    // negative values are raised to what the code needs, like any smaller value
                    if (v.Value > uint.MaxValue) throw new KernelException(f + " " + v.Value + " does not fit in 32 bits");
                    setf[f](v.Value.Sign < 0 ? 0u : (uint)v.Value);
                }
                k.UpdateHeader(true);
                foreach (string f in hf)
                {
                    BigInteger? v;
                    if (explicitH.TryGetValue(f, out v) && v.HasValue && getf[f]() != v.Value)
                        Notes.Add(f + " raised from " + v.Value + " to " + getf[f]() + " (the code needs it)");
                }
                long lbas = Kernel.Seg1Base(k.Channels, k.Nodes, k.Callstack);
                if (lbas > Kernel.MaxSegment) throw new KernelException("the state header part (0x" + lbas.ToString("x") + " bytes for the event channels, nodes and call stack) is too large");
                int bas = (int)lbas;
                if (k.StatePrefix.Count != bas)
                {
                    if (k.StatePrefix.Skip(bas).Any(b => b != 0)) throw new KernelException(".state_prefix is longer than the state header part (0x" + bas.ToString("x") + " bytes)");
                    List<byte> pre = k.StatePrefix.Take(bas).ToList();
                    while (pre.Count < bas) pre.Add(0);
                    k.StatePrefix = pre;
                }
                return k;
            }
        }
    }
}
