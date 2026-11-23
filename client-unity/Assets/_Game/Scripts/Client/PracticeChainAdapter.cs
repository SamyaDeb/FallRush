using FallRush.Shared;
using UnityEngine.InputSystem;

namespace FallRush.Client
{
    /// <summary>
    /// G3.6 today's adapter: the practice match's local seed and "● Server" (nothing is on chain). A DEV toggle cycles
    /// the four chip states for screenshots: F9 in the Editor / development builds, or <c>?chain=cycle</c>
    /// (<c>-fallrushChain cycle</c>) anywhere (it only changes what the chip shows). Recorded matches use the on-chain
    /// adapter (OnChainChainAdapter) while the Final runs on the rollup; the match's VRF deal link comes from the arena
    /// keeper (MatchHud.DealProvider).
    /// </summary>
    public sealed class PracticeChainAdapter : IChainAdapter
    {
        private static PracticeChainAdapter s_Instance;
        public static IChainAdapter Current => s_Instance ??= new PracticeChainAdapter();

        private byte[] _seed = new byte[32];
        private int _forced = -1;
        private readonly bool _cycle;
        private float _cycleAt;

        private PracticeChainAdapter()
        {
            var q = NetTargets.GetQueryParam(UnityEngine.Application.absoluteURL, "chain");
            var a = System.Environment.GetCommandLineArgs();
            for (var i = 0; i < a.Length - 1; i++)
                if (a[i] == "-fallrushChain")
                    q = a[i + 1];
            _cycle = q == "cycle";
        }

        public bool IsPractice => true;

        public ChainState State
        {
            get
            {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                var kb = Keyboard.current;
                if (kb != null && kb.f9Key.wasPressedThisFrame)
                    _forced = (_forced + 1) % 4;
#endif
                if (_cycle && UnityEngine.Time.unscaledTime >= _cycleAt)
                {
                    _cycleAt = UnityEngine.Time.unscaledTime + 2.5f;
                    _forced = (_forced + 1) % 4;
                }

                return _forced >= 0 ? (ChainState)_forced : ChainState.Server;
            }
        }

        public string Detail => "Practice match: not recorded on chain";

        public DealProof Deal => new DealProof
        {
            SeedHex = Sha256.Hex(_seed),
            Headline = "Practice deal (local seed)",
            Verified = false,
            VrfSignature = null, // practice: nothing on chain
            ExplorerUrl = null,
        };

        public ReceiptLinks Receipt => default; // practice: not recorded on chain

        public void OnMatchState(MatchStateMessage m)
        {
            if (m?.Seed != null && m.Seed.Length == 32)
                _seed = m.Seed;
        }
    }
}
