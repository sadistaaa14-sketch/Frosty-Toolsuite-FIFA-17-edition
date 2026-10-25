using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;

namespace FrostyAnimationEditor.Core
{
    public struct CountIssue
    {
        public string Path;
        public object Have;
        public object Want;
        public CountIssue(string path, object have, object want) { Path = path; Have = have; Want = want; }
        public override string ToString() { return Path + ": " + (Have ?? "null") + " (should be " + Want + ")"; }
    }

    /// <summary>
    /// Fields the file stores redundantly, and how the editor keeps them consistent.
    /// Count fields: the game sizes its lists from the count field and then reads that many elements of the
    /// list (ActorControllerAsset NumTracks, SequenceAnimTrack counts ...).  Every rule holds for every
    /// object in the game's file.
    ///   FixCounts(obj) / CheckCounts(obj), FixLinks(db) (two-way links, enum flags), FixAll(db) (Save runs it)
    /// </summary>
    public static class Consistency
    {
        public sealed class CountRule
        {
            public string Count, List;
            public string[] Others;
            public CountRule(string c, string l, params string[] o) { Count = c; List = l; Others = o; }
        }

        public static readonly Dictionary<string, CountRule[]> CountRules = new Dictionary<string, CountRule[]>
        {
            { "ActorControllerAsset", new[] { new CountRule("NumTracks", "Tracks") } },
            { "SequenceAnimTrack", new[] { new CountRule("NumAnims", "Anims"), new CountRule("NumWarpers", "WarpersParam") } },
            { "BlendArrayAsset", new[] { new CountRule("NumItems", "Items") } },
            { "CharacterInteractionControllerAsset", new[] { new CountRule("NumInteractionSlots", "SlaveControllerAsset", "SlaveControllerInitParams", "QueryTasks") } },
            { "CollisionDistillAsset", new[] { new CountRule("FunctionCount", "FunctionAssets") } },
            { "StepDistillAsset", new[] { new CountRule("FunctionCount", "FunctionAssets") } },
            { "DctAnimationAsset", new[] { new CountRule("NumKeys", "KeyTimes") } },
            { "RawAnimationAsset", new[] { new CountRule("NumKeys", "KeyTimes") } },
            { "VbrAnimationAsset", new[] { new CountRule("ConstPaletteSize", "ConstantPalette") } },
            { "WarpAsset", new[] { new CountRule("NumSegments", "Segments"), new CountRule("LenPosition", "Pose"),
                                   new CountRule("NumTouchRegionSpeeds", "TouchRegions", "TouchTimes"),
                                   new CountRule("NumTouchRegionTouchTimes", "TouchTimes", "TouchRegions") } },
        };

        /// <summary>(owner class, owner list field, member class, member back field) - 1:1 on the shipped file</summary>
        public static readonly string[][] InverseLinks =
        {
            new[] { "EnumerationAsset", "EnumerationValues", "EnumerationValueAsset", "EnumerationAsset" },
            new[] { "StepClipAsset", "Steps", "StepAssetAnt", "AssetImpl.StepClip" },
            new[] { "BaseJointMapTemplateAsset", "IDList", "JointMapTemplateIDAsset", "TemplateAsset" },
            new[] { "GestureConfigAsset", "GestureMappings", "GestureToSignalMappingAsset", "Config" },
        };

        public static readonly string[] EnumOwners = { "EnumerationAsset", "QueryGroupEnumAsset" };

        private static bool IsA(GdObj o, string cls)
        {
            foreach (GdObj x in o.Chain()) if (x.Type.Name == cls) return true;
            return false;
        }

        /// <summary>loaded or added objects that are cls or derive from it</summary>
        private static List<GdObj> Owners(AntDb db, string cls)
        {
            List<GdObj> r = new List<GdObj>();
            foreach (int i in db.Cache.Keys.ToList().Concat(db.NewObjs.Keys.ToList()))
            {
                if (db.Removed.Contains(i)) continue;
                GdObj o = db.Obj(i);
                if (IsA(o, cls)) r.Add(o);
            }
            return r;
        }

        private static long LenOf(object v) { return AllocModel.LenOf(v); }

        private static bool LongEq(object v, long n)
        {
            if (v == null) return false;
            if (v is bool) return false;
            return Num.ToLong(v) == n;
        }

