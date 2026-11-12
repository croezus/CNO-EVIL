using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using CnoEvil.Config;

namespace CnoEvil.Core
{
    /// <summary>
    /// AI emission control against anti-radiation missiles (ADR-74). An AI radar (air defense, ship, radar site, AI
    /// aircraft) stops transmission when its knowledge shows an ARM that flies at it inside AIRadarShutdownRange_m:
    /// - ground and ship radars: the faction missile picture (MissilePicture, faction radars, dead reckoning);
    /// - AI aircraft: the missile warning model of the aircraft (MAWS, own radar, faction picture).
    /// "Flies at it": closest approach of the known path (3D or horizontal) within AIRadarShutdownMissDistance_m.
    /// After a reaction delay of 0.5 to 2 s the radar goes off until the estimated impact time plus
    /// AIRadarShutdownMargin_s (the missile picture loses the ARM 3 s after the own radar stops, so the off time comes
    /// from the estimate, not from a live track). A new ARM extends the off time, up to AIRadarShutdownMaxOff_s from the
    /// switch-off; after that cap the radar transmits for at least LockoutSeconds.
    /// The ARM seeker then flies to its datum with the launch miss (ARMSeeker_EmitterLoss_Patch).
    /// Host only. Only radars that this class switched off are switched on again (also when the mod or the option
    /// goes off). Ground and ship states go to clients with RadarEmissionSync; aircraft use the vanilla RpcToggleRadar.
    /// </summary>
    public static class RadarEmcon
    {
        private const float TickInterval = 0.25f;
        private const float LockoutSeconds = 10.0f;
        private const float MinReactionSeconds = 0.5f;
        private const float MaxReactionSeconds = 2.0f;
        // A missile-warning entry older than this is not current knowledge (s, Time.time)
        private const float WarningMaxAge = 1.0f;

        private sealed class State
        {
            public Radar Radar;
            public Unit Unit;
            public Aircraft Aircraft;
            public bool Off;
            public float OffSince;
            public float OffUntil;
            public float PendingAt = -1.0f;
            public float LockoutUntil;
        }

        private static readonly Dictionary<Radar, State> s_states = new Dictionary<Radar, State>();
        private static readonly HashSet<Radar> s_seen = new HashSet<Radar>();
        private static readonly List<Radar> s_remove = new List<Radar>();
        private static readonly List<Unit> s_silencedUnits = new List<Unit>();
        private static float s_nextTick;
        private static bool s_silencedDirty;

        private static readonly Action<Aircraft, bool> s_rpcToggleRadar = CreateToggle();

        private static Action<Aircraft, bool> CreateToggle()
        {
            try
            {
                MethodInfo m = AccessTools.Method(typeof(Aircraft), "RpcToggleRadar", new Type[] { typeof(bool) });
                if (m == null) return null;
                return (Action<Aircraft, bool>)Delegate.CreateDelegate(typeof(Action<Aircraft, bool>), m);
            }
            catch (Exception ex)
            {
                PatchErrorLog.Once("RadarEmcon.CreateToggle", ex);
                return null;
            }
        }

        /// <summary>
        /// Ground and ship units whose radar this class holds off (for RadarEmissionSync).
        /// </summary>
        public static List<Unit> SilencedSurfaceUnits
        {
            get { return s_silencedUnits; }
        }

        /// <summary>
        /// True once after the list of silenced ground and ship radars changed.
        /// </summary>
        public static bool ConsumeSilencedChanged()
        {
            bool dirty = s_silencedDirty;
            s_silencedDirty = false;
            return dirty;
        }

        /// <summary>
        /// Called every frame from the plugin, also while the mod is off (to restore the radars).
        /// </summary>
        public static void Update()
        {
            try
            {
                if (CnoEvilNet.IsMirrorClient) return;
                bool run = CnoEvilConfig.IsModActive && CnoEvilConfig.AIRadarShutdownEnabled != null && CnoEvilConfig.AIRadarShutdownEnabled.Value;
                if (!run)
                {
                    if (s_states.Count > 0) RestoreAll();
                    return;
                }

                float now = Time.time;
                if (now < s_nextTick) return;
                s_nextTick = now + TickInterval;

                PruneDead();
                bool armInFlight = AnyArmInFlight();
                if (!armInFlight && s_states.Count == 0)
                {
                    s_silencedUnits.Clear();
                    return;
                }

                List<Unit> units = UnitRegistry.allUnits;
                if (units == null) return;
                s_seen.Clear();
                for (int i = 0; i < units.Count; i++)
                {
                    Unit u = units[i];
                    if (u == null || u.disabled || u is Missile) continue;
                    Radar radar = u.radar as Radar;
                    if (radar == null || !s_seen.Add(radar)) continue;
                    Unit owner = radar.GetAttachedUnit();
                    if (owner == null) owner = u;
                    if (IsHumanControlled(owner)) continue;

                    State state;
                    s_states.TryGetValue(radar, out state);
                    if (state == null && !armInFlight) continue;
                    Evaluate(radar, owner, state, now);
                }
                RebuildSilencedList();
            }
            catch (Exception ex)
            {
                PatchErrorLog.Once("RadarEmcon.Update", ex);
            }
        }

