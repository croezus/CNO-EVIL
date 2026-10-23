using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Configuration;
using UnityEngine;

namespace CnoEvil.Config
{
    /// <summary>
    /// BepInEx configuration. One partial file per config section (CnoEvilConfig.*.cs); every value is a plain
    /// number or switch in BepInEx/config. Bound in CnoEvilPlugin.Awake before any Harmony patch is applied,
    /// so entries are never null at runtime. Field name = config key.
    /// </summary>
    public static partial class CnoEvilConfig
    {
        public static void Initialize(ConfigFile config)
        {
            BindGeneral(config);
            BindDebugOverlays(config);
            BindRadarPhysics(config);
            BindElectronicWarfare(config);
            BindMissileGuidance(config);
            BindRwrReceiver(config);
            BindTargetTracking(config);
            BindTrackQuality(config);
            BindWeaponDispersion(config);
            BindAIDoctrine(config);
            BindDatalink(config);
            BindHudMarkers(config);
            BindTacticalMap(config);
            BindCockpitTacScreen(config);
            BindMissileTrajectoryHud(config);
            BindHmdThermalWindow(config);

            MigrateMovedEntries(config);
        }

        public static bool IsModActive
        {
            // A multiplayer client follows the host switch only, its own ModEnabled (F10) is ignored
            get { return CnoEvil.Core.CnoEvilNet.ModAllowed && (!DebugAllowed || ModEnabled.Value); }
        }

        /// <summary>
        /// F8 / F9 / F10 / F11 and the debug overlays work in single player and on the host only.
        /// </summary>
        public static bool DebugAllowed
        {
            get { return CnoEvil.Core.CnoEvilNet.Role == CnoEvil.Core.NetRole.Authority; }
        }

        public static bool DebugHUDOn
        {
            get { return DebugAllowed && ShowDebugHUD.Value; }
        }

        public static bool GizmosOn
        {
            get { return DebugAllowed && DrawDebugGizmos.Value; }
        }

        public static bool DatalinkOverlayOn
        {
            get { return DebugAllowed && DrawDatalinkVisualizer.Value; }
        }

        /// <summary>
        /// The F9 telemetry panel or the F11 radar rays are on: the only readers of RadarTelemetry.
        /// </summary>
        public static bool RadarTelemetryWanted
        {
            get { return DebugHUDOn || GizmosOn; }
        }

        public static float EffectiveNotchThreshold
        {
            get { return NotchVelocityThreshold_mps.Value; }
        }

        public static float EffectiveNotchClutterEdgeFactor
        {
            get { return NotchClutterEdgeFactor.Value; }
        }

        /// <summary>
        /// Radar sidelobe level (dB, at most 0) for ground clutter and for jamming that enters through the sidelobes.
        /// </summary>
        public static float EffectiveRadarSidelobeClutterDb
        {
            get { return Mathf.Min(0.0f, RadarSidelobeClutter_dB.Value); }
        }

        /// <summary>
        /// Seeker sidelobe level (dB, at most 0) for ground clutter and for jamming that enters through the sidelobes.
        /// </summary>
        public static float EffectiveSeekerSidelobeClutterDb
        {
            get { return Mathf.Min(0.0f, SeekerSidelobeClutter_dB.Value); }
        }

        public static float EffectiveMinSNR
        {
            get { return MinSNR_dB.Value; }
        }

        /// <summary>
        /// Seconds an RWR threat stays after its last pulse, at least 0.5 (receiver, TacScreen and map use the same value).
        /// </summary>
        public static float EffectiveRwrStrobePersistence
        {
            get { return Mathf.Max(0.5f, RWRStrobePersistence_s.Value); }
        }

        /// <summary>
        /// Seconds between datalink cycles, at least 0.05.
        /// </summary>
        public static float EffectiveDatalinkScanInterval
        {
            get { return Mathf.Max(0.05f, DatalinkScanInterval_s.Value); }
        }

        /// <summary>
        /// Seconds a relayed datalink threat stays on a receiver without a new relay: 3.5, or longer than the
        /// Basic relay interval, so a Basic datalink threat does not blink out between relays.
        /// </summary>
        public static float DatalinkThreatExpirySeconds
        {
            get { return Mathf.Max(3.5f, BasicDatalinkRelayInterval_s.Value + 1.0f); }
        }

        private static ConfigEntry<T> Bind<T>(ConfigFile config, string section, string key, T defaultValue, string description)
        {
            return config.Bind(section, key, defaultValue, description);
        }

