using System;
using System.Collections.Generic;
using UnityEngine;
using CnoEvil.Config;

namespace CnoEvil.Core
{
    /// <summary>
    /// Beam of one jamming pod (EW-25 Medusa) at its last vanilla jam tick.
    /// </summary>
    public struct PodBeam
    {
        public Unit Owner;
        public Unit Target;
        public JammingPod Pod;        // null on a mirror client
        public int PodIndex;          // index of the pod on its owner (network key)
        public float PowerFraction;   // capacitor level of the tick, 0..1
        public float PowerScale;      // pod power / VanillaPodPower (game data of the pod, 1 when unknown)
        public float Time;            // Time.timeSinceLevelLoad of the tick
        public float MaxRange;        // end of the vanilla rangeFalloff curve (m)
    }

    /// <summary>
    /// Stand-off noise jamming from jamming pods, from the one-way radar equation at this moment (no accumulation).
    /// The vanilla pod tick (0.2 s) registers a beam; a beam is active for BeamHoldSeconds after its tick, so the jam
    /// stops when the vanilla tick stops (trigger released, no target, no line of sight, empty capacitor).
    /// Every radar receiver (search radar, SARH illuminator and seeker, ARH seeker) adds JamPowerAtReceiver to its noise.
    /// </summary>
    public static class StandoffJamming
    {
        private const float BeamHoldSeconds = 0.3f;
        private const float RemoteBeamHoldSeconds = 0.5f;
        public const float MinPhysicsConeDeg = 5.0f;
        private const float SquareDegreesPerSphere = 41253.0f;
        private const float GaussBeamK = 2.7725887f; // 4 ln 2
        private const float LosCacheSeconds = 0.5f;
        private const int MaxLosCacheEntries = 1024;
        // JammingPod.power of the vanilla JammingPod1 (EW-25 Medusa): the pod that JammingPodPower_W describes.
        // Another pod transmits JammingPodPower_W x its own power / this (Aryx AEC-124: 12).
        public const float VanillaPodPower = 13.0f;

        private struct LosEntry
        {
            public float Time;
            public bool Blocked;
        }

        private static readonly List<PodBeam> s_beams = new List<PodBeam>();
        private static readonly Dictionary<Unit, JammingPod[]> s_podsByOwner = new Dictionary<Unit, JammingPod[]>();
        private static readonly Dictionary<long, LosEntry> s_losCache = new Dictionary<long, LosEntry>();
        // Debug log: pod -> target of the last logged beam
        private static readonly Dictionary<JammingPod, Unit> s_loggedBeams = new Dictionary<JammingPod, Unit>();

        private static Func<JammingPod, AnimationCurve> s_rangeFalloffGetter;
        private static Func<JammingPod, Transform> s_directionGetter;
        private static Func<JammingPod, float> s_powerGetter;
        private static bool s_gettersInitialized;

        /// <summary>
        /// Active beams (read-only for callers).
        /// </summary>
        public static List<PodBeam> Beams
        {
            get { return s_beams; }
        }

        public static float PodPowerWatts
        {
            get { return Mathf.Max(0.0f, CnoEvilConfig.JammingPodPower_W.Value); }
        }

        /// <summary>
        /// Full beam angle (deg) of the physics model: JammingPodConeAngle_deg, at least MinPhysicsConeDeg.
        /// </summary>
        public static float PhysicsConeDeg
        {
            get { return Mathf.Max(MinPhysicsConeDeg, JammingConeProcessor.ConeAngleDeg); }
        }

        /// <summary>
        /// Length of the beam tail past the target, as a fraction of the target distance.
        /// </summary>
        public const float BeyondTargetFraction = 0.25f;

