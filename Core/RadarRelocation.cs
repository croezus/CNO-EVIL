using System;
using System.Collections.Generic;
using UnityEngine;
using CnoEvil.Config;

namespace CnoEvil.Core
{
    /// <summary>
    /// A mobile AI radar vehicle drives to a new site when RadarEmcon switches its radar off against an anti-radiation
    /// missile (ADR-78). The ARM that loses the emitter flies to its datum (the old position plus the launch miss,
    /// ARMSeeker_EmitterLoss_Patch); the vehicle is no longer there.
    /// - Eligible: an AI GroundVehicle with the vanilla `mobile` flag, no player command, not on a bridge. Ships, buildings
    ///   and fixed vehicles stay (they only go silent).
    /// - Site: AIRadarRelocateMinDistance_m to AIRadarRelocateMaxDistance_m away, ground rules of the mission plan
    ///   (DynamicPlacement.SiteProbe), a drivable straight path (vanilla NavigateOffRoad rules: 20 m steps, less than 3 m
    ///   height step), not toward the missile. The score prefers a site to the side of the missile path and with an open
    ///   horizon. Launchers linked to this radar (Unit.radar, vanilla link 300 m) keep the link when a site allows it.
    /// - Drive: after a pack-up delay, vanilla UnitCommand.SetDestination (resets the stationary state, host authority,
    ///   the position goes to clients with GroundVehicleNetworkTransform) and PathfindingAgent.Shortcut (one straight
    ///   waypoint, no road route). holdPosition is set for the drive so that vanilla CheckObstacles does not send the
    ///   vehicle to the objectives, and restored at the end.
    /// Vanilla deployable parts stay vanilla: a radar on a mast (HLT-R) is stowed while the vehicle moves.
    /// Host only.
    /// </summary>
    public static class RadarRelocation
    {
        private const float TickInterval = 0.5f;
        private const float MinPackUpSeconds = 2.0f;
        private const float MaxPackUpSeconds = 5.0f;
        private const float CooldownSeconds = 20.0f;
        private const float ArrivalMeters = 25.0f;
        private const float ArrivalSpeed = 1.0f;
        private const float MinDriveSeconds = 3.0f;
        private const float DeadlineMarginSeconds = 15.0f;
        private const float DefaultOffroadKmh = 30.0f;
        private const int Azimuths = 16;
        private const int DistanceSteps = 3;
        // A site in this cone toward the launch side of the missile is not used (cos 45 deg)
        private const float TowardMissileCos = 0.7f;
        private const float PathStepMeters = 20.0f;
        // Vanilla GroundVehicle.NavigateOffRoad: height step under 3 m for each probe step
        private const float MaxPathStepRise = 3.0f;
        private const float MinPathNormalY = 0.85f;
        private const float PathClearanceMeters = 1.5f;
        private const float VehicleRadiusMeters = 6.0f;
        private const float MinSensorHeight = 3.0f;
        private const float HorizonRange = 15000.0f;
        // Vanilla Turret.SearchForRadar links a radar inside 300 m; keep a margin
        private const float LinkSearchMeters = 300.0f;
        private const float LinkKeepMeters = 280.0f;

        private enum Phase
        {
            PackUp,
            Driving
        }

        private sealed class State
        {
            public GroundVehicle Vehicle;
            public GlobalPosition Site;
            public float Distance;
            public Phase Phase;
            public float StartAt;
            public float DriveSince;
            public float Deadline;
            public bool OldHoldPosition;
        }

        private struct Site
        {
            public Vector3 Ground;
            public float Distance;
            public float Score;
            public bool LinkLost;
        }

        private static readonly Func<GroundVehicle, bool> f_mobile = FastReflection.CreateFieldGetter<GroundVehicle, bool>("mobile");
        private static readonly Func<GroundVehicle, bool> f_commanded = FastReflection.CreateFieldGetter<GroundVehicle, bool>("commandedDestination");
        private static readonly Func<GroundVehicle, PathfindingAgent> f_pathfinder = FastReflection.CreateFieldGetter<GroundVehicle, PathfindingAgent>("pathfinder");
        private static readonly Func<GroundVehicle, float> f_offroadSpeed = FastReflection.CreateFieldGetter<GroundVehicle, float>("topSpeedOffroad");

        private static readonly Dictionary<GroundVehicle, State> s_states = new Dictionary<GroundVehicle, State>();
        private static readonly Dictionary<GroundVehicle, float> s_nextAllowed = new Dictionary<GroundVehicle, float>();
        private static readonly List<GroundVehicle> s_remove = new List<GroundVehicle>();
        private static readonly List<Vector3> s_launchers = new List<Vector3>();
        private static float s_nextTick;

