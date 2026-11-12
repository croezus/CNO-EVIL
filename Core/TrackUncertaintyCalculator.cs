using System;
using System.Collections.Generic;
using UnityEngine;
using CnoEvil.Config;
using CnoEvil.Components;

namespace CnoEvil.Core
{
    /// <summary>
    /// Track quality and weapon dispersion engine (ADR-44).
    /// Q (0..1) of a faction track fills while sensors of the faction see the target: every sensor adds a fill rate R
    /// (radar: by range, accuracy and channel; RWR: by range and tier), and Q = 1 - (1 - Q) x exp(-sum R x dt).
    /// Visual contact, laser and passive fixes set a Q floor. The weapon CEP comes from Q and the launch distance
    /// (two independent terms added in quadrature); each munition gets one Gaussian draw in CEP units, scaled by the CEP
    /// of the faction track while the datalink has a contact.
    /// </summary>
    public static class TrackUncertaintyCalculator
    {
        /// <summary>
        /// Launch dispersion of one munition: a draw in CEP units (Unit x CepScale = offset), or a raw offset
        /// (CepScale 0: received from the host, or zero for a target the mod does not guide).
        /// </summary>
        private struct DispersionEntry
        {
            public Vector3 Unit;           // Draw in CEP units (X/Z), or the raw offset when CepScale is 0
            public float CepScale;         // CEP (m) the draw is scaled by; 0 = raw offset
            public float LaunchDistance;   // Launcher to target at launch (distance term of the CEP)
            public float LimitMeters;      // Largest offset (vanilla datum rules); infinity = none
        }

        private static readonly Dictionary<int, DispersionEntry> _missileDispersion = new Dictionary<int, DispersionEntry>();

        // A sensor contribution counts for this long after its last report (covers the radar scan and RWR pulse intervals)
        public const float SensorRateHoldSeconds = 1.5f;

        // A unit slower than this (m/s) counts as stationary (visual contact quality, memory drift)
        private const float StationarySpeed = 1.0f;

        /// <summary>
        /// Drift speed (m/s) assumed for a lost contact that was moving slower than this.
        /// </summary>
        public static float MinMemoryDriftSpeed
        {
            get { return Mathf.Max(0.0f, CnoEvilConfig.MinMemoryDriftSpeed_mps.Value); }
        }

        /// <summary>
        /// Speed (m/s) a lost contact may have moved since the last contact: 0 for buildings, half of MinMemoryDriftSpeed
        /// for a contact that was stationary (a SAM site that can relocate), otherwise the speed at last contact with the
        /// MinMemoryDriftSpeed floor.
        /// </summary>
        public static float GetMemoryDriftSpeed(Unit target, float knownSpeedMps)
        {
            if (target is Building) return 0.0f;
            if (knownSpeedMps < StationarySpeed) return 0.5f * MinMemoryDriftSpeed;
            return Mathf.Max(MinMemoryDriftSpeed, knownSpeedMps);
        }

        #region Range Weighting

        /// <summary>
        /// Range weight of the RWR fill rate: full up to QualityGainFullWeightRange_m, falling along a power curve
        /// (QualityGainRangeWeightExponent) to QualityGainRangeWeightFloor at QualityGainFloorRange_m.
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
        /// Records the range from a sensor to the target. The closest sensor within ContactHold_s sets the range for the
        /// map uncertainty ring; in memory the last range stays.
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

        #region Fill Rates

        /// <summary>
        /// Q fill rate (1/s) of one radar on a target at distanceMeters: RadarQualityRate_per_s x min(1, ref / d)^exponent,
        /// times SurfaceRadarQualityFactor for a surface search radar, times AlliedRadarSweepFactor for a radar that is not
        /// the radar of a human pilot.
        /// </summary>
        public static float RadarFillRate(float distanceMeters, bool humanPilotRadar, bool surfaceStation)
        {
            float rate = Mathf.Max(0.0f, CnoEvilConfig.RadarQualityRate_per_s.Value);
            float refRange = Mathf.Max(1.0f, CnoEvilConfig.RadarQualityRefRange_m.Value);
            if (distanceMeters > refRange)
            {
                rate *= Mathf.Pow(refRange / distanceMeters, Mathf.Max(0.0f, CnoEvilConfig.RadarQualityRangeExponent.Value));
            }
            if (surfaceStation) rate *= Mathf.Max(0.0f, CnoEvilConfig.SurfaceRadarQualityFactor.Value);
            if (!humanPilotRadar) rate *= Mathf.Max(0.0f, CnoEvilConfig.AlliedRadarSweepFactor.Value);
            return rate;
        }

