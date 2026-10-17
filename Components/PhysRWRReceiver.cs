using System;
using System.Collections.Generic;
using UnityEngine;
using CnoEvil.Core;
using CnoEvil.Config;

namespace CnoEvil.Components
{
    public class RwrThreatEntry
    {
        public Unit EmitterUnit;
        public RwrThreatState State;
        public float LastPingTime;
        public Vector3 LastDirection; // Measured world direction from the last ping (includes antenna jitter)
        public float RelativeBearing; // Degrees (-180 to +180 relative to aircraft forward)
        public int ClockPosition;      // 1 to 12 o'clock
        public float SignalStrength01; // 0.0 to 1.0 normalized intensity
        public bool IsActiveSeeker;
    }

    public class PhysRWRReceiver : MonoBehaviour
    {
        public Aircraft AttachedAircraft;
        public RwrCapabilities Capabilities;

        private readonly Dictionary<Unit, RwrThreatEntry> _threats = new Dictionary<Unit, RwrThreatEntry>();
        private readonly List<Unit> _staleList = new List<Unit>();

        // Allied Tactical Datalink Tracks
        private readonly Dictionary<Unit, DatalinkThreatEntry> _datalinkThreats = new Dictionary<Unit, DatalinkThreatEntry>();
        private readonly List<Unit> _staleDatalinkList = new List<Unit>();

        private static readonly Dictionary<Aircraft, PhysRWRReceiver> s_registry = new Dictionary<Aircraft, PhysRWRReceiver>();

        public static PhysRWRReceiver Get(Aircraft aircraft)
        {
            if (aircraft == null) return null;
            PhysRWRReceiver rwr;
            if (!s_registry.TryGetValue(aircraft, out rwr))
            {
                rwr = aircraft.GetComponent<PhysRWRReceiver>();
                if (rwr != null) s_registry[aircraft] = rwr;
            }
            return rwr;
        }

        public static void ClearAll()
        {
            s_registry.Clear();
        }

        public ICollection<RwrThreatEntry> ActiveThreats
        {
            get { return _threats.Values; }
        }

        /// <summary>
        /// Half-width (degrees) of the uniform angle-of-arrival error: the receiver tier's jitter,
        /// floored by the RWRJitterDegrees config.
        /// </summary>
        public float EffectiveBearingJitterDegrees
        {
            get
            {
                float jitter = Capabilities.AzimuthJitterDegrees;
                if (CnoEvilConfig.RWRJitterDegrees != null)
                {
                    jitter = Mathf.Max(jitter, CnoEvilConfig.RWRJitterDegrees.Value);
                }
                return jitter;
            }
        }

        public ICollection<DatalinkThreatEntry> ActiveDatalinkThreats
        {
            get { return _datalinkThreats.Values; }
        }

        public bool TryGetDatalinkThreat(Unit unit, out DatalinkThreatEntry entry)
        {
            entry = null;
            return unit != null && _datalinkThreats.TryGetValue(unit, out entry) && entry != null;
        }

        private void Awake()
        {
            AttachedAircraft = GetComponent<Aircraft>();
            if (AttachedAircraft != null) s_registry[AttachedAircraft] = this;

            bool enableTiers = CnoEvilConfig.EnableRWRTierGrading == null || CnoEvilConfig.EnableRWRTierGrading.Value;
            Capabilities = enableTiers ? RWRTierDatabase.GetCapabilities(AttachedAircraft) : RWRTierDatabase.GetTier2Capabilities();
        }

        private void OnDestroy()
        {
            if (AttachedAircraft != null) s_registry.Remove(AttachedAircraft);
        }

