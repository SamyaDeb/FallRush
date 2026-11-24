using System;
using System.Collections.Generic;
using FallRush.Shared;
using NUnit.Framework;

namespace FallRush.Tests
{
    /// <summary>
    /// M4 §5.1: the <see cref="MatchDirector"/> with a fake host and scripted round rules: the full state walk and timings,
    /// event order and seq exactly as doc 06, the transcript equal to the TS reference for scripted matches, minimum-advance
    /// edge cases, roster lock and bot fill, no late joiners, determinism, disconnect grace and reconnects, abort.
    /// </summary>
    public class MatchDirectorTests
    {
        // ------------------------------------------------------------------ fakes

        /// <summary>Round rules that end <see cref="RushTicks"/> after RUSH with a scripted ranking.</summary>
        internal sealed class ScriptedRules : IRoundRules
        {
            public int[] Order = Array.Empty<int>();       // full ranking among alive slots (non-participants are skipped)
            public ElimReason[] Reasons = Array.Empty<ElimReason>(); // per place beyond the target (scripted)
            public int QualifyOverride = -1;              // < target: fewer survive (minimum-advance cases)
            public int RushTicks = 120;
            public string Mode { get; set; } = "Race";
            public RoundPhase Phase { get; private set; } = RoundPhase.Waiting;
            public bool IsOver => Phase == RoundPhase.Ended;
            public int IntroTicks { get; set; }
            public int CountdownTicks { get; set; }
            public uint TimerTicks => 3000;
            public uint RushTick { get; private set; }
            public int Target { get; set; }
            public bool[] Participants { get; set; }
            public RoundResult RoundOutcome { get; private set; }
            public List<RaceEvent> Pending { get; } = new List<RaceEvent>();
            public int LiveCount => 0;
            public event Action<RoundPhase> PhaseChanged;
            private uint _start;

            public uint Begin(SimCore sim, uint tick)
            {
                _start = tick + 1;
                RushTick = _start + (uint)IntroTicks + (uint)CountdownTicks;
                SetPhase(RoundPhase.Intro);
                return RushTick;
            }

            private void SetPhase(RoundPhase p)
            {
                Phase = p;
                PhaseChanged?.Invoke(p);
            }

            public void BeginTick(SimCore sim, uint tick)
            {
                if (Phase == RoundPhase.Intro && tick >= _start + (uint)IntroTicks)
                    SetPhase(RoundPhase.Countdown);
                if (Phase == RoundPhase.Countdown && tick >= RushTick)
                    SetPhase(RoundPhase.Rush);
            }

            public void EndTick(SimCore sim, uint tick)
            {
                if (Phase != RoundPhase.Rush || tick < RushTick + (uint)RushTicks)
                    return;
                var order = new List<int>();
                var q = new List<bool>();
                var reasons = new List<ElimReason>();
                var values = new List<int>();
                var n = QualifyOverride >= 0 ? QualifyOverride : Target;
                var beyond = 0;
                foreach (var s in Order)
                {
                    if (Participants != null && !Participants[s])
                        continue;
                    var qualified = order.Count < n;
                    order.Add(s);
                    q.Add(qualified);
                    reasons.Add(qualified ? ElimReason.NotQualified : beyond < Reasons.Length ? Reasons[beyond] : ElimReason.NotQualified);
                    if (!qualified)
                        beyond++;
                    values.Add(order.Count);
                }

                RoundOutcome = RoundResult.From(order, q, reasons, values, Target, Mode, RushTick, tick, false);
                SetPhase(RoundPhase.Ended);
            }

            public bool AcceptsInput(SimCore sim, int slot) => Phase == RoundPhase.Rush;
            public bool Simulates(SimCore sim, int slot) => Participants == null || Participants[slot];
            public bool Collides(SimCore sim, int slot) => Simulates(sim, slot);
            public bool Respawn(SimCore sim, int slot) => false;
            public byte RaceByte(int slot) => 0;
            public bool InRound(int slot) => Participants == null || Participants[slot];
            public RaceStateMessage StateMessage() => new RaceStateMessage { Phase = Phase, FinisherSlots = Array.Empty<byte>(), FinisherTicks = Array.Empty<uint>() };
        }

