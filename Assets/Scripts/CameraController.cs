using UnityEngine;
using UnityEngine.InputSystem;

namespace TankIO
{
    // top-down isometric orthographic camera
    [RequireComponent(typeof(Camera))]
    public class CameraController : MonoBehaviour
    {
        [SerializeField]
        private float scrollZoomSpeed = 5f;

        [SerializeField]
        private float minZoom = 2f;

        [SerializeField]
        private float maxZoom = 16f;

        [SerializeField]
        private float edgeMarginFraction = 0.1f; // margin past the map edge as a fraction of the viewport

        [SerializeField]
        private TileGrid tileGrid;

        // world-space map bounds, pulled from the grid in RefreshClampLimits
        private float minX;
        private float maxX;
        private float minZ;
        private float maxZ;

        private Camera cam;
        private readonly Plane groundPlane = new Plane(Vector3.up, Vector3.zero);
        private Vector3 dragOrigin;
        private float groundZOffset;

        void Start()
        {
            cam = GetComponent<Camera>();
            RefreshClampLimits();
            Vector3 groundTarget = new Vector3((minX + maxX) * 0.5f, 0f, (minZ + maxZ) * 0.5f);
            float cameraDistance = 1000f; // so wont clip into large grid
            cam.transform.position = groundTarget - cam.transform.forward * cameraDistance;
            cam.farClipPlane = Mathf.Max(cam.farClipPlane, cameraDistance * 2f);

            float tiltDegrees = transform.eulerAngles.x;
            groundZOffset = cam.transform.position.y / Mathf.Tan(Mathf.Deg2Rad * tiltDegrees);
        }

        void Update()
        {
            HandleMouseDrag();
            HandleScrollZoom();
            ClampCamera();
        }

        // pull the map bounds from the grid; call again if the grid changes size.
        public void RefreshClampLimits()
        {
            MapBounds b = tileGrid.CalculateWorldMapBounds();
            minX = b.minX;
            maxX = b.maxX;
            minZ = b.minZ;
            maxZ = b.maxZ;
        }

        void HandleMouseDrag()
        {
            var mouse = Mouse.current;
            if (mouse == null)
                return;

            Vector2 screenPos = mouse.position.ReadValue();
            if (mouse.rightButton.wasPressedThisFrame)
                dragOrigin = GetWorldPoint(screenPos);
            else if (mouse.rightButton.isPressed)
                cam.transform.position += dragOrigin - GetWorldPoint(screenPos); // keep grabbed point under cursor
        }

        void HandleScrollZoom()
        {
            var mouse = Mouse.current;
            if (mouse == null)
                return;

            float scroll = mouse.scroll.ReadValue().y;
            if (Mathf.Approximately(scroll, 0f))
                return;

            cam.orthographicSize = Mathf.Clamp(
                cam.orthographicSize - scroll * 0.01f * scrollZoomSpeed,
                minZoom,
                maxZoom
            );
        }

        void ClampCamera()
        {
            // clamp the ground point the camera looks at, not the pivot: the tilt
            // offsets them in Z.
            Vector3 p = cam.transform.position;

            p.x = ClampAxis(p.x, minX, maxX, ViewHalfExtentX()); // tilt is around X, so X shares pivot and ground point
            float groundZ = ClampAxis(p.z + groundZOffset, minZ, maxZ, ViewHalfExtentZ());
            p.z = groundZ - groundZOffset;

            cam.transform.position = p;
        }

        // clamp within [min,max] inset by halfView; center the axis if the view is wider than the range.
        static float ClampAxis(float value, float min, float max, float halfView)
        {
            float lo = min + halfView;
            float hi = max - halfView;
            return lo > hi ? (min + max) * 0.5f : Mathf.Clamp(value, lo, hi);
        }

        float ViewHalfExtentX()
        {
            return cam.orthographicSize * cam.aspect * (1f - edgeMarginFraction); // orthographicsize always is height; aspect is convert height to width
        }

        // tilt stretches the vertical view onto the ground, so divide by sin(tilt)
        float ViewHalfExtentZ()
        {
            float sin = Mathf.Sin(Mathf.Deg2Rad * transform.eulerAngles.x);
            return cam.orthographicSize / sin * (1f - edgeMarginFraction);
        }

        Vector3 GetWorldPoint(Vector2 screenPosition) // project a screen point onto the ground (y=0) plane
        {
            Ray ray = cam.ScreenPointToRay(screenPosition);
            return groundPlane.Raycast(ray, out float distance)
              ? ray.GetPoint(distance)
              : Vector3.zero;
        }
    }
}
