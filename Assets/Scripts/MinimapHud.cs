using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.EventSystems;

namespace TankIO
{
    // shows HQ positions on the world map, as well as capital (and refineries in future)
    [RequireComponent(typeof(RectTransform))]
    public class MinimapHud : MonoBehaviour, IPointerClickHandler
    {
        [SerializeField]
        private RectTransform capitalDotPrefab;

        [SerializeField]
        private RectTransform ownHqDotPrefab;

        [SerializeField]
        private RectTransform enemyHqDotPrefab;

        [SerializeField]
        private RectTransform viewRect;

        private readonly Plane groundPlane = new Plane(Vector3.up, Vector3.zero);
        private readonly Dictionary<NetworkBehaviour, RectTransform> dots =
            new Dictionary<NetworkBehaviour, RectTransform>();
        private readonly List<NetworkBehaviour> goneDots = new List<NetworkBehaviour>(); // gathered first, since a dictionary cannot be edited mid-walk
        private RectTransform panelRect;

        void Awake()
        {
            panelRect = (RectTransform)transform;
        }

        void LateUpdate()
        {
            MapBounds bounds = TileGrid.Instance.CalculateWorldMapBounds();

            // the grid is a scene object but the capital is network spawned, so it is missing until a session starts
            CapitalController capital = CapitalController.Instance;
            if (capital != null)
                MoveTo(
                    DotFor(capital, capitalDotPrefab),
                    PanelPoint(TileGrid.Instance.TileToWorldCenter(capital.CenterTile), bounds)
                );

            foreach (HqController hq in HqController.SpawnedHqs)
            {
                RectTransform prefab = hq.CommandedByLocalPlayer ? ownHqDotPrefab : enemyHqDotPrefab;
                MoveTo(DotFor(hq, prefab), PanelPoint(hq.transform.position, bounds));
            }
            DestroyGoneDots();
            UpdateViewRect(bounds);
        }

        // the dot drawn for one world object, created the first frame that object is seen
        RectTransform DotFor(NetworkBehaviour worldObject, RectTransform prefab)
        {
            if (!dots.TryGetValue(worldObject, out RectTransform dot))
            {
                dot = Instantiate(prefab, panelRect, false);
                dots.Add(worldObject, dot);
            }
            return dot;
        }

        // a despawn destroys the object but leaves its entry here, so the null keys are the dots to drop
        void DestroyGoneDots()
        {
            foreach (KeyValuePair<NetworkBehaviour, RectTransform> entry in dots)
            {
                if (entry.Key == null)
                {
                    Destroy(entry.Value.gameObject);
                    goneDots.Add(entry.Key);
                }
            }
            for (int index = 0; index < goneDots.Count; index++)
                dots.Remove(goneDots[index]);
            goneDots.Clear();
        }

        // world ground point to panel coordinates, measured from the panel's bottom-left corner
        Vector2 PanelPoint(Vector3 worldPosition, MapBounds bounds)
        {
            Rect rect = panelRect.rect;
            float normalizedX = Mathf.InverseLerp(bounds.minX, bounds.maxX, worldPosition.x);
            float normalizedZ = Mathf.InverseLerp(bounds.minZ, bounds.maxZ, worldPosition.z);
            return new Vector2(normalizedX * rect.width, normalizedZ * rect.height);
        }

        // the viewport's ground footprint as a box, the "you are here"
        void UpdateViewRect(MapBounds bounds)
        {
            Camera camera = Camera.main;
            Vector2 min = new Vector2(float.PositiveInfinity, float.PositiveInfinity);
            Vector2 max = new Vector2(float.NegativeInfinity, float.NegativeInfinity);
            for (int cornerIndex = 0; cornerIndex < 4; cornerIndex++)
            {
                Ray ray = camera.ViewportPointToRay(new Vector3(cornerIndex & 1, cornerIndex >> 1, 0f));
                groundPlane.Raycast(ray, out float distance);
                Vector2 corner = PanelPoint(ray.GetPoint(distance), bounds);
                min = Vector2.Min(min, corner);
                max = Vector2.Max(max, corner);
            }
            MoveTo(viewRect, min); // its pivot is authored bottom-left, so this corner is what places it
            Vector2 boxSize = max - min;
            if (viewRect.sizeDelta != boxSize)
                viewRect.sizeDelta = boxSize;
        }

        public void OnPointerClick(PointerEventData eventData)
        {
            if (
                !RectTransformUtility.ScreenPointToLocalPointInRectangle(
                    panelRect,
                    eventData.position,
                    eventData.pressEventCamera,
                    out Vector2 localPoint
                )
            )
                return;
            Rect rect = panelRect.rect;
            MapBounds bounds = TileGrid.Instance.CalculateWorldMapBounds();
            float worldX = Mathf.Lerp(bounds.minX, bounds.maxX, (localPoint.x - rect.xMin) / rect.width);
            float worldZ = Mathf.Lerp(bounds.minZ, bounds.maxZ, (localPoint.y - rect.yMin) / rect.height);
            CameraController.Instance.CenterOn(new Vector3(worldX, 0f, worldZ)); // CenterOn clamps, so a click past the rim lands at the edge
        }

        // a canvas rebuild is triggered per write, so a parked world should dirty nothing
        static void MoveTo(RectTransform rect, Vector2 panelPosition)
        {
            if (rect.anchoredPosition != panelPosition)
                rect.anchoredPosition = panelPosition;
        }
    }
}
