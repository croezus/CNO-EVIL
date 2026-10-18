using System;
using HarmonyLib;
using CnoEvil.Core;
using CnoEvil.Config;

namespace CnoEvil.Patches
{
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
