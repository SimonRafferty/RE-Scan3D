// Full-resolution plane sections (spec §4): slice the scan by the sketch plane, chain the segments into
// polylines, optionally combine a thin slab of parallel sections to beat scanner noise, and snap a cursor
// to the nearest point on the result.
using System;
using System.Collections.Generic;
using RES3D.Core.Jobs;
using RES3D.Core.Math3;
using RES3D.Core.Mesh;
using RES3D.Core.Spatial;

namespace RES3D.Core.Section
{
    public sealed class Polyline
    {
        public readonly List<Vec3> Points = new List<Vec3>();
        public bool Closed;

        public int SegmentCount { get { return Closed ? Points.Count : Points.Count - 1; } }
        public Vec3 SegA(int s) { return Points[s]; }
        public Vec3 SegB(int s) { return Points[(s + 1) % Points.Count]; }

        public double Length
        {
            get
            {
                double l = 0;
                for (int s = 0; s < SegmentCount; s++) l += (SegB(s) - SegA(s)).Length;
                return l;
            }
        }

        public Polyline Transformed(Rigid t)
        {
            var p = new Polyline { Closed = Closed };
            foreach (var q in Points) p.Points.Add(t.Apply(q));
            return p;
        }
    }

    /// <summary>A location on a section: polyline index, segment index and parameter 0..1 along it.</summary>
    public struct SectionPoint
    {
        public int Line, Segment;
        public double Param;
        public Vec3 Point;
        public double Distance;
        public bool Valid { get { return Line >= 0; } }
        public static readonly SectionPoint None = new SectionPoint { Line = -1, Distance = double.PositiveInfinity };
        /// <summary>Position along the polyline as segment + param (monotonic on open polylines).</summary>
        public double Arc { get { return Segment + Param; } }
    }

    public sealed class SectionOptions
    {
        /// <summary>Half-thickness of the slab (mm); 0 = single section.</summary>
        public double SlabHalfThickness = 0;
        /// <summary>Number of extra layers each side of the plane in slab mode.</summary>
        public int SlabLayersEachSide = 2;
        /// <summary>Drop polylines shorter than this (mm): scan crumbs.</summary>
        public double MinLength = 0.5;
    }

    public sealed class PlaneSection
    {
        /// <summary>The section plane in PART coordinates.</summary>
        public Plane3 Plane;
        /// <summary>The section polylines in PART coordinates (denoised when a slab was used).</summary>
        public readonly List<Polyline> Lines = new List<Polyline>();
        private SegmentGrid grid;

        public static PlaneSection Compute(Bvh bvh, Rigid scanToPart, Plane3 partPlane, SectionOptions opt, JobContext job)
        {
            var toScan = scanToPart.Inverse();
            var scanPlane = partPlane.Transformed(toScan);
            var sec = new PlaneSection { Plane = partPlane };
            var centre = Slice(bvh, scanPlane, job);
            if (opt.SlabHalfThickness > 0 && opt.SlabLayersEachSide > 0)
            {
                var layers = new List<List<Polyline>>();
                int n = opt.SlabLayersEachSide;
                for (int i = -n; i <= n; i++)
                {
                    if (i == 0) continue;
                    double off = opt.SlabHalfThickness * i / n;
                    var pl = new Plane3(scanPlane.Origin + scanPlane.Normal * off, scanPlane.Normal);
                    layers.Add(Slice(bvh, pl, job));
                }
                centre = Slab.Denoise(centre, layers, scanPlane, 2 * opt.SlabHalfThickness + bvh.Mesh.MeanEdgeLength(), job);
            }
            foreach (var pl in centre)
            {
                if (pl.Length < opt.MinLength) continue;
                var p = pl.Transformed(scanToPart);
                // project exactly onto the plane: sketch points must lie on the sketch plane
                for (int i = 0; i < p.Points.Count; i++) p.Points[i] = partPlane.Project(p.Points[i]);
                sec.Lines.Add(p);
            }
            sec.grid = new SegmentGrid(sec);
            return sec;
        }

        /// <summary>A section from existing polylines (already on `plane`), with its snapping grid.</summary>
        public static PlaneSection FromLines(Plane3 plane, IEnumerable<Polyline> lines)
        {
            var sec = new PlaneSection { Plane = plane };
            sec.Lines.AddRange(lines);
            sec.grid = new SegmentGrid(sec);
            return sec;
        }

