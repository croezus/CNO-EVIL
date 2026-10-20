using System;
using System.Collections.Generic;
using HarmonyLib;
using TMPro;
using CnoEvil.Core;
using CnoEvil.Components;

namespace CnoEvil.Patches
{
    /// <summary>
    /// Attaches the detection status line to the vanilla Altitude HUD app (radar and absolute altitude).
    /// Attach patch: runs while the mod is off; the line hides itself then.
    /// </summary>
    [HarmonyPatch(typeof(Altitude), "Initialize")]
    public static class Altitude_Initialize_Patch
    {
        [HarmonyPostfix]
        public static void Postfix(Altitude __instance, TextMeshProUGUI ___radarAlt, TextMeshProUGUI ___absAlt)
        {
            try
            {
                if (__instance == null || ___absAlt == null) return;
                DetectionStatusLine line = __instance.GetComponent<DetectionStatusLine>();
                if (line == null)
                {
                    line = __instance.gameObject.AddComponent<DetectionStatusLine>();
                    line.Initialize(__instance, ___radarAlt, ___absAlt);
                }
                AltitudeHUDPatches.Register(__instance, line);
            }
            catch (Exception ex)
            {
                PatchErrorLog.Once("Altitude_Initialize_Patch.Postfix", ex);
            }
        }
    }

    /// <summary>
    /// Updates the detection status line with the vanilla altitude readouts.
    /// </summary>
    [HarmonyPatch(typeof(Altitude), "Refresh")]
    public static class Altitude_Refresh_Patch
    {
        [HarmonyPostfix]
        public static void Postfix(Altitude __instance, Aircraft ___aircraft)
        {
            try
            {
                DetectionStatusLine line = AltitudeHUDPatches.GetLine(__instance);
                if (line != null) line.Refresh(___aircraft);
            }
            catch (Exception ex)
            {
                PatchErrorLog.Once("Altitude_Refresh_Patch.Postfix", ex);
            }
        }
    }

    /// <summary>
    /// Keeps the line at the vanilla HUD font size.
    /// </summary>
    [HarmonyPatch(typeof(Altitude), "RefreshSettings")]
    public static class Altitude_RefreshSettings_Patch
    {
        [HarmonyPostfix]
        public static void Postfix(Altitude __instance)
        {
            try
            {
                DetectionStatusLine line = AltitudeHUDPatches.GetLine(__instance);
                if (line != null) line.SyncFontSize();
            }
            catch (Exception ex)
            {
                PatchErrorLog.Once("Altitude_RefreshSettings_Patch.Postfix", ex);
            }
        }
    }

    public static class AltitudeHUDPatches
    {
        // Refresh runs every frame: look the line up here instead of GetComponent
        private static readonly Dictionary<Altitude, DetectionStatusLine> s_lines = new Dictionary<Altitude, DetectionStatusLine>();

        internal static void Register(Altitude app, DetectionStatusLine line)
        {
            if (app != null && line != null) s_lines[app] = line;
        }

        internal static void Unregister(Altitude app)
        {
            if (app != null) s_lines.Remove(app);
        }

        internal static DetectionStatusLine GetLine(Altitude app)
        {
            DetectionStatusLine line;
            return (app != null && s_lines.TryGetValue(app, out line)) ? line : null;
        }

        public static void ClearAll()
        {
            s_lines.Clear();
        }
    }
}
