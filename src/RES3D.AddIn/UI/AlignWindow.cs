// Align Scan (spec §2), one window (user request 2026-10-26): define a plane on the scan - click a face, or pick
// points (the plane updates with every point) - mate it to Front, Top or Right, then Save or Cancel. One scan plane
// per standard plane. Saving re-solves the alignment, moves the scan and makes / moves the Scan-* plane.
// The saved mates are listed at the top: select one to see its plane or redefine it; right-click to delete it.
using System;
using System.Collections.Generic;
using System.Windows.Forms;
using RES3D.Core.Align;
using RES3D.Core.Math3;

namespace RES3D.AddIn.UI
{
    public sealed class AlignWindow : PlaneWindow
    {
        private readonly ListView mates = new ListView { View = System.Windows.Forms.View.Details, FullRowSelect = true, HideSelection = false, MultiSelect = false, Height = 90 };
        private readonly ComboBox target = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
        private readonly ComboBox type = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
        private readonly NumericUpDown offset = Number(0, -100000, 100000, 3, 1);
        private readonly NumericUpDown angle = Number(0, -180, 180, 2, 1);
        private readonly CheckBox flip = new CheckBox { Text = "Flip: put the scan on the other side", AutoSize = true };
        private readonly Button save = new Button { Text = "Save" };
        private readonly Button cancel = new Button { Text = "Cancel" };
        private Label offsetLabel, angleLabel;
        private int editing = -1;                   // index of the saved mate being redefined, or -1 for a new one

        private static readonly MateType[] Types = { MateType.Coincident, MateType.Distance, MateType.Angle, MateType.Parallel };
        private static readonly string[] TypeNames = { "Coincident", "Offset", "Angle", "Parallel" };

        protected override string NextStep { get { return "Choose the standard plane below and Save."; } }

        public AlignWindow(DocSession s) : base(s, "Align Scan")
        {
            mates.Columns.Add("Standard plane", 110); mates.Columns.Add("Scan plane from", 110); mates.Columns.Add("Mate", 120);
            foreach (BasePlane b in Enum.GetValues(typeof(BasePlane))) target.Items.Add(BasePlanes.SwName(b));
            foreach (var n in TypeNames) type.Items.Add(n);
            type.SelectedIndex = 0;

            Heading("Saved mates");
            Tip(Wide(mates), "The scan planes already mated to the standard planes. Select one to see its plane on the scan and to redefine it; right-click (or press Delete) to remove it.");
            var del = new ToolStripMenuItem("Delete this mate");
            mates.ContextMenuStrip = new ContextMenuStrip();
            mates.ContextMenuStrip.Items.Add(del);
            mates.ContextMenuStrip.Opening += (o, e) => { if (mates.SelectedIndices.Count == 0) e.Cancel = true; };
            del.Click += (o, e) => DeleteSelectedMate();
            mates.KeyDown += (o, e) => { if (e.KeyCode == Keys.Delete) DeleteSelectedMate(); };
            mates.SelectedIndexChanged += (o, e) => SelectMate();

            BuildDefineSection("1. Define a plane on the scan");

            Heading("2. Mate it to");
            Row("Standard plane", target, "The SolidWorks plane this scan plane is mated to. Each standard plane takes one scan plane: saving to one already used replaces that mate.");
            Row("Mate", type, "Coincident: the scan plane will lie on the standard plane. Offset: the standard plane will be parallel to the scan plane, at the distance below. Angle: tilted from it by the angle below (position left free). Parallel: same direction, position left free. For Offset and Angle the blue stripes show where the standard plane will be relative to the scan; the scan moves there when you Save.");
            Row("Offset (mm)", offset, "How far from the scan plane the standard plane will be. The blue stripes show where: they start on the scan plane (orange) and move with this value. Flip puts them on the other side.", out offsetLabel);
            Row("Angle (°)", angle, "How far the standard plane will be tilted from the scan plane. The blue stripes show it: they start on the scan plane (orange) and tilt with this value. Flip tilts them the other way.", out angleLabel);
            Tip(Wide(flip), "Offset: put the standard plane on the other side of the scan plane. Angle: tilt the other way. Coincident / Parallel: turn the scan over, to the other side of the standard plane.");

            BottomButtons(save, cancel);
            Tip(save, "Store this mate, move the scan to it and create or update the Scan-Top / Scan-Front / Scan-Right plane, then close.");
            Tip(cancel, "Close without saving the plane defined here (saved mates are kept).");

            type.SelectedIndexChanged += (o, e) => { UpdateControls(); ShowTarget(); };
            target.SelectedIndexChanged += (o, e) => { UpdateControls(); ShowTarget(); };
            offset.ValueChanged += (o, e) => ShowTarget();
            angle.ValueChanged += (o, e) => ShowTarget();
            flip.CheckedChanged += (o, e) => ShowTarget();
            save.Click += (o, e) => Act(Save);
            cancel.Click += (o, e) => Close();

            List<string> notes;
            try { notes = s.ReconcileMates(); }
            catch (Exception e) { Log.Error("reconcile mates", e); notes = new List<string>(); }
            RefreshMates();
            target.SelectedIndex = FirstFreeTarget();
            ModeChanged();
            if (notes.Count > 0) planeInfo.Text = string.Join(" ", notes);
            HookActiveView();
        }

