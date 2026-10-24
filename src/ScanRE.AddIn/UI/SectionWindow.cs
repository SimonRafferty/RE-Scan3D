// Section & Snap and Trace (spec §4-5). With a sketch open: section the full-res scan by the sketch plane,
// show it in a temporary 3D sketch, then the cursor snaps to the nearest section point (moving marker) and
// clicks place FIXED native sketch points - or trace between two clicks into points or a spline.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Windows.Forms;
using ScanRE.AddIn.Sw;
using ScanRE.Core.Math3;
using ScanRE.Core.Section;
using ScanRE.Core.Trace;

namespace ScanRE.AddIn.UI
{
    public sealed class SectionWindow : ToolWindow
    {
        private readonly Label sketchLabel = new Label { AutoSize = true };
        private readonly NumericUpDown slab = Number(Settings.Get("SectionSlab", 0), 0, 20, 2, 0.1);
        private readonly NumericUpDown layers = Number(Settings.Get("SectionLayers", 2), 1, 10, 0, 1);
        private readonly RadioButton snapPoints = new RadioButton { Text = "Snap: click places a fixed point", AutoSize = true, Checked = true };
        private readonly RadioButton tracePoints = new RadioButton { Text = "Trace → points (click start, then end)", AutoSize = true };
        private readonly RadioButton traceSpline = new RadioButton { Text = "Trace → spline (click start, then end)", AutoSize = true };
        private readonly NumericUpDown spacing = Number(Settings.Get("TraceSpacing", 2), 0.05, 100, 2, 0.5);
        private readonly NumericUpDown splineTol = Number(Settings.Get("TraceTol", 0.05), 0.001, 5, 3, 0.01);
        private readonly CheckBox longWay = new CheckBox { Text = "Closed curve: go the long way round", AutoSize = true };
        private readonly NumericUpDown radiusPx = Number(Settings.Get("SnapRadiusPx", 25), 2, 200, 0, 5);
        private readonly CheckBox marker = new CheckBox { Text = "Show snap marker (a temporary sketch point)", AutoSize = true, Checked = Settings.Get("SnapMarker", 0) > 0 };
        private readonly Label snapInfo = new Label { AutoSize = true };
        private bool markerPending;
        private readonly Label status = new Label { AutoSize = true, MaximumSize = new System.Drawing.Size(340, 0) };

        private readonly SwSketch sketches;
        private PlaneSection section;
        private string sectionFor;          // sketch name the section belongs to
        private SectionPoint traceStart = SectionPoint.None;
        private SectionPoint lastSnap = SectionPoint.None;
        private readonly Stack<List<object>> undo = new Stack<List<object>>();
        private readonly Stopwatch moveClock = Stopwatch.StartNew();

        public SectionWindow(DocSession s) : base(s, "Section & Snap", 390)
        {
            sketches = new SwSketch(s.Sw);
            Heading("Sketch");
            Wide(sketchLabel);
            Heading("Section");
            Row("Slab half-thickness (mm)", slab);
            Row("Layers each side", layers);
            var compute = new Button { Text = "Compute section" };
            var clear = new Button { Text = "Remove section display" };
            Buttons(compute, clear);
            Heading("Click mode");
            Wide(snapPoints); Wide(tracePoints); Wide(traceSpline);
            Row("Point spacing (mm)", spacing);
            Row("Spline tolerance (mm)", splineTol);
            Wide(longWay);
            Row("Snap radius (pixels)", radiusPx);
            Wide(marker);
            Wide(snapInfo);
            var undoBtn = new Button { Text = "Undo last" };
            var done = new Button { Text = "Done" };
            Buttons(undoBtn, done);
            Wide(status);
            Wide(Output);

            compute.Click += (o, e) => Act(Compute);
            clear.Click += (o, e) => Act(() => { sketches.RemoveSection(); section = null; status.Text = "Section display removed."; });
            undoBtn.Click += (o, e) => Act(() => { if (undo.Count > 0) { sketches.DeleteEntities(undo.Pop()); Say("undone"); } });
            done.Click += (o, e) => Close();
            tracePoints.CheckedChanged += (o, e) => traceStart = SectionPoint.None;
            traceSpline.CheckedChanged += (o, e) => traceStart = SectionPoint.None;
            marker.CheckedChanged += (o, e) => { if (!marker.Checked) sketches.RemoveMarker(); };
            UpdateSketchLabel();
            status.Text = sketches.Active == null
                ? "Open a sketch on a plane (Scan-Top, a fitted plane...), then press Compute section."
                : "Press Compute section.";
            HookActiveView();
        }

        private void Act(Action a)
        {
            try { a(); }
            catch (Exception e) { Msg.Error(this, e); }
        }

        private void UpdateSketchLabel()
        {
            var n = sketches.ActiveName;
            sketchLabel.Text = n == null ? "(no sketch open)" : n;
        }

