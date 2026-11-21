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
    /// M3b §5.6, run as an EditMode test on <see cref="SimCore"/> + <see cref="PhysxMotorWorld"/> instead of a PlayMode
    /// test (reason: a PlayMode batch run loads the whole player loop, NGO and the client view on the 8 GB machine; the race
    /// logic under test is engine-free and the PhysX world is the same one the server uses). Loads the real
    /// <c>Map_BlockPartyDash</c> scene (G4.2: Capsule Run) and runs a full race with 12 waypoint bots to <c>Ended</c>;
    /// G4.2 adds the layout rules (no in-world text, a knock hazard in every section, a route for every section) and the
    /// chain-facing identity (key, id, mode, variants, modifiers unchanged).
    /// </summary>
    public class BlockPartyDashTests
    {
        private static MapInfo Load()
        {
            var scene = EditorSceneManager.OpenScene("Assets/_Game/Scenes/Map_BlockPartyDash.unity", OpenSceneMode.Single);
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

        [Test]
        public void FullRace_12WaypointBots_ToEnded_NQualified_Ordered([Values(0, 1)] int variant)
        {
            var map = Load();
            var probe = PhysxMotorWorld.CreateProbe(map.transform, MovementConfig.Default.Radius, MovementConfig.Default.Height);
            var st = MapQa.RunOne(map, probe, variant, 0, 0);
            TestContext.WriteLine($"variant {variant}: {st.Result} falls={st.Falls} hits={st.Hits} stuck={st.Stuck} softLocks={st.SoftLocks} {st.SoftLockReasons}");
            Assert.That(st.Result, Does.Not.StartWith("no result"), "reached Ended");
            Assert.That(st.QualifyCount, Is.EqualTo(8));
            Assert.That(st.Qualified, Is.EqualTo(8), "exactly N qualified");
            Assert.That(st.SoftLocks, Is.EqualTo(0), st.SoftLockReasons);
            var again = MapQa.RunOne(map, probe, variant, 0, 0);
            Assert.That(again.Result, Is.EqualTo(st.Result), "deterministic for a run seed");
        }

        /// <summary>G4.2 rule 6: readable without words. No world text anywhere in the scene (Geometry, Hazards, Dressing).</summary>
        [Test]
        public void Scene_HasNoInWorldText()
        {
            Load();
            var texts = new List<string>();
            foreach (var c in UnityEngine.Object.FindObjectsByType<Component>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                var n = c.GetType().Name;
                if (c is TextMesh || n.Contains("TextMeshPro") || n == "Text" || c.gameObject.name.StartsWith("Label_"))
                    texts.Add($"{c.gameObject.name} ({n})");
            }

            Assert.That(texts, Is.Empty, string.Join(", ", texts));
        }

        /// <summary>Section k runs from checkpoint k (the start for k = 0) to checkpoint k + 1 along z.</summary>
        private static List<(float z0, float z1)> Sections(MapInfo map)
        {
            var s = new List<(float, float)>();
            var z = map.spawns[0].position.z;
            foreach (var cp in map.checkpoints)
            {
                s.Add((z, cp.transform.position.z));
                z = cp.transform.position.z;
            }

            return s;
        }

        /// <summary>G4.2 rule 1 (no free route): every section's z range meets the sweep of at least one knock hazard.</summary>
        [Test]
        public void EverySection_HasAKnockHazard([Values(0, 1)] int variant)
        {
            var map = Load();
            var hz = map.BuildHazards(new RoundParams { MapId = map.MapId, Variant = (byte)variant }, out _);
            var sweeps = new List<(string who, float z0, float z1)>();
            foreach (var d in hz.Sweepers)
                sweeps.Add(("sweeper", (d.PivotZMm - d.ArmMm) * 0.001f, (d.PivotZMm + d.ArmMm) * 0.001f));
            foreach (var d in hz.Mallets)
            {
                var reach = d.SwingAxis == 2 ? d.ArmMm : d.HeadHalfWidthMm;
                sweeps.Add(("mallet", (d.PivotZMm - reach - d.HeadRadiusMm) * 0.001f, (d.PivotZMm + reach + d.HeadRadiusMm) * 0.001f));
            }

            foreach (var d in hz.RollingBalls)
                sweeps.Add(("ball", (Math.Min(d.StartZMm, d.EndZMm) - d.RadiusMm) * 0.001f, (Math.Max(d.StartZMm, d.EndZMm) + d.RadiusMm) * 0.001f));
            foreach (var d in hz.Bumpers)
                sweeps.Add(("bumper", (d.CenterZMm - d.RadiusMm) * 0.001f, (d.CenterZMm + d.RadiusMm) * 0.001f));
            var sections = Sections(map);
            Assert.That(sections.Count, Is.EqualTo(5));
            for (var k = 0; k < sections.Count; k++)
            {
                var (z0, z1) = sections[k];
                var found = sweeps.FindAll(w => w.z1 > z0 && w.z0 < z1);
                TestContext.WriteLine($"section {k} z {z0:F1}…{z1:F1}: {string.Join(", ", found.ConvertAll(w => w.who))}");
                Assert.That(found, Is.Not.Empty, $"section {k} (z {z0:F1}…{z1:F1}) has no knock hazard");
            }
        }

        /// <summary>Every section 0…4 has at least one route the bots can use (both variants: no route needs a door the variant keeps shut).</summary>
        [Test]
        public void EverySection_HasARoute([Values(0, 1)] int variant)
        {
            var map = Load();
            var hz = map.BuildHazards(new RoundParams { MapId = map.MapId, Variant = (byte)variant }, out _);
            var routes = BotBrainSet.UsableRoutes(map.botRoutes, hz);
            for (var k = 0; k < map.definition.checkpointCount; k++)
            {
                var n = Array.FindAll(routes, r => r.Section == k).Length;
                TestContext.WriteLine($"section {k}: {n} routes");
                Assert.That(n, Is.GreaterThanOrEqualTo(1), $"section {k}");
            }

            Assert.That(Array.FindAll(routes, r => r.Section == 0).Length, Is.GreaterThanOrEqualTo(3), "three slope routes");
        }

        /// <summary>Chain compatibility (G4.2 rule 2): the rebuild keeps the map's id, key, mode, variant count and modifiers; only the content hash changes.</summary>
        [Test]
        public void Identity_KeyIdModeVariantsModifiers_Unchanged()
        {
            var map = Load();
            var def = map.definition;
            Assert.That(map.gameObject.scene.name, Is.EqualTo("Map_BlockPartyDash"));
            Assert.That(def.mapId, Is.EqualTo(MapPoolV1.BlockPartyDash));
            Assert.That(def.mapId, Is.EqualTo(0));
            Assert.That(def.mapKey, Is.EqualTo("BlockPartyDash"));
            Assert.That(MapPoolV1.KeyOf(def.mapId), Is.EqualTo("BlockPartyDash"));
            Assert.That(MapPoolV1.ModeOf(def.mapId), Is.EqualTo("Race"));
            Assert.That(def.IsRace, Is.True);
            Assert.That(def.variants, Is.EqualTo(2));
            Assert.That(MapPoolV1.VariantCount(def.mapId), Is.EqualTo(2));
            Assert.That(def.allowedModifiers, Is.EquivalentTo(new[] { 0, 1, 2, 3, 5 }));
            foreach (var m in def.allowedModifiers)
                Assert.That(MapPoolV1.ModifierAllowed(def.mapId, m), Is.True, $"modifier {m}");
            Assert.That(MapDisplayNames.Of(def.mapId), Is.EqualTo("Capsule Run"));
        }
    }
}
