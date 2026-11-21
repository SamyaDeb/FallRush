using System;

namespace FallRush.Shared
{
    /// <summary>
    /// The resolved hazards of one map for one round (variant, hazard seed and modifier already applied), evaluated by tick.
    /// Shared by the server (<see cref="SimCore"/>) and client prediction, so every hazard effect is predicted exactly
    /// when the server applies it. Hazard time is round-relative: <see cref="Time"/> = tick − <see cref="RoundStartTick"/>.
    /// <para><b>Surfaces</b> (things a player stands on that move them): one id space, in this order: platforms,
    /// turn tables, conveyors, spring pads. <c>MotorState.Surface</c> = id + 1.</para>
    /// </summary>
    public sealed class HazardSet
    {
        // Effect counters (server statistics / QA; never read by the simulation).
        public long Launches, DoorPushes;

        public enum SurfaceKind : byte
        {
            None,
            Platform,
            TurnTable,
            Conveyor,
            SpringPad,
        }

        public readonly SweeperDef[] Sweepers;
        public readonly PlatformDef[] Platforms;
        public readonly ConveyorDef[] Conveyors;
        public readonly SpringPadDef[] Pads;
        public readonly TurnTableDef[] TurnTables;
        public readonly GateDoorDef[] Doors;
        public readonly MalletDef[] Mallets;
        public readonly BumperDef[] Bumpers;

        /// <summary>Server tick at which hazard time is 0 (the round's start, from <see cref="RoundParams"/>).</summary>
        public uint RoundStartTick;

        /// <summary>The map's fall volume (mm): below it a player respawns. <see cref="int.MinValue"/> = use <see cref="MovementConfig.KillY"/>.</summary>
        public int KillYMm = int.MinValue;

        public HazardSet(SweeperDef[] sweepers, PlatformDef[] platforms)
            : this(sweepers, platforms, null, null, null, null, null, null)
        {
        }

        public HazardSet(SweeperDef[] sweepers, PlatformDef[] platforms, ConveyorDef[] conveyors, SpringPadDef[] pads,
            TurnTableDef[] turnTables, GateDoorDef[] doors, MalletDef[] mallets, BumperDef[] bumpers)
        {
            Sweepers = sweepers ?? Array.Empty<SweeperDef>();
            Platforms = platforms ?? Array.Empty<PlatformDef>();
            Conveyors = conveyors ?? Array.Empty<ConveyorDef>();
            Pads = pads ?? Array.Empty<SpringPadDef>();
            TurnTables = turnTables ?? Array.Empty<TurnTableDef>();
            Doors = doors ?? Array.Empty<GateDoorDef>();
            Mallets = mallets ?? Array.Empty<MalletDef>();
            Bumpers = bumpers ?? Array.Empty<BumperDef>();
            if (SurfaceCount > 254)
                throw new ArgumentException("more than 254 moving surfaces");
        }

        public static readonly HazardSet Empty = new HazardSet(null, null);

        // ------------------------------------------------------------------ M4 kit (goo, crumble tiles, falling blocks, stars)

        public GooDef[] Goo = Array.Empty<GooDef>();
        public TileGridDef[] Grids = Array.Empty<TileGridDef>();
        public FallingBlockDef[] Blocks = Array.Empty<FallingBlockDef>();
        public StarFieldDef[] StarFields = Array.Empty<StarFieldDef>();
        /// <summary>Crumble Shelf: spinner bars sweep only from this hazard time on (60 s); <see cref="int.MinValue"/> = always.</summary>
        public int SweeperStartT = int.MinValue;
        /// <summary>The round's hazard seed (star timeline).</summary>
        public ulong Seed;
        /// <summary>Server-owned reactive state (tiles, blocks, stars), replicated with <c>rx</c>; null when the map has none.</summary>
        public ReactiveSet Reactive;

        public bool HasM4 => Goo.Length + Grids.Length + Blocks.Length + StarFields.Length > 0 || SweeperStartT != int.MinValue;

        // ------------------------------------------------------------------ G4 kit (rolling balls, lid grids)

        /// <summary>G4.1a <c>ROLLING_BALL</c> lanes (knock volumes, no collider), checked in <see cref="ApplyKnocks"/> after the mallets.</summary>
        public RollingBallDef[] RollingBalls = Array.Empty<RollingBallDef>();

