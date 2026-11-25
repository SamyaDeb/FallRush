using System;
using System.Collections.Generic;
using FallRush.Net;
using FallRush.Shared;

namespace FallRush.OnChain
{
    /// <summary>
    /// P3-D: turns arena Worlds into the structures the Classic screens already read (<see cref="ClientSim"/> feed):
    /// snapshots (<see cref="PlayerState"/>), <see cref="RoundParams"/>, <see cref="RaceStateMessage"/>, round events
    /// (from World diffs: phase, OUT, the result) and a <see cref="MatchStateMessage"/> for the Final (round 4 of 4:
    /// intro → active → result 6 s → champion 8 s → receipt). Pure (no Unity objects); tested in EditMode.
    /// <para>fullchain (<see cref="Full"/>): a whole match on the arena (<c>start_sim</c>, World v2). The match state comes
    /// from the World's director (<see cref="FallRush.Shared.ChainDirector"/>): phase, round 1–4, deal, targets, roster,
    /// alive / eliminated per round, champion; per-round qualify / eliminate events; finished racers. Off = the Final
    /// path below, unchanged.</para>
    /// </summary>
    public sealed class OnChainMatchFeed
    {
        public const float ResultSeconds = 6f, ChampionSeconds = 8f;

        public readonly PlayerState[] Players = new PlayerState[12];
        public int PlayerCount;
        public RoundParams Round;
        public bool HasRound;
        public RaceStateMessage Race;
        public readonly List<RaceEvent> NewEvents = new List<RaceEvent>();
        public MatchStateMessage Match;

        /// <summary>Roster names / kinds by slot (join reply, or the Classic match in the switch).</summary>
        public readonly string[] Names = new string[12];
        public readonly bool[] Bots = new bool[12];
        /// <summary>Optional: the Classic match this Final continues (rounds 1–3 places, names, ids); null = standalone.</summary>
        public MatchStateMessage Base;
        public int OwnSlot = 255;
        public byte MatchSerial = 200;
        /// <summary>fullchain: the World runs the whole match (rounds 1–4 from its director).</summary>
        public bool Full;
        /// <summary>fullchain: places per slot per round (1-based; 0 = not ranked), from each round's result.</summary>
        private readonly byte[][] _places = new byte[12][];
        private uint _doneMask, _qualMask;

        private byte _lastPhase = 255;
        private uint _outMask;
        private bool _resultSent;
        private uint _seq;
        private double _endedAt = double.NaN;
        private uint _roundKey;

        public void Reset()
        {
            _lastPhase = 255;
            _outMask = 0;
            _resultSent = false;
            _endedAt = double.NaN;
            _doneMask = _qualMask = 0;
            for (var s = 0; s < 12; s++)
                _places[s] = null;
            HasRound = false;
            NewEvents.Clear();
            MatchSerial++;
        }

        private static short Vel(int mmPerS) => (short)Math.Max(short.MinValue, Math.Min(short.MaxValue, mmPerS / 10)); // mm/s → cm/s

