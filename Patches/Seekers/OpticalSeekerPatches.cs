using System;
using HarmonyLib;
using UnityEngine;
using CnoEvil.Core;
using CnoEvil.Config;

namespace CnoEvil.Patches
{
    /// <summary>
    /// Shared config overrides of the optical seekers (0 = vanilla).
    /// </summary>
    internal static class OpticalSeekerConfig
    {
        public static void ApplySearchRadius(ref float searchRadius)
        {
            float r = (CnoEvilConfig.OpticalSearchRadiusMeters != null) ? CnoEvilConfig.OpticalSearchRadiusMeters.Value : 0.0f;
            if (r > 0.0f) searchRadius = r;
        }

        /// <summary>
        /// False when OpticalSeekerActivationRangeMeters is set and the target is farther than it.
        /// </summary>
        public static bool InActivationRange(Missile missile, Unit target)
        {
            float range = MunitionCatalog.OpticalActivationRange;
            if (range <= 0.0f || missile == null || target == null) return true;
            return (missile.transform.position - target.transform.position).sqrMagnitude <= range * range;
        }
    }

    /// <summary>
    /// Optical missiles (AGM-48, AGM-68, ATP-1, Tusko-B, AT-145, GS25, glide bombs): the launch datum is the
    /// faction track + dispersion. The vanilla seeker finds the target inside searchRadius of the datum.
    /// </summary>
    [HarmonyPatch(typeof(OpticalSeeker), "Initialize", new Type[] { typeof(Unit), typeof(GlobalPosition) })]
    public static class OpticalSeeker_Initialize_Patch
    {
        [HarmonyPostfix]
        public static void Postfix(
            OpticalSeeker __instance,
            Unit target,
            Missile ___missile,
            ref GlobalPosition ___knownPos,
            ref Vector3 ___knownVel,
            ref float ___searchRadius)
        {
            try
            {
                if (!CnoEvilConfig.IsModActive) return;
                if (__instance == null || ___missile == null) return;

                OpticalSeekerConfig.ApplySearchRadius(ref ___searchRadius);
                SeekerDispersionHelper.ApplyLaunchDatum(___missile, target, ref ___knownPos, ref ___knownVel);
            }
            catch (Exception ex)
            {
                PatchErrorLog.Once("OpticalSeeker_Initialize_Patch", ex);
            }
        }
    }

    /// <summary>
    /// Vanilla OpticalCheck moves the datum to the true target while the faction tracks it (datalink). For a target
    /// the mod guides, the datum is the faction track + dispersion instead (or stays without a track), and the
    /// vanilla datalink step is off for this call. The vanilla visual check (searchRadius, line of sight,
    /// searchAngle) then runs unchanged.
    /// </summary>
    [HarmonyPatch(typeof(OpticalSeeker), "OpticalCheck")]
    public static class OpticalSeeker_OpticalCheck_Patch
    {
        [HarmonyPrefix]
        public static void Prefix(
            OpticalSeeker __instance,
            Missile ___missile,
            Unit ___targetUnit,
            bool ___hasVisual,
            ref bool ___useDatalink,
            ref GlobalPosition ___knownPos,
            ref Vector3 ___knownVel,
            out bool __state)
        {
            __state = false;
            try
            {
                if (!CnoEvilConfig.IsModActive) return;
                if (__instance == null || ___missile == null) return;
                if (___hasVisual || !___useDatalink) return;
                if (!SeekerDispersionHelper.IsModGuided(___missile, ___targetUnit) || SeekerDispersionHelper.HasSeekerAcquired(___missile)) return;

                SeekerDispersionHelper.CorrectDatum(___missile, ___targetUnit, ref ___knownPos, ref ___knownVel, ___knownPos, false);
                ___useDatalink = false;
                __state = true;
            }
            catch (Exception ex)
            {
                PatchErrorLog.Once("OpticalSeeker_OpticalCheck_Patch", ex);
            }
        }

        [HarmonyPostfix]
        public static void Postfix(
            OpticalSeeker __instance,
            Missile ___missile,
            Unit ___targetUnit,
            ref bool ___hasVisual,
            ref bool ___useDatalink,
            bool __state)
        {
            if (__state) ___useDatalink = true;
            try
            {
                if (!CnoEvilConfig.IsModActive) return;
                if (__instance == null || ___missile == null || !___hasVisual) return;

                if (!OpticalSeekerConfig.InActivationRange(___missile, ___targetUnit))
                {
                    ___hasVisual = false;
                    return;
                }
                SeekerDispersionHelper.MarkSeekerAcquired(___missile);
            }
            catch (Exception ex)
            {
                PatchErrorLog.Once("OpticalSeeker_OpticalCheck_Patch", ex);
            }
        }
    }

