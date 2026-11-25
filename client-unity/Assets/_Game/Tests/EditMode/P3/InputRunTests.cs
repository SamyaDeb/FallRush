using FallRush.OnChain;
using NUnit.Framework;

namespace FallRush.Tests
{
    /// <summary>
    /// Input run (2026-11-25): on the rollup the client stamps further ahead (T+5) inside a 13-tick window
    /// (set_input_lead on the arenas), so a chain micro-stall no longer gets our batches refused as out of window.
    /// The emulator and explicit ?lead= / ?window= keep their values.
    /// </summary>
    public class InputRunTests
    {
        [Test]
        public void RollupGetsWindow13AndLead5()
        {
            var c = new ChainConfig { target = "devnet" };
            c.ApplyRollupDefaults();
            Assert.AreEqual(13, c.inputWindow);
            Assert.AreEqual(5, c.inputLead);
        }

        [Test]
        public void EmulatorKeepsTheOldValues()
        {
            var c = new ChainConfig { target = "emu" };
            c.ApplyRollupDefaults();
            Assert.AreEqual(8, c.inputWindow);
            Assert.AreEqual(3, c.inputLead);
        }

        [Test]
        public void ExplicitWindowAndLeadWin()
        {
            var c = new ChainConfig { target = "devnet", inputWindow = 8, windowSet = true };
            c.ApplyRollupDefaults();
            Assert.AreEqual(8, c.inputWindow, "?window=8 = the old behaviour");
            Assert.AreEqual(5, c.inputLead, "lead ≤ window − 3");

            var d = new ChainConfig { target = "devnet", inputLead = 2, leadSet = true };
            d.ApplyRollupDefaults();
            Assert.AreEqual(13, d.inputWindow);
            Assert.AreEqual(2, d.inputLead);
        }

        [Test]
        public void TheExtraLeadStaysInsideTheWindow()
        {
            var c = new ChainConfig { target = "devnet" };
            c.ApplyRollupDefaults();
            // OnChainSession.MaxExtraLead = window − lead − 2: the batch's first stamp T+lead+extra ≤ T+window−2
            var maxExtra = c.inputWindow - c.inputLead - 2;
            Assert.AreEqual(6, maxExtra);
            Assert.LessOrEqual(c.inputLead + maxExtra + c.batchTicks - 1, c.inputWindow, "the last tick of a batch fits too");
        }
    }
}
