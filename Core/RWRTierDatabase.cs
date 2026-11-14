using System;
using BepInEx.Configuration;
using UnityEngine;
using CnoEvil.Components;
using CnoEvil.Config;

namespace CnoEvil.Core
{
    public enum RwrTier
    {
        Tier1_Basic,      // Rank 0-1, no radar: CI-22, T/A-30, SAH-46, UH-90 (analog 4-quadrant, narrow elevation, poor Ku sensitivity)
        Tier2_Standard,   // Rank 2, or rank 0-1 with a radar: A-19, VL-49, VT-7 (digital threat library, standard Ku, TADIL datalink)
        Tier3_Advanced    // Rank 3+: FS-12, FS-20, EW-25, KR-67, SFB-81, AB-4, UFO (spherical array, zero blind cone, fusion datalink)
    }

    public enum DatalinkTier
    {
        Basic,     // Tactical datalink: receives allied missile & threat vectors (enabled on Tier 1 and Tier 2)
        Advanced   // High-speed Sensor Fusion: instant, precision track correlation with allied network
    }

    public struct RwrCapabilities
    {
        public RwrTier Tier;
        public DatalinkTier Datalink;
        public float AzimuthJitterDegrees;
        public float ElevationMinDeg;      // Lower aperture limit (negative degrees, e.g. -25°)
        public float ElevationMaxDeg;      // Upper aperture limit (positive degrees, e.g. +35°, zenith blind cone above)
        public float KuBandMaxDetectRange; // Max range to detect a Ku-band active missile seeker (meters)
        public bool CanClassifyThreats;    // True if digital threat library identifies AIR, SAM, NAV, ACT M
        public bool HasHUDNotchGuidance;   // True if HUD computer calculates clutter notch gate
    }

    public static class RWRTierDatabase
    {
        // Vanilla unlock rank (AircraftParameters.rankRequired) at which each tier starts (ADR-70)
        private const int Tier2MinRank = 2;
        private const int Tier3MinRank = 3;

        /// <summary>
        /// RWR tier from the vanilla unlock rank of the aircraft (AircraftParameters.rankRequired): rank 0-1 -> Tier 1,
        /// rank 2 -> Tier 2, rank 3 or more -> Tier 3. An aircraft whose prefab has a Radar gets at least Tier 2
        /// (VT-7: typeIdentity.radar = 0 in its definition, but it has a radar). A PlatformOverrides entry with an RWR
        /// tier comes first. A unit that is not an aircraft, or has no parameters, gets Tier 2.
        /// </summary>
        public static RwrTier GetTier(Unit unit)
        {
            PlatformOverride entry;
            if (PlatformOverrides.TryGet(unit, out entry) && entry.RwrTier >= 1 && entry.RwrTier <= 3)
            {
                return (RwrTier)(entry.RwrTier - 1);
            }

            try
            {
                Aircraft aircraft = unit as Aircraft;
                if (aircraft == null) return RwrTier.Tier2_Standard;
                AircraftParameters parameters = aircraft.GetAircraftParameters();
                if (parameters == null) return RwrTier.Tier2_Standard;

                int rank = parameters.rankRequired;
                if (rank >= Tier3MinRank) return RwrTier.Tier3_Advanced;
                if (rank >= Tier2MinRank) return RwrTier.Tier2_Standard;
                return HasRadar(aircraft) ? RwrTier.Tier2_Standard : RwrTier.Tier1_Basic;
            }
            catch (Exception ex)
            {
                PatchErrorLog.Once("RWRTierDatabase.GetTier", ex);
                return RwrTier.Tier2_Standard;
            }
        }

        // Radar of the airframe: the prefab of the definition (no weapon mounts, so a radome pod gives Tier 4, not this),
        // else the live aircraft
        private static bool HasRadar(Aircraft aircraft)
        {
            UnitDefinition def = aircraft.definition;
            GameObject prefab = (def != null) ? def.unitPrefab : null;
            if (prefab != null) return prefab.GetComponentInChildren<Radar>(true) != null;
            return aircraft.GetComponentInChildren<Radar>(true) != null;
        }

