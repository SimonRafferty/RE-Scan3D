// Click-once face selection (spec §3): grow from the picked triangle across connected triangles while they
// stay within a normal and a distance tolerance of the shape fitted so far, refitting as the region doubles.
using System;
using System.Collections.Generic;
using RES3D.Core.Jobs;
using RES3D.Core.Math3;
using RES3D.Core.Mesh;

namespace RES3D.Core.Fit
{
    public sealed class GrowOptions
    {
        /// <summary>Max angle (degrees) between a triangle's normal and the fitted surface normal there.</summary>
        public double NormalTolDeg = 20;
        /// <summary>Max distance (mm) of a triangle's vertices from the fitted surface.</summary>
        public double DistTol = 0.2;
        /// <summary>Safety limit on region size.</summary>
        public int MaxTriangles = 5000000;
        /// <summary>Initial smoothness-only growth for cylinders before the first fit (triangles).</summary>
        public int CylinderSeedTriangles = 300;
        /// <summary>Normal change between neighbours (degrees) allowed during the cylinder seed phase.</summary>
        public double SmoothTolDeg = 12;
    }

    public static class RegionGrow
    {
        public static PlaneFitResult GrowPlane(TriMesh m, TriAdjacency adj, int seed, GrowOptions opt, JobContext job)
        {
            // seed plane from a small patch around the pick, not the single triangle: on dense noisy scans one
            // triangle's normal can be tens of degrees off (bench: 4M-triangle mesh, σ 0.03 mm at 0.07 mm spacing)
            var seedN = SmoothNormal(m, adj, seed);
            var patch = Flood(m, adj, seed, t => true, 64, 64, job);
            var plane = patch.Count >= 16 ? PlaneFit.Robust(VerticesOf(m, patch), seedN).Plane : new Plane3(m.TriangleCentroid(seed), seedN);
            double cosTol = Math.Cos(opt.NormalTolDeg * Math.PI / 180);
            List<int> region = null;
            // grow in stages (64, 256, 1024... triangles), refitting between stages; once the region stops at
            // the face's edge on its own, regrow against the refined plane until the count settles. Each
            // stage regrows from the seed, so early, poorly-fitted acceptance can't leak into the result.
            int limit = 64;
            for (int pass = 0; pass < 12; pass++)
            {
                var p = plane;
                Func<int, bool> accept = t =>
                {
                    var n = SmoothNormal(m, adj, t);
                    if (Math.Abs(Vec3.Dot(n, p.Normal)) < cosTol) return false;
                    Vec3 a, b, c;
                    m.Triangle(t, out a, out b, out c);
                    return Math.Abs(p.SignedDistance(a)) <= opt.DistTol && Math.Abs(p.SignedDistance(b)) <= opt.DistTol &&
                           Math.Abs(p.SignedDistance(c)) <= opt.DistTol;
                };
                int before = region == null ? -1 : region.Count;
                region = Flood(m, adj, seed, accept, opt.MaxTriangles, limit, job);
                var pts = VerticesOf(m, region);
                if (pts.Count < 3) break;
                plane = PlaneFit.Robust(pts, seedN).Plane;
                bool grownOut = region.Count < limit;
                if (grownOut && Math.Abs(region.Count - before) <= Math.Max(2, before / 1000)) break;
                limit = grownOut ? int.MaxValue : limit * 4;
            }
            // fit on the interior only: the outermost rings sit where the face starts to roll into a fillet or
            // edge and bias the plane (measured: results/accuracy_*, plane offset_err)
            var core = Erode(m, adj, region, 2);
            var final = PlaneFit.Robust(VerticesOf(m, core), seedN);
            final.Triangles = region;
            return final;
        }

        public static CylinderFitResult GrowCylinder(TriMesh m, TriAdjacency adj, int seed, GrowOptions opt, JobContext job)
        {
            // phase 1: smoothness-only growth to gather enough curvature to fit
            double cosSmooth = Math.Cos(opt.SmoothTolDeg * Math.PI / 180);
            var region = FloodSmooth(m, adj, seed, cosSmooth, opt.CylinderSeedTriangles, job);
            if (region.Count < 20)
                throw new Res3dException("The picked surface is too small or too noisy to fit a cylinder.",
                    "Pick nearer the middle of the cylindrical face, or increase the smoothness tolerance.");
            CylinderFitResult fit = null;
            double cosTol = Math.Cos(opt.NormalTolDeg * Math.PI / 180);
            int limit = 4 * region.Count;
            for (int pass = 0; pass < 8; pass++)
            {
                List<Vec3> pts, nrm;
                PointsAndNormals(m, region, out pts, out nrm);
                fit = CylinderFit.Robust(pts, nrm);
                var f = fit;
                Func<int, bool> accept = t =>
                {
                    Vec3 a, b, c;
                    m.Triangle(t, out a, out b, out c);
                    if (Math.Abs(CylinderFit.Residual(a, f.AxisPoint, f.AxisDir, f.Radius)) > opt.DistTol ||
                        Math.Abs(CylinderFit.Residual(b, f.AxisPoint, f.AxisDir, f.Radius)) > opt.DistTol ||
                        Math.Abs(CylinderFit.Residual(c, f.AxisPoint, f.AxisDir, f.Radius)) > opt.DistTol) return false;
                    var cen = (a + b + c) / 3;
                    var d = cen - f.AxisPoint;
                    var radial = (d - f.AxisDir * Vec3.Dot(d, f.AxisDir)).Normalized();
                    return Math.Abs(Vec3.Dot(radial, SmoothNormal(m, adj, t))) >= cosTol;
                };
                int before = region.Count;
                region = Flood(m, adj, seed, accept, opt.MaxTriangles, pass == 7 ? int.MaxValue : limit, job);
                if (region.Count < limit && Math.Abs(region.Count - before) <= Math.Max(2, before / 1000)) break;
                limit = region.Count >= limit ? limit * 4 : int.MaxValue;
            }
            List<Vec3> fp, fn;
            PointsAndNormals(m, Erode(m, adj, region, 2), out fp, out fn);
            fit = CylinderFit.Robust(fp, fn);
            fit.Triangles = region;
            return fit;
        }

