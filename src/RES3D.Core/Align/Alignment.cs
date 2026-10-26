// Alignment by "mating" fitted scan planes to the SolidWorks base planes (spec §2). These are add-in
// constraints, never SolidWorks mates. The solver never fails: with imperfect (non-orthogonal) scan faces,
// the error goes to the later mates (datum priority) or is spread by weight (best fit).
using System;
using System.Collections.Generic;
using RES3D.Core.Math3;

namespace RES3D.Core.Align
{
    public enum BasePlane { Front, Top, Right }

    public enum MateType { Coincident, Parallel, Perpendicular, Angle, Distance }

    public enum SolverMode { Datum, Weighted }

    public sealed class AlignMate
    {
        /// <summary>Id of the fitted plane record (ScanState.Fits) the mate uses.</summary>
        public string FitId;
        public BasePlane Target;
        public MateType Type;
        /// <summary>Angle (degrees) for Angle, distance (mm, along the base plane normal) for Distance.</summary>
        public double Value;
        /// <summary>+1: face normal along the base plane normal, -1: against it, 0: not decided yet (the first
        /// solve picks whichever is nearer and stores it, so re-solving never flips the scan). Flip negates it.</summary>
        public int Sense;
        public bool Suppressed;
        /// <summary>Relative weight in Weighted mode.</summary>
        public double Weight = 1;
        /// <summary>What the user entered in the Align window (offset mm / angle °, and Flip), kept so the mate is
        /// shown and edited as entered; Value and Sense above are what the solver uses.</summary>
        public double UserValue;
        public bool UserFlip;

        public string Describe()
        {
            string v = Type == MateType.Angle ? string.Format(" {0:0.###}°", Value) : Type == MateType.Distance ? string.Format(" {0:0.###} mm", Value) : "";
            return string.Format("{0}{1} {2} {3} Plane{4}{5}", FitId, Sense < 0 ? " (opposed)" : "", Type.ToString().ToLowerInvariant(), Target, v, Suppressed ? " [suppressed]" : "");
        }
    }

    public static class BasePlanes
    {
        /// <summary>SolidWorks base plane normals in part coordinates: Front = XY (+Z), Top = XZ (+Y), Right = YZ (+X).</summary>
        public static Vec3 Normal(BasePlane p)
        {
            return p == BasePlane.Front ? Vec3.UnitZ : p == BasePlane.Top ? Vec3.UnitY : Vec3.UnitX;
        }

        public static string SwName(BasePlane p) { return p + " Plane"; }
    }

    public sealed class AlignResult
    {
        /// <summary>Scan coordinates -> part coordinates (before manual nudge).</summary>
        public Rigid Transform;
        /// <summary>Per active mate: angular residual (degrees) and positional residual (mm, NaN when not positional).</summary>
        public readonly List<double> AngleErrorDeg = new List<double>();
        public readonly List<double> PositionErrorMm = new List<double>();
        public readonly List<string> Notes = new List<string>();
    }

    public static class AlignSolver
    {
        /// <param name="faces">FitId -> fitted plane in SCAN coordinates.</param>
        /// <param name="current">The current scan->part transform; the solve moves the scan as little as possible from it.</param>
        public static AlignResult Solve(IList<AlignMate> mates, IDictionary<string, Plane3> faces, Rigid current, SolverMode mode, Vec3 scanCentroid)
        {
            var active = new List<AlignMate>();
            foreach (var m in mates)
                if (!m.Suppressed && faces.ContainsKey(m.FitId)) active.Add(m);
            var res = new AlignResult { Transform = current };
            if (active.Count == 0) { res.Notes.Add("no active mates: scan left where it is"); return res; }

            var t = SolveDatum(active, faces, current, scanCentroid, res.Notes);
            if (mode == SolverMode.Weighted && active.Count > 1)
                t = RefineWeighted(active, faces, t, scanCentroid);
            res.Transform = t;
            foreach (var m in active)
            {
                double ang, pos;
                Residuals(m, faces[m.FitId], t, out ang, out pos);
                res.AngleErrorDeg.Add(ang * 180 / Math.PI);
                res.PositionErrorMm.Add(pos);
            }
            return res;
        }

