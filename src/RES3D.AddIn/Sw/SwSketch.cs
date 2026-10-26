// Sketch-side operations for Section & Snap and Trace (spec §4-5): the active sketch's plane, fixed native
// points and splines, a moving snap marker, and a TEMPORARY 3D sketch that shows the section.
// Entities are added with AddToDB so SolidWorks' own inference can never relate them to the scan.
using System;
using System.Collections.Generic;
using RES3D.Core.Math3;
using RES3D.Core.Section;
using SolidWorks.Interop.sldworks;

namespace RES3D.AddIn.Sw
{
    public sealed class SwSketch
    {
        public const string TempPrefix = "_RES3D ";
        public const string SectionSketchName = TempPrefix + "Section (temporary)";

        /// <summary>A temporary sketch of this add-in (also the name used before the rename to RE-Scan3D).</summary>
        public static bool IsTemporary(string featureName)
        {
            return featureName.StartsWith(TempPrefix, StringComparison.Ordinal) || featureName.StartsWith("_ScanRE ", StringComparison.Ordinal);
        }
        private readonly SwDoc doc;

        public SwSketch(SwDoc doc) { this.doc = doc; }

        /// <summary>The 2D sketch being edited, or null.</summary>
        public ISketch Active
        {
            get
            {
                var s = doc.Sketches.ActiveSketch;
                return s != null && !s.Is3D() ? s : null;
            }
        }

        public string ActiveName
        {
            get { var s = Active; return s == null ? null : ((IFeature)s).Name; }
        }

        /// <summary>The active sketch's plane in part coordinates (mm).</summary>
        public Plane3 PlaneOf(ISketch s)
        {
            var toSketch = s.ModelToSketchTransform;
            var toModel = (MathTransform)toSketch.Inverse();
            var o = doc.TransformPoint(toModel, new double[] { 0, 0, 0 });
            var n = doc.TransformVector(toModel, new double[] { 0, 0, 1 });
            return new Plane3(Units.ToMm(o), new Vec3(n[0], n[1], n[2]));
        }

        private double[] ToSketch(ISketch s, Vec3 partMm)
        {
            return doc.TransformPoint(s.ModelToSketchTransform, Units.ToM(partMm));
        }

        // ------------------------------------------------------------------ native output

        /// <summary>Fixed sketch points at the given part positions (mm) in the active sketch.</summary>
        public List<object> CreateFixedPoints(IList<Vec3> partPoints, bool fix = true)
        {
            var s = Active;
            var made = new List<object>();
            if (s == null) return made;
            var sm = doc.Sketches;
            bool oldDb = sm.AddToDB;
            sm.AddToDB = true;
            try
            {
                foreach (var p in partPoints)
                {
                    var q = ToSketch(s, p);
                    var sp = sm.CreatePoint(q[0], q[1], 0);
                    if (sp != null) made.Add(sp);
                }
                if (fix) Fix(made);
            }
            finally { sm.AddToDB = oldDb; }
            return made;
        }

        /// <summary>
        /// A fixed through-point spline in the active sketch that stays within `tol` of `path` (both part mm).
        /// The through points were chosen against a stand-in curve; SolidWorks' own spline differs, so the real
        /// spline is sampled, out-of-tolerance stretches get extra points, and it is made again (a few passes).
        /// `through` is updated to the points used and `result` gets the deviation of the real spline.
        /// </summary>
        public object CreateFixedSpline(List<Vec3> through, bool closed, List<Vec3> path, double tol, Core.Trace.TraceResult result, bool fix = true)
        {
            var s = Active;
            if (s == null || through.Count < 2) return null;
            var toModel = (MathTransform)s.ModelToSketchTransform.Inverse();
            SketchSegment seg = null;
            var clock = System.Diagnostics.Stopwatch.StartNew();
            for (int pass = 0; pass < 6; pass++)
            {
                seg = AddSpline(s, through, closed);
                long tAdd = clock.ElapsedMilliseconds;
                if (seg == null) return null;
                var curve = Sample(seg, toModel, tol / 20);
                long tSample = clock.ElapsedMilliseconds;
                bool again = Core.Trace.Tracer.RefineAgainst(path, through, curve, tol, result);
                Log.Info(string.Format("spline pass {0}: {1} points, max dev {2:0.####} mm (add {3} ms, sample {4} ms, check {5} ms)",
                    pass, through.Count, result.MaxDev, tAdd, tSample - tAdd, clock.ElapsedMilliseconds - tSample));
                clock.Restart();
                if (!again || pass == 5) break;
                DeleteEntities(new List<object> { seg });
                Log.Timing("spline delete", clock.ElapsedMilliseconds);
                clock.Restart();
            }
            if (fix) Fix(new List<object> { seg });
            Log.Timing("spline fix", clock.ElapsedMilliseconds);
            return seg;
        }

