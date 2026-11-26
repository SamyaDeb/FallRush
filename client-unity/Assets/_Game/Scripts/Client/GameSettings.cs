using UnityEngine;

namespace FallRush.Client
{
    /// <summary>
    /// Player options, saved locally (PlayerPrefs; per browser on WebGL): mouse sensitivity, invert Y, FOV, camera distance,
    /// reduced motion, master / music / SFX volume (the music and SFX sliders are wired for G3's audio), nameplates on/off,
    /// and the display name. Values are cached in memory (read every frame by the visuals; PlayerPrefs is only touched on set).
    /// </summary>
    public static class GameSettings
    {
        public const float MinSensitivity = 0.03f;
        public const float MaxSensitivity = 0.40f;
        public const float DefaultSensitivity = 0.12f; // degrees per mouse count
        public const float MinFovOffset = -10f, MaxFovOffset = 15f;
        public const float MinCamDistance = 0.8f, MaxCamDistance = 1.4f;
        public const int MaxNameChars = 16;
        public const float DefaultFovOffset = 0f, DefaultCamDistance = 1f;
        // 2026-11-27 (user: music and in-game sound too loud by default): quieter out of the box. Was 0.8 / 0.7 / 0.8.
        public const float DefaultMaster = 0.7f, DefaultMusic = 0.4f, DefaultSfx = 0.6f;
        private const int AudioDefaultsVersion = 2;

        private const string KeySens = "fr.sens";
        private const string KeyInvert = "fr.invertY";
        private const string KeyReduced = "fr.reducedMotion";
        private const string KeyFov = "fr.fovOffset";
        private const string KeyCamDist = "fr.camDistance";
        private const string KeyMaster = "fr.volMaster";
        private const string KeyMusic = "fr.volMusic";
        private const string KeySfx = "fr.volSfx";
        private const string KeyPlates = "fr.nameplates";
        private const string KeyName = "fr.name";
        private const string KeyGfx = "fr.graphicsHigh";
        private const string KeyPreset = "fr.preset";
        private const string KeyAudioVer = "fr.audioDefaultsVer";

        private static bool s_Loaded;
        private static float s_Sens, s_Fov, s_CamDist, s_Master, s_Music, s_Sfx;
        private static bool s_Invert, s_Reduced, s_Plates, s_GfxHigh;
        private static int s_Preset;
        private static string s_Name;

        private static void Load()
        {
            if (s_Loaded)
                return;
            s_Loaded = true;
            // one-time move to the quieter defaults: volumes saved under the old, louder defaults are dropped once
            if (PlayerPrefs.GetInt(KeyAudioVer, 0) < AudioDefaultsVersion)
            {
                PlayerPrefs.DeleteKey(KeyMaster);
                PlayerPrefs.DeleteKey(KeyMusic);
                PlayerPrefs.DeleteKey(KeySfx);
                PlayerPrefs.SetInt(KeyAudioVer, AudioDefaultsVersion);
                PlayerPrefs.Save();
            }

            s_Sens = Mathf.Clamp(PlayerPrefs.GetFloat(KeySens, DefaultSensitivity), MinSensitivity, MaxSensitivity);
            s_Invert = PlayerPrefs.GetInt(KeyInvert, 0) != 0;
            s_Reduced = PlayerPrefs.GetInt(KeyReduced, 0) != 0;
            s_Fov = Mathf.Clamp(PlayerPrefs.GetFloat(KeyFov, DefaultFovOffset), MinFovOffset, MaxFovOffset);
            s_CamDist = Mathf.Clamp(PlayerPrefs.GetFloat(KeyCamDist, DefaultCamDistance), MinCamDistance, MaxCamDistance);
            s_Master = Mathf.Clamp01(PlayerPrefs.GetFloat(KeyMaster, DefaultMaster));
            s_Music = Mathf.Clamp01(PlayerPrefs.GetFloat(KeyMusic, DefaultMusic));
            s_Sfx = Mathf.Clamp01(PlayerPrefs.GetFloat(KeySfx, DefaultSfx));
            s_Plates = PlayerPrefs.GetInt(KeyPlates, 1) != 0;
            s_Name = CleanName(PlayerPrefs.GetString(KeyName, string.Empty));
            s_GfxHigh = PlayerPrefs.GetInt(KeyGfx, 1) != 0;
            s_Preset = Mathf.Clamp(PlayerPrefs.GetInt(KeyPreset, 0), 0, 255);
            AudioListener.volume = s_Master;
        }

