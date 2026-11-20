using System.Collections.Generic;
using FallRush.Shared;
using NUnit.Framework;

namespace FallRush.Tests
{
    public class NetTargetsTests
    {
        // ---------------- URL parsing ----------------

        [TestCase("ws://127.0.0.1:7777", "127.0.0.1", 7777, false, "/")]
        [TestCase("ws://localhost:7777", "localhost", 7777, false, "/")]
        [TestCase("wss://localhost:8443", "localhost", 8443, true, "/")]
        [TestCase("wss://gs1.1-2-3-4.sslip.io", "gs1.1-2-3-4.sslip.io", 443, true, "/")]
        [TestCase("ws://example.com", "example.com", 80, false, "/")]
        [TestCase("WSS://Host:9000/game", "Host", 9000, true, "/game")]
        [TestCase("  ws://h:1/  ", "h", 1, false, "/")]
        public void ParsesValidUrls(string url, string host, int port, bool secure, string path)
        {
            Assert.That(NetTargets.TryParseServerUrl(url, out var t, out var err), Is.True, err);
            Assert.That(t.Host, Is.EqualTo(host));
            Assert.That(t.Port, Is.EqualTo(port));
            Assert.That(t.Secure, Is.EqualTo(secure));
            Assert.That(t.Path, Is.EqualTo(path));
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase("http://localhost:7777")]
        [TestCase("localhost:7777")]
        [TestCase("ws://")]
        [TestCase("ws://:7777")]
        [TestCase("ws://host:0")]
        [TestCase("ws://host:70000")]
        [TestCase("ws://host:abc")]
        [TestCase("ws://host:7777/?x=1")]
        [TestCase("ws://host:7777#frag")]
        [TestCase("ws://user@host:7777")]
        [TestCase("ws://[::1]:7777")]
        public void RejectsInvalidUrls(string url)
        {
            Assert.That(NetTargets.TryParseServerUrl(url, out _, out var err), Is.False);
            Assert.That(err, Is.Not.Null.And.Not.Empty);
        }

        [Test]
        public void TargetUrl_OmitsDefaultPorts()
        {
            NetTargets.TryParseServerUrl("wss://a.b", out var s, out _);
            NetTargets.TryParseServerUrl("ws://a.b:7777", out var p, out _);
            Assert.That(s.Url, Is.EqualTo("wss://a.b/"));
            Assert.That(p.Url, Is.EqualTo("ws://a.b:7777/"));
        }

        // ---------------- query string ----------------

        [Test]
        public void QueryParam_FoundAndDecoded()
        {
            Assert.That(NetTargets.GetQueryParam("http://localhost:8080/?server=ws://localhost:7777", "server"), Is.EqualTo("ws://localhost:7777"));
            Assert.That(NetTargets.GetQueryParam("https://x/index.html?a=1&server=wss%3A%2F%2Fh%3A8443&b=2", "server"), Is.EqualTo("wss://h:8443"));
            Assert.That(NetTargets.GetQueryParam("https://x/?server=ws://h:1#top", "server"), Is.EqualTo("ws://h:1"));
            Assert.That(NetTargets.GetQueryParam("https://x/?proto=999", "proto"), Is.EqualTo("999"));
        }

        [Test]
        public void QueryParam_Missing()
        {
            Assert.That(NetTargets.GetQueryParam("", "server"), Is.Null);
            Assert.That(NetTargets.GetQueryParam(null, "server"), Is.Null);
            Assert.That(NetTargets.GetQueryParam("http://x/", "server"), Is.Null);
            Assert.That(NetTargets.GetQueryParam("http://x/?servers=ws://h:1", "server"), Is.Null);
        }

        // ---------------- resolution order: query → config → default ----------------

        [Test]
        public void Resolve_QueryWins()
        {
            var t = NetTargets.ResolveServerTarget("http://localhost:8080/?server=wss://localhost:8443", "ws://config:1", out var src, out var warn);
            Assert.That(src, Is.EqualTo(TargetSource.QueryString));
            Assert.That(t.Url, Is.EqualTo("wss://localhost:8443/"));
            Assert.That(warn, Is.Null);
        }

        [Test]
        public void Resolve_ConfigWhenNoQuery()
        {
            var t = NetTargets.ResolveServerTarget("http://localhost:8080/", "ws://config:1234", out var src, out _);
            Assert.That(src, Is.EqualTo(TargetSource.Config));
            Assert.That(t.Host, Is.EqualTo("config"));
            Assert.That(t.Port, Is.EqualTo(1234));
        }

