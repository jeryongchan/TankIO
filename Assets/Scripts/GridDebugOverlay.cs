using UnityEngine;

namespace TankIO
{
    // line-mesh overlay of the grid, for dev only. lives on a child of TileGrid (the parent
    // already has the ground's MeshFilter) lifted slightly on Y so lines don't z-fight with
    // the ground. disable the GameObject to hide it.
    [ExecuteAlways]
    [RequireComponent(typeof(MeshFilter))]
    public class GridDebugOverlay : MonoBehaviour
    {
        [SerializeField]
        private TileGrid tileGrid;

        [SerializeField]
        private bool showCoordinates = true; // label every tile with its (col, row)

        private MeshFilter meshFilter;
        private Mesh lineMesh;

        void OnEnable()
        {
            meshFilter = GetComponent<MeshFilter>();
            tileGrid.GridChanged += Rebuild;
            Rebuild();
        }

        void OnDisable()
        {
            tileGrid.GridChanged -= Rebuild;
            if (lineMesh != null)
                DestroyImmediate(lineMesh);
        }

        void Rebuild()
        {
            if (lineMesh == null)
                lineMesh = new Mesh { name = "GridLines", hideFlags = HideFlags.DontSave };
            BuildLineMesh(lineMesh);
            meshFilter.sharedMesh = lineMesh;
        }

        void BuildLineMesh(Mesh mesh)
        {
            tileGrid.LineExtents(out float x0, out float x1, out float z0, out float z1);

            int width = tileGrid.Width;
            int height = tileGrid.Height;
            float tileSize = tileGrid.TileSize;

            int vertexCount = (width + 1 + height + 1) * 2;
            var vertices = new Vector3[vertexCount];
            var indices = new int[vertexCount];

            int v = 0;
            for (int x = 0; x <= width; x++) // vertical lines, constant X
            {
                float wx = x0 + x * tileSize;
                vertices[v] = new Vector3(wx, 0f, z0);
                vertices[v + 1] = new Vector3(wx, 0f, z1);
                v += 2;
            }
            for (int z = 0; z <= height; z++) // horizontal lines, constant Z
            {
                float wz = z0 + z * tileSize;
                vertices[v] = new Vector3(x0, 0f, wz);
                vertices[v + 1] = new Vector3(x1, 0f, wz);
                v += 2;
            }

            for (int i = 0; i < vertexCount; i++)
                indices[i] = i;

            mesh.Clear();
            mesh.vertices = vertices;
            mesh.SetIndices(indices, MeshTopology.Lines, 0);
            mesh.RecalculateBounds();
        }

#if UNITY_EDITOR
        void OnDrawGizmos()
        {
            if (tileGrid == null)
                return;

            float lineHeight = transform.position.y;
            DrawBlockedTiles(lineHeight);

            if (!showCoordinates)
                return;

            for (int row = 0; row < tileGrid.Height; row++)
            {
                for (int col = 0; col < tileGrid.Width; col++)
                {
                    Vector3 center = tileGrid.TileToWorldCenter(new Vector2Int(col, row));
                    center.y = lineHeight;
                    UnityEditor.Handles.Label(center, "(" + col + ", " + row + ")");
                }
            }
        }

        void DrawBlockedTiles(float lineHeight)
        {
            float tileSize = tileGrid.TileSize;
            Gizmos.color = new Color(1f, 0.2f, 0.2f, 0.5f);

            for (int row = 0; row < tileGrid.Height; row++)
            {
                for (int col = 0; col < tileGrid.Width; col++)
                {
                    Vector2Int tile = new Vector2Int(col, row);
                    if (tileGrid.IsWalkable(tile))
                        continue;
                    if (tileGrid.RingDepth01(tile) <= 0f)
                        continue; // off the disc: not an obstacle, just not map

                    Vector3 center = tileGrid.TileToWorldCenter(tile);
                    center.y = lineHeight;
                    Gizmos.DrawCube(center, new Vector3(tileSize, 0.02f, tileSize));
                }
            }
        }
#endif
    }
}
