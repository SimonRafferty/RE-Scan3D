// Trace and fit (spec §5): follow a section between two picked points, then reduce the path to sketch
// points or to the fewest spline through-points that stay within a deviation tolerance.
using System;
using System.Collections.Generic;
using RES3D.Core.Math3;
using RES3D.Core.Section;

namespace RES3D.Core.Trace
{
    public enum TraceOutput { Points, Spline }

    public sealed class TraceResult
    {
        /// <summary>The followed section path (part coordinates, dense).</summary>
        public List<Vec3> Path;
        /// <summary>Points to create: sketch points (Points) or spline through-points (Spline).</summary>
        public List<Vec3> Output;
        public bool ClosedLoop;
        /// <summary>Largest distance of the dense path from the output curve (mm).</summary>
        public double MaxDev;
        public double RmsDev;
    }

    public static class Tracer
    {
        /// <summary>
        /// The path along the section from a to b (same polyline). On a closed loop the side containing `via`
        /// is taken, or the shorter side when via is null. a == b on a closed loop gives the whole loop.
        /// </summary>
        public static List<Vec3> PathBetween(PlaneSection sec, SectionPoint a, SectionPoint b, Vec3? via, out bool wholeLoop)
        {
            wholeLoop = false;
            if (!a.Valid || !b.Valid) throw new ArgumentException("both ends must be on the section");
            if (a.Line != b.Line)
                throw new Jobs.Res3dException("The two points are on different section curves.",
                    "Pick the start and end on the same continuous curve; gaps in the scan split a section into several curves.");
            var pl = sec.Lines[a.Line];
            if (!pl.Closed)
            {
                var fwd = Walk(pl, a, b, true);
                if (a.Arc > b.Arc) { fwd = Walk(pl, b, a, true); fwd.Reverse(); }
                return fwd;
            }
            if (Math.Abs(a.Arc - b.Arc) < 1e-9)
            {
                wholeLoop = true;
                var loop = Walk(pl, a, a, true, true);
                return loop;
            }
            var p1 = Walk(pl, a, b, true);
            var p2 = Walk(pl, a, b, false);
            if (via.HasValue)
            {
                double d1 = DistanceTo(p1, via.Value), d2 = DistanceTo(p2, via.Value);
                return d1 <= d2 ? p1 : p2;
            }
            return Length(p1) <= Length(p2) ? p1 : p2;
        }

        /// <summary>Walk the polyline from a to b, forwards (increasing segment index) or backwards.</summary>
        private static List<Vec3> Walk(Polyline pl, SectionPoint a, SectionPoint b, bool forward, bool fullLoop = false)
        {
            var path = new List<Vec3> { a.Point };
            int n = pl.Points.Count;
            int segs = pl.SegmentCount;
            if (forward)
            {
                if (!fullLoop && a.Segment == b.Segment && b.Param >= a.Param) { path.Add(b.Point); return path; }
                int s = a.Segment;
                for (int guard = 0; guard <= segs; guard++)
                {
                    int vEnd = (s + 1) % n;          // end vertex of segment s
                    path.Add(pl.Points[vEnd]);
                    s = (s + 1) % segs;
                    if (!pl.Closed && s == 0) break;
                    if (s == b.Segment) break;
                }
                path.Add(b.Point);
            }
            else
            {
                if (a.Segment == b.Segment && b.Param <= a.Param) { path.Add(b.Point); return path; }
                int s = a.Segment;
                for (int guard = 0; guard <= segs; guard++)
                {
                    path.Add(pl.Points[s]);           // start vertex of segment s
                    s = (s - 1 + segs) % segs;
                    if (s == b.Segment) break;
                }
                path.Add(b.Point);
            }
            return path;
        }

        private static double Length(List<Vec3> p)
        {
            double l = 0;
            for (int i = 1; i < p.Count; i++) l += (p[i] - p[i - 1]).Length;
            return l;
        }

        private static double DistanceTo(List<Vec3> p, Vec3 q)
        {
            double best = double.MaxValue;
            for (int i = 1; i < p.Count; i++) best = Math.Min(best, PointSegment(q, p[i - 1], p[i]));
            return best;
        }

        public static double PointSegment(Vec3 q, Vec3 a, Vec3 b)
        {
            var ab = b - a;
            double l2 = ab.LengthSq;
            double t = l2 > 0 ? Math.Max(0, Math.Min(1, Vec3.Dot(q - a, ab) / l2)) : 0;
            return (q - (a + ab * t)).Length;
        }

        // ------------------------------------------------------------------ outputs