        /// <summary>G4.1b <c>LID_FLIP</c> grids: lid colliders on/off from the pure state at each tick (posed like doors).</summary>
        public LidGridDef[] LidGrids = Array.Empty<LidGridDef>();

        /// <summary>True when the set has G4 kit defs (they are hashed only then, so older maps keep their content hash).</summary>
        public bool HasG4 => RollingBalls.Length + LidGrids.Length > 0;

        private LidState[] _lidStates = Array.Empty<LidState>();
        private LidState[] _lidGridStates = Array.Empty<LidState>();
        private int[] _lidScratch = Array.Empty<int>();
        private int _lidT = int.MinValue;
        private bool _lidValid;

        /// <summary>All lids of all grids, grid by grid in <see cref="LidGrids"/> order.</summary>
        public int LidCount
        {
            get
            {
                var n = 0;
                foreach (var g in LidGrids)
                    n += g.Count;
                return n;
            }
        }

        /// <summary>Sizes the lid buffers (call once after setting <see cref="LidGrids"/>; no allocation per tick afterwards).</summary>
        public void InitLids()
        {
            var total = LidCount;
            var max = 0;
            foreach (var g in LidGrids)
                max = Math.Max(max, g.Count);
            _lidStates = new LidState[total];
            _lidGridStates = new LidState[max];
            _lidScratch = new int[max];
            _lidValid = false;
        }

        /// <summary>Every lid's state at server tick <paramref name="tick"/> (cached for the last tick asked). Pure: f(defs, seed, t).</summary>
        public LidState[] LidStatesAt(uint tick)
        {
            var t = Time(tick);
            if (_lidValid && t == _lidT)
                return _lidStates;
            if (_lidStates.Length != LidCount)
                InitLids();
            var o = 0;
            for (var g = 0; g < LidGrids.Length; g++)
            {
                ref readonly var d = ref LidGrids[g];
                HazardMath.LidStates(in d, t, _lidGridStates, _lidScratch);
                Array.Copy(_lidGridStates, 0, _lidStates, o, d.Count);
                o += d.Count;
            }

            _lidT = t;
            _lidValid = true;
            return _lidStates;
        }

        /// <summary>M4: number of reactive elements (tiles, falling blocks, star anchors).</summary>
        public int ReactiveCount => (Grids.Length > 0 ? Grids[0].Count : 0) + Blocks.Length + (StarFields.Length > 0 ? StarFields[0].AnchorCount : 0);

        /// <summary>Creates <see cref="Reactive"/> for the M4 elements (call after setting them).</summary>
        public void InitReactive()
        {
            Reactive = Grids.Length > 0 || Blocks.Length > 0 || StarFields.Length > 0
                ? new ReactiveSet(Grids.Length > 0 ? Grids[0] : (TileGridDef?)null, Blocks, StarFields.Length > 0 ? StarFields[0] : (StarFieldDef?)null, Seed)
                : null;
        }

        /// <summary>Goo surface (mm) at <paramref name="tick"/>, or <see cref="int.MinValue"/> without goo.</summary>
        public int GooYMm(uint tick) => Goo.Length > 0 ? HazardMath.GooYMm(in Goo[0], Time(tick)) : int.MinValue;

        public int Count => Sweepers.Length + Platforms.Length + Conveyors.Length + Pads.Length + TurnTables.Length + Doors.Length + Mallets.Length + Bumpers.Length + RollingBalls.Length + LidGrids.Length;

        /// <summary>Round-relative hazard time of server tick <paramref name="tick"/> (negative before the round starts).</summary>
        public int Time(uint tick) => unchecked((int)(tick - RoundStartTick));

        // ------------------------------------------------------------------ surfaces

        public int SurfaceCount => Platforms.Length + TurnTables.Length + Conveyors.Length + Pads.Length;
        public int PlatformSurface(int i) => i;
        public int TurnTableSurface(int i) => Platforms.Length + i;
        public int ConveyorSurface(int i) => Platforms.Length + TurnTables.Length + i;
        public int PadSurface(int i) => Platforms.Length + TurnTables.Length + Conveyors.Length + i;

