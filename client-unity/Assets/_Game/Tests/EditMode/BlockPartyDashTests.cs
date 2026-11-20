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
    /// <c>Map_BlockPartyDash</c> scene and runs a full race with 12 waypoint bots to <c>Ended</c>.
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
    }
}
