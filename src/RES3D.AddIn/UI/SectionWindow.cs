// Contours (was Section and Snap; spec §4-5). With a sketch open: cut the full-resolution scan with the sketch plane and show the
// outline (a temporary 3D sketch), then clicks on the outline make native sketch geometry in the user's sketch:
// a snapped point, or - between two clicks - points, a spline, or best-fit lines and arcs (user request 2026-10-26).
// Laid out like the Align Scan / Add Plane windows (user test 2026-10-26: the first version was confusing).
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Windows.Forms;
using RES3D.AddIn.Sw;
using RES3D.Core.Math3;
using RES3D.Core.Section;
using RES3D.Core.Trace;

namespace RES3D.AddIn.UI
{
    public sealed class SectionWindow : ToolWindow
    {
        private readonly Label sketchLabel = new Label { AutoSize = true, MaximumSize = new System.Drawing.Size(360, 0) };
        private readonly NumericUpDown slab = Number(Settings.Get("SectionSlab", 0), 0, 20, 2, 0.1);
        private readonly NumericUpDown layers = Number(Settings.Get("SectionLayers", 2), 1, 10, 0, 1);
        private readonly RadioButton modeSnap = new RadioButton { Text = "Point: each click places a point on the outline", AutoSize = true, Checked = true };
        private readonly RadioButton modePoints = new RadioButton { Text = "Trace → points between two clicks", AutoSize = true };
        private readonly RadioButton modeSpline = new RadioButton { Text = "Trace → spline between two clicks", AutoSize = true };
        private readonly RadioButton modeLines = new RadioButton { Text = "Trace → lines and arcs between two clicks", AutoSize = true };
        private readonly NumericUpDown spacing = Number(Settings.Get("TraceSpacing", 2), 0.05, 100, 2, 0.5);
        private readonly NumericUpDown tolerance = Number(Settings.Get("TraceTol", 0.05), 0.001, 5, 3, 0.01);
        private readonly CheckBox longWay = new CheckBox { Text = "Closed outline: go the long way round", AutoSize = true };
        private readonly CheckBox construction = new CheckBox { Text = "Outline as construction geometry", AutoSize = true, Checked = Settings.Get("OutlineConstruction", 1) > 0 };
        private readonly CheckBox fix = new CheckBox { Text = "Fix the result (fully defined, no relation to the scan)", AutoSize = true, Checked = Settings.Get("TraceFix", 1) > 0 };
        private readonly NumericUpDown radiusPx = Number(Settings.Get("SnapRadiusPx", 25), 2, 200, 0, 5);
        private readonly CheckBox marker = new CheckBox { Text = "Show the snap point as you move (a temporary sketch point)", AutoSize = true, Checked = Settings.Get("SnapMarker", 0) > 0 };
        private readonly Label status = new Label { AutoSize = true, MaximumSize = new System.Drawing.Size(360, 0) };
        private Label spacingLabel, toleranceLabel;
        private bool markerPending;

        private readonly SwSketch sketches;
        private readonly SwPreview planeShow;     // blue stripes on the sketch plane, over the section (user request)
        private readonly SwPreview balls;         // a ball on each registered click until the result is made
        private PlaneSection section;
        private string sectionFor;          // sketch name the section belongs to
        private SectionPoint traceStart = SectionPoint.None;
        private SectionPoint lastSnap = SectionPoint.None;
        private readonly Stack<List<object>> undo = new Stack<List<object>>();
        private readonly Stopwatch moveClock = Stopwatch.StartNew();

