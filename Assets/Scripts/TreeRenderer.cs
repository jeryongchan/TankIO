using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using Random = Unity.Mathematics.Random; // aliased, or it collides with UnityEngine.Random

namespace TankIO
{
    // draw trees with InstancedMeshDrawer: matrices only, no GameObjects.
    [ExecuteAlways]
    [RequireComponent(typeof(TileGrid))]
    public class TreeRenderer : MonoBehaviour
    {
        [SerializeField]
        private GameObject[] treePrefabs;

        [SerializeField, Range(0f, 0.5f)]
        private float positionJitter = 0.2f;

        [SerializeField, Range(0f, 0.5f)]
        private float scaleJitter = 0.1f;

        [SerializeField, Min(1)]
        private int regionSize = 16; // tiles per region. one culling test each

        [SerializeField]
        private Material iconMaterial; //  tree sprite for mid tier

        [SerializeField, Min(0f)]
        private float iconScale = 1.5f;

        private TileGrid tileGrid;
        private InstancedMeshDrawer[] drawers; // one per variant: a drawer holds one mesh
        private InstancedMeshDrawer iconDrawer;

        void OnEnable()
        {
            tileGrid = GetComponent<TileGrid>();
            tileGrid.GridChanged += Rebuild;
            tileGrid.TreeFelled += OnTreeFelled;
            RenderPipelineManager.beginCameraRendering += OnBeginCamera;
            Rebuild();
        }

        void OnDisable()
        {
            tileGrid.GridChanged -= Rebuild;
            tileGrid.TreeFelled -= OnTreeFelled;
            RenderPipelineManager.beginCameraRendering -= OnBeginCamera;
            drawers = null;
            iconDrawer = null;
        }

        // full replant on one felled tree: a map scan plus a few thousand matrices, well under
        // a frame. felling is rare, so per-region bookkeeping is not worth its code.
        void OnTreeFelled(Vector2Int tile)
        {
            Rebuild();
        }

        void OnBeginCamera(ScriptableRenderContext context, Camera camera)
        {
            if (drawers == null)
                return;
            // outside play mode the static holds whatever zoom the last session ended on
            LodTier lod = Application.isPlaying ? CameraController.Lod : LodTier.Near;
            if (lod == LodTier.Near)
            {
                foreach (var drawer in drawers)
                    drawer.Draw(camera);
            }
            else if (lod == LodTier.Mid && iconDrawer != null)
            {
                iconDrawer.Draw(camera);
            }
            // Far: no trees
        }

        void Rebuild()
        {
            drawers = null;
            iconDrawer = null;
            if (treePrefabs == null || treePrefabs.Length == 0)
                return;
            if (Application.isBatchMode)
                return; // headless server: the tile data is the authority, nothing needs drawing

            MeshRenderer[] renderers = ResolveRenderers();
            if (renderers == null)
                return;

            CollectMatrices(renderers, out var regions, out var iconRegions);
            BuildDrawers(renderers, regions);
            BuildIconDrawer(iconRegions);
        }

        // one renderer per prefab, or null if any of them cannot be instanced
        MeshRenderer[] ResolveRenderers()
        {
            var renderers = new MeshRenderer[treePrefabs.Length];
            for (int i = 0; i < renderers.Length; i++)
            {
                renderers[i] = LowestLod(treePrefabs[i]);
                var filter = renderers[i] != null ? renderers[i].GetComponent<MeshFilter>() : null;
                if (filter == null || filter.sharedMesh == null)
                {
                    Debug.LogError($"TreeRenderer: no mesh found on treePrefabs[{i}]", treePrefabs[i]);
                    return null;
                }
                foreach (var material in renderers[i].sharedMaterials)
                {
                    if (material != null && !material.enableInstancing)
                    {
                        // RenderMeshInstanced rejects the material outright, so name the fix instead
                        Debug.LogError($"TreeRenderer: tick Enable GPU Instancing on {material.name}", material);
                        return null;
                    }
                }
            }
            return renderers;
        }

