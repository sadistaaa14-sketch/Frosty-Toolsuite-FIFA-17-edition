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
    /// Engine limits: edits the game would refuse, crash on, or load into corrupted memory.  Count fields and
    /// the other redundant data are re-derived on save; the rules here cover what cannot be derived - fixed-size
    /// arrays in the game, lists that must match other lists, required embedded objects.
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
                // the game holds 8 x values and then 8 y values; a 9th point's x would land on the y values.
                // All 8,598 in the game's file have 8 or fewer.
                List<Tuple<string, string>> r = new List<Tuple<string, string>>();
                long n = Len(o, "PointValues");
                if (n > 8) r.Add(I("PointValues", n + " points; the game holds 8, so further points overwrite the y values"));
                return r;
            }, "PiecewiseLinearAsset");

            Register((o, db) =>
            {
                // the game unpacks the StepAssetImpl into a fixed-size block: for each of these arrays it clears
                // a fixed number of bytes and then writes one entry's worth per entry with no bound, so a longer list
                // runs into whatever follows. Every one of the 3,472 shipped steps is at the limit.
                List<Tuple<string, string>> r = new List<Tuple<string, string>>();
                GdStruct im = o["AssetImpl"] as GdStruct;
                if (im == null) return r;
                long n = Len(im, "InterruptableT");
                if (n > 2) r.Add(I("AssetImpl.InterruptableT", n + " ranges; the game holds 2, so further ranges overwrite PlantInfo"));
                n = Len(im, "PlantInfo");
                if (n > 4) r.Add(I("AssetImpl.PlantInfo", n + " plants; the game holds 4, so further plants overwrite DeltaSpeed and what follows it"));
                n = Len(im, "StartLeanAngle");
                if (n > 2) r.Add(I("AssetImpl.StartLeanAngle", n + " entries; the game holds 2, so further entries overwrite EndLeanAngle"));
                n = Len(im, "EndLeanAngle");
                if (n > 2) r.Add(I("AssetImpl.EndLeanAngle", n + " entries; the game holds 2, so further entries corrupt memory"));
                return r;
            }, "StepAssetAnt");

            Register((o, db) =>
            {
                List<Tuple<string, string>> r = new List<Tuple<string, string>>();
                GdStruct w = o["WarpAsset"] as GdStruct;
                if (w == null) { r.Add(I("WarpAsset", "must be set: the game always reads it (the load fails)")); return r; }
                long n = Len(w, "Pose");
                if (n > 200) r.Add(I("WarpAsset.Pose", n + " poses; the game holds 200 (more crashes the game)"));
                if (Len(w, "Segments") > 3) r.Add(I("WarpAsset.Segments", Len(w, "Segments") + " segments; the game holds 3 (further segments corrupt the warp's other settings)"));
                foreach (string f in new[] { "TouchRegions", "TouchTimes" })
                    if (Len(w, f) > 3) r.Add(I("WarpAsset." + f, Len(w, f) + " entries; the game holds 3"));
                if (w.GetBool("StoreLeanPose") && Len(w, "Lean") < n)
                    r.Add(I("WarpAsset.Lean", "StoreLeanPose is set: one Lean entry per Pose is read (" + Len(w, "Lean") + " < " + n + ")"));
                return r;
            }, "WarpControllerAsset");

            Register((o, db) =>
            {
                // n = len(ControllerAssets); entry i reads element i of every parallel list
                List<Tuple<string, string>> r = new List<Tuple<string, string>>();
                long n = Len(o, "ControllerAssets");
                foreach (string f in new[] { "SignalAssets", "BlendMaskControllers", "UpdateModes", "UpdateRates", "BlendModes", "BranchInModes" })
                    if (Len(o, f) < n) r.Add(I(f, "has " + Len(o, f) + " entries, ControllerAssets " + n + ": the game reads one per controller"));
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
                    if (k > 16) r.Add(I(f, k + " values; the game uses the first 16"));
                    cells *= Math.Min(16, k);
                }
                long ql = AllocModel.LenOf(o["Quads"]);
                long nq = ql >> 5;
                if (ql % 32 != 0) r.Add(I("Quads", ql + " bytes is not a whole number of 32-byte quads"));
                if (nq != 0)
                {
                    IList qm = o["QuadMap"] as IList ?? new object[0];
                    if (qm.Count < cells) r.Add(I("QuadMap", qm.Count + " cells, the four axes make " + cells + " (the game reads that many)"));
                    List<int> bad = new List<int>();
                    for (int i = 0; i < qm.Count && i < cells; i++)
                    {
                        long x = Num.ToLong(qm[i]);
                        if ((x & 0xFFFFFFFF) != 0xFFFFFFFF && (x & 0xFFFFFF) >= nq) bad.Add(i);
                    }
                    if (bad.Count > 0) r.Add(I("QuadMap[" + bad[0] + "]", bad.Count + " cells name a quad past the " + nq + " quads"));
                    // the lookup (Contacts) assumes ascending axes
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
                    return new List<Tuple<string, string>> { I("Array", Len(o, "Array") + " words, RowCount x ColumnCount needs " + words + "") };
                return new List<Tuple<string, string>>();
            }, "CullingTableAsset");

            Register((o, db) =>
            {
                List<Tuple<string, string>> r = new List<Tuple<string, string>>();
                IList mj = o["MappedJoints"] as IList ?? new object[0];
                if (mj.Count > 26) r.Add(I("MappedJoints", mj.Count + " entries; the game holds 26 (more corrupts memory)"));
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
                    if (Len(o, f) < n) r.Add(I(f, Len(o, f) + " entries for " + n + " GameStateValueAssets (the game reads one each)"));
                if (Len(o, "DofMapping") % 4 != 0)
                    r.Add(I("DofMapping", Len(o, "DofMapping") + " entries; the game reads groups of 4 (group name, DOF name, handle slot, default slot)"));
                byte[] blob = Kernel.BlobOf(o, "ExpressionKernel");
                if (blob.Length > 0)
                {
                    Kernel k;
                    try { k = Kernel.Parse(blob); }
                    catch (KernelException e)
                    {
                        r.Add(I("ExpressionKernel", "the game cannot read this kernel: " + e.Message));
                        return r;
                    }
                    foreach (Tuple<string, string> c in Kernel.Check(k, KernelBindings.FromObj(o)))
                        if (c.Item1 == "error") r.Add(I("ExpressionKernel", c.Item2));
                }
                return r;
            }, "ExpressionAsset");

            Register((o, db) => o["MemberValues"] == null
                ? new List<Tuple<string, string>> { I("MemberValues", "must be set: the game lays the values out from its type") }
                : new List<Tuple<string, string>>(), "ConstCollectionAsset");

            Register((o, db) => Len(o, "RunCurveList") < 31
                ? new List<Tuple<string, string>> { I("RunCurveList", Len(o, "RunCurveList") + " curves; the game needs 31 (with fewer the load fails)") }
                : new List<Tuple<string, string>>(), "RunCurveAsset");

            Register((o, db) => o["MemberDefaults"] == null
                ? new List<Tuple<string, string>> { I("MemberDefaults", "must be set: the game lays the values out from its type") }
                : new List<Tuple<string, string>>(), "StructAsset");

            Register((o, db) => o["ContextDB"] == null
                ? new List<Tuple<string, string>> { I("ContextDB", "must be set: the game reads it to build the database") }
                : new List<Tuple<string, string>>(), "PriorityDatabaseAsset");

            Register((o, db) =>
            {
                // the game reads TrajX/TrajY/TrajZ/TrajAngles for every sample of the tick range
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
                // the curve kind comes from SplineStorageFlags (Curves)
                long fl = o.GetInt("SplineStorageFlags");
                if (fl != 0x10 && fl != 0x40 && (fl & 0x20) == 0)
                    return new List<Tuple<string, string>> { I("SplineStorageFlags", "0x" + fl.ToString("X2") + ": the game builds no curve for it (only 0x10 values, 0x40 compressed values, or 0x20 plus a type for a formula curve)") };
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