    /// <summary>
    /// Guided bombs (PAB, GPO, CBO-400, demolition, nuclear): launch datum = faction track + dispersion.
    /// Vanilla bombs have no datalink; they find the target inside searchRadius of the datum.
    /// </summary>
    [HarmonyPatch(typeof(OpticalSeekerBomb), "Initialize", new Type[] { typeof(Unit), typeof(GlobalPosition) })]
    public static class OpticalSeekerBomb_Initialize_Patch
    {
        [HarmonyPostfix]
        public static void Postfix(
            OpticalSeekerBomb __instance,
            Unit target,
            Missile ___missile,
            ref GlobalPosition ___knownPos,
            ref Vector3 ___knownVel,
            ref float ___searchRadius)
        {
            try
            {
                if (!CnoEvilConfig.IsModActive) return;
                if (__instance == null || ___missile == null) return;

                OpticalSeekerConfig.ApplySearchRadius(ref ___searchRadius);
                SeekerDispersionHelper.ApplyLaunchDatum(___missile, target, ref ___knownPos, ref ___knownVel);
            }
            catch (Exception ex)
            {
                PatchErrorLog.Once("OpticalSeekerBomb_Initialize_Patch", ex);
            }
        }
    }

    /// <summary>
    /// Optional activation range of the bomb seeker (OpticalSeekerActivationRangeMeters). 0 = vanilla.
    /// </summary>
    [HarmonyPatch(typeof(OpticalSeekerBomb), "TrackVisual")]
    public static class OpticalSeekerBomb_TrackVisual_Patch
    {
        [HarmonyPostfix]
        public static void Postfix(ref bool __result, Missile ___missile, Unit ___targetUnit)
        {
            try
            {
                if (!CnoEvilConfig.IsModActive || !__result) return;
                if (!OpticalSeekerConfig.InActivationRange(___missile, ___targetUnit)) __result = false;
            }
            catch (Exception ex)
            {
                PatchErrorLog.Once("OpticalSeekerBomb_TrackVisual_Patch", ex);
            }
        }
    }

    /// <summary>
    /// Guided shells (76 mm, 127 mm): launch datum = faction track + dispersion. The vanilla seeker finds the
    /// target inside searchRadius of the datum.
    /// </summary>
    [HarmonyPatch(typeof(OpticalSeekerShell), "Initialize", new Type[] { typeof(Unit), typeof(GlobalPosition) })]
    public static class OpticalSeekerShell_Initialize_Patch
    {
        [HarmonyPostfix]
        public static void Postfix(
            OpticalSeekerShell __instance,
            Unit target,
            Missile ___missile,
            ref GlobalPosition ___knownPos,
            ref Vector3 ___knownVel,
            ref float ___searchRadius)
        {
            try
            {
                if (!CnoEvilConfig.IsModActive) return;
                if (__instance == null || ___missile == null) return;

                OpticalSeekerConfig.ApplySearchRadius(ref ___searchRadius);
                SeekerDispersionHelper.ApplyLaunchDatum(___missile, target, ref ___knownPos, ref ___knownVel);
            }
            catch (Exception ex)
            {
                PatchErrorLog.Once("OpticalSeekerShell_Initialize_Patch", ex);
            }
        }
    }

    /// <summary>
    /// Optional activation range of the shell seeker (OpticalSeekerActivationRangeMeters). 0 = vanilla.
    /// </summary>
    [HarmonyPatch(typeof(OpticalSeekerShell), "TrackVisual")]
    public static class OpticalSeekerShell_TrackVisual_Patch
    {
        [HarmonyPostfix]
        public static void Postfix(ref bool __result, Missile ___missile, Unit ___targetUnit)
        {
            try
            {
                if (!CnoEvilConfig.IsModActive || !__result) return;
                if (!OpticalSeekerConfig.InActivationRange(___missile, ___targetUnit)) __result = false;
            }
            catch (Exception ex)
            {
                PatchErrorLog.Once("OpticalSeekerShell_TrackVisual_Patch", ex);
            }
        }
    }
}
