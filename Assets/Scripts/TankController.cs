using System;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.InputSystem;

namespace TankIO
{
    // tanks have no collision or mid-route effects, so position is a closed form:
    //     position(t) = point along trip at moveSpeed * (t - startTime)
    // one message per command covers the whole route, instead of streaming states every tick like a MOBA.
    // client sends command (while also predict its own movement), the server sends the replicated path/trip,
    // the position error (disagreements) are closed smoothly (mostly by speeding up) instead of snapping (unless large gap).

    public class TankController : NetworkBehaviour
    {
        [SerializeField]
        private float moveSpeed = 5f;

        [SerializeField]
        private float turnSpeed = 720f; // degrees per second toward the move direction

        [SerializeField]
        private float maxSpeedMultiplier = 1.25f; // for closing in position errors: the margin above 1 is the error correction's budget.

        private const float PositionErrorSnapDistance = 5f; // a position error this wide is snapped instead of closed in

        // the tank's whole networked movement state
        private readonly NetworkVariable<TripState> replicatedTripState = new NetworkVariable<TripState>(
            new TripState { Path = Array.Empty<Vector2Int>() } // path is reference field, must not be null to be serialized
        );

        private Pathfinder pathfinder;
        private readonly List<Vector2Int> pathBuffer = new List<Vector2Int>();

        private Trip serverTrip; // the replicated trip state, evaluatable
        private Trip predictedTrip; // owner only: the click, run immediately, replaced by the acknowledgement
        private Trip renderedTrip; // which of the two the last frame drew, to notice a swap
        private int lastCommandId; // owner only: the latest click's id, so an old acknowledgement cannot end a newer click's prediction

        private Vector3 positionError;

        private readonly Plane groundPlane = new Plane(Vector3.up, Vector3.zero); // owner
        private Camera mainCamera; // owner

        public override void OnNetworkSpawn()
        {
            if (IsServer || IsOwner)
                pathfinder = new Pathfinder(TileGrid.Instance); // only the server routes commands, only the owner predicts
            replicatedTripState.OnValueChanged += OnTripStateChanged;

            if (IsServer)
            {
                transform.position = SpawnPosition();
                replicatedTripState.Value = new TripState
                {
                    StartPosition = transform.position,
                    Path = Array.Empty<Vector2Int>(),
                    StartTime = 0.0,
                    AcknowledgedCommandId = 0
                };
            }
            else
            {
                // a client fixes small server-clock errors by slewing 1% (imperceptible) and big ones by jumping the clock, which teleports tanks. default was 0.2s. move to client bootstrap later.
                NetworkManager.NetworkTimeSystem.HardResetThresholdSec = 1.0;
                OnTripStateChanged(default, replicatedTripState.Value); // e.g. for late joiner, they will now get the repltripstate of the tank (stored in server before they join)
            }

            if (IsOwner)
            {
                mainCamera = Camera.main;
            }
        }

        public override void OnNetworkDespawn()
        {
            replicatedTripState.OnValueChanged -= OnTripStateChanged;
        }

        void Update()
        {
            if (serverTrip == null)
                return; // not spawned yet

            if (IsOwner)
                HandleMoveCommand();

            Render();
        }

        // owner only submits input command (goal, i.e. target tile),
        // the server derives its own start point from its own clock, roughly a one-way latency further along than ours.
        void HandleMoveCommand()
        {
            Mouse mouse = Mouse.current;
            if (mouse == null || !mouse.leftButton.wasPressedThisFrame)
                return;
            Ray ray = mainCamera.ScreenPointToRay(mouse.position.ReadValue());
            if (!groundPlane.Raycast(ray, out float distance))
                return;
            if (!TileGrid.Instance.WorldToTile(ray.GetPoint(distance), out Vector2Int goal))
                return; // clicked outside the grid

            lastCommandId++;

            double clickTime = NetworkManager.ServerTime.Time;
            Trip currentTrip = predictedTrip ?? serverTrip;
            Vector3 startPosition = PositionAtTime(currentTrip, clickTime);
            predictedTrip = PredictTrip(startPosition, goal, clickTime);

            SubmitMoveCommandRpc(goal, lastCommandId);
        }

        [Rpc(SendTo.Server)]
        void SubmitMoveCommandRpc(Vector2Int goal, int commandId)
        {
            double now = NetworkManager.ServerTime.Time;

            // the start is wherever the server's own trip puts the tank right now, so movement state never depends
            // on anything the client sends. the owner set off ~one-way latency earlier; that gap comes back with the
            // acknowledgement and the error smoothing closes it.
            Vector3 startPosition = PositionAtTime(serverTrip, now);
            replicatedTripState.Value = new TripState
            {
                StartPosition = startPosition,
                Path = ComputePath(startPosition, goal),
                StartTime = now,
                AcknowledgedCommandId = commandId
            };
        }

        void OnTripStateChanged(TripState previousState, TripState newState)
        {
            serverTrip = TripFromState(newState);
            // the acknowledgement for our click (or a newer one) arrived. the server stamped its start a round trip after we set off,
            // so adopting it would rewind the tank; keep ours, and take the server's only when we never predicted.
            // an acknowledgement for an older click leaves the newer prediction alone.
            if (IsOwner && newState.AcknowledgedCommandId >= lastCommandId)
            {
                if (predictedTrip != null)
                    serverTrip.startTime = predictedTrip.startTime;
                predictedTrip = null;
            }
        }

        // owner: path the click locally and drive it as the trip the server will answer with. the server's answer
        // replaces it; a rare wrong guess just opens a position error.
        Trip PredictTrip(Vector3 startPosition, Vector2Int goal, double startTime)
        {
            return TripFromState(
                new TripState
                {
                    StartPosition = startPosition,
                    Path = ComputePath(startPosition, goal),
                    StartTime = startTime
                }
            );
        }