        public SectionWindow(DocSession s) : base(s, "Contours", 400)
        {
            sketches = new SwSketch(s.Sw);
            planeShow = new SwPreview(s.Sw, SwPreview.Blue);
            balls = new SwPreview(s.Sw);

            Heading("1. Section the scan");
            Tip(Wide(sketchLabel), "The sketch the outline is made for. Open (edit) a sketch on a plane through the scan first.");
            Row("Slab half-thickness (mm)", slab, "0 = a single cut through the scan at the sketch plane. Above 0, cuts are also taken this far either side and averaged, which smooths out scanner noise (keep it small where the shape changes quickly).");
            Row("Layers each side", layers, "How many extra cuts to take on each side of the sketch plane when the slab half-thickness is above 0.");
            Tip(Wide(construction), "Draw the outline as construction geometry (a guide to trace over and snap to). Untick to draw it as ordinary geometry you can use directly, e.g. as a profile.");
            var compute = new Button { Text = "Compute section" };
            var clear = new Button { Text = "Remove outline" };
            Buttons(compute, clear);
            Tip(compute, "Cut the full-resolution scan with the open sketch's plane and draw its outline in your sketch as splines, within the tolerance below. It stays in the sketch for you to use; computing again in the same sketch replaces it.");
            Tip(clear, "Delete the outline drawn last from its sketch.");

            Heading("2. Click on the outline");
            Tip(Wide(modeSnap), "Each click near the outline places a sketch point on its nearest point.");
            Tip(Wide(modePoints), "Click a start and an end on the outline: sketch points are placed along it between them. Click the same point twice for a whole closed outline.");
            Tip(Wide(modeSpline), "Click a start and an end on the outline: one spline follows it between them, with as few points as the tolerance allows. Click the same point twice for a whole closed outline.");
            Tip(Wide(modeLines), "Click a start and an end on the outline: straight lines and circular arcs are best-fitted to it within the tolerance, joined end to end. A round hole becomes a circle. Click the same point twice for a whole closed outline.");
            Row("Point spacing (mm)", spacing, "Distance between the points placed by Trace → points.", out spacingLabel);
            Row("Tolerance (mm)", tolerance, "How closely the outline, a traced spline, or traced lines and arcs follow the scan's section. Smaller = closer but more points / pieces.", out toleranceLabel);
            Tip(Wide(longWay), "On a closed outline, trace round the longer way between the start and the end instead of the shorter way.");
            Tip(Wide(fix), "Give what is made a Fixed relation: fully defined and exactly where the scan is. Untick to dimension and relate it yourself.");
            Row("Snap radius (pixels)", radiusPx, "How close to the outline (on screen) a click must be to snap to it.");
            Tip(Wide(marker), "Show where a click would snap, as a temporary sketch point that follows the mouse. Off by default: SolidWorks may attach your own sketching to it.");
            Tip(Wide(status), "What happened on the last click.");

            var undoBtn = new Button { Text = "Undo last" };
            var close = new Button { Text = "Close" };
            BottomButtons(undoBtn, close);
            Tip(undoBtn, "Remove what the last click made.");
            Tip(close, "Close this window. The outline and everything you made stay in the sketch.");

            compute.Click += (o, e) => Act(Compute);
            clear.Click += (o, e) => Act(() => { sketches.RemoveOutline(); balls.ClearPoints(); status.Text = "Outline removed."; });
            undoBtn.Click += (o, e) => Act(() => { if (undo.Count > 0) { sketches.DeleteEntities(undo.Pop()); status.Text = "Last result removed."; } });
            close.Click += (o, e) => Close();
            foreach (var r in new[] { modeSnap, modePoints, modeSpline, modeLines }) r.CheckedChanged += (o, e) => { traceStart = SectionPoint.None; UpdateControls(); };
            marker.CheckedChanged += (o, e) => { if (!marker.Checked) sketches.RemoveMarker(); };
            UpdateSketchLabel();
            UpdateControls();
            if (s.LastSection != null && sketches.ActiveName != null && s.LastSectionSketch == sketches.ActiveName)
            {
                section = s.LastSection;
                sectionFor = s.LastSectionSketch;
                ShowSectionPlane();
                status.Text = "The outline computed earlier for this sketch is ready: " + ClickHint();
            }
            else
                status.Text = sketches.Active == null
                    ? "Open (edit) a sketch on a plane through the scan, then press Compute section."
                    : "Press Compute section (or just click: the section is computed for the open sketch when needed).";
            HookActiveView();
        }

        private void UpdateControls()
        {
            spacing.Visible = spacingLabel.Visible = modePoints.Checked;
            tolerance.Visible = toleranceLabel.Visible = true;   // also how closely the outline follows the scan
            longWay.Visible = !modeSnap.Checked;
            FitToContent(true);
        }

