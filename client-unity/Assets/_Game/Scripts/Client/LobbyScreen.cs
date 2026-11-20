using FallRush.Net;
using FallRush.Shared;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace FallRush.Client
{
    /// <summary>Lobby: connection state, my client id, connected players, live RTT p50/p95, Reconnect.</summary>
    public sealed class LobbyScreen : MonoBehaviour
    {
        private const float RefreshSeconds = 0.2f;

        private Text _state;
        private Text _details;
        private Text _rtt;
        private Button _reconnect;
        private Button _enter;
        private bool _autoplay;
        private float _nextRefresh;

        private void Start()
        {
            var canvas = UiKit.CreateCanvas("LobbyCanvas");
            var panel = UiKit.Panel(canvas, new Vector2(760, 680), UiKit.CloudWhite);

            UiKit.Label(panel, "LOBBY", 56, UiKit.Grape, FontStyle.Bold, 72);
            _state = UiKit.Label(panel, string.Empty, 34, UiKit.Ink, FontStyle.Bold, 48);
            _details = UiKit.Label(panel, string.Empty, 22, UiKit.Ink, FontStyle.Normal, 96);
            _rtt = UiKit.Label(panel, string.Empty, 30, UiKit.Magenta, FontStyle.Bold, 44);
            _enter = UiKit.Button(panel, "Enter Sandbox (M2)", UiKit.Mint, OnEnter);
            _reconnect = UiKit.Button(panel, "Reconnect", UiKit.SunYellow, OnReconnect);
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
                    _state.text = "Connecting…";
                    _state.color = UiKit.Sky;
                    break;
                case ClientState.Connected:
                    _state.text = "Connected";
                    _state.color = UiKit.Mint;
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
                _details.text = $"Server: {boot.Target.Url}\nMy client id: {nm.LocalClientId}\nPlayers connected: {(players > 0 ? players.ToString() : "…")} / {Protocol.MaxPlayers}";
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
                _rtt.text = $"RTT p50 {ping.P50Ms:F0} ms · p95 {ping.P95Ms:F0} ms  (n={ping.SampleCount})";
            else
                _rtt.text = boot.State == ClientState.Connected ? "RTT: measuring…" : string.Empty;

            _reconnect.gameObject.SetActive(boot.State == ClientState.Disconnected);
            _enter.gameObject.SetActive(boot.State == ClientState.Connected);
            if (_autoplay && boot.State == ClientState.Connected)
            {
                _autoplay = false;
                OnEnter();
            }
        }

        private void OnReconnect() => NetBootstrap.Instance?.Reconnect();

        private static void OnEnter() => SceneManager.LoadScene(NetBootstrap.SandboxScene);

        private void OnBack()
        {
            NetBootstrap.Instance?.Disconnect();
            SceneManager.LoadScene(NetBootstrap.MenuScene);
        }
    }
}
