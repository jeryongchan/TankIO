using Unity.Netcode;
using UnityEngine;

namespace TankIO
{
    // everything that enters the world by itself: the capital when the server comes up, one HQ per
    // player on connect. Default Player Prefab is set to none and we spawn here instead.
    public class WorldSpawner : MonoBehaviour
    {
        [SerializeField]
        private GameObject hqPrefab;

        [SerializeField]
        private GameObject capitalPrefab;

        void Start()
        {
            NetworkManager.Singleton.OnClientConnectedCallback += SpawnHqFor;
            NetworkManager.Singleton.OnServerStarted += SpawnWorld;
            NetworkManager.Singleton.OnClientStarted += WidenClockResetThreshold;
        }

        void OnDestroy()
        {
            if (NetworkManager.Singleton != null) // teardown order on play mode exit is not guaranteed
            {
                NetworkManager.Singleton.OnClientConnectedCallback -= SpawnHqFor;
                NetworkManager.Singleton.OnServerStarted -= SpawnWorld;
                NetworkManager.Singleton.OnClientStarted -= WidenClockResetThreshold;
            }
        }

        // a client fixes small server-clock errors by slewing 1% (imperceptible) and big ones by jumping
        // the clock, which teleports everything evaluated against it: tanks, the HQ glide, shell and wreck visuals. the 0.2s default is jitter-sized.
        // set at connect: this used to live in TankController.OnNetworkSpawn, where an HQ-only client (nothing deployed yet) never got it.
        void WidenClockResetThreshold()
        {
            if (!NetworkManager.Singleton.IsServer)
                NetworkManager.Singleton.NetworkTimeSystem.HardResetThresholdSec = 1.0;
        }

        // the capital (and later refineries): once per map, not per player.
        void SpawnWorld()
        {
            if (capitalPrefab == null)
            {
                Debug.LogError("Yet to provide capital prefab  will spawn.");
                return;
            }
            Vector2Int center = new Vector2Int(TileGrid.Instance.Width / 2, TileGrid.Instance.Height / 2);
            GameObject capital = Instantiate(capitalPrefab);
            capital.transform.position = TileGrid.Instance.TileToWorldCenter(center);
            capital.GetComponent<NetworkObject>().Spawn();
        }

        void SpawnHqFor(ulong clientId)
        {
            if (!NetworkManager.Singleton.IsServer)
                return; // the callback also runs on the connecting client, which owns nothing yet
            if (hqPrefab == null)
            {
                Debug.LogError("Yet to provide HQ prefab will spawn");
                return;
            }
            GameObject hq = Instantiate(hqPrefab);
            hq.transform.position = TileGrid.Instance.TileToWorldCenter(HqSpawnTile());
            hq.GetComponent<NetworkObject>().SpawnWithOwnership(clientId);
        }

        // every player starts on the rim, and pushes inward from there.
        // each joined player will take the furthest rim spot from other players HD.
        Vector2Int HqSpawnTile()
        {
            TileGrid grid = TileGrid.Instance;
            Vector2 center = grid.CenterTileSpace;
            float rimRadius = grid.Radius - (HqController.FootprintRadius + 1); // the footprint fits inside the rim

            for (float radius = rimRadius; radius >= 1f; radius -= 1f)
            {
                Vector2Int best = default;
                float bestDistanceSquared = -1f;
                int candidateCount = Mathf.CeilToInt(2f * Mathf.PI * radius); // one candidate per tile of arc
                for (int index = 0; index < candidateCount; index++)
                {
                    Vector2Int tile = TileAt(center, index * 2f * Mathf.PI / candidateCount, radius);
                    if (!HqController.IsFootprintFree(tile, 0))
                        continue;
                    float distanceSquared = DistanceSquaredToNearestHq(tile);
                    if (distanceSquared > bestDistanceSquared)
                    {
                        best = tile;
                        bestDistanceSquared = distanceSquared;
                    }
                }
                if (bestDistanceSquared >= 0f)
                    return best;
            }
            return TileAt(center, 0f, rimRadius); // disc is full: overlapping at the rim beats no HQ at all
        }

        static Vector2Int TileAt(Vector2 center, float angle, float radius)
        {
            return new Vector2Int(
                Mathf.FloorToInt(center.x + Mathf.Cos(angle) * radius),
                Mathf.FloorToInt(center.y + Mathf.Sin(angle) * radius)
            );
        }

        // float.MaxValue when nobody is out there yet, so the first player takes the first free tile
        static float DistanceSquaredToNearestHq(Vector2Int tile)
        {
            float nearest = float.MaxValue;
            foreach (HqController hq in HqController.SpawnedHqs)
                nearest = Mathf.Min(nearest, (hq.HomeTile - tile).sqrMagnitude);
            return nearest;
        }
    }
}