        /// <summary>
        /// Q fill rate (1/s) of one RWR receiver: RwrStrobeQualityRate_per_s x range weight x tier multiplier.
        /// </summary>
        public static float RwrFillRate(float distanceMeters, RwrTier tier, bool isTier4)
        {
            float tierMult = 1.0f;
            if (isTier4) tierMult = Mathf.Max(0.0f, CnoEvilConfig.RwrTier4QualityMultiplier.Value);
            else if (tier == RwrTier.Tier1_Basic) tierMult = Mathf.Max(0.0f, CnoEvilConfig.RwrTier1QualityMultiplier.Value);
            else if (tier == RwrTier.Tier3_Advanced) tierMult = Mathf.Max(0.0f, CnoEvilConfig.RwrTier3QualityMultiplier.Value);
            return Mathf.Max(0.0f, CnoEvilConfig.RwrStrobeQualityRate_per_s.Value) * CalculateRangeWeight(distanceMeters) * tierMult;
        }

        /// <summary>
        /// A sensor (a Radar or an RWR receiver) of the faction reports the target now with this fill rate. One entry per
        /// sensor: the same radar reported by the scan and by the detection census counts once.
        /// </summary>
        public static void RecordSensorRate(TriangulationTrack track, UnityEngine.Object sensor, float rate)
        {
            if (track == null || (object)sensor == null || rate <= 0.0f) return;
            if (track.QualityContributors == null) track.QualityContributors = new Dictionary<UnityEngine.Object, SensorRate>();
            SensorRate r;
            r.Rate = rate;
            r.Time = Time.timeSinceLevelLoad;
            track.QualityContributors[sensor] = r;
        }

        private static readonly List<UnityEngine.Object> s_staleSensors = new List<UnityEngine.Object>();

        /// <summary>
        /// Sum of the fill rates of the sensors that reported within SensorRateHoldSeconds; older entries are removed.
        /// </summary>
        public static float SumSensorRates(TriangulationTrack track, float now)
        {
            if (track == null || track.QualityContributors == null || track.QualityContributors.Count == 0) return 0.0f;
            float sum = 0.0f;
            s_staleSensors.Clear();
            foreach (KeyValuePair<UnityEngine.Object, SensorRate> kv in track.QualityContributors)
            {
                if (now - kv.Value.Time > SensorRateHoldSeconds || kv.Key == null) s_staleSensors.Add(kv.Key);
                else sum += kv.Value.Rate;
            }
            for (int i = 0; i < s_staleSensors.Count; i++) track.QualityContributors.Remove(s_staleSensors[i]);
            s_staleSensors.Clear();
            return sum;
        }

        /// <summary>
        /// Fills Q with the summed rate of the sensors: Q = 1 - (1 - Q) x exp(-R x dt). No cap; Q goes to 1.
        /// </summary>
        public static void ApplyQualityFill(TriangulationTrack track, float dt, float now)
        {
            if (track == null || dt <= 0.0f) return;
            float r = SumSensorRates(track, now);
            if (r <= 0.0f) return;
            float q = Mathf.Clamp01(track.TrackingQuality);
            track.TrackingQuality = Mathf.Clamp01(1.0f - (1.0f - q) * Mathf.Exp(-r * dt));
        }

        #endregion

        #region Contacts

        /// <summary>
        /// A radar of the faction detects the target: the track is in active contact. Q comes from the fill rate
        /// (RecordSensorRate, ApplyQualityFill).
        /// </summary>
        public static void RecordRadarContact(TriangulationTrack track, float distanceMeters)
        {
            if (track == null) return;
            float now = Time.timeSinceLevelLoad;
            track.LastActiveDetectionTime = now;
            track.LastRadarIlluminatedTime = now;
            track.HadActiveContact = true;
            track.IsTriangulated = true;
            RecordSensorRange(track, distanceMeters);
        }

