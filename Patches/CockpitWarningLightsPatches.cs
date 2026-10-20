using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using CnoEvil.Config;
using CnoEvil.Core;
using CnoEvil.Components;

namespace CnoEvil.Patches
{
    /// <summary>
    /// Adds RWR missile-guidance warnings (ARH seeker or SARH illuminator in guidance mode) to the cockpit missile
    /// warning lights. Vanilla flashes them for its known missiles and switches them off (and disables itself)
    /// otherwise; this flashes in the same phase when only the RWR has a missile guidance threat.
    /// </summary>
    [HarmonyPatch(typeof(CockpitWarningLights))]
    public static class CockpitWarningLightsPatches
    {
        [HarmonyPatch("Update")]
        [HarmonyPostfix]
        public static void Update_Postfix(CockpitWarningLights __instance, Aircraft ___aircraft, Renderer[] ___lightRenderers, List<Missile> ___knownMissiles)
        {
            if (!CnoEvilConfig.IsModActive) return;
            if (___knownMissiles != null && ___knownMissiles.Count > 0) return; // Vanilla is already flashing

            Aircraft aircraft = ___aircraft;
            if (aircraft == null || aircraft.disabled || ___lightRenderers == null) return;

            PhysRWRReceiver rwr = PhysRWRReceiver.Get(aircraft);
            if (rwr == null || rwr.GetHighestThreatState() != RwrThreatState.MissileGuidance) return;

            // Same phase as vanilla CockpitWarningLights.Update
            bool flash = Mathf.Sin(Time.timeSinceLevelLoad * 20.0f) > 0.0f;
            for (int i = 0; i < ___lightRenderers.Length; i++)
            {
                if (___lightRenderers[i] != null) ___lightRenderers[i].enabled = flash;
            }
            // Keep Update running so vanilla switches the lights off once the threat is gone
            __instance.enabled = true;
        }
    }
}