        // ------------------------------------------------------------------ datum (priority) solve

        private enum Freedom { Full, OneAxis, TwoAxes, None }

        private static Rigid SolveDatum(List<AlignMate> mates, IDictionary<string, Plane3> faces, Rigid current, Vec3 centroid, List<string> notes)
        {
            // rotations are applied about the scan's (current) centroid so the scan doesn't swing away
            var R = current.R;
            var pivot = current.Apply(centroid);
            Freedom free = Freedom.Full;
            Vec3 axisA = Vec3.UnitZ, axisB = Vec3.UnitX;   // the remaining rotation axes (part coordinates)

            foreach (var m in mates)
            {
                var n = R * faces[m.FitId].Normal;   // face normal in part coordinates now
                var N = BasePlanes.Normal(m.Target);
                bool parallelType = m.Type == MateType.Coincident || m.Type == MateType.Distance || m.Type == MateType.Parallel;
                if (free == Freedom.None)
                {
                    notes.Add(m.Describe() + ": orientation already fixed by earlier mates; angular error stays on this mate");
                    continue;
                }
                if (free == Freedom.Full)
                {
                    if (m.Sense == 0 && m.Type != MateType.Perpendicular) m.Sense = Vec3.Dot(n, N) >= 0 ? 1 : -1;
                    Vec3 target = parallelType ? N * m.Sense : m.Type == MateType.Perpendicular
                        ? ProjectPerp(n, N) : AngleTarget(n, N, m.Value * Math.PI / 180, m.Sense);
                    R = (Mat3.RotationBetween(n, target) * R).Orthonormalized();
                    if (parallelType) { free = Freedom.OneAxis; axisA = N; }
                    else { free = Freedom.TwoAxes; axisA = N; axisB = target; }
                    continue;
                }
                if (m.Sense == 0 && m.Type != MateType.Perpendicular) m.Sense = Vec3.Dot(n, N) >= 0 ? 1 : -1;
                // constrained: search the remaining rotation(s) for the smallest error on this mate,
                // preferring the smallest movement among equally good answers
                Func<Mat3, double> err = rot =>
                {
                    double a, p;
                    Residuals(m, faces[m.FitId], new Rigid(rot, Vec3.Zero), out a, out p);
                    return a;
                };
                if (free == Freedom.OneAxis)
                {
                    double best = Search1D(th => err(Mat3.Rotation(axisA, th) * R));
                    double e0 = err(Mat3.Rotation(axisA, 0) * R), eBest = err(Mat3.Rotation(axisA, best) * R);
                    if (IsFlat(th => err(Mat3.Rotation(axisA, th) * R)))
                    {
                        notes.Add(m.Describe() + ": adds no information about rotation (parallel to an earlier mate)");
                        continue;   // freedom unchanged; a later mate may use it
                    }
                    R = (Mat3.Rotation(axisA, best) * R).Orthonormalized();
                    free = Freedom.None;
                    if (eBest > 1e-9 && e0 >= eBest) { /* best fit within the remaining freedom */ }
                }
                else // TwoAxes
                {
                    // rotate about the mated face normal first (it stays put), then about the base normal
                    // (the angle to it stays put): both keep the earlier angle/perpendicular mate satisfied
                    double bestA = 0, bestB = 0, bestE = double.MaxValue;
                    for (int i = -36; i <= 36; i++)
                        for (int j = -36; j <= 36; j++)
                        {
                            double a = i * Math.PI / 36, b = j * Math.PI / 36;
                            double e = err(Mat3.Rotation(axisA, a) * Mat3.Rotation(axisB, b) * R) + 1e-9 * (Math.Abs(a) + Math.Abs(b));
                            if (e < bestE) { bestE = e; bestA = a; bestB = b; }
                        }
                    double step = Math.PI / 36;
                    for (int it = 0; it < 60 && step > 1e-12; it++)
                    {
                        bool moved = false;
                        foreach (var d in new[] { new[] { 1, 0 }, new[] { -1, 0 }, new[] { 0, 1 }, new[] { 0, -1 } })
                        {
                            double a = bestA + d[0] * step, b = bestB + d[1] * step;
                            double e = err(Mat3.Rotation(axisA, a) * Mat3.Rotation(axisB, b) * R) + 1e-9 * (Math.Abs(a) + Math.Abs(b));
                            if (e < bestE) { bestE = e; bestA = a; bestB = b; moved = true; }
                        }
                        if (!moved) step /= 2;
                    }
                    R = (Mat3.Rotation(axisA, bestA) * Mat3.Rotation(axisB, bestB) * R).Orthonormalized();
                    free = Freedom.None;
                }
            }

            // translation: keep the pivot fixed under the rotation, then satisfy positional mates in priority
            // order, each only along directions not already fixed by earlier ones
            var T = pivot - R * centroid;
            var fixedDirs = new List<Vec3>();
            foreach (var m in mates)
            {
                if (m.Type != MateType.Coincident && m.Type != MateType.Distance) continue;
                var N = BasePlanes.Normal(m.Target);
                var face = faces[m.FitId];
                double want = m.Type == MateType.Distance ? m.Value : 0;
                double have = Vec3.Dot(N, R * face.Origin + T);
                // move along N's component orthogonal to already-fixed directions
                var dir = N;
                foreach (var f in fixedDirs) dir = dir - f * Vec3.Dot(dir, f);
                double k = Vec3.Dot(dir, N);
                if (dir.Length < 1e-6 || Math.Abs(k) < 1e-6)
                {
                    notes.Add(m.Describe() + ": position already fixed by earlier mates; positional error stays on this mate");
                    continue;
                }
                T = T + dir * ((want - have) / k);
                fixedDirs.Add(dir.Normalized());
            }
            return new Rigid(R, T);
        }

