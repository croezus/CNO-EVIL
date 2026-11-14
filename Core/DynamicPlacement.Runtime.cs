using System.Collections.Generic;
using UnityEngine;

namespace CnoEvil.Core
{
    /// <summary>
    /// The site checks of the mission plan for use during the mission (RadarRelocation, ADR-78).
    /// </summary>
    public static partial class DynamicPlacement
    {
        // The tree grids of the map, read once per scene (FindObjectsOfType is slow). GlobalPositions, so they stay valid.
        private static List<TreeGrid> s_runtimeTrees;

        /// <summary>
        /// One set of site checks. The airbase areas are local positions: make a new probe for each search.
        /// </summary>
        internal sealed class SiteProbe
        {
            private readonly Context m_ctx;

            internal SiteProbe()
            {
                m_ctx = new Context();
                m_ctx.SeaY = Datum.LocalSeaY;
                AddAirfields(m_ctx);
                if (s_runtimeTrees == null)
                {
                    Context trees = new Context();
                    LoadTrees(trees);
                    s_runtimeTrees = new List<TreeGrid>(trees.Trees);
                }
                m_ctx.Trees.AddRange(s_runtimeTrees);
            }

            /// <summary>
            /// The terrain point under this position, with the ground rules of the mission plan (slope, sea, no unit).
            /// </summary>
            internal bool TryGround(Vector3 local, out Vector3 ground)
            {
                return DynamicPlacement.TryGround(local, m_ctx, out ground);
            }

            /// <summary>
            /// True if a vehicle of this radius cannot stand here: trees, runways, and airbase areas (taxiways only, when
            /// the vehicle already stands inside an airbase).
            /// </summary>
            internal bool IsBlocked(Vector3 ground, float radius, bool insideAirbase)
            {
                m_ctx.AllowBaseInterior = insideAirbase;
                bool blocked = IsInAirbase(ground, m_ctx);
                m_ctx.AllowBaseInterior = false;
                return blocked || IsNearTree(ground, radius, m_ctx);
            }

            /// <summary>
            /// True if this position is inside the area of an airbase.
            /// </summary>
            internal bool IsInsideAirbase(Vector3 local)
            {
                for (int i = 0; i < m_ctx.Bases.Count; i++)
                {
                    BaseZone zone = m_ctx.Bases[i];
                    if (FlatSqr(zone.Center, local) < zone.Radius * zone.Radius) return true;
                }
                return false;
            }

            /// <summary>
            /// Part (0 to 1) of the directions with no terrain up to this range, from a sensor at this height.
            /// </summary>
            internal float HorizonCoverage(Vector3 ground, float sensorHeight, float range)
            {
                return DynamicPlacement.HorizonCoverage(ground, sensorHeight, range);
            }

            internal float SeaY
            {
                get { return m_ctx.SeaY; }
            }
        }
    }
}
