using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

namespace CnoEvil.Core
{
    public enum PortalShape
    {
        Frame,          // 9-sliced 1 unit border (button and page frames)
        Disc,
        Ring,           // thin circle line (small symbols)
        RingFine,       // hairline circle for large rings (range rings, compass rose); 512 px texture
        RingBold,       // bold line for large rings and arcs (RWR missile warning arc); 512 px texture
        RingThick,      // thick circle line (lock circles)
        RingDotted,
        HalfRingTop,
        TriangleOutline,
        TriangleFilled,
        TriangleThick,
        Diamond,
        Chevron,
        SquareOutline,
        Arch,           // allied ground: square bottom, round top
        RadarArc,       // emitter mark: lower half ring
        PlayerArrow,    // ownship: narrow filled triangle
        RadarEnemy,     // B-scope hostile: open triangle with a stem
        RadarFriendly,  // B-scope friendly: ring with a stem
        RadarMissile,   // B-scope missile: ring with three stems
        AirHat          // RWR air threat cap
    }

    /// <summary>
    /// Procedural sprites and UI builders for the portal MFD (TacScreenPortalMFD and its pages).
    /// Each sprite is a white antialiased shape drawn once from a distance function and cached;
    /// the Image color tints it. All builders work in the layout units of the portal (one half is 345 x 300).
    /// </summary>
    public static class PortalMFDGraphics
    {
        private const int ShapeSize = 64;
        private static readonly Dictionary<PortalShape, Sprite> s_sprites = new Dictionary<PortalShape, Sprite>();

        public static readonly Color Background = new Color(0.0f, 0.02f, 0.03f, 1.0f);
        public static readonly Color QuarterFrame = new Color(0.23f, 0.71f, 0.0f, 0.78f);
        public static readonly Color PageBorder = new Color(1.0f, 1.0f, 1.0f, 0.27f);
        public static readonly Color ButtonBox = new Color(0.0f, 0.0f, 0.0f, 0.63f);
        public static readonly Color TextWhite = Color.white;
        public static readonly Color DimGreen = new Color(0.0f, 0.67f, 0.03f, 0.74f);
        public static readonly Color RingGrey = new Color(0.23f, 0.23f, 0.23f, 1.0f);
        public static readonly Color LabelGrey = new Color(0.32f, 0.32f, 0.32f, 1.0f);
        public static readonly Color Hostile = new Color(1.0f, 0.39f, 0.0f, 1.0f);
        public static readonly Color Friendly = new Color(0.0f, 0.67f, 1.0f, 1.0f);
        public static readonly Color Missile = new Color(1.0f, 0.0f, 0.0f, 1.0f);
        // Datalink cyan, as the datalink strobes on the map and the AdvancedCanvas TacScreen
        public static readonly Color DatalinkMissile = new Color(0.2f, 0.85f, 1.0f, 1.0f);
        public static readonly Color Amber = new Color(1.0f, 0.6f, 0.0f, 1.0f);

        // Round scopes (TSD, NAV, RWR) share one center and radius: radius = page height / 2 - ScopeInset
        public const float ScopeInset = 16f;
        public static readonly Vector2 ScopeCenter = new Vector2(0f, -2f);

        public static Sprite Get(PortalShape shape)
        {
            Sprite sprite;
            if (s_sprites.TryGetValue(shape, out sprite) && sprite != null) return sprite;
            sprite = shape == PortalShape.Frame ? CreateFrame() : CreateShape(shape);
            s_sprites[shape] = sprite;
            return sprite;
        }

