using System;
using System.Collections.Generic;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using CnoEvil.Core;
using CnoEvil.Config;

namespace CnoEvil.Patches
{
    /// <summary>
    /// Shared ident tier lookup for the map target info (selection marker and hover tooltip), the HMD target info
    /// and the target camera info: hostile units with a quality track only, read-only lookup.
    /// </summary>
    internal static class MapTargetInfo
    {
        /// <summary>
        /// Map lookup: gated by the mod switch and EnableTargetCamIdentDegradation, player faction.
        /// </summary>
        public static bool TryGetTier(Unit unit, out IdentTier tier, out TriangulationTrack track)
        {
            tier = IdentTier.High;
            track = null;
            if (!CnoEvilConfig.IsModActive) return false;
            if (!CnoEvilConfig.EnableTargetCamIdentDegradation.Value) return false;
            return TryGetTier(unit, CombatHUDPatches.GetPlayerHQ(), out tier, out track);
        }

        /// <summary>
        /// Lookup without config gates, for the callers with their own switch.
        /// </summary>
        public static bool TryGetTier(Unit unit, FactionHQ hq, out IdentTier tier, out TriangulationTrack track)
        {
            tier = IdentTier.High;
            track = null;
            if (unit == null || hq == null || !ThreatClassification.HasQualityTrack(unit, hq)) return false;

            // Read-only lookup
            Dictionary<Unit, TriangulationTrack> tracks = RWRTriangulationProcessor.GetTrackDictionary(hq);
            if (tracks == null || !tracks.TryGetValue(unit, out track) || track == null) return false;

            tier = TargetIdentification.GetTier(track);
            return true;
        }

        public static string Label(Unit unit, IdentTier tier, TriangulationTrack track)
        {
            return WithQuality(TargetIdentification.GetLabel(unit, tier), track);
        }

        /// <summary>
        /// The label with the Q grade of the track (none at full quality).
        /// </summary>
        public static string WithQuality(string label, TriangulationTrack track)
        {
            string quality = TargetIdentification.FormatQuality(track.TrackingQuality);
            return (quality.Length > 0) ? label + " " + quality : label;
        }
    }

    /// <summary>
    /// The map marker of the selected target (vanilla: unit code, pilot name and, with an accurate vanilla position,
    /// exact speed, altitude, heading and range) follows the faction's tracking quality Q with the HMD ident tiers.
    /// Low: domain label, range to 10 km, other rows "-". Mid: class label, range to 2 km, heading / altitude / speed
    /// rounded. High: vanilla values. The name carries the Q grade at every tier; the pilot name shows only at High.
    /// Below LocatedQualityThreshold the marker does not show at all (Show_Postfix).
    /// Vanilla writes the name once (ExtraSetup) and the rows on its slow refresh (Update sets lastRefresh),
    /// so the postfix writes only on those frames.
    /// </summary>
    [HarmonyPatch(typeof(TargetMarker))]
    public static class TargetMarker_Info_Patch
    {
        [HarmonyPatch("ExtraSetup")]
        [HarmonyPostfix]
        public static void ExtraSetup_Postfix(TargetMarker __instance, Text ___infoPlayer, Text ___infoName, Text ___infoSpeed,
            Text ___infoAlt, Text ___infoHeading, Text ___infoRange)
        {
            try
            {
                Apply(__instance, ___infoPlayer, ___infoName, ___infoSpeed, ___infoAlt, ___infoHeading, ___infoRange);
            }
            catch (Exception ex)
            {
                PatchErrorLog.Once("TargetMarker_Info_Patch.ExtraSetup", ex);
            }
        }

        [HarmonyPatch("Update")]
        [HarmonyPostfix]
        public static void Update_Postfix(TargetMarker __instance, Text ___infoPlayer, Text ___infoName, Text ___infoSpeed,
            Text ___infoAlt, Text ___infoHeading, Text ___infoRange)
        {
            try
            {
                // Vanilla refresh frame only
                if (__instance.lastRefresh != Time.timeSinceLevelLoad) return;
                Apply(__instance, ___infoPlayer, ___infoName, ___infoSpeed, ___infoAlt, ___infoHeading, ___infoRange);
            }
            catch (Exception ex)
            {
                PatchErrorLog.Once("TargetMarker_Info_Patch.Update", ex);
            }
        }

