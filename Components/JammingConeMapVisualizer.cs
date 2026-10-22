using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using CnoEvil.Config;
using CnoEvil.Core;

namespace CnoEvil.Components
{
    /// <summary>
    /// Map and minimap (DynamicMap) zone of each active jamming pod beam of the player's faction: a translucent white
    /// cone from the pod owner along the beam axis, full angle StandoffJamming.PhysicsConeDeg (-10 dB edge), length = the
    /// target distance plus the beam tail (StandoffJamming.BeyondTargetFraction, faded out), at most the pod range. Replaces the vanilla straight jam line (JammedMarker_Update_Patch).
    /// The faction knows its own jammers, so the live owner position and beam axis are shown.
    /// Zero heap allocations per frame, pre-allocated pool of POOL_SIZE cones.
    /// </summary>
    public static class JammingConeMapVisualizer
    {
        private const int POOL_SIZE = 8;
        private const float ConeAlpha = 0.08f;

        private static readonly Func<DynamicMap, GameObject> s_getRadarVisPrefab =
            FastReflection.CreateFieldGetter<DynamicMap, GameObject>("radarVisPrefab");

        private static readonly List<Image> _pool = new List<Image>(POOL_SIZE);
        private static Transform _cachedParent;
        private static bool s_errorLogged;

        /// <summary>
        /// Hooked into DynamicMapPatches.UpdateMap_Postfix.
        /// </summary>
        public static void UpdateVisuals(DynamicMap map)
        {
            try
            {
                if (!CnoEvilConfig.IsModActive || map == null || map.iconLayer == null)
                {
                    HideFrom(0);
                    return;
                }

                List<PodBeam> beams = StandoffJamming.Beams;
                if (beams.Count == 0)
                {
                    HideFrom(0);
                    return;
                }

                FactionHQ playerHq = RWRTriangulationProcessor.GetPlayerFactionHQ();
                if (playerHq == null)
                {
                    HideFrom(0);
                    return;
                }

                EnsurePool(map);

                float mapZ = (map.mapImage != null) ? map.mapImage.transform.eulerAngles.z : 0f;
                float mapFactor = map.mapDisplayFactor;
                float halfAngle = StandoffJamming.PhysicsConeDeg * 0.5f;
                float now = Time.timeSinceLevelLoad;

                int slot = 0;
                for (int i = 0; i < beams.Count && slot < _pool.Count; i++)
                {
                    PodBeam beam = beams[i];
                    if (!StandoffJamming.IsActive(beam, now)) continue;
                    if (beam.Owner.NetworkHQ != playerHq) continue;

                    Vector3 ownerGlobal = beam.Owner.GlobalPosition().AsVector3();
                    Vector3 targetGlobal = beam.Target.GlobalPosition().AsVector3();
                    Vector3 start = new Vector3(ownerGlobal.x * mapFactor, ownerGlobal.z * mapFactor, 0f);
                    Vector3 toTarget = new Vector3((targetGlobal.x - ownerGlobal.x) * mapFactor, (targetGlobal.z - ownerGlobal.z) * mapFactor, 0f);
                    if (toTarget.sqrMagnitude < 0.000001f) continue;

                    // Ends where the physics taper reaches 0 (StandoffJamming.BeyondTargetFactor), at most the pod range
                    float worldLength = (targetGlobal - ownerGlobal).magnitude * (1.0f + StandoffJamming.BeyondTargetFraction);
                    if (beam.MaxRange > 0.0f && worldLength > beam.MaxRange) worldLength = beam.MaxRange;
                    float length = worldLength * mapFactor;
                    if (length < 0.001f) continue;

                    Image img = _pool[slot];
                    if (img == null) continue;
                    if (!img.gameObject.activeSelf) img.gameObject.SetActive(true);

                    RectTransform rt = img.rectTransform;
                    rt.localPosition = start;
                    float baseAngle = -Mathf.Atan2(toTarget.x, toTarget.y) * Mathf.Rad2Deg;
                    rt.eulerAngles = new Vector3(0f, 0f, baseAngle + mapZ);
                    rt.sizeDelta = new Vector2(RWRConeGraphic.GetConeBaseWidth(length, halfAngle), length);
                    rt.localScale = Vector3.one;
                    img.color = new Color(1.0f, 1.0f, 1.0f, ConeAlpha);
                    slot++;
                }
                HideFrom(slot);
            }
            catch (Exception ex)
            {
                // Log once: this runs every map frame
                if (!s_errorLogged && CnoEvilPlugin.Log != null)
                {
                    s_errorLogged = true;
                    CnoEvilPlugin.Log.LogError("JammingConeMapVisualizer error: " + ex);
                }
            }
        }

        private static void EnsurePool(DynamicMap map)
        {
            Transform parent = map.iconLayer.transform;
            if (_cachedParent != parent)
            {
                ClearAll();
                _cachedParent = parent;
            }
            if (_pool.Count >= POOL_SIZE) return;

            GameObject prefab = (s_getRadarVisPrefab != null) ? s_getRadarVisPrefab(map) : null;
            while (_pool.Count < POOL_SIZE)
            {
                _pool.Add(CreateCone(parent, prefab, _pool.Count));
            }
        }

        private static Image CreateCone(Transform parent, GameObject prefab, int index)
        {
            GameObject go;
            if (prefab != null)
            {
                go = UnityEngine.Object.Instantiate(prefab, parent);
            }
            else
            {
                go = new GameObject("JamCone", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
                go.transform.SetParent(parent, false);
            }
            go.name = "JammingPodCone_" + index;

            Image img = go.GetComponent<Image>();
            if (img == null) img = go.AddComponent<Image>();
            img.sprite = RWRConeGraphic.GetOrCreateFlatConeSprite();
            img.type = Image.Type.Simple;
            img.raycastTarget = false;

            RectTransform rt = img.rectTransform;
            rt.anchorMin = new Vector2(0.5f, 0.5f);
            rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.pivot = new Vector2(0.5f, 0.0f); // Apex at the pod owner
            rt.localPosition = Vector3.zero;
            rt.localEulerAngles = Vector3.zero;
            rt.localScale = Vector3.one;

            // Below the unit icons
            go.transform.SetAsFirstSibling();
            go.SetActive(false);
            return img;
        }

        private static void HideFrom(int start)
        {
            for (int i = start; i < _pool.Count; i++)
            {
                if (_pool[i] != null && _pool[i].gameObject.activeSelf) _pool[i].gameObject.SetActive(false);
            }
        }

        /// <summary>
        /// Destroys the cones on scene load and plugin unload.
        /// </summary>
        public static void ClearAll()
        {
            for (int i = 0; i < _pool.Count; i++)
            {
                if (_pool[i] != null) UnityEngine.Object.Destroy(_pool[i].gameObject);
            }
            _pool.Clear();
            _cachedParent = null;
        }
    }
}
