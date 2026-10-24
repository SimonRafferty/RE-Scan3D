// The in-memory triangle mesh. Flat arrays (not objects per vertex) so multi-million-triangle scans stay
// compact and cache-friendly; floats for storage, doubles for every computation.
using System;
using ScanRE.Core.Math3;

namespace ScanRE.Core.Mesh
{
    public sealed class TriMesh
    {
        /// <summary>x0,y0,z0,x1,... in millimetres.</summary>
        public float[] Positions;
        /// <summary>a0,b0,c0,a1,... vertex indices, counter-clockwise seen from outside.</summary>
        public int[] Indices;
        /// <summary>Optional r,g,b per vertex (null when the file had no colour).</summary>
        public byte[] Colors;

        public TriMesh(float[] positions, int[] indices, byte[] colors = null)
        {
            Positions = positions;
            Indices = indices;
            Colors = colors;
        }

        public int VertexCount { get { return Positions.Length / 3; } }
        public int TriangleCount { get { return Indices.Length / 3; } }

        public Vec3 Vertex(int i)
        {
            int k = 3 * i;
            return new Vec3(Positions[k], Positions[k + 1], Positions[k + 2]);
        }

        public void SetVertex(int i, Vec3 p)
        {
            int k = 3 * i;
            Positions[k] = (float)p.X; Positions[k + 1] = (float)p.Y; Positions[k + 2] = (float)p.Z;
        }

        public void Triangle(int t, out Vec3 a, out Vec3 b, out Vec3 c)
        {
            int k = 3 * t;
            a = Vertex(Indices[k]); b = Vertex(Indices[k + 1]); c = Vertex(Indices[k + 2]);
        }

        /// <summary>Unnormalised normal (length = 2 x area).</summary>
        public Vec3 TriangleNormalRaw(int t)
        {
            Vec3 a, b, c;
            Triangle(t, out a, out b, out c);
            return Vec3.Cross(b - a, c - a);
        }

        public Vec3 TriangleNormal(int t) { return TriangleNormalRaw(t).Normalized(); }
        public double TriangleArea(int t) { return 0.5 * TriangleNormalRaw(t).Length; }

        public Vec3 TriangleCentroid(int t)
        {
            Vec3 a, b, c;
            Triangle(t, out a, out b, out c);
            return (a + b + c) / 3.0;
        }

        public void Bounds(out Vec3 min, out Vec3 max)
        {
            min = new Vec3(double.MaxValue, double.MaxValue, double.MaxValue);
            max = new Vec3(double.MinValue, double.MinValue, double.MinValue);
            for (int i = 0; i < VertexCount; i++)
            {
                var p = Vertex(i);
                min = Vec3.Min(min, p); max = Vec3.Max(max, p);
            }
        }

        /// <summary>Mean edge length: a scale for default tolerances.</summary>
        public double MeanEdgeLength(int sample = 20000)
        {
            int n = TriangleCount;
            if (n == 0) return 1;
            int step = Math.Max(1, n / sample);
            double sum = 0; int cnt = 0;
            for (int t = 0; t < n; t += step)
            {
                Vec3 a, b, c;
                Triangle(t, out a, out b, out c);
                sum += (b - a).Length + (c - b).Length + (a - c).Length;
                cnt += 3;
            }
            return sum / cnt;
        }

        /// <summary>A transformed deep copy (the display copy follows the alignment; the full-res mesh never moves).</summary>
        public TriMesh Transformed(Rigid t)
        {
            var p = new float[Positions.Length];
            for (int i = 0; i < VertexCount; i++)
            {
                var q = t.Apply(Vertex(i));
                p[3 * i] = (float)q.X; p[3 * i + 1] = (float)q.Y; p[3 * i + 2] = (float)q.Z;
            }
            return new TriMesh(p, (int[])Indices.Clone(), Colors == null ? null : (byte[])Colors.Clone());
        }

        /// <summary>Remove triangles with repeated vertices (degenerate after welding/collapse) and unused vertices.</summary>
        public TriMesh Compacted()
        {
            int nt = TriangleCount, nv = VertexCount;
            var keep = new bool[nt];
            int kept = 0;
            for (int t = 0; t < nt; t++)
            {
                int a = Indices[3 * t], b = Indices[3 * t + 1], c = Indices[3 * t + 2];
                if (a != b && b != c && a != c) { keep[t] = true; kept++; }
            }
            var remap = new int[nv];
            for (int i = 0; i < nv; i++) remap[i] = -1;
            int nNew = 0;
            var idx = new int[3 * kept];
            int w = 0;
            for (int t = 0; t < nt; t++)
            {
                if (!keep[t]) continue;
                for (int k = 0; k < 3; k++)
                {
                    int v = Indices[3 * t + k];
                    if (remap[v] < 0) remap[v] = nNew++;
                    idx[w++] = remap[v];
                }
            }
            var pos = new float[3 * nNew];
            byte[] col = Colors == null ? null : new byte[3 * nNew];
            for (int i = 0; i < nv; i++)
            {
                int j = remap[i];
                if (j < 0) continue;
                Array.Copy(Positions, 3 * i, pos, 3 * j, 3);
                if (col != null) Array.Copy(Colors, 3 * i, col, 3 * j, 3);
            }
            return new TriMesh(pos, idx, col);
        }

        /// <summary>Keep only the listed triangles (a sub-mesh, e.g. a fitted region for display).</summary>
        public TriMesh Subset(System.Collections.Generic.IList<int> tris)
        {
            var idx = new int[3 * tris.Count];
            for (int i = 0; i < tris.Count; i++)
                Array.Copy(Indices, 3 * tris[i], idx, 3 * i, 3);
            return new TriMesh(Positions, idx, Colors).Compacted();
        }
    }
}
