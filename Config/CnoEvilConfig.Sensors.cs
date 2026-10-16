using BepInEx.Configuration;
using UnityEngine;

namespace CnoEvil.Config
{
    public static partial class CnoEvilConfig
    {
        // 03. Radar Physics
        public static ConfigEntry<float> MinSNR_dB;
        public static ConfigEntry<float> NotchVelocityThreshold;
        public static ConfigEntry<bool> EarthCurvatureEnabled;
        public static ConfigEntry<bool> AtmosphericLossEnabled;
        public static ConfigEntry<bool> ExternalStoresRCSEnabled;
        public static ConfigEntry<bool> HERMEnabled;
        public static ConfigEntry<float> RadarDetectionRangeScale;
        public static ConfigEntry<float> RadarSidelobeClutter_dB;
        public static ConfigEntry<float> SeekerSidelobeClutter_dB;
        public static ConfigEntry<float> NotchClutterEdgeFactor;

        // 04. Electronic Warfare
        public static ConfigEntry<float> JammerBurnThroughRatio;
        public static ConfigEntry<float> ECMNotchExpansionMultiplier;
        public static ConfigEntry<bool> HOJEnabled;
        public static ConfigEntry<float> EcmWattsPerIntensity;
        public static ConfigEntry<float> MedusaJamConeAngleDeg;

        // 06. RWR Receiver
        public static ConfigEntry<bool> EnableRWRTierGrading;
        public static ConfigEntry<bool> EnableRWRBlindZones;
        public static ConfigEntry<float> RWRJitterDegrees;
        public static ConfigEntry<float> RWRStrobePersistenceSeconds;
        public static ConfigEntry<float> RwrTier1AzimuthJitterDeg;
        public static ConfigEntry<float> RwrTier1KuBandDetectRangeMeters;
        public static ConfigEntry<float> RwrTier1ElevationMinDeg;
        public static ConfigEntry<float> RwrTier1ElevationMaxDeg;
        public static ConfigEntry<float> RwrTier2AzimuthJitterDeg;
        public static ConfigEntry<float> RwrTier2KuBandDetectRangeMeters;
        public static ConfigEntry<float> RwrTier2ElevationMinDeg;
        public static ConfigEntry<float> RwrTier2ElevationMaxDeg;
        public static ConfigEntry<float> RwrTier3AzimuthJitterDeg;
        public static ConfigEntry<float> RwrTier3KuBandDetectRangeMeters;
        public static ConfigEntry<float> RwrTier3ElevationMinDeg;
        public static ConfigEntry<float> RwrTier3ElevationMaxDeg;

        private const string SectionRadar = "03. Radar Physics";
        private const string SectionEW = "04. Electronic Warfare";
        private const string SectionRwr = "06. RWR Receiver";

        /// <summary>
        /// Factor on every radar's peak power so that two-way detection range scales by RadarDetectionRangeScale
        /// (radar equation: R ~ P^(1/4)).
        /// </summary>
        public static float RadarPowerFactor
        {
            get
            {
                float scale = Mathf.Max(0.05f, RadarDetectionRangeScale.Value);
                float sq = scale * scale;
                return sq * sq;
            }
        }

        private static void BindRadarPhysics(ConfigFile c)
        {
            MinSNR_dB = Bind(c, SectionRadar, "MinSNR_dB", 10.0f,
                "Signal-to-noise ratio (dB) a radar needs to detect and track a target. Lower = longer detection range.");
            NotchVelocityThreshold = Bind(c, SectionRadar, "NotchVelocityThreshold_mps", 20.0f,
                "Pulse-Doppler clutter notch half-width (m/s). A target whose radial speed relative to the ground is below this is lost in the notch (beaming).");
            EarthCurvatureEnabled = Bind(c, SectionRadar, "EarthCurvatureEnabled", true,
                "Radar horizon: the radar path bends with a 4/3 effective Earth radius (standard refraction) over the real terrain and the sea. Off = the vanilla horizon (Earth radius, sea level) and a straight terrain line of sight.");
            AtmosphericLossEnabled = Bind(c, SectionRadar, "AtmosphericLossEnabled", true,
                "Two-way atmospheric absorption by radar band (dB/km).");
            ExternalStoresRCSEnabled = Bind(c, SectionRadar, "ExternalStoresRCSEnabled", true,
                "External weapons, pylons and tanks add radar cross section.");
            HERMEnabled = Bind(c, SectionRadar, "HelicopterRotorModulationEnabled", true,
                "Helicopter rotor micro-Doppler (HERM) keeps hovering helicopters out of the clutter notch.");
            RadarDetectionRangeScale = Bind(c, SectionRadar, "RadarDetectionRangeScale", 1.0f,
                "Global radar range multiplier (1.0 = calibrated). Applied as peak power x scale^4, so every radar (ground, airborne, ARH seeker, SARH illuminator) detects scale x farther and burns through jamming accordingly.");
            RadarSidelobeClutter_dB = Bind(c, SectionRadar, "RadarSidelobeClutter_dB", -35.0f,
                "Sidelobe ground clutter of an aircraft or surface radar relative to main-lobe clutter (dB). Applies in look-down when the target Doppler is inside the clutter band of the moving radar (closing speed below the radar speed, for example tail chase). Higher = shorter look-down range against such targets.");
            SeekerSidelobeClutter_dB = Bind(c, SectionRadar, "SeekerSidelobeClutter_dB", -30.0f,
                "Sidelobe ground clutter of an ARH or SARH missile seeker relative to main-lobe clutter (dB). Same rule as RadarSidelobeClutter_dB. Higher = seekers lose look-down targets in tail chase more easily.");
            NotchClutterEdgeFactor = Bind(c, SectionRadar, "NotchClutterEdgeFactor", 2.0f,
                "Edge of the main-lobe clutter outside the notch, as a multiple of the notch half-width. Between the notch and this speed the clutter decreases smoothly to the sidelobe level. 1.0 = hard edge.");
        }

