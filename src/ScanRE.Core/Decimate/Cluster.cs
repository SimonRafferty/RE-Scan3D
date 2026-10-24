// Quadric vertex clustering (Lindstrom, "Out-of-core simplification of large polygonal models", 2000):
// snap vertices to a uniform grid, place each cell's single vertex at the minimiser of the summed face
// quadrics of its triangles. O(n) time and modest memory, and the quadric placement keeps sharp edges
// and fillet tangent lines far better than averaging. Used to bring multi-million-triangle scans down
// quickly before the slower, higher-quality edge-collapse pass (Qem.cs). Measured: results/bench_*.csv.
using System;
using System.Collections.Generic;
using ScanRE.Core.Jobs;
using ScanRE.Core.Math3;
using ScanRE.Core.Mesh;

namespace ScanRE.Core.Decimate
{
    public static class Cluster
    {
        public static TriMesh Simplify(TriMesh m, int targetTriangles, JobContext job)
        {
            // surface area -> first guess of cell size (occupied cells ≈ area / cell², triangles ≈ 2 x cells)
            double area = 0;
            for (int t = 0; t < m.TriangleCount; t++) area += m.TriangleArea(t);
            double cell = Math.Sqrt(2 * area / Math.Max(1, targetTriangles));
            TriMesh best = null;
            for (int pass = 0; pass < 4; pass++)
            {
                var r = Run(m, cell, job);
                best = r;
                double ratio = (double)r.TriangleCount / targetTriangles;
                if (ratio > 0.8 && ratio < 1.25) break;
                cell *= Math.Sqrt(ratio);   // too many triangles -> bigger cells
            }
            return best;
        }

        private struct CellKey : IEquatable<CellKey>
        {
            public int X, Y, Z;
            public bool Equals(CellKey o) { return X == o.X && Y == o.Y && Z == o.Z; }
            public override bool Equals(object o) { return o is CellKey && Equals((CellKey)o); }
            public override int GetHashCode() { unchecked { return (X * 73856093) ^ (Y * 19349663) ^ (Z * 83492791); } }
        }

        private static TriMesh Run(TriMesh m, double cell, JobContext job)
        {
            int nv = m.VertexCount, nt = m.TriangleCount;
            Vec3 lo, hi;
            m.Bounds(out lo, out hi);
            var cellOf = new int[nv];
            var index = new Dictionary<CellKey, int>(nv / 4 + 16);
            var sumPos = new List<Vec3>();
            var count = new List<int>();
            for (int i = 0; i < nv; i++)
            {
                var p = m.Vertex(i);
                var k = new CellKey { X = (int)Math.Floor((p.X - lo.X) / cell), Y = (int)Math.Floor((p.Y - lo.Y) / cell), Z = (int)Math.Floor((p.Z - lo.Z) / cell) };
                int c;
                if (!index.TryGetValue(k, out c)) { c = sumPos.Count; index.Add(k, c); sumPos.Add(Vec3.Zero); count.Add(0); }
                cellOf[i] = c;
                sumPos[c] = sumPos[c] + p;
                count[c]++;
                job.Step("Simplifying (clustering)", i, 2L * nv + nt, 65536);
            }
            int nc = sumPos.Count;
            var q = new Qem.Q[nc];
            for (int t = 0; t < nt; t++)
            {
                Vec3 a, b, c2;
                m.Triangle(t, out a, out b, out c2);
                var n = Vec3.Cross(b - a, c2 - a);
                double a2 = n.Length;
                if (a2 < 1e-30) continue;
                n = n / a2;
                var fq = Qem.Q.Plane(n.X, n.Y, n.Z, -Vec3.Dot(n, a), 0.5 * a2);
                for (int k = 0; k < 3; k++) q[cellOf[m.Indices[3 * t + k]]].Add(fq);
                job.Step("Simplifying (clustering)", nv + t, 2L * nv + nt, 65536);
            }
            var pos = new float[3 * nc];
            for (int c = 0; c < nc; c++)
            {
                var mean = sumPos[c] / count[c];
                Vec3 opt;
                // the optimum must stay near its cell, otherwise near-flat cells put vertices far away
                if (!q[c].Optimum(out opt) || (opt - mean).Length > 1.5 * cell) opt = mean;
                pos[3 * c] = (float)opt.X; pos[3 * c + 1] = (float)opt.Y; pos[3 * c + 2] = (float)opt.Z;
            }
            // re-index triangles; drop collapsed ones and duplicates
            var idx = new List<int>(nt / 4);
            var seen = new HashSet<CellKey>();
            for (int t = 0; t < nt; t++)
            {
                int a = cellOf[m.Indices[3 * t]], b = cellOf[m.Indices[3 * t + 1]], c = cellOf[m.Indices[3 * t + 2]];
                if (a == b || b == c || a == c) continue;
                // order-independent key of the three cells (orientation kept from the first triangle seen)
                int x = Math.Min(a, Math.Min(b, c)), z = Math.Max(a, Math.Max(b, c)), y = a + b + c - x - z;
                if (!seen.Add(new CellKey { X = x, Y = y, Z = z })) continue;
                idx.Add(a); idx.Add(b); idx.Add(c);
                job.Step("Simplifying (clustering)", 2L * nv + t, 2L * nv + nt, 65536);
            }
            return new TriMesh(pos, idx.ToArray(), null).Compacted();
        }
    }
}
