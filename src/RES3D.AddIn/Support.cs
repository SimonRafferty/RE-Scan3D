// Logging, per-user settings and the long-operation runner (visible activity + a Cancel that works,
// spec principle 7). None of this calls SolidWorks.
using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;
using RES3D.Core.Jobs;

namespace RES3D.AddIn
{
    /// <summary>Plain-text log at %LOCALAPPDATA%\RES3D\res3d.log (timings for INVESTIGATION.md, errors for support).</summary>
    public static class Log
    {
        private static readonly object gate = new object();
        public static string Folder { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RES3D"); } }
        public static string FilePath { get { return Path.Combine(Folder, "res3d.log"); } }

        public static void Info(string msg) { Write("INFO ", msg); }
        public static void Error(string what, Exception e) { Write("ERROR", what + ": " + e); }
        public static void Timing(string what, long ms) { Write("TIME ", what + " " + ms + " ms"); }

        private static void Write(string level, string msg)
        {
            try
            {
                lock (gate)
                {
                    Directory.CreateDirectory(Folder);
                    var fi = new FileInfo(FilePath);
                    if (fi.Exists && fi.Length > 4 << 20)
                    {
                        if (File.Exists(FilePath + ".old")) File.Delete(FilePath + ".old");
                        File.Move(FilePath, FilePath + ".old");
                    }
                    File.AppendAllText(FilePath, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + " " + level + " " + msg + Environment.NewLine);
                }
            }
            catch (Exception) { /* logging must never break a command */ }
        }
    }

    /// <summary>User settings in HKCU\Software\RES3D (tolerances, display level...).</summary>
    public static class Settings
    {
        private const string Key = @"Software\RES3D";

        public static double Get(string name, double def)
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(Key))
                {
                    var v = k == null ? null : k.GetValue(name) as string;
                    double d;
                    return v != null && double.TryParse(v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out d) ? d : def;
                }
            }
            catch (Exception) { return def; }
        }

        public static void Set(string name, double value)
        {
            try
            {
                using (var k = Registry.CurrentUser.CreateSubKey(Key))
                    k.SetValue(name, value.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
            }
            catch (Exception) { }
        }

        public static string GetString(string name, string def)
        {
            try { using (var k = Registry.CurrentUser.OpenSubKey(Key)) return (k == null ? null : k.GetValue(name) as string) ?? def; }
            catch (Exception) { return def; }
        }

        public static void SetString(string name, string value)
        {
            try { using (var k = Registry.CurrentUser.CreateSubKey(Key)) k.SetValue(name, value ?? ""); }
            catch (Exception) { }
        }
    }

    /// <summary>Wraps SolidWorks' main window handle so dialogs are owned by it (stay on top, centred).</summary>
    public sealed class SwWindow : IWin32Window
    {
        public SwWindow(IntPtr h) { Handle = h; }
        public IntPtr Handle { get; private set; }
    }

    /// <summary>
    /// Run a long operation on a worker thread behind a small progress window with Cancel. The callback
    /// must not call SolidWorks (it isn't thread-safe); do SolidWorks work after Run returns, on the UI thread.
    /// </summary>
    public static class Progress
    {
        public static T Run<T>(IWin32Window owner, string title, Func<JobContext, T> work)
        {
            using (var cts = new CancellationTokenSource())
            using (var dlg = new ProgressForm(title, cts))
            {
                T result = default(T);
                Exception error = null;
                var sw = Stopwatch.StartNew();
                var job = new JobContext(cts.Token, (activity, f) => dlg.Post(activity, f));
                Task.Run(() =>
                {
                    try { result = work(job); }
                    catch (Exception e) { error = e; }
                    finally { dlg.Finish(); }
                });
                dlg.ShowDialog(owner);   // nested message loop: SolidWorks keeps painting
                Log.Timing(title, sw.ElapsedMilliseconds);
                if (error is AggregateException && error.InnerException != null) error = error.InnerException;
                if (error != null)
                {
                    if (error is OperationCanceledException) throw new OperationCanceledException(title + " cancelled");
                    throw error;
                }
                return result;
            }
        }
    }

    internal sealed class ProgressForm : Form
    {
        private readonly Label activity = new Label { Dock = DockStyle.Top, Height = 34, Text = "Starting…" };
        private readonly ProgressBar bar = new ProgressBar { Dock = DockStyle.Top, Height = 18, Maximum = 1000 };
        private readonly Label elapsed = new Label { Dock = DockStyle.Top, Height = 20, ForeColor = SystemColors.GrayText };
        private readonly Button cancel = new Button { Text = "Cancel", Width = 90 };
        private readonly Stopwatch clock = Stopwatch.StartNew();
        private readonly System.Windows.Forms.Timer tick = new System.Windows.Forms.Timer { Interval = 250 };
        private volatile bool finished;

        public ProgressForm(string title, CancellationTokenSource cts)
        {
            Text = "RE-Scan3D - " + title;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MinimizeBox = MaximizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(420, 120);
            Padding = new Padding(12);
            var bottom = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, Height = 34 };
            bottom.Controls.Add(cancel);
            Controls.Add(bottom);
            Controls.Add(elapsed);
            Controls.Add(bar);
            Controls.Add(activity);
            cancel.Click += (s, e) => { cts.Cancel(); cancel.Enabled = false; activity.Text = "Cancelling…"; };
            tick.Tick += (s, e) =>
            {
                elapsed.Text = string.Format("{0:0.0} s", clock.Elapsed.TotalSeconds);
                if (finished) { tick.Stop(); Close(); }
            };
            tick.Start();
            // a closed form while work runs = cancel
            FormClosing += (s, e) => { if (!finished) { cts.Cancel(); e.Cancel = true; } };
        }

        public void Post(string text, double fraction)
        {
            if (!IsHandleCreated) return;
            try { BeginInvoke((Action)(() => { activity.Text = text; bar.Value = (int)(1000 * fraction); })); }
            catch (Exception) { }
        }

        public void Finish() { finished = true; }

        protected override void Dispose(bool disposing)
        {
            if (disposing) tick.Dispose();
            base.Dispose(disposing);
        }
    }

    public static class Msg
    {
        public static void Error(IWin32Window owner, Exception e)
        {
            var sre = e as Res3dException;
            if (e is OperationCanceledException) { Log.Info(e.Message); return; }
            Log.Error("command", e);
            string text = sre != null ? sre.Message + (string.IsNullOrEmpty(sre.Advice) ? "" : "\n\nWhat to do: " + sre.Advice)
                                      : "Unexpected error: " + e.Message + "\n\nDetails are in " + Log.FilePath + ".";
            MessageBox.Show(owner, text, "RE-Scan3D", MessageBoxButtons.OK, sre != null ? MessageBoxIcon.Warning : MessageBoxIcon.Error);
        }

        public static void Info(IWin32Window owner, string text)
        {
            MessageBox.Show(owner, text, "RE-Scan3D", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        public static bool Confirm(IWin32Window owner, string text)
        {
            return MessageBox.Show(owner, text, "RE-Scan3D", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) == DialogResult.OK;
        }
    }
}
