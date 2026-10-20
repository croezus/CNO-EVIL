using UnityEngine;
using UnityEngine.UI;

namespace CnoEvil.Components
{
    /// <summary>
    /// Closed polyline drawn as one quad per segment. The owner writes Points and calls SetVerticesDirty.
    /// </summary>
    public class EmScanLineGraphic : MaskableGraphic
    {
        public Vector2[] Points;
        public float Thickness = 1.0f;

        protected override void OnPopulateMesh(VertexHelper vh)
        {
            vh.Clear();
            Vector2[] pts = Points;
            if (pts == null || pts.Length < 2) return;

            Color32 c = color;
            float half = Thickness * 0.5f;
            int n = pts.Length;
            int v = 0;

            for (int i = 0; i < n; i++)
            {
                Vector2 a = pts[i];
                Vector2 b = pts[(i + 1) % n];
                Vector2 d = b - a;
                float len = d.magnitude;
                if (len < 0.0001f) continue;

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