        private static void Evaluate(Radar radar, Unit owner, State state, float now)
        {
            if (!radar.IsOperational())
            {
                if (state != null) s_states.Remove(radar);
                return;
            }

            float timeToGo;
            bool threat = FindArmThreat(radar, owner, now, out timeToGo);
            float margin = Mathf.Max(0.0f, CnoEvilConfig.AIRadarShutdownMargin_s.Value);
            float maxOff = Mathf.Max(1.0f, CnoEvilConfig.AIRadarShutdownMaxOff_s.Value);

            if (state != null && state.Off)
            {
                float cap = state.OffSince + maxOff;
                if (threat) state.OffUntil = Mathf.Min(cap, Mathf.Max(state.OffUntil, now + timeToGo + margin));
                if (now >= state.OffUntil)
                {
                    bool capped = now >= cap - 0.01f;
                    SwitchOn(state, capped ? "max off time" : "estimated impact passed");
                    state.LockoutUntil = capped ? now + LockoutSeconds : 0.0f;
                }
                return;
            }

            if (!threat)
            {
                if (state != null)
                {
                    state.PendingAt = -1.0f;
                    if (now >= state.LockoutUntil) s_states.Remove(radar);
                }
                return;
            }

            if (state == null)
            {
                state = new State();
                state.Radar = radar;
                state.Unit = owner;
                state.Aircraft = owner as Aircraft;
                s_states[radar] = state;
            }
            if (now < state.LockoutUntil) return;
            // A radar that vanilla (or another mod) holds off is not ours to switch
            if (!radar.activated)
            {
                state.PendingAt = -1.0f;
                return;
            }
            if (state.PendingAt < 0.0f)
            {
                state.PendingAt = now + UnityEngine.Random.Range(MinReactionSeconds, MaxReactionSeconds);
                return;
            }
            if (now < state.PendingAt) return;

            state.Off = true;
            state.OffSince = now;
            state.OffUntil = now + Mathf.Min(maxOff, timeToGo + margin);
            state.PendingAt = -1.0f;
            SetEmission(state, false);
            if (EvilLog.On(LogCat.AI))
            {
                EvilLog.Write(LogCat.AI, "EMCON {0}: radar off, ARM time to go {1:F1} s, back on in {2:F1} s",
                    EvilLog.Name(owner), timeToGo, state.OffUntil - now);
            }
        }

        private static void SwitchOn(State state, string why)
        {
            state.Off = false;
            state.PendingAt = -1.0f;
            SetEmission(state, true);
            if (EvilLog.On(LogCat.AI))
            {
                EvilLog.Write(LogCat.AI, "EMCON {0}: radar on ({1})", EvilLog.Name(state.Unit), why);
            }
        }

        private static void SetEmission(State state, bool on)
        {
            Radar radar = state.Radar;
            if ((object)radar == null || radar == null) return;
            if (state.Aircraft != null && s_rpcToggleRadar != null)
            {
                // Vanilla ClientRpc: sets the flag on every client; a dedicated host does not run it locally
                try { s_rpcToggleRadar(state.Aircraft, on); }
                catch (Exception ex) { PatchErrorLog.Once("RadarEmcon.RpcToggleRadar", ex); }
            }
            else
            {
                s_silencedDirty = true;
            }
            radar.activated = on;
            // Vanilla keeps the last scan while off: the contacts are not current
            if (!on && radar.detectedTargets != null) radar.detectedTargets.Clear();
        }

        /// <summary>
        /// A known ARM that flies at this radar inside the shutdown range; timeToGo to its closest approach.
        /// </summary>
        private static bool FindArmThreat(Radar radar, Unit owner, float now, out float timeToGo)
        {
            timeToGo = 0.0f;
            Transform scan = radar.GetScanPoint();
            Vector3 radarPos = scan != null ? scan.position : radar.transform.position;
            Vector3 radarVel = radar.GetVelocity();
            float range = CnoEvilConfig.AIRadarShutdownRange_m.Value;
            float miss = Mathf.Max(0.0f, CnoEvilConfig.AIRadarShutdownMissDistance_m.Value);
            bool found = false;
            float best = float.MaxValue;

            Aircraft aircraft = owner as Aircraft;
            if (aircraft != null)
            {
                Dictionary<Missile, MissileWarningModel.Warning> warnings = MissileWarningModel.GetWarnings(aircraft);
                if (warnings == null) return false;
                foreach (KeyValuePair<Missile, MissileWarningModel.Warning> kv in warnings)
                {
                    MissileWarningModel.Warning w = kv.Value;
                    if (kv.Key == null || kv.Key.disabled || !w.HasKinematics || now - w.LastKnown > WarningMaxAge) continue;
                    if (!IsArm(kv.Key)) continue;
                    float t;
                    if (Approaches(w.Position, w.Velocity, radarPos, radarVel, range, miss, out t) && t < best)
                    {
                        best = t;
                        found = true;
                    }
                }
            }
            else
            {
                FactionHQ hq = DatalinkNetwork.GetUnitFactionHQ(owner);
                Dictionary<Missile, MissilePicture.Sighting> picture = MissilePicture.GetPicture(hq);
                if (picture == null) return false;
                foreach (KeyValuePair<Missile, MissilePicture.Sighting> kv in picture)
                {
                    Missile m = kv.Key;
                    if (m == null || m.disabled || m.NetworkHQ == hq || !IsArm(m)) continue;
                    Vector3 pos, vel;
                    if (!MissilePicture.TryGet(hq, m, now, out pos, out vel)) continue;
                    float t;
                    if (Approaches(pos, vel, radarPos, radarVel, range, miss, out t) && t < best)
                    {
                        best = t;
                        found = true;
                    }
                }
            }
            if (found) timeToGo = best;
            return found;
        }

