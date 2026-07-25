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

    [ExecuteAlways]
    public partial class TileGrid : MonoBehaviour
    {
        public static TileGrid Instance { get; private set; }

        [Header("Grid")]
        [SerializeField, Min(1)]
        private int width = 25;

        [SerializeField, Min(1)]
        private int height = 25;

        [SerializeField, Min(0.01f)]
        private float tileSize = 1f;

        [Header("Shape")]
        [SerializeField]
        private bool discShaped = true;

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

        public Vector2 CenterPoint
        {
            get { return new Vector2(width * 0.5f, height * 0.5f); }
        }

        public float Radius // in tiles
        {
            get { return Mathf.Min(width, height) * 0.5f; }
        }

        // the one number every other system reads: 0 at the rim, 1 at the centre.
        // gold rate, move cost and spawn placement are all curves over this.
        public float RingDepth01(Vector2Int tile)
        {
            float distance = (TileCentreOffset(tile) - CenterPoint).magnitude;
            return Mathf.Clamp01(1f - distance / Radius);
        }

        static Vector2 TileCentreOffset(Vector2Int tile)
        {
            return new Vector2(tile.x + 0.5f, tile.y + 0.5f);
        }

        void Awake()
        {
            Instance = this;
            BuildTiles();
        }

        void BuildTiles()
        {
            tiles = new TileData[width, height];
            int groundCount = 0;
            for (int row = 0; row < height; row++)
            {
                for (int col = 0; col < width; col++)
                {
                    bool ground = IsGround(new Vector2Int(col, row));
                    tiles[col, row].Walkable = ground;
                    if (ground)
                        groundCount++;
                }
            }
            PlantTrees(groundCount); // plant last so other structures can be placed first
        }

        public bool IsWalkable(Vector2Int tile)
        {
            if (tiles == null)
                BuildTiles(); // ExecuteAlways: a renderer's OnEnable can beat Awake after a domain reload
            return IsInsideGrid(tile) && tiles[tile.x, tile.y].Walkable;
        }

        // the disc test itself rather than a stored flag: BuildTiles seeds Walkable from it, so the two cannot describe different shapes.
        public bool IsGround(Vector2Int tile)
        {
            if (!IsInsideGrid(tile))
                return false;
            if (!discShaped)
                return true;
            float radius = Radius;
            return (TileCentreOffset(tile) - CenterPoint).sqrMagnitude <= radius * radius;
        }

        // local extents centered on the origin, reaching half a tile past the border
        // cell centers so cell edges are included. transform is not applied: CalculateWorldMapBounds
        // is the world-space version, and it exists because a rotated grid stops being a rect.
        public void GridCornersBeforeTransform(out float x0, out float x1, out float z0, out float z1)
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
            return transform.TransformPoint(TileToLocalCenter(tile));
        }

        // untransformed tile centre. mesh builders want this: their vertices are local already.
        public Vector3 TileToLocalCenter(Vector2Int tile)
        {
            GridCornersBeforeTransform(out float x0, out _, out float z0, out _);
            return new Vector3(x0 + (tile.x + 0.5f) * tileSize, 0f, z0 + (tile.y + 0.5f) * tileSize);
        }

        // tile containing a world point. false if the point falls outside the grid.
        public bool WorldToTile(Vector3 worldPosition, out Vector2Int tile)
        {
            GridCornersBeforeTransform(out float x0, out _, out float z0, out _);
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
            GridCornersBeforeTransform(out float x0, out float x1, out float z0, out float z1);

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
            BuildTiles(); // width/height may have changed. tile data only: no GameObjects, so this is legal here
        }

        // every renderer at once. deliberately not raised from OnValidate: that runs once per
        // keystroke, and instantiating or destroying a GameObject inside it is forbidden.
        [ContextMenu("Rebuild Map")]
        void RebuildMap()
        {
            BuildTiles();
            if (GridChanged != null)
                GridChanged();
        }
#endif
    }
}
