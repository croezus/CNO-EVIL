using System;
using UnityEngine;
using CnoEvil.Config;
using CnoEvil.Components;

namespace CnoEvil.Core
{
    /// <summary>
    /// Procedural phosphor sprites for RWR displays: the cone (map zone cones, TacScreen strobes), the zone circle,
    /// and line / crosshair sprites shared with the missile map overlay.
    /// </summary>
    public static class RWRConeGraphic
    {
        private static Sprite _cachedConeSprite;

        /// <summary>
        /// Retrieves or lazily creates a cached 128x256 smooth phosphor cone sprite with bottom-center pivot (0.5, 0.0).
        /// Employs a cosine angular cross-section and smooth radial tip falloff. Zero allocations during flight.
        /// </summary>
        public static Sprite GetOrCreateConeSprite()
        {
            if (_cachedConeSprite != null)
            {
                return _cachedConeSprite;
            }

            int width = 128;
            int height = 256;
            float invW = 1.0f / (width - 1);
            float invH = 1.0f / (height - 1);

            // Apex pivot at center bottom
            _cachedConeSprite = SpriteFactory.MakeSprite(width, height, new Vector2(0.5f, 0.0f), TextureWrapMode.Clamp, "RWR_BearingCone", delegate(int x, int y)
            {
                float v = y * invH; // 0 at apex, 1 at outer range
                float halfWidthNorm = 0.5f * Mathf.Max(0.005f, v);

                // Radial distance attenuation:
                // Fast gentle apex rise near ownship, solid luminous beam body, and soft cosine dispersion at tip
                float radialFactor;
                if (v < 0.02f)
                {
                    radialFactor = v / 0.02f; // Soft, tight apex right at ownship
                }
                else if (v < 0.75f)
                {
                    // Main body: solid glowing illumination with gentle transition (1.0 -> 0.75)
                    radialFactor = Mathf.Lerp(1.0f, 0.75f, (v - 0.02f) / 0.73f);
                }
                else
                {
                    // Tip feather: soft dispersion at outer perimeter (reaching the 85% distance margin)
                    float tipT = (v - 0.75f) / 0.25f;
                    radialFactor = 0.75f * Mathf.Cos(tipT * Mathf.PI * 0.5f);
                }

                float u = x * invW;
                float distFromCenter = Mathf.Abs(u - 0.5f);
                if (distFromCenter > halfWidthNorm) return 0.0f;

                float delta = distFromCenter / halfWidthNorm; // 0 at core axis, 1 at lateral edge
                float angularCos = Mathf.Cos(delta * Mathf.PI * 0.5f);
                float angularFactor = angularCos * angularCos; // Smooth cos^2 beam cross-section with feathered edges
                return Mathf.Clamp01(angularFactor * radialFactor);
            });
            return _cachedConeSprite;
        }

        private static Sprite _cachedFlatConeSprite;

        /// <summary>
        /// Cached 128x256 flat cone sprite (bottom-center apex pivot): uniform alpha 1 inside, a soft 3 px lateral edge,
        /// and a cos^2 tip fade over the beam tail past the target (StandoffJamming.BeyondTargetFraction of the length).
        /// For translucent zone fills (jamming pod beam on the map), where the color alpha sets the opacity.
        /// </summary>
        public static Sprite GetOrCreateFlatConeSprite()
        {
            if (_cachedFlatConeSprite != null) return _cachedFlatConeSprite;

            int width = 128;
            int height = 256;
            const float EdgePixels = 3.0f;
            float center = (width - 1) * 0.5f;
            float fadeStart = 1.0f / (1.0f + StandoffJamming.BeyondTargetFraction); // target position along the cone

            _cachedFlatConeSprite = SpriteFactory.MakeSprite(width, height, new Vector2(0.5f, 0.0f), TextureWrapMode.Clamp, "Jam_FlatCone", delegate(int x, int y)
            {
                float v = y / (float)(height - 1); // 0 at apex, 1 at outer range
                float halfWidthPx = center * v;
                float tip = 1.0f;
                if (v > fadeStart)
                {
                    float c = Mathf.Cos((v - fadeStart) / (1.0f - fadeStart) * Mathf.PI * 0.5f);
                    tip = c * c;
                }
                float inside = halfWidthPx - Mathf.Abs(x - center);
                return Mathf.Clamp01(inside / EdgePixels) * tip;
            });
            return _cachedFlatConeSprite;
        }

        private const float MinHalfAngleDeg = 0.5f;
        private const float MaxHalfAngleDeg = 45.0f;

        /// <summary>
        /// Half-angle (degrees) of a TacScreen RWR strobe: half the configured full angle for the threat state
        /// (CockpitRWRSearch/Track/MissileSectorAngle).
        /// </summary>
        public static float GetCockpitConeHalfAngle(RwrThreatState state)
        {
            float fullAngle;
            switch (state)
            {
                case RwrThreatState.MissileGuidance: fullAngle = CnoEvilConfig.RWRMissileStrobeAngle_deg.Value; break;
                case RwrThreatState.Track: fullAngle = CnoEvilConfig.RWRTrackStrobeAngle_deg.Value; break;
                case RwrThreatState.Search:
                default: fullAngle = CnoEvilConfig.RWRSearchStrobeAngle_deg.Value; break;
            }
            return Mathf.Clamp(fullAngle * 0.5f, MinHalfAngleDeg, MaxHalfAngleDeg);
        }

