// Fit Feature (spec §3): click a face of the scan -> region grow -> robust plane / cylinder -> native plane
// or axis. Or pick 3+ points for a plane. During alignment the fits are records only (no feature yet).
using System;
using System.Collections.Generic;
using System.Windows.Forms;
using ScanRE.Core.Fit;
using ScanRE.Core.Math3;

namespace ScanRE.AddIn.UI
{
    public sealed class FitWindow : ToolWindow
    {
        private readonly RadioButton modePlane = new RadioButton { Text = "Plane: click a face (region grow)", AutoSize = true, Checked = true };
        private readonly RadioButton modePoints = new RadioButton { Text = "Plane: pick 3 or more points", AutoSize = true };
        private readonly RadioButton modeCyl = new RadioButton { Text = "Cylinder → axis: click a hole or boss", AutoSize = true };
        private readonly NumericUpDown dist = Number(Settings.Get("FitDistTol", 0.2), 0.005, 10, 3, 0.05);
        private readonly NumericUpDown normal = Number(Settings.Get("FitNormalTol", 20), 1, 60, 0, 1);
        private readonly CheckBox snap = new CheckBox { Text = "Snap to parallel / perpendicular / coaxial", AutoSize = true, Checked = Settings.Get("FitSnap", 1) > 0 };
        private readonly NumericUpDown snapTol = Number(Settings.Get("FitSnapTol", 1.0), 0.01, 10, 2, 0.25);
        private readonly CheckBox create = new CheckBox { Text = "Create the SolidWorks plane / axis", AutoSize = true };
        private readonly TextBox nameHint = new TextBox();
        private readonly Label pointsLabel = new Label { AutoSize = true, Text = "0 points" };
        private readonly List<Vec3> points = new List<Vec3>();
        private Vec3 lastViewDir = Vec3.UnitZ;

        /// <summary>A fit was recorded (the alignment window refreshes its list).</summary>
        public event Action<DocSession.FitOutcome> Fitted;

        public FitWindow(DocSession s, bool forAlignment) : base(s, forAlignment ? "Fit Plane for Alignment" : "Fit Feature", 380)
        {
            create.Checked = !forAlignment && s.State.AlignmentAccepted;
            if (forAlignment) { modeCyl.Enabled = false; create.Checked = false; create.Enabled = false; }
            Heading("What to fit");
            Wide(modePlane); Wide(modePoints); Wide(modeCyl);
            Heading("Region growing");
            Row("Distance tolerance (mm)", dist);
            Row("Normal tolerance (°)", normal);
            Heading("Output");
            Wide(snap);
            Row("Snap tolerance (°)", snapTol);
            Wide(create);
            Row("Name hint", nameHint);
            Heading("Manual points");
            var fitPts = new Button { Text = "Fit plane to points" };
            var clear = new Button { Text = "Clear points" };
            Wide(pointsLabel);
            Buttons(fitPts, clear);
            Heading("Results");
            Wide(Output);
            var close = new Button { Text = "Close" };
            Buttons(close);
            close.Click += (o, e) => Close();
            clear.Click += (o, e) => { points.Clear(); pointsLabel.Text = "0 points"; };
            fitPts.Click += (o, e) =>
            {
                try { Report(Session.FitPlaneThrough(points, -lastViewDir, Options())); points.Clear(); pointsLabel.Text = "0 points"; }
                catch (Exception ex) { Msg.Error(this, ex); }
            };
            Say("Click on the scan. Tolerances: distance = how far the face may wander (raise for noisy scans); normal = how much it may bend.");
            if (forAlignment) Say("Alignment fits are stored as records only; Scan-Top/Front/Right are made when you accept the alignment.");
            HookActiveView();
        }

        private DocSession.FitOptions Options()
        {
            Settings.Set("FitDistTol", (double)dist.Value);
            Settings.Set("FitNormalTol", (double)normal.Value);
            Settings.Set("FitSnap", snap.Checked ? 1 : 0);
            Settings.Set("FitSnapTol", (double)snapTol.Value);
            return new DocSession.FitOptions
            {
                Grow = new GrowOptions { DistTol = (double)dist.Value, NormalTolDeg = (double)normal.Value },
                SnapSquare = snap.Checked, SnapTolDeg = (double)snapTol.Value, CreateFeature = create.Checked, NameHint = nameHint.Text.Trim()
            };
        }

        protected override void OnMouseClick(int x, int y)
        {
            if (View == null || !Session.HasScan) return;
            var ray = View.RayAt(x, y);
            lastViewDir = ray.Dir;
            if (modePoints.Checked)
            {
                var hit = Session.Scan.Pick(ray);
                if (!hit.Hit) { Say("(missed the scan)"); return; }
                points.Add(hit.Point);
                pointsLabel.Text = points.Count + " points" + (points.Count >= 3 ? " - ready" : "");
                Say(string.Format("point {0}: {1}", points.Count, hit.Point));
                return;
            }
            var outcome = modeCyl.Checked ? Session.FitCylinderAt(ray, Options()) : Session.FitPlaneAt(ray, Options());
            Report(outcome);
            Session.Sw.ClearSelection();
        }

        private void Report(DocSession.FitOutcome o)
        {
            Say(o.Report + (o.Feature != null ? " -> " + o.Feature : " (record only)"));
            var h = Fitted;
            if (h != null) h(o);
        }
    }
}
