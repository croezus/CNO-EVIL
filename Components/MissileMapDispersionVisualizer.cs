using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using CnoEvil.Config;
using CnoEvil.Core;
using CnoEvil.Patches;

namespace CnoEvil.Components
{
    /// <summary>
    /// Tactical Map (Key M, DynamicMap) visualizer for missile midcourse track uncertainty dispersion.
    /// Provides real-time visual confirmation of unguided munition flight paths:
    /// 1. Fly-to-Waypoint Guidance Line: Line connecting missile to the displaced aimpoint (where munition steers).
    /// 2. Displaced Waypoint Marker: Crosshair / diamond marker at displaced coordinates.
    /// 3. CEP Offset Vector: Dashed line connecting the faction-known target position to the displaced aimpoint.
    /// 4. Terminal Seeker Lock Transition: Snaps line to real target and switches to Crimson Red upon lock.
    /// Zero heap allocations per frame, pre-allocated object pool of 8 visual slots.
    /// </summary>
    public static class MissileMapDispersionVisualizer
    {
        private const int POOL_SIZE = 8;

        private class MissileMapVisualSlot
        {
            public GameObject Root;

            // 1. Guidance Line (Missile to Aimpoint or True Target)
            public Image FlyToLine;
            public RectTransform FlyToLineRt;

            // 2. Displaced Waypoint Marker
            public Image AimMarker;
            public RectTransform AimMarkerRt;

            // 3. CEP Offset Vector (True Target to Aimpoint)
            public Image OffsetLine;
            public RectTransform OffsetLineRt;

            public void SetActive(bool active)
            {
                if (Root != null && Root.activeSelf != active)
                {
                    Root.SetActive(active);
                }
            }
        }

        private static readonly MapOverlayPool<MissileMapVisualSlot> _slotPool =
            new MapOverlayPool<MissileMapVisualSlot>(POOL_SIZE, CreateSlot, slot => slot != null ? slot.Root : null);

