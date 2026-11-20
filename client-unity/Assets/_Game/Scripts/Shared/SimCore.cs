using System;

namespace FallRush.Shared
{
    /// <summary>
    /// Engine-free authoritative simulation for one map (doc 04 §4.2 steps 1, 2 and 5 inputs; hazards and rules come later).
    /// <c>FallRush.Net.ServerSim</c> is the thin NGO adapter around it; EditMode tests drive it with an analytic world.
    /// <para>Per tick: consume each slot's input for this tick (sanitised; bots via <see cref="TestBotInput"/>) →
    /// <see cref="KinematicMotor.Step"/> → fall volume / respawn → player–player soft separation (server-only, not predicted).</para>
    /// </summary>
    public sealed class SimCore
    {
        public const int MaxSlots = Protocol.MaxPlayers;

        public struct Slot
        {
            public bool Active;
            public bool Bot;
            public ulong ClientId;
            public MotorState State;
            public int RespawnTicks;
            public uint Respawns;
            /// <summary>M4: a human whose connection dropped: the Rushling stays in the world on neutral input (grace, M4-6).</summary>
            public bool Detached;
            /// <summary>What <see cref="PlayerStep.Run"/> reported on the last simulated tick (rules read goo / fall-out / hits).</summary>
            public PlayerStep.Result LastStep;
        }

        public readonly Slot[] Slots = new Slot[MaxSlots];
        public MovementConfig Cfg { get; private set; }

        private readonly InputSanitizer[] _inputs = new InputSanitizer[MaxSlots];
        private readonly TestBotInput[] _bots = new TestBotInput[MaxSlots];
        private readonly Vec3[] _push = new Vec3[MaxSlots];
        private readonly bool[] _pushedSinceSnap = new bool[MaxSlots];
        private KinematicMotor _motor;
        private IMotorWorld _world;
        private Vec3[] _spawns;
        private byte[] _spawnYaws = new byte[MaxSlots];
        private HazardSet _hazards;
        private float _botAreaCx, _botAreaCz, _botAreaHx = 10f, _botAreaHz = 10f;

        /// <summary>Last simulated tick (0 before the first step). Commands for <c>Tick + 1</c> are due next.</summary>
        public uint Tick { get; private set; }

        public uint NextTick => Tick + 1;

        public int HumanCount { get; private set; }
        public int BotCount { get; private set; }
        public long PushEvents { get; private set; }
        public long HazardHits { get; private set; }

        public HazardSet Hazards => _hazards;
        public IMotorWorld World => _world;
        public KinematicMotor Motor => _motor;

        /// <summary>Round rules (race, M3b); null = free play (sandbox). Called around every step.</summary>
        public ISimRules Rules;
        /// <summary>Per-slot input override for bots (waypoint bots, M3b); null = <see cref="TestBotInput"/>.</summary>
        public Func<int, uint, MotorState, InputCmd> BotInput;
        /// <summary>Hazard hits per slot (QA statistics).</summary>
        public readonly int[] HitsBySlot = new int[MaxSlots];
        public readonly int[] FallsBySlot = new int[MaxSlots];

        /// <summary>
        /// New round: motor constants (modifier), world and hazards (variant, seed, round start) and spawns. Slots, inputs and
        /// connections stay. Every active player is put back on their spawn.
        /// </summary>
        public void Reconfigure(MovementConfig cfg, IMotorWorld world, HazardSet hazards, Vec3[] spawns, byte[] spawnYaws = null)
        {
            Cfg = cfg;
            _motor = new KinematicMotor(cfg);
            _world = world;
            _hazards = hazards ?? HazardSet.Empty;
            if (spawns == null || spawns.Length < MaxSlots)
                throw new ArgumentException($"need {MaxSlots} spawn points", nameof(spawns));
            _spawns = spawns;
            _spawnYaws = spawnYaws ?? new byte[MaxSlots];
            Array.Clear(HitsBySlot, 0, MaxSlots);
            Array.Clear(FallsBySlot, 0, MaxSlots);
            for (var i = 0; i < MaxSlots; i++)
            {
                if (!Slots[i].Active)
                    continue;
                Slots[i].State = SpawnState(i);
                Slots[i].RespawnTicks = 0;
            }
        }

