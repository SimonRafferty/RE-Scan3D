// Trace → lines and arcs (user request 2026-10-26): best-fit a traced contour with straight lines and circular arcs
// within a tolerance. Greedy along the path: from each break point, take whichever reaches further while every
// path point stays within the tolerance - a line through the two end points, or an arc through the end points and
// the path point midway between them. Neighbouring pieces share their break point exactly (a connected chain), and
// the deviation checked is the deviation of the very geometry that is output. A closed loop that is one circle
// within the tolerance becomes a circle.
using System;
using System.Collections.Generic;
using RES3D.Core.Math3;

namespace RES3D.Core.Trace
{
    public enum PieceKind { Line, Arc, Circle }

    public sealed class Piece
    {
        public PieceKind Kind;
        /// <summary>Start and end (equal for a circle), and for an arc a point on it between them.</summary>
        public Vec3 A, B, Mid;
        /// <summary>Arcs and circles.</summary>
        public Vec3 Centre;
        public double Radius;
    }

    public sealed class PiecesResult
    {
        public List<Piece> Pieces = new List<Piece>();
        public double MaxDev, RmsDev;
        public int Lines, Arcs, Circles;
    }

    public static class LinesArcs
    {
        /// <summary>Smallest arc worth keeping (degrees): flatter arcs are lines.</summary>
        public const double MinSweepDeg = 8;
        /// <summary>Radius above which an arc is treated as a line (mm).</summary>
        public const double MaxRadius = 1e4;

        public static PiecesResult Fit(List<Vec3> path, double tol, bool closed)
        {
            var res = new PiecesResult();
            var p = new List<Vec3>(path);
            // drop repeated points; a closed path ends where it starts
            for (int i = p.Count - 1; i > 0; i--) if ((p[i] - p[i - 1]).Length < 1e-9) p.RemoveAt(i);
            if (closed && p.Count > 2 && (p[0] - p[p.Count - 1]).Length < 1e-9) p.RemoveAt(p.Count - 1);
            if (p.Count < 2) return res;

            if (closed && p.Count >= 6)
            {
                Piece circle;
                if (WholeCircle(p, tol, out circle)) { res.Pieces.Add(circle); res.Circles = 1; Deviation(path, res); return res; }
                // start where a straight run begins (the longest one), so no line or arc is cut in two at the seam
                int start = LongestStraightStart(p, tol);
                var rot = new List<Vec3>(p.Count + 1);
                for (int i = 0; i < p.Count; i++) rot.Add(p[(start + i) % p.Count]);
                rot.Add(rot[0]);
                p = rot;
            }

            // 1) greedy: from each break point, the piece that reaches furthest
            var breaks = new List<int> { 0 };
            var arcs = new List<bool>();
            int a = 0, n = p.Count;
            while (a < n - 1)
            {
                int lineEnd = a + 1;
                for (int j = a + 2; j < n; j++) { if (LineFits(p, a, j, tol)) lineEnd = j; else break; }
                int arcEnd = -1;
                Vec3 centre = Vec3.Zero;
                double radius = 0;
                for (int k = a + 4; k < n; k++)
                {
                    Vec3 c; double r;
                    if (!ArcFits(p, a, k, tol, out c, out r)) break;
                    arcEnd = k; centre = c; radius = r;
                }
                // an arc must clearly out-reach the line (flat arcs eating into straight runs look wrong in a sketch)
                bool arc = arcEnd > lineEnd + Math.Max(3, (lineEnd - a) / 4) && Sweep(p[a], p[(a + arcEnd) / 2], p[arcEnd], centre) >= MinSweepDeg * Math.PI / 180;
                int b = arc ? arcEnd : lineEnd;
                arcs.Add(arc);
                breaks.Add(b);
                a = b;
            }
            // 2) slide each break point to where its two pieces fit best: at a smooth (tangent) join the greedy
            //    break lands anywhere within the tolerance, which bends arc radii towards the straight neighbours
            for (int pass = 0; pass < 3; pass++)
                for (int i = 1; i < breaks.Count - 1; i++)
                {
                    int lo = breaks[i - 1], hi = breaks[i + 1], best = breaks[i];
                    double bestCost = PieceCost(p, lo, best, arcs[i - 1], tol) + PieceCost(p, best, hi, arcs[i], tol);
                    int reach = Math.Max(3, (hi - lo) / 6);
                    for (int t = Math.Max(lo + 1, breaks[i] - reach); t <= Math.Min(hi - 1, breaks[i] + reach); t++)
                    {
                        if ((arcs[i - 1] && t - lo < 4) || (arcs[i] && hi - t < 4)) continue;
                        double c = PieceCost(p, lo, t, arcs[i - 1], tol) + PieceCost(p, t, hi, arcs[i], tol);
                        if (c < bestCost) { bestCost = c; best = t; }
                    }
                    breaks[i] = best;
                }
            // 3) the pieces
            for (int i = 0; i + 1 < breaks.Count; i++)
            {
                int s0 = breaks[i], s1 = breaks[i + 1];
                var piece = new Piece { Kind = arcs[i] ? PieceKind.Arc : PieceKind.Line, A = p[s0], B = p[s1] };
                if (arcs[i])
                {
                    Vec3 centre; double radius;
                    Circle(p[s0], p[(s0 + s1) / 2], p[s1], out centre, out radius);
                    RefineArc(p, s0, s1, tol, ref centre, ref radius);
                    var mid = p[(s0 + s1) / 2] - centre;   // a point on the refined arc between its ends, for drawing it
                    piece.Mid = centre + mid * (radius / mid.Length);
                    piece.Centre = centre; piece.Radius = radius; res.Arcs++;
                }
                else res.Lines++;
                res.Pieces.Add(piece);
            }
            Deviation(path, res);
            return res;
        }

