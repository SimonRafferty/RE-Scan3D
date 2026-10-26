// One part with a scan attached: the core ScanSession (full-res mesh, alignment, fits) plus the SolidWorks
// side (display body, planes, sketches). Commands and the automation interface both go through here.
// Heavy work runs inside Progress.Run on a worker thread; SolidWorks calls happen after, on the UI thread.
using System;
using System.Collections.Generic;
using System.IO;
using System.Windows.Forms;
using RES3D.AddIn.Sw;
using RES3D.Core;
using RES3D.Core.Align;
using RES3D.Core.Cache;
using RES3D.Core.Fit;
using RES3D.Core.Jobs;
using RES3D.Core.Math3;
using SolidWorks.Interop.sldworks;

namespace RES3D.AddIn
{
    public sealed class DocSession
    {
        public const string AlignmentProperty = "RES3D.Alignment";
        private const string LegacyAlignmentProperty = "ScanRE.Alignment";   // before the rename to RE-Scan3D

        public readonly SwDoc Sw;
        public ScanSession Scan { get; private set; }
        public readonly IWin32Window Owner;

        public DocSession(SwDoc sw, IWin32Window owner) { Sw = sw; Owner = owner; }

        public bool HasScan { get { return Scan != null; } }

        /// <summary>The section most recently computed (Section and Snap or scripting), and the sketch it is for:
        /// a reopened Section and Snap window traces on it without computing again (user test 2026-10-26).</summary>
        public Core.Section.PlaneSection LastSection;
        public string LastSectionSketch;
        public ScanState State { get { return Scan.State; } }

        private void RequireScan()
        {
            if (Scan == null)
                throw new Res3dException("No scan is attached to this part.", "Use Import Scan (or Re-attach Scan if this part had one before).");
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
            string stored = Sw.GetLongProperty(AlignmentProperty) ?? Sw.GetLongProperty(LegacyAlignmentProperty);
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
            if (string.IsNullOrEmpty(Sw.PathName)) return false;
            var sidecar = Sidecar.ForPart(Sw.PathName);
            if (!sidecar.Exists) return false;
            Scan = Progress.Run(Owner, "Loading scan", job => ScanSession.Reattach(sidecar, job));
            // the display is stored in the part: keep it when it is there; a missing or broken one (older versions
            // linked a cache part that could go missing) is made again
            var body = new SwScanBody(Sw);
            if (body.HasLiveDisplay())
            {
                var shown = body.Displays();
                if (shown.Count > 0) State.ScanFeatureName = shown[0].Name;
            }
            else RefreshDisplay(State.DisplayTriangles, 0);
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
            // mesh/graphics bodies are found as bodies and matched to their feature (review I5)
            foreach (var kv in Sw.MeshBodies())
                if (kv.Value != null && kv.Value.Name.StartsWith(SwScanBody.Prefix, StringComparison.Ordinal))
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
            /// <summary>The plane as fitted (after snapping), part mm; null for cylinders.</summary>
            public Plane3? Plane;
            /// <summary>Points of the fitted region (part mm), to size the preview plate.</summary>
            public List<Vec3> Extent;
        }

        public FitOutcome FitPlaneAt(Ray3 ray, FitOptions opt)
        {
            RequireScan();
            PlaneFitResult part = null;
            var rec = Progress.Run(Owner, "Fitting plane", job => Scan.FitPlaneAt(ray, opt.Grow, job, out part));
            return FinishPlane(rec, part, opt, Scan.RegionPoints(part.Triangles));
        }

        public FitOutcome FitPlaneThrough(IList<Vec3> pts, Vec3 towardViewer, FitOptions opt)
        {
            RequireScan();
            PlaneFitResult part;
            var rec = Scan.FitPlaneThrough(pts, towardViewer, out part);
            return FinishPlane(rec, part, opt, new List<Vec3>(pts));
        }

        private FitOutcome FinishPlane(FitRecord rec, PlaneFitResult part, FitOptions opt, List<Vec3> extent)
        {
            var outcome = new FitOutcome { Record = rec, Extent = extent };
            var plane = part.Plane;
            string snap = "";
            if (opt.CreateFeature && opt.SnapSquare)
            {
                var s = SnapToSquare.SnapDirection(plane.Normal, new SwPlanes(Sw).References(), opt.SnapTolDeg);
                if (s.Snapped) { plane = new Plane3(plane.Origin, s.Dir); snap = s.Describe(); }
            }
            rec.Snap = snap;
            outcome.Plane = plane;
            outcome.Report = string.Format("{0} plane: {1}{2}", rec.Id, part.Summary(), snap.Length > 0 ? "; " + snap : "");
            if (opt.CreateFeature)
            {
                string name = string.Format("Plane {0}{1} (rms {2:0.000} max {3:0.000})", rec.Id, opt.NameHint.Length > 0 ? " " + opt.NameHint : "", part.Rms, part.MaxDev);
                string desc = "RE-Scan3D fit: " + part.Summary() + (snap.Length > 0 ? "; snapped " + snap : "") + "; source " + Path.GetFileName(State.SourcePath) + "; " + rec.Created;
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
                "RE-Scan3D fit: " + (part.Convex ? "boss/shaft " : "hole ") + part.Summary() + (snap.Length > 0 ? "; snapped " + snap : "") + "; " + rec.Created);
            rec.FeatureName = outcome.Feature = f.Name;
            Scan.Save();
            Log.Info(outcome.Report);
            return outcome;
        }

