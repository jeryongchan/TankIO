using UnityEngine;
using UnityEngine.InputSystem;

namespace TankIO
{
    public enum LodTier
    {
        Near,
        Mid,
        Far
    }

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
        private bool lodEnabled = true; // off forces the Near tier at every zoom

        [SerializeField]
        private float midTierZoom = 12f; // zoom past which icons stand in for meshes

        [SerializeField]
        private float farTierZoom = 30f; // zoom past which only HQ dots remain

        [SerializeField]
        private float overlayReferenceZoom = 8f; // the zoom screen-space overlays are authored to look right at

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

        // the HQ jumps the camera here the moment it spawns, so the view starts on your own base instead of the middle of the map.
        public static CameraController Instance { get; private set; }

        public static LodTier Lod { get; private set; }

        public bool LodEnabled
        {
            get { return lodEnabled; }
            set { lodEnabled = value; }
        }

        // health bars, badges and the HQ buttons all read this so they scale with camera zoom (dont retain size).
        public static float OverlayScale
        {
            get
            {
                if (Instance == null || Instance.cam == null)
                    return 1f;
                return Instance.overlayReferenceZoom / Instance.cam.orthographicSize;
            }
        }

        // place a screen-space overlay over a world point, at the scale the others are using.
        // returns false when there is nowhere to put it, and the caller turns its overlay off.
        public static bool TryPin(Transform overlay, Vector3 worldPosition)
        {
            Camera mainCamera = Camera.main;
            if (mainCamera == null)
                return false;
            Vector3 screen = mainCamera.WorldToScreenPoint(worldPosition);
            if (screen.z <= 0f)
                return false; // behind the camera
            overlay.position = new Vector3(screen.x, screen.y, 0f);
            overlay.localScale = Vector3.one * OverlayScale;
            return true;
        }

        // shadow distance is measured from the camera, so the Near tier stays here and no further
        private const float MinCameraDistance = 50f;

        // depth a point loses per unit of height is height / sin(tilt), so this buys 10 units of it
        private const float ClipMargin = 20f;

        void Start()
        {
            Instance = this;
            cam = GetComponent<Camera>();
            RefreshClampLimits();
            PlaceAbove(new Vector3((minX + maxX) * 0.5f, 0f, (minZ + maxZ) * 0.5f));
            cam.farClipPlane = Mathf.Max(cam.farClipPlane, DistanceFor(maxZoom) * 2f);
            ApplyCameraDistance();
        }

        // grows with zoom, or the bottom of the view crosses the near plane and cuts away the ground
        float DistanceFor(float zoom)
        {
            return Mathf.Max(MinCameraDistance, zoom / Mathf.Tan(Mathf.Deg2Rad * transform.eulerAngles.x) + ClipMargin);
        }

        // an ortho camera renders the same image from any distance along forward: only the clip range moves
        void ApplyCameraDistance()
        {
            float tilt = Mathf.Deg2Rad * transform.eulerAngles.x;
            float distance = DistanceFor(cam.orthographicSize);
            cam.transform.position += cam.transform.forward * (cam.transform.position.y / Mathf.Sin(tilt) - distance);
            groundZOffset = distance * Mathf.Cos(tilt);
        }

        public void CenterOn(Vector3 worldPoint)
        {
            if (cam == null)
                cam = GetComponent<Camera>();
            PlaceAbove(new Vector3(worldPoint.x, 0f, worldPoint.z));
            ClampCamera();
        }

        void PlaceAbove(Vector3 groundTarget)
        {
            cam.transform.position = groundTarget - cam.transform.forward * DistanceFor(cam.orthographicSize);
        }

        void Update()
        {
            HandleMouseDrag();
            HandleScrollZoom();
            ApplyCameraDistance(); // before the clamp, which reads groundZOffset
            ClampCamera();
            Lod = CurrentLod();
        }

        LodTier CurrentLod()
        {
            if (!lodEnabled)
            {
                return LodTier.Near;
            }

            if (cam.orthographicSize < midTierZoom)
            {
                return LodTier.Near;
            }
            else if (cam.orthographicSize < farTierZoom)
            {
                return LodTier.Mid;
            }
            else
            {
                return LodTier.Far;
            }
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
                cam.orthographicSize - scroll * 0.01f * scrollZoomSpeed * cam.orthographicSize,
                minZoom,
                maxZoom
            );
        }

        void ClampCamera()
        {
            // clamp the ground point the camera looks at, not the pivot: the tilt offsets them in Z.
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
            return groundPlane.Raycast(ray, out float distance) ? ray.GetPoint(distance) : Vector3.zero;
        }
    }
}