        /// <summary>
        /// Old key -> new key of renamed entries. The saved value of the old key goes to the new key.
        /// </summary>
        private static readonly Dictionary<string, string> RenamedKeys = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            // 04. Electronic Warfare
            { "ECMNotchExpansionMultiplier", "ECMNotchWidening" },
            { "EcmWattsPerIntensity", "EcmPowerPerIntensity_W" },
            { "MedusaJamConeAngleDeg", "JammingPodConeAngle_deg" },
            { "JammingPodPowerW", "JammingPodPower_W" },
            // 05. Missile Guidance
            { "MissileMemoryCoastSeconds", "MissileMemoryCoast_s" },
            { "ARHSeekerActivationRangeMeters", "ARHSeekerActivationRange_m" },
            { "ARMSeekerActivationRangeMeters", "ARMSeekerActivationRange_m" },
            { "OpticalSeekerActivationRangeMeters", "OpticalSeekerActivationRange_m" },
            { "OpticalSearchRadiusMeters", "OpticalSearchRadius_m" },
            { "CruiseTerminalRangeMeters", "CruiseTerminalRange_m" },
            { "CruiseTerminalSearchRadiusMeters", "CruiseTerminalSearchRadius_m" },
            { "MissileMaxBattlefieldRadiusKm", "MissileMaxBattlefieldRadius_km" },
            // 06. RWR Receiver
            { "RWRStrobePersistenceSeconds", "RWRStrobePersistence_s" },
            { "Tier1_AzimuthJitterDeg", "Tier1_AzimuthJitter_deg" },
            { "Tier1_KuBandDetectRange_m", "Tier1_SeekerDetectRange_m" },
            { "Tier1_ElevationMinDeg", "Tier1_ElevationMin_deg" },
            { "Tier1_ElevationMaxDeg", "Tier1_ElevationMax_deg" },
            { "Tier2_AzimuthJitterDeg", "Tier2_AzimuthJitter_deg" },
            { "Tier2_KuBandDetectRange_m", "Tier2_SeekerDetectRange_m" },
            { "Tier2_ElevationMinDeg", "Tier2_ElevationMin_deg" },
            { "Tier2_ElevationMaxDeg", "Tier2_ElevationMax_deg" },
            { "Tier3_AzimuthJitterDeg", "Tier3_AzimuthJitter_deg" },
            { "Tier3_KuBandDetectRange_m", "Tier3_SeekerDetectRange_m" },
            { "Tier3_ElevationMinDeg", "Tier3_ElevationMin_deg" },
            { "Tier3_ElevationMaxDeg", "Tier3_ElevationMax_deg" },
            // 07. Target Tracking & Memory
            { "AircraftMemoryDurationSeconds", "AircraftMemory_s" },
            { "GroundTargetMemoryDurationSeconds", "GroundTargetMemory_s" },
            { "StrategicBuildingVisualRangeMeters", "StrategicBuildingVisualRange_m" },
            { "StrategicBuildingMemorySeconds", "StrategicBuildingMemory_s" },
            { "ContactHoldSeconds", "ContactHold_s" },
            { "BearingObservationMaxAgeSeconds", "BearingObservationMaxAge_s" },
            { "MapReconnaissanceQualityThreshold", "LocatedQualityThreshold" },
            // 08. Track Quality Balance
            { "SensorIntelligenceTickIntervalSeconds", "QualityGainTick_s" },
            { "RadarSweepBaseContribution", "RadarSweepQualityGain" },
            { "RadarDwellContributionRate", "RadarDwellQualityGainPerSecond" },
            { "RWRBaseContribution", "RwrQualityGain" },
            { "VisualReconnaissanceBoost", "VisualContactQuality" },
            { "TriangulationBaselineBoost", "FirstFixQualityGain" },
            { "TrackUncertaintyRangeWeightingMin", "QualityGainFullWeightRange_m" },
            { "TrackUncertaintyRangeWeightingMax", "QualityGainFloorRange_m" },
            { "RangeWeightFloor", "QualityGainRangeWeightFloor" },
            { "RangeWeightExponent", "QualityGainRangeWeightExponent" },
            // 09. Weapon Dispersion (CEP)
            { "CepAtZeroQualityMeters", "CepAtZeroQuality_m" },
            { "CepMinMeters", "CepMin_m" },
            { "CepDistanceReferenceMeters", "CepDistanceReference_m" },
            // 11. Datalink
            { "DatalinkScanIntervalSeconds", "DatalinkScanInterval_s" },
            { "BasicDatalinkRelayIntervalSeconds", "BasicDatalinkRelayInterval_s" },
            // 12. HUD & Markers
            { "EnableHMDTrackQualityColoring", "EnableHUDMarkerQualityTint" },
            { "HMDIdentMidQuality", "IdentMidQuality" },
            { "HMDIdentHighQuality", "IdentHighQuality" },
            { "EnableHUDNotchIndicator", "EnableHUDEWCues" },
            { "DetectionStatusIntervalSeconds", "DetectionStatusInterval_s" },
            // 13. Tactical Map & Minimap
            { "EnableRealisticRWRMapStrobes", "EnableMapRWRZones" },
            { "EnableMinimapRWRStrobes", "EnableMinimapRWRZones" },
            { "HideUntriangulatedRadarIcons", "HideUnlocatedThreatIcons" },
            { "MapRWRZoneNearRange", "MapRWRZoneNearRange_m" },
            { "MapRWRZoneNearRadius", "MapRWRZoneNearRadius_m" },
            { "MapRWRZoneFarRange", "MapRWRZoneFarRange_m" },
            { "MapRWRZoneFarRadius", "MapRWRZoneFarRadius_m" },
            { "RWRSearchConeOpacity", "RWRSearchStrobeOpacity" },
            { "RWRTrackConeOpacity", "RWRTrackStrobeOpacity" },
            { "RWRMissileConeOpacity", "RWRMissileStrobeOpacity" },
            { "RWRUncertaintyZoneOpacity", "MapRWRZoneOpacity" },
            { "EnableDatalinkVisualStrobes", "EnableDatalinkStrobes" },
            { "RWRDatalinkConeOpacity", "RWRDatalinkStrobeOpacity" },
            { "ShowPlayerMissilesOnlyOnMap", "MissileAimpointPlayerOnly" },
            { "MinimapLookAhead", "MinimapLookAhead_m" },
            // 14. Cockpit TacScreen
            { "EnableRWRBearingCone", "RWRStrobeCones" },
            { "TacScreenEmScanRingRangeMeters", "TacScreenEmScanRingRange_m" },
            { "TacScreenEmScanRingThickness", "TacScreenEmScanRingThickness_px" },
            { "CockpitRWRSearchSectorAngle", "RWRSearchStrobeAngle_deg" },
            { "CockpitRWRTrackSectorAngle", "RWRTrackStrobeAngle_deg" },
            { "CockpitRWRMissileSectorAngle", "RWRMissileStrobeAngle_deg" },
            { "CockpitRWRThreatDistanceRatio", "DatalinkStrobeLengthRatio" },
            { "PortalMFDTsdRangeKm", "PortalMFDTsdRange_km" },
            { "PortalMFDNavRangeKm", "PortalMFDNavRange_km" },
            { "RWRUncertaintyCircleScale", "PortalUncertaintyRingScale" },
            // 15. Missile Trajectory HUD
            { "MissileTrajectoryUpdateRate", "MissileTrailSampleRate" },
            { "MissileTrajectoryMaxSegments", "MissileTrailSegments" },
            // 16. HMD Thermal Window
            { "HMDThermalNearClip", "HMDThermalNearClip_m" },
            { "HMDThermalMaxRange", "HMDThermalMaxRange_m" },
        };

