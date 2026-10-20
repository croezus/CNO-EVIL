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
            if (!CnoEvilConfig.IsModActive || __instance == null || aircraft == null) return;

            try
            {
                FlightHUDEWIndicator indicator = __instance.gameObject.GetComponent<FlightHUDEWIndicator>();
                if (CnoEvilConfig.EnableHUDNotchIndicator != null && CnoEvilConfig.EnableHUDNotchIndicator.Value)
                {
                    if (indicator == null)
                    {
                        indicator = __instance.gameObject.AddComponent<FlightHUDEWIndicator>();
                    }
                    indicator.Initialize(__instance, aircraft);
                }
                else if (indicator != null)
                {
                    // Left over from a previous aircraft while the indicator was enabled
                    UnityEngine.Object.Destroy(indicator);
                }

                if (CnoEvilConfig.EnableMissileTrajectoryHUD == null || CnoEvilConfig.EnableMissileTrajectoryHUD.Value)
                {
                    MissileTrajectoryHUDVisualizer trajVis = __instance.gameObject.GetComponent<MissileTrajectoryHUDVisualizer>();
                    if (trajVis == null)
                    {
                        trajVis = __instance.gameObject.AddComponent<MissileTrajectoryHUDVisualizer>();
                    }
                }

                if (CnoEvilConfig.EnableHMDThermalWindow == null || CnoEvilConfig.EnableHMDThermalWindow.Value)
                {
                    HMDThermalWindow thermal = __instance.gameObject.GetComponent<HMDThermalWindow>();
                    if (thermal == null)
                    {
                        thermal = __instance.gameObject.AddComponent<HMDThermalWindow>();
                    }
                    thermal.Initialize(__instance, aircraft);
                }
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
