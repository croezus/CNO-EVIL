using System;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;
using CnoEvil.Core;
using CnoEvil.Config;
using CnoEvil.Components;

namespace CnoEvil.Patches
{
    [HarmonyPatch(typeof(TacScreen))]
    public static class TacScreenPatches
    {
        private static bool IsAdvancedCanvasActive()
        {
            if (!CnoEvilConfig.IsModActive) return false;
            return CnoEvilConfig.TacScreenDisplayPreset.Value == CnoEvilConfig.TacScreenPreset.AdvancedCanvas;
        }

        /// <summary>
        /// AdvancedCanvas or PortalMFD: the mod draws the TacScreen, so the vanilla radar icons and pings stay off.
        /// </summary>
        private static bool IsVanillaRadarSuppressed()
        {
            return IsAdvancedCanvasActive() || TacScreenPortalMFD.IsPresetActive();
        }

        [HarmonyPatch("Initialize")]
        [HarmonyPostfix]
        public static void Initialize_Postfix(TacScreen __instance, Aircraft aircraft, Cockpit cockpit)
        {
            if (aircraft == null) return;

            try
            {
                // Applies later preset changes in flight
                TacScreenPresetWatcher watcher = __instance.gameObject.GetComponent<TacScreenPresetWatcher>();
                if (watcher == null) watcher = __instance.gameObject.AddComponent<TacScreenPresetWatcher>();
                watcher.Initialize(__instance, aircraft, cockpit);
            }
            catch (Exception ex)
            {
                if (CnoEvilPlugin.Log != null) CnoEvilPlugin.Log.LogError(string.Format("Error in TacScreen.Initialize_Postfix: {0}", ex));
            }

            ApplyPreset(__instance, aircraft, cockpit);
        }

        /// <summary>
        /// Adds the display component of the current preset to the TacScreen and removes the others.
        /// Called at spawn and by TacScreenPresetWatcher when the preset changes in flight.
        /// </summary>
        public static void ApplyPreset(TacScreen __instance, Aircraft aircraft, Cockpit cockpit)
        {
            if (!CnoEvilConfig.IsModActive || __instance == null || aircraft == null) return;

            try
            {
                if (IsAdvancedCanvasActive())
                {
                    TacScreenTacticalDisplay tacticalDisplay = __instance.gameObject.GetComponent<TacScreenTacticalDisplay>();
                    if (tacticalDisplay == null)
                    {
                        tacticalDisplay = __instance.gameObject.AddComponent<TacScreenTacticalDisplay>();
                    }
                    tacticalDisplay.Initialize(__instance, aircraft);
                }
                else
                {
                    TacScreenTacticalDisplay tacticalDisplay = __instance.gameObject.GetComponent<TacScreenTacticalDisplay>();
                    if (tacticalDisplay != null)
                    {
                        UnityEngine.Object.Destroy(tacticalDisplay);
                    }
                }

                TacScreenPortalMFD portal = __instance.gameObject.GetComponent<TacScreenPortalMFD>();
                if (TacScreenPortalMFD.IsPresetActive())
                {
                    if (portal == null) portal = __instance.gameObject.AddComponent<TacScreenPortalMFD>();
                    portal.Initialize(__instance, aircraft, cockpit);
                }
                else if (portal != null)
                {
                    portal.Teardown();
                    UnityEngine.Object.Destroy(portal);
                }
            }
            catch (Exception ex)
            {
                if (CnoEvilPlugin.Log != null) CnoEvilPlugin.Log.LogError(string.Format("Error in TacScreenPatches.ApplyPreset: {0}", ex));
            }
        }

