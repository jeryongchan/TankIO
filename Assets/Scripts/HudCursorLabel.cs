using UnityEngine;
using TMPro;

namespace TankIO
{
    // the one label that follows the mouse (currently the HQ move-cost preview). the caller owns the
    // frame contract: Show every frame the label applies, Hide when it stops.
    public class HudCursorLabel : MonoBehaviour
    {
        private static HudCursorLabel instance;

        [SerializeField]
        private TMP_Text text; // pivot (0,1): hangs down-right of the cursor

        void Awake()
        {
            instance = this;
            text.enabled = false;
        }

        public static void Show(string message, Color color, Vector2 mouseScreenPosition)
        {
            if (instance == null)
                return;
            instance.text.text = message;
            instance.text.color = color;
            instance.text.rectTransform.position = new Vector3(
                mouseScreenPosition.x + 15f,
                mouseScreenPosition.y + 10f,
                0f
            );
            instance.text.enabled = true;
        }

        public static void Hide()
        {
            if (instance != null)
                instance.text.enabled = false;
        }
    }
}