        internal sealed class FakeHost : IMatchHost
        {
            public readonly List<MatchEvent> Events = new List<MatchEvent>();
            public readonly List<MatchPhase> Phases = new List<MatchPhase>();
            public readonly List<(MatchPhase phase, uint tick)> PhaseTicks = new List<(MatchPhase, uint)>();
            public readonly List<byte> Loads = new List<byte>();
            public MatchDirector Director;
            public Func<int, ScriptedRules> RoundScript = r => new ScriptedRules();
            public int LoadDelayTicks;
            public bool AutoReady = true;
            public readonly List<int> ReadyHumans = new List<int>();
            private uint _loadAt;
            private SimCore _sim;

            public FakeHost(SimCore sim) => _sim = sim;

            public void LoadMap(byte mapId, in RoundParams p)
            {
                Loads.Add(mapId);
                _loadAt = _sim.Tick + (uint)LoadDelayTicks;
            }

            public bool MapLoaded => _sim.Tick >= _loadAt;

            public IRoundRules CreateRules(byte mapId) => mapId == MapPoolV1.Lobby ? new LobbyRules() : (IRoundRules)RoundScript(Director.Round);

            public Func<int, uint, MotorState, InputCmd> CreateBotInput(IRoundRules rules, RosterSlot[] roster, in RoundParams p) => null;

            public void OnRoundStarted(in RoundParams p)
            {
            }

            public void OnEvent(in MatchEvent e) => Events.Add(e);

            public void OnMatchChanged()
            {
                if (Director == null)
                    return;
                if (Phases.Count == 0 || Phases[Phases.Count - 1] != Director.Phase)
                {
                    Phases.Add(Director.Phase);
                    PhaseTicks.Add((Director.Phase, Director.Tick));
                }
            }
        }

        internal sealed class Harness
        {
            public readonly SimCore Sim;
            public readonly FakeHost Host;
            public readonly MatchDirector D;

            public Harness(byte[] seed, int[] humans)
            {
                var spawns = new Vec3[12];
                for (var i = 0; i < 12; i++)
                    spawns[i] = new Vec3(-11f + 2f * i, 0.1f, 0f);
                Sim = new SimCore(MovementConfig.Default, new AnalyticWorld().Ground(), spawns);
                Host = new FakeHost(Sim);
                D = new MatchDirector(Sim, Host) { SeedSource = _ => seed };
                Host.Director = D;
                D.Start();
                foreach (var h in humans)
                {
                    Assert.That(Sim.AddHumanAt(h, (ulong)(100 + h)), Is.True);
                    D.OnHumanJoined(h, Token(h), $"Human{h}");
                }
            }

            public static byte[] Token(int slot)
            {
                var t = new byte[16];
                for (var i = 0; i < 16; i++)
                    t[i] = (byte)(slot * 31 + i + 1);
                return t;
            }

            /// <summary>One server tick: director, then the sim (as ServerSim does), and the humans' ready messages.</summary>
            public void Tick()
            {
                D.PreStep();
                if (Host.AutoReady && D.Loading)
                    for (var i = 0; i < 12; i++)
                        if (Sim.Slots[i].Active && !Sim.Slots[i].Bot && !Sim.Slots[i].Detached && (Host.ReadyHumans.Count == 0 || Host.ReadyHumans.Contains(i)))
                            D.MarkReady(i, D.LoadSerial);
                Sim.Step();
                D.AfterStep();
            }

            public void RunUntil(Func<bool> cond, int maxTicks = 40000)
            {
                for (var i = 0; i < maxTicks && !cond(); i++)
                    Tick();
                Assert.That(cond(), Is.True, $"condition not reached by tick {Sim.Tick}; phase {D.Phase}");
            }

            public void RunMatch(int maxTicks = 60000) => RunUntil(() => D.MatchesCompleted + D.MatchesAborted > 0 && D.Phase == MatchPhase.LobbyOpen, maxTicks);
        }

        private static Dictionary<string, object> Vector(string name)
        {
            var v = ParityTests.LoadJson("transcript_vectors.json");
            foreach (Dictionary<string, object> m in (List<object>)v["matches"])
                if ((string)m["name"] == name)
                    return m;
            Assert.Fail($"vector {name} missing");
            return null;
        }

        private static int[] Ints(object o)
        {
            var l = (List<object>)o;
            var r = new int[l.Count];
            for (var i = 0; i < r.Length; i++)
                r[i] = (int)(long)l[i];
            return r;
        }

