using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

namespace TankIO
{
    // the move transaction: validate > pay > relocate, replicated as one state every machine plays out on its own clock (pack, glide, unpack).
    // no trips, no pathfinding, no prediction
    //
    // the 3x3 footprint lives in the reservation table as parked claims, so tanks route around hq.
    // confirming a move moves the actual HQ position (not visual) straight to the destination, so the landing spot cannot be taken, maybe in future put some marker on ground for arriving HQ.
    public partial class HqController
    {
        private const float PackSeconds = 1f;
        private const float TravelSpeed = 2f; // metres per second, straight glide; ignores terrain (the traveling HQ is a non-entity)
        private const float TransitScale = 0.55f; // stand-in for the pack animation: the packed vehicle is smaller than the building

        // placeholder pricing, moves get expensive toward the centre
        private const float MoveCostPerTile = 5f;
        private const float MoveCostCenterMultiplier = 10f; // a tile of movement at the centre costs this many times the edge price

        // the whole networked move state; the phase at any moment is derived from the two times
        private struct MoveState : INetworkSerializable
        {
            public Vector2Int FromTile;
            public Vector2Int ToTile;
            public double DepartTime; // pack-complete: the origin frees and the glide begins
            public double ArriveTime;

            public bool IsParkedAt(double time)
            {
                return time >= ArriveTime;
            }

            public bool InTransitAt(double time)
            {
                return time > DepartTime && time < ArriveTime;
            }

            // the footprint centre of whichever endpoint the HQ stands on; the line between them mid-glide
            public Vector3 PositionAtTime(double time)
            {
                if (time <= DepartTime)
                    return TileGrid.Instance.TileToWorldCenter(FromTile);
                if (time >= ArriveTime)
                    return TileGrid.Instance.TileToWorldCenter(ToTile);
                float travelled = (float)((time - DepartTime) / (ArriveTime - DepartTime));
                return Vector3.Lerp(
                    TileGrid.Instance.TileToWorldCenter(FromTile),
                    TileGrid.Instance.TileToWorldCenter(ToTile),
                    travelled
                );
            }

            public void NetworkSerialize<T>(BufferSerializer<T> serializer) where T : IReaderWriter
            {
                serializer.SerializeValue(ref FromTile);
                serializer.SerializeValue(ref ToTile);
                serializer.SerializeValue(ref DepartTime);
                serializer.SerializeValue(ref ArriveTime);
            }
        }

        private readonly NetworkVariable<MoveState> replicatedMoveState = new NetworkVariable<MoveState>();

        private bool arrivalApplied;

        private Vector3 authoredScale; // the prefab's scale; the transit shrink multiplies it instead of replacing it
        private Renderer[] renderers;
        private bool visible = true;

        private static readonly List<Vector2Int> footprintBuffer = new List<Vector2Int>();
        private static readonly List<ulong> tanksToRepath = new List<ulong>();

        // every machine mirrors the same bookkeeping, clients so their pathfinding prediction sees the
        // footprint the server routes around.
        void OnMoveStateChanged(MoveState previousState, MoveState state)
        {
            double now = NetworkManager.ServerTime.Time;
            TripReservations.Release(NetworkObjectId);
            FootprintTiles(state.ToTile, footprintBuffer);
            TripReservations.AddParkedTiles(NetworkObjectId, footprintBuffer, now);
            arrivalApplied = false;

            // wrecks driving home to this HQ turn toward the new destination (covers knockback too,
            // which moves through the same state)
            WreckVisual.RetargetFor(CommanderId, TileGrid.Instance.TileToWorldCenter(state.ToTile));
        }

        // the one one-shot moment of a move, replayed by every machine on its own clock
        void UpdateMoveTransitions(MoveState state, double now)
        {
            if (!arrivalApplied && now >= state.ArriveTime)
            {
                if (IsServer)
                {
                    SetGoldRate(GoldRateAt(state.ToTile), now);
                    SetTroopRate(TroopRegenPerSecond, now);
                }
                arrivalApplied = true;
            }
        }

