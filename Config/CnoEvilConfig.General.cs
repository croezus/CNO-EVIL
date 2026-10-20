using BepInEx.Configuration;
using UnityEngine;

namespace CnoEvil.Config
{
    public static partial class CnoEvilConfig
    {
        // 01. General
        public static ConfigEntry<bool> ModEnabled;
        public static ConfigEntry<KeyCode> ToggleModKey;
        public static ConfigEntry<bool> DebugLogging;
        public static ConfigEntry<bool> LogPerfStats;

        // 02. Debug Overlays
        public static ConfigEntry<bool> ShowDebugHUD;
        public static ConfigEntry<KeyCode> ToggleHUDKey;
        public static ConfigEntry<bool> DrawDebugGizmos;
        public static ConfigEntry<KeyCode> ToggleGizmosKey;
        public static ConfigEntry<bool> DrawDatalinkVisualizer;
        public static ConfigEntry<KeyCode> ToggleDatalinkVisualizerKey;
        public static ConfigEntry<bool> ShowDatalinkDiagnosticPanel;
        public static ConfigEntry<bool> ShowDatalinkMeshLines;

        private const string SectionGeneral = "01. General";
        private const string SectionDebug = "02. Debug Overlays";

        private static void BindGeneral(ConfigFile c)
        {
            ModEnabled = Bind(c, SectionGeneral, "ModEnabled", true,
                "Master switch. TRUE: CNO-EVIL radar physics, RWR, tracking and doctrine run. FALSE: the vanilla game code runs.");
            ToggleModKey = Bind(c, SectionGeneral, "ToggleModKey", KeyCode.F10,
                "Hotkey that flips ModEnabled in game (A/B comparison with vanilla).");
            DebugLogging = Bind(c, SectionGeneral, "DebugLogging", false,
                "Verbose physics, tracking and seeker logs in the BepInEx console.");
            LogPerfStats = Bind(c, SectionGeneral, "LogPerfStats", false,
                "Every 10 s, write the CPU time (ms per second) and the call count of each mod subsystem and the mod network traffic (bytes and packets per second) to the BepInEx log. Use it to measure the load of a host.");
        }

        private static void BindDebugOverlays(ConfigFile c)
        {
            ShowDebugHUD = Bind(c, SectionDebug, "ShowDebugHUD", false,
                "Radar telemetry panel (distance, echo power, SNR, RCS, notch, jamming, burn-through).");
            ToggleHUDKey = Bind(c, SectionDebug, "ToggleHUDKey", KeyCode.F9,
                "Hotkey for the telemetry panel.");
            DrawDebugGizmos = Bind(c, SectionDebug, "DrawDebugGizmos", false,
                "Radar line-of-sight rays and Doppler vectors in the world (green clear, yellow clutter-degraded, red notched / masked).");
            ToggleGizmosKey = Bind(c, SectionDebug, "ToggleGizmosKey", KeyCode.F11,
                "Hotkey for the radar debug rays.");
            DrawDatalinkVisualizer = Bind(c, SectionDebug, "DrawDatalinkVisualizer", false,
                "3D datalink overlay: network mesh, shared radar / visual tracks, relayed missile alerts.");
            ToggleDatalinkVisualizerKey = Bind(c, SectionDebug, "ToggleDatalinkVisualizerKey", KeyCode.F8,
                "Hotkey for the datalink overlay.");
            ShowDatalinkDiagnosticPanel = Bind(c, SectionDebug, "ShowDatalinkDiagnosticPanel", true,
                "Datalink telemetry panel while the datalink overlay is on.");
            ShowDatalinkMeshLines = Bind(c, SectionDebug, "ShowDatalinkMeshLines", true,
                "Lines between friendly datalink nodes in the datalink overlay.");
        }
    }
}