        private static void AssertEventsEqualVector(List<MatchEvent> got, Dictionary<string, object> m)
        {
            var ev = (List<object>)m["events"];
            Assert.That(got.Count, Is.EqualTo(ev.Count), "event count");
            for (var i = 0; i < ev.Count; i++)
            {
                var e = (Dictionary<string, object>)ev[i];
                var g = got[i];
                var where = $"{m["name"]} #{i} got {g}";
                Assert.That(g.Seq, Is.EqualTo((uint)i), where);
                Assert.That(g.Tag, Is.EqualTo((byte)(long)e["tag"]), where);
                Assert.That(g.Round, Is.EqualTo((byte)(long)e["round"]), where);
                Assert.That(g.Slot, Is.EqualTo((byte)(long)e["slot"]), where);
                Assert.That(Sha256.Hex(g.Arg), Is.EqualTo((string)e["arg"]), where);
                Assert.That(Sha256.Hex(g.TranscriptAfter), Is.EqualTo((string)e["after"]), where);
            }
        }

        // ------------------------------------------------------------------ tests

        [Test]
        public void FullMatch_StateWalk_Timings_EventOrder_AndTranscript_EqualTheTsReference()
        {
            var m = Vector("full-12p-4r");
            var seed = ParityTests.Hex((string)m["seed"]);
            var h = new Harness(seed, new[] { 0 });
            var orders = (List<object>)m["orders"];
            var reasons = (List<object>)m["reasons"];
            h.Host.RoundScript = r =>
            {
                var rr = Ints(reasons[r - 1]);
                var er = new ElimReason[rr.Length];
                for (var i = 0; i < rr.Length; i++)
                    er[i] = (ElimReason)rr[i];
                return new ScriptedRules { Order = Ints(orders[r - 1]), Reasons = er, RushTicks = 150 + r * 10 };
            };
            h.RunMatch();

            AssertEventsEqualVector(h.Host.Events, m);
            Assert.That(Sha256.Hex(h.Host.Events[h.Host.Events.Count - 1].TranscriptAfter), Is.EqualTo((string)m["final"]));
            // Program order checks (doc 06): DEAL first; QUALIFIED placements 1..k; COMPLETE_ROUND arg = qualified.
            Assert.That(h.Host.Events[0].Tag, Is.EqualTo(Transcript.Deal));
            Assert.That(h.Host.Events[0].Arg, Is.EqualTo(seed));

            var walk = string.Join(">", h.Host.Phases);
            TestContext.WriteLine(walk);
            var expect = new List<MatchPhase> { MatchPhase.LobbyOpen, MatchPhase.Countdown, MatchPhase.RosterLocked, MatchPhase.Dealing, MatchPhase.Dealt };
            for (var r = 1; r <= 4; r++)
            {
                expect.Add(MatchPhase.RoundIntro);
                expect.Add(MatchPhase.RoundActive);
                expect.Add(MatchPhase.RoundResult);
                if (r < 4)
                    expect.Add(MatchPhase.Intermission);
            }

            expect.AddRange(new[] { MatchPhase.Champion, MatchPhase.Finalizing, MatchPhase.Done, MatchPhase.LobbyOpen });
            Assert.That(h.Host.Phases, Is.EqualTo(expect), walk);

            // Timings (M4-10): countdown 20 s, dealing 3 + dealt 3, results 6, intermission 8, champion 8, receipt 20 (nobody pressed Back).
            uint At(MatchPhase p, int nth = 0)
            {
                var k = 0;
                foreach (var (ph, t) in h.Host.PhaseTicks)
                    if (ph == p && k++ == nth)
                        return t;
                Assert.Fail($"{p} #{nth} missing");
                return 0;
            }

            Assert.That(At(MatchPhase.RosterLocked) - At(MatchPhase.Countdown), Is.EqualTo((uint)MatchDirector.CountdownTicks));
            Assert.That(At(MatchPhase.Dealt) - At(MatchPhase.Dealing), Is.EqualTo((uint)MatchDirector.DealingTicks));
            Assert.That(At(MatchPhase.RoundIntro) - At(MatchPhase.Dealt), Is.EqualTo((uint)MatchDirector.DealtTicks));
            Assert.That(At(MatchPhase.Intermission) - At(MatchPhase.RoundResult), Is.EqualTo((uint)MatchDirector.ResultTicks));
            Assert.That(At(MatchPhase.RoundIntro, 1) - At(MatchPhase.Intermission), Is.EqualTo((uint)MatchDirector.IntermissionTicks));
            Assert.That(At(MatchPhase.Finalizing) - At(MatchPhase.Champion), Is.EqualTo((uint)MatchDirector.ChampionTicks));
            Assert.That(At(MatchPhase.LobbyOpen, 1) - At(MatchPhase.Done), Is.EqualTo((uint)MatchDirector.DoneTicks));
            var introToActive = At(MatchPhase.RoundActive) - At(MatchPhase.RoundIntro);
            Assert.That(introToActive, Is.EqualTo((uint)(MatchDirector.RoundIntroTicks + MatchDirector.RoundCountdownTicks + 2)), "5 s intro + 3-2-1 (+ the ready tick, + intro starts the tick after Begin)");

            Assert.That(h.D.MatchesCompleted, Is.EqualTo(1));
            Assert.That(h.Sim.BotCount, Is.EqualTo(0), "bots leave at the lobby");
            Assert.That(h.Sim.HumanCount, Is.EqualTo(1));
        }

