// Readers for STL (binary + ASCII), OBJ and PLY (ASCII, binary LE/BE, vertex colour), and writers for
// binary STL (the display copy handed to SolidWorks) and binary PLY. Every reader returns a welded mesh.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using RES3D.Core.Jobs;

namespace RES3D.Core.Mesh
{
    public static class MeshIO
    {
        public static readonly string[] Extensions = { ".stl", ".obj", ".ply" };

        public static TriMesh Read(string path, JobContext job)
        {
            if (!File.Exists(path))
                throw new Res3dException("Scan file not found: " + path, "Check the path, or browse to the file again.");
            string ext = Path.GetExtension(path).ToLowerInvariant();
            try
            {
                switch (ext)
                {
                    case ".stl": return ReadStl(path, job);
                    case ".obj": return ReadObj(path, job);
                    case ".ply": return ReadPly(path, job);
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Res3dException) { throw; }
            catch (Exception e)
            {
                throw new Res3dException("Could not read " + Path.GetFileName(path) + ": " + e.Message,
                    "The file may be truncated or in an unsupported variant. Re-export it from the scanner software as binary STL or PLY.", e);
            }
            throw new Res3dException("Unsupported scan format '" + ext + "'.", "Export the scan as STL, OBJ or PLY.");
        }

        // ------------------------------------------------------------------ STL

        private static TriMesh ReadStl(string path, JobContext job)
        {
            long len = new FileInfo(path).Length;
            bool binary = false;
            if (len >= 84)
            {
                using (var f = File.OpenRead(path))
                {
                    var hdr = new byte[84];
                    f.Read(hdr, 0, 84);
                    uint n = BitConverter.ToUInt32(hdr, 80);
                    // the size test is decisive: ASCII files that begin "solid" exist in binary (SolidWorks writes them)
                    bool solid = Encoding.ASCII.GetString(hdr, 0, 5).Equals("solid", StringComparison.OrdinalIgnoreCase);
                    binary = 84L + 50L * n == len || (!solid && 84L + 50L * n <= len);   // some writers pad the end
                }
            }
            return binary ? ReadStlBinary(path, job) : ReadStlAscii(path, job);
        }

        private static TriMesh ReadStlBinary(string path, JobContext job)
        {
            using (var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20))
            using (var r = new BinaryReader(f))
            {
                r.ReadBytes(80);
                int n = checked((int)r.ReadUInt32());
                var soup = new float[9 * n];
                var buf = new byte[50 * 4096];
                int done = 0;
                while (done < n)
                {
                    int chunk = Math.Min(4096, n - done);
                    int got = r.Read(buf, 0, 50 * chunk);
                    if (got < 50 * chunk) throw new EndOfStreamException("binary STL is shorter than its triangle count says");
                    for (int i = 0; i < chunk; i++)
                    {
                        int o = 50 * i + 12;   // skip the facet normal: recomputed from the vertices
                        int w = 9 * (done + i);
                        for (int k = 0; k < 9; k++) soup[w + k] = BitConverter.ToSingle(buf, o + 4 * k);
                    }
                    done += chunk;
                    job.Step("Reading STL", done, n, 4096);
                }
                return Weld.FromSoup(soup, job);
            }
        }

        private static TriMesh ReadStlAscii(string path, JobContext job)
        {
            var soup = new List<float>(1 << 20);
            long len = Math.Max(1, new FileInfo(path).Length);
            using (var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20))
            using (var r = new StreamReader(f, Encoding.ASCII))
            {
                string line;
                long lines = 0;
                while ((line = r.ReadLine()) != null)
                {
                    if (++lines % 8192 == 0) job.Report("Reading STL", (double)f.Position / len);
                    var s = line.TrimStart();
                    if (!s.StartsWith("vertex", StringComparison.OrdinalIgnoreCase)) continue;
                    var p = Split(s);
                    if (p.Count < 4) throw new FormatException("bad vertex line: " + line);
                    soup.Add(ParseF(p[1])); soup.Add(ParseF(p[2])); soup.Add(ParseF(p[3]));
                }
            }
            if (soup.Count % 9 != 0) throw new FormatException("ASCII STL facet without three vertices");
            return Weld.FromSoup(soup.ToArray(), job);
        }

