using System;
using FallRush.Net;
using UnityEngine;

namespace FallRush.OnChain
{
    /// <summary>
    /// P3-F (client): listens for the game server's <c>fonc</c> (<see cref="ChainFinalHandoff"/>) during a Classic match
    /// and starts <see cref="OnChainSession.StartFinal"/>: our slot if we are in the Final's roster, else spectate the
    /// arena (eliminated players watch the on-chain World). Without <c>fonc</c> nothing happens (Classic unchanged).
    /// </summary>
    public static class OnChainSwitch
    {
        [Serializable] private sealed class Fonc { public string t, net, arena, match, url; public bool all; public RosterEntry[] slot_map; }

        /// <summary>fullchain: does this <c>fonc</c> hand over the whole match (<c>"all":true</c>)?</summary>
        public static bool IsWholeMatch(string json)
        {
            try { return JsonUtility.FromJson<Fonc>(json)?.all == true; }
            catch { return false; }
        }

        private static ClientSim _hooked;

        /// <summary>Replays a <c>fonc</c> once the previous <see cref="OnChainSession"/> is destroyed (≤ 60 frames).</summary>
        private sealed class Later : MonoBehaviour
        {
            public void Replay(string json) => StartCoroutine(Run(json));

            private System.Collections.IEnumerator Run(string json)
            {
                for (var i = 0; i < 60 && OnChainSession.Instance != null; i++)
                    yield return null;
                if (OnChainSession.Instance == null)
                    OnFonc(json);
                Destroy(gameObject);
            }
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Install()
        {
            UnityEngine.SceneManagement.SceneManager.sceneLoaded += (_, __) => Hook();
            Hook();
        }

        private static void Hook()
        {
            var cs = ClientSim.Instance;
            if (cs == null || ReferenceEquals(cs, _hooked))
                return;
            _hooked = cs;
            cs.FinalOnchain += OnFonc;
        }

        /// <summary>Our slot if it is a human slot of the Final's roster, else −1 (spectate).</summary>
        public static int PickSlot(RosterEntry[] slotMap, int own)
        {
            foreach (var r in slotMap ?? Array.Empty<RosterEntry>())
                if (r.slot == own && r.kind == "human")
                    return own;
            return -1;
        }

        public static RosterEntry[] ParseSlotMap(string json) => JsonUtility.FromJson<Fonc>(json)?.slot_map;

        public static void OnFonc(string json)
        {
            var cs = ClientSim.Instance;
            if (cs == null)
                return;
            Fonc f;
            try { f = JsonUtility.FromJson<Fonc>(json); }
            catch (Exception e)
            {
                Debug.LogWarning("[onchain] bad fonc: " + e.Message);
                return;
            }

            var slot = PickSlot(f.slot_map, cs.MatchSlot);
            var cfg = ChainConfig.FromArgs();
            cfg.target = string.IsNullOrEmpty(f.net) ? "emu" : f.net;
            cfg.bundleConfig = cfg.target switch { "local" => "local.json", "devnet" => "devnet.json", _ => "emulator.json" };
            if (cfg.target != "emu" && string.IsNullOrEmpty(ChainConfig.UrlArg()))
                cfg.url = "ws://127.0.0.1:8788/"; // Mac player → tools/p3/chain-relay.mjs (WebGL: the bundle itself)
            if (!string.IsNullOrEmpty(f.url))
                cfg.url = f.url;
            cfg.arena = f.arena;
            // P5 arena pool: one bundle config per arena (StreamingAssets/chain/devnet-<arena>.json, tools/p5/arena-configs.mjs)
            if (cfg.target == "devnet" && !string.IsNullOrEmpty(f.arena))
                cfg.bundleConfig = "devnet-" + f.arena + ".json";
            if (f.t == "final_prejoin")
            {
                if (OnChainSession.Instance != null)
                {
                    // fullchain (production canary): the previous whole match's session can still be up when the next
                    // match's pre-join arrives; it dropped the pre-join and the player's slot ran as a bot. End the
                    // stale session and replay this pre-join once it is gone (Destroy lands at the end of the frame).
                    var old = OnChainSession.Instance;
                    if (f.all && !string.IsNullOrEmpty(f.match) && old.MatchHex != f.match)
                    {
                        Debug.Log($"[onchain] pre-join for match {f.match}: ending the previous match's session ({old.MatchHex})");
                        old.End();
                        new GameObject("onchain-prejoin-later").AddComponent<Later>().Replay(json);
                    }
                    return;
                }
                Debug.Log($"[onchain] pre-join: {cfg.target} arena {f.arena} match {f.match}, slot {slot}");
                var s = OnChainSession.StartFinal(cfg, cs.ServerMatch, slot);
                s.MatchHex = f.match;
                s.FullMatch = f.all; // fullchain: join for rounds 1–4
                return;
            }

            if (OnChainSession.Instance != null)
            {
                OnChainSession.Instance.ServerConfirmed = true; // pre-joined: the server confirms the on-chain Final
                if (f.all)
                    OnChainSession.Instance.FullMatch = true;
                return;
            }
            Debug.Log($"[onchain] switch: {(f.all ? "whole match" : "Final")} on {f.arena} ({cfg.url}), {(slot >= 0 ? "playing slot " + slot : "spectating")}");
            var st = OnChainSession.StartFinal(cfg, cs.ServerMatch, slot);
            st.FullMatch = f.all;
        }
    }
}
