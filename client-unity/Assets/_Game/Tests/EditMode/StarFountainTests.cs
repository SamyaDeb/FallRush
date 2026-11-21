using System;
using System.Collections.Generic;
using FallRush.Net;
using FallRush.Shared;
using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace FallRush.Tests
{
    /// <summary>
    /// G4.4 Star Fountain (key <c>StarScramble</c>, doc 08 §6), modelled on <see cref="GooTowerTests"/>: loads the real
    /// <c>Map_StarScramble</c> scene and runs full Skill rounds on <see cref="SimCore"/> + <see cref="PhysxMotorWorld"/> with
    /// 5 bots; checks the layout rules (no in-world text, the fountain in the middle and out of reach, a 9 m turntable,
    /// 3 islands with a spring each, every star on a walkable surface, golden stars only on islands), that the burst view
    /// is a pure function of the hazard time, and the chain-facing identity (key, id, mode, variants, modifiers unchanged).
    /// </summary>
    public class StarFountainTests
    {
        private static MapInfo Load()
        {
            var scene = EditorSceneManager.OpenScene("Assets/_Game/Scenes/Map_StarScramble.unity", OpenSceneMode.Single);
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

        private static HazardSet Hazards(MapInfo map, int variant, ulong seed = 0x5EEDu) =>
            map.BuildHazards(new RoundParams { MapId = map.MapId, Variant = (byte)variant, HazardSeed = seed }, out _);

        /// <summary>A full round with 5 brain bots (reference tier, run seed 0): 3 qualified, ranked by score, deterministic.</summary>
        [Test]
        public void FullRound_5Bots_ThreeQualifyOrdered([Values(0, 1)] int variant)
        {
            var map = Load();
            var probe = PhysxMotorWorld.CreateProbe(map.transform, MovementConfig.Default.Radius, MovementConfig.Default.Height);
            var st = ModeQa.RunOne(map, probe, variant, 0, 0, BotTier.Reference, 5);
            TestContext.WriteLine($"variant {variant}: {st.Result} pickups={st.Pickups} drops={st.Drops} table={st.TablePicks} golden={st.GoldenCollected}/{st.GoldenSpawned} springs={st.SpringOk}/{st.SpringTries}");
            Assert.That(st.Over, Is.True, "round ended");
            Assert.That(st.TimerExpired, Is.True, "the Skill round runs the full timer");
            Assert.That(st.Duration, Is.EqualTo(60f).Within(0.1f));
            Assert.That(st.Qualified, Is.EqualTo(3));
            Assert.That(st.Order, Is.EqualTo(5));
            for (var k = 0; k < st.Values.Length; k++)
            {
                Assert.That(st.QualifiedFlags[k], Is.EqualTo(k < 3), $"place {k}");
                if (k > 0)
                    Assert.That(st.Values[k], Is.LessThanOrEqualTo(st.Values[k - 1]), "ranked by score");
            }

            Assert.That(st.ScoreSum, Is.EqualTo(st.ScoreExpected), "scores = pickups − drops");
            Assert.That(st.Pickups, Is.GreaterThan(30));
            var again = ModeQa.RunOne(map, probe, variant, 0, 0, BotTier.Reference, 5);
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

        /// <summary>The fountain stands in the centre, r 1.4 m, and its top is out of hop reach from the table (MoonBounce included).</summary>
        [Test]
        public void Fountain_AtCentre_OutOfHopReach()
        {
            var map = Load();
            var f = GameObject.Find("ss_fountain");
            Assert.That(f, Is.Not.Null);
            var b = f.GetComponent<Collider>().bounds;
            Assert.That(new Vector2(b.center.x, b.center.z).magnitude, Is.LessThan(0.01f), "centred");
            Assert.That(b.extents.x, Is.EqualTo(1.4f).Within(0.05f));
            var hz = Hazards(map, 0);
            var tableTop = hz.TurnTables[0].CenterYMm * 0.001f;
            var moonApex = MovementConfig.Default.JumpApex * ModifierEffects.Get(1).JumpApexPermille / 1000f;
            Assert.That(b.max.y - tableTop, Is.GreaterThan(Math.Max(MovementConfig.Default.JumpApex, moonApex) + 0.5f), "top out of hop reach");
            Assert.That(map.starFields[0].burstOrigin, Is.Not.Null, "the burst starts at the crown");
            Assert.That(map.starFields[0].burstOrigin.position.y, Is.GreaterThan(b.max.y));
        }

        /// <summary>The allowlist is the ceiling: one turntable round the fountain, r ≤ 9 m (both variants, every modifier passes).</summary>
        [Test]
        public void Turntable_RadiusAtMost9m([Values(0, 1)] int variant)
        {
            var map = Load();
            var hz = Hazards(map, variant);
            Assert.That(hz.TurnTables.Length, Is.EqualTo(1));
            Assert.That(hz.TurnTables[0].RadiusMm, Is.LessThanOrEqualTo(9000));
            Assert.That(hz.TurnTables[0].CenterXMm, Is.EqualTo(0));
            Assert.That(hz.TurnTables[0].CenterZMm, Is.EqualTo(0));
            foreach (var m in map.definition.allowedModifiers)
                Assert.That(map.Validate(new RoundParams { MapId = map.MapId, Variant = (byte)variant, Modifier = (byte)m }), Is.Empty, $"modifier {m}");
        }

        /// <summary>Three islands, each with one spring that launches at it; three low sweepers on the outer ring.</summary>
        [Test]
        public void ThreeIslands_EachWithASpring_ThreeSweepers([Values(0, 1)] int variant)
        {
            var map = Load();
            var hz = Hazards(map, variant);
            Assert.That(hz.Platforms.Length, Is.EqualTo(3), "islands");
            Assert.That(hz.Pads.Length, Is.EqualTo(3), "springs");
            Assert.That(hz.Bumpers.Length, Is.EqualTo(0), "the M4 bumpers are gone");
            for (var i = 0; i < 3; i++)
            {
                ref readonly var pl = ref hz.Platforms[i];
                var n = 0;
                foreach (var p in hz.Pads)
                {
                    double lx = p.LaunchXCms, lz = p.LaunchZCms, tx = pl.CenterXMm - p.CenterXMm, tz = pl.CenterZMm - p.CenterZMm;
                    if ((lx * tx + lz * tz) / (Math.Sqrt(lx * lx + lz * lz) * Math.Sqrt(tx * tx + tz * tz)) > 0.98)
                        n++;
                }

                Assert.That(n, Is.EqualTo(1), $"island {i} has one spring aimed at it");
                Assert.That(Math.Sqrt((double)pl.CenterXMm * pl.CenterXMm + (double)pl.CenterZMm * pl.CenterZMm), Is.EqualTo(22000).Within(1), "r 22 m");
            }

            Assert.That(hz.Sweepers.Length, Is.EqualTo(3));
            foreach (var d in hz.Sweepers)
            {
                Assert.That(d.BarHeightMm - d.BarRadiusMm, Is.LessThan(MovementConfig.Default.JumpApex * 1000f * 0.5f), "low bar: jump it");
                Assert.That(Math.Sqrt((double)d.PivotXMm * d.PivotXMm + (double)d.PivotZMm * d.PivotZMm), Is.EqualTo(13500).Within(1));
            }
        }

        /// <summary>Every star anchor sits on a walkable collider (turntable, floor or island) at both variants' poses over a round.</summary>
        [Test]
        public void EveryStarAnchor_OnAWalkableSurface([Values(0, 1)] int variant)
        {
            var map = Load();
            var hz = Hazards(map, variant);
            var f = hz.StarFields[0];
            var mask = PhysxMotorWorld.GameplayMask();
            for (var a = 0; a < f.AnchorCount; a++)
            {
                var att = f.Attach[a];
                for (var t = 0; t <= 1800; t += 90)
                {
                    HazardMath.StarAnchorMm(in f, hz, a, t, out var x, out var y, out var z);
                    var r = Math.Sqrt((double)x * x + (double)z * z);
                    if (att >= 1000)
                    {
                        ref readonly var tt = ref hz.TurnTables[att - 1000];
                        Assert.That(r, Is.LessThanOrEqualTo(tt.RadiusMm - 300), $"anchor {a} on the table at t={t}");
                        Assert.That(r, Is.GreaterThan(1400 + 600), $"anchor {a} clear of the fountain");
                        Assert.That(y - tt.CenterYMm, Is.EqualTo(600), "0.6 m above the table top");
                    }
                    else if (att >= 0)
                    {
                        ref readonly var pl = ref hz.Platforms[att];
                        HazardMath.PlatformCenterMm(in pl, t, out var cx, out var cy, out var cz);
                        Assert.That(Math.Abs(x - cx), Is.LessThan(pl.HalfXMm), $"anchor {a} on island {att}");
                        Assert.That(Math.Abs(z - cz), Is.LessThan(pl.HalfZMm));
                        Assert.That(y - (cy + pl.HalfYMm), Is.EqualTo(600));
                    }
                    else
                    {
                        var hit = Physics.Raycast(new Vector3(x * 0.001f, y * 0.001f + 0.2f, z * 0.001f), Vector3.down, out var h, 1.5f, mask, QueryTriggerInteraction.Ignore);
                        Assert.That(hit, Is.True, $"static anchor {a} over the floor");
                        Assert.That(h.point.y, Is.EqualTo(0f).Within(0.05f), $"static anchor {a} on the arena floor");
                        Assert.That(r, Is.LessThan(17500));
                    }
                }
            }
        }

        /// <summary>Golden anchors ride islands only, and only golden anchors do; most normal anchors ride the turntable.</summary>
        [Test]
        public void GoldenAnchors_OnlyOnIslands([Values(0, 1)] int variant)
        {
            var map = Load();
            var f = Hazards(map, variant).StarFields[0];
            Assert.That(f.Golden.Length, Is.EqualTo(3));
            var golden = new HashSet<int>(f.Golden);
            int table = 0, normal = 0;
            for (var a = 0; a < f.AnchorCount; a++)
            {
                var onIsland = f.Attach[a] >= 0 && f.Attach[a] < 1000;
                Assert.That(onIsland, Is.EqualTo(golden.Contains(a)), $"anchor {a}");
                if (!golden.Contains(a))
                {
                    normal++;
                    table += f.Attach[a] >= 1000 ? 1 : 0;
                }
            }

            Assert.That(table, Is.EqualTo(16));
            Assert.That(normal - table, Is.EqualTo(6));
            Assert.That(f.WaveEveryTicks, Is.EqualTo(240), "a burst every 8 s");
            Assert.That(f.WaveSize, Is.EqualTo(8));
        }

        /// <summary>The burst (view only) is a pure function of the hazard time: same t → same arc point; it starts at the crown and ends on the anchor's spawn-tick pose.</summary>
        [Test]
        public void Burst_IsAPureFunctionOfTime()
        {
            var map = Load();
            var hz = Hazards(map, 0, 0xABCDEF01u);
            var f = hz.StarFields[0];
            var c = map.starFields[0].burstOrigin.position;
            int cx = Mathf.RoundToInt(c.x * 1000f), cy = Mathf.RoundToInt(c.y * 1000f), cz = Mathf.RoundToInt(c.z * 1000f);
            for (var w = 1; w <= 6; w++)
                for (var k = 0; k < f.WaveSize; k++)
                {
                    var id = w * f.WaveSize + k;
                    var spawn = w * f.WaveEveryTicks;
                    Assert.That(StarBurst.ArcPosMm(in f, hz, hz.Seed, id, spawn - 20, cx, cy, cz, out _, out _, out _, out _), Is.False, "before the arc window");
                    Assert.That(StarBurst.ArcPosMm(in f, hz, hz.Seed, id, spawn, cx, cy, cz, out _, out _, out _, out _), Is.False, "the star itself from the spawn tick");
                    Assert.That(StarBurst.ArcPosMm(in f, hz, hz.Seed, id, spawn - 7.25, cx, cy, cz, out var x1, out var y1, out var z1, out _), Is.True);
                    StarBurst.ArcPosMm(in f, hz, hz.Seed, id, spawn - 7.25, cx, cy, cz, out var x2, out var y2, out var z2, out _);
                    Assert.That((x1, y1, z1), Is.EqualTo((x2, y2, z2)), "same t, same point");
                    StarBurst.ArcPosMm(in f, hz, hz.Seed, id, spawn - StarBurst.ArcTicks, cx, cy, cz, out var sx, out var sy, out var sz, out var u0);
                    Assert.That(u0, Is.EqualTo(0).Within(1e-9));
                    Assert.That((sx, sy, sz), Is.EqualTo(((double)cx, (double)cy, (double)cz)), "starts at the crown");
                    StarBurst.ArcPosMm(in f, hz, hz.Seed, id, spawn - 1e-6, cx, cy, cz, out var ex, out var ey, out var ez, out _);
                    HazardMath.StarSlot(in f, hz.Seed, id, out var a, out _, out _, out _);
                    HazardMath.StarAnchorMm(in f, hz, a, spawn, out var ax, out var ay, out var az);
                    Assert.That(ex, Is.EqualTo(ax).Within(1.0));
                    Assert.That(ey, Is.EqualTo(ay).Within(1.0));
                    Assert.That(ez, Is.EqualTo(az).Within(1.0));
                }

            Assert.That(StarBurst.CrownGlow(in f, 240 - 31), Is.EqualTo(0f), "dark more than 1 s before a wave");
            Assert.That(StarBurst.CrownGlow(in f, 240 - 15), Is.EqualTo(0.5f).Within(1e-4f));
            Assert.That(StarBurst.CrownGlow(in f, 240 - 15), Is.EqualTo(StarBurst.CrownGlow(in f, 240 - 15)));
        }

        /// <summary>Chain compatibility (rule 2): id, key, mode, variant count and modifiers unchanged; only the content hash changes.</summary>
        [Test]
        public void Identity_KeyIdModeVariantsModifiers_Unchanged()
        {
            var map = Load();
            var def = map.definition;
            Assert.That(map.gameObject.scene.name, Is.EqualTo("Map_StarScramble"));
            Assert.That(def.mapId, Is.EqualTo(MapPoolV1.StarScramble));
            Assert.That(def.mapId, Is.EqualTo(2));
            Assert.That(def.mapKey, Is.EqualTo("StarScramble"));
            Assert.That(MapPoolV1.KeyOf(def.mapId), Is.EqualTo("StarScramble"));
            Assert.That(MapPoolV1.ModeOf(def.mapId), Is.EqualTo("Skill"));
            Assert.That(def.mode, Is.EqualTo("Skill"));
            Assert.That(def.variants, Is.EqualTo(2));
            Assert.That(MapPoolV1.VariantCount(def.mapId), Is.EqualTo(2));
            Assert.That(def.allowedModifiers, Is.EquivalentTo(new[] { 0, 1, 3, 4 }));
            foreach (var m in new[] { 0, 1, 3, 4 })
                Assert.That(MapPoolV1.ModifierAllowed(def.mapId, m), Is.True);
            Assert.That(def.timerCapSeconds, Is.EqualTo(60), "the round timer is unchanged");
        }
    }
}
