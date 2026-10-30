using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using CnoEvil.Config;
using CnoEvil.Core;

namespace CnoEvil.Components
{
    /// <summary>
    /// Map and minimap markers of the hostile missiles the player knows (EnableMapDatalinkMissiles): the missiles the
    /// player's aircraft warns of (MissileWarningModel, flashing red) and the missiles relayed over the datalink
    /// (cyan). A diamond at the known position, dead-reckoned from the last report, and a 10 s velocity line.
    /// A missile with a vanilla map icon gets no marker. Pool of MaxMarkers slots, no allocation per frame.
    /// </summary>
    public static class DatalinkMissileMapVisualizer
    {
        private const int MaxMarkers = 16;
        private const float LeaderSeconds = 10.0f;

        private static readonly Color WarnColor = new Color(1.0f, 0.15f, 0.15f, 0.95f);
        private static readonly Color WarnDimColor = new Color(0.85f, 0.08f, 0.08f, 0.5f);
        private static readonly Color DatalinkColor = new Color(0.2f, 0.85f, 1.0f, 0.9f);

        private class Slot
        {
            public GameObject Root;
            public Image Marker;
            public Image Leader;
        }

        private static readonly MapOverlayPool<Slot> s_pool =
            new MapOverlayPool<Slot>(MaxMarkers, CreateSlot, slot => slot != null ? slot.Root : null);
        private static readonly List<Missile> s_drawn = new List<Missile>(MaxMarkers);

        public static void UpdateVisuals(DynamicMap map)
        {
            try
            {
                Aircraft own = CombatHUD.i != null ? CombatHUD.i.aircraft : null;
                if (!CnoEvilConfig.IsModActive || !CnoEvilConfig.EnableMapDatalinkMissiles.Value ||
                    map == null || map.iconLayer == null || own == null || own.disabled)
                {
                    s_pool.HideFrom(0);
                    return;
                }

                float now = Time.time;
                float layerScale = map.iconLayer.transform.lossyScale.y > 0.0001f ? map.iconLayer.transform.lossyScale.y : 1.0f;
                float mapZ = map.mapImage != null ? map.mapImage.transform.eulerAngles.z : 0f;
                bool flashOn = Mathf.Sin(now * 20.0f) > 0.0f;
                int n = 0;
                s_drawn.Clear();

                Dictionary<Missile, MissileWarningModel.Warning> warnings = MissileWarningModel.GetWarnings(own);
                if (warnings != null)
                {
                    foreach (MissileWarningModel.Warning w in warnings.Values)
                    {
                        if (n >= MaxMarkers) break;
                        if (!w.Warned || !w.HasKinematics || w.Missile == null || w.Missile.disabled || HasVanillaIcon(w.Missile)) continue;
                        if (n == 0) s_pool.Ensure(map);
                        Place(s_pool[n++], map, w.Position, w.Velocity, layerScale, mapZ, flashOn ? WarnColor : WarnDimColor);
                        s_drawn.Add(w.Missile);
                    }
                }

                PhysRWRReceiver rwr = PhysRWRReceiver.Get(own);
                if (rwr != null)
                {
                    foreach (DatalinkThreatEntry d in rwr.ActiveDatalinkThreats)
                    {
                        if (n >= MaxMarkers) break;
                        Missile m = d != null && d.IsMissile ? d.ThreatUnit as Missile : null;
                        if (m == null || m.disabled || s_drawn.Contains(m) || HasVanillaIcon(m)) continue;
                        Vector3 pos = d.EstimatedPosition + d.EstimatedVelocity * Mathf.Max(0.0f, now - d.LastPingTime);
                        if (n == 0) s_pool.Ensure(map);
                        Place(s_pool[n++], map, pos, d.EstimatedVelocity, layerScale, mapZ, DatalinkColor);
                    }
                }

                s_pool.HideFrom(n);
            }
            catch (Exception ex)
            {
                PatchErrorLog.Once("DatalinkMissileMapVisualizer", ex);
            }
        }

        private static bool HasVanillaIcon(Missile m)
        {
            UnitMapIcon icon;
            return DynamicMap.TryGetMapIcon(m, out icon) && icon != null && icon.gameObject.activeInHierarchy;
        }

        private static void Place(Slot slot, DynamicMap map, Vector3 worldPos, Vector3 worldVel, float layerScale, float mapZ, Color color)
        {
            if (slot == null || slot.Root == null) return;
            if (!slot.Root.activeSelf) slot.Root.SetActive(true);

            float factor = map.mapDisplayFactor;
            Vector3 g = worldPos.ToGlobalPosition().AsVector3();
            Vector3 local = new Vector3(g.x * factor, g.z * factor, 0f);

            float size = (DynamicMap.mapMaximized ? 14.0f : 10.0f) / layerScale;
            RectTransform mrt = slot.Marker.rectTransform;
            mrt.localPosition = local;
            mrt.sizeDelta = new Vector2(size, size);
            mrt.localEulerAngles = Vector3.zero;
            slot.Marker.color = color;

            Vector3 lead = new Vector3(worldVel.x, worldVel.z, 0f) * (LeaderSeconds * factor);
            float len = lead.magnitude;
            RectTransform lrt = slot.Leader.rectTransform;
            lrt.localPosition = local;
            if (len < 0.001f)
            {
                lrt.sizeDelta = Vector2.zero;
            }
            else
            {
                lrt.eulerAngles = new Vector3(0f, 0f, -Mathf.Atan2(lead.x, lead.y) * Mathf.Rad2Deg + mapZ);
                lrt.sizeDelta = new Vector2(2.0f / layerScale, len);
            }
            slot.Leader.color = color;
        }

        private static Slot CreateSlot(Transform parent, GameObject prefab, int index)
        {
            GameObject root = new GameObject("DatalinkMissileMarker_" + index, typeof(RectTransform));
            root.transform.SetParent(parent, false);
            RectTransform rootRt = root.GetComponent<RectTransform>();
            rootRt.anchorMin = new Vector2(0.5f, 0.5f);
            rootRt.anchorMax = new Vector2(0.5f, 0.5f);
            rootRt.pivot = new Vector2(0.5f, 0.5f);

            Image leader = MapOverlayPool.CreateImage(root.transform, "Leader", prefab);
            leader.sprite = RWRConeGraphic.GetOrCreateSolidLineSprite();
            leader.type = Image.Type.Simple;
            RectTransform lrt = leader.rectTransform;
            lrt.anchorMin = new Vector2(0.5f, 0.5f);
            lrt.anchorMax = new Vector2(0.5f, 0.5f);
            lrt.pivot = new Vector2(0.5f, 0.0f);

            Image marker = MapOverlayPool.CreateImage(root.transform, "Marker", prefab);
            marker.sprite = PortalMFDGraphics.Get(PortalShape.Diamond);
            marker.type = Image.Type.Simple;
            marker.preserveAspect = true;
            RectTransform mrt = marker.rectTransform;
            mrt.anchorMin = new Vector2(0.5f, 0.5f);
            mrt.anchorMax = new Vector2(0.5f, 0.5f);
            mrt.pivot = new Vector2(0.5f, 0.5f);

            Slot slot = new Slot { Root = root, Marker = marker, Leader = leader };
            root.SetActive(false);
            return slot;
        }

        public static void ClearAll()
        {
            s_pool.Clear();
            s_drawn.Clear();
        }
    }
}
