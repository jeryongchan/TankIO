using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using Random = Unity.Mathematics.Random; // aliased, or it collides with UnityEngine.Random

namespace TankIO
{
    // grass chunks on the splat map's grass tiles, drawn through InstancedMeshDrawer:
    // matrices only, no GameObjects. planting reruns on GridChanged; drawing resubmits
    // every frame because RenderMeshInstanced keeps nothing between frames.
    [ExecuteAlways]
    public class GrassRenderer : MonoBehaviour
    {
        [SerializeField]
        private GameObject grassPrefab; // mesh, material and base scale are read off it

        [SerializeField, Min(0f)]
        private float chunksPerTile = 0.1f; // below 1, the fraction is the chance a tile gets one

        [SerializeField, Min(0)]
        private int maxChunks = 1000000; // safety handle

        [SerializeField]
        private int grassSeed = 1;

        [SerializeField, Range(0f, 0.5f)]
        private float positionJitter = 0.4f; // offset from tile centre (fraction of tile size)

        [SerializeField, Range(0f, 0.5f)]
        private float scaleJitter = 0.15f;

        [SerializeField, Min(1)]
        private int regionSize = 32; // tiles per region side: one draw call and one culling test each

        // dirt-grass blend 0.5 to 0.55 (0.05 blend width)
        [SerializeField, Range(0.5f, 0.9f)]
        private float plantCutoff = 0.55f;

        [SerializeField, Range(0.01f, 0.5f)]
        private float edgeFade = 0.2f; // weight above the cutoff at which edgeSink and edgeThinning reach zero

        [SerializeField, Range(0f, 1f)]
        private float edgeSink = 0.5f; // how deep grass on the cutoff is buried, as a fraction of its height

        [SerializeField, Range(0f, 1f)]
        private float edgeThinning = 0.7f; // on top of the cutoff gradient of sinking grass, it also choose not to plant grass' according to the same gradient band

        [SerializeField, Range(0f, 3f)]
        private float treeClearRadius = 1.2f; // tiles from a trunk before grass is back to full
#if UNITY_EDITOR
        [SerializeField]
        private bool showRegionGizmos = true;
#endif

        private TileGrid tileGrid;
        private InstancedMeshDrawer drawer;

        void OnEnable()
        {
            tileGrid = GetComponent<TileGrid>();
            tileGrid.GridChanged += Rebuild;
            RenderPipelineManager.beginCameraRendering += OnBeginCamera;
            Rebuild();
        }

        void OnDisable()
        {
            tileGrid.GridChanged -= Rebuild;
            RenderPipelineManager.beginCameraRendering -= OnBeginCamera;
            drawer = null;
        }

        void OnBeginCamera(ScriptableRenderContext context, Camera camera)
        {
            if (drawer == null)
                return;
            if (Application.isPlaying && CameraController.Lod != LodTier.Near) // play mode's zoom out will make trees vanish; isPlaying prevent it vanishing in editor
                return;
            // flat grass has no visible mirror image.
            // need to manually exclude since RenderMeshInstanced has no GameObject for the mirror's cullingMask to filter.
            if (camera == PlanarReflection.MirrorCamera)
                return;
            drawer.Draw(camera);
        }

