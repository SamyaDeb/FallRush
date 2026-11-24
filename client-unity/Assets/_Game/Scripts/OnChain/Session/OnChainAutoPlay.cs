using System;
using System.IO;
using FallRush.Client;
using FallRush.Net;
using FallRush.Shared;
using UnityEngine;

namespace FallRush.OnChain
{
    /// <summary>
    /// P3-D measurement (dev): <c>-fallrushOnchainAuto &lt;seconds&gt;</c> / <c>?onchainauto=&lt;seconds&gt;</c> plays the own
    /// Rushling with a scripted input (shadows the nearest bot, a small hop every 3 s), logs
    /// <see cref="OnChainSession.StatsLine"/> every 5 s (corrections per 5 s, updates/s, batches/s, RTT, confirms),
    /// saves a screenshot at half time (<c>-fallrushOnchainShot &lt;png&gt;</c>) and quits (desktop) when the time is up.
    /// </summary>
    public sealed class OnChainAutoPlay : MonoBehaviour, IInputSource
    {
        private float _seconds, _t, _logAt = 5f;
        private bool _shot;
        private string _shotPath;
        private uint _n;
        public int CorrectionSamples;
        public long CorrectionSum;
        public int MaxPer5;

        public static float Arg()
        {
            var a = Environment.GetCommandLineArgs();
            for (var i = 0; i < a.Length - 1; i++)
                if (a[i] == "-fallrushOnchainAuto" && float.TryParse(a[i + 1], out var s))
                    return s;
            return float.TryParse(NetTargets.GetQueryParam(Application.absoluteURL, "onchainauto"), out var q) ? q : 0f;
        }

        /// <summary>P4 feel: a screenshot of each Chaos Drop's telegraph (10 ticks after it appears), next to the probe's shot.</summary>
        private static void HookDropShots(OnChainSession s)
        {
            var shot = ShotArg();
            if (string.IsNullOrEmpty(shot))
                return;
            var n = 0;
            s.DropSeen += (kind, trigger) =>
            {
                var path = System.IO.Path.ChangeExtension(shot, null) + $"-drop{++n}-{ChaosDropFx.Names[kind].Replace(' ', '_')}.png";
                s.StartCoroutine(ShotLater(path, 0.33f));
            };
        }

        private static System.Collections.IEnumerator ShotLater(string path, float secs)
        {
            yield return new WaitForSecondsRealtime(secs);
            ScreenCapture.CaptureScreenshot(path);
            Debug.Log($"[onchain] drop telegraph shot {path}");
        }

        private static string ShotArg()
        {
            var a = Environment.GetCommandLineArgs();
            for (var i = 0; i < a.Length - 1; i++)
                if (a[i] == "-fallrushOnchainShot")
                    return a[i + 1];
            return null;
        }

        public static void Attach(OnChainSession s, float seconds)
        {
            HookDropShots(s);
            var a = s.gameObject.AddComponent<OnChainAutoPlay>();
            a._seconds = seconds;
            var args = Environment.GetCommandLineArgs();
            for (var i = 0; i < args.Length - 1; i++)
                if (args[i] == "-fallrushOnchainShot")
                    a._shotPath = args[i + 1];
        }

        public void Sample(ref InputCmd cmd)
        {
            _n++;
            // Shadow the nearest bot (the bots read the lids and keep to safe ones): walk to ~0.6 m behind it, small
            // hops now and then. Falls still happen (pushes, flips), as in real play.
            var cs = ClientSim.Instance;
            var dir = Vector2.zero;
            if (cs != null && cs.TrySample(cs.OwnSlot, out var me, out _, out _, out _, out _))
            {
                var best = float.MaxValue;
                var target = me;
                for (var s = 0; s < 12; s++)
                    if (s != cs.OwnSlot && cs.Remote.Sample(s, out var f, out var fv, out _))
                    {
                        var p = new Vector3(f.X, f.Y, f.Z);
                        var d = (p - me).sqrMagnitude;
                        if (d < best)
                        {
                            best = d;
                            target = p - new Vector3(fv.X, 0f, fv.Z).normalized * 0.6f;
                        }
                    }

                var to = new Vector2(target.x - me.x, target.z - me.z);
                if (to.magnitude > 0.3f)
                    dir = to.normalized * Mathf.Clamp01(to.magnitude / 1.5f);
            }

            cmd.MoveX = (sbyte)(dir.x * 120f);
            cmd.MoveY = (sbyte)(dir.y * 120f);
            cmd.Buttons = 0;
            if (_n % 90 < 4)
                cmd.Buttons |= InputCmd.JumpHeld;
            if (_n % 90 == 0)
                cmd.Buttons |= InputCmd.JumpPressed;
            cmd.CamYaw = 0;
        }

        // P4 (auto-play probe): one bet per open market through the in-game economy (the Mac player's wallet, session key)
        private readonly bool[] _bet = new bool[5];
        private float _betAt;