        private SketchSegment AddSpline(ISketch s, List<Vec3> pts, bool closed)
        {
            var sm = doc.Sketches;
            bool oldDb = sm.AddToDB;
            sm.AddToDB = true;
            try
            {
                // closed: the first point repeated at the end makes SolidWorks close the spline (CreateSpline2's
                // flag is "simulate natural ends", not "periodic" - checked in SolidWorks 2025)
                int n = pts.Count + (closed ? 1 : 0);
                var arr = new double[3 * n];
                for (int i = 0; i < n; i++)
                {
                    var q = ToSketch(s, pts[i % pts.Count]);
                    arr[3 * i] = q[0]; arr[3 * i + 1] = q[1]; arr[3 * i + 2] = 0;
                }
                return sm.CreateSpline2(arr, false) as SketchSegment;
            }
            finally { sm.AddToDB = oldDb; }
        }

        /// <summary>The sketch segment as a fine polyline in part coordinates (mm), from one tessellation call.</summary>
        private List<Vec3> Sample(SketchSegment seg, MathTransform sketchToModel, double chordTolMm)
        {
            var c = (ICurve)seg.GetCurve();   // sketch space (measured in SolidWorks 2025)
            double t0, t1; bool isClosed, isPeriodic;
            c.GetEndParams(out t0, out t1, out isClosed, out isPeriodic);
            var a = (double[])c.Evaluate2(t0, 0);
            var b = (double[])c.Evaluate2(t1, 0);
            var tess = c.GetTessPts(Units.ToM(new Vec3(chordTolMm, 0, 0))[0], 1e-5, new[] { a[0], a[1], a[2] }, new[] { b[0], b[1], b[2] }) as double[];
            var res = new List<Vec3>();
            if (tess == null) return res;
            // sketch space -> model space
            for (int i = 0; i + 2 < tess.Length; i += 3)
                res.Add(Units.ToMm(doc.TransformPoint(sketchToModel, new[] { tess[i], tess[i + 1], tess[i + 2] })));
            return res;
        }

        /// <summary>
        /// Lines, arcs and circles (part mm) in the active sketch, as one connected chain: each join is merged into a
        /// single point. With `fix` everything is given a Fixed relation (fully defined, nothing refers to the scan);
        /// without, the user can dimension and relate it.
        /// </summary>
        public List<object> CreateLinesAndArcs(IList<Core.Trace.Piece> pieces, bool closed, bool fix)
        {
            var s = Active;
            var made = new List<object>();
            if (s == null || pieces.Count == 0) return made;
            var sm = doc.Sketches;
            bool oldDb = sm.AddToDB;
            sm.AddToDB = true;
            try
            {
                foreach (var pc in pieces)
                {
                    var a = ToSketch(s, pc.A);
                    SketchSegment seg;
                    if (pc.Kind == Core.Trace.PieceKind.Line)
                    {
                        var b = ToSketch(s, pc.B);
                        seg = sm.CreateLine(a[0], a[1], 0, b[0], b[1], 0);
                    }
                    else
                    {
                        var c = ToSketch(s, pc.Centre);
                        if (pc.Kind == Core.Trace.PieceKind.Circle)
                            seg = sm.CreateCircleByRadius(c[0], c[1], 0, pc.Radius / Units.MmPerM);
                        else
                        {
                            var b = ToSketch(s, pc.B);
                            var m = ToSketch(s, pc.Mid);
                            // direction in the sketch: +1 anticlockwise from A through Mid to B
                            double turn = (m[0] - a[0]) * (b[1] - m[1]) - (m[1] - a[1]) * (b[0] - m[0]);
                            seg = sm.CreateArc(c[0], c[1], 0, a[0], a[1], 0, b[0], b[1], 0, (short)(turn > 0 ? 1 : -1));
                        }
                    }
                    if (seg != null) made.Add(seg);
                }
                // merge the joins (AddToDB means nothing was merged or related while drawing)
                int joins = closed ? made.Count : made.Count - 1;
                if (made.Count > 1)
                    for (int i = 0; i < joins; i++)
                    {
                        var p1 = EndNear(made[i], pieces[i].B, s);
                        var p2 = EndNear(made[(i + 1) % made.Count], pieces[(i + 1) % pieces.Count].A, s);
                        if (p1 == null || p2 == null) continue;
                        doc.ClearSelection();
                        var data = doc.Selection.CreateSelectData();
                        if (p1.Select4(false, data) && p2.Select4(true, data)) doc.Doc.SketchAddConstraints("sgMERGEPOINTS");
                    }
                doc.ClearSelection();
                if (fix) Fix(made);
            }
            finally { sm.AddToDB = oldDb; }
            return made;
        }