        /// <summary>
        /// Entries that moved to a new section keep their key, renamed entries are in RenamedKeys: carry the value a
        /// user saved under the old section or key over to the new entry, then drop every leftover entry (moved,
        /// renamed, removed presets and dead keys) from the file.
        /// </summary>
        private static void MigrateMovedEntries(ConfigFile config)
        {
            try
            {
                PropertyInfo orphanProp = typeof(ConfigFile).GetProperty("OrphanedEntries", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                Dictionary<ConfigDefinition, string> orphans = (orphanProp != null)
                    ? orphanProp.GetValue(config, null) as Dictionary<ConfigDefinition, string>
                    : null;
                if (orphans == null || orphans.Count == 0) return;

                Dictionary<string, string> orphanByKey = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var kvp in orphans)
                {
                    string newKey;
                    string key = RenamedKeys.TryGetValue(kvp.Key.Key, out newKey) ? newKey : kvp.Key.Key;
                    orphanByKey[key] = kvp.Value;
                }

                int migrated = 0;
                foreach (var kvp in config)
                {
                    string oldValue;
                    if (orphanByKey.TryGetValue(kvp.Key.Key, out oldValue))
                    {
                        kvp.Value.SetSerializedValue(oldValue);
                        migrated++;
                    }
                }

                int dropped = orphans.Count;
                orphans.Clear();
                config.Save();

                if (CnoEvilPlugin.Log != null)
                {
                    CnoEvilPlugin.Log.LogInfo(string.Format("Config migration: {0} values carried to new sections or keys, {1} obsolete entries removed.", migrated, dropped));
                }
            }
            catch (Exception ex)
            {
                if (CnoEvilPlugin.Log != null) CnoEvilPlugin.Log.LogWarning("Config migration skipped: " + ex.Message);
            }
        }
    }
}
