using System;
using System.Collections.Generic;
using UnityEngine;
using CnoEvil.Config;
using CnoEvil.Components;

namespace CnoEvil.Core
{
    /// <summary>
    /// Missile warning from sensor knowledge (ADR-28), in place of the vanilla MissileWarning rule (true target ID).
    /// A hostile missile is warned when:
    /// - the own RWR hears its seeker, or its launcher guiding a missile at us (at once), or
    /// - its known path is a collision course with us for MissileWarningHold_s. Known path: own MAWS (5 km, line of
    ///   sight, the vanilla detectionRange), own radar contact, or the faction missile picture (MissilePicture; on a
    ///   mirror client the datalink relay of the host). Collision course: closest approach of the relative velocity line,
    ///   in 3D or horizontal (a lofting missile), inside the MissileThreatInference tolerance.
    /// The warning clears ReleaseSeconds after the last collision-course result.
    /// OwnSensor: the own RWR or MAWS sees the missile; otherwise the warning comes only over the datalink.
    /// The vanilla knownMissiles list and its events carry the result to the threat list, the alarm, the HUD, the map,
    /// the warning lights and the AI.
    /// </summary>
    public static class MissileWarningModel
    {
        public const float MawsRange = 5000.0f;
        private const float MawsLineOfSightMagnification = 1000.0f;
        private const float ReleaseSeconds = 2.0f;
        private const float EvaluateInterval = 0.2f;
        private const float EnableInterval = 0.25f;

        public sealed class Warning
        {
            public Missile Missile;
            public Vector3 Position;
            public Vector3 Velocity;
            public bool HasKinematics;
            public bool Warned;
            public bool OwnSensor;
            public float OnCourseSince = -1.0f;
            public float LastOnCourse = -1000.0f;
            public float LastKnown;
        }

        private sealed class AircraftState
        {
            public Aircraft Aircraft;
            public float NextEvaluate;
            public readonly Dictionary<Missile, Warning> Missiles = new Dictionary<Missile, Warning>();
        }

        private static readonly Func<Aircraft, MissileWarning> f_missileWarning =
            FastReflection.CreateFieldGetter<Aircraft, MissileWarning>("missileWarning");
        private static readonly Func<MissileWarning, Aircraft> f_aircraft =
            FastReflection.CreateFieldGetter<MissileWarning, Aircraft>("aircraft");
        private static readonly System.Reflection.FieldInfo s_aircraftField =
            HarmonyLib.AccessTools.Field(typeof(MissileWarning), "aircraft");
        private static readonly Func<MissileWarning, List<Missile>> f_unknownMissiles =
            FastReflection.CreateFieldGetter<MissileWarning, List<Missile>>("unknownMissiles");
        private static readonly Func<MissileWarning, Action<MissileWarning.OnMissileWarning>> f_onWarning =
            FastReflection.CreateFieldGetter<MissileWarning, Action<MissileWarning.OnMissileWarning>>("onMissileWarning");
        private static readonly Func<MissileWarning, Action<MissileWarning.OffMissileWarning>> f_offWarning =
            FastReflection.CreateFieldGetter<MissileWarning, Action<MissileWarning.OffMissileWarning>>("offMissileWarning");

        private static readonly Dictionary<MissileWarning, AircraftState> s_states = new Dictionary<MissileWarning, AircraftState>();
        private static readonly Dictionary<Aircraft, AircraftState> s_byAircraft = new Dictionary<Aircraft, AircraftState>();
        private static readonly List<Missile> s_dropped = new List<Missile>();
        private static readonly List<MissileWarning> s_deadStates = new List<MissileWarning>();
        private static float s_nextEnable;

        /// <summary>
        /// Vanilla enables MissileWarning only when a missile locks the aircraft. With the mod every aircraft
        /// evaluates while hostile missiles fly: the host and single player all aircraft (AI included), a mirror
        /// client only its own aircraft.
        /// </summary>
        public static void Tick()
        {
            float now = Time.time;
            if (now < s_nextEnable) return;
            s_nextEnable = now + EnableInterval;
            PruneStates();
            if (f_missileWarning == null || DatalinkNetwork.ActiveMissiles.Count == 0) return;

            if (CnoEvilNet.IsMirrorClient)
            {
                Enable(CombatHUD.i != null ? CombatHUD.i.aircraft : null);
                return;
            }
            List<Unit> units = UnitRegistry.allUnits;
            if (units == null) return;
            for (int i = 0; i < units.Count; i++)
            {
                Aircraft a = units[i] as Aircraft;
                if (a != null) Enable(a);
            }
        }

