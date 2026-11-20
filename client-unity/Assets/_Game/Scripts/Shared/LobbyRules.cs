using System;
using System.Collections.Generic;

namespace FallRush.Shared
{
    /// <summary>
    /// The lobby room (<c>Map_Lobby</c>, M4-11): free play while waiting, no eliminations, a fall respawns you on your
    /// spawn after 1 s. Never ends; the match director replaces it at roster lock.
    /// </summary>
    public sealed class LobbyRules : IRoundRules
    {
        public string Mode => "Lobby";
        public RoundPhase Phase => RoundPhase.Sandbox;
        public bool IsOver => false;
        public int IntroTicks { get; set; }
        public int CountdownTicks { get; set; }
        public uint TimerTicks => 0;
        public uint RushTick => 0;
        public int Target { get; set; }
        public bool[] Participants { get; set; }
        public RoundResult RoundOutcome => null;
        public List<RaceEvent> Pending { get; } = new List<RaceEvent>();
        public int LiveCount => 0;
        public event Action<RoundPhase> PhaseChanged { add { } remove { } }

        public uint Begin(SimCore sim, uint tick) => 0;
        public RaceStateMessage StateMessage() => new RaceStateMessage { Phase = RoundPhase.Sandbox, FinisherSlots = Array.Empty<byte>(), FinisherTicks = Array.Empty<uint>() };
        public void BeginTick(SimCore sim, uint tick) { }
        public void EndTick(SimCore sim, uint tick) { }
        public bool AcceptsInput(SimCore sim, int slot) => true;
        public bool Simulates(SimCore sim, int slot) => true;
        public bool Collides(SimCore sim, int slot) => true;
        public bool Respawn(SimCore sim, int slot) => false;
        public byte RaceByte(int slot) => 0;
        public bool InRound(int slot) => true;
    }
}
