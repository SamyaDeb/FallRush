using System;
using FallRush.Client;
using FallRush.Shared;
using NUnit.Framework;
using TMPro;
using UnityEngine;

namespace FallRush.Tests
{
    /// <summary>G3 juice: chain chip states (G3.6), the practice adapter, every audio cue has a clip, every UI glyph renders, juice hooks.</summary>
    public sealed class JuiceTests
    {
        [Test]
        public void ChainChip_FourStates_MapToDoc17Styles()
        {
            Assert.That(ChainChipStyle.Text(ChainState.Server), Is.EqualTo("● Server"));
            Assert.That(ChainChipStyle.Text(ChainState.RollupConfirmed), Is.EqualTo("◆ Rollup ✓"));
            Assert.That(ChainChipStyle.Text(ChainState.SolanaConfirmed), Is.EqualTo("⬢ Solana ✓"));
            Assert.That(ChainChipStyle.Text(ChainState.Pending), Is.EqualTo("! Pending"));
            Assert.That(ChainChipStyle.Color(ChainState.Server), Is.EqualTo(UiKit.Grey));
            Assert.That(ChainChipStyle.Color(ChainState.RollupConfirmed), Is.EqualTo(UiKit.Mint));
            Assert.That(ChainChipStyle.Color(ChainState.SolanaConfirmed), Is.EqualTo(UiKit.SunYellow));
            Assert.That(ChainChipStyle.Color(ChainState.Pending), Is.EqualTo(UiKit.Amber));
        }

        [Test]
        public void PracticeAdapter_ServerState_LocalSeed_NoLinks()
        {
            var a = PracticeChainAdapter.Current;
            Assert.That(a.IsPractice, Is.True);
            Assert.That(a.State, Is.EqualTo(ChainState.Server));
            var m = new MatchStateMessage();
            for (var i = 0; i < 32; i++)
                m.Seed[i] = (byte)i;
            a.OnMatchState(m);
            Assert.That(a.Deal.SeedHex, Is.EqualTo(Sha256.Hex(m.Seed)));
            Assert.That(a.Deal.Headline, Is.EqualTo("Practice deal (local seed)"));
            Assert.That(a.Deal.Verified, Is.False);
            Assert.That(a.Deal.VrfSignature, Is.Null);
            Assert.That(a.Receipt.RollupTx, Is.Null);
            Assert.That(a.Receipt.SolanaTx, Is.Null);
            Assert.That(a.Receipt.MatchAccount, Is.Null);
        }

        [Test]
        public void Audio_EveryCueHasAClip()
        {
            foreach (var n in Enum.GetNames(typeof(GameAudio.Sfx)))
                Assert.That(Resources.Load<AudioClip>("FallRush/Audio/sfx_" + n.ToLowerInvariant()), Is.Not.Null, "sfx " + n);
            foreach (var n in Enum.GetNames(typeof(GameAudio.UiCue)))
                Assert.That(Resources.Load<AudioClip>("FallRush/Audio/ui_" + n.ToLowerInvariant()), Is.Not.Null, "ui " + n);
            foreach (var n in new[] { "music_lobby", "music_race", "music_final", "music_victory", "chirp_0", "chirp_1", "chirp_2", "chirp_3" })
                Assert.That(Resources.Load<AudioClip>("FallRush/Audio/" + n), Is.Not.Null, n);
        }

        [Test]
        public void Fonts_EveryUiGlyphRenders_WithFallback()
        {
            var display = Resources.Load<TMP_FontAsset>("FallRush/Fonts/FR_Display");
            var body = Resources.Load<TMP_FontAsset>("FallRush/Fonts/FR_Body");
            Assert.That(display, Is.Not.Null);
            Assert.That(body, Is.Not.Null);
            const string used = "●○▼▶◀◆⬢★✓♛♥·…–—×°";
            foreach (var f in new[] { display, body })
            {
                foreach (var ch in used)
                    Assert.That(f.HasCharacter(ch, searchFallbacks: true), Is.True, $"{f.name} lacks '{ch}'");
                for (var c = 32; c < 127; c++)
                    Assert.That(f.HasCharacter((char)c), Is.True, $"{f.name} lacks ASCII {c}");
                Assert.That(f.atlasPopulationMode, Is.EqualTo(AtlasPopulationMode.Static), "static atlas: no source font in the build");
            }

            // Tabular figures in the body face (tables, the timer).
            var adv = body.characterLookupTable['0'].glyph.metrics.horizontalAdvance;
            foreach (var d in "123456789")
                Assert.That(body.characterLookupTable[d].glyph.metrics.horizontalAdvance, Is.EqualTo(adv).Within(0.01f), "digit " + d);
        }

        [Test]
        public void Rushling_JuiceHooks_LandingAndDive()
        {
            var go = new GameObject("RushlingTest");
            try
            {
                var v = go.AddComponent<RushlingVisuals>();
                v.Build(0, 0, null, null);
                v.SetVisible(true);
                const float dt = 1f / 30f;
                v.Drive(Vector3.zero, Vector3.zero, MotorState.Grounded, 0, 0, dt);
                for (var i = 0; i < 10; i++)
                    v.Drive(new Vector3(0f, 1f, 0f), new Vector3(0f, -9f, 0f), 0, 0, 0, dt); // falling
                v.Drive(Vector3.zero, Vector3.zero, MotorState.Grounded, 0, 0, dt);
                Assert.That(v.LandedThisFrame, Is.True);
                Assert.That(v.LandImpact, Is.GreaterThan(0.5f));
                v.Drive(Vector3.zero, Vector3.zero, MotorState.Grounded, 0, 0, dt);
                Assert.That(v.LandedThisFrame, Is.False, "one frame only");
                v.Drive(Vector3.zero, new Vector3(0f, 2f, 7f), MotorState.Diving, 0, 0, dt);
                Assert.That(v.DiveStartedThisFrame, Is.True);
                v.Drive(Vector3.zero, Vector3.zero, MotorState.Grounded, 0, 20, dt);
                Assert.That(v.StunStartedThisFrame, Is.True);
                v.PlayEmote(2);
                v.Drive(Vector3.zero, Vector3.zero, MotorState.Grounded, 0, 0, dt);
                Assert.That(v.EmoteStartedThisFrame, Is.True);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(go);
                var limbs = GameObject.Find("Limbs");
                if (limbs != null)
                    UnityEngine.Object.DestroyImmediate(limbs);
            }
        }
    }
}
