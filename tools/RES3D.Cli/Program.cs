// RE-Scan3D command line: the core operations on a mesh file, without SolidWorks. For checking a real scan
// (does it load, how big, how flat is that face) and for timing.
//   RES3D.Cli info <scan>
//   RES3D.Cli decimate <scan> <triangles> <out.stl>
//   RES3D.Cli fitplane <scan> <x> <y> <z> <dx> <dy> <dz>      (ray from x,y,z along d, mm)
//   RES3D.Cli fitcyl   <scan> <x> <y> <z> <dx> <dy> <dz>
//   RES3D.Cli section  <scan> <px> <py> <pz> <nx> <ny> <nz> [slabHalf]
using System;
using System.Diagnostics;
using System.Globalization;
using RES3D.Core.Decimate;
using RES3D.Core.Fit;
using RES3D.Core.Jobs;
using RES3D.Core.Math3;
using RES3D.Core.Mesh;
using RES3D.Core.Section;
using RES3D.Core.Spatial;

namespace RES3D.Cli
{
    public static class Program
    {
        public static int Main(string[] a)
        {
            System.Threading.Thread.CurrentThread.CurrentCulture = CultureInfo.InvariantCulture;
            if (a.Length < 2) { Usage(); return 2; }
            var job = new JobContext(System.Threading.CancellationToken.None, (s, f) => Console.Error.Write("\r{0,-40} {1,5:P0}", s, f));
            try
            {
                var sw = Stopwatch.StartNew();
                var m = MeshIO.Read(a[1], job);
                Console.Error.WriteLine();
                Console.WriteLine("loaded {0:N0} triangles, {1:N0} vertices in {2} ms{3}", m.TriangleCount, m.VertexCount, sw.ElapsedMilliseconds, m.Colors != null ? " (with colour)" : "");
                switch (a[0])
                {
                    case "info":
                        {
                            Vec3 lo, hi; m.Bounds(out lo, out hi);
                            Console.WriteLine("bounds {0} .. {1} mm, size {2}", lo, hi, hi - lo);
                            Console.WriteLine("mean edge {0:0.###} mm", m.MeanEdgeLength());
                            var adj = new TriAdjacency(m, job);
                            int[] comp;
                            Console.WriteLine("{0} connected components", Topology.Components(m, adj, out comp));
                            return 0;
                        }
                    case "decimate":
                        {
                            sw.Restart();
                            var d = Qem.Decimate(m, new QemOptions { TargetTriangles = int.Parse(a[2]) }, job);
                            Console.Error.WriteLine();
                            Console.WriteLine("decimated to {0:N0} in {1} ms", d.TriangleCount, sw.ElapsedMilliseconds);
                            MeshIO.WriteStlBinary(a[3], d, JobContext.None);
                            return 0;
                        }
                    case "fitplane":
                    case "fitcyl":
                        {
                            var bvh = Bvh.Build(m, job);
                            var adj = new TriAdjacency(m, job);
                            var ray = new Ray3(V(a, 2), V(a, 5));
                            var hit = bvh.Raycast(ray);
                            if (!hit.Hit) { Console.WriteLine("ray misses the scan"); return 1; }
                            Console.Error.WriteLine();
                            if (a[0] == "fitplane")
                            {
                                var f = RegionGrow.GrowPlane(m, adj, hit.Triangle, new GrowOptions(), job);
                                Console.WriteLine("plane origin {0} normal {1}; {2}; {3} triangles", f.Plane.Origin, f.Plane.Normal, f.Summary(), f.Triangles.Count);
                            }
                            else
                            {
                                var f = RegionGrow.GrowCylinder(m, adj, hit.Triangle, new GrowOptions(), job);
                                Console.WriteLine("axis {0} dir {1}; {2}; {3}", f.AxisPoint, f.AxisDir, f.Summary(), f.Convex ? "boss" : "hole");
                            }
                            return 0;
                        }
                    case "section":
                        {
                            var bvh = Bvh.Build(m, job);
                            var opt = new SectionOptions { SlabHalfThickness = a.Length > 8 ? D(a[8]) : 0 };
                            sw.Restart();
                            var s = PlaneSection.Compute(bvh, Rigid.Identity, new Plane3(V(a, 2), V(a, 5)), opt, job);
                            Console.Error.WriteLine();
                            Console.WriteLine("{0} curves, {1} segments in {2} ms", s.Lines.Count, s.SegmentTotal, sw.ElapsedMilliseconds);
                            foreach (var l in s.Lines) Console.WriteLine("  {0} {1:0.##} mm, {2} points", l.Closed ? "closed" : "open  ", l.Length, l.Points.Count);
                            return 0;
                        }
                }
                Usage();
                return 2;
            }
            catch (Res3dException e) { Console.Error.WriteLine(); Console.WriteLine(e.ToString()); return 1; }
        }

        private static double D(string s) { return double.Parse(s, CultureInfo.InvariantCulture); }
        private static Vec3 V(string[] a, int i) { return new Vec3(D(a[i]), D(a[i + 1]), D(a[i + 2])); }

        private static void Usage()
        {
            Console.WriteLine("RES3D.Cli info|decimate|fitplane|fitcyl|section <scan> ...  (see the header of tools\\RES3D.Cli\\Program.cs)");
        }
    }
}
