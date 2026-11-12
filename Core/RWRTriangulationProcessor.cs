using System;
using System.Collections.Generic;
using UnityEngine;
using CnoEvil.Config;
using CnoEvil.Components;
using CnoEvil.Patches;

namespace CnoEvil.Core
{
    /// <summary>
    /// Tracks emitter state, cooperative multi-bearing Datalink triangulation,
    /// single-ship kinematic baseline observations, and distance-proportional spatial offsets.
    /// In v1.6.0, supports continuous radar dwell quality refinement, uncertainty bubble expansion/contraction,
    /// and dynamic Predicted Intercept Point (PIP) displacement for missile guidance.
    /// </summary>
    public class TriangulationTrack
    {
        public Unit EmitterUnit;
        public bool IsTriangulated;
        public float LastUpdateTime;
        public Vector3 TriangulatedWorldPos;       // Target position (live or last known)
        public GlobalPosition EstimatedCenter;     // Center of the map ambiguity circle (true position, or the passive bearing fix)
        public float TriangulationErrorMeters;     // 1-sigma error of the passive bearing fix (0 for radar / visual contact)
        public float LastFixTime;                  // Timestamp of the last accepted bearing fix
        public float LastPreciseContactTime;       // Timestamp of the last radar / visual contact
        public float UncertaintyRadiusMeters;      // Radius of ambiguity circle in meters
        public float UncertaintyObserverDist;      // RWR observer distance of the last ping (0 = none): input of the radius
        public float UncertaintySignal01 = 1.0f;   // Signal strength of the last ping: input of the radius
        public float SensorRangeMeters;            // Range from the closest recent sensor to the target (0 = unknown); drives weapon CEP
        public float SensorRangeTime;              // Timestamp of SensorRangeMeters

        // Tracking Quality & Radar Illumination
        public float TrackingQuality;             // 0.0 (lost/untracked) to 1.0 (refined pinpoint lock)
        public float LastRadarIlluminatedTime;    // Timestamp of last active radar detection/sweep

        public float VisualHoldUntil;             // A visual contact counts as held until this time (covers the spotter's check interval)

        // Host (ADR-44): Q fill rate of each sensor (Radar or RWR receiver) of the faction that reports the target
        public Dictionary<UnityEngine.Object, SensorRate> QualityContributors;

        // Lost Contact Memory State (forget rule: IsTrackForgotten)
        public bool HadActiveContact;             // Has this target ever been actively identified/detected by radar/optics/datalink?
        public float LastActiveDetectionTime;     // Timestamp of last active positive radar/visual/datalink detection
        public GlobalPosition LastKnownGlobalPosition; // Last position the faction knew: precise contact or passive fix center
        public float LastKnownTime;                    // Timestamp of LastKnownGlobalPosition (0 = never located)
        public float LastKnownQuality;                 // Q when LastKnownGlobalPosition was written (weapon CEP in memory)
        public bool LastKnownFromFix;                  // LastKnownGlobalPosition is a passive fix or decoy centre (its error is in the datum)
        public Vector3 LastKnownVelocity;         // Velocity at last contact (drives memory CEP drift)
        public Vector3 LastPreciseVelocity;       // Velocity measured at the last radar / visual contact (drives AI lead)
        public float LastPreciseVelocityTime;     // Timestamp of LastPreciseVelocity (0 = never measured)

        // Host only (vanilla RadarLocator runs on the server): passive fixes of the real emitter
        public float LastEmitterFixTime;          // Last accepted bearing fix of the emitter (not a decoy; 0 = none)
        public float PassiveReacquireTime;        // Fix after a gap of more than ContactHold_s: the emitter was found again
        public bool PersistentStatic;             // Located strategic / radar building: never forgotten while it exists
        public float RemoteSeenTime;              // Mirror client: last host record of this track (0 = none)

        /// <summary>
        /// Radar or visual contact within the vanilla FactionHQ "spotted" window (4 s).
        /// </summary>
        public bool HasRecentPreciseContact
        {
            get { return LastPreciseContactTime != 0.0f && Time.timeSinceLevelLoad - LastPreciseContactTime < RWRTriangulationProcessor.PreciseContactWindow; }
        }

        /// <summary>
        /// A spotter of the faction holds a visual contact (vanilla TargetDetector or the strategic building scan).
        /// </summary>
        public bool HasVisualContact
        {
            get { return Time.timeSinceLevelLoad < VisualHoldUntil; }
        }

        public bool IsActivelyDetected
        {
            get
            {
                // Active if positively observed / illuminated / pinged within ContactHold_s
                return (Time.timeSinceLevelLoad - LastActiveDetectionTime < CnoEvilConfig.ContactHold_s.Value);
            }
        }

        /// <summary>
        /// Contact lost, track still kept. A forgotten track is removed (RWRTriangulationProcessor.IsTrackForgotten),
        /// so every remaining track out of contact is in memory.
        /// </summary>
        public bool IsInMemoryState()
        {
            return HadActiveContact && !IsActivelyDetected;
        }

        // Single-ship fix (host only, ADR-42): own bearings of human pilots on this emitter, least-squares fit
        public List<BearingSample> OwnBearings;
        public float FixQuality;                  // Q that the error of the last accepted fix supports (CEP of the fix)
        public bool PassiveLocated;               // A passive fix within LocatedFixMaxErrorFraction located the emitter
        public float LaserHoldUntil;              // Host: a faction laser designator lases the target until this time

        /// <summary>
        /// Host: a laser designator of the faction lases the target now (weapons get CepMin_m).
        /// </summary>
        public bool IsLased
        {
            get { return Time.timeSinceLevelLoad < LaserHoldUntil; }
        }

    }

    /// <summary>
    /// Q fill rate (1/s) of one sensor and the time of its last report (Time.timeSinceLevelLoad).
    /// </summary>
    public struct SensorRate
    {
        public float Rate;
        public float Time;
    }

    /// <summary>
    /// One measured bearing of one observer: horizontal unit direction, its 1-sigma error and the observer position.
    /// </summary>
    public struct BearingSample
    {
        public Unit Observer;
        public GlobalPosition Position;
        public float DirX;
        public float DirZ;
        public float SigmaRad;
        public float Time;
    }

    /// <summary>
    /// Faction track state the host sends to a mirror client (TrackSync). Timestamps are local client times
    /// (client now - host age); 0 = no timestamp.
    /// </summary>
    public struct RemoteTrackData
    {
        public bool IsTriangulated;
        public bool HadActiveContact;
        public bool HasFixCenter;                  // EstimatedCenter is a passive fix or a stale contact position
        public bool PassiveLocated;                // A passive fix located the emitter (TriangulationTrack.PassiveLocated)
        public float TrackingQuality;
        public float LastUpdateTime;
        public float LastFixTime;
        public float LastPreciseContactTime;
        public float LastRadarIlluminatedTime;
        public float VisualHoldUntil;
        public float LastActiveDetectionTime;
        public float LastKnownTime;
        public float SensorRangeTime;
        public float LastPreciseVelocityTime;
        public float TriangulationErrorMeters;
        public float UncertaintyRadiusMeters;
        public float SensorRangeMeters;
        public GlobalPosition EstimatedCenter;
        public GlobalPosition LastKnownGlobalPosition;
        public Vector3 LastKnownVelocity;
        public Vector3 LastPreciseVelocity;
    }

    /// <summary>
    /// Electronic Support Measures (ESM) triangulation & target tracking quality engine.
    /// Evaluates whether radar threats are resolved to pinpoint locations via multi-platform Datalink,
    /// continuous radar dwell, or single-ship maneuver baselines, generating distance-proportional spatial error offsets.
    /// Operates as a universal, symmetric multi-faction network supporting bots, players, and co-op/MP teams.
    /// </summary>
    public static class RWRTriangulationProcessor
    {
        private static readonly Dictionary<FactionHQ, Dictionary<Unit, TriangulationTrack>> _factionTracks = new Dictionary<FactionHQ, Dictionary<Unit, TriangulationTrack>>();
        private static readonly Dictionary<Unit, TriangulationTrack> _fallbackTracks = new Dictionary<Unit, TriangulationTrack>();
        private static readonly List<Unit> _staleList = new List<Unit>();
        private static FactionHQ _lastPlayerHQ = null;

        private struct AlliedBearingObservation
        {
            public Unit Observer;
            public GlobalPosition Position;
            public Vector3 Bearing;
            public float SigmaRad;
            public bool SharesBearings;
            public float Timestamp;
        }

        private static readonly Dictionary<FactionHQ, Dictionary<Unit, List<AlliedBearingObservation>>> _factionBearingObs =
            new Dictionary<FactionHQ, Dictionary<Unit, List<AlliedBearingObservation>>>();

        /// <summary>
        /// Creates a new track for an emitter. The single-ship fix uses only the own bearings of a pilot (OwnBearings),
        /// never the position of another sensor.
        /// </summary>
        private static TriangulationTrack CreateTrack(Unit emitter)
        {
            TriangulationTrack track = new TriangulationTrack();
            track.EmitterUnit = emitter;
            track.TrackingQuality = 0.0f; // Accumulates via sensor ticks
            return track;
        }

        public static float GetTargetMemoryDuration(Unit target)
        {
            if (ThreatClassification.IsStrategicBuilding(target, null)) return CnoEvilConfig.StrategicBuildingMemory_s.Value;
            if (target is Aircraft) return CnoEvilConfig.AircraftMemory_s.Value;
            return CnoEvilConfig.GroundTargetMemory_s.Value;
        }

        /// <summary>
        /// Host forget rule (ADR-46). A located strategic / radar building is never forgotten. A track in contact is
        /// kept. A track in memory is forgotten when the time cap (GetTargetMemoryDuration) since the last detection
        /// is over. The true target motion is never used: a ghost that goes would tell the player that the target moved.
        /// </summary>
        public static bool IsTrackForgotten(TriangulationTrack track, Unit target, float now)
        {
            if (track == null || target == null || target.disabled) return true;
            if (track.PersistentStatic) return false;
            float cap = GetTargetMemoryDuration(target);
            if (!track.HadActiveContact) return now - track.LastUpdateTime > cap;
            if (track.IsActivelyDetected) return false;
            return now - track.LastActiveDetectionTime > cap;
        }

        // A bearing fix counts as triangulated when its 1-sigma position error is within this fraction of range
        private static float FixRelativeErrorThreshold
        {
            get { return Mathf.Max(0.0f, CnoEvilConfig.BearingFixMaxErrorFraction.Value); }
        }

        private static float BearingObservationMaxAge
        {
            get { return Mathf.Max(0.1f, CnoEvilConfig.BearingObservationMaxAge_s.Value); }
        }

        private static float SingleShipBaselineMaxAge
        {
            get { return Mathf.Max(10.0f, CnoEvilConfig.SingleShipBaselineMaxAge_s.Value); }
        }

        // Bearings closer than ~1 degree are treated as parallel (no usable intersection)
        private const float MinIntersectionSine = 0.0175f;

        /// <summary>
        /// Stores an observer's latest bearing to an emitter for multi-station ESM fusion.
        /// sigmaRad is the observer's 1-sigma bearing error; sharesBearings is true for receivers on an
        /// Advanced (sensor-fusion) datalink, which can contribute raw bearings to other stations.
        /// </summary>
        public static void RecordAlliedBearing(FactionHQ hq, Unit emitter, Unit observer, Vector3 observerPos, Vector3 bearing,
            float sigmaRad, bool sharesBearings, float timestamp)
        {
            if (hq == null || emitter == null || observer == null) return;
            Dictionary<Unit, List<AlliedBearingObservation>> obsDict;
            if (!_factionBearingObs.TryGetValue(hq, out obsDict))
            {
                obsDict = new Dictionary<Unit, List<AlliedBearingObservation>>();
                _factionBearingObs[hq] = obsDict;
            }

            List<AlliedBearingObservation> list;
            if (!obsDict.TryGetValue(emitter, out list))
            {
                list = new List<AlliedBearingObservation>();
                obsDict[emitter] = list;
            }

            for (int i = list.Count - 1; i >= 0; i--)
            {
                if (timestamp - list[i].Timestamp > BearingObservationMaxAge || list[i].Observer == null || list[i].Observer.disabled)
                {
                    list.RemoveAt(i);
                }
            }

            AlliedBearingObservation obs = new AlliedBearingObservation
            {
                Observer = observer,
                Position = GlobalPositionExtensions.ToGlobalPosition(observerPos),
                Bearing = bearing,
                SigmaRad = sigmaRad,
                SharesBearings = sharesBearings,
                Timestamp = timestamp
            };

            for (int i = 0; i < list.Count; i++)
            {
                if (list[i].Observer == observer)
                {
                    list[i] = obs;
                    return;
                }
            }
            list.Add(obs);
        }