        /// <summary>
        /// Range taper of the beam: 1 up to the target, cos^2 fade to 0 at targetRange * (1 + BeyondTargetFraction).
        /// </summary>
        public static float BeyondTargetFactor(float range, float targetRange)
        {
            if (range <= targetRange) return 1.0f;
            float tail = targetRange * BeyondTargetFraction;
            if (tail <= 0.0f) return 0.0f;
            float t = (range - targetRange) / tail;
            if (t >= 1.0f) return 0.0f;
            float c = Mathf.Cos(t * Mathf.PI * 0.5f);
            return c * c;
        }

        /// <summary>
        /// Main-lobe gain of the pod antenna from its beam width (gain ~ 41253 / beam^2).
        /// </summary>
        public static float PodMainGain
        {
            get
            {
                float cone = PhysicsConeDeg;
                return SquareDegreesPerSphere / (cone * cone);
            }
        }

        public static bool AnyActive
        {
            get
            {
                float now = UnityEngine.Time.timeSinceLevelLoad;
                for (int i = 0; i < s_beams.Count; i++)
                {
                    if (IsActive(s_beams[i], now)) return true;
                }
                return false;
            }
        }

        public static bool IsActive(PodBeam beam, float now)
        {
            // A beam from the host (no pod) covers two sync intervals, so one lost packet does not cut it
            float hold = (beam.Pod != null) ? BeamHoldSeconds : RemoteBeamHoldSeconds;
            return beam.Owner != null && !beam.Owner.disabled && beam.Target != null && !beam.Target.disabled &&
                   now - beam.Time <= hold && beam.PowerFraction > 0.0f;
        }

        private static void InitGetters()
        {
            if (s_gettersInitialized) return;
            s_gettersInitialized = true;
            s_rangeFalloffGetter = FastReflection.CreateFieldGetter<JammingPod, AnimationCurve>("rangeFalloff");
            s_directionGetter = FastReflection.CreateFieldGetter<JammingPod, Transform>("directionTransform");
            s_powerGetter = FastReflection.CreateFieldGetter<JammingPod, float>("power");
        }

        /// <summary>
        /// Transmit power of the pod relative to the vanilla pod (its vanilla power field / VanillaPodPower), 1 when unknown.
        /// </summary>
        public static float GetPowerScale(JammingPod pod)
        {
            InitGetters();
            if (pod == null || s_powerGetter == null) return 1.0f;
            float power = s_powerGetter(pod);
            return (power > 0.0f) ? power / VanillaPodPower : 1.0f;
        }

        public static AnimationCurve GetRangeFalloff(JammingPod pod)
        {
            InitGetters();
            return (pod != null && s_rangeFalloffGetter != null) ? s_rangeFalloffGetter(pod) : null;
        }

        /// <summary>
        /// Range (m) where the vanilla falloff curve first reaches 0 (EW-25 pod: 80 km).
        /// </summary>
        public static float CurveMaxRange(AnimationCurve falloff)
        {
            if (falloff == null || falloff.length == 0) return 0.0f;
            for (int i = 0; i < falloff.length; i++)
            {
                Keyframe k = falloff[i];
                if (k.value <= 0.0f && k.time > 0.0f) return k.time;
            }
            return falloff[falloff.length - 1].time;
        }

        /// <summary>
        /// Index of a pod among the pods of its owner (same order on every peer).
        /// </summary>
        public static int GetPodIndex(Unit owner, JammingPod pod)
        {
            JammingPod[] pods = GetPods(owner, pod);
            if (pods == null) return 0;
            for (int i = 0; i < pods.Length; i++)
            {
                if (pods[i] == pod) return i;
            }
            return 0;
        }

        /// <summary>
        /// Pods of the owner, cached. The cache is built again when a pod in it is destroyed or when expected is not in
        /// it (the loadout changed on rearm), so the pod index stays the same on every peer.
        /// </summary>
        private static JammingPod[] GetPods(Unit owner, JammingPod expected)
        {
            if (owner == null) return null;
            JammingPod[] pods;
            if (!s_podsByOwner.TryGetValue(owner, out pods) || pods == null || !IsCurrent(pods, expected))
            {
                pods = owner.GetComponentsInChildren<JammingPod>(true);
                s_podsByOwner[owner] = pods;
            }
            return pods;
        }

