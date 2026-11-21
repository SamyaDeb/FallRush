using UnityEngine;

namespace FallRush.Client
{
    /// <summary>
    /// G3.2 audio facade (doc 17 §6). All clips are our own, synthesised by <c>tools/audio/synth.py</c> (no third-party
    /// audio). Three buses in code: Music, SFX, UI (no AudioMixer asset: it cannot be created from code and Unity's Web
    /// platform audio is a limited subset, so volumes, ducking and pooling are done on AudioSources, which works on
    /// every platform). Volumes = Settings master × bus slider × duck. Nothing plays until the first click or key press
    /// on WebGL (browsers keep audio locked until a user gesture). Pooled sources, no allocations per call.
    /// </summary>
    public static class GameAudio
    {
        public enum UiCue : byte { Click, Pop, Crank, Coin, Tick, Whoosh }
        public enum Sfx : byte
        {
            Step, Jump, Land, Dive, Slide, Stumble, Bonk, Mallet, Spinner, Goo, Wobble, TileDrop, Star, StarGold, Checkpoint,
            Qualify, Eliminated, Count, Rush, Emote, Spring, Claw, CapsulePop, Confetti,
        }

        public enum Music : byte { None, Lobby, Race, Final, Victory }

        public static void Ui(UiCue c) => AudioDirector.Get()?.PlayUi(c);

        public static void Play(Sfx s, Vector3 at, float volume = 1f, float pitch = 1f) => AudioDirector.Get()?.PlaySfx(s, at, volume, pitch, false);

        /// <summary>A non-positional SFX (your own events: qualify, countdown).</summary>
        public static void Play2D(Sfx s, float volume = 1f, float pitch = 1f) => AudioDirector.Get()?.PlaySfx(s, Vector3.zero, volume, pitch, true);

        public static void SetMusic(Music m) => AudioDirector.Get()?.SetMusic(m);

        /// <summary>Ducks music and SFX under an important cue (countdown, elimination) for <paramref name="seconds"/>.</summary>
        public static void Duck(float seconds, float depth = 0.35f) => AudioDirector.Get()?.Duck(seconds, depth);

        /// <summary>A gibberish chirp for an expression (pitch-randomised; no voice lines).</summary>
        public static void Chirp(int mood, Vector3 at) => AudioDirector.Get()?.Chirp(mood, at);
    }
}
