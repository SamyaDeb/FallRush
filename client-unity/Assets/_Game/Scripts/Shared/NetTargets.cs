using System;

namespace FallRush.Shared
{
    /// <summary>Where a client connects: parsed from <c>ws://host:port[/path]</c> or <c>wss://host[:port][/path]</c>.</summary>
    public readonly struct ServerTarget
    {
        public readonly string Host;
        public readonly ushort Port;
        public readonly bool Secure;
        public readonly string Path;

        public ServerTarget(string host, ushort port, bool secure, string path)
        {
            Host = host;
            Port = port;
            Secure = secure;
            Path = string.IsNullOrEmpty(path) ? "/" : path;
        }

        public string Url
        {
            get
            {
                var scheme = Secure ? "wss" : "ws";
                var defaultPort = Secure ? 443 : 80;
                var port = Port == defaultPort ? string.Empty : ":" + Port;
                return $"{scheme}://{Host}{port}{Path}";
            }
        }

        public override string ToString() => Url;
    }

    public enum TargetSource
    {
        QueryString,
        Config,
        DevDefault,
    }

    public enum PortSource
    {
        CommandLine,
        Environment,
        Default,
    }

    /// <summary>
    /// Pure resolution rules for the client's server URL and the server's listen port.
    /// No UnityEngine dependency, so the EditMode tests exercise exactly what ships.
    /// </summary>
    public static class NetTargets
    {
        public const string DevDefaultUrl = "ws://127.0.0.1:7777";
        public const ushort DefaultPort = 7777;
        public const string PortEnvVar = "GAMESERVER_PORT";
        public const string PortArg = "-port";
        public const string ServerQueryKey = "server";

        /// <summary>
        /// Parses <c>ws://host:port[/path]</c> or <c>wss://host[:port][/path]</c>.
        /// Missing port defaults to 80 (ws) / 443 (wss). Query strings and fragments are rejected,
        /// because UTP's WebSocket server matches the request path exactly.
        /// </summary>
        public static bool TryParseServerUrl(string url, out ServerTarget target, out string error)
        {
            target = default;
            if (string.IsNullOrWhiteSpace(url))
            {
                error = "empty server URL";
                return false;
            }

            url = url.Trim();
            bool secure;
            string rest;
            if (url.StartsWith("wss://", StringComparison.OrdinalIgnoreCase))
            {
                secure = true;
                rest = url.Substring(6);
            }
            else if (url.StartsWith("ws://", StringComparison.OrdinalIgnoreCase))
            {
                secure = false;
                rest = url.Substring(5);
            }
            else
            {
                error = $"server URL must start with ws:// or wss:// (got \"{url}\")";
                return false;
            }

            if (rest.IndexOfAny(new[] { '?', '#', '@' }) >= 0)
            {
                error = "server URL must not contain a query, fragment or credentials";
                return false;
            }

            var slash = rest.IndexOf('/');
            var authority = slash >= 0 ? rest.Substring(0, slash) : rest;
            var path = slash >= 0 ? rest.Substring(slash) : "/";

            if (authority.Length == 0)
            {
                error = "server URL has no host";
                return false;
            }

            if (authority.IndexOf('[') >= 0)
            {
                error = "IPv6 literals are not supported in server URLs";
                return false;
            }

            string host;
            ushort port;
            var colon = authority.LastIndexOf(':');
            if (colon >= 0)
            {
                host = authority.Substring(0, colon);
                var portText = authority.Substring(colon + 1);
                if (!ushort.TryParse(portText, out port) || port == 0)
                {
                    error = $"invalid port \"{portText}\"";
                    return false;
                }
            }
            else
            {
                host = authority;
                port = (ushort)(secure ? 443 : 80);
            }

            if (host.Length == 0 || host.IndexOf(':') >= 0 || host.IndexOf(' ') >= 0)
            {
                error = $"invalid host \"{host}\"";
                return false;
            }

            target = new ServerTarget(host, port, secure, path);
            error = null;
            return true;
        }

