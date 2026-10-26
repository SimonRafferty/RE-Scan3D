// RE-Scan3D SolidWorks add-in: the "RE-Scan3D" CommandManager tab / toolbar for parts, the command callbacks,
// and the scriptable automation surface (ISldWorks.GetAddInObject("RES3D.AddIn")) so other tools such as
// the layoutcad MCP can drive it later (spec "Scriptable interface").
//
// COM: the class uses the default ClassInterface (AutoDispatch), so SolidWorks finds command callbacks by
// name through IDispatch and late-bound clients (pywin32, VBA) see every public method (AsmFEA lesson D19:
// with ClassInterfaceType.None, callbacks not on an interface silently do nothing).
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using Microsoft.Win32;
using RES3D.AddIn.Sw;
using RES3D.AddIn.UI;
using RES3D.Core;
using RES3D.Core.Align;
using RES3D.Core.Math3;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SolidWorks.Interop.swpublished;

[assembly: AssemblyTitle("RE-Scan3D - scan reverse engineering for SolidWorks")]
[assembly: AssemblyVersion("0.2.1.0")]
[assembly: AssemblyFileVersion("0.2.1.0")]

namespace RES3D.AddIn
{
    [ComVisible(true)]
    [Guid("5dc170a8-86cf-42c8-8397-a024ac0657e0")]
    [ProgId("RES3D.AddIn")]
    public class Res3dAddIn : ISwAddin
    {
        public const string Title = "RE-Scan3D";
        public const string TabName = "RE-Scan3D";
        public const int MinRevision = 26;          // SolidWorks 2018 (RevisionNumber major + 1992); built against the 2018 API
        private const int GroupId = 0x5C3E;
        // bump when commands are added/removed so SolidWorks rebuilds the stored toolbar
        private const int ToolbarVersion = 7;   // 7: Section and Snap -> Contours
        private const int ToolbarVersionOld6 = 6;   // 2: renamed to RE-Scan3D; 3: Add Plane; 4: Fit Feature -> Fit Axis; 5: enable states; 6: Remove Mesh Data, no Check References

        private ISldWorks sw;
        private int cookie;
        private ICommandManager cmdMgr;
        private SldWorks events;
        private readonly List<KeyValuePair<IModelDoc2, DocSession>> sessions = new List<KeyValuePair<IModelDoc2, DocSession>>();

        public string Version { get { return Assembly.GetExecutingAssembly().GetName().Version.ToString(3); } }

        // ------------------------------------------------------------------ ISwAddin

        public bool ConnectToSW(object thisSW, int cookie)
        {
            var app = (ISldWorks)thisSW;
            int major;
            int.TryParse((app.RevisionNumber() ?? "").Split('.')[0], out major);
            // one registration serves every installed SolidWorks; stay dormant (and silent) before 2018.
            // Development only: HKCU\Software\RES3D OnlyRevision=<major> runs RE-Scan3D in that one version and
            // keeps it dormant in every other (used to test in SolidWorks 2025 while 2026 is in use).
            int only = (int)Settings.Get("OnlyRevision", 0);
            bool run = only > 0 ? major == only : !(major > 0 && major < MinRevision);
            if (!run)
            {
                // test setup only: the startup flag is shared by every SolidWorks version, don't come back here.
                // (For a normal install an older SolidWorks must NOT switch it off: the supported ones need it.)
                if (only > 0) NoAutoStart();
                Log.Info("SolidWorks revision " + app.RevisionNumber() + (only > 0 ? " is not the OnlyRevision test setting" : " is older than 2018") + ": RE-Scan3D stays dormant");
                return true;
            }
            sw = app;
            this.cookie = cookie;
            ui = new Control();
            var handle = ui.Handle;   // forces the handle now, so it belongs to this (SolidWorks' main) thread
            try
            {
                sw.SetAddinCallbackInfo2(0, this, cookie);
                cmdMgr = sw.GetCommandManager(cookie);
                AddToolbar();
                events = (SldWorks)sw;
                events.ActiveModelDocChangeNotify += OnActiveDocChange;
                Log.Info("RE-Scan3D " + Version + " connected to SolidWorks " + sw.RevisionNumber());
            }
            catch (Exception e)
            {
                Log.Error("connect", e);
            }
            return true;
        }

