// Align Scan (spec §2): an editable list of add-in "mates" between fitted scan planes and the base planes,
// re-solved as a whole on every change; manual nudges; Accept creates/updates Scan-Top/Front/Right.
// Residuals are deliberately not shown (spec): the Scan-* planes show where the real faces are.
using System;
using System.Collections.Generic;
using System.Windows.Forms;
using ScanRE.Core.Align;
using ScanRE.Core.Cache;
using ScanRE.Core.Math3;

namespace ScanRE.AddIn.UI
{
    public sealed class AlignWindow : ToolWindow
    {
        private readonly ListView list = new ListView { View = System.Windows.Forms.View.Details, FullRowSelect = true, HideSelection = false, MultiSelect = false, Height = 130 };
        private readonly ComboBox fit = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
        private readonly ComboBox type = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
        private readonly ComboBox target = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
        private readonly NumericUpDown value = Number(0, -100000, 100000, 3, 1);
        private readonly NumericUpDown weight = Number(1, 0, 1000, 2, 0.5);
        private readonly RadioButton datum = new RadioButton { Text = "Datum priority (first mate exact)", AutoSize = true };
        private readonly RadioButton weighted = new RadioButton { Text = "Weighted best fit", AutoSize = true };
        private readonly NumericUpDown[] nudge = new NumericUpDown[6];
        private readonly Label status = new Label { AutoSize = true, MaximumSize = new System.Drawing.Size(340, 0) };
        private readonly int coarse;
        private FitWindow fitWindow;

        public AlignWindow(DocSession s) : base(s, "Align Scan", 400)
        {
            coarse = (int)Settings.Get("AlignDisplayTriangles", 60000);
            list.Columns.Add("#", 28); list.Columns.Add("Face", 50); list.Columns.Add("Mate", 95); list.Columns.Add("To", 60); list.Columns.Add("Value", 60); list.Columns.Add("", 70);
            foreach (MateType t in Enum.GetValues(typeof(MateType))) type.Items.Add(t);
            foreach (BasePlane b in Enum.GetValues(typeof(BasePlane))) target.Items.Add(b);
            type.SelectedIndex = 0; target.SelectedIndex = 1;
            datum.Checked = s.State.Solver == SolverMode.Datum;
            weighted.Checked = !datum.Checked;

            Heading("1. Fit planes on the scan");
            var fitBtn = new Button { Text = "Fit a plane…" };
            Buttons(fitBtn);
            Heading("2. Mates (scan face → base plane)");
            Wide(list);
            Row("Face", fit);
            Row("Mate", type);
            Row("To base plane", target);
            Row("Angle (°) / distance (mm)", value);
            Row("Weight (best fit)", weight);
            var add = new Button { Text = "Add" };
            var upd = new Button { Text = "Update" };
            var del = new Button { Text = "Delete" };
            var up = new Button { Text = "Up" };
            var down = new Button { Text = "Down" };
            var sup = new Button { Text = "Suppress" };
            var flip = new Button { Text = "Flip" };
            Buttons(add, upd, del, up, down, sup, flip);
            Heading("3. Solve");
            Wide(datum); Wide(weighted);
            Heading("Nudge (after the solve)");
            string[] nl = { "Rotate X (°)", "Rotate Y (°)", "Rotate Z (°)", "Move X (mm)", "Move Y (mm)", "Move Z (mm)" };
            for (int i = 0; i < 6; i++) nudge[i] = Row(nl[i], Number(0, -100000, 100000, 3, i < 3 ? 0.1 : 0.1));
            var applyNudge = new Button { Text = "Apply nudge" };
            var resetNudge = new Button { Text = "Reset nudge" };
            Buttons(applyNudge, resetNudge);
            Heading("4. Accept");
            var accept = new Button { Text = "Accept alignment" };
            Buttons(accept);
            Wide(status);
            Wide(Output);

            fitBtn.Click += (o, e) => OpenFit();
            add.Click += (o, e) => Act(() =>
            {
                var m = FromControls();
                if (m == null) return;
                s.State.Mates.Add(m);
                Resolve();
            });
            upd.Click += (o, e) => Act(() =>
            {
                int i = Selected(); if (i < 0) return;
                var m = FromControls(); if (m == null) return;
                m.Suppressed = s.State.Mates[i].Suppressed;
                if (m.FitId == s.State.Mates[i].FitId && m.Target == s.State.Mates[i].Target) m.Sense = s.State.Mates[i].Sense;
                s.State.Mates[i] = m;
                Resolve();
            });
            del.Click += (o, e) => Act(() => { int i = Selected(); if (i < 0) return; s.State.Mates.RemoveAt(i); Resolve(); });
            up.Click += (o, e) => Act(() => MoveMate(-1));
            down.Click += (o, e) => Act(() => MoveMate(1));
            sup.Click += (o, e) => Act(() => { int i = Selected(); if (i < 0) return; s.State.Mates[i].Suppressed = !s.State.Mates[i].Suppressed; Resolve(); });
            flip.Click += (o, e) => Act(() =>
            {
                int i = Selected(); if (i < 0) return;
                var m = s.State.Mates[i];
                m.Sense = m.Sense == 0 ? -1 : -m.Sense;
                Resolve();
            });
            datum.CheckedChanged += (o, e) => { if (datum.Checked) Act(() => { s.State.Solver = SolverMode.Datum; Resolve(); }); };
            weighted.CheckedChanged += (o, e) => { if (weighted.Checked) Act(() => { s.State.Solver = SolverMode.Weighted; Resolve(); }); };
            applyNudge.Click += (o, e) => Act(() =>
            {
                s.Scan.SetNudge(new Vec3((double)nudge[0].Value, (double)nudge[1].Value, (double)nudge[2].Value),
                                new Vec3((double)nudge[3].Value, (double)nudge[4].Value, (double)nudge[5].Value));
                SolveShow();
            });
            resetNudge.Click += (o, e) => Act(() =>
            {
                foreach (var n in nudge) n.Value = 0;
                s.State.Nudge = Rigid.Identity.ToArray();
                SolveShow();
            });
            accept.Click += (o, e) => Act(() =>
            {
                s.Accept();
                Session.RefreshDisplay(s.State.DisplayTriangles, 0);
                status.Text = "Accepted. Scan-* planes: " + string.Join(", ", new List<string>(s.State.ScanPlanes.Values)) +
                              ". Model from the base planes, or the Scan-* planes where the real part isn't square.";
                Say(status.Text);
            });
            list.SelectedIndexChanged += (o, e) => ToControls();
            RefreshFits();
            RefreshList();
            status.Text = s.State.AlignmentAccepted
                ? "This alignment was accepted earlier. Changing it moves the scan; geometry already made from it will no longer match."
                : "Fit a plane, add a mate, repeat for up to three faces, then Accept.";
        }

