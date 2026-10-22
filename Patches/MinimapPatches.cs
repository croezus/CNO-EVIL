using System;
using HarmonyLib;
using UnityEngine;
using CnoEvil.Core;
using CnoEvil.Config;

namespace CnoEvil.Patches
{
    /// <summary>
    /// Cockpit minimap: centered on the aircraft and zoomable with MinimapZoomInKey / MinimapZoomOutKey.
    /// Vanilla CenterMinimizedMap centers the track-up minimap 4 km ahead of the camera and keeps the scale
    /// Minimize set (about 2); icons, grid labels and the RWR strobes all follow mapImage's live scale.
    /// </summary>
    [HarmonyPatch(typeof(DynamicMap))]
    public static class MinimapPatches
    {
        private const float ZoomStep = 1.25f;
        private const float MinZoom = 0.25f;
        private const float MaxZoom = 8.0f;

        // mapImage scale vanilla Minimize leaves on the minimap
        private static float s_baseScale = 2.0f;
        private static bool s_zoomApplied;

        [HarmonyPatch("Minimize")]
        [HarmonyPostfix]
        public static void Minimize_Postfix(DynamicMap __instance)
        {
            try
            {
                if (__instance.mapImage == null) return;
                float scale = __instance.mapImage.transform.localScale.x;
                if (scale > 0.0001f) s_baseScale = scale;
                s_zoomApplied = false;
            }
            catch (Exception ex)
            {
                PatchErrorLog.Once("MinimapPatches.Minimize_Postfix", ex);
            }
        }

        [HarmonyPatch("Maximize")]
        [HarmonyPrefix]
        public static void Maximize_Prefix(DynamicMap __instance)
        {
            try
            {
                // The full map opens at the vanilla scale, which its zoom controls (mapScaleCenter) assume
                if (DynamicMap.mapMaximized || !s_zoomApplied || __instance.mapImage == null) return;
                __instance.mapImage.transform.localScale = Vector3.one * s_baseScale;
                s_zoomApplied = false;
            }
            catch (Exception ex)
            {
                PatchErrorLog.Once("MinimapPatches.Maximize_Prefix", ex);
            }
        }

        [HarmonyPatch("CenterMinimizedMap")]
        [HarmonyPrefix]
        public static void CenterMinimizedMap_Prefix(DynamicMap __instance)
        {
            try
            {
                if (DynamicMap.mapMaximized || __instance.mapImage == null || CnoEvilConfig.MinimapZoom == null) return;
                if (!CnoEvilConfig.IsModActive)
                {
                    if (s_zoomApplied) __instance.mapImage.transform.localScale = Vector3.one * s_baseScale;
                    s_zoomApplied = false;
                    return;
                }

                float zoom = CnoEvilConfig.MinimapZoom.Value;
                // Typing [ or ] in the chat or the editor does not zoom
                if (!NuclearOption.MissionEditorScripts.InputFieldChecker.InsideInputField)
                {
                    if (Input.GetKeyDown(CnoEvilConfig.MinimapZoomInKey.Value)) zoom *= ZoomStep;
                    if (Input.GetKeyDown(CnoEvilConfig.MinimapZoomOutKey.Value)) zoom /= ZoomStep;
                }
                zoom = Mathf.Clamp(zoom, MinZoom, MaxZoom);
                if (!Mathf.Approximately(zoom, CnoEvilConfig.MinimapZoom.Value)) CnoEvilConfig.MinimapZoom.Value = zoom;

                // Before vanilla runs, so its position math uses the new scale
                __instance.mapImage.transform.localScale = Vector3.one * (s_baseScale * zoom);
                s_zoomApplied = true;
            }
            catch (Exception ex)
            {
                PatchErrorLog.Once("MinimapPatches.CenterMinimizedMap_Prefix", ex);
            }
        }

        [HarmonyPatch("CenterMinimizedMap")]
        [HarmonyPostfix]
        public static void CenterMinimizedMap_Postfix(DynamicMap __instance)
        {
            try
            {
                if (!CnoEvilConfig.IsModActive || DynamicMap.mapMaximized) return;
                if (__instance.mapImage == null || __instance.mapBackground == null) return;
                Aircraft aircraft = CombatHUD.i != null ? CombatHUD.i.aircraft : null;
                if (aircraft == null) return;

                // Vanilla: camera position + 4000 m along the flat heading; here the aircraft + MinimapLookAhead_m
                Vector3 forward = aircraft.transform.forward;
                forward.y = 0.0f;
                float lookAhead = CnoEvilConfig.MinimapLookAhead_m != null ? CnoEvilConfig.MinimapLookAhead_m.Value : 0.0f;
                Vector3 center = (aircraft.GlobalPosition().AsVector3() + forward.normalized * lookAhead) * __instance.mapDisplayFactor;

                Transform map = __instance.mapImage.transform;
                map.localPosition = (map.right * -center.x + map.up * -center.z)
                    * map.localScale.x * __instance.mapBackground.transform.localScale.x;
            }
            catch (Exception ex)
            {
                PatchErrorLog.Once("MinimapPatches.CenterMinimizedMap_Postfix", ex);
            }
        }
    }
}
