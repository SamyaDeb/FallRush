using System;
using FallRush.Client;
using FallRush.Net;
using FallRush.Shared;
using NUnit.Framework;
using UnityEngine;

namespace FallRush.Tests
{
    /// <summary>G2 look: palette tokens (doc 17 §3.1) and readability rules, cosmetic presets on the wire, Rushling budget.</summary>
    public sealed class LookTests
    {
        private static string Hex(Color c) => ColorUtility.ToHtmlStringRGB(c);

        [Test]
        public void Palette_TokensMatchDoc17()
        {
            var p = ScriptableObject.CreateInstance<PaletteTokens>();
            // G4.6 meaning colours (reserved).
            Assert.That(Hex(p.skyTop), Is.EqualTo("2E9BFF"));
            Assert.That(Hex(p.skyHorizon), Is.EqualTo("A6E3FF"));
            Assert.That(Hex(p.hazardMagenta), Is.EqualTo("FF2D8A"));
            Assert.That(Hex(p.hazardYellow), Is.EqualTo("FFD43B"));
            Assert.That(Hex(p.mint), Is.EqualTo("2FE0A8"));
            Assert.That(Hex(p.goo), Is.EqualTo("9B3CFF"));
            // Brand / UI tokens (unchanged).
            Assert.That(Hex(p.magenta), Is.EqualTo("FF3EA5"));
            Assert.That(Hex(p.sunYellow), Is.EqualTo("FFD21F"));
            Assert.That(Hex(p.grape), Is.EqualTo("7B2FF7"));
            Assert.That(Hex(p.cloudWhite), Is.EqualTo("FFFFFF"));
            // G4.6 surface hues: tops (the plan hues brought into the top band) and sides.
            var tops = new[] { "EB66A0", "EBC53D", "46B2EB", "86E04E", "9A73EB", "EB903D" };
            var sides = new[] { "D8327F", "F09A12", "1F82E0", "3FA82F", "6D3FE0", "E05A1F" };
            Assert.That(p.hueTops.Length, Is.EqualTo(6));
            for (var i = 0; i < 6; i++)
            {
                Assert.That(Hex(p.hueTops[i]), Is.EqualTo(tops[i]), ((SurfaceHue)i).ToString());
                Assert.That(Hex(p.hueSides[i]), Is.EqualTo(sides[i]), ((SurfaceHue)i).ToString());
            }

            // Light: warm sun, cool sky ambient, warm violet ground.
            Assert.That(Hex(p.sun), Is.EqualTo("FFF1DC"));
            Assert.That(Hex(p.ambientSky), Is.EqualTo("BFD9FF"));
            Assert.That(Hex(p.ambientGround), Is.EqualTo("C2A6D6"));
            UnityEngine.Object.DestroyImmediate(p);
        }

        private static float Luma(Color c) => c.r * 0.2126f + c.g * 0.7152f + c.b * 0.0722f;

        [Test]
        public void Palette_ReadabilityRules()
        {
            var p = ScriptableObject.CreateInstance<PaletteTokens>();
            for (var i = 0; i < p.hueTops.Length; i++)
            {
                var name = ((SurfaceHue)i).ToString();
                // Every surface-hue top is candy: S 0.45–0.75, V 0.85–0.95, never white.
                PaletteTokens.Sv(p.hueTops[i], out var s, out var v);
                Assert.That(s, Is.InRange(0.45f, 0.75f), name + " top saturation");
                Assert.That(v, Is.InRange(0.85f, 0.95f), name + " top value");
                Assert.That(p.hueTops[i], Is.Not.EqualTo(Color.white), name);
                // Its side is the same hue, darker (luma) and at least as saturated.
                PaletteTokens.Sv(p.hueSides[i], out var ss, out _);
                Assert.That(Luma(p.hueSides[i]), Is.LessThan(Luma(p.hueTops[i]) - 0.05f), name + " side darker than its top");
                Assert.That(ss, Is.GreaterThanOrEqualTo(s), name + " side saturation");
                Color.RGBToHSV(p.hueTops[i], out var ht, out _, out _);
                Color.RGBToHSV(p.hueSides[i], out var hs, out _, out _);
                var dh = Mathf.Abs(ht - hs);
                Assert.That(Mathf.Min(dh, 1f - dh), Is.LessThan(0.05f), name + " side keeps the hue (< 18°)");
            }

            // Hazards: high saturation (and always striped: the stripe material / merged stripe segments).
            foreach (var h in new[] { p.hazardYellow, p.hazardMagenta })
            {
                PaletteTokens.Sv(h, out var s, out _);
                Assert.That(s, Is.GreaterThan(0.7f), "hazards saturated");
            }

            // The two hazard stripe colours differ in brightness, so the stripes read without hue (colour-blind rule).
            Assert.That(Mathf.Abs(Luma(p.hazardYellow) - Luma(p.hazardMagenta)), Is.GreaterThan(0.3f), "stripe luminance contrast");
            // Bubblegum vs hit pink: apart in colour, and a striped hit-pink hazard on a Bubblegum section (the Sweep Pad)
            // still reads by its stripe: the stripe's light band stands off the Bubblegum floor in luma.
            var bubble = p.HueTop(SurfaceHue.Bubblegum);
            Assert.That(PaletteTests.DeltaE(bubble, p.hazardMagenta), Is.GreaterThan(15f), "Bubblegum vs hit pink");
            Assert.That(Mathf.Abs(Luma(p.hazardYellow) - Luma(bubble)), Is.GreaterThan(0.15f), "stripe vs Bubblegum floor");
            UnityEngine.Object.DestroyImmediate(p);
        }

