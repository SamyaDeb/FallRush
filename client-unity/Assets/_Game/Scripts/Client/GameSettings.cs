using UnityEngine;

namespace FallRush.Client
{
    /// <summary>Player options (PlayerPrefs; per browser on WebGL): mouse sensitivity, invert Y, reduced motion.</summary>
    public static class GameSettings
    {
        public const float MinSensitivity = 0.03f;
        public const float MaxSensitivity = 0.40f;
        public const float DefaultSensitivity = 0.12f; // degrees per mouse count

        private const string KeySens = "fr.sens";
        private const string KeyInvert = "fr.invertY";
        private const string KeyReduced = "fr.reducedMotion";

        public static float Sensitivity
        {
            get => Mathf.Clamp(PlayerPrefs.GetFloat(KeySens, DefaultSensitivity), MinSensitivity, MaxSensitivity);
            set => PlayerPrefs.SetFloat(KeySens, Mathf.Clamp(value, MinSensitivity, MaxSensitivity));
        }

        public static bool InvertY
        {
            get => PlayerPrefs.GetInt(KeyInvert, 0) != 0;
            set => PlayerPrefs.SetInt(KeyInvert, value ? 1 : 0);
        }

        /// <summary>No camera shake and no FOV kick (doc 01 §13 accessibility).</summary>
        public static bool ReducedMotion
        {
            get => PlayerPrefs.GetInt(KeyReduced, 0) != 0;
            set => PlayerPrefs.SetInt(KeyReduced, value ? 1 : 0);
        }

        public static void Save() => PlayerPrefs.Save();
    }
}
