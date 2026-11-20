using System;

namespace FallRush.Shared
{
    /// <summary>
    /// Qualify counts per round from the player count P at roster lock (bots included), GDD §4.1 / <c>ruleset_v1.json</c>.
    /// Bit-identical port of <c>packages/shared/src/targets.ts</c> and the program's <c>round_targets</c> (integer ceil:
    /// <c>ceil(a · pct / 100) = (a · pct + 99) / 100</c>). Checked against TS vectors for P = 4…20.
    /// The only place qualify counts come from; clients receive them from the server.
    /// </summary>
    public static class RoundTargets
    {
        public const int MinPlayers = 4, MaxPlayers = 20;

        public static byte[] For(int p)
        {
            if (p < MinPlayers || p > MaxPlayers)
                throw new ArgumentOutOfRangeException(nameof(p), $"player count out of range: {p}");
            var r1 = (p * 66 + 99) / 100;
            var r2 = (r1 * 62 + 99) / 100;
            var r3 = Math.Min(3, r2 - 1);
            return new[] { (byte)r1, (byte)r2, (byte)r3, (byte)1 };
        }
    }
}
