using UnityEngine;
using UnityEngine.UI;

namespace CnoEvil.Components
{
    /// <summary>
    /// Separate line segments drawn as one quad each: Points[2i] -> Points[2i+1] for i below Count.
    /// Optional Alphas[i] scales the colour alpha and Widths[i] replaces Thickness per segment.
    /// The owner writes Points and Count and calls SetVerticesDirty.
    /// </summary>
    public class HudDashGraphic : MaskableGraphic
    {
        public Vector2[] Points;
        public float[] Alphas;
        public float[] Widths;
        public int Count;
        public float Thickness = 1.0f;

        protected override void OnPopulateMesh(VertexHelper vh)
        {
            vh.Clear();
            Vector2[] pts = Points;
            if (pts == null) return;

            Color baseColor = color;
            float[] alphas = Alphas;
            float[] widths = Widths;
            int n = Mathf.Min(Count, pts.Length / 2);
            int v = 0;

            for (int i = 0; i < n; i++)
            {
                Vector2 a = pts[i * 2];
                Vector2 b = pts[i * 2 + 1];
                Vector2 d = b - a;
                float len = d.magnitude;
                if (len < 0.0001f) continue;

                Color segColor = baseColor;
                if (alphas != null && i < alphas.Length) segColor.a *= alphas[i];
                if (segColor.a < 0.004f) continue;
                Color32 c = segColor;

                float half = ((widths != null && i < widths.Length) ? widths[i] : Thickness) * 0.5f;
                Vector2 nrm = new Vector2(-d.y, d.x) * (half / len);

                vh.AddVert(a - nrm, c, Vector2.zero);
                vh.AddVert(a + nrm, c, Vector2.zero);
                vh.AddVert(b + nrm, c, Vector2.zero);
                vh.AddVert(b - nrm, c, Vector2.zero);
                vh.AddTriangle(v, v + 1, v + 2);
                vh.AddTriangle(v, v + 2, v + 3);
                v += 4;
            }
        }
    }
}
