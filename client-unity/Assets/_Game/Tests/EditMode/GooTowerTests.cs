using System;
using System.Collections.Generic;
using FallRush.Net;
using FallRush.Shared;
using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.TestTools.Utils;

namespace FallRush.Tests
{
    /// <summary>
    /// G4.3 Goo Tower (2 laps, 14 m core, 22 m; doc 08 §5), modelled on <see cref="BlockPartyDashTests"/>: loads the real
    /// <c>Map_GooTower</c> scene and runs full Survival rounds on <see cref="SimCore"/> + <see cref="PhysxMotorWorld"/> with
    /// 8 bots; checks the layout rules (no in-world text, a knock hazard and a route on every side) and the chain-facing
    /// identity (key, id, mode, variants, modifiers unchanged).
    /// </summary>
    public class GooTowerTests
    {
        // The tower frame (mirrors GooTowerBuilder, which is Editor-only): 8 sides, +2.75 m each, 5 m ledge round a 14 m core.
        private const int Sides = 8;
        private const float Height = 22f, Rise = Height / Sides, Core = 7f, HalfW = 2.5f, Mid = Core + HalfW, HalfLen = 7f;
        private static float H(int k) => k * Rise;
        private static Vector3 U(int k) => (k % 4) switch { 0 => Vector3.forward, 1 => Vector3.left, 2 => Vector3.back, _ => Vector3.right };
        private static Vector3 Vo(int k) => (k % 4) switch { 0 => Vector3.right, 1 => Vector3.forward, 2 => Vector3.left, _ => Vector3.back };
        private static Vector3 P(int k, float s, float w, float y) => Vo(k) * Mid + U(k) * s + Vo(k) * w + Vector3.up * y;

