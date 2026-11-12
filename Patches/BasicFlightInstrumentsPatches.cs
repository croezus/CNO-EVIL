using System;
using HarmonyLib;
using UnityEngine.UI;
using TMPro;
using CnoEvil.Core;
using CnoEvil.Components;

namespace CnoEvil.Patches
{
    /// <summary>
    /// Attaches the attitude display to each cockpit flight instrument panel. Attach only: the component
    /// shows itself while the mod is active and restores vanilla when it goes off.
    /// </summary>
    [HarmonyPatch(typeof(BasicFlightInstruments), "Initialize")]
    public static class BasicFlightInstruments_Initialize_Patch
    {
        [HarmonyPostfix]
        public static void Postfix(BasicFlightInstruments __instance, Aircraft aircraft,
            TextMeshProUGUI ___headingDisplay, TextMeshProUGUI ___airspeedDisplay, TextMeshProUGUI ___altitudeDisplay,
            TextMeshProUGUI ___climbRateDisplay, Image ___horizonDisplay, Image ___verticalSpeedIndicator, Image ___AoAIndicator)
        {
            if (aircraft == null) return;
            try
            {
                CockpitAttitudeDisplay display = __instance.GetComponent<CockpitAttitudeDisplay>();
                if (display == null) display = __instance.gameObject.AddComponent<CockpitAttitudeDisplay>();
                display.Initialize(aircraft, ___headingDisplay, ___airspeedDisplay, ___altitudeDisplay,
                    ___climbRateDisplay, ___horizonDisplay, ___verticalSpeedIndicator, ___AoAIndicator);
            }
            catch (Exception ex)
            {
                PatchErrorLog.Once("BasicFlightInstruments_Initialize_Patch", ex);
            }
        }
    }
}
