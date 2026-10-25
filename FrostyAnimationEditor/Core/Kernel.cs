using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace FrostyAnimationEditor.Core
{
    public sealed class KernelException : Exception
    {
        public KernelException(string msg) : base(msg) { }
    }

    /// <summary>a registered native function of the expression VM (kernel_natives.json)</summary>
    public sealed class Native
    {
        public uint Hash;
        public string Fn, Name, Doc;
        public List<string> In = new List<string>(), Out = new List<string>(), State = new List<string>();
        public List<string> InRepeat;
    }

    /// <summary>one instruction of an expression kernel</summary>
    public sealed class Ins
    {
        public int Op;
        public uint Next;
        public int Off = -1;
        public bool NextDefault = true;
        // switch
        public uint Sel;
        public List<uint[]> Cases = new List<uint[]>();
        // call
        public uint Hash;
        public List<uint> InOps = new List<uint>(), Outs = new List<uint>(), StateOps = new List<uint>();
        public bool Generic;
        public ulong SlotFn;
        public uint SlotX;
        // events
        public ulong Chan;
        public uint Node, Value, Data;
        // memcpy / moves / bf / jsr / jmpi
        public uint Src, Dst, Size, Cond, Target;
        // assembler: literal placeholders (operand index -> literal key)
        internal Dictionary<string, object> Lits;

        public Ins(int op) { Op = op; }

        public int Words()
        {
            int op = Op;
            if (op == Kernel.OP_SWITCH) return 3 + 2 * Cases.Count;
            if (op == Kernel.OP_CALL) return 8 + InOps.Count + Outs.Count + StateOps.Count;
            if (Kernel.Fixed.ContainsKey(op)) return 5 + InOps.Count + Outs.Count;
            if (op == Kernel.OP_SUB) return 4;
            if (op == Kernel.OP_UNSUB || op == Kernel.OP_JMPI || op == Kernel.OP_JSR) return 2;
            if (op == Kernel.OP_EMIT) return 5;
            if (op == Kernel.OP_MEMCPY) return 4;
            if (op == Kernel.OP_BF || Kernel.Moves.ContainsKey(op)) return 3;
            return 1;
        }

        public List<uint> Encode()
        {
            List<uint> w = new List<uint> { (uint)(Op & 0xFF) | ((Next & 0xFFFFFF) << 8) };
            int op = Op;
            if (op == Kernel.OP_SWITCH)
            {
                w.Add(Sel); w.Add((uint)Cases.Count);
                foreach (uint[] c in Cases) w.Add(c[0]);
                foreach (uint[] c in Cases) w.Add(c[1]);
            }
            else if (Kernel.IsCall(op))
            {
                w.Add((uint)(SlotFn & Kernel.NONE)); w.Add((uint)(SlotFn >> 32)); w.Add(Hash); w.Add(SlotX);
                if (op == Kernel.OP_CALL)
                    foreach (List<uint> g in new[] { InOps, Outs, StateOps }) { w.Add((uint)g.Count); w.AddRange(g); }
                else { w.AddRange(InOps); w.AddRange(Outs); }
            }
            else if (op == Kernel.OP_SUB) { w.Add((uint)(Chan & Kernel.NONE)); w.Add((uint)(Chan >> 32)); w.Add(Node); }
            else if (op == Kernel.OP_UNSUB) w.Add(Node);
            else if (op == Kernel.OP_JMPI) w.Add(Src);
            else if (op == Kernel.OP_EMIT) { w.Add((uint)(Chan & Kernel.NONE)); w.Add((uint)(Chan >> 32)); w.Add(Value); w.Add(Data); }
            else if (op == Kernel.OP_MEMCPY) { w.Add(Src); w.Add(Dst); w.Add(Size); }
            else if (op == Kernel.OP_BF) { w.Add(Cond); w.Add(Target); }
            else if (op == Kernel.OP_JSR) w.Add(Target);
            else if (Kernel.Moves.ContainsKey(op)) { w.Add(Src); w.Add(Dst); }
            return w;
        }

        public struct Operand
        {
            public string Role;
            public uint X;
            public object Type;     // type code string, or an int size
            public Operand(string role, uint x, object type) { Role = role; X = x; Type = type; }
        }

        /// <summary>[(role, operand, type code)] of every register operand</summary>
        public List<Operand> Operands()
        {
            List<Operand> r = new List<Operand>();
            int op = Op;
            if (op == Kernel.OP_SWITCH) { r.Add(new Operand("sel", Sel, "i")); return r; }
            if (Kernel.IsCall(op))
            {
                Native e = Kernel.NativeOf(Hash);
                foreach (Tuple<string, List<uint>, string> g in new[] { Tuple.Create("in", InOps, "in"), Tuple.Create("out", Outs, "out"), Tuple.Create("state", StateOps, "state") })
                {
                    List<string> sig = Kernel.SigTypes(e, g.Item3, g.Item2.Count);
                    for (int j = 0; j < g.Item2.Count; j++) r.Add(new Operand(g.Item1, g.Item2[j], sig[j]));
                }
                return r;
            }
            if (op == Kernel.OP_EMIT) { r.Add(new Operand("value", Value, "i")); r.Add(new Operand("data", Data, "?")); return r; }
            if (op == Kernel.OP_MEMCPY) { r.Add(new Operand("src", Src, "mem")); r.Add(new Operand("dst", Dst, "mem")); return r; }
            if (op == Kernel.OP_BF) { r.Add(new Operand("cond", Cond, "b")); return r; }
            if (op == Kernel.OP_JMPI) { r.Add(new Operand("src", Src, "i")); return r; }
            if (Kernel.Moves.ContainsKey(op)) { int sz = Kernel.Moves[op]; r.Add(new Operand("src", Src, sz)); r.Add(new Operand("dst", Dst, sz)); return r; }
            return r;
        }

        /// <summary>code offsets this instruction can transfer to besides next</summary>
        public List<uint> Targets()
        {
            if (Op == Kernel.OP_SWITCH) return Cases.Select(c => c[1]).ToList();
            if (Op == Kernel.OP_BF || Op == Kernel.OP_JSR) return new List<uint> { Target };
            return new List<uint>();
        }

        /// <summary>whether next is used (halt, ret and jmpi ignore it)</summary>
        public bool FallsThrough() { return Op != Kernel.OP_HALT && Op != Kernel.OP_RET && Op != Kernel.OP_JMPI; }
    }

    /// <summary>
    /// ExpressionKernel blobs: the bytecode of FIFA 17's expression VM, as the game reads and runs it.
    ///   The game loads it, fills its call slots from the native table (an unknown hash crashes the game),
    ///   sets up its state and DofMapping, then runs it.
    /// Blob (little-endian): +0x00 u64 0; +0x08 u32 kernel id; +0x0c 4xu32 0xFFFFFFFF (not known); +0x1c code
    /// words; +0x20 constant relocations; +0x24 state relocations; +0x28 state size; +0x2c 0xFFFFFFFF (not known);
    /// +0x30 constants size; +0x34 temporaries; +0x38/3c/40 max inputs/outputs/state operands; +0x44 event channels;
    /// +0x48 event nodes; +0x4c call-stack entries; +0x50 constants; code at align4; relocations; state at align16.
    /// Operand = segment &lt;&lt; 24 | offset (0 constants, 1 state registers, 2 temporaries), 0xFFFFFFFF = none.
    /// Instruction word = op | next &lt;&lt; 8 (byte offset of the following instruction); execution ends at op 0x16.
    /// </summary>
    public sealed class Kernel
    {
        public const uint NONE = 0xFFFFFFFF;
        public const int HDR = 0x50;
        public const int OP_SWITCH = 0x02, OP_CALL = 0x03;
        public const int OP_SUB = 0x0F, OP_UNSUB = 0x10, OP_EMIT = 0x11, OP_MEMCPY = 0x12;
        public const int OP_BF = 0x13, OP_JSR = 0x14, OP_RET = 0x15, OP_HALT = 0x16, OP_JMPI = 0x19;

        public static readonly Dictionary<int, int[]> Fixed = new Dictionary<int, int[]>
        {
            { 0x1A, new[] { 1, 1 } }, { 0x1B, new[] { 1, 2 } }, { 0x1C, new[] { 1, 3 } }, { 0x1D, new[] { 1, 4 } }, { 0x1E, new[] { 2, 0 } },
            { 0x1F, new[] { 2, 1 } }, { 0x20, new[] { 3, 0 } }, { 0x21, new[] { 3, 1 } }, { 0x22, new[] { 4, 0 } }, { 0x23, new[] { 4, 1 } },
        };

        public static int? FixedOf(int nin, int nout)
        {
            foreach (KeyValuePair<int, int[]> kv in Fixed) if (kv.Value[0] == nin && kv.Value[1] == nout) return kv.Key;
            return null;
        }

        public static readonly Dictionary<int, int> Moves = new Dictionary<int, int> { { 0x24, 1 }, { 0x25, 2 }, { 0x26, 4 }, { 0x27, 8 }, { 0x28, 16 } };
        public static int MoveOp(int size) { foreach (KeyValuePair<int, int> kv in Moves) if (kv.Value == size) return kv.Key; return -1; }
        public static bool IsCall(int op) { return op == OP_CALL || Fixed.ContainsKey(op); }

        public static readonly Dictionary<int, string> Segs = new Dictionary<int, string> { { 0, "c" }, { 1, "s" }, { 2, "t" } };

        public static readonly Dictionary<string, int> TypeSize = new Dictionary<string, int>
        {
            { "b", 1 }, { "i", 4 }, { "f", 4 }, { "v", 16 }, { "q", 16 }, { "m34", 48 }, { "m44", 64 }, { "dof", 8 }, { "pose", 8 },
            { "obj", 8 }, { "ctx", 8 }, { "u8", 1 }, { "u16", 2 }, { "mem", 16 }, { "?", 16 },
        };

        public static readonly Dictionary<uint, string> Ifaces = new Dictionary<uint, string>
        {
            { 0x0BD87F16, "float" }, { 0x01C0B376, "bool" }, { 0xBAD57F47, "vec3" }, { 0x12FF6890, "int" },
        };

        public static uint? IfaceOf(string name)
        {
            foreach (KeyValuePair<uint, string> kv in Ifaces) if (kv.Value == name) return kv.Key;
            return null;
        }

        // ------------------------------------------------------------------ natives
        private static Dictionary<uint, Native> natives;
        private static Dictionary<string, List<uint>> byName;

        public static Dictionary<uint, Native> Natives
        {
            get
            {
                if (natives == null)
                {
                    JObject d = (JObject)CoreData.Json("kernel_natives.json");
                    Dictionary<uint, Native> r = new Dictionary<uint, Native>();
                    foreach (KeyValuePair<string, object> kv in d.Obj("entries"))
                    {
                        JObject e = (JObject)kv.Value;
                        Native n = new Native
                        {
                            Hash = Convert.ToUInt32(kv.Key, 16), Fn = e.Str("fn"), Name = e.Str("name"), Doc = e.Str("doc"),
                            In = e.Arr("in").Cast<string>().ToList(), Out = e.Arr("out").Cast<string>().ToList(), State = e.Arr("state").Cast<string>().ToList(),
                            InRepeat = e.ContainsKey("in_repeat") ? e.Arr("in_repeat").Cast<string>().ToList() : null,
                        };
                        r[n.Hash] = n;
                    }
                    natives = r;
                }
                return natives;
            }
        }

        public static Native NativeOf(uint hash) { Native n; return Natives.TryGetValue(hash, out n) ? n : null; }

        /// <summary>preferred hash for a name (the most used one in the shipped file, else the lowest)</summary>
        public static readonly Dictionary<string, uint> Preferred = new Dictionary<string, uint>
        {
            { "vec.add", 0x9988C791 }, { "vec.sub", 0x4FE65284 }, { "vec.mul", 0xAEC377CE }, { "vec.mul_f32", 0xD8353A9B },
            { "vec.lerp", 0x3AE57534 }, { "gs.get.vec", 0xAE764B0E }, { "gs.set.ref", 0x45AF06F4 },
        };

        public static Dictionary<string, List<uint>> NativeByName()
        {
            if (byName == null)
            {
                Dictionary<string, List<uint>> r = new Dictionary<string, List<uint>>();
                foreach (uint h in Natives.Keys.OrderBy(x => x))
                {
                    string n = Natives[h].Name;
                    List<uint> l;
                    if (!r.TryGetValue(n, out l)) { l = new List<uint>(); r[n] = l; }
                    l.Add(h);
                }
                byName = r;
            }
            return byName;
        }

        /// <summary>type codes of n operands of group key ("in", "out", "state") of native e</summary>
        public static List<string> SigTypes(Native e, string key, int n)
        {
            List<string> sig = e == null ? new List<string>() : new List<string>(key == "in" ? e.In : key == "out" ? e.Out : e.State);
            List<string> rep = e != null && key == "in" ? e.InRepeat : null;
            while (sig.Count < n)
            {
                if (rep != null && rep.Count > 0) sig.AddRange(rep);
                else sig.Add("?");
            }
            return sig.Take(n).ToList();
        }

        public static bool SigOk(Native e, int nin, int nout, int nst)
        {
            if (e.InRepeat != null)
            {
                int r = e.InRepeat.Count;
                return nin >= e.In.Count && (nin - e.In.Count) % r == 0 && nout == e.Out.Count && nst == e.State.Count;
            }
            return nin == e.In.Count && nout == e.Out.Count && nst == e.State.Count;
        }

        public static uint Align(uint x, uint a) { return (x + a - 1) & ~(a - 1); }
        public static int Align(int x, int a) { return (x + a - 1) & ~(a - 1); }

        private static uint U32(byte[] b, int p) { return BE.U32LE(b, p); }

        // ------------------------------------------------------------------ fields
        public ulong Raw0;
        public uint Kid;
        public uint[] Unk = { NONE, NONE, NONE, NONE };
        public uint Unk2c = NONE;
        public uint Temps, MaxIn, MaxOut, MaxState, Channels, Nodes, Callstack;
        public List<byte> Consts = new List<byte>();
        public List<Ins> Code = new List<Ins>();
        public List<uint[]> RelocC = new List<uint[]>(), RelocS = new List<uint[]>();
        public List<byte> StatePrefix = new List<byte>(new byte[0x20]);
        public List<byte> Regs = new List<byte>();

        /// <summary>the linker's walk</summary>
        public static List<Ins> DecodeCode(byte[] code)
        {
            int n = code.Length;
            int p = 0;
            List<Ins> outp = new List<Ins>();
            Func<int, uint> W = q =>
            {
                if (q + 4 > n) throw new KernelException("instruction at code+0x" + p.ToString("x") + " runs past the end of the code");
                return U32(code, q);
            };
            while (p < n)
            {
                uint w = W(p);
                int op = (int)(w & 0xFF);
                uint nx = w >> 8;
                int q = p + 4;
                Ins ins = new Ins(op) { Next = nx };
                if (op == OP_SWITCH)
                {
                    uint sel = W(q), cnt = W(q + 4);
                    q += 8;
                    if ((long)q + 8L * cnt > n) throw new KernelException("switch at code+0x" + p.ToString("x") + " runs past the end of the code");
                    ins.Sel = sel;
                    for (int j = 0; j < cnt; j++) ins.Cases.Add(new[] { W(q + 4 * j), W(q + 4 * ((int)cnt + j)) });
                    q += 8 * (int)cnt;
                }
                else if (IsCall(op))
                {
                    uint lo = W(q), hi = W(q + 4), h = W(q + 8), x = W(q + 12);
                    q += 16;
                    ins.Hash = h;
                    ins.SlotFn = lo | ((ulong)hi << 32);
                    ins.SlotX = x;
                    if (op == OP_CALL)
                    {
                        List<uint>[] grps = { ins.InOps, ins.Outs, ins.StateOps };
                        for (int g = 0; g < 3; g++)
                        {
                            uint cnt = W(q);
                            q += 4;
                            if ((long)q + 4L * cnt > n) throw new KernelException("call at code+0x" + p.ToString("x") + " runs past the end of the code");
                            for (int j = 0; j < cnt; j++) grps[g].Add(W(q + 4 * j));
                            q += 4 * (int)cnt;
                        }
                        ins.Generic = true;
                    }
                    else
                    {
                        int ni = Fixed[op][0], no = Fixed[op][1];
                        for (int j = 0; j < ni + no; j++) (j < ni ? ins.InOps : ins.Outs).Add(W(q + 4 * j));
                        q += 4 * (ni + no);
                        ins.Generic = false;
                    }
                }
                else if (op == OP_SUB) { ins.Chan = W(q) | ((ulong)W(q + 4) << 32); ins.Node = W(q + 8); q += 12; }
                else if (op == OP_UNSUB) { ins.Node = W(q); q += 4; }
                else if (op == OP_JMPI) { ins.Src = W(q); q += 4; }
                else if (op == OP_EMIT) { ins.Chan = W(q) | ((ulong)W(q + 4) << 32); ins.Value = W(q + 8); ins.Data = W(q + 12); q += 16; }
                else if (op == OP_MEMCPY) { ins.Src = W(q); ins.Dst = W(q + 4); ins.Size = W(q + 8); q += 12; }
                else if (op == OP_BF) { ins.Cond = W(q); ins.Target = W(q + 4); q += 8; }
                else if (op == OP_JSR) { ins.Target = W(q); q += 4; }
                else if (Moves.ContainsKey(op)) { ins.Src = W(q); ins.Dst = W(q + 4); q += 8; }
                ins.Off = p;
                outp.Add(ins);
                p = q;
            }
            return outp;
        }

        /// <summary>offset of the state registers in the ExpressionState</summary>
        public static long Seg1Base(uint channels, uint nodes, uint callstack)
        {
            return (0x20L * channels + 0x28L * nodes + 4L * callstack + 0x2F) & ~0xFL;
        }

        /// <summary>largest state header part the editor builds (the game allocates it per instance)</summary>
        public const long MaxSegment = 0x1000000;

        public static Kernel Parse(byte[] blob)
        {
            if (blob.Length < HDR) throw new KernelException("kernel shorter than its 0x50-byte header");
            Kernel k = new Kernel();
            k.Raw0 = BE.U64LE(blob, 0);
            uint[] h = new uint[18];
            for (int i = 0; i < 18; i++) h[i] = U32(blob, 8 + 4 * i);
            k.Kid = h[0];
            k.Unk = new[] { h[1], h[2], h[3], h[4] };
            uint codeWords = h[5], nrc = h[6], nrs = h[7], stateSize = h[8];
            k.Unk2c = h[9];
            uint dataSize = h[10];
            k.Temps = h[11]; k.MaxIn = h[12]; k.MaxOut = h[13]; k.MaxState = h[14]; k.Channels = h[15]; k.Nodes = h[16]; k.Callstack = h[17];
            if (HDR + (long)dataSize > blob.Length) throw new KernelException("constants run past the end of the blob");
            k.Consts = new List<byte>(blob.Skip(HDR).Take((int)dataSize));
            long c0 = Align(HDR + (int)dataSize, 4);
            long c1 = c0 + 4L * codeWords;
            long r1 = c1 + 8L * (nrc + nrs);
            long s0 = (r1 + 15) & ~15L;
            if (s0 + stateSize != blob.Length)
                throw new KernelException("blob size 0x" + blob.Length.ToString("x") + ", the header describes 0x" + (s0 + stateSize).ToString("x"));
            for (long i = HDR + dataSize; i < c0; i++) if (blob[i] != 0) throw new KernelException("non-zero alignment padding");
            for (long i = r1; i < s0; i++) if (blob[i] != 0) throw new KernelException("non-zero alignment padding");
            byte[] code = new byte[c1 - c0];
            Buffer.BlockCopy(blob, (int)c0, code, 0, code.Length);
            k.Code = DecodeCode(code);
            for (int j = 0; j < nrc + nrs; j++)
            {
                uint[] r = { U32(blob, (int)c1 + 8 * j), U32(blob, (int)c1 + 8 * j + 4) };
                (j < nrc ? k.RelocC : k.RelocS).Add(r);
            }
            long bas = Seg1Base(k.Channels, k.Nodes, k.Callstack);
            long st = s0;
            if (blob.Length - st < bas) throw new KernelException("state template (0x" + (blob.Length - st).ToString("x") + " bytes) shorter than its header part (0x" + bas.ToString("x") + ")");
            k.StatePrefix = new List<byte>(blob.Skip((int)st).Take((int)bas));
            k.Regs = new List<byte>(blob.Skip((int)(st + bas)));
            foreach (Ins i in k.Code) i.NextDefault = false;
            return k;
        }

        /// <summary>assign instruction offsets; returns the code size in bytes</summary>
        public int Layout()
        {
            int p = 0;
            foreach (Ins i in Code) { i.Off = p; p += 4 * i.Words(); }
            return p;
        }

        /// <summary>next of instruction n when not given: the following instruction, 0 for the last one</summary>
        public uint DefaultNext(int n) { return n + 1 < Code.Count ? (uint)Code[n + 1].Off : 0; }

        public byte[] Serialize()
        {
            Layout();
            List<uint> words = new List<uint>();
            for (int n = 0; n < Code.Count; n++)
            {
                Ins i = Code[n];
                if (i.NextDefault) i.Next = DefaultNext(n);
                words.AddRange(i.Encode());
            }
            long lbas = Seg1Base(Channels, Nodes, Callstack);
            if (lbas > MaxSegment) throw new KernelException("the state header part (0x" + lbas.ToString("x") + " bytes for the event channels, nodes and call stack) is too large");
            int bas = (int)lbas;
            List<byte> pre = StatePrefix.Take(bas).ToList();
            while (pre.Count < bas) pre.Add(0);
            List<byte> state = new List<byte>(pre);
            state.AddRange(Regs);
            List<byte> b = new List<byte>();
            byte[] hdr = new byte[HDR];
            BE.W64LE(hdr, 0, Raw0);
            uint[] h = { Kid, Unk[0], Unk[1], Unk[2], Unk[3], (uint)words.Count, (uint)RelocC.Count, (uint)RelocS.Count, (uint)state.Count, Unk2c,
                         (uint)Consts.Count, Temps, MaxIn, MaxOut, MaxState, Channels, Nodes, Callstack };
            for (int i = 0; i < 18; i++) BE.W32LE(hdr, 8 + 4 * i, h[i]);
            b.AddRange(hdr);
            b.AddRange(Consts);
            while (b.Count % 4 != 0) b.Add(0);
            byte[] w4 = new byte[4];
            foreach (uint w in words) { BE.W32LE(w4, 0, w); b.AddRange(w4); }
            foreach (uint[] r in RelocC.Concat(RelocS)) { BE.W32LE(w4, 0, r[0]); b.AddRange(w4); BE.W32LE(w4, 0, r[1]); b.AddRange(w4); }
            while (b.Count % 16 != 0) b.Add(0);
            b.AddRange(state);
            return b.ToArray();
        }

        public Dictionary<int, int> ByOffset()
        {
            if (Code.Any(i => i.Off < 0)) Layout();
            Dictionary<int, int> r = new Dictionary<int, int>();
            for (int n = 0; n < Code.Count; n++) r[Code[n].Off] = n;
            return r;
        }

        public sealed class Needs
        {
            public long Temps;
            public uint MaxIn, MaxOut, MaxState;
            public uint? Callstack;
        }

        /// <summary>what the code requires of the header: temps, max in/out/state, callstack depth</summary>
        public Needs GetNeeds()
        {
            long temps = 0;
            uint mi = 0, mo = 0, ms = 0;
            byte[] consts = Consts.ToArray();
            foreach (Ins i in Code)
            {
                if (IsCall(i.Op))
                {
                    mi = Math.Max(mi, (uint)i.InOps.Count);
                    mo = Math.Max(mo, (uint)i.Outs.Count);
                    ms = Math.Max(ms, (uint)i.StateOps.Count);
                }
                foreach (Ins.Operand o in i.Operands())
                {
                    if (o.X == NONE || o.X >> 24 != 2) continue;
                    long size = OperandSize(i, o.Role, o.Type, consts);
                    temps = Math.Max(temps, (o.X & 0xFFFFFF) + size);
                }
            }
            return new Needs { Temps = temps, MaxIn = mi, MaxOut = mo, MaxState = ms, Callstack = CallDepth() };
        }

        /// <summary>deepest jsr nesting reachable from offset 0 (null if unbounded: recursion)</summary>
        public uint? CallDepth()
        {
            Dictionary<int, int> idx = ByOffset();
            Dictionary<long, uint?> memo = new Dictionary<long, uint?>();
            Func<long, HashSet<long>, uint?> depthFrom = null;
            depthFrom = (start, stack) =>
            {
                uint? m;
                if (memo.TryGetValue(start, out m)) return m;
                if (stack.Contains(start)) return null;
                if (stack.Count > 900) throw new KernelException("subroutine nesting too deep to analyse");
                HashSet<long> st2 = new HashSet<long>(stack) { start };
                uint best = 0;
                HashSet<long> seen = new HashSet<long>();
                List<long> todo = new List<long> { start };
                while (todo.Count > 0)
                {
                    long off = todo[todo.Count - 1];
                    todo.RemoveAt(todo.Count - 1);
                    if (seen.Contains(off) || !idx.ContainsKey((int)off)) continue;
                    seen.Add(off);
                    Ins i = Code[idx[(int)off]];
                    if (i.Op == OP_JSR)
                    {
                        uint? d = depthFrom(i.Target, st2);
                        if (!d.HasValue) return null;
                        best = Math.Max(best, 1 + d.Value);
                        todo.Add(i.Next);
                        continue;
                    }
                    if (i.Op == OP_HALT || i.Op == OP_RET || i.Op == OP_JMPI) continue;
                    todo.Add(i.Next);
                    foreach (uint t in i.Targets()) todo.Add(t);
                }
                memo[start] = best;
                return best;
            };
            if (Code.Count == 0) return 0;
            return depthFrom(Code[0].Off, new HashSet<long>());
        }

        /// <summary>set temps / max counts / callstack to what the code needs (or keep larger values)</summary>
        public Needs UpdateHeader(bool keepLarger = true)
        {
            Needs need = GetNeeds();
            if (!need.Callstack.HasValue) throw new KernelException("subroutine recursion: the call stack depth is unbounded");
            if (!keepLarger || Temps < need.Temps)
            {
                if (need.Temps > uint.MaxValue) throw new KernelException("the code needs 0x" + need.Temps.ToString("x") + " bytes of temporaries, more than the header holds");
                Temps = (uint)need.Temps;
            }
            if (!keepLarger || MaxIn < need.MaxIn) MaxIn = need.MaxIn;
            if (!keepLarger || MaxOut < need.MaxOut) MaxOut = need.MaxOut;
            if (!keepLarger || MaxState < need.MaxState) MaxState = need.MaxState;
            if (!keepLarger || Callstack < need.Callstack.Value) Callstack = need.Callstack.Value;
            return need;
        }

        /// <summary>bytes an operand of the given type covers</summary>
        public static long OperandSize(Ins ins, string role, object t, byte[] consts)
        {
            if (t is int) return (int)t;
            string ts = (string)t;
            if (ts == "mem")
            {
                if (ins.Op == OP_MEMCPY) return ins.Size;
                // DOF read / write: the u8 size operand (input 3) when it is a constant
                if (IsCall(ins.Op) && ins.InOps.Count >= 4)
                {
                    uint x = ins.InOps[3];
                    if (x != NONE && x >> 24 == 0 && (x & 0xFFFFFF) < consts.Length) return consts[x & 0xFFFFFF];
                }
                return 16;
            }
            int s;
            return TypeSize.TryGetValue(ts, out s) ? s : 16;
        }

        // ------------------------------------------------------------------ checks
        /// <summary>[(level, message)] of what would break in the game (level "error" or "warning")</summary>
        public static List<Tuple<string, string>> Check(Kernel k, KernelBindings bindings = null)
        {
            List<Tuple<string, string>> r = new List<Tuple<string, string>>();
            Dictionary<uint, Native> nat = Natives;
            k.Layout();
            HashSet<long> offs = new HashSet<long>(k.Code.Select(i => (long)i.Off));
            int nconst = k.Consts.Count, nregs = k.Regs.Count;
            byte[] consts = k.Consts.ToArray();
            Needs need = null;
            try { need = k.GetNeeds(); }
            catch (KernelException) { r.Add(Tuple.Create("error", "subroutine nesting too deep to analyse")); }
            if (k.Code.Count == 0)
            {
                r.Add(Tuple.Create("error", "no code (the VM would run whatever follows)"));
                return r;
            }
            foreach (Ins i in k.Code)
            {
                string where = "code+0x" + i.Off.ToString("x");
                if (IsCall(i.Op) && !nat.ContainsKey(i.Hash))
                    r.Add(Tuple.Create("error", where + ": native " + i.Hash.ToString("x8") + " does not exist in the game (the game crashes)"));
                foreach (Ins.Operand o in i.Operands())
                {
                    if (o.X == NONE)
                    {
                        if (!(IsCall(i.Op) && nat.ContainsKey(i.Hash) && nat[i.Hash].Name == "nop"))
                            r.Add(Tuple.Create("error", where + ": " + o.Role + " operand is empty (the VM would use address 0)"));
                        continue;
                    }
                    int seg = (int)(o.X >> 24);
                    long off = o.X & 0xFFFFFF;
                    long size = (o.Type is string && (string)o.Type == "?") ? 1 : OperandSize(i, o.Role, o.Type, consts);
                    long? lim = seg == 0 ? nconst : seg == 1 ? nregs : seg == 2 ? (long?)k.Temps : null;
                    if (!lim.HasValue) r.Add(Tuple.Create("warning", where + ": " + o.Role + " operand " + KernelText.FmtOp(o.X) + " addresses VM internals"));
                    else if (off + size > lim.Value)
                        r.Add(Tuple.Create("error", where + ": " + o.Role + " operand " + KernelText.FmtOp(o.X) + " (" + size + " bytes) past the end of its segment (0x" + lim.Value.ToString("x") + ")"));
                    if (seg == 0 && (o.Role == "out" || o.Role == "dst" || o.Role == "state"))
                        r.Add(Tuple.Create("warning", where + ": writes the constant " + KernelText.FmtOp(o.X) + " (shared by every instance)"));
                }
                if (i.FallsThrough() && !offs.Contains(i.Next))
                    r.Add(Tuple.Create("error", where + ": next instruction 0x" + i.Next.ToString("x") + " is not an instruction"));
                foreach (uint t in i.Targets())
                    if (!offs.Contains(t)) r.Add(Tuple.Create("error", where + ": jump target 0x" + t.ToString("x") + " is not an instruction"));
                if (IsCall(i.Op) && nat.ContainsKey(i.Hash))
                {
                    Native e = nat[i.Hash];
                    if (!SigOk(e, i.InOps.Count, i.Outs.Count, i.StateOps.Count))
                    {
                        string extra = e.InRepeat != null ? " (+ " + e.InRepeat.Count + " per repeat)" : "";
                        r.Add(Tuple.Create("warning", where + ": " + e.Name + " takes " + e.In.Count + extra + "/" + e.Out.Count + "/" + e.State.Count +
                                                      " inputs/outputs/state operands, " + i.InOps.Count + "/" + i.Outs.Count + "/" + i.StateOps.Count + " given"));
                    }
                }
            }
            // events: channel ids live in the state header records (u64 at 0x30 + 0x20 k; records past the prefix read as 0)
            HashSet<ulong> chans = new HashSet<ulong>();
            byte[] pa = k.StatePrefix.ToArray();
            for (long j = 0; j < k.Channels; j++)
            {
                long at = 0x30 + 0x20 * j;
                if (at + 8 <= pa.Length) { chans.Add(BE.U64LE(pa, (int)at)); continue; }
                ulong part = 0;
                for (long q = at; q < at + 8 && q < pa.Length; q++) part |= (ulong)pa[q] << (int)(8 * (q - at));
                chans.Add(part);
                if (at >= pa.Length) break;
            }
            HashSet<ulong> subbed = new HashSet<ulong>();
            foreach (Ins i in k.Code)
            {
                string where = "code+0x" + i.Off.ToString("x");
                if ((i.Op == OP_SUB || i.Op == OP_UNSUB) && !(i.Node < k.Nodes))
                    r.Add(Tuple.Create("error", where + ": event node " + i.Node + " outside the " + k.Nodes + " node records (the game writes past them)"));
                if (i.Op == OP_SUB)
                {
                    if (chans.Contains(i.Chan)) subbed.Add(i.Chan);
                    else r.Add(Tuple.Create("warning", where + ": no event channel 0x" + i.Chan.ToString("x") + " in the state header (the node is only unlinked)"));
                }
            }
            foreach (Ins i in k.Code)
                if (i.Op == OP_EMIT && subbed.Contains(i.Chan))
                    r.Add(Tuple.Create("warning", "code+0x" + i.Off.ToString("x") + ": emit on channel 0x" + i.Chan.ToString("x") + ", which nodes subscribe to: the game runs kernels " +
                                                  "without events and crashes if a node is subscribed when this runs"));
            if (need != null)
            {
                Tuple<string, string, long, long?>[] fs =
                {
                    Tuple.Create("temps", "temporaries size", (long)k.Temps, (long?)need.Temps), Tuple.Create("max_in", "max inputs", (long)k.MaxIn, (long?)need.MaxIn),
                    Tuple.Create("max_out", "max outputs", (long)k.MaxOut, (long?)need.MaxOut), Tuple.Create("max_state", "max state operands", (long)k.MaxState, (long?)need.MaxState),
                    Tuple.Create("callstack", "call stack", (long)k.Callstack, need.Callstack.HasValue ? (long?)need.Callstack.Value : null),
                };
                foreach (Tuple<string, string, long, long?> f in fs)
                {
                    if (!f.Item4.HasValue) r.Add(Tuple.Create("error", "recursive subroutine calls (the call stack would overflow)"));
                    else if (f.Item3 < f.Item4.Value) r.Add(Tuple.Create("error", "header " + f.Item2 + " " + f.Item3 + " is less than the code needs (" + f.Item4.Value + ")"));
                }
            }
            // reachability, halt and loops
            Dictionary<int, int> idx = new Dictionary<int, int>();
            for (int n = 0; n < k.Code.Count; n++) idx[k.Code[n].Off] = n;
            HashSet<long> seen = new HashSet<long>();
            List<long> stk = new List<long> { k.Code[0].Off };
            bool back = false;
            while (stk.Count > 0)
            {
                long off = stk[stk.Count - 1];
                stk.RemoveAt(stk.Count - 1);
                if (seen.Contains(off) || !idx.ContainsKey((int)off)) continue;
                seen.Add(off);
                Ins i = k.Code[idx[(int)off]];
                List<uint> nx = new List<uint>();
                if (i.FallsThrough()) nx.Add(i.Next);
                nx.AddRange(i.Targets());
                foreach (uint t in nx)
                {
                    if (t <= off && i.Op != OP_JSR) back = true;
                    stk.Add(t);
                }
            }
            if (!seen.Any(o => k.Code[idx[(int)o]].Op == OP_HALT)) r.Add(Tuple.Create("error", "no reachable halt (op 0x16): the VM never stops"));
            if (back) r.Add(Tuple.Create("warning", "backward jumps: the VM has no loop limit, make sure every loop ends"));
            foreach (uint[] rc in k.RelocC)
                if (rc[0] + 8L > nconst || rc[1] > nconst) r.Add(Tuple.Create("error", "constant relocation c:" + rc[0].ToString("x4") + " -> c:" + rc[1].ToString("x4") + " outside the constants"));
            foreach (uint[] rs in k.RelocS)
                if (rs[0] + 8L > nregs || rs[1] > nregs) r.Add(Tuple.Create("error", "state relocation s:" + rs[0].ToString("x4") + " -> s:" + rs[1].ToString("x4") + " outside the registers"));
            if (bindings != null)
            {
                foreach (Tuple<uint, int, string> reg in bindings.Registers())
                {
                    int seg = (int)(reg.Item1 >> 24);
                    long off = reg.Item1 & 0xFFFFFF;
                    if (seg == 0)
                    {
                        r.Add(Tuple.Create("warning", reg.Item3 + " is bound to the constant " + KernelText.FmtOp(reg.Item1)));
                        if (off + reg.Item2 > nconst) r.Add(Tuple.Create("error", reg.Item3 + " register " + KernelText.FmtOp(reg.Item1) + " past the constants"));
                    }
                    else if (off + reg.Item2 > nregs)
                        r.Add(Tuple.Create("error", reg.Item3 + " register " + KernelText.FmtOp(reg.Item1) + " past the state registers (0x" + nregs.ToString("x") + ")"));
                }
                for (int j = 0; j < bindings.Dofs.Count; j++)
                    if (string.IsNullOrEmpty(bindings.Dofs[j].Item2)) r.Add(Tuple.Create("warning", "dof" + j + " has no DOF name"));
            }
            return r;
        }

        // ------------------------------------------------------------------ ExpressionAsset glue
        public static readonly string[] KernelFields =
        {
            "ExpressionKernel", "PoseRegisterOffset", "MasterPoseRegisterOffset", "DeltaTicksRegisterOffset", "TableRegisterOffset",
            "GameStateValueAssets", "GameStateAssetOffsets", "GameStateInterfaceTypes", "DofMapping", "DofNames",
        };

        private static bool Same(object cur, object val)
        {
            if (val is byte[] || cur is byte[])
            {
                byte[] a = cur as byte[] ?? new byte[0], b = val as byte[] ?? new byte[0];
                if (cur != null && !(cur is byte[]) && cur is IList) a = ((IList)cur).Cast<object>().Select(x => (byte)Num.ToLong(x)).ToArray();
                return a.SequenceEqual(b);
            }
            IList vl = val as IList;
            if (vl != null)
            {
                IList cl = cur as IList ?? new object[0];
                if (cl.Count != vl.Count) return false;
                for (int i = 0; i < cl.Count; i++)
                {
                    if (cl[i] is ulong || vl[i] is ulong) { if (Num.ToKey(cl[i]) != Num.ToKey(vl[i])) return false; }
                    else if ((Num.ToLong(cl[i]) & NONE) != (Num.ToLong(vl[i]) & NONE)) return false;
                }
                return true;
            }
            if (cur == null) return val == null;
            return (Num.ToLong(cur) & NONE) == (Num.ToLong(val) & NONE);
        }

        /// <summary>set field f of o unless it already holds val; -> whether it changed</summary>
        private static bool Put(GdStruct o, string f, object val)
        {
            Field fd = o.Type.GetField(f);
            if (fd == null) return false;
            object cur = o[f];
            if (Same(cur, val)) return false;
            o[f] = Coerce.ToField(fd, val);
            return true;
        }

        public static List<string> ApplyBindings(KernelBindings b, GdStruct o, bool dbl = false)
        {
            string sfx = dbl ? "DBL" : "";
            List<string> changed = new List<string>();
            foreach (KeyValuePair<string, object> kv in b.Fields())
                if (Put(o, kv.Key + sfx, kv.Value)) changed.Add(kv.Key + sfx);
            return changed;
        }

        public static ulong OwnerKey(GdStruct o)
        {
            byte[] raw = o["OwnerAsset"] as byte[];
            if (raw == null && o["OwnerAsset"] is IList) raw = ((IList)o["OwnerAsset"]).Cast<object>().Select(x => (byte)Num.ToLong(x)).ToArray();
            return raw != null && raw.Length >= 8 ? BE.U64LE(raw, 0) : 0;
        }

        /// <summary>key -> "Class 'name'" of the bound game-state values (for comments)</summary>
        public static Dictionary<ulong, string> GsLabels(AntDb db, KernelBindings b)
        {
            Dictionary<ulong, string> r = new Dictionary<ulong, string>();
            foreach (Tuple<ulong, uint, uint> g in b.Gs)
            {
                if (g.Item1 == 0 || r.ContainsKey(g.Item1)) continue;
                int i = db.BlockOf(g.Item1);
                r[g.Item1] = i < 0 ? "(not in this file)" : db.TypeName(i) + " " + PyFormat.Repr(db.NameOf(i));
            }
            return r;
        }

        public static byte[] BlobOf(GdStruct o, string field)
        {
            object v = o[field];
            if (v is byte[]) return (byte[])v;
            IList l = v as IList;
            return l != null ? l.Cast<object>().Select(x => (byte)Num.ToLong(x)).ToArray() : new byte[0];
        }

        /// <summary>readable text of an ExpressionAsset's kernel (or its *DBL copy) with its bindings</summary>
        public static string ExportText(AntDb db, GdObj o, bool dbl = false)
        {
            string sfx = dbl ? "DBL" : "";
            byte[] blob = BlobOf(o, "ExpressionKernel" + sfx);
            if (blob.Length == 0) throw new KernelException("the ExpressionAsset has no ExpressionKernel" + sfx);
            Kernel k = Parse(blob);
            KernelBindings b = KernelBindings.FromObj(o, dbl);
            ulong ow = OwnerKey(o);
            int oi = ow != 0 ? db.BlockOf(ow) : -1;
            string title = "ExpressionAsset " + BE.KeyHex(o.Key) + (dbl ? " (ExpressionKernelDBL: not loaded by the game)" : "") + ", owner " +
                           (oi >= 0 ? db.TypeName(oi) + " " + BE.KeyHex(ow) + " " + PyFormat.Repr(db.NameOf(oi)) : BE.KeyHex(ow));
            return KernelText.Disassemble(k, b, title, GsLabels(db, b));
        }

        public sealed class ImportResult
        {
            public List<string> Changed = new List<string>();
            public List<string> Notes = new List<string>();
            public List<Tuple<string, string>> Issues = new List<Tuple<string, string>>();
        }

        /// <summary>assemble text into the ExpressionAsset o. dbl: "both" (ExpressionKernel and the *DBL copy, like the
        /// shipped file), "main" (only the loaded fields) or "dbl"</summary>
        public static ImportResult ImportText(GdObj o, string text, string dbl = "both", bool force = false)
        {
            KernelBindings b;
            List<string> notes;
            Kernel k = KernelText.Assemble(text, out b, out notes);
            ImportResult res = new ImportResult { Notes = notes, Issues = Check(k, b) };
            List<string> errs = res.Issues.Where(x => x.Item1 == "error").Select(x => x.Item2).ToList();
            if (errs.Count > 0 && !force) throw new KernelException("the kernel would break in the game:\n  " + string.Join("\n  ", errs));
            byte[] blob = k.Serialize();
            bool[] targets = dbl == "both" ? new[] { false, true } : dbl == "main" ? new[] { false } : new[] { true };
            foreach (bool d in targets)
            {
                string sfx = d ? "DBL" : "";
                if (Put(o, "ExpressionKernel" + sfx, blob)) res.Changed.Add("ExpressionKernel" + sfx);
                if (b != null) res.Changed.AddRange(ApplyBindings(b, o, d));
            }
            return res;
        }

        /// <summary>(instructions, natives {name: count}, code bytes) of an ExpressionAsset's kernel</summary>
        public static Tuple<int, Dictionary<string, int>, int> Summary(GdStruct o)
        {
            byte[] blob = BlobOf(o, "ExpressionKernel");
            if (blob.Length == 0) return Tuple.Create(0, new Dictionary<string, int>(), 0);
            Kernel k = Parse(blob);
            Dictionary<string, int> used = new Dictionary<string, int>();
            foreach (Ins i in k.Code)
                if (IsCall(i.Op))
                {
                    Native n = NativeOf(i.Hash);
                    string nm = n != null ? n.Name : "native[" + i.Hash.ToString("x8") + "]";
                    int c;
                    used.TryGetValue(nm, out c);
                    used[nm] = c + 1;
                }
            return Tuple.Create(k.Code.Count, used, k.Layout());
        }
    }

    /// <summary>
    /// the ExpressionAsset fields that tie registers to the outside:
    /// pose / master_pose / delta_ticks / table registers, game-state value objects and DOF mappings
    /// </summary>
    public sealed class KernelBindings
    {
        public static readonly string[][] RegFields =
        {
            new[] { "pose", "PoseRegisterOffset" }, new[] { "master_pose", "MasterPoseRegisterOffset" },
            new[] { "delta_ticks", "DeltaTicksRegisterOffset" }, new[] { "table", "TableRegisterOffset" },
        };

        public readonly Dictionary<string, uint> Regs = new Dictionary<string, uint>();
        public readonly List<Tuple<ulong, uint, uint>> Gs = new List<Tuple<ulong, uint, uint>>();             // (key, operand, iface)
        public readonly List<Tuple<string, string, uint, uint>> Dofs = new List<Tuple<string, string, uint, uint>>();   // (group, dof, handle, default)

        public KernelBindings()
        {
            foreach (string[] r in RegFields) Regs[r[0]] = Kernel.NONE;
        }

        private static List<long> Longs(object v)
        {
            List<long> r = new List<long>();
            if (v is byte[]) { foreach (byte b in (byte[])v) r.Add(b); return r; }
            IList l = v as IList;
            if (l != null) foreach (object x in l) r.Add(Num.ToLong(x));
            return r;
        }

        public static KernelBindings FromObj(GdStruct o, bool dbl = false)
        {
            string sfx = dbl ? "DBL" : "";
            KernelBindings b = new KernelBindings();
            foreach (string[] r in RegFields)
            {
                object x = o[r[1] + sfx];
                b.Regs[r[0]] = x == null ? Kernel.NONE : (uint)(Num.ToLong(x) & Kernel.NONE);
            }
            List<ulong> keys = (o["GameStateValueAssets" + sfx] as IList ?? new object[0]).Cast<object>().Select(Num.ToKey).ToList();
            List<long> offs = Longs(o["GameStateAssetOffsets" + sfx]);
            List<long> ifs = Longs(o["GameStateInterfaceTypes" + sfx]);
            int n = Math.Max(keys.Count, Math.Max(offs.Count, ifs.Count));
            for (int j = 0; j < n; j++)
                b.Gs.Add(Tuple.Create(j < keys.Count ? keys[j] : 0UL, j < offs.Count ? (uint)(offs[j] & Kernel.NONE) : Kernel.NONE, j < ifs.Count ? (uint)(ifs[j] & Kernel.NONE) : 0u));
            byte[] names = Kernel.BlobOf(o, "DofNames" + sfx);
            List<long> dm = Longs(o["DofMapping" + sfx]);
            Func<long, string> s = at =>
            {
                if (at < 0 || at >= names.Length) return "";
                int e = Array.IndexOf(names, (byte)0, (int)at);
                return BE.Latin1.GetString(names, (int)at, (e >= 0 ? e : names.Length) - (int)at);
            };
            for (int j = 0; j < dm.Count - 3; j += 4)
                b.Dofs.Add(Tuple.Create(s(dm[j]), s(dm[j + 1]), (uint)(dm[j + 2] & Kernel.NONE), (uint)(dm[j + 3] & Kernel.NONE)));
            return b;
        }

        /// <summary>{field name (without DBL): value} for the ExpressionAsset</summary>
        public Dictionary<string, object> Fields()
        {
            Dictionary<string, object> r = new Dictionary<string, object>();
            foreach (string[] rf in RegFields) r[rf[1]] = (long)Regs[rf[0]];
            r["GameStateValueAssets"] = Gs.Select(g => g.Item1).ToList();
            r["GameStateAssetOffsets"] = Gs.Select(g => (long)g.Item2).ToList();
            r["GameStateInterfaceTypes"] = Gs.Select(g => (long)g.Item3).ToList();
            List<byte> names = new List<byte>();
            List<long> dm = new List<long>();
            foreach (Tuple<string, string, uint, uint> d in Dofs)
            {
                long a = names.Count;
                names.AddRange(BE.Latin1.GetBytes(d.Item1)); names.Add(0);
                long c = names.Count;
                names.AddRange(BE.Latin1.GetBytes(d.Item2)); names.Add(0);
                dm.AddRange(new[] { a, c, (long)d.Item3, (long)d.Item4 });
            }
            r["DofMapping"] = dm;
            r["DofNames"] = names.ToArray();
            return r;
        }

        /// <summary>operand -> name, for the readable text</summary>
        public Dictionary<uint, string> Symbols()
        {
            Dictionary<uint, string> r = new Dictionary<uint, string>();
            foreach (string[] rf in RegFields) if (Regs[rf[0]] != Kernel.NONE && !r.ContainsKey(Regs[rf[0]])) r[Regs[rf[0]]] = rf[0];
            for (int j = 0; j < Gs.Count; j++) if (Gs[j].Item2 != Kernel.NONE && !r.ContainsKey(Gs[j].Item2)) r[Gs[j].Item2] = "gs" + j;
            for (int j = 0; j < Dofs.Count; j++)
            {
                if (Dofs[j].Item3 != Kernel.NONE && !r.ContainsKey(Dofs[j].Item3)) r[Dofs[j].Item3] = "dof" + j;
                if (Dofs[j].Item4 != Kernel.NONE && !r.ContainsKey(Dofs[j].Item4)) r[Dofs[j].Item4] = "dof" + j + ".def";
            }
            return r;
        }

        /// <summary>[(operand, size, name)] of every register the bindings write</summary>
        public List<Tuple<uint, int, string>> Registers()
        {
            List<Tuple<uint, int, string>> r = new List<Tuple<uint, int, string>>();
            foreach (string[] rf in RegFields)
                if (Regs[rf[0]] != Kernel.NONE) r.Add(Tuple.Create(Regs[rf[0]], rf[0] == "delta_ticks" ? 4 : 8, rf[0]));
            for (int j = 0; j < Gs.Count; j++) if (Gs[j].Item2 != Kernel.NONE) r.Add(Tuple.Create(Gs[j].Item2, 8, "gs" + j));
            for (int j = 0; j < Dofs.Count; j++) if (Dofs[j].Item3 != Kernel.NONE) r.Add(Tuple.Create(Dofs[j].Item3, 8, "dof" + j));
            return r;
        }
    }
}
