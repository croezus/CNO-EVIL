using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using CnoEvil.Config;
using CnoEvil.Core;

namespace CnoEvil.Patches
{
    // Patches on Balance and Variety Rework types (BvrCompat). Prepare skips each class when BVR is not installed.

    /// <summary>
    /// BVR multiplies maxAmmo and ammo of every loaded FlareEjector once, at the first WeaponManager.SpawnWeapons, and
    /// skips an ejector with its ModifiedStatsFlag. The sweep also finds the live ejectors of the aircraft that exist at
    /// that moment, which already have the counts of the CNO-EVIL budget (Aircraft_Awake_Patch): they would get the
    /// budget flares times the multiplier and a station total that does not agree. While the mod is on, the prefix marks
    /// the live ejectors with the BVR flag, so BVR changes only the prefabs. The budget sets the flares of each aircraft;
    /// with the mod off (F10, multiplayer Pending / Inert) a new aircraft gets the BVR count from its prefab.
    /// </summary>
    [HarmonyPatch]
    public static class BvrStatsPatch_ApplyFlareCountBuff_Patch
    {
        public static bool Prepare()
        {
            return BvrCompat.FlareCountSweep != null && BvrCompat.ModifiedStatsFlagType != null;
        }

        public static MethodBase TargetMethod()
        {
            return BvrCompat.FlareCountSweep;
        }

        [HarmonyPrefix]
        public static void Prefix()
        {
            FlareEjector[] ejectors = null;
            // Also with the mod off: the vanilla flare count of each prefab before the sweep doubles it (budget floor)
            try
            {
                ejectors = Resources.FindObjectsOfTypeAll<FlareEjector>();
                CountermeasureLoadout.RecordPrefabFlares(ejectors, BvrCompat.ModifiedStatsFlagType);
            }
            catch (Exception ex)
            {
                PatchErrorLog.Once("BvrStatsPatch_ApplyFlareCountBuff_Patch.Record", ex);
            }

            if (!CnoEvilConfig.IsModActive || ejectors == null) return;
            try
            {
                for (int i = 0; i < ejectors.Length; i++)
                {
                    FlareEjector e = ejectors[i];
                    if (e == null || !e.gameObject.scene.IsValid()) continue;
                    if (e.gameObject.GetComponent(BvrCompat.ModifiedStatsFlagType) == null)
                    {
                        e.gameObject.AddComponent(BvrCompat.ModifiedStatsFlagType);
                    }
                }
            }
            catch (Exception ex)
            {
                PatchErrorLog.Once("BvrStatsPatch_ApplyFlareCountBuff_Patch", ex);
            }
        }
    }
}
