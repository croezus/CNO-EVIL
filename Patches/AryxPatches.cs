using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using CnoEvil.Core;

namespace CnoEvil.Patches
{
    // Patches on Aryx Weapons Pack types (AryxWeaponsCompat). Prepare skips each class when the pack is not installed.

    /// <summary>
    /// ECS-16 / ECS-32 flare pod attached: the pod added its flares to the ejector (ammo and maxAmmo). The station ammo
    /// is recounted so the HUD and the countermeasure programs see the new total, and the pod is registered as a store
    /// without a WeaponStation (AspectRCS). Attach patch: runs also while the mod is off.
    /// </summary>
    [HarmonyPatch]
    public static class AryxExternalFlareLauncher_AttachToHardpoint_Patch
    {
        public static bool Prepare()
        {
            return AryxWeaponsCompat.FlareLauncherType != null;
        }

        public static MethodBase TargetMethod()
        {
            return AccessTools.Method(AryxWeaponsCompat.FlareLauncherType, "AttachToHardpoint",
                new Type[] { typeof(Aircraft), typeof(Hardpoint), typeof(WeaponMount) });
        }

        [HarmonyPostfix]
        public static void Postfix(Weapon __instance, Aircraft aircraft)
        {
            try
            {
                AryxWeaponsCompat.RegisterAddon(__instance, aircraft);
                if (aircraft != null) CountermeasureLoadout.RecountStations(aircraft);
            }
            catch (Exception ex)
            {
                PatchErrorLog.Once("AryxExternalFlareLauncher_AttachToHardpoint_Patch", ex);
            }
        }
    }

    /// <summary>
    /// Flare pod removed (destroyed, or attached again): the pod took its flares back. The aircraft field is cleared
    /// by the method, so the prefix keeps it for the recount.
    /// </summary>
    [HarmonyPatch]
    public static class AryxExternalFlareLauncher_RemoveContribution_Patch
    {
        public static bool Prepare()
        {
            return AryxWeaponsCompat.FlareLauncherType != null;
        }

        public static MethodBase TargetMethod()
        {
            return AccessTools.Method(AryxWeaponsCompat.FlareLauncherType, "RemoveContribution", Type.EmptyTypes);
        }

        [HarmonyPrefix]
        public static void Prefix(Aircraft ___aircraft, out Aircraft __state)
        {
            __state = ___aircraft;
        }

        [HarmonyPostfix]
        public static void Postfix(Weapon __instance, Aircraft __state)
        {
            try
            {
                AryxWeaponsCompat.UnregisterAddon(__instance);
                if (__state != null && !__state.disabled) CountermeasureLoadout.RecountStations(__state);
            }
            catch (Exception ex)
            {
                PatchErrorLog.Once("AryxExternalFlareLauncher_RemoveContribution_Patch", ex);
            }
        }
    }

    /// <summary>
    /// ETS-15 targeting pod attached: registered as a store without a WeaponStation (AspectRCS).
    /// </summary>
    [HarmonyPatch]
    public static class AryxExternalTargetingPodAddon_AttachToHardpoint_Patch
    {
        public static bool Prepare()
        {
            return AryxWeaponsCompat.TargetingPodType != null;
        }

        public static MethodBase TargetMethod()
        {
            return AccessTools.Method(AryxWeaponsCompat.TargetingPodType, "AttachToHardpoint",
                new Type[] { typeof(Aircraft), typeof(Hardpoint), typeof(WeaponMount) });
        }

        [HarmonyPostfix]
        public static void Postfix(Weapon __instance, Aircraft aircraft)
        {
            try
            {
                AryxWeaponsCompat.RegisterAddon(__instance, aircraft);
            }
            catch (Exception ex)
            {
                PatchErrorLog.Once("AryxExternalTargetingPodAddon_AttachToHardpoint_Patch", ex);
            }
        }
    }