        // ------------------------------------------------------------------ G4.8 soft look

        [Test]
        public void SoftPalette_TokensAsDocumented()
        {
            var p = ScriptableObject.CreateInstance<PaletteTokens>();
            var tops = new[] { "F9D1E2", "F9EDC2", "D6EDF9", "E0F9D1", "DED1F9", "F9E4D1" };
            var sides = new[] { "DB79A4", "DBC679", "79BADB", "9EDB79", "9879DB", "DBA879" };
            var rims = new[] { "FF6FAE", "FFD43B", "4CC2FF", "86E04E", "A77DFF", "FF9A3D" };
            for (var i = 0; i < 6; i++)
            {
                Assert.That(Hex(p.Top((SurfaceHue)i, LookId.Soft)), Is.EqualTo(tops[i]), ((SurfaceHue)i).ToString());
                Assert.That(Hex(p.Side((SurfaceHue)i, LookId.Soft)), Is.EqualTo(sides[i]), ((SurfaceHue)i).ToString());
                Assert.That(Hex(p.Rim((SurfaceHue)i, LookId.Soft)), Is.EqualTo(rims[i]), ((SurfaceHue)i).ToString());
                // The candy look still answers the G4.6 tokens.
                Assert.That(p.Top((SurfaceHue)i, LookId.Candy), Is.EqualTo(p.hueTops[i]));
                Assert.That(p.Side((SurfaceHue)i, LookId.Candy), Is.EqualTo(p.hueSides[i]));
            }

            Assert.That(Hex(p.Sun(LookId.Soft)), Is.EqualTo("FFF3E0"));
            Assert.That(Hex(p.SkyTop(LookId.Soft)), Is.EqualTo("66B5F2"));
            Assert.That(Hex(p.SkyHorizon(LookId.Soft)), Is.EqualTo("9CD2F5"));
            Assert.That(Hex(p.softLit), Is.EqualTo("FF8A00"));
            UnityEngine.Object.DestroyImmediate(p);
        }