        /// <summary>point every member of an edited owner's list back at that owner; recompute
        /// EnumerationAsset.ZeroBasedSequential.  Returns the number of fields changed.</summary>
        public static int FixLinks(AntDb db, Action<string> log = null)
        {
            int n = 0;
            foreach (string[] link in InverseLinks)
            {
                string ownerCls = link[0], lst = link[1], memberCls = link[2], back = link[3];
                foreach (GdObj o in Owners(db, ownerCls))
                {
                    IList keys = o.GetInherited(lst) as IList;
                    if (keys == null) continue;
                    foreach (object ko in keys)
                    {
                        GdObj m = db.Get(Num.ToKey(ko));
                        if (m == null || !IsA(m, memberCls)) continue;
                        object cur = JsonIO.TryGetPath(m, back);
                        if (cur == null || Num.ToKey(cur) != o.Key)
                        {
                            JsonIO.Locate(m, back).Set(o.Key);
                            n++;
                            if (log != null) log(memberCls + " '" + m.Name + "': " + back + " now points to '" + o.Name + "'");
                        }
                    }
                }
            }
            // ZeroBasedSequential = every value's Value equals its index (132/132 on the shipped file)
            HashSet<ulong> touched = new HashSet<ulong>();
            foreach (GdObj o in Owners(db, "EnumerationValueAsset"))
            {
                ulong k = Num.ToKey(o["EnumerationAsset"]);
                if (k != 0) touched.Add(k);
            }
            Dictionary<ulong, GdObj> enums = new Dictionary<ulong, GdObj>();
            foreach (string c in EnumOwners) foreach (GdObj o in Owners(db, c)) enums[o.Key] = o;
            foreach (ulong k in touched)
                if (!enums.ContainsKey(k) && db.Get(k) != null) enums[k] = db.Get(k);
            foreach (GdObj o in enums.Values)
            {
                IList ev = o.GetInherited("EnumerationValues") as IList ?? new object[0];
                bool zb = true;
                for (int j = 0; j < ev.Count; j++)
                {
                    GdObj v = db.Get(Num.ToKey(ev[j]));
                    if (v == null || !LongEq(v["Value"], j)) { zb = false; break; }
                }
                object cur = o.GetInherited("ZeroBasedSequential");
                if (cur != null && Num.ToBool(cur) != zb)
                {
                    o.SetInherited("ZeroBasedSequential", zb);
                    n++;
                }
            }
            return n;
        }

        /// <summary>[(member key, problem)] for two-way links that disagree</summary>
        public static List<Tuple<ulong, string>> CheckLinks(AntDb db)
        {
            List<Tuple<ulong, string>> bad = new List<Tuple<ulong, string>>();
            foreach (string[] link in InverseLinks)
            {
                string ownerCls = link[0], lst = link[1], memberCls = link[2], back = link[3];
                Dictionary<ulong, ulong> listed = new Dictionary<ulong, ulong>();
                foreach (string cls in db.ClassesDeriving(ownerCls))
                    foreach (GdObj o in db.Objects(cls))
                    {
                        IList keys = o.GetInherited(lst) as IList;
                        if (keys == null) continue;
                        foreach (object ko in keys)
                        {
                            ulong k = Num.ToKey(ko);
                            ulong prev;
                            if (listed.TryGetValue(k, out prev) && prev != o.Key) bad.Add(Tuple.Create(k, "listed by two " + ownerCls + " objects"));
                            listed[k] = o.Key;
                        }
                    }
                foreach (string cls in db.ClassesDeriving(memberCls))
                    foreach (GdObj m in db.Objects(cls))
                    {
                        object b = JsonIO.TryGetPath(m, back);
                        ulong bk = Num.ToKey(b);
                        ulong l;
                        if (bk != 0 && (!listed.TryGetValue(m.Key, out l) || l != bk))
                            bad.Add(Tuple.Create(m.Key, back + " points to " + BE.KeyHex(bk) + " which does not list it"));
                    }
            }
            // a step clip's steps tile it end to end: AnimStartT(n+1) = AnimStartT(n) + EndT(n).
            // Exact in all 2,788 consecutive pairs of the shipped file.
            foreach (GdObj sc in db.Objects("StepClipAsset"))
            {
                IList sl = sc["Steps"] as IList;
                if (sl == null) continue;
                long want = -1;
                foreach (object x in sl)
                {
                    GdObj so = db.Get(Num.ToKey(x));
                    GdStruct im = so != null ? so["AssetImpl"] as GdStruct : null;
                    if (im == null) continue;
                    GdStruct sa = im["AnimStartT"] as GdStruct, se = im["EndT"] as GdStruct;
                    if (sa == null || se == null) continue;
                    long at = sa.GetInt("T");
                    if (want >= 0 && at != want)
                        bad.Add(Tuple.Create(so.Key, "AnimStartT " + at + " does not continue from the previous step's end " + want));
                    want = at + se.GetInt("T");
                }
            }
            return bad;
        }

