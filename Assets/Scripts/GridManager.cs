using UnityEngine;
using UnityEngine.Rendering;

namespace TankIO
{
    public struct MapBounds
    {
        public float minX,
            maxX,
            minZ,
            maxZ;
    }

    // flat XZ grid drawn as GL debug lines. render-only for now
    [ExecuteAlways] // so the line draw also runs in edit mode
    public class GridManager : MonoBehaviour
    {
        [Header("Grid")]
        [SerializeField, Min(1)]
        private int width = 25;

        [SerializeField, Min(1)]
        private int height = 25;

        [SerializeField, Min(0.01f)]
        private float tileSize = 1f;

        [Header("Debug draw")]
        [SerializeField]
        private bool drawGridLines = true;

        [SerializeField]
        private Color gridLineColor = new Color(1f, 1f, 1f, 0.25f);

        private Material lineMaterial;

        // world extents centered on the origin, reaching half a tile past the border
        // cell centers so cell edges are included.
        void LineExtents(out float x0, out float x1, out float z0, out float z1)
        {
            float halfW = width * 0.5f * tileSize;
            float halfH = height * 0.5f * tileSize;
            x0 = -halfW;
            x1 = halfW;
            z0 = -halfH;
            z1 = halfH;
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

        // draw via the SRP callback so lines appear on every camera (scene + game,
        // edit + play) and respect the transform once the grid is rotated.
        void OnEnable() => RenderPipelineManager.endCameraRendering += OnEndCameraRendering;

        void OnDisable() => RenderPipelineManager.endCameraRendering -= OnEndCameraRendering;

        void OnEndCameraRendering(ScriptableRenderContext context, Camera cam) => DrawGridLines();

        void DrawGridLines()
        {
            if (!drawGridLines)
                return;

            EnsureLineMaterial();
            lineMaterial.SetPass(0);

            LineExtents(out float x0, out float x1, out float z0, out float z1);

            GL.PushMatrix();
            GL.MultMatrix(transform.localToWorldMatrix);
            GL.Begin(GL.LINES);
            GL.Color(gridLineColor);

            for (int x = 0; x <= width; x++) // vertical lines, constant X
            {
                float wx = x0 + x * tileSize;
                GL.Vertex3(wx, 0f, z0);
                GL.Vertex3(wx, 0f, z1);
            }
            for (int z = 0; z <= height; z++) // horizontal lines, constant Z
            {
                float wz = z0 + z * tileSize;
                GL.Vertex3(x0, 0f, wz);
                GL.Vertex3(x1, 0f, wz);
            }

            GL.End();
            GL.PopMatrix();
        }

        void EnsureLineMaterial()
        {
            if (lineMaterial != null)
                return;

            // built-in unlit vertex-colored material, the standard way to GL-draw
            lineMaterial = new Material(Shader.Find("Hidden/Internal-Colored"))
            {
                hideFlags = HideFlags.HideAndDontSave
            };
            lineMaterial.SetInt("_SrcBlend", (int)BlendMode.SrcAlpha);
            lineMaterial.SetInt("_DstBlend", (int)BlendMode.OneMinusSrcAlpha);
            lineMaterial.SetInt("_Cull", (int)CullMode.Off);
            lineMaterial.SetInt("_ZWrite", 0);
        }

        void OnDestroy()
        {
            if (lineMaterial != null)
                DestroyImmediate(lineMaterial);
        }
    }
}
