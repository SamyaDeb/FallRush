using System;
using System.Collections.Generic;
using System.Text;

namespace FallRush.Shared
{
    /// <summary>What the <see cref="MatchDirector"/> needs from its engine (the Unity server, or a fake in EditMode tests).</summary>
    public interface IMatchHost
    {
        /// <summary>Start loading <paramref name="mapId"/> for the next stage (lobby or a round); the host reconfigures the sim's world when it is loaded.</summary>
        void LoadMap(byte mapId, in RoundParams p);

        /// <summary>True once the last requested map is loaded and the sim runs on it.</summary>
        bool MapLoaded { get; }

        /// <summary>Round rules for the loaded map (Race / Survival / Skill / Final / Lobby).</summary>
        IRoundRules CreateRules(byte mapId);

        /// <summary>Bot inputs for a round that just began (BotBrain on the loaded map); null = the sim's default.</summary>
        Func<int, uint, MotorState, InputCmd> CreateBotInput(IRoundRules rules, RosterSlot[] roster, in RoundParams p);

        /// <summary>The round began (params final: RUSH tick, qualify count): broadcast <c>round</c>.</summary>
        void OnRoundStarted(in RoundParams p);

        /// <summary>A transcript event was appended (send <c>mevt</c>, write the jsonl log).</summary>
        void OnEvent(in MatchEvent e);

        /// <summary>The match phase or the roster changed (broadcast <c>match</c>).</summary>
        void OnMatchChanged();
    }

    /// <summary>
    /// The match state machine (doc 05 §4, M4 §3.1), engine-free and driven by <see cref="SimCore"/> ticks:
    /// <code>
    /// LOBBY_OPEN → COUNTDOWN (20 s / host Start) → ROSTER_LOCKED (bots fill to the match size) → DEALING (3 s) → DEALT (3 s)
    ///   → (ROUND_INTRO(r): load + ready (≤ 10 s) → START_ROUND → intro 2 s + 3-2-1 → ROUND_ACTIVE(r) → ROUND_RESULT(r) 6 s
    ///      → INTERMISSION 8 s) × rounds → CHAMPION 8 s → FINALIZING (no-op, TODO(M6)) → DONE (receipt, ≤ 20 s) → LOBBY_OPEN
    ///   ABORTED (5 s) → LOBBY_OPEN
    /// </code>
    /// Owns the roster, the deal (<see cref="Deal"/> from a 32-byte seed; practice seed in M4, VRF in M6), the per-round
    /// rules, qualification and elimination with reasons, the champion, and the transcript events in program order (doc 06:
    /// seq 0 = DEAL; per round START_ROUND, QUALIFIED in placement order, ELIMINATED, COMPLETE_ROUND; COMPLETE_MATCH or
    /// ABORT). Deterministic: the same seed, roster and inputs give the same events and transcript.
    /// </summary>
    public sealed class MatchDirector
    {
        // ---- timings (ticks at 30 Hz; M4-10) ----
        public const int CountdownTicks = 600, DealingTicks = 90, DealtTicks = 90, LoadTimeoutTicks = 300;
        public const int RoundIntroTicks = 60, RoundCountdownTicks = 90; // 5 s: 2 s intro (fly-through starts) + 3-2-1-RUSH
        public const int ResultTicks = 180, IntermissionTicks = 240, ChampionTicks = 240, DoneTicks = 600, AbortTicks = 150;
        public const int GraceTicks = 600; // M4-6: 20 s
        public const int MaxSlots = SimCore.MaxSlots;

        public static readonly string[] BotNames = { "Gizmo", "Sprocket", "Pip", "Nimbus", "Widget", "Tinker", "Doodle", "Zippy", "Bloop", "Clank", "Nova", "Fizz" };

        private readonly SimCore _sim;
        private readonly IMatchHost _host;

        // ---- configuration ----
        /// <summary>Soak / QA: start matches with no human in the lobby, never abort for lack of humans.</summary>
        public bool Soak;
        /// <summary>Occupants at roster lock (bots fill to it). 12 for practice (M4-2).</summary>
        public int MatchSize = MaxSlots;

        /// <summary>P7-UX (OC-46): a private room is active on this server (the host starts it; no auto countdown).</summary>
        public bool PrivateRoom;

        /// <summary>P7-UX: the room's bot mix (0 easy, 1 mixed = the public default, 2 hard).</summary>
        public int RoomBotMix = 1;