        /// <summary>
        /// Optics magnification of an aircraft from its avionics tier (the tier of its RWR, from PhysRWRReceiver):
        /// AircraftOpticsMagnificationTier1..3. Replaces the vanilla TargetDetector.magnification of aircraft.
        /// </summary>
        public static float GetOpticsMagnification(Aircraft aircraft)
        {
            PhysRWRReceiver rwr = PhysRWRReceiver.Get(aircraft);
            RwrTier tier = (rwr != null) ? rwr.Capabilities.Tier : GetTier(aircraft);
            ConfigEntry<float> entry;
            switch (tier)
            {
                case RwrTier.Tier1_Basic: entry = CnoEvilConfig.AircraftOpticsMagnificationTier1; break;
                case RwrTier.Tier3_Advanced: entry = CnoEvilConfig.AircraftOpticsMagnificationTier3; break;
                default: entry = CnoEvilConfig.AircraftOpticsMagnificationTier2; break;
            }
            return Mathf.Max(0.0f, entry.Value);
        }

        public static RwrCapabilities GetCapabilities(Unit unit)
        {
            RwrCapabilities caps = new RwrCapabilities();
            RwrTier tier = GetTier(unit);

            if (tier == RwrTier.Tier1_Basic)
            {
                // Tier 1: aircraft without radar. Datalink stays enabled so the allied team helps them.
                caps.Tier = RwrTier.Tier1_Basic;
                caps.Datalink = DatalinkTier.Basic;
                // Analog 4-quadrant receiver: coarse bearing, narrow coverage, hears active seekers only at point-blank range
                caps.AzimuthJitterDegrees = CnoEvilConfig.Tier1_AzimuthJitter_deg.Value;
                caps.ElevationMinDeg = CnoEvilConfig.Tier1_ElevationMin_deg.Value;
                caps.ElevationMaxDeg = CnoEvilConfig.Tier1_ElevationMax_deg.Value;
                caps.KuBandMaxDetectRange = CnoEvilConfig.Tier1_SeekerDetectRange_m.Value;
                caps.CanClassifyThreats = false;   // Generic warning only (no threat type discrimination)
                caps.HasHUDNotchGuidance = false;  // No HUD notch cheat cue; pilot must use compass instruments
                return caps;
            }

            if (tier == RwrTier.Tier3_Advanced)
            {
                // Tier 3: radar aircraft with a stealth airframe
                caps.Tier = RwrTier.Tier3_Advanced;
                caps.Datalink = DatalinkTier.Advanced;
                // Phase interferometry, near-spherical coverage from conformal distributed antennas
                caps.AzimuthJitterDegrees = CnoEvilConfig.Tier3_AzimuthJitter_deg.Value;
                caps.ElevationMinDeg = CnoEvilConfig.Tier3_ElevationMin_deg.Value;
                caps.ElevationMaxDeg = CnoEvilConfig.Tier3_ElevationMax_deg.Value;
                caps.KuBandMaxDetectRange = CnoEvilConfig.Tier3_SeekerDetectRange_m.Value;
                caps.CanClassifyThreats = true;
                caps.HasHUDNotchGuidance = true;   // Advanced 5th-gen EW computer calculates notch status
                return caps;
            }

            // Tier 2: radar aircraft with a conventional airframe
            return GetTier2Capabilities();
        }

        public static RwrCapabilities GetTier2Capabilities()
        {
            RwrCapabilities caps = new RwrCapabilities();
            caps.Tier = RwrTier.Tier2_Standard;
            caps.Datalink = DatalinkTier.Basic;
            // Standard digital RWR; lofting missiles diving from above ElevationMax fall into the zenith blind cone
            caps.AzimuthJitterDegrees = CnoEvilConfig.Tier2_AzimuthJitter_deg.Value;
            caps.ElevationMinDeg = CnoEvilConfig.Tier2_ElevationMin_deg.Value;
            caps.ElevationMaxDeg = CnoEvilConfig.Tier2_ElevationMax_deg.Value;
            caps.KuBandMaxDetectRange = CnoEvilConfig.Tier2_SeekerDetectRange_m.Value;
            caps.CanClassifyThreats = true;    // Threat classification: AIR, SAM, NAV, ACT M
            caps.HasHUDNotchGuidance = false;  // Approximate beaming guidance only, no guaranteed notch cheat
            return caps;
        }
    }
}