        /// <summary>Sketch points along the path at roughly `spacing` mm, always including both ends.</summary>
        public static TraceResult AsPoints(List<Vec3> path, double spacing, bool closed)
        {
            var outp = Resample(path, spacing);
            if (closed && outp.Count > 2) outp.RemoveAt(outp.Count - 1);   // the end repeats the start
            var r = new TraceResult { Path = path, Output = outp, ClosedLoop = closed };
            Deviation(path, outp, closed, false, r);
            return r;
        }

        /// <summary>Fewest spline through-points (≥ 2) for which the spline stays within `tol` of the path.</summary>
        public static TraceResult AsSpline(List<Vec3> path, double tol, bool closed)
        {
            // start from a coarse Douglas-Peucker set, then add the worst point of every span still out of
            // tolerance. Each dense point is only compared with its own span (and its neighbours), so a pass
            // is O(n) rather than O(n x curve samples).
            var dense = Resample(path, Math.Max(tol / 4, 1e-4));
            var keep = new SortedSet<int> { 0, dense.Count - 1 };
            foreach (int i in DouglasPeucker(dense, 4 * tol)) keep.Add(i);
            for (int iter = 0; iter < dense.Count; iter++)
            {
                var idx = new List<int>(keep);
                var pts = new List<Vec3>();
                foreach (int i in idx) pts.Add(dense[i]);
                var spans = CadSpline.Spans(pts, closed, 16);
                bool added = false;
                for (int s = 0; s + 1 < idx.Count; s++)
                {
                    int worst = -1; double worstD = tol;
                    for (int i = idx[s] + 1; i < idx[s + 1]; i++)
                    {
                        double d = DistanceTo(spans[s], dense[i]);
                        if (d > worstD) { worstD = d; worst = i; }
                    }
                    if (worst >= 0) { keep.Add(worst); added = true; }
                }
                if (!added) break;
            }
            var outp = new List<Vec3>();
            foreach (int i in keep) outp.Add(dense[i]);
            if (closed && outp.Count > 2 && (outp[0] - outp[outp.Count - 1]).Length < 1e-9) outp.RemoveAt(outp.Count - 1);
            var r = new TraceResult { Path = path, Output = outp, ClosedLoop = closed };
            Deviation(path, outp, closed, true, r);
            return r;
        }

        /// <summary>
        /// Check a real CAD spline (sampled densely as `curve`) against the path it was traced from, and add the
        /// worst path point of every stretch that is more than `tol` away to `through` (kept in path order).
        /// Returns true when points were added, i.e. the spline must be made again. MaxDev/RmsDev are written to `r`.
        /// </summary>
        public static bool RefineAgainst(List<Vec3> path, List<Vec3> through, List<Vec3> curve, double tol, TraceResult r)
        {
            int n = path.Count;
            var dev = new double[n];
            double ss = 0, mx = 0;
            for (int i = 0; i < n; i++)
            {
                dev[i] = curve.Count >= 2 ? DistanceTo(curve, path[i]) : 0;
                ss += dev[i] * dev[i];
                mx = Math.Max(mx, dev[i]);
            }
            r.MaxDev = mx;
            r.RmsDev = n > 0 ? Math.Sqrt(ss / n) : 0;
            if (mx <= tol) return false;

            // where each through point sits along the path
            var at = new List<int>();
            foreach (var t in through) at.Add(NearestIndex(path, t));
            bool added = false;
            for (int i = 0; i < n; )
            {
                if (dev[i] <= tol) { i++; continue; }
                int worst = i;
                for (; i < n && dev[i] > tol; i++) if (dev[i] > dev[worst]) worst = i;
                if (at.Contains(worst)) continue;   // already a through point: nothing more to gain here
                int k = 0;
                while (k < at.Count && at[k] < worst) k++;
                at.Insert(k, worst);
                through.Insert(k, path[worst]);
                added = true;
            }
            return added;
        }

        private static int NearestIndex(List<Vec3> path, Vec3 p)
        {
            int best = 0; double bd = double.MaxValue;
            for (int i = 0; i < path.Count; i++)
            {
                double d = Vec3.Dot(path[i] - p, path[i] - p);
                if (d < bd) { bd = d; best = i; }
            }
            return best;
        }

        private static void Deviation(List<Vec3> path, List<Vec3> outp, bool closed, bool spline, TraceResult r)
        {
            List<Vec3> curve = spline ? CadSpline.Sample(outp, closed, 16) : new List<Vec3>(outp);
            if (!spline && closed && curve.Count > 0) curve.Add(curve[0]);
            double ss = 0, mx = 0;
            foreach (var p in path)
            {
                double d = curve.Count >= 2 ? DistanceTo(curve, p) : 0;
                ss += d * d; mx = Math.Max(mx, d);
            }
            r.MaxDev = mx;
            r.RmsDev = path.Count > 0 ? Math.Sqrt(ss / path.Count) : 0;
        }

