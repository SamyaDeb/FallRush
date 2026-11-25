using UnityEngine;

namespace FallRush.OnChain
{
    /// <summary>harden #13: a one-line notice that outlives the on-chain session (e.g. "could not join on-chain, watching
    /// on /live" after the session ended). IMGUI, as the session's own banners.</summary>
    public sealed class OnChainNotice : MonoBehaviour
    {
        private static OnChainNotice _i;
        private string _text;
        private float _until;

        public static void Show(string text, float seconds = 12f)
        {
            if (_i == null)
            {
                var go = new GameObject("OnChainNotice");
                DontDestroyOnLoad(go);
                _i = go.AddComponent<OnChainNotice>();
            }

            _i._text = text;
            _i._until = Time.unscaledTime + seconds;
        }

        private void OnGUI()
        {
            if (string.IsNullOrEmpty(_text) || Time.unscaledTime > _until)
                return;
            GUI.color = new Color(1f, 0.75f, 0.2f);
            GUI.Label(new Rect(Screen.width * 0.5f - 220, 96, 440, 24), _text);
            GUI.color = Color.white;
        }
    }
}