        public SurfaceKind KindOf(int surfaceId, out int index)
        {
            index = surfaceId;
            if (surfaceId < 0)
                return SurfaceKind.None;
            if (index < Platforms.Length)
                return SurfaceKind.Platform;
            index -= Platforms.Length;
            if (index < TurnTables.Length)
                return SurfaceKind.TurnTable;
            index -= TurnTables.Length;
            if (index < Conveyors.Length)
                return SurfaceKind.Conveyor;
            index -= Conveyors.Length;
            if (index < Pads.Length)
                return SurfaceKind.SpringPad;
            return SurfaceKind.None;
        }

        /// <summary>
        /// How surface <paramref name="surfaceId"/> moves a rider whose feet are at <paramref name="feet"/>, from tick − 1 to
        /// <paramref name="tick"/>: platform translation, turn-table rotation about its axis (point-aware), belt push.
        /// Always whole millimetres, so carried riders stay mm-exact.
        /// </summary>
        public bool SurfaceDelta(int surfaceId, uint tick, Vec3 feet, out Vec3 delta)
        {
            var t = Time(tick);
            switch (KindOf(surfaceId, out var i))
            {
                case SurfaceKind.Platform:
                    delta = HazardMath.PlatformDelta(in Platforms[i], t);
                    return true;
                case SurfaceKind.TurnTable:
                    HazardMath.TurnTableCarryMm(in TurnTables[i], t, MotorState.QuantizeMm(feet.X), MotorState.QuantizeMm(feet.Z), out var dx, out var dz);
                    delta = HazardMath.MmToVec(dx, 0, dz);
                    return true;
                case SurfaceKind.Conveyor:
                    HazardMath.ConveyorDeltaMm(in Conveyors[i], t, out var cx, out var cy, out var cz);
                    delta = HazardMath.MmToVec(cx, cy, cz);
                    return true;
                default:
                    delta = Vec3.Zero;
                    return false;
            }
        }

        /// <summary>M2 form: platform <paramref name="index"/>'s displacement into <paramref name="tick"/>.</summary>
        public bool PlatformDelta(int index, uint tick, out Vec3 delta)
        {
            if ((uint)index >= (uint)Platforms.Length)
            {
                delta = Vec3.Zero;
                return false;
            }

            delta = HazardMath.PlatformDelta(in Platforms[index], Time(tick));
            return true;
        }

        /// <summary>Door box centre (mm) at <paramref name="tick"/>.</summary>
        public void DoorCenterMm(int i, uint tick, out int x, out int y, out int z)
        {
            ref readonly var d = ref Doors[i];
            x = d.CenterXMm;
            y = d.CenterYMm + HazardMath.DoorLiftMm(in d, Time(tick));
            z = d.CenterZMm;
        }

        // ------------------------------------------------------------------ effects (all called from PlayerStep.Run)

        /// <summary>Velocity override + stun + immunity (doc 08 §2 knockback rules). Ends any dive / landing / ride.</summary>
        public static void Knock(ref MotorState s, float nx, float nz, int pushCms, int upCms, int stunTicks, int immunityTicks)
        {
            // Straight to cm/s; the explicit casts round each product (no fused multiply-add on any platform).
            var vx = (float)(nx * pushCms);
            var vz = (float)(nz * pushCms);
            s.VelX = (short)HazardMath.RoundMm(vx);
            s.VelY = (short)upCms;
            s.VelZ = (short)HazardMath.RoundMm(vz);
            s.StunTicks = (byte)Math.Min(255, Math.Max(0, stunTicks));
            s.ImmunityTicks = (byte)Math.Min(255, Math.Max(0, immunityTicks));
            s.Set(MotorState.Grounded, false);
            s.Set(MotorState.Diving, false);
            s.Set(MotorState.DiveLanding, false);
            s.Set(MotorState.Stumbling, false);
            s.LandTicks = 0;
            s.CoyoteTicks = 0;
            s.JumpBufferTicks = 0;
            s.Surface = 0;
        }

        /// <summary>
        /// Hit height for the analytic knock hazards: a diving (or dive-landed) Rushling lies nearly flat (80° pitch), so it
        /// presents 0.5 m instead of the 1.0 m standing capsule. That is how the high spinner bar is dived under; the motor's
        /// collision capsule stays upright.
        /// </summary>
        public const int DiveHitHeightMm = 500;