        private static bool IsRunning
        {
            get
            {
                return CnoEvilConfig.IsModActive
                    && CnoEvilConfig.AIRadarShutdownEnabled != null && CnoEvilConfig.AIRadarShutdownEnabled.Value
                    && CnoEvilConfig.AIRadarRelocateEnabled != null && CnoEvilConfig.AIRadarRelocateEnabled.Value;
            }
        }

        /// <summary>
        /// True when RadarRelocation would drive this surface unit away now (option on, mobile AI vehicle, no player
        /// command, not on a bridge). RadarEmcon uses it to pick the larger intercept reserve, so a vehicle keeps the
        /// time to pack up and leave; a vehicle that stays gets the fixed reserve.
        /// </summary>
        public static bool WillRelocate(Unit owner)
        {
            if (!IsRunning) return false;
            GroundVehicle vehicle = owner as GroundVehicle;
            PathfindingAgent pathfinder;
            return CanDrive(vehicle, out pathfinder);
        }

        // An AI ground vehicle that vanilla lets move and that has no player command
        private static bool CanDrive(GroundVehicle vehicle, out PathfindingAgent pathfinder)
        {
            pathfinder = null;
            if (vehicle == null || vehicle.disabled || vehicle.UnitCommand == null) return false;
            if (f_mobile == null || f_pathfinder == null || !f_mobile(vehicle)) return false;
            if (f_commanded != null && f_commanded(vehicle)) return false;
            pathfinder = f_pathfinder(vehicle);
            return pathfinder != null && !pathfinder.IsOnBridge();
        }

        /// <summary>
        /// Called by RadarEmcon when it switches the radar of a surface unit off. armPos / armVel: the known missile.
        /// </summary>
        public static void TryStart(Radar radar, Unit owner, Vector3 armPos, Vector3 armVel)
        {
            try
            {
                if (CnoEvilNet.IsMirrorClient || !IsRunning) return;
                GroundVehicle vehicle = owner as GroundVehicle;
                if (vehicle == null || s_states.ContainsKey(vehicle)) return;
                PathfindingAgent pathfinder;
                if (!CanDrive(vehicle, out pathfinder)) return;

                float now = Time.time;
                PruneCooldowns();
                float allowed;
                if (s_nextAllowed.TryGetValue(vehicle, out allowed) && now < allowed) return;

                Site site;
                if (!TryPickSite(radar, vehicle, armPos, armVel, out site))
                {
                    s_nextAllowed[vehicle] = now + CooldownSeconds;
                    if (EvilLog.On(LogCat.AI)) EvilLog.Write(LogCat.AI, "EMCON {0}: no relocation site, stays silent in place", EvilLog.Name(vehicle));
                    return;
                }

                State state = new State();
                state.Vehicle = vehicle;
                state.Site = GlobalPositionExtensions.ToGlobalPosition(site.Ground);
                state.Distance = site.Distance;
                state.Phase = Phase.PackUp;
                state.StartAt = now + UnityEngine.Random.Range(MinPackUpSeconds, MaxPackUpSeconds);
                s_states[vehicle] = state;

                if (EvilLog.On(LogCat.AI))
                {
                    Vector3 d = site.Ground - vehicle.transform.position;
                    float bearing = Mathf.Repeat(Mathf.Atan2(d.x, d.z) * Mathf.Rad2Deg, 360.0f);
                    EvilLog.Write(LogCat.AI, "EMCON {0}: relocate {1:F0} m bearing {2:F0}, score {3:F2}{4}, moves in {5:F1} s",
                        EvilLog.Name(vehicle), site.Distance, bearing, site.Score, site.LinkLost ? " (launcher link lost)" : "",
                        state.StartAt - now);
                }
            }
            catch (Exception ex)
            {
                PatchErrorLog.Once("RadarRelocation.TryStart", ex);
            }
        }

        /// <summary>
        /// Called every frame from the plugin, also while the mod is off (to give the vehicles back to vanilla).
        /// </summary>
        public static void Update()
        {
            try
            {
                if (CnoEvilNet.IsMirrorClient) return;
                if (!IsRunning)
                {
                    if (s_states.Count > 0) RestoreAll();
                    return;
                }
                if (s_states.Count == 0) return;

                float now = Time.time;
                if (now < s_nextTick) return;
                s_nextTick = now + TickInterval;

                s_remove.Clear();
                foreach (KeyValuePair<GroundVehicle, State> kv in s_states)
                {
                    State st = kv.Value;
                    if (kv.Key == null || st.Vehicle == null || st.Vehicle.disabled)
                    {
                        s_remove.Add(kv.Key);
                        continue;
                    }
                    if (st.Phase == Phase.PackUp)
                    {
                        if (now >= st.StartAt && !Drive(st, now))
                        {
                            // A player took the unit or it is on a bridge: no new hop until the cooldown ends
                            s_nextAllowed[st.Vehicle] = now + CooldownSeconds;
                            s_remove.Add(kv.Key);
                        }
                        continue;
                    }
                    if (Step(st, now)) s_remove.Add(kv.Key);
                }
                for (int i = 0; i < s_remove.Count; i++) s_states.Remove(s_remove[i]);
                s_remove.Clear();
            }
            catch (Exception ex)
            {
                PatchErrorLog.Once("RadarRelocation.Update", ex);
            }
        }

