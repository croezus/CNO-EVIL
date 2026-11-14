using UnityEngine;

namespace CnoEvil.Core
{
    /// <summary>
    /// Procedural white textures and sprites (RWR cones, map lines, portal MFD shapes): RGBA32, no mipmaps, bilinear.
    /// The alpha function gives the alpha of each pixel; the Image or RawImage color tints the result.
    /// </summary>
    public static class SpriteFactory
    {
        public delegate float AlphaFn(int x, int y);

        public static Texture2D NewTexture(int w, int h, string name, TextureWrapMode wrap)
        {
            Texture2D tex = new Texture2D(w, h, TextureFormat.RGBA32, false);
            tex.name = name;
            tex.wrapMode = wrap;
            tex.filterMode = FilterMode.Bilinear;
            return tex;
        }

        /// <summary>White texture with alpha(x, y), uploaded and made non-readable.</summary>
        public static Texture2D MakeTexture(int w, int h, string name, TextureWrapMode wrap, AlphaFn alpha)
        {
            Texture2D tex = NewTexture(w, h, name, wrap);
            Color[] pixels = new Color[w * h];
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++) pixels[y * w + x] = new Color(1.0f, 1.0f, 1.0f, alpha(x, y));
            }
            tex.SetPixels(pixels);
            tex.Apply(false, true);
            return tex;
        }

        /// <summary>
        /// Sprite over the whole MakeTexture texture: 100 px per unit, full rect mesh.
        /// The texture is named name + "_Texture", the sprite name + "_Sprite".
        /// </summary>
        public static Sprite MakeSprite(int w, int h, Vector2 pivot, TextureWrapMode wrap, string name, AlphaFn alpha)
        {
            Texture2D tex = MakeTexture(w, h, name + "_Texture", wrap, alpha);
            Sprite sprite = Sprite.Create(tex, new Rect(0, 0, w, h), pivot, 100.0f, 0, SpriteMeshType.FullRect);
            sprite.name = name + "_Sprite";
            return sprite;
        }
    }
}
