using BepInEx.Configuration;

namespace CnoEvil.Config
{
    public static partial class CnoEvilConfig
    {
        // 07. Target Tracking & Memory
        public static ConfigEntry<float> AircraftOpticsMagnificationTier1;
        public static ConfigEntry<float> AircraftOpticsMagnificationTier2;
        public static ConfigEntry<float> AircraftOpticsMagnificationTier3;
        public static ConfigEntry<float> AircraftMemory_s;
        public static ConfigEntry<float> GroundTargetMemory_s;
        public static ConfigEntry<float> StrategicBuildingVisualRange_m;
        public static ConfigEntry<float> StrategicBuildingMemory_s;
        public static ConfigEntry<float> PassiveRwrQualityCap;
        public static ConfigEntry<float> ContactHold_s;
        public static ConfigEntry<float> BearingFixMaxErrorFraction;
        public static ConfigEntry<float> BearingObservationMaxAge_s;
        public static ConfigEntry<float> LocatedQualityThreshold;
        public static ConfigEntry<float> MinMemoryDriftSpeed_mps;

        // 08. Track Quality Balance
        public static ConfigEntry<float> QualityGainTick_s;
        public static ConfigEntry<float> RadarSweepQualityGain;
        public static ConfigEntry<float> AlliedRadarSweepFactor;
        public static ConfigEntry<float> HeavyStationSweepBonus;
        public static ConfigEntry<float> RadarDwellQualityGainPerSecond;
        public static ConfigEntry<float> RwrQualityGain;
        public static ConfigEntry<float> RwrTier1QualityMultiplier;
        public static ConfigEntry<float> RwrTier3QualityMultiplier;
        public static ConfigEntry<float> VisualContactQuality;
        public static ConfigEntry<float> StrategicBuildingVisualQuality;
        public static ConfigEntry<float> FirstFixQualityGain;
        public static ConfigEntry<float> MultiStationFixBonus;
        public static ConfigEntry<float> QualityGainFullWeightRange_m;
        public static ConfigEntry<float> QualityGainFloorRange_m;
        public static ConfigEntry<float> QualityGainRangeWeightFloor;
        public static ConfigEntry<float> QualityGainRangeWeightExponent;

        // 09. Weapon Dispersion (CEP)
        public static ConfigEntry<bool> EnableTrackUncertaintyDispersion;
        public static ConfigEntry<float> CepAtZeroQuality_m;
        public static ConfigEntry<float> CepMin_m;
        public static ConfigEntry<float> CepQualityExponent;
        public static ConfigEntry<float> CepDistanceWeight;
        public static ConfigEntry<float> CepDistanceReference_m;

        private const string SectionTracking = "07. Target Tracking & Memory";
        private const string SectionQuality = "08. Track Quality Balance";
        private const string SectionDispersion = "09. Weapon Dispersion (CEP)";

