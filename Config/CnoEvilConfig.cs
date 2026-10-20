using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Configuration;

namespace CnoEvil.Config
{
    /// <summary>
    /// BepInEx configuration. One partial file per config section (CnoEvilConfig.*.cs); every value is a plain
    /// number or switch in BepInEx/config. Bound in CnoEvilPlugin.Awake before any Harmony patch is applied,
    /// so entries are never null at runtime.
    /// </summary>
    public static partial class CnoEvilConfig
    {
        public static void Initialize(ConfigFile config)
        {
            BindGeneral(config);
            BindDebugOverlays(config);
            BindRadarPhysics(config);
            BindElectronicWarfare(config);
            BindMissileGuidance(config);
            BindRwrReceiver(config);
            BindTargetTracking(config);
            BindTrackQuality(config);
            BindWeaponDispersion(config);
            BindAIDoctrine(config);
            BindDatalink(config);
            BindHudMarkers(config);
            BindTacticalMap(config);
            BindCockpitTacScreen(config);
            BindMissileTrajectoryHud(config);
            BindHmdThermalWindow(config);

            MigrateMovedEntries(config);
        }

        public static bool IsModActive
        {
            get { return ModEnabled != null && ModEnabled.Value && CnoEvil.Core.CnoEvilNet.ModAllowed; }
        }

        /// <summary>
        /// The F9 telemetry panel or the F11 radar rays are on: the only readers of RadarTelemetry.
        /// </summary>
        public static bool RadarTelemetryWanted
        {
            get
            {
                return (ShowDebugHUD != null && ShowDebugHUD.Value) || (DrawDebugGizmos != null && DrawDebugGizmos.Value);
            }
        }

        public static float EffectiveNotchThreshold
        {
            get { return NotchVelocityThreshold != null ? NotchVelocityThreshold.Value : 20.0f; }
        }

        public static float EffectiveNotchClutterEdgeFactor
        {
            get { return NotchClutterEdgeFactor != null ? NotchClutterEdgeFactor.Value : 2.0f; }
        }

        public static float EffectiveRadarSidelobeClutterDb
        {
            get { return RadarSidelobeClutter_dB != null ? RadarSidelobeClutter_dB.Value : -35.0f; }
        }

        public static float EffectiveSeekerSidelobeClutterDb
        {
            get { return SeekerSidelobeClutter_dB != null ? SeekerSidelobeClutter_dB.Value : -30.0f; }
        }

        public static float EffectiveMinSNR
        {
            get { return MinSNR_dB != null ? MinSNR_dB.Value : 10.0f; }
        }

        private static ConfigEntry<T> Bind<T>(ConfigFile config, string section, string key, T defaultValue, string description)
        {
            return config.Bind(section, key, defaultValue, description);
        }

        /// <summary>
        /// Entries that moved to a new section keep their key: carry the value a user saved under the old section
        /// over to the new one, then drop every leftover entry (moved, removed presets and dead keys) from the file.
        /// </summary>
        private static void MigrateMovedEntries(ConfigFile config)
        {
            try
            {
                PropertyInfo orphanProp = typeof(ConfigFile).GetProperty("OrphanedEntries", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                Dictionary<ConfigDefinition, string> orphans = (orphanProp != null)
                    ? orphanProp.GetValue(config, null) as Dictionary<ConfigDefinition, string>
                    : null;
                if (orphans == null || orphans.Count == 0) return;

                Dictionary<string, string> orphanByKey = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var kvp in orphans)
                {
                    orphanByKey[kvp.Key.Key] = kvp.Value;
                }

                int migrated = 0;
                foreach (var kvp in config)
                {
                    string oldValue;
                    if (orphanByKey.TryGetValue(kvp.Key.Key, out oldValue))
                    {
                        kvp.Value.SetSerializedValue(oldValue);
                        migrated++;
                    }
                }

                int dropped = orphans.Count;
                orphans.Clear();
                config.Save();

                if (CnoEvilPlugin.Log != null)
                {
                    CnoEvilPlugin.Log.LogInfo(string.Format("Config migration: {0} values carried to new sections, {1} obsolete entries removed.", migrated, dropped));
                }
            }
            catch (Exception ex)
            {
                if (CnoEvilPlugin.Log != null) CnoEvilPlugin.Log.LogWarning("Config migration skipped: " + ex.Message);
            }
        }
    }
}
