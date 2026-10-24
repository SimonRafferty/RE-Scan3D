// Trace and fit (spec §5): follow a section between two picked points, then reduce the path to sketch
// points or to the fewest spline through-points that stay within a deviation tolerance.
using System;
using System.Collections.Generic;
using ScanRE.Core.Math3;
using ScanRE.Core.Section;

namespace ScanRE.Core.Trace
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
                throw new Jobs.ScanReException("The two points are on different section curves.",
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
                var spans = CatmullRom.Spans(pts, closed, 16);
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

        private static void Deviation(List<Vec3> path, List<Vec3> outp, bool closed, bool spline, TraceResult r)
        {
            List<Vec3> curve = spline ? CatmullRom.Sample(outp, closed, 16) : new List<Vec3>(outp);
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
    /// Centripetal Catmull-Rom through points: a stand-in for SolidWorks' through-point spline when choosing
    /// how many points are needed. SolidWorks' own spline is a smoother C2 curve, so the true deviation is
    /// re-measured on the created sketch spline by the add-in (INVESTIGATION.md).
    /// </summary>
    public static class CatmullRom
    {
        /// <summary>Samples per span (each list includes both span ends).</summary>
        public static List<List<Vec3>> Spans(List<Vec3> pts, bool closed, int perSpan)
        {
            var res = new List<List<Vec3>>();
            int n = pts.Count;
            if (n < 2) return res;
            int spans = closed ? n : n - 1;
            for (int s = 0; s < spans; s++)
            {
                Vec3 p0 = Get(pts, s - 1, closed), p1 = Get(pts, s, closed), p2 = Get(pts, s + 1, closed), p3 = Get(pts, s + 2, closed);
                var l = new List<Vec3>(perSpan + 1);
                for (int k = 0; k <= perSpan; k++) l.Add(Eval(p0, p1, p2, p3, (double)k / perSpan));
                res.Add(l);
            }
            return res;
        }

        public static List<Vec3> Sample(List<Vec3> pts, bool closed, int perSpan)
        {
            var outp = new List<Vec3>();
            int n = pts.Count;
            if (n < 2) { outp.AddRange(pts); return outp; }
            int spans = closed ? n : n - 1;
            for (int s = 0; s < spans; s++)
            {
                Vec3 p0 = Get(pts, s - 1, closed), p1 = Get(pts, s, closed), p2 = Get(pts, s + 1, closed), p3 = Get(pts, s + 2, closed);
                for (int k = 0; k < perSpan; k++) outp.Add(Eval(p0, p1, p2, p3, (double)k / perSpan));
            }
            outp.Add(closed ? pts[0] : pts[n - 1]);
            return outp;
        }

        private static Vec3 Get(List<Vec3> p, int i, bool closed)
        {
            int n = p.Count;
            if (closed) return p[((i % n) + n) % n];
            if (i < 0) return p[0] * 2 - p[1];               // reflected end tangents
            if (i >= n) return p[n - 1] * 2 - p[n - 2];
            return p[i];
        }

        private static Vec3 Eval(Vec3 p0, Vec3 p1, Vec3 p2, Vec3 p3, double u)
        {
            // centripetal parameterisation (alpha = 0.5) avoids cusps and self-intersections
            double t0 = 0;
            double t1 = t0 + Math.Sqrt(Math.Max((p1 - p0).Length, 1e-12));
            double t2 = t1 + Math.Sqrt(Math.Max((p2 - p1).Length, 1e-12));
            double t3 = t2 + Math.Sqrt(Math.Max((p3 - p2).Length, 1e-12));
            double t = t1 + (t2 - t1) * u;
            var a1 = p0 * ((t1 - t) / (t1 - t0)) + p1 * ((t - t0) / (t1 - t0));
            var a2 = p1 * ((t2 - t) / (t2 - t1)) + p2 * ((t - t1) / (t2 - t1));
            var a3 = p2 * ((t3 - t) / (t3 - t2)) + p3 * ((t - t2) / (t3 - t2));
            var b1 = a1 * ((t2 - t) / (t2 - t0)) + a2 * ((t - t0) / (t2 - t0));
            var b2 = a2 * ((t3 - t) / (t3 - t1)) + a3 * ((t - t1) / (t3 - t1));
            return b1 * ((t2 - t) / (t2 - t1)) + b2 * ((t - t1) / (t2 - t1));
        }
    }
}
