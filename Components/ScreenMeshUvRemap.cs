using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using CnoEvil.Core;

namespace CnoEvil.Components
{
    /// <summary>
    /// A copy of a cockpit screen mesh in which one UV island samples another part of the screen texture (ADR-68).
    /// A small cockpit dial can sample texels inside a main screen (Su-30: the round dial above the main glass samples
    /// StatusGauges in the left part), so it shows the portal page that covers them. The copy moves the TexCoord0 of
    /// that island by a u shift; the host moves the vanilla panel by the same shift, so the dial keeps its gauge.
    /// The mesh of a mod bundle is not CPU-readable: the copy reads the GPU vertex and index buffers.
    /// Apply swaps the copy onto the MeshFilter, Restore puts the original back.
    /// </summary>
    public class ScreenMeshUvRemap
    {
        // UV tolerance around the island rect: about 1 texel of a 1024 px texture
        private const float UvTolerance = 0.002f;

        private MeshFilter _filter;
        private Mesh _original;
        private Mesh _copy;
        private bool _applied;

        /// <summary>
        /// Makes the copy for the renderer: the islands whose UVs are all inside islandUv (fraction of the texture,
        /// origin bottom left) and which cover at least half of it move by shiftU. Null and one log line when the mesh
        /// cannot be read or no island fits.
        /// </summary>
        public static ScreenMeshUvRemap Create(Renderer renderer, Rect islandUv, float shiftU, string label)
        {
            MeshFilter filter = renderer != null ? renderer.GetComponent<MeshFilter>() : null;
            Mesh src = filter != null ? filter.sharedMesh : null;
            if (src == null)
            {
                Warn(label, "no MeshFilter mesh on the screen renderer");
                return null;
            }
            string reason;
            Mesh copy = BuildCopy(src, islandUv, shiftU, out reason);
            if (copy == null)
            {
                Warn(label, reason);
                return null;
            }
            ScreenMeshUvRemap remap = new ScreenMeshUvRemap();
            remap._filter = filter;
            remap._original = src;
            remap._copy = copy;
            return remap;
        }

        public void Apply()
        {
            if (_applied || _filter == null || _copy == null) return;
            if (_filter.sharedMesh != _original) return;
            _filter.sharedMesh = _copy;
            _applied = true;
        }

        public void Restore()
        {
            if (!_applied) return;
            _applied = false;
            if (_filter != null && _filter.sharedMesh == _copy) _filter.sharedMesh = _original;
        }

        /// <summary>Restore and destroy the copy.</summary>
        public void Dispose()
        {
            Restore();
            if (_copy != null) UnityEngine.Object.Destroy(_copy);
            _copy = null;
        }

        private static void Warn(string label, string reason)
        {
            if (CnoEvilPlugin.Log != null)
            {
                CnoEvilPlugin.Log.LogWarning(string.Format("TacScreenPortalMFD: {0} dial UV remap skipped: {1}.", label, reason));
            }
        }

