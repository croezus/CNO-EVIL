using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;
using CnoEvil.Core;
using CnoEvil.Config;
using CnoEvil.Components;

namespace CnoEvil.Patches
{
    public class RealisticRWRMapStrobe
    {
        public Image VectorImage;
        public Image UncertaintyCircleImage;
        public bool Seen; // Refreshed this map update
    }

    /// <summary>
    /// Map and minimap RWR strobes for every emitter the ownship receiver holds: a translucent zone of radius R(range)
    /// that contains the emitter at a random point fixed for the contact (never its center), and a cone from the ownship
    /// widening to the zone diameter. The threat state (search / track / missile guidance) only changes the color.
    /// Once the faction has located the emitter (its map icon is shown) the zone is dropped and the cone narrows
    /// onto the true position.
    /// </summary>
    [HarmonyPatch(typeof(DynamicMap))]
    public static class DynamicMapPatches
    {
        private static readonly Func<DynamicMap, GameObject> s_getRadarVisPrefab =
            FastReflection.CreateFieldGetter<DynamicMap, GameObject>("radarVisPrefab");
        private static readonly Dictionary<Unit, RealisticRWRMapStrobe> _strobes = new Dictionary<Unit, RealisticRWRMapStrobe>();
        private static readonly List<Unit> _staleStrobes = new List<Unit>();

        /// <summary>
        /// Where the emitter sits inside its zone: a horizontal (x, z) unit direction from the zone center and a
        /// fraction of the zone radius (sqrt-distributed, uniform over the disk). Kept across gaps between pings so a
        /// slow-sweeping radar does not move its zone on every sweep.
        /// </summary>
        private struct ZoneSeed
        {
            public Vector2 Dir;
            public float Frac;
            public float LastSeen;
        }

        private static readonly Dictionary<Unit, ZoneSeed> _zoneSeeds = new Dictionary<Unit, ZoneSeed>();
        private static readonly List<Unit> _staleSeeds = new List<Unit>();

        private const float DatalinkStrobeDuration = 3.5f;
        private const float ZoneSeedMemorySeconds = 60.0f;
        private const float MinConeScreenLength = 8.0f;

        private static bool StrobesEnabled()
        {
            if (!CnoEvilConfig.IsModActive) return false;
            ConfigEntry<bool> entry = DynamicMap.mapMaximized ? CnoEvilConfig.EnableMapRWRZones : CnoEvilConfig.EnableMinimapRWRZones;
            return entry == null || entry.Value;
        }

        [HarmonyPatch("ShowRadarPing")]
        [HarmonyPrefix]
        public static bool ShowRadarPing_Prefix()
        {
            // Strobes are built from PhysRWRReceiver in UpdateMap; the vanilla ping is an exact line to the emitter
            return !StrobesEnabled();
        }

        [HarmonyPatch("UpdateMap")]
        [HarmonyPostfix]
        public static void UpdateMap_Postfix(DynamicMap __instance)
        {
            try
            {
                if (__instance == null) return;
                MissileMapDispersionVisualizer.UpdateVisuals(__instance);
                JammingConeMapVisualizer.UpdateVisuals(__instance);

                if (!StrobesEnabled())
                {
                    ClearStrobes();
                    return;
                }

                Aircraft playerAircraft = CombatHUD.i != null ? CombatHUD.i.aircraft : null;
                PhysRWRReceiver rwr = (playerAircraft != null && !playerAircraft.disabled) ? PhysRWRReceiver.Get(playerAircraft) : null;
                if (rwr == null)
                {
                    ClearStrobes();
                    return;
                }

                UpdateStrobes(__instance, playerAircraft, rwr);
            }
            catch (Exception ex)
            {
                PatchErrorLog.Once("DynamicMapPatches.UpdateMap_Postfix", ex);
            }
        }

        private struct MapFrame
        {
            public DynamicMap Map;
            public Transform PlayerAnchor;
            public Vector3 PlayerFallbackLocalPos;
            public Vector3 PlayerWorldPos;
            public float LayerScale;
            public float MapRotationZ;
        }

