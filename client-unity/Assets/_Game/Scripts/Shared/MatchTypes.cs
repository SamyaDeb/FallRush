using System;
using System.Collections.Generic;

namespace FallRush.Shared
{
    /// <summary>
    /// The canonical match state machine (doc 05 §4), as built in M4. The byte value is on the wire (<c>match</c>).
    /// </summary>
    public enum MatchPhase : byte
    {
        LobbyOpen = 0,
        Countdown = 1,
        RosterLocked = 2,
        Dealing = 3,
        Dealt = 4,
        RoundIntro = 5,   // map load (+ ready / 10 s timeout) → START_ROUND → intro + 3-2-1
        RoundActive = 6,  // RUSH → the round's rules end it
        RoundResult = 7,  // QUALIFIED / ELIMINATED / COMPLETE_ROUND, 6 s
        Intermission = 8, // 8 s
        Champion = 9,     // COMPLETE_MATCH, 8 s
        Finalizing = 10,  // no-op in M4 (TODO(M6): commit + undelegate)
        Done = 11,        // receipt, until everyone pressed "Back to lobby" (auto after 20 s)
        Aborted = 12,
    }

    /// <summary>
    /// Elimination reason codes (u8): one table for the server, the event log, the receipt and the program's
    /// <c>SlotResult.reason</c> (M4-9, proposed to M6 in doc 06's notes).
    /// </summary>
    public enum ElimReason : byte
    {
        NotQualified = 0, // race: did not finish in the top N
        Goo = 1,          // survival: touched the goo
        FellOut = 2,      // survival / final: fell out of the map
        LowerScore = 3,   // skill: below the top N by score
        TimeoutRank = 4,  // timer ended and ranked below the target
        Disconnect = 5,   // disconnected for longer than the 20 s grace
    }

    /// <summary>ABORT reason codes (arg of the ABORT event).</summary>
    public enum AbortReason : byte
    {
        None = 0,
        NoHumans = 1,    // practice match: every human left (beyond the grace)
        NoPlayers = 2,   // no candidate left to advance
        ServerStop = 3,
    }

    /// <summary>
    /// One event of the match transcript (doc 06 §3/§9), in program order: seq 0 = DEAL, then strictly increasing. M5's
    /// outbox ingests these from the jsonl log; M6 forwards them to the ER unchanged (tag → instruction).
    /// </summary>
    public struct MatchEvent
    {
        public uint Seq;
        public byte Tag;    // Transcript.Deal … Transcript.Abort
        public byte Round;
        public byte Slot;   // Transcript.NoSlot = none
        public byte[] Arg;  // DEAL: 32 B seed; QUALIFIED: [placement]; ELIMINATED: [reason]; COMPLETE_ROUND: [qualified]; ABORT: [reason]
        public uint Tick;
        public byte[] TranscriptAfter;

        public override string ToString() =>
            $"#{Seq} {Transcript.TagName(Tag)} r{Round} slot={(Slot == Transcript.NoSlot ? "-" : Slot.ToString())} arg={(Arg == null || Arg.Length == 0 ? "-" : Arg.Length == 1 ? Arg[0].ToString() : Sha256.Hex(Arg).Substring(0, 16) + "…")} tick={Tick}";
    }

    public enum BotTier : byte
    {
        Reference = 0, // QA: no noise
        Easy = 1,
        Normal = 2,
        Good = 3,
    }

    /// <summary>One roster slot of the current match (slot index = <see cref="SimCore"/> slot = on-chain slot).</summary>
    public struct RosterSlot
    {
        public bool Occupied;
        public bool Bot;
        public bool Alive;            // still in the match (qualified every round so far)
        public bool Connected;        // human: the connection is up (bots: always)
        public uint DisconnectedAt;   // tick the human dropped (0 = connected)
        public bool GraceExpired;     // dropped for longer than the grace: eliminated (DISCONNECT) at the end of the current round
        public byte EliminatedRound;  // 0 = not eliminated
        public ElimReason Reason;
        public BotTier Tier;
        public byte Preset;           // G2.2 cosmetic preset (hello for humans, CosmeticPresets.ForBot for bots)
        public string Name;
        public byte[] Places;         // per round: place 1.. in that round's ranking, 0 = not played
        public bool[] Late;           // per round: missed the load timeout and spectated it
    }

