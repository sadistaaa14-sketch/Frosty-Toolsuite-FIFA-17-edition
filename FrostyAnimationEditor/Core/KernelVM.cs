using System;
using System.Collections.Generic;
using System.Linq;

namespace FrostyAnimationEditor.Core
{
    public sealed class VMError : Exception { public VMError(string m) : base(m) { } }

    public sealed class NotSimulated : Exception { public NotSimulated(string m) : base(m) { } }

    /// <summary>four float32 lanes with SSE / numpy float32 element-wise arithmetic</summary>
    public struct F4
    {
        public float X, Y, Z, W;

        public F4(float x, float y, float z, float w) { X = x; Y = y; Z = z; W = w; }

        public float this[int i]
        {
            get { switch (i) { case 0: return X; case 1: return Y; case 2: return Z; default: return W; } }
            set { switch (i) { case 0: X = value; break; case 1: Y = value; break; case 2: Z = value; break; default: W = value; break; } }
        }

        public static F4 operator +(F4 a, F4 b) { return new F4(a.X + b.X, a.Y + b.Y, a.Z + b.Z, a.W + b.W); }
        public static F4 operator -(F4 a, F4 b) { return new F4(a.X - b.X, a.Y - b.Y, a.Z - b.Z, a.W - b.W); }
        public static F4 operator *(F4 a, F4 b) { return new F4(a.X * b.X, a.Y * b.Y, a.Z * b.Z, a.W * b.W); }
        public static F4 operator /(F4 a, F4 b) { return new F4(a.X / b.X, a.Y / b.Y, a.Z / b.Z, a.W / b.W); }
        public static F4 operator *(F4 a, float s) { return new F4(a.X * s, a.Y * s, a.Z * s, a.W * s); }
        public static F4 operator *(float s, F4 a) { return new F4(s * a.X, s * a.Y, s * a.Z, s * a.W); }
        public static F4 operator /(F4 a, float s) { return new F4(a.X / s, a.Y / s, a.Z / s, a.W / s); }
        public static F4 operator +(F4 a, float s) { return new F4(a.X + s, a.Y + s, a.Z + s, a.W + s); }
        public static F4 operator -(F4 a, float s) { return new F4(a.X - s, a.Y - s, a.Z - s, a.W - s); }
        public static F4 operator -(float s, F4 a) { return new F4(s - a.X, s - a.Y, s - a.Z, s - a.W); }

        /// <summary>lanes a, b, c, d of this vector (numpy fancy indexing)</summary>
        public F4 Sw(int a, int b, int c, int d) { return new F4(this[a], this[b], this[c], this[d]); }

        /// <summary>PSHUFD v, imm</summary>
        public F4 Pshufd(int imm) { return Sw(imm & 3, (imm >> 2) & 3, (imm >> 4) & 3, (imm >> 6) & 3); }

        public float[] ToArray() { return new[] { X, Y, Z, W }; }

        public static F4 Of(IList<float> v) { return new F4(v.Count > 0 ? v[0] : 0f, v.Count > 1 ? v[1] : 0f, v.Count > 2 ? v[2] : 0f, v.Count > 3 ? v[3] : 0f); }
    }

    /// <summary>The C library's float functions as antstate models them (the game imports them from MSVCR120.dll):
    /// Python's math results (C99 special cases), NaN arguments returned as they are, the x86 default NaN for invalid
    /// arguments and a signed infinity on overflow. Callers round the double result to float32.</summary>
    public static class CrtMath
    {
        public static readonly double DNAN = BitConverter.Int64BitsToDouble(unchecked((long)0xFFF8000000000000UL));
        public const double INF = double.PositiveInfinity;

        private static bool Nan(double x) { return double.IsNaN(x); }
        private static bool Inf(double x) { return double.IsInfinity(x); }