        /// <summary>P7-UX: each seated human's skill tier from their hello (0 Rookie, 1 Contender, 2 Champion).</summary>
        public readonly byte[] SkillTiers = new byte[MaxSlots];

        /// <summary>P7-UX: the lobby's tier = the most common tier among the connected humans (ties → the higher count's
        /// lower tier; nobody → Contender). It sets the bots' mix in a public match.</summary>
        public int LobbyTier
        {
            get
            {
                var n = new int[3];
                for (var i = 0; i < MaxSlots; i++)
                    if (_sim.Slots[i].Active && !_sim.Slots[i].Bot && !_sim.Slots[i].Detached)
                        n[Math.Min(2, (int)SkillTiers[i])]++;
                if (n[0] + n[1] + n[2] == 0)
                    return 1;
                var best = 1;
                for (var t = 0; t < 3; t++)
                    if (n[t] > n[best])
                        best = t;
                return best;
            }
        }

        /// <summary>The bot mix the roster lock uses.</summary>
        public int BotMix => PrivateRoom ? Math.Max(0, Math.Min(2, RoomBotMix)) : LobbyTier;
        /// <summary>Dev: play only these maps (one round each; the last round crowns 1). Null = the deal's four rounds.</summary>
        public byte[] PlanOverride;
        public int VariantOverride = -1, ModifierOverride = -1;
        /// <summary>The next match's 32-byte seed (M4: <c>-fallrushSeed</c> or random; M6: the VRF output).</summary>
        public Func<int, byte[]> SeedSource;

        // ---- state ----
        public MatchPhase Phase { get; private set; } = MatchPhase.LobbyOpen;
        public uint PhaseStartTick { get; private set; }
        public uint PhaseEndTick { get; private set; }
        public int Round { get; private set; }
        public int RoundCount { get; private set; }
        public readonly RosterSlot[] Roster = new RosterSlot[MaxSlots];
        public readonly byte[][] Tokens = new byte[MaxSlots][];
        public readonly string[] Names = new string[MaxSlots];
        /// <summary>G2.2 cosmetic preset per lobby slot (from hello).</summary>
        public readonly byte[] Presets = new byte[MaxSlots];
        public byte[] MatchSeed { get; private set; }
        public byte[] MatchId { get; private set; }
        public byte[] RosterHashBytes { get; private set; }
        public byte[] TranscriptHash { get; private set; }
        public byte[] TranscriptInit { get; private set; }
        public Deal.Result DealResult { get; private set; }
        public byte[] Targets { get; private set; } = new byte[4];
        public byte[] PlanMaps { get; private set; } = new byte[4];
        public int PlayerCount { get; private set; }
        public readonly List<MatchEvent> Events = new List<MatchEvent>();
        public int Champion { get; private set; } = -1;
        public IRoundRules Rules { get; private set; }
        public RoundParams CurrentParams { get; private set; }
        public RoundResult LastResult { get; private set; }
        public readonly List<RoundResult> Results = new List<RoundResult>();
        /// <summary>The last finished match's round results and champion (kept after the lobby reopens; tests, logs).</summary>
        public List<RoundResult> PreviousResults { get; private set; } = new List<RoundResult>();
        public int PreviousChampion { get; private set; } = -1;
        public int HostSlot { get; private set; } = -1;
        public int MatchSerial { get; private set; }
        public int MatchesCompleted { get; private set; }
        public int MatchesAborted { get; private set; }
        public AbortReason LastAbort { get; private set; }
        /// <summary>Changes on every map load (round or lobby); clients echo it in <c>ready</c>.</summary>
        public byte LoadSerial { get; private set; }
        public bool Loading { get; private set; }
        public uint MatchStartTick { get; private set; }

        private readonly bool[] _ready = new bool[MaxSlots];
        private readonly bool[] _back = new bool[MaxSlots];
        private uint _loadStart;
        private bool _roundBegun;
        private bool _hostStart;
        private bool _lobbyLoaded;
        private bool _protectLate;

        public MatchDirector(SimCore sim, IMatchHost host)
        {
            _sim = sim;
            _host = host;
        }

        public uint Tick => _sim.Tick;

        // =====================================================================
        // Connections (called by the host)
        // =====================================================================