        // ContextDatabaseAsset (and StepClipAsset / ContinuousContactAsset, which derive from it):
        //   NumEntryAssetsTotal       = sum of len(entry.Assets)
        //   NumDeduplicatedListAssets = number of entries with more than one asset
        //   NumAuxiliaryEntryAsset    = kept, clamped to the entry count
        //   every entry has one CellAsset per FieldDescriptionAssets element
        public static GdObj CdbPart(GdStruct o)
        {
            GdObj ob = o as GdObj;
            if (ob == null) return null;
            foreach (GdObj x in ob.Chain()) if (x.Type.Name == "ContextDatabaseAsset") return x;
            return null;
        }

        public static List<Tuple<string, GdObj>> CdbParts(GdStruct o)
        {
            List<Tuple<string, GdObj>> r = new List<Tuple<string, GdObj>>();
            GdObj ob = o as GdObj;
            if (ob == null) return r;
            GdObj p = CdbPart(ob);
            if (p != null) r.Add(Tuple.Create(ob.Type.Name, p));
            foreach (GdObj part in ob.Chain())
                for (int i = 0; i < part.Type.Fields.Count; i++)
                {
                    Field f = part.Type.Fields[i];
                    GdObj x = part.Values[i] as GdObj;
                    if (f.Name != "__base" && x != null && x.Type.Name == "ContextDatabaseAsset")
                        r.Add(Tuple.Create(ob.Type.Name + "." + f.Name, x));
                }
            return r;
        }

        private static readonly string[] CdbCountFields = { "NumEntryAssetsTotal", "NumDeduplicatedListAssets", "NumAuxiliaryEntryAsset" };

        private static Dictionary<string, long> CdbWant(GdStruct part)
        {
            IList ea = part["EntryAssets"] as IList ?? new object[0];
            long total = 0, dedup = 0;
            foreach (object e in ea)
            {
                GdStruct es = e as GdStruct;
                if (es == null) continue;
                long n = LenOf(es["Assets"]);
                total += n;
                if (n > 1) dedup++;
            }
            long aux = part["NumAuxiliaryEntryAsset"] == null ? 0 : Num.ToLong(part["NumAuxiliaryEntryAsset"]);
            Dictionary<string, long> want = new Dictionary<string, long>();
            want["NumEntryAssetsTotal"] = total;
            want["NumDeduplicatedListAssets"] = dedup;
            want["NumAuxiliaryEntryAsset"] = Math.Max(0, Math.Min(aux, ea.Count));
            return want;
        }

        public static int FixCdb(GdStruct part)
        {
            int n = 0;
            foreach (KeyValuePair<string, long> kv in CdbWant(part))
            {
                if (!part.Has(kv.Key)) continue;
                if (!LongEq(part[kv.Key], kv.Value))
                {
                    part[kv.Key] = kv.Value;
                    n++;
                }
            }
            return n;
        }

        public static List<CountIssue> CheckCdb(GdStruct part, string path)
        {
            List<CountIssue> bad = new List<CountIssue>();
            IList ea = part["EntryAssets"] as IList ?? new object[0];
            long nf = LenOf(part["FieldDescriptionAssets"]);
            for (int j = 0; j < ea.Count; j++)
            {
                GdStruct e = ea[j] as GdStruct;
                if (e != null && LenOf(e["Cells"]) != nf)
                    bad.Add(new CountIssue(path + ".EntryAssets[" + j + "].Cells (length)", LenOf(e["Cells"]), nf));
            }
            return bad;
        }

