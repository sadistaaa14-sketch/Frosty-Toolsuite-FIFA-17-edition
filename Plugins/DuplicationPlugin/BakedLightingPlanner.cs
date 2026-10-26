using System;
using System.Collections.Generic;
using System.Linq;

namespace DuplicationPlugin
{
    /// <summary>
    /// Finds atlas rectangles for the package's lit meshes in a stadium's Enlighten database (no Frosty types here; the
    /// offline test runs this file as it is).
    /// <list type="number">
    /// <item>The chart records of the lit meshes and of the meshes the import cleared do not hold space; this package's
    /// systems from an earlier import are removed.</item>
    /// <item>Pass A: every combination of levels (finest first, fewest halvings first) is tried, the largest mesh placed
    /// first; the first combination that places every mesh wins, otherwise each mesh gets the finest level that still
    /// fits.</item>
    /// <item>Unless pass A placed every mesh at its finest level: pass B also frees the database's systems whose chart
    /// records all belong to those meshes (their output lights nothing that is still drawn), tries again, and is taken
    /// when it places more meshes or finer levels. Of the freed systems only those a new rectangle overlaps are
    /// dropped.</item>
    /// </list>
    /// </summary>
    public static class BakedLightingPlanner
    {
        public sealed class Candidate
        {
            public string Name;
            /// <summary>Instance key of the mesh (root instance GUID + placement).</summary>
            public uint Key;
            public FbLight Light;
        }

        public sealed class Placement
        {
            public Candidate Mesh;
            public int Level;
            public int X, Y;
            public FbLight.Level Data { get { return Mesh.Light.Levels[Level]; } }
        }

        public sealed class Plan
        {
            public readonly List<Placement> Placed = new List<Placement>();
            public readonly List<Candidate> Unplaced = new List<Candidate>();
            /// <summary>Systems of the database dropped to make room (indices into EnlightenDbEditor.Systems).</summary>
            public readonly List<int> FreedSystems = new List<int>();
            public int PreviousSystemsRemoved;
            public int LevelSum { get { return Placed.Sum(p => p.Level); } }
        }

        /// <summary>Free texels around a new rectangle (FIFA packs its systems 4 apart: SystemPackPadding 4).</summary>
        public const int Margin = 2;
        /// <summary>Level combinations tried per pass at most (3 levels of 6 meshes = 729).</summary>
        public const int MaxCombinations = 4096;

        /// <param name="freedKeys">keys of the lit meshes and of the meshes the import emptied</param>
        public static Plan Make(EnlightenDbEditor db, IList<Candidate> meshes, ICollection<uint> freedKeys, Action<string> log)
        {
            int previous = db.RemoveSystems(meshes.Select(m => new Guid(m.Light.SystemGuid)));
            foreach (uint k in freedKeys) db.IgnoreInstance(k);
            List<Candidate> order = meshes.OrderByDescending(m => m.Light.Levels[0].Area).ToList();

            Plan a = TryAll(db, order);
            a.PreviousSystemsRemoved = previous;
            if (a.Unplaced.Count == 0 && a.LevelSum == 0)
                return a;

            List<int> removable = db.SystemsServingOnly(freedKeys);
            if (removable.Count == 0)
            {
                log("  no system of the database lights only meshes the import replaces or empties; nothing to free");
                return a;
            }
            db.RemoveSystemIndices(removable);
            Plan b = TryAll(db, order);
            b.PreviousSystemsRemoved = previous;
            bool better = b.Placed.Count > a.Placed.Count || (b.Placed.Count == a.Placed.Count && b.LevelSum < a.LevelSum);
            if (!better)
            {
                db.RestoreSystemIndices(removable);
                log(string.Format("  freeing the {0} system(s) that light only replaced or emptied meshes would not help; none freed", removable.Count));
                return a;
            }
            // keep only the freed systems a new rectangle (with its margin) overlaps
            List<int> needed = removable.Where(i =>
            {
                EnlightenDbEditor.SystemInfo s = db.Systems[i];
                return b.Placed.Any(p => p.X - Margin < s.X + s.W && s.X < p.X + p.Data.Width + Margin && p.Y - Margin < s.Y + s.H && s.Y < p.Y + p.Data.Height + Margin);
            }).ToList();
            db.RestoreSystemIndices(removable.Except(needed));
            b.FreedSystems.AddRange(needed);
            return b;
        }

        /// <summary>Level combinations by number of halvings, then by the area they give up; the first that places every mesh wins.</summary>
        private static Plan TryAll(EnlightenDbEditor db, List<Candidate> order)
        {
            List<int[]> combos = new List<int[]>();
            int[] cur = new int[order.Count];
            long total = 1;
            foreach (Candidate c in order) total *= c.Light.Levels.Count;
            if (total <= MaxCombinations)
            {
                Enumerate(order, 0, cur, combos);
                combos = combos.OrderBy(c => c.Sum()).ThenBy(c => Lost(order, c)).ToList();
                foreach (int[] combo in combos)
                {
                    Plan p = Attempt(db, order, combo, true);
                    if (p != null) return p;
                }
            }
            // no combination places every mesh: each the finest level that still fits, the largest first
            db.ClearReservations();
            Plan g = new Plan();
            foreach (Candidate c in order)
            {
                bool placed = false;
                for (int lv = 0; lv < c.Light.Levels.Count && !placed; lv++)
                {
                    int x, y;
                    if (db.Place(c.Light.Levels[lv].Width, c.Light.Levels[lv].Height, Margin, out x, out y))
                    {
                        g.Placed.Add(new Placement { Mesh = c, Level = lv, X = x, Y = y });
                        placed = true;
                    }
                }
                if (!placed) g.Unplaced.Add(c);
            }
            return g;
        }

        private static void Enumerate(List<Candidate> order, int i, int[] cur, List<int[]> outList)
        {
            if (i == order.Count) { outList.Add((int[])cur.Clone()); return; }
            for (int lv = 0; lv < order[i].Light.Levels.Count; lv++)
            {
                cur[i] = lv;
                Enumerate(order, i + 1, cur, outList);
            }
        }

        private static long Lost(List<Candidate> order, int[] combo)
        {
            long lost = 0;
            for (int i = 0; i < order.Count; i++) lost += order[i].Light.Levels[0].Area - order[i].Light.Levels[combo[i]].Area;
            return lost;
        }

        /// <summary>Places the meshes at the given levels (largest first); null when one does not fit.</summary>
        private static Plan Attempt(EnlightenDbEditor db, List<Candidate> order, int[] combo, bool all)
        {
            db.ClearReservations();
            Plan p = new Plan();
            for (int i = 0; i < order.Count; i++)
            {
                FbLight.Level lv = order[i].Light.Levels[combo[i]];
                int x, y;
                if (!db.Place(lv.Width, lv.Height, Margin, out x, out y))
                {
                    if (all) return null;
                    p.Unplaced.Add(order[i]);
                    continue;
                }
                p.Placed.Add(new Placement { Mesh = order[i], Level = combo[i], X = x, Y = y });
            }
            return p;
        }
    }
}
