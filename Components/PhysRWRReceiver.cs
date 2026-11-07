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
        public float SignalStrength01; // 0.0 to 1.0 normalized intensity
        public bool IsActiveSeeker;
    }

    public class PhysRWRReceiver : MonoBehaviour
    {
        public Aircraft AttachedAircraft;
        public RwrCapabilities Capabilities;

        private readonly Dictionary<Unit, RwrThreatEntry> _threats = new Dictionary<Unit, RwrThreatEntry>();
        private readonly List<Unit> _staleList = new List<Unit>();

        // Illuminations below the emitter's detection threshold (EM scan ring only)
        private readonly Dictionary<Unit, RwrThreatEntry> _belowThreshold = new Dictionary<Unit, RwrThreatEntry>();
        private const float BelowThresholdStaleSeconds = 1.0f;

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

        public Dictionary<Unit, RwrThreatEntry>.ValueCollection ActiveThreats
        {
            get { return _threats.Values; }
        }

        public Dictionary<Unit, RwrThreatEntry>.ValueCollection BelowThresholdPings
        {
            get { return _belowThreshold.Values; }
        }

        /// <summary>
        /// Half-width (degrees) of the uniform angle-of-arrival error: the receiver tier's jitter.
        /// </summary>
        public float EffectiveBearingJitterDegrees
        {
            get
            {
                float tier = Capabilities.AzimuthJitterDegrees;
                return IsTier4 ? Mathf.Min(tier, Mathf.Max(0.0f, CnoEvilConfig.Tier4_AzimuthJitter_deg.Value)) : tier;
            }
        }

        // Seconds between loadout scans for a radome (the loadout can change on rearm)
        private const float Tier4ScanInterval = 5.0f;
        private bool _isTier4;
        private float _nextTier4Scan;

        /// <summary>
        /// Tier 4 (ELINT, ADR-43, ADR-51), on top of the base tier 1..3: the aircraft carries a radar mount
        /// (MunitionCatalog.HasRadarMount, the radome). A jamming pod alone does not give it. Better bearings, sensitivity
        /// and an elevation measurement; shares bearings like an Advanced datalink.
        /// </summary>
        public bool IsTier4
        {
            get
            {
                float now = Time.time;
                if (now >= _nextTier4Scan)
                {
                    _nextTier4Scan = now + Tier4ScanInterval;
                    _isTier4 = AttachedAircraft != null && MunitionCatalog.HasRadarMount(AttachedAircraft);
                }
                return _isTier4;
            }
        }

        /// <summary>
        /// The receiver contributes raw bearings to multi-station fixes: Advanced datalink or Tier 4.
        /// </summary>
        public bool SharesBearings
        {
            get { return Capabilities.Datalink == DatalinkTier.Advanced || IsTier4; }
        }

        /// <summary>
        /// 1-sigma elevation error (rad) of Tier 4 (uniform +/-J -> J / sqrt 3).
        /// </summary>
        public float Tier4ElevationSigmaRad
        {
            get { return Mathf.Max(0.01f, CnoEvilConfig.Tier4_ElevationJitter_deg.Value) * Mathf.Deg2Rad / Mathf.Sqrt(3.0f); }
        }

        /// <summary>
        /// Measured elevation (rad, up positive) of a true world direction with the Tier 4 elevation error.
        /// </summary>
        public float MeasureElevation(Vector3 trueDirection)
        {
            float el = Mathf.Asin(Mathf.Clamp(trueDirection.normalized.y, -1.0f, 1.0f));
            float j = Mathf.Max(0.0f, CnoEvilConfig.Tier4_ElevationJitter_deg.Value) * Mathf.Deg2Rad;
            return el + UnityEngine.Random.Range(-j, j);
        }

        public Dictionary<Unit, DatalinkThreatEntry>.ValueCollection ActiveDatalinkThreats
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

            bool enableTiers = CnoEvilConfig.EnableRWRTierGrading.Value;
            Capabilities = enableTiers ? RWRTierDatabase.GetCapabilities(AttachedAircraft) : RWRTierDatabase.GetTier2Capabilities();
        }

        private void OnDestroy()
        {
            if ((object)AttachedAircraft != null) s_registry.Remove(AttachedAircraft);
        }

        /// <summary>
        /// Antenna coverage of the receiver: false when the emitter is in a blind cone or an active seeker is
        /// beyond the Ku-band range. dirToEmitter is the true world direction to the emitter.
        /// </summary>
        private bool CanHear(Unit emitter, bool isActiveSeeker, out Vector3 dirToEmitter)
        {
            Vector3 toEmitter = emitter.transform.position - transform.position;
            float distToEmitter = toEmitter.magnitude;
            if (distToEmitter < 1.0f) distToEmitter = 1.0f;
            dirToEmitter = toEmitter / distToEmitter;

            // 1. RWR antenna blind cones (zenith & nadir), evaluated in the aircraft body frame
            // so banking and pitching rotate them with the airframe (fuselage / wing masking)
            bool checkBlindZones = CnoEvilConfig.EnableRWRBlindZones.Value;
            if (checkBlindZones)
            {
                // Convert LOS vector to aircraft body frame (+Z forward, +X right, +Y up)
                Vector3 localDir = transform.InverseTransformDirection(dirToEmitter);
                float elevDeg = Mathf.Asin(Mathf.Clamp(localDir.y, -1.0f, 1.0f)) * Mathf.Rad2Deg;

                if (elevDeg < Capabilities.ElevationMinDeg || elevDeg > Capabilities.ElevationMaxDeg)
                {
                    // Emitter is inside the zenith blind cone (diving from high above) or belly blind cone!
                    return false;
                }
            }

            // 2. Ku-Band Active Seeker Sensitivity Check (Tiered)
            if (isActiveSeeker && distToEmitter > Capabilities.KuBandMaxDetectRange)
            {
                // Beyond the receiver's Ku-band sensitivity (3.5 / 16 / 40 km by tier)
                return false;
            }
            return true;
        }

        // Measured bearing: true direction with the receiver tier's angle-of-arrival jitter
        private Vector3 MeasureBearing(Vector3 dirToEmitter)
        {
            float jitterRange = EffectiveBearingJitterDegrees;
            Quaternion errorJitter = Quaternion.Euler(0.0f, UnityEngine.Random.Range(-jitterRange, jitterRange), 0.0f);
            return (errorJitter * dirToEmitter).normalized;
        }

        public void RegisterRadarPing(Unit emitter, Radar radar, RwrThreatState threatLevel, float signalStrength01 = 0.5f, bool isSeeker = false)
        {
            if (emitter == null || transform == null) return;

            bool isActiveSeeker = isSeeker || emitter is Missile;
            Vector3 dirToEmitter;
            if (!CanHear(emitter, isActiveSeeker, out dirToEmitter)) return;

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
            Vector3 measuredBearing = MeasureBearing(dirToEmitter);
            entry.LastDirection = measuredBearing;

            UpdateRelativeBearing(entry);

            // Feed bearing directly into ESM Triangulation Processor for cooperative multi-bearing tracking
            Aircraft hostAircraft = AttachedAircraft != null ? AttachedAircraft : GetComponent<Aircraft>();
            if (hostAircraft != null && !hostAircraft.disabled && !emitter.disabled)
            {
                RWRTriangulationProcessor.UpdateTrack(emitter, hostAircraft, measuredBearing, entry.SignalStrength01);
            }
        }

        /// <summary>
        /// Antenna coverage for a jammer (noise jamming on this aircraft): the same blind cones as a radar pulse.
        /// dirToJammer is the true world direction; displays must spread it (HMDJamCue sector), never show it.
        /// </summary>
        public bool CanHearJammer(Unit jammer, out Vector3 dirToJammer)
        {
            dirToJammer = Vector3.zero;
            if (jammer == null || transform == null) return false;
            return CanHear(jammer, false, out dirToJammer);
        }

        /// <summary>
        /// Server: a jam tick on this aircraft gives a bearing to the jammer for the passive tracking. Not a threat entry:
        /// no strobe, no warning, no vanilla locate. A Tier 1 receiver gets no single-ship fix from it (direction only).
        /// </summary>
        public void RegisterJamStrobe(Unit jammer, float amount01)
        {
            Aircraft hostAircraft = AttachedAircraft;
            if (hostAircraft == null || hostAircraft.disabled || jammer == null || jammer.disabled) return;

            Vector3 dirToJammer;
            if (!CanHearJammer(jammer, out dirToJammer)) return;
            Vector3 measuredBearing = MeasureBearing(dirToJammer);

            bool allowOwnFix = Capabilities.Tier != RwrTier.Tier1_Basic;
            RWRTriangulationProcessor.UpdateTrack(jammer, hostAircraft, measuredBearing, Mathf.Clamp01(amount01), allowOwnFix);
        }

        /// <summary>
        /// Receiver sensitivity (dBm) of the tier: the lowest one-way radar power that gives a passive bearing.
        /// </summary>
        public float EsmSensitivityDbm
        {
            get
            {
                float tier;
                switch (Capabilities.Tier)
                {
                    case RwrTier.Tier1_Basic: tier = CnoEvilConfig.Tier1_EsmSensitivity_dBm.Value; break;
                    case RwrTier.Tier3_Advanced: tier = CnoEvilConfig.Tier3_EsmSensitivity_dBm.Value; break;
                    default: tier = CnoEvilConfig.Tier2_EsmSensitivity_dBm.Value; break;
                }
                return IsTier4 ? Mathf.Min(tier, CnoEvilConfig.Tier4_EsmSensitivity_dBm.Value) : tier;
            }
        }

        /// <summary>
        /// Server: a radar scans this aircraft but does not detect it (below its threshold, or in its Doppler notch).
        /// The receiver still hears the radar one way when the main-lobe power is at or above EsmSensitivityDbm: a
        /// bearing for the passive tracking only. Not a threat entry: no strobe, no warning, no vanilla locate.
        /// </summary>
        public void RegisterEsmIntercept(Unit emitter, Radar radar, float dist)
        {
            Aircraft hostAircraft = AttachedAircraft;
            if (hostAircraft == null || hostAircraft.disabled || emitter == null || emitter.disabled || radar == null) return;

            float powerDbm = RadarSignalModel.EsmReceivedPowerDbm(radar, dist);
            float sensitivity = EsmSensitivityDbm;
            if (powerDbm < sensitivity) return;

            Vector3 dirToEmitter;
            if (!CanHear(emitter, false, out dirToEmitter)) return;
            Vector3 measuredBearing = MeasureBearing(dirToEmitter);
            float signal01 = Mathf.Clamp01((powerDbm - sensitivity) / 20.0f);
            RWRTriangulationProcessor.UpdateTrack(emitter, hostAircraft, measuredBearing, signal01);
        }

        /// <summary>
        /// Illumination below the emitter's detection threshold: the radar paints this aircraft but does not
        /// detect it. Only the TacScreen EM scan ring reads these (inward dip); SignalStrength01 is the dip depth.
        /// Not a threat: no strobe, no warning, no triangulation.
        /// </summary>
        public void RegisterBelowThreshold(Unit emitter, RwrThreatState threatLevel, float depth01, bool isSeeker = false)
        {
            if (emitter == null || transform == null || depth01 <= 0.0f) return;

            bool isActiveSeeker = isSeeker || emitter is Missile;
            Vector3 dirToEmitter;
            if (!CanHear(emitter, isActiveSeeker, out dirToEmitter)) return;

            StoreBelowThreshold(emitter, threatLevel, depth01, isActiveSeeker, MeasureBearing(dirToEmitter), Time.time);
        }

        /// <summary>
        /// Mirror client: a below-threshold illumination the host receiver measured. Returns true for a new one.
        /// </summary>
        public bool ApplyRemoteBelowThreshold(Unit emitter, RwrThreatState threatLevel, float depth01, bool isSeeker, Vector3 worldDir, float pingTime)
        {
            if (emitter == null) return false;
            RwrThreatEntry entry;
            if (_belowThreshold.TryGetValue(emitter, out entry) && pingTime <= entry.LastPingTime) return false;
            StoreBelowThreshold(emitter, threatLevel, depth01, isSeeker, worldDir, pingTime);
            return true;
        }

        private void StoreBelowThreshold(Unit emitter, RwrThreatState threatLevel, float depth01, bool isSeeker, Vector3 worldDir, float pingTime)
        {
            RwrThreatEntry entry;
            if (!_belowThreshold.TryGetValue(emitter, out entry))
            {
                entry = new RwrThreatEntry();
                entry.EmitterUnit = emitter;
                _belowThreshold[emitter] = entry;
            }
            entry.State = threatLevel;
            entry.LastPingTime = pingTime;
            entry.SignalStrength01 = Mathf.Clamp01(depth01);
            entry.IsActiveSeeker = isSeeker;
            entry.LastDirection = worldDir;
        }

        /// <summary>
        /// Strongest radar illumination of the last maxAgeSeconds: SNR of the emitter minus its detection threshold
        /// (-20..+20 dB, the scale of RadarPingSignal01 and BelowThresholdDepth01). Active seekers are skipped
        /// (SeekerPingSignal01 has a different scale). guidance: the strongest detection guides a missile.
        /// </summary>
        public bool TryGetPeakRadarMargin(float maxAgeSeconds, out float marginDb, out bool guidance)
        {
            marginDb = -20.0f;
            guidance = false;
            bool found = false;
            float now = Time.time;

            foreach (RwrThreatEntry threat in _threats.Values)
            {
                if (!IsFreshRadarPaint(threat, now, maxAgeSeconds)) continue;
                float margin = threat.SignalStrength01 * 20.0f;
                if (!found || margin > marginDb || (margin == marginDb && threat.State == RwrThreatState.MissileGuidance))
                {
                    marginDb = margin;
                    guidance = threat.State == RwrThreatState.MissileGuidance;
                }
                found = true;
            }

            foreach (RwrThreatEntry paint in _belowThreshold.Values)
            {
                if (!IsFreshRadarPaint(paint, now, maxAgeSeconds)) continue;
                float margin = -paint.SignalStrength01 * 20.0f;
                if (!found || margin > marginDb)
                {
                    marginDb = margin;
                    guidance = false;
                }
                found = true;
            }
            return found;
        }

        private static bool IsFreshRadarPaint(RwrThreatEntry entry, float now, float maxAgeSeconds)
        {
            if (entry == null || entry.EmitterUnit == null || entry.EmitterUnit.disabled) return false;
            if (entry.IsActiveSeeker || entry.EmitterUnit is Missile) return false;
            return now - entry.LastPingTime <= maxAgeSeconds;
        }

        // A ping older than this does not count as hearing the missile now (a ping needs line of sight)
        private const float HeardFreshSeconds = 1.0f;

        /// <summary>
        /// The receiver hears the missile now: its own seeker, or the CW illumination of a SARH missile homing on this
        /// aircraft (the launcher guides this missile at us). The guidance link of an ARH missile in midcourse is only an
        /// RWR strobe: it does not tell which missile is in the air or where.
        /// </summary>
        public bool HearsMissile(Missile missile)
        {
            if ((object)missile == null) return false;
            float now = Time.time;
            RwrThreatEntry entry;
            if (_threats.TryGetValue(missile, out entry) && entry != null) return now - entry.LastPingTime <= HeardFreshSeconds;

            Unit launcher = missile.owner;
            if (launcher == null || !_threats.TryGetValue(launcher, out entry) || entry == null) return false;
            if (entry.State != RwrThreatState.MissileGuidance || now - entry.LastPingTime > HeardFreshSeconds) return false;
            if (MunitionCatalog.Get(missile).Kind != SeekerKind.SemiActiveRadar || AttachedAircraft == null) return false;
            return CnoEvil.Patches.SeekerDispersionHelper.GetTargetForMissile(missile) == AttachedAircraft ||
                   missile.targetID == AttachedAircraft.persistentID;
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
            dlEntry.EstimatedPosition = estPos.ToGlobalPosition();
            dlEntry.EstimatedVelocity = estVel;
            dlEntry.IsMissile = isMissile;
            dlEntry.LastPingTime = now;

            Vector3 toThreat = estPos - transform.position;
            dlEntry.Distance = toThreat.magnitude;
            dlEntry.LastDirection = toThreat.sqrMagnitude > 1.0f ? toThreat.normalized : transform.forward;

            float angle;
            if (ComputeRelativeBearing(dlEntry.LastDirection, out angle)) dlEntry.RelativeBearing = angle;
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
        /// Horizontal bearing of a world direction relative to the aircraft nose (-180..180 deg).
        /// </summary>
        private bool ComputeRelativeBearing(Vector3 worldDir, out float angle)
        {
            angle = 0.0f;

            Vector3 fwd = transform.forward;
            fwd.y = 0.0f;
            worldDir.y = 0.0f;
            if (fwd.sqrMagnitude <= 0.001f || worldDir.sqrMagnitude <= 0.001f) return false;

            angle = Vector3.SignedAngle(fwd, worldDir, Vector3.up);
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
            if (ComputeRelativeBearing(entry.LastDirection, out angle)) entry.RelativeBearing = angle;
        }

        // Receivers of other aircraft feed only the faction tracks: their stale entries are pruned at this
        // interval, and their bearings relative to the nose (display only) are not refreshed
        private const float RemotePruneIntervalSeconds = 0.5f;
        private float _nextPruneTime;

        private void Update()
        {
            if (_threats.Count == 0 && _datalinkThreats.Count == 0 && _belowThreshold.Count == 0) return;

            float now = Time.time;
            bool displayed = CombatHUD.i != null && AttachedAircraft != null && CombatHUD.i.aircraft == AttachedAircraft;
            if (!displayed)
            {
                if (now < _nextPruneTime) return;
                _nextPruneTime = now + RemotePruneIntervalSeconds;
            }

            float maxStale = CnoEvilConfig.EffectiveRwrStrobePersistence;
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

            _staleList.Clear();
            foreach (var kvp in _belowThreshold)
            {
                if (now - kvp.Value.LastPingTime > BelowThresholdStaleSeconds || kvp.Key == null || kvp.Key.disabled)
                {
                    _staleList.Add(kvp.Key);
                }
            }
            for (int i = 0; i < _staleList.Count; i++)
            {
                _belowThreshold.Remove(_staleList[i]);
            }

            // Clean up stale Datalink threats (> 3.5s)
            _staleDatalinkList.Clear();
            foreach (var kvp in _datalinkThreats)
            {
                DatalinkThreatEntry dl = kvp.Value;
                if (now - dl.LastPingTime > CnoEvilConfig.DatalinkThreatExpirySeconds || dl.ThreatUnit == null || dl.ThreatUnit.disabled)
                {
                    _staleDatalinkList.Add(kvp.Key);
                }
                else if (displayed)
                {
                    // Refresh relative bearing as aircraft turns
                    float angle;
                    if (ComputeRelativeBearing(dl.EstimatedPosition.ToLocalPosition() - transform.position, out angle)) dl.RelativeBearing = angle;
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
    }
}