        private static void Enable(Aircraft aircraft)
        {
            if (aircraft == null || aircraft.disabled) return;
            MissileWarning mw = f_missileWarning(aircraft);
            if (mw != null && !mw.enabled) mw.enabled = true;
        }

        /// <summary>
        /// Replaces the body of MissileWarning.Update while the mod is on.
        /// </summary>
        public static void Evaluate(MissileWarning mw)
        {
            float now = Time.time;
            AircraftState state = GetState(mw);
            if (state == null || state.Aircraft == null || state.Aircraft.disabled)
            {
                ReleaseAll(mw, state);
                mw.enabled = false;
                return;
            }
            if (now < state.NextEvaluate) return;
            state.NextEvaluate = now + EvaluateInterval;

            Aircraft aircraft = state.Aircraft;
            FactionHQ hq = aircraft.NetworkHQ;
            PhysRWRReceiver rwr = PhysRWRReceiver.Get(aircraft);
            Radar ownRadar = aircraft.radar as Radar;
            Vector3 ownPos = aircraft.transform.position;
            Vector3 ownVel = aircraft.rb != null ? aircraft.rb.velocity : Vector3.zero;
            bool mirror = CnoEvilNet.IsMirrorClient;
            float holdTime = Mathf.Max(0.0f, CnoEvilConfig.MissileWarningHold_s.Value);

            List<Missile> missiles = DatalinkNetwork.ActiveMissiles;
            for (int i = 0; i < missiles.Count; i++)
            {
                Missile m = missiles[i];
                if (m == null || m.disabled || !IsHostile(m, aircraft, hq)) continue;

                Vector3 pos = Vector3.zero;
                Vector3 vel = Vector3.zero;
                bool kinematics = false;
                bool ownSensor = false;

                bool heard = rwr != null && rwr.HearsMissile(m);
                Vector3 truePos = m.transform.position;
                if ((truePos - ownPos).sqrMagnitude <= MawsRange * MawsRange && m.LineOfSight(ownPos, MawsLineOfSightMagnification))
                {
                    ownSensor = true;
                    kinematics = true;
                }
                else if (ownRadar != null && ownRadar.detectedTargets != null && ownRadar.detectedTargets.Contains(m))
                {
                    kinematics = true;
                }
                if (kinematics)
                {
                    pos = truePos;
                    vel = m.rb != null ? m.rb.velocity : Vector3.zero;
                }
                else
                {
                    kinematics = TryGetOffboard(hq, rwr, m, now, mirror, out pos, out vel);
                }

                if (!heard && !kinematics) continue;

                Warning w;
                if (!state.Missiles.TryGetValue(m, out w))
                {
                    w = new Warning();
                    w.Missile = m;
                    state.Missiles[m] = w;
                }
                w.LastKnown = now;
                w.HasKinematics = kinematics;
                if (kinematics)
                {
                    w.Position = pos;
                    w.Velocity = vel;
                }
                w.OwnSensor = heard || ownSensor;

                bool onCourse = heard || (kinematics && OnCollisionCourse(m, aircraft, pos, vel, ownPos, ownVel));
                if (onCourse)
                {
                    if (w.OnCourseSince < 0.0f) w.OnCourseSince = now;
                    w.LastOnCourse = now;
                    if (heard || now - w.OnCourseSince >= holdTime) w.Warned = true;
                }
                else
                {
                    w.OnCourseSince = -1.0f;
                }
            }

            // Release: no collision course (or no knowledge) for ReleaseSeconds, or the missile is gone
            s_dropped.Clear();
            foreach (KeyValuePair<Missile, Warning> kv in state.Missiles)
            {
                Warning w = kv.Value;
                if (kv.Key == null || kv.Key.disabled)
                {
                    s_dropped.Add(kv.Key);
                    continue;
                }
                if (now - w.LastOnCourse > ReleaseSeconds) w.Warned = false;
                if (now - w.LastKnown > ReleaseSeconds && !w.Warned) s_dropped.Add(kv.Key);
            }
            for (int i = 0; i < s_dropped.Count; i++) state.Missiles.Remove(s_dropped[i]);

            SyncVanillaList(mw, state);
            PruneUnknown(mw);

            // Nothing in flight: rest until Tick or a vanilla lock enables the component again
            if (missiles.Count == 0 && mw.knownMissiles.Count == 0) mw.enabled = false;
        }

