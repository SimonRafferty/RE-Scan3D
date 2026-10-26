// Import Scan options (spec §1): file, units, island clean-up, display detail, display-only smoothing.
using System;
using System.IO;
using System.Windows.Forms;
using RES3D.Core;

namespace RES3D.AddIn.UI
{
    public sealed class ImportForm : Form
    {
        private readonly TextBox file = new TextBox { Width = 320 };
        private readonly ComboBox units = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
        private readonly NumericUpDown islands = new NumericUpDown { DecimalPlaces = 2, Minimum = 0, Maximum = 50, Increment = 0.5m };
        private readonly NumericUpDown tris = new NumericUpDown { Minimum = 5000, Maximum = 5000000, Increment = 25000, ThousandsSeparator = true };
        private readonly NumericUpDown smooth = new NumericUpDown { Minimum = 0, Maximum = 20 };

        private static readonly string[] UnitNames = { "millimetres", "centimetres", "metres", "inches" };
        private static readonly double[] UnitScale = { 1, 10, 1000, 25.4 };

        public string FilePath { get { return file.Text.Trim(); } }

        public ImportOptions Options
        {
            get
            {
                return new ImportOptions
                {
                    UnitScale = UnitScale[Math.Max(0, units.SelectedIndex)],
                    IslandFraction = (double)islands.Value / 100.0,
                    DisplayTriangles = (int)tris.Value,
                    DisplaySmoothing = (int)smooth.Value
                };
            }
        }

        public ImportForm(string initialFile)
        {
            Text = "RE-Scan3D - Import Scan";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            var g = new TableLayoutPanel { ColumnCount = 3, AutoSize = true, Padding = new Padding(10), Dock = DockStyle.Fill };
            var browse = new Button { Text = "Browse…", AutoSize = true };
            browse.Click += (s, e) =>
            {
                using (var d = new OpenFileDialog { Filter = "Scan meshes (*.stl;*.obj;*.ply)|*.stl;*.obj;*.ply|All files|*.*", Title = "Choose the scan" })
                {
                    if (File.Exists(file.Text)) d.InitialDirectory = Path.GetDirectoryName(file.Text);
                    if (d.ShowDialog(this) == DialogResult.OK) file.Text = d.FileName;
                }
            };
            units.Items.AddRange(UnitNames);
            units.SelectedIndex = (int)Settings.Get("ImportUnits", 0);
            islands.Value = (decimal)Settings.Get("ImportIslandsPct", 1.0);
            tris.Value = (decimal)Settings.Get("DisplayTriangles", 60000);
            smooth.Value = (decimal)Settings.Get("DisplaySmoothing", 0);
            file.Text = initialFile ?? Settings.GetString("LastScan", "");

            AddRow(g, "Scan file", file, browse);
            AddRow(g, "Scan units", units, null);
            AddRow(g, "Remove islands under (% of largest)", islands, null);
            AddRow(g, "Display triangles", tris, new Label { Text = "coarse = fast; the full scan is always used for picking and fitting", AutoSize = true, MaximumSize = new System.Drawing.Size(220, 0) });
            AddRow(g, "Display smoothing passes", smooth, new Label { Text = "display only", AutoSize = true });

            var ok = new Button { Text = "Import", DialogResult = DialogResult.OK, AutoSize = true };
            var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true };
            var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Dock = DockStyle.Fill };
            buttons.Controls.Add(cancel); buttons.Controls.Add(ok);
            g.Controls.Add(buttons);
            g.SetColumnSpan(buttons, 3);
            Controls.Add(g);
            var tips = new ToolTip { AutoPopDelay = 30000, InitialDelay = 400, ShowAlways = true };
            Action<Control, string> tip = (c, t) => tips.SetToolTip(c, ToolWindow.Wrap(t, 60));
            tip(file, "The scan to import: STL, OBJ or PLY.");
            tip(browse, "Choose the scan file.");
            tip(units, "The units the scan was saved in. Most scanners save millimetres.");
            tip(islands, "Loose fragments smaller than this percentage of the main scan are dropped (scanner noise, bits of the fixture). 0 keeps everything.");
            tip(tris, "How many triangles SolidWorks shows. Fewer = faster. Picking, fitting and sections always use the full scan.");
            tip(smooth, "Smooths the copy SolidWorks shows (display only); fitting uses the original data.");
            tip(ok, "Import the scan into this part.");
            tip(cancel, "Close without importing.");
            AcceptButton = ok; CancelButton = cancel;
            ok.Click += (s, e) =>
            {
                if (!File.Exists(FilePath))
                {
                    MessageBox.Show(this, "Choose an existing STL, OBJ or PLY file.", "RE-Scan3D", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    DialogResult = DialogResult.None;
                    return;
                }
                Settings.Set("ImportUnits", units.SelectedIndex);
                Settings.Set("ImportIslandsPct", (double)islands.Value);
                Settings.Set("DisplayTriangles", (double)tris.Value);
                Settings.Set("DisplaySmoothing", (double)smooth.Value);
                Settings.SetString("LastScan", FilePath);
            };
        }

        private static void AddRow(TableLayoutPanel g, string label, Control c, Control extra)
        {
            g.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left });
            g.Controls.Add(c);
            if (extra != null) g.Controls.Add(extra); else g.Controls.Add(new Label());
        }
    }
}