        private static Vec3 ProjectPerp(Vec3 n, Vec3 N)
        {
            var p = n - N * Vec3.Dot(n, N);
            return p.Length < 1e-9 ? N.AnyPerpendicular() : p.Normalized();
        }

        private static Vec3 AngleTarget(Vec3 n, Vec3 N, double angle, int sense)
        {
            // rotate within the plane spanned by n and N to make angle(n, N) = angle (or pi - angle when opposed)
            var perp = ProjectPerp(n, N);
            double a = sense < 0 ? Math.PI - angle : angle;
            return (N * Math.Cos(a) + perp * Math.Sin(a)).Normalized();
        }

        /// <summary>Angular (radians) and positional (mm; NaN if none) residual of a mate under transform t.</summary>
        public static void Residuals(AlignMate m, Plane3 face, Rigid t, out double angle, out double pos)
        {
            var n = t.ApplyDir(face.Normal).Normalized();
            var N = BasePlanes.Normal(m.Target);
            double a = Vec3.Angle(n, N);
            switch (m.Type)
            {
                case MateType.Perpendicular: angle = Math.Abs(a - Math.PI / 2); break;
                case MateType.Angle:
                    {
                        double want = m.Value * Math.PI / 180;
                        double aligned = Math.Abs(a - want), opposed = Math.Abs(a - (Math.PI - want));
                        angle = m.Sense > 0 ? aligned : m.Sense < 0 ? opposed : Math.Min(aligned, opposed);
                        break;
                    }
                default:
                    angle = m.Sense > 0 ? a : m.Sense < 0 ? Math.PI - a : Math.Min(a, Math.PI - a);
                    break;
            }
            pos = double.NaN;
            if (m.Type == MateType.Coincident || m.Type == MateType.Distance)
            {
                double want = m.Type == MateType.Distance ? m.Value : 0;
                pos = Vec3.Dot(N, t.Apply(face.Origin)) - want;
            }
        }

        private static double Search1D(Func<double, double> f)
        {
            // coarse scan of the full turn, then golden-section refinement; ties go to the smallest rotation
            double best = 0, bestV = f(0);
            for (int i = -180; i <= 180; i++)
            {
                double th = i * Math.PI / 180;
                double v = f(th) + 1e-10 * Math.Abs(th);
                if (v < bestV) { bestV = v; best = th; }
            }
            double a = best - Math.PI / 180, b = best + Math.PI / 180;
            const double g = 0.6180339887498949;
            double c = b - g * (b - a), d = a + g * (b - a);
            double fc = f(c), fd = f(d);
            for (int it = 0; it < 80; it++)
            {
                if (fc < fd) { b = d; d = c; fd = fc; c = b - g * (b - a); fc = f(c); }
                else { a = c; c = d; fc = fd; d = a + g * (b - a); fd = f(d); }
            }
            double mid = 0.5 * (a + b);
            return f(mid) <= f(best) ? mid : best;
        }

