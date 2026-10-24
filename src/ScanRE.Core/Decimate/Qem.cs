// Quadric-error-metric decimation (Garland & Heckbert 1997) for the DISPLAY copy only.
// Edge/fillet preservation (spec §1): each edge whose dihedral angle exceeds FeatureAngle adds a heavily
// weighted perpendicular constraint plane, so sharp edges and the tangent lines of fillets collapse last.
// Open boundaries get the same treatment so holes in the scan keep their outline.
using System;
using System.Collections.Generic;
using ScanRE.Core.Jobs;
using ScanRE.Core.Math3;
using ScanRE.Core.Mesh;

namespace ScanRE.Core.Decimate
{
    public sealed class QemOptions
    {
        public int TargetTriangles = 150000;
        /// <summary>Dihedral angle (degrees) above which an edge is a feature edge.</summary>
        public double FeatureAngleDeg = 30;
        /// <summary>Weight of feature/boundary constraint planes relative to face planes.</summary>
        public double FeatureWeight = 1000;
        /// <summary>Reject a collapse that flips any surrounding face normal by more than this (degrees).</summary>
        public double MaxNormalFlipDeg = 60;
    }

    public static class Qem
    {
        // symmetric 4x4 quadric stored as 10 doubles
        internal struct Q
        {
            public double A00, A01, A02, A03, A11, A12, A13, A22, A23, A33;

            public static Q Plane(double a, double b, double c, double d, double w)
            {
                return new Q
                {
                    A00 = w * a * a, A01 = w * a * b, A02 = w * a * c, A03 = w * a * d,
                    A11 = w * b * b, A12 = w * b * c, A13 = w * b * d,
                    A22 = w * c * c, A23 = w * c * d, A33 = w * d * d
                };
            }

            public void Add(Q o)
            {
                A00 += o.A00; A01 += o.A01; A02 += o.A02; A03 += o.A03; A11 += o.A11;
                A12 += o.A12; A13 += o.A13; A22 += o.A22; A23 += o.A23; A33 += o.A33;
            }

            public double Eval(Vec3 p)
            {
                double x = p.X, y = p.Y, z = p.Z;
                return A00 * x * x + 2 * A01 * x * y + 2 * A02 * x * z + 2 * A03 * x
                     + A11 * y * y + 2 * A12 * y * z + 2 * A13 * y
                     + A22 * z * z + 2 * A23 * z + A33;
            }

            /// <summary>Minimiser of the quadric, if the 3x3 system is well-conditioned.</summary>
            public bool Optimum(out Vec3 p)
            {
                double det = A00 * (A11 * A22 - A12 * A12) - A01 * (A01 * A22 - A12 * A02) + A02 * (A01 * A12 - A11 * A02);
                double scale = Math.Abs(A00) + Math.Abs(A11) + Math.Abs(A22);
                p = Vec3.Zero;
                if (scale == 0 || Math.Abs(det) < 1e-12 * scale * scale * scale) return false;
                double bx = -A03, by = -A13, bz = -A23;
                // Cramer
                double dx = bx * (A11 * A22 - A12 * A12) - A01 * (by * A22 - A12 * bz) + A02 * (by * A12 - A11 * bz);
                double dy = A00 * (by * A22 - A12 * bz) - bx * (A01 * A22 - A12 * A02) + A02 * (A01 * bz - by * A02);
                double dz = A00 * (A11 * bz - by * A12) - A01 * (A01 * bz - by * A02) + bx * (A01 * A12 - A11 * A02);
                p = new Vec3(dx / det, dy / det, dz / det);
                return true;
            }
        }

        private struct Candidate : IComparable<Candidate>
        {
            public double Cost;
            public int A, B;
            public int StampA, StampB;
            public Vec3 Target;
            public int CompareTo(Candidate o) { return Cost.CompareTo(o.Cost); }
        }

        /// <summary>
        /// Decimate to about opt.TargetTriangles. Inputs much larger than the target are first reduced by
        /// quadric vertex clustering (O(n), Cluster.cs) to about twice the target, then finished by QEM.
        /// </summary>
        public static TriMesh Decimate(TriMesh input, QemOptions opt, JobContext job)
        {
            if (opt.TargetTriangles >= input.TriangleCount) return input;
            if (input.TriangleCount > 4 * opt.TargetTriangles)
                input = Cluster.Simplify(input, 2 * opt.TargetTriangles, job);
            return Collapse(input, opt, job);
        }

