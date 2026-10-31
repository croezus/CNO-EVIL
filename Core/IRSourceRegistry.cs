using System.Collections.Generic;

namespace CnoEvil.Core
{
    public struct IRSourceEntry
    {
        public Unit Owner;
        public IRSource Source;
    }

    /// <summary>
    /// All live IR sources (engine nozzles, turbines, rocket motors, flares) with their owner unit.
    /// Filled from Unit.AddIRSource / RemoveIRSource, the same registration that the vanilla IR seekers use.
    /// Read by the HMD thermal window for its hot spots.
    /// </summary>
    public static class IRSourceRegistry
    {
        private static readonly List<IRSourceEntry> s_entries = new List<IRSourceEntry>(256);
        // Index of each source in s_entries: O(1) Add / Remove (swap-remove, so the list order is not kept)
        private static readonly Dictionary<IRSource, int> s_index = new Dictionary<IRSource, int>(256);

        public static List<IRSourceEntry> Entries
        {
            get { return s_entries; }
        }

        public static void Add(Unit owner, IRSource source)
        {
            if (owner == null || source == null) return;
            if (s_index.ContainsKey(source)) return;
            IRSourceEntry entry;
            entry.Owner = owner;
            entry.Source = source;
            s_index[source] = s_entries.Count;
            s_entries.Add(entry);
        }

        public static void Remove(IRSource source)
        {
            if (source == null) return;
            int i;
            if (!s_index.TryGetValue(source, out i)) return;
            s_index.Remove(source);
            int last = s_entries.Count - 1;
            if (i != last)
            {
                IRSourceEntry moved = s_entries[last];
                s_entries[i] = moved;
                s_index[moved.Source] = i;
            }
            s_entries.RemoveAt(last);
        }

        /// <summary>Removes entries whose owner unit or source transform no longer exists.</summary>
        public static void Prune()
        {
            int write = 0;
            for (int i = 0; i < s_entries.Count; i++)
            {
                IRSourceEntry e = s_entries[i];
                if (e.Owner == null || e.Source == null || e.Source.transform == null)
                {
                    if (!ReferenceEquals(e.Source, null)) s_index.Remove(e.Source);
                    continue;
                }
                if (write != i)
                {
                    s_entries[write] = e;
                    s_index[e.Source] = write;
                }
                write++;
            }
            if (write < s_entries.Count) s_entries.RemoveRange(write, s_entries.Count - write);
        }

        public static void ClearAll()
        {
            s_entries.Clear();
            s_index.Clear();
        }
    }
}