        /// <summary>
        /// Intersects two bearing lines in the horizontal plane.
        /// Returns the estimated emitter position and its 1-sigma error:
        /// sigma = sqrt((R1*s1)^2 + (R2*s2)^2) / sin(theta), combined with the emitter's motion between the two
        /// observations (speed * |t1 - t2|). meanRange is the mean observer distance to the estimate.
        /// </summary>
        public static bool TryIntersectBearings(
            Vector3 p1, Vector3 b1, float sigma1, float t1,
            Vector3 p2, Vector3 b2, float sigma2, float t2,
            float emitterSpeed,
            out Vector3 fix, out float sigmaPos, out float meanRange)
        {
            fix = Vector3.zero;
            sigmaPos = float.MaxValue;
            meanRange = 0.0f;

            Vector2 d1 = new Vector2(b1.x, b1.z);
            Vector2 d2 = new Vector2(b2.x, b2.z);
            if (d1.sqrMagnitude < 1e-6f || d2.sqrMagnitude < 1e-6f) return false;
            d1.Normalize();
            d2.Normalize();

            float cross = d1.x * d2.y - d1.y * d2.x;
            float sinTheta = Mathf.Abs(cross);
            if (sinTheta < MinIntersectionSine) return false;

            Vector2 w = new Vector2(p2.x - p1.x, p2.z - p1.z);
            float r1 = (w.x * d2.y - w.y * d2.x) / cross;
            float r2 = (w.x * d1.y - w.y * d1.x) / cross;
            if (r1 <= 0.0f || r2 <= 0.0f) return false; // Lines cross behind an observer

            float e1 = r1 * sigma1;
            float e2 = r2 * sigma2;
            float geometric = Mathf.Sqrt(e1 * e1 + e2 * e2) / sinTheta;
            float motion = emitterSpeed * Mathf.Abs(t1 - t2);

            sigmaPos = Mathf.Sqrt(geometric * geometric + motion * motion);
            meanRange = 0.5f * (r1 + r2);
            fix = new Vector3(p1.x + d1.x * r1, 0.5f * (p1.y + p2.y), p1.z + d1.y * r1);
            return true;
        }

        /// <summary>
        /// Best (lowest-error) two-station bearing fix on an emitter from bearings shared over the faction datalink.
        /// fixTime: the time of the newer of the two bearings (the fix is not newer than its observations).
        /// </summary>
        public static bool TryGetMultiStationFix(FactionHQ hq, Unit emitter, float now, out Vector3 fix, out float sigmaPos, out float meanRange,
            out float fixTime)
        {
            fix = Vector3.zero;
            fixTime = 0.0f;
            sigmaPos = float.MaxValue;
            meanRange = 0.0f;
            if (hq == null || emitter == null) return false;

            Dictionary<Unit, List<AlliedBearingObservation>> obsDict;
            if (!_factionBearingObs.TryGetValue(hq, out obsDict)) return false;

            List<AlliedBearingObservation> list;
            if (!obsDict.TryGetValue(emitter, out list) || list == null || list.Count < 2) return false;

            float emitterSpeed = (emitter.rb != null) ? emitter.rb.velocity.magnitude : 0.0f;
            bool found = false;
            Unit bestA = null, bestB = null;

            for (int i = 0; i < list.Count; i++)
            {
                AlliedBearingObservation a = list[i];
                if (!a.SharesBearings || now - a.Timestamp > BearingObservationMaxAge || a.Observer == null || a.Observer.disabled) continue;

                for (int j = i + 1; j < list.Count; j++)
                {
                    AlliedBearingObservation b = list[j];
                    if (!b.SharesBearings || now - b.Timestamp > BearingObservationMaxAge || b.Observer == null || b.Observer.disabled) continue;

                    Vector3 f;
                    float sig, rng;
                    if (TryIntersectBearings(a.Position.ToLocalPosition(), a.Bearing, a.SigmaRad, a.Timestamp,
                                             b.Position.ToLocalPosition(), b.Bearing, b.SigmaRad, b.Timestamp,
                                             emitterSpeed, out f, out sig, out rng) && sig < sigmaPos)
                    {
                        fix = f;
                        sigmaPos = sig;
                        meanRange = rng;
                        fixTime = Mathf.Max(a.Timestamp, b.Timestamp);
                        bestA = a.Observer;
                        bestB = b.Observer;
                        found = true;
                    }
                }
            }

            if (found && EvilLog.On(LogCat.Track))
            {
                LogMultiStationFix(hq, emitter, bestA, bestB, sigmaPos, meanRange);
            }
            return found;
        }

        private struct LoggedFix
        {
            public Unit ObserverA;
            public Unit ObserverB;
            public float Sigma;
            public bool Accepted;
        }

        private static readonly Dictionary<KeyValuePair<FactionHQ, Unit>, LoggedFix> s_loggedFixes =
            new Dictionary<KeyValuePair<FactionHQ, Unit>, LoggedFix>();

        /// <summary>
        /// DebugLogging: one line when the observer pair, the acceptance or the error (by more than 10 %) of a faction's
        /// multi-station fix changes. The fix is computed every call; most calls reuse the same two bearings.
        /// </summary>
        private static void LogMultiStationFix(FactionHQ hq, Unit emitter, Unit observerA, Unit observerB, float sigmaPos, float meanRange)
        {
            bool accepted = sigmaPos <= FixRelativeErrorThreshold * meanRange;
            KeyValuePair<FactionHQ, Unit> key = new KeyValuePair<FactionHQ, Unit>(hq, emitter);
            LoggedFix last;
            if (s_loggedFixes.TryGetValue(key, out last) && last.ObserverA == observerA && last.ObserverB == observerB &&
                last.Accepted == accepted && Mathf.Abs(sigmaPos - last.Sigma) <= 0.1f * last.Sigma)
            {
                return;
            }
            s_loggedFixes[key] = new LoggedFix { ObserverA = observerA, ObserverB = observerB, Sigma = sigmaPos, Accepted = accepted };
            EvilLog.Write(LogCat.Track,
                "Datalink cross-bearing fix on {0}: obs1={1}, obs2={2}, sigma={3:F0}m at {4:F0}m ({5:F1}%), {6}",
                emitter.unitName, observerA != null ? observerA.unitName : "null", observerB != null ? observerB.unitName : "null",
                sigmaPos, meanRange, meanRange > 0.0f ? 100.0f * sigmaPos / meanRange : 0.0f, accepted ? "accepted" : "rejected");
        }

        private static float LocatedFixMaxErrorFraction
        {
            get { return Mathf.Max(0.0f, CnoEvilConfig.LocatedFixMaxErrorFraction.Value); }
        }

        /// <summary>
        /// Applies a passive bearing fix to a track. The fix triangulates the track when its error is within
        /// FixRelativeErrorThreshold of range; the map uncertainty circle is then centered on the estimate.
        /// Q becomes at least the Q whose CEP is the CEP of the fix (ADR-42); a fix within LocatedFixMaxErrorFraction
        /// of range locates the emitter. fixTime is the time of the newer bearing. Returns true when the fix was accepted.
        /// </summary>
        private static bool ApplyBearingFix(TriangulationTrack track, Unit emitter, Vector3 fix, float sigmaPos, float meanRange, bool smoothFix, float fixTime)
        {
            if (sigmaPos > FixRelativeErrorThreshold * meanRange) return false;

            // A precise sensor contact (radar / visual) is never degraded by a passive fix
            if (track.IsTriangulated && track.TriangulationErrorMeters <= 0.0f && fixTime - track.LastPreciseContactTime < BearingObservationMaxAge)
            {
                return true;
            }
            track.IsTriangulated = true;

            fix.y = emitter.transform.position.y; // Passive ESM gives no usable altitude; the map is 2D
            // The single-ship fit already uses all own bearings; single-sample fixes (best multi-station pair, Tier 4
            // elevation) are smoothed
            bool smooth = smoothFix && track.LastFixTime != 0.0f && track.TriangulationErrorMeters > 0.0f &&
                          fixTime - track.LastFixTime < BearingObservationMaxAge;
            track.EstimatedCenter = smooth
                ? GlobalPositionExtensions.ToGlobalPosition(Vector3.Lerp(track.EstimatedCenter.ToLocalPosition(), fix, 0.3f))
                : GlobalPositionExtensions.ToGlobalPosition(fix);
            track.TriangulationErrorMeters = smooth ? Mathf.Lerp(track.TriangulationErrorMeters, sigmaPos, 0.3f) : sigmaPos;
            track.LastFixTime = fixTime;
            TrackUncertaintyCalculator.RecordSensorRange(track, meanRange);
            track.LastKnownGlobalPosition = track.EstimatedCenter;
            track.LastKnownTime = fixTime;
            // A fix after a gap finds the emitter again: vanilla may move its entry once (RadarLocator_OnRadarWarning_Patch)
            if (track.LastEmitterFixTime == 0.0f || fixTime - track.LastEmitterFixTime > CnoEvilConfig.ContactHold_s.Value)
            {
                track.PassiveReacquireTime = fixTime;
            }
            track.LastEmitterFixTime = fixTime;

            float qFix = TrackUncertaintyCalculator.QualityFromFixError(track.TriangulationErrorMeters);
            track.FixQuality = qFix;
            TrackUncertaintyCalculator.RecordFixQuality(track, qFix);
            // The datum in memory is the fix centre: its error is already in the position (ADR-44)
            track.LastKnownQuality = qFix;
            track.LastKnownFromFix = true;
            if (track.TriangulationErrorMeters <= LocatedFixMaxErrorFraction * meanRange) track.PassiveLocated = true;
            return true;
        }

        // Own bearings of a pilot: at most one each OwnBearingInterval s, at most MaxOwnBearings per track
        private const float OwnBearingInterval = 0.5f;
        private const int MaxOwnBearings = 64;
        // The fit needs bearings over at least this time
        private const float MinOwnBaselineSeconds = 3.0f;

        private static float IndependentBearingSamples
        {
            get { return Mathf.Max(1.0f, CnoEvilConfig.BearingIndependentSamples.Value); }
        }