        /// <summary>
        /// Q of a visual contact (ADR-43): VisualContactNearQuality up to VisualContactNearRange_m, falling linearly to
        /// farQuality at VisualContactFarRange_m. A building or a stationary unit holds VisualContactNearQuality at any range
        /// (a fixed point does not drift between two looks). distanceMeters 0 = unknown spotter range: farQuality.
        /// </summary>
        public static float VisualContactQualityFor(Unit target, float farQuality, float distanceMeters)
        {
            float far = Mathf.Clamp01(farQuality);
            float near = Mathf.Max(far, Mathf.Clamp01(CnoEvilConfig.VisualContactNearQuality.Value));
            if (target == null || distanceMeters <= 0.0f) return far;

            float speed = (target.rb != null) ? target.rb.velocity.magnitude : target.speed;
            if (target is Building || speed < StationarySpeed) return near;

            float nearRange = Mathf.Max(0.0f, CnoEvilConfig.VisualContactNearRange_m.Value);
            float farRange = Mathf.Max(nearRange + 1.0f, CnoEvilConfig.VisualContactFarRange_m.Value);
            float t = Mathf.Clamp01((distanceMeters - nearRange) / (farRange - nearRange));
            return Mathf.Lerp(near, far, t);
        }

        /// <summary>
        /// A laser designator of the faction lases the target (vanilla FactionHQ.IsTargetLased): Q at least
        /// LaserContactQuality, a precise contact for LaserHoldSeconds, and weapons get CepMin_m.
        /// </summary>
        public static void RecordLaserContact(TriangulationTrack track, float holdSeconds)
        {
            if (track == null) return;
            float now = Time.timeSinceLevelLoad;
            track.LastActiveDetectionTime = now;
            track.HadActiveContact = true;
            track.IsTriangulated = true;
            track.LaserHoldUntil = Mathf.Max(track.LaserHoldUntil, now + holdSeconds);
            track.VisualHoldUntil = Mathf.Max(track.VisualHoldUntil, now + holdSeconds);
            track.TrackingQuality = Mathf.Max(track.TrackingQuality, Mathf.Clamp01(CnoEvilConfig.LaserContactQuality.Value));
        }

        /// <summary>
        /// Visual contact that raises quality to qualityFloor (VisualContactQualityFor). distanceMeters is the spotter
        /// range (0 = unknown).
        /// </summary>
        public static void RecordVisualContact(TriangulationTrack track, float qualityFloor, float distanceMeters)
        {
            if (track == null) return;
            float now = Time.timeSinceLevelLoad;
            track.LastActiveDetectionTime = now;
            track.HadActiveContact = true;
            track.IsTriangulated = true;
            RecordSensorRange(track, distanceMeters);
            track.TrackingQuality = Mathf.Max(track.TrackingQuality, Mathf.Clamp01(qualityFloor));
        }

        /// <summary>
        /// A passive RWR pulse from the target's radar reached a receiver of the faction: the track is in contact and the
        /// receiver adds its fill rate (range, tier). There is no cap; a fix sets its own Q floor (RecordFixQuality).
        /// </summary>
        public static void RecordRWRDetection(TriangulationTrack track, UnityEngine.Object receiver, float distanceMeters, RwrTier tier, bool isTier4)
        {
            if (track == null) return;
            track.LastActiveDetectionTime = Time.timeSinceLevelLoad;
            track.HadActiveContact = true;
            RecordSensorRange(track, distanceMeters);
            RecordSensorRate(track, receiver, RwrFillRate(distanceMeters, tier, isTier4));
        }

        /// <summary>
        /// An accepted passive fix (single-ship fit or multi-station pair): Q becomes at least qFix, the Q whose CEP is
        /// the CEP of the fix (QualityFromFixError). A higher Q from other sensors stays.
        /// </summary>
        public static void RecordFixQuality(TriangulationTrack track, float qFix)
        {
            if (track == null) return;
            track.TrackingQuality = Mathf.Max(track.TrackingQuality, Mathf.Clamp01(qFix));
            track.LastActiveDetectionTime = Time.timeSinceLevelLoad;
            track.HadActiveContact = true;
            track.IsTriangulated = true;
        }

        // Median miss of a 2D Gaussian in sigma: CEP = sigma * sqrt(2 ln 2)
        private const float CepPerSigma = 1.1774100f;

