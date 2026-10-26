using System;
using System.Collections.Generic;
using UnityEngine;
using CnoEvil.Config;

namespace CnoEvil.Core
{
    /// <summary>
    /// Broadside RCS of ships (m², X band, median return near the beam aspect). The game gives every hull
    /// radarSize 1.0, so there is no game value; the vanilla hulls use a real-world analog each, other hulls
    /// the empirical ship model sigma = 52 * sqrt(f_MHz) * D_kt^1.5 (Skolnik) with the displacement from the hull box.
    /// </summary>
    public static class ShipRcsDatabase
    {
        // Skolnik ship model at 10 GHz: 52 * sqrt(10000)
        private const float SkolnikXBandFactor = 5200.0f;

        // Displacement (kt) per m³ of the L x W x H hull box; matches the analogs of the vanilla hulls within ~20 %
        private const float DisplacementPerBoxVolumeKt = 0.047f / 1000.0f;

        private const float MinShipRcs = 10.0f;

        // Key: exact UnitDefinition.jsonKey
        private static readonly Dictionary<string, float> s_byJsonKey =
            new Dictionary<string, float>(StringComparer.Ordinal)
        {
            // Surf Class Patrol Boat (30 m): Mk VI / Island class patrol boat, ~0.1 kt
            { "PatrolBoat1", 160.0f },
            // OTB-31 landing craft (26 m): LCAC, ~0.18 kt
            { "LandingCraft1", 400.0f },
            // Shard Class Corvette (76 m, stealth): Visby, 0.64 kt; conventional ~2700 m², -99 % by shaping and composites
            { "Corvette1", 25.0f },
            // Argus Class Frigate (112 m): conventional frigate ~3 kt (MEKO 200, Kortenaer class)
            { "Frigate1", 27000.0f },
            // Cursor Class LFD (96 m): small amphibious dock ship ~4.5 kt
            { "SmallCarrier1", 50000.0f },
            // Dynamo Class Destroyer (162 m, stealth, Zumwalt-inspired): Arleigh Burke (9 kt, ~140000 m²) / 50
            { "Destroyer1", 2800.0f },
            // Annex Class Carrier (242 m): America class LHA, ~40-45 kt
            { "AssaultCarrier1", 1300000.0f },
            // Hyperion Class Carrier (267 m): Charles de Gaulle, 42 kt
            { "FleetCarrier1", 1400000.0f },
        };

        private static readonly HashSet<string> s_logged = new HashSet<string>(StringComparer.Ordinal);

        public static void ClearAll()
        {
            s_logged.Clear();
        }

        public static float GetBroadsideRcs(UnitDefinition def)
        {
            string key = def != null ? def.jsonKey : null;
            float rcs;
            string source;
            if (!string.IsNullOrEmpty(key) && s_byJsonKey.TryGetValue(key, out rcs))
            {
                source = "analog table";
            }
            else
            {
                float volume = (def != null) ? Mathf.Max(0.0f, def.length) * Mathf.Max(0.0f, def.width) * Mathf.Max(0.0f, def.height) : 0.0f;
                float displacementKt = volume * DisplacementPerBoxVolumeKt;
                rcs = Mathf.Max(MinShipRcs, SkolnikXBandFactor * Mathf.Pow(displacementKt, 1.5f));
                source = string.Format("Skolnik, {0:F2} kt from hull box", displacementKt);
            }

            if (CnoEvilConfig.DebugLogging.Value && CnoEvilPlugin.Log != null &&
                s_logged.Add(key ?? "-"))
            {
                CnoEvilPlugin.Log.LogInfo(string.Format("[CNO-EVIL] RCS ship {0}: {1:F0} m2 ({2})", key ?? "-", rcs, source));
            }
            return rcs;
        }
    }
}