        public static double Sinf(double x) { if (Nan(x)) return x; if (Inf(x)) return DNAN; return Math.Sin(x); }
        public static double Cosf(double x) { if (Nan(x)) return x; if (Inf(x)) return DNAN; return Math.Cos(x); }
        public static double Tanf(double x) { if (Nan(x)) return x; if (Inf(x)) return DNAN; return Math.Tan(x); }
        public static double Asinf(double x) { if (Nan(x)) return x; if (x > 1.0 || x < -1.0) return DNAN; return Math.Asin(x); }
        public static double Acosf(double x) { if (Nan(x)) return x; if (x > 1.0 || x < -1.0) return DNAN; return Math.Acos(x); }

        public static double Atanf(double x)
        {
            if (Nan(x)) return x;
            if (double.IsPositiveInfinity(x)) return Math.PI / 2;
            if (double.IsNegativeInfinity(x)) return -Math.PI / 2;
            return Math.Atan(x);
        }

        public static double Expf(double x)
        {
            if (Nan(x)) return x;
            if (double.IsPositiveInfinity(x)) return INF;
            if (double.IsNegativeInfinity(x)) return 0.0;
            double r = Math.Exp(x);
            return double.IsInfinity(r) ? INF : r;
        }

        public static double Sqrtf(double x)
        {
            if (Nan(x)) return x;
            if (x < 0) return DNAN;
            return Math.Sqrt(x);
        }

        // Python's math.floor / math.ceil return integers: a zero result is +0
        public static double Floorf(double x) { if (Nan(x) || Inf(x)) return x; double r = Math.Floor(x); return r == 0 ? 0.0 : r; }
        public static double Ceilf(double x) { if (Nan(x) || Inf(x)) return x; double r = Math.Ceiling(x); return r == 0 ? 0.0 : r; }

        public static double Logf(double x)
        {
            if (Nan(x)) return x;
            if (x == 0) return double.NegativeInfinity;
            if (x < 0) return DNAN;
            return Math.Log(x);
        }

        private static double CopySign(double mag, double sign)
        {
            long m = BitConverter.DoubleToInt64Bits(mag) & 0x7FFFFFFFFFFFFFFFL;
            long s = BitConverter.DoubleToInt64Bits(sign) & unchecked((long)0x8000000000000000UL);
            return BitConverter.Int64BitsToDouble(m | s);
        }

        /// <summary>CPython's m_atan2 (C99 special cases), after crtmath's NaN rule (the first NaN argument)</summary>
        public static double Atan2f(double y, double x)
        {
            if (Nan(y)) return y;
            if (Nan(x)) return x;
            if (Inf(y))
            {
                if (Inf(x)) return CopySign(CopySign(1.0, x) == 1.0 ? 0.25 * Math.PI : 0.75 * Math.PI, y);
                return CopySign(0.5 * Math.PI, y);
            }
            if (Inf(x) || y == 0.0)
            {
                if (CopySign(1.0, x) == 1.0) return CopySign(0.0, y);
                return CopySign(Math.PI, y);
            }
            return Math.Atan2(y, x);
        }

        /// <summary>crtmath.powf over CPython's math.pow</summary>
        public static double Powf(double x, double y)
        {
            bool nan = Nan(x) || Nan(y);
            if (nan && !(x == 1.0 || y == 0.0)) return Nan(x) ? x : y;
            bool odd = !Inf(y) && !Nan(y) && y == Math.Floor(y) && Math.Abs(y) < 9007199254740992.0 && ((long)y) % 2 != 0;
            if (x == 0 && y < 0) return odd && CopySign(1.0, x) < 0 ? double.NegativeInfinity : INF;
            // CPython math.pow
            double r;
            if (Inf(x) || Nan(x) || Inf(y) || Nan(y))
            {
                if (Nan(x)) r = y == 0.0 ? 1.0 : x;
                else if (Nan(y)) r = x == 1.0 ? 1.0 : y;
                else if (Inf(x))
                {
                    bool oddY = !Inf(y) && (Math.Abs(y) % 2.0) == 1.0;
                    if (y > 0.0) r = oddY ? x : Math.Abs(x);
                    else if (y == 0.0) r = 1.0;
                    else r = oddY ? CopySign(0.0, x) : 0.0;
                }
                else
                {
                    if (Math.Abs(x) == 1.0) r = 1.0;
                    else if (y > 0.0 && Math.Abs(x) > 1.0) r = y;
                    else if (y < 0.0 && Math.Abs(x) < 1.0) r = -y;
                    else r = 0.0;
                }
                return r;
            }
            r = Math.Pow(x, y);
            if (double.IsNaN(r)) return DNAN;                                   // (-x) ** non-integer: ValueError
            if (double.IsInfinity(r)) return x < 0 && odd ? double.NegativeInfinity : INF;     // OverflowError
            return r;
        }
    }

