// Shared plumbing for RE-Scan3D's tool windows: a small modeless window owned by SolidWorks, a simple
// label/control row layout, and a mouse hook on the active view while the window is open.
// (WinForms rather than PropertyManager pages: DECISIONS D6.)
using System;
using System.Drawing;
using System.Windows.Forms;
using RES3D.AddIn.Sw;
using RES3D.Core.Math3;
using SolidWorks.Interop.sldworks;

namespace RES3D.AddIn.UI
{
    public class ToolWindow : Form
    {
        protected readonly DocSession Session;
        protected readonly TableLayoutPanel Grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, AutoSize = true, AutoScroll = true, Padding = new Padding(8) };
        protected readonly TextBox Output = new TextBox { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, Height = 120, Font = new Font(FontFamily.GenericMonospace, 8.5f) };
        protected MouseHook Hook;
        protected SwView View;

        /// <summary>Raised when the window closes (the add-in drops its reference).</summary>
        public event Action<ToolWindow> Closed2;

        private static readonly System.Collections.Generic.List<ToolWindow> open = new System.Collections.Generic.List<ToolWindow>();

        /// <summary>Every open RE-Scan3D tool window.</summary>
        public static ToolWindow[] All { get { return open.ToArray(); } }

        public DocSession For { get { return Session; } }

        /// <summary>Close every window (Finish, add-in unload), or only those of one document.</summary>
        public static void CloseAll(DocSession only = null)
        {
            foreach (var w in All)
                if (!w.IsDisposed && (only == null || ReferenceEquals(w.Session, only))) w.Close();
        }

        public ToolWindow(DocSession session, string title, int width)
        {
            Session = session;
            Text = "RE-Scan3D - " + title;
            FormBorderStyle = FormBorderStyle.SizableToolWindow;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            Width = width;
            Height = 560;
            Grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
            Grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            Controls.Add(Grid);
            FormClosed += (s, e) =>
            {
                open.Remove(this);
                try { Unhook(); } catch (Exception ex) { Log.Error("unhook", ex); }   // the view may already be gone
                try { OnClosedWindow(); } catch (Exception ex) { Log.Error("window close", ex); }
                var h = Closed2; if (h != null) h(this);
            };
        }

        public void ShowNear(IWin32Window owner)
        {
            // top-left of the SolidWorks graphics area is usually free of the feature tree's right edge
            var r = Screen.FromHandle(owner.Handle).WorkingArea;
            Location = new Point(r.Right - Width - 40, r.Top + 140);
            // one window of each kind: a second Fit window would add a second mouse hook (two fits per click)
            foreach (var w in All) if (w.GetType() == GetType() && w != this) w.Close();
            open.Add(this);
            Show(owner);
            FitToContent(false);
        }

        protected virtual void OnClosedWindow() { }

        /// <summary>The height that shows everything in the window (no scrolling).</summary>
        protected int ContentHeight()
        {
            int bottom = 0;
            foreach (Control c in Controls) if (c != Grid && c.Visible && c.Dock == DockStyle.Bottom) bottom += c.Height;
            return Grid.GetPreferredSize(new Size(ClientSize.Width, 0)).Height + bottom + (Height - ClientSize.Height) + 12;
        }

        /// <summary>Make the window tall enough for everything in it (within the screen); `growOnly` never shrinks it.</summary>
        protected void FitToContent(bool growOnly)
        {
            if (!IsHandleCreated) return;
            var wa = Screen.FromControl(this).WorkingArea;
            int h = Math.Min(ContentHeight(), wa.Height - 20);
            if (growOnly && h <= Height) return;
            Height = h;
            if (Bottom > wa.Bottom) Top = Math.Max(wa.Top, wa.Bottom - Height);
        }

        // ------------------------------------------------------------------ layout helpers

