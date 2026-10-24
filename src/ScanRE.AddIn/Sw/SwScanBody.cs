// The scan's display copy in the part: a decimated binary STL imported as a GRAPHICS body (lightest
// SolidWorks mesh representation) in a clearly named feature inside a "Scan" folder (spec §1).
// Route chosen first: IPartDoc.InsertImportedFeature with the STL import preference set to graphics body
// (INVESTIGATION.md Q1 lists the alternatives to measure).
using System;
using System.Collections.Generic;
using System.IO;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace ScanRE.AddIn.Sw
{
    public sealed class SwScanBody
    {
        public const string Folder = "Scan";
        public const string Prefix = "Scan - ";
        private readonly SwDoc doc;

        public SwScanBody(SwDoc doc) { this.doc = doc; }

        /// <summary>
        /// Import `stlPath` (millimetres) as a graphics-body feature, replacing `oldFeature` if given.
        /// Returns the new feature's name. The user's STL import preferences are restored afterwards.
        /// </summary>
        public string Insert(string stlPath, string scanName, string oldFeature, out long importMs)
        {
            var app = doc.App;
            int oldType = app.GetUserPreferenceIntegerValue((int)swUserPreferenceIntegerValue_e.swImportStlVrmlModelType);
            int oldUnits = app.GetUserPreferenceIntegerValue((int)swUserPreferenceIntegerValue_e.swImportStlVrmlUnits);
            bool oldPsMesh = app.GetUserPreferenceToggle((int)swUserPreferenceToggle_e.swVrmlStlImportAsPSMesh);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            IFeature feat;
            try
            {
                app.SetUserPreferenceIntegerValue((int)swUserPreferenceIntegerValue_e.swImportStlVrmlModelType, (int)swImportStlVrmlModelType_e.swImportStlVrmlModelType_Graphics);
                app.SetUserPreferenceIntegerValue((int)swUserPreferenceIntegerValue_e.swImportStlVrmlUnits, (int)swLengthUnit_e.swMM);
                app.SetUserPreferenceToggle((int)swUserPreferenceToggle_e.swVrmlStlImportAsPSMesh, false);

                if (oldFeature != null) Remove(oldFeature);
                int err = 0;
                var part = (IPartDoc)doc.Doc;
                feat = part.InsertImportedFeature(stlPath, out err) as IFeature;
                if (feat == null)
                    throw new Core.Jobs.ScanReException("SolidWorks could not import the display mesh (error " + err + ").",
                        "Check that " + Path.GetFileName(stlPath) + " exists in the scan cache folder. Try Import Scan again with a lower display triangle count.");
            }
            finally
            {
                app.SetUserPreferenceIntegerValue((int)swUserPreferenceIntegerValue_e.swImportStlVrmlModelType, oldType);
                app.SetUserPreferenceIntegerValue((int)swUserPreferenceIntegerValue_e.swImportStlVrmlUnits, oldUnits);
                app.SetUserPreferenceToggle((int)swUserPreferenceToggle_e.swVrmlStlImportAsPSMesh, oldPsMesh);
            }
            importMs = sw.ElapsedMilliseconds;
            doc.Rename(feat, Prefix + scanName);
            feat.Description = "ScanRE temporary scan display. Delete with Finish Reverse Engineering; re-attach with Re-attach Scan.";
            doc.IntoFolder(feat, Folder);
            doc.ClearSelection();
            return feat.Name;
        }

        public void Remove(string featureName)
        {
            var f = doc.FindFeature(featureName);
            if (f != null) doc.Delete(f);
            doc.DeleteFolderIfEmpty(Folder);
        }

        /// <summary>Scan features: ScanRE's own ("Scan - ...") plus any graphics/mesh-body feature the user inserted
        /// themselves (an imported mesh or a derived part), so both sources work the same (spec §7).</summary>
        public List<IFeature> Find(string knownName)
        {
            var list = new List<IFeature>();
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var f in doc.AllFeatures())
            {
                string n = f.Name;
                if (string.Equals(n, knownName, StringComparison.OrdinalIgnoreCase) || n.StartsWith(Prefix, StringComparison.Ordinal))
                    if (names.Add(n)) list.Add(f);
            }
            // graphics/mesh bodies however they got there (imported mesh, inserted part): their owning features
            foreach (var kv in doc.MeshBodies())
                if (kv.Value != null && names.Add(kv.Value.Name)) list.Add(kv.Value);
            return list;
        }
    }
}
