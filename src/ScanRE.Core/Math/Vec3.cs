// Small double-precision linear algebra used throughout the core.
// Units convention (DECISIONS D3): the core works in MILLIMETRES; only the SolidWorks layer converts to metres.
using System;

namespace ScanRE.Core.Math3
{
    public struct Vec3
    {
        public double X, Y, Z;

        public Vec3(double x, double y, double z) { X = x; Y = y; Z = z; }

        public static readonly Vec3 Zero = new Vec3(0, 0, 0);
        public static readonly Vec3 UnitX = new Vec3(1, 0, 0);
        public static readonly Vec3 UnitY = new Vec3(0, 1, 0);
        public static readonly Vec3 UnitZ = new Vec3(0, 0, 1);

        public static Vec3 operator +(Vec3 a, Vec3 b) { return new Vec3(a.X + b.X, a.Y + b.Y, a.Z + b.Z); }
        public static Vec3 operator -(Vec3 a, Vec3 b) { return new Vec3(a.X - b.X, a.Y - b.Y, a.Z - b.Z); }
        public static Vec3 operator -(Vec3 a) { return new Vec3(-a.X, -a.Y, -a.Z); }
        public static Vec3 operator *(Vec3 a, double s) { return new Vec3(a.X * s, a.Y * s, a.Z * s); }
        public static Vec3 operator *(double s, Vec3 a) { return new Vec3(a.X * s, a.Y * s, a.Z * s); }
        public static Vec3 operator /(Vec3 a, double s) { return new Vec3(a.X / s, a.Y / s, a.Z / s); }

        public static double Dot(Vec3 a, Vec3 b) { return a.X * b.X + a.Y * b.Y + a.Z * b.Z; }
        public static Vec3 Cross(Vec3 a, Vec3 b)
        {
            return new Vec3(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);
        }

        public double Length { get { return Math.Sqrt(X * X + Y * Y + Z * Z); } }
        public double LengthSq { get { return X * X + Y * Y + Z * Z; } }

        public Vec3 Normalized()
        {
            double l = Length;
            return l > 1e-300 ? this / l : UnitZ;
        }

        public double this[int i]
        {
            get { return i == 0 ? X : i == 1 ? Y : Z; }
            set { if (i == 0) X = value; else if (i == 1) Y = value; else Z = value; }
        }

        /// <summary>Angle between two directions, radians, 0..pi.</summary>
        public static double Angle(Vec3 a, Vec3 b)
        {
            // atan2 form is accurate for both tiny and near-pi angles (acos is not)
            return Math.Atan2(Cross(a, b).Length, Dot(a, b));
        }

        /// <summary>Any unit vector perpendicular to this one.</summary>
        public Vec3 AnyPerpendicular()
        {
            var a = Math.Abs(X) < 0.9 ? UnitX : UnitY;
            return Cross(this, a).Normalized();
        }

        public static Vec3 Min(Vec3 a, Vec3 b) { return new Vec3(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Min(a.Z, b.Z)); }
        public static Vec3 Max(Vec3 a, Vec3 b) { return new Vec3(Math.Max(a.X, b.X), Math.Max(a.Y, b.Y), Math.Max(a.Z, b.Z)); }

        public override string ToString() { return string.Format("({0:G6}, {1:G6}, {2:G6})", X, Y, Z); }
    }

    /// <summary>Row-major 3x3 matrix.</summary>
    public struct Mat3
    {
        public double M00, M01, M02, M10, M11, M12, M20, M21, M22;

        public static Mat3 Identity
        {
            get { return new Mat3 { M00 = 1, M11 = 1, M22 = 1 }; }
        }

        public static Mat3 FromRows(Vec3 r0, Vec3 r1, Vec3 r2)
        {
            return new Mat3 { M00 = r0.X, M01 = r0.Y, M02 = r0.Z, M10 = r1.X, M11 = r1.Y, M12 = r1.Z, M20 = r2.X, M21 = r2.Y, M22 = r2.Z };
        }

        public static Mat3 FromColumns(Vec3 c0, Vec3 c1, Vec3 c2)
        {
            return new Mat3 { M00 = c0.X, M01 = c1.X, M02 = c2.X, M10 = c0.Y, M11 = c1.Y, M12 = c2.Y, M20 = c0.Z, M21 = c1.Z, M22 = c2.Z };
        }

