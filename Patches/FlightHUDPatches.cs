using System;
using HarmonyLib;
using UnityEngine;
using CnoEvil.Config;
using CnoEvil.Components;

namespace CnoEvil.Patches
{
    [HarmonyPatch(typeof(FlightHud))]
    public static class FlightHUDPatches
    {
        [HarmonyPatch("SetAircraft")]
        [HarmonyPostfix]
        public static void SetAircraft_Postfix(FlightHud __instance, Aircraft aircraft)
        {
            if (__instance == null || aircraft == null) return;

            // The components are always attached and check their own setting (and IsModActive) every frame,
            // so a setting turned on in flight works without a respawn
            try
            {
                FlightHUDEWIndicator indicator = __instance.gameObject.GetComponent<FlightHUDEWIndicator>();
                if (indicator == null) indicator = __instance.gameObject.AddComponent<FlightHUDEWIndicator>();
                indicator.Initialize(__instance, aircraft);

                if (__instance.gameObject.GetComponent<MissileTrajectoryHUDVisualizer>() == null)
                {
                    __instance.gameObject.AddComponent<MissileTrajectoryHUDVisualizer>();
                }

                HMDThermalWindow thermal = __instance.gameObject.GetComponent<HMDThermalWindow>();
                if (thermal == null) thermal = __instance.gameObject.AddComponent<HMDThermalWindow>();
                thermal.Initialize(__instance, aircraft);
            }
            catch (Exception ex)
            {
                if (CnoEvilPlugin.Log != null)
                {
                    CnoEvilPlugin.Log.LogError(string.Format("Error attaching components in FlightHud.SetAircraft: {0}", ex));
                }
            }
        }
    }
}
