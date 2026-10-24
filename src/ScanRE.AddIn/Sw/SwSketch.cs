// Sketch-side operations for Section & Snap and Trace (spec §4-5): the active sketch's plane, fixed native
// points and splines, a moving snap marker, and a TEMPORARY 3D sketch that shows the section.
// Entities are added with AddToDB so SolidWorks' own inference can never relate them to the scan.
using System;
using System.Collections.Generic;
using ScanRE.Core.Math3;
using ScanRE.Core.Section;
using SolidWorks.Interop.sldworks;

namespace ScanRE.AddIn.Sw
{
    public sealed class SwSketch
    {
        public const string TempPrefix = "_ScanRE ";
        public const string SectionSketchName = TempPrefix + "Section (temporary)";
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
        public List<object> CreateFixedPoints(IList<Vec3> partPoints)
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
                Fix(made);
            }
            finally { sm.AddToDB = oldDb; }
            return made;
        }

        /// <summary>A fixed through-point spline in the active sketch.</summary>
        public object CreateFixedSpline(IList<Vec3> partPoints, bool closed)
        {
            var s = Active;
            if (s == null || partPoints.Count < 2) return null;
            var sm = doc.Sketches;
            bool oldDb = sm.AddToDB;
            sm.AddToDB = true;
            try
            {
                // closed: points given once, periodic=true closes it (to verify in SW: INVESTIGATION Q5)
                var arr = new double[3 * partPoints.Count];
                for (int i = 0; i < partPoints.Count; i++)
                {
                    var q = ToSketch(s, partPoints[i]);
                    arr[3 * i] = q[0]; arr[3 * i + 1] = q[1]; arr[3 * i + 2] = 0;
                }
                var seg = sm.CreateSpline2(arr, closed);
                if (seg != null) Fix(new List<object> { seg });
                return seg;
            }
            finally { sm.AddToDB = oldDb; }
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
        /// Show the section as construction lines in a temporary 3D sketch, then return to the user's sketch.
        /// The polylines are simplified to `tolMm` so thousands of segments don't slow SolidWorks down.
        /// </summary>
        public void ShowSection(PlaneSection sec, double tolMm)
        {
            RemoveMarker();
            RemoveSection();
            string userSketch = doc.LeaveSketch();   // 2D or 3D: the section must never land in it
            try { Draw(sec, tolMm); }
            finally { doc.ReenterSketch(userSketch); }
        }

        private void Draw(PlaneSection sec, double tolMm)
        {
            var sm = doc.Sketches;
            var sk = doc.Begin3DSketch();
            bool oldDb = sm.AddToDB;
            sm.AddToDB = true;
            sm.DisplayWhenAdded = false;
            try
            {
                foreach (var line in sec.Lines)
                {
                    var pts = new List<Vec3>(line.Points);
                    if (line.Closed) pts.Add(pts[0]);
                    var keep = Core.Trace.Tracer.DouglasPeucker(pts, tolMm);
                    keep.Add(0); keep.Add(pts.Count - 1);
                    keep.Sort();
                    for (int i = 1; i < keep.Count; i++)
                    {
                        if (keep[i] == keep[i - 1]) continue;
                        var a = Units.ToM(pts[keep[i - 1]]); var b = Units.ToM(pts[keep[i]]);
                        var seg = sm.CreateLine(a[0], a[1], a[2], b[0], b[1], b[2]);
                        if (seg != null) seg.ConstructionGeometry = true;
                    }
                }
            }
            finally
            {
                sm.AddToDB = oldDb;
                sm.DisplayWhenAdded = true;   // SolidWorks' default (the API has no getter to restore from)
                sm.Insert3DSketch(true);
            }
            {
                var f = (IFeature)sk;
                f.Name = doc.UniqueName(SectionSketchName);
                f.Description = "ScanRE temporary section display; deleted automatically.";
            }
        }

        /// <summary>Delete every temporary ScanRE sketch (section displays), returning to the user's sketch if one was open.</summary>
        public void RemoveSection()
        {
            var temps = new List<IFeature>();
            foreach (var f in doc.AllFeatures())
                if (f.Name.StartsWith(TempPrefix, StringComparison.Ordinal)) temps.Add(f);
            if (temps.Count == 0) return;
            RemoveMarker();
            string userSketch = doc.LeaveSketch();
            foreach (var f in temps) doc.Delete(f);
            doc.ReenterSketch(userSketch);
        }

        public int CountTemporary()
        {
            int n = 0;
            foreach (var f in doc.AllFeatures()) if (f.Name.StartsWith(TempPrefix, StringComparison.Ordinal)) n++;
            return n;
        }

    }
}
