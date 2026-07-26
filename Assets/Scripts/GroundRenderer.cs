using UnityEngine;

namespace TankIO
{
    [ExecuteAlways]
    [RequireComponent(typeof(TileGrid), typeof(MeshFilter), typeof(MeshRenderer))]
    public class GroundRenderer : MonoBehaviour
    {
        private TileGrid tileGrid;
        private MeshFilter meshFilter;
        private MeshRenderer meshRenderer;
        private Mesh groundMesh;
        private Texture2D groundMask;
        private Texture2D splatMap;
        private MaterialPropertyBlock properties;
        private static readonly int GroundMaskId = Shader.PropertyToID("_GroundMask");
        private static readonly int SplatMapId = Shader.PropertyToID("_SplatMap");

        void OnEnable()
        {
            tileGrid = GetComponent<TileGrid>();
            meshFilter = GetComponent<MeshFilter>();
            meshRenderer = GetComponent<MeshRenderer>();
            tileGrid.GridChanged += Rebuild;
            Rebuild();
        }

        void OnDisable()
        {
            tileGrid.GridChanged -= Rebuild;
            if (groundMesh != null)
                DestroyImmediate(groundMesh);
            if (groundMask != null)
                DestroyImmediate(groundMask);
            if (splatMap != null)
                DestroyImmediate(splatMap);
        }

        void Rebuild()
        {
            BuildQuad();
            BuildGroundMask();
            BuildSplatMap();
        }

        void BuildQuad()
        {
            if (groundMesh == null)
                groundMesh = new Mesh { name = "Ground", hideFlags = HideFlags.DontSave };

            tileGrid.GridCornersBeforeTransform(out float x0, out float x1, out float z0, out float z1);

            var vertices = new Vector3[]
            {
                new Vector3(x0, 0f, z0),
                new Vector3(x0, 0f, z1),
                new Vector3(x1, 0f, z1),
                new Vector3(x1, 0f, z0),
            };

            // uv spans the grid, so uv and tile coords are the same space and the mask needs no remapping.
            // detail textures do NOT use this uv: the shader tiles them by world position, which keeps
            // texel density fixed no matter how large the grid gets.
            var uv = new Vector2[]
            {
                new Vector2(0f, 0f),
                new Vector2(0f, 1f),
                new Vector2(1f, 1f),
                new Vector2(1f, 0f),
            };

            var normals = new Vector3[] { Vector3.up, Vector3.up, Vector3.up, Vector3.up };
            // w = -1 puts the bitangent along +Z, matching uv.y. without tangents the normal maps
            // would light from an arbitrary direction.
            var tangent = new Vector4(1f, 0f, 0f, -1f);
            var tangents = new Vector4[] { tangent, tangent, tangent, tangent };

            var triangles = new int[] { 0, 1, 2, 0, 2, 3 };

            groundMesh.Clear();
            groundMesh.SetVertices(vertices);
            groundMesh.SetNormals(normals);
            groundMesh.SetTangents(tangents);
            groundMesh.SetUVs(0, uv);
            groundMesh.SetTriangles(triangles, 0);
            groundMesh.RecalculateBounds();

            meshFilter.sharedMesh = groundMesh;
        }

        void BuildGroundMask()
        {
            int w = tileGrid.Width;
            int h = tileGrid.Height;
            if (groundMask != null && (groundMask.width != w || groundMask.height != h))
            {
                DestroyImmediate(groundMask);
                groundMask = null;
            }
            if (groundMask == null)
            {
                // no mip chain: a mip would average ground against void and eat the rim.
                groundMask = new Texture2D(w, h, TextureFormat.R8, false, true)
                {
                    name = "GroundMask",
                    hideFlags = HideFlags.DontSave,
                    wrapMode = TextureWrapMode.Clamp,
                };
            }
            // nearest texel: the rim keeps its square tile edges instead of blending into a curve
            groundMask.filterMode = FilterMode.Point;

            var texels = new byte[w * h];
            for (int row = 0; row < h; row++)
            {
                int rowStart = row * w;
                for (int col = 0; col < w; col++)
                    texels[rowStart + col] = tileGrid.IsGround(new Vector2Int(col, row)) ? (byte)255 : (byte)0;
            }
            groundMask.SetPixelData(texels, 0);
            groundMask.Apply(false, false);
            // a property block rather than the shared material: the mask is generated per scene and
            // writing it to the material asset would dirty it on every domain reload.
            if (properties == null)
                properties = new MaterialPropertyBlock();
            meshRenderer.GetPropertyBlock(properties);
            properties.SetTexture(GroundMaskId, groundMask);
            meshRenderer.SetPropertyBlock(properties);
        }

        void BuildSplatMap()
        {
            int w = tileGrid.Width;
            int h = tileGrid.Height;
            if (splatMap != null && (splatMap.width != w || splatMap.height != h))
            {
                DestroyImmediate(splatMap);
                splatMap = null;
            }
            if (splatMap == null)
            {
                splatMap = new Texture2D(w, h, TextureFormat.R8, false, true)
                {
                    name = "GroundSplat",
                    hideFlags = HideFlags.DontSave,
                    wrapMode = TextureWrapMode.Clamp,
                    // bilinear, unlike the mask: the grass/dirt border blends between tiles.
                    // the disc rim stays crisp because only the mask clips.
                    filterMode = FilterMode.Bilinear,
                };
            }
            var texels = new byte[w * h];
            for (int row = 0; row < h; row++)
            {
                int rowStart = row * w;
                for (int col = 0; col < w; col++)
                    texels[rowStart + col] = (byte)(tileGrid.GrassWeight(new Vector2Int(col, row)) * 255f);
            }
            splatMap.SetPixelData(texels, 0);
            splatMap.Apply(false, false);
            if (properties == null)
                properties = new MaterialPropertyBlock();
            meshRenderer.GetPropertyBlock(properties);
            properties.SetTexture(SplatMapId, splatMap);
            meshRenderer.SetPropertyBlock(properties);
        }
    }
}