        /// <summary>Teleports <paramref name="slot"/> (respawn at a checkpoint, round start).</summary>
        public void Place(int slot, Vec3 feet, byte yaw)
        {
            Slots[slot].State = new MotorState { Position = feet, Yaw = yaw };
            Slots[slot].RespawnTicks = 0;
        }

        public SimCore(MovementConfig cfg, IMotorWorld world, Vec3[] spawns, HazardSet hazards = null, byte[] spawnYaws = null)
        {
            Reconfigure(cfg, world, hazards, spawns, spawnYaws);
        }

        public void SetBotArea(float cx, float cz, float halfX, float halfZ)
        {
            _botAreaCx = cx;
            _botAreaCz = cz;
            _botAreaHx = halfX;
            _botAreaHz = halfZ;
        }

        public InputSanitizer Input(int slot) => _inputs[slot];

        public int FindSlot(ulong clientId)
        {
            for (var i = 0; i < MaxSlots; i++)
                if (Slots[i].Active && !Slots[i].Bot && !Slots[i].Detached && Slots[i].ClientId == clientId)
                    return i;
            return -1;
        }

        /// <summary>M4-6: the human's connection dropped; the slot stays in the world on neutral input (no new commands arrive).</summary>
        public void Detach(int slot)
        {
            if (!Slots[slot].Active || Slots[slot].Bot)
                return;
            Slots[slot].Detached = true;
            Slots[slot].ClientId = ulong.MaxValue;
        }

        /// <summary>M4-6: a reconnect takes the same slot back (fresh input buffer: the client's seq restarts).</summary>
        public void Attach(int slot, ulong clientId)
        {
            if (!Slots[slot].Active || Slots[slot].Bot)
                return;
            Slots[slot].Detached = false;
            Slots[slot].ClientId = clientId;
            _inputs[slot] = new InputSanitizer();
        }

        /// <summary>A human slot at <paramref name="slot"/> (the lobby re-seats a player). False if taken.</summary>
        public bool AddHumanAt(int slot, ulong clientId)
        {
            if (Slots[slot].Active)
                return false;
            Activate(slot, bot: false, clientId);
            _inputs[slot] = new InputSanitizer();
            HumanCount++;
            return true;
        }

        /// <summary>A bot at <paramref name="slot"/> (roster lock fills the free slots). False if taken.</summary>
        public bool AddBotAt(int slot)
        {
            if (Slots[slot].Active)
                return false;
            Activate(slot, bot: true, 0);
            _bots[slot] = new TestBotInput(slot) { AreaCenterX = _botAreaCx, AreaCenterZ = _botAreaCz, AreaHalfX = _botAreaHx, AreaHalfZ = _botAreaHz };
            BotCount++;
            return true;
        }

        /// <summary>Lowest free slot; if none, replaces the highest-numbered bot. −1 if all slots are humans.</summary>
        public int AddHuman(ulong clientId)
        {
            var slot = FreeSlot();
            if (slot < 0)
            {
                for (var i = MaxSlots - 1; i >= 0; i--)
                {
                    if (Slots[i].Active && Slots[i].Bot)
                    {
                        Remove(i);
                        slot = i;
                        break;
                    }
                }
            }

            if (slot < 0)
                return -1;
            Activate(slot, bot: false, clientId);
            _inputs[slot] = new InputSanitizer();
            HumanCount++;
            return slot;
        }

        public int AddBot()
        {
            var slot = FreeSlot();
            if (slot < 0)
                return -1;
            Activate(slot, bot: true, 0);
            _bots[slot] = new TestBotInput(slot)
            {
                AreaCenterX = _botAreaCx, AreaCenterZ = _botAreaCz, AreaHalfX = _botAreaHx, AreaHalfZ = _botAreaHz,
            };
            BotCount++;
            return slot;
        }

        public void Remove(int slot)
        {
            if (!Slots[slot].Active)
                return;
            if (Slots[slot].Bot)
                BotCount--;
            else
                HumanCount--;
            Slots[slot] = default;
            _pushedSinceSnap[slot] = false;
            _inputs[slot] = null;
            _bots[slot] = null;
        }

        private int FreeSlot()
        {
            for (var i = 0; i < MaxSlots; i++)
                if (!Slots[i].Active)
                    return i;
            return -1;
        }

