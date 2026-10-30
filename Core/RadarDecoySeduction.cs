using System.Collections.Generic;
using UnityEngine;
using CnoEvil.Components;
using CnoEvil.Config;

namespace CnoEvil.Core
{
    /// <summary>
    /// Active decoys (ChaffCloud.IsDecoy) against track radars: a radar that guides a missile at the target or holds a
    /// missile lock on it (Radar_CanSeeRadarReturn_Patch, states MissileGuidance and HardLock). The decoy repeats the radar
    /// signal with the equivalent RCS of ChaffCloud.EquivalentRcs at the radar range. When it is in the radar resolution
    /// cell of the target (half-power beam from the antenna gain, range gate ChaffSeekerRangeGate_m), not behind the
    /// target for the leading edge, and its share of the echo power is above the threshold of ChaffSeduction, the track
    /// gate moves to the decoy. The radar then does not detect the target, and its faction track gets a false fix at the
    /// decoy. The gate stays on the decoy until its battery is empty, or the target in the decoy cell is stronger.
    /// Search radars are not affected.
    /// </summary>
    public static class RadarDecoySeduction
    {
        private struct CaptureState
        {
            public ChaffCloud Decoy;
            public int DecoyId;
            public Unit RadarUnit;
            public Unit Target;
        }

        private static readonly Dictionary<long, CaptureState> s_states = new Dictionary<long, CaptureState>();
        private static readonly List<long> s_removeKeys = new List<long>();

        private static bool On
        {
            get { return CnoEvilConfig.DecoyEnabled.Value && CnoEvilConfig.DecoyBreaksRadarLock.Value; }
        }

        /// <summary>
        /// Cheap gate for the radar patch: decoys can act on radars now (a decoy in flight or a capture held).
        /// </summary>
        public static bool Active
        {
            get { return On && (s_states.Count > 0 || ChaffCloudRegistry.AnyDecoy); }
        }

        private static long Key(Radar radar, Unit target)
        {
            return ((long)radar.GetInstanceID() << 32) | (uint)target.GetInstanceID();
        }

        /// <summary>
        /// True while a decoy holds the track gate of this radar on this target. decoy = the tracked decoy.
        /// targetRcs: the target RCS that the radar sees (after the Doppler filter).
        /// </summary>
        public static bool Evaluate(Radar radar, Unit target, Vector3 radarPos, Vector3 targetPos, float targetRcs, out ChaffCloud decoy)
        {
            decoy = null;
            if (radar == null || target == null) return false;
            if (!On || (s_states.Count == 0 && !ChaffCloudRegistry.AnyDecoy)) return false;
            ChaffCloudRegistry.Prune();

            long key = Key(radar, target);
            Toward toward = new Toward(radarPos, targetPos, BeamCos(radar));
            CaptureState state;
            if (s_states.TryGetValue(key, out state))
            {
                if (state.Decoy != null && state.Decoy.Id == state.DecoyId && state.Decoy.Alive &&
                    StillHolds(state.Decoy, radarPos, ref toward, targetRcs))
                {
                    decoy = state.Decoy;
                    return true;
                }
                s_states.Remove(key);
                if (CnoEvilConfig.DebugLogging.Value && CnoEvilPlugin.Log != null)
                {
                    CnoEvilPlugin.Log.LogInfo(string.Format("Decoy released radar {0} track of {1}",
                        RadarName(radar), target.unitName));
                }
                return false;
            }

            if (toward.Range < 1.0f) return false;
            float rangeGate = CnoEvilConfig.ChaffSeekerRangeGate_m.Value;
            List<ChaffCloud> clouds = ChaffCloudRegistry.Clouds;
            float total = 0.0f;
            float bestRcs = 0.0f;
            ChaffCloud best = null;
            for (int i = 0; i < clouds.Count; i++)
            {
                ChaffCloud c = clouds[i];
                if (!c.IsDecoy) continue;
                Vector3 toDecoy = c.Position - radarPos;
                float decoyRange = toDecoy.magnitude;
                if (decoyRange < 1.0f) continue;
                if (Mathf.Abs(decoyRange - toward.Range) > rangeGate) continue;
                // A monostatic radar is an active transmitter: its range tracker sees the target echo first
                if (ChaffSeduction.LeadingEdgeRejects(true, decoyRange, toward.Range)) continue;
                if (Vector3.Dot(toDecoy / decoyRange, toward.Dir) < toward.CosHalfBeam) continue;

                float rcs = c.EquivalentRcs(decoyRange);
                total += rcs;
                if (rcs > bestRcs)
                {
                    bestRcs = rcs;
                    best = c;
                }
            }
            if (best == null || total <= 0.0f) return false;

            float share = total / (total + Mathf.Max(0.0f, targetRcs));
            if (share < ChaffSeduction.Threshold(radar.GetInstanceID(), best.Id, 0.0f)) return false;

            state.Decoy = best;
            state.DecoyId = best.Id;
            state.RadarUnit = radar.GetAttachedUnit();
            state.Target = target;
            s_states[key] = state;
            decoy = best;

            if (CnoEvilConfig.DebugLogging.Value && CnoEvilPlugin.Log != null)
            {
                CnoEvilPlugin.Log.LogInfo(string.Format("Decoy captured radar {0} track of {1}: echo {2:F3} m2, target {3:F3} m2, share {4:F2}, range {5:F0} m",
                    RadarName(radar), target.unitName, total, targetRcs, share, toward.Range));
            }
            return true;
        }

