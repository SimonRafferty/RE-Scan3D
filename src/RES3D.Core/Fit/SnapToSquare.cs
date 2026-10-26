// Real parts are usually designed square; scans are noisy (spec §3 "Relationships"). Snap a fitted direction
// to exactly parallel or perpendicular to existing reference directions when within tolerance, and say so.
using System;
using System.Collections.Generic;
using RES3D.Core.Math3;

namespace RES3D.Core.Fit
{
    public sealed class ReferenceDir
    {
        public string Name;
        public Vec3 Dir;
        /// <summary>A point on the reference (axis point, or plane origin), for coaxial/coincident snapping.</summary>
        public Vec3 Point;
        public bool IsAxis;
    }

    public sealed class SnapResult
    {
        public Vec3 Dir;
        /// <summary>Angle moved (degrees); 0 when nothing snapped.</summary>
        public double AdjustDeg;
        /// <summary>Human-readable relations applied, e.g. "parallel to Top Plane".</summary>
        public readonly List<string> Relations = new List<string>();
        public bool Snapped { get { return Relations.Count > 0; } }
        public string Describe()
        {
            return Snapped ? string.Join(", ", Relations) + string.Format(" (moved {0:0.###}°)", AdjustDeg) : "no snap";
        }
    }

    public static class SnapToSquare
    {
        /// <summary>
        /// Snap `dir` (a plane normal or an axis direction). Parallel beats perpendicular; two perpendicular
        /// references fix the direction completely (their cross product).
        /// </summary>
        public static SnapResult SnapDirection(Vec3 dir, IList<ReferenceDir> refs, double tolDeg)
        {
            dir = dir.Normalized();
            var res = new SnapResult { Dir = dir };
            if (refs == null || refs.Count == 0 || tolDeg <= 0) return res;
            double tol = tolDeg * Math.PI / 180;

            // parallel: the closest reference within tolerance
            ReferenceDir bestPar = null; double bestAng = tol;
            foreach (var r in refs)
            {
                double a = Vec3.Angle(dir, r.Dir);
                double off = Math.Min(a, Math.PI - a);
                if (off <= bestAng) { bestAng = off; bestPar = r; }
            }
            if (bestPar != null)
            {
                var d = Vec3.Dot(dir, bestPar.Dir) >= 0 ? bestPar.Dir.Normalized() : -bestPar.Dir.Normalized();
                res.Relations.Add("parallel to " + bestPar.Name);
                Finish(res, dir, d);
                return res;
            }

            // perpendicular to one or two references
            var perp = new List<ReferenceDir>();
            foreach (var r in refs)
            {
                double off = Math.Abs(Math.PI / 2 - Vec3.Angle(dir, r.Dir));
                if (off <= tol) perp.Add(r);
            }
            if (perp.Count == 0) return res;
            perp.Sort((p, q) => Math.Abs(Math.PI / 2 - Vec3.Angle(dir, p.Dir)).CompareTo(Math.Abs(Math.PI / 2 - Vec3.Angle(dir, q.Dir))));
            var r0 = perp[0].Dir.Normalized();
            // a second, non-parallel perpendicular reference determines the direction
            for (int i = 1; i < perp.Count; i++)
            {
                var c = Vec3.Cross(r0, perp[i].Dir);
                if (c.Length < 0.2) continue;   // nearly parallel references: no extra information
                c = c.Normalized();
                if (Vec3.Dot(c, dir) < 0) c = -c;
                res.Relations.Add("perpendicular to " + perp[0].Name);
                res.Relations.Add("perpendicular to " + perp[i].Name);
                Finish(res, dir, c);
                return res;
            }
            var proj = (dir - r0 * Vec3.Dot(dir, r0)).Normalized();
            res.Relations.Add("perpendicular to " + perp[0].Name);
            Finish(res, dir, proj);
            return res;
        }

        private static void Finish(SnapResult res, Vec3 from, Vec3 to)
        {
            res.AdjustDeg = Vec3.Angle(from, to) * 180 / Math.PI;
            res.Dir = to;
        }

        /// <summary>
        /// Coaxial snap for an axis already snapped parallel: if an existing axis is within `posTol` (mm),
        /// return its point so the new axis coincides with it.
        /// </summary>
        public static bool SnapCoaxial(Vec3 point, Vec3 dir, IList<ReferenceDir> refs, double posTol, out Vec3 snappedPoint, out string relation)
        {
            snappedPoint = point; relation = null;
            if (refs == null) return false;
            double best = posTol;
            foreach (var r in refs)
            {
                if (!r.IsAxis) continue;
                if (Math.Abs(Math.Abs(Vec3.Dot(dir.Normalized(), r.Dir.Normalized())) - 1) > 1e-9) continue;
                var d = point - r.Point;
                double dist = (d - r.Dir.Normalized() * Vec3.Dot(d, r.Dir.Normalized())).Length;
                if (dist <= best)
                {
                    best = dist;
                    snappedPoint = r.Point + r.Dir.Normalized() * Vec3.Dot(d, r.Dir.Normalized());
                    relation = string.Format("coaxial with {0} (moved {1:0.###} mm)", r.Name, dist);
                }
            }
            return relation != null;
        }
    }
}