        private static List<int> Flood(TriMesh m, TriAdjacency adj, int seed, Func<int, bool> accept, int maxTris, int stopAt, JobContext job)
        {
            var region = new List<int> { seed };
            var seen = new HashSet<int> { seed };
            var queue = new Queue<int>();
            queue.Enqueue(seed);
            while (queue.Count > 0 && region.Count < Math.Min(maxTris, stopAt))
            {
                int t = queue.Dequeue();
                for (int k = 0; k < 3; k++)
                {
                    int n = adj.Neighbour[3 * t + k];
                    if (n < 0 || !seen.Add(n)) continue;
                    if (!accept(n)) continue;
                    region.Add(n);
                    queue.Enqueue(n);
                }
                if ((region.Count & 4095) == 0) job.Report("Growing region (" + region.Count + " triangles)", 0.5);
            }
            return region;
        }

        private static List<int> FloodSmooth(TriMesh m, TriAdjacency adj, int seed, double cosSmooth, int stopAt, JobContext job)
        {
            var region = new List<int> { seed };
            var seen = new HashSet<int> { seed };
            var queue = new Queue<int>();
            queue.Enqueue(seed);
            while (queue.Count > 0 && region.Count < stopAt)
            {
                int t = queue.Dequeue();
                var nt = SmoothNormal(m, adj, t);
                for (int k = 0; k < 3; k++)
                {
                    int n = adj.Neighbour[3 * t + k];
                    if (n < 0 || !seen.Add(n)) continue;
                    if (Math.Abs(Vec3.Dot(nt, SmoothNormal(m, adj, n))) < cosSmooth) continue;
                    region.Add(n);
                    queue.Enqueue(n);
                }
            }
            job.Check();
            return region;
        }

        /// <summary>
        /// Area-weighted normal over the triangle and its two rings of edge neighbours. Single-triangle normals
        /// on a fine, noisy scan scatter by tens of degrees (σ 0.1 mm at 0.5 mm spacing); this averages ~10.
        /// </summary>
        public static Vec3 SmoothNormal(TriMesh m, TriAdjacency adj, int t)
        {
            var sum = m.TriangleNormalRaw(t);
            for (int k = 0; k < 3; k++)
            {
                int n = adj.Neighbour[3 * t + k];
                if (n < 0) continue;
                sum += m.TriangleNormalRaw(n);
                for (int j = 0; j < 3; j++)
                {
                    int nn = adj.Neighbour[3 * n + j];
                    if (nn >= 0 && nn != t) sum += m.TriangleNormalRaw(nn);
                }
            }
            return sum.Normalized();
        }

        /// <summary>Remove `rings` boundary rings from a region, unless that would leave too little to fit.</summary>
        public static List<int> Erode(TriMesh m, TriAdjacency adj, List<int> region, int rings)
        {
            var inside = new HashSet<int>(region);
            var cur = region;
            for (int r = 0; r < rings; r++)
            {
                var next = new List<int>(cur.Count);
                foreach (int t in cur)
                {
                    bool boundary = false;
                    for (int k = 0; k < 3 && !boundary; k++)
                    {
                        int n = adj.Neighbour[3 * t + k];
                        boundary = n < 0 || !inside.Contains(n);
                    }
                    if (!boundary) next.Add(t);
                }
                if (next.Count < Math.Max(30, region.Count / 2)) return cur;
                inside = new HashSet<int>(next);
                cur = next;
            }
            return cur;
        }

        public static List<Vec3> VerticesOf(TriMesh m, List<int> tris)
        {
            var seen = new HashSet<int>();
            var pts = new List<Vec3>(tris.Count);
            foreach (int t in tris)
                for (int k = 0; k < 3; k++)
                {
                    int v = m.Indices[3 * t + k];
                    if (seen.Add(v)) pts.Add(m.Vertex(v));
                }
            return pts;
        }

        /// <summary>Unique vertices with area-weighted vertex normals (from the region's triangles).</summary>
        private static void PointsAndNormals(TriMesh m, List<int> tris, out List<Vec3> pts, out List<Vec3> nrm)
        {
            var index = new Dictionary<int, int>();
            pts = new List<Vec3>(tris.Count);
            nrm = new List<Vec3>(tris.Count);
            foreach (int t in tris)
            {
                var n = m.TriangleNormalRaw(t);
                for (int k = 0; k < 3; k++)
                {
                    int v = m.Indices[3 * t + k];
                    int i;
                    if (!index.TryGetValue(v, out i)) { i = pts.Count; index[v] = i; pts.Add(m.Vertex(v)); nrm.Add(Vec3.Zero); }
                    nrm[i] = nrm[i] + n;
                }
            }
            for (int i = 0; i < nrm.Count; i++) nrm[i] = nrm[i].Normalized();
        }
    }
}
