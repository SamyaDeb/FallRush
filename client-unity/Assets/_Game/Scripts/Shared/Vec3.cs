using System;

namespace FallRush.Shared
{
    /// <summary>
    /// Minimal float vector for the engine-free motor (FallRush.Shared has no UnityEngine reference).
    /// Only +, −, ×, ÷ and sqrt are used, which IEEE 754 rounds identically on every platform.
    /// </summary>
    public struct Vec3 : IEquatable<Vec3>
    {
        public float X, Y, Z;

        public Vec3(float x, float y, float z)
        {
            X = x;
            Y = y;
            Z = z;
        }

        public static readonly Vec3 Zero = new Vec3(0f, 0f, 0f);
        public static readonly Vec3 Up = new Vec3(0f, 1f, 0f);
        public static readonly Vec3 Down = new Vec3(0f, -1f, 0f);

        public static Vec3 operator +(Vec3 a, Vec3 b) => new Vec3(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
        public static Vec3 operator -(Vec3 a, Vec3 b) => new Vec3(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
        public static Vec3 operator -(Vec3 a) => new Vec3(-a.X, -a.Y, -a.Z);
        public static Vec3 operator *(Vec3 a, float s) => new Vec3(a.X * s, a.Y * s, a.Z * s);
        public static Vec3 operator *(float s, Vec3 a) => new Vec3(a.X * s, a.Y * s, a.Z * s);
        public static Vec3 operator /(Vec3 a, float s) => new Vec3(a.X / s, a.Y / s, a.Z / s);

        public static float Dot(Vec3 a, Vec3 b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;

        public float SqrMagnitude => X * X + Y * Y + Z * Z;
        public float Magnitude => (float)Math.Sqrt(SqrMagnitude);
        public Vec3 Horizontal => new Vec3(X, 0f, Z);

        public Vec3 Normalized
        {
            get
            {
                var m = Magnitude;
                return m > 1e-6f ? this / m : Zero;
            }
        }

        public bool Equals(Vec3 o) => X == o.X && Y == o.Y && Z == o.Z;
        public override bool Equals(object obj) => obj is Vec3 v && Equals(v);
        public override int GetHashCode() => X.GetHashCode() ^ (Y.GetHashCode() << 2) ^ (Z.GetHashCode() >> 2);
        public override string ToString() => $"({X:F4}, {Y:F4}, {Z:F4})";
    }
}