        /// <summary>
        /// Below LocatedQualityThreshold the selection box does not show on the map or the minimap. All vanilla paths
        /// (ExtraSetup, DynamicHide on the slow refresh) go through Show, so the box hides in the same frame and shows
        /// again on the next refresh when Q rises or the mod turns off. The test is Q only: a recent contact gives
        /// the unit a map icon, but not this box.
        /// </summary>
        [HarmonyPatch("Show")]
        [HarmonyPostfix]
        public static void Show_Postfix(TargetMarker __instance, Image ___markerImg, Text ___infoPlayer, Text ___infoName,
            Text ___infoSpeed, Text ___infoAlt, Text ___infoHeading, Text ___infoRange)
        {
            try
            {
                IdentTier tier;
                TriangulationTrack track;
                if (!MapTargetInfo.TryGetTier(__instance.GetUnit(), out tier, out track) || !IsBoxHidden(track)) return;

                if (___markerImg != null) ___markerImg.enabled = false;
                if (___infoPlayer != null) ___infoPlayer.enabled = false;
                if (___infoName != null) ___infoName.enabled = false;
                if (___infoSpeed != null) ___infoSpeed.enabled = false;
                if (___infoAlt != null) ___infoAlt.enabled = false;
                if (___infoHeading != null) ___infoHeading.enabled = false;
                if (___infoRange != null) ___infoRange.enabled = false;
            }
            catch (Exception ex)
            {
                PatchErrorLog.Once("TargetMarker_Info_Patch.Show", ex);
            }
        }

        private static bool IsBoxHidden(TriangulationTrack track)
        {
            return track.TrackingQuality < CnoEvilConfig.LocatedQualityThreshold.Value;
        }

        /// <summary>
        /// Vanilla turns the pilot name on only in ExtraSetup, so the mod shows again what it hid below High.
        /// </summary>
        private static void RestorePilotName(Unit unit, Text infoPlayer)
        {
            if (infoPlayer == null || infoPlayer.enabled) return;
            Aircraft aircraft = unit as Aircraft;
            if (aircraft == null || aircraft.pilots == null || aircraft.pilots.Length == 0) return;
            if (aircraft.pilots[0] != null && aircraft.pilots[0].player != null) infoPlayer.enabled = true;
        }

        private static void Apply(TargetMarker marker, Text infoPlayer, Text infoName, Text infoSpeed,
            Text infoAlt, Text infoHeading, Text infoRange)
        {
            Unit unit = marker.GetUnit();
            IdentTier tier;
            TriangulationTrack track;
            if (!MapTargetInfo.TryGetTier(unit, out tier, out track))
            {
                RestorePilotName(unit, infoPlayer);
                return;
            }

            if (infoName != null) infoName.text = MapTargetInfo.Label(unit, tier, track);
            if (tier == IdentTier.High)
            {
                if (!IsBoxHidden(track)) RestorePilotName(unit, infoPlayer);
                return;
            }

            if (infoPlayer != null && infoPlayer.enabled) infoPlayer.enabled = false;

            // Same condition as vanilla for the data rows; otherwise vanilla already wrote dashes
            FactionHQ mapHq = DynamicMap.i != null ? DynamicMap.i.HQ : null;
            if (mapHq == null || !mapHq.IsTargetPositionAccurate(unit, 20f)) return;

            Aircraft ownship = CombatHUD.i != null ? CombatHUD.i.aircraft : null;
            if (infoRange != null && ownship != null)
            {
                float dist = FastMath.Distance(ownship.GlobalPosition(), unit.GlobalPosition());
                infoRange.text = "RNG " + TargetIdentification.FormatRange(dist, tier);
            }

            if (tier == IdentTier.Low)
            {
                if (infoSpeed != null) infoSpeed.text = "SPD -";
                if (infoAlt != null) infoAlt.text = "ALT -";
                if (infoHeading != null) infoHeading.text = "HDG -";
                return;
            }

            // Mid: rounded values from the same data vanilla uses; ground units keep the vanilla "ALT -"
            if (infoSpeed != null) infoSpeed.text = "SPD " + TargetIdentification.FormatCoarseSpeed(unit.speed);
            if (infoHeading != null) infoHeading.text = "HDG " + TargetIdentification.FormatCoarseHeading(unit.transform.eulerAngles.y);
            if (infoAlt != null)
            {
                if (unit is Aircraft) infoAlt.text = "ALT " + TargetIdentification.FormatCoarseAltitude(unit.radarAlt, false);
                else if (unit is Missile) infoAlt.text = "ALT " + TargetIdentification.FormatCoarseAltitude(unit.GlobalPosition().y, false);
            }
        }
    }