        protected T Row<T>(string label, T control) where T : Control
        {
            control.Dock = DockStyle.Fill;
            Grid.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Padding = new Padding(0, 6, 0, 0) });
            Grid.Controls.Add(control);
            return control;
        }

        protected T Wide<T>(T control) where T : Control
        {
            control.Dock = DockStyle.Fill;
            Grid.Controls.Add(control);
            Grid.SetColumnSpan(control, 2);
            return control;
        }

        protected void Heading(string text)
        {
            var l = new Label { Text = text, AutoSize = true, Font = new Font(Font, FontStyle.Bold), Padding = new Padding(0, 10, 0, 2) };
            Grid.Controls.Add(l);
            Grid.SetColumnSpan(l, 2);
        }

        protected FlowLayoutPanel Buttons(params Button[] buttons)
        {
            var p = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = true };
            foreach (var b in buttons) { b.AutoSize = true; p.Controls.Add(b); }
            return Wide(p);
        }

        private readonly ToolTip tips = new ToolTip { AutoPopDelay = 30000, InitialDelay = 400, ReshowDelay = 200, ShowAlways = true };

        /// <summary>A tooltip, word-wrapped (WinForms tooltips don't wrap by themselves).</summary>
        protected T Tip<T>(T control, string text) where T : Control
        {
            tips.SetToolTip(control, Wrap(text, 60));
            return control;
        }

        public static string Wrap(string text, int width)
        {
            var sb = new System.Text.StringBuilder();
            foreach (var para in text.Split('\n'))
            {
                int col = 0;
                foreach (var word in para.Split(' '))
                {
                    if (col > 0 && col + 1 + word.Length > width) { sb.Append('\n'); col = 0; }
                    else if (col > 0) { sb.Append(' '); col++; }
                    sb.Append(word);
                    col += word.Length;
                }
                sb.Append('\n');
            }
            return sb.ToString().TrimEnd('\n');
        }

        protected T Row<T>(string label, T control, string tip) where T : Control
        {
            Label unused;
            return Row(label, control, tip, out unused);
        }

        /// <summary>A row whose label and control can be shown or hidden together.</summary>
        protected T Row<T>(string label, T control, string tip, out Label labelOut) where T : Control
        {
            control.Dock = DockStyle.Fill;
            labelOut = new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Padding = new Padding(0, 6, 0, 0) };
            Grid.Controls.Add(labelOut);
            Grid.Controls.Add(control);
            Tip(labelOut, tip);
            return Tip(control, tip);
        }

        /// <summary>Buttons along the bottom of the window (outside the scrolling area), right-aligned.</summary>
        protected FlowLayoutPanel BottomButtons(params Button[] buttons)
        {
            var p = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(8) };
            for (int i = buttons.Length - 1; i >= 0; i--) { buttons[i].AutoSize = true; buttons[i].MinimumSize = new Size(80, 0); p.Controls.Add(buttons[i]); }
            Controls.Add(p);
            return p;
        }

        protected static NumericUpDown Number(double value, double min, double max, int decimals, double step)
        {
            return new NumericUpDown
            {
                DecimalPlaces = decimals, Minimum = (decimal)min, Maximum = (decimal)max,
                Increment = (decimal)step, Value = (decimal)Math.Max(min, Math.Min(max, value))
            };
        }

        protected void Say(string line)
        {
            Output.AppendText(line + System.Environment.NewLine);
        }

        /// <summary>Run an action from a SolidWorks event after the event returns (never re-enter SolidWorks inside its own callback).</summary>
        protected void Later(Action a)
        {
            if (IsDisposed || !IsHandleCreated) return;
            BeginInvoke((Action)(() =>
            {
                try { a(); }
                catch (Exception e) { Msg.Error(this, e); }
            }));
        }

        // ------------------------------------------------------------------ mouse

        protected void HookActiveView()
        {
            Unhook();
            var v = Session.Sw.Doc.ActiveView as IModelView;
            if (v == null) return;
            View = new SwView(Session.Sw, v);
            Hook = new MouseHook(v);
            Log.Info(GetType().Name + ": mouse hooked to the active view");
            Hook.Picked += (x, y, p) => { View.Calibrate(x, y, p); pickX = x; pickY = y; pickAt = p; };
            Hook.Move += OnMouseMove;
            Hook.Click += (x, y) => Later(() => OnMouseClick(x, y));
        }

        protected void Unhook()
        {
            if (Hook != null) { Hook.Dispose(); Hook = null; }
        }

        private int pickX = int.MinValue, pickY;
        private Vec3 pickAt;

        /// <summary>The model point SolidWorks itself picked for the click at (x, y), if it reported one.</summary>
        protected bool SolidWorksPick(int x, int y, out Vec3 point)
        {
            point = pickAt;
            return pickX != int.MinValue && System.Math.Abs(pickX - x) <= 4 && System.Math.Abs(pickY - y) <= 4;
        }

        /// <summary>
        /// The ray for a click, aimed so its first hit is the face the user SEES. When SolidWorks reported its own
        /// pick point for this click, cast both ways along the view line and keep the direction whose first hit is
        /// next to that point; this also settles SwView.FlipDir for the session.
        /// </summary>
        protected Ray3 ClickRay(int x, int y)
        {
            var r = View.RayAt(x, y);
            bool have = pickX != int.MinValue && System.Math.Abs(pickX - x) <= 4 && System.Math.Abs(pickY - y) <= 4;
            pickX = int.MinValue;
            if (Session.Scan == null) return r;
            var back = new Ray3(r.Origin + r.Dir * 2e5, r.Dir * -1);
            var h1 = Session.Scan.Pick(r);
            var h2 = Session.Scan.Pick(back);
            Log.Info(string.Format("click {0},{1}: SolidWorks pick {2}; ray {3} dir {4}; hit {5} / other way {6}", x, y,
                have ? pickAt.ToString() : "none", r.Origin, r.Dir, h1.Hit ? h1.Point.ToString() : "none", h2.Hit ? h2.Point.ToString() : "none"));
            if (!have) return h1.Hit || !h2.Hit ? r : back;   // no SolidWorks pick: a ray that hits at all
            double d1 = h1.Hit ? (h1.Point - pickAt).Length : double.MaxValue;
            double d2 = h2.Hit ? (h2.Point - pickAt).Length : double.MaxValue;
            // only when SolidWorks' pick really is on the scan the other way (a click on a plane or sketch gives a pick
            // point off the scan; flipping on that sent the next click to the wrong face - user test 2026-10-26)
            if (d2 < d1 && d2 < 2.0)
            {
                SwView.FlipDir = !SwView.FlipDir;
                Log.Info(string.Format("picking: view direction reversed (hit {0:0.###} mm from SolidWorks' pick, other way {1:0.###} mm)", d2, d1));
                return View.RayAt(x, y);
            }
            return r;
        }

        protected virtual void OnMouseMove(int x, int y) { }
        protected virtual void OnMouseClick(int x, int y) { }
    }
}
