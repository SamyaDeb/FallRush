using System;
using System.Collections.Generic;
using System.Text;
using FallRush.Net;
using FallRush.Shared;
using NUnit.Framework;

namespace FallRush.Tests.EditMode
{
    /// <summary>P5 task 4: the capsule-shell hello extension and the server's Core asset check.</summary>
    public sealed class ShellCheckTests
    {
        private static byte[] Asset(byte[] owner, byte[] auth, string shell)
        {
            var b = new List<byte> { 1 };
            b.AddRange(owner);
            b.Add(1);
            b.AddRange(auth);
            void Str(string s) { var x = Encoding.UTF8.GetBytes(s); b.AddRange(BitConverter.GetBytes(x.Length)); b.AddRange(x); }
            Str("Rushling Capsule (Rare)");
            Str("data:application/json,{}");
            b.Add(0);
            b.AddRange(new byte[20]);
            Str("rarity"); Str("Rare");
            if (shell != null) { Str("shell"); Str(shell); }
            Str("season"); Str("0");
            return b.ToArray();
        }

        [Test]
        public void HelloRoundTripsTheShellExtension()
        {
            var h = new Protocol.Hello { ProtocolVersion = Protocol.Version, BuildVersion = "1.0", CosmeticPreset = 3, Name = "Ann", Token = new byte[16], ShellAsset = "8d6SFeBgcitcisjU3NMvgV5GaVFxjsH9Gk8HCPvKzbfq", ShellWallet = "58YqMtZDoDAZ3zToumjRJa8zJuyU5bhpiFJqkTeZwAVk" };
            Assert.IsTrue(Protocol.TryDecodeHello(Protocol.EncodeHello(h), out var d, out var err), err);
            Assert.AreEqual(h.ShellAsset, d.ShellAsset);
            Assert.AreEqual(h.ShellWallet, d.ShellWallet);
            Assert.AreEqual("Ann", d.Name);
            var plain = Protocol.EncodeHello(new Protocol.Hello { ProtocolVersion = Protocol.Version, BuildVersion = "1.0", Name = "Ann", Token = new byte[16] });
            Assert.IsTrue(Protocol.TryDecodeHello(plain, out var p, out _));
            Assert.IsNull(p.ShellAsset);
            var junk = new byte[plain.Length + 1];
            Array.Copy(plain, junk, plain.Length);
            junk[plain.Length] = 7;
            Assert.IsFalse(Protocol.TryDecodeHello(junk, out _, out _), "trailing garbage is still refused");
        }

        [Test]
        public void ChecksOwnerAuthorityAndShell()
        {
            var owner = ShellCheck.Base58("58YqMtZDoDAZ3zToumjRJa8zJuyU5bhpiFJqkTeZwAVk");
            var auth = ShellCheck.Base58(ShellCheck.AssetsAuthority);
            Assert.AreEqual(32, owner.Length);
            Assert.AreEqual(32, auth.Length);
            Assert.AreEqual(9, ShellCheck.Check(Asset(owner, auth, "Lava"), owner).preset);
            var other = (byte[])owner.Clone();
            other[0] ^= 1;
            Assert.AreEqual(-1, ShellCheck.Check(Asset(owner, auth, "Lava"), other).preset, "not the owner");
            Assert.AreEqual(-1, ShellCheck.Check(Asset(owner, other, "Lava"), owner).preset, "not minted by Fall Rush");
            Assert.AreEqual(-1, ShellCheck.Check(Asset(owner, auth, null), owner).preset, "a Crown has no shell");
            Assert.AreEqual(-1, ShellCheck.Check(Asset(owner, auth, "Gold"), owner).preset, "unknown shell");
        }
    }
}
