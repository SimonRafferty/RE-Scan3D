// Everything the add-in remembers about a part's scan: source, alignment (mates + transform), fitted
// features. Saved as state.json in the sidecar folder; the alignment part is also mirrored into a part
// custom property so re-attaching works even if the sidecar is lost (spec §2 "stored with the part and the sidecar").
using System;
using System.Collections.Generic;
using System.Web.Script.Serialization;
using ScanRE.Core.Align;
using ScanRE.Core.Math3;

namespace ScanRE.Core.Cache
{
    public sealed class FitRecord
    {
        public string Id;              // "F1", "F2"...
        public string Kind;            // "plane" | "cylinder"
        public double[] Origin;        // scan coordinates (mm): plane origin / axis point
        public double[] Direction;     // plane normal / axis direction (scan coordinates)
        public double Radius;          // cylinders
        public double Rms, MaxDev;
        public int Points, Outliers;
        public string Method;          // "region" | "points"
        public string Snap;            // relations applied, or ""
        public string FeatureName;     // the SolidWorks feature created from it ("" for alignment-only fits)
        public string Created;         // ISO time

        public Plane3 PlaneScan()
        {
            return new Plane3(new Vec3(Origin[0], Origin[1], Origin[2]), new Vec3(Direction[0], Direction[1], Direction[2]));
        }

        public static double[] A(Vec3 v) { return new[] { v.X, v.Y, v.Z }; }
    }

    public sealed class ScanState
    {
        public const int CurrentVersion = 1;

        public int Version = CurrentVersion;
        public string SourcePath;
        public long SourceSize;
        public string SourceStamp;     // fast content fingerprint (Sidecar.Fingerprint)
        /// <summary>Scan units -> mm, applied on load (1 for mm scans, 25.4 for inch, 1000 for metres).</summary>
        public double UnitScale = 1;
        public double IslandFraction = 0.01;

        /// <summary>Solved scan -> part transform (12 numbers: row-major R then T).</summary>
        public double[] Transform = Rigid.Identity.ToArray();
        /// <summary>Manual nudge applied after the solve (part coordinates).</summary>
        public double[] Nudge = Rigid.Identity.ToArray();
        public SolverMode Solver = SolverMode.Datum;
        public List<AlignMate> Mates = new List<AlignMate>();
        public bool AlignmentAccepted;
        /// <summary>Base plane name -> the Scan-* plane feature made for it.</summary>
        public Dictionary<string, string> ScanPlanes = new Dictionary<string, string>();

        public List<FitRecord> Fits = new List<FitRecord>();
        public int NextFit = 1;

        public int DisplayTriangles = 150000;
        public string ScanFeatureName;   // the graphics-body feature in the part

        public Rigid ScanToPart()
        {
            return Rigid.Then(Rigid.FromArray(Transform), Rigid.FromArray(Nudge));
        }

        public FitRecord Fit(string id) { return Fits.Find(f => f.Id == id); }

        public string NewFitId() { return "F" + (NextFit++); }

        // ------------------------------------------------------------------ JSON

        private static JavaScriptSerializer Json()
        {
            return new JavaScriptSerializer { MaxJsonLength = int.MaxValue, RecursionLimit = 64 };
        }

        public string ToJson() { return Json().Serialize(this); }

        public static ScanState FromJson(string json)
        {
            var s = Json().Deserialize<ScanState>(json);
            if (s.Mates == null) s.Mates = new List<AlignMate>();
            if (s.Fits == null) s.Fits = new List<FitRecord>();
            if (s.ScanPlanes == null) s.ScanPlanes = new Dictionary<string, string>();
            return s;
        }

        /// <summary>The small alignment-only subset stored in the part's custom property.</summary>
        public string AlignmentJson()
        {
            var a = new AlignmentSnapshot
            {
                Transform = Transform, Nudge = Nudge, Solver = Solver, Mates = Mates, Accepted = AlignmentAccepted,
                SourcePath = SourcePath, Fits = Fits.FindAll(f => Mates.Exists(m => m.FitId == f.Id))
            };
            return Json().Serialize(a);
        }

        public void ApplyAlignmentJson(string json)
        {
            var a = Json().Deserialize<AlignmentSnapshot>(json);
            if (a == null) return;
            Transform = a.Transform ?? Transform; Nudge = a.Nudge ?? Nudge; Solver = a.Solver;
            Mates = a.Mates ?? new List<AlignMate>(); AlignmentAccepted = a.Accepted;
            if (a.Fits != null)
                foreach (var f in a.Fits) if (Fit(f.Id) == null) Fits.Add(f);
        }

        public static string Now() { return DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss"); }
    }

    public sealed class AlignmentSnapshot
    {
        public double[] Transform, Nudge;
        public SolverMode Solver;
        public List<AlignMate> Mates;
        public bool Accepted;
        public string SourcePath;
        public List<FitRecord> Fits;
    }
}