        private static void BindElectronicWarfare(ConfigFile c)
        {
            JammerBurnThroughRatio = Bind(c, SectionEW, "JammerBurnThroughRatio", 1.0f,
                "Burn-through sensitivity multiplier. Higher = radars and radar seekers burn through noise jamming from farther away.");
            ECMNotchExpansionMultiplier = Bind(c, SectionEW, "ECMNotchExpansionMultiplier", 1.5f,
                "Clutter notch widening while the target jams (DRFM velocity gate pull-off, Doppler noise).");
            HOJEnabled = Bind(c, SectionEW, "HOJEnabled", true,
                "Radar-guided missiles may home on jam strobes.");
            EcmWattsPerIntensity = Bind(c, SectionEW, "EcmWattsPerIntensity", 100.0f,
                "Effective jammer transmit power (W) per unit of the game's ECM intensity. Higher = stronger jamming.");
            MedusaJamConeAngleDeg = Bind(c, SectionEW, "MedusaJamConeAngleDeg", 30.0f,
                "Full angle of the jamming pod beam (deg), for example EW-25 Medusa. The pod also jams the hostile units in this cone around its target. The effect is almost constant to half of the cone radius and is -10 dB at the edge. The width of the zone increases with range. 0 = only the target, as in the game.");
        }

        private static void BindRwrReceiver(ConfigFile c)
        {
            EnableRWRTierGrading = Bind(c, SectionRwr, "EnableRWRTierGrading", true,
                "Aircraft get Tier 1 (analog), Tier 2 (digital) or Tier 3 (5th-gen) RWR. FALSE: every aircraft uses Tier 2.");
            EnableRWRBlindZones = Bind(c, SectionRwr, "EnableRWRBlindZones", true,
                "RWR antennas are blind above ElevationMax and below ElevationMin of the tier (aircraft body frame).");
            RWRJitterDegrees = Bind(c, SectionRwr, "RWRJitterDegrees", 3.0f,
                "Minimum bearing error (+/- deg) of any RWR; each receiver uses the larger of this and its tier jitter.");
            RWRStrobePersistenceSeconds = Bind(c, SectionRwr, "RWRStrobePersistenceSeconds", 3.0f,
                "Seconds an RWR threat stays displayed after its last received pulse.");

            RwrTier1AzimuthJitterDeg = Bind(c, SectionRwr, "Tier1_AzimuthJitterDeg", 18.0f,
                "Tier 1 (helicopters, light aircraft) bearing error, +/- deg. Tier values apply to aircraft spawned after a change.");
            RwrTier1KuBandDetectRangeMeters = Bind(c, SectionRwr, "Tier1_KuBandDetectRange_m", 3500.0f,
                "Tier 1 range (m) at which an active missile seeker is heard.");
            RwrTier1ElevationMinDeg = Bind(c, SectionRwr, "Tier1_ElevationMinDeg", -25.0f,
                "Tier 1 lower antenna coverage limit (deg); below it the belly blind cone starts.");
            RwrTier1ElevationMaxDeg = Bind(c, SectionRwr, "Tier1_ElevationMaxDeg", 35.0f,
                "Tier 1 upper antenna coverage limit (deg); above it the zenith blind cone starts.");

            RwrTier2AzimuthJitterDeg = Bind(c, SectionRwr, "Tier2_AzimuthJitterDeg", 5.0f,
                "Tier 2 (4th-gen) bearing error, +/- deg.");
            RwrTier2KuBandDetectRangeMeters = Bind(c, SectionRwr, "Tier2_KuBandDetectRange_m", 16000.0f,
                "Tier 2 range (m) at which an active missile seeker is heard.");
            RwrTier2ElevationMinDeg = Bind(c, SectionRwr, "Tier2_ElevationMinDeg", -40.0f,
                "Tier 2 lower antenna coverage limit (deg).");
            RwrTier2ElevationMaxDeg = Bind(c, SectionRwr, "Tier2_ElevationMaxDeg", 52.0f,
                "Tier 2 upper antenna coverage limit (deg).");

            RwrTier3AzimuthJitterDeg = Bind(c, SectionRwr, "Tier3_AzimuthJitterDeg", 1.0f,
                "Tier 3 (5th-gen interferometer) bearing error, +/- deg.");
            RwrTier3KuBandDetectRangeMeters = Bind(c, SectionRwr, "Tier3_KuBandDetectRange_m", 40000.0f,
                "Tier 3 range (m) at which an active missile seeker is heard.");
            RwrTier3ElevationMinDeg = Bind(c, SectionRwr, "Tier3_ElevationMinDeg", -85.0f,
                "Tier 3 lower antenna coverage limit (deg).");
            RwrTier3ElevationMaxDeg = Bind(c, SectionRwr, "Tier3_ElevationMaxDeg", 85.0f,
                "Tier 3 upper antenna coverage limit (deg).");
        }
    }
}
