using System;
using System.Collections.Generic;
using HarmonyLib;
using TMPro;
using CnoEvil.Core;
using CnoEvil.Components;

namespace CnoEvil.Patches
{
    /// <summary>
    /// Attaches the SNR bar to the vanilla G HUD app (G and max-G readouts).
    /// Attach patch: runs while the mod is off; the bar hides itself then.
    /// </summary>
    [HarmonyPatch(typeof(GIndicators), "Initialize")]
    public static class GIndicators_Initialize_Patch
    {
        [HarmonyPostfix]
        public static void Postfix(GIndicators __instance, TextMeshProUGUI ___gMaxForceLabel)
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
                GIndicatorsHUDPatches.Register(__instance, bar);
            }
            catch (Exception ex)
            {
                PatchErrorLog.Once("GIndicators_Initialize_Patch.Postfix", ex);
            }
        }
    }

    /// <summary>
    /// Updates the SNR bar with the vanilla G readouts.
    /// </summary>
    [HarmonyPatch(typeof(GIndicators), "Refresh")]
    public static class GIndicators_Refresh_Patch
    {
        [HarmonyPostfix]
        public static void Postfix(GIndicators __instance, Aircraft ___aircraft)
        {
            try
            {
                HudSnrIndicator bar = GIndicatorsHUDPatches.Get(__instance);
                if (bar != null) bar.Refresh(___aircraft);
            }
            catch (Exception ex)
            {
                PatchErrorLog.Once("GIndicators_Refresh_Patch.Postfix", ex);
            }
        }
    }

    /// <summary>
    /// Keeps the bar at the vanilla HUD font size.
    /// </summary>
    [HarmonyPatch(typeof(GIndicators), "RefreshSettings")]
    public static class GIndicators_RefreshSettings_Patch
    {
        [HarmonyPostfix]
        public static void Postfix(GIndicators __instance)
        {
            try
            {
                HudSnrIndicator bar = GIndicatorsHUDPatches.Get(__instance);
                if (bar != null) bar.SyncSize();
            }
            catch (Exception ex)
            {
                PatchErrorLog.Once("GIndicators_RefreshSettings_Patch.Postfix", ex);
            }
        }
    }

    public static class GIndicatorsHUDPatches
    {
        // Refresh runs every frame: look the bar up here instead of GetComponent
        private static readonly Dictionary<GIndicators, HudSnrIndicator> s_bars = new Dictionary<GIndicators, HudSnrIndicator>();

        internal static void Register(GIndicators app, HudSnrIndicator bar)
        {
            if (app != null && bar != null) s_bars[app] = bar;
        }

        internal static void Unregister(GIndicators app)
        {
            if (app != null) s_bars.Remove(app);
        }

        internal static HudSnrIndicator Get(GIndicators app)
        {
            HudSnrIndicator bar;
            return (app != null && s_bars.TryGetValue(app, out bar)) ? bar : null;
        }

        public static void ClearAll()
        {
            s_bars.Clear();
        }
    }
}