        private static Mesh BuildCopy(Mesh src, Rect islandUv, float shiftU, out string reason)
        {
            reason = null;
            VertexAttributeDescriptor[] attrs = src.GetVertexAttributes();
            int uvAttr = -1;
            for (int i = 0; i < attrs.Length; i++)
            {
                if (attrs[i].attribute == VertexAttribute.TexCoord0) uvAttr = i;
            }
            if (uvAttr < 0)
            {
                reason = "the mesh has no TexCoord0";
                return null;
            }
            VertexAttributeFormat uvFormat = attrs[uvAttr].format;
            if (uvFormat != VertexAttributeFormat.Float32 && uvFormat != VertexAttributeFormat.Float16)
            {
                reason = "TexCoord0 format " + uvFormat;
                return null;
            }
            int vertexCount = src.vertexCount;
            if (vertexCount <= 0)
            {
                reason = "no vertices";
                return null;
            }

            // GPU vertex streams
            int streams = src.vertexBufferCount;
            byte[][] data = new byte[streams][];
            for (int s = 0; s < streams; s++)
            {
                int stride = src.GetVertexBufferStride(s);
                data[s] = new byte[vertexCount * stride];
                if (stride <= 0) continue;
                using (GraphicsBuffer gb = src.GetVertexBuffer(s))
                {
                    if (gb == null)
                    {
                        reason = "vertex buffer " + s + " not available";
                        return null;
                    }
                    gb.GetData(data[s]);
                }
            }

            // GPU index buffer; triangles of all submeshes
            bool wide = src.indexFormat == IndexFormat.UInt32;
            int[] indices;
            using (GraphicsBuffer ib = src.GetIndexBuffer())
            {
                if (ib == null)
                {
                    reason = "index buffer not available";
                    return null;
                }
                byte[] raw = new byte[ib.count * ib.stride];
                ib.GetData(raw);
                int count = raw.Length / (wide ? 4 : 2);
                indices = new int[count];
                for (int i = 0; i < count; i++)
                {
                    indices[i] = wide ? BitConverter.ToInt32(raw, i * 4) : BitConverter.ToUInt16(raw, i * 2);
                }
            }

            int uvStream = attrs[uvAttr].stream;
            int uvStride = src.GetVertexBufferStride(uvStream);
            int uvOffset = src.GetVertexAttributeOffset(VertexAttribute.TexCoord0);
            byte[] uvData = data[uvStream];

            // Islands: vertices joined by triangles (union-find)
            int[] parent = new int[vertexCount];
            for (int i = 0; i < vertexCount; i++) parent[i] = i;
            for (int sm = 0; sm < src.subMeshCount; sm++)
            {
                SubMeshDescriptor d = src.GetSubMesh(sm);
                if (d.topology != MeshTopology.Triangles) continue;
                for (int t = d.indexStart; t + 2 < d.indexStart + d.indexCount && t + 2 < indices.Length; t += 3)
                {
                    int a = indices[t] + d.baseVertex, b = indices[t + 1] + d.baseVertex, c = indices[t + 2] + d.baseVertex;
                    if (a < 0 || b < 0 || c < 0 || a >= vertexCount || b >= vertexCount || c >= vertexCount) continue;
                    Union(parent, a, b);
                    Union(parent, a, c);
                }
            }

            Rect outer = Rect.MinMaxRect(islandUv.xMin - UvTolerance, islandUv.yMin - UvTolerance, islandUv.xMax + UvTolerance, islandUv.yMax + UvTolerance);
            Dictionary<int, Rect> bounds = new Dictionary<int, Rect>();
            HashSet<int> outside = new HashSet<int>();
            for (int v = 0; v < vertexCount; v++)
            {
                int root = Find(parent, v);
                Vector2 uv = ReadUv(uvData, v * uvStride + uvOffset, uvFormat);
                if (!outer.Contains(uv)) outside.Add(root);
                Rect r;
                if (bounds.TryGetValue(root, out r)) bounds[root] = Rect.MinMaxRect(Mathf.Min(r.xMin, uv.x), Mathf.Min(r.yMin, uv.y), Mathf.Max(r.xMax, uv.x), Mathf.Max(r.yMax, uv.y));
                else bounds[root] = new Rect(uv, Vector2.zero);
            }
            // An island inside the rect that covers at least half of it: not a one-texel island of a flat colour
            HashSet<int> moved = new HashSet<int>();
            foreach (KeyValuePair<int, Rect> kv in bounds)
            {
                if (outside.Contains(kv.Key)) continue;
                if (kv.Value.width < 0.5f * islandUv.width || kv.Value.height < 0.5f * islandUv.height) continue;
                moved.Add(kv.Key);
            }
            if (moved.Count == 0)
            {
                reason = "no UV island inside the dial rect (the GPU readback can be empty)";
                return null;
            }
            int movedVertices = 0;
            for (int v = 0; v < vertexCount; v++)
            {
                if (!moved.Contains(Find(parent, v))) continue;
                int at = v * uvStride + uvOffset;
                Vector2 uv = ReadUv(uvData, at, uvFormat);
                WriteU(uvData, at, uvFormat, uv.x + shiftU);
                movedVertices++;
            }

            Mesh copy = new Mesh();
            copy.name = src.name + "_PortalMFD";
            MeshUpdateFlags flags = MeshUpdateFlags.DontRecalculateBounds | MeshUpdateFlags.DontValidateIndices;
            copy.SetVertexBufferParams(vertexCount, attrs);
            for (int s = 0; s < streams; s++)
            {
                if (data[s].Length > 0) copy.SetVertexBufferData(data[s], 0, 0, data[s].Length, s, flags);
            }
            copy.SetIndexBufferParams(indices.Length, src.indexFormat);
            if (wide)
            {
                uint[] ui = new uint[indices.Length];
                for (int i = 0; i < ui.Length; i++) ui[i] = (uint)indices[i];
                copy.SetIndexBufferData(ui, 0, 0, ui.Length, flags);
            }
            else
            {
                ushort[] us = new ushort[indices.Length];
                for (int i = 0; i < us.Length; i++) us[i] = (ushort)indices[i];
                copy.SetIndexBufferData(us, 0, 0, us.Length, flags);
            }
            copy.subMeshCount = src.subMeshCount;
            for (int sm = 0; sm < src.subMeshCount; sm++) copy.SetSubMesh(sm, src.GetSubMesh(sm), flags);
            copy.bounds = src.bounds;
            copy.UploadMeshData(true);

            if (CnoEvilPlugin.Log != null)
            {
                CnoEvilPlugin.Log.LogInfo(string.Format("TacScreenPortalMFD: mesh {0}: {1} UV island(s), {2} vertices moved by u {3:F4}.",
                    src.name, moved.Count, movedVertices, shiftU));
            }
            return copy;
        }

        private static int Find(int[] parent, int i)
        {
            while (parent[i] != i)
            {
                parent[i] = parent[parent[i]];
                i = parent[i];
            }
            return i;
        }

        private static void Union(int[] parent, int a, int b)
        {
            int ra = Find(parent, a), rb = Find(parent, b);
            if (ra != rb) parent[ra] = rb;
        }

        private static Vector2 ReadUv(byte[] data, int at, VertexAttributeFormat format)
        {
            if (format == VertexAttributeFormat.Float32) return new Vector2(BitConverter.ToSingle(data, at), BitConverter.ToSingle(data, at + 4));
            return new Vector2(Mathf.HalfToFloat(BitConverter.ToUInt16(data, at)), Mathf.HalfToFloat(BitConverter.ToUInt16(data, at + 2)));
        }

        private static void WriteU(byte[] data, int at, VertexAttributeFormat format, float u)
        {
            byte[] b = format == VertexAttributeFormat.Float32 ? BitConverter.GetBytes(u) : BitConverter.GetBytes(Mathf.FloatToHalf(u));
            Buffer.BlockCopy(b, 0, data, at, b.Length);
        }
    }
}