        private void Act(Action a)
        {
            try { a(); }
            catch (Exception e) { Msg.Error(this, e); }
        }

        private void UpdateSketchLabel()
        {
            var n = sketches.ActiveName;
            sketchLabel.Text = n == null ? "No sketch is open." : "Sketch: " + n;
        }

        private void Compute()
        {
            UpdateSketchLabel();
            var sk = sketches.Active;
            if (sk == null)
                throw new Core.Jobs.Res3dException("No sketch is open.", "Edit or create a sketch on a plane through the scan, then press Compute section.");
            Settings.Set("SectionSlab", (double)slab.Value);
            Settings.Set("SectionLayers", (double)layers.Value);
            var plane = sketches.PlaneOf(sk);
            var opt = new SectionOptions { SlabHalfThickness = (double)slab.Value, SlabLayersEachSide = (int)layers.Value };
            sectionFor = sketches.ActiveName;
            var sw = Stopwatch.StartNew();
            section = Progress.Run(Session.Owner, "Sectioning scan", job => Session.Scan.Section(plane, opt, job));
            Session.LastSection = section;
            Session.LastSectionSketch = sectionFor;
            balls.ClearPoints();
            ShowSectionPlane();
            undo.Clear();
            long coreMs = sw.ElapsedMilliseconds;
            sw.Restart();
            status.Text = "Drawing the outline...";
            status.Refresh();
            // the outline is the user's geometry: as close to the section as the tolerance asks
            double displayTol = Math.Max(0.005, (double)tolerance.Value);
            Settings.Set("TraceTol", (double)tolerance.Value);
            Settings.Set("OutlineConstruction", construction.Checked ? 1 : 0);
            Settings.Set("TraceFix", fix.Checked ? 1 : 0);
            sketches.ShowSection(section, displayTol, construction.Checked, fix.Checked);
            Log.Timing("section.core (" + section.SegmentTotal + " segments)", coreMs);
            Log.Timing("section.display (tolerance " + displayTol.ToString("0.###") + " mm)", sw.ElapsedMilliseconds);
            HookActiveView();   // re-entering the sketch can change the active view object
            traceStart = SectionPoint.None;
            Log.Info("after Compute section the open sketch is " + (sketches.ActiveName ?? "none"));
            // check again once SolidWorks has finished: re-open the sketch if something left it
            Later(() =>
            {
                string now = sketches.ActiveName;
                Log.Info("after Compute section (settled) the open sketch is " + (now ?? "none"));
                if (now == null && sectionFor != null) { Session.Sw.ReenterSketch(sectionFor); HookActiveView(); }
            });
            status.Text = string.Format("Outline ready: {0} curve(s). {1}", section.Lines.Count, ClickHint())
                          + (section.Lines.Count > 20 ? " (So many pieces usually means the sketch plane lies along a face of the scan: a trace only follows one piece - use a plane that cuts through the part.)" : "");
        }

        private string ClickHint()
        {
            return modeSnap.Checked ? "Click near the outline to place points." : "Click the start of the stretch to trace on the outline.";
        }

        private bool SectionValid()
        {
            if (section == null)
            {
                var sk = sketches.Active;
                if (sk == null) { status.Text = "Open (edit) a sketch on a plane through the scan first."; Log.Info("section click ignored: no sketch open"); return false; }
                // the outline may already be in the sketch (drawn earlier): compute the section data, draw nothing
                var opt = new SectionOptions { SlabHalfThickness = (double)slab.Value, SlabLayersEachSide = (int)layers.Value };
                var plane = sketches.PlaneOf(sk);
                section = Progress.Run(Session.Owner, "Sectioning scan", job => Session.Scan.Section(plane, opt, job));
                sectionFor = sketches.ActiveName;
                Session.LastSection = section;
                Session.LastSectionSketch = sectionFor;
                Log.Info("section computed on click for " + sectionFor + ": " + section.Lines.Count + " curve(s)");
                ShowSectionPlane();
            }
            if (sketches.ActiveName == null && sectionFor != null && Session.Sw.FindFeature(sectionFor) != null)
            {
                // no sketch open (SolidWorks left the user's sketch after the outline was drawn - user test): open it again
                Log.Info("section click: no sketch open - re-opening " + sectionFor);
                Session.Sw.ReenterSketch(sectionFor);
                Session.Sw.ClearSelection();
                HookActiveView();
            }
            if (sketches.ActiveName != sectionFor)
            {
                Log.Info("section click ignored: the open sketch is " + (sketches.ActiveName ?? "none") + ", the outline was made for " + sectionFor);
                section = null;
                sketches.RemoveMarker();
                planeShow.HidePlane();
                balls.ClearPoints();
                status.Text = "The open sketch changed: press Compute section again.";
                UpdateSketchLabel();
                return false;
            }
            return true;
        }