        /// <summary>The end point of a line or arc nearest a part position.</summary>
        private SketchPoint EndNear(object segment, Vec3 partPos, ISketch s)
        {
            SketchPoint p1 = null, p2 = null;
            var line = segment as SketchLine;
            var arc = segment as SketchArc;
            if (line != null) { p1 = line.GetStartPoint2() as SketchPoint; p2 = line.GetEndPoint2() as SketchPoint; }
            else if (arc != null) { p1 = arc.GetStartPoint2() as SketchPoint; p2 = arc.GetEndPoint2() as SketchPoint; }
            if (p1 == null || p2 == null) return p1 ?? p2;
            var q = ToSketch(s, partPos);
            double d1 = (p1.X - q[0]) * (p1.X - q[0]) + (p1.Y - q[1]) * (p1.Y - q[1]);
            double d2 = (p2.X - q[0]) * (p2.X - q[0]) + (p2.Y - q[1]) * (p2.Y - q[1]);
            return d1 <= d2 ? p1 : p2;
        }

        private void Fix(List<object> entities)
        {
            if (entities.Count == 0) return;
            doc.ClearSelection();
            var data = doc.Selection.CreateSelectData();
            foreach (var e in entities)
            {
                if (e is SketchPoint) ((SketchPoint)e).Select4(true, data);
                else if (e is SketchSegment) ((SketchSegment)e).Select4(true, data);
            }
            doc.Doc.SketchAddConstraints("sgFIXED");
            doc.ClearSelection();
        }

        /// <summary>Delete entities created earlier (Undo last in the trace window).</summary>
        public void DeleteEntities(IList<object> entities)
        {
            doc.ClearSelection();
            var data = doc.Selection.CreateSelectData();
            int n = 0;
            foreach (var e in entities)
            {
                try
                {
                    if (e is SketchPoint && ((SketchPoint)e).Select4(true, data)) n++;
                    else if (e is SketchSegment && ((SketchSegment)e).Select4(true, data)) n++;
                }
                catch (Exception) { /* already deleted by the user */ }
            }
            if (n > 0) doc.Doc.EditDelete();
            doc.ClearSelection();
        }

        // ------------------------------------------------------------------ snap marker

        private SketchPoint marker;

        /// <summary>
        /// Show the snap position as a temporary point in the active sketch (moving it is cheap). Optional and off
        /// by default: SolidWorks inference can attach the user's own sketching to it (review I1); an overlay is
        /// the proper replacement (INVESTIGATION Q4).
        /// </summary>
        public void ShowMarker(Vec3 partPos)
        {
            var s = Active;
            if (s == null) { marker = null; return; }
            var q = ToSketch(s, partPos);
            try
            {
                if (marker != null && marker.SetCoords(q[0], q[1], 0)) { doc.Redraw(); return; }
            }
            catch (Exception) { marker = null; }
            var sm = doc.Sketches;
            bool oldDb = sm.AddToDB;
            sm.AddToDB = true;
            try { marker = sm.CreatePoint(q[0], q[1], 0); }
            finally { sm.AddToDB = oldDb; }
            doc.Redraw();
        }

        public void RemoveMarker()
        {
            if (marker == null) return;
            try
            {
                doc.ClearSelection();
                if (marker.Select4(false, doc.Selection.CreateSelectData())) doc.Doc.EditDelete();
            }
            catch (Exception) { }
            marker = null;
            doc.ClearSelection();
        }

        // ------------------------------------------------------------------ temporary section display

