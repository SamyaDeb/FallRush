namespace FallRush.Shared
{
    public struct MotorHit
    {
        public float Distance;
        public Vec3 Point;
        public Vec3 Normal;
        /// <summary>Surface id (<see cref="HazardSet"/>: platform, turn table, conveyor or spring pad), or −1 for plain static geometry.</summary>
        public int SurfaceId;
    }

    /// <summary>
    /// Collision queries for <see cref="KinematicMotor"/>. The capsule is always upright; <c>feet</c> is its bottom point.
    /// In play: PhysX scene queries on the <c>Gameplay</c> layer (<c>FallRush.Net.PhysxMotorWorld</c>).
    /// In EditMode tests: an analytic world (planes + boxes).
    /// </summary>
    public interface IMotorWorld
    {
        /// <summary>Sweeps the capsule from <paramref name="feet"/> along unit <paramref name="dir"/> up to <paramref name="maxDistance"/>.</summary>
        bool CapsuleSweep(Vec3 feet, float radius, float height, Vec3 dir, float maxDistance, out MotorHit hit);

        /// <summary>Ray query (used to read the true face normal under a step edge).</summary>
        bool Raycast(Vec3 origin, Vec3 dir, float maxDistance, out MotorHit hit);

        /// <summary>If the capsule overlaps geometry, returns the smallest translation that separates it.</summary>
        bool ComputePenetration(Vec3 feet, float radius, float height, out Vec3 correction);

        /// <summary>
        /// Poses moving geometry for <paramref name="tick"/> (<c>pose = f(params, tick)</c>, <see cref="HazardMath"/>).
        /// Called once before the players of that tick are stepped; a no-op for static worlds.
        /// </summary>
        void BeginTick(uint tick);

        /// <summary>
        /// How surface <paramref name="surfaceId"/> moves a rider standing at <paramref name="feet"/> from the previous tick to
        /// the current one: linear platform carry, rotational turn-table carry (point-aware), conveyor belt push.
        /// </summary>
        bool SurfaceDelta(int surfaceId, Vec3 feet, out Vec3 delta);
    }

    /// <summary>
    /// OC-56 (2026-11-24, user-approved narrow motor fix): a world that opts in lets a falling capsule land on a surface that
    /// rises under it. Without it the ground probe only runs while vel.y ≤ 0 after collide-and-slide, and sliding onto an
    /// up-slope turns the fall into a small climb (vel.y &gt; 0), so a jump onto any ramp skated 3 mm above it, airborne, until
    /// the ramp ended. Opt-in per map (Capsule Run, Goo Tower, Sandbox: Classic-only maps); the on-chain Final's map and
    /// rush-core (Rust) keep the old rule, so the program, the wasm and the motor vectors are unchanged.
    /// </summary>
    public interface ISlopeLandingWorld
    {
        bool SlopeLanding { get; }
    }
}