        protected override void OnClosedWindow()
        {
            if (fitWindow != null && !fitWindow.IsDisposed) fitWindow.Close();
        }

        private void Act(Action a)
        {
            try { a(); }
            catch (Exception e) { Msg.Error(this, e); }
        }

        private void OpenFit()
        {
            if (fitWindow != null && !fitWindow.IsDisposed) { fitWindow.Activate(); return; }
            fitWindow = new FitWindow(Session, true);
            fitWindow.Fitted += o => { RefreshFits(); fit.SelectedItem = o.Record.Id; };
            fitWindow.Closed2 += w => fitWindow = null;
            fitWindow.ShowNear(Session.Owner);
        }

        private void RefreshFits()
        {
            var keep = fit.SelectedItem;
            fit.Items.Clear();
            foreach (var f in Session.State.Fits) if (f.Kind == "plane") fit.Items.Add(f.Id);
            if (keep != null && fit.Items.Contains(keep)) fit.SelectedItem = keep;
            else if (fit.Items.Count > 0) fit.SelectedIndex = fit.Items.Count - 1;
        }

        private void RefreshList()
        {
            list.Items.Clear();
            int k = 1;
            foreach (var m in Session.State.Mates)
            {
                string v = m.Type == MateType.Angle ? m.Value.ToString("0.###") + "°" : m.Type == MateType.Distance ? m.Value.ToString("0.###") : "";
                var it = new ListViewItem(new[] { (k++).ToString(), m.FitId, m.Type.ToString(), m.Target.ToString(), v,
                                                  (m.Suppressed ? "suppressed " : "") + (m.Sense < 0 ? "flipped" : "") });
                if (m.Suppressed) it.ForeColor = System.Drawing.SystemColors.GrayText;
                list.Items.Add(it);
            }
        }

        private int Selected() { return list.SelectedIndices.Count > 0 ? list.SelectedIndices[0] : -1; }

        private AlignMate FromControls()
        {
            if (fit.SelectedItem == null) { Msg.Info(this, "Fit a plane on the scan first (Fit a plane…)."); return null; }
            return new AlignMate
            {
                FitId = (string)fit.SelectedItem, Type = (MateType)type.SelectedItem, Target = (BasePlane)target.SelectedItem,
                Value = (double)value.Value, Weight = (double)weight.Value
            };
        }

        private void ToControls()
        {
            int i = Selected(); if (i < 0) return;
            var m = Session.State.Mates[i];
            if (fit.Items.Contains(m.FitId)) fit.SelectedItem = m.FitId;
            type.SelectedItem = m.Type; target.SelectedItem = m.Target;
            value.Value = (decimal)m.Value; weight.Value = (decimal)Math.Min(1000, m.Weight);
        }

        private void MoveMate(int d)
        {
            int i = Selected(); int j = i + d;
            var ms = Session.State.Mates;
            if (i < 0 || j < 0 || j >= ms.Count) return;
            var t = ms[i]; ms[i] = ms[j]; ms[j] = t;
            Resolve();
            list.Items[j].Selected = true;
        }

        private void Resolve()
        {
            if (Session.State.AlignmentAccepted && !warned)
            {
                if (!Msg.Confirm(this, "The alignment was accepted before. Changing it moves the scan: geometry you already made from the scan won't match it any more (nothing will fail to rebuild).\n\nChange the alignment?"))
                {
                    // undo is not tracked: reload the stored state
                    var stored = Session.Sw.GetLongProperty(DocSession.AlignmentProperty);
                    if (stored != null) Session.State.ApplyAlignmentJson(stored);
                    RefreshList();
                    return;
                }
                warned = true;
            }
            SolveShow();
            RefreshList();
        }

        private bool warned;

        private void SolveShow()
        {
            Session.SolveAndShow(coarse);
            Say(Session.State.Mates.Count + " mate(s) solved (" + Session.State.Solver + ")");
        }
    }
}
