using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using TMPro;
using NuclearOption.Networking;
using CnoEvil.Core;
using CnoEvil.Config;

namespace CnoEvil.Patches
{
    public static class CombatHUDPatches
    {
        internal static FactionHQ GetPlayerHQ()
        {
            Aircraft player = CombatHUD.i != null ? CombatHUD.i.aircraft : null;
            return (player != null && player.NetworkHQ != null) ? player.NetworkHQ : RWRTriangulationProcessor.GetPlayerFactionHQ();
        }
    }

    /// <summary>
    /// HUD / HMD unit markers stay vanilla (position, outdated state, selection and locking). Two changes:
    /// - The marker of a hostile tracked threat that fails UnitMapIcon_UpdateIcon_Patch.IsHmdVisible (no recent
    ///   radar / visual contact, not located, Q below HMDMarkerQualityThreshold) is hidden the vanilla way
    ///   (image.enabled = false; vanilla enables it again each frame, the postfix runs before the frame renders).
    ///   Vanilla CombatHUD.TargetSelect skips disabled markers, so it cannot be selected. A selected marker stays.
    /// - A tint by the player faction's tracking quality Q for units it holds a track on: pale white (low) -> amber
    ///   -> red (fire-control quality). Selection, fresh and flashing colors stay vanilla, and the vanilla alpha is
    ///   kept (opacity, halved while outdated and minimized).
    /// </summary>
    [HarmonyPatch(typeof(HUDUnitMarker), "UpdatePosition", new Type[] { typeof(FactionHQ), typeof(GlobalPosition), typeof(Vector3) })]
    public static class HUDUnitMarker_UpdatePosition_Patch
    {
        [HarmonyPostfix]
        public static void Postfix(HUDUnitMarker __instance, bool ___hidden, bool ___flashing, bool ___maximized, float ___opacity)
        {
            try
            {
                if (!CnoEvilConfig.IsModActive) return;
                if (___hidden || __instance.unit == null || __instance.image == null || !__instance.image.enabled) return;

                FactionHQ hq = CombatHUDPatches.GetPlayerHQ();
                if (hq == null) return;
                if (!__instance.selected && UnitMapIcon_UpdateIcon_Patch.IsHmdHiddenThreat(hq, __instance.unit))
                {
                    __instance.image.enabled = false;
                    return;
                }

                if (!CnoEvilConfig.EnableHUDMarkerQualityTint.Value) return;
                if (__instance.selected || __instance.fresh || ___flashing) return;
                if (!ThreatClassification.HasQualityTrack(__instance.unit, hq)) return;

                // Read-only lookup
                Dictionary<Unit, TriangulationTrack> tracks = RWRTriangulationProcessor.GetTrackDictionary(hq);
                TriangulationTrack track;
                if (tracks == null || !tracks.TryGetValue(__instance.unit, out track) || track == null) return;

                float q = Mathf.Clamp01(track.TrackingQuality);
                float a = ___opacity * ((__instance.outdated && !___maximized) ? 0.5f : 1.0f);
                Color lowColor = new Color(0.95f, 0.95f, 0.98f, a);
                Color midColor = new Color(1.00f, 0.80f, 0.15f, a);
                Color highColor = new Color(1.00f, 0.25f, 0.25f, a);
                __instance.image.color = (q < 0.5f)
                    ? Color.Lerp(lowColor, midColor, q * 2.0f)
                    : Color.Lerp(midColor, highColor, (q - 0.5f) * 2.0f);
            }
            catch (Exception ex)
            {
                PatchErrorLog.Once("HUDUnitMarker_UpdatePosition_Patch.Postfix", ex);
            }
        }
    }

    /// <summary>
    /// CombatHUD.SelectUnit (vanilla caller: a click on a map icon) does not select a hostile tracked threat whose
    /// HMD marker is hidden (UnitMapIcon_UpdateIcon_Patch.IsHmdHiddenThreat). HUD designator selection
    /// (TargetSelect) already skips hidden markers.
    /// </summary>
    [HarmonyPatch(typeof(CombatHUD), "SelectUnit", new Type[] { typeof(Unit) })]
    public static class CombatHUD_SelectUnit_Patch
    {
        [HarmonyPrefix]
        public static bool Prefix(CombatHUD __instance, Unit unit)
        {
            try
            {
                if (!CnoEvilConfig.IsModActive || unit == null) return true;
                FactionHQ hq = CombatHUDPatches.GetPlayerHQ();
                return !UnitMapIcon_UpdateIcon_Patch.IsHmdHiddenThreat(hq, unit);
            }
            catch (Exception ex)
            {
                PatchErrorLog.Once("CombatHUD_SelectUnit_Patch", ex);
                return true;
            }
        }
    }

    /// <summary>
    /// The selected target's HMD info (vanilla: pilot name, definition.code, exact range) follows the faction's
    /// tracking quality Q (TargetIdentification): AIR? / ~40 km at low Q, FTR / ~38 km at mid Q, vanilla at high Q.
    /// The label carries the Q grade (Q>0, Q>25, Q>50, Q>75; none at full quality).
    /// Units without a track (friendly, non-threat types) stay vanilla.
    /// </summary>
    [HarmonyPatch(typeof(CombatHUD), "ShowTargetInfo")]
    public static class CombatHUD_ShowTargetInfo_Patch
    {
        [HarmonyPostfix]
        public static void Postfix(CombatHUD __instance, bool __result, List<Unit> ___targetList, TextMeshProUGUI ___targetInfo)
        {
            try
            {
                if (!__result || !CnoEvilConfig.IsModActive) return;
                if (!CnoEvilConfig.EnableHMDTargetIdentDegradation.Value) return;
                if (___targetInfo == null || ___targetList == null || ___targetList.Count == 0 || __instance.aircraft == null) return;

                Unit unit = ___targetList[0];
                FactionHQ hq = CombatHUDPatches.GetPlayerHQ();
                IdentTier tier;
                TriangulationTrack track;
                if (!MapTargetInfo.TryGetTier(unit, hq, out tier, out track)) return;

                // Same range as vanilla: faction known position to ownship
                GlobalPosition knownPos;
                if (!hq.TryGetKnownPosition(unit, out knownPos)) return;
                float dist = FastMath.Distance(knownPos, __instance.aircraft.GlobalPosition());

                string text = string.Empty;
                Aircraft aircraft = unit as Aircraft;
                if (tier == IdentTier.High && aircraft != null && aircraft.pilots[0] != null && aircraft.Player != null)
                {
                    text = aircraft.Player.GetDisplayName(PlayerNameContext.Other) + "\n";
                }
                // Vanilla "\n\n\n" keeps the label above the marker icon and the range below it
                ___targetInfo.text = text + MapTargetInfo.Label(unit, tier, track) + "\n\n\n" + TargetIdentification.FormatRange(dist, tier);
            }
            catch (Exception ex)
            {
                PatchErrorLog.Once("CombatHUD_ShowTargetInfo_Patch.Postfix", ex);
            }
        }
    }
}
