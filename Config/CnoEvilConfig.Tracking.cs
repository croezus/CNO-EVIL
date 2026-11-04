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
        public static ConfigEntry<float> ContactHold_s;
        public static ConfigEntry<float> BearingFixMaxErrorFraction;
        public static ConfigEntry<float> BearingObservationMaxAge_s;
        public static ConfigEntry<float> SingleShipBaselineMaxAge_s;
        public static ConfigEntry<float> BearingIndependentSamples;
        public static ConfigEntry<float> LocatedFixMaxErrorFraction;
        public static ConfigEntry<float> Tier4TmaWindow_s;
        public static ConfigEntry<float> LocatedQualityThreshold;
        public static ConfigEntry<float> HMDMarkerQualityThreshold;
        public static ConfigEntry<float> MinMemoryDriftSpeed_mps;

        // 08. Track Quality Balance
        public static ConfigEntry<float> RadarQualityRate_per_s;
        public static ConfigEntry<float> RadarQualityRefRange_m;
        public static ConfigEntry<float> RadarQualityRangeExponent;
        public static ConfigEntry<float> SurfaceRadarQualityFactor;
        public static ConfigEntry<float> AlliedRadarSweepFactor;
        public static ConfigEntry<float> RwrStrobeQualityRate_per_s;
        public static ConfigEntry<float> RwrTier1QualityMultiplier;
        public static ConfigEntry<float> RwrTier3QualityMultiplier;
        public static ConfigEntry<float> RwrTier4QualityMultiplier;
        public static ConfigEntry<float> VisualContactQuality;
        public static ConfigEntry<float> StrategicBuildingVisualQuality;
        public static ConfigEntry<float> VisualContactNearQuality;
        public static ConfigEntry<float> VisualContactNearRange_m;
        public static ConfigEntry<float> VisualContactFarRange_m;
        public static ConfigEntry<float> LaserContactQuality;
        public static ConfigEntry<float> QualityGainFullWeightRange_m;
        public static ConfigEntry<float> QualityGainFloorRange_m;
        public static ConfigEntry<float> QualityGainRangeWeightFloor;
        public static ConfigEntry<float> QualityGainRangeWeightExponent;

        // 09. Weapon Dispersion (CEP)
        public static ConfigEntry<bool> EnableTrackUncertaintyDispersion;
        public static ConfigEntry<float> CepAtZeroQuality_m;
        public static ConfigEntry<float> CepMin_m;
        public static ConfigEntry<float> CepQualityExponent;
        public static ConfigEntry<float> CepDistanceMax_m;
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
            AircraftMemory_s = Bind(c, SectionTracking, "AircraftMemory_s", 120.0f,
                "Time (s) a lost aircraft contact stays at its last known position. The track is dropped this long after the last " +
                "detection. In memory, Q stays at its last value. A new detection moves the contact to the current position.");
            GroundTargetMemory_s = Bind(c, SectionTracking, "GroundTargetMemory_s", 600.0f,
                "The same for ground and naval contacts. A located radar building is never dropped while it exists.");
            StrategicBuildingVisualRange_m = Bind(c, SectionTracking, "StrategicBuildingVisualRange_m", 20000.0f,
                "Range (m) of the faction scan that visually identifies enemy strategic buildings (factories, hangars, shelters, depots, " +
                "ammo dumps, helipads, refineries), with terrain line of sight. Vanilla spotters also identify them at their own range. " +
                "The scan runs in the datalink cycle (EnableAlliedDatalink).");
            StrategicBuildingMemory_s = Bind(c, SectionTracking, "StrategicBuildingMemory_s", 300.0f,
                "Seconds a strategic building stays tracked after the last visual contact while its Q is below " +
                "LocatedQualityThreshold. At or above it, the building stays as a map and HUD ghost until it is destroyed. Its Q " +
                "does not decay in memory.");
            ContactHold_s = Bind(c, SectionTracking, "ContactHold_s", 6.0f,
                "A track stays in contact for this many seconds after its last radar detection, RWR pulse or visual contact " +
                "(covers the gap between sweeps of rotating radars). Q does not decay in contact; afterwards the track is in memory.");
            BearingFixMaxErrorFraction = Bind(c, SectionTracking, "BearingFixMaxErrorFraction", 0.15f,
                "A cross-bearing fix is accepted when its 1-sigma error is at most this fraction of the range (0.15 = 15%).");
            BearingObservationMaxAge_s = Bind(c, SectionTracking, "BearingObservationMaxAge_s", 6.0f,
                "Oldest bearing (s) still used for multi-station fixes, and how long a fix stays fresh (fix smoothing, the faction " +
                "position estimate). For this long after a radar or visual contact no passive fix is made.");
            SingleShipBaselineMaxAge_s = Bind(c, SectionTracking, "SingleShipBaselineMaxAge_s", 180.0f,
                "Oldest own bearing (s) of a pilot that the single-ship fix uses. The fix is a least-squares fit of all own bearings " +
                "in this window (one each 0.5 s). For a moving emitter the window is shorter: the emitter motion in it must stay " +
                "below half of BearingFixMaxErrorFraction x range. Tier 1 receivers make no single-ship fix.");
            BearingIndependentSamples = Bind(c, SectionTracking, "BearingIndependentSamples", 16.0f,
                "Number of own bearings that count as independent in the single-ship fix. More bearings give no more accuracy " +
                "(the bearing errors of one receiver are not fully independent). The fix error falls with the square root of the " +
                "bearings up to this number.");
            LocatedFixMaxErrorFraction = Bind(c, SectionTracking, "LocatedFixMaxErrorFraction", 0.05f,
                "A passive fix with a 1-sigma error at most this fraction of the range (0.05 = 5 %) locates the emitter for the map " +
                "icon, the vanilla tracking entry and the building ghost, also when Q is below LocatedQualityThreshold. The located " +
                "state stays until the track is forgotten. Weapons use Q only.");
            Tier4TmaWindow_s = Bind(c, SectionTracking, "Tier4TmaWindow_s", 60.0f,
                "Window (s) of the Tier 4 motion analysis on an aircraft emitter: a least-squares fit of position and constant " +
                "velocity to the own bearings in this window. It needs an own turn (dogleg) in the window; a straight flight gives " +
                "no fix. A target that turns in the window makes the fix wrong.");
            LocatedQualityThreshold = Bind(c, SectionTracking, "LocatedQualityThreshold", 0.55f,
                "Tracking quality Q (0..1) from which the faction has located a target without a recent radar / visual contact. " +
                "A located target gets a map icon that can be selected (HideUnlocatedThreatIcons), and its map RWR zone shrinks " +
                "to a cone. The HMD marker has its own threshold (HMDMarkerQualityThreshold).");
            HMDMarkerQualityThreshold = Bind(c, SectionTracking, "HMDMarkerQualityThreshold", 0.35f,
                "Tracking quality Q (0..1) from which an enemy tracked threat without a recent radar / visual contact gets a " +
                "vanilla tracking entry from RWR pings and an HMD marker that can be selected. Below it, the HMD marker is hidden " +
                "and cannot be selected (HideUnlocatedThreatIcons). A located target always has the marker.");
            MinMemoryDriftSpeed_mps = Bind(c, SectionTracking, "MinMemoryDriftSpeed_mps", 5.0f,
                "Drift speed (m/s) assumed for a lost moving contact that was slower than this. A contact that was stationary " +
                "(slower than 1 m/s, a SAM site that can relocate) gets half of this value. Sets the position error of the faction " +
                "estimate of a contact in memory (AI launch check, HUD cues, map aim points, stand-off jamming). The error is not " +
                "more than the forget displacement. Buildings do not drift.");
        }

        private static void BindTrackQuality(ConfigFile c)
        {
            RadarQualityRate_per_s = Bind(c, SectionQuality, "RadarQualityRate_per_s", 0.30f,
                "Fill rate (1/s) of Q from one own airborne radar at RadarQualityRefRange_m or nearer. Every sensor of the faction " +
                "that sees the target adds its rate R, and Q fills as Q = 1 - (1 - Q) x exp(-sum R x dt): with 0.30, Q 0.5 after " +
                "2.3 s, 0.9 after 7.7 s. Three radars fill Q three times as fast as one.");
            RadarQualityRefRange_m = Bind(c, SectionQuality, "RadarQualityRefRange_m", 20000.0f,
                "Range (m) up to which a radar gives its full fill rate. Farther, the rate falls with (this / range) to the power " +
                "RadarQualityRangeExponent.");
            RadarQualityRangeExponent = Bind(c, SectionQuality, "RadarQualityRangeExponent", 1.0f,
                "Shape of the radar fill rate fall with range (1: half the rate at twice RadarQualityRefRange_m).");
            SurfaceRadarQualityFactor = Bind(c, SectionQuality, "SurfaceRadarQualityFactor", 1.5f,
                "Fill rate multiplier of surface radars of 50 km range or more and radar units (better accuracy than an airborne radar).");
            AlliedRadarSweepFactor = Bind(c, SectionQuality, "AlliedRadarSweepFactor", 0.75f,
                "Fill rate multiplier of any radar of the faction that is not the radar of a human pilot (AI, ground stations; the " +
                "contact comes through the datalink).");
            RwrStrobeQualityRate_per_s = Bind(c, SectionQuality, "RwrStrobeQualityRate_per_s", 0.02f,
                "Fill rate (1/s) of Q from the RWR pulses of one receiver at close range (range-weighted, times the tier multiplier). " +
                "All receivers of the faction that got a pulse in the last 1.5 s add their rates. There is no cap: a long run of " +
                "bearings fills Q also without a fix (Tier 2 at 20 km: Q 0.5 after about 55 s).");
            RwrTier1QualityMultiplier = Bind(c, SectionQuality, "RwrTier1QualityMultiplier", 0.75f,
                "RWR fill rate multiplier for Tier 1 receivers (Tier 2 = 1.0).");
            RwrTier3QualityMultiplier = Bind(c, SectionQuality, "RwrTier3QualityMultiplier", 1.30f,
                "RWR fill rate multiplier for Tier 3 receivers.");
            RwrTier4QualityMultiplier = Bind(c, SectionQuality, "RwrTier4QualityMultiplier", 1.60f,
                "RWR fill rate multiplier for Tier 4 (ELINT) receivers.");
            VisualContactQuality = Bind(c, SectionQuality, "VisualContactQuality", 0.90f,
                "Q held while a spotter of the faction sees a moving target (vanilla optics) at VisualContactFarRange_m or " +
                "farther; nearer, it rises to VisualContactNearQuality. Higher Q from other sensors stays.");
            StrategicBuildingVisualQuality = Bind(c, SectionQuality, "StrategicBuildingVisualQuality", 0.90f,
                "Q given by visual identification of a strategic building at VisualContactFarRange_m and farther.");
            VisualContactNearQuality = Bind(c, SectionQuality, "VisualContactNearQuality", 1.0f,
                "Q held by a visual contact at VisualContactNearRange_m or nearer, and by a visual contact of a building or a " +
                "stationary unit at any range. Between the near and the far range it falls to VisualContactQuality " +
                "(StrategicBuildingVisualQuality for strategic buildings).");
            VisualContactNearRange_m = Bind(c, SectionQuality, "VisualContactNearRange_m", 5000.0f,
                "Spotter range (m) up to which a visual contact holds VisualContactNearQuality.");
            VisualContactFarRange_m = Bind(c, SectionQuality, "VisualContactFarRange_m", 15000.0f,
                "Spotter range (m) from which a visual contact of a moving unit holds only VisualContactQuality.");
            LaserContactQuality = Bind(c, SectionQuality, "LaserContactQuality", 1.0f,
                "Q held while a laser designator of the faction lases the target (vanilla FactionHQ lased state). A weapon " +
                "fired on a lased target gets CepMin_m (no distance term).");
            QualityGainFullWeightRange_m = Bind(c, SectionQuality, "QualityGainFullWeightRange_m", 5000.0f,
                "Receiver range (m) up to which the RWR fill rate counts fully.");
            QualityGainFloorRange_m = Bind(c, SectionQuality, "QualityGainFloorRange_m", 80000.0f,
                "Receiver range (m) at which the RWR fill rate falls to QualityGainRangeWeightFloor.");
            QualityGainRangeWeightFloor = Bind(c, SectionQuality, "QualityGainRangeWeightFloor", 0.03f,
                "Weight of the RWR fill rate at QualityGainFloorRange_m and beyond.");
            QualityGainRangeWeightExponent = Bind(c, SectionQuality, "QualityGainRangeWeightExponent", 0.60f,
                "Shape of the range weighting between the full-weight and the floor range (< 1 drops quickly after the full-weight range).");
        }

        private static void BindWeaponDispersion(ConfigFile c)
        {
            EnableTrackUncertaintyDispersion = Bind(c, SectionDispersion, "EnableTrackUncertaintyDispersion", true,
                "Missiles and guided munitions aimed at a tracked threat or a tracked strategic building get their datum displaced by a " +
                "Gaussian miss with the CEP of the faction track. Other targets, guns and rockets are not displaced. FALSE: exact coordinates.");
            CepAtZeroQuality_m = Bind(c, SectionDispersion, "CepAtZeroQuality_m", 3000.0f,
                "CEP (m) of the Q term at Q = 0. CEP = sqrt((this x (1 - Q)^CepQualityExponent)^2 + (CepDistanceMax_m x " +
                "min(1, distance / CepDistanceReference_m) x (1 - Q))^2), at least CepMin_m. Distance = launcher to target at launch. " +
                "Half of the rounds fall inside the CEP. A passive fix sets Q so that the Q term is the CEP of the fix.");
            CepMin_m = Bind(c, SectionDispersion, "CepMin_m", 2.0f,
                "Smallest CEP (m): the CEP at Q = 1 and on a lased target.");
            CepQualityExponent = Bind(c, SectionDispersion, "CepQualityExponent", 2.0f,
                "Shape of the Q term: the CEP falls with (1 - Q) to this power (2: Q 0.5 gives 25 % of the Q term, Q 0.8 gives 4 %).");
            CepDistanceMax_m = Bind(c, SectionDispersion, "CepDistanceMax_m", 300.0f,
                "CEP (m) of the distance term at CepDistanceReference_m and Q = 0. The term falls with (1 - Q), so a full-quality " +
                "track gets no distance error. It adds to the Q term in quadrature (independent errors).");
            CepDistanceReference_m = Bind(c, SectionDispersion, "CepDistanceReference_m", 50000.0f,
                "Launch distance (m) at which the distance term is full; farther launches do not increase it.");
        }
    }
}