        private static bool IsHostile(Missile m, Aircraft aircraft, FactionHQ hq)
        {
            if (m.owner == aircraft) return false;
            if (m.owner != null && m.owner.NetworkHQ == hq) return false;
            if (m.NetworkHQ != null && m.NetworkHQ == hq) return false;
            return true;
        }

        private static bool TryGetOffboard(FactionHQ hq, PhysRWRReceiver rwr, Missile m, float now, bool mirror, out Vector3 pos, out Vector3 vel)
        {
            if (!mirror) return MissilePicture.TryGet(hq, m, now, out pos, out vel);

            // Mirror client: the datalink relay of the host, dead-reckoned from its last report
            pos = Vector3.zero;
            vel = Vector3.zero;
            DatalinkThreatEntry relay;
            if (rwr == null || !rwr.TryGetDatalinkThreat(m, out relay) || relay == null || !relay.IsMissile) return false;
            vel = relay.EstimatedVelocity;
            pos = relay.EstimatedPosition + vel * Mathf.Max(0.0f, now - relay.LastPingTime);
            return true;
        }

        /// <summary>
        /// Closest approach of the missile to the aircraft along their relative velocity. A missile on proportional
        /// navigation keeps the relative velocity on the line of sight, so its lead turn counts as a collision course.
        /// The horizontal test covers a lofting missile that climbs before it dives on the aircraft.
        /// </summary>
        public static bool OnCollisionCourse(Missile m, Aircraft aircraft, Vector3 missilePos, Vector3 missileVel, Vector3 ownPos, Vector3 ownVel)
        {
            Vector3 r = ownPos - missilePos;
            Vector3 vRel = missileVel - ownVel;
            float baseTolerance = MissileThreatInference.BaseTolerance(m) + aircraft.maxRadius;
            if (PassesWithin(r, vRel, baseTolerance)) return true;
            r.y = 0.0f;
            vRel.y = 0.0f;
            return PassesWithin(r, vRel, baseTolerance);
        }

        private static bool PassesWithin(Vector3 r, Vector3 vRel, float baseTolerance)
        {
            float speedSqr = vRel.sqrMagnitude;
            if (speedSqr < 1.0f) return false;
            float t = Vector3.Dot(r, vRel) / speedSqr;
            if (t <= 0.0f || t > MissileThreatInference.MaxTimeToGo) return false;
            float miss = (r - vRel * t).magnitude;
            float along = Mathf.Sqrt(speedSqr) * t;
            return miss <= baseTolerance + MissileThreatInference.PathToleranceSlope * along;
        }

        /// <summary>
        /// knownMissiles = the warned missiles; the vanilla events fire on each change.
        /// </summary>
        private static void SyncVanillaList(MissileWarning mw, AircraftState state)
        {
            List<Missile> known = mw.knownMissiles;
            for (int i = known.Count - 1; i >= 0; i--)
            {
                Missile m = known[i];
                Warning w;
                bool keep = m != null && !m.disabled && state.Missiles.TryGetValue(m, out w) && w.Warned;
                if (keep) continue;
                known.RemoveAt(i);
                if (m != null) FireOff(mw, m);
            }
            foreach (KeyValuePair<Missile, Warning> kv in state.Missiles)
            {
                if (!kv.Value.Warned || known.Contains(kv.Key)) continue;
                known.Add(kv.Key);
                FireOn(mw, kv.Key);
            }
        }

