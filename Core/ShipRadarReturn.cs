using System.Collections.Generic;
using UnityEngine;

namespace CnoEvil.Core
{
    /// <summary>
    /// Radar return of a ship. Vanilla IRadarReturn is only on Aircraft and Missile, so Radar.RadarCheck never
    /// requests a ship. DetectionRequest takes the target unit and its IRadarReturn separately: Radar_RadarCheck_Patch
    /// queues ships with this proxy, and Radar_CanSeeRadarReturn_Patch evaluates them with the mod physics.
    /// </summary>
    public sealed class ShipRadarReturn : IRadarReturn
    {
        private static readonly List<ShipRadarReturn> s_all = new List<ShipRadarReturn>();
        private static readonly Dictionary<Ship, ShipRadarReturn> s_byShip = new Dictionary<Ship, ShipRadarReturn>();

        public readonly Ship Ship;

        // Height of the highest collider point above the ship transform (masts and radar arrays)
        public readonly float TopOffset;

        private ShipRadarReturn(Ship ship)
        {
            Ship = ship;
            TopOffset = MeasureTopOffset(ship);
        }

        public static List<ShipRadarReturn> All
        {
            get { return s_all; }
        }

        public static void Register(Ship ship)
        {
            if (ship == null || s_byShip.ContainsKey(ship)) return;
            ShipRadarReturn entry = new ShipRadarReturn(ship);
            s_byShip[ship] = entry;
            s_all.Add(entry);
        }

        public static void Unregister(Unit unit)
        {
            Ship ship = unit as Ship;
            ShipRadarReturn entry;
            if (ship == null || !s_byShip.TryGetValue(ship, out entry)) return;
            s_byShip.Remove(ship);
            s_all.Remove(entry);
        }

        /// <summary>
        /// Registers the ships that already exist (scene loaded after their InitializeUnit).
        /// </summary>
        public static void CensusScene()
        {
            if (UnitRegistry.allUnits == null) return;
            for (int i = 0; i < UnitRegistry.allUnits.Count; i++)
            {
                Ship ship = UnitRegistry.allUnits[i] as Ship;
                if (ship != null) Register(ship);
            }
        }

        public static void ClearAll()
        {
            s_all.Clear();
            s_byShip.Clear();
        }

        /// <summary>
        /// Highest point of the ship (top of the superstructure): the first part a radar sees over the horizon.
        /// </summary>
        public Vector3 GetAimPoint()
        {
            Vector3 p = Ship.transform.position;
            p.y += TopOffset;
            return p;
        }

        private static float MeasureTopOffset(Ship ship)
        {
            float height = (ship.definition != null) ? ship.definition.height : 0.0f;
            float baseY = ship.transform.position.y;
            float top = float.MinValue;
            Collider[] colliders = ship.GetComponentsInChildren<Collider>(true);
            for (int i = 0; i < colliders.Length; i++)
            {
                if (colliders[i] != null) top = Mathf.Max(top, colliders[i].bounds.max.y);
            }

            if (top == float.MinValue) return Mathf.Max(1.0f, height);
            float offset = top - baseY;
            if (height > 0.0f) offset = Mathf.Min(offset, height);
            return Mathf.Max(1.0f, offset);
        }

        public float GetRadarReturn(Vector3 source, Radar radar, Unit emitter, float dist, float clutterFactor, RadarParams radarParams, bool triggerWarning)
        {
            // Reached only when the mod is turned off between the request and the result: vanilla has no ship returns
            return 0.0f;
        }

        public float GetECMIntensity()
        {
            return 0.0f;
        }
    }
}
