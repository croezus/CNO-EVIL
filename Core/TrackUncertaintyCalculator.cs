using System;
using System.Collections.Generic;
using UnityEngine;
using CnoEvil.Config;
using CnoEvil.Components;

namespace CnoEvil.Core
{
    /// <summary>
    /// Core intelligence accumulation and weapon dispersion engine.
    /// Manages team-wide sensor fusion progress (Q: 0.0 to 1.0) across allied radars,
    /// visual spotters, and RWR receivers with range-dependent weighting and per-target memory retention.
    /// The weapon CEP comes from Q (95 %) and the distance (5 %); each munition gets one Gaussian
    /// horizontal miss (X/Z, Dy=0) inside that CEP.
    /// </summary>
    public static class TrackUncertaintyCalculator
    {
        private static readonly Dictionary<int, Vector3> _missileDispersionOffsets = new Dictionary<int, Vector3>();

        /// <summary>
        /// Drift speed (m/s) assumed for a lost contact that was stationary.
        /// </summary>
        public static float MinMemoryDriftSpeed
        {
            get { return Mathf.Max(0.0f, CnoEvilConfig.MinMemoryDriftSpeed_mps.Value); }
        }

        /// <summary>
        /// Speed (m/s) a lost contact may have moved since the last contact: 0 for buildings,
        /// otherwise the speed at last contact with the MinMemoryDriftSpeed floor.
        /// </summary>
        public static float GetMemoryDriftSpeed(Unit target, float knownSpeedMps)
        {
            if (target is Building) return 0.0f;
            return Mathf.Max(MinMemoryDriftSpeed, knownSpeedMps);
        }

        private static float TickInterval
        {
            get { return Mathf.Max(CnoEvilConfig.QualityGainTick_s.Value, 0.05f); }
        }

        #region Range Weighting & Distance Decay

        /// <summary>
        /// Range coefficient weighting factor: closer sensor observations provide significantly higher
        /// angular accuracy and signal-to-noise ratio than distant detections.
        /// Full weight up to QualityGainFullWeightRange_m, falling along a power curve (QualityGainRangeWeightExponent)
        /// to QualityGainRangeWeightFloor at QualityGainFloorRange_m, so long-range strobes accumulate little.
        /// </summary>
        public static float CalculateRangeWeight(float distanceMeters)
        {
            float minD = CnoEvilConfig.QualityGainFullWeightRange_m.Value;
            float maxD = CnoEvilConfig.QualityGainFloorRange_m.Value;

            if (distanceMeters <= minD) return 1.0f;
            if (maxD <= minD) maxD = minD + 1000.0f;

            float t = Mathf.Clamp01((distanceMeters - minD) / (maxD - minD));
            float curve = Mathf.Pow(t, Mathf.Max(0.01f, CnoEvilConfig.QualityGainRangeWeightExponent.Value));
            return Mathf.Lerp(1.0f, Mathf.Clamp01(CnoEvilConfig.QualityGainRangeWeightFloor.Value), curve);
        }

        /// <summary>
        /// Records the range from a sensor to the target. The closest sensor within ContactHold_s
        /// sets the range for the weapon CEP; in memory the last range stays.
        /// </summary>
        public static void RecordSensorRange(TriangulationTrack track, float distanceMeters)
        {
            if (track == null || distanceMeters <= 0.0f) return;
            float now = Time.timeSinceLevelLoad;
            bool stale = now - track.SensorRangeTime > CnoEvilConfig.ContactHold_s.Value;
            if (stale || track.SensorRangeMeters <= 0.0f || distanceMeters <= track.SensorRangeMeters)
            {
                track.SensorRangeMeters = distanceMeters;
                track.SensorRangeTime = now;
            }
        }

        #endregion

        #region Team-Wide Intelligence Accumulation Ticks

        /// <summary>
        /// Registers an active radar sweep pulse on the target from an ownship or allied radar.
        /// Progress ticks at most once per configurable tick interval (default: 1.0s) per target.
        /// Returns true when this call produced a tick.
        /// </summary>
        public static bool RecordRadarSweep(TriangulationTrack track, float distanceMeters, bool isOwnship)
        {
            if (track == null) return false;
            float now = Time.timeSinceLevelLoad;
            track.LastActiveDetectionTime = now;
            track.LastRadarIlluminatedTime = now;
            track.HadActiveContact = true;
            track.IsTriangulated = true;
            RecordSensorRange(track, distanceMeters);

            // Enforce discrete tick rate
            if (now - track.LastRadarTickTime < TickInterval) return false;
            track.LastRadarTickTime = now;

            float rangeWeight = CalculateRangeWeight(distanceMeters);
            float configBase = Mathf.Clamp01(CnoEvilConfig.RadarSweepQualityGain.Value);
            float baseTick = isOwnship ? configBase : (configBase * Mathf.Max(0.0f, CnoEvilConfig.AlliedRadarSweepFactor.Value));
            float deltaQ = baseTick * rangeWeight;

            track.TrackingQuality = Mathf.Clamp01(track.TrackingQuality + deltaQ);
            return true;
        }

