// Visual feedback while fitting (user request 2026-10-26): picked points as balls and the fitted plane as a
// translucent plate the size of the fitted region. Neither is part of the document, so nothing is saved and
// nothing can reference them:
//   * balls are triad manipulators showing only their origin ball (as AsmFEA's point markers);
//   * the plate is temporary sheet bodies (IModeler) shown with IBody2.Display3. SolidWorks ignores transparency on
//     temporary bodies (tried: body and face material values, before and after Display3, colour -1), so the plate
//     is drawn as stripes with gaps of the same width plus a thin frame: about 50 % see-through.
using System;
using System.Collections.Generic;
using RES3D.Core.Math3;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SolidWorks.Interop.swpublished;

namespace RES3D.AddIn.Sw
{
    public sealed class SwPreview : IDisposable
    {
        private const int BallColour = 0x0000FF;      // COLORREF 0xBBGGRR: red
        public const int Orange = 0x0080FF, Blue = 0xFF8000;   // COLORREF 0xBBGGRR
        private readonly int PlateColour;
        private readonly SwDoc doc;
        private readonly List<IManipulator> balls = new List<IManipulator>();
        private readonly NoDrag handler = new NoDrag();
        private readonly List<IBody2> plate = new List<IBody2>();
        private const int Stripes = 14;

        public SwPreview(SwDoc doc, int plateColour = Orange) { this.doc = doc; PlateColour = plateColour; }

        // ------------------------------------------------------------------ points

        /// <summary>Show a ball at each point (part mm); balls already shown are moved, spare ones removed.</summary>
        public void ShowPoints(IList<Vec3> pts)
        {
            var mu = doc.Math;
            for (int i = 0; i < pts.Count; i++)
            {
                var origin = (MathPoint)mu.CreatePoint(Units.ToM(pts[i]));
                if (i < balls.Count)
                {
                    try
                    {
                        var t = (ITriadManipulator)balls[i].GetSpecificManipulator();
                        t.Origin = origin;
                        t.UpdatePosition();
                        continue;
                    }
                    catch (Exception) { balls[i] = null; }
                }
                var m = MakeBall(origin);
                if (i < balls.Count) balls[i] = m; else balls.Add(m);
            }
            while (balls.Count > pts.Count) { Remove(balls[balls.Count - 1]); balls.RemoveAt(balls.Count - 1); }
            balls.RemoveAll(b => b == null);
            if (highlighted >= balls.Count) highlighted = -1;
            Highlight(highlighted);
        }

        private const int OriginOnly = (int)(swTriadManipulatorDoNotShow_e.swTriadManipulatorDoNotShowXAxis | swTriadManipulatorDoNotShow_e.swTriadManipulatorDoNotShowYAxis
                          | swTriadManipulatorDoNotShow_e.swTriadManipulatorDoNotShowZAxis | swTriadManipulatorDoNotShow_e.swTriadManipulatorDoNotShowXYPlane
                          | swTriadManipulatorDoNotShow_e.swTriadManipulatorDoNotShowYZPlane | swTriadManipulatorDoNotShow_e.swTriadManipulatorDoNotShowZXPlane
                          | swTriadManipulatorDoNotShow_e.swTriadManipulatorDoNotShowXYRING | swTriadManipulatorDoNotShow_e.swTriadManipulatorDoNotShowYZRING
                          | swTriadManipulatorDoNotShow_e.swTriadManipulatorDoNotShowZXRING);
        private const int WithAxes = (int)(swTriadManipulatorDoNotShow_e.swTriadManipulatorDoNotShowXYPlane
                          | swTriadManipulatorDoNotShow_e.swTriadManipulatorDoNotShowYZPlane | swTriadManipulatorDoNotShow_e.swTriadManipulatorDoNotShowZXPlane
                          | swTriadManipulatorDoNotShow_e.swTriadManipulatorDoNotShowXYRING | swTriadManipulatorDoNotShow_e.swTriadManipulatorDoNotShowYZRING
                          | swTriadManipulatorDoNotShow_e.swTriadManipulatorDoNotShowZXRING);
        private int highlighted = -1;

