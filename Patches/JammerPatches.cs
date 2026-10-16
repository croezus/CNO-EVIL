using System;
using HarmonyLib;
using UnityEngine;
using CnoEvil.Core;
using CnoEvil.Config;
using CnoEvil.Components;

namespace CnoEvil.Patches
{
    [HarmonyPatch(typeof(RadarJammer), "Fire")]
    public static class RadarJammer_Fire_Patch
    {
        [HarmonyPostfix]
        public static void Postfix(RadarJammer __instance)
        {
            if (!CnoEvilConfig.IsModActive) return;
            if (__instance == null || __instance.aircraft == null) return;

            PhysRadarTarget target = __instance.aircraft.GetComponent<PhysRadarTarget>();
            if (target != null)
            {
                target.HasActiveJammer = true;
                target.Jammer.PowerWatts = Mathf.Max(250.0f, __instance.GetMaxJammingIntensity() * 150.0f);
            }
        }
    }

    [HarmonyPatch(typeof(RadarJammer), "Update")]
    public static class RadarJammer_Update_Patch
    {
        [HarmonyPostfix]
        public static void Postfix(RadarJammer __instance)
        {
            if (!CnoEvilConfig.IsModActive) return;
            if (__instance == null || __instance.aircraft == null) return;

            if (!__instance.enabled)
            {
                PhysRadarTarget target = __instance.aircraft.GetComponent<PhysRadarTarget>();
                if (target != null)
                {
                    target.HasActiveJammer = false;
                }
            }
        }
    }

    /// <summary>
    /// Marks the JammingPod whose vanilla tick runs now, so Unit_Jam_Patch can spread its jam over the beam cone.
    /// </summary>
    [HarmonyPatch(typeof(JammingPod), "FixedUpdate")]
    public static class JammingPod_FixedUpdate_Patch
    {
        [HarmonyPrefix]
        public static void Prefix(JammingPod __instance)
        {
            try
            {
                if (!CnoEvilConfig.IsModActive) return;
                JammingConeProcessor.BeginPodTick(__instance);
            }
            catch (Exception ex)
            {
                PatchErrorLog.Once("JammingPod_FixedUpdate_Patch.Prefix", ex);
            }
        }

        [HarmonyPostfix]
        public static void Postfix()
        {
            JammingConeProcessor.EndPodTick();
        }
    }

    /// <summary>
    /// After the vanilla jam of the pod target, jams the hostile units inside the pod beam cone.
    /// </summary>
    [HarmonyPatch(typeof(Unit), "Jam")]
    public static class Unit_Jam_Patch
    {
        [HarmonyPostfix]
        public static void Postfix(Unit __instance, Unit.JamEventArgs args)
        {
            try
            {
                if (!CnoEvilConfig.IsModActive) return;
                JammingConeProcessor.OnUnitJammed(__instance, args);
            }
            catch (Exception ex)
            {
                JammingConeProcessor.EndPodTick();
                PatchErrorLog.Once("Unit_Jam_Patch", ex);
            }
        }
    }
}