        /// <summary>
        /// Continuously refines tracking quality during sustained radar dwell (pencil-beam track).
        /// </summary>
        public static void RecordRadarDwell(TriangulationTrack track, float dt, float distanceMeters)
        {
            if (track == null) return;
            float now = Time.timeSinceLevelLoad;
            float rangeWeight = CalculateRangeWeight(distanceMeters);
            float configRate = Mathf.Max(CnoEvilConfig.RadarDwellQualityGainPerSecond.Value, 0.0f);
            float ratePerSecond = configRate * rangeWeight;

            track.TrackingQuality = Mathf.Clamp01(track.TrackingQuality + ratePerSecond * dt);
            track.LastActiveDetectionTime = now;
            track.LastRadarIlluminatedTime = now;
            track.HadActiveContact = true;
            track.IsTriangulated = true;
            RecordSensorRange(track, distanceMeters);
        }

        /// <summary>
        /// Registers positive visual reconnaissance from an allied spotter (aircraft, vehicle, ship).
        /// Raises quality to the visual floor (VisualContactQuality, default 0.90) and keeps the target
        /// actively detected; it does not climb further without radar or RWR sensor input.
        /// </summary>
        public static void RecordVisualContact(TriangulationTrack track, float distanceMeters)
        {
            RecordVisualContact(track, CnoEvilConfig.VisualContactQuality.Value, distanceMeters);
        }

        /// <summary>
        /// Visual contact that raises quality to qualityFloor (StrategicBuildingVisualQuality for strategic buildings).
        /// distanceMeters is the spotter range (0 = unknown).
        /// </summary>
        public static void RecordVisualContact(TriangulationTrack track, float qualityFloor, float distanceMeters)
        {
            if (track == null) return;
            float now = Time.timeSinceLevelLoad;
            track.LastActiveDetectionTime = now;
            track.HadActiveContact = true;
            track.IsTriangulated = true;
            RecordSensorRange(track, distanceMeters);

            float boost = Mathf.Clamp01(qualityFloor);

            // Direct visual identification establishes and sustains high confidence tracking (the visual floor)
            track.TrackingQuality = Mathf.Max(track.TrackingQuality, boost);
        }

        /// <summary>
        /// Registers a passive RWR RF strobe detection from enemy radar emissions.
        /// Weighted by range coefficient (significant below 5 km) and receiver hardware tier.
        /// Progress ticks at most once per configurable tick interval (default: 1.0s) per target.
        /// Without a current radar / visual contact or a fresh bearing fix the gain stops at PassiveRwrQualityCap
        /// (default: 0.20): bearings alone do not locate the emitter.
        /// </summary>
        /// <summary>
        /// The faction knows where the emitter is right now: a radar / visual contact within ContactHold_s or a
        /// bearing fix within BearingObservationMaxAge_s.
        /// </summary>
        private static bool IsLocatedNow(TriangulationTrack track, float now)
        {
            if (track.LastPreciseContactTime > 0.0f && now - track.LastPreciseContactTime < CnoEvilConfig.ContactHold_s.Value) return true;
            return track.LastFixTime > 0.0f && now - track.LastFixTime < CnoEvilConfig.BearingObservationMaxAge_s.Value;
        }

        public static void RecordRWRDetection(TriangulationTrack track, float distanceMeters, RwrTier tier)
        {
            if (track == null) return;
            float now = Time.timeSinceLevelLoad;
            track.LastActiveDetectionTime = now;
            track.HadActiveContact = true;
            RecordSensorRange(track, distanceMeters);

            // Enforce discrete tick rate
            if (now - track.LastRwrTickTime < TickInterval) return;
            track.LastRwrTickTime = now;

            float rangeWeight = CalculateRangeWeight(distanceMeters);

            float tierMult = 1.0f;
            if (tier == RwrTier.Tier1_Basic) tierMult = Mathf.Max(0.0f, CnoEvilConfig.RwrTier1QualityMultiplier.Value);
            else if (tier == RwrTier.Tier3_Advanced) tierMult = Mathf.Max(0.0f, CnoEvilConfig.RwrTier3QualityMultiplier.Value);

            float deltaQ = Mathf.Clamp01(CnoEvilConfig.RwrQualityGain.Value) * rangeWeight * tierMult;

            float cap = IsLocatedNow(track, now) ? 1.0f : Mathf.Clamp01(CnoEvilConfig.PassiveRwrQualityCap.Value);

            if (track.TrackingQuality < cap)
            {
                track.TrackingQuality = Mathf.Min(cap, track.TrackingQuality + deltaQ);
            }
        }

        /// <summary>
        /// Grants an intelligence boost upon successful geometric triangulation
        /// (either kinematic cross-bearing flight or multi-station datalink intersection).
        /// </summary>
        public static void RecordTriangulationBoost(TriangulationTrack track, bool isMultiStation)
        {
            if (track == null) return;
            float now = Time.timeSinceLevelLoad;
            float baseBoost = Mathf.Clamp01(CnoEvilConfig.FirstFixQualityGain.Value);
            float boost = isMultiStation ? (baseBoost + Mathf.Max(0.0f, CnoEvilConfig.MultiStationFixBonus.Value)) : baseBoost;

            track.TrackingQuality = Mathf.Clamp01(track.TrackingQuality + boost);
            track.LastActiveDetectionTime = now;
            track.HadActiveContact = true;
            track.IsTriangulated = true;
        }

