using System;
using System.Collections.Generic;
using UnityEngine;
using CnoEvil.Config;

namespace CnoEvil.Core
{
    /// <summary>
    /// Decides whether the weapons that use one AI radar can still shoot down an anti-radiation missile before it
    /// arrives (ADR-80). RadarEmcon asks this before it switches a surface radar off: while the answer is yes the radar
    /// stays on and the turrets engage the ARM; at the first no it goes silent and (for a mobile vehicle) relocates.
    /// Pure assessment: it reads game state only and has no side effects.
    ///
    /// Defenders of a radar R = the own unit's weapon stations plus the stations of every unit that links its turrets to
    /// R (Unit.radar == R, the vanilla 300 m turret-radar link), plus (ADR-84) surface air defense of the same faction
    /// whose own radar transmits and whose station range reaches R. A station counts when it has an anti-missile weapon
    /// (WeaponInfo.effectiveness.antiMissile greater than 0), ammunition, and a turret.
    ///
    /// An ARM is covered when either an interceptor is already in flight at it and closes to it before the deadline, or
    /// a defender station can launch and the interceptor flies out to a meet point inside the weapon envelope before the
    /// deadline. The deadline is the ARM time to go minus the caller reserve (pack-up time). In a salvo the ARMs share
    /// the stations (ADR-84): each planned shot takes a round and holds the station for its fire interval.
    /// Host only.
    /// </summary>
    public static class ArmInterceptAssessor
    {
        private const float DefenderCacheSeconds = 2.0f;
        private const float DetectionLagSeconds = 1.0f;
        private const float FlyoutSpeedFactor = 0.8f;
        private const float InFlightClosureThreshold = 150.0f;

        private struct Defender
        {
            public WeaponStation Station;
            public Turret Turret;
        }

        private sealed class DefenderCache
        {
            public readonly List<Defender> Defenders = new List<Defender>();
            public float BuiltAt = -1.0f;
        }

        private static readonly Dictionary<Radar, DefenderCache> s_cache = new Dictionary<Radar, DefenderCache>();
        private static readonly List<Radar> s_remove = new List<Radar>();

        private static readonly Func<Turret, float> f_lockTime = FastReflection.CreateFieldGetter<Turret, float>("lockTime");
        private static readonly Func<Turret, float> f_traverseRate = FastReflection.CreateFieldGetter<Turret, float>("traverseRate");

        public static void ClearAll()
        {
            s_cache.Clear();
            s_remove.Clear();
        }

        /// <summary>
        /// One known anti-radiation missile that flies at the radar (from RadarEmcon).
        /// </summary>
        public struct ArmThreat
        {
            public Missile Arm;
            public Vector3 Pos;
            public Vector3 Vel;
            public float TimeToGo;
        }

        public enum SalvoVerdict
        {
            /// <summary>Every ARM is covered by an interceptor in flight or a planned shot: stay on.</summary>
            Covered,
            /// <summary>A later ARM of a salvo cannot be covered in time because the stations are busy with earlier ARMs:
            /// stay on and engage until the deadline of that ARM (ADR-84).</summary>
            HoldUntilDeadline,
            /// <summary>Go silent now.</summary>
            GiveUp
        }

        private static readonly List<float> s_freeAt = new List<float>();
        private static readonly List<int> s_ammoLeft = new List<int>();
        private static readonly Comparison<ArmThreat> s_byTimeToGo = (a, b) => a.TimeToGo.CompareTo(b.TimeToGo);

