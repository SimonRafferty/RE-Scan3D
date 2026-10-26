// One open scan attached to one part: the full-resolution mesh + index held by the add-in, the alignment,
// and the operations the commands call. Everything here is SolidWorks-free and runs on a worker thread;
// all inputs and outputs are in PART coordinates (mm) unless named "Scan".
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using RES3D.Core.Align;
using RES3D.Core.Cache;
using RES3D.Core.Decimate;
using RES3D.Core.Fit;
using RES3D.Core.Jobs;
using RES3D.Core.Math3;
using RES3D.Core.Mesh;
using RES3D.Core.Section;
using RES3D.Core.Spatial;

namespace RES3D.Core
{
    public sealed class ImportOptions
    {
        public double UnitScale = 1;
        /// <summary>Drop islands smaller than this fraction of the largest component (0 = keep all).</summary>
        public double IslandFraction = 0.01;
        public int DisplayTriangles = 60000;
        /// <summary>Taubin smoothing iterations for the display copy only.</summary>
        public int DisplaySmoothing = 0;
    }

    public sealed class ScanSession
    {
        public Sidecar Sidecar { get; private set; }
        public ScanState State { get; private set; }
        public TriMesh Full { get; private set; }
        public Bvh Index { get; private set; }
        /// <summary>Timings of the last load (stage -> ms), recorded for INVESTIGATION.md.</summary>
        public readonly Dictionary<string, long> Timings = new Dictionary<string, long>();

        private TriAdjacency adjacency;
        private Vec3 centroid;

        public Rigid ScanToPart { get { return State.ScanToPart(); } }

        public Vec3 CentroidScan { get { return centroid; } }

        // ------------------------------------------------------------------ load

        /// <summary>Import a scan for a part: reuse the sidecar cache when it matches the source, else process the file.</summary>
        public static ScanSession Import(string sourcePath, Sidecar sidecar, ImportOptions opt, JobContext job)
        {
            var s = new ScanSession { Sidecar = sidecar };
            var sw = Stopwatch.StartNew();
            var old = sidecar.Exists ? TryLoadState(sidecar) : null;
            long size = new FileInfo(sourcePath).Length;
            string stamp = Sidecar.Fingerprint(sourcePath);
            bool cacheOk = old != null && old.SourceSize == size && old.SourceStamp == stamp &&
                           Math.Abs(old.UnitScale - opt.UnitScale) < 1e-12 && Math.Abs(old.IslandFraction - opt.IslandFraction) < 1e-12 &&
                           File.Exists(sidecar.BvhPath);
            if (cacheOk)
            {
                s.State = old;
                s.LoadCached(job);
            }
            else
            {
                s.State = old ?? new ScanState();   // keep alignment/fits if only the processing options changed
                s.State.SourcePath = Path.GetFullPath(sourcePath);
                s.State.SourceSize = size;
                s.State.SourceStamp = stamp;
                s.State.UnitScale = opt.UnitScale;
                s.State.IslandFraction = opt.IslandFraction;
                var t = Stopwatch.StartNew();
                var m = MeshIO.Read(sourcePath, job);
                s.Timings["read"] = t.ElapsedMilliseconds;
                if (m.TriangleCount == 0)
                    throw new Res3dException("The scan contains no triangles.", "Check the export settings in the scanner software.");
                if (Math.Abs(opt.UnitScale - 1) > 1e-12)
                    for (int i = 0; i < m.Positions.Length; i++) m.Positions[i] = (float)(m.Positions[i] * opt.UnitScale);
                t.Restart();
                int removed;
                m = Topology.RemoveIslands(m, opt.IslandFraction, job, out removed);
                s.Timings["islands"] = t.ElapsedMilliseconds;
                s.Timings["islands_removed_tris"] = removed;
                s.Full = m;
                t.Restart();
                s.Index = Bvh.Build(m, job);
                s.Timings["bvh"] = t.ElapsedMilliseconds;
                job.Report("Saving cache", 0.95, true);
                sidecar.EnsureFolder();
                foreach (var f in Directory.GetFiles(sidecar.Folder, "display_*.bin")) File.Delete(f);   // stale levels
                Sidecar.WriteMesh(sidecar.MeshPath, m);
                sidecar.WriteBvh(s.Index);
            }
            s.State.DisplayTriangles = opt.DisplayTriangles;
            s.ComputeCentroid();
            sidecar.SaveState(s.State);
            s.Timings["total"] = sw.ElapsedMilliseconds;
            return s;
        }

