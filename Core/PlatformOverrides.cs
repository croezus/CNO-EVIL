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
        // The jsonKey of each unit is in the BepInEx log when DebugLogging is on ("[CNO-EVIL] Radar ...").
        private static readonly Dictionary<string, PlatformOverride> s_byJsonKey =
            new Dictionary<string, PlatformOverride>(StringComparer.Ordinal)
        {
            // Aryx F-16M King Viper: typeIdentity.radar = 0 in its definition, but the aircraft has a Radar
            // (32 km / minSignal 0.5 = 64 km on 1 m²). Template and range stay from the game data.
            { "Aryx_F16M_KingViper", new PlatformOverride(RadarTemplate.FromType, 0.0f, 2) },
            // Aryx F-22E Strike Raptor: typeIdentity.radar = 0 in its definition, but the aircraft has a Radar
            // (60 km / minSignal 0.5 = 120 km on 1 m²) and a stealth airframe (radarSize 0.001). Template and range
            // stay from the game data.
            { "Aryx_F22E_StrikeRaptor", new PlatformOverride(RadarTemplate.FromType, 0.0f, 3) },
            // Aryx F-99 Shrike: typeIdentity.radar = 0 in its definition, but the aircraft has a Radar
            // (25 km / minSignal 0.5 = 50 km on 1 m²) and a jammer; radarSize 0.1 is not stealth. Template and range
            // stay from the game data.
            { "Aryx_LightFighter1", new PlatformOverride(RadarTemplate.FromType, 0.0f, 2) },
            // Aryx FS-41 Eclipse: typeIdentity.radar = 0 in its definition, but the aircraft has a Radar
            // (75 km / minSignal 0.4 = 187 km on 1 m²), a jammer and a stealth airframe (radarSize 0.0018). Template
            // and range stay from the game data.
            { "Aryx_Interceptor1", new PlatformOverride(RadarTemplate.FromType, 0.0f, 3) },
            // Aryx MC-260 Chimera: typeIdentity.radar = 0 in its definition, but the aircraft has a Radar
            // (50 km / minSignal 0.5 = 100 km on 1 m²), a jammer, a radar locator and a radome mount with its own Radar;
            // radarSize 9 is not stealth. Template and range stay from the game data.
            { "Aryx_CargoPlane1", new PlatformOverride(RadarTemplate.FromType, 0.0f, 2) }
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
