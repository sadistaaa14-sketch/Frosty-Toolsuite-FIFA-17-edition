using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using FrostyAnimationEditor.Core.Anim;

namespace FrostyAnimationEditor.Core
{
    public struct LimitIssue
    {
        public ulong Key;
        public string Class, Path, Message;
        public override string ToString() { return Class + " " + BE.KeyHex(Key) + " " + Path + ": " + Message; }
    }

    /// <summary>
    /// Engine limits: edits FIFA17.exe would refuse, crash on, or load into corrupted memory.  Count fields and
    /// the other redundant data are re-derived on save; the rules here cover what cannot be derived - fixed-size
    /// arrays in the loaders, lists that must match other lists, required embedded objects.  Every rule names the
    /// loader it comes from; the "not loadable" cases were reproduced by loading edited copies in the emulated loader.
    /// </summary>
    public static class Limits
    {
        public delegate List<Tuple<string, string>> Rule(GdObj part, AntDb db);

        private static readonly Dictionary<string, List<Rule>> rules = new Dictionary<string, List<Rule>>();

        public static void Register(Rule fn, params string[] classes)
        {
            foreach (string c in classes)
            {
                List<Rule> l;
                if (!rules.TryGetValue(c, out l)) { l = new List<Rule>(); rules[c] = l; }
                l.Add(fn);
            }
        }

        public static bool HasRules(string cls) { return rules.ContainsKey(cls); }

        private static long Len(GdStruct v, string name) { return v == null ? 0 : AllocModel.LenOf(v[name]); }

        private static Tuple<string, string> I(string path, string msg) { return Tuple.Create(path, msg); }