        private SectionPoint SnapAt(int x, int y) { return SnapAt(x, y, false); }

        /// <summary>
        /// The outline point for a click. When the click landed on the outline drawn in the sketch, SolidWorks reports
        /// the point it picked on it: that is exactly what the user sees, whatever the view (user test: the view-ray
        /// guess picked the wrong contour). Otherwise the view ray is cut with the sketch plane.
        /// </summary>
        private SectionPoint SnapAt(int x, int y, bool useSolidWorksPick)
        {
            if (View == null) return SectionPoint.None;
            double radius = (double)radiusPx.Value * View.PixelSizeMm();
            Vec3 picked;
            if (useSolidWorksPick && SolidWorksPick(x, y, out picked) && Math.Abs(section.Plane.SignedDistance(picked)) < 0.5)
            {
                var sp = section.Nearest(picked, radius);
                if (sp.Valid) { Log.Info("section click: used SolidWorks' pick on the outline"); return sp; }
            }
            var ray = View.RayAt(x, y);
            double t;
            if (!ray.Intersect(section.Plane, out t)) return SectionPoint.None;
            return section.Nearest(ray.At(t), radius);
        }

        protected override void OnMouseMove(int x, int y)
        {
            if (!marker.Checked || section == null || moveClock.ElapsedMilliseconds < 30) return;   // ~30 updates per second is plenty
            moveClock.Restart();
            if (!SectionValid()) return;
            var sp = SnapAt(x, y);
            lastSnap = sp;
            if (!sp.Valid || markerPending) return;
            // never touch the sketch inside SolidWorks' own mouse callback: do it after it returns
            markerPending = true;
            Later(() => { markerPending = false; if (lastSnap.Valid && marker.Checked) sketches.ShowMarker(lastSnap.Point); });
        }

