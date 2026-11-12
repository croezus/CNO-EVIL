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
        public static ConfigEntry<bool> DebugLogRadar;
        public static ConfigEntry<bool> DebugLogTrack;
        public static ConfigEntry<bool> DebugLogMissile;
        public static ConfigEntry<bool> DebugLogAI;
        public static ConfigEntry<bool> DebugLogEW;
        public static ConfigEntry<bool> DebugLogNet;
        public static ConfigEntry<bool> DebugLogPlacement;
        public static ConfigEntry<bool> DebugLogData;
        public static ConfigEntry<int> DebugLogKeepSessions;
        public static ConfigEntry<KeyCode> LogMarkKey;

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
                "Writes a session log to BepInEx\\CNO-EVIL-Logs, with time, scene number and frame on each line. " +
                "The DebugLog switches in section 02 select the categories. Errors and warnings always go to LogOutput.log.");
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
            DebugLogRadar = Bind(c, SectionDebug, "DebugLogRadar", true,
                "Session log: radar model of each unit type, and each change of a scan result that involves the player aircraft " +
                "(detected, below threshold, notched, masked, decoy capture) with SNR, RCS and jamming power.");
            DebugLogTrack = Bind(c, SectionDebug, "DebugLogTrack", true,
                "Session log: faction tracks. New track, change of Q band (0.25 steps), memory state, located, forgotten, bearing fixes.");
            DebugLogMissile = Bind(c, SectionDebug, "DebugLogMissile", true,
                "Session log: missiles. Launch with Q and aim offset, seeker lock and loss, memory coast loss, end of flight.");
            DebugLogAI = Bind(c, SectionDebug, "DebugLogAI", true,
                "Session log: AI. The reason of each held launch (at most one line in 20 s for a launcher, target and reason), " +
                "drag, crank, turn away from own countermeasures.");
            DebugLogEW = Bind(c, SectionDebug, "DebugLogEW", true,
                "Session log: electronic warfare. Self-protection jammer on, off and out of charge, pod beams, chaff and decoy captures.");
            DebugLogNet = Bind(c, SectionDebug, "DebugLogNet", true,
                "Session log: multiplayer resync requests of a mirror client.");
            DebugLogPlacement = Bind(c, SectionDebug, "DebugLogPlacement", true,
                "Session log: dynamic placement. Result of each battery and the ground colliders.");
            DebugLogData = Bind(c, SectionDebug, "DebugLogData", true,
                "Session log: data of the mod. Munition profiles, RCS database values, player aircraft, mod on / off.");
            DebugLogKeepSessions = Bind(c, SectionDebug, "DebugLogKeepSessions", 10,
                "Number of session log files to keep. Older files are deleted when a new session starts.");
            LogMarkKey = Bind(c, SectionDebug, "LogMarkKey", KeyCode.F6,
                "Hotkey that writes a numbered mark and a snapshot (player, targets, Q, missiles, radars on the player) to the " +
                "session log. Needs DebugLogging.");
        }
    }
}
