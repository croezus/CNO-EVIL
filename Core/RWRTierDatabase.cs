using System;
using BepInEx.Configuration;
using UnityEngine;
using CnoEvil.Components;
using CnoEvil.Config;

namespace CnoEvil.Core
{
    public enum RwrTier
    {
        Tier1_Basic,      // No radar: CI-22, T/A-30, VT-7, A-19, VL-49, SAH-46, UH-90 (analog 4-quadrant, narrow elevation, poor Ku sensitivity)
        Tier2_Standard,   // Radar, conventional airframe: FS-12 Revoker, UFO (digital threat library, standard Ku, TADIL datalink)
        Tier3_Advanced    // Radar, stealth fixed-wing: FS-20, KR-67, EW-25, AB-4, SFB-81 (5th-gen spherical array, zero blind cone, fusion datalink)
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
        /// <summary>
        /// RWR tier from the vanilla unit definition: no radar (typeIdentity.radar = 0) -> Tier 1;
        /// radar and a stealth fixed-wing airframe (radarSize at or below the stealth RCS threshold, no swashplate rotor)
        /// -> Tier 3; else Tier 2. A PlatformOverrides entry with an RWR tier comes first.
        /// </summary>
        public static RwrTier GetTier(Unit unit)
        {
            PlatformOverride entry;
            if (PlatformOverrides.TryGet(unit, out entry) && entry.RwrTier >= 1 && entry.RwrTier <= 3)
            {
                return (RwrTier)(entry.RwrTier - 1);
            }

            UnitDefinition def = (unit != null) ? unit.definition : null;
            if (def == null) return RwrTier.Tier2_Standard;
            if (def.typeIdentity.radar <= 0.0f) return RwrTier.Tier1_Basic;
            // Helicopters have small radarSize from their size, not from a stealth airframe (SAH-46 0.02, UH-90 0.03)
            if (AspectRCSProfile.IsStealthAirframe(def.radarSize) && unit.GetComponentInChildren<SwashRotor>(true) == null)
            {
                return RwrTier.Tier3_Advanced;
            }
            return RwrTier.Tier2_Standard;
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
            return (entry != null) ? Mathf.Max(0.0f, entry.Value) : 1.0f;
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
                caps.AzimuthJitterDegrees = CnoEvilConfig.RwrTier1AzimuthJitterDeg.Value;
                caps.ElevationMinDeg = CnoEvilConfig.RwrTier1ElevationMinDeg.Value;
                caps.ElevationMaxDeg = CnoEvilConfig.RwrTier1ElevationMaxDeg.Value;
                caps.KuBandMaxDetectRange = CnoEvilConfig.RwrTier1KuBandDetectRangeMeters.Value;
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
                caps.AzimuthJitterDegrees = CnoEvilConfig.RwrTier3AzimuthJitterDeg.Value;
                caps.ElevationMinDeg = CnoEvilConfig.RwrTier3ElevationMinDeg.Value;
                caps.ElevationMaxDeg = CnoEvilConfig.RwrTier3ElevationMaxDeg.Value;
                caps.KuBandMaxDetectRange = CnoEvilConfig.RwrTier3KuBandDetectRangeMeters.Value;
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
            caps.AzimuthJitterDegrees = CnoEvilConfig.RwrTier2AzimuthJitterDeg.Value;
            caps.ElevationMinDeg = CnoEvilConfig.RwrTier2ElevationMinDeg.Value;
            caps.ElevationMaxDeg = CnoEvilConfig.RwrTier2ElevationMaxDeg.Value;
            caps.KuBandMaxDetectRange = CnoEvilConfig.RwrTier2KuBandDetectRangeMeters.Value;
            caps.CanClassifyThreats = true;    // Threat classification: AIR, SAM, NAV, ACT M
            caps.HasHUDNotchGuidance = false;  // Approximate beaming guidance only, no guaranteed notch cheat
            return caps;
        }
    }
}