        /// <summary>Re-attach from the sidecar alone (the source scan file is not needed).</summary>
        public static ScanSession Reattach(Sidecar sidecar, JobContext job)
        {
            if (!sidecar.Exists)
                throw new Res3dException("No scan cache found at " + sidecar.Folder + ".",
                    "Use Import Scan to load the scan file again. If you have the part's alignment stored, it will be reused.");
            var s = new ScanSession { Sidecar = sidecar, State = sidecar.LoadState() };
            var sw = Stopwatch.StartNew();
            s.LoadCached(job);
            s.ComputeCentroid();
            s.Timings["total"] = sw.ElapsedMilliseconds;
            return s;
        }

        private static ScanState TryLoadState(Sidecar sc)
        {
            try { return sc.LoadState(); } catch (Exception) { return null; }
        }

        private void LoadCached(JobContext job)
        {
            var t = Stopwatch.StartNew();
            job.Report("Loading cached scan", 0.1, true);
            Full = Sidecar.ReadMesh(Sidecar.MeshPath);
            Timings["cache_mesh"] = t.ElapsedMilliseconds;
            t.Restart();
            job.Report("Loading cached index", 0.5, true);
            try { Index = Sidecar.ReadBvh(Full); }
            catch (Exception) { Index = Bvh.Build(Full, job); Sidecar.WriteBvh(Index); }
            Timings["cache_bvh"] = t.ElapsedMilliseconds;
        }

        private void ComputeCentroid()
        {
            Vec3 lo, hi;
            Full.Bounds(out lo, out hi);
            centroid = (lo + hi) * 0.5;
        }

        public TriAdjacency Adjacency(JobContext job)
        {
            if (adjacency == null) adjacency = new TriAdjacency(Full, job);
            return adjacency;
        }

        public void Save() { Sidecar.SaveState(State); }

        // ------------------------------------------------------------------ display copy

        /// <summary>
        /// The decimated display mesh in PART coordinates, written as binary STL for SolidWorks. Decimated
        /// levels are cached (scan coordinates), so re-alignment only re-transforms and re-writes.
        /// </summary>
        public string WriteDisplayStl(int targetTriangles, int smoothing, JobContext job)
        {
            var t = Stopwatch.StartNew();
            TriMesh level;
            string levelPath = Sidecar.DisplayLevelPath(targetTriangles);
            if (targetTriangles >= Full.TriangleCount) level = Full;
            else if (File.Exists(levelPath)) level = Sidecar.ReadMesh(levelPath);
            else
            {
                level = Qem.Decimate(Full, new QemOptions { TargetTriangles = targetTriangles }, job);
                Sidecar.WriteMesh(levelPath, level);
            }
            Timings["decimate"] = t.ElapsedMilliseconds;
            if (smoothing > 0) level = Topology.SmoothForDisplay(level, smoothing, job);
            t.Restart();
            var display = level.Transformed(ScanToPart);
            string path = Sidecar.DisplayStlPath(DateTime.Now.ToString("HHmmssfff"));
            MeshIO.WriteStlBinary(path, display, job);
            Timings["write_display"] = t.ElapsedMilliseconds;
            return path;
        }

        // ------------------------------------------------------------------ picking

