// One part with a scan attached: the core ScanSession (full-res mesh, alignment, fits) plus the SolidWorks
// side (display body, planes, sketches). Commands and the automation interface both go through here.
// Heavy work runs inside Progress.Run on a worker thread; SolidWorks calls happen after, on the UI thread.
using System;
using System.Collections.Generic;
using System.IO;
using System.Windows.Forms;
using ScanRE.AddIn.Sw;
using ScanRE.Core;
using ScanRE.Core.Align;
using ScanRE.Core.Cache;
using ScanRE.Core.Fit;
using ScanRE.Core.Jobs;
using ScanRE.Core.Math3;
using SolidWorks.Interop.sldworks;

namespace ScanRE.AddIn
{
    public sealed class DocSession
    {
        public const string AlignmentProperty = "ScanRE.Alignment";

        public readonly SwDoc Sw;
        public ScanSession Scan { get; private set; }
        public readonly IWin32Window Owner;

        public DocSession(SwDoc sw, IWin32Window owner) { Sw = sw; Owner = owner; }

        public bool HasScan { get { return Scan != null; } }
        public ScanState State { get { return Scan.State; } }

        private void RequireScan()
        {
            if (Scan == null)
                throw new ScanReException("No scan is attached to this part.", "Use Import Scan (or Re-attach Scan if this part had one before).");
        }

        // ------------------------------------------------------------------ import / re-attach / display

        public void Import(string scanPath, ImportOptions opt)
        {
            string previousDisplay = Scan != null ? State.ScanFeatureName : null;
            var sidecar = Sidecar.ForPart(Sw.PathName);
            var s = Progress.Run(Owner, "Importing scan", job =>
            {
                var sess = ScanSession.Import(scanPath, sidecar, opt, job);
                // alignment stored in the part but no sidecar (e.g. copied part): reuse it
                return sess;
            });
            string stored = Sw.GetLongProperty(AlignmentProperty);
            if (stored != null && s.State.Mates.Count == 0)
                try { s.State.ApplyAlignmentJson(stored); } catch (Exception e) { Log.Error("stored alignment", e); }
            Scan = s;
            if (State.ScanFeatureName == null) State.ScanFeatureName = previousDisplay;   // replace, don't orphan, the old display
            foreach (var kv in s.Timings) Log.Timing("import." + kv.Key + " (" + s.Full.TriangleCount + " tris)", kv.Value);
            RefreshDisplay(opt.DisplayTriangles, opt.DisplaySmoothing);
            SaveAll();
        }

        public bool TryReattach()
        {
            var sidecar = Sidecar.ForPart(Sw.PathName);
            if (!sidecar.Exists) return false;
            Scan = Progress.Run(Owner, "Re-attaching scan", job => ScanSession.Reattach(sidecar, job));
            RefreshDisplay(State.DisplayTriangles, 0);
            return true;
        }

        /// <summary>Re-attach from a sidecar folder the user picked (part was renamed or moved).</summary>
        public void ReattachFrom(string sidecarFolder)
        {
            var sc = new Sidecar(sidecarFolder);
            Scan = Progress.Run(Owner, "Re-attaching scan", job => ScanSession.Reattach(sc, job));
            RefreshDisplay(State.DisplayTriangles, 0);
        }

        /// <summary>(Re)write the display copy with the current alignment and swap the scan feature.</summary>
        public void RefreshDisplay(int triangles, int smoothing)
        {
            RequireScan();
            State.DisplayTriangles = triangles;
            string stl = Progress.Run(Owner, "Preparing display mesh", job => Scan.WriteDisplayStl(triangles, smoothing, job));
            long ms;
            var body = new SwScanBody(Sw);
            string old = State.ScanFeatureName != null && Sw.FindFeature(State.ScanFeatureName) != null ? State.ScanFeatureName : null;
            State.ScanFeatureName = body.Insert(stl, Path.GetFileNameWithoutExtension(State.SourcePath ?? "scan"), old, out ms);
            Log.Timing("sw.insert_graphics_body (" + triangles + " tris)", ms);
            Scan.Sidecar.PruneDisplayStls(stl);
            Sw.Redraw();
            Scan.Save();
        }

        public bool ScanHidden { get; private set; }

