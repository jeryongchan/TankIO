using UnityEngine;

namespace TankIO
{
    // flat quad covering the grid. swap for chunked/textured terrain later.
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

            tileGrid.LineExtents(out float x0, out float x1, out float z0, out float z1);

            groundMesh.Clear();
            groundMesh.vertices = new Vector3[]
            {
                new Vector3(x0, 0f, z0),
                new Vector3(x0, 0f, z1),
                new Vector3(x1, 0f, z1),
                new Vector3(x1, 0f, z0),
            };
            groundMesh.normals = new Vector3[] { Vector3.up, Vector3.up, Vector3.up, Vector3.up };
            groundMesh.uv = new Vector2[]
            {
                new Vector2(0f, 0f),
                new Vector2(0f, 1f),
                new Vector2(1f, 1f),
                new Vector2(1f, 0f),
            };
            groundMesh.triangles = new int[] { 0, 1, 2, 0, 2, 3 };
            groundMesh.RecalculateBounds();

            meshFilter.sharedMesh = groundMesh;
        }
    }
}
