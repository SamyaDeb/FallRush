using System.Text;
using FallRush.Net;
using FallRush.Shared;
using UnityEngine;

namespace FallRush.Client
{
    /// <summary>
    /// dizzy2 diagnostics (2026-11-25, user: "after a hit the Rushling is gone, then reappears" + "a creepy vibration"):
    /// only with <c>?debughits=1</c> in the page URL. A small box at the top shows, for your own Rushling, why it is
    /// hidden right now (no sample / respawning / capsule / camera inside it), the last hidden episode (reason, length),
    /// and the last sounds played (names + age). Visual only; normal players never see it.
    /// </summary>
    public sealed class HitDebugOverlay : MonoBehaviour
    {
        public static bool Enabled { get; private set; }
        private static HitDebugOverlay s_Instance;

        private const int SfxN = 10;
        private readonly string[] _sfx = new string[SfxN];
        private readonly float[] _sfxAt = new float[SfxN];
        private int _sfxI;

        private string _now = "-", _last = "none yet";
        private float _hiddenSince = -1f;
        private string _hiddenWhy;
        private float _camDist, _minCamDist = 99f;
        private int _hits;

        public static void Init()
        {
            Enabled = NetTargets.GetQueryParam(Application.absoluteURL, "debughits") == "1";
            if (!Enabled || s_Instance != null)
                return;
            var go = new GameObject("HitDebugOverlay");
            DontDestroyOnLoad(go);
            s_Instance = go.AddComponent<HitDebugOverlay>();
            GameAudio.Requested += s => s_Instance.Sfx(s.ToString());
            GameAudio.ChirpRequested += m => s_Instance.Sfx("chirp" + m);
        }

        private void Sfx(string name)
        {
            _sfx[_sfxI % SfxN] = name;
            _sfxAt[_sfxI % SfxN] = Time.unscaledTime;
            _sfxI++;
        }

        /// <summary>Once per frame for your own slot (from SandboxView).</summary>
        public static void Own(bool sampled, byte flags, bool capsuled, byte stun, ClientSim cs, Vector3 feet, bool stunStarted)
        {
            if (!Enabled || s_Instance == null)
                return;
            s_Instance.Frame(sampled, flags, capsuled, stun, cs, feet, stunStarted);
        }

        private void Frame(bool sampled, byte flags, bool capsuled, byte stun, ClientSim cs, Vector3 feet, bool stunStarted)
        {
            if (stunStarted)
                _hits++;
            var cam = Camera.main;
            _camDist = cam != null && sampled ? Vector3.Distance(cam.transform.position, feet + Vector3.up * 0.6f) : -1f;
            if (_camDist >= 0f && _camDist < _minCamDist)
                _minCamDist = _camDist;
            string why = null;
            if (!sampled)
            {
                var pred = cs.Prediction;
                why = "NO SAMPLE (pred " + (pred == null ? "null" : pred.HasState ? "ok" : "noState") + ", active " + cs.Active + ", outOfSim " + cs.OwnOutOfSim +
                      ", feed " + (cs.Feed == null ? "none" : cs.Feed.OverridesServer ? "OVERRIDES" : "idle") + ")";
            }
            else if ((flags & MotorState.Respawning) != 0)
                why = "RESPAWNING flag (phase " + cs.Phase + ")";
            else if (capsuled)
                why = "CAPSULE intro";
            else if (_camDist >= 0f && _camDist < 0.9f)
                why = "CAMERA INSIDE (" + _camDist.ToString("F2") + " m)";

            _now = why ?? "visible";
            if (why != null && _hiddenSince < 0f)
            {
                _hiddenSince = Time.unscaledTime;
                _hiddenWhy = why;
            }
            else if (why == null && _hiddenSince >= 0f)
            {
                _last = _hiddenWhy + " for " + ((Time.unscaledTime - _hiddenSince) * 1000f).ToString("F0") + " ms, " + Time.unscaledTime.ToString("F1") + " s";
                Debug.Log("[debughits] hidden: " + _last);
                _hiddenSince = -1f;
            }
        }

        private void OnGUI()
        {
            var sb = new StringBuilder();
            sb.Append("debughits  hits ").Append(_hits).Append("  cam ").Append(_camDist.ToString("F2")).Append(" m (min ").Append(_minCamDist.ToString("F2")).Append(")\n");
            sb.Append("now: ").Append(_now).Append('\n');
            sb.Append("last hidden: ").Append(_last).Append('\n');
            sb.Append("sounds (newest first):");
            for (var k = 1; k <= SfxN && k <= _sfxI; k++)
            {
                var j = (_sfxI - k) % SfxN;
                sb.Append(' ').Append(_sfx[j]).Append('@').Append(((Time.unscaledTime - _sfxAt[j]) * 1000f).ToString("F0")).Append("ms");
            }

            var w = Mathf.Min(Screen.width - 20, 900);
            var style = new GUIStyle(GUI.skin.box) { alignment = TextAnchor.UpperLeft, fontSize = 14, wordWrap = true };
            style.normal.textColor = Color.white;
            GUI.Box(new Rect((Screen.width - w) / 2f, 8, w, 96), sb.ToString(), style);
        }
    }
}
