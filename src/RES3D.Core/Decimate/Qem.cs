// Quadric-error-metric decimation (Garland & Heckbert 1997) for the DISPLAY copy only.
// Edge/fillet preservation (spec §1): each edge whose dihedral angle exceeds FeatureAngle adds a heavily
// weighted perpendicular constraint plane, so sharp edges and the tangent lines of fillets collapse last.
// Open boundaries get the same treatment so holes in the scan keep their outline.
using System;
using System.Collections.Generic;
using RES3D.Core.Jobs;
using RES3D.Core.Math3;
using RES3D.Core.Mesh;

namespace RES3D.Core.Decimate
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

        // 24 bytes: the heap holds millions of these, so the collapse target is recomputed when one is used
        private struct Candidate : IComparable<Candidate>
        {
            public double Cost;
            public int A, B;
            public int StampA, StampB;
            public int CompareTo(Candidate o) { return Cost.CompareTo(o.Cost); }
        }

        /// <summary>
        /// Decimate to about opt.TargetTriangles by edge collapse, keeping the mesh manifold. (A vertex-clustering
        /// pre-pass was tried for speed and dropped: it made thousands of non-manifold edges and 0.5-0.8 mm vertex
        /// errors on dense scans, and SolidWorks takes minutes to import such a mesh - INVESTIGATION Q2.)
        /// </summary>
        public static TriMesh Decimate(TriMesh input, QemOptions opt, JobContext job)
        {
            if (opt.TargetTriangles >= input.TriangleCount) return input;
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

            adj = null;
            int alive = nt0;
            double cosFlip = Math.Cos(opt.MaxNormalFlipDeg * Math.PI / 180);
            var stamp = new int[nv];

            // collapse b into a at `target` (all checks passed)
            Action<int, int, Vec3> collapse = (a, b, target) =>
            {
                pos[a] = target;
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
                var la = vt[a];
                int kept = 0;
                for (int i = 0; i < la.Count; i++) if (triAlive[la[i]]) la[kept++] = la[i];
                la.RemoveRange(kept, la.Count - kept);
                stamp[a]++;
            };

            // every edge once: u -> v with u < v, plus boundary edges in whichever direction they run
            Func<int, int, bool> wanted = (u, v) =>
            {
                if (u < v) return true;
                foreach (int t in vt[v])
                {
                    if (!triAlive[t]) continue;
                    for (int k = 0; k < 3; k++) if (tri[3 * t + k] == v && tri[3 * t + (k + 1) % 3] == u) return false;
                }
                return true;
            };

            // Phase 1 (bulk, down to twice the target): passes over all edges in approximate cost order, at
            // most one collapse per vertex per pass. A global heap here spent most of its time on cache misses
            // (24 s for 4M triangles); passes are linear and give the same quality once phase 2 finishes.
            var ea = new List<int>(); var eb = new List<int>();
            var lockPass = new int[nv];
            var rnd = new Random(12345);
            int pass = 0;
            const int Buckets = 32;
            while (alive > 2L * opt.TargetTriangles)
            {
                pass++;
                ea.Clear(); eb.Clear();
                for (int t = 0; t < nt0; t++)
                {
                    if (!triAlive[t]) continue;
                    for (int k = 0; k < 3; k++)
                    {
                        int u = tri[3 * t + k], v = tri[3 * t + (k + 1) % 3];
                        if (!wanted(u, v)) continue;
                        ea.Add(u); eb.Add(v);
                    }
                }
                int ne = ea.Count;
                if (ne == 0) break;
                var ec = new double[ne];   // costs are independent: compute them on all cores
                System.Threading.Tasks.Parallel.For(0, ne, i => { double c; Target(q, pos, ea[i], eb[i], out c); ec[i] = c; });
                // cost quantiles from a sample: consider the cheapest half, in 32 buckets of rising cost
                var sample = new double[Math.Min(ne, 4096)];
                for (int i = 0; i < sample.Length; i++) sample[i] = ec[rnd.Next(ne)];
                Array.Sort(sample);
                var limits = new double[Buckets];
                for (int j = 0; j < Buckets; j++) limits[j] = sample[Math.Min(sample.Length - 1, (int)((j + 1) * 0.5 * sample.Length / Buckets))];
                var bucketOf = new byte[ne];
                var counts = new int[Buckets + 1];
                for (int i = 0; i < ne; i++)
                {
                    int lo = 0, hi = Buckets;   // first limit >= cost; Buckets = beyond the cheapest half
                    while (lo < hi) { int mid = (lo + hi) >> 1; if (limits[mid] >= ec[i]) hi = mid; else lo = mid + 1; }
                    bucketOf[i] = (byte)lo;
                    counts[lo]++;
                }
                var start = new int[Buckets + 1];
                for (int j = 1; j <= Buckets; j++) start[j] = start[j - 1] + counts[j - 1];
                var order = new int[ne - counts[Buckets]];
                var fill = (int[])start.Clone();
                for (int i = 0; i < ne; i++) if (bucketOf[i] < Buckets) order[fill[bucketOf[i]]++] = i;

                int done = 0;
                foreach (int i in order)
                {
                    if (alive <= 2L * opt.TargetTriangles) break;
                    int a = ea[i], b = eb[i];
                    if (lockPass[a] == pass || lockPass[b] == pass || vt[a] == null || vt[b] == null) continue;
                    if (!LinkConditionOk(a, b, vt, tri, triAlive)) continue;
                    double unused;
                    var target = Target(q, pos, a, b, out unused);
                    if (FlipsOrDegenerates(a, b, target, pos, vt, tri, triAlive, cosFlip)) continue;
                    collapse(a, b, target);
                    lockPass[a] = lockPass[b] = pass;
                    done++;
                }
                job.Check();
                job.Report("Decimating", (double)(nt0 - alive) / (nt0 - opt.TargetTriangles));
                if (done == 0) break;
            }
            ea = eb = null;

            // Phase 2 (exact greedy to the target): a heap of edge costs; entries made stale by a collapse are
            // recognised by the vertex stamps and skipped.
            var initial = new List<Candidate>(3 * alive / 2 + 16);
            for (int t = 0; t < nt0; t++)
            {
                if (!triAlive[t]) continue;
                for (int k = 0; k < 3; k++)
                {
                    int a = tri[3 * t + k], b = tri[3 * t + (k + 1) % 3];
                    if (!wanted(a, b)) continue;
                    double cost;
                    Target(q, pos, a, b, out cost);
                    initial.Add(new Candidate { Cost = cost, A = a, B = b, StampA = stamp[a], StampB = stamp[b] });
                }
            }
            var heap = new MinHeap<Candidate>(initial.ToArray());   // heapify: O(n)
            initial = null;
            Action<int, int> push = (a, b) =>
            {
                double cost;
                Target(q, pos, a, b, out cost);
                heap.Push(new Candidate { Cost = cost, A = a, B = b, StampA = stamp[a], StampB = stamp[b] });
            };

            var touched = new List<int>(32);
            while (alive > opt.TargetTriangles && heap.Count > 0)
            {
                var c = heap.Pop();
                if (c.StampA != stamp[c.A] || c.StampB != stamp[c.B]) continue;   // stale
                int a = c.A, b = c.B;
                if (a == b || vt[a] == null || vt[b] == null) continue;
                if (!LinkConditionOk(a, b, vt, tri, triAlive)) continue;
                double unused;
                var target = Target(q, pos, a, b, out unused);   // same as when queued: a and b are unchanged since
                if (FlipsOrDegenerates(a, b, target, pos, vt, tri, triAlive, cosFlip)) continue;
                collapse(a, b, target);
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

        /// <summary>Where collapsing edge a-b puts the merged vertex, and the quadric error there.</summary>
        private static Vec3 Target(Q[] q, Vec3[] pos, int a, int b, out double cost)
        {
            var qa = q[a]; qa.Add(q[b]);
            Vec3 target;
            var mid = (pos[a] + pos[b]) * 0.5;
            if (!qa.Optimum(out target))
            {
                // degenerate quadric: best of the endpoints and midpoint
                double ca = qa.Eval(pos[a]), cb = qa.Eval(pos[b]), cm = qa.Eval(mid);
                target = ca <= cb && ca <= cm ? pos[a] : cb <= cm ? pos[b] : mid;
            }
            else if ((target - mid).Length > 2 * (pos[a] - pos[b]).Length + 1e-9)
                target = mid;   // keep the optimum near the edge; far optima come from nearly-singular quadrics
            cost = Math.Max(0, qa.Eval(target));
            return target;
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
            for (int side = 0; side < 2; side++)
                foreach (int t in vt[side == 0 ? a : b])
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

        /// <summary>Build from an array in O(n) (the array becomes the heap's storage).</summary>
        public MinHeap(T[] data)
        {
            items = data.Length >= 16 ? data : Grow(data);
            Count = data.Length;
            for (int i = Count / 2 - 1; i >= 0; i--) SiftDown(i);
        }

        private static T[] Grow(T[] d) { var a = new T[16]; Array.Copy(d, a, d.Length); return a; }

        private void SiftDown(int i)
        {
            while (true)
            {
                int l = 2 * i + 1, r = l + 1, m = i;
                if (l < Count && items[l].CompareTo(items[m]) < 0) m = l;
                if (r < Count && items[r].CompareTo(items[m]) < 0) m = r;
                if (m == i) return;
                var s = items[m]; items[m] = items[i]; items[i] = s;
                i = m;
            }
        }

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
            SiftDown(0);
            return top;
        }
    }
}
