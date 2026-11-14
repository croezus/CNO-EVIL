using System;
using System.Collections.Generic;

namespace CnoEvil.Core
{
    /// <summary>
    /// Error logging for per-frame Harmony patches: the first exception of each context in a scene is logged in full,
    /// later ones are only counted, so a persistent failure does not flood the BepInEx log every frame.
    /// The end of the scene reports the counts; the next scene logs the first exception of each context again.
    /// </summary>
    public static class PatchErrorLog
    {
        private static readonly Dictionary<string, int> s_counts = new Dictionary<string, int>();

        public static void Once(string context, Exception ex)
        {
            if (Count(context) == 0) Report(context, ex);
        }

        public static void Once(string context, string detail)
        {
            if (Count(context) == 0) Report(context, detail);
        }

        /// <summary>
        /// Scene end: a warning for each context that failed more than once, then the counts are cleared.
        /// </summary>
        public static void ReportAndClear()
        {
            if (CnoEvilPlugin.Log != null)
            {
                foreach (KeyValuePair<string, int> entry in s_counts)
                {
                    if (entry.Value > 1)
                    {
                        CnoEvilPlugin.Log.LogWarning(string.Format("{0} failed {1} times in this scene.", entry.Key, entry.Value));
                    }
                }
            }
            s_counts.Clear();
        }

        private static int Count(string context)
        {
            int count;
            s_counts.TryGetValue(context, out count);
            s_counts[context] = count + 1;
            return count;
        }

        private static void Report(string context, object detail)
        {
            if (CnoEvilPlugin.Log == null) return;
            CnoEvilPlugin.Log.LogError(string.Format("[{0}] {1} error (further occurrences counted to the end of the scene): {2}",
                EvilLog.Stamp(), context, detail));
        }
    }
}