        // owner input path, called by PlayerCommander. no prediction: the transaction runs on the server
        // and the pack animation starting late covers the round trip.
        public void RequestMove(Vector2Int targetTile)
        {
            SubmitMoveRpc(targetTile);
        }

        [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Owner)]
        void SubmitMoveRpc(Vector2Int targetTile)
        {
            ExecuteMove(targetTile);
        }

        public void ExecuteMove(Vector2Int targetTile)
        {
            double now = NetworkManager.ServerTime.Time;
            targetTile = CapitalController.SnapToDock(targetTile); // the client snapped for its preview; the server owns the decision
            if (!IsParked(now))
                return; // no cancel, no queue: nothing new until the move in progress lands
            if (!IsValidDestination(targetTile))
                return;
            float cost = MoveCost(HomeTile, targetTile);
            if (Gold(now) < cost)
                return;

            Spend(cost, now);
            BeginMove(targetTile, now + PackSeconds, TravelSpeed, now);
        }

        void BeginMove(Vector2Int toTile, double departTime, float glideSpeed, double now)
        {
            SetGoldRate(0f, now);
            SetTroopRate(0f, now);
            RetargetPendingReturns(toTile, now); // wreck arrival times follow the move; the visuals follow in OnMoveStateChanged

            Vector2Int fromTile = replicatedMoveState.Value.ToTile;
            Vector3 fromCentre = TileGrid.Instance.TileToWorldCenter(fromTile);
            Vector3 toCentre = TileGrid.Instance.TileToWorldCenter(toTile);
            replicatedMoveState.Value = new MoveState
            {
                FromTile = fromTile,
                ToTile = toTile,
                DepartTime = departTime,
                ArriveTime = departTime + (toCentre - fromCentre).magnitude / glideSpeed
            };

            FootprintTiles(toTile, footprintBuffer);
            TripReservations.FindTanksCrossingTiles(footprintBuffer, now, tanksToRepath); // find which tanks will path/park onto the 9 landing tiles
            foreach (ulong tankId in tanksToRepath)
            {
                TankController crossingTank = TankController.TankFromObjectId(tankId);
                if (crossingTank != null)
                    crossingTank.RepathAroundPark(now); // ask each to repath around the new HQ tiles
            }
        }

        void Render(MoveState state, double now)
        {
            bool inTransit = state.InTransitAt(now);
            transform.position = state.PositionAtTime(now);
            transform.localScale = authoredScale * (inTransit ? TransitScale : 1f);
            SetVisible(!IsDockedAtCapital(now));
            RenderGarrisonTracer();
        }

        // packed HQ vanish into capital and dock there.
        // purely visual, all HQ functionality stays.
        public bool IsDockedAtCapital(double now)
        {
            return CapitalController.Instance != null
                && IsParked(now)
                && HomeTile == CapitalController.Instance.CenterTile;
        }

        void SetVisible(bool shouldBeVisible)
        {
            if (visible == shouldBeVisible)
                return;
            visible = shouldBeVisible;
            foreach (Renderer meshRenderer in renderers)
                meshRenderer.enabled = shouldBeVisible;
        }

        public bool IsParked(double now)
        {
            return replicatedMoveState.Value.IsParkedAt(now);
        }

        // the current tile HQ is on, once a move is confirmed (income etc will be based on this)
        public Vector2Int HomeTile
        {
            get { return replicatedMoveState.Value.ToTile; }
        }

        // the commander's hover preview asks the same question the server's confirm will
        public bool IsValidDestination(Vector2Int tile)
        {
            return tile != HomeTile && CapitalController.AllowsHqAt(tile) && IsFootprintFree(tile, NetworkObjectId);
        }

        // gets more expensive to move as you get deeper, squared. retreating is cheap
        public static float MoveCost(Vector2Int fromTile, Vector2Int toTile)
        {
            float depth = TileGrid.Instance.RingDepth01(toTile);
            float pricePerTile = MoveCostPerTile * (1f + (MoveCostCenterMultiplier - 1f) * depth * depth);
            return Mathf.Ceil(Vector2Int.Distance(fromTile, toTile) * pricePerTile);
        }
    }
}