        private static bool Drive(State st, float now)
        {
            GroundVehicle vehicle = st.Vehicle;
            PathfindingAgent pathfinder;
            if (!CanDrive(vehicle, out pathfinder)) return false;
            UnitCommand command = vehicle.UnitCommand;

            st.OldHoldPosition = vehicle.GetHoldPosition();
            vehicle.SetHoldPosition(true);
            // Vanilla path: anchored off, destination, stationary reset. Then one straight waypoint instead of a road route.
            command.SetDestination(st.Site, false);
            pathfinder.Shortcut(st.Site);

            float speedKmh = f_offroadSpeed != null ? f_offroadSpeed(vehicle) : 0.0f;
            if (speedKmh < 5.0f) speedKmh = DefaultOffroadKmh;
            st.Phase = Phase.Driving;
            st.DriveSince = now;
            st.Deadline = now + 2.0f * st.Distance / (speedKmh / 3.6f) + DeadlineMarginSeconds;
            return true;
        }

        // True when the drive is over
        private static bool Step(State st, float now)
        {
            GroundVehicle vehicle = st.Vehicle;
            if (f_commanded != null && f_commanded(vehicle))
            {
                // A player took the unit: the player's command stays
                vehicle.SetHoldPosition(st.OldHoldPosition);
                Finish(st, now, "player command");
                return true;
            }
            float flat = FlatDistance(vehicle.transform.position, st.Site.ToLocalPosition());
            bool arrived = flat < ArrivalMeters && Mathf.Abs(vehicle.speed) < ArrivalSpeed && now - st.DriveSince > MinDriveSeconds;
            bool late = now >= st.Deadline;
            if (!arrived && !late) return false;

            if (late) vehicle.StopImmediately();
            vehicle.SetHoldPosition(st.OldHoldPosition);
            Finish(st, now, arrived ? "arrived" : "time limit, " + flat.ToString("F0") + " m short");
            return true;
        }

        private static void Finish(State st, float now, string why)
        {
            s_nextAllowed[st.Vehicle] = now + CooldownSeconds;
            if (EvilLog.On(LogCat.AI))
            {
                EvilLog.Write(LogCat.AI, "EMCON {0}: relocation end ({1}) after {2:F0} s", EvilLog.Name(st.Vehicle), why, now - st.DriveSince);
            }
        }

        private static bool TryPickSite(Radar radar, GroundVehicle vehicle, Vector3 armPos, Vector3 armVel, out Site best)
        {
            best = default(Site);
            float minD = Mathf.Max(20.0f, CnoEvilConfig.AIRadarRelocateMinDistance_m.Value);
            float maxD = Mathf.Max(minD, CnoEvilConfig.AIRadarRelocateMaxDistance_m.Value);

            DynamicPlacement.SiteProbe probe = new DynamicPlacement.SiteProbe();
            Vector3 origin = vehicle.transform.position;
            Vector3 start;
            if (!probe.TryGround(origin, out start))
            {
                // The vehicle can stand on a slope that a new site may not have; the path starts at its own position
                start = origin;
            }
            bool insideAirbase = probe.IsInsideAirbase(origin);

            // Direction of the missile flight, flat. A missile with no known velocity flies from its position at the radar.
            Vector3 armDir = new Vector3(armVel.x, 0.0f, armVel.z);
            if (armDir.sqrMagnitude < 1.0f) armDir = new Vector3(origin.x - armPos.x, 0.0f, origin.z - armPos.z);
            if (armDir.sqrMagnitude < 0.01f) armDir = Vector3.forward;
            armDir.Normalize();

            Transform scan = radar != null ? radar.GetScanPoint() : null;
            float sensorHeight = scan != null ? Mathf.Max(MinSensorHeight, scan.position.y - origin.y) : MinSensorHeight;

            s_launchers.Clear();
            List<Unit> units = UnitRegistry.allUnits;
            if (units != null && radar != null)
            {
                for (int i = 0; i < units.Count; i++)
                {
                    Unit u = units[i];
                    if (u == null || u.disabled || u == vehicle || !ReferenceEquals(u.radar, radar)) continue;
                    if (FlatDistance(u.transform.position, origin) <= LinkSearchMeters) s_launchers.Add(u.transform.position);
                }
            }

            bool found = false;
            float azimuthOffset = UnityEngine.Random.Range(0.0f, 360.0f / Azimuths);
            for (int di = 0; di < DistanceSteps; di++)
            {
                float d = DistanceSteps > 1 ? Mathf.Lerp(minD, maxD, (float)di / (DistanceSteps - 1)) : maxD;
                for (int ai = 0; ai < Azimuths; ai++)
                {
                    float angle = (azimuthOffset + ai * (360.0f / Azimuths)) * Mathf.Deg2Rad;
                    Vector3 dir = new Vector3(Mathf.Sin(angle), 0.0f, Mathf.Cos(angle));
                    // Not toward the side the missile comes from
                    if (Vector3.Dot(dir, -armDir) > TowardMissileCos) continue;

                    Vector3 ground;
                    if (!probe.TryGround(origin + dir * d, out ground)) continue;
                    if (probe.IsBlocked(ground, VehicleRadiusMeters, insideAirbase)) continue;
                    if (!IsDrivable(start, ground, probe.SeaY)) continue;

                    bool linkLost = false;
                    for (int i = 0; i < s_launchers.Count; i++)
                    {
                        if (FlatDistance(s_launchers[i], ground) > LinkKeepMeters)
                        {
                            linkLost = true;
                            break;
                        }
                    }

                    float lateral = Mathf.Abs(armDir.x * dir.z - armDir.z * dir.x);
                    float horizon = probe.HorizonCoverage(ground, sensorHeight, HorizonRange);
                    float score = lateral + horizon + 0.3f * (d / maxD) + UnityEngine.Random.Range(0.0f, 0.2f);
                    if (linkLost) score -= 2.0f;
                    if (!found || score > best.Score)
                    {
                        best.Ground = ground;
                        best.Distance = FlatDistance(origin, ground);
                        best.Score = score;
                        best.LinkLost = linkLost;
                        found = true;
                    }
                }
            }
            s_launchers.Clear();
            return found;
        }

