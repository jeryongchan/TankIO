using Unity.Netcode;
using UnityEngine;

namespace TankIO
{
    // a 3x3 building (currently a pad) you dock your HQ on
    // A HQ docked here is still the HQ in every system that matters - shells, garrison fire, troops
    public class CapitalController : NetworkBehaviour
    {
        public static CapitalController Instance { get; private set; }

        private const double HoldCheckInterval = 0.3; // same cadence as garrison and targeting

        public struct HoldState : INetworkSerializable
        {
            public bool Held;
            public ulong HolderCommanderId;
            public double HoldStartTime;

            public void NetworkSerialize<T>(BufferSerializer<T> serializer) where T : IReaderWriter
            {
                serializer.SerializeValue(ref Held);
                serializer.SerializeValue(ref HolderCommanderId);
                serializer.SerializeValue(ref HoldStartTime);
            }
        }

        // one replicated event per capture, never per tick: clients derive the running clock from
        // HoldStartTime the same way gold and troops derive their balances.
        private readonly NetworkVariable<HoldState> replicatedHoldState = new NetworkVariable<HoldState>();

        private double holdCheckTimer;
        private Vector2Int centerTile;

        public Vector2Int CenterTile
        {
            get { return centerTile; }
        }

        public override void OnNetworkSpawn()
        {
            Instance = this;
            TileGrid.Instance.WorldToTile(transform.position, out centerTile);
        }

        public override void OnNetworkDespawn()
        {
            if (Instance == this)
                Instance = null;
        }

        void Update()
        {
            if (!IsSpawned) // Update runs on the instantiated-but-unspawned frame, when NetworkManager is null
                return;
            if (IsServer)
                UpdateHolder(NetworkManager.ServerTime.Time);
        }

        // capture is "whose HQ is parked on my centre tile", asked once every 0.3s. no capture timer and
        // no contest state: the HQ move already made taking the centre slow and visible, so a second
        // layer of delay would only hide the moment the pad changes hands.
        void UpdateHolder(double now)
        {
            if (now < holdCheckTimer)
                return;
            holdCheckTimer = now + HoldCheckInterval;

            HqController holder = DockedHq(now);
            HoldState current = replicatedHoldState.Value;

            if (holder == null)
            {
                if (current.Held)
                    replicatedHoldState.Value = new HoldState { Held = false };
                return;
            }
            if (current.Held && current.HolderCommanderId == holder.CommanderId)
                return; // same holder, keep the clock running

            replicatedHoldState.Value = new HoldState
            {
                Held = true,
                HolderCommanderId = holder.CommanderId,
                HoldStartTime = now
            };
        }

        HqController DockedHq(double now)
        {
            foreach (HqController hq in HqController.SpawnedHqs)
            {
                if (hq.IsParked(now) && hq.HomeTile == centerTile)
                    return hq;
            }
            return null;
        }

        // the building's 9 tiles. they stay walkable in the grid. 
        // A HQ docks by parking its footprint on them, but tanks treat them as a solid building: no driving through, no stopping on them.
        public static bool CoversTile(Vector2Int tile)
        {
            return Instance != null && TileGap(tile, Instance.centerTile) <= HqController.FootprintRadius;
        }

        // clicking on any tile of the capital's 9 tiles will dock here
        public static Vector2Int SnapToDock(Vector2Int tile)
        {
            if (Instance == null)
                return tile;
            return TileGap(tile, Instance.centerTile) <= HqController.FootprintRadius ? Instance.centerTile : tile;
        }

        // can the HQ stays on this tile without partial overlap with capital? after we have refinery, need to abstract this
        public static bool AllowsHqAt(Vector2Int centerTile)
        {
            if (Instance == null || centerTile == Instance.centerTile)
                return true;
            return TileGap(centerTile, Instance.centerTile) > HqController.FootprintRadius * 2;
        }

        static int TileGap(Vector2Int a, Vector2Int b)
        {
            return Mathf.Max(Mathf.Abs(a.x - b.x), Mathf.Abs(a.y - b.y));
        }

        public bool IsHeldBy(ulong commanderId)
        {
            HoldState state = replicatedHoldState.Value;
            return state.Held && state.HolderCommanderId == commanderId;
        }

        public double HoldSeconds(double now)
        {
            HoldState state = replicatedHoldState.Value;
            return state.Held ? now - state.HoldStartTime : 0.0;
        }
    }
}
