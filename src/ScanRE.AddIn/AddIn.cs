// ScanRE SolidWorks add-in: the "Scan RE" CommandManager tab / toolbar for parts, the command callbacks,
// and the scriptable automation surface (ISldWorks.GetAddInObject("ScanRE.AddIn")) so other tools such as
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
using ScanRE.AddIn.Sw;
using ScanRE.AddIn.UI;
using ScanRE.Core;
using ScanRE.Core.Align;
using ScanRE.Core.Math3;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SolidWorks.Interop.swpublished;

[assembly: AssemblyTitle("ScanRE - scan reverse engineering for SolidWorks")]
[assembly: AssemblyVersion("0.1.0.0")]
[assembly: AssemblyFileVersion("0.1.0.0")]

namespace ScanRE.AddIn
{
    [ComVisible(true)]
    [Guid("5dc170a8-86cf-42c8-8397-a024ac0657e0")]
    [ProgId("ScanRE.AddIn")]
    public class ScanReAddIn : ISwAddin
    {
        public const string Title = "ScanRE";
        public const string TabName = "Scan RE";
        public const int MinRevision = 34;          // SolidWorks 2026 (RevisionNumber major + 1992)
        private const int GroupId = 0x5C3E;
        // bump when commands are added/removed so SolidWorks rebuilds the stored toolbar
        private const int ToolbarVersion = 1;

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
            // one registration serves every installed SolidWorks; stay dormant (and silent) before 2026
            if (major > 0 && major < MinRevision)
            {
                Log.Info("SolidWorks revision " + app.RevisionNumber() + " is older than 2026: ScanRE stays dormant");
                return true;
            }
            sw = app;
            this.cookie = cookie;
            try
            {
                sw.SetAddinCallbackInfo2(0, this, cookie);
                cmdMgr = sw.GetCommandManager(cookie);
                AddToolbar();
                events = (SldWorks)sw;
                events.ActiveModelDocChangeNotify += OnActiveDocChange;
                Log.Info("ScanRE " + Version + " connected to SolidWorks " + sw.RevisionNumber());
            }
            catch (Exception e)
            {
                Log.Error("connect", e);
            }
            return true;
        }

        public bool DisconnectFromSW()
        {
            try
            {
                ToolWindow.CloseAll();
                foreach (var kv in sessions) kv.Value.Close();
                sessions.Clear();
                if (events != null) events.ActiveModelDocChangeNotify -= OnActiveDocChange;
                RemoveToolbar();
            }
            catch (Exception e) { Log.Error("disconnect", e); }
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
            new Cmd("Import Scan", "Import a scan (STL, OBJ, PLY) as a temporary graphics body", "CmdImport", "EnablePart", "IN", Blue),
            new Cmd("Re-attach Scan", "Restore the scan in its stored position (from the cache next to the part)", "CmdReattach", "EnablePart", "RE", Blue),
            new Cmd("Display Detail", "Change how many triangles the display copy has (picking always uses the full scan)", "CmdDetail", "EnableScan", "DD", Grey),
            new Cmd("Show/Hide Scan", "Show or hide the scan display body", "CmdToggle", "EnableScan", "SH", Grey),
            new Cmd("Align Scan", "Align the scan to the base planes by mating fitted planes", "CmdAlign", "EnableScan", "AL", Green),
            new Cmd("Fit Feature", "Click a face: fit a reference plane, or an axis for a hole or boss", "CmdFit", "EnableScan", "FIT", Green),
            new Cmd("Section and Snap", "In a sketch: section the scan, snap points to it, trace splines", "CmdSection", "EnableScan", "SN", Green),
            new Cmd("Check References", "List anything that still depends on the scan", "CmdCheck", "EnablePart", "CK", Orange),
            new Cmd("Finish", "Finish reverse engineering: remove the scan, leaving a clean native part", "CmdFinish", "EnablePart", "FN", Orange),
            new Cmd("Help", "ScanRE user guide and log", "CmdHelp", "EnableAlways", "?", Grey)
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
            var grp = cmdMgr.CreateCommandGroup2(GroupId, Title, "Scan reverse engineering", "ScanRE", -1, ignorePrevious, ref err);
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

            // the "Scan RE" tab on the CommandManager, parts only
            int docType = (int)swDocumentTypes_e.swDocPART;
            var tab = cmdMgr.GetCommandTab(docType, TabName);
            if (tab != null && ignorePrevious) { cmdMgr.RemoveCommandTab(tab); tab = null; }
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
            }
            catch (Exception e) { Log.Error("doc change", e); }
            return 0;
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
                    throw new Core.Jobs.ScanReException("The part has not been saved yet.", "Save the part first: the scan cache is kept in a folder next to the part file.");
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
                using (var d = new FolderBrowserDialog { Description = "No scan cache next to this part. Choose its .scanre folder, or Cancel and use Import Scan." })
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
                int[] levels = { 30000, 60000, 150000, 400000, 1000000 };
                int cur = s.State.DisplayTriangles, next = levels[0];
                foreach (int l in levels) if (l > cur) { next = l; break; }
                if (next > s.Scan.Full.TriangleCount) next = levels[0];
                s.RefreshDisplay(next, 0);
                Settings.Set("DisplayTriangles", next);
                SetStatus("Scan display: " + next.ToString("N0") + " triangles (click again for the next level)");
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

