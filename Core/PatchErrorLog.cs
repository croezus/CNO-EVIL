using System;
using System.Collections.Generic;

namespace CnoEvil.Core
{
    /// <summary>
    /// Error logging for per-frame Harmony patches: the first exception of each context is logged in full,
    /// later ones are only counted, so a persistent failure does not flood the BepInEx log every frame.
    /// </summary>
    public static class PatchErrorLog
    {
        private static readonly Dictionary<string, int> s_counts = new Dictionary<string, int>();

        public static void Once(string context, Exception ex)
        {
            int count;
            s_counts.TryGetValue(context, out count);
            s_counts[context] = count + 1;

            if (count == 0 && CnoEvilPlugin.Log != null)
            {
                CnoEvilPlugin.Log.LogError(string.Format("{0} error (further occurrences suppressed): {1}", context, ex));
            }
        }
    }
}
