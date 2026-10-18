using System;
using System.Collections.Generic;

namespace CnoEvil.Core
{
    /// <summary>
    /// Hand-set classes for units whose game data gives the wrong result (mostly units of other mods).
    /// The key is the exact UnitDefinition.jsonKey; there is no substring match.
    /// </summary>
    public struct PlatformOverride
    {
        public RadarTemplate Radar;       // FromType: radar hardware from the unit type
        public float RadarRangeMeters;    // Reference range on 1 m²; 0: vanilla RadarParams (maxRange / minSignal)
        public int RwrTier;               // 1..3; 0: RWR tier from the unit definition

        public PlatformOverride(RadarTemplate radar, float radarRangeMeters, int rwrTier)
        {
            Radar = radar;
            RadarRangeMeters = radarRangeMeters;
            RwrTier = rwrTier;
        }
    }

    public static class PlatformOverrides
    {
        // Add one line per unit, for example:
        // { "Aryx_F22E_StrikeRaptor", new PlatformOverride(RadarTemplate.Fighter5thGenAESA, 110000.0f, 3) },
        // The jsonKey of each unit is in the BepInEx log when DebugLogging is on ("[CNO-EVIL] Radar ...").
        private static readonly Dictionary<string, PlatformOverride> s_byJsonKey =
            new Dictionary<string, PlatformOverride>(StringComparer.Ordinal)
        {
        };

        public static bool TryGet(Unit unit, out PlatformOverride entry)
        {
            entry = new PlatformOverride();
            if (s_byJsonKey.Count == 0 || unit == null || unit.definition == null) return false;
            string key = unit.definition.jsonKey;
            return !string.IsNullOrEmpty(key) && s_byJsonKey.TryGetValue(key, out entry);
        }
    }
}
