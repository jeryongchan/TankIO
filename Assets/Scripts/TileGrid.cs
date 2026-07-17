using UnityEngine;

namespace TankIO
{
    public struct MapBounds
    {
        public float minX;
        public float maxX;
        public float minZ;
        public float maxZ;
    }

    // grid dimensions, tile data and world-space math. source of truth for renderers on
    // this object (GroundRenderer, GridDebugOverlay). holds no visuals itself.
    // not named Grid: UnityEngine.Grid already exists and the two would clash.
    [ExecuteAlways]
    public class TileGrid : MonoBehaviour
    {
        // one grid per scene. tanks are spawned from a prefab, which cannot serialize a scene reference, so
        // they reach the grid through here.
        public static TileGrid Instance { get; private set; }

        [Header("Grid")]
        [SerializeField, Min(1)]
        private int width = 25;

        [SerializeField, Min(1)]
        private int height = 25;

        [SerializeField, Min(0.01f)]
        private float tileSize = 1f;

        [SerializeField]
        private Vector2Int[] blockedTiles; // hand-placed obstacles to path around, for testing

        private TileData[,] tiles;

        public int Width
        {
            get { return width; }
        }
        public int Height
        {
            get { return height; }
        }
        public float TileSize
        {
            get { return tileSize; }
        }

        public event System.Action GridChanged;

        void Awake()
        {
            Instance = this;
            BuildTiles();
        }

        void BuildTiles()
        {
            tiles = new TileData[width, height];
            for (int row = 0; row < height; row++)
            {
                for (int col = 0; col < width; col++)
                    tiles[col, row].Walkable = true;
            }

            foreach (Vector2Int blocked in blockedTiles)
            {
                if (IsInsideGrid(blocked))
                    tiles[blocked.x, blocked.y].Walkable = false;
            }
        }

        public bool IsWalkable(Vector2Int tile)
        {
            return IsInsideGrid(tile) && tiles[tile.x, tile.y].Walkable;
        }

        // world extents centered on the origin, reaching half a tile past the border
        // cell centers so cell edges are included.
        public void LineExtents(out float x0, out float x1, out float z0, out float z1)
        {
            float halfW = width * 0.5f * tileSize;
            float halfH = height * 0.5f * tileSize;
            x0 = -halfW;
            x1 = halfW;
            z0 = -halfH;
            z1 = halfH;
        }

        // world-space center of a tile. grid coords are (col, row) with the origin at
        // the grid's corner, so they index a TileData[,] directly.
        public Vector3 TileToWorldCenter(Vector2Int tile)
        {
            LineExtents(out float x0, out float x1, out float z0, out float z1);
            Vector3 local = new Vector3(
                x0 + (tile.x + 0.5f) * tileSize,
                0f,
                z0 + (tile.y + 0.5f) * tileSize
            );
            return transform.TransformPoint(local);
        }

        // tile containing a world point. false if the point falls outside the grid.
        public bool WorldToTile(Vector3 worldPosition, out Vector2Int tile)
        {
            LineExtents(out float x0, out float x1, out float z0, out float z1);
            Vector3 local = transform.InverseTransformPoint(worldPosition);

            int col = Mathf.FloorToInt((local.x - x0) / tileSize);
            int row = Mathf.FloorToInt((local.z - z0) / tileSize);
            tile = new Vector2Int(col, row);
            return IsInsideGrid(tile);
        }

        public bool IsInsideGrid(Vector2Int tile)
        {
            return tile.x >= 0 && tile.x < width && tile.y >= 0 && tile.y < height;
        }

        // grid extents as a world AABB, accounting for rotation (a 45deg-rotated grid
        // spans wider on X/Z). consumed by the camera for clamping.
        public MapBounds CalculateWorldMapBounds()
        {
            LineExtents(out float x0, out float x1, out float z0, out float z1);

            Vector3[] corners =
            {
                transform.TransformPoint(new Vector3(x0, 0f, z0)),
                transform.TransformPoint(new Vector3(x0, 0f, z1)),
                transform.TransformPoint(new Vector3(x1, 0f, z0)),
                transform.TransformPoint(new Vector3(x1, 0f, z1)),
            };

            var b = new MapBounds
            {
                minX = float.MaxValue,
                maxX = float.MinValue,
                minZ = float.MaxValue,
                maxZ = float.MinValue
            };
            foreach (var c in corners)
            {
                b.minX = Mathf.Min(b.minX, c.x);
                b.maxX = Mathf.Max(b.maxX, c.x);
                b.minZ = Mathf.Min(b.minZ, c.z);
                b.maxZ = Mathf.Max(b.maxZ, c.z);
            }
            return b;
        }

#if UNITY_EDITOR
        void OnValidate()
        {
            BuildTiles(); // width/height/blockedTiles may have changed
            if (GridChanged != null)
                GridChanged();
        }
#endif
    }
}