        /// <summary>Reads one World (call for every World in order). <paramref name="now"/> = ClientSim.Now.</summary>
        public void OnWorld(in WorldView w, double now)
        {
            NewEvents.Clear();
            var phase = w.Phase;
            var key = w.RushTick ^ (uint)w.HazardSeed;
            if (key != _roundKey && (phase == WorldView.PhaseIntro || phase == WorldView.PhaseCountdown))
            {
                _roundKey = key;
                _outMask = 0;
                _doneMask = _qualMask = 0;
                _resultSent = false;
                _endedAt = double.NaN;
            }

            var dir = Full ? w.Director : default;
            var dirOn = Full && dir.Valid && dir.Running && dir.Round >= 1;

            // ---- players (out players leave the snapshot: they spectate, as in a Classic Final)
            PlayerCount = 0;
            var free = phase == WorldView.PhaseSandbox || phase == WorldView.PhaseWaiting;
            for (var s = 0; s < 12; s++)
            {
                if (!w.Active(s))
                    continue;
                var inRound = free || w.InRound(s);
                var outNow = !free && w.Out(s);
                if (!inRound || outNow)
                    continue;
                if (free && dirOn && dir.InRoster(s) && !dir.Alive(s))
                    continue; // fullchain: eliminated in an earlier round (still active in the World): spectates
                byte race = 0;
                if (Full)
                    race = RaceByte(in w, s);
                else if (phase == WorldView.PhaseEnded && w.HasResult)
                    race = w.Champion == s ? PlayerState.RaceQualified : PlayerState.RaceEliminated;
                Players[PlayerCount++] = new PlayerState
                {
                    Slot = (byte)s,
                    PosX = w.PosX(s), PosY = w.PosY(s), PosZ = w.PosZ(s),
                    VelX = Vel(w.VelX(s)), VelY = Vel(w.VelY(s)), VelZ = Vel(w.VelZ(s)),
                    Yaw = w.Yaw(s),
                    Flags = (byte)(w.MotorFlags(s) | (w.IsBot(s) ? MotorState.Bot : 0)),
                    StunTicks = w.Stun(s),
                    Race = race,
                };
            }

            // ---- round
            HasRound = !free;
            if (HasRound)
            {
                Round = new RoundParams
                {
                    MapId = w.MapKey,
                    Variant = w.Variant,
                    Modifier = w.Modifier,
                    RoundIndex = Full ? w.RoundIndex : (byte)4,
                    HazardSeed = w.HazardSeed,
                    RoundStartTick = w.RoundStart,
                    QualifyCount = w.QualifyCount,
                    PlayerCount = (byte)Popcount(w.ActiveMask),
                    IntroTicks = w.IntroTicks,
                    CountdownTicks = w.CountdownTicks,
                    TimerTicks = w.RushEnd > w.RushTick ? w.RushEnd - w.RushTick : 0,
                };
            }

            // ---- events from diffs
            if (phase != _lastPhase && HasRound)
                Emit(RaceEventType.Phase, w.Tick, 255, phase);
            _lastPhase = phase;
            for (var s = 0; s < 12; s++)
            {
                if (!w.Active(s) || free || !w.Out(s) || (_outMask >> s & 1) != 0)
                    continue;
                _outMask |= 1u << s;
                Emit(RaceEventType.Out, w.OutTick(s), (byte)s, w.OutReason(s));
            }

            if (Full && !free)
            {
                // fullchain: finished racers (Capsule Run / Star Fountain DONE) and early qualifiers, as Classic emits them
                for (var s = 0; s < 12; s++)
                {
                    if (!w.Active(s) || !w.InRound(s))
                        continue;
                    if (w.Done(s) && (_doneMask >> s & 1) == 0)
                    {
                        _doneMask |= 1u << s;
                        Emit(RaceEventType.Finished, w.Tick, (byte)s, w.Place(s));
                    }

                    if (w.SlotQualifiedFlag(s) && (_qualMask >> s & 1) == 0 && !(phase == WorldView.PhaseEnded && w.HasResult))
                    {
                        _qualMask |= 1u << s;
                        Emit(RaceEventType.Qualified, w.Tick, (byte)s, Popcount(_qualMask));
                    }
                }
            }

            if (Full && phase == WorldView.PhaseEnded && w.HasResult && !_resultSent)
            {
                _resultSent = true;
                _endedAt = now;
                var q = 0;
                for (var k = 0; k < w.ResultCount && k < 12; k++)
                {
                    var s = w.ResultOrder(k);
                    if (s >= 12)
                        continue;
                    if (w.RoundIndex >= 1 && w.RoundIndex <= 4)
                        (_places[s] ??= new byte[4])[w.RoundIndex - 1] = (byte)(k + 1);
                    if (w.ResultQualifiedAt(k))
                    {
                        q++;
                        if ((_qualMask >> s & 1) == 0)
                        {
                            _qualMask |= 1u << s;
                            Emit(RaceEventType.Qualified, w.Tick, s, Popcount(_qualMask));
                        }
                    }
                    else
                        Emit(RaceEventType.Eliminated, w.Tick, s, k + 1);
                }

                Emit(RaceEventType.RoundEnd, w.Tick, 255, q);
            }
            else if (phase == WorldView.PhaseEnded && w.HasResult && !_resultSent)
            {
                _resultSent = true;
                _endedAt = now;
                for (var k = 0; k < w.ResultCount; k++)
                {
                    var s = w.ResultOrder(k);
                    if (s >= 12)
                        continue;
                    Emit(k == 0 ? RaceEventType.Qualified : RaceEventType.Eliminated, w.Tick, s, k + 1);
                }

                Emit(RaceEventType.RoundEnd, w.Tick, 255, 1);
            }

            // Final: the Classic rstate carries the alive count in FinisherCount (MatchHud "ALIVE n").
            var alive = new List<byte>(12);
            if (w.Mode == WorldView.ModeFinal)
                for (var s = 0; s < 12; s++)
                    if (w.Active(s) && w.InRound(s) && !w.Out(s))
                        alive.Add((byte)s);
            Race = new RaceStateMessage
            {
                LastEventSeq = _seq,
                Phase = (RoundPhase)(phase <= 5 ? phase : 0),
                PhaseStartTick = w.PhaseStart,
                RushEndTick = w.RushEnd,
                QualifyCount = w.QualifyCount,
                QualifiedCount = w.QualifiedCount,
                FinisherCount = (byte)alive.Count,
                FinisherSlots = alive.ToArray(),
                FinisherTicks = new uint[alive.Count],
            };

            Match = BuildMatch(in w, now);
        }