        /// <summary>
        /// The missile is inside range and closes to within miss metres (3D, or horizontal for a lofting missile).
        /// </summary>
        private static bool Approaches(Vector3 missilePos, Vector3 missileVel, Vector3 radarPos, Vector3 radarVel,
            float range, float miss, out float timeToGo)
        {
            timeToGo = 0.0f;
            Vector3 rel = radarPos - missilePos;
            if (rel.sqrMagnitude > range * range) return false;
            Vector3 vRel = missileVel - radarVel;
            float v2 = vRel.sqrMagnitude;
            if (v2 < 1.0f) return false;
            float t = Vector3.Dot(rel, vRel) / v2;
            if (t <= 0.0f) return false;
            float miss3d = (rel - vRel * t).magnitude;

            Vector3 relH = new Vector3(rel.x, 0.0f, rel.z);
            Vector3 vH = new Vector3(vRel.x, 0.0f, vRel.z);
            float vH2 = vH.sqrMagnitude;
            float tH = vH2 > 1.0f ? Vector3.Dot(relH, vH) / vH2 : -1.0f;
            float missH = tH > 0.0f ? (relH - vH * tH).magnitude : float.MaxValue;

            if (miss3d <= miss)
            {
                timeToGo = t;
                return true;
            }
            if (missH <= miss)
            {
                timeToGo = tH;
                return true;
            }
            return false;
        }

        private static bool IsArm(Missile m)
        {
            MunitionProfile p = MunitionCatalog.Get(m);
            if (p.Kind == SeekerKind.AntiRadiation) return true;
            return p.Kind == SeekerKind.OtherHoming && p.SeekerType == "ARM";
        }

        // Cheap gate only (no knowledge): skip the unit loop while no ARM flies
        private static bool AnyArmInFlight()
        {
            List<Missile> missiles = DatalinkNetwork.ActiveMissiles;
            for (int i = 0; i < missiles.Count; i++)
            {
                Missile m = missiles[i];
                if (m != null && !m.disabled && IsArm(m)) return true;
            }
            return false;
        }

        private static bool IsHumanControlled(Unit unit)
        {
            Aircraft a = unit as Aircraft;
            if (a == null) return false;
            return a.Player != null || (CombatHUD.i != null && CombatHUD.i.aircraft == a);
        }

        private static void PruneDead()
        {
            s_remove.Clear();
            foreach (KeyValuePair<Radar, State> kv in s_states)
            {
                if (kv.Key == null || kv.Value.Unit == null || kv.Value.Unit.disabled) s_remove.Add(kv.Key);
            }
            for (int i = 0; i < s_remove.Count; i++)
            {
                State st;
                if (s_states.TryGetValue(s_remove[i], out st) && st.Off && st.Aircraft == null) s_silencedDirty = true;
                s_states.Remove(s_remove[i]);
            }
            s_remove.Clear();
        }

        private static void RebuildSilencedList()
        {
            s_silencedUnits.Clear();
            foreach (KeyValuePair<Radar, State> kv in s_states)
            {
                State st = kv.Value;
                if (st.Off && st.Aircraft == null && st.Unit != null) s_silencedUnits.Add(st.Unit);
            }
        }

        /// <summary>
        /// Switches on every radar this class holds off (mod or option off).
        /// </summary>
        public static void RestoreAll()
        {
            foreach (KeyValuePair<Radar, State> kv in s_states)
            {
                State st = kv.Value;
                if (!st.Off || kv.Key == null) continue;
                try
                {
                    SwitchOn(st, "EMCON off");
                }
                catch (Exception ex)
                {
                    PatchErrorLog.Once("RadarEmcon.RestoreAll", ex);
                }
            }
            s_states.Clear();
            s_silencedUnits.Clear();
            s_silencedDirty = true;
        }

        public static void ClearAll()
        {
            s_states.Clear();
            s_seen.Clear();
            s_remove.Clear();
            s_silencedUnits.Clear();
            s_nextTick = 0.0f;
            s_silencedDirty = false;
        }
    }
}
