using BepInEx.Configuration;
using UnityEngine;

namespace CnoEvil.Config
{
    public static partial class CnoEvilConfig
    {
        // 03. Radar Physics
        public static ConfigEntry<float> MinSNR_dB;
        public static ConfigEntry<float> NotchVelocityThreshold_mps;
        public static ConfigEntry<bool> EarthCurvatureEnabled;
        public static ConfigEntry<bool> AtmosphericLossEnabled;
        public static ConfigEntry<bool> ExternalStoresRCSEnabled;
        public static ConfigEntry<bool> HelicopterRotorModulationEnabled;
        public static ConfigEntry<float> RadarDetectionRangeScale;
        public static ConfigEntry<float> RadarSidelobeClutter_dB;
        public static ConfigEntry<float> SeekerSidelobeClutter_dB;
        public static ConfigEntry<float> NotchClutterEdgeFactor;

        // 04. Electronic Warfare
        public static ConfigEntry<float> JammerBurnThroughRatio;
        public static ConfigEntry<float> ECMNotchWidening;
        public static ConfigEntry<bool> HOJEnabled;
        public static ConfigEntry<float> EcmPowerPerIntensity_W;
        public static ConfigEntry<float> JammingPodConeAngle_deg;
        public static ConfigEntry<float> JammingPodPower_W;

        // 06. RWR Receiver
        public static ConfigEntry<bool> EnableRWRTierGrading;
        public static ConfigEntry<bool> EnableRWRBlindZones;
        public static ConfigEntry<float> RWRStrobePersistence_s;
        public static ConfigEntry<float> Tier1_AzimuthJitter_deg;
        public static ConfigEntry<float> Tier1_SeekerDetectRange_m;
        public static ConfigEntry<float> Tier1_ElevationMin_deg;
        public static ConfigEntry<float> Tier1_ElevationMax_deg;
        public static ConfigEntry<float> Tier2_AzimuthJitter_deg;
        public static ConfigEntry<float> Tier2_SeekerDetectRange_m;
        public static ConfigEntry<float> Tier2_ElevationMin_deg;
        public static ConfigEntry<float> Tier2_ElevationMax_deg;
        public static ConfigEntry<float> Tier3_AzimuthJitter_deg;
        public static ConfigEntry<float> Tier3_SeekerDetectRange_m;
        public static ConfigEntry<float> Tier3_ElevationMin_deg;
        public static ConfigEntry<float> Tier3_ElevationMax_deg;

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
                "Signal-to-noise ratio (dB) a radar or radar seeker needs to detect and track a target. Lower = longer detection range.");
            NotchVelocityThreshold_mps = Bind(c, SectionRadar, "NotchVelocityThreshold_mps", 20.0f,
                "Pulse-Doppler clutter notch half-width (m/s). A target whose radial speed relative to the ground is below this " +
                "(beaming) is lost when it has ground clutter behind it (look-down, below 120 m above the terrain, or below 300 m " +
                "near the horizon) or when it jams. Against open sky it stays visible. Hovering helicopters (rotor modulation) and " +
                "ships are not lost by this rule; for ships the sea clutter decides.");
            EarthCurvatureEnabled = Bind(c, SectionRadar, "EarthCurvatureEnabled", true,
                "Radar horizon: the radar path bends with a 4/3 effective Earth radius (standard refraction) over the real terrain and the sea. " +
                "FALSE: the vanilla horizon (Earth radius, sea level) and a straight terrain line of sight.");
            AtmosphericLossEnabled = Bind(c, SectionRadar, "AtmosphericLossEnabled", true,
                "Two-way atmospheric absorption by radar band (dB/km). The radars are calibrated with or without it, so they keep " +
                "their vanilla range at 1 m2 either way; the setting changes how fast the signal falls with range. A change " +
                "recalibrates all radars at once.");
            ExternalStoresRCSEnabled = Bind(c, SectionRadar, "ExternalStoresRCSEnabled", true,
                "External weapons, pylons, tanks and pods, and open weapon bay doors, add radar cross section.");
            HelicopterRotorModulationEnabled = Bind(c, SectionRadar, "HelicopterRotorModulationEnabled", true,
                "Helicopter rotor blades: their micro-Doppler (HERM) keeps hovering helicopters out of the clutter notch, and " +
                "their rotor flash adds RCS. FALSE: no rotor effects.");
            RadarDetectionRangeScale = Bind(c, SectionRadar, "RadarDetectionRangeScale", 1.0f,
                "Global radar range multiplier (1.0 = calibrated, minimum 0.05). Applied as peak power x scale^4, so every radar " +
                "(ground, airborne, ARH seeker, SARH illuminator) detects scale x farther; burn-through range against noise jamming " +
                "grows by scale^2. The vanilla radar search still stops at 2 x the vanilla radar range.");
            RadarSidelobeClutter_dB = Bind(c, SectionRadar, "RadarSidelobeClutter_dB", -35.0f,
                "Sidelobe level of an aircraft or surface radar relative to the main lobe (dB, at most 0). Sets the sidelobe ground " +
                "clutter in look-down when the target Doppler is inside the clutter band of the moving radar (closing speed below " +
                "the radar speed, for example tail chase), and how much jamming-pod power enters away from the main lobe. " +
                "Higher = shorter look-down range against such targets and more sidelobe jamming.");
            SeekerSidelobeClutter_dB = Bind(c, SectionRadar, "SeekerSidelobeClutter_dB", -30.0f,
                "Sidelobe level of an ARH or SARH missile seeker (dB, at most 0). Same rules as RadarSidelobeClutter_dB.");
            NotchClutterEdgeFactor = Bind(c, SectionRadar, "NotchClutterEdgeFactor", 2.0f,
                "Edge of the main-lobe clutter outside the notch, as a multiple of the notch half-width. Between the notch and this speed " +
                "the clutter decreases smoothly to the sidelobe level. 1.0 = hard edge.");

