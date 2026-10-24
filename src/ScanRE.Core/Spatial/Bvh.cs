// Bounding-volume hierarchy over the full-resolution triangles: ray casting for picking (spec §4: always the
// full-res mesh, never the decimated display copy) and plane culling for fast sections.
// Built top-down with a binned surface-area heuristic; stored as flat arrays so it caches to disk as-is.
using System;
using System.Collections.Generic;
using System.IO;
using ScanRE.Core.Jobs;
using ScanRE.Core.Math3;
using ScanRE.Core.Mesh;

namespace ScanRE.Core.Spatial
{
    public struct RayHit
    {
        public int Triangle;
        public double T;
        public Vec3 Point;
        public bool Hit { get { return Triangle >= 0; } }
        public static readonly RayHit None = new RayHit { Triangle = -1, T = double.PositiveInfinity };
    }

    public sealed class Bvh
    {
        private const int LeafSize = 4;
        private const int Bins = 12;

        // node arrays: bounds (6 floats), and either (left child, -1) for inner nodes (right = left+1)
        // or (first, count) into TriOrder for leaves (count > 0)
        public float[] NodeBounds;
        public int[] NodeFirst;
        public int[] NodeCount;   // 0 = inner node: children at NodeFirst, NodeFirst+1
        public int[] TriOrder;
        public int NodeTotal;

        private TriMesh mesh;

        private Bvh() { }

        public TriMesh Mesh { get { return mesh; } }

