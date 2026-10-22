using System;
using HarmonyLib;
using UnityEngine;
using CnoEvil.Core;
using CnoEvil.Config;

namespace CnoEvil.Patches
{
    /// <summary>
    /// Cruise missiles (AGM-99, AShM-300, ALM-C450, ALND-4): launch datum = faction track + dispersion.
    /// Boost direction, waypoints, terrain following and the cruise altitude stay vanilla.
    /// CruiseTerminalRange_m / CruiseTerminalSearchRadius_m > 0 replace the vanilla values.
    /// </summary>
    [HarmonyPatch(typeof(OpticalSeekerCruiseMissile), "Initialize", new Type[] { typeof(Unit), typeof(GlobalPosition) })]
    public static class OpticalSeekerCruiseMissile_Initialize_Patch
    {
        [HarmonyPostfix]
        public static void Postfix(
            OpticalSeekerCruiseMissile __instance,
            Unit target,
            Missile ___missile,
            ref GlobalPosition ___knownPos,
            ref Vector3 ___knownVel,
            ref float ___terminalRange,
            ref float ___terminalSearchRadius)
        {
            try
            {
                if (!CnoEvilConfig.IsModActive) return;
                if (__instance == null || ___missile == null) return;

                float range = (CnoEvilConfig.CruiseTerminalRange_m != null) ? CnoEvilConfig.CruiseTerminalRange_m.Value : 0.0f;
                if (range > 0.0f) ___terminalRange = range;
                float radius = (CnoEvilConfig.CruiseTerminalSearchRadius_m != null) ? CnoEvilConfig.CruiseTerminalSearchRadius_m.Value : 0.0f;
                if (radius > 0.0f) ___terminalSearchRadius = radius;

                SeekerDispersionHelper.ApplyLaunchDatum(___missile, target, ref ___knownPos, ref ___knownVel);
            }
            catch (Exception ex)
            {
                PatchErrorLog.Once("OpticalSeekerCruiseMissile_Initialize_Patch", ex);
            }
        }
    }

    /// <summary>
    /// Vanilla UpdateTargetParameters copies the faction's vanilla knowledge into the datum every frame. For a
    /// target the mod guides, the datum is the faction track + dispersion, or stays when the faction lost it.
    /// Only the datum changes; vanilla PreTerminalMode flies to it through TerrainWaypoint (cruise altitude).
    /// </summary>
    [HarmonyPatch(typeof(OpticalSeekerCruiseMissile), "UpdateTargetParameters")]
    public static class OpticalSeekerCruiseMissile_UpdateTargetParameters_Patch
    {
        [HarmonyPrefix]
        public static void Prefix(GlobalPosition ___knownPos, out GlobalPosition __state)
        {
            __state = ___knownPos;
        }

        [HarmonyPostfix]
        public static void Postfix(
            OpticalSeekerCruiseMissile __instance,
            Unit ___targetUnit,
            Missile ___missile,
            bool ___terminalMode,
            ref GlobalPosition ___knownPos,
            ref Vector3 ___knownVel,
            GlobalPosition __state)
        {
            try
            {
                if (!CnoEvilConfig.IsModActive) return;
                if (__instance == null || ___missile == null) return;
                if (___terminalMode)
                {
                    SeekerDispersionHelper.MarkSeekerAcquired(___missile);
                    return;
                }
                SeekerDispersionHelper.CorrectDatum(___missile, ___targetUnit, ref ___knownPos, ref ___knownVel, __state, true);
            }
            catch (Exception ex)
            {
                PatchErrorLog.Once("OpticalSeekerCruiseMissile_UpdateTargetParameters_Patch", ex);
            }
        }
    }

    /// <summary>
    /// Search basket: vanilla starts the terminal attack within terminalRange of the datum whenever the target is
    /// alive. For a target the mod guides, the attack starts only when the target is inside terminalSearchRadius of
    /// the datum; else terminalRange is 0 for this call, the missile flies on to the datum and the vanilla
    /// MissedTarget check detonates it there.
    /// </summary>
    [HarmonyPatch(typeof(OpticalSeekerCruiseMissile), "PreTerminalMode")]
    public static class OpticalSeekerCruiseMissile_PreTerminalMode_Patch
    {
        [HarmonyPrefix]
        public static void Prefix(
            OpticalSeekerCruiseMissile __instance,
            Missile ___missile,
            Unit ___targetUnit,
            GlobalPosition ___knownPos,
            ref float ___terminalRange,
            float ___terminalSearchRadius,
            out float __state)
        {
            __state = -1.0f;
            try
            {
                if (!CnoEvilConfig.IsModActive) return;
                if (__instance == null || ___missile == null) return;
                Unit target = ___targetUnit;
                if (target == null || target.disabled) return;
                if (!SeekerDispersionHelper.IsModGuided(___missile, target)) return;

                if (!FastMath.InRange(target.GlobalPosition(), ___knownPos, ___terminalSearchRadius + target.maxRadius))
                {
                    __state = ___terminalRange;
                    ___terminalRange = 0.0f;
                }
            }
            catch (Exception ex)
            {
                PatchErrorLog.Once("OpticalSeekerCruiseMissile_PreTerminalMode_Patch", ex);
            }
        }

        [HarmonyPostfix]
        public static void Postfix(ref float ___terminalRange, float __state)
        {
            if (__state >= 0.0f) ___terminalRange = __state;
        }
    }
}