        /// <summary>
        /// Adds an own bearing of a pilot to the track. Old bearings (SingleShipBaselineMaxAge_s) and bearings of dead
        /// observers drop. When the list is full, the bearing nearest in time to its neighbours goes, so the bearings
        /// stay spread over the whole window (the long baseline is kept).
        /// </summary>
        private static bool RecordOwnBearing(TriangulationTrack track, Unit observer, Vector3 observerPos, Vector3 bearing, float sigmaRad, float now)
        {
            Vector2 d = new Vector2(bearing.x, bearing.z);
            if (d.sqrMagnitude < 1e-6f) return false;
            d.Normalize();

            List<BearingSample> list = track.OwnBearings;
            if (list == null)
            {
                list = new List<BearingSample>(16);
                track.OwnBearings = list;
            }

            float window = SingleShipBaselineMaxAge;
            float lastOwn = float.NegativeInfinity;
            for (int i = list.Count - 1; i >= 0; i--)
            {
                BearingSample s = list[i];
                if (now - s.Time > window || s.Observer == null || s.Observer.disabled)
                {
                    list.RemoveAt(i);
                    continue;
                }
                if (s.Observer == observer && s.Time > lastOwn) lastOwn = s.Time;
            }
            if (now - lastOwn < OwnBearingInterval) return false;

            if (list.Count >= MaxOwnBearings)
            {
                // List is in time order: drop the inner sample with the smallest gap between its neighbours
                int drop = 1;
                float best = float.MaxValue;
                for (int i = 1; i < list.Count - 1; i++)
                {
                    float gap = list[i + 1].Time - list[i - 1].Time;
                    if (gap < best)
                    {
                        best = gap;
                        drop = i;
                    }
                }
                list.RemoveAt(drop);
            }

            BearingSample sample;
            sample.Observer = observer;
            sample.Position = GlobalPositionExtensions.ToGlobalPosition(observerPos);
            sample.DirX = d.x;
            sample.DirZ = d.y;
            sample.SigmaRad = Mathf.Max(1e-4f, sigmaRad);
            sample.Time = now;
            list.Add(sample);
            return true;
        }

        // Tier 4 elevation ranging: the depression angle must be at least this (smaller angles give no usable range)
        private const float Tier4MinDepressionRad = 0.5f * Mathf.Deg2Rad;
        private const float Tier4MaxRayMeters = 250000.0f;

        /// <summary>
        /// Tier 4 single-pass fix on a surface emitter (ADR-43): the measured azimuth and elevation give a line of sight; its
        /// first hit on the terrain (or the sea surface) is the fix. Ground range r, depression angle d:
        /// cross error = r sigma_az, along error = r sigma_el / (sin d cos d). A small depression angle (low flight, far
        /// emitter) gives a large error and the fix gate rejects it.
        /// </summary>
        private static bool TrySolveElevationFix(Vector3 observerPos, Vector3 measuredBearing, float measuredElevationRad,
            float sigmaAzRad, float sigmaElRad, out Vector3 fix, out float sigmaPos, out float groundRange)
        {
            fix = Vector3.zero;
            sigmaPos = float.MaxValue;
            groundRange = 0.0f;
            float depression = -measuredElevationRad;
            if (depression < Tier4MinDepressionRad || depression > 1.5f) return false;

            Vector2 h = new Vector2(measuredBearing.x, measuredBearing.z);
            if (h.sqrMagnitude < 1e-6f) return false;
            h.Normalize();
            float cosEl = Mathf.Cos(measuredElevationRad);
            Vector3 dir = new Vector3(h.x * cosEl, Mathf.Sin(measuredElevationRad), h.y * cosEl);

            RaycastHit hit;
            Vector3 start = observerPos + dir * 20.0f;
            if (Physics.Raycast(start, dir, out hit, Tier4MaxRayMeters, (int)PhysicsLayers.StaticsMask))
            {
                fix = hit.point;
            }
            else
            {
                float seaY = Datum.LocalSeaY;
                if (dir.y >= -1e-4f || observerPos.y <= seaY) return false;
                float t = (seaY - observerPos.y) / dir.y;
                if (t <= 0.0f || t > Tier4MaxRayMeters) return false;
                fix = observerPos + dir * t;
            }

            groundRange = new Vector2(fix.x - observerPos.x, fix.z - observerPos.z).magnitude;
            if (groundRange < 10.0f) return false;
            float cross = groundRange * sigmaAzRad;
            float along = groundRange * sigmaElRad / Mathf.Max(1e-3f, Mathf.Sin(depression) * Mathf.Cos(depression));
            sigmaPos = Mathf.Sqrt(cross * cross + along * along);
            return true;
        }

        private static float Tier4TmaWindow
        {
            get { return Mathf.Max(10.0f, CnoEvilConfig.Tier4TmaWindow_s.Value); }
        }

        // Motion analysis needs at least this many bearings and this span
        private const int MinTmaBearings = 8;
        private const float MinTmaSpanSeconds = 10.0f;
        // Motion analysis needs an own manoeuvre: the largest deviation of the own track from a constant-velocity line
        // must be at least this fraction of the range (a straight flight cannot observe the emitter motion)
        private const double MinTmaManoeuvreFraction = 0.03;

        // Reused per fit: indices of the samples of one observer in the window (single thread, not re-entrant)
        private static readonly List<int> s_fitIndices = new List<int>(MaxOwnBearings);

        // Reused per fit: the samples relative to the newest bearing (position, time offset, direction, sigma, angle)
        private static readonly double[] s_fitPx = new double[MaxOwnBearings];
        private static readonly double[] s_fitPz = new double[MaxOwnBearings];
        private static readonly double[] s_fitTau = new double[MaxOwnBearings];
        private static readonly double[] s_fitDx = new double[MaxOwnBearings];
        private static readonly double[] s_fitDz = new double[MaxOwnBearings];
        private static readonly double[] s_fitSigma = new double[MaxOwnBearings];
        private static readonly double[] s_fitAngle = new double[MaxOwnBearings];

        // Reused normal matrices of the fits (column 4 = right side or gradient), inverse, step and trial point
        private static readonly double[,] s_fitA = new double[4, 5];
        private static readonly double[,] s_fitDamped = new double[4, 5];
        private static readonly double[,] s_fitInv = new double[4, 4];
        private static readonly double[,] s_fitWork = new double[4, 9];
        private static readonly double[] s_fitU = new double[4];
        private static readonly double[] s_fitStep = new double[4];
        private static readonly double[] s_fitTrial = new double[4];

        private const int FitMaxIterations = 30;
        private const int FitMaxDampingTries = 12;

        /// <summary>
        /// Copies the samples in s_fitIndices into the fit arrays, relative to origin (the position of the newest bearing)
        /// and lastTime. Returns the mean height offset of the observer positions.
        /// </summary>
        private static float PrepareFitSamples(List<BearingSample> list, Vector3 origin, float lastTime)
        {
            int n = s_fitIndices.Count;
            float ySum = 0.0f;
            for (int k = 0; k < n; k++)
            {
                BearingSample s = list[s_fitIndices[k]];
                Vector3 p = s.Position.ToLocalPosition() - origin;
                s_fitPx[k] = p.x;
                s_fitPz[k] = p.z;
                s_fitTau[k] = s.Time - lastTime;
                s_fitDx[k] = s.DirX;
                s_fitDz[k] = s.DirZ;
                s_fitSigma[k] = s.SigmaRad;
                s_fitAngle[k] = Math.Atan2(s.DirX, s.DirZ);
                ySum += p.y;
            }
            return n > 0 ? ySum / n : 0.0f;
        }

        private static double WrapPi(double a)
        {
            while (a > Math.PI) a -= 2.0 * Math.PI;
            while (a < -Math.PI) a += 2.0 * Math.PI;
            return a;
        }

        /// <summary>
        /// Sum of the squared bearing residuals over sigma^2 for the parameters u (k = 2: position x0; k = 4: position x0
        /// at the newest bearing and velocity v). Infinity when the estimate is behind an observation.
        /// </summary>
        private static double BearingCost(int n, int k, double[] u)
        {
            double cost = 0.0;
            for (int i = 0; i < n; i++)
            {
                double ex = u[0] - s_fitPx[i], ez = u[1] - s_fitPz[i];
                if (k == 4)
                {
                    ex += u[2] * s_fitTau[i];
                    ez += u[3] * s_fitTau[i];
                }
                if (ex * s_fitDx[i] + ez * s_fitDz[i] <= 0.0) return double.PositiveInfinity;
                double e = WrapPi(s_fitAngle[i] - Math.Atan2(ex, ez));
                cost += e * e / (s_fitSigma[i] * s_fitSigma[i]);
            }
            return cost;
        }

        /// <summary>
        /// Gauss-Newton normal matrix J^T W J (columns 0..k-1) and gradient J^T W e (column 4) of the bearing residuals.
        /// </summary>
        private static void BearingNormal(int n, int k, double[] u, double[,] m)
        {
            for (int r = 0; r < 4; r++) for (int c = 0; c < 5; c++) m[r, c] = 0.0;
            double[] j = s_fitTrial;
            for (int i = 0; i < n; i++)
            {
                double tau = s_fitTau[i];
                double ex = u[0] - s_fitPx[i], ez = u[1] - s_fitPz[i];
                if (k == 4)
                {
                    ex += u[2] * tau;
                    ez += u[3] * tau;
                }
                double r2 = Math.Max(1.0, ex * ex + ez * ez);
                double e = WrapPi(s_fitAngle[i] - Math.Atan2(ex, ez));
                double w = 1.0 / (s_fitSigma[i] * s_fitSigma[i]);
                j[0] = ez / r2;
                j[1] = -ex / r2;
                j[2] = j[0] * tau;
                j[3] = j[1] * tau;
                for (int r = 0; r < k; r++)
                {
                    for (int c = 0; c < k; c++) m[r, c] += w * j[r] * j[c];
                    m[r, 4] += w * j[r] * e;
                }
            }
        }

        /// <summary>
        /// Maximum-likelihood refinement of a bearing fit (Levenberg-Marquardt on the bearing residuals, start in u).
        /// The linear (pseudo-linear) fit is biased to short range; this removes the bias. posCov = C_xx + C_zz of the
        /// inverse normal matrix at the solution. False when the fit is not observable.
        /// </summary>
        private static bool RefineBearingFit(int n, int k, double[] u, out double posCov)
        {
            posCov = double.MaxValue;
            double cost = BearingCost(n, k, u);
            if (double.IsInfinity(cost) || double.IsNaN(cost)) return false;
            double lambda = 1e-3;
            for (int it = 0; it < FitMaxIterations; it++)
            {
                BearingNormal(n, k, u, s_fitA);
                bool improved = false;
                double stepPos = 0.0;
                for (int t = 0; t < FitMaxDampingTries; t++)
                {
                    for (int r = 0; r < k; r++)
                    {
                        for (int c = 0; c < k; c++) s_fitDamped[r, c] = s_fitA[r, c] + (r == c ? lambda * s_fitA[r, r] : 0.0);
                        s_fitDamped[r, 4] = s_fitA[r, 4];
                    }
                    if (SolveNormal(k, s_fitDamped, s_fitInv, s_fitStep))
                    {
                        for (int r = 0; r < k; r++) s_fitTrial[r] = u[r] + s_fitStep[r];
                        double trialCost = BearingCost(n, k, s_fitTrial);
                        if (trialCost < cost)
                        {
                            for (int r = 0; r < k; r++) u[r] = s_fitTrial[r];
                            cost = trialCost;
                            lambda = Math.Max(lambda * 0.3, 1e-9);
                            stepPos = Math.Sqrt(s_fitStep[0] * s_fitStep[0] + s_fitStep[1] * s_fitStep[1]);
                            improved = true;
                            break;
                        }
                    }
                    lambda *= 10.0;
                }
                if (!improved || stepPos < 1.0) break;
            }

            BearingNormal(n, k, u, s_fitA);
            if (!SolveNormal(k, s_fitA, s_fitInv, s_fitStep)) return false;
            double cxx = s_fitInv[0, 0], czz = s_fitInv[1, 1];
            if (!(cxx > 0.0) || !(czz > 0.0) || double.IsInfinity(cxx + czz)) return false;
            posCov = cxx + czz;
            return true;
        }