        public static Bvh Build(TriMesh m, JobContext job)
        {
            int nt = m.TriangleCount;
            var b = new Bvh { mesh = m };
            var cen = new float[3 * nt];
            var tbox = new float[6 * nt];
            for (int t = 0; t < nt; t++)
            {
                Vec3 p0, p1, p2;
                m.Triangle(t, out p0, out p1, out p2);
                var lo = Vec3.Min(p0, Vec3.Min(p1, p2));
                var hi = Vec3.Max(p0, Vec3.Max(p1, p2));
                for (int k = 0; k < 3; k++)
                {
                    tbox[6 * t + k] = (float)lo[k];
                    tbox[6 * t + 3 + k] = (float)hi[k];
                    cen[3 * t + k] = (float)(0.5 * (lo[k] + hi[k]));
                }
                job.Step("Building spatial index", t, 4L * nt, 65536);
            }
            int cap = Math.Max(1, 2 * nt / LeafSize + 2) * 2;
            b.NodeBounds = new float[6 * cap];
            b.NodeFirst = new int[cap];
            b.NodeCount = new int[cap];
            b.TriOrder = new int[nt];
            for (int i = 0; i < nt; i++) b.TriOrder[i] = i;
            b.NodeTotal = 1;
            long done = 0;
            // iterative build with an explicit stack (deep recursion on 10M tris would be risky)
            var stack = new Stack<int[]>();   // node, first, count
            stack.Push(new[] { 0, 0, nt });
            var binCount = new int[Bins];
            var binLo = new double[Bins * 3];
            var binHi = new double[Bins * 3];
            while (stack.Count > 0)
            {
                var job3 = stack.Pop();
                int node = job3[0], first = job3[1], count = job3[2];
                // node bounds + centroid bounds
                double[] lo = { double.MaxValue, double.MaxValue, double.MaxValue }, hi = { double.MinValue, double.MinValue, double.MinValue };
                double[] clo = { double.MaxValue, double.MaxValue, double.MaxValue }, chi = { double.MinValue, double.MinValue, double.MinValue };
                for (int i = first; i < first + count; i++)
                {
                    int t = b.TriOrder[i];
                    for (int k = 0; k < 3; k++)
                    {
                        lo[k] = Math.Min(lo[k], tbox[6 * t + k]); hi[k] = Math.Max(hi[k], tbox[6 * t + 3 + k]);
                        clo[k] = Math.Min(clo[k], cen[3 * t + k]); chi[k] = Math.Max(chi[k], cen[3 * t + k]);
                    }
                }
                for (int k = 0; k < 3; k++) { b.NodeBounds[6 * node + k] = (float)lo[k]; b.NodeBounds[6 * node + 3 + k] = (float)hi[k]; }

                int axis = -1, splitBin = -1;
                if (count > LeafSize)
                {
                    double best = double.MaxValue;
                    // SAH on the longest centroid axis only: ~3x faster builds, ray casts barely slower (bench)
                    int longest = chi[0] - clo[0] >= chi[1] - clo[1] ? (chi[0] - clo[0] >= chi[2] - clo[2] ? 0 : 2) : (chi[1] - clo[1] >= chi[2] - clo[2] ? 1 : 2);
                    for (int ax = longest; ax == longest; ax++)
                    {
                        double ext = chi[ax] - clo[ax];
                        if (ext <= 1e-12) continue;
                        Array.Clear(binCount, 0, Bins);
                        for (int i = 0; i < Bins * 3; i++) { binLo[i] = double.MaxValue; binHi[i] = double.MinValue; }
                        for (int i = first; i < first + count; i++)
                        {
                            int t = b.TriOrder[i];
                            int bi = Math.Min(Bins - 1, (int)(Bins * (cen[3 * t + ax] - clo[ax]) / ext));
                            binCount[bi]++;
                            for (int k = 0; k < 3; k++)
                            {
                                binLo[3 * bi + k] = Math.Min(binLo[3 * bi + k], tbox[6 * t + k]);
                                binHi[3 * bi + k] = Math.Max(binHi[3 * bi + k], tbox[6 * t + 3 + k]);
                            }
                        }
                        for (int s = 0; s < Bins - 1; s++)
                        {
                            double cost = SideCost(binCount, binLo, binHi, 0, s) + SideCost(binCount, binLo, binHi, s + 1, Bins - 1);
                            if (cost < best) { best = cost; axis = ax; splitBin = s; }
                        }
                    }
                }
                int mid = -1;
                if (axis >= 0)
                {
                    double ext = chi[axis] - clo[axis];
                    int i = first, j = first + count - 1;
                    while (i <= j)
                    {
                        int t = b.TriOrder[i];
                        int bi = Math.Min(Bins - 1, (int)(Bins * (cen[3 * t + axis] - clo[axis]) / ext));
                        if (bi <= splitBin) i++;
                        else { b.TriOrder[i] = b.TriOrder[j]; b.TriOrder[j] = t; j--; }
                    }
                    mid = i;
                    if (mid == first || mid == first + count) mid = -1;
                }
                if (mid < 0 && count > 4 * LeafSize)
                    mid = first + count / 2;   // all centroids coincide: split arbitrarily rather than make a huge leaf
                if (mid < 0)
                {
                    b.NodeFirst[node] = first;
                    b.NodeCount[node] = count;
                    done += count;
                    job.Step("Building spatial index", nt + 3 * done, 4L * nt, 1);
                    continue;
                }
                if (b.NodeTotal + 2 > b.NodeFirst.Length) b.Grow();
                int left = b.NodeTotal;
                b.NodeTotal += 2;
                b.NodeFirst[node] = left;
                b.NodeCount[node] = 0;
                stack.Push(new[] { left + 1, mid, first + count - mid });
                stack.Push(new[] { left, first, mid - first });
            }
            return b;
        }

        private static double SideCost(int[] cnt, double[] lo, double[] hi, int a, int z)
        {
            int n = 0;
            double x0 = double.MaxValue, y0 = double.MaxValue, z0 = double.MaxValue, x1 = double.MinValue, y1 = double.MinValue, z1 = double.MinValue;
            for (int i = a; i <= z; i++)
            {
                if (cnt[i] == 0) continue;
                n += cnt[i];
                x0 = Math.Min(x0, lo[3 * i]); y0 = Math.Min(y0, lo[3 * i + 1]); z0 = Math.Min(z0, lo[3 * i + 2]);
                x1 = Math.Max(x1, hi[3 * i]); y1 = Math.Max(y1, hi[3 * i + 1]); z1 = Math.Max(z1, hi[3 * i + 2]);
            }
            if (n == 0) return 0;
            double dx = x1 - x0, dy = y1 - y0, dz = z1 - z0;
            return n * (dx * dy + dy * dz + dz * dx);
        }