        /// <summary>
        /// Calculates the outer base width of the uncertainty cone for a given length and half-angle.
        /// Base width = 2 * length * tan(halfAngleDeg).
        /// </summary>
        public static float GetConeBaseWidth(float length, float halfAngleDeg)
        {
            float halfRad = Mathf.Clamp(halfAngleDeg, MinHalfAngleDeg, MaxHalfAngleDeg) * Mathf.Deg2Rad;
            return 2.0f * length * Mathf.Tan(halfRad);
        }

        private static Sprite _cachedCircleSprite;

        /// <summary>
        /// Retrieves or lazily creates a cached 128x128 procedural ESM uncertainty circle sprite.
        /// Renders an authentic phosphor CEP (Circular Error Probable) boundary:
        /// Glowing outer rim with soft antialiased stroke and a faint, translucent phosphor interior fill.
        /// Center pivot (0.5, 0.5) for direct placement on estimated target coordinates.
        /// </summary>
        public static Sprite GetOrCreateUncertaintyCircleSprite()
        {
            if (_cachedCircleSprite != null)
            {
                return _cachedCircleSprite;
            }

            int size = 128;
            float center = (size - 1) * 0.5f;
            float invRadius = 1.0f / center;

            _cachedCircleSprite = SpriteFactory.MakeSprite(size, size, new Vector2(0.5f, 0.5f), TextureWrapMode.Clamp, "RWR_UncertaintyCircle", delegate(int x, int y)
            {
                float dy = (y - center) * invRadius;
                float dx = (x - center) * invRadius;
                float dist = Mathf.Sqrt(dx * dx + dy * dy);

                if (dist > 1.0f) return 0.0f;
                if (dist <= 0.82f)
                {
                    // Distinct semi-transparent tactical phosphor zone fill
                    return 0.38f;
                }
                if (dist <= 0.95f)
                {
                    // Crisp glowing phosphor perimeter boundary ring
                    float strokeT = (dist - 0.82f) / 0.13f;
                    return Mathf.Lerp(0.38f, 0.95f, strokeT);
                }
                // Antialiased outer feather
                float featherT = (dist - 0.95f) / 0.05f;
                return 0.95f * (1.0f - featherT);
            });
            return _cachedCircleSprite;
        }

        private static Sprite _cachedSolidLineSprite;

        public static Sprite GetOrCreateSolidLineSprite()
        {
            if (_cachedSolidLineSprite != null) return _cachedSolidLineSprite;
            _cachedSolidLineSprite = SpriteFactory.MakeSprite(4, 4, new Vector2(0.5f, 0.0f), TextureWrapMode.Clamp, "RWR_SolidLine",
                delegate(int x, int y) { return 1.0f; });
            return _cachedSolidLineSprite;
        }

        private static Sprite _cachedDashedLineSprite;

        /// <summary>
        /// 8x32 dash period, lower half opaque, repeating (map offset line, Tiled). Its texture is also the portal MFD dash.
        /// </summary>
        public static Sprite GetOrCreateDashedLineSprite()
        {
            if (_cachedDashedLineSprite != null) return _cachedDashedLineSprite;
            _cachedDashedLineSprite = SpriteFactory.MakeSprite(8, 32, new Vector2(0.5f, 0.0f), TextureWrapMode.Repeat, "RWR_DashedLine",
                delegate(int x, int y) { return y < 16 ? 1.0f : 0.0f; });
            return _cachedDashedLineSprite;
        }

        private static Sprite _cachedCrosshairSprite;

        public static Sprite GetOrCreateCrosshairSprite()
        {
            if (_cachedCrosshairSprite != null) return _cachedCrosshairSprite;

            int size = 32;
            float center = (size - 1) * 0.5f;
            _cachedCrosshairSprite = SpriteFactory.MakeSprite(size, size, new Vector2(0.5f, 0.5f), TextureWrapMode.Clamp, "RWR_Crosshair", delegate(int x, int y)
            {
                float dx = Mathf.Abs(x - center);
                float dy = Mathf.Abs(y - center);

                float manhattan = dx + dy;
                bool isDiamond = Mathf.Abs(manhattan - 9.0f) <= 1.2f;
                bool isCrossX = (dy <= 1.0f) && (dx >= 3.0f && dx <= 13.0f);
                bool isCrossY = (dx <= 1.0f) && (dy >= 3.0f && dy <= 13.0f);
                bool isCenter = (dx <= 1.5f && dy <= 1.5f);

                return (isDiamond || isCrossX || isCrossY || isCenter) ? 1.0f : 0.0f;
            });
            return _cachedCrosshairSprite;
        }
    }
}
