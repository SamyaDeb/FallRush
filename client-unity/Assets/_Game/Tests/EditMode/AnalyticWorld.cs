using System;
using System.Collections.Generic;
using FallRush.Shared;

namespace FallRush.Tests
{
    /// <summary>
    /// Test-only <see cref="IMotorWorld"/>: half-spaces and axis-aligned boxes with exact signed distances to the
    /// motor's upright capsule (a vertical segment + radius). Sweeps use conservative advancement on that distance,
    /// so the results do not depend on PhysX. Normals are impact normals (edge contacts give diagonal normals, like PhysX sweeps).
    /// </summary>
    public sealed class AnalyticWorld : IMotorWorld
    {
        private const float HitEps = 1e-4f;
        private const int MaxIterations = 512;

        private struct HalfSpace
        {
            public Vec3 N;   // outward normal (unit)
            public float D;  // solid where Dot(N, p) <= D
        }

        private struct Box
        {
            public Vec3 Min, Max;
        }

        private readonly List<HalfSpace> _planes = new List<HalfSpace>();
        private readonly List<Box> _boxes = new List<Box>();

        /// <summary>Solid ground below y = <paramref name="y"/>.</summary>
        public AnalyticWorld Ground(float y = 0f) => Plane(Vec3.Up, y);

        /// <summary>Solid where Dot(n, p) ≤ d.</summary>
        public AnalyticWorld Plane(Vec3 n, float d)
        {
            _planes.Add(new HalfSpace { N = n.Normalized, D = d });
            return this;
        }

        /// <summary>A ramp rising toward +Z from z = <paramref name="z0"/> (y = 0) at <paramref name="degrees"/>.</summary>
        public AnalyticWorld RampZ(float z0, float degrees)
        {
            var a = degrees * Math.PI / 180.0;
            // Solid where y <= (z - z0) * tan(a)  ⇔  Dot((0, cos a, −sin a), p) <= −z0·sin a
            var n = new Vec3(0f, (float)Math.Cos(a), (float)-Math.Sin(a));
            return Plane(n, (float)(-z0 * Math.Sin(a)));
        }

        public AnalyticWorld AddBox(Vec3 min, Vec3 max)
        {
            _boxes.Add(new Box { Min = min, Max = max });
            return this;
        }

        // ------------------------------------------------------------------ distance

        /// <summary>Signed distance from the capsule (feet, r, h) to the world; closest point on the surface and its normal.</summary>
        private float CapsuleDistance(Vec3 feet, float r, float h, out Vec3 normal, out Vec3 point)
        {
            var y0 = feet.Y + r;
            var y1 = feet.Y + h - r;
            var best = float.MaxValue;
            normal = Vec3.Up;
            point = feet;

            foreach (var p in _planes)
            {
                // Linear along the segment → min at an endpoint.
                var a = new Vec3(feet.X, y0, feet.Z);
                var b = new Vec3(feet.X, y1, feet.Z);
                var da = Vec3.Dot(p.N, a) - p.D;
                var db = Vec3.Dot(p.N, b) - p.D;
                var c = da <= db ? a : b;
                var d = Math.Min(da, db) - r;
                if (d < best)
                {
                    best = d;
                    normal = p.N;
                    point = c - p.N * (d + r);
                }
            }

            foreach (var bx in _boxes)
            {
                // Closest point on the vertical segment to the box: pick y in the overlap, else the nearer end.
                float sy;
                if (y1 < bx.Min.Y)
                    sy = y1;
                else if (y0 > bx.Max.Y)
                    sy = y0;
                else
                    sy = Math.Max(y0, Math.Min(y1, (Math.Max(y0, bx.Min.Y) + Math.Min(y1, bx.Max.Y)) * 0.5f));
                var s = new Vec3(feet.X, sy, feet.Z);
                var d = BoxDistance(bx, s, out var n, out var cp) - r;
                if (d < best)
                {
                    best = d;
                    normal = n;
                    point = cp;
                }
            }

            return best;
        }