        private void Grow()
        {
            int cap = NodeFirst.Length * 2;
            Array.Resize(ref NodeBounds, 6 * cap);
            Array.Resize(ref NodeFirst, cap);
            Array.Resize(ref NodeCount, cap);
        }

        // ------------------------------------------------------------------ queries

        /// <summary>Nearest hit along the ray (both triangle sides count: scans are often not consistently oriented).</summary>
        public RayHit Raycast(Ray3 ray, double tMax = double.PositiveInfinity)
        {
            var best = RayHit.None;
            best.T = tMax;
            double ix = 1.0 / ray.Dir.X, iy = 1.0 / ray.Dir.Y, iz = 1.0 / ray.Dir.Z;
            var stack = new int[128];
            int sp = 0;
            stack[sp++] = 0;
            while (sp > 0)
            {
                int n = stack[--sp];
                if (!SlabHit(n, ray.Origin, ix, iy, iz, best.T)) continue;
                int cnt = NodeCount[n];
                if (cnt > 0)
                {
                    int f = NodeFirst[n];
                    for (int i = f; i < f + cnt; i++)
                    {
                        int t = TriOrder[i];
                        double d;
                        if (IntersectTri(t, ray, out d) && d < best.T && d >= 0)
                        {
                            best.T = d; best.Triangle = t;
                        }
                    }
                }
                else
                {
                    if (sp + 2 > stack.Length) Array.Resize(ref stack, stack.Length * 2);
                    stack[sp++] = NodeFirst[n];
                    stack[sp++] = NodeFirst[n] + 1;
                }
            }
            if (best.Hit) best.Point = ray.At(best.T);
            return best;
        }

        private bool SlabHit(int n, Vec3 o, double ix, double iy, double iz, double tMax)
        {
            int b = 6 * n;
            double t0 = 0, t1 = tMax;
            double a = (NodeBounds[b] - o.X) * ix, c = (NodeBounds[b + 3] - o.X) * ix;
            if (a > c) { var s = a; a = c; c = s; }
            t0 = Math.Max(t0, a); t1 = Math.Min(t1, c);
            a = (NodeBounds[b + 1] - o.Y) * iy; c = (NodeBounds[b + 4] - o.Y) * iy;
            if (a > c) { var s = a; a = c; c = s; }
            t0 = Math.Max(t0, a); t1 = Math.Min(t1, c);
            a = (NodeBounds[b + 2] - o.Z) * iz; c = (NodeBounds[b + 5] - o.Z) * iz;
            if (a > c) { var s = a; a = c; c = s; }
            t0 = Math.Max(t0, a); t1 = Math.Min(t1, c);
            // NaN from 0*inf (ray in a slab face) compares false: Math.Max/Min propagate NaN, treat as hit
            return !(t0 > t1 * (1 + 1e-9) + 1e-9);
        }

        /// <summary>Möller–Trumbore, double precision.</summary>
        private bool IntersectTri(int t, Ray3 ray, out double dist)
        {
            Vec3 a, b, c;
            mesh.Triangle(t, out a, out b, out c);
            dist = 0;
            var e1 = b - a; var e2 = c - a;
            var p = Vec3.Cross(ray.Dir, e2);
            double det = Vec3.Dot(e1, p);
            if (Math.Abs(det) < 1e-18) return false;
            double inv = 1.0 / det;
            var s = ray.Origin - a;
            double u = Vec3.Dot(s, p) * inv;
            if (u < -1e-9 || u > 1 + 1e-9) return false;
            var q = Vec3.Cross(s, e1);
            double v = Vec3.Dot(ray.Dir, q) * inv;
            if (v < -1e-9 || u + v > 1 + 1e-9) return false;
            dist = Vec3.Dot(e2, q) * inv;
            return true;
        }