        /// <summary>fullchain: the Classic race byte (checkpoint + finished / qualified / eliminated) from the World's
        /// slot rules; at the round's result, its qualify bit by rank.</summary>
        public static byte RaceByte(in WorldView w, int s)
        {
            var b = (byte)(Math.Min(15, (int)w.Checkpoint(s)) & PlayerState.RaceCheckpointMask);
            if (w.Done(s))
                b |= PlayerState.RaceFinished;
            var q = w.SlotQualifiedFlag(s);
            var e = w.SlotEliminatedFlag(s);
            if (w.Phase == WorldView.PhaseEnded && w.HasResult)
                for (var k = 0; k < w.ResultCount && k < 12; k++)
                    if (w.ResultOrder(k) == s)
                    {
                        q = w.ResultQualifiedAt(k);
                        e = !q;
                    }

            if (q)
                b |= PlayerState.RaceQualified;
            if (e)
                b |= PlayerState.RaceEliminated;
            return b;
        }

        private void Emit(RaceEventType t, uint tick, byte slot, int arg) =>
            NewEvents.Add(new RaceEvent { Seq = ++_seq, Type = t, Tick = tick, Slot = slot, Arg = arg });

        private static int Popcount(uint m)
        {
            var c = 0;
            for (; m != 0; m &= m - 1)
                c++;
            return c;
        }

        /// <summary>The match phase now: the World's round phase, then the result / champion / receipt timeline.</summary>
        public MatchPhase PhaseAt(in WorldView w, double now)
        {
            if (Full)
            {
                var d = w.Director;
                if (d.Valid && d.Phase <= ChainDirector.Aborted)
                    return (MatchPhase)d.Phase; // rush-core director::phase = MatchPhase, 0…12
            }

            switch (w.Phase)
            {
                case WorldView.PhaseRush:
                    return MatchPhase.RoundActive;
                case WorldView.PhaseEnded:
                    var t = double.IsNaN(_endedAt) ? 0 : now - _endedAt;
                    return t < ResultSeconds ? MatchPhase.RoundResult : t < ResultSeconds + ChampionSeconds ? MatchPhase.Champion : MatchPhase.Done;
                default:
                    return MatchPhase.RoundIntro;
            }
        }

        private MatchStateMessage BuildMatch(in WorldView w, double now)
        {
            if (Full && w.Director.Valid)
                return BuildFullMatch(in w, now);
            var b = Base;
            var m = new MatchStateMessage
            {
                Phase = PhaseAt(in w, now),
                PhaseStartTick = w.PhaseStart,
                PhaseEndTick = 0,
                ServerTick = w.Tick,
                Round = 4,
                RoundCount = 4,
                LoadSerial = b?.LoadSerial ?? 0,
                Flags = 0,
                HostSlot = 255,
                OwnSlot = (byte)OwnSlot,
                Champion = w.Phase == WorldView.PhaseEnded ? w.Champion : (byte)255,
                MatchSerial = b?.MatchSerial ?? MatchSerial,
            };
            if (b != null)
            {
                Buffer.BlockCopy(b.MatchId, 0, m.MatchId, 0, 16);
                Buffer.BlockCopy(b.Seed, 0, m.Seed, 0, 32);
                Buffer.BlockCopy(b.Maps, 0, m.Maps, 0, 4);
                Buffer.BlockCopy(b.Variants, 0, m.Variants, 0, 4);
                Buffer.BlockCopy(b.Modifiers, 0, m.Modifiers, 0, 4);
                Buffer.BlockCopy(b.Targets, 0, m.Targets, 0, 4);
                m.HostSlot = b.HostSlot;
                m.Transcript = b.Transcript;
                m.EventCount = b.EventCount;
            }
            else
            {
                var seed = BitConverter.GetBytes(w.HazardSeed);
                Buffer.BlockCopy(seed, 0, m.Seed, 0, 8);
                Buffer.BlockCopy(seed, 0, m.MatchId, 0, 8);
            }

            m.Maps[3] = w.MapKey;
            m.Variants[3] = w.Variant;
            m.Modifiers[3] = w.Modifier;
            m.Targets[3] = 1;

            var slots = new List<MatchSlotInfo>(12);
            for (var s = 0; s < 12; s++)
            {
                MatchSlotInfo bs = default;
                var inBase = false;
                if (b != null)
                    foreach (var x in b.Slots)
                        if (x.Slot == s)
                        {
                            bs = x;
                            inBase = true;
                        }

                if (!w.Active(s) && !inBase)
                    continue;
                var info = new MatchSlotInfo
                {
                    Slot = (byte)s,
                    Name = inBase ? bs.Name : Names[s] ?? (w.IsBot(s) ? $"Bot {s}" : $"Player {s}"),
                    Places = inBase && bs.Places != null ? (byte[])bs.Places.Clone() : new byte[4],
                    Preset = inBase ? bs.Preset : (byte)s,
                    Tier = inBase ? bs.Tier : default,
                    Reason = inBase ? bs.Reason : default,
                    EliminatedRound = inBase ? bs.EliminatedRound : (byte)0,
                };
                var bot = w.Active(s) ? w.IsBot(s) : bs.Has(MatchSlotInfo.Bot);
                byte f = MatchSlotInfo.Active | MatchSlotInfo.Rostered | MatchSlotInfo.Connected;
                if (bot)
                    f |= MatchSlotInfo.Bot;
                var alive = w.Active(s) && (w.Phase != WorldView.PhaseEnded || w.Champion == s) && !w.Out(s);
                if (alive)
                    f |= MatchSlotInfo.Alive;
                if (w.Active(s) && w.Phase == WorldView.PhaseEnded && w.HasResult)
                {
                    var rank = -1;
                    for (var k = 0; k < w.ResultCount; k++)
                        if (w.ResultOrder(k) == s)
                            rank = k;
                    if (rank >= 0)
                        info.Places[3] = (byte)(rank + 1);
                    if (w.Champion != s)
                    {
                        info.EliminatedRound = 4;
                        info.Reason = rank >= 0 ? (ElimReason)w.ResultReason(rank) : ElimReason.FellOut;
                    }
                }

                info.Flags = f;
                slots.Add(info);
            }

            m.Slots = slots.ToArray();
            m.PlayerCount = (byte)slots.Count;
            return m;
        }