        /// <summary>A human took <paramref name="slot"/> in the lobby (or as a viewer moved in). <paramref name="token"/> = their reconnect token.</summary>
        public void OnHumanJoined(int slot, byte[] token, string name, byte preset = 0, byte tier = 1)
        {
            Tokens[slot] = token;
            SkillTiers[slot] = (byte)Math.Min(2, (int)tier);
            Presets[slot] = CosmeticPresets.Clamp(preset);
            Names[slot] = string.IsNullOrEmpty(name) ? $"Player {slot + 1}" : name;
            UpdateHost();
            _host.OnMatchChanged();
        }

        /// <summary>True if the slot is in the current match roster (a disconnect then keeps it for the grace period).</summary>
        public bool InMatch => Phase != MatchPhase.LobbyOpen && Phase != MatchPhase.Countdown;

        /// <summary>
        /// A human's connection dropped. In the lobby the slot is freed at once (returns true: remove it from the sim). In a
        /// match the Rushling stays on neutral input; a reconnect within <see cref="GraceTicks"/> takes the slot back.
        /// </summary>
        public bool OnHumanLeft(int slot)
        {
            if (!InMatch || !Roster[slot].Occupied)
            {
                Tokens[slot] = null;
                SkillTiers[slot] = 1;
                Names[slot] = null;
                _ready[slot] = false;
                UpdateHost();
                _host.OnMatchChanged();
                return true;
            }

            Roster[slot].Connected = false;
            Roster[slot].DisconnectedAt = Math.Max(1, Tick);
            _host.OnMatchChanged();
            return false;
        }

        /// <summary>A reconnect with <paramref name="token"/>: the roster slot it belongs to (still disconnected), or −1.</summary>
        public int FindResumeSlot(ReadOnlySpan<byte> token)
        {
            if (token.Length != 16 || IsZero(token))
                return -1;
            for (var i = 0; i < MaxSlots; i++)
            {
                if (Tokens[i] == null || !Roster[i].Occupied || Roster[i].Bot || Roster[i].Connected)
                    continue;
                if (token.SequenceEqual(Tokens[i]))
                    return i;
            }

            return -1;
        }

        /// <summary>The reconnect took the slot back (same Rushling; still alive unless the grace had already expired).</summary>
        public void OnHumanResumed(int slot)
        {
            Roster[slot].Connected = true;
            Roster[slot].DisconnectedAt = 0;
            _ready[slot] = false; // must load the current map again
            _host.OnMatchChanged();
        }

        private static bool IsZero(ReadOnlySpan<byte> b)
        {
            foreach (var x in b)
                if (x != 0)
                    return false;
            return true;
        }

        /// <summary><c>ready</c>: the client loaded the map of load <paramref name="serial"/> and its map hash matched.</summary>
        public void MarkReady(int slot, byte serial)
        {
            if (slot >= 0 && slot < MaxSlots && serial == LoadSerial)
                _ready[slot] = true;
        }

        public bool IsReady(int slot) => _ready[slot];

        /// <summary><c>start</c> from the host (the first human): skip the rest of the lobby countdown.</summary>
        public bool HostStart(int slot)
        {
            if (slot != HostSlot || (Phase != MatchPhase.Countdown && Phase != MatchPhase.LobbyOpen))
                return false;
            _hostStart = true;
            return true;
        }

        /// <summary><c>back</c>: this human dismissed the receipt.</summary>
        public void Back(int slot)
        {
            if (slot >= 0 && slot < MaxSlots)
                _back[slot] = true;
        }

        private void UpdateHost()
        {
            var prev = HostSlot;
            HostSlot = -1;
            for (var i = 0; i < MaxSlots; i++)
            {
                if (!_sim.Slots[i].Active || _sim.Slots[i].Bot || _sim.Slots[i].Detached)
                    continue;
                HostSlot = i;
                break;
            }

            if (prev != HostSlot && Phase <= MatchPhase.Countdown)
                _hostStart = false;
        }

        // =====================================================================
        // The tick (before SimCore.Step)
        // =====================================================================

        /// <summary>Starts the director: loads the lobby.</summary>
        public void Start()
        {
            EnterLobby();
        }