    /// <summary>the VM's segments: 0 constants, 1 state registers, 2 temporaries</summary>
    public sealed class VmMem
    {
        public readonly byte[][] Seg = new byte[3][];

        public VmMem(byte[] consts, byte[] regs, int temps)
        {
            Seg[0] = (byte[])consts.Clone();
            Seg[1] = (byte[])regs.Clone();
            Seg[2] = new byte[temps];
        }

        private byte[] Buf(uint x, long n, out int off)
        {
            if (x == Kernel.NONE) throw new VMError("empty operand used");
            int seg = (int)(x >> 24);
            off = (int)(x & 0xFFFFFF);
            if (seg > 2) throw new VMError("segment " + seg + " is VM-internal; not simulated");
            byte[] b = Seg[seg];
            if (off + n > b.Length) throw new VMError("operand " + KernelText.FmtOp(x) + " (" + n + " bytes) past its segment (0x" + b.Length.ToString("x") + ")");
            return b;
        }

        public byte[] Read(uint x, long n)
        {
            int o;
            byte[] b = Buf(x, n, out o);
            byte[] r = new byte[n];
            Buffer.BlockCopy(b, o, r, 0, (int)n);
            return r;
        }

        public void Write(uint x, byte[] data)
        {
            int o;
            byte[] b = Buf(x, data.Length, out o);
            Buffer.BlockCopy(data, 0, b, o, data.Length);
        }

        public float F(uint x) { int o; byte[] b = Buf(x, 4, out o); return BE.BitsFloat(BE.U32LE(b, o)); }
        public int I(uint x) { int o; byte[] b = Buf(x, 4, out o); return (int)BE.U32LE(b, o); }
        public uint U32(uint x) { int o; byte[] b = Buf(x, 4, out o); return BE.U32LE(b, o); }
        public byte B(uint x) { int o; byte[] b = Buf(x, 1, out o); return b[o]; }
        public ushort U16(uint x) { int o; byte[] b = Buf(x, 2, out o); return BE.U16LE(b, o); }
        public ulong Q64(uint x) { int o; byte[] b = Buf(x, 8, out o); return BE.U64LE(b, o); }

        public F4 V(uint x)
        {
            int o;
            byte[] b = Buf(x, 16, out o);
            return new F4(BE.BitsFloat(BE.U32LE(b, o)), BE.BitsFloat(BE.U32LE(b, o + 4)), BE.BitsFloat(BE.U32LE(b, o + 8)), BE.BitsFloat(BE.U32LE(b, o + 12)));
        }

        public float[] M(uint x, int n)
        {
            int o;
            byte[] b = Buf(x, 4L * n, out o);
            float[] r = new float[n];
            for (int j = 0; j < n; j++) r[j] = BE.BitsFloat(BE.U32LE(b, o + 4 * j));
            return r;
        }

        public void WF(uint x, float v) { byte[] d = new byte[4]; BE.W32LE(d, 0, BE.FloatBits(v)); Write(x, d); }
        public void WI(uint x, long v) { byte[] d = new byte[4]; BE.W32LE(d, 0, (uint)(v & 0xFFFFFFFF)); Write(x, d); }
        public void WB(uint x, bool v) { Write(x, new[] { (byte)(v ? 1 : 0) }); }
        public void WV(uint x, F4 v) { Write(x, Floats(v.X, v.Y, v.Z, v.W)); }
        public void WM(uint x, float[] v) { Write(x, Floats(v)); }

