using UnityEngine;
using Random = Unity.Mathematics.Random; // aliased, or it collides with UnityEngine.Random

namespace TankIO
{
    // trees are world data, not entities: no NetworkObject, no collider, no IShellTarget. both sides
    // derive the same forest from treeSeed, so only destruction travels (TreeSystem).
    public partial class TileGrid
    {
        [Header("Trees")]
        [SerializeField]
        private int treeSeed = 1; // if need to build other strucutre like refineries, might need to rename to a global seed?

        [SerializeField]
        private float treeDensity = 0.1f;

        [SerializeField]
        private float trunkRadius = 0.25f;

        public event System.Action<Vector2Int> TreeFelled;

        void PlantTrees(int groundCount)
        {
            int budget = Mathf.RoundToInt(groundCount * treeDensity);
            int dense = Mathf.RoundToInt(budget * 0.75f);
            PlantPatches(dense, 50, 100, 50f, 0); // plant dense pack of forest. 0.75 of the budget.
            PlantPatches(budget - dense, 1, 20, 1f, 1); // few stray trees here and there
        }

        void PlantPatches(int budget, int minTrees, int maxTrees, float packing, int pass)
        {
            // pass separates the two calls, if not will plant the same forest twice.
            var rng = Random.CreateFromIndex((uint)(treeSeed * 1000003 + pass));
            int planted = 0;
            for (int patch = 0; planted < budget && patch < budget; patch++)
            {
                float centreCol = rng.NextFloat() * width;
                float centreRow = rng.NextFloat() * height;
                // forests grow on grass patches: reject centres on dirt. 0.5 is the same
                // cutoff the shader's smoothstep blends around, so trees match the visual.
                if (GrassWeight(new Vector2Int((int)centreCol, (int)centreRow)) < 0.5f)
                    continue;
                int trees = rng.NextInt(minTrees, maxTrees + 1);
                // we deduce the required radius based on how many trees we need in this patch.
                float radius = Mathf.Sqrt(trees / (Mathf.PI * packing)); // packing need to be tuned, approaches 1.0 density at large number.
                for (int tree = 0; tree < trees && planted < budget; tree++)
                {
                    float angle = rng.NextFloat() * Mathf.PI * 2f;
                    float distance = Mathf.Sqrt(rng.NextFloat()) * radius;
                    var tile = new Vector2Int(
                        Mathf.FloorToInt(centreCol + Mathf.Cos(angle) * distance),
                        Mathf.FloorToInt(centreRow + Mathf.Sin(angle) * distance)
                    );
                    if (!IsInsideGrid(tile) || !tiles[tile.x, tile.y].Walkable
                        || GrassWeight(tile) < 0.5f)
                        continue;
                    tiles[tile.x, tile.y].HasTree = true;
                    tiles[tile.x, tile.y].Walkable = false;
                    planted++;
                }
            }
        }

        public bool HasTree(Vector2Int tile)
        {
            if (tiles == null)
                BuildTiles();
            return IsInsideGrid(tile) && tiles[tile.x, tile.y].HasTree;
        }

        // felling is the only runtime tile mutation.
        public void RemoveTree(Vector2Int tile)
        {
            if (!HasTree(tile))
                return;
            tiles[tile.x, tile.y].HasTree = false;
            tiles[tile.x, tile.y].Walkable = true;
            if (TreeFelled != null)
                TreeFelled(tile);
        }

        // per-tile look for renderers, not seeded on treeSeed so replanting does not repaint trees that stayed put.
        public Random TreeRng(Vector2Int tile)
        {
            return Random.CreateFromIndex((uint)(tile.x + tile.y * width));
        }

        // walk by small steps (roughly 0.5units), then sample the tiles on each step.
        // for each step, we check if the tile has trees. if so, we check if the shell can hit given the tree's radius
        public bool TryFindTreeAlongSegment(
            Vector3 fromWorld,
            Vector3 toWorld,
            out float distance,
            out Vector2Int hitTile
        )
        {
            distance = float.MaxValue; // initialize
            hitTile = new Vector2Int(0, 0); // initialize
            if (tiles == null)
                BuildTiles();

            GridCornersBeforeTransform(out float x0, out _, out float z0, out _);
            Vector3 from = transform.InverseTransformPoint(fromWorld); //  'from' is global, and we trying to get its local position relative to tilegrid
            Vector3 to = transform.InverseTransformPoint(toWorld);

            // convert to grid space: - x0 > reorigin from grid centre to grid corner (x0 is -halfW, so this adds halfW); / tileSize > meters to tiles.
            var start = new Vector2((from.x - x0) / tileSize, (from.z - z0) / tileSize);
            var displacement = new Vector2((to.x - x0) / tileSize, (to.z - z0) / tileSize) - start; // technically is the shell shot's displacement

            const float sampleStep = 0.5f; // tile units between samples. lower is safer. bigger might pass through tile corners and accidentally miss tree tiles (mostly only a prob if tree radius is large)
            int steps = Mathf.CeilToInt(displacement.magnitude / sampleStep);
            steps = Mathf.Max(steps, 1); // min 1
            for (int step = 0; step <= steps; step++)
            {
                Vector2 point = start + displacement * ((float)step / steps);
                int col = Mathf.FloorToInt(point.x);
                int row = Mathf.FloorToInt(point.y);
                if (col < 0 || col >= width || row < 0 || row >= height || !tiles[col, row].HasTree)
                    continue;

                // closest approach of the line to the trunk, clamped to the segment for shots that end short of it.
                // the Max guards the zero-length segment against dividing by zero.
                var trunk = new Vector2(col + 0.5f, row + 0.5f);
                float closest = Mathf.Clamp(
                    Vector2.Dot(trunk - start, displacement) / Mathf.Max(displacement.sqrMagnitude, 1e-12f),
                    0f,
                    1f
                );
                if ((start + displacement * closest - trunk).sqrMagnitude > trunkRadius * trunkRadius)
                    continue; // passes wide of this trunk; a later tree may still stand in the way

                hitTile = new Vector2Int(col, row);
                // closest is a fraction of the line, which is the same fraction in 3D as on the
                // ground plane. scaling by the 3D length keeps this comparable to a shell's
                // distanceTraveled, which runs along a barrel-height-to-y=0 slope.
                distance = closest * (toWorld - fromWorld).magnitude;
                return true;
            }

            return false;
        }
    }
}