        private static bool IsCurrent(JammingPod[] pods, JammingPod expected)
        {
            bool found = expected == null;
            for (int i = 0; i < pods.Length; i++)
            {
                if (pods[i] == null) return false;
                if (pods[i] == expected) found = true;
            }
            return found;
        }

        /// <summary>
        /// Server: the vanilla pod tick jammed its target with the given capacitor fraction.
        /// </summary>
        public static void RegisterPodTick(JammingPod pod, Unit target, float powerFraction, float maxRange)
        {
            if (pod == null || pod.attachedUnit == null || target == null) return;
            PodBeam beam;
            beam.Owner = pod.attachedUnit;
            beam.Target = target;
            beam.Pod = pod;
            beam.PodIndex = GetPodIndex(pod.attachedUnit, pod);
            beam.PowerFraction = Mathf.Clamp01(powerFraction);
            beam.PowerScale = GetPowerScale(pod);
            beam.Time = UnityEngine.Time.timeSinceLevelLoad;
            beam.MaxRange = maxRange;
            Upsert(beam);
            LogBeam(beam);
        }

        /// <summary>
        /// DebugLogging: one line when a pod starts to jam a new target.
        /// </summary>
        private static void LogBeam(PodBeam beam)
        {
            if (!CnoEvilConfig.DebugLogging.Value || CnoEvilPlugin.Log == null) return;
            Unit last;
            if (s_loggedBeams.TryGetValue(beam.Pod, out last) && last == beam.Target) return;
            s_loggedBeams[beam.Pod] = beam.Target;
            CnoEvilPlugin.Log.LogInfo(string.Format(
                "[CNO-EVIL] Jamming pod {0} on {1} jams {2}: capacitor {3:F2}, power x{4:F2} ({5:F0} W), range {6:F0} m, pod range {7:F0} m",
                beam.Pod.GetType().Name, beam.Owner.unitName, beam.Target.unitName, beam.PowerFraction, beam.PowerScale,
                PodPowerWatts * beam.PowerScale, Vector3.Distance(GetOrigin(beam), beam.Target.transform.position), beam.MaxRange));
        }

        /// <summary>
        /// Mirror client: a beam the host sent (JamBeamSync). The pod power comes from the local copy of the pod.
        /// </summary>
        public static void ApplyRemoteBeam(Unit owner, Unit target, int podIndex, float powerFraction, float maxRange)
        {
            if (owner == null || target == null) return;
            JammingPod[] pods = GetPods(owner, null);
            JammingPod pod = (pods != null && podIndex >= 0 && podIndex < pods.Length) ? pods[podIndex] : null;
            PodBeam beam;
            beam.Owner = owner;
            beam.Target = target;
            beam.Pod = null;
            beam.PodIndex = podIndex;
            beam.PowerFraction = Mathf.Clamp01(powerFraction);
            beam.PowerScale = GetPowerScale(pod);
            beam.Time = UnityEngine.Time.timeSinceLevelLoad;
            beam.MaxRange = maxRange;
            Upsert(beam);
        }

        private static void Upsert(PodBeam beam)
        {
            float now = beam.Time;
            for (int i = s_beams.Count - 1; i >= 0; i--)
            {
                PodBeam b = s_beams[i];
                if (b.Owner == beam.Owner && b.PodIndex == beam.PodIndex)
                {
                    s_beams[i] = beam;
                    return;
                }
                // Drop expired beams while scanning (the list stays small)
                if (!IsActive(b, now) && now - b.Time > 5.0f) s_beams.RemoveAt(i);
            }
            s_beams.Add(beam);
        }

        /// <summary>
        /// Beam origin: the pod direction transform when known, else the owner.
        /// </summary>
        public static Vector3 GetOrigin(PodBeam beam)
        {
            return (beam.Pod != null) ? GetPodOrigin(beam.Pod) : beam.Owner.transform.position;
        }

