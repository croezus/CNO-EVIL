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
    /// Calculates horizontal-only (X/Z, Dy=0) Circular Error Probable (CEP) displacement
    /// for launched munitions (ARAD, ARH, SARH, and Optical).
    /// </summary>
    public static class TrackUncertaintyCalculator
    {
        /// <summary>
        /// Launch dispersion of one munition: Full is relative to the true position; WithoutFixError is the same
        /// draw from a CEP without the passive fix error, for a datum at a fix center (the fix error is already in it).
        /// </summary>
        private struct MissileDispersion
        {
            public Vector3 Full;
            public Vector3 WithoutFixError;
        }

        private static readonly Dictionary<int, MissileDispersion> _missileDispersionOffsets = new Dictionary<int, MissileDispersion>();

        /// <summary>
        /// Drift speed (m/s) assumed for a lost contact that was stationary.
        /// </summary>
        public static float MinMemoryDriftSpeed
        {
            get { return Mathf.Max(0.0f, CnoEvilConfig.MinMemoryDriftSpeed.Value); }
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
            get { return Mathf.Max(CnoEvilConfig.SensorIntelligenceTickIntervalSeconds.Value, 0.05f); }
        }

        #region Range Weighting & Distance Decay

        /// <summary>
        /// Range coefficient weighting factor: closer sensor observations provide significantly higher
        /// angular accuracy and signal-to-noise ratio than distant detections.
        /// Full weight up to TrackUncertaintyRangeWeightingMin, falling along a power curve (RangeWeightExponent)
        /// to RangeWeightFloor at TrackUncertaintyRangeWeightingMax, so long-range strobes accumulate little.
        /// </summary>
        public static float CalculateRangeWeight(float distanceMeters)
        {
            float minD = CnoEvilConfig.TrackUncertaintyRangeWeightingMin.Value;
            float maxD = CnoEvilConfig.TrackUncertaintyRangeWeightingMax.Value;

            if (distanceMeters <= minD) return 1.0f;
            if (maxD <= minD) maxD = minD + 1000.0f;

            float t = Mathf.Clamp01((distanceMeters - minD) / (maxD - minD));
            float curve = Mathf.Pow(t, Mathf.Max(0.01f, CnoEvilConfig.RangeWeightExponent.Value));
            return Mathf.Lerp(1.0f, Mathf.Clamp01(CnoEvilConfig.RangeWeightFloor.Value), curve);
        }

        /// <summary>
        /// Records the range from a sensor to the target. The closest sensor within ActiveContactWindowSeconds
        /// sets the range for the weapon CEP; in memory the last range stays.
        /// </summary>
        public static void RecordSensorRange(TriangulationTrack track, float distanceMeters)
        {
            if (track == null || distanceMeters <= 0.0f) return;
            float now = Time.timeSinceLevelLoad;
            bool stale = now - track.SensorRangeTime > CnoEvilConfig.ActiveContactWindowSeconds.Value;
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
            float configBase = Mathf.Clamp01(CnoEvilConfig.RadarSweepBaseContribution.Value);
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
            float configRate = Mathf.Max(CnoEvilConfig.RadarDwellContributionRate.Value, 0.0f);
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
        /// Raises quality to the visual floor (VisualReconnaissanceBoost, default 0.80) and keeps the target
        /// actively detected; it does not climb further without radar or RWR sensor input.
        /// </summary>
        public static void RecordVisualContact(TriangulationTrack track, float distanceMeters)
        {
            RecordVisualContact(track, CnoEvilConfig.VisualReconnaissanceBoost.Value, distanceMeters);
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
            track.LastVisualTickTime = now;
            track.HadActiveContact = true;
            track.IsTriangulated = true;
            RecordSensorRange(track, distanceMeters);

            float boost = Mathf.Clamp01(qualityFloor);

            // Direct visual identification establishes and sustains high confidence tracking (>= 0.80)
            track.TrackingQuality = Mathf.Max(track.TrackingQuality, boost);
        }

        /// <summary>
        /// Registers a passive RWR RF strobe detection from enemy radar emissions.
        /// Weighted by range coefficient (significant below 5 km) and receiver hardware tier.
        /// Progress ticks at most once per configurable tick interval (default: 1.0s) per target.
        /// Un-triangulated passive tracks are capped at PassiveRwrQualityCap (default: 0.20) to prevent
        /// artificial unmasking without geometric baseline or active sensor fusion.
        /// </summary>
        public static void RecordRWRDetection(TriangulationTrack track, float distanceMeters, RwrTier tier, bool isOwnship)
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

            float configBase = Mathf.Clamp01(CnoEvilConfig.RWRBaseContribution.Value);
            float baseTick = isOwnship ? configBase : (configBase * Mathf.Max(0.0f, CnoEvilConfig.AlliedRwrFactor.Value));
            float deltaQ = baseTick * rangeWeight * tierMult;

            float cap = track.IsTriangulated ? 1.0f : Mathf.Clamp01(CnoEvilConfig.PassiveRwrQualityCap.Value);

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
            float baseBoost = Mathf.Clamp01(CnoEvilConfig.TriangulationBaselineBoost.Value);
            float boost = isMultiStation ? (baseBoost + Mathf.Max(0.0f, CnoEvilConfig.MultiStationFixBonus.Value)) : baseBoost;

            track.TrackingQuality = Mathf.Clamp01(track.TrackingQuality + boost);
            track.LastActiveDetectionTime = now;
            track.HadActiveContact = true;
            track.IsTriangulated = true;
        }

        #endregion

        #region Dispersion & CEP Calculation

        /// <summary>
        /// Computes the horizontal Circular Error Probable (CEP) radius in meters
        /// from target distance, tracking quality (Q), triangulation status, and memory state.
        /// driftSpeedMps is how fast the lost contact may move (GetMemoryDriftSpeed); it drives dead-reckoning drift in memory.
        /// fixErrorMeters is the 1-sigma error of a passive bearing fix (0 for radar / visual contact).
        /// </summary>
        public static float CalculateDispersionRadius(float distanceMeters, float trackingQuality, bool isInMemoryState, float memoryElapsedSeconds, bool isTriangulated, float driftSpeedMps, float fixErrorMeters)
        {
            float q = Mathf.Clamp01(trackingQuality);

            // Angular position error from AngularErrorMaxDeg (Q = 0) down to AngularErrorMinDeg (Q = 1)
            float thetaMaxRad = Mathf.Clamp(CnoEvilConfig.AngularErrorMaxDeg.Value, 0.0f, 89.0f) * Mathf.Deg2Rad;
            float thetaMinRad = Mathf.Clamp(CnoEvilConfig.AngularErrorMinDeg.Value, 0.0f, 89.0f) * Mathf.Deg2Rad;
            float thetaEff = Mathf.Lerp(thetaMaxRad, thetaMinRad, q);

            float rawAngularError = distanceMeters * Mathf.Tan(thetaEff);

            // Range uncertainty: passive bearing-fix error when triangulated, single-strobe range ambiguity otherwise
            float rangeErr = isTriangulated ? Mathf.Max(0.0f, fixErrorMeters) : (distanceMeters * Mathf.Max(0.0f, CnoEvilConfig.UntriangulatedRangeErrorFraction.Value) * (1.0f - q));
            float totalBaseError = Mathf.Sqrt(rawAngularError * rawAngularError + rangeErr * rangeErr);

            // Non-linear collapse damping as Q approaches fire-control precision
            float qDamping = 1.0f - (Mathf.Clamp01(CnoEvilConfig.QualityDamping.Value) * q * q);
            float baseCep = totalBaseError * qDamping;

            // Stale memory expansion: the target may have moved up to |v|·t since last contact
            // (MemoryDriftFactor of that as CEP growth; buildings do not drift)
            if (isInMemoryState && memoryElapsedSeconds > 0f)
            {
                baseCep += Mathf.Max(0.0f, driftSpeedMps) * memoryElapsedSeconds * Mathf.Max(0.0f, CnoEvilConfig.MemoryDriftFactor.Value);
            }

            float minDisp = CnoEvilConfig.TrackUncertaintyMinDispersionMeters.Value;

            // Uncapped physical ceiling (optional manual override if TrackUncertaintyMaxDispersionMeters > 0)
            float maxDisp = (CnoEvilConfig.TrackUncertaintyMaxDispersionMeters.Value > 0f)
                ? CnoEvilConfig.TrackUncertaintyMaxDispersionMeters.Value
                : (distanceMeters * 0.90f);

            return Mathf.Clamp(baseCep, minDisp, Mathf.Max(minDisp, maxDisp));
        }

        /// <summary>
        /// Generates a random horizontal displacement vector (Dx, Dz, Dy=0) within the CEP circle.
        /// Altitude (Y) is strictly zero to preserve target height.
        /// offsetWithoutFixError is the same draw (direction and edge fraction) from the CEP without the passive
        /// fix error, for a datum at a fix center that already contains the fix error.
        /// </summary>
        public static Vector3 GenerateHorizontalDispersion(float distanceMeters, float trackingQuality, bool isInMemoryState, float memoryElapsedSeconds, bool isTriangulated, float driftSpeedMps, float fixErrorMeters, out Vector3 offsetWithoutFixError)
        {
            offsetWithoutFixError = Vector3.zero;
            if (!CnoEvilConfig.IsModActive) return Vector3.zero;
            if (!CnoEvilConfig.EnableTrackUncertaintyDispersion.Value) return Vector3.zero;

            float cepRadius = CalculateDispersionRadius(distanceMeters, trackingQuality, isInMemoryState, memoryElapsedSeconds, isTriangulated, driftSpeedMps, fixErrorMeters);
            float cepWithoutFix = CalculateDispersionRadius(distanceMeters, trackingQuality, isInMemoryState, memoryElapsedSeconds, isTriangulated, driftSpeedMps, 0.0f);

            // Peripheral / edge-biased radial distribution:
            // Munitions disperse preferentially towards the outer perimeter of the uncertainty disk (60% to 100%),
            // preventing un-triangulated launches from scoring lucky center hits.
            float minFrac = Mathf.Clamp01(CnoEvilConfig.TrackUncertaintyEdgeBiasMinFraction.Value);
            float exp = Mathf.Max(0.01f, CnoEvilConfig.TrackUncertaintyEdgeBiasExponent.Value);

            float phi = UnityEngine.Random.Range(0f, Mathf.PI * 2f);
            float u = UnityEngine.Random.value;
            float rFrac = Mathf.Lerp(minFrac, 1.0f, Mathf.Pow(u, exp));
            float cosPhi = Mathf.Cos(phi);
            float sinPhi = Mathf.Sin(phi);

            float rNoFix = cepWithoutFix * rFrac;
            offsetWithoutFixError = new Vector3(rNoFix * cosPhi, 0f, rNoFix * sinPhi);

            float r = cepRadius * rFrac;
            return new Vector3(r * cosPhi, 0f, r * sinPhi);
        }

        #endregion

        #region Missile Launch Dispersion Registry

        /// <summary>
        /// Records an initial launch dispersion vector for a specific munition instance.
        /// </summary>
        public static void RegisterMissileDispersion(Missile missile, Vector3 horizontalOffset, Vector3 offsetWithoutFixError)
        {
            if (missile == null) return;
            MissileDispersion d;
            d.Full = horizontalOffset;
            d.WithoutFixError = offsetWithoutFixError;
            _missileDispersionOffsets[missile.GetInstanceID()] = d;
        }

        /// <summary>
        /// Retrieves the registered launch dispersion vector for a munition (relative to the true position).
        /// </summary>
        public static bool TryGetMissileDispersion(Missile missile, out Vector3 offset)
        {
            return TryGetMissileDispersion(missile, true, out offset);
        }

        /// <summary>
        /// Retrieves the registered launch dispersion; includeFixError = false for a datum at a fix center.
        /// </summary>
        public static bool TryGetMissileDispersion(Missile missile, bool includeFixError, out Vector3 offset)
        {
            offset = Vector3.zero;
            if (missile == null) return false;
            MissileDispersion d;
            if (!_missileDispersionOffsets.TryGetValue(missile.GetInstanceID(), out d)) return false;
            offset = includeFixError ? d.Full : d.WithoutFixError;
            return true;
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