        /// <summary>
        /// Stationary-emitter fit on the prepared samples: pseudo-linear least squares (two passes, w_i = 1 / (sigma_i r_i)^2)
        /// as the start, then the maximum-likelihood refinement. Result in s_fitU[0..1], position covariance trace in posCov.
        /// </summary>
        private static bool SolveStaticPrepared(int n, out double posCov)
        {
            posCov = double.MaxValue;
            double ex = 0.0, ez = 0.0;
            for (int pass = 0; pass < 2; pass++)
            {
                double a11 = 0.0, a12 = 0.0, a22 = 0.0, b1 = 0.0, b2 = 0.0;
                for (int i = 0; i < n; i++)
                {
                    double r = 1.0;
                    if (pass == 1)
                    {
                        r = (ex - s_fitPx[i]) * s_fitDx[i] + (ez - s_fitPz[i]) * s_fitDz[i];
                        if (r <= 0.0) return false; // The estimate is behind this observation
                    }
                    double sr = s_fitSigma[i] * r;
                    double w = 1.0 / (sr * sr);
                    double nx = -s_fitDz[i], nz = s_fitDx[i];
                    double c = nx * s_fitPx[i] + nz * s_fitPz[i];
                    a11 += w * nx * nx;
                    a12 += w * nx * nz;
                    a22 += w * nz * nz;
                    b1 += w * nx * c;
                    b2 += w * nz * c;
                }
                double det = a11 * a22 - a12 * a12;
                if (det <= 1e-30 * (a11 * a22 + 1e-30)) return false;
                ex = (a22 * b1 - a12 * b2) / det;
                ez = (a11 * b2 - a12 * b1) / det;
            }
            s_fitU[0] = ex;
            s_fitU[1] = ez;
            s_fitU[2] = 0.0;
            s_fitU[3] = 0.0;
            return RefineBearingFit(n, 2, s_fitU, out posCov);
        }

        /// <summary>
        /// Pseudo-linear constant-velocity fit on the prepared samples (two passes), the start of the motion analysis.
        /// Result in s_fitU.
        /// </summary>
        private static bool SolveTmaLinearPrepared(int n)
        {
            double[] u = s_fitU;
            double[] a = s_fitStep;
            for (int r = 0; r < 4; r++) u[r] = 0.0;
            for (int pass = 0; pass < 2; pass++)
            {
                for (int r = 0; r < 4; r++) for (int c = 0; c < 5; c++) s_fitA[r, c] = 0.0;
                for (int i = 0; i < n; i++)
                {
                    double tau = s_fitTau[i];
                    double range = 1.0;
                    if (pass == 1)
                    {
                        double ex = u[0] + u[2] * tau - s_fitPx[i], ez = u[1] + u[3] * tau - s_fitPz[i];
                        range = ex * s_fitDx[i] + ez * s_fitDz[i];
                        if (range <= 0.0) return false;
                    }
                    double sr = s_fitSigma[i] * range;
                    double w = 1.0 / (sr * sr);
                    double nx = -s_fitDz[i], nz = s_fitDx[i];
                    a[0] = nx; a[1] = nz; a[2] = nx * tau; a[3] = nz * tau;
                    double rhs = nx * s_fitPx[i] + nz * s_fitPz[i];
                    for (int r = 0; r < 4; r++)
                    {
                        for (int c = 0; c < 4; c++) s_fitA[r, c] += w * a[r] * a[c];
                        s_fitA[r, 4] += w * a[r] * rhs;
                    }
                }
                if (!SolveNormal(4, s_fitA, s_fitInv, s_fitTrial)) return false;
                for (int r = 0; r < 4; r++) u[r] = s_fitTrial[r];
            }
            return true;
        }

        /// <summary>
        /// Largest deviation of the prepared observer positions from their least-squares constant-velocity line (m).
        /// </summary>
        private static double OwnManoeuvreMeters(int n)
        {
            double mt = 0.0, mx = 0.0, mz = 0.0;
            for (int i = 0; i < n; i++)
            {
                mt += s_fitTau[i];
                mx += s_fitPx[i];
                mz += s_fitPz[i];
            }
            mt /= n; mx /= n; mz /= n;
            double stt = 0.0, stx = 0.0, stz = 0.0;
            for (int i = 0; i < n; i++)
            {
                double dt = s_fitTau[i] - mt;
                stt += dt * dt;
                stx += dt * (s_fitPx[i] - mx);
                stz += dt * (s_fitPz[i] - mz);
            }
            if (stt <= 0.0) return 0.0;
            double vx = stx / stt, vz = stz / stt;
            double best = 0.0;
            for (int i = 0; i < n; i++)
            {
                double dt = s_fitTau[i] - mt;
                double rx = s_fitPx[i] - (mx + vx * dt), rz = s_fitPz[i] - (mz + vz * dt);
                double d = Math.Sqrt(rx * rx + rz * rz);
                if (d > best) best = d;
            }
            return best;
        }

        /// <summary>
        /// Tier 4 bearings-only motion analysis on an aircraft emitter (ADR-43): maximum-likelihood fit of the position at
        /// the newest bearing x0 and a constant velocity v to the own bearings of the observer in Tier4TmaWindow_s.
        /// Start: the pseudo-linear fit (n_i . (x0 + v tau_i - p_i) = 0), else the stationary fit with v = 0; then
        /// Levenberg-Marquardt on the bearing residuals. sigma = sqrt(C_xx + C_zz) of the position block, times
        /// sqrt(max(1, N / BearingIndependentSamples)). Without an own manoeuvre there is no fix.
        /// </summary>
        private static bool TrySolveTmaFix(TriangulationTrack track, Unit observer, float now,
            out Vector3 fix, out float sigmaPos, out float meanRange, out Vector3 velocity)
        {
            fix = Vector3.zero;
            velocity = Vector3.zero;
            sigmaPos = float.MaxValue;
            meanRange = 0.0f;
            List<BearingSample> list = track.OwnBearings;
            if (list == null || list.Count < MinTmaBearings) return false;

            float window = Tier4TmaWindow;
            s_fitIndices.Clear();
            int newest = -1;
            for (int i = 0; i < list.Count; i++)
            {
                BearingSample s = list[i];
                if (s.Observer != observer || now - s.Time > window) continue;
                s_fitIndices.Add(i);
                if (newest < 0 || s.Time > list[newest].Time) newest = i;
            }
            int n = s_fitIndices.Count;
            if (n < MinTmaBearings || newest < 0) return false;

            BearingSample last = list[newest];
            Vector3 origin = last.Position.ToLocalPosition();
            float oldest = last.Time;
            for (int k = 0; k < n; k++) oldest = Mathf.Min(oldest, list[s_fitIndices[k]].Time);
            if (last.Time - oldest < MinTmaSpanSeconds) return false;

            PrepareFitSamples(list, origin, last.Time);

            // The stationary fit gives the range scale of the manoeuvre gate and the fallback start
            double staticCov;
            if (!SolveStaticPrepared(n, out staticCov)) return false;
            double sx = s_fitU[0], sz = s_fitU[1];
            double staticRange = Math.Sqrt(sx * sx + sz * sz);
            if (OwnManoeuvreMeters(n) < MinTmaManoeuvreFraction * staticRange) return false;

            double cov = double.MaxValue;
            bool solved = SolveTmaLinearPrepared(n) && RefineBearingFit(n, 4, s_fitU, out cov);
            if (!solved)
            {
                s_fitU[0] = sx;
                s_fitU[1] = sz;
                s_fitU[2] = 0.0;
                s_fitU[3] = 0.0;
                if (!RefineBearingFit(n, 4, s_fitU, out cov)) return false;
            }
            cov *= Math.Max(1.0, n / (double)IndependentBearingSamples);
            if (cov <= 0.0 || double.IsNaN(cov) || double.IsInfinity(cov)) return false;
            sigmaPos = (float)Math.Sqrt(cov);

            double[] u = s_fitU;
            double rangeSum = 0.0;
            for (int i = 0; i < n; i++)
            {
                double ex = u[0] + u[2] * s_fitTau[i] - s_fitPx[i], ez = u[1] + u[3] * s_fitTau[i] - s_fitPz[i];
                rangeSum += Math.Sqrt(ex * ex + ez * ez);
            }
            meanRange = (float)(rangeSum / n);
            fix = origin + new Vector3((float)u[0], 0.0f, (float)u[1]);
            velocity = new Vector3((float)u[2], 0.0f, (float)u[3]);
            return !float.IsNaN(sigmaPos) && !float.IsInfinity(sigmaPos);
        }

        /// <summary>
        /// Gauss-Jordan with partial pivoting on the k x k normal matrix m (k at most 4, column 4 = right side): solution
        /// in x, inverse in inv. False when a pivot is below 1e-12 of the largest diagonal element (no observability).
        /// </summary>
        private static bool SolveNormal(int k, double[,] m, double[,] inv, double[] x)
        {
            double[,] w = s_fitWork;
            double scale = 0.0;
            for (int r = 0; r < k; r++)
            {
                for (int c = 0; c < k; c++) w[r, c] = m[r, c];
                for (int c = 0; c < k; c++) w[r, 4 + c] = (r == c) ? 1.0 : 0.0;
                w[r, 8] = m[r, 4];
                scale = Math.Max(scale, Math.Abs(m[r, r]));
            }
            if (!(scale > 0.0) || double.IsInfinity(scale)) return false;
            for (int col = 0; col < k; col++)
            {
                int piv = col;
                for (int r = col + 1; r < k; r++) if (Math.Abs(w[r, col]) > Math.Abs(w[piv, col])) piv = r;
                if (Math.Abs(w[piv, col]) < 1e-12 * scale) return false;
                if (piv != col)
                {
                    for (int c = 0; c < 9; c++) { double t = w[col, c]; w[col, c] = w[piv, c]; w[piv, c] = t; }
                }
                double d = w[col, col];
                for (int c = 0; c < 9; c++) w[col, c] /= d;
                for (int r = 0; r < k; r++)
                {
                    if (r == col) continue;
                    double f = w[r, col];
                    if (f == 0.0) continue;
                    for (int c = 0; c < 9; c++) w[r, c] -= f * w[col, c];
                }
            }
            for (int r = 0; r < k; r++)
            {
                x[r] = w[r, 8];
                for (int c = 0; c < k; c++) inv[r, c] = w[r, 4 + c];
            }
            return true;
        }

        /// <summary>
        /// Single-ship fix (ADR-42): maximum-likelihood intersection of all own bearings of the observer in the window,
        /// stationary emitter model in the horizontal plane. Start: the pseudo-linear least-squares fit (each bearing i is
        /// a line with normal n_i, w_i = 1 / (sigma_i r_i)^2, two passes); then Levenberg-Marquardt on the bearing
        /// residuals (the linear fit is biased to short range). sigma = sqrt((C_xx + C_zz) * max(1, N /
        /// BearingIndependentSamples)), combined with the emitter motion over the window. For a moving emitter the window
        /// holds only the bearings in which it moved less than half of the acceptance error at the present range.
        /// </summary>
        private static bool TrySolveOwnFix(TriangulationTrack track, Unit observer, float emitterSpeed, float trueDist, float now,
            out Vector3 fix, out float sigmaPos, out float meanRange)
        {
            fix = Vector3.zero;
            sigmaPos = float.MaxValue;
            meanRange = 0.0f;
            List<BearingSample> list = track.OwnBearings;
            if (list == null || list.Count < 2) return false;

            float window = SingleShipBaselineMaxAge;
            if (emitterSpeed > 1.0f)
            {
                window = Mathf.Min(window, 0.5f * FixRelativeErrorThreshold * trueDist / emitterSpeed);
            }

            s_fitIndices.Clear();
            int newest = -1;
            for (int i = 0; i < list.Count; i++)
            {
                BearingSample s = list[i];
                if (s.Observer != observer || now - s.Time > window) continue;
                s_fitIndices.Add(i);
                if (newest < 0 || s.Time > list[newest].Time) newest = i;
            }
            int n = s_fitIndices.Count;
            if (n < 2 || newest < 0) return false;

            BearingSample last = list[newest];
            Vector3 origin = last.Position.ToLocalPosition();
            float oldestTime = last.Time;
            float maxSine = 0.0f;
            for (int k = 0; k < n; k++)
            {
                BearingSample s = list[s_fitIndices[k]];
                if (s.Time < oldestTime) oldestTime = s.Time;
                float sine = Mathf.Abs(s.DirX * last.DirZ - s.DirZ * last.DirX);
                if (sine > maxSine) maxSine = sine;
            }
            float span = last.Time - oldestTime;
            if (span < MinOwnBaselineSeconds || maxSine < MinIntersectionSine) return false;

            float meanY = PrepareFitSamples(list, origin, last.Time);
            double posCov;
            if (!SolveStaticPrepared(n, out posCov)) return false;
            double ex = s_fitU[0], ez = s_fitU[1];

            double scale = Math.Max(1.0, n / (double)IndependentBearingSamples);
            float geometric = (float)Math.Sqrt(posCov * scale);
            float motion = emitterSpeed * span;
            sigmaPos = Mathf.Sqrt(geometric * geometric + motion * motion);

            double rangeSum = 0.0;
            for (int i = 0; i < n; i++)
            {
                double dx = ex - s_fitPx[i], dz = ez - s_fitPz[i];
                rangeSum += Math.Sqrt(dx * dx + dz * dz);
            }
            meanRange = (float)(rangeSum / n);
            fix = origin + new Vector3((float)ex, meanY, (float)ez);
            return !float.IsNaN(sigmaPos) && !float.IsInfinity(sigmaPos);
        }