        /// <summary>
        /// Mark one ball (the point selected in a list), or none with -1. SolidWorks ignored the ball colour in 2025,
        /// so the marked ball also shows its coloured axes (as AsmFEA's point markers); the colour is still tried.
        /// </summary>
        public void Highlight(int index)
        {
            highlighted = index;
            for (int i = 0; i < balls.Count; i++)
            {
                try
                {
                    var t = (ITriadManipulator)balls[i].GetSpecificManipulator();
                    t.DoNotShow = i == index ? WithAxes : OriginOnly;
                    try { t.SetColorRefAtIndex((int)swTriadManipulatorControlPoints_e.swTriadManipulatorOrigin, i == index ? HighlightColour : BallColour); } catch (Exception) { }
                    t.UpdatePosition();
                }
                catch (Exception) { }
            }
            doc.Redraw();
        }

        private const int HighlightColour = 0x00FF00;   // green

        private IManipulator MakeBall(MathPoint origin)
        {
            var mgr = (IModelViewManager)doc.Doc.ModelViewManager;
            var m = mgr.CreateManipulator((int)swManipulatorType_e.swTriadManipulator, handler) as IManipulator;
            if (m == null) return null;
            var t = (ITriadManipulator)m.GetSpecificManipulator();
            t.Origin = origin;
            t.DoNotShow = (int)(swTriadManipulatorDoNotShow_e.swTriadManipulatorDoNotShowXAxis | swTriadManipulatorDoNotShow_e.swTriadManipulatorDoNotShowYAxis
                              | swTriadManipulatorDoNotShow_e.swTriadManipulatorDoNotShowZAxis | swTriadManipulatorDoNotShow_e.swTriadManipulatorDoNotShowXYPlane
                              | swTriadManipulatorDoNotShow_e.swTriadManipulatorDoNotShowYZPlane | swTriadManipulatorDoNotShow_e.swTriadManipulatorDoNotShowZXPlane
                              | swTriadManipulatorDoNotShow_e.swTriadManipulatorDoNotShowXYRING | swTriadManipulatorDoNotShow_e.swTriadManipulatorDoNotShowYZRING
                              | swTriadManipulatorDoNotShow_e.swTriadManipulatorDoNotShowZXRING);
            try { t.SetColorRefAtIndex((int)swTriadManipulatorControlPoints_e.swTriadManipulatorOrigin, BallColour); } catch (Exception) { }
            t.UpdatePosition();
            m.Show(doc.Doc);
            return m;
        }

        private static void Remove(IManipulator m)
        {
            if (m == null) return;
            try { m.Remove(); } catch (Exception) { /* the document may have closed */ }
        }

        public void ClearPoints()
        {
            foreach (var b in balls) Remove(b);
            balls.Clear();
            doc.Redraw();
        }

        // ------------------------------------------------------------------ plane