        [Test]
        public void SurvivalMinimumAdvance_OneSurvivor_IsChampion_RoundsSkipped_EqualsTsVector()
        {
            var m = Vector("survival-one-survivor");
            var h = new Harness(ParityTests.Hex((string)m["seed"]), new[] { 0, 5 });
            h.Host.RoundScript = r => r == 1
                ? new ScriptedRules { Order = new[] { 5, 1, 2, 3, 4, 0, 6, 7, 8, 9, 10, 11 } }
                : new ScriptedRules { Order = new[] { 3, 5, 1, 2, 4, 0, 6, 7 }, QualifyOverride = 1, Reasons = new[] { ElimReason.Goo, ElimReason.Goo, ElimReason.Goo, ElimReason.Goo, ElimReason.Goo, ElimReason.Goo, ElimReason.Goo }, Mode = "Survival" };
            h.RunMatch();
            AssertEventsEqualVector(h.Host.Events, m);
            Assert.That(h.D.PreviousResults.Count, Is.EqualTo(2), "rounds 3–4 skipped");
            Assert.That(h.Host.Events[h.Host.Events.Count - 1].Tag, Is.EqualTo(Transcript.CompleteMatch));
        }

        [Test]
        public void ZeroSurvivors_HighestPlacedAdvances_AsTheSoleSurvivor()
        {
            var h = new Harness(new byte[32], new[] { 0 });
            h.Host.RoundScript = r => r == 1
                ? new ScriptedRules { Order = new[] { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11 } }
                : new ScriptedRules { Order = new[] { 6, 0, 1, 2, 3, 4, 5, 7 }, QualifyOverride = 0, Mode = "Survival" };
            h.RunMatch();
            var r2 = h.D.PreviousResults[1];
            Assert.That(r2.QualifiedCount, Is.EqualTo(1));
            Assert.That(r2.Order[0], Is.EqualTo(6));
            Assert.That(r2.Qualified[0], Is.True);
            var last = h.Host.Events[h.Host.Events.Count - 1];
            Assert.That(last.Tag, Is.EqualTo(Transcript.CompleteMatch));
            Assert.That(last.Slot, Is.EqualTo(6), "never a match without a champion");
            Assert.That(last.Round, Is.EqualTo(2));
        }

        [Test]
        public void FewerThanN_Survive_OnlySurvivorsAdvance_NextRoundRunsWithThem()
        {
            var h = new Harness(new byte[32], new[] { 0 });
            var started = new List<int>();
            h.Host.RoundScript = r =>
            {
                var s = r switch
                {
                    1 => new ScriptedRules { Order = new[] { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11 } },
                    2 => new ScriptedRules { Order = new[] { 2, 0, 4, 1, 3, 5, 6, 7 }, QualifyOverride = 3 }, // target 5, only 3 survive
                    _ => new ScriptedRules { Order = new[] { 0, 2, 4 } },
                };
                return s;
            };
            h.RunMatch();
            Assert.That(h.D.PreviousResults[1].QualifiedCount, Is.EqualTo(3));
            Assert.That(h.D.PreviousResults[2].Order.Length, Is.EqualTo(3), "round 3 ran with the 3 survivors");
            // Round 3's target (3) ≥ the 3 alive → all qualify → round 4 crowns 1.
            Assert.That(h.D.PreviousResults.Count, Is.EqualTo(4));
            Assert.That(h.D.PreviousChampion, Is.EqualTo(0));
        }

