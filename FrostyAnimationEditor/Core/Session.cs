using System;
using System.Collections.Generic;
using System.Linq;

namespace FrostyAnimationEditor.Core
{
    /// <summary>
    /// Editing session: an AntDb plus an undo/redo journal.
    ///   using (s.Change("set FPS", key)) { s.Db.Get(key)["FPS"] = 60f; }
    /// A change records deep copies of the root objects it names before it runs and notices the objects it adds
    /// or removes; undo puts the copies back, removes what was added and restores what was removed.
    /// </summary>
    public sealed class Session
    {
        public readonly AntDb Db;
        public readonly List<Entry> UndoStack = new List<Entry>();
        public readonly List<Entry> RedoStack = new List<Entry>();
        public bool Dirty;
        public event Action Changed;

        public Session(AntDb db) { Db = db; db.TrackTouches = true; }

        private void TouchKeys(IEnumerable<ulong> keys)
        {
            foreach (ulong k in keys) Db.Touch(Db.BlockOf(k));
        }

        public sealed class Entry
        {
            public string Label;
            public Dictionary<ulong, Tuple<int, GdObj>> Before, After;
            public List<int> Added, Removed;
            public List<ulong> CBefore, CAfter;
            public HashSet<ulong> Keys;
        }

        private Dictionary<ulong, Tuple<int, GdObj>> Snap(IEnumerable<ulong> keys)
        {
            Dictionary<ulong, Tuple<int, GdObj>> r = new Dictionary<ulong, Tuple<int, GdObj>>();
            foreach (ulong k in keys)
            {
                int i = Db.BlockOf(k);
                if (i >= 0 && !Db.Removed.Contains(i)) r[k] = Tuple.Create(i, DeepCopy.Obj(Db.Obj(i)));
            }
            return r;
        }

        public sealed class Scope : IDisposable
        {
            private readonly Session s;
            private readonly Entry e;
            private readonly HashSet<int> new0;
            private readonly HashSet<int> removed0;
            private bool completed;

            internal Scope(Session s, string label, IEnumerable<ulong> keys)
            {
                this.s = s;
                e = new Entry { Label = label, Keys = new HashSet<ulong>(keys) };
                s.TouchKeys(e.Keys);
                e.Before = s.Snap(e.Keys);
                new0 = new HashSet<int>(s.Db.NewObjs.Keys);
                removed0 = new HashSet<int>(s.Db.Removed);
                e.CBefore = new List<ulong>(s.Db.Exports);
            }

            /// <summary>add keys to snapshot (before the change touches them)</summary>
            public void Track(ulong key)
            {
                s.Db.Touch(s.Db.BlockOf(key));
                if (e.Keys.Add(key))
                    foreach (KeyValuePair<ulong, Tuple<int, GdObj>> kv in s.Snap(new[] { key })) e.Before[kv.Key] = kv.Value;
            }

            public void Complete() { completed = true; }

            public void Dispose()
            {
                AntDb db = s.Db;
                s.TouchKeys(e.Keys);
                if (!completed)
                {
                    s.Restore(e.Before);
                    foreach (int i in db.NewObjs.Keys.Where(i => !new0.Contains(i)).ToList()) s.Kill(i);
                    foreach (int i in db.Removed.Where(i => !removed0.Contains(i)).ToList()) s.Revive(i);
                    db.Exports = e.CBefore;
                    return;
                }
                e.Added = db.NewObjs.Keys.Where(i => !new0.Contains(i)).OrderBy(i => i).ToList();
                e.Removed = db.Removed.Where(i => !removed0.Contains(i)).OrderBy(i => i).ToList();
                e.After = s.Snap(e.Keys);
                e.CAfter = new List<ulong>(db.Exports);
                s.UndoStack.Add(e);
                s.RedoStack.Clear();
                s.Dirty = true;
                s.Fire();
            }
        }

        /// <summary>
        /// using (var c = session.Change("label", keys)) { ...edit...; c.Complete(); }
        /// Without Complete() (an exception, or an early return) the change is rolled back.
        /// </summary>
        public Scope Change(string label, params ulong[] keys) { return new Scope(this, label, keys); }
        public Scope Change(string label, IEnumerable<ulong> keys) { return new Scope(this, label, keys); }

        /// <summary>runs an edit as one undoable change; rolls it back if it throws</summary>
        public void Do(string label, IEnumerable<ulong> keys, Action edit)
        {
            using (Scope c = Change(label, keys))
            {
                edit();
                c.Complete();
            }
        }

        private void Fire() { Action a = Changed; if (a != null) a(); }

        private void Restore(Dictionary<ulong, Tuple<int, GdObj>> snap)
        {
            foreach (KeyValuePair<ulong, Tuple<int, GdObj>> kv in snap)
            {
                int i = kv.Value.Item1;
                Db.Touch(i);
                GdObj c = DeepCopy.Obj(kv.Value.Item2);
                if (i >= Db.N0) Db.NewObjs[i] = c;
                else Db.Cache[i] = c;
            }
        }

        private void Kill(int i)
        {
            ulong k = Db.Keys[i];
            Db.Removed.Add(i);
            int cur;
            if (Db.Key2Blk.TryGetValue(k, out cur) && cur == i) Db.Key2Blk.Remove(k);
        }

        private void Revive(int i)
        {
            Db.Removed.Remove(i);
            Db.Key2Blk[Db.Keys[i]] = i;
        }

        public string Undo()
        {
            if (UndoStack.Count == 0) return null;
            Entry e = UndoStack[UndoStack.Count - 1];
            UndoStack.RemoveAt(UndoStack.Count - 1);
            foreach (int i in e.Added) Kill(i);
            foreach (int i in e.Removed) Revive(i);
            Restore(e.Before);
            Db.Exports = new List<ulong>(e.CBefore);
            RedoStack.Add(e);
            Dirty = true;
            Fire();
            return e.Label;
        }

        public string Redo()
        {
            if (RedoStack.Count == 0) return null;
            Entry e = RedoStack[RedoStack.Count - 1];
            RedoStack.RemoveAt(RedoStack.Count - 1);
            foreach (int i in e.Added) Revive(i);
            foreach (int i in e.Removed) Kill(i);
            Restore(e.After);
            Db.Exports = new List<ulong>(e.CAfter);
            UndoStack.Add(e);
            Dirty = true;
            Fire();
            return e.Label;
        }
    }
}