        private static void Walk(GdStruct st, string path, Action<GdStruct, string> fn)
        {
            fn(st, path);
            for (int i = 0; i < st.Type.Fields.Count; i++)
            {
                Field f = st.Type.Fields[i];
                object v = st.Values[i];
                if (v is GdStruct) Walk((GdStruct)v, path + "." + f.Name, fn);
                else
                {
                    IList l = v as IList;
                    if (l != null && !(v is byte[]) && !(v is float[]) && l.Count > 0 && l[0] is GdStruct)
                        for (int j = 0; j < l.Count; j++)
                        {
                            GdStruct e = l[j] as GdStruct;
                            if (e != null) Walk(e, path + "." + f.Name + "[" + j + "]", fn);
                        }
                }
            }
        }

        public static List<CountIssue> CheckCounts(GdStruct o)
        {
            List<CountIssue> bad = new List<CountIssue>();
            foreach (Tuple<string, GdObj> cp in CdbParts(o))
            {
                bad.AddRange(CheckCdb(cp.Item2, cp.Item1));
                foreach (KeyValuePair<string, long> kv in CdbWant(cp.Item2))
                {
                    if (!cp.Item2.Has(kv.Key)) continue;
                    object have = cp.Item2[kv.Key];
                    if (!LongEq(have, kv.Value)) bad.Add(new CountIssue(cp.Item1 + "." + kv.Key, have, kv.Value));
                }
            }
            Walk(o, o.Type.Name, (st, path) =>
            {
                if (st.Type.Name == "OverrideGameStatesCollectionAsset" && st.Type.Has("OverrideTargets"))
                {
                    // the game walks OverrideTargets once and takes one per value, in this order
                    long want = LenOf(st["OverrideAssets"]) + LenOf(st["OverrideVec3s"]) + LenOf(st["OverrideFloats"])
                                + LenOf(st["OverrideInts"]) + LenOf(st["OverrideBools"]);
                    long have = LenOf(st["OverrideTargets"]);
                    if (have != want) bad.Add(new CountIssue(path + ".OverrideTargets (length)", have, want));
                }
                if (st.Type.Name == "TrajectoryState" && st.Type.Has("TrajX"))
                {
                    // the four sample arrays are the same length in every one of the 19,244 shipped actor controllers
                    long n5 = LenOf(st["TrajX"]);
                    foreach (string f5 in new[] { "TrajY", "TrajZ", "TrajAngles" })
                        if (st.Has(f5) && LenOf(st[f5]) != n5) bad.Add(new CountIssue(path + "." + f5 + " (length)", LenOf(st[f5]), n5));
                    // the sample count is (TrajEndTick - TrajStartTick) / RecordDeltaT + 1 in 4,395 of the 4,563
                    // shipped trajectories and one fewer in the other 168, so it is not checked here
                }
                if (st.Type.Name == "AudioEventTag" && st.Type.Has("ParamTypes"))
                {
                    // the game loads one array per parameter kind; all 7,300 shipped tags keep them paired
                    long want3 = LenOf(st["IntIDs"]) + LenOf(st["FloatIDs"]) + LenOf(st["BoolIDs"]) + LenOf(st["StringIDs"]);
                    long have3 = LenOf(st["ParamTypes"]);
                    if (have3 != want3) bad.Add(new CountIssue(path + ".ParamTypes (length)", have3, want3));
                    foreach (string kk in new[] { "Int", "Float", "Bool", "String" })
                    {
                        if (st.Has(kk + "IDs") && st.Has(kk + "Vals") && LenOf(st[kk + "IDs"]) != LenOf(st[kk + "Vals"]))
                            bad.Add(new CountIssue(path + "." + kk + "Vals (length)", LenOf(st[kk + "Vals"]), LenOf(st[kk + "IDs"])));
                        if (st.Has(kk + "GSIDs") && st.Has(kk + "GSs") && LenOf(st[kk + "GSIDs"]) != LenOf(st[kk + "GSs"]))
                            bad.Add(new CountIssue(path + "." + kk + "GSs (length)", LenOf(st[kk + "GSs"]), LenOf(st[kk + "GSIDs"])));
                    }
                }
                if (st.Type.Name == "DatasetPreSaveAsset" && st.Type.Has("BlendTimesCount"))
                {
                    // the 29 shipped assets that set BlendTimesCount all hold one Min/Max per blend time and one
                    // NX x NY plane per pair of them; the two that leave it 0 are not checked
                    long btc = st.GetInt("BlendTimesCount");
                    if (btc > 0)
                    {
                        if (LenOf(st["Min"]) != btc) bad.Add(new CountIssue(path + ".Min (length)", LenOf(st["Min"]), btc));
                        if (LenOf(st["Max"]) != btc) bad.Add(new CountIssue(path + ".Max (length)", LenOf(st["Max"]), btc));
                        long want4 = st.GetInt("NX") * st.GetInt("NY") * ((btc + 1) / 2);
                        if (LenOf(st["Data"]) != want4) bad.Add(new CountIssue(path + ".Data (length)", LenOf(st["Data"]), want4));
                    }
                }
                if (st.Type.Name == "StepAssetImpl" && st.Type.Has("ShoulderAngle") && st.Type.Has("EndT"))
                {
                    // one shoulder angle per tick of the step, 0..EndT: it holds for all 3,472 shipped steps
                    GdStruct e = st["EndT"] as GdStruct;
                    if (e != null && e.Type.Has("T"))
                    {
                        long want2 = e.GetInt("T") + 1;
                        long have2 = LenOf(st["ShoulderAngle"]);
                        if (have2 != want2) bad.Add(new CountIssue(path + ".ShoulderAngle (length)", have2, want2));
                    }
                }
                CountRule[] rules;
                if (!CountRules.TryGetValue(st.Type.Name, out rules)) return;
                foreach (CountRule r in rules)
                {
                    if (!st.Has(r.Count) || !st.Has(r.List)) continue;
                    long n = LenOf(st[r.List]);
                    if (!LongEq(st[r.Count], n)) bad.Add(new CountIssue(path + "." + r.Count, st[r.Count], n));
                    foreach (string other in r.Others)
                        if (st.Has(other) && LenOf(st[other]) != n)
                            bad.Add(new CountIssue(path + "." + other + " (length)", LenOf(st[other]), n));
                }
            });
            return bad;
        }

