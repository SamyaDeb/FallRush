using FallRush.OnChain;
using NUnit.Framework;
using UnityEngine;

namespace FallRush.Tests
{
    /// <summary>P6: the bundle's sealed-bet / season / receipt JSON (packages/chain/src/rollup.ts) parses into the C#
    /// shapes the bet panel and the receipt card read (JsonUtility; nulls become default objects).</summary>
    public sealed class P6SealedSeasonJsonTests
    {
        [Test]
        public void SealedStateParses()
        {
            const string j = "{\"ok\":true,\"match\":\"M\",\"open\":true,\"escrow\":{\"n\":3,\"pool\":\"27000000\",\"state\":1,\"nRevealed\":0,\"nForfeit\":0,\"forfeit\":\"0\",\"closedAt\":0},"
                + "\"mine\":[{\"i\":1,\"wallet\":\"W\",\"amount\":\"10000000\",\"hash\":\"ab\",\"outcome\":255,\"state\":0,\"saved\":{\"match\":\"M\",\"outcome\":4,\"amount\":\"10000000\",\"salt\":\"00\",\"hash\":\"ab\",\"at\":1}}],\"tee\":\"https://devnet-tee.magicblock.app\"}";
            var s = JsonUtility.FromJson<ESealed>(j);
            Assert.IsTrue(s.ok && s.open);
            Assert.AreEqual(3, s.escrow.n);
            Assert.AreEqual("27000000", s.escrow.pool);
            Assert.AreEqual(1, s.mine.Length);
            Assert.AreEqual(4, s.mine[0].saved.outcome);
            Assert.AreEqual("10000000", s.mine[0].amount);
            var none = JsonUtility.FromJson<ESealed>("{\"ok\":true,\"open\":false,\"escrow\":null,\"mine\":[]}");
            Assert.IsFalse(none.open);
            Assert.AreEqual(0, none.mine.Length);
        }

        [Test]
        public void SeasonAndReceiptParse()
        {
            var s = JsonUtility.FromJson<ESeason>("{\"ok\":true,\"id\":202648,\"player\":{\"wallet\":\"W\",\"crowns\":2,\"finals\":3,\"matches\":4,\"achievements\":3,\"net\":\"-5000000\",\"betsWon\":1,\"betsLost\":2,\"drops\":1}}");
            Assert.AreEqual(202648, s.id);
            Assert.AreEqual(2, s.player.crowns);
            Assert.AreEqual(3, s.player.achievements);
            var r = JsonUtility.FromJson<EReceipt>("{\"ok\":true,\"onSolana\":true,\"match\":\"M\",\"champion\":4,\"receipt\":{\"flags\":9,\"humanMask\":16,\"sealedN\":5,\"sealedRevealed\":4,\"sealedForfeits\":1,\"wallets\":[\"a\"],\"markets\":[]}}");
            Assert.AreEqual(5, r.receipt.sealedN);
            Assert.AreEqual(4, r.receipt.sealedRevealed);
            Assert.AreEqual(1, r.receipt.sealedForfeits);
        }
    }
}