        private static void UpdateStrobes(DynamicMap map, Aircraft playerAircraft, PhysRWRReceiver rwr)
        {
            MapFrame frame = new MapFrame();
            frame.Map = map;
            frame.PlayerWorldPos = playerAircraft.transform.position;

            UnitMapIcon playerIcon;
            DynamicMap.TryGetMapIcon(playerAircraft, out playerIcon);
            frame.PlayerAnchor = (playerIcon != null)
                ? (playerIcon.iconImage != null ? playerIcon.iconImage.transform : playerIcon.transform)
                : null;
            if (frame.PlayerAnchor == null)
            {
                Vector3 playerGlobal = playerAircraft.GlobalPosition().AsVector3();
                frame.PlayerFallbackLocalPos = new Vector3(playerGlobal.x * map.mapDisplayFactor, playerGlobal.z * map.mapDisplayFactor, 0f);
            }

            frame.LayerScale = (map.iconLayer != null && map.iconLayer.transform.lossyScale.y > 0.0001f)
                ? map.iconLayer.transform.lossyScale.y
                : 1.0f;
            // Map rotation around Z (0 when maximized; aircraft heading on the track-up minimap)
            frame.MapRotationZ = (map.mapImage != null) ? map.mapImage.transform.eulerAngles.z : 0f;

            foreach (var kvp in _strobes) kvp.Value.Seen = false;

            FactionHQ playerHq = RWRTriangulationProcessor.GetPlayerFactionHQ();
            float now = Time.time; // PhysRWRReceiver ping timestamps use Time.time
            float persistence = CnoEvilConfig.EffectiveRwrStrobePersistence;

            foreach (RwrThreatEntry threat in rwr.ActiveThreats)
            {
                Unit emitter = threat.EmitterUnit;
                if (emitter == null || emitter.disabled) continue;
                float age = now - threat.LastPingTime;
                if (age >= persistence) continue;

                RealisticRWRMapStrobe strobe = GetOrCreateStrobe(map, emitter);
                if (strobe == null) continue;
                strobe.Seen = true;

                LayoutZone(strobe, frame, emitter, emitter.transform.position, now,
                    UnitMapIcon_UpdateIcon_Patch.IsLocatedThreat(playerHq, emitter));
                ApplyThreatColor(strobe, threat.State, Mathf.Clamp01(1.0f - age / persistence));
            }

            bool showAllDl = CnoEvilConfig.EnableDatalinkStrobes != null && CnoEvilConfig.EnableDatalinkStrobes.Value;
            if (showAllDl)
            {
                foreach (DatalinkThreatEntry dl in rwr.ActiveDatalinkThreats)
                {
                    Unit threatUnit = dl.ThreatUnit;
                    if (threatUnit == null || threatUnit.disabled) continue;
                    float age = now - dl.LastPingTime;
                    if (age >= DatalinkStrobeDuration) continue;

                    RealisticRWRMapStrobe strobe = GetOrCreateStrobe(map, threatUnit);
                    if (strobe == null || strobe.Seen) continue; // The ownship receiver's own strobe takes precedence
                    strobe.Seen = true;

                    // Zone around the position reported by the allied source; a located threat points at its icon
                    bool located = UnitMapIcon_UpdateIcon_Patch.IsLocatedThreat(playerHq, threatUnit);
                    LayoutZone(strobe, frame, threatUnit, located ? threatUnit.transform.position : dl.EstimatedPosition, now, located);
                    ApplyDatalinkColor(strobe, Mathf.Clamp01(1.0f - age / DatalinkStrobeDuration));
                }
            }

            _staleStrobes.Clear();
            foreach (var kvp in _strobes)
            {
                if (!kvp.Value.Seen) _staleStrobes.Add(kvp.Key);
            }
            for (int i = 0; i < _staleStrobes.Count; i++)
            {
                DestroyStrobe(_strobes[_staleStrobes[i]]);
                _strobes.Remove(_staleStrobes[i]);
            }

            _staleSeeds.Clear();
            foreach (var kvp in _zoneSeeds)
            {
                if (kvp.Key == null || kvp.Key.disabled || now - kvp.Value.LastSeen > ZoneSeedMemorySeconds) _staleSeeds.Add(kvp.Key);
            }
            for (int i = 0; i < _staleSeeds.Count; i++)
            {
                _zoneSeeds.Remove(_staleSeeds[i]);
            }
        }