        public static byte[] Floats(params float[] v)
        {
            byte[] d = new byte[4 * v.Length];
            for (int j = 0; j < v.Length; j++) BE.W32LE(d, 4 * j, BE.FloatBits(v[j]));
            return d;
        }
    }

    /// <summary>a bound game-state value object (FloatAsset, BoolAsset, IntegerAsset, Vector3Asset, ...):
    /// Value is a double, long, bool or double[]</summary>
    public sealed class GSValue
    {
        public string Name, Iface;
        public object Value;

        public GSValue(string name, string iface, object value)
        {
            Name = name;
            Iface = iface;
            Value = value ?? (iface == "vec3" ? (object)new double[] { 0, 0, 0, 0 } : 0L);
        }
    }

    /// <summary>
    /// A pose laid out like the game's: per-track 'set' flags at data+0x10+track (dof.write sets them, dof.read_if_set
    /// and pose.chain test them), DOF values at data+offset (offsets from 0x100 here). DOF values by name ('Group.Dof' or
    /// 'Dof'). Every DOF a mapping asks for gets a track and a 16-byte slot (as if the rig had it): the given value, else
    /// identity for '.q' names, (1, 1, 1, 0) for '.s' and zeros otherwise. Tracks of given values start with the flag
    /// set; names in Unbound get no track (their handles are 0x80000000).
    /// </summary>
    public sealed class VmPose
    {
        public const int FLAGS = 0x10, VALUES = 0x100;
        public readonly Dictionary<string, float[]> Values;
        public readonly HashSet<string> Unbound;
        public byte[] Data = new byte[VALUES];
        public readonly Dictionary<string, int[]> Tracks = new Dictionary<string, int[]>();
        public readonly Dictionary<int, string> Names = new Dictionary<int, string>();

        public VmPose(Dictionary<string, float[]> values = null, IEnumerable<string> unbound = null)
        {
            Values = values != null ? new Dictionary<string, float[]>(values) : new Dictionary<string, float[]>();
            Unbound = new HashSet<string>(unbound ?? new string[0]);
        }

        public byte Flag(int track) { long p = FLAGS + (long)track; return p >= 0 && p < Data.Length ? Data[p] : (byte)0; }

        public void SetFlag(int track)
        {
            long p = FLAGS + (long)track;
            if (!(p >= 0 && p < Data.Length)) throw new VMError("DOF track " + track + " outside the pose");
            Data[p] = 0xFF;
        }

        /// <summary>(track, offset) of the DOF, or null for an unbound one</summary>
        public int[] Handle(string group, string dof)
        {
            string[] names = { group + "." + dof, dof };
            if (names.Any(n => Unbound.Contains(n))) return null;
            string key = names.FirstOrDefault(n => Values.ContainsKey(n)) ?? names[0];
            int[] t;
            if (!Tracks.TryGetValue(key, out t))
            {
                int tr = Tracks.Count;
                if (FLAGS + tr >= VALUES) throw new VMError("more than " + (VALUES - FLAGS) + " DOFs in one pose");
                int off = Data.Length;
                Array.Resize(ref Data, Data.Length + 16);
                float[] raw;
                if (Values.ContainsKey(key))
                {
                    raw = Values[key];
                    Data[FLAGS + tr] = 0xFF;
                }
                else raw = dof.EndsWith(".q") ? new[] { 0f, 0f, 0f, 1f } : dof.EndsWith(".s") ? new[] { 1f, 1f, 1f, 0f } : new[] { 0f, 0f, 0f, 0f };
                byte[] rb = VmMem.Floats(raw);
                Buffer.BlockCopy(rb, 0, Data, off, Math.Min(16, rb.Length));
                t = new[] { tr, off };
                Tracks[key] = t;
                Names[tr] = key;
            }
            return t;
        }