        #endregion

        #region Dispersion & CEP Calculation

        // CEP radius = median miss distance: a 2D Gaussian with sigma = CEP / sqrt(2 ln 2)
        private static readonly float Ln2 = Mathf.Log(2.0f);

        // Largest drawn miss, in CEP (3 CEP holds 99.8 % of the draws)
        private const float MaxDispersionInCep = 3.0f;

        /// <summary>
        /// Horizontal CEP (m) of a weapon aimed on a track of quality Q at distanceMeters:
        /// CEP = CepAtZeroQuality * ((1 - w) * (1 - Q)^p + w * clamp01(distance / CepDistanceReference)),
        /// w = CepDistanceWeight (weight of the distance term against the Q term), not less than CepMin.
        /// Track memory and passive fixes act through Q (it decays in memory, a first fix adds FirstFixQualityGain).
        /// </summary>
        public static float CalculateCep(float trackingQuality, float distanceMeters)
        {
            float q = Mathf.Clamp01(trackingQuality);
            float cepMax = Mathf.Max(0.0f, CnoEvilConfig.CepAtZeroQuality_m.Value);
            float cepMin = Mathf.Max(0.0f, CnoEvilConfig.CepMin_m.Value);
            float p = Mathf.Max(0.01f, CnoEvilConfig.CepQualityExponent.Value);
            float w = Mathf.Clamp01(CnoEvilConfig.CepDistanceWeight.Value);
            float dRef = Mathf.Max(1.0f, CnoEvilConfig.CepDistanceReference_m.Value);

            float qualityTerm = Mathf.Pow(1.0f - q, p);
            float distanceTerm = Mathf.Clamp01(Mathf.Max(0.0f, distanceMeters) / dRef);
            return Mathf.Max(cepMin, cepMax * ((1.0f - w) * qualityTerm + w * distanceTerm));
        }

        /// <summary>
        /// Probability that a round with this CEP lands within basketRadius of the target (Rayleigh distribution):
        /// P = 1 - 0.5^((R / CEP)^2).
        /// </summary>
        public static float HitProbability(float basketRadius, float cep)
        {
            if (float.IsPositiveInfinity(basketRadius)) return 1.0f;
            if (basketRadius <= 0.0f) return 0.0f;
            if (cep <= 0.001f) return 1.0f;
            float k = basketRadius / cep;
            return 1.0f - Mathf.Pow(0.5f, k * k);
        }

        /// <summary>
        /// Random horizontal miss (Dx, 0, Dz) of a 2D Gaussian with this CEP: half of the draws fall inside the CEP.
        /// The miss is limited to 3 CEP. Altitude (Y) is zero, so the datum keeps the target height.
        /// </summary>
        public static Vector3 GenerateHorizontalDispersion(float cep)
        {
            if (!CnoEvilConfig.IsModActive) return Vector3.zero;
            if (!CnoEvilConfig.EnableTrackUncertaintyDispersion.Value || cep <= 0.0f) return Vector3.zero;

            // Inverse of the Rayleigh CDF with median CEP: r = CEP * sqrt(-ln(1 - u) / ln 2)
            float u = UnityEngine.Random.value;
            float r = cep * Mathf.Sqrt(-Mathf.Log(Mathf.Max(1e-6f, 1.0f - u)) / Ln2);
            r = Mathf.Min(r, cep * MaxDispersionInCep);

            float phi = UnityEngine.Random.Range(0f, Mathf.PI * 2f);
            return new Vector3(r * Mathf.Cos(phi), 0f, r * Mathf.Sin(phi));
        }

        #endregion

        #region Missile Launch Dispersion Registry

        /// <summary>
        /// Records the launch dispersion of one munition (drawn once, at launch).
        /// </summary>
        public static void RegisterMissileDispersion(Missile missile, Vector3 horizontalOffset)
        {
            if (missile == null) return;
            _missileDispersionOffsets[missile.GetInstanceID()] = horizontalOffset;
        }

        /// <summary>
        /// Retrieves the registered launch dispersion of a munition (offset of its datum from the known target position).
        /// </summary>
        public static bool TryGetMissileDispersion(Missile missile, out Vector3 offset)
        {
            offset = Vector3.zero;
            if (missile == null) return false;
            return _missileDispersionOffsets.TryGetValue(missile.GetInstanceID(), out offset);
        }

        /// <summary>
        /// Unregisters a munition upon detonation, splash, or destruction.
        /// </summary>
        public static void UnregisterMissile(Missile missile)
        {
            if (missile == null) return;
            _missileDispersionOffsets.Remove(missile.GetInstanceID());
        }

        /// <summary>
        /// Clears all stored missile dispersion offsets (e.g. level reload).
        /// </summary>
        public static void Clear()
        {
            _missileDispersionOffsets.Clear();
        }

        #endregion
    }
}
