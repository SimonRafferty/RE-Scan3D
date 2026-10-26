// Shared part of the Align Scan and Add Plane windows: defining a plane on the scan, by clicking a flat face or by
// picking points (the plane is fitted and shown live from the third point; points are listed, can be marked and
// deleted). The orange stripes show the plane defined; the blue ones what the second section of the window makes
// of it (offset, tilt), drawn by the window itself.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using RES3D.Core.Fit;
using RES3D.Core.Math3;

namespace RES3D.AddIn.UI
{
    public abstract class PlaneWindow : ToolWindow
    {
        protected readonly RadioButton modeFace = new RadioButton { Text = "Click a flat face", AutoSize = true, Checked = true };
        protected readonly RadioButton modePoints = new RadioButton { Text = "Pick 3 or more points", AutoSize = true };
        protected readonly NumericUpDown dist = Number(Settings.Get("FitDistTol", 0.2), 0.005, 10, 3, 0.05);
        protected readonly NumericUpDown normal = Number(Settings.Get("FitNormalTol", 20), 1, 60, 0, 1);
        protected readonly ListBox pointList = new ListBox { Height = 110, IntegralHeight = false };
        protected readonly Label planeInfo = new Label { AutoSize = true, MaximumSize = new Size(360, 0), Text = "No plane yet." };
        private Label distLabel, normalLabel, pointsLabel;

        protected readonly Sw.SwPreview preview;         // orange: the plane defined on the scan, and the point balls
        protected readonly Sw.SwPreview targetPreview;   // blue: what the window will make of it
        protected readonly List<Vec3> points = new List<Vec3>();
        protected Vec3 viewDir = Vec3.UnitZ;
        protected DocSession.PendingPlane pending;      // the plane defined here, not stored yet

        /// <summary>What to do next, appended to the fit report (e.g. "Choose the standard plane below and Save.").</summary>
        protected abstract string NextStep { get; }
        /// <summary>Enable/disable and show/hide the window's own controls.</summary>
        protected abstract void UpdateControls();
        /// <summary>Draw (or hide) the blue preview.</summary>
        protected abstract void ShowTarget();

        protected PlaneWindow(DocSession s, string title) : base(s, title, 400)
        {
            preview = new Sw.SwPreview(s.Sw);
            targetPreview = new Sw.SwPreview(s.Sw, Sw.SwPreview.Blue);
        }

        /// <summary>Add the "Define a plane on the scan" section to the window (call where it belongs in the layout).</summary>
        protected void BuildDefineSection(string heading)
        {
            Heading(heading);
            Tip(Wide(modeFace), "Click a flat face of the scan: the add-in grows the face from the click and fits a plane to it.");
            Tip(Wide(modePoints), "Click points on the scan: the plane through them is fitted and shown after the third point, and updates with every point.");
            Row("Face: distance tolerance (mm)", dist, "How far from the plane the face may wander and still count as the same face. Raise it for a noisy scan; lower it if the face spills over an edge.", out distLabel);
            Row("Face: normal tolerance (°)", normal, "How much the surface may tilt and still count as the same face. Lower it if the face spills round a fillet.", out normalLabel);
            Row("Points", pointList, "The points picked so far, with each one's distance from the fitted plane. Select one to turn its ball green on the scan; right-click or press Delete to remove it.", out pointsLabel);
            var delPoint = new ToolStripMenuItem("Delete point");
            pointList.ContextMenuStrip = new ContextMenuStrip();
            pointList.ContextMenuStrip.Items.Add(delPoint);
            pointList.ContextMenuStrip.Opening += (o, e) => { if (pointList.SelectedIndex < 0) e.Cancel = true; };
            delPoint.Click += (o, e) => DeleteSelectedPoint();
            pointList.KeyDown += (o, e) => { if (e.KeyCode == Keys.Delete) DeleteSelectedPoint(); };
            pointList.MouseDown += (o, e) =>
            {
                // right-click selects the point under the mouse first, so the menu acts on it
                if (e.Button != MouseButtons.Right) return;
                int i = pointList.IndexFromPoint(e.Location);
                if (i >= 0) pointList.SelectedIndex = i;
            };
            pointList.SelectedIndexChanged += (o, e) => preview.Highlight(pointList.SelectedIndex);
            Tip(Wide(planeInfo), "How well the plane fits: rms and largest distance of the scan from it. On the scan the plane shows as orange stripes; where the scan shows through them evenly, the fit is good.");
            modeFace.CheckedChanged += (o, e) => ModeChanged();
        }

        protected void ModeChanged()
        {
            bool face = modeFace.Checked;
            dist.Visible = distLabel.Visible = normal.Visible = normalLabel.Visible = face;
            pointList.Visible = pointsLabel.Visible = !face;
            StartNewPlane();
            planeInfo.Text = face ? "Click a flat face of the scan." : "Click points on the scan (3 or more).";
        }