        /// <summary>
        /// Marks a precise (radar or visual) contact: position known, no passive fix error.
        /// </summary>
        private static void MarkPreciseContact(TriangulationTrack track, Unit emitter, float now)
        {
            track.TriangulationErrorMeters = 0.0f;
            track.EstimatedCenter = GlobalPositionExtensions.GlobalPosition(emitter);
            track.LastPreciseContactTime = now;
            track.LastKnownGlobalPosition = GlobalPositionExtensions.GlobalPosition(emitter);
            track.LastKnownTime = now;
            track.LastKnownQuality = track.TrackingQuality;
            track.LastKnownFromFix = false;
            track.LastPreciseVelocity = (emitter.rb != null) ? emitter.rb.velocity : Vector3.zero;
            track.LastPreciseVelocityTime = now;
        }

        // Vanilla FactionHQ treats a unit as spotted for 4 s after its last detection
        public const float PreciseContactWindow = 4.0f;

        /// <summary>
        /// Target velocity the track's faction can use for an intercept lead:
        /// recent radar / visual contact -> live velocity;
        /// otherwise, inside the target's memory window -> velocity measured at the last precise contact;
        /// otherwise (bearing-only or expired) -> no lead.
        /// </summary>
        public static Vector3 GetFactionLeadVelocity(TriangulationTrack track, Unit target)
        {
            if (track == null || target == null) return Vector3.zero;
            if (track.HasRecentPreciseContact)
            {
                return (target.rb != null) ? target.rb.velocity : Vector3.zero;
            }
            // The track exists, so it is not forgotten (IsTrackForgotten)
            if (track.LastPreciseVelocityTime != 0.0f)
            {
                return track.LastPreciseVelocity;
            }
            return Vector3.zero;
        }

        /// <summary>
        /// Where the track's faction believes the target is, and its own error estimate:
        /// recent radar / visual contact -> live position, 0 m;
        /// fresh passive bearing fix -> fix center, 1-sigma fix error;
        /// otherwise, while the track is active or in memory -> last known position, error grown by
        /// dead-reckoning drift since then. A bearing-only track that was never located has no position.
        /// </summary>
        public static bool TryGetFactionEstimate(TriangulationTrack track, Unit target, out GlobalPosition position, out float errorMeters)
        {
            position = default(GlobalPosition);
            errorMeters = float.MaxValue;
            if (track == null || target == null) return false;

            float now = Time.timeSinceLevelLoad;
            if (track.HasRecentPreciseContact)
            {
                position = GlobalPositionExtensions.GlobalPosition(target);
                errorMeters = 0.0f;
                return true;
            }

            if (track.IsTriangulated && track.TriangulationErrorMeters > 0.0f && track.LastFixTime != 0.0f &&
                now - track.LastFixTime < BearingObservationMaxAge)
            {
                position = track.EstimatedCenter;
                errorMeters = track.TriangulationErrorMeters;
                return true;
            }

            if (track.LastKnownTime != 0.0f && (track.IsActivelyDetected || track.IsInMemoryState() || track.PersistentStatic))
            {
                position = track.LastKnownGlobalPosition;
                float driftSpeed = TrackUncertaintyCalculator.GetMemoryDriftSpeed(target, track.LastKnownVelocity.magnitude);
                // Dead-reckoning belief, not the true displacement; the age is bounded by the memory time cap
                errorMeters = driftSpeed * Mathf.Max(0.0f, now - track.LastKnownTime);
                return true;
            }
            return false;
        }

        // Mirror client: the faction of the host tracks (TrackReset); it wins over the cached local faction
        private static FactionHQ s_mirrorFaction;

        /// <summary>
        /// Mirror client: the host sends the tracks of this faction (null = none or not known).
        /// </summary>
        public static void SetMirrorFaction(FactionHQ hq)
        {
            s_mirrorFaction = hq;
            if (hq != null) _lastPlayerHQ = hq;
        }

        public static FactionHQ GetPlayerFactionHQ()
        {
            if (s_mirrorFaction != null && CnoEvilNet.IsMirrorClient) return s_mirrorFaction;
            if (CombatHUD.i != null && CombatHUD.i.aircraft != null && CombatHUD.i.aircraft.NetworkHQ != null)
            {
                _lastPlayerHQ = CombatHUD.i.aircraft.NetworkHQ;
            }
            if (_lastPlayerHQ == null && DynamicMap.i != null && DynamicMap.i.HQ != null)
            {
                _lastPlayerHQ = DynamicMap.i.HQ;
            }
            if (_lastPlayerHQ == null)
            {
                try
                {
                    Faction localFaction;
                    if (GameManager.GetLocalFaction(out localFaction) && localFaction != null)
                    {
                        _lastPlayerHQ = FactionRegistry.HQFromFaction(localFaction);
                    }
                }
                catch (Exception ex) { PatchErrorLog.Once("RWRTriangulationProcessor.GetPlayerFactionHQ", ex); }
            }
            return _lastPlayerHQ;
        }

        public static Dictionary<Unit, TriangulationTrack> GetTrackDictionary(FactionHQ faction)
        {
            if (faction == null) faction = GetPlayerFactionHQ();
            if (faction == null) return _fallbackTracks;

            Dictionary<Unit, TriangulationTrack> dict;
            if (!_factionTracks.TryGetValue(faction, out dict))
            {
                dict = new Dictionary<Unit, TriangulationTrack>();
                _factionTracks[faction] = dict;
            }
            return dict;
        }

        /// <summary>
        /// All faction track dictionaries, for read-only callers that must look at every faction.
        /// Do not modify.
        /// </summary>
        public static Dictionary<FactionHQ, Dictionary<Unit, TriangulationTrack>> GetAllFactionTracks()
        {
            return _factionTracks;
        }

        /// <summary>
        /// Read-only track lookup: no track is created. Tracks are made only by the sensor records (radar, visual,
        /// RWR bearings, datalink).
        /// </summary>
        public static bool TryPeekTrack(FactionHQ faction, Unit emitter, out TriangulationTrack track)
        {
            track = null;
            if (emitter == null) return false;
            if (faction == null) faction = GetPlayerFactionHQ();
            Dictionary<Unit, TriangulationTrack> tracks;
            if (faction == null) tracks = _fallbackTracks;
            else if (!_factionTracks.TryGetValue(faction, out tracks)) return false;
            return tracks.TryGetValue(emitter, out track) && track != null;
        }

        /// <summary>
        /// Registers or refreshes active visual reconnaissance contact on a target from any operational friendly unit (bot or player).
        /// Fuses high-confidence visual intelligence into the faction's Tactical Datalink.
        /// </summary>
        public static void RecordVisualContact(FactionHQ faction, Unit target, Unit observer)
        {
            RecordVisualContact(faction, target, observer, 1.0f);
        }

        /// <summary>
        /// Visual contact held for holdSeconds (the spotter's check interval with margin), so Q and the active state
        /// do not drop between two checks of the same spotter.
        /// </summary>
        public static void RecordVisualContact(FactionHQ faction, Unit target, Unit observer, float holdSeconds)
        {
            if (target == null || target.disabled || target is Missile) return;
            if (faction == null) faction = (observer != null) ? observer.NetworkHQ : GetPlayerFactionHQ();
            if (faction == null) return;

            // Only tracked threat types and strategic buildings (ThreatClassification)
            bool strategicBuilding = ThreatClassification.IsStrategicBuilding(target, faction);
            if (!strategicBuilding && !ThreatClassification.IsTrackedThreat(target, faction)) return;

            float now = Time.timeSinceLevelLoad;
            Dictionary<Unit, TriangulationTrack> tracks = GetTrackDictionary(faction);
            TriangulationTrack track;
            if (!tracks.TryGetValue(target, out track))
            {
                track = CreateTrack(target);
                tracks[target] = track;
            }

            float spotterDist = (observer != null) ? Vector3.Distance(observer.transform.position, target.transform.position) : 0.0f;
            float farQuality = strategicBuilding ? CnoEvilConfig.StrategicBuildingVisualQuality.Value : CnoEvilConfig.VisualContactQuality.Value;
            float visualQuality = TrackUncertaintyCalculator.VisualContactQualityFor(target, farQuality, spotterDist);
            TrackUncertaintyCalculator.RecordVisualContact(track, visualQuality, spotterDist);
            track.VisualHoldUntil = Mathf.Max(track.VisualHoldUntil, now + Mathf.Max(1.0f, holdSeconds));
            MarkPreciseContact(track, target, now);
            RefreshMotionState(track, target);
            track.LastUpdateTime = now;
        }

        /// <summary>
        /// Registers or refreshes active radar illumination on a target from an allied radar.
        /// The radar adds its Q fill rate (range, surface station accuracy, own or allied channel, ADR-44); the rates
        /// of all radars of the faction that see the target add up.
        /// </summary>
        public static void RecordRadarIllumination(FactionHQ faction, Unit target, Radar sourceRadar)
        {
            if (target == null || target.disabled || target is Missile || sourceRadar == null) return;
            // The caller resolves the radar's faction; a neutral radar (no faction) feeds nobody
            if (faction == null) return;
            Unit sourceUnit = sourceRadar.GetAttachedUnit();

            // Only tracked threat types (ThreatClassification)
            if (!ThreatClassification.IsTrackedThreat(target, faction)) return;

            float now = Time.timeSinceLevelLoad;
            Dictionary<Unit, TriangulationTrack> tracks = GetTrackDictionary(faction);
            TriangulationTrack track;
            if (!tracks.TryGetValue(target, out track))
            {
                track = CreateTrack(target);
                track.LastRadarIlluminatedTime = now;
                tracks[target] = track;
            }

            // A mirror client keeps the contact time of its own radar; Q comes from the host (ApplyRemoteTrack)
            if (CnoEvilNet.IsMirrorClient)
            {
                MarkPreciseContact(track, target, now);
                RefreshMotionState(track, target);
                track.LastUpdateTime = now;
                return;
            }

            // Team-wide Q fill (ADR-44): this radar adds its fill rate; the rates of all radars of the faction add up
            float sweepDist = (sourceUnit != null) ? Vector3.Distance(sourceUnit.transform.position, target.transform.position) : 30000f;
            TrackUncertaintyCalculator.RecordRadarContact(track, sweepDist);
            TrackUncertaintyCalculator.RecordSensorRate(track, sourceRadar, RadarFillRate(sourceRadar, sourceUnit, sweepDist));

            MarkPreciseContact(track, target, now);
            RefreshMotionState(track, target);
            track.LastUpdateTime = now;
        }

