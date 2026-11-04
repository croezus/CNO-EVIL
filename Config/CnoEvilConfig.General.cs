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
                "Master switch. TRUE: CNO-EVIL radar physics, RWR, tracking and doctrine run. FALSE: the vanilla game code runs. " +
                "In multiplayer the mod runs only when the host also runs it. A client then uses the host values of sections 03 to 11 " +
                "and of the display settings that change what a player knows (Q tint, ident tiers, HUD cues, map zones and icon hiding, " +
                "missile aim points, target camera zoom, thermal range); its own values come back after the session. Visual settings stay local.");
            ToggleModKey = Bind(c, SectionGeneral, "ToggleModKey", KeyCode.F10,
                "Hotkey that flips ModEnabled in game (A/B comparison with vanilla). The new value is saved to this file. " +
                "Single player and host only: a multiplayer client follows the host switch and ignores this key and ModEnabled.");
            DebugLogging = Bind(c, SectionGeneral, "DebugLogging", false,
                "Extra lines in the BepInEx log: the radar model of each emitter, the munition profiles, passive bearing fixes, " +
                "RCS database values and lost missile memory coasts.");
        }

        private static void BindDebugOverlays(ConfigFile c)
        {
            ShowDebugHUD = Bind(c, SectionDebug, "ShowDebugHUD", false,
                "Radar telemetry panel (distance, echo power, SNR, RCS, notch, jamming, burn-through).");
            ToggleHUDKey = Bind(c, SectionDebug, "ToggleHUDKey", KeyCode.F9,
                "Hotkey for the telemetry panel. The new value is saved to this file. Single player and host only.");
            DrawDebugGizmos = Bind(c, SectionDebug, "DrawDebugGizmos", false,
                "Radar line-of-sight rays and Doppler vectors in the world: green clear, yellow clutter-degraded, red notched or " +
                "below the radar horizon. Targets behind terrain get no ray.");
            ToggleGizmosKey = Bind(c, SectionDebug, "ToggleGizmosKey", KeyCode.F11,
                "Hotkey for the radar debug rays. The new value is saved to this file. Single player and host only.");
            DrawDatalinkVisualizer = Bind(c, SectionDebug, "DrawDatalinkVisualizer", false,
                "3D datalink overlay: network nodes, shared radar / visual tracks, relayed missile alerts.");
            ToggleDatalinkVisualizerKey = Bind(c, SectionDebug, "ToggleDatalinkVisualizerKey", KeyCode.F8,
                "Hotkey for the datalink overlay. The new value is saved to this file. Single player and host only.");
            ShowDatalinkDiagnosticPanel = Bind(c, SectionDebug, "ShowDatalinkDiagnosticPanel", true,
                "Datalink telemetry panel while the datalink overlay is on.");
            ShowDatalinkMeshLines = Bind(c, SectionDebug, "ShowDatalinkMeshLines", true,
                "Lines in the datalink overlay from the network hub (AWACS, else a warship, else your aircraft) to each friendly node " +
                "closer than 80 km.");
        }
    }
}
