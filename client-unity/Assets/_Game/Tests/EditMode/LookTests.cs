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
            Assert.That(Hex(p.skyTop), Is.EqualTo("3FA9F5"));
            Assert.That(Hex(p.skyHorizon), Is.EqualTo("BFE6FF"));
            Assert.That(Hex(p.magenta), Is.EqualTo("FF3EA5"));
            Assert.That(Hex(p.sunYellow), Is.EqualTo("FFD21F"));
            Assert.That(Hex(p.grape), Is.EqualTo("7B2FF7"));
            Assert.That(Hex(p.cloudWhite), Is.EqualTo("FFFFFF"));
            Assert.That(Hex(p.mint), Is.EqualTo("5BE3B5"));
            Assert.That(Hex(p.hazardYellow), Is.EqualTo("FFD21F"));
            Assert.That(Hex(p.hazardMagenta), Is.EqualTo("FF3EA5"));
            Assert.That(Hex(p.goo), Is.EqualTo("9B5CFF"));
            UnityEngine.Object.DestroyImmediate(p);
        }

        [Test]
        public void Palette_ReadabilityRules()
        {
            var p = ScriptableObject.CreateInstance<PaletteTokens>();
            // Walkable tops: light and desaturated.
            PaletteTokens.Sv(p.walkTop, out var s, out var v);
            Assert.That(s, Is.LessThan(0.12f), "walkable tops desaturated");
            Assert.That(v, Is.GreaterThan(0.9f), "walkable tops light");
            // Hazards: high saturation (and always striped: the stripe material / merged stripe segments).
            foreach (var h in new[] { p.hazardYellow, p.hazardMagenta })
            {
                PaletteTokens.Sv(h, out s, out v);
                Assert.That(s, Is.GreaterThan(0.7f), "hazards saturated");
            }

            // The two hazard stripe colours differ in brightness, so the stripes read without hue (colour-blind rule).
            var ly = p.hazardYellow.r * 0.2126f + p.hazardYellow.g * 0.7152f + p.hazardYellow.b * 0.0722f;
            var lm = p.hazardMagenta.r * 0.2126f + p.hazardMagenta.g * 0.7152f + p.hazardMagenta.b * 0.0722f;
            Assert.That(Mathf.Abs(ly - lm), Is.GreaterThan(0.3f), "stripe luminance contrast");
            UnityEngine.Object.DestroyImmediate(p);
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
