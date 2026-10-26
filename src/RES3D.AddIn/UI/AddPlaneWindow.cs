// Add Plane (user request 2026-10-26): the Align Scan window's format for making reference planes. Define a plane on
// the scan (click a face, or pick points - live), optionally offset or tilt it (blue preview), then Add: a native
// SolidWorks plane at exact coordinates. The window stays open for the next plane until Close. Planes added are
// listed at the top: select one to see it, right-click to delete it.
using System;
using System.Collections.Generic;
using System.Windows.Forms;
using RES3D.Core.Cache;
using RES3D.Core.Math3;

namespace RES3D.AddIn.UI
{
    public sealed class AddPlaneWindow : PlaneWindow
    {
        private readonly ListView added = new ListView { View = System.Windows.Forms.View.Details, FullRowSelect = true, HideSelection = false, MultiSelect = false, Height = 90 };
        private readonly NumericUpDown offset = Number(0, -100000, 100000, 3, 1);
        private readonly NumericUpDown angle = Number(0, -180, 180, 2, 1);
        private readonly CheckBox flip = new CheckBox { Text = "Flip: offset / tilt the other way", AutoSize = true };
        private readonly CheckBox snap = new CheckBox { Text = "Snap square to existing planes", AutoSize = true, Checked = Settings.Get("FitSnap", 1) > 0 };
        private readonly NumericUpDown snapTol = Number(Settings.Get("FitSnapTol", 1.0), 0.01, 10, 2, 0.25);
        private readonly TextBox name = new TextBox();
        private readonly Button add = new Button { Text = "Add" };
        private readonly Button close = new Button { Text = "Close" };
        private readonly List<FitRecord> listed = new List<FitRecord>();

        protected override string NextStep { get { return "Offset or tilt it below if needed, then Add."; } }

        public AddPlaneWindow(DocSession s) : base(s, "Add Plane")
        {
            added.Columns.Add("Plane", 170); added.Columns.Add("From", 80); added.Columns.Add("Fit (mm)", 90);

            Heading("Planes added");
            Tip(Wide(added), "Reference planes made from the scan. Select one to see it on the scan; right-click (or press Delete) to delete it - it is kept if other features are built on it.");
            var del = new ToolStripMenuItem("Delete this plane");
            added.ContextMenuStrip = new ContextMenuStrip();
            added.ContextMenuStrip.Items.Add(del);
            added.ContextMenuStrip.Opening += (o, e) => { if (added.SelectedIndices.Count == 0) e.Cancel = true; };
            del.Click += (o, e) => DeleteSelected();
            added.KeyDown += (o, e) => { if (e.KeyCode == Keys.Delete) DeleteSelected(); };
            added.SelectedIndexChanged += (o, e) => SelectAdded();

            BuildDefineSection("1. Define a plane on the scan");

            Heading("2. Position the new plane");
            Row("Offset (mm)", offset, "Move the new plane this far from the plane defined on the scan, along its normal (the side facing you when it was defined). The blue stripes show where; Flip puts it on the other side. 0 = on the scan's face.");
            Row("Angle (°)", angle, "Tilt the new plane by this angle about the long direction of the fitted area, through its middle. The blue stripes show the result; Flip tilts it the other way.");
            Tip(Wide(flip), "Offset to the other side of the scan plane, and tilt the other way.");
            Tip(Wide(snap), "Make the new plane exactly parallel or perpendicular to an existing plane when it is within the snap tolerance (the adjustment is reported).");
            Row("Snap tolerance (°)", snapTol, "The largest angle that is snapped square.");
            Row("Name", name, "Name for the new plane. Empty: \"Plane F<n> (rms ... max ...)\", which records how well it fits.");

            BottomButtons(add, close);
            Tip(add, "Create the reference plane (shown in blue, or on the orange stripes when there is no offset or tilt). The window stays open for the next one.");
            Tip(close, "Close the window. Planes already added stay.");

            offset.ValueChanged += (o, e) => ShowTarget();
            angle.ValueChanged += (o, e) => ShowTarget();
            flip.CheckedChanged += (o, e) => ShowTarget();
            add.Click += (o, e) => Act(Add);
            close.Click += (o, e) => Close();

            RefreshAdded();
            ModeChanged();
            if (!s.State.AlignmentAccepted)
                planeInfo.Text += " (The scan is not aligned yet: planes added now stay where they are if you align it later.)";
            HookActiveView();
        }