        public static int HitHeightMm(in MotorState s, MovementConfig cfg) =>
            s.Has(MotorState.Diving) || s.Has(MotorState.DiveLanding) ? DiveHitHeightMm : MotorState.QuantizeMm(cfg.Height);

        /// <summary>M2 API: sweeper hits only. Returns true on a new hit.</summary>
        public bool ApplySweepers(ref MotorState s, uint tick, MovementConfig cfg)
        {
            if (s.ImmunityTicks > 0 || s.Has(MotorState.Respawning))
                return false;
            var t = Time(tick);
            if (t < SweeperStartT)
                return false; // M4 Crumble Shelf: the spinners sweep only from 60 s
            var r = MotorState.QuantizeMm(cfg.Radius);
            var h = HitHeightMm(in s, cfg);
            for (var i = 0; i < Sweepers.Length; i++)
            {
                ref readonly var d = ref Sweepers[i];
                if (!HazardMath.SweeperContactMm(in d, t, s.PosX, s.PosY, s.PosZ, r, h, out var nx, out var nz))
                    continue;
                Knock(ref s, nx, nz, d.KnockbackCms, d.KnockUpCms, d.StunTicks, d.ImmunityTicks);
                return true;
            }

            return false;
        }

        /// <summary>
        /// Every knock hazard (spinner bars, mallets, rolling balls, bumpers) after the motor step of <paramref name="tick"/>. At most one
        /// hit per tick; none while immune (the 0.5 s after a stun), so two adjacent hazards cannot stun-lock a player.
        /// </summary>
        public bool ApplyKnocks(ref MotorState s, uint tick, MovementConfig cfg)
        {
            if (s.ImmunityTicks > 0 || s.Has(MotorState.Respawning))
                return false;
            if (ApplySweepers(ref s, tick, cfg))
                return true;
            var t = Time(tick);
            var r = MotorState.QuantizeMm(cfg.Radius);
            var h = HitHeightMm(in s, cfg);
            for (var i = 0; i < Mallets.Length; i++)
            {
                ref readonly var d = ref Mallets[i];
                if (!HazardMath.MalletContactMm(in d, t, s.PosX, s.PosY, s.PosZ, r, h, out var nx, out var nz))
                    continue;
                Knock(ref s, nx, nz, d.KnockbackCms, d.KnockUpCms, d.StunTicks, d.ImmunityTicks);
                return true;
            }

            for (var i = 0; i < RollingBalls.Length; i++)
            {
                ref readonly var d = ref RollingBalls[i];
                if (!HazardMath.RollingBallContactMm(in d, t, s.PosX, s.PosY, s.PosZ, r, h, out var nx, out var nz))
                    continue;
                Knock(ref s, nx, nz, d.KnockbackCms, d.KnockUpCms, d.StunTicks, d.ImmunityTicks);
                return true;
            }

            for (var i = 0; i < Bumpers.Length; i++)
            {
                ref readonly var d = ref Bumpers[i];
                if (!HazardMath.BumperContactMm(in d, s.PosX, s.PosY, s.PosZ, r, h, out var nx, out var nz))
                    continue;
                Knock(ref s, nx, nz, d.KnockbackCms, d.KnockUpCms, d.StunTicks, d.ImmunityTicks);
                return true;
            }

            return false;
        }

        /// <summary>Spring pads: grounded on a pad at the end of the step → launch (velocity override), once per landing.</summary>
        public bool ApplySprings(ref MotorState s)
        {
            if (!s.Has(MotorState.Grounded) || s.Surface == 0)
                return false;
            if (KindOf(s.Surface - 1, out var i) != SurfaceKind.SpringPad)
                return false;
            ref readonly var d = ref Pads[i];
            s.VelX = (short)d.LaunchXCms;
            s.VelY = (short)d.LaunchYCms;
            s.VelZ = (short)d.LaunchZCms;
            s.Set(MotorState.Grounded, false);
            s.Set(MotorState.Diving, false);
            s.Set(MotorState.DiveLanding, false);
            s.Set(MotorState.Stumbling, false);
            s.LandTicks = 0;
            s.CoyoteTicks = 0;
            s.JumpBufferTicks = 0;
            s.Surface = 0;
            Launches++;
            return true;
        }

