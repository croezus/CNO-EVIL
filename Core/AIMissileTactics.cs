using System.Collections.Generic;
using UnityEngine;
using CnoEvil.Config;
using CnoEvil.Patches;

namespace CnoEvil.Core
{
    /// <summary>
    /// AI tactics against radar missiles and with its own radar missiles (ADR-31), on the server where the AI runs:
    /// drag (tail to a far missile), turn away from the own chaff cloud or decoy after an ejection, and crank (turn off
    /// the target while the own ARH or SARH missile flies). The pilot uses only what it knows: the warned missile, its
    /// own clouds and its own missiles. It does not know whether a seeker is captured.
    /// </summary>
    public static class AIMissileTactics
    {
        private const float DragHysteresis_s = 4.0f;

        private struct DragState
        {
            public int MissileId;
            public bool Dragging;
        }

        private static readonly Dictionary<int, DragState> s_drag = new Dictionary<int, DragState>();
        // Debug log state: last cloud turned away from, aircraft that crank
        private static readonly Dictionary<int, int> s_turnAwayCloud = new Dictionary<int, int>();
        private static readonly HashSet<int> s_cranking = new HashSet<int>();
        // Own ARH and SARH missiles in flight that were fired by aircraft (Missile_StartMissile_Patch)
        private static readonly List<Missile> s_ownMissiles = new List<Missile>();

        /// <summary>
        /// Drag decision for the warned missile, with hysteresis: starts above AIDragMinImpactTime_s, stops below it
        /// minus 4 s. A new missile starts a new decision.
        /// </summary>
        public static bool UpdateDrag(Aircraft aircraft, Missile missile, float impactTime)
        {
            if (aircraft == null || missile == null || !CnoEvilConfig.AIDragEnabled.Value) return false;
            int key = aircraft.GetInstanceID();
            int missileId = missile.GetInstanceID();
            float start = CnoEvilConfig.AIDragMinImpactTime_s.Value;

            DragState state;
            bool known = s_drag.TryGetValue(key, out state) && state.MissileId == missileId;
            bool dragging = known && state.Dragging ? impactTime > start - DragHysteresis_s : impactTime > start;
            if (!known || dragging != state.Dragging)
            {
                if (CnoEvilConfig.DebugLogging.Value && CnoEvilPlugin.Log != null && (known || dragging))
                {
                    CnoEvilPlugin.Log.LogInfo(string.Format("AI {0} {1} drag of missile {2}, impact in {3:F0} s",
                        aircraft.unitName, dragging ? "starts" : "stops", missileId, impactTime));
                }
                state.MissileId = missileId;
                state.Dragging = dragging;
                s_drag[key] = state;
            }
            return dragging;
        }

        /// <summary>
        /// The newest chaff cloud or decoy of this aircraft that is still alive and younger than AITurnAwayWindow_s.
        /// </summary>
        public static ChaffCloud RecentOwnCloud(Aircraft aircraft)
        {
            if (aircraft == null || !CnoEvilConfig.AITurnAwayFromDecoy.Value || !ChaffCloudRegistry.Any) return null;
            float now = Time.timeSinceLevelLoad;
            float window = CnoEvilConfig.AITurnAwayWindow_s.Value;
            ChaffCloud best = null;
            List<ChaffCloud> clouds = ChaffCloudRegistry.Clouds;
            for (int i = 0; i < clouds.Count; i++)
            {
                ChaffCloud c = clouds[i];
                if (c.Owner != aircraft || !c.Alive || now - c.LaunchTime > window) continue;
                if (best == null || c.LaunchTime > best.LaunchTime) best = c;
            }
            return best;
        }

        /// <summary>
        /// Missile_StartMissile_Patch: keeps an ARH or SARH missile that an aircraft fired, for the crank of its owner.
        /// </summary>
        public static void RegisterLaunch(Missile missile)
        {
            if (missile == null || !(missile.owner is Aircraft)) return;
            string seeker = missile.GetSeekerType();
            if (seeker != "ARH" && seeker != "SARH") return;
            if (!s_ownMissiles.Contains(missile)) s_ownMissiles.Add(missile);
        }