        /// <summary>
        /// Q of a passive fix with this 1-sigma position error (m): the Q at which the Q term of CalculateCep is the CEP of
        /// the fix (1.1774 sigma). Q = 1 - (CEP / CepAtZeroQuality_m)^(1 / CepQualityExponent), 0..1. With the default
        /// values: sigma 100 m -> Q 0.80, 300 m -> 0.66, 1 km -> 0.37, 2.55 km and more -> 0.
        /// </summary>
        public static float QualityFromFixError(float sigmaMeters)
        {
            if (sigmaMeters <= 0.0f) return 1.0f;
            float scale = Mathf.Max(0.0f, CnoEvilConfig.CepAtZeroQuality_m.Value);
            float p = Mathf.Max(0.01f, CnoEvilConfig.CepQualityExponent.Value);
            if (scale <= 0.0f) return 1.0f;
            float t = Mathf.Clamp01(sigmaMeters * CepPerSigma / scale);
            return Mathf.Clamp01(1.0f - Mathf.Pow(t, 1.0f / p));
        }

        #endregion

        #region Dispersion & CEP Calculation

        // CEP radius = median miss distance: a 2D Gaussian with sigma = CEP / sqrt(2 ln 2)
        private static readonly float Ln2 = Mathf.Log(2.0f);

        // Largest drawn miss, in CEP (3 CEP holds 99.8 % of the draws)
        private const float MaxDispersionInCep = 3.0f;

        /// <summary>
        /// Horizontal CEP (m) of a weapon aimed on a track of quality Q at distanceMeters:
        /// CEP = max(CepMin, sqrt((A x (1 - Q)^p)^2 + (B x clamp01(distance / CepDistanceReference) x (1 - Q))^2)),
        /// A = CepAtZeroQuality_m, B = CepDistanceMax_m, p = CepQualityExponent. At Q = 1 the CEP is CepMin.
        /// </summary>
        public static float CalculateCep(float trackingQuality, float distanceMeters)
        {
            float q = Mathf.Clamp01(trackingQuality);
            return CalculateCep(q, distanceMeters, true);
        }

