using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

namespace TankIO
{
    // original tree placement is derived from TileGrid seed.
    // only the delta (destroyed trees) travel. health is server-only.
    public class TreeSystem : NetworkBehaviour
    {
        public static TreeSystem Instance { get; private set; }

        [SerializeField, Min(1)]
        private int hitsToFell = 4; // hits, not damage

        // only trees that have taken a hit, so a 100k-tree map costs nothing until someone shoots one. needs dict cuz stores number of hits taken (if its not already obvious duh)
        private readonly Dictionary<int, int> damagedTrees = new Dictionary<int, int>();

        // every tree felled this session, each client's tilegrid hold the same information
        private readonly HashSet<int> felledTrees = new HashSet<int>();

        void Awake()
        {
            Instance = this;
        }

        public override void OnNetworkSpawn()
        {
            if (!IsServer)
                RequestFelledTreesRpc();
        }

        public override void OnNetworkDespawn()
        {
            damagedTrees.Clear();
            felledTrees.Clear();
        }

        int PackTile(Vector2Int tile)
        {
            return tile.x + tile.y * TileGrid.Instance.Width;
        }

        Vector2Int UnpackTile(int packed)
        {
            int width = TileGrid.Instance.Width;
            return new Vector2Int(packed % width, packed / width);
        }

        public void RegisterShellHit(Vector2Int tile)
        {
            int packed = PackTile(tile);
            damagedTrees.TryGetValue(packed, out int hits);
            hits++;

            if (hits < hitsToFell)
            {
                damagedTrees[packed] = hits;
                return;
            }
            damagedTrees.Remove(packed);
            FellTree(packed);
            FellTreeRpc(packed);
        }

        // idempotent, because RemoveTree ignores a tile with no tree
        void FellTree(int packed)
        {
            TileGrid.Instance.RemoveTree(UnpackTile(packed));
            if (IsServer)
                felledTrees.Add(packed);
        }

        // every client (so we can do prop streaming), not just those subscribed to the region.
        [Rpc(SendTo.ClientsAndHost)]
        void FellTreeRpc(int packed)
        {
            FellTree(packed);
        }

        [Rpc(SendTo.Server)]
        void RequestFelledTreesRpc(RpcParams serverParams = default)
        {
            if (felledTrees.Count == 0)
                return;
            BaseRpcTarget target = RpcTarget.Single(serverParams.Receive.SenderClientId, RpcTargetUse.Temp);
            // one rpc for the whole set.
            // current transport max payload is 1500 trees at the default 6KB. batch this if future players actually fell so many trees.
            var packedTreeTiles = new int[felledTrees.Count];
            felledTrees.CopyTo(packedTreeTiles);
            FelledTreesRpc(packedTreeTiles, target);
        }

        [Rpc(SendTo.SpecifiedInParams)] // need to receive with custom func because trees are not network objects (unlike receiving HQ of all players)
        void FelledTreesRpc(int[] packedTreeTiles, RpcParams clientParams)
        {
            foreach (int packed in packedTreeTiles)
                FellTree(packed);
        }
    }
}
