using FallRush.Net;
using UnityEngine;

namespace FallRush.Client
{
    /// <summary>
    /// G4.8: a render-only object drawn in one look only (the soft look's far stadium backdrop). No colliders ever sit on it;
    /// <see cref="LookState.ApplyScene"/> switches its renderers.
    /// </summary>
    public sealed class LookOnly : MonoBehaviour
    {
        public LookId look = LookId.Soft;

        private void OnEnable() => Apply(LookState.Current);

        public void Apply(LookId current)
        {
            foreach (var r in GetComponentsInChildren<Renderer>(true))
                r.enabled = current == look;
        }
    }
}
