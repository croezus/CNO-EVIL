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
        public static ConfigEntry<float> EcmJamDuration_s;
        public static ConfigEntry<float> EcmRechargeTime_s;
        public static ConfigEntry<int> CountermeasureBudget;
        public static ConfigEntry<int> DefaultFlareCount;
        public static ConfigEntry<int> DefaultChaffCount;
        public static ConfigEntry<int> DefaultDecoyCount;
        public static ConfigEntry<bool> ChaffEnabled;
        public static ConfigEntry<float> ChaffEjectionInterval_s;
        public static ConfigEntry<float> ChaffCartridgeRcs_m2;
        public static ConfigEntry<float> ChaffBloomTime_s;
        public static ConfigEntry<float> ChaffLifetime_s;
        public static ConfigEntry<float> ChaffDrag;
        public static ConfigEntry<float> ChaffSeekerBeamwidth_deg;
        public static ConfigEntry<float> ChaffSeekerRangeGate_m;
        public static ConfigEntry<float> ChaffSeekerVelocityGate_mps;
        public static ConfigEntry<float> ChaffDopplerRateLimit_mps2;
        public static ConfigEntry<float> ChaffDopplerRejection_dB;
        public static ConfigEntry<float> ChaffCaptureRatioMin;
        public static ConfigEntry<bool> DecoyEnabled;
        public static ConfigEntry<float> DecoyEjectionInterval_s;
        public static ConfigEntry<float> DecoyArmDelay_s;
        public static ConfigEntry<float> DecoyActiveTime_s;
        public static ConfigEntry<float> DecoyDrag;
        public static ConfigEntry<float> DecoyRcsAtReference_m2;
        public static ConfigEntry<float> DecoyReferenceRange_m;
        public static ConfigEntry<float> DecoyMaxRcs_m2;
        public static ConfigEntry<bool> DecoyLeadingEdgeRejection;
        public static ConfigEntry<bool> DecoyBreaksRadarLock;
        public static ConfigEntry<bool> DecoyBeaconVisible;

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

            EcmJamDuration_s = Bind(c, SectionEW, "EcmJamDuration_s", 12.0f,
                "Self-protection jammer transmit budget: seconds of jamming that empty the full power supply of the aircraft (kJ). " +
                "The jamming power is proportional to the charge of the supply. 0 = vanilla (the engines keep the supply charged).");
            EcmRechargeTime_s = Bind(c, SectionEW, "EcmRechargeTime_s", 30.0f,
                "Seconds for the engines to recharge the power supply from empty to full while the jammer does not transmit. " +
                "The radar and other users still get their power. Aircraft with a jamming pod or a laser keep the vanilla recharge.");

            CountermeasureBudget = Bind(c, SectionEW, "CountermeasureBudget", 80,
                "Highest total number of flares, chaff cartridges and active decoys on one aircraft with flares. Each player " +
                "divides this number between the three types with the sliders in the loadout menu.");
            DefaultFlareCount = Bind(c, SectionEW, "DefaultFlareCount", 40,
                "Number of flares on an aircraft with flares when the player did not set a number (AI aircraft, first flight). " +
                "Replaces the flare count of the aircraft prefab. Applies to aircraft spawned after a change.");
            DefaultChaffCount = Bind(c, SectionEW, "DefaultChaffCount", 20,
                "Number of chaff cartridges on an aircraft with flares when the player did not set a number. Applies to " +
                "aircraft spawned after a change.");
            DefaultDecoyCount = Bind(c, SectionEW, "DefaultDecoyCount", 20,
                "Number of active decoys on an aircraft with flares when the player did not set a number. Applies to " +
                "aircraft spawned after a change.");

            ChaffEnabled = Bind(c, SectionEW, "ChaffEnabled", true,
                "Aircraft with flares also carry chaff: a separate countermeasure station (Next Countermeasure) against ARH and SARH " +
                "missiles. FALSE: the chaff station stays (the station list must be the same on all computers) but it does not fire, " +
                "and chaff has no effect on seekers.");
            ChaffEjectionInterval_s = Bind(c, SectionEW, "ChaffEjectionInterval_s", 0.75f,
                "Minimum seconds between two chaff cartridges while the countermeasure button is held.");
            ChaffCartridgeRcs_m2 = Bind(c, SectionEW, "ChaffCartridgeRcs_m2", 0.4f,
                "Radar cross section (m2, game scale) of one chaff cloud after bloom. The game scale is the scale of the aircraft " +
                "radarSize: a conventional fighter is 0.06 m2 from the nose and 0.2 m2 from the side, a stealth fighter 0.0002 m2 " +
                "from the nose. Higher = chaff outshines more aircraft.");
            ChaffBloomTime_s = Bind(c, SectionEW, "ChaffBloomTime_s", 0.5f,
                "Seconds for a chaff cartridge to bloom to 95 % of its radar cross section.");
            ChaffLifetime_s = Bind(c, SectionEW, "ChaffLifetime_s", 12.0f,
                "Seconds a chaff cloud exists. Its radar cross section in a seeker cell decreases linearly from 40 % of this time to 0.");
            ChaffDrag = Bind(c, SectionEW, "ChaffDrag", 0.05f,
                "Quadratic drag of the chaff cloud (1/m). The dipoles decelerate by this x speed^2: at 250 m/s the cloud loses half " +
                "its speed in 0.08 s. Higher = the cloud stops faster and leaves the seeker velocity gate sooner.");
            ChaffSeekerBeamwidth_deg = Bind(c, SectionEW, "ChaffSeekerBeamwidth_deg", 8.0f,
                "Full beam angle (deg) of an ARH or SARH seeker antenna. Chaff in this cone around the target competes with the target.");
            ChaffSeekerRangeGate_m = Bind(c, SectionEW, "ChaffSeekerRangeGate_m", 150.0f,
                "Range gate (m) of the seeker: chaff more than this distance in range from the target does not compete with it.");
            ChaffSeekerVelocityGate_mps = Bind(c, SectionEW, "ChaffSeekerVelocityGate_mps", 25.0f,
                "Doppler velocity gate (m/s) of the seeker: chaff whose radial speed differs more from the target is rejected " +
                "(see ChaffDopplerRejection_dB). A beaming target keeps its chaff in the gate.");
            ChaffDopplerRateLimit_mps2 = Bind(c, SectionEW, "ChaffDopplerRateLimit_mps2", 120.0f,
                "Highest radial acceleration (m/s2) the seeker velocity tracker accepts. A fresh cartridge decelerates faster than " +
                "any aircraft, so it is rejected until its radial speed is low.");
            ChaffDopplerRejection_dB = Bind(c, SectionEW, "ChaffDopplerRejection_dB", 30.0f,
                "Attenuation (dB) of rejected chaff in the seeker. The rejection is not perfect: against a very small target " +
                "(stealth, nose aspect) the remaining chaff echo can still be larger. Higher = chaff is less effective head-on.");
            ChaffCaptureRatioMin = Bind(c, SectionEW, "ChaffCaptureRatioMin", 0.5f,
                "Lowest chaff share of the echo power in the seeker cell (0-1) that can capture the seeker. Each missile draws " +
                "for each chaff cloud a capture threshold between this value and 0.95, raised by the seeker jam tolerance " +
                "(game data). A captured seeker flies to the cloud and must find the target again after the cloud fades.");

            DecoyEnabled = Bind(c, SectionEW, "DecoyEnabled", true,
                "Aircraft with flares also carry expendable active decoys: a DRFM repeater in a flare cartridge, a separate " +
                "countermeasure station after chaff, against ARH and SARH missiles. FALSE: the decoy station stays (the station " +
                "list must be the same on all computers) but it does not fire, and decoys have no effect on seekers.");
            DecoyEjectionInterval_s = Bind(c, SectionEW, "DecoyEjectionInterval_s", 2.0f,
                "Minimum seconds between two decoys while the countermeasure button is held.");
            DecoyArmDelay_s = Bind(c, SectionEW, "DecoyArmDelay_s", 0.3f,
                "Seconds from ejection until the decoy starts to transmit.");
            DecoyActiveTime_s = Bind(c, SectionEW, "DecoyActiveTime_s", 10.0f,
                "Seconds the decoy battery lets it transmit. After this time the decoy falls without effect.");
            DecoyDrag = Bind(c, SectionEW, "DecoyDrag", 0.006f,
                "Quadratic drag of the decoy cartridge (1/m). The decoy decelerates by this x speed^2: at 250 m/s it loses half " +
                "its speed in 0.7 s. Lower = the decoy stays near the aircraft longer.");
            DecoyRcsAtReference_m2 = Bind(c, SectionEW, "DecoyRcsAtReference_m2", 0.5f,
                "Equivalent radar cross section (m2, game scale) of the decoy for a transmitter at DecoyReferenceRange_m. The decoy " +
                "has a fixed output power, so its equivalent RCS increases with the square of the transmitter range (the missile " +
                "seeker for ARH, the illuminator for SARH). A conventional fighter is 0.06 m2 from the nose and 0.2 m2 from the side.");
            DecoyReferenceRange_m = Bind(c, SectionEW, "DecoyReferenceRange_m", 3000.0f,
                "Transmitter range (m) at which the decoy has the equivalent RCS DecoyRcsAtReference_m2.");
            DecoyMaxRcs_m2 = Bind(c, SectionEW, "DecoyMaxRcs_m2", 3.0f,
                "Highest equivalent radar cross section (m2, game scale) of the decoy: the repeater gain limit at long range.");
            DecoyLeadingEdgeRejection = Bind(c, SectionEW, "DecoyLeadingEdgeRejection", true,
                "ARH seekers track the leading edge of the echo: a decoy that is more than half the range gate " +
                "(ChaffSeekerRangeGate_m) farther from the missile than the target is ignored. The decoy falls behind the aircraft, " +
                "so it works against a missile from the side or from behind, not head-on. FALSE: no leading-edge tracking.");
            DecoyBreaksRadarLock = Bind(c, SectionEW, "DecoyBreaksRadarLock", true,
                "A decoy also captures the track gate of a radar that guides a missile at the aircraft or holds a missile lock on it. " +
                "The capture uses the radar beam and the rules of the seeker capture. The radar then does not detect the aircraft, " +
                "its faction track moves with the decoy, and a SARH missile of that radar flies to the decoy. Search radars are not " +
                "affected. FALSE: decoys capture only missile seekers.");
            DecoyBeaconVisible = Bind(c, SectionEW, "DecoyBeaconVisible", true,
                "A faint red light flashes on the decoy while it transmits, to make it easier to see. FALSE: no light (realistic).");
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