        public bool DisconnectFromSW()
        {
            if (Settings.Get("OnlyRevision", 0) > 0) NoAutoStart();   // test setup: never auto-load in the other versions
            try
            {
                ToolWindow.CloseAll();
                foreach (var kv in sessions) kv.Value.Close();
                sessions.Clear();
                if (events != null) events.ActiveModelDocChangeNotify -= OnActiveDocChange;
                RemoveToolbar();
            }
            catch (Exception e) { Log.Error("disconnect", e); }
            if (ui != null) { ui.Dispose(); ui = null; }
            if (cmdMgr != null) Marshal.ReleaseComObject(cmdMgr);
            cmdMgr = null;
            sw = null;
            GC.Collect();
            GC.WaitForPendingFinalizers();
            return true;
        }

        // ------------------------------------------------------------------ toolbar

        private sealed class Cmd
        {
            public string Name, Tip, Callback, Enable;
            public Icons.Glyph Glyph;
            public Cmd(string name, string tip, string callback, string enable, string glyph, System.Drawing.Color c)
            {
                Name = name; Tip = tip; Callback = callback; Enable = enable; Glyph = new Icons.Glyph(glyph, c);
            }
        }

        private static readonly System.Drawing.Color Blue = System.Drawing.Color.FromArgb(40, 110, 190);
        private static readonly System.Drawing.Color Green = System.Drawing.Color.FromArgb(40, 150, 90);
        private static readonly System.Drawing.Color Orange = System.Drawing.Color.FromArgb(220, 120, 30);
        private static readonly System.Drawing.Color Grey = System.Drawing.Color.FromArgb(110, 110, 120);

        private static readonly Cmd[] Commands =
        {
            new Cmd("Import Scan", "Import a scan (STL, OBJ, PLY) as a temporary display mesh", "CmdImport", "EnableImport", "IN", Blue),
            new Cmd("Re-attach Scan", "Restore the scan in its stored position (from the cache next to the part)", "CmdReattach", "EnableReattach", "RE", Blue),
            new Cmd("Display Detail", "Choose how detailed the scan looks: low, medium or high (picking and fitting always use the full scan)", "CmdDetail", "EnableScan", "DD", Grey),
            new Cmd("Show/Hide Scan", "Show or hide the scan display body", "CmdToggle", "EnableScan", "SH", Grey),
            new Cmd("Align Scan", "Align the scan to the base planes by mating fitted planes", "CmdAlign", "EnableScan", "AL", Green),
            new Cmd("Add Plane", "Define a plane on the scan (click a face or pick points), offset or tilt it, and add it as a reference plane", "CmdAddPlane", "EnableScan", "PL", Green),
            new Cmd("Fit Axis", "Click a hole or boss: fit a cylinder and make a reference axis along its centre line", "CmdFit", "EnableScan", "AX", Green),
            new Cmd("Contours", "In an open sketch: cut the scan with the sketch plane, then place points, splines or lines and arcs on its outline", "CmdSection", "EnableScan", "CT", Green),
            // (Check References was dropped: Remove Mesh Data lists anything that depends on the scan before removing it)
            new Cmd("Remove Mesh Data", "Remove the scan from the part, leaving only native features (anything that still depends on the scan is listed first)", "CmdFinish", "EnablePart", "RM", Orange),
            new Cmd("Help", "RE-Scan3D user guide and log", "CmdHelp", "EnableAlways", "?", Grey)
        };

        private void AddToolbar()
        {
            int err = 0;
            object regIds;
            bool ignorePrevious = false;
            if (cmdMgr.GetGroupDataFromRegistry(GroupId, out regIds))
            {
                var ids = regIds as int[];
                ignorePrevious = ids == null || ids.Length != Commands.Length ||
                                 (int)Settings.Get("ToolbarVersion", 0) != ToolbarVersion;
            }
            Settings.Set("ToolbarVersion", ToolbarVersion);
            var grp = cmdMgr.CreateCommandGroup2(GroupId, Title, "Scan reverse engineering", "RE-Scan3D", -1, ignorePrevious, ref err);
            var glyphs = new Icons.Glyph[Commands.Length];
            for (int i = 0; i < Commands.Length; i++) glyphs[i] = Commands[i].Glyph;
            grp.IconList = Icons.Strips("cmds", glyphs);
            grp.MainIconList = Icons.Strips("main", new[] { new Icons.Glyph("RE", Blue) });
            var index = new int[Commands.Length];
            int itemType = (int)(swCommandItemType_e.swMenuItem | swCommandItemType_e.swToolbarItem);
            for (int i = 0; i < Commands.Length; i++)
            {
                var c = Commands[i];
                index[i] = grp.AddCommandItem2(c.Name, -1, c.Tip, c.Name, i, c.Callback, c.Enable, i, itemType);
            }
            grp.HasToolbar = true;
            grp.HasMenu = true;
            grp.Activate();

            // the "RE-Scan3D" tab on the CommandManager, parts only
            int docType = (int)swDocumentTypes_e.swDocPART;
            var oldTab = cmdMgr.GetCommandTab(docType, "Scan RE");   // the tab's name before the rename
            if (oldTab != null) cmdMgr.RemoveCommandTab(oldTab);
            // always rebuilt from scratch: a tab SolidWorks kept from an earlier layout (or built while a part was
            // already open) showed only the first button (user test 2026-10-26)
            var tab = cmdMgr.GetCommandTab(docType, TabName);
            if (tab != null) { cmdMgr.RemoveCommandTab(tab); tab = null; }
            if (tab == null)
            {
                tab = cmdMgr.AddCommandTab(docType, TabName);
                var box = tab.AddCommandTabBox();
                var ids = new int[Commands.Length];
                var text = new int[Commands.Length];
                for (int i = 0; i < Commands.Length; i++)
                {
                    ids[i] = grp.get_CommandID(index[i]);
                    text[i] = (int)swCommandTabButtonTextDisplay_e.swCommandTabButton_TextBelow;
                }
                box.AddCommands(ids, text);
            }
        }