        /// <summary>
        /// Zone radius R(range): linear from MapRWRZoneNearRadius_m at MapRWRZoneNearRange_m
        /// to MapRWRZoneFarRadius_m at MapRWRZoneFarRange_m, held constant outside that span.
        /// </summary>
        private static float GetZoneRadius(float rangeMeters)
        {
            float nearRange = CnoEvilConfig.MapRWRZoneNearRange_m.Value;
            float farRange = Mathf.Max(nearRange + 1.0f, CnoEvilConfig.MapRWRZoneFarRange_m.Value);
            float t = Mathf.Clamp01((rangeMeters - nearRange) / (farRange - nearRange));
            return Mathf.Max(1.0f, Mathf.Lerp(CnoEvilConfig.MapRWRZoneNearRadius_m.Value, CnoEvilConfig.MapRWRZoneFarRadius_m.Value, t));
        }

        private static ZoneSeed GetZoneSeed(Unit emitter, float now)
        {
            ZoneSeed seed;
            if (!_zoneSeeds.TryGetValue(emitter, out seed))
            {
                float angle = UnityEngine.Random.Range(0.0f, 2.0f * Mathf.PI);
                seed.Dir = new Vector2(Mathf.Cos(angle), Mathf.Sin(angle));
                seed.Frac = Mathf.Sqrt(UnityEngine.Random.value);
            }
            seed.LastSeen = now;
            _zoneSeeds[emitter] = seed;
            return seed;
        }

        /// <summary>
        /// Places the zone so the emitter sits at its seeded point inside it, and the cone from the ownship
        /// to the zone, as wide as the zone at its center. Inside the zone only the zone is drawn.
        /// A located emitter (map icon shown) gets no zone, only a narrow cone to its true position.
        /// </summary>
        private static void LayoutZone(RealisticRWRMapStrobe strobe, MapFrame frame, Unit emitter, Vector3 emitterWorldPos, float now, bool located)
        {
            Vector3 toEmitter = emitterWorldPos - frame.PlayerWorldPos;
            toEmitter.y = 0.0f;

            if (located)
            {
                Image circle = strobe.UncertaintyCircleImage;
                if (circle != null && circle.gameObject.activeSelf) circle.gameObject.SetActive(false);

                float emitterLength = toEmitter.magnitude * frame.Map.mapDisplayFactor;
                LayoutCone(strobe, frame, toEmitter, emitterLength, MinZoneDiameter(frame), emitterLength * frame.LayerScale >= MinConeScreenLength);
                return;
            }

            float radius = GetZoneRadius(toEmitter.magnitude);

            ZoneSeed seed = GetZoneSeed(emitter, now);
            Vector3 center = emitterWorldPos - new Vector3(seed.Dir.x, 0.0f, seed.Dir.y) * (seed.Frac * radius);
            float diameter = LayoutCircle(strobe, frame, center, radius);

            Vector3 toCenter = center - frame.PlayerWorldPos;
            toCenter.y = 0.0f;
            float distance = toCenter.magnitude;
            float length = distance * frame.Map.mapDisplayFactor;
            bool showCone = distance > radius && length * frame.LayerScale >= MinConeScreenLength;
            LayoutCone(strobe, frame, toCenter, length, diameter, showCone);
        }

