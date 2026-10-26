// The scan's display copy in the part (spec §1): a decimated STL shown as SURFACE MESH bodies, in a clearly
// named feature inside a "Scan" folder.
//
// Route (measured in SolidWorks 2025, INVESTIGATION Q1): the API has no call that puts a mesh into an existing
// part - IPartDoc.InsertImportedFeature rejects STL (error 1) and InsertPart3 drops graphics bodies - so the
// STL is opened as its own part (surface mesh body: 3.8 s for 83k triangles; solid mesh body took 219 s),
// saved in the scan cache, and inserted with InsertPart3 WITH THE LINK BROKEN: the mesh becomes ordinary surface
// features inside the part, so the part never refers to a cache file (a linked display part went missing when
// the display was refreshed and the part not saved again - user test 2026-10-26). The temporary display part
// is deleted straight after.
using System;
using System.Collections.Generic;
using System.IO;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace RES3D.AddIn.Sw
{
    public sealed class SwScanBody
    {
        public const string Folder = "Scan";
        public const string Prefix = "Scan - ";
        private readonly SwDoc doc;

        public SwScanBody(SwDoc doc) { this.doc = doc; }

        /// <summary>
        /// Show `stlPath` (millimetres) as the scan feature, replacing `oldFeature` if given.
        /// Returns the new feature's name. The user's STL import preferences are restored afterwards.
        /// </summary>
        public string Insert(string stlPath, string scanName, string oldFeature, out long importMs)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            string meshPart = MeshPart(stlPath);
            // every earlier display goes; the Scan folder stays (a new tree folder can be left in rename mode)
            RemoveDisplays(false);
            var before = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var f in doc.AllFeatures()) before.Add(f.Name);
            var part = (IPartDoc)doc.Doc;
            const int options = (int)(swInsertPartOptions_e.swInsertPartImportSolids | swInsertPartOptions_e.swInsertPartImportSurfaces | swInsertPartOptions_e.swInsertPartBreakLink);
            part.InsertPart3(meshPart, options, "");
            // with the link broken SolidWorks makes a "broken derived part" folder holding one surface feature per
            // mesh body (seen in 2025). The folder is the display: named "Scan - <name>" and put in the Scan folder;
            // the surfaces inside are named "Scan - <name> 1, 2, ..." so every part of it is found and deleted.
            IFeature holder = null;
            var made = new List<IFeature>();
            foreach (var f in doc.AllFeatures())
            {
                if (before.Contains(f.Name) || f.Name.EndsWith("___EndTag___", StringComparison.Ordinal)) continue;
                if (holder == null && f.GetTypeName2() == "BrokenDerivedPartFolder") holder = f; else made.Add(f);
            }
            if (holder == null && made.Count == 0)
                throw new Core.Jobs.Res3dException("SolidWorks could not insert the display mesh.",
                    "Try Import Scan again with a lower display triangle count.");
            importMs = sw.ElapsedMilliseconds;
            const string desc = "RE-Scan3D temporary scan display. Delete with Finish Reverse Engineering; re-attach with Re-attach Scan.";
            string first = doc.UniqueName(Prefix + scanName);
            int k = 0;
            foreach (var f in made)
            {
                if (f.GetTypeName2() == "FtrFolder") continue;
                doc.Rename(f, doc.UniqueName(first + " " + (++k)));
                f.Description = desc;
            }
            if (holder != null)
            {
                doc.Rename(holder, first);
                holder.Description = desc;
                doc.IntoFolder(holder, Folder);
            }
            else
                foreach (var f in made) if (f.GetTypeName2() != "FtrFolder") doc.IntoFolder(f, Folder);
            doc.ClearSelection();
            Log.Info("display features: " + (holder != null ? holder.GetTypeName2() + " + " : "") + k + " surface feature(s)");
            // the temporary display part is not needed any more
            ReleaseCached(Path.GetDirectoryName(stlPath), null);
            try { File.Delete(meshPart); } catch (Exception) { /* pruned next time */ }
            return holder != null ? holder.Name : first + " 1";
        }

        /// <summary>The display features (RE-Scan3D's own, "Scan - ...").</summary>
        public List<IFeature> Displays()
        {
            var list = new List<IFeature>();
            foreach (var f in doc.AllFeatures()) if (f.Name.StartsWith(Prefix, StringComparison.Ordinal)) list.Add(f);
            return list;
        }

        /// <summary>True when the part shows the scan: a display feature that really has mesh bodies.</summary>
        public bool HasLiveDisplay()
        {
            foreach (var kv in doc.MeshBodies())
                if (kv.Value != null && kv.Value.Name.StartsWith(Prefix, StringComparison.Ordinal)) return true;
            return false;
        }

        /// <summary>Delete every display feature (also broken ones from older versions that linked a cache part).</summary>
        public void RemoveDisplays(bool deleteEmptyFolder)
        {
            // the holder folder first (deleting it may take its contents), then anything left
            var all = Displays();
            all.Sort((a, b) => (b.GetTypeName2() == "BrokenDerivedPartFolder").CompareTo(a.GetTypeName2() == "BrokenDerivedPartFolder"));
            foreach (var f in all)
                if (doc.FindFeature(f.Name) != null) doc.Delete(f);
            if (deleteEmptyFolder) doc.DeleteFolderIfEmpty(Folder);
        }

        /// <summary>The STL converted to a part holding surface mesh bodies, next to the STL (made once).</summary>
        private string MeshPart(string stlPath)
        {
            string meshPart = Path.ChangeExtension(stlPath, ".SLDPRT");
            if (File.Exists(meshPart)) return meshPart;
            var app = doc.App;
            int oldType = app.GetUserPreferenceIntegerValue((int)swUserPreferenceIntegerValue_e.swImportStlVrmlModelType);
            int oldUnits = app.GetUserPreferenceIntegerValue((int)swUserPreferenceIntegerValue_e.swImportStlVrmlUnits);
            bool oldPsMesh = app.GetUserPreferenceToggle((int)swUserPreferenceToggle_e.swVrmlStlImportAsPSMesh);
            IModelDoc2 m = null;
            try
            {
                app.SetUserPreferenceIntegerValue((int)swUserPreferenceIntegerValue_e.swImportStlVrmlModelType, (int)swImportStlVrmlModelType_e.swImportStlVrmlModelType_Surface);
                app.SetUserPreferenceIntegerValue((int)swUserPreferenceIntegerValue_e.swImportStlVrmlUnits, (int)swLengthUnit_e.swMM);
                app.SetUserPreferenceToggle((int)swUserPreferenceToggle_e.swVrmlStlImportAsPSMesh, true);
                app.DocumentVisible(false, (int)swDocumentTypes_e.swDocPART);   // no window flashing up
                int err = 0, warn = 0;
                m = app.LoadFile4(stlPath, "r", null, ref err) as IModelDoc2;
                if (m == null)
                    throw new Core.Jobs.Res3dException("SolidWorks could not read the display mesh (error " + err + ").",
                        "Check that " + Path.GetFileName(stlPath) + " exists in the scan cache folder. Try Import Scan again with a lower display triangle count.");
                if (!m.Extension.SaveAs(meshPart, (int)swSaveAsVersion_e.swSaveAsCurrentVersion, (int)swSaveAsOptions_e.swSaveAsOptions_Silent, null, ref err, ref warn))
                    throw new Core.Jobs.Res3dException("SolidWorks could not save the display mesh part (error " + err + ").",
                        "Check that the scan cache folder next to the part is writable.");
            }
            finally
            {
                app.DocumentVisible(true, (int)swDocumentTypes_e.swDocPART);
                if (m != null) app.CloseDoc(m.GetTitle());
                app.SetUserPreferenceIntegerValue((int)swUserPreferenceIntegerValue_e.swImportStlVrmlModelType, oldType);
                app.SetUserPreferenceIntegerValue((int)swUserPreferenceIntegerValue_e.swImportStlVrmlUnits, oldUnits);
                app.SetUserPreferenceToggle((int)swUserPreferenceToggle_e.swVrmlStlImportAsPSMesh, oldPsMesh);
            }
            return meshPart;
        }

        /// <summary>
        /// Close cached display parts SolidWorks still holds in memory although no feature uses them any more,
        /// so the cache can be pruned or deleted. Only invisible documents inside `folder` other than `keep` (the
        /// one the scan feature uses now) are touched.
        /// </summary>
        public void ReleaseCached(string folder, string keep)
        {
            var docs = doc.App.GetDocuments() as object[];
            if (docs == null || string.IsNullOrEmpty(folder)) return;
            string root = Path.GetFullPath(folder).TrimEnd('\\') + "\\";
            foreach (var o in docs)
            {
                var d = o as IModelDoc2;
                if (d == null || d.Visible || ReferenceEquals(d, doc.Doc)) continue;
                string p = d.GetPathName();
                if (!string.IsNullOrEmpty(p) && Path.GetFullPath(p).StartsWith(root, StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(Path.GetFullPath(p), keep == null ? null : Path.GetFullPath(keep), StringComparison.OrdinalIgnoreCase))
                    try { doc.App.CloseDoc(d.GetTitle()); } catch (Exception) { }
            }
        }

        public void Remove(string featureName, bool deleteEmptyFolder = true)
        {
            var f = doc.FindFeature(featureName);
            if (f != null) doc.Delete(f);
            if (deleteEmptyFolder) doc.DeleteFolderIfEmpty(Folder);
        }

        /// <summary>Scan features: RE-Scan3D's own ("Scan - ...") plus any graphics/mesh-body feature the user inserted
        /// themselves (an imported mesh or a derived part), so both sources work the same (spec §7).</summary>
        public List<IFeature> Find(string knownName)
        {
            var list = new List<IFeature>();
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var f in doc.AllFeatures())
            {
                string n = f.Name;
                if (string.Equals(n, knownName, StringComparison.OrdinalIgnoreCase) || n.StartsWith(Prefix, StringComparison.Ordinal) ||
                    f.GetTypeName2() == "MeshFeatureContainer")   // a mesh the user opened/imported as graphics bodies
                    if (names.Add(n)) list.Add(f);
            }
            // graphics/mesh bodies however they got there (imported mesh, inserted part): their owning features
            foreach (var kv in doc.MeshBodies())
                if (kv.Value != null && names.Add(kv.Value.Name)) list.Add(kv.Value);
            return list;
        }
    }
}