    /// <summary>
    /// The row of a selected unit on the TGT page (vanilla: full unit name, grid of the true position and range)
    /// follows the same ident tiers as TargetMarker_Info_Patch. Below High: class or domain label with the Q grade,
    /// no grid, rounded range. Vanilla writes the name once (SetUnit), so the mod also writes the name back at High
    /// and with the mod off. Vanilla writes the grid and range on its slow refresh (Update sets lastUpdate).
    /// </summary>
    [HarmonyPatch(typeof(TargetListSelector_UnitItem))]
    public static class TargetListSelector_UnitItem_Patch
    {
        [HarmonyPatch("SetUnit")]
        [HarmonyPostfix]
        public static void SetUnit_Postfix(TargetListSelector_UnitItem __instance, TextMeshProUGUI ___unitName, TextMeshProUGUI ___unitDist)
        {
            try
            {
                Apply(__instance.unit, ___unitName, ___unitDist);
            }
            catch (Exception ex)
            {
                PatchErrorLog.Once("TargetListSelector_UnitItem_Patch.SetUnit", ex);
            }
        }

        [HarmonyPatch("Update")]
        [HarmonyPostfix]
        public static void Update_Postfix(TargetListSelector_UnitItem __instance, float ___lastUpdate,
            TextMeshProUGUI ___unitName, TextMeshProUGUI ___unitDist)
        {
            try
            {
                // Vanilla refresh frame only
                if (___lastUpdate != Time.timeSinceLevelLoad) return;
                Apply(__instance.unit, ___unitName, ___unitDist);
            }
            catch (Exception ex)
            {
                PatchErrorLog.Once("TargetListSelector_UnitItem_Patch.Update", ex);
            }
        }

        private static void Apply(Unit unit, TextMeshProUGUI unitName, TextMeshProUGUI unitDist)
        {
            if (unit == null) return;

            IdentTier tier;
            TriangulationTrack track;
            if (!MapTargetInfo.TryGetTier(unit, out tier, out track))
            {
                if (unitName != null && unitName.text != unit.unitName) unitName.text = unit.unitName;
                return;
            }

            if (unitName != null)
            {
                unitName.text = (tier == IdentTier.High)
                    ? MapTargetInfo.WithQuality(unit.unitName, track)
                    : MapTargetInfo.Label(unit, tier, track);
            }
            if (tier == IdentTier.High || unitDist == null) return;

            // Range to the known position, as vanilla; the vanilla grid comes from the true position
            string text = "?";
            FactionHQ mapHq = DynamicMap.i != null ? DynamicMap.i.HQ : null;
            Aircraft ownship = CombatHUD.i != null ? CombatHUD.i.aircraft : null;
            GlobalPosition known;
            if (mapHq != null && ownship != null && mapHq.TryGetKnownPosition(unit, out known))
                text = "? | " + TargetIdentification.FormatRange(FastMath.Distance(ownship.GlobalPosition(), known), tier);
            unitDist.text = text;
        }
    }