        /// <summary>
        /// Anchors the cone apex at the ownship icon and points it along a world direction.
        /// </summary>
        private static void LayoutCone(RealisticRWRMapStrobe strobe, MapFrame frame, Vector3 worldDir, float length, float baseWidth, bool show)
        {
            GameObject go = strobe.VectorImage.gameObject;
            if (go.activeSelf != show) go.SetActive(show);
            if (!show) return;

            Transform t = strobe.VectorImage.transform;
            if (frame.PlayerAnchor != null)
            {
                t.position = frame.PlayerAnchor.position;
            }
            else
            {
                t.localPosition = frame.PlayerFallbackLocalPos;
            }

            float baseAngle = -Mathf.Atan2(worldDir.x, worldDir.z) * Mathf.Rad2Deg;
            t.eulerAngles = new Vector3(0f, 0f, baseAngle + frame.MapRotationZ);
            t.localScale = Vector3.one;

            RectTransform rt = strobe.VectorImage.rectTransform;
            rt.anchorMin = new Vector2(0.5f, 0.5f);
            rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.pivot = new Vector2(0.5f, 0.0f);
            rt.sizeDelta = new Vector2(baseWidth, length);
        }

        // Smallest zone (and located-emitter cone base) in icon-layer units, so it stays visible when zoomed out
        private static float MinZoneDiameter(MapFrame frame)
        {
            return (DynamicMap.mapMaximized ? 24.0f : 16.0f) / frame.LayerScale;
        }

        /// <summary>
        /// Shows the zone at a world position; returns its diameter in icon-layer units
        /// (floored so a small zone stays visible when zoomed out).
        /// </summary>
        private static float LayoutCircle(RealisticRWRMapStrobe strobe, MapFrame frame, Vector3 centerWorld, float radiusMeters)
        {
            float diameter = Mathf.Max(2.0f * radiusMeters * frame.Map.mapDisplayFactor, MinZoneDiameter(frame));

            Image circle = strobe.UncertaintyCircleImage;
            if (circle == null) return diameter;
            if (!circle.gameObject.activeSelf) circle.gameObject.SetActive(true);

            Vector3 mapPos = GlobalPositionExtensions.ToGlobalPosition(centerWorld).AsVector3() * frame.Map.mapDisplayFactor;
            circle.transform.localPosition = new Vector3(mapPos.x, mapPos.z, 0f);
            circle.rectTransform.sizeDelta = new Vector2(diameter, diameter);
            circle.transform.localScale = Vector3.one;
            return diameter;
        }

        private static void ApplyThreatColor(RealisticRWRMapStrobe strobe, RwrThreatState state, float normLife)
        {
            Color c;
            switch (state)
            {
                case RwrThreatState.MissileGuidance:
                    float missileAlpha = CnoEvilConfig.RWRMissileStrobeOpacity != null ? Mathf.Clamp(CnoEvilConfig.RWRMissileStrobeOpacity.Value, 0.01f, 1.0f) : 0.75f;
                    bool mFlash = (Mathf.Sin(Time.time * 26.0f) > 0.0f);
                    c = mFlash
                        ? new Color(1.0f, 0.15f, 0.15f, missileAlpha)
                        : new Color(0.85f, 0.08f, 0.08f, missileAlpha * 0.5f * normLife);
                    break;

                case RwrThreatState.Track:
                    float trackAlpha = CnoEvilConfig.RWRTrackStrobeOpacity != null ? Mathf.Clamp(CnoEvilConfig.RWRTrackStrobeOpacity.Value, 0.01f, 1.0f) : 0.40f;
                    c = new Color(1.0f, 0.55f, 0.0f, trackAlpha * Mathf.Lerp(0.40f, 1.0f, normLife));
                    break;

                case RwrThreatState.Search:
                default:
                    float searchAlpha = CnoEvilConfig.RWRSearchStrobeOpacity != null ? Mathf.Clamp(CnoEvilConfig.RWRSearchStrobeOpacity.Value, 0.01f, 1.0f) : 0.10f;
                    c = new Color(1.0f, 0.85f, 0.15f, searchAlpha * normLife);
                    break;
            }
            ApplyColor(strobe, c, normLife);
        }

