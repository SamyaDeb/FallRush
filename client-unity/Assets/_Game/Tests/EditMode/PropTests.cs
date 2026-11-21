using System.Collections.Generic;
using System.IO;
using FallRush.Client;
using FallRush.Net;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace FallRush.Tests
{
    /// <summary>
    /// G4.9 prop kit: every Blender prop is ≤ 1.5k triangles, imports at the generator's size with its slot / shade UVs, and
    /// in the built map scenes every hazard / platform render that got a prop sits within 0.15 m of what collides (or of
    /// the analytic hazard shape), and every hazard still carries the lemon hit stripe.
    /// </summary>
    public sealed class PropTests
    {
        private const string Dir = "Assets/_Game/Art/Props";
        private const float Tol = 0.15f;

        [System.Serializable]
        private sealed class Manifest
        {
            public string[] slots;
            public int triCap;
            public Entry[] props;
        }

        [System.Serializable]
        private sealed class Entry
        {
            public string name;
            public int tris;
            public B bounds;
        }

        [System.Serializable]
        private sealed class B
        {
            public float[] min, max;
        }

        private static Manifest Load() => JsonUtility.FromJson<Manifest>(File.ReadAllText(Path.Combine(Path.GetDirectoryName(Application.dataPath) ?? ".", Dir, "props.json")));

        private static Mesh Raw(string name)
        {
            foreach (var o in AssetDatabase.LoadAllAssetsAtPath($"{Dir}/{name}.fbx"))
                if (o is Mesh m)
                    return m;
            return null;
        }

        [Test]
        public void EveryProp_UnderTheTriCap_ImportsAtItsSize_WithSlotAndShadeUvs()
        {
            var j = Load();
            Assert.That(j.triCap, Is.EqualTo(1500));
            Assert.That(j.props.Length, Is.GreaterThanOrEqualTo(20), "about 20 props");
            foreach (var e in j.props)
            {
                var m = Raw(e.name);
                Assert.That(m, Is.Not.Null, e.name);
                Assert.That(m.triangles.Length / 3, Is.LessThanOrEqualTo(1500), e.name + " tris");
                Assert.That(e.tris, Is.LessThanOrEqualTo(1500), e.name + " tris (generator)");
                var uv0 = new List<Vector2>();
                var uv1 = new List<Vector2>();
                m.GetUVs(0, uv0);
                m.GetUVs(1, uv1);
                Assert.That(uv0.Count, Is.EqualTo(m.vertexCount), e.name + " slot UVs");
                Assert.That(uv1.Count, Is.EqualTo(m.vertexCount), e.name + " shade UVs");
                foreach (var u in uv0)
                {
                    Assert.That(Mathf.RoundToInt(u.x), Is.InRange(0, j.slots.Length - 1), e.name + " slot");
                    Assert.That(Mathf.RoundToInt(u.y), Is.InRange(2, 5), e.name + " role");
                }

                foreach (var u in uv1)
                    Assert.That(u.x, Is.InRange(0.2f, 1.001f), e.name + " shade");
                for (var a = 0; a < 3; a++)
                    Assert.That(m.bounds.size[a], Is.EqualTo(e.bounds.max[a] - e.bounds.min[a]).Within(0.02f), $"{e.name} axis {a} size (axes / units)");
            }
        }

        [Test]
        public void BumperSquash_12Percent_WobblesBackIn0p4s_ReducedMotion4PercentNoWobble()
        {
            Assert.That(HazardViewDriver.BumperScale(0f, false).y, Is.EqualTo(0.88f).Within(1e-4f));
            Assert.That(HazardViewDriver.BumperScale(0f, true).y, Is.EqualTo(0.96f).Within(1e-4f));
            Assert.That(HazardViewDriver.BumperScale(0.4f, false), Is.EqualTo(Vector3.one));
            Assert.That(HazardViewDriver.BumperScale(0.4f, true), Is.EqualTo(Vector3.one));
            var overshoot = false;
            for (var t = 0f; t < 0.4f; t += 0.01f)
            {
                var full = HazardViewDriver.BumperScale(t, false);
                var red = HazardViewDriver.BumperScale(t, true);
                overshoot |= full.y > 1.001f;                      // the wobble springs past rest
                Assert.That(red.y, Is.LessThanOrEqualTo(1f), "reduced motion: no wobble past rest");
                Assert.That(Mathf.Abs(1f - full.y), Is.LessThanOrEqualTo(0.12f + 1e-4f));
            }

            Assert.That(overshoot, Is.True, "full motion wobbles");
        }

        private static IEnumerable<(string scene, MapInfo info)> Maps()
        {
            foreach (var line in File.ReadAllLines(Path.Combine(Application.dataPath, "_Game", "Tests", "Vectors", "map_hashes_v1.txt")))
            {
                var name = line.Split(' ')[0];
                var scene = EditorSceneManager.OpenScene($"Assets/_Game/Scenes/{name}.unity", OpenSceneMode.Single);
                Physics.SyncTransforms();
                MapInfo info = null;
                foreach (var root in scene.GetRootGameObjects())
                    info ??= root.GetComponentInChildren<MapInfo>();
                yield return (name, info);
            }
        }

        private static void Within(Bounds render, Bounds collide, string what)
        {
            for (var a = 0; a < 3; a++)
            {
                Assert.That(Mathf.Abs(render.min[a] - collide.min[a]), Is.LessThanOrEqualTo(Tol), $"{what}: min[{a}] {render.min[a]:F3} vs {collide.min[a]:F3}");
                Assert.That(Mathf.Abs(render.max[a] - collide.max[a]), Is.LessThanOrEqualTo(Tol), $"{what}: max[{a}] {render.max[a]:F3} vs {collide.max[a]:F3}");
            }
        }

        /// <summary>The lemon hit stripe (hue 40°–62°, saturated) is somewhere on the mesh (soft colours; AO-shaded).</summary>
        private static bool HasStripe(Mesh m)
        {
            foreach (var c in m.colors)
            {
                Color.RGBToHSV(c.gamma, out var h, out var sat, out var v);
                if (h * 360f > 40f && h * 360f < 62f && sat > 0.55f && v > 0.5f)
                    return true;
            }

            return false;
        }

        [Test]
        public void Props_CollisionEqualsVisuals_AndEveryHazardKeepsItsHitStripe()
        {
            var checkedCount = 0;
            foreach (var (scene, info) in Maps())
            {
                Assert.That(info, Is.Not.Null, scene);
                foreach (var bm in info.bumpers)
                {
                    if (bm == null || bm.visual == null)
                        continue;
                    var r = bm.visual.GetComponent<MeshRenderer>();
                    Within(r.bounds, bm.motorCollider.bounds, $"{scene} bumper {bm.obstacleId}");
                    Assert.That(HasStripe(bm.visual.GetComponent<MeshFilter>().sharedMesh), Is.True, $"{scene} bumper {bm.obstacleId}: hit stripe");
                    checkedCount++;
                }

                foreach (var pd in info.pads)
                {
                    if (pd == null || pd.motorCollider == null)
                        continue;
                    var prop = pd.motorCollider.transform.Find("PadProp");
                    if (prop == null)
                        continue;
                    Within(prop.GetComponent<MeshRenderer>().bounds, pd.motorCollider.bounds, $"{scene} spring pad {pd.obstacleId}");
                    checkedCount++;
                }

                foreach (var lane in info.rollingBalls)
                {
                    if (lane == null || lane.balls == null)
                        continue;
                    var radius = lane.def.RadiusMm / 1000f;
                    foreach (var b in lane.balls)
                    {
                        var body = b != null ? b.Find("BallBody") : null;
                        if (body == null)
                            continue;
                        var mesh = body.GetComponent<MeshFilter>().sharedMesh;
                        var size = Vector3.Scale(mesh.bounds.size, body.lossyScale);
                        for (var a = 0; a < 3; a++)
                            Assert.That(Mathf.Abs(size[a] - 2f * radius), Is.LessThanOrEqualTo(Tol), $"{scene} gumball {lane.obstacleId} axis {a}");
                        Assert.That(HasStripe(mesh), Is.True, $"{scene} gumball {lane.obstacleId}: hit stripe");
                        checkedCount++;
                    }
                }

                foreach (var sw in info.sweepers)
                {
                    if (sw == null || sw.barVisual == null)
                        continue;
                    var mesh = sw.barVisual.GetComponent<MeshFilter>().sharedMesh;
                    var arm = sw.def.ArmMm / 1000f;
                    var bar = sw.def.BarRadiusMm / 1000f;
                    Assert.That(Mathf.Abs(mesh.bounds.extents.z - arm), Is.LessThanOrEqualTo(Tol), $"{scene} spinner {sw.obstacleId}: reach {mesh.bounds.extents.z:F3} vs arm {arm:F3}");
                    Assert.That(Mathf.Max(mesh.bounds.extents.x, mesh.bounds.extents.z), Is.LessThanOrEqualTo(arm + Tol), $"{scene} spinner {sw.obstacleId}: nothing reaches past the arm");
                    _ = bar;
                    Assert.That(HasStripe(mesh), Is.True, $"{scene} spinner {sw.obstacleId}: hit stripe");
                    checkedCount++;
                }

                foreach (var ml in info.mallets)
                {
                    if (ml == null || ml.visual == null)
                        continue;
                    var mesh = ml.visual.GetComponent<MeshFilter>().sharedMesh;
                    var arm = ml.def.ArmMm / 1000f;
                    var head = ml.def.HeadRadiusMm / 1000f;
                    var half = ml.def.HeadHalfWidthMm / 1000f;
                    Assert.That(Mathf.Abs(mesh.bounds.min.y - (-arm - head)), Is.LessThanOrEqualTo(Tol), $"{scene} mallet {ml.obstacleId}: head bottom");
                    var across = ml.def.SwingAxis == 0 ? mesh.bounds.extents.z : mesh.bounds.extents.x;
                    Assert.That(Mathf.Abs(across - half), Is.LessThanOrEqualTo(Tol), $"{scene} mallet {ml.obstacleId}: head half width {across:F3} vs {half:F3}");
                    Assert.That(HasStripe(mesh), Is.True, $"{scene} mallet {ml.obstacleId}: hit stripe");
                    checkedCount++;
                }

                // Capsule Run finish arch: each inflatable piece inside its box collider's bounds (± 0.15 m).
                foreach (var col in Object.FindObjectsByType<BoxCollider>(FindObjectsSortMode.None))
                {
                    var prop = col.transform.Find("Prop");
                    if (prop == null || (!col.name.StartsWith("ArchPost") && !col.name.StartsWith("ArchTop_")))
                        continue;
                    Within(prop.GetComponent<MeshRenderer>().bounds, col.bounds, $"{scene} {col.name}");
                    checkedCount++;
                }
            }

            Assert.That(checkedCount, Is.GreaterThan(40), "props checked across the maps");
            TestContext.WriteLine($"{checkedCount} prop placements checked");
        }
    }
}