        private void RemoveToolbar()
        {
            if (cmdMgr == null) return;
            try
            {
                var tab = cmdMgr.GetCommandTab((int)swDocumentTypes_e.swDocPART, TabName);
                if (tab != null) cmdMgr.RemoveCommandTab(tab);
            }
            catch (Exception) { }
            cmdMgr.RemoveCommandGroup(GroupId);
        }

        // enable callbacks: 1 = enabled, 0 = disabled (swCommandItemState)
        public int EnablePart() { return ActivePart() != null ? 1 : 0; }
        public int EnableScan() { var s = ActiveSession(false); return s != null && s.HasScan ? 1 : 0; }
        public int EnableAlways() { return 1; }

        // one scan per part (user request 2026-10-26): Import only while the part has none; Re-attach only when there
        // is scan data that is not attached. Called on every toolbar refresh, so only cheap checks.
        public int EnableImport()
        {
            var d = ActivePart();
            if (d == null) return 0;
            var s = ActiveSession(false);
            return (s != null && s.HasScan) || HasScanCache(d) ? 0 : 1;
        }

        public int EnableReattach()
        {
            var d = ActivePart();
            if (d == null) return 0;
            var s = ActiveSession(false);
            return s != null && s.HasScan ? 0 : HasScanCache(d) ? 1 : 0;
        }

        private static bool HasScanCache(IModelDoc2 d)
        {
            string path = d.GetPathName();
            if (string.IsNullOrEmpty(path)) return false;
            string stem = Path.Combine(Path.GetDirectoryName(path) ?? "", Path.GetFileNameWithoutExtension(path));
            return Directory.Exists(stem + ".res3d") || Directory.Exists(stem + ".scanre");
        }

        // ------------------------------------------------------------------ documents

        private IWin32Window Owner
        {
            get { return new SwWindow((IntPtr)((IFrame)sw.Frame()).GetHWndx64()); }
        }

        private IModelDoc2 ActivePart()
        {
            if (sw == null) return null;
            var d = sw.ActiveDoc as IModelDoc2;
            return d != null && d.GetType() == (int)swDocumentTypes_e.swDocPART ? d : null;
        }

        private DocSession ActiveSession(bool create)
        {
            var d = ActivePart();
            if (d == null) return null;
            foreach (var kv in sessions) if (ReferenceEquals(kv.Key, d)) return kv.Value;
            if (!create) return null;
            var s = new DocSession(new SwDoc(sw, d), Owner);
            sessions.Add(new KeyValuePair<IModelDoc2, DocSession>(d, s));
            var part = d as PartDoc;
            if (part != null)
            {
                DPartDocEvents_DestroyNotify2EventHandler h = null;
                h = t =>
                {
                    try
                    {
                        ToolWindow.CloseAll(s);
                        s.Close();
                        sessions.RemoveAll(kv => ReferenceEquals(kv.Key, d));
                        part.DestroyNotify2 -= h;
                    }
                    catch (Exception e) { Log.Error("doc close", e); }
                    return 0;
                };
                part.DestroyNotify2 += h;
            }
            return s;
        }

        private int OnActiveDocChange()
        {
            // tool windows act on one document: close them when the user switches documents
            try
            {
                var active = ActiveSession(false);
                foreach (var w in ToolWindow.All)
                    if (!w.IsDisposed && !ReferenceEquals(w.For, active)) w.Close();
                // a part with a scan cache gets its scan back without asking (after SolidWorks finishes opening it)
                if (ui != null) ui.BeginInvoke((Action)AutoAttach);
            }
            catch (Exception e) { Log.Error("doc change", e); }
            return 0;
        }