        private static TriMesh Collapse(TriMesh input, QemOptions opt, JobContext job)
        {
            int nt0 = input.TriangleCount;
            if (opt.TargetTriangles >= nt0) return input;
            int nv = input.VertexCount;
            var pos = new Vec3[nv];
            for (int i = 0; i < nv; i++) pos[i] = input.Vertex(i);
            var tri = (int[])input.Indices.Clone();
            var triAlive = new bool[nt0];
            for (int t = 0; t < nt0; t++) triAlive[t] = true;

            // vertex -> incident triangles
            var vt = new List<int>[nv];
            for (int i = 0; i < nv; i++) vt[i] = new List<int>(7);
            for (int t = 0; t < nt0; t++)
                for (int k = 0; k < 3; k++) vt[tri[3 * t + k]].Add(t);

            // quadrics
            var q = new Q[nv];
            for (int t = 0; t < nt0; t++)
            {
                var n = FaceNormalRaw(pos, tri, t);
                double area2 = n.Length;
                if (area2 < 1e-30) continue;
                n = n / area2;
                var fq = Q.Plane(n.X, n.Y, n.Z, -Vec3.Dot(n, pos[tri[3 * t]]), 0.5 * area2);
                for (int k = 0; k < 3; k++) q[tri[3 * t + k]].Add(fq);
                job.Step("Decimating: quadrics", t, nt0, 65536);
            }
            var adj = new TriAdjacency(input, job);
            double cosFeat = Math.Cos(opt.FeatureAngleDeg * Math.PI / 180);
            for (int t = 0; t < nt0; t++)
            {
                var nA = FaceNormalRaw(pos, tri, t).Normalized();
                for (int k = 0; k < 3; k++)
                {
                    int o = adj.Neighbour[3 * t + k];
                    bool constrain;
                    if (o < 0) constrain = true;              // open boundary
                    else if (o < t) continue;                 // each interior edge once
                    else constrain = Vec3.Dot(nA, FaceNormalRaw(pos, tri, o).Normalized()) < cosFeat;
                    if (!constrain) continue;
                    int a = tri[3 * t + k], b = tri[3 * t + (k + 1) % 3];
                    var e = pos[b] - pos[a];
                    double len = e.Length;
                    if (len < 1e-20) continue;
                    var cn = Vec3.Cross(e, nA).Normalized();  // plane containing the edge, perpendicular to the face
                    var cq = Q.Plane(cn.X, cn.Y, cn.Z, -Vec3.Dot(cn, pos[a]), opt.FeatureWeight * len * len);
                    q[a].Add(cq); q[b].Add(cq);
                    if (o >= 0)
                    {
                        // both sides: the edge line is the intersection of the two constraint planes
                        var nB = FaceNormalRaw(pos, tri, o).Normalized();
                        var cn2 = Vec3.Cross(e, nB).Normalized();
                        var cq2 = Q.Plane(cn2.X, cn2.Y, cn2.Z, -Vec3.Dot(cn2, pos[a]), opt.FeatureWeight * len * len);
                        q[a].Add(cq2); q[b].Add(cq2);
                    }
                }
            }

            var stamp = new int[nv];
            var heap = new MinHeap<Candidate>(3 * nt0 / 2 + 16);
            Action<int, int> push = (a, b) =>
            {
                var qa = q[a]; qa.Add(q[b]);
                Vec3 target;
                if (!qa.Optimum(out target))
                {
                    // degenerate quadric: best of the endpoints and midpoint
                    var mid = (pos[a] + pos[b]) * 0.5;
                    double ca = qa.Eval(pos[a]), cb = qa.Eval(pos[b]), cm = qa.Eval(mid);
                    target = ca <= cb && ca <= cm ? pos[a] : cb <= cm ? pos[b] : mid;
                }
                else
                {
                    // keep the optimum near the edge; far optima come from nearly-singular quadrics
                    var mid = (pos[a] + pos[b]) * 0.5;
                    if ((target - mid).Length > 2 * (pos[a] - pos[b]).Length + 1e-9) target = mid;
                }
                heap.Push(new Candidate { Cost = Math.Max(0, qa.Eval(target)), A = a, B = b, StampA = stamp[a], StampB = stamp[b], Target = target });
            };
            for (int t = 0; t < nt0; t++)
                for (int k = 0; k < 3; k++)
                {
                    int a = tri[3 * t + k], b = tri[3 * t + (k + 1) % 3];
                    int o = adj.Neighbour[3 * t + k];
                    if (o < 0 || o > t) push(a, b);   // each edge once
                }

            int alive = nt0;
            double cosFlip = Math.Cos(opt.MaxNormalFlipDeg * Math.PI / 180);
            var touched = new List<int>(32);
            while (alive > opt.TargetTriangles && heap.Count > 0)
            {
                var c = heap.Pop();
                if (c.StampA != stamp[c.A] || c.StampB != stamp[c.B]) continue;   // stale
                int a = c.A, b = c.B;
                if (a == b || vt[a] == null || vt[b] == null) continue;
                if (!LinkConditionOk(a, b, vt, tri, triAlive)) continue;
                if (FlipsOrDegenerates(a, b, c.Target, pos, vt, tri, triAlive, cosFlip)) continue;

                // collapse b into a
                pos[a] = c.Target;
                q[a].Add(q[b]);
                foreach (int t in vt[b])
                {
                    if (!triAlive[t]) continue;
                    bool hasA = tri[3 * t] == a || tri[3 * t + 1] == a || tri[3 * t + 2] == a;
                    if (hasA) { triAlive[t] = false; alive--; continue; }
                    for (int k = 0; k < 3; k++) if (tri[3 * t + k] == b) tri[3 * t + k] = a;
                    vt[a].Add(t);
                }
                vt[b] = null;
                vt[a].RemoveAll(t => !triAlive[t]);
                stamp[a]++;
                // only edges at `a` changed: re-queue them; every other queued edge is still valid
                touched.Clear();
                foreach (int t in vt[a])
                    for (int k = 0; k < 3; k++)
                    {
                        int v = tri[3 * t + k];
                        if (v != a && !touched.Contains(v)) touched.Add(v);
                    }
                foreach (int v in touched) push(a, v);
                job.Step("Decimating", nt0 - alive, nt0 - opt.TargetTriangles, 8192);
            }

            // output
            var outIdx = new List<int>(3 * alive);
            for (int t = 0; t < nt0; t++)
                if (triAlive[t]) { outIdx.Add(tri[3 * t]); outIdx.Add(tri[3 * t + 1]); outIdx.Add(tri[3 * t + 2]); }
            var outPos = new float[3 * nv];
            for (int i = 0; i < nv; i++) { outPos[3 * i] = (float)pos[i].X; outPos[3 * i + 1] = (float)pos[i].Y; outPos[3 * i + 2] = (float)pos[i].Z; }
            return new TriMesh(outPos, outIdx.ToArray(), input.Colors).Compacted();
        }