        /// <summary>
        /// Salvo assessment (ADR-84). The ARMs (sorted here by time to go) share the defender stations: each planned shot
        /// takes one round and holds the station for its fire interval. The deadline of an ARM is its time to go minus the
        /// caller reserve (pack-up time). holdFor is the deadline (s from now) of the first ARM that cannot be covered when
        /// the verdict is HoldUntilDeadline.
        /// </summary>
        public static SalvoVerdict AssessSalvo(Radar radar, Unit owner, List<ArmThreat> arms, float reserve,
            out float holdFor, out int covered, out string why)
        {
            holdFor = 0.0f;
            covered = 0;
            why = string.Empty;
            try
            {
                if (radar == null || owner == null || arms == null || arms.Count == 0)
                {
                    why = "no defender";
                    return SalvoVerdict.GiveUp;
                }
                arms.Sort(s_byTimeToGo);

                List<Defender> defenders = GetDefenders(radar, owner);
                s_freeAt.Clear();
                s_ammoLeft.Clear();
                for (int i = 0; i < defenders.Count; i++)
                {
                    WeaponStation st = defenders[i].Station;
                    s_freeAt.Add(0.0f);
                    s_ammoLeft.Add(st != null ? st.Ammo : 0);
                }

                float nowLevel = Time.timeSinceLevelLoad;
                for (int k = 0; k < arms.Count; k++)
                {
                    ArmThreat t = arms[k];
                    if (t.Arm == null) continue;
                    float deadline = t.TimeToGo - reserve;

                    // An interceptor already committed to this ARM covers it (a SARH one needs the radar on to guide).
                    if (InterceptorInFlight(owner, t.Arm, t.Pos, t.Vel, deadline))
                    {
                        covered++;
                        continue;
                    }

                    string reason;
                    if (deadline <= 0.0f)
                    {
                        reason = "too late";
                    }
                    else if (defenders.Count == 0)
                    {
                        reason = "no defender";
                    }
                    else
                    {
                        bool detected = radar.detectedTargets != null && radar.detectedTargets.Contains(t.Arm);
                        if (PlanShot(defenders, t, deadline, nowLevel, detected, out reason))
                        {
                            covered++;
                            continue;
                        }
                    }

                    why = reason;
                    // Only a later ARM of a salvo that misses its deadline because earlier ARMs hold the stations keeps the
                    // radar on, until its own deadline. Out of envelope, no ammo, no defender, or the first ARM: off now.
                    if (k > 0 && covered > 0 && reason == "too late" && deadline > 0.0f)
                    {
                        holdFor = deadline;
                        return SalvoVerdict.HoldUntilDeadline;
                    }
                    return SalvoVerdict.GiveUp;
                }
                why = "intercept in time";
                return SalvoVerdict.Covered;
            }
            catch (Exception ex)
            {
                PatchErrorLog.Once("ArmInterceptAssessor.AssessSalvo", ex);
                why = "error";
                return SalvoVerdict.GiveUp;
            }
        }

        /// <summary>
        /// Plans the earliest-meeting shot at one ARM over the shared station schedule and books the station on success.
        /// </summary>
        private static bool PlanShot(List<Defender> defenders, ArmThreat t, float deadline, float nowLevel, bool detected,
            out string reason)
        {
            float armSpeed = t.Vel.magnitude;
            bool anyAmmo = false;
            bool anyLate = false;
            int bestIdx = -1;
            float bestMeet = float.MaxValue;
            float bestLaunch = 0.0f;

            for (int i = 0; i < defenders.Count; i++)
            {
                Defender d = defenders[i];
                WeaponStation station = d.Station;
                // Vanilla sets WeaponStation.Reloading for the whole station when ONE launcher empties: the rounds left
                // decide, not the flag.
                if (station == null || station.WeaponInfo == null || s_ammoLeft[i] <= 0) continue;
                anyAmmo = true;

                WeaponInfo info = station.WeaponInfo;
                TargetRequirements req = info.targetRequirements;
                if (armSpeed > req.maxSpeed && req.maxSpeed > 0.0f) continue;

                Turret turret = d.Turret != null ? d.Turret : station.GetTurret();
                Vector3 launcher = turret != null ? turret.transform.position : t.Pos;
                if (turret == null) continue;

                float tLaunch = Mathf.Max(s_freeAt[i], LaunchDelay(d, station, launcher, t.Pos, nowLevel, detected));
                if (tLaunch > deadline)
                {
                    anyLate = true;
                    continue;
                }

                float vAvg = info.gun ? info.muzzleVelocity : FlyoutSpeedFactor * info.GetMaxSpeed();
                if (vAvg < 1.0f) continue;

                float tMeet;
                if (!SolveFlyout(t.Pos, t.Vel, launcher, vAvg, tLaunch, out tMeet) || tMeet > deadline)
                {
                    anyLate = true;
                    continue;
                }

                Vector3 meet = t.Pos + t.Vel * tMeet;
                float meetRange = (meet - launcher).magnitude;
                if (meetRange < req.minRange || meetRange > req.maxRange) continue;

                float altMin = req.maxRange > 0.0f ? req.minAltitude * meetRange / req.maxRange : req.minAltitude;
                float heightAboveGround = HeightAboveGround(meet);
                if (heightAboveGround < altMin || heightAboveGround > req.maxAltitude) continue;

                if (tMeet < bestMeet)
                {
                    bestMeet = tMeet;
                    bestIdx = i;
                    bestLaunch = tLaunch;
                }
            }

            if (bestIdx >= 0)
            {
                WeaponInfo info = defenders[bestIdx].Station.WeaponInfo;
                s_freeAt[bestIdx] = bestLaunch + Mathf.Max(0.1f, info.fireInterval);
                s_ammoLeft[bestIdx]--;
                reason = string.Empty;
                return true;
            }
            // A station that only missed the deadline names the reason; otherwise the envelope (speed, range, altitude)
            reason = !anyAmmo ? "no ammo" : (anyLate ? "too late" : "out of envelope");
            return false;
        }