        /// <summary>Returns the URL-decoded value of <paramref name="key"/> in the URL's query string, or null.</summary>
        public static string GetQueryParam(string absoluteUrl, string key)
        {
            if (string.IsNullOrEmpty(absoluteUrl) || string.IsNullOrEmpty(key))
                return null;

            var q = absoluteUrl.IndexOf('?');
            if (q < 0)
                return null;

            var query = absoluteUrl.Substring(q + 1);
            var hash = query.IndexOf('#');
            if (hash >= 0)
                query = query.Substring(0, hash);

            foreach (var pair in query.Split('&'))
            {
                if (pair.Length == 0)
                    continue;
                var eq = pair.IndexOf('=');
                var k = eq >= 0 ? pair.Substring(0, eq) : pair;
                if (!string.Equals(Unescape(k), key, StringComparison.Ordinal))
                    continue;
                return eq >= 0 ? Unescape(pair.Substring(eq + 1)) : string.Empty;
            }

            return null;
        }

        private static string Unescape(string s)
        {
            try
            {
                return Uri.UnescapeDataString(s.Replace('+', ' '));
            }
            catch (UriFormatException)
            {
                return s;
            }
        }

        /// <summary>
        /// Resolution order: <c>?server=</c> on the page URL → config default → dev default.
        /// An invalid value at one level falls through to the next and is reported in <paramref name="warning"/>.
        /// </summary>
        public static ServerTarget ResolveServerTarget(string absoluteUrl, string configDefault, out TargetSource source, out string warning)
        {
            warning = null;

            var fromQuery = GetQueryParam(absoluteUrl, ServerQueryKey);
            if (!string.IsNullOrEmpty(fromQuery))
            {
                if (TryParseServerUrl(fromQuery, out var t, out var err))
                {
                    source = TargetSource.QueryString;
                    return t;
                }

                warning = $"ignoring ?{ServerQueryKey}=: {err}";
            }

            if (!string.IsNullOrWhiteSpace(configDefault))
            {
                if (TryParseServerUrl(configDefault, out var t, out var err))
                {
                    source = TargetSource.Config;
                    return t;
                }

                warning = (warning == null ? string.Empty : warning + "; ") + $"ignoring config default: {err}";
            }

            TryParseServerUrl(DevDefaultUrl, out var dev, out _);
            source = TargetSource.DevDefault;
            return dev;
        }

        /// <summary>
        /// Resolution order: <c>-port N</c> (or <c>-port=N</c>) command-line arg → <c>GAMESERVER_PORT</c> → 7777.
        /// Invalid values fall through to the next source and are reported in <paramref name="warning"/>.
        /// </summary>
        public static ushort ResolvePort(string[] args, Func<string, string> getEnv, out PortSource source, out string warning)
        {
            warning = null;

            if (args != null)
            {
                for (var i = 0; i < args.Length; i++)
                {
                    string value = null;
                    if (string.Equals(args[i], PortArg, StringComparison.OrdinalIgnoreCase))
                        value = i + 1 < args.Length ? args[i + 1] : string.Empty;
                    else if (args[i].StartsWith(PortArg + "=", StringComparison.OrdinalIgnoreCase))
                        value = args[i].Substring(PortArg.Length + 1);

                    if (value == null)
                        continue;

                    if (ushort.TryParse(value, out var p) && p != 0)
                    {
                        source = PortSource.CommandLine;
                        return p;
                    }

                    warning = $"ignoring {PortArg} \"{value}\"";
                    break;
                }
            }

            var env = getEnv?.Invoke(PortEnvVar);
            if (!string.IsNullOrEmpty(env))
            {
                if (ushort.TryParse(env.Trim(), out var p) && p != 0)
                {
                    source = PortSource.Environment;
                    return p;
                }

                warning = (warning == null ? string.Empty : warning + "; ") + $"ignoring {PortEnvVar}=\"{env}\"";
            }

            source = PortSource.Default;
            return DefaultPort;
        }
    }
}