        private static void FireOn(MissileWarning mw, Missile m)
        {
            Action<MissileWarning.OnMissileWarning> handler = f_onWarning != null ? f_onWarning(mw) : null;
            if (handler == null) return;
            MissileWarning.OnMissileWarning e = new MissileWarning.OnMissileWarning();
            e.missile = m;
            handler(e);
        }

        private static void FireOff(MissileWarning mw, Missile m)
        {
            Action<MissileWarning.OffMissileWarning> handler = f_offWarning != null ? f_offWarning(mw) : null;
            if (handler == null) return;
            MissileWarning.OffMissileWarning e = new MissileWarning.OffMissileWarning();
            e.missile = m;
            handler(e);
        }

        // Vanilla adds every missile that locks the aircraft; with the mod on only dead ones leave the list
        private static void PruneUnknown(MissileWarning mw)
        {
            List<Missile> unknown = f_unknownMissiles != null ? f_unknownMissiles(mw) : null;
            if (unknown == null) return;
            for (int i = unknown.Count - 1; i >= 0; i--)
            {
                if (unknown[i] == null || unknown[i].disabled) unknown.RemoveAt(i);
            }
        }

        private static void ReleaseAll(MissileWarning mw, AircraftState state)
        {
            List<Missile> known = mw.knownMissiles;
            for (int i = known.Count - 1; i >= 0; i--)
            {
                Missile m = known[i];
                known.RemoveAt(i);
                if (m != null) FireOff(mw, m);
            }
            if (state != null) state.Missiles.Clear();
        }

        private static AircraftState GetState(MissileWarning mw)
        {
            AircraftState state;
            if (s_states.TryGetValue(mw, out state)) return state;

            Aircraft aircraft = f_aircraft != null ? f_aircraft(mw) : null;
            if (aircraft == null)
            {
                // Vanilla sets the field only in LockedByMissile; TryGetNearestIncoming reads it
                aircraft = mw.GetComponent<Aircraft>();
                if (aircraft == null) return null;
                if (s_aircraftField != null) s_aircraftField.SetValue(mw, aircraft);
            }
            state = new AircraftState();
            state.Aircraft = aircraft;
            s_states[mw] = state;
            s_byAircraft[aircraft] = state;
            return state;
        }

        private static void PruneStates()
        {
            s_deadStates.Clear();
            foreach (KeyValuePair<MissileWarning, AircraftState> kv in s_states)
            {
                if (kv.Key == null || kv.Value.Aircraft == null) s_deadStates.Add(kv.Key);
            }
            for (int i = 0; i < s_deadStates.Count; i++)
            {
                AircraftState dead = s_states[s_deadStates[i]];
                if ((object)dead.Aircraft != null) s_byAircraft.Remove(dead.Aircraft);
                s_states.Remove(s_deadStates[i]);
            }
        }

        /// <summary>
        /// The warning state of the aircraft for each missile it knows (warned or not); null when there is none.
        /// </summary>
        public static Dictionary<Missile, Warning> GetWarnings(Aircraft aircraft)
        {
            AircraftState state;
            return (aircraft != null && s_byAircraft.TryGetValue(aircraft, out state)) ? state.Missiles : null;
        }

        public static bool IsWarned(Aircraft aircraft, Missile missile)
        {
            Dictionary<Missile, Warning> warnings = GetWarnings(aircraft);
            Warning w;
            return warnings != null && (object)missile != null && warnings.TryGetValue(missile, out w) && w.Warned;
        }

        /// <summary>
        /// True when the aircraft warns of the missile only through the datalink (no own RWR or MAWS contact).
        /// </summary>
        public static bool IsDatalinkOnly(Aircraft aircraft, Missile missile)
        {
            Dictionary<Missile, Warning> warnings = GetWarnings(aircraft);
            Warning w;
            return warnings != null && (object)missile != null && warnings.TryGetValue(missile, out w) && w.Warned && !w.OwnSensor;
        }

        public static void ClearAll()
        {
            s_states.Clear();
            s_byAircraft.Clear();
            s_dropped.Clear();
            s_deadStates.Clear();
            s_nextEnable = 0.0f;
        }
    }
}
