using System;
using HarmonyLib;
using TMPro;
using CnoEvil.Core;
using CnoEvil.Components;

namespace CnoEvil.Patches
{
    /// <summary>
    /// Attaches the detection status line to the vanilla Altitude HUD app (radar and absolute altitude)
    /// and gives it the aircraft of the app. The line refreshes itself after the vanilla refresh (LateUpdate).
    /// Attach patch: runs while the mod is off; the line hides itself then.
    /// </summary>
    [HarmonyPatch(typeof(Altitude), "Initialize")]
    public static class Altitude_Initialize_Patch
    {
        [HarmonyPostfix]
        public static void Postfix(Altitude __instance, TextMeshProUGUI ___radarAlt, TextMeshProUGUI ___absAlt, Aircraft ___aircraft)
        {
            try
            {
                if (__instance == null || ___absAlt == null) return;
                DetectionStatusLine line = __instance.GetComponent<DetectionStatusLine>();
                if (line == null)
                {
                    line = __instance.gameObject.AddComponent<DetectionStatusLine>();
                    line.Initialize(___radarAlt, ___absAlt);
                }
                // The head-mounted display initializes its apps again when the player aircraft changes
                line.SetAircraft(___aircraft);
            }
            catch (Exception ex)
            {
                PatchErrorLog.Once("Altitude_Initialize_Patch.Postfix", ex);
            }
        }
    }
}