        /// <summary>Points at even arc-length spacing (ends exact).</summary>
        public static List<Vec3> Resample(List<Vec3> path, double spacing)
        {
            var res = new List<Vec3>();
            if (path.Count == 0) return res;
            double total = Length(path);
            int n = Math.Max(1, (int)Math.Round(total / Math.Max(spacing, 1e-9)));
            double step = total / n;
            res.Add(path[0]);
            double acc = 0, nextAt = step;
            for (int i = 1; i < path.Count && res.Count < n; i++)
            {
                var a = path[i - 1]; var b = path[i];
                double seg = (b - a).Length;
                while (seg > 0 && acc + seg >= nextAt - 1e-12 && res.Count < n)
                {
                    double t = (nextAt - acc) / seg;
                    res.Add(a + (b - a) * t);
                    nextAt += step;
                }
                acc += seg;
            }
            res.Add(path[path.Count - 1]);
            return res;
        }

        public static List<int> DouglasPeucker(List<Vec3> p, double tol)
        {
            var keep = new List<int>();
            var stack = new Stack<int[]>();
            stack.Push(new[] { 0, p.Count - 1 });
            while (stack.Count > 0)
            {
                var r = stack.Pop();
                int i0 = r[0], i1 = r[1];
                if (i1 - i0 < 2) continue;
                int worst = -1; double wd = tol;
                for (int i = i0 + 1; i < i1; i++)
                {
                    double d = PointSegment(p[i], p[i0], p[i1]);
                    if (d > wd) { wd = d; worst = i; }
                }
                if (worst < 0) continue;
                keep.Add(worst);
                stack.Push(new[] { i0, worst });
                stack.Push(new[] { worst, i1 });
            }
            return keep;
        }
    }

    /// <summary>
    /// The curve SolidWorks builds through sketch spline points (measured in SolidWorks 2025 to 0.0000 mm):
    /// a C2 cubic with chord-length parameters, "not-a-knot" ends when open and periodic when closed.
    /// Used to choose the fewest through points, so the created spline needs no correction in the normal case.
    /// </summary>
    public static class CadSpline
    {
        /// <summary>Samples per span (each list includes both span ends). On a closed curve a repeated end point is ignored.</summary>
        public static List<List<Vec3>> Spans(List<Vec3> pts, bool closed, int perSpan)
        {
            var res = new List<List<Vec3>>();
            var p = Distinct(pts, closed);
            int n = p.Count;
            if (n < 2) return res;
            if (closed && n < 3) closed = false;
            double[] h;
            var m = SecondDerivatives(p, closed, out h);
            int spans = closed ? n : n - 1;
            for (int s = 0; s < spans; s++)
            {
                int e = (s + 1) % n;
                var l = new List<Vec3>(perSpan + 1);
                for (int k = 0; k <= perSpan; k++) l.Add(Eval(p[s], p[e], m[s], m[e], h[s], (double)k / perSpan * h[s]));
                res.Add(l);
            }
            return res;
        }

        public static List<Vec3> Sample(List<Vec3> pts, bool closed, int perSpan)
        {
            var outp = new List<Vec3>();
            var spans = Spans(pts, closed, perSpan);
            if (spans.Count == 0) { outp.AddRange(pts); return outp; }
            foreach (var s in spans) for (int k = 0; k < s.Count - 1; k++) outp.Add(s[k]);
            var last = spans[spans.Count - 1];
            outp.Add(last[last.Count - 1]);
            return outp;
        }

        private static List<Vec3> Distinct(List<Vec3> pts, bool closed)
        {
            var p = new List<Vec3>(pts.Count);
            foreach (var q in pts) if (p.Count == 0 || (q - p[p.Count - 1]).Length > 1e-9) p.Add(q);
            if (closed && p.Count > 1 && (p[0] - p[p.Count - 1]).Length <= 1e-9) p.RemoveAt(p.Count - 1);
            return p;
        }

        private static Vec3 Eval(Vec3 y0, Vec3 y1, Vec3 m0, Vec3 m1, double h, double t)
        {
            double a = (h - t) / h, b = t / h;
            return y0 * a + y1 * b + (m0 * (a * a * a - a) + m1 * (b * b * b - b)) * (h * h / 6);
        }