    /// <summary>
    /// Ranked outcome of one round (every <see cref="IRoundRules"/> produces one): participating slots in final order, a
    /// qualified flag and an elimination reason per place, and a mode statistic (finish tick, height mm, score, layer).
    /// Deterministic: the same inputs give the same result.
    /// </summary>
    public sealed class RoundResult
    {
        public string Mode = "Race";
        public int Target;
        public int[] Order = Array.Empty<int>();
        public bool[] Qualified = Array.Empty<bool>();
        public ElimReason[] Reasons = Array.Empty<ElimReason>();
        public int[] Value = Array.Empty<int>();
        public uint StartTick, EndTick;
        public bool TimerExpired;

        public int QualifiedCount
        {
            get
            {
                var n = 0;
                foreach (var q in Qualified)
                    if (q)
                        n++;
                return n;
            }
        }

        public override string ToString()
        {
            var sb = new System.Text.StringBuilder();
            sb.Append(Mode).Append(" target=").Append(Target).Append(" timer=").Append(TimerExpired).Append(" duration=")
                .Append(((EndTick - StartTick) / 30.0).ToString("F1", System.Globalization.CultureInfo.InvariantCulture)).Append("s order=");
            for (var i = 0; i < Order.Length; i++)
                sb.Append(i > 0 ? "," : "").Append(Order[i]).Append(Qualified[i] ? "Q" : "x" + (byte)Reasons[i]).Append('(').Append(Value[i]).Append(')');
            return sb.ToString();
        }

        public static RoundResult From(List<int> order, List<bool> qualified, List<ElimReason> reasons, List<int> values, int target, string mode, uint start, uint end, bool timer) =>
            new RoundResult
            {
                Mode = mode, Target = target, Order = order.ToArray(), Qualified = qualified.ToArray(), Reasons = reasons.ToArray(),
                Value = values.ToArray(), StartTick = start, EndTick = end, TimerExpired = timer,
            };
    }

    /// <summary>
    /// Round rules the <see cref="MatchDirector"/> drives (Race, Survival, Skill, Final, the lobby room). They plug into
    /// <see cref="SimCore"/> through <see cref="ISimRules"/>, run their own Intro → Countdown → Rush → Ended phases, and
    /// return a <see cref="RoundResult"/>. The director sets <see cref="Target"/> (from <see cref="RoundTargets"/>) and
    /// <see cref="Participants"/> (alive slots) before <see cref="Begin"/>.
    /// </summary>
    public interface IRoundRules : ISimRules
    {
        string Mode { get; }
        RoundPhase Phase { get; }
        bool IsOver { get; }
        int IntroTicks { get; set; }
        int CountdownTicks { get; set; }
        uint TimerTicks { get; }
        uint RushTick { get; }
        /// <summary>Qualify count for this round (set by the director before <see cref="Begin"/>); 0 = the M3 default.</summary>
        int Target { get; set; }
        /// <summary>Slots that take part (null = every active slot).</summary>
        bool[] Participants { get; set; }
        uint Begin(SimCore sim, uint tick);
        RoundResult RoundOutcome { get; }
        /// <summary>Gameplay events for the HUD (<c>evt</c>), drained by the server every tick.</summary>
        List<RaceEvent> Pending { get; }
        RaceStateMessage StateMessage();
        event Action<RoundPhase> PhaseChanged;
        /// <summary>The live top-centre HUD number: qualified so far / alive / … (mode-specific).</summary>
        int LiveCount { get; }
        /// <summary>Is this slot in the round (participant, not out)? Out-of-round slots are left out of the snapshot.</summary>
        bool InRound(int slot);
    }
}
