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
            Dizzy, Wheee, // 2026-11-24 dizzy hits: boing + cartoon birds (own hit), slide whistle (own knock-off fall)
        }

        public enum Music : byte { None, Lobby, Race, Final, Victory }

        public static void Ui(UiCue c) => AudioDirector.Get()?.PlayUi(c);

        /// <summary>Test hook: every SFX request (before the director, so it works without one).</summary>
        public static System.Action<Sfx> Requested;

        /// <summary>Test hook: every chirp request (the mood).</summary>
        public static System.Action<int> ChirpRequested;

        public static void Play(Sfx s, Vector3 at, float volume = 1f, float pitch = 1f)
        {
            Requested?.Invoke(s);
            AudioDirector.Get()?.PlaySfx(s, at, volume, pitch, false);
        }

        /// <summary>A non-positional SFX (your own events: qualify, countdown).</summary>
        public static void Play2D(Sfx s, float volume = 1f, float pitch = 1f)
        {
            Requested?.Invoke(s);
            AudioDirector.Get()?.PlaySfx(s, Vector3.zero, volume, pitch, true);
        }

        /// <summary>dizzy2: your own hit cue on a reserved 2D voice that world SFX never steal.</summary>
        public static void PlayOwn(Sfx s, float volume, float pitch)
        {
            Requested?.Invoke(s);
            AudioDirector.Get()?.PlayOwn(s, volume, pitch);
        }

        public static void SetMusic(Music m) => AudioDirector.Get()?.SetMusic(m);

        /// <summary>Ducks music and SFX under an important cue (countdown, elimination) for <paramref name="seconds"/>.</summary>
        public static void Duck(float seconds, float depth = 0.35f) => AudioDirector.Get()?.Duck(seconds, depth);

        /// <summary>A gibberish chirp for an expression (pitch-randomised; no voice lines).</summary>
        public static void Chirp(int mood, Vector3 at)
        {
            ChirpRequested?.Invoke(mood);
            AudioDirector.Get()?.Chirp(mood, at);
        }

        /// <summary>
        /// Dizzy hits (2026-11-24): only your own Rushling makes hit sounds, never other players or bots (and a spectator,
        /// who owns no slot, hears none). Flat 2D cues: they are about you, not a place in the world.
        /// </summary>
        public static class Hits
        {
            /// <summary>A hazard knocked a Rushling (stun started).</summary>
            public static void Stunned(bool own, Vector3 feet)
            {
                if (!own)
                    return;
                // boing + cartoon birds on your own voice (dizzy2: in a 12-player round the shared pool cut it off
                // under everyone's footsteps, so only the low start was heard; the buzzy "whoa" chirp is gone too)
                PlayOwn(Sfx.Dizzy, 1f, 0.96f + Random.value * 0.08f);
            }

            /// <summary>The Rushling got back up after the stun.</summary>
            public static void GotUp(bool own, Vector3 feet)
            {
                if (own)
                    Chirp(0, feet); // a cheerful "phew"
            }

            /// <summary>The Rushling left the course (fell out, or into goo).</summary>
            public static void KnockedOut(bool own, bool gooed)
            {
                if (own)
                    PlayOwn(gooed ? Sfx.Goo : Sfx.Wheee, 1f, 1f);
            }
        }
    }
}
