using System;
using System.Collections;
using System.Collections.Generic;

namespace FrostyAnimationEditor.Core
{
    public struct ResolvedRef
    {
        public ulong Key;
        public int Times;
        public ResolvedRef(ulong key, int times) { Key = key; Times = times; }
    }

    /// <summary>
    /// Exact models of the two header values the game sizes from the file's content.
    /// H7 - the reference-waiting reserve (H7 x 16-byte entries).  While the game
    ///      attaches objects in file order, every key reference to an object of this file that is not
    ///      attached yet gets a waiter; attaching an object releases its
    ///      waiters.  The pool grows to the peak number of pending waiters; the shipped H7
    ///      (354,549) is exactly that peak.  Each key field is resolved once, except where a loader
    ///      resolves it several times or not at all (ResolveMult).
    /// H8 - the AntLinearResourceAllocator size; version-3 files get H8 + 0xC0080 + (H8 + 0xC0000) / 0xA00
    ///      bytes.  The shipped file uses H8 - 704 bytes during the blob loop and 11,472 more in the
    ///      post-load step (AllocModel gives the exact request list of every object in both phases).
    /// </summary>
    public static class Runtime
    {
        private static readonly Dictionary<string, int> ResolveMult = new Dictionary<string, int>
        {
            { "GameStatePostUpdateSceneOp|GameStatePostUpdateSceneOp.UpdateList[]", 2 },
            { "ImmediateTagCollectionAsset|ImmediateTagCollectionAsset.ImmediateTagKeys[]", 2 },
            { "LayersControllerAsset|LayersControllerAsset.SignalAssets[]", 2 },
            { "StateFlowNodeControllerAsset|StateFlowNodeControllerAsset.ActiveSignals[]", 3 },
            { "StateFlowNodeControllerAsset|StateFlowNodeControllerAsset.EntryConditionsRequiredFalse[]", 4 },
            { "StateFlowNodeControllerAsset|StateFlowNodeControllerAsset.EntryConditionsRequiredTrue[]", 4 },
            { "StateFlowNodeControllerAsset|StateFlowNodeControllerAsset.SubjectController", 2 },
            { "StateFlowNodeControllerAsset|StateFlowNodeControllerAsset.TransitionLinks[]", 0 },     // not loaded
            { "StateFlowTransitionAsset|StateFlowTransitionAsset.ConditionsRequiredFalse[]", 4 },
            { "StateFlowTransitionAsset|StateFlowTransitionAsset.ConditionsRequiredTrue[]", 4 },
            { "ExpressionAsset|ExpressionAsset.OwnerAsset", 0 },                                     // stored raw
        };

        public static long LinearSlack(long h8)
        {
            return (h8 + 0xC0080 + ((h8 + 0xC0000) & 0xFFFFFFFF) / 0xA00) & 0xFFFFFFFF;
        }

        private static bool TryExprGsv(string p, out int index)
        {
            const string pre = "ExpressionAsset.GameStateValueAssets[";
            index = -1;
            if (!p.StartsWith(pre, StringComparison.Ordinal) || !p.EndsWith("]", StringComparison.Ordinal)) return false;
            string num = p.Substring(pre.Length, p.Length - pre.Length - 1);
            if (num.Length == 0) return false;
            foreach (char c in num) if (c < '0' || c > '9') return false;
            index = int.Parse(num);
            return true;
        }

        /// <summary>[(key, times resolved)] for the key references of object o</summary>
        public static List<ResolvedRef> ResolvedRefs(GdObj o, List<KeyRef> refs)
        {
            string t = o.Type.Name;
            List<ResolvedRef> outp = new List<ResolvedRef>();
            if (t == "DataInterfaceAsset")
            {
                // the bind method walks its own members and resolves MemberAssetReferences[i] for the first i
                // with MemberUniqueIds[i] == the member id
                JObject di = AllocModel.DataInterface(o);
                if (di == null || di.Arr("members").Count == 0) return outp;
                IList ids = o["MemberUniqueIds"] as IList ?? new object[0];
                IList mref = o["MemberAssetReferences"] as IList ?? new object[0];
                Dictionary<string, int> first = new Dictionary<string, int>();
                for (int i = 0; i < ids.Count; i++)
                {
                    string x = ((uint)Num.ToLong(ids[i])).ToString("x8");
                    if (!first.ContainsKey(x)) first[x] = i;
                }
                foreach (object m in di.Arr("members"))
                {
                    int i;
                    if (first.TryGetValue((string)m, out i) && i < mref.Count)
                    {
                        ulong k = Num.ToKey(mref[i]);
                        if (k != 0) outp.Add(new ResolvedRef(k, 1));
                    }
                }
                return outp;
            }
            if (t == "ExpressionAsset")
            {
                // the game resolves GameStateValueAssets[i] unless GameStateAssetOffsets[i] is -1;
                // the *DBL variants are not loaded by this build
                IList offs = o["GameStateAssetOffsets"] as IList ?? new object[0];
                foreach (KeyRef r in refs)
                {
                    if (r.Key == 0) continue;
                    int j;
                    if (TryExprGsv(r.Path, out j))
                    {
                        if (j < offs.Count && (Num.ToLong(offs[j]) & 0xFFFFFFFF) != 0xFFFFFFFF) outp.Add(new ResolvedRef(r.Key, 1));
                    }
                    else if (r.Path.Contains("DBL") || r.Path == "ExpressionAsset.OwnerAsset") continue;
                    else outp.Add(new ResolvedRef(r.Key, 1));
                }
                return outp;
            }
            foreach (KeyRef r in refs)
            {
                if (r.Key == 0) continue;
                int mu;
                if (!ResolveMult.TryGetValue(t + "|" + Refs.Pattern(r.Path), out mu)) mu = 1;
                if (mu != 0) outp.Add(new ResolvedRef(r.Key, mu));
            }
            return outp;
        }

        /// <summary>peak pending waiters while objects attach in order</summary>
        public static long WaiterPeak(IList<ulong> keysInOrder, Func<int, List<ResolvedRef>> refsOf)
        {
            Dictionary<ulong, int> pos = new Dictionary<ulong, int>(keysInOrder.Count);
            for (int n = 0; n < keysInOrder.Count; n++) pos[keysInOrder[n]] = n;
            long[] pending = new long[keysInOrder.Count];
            long outs = 0, peak = 0;
            for (int n = 0; n < keysInOrder.Count; n++)
            {
                List<ResolvedRef> rr = refsOf(n);
                if (rr != null)
                    foreach (ResolvedRef r in rr)
                    {
                        int j;
                        if (pos.TryGetValue(r.Key, out j) && j >= n)
                        {
                            pending[j] += r.Times;
                            outs += r.Times;
                        }
                    }
                if (outs > peak) peak = outs;
                outs -= pending[n];
                pending[n] = 0;
            }
            return peak;
        }

        /// <summary>bump allocator over a sequence of request lists -> offset after the last request</summary>
        public static long Run(IEnumerable<List<Alloc>> seqs, long start = 0)
        {
            long p = start;
            foreach (List<Alloc> seq in seqs)
            {
                if (seq == null) continue;
                foreach (Alloc a in seq) p = ((p + a.Align - 1) & ~(a.Align - 1)) + a.Size;
            }
            return p;
        }

        public static long Run(List<Alloc> seq, long start)
        {
            long p = start;
            if (seq != null) foreach (Alloc a in seq) p = ((p + a.Align - 1) & ~(a.Align - 1)) + a.Size;
            return p;
        }
    }
}