        /// <summary>
        /// Updates and renders all active missile dispersion visuals on the dynamic map.
        /// Hooked directly into DynamicMapPatches.UpdateMap_Postfix.
        /// </summary>
        public static void UpdateVisuals(DynamicMap map)
        {
            try
            {
                if (!CnoEvilConfig.IsModActive)
                {
                    _slotPool.HideFrom(0);
                    return;
                }

                bool enabled = CnoEvilConfig.ShowMissileAimpointOnMap.Value;
                if (!enabled || map == null || map.iconLayer == null)
                {
                    _slotPool.HideFrom(0);
                    return;
                }

                List<Missile> activeMissiles = DatalinkNetwork.ActiveMissiles;
                if (activeMissiles == null || activeMissiles.Count == 0)
                {
                    _slotPool.HideFrom(0);
                    return;
                }

                _slotPool.Ensure(map);

                Aircraft playerAircraft = (CombatHUD.i != null) ? CombatHUD.i.aircraft : null;
                bool playerOnly = CnoEvilConfig.MissileAimpointPlayerOnly.Value;

                float layerScale = (map.iconLayer.transform.lossyScale.y > 0.0001f)
                    ? map.iconLayer.transform.lossyScale.y
                    : 1.0f;

                float mapZ = (map.mapImage != null) ? map.mapImage.transform.eulerAngles.z : 0f;
                float mapFactor = map.mapDisplayFactor;

                int slotIndex = 0;

                for (int i = 0; i < activeMissiles.Count; i++)
                {
                    if (slotIndex >= POOL_SIZE) break;

                    Missile missile = activeMissiles[i];
                    if (missile == null || missile.disabled) continue;

                    // Filter by owner aircraft if player-only mode is active
                    if (playerOnly)
                    {
                        if (playerAircraft == null || playerAircraft.disabled) continue;
                        if (missile.owner != playerAircraft) continue;
                    }
                    else
                    {
                        // Exclude enemy missiles from player diagnostic visualizer
                        if (playerAircraft != null && missile.NetworkHQ != null && missile.NetworkHQ != playerAircraft.NetworkHQ)
                        {
                            continue;
                        }
                    }

                    MissileMapVisualSlot slot = _slotPool[slotIndex];
                    if (slot == null || slot.Root == null) continue;

                    // 1. Resolve missile current map coordinates
                    Vector3 missileGlobal = missile.GlobalPosition().AsVector3();
                    Vector3 missileLocalPos = new Vector3(missileGlobal.x * mapFactor, missileGlobal.z * mapFactor, 0f);

                    // 2. Resolve target and dispersion
                    Unit target = SeekerDispersionHelper.GetTargetForMissile(missile);

                    // 3. Resolve true target position and aimpoint
                    // Target position as the missile's faction knows it (never the true position)
                    Vector3 targetLocalPos = Vector3.zero;
                    bool hasTarget = false;
                    bool targetAlive = (target != null && !target.disabled);
                    if (targetAlive)
                    {
                        FactionHQ hq = SeekerDispersionHelper.GetMissileFaction(missile);
                        Dictionary<Unit, TriangulationTrack> tracks = RWRTriangulationProcessor.GetTrackDictionary(hq);
                        TriangulationTrack track;
                        GlobalPosition estimate;
                        float estimateError;
                        if (tracks != null && tracks.TryGetValue(target, out track) &&
                            RWRTriangulationProcessor.TryGetFactionEstimate(track, target, out estimate, out estimateError))
                        {
                            Vector3 estGlobal = estimate.AsVector3();
                            targetLocalPos = new Vector3(estGlobal.x * mapFactor, estGlobal.z * mapFactor, 0f);
                            hasTarget = true;
                        }
                    }

                    Vector3 aimWorld;
                    if (!SeekerDispersionHelper.TryGetGuidanceAimPoint(missile, targetAlive ? target : null, out aimWorld))
                    {
                        continue;
                    }
                    Vector3 aimGlobal = aimWorld.ToGlobalPosition().AsVector3();
                    Vector3 aimLocalPos = new Vector3(aimGlobal.x * mapFactor, aimGlobal.z * mapFactor, 0f);

                    // 4. Check seeker terminal acquisition status
                    MissileSeeker seeker = SeekerDispersionHelper.GetSeeker(missile);
                    bool isIR = seeker is IRSeeker;
                    bool isSARH = seeker is SARHSeeker;
                    bool isLocked = SeekerDispersionHelper.IsSeekerTerminalLocked(missile);

                    slot.SetActive(true);

                    if (isLocked)
                    {
                        // Terminal lock achieved: guidance snaps directly to real target in Crimson Red
                        // (the missile's own seeker sees the target, so the live position is sensor knowledge)
                        Vector3 destPos = aimLocalPos;
                        if (targetAlive)
                        {
                            Vector3 liveGlobal = target.GlobalPosition().AsVector3();
                            destPos = new Vector3(liveGlobal.x * mapFactor, liveGlobal.z * mapFactor, 0f);
                        }
                        PositionLine(slot.FlyToLineRt, missileLocalPos, destPos, 3.0f / layerScale, mapZ);
                        slot.FlyToLine.color = new Color(1.0f, 0.15f, 0.15f, 0.95f); // Crimson Red

                        // Hide unguided CEP visual cues once locked
                        if (slot.AimMarker.gameObject.activeSelf) slot.AimMarker.gameObject.SetActive(false);
                        if (slot.OffsetLine.gameObject.activeSelf) slot.OffsetLine.gameObject.SetActive(false);
                    }
                    else
                    {
                        // Midcourse unguided / datalink guidance towards displaced aimpoint
                        PositionLine(slot.FlyToLineRt, missileLocalPos, aimLocalPos, 2.5f / layerScale, mapZ);
                        slot.FlyToLine.color = new Color(1.0f, 0.70f, 0.15f, 0.88f); // Amber / Gold

                        // 1. Waypoint Marker at displaced aimpoint (omitted for direct IR line-of-sight and SARH continuous beam)
                        if (!isIR && !isSARH)
                        {
                            if (!slot.AimMarker.gameObject.activeSelf) slot.AimMarker.gameObject.SetActive(true);
                            slot.AimMarkerRt.localPosition = aimLocalPos;
                            slot.AimMarkerRt.sizeDelta = new Vector2(22.0f / layerScale, 22.0f / layerScale);
                            slot.AimMarkerRt.localEulerAngles = Vector3.zero;
                            slot.AimMarker.color = new Color(1.0f, 0.75f, 0.20f, 0.95f);
                        }
                        else
                        {
                            if (slot.AimMarker.gameObject.activeSelf) slot.AimMarker.gameObject.SetActive(false);
                        }

                        // Seeker search radius around the datum (MunitionCatalog.GetAcquisitionRadius), for the offset
                        // line color only
                        float weaponBasketDist = MunitionCatalog.GetAcquisitionRadius(MunitionCatalog.Get(missile), null, 0.0f);

                        // 2. CEP Offset Vector (Connecting real target to displaced waypoint)
                        float offsetMeters = hasTarget ? (aimLocalPos - targetLocalPos).magnitude / mapFactor : 0f;
                        if (!isIR && hasTarget && offsetMeters > 2.0f)
                        {
                            if (!slot.OffsetLine.gameObject.activeSelf) slot.OffsetLine.gameObject.SetActive(true);
                            PositionLine(slot.OffsetLineRt, targetLocalPos, aimLocalPos, 2.0f / layerScale, mapZ);

                            // Dash color: Green if target is inside basket, Amber if outside
                            bool targetInBasket = offsetMeters <= weaponBasketDist;
                            slot.OffsetLine.color = targetInBasket
                                ? new Color(0.3f, 1.0f, 0.5f, 0.80f)
                                : new Color(1.0f, 0.65f, 0.15f, 0.80f);
                        }
                        else
                        {
                            if (slot.OffsetLine.gameObject.activeSelf) slot.OffsetLine.gameObject.SetActive(false);
                        }
                    }

                    slotIndex++;
                }

                // Deactivate remaining unused pool slots
                _slotPool.HideFrom(slotIndex);
            }
            catch (Exception ex)
            {
                // Log once: this runs every map frame
                PatchErrorLog.Once("MissileMapDispersionVisualizer", ex);
            }
        }