        /// <summary>
        /// Gate doors, before the motor step of <paramref name="tick"/> (the doors are already posed for it): a door that
        /// closed onto the player pushes them out along its normal, to the side they are on, so nobody is ever stuck in a door.
        /// </summary>
        public bool ApplyDoors(ref MotorState s, uint tick, MovementConfig cfg)
        {
            if (Doors.Length == 0)
                return false;
            var t = Time(tick);
            var r = cfg.Radius;
            var pushed = false;
            for (var i = 0; i < Doors.Length; i++)
            {
                ref readonly var d = ref Doors[i];
                var lift = HazardMath.DoorLiftMm(in d, t);
                var bottom = d.CenterYMm + lift - d.HalfYMm;
                var top = d.CenterYMm + lift + d.HalfYMm;
                var rMm = MotorState.QuantizeMm(r);
                var hMm = MotorState.QuantizeMm(cfg.Height);
                if (s.PosY >= top || s.PosY + hMm <= bottom)
                    continue;
                var dx = s.PosX - d.CenterXMm;
                var dz = s.PosZ - d.CenterZMm;
                if (Math.Abs(dx) >= d.HalfXMm + rMm || Math.Abs(dz) >= d.HalfZMm + rMm)
                    continue;
                var clear = rMm + MotorState.QuantizeMm(cfg.Skin) + 2;
                if (HazardMath.DoorNormalAxis(in d) == 0)
                {
                    var side = dx >= 0 ? 1 : -1;
                    s.PosX = d.CenterXMm + side * (d.HalfXMm + clear);
                    if (s.VelX * side < 0)
                        s.VelX = 0;
                }
                else
                {
                    var side = dz >= 0 ? 1 : -1;
                    s.PosZ = d.CenterZMm + side * (d.HalfZMm + clear);
                    if (s.VelZ * side < 0)
                        s.VelZ = 0;
                }

                pushed = true;
            }

            if (pushed)
                DoorPushes++;
            return pushed;
        }
    }

    /// <summary>
    /// One simulated tick of one player, identical on the server and in client prediction (doc 04 §4.2/§4.3). The caller
    /// poses the world first (<c>world.BeginTick(tick)</c>, once per tick: platforms, doors, G4 lids). Then: door push-out → motor step (carried by
    /// moving surfaces through <see cref="IMotorWorld.SurfaceDelta"/>, conveyor push included) → knock hazards (spinners, mallets, rolling balls, bumpers) → spring
    /// pads → fall volume. Player–player separation is server-only and not part of this.
    /// </summary>
    public static class PlayerStep
    {
        [Flags]
        public enum Result : byte
        {
            None = 0,
            HazardHit = 1,
            FellOut = 2,
            Launched = 4,
            DoorPushed = 8,
            Gooed = 16, // M4: touched the goo plane (frozen like a fall; survival eliminates)
        }

        public static Result Run(KinematicMotor motor, ref MotorState s, in InputCmd cmd, uint tick, IMotorWorld world, HazardSet hazards)
        {
            if (s.Has(MotorState.Respawning))
                return Result.None;
            var cfg = motor.Cfg;
            var r = Result.None;
            if (hazards != null && hazards.ApplyDoors(ref s, tick, cfg))
                r |= Result.DoorPushed;
            motor.Step(ref s, in cmd, cfg.Dt, world);
            if (hazards != null)
            {
                if (hazards.ApplyKnocks(ref s, tick, cfg))
                    r |= Result.HazardHit;
                else if (hazards.ApplySprings(ref s))
                    r |= Result.Launched;
            }

            var killed = hazards != null && hazards.KillYMm != int.MinValue ? s.PosY < hazards.KillYMm : s.PosY * 0.001f < cfg.KillY;
            // M4 GOO_PLANE: feet below the goo surface → out (frozen like a fall; the survival rules eliminate).
            if (!killed && hazards != null && hazards.Goo.Length > 0 && s.PosY < hazards.GooYMm(tick))
            {
                s.Velocity = Vec3.Zero;
                s.Flags = MotorState.Respawning;
                s.Surface = 0;
                return r | Result.Gooed;
            }

            if (killed)
            {
                s.Velocity = Vec3.Zero;
                s.Flags = MotorState.Respawning;
                s.Surface = 0;
                r |= Result.FellOut;
            }

            return r;
        }
    }
}
