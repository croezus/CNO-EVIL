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
        private static readonly Func<ARHSeeker, bool> s_getArhHomeOnJam = FastReflection.CreateFieldGetter<ARHSeeker, bool>("homeOnJam");

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

        private const float ClientSignalFallback = 0.7f;
        private const float ClientSignalFloor = 0.1f;

        /// <summary>
        /// Signal strength 0..1 for a ping on a client. The warning RPC carries only the emitter, so the client runs
        /// the same calculation as the host (RadarSignalModel: aspect RCS, ECM, Doppler notch, clutter, SNR) with the
        /// emitter's radar and its own aircraft. A missile uses the ARH seeker model. The host sent the warning, so
        /// the ping is real: the strength does not go below ClientSignalFloor.
        /// </summary>
        private static float ClientSignalStrength(Aircraft.OnRadarWarning source, Aircraft aircraft, bool isMissile)
        {
            Vector3 ownPos = aircraft.transform.position;

            if (isMissile)
            {
                Missile missile = (Missile)source.emitter;
                Vector3 missilePos = missile.transform.position;
                Vector3 missileVel = missile.rb != null ? missile.rb.velocity : Vector3.zero;
                float missileDist = Mathf.Max(1.0f, Vector3.Distance(missilePos, ownPos));

                float dynamicRcs;
                float ecmIntensity;
                DopplerResult doppler = RadarSignalModel.EvaluateArhDoppler(missilePos, missileVel, aircraft, out dynamicRcs, out ecmIntensity);
                float echoPower;
                float snrDb = RadarSignalModel.EvaluateArhSnrDb(missile, ownPos, missileDist, dynamicRcs, ecmIntensity, ref doppler, out echoPower);
                // Same rule as the host ARH patch: HOJEnabled or the vanilla homeOnJam flag of the seeker
                ARHSeeker arh = SeekerDispersionHelper.GetSeeker(missile) as ARHSeeker;
                bool allowHoj = CnoEvilConfig.HOJEnabled.Value ||
                                (arh != null && s_getArhHomeOnJam != null && s_getArhHomeOnJam(arh));
                bool isHojHoming = allowHoj && ElectronicWarfare.EvaluateHOJGuidance(ecmIntensity, doppler.IsInsideClutterNotch, missileDist);
                float strength = RadarSignalModel.ArhSeekerStrength(snrDb, ecmIntensity, isHojHoming);
                return Mathf.Clamp(RadarSignalModel.SeekerPingSignal01(strength), ClientSignalFloor, 1.0f);
            }

            Radar radar = source.radar;
            if (radar == null) return ClientSignalFallback;
            Transform scanner = radar.GetScanPoint();
            Vector3 radarPos = scanner != null ? scanner.position : radar.transform.position;
            float dist = Mathf.Max(1.0f, Vector3.Distance(radarPos, ownPos));

            RadarSkinSignal sig = RadarSignalModel.EvaluateRadar(radar, aircraft, aircraft, radarPos, radar.GetVelocity(), dist);
            return Mathf.Clamp(RadarSignalModel.RadarPingSignal01(sig.SnrDb), ClientSignalFloor, 1.0f);
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

            // The server registers every ping in PhysRWRReceiver with its physical signal before sending this
            // warning (RadarPatches, SeekerPatches). A mirror client gets the pings from the host RWR feed
            // (RwrFeedSync); on any other client the warning RPC is the only feed.
            if (!aircraft.IsServer && !CnoEvilNet.IsMirrorClient)
            {
                try
                {
                    PhysRWRReceiver rwr = PhysRWRReceiver.Get(aircraft);
                    if (rwr != null)
                    {
                        bool isMissile = (source.emitter is Missile);
                        RwrThreatState threatState = isMissile ? RwrThreatState.MissileGuidance : (source.isTarget ? RwrThreatState.Track : RwrThreatState.Search);
                        rwr.RegisterRadarPing(source.emitter, null, threatState, ClientSignalStrength(source, aircraft, isMissile), isMissile);
                    }
                }
                catch (Exception ex)
                {
                    PatchErrorLog.Once("TacScreen_OnRadarWarning_Prefix", ex);
                }
            }

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