        private void AutoAttach()
        {
            try
            {
                var d = ActivePart();
                if (d == null || string.IsNullOrEmpty(d.GetPathName())) return;
                if (!Core.Cache.Sidecar.ForPart(d.GetPathName()).Exists) return;
                var s = ActiveSession(true);
                if (s == null || s.HasScan) return;
                if (s.TryReattach())
                {
                    Log.Info("scan attached automatically: " + d.GetPathName());
                    SetStatus("RES3D: scan attached (" + s.Scan.Full.TriangleCount.ToString("N0") + " triangles)");
                }
            }
            catch (Exception e) { Log.Error("automatic attach", e); }
        }

        private void Open(ToolWindow w, DocSession s)
        {
            w.ShowNear(s.Owner);   // ToolWindow keeps the registry and closes an older window of the same kind
        }

        /// <summary>Run a command with the standard error handling (spec: say why and what to do).</summary>
        private void Command(string name, bool needScan, Action<DocSession> body)
        {
            DocSession s = null;
            try
            {
                s = ActiveSession(true);
                if (s == null) { Msg.Info(Owner, "Open a part first."); return; }
                if (needScan && !s.HasScan && !s.TryReattach())
                {
                    Msg.Info(s.Owner, "No scan is attached to this part. Use Import Scan first.");
                    return;
                }
                body(s);
            }
            catch (Exception e)
            {
                Msg.Error(s != null ? s.Owner : Owner, e);
            }
        }

        // ------------------------------------------------------------------ command callbacks

        public void CmdImport()
        {
            Command("import", false, s =>
            {
                if (string.IsNullOrEmpty(s.Sw.PathName))
                    throw new Core.Jobs.Res3dException("The part has not been saved yet.", "Save the part first: the scan cache is kept in a folder next to the part file.");
                if (s.HasScan && !Msg.Confirm(s.Owner, "This part already has a scan attached. Import a new scan (the alignment and fits are kept if you re-import the same file)?")) return;
                using (var f = new ImportForm(null))
                {
                    if (f.ShowDialog(s.Owner) != DialogResult.OK) return;
                    s.Import(f.FilePath, f.Options);
                }
                Log.Info("imported " + s.State.SourcePath);
                if (!s.State.AlignmentAccepted)
                    Msg.Info(s.Owner, "Scan imported: " + s.Scan.Full.TriangleCount.ToString("N0") + " triangles.\n\nNext: Align Scan, to place it relative to the Front, Top and Right planes.");
            });
        }

        public void CmdReattach()
        {
            Command("reattach", false, s =>
            {
                if (s.HasScan) { Msg.Info(s.Owner, "The scan is already attached."); return; }
                if (s.TryReattach()) { Msg.Info(s.Owner, "Scan re-attached in its stored position."); return; }
                // no cache next to the part: the part may have been renamed - let the user find the cache folder
                using (var d = new FolderBrowserDialog { Description = "No scan cache next to this part. Choose its .res3d folder, or Cancel and use Import Scan." })
                {
                    if (d.ShowDialog(s.Owner) != DialogResult.OK) return;
                    s.ReattachFrom(d.SelectedPath);
                }
            });
        }

        public void CmdDetail()
        {
            Command("detail", true, s =>
            {
                using (var f = new DetailForm(s.State.DisplayTriangles, s.Scan.Full.TriangleCount))
                {
                    if (f.ShowDialog(s.Owner) != DialogResult.OK || f.Chosen <= 0) return;
                    int level = Math.Min(f.Chosen, s.Scan.Full.TriangleCount);
                    if (level == s.State.DisplayTriangles) return;
                    s.RefreshDisplay(level, 0);
                    Settings.Set("DisplayTriangles", level);
                    SetStatus("Scan display: " + level.ToString("N0") + " triangles");
                }
            });
        }

        public void CmdToggle()
        {
            Command("toggle", true, s => s.SetScanVisible(s.ScanHidden));
        }

        public void CmdAlign()
        {
            Command("align", true, s => Open(new AlignWindow(s), s));
        }

        public void CmdAddPlane()
        {
            Command("addplane", true, s => Open(new AddPlaneWindow(s), s));
        }

        public void CmdFit()
        {
            Command("fit", true, s => Open(new FitWindow(s), s));
        }

