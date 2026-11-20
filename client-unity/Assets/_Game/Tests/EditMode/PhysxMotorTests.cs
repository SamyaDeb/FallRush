using System.Collections.Generic;
using FallRush.Net;
using FallRush.Shared;
using NUnit.Framework;
using UnityEngine;

namespace FallRush.Tests
{
    /// <summary>
    /// The same motor checks against real PhysX scene queries (BoxColliders in the edit scene), because step-up and
    /// edge handling depend on the normals PhysX reports for capsule sweeps.
    /// </summary>
    public class PhysxMotorTests
    {
        private static readonly MovementConfig Cfg = MovementConfig.Default;
        private readonly List<GameObject> _objects = new List<GameObject>();
        private PhysxMotorWorld _world;

        [SetUp]
        public void SetUp()
        {
            var probeHost = new GameObject("ProbeHost");
            _objects.Add(probeHost);
            var probe = PhysxMotorWorld.CreateProbe(probeHost.transform, Cfg.Radius, Cfg.Height);
            // Default layer (0) in tests; the probe is on Ignore Raycast (2) and excluded.
            _world = new PhysxMotorWorld(Physics.defaultPhysicsScene, 1 << 0, probe);
            Box(new Vector3(0, -0.5f, 0), new Vector3(200, 1, 200), Quaternion.identity);
        }

        [TearDown]
        public void TearDown()
        {
            foreach (var o in _objects)
                Object.DestroyImmediate(o);
            _objects.Clear();
        }

        private void Box(Vector3 c, Vector3 size, Quaternion rot)
        {
            var go = new GameObject("Box");
            go.transform.SetPositionAndRotation(c, rot);
            go.transform.localScale = size;
            go.AddComponent<BoxCollider>();
            _objects.Add(go);
            Physics.SyncTransforms();
        }

        private void Ramp(float z0, float degrees, float height)
        {
            var a = degrees * Mathf.Deg2Rad;
            var length = height / Mathf.Sin(a);
            var run = height / Mathf.Tan(a);
            var rot = Quaternion.Euler(-degrees, 0f, 0f);
            var n = rot * Vector3.up;
            Box(new Vector3(0, height * 0.5f, z0 + run * 0.5f) - n * 0.2f, new Vector3(5, 0.4f, length), rot);
            Box(new Vector3(0, height * 0.5f - 0.25f, z0 + run + 2f), new Vector3(5, height + 0.5f, 4f), Quaternion.identity);
        }

        /// <summary>Runs with constant input; returns the final state and the highest feet height at which it stood grounded.</summary>
        private MotorState Run(Vec3 start, int ticks, sbyte mx, sbyte my, out float maxGroundedY, byte buttonsFirstTick = 0)
        {
            var m = new KinematicMotor(Cfg);
            var s = new MotorState { Position = start };
            for (var i = 0; i < 30; i++)
                m.Step(ref s, default, Cfg.Dt, _world);
            Assert.That(s.Has(MotorState.Grounded), Is.True, "settled");
            maxGroundedY = s.PosY / 1000f;
            for (var t = 0; t < ticks; t++)
            {
                m.Step(ref s, new InputCmd { MoveX = mx, MoveY = my, Buttons = t == 0 ? buttonsFirstTick : (byte)0 }, Cfg.Dt, _world);
                if (s.Has(MotorState.Grounded))
                    maxGroundedY = Mathf.Max(maxGroundedY, s.PosY / 1000f);
            }

            return s;
        }

        [Test]
        public void Physx_FlatRun_MaxSpeedAndGrounded()
        {
            var s = Run(new Vec3(0, 0.05f, -50), 60, 0, 127, out _);
            Assert.That(s.VelZ, Is.EqualTo(700).Within(1));
            Assert.That(s.Has(MotorState.Grounded), Is.True);
            Assert.That(s.PosY, Is.InRange(0, 20), "rests ~skin above the ground");
        }

