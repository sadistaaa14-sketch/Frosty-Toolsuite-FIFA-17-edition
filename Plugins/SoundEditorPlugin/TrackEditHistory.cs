using System;
using System.Collections.Generic;

namespace SoundEditorPlugin
{
    /// <summary>
    /// The undo stack behind the editor's <c>Edit &gt; Undo Edit</c> command.
    ///
    /// A sample edit is destructive for the clip it is applied to: it rewrites the bytes in the chunk
    /// or the resource, so putting the previous samples back is the only way to take it back. Each
    /// entry is a clone of the track's samples *before* the edit that was written, which is the whole
    /// reason the stack is shallow: an undo entry for a ten second stereo clip is 1.9 MB, so an
    /// unbounded per-track history would hold gigabytes of a bank that has thousands of clips.
    /// Entries are per track index and independent: undoing track 3 does not disturb track 4's stack.
    /// </summary>
    public class TrackEditHistory
    {
        private readonly int depth;
        private readonly Dictionary<int, List<short[]>> entries = new Dictionary<int, List<short[]>>();

        /// <summary>How many edits of one track can be taken back. Four is a choice, not a limit of
        /// the design - each level costs one copy of the track's samples in memory.</summary>
        public const int DefaultDepth = 4;

        public TrackEditHistory() : this(DefaultDepth)
        {
        }

        public TrackEditHistory(int depth)
        {
            if (depth < 1)
                throw new ArgumentOutOfRangeException(nameof(depth), "An undo history needs at least one level.");
            this.depth = depth;
        }

        public int Depth => depth;

        /// <summary>Records the samples a track had before an edit that has just been written.</summary>
        public void Push(int trackIndex, short[] samples)
        {
            List<short[]> stack;
            if (!entries.TryGetValue(trackIndex, out stack))
            {
                stack = new List<short[]>();
                entries[trackIndex] = stack;
            }

            // Oldest first, so the end of the list is the most recent edit.
            stack.Add(samples == null ? new short[0] : (short[])samples.Clone());
            while (stack.Count > depth)
                stack.RemoveAt(0);
        }

        public bool CanUndo(int trackIndex)
        {
            List<short[]> stack;
            return entries.TryGetValue(trackIndex, out stack) && stack.Count > 0;
        }

        /// <summary>
        /// Hands back the samples the track had before its most recent edit and forgets them, so a
        /// second undo steps one edit further back rather than re-applying the same one.
        /// </summary>
        public bool TryUndo(int trackIndex, out short[] samples)
        {
            samples = null;

            List<short[]> stack;
            if (!entries.TryGetValue(trackIndex, out stack) || stack.Count == 0)
                return false;

            samples = stack[stack.Count - 1];
            stack.RemoveAt(stack.Count - 1);
            return true;
        }

        /// <summary>How many edits of a track are still on its stack.</summary>
        public int CountFor(int trackIndex)
        {
            List<short[]> stack;
            if (!entries.TryGetValue(trackIndex, out stack))
                return 0;
            return stack.Count;
        }

        public void ClearTrack(int trackIndex)
        {
            entries.Remove(trackIndex);
        }

        /// <summary>
        /// Drops everything. Called when the track list is rebuilt, because a reloaded list can have a
        /// different number of tracks - an undo entry that outlived its track index would put the wrong
        /// audio back into the wrong clip.
        /// </summary>
        public void Clear()
        {
            entries.Clear();
        }
    }
}