        [Test]
        public void RosterLock_FillsBotsTo12_LabelsThem_AndAdmitsNoLateJoiners()
        {
            var h = new Harness(new byte[32], new[] { 0, 3 });
            h.RunUntil(() => h.D.Phase == MatchPhase.Countdown);
            Assert.That(h.D.HostSlot, Is.EqualTo(0), "the first human is the host");
            Assert.That(h.D.HostStart(3), Is.False, "only the host can start");
            Assert.That(h.D.HostStart(0), Is.False, "beta audit: no host start in a public lobby (the countdown runs out)");
            h.RunUntil(() => h.D.Phase == MatchPhase.Dealing);
            Assert.That(h.D.PlayerCount, Is.EqualTo(12));
            Assert.That(h.Sim.BotCount, Is.EqualTo(10));
            for (var i = 0; i < 12; i++)
            {
                Assert.That(h.D.Roster[i].Occupied, Is.True);
                Assert.That(h.D.Roster[i].Bot, Is.EqualTo(i != 0 && i != 3), $"slot {i}");
                Assert.That(h.D.Roster[i].Bot ? h.D.Roster[i].Tier != BotTier.Reference : h.D.Roster[i].Tier == BotTier.Reference, Is.True);
            }

            Assert.That(h.D.Targets, Is.EqualTo(new byte[] { 8, 5, 3, 1 }));
            Assert.That(h.D.InMatch, Is.True, "a new connection now is a viewer (the server never seats it)");
            Assert.That(h.D.FindResumeSlot(Harness.Token(7)), Is.EqualTo(-1), "no slot for a stranger's token");
            // The roster hash commits to the placeholder ids (bots labelled).
            var entries = new List<RosterHash.Entry>();
            for (var i = 0; i < 12; i++)
                entries.Add(new RosterHash.Entry { Slot = (byte)i, Bot = i != 0 && i != 3, UserId = RosterHash.PlaceholderUserId(i, i != 0 && i != 3) });
            Assert.That(h.D.RosterHashBytes, Is.EqualTo(RosterHash.Compute(entries)));
        }

        [Test]
        public void Lobby_NeedsAHuman_UnlessSoak_AndCountdownResetsWhenTheyLeave()
        {
            var h = new Harness(new byte[32], Array.Empty<int>());
            for (var i = 0; i < 100; i++)
                h.Tick();
            Assert.That(h.D.Phase, Is.EqualTo(MatchPhase.LobbyOpen), "practice: ≥ 1 human");
            h.Sim.AddHumanAt(2, 7);
            h.D.OnHumanJoined(2, Harness.Token(2), null);
            h.RunUntil(() => h.D.Phase == MatchPhase.Countdown);
            Assert.That(h.D.OnHumanLeft(2), Is.True, "lobby: the slot is freed at once");
            h.Sim.Remove(2);
            h.Tick();
            Assert.That(h.D.Phase, Is.EqualTo(MatchPhase.LobbyOpen));

            var soak = new Harness(new byte[32], Array.Empty<int>());
            soak.D.Soak = true;
            soak.RunMatch();
            Assert.That(soak.D.MatchesCompleted, Is.EqualTo(1), "soak: bot-only match runs to the end");
        }

        [Test]
        public void SameSeedRosterAndInputs_GiveTheSameEventsAndTranscript()
        {
            List<MatchEvent> Run()
            {
                var h = new Harness(Sha256.Hash(new byte[] { 1, 2, 3 }), new[] { 0 });
                h.Host.RoundScript = r => new ScriptedRules { Order = new[] { 11, 10, 9, 8, 7, 6, 5, 4, 3, 2, 1, 0 } };
                h.RunMatch();
                return h.Host.Events;
            }

            var a = Run();
            var b = Run();
            Assert.That(a.Count, Is.EqualTo(b.Count));
            for (var i = 0; i < a.Count; i++)
            {
                Assert.That(a[i].ToString(), Is.EqualTo(b[i].ToString()));
                Assert.That(a[i].TranscriptAfter, Is.EqualTo(b[i].TranscriptAfter));
            }
        }