        private static void ApplyDatalinkColor(RealisticRWRMapStrobe strobe, float normLife)
        {
            float dlAlpha = CnoEvilConfig.RWRDatalinkStrobeOpacity != null ? Mathf.Clamp(CnoEvilConfig.RWRDatalinkStrobeOpacity.Value, 0.01f, 1.0f) : 0.35f;
            bool dlFlash = (Mathf.Sin(Time.time * 20.0f) > 0.0f);
            Color c = dlFlash
                ? new Color(0.2f, 0.85f, 1.0f, dlAlpha)
                : new Color(0.1f, 0.5f, 0.8f, dlAlpha * 0.5f * normLife);
            ApplyColor(strobe, c, normLife);
        }

        private static void ApplyColor(RealisticRWRMapStrobe strobe, Color c, float normLife)
        {
            strobe.VectorImage.color = c;
            if (strobe.UncertaintyCircleImage != null && strobe.UncertaintyCircleImage.gameObject.activeSelf)
            {
                float zoneAlpha = CnoEvilConfig.MapRWRZoneOpacity != null ? Mathf.Clamp(CnoEvilConfig.MapRWRZoneOpacity.Value, 0.01f, 1.0f) : 0.10f;
                strobe.UncertaintyCircleImage.color = new Color(c.r, c.g, c.b, zoneAlpha * Mathf.Lerp(0.5f, 1.0f, normLife));
            }
        }

        private static RealisticRWRMapStrobe GetOrCreateStrobe(DynamicMap map, Unit emitter)
        {
            RealisticRWRMapStrobe strobe;
            if (_strobes.TryGetValue(emitter, out strobe))
            {
                if (strobe.VectorImage != null) return strobe;
                DestroyStrobe(strobe);
                _strobes.Remove(emitter);
            }

            GameObject prefab = (s_getRadarVisPrefab != null) ? s_getRadarVisPrefab(map) : null;
            if (prefab == null || map.iconLayer == null) return null;

            GameObject go = UnityEngine.Object.Instantiate(prefab, map.iconLayer.transform);
            Image img = go.GetComponent<Image>();
            if (img == null)
            {
                UnityEngine.Object.Destroy(go);
                return null;
            }
            img.sprite = RWRConeGraphic.GetOrCreateConeSprite();
            img.type = Image.Type.Simple;
            img.raycastTarget = false;

            GameObject circleGo = UnityEngine.Object.Instantiate(prefab, map.iconLayer.transform);
            circleGo.name = "RWR_UncertaintyCircle";
            Image circleImg = circleGo.GetComponent<Image>();
            if (circleImg != null)
            {
                circleImg.sprite = RWRConeGraphic.GetOrCreateUncertaintyCircleSprite();
                circleImg.type = Image.Type.Simple;
                circleImg.preserveAspect = true;
                circleImg.raycastTarget = false;
                RectTransform cRt = circleImg.rectTransform;
                cRt.anchorMin = new Vector2(0.5f, 0.5f);
                cRt.anchorMax = new Vector2(0.5f, 0.5f);
                cRt.pivot = new Vector2(0.5f, 0.5f);
            }
            circleGo.SetActive(false);

            strobe = new RealisticRWRMapStrobe { VectorImage = img, UncertaintyCircleImage = circleImg };
            _strobes[emitter] = strobe;
            return strobe;
        }

        private static void DestroyStrobe(RealisticRWRMapStrobe strobe)
        {
            if (strobe.VectorImage != null) UnityEngine.Object.Destroy(strobe.VectorImage.gameObject);
            if (strobe.UncertaintyCircleImage != null) UnityEngine.Object.Destroy(strobe.UncertaintyCircleImage.gameObject);
        }

        [HarmonyPatch("ClearMap")]
        [HarmonyPostfix]
        public static void ClearMap_Postfix()
        {
            ClearAll();
        }

