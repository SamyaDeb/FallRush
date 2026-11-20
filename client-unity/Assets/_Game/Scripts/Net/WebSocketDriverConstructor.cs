using Unity.Netcode.Transports.UTP;
using Unity.Networking.Transport;
using Unity.Networking.Transport.Utilities;

namespace FallRush.Net
{
    /// <summary>
    /// Builds NGO's UTP driver exactly like <see cref="UnityTransport.CreateDriver"/> (WebSocket path, NGO 2.14.0 source),
    /// except for the reliable pipeline's resend timer (R12, M2b).
    /// <para>Over WebSockets every byte already rides TCP, so UTP's reliable-stage retransmits are redundant. They are
    /// harmful when a browser tab is hidden: the client stops acking, and NGO's settings (window 64, resend ≤ 500 ms) make the
    /// server resend the whole window twice a second — measured 16.7 KB/s into a stalled client, whose 128/1024-packet
    /// receive queue then overflows on wake-up and UTP closes the connection with ProtocolError. A 10–30 s resend
    /// window keeps reliability (a packet UTP itself drops still gets resent) without the flood.</para>
    /// <para>Installed via <see cref="UnityTransport.s_DriverConstructor"/> (NGO's documented hook) by <see cref="NetBootstrap"/>.</para>
    /// </summary>
    public sealed class WebSocketDriverConstructor : INetworkStreamDriverConstructor
    {
        public const int ReliableWindow = 64;           // NGO's value
        public const int MinimumResendMs = 10000;       // NGO: UTP default 64 (1 s still overflowed after a 60 s stall)
        public const int MaximumResendMs = 30000;       // NGO: 500

        public void CreateDriver(
            UnityTransport transport,
            out NetworkDriver driver,
            out NetworkPipeline unreliableFragmentedPipeline,
            out NetworkPipeline unreliableSequencedFragmentedPipeline,
            out NetworkPipeline reliableSequencedPipeline)
        {
            var settings = transport.GetDefaultNetworkSettings();
            settings.WithReliableStageParameters(windowSize: ReliableWindow, minimumResendTime: MinimumResendMs, maximumResendTime: MaximumResendMs);
            driver = NetworkDriver.Create(new WebSocketNetworkInterface(), settings);

            transport.GetDefaultPipelineConfigurations(
                ref driver,
                out var unreliableFragmentedStages,
                out var unreliableSequencedFragmentedStages,
                out var reliableSequencedStages);

            unreliableFragmentedPipeline = driver.CreatePipeline(unreliableFragmentedStages);
            unreliableSequencedFragmentedPipeline = driver.CreatePipeline(unreliableSequencedFragmentedStages);
            reliableSequencedPipeline = driver.CreatePipeline(reliableSequencedStages);
        }
    }
}