        [Test]
        public void Physx_JumpApex()
        {
            var m = new KinematicMotor(Cfg);
            var s = new MotorState { Position = new Vec3(0, 0.05f, -50) };
            for (var i = 0; i < 30; i++)
                m.Step(ref s, default, Cfg.Dt, _world);
            var y0 = s.PosY;
            var max = y0;
            m.Step(ref s, new InputCmd { Buttons = InputCmd.JumpPressed }, Cfg.Dt, _world);
            for (var i = 0; i < 60; i++)
            {
                m.Step(ref s, default, Cfg.Dt, _world);
                max = System.Math.Max(max, s.PosY);
            }

            Assert.That((max - y0) / 1000f, Is.EqualTo(1.6f).Within(0.02f));
        }

        [TestCase(15f, true)]
        [TestCase(30f, true)]
        [TestCase(44f, true)]
        [TestCase(50f, false)]
        public void Physx_SlopeLimit(float degrees, bool climbs)
        {
            Ramp(0f, degrees, 2f);
            Run(new Vec3(0, 0.05f, -2f), 75, 0, 127, out var top);
            if (climbs)
                Assert.That(top, Is.GreaterThan(1.9f), $"{degrees}° should be climbable");
            else
                Assert.That(top, Is.LessThan(0.3f), $"{degrees}° should block");
        }

        [TestCase(0.2f, true)]
        [TestCase(0.3f, false)]
        public void Physx_StepOffset(float height, bool climbs)
        {
            Box(new Vector3(0, height * 0.5f, 3f), new Vector3(6, height, 4), Quaternion.identity);
            var s = Run(new Vec3(0, 0.05f, -1f), 30, 0, 127, out var top);
            if (climbs)
            {
                Assert.That(top, Is.EqualTo(height + Cfg.Skin).Within(0.01f));
            }
            else
            {
                Assert.That(top, Is.LessThan(0.05f));
                Assert.That(s.PosZ / 1000f, Is.LessThan(1f));
            }
        }

        [TestCase(1.5f, true)]
        [TestCase(1.7f, false)]
        public void Physx_JumpOntoWall(float wallHeight, bool reachesTop)
        {
            Box(new Vector3(0, wallHeight * 0.5f, 3f), new Vector3(6, wallHeight, 3), Quaternion.identity);
            var m = new KinematicMotor(Cfg);
            var s = new MotorState { Position = new Vec3(0, 0.05f, 0.4f) };
            for (var i = 0; i < 30; i++)
                m.Step(ref s, default, Cfg.Dt, _world);
            // Jump from 0.4 m in front of the wall face (z = 1.5) while holding forward.
            var top = 0f;
            for (var t = 0; t < 45; t++)
            {
                m.Step(ref s, new InputCmd { MoveY = 127, Buttons = t == 0 ? InputCmd.JumpPressed : (byte)0 }, Cfg.Dt, _world);
                if (s.Has(MotorState.Grounded))
                    top = Mathf.Max(top, s.PosY / 1000f);
            }

            Assert.That(top >= wallHeight, Is.EqualTo(reachesTop), $"highest grounded feet height {top:F3} m");
        }

        [Test]
        public void Physx_Determinism_SameLog()
        {
            Ramp(5f, 30f, 2f);
            Box(new Vector3(4, 0.1f, 0), new Vector3(2, 0.2f, 2), Quaternion.identity);
            var rng = new System.Random(5);
            var log = new InputCmd[900];
            for (var i = 0; i < log.Length; i++)
                log[i] = new InputCmd { MoveX = (sbyte)rng.Next(-127, 128), MoveY = (sbyte)rng.Next(-127, 128), Buttons = rng.Next(15) == 0 ? InputCmd.JumpPressed : (byte)0 };
            for (var i = 0; i < log.Length; i++)
                InputSanitizer.Sanitize(ref log[i]);

            var m = new KinematicMotor(Cfg);
            var a = new MotorState { Position = new Vec3(0, 0.05f, 0) };
            var b = a;
            var statesA = new MotorState[log.Length];
            for (var i = 0; i < log.Length; i++)
            {
                m.Step(ref a, log[i], Cfg.Dt, _world);
                statesA[i] = a;
            }

            for (var i = 0; i < log.Length; i++)
            {
                m.Step(ref b, log[i], Cfg.Dt, _world);
                Assert.That(b, Is.EqualTo(statesA[i]), $"tick {i}");
            }
        }
    }
}