        public void SetScanVisible(bool visible)
        {
            RequireScan();
            // graphics bodies have no B-rep faces: find them as bodies and match their feature (review I5)
            foreach (var kv in Sw.MeshBodies())
                if (kv.Value != null && string.Equals(kv.Value.Name, State.ScanFeatureName, StringComparison.OrdinalIgnoreCase))
                    kv.Key.HideBody(!visible);
            ScanHidden = !visible;
            Sw.Redraw();
        }

        public void SaveAll()
        {
            if (Scan == null) return;
            Scan.Save();
            Sw.SetLongProperty(AlignmentProperty, State.AlignmentJson());
        }

        // ------------------------------------------------------------------ fitting

        public sealed class FitOptions
        {
            public GrowOptions Grow = new GrowOptions();
            public bool SnapSquare = true;
            public double SnapTolDeg = 1.0;
            public double CoaxialTolMm = 0.25;
            /// <summary>Create the SolidWorks plane/axis now (after alignment); during alignment fits are records only.</summary>
            public bool CreateFeature = true;
            public string NameHint = "";
        }

        public sealed class FitOutcome
        {
            public FitRecord Record;
            public string Feature;
            public string Report;
        }

        public FitOutcome FitPlaneAt(Ray3 ray, FitOptions opt)
        {
            RequireScan();
            PlaneFitResult part = null;
            var rec = Progress.Run(Owner, "Fitting plane", job => Scan.FitPlaneAt(ray, opt.Grow, job, out part));
            return FinishPlane(rec, part, opt);
        }

        public FitOutcome FitPlaneThrough(IList<Vec3> pts, Vec3 towardViewer, FitOptions opt)
        {
            RequireScan();
            PlaneFitResult part;
            var rec = Scan.FitPlaneThrough(pts, towardViewer, out part);
            return FinishPlane(rec, part, opt);
        }

        private FitOutcome FinishPlane(FitRecord rec, PlaneFitResult part, FitOptions opt)
        {
            var outcome = new FitOutcome { Record = rec };
            var plane = part.Plane;
            string snap = "";
            if (opt.CreateFeature && opt.SnapSquare)
            {
                var s = SnapToSquare.SnapDirection(plane.Normal, new SwPlanes(Sw).References(), opt.SnapTolDeg);
                if (s.Snapped) { plane = new Plane3(plane.Origin, s.Dir); snap = s.Describe(); }
            }
            rec.Snap = snap;
            outcome.Report = string.Format("{0} plane: {1}{2}", rec.Id, part.Summary(), snap.Length > 0 ? "; " + snap : "");
            if (opt.CreateFeature)
            {
                string name = string.Format("Plane {0}{1} (rms {2:0.000} max {3:0.000})", rec.Id, opt.NameHint.Length > 0 ? " " + opt.NameHint : "", part.Rms, part.MaxDev);
                string desc = "ScanRE fit: " + part.Summary() + (snap.Length > 0 ? "; snapped " + snap : "") + "; source " + Path.GetFileName(State.SourcePath) + "; " + rec.Created;
                var f = new SwPlanes(Sw).CreatePlane(name, plane, desc);
                rec.FeatureName = outcome.Feature = f.Name;
            }
            Scan.Save();
            Log.Info(outcome.Report);
            return outcome;
        }

        public FitOutcome FitCylinderAt(Ray3 ray, FitOptions opt)
        {
            RequireScan();
            CylinderFitResult part = null;
            var rec = Progress.Run(Owner, "Fitting cylinder", job => Scan.FitCylinderAt(ray, opt.Grow, job, out part));
            var outcome = new FitOutcome { Record = rec };
            var dir = part.AxisDir;
            var point = part.AxisPoint;
            string snap = "";
            if (opt.SnapSquare)
            {
                var refs = new SwPlanes(Sw).References();
                var s = SnapToSquare.SnapDirection(dir, refs, opt.SnapTolDeg);
                if (s.Snapped)
                {
                    dir = s.Dir; snap = s.Describe();
                    Vec3 p2; string rel;
                    if (SnapToSquare.SnapCoaxial(point, dir, refs, opt.CoaxialTolMm, out p2, out rel)) { point = p2; snap += ", " + rel; }
                }
            }
            rec.Snap = snap;
            outcome.Report = string.Format("{0} {1}: {2}{3}", rec.Id, part.Convex ? "boss" : "hole", part.Summary(), snap.Length > 0 ? "; " + snap : "");
            string name = string.Format("Axis {0} D{1:0.000} (rms {2:0.000})", rec.Id, 2 * part.Radius, part.Rms);
            var mid = point + dir * (0.5 * (part.MinT + part.MaxT));
            var f = new SwPlanes(Sw).CreateAxis(name, mid, dir, part.MaxT - part.MinT,
                "ScanRE fit: " + (part.Convex ? "boss/shaft " : "hole ") + part.Summary() + (snap.Length > 0 ? "; snapped " + snap : "") + "; " + rec.Created);
            rec.FeatureName = outcome.Feature = f.Name;
            Scan.Save();
            Log.Info(outcome.Report);
            return outcome;
        }

