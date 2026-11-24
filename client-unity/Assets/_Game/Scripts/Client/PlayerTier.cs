using UnityEngine;

namespace FallRush.Client
{
    /// <summary>
    /// P7-UX (OC-47): the player's skill tier for matchmaking and the bots' mix. Score = crowns × 10 + Finals reached × 4 +
    /// rounds survived, from this browser's match history (a guest's burner wallet lives in the same browser, so it is the
    /// same identity). Rookie &lt; 15 ≤ Contender &lt; 60 ≤ Champion (thresholds UNVERIFIED: tune after the playtests).
    /// The tier only shapes grouping and bot difficulty, so a forged one gains nothing.
    /// </summary>
    public static class PlayerTier
    {
        public static readonly string[] Names = { "Rookie", "Contender", "Champion" };
        public const int ContenderAt = 15, ChampionAt = 60;

        private const string KCrowns = "fr.tier.crowns", KFinals = "fr.tier.finals", KRounds = "fr.tier.rounds", KMatches = "fr.tier.matches", KLast = "fr.tier.last";

        public static int Crowns => PlayerPrefs.GetInt(KCrowns, 0);
        public static int Finals => PlayerPrefs.GetInt(KFinals, 0);
        public static int Rounds => PlayerPrefs.GetInt(KRounds, 0);
        public static int Matches => PlayerPrefs.GetInt(KMatches, 0);

        public static int ScoreOf(int crowns, int finals, int rounds) => crowns * 10 + finals * 4 + rounds;

        public static byte TierOf(int score) => (byte)(score >= ChampionAt ? 2 : score >= ContenderAt ? 1 : 0);

        public static int Score => ScoreOf(Crowns, Finals, Rounds);

        public static byte Tier => TierOf(Score);

        public static string Name => Names[Tier];

        /// <summary>Points to the next tier (0 at Champion).</summary>
        public static int ToNext => Tier == 0 ? ContenderAt - Score : Tier == 1 ? ChampionAt - Score : 0;

        /// <summary>Records one finished match once (by its id).</summary>
        public static void Record(string matchId, int roundsSurvived, bool reachedFinal, bool crown)
        {
            if (string.IsNullOrEmpty(matchId) || PlayerPrefs.GetString(KLast, string.Empty) == matchId)
                return;
            PlayerPrefs.SetString(KLast, matchId);
            PlayerPrefs.SetInt(KMatches, Matches + 1);
            PlayerPrefs.SetInt(KRounds, Rounds + Mathf.Max(0, roundsSurvived));
            if (reachedFinal)
                PlayerPrefs.SetInt(KFinals, Finals + 1);
            if (crown)
                PlayerPrefs.SetInt(KCrowns, Crowns + 1);
            PlayerPrefs.Save();
        }
    }
}
