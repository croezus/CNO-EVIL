using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using TMPro;
using CnoEvil.Core;
using CnoEvil.Config;

namespace CnoEvil.Patches
{
    [HarmonyPatch(typeof(TargetCam))]
    public static class TargetCamPatches
    {
        // Vanilla clamps targetFOV to 0.25 deg (GetMag = 10 / fieldOfView, so x40); Update lerps the camera
        // FOV toward targetFOV, so raising its floor caps the zoom at TargetCamMaxZoom
        [HarmonyPatch("SetTargetCam")]
        [HarmonyPostfix]
        public static void SetTargetCam_Postfix(ref float ___targetFOV)
        {
            if (!CnoEvilConfig.IsModActive) return;

            float maxZoom = CnoEvilConfig.TargetCamMaxZoom != null ? Mathf.Clamp(CnoEvilConfig.TargetCamMaxZoom.Value, 1f, 40f) : 15f;
            float minFOV = 10f / maxZoom;
            if (___targetFOV < minFOV) ___targetFOV = minFOV;
        }
    }

    /// <summary>
    /// The target camera info (vanilla: full unit name, pilot, exact range, grid, and true heading, altitude and
    /// speed) follows the faction's tracking quality Q with the HMD tiers (TargetIdentification).
    /// Low: HMD label and Q grade, range to 10 km, all other rows "-". Mid: class label, range to 2 km,
    /// heading / altitude / speed rounded, no grid and no closure speed. High: vanilla values, full name and Q grade.
    /// Vanilla writes the texts in a 0.1 s slow update, so the postfix overwrite does not flicker.
    /// Units without a track (friendly, missiles, non-threat types) stay vanilla.
    /// </summary>
    [HarmonyPatch(typeof(TargetScreenUI), "UpdateTargetInfo")]
    public static class TargetScreenUI_UpdateTargetInfo_Patch
    {
        [HarmonyPostfix]
        public static void Postfix(List<Unit> ___targetList, TargetCam ___targetCam, bool ___displayingInfo,
            TextMeshProUGUI ___typeText, TextMeshProUGUI ___pilotText, TextMeshProUGUI ___distance, TextMeshProUGUI ___gridText,
            TextMeshProUGUI ___heading, TextMeshProUGUI ___altitude, TextMeshProUGUI ___rel_altitude,
            TextMeshProUGUI ___speed, TextMeshProUGUI ___rel_speed)
        {
            try
            {
                if (!CnoEvilConfig.IsModActive) return;
                if (CnoEvilConfig.EnableTargetCamIdentDegradation == null || !CnoEvilConfig.EnableTargetCamIdentDegradation.Value) return;
                if (!___displayingInfo || ___targetList == null || ___targetList.Count == 0 || ___targetCam == null) return;

                Aircraft ownship = CombatHUD.i != null ? CombatHUD.i.aircraft : null;
                if (ownship == null) return;

                FactionHQ hq = CombatHUDPatches.GetPlayerHQ();
                if (hq == null) return;

                // Read-only lookup: GetTrack would record a radar illumination as a side effect
                Dictionary<Unit, TriangulationTrack> tracks = RWRTriangulationProcessor.GetTrackDictionary(hq);
                if (tracks == null) return;

                // Several targets: the worst tier among the tracked ones sets range and grid
                bool anyTracked = false;
                IdentTier tier = IdentTier.High;
                TriangulationTrack firstTrack = null;
                for (int i = 0; i < ___targetList.Count; i++)
                {
                    Unit unit = ___targetList[i];
                    if (!ThreatClassification.HasQualityTrack(unit, hq)) continue;
                    TriangulationTrack track;
                    if (!tracks.TryGetValue(unit, out track) || track == null) continue;

                    IdentTier unitTier = TargetIdentification.GetTier(track);
                    if (!anyTracked || unitTier < tier) tier = unitTier;
                    if (i == 0) firstTrack = track;
                    anyTracked = true;
                }
                if (!anyTracked) return;

                if (tier != IdentTier.High)
                {
                    if (___distance != null) ___distance.text = "RNG " + TargetIdentification.FormatRange(___targetCam.GetDist(), tier);
                    if (___gridText != null) ___gridText.text = "GRID: -";
                }

                // Single target only: vanilla shows "{n} targets" and dashes for several
                if (___targetList.Count != 1 || firstTrack == null) return;
                Unit target = ___targetList[0];

                if (___typeText != null)
                {
                    string label = (tier == IdentTier.High) ? ___typeText.text : TargetIdentification.GetLabel(target, tier);
                    string quality = TargetIdentification.FormatQuality(firstTrack.TrackingQuality);
                    ___typeText.text = (quality.Length > 0) ? label + " " + quality : label;
                }
                if (tier == IdentTier.High) return;

                if (___pilotText != null && ___pilotText.gameObject.activeSelf) ___pilotText.gameObject.SetActive(false);

                // Same condition as vanilla for the flight data rows; otherwise vanilla already wrote dashes
                bool flightData = (target is Aircraft || target is Missile) && hq.IsTargetPositionAccurate(target, 20f);
                if (!flightData) return;

                if (tier == IdentTier.Low)
                {
                    if (___heading != null) ___heading.text = "HDG -";
                    if (___altitude != null) ___altitude.text = "ALT -";
                    if (___rel_altitude != null) ___rel_altitude.text = "REL -";
                    if (___speed != null) ___speed.text = "SPD -";
                    if (___rel_speed != null) ___rel_speed.text = "REL -";
                    return;
                }

                // Mid: rounded values from the same data vanilla uses; closure speed needs a precise track
                GlobalPosition targetPos = target.GlobalPosition();
                float relAlt = targetPos.y - ownship.GlobalPosition().y;
                if (___heading != null) ___heading.text = "HDG " + TargetIdentification.FormatCoarseHeading(target.transform.eulerAngles.y);
                if (___altitude != null) ___altitude.text = "ALT " + TargetIdentification.FormatCoarseAltitude(targetPos.y, false);
                if (___rel_altitude != null) ___rel_altitude.text = "REL " + TargetIdentification.FormatCoarseAltitude(relAlt, true);
                if (___speed != null) ___speed.text = "SPD " + TargetIdentification.FormatCoarseSpeed(target.speed);
                if (___rel_speed != null) ___rel_speed.text = "REL -";
            }
            catch (Exception ex)
            {
                PatchErrorLog.Once("TargetScreenUI_UpdateTargetInfo_Patch.Postfix", ex);
            }
        }
    }
}
