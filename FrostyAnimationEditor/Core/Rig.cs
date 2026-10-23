using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;

namespace FrostyAnimationEditor.Core
{
    /// <summary>
    /// Rig knowledge: DOF ids -> names, skeletons, bind poses and which rig an animation drives.
    ///  * RigAsset.DofIds is partitioned by RigAsset.DofSetIdIndices into one run per RigDofSets entry; RigDofSets[j]
    ///    is a LayoutAsset whose Slots ({Name, Type}) name the DOFs of that run -> "Joint.q" / "Joint.t" / "Joint.s"
    ///    or float names (2,574 ids, no conflicts across the 18 rigs).
    ///  * Slot Type 14 = Quaternion, 0x7A2E5497 = translation, 0x7A2E53C6 = scale, 10 = Float, 12 = Vector3,
    ///    6 = Int32, 15 = Matrix44.
    ///  * RigAsset.Default{Vector4,Vector3,Float,Int}Values = bind/default value of every DOF id.
    ///  * SkeletonAsset.Joints = [{JointName, ParentIndex, JointType}], TrajJointIndex.
    /// </summary>
    public sealed class RigIndex
    {
        public const long SlotQuat = 14, SlotTrans = 2049856663, SlotScale = 2049856454, SlotFloat = 10, SlotVec3 = 12, SlotInt = 6, SlotMatrix = 15;

        public static string SlotTypeName(long t)
        {
            if (t == SlotQuat) return "quat";
            if (t == SlotTrans) return "translation";
            if (t == SlotScale) return "scale";
            if (t == SlotFloat) return "float";
            if (t == SlotVec3) return "vec3";
            if (t == SlotInt) return "int";
            if (t == SlotMatrix) return "matrix";
            return t.ToString();
        }

        public readonly AntDb Db;
        public readonly Dictionary<long, Tuple<string, long>> Dof = new Dictionary<long, Tuple<string, long>>();
        public readonly Dictionary<ulong, GdObj> Rigs = new Dictionary<ulong, GdObj>();
        public readonly List<ulong> RigOrder = new List<ulong>();
        public readonly Dictionary<ulong, Dictionary<long, object>> Defaults = new Dictionary<ulong, Dictionary<long, object>>();
        public readonly Dictionary<ulong, List<ulong>> LhToRigs = new Dictionary<ulong, List<ulong>>();

        public sealed class Joint
        {
            public string Name;
            public int Parent;
            public long Type;
        }

        public sealed class Skeleton
        {
            public string Name;
            public List<Joint> Joints = new List<Joint>();
            public long TrajJoint;
        }

        public RigIndex(AntDb db)
        {
            Db = db;
            Build();
        }

        private static List<long> Longs(object v)
        {
            List<long> r = new List<long>();
            if (v is byte[]) { foreach (byte b in (byte[])v) r.Add(b); return r; }
            IList l = v as IList;
            if (l != null) foreach (object x in l) r.Add(Num.ToLong(x));
            return r;
        }

        private void Build()
        {
            foreach (int i in Db.BlockIds("RigAsset"))
            {
                GdObj r = Db.Obj(i);
                Rigs[r.Key] = r;
                RigOrder.Add(r.Key);
                List<long> ids = Longs(r["DofIds"]), idx = Longs(r["DofSetIdIndices"]);
                IList sets = r["RigDofSets"] as IList ?? new object[0];
                for (int j = 0; j < sets.Count; j++)
                {
                    if (j >= idx.Count) break;
                    int a = (int)idx[j];
                    int b = j + 1 < idx.Count ? (int)idx[j + 1] : ids.Count;
                    if (a < 0 || b > ids.Count || a > b) continue;
                    GdObj la = Db.Get(Num.ToKey(sets[j]));
                    IList slots = la != null ? la.GetInherited("Slots") as IList : null;
                    if (slots == null || slots.Count != b - a) continue;
                    for (int s = 0; s < slots.Count; s++)
                    {
                        long d = ids[a + s];
                        GdStruct sl = (GdStruct)slots[s];
                        if (!Dof.ContainsKey(d)) Dof[d] = Tuple.Create(Str.Text(sl["Name"]) ?? "", sl.GetInt("Type"));
                    }
                }
                Dictionary<long, object> dv = new Dictionary<long, object>();
                foreach (object e in r["DefaultVector4Values"] as IList ?? new object[0])
                {
                    GdStruct d = (GdStruct)e;
                    dv[d.GetInt("DofId")] = ((float[])d["Value"]).Clone();
                }
                foreach (object e in r["DefaultVector3Values"] as IList ?? new object[0])
                {
                    GdStruct d = (GdStruct)e;
                    float[] v = (float[])d["Value"];
                    dv[d.GetInt("DofId")] = new[] { v[0], v[1], v[2] };
                }
                foreach (object e in r["DefaultFloatValues"] as IList ?? new object[0])
                {
                    GdStruct d = (GdStruct)e;
                    dv[d.GetInt("DofId")] = d["Value"];
                }
                foreach (object e in r["DefaultIntValues"] as IList ?? new object[0])
                {
                    GdStruct d = (GdStruct)e;
                    dv[d.GetInt("DofId")] = d["Value"];
                }
                Defaults[r.Key] = dv;
                foreach (object k in r["DofSetLists"] as IList ?? new object[0])
                    foreach (ulong lh in LhClosure(Num.ToKey(k), new HashSet<ulong>()))
                    {
                        List<ulong> l;
                        if (!LhToRigs.TryGetValue(lh, out l)) { l = new List<ulong>(); LhToRigs[lh] = l; }
                        if (!l.Contains(r.Key)) l.Add(r.Key);
                    }
            }
        }