        private static void BindTargetTracking(ConfigFile c)
        {
            AircraftOpticsMagnificationTier1 = Bind(c, SectionTracking, "AircraftOpticsMagnificationTier1", 1.0f,
                "Optics magnification of Tier 1 aircraft (no radar; tiers: EnableRWRTierGrading). An aircraft sees a target at " +
                "target visibility x magnification; this replaces the vanilla magnification check, the vanilla visualRange of the " +
                "aircraft still limits it. Vanilla detection (HUD markers, AI target search) uses the same range.");
            AircraftOpticsMagnificationTier2 = Bind(c, SectionTracking, "AircraftOpticsMagnificationTier2", 1.5f,
                "Optics magnification of Tier 2 aircraft (radar, conventional airframe).");
            AircraftOpticsMagnificationTier3 = Bind(c, SectionTracking, "AircraftOpticsMagnificationTier3", 2.0f,
                "Optics magnification of Tier 3 aircraft (radar, stealth airframe).");
            AircraftMemory_s = Bind(c, SectionTracking, "AircraftMemory_s", 60.0f,
                "Seconds a lost aircraft contact is remembered at its last known position. After ContactHold_s its Q falls by " +
                "1 / this per second; the track is dropped this long after the last detection.");
            GroundTargetMemory_s = Bind(c, SectionTracking, "GroundTargetMemory_s", 180.0f,
                "The same for ground and naval contacts. Buildings (radar buildings too) keep their Q in memory.");
            StrategicBuildingVisualRange_m = Bind(c, SectionTracking, "StrategicBuildingVisualRange_m", 20000.0f,
                "Range (m) of the faction scan that visually identifies enemy strategic buildings (factories, hangars, shelters, depots, " +
                "ammo dumps, helipads, refineries), with terrain line of sight. Vanilla spotters also identify them at their own range. " +
                "The scan runs in the datalink cycle (EnableAlliedDatalink).");
            StrategicBuildingMemory_s = Bind(c, SectionTracking, "StrategicBuildingMemory_s", 300.0f,
                "Seconds a strategic building stays tracked after the last visual contact. Its Q does not decay in memory.");
            PassiveRwrQualityCap = Bind(c, SectionTracking, "PassiveRwrQualityCap", 0.20f,
                "Highest tracking quality Q (0..1) RWR bearings can give while the faction has no radar / visual contact within " +
                "ContactHold_s and no bearing fix within BearingObservationMaxAge_s.");
            ContactHold_s = Bind(c, SectionTracking, "ContactHold_s", 6.0f,
                "A track stays in contact for this many seconds after its last radar detection, RWR pulse or visual contact " +
                "(covers the gap between sweeps of rotating radars). Q does not decay in contact; afterwards the track is in memory.");
            BearingFixMaxErrorFraction = Bind(c, SectionTracking, "BearingFixMaxErrorFraction", 0.15f,
                "A cross-bearing fix is accepted when its 1-sigma error is at most this fraction of the range (0.15 = 15%).");
            BearingObservationMaxAge_s = Bind(c, SectionTracking, "BearingObservationMaxAge_s", 6.0f,
                "Oldest bearing (s) still used for multi-station fixes, and how long a fix stays fresh (fix smoothing, the faction " +
                "position estimate, PassiveRwrQualityCap). For this long after a radar or visual contact no passive fix is made.");
            LocatedQualityThreshold = Bind(c, SectionTracking, "LocatedQualityThreshold", 0.75f,
                "Tracking quality Q (0..1) from which the faction has located a target without a recent radar / visual contact. " +
                "A located emitter gets a vanilla tracking entry from RWR pings (HUD marker, AI, vanilla seekers) and a map icon " +
                "(HideUnlocatedThreatIcons), and its map RWR zone shrinks to a cone.");
            MinMemoryDriftSpeed_mps = Bind(c, SectionTracking, "MinMemoryDriftSpeed_mps", 5.0f,
                "Drift speed (m/s) assumed for a lost contact that was stationary (relocating SAM sites). Sets the position error of the " +
                "faction estimate of a contact in memory (AI aim check, HUD cues, map aim points, stand-off jamming); it is not part of " +
                "the CEP. Buildings do not drift.");
        }

