using System.IO;
using FallRush.Net;
using FallRush.Shared;
using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace FallRush.Tests
{
    /// <summary>M3: the committed map scenes hash to the values their builders recorded (stable across rebuilds), and their defs pass the allowlist.</summary>
    public class MapSceneTests
    {
        private static string[] Lines() =>
            File.ReadAllLines(Path.Combine(Application.dataPath, "_Game", "Tests", "Vectors", "map_hashes_v1.txt"));

        [Test]
        public void CommittedScenes_HashToTheRecordedValue_AndPassTheAllowlist()
        {
            var lines = Lines();
            Assert.That(lines.Length, Is.GreaterThanOrEqualTo(1));
            foreach (var line in lines)
            {
                var parts = line.Split(' ');
                var scene = EditorSceneManager.OpenScene($"Assets/_Game/Scenes/{parts[0]}.unity", OpenSceneMode.Single);
                Physics.SyncTransforms();
                MapInfo info = null;
                foreach (var root in scene.GetRootGameObjects())
                    info ??= root.GetComponentInChildren<MapInfo>();
                Assert.That(info, Is.Not.Null, parts[0]);
                var h = Sha256.Hex(info.BuildContent().Hash());
                TestContext.WriteLine($"{parts[0]} {h}");
                Assert.That(h, Is.EqualTo(parts[1]), $"{parts[0]}: map content changed without re-running its builder");
                Assert.That(Sha256.Hex(info.BuildContent().Hash()), Is.EqualTo(h), "stable within a process");
                for (byte v = 0; v < 2; v++)
                    for (byte m = 0; m < 6; m++)
                    {
                        if (info.definition != null && info.definition.mapId != MapPoolV1.Sandbox && !MapPoolV1.ModifierAllowed(info.definition.mapId, m))
                            continue;
                        var problems = info.Validate(new RoundParams { MapId = info.MapId, Variant = v, Modifier = m, HazardSeed = 12345 });
                        Assert.That(problems, Is.Empty, $"{parts[0]} variant {v} modifier {m}: {string.Join("; ", problems)}");
                    }
            }
        }
    }
}