        // P4 (auto-play probe, opt-in: -fallrushOnchainAutoDrop / ?autodrop=1): buys up to 2 Chaos Drops through the
        // in-game economy (the drop bar's path) once the Final's RUSH is on
        private static readonly bool AutoDropOn = Array.Exists(Environment.GetCommandLineArgs(), a => a == "-fallrushOnchainAutoDrop") || NetTargets.GetQueryParam(Application.absoluteURL, "autodrop") == "1";
        private int _drops;
        private float _dropAt;

        private void AutoDrop(ChainEconomy e)
        {
            if (!AutoDropOn || _drops >= 2 || _t < _dropAt || !e.DropOpen(out _))
                return;
            _dropAt = _t + 7f;
            var kind = _drops == 0 ? 1 : 3;
            _drops++;
            Debug.Log($"[onchain] auto-drop {ChaosDropFx.Names[kind]}");
            e.Drop(kind, ok => Debug.Log($"[onchain] auto-drop {kind}: {(ok ? "ok" : e.LastError)} {(e.LastSession ? "session key" : "")} {e.LastSig}"));
        }

        // P4 UI proof: -fallrushOnchainShots <dir> → an in-app screenshot every 10 s (the economy HUD in the player build)
        private static readonly string ShotsDir = ArgValue("-fallrushOnchainShots");
        private float _shotsAt = 5f;
        private int _shotsN;

        private static string ArgValue(string k)
        {
            var a = Environment.GetCommandLineArgs();
            for (var i = 0; i < a.Length - 1; i++)
                if (a[i] == k)
                    return a[i + 1];
            return null;
        }

        private void PeriodicShot()
        {
            if (string.IsNullOrEmpty(ShotsDir) || _t < _shotsAt)
                return;
            _shotsAt = _t + 10f;
            ScreenCapture.CaptureScreenshot(Path.Combine(ShotsDir, $"ui-{++_shotsN:00}.png"));
        }

        private void AutoBet(OnChainSession s)
        {
            PeriodicShot();
            var e = ChainEconomy.Instance;
            if (e != null && e.State != null)
                AutoDrop(e);
            if (e?.State?.match == null || _t < _betAt || !e.ChipsReady)
                return;
            _betAt = _t + 1.5f;
            if (e.MatchPda != null && e.State.match.pda != e.MatchPda)
                return;
            for (var k = 0; k < 5; k++)
            {
                if (_bet[k] || !e.MarketOpen(k) || (k == 0 && e.AutobetOwnsChampion))
                    continue;
                _bet[k] = true;
                var slots = e.State.match.slots;
                var pick = s.OwnSlot < 12 && s.OwnSlot < slots.Length && slots[s.OwnSlot] == 1 ? s.OwnSlot : System.Array.FindIndex(slots, x => x == 1);
                if (pick < 0)
                    continue;
                Debug.Log($"[onchain] auto-bet market {k} on slot {pick}");
                e.Bet(k, pick, 10 + 2 * k, ok => Debug.Log($"[onchain] auto-bet market {k}: {(ok ? "ok" : e.LastError)} {(e.LastSession ? "session key" : "")} {e.LastSig}"));
                return;
            }
        }

        private void Update()
        {
            var cs = ClientSim.Instance;
            if (cs != null && !ReferenceEquals(cs.InputSource, this))
                cs.InputSource = this;
            _t += Time.unscaledDeltaTime;
            var s = OnChainSession.Instance;
            if (s == null)
                return;
            AutoBet(s);
            if (_t >= _logAt)
            {
                _logAt += 5f;
                var now = ClientSim.Now;
                var p = s.Prediction;
                if (p != null && cs != null && !cs.OwnOutOfSim)
                {
                    var c = p.CorrectionsSince(now, 5);
                    CorrectionSamples++;
                    CorrectionSum += c;
                    MaxPer5 = Mathf.Max(MaxPer5, c);
                }

                Debug.Log(s.StatsLine(now) + $" fps {1f / Mathf.Max(1e-4f, Time.smoothDeltaTime):F0} playing {(cs != null && !cs.OwnOutOfSim)} phase {(cs != null && cs.Match != null ? cs.Match.Phase.ToString() : "-")}");
            }

            if (!_shot && _shotPath != null && _t >= _seconds * 0.5f)
            {
                _shot = true;
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_shotPath)) ?? ".");
                ScreenCapture.CaptureScreenshot(_shotPath);
            }

            if (_t >= _seconds)
            {
                Debug.Log($"[onchain] AUTO DONE {s.StatsLine(ClientSim.Now)} corr/5s mean {(CorrectionSamples > 0 ? (float)CorrectionSum / CorrectionSamples : 0):F2} max {MaxPer5} over {CorrectionSamples} windows");
                if (Application.platform != RuntimePlatform.WebGLPlayer)
                    Application.Quit(0);
                enabled = false;
            }
        }
    }
}
