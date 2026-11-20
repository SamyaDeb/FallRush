using UnityEngine;

namespace FallRush.Net
{
    /// <summary>
    /// Client connection defaults. Resolution order (see <see cref="FallRush.Shared.NetTargets.ResolveServerTarget"/>):
    /// <c>?server=</c> on the page URL → <see cref="defaultServerUrl"/> → <c>ws://127.0.0.1:7777</c>.
    /// </summary>
    [CreateAssetMenu(menuName = "Fall Rush/Net Client Config", fileName = "NetClientConfig")]
    public sealed class NetClientConfig : ScriptableObject
    {
        [Tooltip("ws://host:port or wss://host[:port]. Empty = dev default ws://127.0.0.1:7777.")]
        public string defaultServerUrl = string.Empty;

        [Tooltip("Cosmetic preset sent in hello (placeholder until cosmetics exist).")]
        public byte cosmeticPreset;

        [Tooltip("UTP connect attempt timeout (ms).")]
        public int connectTimeoutMs = 1000;

        [Tooltip("UTP connect attempts before giving up.")]
        public int maxConnectAttempts = 10;
    }
}
