using System;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using CnoEvil.Config;

namespace CnoEvil
{
    [BepInPlugin(ModGuid, ModName, ModVersion)]
    public class CnoEvilPlugin : BaseUnityPlugin
    {
        public const string ModGuid = "com.cnoevil.nuclearoption.overhaul";
        public const string ModName = "CNO-EVIL";
        public const string ModVersion = "1.0.7";

        public static CnoEvilPlugin Instance { get; private set; }
        public static ManualLogSource Log { get; private set; }

        private Harmony _harmony;

        private void Awake()
        {
            Instance = this;
            Log = Logger;

            Log.LogInfo(string.Format("Initializing {0} v{1}...", ModName, ModVersion));

            // Carry the config over from the old Radio Wars GUID once, so player settings survive the rename
            MigrateLegacyConfigFile();

            // Initialize BepInEx Configuration
            CnoEvilConfig.Initialize(Config);

            // Apply Harmony Patches safely per-class
            try
            {
                _harmony = new Harmony(ModGuid);
                int successCount = 0;
                int failCount = 0;
                Type[] allTypes = System.Reflection.Assembly.GetExecutingAssembly().GetTypes();
                for (int i = 0; i < allTypes.Length; i++)
                {
                    Type patchType = allTypes[i];
                    if (patchType.IsDefined(typeof(HarmonyPatch), true))
                    {
                        try
                        {
                            _harmony.CreateClassProcessor(patchType).Patch();
                            successCount++;
                        }
                        catch (Exception ex)
                        {
                            failCount++;
                            Log.LogError(string.Format("Failed to apply patch class {0}: {1}", patchType.FullName, ex));
                        }
                    }
                }
                Log.LogInfo(string.Format("CNO-EVIL Harmony patches applied: {0} succeeded, {1} failed.", successCount, failCount));
            }
            catch (Exception ex)
            {
                Log.LogError(string.Format("Failed to initialize Harmony: {0}", ex));
            }

            Log.LogInfo(string.Format("CNO-EVIL mod active: {0}", CnoEvilConfig.IsModActive));
            Log.LogInfo(string.Format("Clutter Notch Velocity: {0} m/s", CnoEvilConfig.EffectiveNotchThreshold));
            Log.LogInfo(string.Format("Minimum Detection SNR: {0} dB", CnoEvilConfig.EffectiveMinSNR));
            Log.LogInfo(string.Format("4/3 Earth Curvature Refraction: {0}", CnoEvilConfig.EarthCurvatureEnabled.Value));

            // Attach Telemetry & Visualizer HUD
            gameObject.AddComponent<CnoEvil.Components.CnoEvilDebugHUD>();
            gameObject.AddComponent<CnoEvil.Components.RadarDebugVisualizer>();
            gameObject.AddComponent<CnoEvil.Components.DatalinkDebugVisualizer>();
            Log.LogInfo("CNO-EVIL Debug Visualizer (F11 Gizmos), Datalink Visualizer (F8), & Telemetry HUD (F9) attached.");

            // Listen for scene/match lifecycle to clear tactical intelligence on mission reset
            UnityEngine.SceneManagement.SceneManager.sceneLoaded += OnSceneLoaded;
        }

        private const string LegacyModGuid = "com.radiowars.nuclearoption.overhaul";

        private void MigrateLegacyConfigFile()
        {
            try
            {
                string legacyPath = System.IO.Path.Combine(Paths.ConfigPath, LegacyModGuid + ".cfg");
                if (System.IO.File.Exists(Config.ConfigFilePath) || !System.IO.File.Exists(legacyPath)) return;

                System.IO.File.Copy(legacyPath, Config.ConfigFilePath);
                Config.Reload();
                Log.LogInfo(string.Format("Config carried over from {0}.cfg", LegacyModGuid));
            }
            catch (Exception ex)
            {
                Log.LogWarning("Legacy config migration skipped: " + ex.Message);
            }
        }