        private HashSet<ulong> LhClosure(ulong k, HashSet<ulong> seen)
        {
            if (seen.Contains(k)) return seen;
            seen.Add(k);
            GdObj o = Db.Get(k);
            if (o != null && o.Type.Name == "LayoutHierarchyAsset")
                foreach (object c in o["Children"] as IList ?? new object[0]) LhClosure(Num.ToKey(c), seen);
            return seen;
        }

        public string DofName(long d)
        {
            Tuple<string, long> n;
            return Dof.TryGetValue(d, out n) ? n.Item1 : "dof_" + d;
        }

        public string DofType(long d)
        {
            Tuple<string, long> n;
            return Dof.TryGetValue(d, out n) ? SlotTypeName(n.Item2) : "?";
        }

        public List<GdObj> RigsForLayout(ulong lhKey)
        {
            List<ulong> l;
            if (!LhToRigs.TryGetValue(lhKey, out l)) return new List<GdObj>();
            return l.Select(k => Rigs[k]).ToList();
        }

        /// <summary>the rig that drives a LayoutHierarchy; prefers the one with a skeleton whose defaults cover
        /// the most of the given dof ids</summary>
        public GdObj BestRig(ulong lhKey, IList<long> dofIds = null)
        {
            List<GdObj> cands = RigsForLayout(lhKey);
            if (cands.Count == 0) return null;
            GdObj best = null;
            long[] bestScore = null;
            foreach (GdObj r in cands)
            {
                Dictionary<long, object> dv;
                Defaults.TryGetValue(r.Key, out dv);
                long cov = 0;
                if (dofIds != null && dv != null) foreach (long d in dofIds) if (dv.ContainsKey(d)) cov++;
                long sk = Db.Get(r.GetKey("Skeleton")) != null ? 1 : 0;
                long[] s = { cov, sk, -AllocModel.LenOf(r["DofIds"]) };
                if (bestScore == null || Compare(s, bestScore) > 0) { best = r; bestScore = s; }
            }
            return best;
        }

        private static int Compare(long[] a, long[] b)
        {
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return a[i].CompareTo(b[i]);
            return 0;
        }

        public Skeleton GetSkeleton(GdObj rig)
        {
            GdObj sk = rig != null ? Db.Get(rig.GetKey("Skeleton")) : null;
            if (sk == null) return null;
            Skeleton s = new Skeleton { Name = sk.Name, TrajJoint = sk.GetInt("TrajJointIndex") };
            foreach (object e in sk["Joints"] as IList ?? new object[0])
            {
                GdStruct j = (GdStruct)e;
                s.Joints.Add(new Joint { Name = Str.Text(j["JointName"]) ?? "", Parent = (int)j.GetInt("ParentIndex"), Type = j.GetInt("JointType") });
            }
            return s;
        }

        /// <summary>{joint name: {"q": float[4], "t": float[3], "s": float[3]}} from the rig defaults</summary>
        public Dictionary<string, Dictionary<string, float[]>> BindPose(GdObj rig)
        {
            Dictionary<string, Dictionary<string, float[]>> r = new Dictionary<string, Dictionary<string, float[]>>();
            Dictionary<long, object> dv;
            if (!Defaults.TryGetValue(rig.Key, out dv)) return r;
            foreach (KeyValuePair<long, object> kv in dv)
            {
                Tuple<string, long> n;
                if (!Dof.TryGetValue(kv.Key, out n)) continue;
                string name = n.Item1;
                long typ = n.Item2;
                int dot = name.LastIndexOf('.');
                if (dot < 0 || !(typ == SlotQuat || typ == SlotTrans || typ == SlotScale)) continue;
                string j = name.Substring(0, dot), comp = name.Substring(dot + 1);
                float[] v = kv.Value as float[];
                if (v == null) continue;
                Dictionary<string, float[]> d;
                if (!r.TryGetValue(j, out d)) { d = new Dictionary<string, float[]>(); r[j] = d; }
                d[comp] = v;
            }
            return r;
        }
    }
}
