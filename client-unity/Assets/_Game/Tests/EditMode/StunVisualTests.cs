using FallRush.Client;
using FallRush.Shared;
using NUnit.Framework;
using UnityEngine;

namespace FallRush.Tests
{
    /// <summary>
    /// G4.8z: a hit shows dizzy swirls (never stars, never gold), and the body stays visible, full size and above the
    /// ground for every frame of the knock → tumble → get-up (hard = mallet profile, soft = spinner / bumper profile;
    /// full and reduced motion).
    /// </summary>
    public sealed class StunVisualTests
    {
        private static bool IsGoldOrYellow(Color c)
        {
            Color.RGBToHSV(c, out var h, out var s, out var v);
            return h > 0.09f && h < 0.19f && s > 0.4f && v > 0.5f; // 32°–68°: amber … yellow
        }

        [Test]
        public void StunSwirls_InkAndLavender_NoGold_SmallBudget()
        {
            var m = RushlingParts.StunSwirls();
            Assert.That(m.name, Is.EqualTo("StunSwirls"));
            Assert.That(RushlingParts.SwirlCount, Is.InRange(2, 3));
            foreach (var c in RushlingParts.SwirlColours)
                Assert.That(IsGoldOrYellow(c), Is.False, "swirl colour " + c);
            Assert.That(m.GetIndexCount(0) / 3, Is.LessThanOrEqualTo(800), "swirl ring tris");
            Assert.That(Mathf.Max(m.bounds.extents.x, m.bounds.extents.z), Is.InRange(0.2f, 0.45f), "orbits the head (ring radius)");
            Assert.That(FxDirector.BonkSparks(false), Is.InRange(3, 5));
            Assert.That(FxDirector.BonkSparks(true), Is.InRange(3, 5));
        }

        [TestCase(true, false)]
        [TestCase(false, false)]
        [TestCase(true, true)]
        [TestCase(false, true)]
        public void Stun_BodyVisibleFullSize_AboveGround_KnockTumbleGetUp(bool hard, bool reduced)
        {
            var was = GameSettings.ReducedMotion;
            GameSettings.ReducedMotion = reduced;
            var go = new GameObject("RushlingStunTest");
            try
            {
                var v = go.AddComponent<RushlingVisuals>();
                v.Build(0, 0, null, null);
                v.SetVisible(true);
                var squash = go.transform.Find("Tilt/Squash");
                var body = squash.Find("Body").GetComponent<MeshRenderer>();
                var swirls = squash.Find("Swirls");
                Assert.That(swirls, Is.Not.Null);
                var path = KnockPreview.Path(hard, Vector3.zero, Vector3.back);
                var sawSwirls = false;
                var sawGetUp = false;
                for (var i = 0; i < path.Length; i++)
                {
                    var f = path[i];
                    v.Drive(f.Feet, f.Vel, f.Flags, 0, f.Stun, KnockPreview.Dt);
                    sawGetUp |= v.GetUpThisFrame;
                    Assert.That(body.enabled && body.gameObject.activeInHierarchy, Is.True, $"frame {i}: body visible");
                    var sc = squash.lossyScale;
                    Assert.That(Mathf.Min(sc.x, sc.y, sc.z), Is.GreaterThan(0.72f), $"frame {i}: not shrunk ({sc})");
                    Assert.That(Mathf.Max(sc.x, sc.y, sc.z), Is.LessThan(1.35f), $"frame {i}: not blown up ({sc})");
                    // The capsule (radius R about the segment LowerCenter … UpperCenter) never dips into the ground (y = 0).
                    var a = squash.TransformPoint(new Vector3(0f, ProcMesh.LowerCenter, 0f));
                    var b = squash.TransformPoint(new Vector3(0f, ProcMesh.UpperCenter, 0f));
                    var lowest = Mathf.Min(a.y, b.y) - ProcMesh.R * Mathf.Min(sc.x, sc.z);
                    Assert.That(lowest, Is.GreaterThan(-0.06f), $"frame {i}: body clips into the ground by {-lowest:F3} m");
                    if (f.Stun > 0)
                    {
                        Assert.That(swirls.gameObject.activeInHierarchy, Is.True, $"frame {i}: swirls during the stun");
                        sawSwirls = true;
                    }
                }

                Assert.That(sawSwirls, Is.True);
                Assert.That(sawGetUp, Is.True, "a get-up pop after the stun");
            }
            finally
            {
                GameSettings.ReducedMotion = was;
                Object.DestroyImmediate(go);
                var limbs = GameObject.Find("Limbs");
                if (limbs != null)
                    Object.DestroyImmediate(limbs);
            }
        }
    }
}