        private static bool IsFlat(Func<double, double> f)
        {
            double lo = double.MaxValue, hi = double.MinValue;
            for (int i = 0; i < 24; i++) { double v = f(i * Math.PI / 12); lo = Math.Min(lo, v); hi = Math.Max(hi, v); }
            return hi - lo < 1e-9;
        }

        // ------------------------------------------------------------------ weighted best fit

        /// <summary>Levenberg-Marquardt over a rotation vector (about the scan centroid) and translation.</summary>
        private static Rigid RefineWeighted(List<AlignMate> mates, IDictionary<string, Plane3> faces, Rigid start, Vec3 centroid)
        {
            const double L = 100;   // mm per radian: balances angular against positional residuals
            var x = new double[6];
            var pivot = start.Apply(centroid);
            Func<double[], Rigid> build = p =>
            {
                var dR = Mat3.FromRotationVector(new Vec3(p[0], p[1], p[2]));
                var R = dR * start.R;
                // rotate about the pivot, then translate
                var T = pivot + dR * (start.T - pivot) + new Vec3(p[3], p[4], p[5]);
                return new Rigid(R, T);
            };
            Func<double[], List<double>> resid = p =>
            {
                var t = build(p);
                var r = new List<double>();
                foreach (var m in mates)
                {
                    double a, pos;
                    Residuals(m, faces[m.FitId], t, out a, out pos);
                    double w = Math.Sqrt(Math.Max(0, m.Weight));
                    r.Add(w * a * L);
                    r.Add(double.IsNaN(pos) ? 0 : w * pos);
                }
                // weak pull toward the start, so unconstrained DOF don't wander
                for (int i = 0; i < 6; i++) r.Add(1e-4 * (i < 3 ? L : 1) * p[i]);
                return r;
            };
            double lambda = 1e-3;
            var r0 = resid(x);
            double cost = SumSq(r0);
            for (int it = 0; it < 100; it++)
            {
                int m = r0.Count;
                var J = new double[m, 6];
                const double h = 1e-7;
                for (int k = 0; k < 6; k++)
                {
                    var xp = (double[])x.Clone(); xp[k] += h;
                    var xm = (double[])x.Clone(); xm[k] -= h;
                    var rp = resid(xp); var rm = resid(xm);
                    for (int i = 0; i < m; i++) J[i, k] = (rp[i] - rm[i]) / (2 * h);
                }
                var A = new double[6, 6]; var g = new double[6];
                for (int a = 0; a < 6; a++)
                {
                    for (int i = 0; i < m; i++) g[a] -= J[i, a] * r0[i];
                    for (int b = 0; b < 6; b++) for (int i = 0; i < m; i++) A[a, b] += J[i, a] * J[i, b];
                }
                bool improved = false;
                for (int tries = 0; tries < 10 && !improved; tries++)
                {
                    var Ad = (double[,])A.Clone();
                    for (int a = 0; a < 6; a++) Ad[a, a] += lambda * (A[a, a] + 1e-12);
                    var d = Fit.Linear.Solve(Ad, g);
                    if (d == null) { lambda *= 10; continue; }
                    var xn = new double[6];
                    for (int a = 0; a < 6; a++) xn[a] = x[a] + d[a];
                    var rn = resid(xn);
                    double cn = SumSq(rn);
                    if (cn < cost) { x = xn; r0 = rn; improved = cost - cn > 1e-18; cost = cn; lambda = Math.Max(1e-9, lambda / 10); if (!improved) break; improved = true; }
                    else lambda *= 10;
                }
                if (!improved) break;
            }
            return build(x);
        }

        private static double SumSq(List<double> r) { double s = 0; foreach (var v in r) s += v * v; return s; }
    }
}
