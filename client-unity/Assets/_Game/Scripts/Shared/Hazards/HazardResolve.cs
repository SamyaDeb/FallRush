namespace FallRush.Shared
{
    /// <summary>Sets the seeded start phase on each def type (doors' phase is an extra fraction of the cycle).</summary>
    public static class HazardResolve
    {
        /// <summary>G4.1b: the lid grid's per-obstacle seed (<c>ObstaclePhase.Hash(hazardSeed, obstacleId)</c>; in M6 from the VRF seed).</summary>
        public static void SetSeed(ref LidGridDef d, uint obstacleSeed) => d.Seed = obstacleSeed;

        public static void SetPhase(ref SweeperDef d, int p) => d.Phase16 = p;
        public static void SetPhase(ref PlatformDef d, int p) => d.Phase16 = p;
        public static void SetPhase(ref TurnTableDef d, int p) => d.Phase16 = p;
        public static void SetPhase(ref GateDoorDef d, int p) => d.Phase16 = p;
        public static void SetPhase(ref MalletDef d, int p) => d.Phase16 = p;
        public static void SetPhase(ref ConveyorDef d, int p) { }
        public static void SetPhase(ref SpringPadDef d, int p) { }
        public static void SetPhase(ref BumperDef d, int p) { }
        /// <summary>G4.1a: the seeded release offset (fraction of the period); also derives the path length once.</summary>
        public static void SetPhase(ref RollingBallDef d, int p)
        {
            d.Phase16 = p;
            HazardMath.PrepareBall(ref d);
        }
    }
}
