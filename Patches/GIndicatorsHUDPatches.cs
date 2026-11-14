using System;
using HarmonyLib;
using TMPro;
using CnoEvil.Core;
using CnoEvil.Components;

namespace CnoEvil.Patches
{
    /// <summary>
    /// Attaches the SNR bar to the vanilla G HUD app (G and max-G readouts) and gives it the aircraft of the app.
    /// The bar refreshes itself after the vanilla refresh (LateUpdate).
    /// Attach patch: runs while the mod is off; the bar hides itself then.
    /// </summary>
    [HarmonyPatch(typeof(GIndicators), "Initialize")]
    public static class GIndicators_Initialize_Patch
    {
        [HarmonyPostfix]
        public static void Postfix(GIndicators __instance, TextMeshProUGUI ___gMaxForceLabel, Aircraft ___aircraft)
        {
            try
            {
                if (__instance == null || ___gMaxForceLabel == null) return;
                HudSnrIndicator bar = __instance.GetComponent<HudSnrIndicator>();
                if (bar == null)
                {
                    bar = __instance.gameObject.AddComponent<HudSnrIndicator>();
                    bar.Initialize(__instance, ___gMaxForceLabel);
                }
                bar.SetAircraft(___aircraft);
            }
            catch (Exception ex)
            {
                PatchErrorLog.Once("GIndicators_Initialize_Patch.Postfix", ex);
            }
        }
    }
}
