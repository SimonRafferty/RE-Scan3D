// Picking (spec §4, open question "Picking"): map the mouse position in the SolidWorks view to a ray in
// part space, then ray-cast the add-in's full-resolution mesh — never the display body.
// The view transform (IModelView.Transform) maps model space to view space including the zoom scale. Two
// details are uncertain from the documentation alone and are settled at run time:
//   * whether view Y runs up (needs FrameHeight - y) or down like Windows;
//   * the sign of the viewing direction.
// MouseSelectNotify reports both the pixel and the model point SolidWorks picked; every click checks which
// convention puts that point on our ray and keeps the better one (Calibration). INVESTIGATION.md Q3.
using System;
using RES3D.Core.Math3;
using SolidWorks.Interop.sldworks;

namespace RES3D.AddIn.Sw
{
    public sealed class SwView
    {
        private readonly SwDoc doc;
        private readonly IModelView view;

        /// <summary>Learned once per session and shared by all views.</summary>
        public static bool FlipY = true;

        /// <summary>Viewing direction sign: true = view +Z points into the screen (measured in SolidWorks 2025, where
        /// every first click corrected it to this); ToolWindow.ClickRay still checks it against SolidWorks' pick.</summary>
        public static bool FlipDir = true;
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
            // the view direction's sign can't be told from one point on the ray (both directions pass through it):
            // ToolWindow.ClickRay settles it by casting both ways against the scan (user test: planes on the far side)
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
    /// Subscribes to a view's mouse events while an RE-Scan3D pick mode is active and forwards them. SolidWorks'
    /// own handling continues (handlers return 0), so its tools are not disturbed.
    /// Click: a click on the scan makes SolidWorks select the mesh body, and then (SolidWorks 2025, user test)
    /// the button down/up events do not arrive - only MouseSelectNotify does. So a selection IS a click; button
    /// down+up without a drag is the fallback for empty space (e.g. sketch area). One physical click is never
    /// reported twice. Picked (SolidWorks' own pick point) is raised first, so calibration precedes the click.
    /// </summary>
    public sealed class MouseHook : IDisposable
    {
        private Mouse mouse;
        private int downX = int.MinValue, downY;
        private int lastX = int.MinValue, lastY;
        private int presses, raisedPress = -1;      // one click per physical button press, once presses are seen
        private readonly System.Diagnostics.Stopwatch sinceClick = System.Diagnostics.Stopwatch.StartNew();
        private bool seenDown, seenUp, seenSelect;
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

        private int OnDown(int x, int y, int flags)
        {
            if (!seenDown) { seenDown = true; Log.Info("mouse: button-down events arrive"); }
            presses++;
            downX = x; downY = y;
            return 0;
        }

        private int OnUp(int x, int y, int flags)
        {
            if (!seenUp) { seenUp = true; Log.Info("mouse: button-up events arrive"); }
            bool click = downX != int.MinValue && Math.Abs(x - downX) <= 4 && Math.Abs(y - downY) <= 4;
            downX = int.MinValue;
            if (click) RaiseClick(x, y);
            return 0;
        }

        private int OnSelect(int ix, int iy, double x, double y, double z)
        {
            if (!seenSelect) { seenSelect = true; Log.Info("mouse: select events arrive"); }
            var h = Picked;
            if (h != null) try { h(ix, iy, new Vec3(x, y, z) * Units.MmPerM); } catch (Exception e) { Log.Error("mouse pick", e); }
            RaiseClick(ix, iy);
            return 0;
        }

        private void RaiseClick(int x, int y)
        {
            // the same click seen through both routes (select and button up): report it once. With button presses
            // seen, once per press however late the second report comes (a slow trace let a repeat through - user
            // test); without them (some SolidWorks states send only selections), by time and place
            if (seenDown) { if (raisedPress == presses) return; raisedPress = presses; }
            else if (lastX != int.MinValue && sinceClick.ElapsedMilliseconds < 400 && Math.Abs(x - lastX) <= 4 && Math.Abs(y - lastY) <= 4) return;
            lastX = x; lastY = y;
            sinceClick.Restart();
            Log.Info("mouse: click at " + x + "," + y);
            var h = Click;
            if (h != null) try { h(x, y); } catch (Exception e) { Log.Error("mouse click", e); }
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
