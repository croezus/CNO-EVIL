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
        public Vector3 EstimatedCenterWorldPos;    // Center of the map ambiguity circle (true position, or the passive bearing fix)
        public float TriangulationErrorMeters;     // 1-sigma error of the passive bearing fix (0 for radar / visual contact)
        public float LastFixTime;                  // Timestamp of the last accepted bearing fix
        public float LastPreciseContactTime;       // Timestamp of the last radar / visual contact
        public float UncertaintyRadiusMeters;      // Radius of ambiguity circle in meters
        public float SensorRangeMeters;            // Range from the closest recent sensor to the target (0 = unknown); drives weapon CEP
        public float SensorRangeTime;              // Timestamp of SensorRangeMeters

        // Tracking Quality & Radar Illumination
        public float TrackingQuality;             // 0.0 (lost/untracked) to 1.0 (refined pinpoint lock)
        public float LastRadarIlluminatedTime;    // Timestamp of last active radar detection/sweep

        // Discrete tick and cooldown tracking
        public float LastRwrTickTime;
        public float LastVisualTickTime;
        public float VisualHoldUntil;             // A visual contact counts as held until this time (covers the spotter's check interval)
        public float LastRadarTickTime;

        // Lost Contact Memory State (60 s aircraft / 180 s ground, see GetTargetMemoryDuration)
        public bool HadActiveContact;             // Has this target ever been actively identified/detected by radar/optics/datalink?
        public float LastActiveDetectionTime;     // Timestamp of last active positive radar/visual/datalink detection
        public GlobalPosition LastKnownGlobalPosition; // Last position the faction knew: precise contact or passive fix center
        public float LastKnownTime;                    // Timestamp of LastKnownGlobalPosition (0 = never located)
        public Vector3 LastKnownVelocity;         // Velocity at last contact (drives memory CEP drift)
        public Vector3 LastPreciseVelocity;       // Velocity measured at the last radar / visual contact (drives AI lead)
        public float LastPreciseVelocityTime;     // Timestamp of LastPreciseVelocity (0 = never measured)

        /// <summary>
        /// Radar or visual contact within the vanilla FactionHQ "spotted" window (4 s).
        /// </summary>
        public bool HasRecentPreciseContact
        {
            get { return LastPreciseContactTime > 0.0f && Time.timeSinceLevelLoad - LastPreciseContactTime < RWRTriangulationProcessor.PreciseContactWindow; }
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
                // Active if positively observed / illuminated within ActiveContactWindowSeconds
                return (Time.timeSinceLevelLoad - LastActiveDetectionTime < CnoEvilConfig.ActiveContactWindowSeconds.Value);
            }
        }

        /// <summary>
        /// In memory with the target's own retention window (60 s aircraft / 180 s ground, see GetTargetMemoryDuration).
        /// </summary>
        public bool IsInMemoryState()
        {
            return IsInMemoryState(RWRTriangulationProcessor.GetTargetMemoryDuration(EmitterUnit));
        }

        public bool IsInMemoryState(float memoryDurationSeconds)
        {
            if (!HadActiveContact) return false;
            if (IsActivelyDetected) return false;
            float elapsed = Time.timeSinceLevelLoad - LastActiveDetectionTime;
            return elapsed >= 0f && elapsed <= memoryDurationSeconds;
        }

        public bool IsExpired(float memoryDurationSeconds = 120.0f)
        {
            if (!HadActiveContact)
            {
                return Time.timeSinceLevelLoad - LastUpdateTime > memoryDurationSeconds;
            }
            return (Time.timeSinceLevelLoad - LastActiveDetectionTime) > memoryDurationSeconds;
        }

        // Single-ship synthetic baseline history
        public Vector3 FirstObservedOwnshipPos;
        public Vector3 FirstObservedBearing;
        public float FirstObservedTime;
        public bool HasInitialObservation;

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
            public Vector3 Position;
            public Vector3 Bearing;
            public float SigmaRad;
            public bool SharesBearings;
            public float Timestamp;
        }

        private static readonly Dictionary<FactionHQ, Dictionary<Unit, List<AlliedBearingObservation>>> _factionBearingObs =
            new Dictionary<FactionHQ, Dictionary<Unit, List<AlliedBearingObservation>>>();

        /// <summary>
        /// Creates a new track for an emitter, optionally seeding the single-ship kinematic baseline observation.
        /// </summary>
        private static TriangulationTrack CreateTrack(Unit emitter, bool seedObservation, Vector3 observerPos, Vector3 bearing, float now)
        {
            TriangulationTrack track = new TriangulationTrack();
            track.EmitterUnit = emitter;
            track.TrackingQuality = 0.0f; // Accumulates via sensor ticks
            if (seedObservation)
            {
                track.FirstObservedOwnshipPos = observerPos;
                track.FirstObservedBearing = bearing;
                track.FirstObservedTime = now;
                track.HasInitialObservation = true;
            }
            return track;
        }

        public static float GetTargetMemoryDuration(Unit target)
        {
            if (ThreatClassification.IsStrategicBuilding(target, null))
            {
                return (CnoEvilConfig.StrategicBuildingMemorySeconds != null)
                    ? CnoEvilConfig.StrategicBuildingMemorySeconds.Value
                    : 300.0f;
            }
            if (target is Aircraft)
            {
                return (CnoEvilConfig.AircraftMemoryDurationSeconds != null)
                    ? CnoEvilConfig.AircraftMemoryDurationSeconds.Value
                    : 60.0f;
            }
            return (CnoEvilConfig.GroundTargetMemoryDurationSeconds != null)
                ? CnoEvilConfig.GroundTargetMemoryDurationSeconds.Value
                : 180.0f;
        }

        // A bearing fix counts as triangulated when its 1-sigma position error is within this fraction of range
        private static float FixRelativeErrorThreshold
        {
            get { return Mathf.Max(0.0f, CnoEvilConfig.BearingFixMaxErrorFraction.Value); }
        }

        private static float BearingObservationMaxAge
        {
            get { return Mathf.Max(0.1f, CnoEvilConfig.BearingObservationMaxAgeSeconds.Value); }
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
                Position = observerPos,
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
        /// </summary>
        public static bool TryGetMultiStationFix(FactionHQ hq, Unit emitter, float now, out Vector3 fix, out float sigmaPos, out float meanRange)
        {
            fix = Vector3.zero;
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
                    if (TryIntersectBearings(a.Position, a.Bearing, a.SigmaRad, a.Timestamp,
                                             b.Position, b.Bearing, b.SigmaRad, b.Timestamp,
                                             emitterSpeed, out f, out sig, out rng) && sig < sigmaPos)
                    {
                        fix = f;
                        sigmaPos = sig;
                        meanRange = rng;
                        bestA = a.Observer;
                        bestB = b.Observer;
                        found = true;
                    }
                }
            }

            if (found && CnoEvilConfig.DebugLogging != null && CnoEvilConfig.DebugLogging.Value && CnoEvilPlugin.Log != null)
            {
                CnoEvilPlugin.Log.LogInfo(string.Format("[CNO-EVIL ESM] Datalink cross-bearing fix on {0}: obs1={1}, obs2={2}, sigma={3:F0}m at {4:F0}m",
                    emitter.unitName, bestA != null ? bestA.unitName : "null", bestB != null ? bestB.unitName : "null", sigmaPos, meanRange));
            }
            return found;
        }

        /// <summary>
        /// Applies a passive bearing fix to a track. The fix triangulates the track when its error is within
        /// FixRelativeErrorThreshold of range; the map uncertainty circle is then centered on the estimate.
        /// Returns true when the fix was accepted.
        /// </summary>
        private static bool ApplyBearingFix(FactionHQ hq, TriangulationTrack track, Unit emitter, Vector3 fix, float sigmaPos, float meanRange, bool isMultiStation, float now)
        {
            if (sigmaPos > FixRelativeErrorThreshold * meanRange) return false;

            // A precise sensor contact (radar / visual) is never degraded by a passive fix
            if (track.IsTriangulated && track.TriangulationErrorMeters <= 0.0f && now - track.LastPreciseContactTime < BearingObservationMaxAge)
            {
                return true;
            }

            if (!track.IsTriangulated)
            {
                TrackUncertaintyCalculator.RecordTriangulationBoost(track, isMultiStation);
            }
            track.IsTriangulated = true;

            fix.y = emitter.transform.position.y; // Passive ESM gives no usable altitude; the map is 2D
            bool freshFix = now - track.LastFixTime < BearingObservationMaxAge;
            track.EstimatedCenterWorldPos = freshFix ? Vector3.Lerp(track.EstimatedCenterWorldPos, fix, 0.3f) : fix;
            track.TriangulationErrorMeters = (freshFix && track.TriangulationErrorMeters > 0.0f)
                ? Mathf.Lerp(track.TriangulationErrorMeters, sigmaPos, 0.3f)
                : sigmaPos;
            track.LastFixTime = now;
            TrackUncertaintyCalculator.RecordSensorRange(track, meanRange);
            track.LastKnownGlobalPosition = GlobalPositionExtensions.ToGlobalPosition(track.EstimatedCenterWorldPos);
            track.LastKnownTime = now;
            return true;
        }

        /// <summary>
        /// Marks a precise (radar or visual) contact: position known, no passive fix error.
        /// </summary>
        private static void MarkPreciseContact(TriangulationTrack track, Unit emitter, float now)
        {
            track.TriangulationErrorMeters = 0.0f;
            track.EstimatedCenterWorldPos = emitter.transform.position;
            track.LastPreciseContactTime = now;
            track.LastKnownGlobalPosition = GlobalPositionExtensions.GlobalPosition(emitter);
            track.LastKnownTime = now;
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
            if (track.LastPreciseVelocityTime > 0.0f &&
                Time.timeSinceLevelLoad - track.LastPreciseVelocityTime <= GetTargetMemoryDuration(target))
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

            if (track.IsTriangulated && track.TriangulationErrorMeters > 0.0f && track.LastFixTime > 0.0f &&
                now - track.LastFixTime < BearingObservationMaxAge)
            {
                position = GlobalPositionExtensions.ToGlobalPosition(track.EstimatedCenterWorldPos);
                errorMeters = track.TriangulationErrorMeters;
                return true;
            }

            if (track.LastKnownTime > 0.0f && (track.IsActivelyDetected || track.IsInMemoryState()))
            {
                position = track.LastKnownGlobalPosition;
                float driftSpeed = TrackUncertaintyCalculator.GetMemoryDriftSpeed(target, track.LastKnownVelocity.magnitude);
                errorMeters = driftSpeed * (now - track.LastKnownTime);
                return true;
            }
            return false;
        }

        public static FactionHQ GetPlayerFactionHQ()
        {
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
                catch { }
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

        public static TriangulationTrack GetTrack(FactionHQ faction, Unit emitter)
        {
            if (emitter == null) return null;
            Dictionary<Unit, TriangulationTrack> tracks = GetTrackDictionary(faction);
            TriangulationTrack track;
            if (tracks.TryGetValue(emitter, out track))
            {
                return track;
            }

            // If unit is currently tracked by the player's own radar, auto-register it for the player's faction only
            if (CombatHUD.i != null && CombatHUD.i.aircraft != null && faction == GetPlayerFactionHQ())
            {
                Aircraft player = CombatHUD.i.aircraft;
                Radar playerRadar = CombatHUDPatches.GetCachedPlayerRadar(player);
                if (playerRadar != null && playerRadar.IsOperational())
                {
                    if (playerRadar.CheckIsTarget(emitter) || (playerRadar.detectedTargets != null && playerRadar.detectedTargets.Contains(emitter)))
                    {
                        RecordRadarIllumination(faction, emitter, playerRadar);
                        tracks.TryGetValue(emitter, out track);
                        return track;
                    }
                }
            }

            return null;
        }

        public static TriangulationTrack GetTrack(Unit emitter)
        {
            return GetTrack(GetPlayerFactionHQ(), emitter);
        }

        public static bool HasTrack(FactionHQ faction, Unit emitter)
        {
            if (emitter == null) return false;
            Dictionary<Unit, TriangulationTrack> tracks = GetTrackDictionary(faction);
            return tracks != null && tracks.ContainsKey(emitter);
        }

        public static bool HasTrack(Unit emitter)
        {
            return HasTrack(GetPlayerFactionHQ(), emitter);
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
                Vector3 obsPos = (observer != null) ? observer.transform.position : target.transform.position;
                Vector3 obsBearing = (observer != null) ? (target.transform.position - obsPos).normalized : target.transform.forward;
                track = CreateTrack(target, true, obsPos, obsBearing, now);
                tracks[target] = track;
            }

            float spotterDist = (observer != null) ? Vector3.Distance(observer.transform.position, target.transform.position) : 0.0f;
            if (strategicBuilding)
            {
                TrackUncertaintyCalculator.RecordVisualContact(track, CnoEvilConfig.StrategicBuildingVisualQuality.Value, spotterDist);
            }
            else
            {
                TrackUncertaintyCalculator.RecordVisualContact(track, spotterDist);
            }
            track.VisualHoldUntil = Mathf.Max(track.VisualHoldUntil, now + Mathf.Max(1.0f, holdSeconds));
            MarkPreciseContact(track, target, now);
            RefreshMotionState(track, target);
            track.LastUpdateTime = now;
        }

        /// <summary>
        /// Registers or refreshes active radar illumination on a target from an allied radar.
        /// Refines tracking quality cumulatively over consecutive radar sweeps.
        /// Heavy ground/naval radar stations add a range-weighted bonus on each sweep tick
        /// to compensate for their slow mechanical rotation.
        /// </summary>
        public static void RecordRadarIllumination(FactionHQ faction, Unit target, Radar sourceRadar)
        {
            if (target == null || target.disabled || target is Missile || sourceRadar == null) return;
            Unit sourceUnit = sourceRadar.GetAttachedUnit();
            if (faction == null) faction = (sourceUnit != null) ? sourceUnit.NetworkHQ : GetPlayerFactionHQ();
            if (faction == null) return;

            // Only tracked threat types (ThreatClassification)
            if (!ThreatClassification.IsTrackedThreat(target, faction)) return;

            float now = Time.timeSinceLevelLoad;
            Dictionary<Unit, TriangulationTrack> tracks = GetTrackDictionary(faction);
            TriangulationTrack track;
            if (!tracks.TryGetValue(target, out track))
            {
                Vector3 srcPos = (sourceUnit != null) ? sourceUnit.transform.position : target.transform.position;
                track = CreateTrack(target, true, srcPos, (target.transform.position - srcPos).normalized, now);
                track.LastRadarIlluminatedTime = now;
                tracks[target] = track;
            }

            // Team-wide intelligence accumulation via radar sweep
            float sweepDist = (sourceUnit != null) ? Vector3.Distance(sourceUnit.transform.position, target.transform.position) : 30000f;
            bool isOwnship = (CombatHUD.i != null && CombatHUD.i.aircraft != null && sourceUnit == CombatHUD.i.aircraft);
            bool ticked = TrackUncertaintyCalculator.RecordRadarSweep(track, sweepDist, isOwnship);

            bool isHeavyStation = (sourceRadar.RadarParameters.maxRange > 50000f) ||
                                  (sourceUnit != null && sourceUnit.definition != null && sourceUnit.definition.code == "RDR");
            if (ticked && isHeavyStation)
            {
                track.TrackingQuality = Mathf.Clamp01(track.TrackingQuality + Mathf.Max(0.0f, CnoEvilConfig.HeavyStationSweepBonus.Value) * TrackUncertaintyCalculator.CalculateRangeWeight(sweepDist));
            }

            MarkPreciseContact(track, target, now);
            RefreshMotionState(track, target);
            track.LastUpdateTime = now;
        }

        public static void RecordRadarIllumination(Unit target, Radar sourceRadar)
        {
            Unit sourceUnit = (sourceRadar != null) ? sourceRadar.GetAttachedUnit() : null;
            FactionHQ hq = (sourceUnit != null && sourceUnit.NetworkHQ != null) ? sourceUnit.NetworkHQ : GetPlayerFactionHQ();
            RecordRadarIllumination(hq, target, sourceRadar);
        }

        public static void UpdateTrack(Unit emitter, Aircraft playerAircraft, Vector3 worldBearing, float signalStrength01)
        {
            if (emitter == null || playerAircraft == null || emitter.disabled || playerAircraft.disabled)
            {
                return;
            }

            FactionHQ hq = playerAircraft.NetworkHQ != null ? playerAircraft.NetworkHQ : GetPlayerFactionHQ();
            if (hq != null && emitter.NetworkHQ != null && emitter.NetworkHQ == hq)
            {
                return; // Friendly radar emission, never treat as threat
            }
            Dictionary<Unit, TriangulationTrack> tracks = GetTrackDictionary(hq);

            float now = Time.timeSinceLevelLoad;
            bool isLocalPlayer = (CombatHUD.i != null && CombatHUD.i.aircraft != null && playerAircraft == CombatHUD.i.aircraft);
            TriangulationTrack track;
            if (!tracks.TryGetValue(emitter, out track))
            {
                track = CreateTrack(emitter, isLocalPlayer, playerAircraft.transform.position, worldBearing, now);
                tracks[emitter] = track;
            }

            if (isLocalPlayer && !track.HasInitialObservation)
            {
                track.FirstObservedOwnshipPos = playerAircraft.transform.position;
                track.FirstObservedBearing = worldBearing;
                track.FirstObservedTime = now;
                track.HasInitialObservation = true;
            }

            track.LastUpdateTime = now;
            track.HadActiveContact = true;
            track.LastActiveDetectionTime = now;
            RefreshMotionState(track, emitter); // A bearing alone does not locate the emitter

            // Compute hardware tier
            PhysRWRReceiver rwr = PhysRWRReceiver.Get(playerAircraft);
            RwrTier tier = rwr != null ? rwr.Capabilities.Tier : RwrTier.Tier2_Standard;

            Vector3 playerPos = playerAircraft.transform.position;
            Vector3 emitterPos = emitter.transform.position;
            float trueDist = Vector3.Distance(playerPos, emitterPos);

            // Record RWR detection tick with distance coefficient and receiver tier weighting
            TrackUncertaintyCalculator.RecordRWRDetection(track, trueDist, tier, true);

            // Receiver bearing error: uniform +/-J degrees -> 1-sigma J/sqrt(3)
            float jitterDeg = (rwr != null) ? rwr.EffectiveBearingJitterDegrees : RWRTierDatabase.GetTier2Capabilities().AzimuthJitterDegrees;
            float sigmaRad = jitterDeg * Mathf.Deg2Rad / Mathf.Sqrt(3.0f);
            bool sharesBearings = (rwr != null) && rwr.Capabilities.Datalink == DatalinkTier.Advanced;

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
            bool enableDatalink = CnoEvilConfig.EnableAlliedDatalink == null || CnoEvilConfig.EnableAlliedDatalink.Value;
            bool hasFix = false;
            if (enableDatalink)
            {
                RecordAlliedBearing(hq, emitter, playerAircraft, playerPos, worldBearing, sigmaRad, sharesBearings, now);

                Vector3 fix;
                float sigmaPos, meanRange;
                if (!isPrecise && TryGetMultiStationFix(hq, emitter, now, out fix, out sigmaPos, out meanRange))
                {
                    hasFix = ApplyBearingFix(hq, track, emitter, fix, sigmaPos, meanRange, true, now);
                }
            }

            // 4. Single-ship kinematic fix: first and current ownship bearings (cross-bearing flight maneuver).
            // Emitter motion between the two observations limits passive ranging on fast movers.
            if (!isPrecise && !hasFix && isLocalPlayer && track.HasInitialObservation)
            {
                float timeSinceFirst = now - track.FirstObservedTime;
                if (timeSinceFirst > 3.0f)
                {
                    Vector3 fix;
                    float sigmaPos, meanRange;
                    float emitterSpeed = (emitter.rb != null) ? emitter.rb.velocity.magnitude : 0.0f;
                    bool accepted = TryIntersectBearings(
                            track.FirstObservedOwnshipPos, track.FirstObservedBearing, sigmaRad, track.FirstObservedTime,
                            playerPos, worldBearing, sigmaRad, now,
                            emitterSpeed, out fix, out sigmaPos, out meanRange) &&
                        ApplyBearingFix(hq, track, emitter, fix, sigmaPos, meanRange, false, now);

                    if (!accepted && timeSinceFirst > 30.0f)
                    {
                        track.FirstObservedOwnshipPos = playerPos;
                        track.FirstObservedBearing = worldBearing;
                        track.FirstObservedTime = now;
                    }
                }
            }

            if (!track.IsTriangulated)
            {
                // Untriangulated: the map shows a bearing cone toward the emitter
                track.EstimatedCenterWorldPos = emitterPos;
            }

            // True position for debug overlays; weapon dispersion and the fix error model the miss distance
            track.TriangulatedWorldPos = emitterPos;

            UpdateUncertaintyRadius(track, emitter, trueDist, signalStrength01);
        }

        /// <summary>
        /// Map/HUD uncertainty circle from team-wide intelligence, distance and triangulation state.
        /// </summary>
        private static void UpdateUncertaintyRadius(TriangulationTrack track, Unit emitter, float observerDist, float signalStrength01)
        {
            float memDuration = GetTargetMemoryDuration(emitter);
            float memElapsed = (track.LastActiveDetectionTime > 0f) ? (Time.timeSinceLevelLoad - track.LastActiveDetectionTime) : 0f;
            float rawCep = TrackUncertaintyCalculator.CalculateDispersionRadius(
                observerDist, track.TrackingQuality, track.IsInMemoryState(memDuration), memElapsed, track.IsTriangulated,
                TrackUncertaintyCalculator.GetMemoryDriftSpeed(emitter, track.LastKnownVelocity.magnitude), track.TriangulationErrorMeters);

            float sigFactor = 1.0f + (1.0f - Mathf.Clamp01(signalStrength01)) * 0.5f;
            float circleScale = CnoEvilConfig.RWRUncertaintyCircleScale != null
                ? Mathf.Clamp(CnoEvilConfig.RWRUncertaintyCircleScale.Value, 0.2f, 3.0f)
                : 1.0f;
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
        /// Visual identification range of strategic buildings (StrategicBuildingVisualRangeMeters). All other units are
        /// identified by the vanilla optics of the spotter (TargetDetector_DetectTarget_Patch).
        /// </summary>
        public static float StrategicBuildingVisualRange
        {
            get
            {
                return (CnoEvilConfig.StrategicBuildingVisualRangeMeters != null)
                    ? Mathf.Max(CnoEvilConfig.StrategicBuildingVisualRangeMeters.Value, 100.0f)
                    : 20000.0f;
            }
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
        /// Updates tracking quality from radar dwell (dt > 0 only) or decays it after contact loss,
        /// and maintains the target memory state for lost contacts.
        /// While active contact (visual, radar, or RWR pings) is maintained, Q does not decay,
        /// and the 180s (ground) or 60s (air) memory countdown is continuously reset.
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
            // within the last sweep interval, or pinged on RWR within ContactHoldSeconds
            float hold = CnoEvilConfig.ContactHoldSeconds.Value;
            bool hasRadarLock = isDirectlyIlluminated || (now - track.LastRadarIlluminatedTime < hold);
            bool hasRecentRwrPing = (now - track.LastUpdateTime < hold);

            if (hasVisual || hasRadarLock || hasRecentRwrPing)
            {
                // Refresh memory timer so the 180s/60s countdown is continuously reset
                track.HadActiveContact = true;
                track.LastActiveDetectionTime = now;
                RefreshMotionState(track, emitter);

                if (isDirectlyIlluminated && dt > 0.0f)
                {
                    // Continuous active radar dwell: refine accuracy towards 1.0 with distance weighting
                    TrackUncertaintyCalculator.RecordRadarDwell(track, dt, radarDist);
                }
                // No decay while active contact (including passive RWR pings) is present
            }
            else if (dt > 0.0f && track.TrackingQuality > 0.0f && !(emitter is Building))
            {
                // Buildings do not move: their Q holds until the memory window expires the track
                // CONTACT IS LOST: linear Q decay over the target's memory window (180s ground, 60s air).
                // Coordinates stay frozen at the last known position.
                float memDuration = GetTargetMemoryDuration(emitter);
                float decayRate = (memDuration > 0.0f) ? (1.0f / memDuration) : (1.0f / 120.0f);
                track.TrackingQuality = Mathf.Max(0.0f, track.TrackingQuality - decayRate * dt);
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

        private static void AddRadarDetections(Radar radar, bool datalinkEnabled, Aircraft player, FactionHQ playerHq)
        {
            if (radar == null || !radar.IsOperational() || radar.detectedTargets == null || radar.detectedTargets.Count == 0) return;
            Unit radarUnit = DatalinkNetwork.GetRadarAttachedUnit(radar);
            if (radarUnit == null || radarUnit.disabled) return;
            FactionHQ hq = DatalinkNetwork.GetUnitFactionHQ(radarUnit);
            if (hq == null) return;

            // Without the datalink the player's faction only knows what the ownship radar sees
            if (!datalinkEnabled && hq == playerHq && radarUnit != player) return;

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
            }
        }

        private static void RebuildFactionDetections()
        {
            foreach (var kvp in s_factionDetections)
            {
                kvp.Value.Clear();
                s_detectionPool.Add(kvp.Value);
            }
            s_factionDetections.Clear();

            Aircraft player = (CombatHUD.i != null) ? CombatHUD.i.aircraft : null;
            FactionHQ playerHq = (player != null && player.NetworkHQ != null) ? player.NetworkHQ : GetPlayerFactionHQ();
            bool datalinkEnabled = CnoEvilConfig.EnableAlliedDatalink == null || CnoEvilConfig.EnableAlliedDatalink.Value;

            List<Radar> registered = DatalinkNetwork.RegisteredRadars;
            for (int i = 0; i < registered.Count; i++)
            {
                AddRadarDetections(registered[i], datalinkEnabled, player, playerHq);
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
                                AddRadarDetections(hqRadars[i], datalinkEnabled, player, playerHq);
                            }
                        }
                    }
                }
            }
            catch { }
        }

        private static readonly List<KeyValuePair<FactionHQ, Dictionary<Unit, TriangulationTrack>>> _allDictsBuffer =
            new List<KeyValuePair<FactionHQ, Dictionary<Unit, TriangulationTrack>>>();

        /// <summary>
        /// Frame-by-frame update called from CnoEvilPlugin.Update().
        /// Updates every faction's tracks from that faction's own sensors (radars, datalink bearings, visual
        /// contacts), decays lost contacts and prunes dead or expired tracks.
        /// </summary>
        public static void UpdateAllTracks(float dt)
        {
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

            bool enableDatalink = CnoEvilConfig.EnableAlliedDatalink == null || CnoEvilConfig.EnableAlliedDatalink.Value;
            float now = Time.timeSinceLevelLoad;

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

                    if (target == null || target.disabled || track.IsExpired(GetTargetMemoryDuration(target)))
                    {
                        // The faction forgets the unit: the vanilla entry follows once it is also old (ADR-15)
                        if (target != null && !target.disabled) VanillaKnowledgeExpiry.MarkExpired(hq, target);
                        _staleList.Add(target);
                        continue;
                    }

                    Vector3 fix;
                    float sigmaPos, meanRange;
                    if (enableDatalink && TryGetMultiStationFix(hq, target, now, out fix, out sigmaPos, out meanRange))
                    {
                        ApplyBearingFix(hq, track, target, fix, sigmaPos, meanRange, true, now);
                    }

                    ProcessTrackQuality(track, target, factionDetected, dt);
                }

                for (int i = 0; i < _staleList.Count; i++)
                {
                    dict.Remove(_staleList[i]);
                }
            }

            VanillaKnowledgeExpiry.Process(now);
        }

        public static void RemoveTrack(Unit emitter)
        {
            if (emitter == null) return;
            foreach (var kvp in _factionTracks)
            {
                if (kvp.Value != null) kvp.Value.Remove(emitter);
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
            _allDictsBuffer.Clear();
            _lastPlayerHQ = null;
        }
    }
}