        // the A* tiles after the start point. the start tile is skipped so the tank sets off toward the next tile, not back to prev's centre.
        Vector2Int[] ComputePath(Vector3 startPosition, Vector2Int goal)
        {
            if (
                TileGrid.Instance.WorldToTile(startPosition, out Vector2Int start)
                && pathfinder.FindPath(start, goal, pathBuffer)
            )
            {
                Vector2Int[] path = new Vector2Int[pathBuffer.Count - 1];
                for (int index = 1; index < pathBuffer.Count; index++)
                    path[index - 1] = pathBuffer[index];
                return path;
            }
            return Array.Empty<Vector2Int>();
        }

        // turns the tile path into world points the tank can drive. no pathfinding here, just conversion.
        Trip TripFromState(TripState state)
        {
            Trip trip = new Trip();
            trip.startTime = state.StartTime;
            trip.points = new List<Vector3> { state.StartPosition };
            foreach (Vector2Int tile in state.Path)
            {
                Vector3 point = TileGrid.Instance.TileToWorldCenter(tile);
                point.y = state.StartPosition.y; // unless you can make sure your tank is always y=0
                trip.points.Add(point);
            }
            return trip;
        }

        // the pure function: where along the trip the tank is at the given time.
        Vector3 PositionAtTime(Trip trip, double time)
        {
            float distanceDriven = moveSpeed * (float)Math.Max(0.0, time - trip.startTime);
            return PositionAtDistance(trip, distanceDriven);
        }

        Vector3 PositionAtDistance(Trip trip, float distanceDriven)
        {
            float remainingDistance = distanceDriven;
            Vector3 current = trip.points[0];
            for (int index = 1; index < trip.points.Count; index++)
            {
                Vector3 toPoint = trip.points[index] - current;
                float segmentLength = toPoint.magnitude;
                if (remainingDistance <= segmentLength && segmentLength > 0f)
                    return current + toPoint / segmentLength * remainingDistance;
                remainingDistance -= segmentLength;
                current = trip.points[index];
            }
            return current; // arrived, or have never left
        }

        void Render()
        {
            // where the trip puts the tank right now. the owner drives its prediction until the acknowledgement lands; everyone else drives the replicated trip.
            Trip trip = predictedTrip ?? serverTrip;
            Vector3 authoritativePosition = PositionAtTime(trip, NetworkManager.ServerTime.Time);
            // in case when trip swapped, like predicted to server, we can note down the gap as an error instead of teleporting the tank
            if (trip != renderedTrip)
            {
                if (renderedTrip != null)
                    positionError = transform.position - authoritativePosition;
                renderedTrip = trip;
            }
            if (positionError.magnitude > PositionErrorSnapDistance)
                positionError = Vector3.zero; // sliding that far reads worse than the jump

            // authoritativeArrived means if it has arrived at the true goal; in that case we use full cap speed to close the position error
            bool authoritativeArrived = authoritativePosition == trip.points[trip.points.Count - 1]; // unity vec3 already has ~1e-5 tolerance
            float closeSpeed = moveSpeed * (authoritativeArrived ? maxSpeedMultiplier : maxSpeedMultiplier - 1f);
            positionError = Vector3.MoveTowards(positionError, Vector3.zero, closeSpeed * Time.deltaTime);
            Vector3 renderPosition = authoritativePosition + positionError;
            Vector3 renderDisplacement = renderPosition - transform.position;
            transform.position = renderPosition;
            // determine tank facing direction based on render positions, currently doesnt sync across clients
            if (renderDisplacement.magnitude > 0.001f)
            {
                Quaternion targetRotation = Quaternion.LookRotation(renderDisplacement.normalized, Vector3.up);
                transform.rotation = Quaternion.RotateTowards(
                    transform.rotation,
                    targetRotation,
                    turnSpeed * Time.deltaTime
                );
            }
        }

        // temporary spawn logic. later move to edge spawn when have concentric map
        Vector3 SpawnPosition()
        {
            Vector2Int tile = new Vector2Int(2 + (int)OwnerClientId * 3, 2);
            Vector3 position = TileGrid.Instance.TileToWorldCenter(tile);
            position.y = transform.position.y;
            return position;
        }

        // a path with a start time, so it can be evaluated at any time.
        // world points rather than tiles, and it begins at the tank's exact position instead of a tile centre.
        private class Trip
        {
            public double startTime;
            public List<Vector3> points;
        }

        // a trip compacted for network: tiles instead of world points. server writes, everyone reads.
        struct TripState : INetworkSerializable
        {
            public Vector3 StartPosition; // derived by the server from the previous trip, not sent by client
            public Vector2Int[] Path; // the server's A* tiles after the start point; the last one is the goal
            public double StartTime; // server clock seconds
            public int AcknowledgedCommandId; // the click this trip answers, so the owner knows when its prediction is done

            public void NetworkSerialize<T>(BufferSerializer<T> serializer) where T : IReaderWriter
            {
                serializer.SerializeValue(ref StartPosition);
                int tileCount = serializer.IsWriter ? Path.Length : 0;
                serializer.SerializeValue(ref tileCount);
                if (serializer.IsReader)
                    Path = new Vector2Int[tileCount];
                for (int index = 0; index < tileCount; index++)
                    serializer.SerializeValue(ref Path[index]);
                serializer.SerializeValue(ref StartTime);
                serializer.SerializeValue(ref AcknowledgedCommandId);
            }
        }
    }
}
