using UnityEngine;
using UnityEngine.UI;

namespace FallRush.Client
{
    /// <summary>P7: keeps a card's helper layer (bevel cover, ribbon foot) the same colour as its source layer.</summary>
    [DisallowMultipleComponent]
    public sealed class CardTint : MonoBehaviour
    {
        public Image Source;
        private Image _img;

        private void LateUpdate()
        {
            if (Source == null)
                return;
            _img ??= GetComponent<Image>();
            if (_img.color != Source.color)
                _img.color = Source.color;
        }
    }
}
