using FallRush.Net;
using FallRush.Shared;
using UnityEngine;
using UnityEngine.InputSystem;

namespace FallRush.Client
{
    /// <summary>
    /// Keyboard + mouse → <see cref="InputCmd"/> (GDD §6): WASD move (camera-relative), Space jump, Shift / LMB dive.
    /// Press edges are accumulated every frame and handed to the next sampled tick, so a tap between ticks is never lost.
    /// The move vector is rotated by the camera yaw here, on the client; the server only sees world-space i8 axes.
    /// </summary>
    public sealed class PlayerInputSampler : MonoBehaviour, IInputSource
    {
        public FollowCamera cameraRig;

        private bool _jumpEdge;
        private bool _diveEdge;

        private void Update()
        {
            var kb = Keyboard.current;
            var mouse = Mouse.current;
            if (kb != null)
            {
                if (kb.spaceKey.wasPressedThisFrame)
                    _jumpEdge = true;
                if (kb.leftShiftKey.wasPressedThisFrame || kb.rightShiftKey.wasPressedThisFrame)
                    _diveEdge = true;
            }

            // LMB dives only while the pointer is locked (the first click locks it).
            if (mouse != null && mouse.leftButton.wasPressedThisFrame && Cursor.lockState == CursorLockMode.Locked)
                _diveEdge = true;
        }

        public void Sample(ref InputCmd cmd)
        {
            var kb = Keyboard.current;
            var mouse = Mouse.current;
            float x = 0f, y = 0f;
            bool jumpHeld = false, diveHeld = false;
            if (kb != null)
            {
                if (kb.wKey.isPressed || kb.upArrowKey.isPressed) y += 1f;
                if (kb.sKey.isPressed || kb.downArrowKey.isPressed) y -= 1f;
                if (kb.dKey.isPressed || kb.rightArrowKey.isPressed) x += 1f;
                if (kb.aKey.isPressed || kb.leftArrowKey.isPressed) x -= 1f;
                jumpHeld = kb.spaceKey.isPressed;
                diveHeld = kb.leftShiftKey.isPressed || kb.rightShiftKey.isPressed;
            }

            if (mouse != null && Cursor.lockState == CursorLockMode.Locked && mouse.leftButton.isPressed)
                diveHeld = true;

            var len = Mathf.Sqrt(x * x + y * y);
            if (len > 1f)
            {
                x /= len;
                y /= len;
            }

            var yawDeg = cameraRig != null ? cameraRig.Yaw : 0f;
            var yaw = yawDeg * Mathf.Deg2Rad;
            float s = Mathf.Sin(yaw), c = Mathf.Cos(yaw);
            // forward = (sin, cos), right = (cos, −sin)
            var wx = s * y + c * x;
            var wz = c * y - s * x;
            var qx = Mathf.RoundToInt(wx * 127f);
            var qz = Mathf.RoundToInt(wz * 127f);
            cmd.MoveX = (sbyte)Mathf.Clamp(qx, -127, 127);
            cmd.MoveY = (sbyte)Mathf.Clamp(qz, -127, 127);
            InputSanitizer.Sanitize(ref cmd); // same length clamp the server applies

            byte b = 0;
            if (jumpHeld) b |= InputCmd.JumpHeld;
            if (_jumpEdge) b |= InputCmd.JumpPressed;
            if (diveHeld) b |= InputCmd.DiveHeld;
            if (_diveEdge) b |= InputCmd.DivePressed;
            cmd.Buttons = b;
            _jumpEdge = _diveEdge = false;

            var y16 = Mathf.RoundToInt(Mathf.Repeat(yawDeg, 360f) / 360f * 65536f) & 0xFFFF;
            cmd.CamYaw = (ushort)y16;
        }
    }
}
