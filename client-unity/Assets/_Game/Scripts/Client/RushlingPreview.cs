using FallRush.Net;
using FallRush.Shared;
using UnityEngine;

namespace FallRush.Client
{
    /// <summary>
    /// G2.2 / G3.5 "My Rushling" preview on the menu: a live Bolt-style Rushling idling and slowly turning in front of the
    /// menu camera (sky gradient, one sun). Changing the preset pops a little cheer emote.
    /// </summary>
    public sealed class RushlingPreview : MonoBehaviour
    {
        private RushlingVisuals _v;
        private int _preset = -1;
        private float _yaw = 200f;
        private Vector3 _at;

        public static RushlingPreview Create(Camera cam, Vector3 viewport)
        {
            UiStage.Ensure(cam);
            var go = new GameObject("RushlingPreview");
            var p = go.AddComponent<RushlingPreview>();
            var ray = cam.ViewportPointToRay(viewport);
            p._at = ray.origin + ray.direction * 3.1f - Vector3.up * 0.5f;
            var root = new GameObject("Rushling").transform;
            root.SetParent(go.transform, false);
            p._v = root.gameObject.AddComponent<RushlingVisuals>();
            p._v.Build(0, GameSettings.CosmeticPreset, Resources.Load<Material>("FallRush/Character"), null);
            p._v.SetVisible(true);
            return p;
        }

        public void Show(int preset)
        {
            if (preset == _preset)
                return;
            var first = _preset < 0;
            _preset = preset;
            _v.SetPreset(preset);
            if (!first)
                _v.PlayEmote(3);
        }

        private void Update()
        {
            Show(GameSettings.CosmeticPreset);
            var dt = Time.unscaledDeltaTime;
            if (!GameSettings.ReducedMotion)
                _yaw += dt * 18f;
            _v.Drive(_at, Vector3.zero, MotorState.Grounded, (byte)Mathf.RoundToInt(Mathf.Repeat(_yaw, 360f) / 360f * 256f) , 0, dt);
        }
    }
}