        /// <summary>
        /// Pod antenna position: the vanilla direction transform (the beam origin vanilla uses for its line of sight).
        /// </summary>
        public static Vector3 GetPodOrigin(JammingPod pod)
        {
            InitGetters();
            Transform dir = (s_directionGetter != null) ? s_directionGetter(pod) : null;
            return (dir != null) ? dir.position : pod.transform.position;
        }

        /// <summary>
        /// Received stand-off jamming power (W) at a radar receiver, summed over the active beams of hostile pods.
        /// Per beam: Pj = Ppod * capacitor * Gj(theta) * Gr(phi) * lambda^2 * min(1, Br/Bj) / ((4 pi)^2 R^2 Lj) / K.
        /// Gj: pod main gain x flat-top beam pattern (theta = receiver off the beam axis).
        /// Gr: receiver gain toward the jammer: Gaussian main lobe around lookDir, floor at sidelobeDb.
        /// receiverId keys the terrain line-of-sight cache (0 = no cache).
        /// </summary>
        public static float JamPowerAtReceiver(Vector3 rxPos, Vector3 lookDir, ref RadarSpecs rx, float sidelobeDb, FactionHQ rxHq, int receiverId)
        {
            if (s_beams.Count == 0 || rxHq == null) return 0.0f;
            float podWatts = PodPowerWatts;
            if (podWatts <= 0.0f) return 0.0f;

            float now = UnityEngine.Time.timeSinceLevelLoad;
            if (!AnyHostileBeam(rxHq, now)) return 0.0f;

            float halfRad = PhysicsConeDeg * 0.5f * Mathf.Deg2Rad;
            float mainGain = PodMainGain;
            float rxGain = Mathf.Max(1.0f, rx.AntennaGainLinear);
            float rxBeamDeg = Mathf.Sqrt(SquareDegreesPerSphere / rxGain);
            float sidelobe = Mathf.Pow(10.0f, sidelobeDb * 0.1f);
            float lookMag = lookDir.magnitude;
            float burnThroughRatio = ElectronicWarfare.BurnThroughRatio;
            // Power and gain are set per beam; the other pod values are the same for every beam
            JammerSpecs jammer = JammerSpecs.CreateStandardPod();

            float total = 0.0f;
            for (int i = 0; i < s_beams.Count; i++)
            {
                PodBeam beam = s_beams[i];
                if (!IsActive(beam, now)) continue;

                // Hostile only: friendly and neutral receivers are not jammed (vanilla targets hostiles only)
                FactionHQ ownerHq = beam.Owner.NetworkHQ;
                if (ownerHq == null || ownerHq == rxHq) continue;

                Vector3 origin = GetOrigin(beam);
                Vector3 toRx = rxPos - origin;
                float range = toRx.magnitude;
                if (range < 1.0f) continue;
                if (beam.MaxRange > 0.0f && range > beam.MaxRange) continue;

                Vector3 axis = beam.Target.transform.position - origin;
                float axisMag = axis.magnitude;
                if (axisMag < 1.0f) continue;

                // The beam ends shortly past the target
                float taper = BeyondTargetFactor(range, axisMag);
                if (taper <= 0.0f) continue;

                float cosTheta = Vector3.Dot(axis, toRx) / (axisMag * range);
                float theta = Mathf.Acos(Mathf.Clamp(cosTheta, -1.0f, 1.0f));
                float podGain = JammingConeProcessor.BeamGain(theta / halfRad);
                if (podGain <= 0.0f) continue;

                // Receiver antenna toward the jammer
                float gr = rxGain * sidelobe;
                if (lookMag > 0.001f)
                {
                    float cosPhi = Vector3.Dot(lookDir, -toRx) / (lookMag * range);
                    float phiDeg = Mathf.Acos(Mathf.Clamp(cosPhi, -1.0f, 1.0f)) * Mathf.Rad2Deg;
                    float u = phiDeg / rxBeamDeg;
                    gr = rxGain * Mathf.Max(sidelobe, Mathf.Exp(-GaussBeamK * u * u));
                }

                if (IsLosBlocked(beam, i, origin, rxPos, receiverId, now)) continue;

                jammer.PowerWatts = podWatts * beam.PowerScale * beam.PowerFraction * taper;
                jammer.AntennaGainLinear = mainGain * podGain;
                total += ElectronicWarfare.CalculateJammerPowerAtReceiver(range, ref jammer, ref rx, gr) / burnThroughRatio;
            }
            return total;
        }

