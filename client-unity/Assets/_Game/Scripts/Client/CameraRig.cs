using UnityEngine;

namespace FallRush.Client
{
    /// <summary>
    /// G4.10 follow-camera tunables in one place (client-only feel; render only). <see cref="Classic"/> = the camera through
    /// G4.8 (6.6 m orbit at 22.5°, look-at 0.4 m above the 0.8 m target). <see cref="Close"/> = lower and flatter at about the
    /// same distance (the classic camera already shows a Rushling at ≈ 14.6 %): the course ahead sits lower in the frame (Capsule Run's finish arch inside the spawn view) while a Rushling
    /// stays 12–15 % of the screen height (measured: <c>CameraShots</c>). Dev switch <c>?cam=classic|close</c> (WebGL) /
    /// <c>-fallrushCam</c> (desktop); the default is <see cref="Default"/>.
    /// </summary>
    public static class CameraRig
    {
        public enum Id
        {
            Classic = 0, Close = 1,
        }

        public struct Preset
        {
            public float Distance;      // orbit radius (m), × the Settings camera distance
            public float Elevation;     // orbit elevation (degrees)
            public float LookOffsetY;   // composer look-at offset above the follow target (m)
            public float Fov;           // vertical field of view (degrees)
        }

        /// <summary>The follow target sits this far above the feet (SandboxView / PracticeMode).</summary>
        public const float TargetHeight = 0.8f;

        public static readonly Preset Classic = new Preset { Distance = 6.6f, Elevation = 22.5f, LookOffsetY = 0.4f, Fov = 55f };
        public static readonly Preset Close = new Preset { Distance = 6.5f, Elevation = 15f, LookOffsetY = 0.75f, Fov = 55f };

        /// <summary>The start camera when the URL / command line names none (G4.10: close, see the proof).</summary>
        public const Id Default = Id.Close;

        private static bool s_Init;
        private static Id s_Current = Default;

        public static Id Current
        {
            get
            {
                if (!s_Init)
                {
                    s_Init = true;
                    s_Current = Parse(FallRush.Shared.NetTargets.GetQueryParam(Application.absoluteURL, "cam") ?? Arg("-fallrushCam")) ?? Default;
                }

                return s_Current;
            }
            set
            {
                s_Init = true;
                s_Current = value;
            }
        }

        public static Preset Active => Get(Current);

        public static Preset Get(Id id) => id == Id.Classic ? Classic : Close;

        public static Id? Parse(string s) => s == "classic" ? Id.Classic : s == "close" ? Id.Close : (Id?)null;

        public static string Name(Id id) => id == Id.Classic ? "classic" : "close";

        /// <summary>The camera pose of a preset with no damping or look-ahead (proof shots): orbit around the follow target.</summary>
        public static void Pose(Preset p, Vector3 feet, float yawDeg, out Vector3 eye, out Vector3 lookAt)
        {
            var target = feet + Vector3.up * TargetHeight;
            eye = target + Quaternion.Euler(p.Elevation, yawDeg, 0f) * new Vector3(0f, 0f, -p.Distance);
            lookAt = target + Vector3.up * p.LookOffsetY;
        }

        private static string Arg(string name)
        {
            var a = System.Environment.GetCommandLineArgs();
            for (var i = 0; i < a.Length - 1; i++)
                if (a[i] == name)
                    return a[i + 1].ToLowerInvariant();
            return null;
        }
    }
}