        /// <summary>Second derivatives at the points; h[i] = chord from point i to the next.</summary>
        private static Vec3[] SecondDerivatives(List<Vec3> p, bool closed, out double[] h)
        {
            int n = p.Count;
            int spans = closed ? n : n - 1;
            var hh = new double[spans];
            for (int i = 0; i < spans; i++) hh[i] = Math.Max((p[(i + 1) % n] - p[i]).Length, 1e-12);
            h = hh;
            var m = new Vec3[n];
            if (!closed && n == 2) return m;   // straight line
            // right-hand sides: 6 x (difference of the chord slopes) at every point that has two spans
            Func<int, Vec3> rhs = i =>
            {
                int a = (i - 1 + n) % n, b = (i + 1) % n;
                double ha = hh[(i - 1 + spans) % spans], hb = hh[i % spans];
                return ((p[b] - p[i]) * (1 / hb) - (p[i] - p[a]) * (1 / ha)) * 6;
            };
            if (closed)
            {
                // cyclic tridiagonal: h[i-1] M[i-1] + 2 (h[i-1] + h[i]) M[i] + h[i] M[i+1] = rhs, Sherman-Morrison
                var lo = new double[n]; var di = new double[n]; var up = new double[n]; var r = new Vec3[n];
                for (int i = 0; i < n; i++)
                {
                    double hp = h[(i - 1 + n) % n], hn = h[i];
                    lo[i] = hp; di[i] = 2 * (hp + hn); up[i] = hn; r[i] = rhs(i);
                }
                double corner = h[n - 1];               // A[0, n-1] = A[n-1, 0]
                double gamma = -di[0];
                var bb = (double[])di.Clone();
                bb[0] -= gamma;
                bb[n - 1] -= corner * corner / gamma;
                var x = Thomas(lo, bb, up, r);
                var u = new Vec3[n];
                u[0] = new Vec3(gamma, gamma, gamma);
                u[n - 1] = new Vec3(corner, corner, corner);
                var z = Thomas(lo, bb, up, u);
                for (int d = 0; d < 3; d++)
                {
                    double fact = (x[0][d] + corner * x[n - 1][d] / gamma) / (1 + z[0][d] + corner * z[n - 1][d] / gamma);
                    for (int i = 0; i < n; i++) m[i] = With(m[i], d, x[i][d] - fact * z[i][d]);
                }
                return m;
            }
            if (n == 3)
            {
                // not-a-knot through three points is one parabola: constant second derivative
                var c = rhs(1) * (1 / (3 * (h[0] + h[1])));
                m[0] = m[1] = m[2] = c;
                return m;
            }
            // not-a-knot: third derivative continuous at points 1 and n-2. Eliminate M[0] and M[n-1] so the rest
            // is tridiagonal in M[1..n-2].
            int k = n - 2;
            var L = new double[k]; var D = new double[k]; var U = new double[k]; var R = new Vec3[k];
            for (int j = 0; j < k; j++)
            {
                int i = j + 1;
                L[j] = h[i - 1]; D[j] = 2 * (h[i - 1] + h[i]); U[j] = h[i]; R[j] = rhs(i);
            }
            // M0 = ((h0 + h1) M1 - h0 M2) / h1
            D[0] += h[0] * (h[0] + h[1]) / h[1];
            U[0] -= h[0] * h[0] / h[1];
            // M[n-1] = ((h[n-3] + h[n-2]) M[n-2] - h[n-2] M[n-3]) / h[n-3]
            double ha2 = h[n - 3], hb2 = h[n - 2];
            D[k - 1] += hb2 * (ha2 + hb2) / ha2;
            L[k - 1] -= hb2 * hb2 / ha2;
            var inner = Thomas(L, D, U, R);
            for (int j = 0; j < k; j++) m[j + 1] = inner[j];
            m[0] = (m[1] * (h[0] + h[1]) - m[2] * h[0]) * (1 / h[1]);
            m[n - 1] = (m[n - 2] * (ha2 + hb2) - m[n - 3] * hb2) * (1 / ha2);
            return m;
        }

        private static Vec3 With(Vec3 v, int d, double value)
        {
            return d == 0 ? new Vec3(value, v.Y, v.Z) : d == 1 ? new Vec3(v.X, value, v.Z) : new Vec3(v.X, v.Y, value);
        }

        /// <summary>Tridiagonal solve (lo[0] and up[n-1] unused), three right-hand sides at once.</summary>
        private static Vec3[] Thomas(double[] lo, double[] di, double[] up, Vec3[] r)
        {
            int n = di.Length;
            var c = new double[n]; var d = new Vec3[n];
            c[0] = up[0] / di[0];
            d[0] = r[0] * (1 / di[0]);
            for (int i = 1; i < n; i++)
            {
                double den = di[i] - lo[i] * c[i - 1];
                c[i] = i < n - 1 ? up[i] / den : 0;
                d[i] = (r[i] - d[i - 1] * lo[i]) * (1 / den);
            }
            var x = new Vec3[n];
            x[n - 1] = d[n - 1];
            for (int i = n - 2; i >= 0; i--) x[i] = d[i] - x[i + 1] * c[i];
            return x;
        }
    }
}