        public float[] Value(string name)
        {
            int[] t;
            if (!Tracks.TryGetValue(name, out t)) return null;
            float[] r = new float[4];
            for (int j = 0; j < 4; j++) r[j] = BE.BitsFloat(BE.U32LE(Data, t[1] + 4 * j));
            return r;
        }

        public F4 Vec(int off)
        {
            float[] r = new float[4];
            for (int j = 0; j < 4; j++) r[j] = off + 4 * j + 4 <= Data.Length && off >= 0 ? BE.BitsFloat(BE.U32LE(Data, off + 4 * j)) : 0f;
            return F4.Of(r);
        }
    }

    /// <summary>a write the kernel made: a game-state value or a DOF</summary>
    public struct VmWrite
    {
        public int Step;
        public string Kind, Target;
        public object Value;      // double, long, bool, double[] / float[]
    }

    /// <summary>
    /// Runs an ExpressionKernel: the game's expression VM and its natives (KernelNatives).
    /// State registers persist between runs, like the game's ExpressionState.
    /// </summary>
    public sealed class Machine
    {
        public const int INVALID_TRACK = -0x80000000;
        public readonly Kernel K;
        public readonly KernelBindings B;
        public readonly VmMem Mem;
        private readonly Dictionary<long, Ins> at = new Dictionary<long, Ins>();
        public readonly Dictionary<ulong, object> Objects = new Dictionary<ulong, object>();
        public readonly List<VmWrite> Writes = new List<VmWrite>();
        public int Steps;
        public readonly VmPose Pose, MasterPose;
        public uint DeltaTicks;
        public readonly long Table;
        public readonly List<GSValue> Gs = new List<GSValue>();
        private Dictionary<string, ulong> ph;

        public Machine(Kernel kernel, KernelBindings bindings = null, Dictionary<string, object> gs = null, VmPose pose = null, VmPose masterPose = null,
                       long deltaTicks = 1, long table = 0x7AB1E)
        {
            K = kernel;
            kernel.Layout();
            B = bindings ?? new KernelBindings();
            Mem = new VmMem(kernel.Consts.ToArray(), kernel.Regs.ToArray(), Kernel.Align((int)Math.Max(kernel.Temps, 1u), 16));
            foreach (Ins i in kernel.Code) at[i.Off] = i;
            Pose = pose ?? new VmPose();
            MasterPose = masterPose ?? Pose;
            DeltaTicks = (uint)(deltaTicks & 0xFFFFFFFF);
            Table = table;
            gs = gs ?? new Dictionary<string, object>();
            for (int j = 0; j < B.Gs.Count; j++)
            {
                Tuple<ulong, uint, uint> g = B.Gs[j];
                string name = "gs" + j;
                object val;
                if (!gs.TryGetValue(name, out val) && !gs.TryGetValue(BE.KeyHex(g.Item1), out val)) val = null;
                string iface;
                if (!Kernel.Ifaces.TryGetValue(g.Item3, out iface)) iface = "0x" + g.Item3.ToString("x8");
                Gs.Add(new GSValue(name, iface, val));
            }
            BindState();
        }

        /// <summary>handles for pointer registers</summary>
        public ulong Handle(object obj)
        {
            ulong h = 0x10000UL + 16UL * (ulong)Objects.Count;
            Objects[h] = obj;
            return h;
        }

        public object Obj(uint x)
        {
            ulong h = Mem.Q64(x);
            object o;
            if (!Objects.TryGetValue(h, out o)) throw new VMError("register " + KernelText.FmtOp(x) + " does not hold a bound object (0x" + h.ToString("x") + ")");
            return o;
        }

        private static byte[] Handle8(int track, uint off)
        {
            byte[] d = new byte[8];
            BE.W32LE(d, 0, (uint)track);
            BE.W32LE(d, 4, off);
            return d;
        }