        private static float BoxDistance(Box b, Vec3 p, out Vec3 normal, out Vec3 closest)
        {
            var cx = Math.Max(b.Min.X, Math.Min(p.X, b.Max.X));
            var cy = Math.Max(b.Min.Y, Math.Min(p.Y, b.Max.Y));
            var cz = Math.Max(b.Min.Z, Math.Min(p.Z, b.Max.Z));
            closest = new Vec3(cx, cy, cz);
            var diff = p - closest;
            var d = diff.Magnitude;
            if (d > 1e-7f)
            {
                normal = diff / d;
                return d;
            }

            // Inside: push out through the nearest face.
            var dxMin = p.X - b.Min.X;
            var dxMax = b.Max.X - p.X;
            var dyMin = p.Y - b.Min.Y;
            var dyMax = b.Max.Y - p.Y;
            var dzMin = p.Z - b.Min.Z;
            var dzMax = b.Max.Z - p.Z;
            var m = dyMax;
            normal = Vec3.Up;
            closest = new Vec3(p.X, b.Max.Y, p.Z);
            if (dxMin < m) { m = dxMin; normal = new Vec3(-1, 0, 0); closest = new Vec3(b.Min.X, p.Y, p.Z); }
            if (dxMax < m) { m = dxMax; normal = new Vec3(1, 0, 0); closest = new Vec3(b.Max.X, p.Y, p.Z); }
            if (dyMin < m) { m = dyMin; normal = new Vec3(0, -1, 0); closest = new Vec3(p.X, b.Min.Y, p.Z); }
            if (dzMin < m) { m = dzMin; normal = new Vec3(0, 0, -1); closest = new Vec3(p.X, p.Y, b.Min.Z); }
            if (dzMax < m) { m = dzMax; normal = new Vec3(0, 0, 1); closest = new Vec3(p.X, p.Y, b.Max.Z); }
            return -m;
        }

        private float PointDistance(Vec3 p, out Vec3 normal, out Vec3 point)
        {
            var best = float.MaxValue;
            normal = Vec3.Up;
            point = p;
            foreach (var pl in _planes)
            {
                var d = Vec3.Dot(pl.N, p) - pl.D;
                if (d < best)
                {
                    best = d;
                    normal = pl.N;
                    point = p - pl.N * d;
                }
            }

            foreach (var b in _boxes)
            {
                var d = BoxDistance(b, p, out var n, out var cp);
                if (d < best)
                {
                    best = d;
                    normal = n;
                    point = cp;
                }
            }

            return best;
        }

        // ------------------------------------------------------------------ IMotorWorld

        public bool CapsuleSweep(Vec3 feet, float radius, float height, Vec3 dir, float maxDistance, out MotorHit hit)
        {
            hit = default;
            var t = 0f;
            for (var i = 0; i < MaxIterations; i++)
            {
                var d = CapsuleDistance(feet + dir * t, radius, height, out var n, out var pt);
                if (d < HitEps)
                {
                    // Only a hit if we are moving into the surface (sliding along it is not).
                    if (Vec3.Dot(n, dir) < -1e-5f || d < -HitEps)
                    {
                        hit = new MotorHit { Distance = Math.Max(0f, t), Normal = n, Point = pt, SurfaceId = -1 };
                        return true;
                    }

                    t += Math.Max(HitEps * 4f, 1e-3f);
                }
                else
                {
                    t += d;
                }

                if (t > maxDistance)
                    return false;
            }

            return false;
        }

        public bool Raycast(Vec3 origin, Vec3 dir, float maxDistance, out MotorHit hit)
        {
            hit = default;
            var t = 0f;
            if (PointDistance(origin, out _, out _) < 0f)
                return false; // starts inside: like PhysX/Unity, no hit
            for (var i = 0; i < MaxIterations; i++)
            {
                var d = PointDistance(origin + dir * t, out var n, out var pt);
                if (d < HitEps)
                {
                    hit = new MotorHit { Distance = t, Normal = n, Point = pt, SurfaceId = -1 };
                    return true;
                }

                t += d;
                if (t > maxDistance)
                    return false;
            }

            return false;
        }

        public bool ComputePenetration(Vec3 feet, float radius, float height, out Vec3 correction)
        {
            var d = CapsuleDistance(feet, radius, height, out var n, out _);
            if (d >= 0f)
            {
                correction = Vec3.Zero;
                return false;
            }

            correction = n * (-d + 1e-4f);
            return true;
        }
    }
}