        public void PreStep()
        {
            var tick = Tick;
            if (InMatch && Phase != MatchPhase.Aborted && Phase < MatchPhase.Finalizing)
            {
                CheckGrace(tick);
                if (!Soak && NoHumansLeft())
                {
                    Abort(AbortReason.NoHumans);
                    return;
                }
            }

            switch (Phase)
            {
                case MatchPhase.LobbyOpen:
                    if (!_lobbyLoaded)
                    {
                        if (!_host.MapLoaded)
                            return;
                        _lobbyLoaded = true;
                        SetRules(_host.CreateRules(MapPoolV1.Lobby));
                        _host.OnMatchChanged();
                    }

                    // P7-UX: a private room waits for its host's Start (friends arrive over minutes, not 20 s)
                    if (PrivateRoom && !Soak)
                    {
                        if (_hostStart && HumansConnected() > 0)
                            LockRoster(tick);
                        break;
                    }

                    if (HumansConnected() > 0 || Soak)
                        SetPhase(MatchPhase.Countdown, tick, (uint)CountdownTicks);
                    break;

                case MatchPhase.Countdown:
                    if (HumansConnected() == 0 && !Soak)
                    {
                        SetPhase(MatchPhase.LobbyOpen, tick, 0);
                        break;
                    }

                    if (_hostStart || tick >= PhaseEndTick)
                        LockRoster(tick);
                    break;

                case MatchPhase.RosterLocked:
                    SetPhase(MatchPhase.Dealing, tick, DealingTicks);
                    break;

                case MatchPhase.Dealing:
                    if (tick >= PhaseEndTick)
                    {
                        SetPhase(MatchPhase.Dealt, tick, DealtTicks);
                        Emit(Transcript.Deal, 0, Transcript.NoSlot, MatchSeed);
                    }

                    break;

                case MatchPhase.Dealt:
                    if (tick >= PhaseEndTick)
                        EnterRoundIntro(1, tick);
                    break;

                case MatchPhase.RoundIntro:
                    if (!_roundBegun)
                    {
                        if (_host.MapLoaded && (AllPlayersReady() || tick - _loadStart >= LoadTimeoutTicks))
                            BeginRound(tick);
                    }
                    else if (Rules.Phase == RoundPhase.Rush)
                    {
                        SetPhase(MatchPhase.RoundActive, tick, Rules.TimerTicks > 0 ? Rules.RushTick + Rules.TimerTicks - tick : 0);
                    }
                    else if (Rules.IsOver)
                    {
                        EnterRoundResult(tick); // ended before RUSH (nobody left)
                    }

                    break;

                case MatchPhase.RoundActive:
                    if (Rules.IsOver)
                        EnterRoundResult(tick);
                    break;

                case MatchPhase.RoundResult:
                    if (tick >= PhaseEndTick)
                    {
                        var alive = AliveCount(out var last);
                        if (alive <= 1 || Round >= RoundCount)
                            EnterChampion(alive == 1 ? last : FirstQualified(), tick);
                        else
                            SetPhase(MatchPhase.Intermission, tick, IntermissionTicks);
                    }

                    break;

                case MatchPhase.Intermission:
                    if (tick >= PhaseEndTick)
                        EnterRoundIntro(Round + 1, tick);
                    break;

                case MatchPhase.Champion:
                    if (tick >= PhaseEndTick)
                        SetPhase(MatchPhase.Finalizing, tick, 1); // TODO(M6): commit_and_undelegate (finalize_match), wait for Solana
                    break;

                case MatchPhase.Finalizing:
                    if (tick >= PhaseEndTick)
                    {
                        Array.Clear(_back, 0, _back.Length);
                        SetPhase(MatchPhase.Done, tick, DoneTicks);
                    }

                    break;

                case MatchPhase.Done:
                    if (tick >= PhaseEndTick || AllHumansBack())
                    {
                        MatchesCompleted++;
                        EnterLobby();
                    }

                    break;

                case MatchPhase.Aborted:
                    if (tick >= PhaseEndTick)
                        EnterLobby();
                    break;
            }
        }

        /// <summary>After <see cref="SimCore.Step"/>: nothing yet (rules end themselves in EndTick; the next Tick_ reacts).</summary>
        public void AfterStep()
        {
        }

        // =====================================================================
        // Phases
        // =====================================================================

        private void SetPhase(MatchPhase p, uint tick, uint duration)
        {
            Phase = p;
            PhaseStartTick = tick;
            PhaseEndTick = duration > 0 ? tick + duration : 0;
            _host.OnMatchChanged();
        }

