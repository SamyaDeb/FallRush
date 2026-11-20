using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using FallRush.Shared;
using NUnit.Framework;
using UnityEngine;

namespace FallRush.Tests
{
    public class MovementConfigTests
    {
        private static string Sha256Hex(string s)
        {
            using var sha = SHA256.Create();
            var h = sha.ComputeHash(Encoding.UTF8.GetBytes(s));
            var sb = new StringBuilder(64);
            foreach (var b in h)
                sb.Append(b.ToString("x2"));
            return sb.ToString();
        }

        [Test]
        public void TypedConstants_HashMatch_GeneratedHash()
        {
            var canon = MovementConfig.ToCanonicalJson();
            Assert.That(canon, Is.EqualTo(MovementConfigData.CanonicalJson));
            Assert.That(Sha256Hex(canon), Is.EqualTo(MovementConfigData.Sha256Hex));
        }

        [Test]
        public void JsonFileOnDisk_HashMatches_UnityValues()
        {
            var path = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "..", "packages", "shared", "config", MovementConfigData.File));
            Assert.That(File.Exists(path), Is.True, path);
            var canon = Canonicalize(new MiniJson(File.ReadAllText(path)).Parse());
            Assert.That(Sha256Hex(canon), Is.EqualTo(MovementConfigData.Sha256Hex),
                "movement_v1.json changed: run `pnpm gen:movement` and commit the regenerated MovementConfigData.g.cs");
            Assert.That(Sha256Hex(canon), Is.EqualTo(Sha256Hex(MovementConfig.ToCanonicalJson())));
        }

        [Test]
        public void TickConversions_RoundHalfUp()
        {
            var c = MovementConfig.Default;
            Assert.That(c.TickRate, Is.EqualTo(30));
            Assert.That(c.CoyoteTicks, Is.EqualTo(3));        // 100 ms
            Assert.That(c.JumpBufferTicks, Is.EqualTo(4));    // 120 ms → 3.6
            Assert.That(c.DiveCooldownTicks, Is.EqualTo(30)); // 1000 ms
            Assert.That(c.DiveLandStunTicks, Is.EqualTo(11)); // 350 ms → 10.5
            Assert.That(c.DiveCancelWindowTicks, Is.EqualTo(5)); // 150 ms → 4.5
            Assert.That(c.RespawnTicks, Is.EqualTo(30));
            Assert.That(c.Radius, Is.EqualTo(0.31f));
            Assert.That(c.Height, Is.EqualTo(1.0f));
        }

        // ---- canonical JSON (doc 03 §5): sorted keys, no whitespace, integers/strings only ----

        internal static string Canonicalize(object v)
        {
            switch (v)
            {
                case string s:
                    return "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
                case long l:
                    return l.ToString(System.Globalization.CultureInfo.InvariantCulture);
                case bool b:
                    return b ? "true" : "false";
                case Dictionary<string, object> o:
                {
                    var keys = new List<string>(o.Keys);
                    keys.Sort(StringComparer.Ordinal);
                    var sb = new StringBuilder("{");
                    for (var i = 0; i < keys.Count; i++)
                    {
                        if (i > 0)
                            sb.Append(',');
                        sb.Append(Canonicalize(keys[i])).Append(':').Append(Canonicalize(o[keys[i]]));
                    }

                    return sb.Append('}').ToString();
                }
                case List<object> a:
                {
                    var sb = new StringBuilder("[");
                    for (var i = 0; i < a.Count; i++)
                    {
                        if (i > 0)
                            sb.Append(',');
                        sb.Append(Canonicalize(a[i]));
                    }

                    return sb.Append(']').ToString();
                }
                default:
                    throw new FormatException("unsupported JSON value");
            }
        }

        /// <summary>Tiny strict JSON reader for the config files (objects, arrays, strings without escapes beyond \" \\, integers, booleans).</summary>
        internal sealed class MiniJson
        {
            private readonly string _s;
            private int _i;

            public MiniJson(string s) => _s = s;

            public object Parse()
            {
                var v = Value();
                Ws();
                if (_i != _s.Length)
                    throw new FormatException("trailing data");
                return v;
            }

            private void Ws()
            {
                while (_i < _s.Length && char.IsWhiteSpace(_s[_i]))
                    _i++;
            }

            private object Value()
            {
                Ws();
                var c = _s[_i];
                if (c == '{')
                {
                    _i++;
                    var o = new Dictionary<string, object>();
                    Ws();
                    if (_s[_i] == '}')
                    {
                        _i++;
                        return o;
                    }

                    while (true)
                    {
                        Ws();
                        var k = Str();
                        Ws();
                        Expect(':');
                        o[k] = Value();
                        Ws();
                        if (_s[_i] == ',')
                        {
                            _i++;
                            continue;
                        }

                        Expect('}');
                        return o;
                    }
                }

                if (c == '[')
                {
                    _i++;
                    var a = new List<object>();
                    Ws();
                    if (_s[_i] == ']')
                    {
                        _i++;
                        return a;
                    }

                    while (true)
                    {
                        a.Add(Value());
                        Ws();
                        if (_s[_i] == ',')
                        {
                            _i++;
                            continue;
                        }

                        Expect(']');
                        return a;
                    }
                }

                if (c == '"')
                    return Str();
                if (_s.Substring(_i).StartsWith("true"))
                {
                    _i += 4;
                    return true;
                }

                if (_s.Substring(_i).StartsWith("false"))
                {
                    _i += 5;
                    return false;
                }

                var start = _i;
                if (_s[_i] == '-')
                    _i++;
                while (_i < _s.Length && char.IsDigit(_s[_i]))
                    _i++;
                if (_i < _s.Length && (_s[_i] == '.' || _s[_i] == 'e' || _s[_i] == 'E'))
                    throw new FormatException("only integers are allowed in canonical config JSON");
                return long.Parse(_s.Substring(start, _i - start), System.Globalization.CultureInfo.InvariantCulture);
            }

            private string Str()
            {
                Expect('"');
                var sb = new StringBuilder();
                while (_s[_i] != '"')
                {
                    if (_s[_i] == '\\')
                    {
                        _i++;
                        sb.Append(_s[_i] switch { 'n' => '\n', 't' => '\t', _ => _s[_i] });
                        _i++;
                        continue;
                    }

                    sb.Append(_s[_i++]);
                }

                _i++;
                return sb.ToString();
            }

            private void Expect(char c)
            {
                if (_s[_i] != c)
                    throw new FormatException($"expected '{c}' at {_i}");
                _i++;
            }
        }
    }
}