        public void CmdFit()
        {
            Command("fit", true, s =>
            {
                if (!s.State.AlignmentAccepted &&
                    !Msg.Confirm(s.Owner, "The alignment hasn't been accepted yet. Features made now are placed for the scan's current position and won't follow it if you align later.\n\nFit features anyway?")) return;
                Open(new FitWindow(s, false), s);
            });
        }

        public void CmdSection()
        {
            Command("section", true, s => Open(new SectionWindow(s), s));
        }

        public void CmdCheck()
        {
            Command("check", false, s =>
            {
                var refs = s.CheckReferences();
                Msg.Info(s.Owner, refs.Count == 0 ? "Nothing in the part depends on the scan." : "Depends on the scan:\n\n" + Describe(refs));
            });
        }

        public void CmdFinish()
        {
            Command("finish", false, s =>
            {
                var refs = s.CheckReferences();
                var real = refs.FindAll(r => !r.IsTemporary);
                var text = new StringBuilder("Finish reverse engineering: delete the scan display and ScanRE's temporary sketches, leaving only native features.\n\n");
                if (real.Count > 0)
                    text.Append("WARNING - these depend on the scan and will lose references:\n" + Describe(real) + "\nFix them first (Check References) or continue anyway.\n\n");
                text.Append("Also delete the scan cache next to the part? (Yes = delete, No = keep it for Re-attach Scan)");
                var r2 = MessageBox.Show(s.Owner, text.ToString(), "ScanRE - Finish", MessageBoxButtons.YesNoCancel, real.Count > 0 ? MessageBoxIcon.Warning : MessageBoxIcon.Question);
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
                var msg = "ScanRE " + Version + "\n\nUser guide: " + guide + "\nLog: " + Log.FilePath + "\nPicking calibration: " + SwView.CalibrationNote + "\n\nOpen the user guide?";
                if (MessageBox.Show(Owner, msg, "ScanRE", MessageBoxButtons.YesNo, MessageBoxIcon.Information) == DialogResult.Yes && File.Exists(guide))
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
            if (s == null) throw new COMException("ScanRE: no active part");
            if (needScan && !s.HasScan && !s.TryReattach()) throw new COMException("ScanRE: no scan attached to the active part");
            return s;
        }

        private static T Wrap<T>(Func<T> f)
        {
            try { return f(); }
            catch (COMException) { throw; }
            catch (Exception e) { throw new COMException("ScanRE: " + e); }
        }

        /// <summary>Import a scan into the active (saved) part; returns the full-res triangle count.</summary>
        public int ImportScan(string path, double unitScale, int displayTriangles)
        {
            return Wrap(() =>
            {
                var s = Auto(false);
                s.Import(path, new ImportOptions { UnitScale = unitScale <= 0 ? 1 : unitScale, DisplayTriangles = displayTriangles <= 0 ? 150000 : displayTriangles });
                return s.Scan.Full.TriangleCount;
            });
        }

        public bool ReattachScan() { return Wrap(() => Auto(false).TryReattach()); }

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

        public string CheckScanReferences() { return Wrap(() => Json(Auto(false).CheckReferences())); }

        public string FinishScan(bool deleteCache) { return Wrap(() => Json(Auto(false).Finish(deleteCache, !deleteCache))); }

        public string GetStateJson() { return Wrap(() => { var s = Auto(true); return s.State.ToJson(); }); }

        public string GetCalibration() { return SwView.CalibrationNote; }

        private static string Json(object o)
        {
            return new System.Web.Script.Serialization.JavaScriptSerializer { MaxJsonLength = int.MaxValue }.Serialize(o);
        }

        // ------------------------------------------------------------------ COM registration
        // RegAsm /codebase ScanRE.AddIn.dll (the installer does this, as administrator). HKLM lists the add-in
        // for every SolidWorks version; HKCU AddInsStartup makes it load at start-up.

        private const string AddinKey = @"SOFTWARE\SolidWorks\Addins\{5dc170a8-86cf-42c8-8397-a024ac0657e0}";
        private const string StartupKey = @"Software\SolidWorks\AddInsStartup\{5dc170a8-86cf-42c8-8397-a024ac0657e0}";

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