        /// <summary>
        /// Show `plane` (part mm) as a translucent plate covering `extent` (points on the fitted region, part mm)
        /// plus a margin, replacing the plate shown before. Returns false when SolidWorks would not draw it.
        /// </summary>
        public bool ShowPlane(Plane3 plane, IList<Vec3> extent)
        {
            HidePlane();
            if (extent == null || extent.Count == 0) return false;
            var n = plane.Normal.Normalized();
            var u = n.AnyPerpendicular().Normalized();
            var v = Vec3.Cross(n, u);
            // rectangle round the region in the plane, from its principal directions so a long face gets a long plate
            double uMin = double.MaxValue, uMax = double.MinValue, vMin = double.MaxValue, vMax = double.MinValue;
            var c = Vec3.Zero;
            foreach (var p in extent) c += p;
            c = plane.Project(c * (1.0 / extent.Count));
            double suu = 0, suv = 0, svv = 0;
            foreach (var p in extent)
            {
                var d = p - c;
                double a = Vec3.Dot(d, u), b = Vec3.Dot(d, v);
                suu += a * a; suv += a * b; svv += b * b;
            }
            double angle = 0.5 * Math.Atan2(2 * suv, suu - svv);
            var u2 = u * Math.Cos(angle) + v * Math.Sin(angle);
            var v2 = Vec3.Cross(n, u2);
            foreach (var p in extent)
            {
                var d = p - c;
                double a = Vec3.Dot(d, u2), b = Vec3.Dot(d, v2);
                uMin = Math.Min(uMin, a); uMax = Math.Max(uMax, a); vMin = Math.Min(vMin, b); vMax = Math.Max(vMax, b);
            }
            double size = Math.Max(uMax - uMin, vMax - vMin);
            double margin = Math.Max(2, 0.15 * size);
            uMin -= margin; uMax += margin; vMin -= margin; vMax += margin;

            try
            {
                var modeler = (IModeler)doc.App.GetModeler();
                var surf = modeler.CreatePlanarSurface2(Units.ToM(c), new[] { n.X, n.Y, n.Z }, new[] { u2.X, u2.Y, u2.Z }) as ISurface;
                if (surf == null) return false;
                Func<double, double, double, double, bool> rect = (u0, u1, v0, v1) =>
                {
                    var sheet = modeler.CreateSheetFromSurface(surf, new[] { u0 / Units.MmPerM, u1 / Units.MmPerM, v0 / Units.MmPerM, v1 / Units.MmPerM }) as IBody2;
                    if (sheet == null) return false;
                    sheet.Display3(doc.Doc, PlateColour, (int)swTempBodySelectOptions_e.swTempBodySelectOptionNone);
                    plate.Add(sheet);
                    return true;
                };
                // stripes across the long direction, then the frame
                double w = (uMax - uMin) / (2 * Stripes - 1);
                for (int k = 0; k < Stripes; k++)
                    if (!rect(uMin + 2 * k * w, uMin + (2 * k + 1) * w, vMin, vMax)) return false;
                double f = Math.Max(0.004 * size, 0.1);
                rect(uMin, uMax, vMin, vMin + f);
                rect(uMin, uMax, vMax - f, vMax);
                doc.Redraw();
                return true;
            }
            catch (Exception e)
            {
                Log.Error("fit preview plate", e);
                return false;
            }
        }

        public void HidePlane()
        {
            if (plate.Count == 0) return;
            foreach (var b in plate)
                try { b.Hide(doc.Doc); } catch (Exception) { }
            plate.Clear();
            doc.Redraw();
        }

        public void Dispose()
        {
            ClearPoints();
            HidePlane();
        }

        /// <summary>The balls are markers only: no dragging, no deleting.</summary>
        [System.Runtime.InteropServices.ComVisible(true)]
        public class NoDrag : SwManipulatorHandler2
        {
            public void OnDirectionFlipped(object pManipulator) { }
            public bool OnHandleLmbSelected(object pManipulator) { return false; }
            public void OnHandleSelected(object pManipulator, int handleIndex) { }
            public void OnUpdateDrag(object pManipulator, int handleIndex, object newPosMathPt) { }
            public void OnEndDrag(object pManipulator, int handleIndex) { }
            public void OnEndNoDrag(object pManipulator, int handleIndex) { }
            public void OnHandleRmbSelected(object pManipulator, int handleIndex) { }
            public bool OnDelete(object pManipulator) { return false; }
            public bool OnDoubleValueChanged(object pManipulator, int id, ref double value) { return false; }
            public bool OnStringValueChanged(object pManipulator, int id, ref string value) { return false; }
            public void OnItemSetFocus(object pManipulator, int id) { }
        }
    }
}