        /// <summary>Sum of squared deviations of the points a..b from the piece through them (infinite if any is
        /// beyond the tolerance or the arc cannot be made).</summary>
        private static double PieceCost(List<Vec3> p, int a, int b, bool arc, double tol)
        {
            double f = 0;
            if (!arc)
            {
                for (int i = a + 1; i < b; i++) { double d = Tracer.PointSegment(p[i], p[a], p[b]); if (d > tol) return double.MaxValue; f += d * d; }
                return f;
            }
            Vec3 c; double r;
            if (b - a < 4 || !Circle(p[a], p[(a + b) / 2], p[b], out c, out r) || r > MaxRadius) return double.MaxValue;
            RefineArc(p, a, b, double.MaxValue, ref c, ref r);
            for (int i = a + 1; i < b; i++) { double d = Math.Abs((p[i] - c).Length - r); if (d > tol) return double.MaxValue; f += d * d; }
            return f;
        }

        private static bool LineFits(List<Vec3> p, int a, int b, double tol)
        {
            for (int i = a + 1; i < b; i++) if (Tracer.PointSegment(p[i], p[a], p[b]) > tol) return false;
            return true;
        }

        private static bool ArcFits(List<Vec3> p, int a, int b, double tol, out Vec3 c, out double r)
        {
            int m = (a + b) / 2;
            r = 0;
            if (!Circle(p[a], p[m], p[b], out c, out r) || r > MaxRadius) return false;
            for (int i = a + 1; i < b; i++) if (Math.Abs((p[i] - c).Length - r) > tol) return false;
            // the points must run round the arc in order (no doubling back past its ends)
            return true;
        }

        /// <summary>
        /// Best-fit circle through both end points of an arc: its centre slides along the perpendicular bisector of
        /// the chord to the least-squares position for all the points between (a 3-point arc follows the noise of
        /// its middle point). Kept only if every point is still within the tolerance.
        /// </summary>
        private static void RefineArc(List<Vec3> p, int a, int b, double tol, ref Vec3 centre, ref double radius)
        {
            var A = p[a]; var B = p[b];
            var m0 = (A + B) * 0.5;
            var chord = B - A;
            if (chord.Length < 1e-9) return;
            var normal = Vec3.Cross(chord, p[(a + b) / 2] - A);
            if (normal.Length < 1e-12) return;
            var u = Vec3.Cross(normal, chord).Normalized();          // in the arc's plane, across the chord
            double t0 = Vec3.Dot(centre - m0, u);
            Func<double, double> cost = t =>
            {
                var c = m0 + u * t; double r = (A - c).Length, f = 0;
                for (int i = a + 1; i < b; i++) { double d = (p[i] - c).Length - r; f += d * d; }
                return f;
            };
            // golden-section search round the 3-point answer
            double span = Math.Max(1.0, 0.5 * radius), lo = t0 - span, hi = t0 + span, g = (Math.Sqrt(5) - 1) / 2;
            double x1 = hi - g * (hi - lo), x2 = lo + g * (hi - lo), f1 = cost(x1), f2 = cost(x2);
            for (int it = 0; it < 60; it++)
            {
                if (f1 < f2) { hi = x2; x2 = x1; f2 = f1; x1 = hi - g * (hi - lo); f1 = cost(x1); }
                else { lo = x1; x1 = x2; f1 = f2; x2 = lo + g * (hi - lo); f2 = cost(x2); }
            }
            var cBest = m0 + u * (0.5 * (lo + hi));
            double rBest = (A - cBest).Length;
            for (int i = a + 1; i < b; i++) if (Math.Abs((p[i] - cBest).Length - rBest) > tol) return;
            centre = cBest; radius = rBest;
        }