        private static Sprite CreateFrame()
        {
            const int size = 16;
            Texture2D tex = SpriteFactory.MakeTexture(size, size, "PortalMFD_Frame", TextureWrapMode.Clamp, delegate(int x, int y)
            {
                bool edge = x < 2 || y < 2 || x >= size - 2 || y >= size - 2;
                return edge ? 1f : 0f;
            });
            // 200 px per unit: the 2 px border draws 1 layout unit wide
            Sprite s = Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), 200f, 0,
                SpriteMeshType.FullRect, new Vector4(4, 4, 4, 4));
            s.name = "PortalMFD_Frame";
            return s;
        }

        private static Sprite CreateShape(PortalShape shape)
        {
            bool large = shape == PortalShape.RingFine || shape == PortalShape.RingBold;
            int n = large ? 512 : ShapeSize;
            float pixel = 2.0f / n; // one pixel in shape space (-1..1)
            // Large rings: half width 1 px (two pixels wide) or 3 px (six pixels wide), outer edge at the rim
            float half = (shape == PortalShape.RingBold ? 3f : 1f) * pixel;
            Texture2D tex = SpriteFactory.MakeTexture(n, n, "PortalMFD_" + shape, TextureWrapMode.Clamp, delegate(int x, int y)
            {
                float v = ((y + 0.5f) / n) * 2f - 1f;
                float u = ((x + 0.5f) / n) * 2f - 1f;
                float d = large
                    ? Mathf.Abs(Mathf.Sqrt(u * u + v * v) - (1f - pixel - half)) - half
                    : Distance(shape, u, v);               // signed distance, negative inside
                return Mathf.Clamp01(0.5f - d / pixel);
            });
            Sprite s = Sprite.Create(tex, new Rect(0, 0, n, n), new Vector2(0.5f, 0.5f), 100f, 0, SpriteMeshType.FullRect);
            s.name = "PortalMFD_" + shape;
            return s;
        }

        #region Distance functions (shape space -1..1, y up)

        private static float Distance(PortalShape shape, float x, float y)
        {
            float r = Mathf.Sqrt(x * x + y * y);
            switch (shape)
            {
                case PortalShape.Disc:
                    return r - 0.94f;
                case PortalShape.Ring:
                    return Mathf.Abs(r - 0.94f) - 0.035f;
                case PortalShape.RingThick:
                    return Mathf.Abs(r - 0.86f) - 0.1f;
                case PortalShape.RingDotted:
                    {
                        float ring = Mathf.Abs(r - 0.84f) - 0.1f;
                        float ang = Mathf.Atan2(y, x) / (2f * Mathf.PI) * 8f;
                        float gap = Mathf.Abs(ang - Mathf.Round(ang)) - 0.3f; // 8 dashes
                        return Mathf.Max(ring, -gap * 0.9f);
                    }
                case PortalShape.HalfRingTop:
                    {
                        float ring = Mathf.Abs(r - 0.85f) - 0.1f;
                        return y < -0.05f ? Mathf.Max(ring, -y - 0.05f) : ring;
                    }
                case PortalShape.TriangleOutline:
                    return Mathf.Abs(Triangle(x, y)) - 0.05f;
                case PortalShape.TriangleThick:
                    return Mathf.Abs(Triangle(x, y) + 0.09f) - 0.09f;
                case PortalShape.TriangleFilled:
                    return Triangle(x, y);
                case PortalShape.Diamond:
                    return Mathf.Abs((Mathf.Abs(x) + Mathf.Abs(y)) * 0.7071f - 0.62f) - 0.05f;
                case PortalShape.Chevron:
                    // ">" made of two strokes meeting at the right
                    return Mathf.Min(Segment(x, y, -0.6f, 0.75f, 0.6f, 0f), Segment(x, y, -0.6f, -0.75f, 0.6f, 0f)) - 0.08f;
                case PortalShape.SquareOutline:
                    return Mathf.Abs(Mathf.Max(Mathf.Abs(x), Mathf.Abs(y)) - 0.8f) - 0.09f;
                case PortalShape.Arch:
                    {
                        float body;
                        if (y > 0.1f)
                        {
                            float ry = y - 0.1f;
                            body = Mathf.Sqrt(x * x + ry * ry) - 0.75f;
                        }
                        else
                        {
                            body = Mathf.Max(Mathf.Abs(x) - 0.75f, -0.85f - y);
                        }
                        return Mathf.Abs(body) - 0.1f;
                    }
                case PortalShape.RadarArc:
                    {
                        float ring = Mathf.Abs(r - 0.75f) - 0.13f;
                        return y > 0.05f ? Mathf.Max(ring, y - 0.05f) : ring;
                    }
                case PortalShape.PlayerArrow:
                    return Polygon3(x, y, 0f, 0.95f, -0.42f, -0.9f, 0.42f, -0.9f);
                case PortalShape.RadarEnemy:
                    {
                        float tri = Mathf.Abs(TriangleAt(x, y, -0.25f, 0.62f)) - 0.07f;
                        float stem = Segment(x, y, 0f, 0.18f, 0f, 0.98f) - 0.07f;
                        return Mathf.Min(tri, stem);
                    }
                case PortalShape.RadarFriendly:
                    {
                        float cy = y + 0.45f;
                        float ring = Mathf.Abs(Mathf.Sqrt(x * x + cy * cy) - 0.4f) - 0.08f;
                        float stem = Segment(x, y, 0f, -0.05f, 0f, 0.98f) - 0.07f;
                        return Mathf.Min(ring, stem);
                    }
                case PortalShape.RadarMissile:
                    {
                        float ring = Mathf.Abs(r - 0.42f) - 0.1f;
                        float s1 = Segment(x, y, 0f, 0.5f, 0f, 0.98f) - 0.08f;
                        float s2 = Segment(x, y, -0.5f, 0f, -0.95f, 0f) - 0.08f;
                        float s3 = Segment(x, y, 0.5f, 0f, 0.95f, 0f) - 0.08f;
                        return Mathf.Min(Mathf.Min(ring, s1), Mathf.Min(s2, s3));
                    }
                case PortalShape.AirHat:
                    return Mathf.Min(Segment(x, y, -0.85f, -0.4f, 0f, 0.4f), Segment(x, y, 0.85f, -0.4f, 0f, 0.4f)) - 0.12f;
            }
            return 1f;
        }

        private static float Triangle(float x, float y)
        {
            return TriangleAt(x, y, -0.78f, 0.88f);
        }

        /// <summary>Signed distance to an upward isosceles triangle with base at y0 and apex at y1.</summary>
        private static float TriangleAt(float x, float y, float y0, float y1)
        {
            float halfBase = (y1 - y0) * 0.577f;
            return Polygon3(x, y, 0f, y1, -halfBase, y0, halfBase, y0);
        }

        private static float Polygon3(float x, float y, float ax, float ay, float bx, float by, float cx, float cy)
        {
            float d = Mathf.Min(Segment(x, y, ax, ay, bx, by), Mathf.Min(Segment(x, y, bx, by, cx, cy), Segment(x, y, cx, cy, ax, ay)));
            bool inside = Side(x, y, ax, ay, bx, by) == Side(x, y, bx, by, cx, cy) && Side(x, y, bx, by, cx, cy) == Side(x, y, cx, cy, ax, ay);
            return inside ? -d : d;
        }

        private static bool Side(float x, float y, float ax, float ay, float bx, float by)
        {
            return (bx - ax) * (y - ay) - (by - ay) * (x - ax) >= 0f;
        }

        private static float Segment(float x, float y, float ax, float ay, float bx, float by)
        {
            float vx = bx - ax, vy = by - ay;
            float wx = x - ax, wy = y - ay;
            float t = Mathf.Clamp01((wx * vx + wy * vy) / (vx * vx + vy * vy));
            float dx = wx - vx * t, dy = wy - vy * t;
            return Mathf.Sqrt(dx * dx + dy * dy);
        }

        #endregion

        #region UI builders

        public static RectTransform Rect(string name, Transform parent, Vector2 anchorMin, Vector2 anchorMax, Vector2 pivot, Vector2 pos, Vector2 size)
        {
            GameObject go = new GameObject(name, typeof(RectTransform));
            go.layer = parent.gameObject.layer;
            RectTransform rt = (RectTransform)go.transform;
            rt.SetParent(parent, false);
            rt.anchorMin = anchorMin;
            rt.anchorMax = anchorMax;
            rt.pivot = pivot;
            rt.anchoredPosition = pos;
            rt.sizeDelta = size;
            return rt;
        }

        /// <summary>Rect stretched over the parent with an inset (negative size delta).</summary>
        public static RectTransform Stretch(string name, Transform parent, float inset)
        {
            return Rect(name, parent, Vector2.zero, Vector2.one, new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(-2f * inset, -2f * inset));
        }

        /// <summary>Rect anchored at one point of the parent (anchor = pivot).</summary>
        public static RectTransform At(string name, Transform parent, Vector2 anchor, Vector2 pos, Vector2 size)
        {
            return Rect(name, parent, anchor, anchor, new Vector2(0.5f, 0.5f), pos, size);
        }

        public static Image AddImage(RectTransform rt, Color color, PortalShape? shape)
        {
            Image img = rt.gameObject.AddComponent<Image>();
            img.raycastTarget = false;
            img.color = color;
            if (shape.HasValue)
            {
                img.sprite = Get(shape.Value);
                if (shape.Value == PortalShape.Frame) img.type = Image.Type.Sliced;
            }
            return img;
        }

        public static Image Shape(string name, Transform parent, Vector2 anchor, Vector2 pos, float size, PortalShape shape, Color color)
        {
            return AddImage(At(name, parent, anchor, pos, new Vector2(size, size)), color, shape);
        }

        public static Image Box(string name, Transform parent, Vector2 anchor, Vector2 pos, Vector2 size, Color color)
        {
            return AddImage(At(name, parent, anchor, pos, size), color, null);
        }

        public static TextMeshProUGUI Text(string name, Transform parent, Vector2 anchor, Vector2 pos, Vector2 size,
            string text, float fontSize, Color color, TextAlignmentOptions align, TMP_FontAsset font)
        {
            RectTransform rt = At(name, parent, anchor, pos, size);
            TextMeshProUGUI t = rt.gameObject.AddComponent<TextMeshProUGUI>();
            if (font != null) t.font = font;
            t.text = text;
            t.fontSize = fontSize;
            t.color = color;
            t.alignment = align;
            t.enableWordWrapping = false;
            t.overflowMode = TextOverflowModes.Overflow;
            t.raycastTarget = false;
            return t;
        }

        /// <summary>
        /// A straight line from a to b (parent space) as a thin Image.
        /// </summary>
        public static Image Line(string name, Transform parent, Vector2 a, Vector2 b, float width, Color color)
        {
            RectTransform rt = Rect(name, parent, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0f), a, new Vector2(width, 1f));
            Image img = AddImage(rt, color, null);
            SetLine(rt, a, b, width);
            return img;
        }

        public static void SetLine(RectTransform rt, Vector2 a, Vector2 b, float width)
        {
            Vector2 d = b - a;
            rt.anchoredPosition = a;
            rt.sizeDelta = new Vector2(width, d.magnitude);
            rt.localEulerAngles = new Vector3(0f, 0f, -Mathf.Atan2(d.x, d.y) * Mathf.Rad2Deg);
        }

        /// <summary>
        /// A dashed line from a to b: the dash texture repeats along the line (reference Tiled 'ui-dashed-50').
        /// </summary>
        public static RawImage DashedLine(string name, Transform parent, float width, Color color)
        {
            RectTransform rt = Rect(name, parent, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0f), Vector2.zero, new Vector2(width, 1f));
            RawImage img = rt.gameObject.AddComponent<RawImage>();
            img.raycastTarget = false;
            img.texture = DashTexture();
            img.color = color;
            return img;
        }

        /// <summary>
        /// Places a DashedLine from a to b. The dash period stays the same whatever the length.
        /// </summary>
        public static void SetDashed(RawImage img, Vector2 a, Vector2 b, float width, float period)
        {
            SetLine(img.rectTransform, a, b, width);
            img.uvRect = new UnityEngine.Rect(0f, 0f, 1f, (b - a).magnitude / period);
        }

        /// <summary>One dash period: the lower half opaque, the upper half clear. Repeats along v.</summary>
        public static Texture2D DashTexture()
        {
            return RWRConeGraphic.GetOrCreateDashedLineSprite().texture;
        }

        /// <summary>
        /// Circle arc of the given total angle, centered on the top of the circle (Radial360 fill of a ring sprite).
        /// The rect may still be rotated to point the arc elsewhere.
        /// </summary>
        public static Image Arc(string name, Transform parent, Vector2 pos, float diameter, float angleDeg, PortalShape ring, Color color)
        {
            Image img = Shape(name, parent, new Vector2(0.5f, 0.5f), pos, diameter, ring, color);
            SetArc(img, angleDeg);
            return img;
        }

        public static void SetArc(Image img, float angleDeg)
        {
            img.type = Image.Type.Filled;
            img.fillMethod = Image.FillMethod.Radial360;
            img.fillOrigin = (int)Image.Origin360.Top;
            img.fillClockwise = true;
            img.fillAmount = Mathf.Clamp01(angleDeg / 360f);
            // The fill starts at the top and runs clockwise: turn it back by half the angle
            img.rectTransform.localEulerAngles = new Vector3(0f, 0f, angleDeg * 0.5f);
        }

        /// <summary>On / off blink with the given half period (reference: 0.3 s for an uncertain track, 0.15 s for a launch).</summary>
        public static bool Blink(float now, float halfPeriod)
        {
            return Mathf.Repeat(now, halfPeriod * 2f) < halfPeriod;
        }

        #endregion
    }
}