        static Limits()
        {
            Register((o, db) =>
            {
                List<Tuple<string, string>> r = new List<Tuple<string, string>>();
                GdStruct w = o["WarpAsset"] as GdStruct;
                if (w == null) { r.Add(I("WarpAsset", "must be set: FUN_146069ff0 reads it unconditionally (the load fails)")); return r; }
                long n = Len(w, "Pose");
                if (n > 200) r.Add(I("WarpAsset.Pose", n + " poses; FUN_146069ff0 copies them into a 200-entry stack buffer (more overruns the stack: the game stops with a security check failure)"));
                if (Len(w, "Segments") > 3) r.Add(I("WarpAsset.Segments", Len(w, "Segments") + " segments; the runtime object holds 3 (0x38 bytes each from +0xA0, the 4th is overwritten at +0x17F, later ones overwrite other fields)"));
                foreach (string f in new[] { "TouchRegions", "TouchTimes" })
                    if (Len(w, f) > 3) r.Add(I("WarpAsset." + f, Len(w, f) + " entries; FUN_146069ff0 keeps 3 on the stack"));
                if (w.GetBool("StoreLeanPose") && Len(w, "Lean") < n)
                    r.Add(I("WarpAsset.Lean", "StoreLeanPose is set: one Lean entry per Pose is read (" + Len(w, "Lean") + " < " + n + ")"));
                return r;
            }, "WarpControllerAsset");

            Register((o, db) =>
            {
                // FUN_145fdd820: n = len(ControllerAssets); entry i reads element i of every parallel list
                List<Tuple<string, string>> r = new List<Tuple<string, string>>();
                long n = Len(o, "ControllerAssets");
                foreach (string f in new[] { "SignalAssets", "BlendMaskControllers", "UpdateModes", "UpdateRates", "BlendModes", "BranchInModes" })
                    if (Len(o, f) < n) r.Add(I(f, "has " + Len(o, f) + " entries, ControllerAssets " + n + ": FUN_145fdd820 reads one per controller"));
                long m = o.GetInt("MasterEntryIndex");
                if (n != 0 && m >= n) r.Add(I("MasterEntryIndex", m + " is not an entry index (0.." + (n - 1) + ")"));
                return r;
            }, "LayersControllerAsset");

            Register((o, db) =>
            {
                List<Tuple<string, string>> r = new List<Tuple<string, string>>();
                long cells = 1;
                foreach (string f in new[] { "InAngles", "OutAngles", "MoveAngles", "Speeds" })
                {
                    long k = Len(o, f);
                    if (k > 16) r.Add(I(f, k + " values; FUN_146c7ce90 uses the first 16"));
                    cells *= Math.Min(16, k);
                }
                long ql = AllocModel.LenOf(o["Quads"]);
                long nq = ql >> 5;
                if (ql % 32 != 0) r.Add(I("Quads", ql + " bytes is not a whole number of 32-byte quads"));
                if (nq != 0)
                {
                    IList qm = o["QuadMap"] as IList ?? new object[0];
                    if (qm.Count < cells) r.Add(I("QuadMap", qm.Count + " cells, the four axes make " + cells + " (FUN_146ca69e0 copies that many)"));
                    List<int> bad = new List<int>();
                    for (int i = 0; i < qm.Count && i < cells; i++)
                    {
                        long x = Num.ToLong(qm[i]);
                        if ((x & 0xFFFFFFFF) != 0xFFFFFFFF && (x & 0xFFFFFF) >= nq) bad.Add(i);
                    }
                    if (bad.Count > 0) r.Add(I("QuadMap[" + bad[0] + "]", bad.Count + " cells name a quad past the " + nq + " quads"));
                    // the lookup (Contacts, FUN_146cd8ff0) assumes ascending axes
                    foreach (string msg in Contacts.Issues(o))
                        if (msg.Contains("ascending") || msg.Contains("empty"))
                            r.Add(I(msg.Split(new[] { ' ' }, 2)[0], msg));
                }
                return r;
            }, "ContinuousContactAsset");

            Register((o, db) =>
            {
                long words = (o.GetInt("RowCount") * o.GetInt("ColumnCount") + 31) >> 5;
                if (Len(o, "Array") < words)
                    return new List<Tuple<string, string>> { I("Array", Len(o, "Array") + " words, RowCount x ColumnCount needs " + words + " (FUN_145f9f3e0 copies them)") };
                return new List<Tuple<string, string>>();
            }, "CullingTableAsset");

            Register((o, db) =>
            {
                List<Tuple<string, string>> r = new List<Tuple<string, string>>();
                IList mj = o["MappedJoints"] as IList ?? new object[0];
                if (mj.Count > 26) r.Add(I("MappedJoints", mj.Count + " entries; FUN_145f33f40 writes them into a 26-entry vector (heap overrun)"));
                long nt = o.GetInt("NumTotalJoints");
                for (int i = 0; i < mj.Count; i++)
                {
                    long x = Num.ToLong(mj[i]);
                    if ((x & 0xFFFFFFFF) != 0xFFFFFFFF && x > nt) { r.Add(I("MappedJoints[" + i + "]", "joint index past NumTotalJoints (" + nt + ")")); break; }
                }
                return r;
            }, "ParticleIKFeatureAsset");

            Register((o, db) =>
            {
                JObject di = AllocModel.DataInterface(o);
                if (di == null) return new List<Tuple<string, string>>();
                IList ids = o["MemberUniqueIds"] as IList ?? new object[0];
                Dictionary<string, int> first = new Dictionary<string, int>();
                for (int i = 0; i < ids.Count; i++)
                {
                    string x = ((uint)Num.ToLong(ids[i])).ToString("x8");
                    if (!first.ContainsKey(x)) first[x] = i;
                }
                int need = 0;
                foreach (object m in di.Arr("members")) { int i; if (first.TryGetValue((string)m, out i)) need = Math.Max(need, i + 1); }
                if (Len(o, "MemberAssetReferences") < need)
                    return new List<Tuple<string, string>> { I("MemberAssetReferences", Len(o, "MemberAssetReferences") + " references, MemberUniqueIds pairs members with index " + (need - 1)) };
                return new List<Tuple<string, string>>();
            }, "DataInterfaceAsset");

            Register((o, db) =>
            {
                List<Tuple<string, string>> r = new List<Tuple<string, string>>();
                long n = Len(o, "GameStateValueAssets");
                foreach (string f in new[] { "GameStateAssetOffsets", "GameStateInterfaceTypes" })
                    if (Len(o, f) < n) r.Add(I(f, Len(o, f) + " entries for " + n + " GameStateValueAssets (FUN_146b72490 reads one each)"));
                if (Len(o, "DofMapping") % 4 != 0)
                    r.Add(I("DofMapping", Len(o, "DofMapping") + " entries; FUN_146b75e30 reads groups of 4 (group name, DOF name, handle register, default register)"));
                byte[] blob = Kernel.BlobOf(o, "ExpressionKernel");
                if (blob.Length > 0)
                {
                    Kernel k;
                    try { k = Kernel.Parse(blob); }
                    catch (KernelException e)
                    {
                        r.Add(I("ExpressionKernel", "the kernel does not parse the way the game's linker walks it: " + e.Message));
                        return r;
                    }
                    foreach (Tuple<string, string> c in Kernel.Check(k, KernelBindings.FromObj(o)))
                        if (c.Item1 == "error") r.Add(I("ExpressionKernel", c.Item2));
                }
                return r;
            }, "ExpressionAsset");

            Register((o, db) => o["MemberValues"] == null
                ? new List<Tuple<string, string>> { I("MemberValues", "must be set: FUN_145ca3e70 lays the values out from its type") }
                : new List<Tuple<string, string>>(), "ConstCollectionAsset");

            Register((o, db) => Len(o, "RunCurveList") < 31
                ? new List<Tuple<string, string>> { I("RunCurveList", Len(o, "RunCurveList") + " curves; FUN_146068aa0 reads 31 (missing ones are read from past the list and the load fails)") }
                : new List<Tuple<string, string>>(), "RunCurveAsset");

            Register((o, db) => o["MemberDefaults"] == null
                ? new List<Tuple<string, string>> { I("MemberDefaults", "must be set: FUN_145d41750 lays the values out from its type") }
                : new List<Tuple<string, string>>(), "StructAsset");

            Register((o, db) => o["ContextDB"] == null
                ? new List<Tuple<string, string>> { I("ContextDB", "must be set: the PriorityDatabase builder (FUN_145f87cc0) reads it") }
                : new List<Tuple<string, string>>(), "PriorityDatabaseAsset");

            Register((o, db) =>
            {
                // FUN_145f50210 -> FUN_145f701f0 reads TrajX/TrajY/TrajZ/TrajAngles for every sample of the tick range
                List<Tuple<string, string>> r = new List<Tuple<string, string>>();
                GdStruct ts = o["TrajectoryState"] as GdStruct;
                if (ts == null) return r;
                long n = AllocModel.TrajSamples(ts);
                foreach (string f in new[] { "TrajX", "TrajY", "TrajZ", "TrajAngles" })
                    if (Len(ts, f) < n) r.Add(I("TrajectoryState." + f, Len(ts, f) + " values for " + n + " samples (TrajStartTick..TrajEndTick / RecordDeltaT)"));
                return r;
            }, "ActorControllerAsset");

            Register((o, db) => Len(o, "CellInfo") == 0
                ? new List<Tuple<string, string>> { I("CellInfo", "must not be empty (the load fails)") }
                : new List<Tuple<string, string>>(), "SceneOpMatrixAsset");

            Register((o, db) =>
            {
                // FUN_145d1e090 picks the class, FUN_145d1d0c0 loads it (Curves, FORMAT.md section 7)
                long fl = o.GetInt("SplineStorageFlags");
                if (fl != 0x10 && fl != 0x40 && (fl & 0x20) == 0)
                    return new List<Tuple<string, string>> { I("SplineStorageFlags", "0x" + fl.ToString("X2") + ": FUN_145d1e090 builds nothing for it (only 0x10 values, 0x40 quantized bytes, or 0x20|type parametric)") };
                return Curves.TagIssues(o).Select(m => I(m.Contains("Spline ") || m.Contains("Bezier") ? "Spline" : (m.Contains("SplineRaw") ? "SplineRaw" : "Mode"), m)).ToList();
            }, "FloatGameStateTag");

            Register((o, db) => Curves.WeightIssues(o).Select(m => I(m.Contains("DofSetCurves") ? "DofSetCurves" : "DefaultCurveBytes", m)).ToList(),
                     "DofWeightCurvesAsset");

            Register((o, db) =>
            {
                // the loaders copy the codec arrays as they are; a clip the decoder cannot read is corrupt
                if (db == null) return new List<Tuple<string, string>>();
                try { new AnimClip(db, o).Sample(); }
                catch (Exception e) { return new List<Tuple<string, string>> { I("", "the animation data does not decode: " + e.Message) }; }
                return new List<Tuple<string, string>>();
            }, "VbrAnimationAsset", "DctAnimationAsset", "RawAnimationAsset");
        }

