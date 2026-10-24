// Shared plumbing for ScanRE's tool windows: a small modeless window owned by SolidWorks, a simple
// label/control row layout, and a mouse hook on the active view while the window is open.
// (WinForms rather than PropertyManager pages: DECISIONS D6.)
using System;
using System.Drawing;
using System.Windows.Forms;
using ScanRE.AddIn.Sw;
using SolidWorks.Interop.sldworks;

namespace ScanRE.AddIn.UI
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

        /// <summary>Every open ScanRE tool window.</summary>
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
            Text = "ScanRE - " + title;
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
        }

        protected virtual void OnClosedWindow() { }

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
            Hook.Picked += (x, y, p) => View.Calibrate(x, y, p);
            Hook.Move += OnMouseMove;
            Hook.Click += (x, y) => Later(() => OnMouseClick(x, y));
        }

        protected void Unhook()
        {
            if (Hook != null) { Hook.Dispose(); Hook = null; }
        }

        protected virtual void OnMouseMove(int x, int y) { }
        protected virtual void OnMouseClick(int x, int y) { }
    }
}
