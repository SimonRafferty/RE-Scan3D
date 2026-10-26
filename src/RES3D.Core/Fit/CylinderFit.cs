// Robust cylinder fitting (holes, bosses, shafts -> reference axis + radius).
// Start: axis = direction least represented in the surface normals (smallest eigenvector of Σ n nᵀ), then a
// circle fit in the perpendicular plane. Refine: Gauss-Newton on (2 axis angles, 2 axis offsets, radius)
// with Tukey reweighting, the same robust scheme as PlaneFit.
using System;
using System.Collections.Generic;
using RES3D.Core.Math3;

namespace RES3D.Core.Fit
{
    public sealed class CylinderFitResult
    {
        /// <summary>A point on the axis (the projection of the data centroid) and the unit axis direction.</summary>
        public Vec3 AxisPoint, AxisDir;
        public double Radius;
        public double Rms, MaxDev;
        public int Points, Outliers;
        /// <summary>Extent of the data along the axis, from AxisPoint (for drawing an axis of sensible length).</summary>
        public double MinT, MaxT;
        /// <summary>True when most normals point away from the axis (a boss/shaft), false for a hole.</summary>
        public bool Convex;
        public List<int> Triangles;

        public string Summary()
        {
            return string.Format("Ø{0:0.000} mm, rms {1:0.000} max {2:0.000} mm, {3} pts, {4} outliers", 2 * Radius, Rms, MaxDev, Points, Outliers);
        }
    }

    public static class CylinderFit
    {
        public static CylinderFitResult Robust(IList<Vec3> pts, IList<Vec3> normals, int iterations = 15)
        {
            if (pts.Count < 6) throw new ArgumentException("a cylinder needs at least 6 points");
            // ---- initial axis from normals
            Vec3 axis;
            if (normals != null && normals.Count >= 3)
            {
                var cov = new double[3, 3];
                foreach (var n0 in normals)
                {
                    var n = n0.Normalized();
                    for (int r = 0; r < 3; r++) for (int k = 0; k < 3; k++) cov[r, k] += n[r] * n[k];
                }
                double[] ev; Vec3[] vec;
                SymEigen.Solve(cov, out ev, out vec);
                axis = vec[0];
            }
            else
            {
                // no normals: the axis is the direction of largest spread (works for long cylinders)
                var cov = new double[3, 3];
                var c0 = Centroid(pts);
                foreach (var p in pts) { var d = p - c0; for (int r = 0; r < 3; r++) for (int k = 0; k < 3; k++) cov[r, k] += d[r] * d[k]; }
                double[] ev; Vec3[] vec;
                SymEigen.Solve(cov, out ev, out vec);
                axis = vec[2];
            }
            var centroid = Centroid(pts);
            Vec3 centre; double radius;
            FitCircle(pts, null, centroid, axis, out centre, out radius);

            // ---- Gauss-Newton refinement with robust weights
            int n_ = pts.Count;
            var w = new double[n_];
            for (int i = 0; i < n_; i++) w[i] = 1;
            double cut = double.PositiveInfinity;
            var res = new double[n_];
            var abs = new double[n_];
            for (int it = 0; it < iterations; it++)
            {
                GaussNewton(pts, w, ref centre, ref axis, ref radius, 6);
                for (int i = 0; i < n_; i++) { res[i] = Residual(pts[i], centre, axis, radius); abs[i] = Math.Abs(res[i]); }
                double s = Math.Max(1.4826 * Stats.Median(abs), 1e-9);
                cut = 4.685 * s;
                for (int i = 0; i < n_; i++) { double u = res[i] / cut; w[i] = Math.Abs(u) < 1 ? (1 - u * u) * (1 - u * u) : 0; }
            }

            var result = new CylinderFitResult { AxisDir = axis.Normalized(), Radius = Math.Abs(radius), Points = n_ };
            // canonical axis point: projection of the centroid
            result.AxisPoint = centre + result.AxisDir * Vec3.Dot(centroid - centre, result.AxisDir);
            double ss = 0; int cnt = 0, outward = 0;
            result.MinT = double.MaxValue; result.MaxT = double.MinValue;
            for (int i = 0; i < n_; i++)
            {
                double d = Math.Abs(Residual(pts[i], result.AxisPoint, result.AxisDir, result.Radius));
                double t = Vec3.Dot(pts[i] - result.AxisPoint, result.AxisDir);
                if (d > cut) { result.Outliers++; continue; }
                result.MinT = Math.Min(result.MinT, t); result.MaxT = Math.Max(result.MaxT, t);
                ss += d * d; cnt++;
                result.MaxDev = Math.Max(result.MaxDev, d);
                if (normals != null && i < normals.Count)
                {
                    var radial = pts[i] - result.AxisPoint - result.AxisDir * t;
                    if (Vec3.Dot(radial, normals[i]) > 0) outward++;
                }
            }
            result.Rms = cnt > 0 ? Math.Sqrt(ss / cnt) : 0;
            result.Convex = outward * 2 > cnt;
            return result;
        }

        private static Vec3 Centroid(IList<Vec3> pts)
        {
            var c = Vec3.Zero;
            foreach (var p in pts) c += p;
            return c / pts.Count;
        }

        /// <summary>Signed distance from the cylinder surface.</summary>
        public static double Residual(Vec3 p, Vec3 c, Vec3 axis, double r)
        {
            var d = p - c;
            var radial = d - axis * Vec3.Dot(d, axis);
            return radial.Length - r;
        }

