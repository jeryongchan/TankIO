using System.Collections.Generic;
using UnityEngine;
using Random = Unity.Mathematics.Random; // aliased, or it collides with UnityEngine.Random

namespace TankIO
{
    // one prefab instance per tree tile. to optimize
    [ExecuteAlways]
    [RequireComponent(typeof(TileGrid))]
    public class TreeRenderer : MonoBehaviour
    {
        [SerializeField]
        private GameObject[] treePrefabs; // the variant is a hash of the tile, so reordering reshuffles the forest

        [SerializeField, Range(0f, 0.5f)]
        private float positionJitter = 0.2f; // offset from tile centre (fraction of tile size)

        [SerializeField, Range(0f, 0.5f)]
        private float scaleJitter = 0.1f;

        private TileGrid tileGrid;
        private readonly Dictionary<Vector2Int, GameObject> spawned = new Dictionary<Vector2Int, GameObject>(); // all trees spawned by renderer in entire map
        private Transform container;

        void OnEnable()
        {
            tileGrid = GetComponent<TileGrid>();
            tileGrid.GridChanged += Rebuild;
            tileGrid.TreeFelled += Remove;
            Rebuild();
        }

        void OnDisable()
        {
            tileGrid.GridChanged -= Rebuild;
            tileGrid.TreeFelled -= Remove;
            Clear();
        }

        void Rebuild()
        {
            Clear();
            if (treePrefabs == null || treePrefabs.Length == 0)
                return;
            if (Application.isBatchMode)
                return; // headless server: the tile data is the authority, nothing needs drawing

            container = new GameObject("Trees") { hideFlags = HideFlags.DontSave }.transform;
            container.SetParent(transform, false);

            for (int row = 0; row < tileGrid.Height; row++)
            {
                for (int col = 0; col < tileGrid.Width; col++)
                {
                    var tile = new Vector2Int(col, row);
                    if (!tileGrid.HasTree(tile))
                        continue;
                    Spawn(tile);
                }
            }
        }

        void Spawn(Vector2Int tile)
        {
            Random rng = tileGrid.TreeRng(tile);
            int variant = rng.NextInt(treePrefabs.Length);
            float jitterX = rng.NextFloat(-1f, 1f);
            float jitterZ = rng.NextFloat(-1f, 1f);
            float rotationY = rng.NextFloat(360f);
            float jitterScale = 1f + rng.NextFloat(-1f, 1f) * scaleJitter;

            GameObject prefab = treePrefabs[variant];
            if (prefab == null)
                return;

            float tileSize = tileGrid.TileSize;
            Vector3 local =
                tileGrid.TileToLocalCenter(tile)
                + new Vector3(jitterX * positionJitter * tileSize, 0f, jitterZ * positionJitter * tileSize);

            GameObject instance = Instantiate(prefab, container);
            instance.transform.localPosition = local;
            instance.transform.localRotation = Quaternion.Euler(0f, rotationY, 0f);
            // the prefab carries its own size; jitter only varies it
            instance.transform.localScale = prefab.transform.localScale * jitterScale;
            instance.hideFlags = HideFlags.DontSave;
            spawned[tile] = instance;
        }

        void Remove(Vector2Int tile)
        {
            if (!spawned.TryGetValue(tile, out GameObject instance))
                return;
            spawned.Remove(tile);
            Discard(instance);
        }

        void Clear()
        {
            spawned.Clear();
            if (container != null)
                Discard(container.gameObject);
            container = null;
        }

        // ExecuteAlways means this runs in edit mode too, where Destroy never happens.
        static void Discard(GameObject target)
        {
            if (target == null)
                return;
            if (Application.isPlaying)
                Destroy(target);
            else
                DestroyImmediate(target);
        }
    }
}
