using System.Reflection;
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
            // every ServerSetCommanderBeforeSpawn write logs "doesn't know its NetworkBehaviour yet",
            // which is harmless for us, the value ships in the spawn payload. NGO's own tests silence it with this internal flag; reflection because internal.
            // if an NGO update renames the field the ?. skips quietly and the warnings come back.
            typeof(NetworkVariableBase)
                .GetField("IgnoreInitializeWarning", BindingFlags.Static | BindingFlags.NonPublic)
                ?.SetValue(null, true);

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

        // the capital (and later refineries): once per map, not per player. the bots come up here too,
        // before anyone connects, spread from near the capital out to the rim.
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

            BotManager.Instance.SpawnBots(SpawnHq);
        }

        void SpawnHqFor(ulong clientId)
        {
            if (!NetworkManager.Singleton.IsServer) // we dont call IsServer directly because this is not networkbehaviour!
                return; // the callback also runs on the connecting client, which owns nothing yet
            SpawnHq(clientId, clientId, 1f); // a human commands through their own connection, and always starts on the rim
        }

        // commanderId is who this HQ fights for; ownerClientId is the connection allowed to send its RPCs.
        // they are the same for a human. a bot's HQ is server-owned and driven by BotManager calling the
        // Server* methods directly, so it needs no connection at all, hence the two ids.
        // spawnDepth: 0 = beside the capital, 1 = the rim.
        // bots spawn at varying depths so a joining player finds a world already in progress.
        void SpawnHq(ulong commanderId, ulong ownerClientId, float spawnDepth)
        {
            if (hqPrefab == null)
            {
                Debug.LogError("Yet to provide HQ prefab will spawn");
                return;
            }
            GameObject hq = Instantiate(hqPrefab);
            hq.transform.position = TileGrid.Instance.TileToWorldCenter(HqSpawnTile(spawnDepth));
            hq.GetComponent<HqController>().ServerSetCommanderBeforeSpawn(commanderId);
            hq.GetComponent<NetworkObject>().SpawnWithOwnership(ownerClientId);
        }

        // the ring at the requested depth, then furthest from everyone else's HQ.
        // a ring with no room falls inward to the next one.
        Vector2Int HqSpawnTile(float spawnDepth)
        {
            TileGrid grid = TileGrid.Instance;
            Vector2 center = grid.CenterTileSpace;
            float rimRadius = grid.Radius - (HqController.FootprintRadius + 1); // the footprint fits inside the rim
            float minRadius = HqController.FootprintRadius * 2 + 2; // innermost ring whose footprint clears the capital's
            float startRadius = Mathf.Lerp(minRadius, rimRadius, spawnDepth);

            for (float radius = startRadius; radius >= 1f; radius -= 1f)
            {
                Vector2Int best = default;
                float bestDistanceSquared = -1f;
                int candidateCount = Mathf.CeilToInt(2f * Mathf.PI * radius); // one candidate per tile of arc
                for (int index = 0; index < candidateCount; index++)
                {
                    Vector2Int tile = TileAt(center, index * 2f * Mathf.PI / candidateCount, radius);
                    if (!HqController.IsFootprintFree(tile, 0))
                        continue;
                    if (!CapitalController.AllowsHqAt(tile)) // deep rings can reach the capital; rim ones never could
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
            return TileAt(center, 0f, startRadius); // disc is full: overlapping beats no HQ at all
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
