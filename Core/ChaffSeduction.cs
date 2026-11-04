using System.Collections.Generic;
using UnityEngine;
using CnoEvil.Config;

namespace CnoEvil.Core
{
    /// <summary>
    /// Chaff against ARH and SARH seekers. A cloud competes with the target only inside the seeker resolution cell
    /// (beam, range gate) and only with the part of its echo that the Doppler processing does not reject:
    /// a cloud outside the velocity gate, decelerating faster than an aircraft can, or in the clutter notch is
    /// attenuated by ChaffDopplerRejection_dB. The seeker is captured when the chaff share of the echo power in the cell,
    /// C / (C + target RCS), is above a threshold drawn once per missile and cloud. A captured seeker tracks the
    /// cloud (the seeker patches move the vanilla datum to it, ADR-22) until the cloud fades, the missile passes it,
    /// or the target echo in the cloud cell is larger. After that the seeker must find the target again inside its
    /// gimbal limit.
    /// An active decoy (ChaffCloud.IsDecoy) uses the same capture: its equivalent RCS grows with the transmitter range,
    /// it passes the Doppler filter, it also captures a seeker that homes on jam, and an ARH seeker with leading-edge
    /// tracking ignores it when it is behind the target.
    /// </summary>
    public static class ChaffSeduction
    {
        private struct CaptureState
        {
            public ChaffCloud Cloud;
            public int CloudId;
            public bool Seduced;
        }

        private static readonly Dictionary<int, CaptureState> s_states = new Dictionary<int, CaptureState>();

        public static bool Active
        {
            get
            {
                return (CnoEvilConfig.ChaffEnabled.Value || CnoEvilConfig.DecoyEnabled.Value) &&
                       (ChaffCloudRegistry.Any || s_states.Count > 0);
            }
        }

        /// <summary>
        /// True while the seeker of this missile tracks a chaff cloud or an active decoy. cloud = the tracked object.
        /// transmitterPos: the seeker itself (ARH, activeSeeker) or the illuminator (SARH); a decoy repeats that signal.
        /// hojMode: the seeker homes on the jam strobe. Then chaff has no effect, but a decoy is an emitter in the beam and
        /// competes without a range gate.
        /// </summary>
        public static bool Evaluate(int missileId, Vector3 seekerPos, Vector3 seekerVel, Vector3 targetPos, Vector3 targetVel,
            float targetRcs, float jamTolerance, bool hasClutterBackground, Vector3 transmitterPos, bool activeSeeker,
            bool hojMode, out ChaffCloud cloud)
        {
            cloud = null;
            ChaffCloudRegistry.Prune();

            CaptureState state;
            bool hasState = s_states.TryGetValue(missileId, out state);
            if (hasState && state.Cloud != null)
            {
                if (state.Cloud.Id == state.CloudId && state.Cloud.Alive && (state.Cloud.IsDecoy || !hojMode) &&
                    StillHolds(state.Cloud, seekerPos, seekerVel, targetPos, targetVel, targetRcs, transmitterPos, activeSeeker))
                {
                    cloud = state.Cloud;
                    return true;
                }
                Release(missileId);
                return false;
            }

            List<ChaffCloud> clouds = ChaffCloudRegistry.Clouds;
            if (clouds.Count == 0) return false;

            Vector3 toTarget = targetPos - seekerPos;
            float targetRange = toTarget.magnitude;
            if (targetRange < 1.0f) return false;
            Vector3 targetDir = toTarget / targetRange;
            float targetRadial = Vector3.Dot(targetVel - seekerVel, targetDir);
            float cosHalfBeam = Mathf.Cos(0.5f * CnoEvilConfig.ChaffSeekerBeamwidth_deg.Value * Mathf.Deg2Rad);
            float rangeGate = CnoEvilConfig.ChaffSeekerRangeGate_m.Value;
            bool chaffOn = CnoEvilConfig.ChaffEnabled.Value && !hojMode;
            bool decoyOn = CnoEvilConfig.DecoyEnabled.Value;

            float chaffTotal = 0.0f;
            float bestRcs = 0.0f;
            ChaffCloud best = null;
            for (int i = 0; i < clouds.Count; i++)
            {
                ChaffCloud c = clouds[i];
                if (c.IsDecoy ? !decoyOn : !chaffOn) continue;
                Vector3 toCloud = c.Position - seekerPos;
                float cloudRange = toCloud.magnitude;
                if (cloudRange < 1.0f) continue;
                // A seeker that homes on jam has no range gate
                if (!hojMode && Mathf.Abs(cloudRange - targetRange) > rangeGate) continue;
                if (!hojMode && c.IsDecoy && LeadingEdgeRejects(activeSeeker, cloudRange, targetRange)) continue;
                Vector3 cloudDir = toCloud / cloudRange;
                if (Vector3.Dot(cloudDir, targetDir) < cosHalfBeam) continue;

                // A decoy repeats the signal coherently with the Doppler of the aircraft: it passes the velocity gate and the notch
                float rcs = c.IsDecoy
                    ? c.EquivalentRcs(Vector3.Distance(c.Position, transmitterPos))
                    : c.Rcs * DopplerFactor(c, cloudDir, seekerVel, targetRadial, hasClutterBackground);
                chaffTotal += rcs;
                if (rcs > bestRcs)
                {
                    bestRcs = rcs;
                    best = c;
                }
            }
            if (best == null || chaffTotal <= 0.0f) return false;

            float share = chaffTotal / (chaffTotal + Mathf.Max(0.0f, targetRcs));
            if (share < Threshold(missileId, best.Id, jamTolerance)) return false;

            state.Cloud = best;
            state.CloudId = best.Id;
            state.Seduced = true;
            s_states[missileId] = state;
            cloud = best;

            if (CnoEvilConfig.DebugLogging.Value && CnoEvilPlugin.Log != null)
            {
                CnoEvilPlugin.Log.LogInfo(string.Format("{0} captured seeker {1}: echo {2:F5} m2, target {3:F5} m2, share {4:F2}, range {5:F0} m{6}",
                    best.IsDecoy ? "Decoy" : "Chaff", missileId, chaffTotal, targetRcs, share, targetRange, hojMode ? ", HOJ" : ""));
            }
            return true;
        }

