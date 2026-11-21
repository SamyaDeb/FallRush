using UnityEngine;
using UnityEngine.EventSystems;

namespace FallRush.Client
{
    /// <summary>G3.1 button squash: pressed → (1.06, 0.88), released → springs back past 1 (doc 17 §5). Off with reduced motion.</summary>
    public sealed class UiPress : MonoBehaviour, IPointerDownHandler, IPointerUpHandler, IPointerExitHandler, IPointerEnterHandler
    {
        public RectTransform Target;
        private float _x = 1f, _v;
        private float _goal = 1f;

        public void OnPointerDown(PointerEventData e) => _goal = 0f;
        public void OnPointerUp(PointerEventData e) => _goal = 1f;
        public void OnPointerExit(PointerEventData e) => _goal = 1f;

        public void OnPointerEnter(PointerEventData e)
        {
            if (!GameSettings.ReducedMotion)
                _v += 2.5f; // a little wiggle on hover
        }

        private void Update()
        {
            if (Target == null)
                return;
            var dt = Mathf.Min(Time.unscaledDeltaTime, 0.05f);
            if (GameSettings.ReducedMotion)
            {
                _x = 1f;
                _v = 0f;
                Target.localScale = Vector3.one;
                return;
            }

            // Underdamped spring between the pressed pose (0) and rest (1).
            var a = -380f * (_x - _goal) - 18f * _v;
            _v += a * dt;
            _x += _v * dt;
            var squash = 1f - _x;
            Target.localScale = new Vector3(1f + 0.06f * squash, 1f - 0.12f * squash, 1f);
        }
    }
}