        /// <summary>
        /// A track radar of this faction tracks an active decoy in place of the target (RadarDecoySeduction): a false fix
        /// at the decoy, with the error of half the range gate. It is not a precise contact, so a recent radar or visual
        /// contact of another sensor still wins (TryGetFactionEstimate). Only an existing track changes; Q does not grow.
        /// </summary>
        public static void RecordDecoyContact(FactionHQ faction, Unit target, ChaffCloud decoy, Radar sourceRadar)
        {
            if (target == null || target.disabled || decoy == null || decoy.Chaff == null || sourceRadar == null) return;
            if (faction == null)
            {
                Unit sourceUnit = sourceRadar.GetAttachedUnit();
                faction = (sourceUnit != null) ? sourceUnit.NetworkHQ : null;
            }
            if (faction == null) return;

            TriangulationTrack track;
            if (!GetTrackDictionary(faction).TryGetValue(target, out track) || track == null) return;
            // A precise contact (radar / visual) is never degraded by the false fix while it is recent
            if (track.HasRecentPreciseContact) return;

            float now = Time.timeSinceLevelLoad;
            Vector3 decoyPos = decoy.Position;
            track.IsTriangulated = true;
            track.EstimatedCenter = GlobalPositionExtensions.ToGlobalPosition(decoyPos);
            track.TriangulationErrorMeters = Mathf.Max(1.0f, 0.5f * CnoEvilConfig.ChaffSeekerRangeGate_m.Value);
            track.LastFixTime = now;
            track.LastKnownGlobalPosition = GlobalPositionExtensions.ToGlobalPosition(decoyPos);
            track.LastKnownTime = now;
            track.LastKnownQuality = track.TrackingQuality;
            track.LastKnownFromFix = true;
            // The faction knows the decoy, not the target. LastUpdateTime stays: a decoy is no RWR ping.
            track.LastKnownVelocity = decoy.Velocity;
        }

        public static void UpdateTrack(Unit emitter, Aircraft playerAircraft, Vector3 worldBearing, float signalStrength01)
        {
            UpdateTrack(emitter, playerAircraft, worldBearing, signalStrength01, true);
        }

        /// <param name="allowSingleShipFix">False: this bearing never starts or completes a single-ship kinematic fix
        /// (jammer strobes of a Tier 1 receiver). Shared bearings and precise contacts still apply.</param>
        public static void UpdateTrack(Unit emitter, Aircraft playerAircraft, Vector3 worldBearing, float signalStrength01, bool allowSingleShipFix)
        {
            UpdateTrack(emitter, playerAircraft, worldBearing, signalStrength01, allowSingleShipFix, -1.0f);
        }

        /// <summary>
        /// IRST bearing from the HMD thermal window of a pilot (HMDThermalWindow, IrstSync on the host): a bearing without
        /// range to a hostile aircraft, with the bearing error of the optics instead of the RWR jitter.
        /// </summary>
        public static void RegisterIrstBearing(Aircraft observer, Unit target, Vector3 measuredBearing, float strength01, float sigmaRad)
        {
            if (observer == null || target == null || observer.disabled || target.disabled) return;
            if (!(target is Aircraft) && !ThermalSignature.IsGroundHeatTarget(target)) return;
            UpdateTrack(target, observer, measuredBearing.normalized, Mathf.Clamp01(strength01), true, Mathf.Max(0.0001f, sigmaRad));
        }

        /// <param name="sigmaRadOverride">Above 0: the 1-sigma bearing error of this observation (IRST); otherwise the RWR jitter.</param>
        private static void UpdateTrack(Unit emitter, Aircraft playerAircraft, Vector3 worldBearing, float signalStrength01, bool allowSingleShipFix,
            float sigmaRadOverride)
        {
            if (emitter == null || playerAircraft == null || emitter.disabled || playerAircraft.disabled)
            {
                return;
            }

            FactionHQ hq = playerAircraft.NetworkHQ != null ? playerAircraft.NetworkHQ : GetPlayerFactionHQ();
            // Friendly or neutral (no faction) radar emission, never treat as threat. Missiles may have no NetworkHQ.
            FactionHQ emitterHq = DatalinkNetwork.GetUnitFactionHQ(emitter);
            if (emitterHq != null ? emitterHq == hq : !(emitter is Missile))
            {
                return;
            }
            Dictionary<Unit, TriangulationTrack> tracks = GetTrackDictionary(hq);

            float now = Time.timeSinceLevelLoad;
            // Single-ship kinematic fix for every human pilot (local or remote: the host runs all receivers)
            bool isLocalPlayer = (CombatHUD.i != null && CombatHUD.i.aircraft != null && playerAircraft == CombatHUD.i.aircraft) ||
                                 playerAircraft.Player != null;
            TriangulationTrack track;
            if (!tracks.TryGetValue(emitter, out track))
            {
                track = CreateTrack(emitter);
                tracks[emitter] = track;
            }

            track.LastUpdateTime = now;
            track.HadActiveContact = true;
            track.LastActiveDetectionTime = now;
            RefreshMotionState(track, emitter); // A bearing alone does not locate the emitter

            // Compute hardware tier
            PhysRWRReceiver rwr = PhysRWRReceiver.Get(playerAircraft);
            RwrTier tier = rwr != null ? rwr.Capabilities.Tier : RwrTier.Tier2_Standard;
            // A Tier 1 (analog quadrant) receiver makes no single-ship fix; an IRST bearing (own sigma) does
            bool singleShipFix = isLocalPlayer && allowSingleShipFix && !(emitter is Missile) &&
                                 (tier != RwrTier.Tier1_Basic || sigmaRadOverride > 0.0f || (rwr != null && rwr.IsTier4));

            Vector3 playerPos = playerAircraft.transform.position;
            Vector3 emitterPos = emitter.transform.position;
            float trueDist = Vector3.Distance(playerPos, emitterPos);

            // This receiver adds its Q fill rate (range and tier); the rates of all receivers of the faction add up (ADR-44)
            TrackUncertaintyCalculator.RecordRWRDetection(track, playerAircraft, trueDist, tier, rwr != null && rwr.IsTier4);

            // Receiver bearing error: uniform +/-J degrees -> 1-sigma J/sqrt(3)
            float jitterDeg = (rwr != null) ? rwr.EffectiveBearingJitterDegrees : RWRTierDatabase.GetTier2Capabilities().AzimuthJitterDegrees;
            float sigmaRad = (sigmaRadOverride > 0.0f) ? sigmaRadOverride : jitterDeg * Mathf.Deg2Rad / Mathf.Sqrt(3.0f);
            bool sharesBearings = (rwr != null) && rwr.SharesBearings;

            bool isPrecise = false;

            // 1. Active radar track by any radar of this faction (ownship included)
            Dictionary<Unit, float> factionDetected = GetFactionDetections(hq);
            if (factionDetected != null && factionDetected.ContainsKey(emitter))
            {
                isPrecise = true;
                track.LastRadarIlluminatedTime = now;
            }

            // 2. Visual contact of any faction spotter (vanilla optics, TargetDetector_DetectTarget_Patch)
            if (!isPrecise && track.HasVisualContact)
            {
                isPrecise = true;
            }

            if (isPrecise)
            {
                track.IsTriangulated = true;
                MarkPreciseContact(track, emitter, now);
            }

            // 3. Cooperative multi-station bearing fix across the faction datalink (Advanced datalink stations only)
            bool enableDatalink = CnoEvilConfig.EnableAlliedDatalink.Value;
            bool hasFix = false;
            if (enableDatalink)
            {
                RecordAlliedBearing(hq, emitter, playerAircraft, playerPos, worldBearing, sigmaRad, sharesBearings, now);

                Vector3 fix;
                float sigmaPos, meanRange, fixTime;
                if (!isPrecise && TryGetMultiStationFix(hq, emitter, now, out fix, out sigmaPos, out meanRange, out fixTime) &&
                    fixTime > track.LastFixTime)
                {
                    hasFix = ApplyBearingFix(track, emitter, fix, sigmaPos, meanRange, true, fixTime);
                }
            }

            // 4. Single-ship fix (ADR-42): least-squares fit of all own bearings in the window (cross-bearing flight).
            // Emitter motion over the window limits passive ranging on fast movers.
            // Tier 4 (ADR-43): elevation ranging on surface emitters, motion analysis on aircraft; the best fix wins.
            if (singleShipFix)
            {
                // Solve only on a new own bearing: between two samples nothing is new
                bool recorded = RecordOwnBearing(track, playerAircraft, playerPos, worldBearing, sigmaRad, now);
                if (!isPrecise && !hasFix && recorded)
                {
                    Vector3 bestFix = Vector3.zero;
                    float bestSigma = float.MaxValue, bestRange = 0.0f;
                    bool bestSmooth = false;

                    Vector3 fix;
                    float sigmaPos, meanRange;
                    float emitterSpeed = (emitter.rb != null) ? emitter.rb.velocity.magnitude : 0.0f;
                    if (TrySolveOwnFix(track, playerAircraft, emitterSpeed, trueDist, now, out fix, out sigmaPos, out meanRange))
                    {
                        bestFix = fix; bestSigma = sigmaPos; bestRange = meanRange;
                    }

                    bool tier4 = rwr != null && rwr.IsTier4 && sigmaRadOverride <= 0.0f;
                    if (tier4)
                    {
                        if (emitter is Aircraft)
                        {
                            Vector3 tmaVelocity;
                            if (TrySolveTmaFix(track, playerAircraft, now, out fix, out sigmaPos, out meanRange, out tmaVelocity) &&
                                sigmaPos < bestSigma)
                            {
                                bestFix = fix; bestSigma = sigmaPos; bestRange = meanRange; bestSmooth = false;
                            }
                        }
                        else
                        {
                            float elevation = rwr.MeasureElevation(emitterPos - playerPos);
                            float groundRange;
                            if (TrySolveElevationFix(playerPos, worldBearing, elevation, sigmaRad, rwr.Tier4ElevationSigmaRad,
                                    out fix, out sigmaPos, out groundRange) && sigmaPos < bestSigma)
                            {
                                bestFix = fix; bestSigma = sigmaPos; bestRange = groundRange; bestSmooth = true;
                            }
                        }
                    }

                    if (bestSigma < float.MaxValue)
                    {
                        ApplyBearingFix(track, emitter, bestFix, bestSigma, bestRange, bestSmooth, now);
                    }
                }
            }

            if (!track.IsTriangulated)
            {
                // Untriangulated: the map shows a bearing cone toward the emitter
                track.EstimatedCenter = GlobalPositionExtensions.ToGlobalPosition(emitterPos);
            }

            // True position for debug overlays; weapon dispersion and the fix error model the miss distance
            track.TriangulatedWorldPos = emitterPos;

            track.UncertaintyObserverDist = trueDist;
            track.UncertaintySignal01 = signalStrength01;
            UpdateUncertaintyRadius(track, trueDist, signalStrength01);
        }

        /// <summary>
        /// PortalMFD uncertainty ring: the CEP of the track (Q and observer distance), scaled by signal strength and
        /// PortalUncertaintyRingScale. Recomputed every track update.
        /// </summary>
        private static void UpdateUncertaintyRadius(TriangulationTrack track, float observerDist, float signalStrength01)
        {
            float rawCep = TrackUncertaintyCalculator.CalculateCep(track.TrackingQuality, observerDist);

            float sigFactor = 1.0f + (1.0f - Mathf.Clamp01(signalStrength01)) * 0.5f;
            float circleScale = Mathf.Clamp(CnoEvilConfig.PortalUncertaintyRingScale.Value, 0.2f, 3.0f);
            track.UncertaintyRadiusMeters = Mathf.Max(15.0f, rawCep * sigFactor * circleScale);
        }

        /// <summary>
        /// Target motion (drives memory drift) and the true position for debug overlays.
        /// The known position itself is written only by precise contacts and passive fixes.
        /// </summary>
        private static void RefreshMotionState(TriangulationTrack track, Unit target)
        {
            track.LastKnownVelocity = (target.rb != null) ? target.rb.velocity : Vector3.zero;
            track.TriangulatedWorldPos = target.transform.position;
        }