        private static Vec3 FaceNormalRaw(Vec3[] pos, int[] tri, int t)
        {
            var a = pos[tri[3 * t]]; var b = pos[tri[3 * t + 1]]; var c = pos[tri[3 * t + 2]];
            return Vec3.Cross(b - a, c - a);
        }

        /// <summary>Edge collapse keeps the mesh manifold only if a and b share exactly the edge's opposite vertices.</summary>
        [ThreadStatic] private static HashSet<int> naSet, seenSet;

        private static bool LinkConditionOk(int a, int b, List<int>[] vt, int[] tri, bool[] alive)
        {
            var na = naSet ?? (naSet = new HashSet<int>());
            na.Clear();
            int shared = 0;
            foreach (int t in vt[a])
            {
                if (!alive[t]) continue;
                for (int k = 0; k < 3; k++) na.Add(tri[3 * t + k]);
                if (tri[3 * t] == b || tri[3 * t + 1] == b || tri[3 * t + 2] == b) shared++;
            }
            if (shared == 0) return false;   // not actually an edge any more
            var common = 0;
            var seen = seenSet ?? (seenSet = new HashSet<int>());
            seen.Clear();
            foreach (int t in vt[b])
            {
                if (!alive[t]) continue;
                for (int k = 0; k < 3; k++)
                {
                    int v = tri[3 * t + k];
                    if (v != a && v != b && na.Contains(v) && seen.Add(v)) common++;
                }
            }
            return common <= shared;   // shared = 1 (boundary) or 2 (interior)
        }

        private static bool FlipsOrDegenerates(int a, int b, Vec3 target, Vec3[] pos, List<int>[] vt, int[] tri, bool[] alive, double cosFlip)
        {
            foreach (int v in new[] { a, b })
                foreach (int t in vt[v])
                {
                    if (!alive[t]) continue;
                    int i0 = tri[3 * t], i1 = tri[3 * t + 1], i2 = tri[3 * t + 2];
                    bool hasA = i0 == a || i1 == a || i2 == a, hasB = i0 == b || i1 == b || i2 == b;
                    if (hasA && hasB) continue;   // removed by the collapse
                    var p0 = i0 == a || i0 == b ? target : pos[i0];
                    var p1 = i1 == a || i1 == b ? target : pos[i1];
                    var p2 = i2 == a || i2 == b ? target : pos[i2];
                    var nNew = Vec3.Cross(p1 - p0, p2 - p0);
                    var nOld = FaceNormalRaw(pos, tri, t);
                    double ln = nNew.Length, lo = nOld.Length;
                    if (ln < 1e-14 * (lo + 1e-30)) return true;
                    if (Vec3.Dot(nNew, nOld) < cosFlip * ln * lo) return true;
                }
            return false;
        }
    }

    /// <summary>Binary min-heap (no PriorityQueue in .NET Framework).</summary>
    public sealed class MinHeap<T> where T : IComparable<T>
    {
        private T[] items;
        public int Count { get; private set; }
        public MinHeap(int capacity) { items = new T[Math.Max(16, capacity)]; }

        public void Push(T x)
        {
            if (Count == items.Length) Array.Resize(ref items, items.Length * 2);
            int i = Count++;
            items[i] = x;
            while (i > 0)
            {
                int p = (i - 1) / 2;
                if (items[p].CompareTo(items[i]) <= 0) break;
                var s = items[p]; items[p] = items[i]; items[i] = s;
                i = p;
            }
        }

        public T Pop()
        {
            var top = items[0];
            items[0] = items[--Count];
            int i = 0;
            while (true)
            {
                int l = 2 * i + 1, r = l + 1, m = i;
                if (l < Count && items[l].CompareTo(items[m]) < 0) m = l;
                if (r < Count && items[r].CompareTo(items[m]) < 0) m = r;
                if (m == i) break;
                var s = items[m]; items[m] = items[i]; items[i] = s;
                i = m;
            }
            return top;
        }
    }
}