        public Vec3 Col(int j) { return j == 0 ? new Vec3(M00, M10, M20) : j == 1 ? new Vec3(M01, M11, M21) : new Vec3(M02, M12, M22); }

        public static Vec3 operator *(Mat3 m, Vec3 v)
        {
            return new Vec3(m.M00 * v.X + m.M01 * v.Y + m.M02 * v.Z,
                            m.M10 * v.X + m.M11 * v.Y + m.M12 * v.Z,
                            m.M20 * v.X + m.M21 * v.Y + m.M22 * v.Z);
        }

        public static Mat3 operator *(Mat3 a, Mat3 b)
        {
            return FromColumns(a * b.Col(0), a * b.Col(1), a * b.Col(2));
        }

        public Mat3 Transposed()
        {
            return new Mat3 { M00 = M00, M01 = M10, M02 = M20, M10 = M01, M11 = M11, M12 = M21, M20 = M02, M21 = M12, M22 = M22 };
        }

        /// <summary>Rotation of `angle` radians about unit `axis` (Rodrigues).</summary>
        public static Mat3 Rotation(Vec3 axis, double angle)
        {
            var k = axis.Normalized();
            double c = Math.Cos(angle), s = Math.Sin(angle), t = 1 - c;
            return new Mat3
            {
                M00 = c + k.X * k.X * t, M01 = k.X * k.Y * t - k.Z * s, M02 = k.X * k.Z * t + k.Y * s,
                M10 = k.Y * k.X * t + k.Z * s, M11 = c + k.Y * k.Y * t, M12 = k.Y * k.Z * t - k.X * s,
                M20 = k.Z * k.X * t - k.Y * s, M21 = k.Z * k.Y * t + k.X * s, M22 = c + k.Z * k.Z * t
            };
        }

        /// <summary>Rotation vector (axis * angle) to matrix.</summary>
        public static Mat3 FromRotationVector(Vec3 r)
        {
            double a = r.Length;
            return a < 1e-15 ? Identity : Rotation(r / a, a);
        }

        /// <summary>The minimal rotation taking unit vector a onto unit vector b.</summary>
        public static Mat3 RotationBetween(Vec3 a, Vec3 b)
        {
            a = a.Normalized(); b = b.Normalized();
            var axis = Vec3.Cross(a, b);
            double ang = Vec3.Angle(a, b);
            if (axis.Length < 1e-12)
                return ang < 1 ? Identity : Rotation(a.AnyPerpendicular(), Math.PI);
            return Rotation(axis, ang);
        }

        /// <summary>Re-orthonormalise (Gram-Schmidt on columns) after accumulating rotations.</summary>
        public Mat3 Orthonormalized()
        {
            var c0 = Col(0).Normalized();
            var c1 = (Col(1) - c0 * Vec3.Dot(c0, Col(1))).Normalized();
            var c2 = Vec3.Cross(c0, c1);
            return FromColumns(c0, c1, c2);
        }
    }

    /// <summary>Symmetric 3x3 eigen-decomposition (cyclic Jacobi). Eigenvalues ascending.</summary>
    public static class SymEigen
    {
        public static void Solve(double[,] a, out double[] values, out Vec3[] vectors)
        {
            var m = (double[,])a.Clone();
            var v = new double[3, 3] { { 1, 0, 0 }, { 0, 1, 0 }, { 0, 0, 1 } };
            for (int sweep = 0; sweep < 50; sweep++)
            {
                double off = m[0, 1] * m[0, 1] + m[0, 2] * m[0, 2] + m[1, 2] * m[1, 2];
                if (off < 1e-30) break;
                for (int p = 0; p < 2; p++)
                    for (int q = p + 1; q < 3; q++)
                    {
                        if (Math.Abs(m[p, q]) < 1e-300) continue;
                        double theta = (m[q, q] - m[p, p]) / (2 * m[p, q]);
                        double t = Math.Sign(theta) / (Math.Abs(theta) + Math.Sqrt(theta * theta + 1));
                        if (theta == 0) t = 1;
                        double c = 1 / Math.Sqrt(t * t + 1), s = t * c;
                        for (int k = 0; k < 3; k++)
                        {
                            double mkp = m[k, p], mkq = m[k, q];
                            m[k, p] = c * mkp - s * mkq; m[k, q] = s * mkp + c * mkq;
                        }
                        for (int k = 0; k < 3; k++)
                        {
                            double mpk = m[p, k], mqk = m[q, k];
                            m[p, k] = c * mpk - s * mqk; m[q, k] = s * mpk + c * mqk;
                        }
                        for (int k = 0; k < 3; k++)
                        {
                            double vkp = v[k, p], vkq = v[k, q];
                            v[k, p] = c * vkp - s * vkq; v[k, q] = s * vkp + c * vkq;
                        }
                    }
            }
            var idx = new[] { 0, 1, 2 };
            Array.Sort(idx, (i, j) => m[i, i].CompareTo(m[j, j]));
            values = new double[3];
            vectors = new Vec3[3];
            for (int k = 0; k < 3; k++)
            {
                int i = idx[k];
                values[k] = m[i, i];
                vectors[k] = new Vec3(v[0, i], v[1, i], v[2, i]).Normalized();
            }
        }
    }