        public void CmdSection()
        {
            Command("section", true, s =>
            {
                // the window works on the open sketch: say so up front (user request 2026-10-26)
                if (new SwSketch(s.Sw).Active == null)
                {
                    Msg.Info(s.Owner, "Contours works in a sketch.\n\nOpen (edit) or create a sketch on a plane that cuts through the scan, then press Contours again.");
                    return;
                }
                Open(new SectionWindow(s), s);
            });
        }

        public void CmdFinish()
        {
            Command("finish", false, s =>
            {
                var refs = s.CheckReferences();
                var real = refs.FindAll(r => !r.IsTemporary);
                var text = new StringBuilder("Remove the mesh data: delete the scan display and RE-Scan3D's temporary sketches, leaving only native features.\n\n");
                if (real.Count > 0)
                    text.Append("WARNING - these depend on the scan and will lose references:\n" + Describe(real) + "\nCancel and fix them first, or continue anyway.\n\n");
                else text.Append("Nothing in the part depends on the scan.\n\n");
                text.Append("Also delete the scan cache next to the part? (Yes = delete, No = keep it for Re-attach Scan)");
                var r2 = MessageBox.Show(s.Owner, text.ToString(), "RE-Scan3D - Remove Mesh Data", MessageBoxButtons.YesNoCancel, real.Count > 0 ? MessageBoxIcon.Warning : MessageBoxIcon.Question);
                if (r2 == DialogResult.Cancel) return;
                var removed = s.Finish(r2 == DialogResult.Yes, r2 != DialogResult.Yes);
                Msg.Info(s.Owner, "Removed:\n  " + string.Join("\n  ", removed) + "\n\nThe part now contains only native features. Save it to keep the result." +
                                  (r2 == DialogResult.Yes ? "" : "\nRe-attach Scan restores the scan in the same position."));
            });
        }