        [HarmonyPatch("OnDestroy")]
        [HarmonyPostfix]
        public static void OnDestroy_Postfix()
        {
            ClearAll();
        }

        private static void ClearStrobes()
        {
            foreach (var kvp in _strobes) DestroyStrobe(kvp.Value);
            _strobes.Clear();
            _staleStrobes.Clear();
        }

        public static void ClearAll()
        {
            MissileMapDispersionVisualizer.ClearAll();
            JammingConeMapVisualizer.ClearAll();
            ClearStrobes();
            _zoneSeeds.Clear();
            _staleSeeds.Clear();
            UnitMapIcon_UpdateIcon_Patch.ClearIconState();
        }
    }

    /// <summary>
    /// Map icons of hostile tracked threats (ThreatClassification) show only what the player's faction knows:
    /// radar / visual contact -> vanilla icon at the live position;
    /// tracking quality at or above LocatedQualityThreshold -> vanilla icon at the vanilla known position
    /// (the locate point when no contact refreshes it, RadarLocator_OnRadarWarning_Patch), dimmed without active
    /// contact (never moved to a passive fix estimate, so it does not jump);
    /// below the threshold -> no icon (HideUnlocatedThreatIcons).
    /// </summary>
    [HarmonyPatch(typeof(UnitMapIcon), "UpdateIcon", new Type[] { typeof(float), typeof(float), typeof(Transform), typeof(bool) })]
    public static class UnitMapIcon_UpdateIcon_Patch
    {
        private const float LostContactAlpha = 0.5f;
        // Icons dimmed by this patch; vanilla only rewrites the color while an icon flashes
        private static readonly HashSet<Image> s_dimmedIcons = new HashSet<Image>();
        // Icons hidden by this patch; DynamicMap_SelectFromMap_Patch keeps them out of map selection
        private static readonly HashSet<Image> s_hiddenIcons = new HashSet<Image>();

        public static HashSet<Image> HiddenIcons { get { return s_hiddenIcons; } }

        [HarmonyPostfix]
        public static void Postfix(UnitMapIcon __instance)
        {
            try
            {
                Image icon = __instance.iconImage;
                Unit unit = __instance.unit;
                if (icon == null || unit == null || unit is Missile) return;

                bool modActive = CnoEvilConfig.IsModActive;
                FactionHQ playerHq = modActive ? RWRTriangulationProcessor.GetPlayerFactionHQ() : null;

                // Friendly units and types the mod does not track (ThreatClassification) stay vanilla
                if (!modActive || !ThreatClassification.IsTrackedThreat(unit, playerHq))
                {
                    ShowIcon(icon, false);
                    return;
                }

                // Located: vanilla keeps the icon at its known position, so it never jumps between estimates
                TriangulationTrack track = RWRTriangulationProcessor.GetTrack(playerHq, unit);
                if (IsLocated(track))
                {
                    ShowIcon(icon, !track.HasRecentPreciseContact && !track.IsActivelyDetected);
                    return;
                }

                bool hideIcons = CnoEvilConfig.HideUnlocatedThreatIcons == null || CnoEvilConfig.HideUnlocatedThreatIcons.Value;
                if (hideIcons) HideIcon(icon);
                else ShowIcon(icon, false);
            }
            catch (Exception ex)
            {
                PatchErrorLog.Once("UnitMapIcon_UpdateIcon_Patch", ex);
            }
        }

        /// <summary>
        /// The faction has located the target: recent radar / visual contact, or tracking quality at or above
        /// LocatedQualityThreshold. Its icon then shows the vanilla known position and its RWR strobe has no zone.
        /// </summary>
        public static bool IsLocated(TriangulationTrack track)
        {
            if (track == null) return false;
            if (track.HasRecentPreciseContact) return true;
            float mapThreshold = (CnoEvilConfig.LocatedQualityThreshold != null)
                ? CnoEvilConfig.LocatedQualityThreshold.Value
                : 0.75f;
            return track.TrackingQuality >= mapThreshold;
        }