    /// <summary>Rigid transform p' = R p + T.</summary>
    public struct Rigid
    {
        public Mat3 R;
        public Vec3 T;

        public static Rigid Identity { get { return new Rigid { R = Mat3.Identity, T = Vec3.Zero }; } }

        public Rigid(Mat3 r, Vec3 t) { R = r; T = t; }

        public Vec3 Apply(Vec3 p) { return R * p + T; }
        public Vec3 ApplyDir(Vec3 d) { return R * d; }

        public Rigid Inverse()
        {
            var rt = R.Transposed();
            return new Rigid(rt, -(rt * T));
        }

        /// <summary>a then b: (b ∘ a)(p) = b(a(p)).</summary>
        public static Rigid Then(Rigid a, Rigid b)
        {
            return new Rigid(b.R * a.R, b.R * a.T + b.T);
        }

        public double[] ToArray()
        {
            return new[] { R.M00, R.M01, R.M02, R.M10, R.M11, R.M12, R.M20, R.M21, R.M22, T.X, T.Y, T.Z };
        }

        public static Rigid FromArray(double[] a)
        {
            if (a == null || a.Length != 12) return Identity;
            return new Rigid(new Mat3 { M00 = a[0], M01 = a[1], M02 = a[2], M10 = a[3], M11 = a[4], M12 = a[5], M20 = a[6], M21 = a[7], M22 = a[8] },
                             new Vec3(a[9], a[10], a[11]));
        }
    }

    /// <summary>Infinite plane through Origin with unit Normal.</summary>
    public struct Plane3
    {
        public Vec3 Origin, Normal;
        public Plane3(Vec3 origin, Vec3 normal) { Origin = origin; Normal = normal.Normalized(); }
        public double SignedDistance(Vec3 p) { return Vec3.Dot(p - Origin, Normal); }
        public Vec3 Project(Vec3 p) { return p - Normal * SignedDistance(p); }
        /// <summary>Offset of the plane from the world origin along its normal.</summary>
        public double D { get { return Vec3.Dot(Origin, Normal); } }

        public Plane3 Transformed(Rigid t) { return new Plane3(t.Apply(Origin), t.ApplyDir(Normal)); }

        /// <summary>An orthonormal in-plane basis (U, V) with U x V = Normal.</summary>
        public void Basis(out Vec3 u, out Vec3 v)
        {
            u = Normal.AnyPerpendicular();
            v = Vec3.Cross(Normal, u);
        }
    }

    /// <summary>Ray Origin + t*Dir, Dir unit.</summary>
    public struct Ray3
    {
        public Vec3 Origin, Dir;
        public Ray3(Vec3 o, Vec3 d) { Origin = o; Dir = d.Normalized(); }
        public Vec3 At(double t) { return Origin + Dir * t; }

        public bool Intersect(Plane3 p, out double t)
        {
            double den = Vec3.Dot(Dir, p.Normal);
            t = 0;
            if (Math.Abs(den) < 1e-12) return false;
            t = Vec3.Dot(p.Origin - Origin, p.Normal) / den;
            return true;
        }

        public Ray3 Transformed(Rigid t) { return new Ray3(t.Apply(Origin), t.ApplyDir(Dir)); }
    }
}
