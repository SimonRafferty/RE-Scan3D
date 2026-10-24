// Robust plane fitting: PCA start, then iteratively reweighted least squares with Tukey's biweight so
// stray points (scan spikes, the edge of a neighbouring fillet) stop pulling the plane.
using System;
using System.Collections.Generic;
using ScanRE.Core.Math3;

namespace ScanRE.Core.Fit
{
    public sealed class PlaneFitResult
    {
        public Plane3 Plane;
        /// <summary>RMS distance of inliers (mm).</summary>
        public double Rms;
        /// <summary>Largest inlier distance (mm).</summary>
        public double MaxDev;
        public int Points, Outliers;
        /// <summary>Region the fit came from (full-res triangle ids), for highlighting; may be null.</summary>
        public List<int> Triangles;

        public string Summary()
        {
            return string.Format("rms {0:0.000} max {1:0.000} mm, {2} pts, {3} outliers", Rms, MaxDev, Points, Outliers);
        }
    }

    public static class PlaneFit
    {
        /// <summary>Plain (weighted) least squares: centroid + smallest-eigenvalue direction.</summary>
        public static Plane3 LeastSquares(IList<Vec3> pts, IList<double> w = null)
        {
            if (pts.Count < 3) throw new ArgumentException("a plane needs at least 3 points");
            double sw = 0;
            var c = Vec3.Zero;
            for (int i = 0; i < pts.Count; i++) { double wi = w == null ? 1 : w[i]; c += pts[i] * wi; sw += wi; }
            if (sw <= 0) throw new ArgumentException("all weights zero");
            c = c / sw;
            var cov = new double[3, 3];
            for (int i = 0; i < pts.Count; i++)
            {
                double wi = w == null ? 1 : w[i];
                if (wi == 0) continue;
                var d = pts[i] - c;
                for (int r = 0; r < 3; r++)
                    for (int k = 0; k < 3; k++) cov[r, k] += wi * d[r] * d[k];
            }
            double[] ev; Vec3[] vec;
            SymEigen.Solve(cov, out ev, out vec);
            return new Plane3(c, vec[0]);
        }

        /// <summary>Robust fit. `preferredNormal` (e.g. the picked triangle's normal) fixes the sign of the result.</summary>
        public static PlaneFitResult Robust(IList<Vec3> pts, Vec3? preferredNormal = null, int iterations = 12)
        {
            var pl = LeastSquares(pts);
            int n = pts.Count;
            var w = new double[n];
            var r = new double[n];
            var abs = new double[n];
            double cut = double.PositiveInfinity;
            if (n > 3)
            {
                for (int it = 0; it < iterations; it++)
                {
                    for (int i = 0; i < n; i++) { r[i] = pl.SignedDistance(pts[i]); abs[i] = Math.Abs(r[i]); }
                    double mad = Stats.Median(abs);
                    double s = Math.Max(1.4826 * mad, 1e-9);
                    cut = 4.685 * s;
                    for (int i = 0; i < n; i++)
                    {
                        double u = r[i] / cut;
                        w[i] = Math.Abs(u) < 1 ? (1 - u * u) * (1 - u * u) : 0;
                    }
                    var next = LeastSquares(pts, w);
                    if (Vec3.Dot(next.Normal, pl.Normal) < 0) next.Normal = -next.Normal;
                    bool converged = Vec3.Angle(next.Normal, pl.Normal) < 1e-9 && Math.Abs(next.SignedDistance(pl.Origin)) < 1e-9;
                    pl = next;
                    if (converged) break;
                }
            }
            if (preferredNormal.HasValue && Vec3.Dot(pl.Normal, preferredNormal.Value) < 0) pl.Normal = -pl.Normal;
            var res = new PlaneFitResult { Plane = pl, Points = n };
            double ss = 0; int cnt = 0;
            for (int i = 0; i < n; i++)
            {
                double d = Math.Abs(pl.SignedDistance(pts[i]));
                if (d > cut) { res.Outliers++; continue; }
                ss += d * d; cnt++;
                res.MaxDev = Math.Max(res.MaxDev, d);
            }
            res.Rms = cnt > 0 ? Math.Sqrt(ss / cnt) : 0;
            return res;
        }
    }

    public static class Stats
    {
        /// <summary>Median (copies; O(n) average quickselect).</summary>
        public static double Median(IList<double> v)
        {
            if (v.Count == 0) return 0;
            var a = new double[v.Count];
            v.CopyTo(a, 0);
            int k = a.Length / 2;
            return Select(a, k);
        }

        private static double Select(double[] a, int k)
        {
            int lo = 0, hi = a.Length - 1;
            var rnd = new Random(12345);
            while (lo < hi)
            {
                double pivot = a[lo + rnd.Next(hi - lo + 1)];
                int i = lo, j = hi;
                while (i <= j)
                {
                    while (a[i] < pivot) i++;
                    while (a[j] > pivot) j--;
                    if (i <= j) { var t = a[i]; a[i] = a[j]; a[j] = t; i++; j--; }
                }
                if (k <= j) hi = j;
                else if (k >= i) lo = i;
                else return a[k];
            }
            return a[k];
        }

        public static double Rms(IList<double> v)
        {
            double s = 0;
            foreach (var x in v) s += x * x;
            return v.Count > 0 ? Math.Sqrt(s / v.Count) : 0;
        }
    }
}