        protected override void OnMouseClick(int x, int y)
        {
            if (!SectionValid()) return;
            var sp = SnapAt(x, y, true);
            double radius = View != null ? (double)radiusPx.Value * View.PixelSizeMm() : 0;
            Log.Info(string.Format("section click {0},{1}: {2} (snap radius {3:0.###} mm, mode {4})", x, y,
                sp.Valid ? "snapped " + sp.Distance.ToString("0.###") + " mm away, curve " + sp.Line : "nothing within the radius", radius,
                modeSnap.Checked ? "point" : modePoints.Checked ? "trace points" : modeSpline.Checked ? "trace spline" : "trace lines+arcs"));
            if (!sp.Valid) { status.Text = "No outline within the snap radius of that click: click closer to the outline (or raise the snap radius)."; return; }
            Settings.Set("TraceFix", fix.Checked ? 1 : 0);
            if (modeSnap.Checked)
            {
                undo.Push(sketches.CreateFixedPoints(new[] { sp.Point }, fix.Checked));
                balls.ShowPoints(new[] { sp.Point });   // the last point placed
                status.Text = string.Format("Point placed at {0:0.###}, {1:0.###}, {2:0.###}.", sp.Point.X, sp.Point.Y, sp.Point.Z);
                return;
            }
            if (!traceStart.Valid)
            {
                traceStart = sp;
                balls.ShowPoints(new[] { sp.Point });
                status.Text = "Start set. Now click the end (the same point again = the whole closed outline).";
                return;
            }
            var start = traceStart;
            traceStart = SectionPoint.None;
            balls.ShowPoints(new[] { start.Point, sp.Point });
            status.Text = "Making it...";
            status.Refresh();
            if (start.Line != sp.Line)
            {
                balls.ClearPoints();
                status.Text = "The start and end are on different outline curves (gaps in the scan split the outline). Click the start again.";
                return;
            }
            bool whole;
            var path = Tracer.PathBetween(section, start, sp, null, out whole);
            if (longWay.Checked && !whole && section.Lines[start.Line].Closed)
            {
                var other = OtherSide(start, sp, path);
                if (other != null) path = other;
            }
            Settings.Set("TraceSpacing", (double)spacing.Value);
            Settings.Set("TraceTol", (double)tolerance.Value);
            double tol = (double)tolerance.Value;
            if (modePoints.Checked)
            {
                var res = Tracer.AsPoints(path, (double)spacing.Value, whole);
                undo.Push(sketches.CreateFixedPoints(res.Output, fix.Checked));
                status.Text = string.Format("{0}: {1} points along {2:0.##} mm.", whole ? "Whole outline" : "Traced", res.Output.Count, Length(path));
            }
            else if (modeSpline.Checked)
            {
                var res = Tracer.AsSpline(path, tol, whole);
                var seg = sketches.CreateFixedSpline(res.Output, whole, path, tol, res, fix.Checked);
                var made = new List<object>();
                if (seg != null) made.Add(seg);
                undo.Push(made);
                status.Text = string.Format("{0}: spline through {1} points, {2:0.##} mm long; largest deviation {3:0.###} mm.", whole ? "Whole outline" : "Traced", res.Output.Count, Length(path), res.MaxDev);
            }
            else
            {
                var res = LinesArcs.Fit(path, tol, whole);
                undo.Push(sketches.CreateLinesAndArcs(res.Pieces, whole, fix.Checked));
                status.Text = string.Format("{0}: {1} line(s), {2} arc(s){3}; largest deviation {4:0.###} mm.", whole ? "Whole outline" : "Traced",
                    res.Lines, res.Arcs, res.Circles > 0 ? ", " + res.Circles + " circle" : "", res.MaxDev)
                    + (res.Pieces.Count > 40 ? " That is a lot of pieces: the tolerance is following scanner noise - Undo last and try 0.1 to 0.2 mm." : "");
                Log.Info("lines and arcs: " + status.Text);
            }
            sketches.RemoveMarker();
            balls.ClearPoints();   // the contour is made: the click markers go
        }

        /// <summary>Blue stripes on the sketch plane, over the area of the section.</summary>
        private void ShowSectionPlane()
        {
            if (section == null || section.Lines.Count == 0) { planeShow.HidePlane(); return; }
            var pts = new List<Vec3>();
            int total = 0;
            foreach (var l in section.Lines) total += l.Points.Count;
            int step = Math.Max(1, total / 400);
            int k = 0;
            foreach (var l in section.Lines)
                foreach (var p in l.Points)
                    if (k++ % step == 0) pts.Add(p);
            planeShow.ShowPlane(section.Plane, pts);
        }

        private List<Vec3> OtherSide(SectionPoint a, SectionPoint b, List<Vec3> shortPath)
        {
            // the side through the loop point farthest from the short path's middle
            bool w;
            var line = section.Lines[a.Line];
            Vec3 far = line.Points[0];
            double best = -1;
            var mid = shortPath[shortPath.Count / 2];
            foreach (var p in line.Points)
            {
                double d = (p - mid).Length;
                if (d > best) { best = d; far = p; }
            }
            return Tracer.PathBetween(section, a, b, far, out w);
        }

        private static double Length(List<Vec3> p)
        {
            double l = 0;
            for (int i = 1; i < p.Count; i++) l += (p[i] - p[i - 1]).Length;
            return l;
        }

        protected override void OnClosedWindow()
        {
            Settings.Set("SnapRadiusPx", (double)radiusPx.Value);
            Settings.Set("SnapMarker", marker.Checked ? 1 : 0);
            try { sketches.RemoveMarker(); }   // the outline stays: it is the user's geometry now
            catch (Exception e) { Log.Error("section cleanup", e); }
            try { planeShow.Dispose(); balls.Dispose(); }
            catch (Exception e) { Log.Error("section cleanup", e); }
        }
    }
}