            // The calibration includes the atmospheric loss: recalibrate the radars when it changes
            AtmosphericLossEnabled.SettingChanged += delegate { CnoEvil.Components.PhysRadarEmitter.RecalibrateAll(); };
        }

        private static void BindElectronicWarfare(ConfigFile c)
        {
            JammerBurnThroughRatio = Bind(c, SectionEW, "JammerBurnThroughRatio", 1.0f,
                "Divisor (minimum 0.1) on the jamming power every radar and radar seeker receives. Higher = burn-through from farther " +
                "away (the burn-through range grows with the square root of this value).");
            ECMNotchWidening = Bind(c, SectionEW, "ECMNotchWidening", 1.5f,
                "Clutter notch widening while the target jams (DRFM velocity gate pull-off, Doppler noise): " +
                "notch = NotchVelocityThreshold_mps x (1 + ECM intensity x this). 0 = no widening; 1.5 = up to 2.5 x at full ECM.");
            HOJEnabled = Bind(c, SectionEW, "HOJEnabled", true,
                "TRUE: every ARH and SARH missile may home on the jamming of its target (not inside the notch). FALSE: only missiles " +
                "with the vanilla home-on-jam ability do.");
            EcmPowerPerIntensity_W = Bind(c, SectionEW, "EcmPowerPerIntensity_W", 100.0f,
                "Effective self-protection jammer transmit power (W) per unit of the game's ECM intensity. Higher = stronger jamming.");
            JammingPodConeAngle_deg = Bind(c, SectionEW, "JammingPodConeAngle_deg", 10f,
                "Full angle of the jamming pod beam (deg, 5-179), for example EW-25 Medusa. The pod jams all hostile radars and radar seekers " +
                "in this cone around its target. The jam is almost constant to half of the cone radius and is -10 dB at the edge. " +
                "Past the target the jam fades out smoothly over 25% of the target distance. Beam gain = 41253 / angle^2: a narrower beam " +
                "is stronger. 0 = the game's jamming events go only to the target, and the physics beam is 5 deg.");
            JammingPodPower_W = Bind(c, SectionEW, "JammingPodPower_W", 100.0f,
                "Transmit power (W) of one jamming pod (EW-25 Medusa) at full capacitor. The jam at a receiver follows the one-way radar " +
                "equation: pod power x capacitor level x beam gain, divided by range squared. A radar is jammed mainly in the direction " +
                "of the jammer and less through its sidelobes. Higher = stronger stand-off jamming.");
        }

        private static void BindRwrReceiver(ConfigFile c)
        {
            EnableRWRTierGrading = Bind(c, SectionRwr, "EnableRWRTierGrading", true,
                "Aircraft get Tier 1, 2 or 3 avionics from the game data: no radar = Tier 1; radar and a stealth airframe " +
                "(RCS 0.02 m2 or less, no rotor) = Tier 3; other aircraft = Tier 2. The tier sets the RWR values below, the optics " +
                "magnification and the datalink (Tier 3: Advanced, shares bearings). FALSE: every aircraft is Tier 2 with a Basic datalink. " +
                "Tier settings apply to aircraft spawned after a change.");
            EnableRWRBlindZones = Bind(c, SectionRwr, "EnableRWRBlindZones", true,
                "RWR antennas are blind above ElevationMax_deg and below ElevationMin_deg of the tier (aircraft body frame).");
            RWRStrobePersistence_s = Bind(c, SectionRwr, "RWRStrobePersistence_s", 3.0f,
                "Seconds (minimum 0.5) an RWR threat stays on the receiver, the TacScreen and the map after its last received pulse.");

            Tier1_AzimuthJitter_deg = Bind(c, SectionRwr, "Tier1_AzimuthJitter_deg", 18.0f,
                "Tier 1 (no radar: helicopters, light aircraft) bearing error, +/- deg.");
            Tier1_SeekerDetectRange_m = Bind(c, SectionRwr, "Tier1_SeekerDetectRange_m", 3500.0f,
                "Tier 1 range (m) at which an active missile seeker is heard.");
            Tier1_ElevationMin_deg = Bind(c, SectionRwr, "Tier1_ElevationMin_deg", -25.0f,
                "Tier 1 lower antenna coverage limit (deg); below it the belly blind cone starts.");
            Tier1_ElevationMax_deg = Bind(c, SectionRwr, "Tier1_ElevationMax_deg", 35.0f,
                "Tier 1 upper antenna coverage limit (deg); above it the zenith blind cone starts.");

            Tier2_AzimuthJitter_deg = Bind(c, SectionRwr, "Tier2_AzimuthJitter_deg", 5.0f,
                "Tier 2 (radar, conventional airframe) bearing error, +/- deg.");
            Tier2_SeekerDetectRange_m = Bind(c, SectionRwr, "Tier2_SeekerDetectRange_m", 16000.0f,
                "Tier 2 range (m) at which an active missile seeker is heard.");
            Tier2_ElevationMin_deg = Bind(c, SectionRwr, "Tier2_ElevationMin_deg", -40.0f,
                "Tier 2 lower antenna coverage limit (deg).");
            Tier2_ElevationMax_deg = Bind(c, SectionRwr, "Tier2_ElevationMax_deg", 52.0f,
                "Tier 2 upper antenna coverage limit (deg).");

            Tier3_AzimuthJitter_deg = Bind(c, SectionRwr, "Tier3_AzimuthJitter_deg", 1.0f,
                "Tier 3 (stealth airframe, interferometer) bearing error, +/- deg.");
            Tier3_SeekerDetectRange_m = Bind(c, SectionRwr, "Tier3_SeekerDetectRange_m", 40000.0f,
                "Tier 3 range (m) at which an active missile seeker is heard.");
            Tier3_ElevationMin_deg = Bind(c, SectionRwr, "Tier3_ElevationMin_deg", -85.0f,
                "Tier 3 lower antenna coverage limit (deg).");
            Tier3_ElevationMax_deg = Bind(c, SectionRwr, "Tier3_ElevationMax_deg", 85.0f,
                "Tier 3 upper antenna coverage limit (deg).");
        }
    }
}