        private void SetPhase(MatchPhase p, uint tick, int duration) => SetPhase(p, tick, (uint)Math.Max(0, duration));

        private void SetRules(IRoundRules rules)
        {
            Rules = rules;
            _sim.Rules = rules;
        }

        private void EnterLobby()
        {
            var tick = Tick;
            // Bots leave; disconnected humans' slots are freed (the next match has a fresh roster).
            for (var i = 0; i < MaxSlots; i++)
            {
                ref var s = ref _sim.Slots[i];
                if (!s.Active)
                    continue;
                if (s.Bot || s.Detached)
                {
                    _sim.Remove(i);
                    if (!s.Bot)
                    {
                        Tokens[i] = null;
                        Names[i] = null;
                    }
                }
            }

            if (Results.Count > 0)
            {
                PreviousResults = new List<RoundResult>(Results);
                PreviousChampion = Champion;
            }

            Array.Clear(Roster, 0, Roster.Length);
            Array.Clear(_ready, 0, _ready.Length);
            Events.Clear();
            Results.Clear();
            Champion = -1;
            Round = 0;
            RoundCount = 0;
            LastResult = null;
            MatchId = null;
            MatchSeed = null;
            RosterHashBytes = null;
            TranscriptHash = null;
            PlayerCount = 0;
            _hostStart = false;
            _roundBegun = false;
            _lobbyLoaded = false;
            _sim.BotInput = null;
            UpdateHost();
            LoadSerial++;
            Loading = true;
            var p = new RoundParams { MapId = MapPoolV1.Lobby, RoundIndex = 0 };
            CurrentParams = p;
            _host.LoadMap(MapPoolV1.Lobby, in p);
            SetPhase(MatchPhase.LobbyOpen, tick, 0);
        }

        private void LockRoster(uint tick)
        {
            MatchSerial++;
            var size = Math.Max(RoundTargets.MinPlayers, Math.Min(MaxSlots, MatchSize));
            var mix = BotMix;
            for (var i = 0; i < MaxSlots; i++)
                if (_sim.Slots[i].Active && !_sim.Slots[i].Bot && i >= size)
                    size = i + 1;
            // Bots fill every free slot below the match size, so the roster is slots 0…P−1 (the program's layout).
            for (var i = 0; i < size; i++)
                if (!_sim.Slots[i].Active)
                    _sim.AddBotAt(i);
            PlayerCount = size;

            MatchSeed = SeedSource != null ? SeedSource(MatchSerial) : new byte[32];
            if (MatchSeed == null || MatchSeed.Length != 32)
                throw new InvalidOperationException("match seed must be 32 bytes");
            MatchId = new byte[16];
            Array.Copy(Sha256Of(Encoding.ASCII.GetBytes("fallrush-match-id"), MatchSeed), MatchId, 16);

            var entries = new List<RosterHash.Entry>();
            for (var i = 0; i < size; i++)
            {
                var bot = _sim.Slots[i].Bot;
                Roster[i] = new RosterSlot
                {
                    Occupied = true, Bot = bot, Alive = true, Connected = true, Places = new byte[4], Late = new bool[4],
                    Name = bot ? BotNames[i % BotNames.Length] : Names[i] ?? $"Player {i + 1}",
                    Tier = bot ? TierFor(MatchSeed, i, mix) : BotTier.Reference,
                    Preset = bot ? CosmeticPresets.ForBot(i) : Presets[i],
                };
                entries.Add(new RosterHash.Entry { Slot = (byte)i, Bot = bot, UserId = RosterHash.PlaceholderUserId(i, bot) }); // TODO(M5): real guest ids
            }

            RosterHashBytes = RosterHash.Compute(entries);
            Targets = RoundTargets.For(size);
            TranscriptInit = Transcript.Init(MatchId, RosterHashBytes);
            TranscriptHash = TranscriptInit;
            Events.Clear();
            Results.Clear();
            DealResult = Deal.Derive(MatchSeed);
            if (PlanOverride != null && PlanOverride.Length > 0)
            {
                RoundCount = Math.Min(4, PlanOverride.Length);
                PlanMaps = new byte[4];
                Array.Copy(PlanOverride, PlanMaps, RoundCount);
            }
            else
            {
                RoundCount = Deal.Rounds;
                PlanMaps = (byte[])DealResult.Maps.Clone();
            }

            if (RoundCount < Deal.Rounds)
                Targets[RoundCount - 1] = 1; // a shortened (dev) match still ends with one champion
            MatchStartTick = tick;
            SetPhase(MatchPhase.RosterLocked, tick, 1);
        }