        /// <summary>
        /// Draw the section outline directly in the open 2D sketch, as the user's own geometry (user 2026-10-26: the
        /// earlier temporary 3D sketch meant leaving and re-entering the user's sketch, which SolidWorks did not always
        /// keep; and an outline deleted on close "can't be used for anything"). Splines within `tolMm` of the section,
        /// split into short ones (a spline's cost grows steeply with its point count), added without inference;
        /// optionally construction geometry and Fixed. An outline drawn earlier in the same sketch in this session
        /// is replaced; otherwise outlines stay until the user deletes them (Remove outline).
        /// </summary>
        public void ShowSection(PlaneSection sec, double tolMm, bool construction = true, bool fix = true)
        {
            RemoveMarker();
            if (doc.OutlineSketch != null && doc.OutlineSketch == ActiveName) RemoveOutline();
            doc.OutlineEntities = null;
            doc.OutlineSketch = null;
            var s = Active;
            if (s == null)
                throw new Core.Jobs.Res3dException("No sketch is open.", "Edit or create a sketch on a plane through the scan, then press Compute section.");
            var sm = doc.Sketches;
            bool oldDb = sm.AddToDB;
            var made = new List<object>();
            sm.AddToDB = true;
            try
            {
                foreach (var line in sec.Lines)
                {
                    var pts = new List<Vec3>(line.Points);
                    if (line.Closed) pts.Add(pts[0]);
                    var keep = Core.Trace.Tracer.DouglasPeucker(pts, tolMm);
                    keep.Add(0); keep.Add(pts.Count - 1);
                    keep.Sort();
                    var simple = new List<Vec3>();
                    for (int i = 0; i < keep.Count; i++) if (i == 0 || keep[i] != keep[i - 1]) simple.Add(pts[keep[i]]);
                    for (int start = 0; start < simple.Count - 1; start += ChunkPoints - 1)
                    {
                        int end = Math.Min(simple.Count - 1, start + ChunkPoints - 1);
                        var arr = new double[3 * (end - start + 1)];
                        for (int i = start; i <= end; i++) { var q = ToSketch(s, simple[i]); arr[3 * (i - start)] = q[0]; arr[3 * (i - start) + 1] = q[1]; }
                        var seg = end - start == 1 ? sm.CreateLine(arr[0], arr[1], 0, arr[3], arr[4], 0) : sm.CreateSpline2(arr, false) as SketchSegment;
                        if (seg == null) continue;
                        if (construction) seg.ConstructionGeometry = true;
                        made.Add(seg);
                    }
                }
            }
            finally { sm.AddToDB = oldDb; }
            if (fix) Fix(made);
            doc.OutlineEntities = made;
            doc.OutlineSketch = ((IFeature)s).Name;
            doc.ClearSelection();
            doc.Redraw();
            Log.Info("section outline: " + made.Count + (construction ? " construction" : "") + " spline(s) in " + doc.OutlineSketch + ", tolerance " + tolMm.ToString("0.###") + " mm");
        }

        private const int ChunkPoints = 200;

        /// <summary>
        /// Delete the outline drawn last (Remove outline), opening its sketch briefly if another, or none, is open.
        /// </summary>
        public void RemoveOutline()
        {
            RemoveMarker();
            var outline = doc.OutlineEntities;
            if (outline != null && outline.Count > 0 && doc.OutlineSketch != null && doc.FindFeature(doc.OutlineSketch) != null)
            {
                string active = ActiveName;
                bool other = active != doc.OutlineSketch;
                string back = other ? doc.LeaveSketch() : null;
                if (other) doc.ReenterSketch(doc.OutlineSketch);
                try { DeleteEntities(outline); }
                catch (Exception e) { Log.Error("remove section outline", e); }
                if (other)
                {
                    doc.LeaveSketch();
                    if (back != null) doc.ReenterSketch(back);
                }
            }
            doc.OutlineEntities = null;
            doc.OutlineSketch = null;
        }

        /// <summary>The snap marker and any temporary section sketch left by earlier versions (Finish, add-in unload).</summary>
        public void RemoveTemporarySketches()
        {
            RemoveMarker();
            var temps = new List<IFeature>();
            foreach (var f in doc.AllFeatures())
                if (IsTemporary(f.Name)) temps.Add(f);
            if (temps.Count == 0) return;
            string userSketch = doc.LeaveSketch();
            foreach (var f in temps) doc.Delete(f);
            doc.ReenterSketch(userSketch);
        }

        public int CountTemporary()
        {
            int n = 0;
            foreach (var f in doc.AllFeatures()) if (IsTemporary(f.Name)) n++;
            return n;
        }

    }
}
