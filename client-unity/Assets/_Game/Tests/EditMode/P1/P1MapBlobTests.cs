using System.Text.RegularExpressions;
using FallRush.Net;
using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace FallRush.Tests
{
    /// <summary>P1-K: the map-blob exporter is deterministic and exports every Gameplay-layer solid of each map scene.</summary>
    public class P1MapBlobTests
    {
        private static readonly string[] Scenes = Build.MapScenes;

        [Test]
        public void ExportIsDeterministicAndComplete([ValueSource(nameof(Scenes))] string scene)
        {
            var a = MapBlobExport.ExportScene(scene, out var key, out var counts);
            var b = MapBlobExport.ExportScene(scene, out _, out _);
            Assert.AreEqual(a, b, $"{key}: two exports differ");

            // Independent count of the solids PhysX queries see (Gameplay layer, not a trigger; lids / tiles / blocks
            // are counted even when switched off, because their state is posed per tick).
            var s = EditorSceneManager.OpenScene(scene, OpenSceneMode.Single);
            var layer = LayerMask.NameToLayer(PhysxMotorWorld.GameplayLayerName);
            MapInfo info = null;
            foreach (var r in s.GetRootGameObjects())
                info ??= r.GetComponentInChildren<MapInfo>(true);
            info.BuildHazards(default, out var cols, zeroPhase: true);
            var toggled = new System.Collections.Generic.HashSet<Collider>();
            foreach (var c in cols.Lids) toggled.Add(c);
            foreach (var c in cols.Tiles) toggled.Add(c);
            foreach (var c in cols.Blocks) toggled.Add(c);
            var n = 0;
            foreach (var r in s.GetRootGameObjects())
            foreach (var c in r.GetComponentsInChildren<Collider>(true))
                if (c.gameObject.layer == layer && !c.isTrigger && (toggled.Contains(c) || (c.enabled && c.gameObject.activeInHierarchy)))
                    n++;
            var m = Regex.Match(counts, @"colliders (\d+)");
            Assert.AreEqual(n, int.Parse(m.Groups[1].Value), $"{key}: {counts}");
            Debug.Log($"[p1] {key} {counts}");
        }
    }
}