        public void RegisterRadarPing(Unit emitter, Radar radar, RwrThreatState threatLevel, float signalStrength01 = 0.5f, bool isSeeker = false)
        {
            if (emitter == null || transform == null) return;

            Vector3 toEmitter = emitter.transform.position - transform.position;
            float distToEmitter = toEmitter.magnitude;
            if (distToEmitter < 1.0f) distToEmitter = 1.0f;
            Vector3 dirToEmitter = toEmitter / distToEmitter;

            // 1. RWR antenna blind cones (zenith & nadir), evaluated in the aircraft body frame
            // so banking and pitching rotate them with the airframe (fuselage / wing masking)
            bool checkBlindZones = CnoEvilConfig.EnableRWRBlindZones == null || CnoEvilConfig.EnableRWRBlindZones.Value;
            if (checkBlindZones)
            {
                // Convert LOS vector to aircraft body frame (+Z forward, +X right, +Y up)
                Vector3 localDir = transform.InverseTransformDirection(dirToEmitter);
                float elevDeg = Mathf.Asin(Mathf.Clamp(localDir.y, -1.0f, 1.0f)) * Mathf.Rad2Deg;

                if (elevDeg < Capabilities.ElevationMinDeg || elevDeg > Capabilities.ElevationMaxDeg)
                {
                    // Emitter is inside the zenith blind cone (diving from high above) or belly blind cone!
                    return;
                }
            }

            bool isActiveSeeker = isSeeker || emitter is Missile;

            // 2. Ku-Band Active Seeker Sensitivity Check (Tiered)
            if (isActiveSeeker && distToEmitter > Capabilities.KuBandMaxDetectRange)
            {
                // Beyond the receiver's Ku-band sensitivity (3.5 / 16 / 40 km by tier)
                return;
            }

            float now = Time.time;
            RwrThreatEntry entry;
            bool isNew = !_threats.TryGetValue(emitter, out entry);
            if (isNew)
            {
                entry = new RwrThreatEntry();
                entry.EmitterUnit = emitter;
                _threats[emitter] = entry;
            }

            // Preserve higher-priority active missile guidance state for 3 s
            bool keepMissileState = !isNew && entry.State == RwrThreatState.MissileGuidance &&
                                    threatLevel < RwrThreatState.MissileGuidance && (now - entry.LastPingTime < 3.0f);
            if (!keepMissileState)
            {
                entry.State = threatLevel;
            }

            entry.LastPingTime = now;
            entry.SignalStrength01 = Mathf.Clamp01(signalStrength01);
            entry.IsActiveSeeker = isActiveSeeker;

            // Direction calculation with realistic antenna measurement jitter by tier
            float jitterRange = EffectiveBearingJitterDegrees;
            Quaternion errorJitter = Quaternion.Euler(0.0f, UnityEngine.Random.Range(-jitterRange, jitterRange), 0.0f);
            Vector3 measuredBearing = (errorJitter * dirToEmitter).normalized;
            entry.LastDirection = measuredBearing;

            UpdateRelativeBearing(entry);

            // Feed bearing directly into ESM Triangulation Processor for cooperative multi-bearing tracking
            Aircraft hostAircraft = AttachedAircraft != null ? AttachedAircraft : GetComponent<Aircraft>();
            if (hostAircraft != null && !hostAircraft.disabled && !emitter.disabled)
            {
                if (CnoEvilConfig.EnableRWRTriangulationSystem == null || CnoEvilConfig.EnableRWRTriangulationSystem.Value)
                {
                    RWRTriangulationProcessor.UpdateTrack(emitter, hostAircraft, measuredBearing, entry.SignalStrength01);
                }
            }
        }

