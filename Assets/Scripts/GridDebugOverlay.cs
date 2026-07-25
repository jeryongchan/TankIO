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
            tileGrid.GridCornersBeforeTransform(out float x0, out float x1, out float z0, out float z1);

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

    }
}