        private void Activate(int slot, bool bot, ulong clientId)
        {
            Slots[slot] = new Slot { Active = true, Bot = bot, ClientId = clientId, State = SpawnState(slot) };
        }

        public MotorState SpawnState(int slot)
        {
            var s = new MotorState { Position = _spawns[slot], Yaw = _spawnYaws[slot] };
            return s;
        }

        /// <summary>Simulates tick <see cref="NextTick"/> for every active slot.</summary>
        public void Step()
        {
            Tick++;
            var dt = Cfg.Dt;
            _world.BeginTick(Tick);
            Rules?.BeginTick(this, Tick);

            for (var i = 0; i < MaxSlots; i++)
            {
                ref var p = ref Slots[i];
                if (!p.Active)
                    continue;

                InputCmd cmd;
                if (p.Bot)
                    cmd = BotInput != null ? BotInput(i, Tick, p.State) : _bots[i].Next(Tick, in p.State);
                else
                    cmd = _inputs[i].Consume(Tick, out _);

                if (Rules != null && !Rules.AcceptsInput(this, i))
                    cmd = new InputCmd { Seq = cmd.Seq, Tick = cmd.Tick, CamYaw = cmd.CamYaw }; // countdown / finished: inputs ignored

                p.LastStep = PlayerStep.Result.None;
                if (Rules != null && !Rules.Simulates(this, i))
                    continue; // finished racers stand at the finish, out of the simulation (M4: and players out of the round)

                if (p.State.Has(MotorState.Respawning))
                {
                    if (--p.RespawnTicks <= 0)
                    {
                        if (Rules == null || !Rules.Respawn(this, i))
                            p.State = SpawnState(i);
                    }

                    continue;
                }

                var r = PlayerStep.Run(_motor, ref p.State, in cmd, Tick, _world, _hazards);
                p.LastStep = r;
                if ((r & PlayerStep.Result.HazardHit) != 0)
                {
                    HazardHits++;
                    HitsBySlot[i]++;
                }

                if ((r & (PlayerStep.Result.FellOut | PlayerStep.Result.Gooed)) != 0)
                {
                    p.RespawnTicks = Cfg.RespawnTicks;
                    p.Respawns++;
                    FallsBySlot[i]++;
                }
            }

            Separate(dt);
            Rules?.EndTick(this, Tick);

            for (var i = 0; i < MaxSlots; i++)
                _inputs[i]?.BeginTick();
        }

        /// <summary>
        /// M4: advances the tick without simulating anyone (the server is loading the next map this frame). Inputs for the
        /// tick are consumed and dropped, so the sanitisers' tick windows stay aligned with the clock.
        /// </summary>
        public void SkipTick()
        {
            Tick++;
            for (var i = 0; i < MaxSlots; i++)
            {
                if (!Slots[i].Active || Slots[i].Bot || _inputs[i] == null)
                    continue;
                _inputs[i].Consume(Tick, out _);
                _inputs[i].BeginTick();
            }
        }

        /// <summary>Soft player–player separation: overlapping capsules are pushed apart at up to <see cref="MovementConfig.PushMaxSpeed"/>; no stun.</summary>
        private void Separate(float dt)
        {
            var minD = Cfg.Radius * 2f;
            var maxStep = Cfg.PushMaxSpeed * dt;
            Array.Clear(_push, 0, _push.Length);
            var any = false;

            for (var i = 0; i < MaxSlots; i++)
            {
                ref var a = ref Slots[i];
                if (!a.Active || a.State.Has(MotorState.Respawning) || (Rules != null && !Rules.Collides(this, i)))
                    continue;
                for (var j = i + 1; j < MaxSlots; j++)
                {
                    ref var b = ref Slots[j];
                    if (!b.Active || b.State.Has(MotorState.Respawning) || (Rules != null && !Rules.Collides(this, j)))
                        continue;
                    var dyMm = a.State.PosY - b.State.PosY;
                    if (dyMm >= 1000 || dyMm <= -1000)
                        continue;
                    var dx = (a.State.PosX - b.State.PosX) * 0.001f;
                    var dz = (a.State.PosZ - b.State.PosZ) * 0.001f;
                    var d2 = dx * dx + dz * dz;
                    if (d2 >= minD * minD)
                        continue;
                    var d = (float)Math.Sqrt(d2);
                    float nx, nz;
                    if (d > 1e-4f)
                    {
                        nx = dx / d;
                        nz = dz / d;
                    }
                    else
                    {
                        Yaw256.Dir((byte)(i * 37 + j * 11), out nx, out nz);
                    }

                    var m = Math.Min((minD - d) * 0.5f, maxStep * 0.5f);
                    _push[i] += new Vec3(nx * m, 0f, nz * m);
                    _push[j] -= new Vec3(nx * m, 0f, nz * m);
                    any = true;
                }
            }

            if (!any)
                return;

            for (var i = 0; i < MaxSlots; i++)
            {
                var v = _push[i];
                var l2 = v.SqrMagnitude;
                if (l2 <= 0f)
                    continue;
                if (l2 > maxStep * maxStep)
                    v *= maxStep / (float)Math.Sqrt(l2);
                _motor.Displace(ref Slots[i].State, v, _world);
                _pushedSinceSnap[i] = true;
                PushEvents++;
            }
        }

