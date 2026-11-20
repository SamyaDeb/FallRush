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
            public int Surface; // −1 static, else HazardSet surface id
            public int Platform; // ≥ 0: posed from HazardSet.Platforms[Platform]
            public int Door;     // ≥ 0: posed from HazardSet.Doors[Door]
        }

        private struct Cyl
        {
            public float X, Z, R, Y0, Y1;
            public int Surface;
        }

        private readonly List<HalfSpace> _planes = new List<HalfSpace>();
        private readonly List<Box> _boxes = new List<Box>();
        private readonly List<Cyl> _cyls = new List<Cyl>();
        private HazardSet _hazards;
        private uint _tick;

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
            _boxes.Add(new Box { Min = min, Max = max, Surface = -1, Platform = -1, Door = -1 });
            return this;
        }

        /// <summary>M2 form: the platforms of <paramref name="hazards"/> as moving boxes (see <see cref="AddHazards"/>).</summary>
        public AnalyticWorld AddPlatforms(HazardSet hazards) => AddHazards(hazards);

        /// <summary>
        /// Every collider of <paramref name="hazards"/>, like the scene builder makes them: platforms and doors as boxes posed
        /// per tick, conveyors (flat only) and spring pads as static surface boxes, turn tables (surfaces) and bumpers as
        /// vertical cylinders. Spinner bars and mallets have no collider (analytic contact).
        /// </summary>
        public AnalyticWorld AddHazards(HazardSet hazards)
        {
            _hazards = hazards;
            for (var i = 0; i < hazards.Platforms.Length; i++)
                _boxes.Add(new Box { Surface = hazards.PlatformSurface(i), Platform = i, Door = -1 });
            for (var i = 0; i < hazards.Doors.Length; i++)
                _boxes.Add(new Box { Surface = -1, Platform = -1, Door = i });
            for (var i = 0; i < hazards.Conveyors.Length; i++)
            {
                ref readonly var d = ref hazards.Conveyors[i];
                if (d.PitchCdeg != 0)
                    throw new NotSupportedException("analytic world: flat conveyors only");
                var c = HazardMath.MmToVec(d.CenterXMm, d.CenterYMm, d.CenterZMm);
                var h = HazardMath.MmToVec(d.HalfXMm, d.HalfYMm, d.HalfZMm);
                _boxes.Add(new Box { Min = c - h, Max = c + h, Surface = hazards.ConveyorSurface(i), Platform = -1, Door = -1 });
            }

            for (var i = 0; i < hazards.Pads.Length; i++)
            {
                ref readonly var d = ref hazards.Pads[i];
                var top = d.CenterYMm * 0.001f;
                _boxes.Add(new Box
                {
                    Min = new Vec3((d.CenterXMm - d.HalfXMm) * 0.001f, top - d.ThicknessMm * 0.001f, (d.CenterZMm - d.HalfZMm) * 0.001f),
                    Max = new Vec3((d.CenterXMm + d.HalfXMm) * 0.001f, top, (d.CenterZMm + d.HalfZMm) * 0.001f),
                    Surface = hazards.PadSurface(i), Platform = -1, Door = -1,
                });
            }

            for (var i = 0; i < hazards.TurnTables.Length; i++)
            {
                ref readonly var d = ref hazards.TurnTables[i];
                _cyls.Add(new Cyl { X = d.CenterXMm * 0.001f, Z = d.CenterZMm * 0.001f, R = d.RadiusMm * 0.001f, Y0 = (d.CenterYMm - d.ThicknessMm) * 0.001f, Y1 = d.CenterYMm * 0.001f, Surface = hazards.TurnTableSurface(i) });
            }

            for (var i = 0; i < hazards.Bumpers.Length; i++)
            {
                ref readonly var d = ref hazards.Bumpers[i];
                _cyls.Add(new Cyl { X = d.CenterXMm * 0.001f, Z = d.CenterZMm * 0.001f, R = d.RadiusMm * 0.001f, Y0 = d.CenterYMm * 0.001f, Y1 = (d.CenterYMm + d.HeightMm) * 0.001f, Surface = -1 });
            }

            BeginTick(0);
            return this;
        }

        public void BeginTick(uint tick)
        {
            _tick = tick;
            if (_hazards == null)
                return;
            var t = _hazards.Time(tick);
            for (var i = 0; i < _boxes.Count; i++)
            {
                var b = _boxes[i];
                if (b.Platform >= 0)
                {
                    ref readonly var d = ref _hazards.Platforms[b.Platform];
                    var c = HazardMath.PlatformCenter(in d, t);
                    var half = new Vec3(d.HalfXMm * 0.001f, d.HalfYMm * 0.001f, d.HalfZMm * 0.001f);
                    b.Min = c - half;
                    b.Max = c + half;
                }
                else if (b.Door >= 0)
                {
                    ref readonly var d = ref _hazards.Doors[b.Door];
                    _hazards.DoorCenterMm(b.Door, tick, out var x, out var y, out var z);
                    var c = HazardMath.MmToVec(x, y, z);
                    var half = new Vec3(d.HalfXMm * 0.001f, d.HalfYMm * 0.001f, d.HalfZMm * 0.001f);
                    b.Min = c - half;
                    b.Max = c + half;
                }
                else
                {
                    continue;
                }

                _boxes[i] = b;
            }
        }

        public bool SurfaceDelta(int surfaceId, Vec3 feet, out Vec3 delta)
        {
            if (_hazards == null)
            {
                delta = Vec3.Zero;
                return false;
            }

            return _hazards.SurfaceDelta(surfaceId, _tick, feet, out delta);
        }

        // ------------------------------------------------------------------ distance

        /// <summary>Signed distance from the capsule (feet, r, h) to the world; closest point on the surface and its normal.</summary>
        private float CapsuleDistance(Vec3 feet, float r, float h, out Vec3 normal, out Vec3 point) =>
            CapsuleDistance(feet, r, h, out normal, out point, out _);

        private float CapsuleDistance(Vec3 feet, float r, float h, out Vec3 normal, out Vec3 point, out int surface)
        {
            surface = -1;
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
                    surface = bx.Surface;
                }
            }

            foreach (var cy in _cyls)
            {
                var d = CylDistance(cy, feet.X, feet.Z, y0, y1, out var n, out var cp) - r;
                if (d < best)
                {
                    best = d;
                    normal = n;
                    point = cp;
                    surface = cy.Surface;
                }
            }

            return best;
        }

        /// <summary>Distance from the vertical segment (x, z, [y0, y1]) to a solid vertical cylinder; negative inside.</summary>
        private static float CylDistance(Cyl c, float x, float z, float y0, float y1, out Vec3 normal, out Vec3 closest)
        {
            var dx = x - c.X;
            var dz = z - c.Z;
            var rr = (float)Math.Sqrt(dx * dx + dz * dz);
            float ux = 1f, uz = 0f;
            if (rr > 1e-6f)
            {
                ux = dx / rr;
                uz = dz / rr;
            }

            var dh = rr - c.R;
            float dv;
            float sy;
            var vertSign = 0;
            if (y0 > c.Y1)
            {
                dv = y0 - c.Y1;
                sy = c.Y1;
                vertSign = 1;
            }
            else if (y1 < c.Y0)
            {
                dv = c.Y0 - y1;
                sy = c.Y0;
                vertSign = -1;
            }
            else
            {
                dv = 0f;
                sy = Math.Max(y0, Math.Min(y1, (Math.Max(y0, c.Y0) + Math.Min(y1, c.Y1)) * 0.5f));
            }

            if (vertSign == 0)
            {
                if (dh >= 0f)
                {
                    normal = new Vec3(ux, 0f, uz);
                    closest = new Vec3(c.X + ux * c.R, sy, c.Z + uz * c.R);
                    return dh;
                }

                // Inside: the cheapest way out (radial, up or down).
                var up = c.Y1 - y0;
                var down = y1 - c.Y0;
                var radial = -dh;
                if (radial <= up && radial <= down)
                {
                    normal = new Vec3(ux, 0f, uz);
                    closest = new Vec3(c.X + ux * c.R, sy, c.Z + uz * c.R);
                    return -radial;
                }

                if (up <= down)
                {
                    normal = Vec3.Up;
                    closest = new Vec3(x, c.Y1, z);
                    return -up;
                }

                normal = Vec3.Down;
                closest = new Vec3(x, c.Y0, z);
                return -down;
            }

            if (dh <= 0f)
            {
                normal = vertSign > 0 ? Vec3.Up : Vec3.Down;
                closest = new Vec3(x, sy, z);
                return dv;
            }

            var len = (float)Math.Sqrt(dh * dh + dv * dv);
            normal = new Vec3(ux * dh / len, vertSign * dv / len, uz * dh / len);
            closest = new Vec3(c.X + ux * c.R, sy, c.Z + uz * c.R);
            return len;
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

            foreach (var cy in _cyls)
            {
                var d = CylDistance(cy, p.X, p.Z, p.Y, p.Y, out var n, out var cp);
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
                var d = CapsuleDistance(feet + dir * t, radius, height, out var n, out var pt, out var surf);
                if (d < HitEps)
                {
                    // Only a hit if we are moving into the surface (sliding along it is not).
                    if (Vec3.Dot(n, dir) < -1e-5f || d < -HitEps)
                    {
                        hit = new MotorHit { Distance = Math.Max(0f, t), Normal = n, Point = pt, SurfaceId = surf };
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