        /// <summary>fullchain: the match screen of a whole on-chain match, from the World's director.</summary>
        private MatchStateMessage BuildFullMatch(in WorldView w, double now)
        {
            var b = Base;
            var d = w.Director;
            var m = new MatchStateMessage
            {
                Phase = PhaseAt(in w, now),
                PhaseStartTick = d.PhaseStart,
                PhaseEndTick = d.PhaseEnd,
                ServerTick = w.Tick,
                Round = d.Round,
                RoundCount = d.RoundCount > 0 ? d.RoundCount : (byte)4,
                LoadSerial = (byte)(b?.LoadSerial ?? 0),
                Flags = 0,
                HostSlot = b != null ? b.HostSlot : (byte)255,
                OwnSlot = (byte)OwnSlot,
                Champion = d.HasChampion ? d.ChampionSlot : (byte)255,
                MatchSerial = b?.MatchSerial ?? MatchSerial,
                PlayerCount = d.PlayerCount,
                EventCount = d.EventCount,
            };
            if (b != null)
                Buffer.BlockCopy(b.MatchId, 0, m.MatchId, 0, 16); // the keeper creates the Match with the server's id
            d.Seed(m.Seed); // the arena's VRF seed (≠ the Classic seed)
            d.Transcript(m.Transcript);
            if (b == null)
                Buffer.BlockCopy(m.Seed, 0, m.MatchId, 0, 16);
            for (var r = 0; r < 4; r++)
            {
                m.Maps[r] = d.Map(r);
                m.Variants[r] = d.Variant(r);
                m.Modifiers[r] = d.Modifier(r);
                m.Targets[r] = d.Target(r);
            }

            var slots = new List<MatchSlotInfo>(12);
            for (var s = 0; s < 12; s++)
            {
                if (!d.InRoster(s) && !w.Active(s))
                    continue;
                MatchSlotInfo bs = default;
                var inBase = false;
                if (b != null)
                    foreach (var x in b.Slots)
                        if (x.Slot == s)
                        {
                            bs = x;
                            inBase = true;
                        }

                var bot = d.InRoster(s) ? d.Bot(s) : w.IsBot(s);
                var info = new MatchSlotInfo
                {
                    Slot = (byte)s,
                    Name = inBase ? bs.Name : Names[s] ?? (bot ? $"Bot {s}" : $"Player {s}"),
                    Places = _places[s] != null ? (byte[])_places[s].Clone() : new byte[4],
                    Preset = inBase ? bs.Preset : (byte)s,
                    Tier = inBase ? bs.Tier : default,
                    Reason = (ElimReason)d.ElimReason(s),
                    EliminatedRound = (byte)d.ElimRound(s),
                };
                byte f = MatchSlotInfo.Active | MatchSlotInfo.Connected;
                if (d.InRoster(s))
                    f |= MatchSlotInfo.Rostered;
                if (bot)
                    f |= MatchSlotInfo.Bot;
                if (d.InRoster(s) ? d.Alive(s) : w.Active(s))
                    f |= MatchSlotInfo.Alive;
                info.Flags = f;
                slots.Add(info);
            }

            m.Slots = slots.ToArray();
            if (m.PlayerCount == 0)
                m.PlayerCount = (byte)slots.Count;
            return m;
        }
    }
}
