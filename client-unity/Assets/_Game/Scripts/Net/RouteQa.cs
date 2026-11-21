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
    /// G4.0c route-cost QA (doc 08 §1 rule 4, §8 "route cost", §9), behind <c>-fallrushQaRoutes</c>, for race maps:
    /// <list type="number">
    /// <item><b>Timing</b>: 12 reference-tier <see cref="BotBrain"/> bots (no noise) race N runs per variant (modifier
    /// None). Every completed section traversal (checkpoint k → k+1; the start for section 0) is timed and binned by
    /// the route index the bot took in that section. Output: median seconds per (section, route) and the ratio to the
    /// section's fastest route. Target: the slowest valid route is 1.0–1.25× the fastest.</item>
    /// <item><b>Contacts</b>: 12 normal-tier bots, same runs. Per completed traversal: hazard hits
    /// (<see cref="PlayerStep.Result.HazardHit"/>), door push-outs, falls. A route with <b>no hazard contact</b> (hits +
    /// door pushes = 0) is flagged <b>FREE ROUTE</b>; falls are shown but do not count (falling off by yourself is not a
    /// hazard).</item>
    /// </list>
    /// Same sim as <see cref="ModeQa"/> (real <see cref="SimCore"/>, <see cref="RaceRules"/>, PhysX world, no networking).
    /// <code>FallRushDevServer -batchmode -nographics -fallrushQA 100 -fallrushQaRoutes -fallrushMap BlockPartyDash -fallrushQAOut route-cost.md</code>
    /// </summary>
    public static class RouteQa
    {
        public const string Arg = "-fallrushQaRoutes";

        public static bool Requested() => Array.IndexOf(Environment.GetCommandLineArgs(), Arg) >= 0;

        private const int MaxSections = 16, MaxRoutes = 16;

        private sealed class Tally
        {
            public readonly List<int>[,] Ticks = new List<int>[MaxSections, MaxRoutes];
            public readonly int[,] Traversals = new int[MaxSections, MaxRoutes];
            public readonly int[,] Hits = new int[MaxSections, MaxRoutes];
            public readonly int[,] Doors = new int[MaxSections, MaxRoutes];
            public readonly int[,] Falls = new int[MaxSections, MaxRoutes];

            public Tally()
            {
                for (var s = 0; s < MaxSections; s++)
                    for (var r = 0; r < MaxRoutes; r++)
                        Ticks[s, r] = new List<int>();
            }
        }

        /// <summary>One race; adds every completed section traversal to <paramref name="t"/>.</summary>
        private static void RunOne(MapInfo map, CapsuleCollider probe, int variant, int run, BotTier tier, Tally t)
        {
            BotBrainSet set = null;
            var cp = new int[SimCore.MaxSlots];
            var entry = new uint[SimCore.MaxSlots];
            var hits = new int[SimCore.MaxSlots];
            var doors = new int[SimCore.MaxSlots];
            var falls = new int[SimCore.MaxSlots];
            var started = false;
            ModeQa.RunOne(map, probe, variant, 0, run, tier, 12, onBots: s => set = s, perTick: (core, rules, hz) =>
            {
                if (!(rules is RaceRules race) || set == null)
                    return;
                if (rules.Phase != RoundPhase.Rush)
                    return;
                if (!started)
                {
                    started = true;
                    for (var i = 0; i < SimCore.MaxSlots; i++)
                        entry[i] = core.Tick - 1; // RUSH: section 0 starts
                }

                for (var i = 0; i < SimCore.MaxSlots; i++)
                {
                    if (!core.Slots[i].Active || set[i]?.Waypoints == null)
                        continue;
                    var step = core.Slots[i].LastStep;
                    if ((step & PlayerStep.Result.HazardHit) != 0) hits[i]++;
                    if ((step & PlayerStep.Result.DoorPushed) != 0) doors[i]++;
                    if ((step & (PlayerStep.Result.FellOut | PlayerStep.Result.Gooed)) != 0) falls[i]++;
                    var now = race.Racers[i].Checkpoint;
                    if (now == cp[i])
                        continue;
                    // Section cp[i] completed this tick (checkpoints are reached in order).
                    var sec = cp[i];
                    var route = Math.Min(MaxRoutes - 1, set[i].Waypoints.RouteTaken[Math.Min(sec, set[i].Waypoints.RouteTaken.Length - 1)]);
                    if (sec < MaxSections)
                    {
                        t.Ticks[sec, route].Add((int)(core.Tick - entry[i]));
                        t.Traversals[sec, route]++;
                        t.Hits[sec, route] += hits[i];
                        t.Doors[sec, route] += doors[i];
                        t.Falls[sec, route] += falls[i];
                    }

                    hits[i] = doors[i] = falls[i] = 0;
                    cp[i] = now;
                    entry[i] = core.Tick;
                }
            });
        }

        private static float Median(List<int> v)
        {
            if (v.Count == 0)
                return float.NaN;
            v.Sort();
            return v[v.Count / 2] / 30f;
        }

        public static void RunAll(MapInfo map, int runs)
        {
            var sw = Stopwatch.StartNew();
            var probe = PhysxMotorWorld.CreateProbe(map.transform, MovementConfig.Default.Radius, MovementConfig.Default.Height);
            var md = new StringBuilder();
            var key = map.definition != null ? map.definition.mapKey : map.gameObject.scene.name;
            md.Append($"### Route cost: {MapDisplayNames.OfKey(key)} (key {key}, map hash {MapContent.Short(map.ContentHash)}), {runs} runs per variant, modifier None, 12 BotBrain bots\n\n");
            md.Append("Timing = reference-tier bots (median section time per route, ratio to the section's fastest route; target 1.0–1.25×). ")
              .Append("Contacts = normal-tier bots, per 100 traversals of the route (hazard hits / door push-outs / falls). ")
              .Append("**FREE ROUTE** = no hazard contact (hits + door pushes = 0) in any traversal; falls alone do not count.\n\n");
            md.Append("| Map | Variant | Section | Route | Traversals (ref) | Median s | Ratio | Traversals (normal) | Hits / 100 | Door pushes / 100 | Falls / 100 | Flag |\n|---|---|---|---|---|---|---|---|---|---|---|---|\n");
            var free = new List<string>();
            var overRatio = new List<string>();
            foreach (var v in new[] { 0, 1 })
            {
                var refT = new Tally();
                var nrmT = new Tally();
                for (var r = 0; r < runs; r++)
                {
                    RunOne(map, probe, v, r, BotTier.Reference, refT);
                    RunOne(map, probe, v, r, BotTier.Normal, nrmT);
                }

                var hz = map.BuildHazards(new RoundParams { MapId = map.MapId, Variant = (byte)v }, out _);
                var routes = BotBrainSet.UsableRoutes(map.botRoutes, hz);
                for (var sec = 0; sec < MaxSections; sec++)
                {
                    var names = new List<string>();
                    foreach (var br in routes)
                        if (br.Section == sec)
                            names.Add(br.Name);
                    if (names.Count == 0)
                        continue;
                    var med = new float[names.Count];
                    var fastest = float.MaxValue;
                    for (var k = 0; k < names.Count && k < MaxRoutes; k++)
                    {
                        med[k] = Median(refT.Ticks[sec, k]);
                        if (!float.IsNaN(med[k]) && med[k] < fastest)
                            fastest = med[k];
                    }

                    for (var k = 0; k < names.Count && k < MaxRoutes; k++)
                    {
                        var ratio = fastest < float.MaxValue && !float.IsNaN(med[k]) ? med[k] / fastest : float.NaN;
                        var n = nrmT.Traversals[sec, k];
                        var per = n > 0 ? 100f / n : 0f;
                        var contacts = nrmT.Hits[sec, k] + nrmT.Doors[sec, k];
                        var flags = new List<string>();
                        if (n > 0 && contacts == 0)
                        {
                            flags.Add("**FREE ROUTE**");
                            free.Add($"{(v == 0 ? "A" : "B")} S{sec} {names[k]}");
                        }

                        if (!float.IsNaN(ratio) && ratio > 1.25f)
                        {
                            flags.Add("ratio > 1.25");
                            overRatio.Add($"{(v == 0 ? "A" : "B")} S{sec} {names[k]} ({ratio:F2}×)");
                        }

                        if (refT.Traversals[sec, k] == 0)
                            flags.Add("no ref traversal");
                        md.Append($"| {key} | {(v == 0 ? "A" : "B")} | {sec} | {names[k]} | {refT.Traversals[sec, k]} | {med[k]:F1} | {ratio:F2} | {n} | {nrmT.Hits[sec, k] * per:F1} | {nrmT.Doors[sec, k] * per:F1} | {nrmT.Falls[sec, k] * per:F1} | {string.Join(", ", flags)} |\n");
                    }
                }

                Debug.Log($"[qa-routes] variant={(v == 0 ? "A" : "B")} done elapsed={sw.Elapsed.TotalSeconds:F0}s");
            }

            md.Append($"\n- Free routes (no hazard contact): **{free.Count}**{(free.Count > 0 ? ": " + string.Join("; ", free) : "")}\n");
            md.Append($"- Routes slower than 1.25× the section's fastest: **{overRatio.Count}**{(overRatio.Count > 0 ? ": " + string.Join("; ", overRatio) : "")}\n");
            md.Append("- Section = checkpoints reached (0 = start → first checkpoint); route index = the bot's choice among that section's usable routes (`BotBrainSet.UsableRoutes`).\n");
            md.Append($"- Wall time: {sw.Elapsed.TotalSeconds:F0} s\n\n");
            var ok = free.Count == 0 && overRatio.Count == 0;
            md.Append($"**ROUTE COST {(ok ? "PASS" : "FAIL")}** (no free route; slowest route ≤ 1.25× the fastest per section)\n");
            var path = OutPath();
            File.WriteAllText(path, md.ToString());
            Debug.Log($"[qa-routes] {(ok ? "PASS" : "FAIL")} free={free.Count} overRatio={overRatio.Count} wall={sw.Elapsed.TotalSeconds:F0}s → {path}\n{md}");
        }

        private static string OutPath()
        {
            var args = Environment.GetCommandLineArgs();
            for (var i = 0; i < args.Length - 1; i++)
                if (args[i] == "-fallrushQAOut")
                    return args[i + 1];
            return "route-cost.md";
        }
    }
}
