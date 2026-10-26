// Triangle adjacency (across shared edges), connected components, island removal and display-only smoothing.
using System;
using System.Collections.Generic;
using RES3D.Core.Jobs;
using RES3D.Core.Math3;

namespace RES3D.Core.Mesh
{
    /// <summary>For each triangle edge k (0: a-b, 1: b-c, 2: c-a), the neighbouring triangle or -1.</summary>
    public sealed class TriAdjacency
    {
        public readonly int[] Neighbour;   // 3 per triangle

        public TriAdjacency(TriMesh m, JobContext job)
        {
            int nt = m.TriangleCount;
            Neighbour = new int[3 * nt];
            for (int i = 0; i < Neighbour.Length; i++) Neighbour[i] = -1;
            // sort half-edges by undirected key; equal keys are the two sides of an edge. Non-manifold
            // edges (3+ triangles) pair up arbitrarily, which is harmless for region growing.
            var keys = new long[3 * nt];
            var ids = new int[3 * nt];
            for (int t = 0; t < nt; t++)
                for (int k = 0; k < 3; k++)
                {
                    int a = m.Indices[3 * t + k], b = m.Indices[3 * t + (k + 1) % 3];
                    keys[3 * t + k] = a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;
                    ids[3 * t + k] = 3 * t + k;
                }
            job.Report("Building adjacency", 0.2);
            Array.Sort(keys, ids);
            job.Report("Building adjacency", 0.8);
            for (int i = 0; i + 1 < keys.Length; i++)
            {
                if (keys[i] != keys[i + 1]) continue;
                int h0 = ids[i], h1 = ids[i + 1];
                Neighbour[h0] = h1 / 3;
                Neighbour[h1] = h0 / 3;
                i++;
            }
        }
    }

    public static class Topology
    {
        /// <summary>Component id per triangle; returns the number of components.</summary>
        public static int Components(TriMesh m, TriAdjacency adj, out int[] comp)
        {
            int nt = m.TriangleCount;
            comp = new int[nt];
            for (int i = 0; i < nt; i++) comp[i] = -1;
            int nc = 0;
            var stack = new Stack<int>();
            for (int s = 0; s < nt; s++)
            {
                if (comp[s] >= 0) continue;
                comp[s] = nc;
                stack.Push(s);
                while (stack.Count > 0)
                {
                    int t = stack.Pop();
                    for (int k = 0; k < 3; k++)
                    {
                        int n = adj.Neighbour[3 * t + k];
                        if (n >= 0 && comp[n] < 0) { comp[n] = nc; stack.Push(n); }
                    }
                }
                nc++;
            }
            return nc;
        }

        /// <summary>Remove disconnected islands with fewer than `minFraction` of the largest component's triangles.</summary>
        public static TriMesh RemoveIslands(TriMesh m, double minFraction, JobContext job, out int removedTriangles)
        {
            removedTriangles = 0;
            if (minFraction <= 0) return m;
            var adj = new TriAdjacency(m, job);
            int[] comp;
            int nc = Components(m, adj, out comp);
            if (nc <= 1) return m;
            var size = new int[nc];
            foreach (int c in comp) size[c]++;
            int largest = 0;
            foreach (int s in size) largest = Math.Max(largest, s);
            int min = (int)Math.Ceiling(largest * minFraction);
            var keep = new List<int>(m.TriangleCount);
            for (int t = 0; t < comp.Length; t++)
                if (size[comp[t]] >= min) keep.Add(t);
            removedTriangles = m.TriangleCount - keep.Count;
            return removedTriangles == 0 ? m : m.Subset(keep);
        }

        /// <summary>Taubin (λ|μ) smoothing for DISPLAY only — fitting always uses the original vertices (spec §1).</summary>
        public static TriMesh SmoothForDisplay(TriMesh m, int iterations, JobContext job)
        {
            if (iterations <= 0) return m;
            int nv = m.VertexCount;
            // vertex neighbour lists via edges
            var nbr = new List<int>[nv];
            for (int i = 0; i < nv; i++) nbr[i] = new List<int>(6);
            for (int t = 0; t < m.TriangleCount; t++)
                for (int k = 0; k < 3; k++)
                {
                    int a = m.Indices[3 * t + k], b = m.Indices[3 * t + (k + 1) % 3];
                    if (!nbr[a].Contains(b)) nbr[a].Add(b);
                    if (!nbr[b].Contains(a)) nbr[b].Add(a);
                }
            var p = (float[])m.Positions.Clone();
            var q = new float[p.Length];
            const double lambda = 0.5, mu = -0.53;
            for (int it = 0; it < 2 * iterations; it++)
            {
                double f = (it & 1) == 0 ? lambda : mu;
                for (int i = 0; i < nv; i++)
                {
                    var l = nbr[i];
                    if (l.Count == 0) { q[3 * i] = p[3 * i]; q[3 * i + 1] = p[3 * i + 1]; q[3 * i + 2] = p[3 * i + 2]; continue; }
                    double sx = 0, sy = 0, sz = 0;
                    foreach (int j in l) { sx += p[3 * j]; sy += p[3 * j + 1]; sz += p[3 * j + 2]; }
                    for (int c = 0; c < 3; c++)
                    {
                        double avg = (c == 0 ? sx : c == 1 ? sy : sz) / l.Count;
                        q[3 * i + c] = (float)(p[3 * i + c] + f * (avg - p[3 * i + c]));
                    }
                }
                var tmp = p; p = q; q = tmp;
                job.Report("Smoothing display copy", (it + 1.0) / (2 * iterations));
            }
            return new TriMesh(p, (int[])m.Indices.Clone(), m.Colors);
        }
    }
}