        /// <summary>
        /// CEP with or without the Q term. Without it (a datum from a passive fix centre: the fix error is already in the
        /// datum) only the distance term of this Q stays.
        /// </summary>
        public static float CalculateCep(float trackingQuality, float distanceMeters, bool includeQualityTerm)
        {
            float q = Mathf.Clamp01(trackingQuality);
            float cepMax = Mathf.Max(0.0f, CnoEvilConfig.CepAtZeroQuality_m.Value);
            float cepMin = Mathf.Max(0.0f, CnoEvilConfig.CepMin_m.Value);
            float p = Mathf.Max(0.01f, CnoEvilConfig.CepQualityExponent.Value);
            float distMax = Mathf.Max(0.0f, CnoEvilConfig.CepDistanceMax_m.Value);
            float dRef = Mathf.Max(1.0f, CnoEvilConfig.CepDistanceReference_m.Value);

            float qualityTerm = includeQualityTerm ? cepMax * Mathf.Pow(1.0f - q, p) : 0.0f;
            float distanceTerm = distMax * Mathf.Clamp01(Mathf.Max(0.0f, distanceMeters) / dRef) * (1.0f - q);
            return Mathf.Max(cepMin, Mathf.Sqrt(qualityTerm * qualityTerm + distanceTerm * distanceTerm));
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
        /// Random horizontal miss in CEP units (Dx, 0, Dz) of a 2D Gaussian with CEP 1: half of the draws fall inside 1.
        /// The miss is limited to 3 CEP. Zero when the mod or the dispersion is off.
        /// </summary>
        public static Vector3 GenerateDispersionDraw()
        {
            if (!CnoEvilConfig.IsModActive) return Vector3.zero;
            if (!CnoEvilConfig.EnableTrackUncertaintyDispersion.Value) return Vector3.zero;

            // Inverse of the Rayleigh CDF with median 1: r = sqrt(-ln(1 - u) / ln 2)
            float u = UnityEngine.Random.value;
            float r = Mathf.Sqrt(-Mathf.Log(Mathf.Max(1e-6f, 1.0f - u)) / Ln2);
            r = Mathf.Min(r, MaxDispersionInCep);

            float phi = UnityEngine.Random.Range(0f, Mathf.PI * 2f);
            return new Vector3(r * Mathf.Cos(phi), 0f, r * Mathf.Sin(phi));
        }

        #endregion

        #region Missile Launch Dispersion Registry

        /// <summary>
        /// Records the drawn dispersion of one munition in CEP units, scaled by cep (drawn once, at launch).
        /// </summary>
        public static void RegisterMissileDraw(Missile missile, Vector3 drawInCep, float cep, float launchDistance)
        {
            if (missile == null) return;
            DispersionEntry e;
            e.Unit = new Vector3(drawInCep.x, 0.0f, drawInCep.z);
            e.CepScale = Mathf.Max(0.0f, cep);
            e.LaunchDistance = Mathf.Max(0.0f, launchDistance);
            e.LimitMeters = float.PositiveInfinity;
            if (e.CepScale <= 0.0f) e.Unit = Vector3.zero;
            _missileDispersion[missile.GetInstanceID()] = e;
        }

        /// <summary>
        /// Records a raw offset (the host's offset on a mirror client, or zero for a target the mod does not guide).
        /// </summary>
        public static void RegisterMissileDispersion(Missile missile, Vector3 horizontalOffset)
        {
            if (missile == null) return;
            DispersionEntry e;
            e.Unit = new Vector3(horizontalOffset.x, 0.0f, horizontalOffset.z);
            e.CepScale = 0.0f;
            e.LaunchDistance = 0.0f;
            e.LimitMeters = float.PositiveInfinity;
            _missileDispersion[missile.GetInstanceID()] = e;
        }

        /// <summary>
        /// The datalink has a contact on the target: the drawn miss is scaled by the CEP of the faction track now
        /// (CalculateCep with the launch distance; cepOverride above 0, a lased target, wins). No change for a raw offset.
        /// </summary>
        public static void RescaleMissileDispersion(Missile missile, float trackingQuality, float cepOverride)
        {
            if (missile == null) return;
            int id = missile.GetInstanceID();
            DispersionEntry e;
            if (!_missileDispersion.TryGetValue(id, out e) || e.CepScale <= 0.0f) return;
            float cep = (cepOverride > 0.0f) ? cepOverride : CalculateCep(trackingQuality, e.LaunchDistance);
            if (Mathf.Approximately(cep, e.CepScale)) return;
            e.CepScale = Mathf.Max(0.001f, cep);
            _missileDispersion[id] = e;
        }

        /// <summary>
        /// Limits the offset of a munition to maxMeters (vanilla rules that drop the target when the datum is too far
        /// from it). The limit stays when the draw is scaled again.
        /// </summary>
        public static void LimitMissileDispersion(Missile missile, float maxMeters)
        {
            if (missile == null) return;
            int id = missile.GetInstanceID();
            DispersionEntry e;
            if (!_missileDispersion.TryGetValue(id, out e)) return;
            e.LimitMeters = Mathf.Min(e.LimitMeters, Mathf.Max(0.0f, maxMeters));
            _missileDispersion[id] = e;
        }

        /// <summary>
        /// The current dispersion of a munition (offset of its datum from the known target position).
        /// </summary>
        public static bool TryGetMissileDispersion(Missile missile, out Vector3 offset)
        {
            offset = Vector3.zero;
            if (missile == null) return false;
            DispersionEntry e;
            if (!_missileDispersion.TryGetValue(missile.GetInstanceID(), out e)) return false;
            offset = (e.CepScale > 0.0f) ? e.Unit * e.CepScale : e.Unit;
            float mag = offset.magnitude;
            if (mag > e.LimitMeters && mag > 0.0f) offset *= e.LimitMeters / mag;
            return true;
        }

        /// <summary>
        /// Unregisters a munition upon detonation, splash, or destruction.
        /// </summary>
        public static void UnregisterMissile(Missile missile)
        {
            if ((object)missile == null) return;
            _missileDispersion.Remove(missile.GetInstanceID());
        }

        /// <summary>
        /// Clears all stored missile dispersion offsets (e.g. level reload).
        /// </summary>
        public static void Clear()
        {
            _missileDispersion.Clear();
            s_staleSensors.Clear();
        }

        #endregion
    }
}