        /// <summary>
        /// G4.8 soft look rules: tops pastel (S 0.12–0.28, V 0.93–0.98, never white), sides a mid tone (S 0.35–0.55) darker than
        /// their tops, rims saturated (S ≥ 0.5), the meaning colours ΔE ≥ 15 off every top, the hit pink ΔE ≥ 30 and Δluma ≥ 0.15
        /// off every floor, the sky under the saturation budget's 0.6.
        /// </summary>
        [Test]
        public void SoftPalette_ReadabilityRules()
        {
            var p = ScriptableObject.CreateInstance<PaletteTokens>();
            var report = new System.Text.StringBuilder();
            for (var i = 0; i < 6; i++)
            {
                var h = (SurfaceHue)i;
                var top = p.Top(h, LookId.Soft);
                var side = p.Side(h, LookId.Soft);
                var rim = p.Rim(h, LookId.Soft);
                PaletteTokens.Sv(top, out var s, out var v);
                Assert.That(s, Is.InRange(0.12f, 0.28f), h + " top saturation (pastel)");
                Assert.That(v, Is.InRange(0.93f, 0.98f), h + " top value");
                Assert.That(top, Is.Not.EqualTo(Color.white), h.ToString());
                PaletteTokens.Sv(side, out var ss, out _);
                Assert.That(ss, Is.InRange(0.35f, 0.55f), h + " side saturation (mid tone)");
                Assert.That(Luma(side), Is.LessThan(Luma(top) - 0.1f), h + " side darker than its top");
                Color.RGBToHSV(top, out var ht, out _, out _);
                Color.RGBToHSV(side, out var hs, out _, out _);
                var dh = Mathf.Abs(ht - hs);
                Assert.That(Mathf.Min(dh, 1f - dh), Is.LessThan(0.03f), h + " side keeps the hue");
                PaletteTokens.Sv(rim, out var rs, out _);
                Assert.That(rs, Is.GreaterThanOrEqualTo(0.5f), h + " rim saturated");
                foreach (var (name, m) in new[] { ("hit pink", p.hazardMagenta), ("mint", p.mint), ("goo", p.goo), ("sky top", p.SkyTop(LookId.Soft)), ("sky horizon", p.SkyHorizon(LookId.Soft)) })
                    Assert.That(PaletteTests.DeltaE(top, m), Is.GreaterThanOrEqualTo(15f), $"{h} top vs {name}");
                // A hazard against its floor (the pale floors make this easier: proven per hue).
                var de = PaletteTests.DeltaE(top, p.hazardMagenta);
                var dl = Mathf.Abs(Luma(top) - Luma(p.hazardMagenta));
                Assert.That(de, Is.GreaterThanOrEqualTo(30f), $"{h}: hit pink vs floor ΔE");
                Assert.That(dl, Is.GreaterThanOrEqualTo(0.15f), $"{h}: hit pink vs floor Δluma");
                report.Append($"{h}: top {Hex(top)} S {s:F2} V {v:F2}; side {Hex(side)}; rim {Hex(rim)}; hit pink ΔE {de:F1} Δluma {dl:F2}; " +
                              $"stripe yellow ΔE {PaletteTests.DeltaE(top, p.hazardYellow):F1} Δluma {Mathf.Abs(Luma(top) - Luma(p.hazardYellow)):F2}\n");
            }

            PaletteTokens.Sv(p.SkyTop(LookId.Soft), out var skyS, out _);
            Assert.That(skyS, Is.LessThan(0.6f), "soft sky top under the saturation budget");
            TestContext.WriteLine(report.ToString());
            UnityEngine.Object.DestroyImmediate(p);
        }

        /// <summary>G4.8: the grade per look: candy = G4.6 (Neutral + gain), soft = natural (no tonemapper, sat +6, contrast +8, exposure 0).</summary>
        [Test]
        public void Grade_PerLook_BakeRoundTrips()
        {
            Assert.That(GraphicsQuality.GradeOf(LookId.Candy).Neutral, Is.True);
            Assert.That(GraphicsQuality.GradeOf(LookId.Candy).Saturation, Is.EqualTo(22f));
            var soft = GraphicsQuality.GradeOf(LookId.Soft);
            Assert.That(soft.Neutral, Is.False);
            Assert.That(soft.Saturation, Is.EqualTo(6f));
            Assert.That(soft.Contrast, Is.EqualTo(8f));
            Assert.That(soft.PostExposure, Is.EqualTo(0f));
            Assert.That(soft.BloomThreshold, Is.GreaterThanOrEqualTo(1.2f), "bloom only on gloss highlights");
            var p = PaletteTokens.Get();
            foreach (var look in new[] { LookId.Soft, LookId.Candy })
                for (var i = 0; i < 6; i++)
                {
                    var c = p.Top((SurfaceHue)i, look);
                    var light = GraphicsQuality.TopLight(look);
                    var a = GraphicsQuality.AlbedoFor(c, look);
                    Assert.That(Mathf.Max(a.r, Mathf.Max(a.g, a.b)), Is.LessThan(GraphicsQuality.MaxAlbedo), $"{look} {(SurfaceHue)i}: bake within the albedo cap");
                    var back = GraphicsQuality.Grade(new Color(a.r * light.r, a.g * light.g, a.b * light.b), look);
                    Assert.That(Mathf.Abs(back.r - c.r) + Mathf.Abs(back.g - c.g) + Mathf.Abs(back.b - c.b), Is.LessThan(0.02f), $"{look} {(SurfaceHue)i}: lit top shows its token");
                }
        }