        /// <summary>Writes all active players into <paramref name="dst"/>; returns the count.</summary>
        public int WritePlayerStates(Span<PlayerState> dst)
        {
            var n = 0;
            for (var i = 0; i < MaxSlots; i++)
            {
                if (!Slots[i].Active || (Rules is IRoundRules rr && !rr.InRound(i)))
                    continue; // M4: players out of the round (eliminated earlier, late loaders) are not drawn
                dst[n++] = PlayerState.From((byte)i, in Slots[i].State, Slots[i].Bot ? MotorState.Bot : (byte)0, Rules != null ? Rules.RaceByte(i) : (byte)0);
            }

            return n;
        }

        public SnapHeader HeaderFor(int slot, int count)
        {
            var h = new SnapHeader { ServerTick = Tick, OwnSlot = SnapHeader.NoSlot, InputLead = SnapHeader.LeadUnknown, Count = (byte)count };
            if (slot < 0 || !Slots[slot].Active)
                return h;
            ref readonly var s = ref Slots[slot].State;
            h.OwnSlot = (byte)slot;
            h.OwnCoyote = s.CoyoteTicks;
            h.OwnJumpBuffer = s.JumpBufferTicks;
            h.OwnDiveCooldown = s.DiveCooldownTicks;
            h.OwnLand = s.LandTicks;
            h.OwnImmunity = s.ImmunityTicks;
            h.OwnSurface = s.Surface;
            if (_pushedSinceSnap[slot])
            {
                h.OwnEvents |= SnapHeader.EventPushed;
                _pushedSinceSnap[slot] = false; // one header per recipient per snapshot
            }
            var input = _inputs[slot];
            if (input != null)
            {
                h.AckSeq = input.AckSeq;
                h.InputLead = input.TakeMinLead();
            }

            return h;
        }
    }
}

namespace FallRush.Shared
{
    /// <summary>Round rules hooked into <see cref="SimCore.Step"/> (M3b: <c>RaceRules</c>). Engine-free.</summary>
    public interface ISimRules
    {
        /// <summary>Before any player of <paramref name="tick"/> is stepped (phase changes).</summary>
        void BeginTick(SimCore sim, uint tick);

        /// <summary>After the players and the separation of <paramref name="tick"/> (checkpoints, finish, timer).</summary>
        void EndTick(SimCore sim, uint tick);

        /// <summary>False during the countdown and for finished players: the command is replaced by a neutral one.</summary>
        bool AcceptsInput(SimCore sim, int slot);

        /// <summary>False for players taken out of the simulation (finished racers).</summary>
        bool Simulates(SimCore sim, int slot);

        /// <summary>False for players that are out of player–player collisions (finished racers).</summary>
        bool Collides(SimCore sim, int slot);

        /// <summary>Respawn after a fall (at the last checkpoint); false = the default spawn.</summary>
        bool Respawn(SimCore sim, int slot);

        /// <summary>The snapshot race byte for <paramref name="slot"/> (<see cref="PlayerState.Race"/>).</summary>
        byte RaceByte(int slot);
    }
}
