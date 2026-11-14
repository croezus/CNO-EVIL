using System;
using HarmonyLib;
using NuclearOption.Networking;
using UnityEngine;
using UnityEngine.UI;
using CnoEvil.Components;
using CnoEvil.Core;

namespace CnoEvil.Patches
{
    /// <summary>
    /// Loadout menu: countermeasure sliders under the fuel slider. Runs also while the mod is off (the rows stay hidden).
    /// </summary>
    [HarmonyPatch(typeof(LoadoutSelector), "AssignAircraft")]
    public static class LoadoutSelector_AssignAircraft_Patch
    {
        private static readonly Func<LoadoutSelector, Slider> f_fuelLevel =
            FastReflection.CreateFieldGetter<LoadoutSelector, Slider>("fuelLevel");

        [HarmonyPostfix]
        public static void Postfix(LoadoutSelector __instance, Aircraft aircraft)
        {
            try
            {
                if (f_fuelLevel == null) return;
                CountermeasureSliders sliders = CountermeasureSliders.Attach(__instance, f_fuelLevel(__instance));
                if (sliders != null) sliders.Show(aircraft);
            }
            catch (Exception ex)
            {
                PatchErrorLog.Once("LoadoutSelector_AssignAircraft_Patch", ex);
            }
        }
    }

    /// <summary>
    /// FLY: the slider choice goes to the host before the vanilla spawn request (CountermeasureSync).
    /// </summary>
    [HarmonyPatch(typeof(AircraftSelectionMenu), "FlyAircraft")]
    public static class AircraftSelectionMenu_FlyAircraft_Patch
    {
        private static readonly Func<AircraftSelectionMenu, LoadoutSelector> f_loadoutSelector =
            FastReflection.CreateFieldGetter<AircraftSelectionMenu, LoadoutSelector>("loadoutSelector");

        [HarmonyPrefix]
        public static void Prefix(AircraftSelectionMenu __instance)
        {
            try
            {
                LoadoutSelector selector = (f_loadoutSelector != null) ? f_loadoutSelector(__instance) : null;
                CountermeasureSliders sliders = (selector != null) ? selector.GetComponent<CountermeasureSliders>() : null;
                if (sliders != null) sliders.Submit();
            }
            catch (Exception ex)
            {
                PatchErrorLog.Once("AircraftSelectionMenu_FlyAircraft_Patch", ex);
            }
        }
    }

    /// <summary>
    /// Host: the countermeasure choice of the player on the new aircraft, sent to the clients.
    /// </summary>
    [HarmonyPatch(typeof(Spawner), "SpawnAircraft")]
    public static class Spawner_SpawnAircraft_Patch
    {
        [HarmonyPostfix]
        public static void Postfix(Player player, Aircraft __result)
        {
            try
            {
                CountermeasureSync.OnAircraftSpawned(player, __result);
            }
            catch (Exception ex)
            {
                PatchErrorLog.Once("Spawner_SpawnAircraft_Patch", ex);
            }
        }
    }
}
