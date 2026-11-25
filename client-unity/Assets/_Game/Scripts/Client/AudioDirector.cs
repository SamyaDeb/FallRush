using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

namespace FallRush.Client
{
    /// <summary>
    /// G3.2 audio runtime (see <see cref="GameAudio"/>): one persistent object with pooled AudioSources: 2 music
    /// (crossfade), 16 SFX (round-robin, positional with a soft rolloff or 2D), 4 UI. Clips from
    /// <c>Resources/FallRush/Audio</c>. Client only (the FallRush.Client assembly is not in server builds).
    /// </summary>
    public sealed class AudioDirector : MonoBehaviour
    {
        private static AudioDirector s_Instance;
        private static bool s_Quitting;

        private readonly AudioSource[] _music = new AudioSource[2];
        private readonly AudioSource[] _sfx = new AudioSource[16];
        private readonly AudioSource[] _ui = new AudioSource[4];
        private AudioSource _own; // dizzy2: own hit cues (dizzy / wheee / goo), never recycled by world SFX
        private int _nextSfx, _nextUi, _musicIdx;
        private readonly Dictionary<string, AudioClip> _clips = new Dictionary<string, AudioClip>();
        private AudioClip[] _sfxClips, _uiClips, _chirps;
        private GameAudio.Music _want, _playing;
        private float _fade = 1f;
        private float _duckUntil, _duckDepth = 1f, _duck = 1f;
        private bool _unlocked;
        private float _victoryEnds;

        public static AudioDirector Get()
        {
            if (s_Instance != null || s_Quitting || !Application.isPlaying) // edit mode (tests, Editor shots): no audio
                return s_Instance;
            var go = new GameObject("AudioDirector");
            DontDestroyOnLoad(go);
            s_Instance = go.AddComponent<AudioDirector>();
            return s_Instance;
        }

        private void OnApplicationQuit() => s_Quitting = true;

        private void Awake()
        {
            for (var i = 0; i < _music.Length; i++)
            {
                _music[i] = gameObject.AddComponent<AudioSource>();
                _music[i].loop = true;
                _music[i].playOnAwake = false;
                _music[i].spatialBlend = 0f;
            }

            for (var i = 0; i < _sfx.Length; i++)
            {
                var go = new GameObject("Sfx" + i);
                go.transform.SetParent(transform, false);
                var s = go.AddComponent<AudioSource>();
                s.playOnAwake = false;
                s.rolloffMode = AudioRolloffMode.Linear;
                s.minDistance = 6f;
                s.maxDistance = 45f;
                s.dopplerLevel = 0f;
                _sfx[i] = s;
            }

            _own = gameObject.AddComponent<AudioSource>();
            _own.playOnAwake = false;
            _own.spatialBlend = 0f;
            _own.priority = 0; // highest: WebAudio voice limits never drop it

            for (var i = 0; i < _ui.Length; i++)
            {
                _ui[i] = gameObject.AddComponent<AudioSource>();
                _ui[i].playOnAwake = false;
                _ui[i].spatialBlend = 0f;
            }

            var names = System.Enum.GetNames(typeof(GameAudio.Sfx));
            _sfxClips = new AudioClip[names.Length];
            for (var i = 0; i < names.Length; i++)
                _sfxClips[i] = Load("sfx_" + names[i].ToLowerInvariant());
            var ui = System.Enum.GetNames(typeof(GameAudio.UiCue));
            _uiClips = new AudioClip[ui.Length];
            for (var i = 0; i < ui.Length; i++)
                _uiClips[i] = Load("ui_" + ui[i].ToLowerInvariant());
            _chirps = new AudioClip[4];
            for (var i = 0; i < _chirps.Length; i++)
                _chirps[i] = Load("chirp_" + i);
#if !UNITY_WEBGL || UNITY_EDITOR
            _unlocked = true;
#endif
        }

        private AudioClip Load(string name)
        {
            if (_clips.TryGetValue(name, out var c))
                return c;
            c = Resources.Load<AudioClip>("FallRush/Audio/" + name);
            _clips[name] = c;
            return c;
        }

        private float Bus(float slider) => slider * _duck;

