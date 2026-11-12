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
    /// R (Unit.radar == R, the vanilla 300 m turret-radar link). A station counts when it has an anti-missile weapon
    /// (WeaponInfo.effectiveness.antiMissile greater than 0), ammunition, and a turret.
    ///
    /// An intercept is expected in time when either an interceptor is already in flight at the ARM and closes to it
    /// before the deadline, or a defender station can launch and the interceptor flies out to a meet point inside the
    /// weapon envelope before the deadline. The deadline is the ARM time to go minus the caller reserve (pack-up time).
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
        /// True while the weapons of this radar can still shoot the ARM down before (ARM time to go - reserve) seconds.
        /// </summary>
        public static bool CanStillIntercept(Radar radar, Unit owner, Missile arm, Vector3 armPos, Vector3 armVel,
            float timeToGo, float reserve, out string why)
        {
            why = string.Empty;
            try
            {
                if (radar == null || owner == null || arm == null)
                {
                    why = "no defender";
                    return false;
                }

                float deadline = timeToGo - reserve;

                // An interceptor already committed to this ARM keeps the radar on (a SARH one needs it on to guide).
                if (InterceptorInFlight(owner, arm, armPos, armVel, deadline))
                {
                    why = "interceptor in flight";
                    return true;
                }

                if (deadline <= 0.0f)
                {
                    why = "too late";
                    return false;
                }

                List<Defender> defenders = GetDefenders(radar, owner);
                if (defenders.Count == 0)
                {
                    why = "no defender";
                    return false;
                }

                float armSpeed = armVel.magnitude;
                bool detected = radar.detectedTargets != null && radar.detectedTargets.Contains(arm);
                float nowLevel = Time.timeSinceLevelLoad;

                bool anyAmmo = false;
                bool anyLate = false;
                for (int i = 0; i < defenders.Count; i++)
                {
                    Defender d = defenders[i];
                    WeaponStation station = d.Station;
                    if (station == null || station.WeaponInfo == null || station.Ammo <= 0) continue;
                    // A station mid-reload has no reliable remaining time: skip it (conservative, the radar may go off
                    // a little early rather than claim an intercept that does not come).
                    if (station.Reloading) continue;
                    anyAmmo = true;

                    WeaponInfo info = station.WeaponInfo;
                    TargetRequirements req = info.targetRequirements;
                    if (armSpeed > req.maxSpeed && req.maxSpeed > 0.0f) continue;

                    Vector3 launcher = d.Turret != null ? d.Turret.transform.position : station.GetTurret() != null
                        ? station.GetTurret().transform.position : owner.transform.position;

                    float tLaunch = LaunchDelay(d, station, launcher, armPos, nowLevel, detected);
                    if (tLaunch > deadline)
                    {
                        anyLate = true;
                        continue;
                    }

                    float vAvg = info.gun ? info.muzzleVelocity : FlyoutSpeedFactor * info.GetMaxSpeed();
                    if (vAvg < 1.0f) continue;

                    float tMeet;
                    if (!SolveFlyout(armPos, armVel, launcher, vAvg, tLaunch, out tMeet) || tMeet > deadline)
                    {
                        anyLate = true;
                        continue;
                    }

                    Vector3 meet = armPos + armVel * tMeet;
                    float meetRange = (meet - launcher).magnitude;
                    if (meetRange < req.minRange || meetRange > req.maxRange) continue;

                    float altMin = req.maxRange > 0.0f ? req.minAltitude * meetRange / req.maxRange : req.minAltitude;
                    float heightAboveGround = HeightAboveGround(meet);
                    if (heightAboveGround < altMin || heightAboveGround > req.maxAltitude) continue;

                    return true;
                }

                // A station that only missed the deadline names the reason; otherwise the envelope (speed, range, altitude)
                why = !anyAmmo ? "no ammo" : (anyLate ? "too late" : "out of envelope");
                return false;
            }
            catch (Exception ex)
            {
                PatchErrorLog.Once("ArmInterceptAssessor.CanStillIntercept", ex);
                why = "error";
                return false;
            }
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
            AddStations(owner, cache.Defenders);

            List<Unit> units = UnitRegistry.allUnits;
            if (units != null)
            {
                for (int i = 0; i < units.Count; i++)
                {
                    Unit u = units[i];
                    if (u == null || u.disabled || u == owner || u is Missile) continue;
                    if (!ReferenceEquals(u.radar, radar)) continue;
                    AddStations(u, cache.Defenders);
                }
            }
            return cache.Defenders;
        }

        private static void AddStations(Unit unit, List<Defender> into)
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
