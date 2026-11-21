using FallRush.Client;
using NUnit.Framework;
using UnityEngine;

namespace FallRush.Tests
{
    /// <summary>G4.10: the camera tunables (classic = the G1 camera, close = the default), the dev switch and the orbit pose.</summary>
    public sealed class CameraRigTests
    {
        [Test]
        public void Classic_IsTheG1Camera_Close_IsTheDefault_SwitchParses()
        {
            Assert.That(CameraRig.Classic.Distance, Is.EqualTo(FollowCamera.Distance));
            Assert.That(CameraRig.Classic.Elevation, Is.EqualTo(FollowCamera.Elevation));
            Assert.That(CameraRig.Classic.Fov, Is.EqualTo(FollowCamera.BaseFov));
            Assert.That(CameraRig.Default, Is.EqualTo(CameraRig.Id.Close));
            Assert.That(CameraRig.Parse("classic"), Is.EqualTo(CameraRig.Id.Classic));
            Assert.That(CameraRig.Parse("close"), Is.EqualTo(CameraRig.Id.Close));
            Assert.That(CameraRig.Parse("x"), Is.Null);
            Assert.That(CameraRig.Close.Elevation, Is.LessThan(CameraRig.Classic.Elevation), "close is lower");
        }

        [TestCase(CameraRig.Id.Classic)]
        [TestCase(CameraRig.Id.Close)]
        public void Pose_OrbitsTheTarget_AtThePresetDistance_AndARushlingIs12To15PercentTall(CameraRig.Id id)
        {
            var p = CameraRig.Get(id);
            var feet = new Vector3(3f, 2f, -5f);
            CameraRig.Pose(p, feet, 30f, out var eye, out var at);
            Assert.That((eye - (feet + Vector3.up * CameraRig.TargetHeight)).magnitude, Is.EqualTo(p.Distance).Within(1e-3f));
            Assert.That(at.y, Is.EqualTo(feet.y + CameraRig.TargetHeight + p.LookOffsetY).Within(1e-4f));
            var go = new GameObject("RigCam");
            try
            {
                var cam = go.AddComponent<Camera>();
                cam.fieldOfView = p.Fov;
                cam.aspect = 16f / 9f;
                go.transform.SetPositionAndRotation(eye, Quaternion.LookRotation(at - eye));
                var share = cam.WorldToViewportPoint(feet + Vector3.up).y - cam.WorldToViewportPoint(feet).y;
                Assert.That(share, Is.InRange(0.12f, 0.15f), $"{id}: Rushling share {share:P1}");
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }
    }
}
