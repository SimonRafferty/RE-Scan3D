// Vertex welding. STL is a triangle soup: identical corners are bit-identical floats, so an exact-key hash
// is both the fastest and the correct weld (no tolerance that could merge genuinely distinct points).
using System;
using System.Collections.Generic;
using RES3D.Core.Jobs;

namespace RES3D.Core.Mesh
{
    public static class Weld
    {
        private struct Key : IEquatable<Key>
        {
            public float X, Y, Z;
            public bool Equals(Key o) { return X == o.X && Y == o.Y && Z == o.Z; }
            public override bool Equals(object o) { return o is Key && Equals((Key)o); }
            public override int GetHashCode()
            {
                unchecked
                {
                    // -0.0f and +0.0f compare equal, so hash them the same
                    int hx = X == 0 ? 0 : X.GetHashCode(), hy = Y == 0 ? 0 : Y.GetHashCode(), hz = Z == 0 ? 0 : Z.GetHashCode();
                    return (hx * 73856093) ^ (hy * 19349663) ^ (hz * 83492791);
                }
            }
        }

        /// <summary>Weld a soup of 9 floats per triangle.</summary>
        public static TriMesh FromSoup(float[] soup, JobContext job)
        {
            int nCorner = soup.Length / 3;
            var map = new Dictionary<Key, int>(nCorner / 5 + 16);
            var pos = new List<float>(nCorner);
            var idx = new int[nCorner];
            for (int i = 0; i < nCorner; i++)
            {
                var k = new Key { X = soup[3 * i], Y = soup[3 * i + 1], Z = soup[3 * i + 2] };
                int v;
                if (!map.TryGetValue(k, out v))
                {
                    v = pos.Count / 3;
                    map.Add(k, v);
                    pos.Add(k.X); pos.Add(k.Y); pos.Add(k.Z);
                }
                idx[i] = v;
                job.Step("Welding vertices", i, nCorner, 65536);
            }
            return new TriMesh(pos.ToArray(), idx).Compacted();
        }

        /// <summary>Weld an already-indexed mesh whose writer duplicated vertices (OBJ/PLY per-face vertices).</summary>
        public static TriMesh Indexed(TriMesh m, JobContext job)
        {
            int nv = m.VertexCount;
            var map = new Dictionary<Key, int>(nv + 16);
            var remap = new int[nv];
            var pos = new List<float>(m.Positions.Length);
            var col = m.Colors == null ? null : new List<byte>(m.Colors.Length);
            for (int i = 0; i < nv; i++)
            {
                var k = new Key { X = m.Positions[3 * i], Y = m.Positions[3 * i + 1], Z = m.Positions[3 * i + 2] };
                int v;
                if (!map.TryGetValue(k, out v))
                {
                    v = pos.Count / 3;
                    map.Add(k, v);
                    pos.Add(k.X); pos.Add(k.Y); pos.Add(k.Z);
                    if (col != null) { col.Add(m.Colors[3 * i]); col.Add(m.Colors[3 * i + 1]); col.Add(m.Colors[3 * i + 2]); }
                }
                remap[i] = v;
                job.Step("Welding vertices", i, nv, 65536);
            }
            var idx = new int[m.Indices.Length];
            for (int i = 0; i < idx.Length; i++)
            {
                int v = m.Indices[i];
                if (v < 0 || v >= nv) throw new FormatException("face refers to vertex " + v + " of " + nv);
                idx[i] = remap[v];
            }
            return new TriMesh(pos.ToArray(), idx, col == null ? null : col.ToArray()).Compacted();
        }
    }
}