        private static MapInfo Load()
        {
            var scene = EditorSceneManager.OpenScene("Assets/_Game/Scenes/Map_GooTower.unity", OpenSceneMode.Single);
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

        /// <summary>A full round with 8 waypoint bots (reference tier, run seed 0) ends with 5 escaped; deterministic.</summary>
        [Test]
        public void FullRound_8Bots_FiveEscape([Values(0, 1)] int variant)
        {
            var map = Load();
            var probe = PhysxMotorWorld.CreateProbe(map.transform, MovementConfig.Default.Radius, MovementConfig.Default.Height);
            var st = ModeQa.RunOne(map, probe, variant, 0, 0, BotTier.Reference, 8);
            TestContext.WriteLine($"variant {variant}: {st.Result} escaped={st.Escaped} goo={st.Goo} duration={st.Duration:F1}s stuck={st.Stuck}");
            Assert.That(st.Over, Is.True, "round ended");
            Assert.That(st.Escaped, Is.EqualTo(5), "the first 5 to the summit escape");
            Assert.That(st.Qualified, Is.EqualTo(5));
            var again = ModeQa.RunOne(map, probe, variant, 0, 0, BotTier.Reference, 8);
            Assert.That(again.Result, Is.EqualTo(st.Result), "deterministic for a run seed");
        }

        /// <summary>Rule 6: readable without words. No world text anywhere in the scene.</summary>
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

        /// <summary>Side k's walk volume: the straight part (s −7…7) of the 5 m ledge, from H(k) − 0.5 to H(k + 1) + 2.5.</summary>
        private static Bounds SideVolume(int k)
        {
            var a = P(k, -HalfLen, -HalfW, H(k) - 0.5f);
            var b = P(k, HalfLen, HalfW, H(k + 1) + 2.5f);
            var bo = new Bounds(a, Vector3.zero);
            bo.Encapsulate(b);
            return bo;
        }

        private static Bounds Box(float x0, float y0, float z0, float x1, float y1, float z1)
        {
            var b = new Bounds(new Vector3(x0, y0, z0), Vector3.zero);
            b.Encapsulate(new Vector3(x1, y1, z1));
            return b;
        }

        /// <summary>Rule 1 (no free route): every side's volume meets the sweep of at least one knock hazard (or a door).</summary>
        [Test]
        public void EverySide_HasAKnockHazard([Values(0, 1)] int variant)
        {
            var map = Load();
            var hz = map.BuildHazards(new RoundParams { MapId = map.MapId, Variant = (byte)variant }, out _);
            var sweeps = new List<(string who, Bounds b)>();
            const float m = 0.001f;
            foreach (var d in hz.Sweepers)
                sweeps.Add(("sweeper", Box((d.PivotXMm - d.ArmMm) * m, d.PivotYMm * m, (d.PivotZMm - d.ArmMm) * m, (d.PivotXMm + d.ArmMm) * m, (d.PivotYMm + d.BarHeightMm + d.BarRadiusMm) * m, (d.PivotZMm + d.ArmMm) * m)));
            foreach (var d in hz.Mallets)
            {
                var rx = (d.SwingAxis == 0 ? d.ArmMm : d.HeadHalfWidthMm) + d.HeadRadiusMm;
                var rz = (d.SwingAxis == 2 ? d.ArmMm : d.HeadHalfWidthMm) + d.HeadRadiusMm;
                sweeps.Add(("mallet", Box((d.PivotXMm - rx) * m, (d.PivotYMm - d.ArmMm - d.HeadRadiusMm) * m, (d.PivotZMm - rz) * m, (d.PivotXMm + rx) * m, d.PivotYMm * m, (d.PivotZMm + rz) * m)));
            }

            foreach (var d in hz.RollingBalls)
                sweeps.Add(("ball", Box((Math.Min(d.StartXMm, d.EndXMm) - d.RadiusMm) * m, (Math.Min(d.StartYMm, d.EndYMm) - d.RadiusMm) * m, (Math.Min(d.StartZMm, d.EndZMm) - d.RadiusMm) * m,
                    (Math.Max(d.StartXMm, d.EndXMm) + d.RadiusMm) * m, (Math.Max(d.StartYMm, d.EndYMm) + d.RadiusMm) * m, (Math.Max(d.StartZMm, d.EndZMm) + d.RadiusMm) * m)));
            foreach (var d in hz.Bumpers)
                sweeps.Add(("bumper", Box((d.CenterXMm - d.RadiusMm) * m, d.CenterYMm * m, (d.CenterZMm - d.RadiusMm) * m, (d.CenterXMm + d.RadiusMm) * m, (d.CenterYMm + d.HeightMm) * m, (d.CenterZMm + d.RadiusMm) * m)));
            foreach (var d in hz.Doors)
                sweeps.Add(("door", Box((d.CenterXMm - d.HalfXMm) * m, (d.CenterYMm - d.HalfYMm) * m, (d.CenterZMm - d.HalfZMm) * m, (d.CenterXMm + d.HalfXMm) * m, (d.CenterYMm + d.HalfYMm) * m, (d.CenterZMm + d.HalfZMm) * m)));
            for (var k = 0; k < Sides; k++)
            {
                var v = SideVolume(k);
                var found = sweeps.FindAll(w => w.b.Intersects(v));
                TestContext.WriteLine($"side {k}: {string.Join(", ", found.ConvertAll(w => w.who))}");
                Assert.That(found.Exists(w => w.who != "door") || k == Sides - 1 && found.Count > 0, Is.True, $"side {k} has no knock hazard");
            }
        }

        /// <summary>Every side 0…7 has at least one route the bots can use (both variants).</summary>
        [Test]
        public void EverySide_HasARoute([Values(0, 1)] int variant)
        {
            var map = Load();
            var hz = map.BuildHazards(new RoundParams { MapId = map.MapId, Variant = (byte)variant }, out _);
            var routes = BotBrainSet.UsableRoutes(map.botRoutes, hz);
            for (var k = 0; k < Sides; k++)
            {
                var n = Array.FindAll(routes, r => r.Section == k).Length;
                TestContext.WriteLine($"side {k}: {n} routes");
                Assert.That(n, Is.GreaterThanOrEqualTo(k == 1 ? 1 : 2), $"side {k}");
            }
        }

        /// <summary>The tower's shape: a 14 m core, the summit trigger on a pad at 22 m.</summary>
        [Test]
        public void Core14m_Summit22m()
        {
            var map = Load();
            var core = GameObject.Find("TowerCore");
            Assert.That(core, Is.Not.Null);
            var b = core.GetComponent<Collider>().bounds;
            Assert.That(b.size.x, Is.EqualTo(14f).Within(0.01f));
            Assert.That(b.size.z, Is.EqualTo(14f).Within(0.01f));
            Assert.That(map.checkpoints.Length, Is.EqualTo(1));
            var summit = map.checkpoints[0];
            Assert.That(summit.isFinish, Is.True);
            Assert.That(summit.transform.position.y - 1.5f, Is.EqualTo(22f).Within(0.1f));
            // The last corner (summit) sits on top of corner 0, 22 m up.
            Assert.That(new Vector2(summit.transform.position.x, summit.transform.position.z), Is.EqualTo(new Vector2(Mid, -Mid)).Using(Vector2EqualityComparer.Instance));
        }

        /// <summary>Chain compatibility (rule 2): id, key, mode, variant count and modifiers unchanged; only the content hash changes.</summary>
        [Test]
        public void Identity_KeyIdModeVariantsModifiers_Unchanged()
        {
            var map = Load();
            var def = map.definition;
            Assert.That(map.gameObject.scene.name, Is.EqualTo("Map_GooTower"));
            Assert.That(def.mapId, Is.EqualTo(MapPoolV1.GooTower));
            Assert.That(def.mapId, Is.EqualTo(1));
            Assert.That(def.mapKey, Is.EqualTo("GooTower"));
            Assert.That(MapPoolV1.KeyOf(def.mapId), Is.EqualTo("GooTower"));
            Assert.That(MapPoolV1.ModeOf(def.mapId), Is.EqualTo("Survival"));
            Assert.That(def.mode, Is.EqualTo("Survival"));
            Assert.That(def.variants, Is.EqualTo(2));
            Assert.That(MapPoolV1.VariantCount(def.mapId), Is.EqualTo(2));
            Assert.That(def.allowedModifiers, Is.EquivalentTo(new[] { 0 }));
            Assert.That(MapPoolV1.ModifierAllowed(def.mapId, 0), Is.True);
        }
    }
}