        /// <summary>Forget the plane being defined (points, preview) - ready for the next one.</summary>
        protected void StartNewPlane()
        {
            ClearPoints();
            pending = null;
            preview.HidePlane();
            UpdateControls();
            ShowTarget();
        }

        protected override void OnMouseClick(int x, int y)
        {
            if (View == null || !Session.HasScan) return;
            var ray = ClickRay(x, y);
            viewDir = ray.Dir;
            if (modePoints.Checked)
            {
                var hit = Session.Scan.Pick(ray);
                if (!hit.Hit) { planeInfo.Text = "That click missed the scan."; return; }
                points.Add(hit.Point);
                Session.Sw.ClearSelection();   // SolidWorks selected the mesh body under the click
                PointsChanged();
                return;
            }
            Settings.Set("FitDistTol", (double)dist.Value);
            Settings.Set("FitNormalTol", (double)normal.Value);
            var p = Session.PreviewPlaneAt(ray, new GrowOptions { DistTol = (double)dist.Value, NormalTolDeg = (double)normal.Value });
            Session.Sw.ClearSelection();
            pending = p;
            ShowPlane(p, null);
            UpdateControls();
            ShowTarget();
        }

        protected void PointsChanged()
        {
            preview.ShowPoints(points);
            pending = null;
            if (points.Count >= 3)
            {
                try
                {
                    pending = Session.PreviewPlaneThrough(points, viewDir * -1);
                    ShowPlane(pending, null);
                }
                catch (Exception e) { planeInfo.Text = e.Message; }
            }
            else
            {
                preview.HidePlane();
                planeInfo.Text = points.Count + " point(s): pick at least " + (3 - points.Count) + " more.";
            }
            int keep = pointList.SelectedIndex;
            pointList.BeginUpdate();
            pointList.Items.Clear();
            for (int i = 0; i < points.Count; i++)
            {
                string dev = pending != null ? string.Format("  {0:+0.000;-0.000} mm", pending.Part.SignedDistance(points[i])) : "";
                pointList.Items.Add(string.Format("{0}:  {1:0.00}, {2:0.00}, {3:0.00}{4}", i + 1, points[i].X, points[i].Y, points[i].Z, dev));
            }
            pointList.EndUpdate();
            if (keep >= 0 && keep < points.Count) pointList.SelectedIndex = keep;
            UpdateControls();
            ShowTarget();
        }

        protected void DeleteSelectedPoint()
        {
            int i = pointList.SelectedIndex;
            if (i < 0 || i >= points.Count) return;
            points.RemoveAt(i);
            preview.Highlight(-1);
            PointsChanged();
        }

        protected void ClearPoints()
        {
            points.Clear();
            pointList.Items.Clear();
            preview.ClearPoints();
        }

        protected void ShowPlane(DocSession.PendingPlane p, string note)
        {
            if (!preview.ShowPlane(p.Part, p.Extent)) planeInfo.Text = "(SolidWorks would not draw the plane - see the log)";
            else if (note != null) planeInfo.Text = note;
            else if (p.ScanFit != null)
                planeInfo.Text = string.Format("Plane from {0}: rms {1:0.000} mm, largest {2:0.000} mm{3}. {4}",
                    p.Method == "points" ? points.Count + " points" : "the face (" + p.ScanFit.Points.ToString("N0") + " scan points)",
                    p.ScanFit.Rms, p.ScanFit.MaxDev, p.ScanFit.Outliers > 0 ? ", " + p.ScanFit.Outliers + " outliers ignored" : "", NextStep);
        }

        /// <summary>The middle of a plane's outline, on the plane.</summary>
        protected static Vec3 Centre(DocSession.PendingPlane p)
        {
            var c = Vec3.Zero;
            foreach (var q in p.Extent) c += q;
            return p.Part.Project(c * (1.0 / p.Extent.Count));
        }

        /// <summary>Test hook shared by both windows: mode, selectpoint, deletepoint; returns points | has plane | info.</summary>
        protected string TestDefine(string command, string arg)
        {
            switch (command)
            {
                case "mode": if (arg == "points") modePoints.Checked = true; else modeFace.Checked = true; break;
                case "selectpoint": pointList.SelectedIndex = int.Parse(arg); break;
                case "deletepoint": pointList.SelectedIndex = int.Parse(arg); DeleteSelectedPoint(); break;
                case "height": return Height + "|" + ContentHeight() + "|" + ClientSize.Height;
            }
            return points.Count + "|" + (pending != null) + "|" + planeInfo.Text;
        }

        protected void Act(Action a)
        {
            try { a(); }
            catch (Exception e) { Msg.Error(this, e); }
        }

        protected override void OnClosedWindow()
        {
            preview.Dispose();
            targetPreview.Dispose();
        }
    }
}