        // ------------------------------------------------------------------ OBJ

        private static TriMesh ReadObj(string path, JobContext job)
        {
            var pos = new List<float>(1 << 20);
            var col = new List<byte>();
            bool hasColour = true;
            var idx = new List<int>(1 << 20);
            long len = Math.Max(1, new FileInfo(path).Length);
            using (var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20))
            using (var r = new StreamReader(f, Encoding.UTF8))
            {
                string line;
                long lines = 0;
                var poly = new List<int>(8);
                while ((line = r.ReadLine()) != null)
                {
                    if (++lines % 8192 == 0) job.Report("Reading OBJ", (double)f.Position / len);
                    if (line.Length < 2) continue;
                    if (line[0] == 'v' && line[1] == ' ')
                    {
                        var p = Split(line);
                        pos.Add(ParseF(p[1])); pos.Add(ParseF(p[2])); pos.Add(ParseF(p[3]));
                        // common extension: "v x y z r g b" with r,g,b in 0..1
                        if (p.Count >= 7 && hasColour)
                        {
                            col.Add(ToByte(ParseF(p[4]))); col.Add(ToByte(ParseF(p[5]))); col.Add(ToByte(ParseF(p[6])));
                        }
                        else hasColour = false;
                    }
                    else if (line[0] == 'f' && line[1] == ' ')
                    {
                        var p = Split(line);
                        poly.Clear();
                        int nv = pos.Count / 3;
                        for (int i = 1; i < p.Count; i++)
                        {
                            var tok = p[i];
                            int slash = tok.IndexOf('/');
                            int v = int.Parse(slash >= 0 ? tok.Substring(0, slash) : tok, CultureInfo.InvariantCulture);
                            poly.Add(v < 0 ? nv + v : v - 1);   // OBJ is 1-based; negative = relative
                        }
                        for (int i = 1; i + 1 < poly.Count; i++)   // fan-triangulate polygons
                        {
                            idx.Add(poly[0]); idx.Add(poly[i]); idx.Add(poly[i + 1]);
                        }
                    }
                }
            }
            var mesh = new TriMesh(pos.ToArray(), idx.ToArray(), hasColour && col.Count == pos.Count ? col.ToArray() : null);
            return Weld.Indexed(mesh, job);
        }

        // ------------------------------------------------------------------ PLY

        private enum PlyFormat { Ascii, BinaryLE, BinaryBE }

        private sealed class PlyProp
        {
            public string Name, Type, CountType;   // CountType != null: a list property
        }

        private sealed class PlyElement
        {
            public string Name;
            public long Count;
            public readonly List<PlyProp> Props = new List<PlyProp>();
        }

        private static TriMesh ReadPly(string path, JobContext job)
        {
            using (var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20))
            {
                // ---- header (ASCII lines up to end_header)
                var elements = new List<PlyElement>();
                PlyFormat fmt = PlyFormat.Ascii;
                string magic = ReadHeaderLine(f);
                if (magic != "ply") throw new FormatException("not a PLY file");
                while (true)
                {
                    string line = ReadHeaderLine(f);
                    if (line == null) throw new FormatException("PLY header has no end_header");
                    var p = Split(line);
                    if (p.Count == 0) continue;
                    if (p[0] == "end_header") break;
                    if (p[0] == "format")
                        fmt = p[1] == "ascii" ? PlyFormat.Ascii : p[1] == "binary_little_endian" ? PlyFormat.BinaryLE : PlyFormat.BinaryBE;
                    else if (p[0] == "element")
                        elements.Add(new PlyElement { Name = p[1], Count = long.Parse(p[2], CultureInfo.InvariantCulture) });
                    else if (p[0] == "property" && elements.Count > 0)
                    {
                        var e = elements[elements.Count - 1];
                        if (p[1] == "list") e.Props.Add(new PlyProp { CountType = p[2], Type = p[3], Name = p[4] });
                        else e.Props.Add(new PlyProp { Type = p[1], Name = p[2] });
                    }
                }

                float[] pos = null; byte[] col = null; var idx = new List<int>();
                var src = fmt == PlyFormat.Ascii ? (IPlySource)new PlyAscii(f) : new PlyBinary(f, fmt == PlyFormat.BinaryBE);
                foreach (var e in elements)
                {
                    if (e.Name == "vertex")
                    {
                        int n = checked((int)e.Count);
                        pos = new float[3 * n];
                        int ix = e.Props.FindIndex(q => q.Name == "x"), iy = e.Props.FindIndex(q => q.Name == "y"), iz = e.Props.FindIndex(q => q.Name == "z");
                        int ir = e.Props.FindIndex(q => q.Name == "red" || q.Name == "r");
                        int ig = e.Props.FindIndex(q => q.Name == "green" || q.Name == "g");
                        int ib = e.Props.FindIndex(q => q.Name == "blue" || q.Name == "b");
                        if (ix < 0 || iy < 0 || iz < 0) throw new FormatException("PLY vertices have no x/y/z");
                        bool colour = ir >= 0 && ig >= 0 && ib >= 0;
                        bool colourIsFloat = colour && (e.Props[ir].Type.StartsWith("float") || e.Props[ir].Type == "double");
                        if (colour) col = new byte[3 * n];
                        var vals = new double[e.Props.Count];
                        for (int i = 0; i < n; i++)
                        {
                            for (int k = 0; k < e.Props.Count; k++)
                            {
                                var pr = e.Props[k];
                                if (pr.CountType != null) { long c = (long)src.Read(pr.CountType); for (long j = 0; j < c; j++) src.Read(pr.Type); }
                                else vals[k] = src.Read(pr.Type);
                            }
                            pos[3 * i] = (float)vals[ix]; pos[3 * i + 1] = (float)vals[iy]; pos[3 * i + 2] = (float)vals[iz];
                            if (colour)
                            {
                                col[3 * i] = colourIsFloat ? ToByte(vals[ir]) : (byte)vals[ir];
                                col[3 * i + 1] = colourIsFloat ? ToByte(vals[ig]) : (byte)vals[ig];
                                col[3 * i + 2] = colourIsFloat ? ToByte(vals[ib]) : (byte)vals[ib];
                            }
                            job.Step("Reading PLY vertices", i, n, 16384);
                        }
                    }
                    else if (e.Name == "face")
                    {
                        long n = e.Count;
                        var poly = new List<int>(8);
                        for (long i = 0; i < n; i++)
                        {
                            foreach (var pr in e.Props)
                            {
                                if (pr.CountType == null) { src.Read(pr.Type); continue; }
                                long c = (long)src.Read(pr.CountType);
                                bool isIdx = pr.Name == "vertex_indices" || pr.Name == "vertex_index";
                                poly.Clear();
                                for (long j = 0; j < c; j++) { double v = src.Read(pr.Type); if (isIdx) poly.Add((int)v); }
                                for (int j = 1; j + 1 < poly.Count; j++) { idx.Add(poly[0]); idx.Add(poly[j]); idx.Add(poly[j + 1]); }
                            }
                            job.Step("Reading PLY faces", i, n, 16384);
                        }
                    }
                    else
                    {
                        // skip unknown elements (edges, materials...)
                        for (long i = 0; i < e.Count; i++)
                            foreach (var pr in e.Props)
                            {
                                if (pr.CountType == null) src.Read(pr.Type);
                                else { long c = (long)src.Read(pr.CountType); for (long j = 0; j < c; j++) src.Read(pr.Type); }
                            }
                    }
                }
                if (pos == null) throw new FormatException("PLY has no vertex element");
                if (idx.Count == 0)
                    throw new Res3dException(Path.GetFileName(path) + " is a point cloud (no faces).",
                        "Point clouds are not supported in this version. Mesh the scan in the scanner software and export STL, OBJ or PLY with faces.");
                return Weld.Indexed(new TriMesh(pos, idx.ToArray(), col), job);
            }
        }

        private static string ReadHeaderLine(Stream f)
        {
            var sb = new StringBuilder();
            int b;
            while ((b = f.ReadByte()) >= 0)
            {
                if (b == '\n') return sb.ToString().TrimEnd('\r');
                sb.Append((char)b);
            }
            return sb.Length > 0 ? sb.ToString() : null;
        }

        private interface IPlySource { double Read(string type); }

        private sealed class PlyBinary : IPlySource
        {
            private readonly BinaryReader r;
            private readonly bool bigEndian;
            private readonly byte[] b8 = new byte[8];
            public PlyBinary(Stream s, bool bigEndian) { r = new BinaryReader(s); this.bigEndian = bigEndian; }

            private byte[] Bytes(int n)
            {
                if (r.Read(b8, 0, n) != n) throw new EndOfStreamException("PLY data ends early");
                if (bigEndian && BitConverter.IsLittleEndian) Array.Reverse(b8, 0, n);
                return b8;
            }

            public double Read(string type)
            {
                switch (type)
                {
                    case "char": case "int8": return (sbyte)Bytes(1)[0];
                    case "uchar": case "uint8": return Bytes(1)[0];
                    case "short": case "int16": return BitConverter.ToInt16(Bytes(2), 0);
                    case "ushort": case "uint16": return BitConverter.ToUInt16(Bytes(2), 0);
                    case "int": case "int32": return BitConverter.ToInt32(Bytes(4), 0);
                    case "uint": case "uint32": return BitConverter.ToUInt32(Bytes(4), 0);
                    case "float": case "float32": return BitConverter.ToSingle(Bytes(4), 0);
                    case "double": case "float64": return BitConverter.ToDouble(Bytes(8), 0);
                }
                throw new FormatException("unknown PLY type " + type);
            }
        }

        private sealed class PlyAscii : IPlySource
        {
            private readonly StreamReader r;
            private List<string> tokens = new List<string>();
            private int next;
            public PlyAscii(Stream s) { r = new StreamReader(s, Encoding.ASCII); }

            public double Read(string type)
            {
                while (next >= tokens.Count)
                {
                    var line = r.ReadLine();
                    if (line == null) throw new EndOfStreamException("PLY data ends early");
                    tokens = Split(line); next = 0;
                }
                return double.Parse(tokens[next++], NumberStyles.Float, CultureInfo.InvariantCulture);
            }
        }

        // ------------------------------------------------------------------ writers

        /// <summary>Binary STL (the display copy SolidWorks imports as surface mesh bodies).</summary>
        public static void WriteStlBinary(string path, TriMesh m, JobContext job)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            using (var f = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
            using (var w = new BinaryWriter(f))
            {
                var hdr = new byte[80];
                Encoding.ASCII.GetBytes("RE-Scan3D display mesh (temporary)").CopyTo(hdr, 0);
                w.Write(hdr);
                int n = m.TriangleCount;
                w.Write((uint)n);
                for (int t = 0; t < n; t++)
                {
                    var nrm = m.TriangleNormal(t);
                    w.Write((float)nrm.X); w.Write((float)nrm.Y); w.Write((float)nrm.Z);
                    for (int k = 0; k < 3; k++)
                    {
                        int v = 3 * m.Indices[3 * t + k];
                        w.Write(m.Positions[v]); w.Write(m.Positions[v + 1]); w.Write(m.Positions[v + 2]);
                    }
                    w.Write((ushort)0);
                    job.Step("Writing display mesh", t, n, 32768);
                }
            }
        }

        public static void WritePlyBinary(string path, TriMesh m)
        {
            using (var f = new FileStream(path, FileMode.Create))
            using (var w = new BinaryWriter(f))
            {
                var hdr = new StringBuilder();
                hdr.Append("ply\nformat binary_little_endian 1.0\n");
                hdr.AppendFormat("element vertex {0}\nproperty float x\nproperty float y\nproperty float z\n", m.VertexCount);
                if (m.Colors != null) hdr.Append("property uchar red\nproperty uchar green\nproperty uchar blue\n");
                hdr.AppendFormat("element face {0}\nproperty list uchar int vertex_indices\nend_header\n", m.TriangleCount);
                w.Write(Encoding.ASCII.GetBytes(hdr.ToString()));
                for (int i = 0; i < m.VertexCount; i++)
                {
                    w.Write(m.Positions[3 * i]); w.Write(m.Positions[3 * i + 1]); w.Write(m.Positions[3 * i + 2]);
                    if (m.Colors != null) { w.Write(m.Colors[3 * i]); w.Write(m.Colors[3 * i + 1]); w.Write(m.Colors[3 * i + 2]); }
                }
                for (int t = 0; t < m.TriangleCount; t++)
                {
                    w.Write((byte)3); w.Write(m.Indices[3 * t]); w.Write(m.Indices[3 * t + 1]); w.Write(m.Indices[3 * t + 2]);
                }
            }
        }

        public static void WriteObj(string path, TriMesh m)
        {
            using (var w = new StreamWriter(path, false, Encoding.ASCII))
            {
                var ci = CultureInfo.InvariantCulture;
                for (int i = 0; i < m.VertexCount; i++)
                    w.WriteLine(string.Format(ci, "v {0} {1} {2}", m.Positions[3 * i], m.Positions[3 * i + 1], m.Positions[3 * i + 2]));
                for (int t = 0; t < m.TriangleCount; t++)
                    w.WriteLine(string.Format(ci, "f {0} {1} {2}", m.Indices[3 * t] + 1, m.Indices[3 * t + 1] + 1, m.Indices[3 * t + 2] + 1));
            }
        }

        public static void WriteStlAscii(string path, TriMesh m)
        {
            using (var w = new StreamWriter(path, false, Encoding.ASCII))
            {
                var ci = CultureInfo.InvariantCulture;
                w.WriteLine("solid res3d");
                for (int t = 0; t < m.TriangleCount; t++)
                {
                    var n = m.TriangleNormal(t);
                    w.WriteLine(string.Format(ci, "facet normal {0} {1} {2}\nouter loop", n.X, n.Y, n.Z));
                    for (int k = 0; k < 3; k++)
                    {
                        int v = 3 * m.Indices[3 * t + k];
                        w.WriteLine(string.Format(ci, "vertex {0:R} {1:R} {2:R}", m.Positions[v], m.Positions[v + 1], m.Positions[v + 2]));
                    }
                    w.WriteLine("endloop\nendfacet");
                }
                w.WriteLine("endsolid res3d");
            }
        }

        // ------------------------------------------------------------------ helpers

        private static readonly char[] Ws = { ' ', '\t' };

        private static List<string> Split(string s)
        {
            return new List<string>(s.Split(Ws, StringSplitOptions.RemoveEmptyEntries));
        }

        private static float ParseF(string s) { return float.Parse(s, NumberStyles.Float, CultureInfo.InvariantCulture); }

        private static byte ToByte(double v01)
        {
            // OBJ/PLY float colours are 0..1, but some writers use 0..255 floats
            double v = v01 > 1.0 ? v01 : v01 * 255.0;
            return (byte)Math.Max(0, Math.Min(255, Math.Round(v)));
        }
    }
}