        /// <summary>Triangles whose bounding boxes lie within `halfThickness` of the plane (candidates for slicing).</summary>
        public List<int> TrianglesNearPlane(Plane3 pl, double halfThickness)
        {
            var result = new List<int>();
            var stack = new Stack<int>();
            stack.Push(0);
            while (stack.Count > 0)
            {
                int n = stack.Pop();
                if (!BoxNearPlane(n, pl, halfThickness)) continue;
                int cnt = NodeCount[n];
                if (cnt > 0)
                    for (int i = NodeFirst[n]; i < NodeFirst[n] + cnt; i++) result.Add(TriOrder[i]);
                else { stack.Push(NodeFirst[n]); stack.Push(NodeFirst[n] + 1); }
            }
            return result;
        }

        private bool BoxNearPlane(int n, Plane3 pl, double h)
        {
            int b = 6 * n;
            var c = new Vec3(0.5 * (NodeBounds[b] + NodeBounds[b + 3]), 0.5 * (NodeBounds[b + 1] + NodeBounds[b + 4]), 0.5 * (NodeBounds[b + 2] + NodeBounds[b + 5]));
            var e = new Vec3(0.5 * (NodeBounds[b + 3] - NodeBounds[b]), 0.5 * (NodeBounds[b + 4] - NodeBounds[b + 1]), 0.5 * (NodeBounds[b + 5] - NodeBounds[b + 2]));
            double r = e.X * Math.Abs(pl.Normal.X) + e.Y * Math.Abs(pl.Normal.Y) + e.Z * Math.Abs(pl.Normal.Z);
            return Math.Abs(pl.SignedDistance(c)) <= r + h;
        }

        /// <summary>Triangles whose boxes intersect the axis-aligned box (local display refinement, region queries).</summary>
        public List<int> TrianglesInBox(Vec3 lo, Vec3 hi)
        {
            var result = new List<int>();
            var stack = new Stack<int>();
            stack.Push(0);
            while (stack.Count > 0)
            {
                int n = stack.Pop();
                int b = 6 * n;
                if (NodeBounds[b] > hi.X || NodeBounds[b + 3] < lo.X || NodeBounds[b + 1] > hi.Y || NodeBounds[b + 4] < lo.Y ||
                    NodeBounds[b + 2] > hi.Z || NodeBounds[b + 5] < lo.Z) continue;
                int cnt = NodeCount[n];
                if (cnt > 0)
                    for (int i = NodeFirst[n]; i < NodeFirst[n] + cnt; i++) result.Add(TriOrder[i]);
                else { stack.Push(NodeFirst[n]); stack.Push(NodeFirst[n] + 1); }
            }
            return result;
        }

        // ------------------------------------------------------------------ cache

        public void Write(BinaryWriter w)
        {
            w.Write(NodeTotal);
            for (int i = 0; i < 6 * NodeTotal; i++) w.Write(NodeBounds[i]);
            for (int i = 0; i < NodeTotal; i++) { w.Write(NodeFirst[i]); w.Write(NodeCount[i]); }
            w.Write(TriOrder.Length);
            foreach (int t in TriOrder) w.Write(t);
        }

        public static Bvh Read(BinaryReader r, TriMesh m)
        {
            var b = new Bvh { mesh = m };
            b.NodeTotal = r.ReadInt32();
            b.NodeBounds = new float[6 * b.NodeTotal];
            b.NodeFirst = new int[b.NodeTotal];
            b.NodeCount = new int[b.NodeTotal];
            for (int i = 0; i < 6 * b.NodeTotal; i++) b.NodeBounds[i] = r.ReadSingle();
            for (int i = 0; i < b.NodeTotal; i++) { b.NodeFirst[i] = r.ReadInt32(); b.NodeCount[i] = r.ReadInt32(); }
            int n = r.ReadInt32();
            if (n != m.TriangleCount) throw new InvalidDataException("cached index does not match the mesh");
            b.TriOrder = new int[n];
            for (int i = 0; i < n; i++) b.TriOrder[i] = r.ReadInt32();
            return b;
        }
    }
}
