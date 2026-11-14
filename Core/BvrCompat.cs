using System;
using System.Reflection;
using HarmonyLib;

namespace CnoEvil.Core
{
    /// <summary>
    /// Run-time binding to Balance and Variety Rework (BVR). The mod has no reference to its assembly: the types are
    /// found by name, and nothing happens when BVR is not installed. BVR changes vanilla prefab values (flare rejection,
    /// SARH lock persistence and relock, munition radarSize, loft, turn rate, laser power, guns, loadouts); CNO-EVIL reads
    /// these vanilla values, so the BVR values apply without a change. One exception: the countermeasure budget of
    /// CNO-EVIL sets the flare count (CountermeasureLoadout), and BVR's flare count sweep must not double the live
    /// ejectors that already have the budget counts (BvrStatsPatch_ApplyFlareCountBuff_Patch).
    /// </summary>
    public static class BvrCompat
    {
        public const string PluginGuid = "com.Draken0015.BVR";
        private const string Ns = "BalanceAndVarietyRework.";

        public static readonly Type StatsPatchType = AccessTools.TypeByName(Ns + "StatsPatch");
        public static readonly Type ModifiedStatsFlagType = AccessTools.TypeByName(Ns + "ModifiedStatsFlag");

        public static readonly MethodInfo FlareCountSweep = (StatsPatchType != null)
            ? AccessTools.Method(StatsPatchType, "ApplyFlareCountBuff", Type.EmptyTypes) : null;

        public static bool Installed
        {
            get { return StatsPatchType != null; }
        }
    }
}
