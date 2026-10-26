// Native reference planes and axes, defined independently of the scan (spec principle 3, DECISIONS D8):
//  * exactly parallel to a base plane -> an ordinary offset plane from that base plane;
//  * otherwise (and always for Scan-* planes) -> a hidden 3D "definition sketch" holding a FIXED point and a
//    FIXED normal line at exact numeric coordinates, and a plane coincident with the point and perpendicular
//    to the line.
// Both survive Finish (no reference to the mesh) and are editable by hand. Axes use a fixed sketch line.
// Any sketch the user is editing is left first and re-entered afterwards, so nothing lands in it.
using System;
using System.Collections.Generic;
using RES3D.Core.Align;
using RES3D.Core.Fit;
using RES3D.Core.Math3;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace RES3D.AddIn.Sw
{
    public sealed class SwPlanes
    {
        public const string DefPrefix = "RES3D-Def ";
        private const double DefLineMm = 10;
        private readonly SwDoc doc;

        public SwPlanes(SwDoc doc) { this.doc = doc; }

        /// <summary>
        /// Create a reference plane in part coordinates (mm). `forceDefinitionSketch`: always use a definition
        /// sketch (Scan-* planes, so a re-solve can move them in place even when they are exactly parallel).
        /// </summary>
        public IFeature CreatePlane(string name, Plane3 plane, string description, bool forceDefinitionSketch = false)
        {
            string userSketch = doc.LeaveSketch();
            try
            {
                IFeature feat = null;
                if (!forceDefinitionSketch)
                    foreach (BasePlane b in Enum.GetValues(typeof(BasePlane)))
                    {
                        var N = BasePlanes.Normal(b);
                        if (Math.Abs(Math.Abs(Vec3.Dot(plane.Normal, N)) - 1) > 1e-12) continue;
                        feat = OffsetPlane(b, Vec3.Dot(plane.Origin, N));
                        break;
                    }
                if (feat == null)
                {
                    var sketch = DefinitionSketch(name, plane.Origin, plane.Normal);
                    feat = PlaneFromSketch(sketch);
                    if (feat == null) doc.Delete(sketch.Feature);
                }
                if (feat == null)
                    throw new Core.Jobs.Res3dException("SolidWorks did not create the plane " + name + ".",
                        "Rebuild the part (Ctrl+Q) and try again.");
                doc.Rename(feat, name);
                feat.Description = description;
                doc.ClearSelection();
                return feat;
            }
            finally { doc.ReenterSketch(userSketch); }
        }

        private IFeature OffsetPlane(BasePlane b, double offsetMm)
        {
            var bp = doc.BasePlane(b);
            if (bp == null) return null;
            doc.ClearSelection();
            bp.Select2(false, 0);
            object f;
            if (Math.Abs(offsetMm) < 1e-9)
                f = doc.Features.InsertRefPlane((int)swRefPlaneReferenceConstraints_e.swRefPlaneReferenceConstraint_Coincident, 0, 0, 0, 0, 0);
            else
            {
                int c = (int)swRefPlaneReferenceConstraints_e.swRefPlaneReferenceConstraint_Distance;
                if (offsetMm < 0) c |= (int)swRefPlaneReferenceConstraints_e.swRefPlaneReferenceConstraint_OptionFlip;
                f = doc.Features.InsertRefPlane(c, Units.ToM(Math.Abs(offsetMm)), 0, 0, 0, 0);
            }
            return f as IFeature;
        }

        // ------------------------------------------------------------------ definition sketches

        private sealed class DefSketch
        {
            public IFeature Feature;
            public SketchPoint Point;
            public SketchSegment Line;
        }

        /// <summary>A hidden 3D sketch with a fixed point at `origin` and a fixed line from it along `dir`. No sketch may be open.</summary>
        private DefSketch DefinitionSketch(string forName, Vec3 origin, Vec3 dir)
        {
            var sm = doc.Sketches;
            var sketch = doc.Begin3DSketch();
            bool oldDb = sm.AddToDB;
            sm.AddToDB = true;   // no inference: nothing may snap to the scan
            SketchPoint pt;
            SketchSegment line;
            try
            {
                var o = Units.ToM(origin);
                var e = Units.ToM(origin + dir.Normalized() * DefLineMm);
                line = sm.CreateLine(o[0], o[1], o[2], e[0], e[1], e[2]);
                pt = sm.CreatePoint(o[0], o[1], o[2]);
                line.ConstructionGeometry = true;
                doc.ClearSelection();
                var data = doc.Selection.CreateSelectData();
                line.Select4(false, data);
                pt.Select4(true, data);
                doc.Doc.SketchAddConstraints("sgFIXED");
            }
            finally
            {
                sm.AddToDB = oldDb;
                sm.Insert3DSketch(true);   // close
            }
            var feat = (IFeature)sketch;
            doc.Rename(feat, DefPrefix + forName);
            feat.Description = "RE-Scan3D plane definition: fixed point + normal line at exact coordinates (no reference to the scan).";
            doc.Hide(feat);
            return new DefSketch { Feature = feat, Point = pt, Line = line };
        }

        private IFeature PlaneFromSketch(DefSketch d)
        {
            doc.ClearSelection();
            var data = doc.Selection.CreateSelectData();
            data.Mark = 0;
            d.Point.Select4(false, data);
            var data2 = doc.Selection.CreateSelectData();
            data2.Mark = 1;
            d.Line.Select4(true, data2);
            var f = doc.Features.InsertRefPlane(
                (int)swRefPlaneReferenceConstraints_e.swRefPlaneReferenceConstraint_Coincident, 0,
                (int)swRefPlaneReferenceConstraints_e.swRefPlaneReferenceConstraint_Perpendicular, 0, 0, 0) as IFeature;
            doc.ClearSelection();
            return f;
        }

        /// <summary>A reference axis along `dir` through `point` (mm), from a definition sketch line.</summary>
        public IFeature CreateAxis(string name, Vec3 point, Vec3 dir, double lengthMm, string description)
        {
            string userSketch = doc.LeaveSketch();
            try
            {
                var sm = doc.Sketches;
                var sketch = doc.Begin3DSketch();
                bool oldDb = sm.AddToDB;
                sm.AddToDB = true;
                SketchSegment line;
                try
                {
                    double half = Math.Max(5, lengthMm / 2);
                    var a = Units.ToM(point - dir.Normalized() * half);
                    var b = Units.ToM(point + dir.Normalized() * half);
                    line = sm.CreateLine(a[0], a[1], a[2], b[0], b[1], b[2]);
                    doc.ClearSelection();
                    var data = doc.Selection.CreateSelectData();
                    // fix the two END POINTS: a fixed 3D line alone, or the line selected together with its ends,
                    // leaves the sketch under-defined (measured in SolidWorks 2025)
                    var sl = (SketchLine)line;
                    ((SketchPoint)sl.GetStartPoint2()).Select4(false, data);
                    ((SketchPoint)sl.GetEndPoint2()).Select4(true, data);
                    doc.Doc.SketchAddConstraints("sgFIXED");
                }
                finally
                {
                    sm.AddToDB = oldDb;
                    sm.Insert3DSketch(true);
                }
                var sf = (IFeature)sketch;
                doc.Rename(sf, DefPrefix + name);
                sf.Description = "RE-Scan3D axis definition: fixed line at exact coordinates (no reference to the scan).";
                // the new axis is whichever RefAxis wasn't there before (the rollback bar may be anywhere)
                var before = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var f in doc.AllFeatures()) before.Add(f.Name);
                doc.ClearSelection();
                line.Select4(false, doc.Selection.CreateSelectData());
                doc.Doc.InsertAxis2(true);
                IFeature axis = null;
                foreach (var f in doc.AllFeatures())
                    if (f.GetTypeName2() == "RefAxis" && !before.Contains(f.Name)) { axis = f; break; }
                if (axis == null)
                {
                    doc.Delete(sf);
                    throw new Core.Jobs.Res3dException("SolidWorks did not create the axis " + name + ".",
                        "Rebuild the part (Ctrl+Q) and try again; the fit itself is recorded.");
                }
                doc.Rename(axis, name);
                axis.Description = description;
                doc.Hide(sf);
                doc.ClearSelection();
                return axis;
            }
            finally { doc.ReenterSketch(userSketch); }
        }

        /// <summary>The definition sketch an RE-Scan3D plane was built from: found through the plane's parents, so
        /// renaming either feature doesn't break the link.</summary>
        /// <summary>A reference plane's position (part mm), read from SolidWorks.</summary>
        public Plane3 PlaneOf(IFeature planeFeature)
        {
            var rp = (IRefPlane)planeFeature.GetSpecificFeature2();
            var t = (MathTransform)rp.Transform;
            var o = doc.TransformPoint(t, new double[] { 0, 0, 0 });
            var n = doc.TransformVector(t, new double[] { 0, 0, 1 });
            return new Plane3(Units.ToMm(o), new Vec3(n[0], n[1], n[2]));
        }

        /// <summary>Delete a plane made by the add-in (and its definition sketch) when nothing is built on it.
        /// Returns false when it was kept because other features use it.</summary>
        public bool DeleteIfUnused(string planeName)
        {
            var plane = doc.FindFeature(planeName);
            if (plane == null) return true;
            if (doc.Children(plane).Count > 0) return false;
            var def = DefinitionOf(plane);
            doc.Delete(plane);
            if (def != null && doc.FindFeature(def.Name) != null) doc.Delete(def);
            return true;
        }

        private IFeature DefinitionOf(IFeature plane)
        {
            var parents = plane.GetParents() as object[];
            if (parents != null)
                foreach (var o in parents)
                {
                    var f = o as IFeature;
                    if (f != null && f.GetTypeName2() == "3DProfileFeature") return f;
                }
            return null;
        }

        /// <summary>
        /// Move an existing def-sketch plane (Scan-Top etc. after a re-solve): unfix, move, refix. The plane
        /// feature itself is untouched, so anything built on it simply follows. False if it isn't one.
        /// </summary>
        public bool UpdatePlane(string planeName, Plane3 plane)
        {
            var pf = doc.FindFeature(planeName);
            var def = pf == null ? null : DefinitionOf(pf);
            var sketch = def == null ? null : def.GetSpecificFeature2() as ISketch;
            if (sketch == null) return false;
            var segs = sketch.GetSketchSegments() as object[];
            var line = segs != null && segs.Length > 0 ? segs[0] as SketchLine : null;
            if (line == null) return false;
            string userSketch = doc.LeaveSketch();
            try
            {
                var start = (SketchPoint)line.GetStartPoint2();
                var end = (SketchPoint)line.GetEndPoint2();
                // the separate point sits on the line's start; the line's own end points are also in
                // GetSketchPoints2, so tell them apart by position before anything moves (review C1)
                var oldEnd = new Vec3(end.X, end.Y, end.Z);
                doc.ClearSelection();
                def.Select2(false, 0);
                doc.Doc.EditSketch();
                var sm = doc.Sketches;
                bool oldDb = sm.AddToDB;
                sm.AddToDB = true;
                try
                {
                    var pts = sketch.GetSketchPoints2() as object[] ?? new object[0];
                    var data = doc.Selection.CreateSelectData();
                    doc.ClearSelection();
                    ((SketchSegment)line).Select4(true, data);
                    foreach (var o in pts) ((SketchPoint)o).Select4(true, data);
                    doc.Doc.SketchConstraintsDelAll();
                    var o2 = Units.ToM(plane.Origin);
                    var e2 = Units.ToM(plane.Origin + plane.Normal.Normalized() * DefLineMm);
                    foreach (var o in pts)
                    {
                        var p = (SketchPoint)o;
                        bool isEnd = (new Vec3(p.X, p.Y, p.Z) - oldEnd).Length < 1e-9;
                        if (isEnd) p.SetCoords(e2[0], e2[1], e2[2]);
                        else p.SetCoords(o2[0], o2[1], o2[2]);
                    }
                    start.SetCoords(o2[0], o2[1], o2[2]);
                    end.SetCoords(e2[0], e2[1], e2[2]);
                    doc.ClearSelection();
                    ((SketchSegment)line).Select4(true, data);
                    foreach (var o in pts) ((SketchPoint)o).Select4(true, data);
                    doc.Doc.SketchAddConstraints("sgFIXED");
                }
                finally
                {
                    sm.AddToDB = oldDb;
                    sm.Insert3DSketch(true);
                }
                doc.ClearSelection();
                return true;
            }
            finally { doc.ReenterSketch(userSketch); }
        }

        // ------------------------------------------------------------------ existing references (for snapping)

        /// <summary>Reference planes and axes in the part, as directions in part coordinates (mm).</summary>
        public List<ReferenceDir> References()
        {
            var list = new List<ReferenceDir>();
            foreach (var f in doc.AllFeatures())
            {
                string t = f.GetTypeName2();
                try
                {
                    if (t == "RefPlane")
                    {
                        var rp = f.GetSpecificFeature2() as IRefPlane;
                        if (rp == null) continue;
                        // the plane's transform maps plane-local (XY plane, +Z normal) to model
                        var xf = rp.Transform;
                        var o = doc.TransformPoint(xf, new double[] { 0, 0, 0 });
                        var n = doc.TransformVector(xf, new double[] { 0, 0, 1 });
                        list.Add(new ReferenceDir { Name = f.Name, Dir = new Vec3(n[0], n[1], n[2]).Normalized(), Point = Units.ToMm(o) });
                    }
                    else if (t == "RefAxis")
                    {
                        var ra = f.GetSpecificFeature2() as IRefAxis;
                        var p = ra == null ? null : ra.GetRefAxisParams() as double[];
                        if (p == null || p.Length < 6) continue;
                        var a = Units.ToMm(p, 0); var b = Units.ToMm(p, 3);
                        list.Add(new ReferenceDir { Name = f.Name, Dir = (b - a).Normalized(), Point = a, IsAxis = true });
                    }
                }
                catch (Exception) { /* a broken reference: skip it */ }
            }
            return list;
        }
    }
}