        public void PlayUi(GameAudio.UiCue c)
        {
            if (!_unlocked)
                return;
            var clip = _uiClips[(int)c];
            if (clip == null)
                return;
            var s = _ui[_nextUi++ % _ui.Length];
            s.pitch = 1f;
            s.PlayOneShot(clip, GameSettings.SfxVolume * 0.8f);
        }

        public void PlaySfx(GameAudio.Sfx id, Vector3 at, float volume, float pitch, bool flat)
        {
            if (!_unlocked)
                return;
            var clip = _sfxClips[(int)id];
            if (clip == null)
                return;
            var s = _sfx[_nextSfx++ % _sfx.Length];
            s.transform.position = at;
            s.spatialBlend = flat ? 0f : 0.85f;
            s.pitch = pitch;
            s.volume = Bus(GameSettings.SfxVolume) * volume;
            s.clip = clip;
            s.Play();
        }

        public void PlayOwn(GameAudio.Sfx id, float volume, float pitch)
        {
            if (!_unlocked)
                return;
            var clip = _sfxClips[(int)id];
            if (clip == null)
                return;
            _own.pitch = pitch;
            _own.volume = Bus(GameSettings.SfxVolume) * volume;
            _own.clip = clip;
            _own.Play();
        }

        public void Chirp(int mood, Vector3 at)
        {
            if (!_unlocked || _chirps[0] == null)
                return;
            var clip = _chirps[Mathf.Abs(mood) % _chirps.Length];
            var s = _sfx[_nextSfx++ % _sfx.Length];
            s.transform.position = at;
            s.spatialBlend = 0.85f;
            s.pitch = 0.9f + Random.value * 0.35f;
            s.volume = Bus(GameSettings.SfxVolume) * 0.55f;
            s.clip = clip;
            s.Play();
        }

        public void SetMusic(GameAudio.Music m) => _want = m;

        public void Duck(float seconds, float depth)
        {
            _duckUntil = Mathf.Max(_duckUntil, Time.unscaledTime + seconds);
            _duckDepth = Mathf.Min(_duckDepth, depth);
        }

        private void Update()
        {
            if (!_unlocked)
            {
                // WebGL: unlock on the first user gesture (click or key); before it nothing plays (no audio errors).
                var m = Mouse.current;
                var k = Keyboard.current;
                if ((m != null && (m.leftButton.wasPressedThisFrame || m.rightButton.wasPressedThisFrame)) || (k != null && k.anyKey.wasPressedThisFrame))
                    _unlocked = true;
                else
                    return;
            }

            var dt = Time.unscaledDeltaTime;
            var duckTarget = Time.unscaledTime < _duckUntil ? _duckDepth : 1f;
            if (Time.unscaledTime >= _duckUntil)
                _duckDepth = 1f;
            _duck = Mathf.MoveTowards(_duck, duckTarget, dt * (duckTarget < _duck ? 6f : 1.5f));

            // Music: crossfade on change; the victory sting plays once, then the lobby loop returns.
            if (_want != _playing && _fade >= 1f)
            {
                _playing = _want;
                _musicIdx ^= 1;
                var next = _music[_musicIdx];
                var clip = _playing switch
                {
                    GameAudio.Music.Lobby => Load("music_lobby"),
                    GameAudio.Music.Race => Load("music_race"),
                    GameAudio.Music.Final => Load("music_final"),
                    GameAudio.Music.Victory => Load("music_victory"),
                    _ => null,
                };
                next.clip = clip;
                next.loop = _playing != GameAudio.Music.Victory;
                next.volume = 0f;
                if (clip != null)
                    next.Play();
                _victoryEnds = _playing == GameAudio.Music.Victory && clip != null ? Time.unscaledTime + clip.length : 0f;
                _fade = 0f;
            }

            _fade = Mathf.Min(1f, _fade + dt / 0.8f);
            var vol = Bus(GameSettings.MusicVolume) * 0.7f;
            _music[_musicIdx].volume = vol * _fade;
            var other = _music[_musicIdx ^ 1];
            other.volume = vol * (1f - _fade);
            if (_fade >= 1f && other.isPlaying)
                other.Stop();
        }

        public bool VictoryPlaying => Time.unscaledTime < _victoryEnds;
    }
}
