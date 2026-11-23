using System;
using System.Collections.Concurrent;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;

namespace FallRush.Net
{
    /// <summary>
    /// P5 task 4 (cosmetic only, never stats): a player may wear the shell of a Rushling Capsule it owns. The client
    /// sends the capsule (Metaplex Core asset) and its wallet in hello; the server reads the asset over Solana RPC and
    /// applies the shell only if (1) the account is owned by Metaplex Core, (2) its owner field is that wallet, (3) its
    /// update authority is the fall_rush program's <c>["assets"]</c> PDA (minted by the program from a receipt) and (4)
    /// its <c>shell</c> attribute names one of the 12 shells. Anything else (a falsified id, a read error) keeps the
    /// preset. Not checked: that the client controls the wallet (no signature in hello; cosmetic only).
    /// </summary>
    public static class ShellCheck
    {
        public const string CoreProgram = "CoREENxT6tW1HoK8ypY1SxRMZTcVPm7R94rH4PZNhX7d";
        /// <summary>fall_rush 4yehff… PDA ["assets"] = the capsules' update authority.</summary>
        public const string AssetsAuthority = "7puJtevqtCLvmYEfcBo5zqwiAqX72LqmMT5ATycn2MND";
        /// <summary>econ.rs SHELLS, index = the cosmetic preset applied.</summary>
        public static readonly string[] Shells = { "Bolt", "Bubblegum", "Tangerine", "SodaBlue", "GrapeFizz", "SourApple", "Lemon", "Mint", "Starlight", "Lava", "Glacier", "Aurora" };

        private static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromSeconds(6) };
        public static readonly ConcurrentQueue<(ulong client, int preset, string asset, string why)> Results = new ConcurrentQueue<(ulong, int, string, string)>();

        public static string RpcUrl => ServerSim.ArgStr("-fallrushSolanaRpc", "GAMESERVER_SOLANA_RPC") ?? "https://api.devnet.solana.com";

        public static void Begin(ulong client, string asset, string wallet) => _ = Run(client, asset, wallet);

        private static async Task Run(ulong client, string asset, string wallet)
        {
            try
            {
                var (preset, why) = await Verify(asset, wallet).ConfigureAwait(false);
                Results.Enqueue((client, preset, asset, why));
            }
            catch (Exception e)
            {
                Results.Enqueue((client, -1, asset, "rpc: " + e.Message));
            }
        }

        public static async Task<(int preset, string why)> Verify(string asset, string wallet)
        {
            var a = Base58(asset);
            var w = Base58(wallet);
            if (a == null || w == null || a.Length != 32 || w.Length != 32)
                return (-1, "bad base58");
            var req = "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"getAccountInfo\",\"params\":[\"" + asset + "\",{\"encoding\":\"base64\",\"commitment\":\"confirmed\"}]}";
            var r = await Http.PostAsync(RpcUrl, new StringContent(req, Encoding.UTF8, "application/json")).ConfigureAwait(false);
            var t = await r.Content.ReadAsStringAsync().ConfigureAwait(false);
            if (!t.Contains("\"owner\":\"" + CoreProgram + "\""))
                return (-1, "not a Core asset");
            var i = t.IndexOf("\"data\":[\"", StringComparison.Ordinal);
            if (i < 0)
                return (-1, "no data");
            i += 9;
            var d = Convert.FromBase64String(t.Substring(i, t.IndexOf('"', i) - i));
            return Check(d, w);
        }

        /// <summary>Core AssetV1: key u8 (1) | owner 32 | update authority (u8 kind 1 = address | 32) | name | uri | … plugins;
        /// the Attributes plugin holds borsh strings ("shell", value).</summary>
        public static (int preset, string why) Check(byte[] d, byte[] wallet)
        {
            if (d.Length < 66 || d[0] != 1)
                return (-1, "not an AssetV1");
            for (var k = 0; k < 32; k++)
                if (d[1 + k] != wallet[k])
                    return (-1, "owner is not the wallet");
            var auth = Base58(AssetsAuthority);
            if (d[33] != 1)
                return (-1, "update authority is not an address");
            for (var k = 0; k < 32; k++)
                if (d[34 + k] != auth[k])
                    return (-1, "not minted by Fall Rush");
            var key = Encoding.ASCII.GetBytes("shell");
            for (var p = 66; p + 4 + key.Length + 4 < d.Length; p++)
            {
                if (d[p] != key.Length || d[p + 1] != 0 || d[p + 2] != 0 || d[p + 3] != 0)
                    continue;
                var m = true;
                for (var k = 0; k < key.Length && m; k++)
                    m = d[p + 4 + k] == key[k];
                if (!m)
                    continue;
                var vAt = p + 4 + key.Length;
                var vl = BitConverter.ToInt32(d, vAt);
                if (vl <= 0 || vl > 32 || vAt + 4 + vl > d.Length)
                    return (-1, "bad shell attribute");
                var v = Encoding.UTF8.GetString(d, vAt + 4, vl);
                var idx = Array.IndexOf(Shells, v);
                return idx >= 0 ? (idx, "ok " + v) : (-1, "unknown shell " + v);
            }
            return (-1, "no shell attribute (not a capsule)");
        }

        private const string Alphabet = "123456789ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz";

        public static byte[] Base58(string s)
        {
            if (string.IsNullOrEmpty(s))
                return null;
            var big = new System.Numerics.BigInteger(0);
            foreach (var c in s)
            {
                var v = Alphabet.IndexOf(c);
                if (v < 0)
                    return null;
                big = big * 58 + v;
            }
            var bytes = big.ToByteArray(); // little-endian, maybe a sign byte
            var len = bytes.Length;
            if (len > 1 && bytes[len - 1] == 0)
                len--;
            var zeros = 0;
            while (zeros < s.Length && s[zeros] == '1')
                zeros++;
            var outp = new byte[zeros + (big.IsZero ? 0 : len)];
            for (var k = 0; k < (big.IsZero ? 0 : len); k++)
                outp[outp.Length - 1 - k] = bytes[k];
            return outp;
        }
    }
}
