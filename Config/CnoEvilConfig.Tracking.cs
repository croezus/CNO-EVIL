using BepInEx.Configuration;

namespace CnoEvil.Config
{
    public static partial class CnoEvilConfig
    {
        // 07. Target Tracking & Memory
        public static ConfigEntry<bool> EnableRWRTriangulationSystem;
        public static ConfigEntry<float> AircraftOpticsMagnificationTier1;
        public static ConfigEntry<float> AircraftOpticsMagnificationTier2;
        public static ConfigEntry<float> AircraftOpticsMagnificationTier3;
        public static ConfigEntry<float> AircraftMemoryDurationSeconds;
        public static ConfigEntry<float> GroundTargetMemoryDurationSeconds;
        public static ConfigEntry<float> StrategicBuildingVisualRangeMeters;
        public static ConfigEntry<float> StrategicBuildingMemorySeconds;
        public static ConfigEntry<float> PassiveRwrQualityCap;
        public static ConfigEntry<float> ActiveContactWindowSeconds;
        public static ConfigEntry<float> ContactHoldSeconds;
        public static ConfigEntry<float> BearingFixMaxErrorFraction;
        public static ConfigEntry<float> BearingObservationMaxAgeSeconds;

        // 08. Track Quality Balance
        public static ConfigEntry<float> SensorIntelligenceTickIntervalSeconds;
        public static ConfigEntry<float> RadarSweepBaseContribution;
        public static ConfigEntry<float> AlliedRadarSweepFactor;
        public static ConfigEntry<float> HeavyStationSweepBonus;
        public static ConfigEntry<float> RadarDwellContributionRate;
        public static ConfigEntry<float> RWRBaseContribution;
        public static ConfigEntry<float> AlliedRwrFactor;
        public static ConfigEntry<float> RwrTier1QualityMultiplier;
        public static ConfigEntry<float> RwrTier3QualityMultiplier;
        public static ConfigEntry<float> VisualReconnaissanceBoost;
        public static ConfigEntry<float> StrategicBuildingVisualQuality;
        public static ConfigEntry<float> TriangulationBaselineBoost;
        public static ConfigEntry<float> MultiStationFixBonus;
        public static ConfigEntry<float> TrackUncertaintyRangeWeightingMin;
        public static ConfigEntry<float> TrackUncertaintyRangeWeightingMax;
        public static ConfigEntry<float> RangeWeightFloor;
        public static ConfigEntry<float> RangeWeightExponent;

        // 09. Weapon Dispersion (CEP)
        public static ConfigEntry<bool> EnableTrackUncertaintyDispersion;
        public static ConfigEntry<float> TrackUncertaintyMinDispersionMeters;
        public static ConfigEntry<float> TrackUncertaintyMaxDispersionMeters;
        public static ConfigEntry<float> AngularErrorMaxDeg;
        public static ConfigEntry<float> AngularErrorMinDeg;
        public static ConfigEntry<float> UntriangulatedRangeErrorFraction;
        public static ConfigEntry<float> QualityDamping;
        public static ConfigEntry<float> MemoryDriftFactor;
        public static ConfigEntry<float> MinMemoryDriftSpeed;
        public static ConfigEntry<float> TrackUncertaintyEdgeBiasMinFraction;
        public static ConfigEntry<float> TrackUncertaintyEdgeBiasExponent;

        private const string SectionTracking = "07. Target Tracking & Memory";
        private const string SectionQuality = "08. Track Quality Balance";
        private const string SectionDispersion = "09. Weapon Dispersion (CEP)";

