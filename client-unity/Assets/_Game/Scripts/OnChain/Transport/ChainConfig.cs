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
        /// <summary>Input run (2026-11-25): set when ?lead= / ?window= (or the -fallrushOnchain… args) chose them.</summary>
        public bool leadSet, windowSet;

        /// <summary>Rollup lead / window for the rollup: the arenas' forward window is widened to 13 ticks
        /// (set_input_lead, the 16-entry ring's maximum), and the first stamp moves T+3 → T+5. Measured on production
        /// (match 61aRaV…, 2026-11-25): with T+3..6 and window 8, 7–43 % of a player's ticks had no input on-chain
        /// (chain micro-stalls → batches past the window rejected; browser stalls → ticks skipped), so the arena moved
        /// them with a stale input ("hit by an invisible ball"). Own movement is predicted, so the lead costs no feel.
        /// An arena still at window 8 is detected from the rejections (<c>OnChainSession</c> falls back to 8 / T+3).</summary>
        public void ApplyRollupDefaults()
        {
            if (target == "emu")
                return;
            if (!windowSet)
                inputWindow = 13;
            if (!leadSet)
                inputLead = Math.Min(5, Math.Max(1, inputWindow - 3));
        }
        public int rate = 30;
        public float smoothingMs = 120f;
        public float snapMeters = 2f;
        /// <summary>Smooth run: remote players on the on-chain interpolation profile (else the Classic 100–150 ms buffer).</summary>
        public bool smoothRemotes = true;
        /// <summary>Smooth run: bots drawn from the own prediction (exact, live) instead of late Worlds.</summary>
        public bool predictBots = true;
        /// <summary>Smooth run: other humans drawn from the prediction too (live, corrected on input changes). Off by default.</summary>
        public bool predictHumans;
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
            {
                c.inputLead = l;
                c.leadSet = true;
            }
            var win = GetArg("-fallrushOnchainWindow") ?? NetTargets.GetQueryParam(Application.absoluteURL, "window");
            if (int.TryParse(win, out var wv) && wv >= 8 && wv <= 13)
            {
                c.inputWindow = wv;
                c.windowSet = true;
            }
            // smooth run playtest switch: ?smooth=0 old behaviour, 1 default (bots predicted + on-chain buffer), 2 + humans predicted
            var sm = GetArg("-fallrushSmooth") ?? NetTargets.GetQueryParam(Application.absoluteURL, "smooth");
            if (int.TryParse(sm, out var smv) && smv >= 0 && smv <= 2)
            {
                c.smoothRemotes = smv >= 1;
                c.predictBots = smv >= 1;
                c.predictHumans = smv >= 2;
            }
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
