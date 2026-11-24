using FallRush.Client;
using FallRush.Shared;
using NUnit.Framework;
using UnityEngine;

namespace FallRush.Tests
{
    /// <summary>
    /// G4.8z + dizzy hits (2026-11-24): a hit shows classic yellow dizzy stars round the head (user pick; they replaced the
    /// G4.8z swirls), the stars outlast the stun by <see cref="RushlingVisuals.DizzyTail"/>, and the body stays visible,
    /// full size and above the ground for every frame of the knock → tumble → get-up (hard = mallet profile, soft =
    /// spinner / bumper profile; full and reduced motion). Only your own hits make a sound.
    /// </summary>
    public sealed class StunVisualTests
    {
        private static bool IsYellow(Color c)
        {
            Color.RGBToHSV(c, out var h, out var s, out var v);
            return h > 0.09f && h < 0.19f && s > 0.4f && v > 0.5f; // 32°–68°: amber … yellow
        }

        [Test]
        public void DizzyStars_ClassicYellow_RingRoundTheHead_SmallBudget()
        {
            var m = RushlingParts.DizzyStars();
            Assert.That(m.name, Is.EqualTo("DizzyStars"));
            Assert.That(RushlingParts.StarCount, Is.InRange(3, 4));
            Assert.That(IsYellow(RushlingParts.StarYellow), Is.True, "star yellow");
            Assert.That(RushlingParts.StarRim.grayscale, Is.LessThan(RushlingParts.StarYellow.grayscale - 0.1f), "the rim is darker than the star");
            Assert.That(m.GetIndexCount(0) / 3, Is.LessThanOrEqualTo(800), "star ring tris");
            Assert.That(Mathf.Max(m.bounds.extents.x, m.bounds.extents.z), Is.InRange(0.25f, 0.6f), "orbits the head (ring radius)");
            Assert.That(FxDirector.BonkSparks(false), Is.InRange(3, 5));
            Assert.That(FxDirector.BonkSparks(true), Is.InRange(3, 5));
            Assert.That(FxDirector.BonkPuffs(false), Is.LessThanOrEqualTo(2), "small puff: never covers the Rushling");
        }

        [Test]
        public void HitSounds_OwnOnly()
        {
            var sfx = new System.Collections.Generic.List<GameAudio.Sfx>();
            var chirps = new System.Collections.Generic.List<int>();
            void OnSfx(GameAudio.Sfx s) => sfx.Add(s);
            void OnChirp(int m) => chirps.Add(m);
            GameAudio.Requested += OnSfx;
            GameAudio.ChirpRequested += OnChirp;
            try
            {
                GameAudio.Hits.Stunned(false, Vector3.zero);
                GameAudio.Hits.GotUp(false, Vector3.zero);
                GameAudio.Hits.KnockedOut(false, false);
                GameAudio.Hits.KnockedOut(false, true);
                Assert.That(sfx, Is.Empty, "another player's or a bot's hit is silent");
                Assert.That(chirps, Is.Empty);

                GameAudio.Hits.Stunned(true, Vector3.zero);
                Assert.That(sfx, Is.EqualTo(new[] { GameAudio.Sfx.Dizzy }));
                Assert.That(chirps, Has.No.Member(1), "no sad chirp");
                GameAudio.Hits.KnockedOut(true, false);
                Assert.That(sfx[sfx.Count - 1], Is.EqualTo(GameAudio.Sfx.Wheee));
                Assert.That(sfx, Has.No.Member(GameAudio.Sfx.Bonk));
                Assert.That(sfx, Has.No.Member(GameAudio.Sfx.CapsulePop));
            }
            finally
            {
                GameAudio.Requested -= OnSfx;
                GameAudio.ChirpRequested -= OnChirp;
            }
        }

        [Test]
        public void DizzyStars_OutlastTheStun_ThenWindDown()
        {
            var go = new GameObject("RushlingDizzyTest");
            try
            {
                var v = go.AddComponent<RushlingVisuals>();
                v.Build(0, 0, null, null);
                v.SetVisible(true);
                var stars = go.transform.Find("Tilt/Squash/Swirls");
                var path = KnockPreview.Path(false, Vector3.zero, Vector3.back);
                var last = path[path.Length - 1];
                // After the stun ends (stun 0, standing again) the stars stay ~DizzyTail, then go.
                var on = 0f;
                var hit = false;
                foreach (var f in path)
                {
                    v.Drive(f.Feet, f.Vel, f.Flags, 0, f.Stun, KnockPreview.Dt);
                    hit |= f.Stun > 0;
                    if (hit && f.Stun == 0 && stars.gameObject.activeSelf)
                        on += KnockPreview.Dt;
                }

                for (var t = 0f; t < RushlingVisuals.DizzyTail + 1f; t += KnockPreview.Dt)
                {
                    v.Drive(last.Feet, Vector3.zero, MotorState.Grounded, 0, 0, KnockPreview.Dt);
                    if (stars.gameObject.activeSelf)
                        on += KnockPreview.Dt;
                }

                Assert.That(on, Is.GreaterThan(RushlingVisuals.DizzyTail * 0.7f), "stars after the get-up");
                Assert.That(stars.gameObject.activeSelf, Is.False, "and then they stop");

                // A respawn after a knock-off fall: Dizzy() alone turns them on.
                v.Dizzy(1f);
                v.Drive(last.Feet, Vector3.zero, MotorState.Grounded, 0, 0, KnockPreview.Dt);
                Assert.That(stars.gameObject.activeSelf, Is.True);
            }
            finally
            {
                Object.DestroyImmediate(go);
                var limbs = GameObject.Find("Limbs");
                if (limbs != null)
                    Object.DestroyImmediate(limbs);
            }
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
                        Assert.That(swirls.gameObject.activeInHierarchy, Is.True, $"frame {i}: stars during the stun");
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
