using UnityEngine;
using UnityEngine.InputSystem;

namespace FallRush.Client
{
    /// <summary>
    /// M2a PLACEHOLDER third-person orbit (doc 17 §4: ~7 m back, ~3 m up, mouse orbit). Replaced by the Cinemachine 3
    /// rig in M2b. Mouse orbits while the pointer is locked (click to lock; Esc releases it).
    /// </summary>
    public sealed class OrbitCameraPlaceholder : MonoBehaviour
    {
        public Transform target;
        public float distance = 7.6f;
        public float pitch = 22f;
        public float sensitivity = 0.12f;
        public bool invertY;

        public float Yaw { get; private set; }

        private void LateUpdate()
        {
            var mouse = Mouse.current;
            if (mouse != null && Cursor.lockState == CursorLockMode.Locked)
            {
                var d = mouse.delta.ReadValue();
                Yaw += d.x * sensitivity;
                pitch = Mathf.Clamp(pitch + (invertY ? d.y : -d.y) * sensitivity, -10f, 70f);
            }

            var pivot = (target != null ? target.position : Vector3.zero) + Vector3.up * 1.0f;
            var rot = Quaternion.Euler(pitch, Yaw, 0f);
            transform.SetPositionAndRotation(pivot + rot * new Vector3(0f, 0f, -distance), rot);
        }
    }
}