        /// <summary>what the game does before the first run: GameStateValueAssets into their registers,
        /// DOF handles and defaults</summary>
        private void BindState()
        {
            VmMem m = Mem;
            for (int j = 0; j < Gs.Count; j++)
            {
                uint x = B.Gs[j].Item2;
                if (x != Kernel.NONE)
                {
                    byte[] d = new byte[8];
                    BE.W64LE(d, 0, Handle(Gs[j]));
                    m.Write(x, d);
                }
            }
            foreach (Tuple<string, string, uint, uint> d in B.Dofs)
            {
                // one layout for both poses (the game binds handles against the rig's layout)
                int[] t = Pose.Handle(d.Item1, d.Item2);
                if (!ReferenceEquals(MasterPose, Pose))
                {
                    int[] t2 = MasterPose.Handle(d.Item1, d.Item2);
                    bool same = (t == null && t2 == null) || (t != null && t2 != null && t[0] == t2[0] && t[1] == t2[1]);
                    if (!same) throw new VMError("pose and master pose must list their DOFs in the same order");
                }
                if (d.Item3 == Kernel.NONE) continue;
                if (t == null) m.Write(d.Item3, Handle8(INVALID_TRACK, Kernel.NONE));
                else
                {
                    m.Write(d.Item3, Handle8(t[0], (uint)t[1]));
                    if (d.Item4 != Kernel.NONE)
                    {
                        int seg = (int)(d.Item4 >> 24);
                        if (seg > 2) throw new VMError("segment " + seg + " is VM-internal; not simulated");
                        int n = Math.Min(16, m.Seg[seg].Length - (int)(d.Item4 & 0xFFFFFF));
                        int avail = Math.Max(0, Math.Min(n, Pose.Data.Length - t[1]));
                        byte[] v = new byte[avail];
                        Buffer.BlockCopy(Pose.Data, t[1], v, 0, avail);
                        m.Write(d.Item4, v);
                    }
                }
            }
            ph = new Dictionary<string, ulong> { { "pose", Handle(Pose) }, { "master_pose", Handle(MasterPose) }, { "table", Handle(Tuple.Create("table", Table)) } };
        }

        /// <summary>the host writes these before every run</summary>
        private void HostRegisters()
        {
            foreach (string name in new[] { "pose", "master_pose", "table" })
            {
                uint x = B.Regs[name];
                if (x != Kernel.NONE)
                {
                    byte[] d = new byte[8];
                    BE.W64LE(d, 0, ph[name]);
                    Mem.Write(x, d);
                }
            }
            uint dt = B.Regs["delta_ticks"];
            if (dt != Kernel.NONE)
            {
                byte[] d = new byte[4];
                BE.W32LE(d, 0, DeltaTicks);
                Mem.Write(dt, d);
            }
        }

        public void Log(string what, string target, object value)
        {
            Writes.Add(new VmWrite { Step = Steps, Kind = what, Target = target, Value = value });
        }

