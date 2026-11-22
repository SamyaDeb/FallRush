using System.Diagnostics;
using System.IO;
using System.Threading;
using FallRush.Net;
using FallRush.OnChain;
using FallRush.Shared;
using NUnit.Framework;
using UnityEngine;

namespace FallRush.Tests
{
    /// <summary>P3-F: the Classic → on-chain switch: the server handoff (live against the real chain emulator, and the
    /// failure path), the Final's rules while on chain, and the client's fonc parsing.</summary>
    public class P3SwitchTests
    {
        private static (int, string, bool)[] Roster => new[] { (2, "Ann", false), (5, "Bot 5", true), (9, "Bot 9", true) };

        [Test]
        public void HandoffFailsFastWithoutAnArena()
        {
            System.Environment.SetEnvironmentVariable("GAMESERVER_ONCHAIN_FINAL_URL", "http://127.0.0.1:9");
            try
            {
                var h = new ChainFinalHandoff();
                h.Begin("ab", 7, Roster);
                var sw = Stopwatch.StartNew();
                while (h.Phase == ChainFinalHandoff.State.Deciding && sw.Elapsed.TotalSeconds < 12)
                    Thread.Sleep(50);
                Assert.AreEqual(ChainFinalHandoff.State.Failed, h.Phase, h.Error);
                Assert.IsNull(h.Message);
            }
            finally
            {
                System.Environment.SetEnvironmentVariable("GAMESERVER_ONCHAIN_FINAL_URL", null);
            }
        }

        /// <summary>Node 24 (nvm, Homebrew or PATH): the Editor's PATH may not include nvm.</summary>
        public static string Node()
        {
            var nvm = Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile), ".nvm", "versions", "node");
            if (Directory.Exists(nvm))
            {
                var d = Directory.GetDirectories(nvm);
                System.Array.Sort(d);
                for (var i = d.Length - 1; i >= 0; i--)
                    if (File.Exists(Path.Combine(d[i], "bin", "node")))
                        return Path.Combine(d[i], "bin", "node");
            }

            return File.Exists("/opt/homebrew/bin/node") ? "/opt/homebrew/bin/node" : "node";
        }

        [Test]
        public void HandoffGoesLiveOnTheEmulatorAndSendsTheSlotMap()
        {
            var root = Path.GetFullPath(Path.Combine(Application.dataPath, "..", ".."));
            var main = Path.Combine(root, "tools", "chain-emulator", "src", "main.mjs");
            if (!File.Exists(Path.Combine(root, "tools", "chain-emulator", "bin", "rush_emu_core.wasm")))
                Assert.Ignore("emulator not built (scripts/p3-build-core.sh)");
            var node = Node();
            var port = 18787 + (System.Environment.TickCount & 255);
            using var p = Process.Start(new ProcessStartInfo(node, $"\"{main}\" --port {port} --no-shaping --autostart 1")
                { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = root });
            System.Environment.SetEnvironmentVariable("GAMESERVER_ONCHAIN_FINAL_URL", $"http://127.0.0.1:{port}");
            try
            {
                Thread.Sleep(800);
                var h = new ChainFinalHandoff();
                h.Begin("00112233", 0x5eed, Roster);
                var sw = Stopwatch.StartNew();
                while (h.Phase == ChainFinalHandoff.State.Deciding && sw.Elapsed.TotalSeconds < 12)
                    Thread.Sleep(50);
                Assert.AreEqual(ChainFinalHandoff.State.Live, h.Phase, h.Error);
                StringAssert.Contains("\"t\":\"final_onchain\"", h.Message);
                StringAssert.Contains($"ws://127.0.0.1:{port}/", h.Message);
                var map = OnChainSwitch.ParseSlotMap(h.Message);
                Assert.AreEqual(3, map.Length);
                Assert.AreEqual(2, OnChainSwitch.PickSlot(map, 2), "the human plays its slot");
                Assert.AreEqual(-1, OnChainSwitch.PickSlot(map, 4), "an eliminated player spectates");
                Assert.AreEqual(-1, OnChainSwitch.PickSlot(map, 5), "a bot slot is never ours");
            }
            finally
            {
                System.Environment.SetEnvironmentVariable("GAMESERVER_ONCHAIN_FINAL_URL", null);
                try { p.Kill(); } catch { /* gone */ }
            }
        }

        [Test]
        public void ChainFinalRulesTakeTheArenaResult()
        {
            var h = new ChainFinalHandoff();
            h.ForceForTest(ChainFinalHandoff.State.Live);
            var inner = new FinalRules(3600);
            var r = new ChainFinalRules(inner, h);
            Assert.IsFalse(r.IsOver);
            Assert.IsNull(r.RoundOutcome);
            Assert.IsFalse(r.AcceptsInput(null, 2));
            Assert.IsFalse(r.Simulates(null, 2));
            h.ForceForTest(ChainFinalHandoff.State.Done, new[] { 9, 2, 5 }, new[] { ElimReason.NotQualified, ElimReason.FellOut, ElimReason.FellOut });
            Assert.IsTrue(r.IsOver);
            Assert.AreEqual(RoundPhase.Ended, r.Phase);
            var o = r.RoundOutcome;
            CollectionAssert.AreEqual(new[] { 9, 2, 5 }, o.Order);
            Assert.IsTrue(o.Qualified[0]);
            Assert.IsFalse(o.Qualified[1]);
            Assert.AreEqual(1, o.QualifiedCount);
        }
    }
}