        /// <summary>
        /// Visual identification range of strategic buildings (StrategicBuildingVisualRange_m). All other units are
        /// identified by the vanilla optics of the spotter (TargetDetector_DetectTarget_Patch).
        /// </summary>
        public static float StrategicBuildingVisualRange
        {
            get { return Mathf.Max(CnoEvilConfig.StrategicBuildingVisualRange_m.Value, 100.0f); }
        }

        /// <summary>
        /// True when the observer can visually identify the strategic building: within StrategicBuildingVisualRange and
        /// with terrain / structure line of sight (hits on the building itself do not block).
        /// </summary>
        public static bool CanSeeStrategicBuilding(Unit observer, Unit target)
        {
            if (observer == null || target == null || observer.disabled || target.disabled) return false;

            Vector3 observerPos = observer.transform.position;
            Vector3 targetPos = target.transform.position;
            float range = StrategicBuildingVisualRange;
            Vector3 los = targetPos - observerPos;
            float distSq = los.sqrMagnitude;
            if (distSq > range * range) return false;
            if (distSq < 25.0f) return true;

            float dist = Mathf.Sqrt(distSq);
            Vector3 dir = los / dist;
            Vector3 eye = observerPos + Vector3.up * 2.0f + dir * Mathf.Max(2.0f, observer.maxRadius);
            Vector3 aim = targetPos + Vector3.up * 1.5f;
            int obstacleMask = (int)PhysicsLayers.StaticsMask | (int)PhysicsLayers.ShipsMask;

            RaycastHit hit;
            if (!Physics.Linecast(eye, aim, out hit, obstacleMask)) return true;

            float clearRadius = Mathf.Max(5.0f, target.maxRadius * 1.5f);
            return (hit.point - targetPos).sqrMagnitude <= clearRadius * clearRadius;
        }

        /// <summary>
        /// Fills tracking quality with the summed sensor rates (dt > 0 only, ADR-44) and maintains the target memory
        /// state for lost contacts. Q never falls while the track exists: in memory it holds until the track is
        /// forgotten (displacement or time cap, ADR-33).
        /// </summary>
        private static void ProcessTrackQuality(TriangulationTrack track, Unit emitter, Dictionary<Unit, float> factionDetected, float dt)
        {
            if (track == null || emitter == null) return;

            float now = Time.timeSinceLevelLoad;

            float radarDist = 0.0f;
            bool isDirectlyIlluminated = factionDetected != null && factionDetected.TryGetValue(emitter, out radarDist);
            if (isDirectlyIlluminated)
            {
                track.LastRadarIlluminatedTime = now;
            }
            bool hasVisual = now < track.VisualHoldUntil;
            if (isDirectlyIlluminated || hasVisual)
            {
                MarkPreciseContact(track, emitter, now);
            }

            // Contact is ACTIVE while a visual contact is held (spotter check interval), illuminated by a faction radar
            // within the last sweep interval, or pinged on RWR within ContactHold_s
            float hold = CnoEvilConfig.ContactHold_s.Value;
            bool hasRadarLock = isDirectlyIlluminated || (now - track.LastRadarIlluminatedTime < hold);
            bool hasRecentRwrPing = (now - track.LastUpdateTime < hold);

            if (hasVisual || hasRadarLock || hasRecentRwrPing)
            {
                // A radar or visual contact restarts the hold and the memory countdown; inside the hold alone the
                // countdown keeps running from the last detection
                track.HadActiveContact = true;
                if (isDirectlyIlluminated || hasVisual) track.LastActiveDetectionTime = now;
                RefreshMotionState(track, emitter);

                if (isDirectlyIlluminated && dt > 0.0f)
                {
                    TrackUncertaintyCalculator.RecordRadarContact(track, radarDist);
                }
                // Q fills with the summed rate of the radars and receivers that report the target (ADR-44);
                // no decay while active contact (including passive RWR pings) is present
                TrackUncertaintyCalculator.ApplyQualityFill(track, dt, now);
            }
            // CONTACT IS LOST: Q holds its value until the track is forgotten (IsTrackForgotten: displacement or time
            // cap, ADR-33). Coordinates stay frozen at the last known position; the memory drift enters the weapon CEP.

            // A strategic or radar building located with a quality track or a passive fix stays as a ghost until it is destroyed
            if (!track.PersistentStatic &&
                (track.TrackingQuality >= CnoEvilConfig.LocatedQualityThreshold.Value || track.PassiveLocated) &&
                ThreatClassification.IsPersistentStatic(emitter))
            {
                track.PersistentStatic = true;
            }

            // Uncertainty ring from the current Q: the last RWR observer distance, else the closest sensor
            float ringDist = track.UncertaintyObserverDist > 0.0f ? track.UncertaintyObserverDist : track.SensorRangeMeters;
            if (ringDist > 0.0f)
            {
                UpdateUncertaintyRadius(track, ringDist, track.UncertaintySignal01);
            }
        }

        // Per-faction radar detections (target -> distance to the nearest detecting radar), rebuilt every UpdateAllTracks
        private static readonly Dictionary<FactionHQ, Dictionary<Unit, float>> s_factionDetections = new Dictionary<FactionHQ, Dictionary<Unit, float>>();
        private static readonly List<Dictionary<Unit, float>> s_detectionPool = new List<Dictionary<Unit, float>>();

        private static Dictionary<Unit, float> GetFactionDetections(FactionHQ hq)
        {
            if (hq == null) return null;
            Dictionary<Unit, float> set;
            return s_factionDetections.TryGetValue(hq, out set) ? set : null;
        }

        private static void AddRadarDetections(Radar radar, bool datalinkEnabled)
        {
            // A radar that does not transmit keeps its last scan in detectedTargets (vanilla): skip it
            if (radar == null || !radar.IsOperational() || !radar.activated || radar.detectedTargets == null || radar.detectedTargets.Count == 0) return;
            Unit radarUnit = DatalinkNetwork.GetRadarAttachedUnit(radar);
            if (radarUnit == null || radarUnit.disabled) return;
            FactionHQ hq = DatalinkNetwork.GetUnitFactionHQ(radarUnit);
            if (hq == null) return;

            // Without the datalink a faction with a human pilot only knows what the human pilots' radars see
            if (!datalinkEnabled && DatalinkNetwork.BlockedWithoutDatalink(hq, radarUnit)) return;

            Dictionary<Unit, float> set;
            if (!s_factionDetections.TryGetValue(hq, out set))
            {
                if (s_detectionPool.Count > 0)
                {
                    set = s_detectionPool[s_detectionPool.Count - 1];
                    s_detectionPool.RemoveAt(s_detectionPool.Count - 1);
                }
                else
                {
                    set = new Dictionary<Unit, float>();
                }
                s_factionDetections[hq] = set;
            }

            // Host: every radar that holds the target adds its Q fill rate to the existing track (ADR-44)
            Dictionary<Unit, TriangulationTrack> tracks = CnoEvilNet.IsMirrorClient ? null : GetTrackDictionary(hq);
            Vector3 radarPos = radarUnit.transform.position;
            List<Unit> targets = radar.detectedTargets;
            for (int t = 0; t < targets.Count; t++)
            {
                Unit u = targets[t];
                if (u == null) continue;
                float d = Vector3.Distance(radarPos, u.transform.position);
                float existing;
                if (!set.TryGetValue(u, out existing) || d < existing)
                {
                    set[u] = d;
                }
                TriangulationTrack track;
                if (tracks != null && tracks.TryGetValue(u, out track) && track != null)
                {
                    TrackUncertaintyCalculator.RecordSensorRate(track, radar, RadarFillRate(radar, radarUnit, d));
                }
            }
        }

        /// <summary>
        /// Q fill rate of this radar on a target at distance (TrackUncertaintyCalculator.RadarFillRate): the own radar of a
        /// human pilot gets the full rate, any other radar of the faction AlliedRadarSweepFactor; surface search radars of
        /// 50 km and more (radar stations, Hyperion, carriers) and units coded RDR get SurfaceRadarQualityFactor.
        /// </summary>
        private static float RadarFillRate(Radar radar, Unit radarUnit, float distance)
        {
            Aircraft aircraft = radarUnit as Aircraft;
            bool humanPilot = aircraft != null &&
                              ((CombatHUD.i != null && aircraft == CombatHUD.i.aircraft) || aircraft.Player != null);
            bool surfaceStation = radarUnit != null && aircraft == null &&
                                  ((radar != null && radar.RadarParameters.maxRange >= 50000f) ||
                                   (radarUnit.definition != null && radarUnit.definition.code == "RDR"));
            return TrackUncertaintyCalculator.RadarFillRate(distance, humanPilot, surfaceStation);
        }

        private static void RebuildFactionDetections()
        {
            foreach (var kvp in s_factionDetections)
            {
                kvp.Value.Clear();
                s_detectionPool.Add(kvp.Value);
            }
            s_factionDetections.Clear();

            bool datalinkEnabled = CnoEvilConfig.EnableAlliedDatalink.Value;

            List<Radar> registered = DatalinkNetwork.RegisteredRadars;
            for (int i = 0; i < registered.Count; i++)
            {
                AddRadarDetections(registered[i], datalinkEnabled);
            }

            // Native HQ radars (ground stations / SAM sites) not yet picked up by the datalink census
            try
            {
                var allHqs = FactionRegistry.GetAllHQs();
                if (allHqs != null)
                {
                    foreach (FactionHQ hq in allHqs)
                    {
                        List<Radar> hqRadars = DatalinkNetwork.GetHqRadars(hq);
                        if (hqRadars == null) continue;
                        for (int i = 0; i < hqRadars.Count; i++)
                        {
                            if (!DatalinkNetwork.IsRadarRegistered(hqRadars[i]))
                            {
                                AddRadarDetections(hqRadars[i], datalinkEnabled);
                            }
                        }
                    }
                }
            }
            catch (Exception ex) { PatchErrorLog.Once("RWRTriangulationProcessor.HqRadarDetections", ex); }
        }

        private static readonly List<KeyValuePair<FactionHQ, Dictionary<Unit, TriangulationTrack>>> _allDictsBuffer =
            new List<KeyValuePair<FactionHQ, Dictionary<Unit, TriangulationTrack>>>();

        // Vanilla LaserDesignator refreshes the lased state each 0.25 s; the contact holds for two refreshes
        private const float LaserHoldSeconds = 0.5f;

        // The track update runs at most every TrackUpdateIntervalSeconds with the summed frame time
        // (Q fill is exponential and decay is linear in dt, so the rates do not change)
        private const float TrackUpdateIntervalSeconds = 0.1f;
        private const float BearingPruneIntervalSeconds = 5.0f;
        private static float s_pendingTrackDt;
        private static float s_nextBearingPruneTime;
        private static readonly List<Unit> s_emptyBearingKeys = new List<Unit>();

        /// <summary>
        /// Called every frame from CnoEvilPlugin.Update().
        /// Updates every faction's tracks from that faction's own sensors (radars, datalink bearings, visual
        /// contacts) and prunes dead or forgotten tracks.
        /// </summary>
        public static void UpdateAllTracks(float dt)
        {
            s_pendingTrackDt += dt;
            if (s_pendingTrackDt < TrackUpdateIntervalSeconds) return;
            float step = s_pendingTrackDt;
            s_pendingTrackDt = 0.0f;
            UpdateAllTracksNow(step);
            PruneBearingObservations();
        }

        /// <summary>
        /// Drops emitters without a fresh bearing from the multi-station observation lists.
        /// </summary>
        private static void PruneBearingObservations()
        {
            float now = Time.timeSinceLevelLoad;
            if (now < s_nextBearingPruneTime) return;
            s_nextBearingPruneTime = now + BearingPruneIntervalSeconds;
            float maxAge = BearingObservationMaxAge;

            foreach (var hqObs in _factionBearingObs)
            {
                s_emptyBearingKeys.Clear();
                foreach (var kv in hqObs.Value)
                {
                    List<AlliedBearingObservation> list = kv.Value;
                    for (int i = list.Count - 1; i >= 0; i--)
                    {
                        if (now - list[i].Timestamp > maxAge || list[i].Observer == null) list.RemoveAt(i);
                    }
                    if (list.Count == 0 || kv.Key == null) s_emptyBearingKeys.Add(kv.Key);
                }
                for (int i = 0; i < s_emptyBearingKeys.Count; i++) hqObs.Value.Remove(s_emptyBearingKeys[i]);
            }
        }