        // ------------------------------------------------------------------ alignment mates (one scan plane per base plane)

        /// <summary>A plane defined on the scan but not stored yet (the Align window's preview).</summary>
        public sealed class PendingPlane
        {
            public PlaneFitResult ScanFit;     // scan coordinates
            public Plane3 Part;                // part coordinates
            public string Method;              // "region" | "points"
            public List<Vec3> Extent;          // part coordinates, for the plate
        }

        public PendingPlane PreviewPlaneAt(Ray3 ray, GrowOptions grow)
        {
            RequireScan();
            var fit = Progress.Run(Owner, "Fitting plane", job => Scan.GrowPlaneAt(ray, grow, job));
            return new PendingPlane { ScanFit = fit, Part = fit.Plane.Transformed(Scan.ScanToPart), Method = "region", Extent = Scan.RegionPoints(fit.Triangles) };
        }

        public PendingPlane PreviewPlaneThrough(IList<Vec3> points, Vec3 towardViewer)
        {
            RequireScan();
            var fit = Scan.PlaneThrough(points, towardViewer);
            return new PendingPlane { ScanFit = fit, Part = fit.Plane.Transformed(Scan.ScanToPart), Method = "points", Extent = new List<Vec3>(points) };
        }

        /// <summary>A stored fit as plane + outline in part coordinates (to show a saved mate again).</summary>
        public PendingPlane PlaneOf(FitRecord f)
        {
            var x = Scan.ScanToPart;
            var ext = new List<Vec3>();
            if (f.Extent != null)
                for (int i = 0; i + 2 < f.Extent.Length; i += 3) ext.Add(x.Apply(new Vec3(f.Extent[i], f.Extent[i + 1], f.Extent[i + 2])));
            return new PendingPlane { Part = f.PlaneScan().Transformed(x), Method = f.Method, Extent = ext };
        }

        /// <summary>
        /// Store a mate between a scan plane and a base plane, re-solve, move the scan and update the Scan-* planes.
        /// One scan plane per base plane: an older mate to the same base plane is replaced. `plane` null keeps the
        /// edited mate's plane; `editIndex` -1 adds a new mate.
        /// `value` and `flip` are as the user sees them, relative to the scan plane (user test 2026-10-26):
        ///   Offset: the standard plane ends up `value` mm from the scan plane, along the scan plane's normal
        ///           (the side facing the viewer when it was fitted), or the other side with `flip`;
        ///   Angle:  the standard plane ends up tilted `value`° from the scan plane, towards where the standard
        ///           plane is now, or the other way with `flip`;
        ///   Coincident / Parallel: `flip` turns the scan over (the scan ends up on the other side of the plane).
        /// </summary>
        public void SaveMate(PendingPlane plane, int editIndex, BasePlane target, MateType type, double value, bool flip)
        {
            RequireScan();
            string fitId;
            if (plane != null) fitId = Record(plane).Id;
            else if (editIndex >= 0) fitId = State.Mates[editIndex].FitId;
            else throw new Res3dException("No plane is defined yet.", "Click a face of the scan, or pick 3 or more points.");

            // the solve starts from the scan as imported, so the automatic side (Sense) is decided afresh each time
            var m = new AlignMate { FitId = fitId, Target = target, Type = type, UserValue = value, UserFlip = flip };
            if (type == MateType.Angle) m.Value = flip ? -value : value;   // a negative angle tilts the other way
            BasePlane? movedFrom = editIndex >= 0 && State.Mates[editIndex].Target != target ? State.Mates[editIndex].Target : (BasePlane?)null;
            if (editIndex >= 0) State.Mates[editIndex] = m; else State.Mates.Add(m);
            State.Mates.RemoveAll(x => !ReferenceEquals(x, m) && x.Target == target);   // one scan plane per standard plane: replaced
            if (movedFrom.HasValue)
            {
                // the mate moved to another standard plane: the old one's Scan-* plane has nothing to follow any more
                string oldName, key = movedFrom.Value.ToString();
                if (State.ScanPlanes.TryGetValue(key, out oldName) && new SwPlanes(Sw).DeleteIfUnused(oldName)) State.ScanPlanes.Remove(key);
            }
            Solve();                                  // decides the side (Sense): face normal with or against the base normal
            if (type == MateType.Distance)
            {
                // the standard plane sits at face + s*value*n; after the solve n = Sense*N, so the face is at
                // N.origin = -s*value*Sense (the solver's signed distance along the base normal)
                double s = flip ? -1 : 1;
                m.Value = -s * value * (m.Sense == 0 ? 1 : m.Sense);
            }
            else if (flip && m.Type != MateType.Angle && m.Sense != 0) m.Sense = -m.Sense;
            Accept();                                 // solve again, Scan-* planes made / moved, saved
            RefreshDisplay(State.DisplayTriangles, 0);
        }