        /// <summary>Bot skill tier per slot from the match seed: 30 % easy, 50 % normal, 20 % good (M4 §3.5).</summary>
        public static BotTier TierFor(byte[] seed, int slot) => TierFor(seed, slot, 1);

        /// <summary>
        /// P7-UX (OC-47): the bot tier for a lobby's mix, still a pure function of the seed. Mix 1 (Contender, the public
        /// default) is exactly the M4 rule; mix 0 (Rookie) 60 / 35 / 5 %, mix 2 (Champion) 10 / 45 / 45 % easy / normal / good.
        /// </summary>
        public static BotTier TierFor(byte[] seed, int slot, int mix)
        {
            var h = Sha256Of(Encoding.ASCII.GetBytes("fallrush-bot-tier"), seed, new[] { (byte)slot });
            if (mix == 1 || mix < 0 || mix > 2)
            {
                var x = h[0] % 10;
                return x < 3 ? BotTier.Easy : x < 8 ? BotTier.Normal : BotTier.Good;
            }

            var y = h[0] % 20;
            return mix == 0 ? (y < 12 ? BotTier.Easy : y < 19 ? BotTier.Normal : BotTier.Good)
                : (y < 2 ? BotTier.Easy : y < 11 ? BotTier.Normal : BotTier.Good);
        }

        /// <summary>The <see cref="RoundParams"/> of round <paramref name="r"/> (1-based) from the deal.</summary>
        public RoundParams ParamsFor(int r)
        {
            var i = r - 1;
            var map = PlanMaps[i];
            var dealIndex = map < MapPoolV1.Keys.Length ? i : 0;
            var p = new RoundParams
            {
                MapId = map,
                Variant = (byte)(VariantOverride >= 0 ? VariantOverride % Math.Max(1, MapPoolV1.VariantCount(map)) : DealResult.Variants[dealIndex] % Math.Max(1, MapPoolV1.VariantCount(map))),
                Modifier = DealResult.Modifiers[dealIndex],
                RoundIndex = (byte)r,
                HazardSeed = Deal.HazardSeed(MatchSeed, r),
                QualifyCount = Targets[i],
                PlayerCount = (byte)AliveCount(out _),
            };
            if (PlanOverride != null && !MapPoolV1.ModifierAllowed(map, p.Modifier))
                p.Modifier = 0; // a dev plan maps the deal's round-i modifier onto another map
            if (ModifierOverride >= 0 && MapPoolV1.ModifierAllowed(map, ModifierOverride))
                p.Modifier = (byte)ModifierOverride;
            return p;
        }

        private void EnterRoundIntro(int r, uint tick)
        {
            Round = r;
            _roundBegun = false;
            Array.Clear(_ready, 0, _ready.Length);
            LoadSerial++;
            Loading = true;
            _loadStart = tick;
            var p = ParamsFor(r);
            CurrentParams = p;
            SetRules(null);
            _sim.BotInput = null;
            _host.LoadMap(p.MapId, in p);
            SetPhase(MatchPhase.RoundIntro, tick, 0);
        }

        private bool AllPlayersReady()
        {
            for (var i = 0; i < MaxSlots; i++)
            {
                ref readonly var s = ref Roster[i];
                if (s.Occupied && !s.Bot && s.Alive && s.Connected && !_ready[i])
                    return false;
            }

            return true;
        }