        /// <summary>
        /// The illuminator of a SARH missile tracks a decoy (RadarDecoySeduction): the seeker sees only the decoy echo,
        /// so it tracks the decoy. No threshold: the target is not lit.
        /// </summary>
        public static void ForceCapture(int missileId, ChaffCloud decoy)
        {
            if (decoy == null) return;
            CaptureState state;
            s_states.TryGetValue(missileId, out state);
            if (state.Cloud == decoy && state.CloudId == decoy.Id) return;
            state.Cloud = decoy;
            state.CloudId = decoy.Id;
            state.Seduced = true;
            s_states[missileId] = state;
        }

        /// <summary>
        /// The captured seeker loses the cloud (too weak, passed, faded). The missile keeps the seduced state, so it must
        /// find the target again inside its gimbal limit.
        /// </summary>
        public static void Release(int missileId)
        {
            CaptureState state;
            if (!s_states.TryGetValue(missileId, out state) || state.Cloud == null) return;
            state.Cloud = null;
            s_states[missileId] = state;
        }

        /// <summary>
        /// Position and velocity of the cloud the seeker tracks now (the datum for the vanilla guidance).
        /// </summary>
        public static bool TryGetDatum(int missileId, out Vector3 position, out Vector3 velocity)
        {
            position = Vector3.zero;
            velocity = Vector3.zero;
            CaptureState state;
            if (!s_states.TryGetValue(missileId, out state) || state.Cloud == null) return false;
            if (state.Cloud.Id != state.CloudId || !state.Cloud.Alive) return false;
            position = state.Cloud.Position;
            velocity = state.Cloud.Velocity;
            return true;
        }

        /// <summary>
        /// True after a capture ended: the seeker looks along the missile axis and sees the target only inside its gimbal limit.
        /// </summary>
        public static bool WasSeduced(int missileId)
        {
            CaptureState state;
            return s_states.TryGetValue(missileId, out state) && state.Seduced && state.Cloud == null;
        }

        public static void Clear(int missileId)
        {
            s_states.Remove(missileId);
        }

        public static void ClearAll()
        {
            s_states.Clear();
        }

        /// <summary>
        /// 1 when the cloud passes the seeker Doppler processing: inside the velocity gate of the target, radial
        /// deceleration within an aircraft limit, and not in the clutter notch with clutter behind it. Else the rejection.
        /// </summary>
        private static float DopplerFactor(ChaffCloud cloud, Vector3 cloudDir, Vector3 seekerVel, float targetRadial, bool hasClutterBackground)
        {
            Vector3 cloudVel = cloud.Velocity;
            float cloudRadial = Vector3.Dot(cloudVel - seekerVel, cloudDir);
            bool inGate = Mathf.Abs(cloudRadial - targetRadial) <= CnoEvilConfig.ChaffSeekerVelocityGate_mps.Value;
            bool steady = Mathf.Abs(Vector3.Dot(cloud.Acceleration, cloudDir)) <= CnoEvilConfig.ChaffDopplerRateLimit_mps2.Value;
            bool notched = hasClutterBackground && Mathf.Abs(Vector3.Dot(cloudVel, cloudDir)) < CnoEvilConfig.EffectiveNotchThreshold;
            if (inGate && steady && !notched) return 1.0f;
            return Mathf.Pow(10.0f, -Mathf.Max(0.0f, CnoEvilConfig.ChaffDopplerRejection_dB.Value) / 10.0f);
        }

