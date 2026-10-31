using System;
using System.Collections.Generic;
using System.Reflection;
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
            float r = CnoEvilConfig.OpticalSearchRadius_m.Value;
            if (r > 0.0f) searchRadius = r;
        }

        /// <summary>
        /// False when OpticalSeekerActivationRange_m is set and the target is farther than it.
        /// </summary>
        public static bool InActivationRange(Missile missile, Unit target)
        {
            float range = MunitionCatalog.OpticalActivationRange;
            if (range <= 0.0f || missile == null || target == null) return true;
            return (missile.transform.position - target.transform.position).sqrMagnitude <= range * range;
        }
    }

    /// <summary>
    /// Optical missiles (AGM-48, AGM-68, ATP-1, Tusko-B, AT-145, GS25, glide bombs), guided bombs (PAB, GPO,
    /// CBO-400, demolition, nuclear) and guided shells (76 mm, 127 mm): the launch datum is the faction track +
    /// dispersion. The vanilla seeker finds the target inside searchRadius of the datum (bombs and shells have no
    /// datalink). The three seeker types declare their own Initialize and the same fields.
    /// </summary>
    [HarmonyPatch]
    public static class OpticalSeekers_Initialize_Patch
    {
        [HarmonyTargetMethods]
        public static IEnumerable<MethodBase> TargetMethods()
        {
            Type[] args = new Type[] { typeof(Unit), typeof(GlobalPosition) };
            yield return AccessTools.Method(typeof(OpticalSeeker), "Initialize", args);
            yield return AccessTools.Method(typeof(OpticalSeekerBomb), "Initialize", args);
            yield return AccessTools.Method(typeof(OpticalSeekerShell), "Initialize", args);
        }

        [HarmonyPostfix]
        public static void Postfix(
            MissileSeeker __instance,
            MethodBase __originalMethod,
            Unit target,
            Missile ___missile,
            ref GlobalPosition ___knownPos,
            ref Vector3 ___knownVel,
            ref float ___searchRadius)
        {
            try
            {
                if (!SeekerDispersionHelper.ShouldRun(__instance, ___missile)) return;

                OpticalSeekerConfig.ApplySearchRadius(ref ___searchRadius);
                SeekerDispersionHelper.ApplyLaunchDatum(___missile, target, ref ___knownPos, ref ___knownVel);
            }
            catch (Exception ex)
            {
                PatchErrorLog.Once(__originalMethod.DeclaringType.Name + "_Initialize_Patch", ex);
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
                if (!SeekerDispersionHelper.ShouldRun(__instance, ___missile)) return;
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
                if (!SeekerDispersionHelper.ShouldRun(__instance, ___missile) || !___hasVisual) return;

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
    /// Optional activation range of the bomb and shell seekers (OpticalSeekerActivationRange_m). 0 = vanilla.
    /// </summary>
    [HarmonyPatch]
    public static class OpticalSeekers_TrackVisual_Patch
    {
        [HarmonyTargetMethods]
        public static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(OpticalSeekerBomb), "TrackVisual");
            yield return AccessTools.Method(typeof(OpticalSeekerShell), "TrackVisual");
        }

        [HarmonyPostfix]
        public static void Postfix(MethodBase __originalMethod, ref bool __result, Missile ___missile, Unit ___targetUnit)
        {
            try
            {
                if (MunitionCatalog.OpticalActivationRange <= 0.0f) return;
                if (!CnoEvilConfig.IsModActive || !__result) return;
                if (!OpticalSeekerConfig.InActivationRange(___missile, ___targetUnit)) __result = false;
            }
            catch (Exception ex)
            {
                PatchErrorLog.Once(__originalMethod.DeclaringType.Name + "_TrackVisual_Patch", ex);
            }
        }
    }
}