        // ------------------------------------------------------------------ mates

        private void RefreshMates()
        {
            mates.Items.Clear();
            foreach (var m in Session.State.Mates)
            {
                var f = Session.State.Fit(m.FitId);
                string from = f == null ? m.FitId : f.Method == "points" ? f.Points + " points" : "a face";
                string how = TypeNames[Math.Max(0, Array.IndexOf(Types, m.Type))] + (m.Type == MateType.Distance ? " " + m.UserValue.ToString("0.###") + " mm" : "")
                             + (m.Type == MateType.Angle ? " " + m.UserValue.ToString("0.##") + "°" : "") + (m.UserFlip ? ", flipped" : "");
                mates.Items.Add(new ListViewItem(new[] { BasePlanes.SwName(m.Target), from, how }));
            }
        }

        private int FirstFreeTarget()
        {
            var used = new HashSet<BasePlane>();
            foreach (var m in Session.State.Mates) used.Add(m.Target);
            // the usual order for a part sitting on its base: Top, then Front, then Right
            foreach (var b in new[] { BasePlane.Top, BasePlane.Front, BasePlane.Right })
                if (!used.Contains(b)) return Array.IndexOf(Enum.GetValues(typeof(BasePlane)), b);
            return 0;
        }

        private void SelectMate()
        {
            if (mates.SelectedIndices.Count == 0) { editing = -1; UpdateControls(); return; }
            editing = mates.SelectedIndices[0];
            var m = Session.State.Mates[editing];
            target.SelectedIndex = Array.IndexOf(Enum.GetValues(typeof(BasePlane)), m.Target);
            type.SelectedIndex = Math.Max(0, Array.IndexOf(Types, m.Type));
            if (m.Type == MateType.Angle) angle.Value = (decimal)Math.Max(-180, Math.Min(180, m.UserValue));
            else offset.Value = (decimal)m.UserValue;
            flip.Checked = m.UserFlip;
            ClearPoints();
            pending = null;
            var f = Session.State.Fit(m.FitId);
            if (f != null) ShowPlane(Session.PlaneOf(f), "Saved plane shown. Define a new plane to replace it, or change the mate below; then Save.");
            UpdateControls();
            ShowTarget();
        }

        private void DeleteSelectedMate()
        {
            if (mates.SelectedIndices.Count == 0) return;
            int i = mates.SelectedIndices[0];
            var m = Session.State.Mates[i];
            if (!Msg.Confirm(this, "Delete the mate to " + BasePlanes.SwName(m.Target) + "? The scan is re-aligned with the remaining mates.")) return;
            Act(() =>
            {
                string note = Session.DeleteMate(i);
                editing = -1;
                preview.HidePlane();
                RefreshMates();
                planeInfo.Text = "Mate deleted." + (note.Length > 0 ? " " + note : "");
                UpdateControls();
            });
        }

