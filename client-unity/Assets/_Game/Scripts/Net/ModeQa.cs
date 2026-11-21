using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using FallRush.Shared;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace FallRush.Net
{
    /// <summary>
    /// M4 map QA (doc 08 §9, M4 §5.2) for every mode: the loaded map × variant A/B × allowed modifier × N runs with 12
    /// <see cref="BotBrain"/> bots (reference tier = no noise, and normal tier), the real <see cref="SimCore"/> + the
    /// mode's rules + PhysX world, as fast as the CPU allows (no networking). Asserts: no soft-lock (the round always
    /// ends with a valid result), qualified ≤ target, Final → exactly one champion (the director's minimum-advance applied),
    /// Skill scores = pickups − drops, Survival: the goo never overtakes the slowest valid route (a reference bot alone on
    /// each route keeps a positive margin all the way up), median duration vs the GDD §4.1 target.
    /// <code>FallRushDevServer -batchmode -nographics -fallrushQA 100 -fallrushMap GooTower [-fallrushQATier normal|reference|both] [-fallrushQAOut path]</code>
    /// </summary>
    public static class ModeQa
    {
        public struct Stats
        {
            public int Variant, Modifier, Run;
            public float Duration;
            public bool Over, TimerExpired;
            public int Qualified, Target, Order;
            public int Escaped, Goo, FellOut, Falls, Hits, Stuck;
            public int Pickups, Drops, ScoreSum, ScoreExpected;
            public bool ChampionOk;
            public string Result;
            // G4.4 Skill (Star Fountain)
            public int TablePicks, GoldenCollected, GoldenSpawned, SpringTries, SpringOk, FallsMaxBot, DropsMaxBot, Slot0Score;
            public int[] Scores; // final scores, best first
            public int[] Values; // the round result's ranking values, in ranking order
            public bool[] QualifiedFlags;
            // G4.5 Final on a lid map (Lid Flip)
            public LidQaTrack Lid;
            public int Alive;
        }

        /// <summary>G4.4 camper probe spots on Star Fountain (world mm; island ≥ 0 = ride that island). Mirrors StarScrambleBuilder.</summary>
        public static readonly (string name, int x, int z, int island)[] CampSpots =
        {
            ("fountain base (r 2.2, on the table)", Spot(2200, 200).x, Spot(2200, 200).z, -1),
            ("turntable edge (r 8.5)", Spot(8500, 90).x, Spot(8500, 90).z, -1),
            ("outer ring between sweepers (r 13, 90°)", Spot(13000, 90).x, Spot(13000, 90).z, -1),
            ("outer anchor at a sweeper flank (r 13, 12°: variant A anchor)", Spot(13000, 12).x, Spot(13000, 12).z, -1),
            ("island 0", 0, 0, 0),
            ("island 1", 0, 0, 1),
            ("island 2", 0, 0, 2),
        };

        private static (int x, int z) Spot(int r, float deg) => ((int)Math.Round(Math.Sin(deg * Math.PI / 180) * r), (int)Math.Round(Math.Cos(deg * Math.PI / 180) * r));

        public static string TierArg()
        {
            var args = Environment.GetCommandLineArgs();
            for (var i = 0; i < args.Length - 1; i++)
                if (string.Equals(args[i], "-fallrushQATier", StringComparison.OrdinalIgnoreCase))
                    return args[i + 1].ToLowerInvariant();
            return "both";
        }

        /// <summary>Players a round of this mode really has in a 12-player match (12 → 8 → 5 → 3).</summary>
        public static int PlayersFor(string mode) => mode switch
        {
            "Survival" => RoundTargets.For(12)[0],
            "Skill" => RoundTargets.For(12)[1],
            "Final" => RoundTargets.For(12)[2],
            _ => 12,
        };

        public static int BotsArg(string mode)
        {
            var args = Environment.GetCommandLineArgs();
            for (var i = 0; i < args.Length - 1; i++)
                if (string.Equals(args[i], "-fallrushQABots", StringComparison.OrdinalIgnoreCase) && int.TryParse(args[i + 1], out var n))
                    return Math.Max(1, Math.Min(12, n));
            return PlayersFor(mode);
        }

        public static int TargetFor(string mode) => mode switch
        {
            "Race" => RoundTargets.For(12)[0],
            "Survival" => RoundTargets.For(12)[1],
            "Skill" => RoundTargets.For(12)[2],
            _ => 1,
        };

        private static readonly bool GooTrace = Environment.GetEnvironmentVariable("FALLRUSH_QA_GOO") == "1";

        public static Stats RunOne(MapInfo map, CapsuleCollider probe, int variant, int modifier, int run, BotTier tier, int bots = 12, int forcedRoute = -1, Action<SimCore, IRoundRules, HazardSet> perTick = null, Action<BotBrainSet> onBots = null, int campSpot = -1)
        {
            var seed = (ulong)(0x4D4E0000 + variant * 1000003 + modifier * 10007 + run * 7919 + (int)tier * 104729);
            var p = new RoundParams { MapId = map.MapId, Variant = (byte)variant, Modifier = (byte)modifier, HazardSeed = seed, RoundIndex = 1 };
            var cfg = NetBootstrap.MovementFor(modifier);
            var world = map.CreateWorld(probe, cfg, in p, out var hz);
            var spawns = map.SpawnPoints();
            var yaws = map.SpawnYaws();
            var rng = (uint)(seed * 2654435761UL) | 1u;
            for (var i = spawns.Length - 1; i > 0; i--)
            {
                rng ^= rng << 13;
                rng ^= rng >> 17;
                rng ^= rng << 5;
                var j = (int)(rng % (uint)(i + 1));
                (spawns[i], spawns[j]) = (spawns[j], spawns[i]);
                (yaws[i], yaws[j]) = (yaws[j], yaws[i]);
            }

            var core = new SimCore(cfg, world, spawns, hz, yaws);
            for (var i = 0; i < bots; i++)
                core.AddBot();
            var rules = RulesFactory.Create(map, map.MapId);
            var mode = rules.Mode;
            rules.Target = Math.Min(bots, TargetFor(mode));
            rules.IntroTicks = MatchDirector.RoundIntroTicks;
            rules.CountdownTicks = MatchDirector.RoundCountdownTicks;
            core.Rules = rules;
            var roster = new RosterSlot[SimCore.MaxSlots];
            for (var i = 0; i < SimCore.MaxSlots; i++)
                roster[i] = new RosterSlot { Occupied = core.Slots[i].Active, Bot = true, Alive = true, Tier = tier };
            var set = new BotBrainSet(map, core, rules, roster, in p, tier);
            if (forcedRoute >= 0)
                for (var i = 0; i < SimCore.MaxSlots; i++)
                    if (set[i]?.Waypoints != null)
                        set[i].Waypoints.ForcedRoute = forcedRoute;
            onBots?.Invoke(set); // G4.0c route QA reads each bot's route choice
            if (campSpot >= 0 && campSpot < CampSpots.Length && core.Slots[0].Active)
            {
                // G4.4 camper probe: slot 0 is a reference bot that never leaves its spot; the others keep their tier.
                var sp = CampSpots[campSpot];
                set.Replace(0, new BotBrain(0, BotTier.Reference, (uint)(p.HazardSeed * 2654435761UL), rules, core, Array.Empty<BotRoute>())
                {
                    Camp = true, CampXMm = sp.x, CampZMm = sp.z, CampIsland = sp.island,
                });
            }

            var skillTrack = mode == "Skill";
            var lidTrack = mode == "Final" && hz.LidGrids.Length > 0 && rules is TimedRoundRules tr0 ? new LidQaTrack(hz, tr0) : null;
            var springAt = new uint[SimCore.MaxSlots];
            core.BotInput = set.Input;
            var rush = rules.Begin(core, core.Tick);
            hz.RoundStartTick = rush;
            var cap = rush + rules.TimerTicks + 120;
            int springTries = 0, springOk = 0;
            var trace = Environment.GetEnvironmentVariable("FALLRUSH_QA_TRACE") == "1";
            while (!rules.IsOver && core.Tick < cap)
            {
                core.Step();
                perTick?.Invoke(core, rules, hz);
                lidTrack?.Step(core, rush);
                if (skillTrack)
                    TrackSprings(core, hz, springAt, ref springTries, ref springOk);
                if (GooTrace && mode == "Survival")
                    for (var i = 0; i < SimCore.MaxSlots; i++)
                        if (core.Slots[i].Active && (core.Slots[i].LastStep & PlayerStep.Result.Gooed) != 0)
                        {
                            ref readonly var g = ref core.Slots[i].State;
                            var w = set[i]?.Waypoints;
                            Debug.Log($"[qa-goo] tier={tier} v={variant} run={run} slot={i} t={(int)(core.Tick - rush) / 30f:F1}s at ({g.PosX / 1000f:F1},{g.PosY / 1000f:F1},{g.PosZ / 1000f:F1}) {w?.CurrentRouteName}#{w?.Waypoint} stuck={w?.Stuck} {w?.LastStuckReason}");
                        }
                if (trace && bots == 1 && (core.Tick - rush) % 15 == 0)
                {
                    ref readonly var s0 = ref core.Slots[0].State;
                    var wp = set[0]?.Waypoints;
                    Debug.Log($"[qa-trace] t={(int)(core.Tick - rush) / 30f:F1}s pos=({s0.PosX},{s0.PosY},{s0.PosZ}) v=({s0.VelX},{s0.VelY},{s0.VelZ}) g={(s0.Has(MotorState.Grounded) ? 1 : 0)} sec={(set[0] != null && mode == "Survival" ? set[0].SurvivalSection(s0.PosY) : -1)} route={wp?.CurrentRouteName}#{wp?.Waypoint} goo={hz.GooYMm(core.Tick)} stuck={wp?.Stuck} {wp?.LastStuckReason}");
                }
            }

            var st = new Stats { Variant = variant, Modifier = modifier, Run = run, Over = rules.IsOver, Target = rules.Target };
            for (var i = 0; i < SimCore.MaxSlots; i++)
            {
                st.Falls += core.FallsBySlot[i];
                st.Hits += core.HitsBySlot[i];
                if (set[i]?.Waypoints != null)
                    st.Stuck += set[i].Waypoints.Stuck;
            }

            var res = rules.RoundOutcome;
            if (res == null)
            {
                st.Result = "no result (cap reached)";
                return st;
            }

            st.Duration = (res.EndTick - res.StartTick) / 30f;
            st.TimerExpired = res.TimerExpired;
            if (res.TimerExpired && rules is RaceRules rr)
            {
                // G4.2: where the racers that had not finished were when the timer ran out (route, waypoint, last stuck reason).
                var sb = new System.Text.StringBuilder();
                for (var i = 0; i < SimCore.MaxSlots; i++)
                {
                    if (!core.Slots[i].Active || set[i]?.Waypoints == null || rr.Racers[i].Checkpoint >= map.checkpoints.Length)
                        continue;
                    ref readonly var s0 = ref core.Slots[i].State;
                    var w = set[i].Waypoints;
                    sb.Append($" slot{i}@cp{rr.Racers[i].Checkpoint} {w.CurrentRouteName}#{w.Waypoint} at ({s0.PosX / 1000},{s0.PosY / 1000},{s0.PosZ / 1000}) stuck={w.Stuck} timeouts={w.WaitTimeouts} last={w.LastStuckReason};");
                }

                Debug.Log($"[qa-timer] tier={tier} v={variant} mod={modifier} run={run}:{sb}");
            }
            st.Qualified = res.QualifiedCount;
            st.Order = res.Order.Length;
            st.Result = res.ToString();
            st.Values = res.Value;
            st.QualifiedFlags = res.Qualified;
            for (var k = 0; k < res.Order.Length; k++)
            {
                if (res.Reasons[k] == ElimReason.Goo)
                    st.Goo++;
                if (res.Reasons[k] == ElimReason.FellOut)
                    st.FellOut++;
            }

            if (rules is SurvivalRules sv)
                st.Escaped = sv.Escaped;
            if (rules is SkillRules sk)
            {
                st.Pickups = (int)sk.Pickups;
                st.Drops = (int)sk.Drops;
                var f = hz.StarFields[0];
                var collectedValue = 0;
                foreach (var e in hz.Reactive.Log)
                    if (e.Type == RxType.StarCollect)
                    {
                        if (e.Id >= HazardMath.StarDropBase)
                            collectedValue += 1;
                        else
                        {
                            HazardMath.StarSlot(in f, hz.Seed, e.Id, out _, out _, out _, out var v);
                            collectedValue += v;
                        }
                    }

                st.ScoreExpected = collectedValue - st.Drops;
                for (var i = 0; i < SimCore.MaxSlots; i++)
                    st.ScoreSum += sk.Players[i].Score;

                // G4.4: where the pickups were (turntable share), golden stars, springs, falls / drops per bot, final scores.
                foreach (var e in hz.Reactive.Log)
                {
                    if (e.Type != RxType.StarCollect)
                        continue;
                    if (e.Id >= HazardMath.StarDropBase)
                    {
                        var d = e.Id - HazardMath.StarDropBase;
                        if (d < hz.Reactive.Drops.Count)
                        {
                            var dr = hz.Reactive.Drops[d];
                            HazardMath.DropPosMm(in f, dr.Xcm * 10, dr.Ycm * 10, dr.Zcm * 10, (uint)(d + 1), f.DropPopTicks, out var x, out _, out var z);
                            if (hz.TurnTables.Length > 0 && (long)x * x + (long)z * z <= (long)hz.TurnTables[0].RadiusMm * hz.TurnTables[0].RadiusMm)
                                st.TablePicks++;
                        }
                    }
                    else if (e.Id >= HazardMath.StarGoldenBase)
                        st.GoldenCollected++;
                    else if (HazardMath.StarSlot(in f, hz.Seed, e.Id, out var a, out _, out _, out _) && f.Attach != null && a < f.Attach.Length && f.Attach[a] >= 1000)
                        st.TablePicks++;
                }

                for (var g = 1; f.GoldenEveryTicks > 0 && g * f.GoldenEveryTicks < (int)rules.TimerTicks; g++)
                    st.GoldenSpawned++;
                st.SpringTries = springTries;
                st.SpringOk = springOk;
                var scores = new List<int>();
                for (var i = 0; i < SimCore.MaxSlots; i++)
                {
                    if (!sk.Players[i].In)
                        continue;
                    scores.Add(sk.Players[i].Score);
                    st.FallsMaxBot = Math.Max(st.FallsMaxBot, core.FallsBySlot[i]);
                }

                scores.Sort((x, y) => y.CompareTo(x));
                st.Scores = scores.ToArray();
                st.Slot0Score = sk.Players[0].Score;
                var dropsBy = new int[SimCore.MaxSlots];
                foreach (var e in hz.Reactive.Log)
                    if (e.Type == RxType.StarDrop && (uint)e.Slot < (uint)dropsBy.Length)
                        dropsBy[e.Slot]++;
                foreach (var dcount in dropsBy)
                    st.DropsMaxBot = Math.Max(st.DropsMaxBot, dcount);
            }

            // Final: exactly one champion once the director's minimum-advance is applied (0 qualified → the top-ranked).
            st.ChampionOk = mode != "Final" || (st.Qualified <= 1 && res.Order.Length > 0);
            if (lidTrack != null && rules is TimedRoundRules tr1)
            {
                lidTrack.Step(core, rush); // the last eliminations (the round ends on the tick they happen)
                lidTrack.Finish(res.EndTick);
                st.Lid = lidTrack;
                if (res.TimerExpired)
                {
                    var sb = new StringBuilder();
                    for (var i = 0; i < SimCore.MaxSlots; i++)
                        if (tr1.Players[i].In && !tr1.Players[i].Out)
                        {
                            ref readonly var q0 = ref core.Slots[i].State;
                            sb.Append($" slot{i} at ({q0.PosX},{q0.PosY},{q0.PosZ}) hits={core.HitsBySlot[i]} misjudges={set[i]?.LidMisjudges} hops={set[i]?.LidHops};");
                        }

                    Debug.Log($"[qa-timer] Final tier={tier} v={variant} mod={modifier} run={run}:{sb}");
                }
                for (var i = 0; i < SimCore.MaxSlots; i++)
                    st.Alive += tr1.Players[i].In && !tr1.Players[i].Out ? 1 : 0;
            }
            return st;
        }

        private static readonly bool SpringTrace = Environment.GetEnvironmentVariable("FALLRUSH_QA_SPRING") == "1";

        /// <summary>G4.4: a spring launch counts as a try; it succeeds if the bot stands on an island within 2 s without falling.</summary>
        private static void TrackSprings(SimCore core, HazardSet hz, uint[] springAt, ref int tries, ref int ok)
        {
            var t = hz.Time(core.Tick);
            for (var i = 0; i < SimCore.MaxSlots; i++)
            {
                if (!core.Slots[i].Active)
                    continue;
                ref readonly var s = ref core.Slots[i].State;
                var step = core.Slots[i].LastStep;
                if ((step & PlayerStep.Result.Launched) != 0 && springAt[i] == 0)
                {
                    springAt[i] = core.Tick;
                    tries++;
                    if (SpringTrace)
                        Debug.Log($"[qa-spring] LAUNCH slot={i} t={t} at ({s.PosX},{s.PosY},{s.PosZ}) v=({s.VelX},{s.VelY},{s.VelZ}) r={(int)Math.Sqrt((double)s.PosX * s.PosX + (double)s.PosZ * s.PosZ)}");
                    continue;
                }

                if (springAt[i] == 0)
                    continue;
                if (SpringTrace && (core.Tick - springAt[i]) % 3 == 0)
                    Debug.Log($"[qa-spring]   fly slot={i} age={core.Tick - springAt[i]} at ({s.PosX},{s.PosY},{s.PosZ}) v=({s.VelX},{s.VelY},{s.VelZ}) g={(s.Has(MotorState.Grounded) ? 1 : 0)} surf={s.Surface} step={step}");
                if ((step & PlayerStep.Result.FellOut) != 0 || s.Has(MotorState.Respawning) || core.Tick - springAt[i] > 60)
                {
                    if (SpringTrace)
                        Debug.Log($"[qa-spring] FAIL slot={i} age={core.Tick - springAt[i]} at ({s.PosX},{s.PosY},{s.PosZ}) r={(int)Math.Sqrt((double)s.PosX * s.PosX + (double)s.PosZ * s.PosZ)}");
                    springAt[i] = 0;
                    continue;
                }

                if (!s.Has(MotorState.Grounded) || core.Tick - springAt[i] < 5)
                    continue;
                for (var k = 0; k < hz.Platforms.Length; k++)
                {
                    ref readonly var pl = ref hz.Platforms[k];
                    HazardMath.PlatformCenterMm(in pl, t, out var cx, out var cy, out var cz);
                    if (Math.Abs(s.PosX - cx) <= pl.HalfXMm + 50 && Math.Abs(s.PosZ - cz) <= pl.HalfZMm + 50 && s.PosY >= cy)
                    {
                        ok++;
                        break;
                    }
                }

                if (SpringTrace)
                {
                    var rr = (int)Math.Sqrt((double)s.PosX * s.PosX + (double)s.PosZ * s.PosZ);
                    var on = false;
                    for (var k = 0; k < hz.Platforms.Length; k++)
                    {
                        ref readonly var pl = ref hz.Platforms[k];
                        HazardMath.PlatformCenterMm(in pl, t, out var cx, out var cy, out var cz);
                        on |= Math.Abs(s.PosX - cx) <= pl.HalfXMm + 50 && Math.Abs(s.PosZ - cz) <= pl.HalfZMm + 50 && s.PosY >= cy;
                    }

                    Debug.Log($"[qa-spring] LAND {(on ? "island" : "MISS")} slot={i} age={core.Tick - springAt[i]} at ({s.PosX},{s.PosY},{s.PosZ}) r={rr}");
                }

                springAt[i] = 0; // landed (on an island or not)
            }
        }

        /// <summary>Survival constraint: one reference bot alone on route index r of every side; min goo margin (s) and the escape time.</summary>
        private static readonly bool TraceArg = System.Array.IndexOf(System.Environment.GetCommandLineArgs(), "-fallrushQaTrace") >= 0;

        public static (bool escaped, float minMarginS, float climbS, int gooed) RouteProbe(MapInfo map, CapsuleCollider probe, int variant, int route)
        {
            var minMargin = int.MaxValue;
            var last = default(MotorState);
            var st = RunOne(map, probe, variant, 0, 9000 + route, BotTier.Reference, bots: 1, forcedRoute: route, perTick: (core, rules, hz) =>
            {
                if (rules.Phase != RoundPhase.Rush || hz.Goo.Length == 0 || !(rules is TimedRoundRules tr) || !tr.InPlay(0))
                    return;
                if (!core.Slots[0].State.Has(MotorState.Respawning))
                    last = core.Slots[0].State;
                ref readonly var q = ref core.Slots[0].State;
                if (TraceArg && q.PosY > 12000 && q.PosY < 18000 && q.PosX > -4500 && q.PosX < 4500 && q.PosZ < -11000)
                    UnityEngine.Debug.Log($"[qa-trace] r={route} tick={core.Tick} {q}");
                var t = hz.Time(core.Tick);
                var reach = HazardMath.GooTicksToReach(in hz.Goo[0], core.Slots[0].State.PosY);
                if (reach - t < minMargin)
                    minMargin = reach - t;
            });
            if (st.Escaped != 1)
                UnityEngine.Debug.Log($"[qa] route probe variant={variant} route={route} last alive state {last}");
            return (st.Escaped == 1, minMargin == int.MaxValue ? float.NaN : minMargin / 30f, st.Duration, st.Goo);
        }

        public static void RunAll(MapInfo map, int runs)
        {
            var sw = Stopwatch.StartNew();
            var probe = PhysxMotorWorld.CreateProbe(map.transform, MovementConfig.Default.Radius, MovementConfig.Default.Height);
            var mods = map.definition.allowedModifiers;
            // G4.2: -fallrushQAMod <id> limits the run to one modifier (tuning aid).
            var cl = Environment.GetCommandLineArgs();
            var mi = Array.IndexOf(cl, "-fallrushQAMod");
            if (mi >= 0 && mi + 1 < cl.Length && int.TryParse(cl[mi + 1], out var only))
                mods = new[] { only };
            var mode = map.definition.mode;
            var tiers = TierArg() switch { "reference" => new[] { BotTier.Reference }, "normal" => new[] { BotTier.Normal }, _ => new[] { BotTier.Reference, BotTier.Normal } };
            var target = map.definition.targetDuration;
            var md = new StringBuilder();
            var allOk = true;
            var nBots = BotsArg(mode);
            md.Append($"### Map QA: {map.gameObject.scene.name} ({mode}, map hash {MapContent.Short(map.ContentHash)}), {runs} runs per variant × modifier × tier, {nBots} BotBrain bots (the players this round has in a 12-player match; target {Math.Min(nBots, TargetFor(mode))})\n\n");
            md.Append("| Tier | Variant | Modifier | Runs | Valid result | Soft-locks | Qualified (mean) | Median / p90 duration (s) | Timer expiries | ");
            md.Append(mode switch { "Survival" => "Escaped / goo / fell (mean) |", "Skill" => "Pickups / drops (mean) · score check |", "Final" => "One champion · fell (mean) |", _ => "Falls / hits (mean) |" });
            md.Append(" Stuck re-acquires / run |\n|---|---|---|---|---|---|---|---|---|---|---|\n");
            var medians = new Dictionary<BotTier, List<float>>();
            var skillRows = new StringBuilder();
            var skillOk = true;
            var lidRows = new StringBuilder();
            var lidOk = true;
            var lidSafe = true;
            foreach (var tier in tiers)
            {
                medians[tier] = new List<float>();
                foreach (var v in new[] { 0, 1 })
                    foreach (var m in mods)
                    {
                        var durs = new List<float>();
                        int valid = 0, soft = 0, timers = 0, q = 0, esc = 0, goo = 0, fell = 0, pick = 0, drop = 0, scoreBad = 0, champOk = 0, falls = 0, hits = 0, stuck = 0;
                        int tablePicks = 0, gold = 0, goldSpawned = 0, springTries = 0, springOk = 0, close = 0, fallsMax = 0, exact3 = 0, third = 0;
                        var thirds = new List<int>();
                        var lidRuns = new List<Stats>();
                        for (var r = 0; r < runs; r++)
                        {
                            var st = RunOne(map, probe, v, m, r, tier, nBots);
                            if (!st.Over)
                            {
                                soft++;
                                continue;
                            }

                            if (st.Qualified <= st.Target && st.Order > 0 && st.ChampionOk)
                                valid++;
                            durs.Add(st.Duration);
                            medians[tier].Add(st.Duration);
                            timers += st.TimerExpired ? 1 : 0;
                            q += st.Qualified;
                            esc += st.Escaped;
                            goo += st.Goo;
                            fell += st.FellOut;
                            pick += st.Pickups;
                            drop += st.Drops;
                            scoreBad += st.ScoreSum != st.ScoreExpected ? 1 : 0;
                            champOk += st.ChampionOk ? 1 : 0;
                            falls += st.Falls;
                            hits += st.Hits;
                            stuck += st.Stuck;
                            if (st.Lid != null)
                                lidRuns.Add(st);
                            if (mode == "Skill")
                            {
                                tablePicks += st.TablePicks;
                                gold += st.GoldenCollected;
                                goldSpawned += st.GoldenSpawned;
                                springTries += st.SpringTries;
                                springOk += st.SpringOk;
                                fallsMax = Math.Max(fallsMax, st.FallsMaxBot);
                                exact3 += st.Qualified == Math.Min(nBots, TargetFor(mode)) ? 1 : 0;
                                if (st.Scores != null && st.Scores.Length >= 4 && st.Scores[2] - st.Scores[3] <= 2)
                                    close++;
                                if (st.Scores != null && st.Scores.Length >= 3)
                                    thirds.Add(st.Scores[2]);
                            }
                        }

                        durs.Sort();
                        var med = durs.Count > 0 ? durs[durs.Count / 2] : float.NaN;
                        var p90 = durs.Count > 0 ? durs[Math.Min(durs.Count - 1, (int)(durs.Count * 0.9))] : float.NaN;
                        var n = Math.Max(1, runs - soft);
                        var extra = mode switch
                        {
                            "Survival" => $"{(float)esc / n:F1} / {(float)goo / n:F1} / {(float)fell / n:F1}",
                            "Skill" => $"{(float)pick / n:F1} / {(float)drop / n:F1} · {(scoreBad == 0 ? "OK" : $"{scoreBad} BAD")}",
                            "Final" => $"{champOk}/{n} · {(float)fell / n:F1}",
                            _ => $"{(float)falls / n:F1} / {(float)hits / n:F1}",
                        };
                        md.Append($"| {tier} | {(v == 0 ? "A" : "B")} | {ModifierEffects.Get(m).Key} | {runs} | {valid}/{runs} | {soft} | {(float)q / n:F2} | {med:F1} / {p90:F1} | {timers} | {extra} | {(float)stuck / n:F1} |\n");
                        if (valid != runs || soft > 0 || scoreBad > 0)
                            allOk = false;
                        if (mode == "Skill")
                        {
                            thirds.Sort();
                            var share = pick > 0 ? 100f * tablePicks / pick : 0f;
                            var dropsPerBot = (float)drop / n / nBots;
                            var fallsPerBot = (float)falls / n / nBots;
                            var goldPct = goldSpawned > 0 ? 100f * gold / goldSpawned : 0f;
                            var springPct = springTries > 0 ? 100f * springOk / springTries : float.NaN;
                            var closePct = 100f * close / n;
                            var dropOk = tier == BotTier.Reference ? dropsPerBot <= 2f : dropsPerBot >= 1f && dropsPerBot <= 4f;
                            var springGate = tier != BotTier.Reference || springTries == 0 || springPct >= 70f;
                            var fallGate = tier != BotTier.Reference || fallsPerBot <= 2f;
                            var cellOk = exact3 == n && share >= 60f && dropOk && goldPct >= 50f && springGate && closePct >= 40f && fallGate;
                            skillOk &= cellOk;
                            skillRows.Append($"| {tier} | {(v == 0 ? "A" : "B")} | {ModifierEffects.Get(m).Key} | {exact3}/{n} | {dropsPerBot:F2} | {share:F0} % | {gold}/{goldSpawned} ({goldPct:F0} %) | {springOk}/{springTries} ({springPct:F0} %) | {closePct:F0} % | {fallsPerBot:F2} (max {fallsMax}) | {(thirds.Count > 0 ? thirds[thirds.Count / 2] : 0)} | {(cellOk ? "met" : "**not met**")} |\n");
                            Debug.Log($"[qa-skill] tier={tier} v={(v == 0 ? "A" : "B")} mod={ModifierEffects.Get(m).Key} exact3={exact3}/{n} drops/bot={dropsPerBot:F2} table={share:F0}% gold={gold}/{goldSpawned} spring={springOk}/{springTries} close={closePct:F0}% falls/bot={fallsPerBot:F2} max={fallsMax} 3rd median={(thirds.Count > 0 ? thirds[thirds.Count / 2] : 0)} {(cellOk ? "met" : "NOT MET")}");
                        }
                        if (lidRuns.Count > 0)
                        {
                            var (row, ok) = LidRow(tier, v, m, lidRuns, med, p90, timers, n);
                            lidRows.Append(row);
                            if (tier != BotTier.Reference)
                                lidOk &= ok;
                            lidSafe &= LidSafeOk(lidRuns);
                        }

                        Debug.Log($"[qa] {mode} tier={tier} variant={(v == 0 ? "A" : "B")} modifier={ModifierEffects.Get(m).Key} valid={valid}/{runs} soft={soft} median={med:F1}s p90={p90:F1}s timers={timers} q={(float)q / n:F2} {extra} elapsed={sw.Elapsed.TotalSeconds:F0}s");
                    }
            }

            md.Append('\n');
            foreach (var kv in medians)
            {
                kv.Value.Sort();
                var med = kv.Value.Count > 0 ? kv.Value[kv.Value.Count / 2] : float.NaN;
                var inTarget = med >= target.x && med <= target.y;
                md.Append($"- {kv.Key} tier: overall median **{med:F1} s** (GDD §4.1 target {target.x}–{target.y} s): **{(inTarget ? "within target" : "outside the target")}**\n");
            }

            if (mode == "Skill")
            {
                md.Append("\n#### Star Fountain targets (G4.4 §3c)\n\n| Tier | Variant | Modifier | Exactly 3 qualify | Drops / bot | Pickups on the table | Golden collected | Spring → island | 3rd–4th ≤ 2 pts | Falls / bot | 3rd-place median | Targets |\n|---|---|---|---|---|---|---|---|---|---|---|---|\n");
                md.Append(skillRows);
                md.Append("\nTargets: exactly 3 qualify; ≥ 60 % of pickups on the turntable; drops per bot 1–4 (normal), ≤ 2 (reference); golden ≥ 50 % collected; reference spring → island ≥ 70 %; 3rd and 4th within 2 points in ≥ 40 % of runs; reference ≤ 2 falls per bot.\n");
                md.Append($"\n**Star Fountain targets {(skillOk ? "MET" : "NOT MET")}** (every cell)\n");
                if (CampArg())
                    md.Append(CamperTable(map, probe, Math.Max(10, runs / 2)));
            }

            if (lidRows.Length > 0)
            {
                md.Append("\n#### Lid Flip targets (G4.5 §3c)\n\n| Tier | Variant | Modifier | Median / p90 (s) | Timer | Outs / run: top-miss · lower · knock-involved | 1st / 2nd out median (s) | Same-tick: any · last pair | Lid falls fair (yellow ≥ 1 s) | Knock / own-move falls | Second chance | Lower-shelf stay mean (s) | Inner floor (min solid) | Targets |\n|---|---|---|---|---|---|---|---|---|---|---|---|---|---|\n");
                md.Append(lidRows);
                md.Append("\nTargets (normal tier; the reference tier is reported): median 60–100 s, p90 < 115 s; timer ≤ 5 %; the last two never out in the same tick in ≥ 98 %; every lid fall preceded by ≥ 1 s of yellow (or magenta) on that lid; ≥ 70 % of top falls from over the lower shelf land on it; lower-shelf stay ≥ 8 s on average; inner floor ≥ 5 solid at every tick (both grids, every tier).\n");
                md.Append($"\n**Lid Flip targets {(lidOk ? "MET" : "NOT MET")}** (normal tier, every cell); fairness + inner floor (every tier): **{(lidSafe ? "PASS" : "FAIL")}**\n");
                if (!lidSafe)
                    allOk = false;
            }

            if (mode == "Survival")
            {
                // G4.3: sides differ in route count; probe every index up to the largest (a side without index r takes route 0).
                var perSide = new Dictionary<int, int>();
                foreach (var br in map.botRoutes)
                    perSide[br.Section] = perSide.TryGetValue(br.Section, out var c) ? c + 1 : 1;
                var maxIdx = 1;
                foreach (var kv in perSide)
                    maxIdx = Math.Max(maxIdx, kv.Value);
                md.Append("\n| Variant | Route index (every side; route 0 where a side has fewer) | Escaped alone | Min goo margin (s) | Climb (s) |\n|---|---|---|---|---|\n");
                foreach (var v in new[] { 0, 1 })
                    for (var r = 0; r < maxIdx; r++)
                    {
                        var (escaped, margin, climb, gooed) = RouteProbe(map, probe, v, r);
                        md.Append($"| {(v == 0 ? "A" : "B")} | {r} | {(escaped ? "yes" : "NO")} | {margin:F1} | {climb:F1} |\n");
                        if (!escaped || gooed > 0 || !(margin > 0f))
                            allOk = false;
                        Debug.Log($"[qa] route probe variant={(v == 0 ? "A" : "B")} route={r} escaped={escaped} minMargin={margin:F1}s climb={climb:F1}s gooed={gooed}");
                    }

                md.Append("\nThe goo constraint (doc 08 §5): a reference bot alone on each route index keeps a positive margin above the goo all the way to the summit.\n");
            }

            // Determinism: run 0 twice per variant.
            var det = true;
            foreach (var v in new[] { 0, 1 })
            {
                var a = RunOne(map, probe, v, mods[0], 0, tiers[tiers.Length - 1], nBots);
                var b = RunOne(map, probe, v, mods[0], 0, tiers[tiers.Length - 1], nBots);
                if (a.Result != b.Result)
                {
                    det = false;
                    Debug.LogError($"[qa] NOT DETERMINISTIC variant {v}: {a.Result} vs {b.Result}");
                }
            }

            if (!det)
                allOk = false;
            md.Append($"- Deterministic for a run seed: **{(det ? "yes" : "NO")}**\n- Wall time: {sw.Elapsed.TotalSeconds:F0} s\n\n**QA {(allOk ? "PASS" : "FAIL")}** (valid result every run, no soft-lock, qualified ≤ target{(mode == "Final" ? ", one champion" : mode == "Skill" ? ", scores = pickups − drops" : mode == "Survival" ? ", goo never overtakes a route" : "")}, determinism)\n");
            var path = OutPath();
            File.WriteAllText(path, md.ToString());
            Debug.Log($"[qa] {(allOk ? "PASS" : "FAIL")} wall={sw.Elapsed.TotalSeconds:F0}s → {path}\n{md}");
        }

        private static bool LidSafeOk(List<Stats> runs)
        {
            foreach (var r in runs)
                if (r.Lid.FallsOpenedUnfair > 0 || r.Lid.InnerFloorViolations > 0)
                    return false;
            return true;
        }

        private static float Median(List<float> x)
        {
            if (x.Count == 0)
                return float.NaN;
            x.Sort();
            return x[x.Count / 2];
        }

        /// <summary>G4.5: one Lid Flip row (a tier × variant × modifier cell) and whether it meets the §3c targets.</summary>
        private static (string row, bool ok) LidRow(BotTier tier, int v, int m, List<Stats> runs, float med, float p90, int timers, int n)
        {
            int top = 0, low = 0, knock = 0, sameAny = 0, sameLast = 0, fair = 0, unfair = 0, kf = 0, self = 0, inFoot = 0, caught = 0, viol = 0, minInner = 9;
            var first = new List<float>();
            var second = new List<float>();
            var stay = new List<float>();
            var notes = new List<string>();
            foreach (var r in runs)
            {
                var l = r.Lid;
                top += l.OutFromTop;
                low += l.OutFromLow;
                knock += l.OutKnockInvolved;
                sameAny += l.SameTickAny ? 1 : 0;
                sameLast += l.LastTwoSameTick(r.Alive) ? 1 : 0;
                fair += l.FallsOpenedFair;
                unfair += l.FallsOpenedUnfair;
                kf += l.FallsKnock;
                self += l.FallsSelf;
                inFoot += l.TopFallsInFoot;
                caught += l.TopFallsInFootCaught;
                viol += l.InnerFloorViolations;
                minInner = Math.Min(minInner, l.MinInnerSolidSeen);
                var outs = new List<float>(l.OutTimes);
                outs.Sort();
                if (outs.Count > 0)
                    first.Add(outs[0]);
                if (outs.Count > 1)
                    second.Add(outs[1]);
                stay.AddRange(l.LowerStay);
                foreach (var u in l.UnfairNotes)
                    if (notes.Count < 3)
                        notes.Add(u);
            }

            var cnt = Math.Max(1, runs.Count);
            var timerPct = 100f * timers / Math.Max(1, n);
            var lastPct = 100f * sameLast / cnt;
            var catchPct = inFoot > 0 ? 100f * caught / inFoot : float.NaN;
            var stayMean = 0f;
            foreach (var x in stay)
                stayMean += x;
            stayMean = stay.Count > 0 ? stayMean / stay.Count : float.NaN;
            var ok = med >= 60f && med <= 100f && p90 < 115f && timerPct <= 5f && lastPct <= 2f && unfair == 0 && (inFoot == 0 || catchPct >= 70f) && (stay.Count == 0 || stayMean >= 8f) && viol == 0;
            var row = $"| {tier} | {(v == 0 ? "A" : "B")} | {ModifierEffects.Get(m).Key} | {med:F1} / {p90:F1} | {timerPct:F0} % | {(float)top / cnt:F2} · {(float)low / cnt:F2} · {(float)knock / cnt:F2} | {Median(first):F1} / {Median(second):F1} | {100f * sameAny / cnt:F0} % · {lastPct:F0} % | {fair}/{fair + unfair}{(notes.Count > 0 ? " (" + string.Join("; ", notes) + ")" : "")} | {kf} / {self} | {caught}/{inFoot} ({catchPct:F0} %) | {stayMean:F1} | {viol} ({minInner}) | {(ok ? "met" : "**not met**")} |\n";
            Debug.Log($"[qa-lid] tier={tier} v={(v == 0 ? "A" : "B")} mod={ModifierEffects.Get(m).Key} median={med:F1} p90={p90:F1} timer={timerPct:F0}% outs top={top} low={low} knock={knock} 1st={Median(first):F1} 2nd={Median(second):F1} sameAny={sameAny} sameLast={sameLast} fair={fair}/{fair + unfair} knockFalls={kf} self={self} catch={caught}/{inFoot} stay={stayMean:F1} viol={viol} minInner={minInner} {(ok ? "met" : "NOT MET")}");
            return (row, ok);
        }

        private static bool CampArg() => Array.IndexOf(Environment.GetCommandLineArgs(), "-fallrushQaCamp") >= 0;

        /// <summary>
        /// G4.4 camper probe: one reference bot that never leaves a spot (slot 0) + 4 normal bots, modifier None, both
        /// variants; its median score vs the 3rd-place median of the same rounds. Every spot must score below 3rd place.
        /// </summary>
        public static string CamperTable(MapInfo map, CapsuleCollider probe, int runs)
        {
            var sb = new StringBuilder();
            sb.Append($"\n#### Camper probe ({runs} runs per spot × variant, 1 reference camper + 4 normal bots, modifier None)\n\n| Spot | Variant | Camper score median (mean) | 3rd-place median | Camper qualified | Below 3rd |\n|---|---|---|---|---|---|\n");
            var all = true;
            for (var spot = 0; spot < CampSpots.Length; spot++)
                foreach (var v in new[] { 0, 1 })
                {
                    var cs = new List<int>();
                    var th = new List<int>();
                    var qual = 0;
                    for (var r = 0; r < runs; r++)
                    {
                        var st = RunOne(map, probe, v, 0, 5000 + r, BotTier.Normal, 5, campSpot: spot);
                        if (st.Scores == null || st.Scores.Length < 3)
                            continue;
                        cs.Add(st.Slot0Score);
                        th.Add(st.Scores[2]);
                        qual += st.Result != null && st.Slot0Score >= st.Scores[2] ? 1 : 0;
                    }

                    cs.Sort();
                    th.Sort();
                    var cm = cs.Count > 0 ? cs[cs.Count / 2] : 0;
                    var tm = th.Count > 0 ? th[th.Count / 2] : 0;
                    var mean = 0.0;
                    foreach (var x in cs)
                        mean += x;
                    mean /= Math.Max(1, cs.Count);
                    var ok = cm < tm;
                    all &= ok;
                    sb.Append($"| {CampSpots[spot].name} | {(v == 0 ? "A" : "B")} | {cm} ({mean:F1}) | {tm} | {qual}/{cs.Count} (score ≥ 3rd) | {(ok ? "yes" : "**NO**")} |\n");
                    Debug.Log($"[qa-camp] {CampSpots[spot].name} v={(v == 0 ? "A" : "B")} camper={cm} ({mean:F1}) third={tm} q={qual}/{cs.Count} {(ok ? "below" : "NOT BELOW")}");
                }

            sb.Append($"\n**Camper probe {(all ? "PASS" : "FAIL")}** (every spot below the 3rd-place median)\n");
            return sb.ToString();
        }

        private static string OutPath()
        {
            var args = Environment.GetCommandLineArgs();
            for (var i = 0; i < args.Length - 1; i++)
                if (args[i] == "-fallrushQAOut")
                    return args[i + 1];
            return "map-qa.md";
        }
    }
}
