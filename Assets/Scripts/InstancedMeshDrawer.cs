using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace TankIO
{
    // draws one mesh many times through Graphics.RenderMeshInstanced: transforms only, no GameObjects.
    // matrices arrive grouped into regions because culling is per call: RenderParams.worldBounds accepts or rejects a whole batch,
    // so one map-wide batch would pass the test from anywhere and every matrix would be drawn every frame.
    public class InstancedMeshDrawer
    {
        struct Region
        {
            public Matrix4x4[] matrices;
            public Bounds bounds;
        }

        private readonly Mesh mesh;
        private readonly Material[] materials; // one per submesh (trees split trunk and leaves)
        private readonly ShadowCastingMode shadowCastingMode;
        private readonly List<Region> regions = new List<Region>();
        private readonly Plane[] frustumPlanes = new Plane[6]; // reused: the allocating overload would put 6 planes on the heap per region-set per frame

        public InstancedMeshDrawer(Mesh mesh, Material[] materials, ShadowCastingMode shadowCastingMode)
        {
            this.mesh = mesh;
            this.materials = materials;
            this.shadowCastingMode = shadowCastingMode;
        }

        public void AddRegion(Matrix4x4[] matrices, Bounds bounds)
        {
            regions.Add(new Region { matrices = matrices, bounds = bounds });
        }

        // bounds from the matrices themselves: wrap the instance origins, then pad by margin
        // for what an instance reaches past its origin (mesh extents, sway, scale jitter).
        // FUTURE: have callers count per region and fill exact-size arrays.
        // List doubling plus this copy measured 150 MB of WASM heap to hold 37 MB of matrices, and the heap only grows.
        public void AddRegions(Dictionary<Vector2Int, List<Matrix4x4>> regionBuckets, float margin)
        {
            foreach (var pair in regionBuckets)
            {
                var matrices = pair.Value;
                if (matrices.Count == 0)
                    continue;
                var bounds = new Bounds(matrices[0].GetPosition(), Vector3.zero);
                for (int i = 1; i < matrices.Count; i++)
                    bounds.Encapsulate(matrices[i].GetPosition());
                bounds.Expand(margin * 2f); // Expand splits the amount across both sides
                AddRegion(matrices.ToArray(), bounds);
            }
        }

        public int RegionCount
        {
            get { return regions.Count; }
        }

        // the box handed to worldBounds, so gizmos draw what culling actually tests
        public Bounds GetRegionBounds(int index)
        {
            return regions[index].bounds;
        }

        public int GetRegionInstanceCount(int index)
        {
            return regions[index].matrices.Length;
        }

        // one camera, one frame: RenderMeshInstanced keeps nothing between frames,
        // so the owner calls this from RenderPipelineManager.beginCameraRendering on every repaint.
        public void Draw(Camera camera)
        {
            GeometryUtility.CalculateFrustumPlanes(camera, frustumPlanes);

            int submeshCount = Mathf.Min(materials.Length, mesh.subMeshCount);
            foreach (var region in regions)
            {
                // pre-test regions first to save cost; even though rendermeshinstance can cull for us, it still cost a lot to call when we pass in the matrices.
                if (!GeometryUtility.TestPlanesAABB(frustumPlanes, region.bounds))
                    continue;
                for (int submesh = 0; submesh < submeshCount; submesh++)
                {
                    var renderParams = new RenderParams(materials[submesh])
                    {
                        camera = camera, // scope to this camera; null would submit once per hook per camera
                        worldBounds = region.bounds,
                        shadowCastingMode = shadowCastingMode,
                    };
                    Graphics.RenderMeshInstanced(renderParams, mesh, submesh, region.matrices);
                }
            }
        }
    }
}