        /// <summary>
        /// Read-only: the decoy that holds the track gate of this radar on this target (SARH illuminator, AI hold).
        /// </summary>
        public static bool TryGetCapture(Radar radar, Unit target, out ChaffCloud decoy)
        {
            decoy = null;
            if (radar == null || target == null || s_states.Count == 0 || !On) return false;
            CaptureState state;
            if (!s_states.TryGetValue(Key(radar, target), out state)) return false;
            if (state.Decoy == null || state.Decoy.Id != state.DecoyId || !state.Decoy.Alive) return false;
            decoy = state.Decoy;
            return true;
        }

        /// <summary>
        /// Removes the captures of a destroyed radar unit or target.
        /// </summary>
        public static void RemoveUnit(Unit unit)
        {
            if (unit == null || s_states.Count == 0) return;
            s_removeKeys.Clear();
            foreach (KeyValuePair<long, CaptureState> kv in s_states)
            {
                if (kv.Value.RadarUnit == unit || kv.Value.Target == unit) s_removeKeys.Add(kv.Key);
            }
            for (int i = 0; i < s_removeKeys.Count; i++) s_states.Remove(s_removeKeys[i]);
            s_removeKeys.Clear();
        }

        public static void ClearAll()
        {
            s_states.Clear();
            s_removeKeys.Clear();
        }

        private struct Toward
        {
            public Vector3 Dir;
            public float Range;
            public float CosHalfBeam;

            public Toward(Vector3 radarPos, Vector3 targetPos, float cosHalfBeam)
            {
                Vector3 d = targetPos - radarPos;
                Range = d.magnitude;
                Dir = Range > 0.0f ? d / Range : Vector3.forward;
                CosHalfBeam = cosHalfBeam;
            }
        }

        /// <summary>
        /// The gate stays on the decoy while it transmits and the target echo in the decoy cell is not larger. The decoy
        /// copies the target Doppler, so only the leading edge and the echo power separate them.
        /// </summary>
        private static bool StillHolds(ChaffCloud decoy, Vector3 radarPos, ref Toward toward, float targetRcs)
        {
            Vector3 toDecoy = decoy.Position - radarPos;
            float decoyRange = toDecoy.magnitude;
            if (decoyRange < 1.0f) return false;
            float decoyRcs = decoy.EquivalentRcs(decoyRange);
            if (decoyRcs <= 0.0f) return false;
            if (toward.Range < 1.0f) return true;

            bool targetInCell = Vector3.Dot(toDecoy / decoyRange, toward.Dir) >= toward.CosHalfBeam &&
                                Mathf.Abs(decoyRange - toward.Range) <= CnoEvilConfig.ChaffSeekerRangeGate_m.Value;
            if (!targetInCell) return true;
            return !ChaffSeduction.LeadingEdgeRejects(true, decoyRange, toward.Range) && targetRcs <= decoyRcs;
        }

        /// <summary>
        /// Cosine of the half-power half beam of the radar: beamwidth = sqrt(41253 / G) deg from the antenna gain of its
        /// hardware class; ChaffSeekerBeamwidth_deg when the radar has no PhysRadarEmitter.
        /// </summary>
        private static float BeamCos(Radar radar)
        {
            float beamDeg = CnoEvilConfig.ChaffSeekerBeamwidth_deg.Value;
            PhysRadarEmitter emitter = PhysRadarEmitter.Get(radar);
            if (emitter != null && emitter.Specs.AntennaGainLinear > 1.0f)
            {
                beamDeg = Mathf.Clamp(Mathf.Sqrt(41253.0f / emitter.Specs.AntennaGainLinear), 0.5f, 60.0f);
            }
            return Mathf.Cos(0.5f * beamDeg * Mathf.Deg2Rad);
        }

        private static string RadarName(Radar radar)
        {
            Unit unit = radar.GetAttachedUnit();
            return unit != null ? unit.unitName : radar.name;
        }
    }
}