        /// <summary>returns the number of count fields changed</summary>
        public static int FixCounts(GdStruct o)
        {
            int changed = 0;
            foreach (Tuple<string, GdObj> cp in CdbParts(o)) changed += FixCdb(cp.Item2);
            Walk(o, o.Type.Name, (st, path) =>
            {
                CountRule[] rules;
                if (!CountRules.TryGetValue(st.Type.Name, out rules)) return;
                foreach (CountRule r in rules)
                {
                    if (!st.Has(r.Count) || !st.Has(r.List)) continue;
                    long n = LenOf(st[r.List]);
                    if (!LongEq(st[r.Count], n))
                    {
                        st[r.Count] = n;
                        changed++;
                    }
                }
            });
            return changed;
        }

        public static int FixAll(AntDb db, Action<string> log = null)
        {
            int n = FixLinks(db, log) + BlendSpaces.Fix(db, log);
            foreach (int i in db.Cache.Keys.ToList().Concat(db.NewObjs.Keys.ToList()))
            {
                if (db.Removed.Contains(i)) continue;
                GdObj o = db.Obj(i);
                int c = FixCounts(o);
                if (c > 0 && log != null) log("count fields updated in " + o.Type.Name + " '" + o.Name + "'");
                n += c;
            }
            return n;
        }

        /// <summary>root classes whose objects carry count rules (directly or in nested structs)</summary>
        public static List<string> CheckedClasses(AntDb db)
        {
            HashSet<string> r = new HashSet<string>();
            foreach (string c in CountRules.Keys) if (db.ByType.ContainsKey(c)) r.Add(c);
            foreach (string c in db.ClassesDeriving("ContextDatabaseAsset")) r.Add(c);
            if (db.ByType.ContainsKey("PriorityDatabaseAsset")) r.Add("PriorityDatabaseAsset");
            if (db.ByType.ContainsKey("OverrideGameStatesCollectionAsset")) r.Add("OverrideGameStatesCollectionAsset");
            if (db.ByType.ContainsKey("StepAssetAnt")) r.Add("StepAssetAnt");
            if (db.ByType.ContainsKey("AudioEventTag")) r.Add("AudioEventTag");
            if (db.ByType.ContainsKey("DatasetPreSaveAsset")) r.Add("DatasetPreSaveAsset");
            r.Add("ActorControllerAsset");
            List<string> l = r.ToList();
            l.Sort(StringComparer.Ordinal);
            return l;
        }
    }
}