        /// <summary>one evaluation from offset 0 to the halt</summary>
        public void Run(int maxSteps = 200000)
        {
            HostRegisters();
            VmMem m = Mem;
            Dictionary<uint, Native> nat = Kernel.Natives;
            long pc = 0;
            List<long> stack = new List<long>();
            int n = 0;
            while (true)
            {
                Steps++;
                n++;
                if (n > maxSteps) throw new VMError("more than " + maxSteps + " instructions: endless loop?");
                Ins i;
                if (!at.TryGetValue(pc, out i)) throw new VMError("execution reached code+0x" + pc.ToString("x") + ", which is not an instruction");
                int op = i.Op;
                long nx = i.Next;
                if (op == Kernel.OP_HALT) return;
                if (Kernel.IsCall(op))
                {
                    Native e;
                    if (!nat.TryGetValue(i.Hash, out e)) throw new VMError("native " + i.Hash.ToString("x8") + " is not registered");
                    KernelNatives.Fn fn = KernelNatives.Get(e.Fn);
                    if (fn == null) throw new NotSimulated(e.Name + " (" + i.Hash.ToString("x8") + ") is not simulated");
                    fn(this, i.InOps, i.Outs, i.StateOps);
                }
                else if (Kernel.Moves.ContainsKey(op)) m.Write(i.Dst, m.Read(i.Src, Kernel.Moves[op]));
                else if (op == Kernel.OP_BF) { if (m.B(i.Cond) == 0) nx = i.Target; }
                else if (op == Kernel.OP_SWITCH)
                {
                    uint v = m.U32(i.Sel);
                    foreach (uint[] c in i.Cases) if (v == c[0]) { nx = c[1]; break; }
                }
                else if (op == Kernel.OP_JSR)
                {
                    if (stack.Count >= K.Callstack) throw new VMError("call stack overflow (header allows " + K.Callstack + ")");
                    stack.Add(nx);
                    nx = i.Target;
                }
                else if (op == Kernel.OP_RET)
                {
                    if (stack.Count == 0) throw new VMError("ret with an empty call stack");
                    nx = stack[stack.Count - 1];
                    stack.RemoveAt(stack.Count - 1);
                }
                else if (op == Kernel.OP_JMPI) nx = m.U32(i.Src);
                else if (op == Kernel.OP_MEMCPY) m.Write(i.Dst, m.Read(i.Src, i.Size));
                else if (op == Kernel.OP_SUB)
                {
                    EvUnlink(i.Node);
                    int? k = EvChannel(i.Chan);
                    if (k.HasValue)
                    {
                        evLists[k.Value].Insert(0, i.Node);
                        evNode[i.Node] = k.Value;
                    }
                }
                else if (op == Kernel.OP_UNSUB) EvUnlink(i.Node);
                else if (op == Kernel.OP_EMIT)
                {
                    // the game would queue one event per subscribed node, but it runs kernels without events,
                    // so with a subscriber the game crashes
                    int? k = EvChannel(i.Chan);
                    if (k.HasValue && evLists[k.Value].Count > 0)
                        throw new VMError("emit on channel 0x" + i.Chan.ToString("x") + " with " + evLists[k.Value].Count + " subscribed node(s): the game runs kernels " +
                                          "without events, so this crashes the game");
                }
                pc = nx;
            }
        }

        // event channels: 0x20-byte records after the state's 0x20-byte header ({subscriber list, type descriptor,
        // u64 channel id, flag}), then 0x28-byte nodes ({next, channel, owner state})
        private List<ulong> evIds;
        private Dictionary<int, List<uint>> evLists;
        private Dictionary<uint, int> evNode;

        private void EvSetup()
        {
            byte[] pre = K.StatePrefix.ToArray();
            evIds = new List<ulong>();
            for (long k = 0; k < K.Channels; k++)
            {
                long o = 0x20 + 0x20 * k + 0x10;
                ulong v = 0;
                for (int q = 0; q < 8; q++) if (o + q < pre.Length) v |= (ulong)pre[o + q] << (8 * q);
                evIds.Add(v);
            }
            evLists = new Dictionary<int, List<uint>>();
            for (int k = 0; k < K.Channels; k++) evLists[k] = new List<uint>();
            evNode = new Dictionary<uint, int>();
        }

        private int? EvChannel(ulong cid)
        {
            if (evIds == null) EvSetup();
            for (int k = 0; k < evIds.Count; k++) if (evIds[k] == cid) return k;
            return null;
        }

        private void EvUnlink(uint node)
        {
            if (evIds == null) EvSetup();
            if (!(node < K.Nodes)) throw new VMError("event node " + node + " outside the " + K.Nodes + " node records");
            int k;
            if (evNode.TryGetValue(node, out k))
            {
                evNode.Remove(node);
                evLists[k].Remove(node);
            }
        }
    }
}
