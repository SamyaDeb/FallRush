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
        }

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

        public static Stats RunOne(MapInfo map, CapsuleCollider probe, int variant, int modifier, int run, BotTier tier, int bots = 12, int forcedRoute = -1, Action<SimCore, IRoundRules, HazardSet> perTick = null, Action<BotBrainSet> onBots = null)
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
            core.BotInput = set.Input;
            var rush = rules.Begin(core, core.Tick);
            hz.RoundStartTick = rush;
            var cap = rush + rules.TimerTicks + 120;
            var trace = Environment.GetEnvironmentVariable("FALLRUSH_QA_TRACE") == "1";
            while (!rules.IsOver && core.Tick < cap)
            {
                core.Step();
                perTick?.Invoke(core, rules, hz);
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
            }

            // Final: exactly one champion once the director's minimum-advance is applied (0 qualified → the top-ranked).
            st.ChampionOk = mode != "Final" || (st.Qualified <= 1 && res.Order.Length > 0);
            return st;
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
            foreach (var tier in tiers)
            {
                medians[tier] = new List<float>();
                foreach (var v in new[] { 0, 1 })
                    foreach (var m in mods)
                    {
                        var durs = new List<float>();
                        int valid = 0, soft = 0, timers = 0, q = 0, esc = 0, goo = 0, fell = 0, pick = 0, drop = 0, scoreBad = 0, champOk = 0, falls = 0, hits = 0, stuck = 0;
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

            if (mode == "Survival")
            {
                md.Append("\n| Variant | Route index (every side) | Escaped alone | Min goo margin (s) | Climb (s) |\n|---|---|---|---|---|\n");
                foreach (var v in new[] { 0, 1 })
                    for (var r = 0; r < 2; r++)
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