        /// <summary>
        /// A straight path that a vehicle can drive: ground at each 20 m step, not too steep, above the sea, no step of
        /// 3 m or more, and nothing static between the steps.
        /// </summary>
        private static bool IsDrivable(Vector3 from, Vector3 to, float seaY)
        {
            Vector3 flat = new Vector3(to.x - from.x, 0.0f, to.z - from.z);
            float length = flat.magnitude;
            if (length < 1.0f) return false;
            int steps = Mathf.Max(1, Mathf.CeilToInt(length / PathStepMeters));
            Vector3 previous = from;
            int mask = (int)PhysicsLayers.StaticsMask;
            for (int i = 1; i <= steps; i++)
            {
                Vector3 probe = from + flat * ((float)i / steps);
                RaycastHit hit;
                if (!PathfindingAgent.RaycastTerrain(probe, out hit)) return false;
                if (hit.normal.y < MinPathNormalY || hit.point.y < seaY + 0.5f) return false;
                if (Mathf.Abs(hit.point.y - previous.y) >= MaxPathStepRise) return false;
                Vector3 up = Vector3.up * PathClearanceMeters;
                if (Physics.Linecast(previous + up, hit.point + up, mask)) return false;
                previous = hit.point;
            }
            return true;
        }

        // Destroyed vehicles leave the cooldown table (rare path: only when a relocation starts)
        private static void PruneCooldowns()
        {
            if (s_nextAllowed.Count == 0) return;
            s_remove.Clear();
            foreach (KeyValuePair<GroundVehicle, float> kv in s_nextAllowed)
            {
                if (kv.Key == null || kv.Key.disabled) s_remove.Add(kv.Key);
            }
            for (int i = 0; i < s_remove.Count; i++) s_nextAllowed.Remove(s_remove[i]);
            s_remove.Clear();
        }

        private static float FlatDistance(Vector3 a, Vector3 b)
        {
            float dx = a.x - b.x;
            float dz = a.z - b.z;
            return Mathf.Sqrt(dx * dx + dz * dz);
        }

        /// <summary>
        /// Gives every vehicle back to vanilla (mod or option off): hold position as before, the drive stops.
        /// </summary>
        public static void RestoreAll()
        {
            foreach (KeyValuePair<GroundVehicle, State> kv in s_states)
            {
                State st = kv.Value;
                if (st.Vehicle == null || st.Phase != Phase.Driving) continue;
                try
                {
                    st.Vehicle.StopImmediately();
                    st.Vehicle.SetHoldPosition(st.OldHoldPosition);
                }
                catch (Exception ex)
                {
                    PatchErrorLog.Once("RadarRelocation.RestoreAll", ex);
                }
            }
            s_states.Clear();
        }

        public static void ClearAll()
        {
            s_states.Clear();
            s_nextAllowed.Clear();
            s_remove.Clear();
            s_launchers.Clear();
            s_nextTick = 0.0f;
        }
    }
}
