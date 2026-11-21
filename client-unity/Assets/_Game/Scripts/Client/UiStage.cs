using FallRush.Net;
using UnityEngine;

namespace FallRush.Client
{
    /// <summary>Menu / lobby-room backdrop: the sky gradient, a sun and the toon ambient (screens without a map).</summary>
    public static class UiStage
    {
        public static void Ensure(Camera cam)
        {
            var sky = Resources.Load<Material>("FallRush/Sky");
            if (sky != null)
            {
                RenderSettings.skybox = sky;
                if (cam != null)
                    cam.clearFlags = CameraClearFlags.Skybox;
            }

            if (Object.FindAnyObjectByType<Light>() == null)
            {
                var l = new GameObject("Sun").AddComponent<Light>();
                l.type = LightType.Directional;
                l.color = PaletteTokens.Get().sun;
                l.intensity = 1.2f;
                l.shadows = LightShadows.None;
                l.transform.rotation = Quaternion.Euler(40f, 150f, 0f);
                RenderSettings.sun = l;
            }

            WorldLook.OnMapLoaded();
        }
    }
}
