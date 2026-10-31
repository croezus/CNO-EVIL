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

        private static readonly MapOverlayPool<Image> _pool =
            new MapOverlayPool<Image>(POOL_SIZE, CreateCone, img => img != null ? img.gameObject : null);

        /// <summary>
        /// Hooked into DynamicMapPatches.UpdateMap_Postfix.
        /// </summary>
        public static void UpdateVisuals(DynamicMap map)
        {
            try
            {
                if (!CnoEvilConfig.IsModActive || map == null || map.iconLayer == null)
                {
                    _pool.HideFrom(0);
                    return;
                }

                List<PodBeam> beams = StandoffJamming.Beams;
                if (beams.Count == 0)
                {
                    _pool.HideFrom(0);
                    return;
                }

                FactionHQ playerHq = RWRTriangulationProcessor.GetPlayerFactionHQ();
                if (playerHq == null)
                {
                    _pool.HideFrom(0);
                    return;
                }

                _pool.Ensure(map);

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
                _pool.HideFrom(slot);
            }
            catch (Exception ex)
            {
                // Log once: this runs every map frame
                PatchErrorLog.Once("JammingConeMapVisualizer", ex);
            }
        }

        private static Image CreateCone(Transform parent, GameObject prefab, int index)
        {
            Image img = MapOverlayPool.CreateImage(parent, "JammingPodCone_" + index, prefab);
            img.sprite = RWRConeGraphic.GetOrCreateFlatConeSprite();
            img.type = Image.Type.Simple;

            RectTransform rt = img.rectTransform;
            rt.anchorMin = new Vector2(0.5f, 0.5f);
            rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.pivot = new Vector2(0.5f, 0.0f); // Apex at the pod owner

            // Below the unit icons
            img.transform.SetAsFirstSibling();
            img.gameObject.SetActive(false);
            return img;
        }

        /// <summary>
        /// Destroys the cones on scene load and plugin unload.
        /// </summary>
        public static void ClearAll()
        {
            _pool.Clear();
        }
    }
}
