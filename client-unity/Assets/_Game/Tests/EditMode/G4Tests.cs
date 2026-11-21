using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using FallRush.Shared;
using NUnit.Framework;
using UnityEngine;

namespace FallRush.Tests
{
    /// <summary>G4.0: display names separate from the on-chain map keys.</summary>
    public class G4Tests
    {
        private static string Root => Path.GetFullPath(Path.Combine(Application.dataPath, "..", ".."));

        [Test]
        public void MapPool_KeysIdsUnchanged_HashEqualsProgram()
        {
            var json = File.ReadAllText(Path.Combine(Root, "packages", "shared", "config", "map_pool_v1.json"));
            var pool = (Dictionary<string, object>)new MovementConfigTests.MiniJson(json).Parse();
            var maps = (List<object>)pool["maps"];
            Assert.That(maps.Count, Is.EqualTo(4));
            var keys = new[] { "BlockPartyDash", "GooTower", "StarScramble", "CrumbleShelf" };
            var modes = new[] { "Race", "Survival", "Skill", "Final" };
            for (var i = 0; i < maps.Count; i++)
            {
                var m = (Dictionary<string, object>)maps[i];
                Assert.That((long)m["id"], Is.EqualTo(i));
                Assert.That((string)m["key"], Is.EqualTo(keys[i]));
                Assert.That((string)m["mode"], Is.EqualTo(modes[i]));
                Assert.That((long)m["variants"], Is.EqualTo(2));
                Assert.That(MapPoolV1.Keys[i], Is.EqualTo(keys[i]), "C# mirror key");
            }

            // sha256(canonical map_pool_v1.json) == MAP_POOL_V1_HASH compiled into programs/fall_rush/src/state.rs.
            var canon = MovementConfigTests.Canonicalize(new MovementConfigTests.MiniJson(json).Parse());
            var disk = Sha256.Hex(Sha256.Hash(Encoding.UTF8.GetBytes(canon)));
            var rs = File.ReadAllText(Path.Combine(Root, "programs", "fall_rush", "src", "state.rs"));
            var block = Regex.Match(rs, @"MAP_POOL_V1_HASH: \[u8; 32\] = \[(?<b>[^\]]*)\]").Groups["b"].Value;
            var sb = new StringBuilder(64);
            foreach (Match b in Regex.Matches(block, @"0x([0-9a-fA-F]{2})"))
                sb.Append(b.Groups[1].Value.ToLowerInvariant());
            Assert.That(sb.Length, Is.EqualTo(64), "parsed 32 bytes from state.rs");
            Assert.That(disk, Is.EqualTo(sb.ToString()), "map pool hash on disk = the program's MAP_POOL_V1_HASH");
        }

        [Test]
        public void DisplayNames_EveryKeyHasOne_AndTheUiUsesThem()
        {
            Assert.That(MapDisplayNames.Names.Length, Is.EqualTo(MapPoolV1.Keys.Length));
            var expected = new[] { "Capsule Run", "Goo Tower", "Star Fountain", "Lid Flip" };
            for (byte i = 0; i < MapPoolV1.Keys.Length; i++)
            {
                Assert.That(MapDisplayNames.Of(i), Is.EqualTo(expected[i]));
                Assert.That(MapDisplayNames.OfKey(MapPoolV1.Keys[i]), Is.EqualTo(expected[i]));
                Assert.That(string.IsNullOrWhiteSpace(MapDisplayNames.Of(i)), Is.False);
                Assert.That(FallRush.Client.UiNames.Map(i), Is.EqualTo(expected[i]), "UI titles go through the display names");
            }

            Assert.That(MapDisplayNames.Of(MapPoolV1.Sandbox), Is.EqualTo("Sandbox"));
            Assert.That(MapDisplayNames.Of(MapPoolV1.Lobby), Is.EqualTo("Lobby"));
            // Keys (and so scene names, logs, the map pool) are unchanged.
            Assert.That(MapPoolV1.KeyOf(0), Is.EqualTo("BlockPartyDash"));
            Assert.That(MapPoolV1.KeyOf(3), Is.EqualTo("CrumbleShelf"));
        }
    }
}