        // ------------------------------------------------------------------ the window's own controls

        protected override void UpdateControls()
        {
            var t = Types[Math.Max(0, type.SelectedIndex)];
            offset.Visible = offsetLabel.Visible = t == MateType.Distance;
            angle.Visible = angleLabel.Visible = t == MateType.Angle;
            flip.Text = t == MateType.Distance ? "Flip: offset to the other side of the scan plane"
                      : t == MateType.Angle ? "Flip: tilt the other way"
                      : "Flip: turn the scan over (other side of the plane)";
            FitToContent(true);
            save.Enabled = pending != null || editing >= 0;
        }

        /// <summary>
        /// The blue preview: where the standard plane will be relative to the scan, following the user's input -
        /// it starts on the scan plane (orange) and moves with the offset / tilts with the angle (and Flip). The scan
        /// is moved to bring it onto the standard plane only when the mate is saved. Coincident / Parallel have
        /// nothing to show (it would lie on the orange plane).
        /// </summary>
        protected override void ShowTarget()
        {
            DocSession.PendingPlane p = pending;
            if (p == null && editing >= 0)
            {
                var f = Session.State.Fit(Session.State.Mates[editing].FitId);
                if (f != null) p = Session.PlaneOf(f);
            }
            var t = Types[Math.Max(0, type.SelectedIndex)];
            double v = t == MateType.Distance ? (double)offset.Value : t == MateType.Angle ? (double)angle.Value : 0;
            if (p == null || p.Extent == null || p.Extent.Count == 0 || v == 0 || target.SelectedIndex < 0) { targetPreview.HidePlane(); return; }
            var n = p.Part.Normal.Normalized();
            var c = Centre(p);
            double s = flip.Checked ? -1 : 1;
            Plane3 dest;
            if (t == MateType.Distance) dest = new Plane3(c + n * (s * v), n);
            else
            {
                // tilt towards where the standard plane is now (the way the solver turns the scan); Flip: the other way
                var b = (BasePlane)Enum.GetValues(typeof(BasePlane)).GetValue(target.SelectedIndex);
                var N = BasePlanes.Normal(b);
                if (Vec3.Dot(n, N) < 0) N = N * -1;
                var k = Vec3.Cross(n, N);
                k = k.Length < 1e-9 ? n.AnyPerpendicular().Normalized() : k.Normalized();
                dest = new Plane3(c, Mat3.Rotation(k, s * v * Math.PI / 180) * n);
            }
            targetPreview.ShowPlane(dest, p.Extent);
        }

        // ------------------------------------------------------------------ save

        private void Save()
        {
            var b = (BasePlane)Enum.GetValues(typeof(BasePlane)).GetValue(target.SelectedIndex);
            var t = Types[type.SelectedIndex];
            // a standard plane that already has a mate simply gets this one instead (SaveMate replaces it, no question asked)
            Session.SaveMate(pending, editing, b, t, t == MateType.Angle ? (double)angle.Value : (double)offset.Value, flip.Checked);
            Close();
        }

        /// <summary>Test hook (scripting only): mode, selectpoint, deletepoint, height (PlaneWindow) and "target"
        /// Front|Top|Right, "type" index, "offset", "angle", "flip" true|false, "selectmate" index, "save".</summary>
        public string Test(string command, string arg)
        {
            switch (command)
            {
                case "target": target.SelectedIndex = Array.IndexOf(Enum.GetValues(typeof(BasePlane)), (BasePlane)Enum.Parse(typeof(BasePlane), arg, true)); break;
                case "type": type.SelectedIndex = int.Parse(arg); break;
                case "offset": offset.Value = decimal.Parse(arg, System.Globalization.CultureInfo.InvariantCulture); break;
                case "angle": angle.Value = decimal.Parse(arg, System.Globalization.CultureInfo.InvariantCulture); break;
                case "selectmate": mates.Items[int.Parse(arg)].Selected = true; break;
                case "flip": flip.Checked = arg == "true"; break;
                case "save": Save(); break;
                default: return TestDefine(command, arg);
            }
            return TestDefine("state", "");
        }
    }
}
