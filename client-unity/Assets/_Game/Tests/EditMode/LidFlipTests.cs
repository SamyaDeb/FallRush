using System;
using System.Collections.Generic;
using FallRush.Client;
using FallRush.Net;
using FallRush.Shared;
using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace FallRush.Tests
{
    /// <summary>
    /// G4.5 Lid Flip (key <c>CrumbleShelf</c>, the Final, doc 08 §7): loads the real <c>Map_CrumbleShelf</c> scene and checks
    /// a full 3-bot Final (one champion, deterministic), the layout rules (two lid grids with distinct ids, the lower one
    /// inside the top one's footprint, the kill-Y below it, spawns on solid lids, no crumble grid, no in-world text), the
    /// fairness floor at every tick, the VRF claim (the hazard seed alone decides the lid pattern), the two-grid collider /
    /// view order, and the chain-facing identity (key, id, mode, variants, modifiers, 120 s timer unchanged).
    /// </summary>
    public class LidFlipTests
    {
        private static MapInfo Load()
        {
            var scene = EditorSceneManager.OpenScene("Assets/_Game/Scenes/Map_CrumbleShelf.unity", OpenSceneMode.Single);
            Physics.simulationMode = SimulationMode.Script;
            Physics.SyncTransforms();
            foreach (var root in scene.GetRootGameObjects())
            {
                var m = root.GetComponentInChildren<MapInfo>();
                if (m != null)
                    return m;
            }

            Assert.Fail("no MapInfo");
            return null;
        }

        private static HazardSet Hazards(MapInfo map, int variant, ulong seed = 0x5EEDu, int modifier = 0) =>
            map.BuildHazards(new RoundParams { MapId = map.MapId, Variant = (byte)variant, Modifier = (byte)modifier, HazardSeed = seed }, out _);

        private const int RoundTicks = 120 * 30;

        /// <summary>A full Final with the 3 players it has in a 12-player match (normal tier, run 0): one champion, deterministic.</summary>
        [Test]
        public void FullRound_3Bots_OneChampion_Deterministic([Values(0, 1)] int variant)
        {
            var map = Load();
            var probe = PhysxMotorWorld.CreateProbe(map.transform, MovementConfig.Default.Radius, MovementConfig.Default.Height);
            var st = ModeQa.RunOne(map, probe, variant, 0, 0, BotTier.Normal, 3);
            TestContext.WriteLine($"variant {variant}: {st.Result} duration={st.Duration:F1}s timer={st.TimerExpired} outs top={st.Lid?.OutFromTop} low={st.Lid?.OutFromLow}");
            Assert.That(st.Over, Is.True, "round ended");
            Assert.That(st.ChampionOk, Is.True, "exactly one champion");
            Assert.That(st.Qualified, Is.LessThanOrEqualTo(1));
            Assert.That(st.Order, Is.EqualTo(3));
            Assert.That(st.Lid, Is.Not.Null, "the lid QA tracker ran");
            Assert.That(st.Lid.FallsOpenedUnfair, Is.Zero, "every lid fall followed a full 1 s telegraph");
            Assert.That(st.Lid.InnerFloorViolations, Is.Zero);
            var again = ModeQa.RunOne(map, probe, variant, 0, 0, BotTier.Normal, 3);
            Assert.That(again.Result, Is.EqualTo(st.Result), "deterministic for a run seed");
        }

        /// <summary>Rule 6: readable without words. No world text anywhere in the scene (the old title and label are gone).</summary>
        [Test]
        public void Scene_HasNoInWorldText()
        {
            Load();
            var texts = new List<string>();
            foreach (var c in UnityEngine.Object.FindObjectsByType<Component>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                var n = c.GetType().Name;
                if (c is TextMesh || n.Contains("TextMeshPro") || n == "Text" || c.gameObject.name.StartsWith("Label_") || c.gameObject.name.StartsWith("Sign_"))
                    texts.Add($"{c.gameObject.name} ({n})");
            }

            Assert.That(texts, Is.Empty, string.Join(", ", texts));
        }

        /// <summary>The reactive crumble grid is the code fallback only (G4-2): not in this scene.</summary>
        [Test]
        public void Scene_HasNoCrumbleGrid()
        {
            var map = Load();
            Assert.That(UnityEngine.Object.FindObjectsByType<HazardCrumbleGrid>(FindObjectsInactive.Include, FindObjectsSortMode.None), Is.Empty);
            Assert.That(map.grids, Is.Empty);
            var hz = Hazards(map, 0);
            Assert.That(hz.Grids, Is.Empty);
            Assert.That(hz.Reactive, Is.Null, "no reactive state to replicate");
        }

        /// <summary>Two lid grids: 9 × 9 of 1.8 m at y 6 (the top shelf, first), 5 × 5 at y 0, distinct obstacle ids.</summary>
        [Test]
        public void TwoLidGrids_TopAndLower_DistinctIds([Values(0, 1)] int variant)
        {
            var map = Load();
            Assert.That(map.lidGrids.Length, Is.EqualTo(2));
            Assert.That(map.lidGrids[0].obstacleId, Is.EqualTo("cs_lids_top"));
            Assert.That(map.lidGrids[1].obstacleId, Is.EqualTo("cs_lids_low"));
            Assert.That(map.lidGrids[0].obstacleId, Is.Not.EqualTo(map.lidGrids[1].obstacleId));
            var hz = Hazards(map, variant);
            ref readonly var top = ref hz.LidGrids[0];
            ref readonly var low = ref hz.LidGrids[1];
            Assert.That(top.N, Is.EqualTo(9));
            Assert.That(top.LidMm, Is.EqualTo(1800));
            Assert.That(top.TopMm, Is.EqualTo(6000));
            Assert.That(low.N, Is.EqualTo(5));
            Assert.That(low.LidMm, Is.EqualTo(1800));
            Assert.That(low.TopMm, Is.EqualTo(0));
            Assert.That(top.Seed, Is.Not.EqualTo(low.Seed), "distinct ids → distinct per-obstacle seeds");
            Assert.That(top.LitTicks, Is.GreaterThanOrEqualTo(30), "the telegraph is never under 1 s");
            Assert.That(low.LitTicks, Is.GreaterThanOrEqualTo(30));
            foreach (var m in map.definition.allowedModifiers)
                Assert.That(map.Validate(new RoundParams { MapId = map.MapId, Variant = (byte)variant, Modifier = (byte)m }), Is.Empty, $"modifier {m}");
        }

        /// <summary>The lower shelf lies inside the top shelf's footprint (a fall from the inner rings can land on it) and the kill-Y is below it.</summary>
        [Test]
        public void LowerShelf_InsideTopFootprint_KillYBelow()
        {
            var map = Load();
            var hz = Hazards(map, 0);
            ref readonly var top = ref hz.LidGrids[0];
            ref readonly var low = ref hz.LidGrids[1];
            var topHalf = top.N * top.PitchMm / 2;
            var lowHalf = low.N * low.PitchMm / 2;
            Assert.That(Math.Abs(low.OriginXMm - top.OriginXMm) + lowHalf, Is.LessThanOrEqualTo(topHalf));
            Assert.That(Math.Abs(low.OriginZMm - top.OriginZMm) + lowHalf, Is.LessThanOrEqualTo(topHalf));
            Assert.That(low.TopMm, Is.LessThan(top.TopMm - 3000), "a real drop between the shelves");
            Assert.That(map.KillYMm, Is.EqualTo(-6000));
            Assert.That(map.KillYMm, Is.LessThan(low.TopMm - low.ThicknessMm - 2000), "kill-Y below the lower shelf");
        }

        /// <summary>Every spawn stands on a top-shelf lid that is solid from t 0 through the first wave's lit time (both variants, several seeds).</summary>
        [Test]
        public void Spawns_OnSolidLids_ThroughFirstWaveLit([Values(0, 1)] int variant)
        {
            var map = Load();
            var spawns = map.SpawnPoints();
            Assert.That(spawns.Length, Is.EqualTo(Protocol.MaxPlayers));
            foreach (var seed in new ulong[] { 1, 0x5EED, 0xDEADBEEF, 424242 })
            {
                var hz = Hazards(map, variant, seed);
                ref readonly var d = ref hz.LidGrids[0];
                var scratch = new int[d.Count];
                for (var i = 0; i < spawns.Length; i++)
                {
                    var feet = spawns[i];
                    int fx = Mathf.RoundToInt(feet.X * 1000f), fy = Mathf.RoundToInt(feet.Y * 1000f), fz = Mathf.RoundToInt(feet.Z * 1000f);
                    var lid = d.LidUnder(fx, d.TopMm, fz);
                    Assert.That(lid, Is.GreaterThanOrEqualTo(0), $"spawn {i} over a lid");
                    Assert.That(Math.Abs(fy - d.TopMm), Is.LessThanOrEqualTo(100), $"spawn {i} on the top shelf");
                    Assert.That(d.Ring(lid), Is.EqualTo(2), $"spawn {i} on ring 2");
                    for (var t = 0; t < d.FirstWaveTicks + d.LitTicks; t++)
                        Assert.That(HazardMath.LidSolid(HazardMath.LidStateAt(in d, lid, t, scratch)), Is.True, $"spawn {i} lid {lid} solid at t {t} (seed {seed})");
                }
            }

            // Slots 0 / 1 / 2 (the usual 3 finalists of a small lobby) stand 120° apart.
            for (var a = 0; a < 3; a++)
            {
                var b = (a + 1) % 3;
                var ang = Vector2.Angle(new Vector2(spawns[a].X, spawns[a].Z), new Vector2(spawns[b].X, spawns[b].Z));
                Assert.That(ang, Is.EqualTo(120f).Within(15f), $"spawns {a}/{b}");
            }
        }

        /// <summary>The fairness floor: at every tick of a whole round ≥ 5 of each grid's inner 3 × 3 are solid (both variants, every modifier, several seeds).</summary>
        [Test]
        public void InnerFloor_AtLeast5Solid_EveryTick([Values(0, 1)] int variant)
        {
            var map = Load();
            foreach (var m in map.definition.allowedModifiers)
                foreach (var seed in new ulong[] { 7, 0x5EED, 0xC0FFEE })
                {
                    var hz = Hazards(map, variant, seed, m);
                    for (uint t = 0; t < RoundTicks; t++)
                    {
                        var all = hz.LidStatesAt(t);
                        var at = 0;
                        for (var g = 0; g < hz.LidGrids.Length; g++)
                        {
                            ref readonly var d = ref hz.LidGrids[g];
                            var solid = 0;
                            for (var i = 0; i < d.Count; i++)
                                if (d.Inner(i) && HazardMath.LidSolid(all[at + i]))
                                    solid++;
                            Assert.That(solid, Is.GreaterThanOrEqualTo(5), $"grid {g} t {t} modifier {m} seed {seed}");
                            at += d.Count;
                        }
                    }
                }
        }

        /// <summary>
        /// Fair telegraph (G4.5-2): every lid that flips was lit for the full 30 ticks right before (lit + open ≤ the shortest
        /// wave interval, so no lid closes already yellow from the next wave). Whole round, both grids, both variants.
        /// </summary>
        [Test]
        public void EveryFlip_FollowsAFullSecondOfYellow([Values(0, 1)] int variant)
        {
            var map = Load();
            foreach (var seed in new ulong[] { 3, 0x5EED })
            {
                var hz = Hazards(map, variant, seed);
                var prev = (LidState[])hz.LidStatesAt(0).Clone();
                var litFor = new int[prev.Length];
                for (uint t = 1; t < RoundTicks; t++)
                {
                    var cur = hz.LidStatesAt(t);
                    for (var k = 0; k < cur.Length; k++)
                    {
                        if (cur[k] == LidState.Flipped && prev[k] != LidState.Flipped)
                            Assert.That(litFor[k], Is.GreaterThanOrEqualTo(30), $"lid {k} flipped at t {t} after {litFor[k]} lit ticks (seed {seed})");
                        litFor[k] = cur[k] == LidState.Lit ? litFor[k] + 1 : 0;
                        prev[k] = cur[k];
                    }
                }
            }
        }

        private static ulong PatternHash(HazardSet hz)
        {
            var h = 1469598103934665603UL;
            for (uint t = 0; t < RoundTicks; t += 2)
                foreach (var s in hz.LidStatesAt(t))
                    h = (h ^ (byte)s) * 1099511628211UL;
            return h;
        }

        /// <summary>
        /// The VRF claim (doc 15 G4.5, PROJECT_STATUS "Notes for M6"): the round's hazard seed alone decides the lid pattern.
        /// Same seed → the same pattern (both grids, the whole round); a different seed → a different one; each grid's seed is
        /// <c>ObstaclePhase.Hash(hazardSeed, obstacleId)</c>.
        /// </summary>
        [Test]
        public void HazardSeed_DecidesTheLidPattern([Values(0, 1)] int variant)
        {
            var map = Load();
            const ulong seedA = 0x1234_5678_9ABC_DEF0UL, seedB = 0x0FED_CBA9_8765_4321UL;
            var a1 = Hazards(map, variant, seedA);
            var a2 = Hazards(map, variant, seedA);
            var b = Hazards(map, variant, seedB);
            Assert.That(a1.LidGrids[0].Seed, Is.EqualTo(ObstaclePhase.Hash(seedA, "cs_lids_top")));
            Assert.That(a1.LidGrids[1].Seed, Is.EqualTo(ObstaclePhase.Hash(seedA, "cs_lids_low")));
            Assert.That(PatternHash(a1), Is.EqualTo(PatternHash(a2)), "same seed → same pattern");
            Assert.That(PatternHash(a1), Is.Not.EqualTo(PatternHash(b)), "different seed → different pattern");
            // And per grid: the first waves differ (not only somewhere late).
            var scratch = new int[81];
            var sa = new int[81];
            var na = HazardMath.LidWaveSet(in a1.LidGrids[0], 0, sa);
            var nb = HazardMath.LidWaveSet(in b.LidGrids[0], 0, scratch);
            Assert.That(na, Is.EqualTo(nb), "same share per wave");
            var same = true;
            for (var i = 0; i < na; i++)
                same &= sa[i] == scratch[i];
            Assert.That(same, Is.False, "wave 0 lights different lids for a different seed");
        }

        /// <summary>
        /// Two-grid order (a real bug for any map with two grids if wrong): the motor world's lid colliders, <see cref="HazardSet.LidStatesAt"/>
        /// and the instanced view all run grid by grid in the same order — collider k sits at lid k's centre and is on exactly when
        /// state k is solid; view entry k is drawn at lid k's centre, yellow exactly when state k is lit.
        /// </summary>
        [Test]
        public void TwoGrids_ColliderAndViewOrder_MatchLidStatesAt()
        {
            var map = Load();
            var p = new RoundParams { MapId = map.MapId, Variant = 0, HazardSeed = 0x5EED };
            var probe = PhysxMotorWorld.CreateProbe(map.transform, MovementConfig.Default.Radius, MovementConfig.Default.Height);
            var world = map.CreateWorld(probe, MovementConfig.Default, in p, out var hz);
            var cols = new List<Collider>();
            foreach (var g in map.lidGrids)
                cols.AddRange(g.lids);
            Assert.That(cols.Count, Is.EqualTo(hz.LidCount));
            Assert.That(hz.LidCount, Is.EqualTo(81 + 25));
            var view = new LidGridView(map);
            var checkedLit = 0;
            foreach (var t in new uint[] { 0, 95, 130, 700, 1000, 1500, 2300, 3000 })
            {
                world.BeginTick(t);
                var states = hz.LidStatesAt(t);
                view.Pose(hz, t, false);
                Assert.That(view.LastBatch.Count, Is.EqualTo(states.Length));
                var k = 0;
                for (var g = 0; g < hz.LidGrids.Length; g++)
                {
                    ref readonly var d = ref hz.LidGrids[g];
                    for (var i = 0; i < d.Count; i++, k++)
                    {
                        d.LidCenterMm(i, out var x, out var y, out var z);
                        var c = new Vector3(x * 0.001f, y * 0.001f, z * 0.001f);
                        Assert.That(Vector3.Distance(cols[k].bounds.center, c), Is.LessThan(0.01f), $"collider {k} = grid {g} lid {i}");
                        Assert.That(cols[k].enabled, Is.EqualTo(HazardMath.LidSolid(states[k])), $"collider {k} at t {t}");
                        if (states[k] == LidState.Lit)
                        {
                            Assert.That(view.LastBatch[k], Is.EqualTo(LidGridView.LitBatch), $"view {k} yellow at t {t}");
                            checkedLit++;
                        }

                        if (states[k] != LidState.Retired)
                            Assert.That(Vector3.Distance(view.LastCenter[k], c), Is.LessThan(0.01f), $"view {k} at grid {g} lid {i}");
                    }
                }
            }

            Assert.That(checkedLit, Is.GreaterThan(5), "lit lids were checked");
        }

        /// <summary>Chain compatibility (rule 2): key, id 3, mode Final, 2 variants, modifiers 0/1/2, the 120 s timer, the scene name.</summary>
        [Test]
        public void Identity_KeyIdModeVariantsModifiersTimer_Unchanged()
        {
            var map = Load();
            var def = map.definition;
            Assert.That(map.gameObject.scene.name, Is.EqualTo("Map_CrumbleShelf"));
            Assert.That(def.mapId, Is.EqualTo(MapPoolV1.CrumbleShelf));
            Assert.That(def.mapId, Is.EqualTo(3));
            Assert.That(def.mapKey, Is.EqualTo("CrumbleShelf"));
            Assert.That(MapPoolV1.KeyOf(def.mapId), Is.EqualTo("CrumbleShelf"));
            Assert.That(MapPoolV1.ModeOf(def.mapId), Is.EqualTo("Final"));
            Assert.That(def.mode, Is.EqualTo("Final"));
            Assert.That(def.variants, Is.EqualTo(2));
            Assert.That(MapPoolV1.VariantCount(def.mapId), Is.EqualTo(2));
            Assert.That(def.allowedModifiers, Is.EquivalentTo(new[] { 0, 1, 2 }));
            foreach (var m in new[] { 0, 1, 2 })
                Assert.That(MapPoolV1.ModifierAllowed(def.mapId, m), Is.True);
            Assert.That(def.timerCapSeconds, Is.EqualTo(120), "the Final timer is unchanged");
        }
    }
}