        public static List<Tuple<string, string>> CheckObject(GdObj o, AntDb db = null)
        {
            List<Tuple<string, string>> r = new List<Tuple<string, string>>();
            foreach (GdObj p in o.Chain())
            {
                List<Rule> l;
                if (rules.TryGetValue(p.Type.Name, out l))
                    foreach (Rule fn in l) r.AddRange(fn(p, db));
            }
            return r;
        }

        /// <summary>decodeClips = false skips decoding animation data (slow over a whole file)</summary>
        public static List<LimitIssue> Check(AntDb db, List<int> ids = null, bool decodeClips = true)
        {
            if (ids == null) ids = db.Cache.Keys.Concat(db.NewObjs.Keys).Where(i => !db.Removed.Contains(i)).ToList();
            List<LimitIssue> r = new List<LimitIssue>();
            foreach (int i in ids)
            {
                string tn = db.TypeName(i);
                if (!rules.ContainsKey(tn) && !db.ClassChain(tn).Any(c => rules.ContainsKey(c))) continue;
                GdObj o = db.Obj(i);
                foreach (Tuple<string, string> x in CheckObject(o, decodeClips ? db : null))
                    r.Add(new LimitIssue { Key = db.Keys[i], Class = tn, Path = x.Item1, Message = x.Item2 });
            }
            return r;
        }
    }
}
