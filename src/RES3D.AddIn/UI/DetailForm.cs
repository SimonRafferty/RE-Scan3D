// Display Detail (user request 2026-10-26): choose how detailed the scan display is - Low / Medium / High - with the
// number of triangles each shows. Picking and fitting always use the full scan; this is only what SolidWorks draws.
using System;
using System.Drawing;
using System.Windows.Forms;

namespace RES3D.AddIn.UI
{
    public sealed class DetailForm : Form
    {
        /// <summary>Display sizes (triangles) and the SolidWorks load times measured for them (INVESTIGATION Q1).</summary>
        public static readonly int[] Levels = { 30000, 60000, 150000 };
        private static readonly string[] Names = { "Low", "Medium", "High" };
        private static readonly string[] Speed = { "quickest to rotate; about 2 s to show", "a good balance; about 3 s to show", "finest; about 8-10 s to show" };
        private readonly RadioButton[] choices = new RadioButton[3];

        /// <summary>The display size chosen (triangles).</summary>
        public int Chosen { get; private set; }

        public DetailForm(int current, int scanTriangles)
        {
            Text = "RE-Scan3D - Display Detail";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            var tips = new ToolTip { AutoPopDelay = 30000, InitialDelay = 400, ShowAlways = true };

            var g = new TableLayoutPanel { ColumnCount = 1, AutoSize = true, Padding = new Padding(12), Dock = DockStyle.Fill };
            g.Controls.Add(new Label
            {
                Text = "How detailed the scan looks in SolidWorks. Picking, fitting and contours always use the full scan (" + scanTriangles.ToString("N0") + " triangles).",
                AutoSize = true, MaximumSize = new Size(360, 0), Padding = new Padding(0, 0, 0, 8)
            });
            // the level nearest the current display is ticked
            int nearest = 0;
            for (int i = 1; i < Levels.Length; i++) if (Math.Abs(Levels[i] - current) < Math.Abs(Levels[nearest] - current)) nearest = i;
            for (int i = 0; i < Levels.Length; i++)
            {
                int shown = Math.Min(Levels[i], scanTriangles);
                choices[i] = new RadioButton
                {
                    Text = string.Format("{0}: about {1:N0} triangles ({2})", Names[i], shown, Speed[i]),
                    AutoSize = true, Checked = i == nearest
                };
                tips.SetToolTip(choices[i], ToolWindow.Wrap(Names[i] + " detail: SolidWorks draws about " + shown.ToString("N0") + " triangles of the scan." +
                    (shown < Levels[i] ? " (The scan itself only has " + scanTriangles.ToString("N0") + ".)" : "") + " More triangles look finer but make the view slower to turn.", 60));
                g.Controls.Add(choices[i]);
            }
            var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, AutoSize = true, MinimumSize = new Size(80, 0) };
            var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true, MinimumSize = new Size(80, 0) };
            tips.SetToolTip(ok, "Show the scan at the chosen detail.");
            tips.SetToolTip(cancel, "Keep the display as it is.");
            var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Dock = DockStyle.Fill, Padding = new Padding(0, 8, 0, 0) };
            buttons.Controls.Add(cancel); buttons.Controls.Add(ok);
            g.Controls.Add(buttons);
            Controls.Add(g);
            AcceptButton = ok;
            CancelButton = cancel;
            ok.Click += (o, e) => { for (int i = 0; i < Levels.Length; i++) if (choices[i].Checked) Chosen = Levels[i]; };
        }
    }
}