        public void RegisterDatalinkThreat(Unit sourceAlliedUnit, Unit threatUnit, Vector3 estPos, Vector3 estVel, bool isMissile)
        {
            if (threatUnit == null || transform == null) return;

            float now = Time.time;
            DatalinkThreatEntry dlEntry;
            if (!_datalinkThreats.TryGetValue(threatUnit, out dlEntry))
            {
                dlEntry = new DatalinkThreatEntry();
                dlEntry.ThreatUnit = threatUnit;
                _datalinkThreats[threatUnit] = dlEntry;
            }

            dlEntry.SourceAlliedUnit = sourceAlliedUnit;
            dlEntry.SourceName = (sourceAlliedUnit != null && !string.IsNullOrEmpty(sourceAlliedUnit.unitName)) ? sourceAlliedUnit.unitName : "Allied Radar";
            dlEntry.EstimatedPosition = estPos;
            dlEntry.EstimatedVelocity = estVel;
            dlEntry.IsMissile = isMissile;
            dlEntry.LastPingTime = now;

            Vector3 toThreat = estPos - transform.position;
            dlEntry.Distance = toThreat.magnitude;
            dlEntry.LastDirection = toThreat.sqrMagnitude > 1.0f ? toThreat.normalized : transform.forward;

            float angle;
            int clock;
            if (ComputeRelativeBearing(dlEntry.LastDirection, out angle, out clock))
            {
                dlEntry.RelativeBearing = angle;
                dlEntry.ClockPosition = clock;
            }
        }

        /// <summary>
        /// Mirror client: a ping the host receiver measured (state, signal and measured world direction are the host
        /// values). The faction track comes from the host (TrackSync), so this does not feed the track processor.
        /// Returns true for a new ping (later than the stored one).
        /// </summary>
        public bool ApplyRemoteThreat(Unit emitter, RwrThreatState threatLevel, float signalStrength01, bool isSeeker, Vector3 worldDir, float pingTime)
        {
            if (emitter == null) return false;
            RwrThreatEntry entry;
            bool isNew = !_threats.TryGetValue(emitter, out entry);
            if (isNew)
            {
                entry = new RwrThreatEntry();
                entry.EmitterUnit = emitter;
                _threats[emitter] = entry;
            }
            else if (pingTime <= entry.LastPingTime)
            {
                return false;
            }

            bool keepMissileState = !isNew && entry.State == RwrThreatState.MissileGuidance &&
                                    threatLevel < RwrThreatState.MissileGuidance && (pingTime - entry.LastPingTime < 3.0f);
            if (!keepMissileState) entry.State = threatLevel;

            entry.LastPingTime = pingTime;
            entry.SignalStrength01 = Mathf.Clamp01(signalStrength01);
            entry.IsActiveSeeker = isSeeker;
            entry.LastDirection = worldDir;
            UpdateRelativeBearing(entry);
            return true;
        }

        /// <summary>
        /// Mirror client: a datalink threat the host relayed to this aircraft.
        /// </summary>
        public void ApplyRemoteDatalinkThreat(Unit sourceAlliedUnit, Unit threatUnit, Vector3 estPos, Vector3 estVel, bool isMissile, float pingTime)
        {
            if (threatUnit == null) return;
            DatalinkThreatEntry dlEntry;
            if (_datalinkThreats.TryGetValue(threatUnit, out dlEntry) && dlEntry != null && pingTime <= dlEntry.LastPingTime) return;
            RegisterDatalinkThreat(sourceAlliedUnit, threatUnit, estPos, estVel, isMissile);
            if (_datalinkThreats.TryGetValue(threatUnit, out dlEntry) && dlEntry != null) dlEntry.LastPingTime = pingTime;
        }

        /// <summary>
        /// Horizontal bearing of a world direction relative to the aircraft nose (-180..180 deg) and its clock position (1-12).
        /// </summary>
        private bool ComputeRelativeBearing(Vector3 worldDir, out float angle, out int clock)
        {
            angle = 0.0f;
            clock = 12;

            Vector3 fwd = transform.forward;
            fwd.y = 0.0f;
            worldDir.y = 0.0f;
            if (fwd.sqrMagnitude <= 0.001f || worldDir.sqrMagnitude <= 0.001f) return false;

            angle = Vector3.SignedAngle(fwd, worldDir, Vector3.up);
            float clockAngle = angle < 0.0f ? angle + 360.0f : angle;
            clock = Mathf.RoundToInt(clockAngle / 30.0f);
            if (clock <= 0 || clock > 12) clock = 12;
            return true;
        }

