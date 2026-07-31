using UnityEngine;

namespace TankIO
{
    // for the fullscreen camera overlays: zoomed out they cover the map as noise, and ZTest Always
    // means each one pays a full screen of overdraw.
    [ExecuteAlways]
    [RequireComponent(typeof(Renderer))]
    public class NearOnlyRenderer : MonoBehaviour
    {
        private Renderer target;

        void OnEnable()
        {
            target = GetComponent<Renderer>();
        }

        void LateUpdate()
        {
            // in edit mode the static holds the last play session's zoom
            target.enabled = !Application.isPlaying || CameraController.Lod == LodTier.Near;
        }
    }
}
