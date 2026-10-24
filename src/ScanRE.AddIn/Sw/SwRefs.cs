// Finish / check (spec §6-7): find everything in the part that still depends on the scan, so the user can
// fix it before the scan is deleted and the part opens cleanly without the add-in.
using System;
using System.Collections.Generic;
using SolidWorks.Interop.sldworks;

namespace ScanRE.AddIn.Sw
{
    public sealed class ScanReference
    {
        public string Feature;     // the dependent feature
        public string Type;        // its SolidWorks type name
        public string Detail;      // what depends on what
        public bool IsTemporary;   // ScanRE's own temporary sketches: removable automatically
    }

    public sealed class SwRefs
    {
        private readonly SwDoc doc;
        public SwRefs(SwDoc doc) { this.doc = doc; }

        public List<ScanReference> Find(IList<IFeature> scanFeatures)
        {
            var list = new List<ScanReference>();
            foreach (var scan in scanFeatures)
            {
                foreach (var child in doc.Children(scan))
                {
                    list.Add(new ScanReference
                    {
                        Feature = child.Name, Type = child.GetTypeName2(),
                        Detail = "depends on " + scan.Name + Describe(child, scan),
                        IsTemporary = child.Name.StartsWith(SwSketch.TempPrefix, StringComparison.Ordinal)
                    });
                }
            }
            // temporary displays, and anything the user related to them while sketching (would dangle after Finish)
            foreach (var f in doc.AllFeatures())
            {
                if (!f.Name.StartsWith(SwSketch.TempPrefix, StringComparison.Ordinal)) continue;
                if (!list.Exists(r => r.Feature == f.Name))
                    list.Add(new ScanReference { Feature = f.Name, Type = f.GetTypeName2(), Detail = "ScanRE temporary display", IsTemporary = true });
                foreach (var child in doc.Children(f))
                    if (!child.Name.StartsWith(SwSketch.TempPrefix, StringComparison.Ordinal))
                        list.Add(new ScanReference
                        {
                            Feature = child.Name, Type = child.GetTypeName2(),
                            Detail = "has relations to the temporary section display " + f.Name + ": delete those relations (Display/Delete Relations)"
                        });
            }
            return list;
        }

        /// <summary>For a sketch, count the relations whose entities lie on a graphics or mesh body.</summary>
        private string Describe(IFeature child, IFeature scan)
        {
            var sk = child.GetSpecificFeature2() as ISketch;
            if (sk == null) return "";
            int n = 0;
            try
            {
                var rm = sk.RelationManager;
                var rels = rm.GetRelations(0) as object[];   // 0 = all relations
                if (rels != null)
                    foreach (var o in rels)
                    {
                        var rel = (ISketchRelation)o;
                        var ents = rel.GetDefinitionEntities2() as object[];
                        if (ents == null) continue;
                        foreach (var e in ents)
                            if (OnMesh(e)) { n++; break; }
                    }
            }
            catch (Exception) { return ""; }
            return n > 0 ? string.Format(" ({0} sketch relation(s) to the scan: open the sketch, Display/Delete Relations, delete those marked as external)", n) : "";
        }

        private static bool OnMesh(object e)
        {
            Body2 body = null;
            if (e is IEdge) body = ((IEdge)e).GetBody();
            else if (e is IFace2) body = ((IFace2)e).IGetBody();
            else if (e is IVertex)
            {
                var edges = ((IVertex)e).GetEdges() as object[];
                if (edges != null && edges.Length > 0) body = ((IEdge)edges[0]).GetBody();
            }
            return body != null && (body.IsGraphicsBody() || body.IsMeshBody());
        }
    }
}