        public static float Sensitivity
        {
            get { Load(); return s_Sens; }
            set { Load(); s_Sens = Mathf.Clamp(value, MinSensitivity, MaxSensitivity); PlayerPrefs.SetFloat(KeySens, s_Sens); }
        }

        public static bool InvertY
        {
            get { Load(); return s_Invert; }
            set { Load(); s_Invert = value; PlayerPrefs.SetInt(KeyInvert, value ? 1 : 0); }
        }

        /// <summary>No camera shake, no FOV kick, smaller hops and no tumble roll (doc 01 §13 accessibility).</summary>
        public static bool ReducedMotion
        {
            get { Load(); return s_Reduced; }
            set { Load(); s_Reduced = value; PlayerPrefs.SetInt(KeyReduced, value ? 1 : 0); }
        }

        /// <summary>Added to the camera's base FOV (degrees).</summary>
        public static float FovOffset
        {
            get { Load(); return s_Fov; }
            set { Load(); s_Fov = Mathf.Clamp(value, MinFovOffset, MaxFovOffset); PlayerPrefs.SetFloat(KeyFov, s_Fov); }
        }

        /// <summary>Camera orbit distance multiplier (1 = default).</summary>
        public static float CameraDistance
        {
            get { Load(); return s_CamDist; }
            set { Load(); s_CamDist = Mathf.Clamp(value, MinCamDistance, MaxCamDistance); PlayerPrefs.SetFloat(KeyCamDist, s_CamDist); }
        }

        public static float MasterVolume
        {
            get { Load(); return s_Master; }
            set { Load(); s_Master = Mathf.Clamp01(value); PlayerPrefs.SetFloat(KeyMaster, s_Master); AudioListener.volume = s_Master; }
        }

        /// <summary>Music bus volume.</summary>
        public static float MusicVolume
        {
            get { Load(); return s_Music; }
            set { Load(); s_Music = Mathf.Clamp01(value); PlayerPrefs.SetFloat(KeyMusic, s_Music); }
        }

        /// <summary>SFX bus volume (world, UI and own hit cues).</summary>
        public static float SfxVolume
        {
            get { Load(); return s_Sfx; }
            set { Load(); s_Sfx = Mathf.Clamp01(value); PlayerPrefs.SetFloat(KeySfx, s_Sfx); }
        }

        /// <summary>G2.1 Graphics: High (post-processing, full render scale) or Low (no post, 0.8 render scale).</summary>
        public static bool GraphicsHigh
        {
            get { Load(); return s_GfxHigh; }
            set { Load(); s_GfxHigh = value; PlayerPrefs.SetInt(KeyGfx, value ? 1 : 0); }
        }

        /// <summary>G2.2 "My Rushling" cosmetic preset id (sent in hello; no stats).</summary>
        public static int CosmeticPreset
        {
            get { Load(); return s_Preset; }
            set { Load(); s_Preset = Mathf.Clamp(value, 0, 255); PlayerPrefs.SetInt(KeyPreset, s_Preset); }
        }

        public static bool ShowNameplates
        {
            get { Load(); return s_Plates; }
            set { Load(); s_Plates = value; PlayerPrefs.SetInt(KeyPlates, value ? 1 : 0); }
        }

        /// <summary>The display name sent in <c>hello</c> (empty = the server picks "Player N"). URL <c>?name=</c> still wins.</summary>
        public static string PlayerName
        {
            get { Load(); return s_Name; }
            set { Load(); s_Name = CleanName(value); PlayerPrefs.SetString(KeyName, s_Name); }
        }

        /// <summary>Trims, drops control characters, caps at <see cref="MaxNameChars"/>.</summary>
        public static string CleanName(string s)
        {
            if (string.IsNullOrEmpty(s))
                return string.Empty;
            var sb = new System.Text.StringBuilder(MaxNameChars);
            foreach (var ch in s.Trim())
            {
                if (char.IsControl(ch) || ch == '<' || ch == '>')
                    continue;
                if (sb.Length >= MaxNameChars)
                    break;
                sb.Append(ch);
            }

            return sb.ToString().Trim();
        }

        /// <summary>Settings panel "Reset defaults": controls, camera, display and audio (not the name or Rushling).</summary>
        public static void ResetToDefaults()
        {
            Sensitivity = DefaultSensitivity;
            InvertY = false;
            FovOffset = DefaultFovOffset;
            CameraDistance = DefaultCamDistance;
            ReducedMotion = false;
            ShowNameplates = true;
            GraphicsHigh = true;
            MasterVolume = DefaultMaster;
            MusicVolume = DefaultMusic;
            SfxVolume = DefaultSfx;
            Save();
        }

        public static void Save() => PlayerPrefs.Save();
    }
}