    /// <summary>
    /// The TGT page filters exclude a unit by its true type: TargetListSelector.CheckExclusions deselects it, stops its
    /// selection (map, HMD) and changes the color of its map icon, so a button told what the faction does not know.
    /// A filter now acts on a unit with a Q track only with the identification it needs: the type buttons (SAM, RDR...)
    /// from the High tier, the GND / BLD buttons from the Mid tier. AIR and SHP show in the Low label and stay vanilla.
    /// </summary>
    [HarmonyPatch(typeof(TargetListSelector_ToggleButton))]
    public static class TargetListSelector_ToggleButton_Patch
    {
        [HarmonyPatch("CheckDefinitions")]
        [HarmonyPostfix]
        public static void CheckDefinitions_Postfix(Unit u, ref bool __result)
        {
            try
            {
                if (!__result) return;
                IdentTier tier;
                TriangulationTrack track;
                if (MapTargetInfo.TryGetTier(u, out tier, out track) && tier != IdentTier.High) __result = false;
            }
            catch (Exception ex)
            {
                PatchErrorLog.Once("TargetListSelector_ToggleButton_Patch.CheckDefinitions", ex);
            }
        }

        [HarmonyPatch("CheckUnitTypes")]
        [HarmonyPostfix]
        public static void CheckUnitTypes_Postfix(Unit u, ref bool __result)
        {
            try
            {
                if (!__result || u is Aircraft || u is Ship) return;
                IdentTier tier;
                TriangulationTrack track;
                if (MapTargetInfo.TryGetTier(u, out tier, out track) && tier == IdentTier.Low) __result = false;
            }
            catch (Exception ex)
            {
                PatchErrorLog.Once("TargetListSelector_ToggleButton_Patch.CheckUnitTypes", ex);
            }
        }
    }

    /// <summary>
    /// The map hover tooltip of a unit (vanilla: full unit name and pilot name, exact speed / altitude / heading,
    /// radar and visual range circles) follows the same ident tiers as TargetMarker_Info_Patch.
    /// Below High: class or domain label with the Q grade, rounded (Mid) or "-" (Low) rows, no range circles
    /// (their size tells the unit type). Vanilla refreshes at refreshRate (sets lastRefresh), so the postfix writes only then.
    /// </summary>
    [HarmonyPatch(typeof(MapToolTip), "Refresh")]
    public static class MapToolTip_Refresh_Patch
    {
        [HarmonyPostfix]
        public static void Postfix(MapIcon icon, float ___lastRefresh, Text ___infoText, List<TooltipItem> ___listToolTips,
            Image ___circle1, Image ___circle2)
        {
            try
            {
                if (___lastRefresh != Time.unscaledTime) return;

                UnitMapIcon unitIcon = icon as UnitMapIcon;
                if (unitIcon == null) return;
                Unit unit = unitIcon.unit;

                IdentTier tier;
                TriangulationTrack track;
                if (!MapTargetInfo.TryGetTier(unit, out tier, out track)) return;

                if (___infoText != null) ___infoText.text = MapTargetInfo.Label(unit, tier, track);
                if (tier == IdentTier.High) return;

                if (___circle1 != null && ___circle1.enabled) ___circle1.enabled = false;
                if (___circle2 != null && ___circle2.enabled) ___circle2.enabled = false;

                if (___listToolTips == null) return;
                for (int i = 0; i < ___listToolTips.Count; i++)
                {
                    TooltipItem item = ___listToolTips[i];
                    if (item == null || item.label == null || !item.gameObject.activeSelf) continue;
                    string text = item.label.text;
                    if (string.IsNullOrEmpty(text) || text.EndsWith("-")) continue;

                    if (text.StartsWith("SPD "))
                        item.SetLabel(tier == IdentTier.Low ? "SPD -" : "SPD " + TargetIdentification.FormatCoarseSpeed(unit.speed));
                    else if (text.StartsWith("HDG "))
                        item.SetLabel(tier == IdentTier.Low ? "HDG -" : "HDG " + TargetIdentification.FormatCoarseHeading(unit.transform.eulerAngles.y));
                    else if (text.StartsWith("ALT "))
                    {
                        float alt = (unit is Aircraft) ? unit.radarAlt : unit.GlobalPosition().y;
                        item.SetLabel(tier == IdentTier.Low ? "ALT -" : "ALT " + TargetIdentification.FormatCoarseAltitude(alt, false));
                    }
                }
            }
            catch (Exception ex)
            {
                PatchErrorLog.Once("MapToolTip_Refresh_Patch", ex);
            }
        }
    }
}
