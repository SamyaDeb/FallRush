using System;
using FallRush.Shared;
using UnityEngine;

namespace FallRush.OnChain
{
    /// <summary>
    /// P3-D: everything the client needs to reach an arena. Layout-agnostic: the World account offsets and (for the
    /// rollup) addresses live in the bundle config JSON (<c>packages/chain/config/*.json</c>); this is the C# side.
    /// Sources, last wins: defaults (emulator on localhost) → <c>?onchain=emu|local|devnet</c> /
    /// <c>-fallrushOnchain …</c> → <c>?onchainurl=</c> / <c>-fallrushOnchainUrl</c>.
    /// </summary>
    [Serializable]
    public sealed class ChainConfig
    {
        /// <summary>"emu" (local chain emulator), "local" (P2's local stack), "devnet" (devnet-as).</summary>
        public string target = "emu";
        /// <summary>Emulator / relay WebSocket (Editor and Mac player; WebGL uses the bundle with the same target).</summary>
        public string url = "ws://127.0.0.1:8797/";
        /// <summary>Bundle config file under StreamingAssets/chain/ (WebGL).</summary>
        public string bundleConfig = "emulator.json";
        public string arena = "";
        public WorldAccLayout world = new WorldAccLayout();
        /// <summary>Inputs stamped T + lead for the first tick of a batch (doc: T+3).</summary>
        public int inputLead = 3;
        /// <summary>P4 0.4: the arena's forward input window (`submit_input` accepts a batch starting ≤ T + window;
        /// program default 8, `set_input_lead` up to 13). Caps the adaptive extra lead.</summary>
        public int inputWindow = 8;
        /// <summary>Ticks per input transaction (doc: 3, one transaction per 100 ms at 30 Hz).</summary>
        public int batchTicks = 3;
        public int rate = 30;
        public float smoothingMs = 120f;
        public float snapMeters = 2f;
        /// <summary>No World for this long → "! LAGGING" / chip Pending.</summary>
        public float lagMs = 300f;
        public float pollSeconds = 1f;

        public static string TargetArg() =>
            GetArg("-fallrushOnchain") ?? NetTargets.GetQueryParam(Application.absoluteURL, "onchain");

        public static ChainConfig FromArgs()
        {
            var c = new ChainConfig();
            var t = TargetArg();
            if (!string.IsNullOrEmpty(t))
                c.target = t;
            c.bundleConfig = c.target switch { "local" => "local.json", "devnet" => "devnet.json", _ => "emulator.json" };
            if (c.target == "local" || c.target == "devnet")
                c.url = "ws://127.0.0.1:8788/"; // Mac player → tools/p3/chain-relay.mjs (the bundle in Node)
            var u = UrlArg();
            if (!string.IsNullOrEmpty(u))
                c.url = u;
            var lead = GetArg("-fallrushOnchainLead") ?? NetTargets.GetQueryParam(Application.absoluteURL, "lead");
            if (int.TryParse(lead, out var l) && l >= 0 && l <= 8)
                c.inputLead = l;
            var win = GetArg("-fallrushOnchainWindow") ?? NetTargets.GetQueryParam(Application.absoluteURL, "window");
            if (int.TryParse(win, out var wv) && wv >= 8 && wv <= 13)
                c.inputWindow = wv;
            return c;
        }

        public static string UrlArg() => GetArg("-fallrushOnchainUrl") ?? NetTargets.GetQueryParam(Application.absoluteURL, "onchainurl");

        private static string GetArg(string a)
        {
            var args = Environment.GetCommandLineArgs();
            for (var i = 0; i < args.Length - 1; i++)
                if (args[i] == a)
                    return args[i + 1];
            return null;
        }
    }
}
