using System.Collections.Generic;
using FallRush.Shared;
using NUnit.Framework;

namespace FallRush.Tests
{
    /// <summary>G4.1 hazard vectors v3: v1 / v2 unchanged, the G4 cases match the committed file and exercise both outcomes.</summary>
    public class HazardVectorsV3Tests
    {
        public const string V1Digest = "fc06826c90569570fe8cda386312687e40dc7543edf322d85415feb6a046e1b8";
        public const string V2Digest = "d4b2a73adcf7be778b181c3a302220bdc3416d440d7ad403f0fc1cc2ba35379c";

        [Test]
        public void HazardVectorsV3_MatchTheCommittedVectors_AndKeepV1V2()
        {
            var all = HazardVectorsV3.ComputeAll();
            var path = System.IO.Path.Combine(UnityEngine.Application.dataPath, "_Game", "Tests", "Vectors", "hazard_vectors_v3.json");
            var json = (Dictionary<string, object>)new MovementConfigTests.MiniJson(System.IO.File.ReadAllText(path)).Parse();
            var cases = (List<object>)json["cases"];
            Assert.That(cases.Count, Is.EqualTo(HazardVectorsV3.Count));
            const int w = HazardVectorsV3.Width;
            int poses = 0, alive = 0, contacts = 0, hits = 0, tele = 0, teleOn = 0, lidCases = 0, flipped = 0, retired = 0, fair = 0;
            for (var i = 0; i < cases.Count; i++)
            {
                var row = (List<object>)cases[i];
                for (var f = 0; f < w; f++)
                    Assert.That(all[i * w + f], Is.EqualTo((int)(long)row[f]), $"case {i} field {f}");
                var type = all[i * w];
                switch (type)
                {
                    case HazardVectorsV3.BallPose: poses++; alive += all[i * w + 6]; break;
                    case HazardVectorsV3.BallContact: contacts++; hits += all[i * w + 8]; break;
                    case HazardVectorsV3.BallTelegraph:
                        tele++;
                        for (var q = 6; q < 12; q++)
                            teleOn += all[i * w + q] > 0 ? 1 : 0;
                        break;
                    case HazardVectorsV3.LidStates:
                        lidCases++;
                        flipped += all[i * w + 7] > 0 ? 1 : 0;
                        retired += all[i * w + 8] >= 1000 ? 1 : 0;
                        break;
                    case HazardVectorsV3.LidFair:
                        fair++;
                        Assert.That(all[i * w + 8], Is.GreaterThanOrEqualTo(5), $"case {i}: fairness floor (≥ 5 of the inner 3 × 3 solid)");
                        break;
                }
            }

            // v2 (and so v1) prefix unchanged.
            var v2 = HazardVectorsV2.ComputeAll();
            for (var i = 0; i < v2.Length; i++)
                Assert.That(all[i], Is.EqualTo(v2[i]));
            Assert.That(HazardVectors.Digest(HazardVectors.ComputeAll()), Is.EqualTo(V1Digest), "v1 digest unchanged");
            Assert.That(HazardVectorsV2.Digest(v2), Is.EqualTo(V2Digest), "v2 digest unchanged");
            Assert.That(HazardVectorsExpected.Digest, Is.EqualTo(V1Digest));
            Assert.That(HazardVectorsV2Expected.Digest, Is.EqualTo(V2Digest));
            Assert.That((string)json["v1Digest"], Is.EqualTo(V1Digest));
            Assert.That((string)json["v2Digest"], Is.EqualTo(V2Digest));
            Assert.That(HazardVectorsV3.Digest(all), Is.EqualTo((string)json["digest"]));
            Assert.That(HazardVectorsV3.Digest(all), Is.EqualTo(HazardVectorsV3Expected.Digest), "generated C# table is current");
            Assert.That(HazardVectorsV3.SelfTest(HazardVectorsV3Expected.Data, HazardVectorsV3Expected.Digest, "editmode", out var line), Is.True, line);
            TestContext.WriteLine($"{line}\nball poses {poses} (alive {alive}), contacts {contacts} (hits {hits}), telegraph {tele} ({teleOn} lit samples), lid states {lidCases} (with flips {flipped}, with retired {retired}), fairness {fair}");
            Assert.That(alive, Is.InRange(poses / 5, poses * 19 / 20), "pose cases see live and despawned balls");
            Assert.That(hits, Is.InRange(contacts / 10, contacts * 9 / 10), "contact cases exercise hit and miss");
            Assert.That(teleOn, Is.GreaterThan(0));
            Assert.That(flipped, Is.GreaterThan(lidCases / 4));
            Assert.That(retired, Is.GreaterThan(0));
        }
    }
}
