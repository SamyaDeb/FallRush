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
        }

        public readonly Slot[] Slots = new Slot[MaxSlots];
        public readonly MovementConfig Cfg;

        private readonly InputSanitizer[] _inputs = new InputSanitizer[MaxSlots];
        private readonly TestBotInput[] _bots = new TestBotInput[MaxSlots];
        private readonly Vec3[] _push = new Vec3[MaxSlots];
        private readonly bool[] _pushedSinceSnap = new bool[MaxSlots];
        private readonly KinematicMotor _motor;
        private readonly IMotorWorld _world;
        private readonly Vec3[] _spawns;
        private readonly HazardSet _hazards;
        private float _botAreaCx, _botAreaCz, _botAreaHx = 10f, _botAreaHz = 10f;

        /// <summary>Last simulated tick (0 before the first step). Commands for <c>Tick + 1</c> are due next.</summary>
        public uint Tick { get; private set; }

        public uint NextTick => Tick + 1;

        public int HumanCount { get; private set; }
        public int BotCount { get; private set; }
        public long PushEvents { get; private set; }
        public long HazardHits { get; private set; }

        public HazardSet Hazards => _hazards;

        public SimCore(MovementConfig cfg, IMotorWorld world, Vec3[] spawns, HazardSet hazards = null)
        {
            Cfg = cfg;
            _world = world;
            _hazards = hazards ?? HazardSet.Empty;
            _motor = new KinematicMotor(cfg);
            if (spawns == null || spawns.Length < MaxSlots)
                throw new ArgumentException($"need {MaxSlots} spawn points", nameof(spawns));
            _spawns = spawns;
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
                if (Slots[i].Active && !Slots[i].Bot && Slots[i].ClientId == clientId)
                    return i;
            return -1;
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
            var s = new MotorState { Position = _spawns[slot], Yaw = 0 };
            return s;
        }

        /// <summary>Simulates tick <see cref="NextTick"/> for every active slot.</summary>
        public void Step()
        {
            Tick++;
            var dt = Cfg.Dt;
            _world.BeginTick(Tick);

            for (var i = 0; i < MaxSlots; i++)
            {
                ref var p = ref Slots[i];
                if (!p.Active)
                    continue;

                InputCmd cmd;
                if (p.Bot)
                    cmd = _bots[i].Next(Tick, in p.State);
                else
                    cmd = _inputs[i].Consume(Tick, out _);

                if (p.State.Has(MotorState.Respawning))
                {
                    if (--p.RespawnTicks <= 0)
                        p.State = SpawnState(i);
                    continue;
                }

                var r = PlayerStep.Run(_motor, ref p.State, in cmd, Tick, _world, _hazards);
                if ((r & PlayerStep.Result.HazardHit) != 0)
                    HazardHits++;
                if ((r & PlayerStep.Result.FellOut) != 0)
                {
                    p.RespawnTicks = Cfg.RespawnTicks;
                    p.Respawns++;
                }
            }

            Separate(dt);

            for (var i = 0; i < MaxSlots; i++)
                _inputs[i]?.BeginTick();
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
                if (!a.Active || a.State.Has(MotorState.Respawning))
                    continue;
                for (var j = i + 1; j < MaxSlots; j++)
                {
                    ref var b = ref Slots[j];
                    if (!b.Active || b.State.Has(MotorState.Respawning))
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
                if (!Slots[i].Active)
                    continue;
                dst[n++] = PlayerState.From((byte)i, in Slots[i].State, Slots[i].Bot ? MotorState.Bot : (byte)0);
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
