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
    /// Automated map QA (doc 08 §9, M3 §5.4), headless on the server build: the loaded race map × variant A/B × each
    /// allowed modifier × N runs, 12 waypoint bots each (random start slots and route choices from a run seed), the real
    /// <see cref="SimCore"/> + <see cref="RaceRules"/> + PhysX world, as fast as the CPU allows (no networking).
    /// <para>Asserts: no soft-lock (every bot finishes or is timed out with a logged reason), a finish is reachable on every
    /// route, no tick where every door is closed, exactly N qualify, the median duration is within the target, and the
    /// result is deterministic for a run seed (re-run check). Writes a markdown table.</para>
    /// <code>FallRushDevServer -batchmode -nographics -fallrushQA 100 -fallrushMap BlockPartyDash [-fallrushQAOut path]</code>
    /// </summary>
    public static class MapQa
    {
        public const string Arg = "-fallrushQA";
        /// <summary>Log every fall (where, which route / waypoint) — tuning aid.</summary>
        public static bool Trace;

        public struct RunStats
        {
            public int Variant, Modifier, Run;
            public float Duration;      // RUSH → end, seconds
            public float FirstFinish;   // best line, seconds
            public int Finishers, Qualified, QualifyCount;
            public int Falls, Hits, Stuck, SoftLocks, Rejected;
            public bool TimerExpired;
            public string Result;
            public string SoftLockReasons;
        }

        public static int Runs()
        {
            var args = Environment.GetCommandLineArgs();
            for (var i = 0; i < args.Length - 1; i++)
                if (string.Equals(args[i], Arg, StringComparison.OrdinalIgnoreCase) && int.TryParse(args[i + 1], out var n))
                    return n;
            return 0;
        }

        private static string OutPath()
        {
            var args = Environment.GetCommandLineArgs();
            for (var i = 0; i < args.Length - 1; i++)
                if (args[i] == "-fallrushQAOut")
                    return args[i + 1];
            return "map-qa.md";
        }

        /// <summary>One race with 12 waypoint bots; returns its stats. <paramref name="routeTally"/> counts [section][route] starts/finishes.</summary>
        public static RunStats RunOne(MapInfo map, CapsuleCollider probe, int variant, int modifier, int run, int[,] routeStarts = null, int[,] routeFinishes = null)
        {
            var seed = (ulong)(0x5EED0000 + variant * 1000003 + modifier * 10007 + run * 7919);
            var p = RoundParams.FromDevSeed(map.MapId, seed, variant, modifier);
            var cfg = MovementConfig.For(modifier);
            var world = map.CreateWorld(probe, cfg, in p, out var hz);
            // Random start slots: permute the spawns by the run seed.
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
            var def = map.definition;
            var rules = new RaceRules(map.CheckpointDefs(), map.CourseLineMm(), (uint)(def.timerCapSeconds * 30))
            {
                IntroTicks = Mathf.RoundToInt(def.introSeconds * 30f),
                CountdownTicks = Mathf.RoundToInt(def.countdownSeconds * 30f),
            };
            core.Rules = rules;
            for (var i = 0; i < SimCore.MaxSlots; i++)
                core.AddBot();
            var bots = new WaypointBot[SimCore.MaxSlots];
            for (var i = 0; i < SimCore.MaxSlots; i++)
                bots[i] = new WaypointBot(i, map.botRoutes, hz, cfg, (uint)seed + (uint)i * 7u);
            core.BotInput = (slot, tick, s) => bots[slot].Next(tick, in s, rules.Racers[slot].Checkpoint);

            var rush = rules.Begin(core, core.Tick);
            hz.RoundStartTick = rush;
            var cap = rush + rules.TimerTicks + 60;
            var lastCp = new int[SimCore.MaxSlots];
            var lastProgress = new uint[SimCore.MaxSlots];
            var prev = new MotorState[SimCore.MaxSlots];
            var lastGround = new MotorState[SimCore.MaxSlots];
            var hist = new MotorState[SimCore.MaxSlots, 12];
            while (!rules.IsOver && core.Tick < cap)
            {
                for (var i = 0; i < SimCore.MaxSlots; i++)
                    prev[i] = core.Slots[i].State;
                core.Step();
                if (Trace)
                    for (var i = 0; i < SimCore.MaxSlots; i++)
                        if (core.Slots[i].State.Has(MotorState.Respawning) && !prev[i].Has(MotorState.Respawning))
                            Debug.Log($"[qa-trace] t={(int)(core.Tick - rush)} slot{i} fell; {bots[i].CurrentRouteName}#{bots[i].Waypoint} cp={rules.Racers[i].Checkpoint}");
                if (Trace)
                    for (var i = 0; i < SimCore.MaxSlots; i++)
                    {
                        var cur = core.Slots[i].State;
                        if (cur.Has(MotorState.Grounded))
                            lastGround[i] = cur;
                        if (cur.Has(MotorState.Respawning) && !prev[i].Has(MotorState.Respawning))
                            Debug.Log($"[qa-trace]   slot{i} last grounded at ({lastGround[i].PosX},{lastGround[i].PosY},{lastGround[i].PosZ}) v=({lastGround[i].VelX},{lastGround[i].VelY},{lastGround[i].VelZ}) surf={lastGround[i].Surface}; 10 ticks before fall-out at ({hist[i, (core.Tick + 1) % 12].PosX},{hist[i, (core.Tick + 1) % 12].PosY},{hist[i, (core.Tick + 1) % 12].PosZ})");
                        hist[i, core.Tick % 12] = cur;
                    }
                for (var i = 0; i < SimCore.MaxSlots; i++)
                {
                    if (rules.Racers[i].Checkpoint != lastCp[i])
                    {
                        if (routeFinishes != null && lastCp[i] < routeFinishes.GetLength(0))
                            routeFinishes[lastCp[i], Math.Min(routeFinishes.GetLength(1) - 1, bots[i].RouteTaken[lastCp[i]])]++;
                        lastCp[i] = rules.Racers[i].Checkpoint;
                        lastProgress[i] = core.Tick;
                    }
                }
            }

            if (routeStarts != null)
                for (var i = 0; i < SimCore.MaxSlots; i++)
                    for (var sec = 0; sec <= Math.Min(lastCp[i], routeStarts.GetLength(0) - 1); sec++)
                        if (sec < lastCp[i] || !rules.Racers[i].Finished)
                            routeStarts[sec, Math.Min(routeStarts.GetLength(1) - 1, bots[i].RouteTaken[sec])]++;

            var st = new RunStats { Variant = variant, Modifier = modifier, Run = run };
            var res = rules.Result;
            var reasons = new StringBuilder();
            for (var i = 0; i < SimCore.MaxSlots; i++)
            {
                st.Falls += core.FallsBySlot[i];
                st.Hits += core.HitsBySlot[i];
                st.Stuck += bots[i].Stuck;
                st.Rejected += rules.Racers[i].RejectedCheckpoints;
                if (rules.Racers[i].Finished)
                    continue;
                // Not finished: a soft-lock if it made no checkpoint progress for the last 40 s of the round.
                var since = (core.Tick - Math.Max(lastProgress[i], rush)) / 30f;
                if (since > 40f && res != null && res.TimerExpired)
                {
                    st.SoftLocks++;
                    reasons.Append($"slot{i}@cp{rules.Racers[i].Checkpoint} route={bots[i].CurrentRouteName}#{bots[i].Waypoint} stuck={bots[i].Stuck} last={bots[i].LastStuckReason}; ");
                }
            }

            st.SoftLockReasons = reasons.ToString();
            if (res != null)
            {
                st.Duration = (res.EndTick - res.RushTick) / 30f;
                st.QualifyCount = res.QualifyCount;
                st.TimerExpired = res.TimerExpired;
                st.FirstFinish = res.FinishTicks.Length > 0 && res.FinishTicks[0] > 0 ? (res.FinishTicks[0] - res.RushTick) / 30f : float.NaN;
                for (var k = 0; k < res.Order.Length; k++)
                {
                    if (res.FinishTicks[k] > 0)
                        st.Finishers++;
                    if (res.Qualified[k])
                        st.Qualified++;
                }

                st.Result = res.ToString();
            }
            else
            {
                st.Result = "no result (cap reached)";
                st.SoftLocks++;
            }

            return st;
        }

        public static void RunAll(MapInfo map, int runs)
        {
            var sw = Stopwatch.StartNew();
            var probe = PhysxMotorWorld.CreateProbe(map.transform, MovementConfig.Default.Radius, MovementConfig.Default.Height);
            var mods = map.definition.allowedModifiers;
            var sb = new StringBuilder();
            var allOk = true;
            var sections = 4;
            var maxRoutes = 6;
            var starts = new int[sections, maxRoutes];
            var finishes = new int[sections, maxRoutes];
            sb.Append($"| Variant | Modifier | Runs | Finish rate | Qualified = N | Median / p90 duration (s) | Best line median (s) | Timer expiries | Falls / run | Hits / run | Bot stuck re-acquires / run | Soft-locks |\n|---|---|---|---|---|---|---|---|---|---|---|---|\n");
            var allDur = new List<float>();
            var softLog = new StringBuilder();
            foreach (var v in new[] { 0, 1 })
            {
                foreach (var m in mods)
                {
                    var durs = new List<float>();
                    var best = new List<float>();
                    int fin = 0, total = 0, qOk = 0, falls = 0, hits = 0, stuck = 0, soft = 0, timers = 0;
                    for (var r = 0; r < runs; r++)
                    {
                        var st = RunOne(map, probe, v, m, r, starts, finishes);
                        durs.Add(st.Duration);
                        allDur.Add(st.Duration);
                        if (!float.IsNaN(st.FirstFinish))
                            best.Add(st.FirstFinish);
                        fin += st.Finishers;
                        total += SimCore.MaxSlots;
                        if (st.Qualified == st.QualifyCount && st.QualifyCount > 0)
                            qOk++;
                        falls += st.Falls;
                        hits += st.Hits;
                        stuck += st.Stuck;
                        soft += st.SoftLocks;
                        if (st.TimerExpired)
                            timers++;
                        if (st.SoftLocks > 0)
                            softLog.Append($"- {(v == 0 ? "A" : "B")}/{ModifierEffects.Get(m).Key} run {r}: {st.SoftLockReasons}\n");
                    }

                    durs.Sort();
                    best.Sort();
                    var med = durs[durs.Count / 2];
                    var p90 = durs[Math.Min(durs.Count - 1, (int)(durs.Count * 0.9))];
                    var bmed = best.Count > 0 ? best[best.Count / 2] : float.NaN;
                    sb.Append($"| {(v == 0 ? "A" : "B")} | {ModifierEffects.Get(m).Key} | {runs} | {100.0 * fin / total:F1} % | {qOk}/{runs} | {med:F1} / {p90:F1} | {bmed:F1} | {timers} | {(float)falls / runs:F1} | {(float)hits / runs:F1} | {(float)stuck / runs:F1} | {soft} |\n");
                    if (qOk != runs || soft > 0)
                        allOk = false;
                    Debug.Log($"[qa] variant={(v == 0 ? "A" : "B")} modifier={ModifierEffects.Get(m).Key} runs={runs} finishRate={100.0 * fin / total:F1}% qualifiedOk={qOk}/{runs} median={med:F1}s p90={p90:F1}s best={bmed:F1}s timers={timers} falls/run={(float)falls / runs:F1} hits/run={(float)hits / runs:F1} stuck/run={(float)stuck / runs:F1} softLocks={soft} elapsed={sw.Elapsed.TotalSeconds:F0}s");
                }
            }

            // Routes: every route must produce finishers of its section.
            var routeLines = new StringBuilder();
            for (var sec = 0; sec < sections; sec++)
            {
                var names = new List<string>();
                foreach (var br in map.botRoutes)
                    if (br.Section == sec)
                        names.Add(br.Name);
                for (var k = 0; k < names.Count && k < maxRoutes; k++)
                {
                    routeLines.Append($"| {sec} | {names[k]} | {starts[sec, k]} | {finishes[sec, k]} |\n");
                    if (finishes[sec, k] == 0)
                        allOk = false;
                }
            }

            // Doors: is there any tick where every door of the gate wall is closed (all routes through S4 blocked)?
            var blockedTicks = 0;
            foreach (var v in new[] { 0, 1 })
                foreach (var m in mods)
                {
                    var hz = map.BuildHazards(new RoundParams { MapId = map.MapId, Variant = (byte)v, Modifier = (byte)m }, out _);
                    var cycleDoors = new List<int>();
                    for (var i = 0; i < hz.Doors.Length; i++)
                        if (hz.Doors[i].Mode == 0)
                            cycleDoors.Add(i);
                    if (cycleDoors.Count == 0)
                        continue;
                    for (var t = 0; t < 2000; t++)
                    {
                        var open = false;
                        foreach (var i in cycleDoors)
                            open |= HazardMath.DoorOpen(in hz.Doors[i], t);
                        if (!open)
                            blockedTicks++;
                    }
                }

            if (blockedTicks > 0)
                allOk = false;

            // Determinism: the first run of each variant again → identical result.
            var det = true;
            foreach (var v in new[] { 0, 1 })
            {
                var a = RunOne(map, probe, v, mods[0], 0);
                var b = RunOne(map, probe, v, mods[0], 0);
                if (a.Result != b.Result)
                {
                    det = false;
                    Debug.LogError($"[qa] NOT DETERMINISTIC variant {v}: {a.Result} vs {b.Result}");
                }
            }

            if (!det)
                allOk = false;
            allDur.Sort();
            var overallMed = allDur[allDur.Count / 2];
            var md = new StringBuilder();
            md.Append($"### Map QA: {map.gameObject.scene.name} (map hash {MapContent.Short(map.ContentHash)}), {runs} runs per variant × modifier, 12 waypoint bots (reference speed)\n\n");
            md.Append(sb).Append('\n');
            md.Append("| Section | Route | Bots that took it | Reached the next checkpoint on it |\n|---|---|---|---|\n").Append(routeLines).Append('\n');
            md.Append($"- Ticks with every S4 door closed (2,000 ticks × each variant × modifier): **{blockedTicks}**\n");
            md.Append($"- Deterministic for a run seed (re-run of run 0, A and B): **{(det ? "yes" : "NO")}**\n");
            // Reference bots (no noise, perfect timing) run ~1.5–2× faster than humans: the bot target is 40–60 s so a human
            // round lands in the GDD's 75–110 s (human times UNVERIFIED until a playtest). Changed 2026-11-19 with the longer course.
            var inTarget = overallMed >= 40f && overallMed <= 60f;
            md.Append($"- Overall median duration: **{overallMed:F1} s** (reference-bot target 40–60 s ≈ humans 75–110 s): **{(inTarget ? "within target" : "NOT MET")}** — reference bots have no noise or mistakes (M4 adds them); human times are UNVERIFIED until a playtest\n");
            md.Append($"- Wall time: {sw.Elapsed.TotalSeconds:F0} s\n");
            if (softLog.Length > 0)
                md.Append("\nSoft-locks:\n").Append(softLog);
            md.Append($"\n**QA {(allOk ? "PASS" : "FAIL")}** (soft-locks, reachability per route, doors, N qualified, determinism){(inTarget ? "" : "; duration target NOT MET")}\n");
            File.WriteAllText(OutPath(), md.ToString());
            Debug.Log($"[qa] {(allOk ? "PASS" : "FAIL")} blockedDoorTicks={blockedTicks} deterministic={det} overallMedian={overallMed:F1}s wall={sw.Elapsed.TotalSeconds:F0}s → {OutPath()}\n{md}");
        }
    }
}