        /// <summary>
        /// Refreshes the bearing relative to the nose as the ownship turns. The world direction stays the
        /// measured one from the last ping: the receiver only learns a new bearing when the emitter pings again.
        /// </summary>
        private void UpdateRelativeBearing(RwrThreatEntry entry)
        {
            if (entry == null || transform == null) return;

            float angle;
            int clock;
            if (ComputeRelativeBearing(entry.LastDirection, out angle, out clock))
            {
                entry.RelativeBearing = angle;
                entry.ClockPosition = clock;
            }
        }

        // Receivers of other aircraft feed only the faction tracks: their stale entries are pruned at this
        // interval, and their bearings relative to the nose (display only) are not refreshed
        private const float RemotePruneIntervalSeconds = 0.5f;
        private float _nextPruneTime;

        private void Update()
        {
            if (_threats.Count == 0 && _datalinkThreats.Count == 0) return;

            float now = Time.time;
            bool displayed = CombatHUD.i != null && AttachedAircraft != null && CombatHUD.i.aircraft == AttachedAircraft;
            if (!displayed)
            {
                if (now < _nextPruneTime) return;
                _nextPruneTime = now + RemotePruneIntervalSeconds;
            }

            float maxStale = CnoEvilConfig.RWRStrobePersistenceSeconds != null ? CnoEvilConfig.RWRStrobePersistenceSeconds.Value : 3.0f;
            _staleList.Clear();

            // Continually update relative bearings as our aircraft turns and maneuvers
            foreach (var kvp in _threats)
            {
                RwrThreatEntry entry = kvp.Value;
                if (now - entry.LastPingTime > maxStale)
                {
                    _staleList.Add(kvp.Key);
                }
                else if (displayed)
                {
                    UpdateRelativeBearing(entry);
                }
            }

            for (int i = 0; i < _staleList.Count; i++)
            {
                _threats.Remove(_staleList[i]);
            }

            // Clean up stale Datalink threats (> 3.5s)
            _staleDatalinkList.Clear();
            foreach (var kvp in _datalinkThreats)
            {
                DatalinkThreatEntry dl = kvp.Value;
                if (now - dl.LastPingTime > 3.5f || dl.ThreatUnit == null || dl.ThreatUnit.disabled)
                {
                    _staleDatalinkList.Add(kvp.Key);
                }
                else if (displayed)
                {
                    // Refresh relative bearing as aircraft turns
                    float angle;
                    int clock;
                    if (ComputeRelativeBearing(dl.EstimatedPosition - transform.position, out angle, out clock))
                    {
                        dl.RelativeBearing = angle;
                        dl.ClockPosition = clock;
                    }
                }
            }

            for (int i = 0; i < _staleDatalinkList.Count; i++)
            {
                _datalinkThreats.Remove(_staleDatalinkList[i]);
            }
        }

        public RwrThreatState GetHighestThreatState()
        {
            RwrThreatState highest = RwrThreatState.None;
            foreach (var kvp in _threats)
            {
                if (kvp.Value.State > highest)
                {
                    highest = kvp.Value.State;
                }
            }
            return highest;
        }

        public DatalinkThreatEntry GetHighestDatalinkThreat()
        {
            DatalinkThreatEntry closestMissile = null;
            float minDist = float.MaxValue;

            foreach (var kvp in _datalinkThreats)
            {
                DatalinkThreatEntry e = kvp.Value;
                if (e.IsMissile && e.Distance < minDist)
                {
                    minDist = e.Distance;
                    closestMissile = e;
                }
            }

            if (closestMissile != null) return closestMissile;

            foreach (var kvp in _datalinkThreats)
            {
                return kvp.Value;
            }

            return null;
        }
    }
}