        /// <summary>
        /// Leading-edge range tracking of an active seeker (DecoyLeadingEdgeRejection): a decoy more than half the range gate
        /// behind the target is ignored, because the target echo arrives first. A repeater cannot answer before it hears the pulse.
        /// </summary>
        internal static bool LeadingEdgeRejects(bool activeSeeker, float decoyRange, float targetRange)
        {
            return activeSeeker && CnoEvilConfig.DecoyLeadingEdgeRejection.Value &&
                   decoyRange - targetRange > 0.5f * CnoEvilConfig.ChaffSeekerRangeGate_m.Value;
        }

        /// <summary>
        /// The captured gate stays on the cloud (or decoy) while it is in front of the missile, still has an echo, and the
        /// target echo in its cell (beam, range and velocity gate around it) is not larger. An active seeker that tracks
        /// the leading edge goes back to the target when the target is in the decoy cell well in front of the decoy.
        /// </summary>
        private static bool StillHolds(ChaffCloud cloud, Vector3 seekerPos, Vector3 seekerVel, Vector3 targetPos, Vector3 targetVel,
            float targetRcs, Vector3 transmitterPos, bool activeSeeker)
        {
            float cloudRcs = cloud.EquivalentRcs(Vector3.Distance(cloud.Position, transmitterPos));
            if (cloudRcs <= 0.0f) return false;
            Vector3 toCloud = cloud.Position - seekerPos;
            float cloudRange = toCloud.magnitude;
            if (cloudRange < 1.0f || Vector3.Dot(toCloud, seekerVel) <= 0.0f) return false;
            Vector3 cloudDir = toCloud / cloudRange;

            Vector3 toTarget = targetPos - seekerPos;
            float targetRange = toTarget.magnitude;
            if (targetRange < 1.0f) return true;
            Vector3 targetDir = toTarget / targetRange;
            float cosHalfBeam = Mathf.Cos(0.5f * CnoEvilConfig.ChaffSeekerBeamwidth_deg.Value * Mathf.Deg2Rad);
            bool targetInCell = Vector3.Dot(cloudDir, targetDir) >= cosHalfBeam &&
                                Mathf.Abs(cloudRange - targetRange) <= CnoEvilConfig.ChaffSeekerRangeGate_m.Value;
            if (!targetInCell) return true;
            if (cloud.IsDecoy)
            {
                // The repeater copies the target Doppler: only the leading edge and the echo power separate them
                return !LeadingEdgeRejects(activeSeeker, cloudRange, targetRange) && targetRcs <= cloudRcs;
            }

            float cloudRadial = Vector3.Dot(cloud.Velocity - seekerVel, cloudDir);
            float targetRadial = Vector3.Dot(targetVel - seekerVel, targetDir);
            bool targetInGate = Mathf.Abs(targetRadial - cloudRadial) <= CnoEvilConfig.ChaffSeekerVelocityGate_mps.Value;
            return !targetInGate || targetRcs <= cloudRcs;
        }

        /// <summary>
        /// Capture threshold of one missile against one cloud: uniform between ChaffCaptureRatioMin and 0.95 (a hash of the two
        /// ids, so it is the same on every evaluation), then raised by the seeker jam tolerance: 1 - (1 - t) / (1 + tolerance).
        /// </summary>
        internal static float Threshold(int missileId, int cloudId, float jamTolerance)
        {
            uint h = unchecked((uint)missileId * 2654435761u ^ (uint)cloudId * 2246822519u);
            h ^= h >> 15;
            h = unchecked(h * 2246822507u);
            h ^= h >> 13;
            float u = (h & 0xFFFFFF) / 16777216.0f;
            float t = Mathf.Lerp(Mathf.Clamp01(CnoEvilConfig.ChaffCaptureRatioMin.Value), 0.95f, u);
            return 1.0f - (1.0f - t) / (1.0f + Mathf.Max(0.0f, jamTolerance));
        }
    }
}