        [Test]
        public void Reconnect_WithinGrace_SameSlot_StillAlive_AndBeyondGrace_EliminatedDisconnect()
        {
            // (a) Drop at round 2, reconnect after 10 s with the token → same slot, still alive, qualifies normally.
            var h = new Harness(new byte[32], new[] { 0, 4 });
            h.Host.RoundScript = r => new ScriptedRules { Order = new[] { 4, 0, 1, 2, 3, 5, 6, 7, 8, 9, 10, 11 }, RushTicks = 1200 };
            h.RunUntil(() => h.D.Phase == MatchPhase.RoundActive && h.D.Round == 2);
            Assert.That(h.D.OnHumanLeft(4), Is.False, "in a match the slot is kept");
            h.Sim.Detach(4);
            for (var i = 0; i < 300; i++)
                h.Tick();
            var slot = h.D.FindResumeSlot(Harness.Token(4));
            Assert.That(slot, Is.EqualTo(4), "same slot");
            h.Sim.Attach(4, 999);
            h.D.OnHumanResumed(4);
            h.RunUntil(() => h.D.Phase == MatchPhase.RoundResult && h.D.Round == 2);
            Assert.That(h.D.Roster[4].Alive, Is.True, "still alive after the round");
            Assert.That(h.D.Roster[4].GraceExpired, Is.False);

            // (b) Drop for 25 s in round 2 → ELIMINATED(DISCONNECT) at the end of the round, even though the script ranked them 1st.
            var g = new Harness(new byte[32], new[] { 0, 4 });
            g.Host.RoundScript = r => new ScriptedRules { Order = new[] { 4, 0, 1, 2, 3, 5, 6, 7, 8, 9, 10, 11 }, RushTicks = 1200 };
            g.RunUntil(() => g.D.Phase == MatchPhase.RoundActive && g.D.Round == 2);
            g.D.OnHumanLeft(4);
            g.Sim.Detach(4);
            for (var i = 0; i < 25 * 30; i++)
                g.Tick();
            Assert.That(g.D.Roster[4].GraceExpired, Is.True);
            Assert.That(g.D.FindResumeSlot(Harness.Token(4)), Is.EqualTo(4), "a late reconnect still finds the slot (to spectate)");
            g.RunUntil(() => g.D.Phase == MatchPhase.RoundResult && g.D.Round == 2);
            var dc = g.Host.Events.FindAll(e => e.Tag == Transcript.Eliminated && e.Slot == 4);
            Assert.That(dc.Count, Is.EqualTo(1));
            Assert.That(dc[0].Arg[0], Is.EqualTo((byte)ElimReason.Disconnect));
            Assert.That(dc[0].Round, Is.EqualTo(2));
            Assert.That(g.D.Results[1].QualifiedCount, Is.EqualTo(4), "the place is not handed down (finish order stands); still ≤ target");
        }

        [Test]
        public void EveryHumanGoneBeyondGrace_PracticeMatch_Aborts_WithAnAbortEvent()
        {
            var h = new Harness(new byte[32], new[] { 0 });
            h.Host.RoundScript = r => new ScriptedRules { Order = new[] { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11 }, RushTicks = 3000 };
            h.RunUntil(() => h.D.Phase == MatchPhase.RoundActive);
            h.D.OnHumanLeft(0);
            h.Sim.Detach(0);
            h.RunUntil(() => h.D.Phase == MatchPhase.Aborted, 2000);
            var last = h.Host.Events[h.Host.Events.Count - 1];
            Assert.That(last.Tag, Is.EqualTo(Transcript.Abort));
            Assert.That(last.Arg[0], Is.EqualTo((byte)AbortReason.NoHumans));
            Assert.That(last.Seq, Is.EqualTo((uint)(h.Host.Events.Count - 1)), "ABORT consumes the next seq");
            h.RunUntil(() => h.D.Phase == MatchPhase.LobbyOpen);
            Assert.That(h.Sim.HumanCount + h.Sim.BotCount, Is.EqualTo(0), "the dropped human's slot is freed in the new lobby");
        }

        [Test]
        public void LateLoader_SpectatesTheRound_IsProtected_AndQualifiedStaysWithinTheTarget()
        {
            var h = new Harness(new byte[32], new[] { 0, 1 });
            h.Host.RoundScript = r => new ScriptedRules { Order = new[] { 0, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 1 } };
            h.Host.ReadyHumans.Add(0); // slot 1 never answers `ready`
            h.RunUntil(() => h.D.Phase == MatchPhase.RoundActive && h.D.Round == 1);
            Assert.That(h.D.Rules.Participants[1], Is.False, "late loader is not in the round");
            Assert.That(h.D.Rules.Target, Is.EqualTo(7), "target 8 − 1 protected late loader");
            h.RunUntil(() => h.D.Phase == MatchPhase.RoundResult);
            var r1 = h.D.Results[0];
            Assert.That(r1.QualifiedCount, Is.EqualTo(8));
            Assert.That(h.D.Roster[1].Alive, Is.True, "not eliminated for a round it could not load");
            Assert.That(h.D.Roster[1].Late[0], Is.True);
        }
    }
}