        private void RefreshAdded()
        {
            added.Items.Clear();
            listed.Clear();
            foreach (var f in Session.AddedPlanes())
            {
                listed.Add(f);
                added.Items.Add(new ListViewItem(new[] { f.FeatureName, f.Method == "points" ? f.Points + " points" : "a face", string.Format("rms {0:0.000}", f.Rms) }));
            }
        }

        private void SelectAdded()
        {
            if (added.SelectedIndices.Count == 0) return;
            var f = listed[added.SelectedIndices[0]];
            var feat = Session.Sw.FindFeature(f.FeatureName);
            if (feat == null) { RefreshAdded(); return; }
            StartNewPlane();
            var p = Session.PlaneOf(f);
            p.Part = new Sw.SwPlanes(Session.Sw).PlaneOf(feat);   // where the plane really is (with any offset / tilt)
            if (p.Extent.Count > 0) ShowPlane(p, f.FeatureName + " shown. Define a new plane on the scan to add another.");
        }

        private void DeleteSelected()
        {
            if (added.SelectedIndices.Count == 0) return;
            var f = listed[added.SelectedIndices[0]];
            Act(() =>
            {
                string n = f.FeatureName;
                bool gone = Session.DeleteAddedPlane(f);
                preview.HidePlane();
                RefreshAdded();
                planeInfo.Text = gone ? n + " deleted." : n + " was kept: other features are built on it.";
            });
        }

        protected override void UpdateControls()
        {
            FitToContent(true);
            add.Enabled = pending != null;
        }

        /// <summary>The blue preview: the plane Add will make (only when it differs from the orange one).</summary>
        protected override void ShowTarget()
        {
            if (pending == null || pending.Extent == null || pending.Extent.Count == 0 || (offset.Value == 0 && angle.Value == 0)) { targetPreview.HidePlane(); return; }
            targetPreview.ShowPlane(DocSession.Adjusted(pending.Part, pending.Extent, (double)offset.Value, (double)angle.Value, flip.Checked), pending.Extent);
        }

        private void Add()
        {
            Settings.Set("FitSnap", snap.Checked ? 1 : 0);
            Settings.Set("FitSnapTol", (double)snapTol.Value);
            var rec = Session.AddPlane(pending, (double)offset.Value, (double)angle.Value, flip.Checked, snap.Checked, (double)snapTol.Value, name.Text);
            name.Text = "";
            RefreshAdded();
            StartNewPlane();
            targetPreview.HidePlane();
            planeInfo.Text = "Added " + rec.FeatureName + (rec.Snap.Length > 0 ? " (snapped " + rec.Snap + ")" : "") + ". Define the next plane, or Close.";
        }

        /// <summary>Test hook (scripting only): mode, selectpoint, deletepoint, height (PlaneWindow) and "offset",
        /// "angle", "flip" true|false, "snap" true|false, "name", "add", "count" (planes listed).</summary>
        public string Test(string command, string arg)
        {
            switch (command)
            {
                case "offset": offset.Value = decimal.Parse(arg, System.Globalization.CultureInfo.InvariantCulture); break;
                case "angle": angle.Value = decimal.Parse(arg, System.Globalization.CultureInfo.InvariantCulture); break;
                case "flip": flip.Checked = arg == "true"; break;
                case "snap": snap.Checked = arg == "true"; break;
                case "name": name.Text = arg; break;
                case "add": Add(); break;
                case "count": return listed.Count.ToString();
                default: return TestDefine(command, arg);
            }
            return TestDefine("state", "");
        }
    }
}