        private void BeginRound(uint tick)
        {
            Loading = false;
            var r = Round;
            var target = Targets[r - 1];
            var participants = new bool[MaxSlots];
            var late = 0;
            var players = 0;
            for (var i = 0; i < MaxSlots; i++)
            {
                ref var s = ref Roster[i];
                if (!s.Occupied || !s.Alive)
                    continue;
                if (!s.Bot && s.Connected && !_ready[i])
                {
                    s.Late[r - 1] = true; // M4-3: missed the 10 s load timeout → spectates this round
                    late++;
                    continue;
                }

                participants[i] = true;
                players++;
            }

            // Late loaders are protected (they advance) if the round can still qualify at least one racer without them;
            // otherwise (e.g. the final, target 1) they are ranked last. Either way the qualified count stays ≤ the target.
            _protectLate = late > 0 && target - late >= 1;
            var rulesTarget = _protectLate ? target - late : target;

            var rules = _host.CreateRules(CurrentParams.MapId);
            rules.Target = Math.Max(1, Math.Min(rulesTarget, Math.Max(1, players)));
            rules.Participants = participants;
            rules.IntroTicks = RoundIntroTicks;
            rules.CountdownTicks = RoundCountdownTicks;
            SetRules(rules);
            var p = CurrentParams;
            p.PlayerCount = (byte)players;
            p.QualifyCount = (byte)target;
            var rush = rules.Begin(_sim, tick);
            p.RoundStartTick = rush;
            p.IntroTicks = (ushort)rules.IntroTicks;
            p.CountdownTicks = (ushort)rules.CountdownTicks;
            p.TimerTicks = rules.TimerTicks;
            CurrentParams = p;
            if (_sim.Hazards != null)
                _sim.Hazards.RoundStartTick = rush;
            _sim.BotInput = _host.CreateBotInput(rules, Roster, in p);
            _roundBegun = true;
            _host.OnRoundStarted(in p);
            Emit(Transcript.StartRound, (byte)r, Transcript.NoSlot, Array.Empty<byte>());
            _host.OnMatchChanged();
        }

        private void EnterRoundResult(uint tick)
        {
            var r = Round;
            var res = Rules.RoundOutcome ?? new RoundResult { Mode = Rules.Mode };
            var order = new List<int>(res.Order);
            var qualified = new List<bool>(res.Qualified);
            var reasons = new List<ElimReason>(res.Reasons);
            var values = new List<int>(res.Value);

            // Late loaders (not in the rules' ranking) go after everyone who played.
            for (var i = 0; i < MaxSlots; i++)
            {
                if (!Roster[i].Occupied || !Roster[i].Alive || !Roster[i].Late[r - 1] || order.Contains(i))
                    continue;
                order.Add(i);
                qualified.Add(_protectLate);
                reasons.Add(ElimReason.NotQualified);
                values.Add(0);
            }

            // Anyone still alive that the rules did not rank (should not happen): last, eliminated.
            for (var i = 0; i < MaxSlots; i++)
            {
                if (!Roster[i].Occupied || !Roster[i].Alive || order.Contains(i))
                    continue;
                order.Add(i);
                qualified.Add(false);
                reasons.Add(ElimReason.NotQualified);
                values.Add(0);
            }

            // M4-6: disconnected for longer than the grace → eliminated (DISCONNECT) at the end of the round.
            for (var k = 0; k < order.Count; k++)
            {
                if (!Roster[order[k]].GraceExpired)
                    continue;
                qualified[k] = false;
                reasons[k] = ElimReason.Disconnect;
            }

            var target = Targets[r - 1];
            var count = 0;
            for (var k = 0; k < order.Count; k++)
            {
                if (!qualified[k])
                    continue;
                if (count >= target)
                {
                    qualified[k] = false; // never more than the target (the program refuses it)
                    reasons[k] = ElimReason.TimeoutRank;
                    continue;
                }

                count++;
            }

            if (count == 0)
            {
                // M4-8: nobody survived → the highest placed (still connected or in grace) advances as the sole survivor.
                for (var k = 0; k < order.Count; k++)
                {
                    if (Roster[order[k]].GraceExpired)
                        continue;
                    qualified[k] = true;
                    reasons[k] = ElimReason.NotQualified;
                    count = 1;
                    break;
                }
            }

            var final = RoundResult.From(order, qualified, reasons, values, target, res.Mode, res.StartTick, res.EndTick, res.TimerExpired);
            LastResult = final;
            Results.Add(final);
            if (count == 0)
            {
                Abort(AbortReason.NoPlayers);
                return;
            }

            // Program order: QUALIFIED in placement order, then ELIMINATED, then COMPLETE_ROUND.
            var placement = 0;
            for (var k = 0; k < order.Count; k++)
            {
                var slot = order[k];
                Roster[slot].Places[r - 1] = (byte)(k + 1);
                if (!qualified[k])
                    continue;
                placement++;
                Emit(Transcript.Qualified, (byte)r, (byte)slot, new[] { (byte)placement });
            }

            for (var k = 0; k < order.Count; k++)
            {
                if (qualified[k])
                    continue;
                var slot = order[k];
                Roster[slot].Alive = false;
                Roster[slot].EliminatedRound = (byte)r;
                Roster[slot].Reason = reasons[k];
                Emit(Transcript.Eliminated, (byte)r, (byte)slot, new[] { (byte)reasons[k] });
            }

            Emit(Transcript.CompleteRound, (byte)r, Transcript.NoSlot, new[] { (byte)count });
            SetPhase(MatchPhase.RoundResult, tick, ResultTicks);
        }