        private void OnSceneLoaded(UnityEngine.SceneManagement.Scene scene, UnityEngine.SceneManagement.LoadSceneMode mode)
        {
            if (Log != null)
            {
                Log.LogInfo(string.Format("Scene loaded ({0}). Resetting electronic intelligence & target memory tracks.", scene.name));
            }
            CnoEvil.Core.RWRTriangulationProcessor.ClearAll();
            CnoEvil.Core.VanillaKnowledgeExpiry.ClearAll();
            CnoEvil.Core.TrackUncertaintyCalculator.Clear();
            CnoEvil.Core.DatalinkNetwork.ClearAll();
            CnoEvil.Core.RadarTelemetry.ClearAll();
            CnoEvil.Components.PhysRadarEmitter.ClearAll();
            CnoEvil.Components.PhysRadarTarget.ClearAll();
            CnoEvil.Components.PhysRWRReceiver.ClearAll();
            CnoEvil.Patches.DynamicMapPatches.ClearAll();
            CnoEvil.Patches.ARHSeeker_GetRadarReturn_Patch.ClearAllCoastTimers();
            CnoEvil.Patches.SARHSeeker_GetTrackingStrength_Patch.ClearAllCoastTimers();
            CnoEvil.Patches.SeekerDispersionHelper.ClearAll();
            CnoEvil.Core.MunitionRCSDatabase.ClearAll();
            CnoEvil.Core.MunitionCatalog.ClearCache();
            CnoEvil.Core.MunitionAudit.ClearAll();
            CnoEvil.Core.JammingConeProcessor.ClearAll();
            CnoEvil.Core.DetectionStatusNet.ClearAll();
            CnoEvil.Core.TrackSync.ClearAll();
            CnoEvil.Core.RwrFeedSync.ClearAll();
            CnoEvil.Core.MissileSync.ClearAll();
            CnoEvil.Patches.AltitudeHUDPatches.ClearAll();

            // Self-healing census for newly loaded mission scene
            CnoEvil.Core.DatalinkNetwork.CensusSceneUnitsAndRadars(false);
        }

        private void Update()
        {
            // Network registration and hello also run while the mod is off
            CnoEvil.Core.CnoEvilNet.Update();
            CnoEvil.Core.DetectionStatusNet.Update();
            CnoEvil.Core.TrackSync.Update();
            CnoEvil.Core.RwrFeedSync.Update();
            CnoEvil.Core.MissileSync.Update();
            CnoEvil.Core.VanillaKnowledgeExpiry.UpdateNet();
            CnoEvil.Core.PerfStats.Tick();

            if (!CnoEvilConfig.IsModActive) return;

            // Universal multi-agent datalink cycle (fuses bot/player visual reconnaissance and radar tracks)
            long perf = CnoEvil.Core.PerfStats.Begin();
            CnoEvil.Core.DatalinkNetwork.ProcessDatalinkCycle();
            CnoEvil.Core.PerfStats.End(CnoEvil.Core.PerfSlot.DatalinkCycle, perf);

            if (CnoEvilConfig.EnableRWRTriangulationSystem == null || CnoEvilConfig.EnableRWRTriangulationSystem.Value)
            {
                perf = CnoEvil.Core.PerfStats.Begin();
                CnoEvil.Core.RWRTriangulationProcessor.UpdateAllTracks(UnityEngine.Time.deltaTime);
                CnoEvil.Core.PerfStats.End(CnoEvil.Core.PerfSlot.TrackUpdate, perf);
            }
        }

        private void OnDestroy()
        {
            UnityEngine.SceneManagement.SceneManager.sceneLoaded -= OnSceneLoaded;
            CnoEvil.Core.RWRTriangulationProcessor.ClearAll();
            CnoEvil.Core.VanillaKnowledgeExpiry.ClearAll();
            CnoEvil.Core.TrackUncertaintyCalculator.Clear();
            CnoEvil.Core.DatalinkNetwork.ClearAll();
            CnoEvil.Core.RadarTelemetry.ClearAll();
            CnoEvil.Components.PhysRadarEmitter.ClearAll();
            CnoEvil.Components.PhysRadarTarget.ClearAll();
            CnoEvil.Components.PhysRWRReceiver.ClearAll();
            CnoEvil.Patches.DynamicMapPatches.ClearAll();
            CnoEvil.Patches.ARHSeeker_GetRadarReturn_Patch.ClearAllCoastTimers();
            CnoEvil.Patches.SARHSeeker_GetTrackingStrength_Patch.ClearAllCoastTimers();
            CnoEvil.Patches.SeekerDispersionHelper.ClearAll();
            CnoEvil.Core.MunitionRCSDatabase.ClearAll();
            CnoEvil.Core.MunitionCatalog.ClearCache();
            CnoEvil.Core.MunitionAudit.ClearAll();
            CnoEvil.Core.JammingConeProcessor.ClearAll();
            CnoEvil.Core.DetectionStatusNet.Shutdown();
            CnoEvil.Core.TrackSync.Shutdown();
            CnoEvil.Core.RwrFeedSync.Shutdown();
            CnoEvil.Core.MissileSync.Shutdown();
            CnoEvil.Core.VanillaKnowledgeExpiry.Shutdown();
            CnoEvil.Core.CnoEvilNet.Shutdown();
            CnoEvil.Patches.AltitudeHUDPatches.ClearAll();
            if (_harmony != null)
            {
                _harmony.UnpatchSelf();
            }
        }
    }
}