        private static void UpdateAllTracksNow(float dt)
        {
            if (CnoEvilNet.IsMirrorClient)
            {
                UpdateMirrorTracks();
                return;
            }

            RebuildFactionDetections();

            _allDictsBuffer.Clear();
            foreach (var kvp in _factionTracks)
            {
                if (kvp.Value != null && kvp.Value.Count > 0)
                {
                    _allDictsBuffer.Add(kvp);
                }
            }
            if (_fallbackTracks.Count > 0)
            {
                _allDictsBuffer.Add(new KeyValuePair<FactionHQ, Dictionary<Unit, TriangulationTrack>>(GetPlayerFactionHQ(), _fallbackTracks));
            }

            if (_allDictsBuffer.Count == 0)
            {
                VanillaKnowledgeExpiry.Process(Time.timeSinceLevelLoad);
                return;
            }

            bool enableDatalink = CnoEvilConfig.EnableAlliedDatalink.Value;
            float now = Time.timeSinceLevelLoad;
            bool logTracks = EvilLog.On(LogCat.Track);

            for (int d = 0; d < _allDictsBuffer.Count; d++)
            {
                FactionHQ hq = _allDictsBuffer[d].Key;
                Dictionary<Unit, TriangulationTrack> dict = _allDictsBuffer[d].Value;
                Dictionary<Unit, float> factionDetected = GetFactionDetections(hq);
                _staleList.Clear();

                foreach (var kvp in dict)
                {
                    Unit target = kvp.Key;
                    TriangulationTrack track = kvp.Value;

                    if (IsTrackForgotten(track, target, now))
                    {
                        // The faction forgets the unit: the vanilla entry follows unless vanilla still sees it (ADR-15, ADR-33)
                        if (target != null && !target.disabled) VanillaKnowledgeExpiry.MarkExpired(hq, target);
                        if (logTracks) EvilEvents.TrackForgotten(hq, target, (target == null || target.disabled) ? "unit gone" : "memory time over");
                        _staleList.Add(target);
                        continue;
                    }

                    // Only a fix from a newer bearing: the stored bearings stay up to BearingObservationMaxAge_s, and a fix
                    // from the same bearings again would look new and re-anchor the memory every update
                    Vector3 fix;
                    float sigmaPos, meanRange, fixTime;
                    if (enableDatalink && TryGetMultiStationFix(hq, target, now, out fix, out sigmaPos, out meanRange, out fixTime) &&
                        fixTime > track.LastFixTime)
                    {
                        ApplyBearingFix(track, target, fix, sigmaPos, meanRange, true, fixTime);
                    }

                    // Laser designation of the faction (vanilla lased state, refreshed each 0.25 s): precise contact
                    if (hq != null && target != null && hq.IsTargetLased(target))
                    {
                        TrackUncertaintyCalculator.RecordLaserContact(track, LaserHoldSeconds);
                    }

                    ProcessTrackQuality(track, target, factionDetected, dt);
                    if (logTracks) EvilEvents.TrackTick(hq, target, track, now);
                }

                for (int i = 0; i < _staleList.Count; i++)
                {
                    dict.Remove(_staleList[i]);
                }
            }

            VanillaKnowledgeExpiry.Process(now);
        }

        /// <summary>
        /// Mirror client: the host owns Q, fixes and forgetting. Only the own faction is kept; the own radar and optics
        /// mark precise contacts at once (dt 0: no local Q dwell or decay); dead units and expired local tracks drop out.
        /// </summary>
        private static void UpdateMirrorTracks()
        {
            RebuildFactionDetections();
            FactionHQ own = GetPlayerFactionHQ();
            _fallbackTracks.Clear();

            foreach (var kvp in _factionTracks)
            {
                Dictionary<Unit, TriangulationTrack> dict = kvp.Value;
                if (dict == null || dict.Count == 0) continue;
                if (kvp.Key != own)
                {
                    dict.Clear();
                    continue;
                }

                Dictionary<Unit, float> factionDetected = GetFactionDetections(own);
                float now = Time.timeSinceLevelLoad;
                _staleList.Clear();
                foreach (var t in dict)
                {
                    Unit target = t.Key;
                    if (target == null || target.disabled || IsMirrorTrackStale(t.Value, target, now))
                    {
                        _staleList.Add(target);
                        continue;
                    }
                    ProcessTrackQuality(t.Value, target, factionDetected, 0.0f);
                    if (EvilLog.On(LogCat.Track)) EvilEvents.TrackTick(own, target, t.Value, now);
                }
                for (int i = 0; i < _staleList.Count; i++) dict.Remove(_staleList[i]);
            }
        }

        // The host resends every track at least each TrackSync refresh (5 s); a host track silent for longer is stale
        private const float MirrorHostSilenceSeconds = 30.0f;

        /// <summary>
        /// Mirror client: the host forgets tracks and sends the removal (ADR-33), so a host track drops here only when
        /// the host stops sending it. A track only the own sensors made keeps the local time cap.
        /// </summary>
        private static bool IsMirrorTrackStale(TriangulationTrack track, Unit target, float now)
        {
            if (track.RemoteSeenTime > 0.0f) return now - track.RemoteSeenTime > MirrorHostSilenceSeconds;
            return now - Mathf.Max(track.LastUpdateTime, track.LastActiveDetectionTime) > GetTargetMemoryDuration(target);
        }

        // Track timestamps: 0 = none. A host time older than the client level load is negative here (CnoEvilNet.ReadAge).
        private static bool IsNewerTime(float candidate, float current)
        {
            return current == 0.0f || (candidate != 0.0f && candidate >= current);
        }

        private static float NewerTime(float current, float candidate)
        {
            return IsNewerTime(candidate, current) ? candidate : current;
        }

        /// <summary>
        /// Mirror client: merges a host track into the own faction track. Q, fix error and the circle come from the host;
        /// timestamps take the newer value; positions come from the newer source (own contact or host).
        /// </summary>
        public static void ApplyRemoteTrack(Unit unit, ref RemoteTrackData data)
        {
            if (unit == null || unit.disabled) return;
            FactionHQ own = GetPlayerFactionHQ();
            if (own == null) return;

            float now = Time.timeSinceLevelLoad;
            Dictionary<Unit, TriangulationTrack> tracks = GetTrackDictionary(own);
            TriangulationTrack track;
            if (!tracks.TryGetValue(unit, out track))
            {
                track = CreateTrack(unit);
                tracks[unit] = track;
            }

            bool hostFixNewer = data.HasFixCenter && IsNewerTime(data.LastFixTime, track.LastPreciseContactTime);
            bool localPreciseNewer = track.LastPreciseContactTime != 0.0f && !IsNewerTime(data.LastPreciseContactTime, track.LastPreciseContactTime);

            track.RemoteSeenTime = now;
            track.TrackingQuality = Mathf.Clamp01(data.TrackingQuality);
            track.HadActiveContact = track.HadActiveContact || data.HadActiveContact;
            track.UncertaintyRadiusMeters = data.UncertaintyRadiusMeters;

            track.LastUpdateTime = NewerTime(track.LastUpdateTime, data.LastUpdateTime);
            track.LastFixTime = NewerTime(track.LastFixTime, data.LastFixTime);
            track.LastPreciseContactTime = NewerTime(track.LastPreciseContactTime, data.LastPreciseContactTime);
            track.LastRadarIlluminatedTime = NewerTime(track.LastRadarIlluminatedTime, data.LastRadarIlluminatedTime);
            track.VisualHoldUntil = Mathf.Max(track.VisualHoldUntil, data.VisualHoldUntil);
            track.LastActiveDetectionTime = NewerTime(track.LastActiveDetectionTime, data.LastActiveDetectionTime);
            track.IsTriangulated = data.IsTriangulated || track.HasRecentPreciseContact;
            track.PassiveLocated = data.PassiveLocated;

            if (data.SensorRangeTime != 0.0f && IsNewerTime(data.SensorRangeTime, track.SensorRangeTime))
            {
                track.SensorRangeMeters = data.SensorRangeMeters;
                track.SensorRangeTime = data.SensorRangeTime;
            }
            if (data.LastKnownTime != 0.0f && IsNewerTime(data.LastKnownTime, track.LastKnownTime))
            {
                track.LastKnownGlobalPosition = data.LastKnownGlobalPosition;
                track.LastKnownTime = data.LastKnownTime;
                track.LastKnownVelocity = data.LastKnownVelocity;
            }
            if (data.LastPreciseVelocityTime != 0.0f && IsNewerTime(data.LastPreciseVelocityTime, track.LastPreciseVelocityTime))
            {
                track.LastPreciseVelocity = data.LastPreciseVelocity;
                track.LastPreciseVelocityTime = data.LastPreciseVelocityTime;
            }

            if (track.HasRecentPreciseContact && (localPreciseNewer || !hostFixNewer))
            {
                // Live contact (own sensor or host): position known
                track.TriangulationErrorMeters = 0.0f;
                track.EstimatedCenter = GlobalPositionExtensions.GlobalPosition(unit);
            }
            else if (data.HasFixCenter)
            {
                track.TriangulationErrorMeters = data.TriangulationErrorMeters;
                track.EstimatedCenter = data.EstimatedCenter;
            }
            else
            {
                // Bearing-only track: the map draws a cone toward the emitter
                track.TriangulationErrorMeters = data.TriangulationErrorMeters;
                track.EstimatedCenter = GlobalPositionExtensions.GlobalPosition(unit);
            }
            track.TriangulatedWorldPos = unit.transform.position;
        }

        /// <summary>
        /// Mirror client: the host faction dropped the track.
        /// </summary>
        public static void RemoveRemoteTrack(Unit unit)
        {
            if (unit == null) return;
            foreach (var kvp in _factionTracks)
            {
                if (kvp.Value != null) kvp.Value.Remove(unit);
            }
        }

        /// <summary>
        /// Mirror client: drop all tracks (faction change or new host session); the host resends its tracks.
        /// </summary>
        public static void ResetRemoteTracks()
        {
            foreach (var kvp in _factionTracks)
            {
                if (kvp.Value != null) kvp.Value.Clear();
            }
            _fallbackTracks.Clear();
        }

        public static void RemoveTrack(Unit emitter)
        {
            if (emitter == null) return;
            foreach (var kvp in _factionTracks)
            {
                if (kvp.Value != null && kvp.Value.Remove(emitter) && EvilLog.On(LogCat.Track))
                {
                    EvilEvents.TrackForgotten(kvp.Key, emitter, "unit gone");
                }
            }
            _fallbackTracks.Remove(emitter);
            foreach (var kvp in _factionBearingObs)
            {
                if (kvp.Value != null) kvp.Value.Remove(emitter);
            }
        }

        public static void ClearAll()
        {
            foreach (var kvp in _factionTracks)
            {
                if (kvp.Value != null) kvp.Value.Clear();
            }
            _factionTracks.Clear();
            _fallbackTracks.Clear();
            _staleList.Clear();
            foreach (var kvp in s_factionDetections) kvp.Value.Clear();
            s_factionDetections.Clear();
            s_detectionPool.Clear();
            _factionBearingObs.Clear();
            s_loggedFixes.Clear();
            _allDictsBuffer.Clear();
            s_emptyBearingKeys.Clear();
            s_pendingTrackDt = 0.0f;
            s_nextBearingPruneTime = 0.0f;
            _lastPlayerHQ = null;
            s_mirrorFaction = null;
        }
    }
}