        /// <summary>Ray (part coordinates) against the full-res mesh. Returns the hit in part coordinates.</summary>
        /// <summary>Vertices of a fitted region in part coordinates (at most `max`, evenly sampled), for display.</summary>
        public List<Vec3> RegionPoints(List<int> triangles, int max = 4000)
        {
            var res = new List<Vec3>();
            if (triangles == null || triangles.Count == 0) return res;
            var x = ScanToPart;
            int step = Math.Max(1, triangles.Count / max);
            for (int i = 0; i < triangles.Count; i += step)
                res.Add(x.Apply(Full.Vertex(Full.Indices[3 * triangles[i]])));
            return res;
        }

        public RayHit Pick(Ray3 partRay)
        {
            var toScan = ScanToPart.Inverse();
            var hit = Index.Raycast(partRay.Transformed(toScan));
            if (hit.Hit) hit.Point = ScanToPart.Apply(hit.Point);
            return hit;
        }

        // ------------------------------------------------------------------ fitting

        /// <summary>Region-grow + robust plane at the picked point. Result plane in PART coordinates.</summary>
        public FitRecord FitPlaneAt(Ray3 partRay, GrowOptions opt, JobContext job, out PlaneFitResult partFit)
        {
            return RecordPlane(GrowPlaneAt(partRay, opt, job), "region", out partFit);
        }

        /// <summary>Region-grow + robust plane at the picked point, NOT recorded. Result in SCAN coordinates.</summary>
        public PlaneFitResult GrowPlaneAt(Ray3 partRay, GrowOptions opt, JobContext job)
        {
            var hit = Pick(partRay);
            if (!hit.Hit) throw NoHit();
            var fit = RegionGrow.GrowPlane(Full, Adjacency(job), hit.Triangle, opt, job);
            if (fit.Triangles.Count < 3 || fit.Points < 4)
                throw new Res3dException("Only " + fit.Triangles.Count + " triangles matched the picked face.",
                    "The surface may be curved or noisy here: raise the distance tolerance, or use 3+ manual points.");
            return fit;
        }

        /// <summary>Robust plane through 3+ picked points (part coordinates), NOT recorded. Result in SCAN coordinates.</summary>
        public PlaneFitResult PlaneThrough(IList<Vec3> partPoints, Vec3 towardViewer)
        {
            if (partPoints.Count < 3)
                throw new Res3dException("A plane needs at least 3 points.", "Pick more points on the face.");
            var toScan = ScanToPart.Inverse();
            var pts = new List<Vec3>();
            foreach (var p in partPoints) pts.Add(toScan.Apply(p));
            return PlaneFit.Robust(pts, toScan.ApplyDir(towardViewer), partPoints.Count > 6 ? 12 : 0);
        }

        /// <summary>Plane through 3+ picked points (part coordinates).</summary>
        public FitRecord FitPlaneThrough(IList<Vec3> partPoints, Vec3 towardViewer, out PlaneFitResult partFit)
        {
            return RecordPlane(PlaneThrough(partPoints, towardViewer), "points", out partFit);
        }

        /// <summary>Store a plane fit (scan coordinates) as a fit record; partFit is the same fit in part coordinates.</summary>
        public FitRecord RecordPlane(PlaneFitResult scanFit, string method, out PlaneFitResult partFit)
        {
            var rec = new FitRecord
            {
                Id = State.NewFitId(), Kind = "plane", Origin = FitRecord.A(scanFit.Plane.Origin), Direction = FitRecord.A(scanFit.Plane.Normal),
                Rms = scanFit.Rms, MaxDev = scanFit.MaxDev, Points = scanFit.Points, Outliers = scanFit.Outliers,
                Method = method, Snap = "", FeatureName = "", Created = ScanState.Now()
            };
            State.Fits.Add(rec);
            partFit = new PlaneFitResult
            {
                Plane = scanFit.Plane.Transformed(ScanToPart), Rms = scanFit.Rms, MaxDev = scanFit.MaxDev,
                Points = scanFit.Points, Outliers = scanFit.Outliers, Triangles = scanFit.Triangles
            };
            return rec;
        }