        [Test]
        public void Presets_TwelveValid_BotsDeterministic()
        {
            Assert.That(CosmeticPresets.Count, Is.InRange(8, 12));
            Assert.That(CosmeticPresets.All[0].Name, Is.EqualTo("Bolt"));
            for (var slot = 0; slot < Protocol.MaxPlayers; slot++)
            {
                Assert.That(CosmeticPresets.ForBot(slot), Is.EqualTo(CosmeticPresets.ForBot(slot)));
                Assert.That(CosmeticPresets.ForBot(slot), Is.LessThan(CosmeticPresets.Count));
            }

            Assert.That(CosmeticPresets.Clamp(200), Is.EqualTo(0), "unknown ids fall back to Bolt");
            Assert.That(CosmeticPresets.Clamp(-1), Is.EqualTo(0));
            Assert.That(CosmeticPresets.Clamp(7), Is.EqualTo(7));
        }

        [Test]
        public void Presets_RoundTripInMatchMessage()
        {
            var m = new MatchStateMessage { Phase = MatchPhase.LobbyOpen, RoundCount = 4 };
            var slots = new MatchSlotInfo[Protocol.MaxPlayers];
            for (var i = 0; i < slots.Length; i++)
                slots[i] = new MatchSlotInfo { Slot = (byte)i, Flags = MatchSlotInfo.Active, Places = new byte[4], Preset = (byte)((i * 5) % CosmeticPresets.Count), Name = "P" + i };
            m.Slots = slots;
            var buf = new byte[MatchStateMessage.MaxSize];
            var n = m.Encode(buf);
            Assert.That(MatchStateMessage.TryDecode(new ReadOnlySpan<byte>(buf, 0, n), out var d), Is.True);
            for (var i = 0; i < slots.Length; i++)
                Assert.That(d.Slots[i].Preset, Is.EqualTo(slots[i].Preset));
            Assert.That(MatchStateMessage.FormatVersion, Is.EqualTo(2));
            // An old (format v1) message is refused rather than misread.
            buf[0] = 1;
            Assert.That(MatchStateMessage.TryDecode(new ReadOnlySpan<byte>(buf, 0, n), out _), Is.False);
            TestContext.WriteLine($"match for 12 with presets: {n} B (max {MatchStateMessage.MaxSize} B)");
        }

        [Test]
        public void Hello_CarriesPresetId()
        {
            var bytes = Protocol.EncodeHello(new Protocol.Hello { ProtocolVersion = Protocol.Version, BuildVersion = "g2", CosmeticPreset = 9, Name = "Me", Token = new byte[16] });
            Assert.That(Protocol.TryDecodeHello(bytes, out var h, out var err), Is.True, err);
            Assert.That(h.CosmeticPreset, Is.EqualTo(9));
            TestContext.WriteLine($"hello with name + token + preset: {bytes.Length} B (unchanged layout)");
        }

        [Test]
        public void Director_SeatsHumanPreset_BotsFromSlot()
        {
            var h = new MatchDirectorTests.Harness(new byte[32], new[] { 0 });
            h.D.OnHumanJoined(3, new byte[16], "Ann", 9);
            Assert.That(h.D.Presets[3], Is.EqualTo(9));
            h.D.OnHumanJoined(4, new byte[16], "Bob", 250);
            Assert.That(h.D.Presets[4], Is.EqualTo(0), "out-of-range preset clamps to Bolt");
        }

        [Test]
        public void Rushling_Within2500Triangles_EveryPreset()
        {
            for (var p = 0; p < CosmeticPresets.Count; p++)
            {
                var tris = RushlingParts.Triangles(p);
                TestContext.WriteLine($"{CosmeticPresets.All[p].Name}: {tris} triangles (body + lid + limbs)");
                Assert.That(tris, Is.LessThanOrEqualTo(2500), CosmeticPresets.All[p].Name);
            }
        }
    }
}