        /// <summary>
        /// A faction interceptor already locked to this ARM that closes to it before the deadline.
        /// </summary>
        private static bool InterceptorInFlight(Unit owner, Missile arm, Vector3 armPos, Vector3 armVel, float deadline)
        {
            if (deadline <= 0.0f) return false;
            FactionHQ hq = DatalinkNetwork.GetUnitFactionHQ(owner);
            List<Missile> missiles = DatalinkNetwork.ActiveMissiles;
            for (int i = 0; i < missiles.Count; i++)
            {
                Missile m = missiles[i];
                if (m == null || m.disabled || m == arm) continue;
                if (hq != null && m.NetworkHQ != hq) continue;
                if (m.targetID != arm.persistentID) continue;

                Vector3 intPos = m.transform.position;
                Vector3 intVel = m.rb != null ? m.rb.velocity : Vector3.zero;
                Vector3 rel = armPos - intPos;
                Vector3 relVel = armVel - intVel;
                float v2 = relVel.sqrMagnitude;
                if (v2 < 1.0f) continue;
                float tCa = -Vector3.Dot(rel, relVel) / v2;
                if (tCa <= 0.0f || tCa > deadline) continue;
                float miss = (rel + relVel * tCa).magnitude;
                if (miss <= InFlightClosureThreshold) return true;
            }
            return false;
        }

        private static float LaunchDelay(Defender d, WeaponStation station, Vector3 launcher, Vector3 armPos,
            float nowLevel, bool detected)
        {
            float tReady = 0.0f;
            if (!station.Ready())
            {
                WeaponInfo info = station.WeaponInfo;
                float sinceFired = nowLevel - station.LastFiredTime;
                tReady = Mathf.Max(0.0f, info.fireInterval - sinceFired);
            }

            // Vanilla Turret_OnInitialize already scales lockTime (/ skill) and traverseRate (x skill) by the crew skill
            float tLock = f_lockTime != null && d.Turret != null ? f_lockTime(d.Turret) : 0.0f;

            float tTraverse = 0.0f;
            if (d.Turret != null && f_traverseRate != null)
            {
                float rate = f_traverseRate(d.Turret);
                if (rate > 0.1f)
                {
                    Vector3 toArm = armPos - launcher;
                    // Vanilla GetDirection returns an aim point (barrel position + forward x 10 km), not a direction
                    Vector3 facing = d.Turret.GetDirection() - launcher;
                    if (toArm.sqrMagnitude > 1.0f && facing.sqrMagnitude > 0.01f)
                    {
                        float angle = Vector3.Angle(facing, toArm);
                        tTraverse = angle / rate;
                    }
                }
            }

            float tDetect = detected ? 0.0f : DetectionLagSeconds;
            return tReady + tLock + tTraverse + tDetect;
        }

        /// <summary>
        /// First time t (from now) at which an interceptor launched at tLaunch from the launcher and flying at vAvg
        /// reaches the ARM straight path P(t) = armPos + armVel t. False when it never catches up.
        /// </summary>
        private static bool SolveFlyout(Vector3 armPos, Vector3 armVel, Vector3 launcher, float vAvg, float tLaunch,
            out float tMeet)
        {
            tMeet = 0.0f;
            Vector3 d0 = armPos - launcher;
            // |d0 + armVel t|^2 = vAvg^2 (t - tLaunch)^2
            float a = armVel.sqrMagnitude - vAvg * vAvg;
            float b = 2.0f * (Vector3.Dot(d0, armVel) + vAvg * vAvg * tLaunch);
            float c = d0.sqrMagnitude - vAvg * vAvg * tLaunch * tLaunch;

            if (Mathf.Abs(a) < 1e-4f)
            {
                if (Mathf.Abs(b) < 1e-4f) return false;
                float t = -c / b;
                if (t < tLaunch) return false;
                tMeet = t;
                return true;
            }

            float disc = b * b - 4.0f * a * c;
            if (disc < 0.0f) return false;
            float sq = Mathf.Sqrt(disc);
            float t1 = (-b - sq) / (2.0f * a);
            float t2 = (-b + sq) / (2.0f * a);
            float lo = Mathf.Min(t1, t2);
            float hi = Mathf.Max(t1, t2);

            float best = float.MaxValue;
            if (lo >= tLaunch) best = lo;
            else if (hi >= tLaunch) best = hi;
            if (best == float.MaxValue) return false;
            tMeet = best;
            return true;
        }

