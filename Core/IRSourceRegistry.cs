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

        public static List<IRSourceEntry> Entries
        {
            get { return s_entries; }
        }

        public static void Add(Unit owner, IRSource source)
        {
            if (owner == null || source == null) return;
            for (int i = 0; i < s_entries.Count; i++)
            {
                if (ReferenceEquals(s_entries[i].Source, source)) return;
            }
            IRSourceEntry entry;
            entry.Owner = owner;
            entry.Source = source;
            s_entries.Add(entry);
        }

        public static void Remove(IRSource source)
        {
            if (source == null) return;
            for (int i = s_entries.Count - 1; i >= 0; i--)
            {
                if (ReferenceEquals(s_entries[i].Source, source))
                {
                    s_entries.RemoveAt(i);
                    return;
                }
            }
        }

        /// <summary>Removes entries whose owner unit or source transform no longer exists.</summary>
        public static void Prune()
        {
            for (int i = s_entries.Count - 1; i >= 0; i--)
            {
                IRSourceEntry e = s_entries[i];
                if (e.Owner == null || e.Source == null || e.Source.transform == null)
                {
                    s_entries.RemoveAt(i);
                }
            }
        }

        public static void ClearAll()
        {
            s_entries.Clear();
        }
    }
}
