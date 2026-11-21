using FallRush.Shared;
using UnityEngine;

namespace FallRush.Client
{
    /// <summary>
    /// G4.8z: a scripted hazard knock for render checks (the stun test and <c>StunShots</c>): the knock profile's push and
    /// lift as a simple ballistic hop with ground friction, at 30 Hz, the stun ticks counting down from the hit, then 20 ticks
    /// of standing. Client-only and offline: it is not the sim (the shape of the path is what a Rushling sees, not the
    /// server's numbers), so it can only be used to look at the visuals.
    /// </summary>
    public static class KnockPreview
    {
        public struct Frame
        {
            public Vector3 Feet, Vel;
            public byte Flags, Stun;
        }

        public const float Dt = 1f / 30f;

        /// <summary>Frames: 6 standing ticks, the hit, the stun, then 20 ticks after the stun ends (the get-up pop).</summary>
        public static Frame[] Path(bool hard, Vector3 start, Vector3 away)
        {
            var push = (hard ? KnockProfile.HardPushCms : KnockProfile.SoftPushCms) / 100f;
            var up = (hard ? KnockProfile.HardUpCms : KnockProfile.SoftUpCms) / 100f;
            var stun = hard ? KnockProfile.HardStunTicks : KnockProfile.SoftStunTicks;
            away.y = 0f;
            away = away.sqrMagnitude > 1e-4f ? away.normalized : Vector3.forward;
            var n = 6 + 1 + stun + 20;
            var f = new Frame[n];
            var p = start;
            var v = Vector3.zero;
            var s = 0;
            for (var i = 0; i < n; i++)
            {
                if (i == 6)
                {
                    v = away * push + Vector3.up * up;
                    s = stun;
                }
                else if (i > 6)
                {
                    v.y -= 25f * Dt;
                    p += v * Dt;
                    if (p.y <= start.y)
                    {
                        p.y = start.y;
                        v.y = 0f;
                        v.x *= 0.7f;
                        v.z *= 0.7f;
                    }

                    if (s > 0)
                        s--;
                }

                var grounded = p.y <= start.y + 1e-4f && (i < 6 || v.y <= 0f);
                f[i] = new Frame { Feet = p, Vel = v, Flags = grounded ? MotorState.Grounded : (byte)0, Stun = (byte)s };
            }

            return f;
        }
    }
}
