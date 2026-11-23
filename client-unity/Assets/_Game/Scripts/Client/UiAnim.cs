using UnityEngine;

namespace FallRush.Client
{
    /// <summary>
    /// P7 A1.1: the per-target player behind <see cref="UiMotion"/>: enter / exit (scale + alpha through a CanvasGroup),
    /// bump, slide and shake. Positional motion is an offset on top of the rest position captured when it starts, and is
    /// re-applied in LateUpdate, so layout rebuilds don't fight it. Unscaled time; reduced motion = fades only.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class UiAnim : MonoBehaviour
    {
        private RectTransform _rt;
        private CanvasGroup _cg;
        private float _enterT = -1f, _exitT = -1f, _bumpT = -1f, _slideT = -1f, _shakeT = -1f, _delay;
        private Vector2 _slideFrom, _offset;
        private bool _slideFade;
        private Vector3 _baseScale = Vector3.one;

        /// <summary>The state the caller last asked for (true while entering / shown, false while exiting / hidden).</summary>
        public bool Wanted { get; private set; }

        private void Awake()
        {
            if (_rt != null)
                return; // already initialised by a call made while inactive (Awake runs again on activation)
            _rt = (RectTransform)transform;
            _cg = GetComponent<CanvasGroup>();
            if (_cg == null)
                _cg = gameObject.AddComponent<CanvasGroup>();
            _baseScale = _rt.localScale;
            Wanted = gameObject.activeSelf;
        }

        private static float Now => Time.unscaledTime;

        public void Enter(float delay)
        {
            if (_rt == null)
                Awake();
            Wanted = true;
            _exitT = -1f;
            _enterT = Now;
            _delay = delay;
            _cg.alpha = 0f;
            if (!GameSettings.ReducedMotion)
                _rt.localScale = _baseScale * UiMotion.EnterFrom;
            _cg.interactable = _cg.blocksRaycasts = true;
            if (!gameObject.activeSelf)
                gameObject.SetActive(true);
        }

        public void Exit()
        {
            if (_rt == null)
                Awake();
            if (!Wanted && _exitT >= 0f)
                return; // already exiting: a repeated Hide must not restart the exit (it would never finish)
            Wanted = false;
            _enterT = -1f;
            _exitT = Now;
            _cg.interactable = _cg.blocksRaycasts = false;
        }

        public void Bump()
        {
            if (_rt == null)
                Awake();
            if (!GameSettings.ReducedMotion)
                _bumpT = Now;
        }

        public void Shake()
        {
            if (_rt == null)
                Awake();
            if (!GameSettings.ReducedMotion)
                _shakeT = Now;
        }

        public void Slide(Vector2 from, bool fade = true)
        {
            if (_rt == null)
                Awake();
            _slideFade = fade;
            if (GameSettings.ReducedMotion)
            {
                if (fade)
                    Enter(0f);
                return;
            }

            _slideFrom = from;
            _slideT = Now;
            if (fade)
                Enter(0f);
        }

        private void Update()
        {
            var now = Now;
            var reduced = GameSettings.ReducedMotion;
            var scale = 1f;
            if (_enterT >= 0f)
            {
                var t = (now - _enterT - _delay) / UiMotion.EnterSeconds;
                if (t < 0f)
                {
                    _cg.alpha = 0f;
                    scale = reduced ? 1f : UiMotion.EnterFrom;
                }
                else if (t >= 1f)
                {
                    _cg.alpha = 1f;
                    _enterT = -1f;
                }
                else
                {
                    _cg.alpha = Mathf.Clamp01(t * 1.6f);
                    scale = reduced ? 1f : UiMotion.EnterScale(t);
                }
            }
            else if (_exitT >= 0f)
            {
                var t = (now - _exitT) / UiMotion.ExitSeconds;
                if (t >= 1f)
                {
                    _exitT = -1f;
                    _cg.alpha = 1f;
                    _rt.localScale = _baseScale;
                    _offset = Vector2.zero;
                    gameObject.SetActive(false);
                    return;
                }

                _cg.alpha = 1f - t;
                scale = reduced ? 1f : Mathf.Lerp(1f, UiMotion.ExitTo, t);
            }

            if (_bumpT >= 0f)
            {
                var t = (now - _bumpT) / UiMotion.BumpSeconds;
                if (t >= 1f || reduced)
                    _bumpT = -1f;
                else
                    scale *= UiMotion.BumpScale(t);
            }

            _rt.localScale = _baseScale * scale;
        }

        private void LateUpdate()
        {
            var now = Now;
            var off = Vector2.zero;
            if (_slideT >= 0f)
            {
                var t = (now - _slideT) / UiMotion.SlideSeconds;
                if (t >= 1f)
                    _slideT = -1f;
                else
                    off += _slideFrom * (1f - UiMotion.EaseOut(t));
            }

            if (_shakeT >= 0f)
            {
                var t = (now - _shakeT) / UiMotion.ShakeSeconds;
                if (t >= 1f)
                    _shakeT = -1f;
                else
                    off.x += UiMotion.ShakeOffset(t);
            }

            if (off == _offset)
                return;
            // remove the last offset, apply the new one (the rest position stays whatever layout / code set)
            _rt.anchoredPosition += off - _offset;
            _offset = off;
        }

        private void OnDisable()
        {
            if (_rt == null)
                return;
            _rt.anchoredPosition -= _offset;
            _offset = Vector2.zero;
            _slideT = _shakeT = _bumpT = -1f;
            _rt.localScale = _baseScale;
            if (_cg != null && _exitT < 0f && _enterT < 0f)
                _cg.alpha = 1f;
        }
    }
}