        private static void BindTrackQuality(ConfigFile c)
        {
            QualityGainTick_s = Bind(c, SectionQuality, "QualityGainTick_s", 1.0f,
                "Minimum seconds between Q gains on one target from radar sweeps (all radars of the faction share it) and from RWR " +
                "pulses (all receivers share it).");
            RadarSweepQualityGain = Bind(c, SectionQuality, "RadarSweepQualityGain", 0.20f,
                "Q gained per tick while your own radar detects the target, at close range (range-weighted).");
            AlliedRadarSweepFactor = Bind(c, SectionQuality, "AlliedRadarSweepFactor", 0.75f,
                "Share of RadarSweepQualityGain from any other radar of the faction (AI, other players; on a dedicated server every radar).");
            HeavyStationSweepBonus = Bind(c, SectionQuality, "HeavyStationSweepBonus", 0.15f,
                "Extra Q per sweep tick from surface radars of 50 km range or more and radar units (range-weighted).");
            RadarDwellQualityGainPerSecond = Bind(c, SectionQuality, "RadarDwellQualityGainPerSecond", 0.30f,
                "Q gained per second while any radar of the faction detects the target (range-weighted), in addition to the sweep ticks.");
            RwrQualityGain = Bind(c, SectionQuality, "RwrQualityGain", 0.005f,
                "Q gained per RWR pulse tick from any receiver of the faction, at close range (range-weighted).");
            RwrTier1QualityMultiplier = Bind(c, SectionQuality, "RwrTier1QualityMultiplier", 0.75f,
                "RWR quality gain multiplier for Tier 1 receivers (Tier 2 = 1.0).");
            RwrTier3QualityMultiplier = Bind(c, SectionQuality, "RwrTier3QualityMultiplier", 1.30f,
                "RWR quality gain multiplier for Tier 3 receivers.");
            VisualContactQuality = Bind(c, SectionQuality, "VisualContactQuality", 0.90f,
                "Q held while a spotter of the faction sees the target (vanilla optics); higher Q from other sensors stays.");
            StrategicBuildingVisualQuality = Bind(c, SectionQuality, "StrategicBuildingVisualQuality", 0.90f,
                "Q given by visual identification of a strategic building.");
            FirstFixQualityGain = Bind(c, SectionQuality, "FirstFixQualityGain", 0.40f,
                "Q gained when a passive bearing fix locates an emitter that the faction never located before (no earlier fix, " +
                "radar or visual contact).");
            MultiStationFixBonus = Bind(c, SectionQuality, "MultiStationFixBonus", 0.05f,
                "Extra Q when the fix comes from the bearings of two aircraft that share bearings over an Advanced datalink (Tier 3) " +
                "instead of one aircraft's flight baseline.");
            QualityGainFullWeightRange_m = Bind(c, SectionQuality, "QualityGainFullWeightRange_m", 5000.0f,
                "Range (m) up to which Q gains count fully.");
            QualityGainFloorRange_m = Bind(c, SectionQuality, "QualityGainFloorRange_m", 80000.0f,
                "Range (m) at which Q gains fall to QualityGainRangeWeightFloor.");
            QualityGainRangeWeightFloor = Bind(c, SectionQuality, "QualityGainRangeWeightFloor", 0.03f,
                "Weight of Q gains at QualityGainFloorRange_m and beyond.");
            QualityGainRangeWeightExponent = Bind(c, SectionQuality, "QualityGainRangeWeightExponent", 0.60f,
                "Shape of the range weighting between the full-weight and the floor range (< 1 drops quickly after the full-weight range).");
        }

        private static void BindWeaponDispersion(ConfigFile c)
        {
            EnableTrackUncertaintyDispersion = Bind(c, SectionDispersion, "EnableTrackUncertaintyDispersion", true,
                "Missiles and guided munitions aimed at a tracked threat or a tracked strategic building get their datum displaced by a " +
                "Gaussian miss with the CEP of the faction track. Other targets, guns and rockets are not displaced. FALSE: exact coordinates.");
            CepAtZeroQuality_m = Bind(c, SectionDispersion, "CepAtZeroQuality_m", 3000.0f,
                "CEP (m) at Q = 0. CEP = this x ((1 - CepDistanceWeight) x (1 - Q)^CepQualityExponent + CepDistanceWeight x distance / " +
                "CepDistanceReference_m), at least CepMin_m. Distance = launcher to target at launch. Half of the rounds fall inside the CEP. " +
                "Memory and fix errors act through Q.");
            CepMin_m = Bind(c, SectionDispersion, "CepMin_m", 10.0f,
                "Smallest CEP (m).");
            CepQualityExponent = Bind(c, SectionDispersion, "CepQualityExponent", 2.0f,
                "Shape of the Q term: the CEP falls with (1 - Q) to this power (2: Q 0.5 gives 25 % of the Q term, Q 0.8 gives 4 %).");
            CepDistanceWeight = Bind(c, SectionDispersion, "CepDistanceWeight", 0.05f,
                "Weight (0 to 1) of the distance term against the Q term in the CEP formula.");
            CepDistanceReference_m = Bind(c, SectionDispersion, "CepDistanceReference_m", 50000.0f,
                "Launch distance (m) at which the distance term is full; farther launches do not increase it.");
        }
    }
}