        private static void PositionLine(RectTransform rt, Vector3 localStart, Vector3 localEnd, float width, float mapZ)
        {
            rt.localPosition = localStart;
            Vector3 delta = localEnd - localStart;
            float len = delta.magnitude;
            if (len < 0.001f)
            {
                rt.sizeDelta = Vector2.zero;
                return;
            }

            float baseAngle = -Mathf.Atan2(delta.x, delta.y) * Mathf.Rad2Deg;
            float finalAngle = baseAngle + mapZ;
            rt.eulerAngles = new Vector3(0f, 0f, finalAngle);
            rt.sizeDelta = new Vector2(width, len);
            rt.localScale = Vector3.one;
        }

        private static MissileMapVisualSlot CreateSlot(Transform parent, GameObject prefab, int index)
        {
            GameObject rootGo = new GameObject("MissileMapAimpointSlot_" + index, typeof(RectTransform));
            rootGo.transform.SetParent(parent, false);
            RectTransform rootRt = rootGo.GetComponent<RectTransform>();
            rootRt.anchorMin = new Vector2(0.5f, 0.5f);
            rootRt.anchorMax = new Vector2(0.5f, 0.5f);
            rootRt.pivot = new Vector2(0.5f, 0.5f);
            rootRt.localPosition = Vector3.zero;
            rootRt.localEulerAngles = Vector3.zero;
            rootRt.localScale = Vector3.one;

            // 1. Fly-To Line
            Image flyLine = MapOverlayPool.CreateImage(rootGo.transform, "FlyToLine", prefab);
            flyLine.sprite = RWRConeGraphic.GetOrCreateSolidLineSprite();
            flyLine.type = Image.Type.Simple;
            RectTransform flyRt = flyLine.rectTransform;
            flyRt.anchorMin = new Vector2(0.5f, 0.5f);
            flyRt.anchorMax = new Vector2(0.5f, 0.5f);
            flyRt.pivot = new Vector2(0.5f, 0.0f); // Bottom-center pivot

            // 2. Aimpoint Waypoint Marker
            Image aimMarker = MapOverlayPool.CreateImage(rootGo.transform, "AimMarker", prefab);
            aimMarker.sprite = RWRConeGraphic.GetOrCreateCrosshairSprite();
            aimMarker.type = Image.Type.Simple;
            aimMarker.preserveAspect = true;
            RectTransform aimRt = aimMarker.rectTransform;
            aimRt.anchorMin = new Vector2(0.5f, 0.5f);
            aimRt.anchorMax = new Vector2(0.5f, 0.5f);
            aimRt.pivot = new Vector2(0.5f, 0.5f);

            // 3. CEP Offset Line (Dashed)
            Image offsetLine = MapOverlayPool.CreateImage(rootGo.transform, "OffsetLine", prefab);
            offsetLine.sprite = RWRConeGraphic.GetOrCreateDashedLineSprite();
            offsetLine.type = Image.Type.Tiled;
            RectTransform offsetRt = offsetLine.rectTransform;
            offsetRt.anchorMin = new Vector2(0.5f, 0.5f);
            offsetRt.anchorMax = new Vector2(0.5f, 0.5f);
            offsetRt.pivot = new Vector2(0.5f, 0.0f);

            MissileMapVisualSlot slot = new MissileMapVisualSlot
            {
                Root = rootGo,
                FlyToLine = flyLine,
                FlyToLineRt = flyRt,
                AimMarker = aimMarker,
                AimMarkerRt = aimRt,
                OffsetLine = offsetLine,
                OffsetLineRt = offsetRt
            };

            slot.SetActive(false);
            return slot;
        }

        /// <summary>
        /// Cleans up all visual objects upon level exit, scene load, or mod toggle.
        /// </summary>
        public static void ClearAll()
        {
            _slotPool.Clear();
        }
    }
}