        private static void BindTargetTracking(ConfigFile c)
        {
            EnableRWRTriangulationSystem = Bind(c, SectionTracking, "EnableRWRTriangulationSystem", true,
                "Faction track system: passive bearing fixes, tracking quality Q, contact memory. Tracks cover aircraft, ships, air defense, radars and strategic buildings (Q only); they drive the HUD marker tint, map icons and AI launch doctrine. HUD markers and vanilla target knowledge stay vanilla.");
            AircraftOpticsMagnificationTier1 = Bind(c, SectionTracking, "AircraftOpticsMagnificationTier1", 1.0f,
                "Optics magnification of Tier 1 aircraft (no radar). Visual detection range = target visibility x magnification, not more than the vanilla visualRange of the aircraft. Replaces the vanilla magnification; vanilla detection uses it too.");
            AircraftOpticsMagnificationTier2 = Bind(c, SectionTracking, "AircraftOpticsMagnificationTier2", 1.5f,
                "Optics magnification of Tier 2 aircraft (radar, conventional airframe).");
            AircraftOpticsMagnificationTier3 = Bind(c, SectionTracking, "AircraftOpticsMagnificationTier3", 2.0f,
                "Optics magnification of Tier 3 aircraft (radar, stealth airframe).");
            AircraftMemoryDurationSeconds = Bind(c, SectionTracking, "AircraftMemoryDurationSeconds", 60.0f,
                "Seconds a lost aircraft contact is remembered at its last known position (Q decays to 0 over this time).");
            GroundTargetMemoryDurationSeconds = Bind(c, SectionTracking, "GroundTargetMemoryDurationSeconds", 180.0f,
                "Seconds a lost ground / naval contact is remembered at its last known position.");
            StrategicBuildingVisualRangeMeters = Bind(c, SectionTracking, "StrategicBuildingVisualRangeMeters", 20000.0f,
                "Range (m) at which enemy strategic buildings (factories, hangars, shelters, depots, ammo dumps, helipads, refineries) are visually identified, with terrain line of sight. Replaces their game visibleRange.");
            StrategicBuildingMemorySeconds = Bind(c, SectionTracking, "StrategicBuildingMemorySeconds", 300.0f,
                "Seconds a strategic building stays tracked after the last visual contact. Its Q does not decay in memory.");
            PassiveRwrQualityCap = Bind(c, SectionTracking, "PassiveRwrQualityCap", 0.20f,
                "Highest tracking quality Q (0..1) bearings alone can give before a fix, radar or visual contact.");
            ActiveContactWindowSeconds = Bind(c, SectionTracking, "ActiveContactWindowSeconds", 5.0f,
                "A track counts as in contact for this many seconds after its last detection; afterwards it is in memory.");
            ContactHoldSeconds = Bind(c, SectionTracking, "ContactHoldSeconds", 6.0f,
                "Seconds a radar sweep or RWR pulse keeps a track in contact (covers the gap between sweeps of rotating radars).");
            BearingFixMaxErrorFraction = Bind(c, SectionTracking, "BearingFixMaxErrorFraction", 0.15f,
                "A cross-bearing fix is accepted when its 1-sigma error is at most this fraction of the range (0.15 = 15%).");
            BearingObservationMaxAgeSeconds = Bind(c, SectionTracking, "BearingObservationMaxAgeSeconds", 6.0f,
                "Oldest bearing (s) still used for multi-station fixes; also how long a fix stays fresh.");
        }

