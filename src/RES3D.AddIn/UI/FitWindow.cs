// Fit Axis (spec §3): click inside a hole or on a boss -> region grow -> robust cylinder -> a native reference axis
// along its centre line. (Planes moved to the Add Plane window, user request 2026-10-26.)
using System;
using System.Windows.Forms;
using RES3D.Core.Fit;

namespace RES3D.AddIn.UI
{
    public sealed class FitWindow : ToolWindow
    {
        private readonly NumericUpDown dist = Number(Settings.Get("FitDistTol", 0.2), 0.005, 10, 3, 0.05);
        private readonly NumericUpDown normal = Number(Settings.Get("FitNormalTol", 20), 1, 60, 0, 1);
        private readonly CheckBox snap = new CheckBox { Text = "Snap square / coaxial to existing planes and axes", AutoSize = true, Checked = Settings.Get("FitSnap", 1) > 0 };
        private readonly NumericUpDown snapTol = Number(Settings.Get("FitSnapTol", 1.0), 0.01, 10, 2, 0.25);
        private readonly TextBox nameHint = new TextBox();
        private readonly Label info = new Label { AutoSize = true, MaximumSize = new System.Drawing.Size(340, 0) };

        public FitWindow(DocSession s) : base(s, "Fit Axis", 380)
        {
            Heading("Click inside a hole or on a boss");
            Tip(Wide(info), "The last axis made, with how well the cylinder fits the scan.");
            Heading("Cylinder growing");
            Row("Distance tolerance (mm)", dist, "How far from the fitted cylinder the surface may wander and still count as the same face. Raise it for a noisy scan.");
            Row("Normal tolerance (°)", normal, "How much the surface may turn and still count as the same face. Lower it if the face spills round a fillet or chamfer.");
            Heading("Output");
            Tip(Wide(snap), "Make the axis exactly parallel or perpendicular to an existing plane, and coaxial with an existing axis, when it is within the snap tolerance (the adjustment is reported).");
            Row("Snap tolerance (°)", snapTol, "The largest angle that is snapped square.");
            Row("Name hint", nameHint, "Optional text added to the new axis' name, e.g. Bore.");
            var close = new Button { Text = "Close" };
            BottomButtons(close);
            Tip(close, "Close this window; axes already made stay.");
            close.Click += (o, e) => Close();
            info.Text = "Each click on a hole or boss makes a reference axis along its centre line."
                        + (s.State.AlignmentAccepted ? "" : " (The scan is not aligned yet: axes made now stay where they are if you align it later.)");
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
                SnapSquare = snap.Checked, SnapTolDeg = (double)snapTol.Value, CreateFeature = true, NameHint = nameHint.Text.Trim()
            };
        }

        protected override void OnMouseClick(int x, int y)
        {
            if (View == null || !Session.HasScan) return;
            var outcome = Session.FitCylinderAt(ClickRay(x, y), Options());
            Session.Sw.ClearSelection();
            info.Text = outcome.Report + (outcome.Feature != null ? " -> " + outcome.Feature : "");
        }
    }
}
