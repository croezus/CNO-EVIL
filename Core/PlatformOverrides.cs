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
        // { "<jsonKey>", new PlatformOverride(RadarTemplate.Fighter5thGenAESA, 110000.0f, 3) },
        // The jsonKey of each unit is in the session log when DebugLogging is on ("Radar ...").
        // No entries: the RWR tier comes from the aircraft rank (ADR-70), so the old tier entries of mod aircraft with
        // typeIdentity.radar = 0 (F-16M, F-22E, F-99, FS-41, MC-260, AH-31) are not necessary.
        private static readonly Dictionary<string, PlatformOverride> s_byJsonKey =
            new Dictionary<string, PlatformOverride>(StringComparer.Ordinal);

        public static bool TryGet(Unit unit, out PlatformOverride entry)
        {
            entry = new PlatformOverride();
            if (s_byJsonKey.Count == 0 || unit == null || unit.definition == null) return false;
            string key = unit.definition.jsonKey;
            return !string.IsNullOrEmpty(key) && s_byJsonKey.TryGetValue(key, out entry);
        }
    }
}