        /// <summary>Raw slice of the mesh by a plane (scan coordinates), chained into polylines.</summary>
        public static List<Polyline> Slice(Bvh bvh, Plane3 pl, JobContext job)
        {
            var m = bvh.Mesh;
            var cand = bvh.TrianglesNearPlane(pl, 0);
            double eps = 1e-7 * Math.Max(1, m.MeanEdgeLength(2000));
            // each crossing point is identified by the mesh edge it lies on, so neighbouring triangles' segments join
            var segs = new List<long[]>();       // edge keys of the two endpoints
            var pointOf = new Dictionary<long, Vec3>();
            int done = 0;
            foreach (int t in cand)
            {
                if ((++done & 4095) == 0) job.Report("Sectioning", (double)done / cand.Count);
                int[] v = { m.Indices[3 * t], m.Indices[3 * t + 1], m.Indices[3 * t + 2] };
                var d = new double[3];
                for (int k = 0; k < 3; k++)
                {
                    d[k] = pl.SignedDistance(m.Vertex(v[k]));
                    if (Math.Abs(d[k]) < eps) d[k] = eps;   // a vertex on the plane counts as above: no zero-length cases
                }
                var keys = new List<long>(2);
                for (int k = 0; k < 3; k++)
                {
                    int a = v[k], b = v[(k + 1) % 3];
                    double da = d[k], db = d[(k + 1) % 3];
                    if ((da > 0) == (db > 0)) continue;
                    long key = a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;
                    if (!pointOf.ContainsKey(key))
                    {
                        double s = da / (da - db);
                        pointOf[key] = m.Vertex(a) + (m.Vertex(b) - m.Vertex(a)) * s;
                    }
                    keys.Add(key);
                }
                if (keys.Count == 2) segs.Add(new[] { keys[0], keys[1] });
            }
            return Chain(segs, pointOf);
        }

        private static List<Polyline> Chain(List<long[]> segs, Dictionary<long, Vec3> pointOf)
        {
            // adjacency key -> segments using it (2 on a manifold mesh, 1 at an open boundary)
            var uses = new Dictionary<long, List<int>>();
            for (int i = 0; i < segs.Count; i++)
                foreach (var k in segs[i])
                {
                    List<int> l;
                    if (!uses.TryGetValue(k, out l)) { l = new List<int>(2); uses[k] = l; }
                    l.Add(i);
                }
            var used = new bool[segs.Count];
            var lines = new List<Polyline>();
            // start open chains at their ends first so they are not split in the middle
            var order = new List<int>();
            foreach (var kv in uses) if (kv.Value.Count == 1) order.Add(kv.Value[0]);
            for (int i = 0; i < segs.Count; i++) order.Add(i);
            foreach (int s0 in order)
            {
                if (used[s0]) continue;
                used[s0] = true;
                // pick the starting key: an end key (used once) if there is one
                long start = uses[segs[s0][0]].Count == 1 ? segs[s0][0] : uses[segs[s0][1]].Count == 1 ? segs[s0][1] : segs[s0][0];
                long cur = segs[s0][0] == start ? segs[s0][1] : segs[s0][0];
                var keys = new List<long> { start, cur };
                while (true)
                {
                    int next = -1;
                    foreach (int s in uses[cur]) if (!used[s]) { next = s; break; }
                    if (next < 0) break;
                    used[next] = true;
                    cur = segs[next][0] == cur ? segs[next][1] : segs[next][0];
                    keys.Add(cur);
                }
                var pl = new Polyline();
                if (keys.Count > 2 && keys[keys.Count - 1] == keys[0]) { pl.Closed = true; keys.RemoveAt(keys.Count - 1); }
                foreach (var k in keys) pl.Points.Add(pointOf[k]);
                // a chain started mid-way on an open curve: extend backwards from the start key
                if (!pl.Closed)
                {
                    var back = new List<Vec3>();
                    long bk = start;
                    while (true)
                    {
                        int next = -1;
                        foreach (int s in uses[bk]) if (!used[s]) { next = s; break; }
                        if (next < 0) break;
                        used[next] = true;
                        bk = segs[next][0] == bk ? segs[next][1] : segs[next][0];
                        back.Add(pointOf[bk]);
                    }
                    if (back.Count > 0) { back.Reverse(); pl.Points.InsertRange(0, back); }
                }
                if (pl.Points.Count >= 2) lines.Add(pl);
            }
            return lines;
        }

        // ------------------------------------------------------------------ snapping

        /// <summary>Nearest point on the section to `p` (part coordinates), within maxDist.</summary>
        public SectionPoint Nearest(Vec3 p, double maxDist)
        {
            return grid.Nearest(this, Plane.Project(p), maxDist);
        }

        public SectionPoint NearestOnLine(int line, Vec3 p)
        {
            var best = SectionPoint.None;
            var pl = Lines[line];
            for (int s = 0; s < pl.SegmentCount; s++) Consider(line, s, p, ref best);
            return best;
        }

        internal void Consider(int line, int seg, Vec3 p, ref SectionPoint best)
        {
            var pl = Lines[line];
            var a = pl.SegA(seg); var b = pl.SegB(seg);
            var ab = b - a;
            double len2 = ab.LengthSq;
            double t = len2 > 0 ? Math.Max(0, Math.Min(1, Vec3.Dot(p - a, ab) / len2)) : 0;
            var q = a + ab * t;
            double d = (p - q).Length;
            if (d < best.Distance) best = new SectionPoint { Line = line, Segment = seg, Param = t, Point = q, Distance = d };
        }

