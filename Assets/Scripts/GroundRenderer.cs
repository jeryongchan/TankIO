using UnityEngine;

namespace TankIO
{
    // one quad per walkable tile, welded into a single mesh, so the drawn ground is the playable
    // area and nothing else. rebuilt only on GridChanged. this is the mesh the chunked/dirty-rebake
    // pass will later split by region; the per-tile loop is already the right shape for that.
    [ExecuteAlways]
    [RequireComponent(typeof(TileGrid), typeof(MeshFilter), typeof(MeshRenderer))]
    public class GroundRenderer : MonoBehaviour
    {
        private TileGrid tileGrid;
        private MeshFilter meshFilter;
        private Mesh groundMesh;

        void OnEnable()
        {
            tileGrid = GetComponent<TileGrid>();
            meshFilter = GetComponent<MeshFilter>();
            tileGrid.GridChanged += Rebuild;
            Rebuild();
        }

        void OnDisable()
        {
            tileGrid.GridChanged -= Rebuild;
            if (groundMesh != null)
                DestroyImmediate(groundMesh);
        }

        void Rebuild()
        {
            if (groundMesh == null)
                groundMesh = new Mesh { name = "Ground", hideFlags = HideFlags.DontSave };

            var vertices = new System.Collections.Generic.List<Vector3>();
            var normals = new System.Collections.Generic.List<Vector3>();
            var uv = new System.Collections.Generic.List<Vector2>();
            var triangles = new System.Collections.Generic.List<int>();

            float half = tileGrid.TileSize * 0.5f;
            for (int row = 0; row < tileGrid.Height; row++)
            {
                for (int col = 0; col < tileGrid.Width; col++)
                {
                    Vector2Int tile = new Vector2Int(col, row);
                    if (!tileGrid.IsWalkable(tile))
                        continue;

                    Vector3 c = tileGrid.TileToLocalCenter(tile);
                    int baseIndex = vertices.Count;
                    vertices.Add(new Vector3(c.x - half, 0f, c.z - half));
                    vertices.Add(new Vector3(c.x - half, 0f, c.z + half));
                    vertices.Add(new Vector3(c.x + half, 0f, c.z + half));
                    vertices.Add(new Vector3(c.x + half, 0f, c.z - half));

                    for (int i = 0; i < 4; i++)
                        normals.Add(Vector3.up);

                    // uv per tile, so a ground texture tiles once per cell instead of stretching over the disc
                    uv.Add(new Vector2(0f, 0f));
                    uv.Add(new Vector2(0f, 1f));
                    uv.Add(new Vector2(1f, 1f));
                    uv.Add(new Vector2(1f, 0f));

                    triangles.Add(baseIndex);
                    triangles.Add(baseIndex + 1);
                    triangles.Add(baseIndex + 2);
                    triangles.Add(baseIndex);
                    triangles.Add(baseIndex + 2);
                    triangles.Add(baseIndex + 3);
                }
            }

            groundMesh.Clear();
            // a 45x45 disc is ~6.4k verts, but the grid is inspector-tunable and 16-bit tops out at 65k
            groundMesh.indexFormat = vertices.Count > 65000
                ? UnityEngine.Rendering.IndexFormat.UInt32
                : UnityEngine.Rendering.IndexFormat.UInt16;
            groundMesh.SetVertices(vertices);
            groundMesh.SetNormals(normals);
            groundMesh.SetUVs(0, uv);
            groundMesh.SetTriangles(triangles, 0);
            groundMesh.RecalculateBounds();

            meshFilter.sharedMesh = groundMesh;
        }
    }
}