        /// <summary>
        /// True while this aircraft has a missile in flight at this target that it must support: ARH until its seeker
        /// locks, SARH for the full flight.
        /// </summary>
        public static bool HasMissileToSupport(Aircraft aircraft, Unit target)
        {
            if (aircraft == null || target == null || s_ownMissiles.Count == 0) return false;
            for (int i = s_ownMissiles.Count - 1; i >= 0; i--)
            {
                Missile m = s_ownMissiles[i];
                if (m == null || m.disabled)
                {
                    s_ownMissiles.RemoveAt(i);
                    continue;
                }
                if (m.owner != aircraft || SeekerDispersionHelper.GetTargetForMissile(m) != target) continue;
                if (m.GetSeekerType() == "SARH" || !SeekerDispersionHelper.IsSeekerTerminalLocked(m)) return true;
            }
            return false;
        }

        /// <summary>
        /// Crank point: the target point turned horizontally around the aircraft by AICrankAngle_deg, to the side where
        /// the nose already is. The height of the target point stays.
        /// </summary>
        public static Vector3 CrankPoint(Vector3 aircraftPos, Vector3 forward, Vector3 targetPoint)
        {
            Vector3 toTarget = targetPoint - aircraftPos;
            Vector3 flat = new Vector3(toTarget.x, 0.0f, toTarget.z);
            if (flat.sqrMagnitude < 1.0f) return targetPoint;
            Vector3 right = Vector3.Cross(Vector3.up, flat);
            float side = Vector3.Dot(right, forward) >= 0.0f ? 1.0f : -1.0f;
            Vector3 turned = Quaternion.AngleAxis(side * CnoEvilConfig.AICrankAngle_deg.Value, Vector3.up) * flat;
            return aircraftPos + turned + Vector3.up * toTarget.y;
        }

        /// <summary>
        /// Debug log once per cloud that the aircraft turns away from.
        /// </summary>
        public static void LogTurnAway(Aircraft aircraft, ChaffCloud cloud)
        {
            if (!CnoEvilConfig.DebugLogging.Value || CnoEvilPlugin.Log == null) return;
            int key = aircraft.GetInstanceID();
            int last;
            if (s_turnAwayCloud.TryGetValue(key, out last) && last == cloud.Id) return;
            s_turnAwayCloud[key] = cloud.Id;
            CnoEvilPlugin.Log.LogInfo(string.Format("AI {0} turns away from its {1} {2}", aircraft.unitName,
                cloud.IsDecoy ? "decoy" : "chaff", cloud.Id));
        }

        /// <summary>
        /// Crank state of the aircraft; debug log on a change.
        /// </summary>
        public static void SetCranking(Aircraft aircraft, bool cranking)
        {
            int key = aircraft.GetInstanceID();
            if (s_cranking.Contains(key) == cranking) return;
            if (cranking) s_cranking.Add(key);
            else s_cranking.Remove(key);
            if (CnoEvilConfig.DebugLogging.Value && CnoEvilPlugin.Log != null)
            {
                CnoEvilPlugin.Log.LogInfo(string.Format("AI {0} crank {1}", aircraft.unitName, cranking ? "on" : "off"));
            }
        }

        public static void ForgetAircraft(int aircraftId)
        {
            s_drag.Remove(aircraftId);
            s_turnAwayCloud.Remove(aircraftId);
            s_cranking.Remove(aircraftId);
        }

        public static void ForgetMissile(Missile missile)
        {
            s_ownMissiles.Remove(missile);
        }

        public static void ClearAll()
        {
            s_drag.Clear();
            s_turnAwayCloud.Clear();
            s_cranking.Clear();
            s_ownMissiles.Clear();
        }
    }
}