        private void Compute()
        {
            UpdateSketchLabel();
            var sk = sketches.Active;
            if (sk == null)
                throw new Core.Jobs.ScanReException("No sketch is open.", "Edit or create a sketch on a plane through the scan, then press Compute section.");
            Settings.Set("SectionSlab", (double)slab.Value);
            Settings.Set("SectionLayers", (double)layers.Value);
            var plane = sketches.PlaneOf(sk);
            var opt = new SectionOptions { SlabHalfThickness = (double)slab.Value, SlabLayersEachSide = (int)layers.Value };
            sectionFor = sketches.ActiveName;
            var sw = Stopwatch.StartNew();
            section = Progress.Run(Session.Owner, "Sectioning scan", job => Session.Scan.Section(plane, opt, job));
            undo.Clear();   // leaving/re-entering the sketch invalidates earlier entity handles
            long coreMs = sw.ElapsedMilliseconds;
            sw.Restart();
            sketches.ShowSection(section, Math.Max(0.005, (double)splineTol.Value / 5));
            Log.Timing("section.core (" + section.SegmentTotal + " segments)", coreMs);
            Log.Timing("section.display", sw.ElapsedMilliseconds);
            HookActiveView();   // re-entering the sketch can change the active view object
            traceStart = SectionPoint.None;
            status.Text = string.Format("{0} curve(s), {1} segments ({2} ms). Move over the sketch: the marker snaps to the scan.",
                                        section.Lines.Count, section.SegmentTotal, coreMs);
            Say(status.Text);
        }

        private bool SectionValid()
        {
            if (section == null) return false;
            if (sketches.ActiveName != sectionFor)
            {
                section = null;
                sketches.RemoveMarker();
                status.Text = "The open sketch changed: press Compute section again.";
                UpdateSketchLabel();
                return false;
            }
            return true;
        }

        private SectionPoint SnapAt(int x, int y)
        {
            if (View == null) return SectionPoint.None;
            var ray = View.RayAt(x, y);
            double t;
            if (!ray.Intersect(section.Plane, out t)) return SectionPoint.None;
            double radius = (double)radiusPx.Value * View.PixelSizeMm();
            return section.Nearest(ray.At(t), radius);
        }

        protected override void OnMouseMove(int x, int y)
        {
            if (moveClock.ElapsedMilliseconds < 30) return;   // ~30 updates per second is plenty
            moveClock.Restart();
            if (!SectionValid()) return;
            var sp = SnapAt(x, y);
            lastSnap = sp;
            snapInfo.Text = sp.Valid ? string.Format("snap {0:0.###} mm from cursor", sp.Distance) : "no section near the cursor";
            if (!marker.Checked || !sp.Valid || markerPending) return;
            // never touch the sketch inside SolidWorks' own mouse callback: do it after it returns
            markerPending = true;
            Later(() => { markerPending = false; if (lastSnap.Valid && marker.Checked) sketches.ShowMarker(lastSnap.Point); });
        }

        protected override void OnMouseClick(int x, int y)
        {
            if (!SectionValid()) return;
            var sp = SnapAt(x, y);
            if (!sp.Valid) { Say("(no section within the snap radius)"); return; }
            if (snapPoints.Checked)
            {
                undo.Push(sketches.CreateFixedPoints(new[] { sp.Point }));
                Say(string.Format("point at {0}", sp.Point));
                return;
            }
            if (!traceStart.Valid) { traceStart = sp; Say("trace start set; click the end (the same point again = whole loop)"); return; }
            var start = traceStart;
            traceStart = SectionPoint.None;
            bool whole;
            var path = Tracer.PathBetween(section, start, sp, null, out whole);
            if (longWay.Checked && !whole && section.Lines[start.Line].Closed)
            {
                // the other side of the loop: trace through a point opposite the short path's middle
                var other = OtherSide(start, sp, path);
                if (other != null) path = other;
            }
            Settings.Set("TraceSpacing", (double)spacing.Value);
            Settings.Set("TraceTol", (double)splineTol.Value);
            TraceResult res;
            List<object> made;
            if (tracePoints.Checked)
            {
                res = Tracer.AsPoints(path, (double)spacing.Value, whole);
                made = sketches.CreateFixedPoints(res.Output);
            }
            else
            {
                res = Tracer.AsSpline(path, (double)splineTol.Value, whole);
                var seg = sketches.CreateFixedSpline(res.Output, whole);
                made = new List<object>();
                if (seg != null) made.Add(seg);
            }
            undo.Push(made);
            Say(string.Format("{0}: {1} {2}, path {3:0.##} mm, deviation max {4:0.###} rms {5:0.###} mm",
                whole ? "loop" : "trace", res.Output.Count, tracePoints.Checked ? "points" : "spline points", Length(path), res.MaxDev, res.RmsDev));
            sketches.RemoveMarker();
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
            try { sketches.RemoveMarker(); sketches.RemoveSection(); }
            catch (Exception e) { Log.Error("section cleanup", e); }
        }
    }
}