        private static float HeightAboveGround(Vector3 point)
        {
            RaycastHit hit;
            if (PathfindingAgent.RaycastTerrain(point, out hit)) return point.y - hit.point.y;
            return point.y;
        }

        private static List<Defender> GetDefenders(Radar radar, Unit owner)
        {
            PruneCache();
            DefenderCache cache;
            if (!s_cache.TryGetValue(radar, out cache))
            {
                cache = new DefenderCache();
                s_cache[radar] = cache;
            }
            float now = Time.time;
            if (cache.BuiltAt >= 0.0f && now - cache.BuiltAt < DefenderCacheSeconds) return cache.Defenders;

            cache.Defenders.Clear();
            cache.BuiltAt = now;
            AddStations(owner, cache.Defenders, Vector3.zero, false);

            List<Unit> units = UnitRegistry.allUnits;
            if (units != null)
            {
                for (int i = 0; i < units.Count; i++)
                {
                    Unit u = units[i];
                    if (u == null || u.disabled || u == owner || u is Missile) continue;
                    if (!ReferenceEquals(u.radar, radar)) continue;
                    AddStations(u, cache.Defenders, Vector3.zero, false);
                }

                // ADR-84: air defense of the same faction nearby helps (a SAM vehicle next to the radar truck that the ARM
                // was launched at). Its own radar must transmit, otherwise its turrets do not see the ARM.
                FactionHQ hq = DatalinkNetwork.GetUnitFactionHQ(owner);
                Vector3 radarPos = radar.transform.position;
                if (hq != null)
                {
                    for (int i = 0; i < units.Count; i++)
                    {
                        Unit u = units[i];
                        if (u == null || u.disabled || u == owner || u is Missile || u is Aircraft) continue;
                        if (ReferenceEquals(u.radar, radar)) continue;
                        Radar own = u.radar as Radar;
                        if (own == null || !own.IsOperational() || !own.activated) continue;
                        if (DatalinkNetwork.GetUnitFactionHQ(u) != hq) continue;
                        AddStations(u, cache.Defenders, radarPos, true);
                    }
                }
            }
            return cache.Defenders;
        }

        private static void AddStations(Unit unit, List<Defender> into, Vector3 radarPos, bool rangeGate)
        {
            if (unit == null || unit.weaponStations == null) return;
            for (int i = 0; i < unit.weaponStations.Count; i++)
            {
                WeaponStation station = unit.weaponStations[i];
                if (station == null || station.WeaponInfo == null) continue;
                WeaponInfo info = station.WeaponInfo;
                if (!info.missile && !info.gun) continue;
                if (info.effectiveness.antiMissile <= 0.0f) continue;
                if (!station.HasTurret()) continue;
                if (rangeGate)
                {
                    Turret turret = station.GetTurret();
                    Vector3 at = turret != null ? turret.transform.position : unit.transform.position;
                    float reach = info.targetRequirements.maxRange;
                    if (reach <= 0.0f || (at - radarPos).sqrMagnitude > reach * reach) continue;
                }
                Defender d;
                d.Station = station;
                d.Turret = station.GetTurret();
                into.Add(d);
            }
        }

        private static void PruneCache()
        {
            if (s_cache.Count == 0) return;
            s_remove.Clear();
            foreach (KeyValuePair<Radar, DefenderCache> kv in s_cache)
            {
                if (kv.Key == null) s_remove.Add(kv.Key);
            }
            for (int i = 0; i < s_remove.Count; i++) s_cache.Remove(s_remove[i]);
            s_remove.Clear();
        }
    }
}