        public void CmdHelp()
        {
            try
            {
                var guide = Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? "", "USER-GUIDE.html");
                if (!File.Exists(guide)) guide = Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? "", "USER-GUIDE.md");
                var msg = "RE-Scan3D " + Version + "\n\nUser guide: " + guide + "\nLog: " + Log.FilePath + "\nPicking calibration: " + SwView.CalibrationNote + "\n\nOpen the user guide?";
                if (MessageBox.Show(Owner, msg, "RE-Scan3D", MessageBoxButtons.YesNo, MessageBoxIcon.Information) == DialogResult.Yes && File.Exists(guide))
                    System.Diagnostics.Process.Start(guide);
            }
            catch (Exception e) { Msg.Error(Owner, e); }
        }

        private static string Describe(List<ScanReference> refs)
        {
            var sb = new StringBuilder();
            foreach (var r in refs) sb.AppendFormat("  {0} ({1}): {2}\n", r.Feature, r.Type, r.Detail);
            return sb.ToString();
        }

        private void SetStatus(string text)
        {
            try { ((IFrame)sw.Frame()).SetStatusBarText(text); } catch (Exception) { }
        }

        // ------------------------------------------------------------------ automation (scriptable interface)
        // All lengths in millimetres, part coordinates. Errors surface as COMException with the advice text.

        private DocSession Auto(bool needScan)
        {
            var s = ActiveSession(true);
            if (s == null) throw new COMException("RES3D: no active part");
            if (needScan && !s.HasScan && !s.TryReattach()) throw new COMException("RES3D: no scan attached to the active part");
            return s;
        }

        /// <summary>
        /// Run an automation call on SolidWorks' main thread. .NET objects are free-threaded, so a call from
        /// another process arrives on an RPC thread; SolidWorks calls made from there are marshalled one by one
        /// (measured: ~40 ms each, a 138-point spline took minutes) and WinForms must not be used from it.
        /// </summary>
        private T Wrap<T>(Func<T> f)
        {
            if (ui != null && ui.InvokeRequired) return (T)ui.Invoke(new Func<T>(() => Wrap(f)));
            try { return f(); }
            catch (COMException) { throw; }
            catch (Exception e) { throw new COMException("RES3D: " + e); }
        }

        /// <summary>A window-less control created on SolidWorks' main thread, used to get back onto it.</summary>
        private Control ui;

        /// <summary>Import a scan into the active (saved) part; returns the full-res triangle count.</summary>
        public int ImportScan(string path, double unitScale, int displayTriangles)
        {
            return Wrap(() =>
            {
                var s = Auto(false);
                s.Import(path, new ImportOptions { UnitScale = unitScale <= 0 ? 1 : unitScale, DisplayTriangles = displayTriangles <= 0 ? 60000 : displayTriangles });
                return s.Scan.Full.TriangleCount;
            });
        }

        public bool ReattachScan() { return Wrap(() => Auto(false).TryReattach()); }

        /// <summary>Rebuild the display copy with this many triangles; returns the time taken (ms).</summary>
        public int SetDisplayDetail(int triangles)
        {
            return Wrap(() =>
            {
                var s = Auto(true);
                var w = System.Diagnostics.Stopwatch.StartNew();
                s.RefreshDisplay(Math.Max(1000, triangles), 0);
                return (int)w.ElapsedMilliseconds;
            });
        }

        /// <summary>Region-grow plane fit at the first hit of a ray (mm). Returns the fit record as JSON.</summary>
        public string FitPlaneAtRay(double x, double y, double z, double dx, double dy, double dz, bool createFeature, double distTol)
        {
            return Wrap(() =>
            {
                var s = Auto(true);
                var opt = new DocSession.FitOptions { CreateFeature = createFeature };
                if (distTol > 0) opt.Grow.DistTol = distTol;
                var o = s.FitPlaneAt(new Ray3(new Vec3(x, y, z), new Vec3(dx, dy, dz)), opt);
                return Json(o.Record);
            });
        }

        public string FitCylinderAtRay(double x, double y, double z, double dx, double dy, double dz, double distTol)
        {
            return Wrap(() =>
            {
                var s = Auto(true);
                var opt = new DocSession.FitOptions();
                if (distTol > 0) opt.Grow.DistTol = distTol;
                return Json(s.FitCylinderAt(new Ray3(new Vec3(x, y, z), new Vec3(dx, dy, dz)), opt).Record);
            });
        }

        /// <summary>Add an alignment mate: target "Front"|"Top"|"Right", type "Coincident"|"Parallel"|"Perpendicular"|"Angle"|"Distance".</summary>
        public int AddMate(string fitId, string target, string type, double value)
        {
            return Wrap(() =>
            {
                var s = Auto(true);
                s.State.Mates.Add(new AlignMate
                {
                    FitId = fitId, Target = (BasePlane)Enum.Parse(typeof(BasePlane), target, true),
                    Type = (MateType)Enum.Parse(typeof(MateType), type, true), Value = value
                });
                s.SaveAll();
                return s.State.Mates.Count - 1;
            });
        }

        public void ClearMates() { Wrap(() => { var s = Auto(true); s.State.Mates.Clear(); s.SaveAll(); return 0; }); }

        /// <summary>Solve and move the scan; mode "Datum" or "Weighted". Returns residuals JSON (for tools; not shown to users).</summary>
        public string SolveAlignment(string mode)
        {
            return Wrap(() =>
            {
                var s = Auto(true);
                if (!string.IsNullOrEmpty(mode)) s.State.Solver = (SolverMode)Enum.Parse(typeof(SolverMode), mode, true);
                var r = s.SolveAndShow((int)Settings.Get("AlignDisplayTriangles", 60000));
                return Json(new { angleDeg = r.AngleErrorDeg, positionMm = r.PositionErrorMm, notes = r.Notes });
            });
        }

        public void AcceptAlignment() { Wrap(() => { var s = Auto(true); s.Accept(); s.RefreshDisplay(s.State.DisplayTriangles, 0); return 0; }); }

        /// <summary>Section the scan by a plane (mm); returns {curves, segments, lengths}.</summary>
        public string SectionPlane(double px, double py, double pz, double nx, double ny, double nz, double slabHalf)
        {
            return Wrap(() =>
            {
                var s = Auto(true);
                var sec = Progress.Run(s.Owner, "Sectioning scan", job => s.Scan.Section(new Plane3(new Vec3(px, py, pz), new Vec3(nx, ny, nz)),
                                                                                     new Core.Section.SectionOptions { SlabHalfThickness = slabHalf }, job));
                var lengths = new List<double>();
                foreach (var l in sec.Lines) lengths.Add(l.Length);
                return Json(new { curves = sec.Lines.Count, segments = sec.SegmentTotal, lengths = lengths });
            });
        }

        /// <summary>Section the scan by the open 2D sketch's plane and show it (as the Contours window does).</summary>
        public string ShowSectionInActiveSketch(double slabHalf, int layersEachSide, double displayTol)
        {
            return Wrap(() =>
            {
                var s = Auto(true);
                var sk = new SwSketch(s.Sw);
                if (sk.Active == null) throw new COMException("RES3D: no 2D sketch is open");
                var opt = new Core.Section.SectionOptions { SlabHalfThickness = slabHalf, SlabLayersEachSide = Math.Max(1, layersEachSide) };
                var plane = sk.PlaneOf(sk.Active);
                s.LastSection = Progress.Run(s.Owner, "Sectioning scan", job => s.Scan.Section(plane, opt, job));
                sk.ShowSection(s.LastSection, displayTol > 0 ? displayTol : 0.01, true, true);   // as the window's defaults
                var lengths = new List<double>();
                foreach (var l in s.LastSection.Lines) lengths.Add(l.Length);
                return Json(new { curves = s.LastSection.Lines.Count, segments = s.LastSection.SegmentTotal, lengths = lengths,
                                  plane = new[] { plane.Origin.X, plane.Origin.Y, plane.Origin.Z, plane.Normal.X, plane.Normal.Y, plane.Normal.Z } });
            });
        }

        private Core.Section.PlaneSection RequireSection(DocSession s)
        {
            if (s.LastSection == null) throw new COMException("RES3D: call ShowSectionInActiveSketch first");
            return s.LastSection;
        }

        /// <summary>Snap a part-space point (mm) to the last section and add it as a fixed point in the open sketch.</summary>
        public string SnapPointInActiveSketch(double x, double y, double z, double radius)
        {
            return Wrap(() =>
            {
                var s = Auto(true);
                var sec = RequireSection(s);
                var sp = sec.Nearest(sec.Plane.Project(new Vec3(x, y, z)), radius > 0 ? radius : 5);
                if (!sp.Valid) throw new COMException("RES3D: no section within the snap radius");
                int made = new SwSketch(s.Sw).CreateFixedPoints(new[] { sp.Point }).Count;
                return Json(new { made = made, point = new[] { sp.Point.X, sp.Point.Y, sp.Point.Z }, distance = sp.Distance });
            });
        }

        /// <summary>Trace the last section between two snapped points (equal points = whole loop) into the open sketch.</summary>
        public string TraceInActiveSketch(double ax, double ay, double az, double bx, double by, double bz, bool spline, double tolOrSpacing)
        {
            return Wrap(() =>
            {
                var s = Auto(true);
                var sec = RequireSection(s);
                var a = sec.Nearest(sec.Plane.Project(new Vec3(ax, ay, az)), 5);
                var b = sec.Nearest(sec.Plane.Project(new Vec3(bx, by, bz)), 5);
                if (!a.Valid || !b.Valid) throw new COMException("RES3D: trace end not within 5 mm of the section");
                bool whole;
                var path = Core.Trace.Tracer.PathBetween(sec, a, b, null, out whole);
                var sk = new SwSketch(s.Sw);
                Core.Trace.TraceResult res;
                int made;
                if (spline)
                {
                    double tol = tolOrSpacing > 0 ? tolOrSpacing : 0.05;
                    res = Core.Trace.Tracer.AsSpline(path, tol, whole);
                    made = sk.CreateFixedSpline(res.Output, whole, path, tol, res) != null ? 1 : 0;   // res: the real spline's deviation
                }
                else
                {
                    res = Core.Trace.Tracer.AsPoints(path, tolOrSpacing > 0 ? tolOrSpacing : 2, whole);
                    made = sk.CreateFixedPoints(res.Output).Count;
                }
                return Json(new { whole = whole, made = made, points = res.Output.Count, maxDev = res.MaxDev, rmsDev = res.RmsDev });
            });
        }

        public void RemoveSectionDisplay() { Wrap(() => { var s = Auto(false); new SwSketch(s.Sw).RemoveOutline(); s.LastSection = null; return 0; }); }

        private SwPreview scriptPreview;

        /// <summary>Show balls at points (flat x,y,z list, mm) and, if `fitId` names a plane fit, its plate over the points.</summary>
        public void PreviewFit(string fitId, double[] pointsXyz)
        {
            Wrap(() =>
            {
                var s = Auto(true);
                if (scriptPreview == null) scriptPreview = new SwPreview(s.Sw);
                var pts = new List<Vec3>();
                for (int i = 0; pointsXyz != null && i + 2 < pointsXyz.Length; i += 3) pts.Add(new Vec3(pointsXyz[i], pointsXyz[i + 1], pointsXyz[i + 2]));
                scriptPreview.ShowPoints(pts);
                var f = string.IsNullOrEmpty(fitId) ? null : s.State.Fit(fitId);
                if (f != null && pts.Count > 0) scriptPreview.ShowPlane(f.PlaneScan().Transformed(s.Scan.ScanToPart), pts);
                return 0;
            });
        }

        public void ClearPreview() { Wrap(() => { if (scriptPreview != null) scriptPreview.Dispose(); scriptPreview = null; return 0; }); }

        /// <summary>Open the Fit window (as the toolbar does) - for tests; `forAlignment` gives the alignment variant.</summary>
        public void OpenFitWindow() { Wrap(() => { var s = Auto(true); Open(new FitWindow(s), s); return 0; }); }

        /// <summary>Open the Align Scan window (as the toolbar does) - for tests.</summary>
        public void OpenAlignWindow() { Wrap(() => { var s = Auto(true); Open(new AlignWindow(s), s); return 0; }); }

        public void OpenAddPlaneWindow() { Wrap(() => { var s = Auto(true); Open(new AddPlaneWindow(s), s); return 0; }); }

        /// <summary>Drive the open Add Plane window - for tests. See AddPlaneWindow.Test.</summary>
        public string AddPlaneWindowTest(string command, string arg)
        {
            return Wrap(() =>
            {
                foreach (var w in ToolWindow.All) { var a = w as AddPlaneWindow; if (a != null && !a.IsDisposed) return a.Test(command, arg); }
                throw new COMException("RES3D: the Add Plane window is not open");
            });
        }

        /// <summary>Drive the open Align Scan window - for tests. See AlignWindow.Test.</summary>
        public string AlignWindowTest(string command, string arg)
        {
            return Wrap(() =>
            {
                foreach (var w in ToolWindow.All) { var a = w as AlignWindow; if (a != null && !a.IsDisposed) return a.Test(command, arg); }
                throw new COMException("RES3D: the Align Scan window is not open");
            });
        }

        public void CloseToolWindows() { Wrap(() => { ToolWindow.CloseAll(); return 0; }); }

        /// <summary>Whether the active part has its scan loaded (does NOT attach it).</summary>
        public bool IsScanAttached() { return Wrap(() => { var s = ActiveSession(false); return s != null && s.HasScan; }); }

        public string CheckScanReferences() { return Wrap(() => Json(Auto(false).CheckReferences())); }

        public string FinishScan(bool deleteCache) { return Wrap(() => Json(Auto(false).Finish(deleteCache, !deleteCache))); }

        public string GetStateJson() { return Wrap(() => { var s = Auto(true); return s.State.ToJson(); }); }

        public string GetCalibration() { return SwView.CalibrationNote; }

        private static string Json(object o)
        {
            return new System.Web.Script.Serialization.JavaScriptSerializer { MaxJsonLength = int.MaxValue }.Serialize(o);
        }

        // ------------------------------------------------------------------ COM registration
        // RegAsm /codebase RES3D.AddIn.dll (the installer does this, as administrator). HKLM lists the add-in
        // for every SolidWorks version; HKCU AddInsStartup makes it load at start-up.

        private const string AddinKey = @"SOFTWARE\SolidWorks\Addins\{5dc170a8-86cf-42c8-8397-a024ac0657e0}";
        private const string StartupKey = @"Software\SolidWorks\AddInsStartup\{5dc170a8-86cf-42c8-8397-a024ac0657e0}";

        /// <summary>
        /// Switch off "load at startup". SolidWorks keeps one such flag per add-in for ALL installed versions, so with
        /// the test setting (OnlyRevision) a 2025 start-up flag made 2018 / 2026 load the DLL (dormant) and lock it.
        /// </summary>
        private static void NoAutoStart()
        {
            try
            {
                using (var k = Registry.CurrentUser.CreateSubKey(StartupKey)) k.SetValue(null, 0, RegistryValueKind.DWord);
                Log.Info("load-at-startup switched off (shared by all SolidWorks versions)");
            }
            catch (Exception e) { Log.Error("startup flag", e); }
        }

        [ComRegisterFunction]
        public static void RegisterFunction(Type t)
        {
            using (var k = Registry.LocalMachine.CreateSubKey(AddinKey))
            {
                k.SetValue(null, 1, RegistryValueKind.DWord);
                k.SetValue("Description", "Reverse engineering from 3D scans: align, fit planes and axes, snap sketches to sections. Leaves only native features.");
                k.SetValue("Title", Title);
            }
            using (var k = Registry.CurrentUser.CreateSubKey(StartupKey))
                k.SetValue(null, 1, RegistryValueKind.DWord);
        }

        [ComUnregisterFunction]
        public static void UnregisterFunction(Type t)
        {
            Registry.LocalMachine.DeleteSubKey(AddinKey, false);
            Registry.CurrentUser.DeleteSubKey(StartupKey, false);
        }
    }
}