        /// <summary>
        /// True when an active beam of a pod hostile to rxHq exists (cheap check before the per-receiver setup).
        /// </summary>
        private static bool AnyHostileBeam(FactionHQ rxHq, float now)
        {
            for (int i = 0; i < s_beams.Count; i++)
            {
                PodBeam beam = s_beams[i];
                if (!IsActive(beam, now)) continue;
                FactionHQ ownerHq = beam.Owner.NetworkHQ;
                if (ownerHq != null && ownerHq != rxHq) return true;
            }
            return false;
        }

        private static bool IsLosBlocked(PodBeam beam, int beamIndex, Vector3 origin, Vector3 rxPos, int receiverId, float now)
        {
            long key = 0;
            if (receiverId != 0)
            {
                int ownerId = beam.Owner.GetInstanceID();
                key = ((long)(ownerId * 8 + beam.PodIndex) << 32) ^ (uint)receiverId;
                LosEntry cached;
                if (s_losCache.TryGetValue(key, out cached) && now - cached.Time < LosCacheSeconds) return cached.Blocked;
            }

            RaycastHit hit;
            bool blocked = Physics.Linecast(origin, rxPos, out hit, PhysicsLayers.StaticsMask);

            if (receiverId != 0)
            {
                if (s_losCache.Count >= MaxLosCacheEntries) s_losCache.Clear();
                LosEntry entry;
                entry.Time = now;
                entry.Blocked = blocked;
                s_losCache[key] = entry;
            }
            return blocked;
        }

        /// <summary>
        /// Gate of the vanilla pod tick (replaces FactionHQ.IsTargetBeingTracked in JammingPod.FixedUpdate).
        /// Vanilla needs a vanilla sighting in the last 4 s; a passively located emitter is not refreshed (ADR-14), so the
        /// pod went quiet after 4 s. A jammer can stay on a known emitter: the faction track (mod) is enough.
        /// </summary>
        public static bool PodCanTrackTarget(FactionHQ hq, Unit target)
        {
            if (hq == null) return false;
            if (hq.IsTargetBeingTracked(target)) return true;
            if (!CnoEvilConfig.IsModActive || target == null) return false;

            TriangulationTrack track;
            if (!RWRTriangulationProcessor.TryPeekTrack(hq, target, out track)) return false;
            GlobalPosition estimate;
            float error;
            return RWRTriangulationProcessor.TryGetFactionEstimate(track, target, out estimate, out error);
        }

        public static void OnUnitDestroyed(Unit unit)
        {
            if ((object)unit == null) return;
            s_podsByOwner.Remove(unit);
            if (s_loggedBeams.Count > 0)
            {
                JammingPod[] pods = unit.GetComponentsInChildren<JammingPod>(true);
                for (int i = 0; i < pods.Length; i++) s_loggedBeams.Remove(pods[i]);
            }
            for (int i = s_beams.Count - 1; i >= 0; i--)
            {
                if ((object)s_beams[i].Owner == (object)unit || (object)s_beams[i].Target == (object)unit) s_beams.RemoveAt(i);
            }
        }

        public static void ClearAll()
        {
            s_beams.Clear();
            s_podsByOwner.Clear();
            s_losCache.Clear();
            s_loggedBeams.Clear();
        }
    }
}
