namespace FallRush.Shared
{
    public struct MotorHit
    {
        public float Distance;
        public Vec3 Point;
        public Vec3 Normal;
        /// <summary>Moving-surface index (a <see cref="HazardSet"/> platform), or −1 for static geometry.</summary>
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

        /// <summary>How far moving surface <paramref name="surfaceId"/> moved from the previous tick to the current one (platform carry).</summary>
        bool SurfaceDelta(int surfaceId, out Vec3 delta);
    }
}