        // ------------------------------------------------------------------ alignment

        public AlignResult Solve()
        {
            RequireScan();
            var r = Scan.SolveAlignment();
            foreach (var n in r.Notes) Log.Info("align: " + n);
            for (int i = 0; i < r.AngleErrorDeg.Count; i++)
                Log.Info(string.Format("align residual {0}: {1:0.####} deg, {2:0.####} mm", i, r.AngleErrorDeg[i], r.PositionErrorMm[i]));
            return r;
        }

        /// <summary>Solve, move the display, and (if the alignment was accepted before) update the Scan-* planes.</summary>
        public AlignResult SolveAndShow(int coarseTriangles)
        {
            var r = Solve();
            RefreshDisplay(coarseTriangles, 0);
            if (State.AlignmentAccepted) UpdateScanPlanes();
            SaveAll();
            return r;
        }

        public void Accept()
        {
            RequireScan();
            Solve();
            UpdateScanPlanes();
            State.AlignmentAccepted = true;
            SaveAll();
        }

        private void UpdateScanPlanes()
        {
            var planes = new SwPlanes(Sw);
            foreach (var kv in Scan.ScanPlanes())
            {
                string key = kv.Key.ToString();
                string name;
                string desc = "ScanRE: actual position of the face mated to " + BasePlanes.SwName(kv.Key) + " after alignment (" + ScanState.Now() + ")";
                if (State.ScanPlanes.TryGetValue(key, out name) && Sw.FindFeature(name) != null)
                {
                    if (!planes.UpdatePlane(name, kv.Value))
                        Log.Info("could not update " + name + " in place (it is not a ScanRE definition-sketch plane)");
                    var f = Sw.FindFeature(name);
                    if (f != null) f.Description = desc;
                }
                else
                {
                    // Scan-* planes always use a definition sketch, so a later re-solve can move them in place
                    var f = planes.CreatePlane("Scan-" + key, kv.Value, desc, true);
                    State.ScanPlanes[key] = f.Name;
                }
            }
            Sw.Rebuild();
        }

        // ------------------------------------------------------------------ check / finish

        public List<ScanReference> CheckReferences()
        {
            var scans = new SwScanBody(Sw).Find(Scan != null ? State.ScanFeatureName : null);
            return new SwRefs(Sw).Find(scans);
        }

        /// <summary>Delete the scan feature(s), temporary sketches and optionally the cache. Returns what was removed.</summary>
        public List<string> Finish(bool deleteSidecar, bool keepAlignmentProperty)
        {
            var removed = new List<string>();
            UI.ToolWindow.CloseAll(this);
            new SwSketch(Sw).RemoveSection();
            var body = new SwScanBody(Sw);
            foreach (var f in body.Find(Scan != null ? State.ScanFeatureName : null))
            {
                string n = f.Name;
                if (Sw.Delete(f)) removed.Add(n);
            }
            Sw.DeleteFolderIfEmpty(SwScanBody.Folder);
            if (Scan != null)
            {
                State.ScanFeatureName = null;
                SaveAll();
                if (deleteSidecar) { Scan.Sidecar.Delete(); removed.Add(Scan.Sidecar.Folder); }
            }
            if (!keepAlignmentProperty) Sw.DeleteLongProperty(AlignmentProperty);
            Scan = null;
            Sw.Rebuild();
            return removed;
        }

        public void Close()
        {
            try { if (Scan != null) Scan.Save(); } catch (Exception e) { Log.Error("save on close", e); }
            Scan = null;
        }
    }
}