        private int FirstQualified()
        {
            if (LastResult != null)
                for (var k = 0; k < LastResult.Order.Length; k++)
                    if (LastResult.Qualified[k])
                        return LastResult.Order[k];
            return -1;
        }

        private void EnterChampion(int slot, uint tick)
        {
            Champion = slot;
            // A longer final ranking than one (defensive; the last round's target is 1): the champion is the only alive slot.
            for (var i = 0; i < MaxSlots; i++)
                if (Roster[i].Occupied && Roster[i].Alive && i != slot)
                {
                    Roster[i].Alive = false;
                    Roster[i].EliminatedRound = (byte)Round;
                    Roster[i].Reason = ElimReason.TimeoutRank;
                }

            Emit(Transcript.CompleteMatch, (byte)Round, (byte)slot, Array.Empty<byte>());
            SetPhase(MatchPhase.Champion, tick, ChampionTicks);
        }

        /// <summary>ABORT (any phase after the roster lock): the event consumes the current seq (doc 06 §9).</summary>
        public void Abort(AbortReason reason)
        {
            var tick = Tick;
            LastAbort = reason;
            MatchesAborted++;
            if (MatchId != null && TranscriptHash != null)
                Emit(Transcript.Abort, (byte)Round, Transcript.NoSlot, new[] { (byte)reason });
            Loading = false;
            SetPhase(MatchPhase.Aborted, tick, AbortTicks);
        }

        // =====================================================================
        // Helpers
        // =====================================================================

        private void Emit(byte tag, byte round, byte slot, byte[] arg)
        {
            var seq = (uint)Events.Count;
            TranscriptHash = Transcript.Next(TranscriptHash, tag, seq, round, slot, arg);
            var e = new MatchEvent { Seq = seq, Tag = tag, Round = round, Slot = slot, Arg = arg, Tick = Tick, TranscriptAfter = TranscriptHash };
            Events.Add(e);
            _host.OnEvent(in e);
        }

        private void CheckGrace(uint tick)
        {
            for (var i = 0; i < MaxSlots; i++)
            {
                ref var s = ref Roster[i];
                if (!s.Occupied || s.Bot || s.Connected || s.GraceExpired || s.DisconnectedAt == 0)
                    continue;
                if (tick - s.DisconnectedAt > GraceTicks)
                {
                    s.GraceExpired = true;
                    _host.OnMatchChanged();
                }
            }
        }

        private bool NoHumansLeft()
        {
            var any = false;
            for (var i = 0; i < MaxSlots; i++)
            {
                ref readonly var s = ref Roster[i];
                if (!s.Occupied || s.Bot)
                    continue;
                any = true;
                if (s.Connected || !s.GraceExpired)
                    return false;
            }

            // A practice match that never had a human (soak) is not aborted here.
            return any;
        }

        public int HumansConnected()
        {
            var n = 0;
            for (var i = 0; i < MaxSlots; i++)
                if (_sim.Slots[i].Active && !_sim.Slots[i].Bot && !_sim.Slots[i].Detached)
                    n++;
            return n;
        }

        private bool AllHumansBack()
        {
            var any = false;
            for (var i = 0; i < MaxSlots; i++)
            {
                if (!_sim.Slots[i].Active || _sim.Slots[i].Bot || _sim.Slots[i].Detached)
                    continue;
                any = true;
                if (!_back[i])
                    return false;
            }

            return any;
        }

        public int AliveCount(out int last)
        {
            var n = 0;
            last = -1;
            for (var i = 0; i < MaxSlots; i++)
                if (Roster[i].Occupied && Roster[i].Alive)
                {
                    n++;
                    last = i;
                }

            return n;
        }

        internal static byte[] Sha256Of(params byte[][] parts)
        {
            var h = new Sha256();
            foreach (var p in parts)
                h.Update(p);
            return h.Final();
        }
    }
}