        /// <summary>
        /// Bring the stored mates in line with the feature tree: a Scan-* plane is the visible form of its mate, so
        /// when the user has deleted it from the tree, the mate goes too (user test 2026-10-26: a deleted Scan-Right
        /// left its mate behind and Save then said Right Plane was still taken). A renamed Scan-* plane is found
        /// again by its description. Re-aligns the scan when a mate was removed. Returns what was changed.
        /// </summary>
        public List<string> ReconcileMates()
        {
            var notes = new List<string>();
            if (Scan == null) return notes;
            bool removed = false, renamed = false;
            foreach (var m in State.Mates.ToArray())
            {
                string key = m.Target.ToString(), name;
                if (!State.ScanPlanes.TryGetValue(key, out name) || Sw.FindFeature(name) != null) continue;
                IFeature again = null;
                string marker = "actual position of the face mated to " + BasePlanes.SwName(m.Target);
                foreach (var f in Sw.AllFeatures())
                    if (f.GetTypeName2() == "RefPlane" && (f.Description ?? "").IndexOf(marker, StringComparison.Ordinal) >= 0) { again = f; break; }
                if (again != null)
                {
                    State.ScanPlanes[key] = again.Name;
                    renamed = true;
                    continue;
                }
                State.Mates.Remove(m);
                State.ScanPlanes.Remove(key);
                var def = Sw.FindFeature(SwPlanes.DefPrefix + name);
                if (def != null && Sw.Children(def).Count == 0) Sw.Delete(def);
                notes.Add("The mate to " + BasePlanes.SwName(m.Target) + " was removed: its plane " + name + " had been deleted from the feature tree.");
                removed = true;
            }
            if (removed)
            {
                Accept();
                RefreshDisplay(State.DisplayTriangles, 0);
            }
            else if (renamed) SaveAll();
            foreach (var n in notes) Log.Info(n);
            return notes;
        }

        /// <summary>Store a plane defined in a window as a fit record, with its outline (scan coordinates).</summary>
        private FitRecord Record(PendingPlane plane)
        {
            PlaneFitResult unused;
            var rec = Scan.RecordPlane(plane.ScanFit, plane.Method, out unused);
            var toScan = Scan.ScanToPart.Inverse();
            var flat = new List<double>();
            int step = Math.Max(1, plane.Extent.Count / 48);
            for (int i = 0; i < plane.Extent.Count; i += step)
            {
                var q = toScan.Apply(plane.Extent[i]);
                flat.Add(q.X); flat.Add(q.Y); flat.Add(q.Z);
            }
            rec.Extent = flat.ToArray();
            return rec;
        }

        // ------------------------------------------------------------------ Add Plane

        /// <summary>
        /// The plane Add Plane makes from a plane defined on the scan: moved `offset` mm along its normal, then tilted
        /// `angleDeg` about the fitted area's long direction through its centre; `flip` reverses both.
        /// </summary>
        public static Plane3 Adjusted(Plane3 plane, IList<Vec3> extent, double offset, double angleDeg, bool flip)
        {
            var n = plane.Normal.Normalized();
            var c = Vec3.Zero;
            foreach (var q in extent) c += q;
            c = plane.Project(c * (1.0 / Math.Max(1, extent.Count)));
            double s = flip ? -1 : 1;
            var o = c + n * (s * offset);
            if (angleDeg == 0) return new Plane3(o, n);
            return new Plane3(o, Mat3.Rotation(LongAxis(plane, extent, c), s * angleDeg * Math.PI / 180) * n);
        }

        /// <summary>The direction in which a fitted area is longest (in-plane principal axis).</summary>
        public static Vec3 LongAxis(Plane3 plane, IList<Vec3> extent, Vec3 centre)
        {
            var n = plane.Normal.Normalized();
            var u = n.AnyPerpendicular().Normalized();
            var v = Vec3.Cross(n, u);
            double suu = 0, suv = 0, svv = 0;
            foreach (var p in extent)
            {
                var d = p - centre;
                double a = Vec3.Dot(d, u), b = Vec3.Dot(d, v);
                suu += a * a; suv += a * b; svv += b * b;
            }
            double t = 0.5 * Math.Atan2(2 * suv, suu - svv);
            return (u * Math.Cos(t) + v * Math.Sin(t)).Normalized();
        }

