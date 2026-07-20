using Unity.Netcode;
using UnityEngine;

namespace TankIO
{
    // Default Player Prefab is set to none and we spawn here instead.
    public class TankSpawner : MonoBehaviour
    {
        [SerializeField]
        private GameObject tankPrefab;

        private const int TanksPerPlayer = 12;

        void Start()
        {
            NetworkManager.Singleton.OnClientConnectedCallback += SpawnTanksFor;
        }

        void OnDestroy()
        {
            if (NetworkManager.Singleton != null) // teardown order on play mode exit is not guaranteed
                NetworkManager.Singleton.OnClientConnectedCallback -= SpawnTanksFor;
        }

        void SpawnTanksFor(ulong clientId)
        {
            if (!NetworkManager.Singleton.IsServer)
                return; // the callback also runs on the connecting client, which owns nothing yet

            for (int tankIndex = 0; tankIndex < TanksPerPlayer; tankIndex++)
            {
                GameObject tank = Instantiate(tankPrefab);
                tank.transform.position = SpawnPosition(tank.transform.position.y, clientId, tankIndex);
                tank.GetComponent<NetworkObject>().SpawnWithOwnership(clientId);
            }
        }

        // temporary: players spread along x, each player's tanks along z. later move to edge spawn on the concentric map.
        static Vector3 SpawnPosition(float tankHeight, ulong clientId, int tankIndex)
        {
            Vector2Int center = new Vector2Int(TileGrid.Instance.Width / 2, TileGrid.Instance.Height / 2);
            Vector2Int tile = center + new Vector2Int((int)clientId * 6 - 3, tankIndex - 1);
            Vector3 position = TileGrid.Instance.TileToWorldCenter(tile);
            position.y = tankHeight; // the tank's root sits above the ground, and the trip is built from this point
            return position;
        }
    }
}
