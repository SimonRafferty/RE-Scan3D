// *** SolidWorks interface layer *** (DECISIONS D4): every SolidWorks API call in RE-Scan3D lives in this
// folder. Only long-established calls are used; the core never sees a SolidWorks type. SolidWorks works
// in metres, the core in millimetres: Units converts at this boundary and nowhere else.
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using RES3D.Core.Math3;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace RES3D.AddIn.Sw
{
    public static class Units
    {
        public const double MmPerM = 1000.0;
        public static double ToM(double mm) { return mm / MmPerM; }
        public static double ToMm(double m) { return m * MmPerM; }
        public static Vec3 ToMm(double[] m, int offset = 0) { return new Vec3(m[offset] * MmPerM, m[offset + 1] * MmPerM, m[offset + 2] * MmPerM); }
        public static double[] ToM(Vec3 mm) { return new[] { mm.X / MmPerM, mm.Y / MmPerM, mm.Z / MmPerM }; }
    }

    /// <summary>Feature-tree and document helpers for one part.</summary>
    public sealed class SwDoc
    {
        public readonly ISldWorks App;

        /// <summary>The section outline drawn in the user's sketch (construction splines), and that sketch's name.</summary>
        public List<object> OutlineEntities;
        public string OutlineSketch;
        public readonly IModelDoc2 Doc;

        public SwDoc(ISldWorks app, IModelDoc2 doc) { App = app; Doc = doc; }

        public string PathName { get { return Doc.GetPathName(); } }
        public string Title { get { return Doc.GetTitle(); } }
        public IFeatureManager Features { get { return Doc.FeatureManager; } }
        public ISketchManager Sketches { get { return Doc.SketchManager; } }
        public IModelDocExtension Ext { get { return Doc.Extension; } }
        public ISelectionMgr Selection { get { return (ISelectionMgr)Doc.SelectionManager; } }
        public IMathUtility Math { get { return (IMathUtility)App.GetMathUtility(); } }

        public bool IsPart { get { return Doc.GetType() == (int)swDocumentTypes_e.swDocPART; } }

        // ------------------------------------------------------------------ features

        public IEnumerable<IFeature> AllFeatures()
        {
            var f = (IFeature)Doc.FirstFeature();
            while (f != null)
            {
                yield return f;
                var sub = (IFeature)f.GetFirstSubFeature();
                while (sub != null)
                {
                    yield return sub;
                    sub = (IFeature)sub.GetNextSubFeature();
                }
                f = (IFeature)f.GetNextFeature();
            }
        }

        public IFeature FindFeature(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            foreach (var f in AllFeatures())
                if (string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase)) return f;
            return null;
        }

        /// <summary>Base planes by their type order (Front, Top, Right are the first three RefPlanes), so renamed or localised planes still work.</summary>
        public IFeature BasePlane(Core.Align.BasePlane p)
        {
            int k = 0;
            foreach (var f in AllFeatures())
            {
                if (f.GetTypeName2() != "RefPlane") continue;
                if (k == (int)p) return f;
                if (++k > 2) break;
            }
            return FindFeature(Core.Align.BasePlanes.SwName(p));
        }

        /// <summary>A unique feature name: "Base", "Base (2)", ...</summary>
        public string UniqueName(string wanted)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var f in AllFeatures()) names.Add(f.Name);
            if (!names.Contains(wanted)) return wanted;
            for (int i = 2; ; i++)
            {
                var n = wanted + " (" + i + ")";
                if (!names.Contains(n)) return n;
            }
        }

        public void Rename(IFeature f, string name)
        {
            // SolidWorks rejects '@' and a few other characters in feature names
            name = name.Replace('@', 'a').Replace('/', '-').Replace('\\', '-');
            f.Name = UniqueName(name);
        }

        // ------------------------------------------------------------------ sketch edit state

        /// <summary>
        /// Leave whatever sketch (2D or 3D) is being edited, so a new 3D sketch really is new and nothing lands in
        /// the user's sketch (review C2/C3). Returns the sketch's feature name, or null if none was open.
        /// </summary>
        public string LeaveSketch()
        {
            var sk = Sketches.ActiveSketch;
            if (sk == null) return null;
            string name = ((IFeature)sk).Name;
            // SolidWorks discards an EMPTY sketch when it is closed (seen in 2025), and users open Section and Snap
            // in a new sketch: keep it alive with a placeholder point, removed again by ReenterSketch
            var segs = sk.GetSketchSegments() as object[];
            var pts = sk.GetSketchPoints2() as object[];
            if ((segs == null || segs.Length == 0) && (pts == null || pts.Length == 0))
            {
                bool oldDb = Sketches.AddToDB;
                Sketches.AddToDB = true;
                try { if (Sketches.CreatePoint(Placeholder[0], Placeholder[1], Placeholder[2]) != null) placeholderIn = name; }
                finally { Sketches.AddToDB = oldDb; }
            }
            if (sk.Is3D()) Sketches.Insert3DSketch(true); else Sketches.InsertSketch(true);
            return name;
        }

        private static readonly double[] Placeholder = { 0.000123457, -0.000098765, 0 };   // metres, sketch space
        private string placeholderIn;

        public void ReenterSketch(string name)
        {
            if (name == null) return;
            var f = FindFeature(name);
            if (f == null) return;
            ClearSelection();
            f.Select2(false, 0);
            Doc.EditSketch();
            ClearSelection();
            var sk = Sketches.ActiveSketch;
            if (placeholderIn == name && sk != null)
            {
                placeholderIn = null;
                var pts = sk.GetSketchPoints2() as object[];
                if (pts != null)
                    foreach (SketchPoint p in pts)
                        if (System.Math.Abs(p.X - Placeholder[0]) < 1e-9 && System.Math.Abs(p.Y - Placeholder[1]) < 1e-9 && System.Math.Abs(p.Z - Placeholder[2]) < 1e-9)
                        {
                            if (p.Select4(false, Selection.CreateSelectData())) Doc.EditDelete();
                            ClearSelection();
                        }
            }
            if (sk == null || ((IFeature)sk).Name != name)
                Log.Info("could not re-enter sketch " + name + " (active: " + (sk == null ? "none" : ((IFeature)sk).Name) + ")");
        }

        /// <summary>Open a NEW 3D sketch (call LeaveSketch first); throws if SolidWorks didn't.</summary>
        public ISketch Begin3DSketch()
        {
            ClearSelection();
            Sketches.Insert3DSketch(true);
            var sk = Sketches.ActiveSketch;
            if (sk == null || !sk.Is3D())
                throw new Core.Jobs.Res3dException("SolidWorks did not open a new 3D sketch.",
                    "Close any open sketch or command (Esc) and try again.");
            return sk;
        }

        /// <summary>Graphics and mesh bodies in the part, each with the feature that owns it.</summary>
        public List<KeyValuePair<IBody2, IFeature>> MeshBodies()
        {
            var list = new List<KeyValuePair<IBody2, IFeature>>();
            var part = Doc as IPartDoc;
            if (part == null) return list;
            // asking GetBodies2 for graphics/mesh bodies by type returns nothing (SolidWorks 2025): list all, filter
            var bodies = part.GetBodies2((int)swBodyType_e.swAllBodies, false) as object[];
            if (bodies == null) return list;
            foreach (var o in bodies)
            {
                var b = (IBody2)o;
                int type = b.GetType();
                if (type != (int)swBodyType_e.swGraphicsBody && type != (int)swBodyType_e.swMeshBody) continue;
                var feats = b.GetFeatures() as object[];
                var f = feats != null && feats.Length > 0 ? feats[0] as IFeature : null;
                list.Add(new KeyValuePair<IBody2, IFeature>(b, f));
            }
            return list;
        }

        public void ClearSelection() { Doc.ClearSelection2(true); }

        public bool Delete(IFeature f)
        {
            if (f == null) return true;
            ClearSelection();
            if (!f.Select2(false, 0)) return false;
            // no option flags: children are NOT deleted with it (RE-Scan3D never deletes user geometry)
            return Ext.DeleteSelection2(0);
        }

        /// <summary>Put `features` in a feature-tree folder named `folder` (created if needed).</summary>
        public void IntoFolder(IFeature feature, string folder)
        {
            var existing = FindFeature(folder);
            if (existing != null && existing.GetTypeName2() == "FtrFolder")
            {
                Ext.ReorderFeature(feature.Name, existing.Name, (int)swMoveLocation_e.swMoveToFolder);
                return;
            }
            ClearSelection();
            feature.Select2(false, 0);
            var f = Features.InsertFeatureTreeFolder2((int)swFeatureTreeFolderType_e.swFeatureTreeFolder_Containing);
            if (f != null) f.Name = UniqueName(folder);
            ClearSelection();
        }

        /// <summary>Delete an empty folder (deleting a folder never deletes its contents).</summary>
        public void DeleteFolderIfEmpty(string folder)
        {
            var f = FindFeature(folder);
            if (f == null || f.GetTypeName2() != "FtrFolder") return;
            var ff = f.GetSpecificFeature2() as IFeatureFolder;
            if (ff == null || ff.GetFeatureCount() > 0) return;
            Delete(f);
        }

        public void Rebuild() { Doc.ForceRebuild3(false); }

        public void Redraw() { Doc.GraphicsRedraw2(); }

        public void Hide(IFeature f)
        {
            ClearSelection();
            if (f.Select2(false, 0))
            {
                if (f.GetTypeName2() == "3DProfileFeature" || f.GetTypeName2() == "ProfileFeature") Doc.BlankSketch();
                else Doc.BlankRefGeom();
            }
            ClearSelection();
        }

        /// <summary>The features that depend on `f` (what would dangle if it were deleted).</summary>
        public List<IFeature> Children(IFeature f)
        {
            var list = new List<IFeature>();
            var arr = f.GetChildren() as object[];
            if (arr != null) foreach (var o in arr) if (o is IFeature) list.Add((IFeature)o);
            return list;
        }

        // ------------------------------------------------------------------ custom properties

        private const int ChunkSize = 240;   // stay under the 255-character limit of older property readers

        /// <summary>
        /// Store a long string in numbered custom properties "<name>.0", "<name>.1", ... Base64, so SolidWorks
        /// can't trim, re-quote or evaluate any chunk; read back and compared.
        /// </summary>
        public void SetLongProperty(string name, string text)
        {
            string value = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(text));
            var pm = Ext.get_CustomPropertyManager("");
            DeleteLongProperty(name);
            int n = 0;
            for (int i = 0; i < value.Length; i += ChunkSize, n++)
                pm.Add3(name + "." + n, (int)swCustomInfoType_e.swCustomInfoText, value.Substring(i, System.Math.Min(ChunkSize, value.Length - i)),
                        (int)swCustomPropertyAddOption_e.swCustomPropertyReplaceValue);
            Doc.SetSaveFlag();
            if (GetLongProperty(name) != text) Log.Info("custom property " + name + " did not read back identically");
        }

        public string GetLongProperty(string name)
        {
            var pm = Ext.get_CustomPropertyManager("");
            var sb = new System.Text.StringBuilder();
            for (int n = 0; ; n++)
            {
                string val, resolved;
                bool wasResolved, linked;
                int r = pm.Get6(name + "." + n, false, out val, out resolved, out wasResolved, out linked);
                if (r == (int)swCustomInfoGetResult_e.swCustomInfoGetResult_NotPresent || val == null) break;
                sb.Append(val);
            }
            if (sb.Length == 0) return null;
            try { return System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(sb.ToString())); }
            catch (FormatException) { return null; }
        }

        public void DeleteLongProperty(string name)
        {
            var pm = Ext.get_CustomPropertyManager("");
            var names = pm.GetNames() as object[];
            if (names == null) return;
            foreach (var o in names)
            {
                var s = o as string;
                if (s != null && s.StartsWith(name + ".", StringComparison.Ordinal)) pm.Delete2(s);
            }
        }

        // ------------------------------------------------------------------ math helpers

        /// <summary>Apply a SolidWorks transform to a point (metres in, metres out).</summary>
        public double[] TransformPoint(MathTransform t, double[] p)
        {
            var mp = (MathPoint)Math.CreatePoint(p);
            mp = (MathPoint)mp.MultiplyTransform(t);
            return (double[])mp.ArrayData;
        }

        public double[] TransformVector(MathTransform t, double[] v)
        {
            var mv = (MathVector)Math.CreateVector(v);
            mv = (MathVector)mv.MultiplyTransform(t);
            return (double[])mv.ArrayData;
        }

        public static void Release(object o)
        {
            if (o != null && Marshal.IsComObject(o)) Marshal.ReleaseComObject(o);
        }
    }
}
