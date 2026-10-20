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
        private static Texture2D _cachedConeTexture;

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

            _cachedConeTexture = new Texture2D(width, height, TextureFormat.RGBA32, false);
            _cachedConeTexture.name = "RWR_BearingCone_Texture";
            _cachedConeTexture.wrapMode = TextureWrapMode.Clamp;
            _cachedConeTexture.filterMode = FilterMode.Bilinear;

            Color[] pixels = new Color[width * height];
            float invW = 1.0f / (width - 1);
            float invH = 1.0f / (height - 1);

            for (int y = 0; y < height; y++)
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

                int rowOffset = y * width;
                for (int x = 0; x < width; x++)
                {
                    float u = x * invW;
                    float distFromCenter = Mathf.Abs(u - 0.5f);

                    if (distFromCenter <= halfWidthNorm)
                    {
                        float delta = distFromCenter / halfWidthNorm; // 0 at core axis, 1 at lateral edge
                        float angularCos = Mathf.Cos(delta * Mathf.PI * 0.5f);
                        float angularFactor = angularCos * angularCos; // Smooth cos^2 beam cross-section with feathered edges

                        float alpha = Mathf.Clamp01(angularFactor * radialFactor);
                        pixels[rowOffset + x] = new Color(1.0f, 1.0f, 1.0f, alpha);
                    }
                    else
                    {
                        pixels[rowOffset + x] = new Color(1.0f, 1.0f, 1.0f, 0.0f);
                    }
                }
            }

            _cachedConeTexture.SetPixels(pixels);
            _cachedConeTexture.Apply(false, true); // Upload and make non-readable to save VRAM

            _cachedConeSprite = Sprite.Create(
                _cachedConeTexture,
                new Rect(0, 0, width, height),
                new Vector2(0.5f, 0.0f), // Apex pivot at center bottom
                100.0f,
                0,
                SpriteMeshType.FullRect
            );
            _cachedConeSprite.name = "RWR_BearingCone_Sprite";

            return _cachedConeSprite;
        }

        private static Sprite _cachedFlatConeSprite;
        private static Texture2D _cachedFlatConeTexture;

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

            _cachedFlatConeTexture = new Texture2D(width, height, TextureFormat.RGBA32, false);
            _cachedFlatConeTexture.name = "Jam_FlatCone_Texture";
            _cachedFlatConeTexture.wrapMode = TextureWrapMode.Clamp;
            _cachedFlatConeTexture.filterMode = FilterMode.Bilinear;

            Color[] pixels = new Color[width * height];
            float center = (width - 1) * 0.5f;
            float fadeStart = 1.0f / (1.0f + StandoffJamming.BeyondTargetFraction); // target position along the cone
            for (int y = 0; y < height; y++)
            {
                float v = y / (float)(height - 1); // 0 at apex, 1 at outer range
                float halfWidthPx = center * v;
                float tip = 1.0f;
                if (v > fadeStart)
                {
                    float c = Mathf.Cos((v - fadeStart) / (1.0f - fadeStart) * Mathf.PI * 0.5f);
                    tip = c * c;
                }
                int row = y * width;
                for (int x = 0; x < width; x++)
                {
                    float inside = halfWidthPx - Mathf.Abs(x - center);
                    float alpha = Mathf.Clamp01(inside / EdgePixels) * tip;
                    pixels[row + x] = new Color(1.0f, 1.0f, 1.0f, alpha);
                }
            }

            _cachedFlatConeTexture.SetPixels(pixels);
            _cachedFlatConeTexture.Apply(false, true);

            _cachedFlatConeSprite = Sprite.Create(
                _cachedFlatConeTexture,
                new Rect(0, 0, width, height),
                new Vector2(0.5f, 0.0f),
                100.0f,
                0,
                SpriteMeshType.FullRect
            );
            _cachedFlatConeSprite.name = "Jam_FlatCone_Sprite";
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
                case RwrThreatState.MissileGuidance: fullAngle = CnoEvilConfig.CockpitRWRMissileSectorAngle.Value; break;
                case RwrThreatState.Track: fullAngle = CnoEvilConfig.CockpitRWRTrackSectorAngle.Value; break;
                case RwrThreatState.Search:
                default: fullAngle = CnoEvilConfig.CockpitRWRSearchSectorAngle.Value; break;
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
        private static Texture2D _cachedCircleTexture;

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
            _cachedCircleTexture = new Texture2D(size, size, TextureFormat.RGBA32, false);
            _cachedCircleTexture.name = "RWR_UncertaintyCircle_Texture";
            _cachedCircleTexture.wrapMode = TextureWrapMode.Clamp;
            _cachedCircleTexture.filterMode = FilterMode.Bilinear;

            Color[] pixels = new Color[size * size];
            float center = (size - 1) * 0.5f;
            float invRadius = 1.0f / center;

            for (int y = 0; y < size; y++)
            {
                float dy = (y - center) * invRadius;
                int row = y * size;
                for (int x = 0; x < size; x++)
                {
                    float dx = (x - center) * invRadius;
                    float dist = Mathf.Sqrt(dx * dx + dy * dy);

                    if (dist > 1.0f)
                    {
                        pixels[row + x] = new Color(1.0f, 1.0f, 1.0f, 0.0f);
                    }
                    else
                    {
                        float alpha;
                        if (dist <= 0.82f)
                        {
                            // Distinct semi-transparent tactical phosphor zone fill
                            alpha = 0.38f;
                        }
                        else if (dist <= 0.95f)
                        {
                            // Crisp glowing phosphor perimeter boundary ring
                            float strokeT = (dist - 0.82f) / 0.13f;
                            alpha = Mathf.Lerp(0.38f, 0.95f, strokeT);
                        }
                        else
                        {
                            // Antialiased outer feather
                            float featherT = (dist - 0.95f) / 0.05f;
                            alpha = 0.95f * (1.0f - featherT);
                        }
                        pixels[row + x] = new Color(1.0f, 1.0f, 1.0f, alpha);
                    }
                }
            }

            _cachedCircleTexture.SetPixels(pixels);
            _cachedCircleTexture.Apply(false, true);

            _cachedCircleSprite = Sprite.Create(
                _cachedCircleTexture,
                new Rect(0, 0, size, size),
                new Vector2(0.5f, 0.5f), // Center pivot
                100.0f,
                0,
                SpriteMeshType.FullRect
            );
            _cachedCircleSprite.name = "RWR_UncertaintyCircle_Sprite";

            return _cachedCircleSprite;
        }

        private static Sprite _cachedSolidLineSprite;
        private static Texture2D _cachedSolidLineTexture;

        public static Sprite GetOrCreateSolidLineSprite()
        {
            if (_cachedSolidLineSprite != null) return _cachedSolidLineSprite;

            int size = 4;
            _cachedSolidLineTexture = new Texture2D(size, size, TextureFormat.RGBA32, false);
            _cachedSolidLineTexture.name = "RWR_SolidLine_Texture";
            _cachedSolidLineTexture.wrapMode = TextureWrapMode.Clamp;
            Color[] pixels = new Color[size * size];
            for (int i = 0; i < pixels.Length; i++) pixels[i] = Color.white;
            _cachedSolidLineTexture.SetPixels(pixels);
            _cachedSolidLineTexture.Apply(false, true);

            _cachedSolidLineSprite = Sprite.Create(
                _cachedSolidLineTexture,
                new Rect(0, 0, size, size),
                new Vector2(0.5f, 0.0f),
                100.0f,
                0,
                SpriteMeshType.FullRect
            );
            _cachedSolidLineSprite.name = "RWR_SolidLine_Sprite";
            return _cachedSolidLineSprite;
        }

        private static Sprite _cachedDashedLineSprite;
        private static Texture2D _cachedDashedLineTexture;

        public static Sprite GetOrCreateDashedLineSprite()
        {
            if (_cachedDashedLineSprite != null) return _cachedDashedLineSprite;

            int w = 8;
            int h = 32;
            _cachedDashedLineTexture = new Texture2D(w, h, TextureFormat.RGBA32, false);
            _cachedDashedLineTexture.name = "RWR_DashedLine_Texture";
            _cachedDashedLineTexture.wrapMode = TextureWrapMode.Repeat;
            Color[] pixels = new Color[w * h];
            for (int y = 0; y < h; y++)
            {
                bool solid = (y < 16);
                Color c = solid ? Color.white : new Color(1.0f, 1.0f, 1.0f, 0.0f);
                for (int x = 0; x < w; x++)
                {
                    pixels[y * w + x] = c;
                }
            }
            _cachedDashedLineTexture.SetPixels(pixels);
            _cachedDashedLineTexture.Apply(false, true);

            _cachedDashedLineSprite = Sprite.Create(
                _cachedDashedLineTexture,
                new Rect(0, 0, w, h),
                new Vector2(0.5f, 0.0f),
                100.0f,
                0,
                SpriteMeshType.FullRect
            );
            _cachedDashedLineSprite.name = "RWR_DashedLine_Sprite";
            return _cachedDashedLineSprite;
        }

        private static Sprite _cachedCrosshairSprite;
        private static Texture2D _cachedCrosshairTexture;

        public static Sprite GetOrCreateCrosshairSprite()
        {
            if (_cachedCrosshairSprite != null) return _cachedCrosshairSprite;

            int size = 32;
            _cachedCrosshairTexture = new Texture2D(size, size, TextureFormat.RGBA32, false);
            _cachedCrosshairTexture.name = "RWR_Crosshair_Texture";
            _cachedCrosshairTexture.wrapMode = TextureWrapMode.Clamp;
            Color[] pixels = new Color[size * size];
            float center = (size - 1) * 0.5f;

            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float dx = Mathf.Abs(x - center);
                    float dy = Mathf.Abs(y - center);

                    float manhattan = dx + dy;
                    bool isDiamond = Mathf.Abs(manhattan - 9.0f) <= 1.2f;
                    bool isCrossX = (dy <= 1.0f) && (dx >= 3.0f && dx <= 13.0f);
                    bool isCrossY = (dx <= 1.0f) && (dy >= 3.0f && dy <= 13.0f);
                    bool isCenter = (dx <= 1.5f && dy <= 1.5f);

                    float alpha = (isDiamond || isCrossX || isCrossY || isCenter) ? 1.0f : 0.0f;
                    pixels[y * size + x] = new Color(1.0f, 1.0f, 1.0f, alpha);
                }
            }
            _cachedCrosshairTexture.SetPixels(pixels);
            _cachedCrosshairTexture.Apply(false, true);

            _cachedCrosshairSprite = Sprite.Create(
                _cachedCrosshairTexture,
                new Rect(0, 0, size, size),
                new Vector2(0.5f, 0.5f),
                100.0f,
                0,
                SpriteMeshType.FullRect
            );
            _cachedCrosshairSprite.name = "RWR_Crosshair_Sprite";
            return _cachedCrosshairSprite;
        }
    }
}