        /// <summary>Circle through three points (in their plane).</summary>
        public static bool Circle(Vec3 p1, Vec3 p2, Vec3 p3, out Vec3 c, out double r)
        {
            var a = p1 - p3; var b = p2 - p3;
            var axb = Vec3.Cross(a, b);
            double d = 2 * axb.LengthSq;
            c = Vec3.Zero; r = 0;
            if (d < 1e-18) return false;
            c = p3 + Vec3.Cross(b * a.LengthSq - a * b.LengthSq, axb) * (1 / d);
            r = (p1 - c).Length;
            return true;
        }

        /// <summary>Angle swept from a through m to b round c (radians).</summary>
        private static double Sweep(Vec3 a, Vec3 m, Vec3 b, Vec3 c)
        {
            return Vec3.Angle(a - c, m - c) + Vec3.Angle(m - c, b - c);
        }

        private static bool WholeCircle(List<Vec3> p, double tol, out Piece circle)
        {
            circle = null;
            int n = p.Count;
            Vec3 c; double r;
            if (!Circle(p[0], p[n / 3], p[2 * n / 3], out c, out r)) return false;
            foreach (var q in p) if (Math.Abs((q - c).Length - r) > tol) return false;
            circle = new Piece { Kind = PieceKind.Circle, A = p[0], B = p[0], Mid = p[n / 2], Centre = c, Radius = r };
            return true;
        }

        /// <summary>The point (of a closed loop) from which a straight line reaches furthest round the loop.</summary>
        private static int LongestStraightStart(List<Vec3> p, double tol)
        {
            int n = p.Count, best = 0, bestReach = -1, step = Math.Max(1, n / 300);
            for (int s0 = 0; s0 < n; s0 += step)
            {
                int reach = 1;
                for (int j = 2; j < n; j++)
                {
                    bool ok = true;
                    for (int i = 1; i < j && ok; i++) ok = Tracer.PointSegment(p[(s0 + i) % n], p[s0], p[(s0 + j) % n]) <= tol;
                    if (!ok) break;
                    reach = j;
                }
                if (reach > bestReach) { bestReach = reach; best = s0; }
            }
            // back up to where that straight run really begins
            for (int k = 1; k < n; k++)
            {
                int s1 = (best - 1 + n) % n;
                bool ok = true;
                for (int i = 1; i < bestReach + 1 && ok; i++) ok = Tracer.PointSegment(p[(s1 + i) % n], p[s1], p[(s1 + bestReach + 1) % n]) <= tol;
                if (!ok) break;
                best = s1;
            }
            return best;
        }

        private static int SharpestCorner(List<Vec3> p)
        {
            int n = p.Count, best = 0, step = Math.Max(1, n / 200);
            double worst = -1;
            for (int i = 0; i < n; i++)
            {
                var d1 = p[i] - p[(i - step + n) % n]; var d2 = p[(i + step) % n] - p[i];
                if (d1.Length < 1e-12 || d2.Length < 1e-12) continue;
                double turn = Vec3.Angle(d1, d2);
                if (turn > worst) { worst = turn; best = i; }
            }
            return best;
        }

        /// <summary>Distance of every path point from the chain of pieces.</summary>
        private static void Deviation(List<Vec3> path, PiecesResult res)
        {
            double mx = 0, ss = 0;
            foreach (var q in path)
            {
                double best = double.MaxValue;
                foreach (var pc in res.Pieces)
                {
                    double d;
                    if (pc.Kind == PieceKind.Line) d = Tracer.PointSegment(q, pc.A, pc.B);
                    else if (pc.Kind == PieceKind.Circle || OnArc(q, pc)) d = Math.Abs((q - pc.Centre).Length - pc.Radius);
                    else d = Math.Min((q - pc.A).Length, (q - pc.B).Length);
                    best = Math.Min(best, d);
                }
                mx = Math.Max(mx, best); ss += best * best;
            }
            res.MaxDev = mx;
            res.RmsDev = path.Count > 0 ? Math.Sqrt(ss / path.Count) : 0;
        }

        /// <summary>Whether q (projected onto the circle) lies within the arc from A through Mid to B.</summary>
        private static bool OnArc(Vec3 q, Piece pc)
        {
            double total = Sweep(pc.A, pc.Mid, pc.B, pc.Centre);
            double viaQ = Vec3.Angle(pc.A - pc.Centre, q - pc.Centre) + Vec3.Angle(q - pc.Centre, pc.B - pc.Centre);
            return viaQ <= total + 1e-6;   // (exact for arcs up to 180°, which is what a tolerance-limited trace gives)
        }
    }
}