        void Rebuild()
        {
            drawer = null;
            if (grassPrefab == null)
                return;
            if (Application.isBatchMode)
                return; // headless server: nothing needs drawing

            var meshFilter = grassPrefab.GetComponentInChildren<MeshFilter>();
            var meshRenderer = grassPrefab.GetComponentInChildren<MeshRenderer>();
            Mesh mesh = meshFilter != null ? meshFilter.sharedMesh : null;
            Material material = meshRenderer != null ? meshRenderer.sharedMaterial : null;
            if (mesh == null || material == null)
                return;
            if (!material.enableInstancing)
            {
                // RenderMeshInstanced rejects the material outright, so name the fix instead
                Debug.LogError($"GrassRenderer: tick Enable GPU Instancing on {material.name}", material);
                return;
            }

            var regions = new Dictionary<Vector2Int, List<Matrix4x4>>();
            Matrix4x4 gridToWorld = transform.localToWorldMatrix;
            // pivot to tip, which is what a sink fraction is measured against
            float chunkHeight = mesh.bounds.max.y * grassPrefab.transform.localScale.y;

            int planted = 0;
            for (int row = 0; row < tileGrid.Height && planted < maxChunks; row++)
            {
                for (int col = 0; col < tileGrid.Width && planted < maxChunks; col++)
                {
                    var tile = new Vector2Int(col, row);
                    // IsGround is not implied by GrassWeight: the noise answers for tiles outside
                    // the disc too, so rim tiles can clear 0.5 with no ground under them.
                    if (!tileGrid.IsGround(tile))
                        continue;
                    // a margin below plantCutoff: chunks sample the weight at their own jittered position,
                    //  so a tile centred just under the cutoff can still hold chunks
                    if (tileGrid.GrassWeight(tile) < plantCutoff - 0.05f)
                        continue;

                    var key = new Vector2Int(col / regionSize, row / regionSize);
                    if (!regions.TryGetValue(key, out var matrices))
                        regions[key] = matrices = new List<Matrix4x4>();
                    planted += Plant(tile, gridToWorld, chunkHeight, matrices);
                }
            }

            // Off: the grass shader has no ShadowCaster pass
            drawer = new InstancedMeshDrawer(mesh, new[] { material }, ShadowCastingMode.Off, "Grass");
            // without a margin, a region at the screen edge pops out while its grass is still
            // visible: a chunk reaches past its origin by its scaled extents plus wind sway
            float margin = mesh.bounds.size.magnitude * grassPrefab.transform.localScale.x * (1f + scaleJitter);
            drawer.AddRegions(regions, margin);
        }

#if UNITY_EDITOR
        // visualize the grass regions!
        void OnDrawGizmosSelected()
        {
            if (!showRegionGizmos || drawer == null)
                return;

            for (int i = 0; i < drawer.RegionCount; i++)
            {
                Bounds bounds = drawer.GetRegionBounds(i);
                Gizmos.color = new Color(0.4f, 1f, 0.4f, 0.7f);
                Gizmos.DrawWireCube(bounds.center, bounds.size);
                UnityEditor.Handles.Label(bounds.center, drawer.GetRegionInstanceCount(i).ToString());
            }

            UnityEditor.Handles.Label(transform.position, $"{drawer.RegionCount} regions");
        }
#endif

        // distance from the tile centre to the nearest trunk.
        float TreeClearance(Vector2Int tile)
        {
            if (treeClearRadius <= 0f)
                return 1f;

            int reach = Mathf.CeilToInt(treeClearRadius);
            float nearest = float.MaxValue;
            for (int dy = -reach; dy <= reach; dy++)
            {
                for (int dx = -reach; dx <= reach; dx++)
                {
                    if (!tileGrid.HasTree(new Vector2Int(tile.x + dx, tile.y + dy)))
                        continue;
                    nearest = Mathf.Min(nearest, Mathf.Sqrt(dx * dx + dy * dy));
                }
            }
            return nearest == float.MaxValue ? 1f : Mathf.InverseLerp(0f, treeClearRadius, nearest);
        }

        int Plant(Vector2Int tile, Matrix4x4 gridToWorld, float chunkHeight, List<Matrix4x4> matrices)
        {
            // offset by the seed so grass jitter does not land on the same values as TreeRng
            var rng = Random.CreateFromIndex((uint)(grassSeed * 1000003 + tile.x + tile.y * tileGrid.Width));
            int count = Mathf.FloorToInt(chunksPerTile);
            if (rng.NextFloat() < chunksPerTile - count)
                count++;
            float tileSize = tileGrid.TileSize;
            Vector3 centre = tileGrid.TileToLocalCenter(tile);

            int planted = 0;
            for (int i = 0; i < count; i++)
            {
                // jitter in tile units: the same fractions position the chunk and sample its weight
                float offsetX = rng.NextFloat(-1f, 1f) * positionJitter;
                float offsetZ = rng.NextFloat(-1f, 1f) * positionJitter;
                Quaternion rotation = Quaternion.Euler(0f, rng.NextFloat(360f), 0f);
                float sizeJitter = 1f + rng.NextFloat(-1f, 1f) * scaleJitter;
                float thinning = rng.NextFloat(); // do rng here instead of after weight to ensure consistency
                // sampled per chunk, not per tile: one shared tile weight made square patch edges
                float weight = tileGrid.GrassWeight(tile.x + offsetX, tile.y + offsetZ);
                if (weight < plantCutoff)
                    continue;
                float edge = Mathf.InverseLerp(plantCutoff, plantCutoff + edgeFade, weight);
                edge = Mathf.Min(edge, TreeClearance(tile)); // read edgefade above better
                if (thinning > Mathf.Lerp(1f - edgeThinning, 1f, edge)) // read edgeThinning above better
                    continue;
                Vector3 scale = grassPrefab.transform.localScale * sizeJitter;
                float sink = (1f - edge) * edgeSink * chunkHeight * sizeJitter;
                Vector3 local = centre + new Vector3(offsetX * tileSize, -sink, offsetZ * tileSize);
                matrices.Add(gridToWorld * Matrix4x4.TRS(local, rotation, scale));
                planted++;
            }
            return planted;
        }
    }
}