        /// <summary>Algebraic (Kåsa) circle fit in the plane perpendicular to `axis`, then a few geometric iterations.</summary>
        private static void FitCircle(IList<Vec3> pts, IList<double> w, Vec3 origin, Vec3 axis, out Vec3 centre, out double radius)
        {
            var pl = new Plane3(origin, axis);
            Vec3 u, v;
            pl.Basis(out u, out v);
            // solve [x y 1][a b c]ᵀ = x²+y²  (normal equations)
            double sxx = 0, sxy = 0, syy = 0, sx = 0, sy = 0, s1 = 0, sxz = 0, syz = 0, sz = 0;
            for (int i = 0; i < pts.Count; i++)
            {
                var d = pts[i] - origin;
                double x = Vec3.Dot(d, u), y = Vec3.Dot(d, v), z = x * x + y * y;
                double wi = w == null ? 1 : w[i];
                sxx += wi * x * x; sxy += wi * x * y; syy += wi * y * y; sx += wi * x; sy += wi * y; s1 += wi;
                sxz += wi * x * z; syz += wi * y * z; sz += wi * z;
            }
            var A = new double[3, 3] { { sxx, sxy, sx }, { sxy, syy, sy }, { sx, sy, s1 } };
            var b = new[] { sxz, syz, sz };
            var sol = Linear.Solve(A, b);
            double cx, cy;
            if (sol == null) { cx = 0; cy = 0; radius = 1; }
            else
            {
                cx = sol[0] / 2; cy = sol[1] / 2;
                radius = Math.Sqrt(Math.Max(1e-12, sol[2] + cx * cx + cy * cy));
            }
            centre = origin + u * cx + v * cy;
        }

        private static void GaussNewton(IList<Vec3> pts, double[] w, ref Vec3 c, ref Vec3 axis, ref double r, int steps)
        {
            for (int s = 0; s < steps; s++)
            {
                // parameterise around the current estimate: axis' = normalize(axis + a1 e1 + a2 e2), c' = c + b1 e1 + b2 e2, r' = r + dr
                var e1 = axis.AnyPerpendicular();
                var e2 = Vec3.Cross(axis, e1);
                var JtJ = new double[5, 5];
                var Jtr = new double[5];
                const double h = 1e-6;
                for (int i = 0; i < pts.Count; i++)
                {
                    if (w[i] == 0) continue;
                    double f0 = Residual(pts[i], c, axis, r);
                    var J = new double[5];
                    // analytic derivatives are fiddly with the normalisation; central differences are exact enough
                    J[0] = (Residual(pts[i], c, (axis + e1 * h).Normalized(), r) - Residual(pts[i], c, (axis - e1 * h).Normalized(), r)) / (2 * h);
                    J[1] = (Residual(pts[i], c, (axis + e2 * h).Normalized(), r) - Residual(pts[i], c, (axis - e2 * h).Normalized(), r)) / (2 * h);
                    J[2] = (Residual(pts[i], c + e1 * h, axis, r) - Residual(pts[i], c - e1 * h, axis, r)) / (2 * h);
                    J[3] = (Residual(pts[i], c + e2 * h, axis, r) - Residual(pts[i], c - e2 * h, axis, r)) / (2 * h);
                    J[4] = -1;
                    for (int a = 0; a < 5; a++)
                    {
                        Jtr[a] += w[i] * J[a] * f0;
                        for (int b = 0; b < 5; b++) JtJ[a, b] += w[i] * J[a] * J[b];
                    }
                }
                for (int a = 0; a < 5; a++) JtJ[a, a] *= 1 + 1e-9;   // tiny damping
                var neg = new double[5];
                for (int a = 0; a < 5; a++) neg[a] = -Jtr[a];
                var d = Linear.Solve(JtJ, neg);
                if (d == null) return;
                axis = (axis + e1 * d[0] + e2 * d[1]).Normalized();
                c = c + e1 * d[2] + e2 * d[3];
                r += d[4];
                double step = Math.Abs(d[0]) + Math.Abs(d[1]) + (Math.Abs(d[2]) + Math.Abs(d[3]) + Math.Abs(d[4])) / Math.Max(1, Math.Abs(r));
                if (step < 1e-12) return;
            }
        }
    }

    public static class Linear
    {
        /// <summary>Gaussian elimination with partial pivoting; null when singular.</summary>
        public static double[] Solve(double[,] A0, double[] b0)
        {
            int n = b0.Length;
            var A = (double[,])A0.Clone();
            var b = (double[])b0.Clone();
            double scale = 0;
            for (int i = 0; i < n; i++) for (int j = 0; j < n; j++) scale = Math.Max(scale, Math.Abs(A[i, j]));
            if (scale == 0) return null;
            for (int col = 0; col < n; col++)
            {
                int piv = col;
                for (int r = col + 1; r < n; r++) if (Math.Abs(A[r, col]) > Math.Abs(A[piv, col])) piv = r;
                if (Math.Abs(A[piv, col]) < 1e-14 * scale) return null;
                if (piv != col)
                {
                    for (int k = 0; k < n; k++) { var t = A[col, k]; A[col, k] = A[piv, k]; A[piv, k] = t; }
                    var tb = b[col]; b[col] = b[piv]; b[piv] = tb;
                }
                for (int r = col + 1; r < n; r++)
                {
                    double f = A[r, col] / A[col, col];
                    if (f == 0) continue;
                    for (int k = col; k < n; k++) A[r, k] -= f * A[col, k];
                    b[r] -= f * b[col];
                }
            }
            var x = new double[n];
            for (int i = n - 1; i >= 0; i--)
            {
                double s = b[i];
                for (int k = i + 1; k < n; k++) s -= A[i, k] * x[k];
                x[i] = s / A[i, i];
            }
            return x;
        }
    }
}