        [Test]
        public void Resolve_DefaultWhenNothingSet()
        {
            var t = NetTargets.ResolveServerTarget(string.Empty, "  ", out var src, out var warn);
            Assert.That(src, Is.EqualTo(TargetSource.DevDefault));
            Assert.That(t.Url, Is.EqualTo("ws://127.0.0.1:7777/"));
            Assert.That(warn, Is.Null);
        }

        [Test]
        public void Resolve_InvalidQueryFallsThroughWithWarning()
        {
            var t = NetTargets.ResolveServerTarget("http://x/?server=http://bad", "ws://config:1", out var src, out var warn);
            Assert.That(src, Is.EqualTo(TargetSource.Config));
            Assert.That(t.Host, Is.EqualTo("config"));
            StringAssert.Contains("ignoring ?server=", warn);
        }

        [Test]
        public void Resolve_InvalidQueryAndConfigFallToDefault()
        {
            var t = NetTargets.ResolveServerTarget("http://x/?server=nope", "also-nope", out var src, out var warn);
            Assert.That(src, Is.EqualTo(TargetSource.DevDefault));
            Assert.That(t.Port, Is.EqualTo(7777));
            StringAssert.Contains("config default", warn);
        }

        // ---------------- port: -port → GAMESERVER_PORT → 7777 ----------------

        private static System.Func<string, string> Env(Dictionary<string, string> d) =>
            k => d != null && d.TryGetValue(k, out var v) ? v : null;

        [Test]
        public void Port_ArgWins()
        {
            var p = NetTargets.ResolvePort(new[] { "server", "-batchmode", "-port", "9001" }, Env(new Dictionary<string, string> { ["GAMESERVER_PORT"] = "9002" }), out var src, out _);
            Assert.That(p, Is.EqualTo(9001));
            Assert.That(src, Is.EqualTo(PortSource.CommandLine));
        }

        [Test]
        public void Port_ArgEqualsForm()
        {
            var p = NetTargets.ResolvePort(new[] { "-port=9003" }, Env(null), out var src, out _);
            Assert.That(p, Is.EqualTo(9003));
            Assert.That(src, Is.EqualTo(PortSource.CommandLine));
        }

        [Test]
        public void Port_EnvWhenNoArg()
        {
            var p = NetTargets.ResolvePort(new[] { "server" }, Env(new Dictionary<string, string> { ["GAMESERVER_PORT"] = " 7800 " }), out var src, out _);
            Assert.That(p, Is.EqualTo(7800));
            Assert.That(src, Is.EqualTo(PortSource.Environment));
        }

        [Test]
        public void Port_DefaultWhenNothingSet()
        {
            var p = NetTargets.ResolvePort(new string[0], Env(null), out var src, out var warn);
            Assert.That(p, Is.EqualTo(7777));
            Assert.That(src, Is.EqualTo(PortSource.Default));
            Assert.That(warn, Is.Null);
        }

        [Test]
        public void Port_NullArgsAndEnv()
        {
            Assert.That(NetTargets.ResolvePort(null, null, out var src, out _), Is.EqualTo(7777));
            Assert.That(src, Is.EqualTo(PortSource.Default));
        }

        [TestCase("0")]
        [TestCase("65536")]
        [TestCase("abc")]
        [TestCase("")]
        public void Port_InvalidArgFallsThroughToEnv(string bad)
        {
            var args = bad.Length == 0 ? new[] { "-port" } : new[] { "-port", bad };
            var p = NetTargets.ResolvePort(args, Env(new Dictionary<string, string> { ["GAMESERVER_PORT"] = "7900" }), out var src, out var warn);
            Assert.That(p, Is.EqualTo(7900));
            Assert.That(src, Is.EqualTo(PortSource.Environment));
            StringAssert.Contains("-port", warn);
        }

        [Test]
        public void Port_InvalidEnvFallsThroughToDefault()
        {
            var p = NetTargets.ResolvePort(new string[0], Env(new Dictionary<string, string> { ["GAMESERVER_PORT"] = "x" }), out var src, out var warn);
            Assert.That(p, Is.EqualTo(7777));
            Assert.That(src, Is.EqualTo(PortSource.Default));
            StringAssert.Contains("GAMESERVER_PORT", warn);
        }
    }
}