    [HarmonyPatch]
    public static class AryxExternalTargetingPodAddon_RemoveContribution_Patch
    {
        public static bool Prepare()
        {
            return AryxWeaponsCompat.TargetingPodType != null;
        }

        public static MethodBase TargetMethod()
        {
            return AccessTools.Method(AryxWeaponsCompat.TargetingPodType, "RemoveContribution", Type.EmptyTypes);
        }

        [HarmonyPostfix]
        public static void Postfix(Weapon __instance)
        {
            try
            {
                AryxWeaponsCompat.UnregisterAddon(__instance);
            }
            catch (Exception ex)
            {
                PatchErrorLog.Once("AryxExternalTargetingPodAddon_RemoveContribution_Patch", ex);
            }
        }
    }

    /// <summary>
    /// HSM-750 Starfall / HSM-N Sunfall (AryxInertialSeeker) launch datum: the faction track + dispersion, as for
    /// guided artillery (InertialSeekerShell_Initialize_Patch). The datalink leash of the seeker starts at this datum.
    /// </summary>
    [HarmonyPatch]
    public static class AryxInertialSeeker_Initialize_Patch
    {
        public static bool Prepare()
        {
            return AryxWeaponsCompat.InertialSeekerType != null;
        }

        public static MethodBase TargetMethod()
        {
            return AccessTools.Method(AryxWeaponsCompat.InertialSeekerType, "Initialize",
                new Type[] { typeof(Unit), typeof(GlobalPosition) });
        }

        [HarmonyPostfix]
        public static void Postfix(
            MissileSeeker __instance,
            Unit target,
            Missile ___missile,
            ref GlobalPosition ___knownPos,
            ref GlobalPosition ___datalinkOrigin)
        {
            try
            {
                if (!SeekerDispersionHelper.ShouldRun(__instance, ___missile)) return;

                Unit t = (target != null && !target.disabled) ? target : SeekerDispersionHelper.GetTargetForMissile(___missile);
                Vector3 vel = Vector3.zero;
                SeekerDispersionHelper.ApplyLaunchDatum(___missile, t, ref ___knownPos, ref vel);
                ___datalinkOrigin = ___knownPos;
            }
            catch (Exception ex)
            {
                PatchErrorLog.Once("AryxInertialSeeker_Initialize_Patch", ex);
            }
        }
    }

    /// <summary>
    /// Starfall datalink update: vanilla knowledge (FactionHQ.GetKnownPosition) moves the datum to the true target.
    /// For a target the mod guides, the datum is the faction track + dispersion, or stays at the last track datum
    /// when the faction lost it (SeekerDispersionHelper.CorrectDatum).
    /// </summary>
    [HarmonyPatch]
    public static class AryxInertialSeeker_UpdateDatalink_Patch
    {
        public static bool Prepare()
        {
            return AryxWeaponsCompat.InertialSeekerType != null;
        }

        public static MethodBase TargetMethod()
        {
            return AccessTools.Method(AryxWeaponsCompat.InertialSeekerType, "UpdateDatalink", Type.EmptyTypes);
        }

        [HarmonyPrefix]
        public static void Prefix(GlobalPosition ___knownPos, out GlobalPosition __state)
        {
            __state = ___knownPos;
        }

        [HarmonyPostfix]
        public static void Postfix(
            MissileSeeker __instance,
            bool ___useDatalink,
            Missile ___missile,
            Unit ___targetUnit,
            ref GlobalPosition ___knownPos,
            GlobalPosition __state)
        {
            try
            {
                if (!SeekerDispersionHelper.ShouldRun(__instance, ___missile) || !___useDatalink) return;
                if (___knownPos == __state) return; // no vanilla update this call (interval, no track, leash committed)

                Vector3 vel = Vector3.zero;
                SeekerDispersionHelper.CorrectDatum(___missile, ___targetUnit, ref ___knownPos, ref vel, __state, true);
            }
            catch (Exception ex)
            {
                PatchErrorLog.Once("AryxInertialSeeker_UpdateDatalink_Patch", ex);
            }
        }
    }
}
