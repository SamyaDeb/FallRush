using FallRush.Net;
using FallRush.Shared;
using UnityEngine;
using UnityEngine.SceneManagement;
using TMPro;
using UnityEngine.UI;

namespace FallRush.Client
{
    /// <summary>Lobby: connection state, my client id, connected players, live RTT p50/p95, Reconnect.</summary>
    public sealed class LobbyScreen : MonoBehaviour
    {
        private const float RefreshSeconds = 0.2f;

        private TMP_Text _state;
        private TMP_Text _details;
        private TMP_Text _rtt;
        private Button _reconnect;
        private Button _enter;
        private bool _autoplay;
        private float _nextRefresh;

        private void Start()
        {
            // G3.5 connecting / reconnecting screen (UI kit over the sky).
            UiStage.Ensure(Camera.main);
            var canvas = UiKit.CreateCanvas("LobbyCanvas");
            var panel = UiKit.Panel(canvas, new Vector2(680, 560), UiKit.CloudWhite);

            UiKit.Shout(panel, "FALL RUSH", 56, UiKit.Magenta).GetComponent<LayoutElement>().minHeight = 70;
            _state = UiKit.Label(panel, string.Empty, 32, UiKit.Ink, FontStyle.Bold, 48);
            _details = UiKit.Label(panel, string.Empty, 17, UiKit.Ink, FontStyle.Normal, 80);
            _rtt = UiKit.Label(panel, string.Empty, 20, UiKit.Grape, FontStyle.Bold, 30);
            _enter = UiKit.Button(panel, "Enter Sandbox (M2)", UiKit.Mint, OnEnter);
            _reconnect = UiKit.Button(panel, "Retry", UiKit.SunYellow, OnReconnect);
            _autoplay = NetTargets.GetQueryParam(Application.absoluteURL, "autoplay") == "1";
            UiKit.Button(panel, "Back to Menu", UiKit.SkyLight, OnBack);

            if (NetBootstrap.Instance != null)
                NetBootstrap.Instance.StateChanged += Refresh;
            Refresh();
        }

        private void OnDestroy()
        {
            if (NetBootstrap.Instance != null)
                NetBootstrap.Instance.StateChanged -= Refresh;
        }

        private void Update()
        {
            if (Time.unscaledTime < _nextRefresh)
                return;
            _nextRefresh = Time.unscaledTime + RefreshSeconds;
            Refresh();
        }

        private void Refresh()
        {
            var boot = NetBootstrap.Instance;
            if (boot == null || _state == null)
            {
                if (_state != null)
                    _state.text = "NetBootstrap missing — start from Boot.";
                return;
            }

            var ping = PingService.Instance;
            switch (boot.State)
            {
                case ClientState.Connecting:
                    _state.text = boot.ConnectAttempt > 1 ? $"Connecting… (attempt {boot.ConnectAttempt} of {NetBootstrap.MaxInitialConnects})" : "Connecting…";
                    _state.fontSize = boot.ConnectAttempt > 1 ? 26 : 32;
                    _state.color = UiKit.Sky;
                    break;
                case ClientState.Connected:
                    _state.text = "Connected";
                    _state.color = UiKit.Mint;
                    break;
                case ClientState.Disconnected when boot.RetryPending:
                    _state.text = $"Connecting… (attempt {boot.ConnectAttempt + 1} of {NetBootstrap.MaxInitialConnects} soon)";
                    _state.color = UiKit.Sky;
                    break;
                case ClientState.Disconnected:
                    _state.text = "Disconnected";
                    _state.color = UiKit.Magenta;
                    break;
                default:
                    _state.text = "Idle";
                    _state.color = UiKit.Ink;
                    break;
            }

            var nm = boot.Manager;
            if (boot.State == ClientState.Connected && nm != null)
            {
                var players = ping != null ? ping.ServerPlayerCount : 0;
                _details.text = $"Players connected: {(players > 0 ? players.ToString() : "…")} / {Protocol.MaxPlayers}\n<size=13><color=#8C88A6>{boot.Target.Url} · client {nm.LocalClientId}</color></size>";
            }
            else if (boot.State == ClientState.Disconnected)
            {
                _details.text = $"Server: {boot.Target.Url}\n{boot.LastDisconnectReason}";
            }
            else
            {
                _details.text = $"Server: {boot.Target.Url}";
            }

            if (boot.State == ClientState.Connected && ping != null && ping.SampleCount > 0)
                _rtt.text = $"<color=#5BE3B5>●</color> {ping.P50Ms:F0} ms  <size=13>(p95 {ping.P95Ms:F0})</size>";
            else
                _rtt.text = boot.State == ClientState.Connected ? "RTT: measuring…" : string.Empty;

            _reconnect.gameObject.SetActive(boot.State == ClientState.Disconnected);
            var cs = ClientSim.Instance;
            var hasRound = cs != null && cs.Round.HasValue;
            _enter.gameObject.SetActive(boot.State == ClientState.Connected && hasRound);
            if (hasRound)
            {
                var r = cs.Round.Value;
                var label = r.MapId == MapPoolV1.BlockPartyDash
                    ? $"Enter {UiNames.Map(r.MapId)} ({(r.Variant == 0 ? "A" : "B")}, {UiNames.Modifier(r.Modifier)})"
                    : "Enter Sandbox + obstacle kit";
                UiKit.SetButtonText(_enter, label);
            }

            // M4: a match server (it sends `match`) drives the scene flow: go straight to the lobby room.
            if (cs != null && cs.Match != null && boot.State == ClientState.Connected)
            {
                cs.FollowScenes = true;
                _state.text = "Joining the lobby…";
                return;
            }

            if (_autoplay && boot.State == ClientState.Connected && hasRound)
            {
                _autoplay = false;
                OnEnter();
            }
        }

        private void OnReconnect() => NetBootstrap.Instance?.Reconnect();

        private static void OnEnter()
        {
            // The server's round message names the map (Map_Sandbox or Map_BlockPartyDash).
            var cs = ClientSim.Instance;
            SceneManager.LoadScene(cs != null && cs.Round.HasValue ? ClientSim.SceneFor(cs.Round.Value.MapId) : NetBootstrap.SandboxScene);
        }

        private void OnBack()
        {
            NetBootstrap.Instance?.Disconnect();
            SceneManager.LoadScene(NetBootstrap.MenuScene);
        }
    }
}
