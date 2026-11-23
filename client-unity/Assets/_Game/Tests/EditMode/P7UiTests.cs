using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using FallRush.Client;
using NUnit.Framework;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace FallRush.Tests
{
    /// <summary>P7 A1: UiMotion maths, the card builder's layout sizes, the icon atlas and the HUD glyph coverage (D-GLYPH).</summary>
    public sealed class P7UiTests
    {
        // ------------------------------------------------------------------ UiMotion

        [Test]
        public void BackOut_StartsAtZero_EndsAtOne_Overshoots()
        {
            Assert.AreEqual(0f, UiMotion.BackOut(0f), 1e-5f);
            Assert.AreEqual(1f, UiMotion.BackOut(1f), 1e-5f);
            var peak = 0f;
            for (var t = 0f; t <= 1f; t += 0.01f)
                peak = Mathf.Max(peak, UiMotion.BackOut(t));
            Assert.Greater(peak, 1.05f, "back-out overshoots");
            Assert.Less(peak, 1.15f, "but only a little");
        }

        [Test]
        public void EnterScale_GoesFrom092To1()
        {
            Assert.AreEqual(UiMotion.EnterFrom, UiMotion.EnterScale(0f), 1e-5f);
            Assert.AreEqual(1f, UiMotion.EnterScale(1f), 1e-5f);
            Assert.AreEqual(0.92f, UiMotion.EnterFrom, 1e-6f);
            Assert.AreEqual(0.22f, UiMotion.EnterSeconds, 1e-6f);
            Assert.AreEqual(0.15f, UiMotion.ExitSeconds, 1e-6f);
            Assert.AreEqual(0.04f, UiMotion.StaggerSeconds, 1e-6f);
        }

        [Test]
        public void Bump_PeaksAt112_AndReturns()
        {
            Assert.AreEqual(1f, UiMotion.BumpScale(0f), 1e-4f);
            Assert.AreEqual(1f, UiMotion.BumpScale(1f), 1e-4f);
            var peak = 0f;
            for (var t = 0f; t <= 1f; t += 0.005f)
                peak = Mathf.Max(peak, UiMotion.BumpScale(t));
            Assert.AreEqual(1.12f, peak, 0.005f);
            Assert.AreEqual(0.18f, UiMotion.BumpSeconds, 1e-6f);
        }

        [Test]
        public void Shake_IsZeroAtBothEnds_AndBounded()
        {
            Assert.AreEqual(0f, UiMotion.ShakeOffset(0f), 1e-4f);
            Assert.AreEqual(0f, UiMotion.ShakeOffset(1f), 1e-4f);
            for (var t = 0f; t <= 1f; t += 0.01f)
                Assert.LessOrEqual(Mathf.Abs(UiMotion.ShakeOffset(t)), UiMotion.ShakePixels + 1e-3f);
        }

        [Test]
        public void CountUp_EasesOut_WithinSixTenths()
        {
            Assert.LessOrEqual(UiMotion.CountSeconds(1e9), UiMotion.CountMax);
            Assert.Greater(UiMotion.CountSeconds(5000), UiMotion.CountSeconds(5));
            var was = GameSettings.ReducedMotion;
            GameSettings.ReducedMotion = false;
            try
            {
                var c = new CountUp();
                c.Set(100, 0f);
                Assert.AreEqual(100, c.Value(0f), 1e-9, "the first value is set at once");
                c.Set(1100, 10f);
                Assert.AreEqual(100, c.Value(10f), 1e-6);
                var mid = c.Value(10f + UiMotion.CountSeconds(1000) * 0.5f);
                Assert.Greater(mid, 600, "ease-out: past halfway at half time");
                Assert.AreEqual(1100, c.Value(10f + UiMotion.CountMax + 0.01f), 1e-6);
                Assert.IsFalse(c.Running(10f + UiMotion.CountMax + 0.01f));
            }
            finally { GameSettings.ReducedMotion = was; }
        }

        [Test]
        public void CountUp_ReducedMotion_JumpsToTarget()
        {
            var was = GameSettings.ReducedMotion;
            GameSettings.ReducedMotion = true;
            try
            {
                var c = new CountUp();
                c.Set(1, 0f);
                c.Set(50, 1f);
                Assert.AreEqual(50, c.Value(1f), 1e-9);
            }
            finally { GameSettings.ReducedMotion = was; }
        }

        // ------------------------------------------------------------------ cards

        [Test]
        public void CardSizes_AreOrdered_AndReadableAt720p()
        {
            Assert.Less(UiKit.CardRibbon(UiKit.CardSize.S), UiKit.CardRibbon(UiKit.CardSize.M));
            Assert.Less(UiKit.CardRibbon(UiKit.CardSize.M), UiKit.CardRibbon(UiKit.CardSize.L));
            Assert.Less(UiKit.CardTitleSize(UiKit.CardSize.S), UiKit.CardTitleSize(UiKit.CardSize.L));
            // doc 17 §5: minimum body size 18 px at 1080p = 12 units at the 1280 × 720 reference
            foreach (UiKit.CardSize s in System.Enum.GetValues(typeof(UiKit.CardSize)))
                Assert.GreaterOrEqual(UiKit.CardTitleSize(s), 12);
        }

        [Test]
        public void CardHues_SoftLook_PastelBodySaturatedRim()
        {
            foreach (UiKit.CardHue h in System.Enum.GetValues(typeof(UiKit.CardHue)))
            {
                Color.RGBToHSV(UiKit.CardPastel(h), out _, out var ps, out var pv);
                Color.RGBToHSV(UiKit.CardRim(h), out _, out var rs, out _);
                Assert.That(ps, Is.InRange(0.10f, 0.30f), $"{h} body is pastel (doc 17 §3.0)");
                Assert.Greater(pv, 0.9f, $"{h} body is light");
                Assert.Greater(rs, ps + 0.25f, $"{h} rim carries the saturated hue");
            }

            Color.RGBToHSV(UiKit.Cream, out _, out var cs, out var cv);
            Assert.Greater(cs, 0.04f, "panel cream is never pure white");
            Assert.Greater(cv, 0.95f);
        }

        [Test]
        public void MakeCard_BuildsLayers_AndKeepsTheBodyBelowTheRibbon()
        {
            var root = new GameObject("CanvasRoot", typeof(RectTransform), typeof(Canvas));
            try
            {
                var c = UiKit.MakeCard(root.transform, UiKit.CardSize.M, UiKit.CardHue.Soda, "TEST", new Vector2(400f, 300f), UiIcon.Crown);
                Assert.AreEqual(new Vector2(400f, 300f), c.Root.sizeDelta);
                Assert.IsNotNull(c.Shadow);
                Assert.IsNotNull(c.Rim);
                Assert.IsNotNull(c.Fill);
                Assert.IsNotNull(c.Icon);
                Assert.IsNotNull(c.Icon.sprite, "the icon comes from the atlas");
                Assert.AreEqual("TEST", c.Title.text);
                Assert.AreEqual(-UiKit.CardRibbon(UiKit.CardSize.M), c.Body.offsetMax.y, 1e-4f, "the body starts under the ribbon");
                Assert.IsNotNull(c.Body.GetComponent<VerticalLayoutGroup>());
                c.SetHue(UiKit.CardHue.Bubblegum);
                Assert.AreEqual(UiKit.CardRim(UiKit.CardHue.Bubblegum), c.Rim.color);
                Assert.AreEqual(UiKit.CardPastel(UiKit.CardHue.Bubblegum), c.Fill.color);
            }
            finally { Object.DestroyImmediate(root); }
        }

        // ------------------------------------------------------------------ icons

        [Test]
        public void EveryIcon_HasInk_AndEmptyCorners()
        {
            foreach (UiIcon i in System.Enum.GetValues(typeof(UiIcon)))
            {
                if (i == UiIcon.None)
                    continue;
                var inside = 0;
                for (var y = 0; y < 32; y++)
                    for (var x = 0; x < 32; x++)
                        if (UiIcons.Sdf(i, new Vector2((x + 0.5f) / 32f, (y + 0.5f) / 32f)) < 0f)
                            inside++;
                Assert.That(inside, Is.InRange(40, 32 * 32 - 200), $"{i}: a readable glyph, not empty, not a full square");
                Assert.Greater(UiIcons.Sdf(i, new Vector2(0.02f, 0.02f)), 0f, $"{i}: the corner is clear");
                Assert.IsNotNull(UiIcons.Get(i), $"{i}: sprite");
            }
        }

        // ------------------------------------------------------------------ glyph coverage (D-GLYPH)

        private static HashSet<uint> FontChain()
        {
            var set = new HashSet<uint> { 9, 10, 13 };
            foreach (var name in new[] { "FallRush/Fonts/FR_Body", "FallRush/Fonts/FR_Display", "FallRush/Fonts/FR_Symbols" })
            {
                var f = Resources.Load<TMP_FontAsset>(name);
                Assert.IsNotNull(f, name);
                foreach (var c in f.characterTable)
                    set.Add(c.unicode);
            }

            return set;
        }

        /// <summary>
        /// Every character in a string literal of the player-facing UI code (Client, OnChain) is drawn by the static font
        /// chain (Lilita One / Varela Round + Noto Sans Symbols 2 fallback). Emoji and other symbols are icons from
        /// <see cref="UiIcons"/> instead (never emoji fonts in WebGL). Log lines and comments are skipped.
        /// </summary>
        [Test]
        public void HudStrings_AreCoveredByTheFontChain()
        {
            var body = Resources.Load<TMP_FontAsset>("FallRush/Fonts/FR_Body");
            var symbols = Resources.Load<TMP_FontAsset>("FallRush/Fonts/FR_Symbols");
            Assert.IsNotNull(body);
            Assert.IsTrue(body.fallbackFontAssetTable.Contains(symbols), "FR_Body falls back to FR_Symbols");
            var chain = FontChain();
            var scripts = Path.Combine(Application.dataPath, "_Game", "Scripts");
            var lit = new Regex("\"((?:[^\"\\\\]|\\\\.)*)\"");
            var missing = new List<string>();
            foreach (var dir in new[] { "Client", "OnChain" })
                foreach (var file in Directory.GetFiles(Path.Combine(scripts, dir), "*.cs", SearchOption.AllDirectories))
                {
                    var lines = File.ReadAllLines(file);
                    for (var n = 0; n < lines.Length; n++)
                    {
                        var l = lines[n].Trim();
                        if (l.StartsWith("//") || l.Contains("Debug.Log"))
                            continue;
                        foreach (Match m in lit.Matches(lines[n]))
                            foreach (var ch in m.Groups[1].Value)
                                if (ch > 126 && !chain.Contains(ch))
                                    missing.Add($"{Path.GetFileName(file)}:{n + 1} U+{(int)ch:X4} '{ch}'");
                    }
                }

            Assert.IsEmpty(missing, "characters outside the font chain (use a UiIcons sprite):\n" + string.Join("\n", missing));
        }
    }
}