        /// <summary>
        /// Add Plane: store the plane defined on the scan and create a SolidWorks reference plane from it (offset /
        /// tilted as Adjusted), optionally snapped square to existing planes. Returns the fit record.
        /// </summary>
        public FitRecord AddPlane(PendingPlane plane, double offset, double angleDeg, bool flip, bool snapSquare, double snapTolDeg, string name)
        {
            RequireScan();
            var rec = Record(plane);
            var p = Adjusted(plane.Part, plane.Extent, offset, angleDeg, flip);
            string snap = "";
            if (snapSquare)
            {
                var sq = SnapToSquare.SnapDirection(p.Normal, new SwPlanes(Sw).References(), snapTolDeg);
                if (sq.Snapped) { p = new Plane3(p.Origin, sq.Dir); snap = sq.Describe(); }
            }
            rec.Snap = snap;
            string label = string.IsNullOrWhiteSpace(name)
                ? string.Format("Plane {0} (rms {1:0.000} max {2:0.000})", rec.Id, rec.Rms, rec.MaxDev)
                : name.Trim();
            string how = (offset != 0 ? string.Format("; offset {0:0.###} mm", flip ? -offset : offset) : "") + (angleDeg != 0 ? string.Format("; tilted {0:0.##}°", flip ? -angleDeg : angleDeg) : "");
            string desc = "RE-Scan3D plane from " + (plane.Method == "points" ? plane.Extent.Count + " points" : "a face") + string.Format(": rms {0:0.000} max {1:0.000} mm", rec.Rms, rec.MaxDev)
                          + how + (snap.Length > 0 ? "; snapped " + snap : "") + "; " + rec.Created;
            var f = new SwPlanes(Sw).CreatePlane(Sw.UniqueName(label), p, desc);
            rec.FeatureName = f.Name;
            SaveAll();
            Log.Info("added plane " + f.Name + how + (snap.Length > 0 ? "; " + snap : ""));
            return rec;
        }

        /// <summary>The reference planes made with Add Plane (or Fit Feature) that are still in the part.</summary>
        public List<FitRecord> AddedPlanes()
        {
            var list = new List<FitRecord>();
            foreach (var f in State.Fits)
                if (f.Kind == "plane" && !string.IsNullOrEmpty(f.FeatureName) && Sw.FindFeature(f.FeatureName) != null) list.Add(f);
            return list;
        }

        /// <summary>Delete a plane made by the add-in; false when it was kept because other features use it.</summary>
        public bool DeleteAddedPlane(FitRecord f)
        {
            if (!new SwPlanes(Sw).DeleteIfUnused(f.FeatureName)) return false;
            f.FeatureName = "";
            SaveAll();
            return true;
        }

        /// <summary>Remove a mate; its Scan-* plane goes too unless other features are built on it.</summary>
        public string DeleteMate(int index)
        {
            RequireScan();
            var m = State.Mates[index];
            State.Mates.RemoveAt(index);
            string key = m.Target.ToString(), name, note = "";
            if (State.ScanPlanes.TryGetValue(key, out name))
            {
                if (new SwPlanes(Sw).DeleteIfUnused(name)) State.ScanPlanes.Remove(key);
                else note = name + " was kept because other features use it; it no longer follows the scan.";
            }
            Accept();
            RefreshDisplay(State.DisplayTriangles, 0);
            return note;
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
                string desc = "RES3D: actual position of the face mated to " + BasePlanes.SwName(kv.Key) + " after alignment (" + ScanState.Now() + ")";
                if (State.ScanPlanes.TryGetValue(key, out name) && Sw.FindFeature(name) != null)
                {
                    if (!planes.UpdatePlane(name, kv.Value))
                        Log.Info("could not update " + name + " in place (it is not an RE-Scan3D definition-sketch plane)");
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
            new SwSketch(Sw).RemoveTemporarySketches();
            var body = new SwScanBody(Sw);
            var names = new List<string>();
            foreach (var f in body.Find(Scan != null ? State.ScanFeatureName : null))
                if (f.GetTypeName2() == "BrokenDerivedPartFolder") names.Insert(0, f.Name); else names.Add(f.Name);
            foreach (var n in names)
            {
                var f = Sw.FindFeature(n);   // deleting a holder folder can take its contents with it
                if (f != null && Sw.Delete(f)) removed.Add(n);
            }
            Sw.DeleteFolderIfEmpty(SwScanBody.Folder);
            if (Scan != null)
            {
                State.ScanFeatureName = null;
                body.ReleaseCached(Scan.Sidecar.Folder, null);   // SolidWorks would otherwise keep the display part open
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