        [HarmonyPatch("TacScreen_OnRadarWarning")]
        [HarmonyPrefix]
        public static bool TacScreen_OnRadarWarning_Prefix(Aircraft.OnRadarWarning source, Aircraft ___aircraft)
        {
            if (!CnoEvilConfig.IsModActive)
            {
                return true; // Let vanilla run if mod is completely disabled
            }

            Aircraft aircraft = ___aircraft;
            if (source.emitter == null || aircraft == null || aircraft.disabled) return false;

            // The host registers every ping in PhysRWRReceiver with its physical signal (RadarPatches, SeekerPatches);
            // a mirror client gets the pings from the host RWR feed (RwrFeedSync). Only the vanilla drawing is decided here.
            // AdvancedCanvas: TacScreenTacticalDisplay draws the strobes; PortalMFD: the RWR page; NativeEnhanced: vanilla ping prefab
            return !IsVanillaRadarSuppressed();
        }

        [HarmonyPatch("TacScreen_OnRadarWarning")]
        [HarmonyPostfix]
        public static void TacScreen_OnRadarWarning_Postfix(Aircraft.OnRadarWarning source, Transform ___iconLayer)
        {
            if (!CnoEvilConfig.IsModActive || IsVanillaRadarSuppressed()) return;
            if (source.emitter == null) return;

            try
            {
                Transform iconLayer = ___iconLayer;
                if (iconLayer != null && iconLayer.childCount > 0)
                {
                    Transform newestItem = iconLayer.GetChild(iconLayer.childCount - 1);
                    if (newestItem != null)
                    {
                        Image img = newestItem.GetComponent<Image>();
                        if (img != null)
                        {
                            bool isMissile = (source.emitter is Missile);
                            Color threatColor;
                            if (isMissile)
                            {
                                threatColor = new Color(1.0f, 0.15f, 0.10f, 0.95f); // Red (Terminal Pitbull)
                            }
                            else if (source.isTarget)
                            {
                                threatColor = new Color(1.0f, 0.55f, 0.05f, 0.90f); // Orange (Track/Lock)
                            }
                            else
                            {
                                threatColor = new Color(1.0f, 0.88f, 0.20f, 0.85f); // Yellow (Search)
                            }
                            img.color = threatColor;
                        }
                    }
                }
            }
            catch { }
        }

        [HarmonyPatch("UpdateMissileWarning")]
        [HarmonyPrefix]
        public static bool UpdateMissileWarning_Prefix()
        {
            if (!CnoEvilConfig.IsModActive)
            {
                return true;
            }

            if (IsVanillaRadarSuppressed())
            {
                // Suppress vanilla's exact-range missile icon placement on the radar scope.
                // Active missile guidance and seeker azimuths are handled physically by TacScreenTacticalDisplay.
                return false;
            }

            return true;
        }

        [HarmonyPatch("TacScreen_OnRadarScan")]
        [HarmonyPrefix]
        public static bool TacScreen_OnRadarScan_Prefix(Aircraft ___aircraft, ref Vector3 ___headingAtScan)
        {
            if (!CnoEvilConfig.IsModActive)
            {
                return true;
            }

            // Maintain headingAtScan for radar line sweep animation
            if (___aircraft != null)
            {
                ___headingAtScan = ___aircraft.transform.forward;
            }

            if (IsVanillaRadarSuppressed())
            {
                // Suppress vanilla's generic dot iconPrefab - TacScreenTacticalDisplay handles
                // high-fidelity F-15/F-16 symbology with velocity vectors.
                return false;
            }

            // In NativeEnhanced mode, let vanilla draw native iconPrefab for physically detected contacts
            return true;
        }

        [HarmonyPatch("TacScreen_OnOpticalScan")]
        [HarmonyPrefix]
        public static bool TacScreen_OnOpticalScan_Prefix(Aircraft ___aircraft, ref Vector3 ___headingAtScan)
        {
            if (!CnoEvilConfig.IsModActive)
            {
                return true;
            }

            if (___aircraft != null)
            {
                ___headingAtScan = ___aircraft.transform.forward;
            }

            if (IsVanillaRadarSuppressed())
            {
                return false;
            }

            return true;
        }
    }
}
