using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using FallRush.Shared;
using NUnit.Framework;
using UnityEngine;

namespace FallRush.Tests
{
    /// <summary>
    /// M4 §3.1: the C# ports of <c>deal.ts</c>, <c>targets.ts</c> and <c>transcript.ts</c> against the TS-generated vectors
    /// (<c>packages/shared/test-vectors/deal_vectors.json</c>, <c>transcript_vectors.json</c>), byte for byte.
    /// </summary>
    public class ParityTests
    {
        private static string VectorDir => Path.GetFullPath(Path.Combine(Application.dataPath, "..", "..", "packages", "shared", "test-vectors"));

        internal static Dictionary<string, object> LoadJson(string file) =>
            (Dictionary<string, object>)new MovementConfigTests.MiniJson(File.ReadAllText(Path.Combine(VectorDir, file))).Parse();

        internal static byte[] Hex(string s)
        {
            var b = new byte[s.Length / 2];
            for (var i = 0; i < b.Length; i++)
                b[i] = Convert.ToByte(s.Substring(i * 2, 2), 16);
            return b;
        }

        private static byte[] Bytes(List<object> l)
        {
            var b = new byte[l.Count];
            for (var i = 0; i < b.Length; i++)
                b[i] = (byte)(long)l[i];
            return b;
        }

        [Test]
        public void Deal_AllHundredSeeds_EqualTheTsVectors()
        {
            var v = LoadJson("deal_vectors.json");
            Assert.That((string)v["mapPoolHash"], Is.EqualTo(MapPoolHashFromDisk()), "vectors were generated from the current map pool");
            var deals = (List<object>)v["deals"];
            Assert.That(deals.Count, Is.EqualTo(100));
            foreach (Dictionary<string, object> d in deals)
            {
                var r = Hex((string)d["randomness"]);
                var got = Deal.Derive(r);
                Assert.That(got.Maps, Is.EqualTo(Bytes((List<object>)d["maps"])), (string)d["randomness"]);
                Assert.That(got.Variants, Is.EqualTo(Bytes((List<object>)d["variants"])), (string)d["randomness"]);
                Assert.That(got.Modifiers, Is.EqualTo(Bytes((List<object>)d["modifiers"])), (string)d["randomness"]);
            }
        }

        [Test]
        public void Rnd_RejectionSamplingEdgeVectors_EqualTheTsVectors()
        {
            var v = LoadJson("deal_vectors.json");
            var rnd = (List<object>)v["rnd"];
            Assert.That(rnd.Count, Is.EqualTo(40));
            foreach (Dictionary<string, object> e in rnd)
            {
                var got = Deal.RndRange(Hex((string)e["randomness"]), Encoding.ASCII.GetBytes((string)e["label"]), (ulong)(long)e["n"]);
                Assert.That((long)got, Is.EqualTo((long)e["out"]), $"{e["label"]} n={e["n"]}");
            }
        }

        private static string MapPoolHashFromDisk()
        {
            var dir = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "..", "packages", "shared", "config"));
            var canon = MovementConfigTests.Canonicalize(new MovementConfigTests.MiniJson(File.ReadAllText(Path.Combine(dir, "map_pool_v1.json"))).Parse());
            return Sha256.Hex(Sha256.Hash(Encoding.UTF8.GetBytes(canon)));
        }

        [Test]
        public void MapPoolMirror_RoundsTable_EqualsTheJson()
        {
            var dir = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "..", "packages", "shared", "config"));
            var pool = (Dictionary<string, object>)new MovementConfigTests.MiniJson(File.ReadAllText(Path.Combine(dir, "map_pool_v1.json"))).Parse();
            var rounds = (List<object>)pool["rounds"];
            Assert.That(rounds.Count, Is.EqualTo(MapPoolV1.RoundMaps.Length));
            for (var i = 0; i < rounds.Count; i++)
            {
                var r = (Dictionary<string, object>)rounds[i];
                Assert.That((long)r["round"], Is.EqualTo(i + 1));
                var maps = (List<object>)r["maps"];
                Assert.That(maps.Count, Is.EqualTo(MapPoolV1.RoundMaps[i].Length));
                for (var k = 0; k < maps.Count; k++)
                    Assert.That((long)maps[k], Is.EqualTo(MapPoolV1.RoundMaps[i][k]));
            }
        }

        [Test]
        public void RoundTargets_P4To20_EqualTheTsVectors()
        {
            var v = LoadJson("transcript_vectors.json");
            var rows = (List<object>)v["targets"];
            Assert.That(rows.Count, Is.EqualTo(17));
            foreach (Dictionary<string, object> row in rows)
            {
                var p = (int)(long)row["p"];
                Assert.That(RoundTargets.For(p), Is.EqualTo(Bytes((List<object>)row["targets"])), $"P={p}");
            }

            Assert.That(RoundTargets.For(12), Is.EqualTo(new byte[] { 8, 5, 3, 1 }), "GDD §4.1: 12 → 8 → 5 → 3 → 1");
            Assert.Throws<ArgumentOutOfRangeException>(() => RoundTargets.For(3));
            Assert.Throws<ArgumentOutOfRangeException>(() => RoundTargets.For(21));
        }

        internal static List<RosterHash.Entry> Roster(List<object> entries)
        {
            var r = new List<RosterHash.Entry>();
            foreach (Dictionary<string, object> e in entries)
                r.Add(new RosterHash.Entry
                {
                    Slot = (byte)(long)e["slot"], Bot = (bool)e["bot"], UserId = (string)e["userId"],
                    Wallet = e.TryGetValue("wallet", out var w) ? Hex((string)w) : null,
                });
            return r;
        }

        [Test]
        public void RosterHash_EqualsTheTsVectors()
        {
            var v = LoadJson("transcript_vectors.json");
            foreach (Dictionary<string, object> r in (List<object>)v["rosters"])
                Assert.That(Sha256.Hex(RosterHash.Compute(Roster((List<object>)r["entries"]))), Is.EqualTo((string)r["hash"]), (string)r["name"]);
            // The placeholder ids are what the director uses.
            Assert.That(RosterHash.PlaceholderUserId(3, true), Is.EqualTo("bot:3"));
            Assert.That(RosterHash.PlaceholderUserId(0, false), Is.EqualTo("guest:practice:0"));
        }

        [Test]
        public void Transcript_EveryStep_EqualsTheTsVectors()
        {
            var v = LoadJson("transcript_vectors.json");
            var matches = (List<object>)v["matches"];
            Assert.That(matches.Count, Is.EqualTo(3));
            foreach (Dictionary<string, object> m in matches)
            {
                var h = Transcript.Init(Hex((string)m["matchId"]), Hex((string)m["rosterHash"]));
                Assert.That(Sha256.Hex(h), Is.EqualTo((string)m["init"]), $"{m["name"]} init");
                var seq = 0u;
                foreach (Dictionary<string, object> e in (List<object>)m["events"])
                {
                    Assert.That((long)e["seq"], Is.EqualTo(seq));
                    h = Transcript.Next(h, (byte)(long)e["tag"], seq, (byte)(long)e["round"], (byte)(long)e["slot"], Hex((string)e["arg"]));
                    Assert.That(Sha256.Hex(h), Is.EqualTo((string)e["after"]), $"{m["name"]} seq {seq} {Transcript.TagName((byte)(long)e["tag"])}");
                    seq++;
                }

                Assert.That(Sha256.Hex(h), Is.EqualTo((string)m["final"]));
            }
        }
    }
}
