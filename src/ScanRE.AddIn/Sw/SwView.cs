// Picking (spec §4, open question "Picking"): map the mouse position in the SolidWorks view to a ray in
// part space, then ray-cast the add-in's full-resolution mesh — never the display body.
// The view transform (IModelView.Transform) maps model space to view space including the zoom scale. Two
// details are uncertain from the documentation alone and are settled at run time:
//   * whether view Y runs up (needs FrameHeight - y) or down like Windows;
//   * the sign of the viewing direction.
// MouseSelectNotify reports both the pixel and the model point SolidWorks picked; every click checks which
// convention puts that point on our ray and keeps the better one (Calibration). INVESTIGATION.md Q3.
using System;
using ScanRE.Core.Math3;
using SolidWorks.Interop.sldworks;

namespace ScanRE.AddIn.Sw
{
    public sealed class SwView
    {
        private readonly SwDoc doc;
        private readonly IModelView view;

        /// <summary>Learned once per session and shared by all views.</summary>
        public static bool FlipY = true;
        /// <summary>Viewing direction sign: view -Z into the screen (default) or +Z.</summary>
        public static bool FlipDir;
        public static bool Calibrated;
        public static string CalibrationNote = "not yet calibrated (click once on the scan or any model geometry)";

        public SwView(SwDoc doc, IModelView view) { this.doc = doc; this.view = view; }

        public IModelView View { get { return view; } }

        /// <summary>Ray through window pixel (x, y), in part coordinates (mm).</summary>
        public Ray3 RayAt(int x, int y) { return RayAt(x, y, FlipY); }

        private Ray3 RayAt(int x, int y, bool flipY)
        {
            var xf = view.Transform;                       // model -> view (pixels)
            var inv = (MathTransform)xf.Inverse();
            double vy = flipY ? view.FrameHeight - y : y;
            var p = doc.TransformPoint(inv, new double[] { x, vy, 0 });
            if (view.HasPerspective())
            {
                var eye = view.GetEyePoint() as double[];
                if (eye != null && eye.Length >= 3)
                {
                    var e = Units.ToMm(eye);
                    return new Ray3(e, Units.ToMm(p) - e);
                }
            }
            // orthographic: the view's -Z (into the screen) in model space
            var d = doc.TransformVector(inv, new double[] { 0, 0, FlipDir ? 1 : -1 });
            var dir = new Vec3(d[0], d[1], d[2]).Normalized();
            var o = Units.ToMm(p);
            return new Ray3(o - dir * 1e5, dir);         // start well in front of everything (100 m)
        }

        /// <summary>
        /// Use one SolidWorks pick (pixel + model point) to settle the Y convention: keep whichever ray
        /// passes closer to the point SolidWorks reported.
        /// </summary>
        public void Calibrate(int x, int y, Vec3 modelPointMm)
        {
            if (Calibrated) return;
            if (modelPointMm.Length == 0) return;          // empty-space click: SolidWorks reports 0,0,0
            double dA = DistanceToRay(RayAt(x, y, true), modelPointMm);
            double dB = DistanceToRay(RayAt(x, y, false), modelPointMm);
            FlipY = dA <= dB;
            // the picked point must lie AHEAD of the ray origin; otherwise the view direction is reversed (review I8)
            var r = RayAt(x, y, FlipY);
            if (!view.HasPerspective() && Vec3.Dot(modelPointMm - r.Origin, r.Dir) < 0) FlipDir = !FlipDir;
            Calibrated = Math.Min(dA, dB) < 1.0;            // within a millimetre: trust it
            CalibrationNote = string.Format("FlipY={0} FlipDir={1}: ray-to-pick distance {2:0.###} mm (other convention {3:0.###} mm){4}",
                                            FlipY, FlipDir, Math.Min(dA, dB), Math.Max(dA, dB),
                                            Calibrated ? "" : " - NOT trusted: the view mapping may not be in pixels; please report with the log");
            Log.Info("picking calibration: " + CalibrationNote);
        }

        private static double DistanceToRay(Ray3 r, Vec3 p)
        {
            var d = p - r.Origin;
            return (d - r.Dir * Vec3.Dot(d, r.Dir)).Length;
        }

        /// <summary>Approximate size of one screen pixel at the model (mm): snap radii follow the zoom.</summary>
        public double PixelSizeMm()
        {
            var inv = (MathTransform)view.Transform.Inverse();
            var a = doc.TransformPoint(inv, new double[] { 0, 0, 0 });
            var b = doc.TransformPoint(inv, new double[] { 1, 0, 0 });
            return (Units.ToMm(b) - Units.ToMm(a)).Length;
        }
    }

    /// <summary>
    /// Subscribes to a view's mouse events while a ScanRE pick mode is active and forwards them. SolidWorks'
    /// own handling continues (handlers return 0), so its tools are not disturbed.
    /// Click comes from left button down+up without a drag (it fires on empty sketch space too, where
    /// SolidWorks selects nothing); Picked is SolidWorks' own selection, used to calibrate the ray.
    /// </summary>
    public sealed class MouseHook : IDisposable
    {
        private Mouse mouse;
        private int downX = int.MinValue, downY;
        public event Action<int, int> Move;
        public event Action<int, int> Click;
        /// <summary>SolidWorks selected something: pixel x, y and the model point it picked (mm).</summary>
        public event Action<int, int, Vec3> Picked;

        public MouseHook(IModelView view)
        {
            mouse = view.GetMouse();
            mouse.MouseMoveNotify += OnMove;
            mouse.MouseLBtnDownNotify += OnDown;
            mouse.MouseLBtnUpNotify += OnUp;
            mouse.MouseSelectNotify += OnSelect;
        }

        private int OnMove(int x, int y, int flags)
        {
            var h = Move;
            if (h != null) try { h(x, y); } catch (Exception e) { Log.Error("mouse move", e); }
            return 0;
        }

        private int OnDown(int x, int y, int flags) { downX = x; downY = y; return 0; }

        private int OnUp(int x, int y, int flags)
        {
            bool click = downX != int.MinValue && Math.Abs(x - downX) <= 4 && Math.Abs(y - downY) <= 4;
            downX = int.MinValue;
            var h = Click;
            if (click && h != null) try { h(x, y); } catch (Exception e) { Log.Error("mouse click", e); }
            return 0;
        }

        private int OnSelect(int ix, int iy, double x, double y, double z)
        {
            var h = Picked;
            if (h != null) try { h(ix, iy, new Vec3(x, y, z) * Units.MmPerM); } catch (Exception e) { Log.Error("mouse pick", e); }
            return 0;
        }

        public void Dispose()
        {
            if (mouse == null) return;
            mouse.MouseMoveNotify -= OnMove;
            mouse.MouseLBtnDownNotify -= OnDown;
            mouse.MouseLBtnUpNotify -= OnUp;
            mouse.MouseSelectNotify -= OnSelect;
            mouse = null;
        }
    }
}