        public FitRecord FitCylinderAt(Ray3 partRay, GrowOptions opt, JobContext job, out CylinderFitResult partFit)
        {
            var hit = Pick(partRay);
            if (!hit.Hit) throw NoHit();
            var fit = RegionGrow.GrowCylinder(Full, Adjacency(job), hit.Triangle, opt, job);
            var rec = new FitRecord
            {
                Id = State.NewFitId(), Kind = "cylinder", Origin = FitRecord.A(fit.AxisPoint), Direction = FitRecord.A(fit.AxisDir),
                Radius = fit.Radius, Rms = fit.Rms, MaxDev = fit.MaxDev, Points = fit.Points, Outliers = fit.Outliers,
                Method = "region", Snap = "", FeatureName = "", Created = ScanState.Now()
            };
            State.Fits.Add(rec);
            var x = ScanToPart;
            partFit = new CylinderFitResult
            {
                AxisPoint = x.Apply(fit.AxisPoint), AxisDir = x.ApplyDir(fit.AxisDir), Radius = fit.Radius, Rms = fit.Rms, MaxDev = fit.MaxDev,
                Points = fit.Points, Outliers = fit.Outliers, MinT = fit.MinT, MaxT = fit.MaxT, Convex = fit.Convex, Triangles = fit.Triangles
            };
            return rec;
        }

        /// <summary>Full-res triangles of a region in PART coordinates (for the highlight overlay).</summary>
        public TriMesh RegionMesh(List<int> tris)
        {
            return Full.Subset(tris).Transformed(ScanToPart);
        }

        private static Res3dException NoHit()
        {
            return new Res3dException("Nothing on the scan under the cursor.", "Click directly on the scan surface.");
        }

        // ------------------------------------------------------------------ sections

        public PlaneSection Section(Plane3 partPlane, SectionOptions opt, JobContext job)
        {
            var sec = PlaneSection.Compute(Index, ScanToPart, partPlane, opt, job);
            if (sec.Lines.Count == 0)
                throw new Res3dException("The sketch plane does not cross the scan.",
                    "Choose a plane that passes through the scan, or check the alignment.");
            return sec;
        }

        // ------------------------------------------------------------------ alignment

        public AlignResult SolveAlignment()
        {
            var faces = new Dictionary<string, Plane3>();
            foreach (var f in State.Fits) if (f.Kind == "plane") faces[f.Id] = f.PlaneScan();
            // solve from the identity so the result depends only on the mates, not on the solve history
            var res = AlignSolver.Solve(State.Mates, faces, Rigid.Identity, State.Solver, centroid);
            State.Transform = res.Transform.ToArray();
            return res;
        }

        /// <summary>
        /// The Scan-* planes (spec §2): for each base plane with an active mate, the first mated face's actual
        /// position after alignment, in part coordinates, with its normal oriented like the base plane's.
        /// </summary>
        public Dictionary<BasePlane, Plane3> ScanPlanes()
        {
            var result = new Dictionary<BasePlane, Plane3>();
            foreach (var m in State.Mates)
            {
                if (m.Suppressed || result.ContainsKey(m.Target)) continue;
                var f = State.Fit(m.FitId);
                if (f == null || f.Kind != "plane") continue;
                var p = f.PlaneScan().Transformed(ScanToPart);
                var N = BasePlanes.Normal(m.Target);
                if (Vec3.Dot(p.Normal, N) < 0) p.Normal = -p.Normal;
                result[m.Target] = p;
            }
            return result;
        }

        public void SetNudge(Vec3 rotDeg, Vec3 translate)
        {
            // rotations about the part X, Y, Z axes through the scan's solved centroid, then the translation
            var R = Mat3.Rotation(Vec3.UnitZ, rotDeg.Z * Math.PI / 180) * Mat3.Rotation(Vec3.UnitY, rotDeg.Y * Math.PI / 180) *
                    Mat3.Rotation(Vec3.UnitX, rotDeg.X * Math.PI / 180);
            var pivot = Rigid.FromArray(State.Transform).Apply(centroid);
            State.Nudge = new Rigid(R, pivot - R * pivot + translate).ToArray();
        }
    }
}