        private static void BindTrackQuality(ConfigFile c)
        {
            SensorIntelligenceTickIntervalSeconds = Bind(c, SectionQuality, "SensorIntelligenceTickIntervalSeconds", 1.0f,
                "Minimum seconds between quality gains from radar sweeps and RWR pulses on one target.");
            RadarSweepBaseContribution = Bind(c, SectionQuality, "RadarSweepBaseContribution", 0.20f,
                "Q gained per ownship radar sweep tick at close range.");
            AlliedRadarSweepFactor = Bind(c, SectionQuality, "AlliedRadarSweepFactor", 0.75f,
                "Share of RadarSweepBaseContribution gained from an allied (datalinked) radar.");
            HeavyStationSweepBonus = Bind(c, SectionQuality, "HeavyStationSweepBonus", 0.15f,
                "Extra Q per sweep tick from heavy ground / naval search radars (range-weighted).");
            RadarDwellContributionRate = Bind(c, SectionQuality, "RadarDwellContributionRate", 0.30f,
                "Q gained per second of continuous radar illumination (range-weighted).");
            RWRBaseContribution = Bind(c, SectionQuality, "RWRBaseContribution", 0.005f,
                "Q gained per ownship RWR pulse tick at close range.");
            AlliedRwrFactor = Bind(c, SectionQuality, "AlliedRwrFactor", 0.57f,
                "Share of RWRBaseContribution gained from an allied receiver.");
            RwrTier1QualityMultiplier = Bind(c, SectionQuality, "RwrTier1QualityMultiplier", 0.75f,
                "RWR quality gain multiplier for Tier 1 receivers (Tier 2 = 1.0).");
            RwrTier3QualityMultiplier = Bind(c, SectionQuality, "RwrTier3QualityMultiplier", 1.30f,
                "RWR quality gain multiplier for Tier 3 receivers.");
            VisualReconnaissanceBoost = Bind(c, SectionQuality, "VisualReconnaissanceBoost", 0.90f,
                "Q held while a target is visually identified (vanilla optics of any spotter of the faction).");
            StrategicBuildingVisualQuality = Bind(c, SectionQuality, "StrategicBuildingVisualQuality", 0.90f,
                "Q given by visual identification of a strategic building.");
            TriangulationBaselineBoost = Bind(c, SectionQuality, "TriangulationBaselineBoost", 0.40f,
                "Q gained when a passive bearing fix first locates the emitter.");
            MultiStationFixBonus = Bind(c, SectionQuality, "MultiStationFixBonus", 0.05f,
                "Extra Q when the fix comes from two datalinked stations instead of one aircraft's flight baseline.");
            TrackUncertaintyRangeWeightingMin = Bind(c, SectionQuality, "TrackUncertaintyRangeWeightingMin", 5000.0f,
                "Range (m) up to which sensor contributions count fully.");
            TrackUncertaintyRangeWeightingMax = Bind(c, SectionQuality, "TrackUncertaintyRangeWeightingMax", 80000.0f,
                "Range (m) at which sensor contributions fall to RangeWeightFloor.");
            RangeWeightFloor = Bind(c, SectionQuality, "RangeWeightFloor", 0.03f,
                "Weight of sensor contributions at TrackUncertaintyRangeWeightingMax and beyond.");
            RangeWeightExponent = Bind(c, SectionQuality, "RangeWeightExponent", 0.60f,
                "Shape of the range weighting between min and max range (< 1 drops quickly after the min range).");
        }

        private static void BindWeaponDispersion(ConfigFile c)
        {
            EnableTrackUncertaintyDispersion = Bind(c, SectionDispersion, "EnableTrackUncertaintyDispersion", true,
                "Weapons are aimed at the target displaced by the faction's position error (CEP). FALSE: perfect coordinates.");
            TrackUncertaintyMinDispersionMeters = Bind(c, SectionDispersion, "TrackUncertaintyMinDispersionMeters", 15.0f,
                "Smallest CEP (m), for a fully refined radar track.");
            TrackUncertaintyMaxDispersionMeters = Bind(c, SectionDispersion, "TrackUncertaintyMaxDispersionMeters", 0.0f,
                "Largest CEP (m). 0 = no fixed cap (limited to 90% of the range).");
            AngularErrorMaxDeg = Bind(c, SectionDispersion, "AngularErrorMaxDeg", 12.0f,
                "Angular position error (deg) at Q = 0.");
            AngularErrorMinDeg = Bind(c, SectionDispersion, "AngularErrorMinDeg", 0.05f,
                "Angular position error (deg) at Q = 1.");
            UntriangulatedRangeErrorFraction = Bind(c, SectionDispersion, "UntriangulatedRangeErrorFraction", 0.25f,
                "Range error of a bearing-only track as a fraction of the range (at Q = 0, shrinking with Q).");
            QualityDamping = Bind(c, SectionDispersion, "QualityDamping", 0.92f,
                "How much a fully refined track (Q = 1) shrinks the CEP (CEP x (1 - damping x Q^2)).");
            MemoryDriftFactor = Bind(c, SectionDispersion, "MemoryDriftFactor", 0.50f,
                "CEP growth for a lost contact as a fraction of the distance it could have moved (speed x time).");
            MinMemoryDriftSpeed = Bind(c, SectionDispersion, "MinMemoryDriftSpeed_mps", 5.0f,
                "Drift speed (m/s) assumed for a lost contact that was stationary (relocating SAM sites).");
            TrackUncertaintyEdgeBiasMinFraction = Bind(c, SectionDispersion, "TrackUncertaintyEdgeBiasMinFraction", 0.60f,
                "Aim offsets fall between this fraction of the CEP and the full CEP (0.6 = never in the inner 60%).");
            TrackUncertaintyEdgeBiasExponent = Bind(c, SectionDispersion, "TrackUncertaintyEdgeBiasExponent", 0.40f,
                "Distribution of aim offsets inside that ring (< 1 favours the outer edge).");
        }
    }
}
