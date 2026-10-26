// The sidecar cache folder next to the part ("<Part>.res3d\"): processed full-res mesh, spatial index,
// decimated display levels and state.json. Never stored inside the part (spec architecture).
using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using RES3D.Core.Jobs;
using RES3D.Core.Mesh;
using RES3D.Core.Spatial;

namespace RES3D.Core.Cache
{
    public sealed class Sidecar
    {
        private const uint MeshMagic = 0x31455253;   // "SRE1"
        private const int MeshFormat = 1;

        public string Folder { get; private set; }

        public Sidecar(string folder) { Folder = folder; }

        /// <summary>"C:\Parts\Bracket.SLDPRT" -> "C:\Parts\Bracket.res3d".</summary>
        public static Sidecar ForPart(string partPath)
        {
            if (string.IsNullOrEmpty(partPath))
                throw new Res3dException("The part has not been saved yet.",
                    "Save the part first: the scan cache is kept in a folder next to the part file.");
            var dir = Path.GetDirectoryName(partPath);
            string folder = Path.Combine(dir, Path.GetFileNameWithoutExtension(partPath) + ".res3d");
            // caches made before the rename to RE-Scan3D were "<part>.scanre": take them over
            string legacy = Path.Combine(dir, Path.GetFileNameWithoutExtension(partPath) + ".scanre");
            if (!Directory.Exists(folder) && Directory.Exists(legacy))
                try { Directory.Move(legacy, folder); } catch (IOException) { return new Sidecar(legacy); }
            return new Sidecar(folder);
        }

        public string StatePath { get { return Path.Combine(Folder, "state.json"); } }
        public string MeshPath { get { return Path.Combine(Folder, "mesh.bin"); } }
        public string BvhPath { get { return Path.Combine(Folder, "bvh.bin"); } }
        public string DisplayLevelPath(int tris) { return Path.Combine(Folder, "display_" + tris + ".bin"); }
        /// <summary>The transformed display STL handed to SolidWorks (unique name per write: SolidWorks may cache by path).</summary>
        public string DisplayStlPath(string tag) { return Path.Combine(Folder, "display_" + tag + ".stl"); }

        public bool Exists { get { return File.Exists(StatePath) && File.Exists(MeshPath); } }

        public void EnsureFolder()
        {
            Directory.CreateDirectory(Folder);
        }

        // ------------------------------------------------------------------ state

        public ScanState LoadState()
        {
            return File.Exists(StatePath) ? ScanState.FromJson(File.ReadAllText(StatePath, Encoding.UTF8)) : null;
        }

        public void SaveState(ScanState s)
        {
            EnsureFolder();
            var tmp = StatePath + ".tmp";
            File.WriteAllText(tmp, s.ToJson(), Encoding.UTF8);
            if (File.Exists(StatePath)) File.Replace(tmp, StatePath, null);
            else File.Move(tmp, StatePath);
        }

        // ------------------------------------------------------------------ mesh + index

        public static void WriteMesh(string path, TriMesh m)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            using (var f = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
            using (var w = new BinaryWriter(f))
            {
                w.Write(MeshMagic); w.Write(MeshFormat);
                w.Write(m.VertexCount); w.Write(m.TriangleCount); w.Write(m.Colors != null);
                var buf = new byte[m.Positions.Length * 4];
                Buffer.BlockCopy(m.Positions, 0, buf, 0, buf.Length);
                w.Write(buf);
                buf = new byte[m.Indices.Length * 4];
                Buffer.BlockCopy(m.Indices, 0, buf, 0, buf.Length);
                w.Write(buf);
                if (m.Colors != null) w.Write(m.Colors);
            }
        }

        public static TriMesh ReadMesh(string path)
        {
            using (var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20))
            using (var r = new BinaryReader(f))
            {
                if (r.ReadUInt32() != MeshMagic || r.ReadInt32() != MeshFormat) throw new InvalidDataException("not an RE-Scan3D mesh cache");
                int nv = r.ReadInt32(), nt = r.ReadInt32();
                bool col = r.ReadBoolean();
                var pos = new float[3 * nv];
                var buf = r.ReadBytes(pos.Length * 4);
                if (buf.Length != pos.Length * 4) throw new EndOfStreamException("mesh cache truncated");
                Buffer.BlockCopy(buf, 0, pos, 0, buf.Length);
                var idx = new int[3 * nt];
                buf = r.ReadBytes(idx.Length * 4);
                if (buf.Length != idx.Length * 4) throw new EndOfStreamException("mesh cache truncated");
                Buffer.BlockCopy(buf, 0, idx, 0, buf.Length);
                byte[] colors = col ? r.ReadBytes(3 * nv) : null;
                return new TriMesh(pos, idx, colors);
            }
        }

        public void WriteBvh(Bvh b)
        {
            using (var f = new FileStream(BvhPath, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
            using (var w = new BinaryWriter(f)) b.Write(w);
        }

        public Bvh ReadBvh(TriMesh m)
        {
            using (var f = new FileStream(BvhPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20))
            using (var r = new BinaryReader(f)) return Bvh.Read(r, m);
        }

        /// <summary>Remove the cached display STLs except `keep` (they accumulate as the alignment changes).</summary>
        public void PruneDisplayStls(string keep)
        {
            if (!Directory.Exists(Folder)) return;
            // display_<n>.stl and the display_<n>.SLDPRT SolidWorks made from it
            string keepStem = keep == null ? null : Path.ChangeExtension(Path.GetFullPath(keep), null);
            foreach (var f in Directory.GetFiles(Folder, "display_*.*"))
            {
                string ext = Path.GetExtension(f).ToLowerInvariant();
                if (ext != ".stl" && ext != ".sldprt") continue;
                if (string.Equals(Path.ChangeExtension(Path.GetFullPath(f), null), keepStem, StringComparison.OrdinalIgnoreCase)) continue;
                try { File.Delete(f); }
                catch (IOException) { /* still held by SolidWorks: next time */ }
                catch (UnauthorizedAccessException) { }
            }
        }

        public void Delete()
        {
            if (Directory.Exists(Folder)) Directory.Delete(Folder, true);
        }

        // ------------------------------------------------------------------ source fingerprint

        /// <summary>Size + SHA-1 of the first and last MiB: detects a changed source in milliseconds, even for GB files.</summary>
        public static string Fingerprint(string path)
        {
            var fi = new FileInfo(path);
            using (var f = File.OpenRead(path))
            using (var sha = SHA1.Create())
            {
                const int chunk = 1 << 20;
                var buf = new byte[chunk];
                int n = f.Read(buf, 0, chunk);
                sha.TransformBlock(buf, 0, n, null, 0);
                if (fi.Length > 2 * chunk)
                {
                    f.Seek(-chunk, SeekOrigin.End);
                    n = f.Read(buf, 0, chunk);
                    sha.TransformBlock(buf, 0, n, null, 0);
                }
                var len = BitConverter.GetBytes(fi.Length);
                sha.TransformFinalBlock(len, 0, len.Length);
                return BitConverter.ToString(sha.Hash).Replace("-", "");
            }
        }
    }
}