        // per variant, matrices bucketed by region — same scheme as GrassRenderer.
        // icons share the region keys so both tiers cull against the same boxes.
        void CollectMatrices(
            MeshRenderer[] renderers,
            out Dictionary<Vector2Int, List<Matrix4x4>>[] regions,
            out Dictionary<Vector2Int, List<Matrix4x4>> iconRegions
        )
        {
            int variants = renderers.Length;
            regions = new Dictionary<Vector2Int, List<Matrix4x4>>[variants];
            for (int i = 0; i < variants; i++)
                regions[i] = new Dictionary<Vector2Int, List<Matrix4x4>>();
            iconRegions = new Dictionary<Vector2Int, List<Matrix4x4>>();

            Matrix4x4 gridToWorld = transform.localToWorldMatrix;
            float tileSize = tileGrid.TileSize;

            for (int row = 0; row < tileGrid.Height; row++)
            {
                for (int col = 0; col < tileGrid.Width; col++)
                {
                    var tile = new Vector2Int(col, row);
                    if (!tileGrid.HasTree(tile))
                        continue;

                    // same rng and call order as the GameObject version, so the forest looks unchanged
                    Random rng = tileGrid.TreeRng(tile);
                    int variant = rng.NextInt(variants);
                    float jitterX = rng.NextFloat(-1f, 1f);
                    float jitterZ = rng.NextFloat(-1f, 1f);
                    float rotationY = rng.NextFloat(360f);
                    float jitterScale = 1f + rng.NextFloat(-1f, 1f) * scaleJitter;

                    Vector3 local =
                        tileGrid.TileToLocalCenter(tile)
                        + new Vector3(jitterX * positionJitter * tileSize, 0f, jitterZ * positionJitter * tileSize);

                    var key = new Vector2Int(col / regionSize, row / regionSize);
                    if (!regions[variant].TryGetValue(key, out var matrices))
                        regions[variant][key] = matrices = new List<Matrix4x4>();
                    // localToWorldMatrix on the prefab asset carries the root scale and the LOD
                    // child's own transform, which Instantiate used to apply
                    matrices.Add(
                        gridToWorld
                            * Matrix4x4.TRS(local, Quaternion.Euler(0f, rotationY, 0f), Vector3.one * jitterScale)
                            * renderers[variant].transform.localToWorldMatrix
                    );
                    if (!iconRegions.TryGetValue(key, out var icons))
                        iconRegions[key] = icons = new List<Matrix4x4>();
                    // upright sprite: camera is fixed, thus quaternion.identity faces it; 0.5f for the height
                    icons.Add(
                        Matrix4x4.TRS(
                            gridToWorld.MultiplyPoint3x4(local) + Vector3.up * iconScale * 0.5f,
                            Quaternion.identity,
                            Vector3.one * iconScale
                        )
                    );
                }
            }
        }

        void BuildDrawers(MeshRenderer[] renderers, Dictionary<Vector2Int, List<Matrix4x4>>[] regions)
        {
            drawers = new InstancedMeshDrawer[renderers.Length];
            for (int i = 0; i < renderers.Length; i++)
            {
                Mesh mesh = renderers[i].GetComponent<MeshFilter>().sharedMesh; // validated in ResolveRenderers
                drawers[i] = new InstancedMeshDrawer(mesh, renderers[i].sharedMaterials, ShadowCastingMode.On);
                float margin = mesh.bounds.size.magnitude * renderers[i].transform.lossyScale.x * (1f + scaleJitter);
                drawers[i].AddRegions(regions[i], margin);
            }
        }

        void BuildIconDrawer(Dictionary<Vector2Int, List<Matrix4x4>> iconRegions)
        {
            // untick Enable GPU Instancing on the icon material and Mid silently draws nothing
            if (iconMaterial == null || !iconMaterial.enableInstancing)
                return;
            iconDrawer = new InstancedMeshDrawer(
                Resources.GetBuiltinResource<Mesh>("Quad.fbx"),
                new[] { iconMaterial },
                ShadowCastingMode.Off
            );
            iconDrawer.AddRegions(iconRegions, iconScale);
        }

        // For testing future tree arts: strip only keep the lowest-vertex LOD
        static MeshRenderer LowestLod(GameObject prefab)
        {
            if (prefab == null)
                return null;
            var lodGroup = prefab.GetComponentInChildren<LODGroup>();
            if (lodGroup != null)
            {
                LOD[] lods = lodGroup.GetLODs();
                if (lods.Length > 0 && lods[lods.Length - 1].renderers.Length > 0)
                {
                    if (lods[lods.Length - 1].renderers[0] is MeshRenderer renderer)
                        return renderer;
                }
            }
            return prefab.GetComponentInChildren<MeshRenderer>();
        }
    }
}