        public int SegmentTotal
        {
            get { int n = 0; foreach (var l in Lines) n += l.SegmentCount; return n; }
        }
    }

    /// <summary>Uniform 2D grid over section segments in the plane's (u,v) basis.</summary>
    internal sealed class SegmentGrid
    {
        private readonly Vec3 origin, u, v;
        private readonly double cell;
        private readonly Dictionary<long, List<int[]>> cells = new Dictionary<long, List<int[]>>();

        public SegmentGrid(PlaneSection sec)
        {
            origin = sec.Plane.Origin;
            sec.Plane.Basis(out u, out v);
            double total = 0; int n = 0;
            foreach (var l in sec.Lines) for (int s = 0; s < l.SegmentCount; s++) { total += (l.SegB(s) - l.SegA(s)).Length; n++; }
            cell = Math.Max(1e-3, n > 0 ? 4 * total / n : 1);
            for (int li = 0; li < sec.Lines.Count; li++)
            {
                var l = sec.Lines[li];
                for (int s = 0; s < l.SegmentCount; s++)
                {
                    double ax, ay, bx, by;
                    To2D(l.SegA(s), out ax, out ay); To2D(l.SegB(s), out bx, out by);
                    int x0 = (int)Math.Floor(Math.Min(ax, bx) / cell), x1 = (int)Math.Floor(Math.Max(ax, bx) / cell);
                    int y0 = (int)Math.Floor(Math.Min(ay, by) / cell), y1 = (int)Math.Floor(Math.Max(ay, by) / cell);
                    for (int x = x0; x <= x1; x++)
                        for (int y = y0; y <= y1; y++)
                        {
                            long k = Key(x, y);
                            List<int[]> list;
                            if (!cells.TryGetValue(k, out list)) { list = new List<int[]>(); cells[k] = list; }
                            list.Add(new[] { li, s });
                        }
                }
            }
        }

        private void To2D(Vec3 p, out double x, out double y) { var d = p - origin; x = Vec3.Dot(d, u); y = Vec3.Dot(d, v); }
        private static long Key(int x, int y) { return ((long)x << 32) ^ (uint)y; }

        public SectionPoint Nearest(PlaneSection sec, Vec3 p, double maxDist)
        {
            double px, py;
            To2D(p, out px, out py);
            int cx = (int)Math.Floor(px / cell), cy = (int)Math.Floor(py / cell);
            var best = SectionPoint.None;
            int maxRing = (int)Math.Ceiling(Math.Min(maxDist, 1e7) / cell) + 1;
            for (int r = 0; r <= maxRing; r++)
            {
                // once something is found, one more ring guarantees it is the true nearest
                if (best.Valid && (r - 1) * cell > best.Distance) break;
                for (int x = cx - r; x <= cx + r; x++)
                    for (int y = cy - r; y <= cy + r; y++)
                    {
                        if (Math.Max(Math.Abs(x - cx), Math.Abs(y - cy)) != r) continue;
                        List<int[]> list;
                        if (!cells.TryGetValue(Key(x, y), out list)) continue;
                        foreach (var e in list) sec.Consider(e[0], e[1], p, ref best);
                    }
                if (r > 64 && !best.Valid && cells.Count < 4 * r * r) break;   // far from everything
            }
            return best.Distance <= maxDist ? best : SectionPoint.None;
        }
    }

    public static class Slab
    {
        /// <summary>
        /// Replace each point of the central section by the mean of itself and the nearest point of each
        /// neighbouring layer (projected onto the central plane) within `radius`. Averaging 2N+1 independent
        /// sections reduces random scanner noise by about sqrt(2N+1) without moving true edges much.
        /// </summary>
        public static List<Polyline> Denoise(List<Polyline> centre, List<List<Polyline>> layers, Plane3 pl, double radius, JobContext job)
        {
            var projected = new List<PlaneSection>();
            foreach (var layer in layers)
            {
                var ps = new PlaneSection { Plane = pl };
                foreach (var l in layer)
                {
                    var q = new Polyline { Closed = l.Closed };
                    foreach (var p in l.Points) q.Points.Add(pl.Project(p));
                    ps.Lines.Add(q);
                }
                projected.Add(PlaneSection.FromLines(pl, ps.Lines));
            }
            var result = new List<Polyline>();
            foreach (var l in centre)
            {
                var o = new Polyline { Closed = l.Closed };
                foreach (var p0 in l.Points)
                {
                    job.Check();
                    var sum = p0; int n = 1;
                    foreach (var ps in projected)
                    {
                        var hit = ps.Nearest(p0, radius);
                        if (hit.Valid) { sum += hit.Point; n++; }
                    }
                    o.Points.Add(sum / n);
                }
                result.Add(o);
            }
            return result;
        }
    }
}