        /// <summary>
        /// IsLocated for a hostile tracked threat of the player's faction.
        /// </summary>
        public static bool IsLocatedThreat(FactionHQ playerHq, Unit unit)
        {
            if (playerHq == null || unit == null) return false;
            if (!ThreatClassification.IsTrackedThreat(unit, playerHq)) return false;
            return IsLocated(RWRTriangulationProcessor.GetTrack(playerHq, unit));
        }

        public static void ClearIconState()
        {
            s_dimmedIcons.Clear();
            s_hiddenIcons.Clear();
        }

        private static void ShowIcon(Image icon, bool dimmed)
        {
            if (!icon.enabled) icon.enabled = true;
            s_hiddenIcons.Remove(icon);
            if (dimmed == s_dimmedIcons.Contains(icon)) return;

            Color col = icon.color;
            col.a = dimmed ? LostContactAlpha : 1.0f;
            icon.color = col;
            if (dimmed) s_dimmedIcons.Add(icon);
            else s_dimmedIcons.Remove(icon);
        }

        private static void HideIcon(Image icon)
        {
            if (icon.enabled) icon.enabled = false;
            s_hiddenIcons.Add(icon);
        }
    }

    /// <summary>
    /// The map "Select" button (DynamicMap.SelectFromMap) picks the icon nearest the cursor among those with
    /// iconImage.raycastTarget set, without checking that the icon is shown, so it selected units hidden by
    /// UnitMapIcon_UpdateIcon_Patch. Their raycastTarget is cleared for the duration of the call only: vanilla uses
    /// it as its own selected / highlighted flag (MapIcon.SelectIcon, HighlightIcon, DeselectIcon).
    /// </summary>
    [HarmonyPatch(typeof(DynamicMap), "SelectFromMap")]
    public static class DynamicMap_SelectFromMap_Patch
    {
        private static readonly List<Image> s_suppressed = new List<Image>();

        [HarmonyPrefix]
        public static void Prefix()
        {
            try
            {
                s_suppressed.Clear();
                UnitMapIcon_UpdateIcon_Patch.HiddenIcons.RemoveWhere(icon => icon == null); // Destroyed icons
                foreach (Image icon in UnitMapIcon_UpdateIcon_Patch.HiddenIcons)
                {
                    if (icon == null || icon.enabled || !icon.raycastTarget) continue;
                    icon.raycastTarget = false;
                    s_suppressed.Add(icon);
                }
            }
            catch (Exception ex)
            {
                PatchErrorLog.Once("DynamicMap_SelectFromMap_Patch.Prefix", ex);
            }
        }

        [HarmonyFinalizer]
        public static void Finalizer()
        {
            try
            {
                for (int i = 0; i < s_suppressed.Count; i++)
                {
                    if (s_suppressed[i] != null) s_suppressed[i].raycastTarget = true;
                }
                s_suppressed.Clear();
            }
            catch (Exception ex)
            {
                PatchErrorLog.Once("DynamicMap_SelectFromMap_Patch.Finalizer", ex);
            }
        }
    }

    /// <summary>
    /// A unit whose map icon is hidden by UnitMapIcon_UpdateIcon_Patch cannot be selected through its icon by any caller.
    /// </summary>
    [HarmonyPatch(typeof(UnitMapIcon), "ClickIcon")]
    public static class UnitMapIcon_ClickIcon_Patch
    {
        [HarmonyPrefix]
        public static bool Prefix(UnitMapIcon __instance)
        {
            try
            {
                Image icon = __instance.iconImage;
                return icon == null || icon.enabled || !UnitMapIcon_UpdateIcon_Patch.HiddenIcons.Contains(icon);
            }
            catch (Exception ex)
            {
                PatchErrorLog.Once("UnitMapIcon_ClickIcon_Patch", ex);
                return true;
            }
        }
    }
}
