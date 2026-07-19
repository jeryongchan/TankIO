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
        private float maxMoveSpeedMultiplier = 1.25f; // for closing in position errors: the margin above 1 is the error correction's budget.

        [SerializeField]
        private Transform turret;

        [SerializeField]
        private Transform muzzle; // shells visually leave from here, the barrel tip

        [SerializeField]
        private float turretTurnSpeed = 180f; // degrees per second toward the target; fire snaps the rest of the way

        private const float PositionErrorSnapDistance = 5f; // a position error this wide is snapped instead of closed in
        private const float AttackRange = 8f;
        private const float IdealFiringDistance = AttackRange * 0.85f; // a chase halts this deep inside range, so the target's jitter can't restart it
        private const float CooldownTime = 2f;
        private const int Damage = 1;
        private const float ShellSpeed = 25f;
        private const float HitRadius = 0.5f;
        private const float AimAngleTolerance = 10f; // a shot waits until the turret bears this close on the aim point
        private const double TargetCheckInterval = 0.3; // how often a tank re-decides who it is shooting at

        // the tank's whole networked movement state
        private readonly NetworkVariable<TripState> replicatedTripState = new NetworkVariable<TripState>(
            new TripState { Path = Array.Empty<Vector2Int>() } // path is reference field, must not be null to be serialized
        );

        private readonly NetworkVariable<int> health = new NetworkVariable<int>(100);

        // held until it dies or command overrides. server writes and replicated so every copy can aim its turret at it. 0 = none.
        private readonly NetworkVariable<ulong> currentTargetId = new NetworkVariable<ulong>();

        private double lastFireTime; // server only, reload gate
        private double targetCheckTimer; // server only
        private int lastAcknowledgedCommandId; // server only: the command the current trip answers, so its own rewrites reuse it
        private Vector3 forceFireAim; // server only, debug tool: the point the turret traverses to and fires once at
        private bool forceFireArmed; // server only
        private Quaternion turretWorldRotation; // last frame's turret world aim, held across hull turns so the turret decouples

        // every machine registers here; ShellSystem scans it to find what a shell met
        public static readonly List<TankController> SpawnedTanks = new List<TankController>();

        // statics outlive a play session when domain reload is off, so tanks from one session would leak into
        // the next. runs before the scene loads on every entry to play mode.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetSessionState()
        {
            SpawnedTanks.Clear();
        }

        private Pathfinder pathfinder;
        private readonly List<Vector2Int> pathBuffer = new List<Vector2Int>();

        private Trip serverTrip; // the replicated trip state, evaluatable
        private Trip predictedTrip; // owner only: the click, run immediately, replaced by the acknowledgement
        private Trip renderedTrip; // which of the two the last frame drew, to notice a swap
        private int lastIssuedCommandId; // owner only: the latest command's id, so an old acknowledgement cannot end a newer command's prediction
        private ulong predictedTargetId; // owner only: the target the owner last commanded, aimed at immediately instead of waiting a round trip for currentTargetId

        private Vector3 positionError;

        private readonly Plane groundPlane = new Plane(Vector3.up, Vector3.zero); // owner
        private Camera mainCamera; // owner

        public override void OnNetworkSpawn()
        {
            if (IsServer || IsOwner)
                pathfinder = new Pathfinder(TileGrid.Instance); // only the server routes commands, only the owner predicts
            replicatedTripState.OnValueChanged += OnTripStateChanged;
            SpawnedTanks.Add(this);
            turretWorldRotation = turret.rotation;

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
            SpawnedTanks.Remove(this);
            replicatedTripState.OnValueChanged -= OnTripStateChanged;
        }

        void Update()
        {
            if (serverTrip == null)
                return; // not spawned yet
            if (IsOwner)
                HandleClickCommand();
            if (IsServer)
            {
                UpdateTargeting();
                if (forceFireArmed && TryFire(0, forceFireAim, NetworkManager.ServerTime.Time))
                    forceFireArmed = false; // one shot per force-fire click, then the turret rests
            }
            Render();
        }

        // left-click an enemy tank to target fire, left-click on empty tile to move.
        void HandleClickCommand()
        {
            Mouse mouse = Mouse.current;
            if (mouse == null || !mouse.leftButton.wasPressedThisFrame)
                return;
            Ray ray = mainCamera.ScreenPointToRay(mouse.position.ReadValue());
            Keyboard keyboard = Keyboard.current;
            if (keyboard != null && keyboard.ctrlKey.isPressed)
            {
                if (groundPlane.Raycast(ray, out float groundDistance))
                    SubmitForceFireCommandRpc(ray.GetPoint(groundDistance));
                return;
            }
            if (Physics.Raycast(ray, out RaycastHit hit))
            {
                TankController target = hit.collider.GetComponentInParent<TankController>();
                if (target != null && target.OwnerClientId != OwnerClientId)
                {
                    HandleAttackCommand(target);
                    return;
                }
            }
            HandleMoveCommand(ray);
        }

        // owner only submits input command (goal, i.e. target tile),
        // the server derives its own start point from its own clock, roughly a one-way latency further along than ours.
        void HandleMoveCommand(Ray ray)
        {
            if (!groundPlane.Raycast(ray, out float distance))
                return;
            if (!TileGrid.Instance.WorldToTile(ray.GetPoint(distance), out Vector2Int goal))
                return; // clicked outside the grid

            lastIssuedCommandId++;
            double clickTime = NetworkManager.ServerTime.Time;
            Trip currentTrip = predictedTrip ?? serverTrip;
            Vector3 startPosition = PositionAtTime(currentTrip, clickTime);
            predictedTrip = PredictTrip(startPosition, goal, clickTime);
            predictedTargetId = 0; // a move order overrides an attack order; drop the aim without waiting for the server

            SubmitMoveCommandRpc(goal, lastIssuedCommandId);
        }

        // the tank chases the target into range, then fires. the owner predicts the initial approach.
        void HandleAttackCommand(TankController target)
        {
            lastIssuedCommandId++;
            double clickTime = NetworkManager.ServerTime.Time;
            Trip currentTrip = predictedTrip ?? serverTrip;
            Vector3 startPosition = PositionAtTime(currentTrip, clickTime);
            Vector3 targetPosition = target.PositionAtTime(target.serverTrip, clickTime);
            Vector2Int[] path =
                (targetPosition - startPosition).magnitude <= IdealFiringDistance
                    ? StopPath(currentTrip, clickTime)
                    : ComputePath(startPosition, FiringTile(startPosition, targetPosition));
            predictedTrip = TripFromState(
                new TripState { StartPosition = startPosition, Path = path, StartTime = clickTime }
            );
            predictedTargetId = target.NetworkObjectId; // the turret starts its traverse now; the traverse masks the round trip

            SubmitAttackCommandRpc(target.NetworkObjectId, lastIssuedCommandId);
        }

        [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Owner)]
        void SubmitMoveCommandRpc(Vector2Int goal, int commandId)
        {
            double now = NetworkManager.ServerTime.Time;
            currentTargetId.Value = 0; // a move order overrides an attack order
            // the start is server calculated based on owner's command. the owner set off ~one-way latency earlier.
            // that gap comes back with the acknowledgement and the error smoothing closes it.
            Vector3 startPosition = PositionAtTime(serverTrip, now);
            WriteTripState(startPosition, ComputePath(startPosition, goal), now, commandId);
        }

        [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Owner)]
        void SubmitAttackCommandRpc(ulong targetObjectId, int commandId)
        {
            double now = NetworkManager.ServerTime.Time;
            TankController target = TankFromObjectId(targetObjectId);
            if (target == null || target.OwnerClientId == OwnerClientId)
                return; // already dead or own tank
            currentTargetId.Value = targetObjectId;
            // always write a trip stamped with this click's id, so it clears the owner's prediction through the ack path 
            // in range: move to the next tile centre, out of range: move to the firing tile.
            if ((target.PositionAtTime(target.serverTrip, now) - PositionAtTime(serverTrip, now)).magnitude <= IdealFiringDistance)
                StopAtNextTile(now, commandId);
            else
                IssueChaseTrip(target, now, commandId);
            TryFire(target.NetworkObjectId, target.PositionAtTime(target.serverTrip, now), now); // first shot right away, not at the next check
        }

        // fires whenever in range (driving or not); drives toward the target only while too far to fire.
        void UpdateTargeting()
        {
            double now = NetworkManager.ServerTime.Time;
            if (now < targetCheckTimer)
                return;
            targetCheckTimer = now + TargetCheckInterval; // tanks spawn at different moments, so checks stagger naturally

            if (currentTargetId.Value == 0)
                return;
            TankController target = TankFromObjectId(currentTargetId.Value);
            if (target == null)
            {
                currentTargetId.Value = 0; // the clicked target died
                StopAtNextTile(now, lastAcknowledgedCommandId); // truncate the chase instead of ghost-driving the rest of the approach
                return;
            }

            Vector3 myPosition = PositionAtTime(serverTrip, now);
            Vector3 targetPosition = target.PositionAtTime(target.serverTrip, now);
            float distance = (targetPosition - myPosition).magnitude;

            // re-path only when the trip no longer ends within range
            // a still target costs no messages, a fleeing one will cost more.
            float tripEndDistanceFromTarget = (targetPosition - serverTrip.EndPoint).magnitude;
            bool tripDeliversFiringPosition = tripEndDistanceFromTarget <= AttackRange;
            if (distance > IdealFiringDistance && !tripDeliversFiringPosition)
                IssueChaseTrip(target, now, lastAcknowledgedCommandId);
            else if (distance <= IdealFiringDistance && !AlreadyStopping(serverTrip, now))
                StopAtNextTile(now, lastAcknowledgedCommandId); // target walked into range: stop early, keep firing

            TryFire(target.NetworkObjectId, targetPosition, now);
        }

        // the tile to attack from: step back from the target to the ideal firing distance along the line to this tank.
        // callers guarantee the tank is farther than that, so the distance is never zero.
        Vector2Int FiringTile(Vector3 selfPosition, Vector3 targetPosition)
        {
            Vector3 targetToSelf = selfPosition - targetPosition;
            Vector3 firingPosition = targetPosition + targetToSelf / targetToSelf.magnitude * IdealFiringDistance;
            TileGrid.Instance.WorldToTile(firingPosition, out Vector2Int tile);
            return tile;
        }

        void IssueChaseTrip(TankController target, double now, int commandId)
        {
            Vector3 startPosition = PositionAtTime(serverTrip, now);
            Vector2Int goal = FiringTile(startPosition, target.PositionAtTime(target.serverTrip, now));
            WriteTripState(startPosition, ComputePath(startPosition, goal), now, commandId);
        }

        // the remaining path of a stopping tank: the next tile centre of its trip, so a rest position always
        // sits on the grid instead of freezing mid-tile. empty when already at rest.
        Vector2Int[] StopPath(Trip trip, double now)
        {
            float remainingDistance = moveSpeed * (float)Math.Max(0.0, now - trip.startTime);
            for (int index = 1; index < trip.points.Count; index++)
            {
                float segmentLength = (trip.points[index] - trip.points[index - 1]).magnitude;
                if (remainingDistance < segmentLength)
                {
                    TileGrid.Instance.WorldToTile(trip.points[index], out Vector2Int tile);
                    return new[] { tile };
                }
                remainingDistance -= segmentLength;
            }
            return Array.Empty<Vector2Int>(); // already at rest
        }

        // stop at the trip's next tile centre, carrying the given ack id so the owner's prediction clears.
        void StopAtNextTile(double now, int commandId)
        {
            Vector3 startPosition = PositionAtTime(serverTrip, now);
            WriteTripState(startPosition, StopPath(serverTrip, now), now, commandId);
        }

        // at rest, or moving to the trip's final tile. rewriting such a trip would change nothing it drives, but the
        // rewrite re-anchors the start at the server's clock — and server rewrites skip the ack-time handover, so a
        // client owner would adopt it a one-way latency behind its own drawn tank and visibly wobble at the rest point.
        bool AlreadyStopping(Trip trip, double now)
        {
            Vector2Int[] remaining = StopPath(trip, now);
            if (remaining.Length == 0)
                return true; // at rest
            TileGrid.Instance.WorldToTile(trip.EndPoint, out Vector2Int endTile);
            return remaining[0] == endTile;
        }

        static TankController TankFromObjectId(ulong objectId)
        {
            if (objectId == 0)
                return null;
            if (
                !NetworkManager.Singleton.SpawnManager.SpawnedObjects.TryGetValue(
                    objectId,
                    out NetworkObject networkObject
                )
            )
                return null;
            return networkObject.GetComponent<TankController>();
        }

        // for debug only; ctrl + click
        [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Owner)]
        void SubmitForceFireCommandRpc(Vector3 aimPoint)
        {
            forceFireAim = aimPoint;
            forceFireArmed = true;
        }

        // server-only. the aim point commits here and never updates, so a target that changes course mid-flight is missed.
        bool TryFire(ulong targetObjectId, Vector3 aimPoint, double now)
        {
            Vector3 muzzlePosition = PositionAtTime(serverTrip, now);
            aimPoint.y = muzzlePosition.y; // shells fly level at tank height; a clicked ground point sits at y=0
            if ((aimPoint - muzzlePosition).sqrMagnitude > AttackRange * AttackRange)
                return false;
            if (Vector3.Angle(turret.forward, aimPoint - muzzlePosition) > AimAngleTolerance) // rmb this is server value! so turret gating is based on server
                return false;
            if (now - lastFireTime < CooldownTime)
                return false;
            lastFireTime = now;
            int shellId = ShellSystem.Instance.Fire(
                muzzlePosition,
                aimPoint,
                now,
                OwnerClientId,
                ShellSpeed,
                HitRadius,
                Damage
            );
            FiredRpc(shellId, targetObjectId, aimPoint, now); // broadcast to clients
            return true;
        }

        // a shell reached this tank. the shell system (server) decides contact; the tank applies the damage.
        public void TakeShellHit(int shellId, float hitFraction, int damage)
        {
            ShellImpactRpc(shellId, hitFraction); // sent before the despawn below, or a killing blow could never announce itself
            health.Value -= damage;
            if (health.Value <= 0)
                NetworkObject.Despawn();
        }

        // each machine flies its own local shell from its own drawn barrel tip.
        // each machine overwrites the sent aim point with its own drawn position for the target.
        // the sent aim point is the server's, which the target's owner has already driven past due to latency;
        // without the overwrite the shell would visibly fly to empty ground behind its tank.
        [Rpc(SendTo.ClientsAndHost)]
        void FiredRpc(int shellId, ulong targetObjectId, Vector3 aimPoint, double fireTime)
        {
            TankController target = TankFromObjectId(targetObjectId);
            if (target != null)
                aimPoint = target.transform.position;
            // the tracking turret may still be mid-swing; a shot snaps it onto the aim point so the shell never exits sideways
            Vector3 aimDirection = aimPoint - transform.position;
            aimDirection.y = 0f;
            if (aimDirection != Vector3.zero)
            {
                turret.rotation = Quaternion.LookRotation(aimDirection);
                turretWorldRotation = turret.rotation; // or RenderTurret would restore the pre-snap cache and undo the snap
            }
            ShellVisual.Spawn(shellId, muzzle.position, aimPoint, fireTime, ShellSpeed, HitRadius);
        }

        // a shell reached a tank before its aim point. the fraction says when along the flight, so an event arriving
        // early cannot cut the shell off midair. it runs on the hit tank, so 'this' is that tank on every machine and
        // the marker rides its locally drawn body. misses need no event: the visual ends itself at the aim point.
        [Rpc(SendTo.ClientsAndHost)]
        void ShellImpactRpc(int shellId, float hitFraction)
        {
            ShellVisual.Impact(shellId, hitFraction, this);
        }

        void WriteTripState(Vector3 startPosition, Vector2Int[] path, double startTime, int commandId)
        {
            lastAcknowledgedCommandId = commandId;
            replicatedTripState.Value = new TripState
            {
                StartPosition = startPosition,
                Path = path,
                StartTime = startTime,
                AcknowledgedCommandId = commandId
            };
        }

        void OnTripStateChanged(TripState previousState, TripState newState)
        {
            serverTrip = TripFromState(newState);
            // every command runs twice: predicted locally the moment it is issued, then for real when the server answers it.
            // the prediction stays in charge until a trip arrives carrying a command id at or past our latest, which is the
            // server saying it has answered our newest command. the server stamped its start time a round trip after we set off,
            // so adopting it would rewind the tank; keep ours, and take the server's only when we never predicted.
            // a trip answering an older command leaves the newer prediction alone.
            if (IsOwner && newState.AcknowledgedCommandId >= lastIssuedCommandId)
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

        // the tank's gameplay position: the replicated trip evaluated at a time. never transform.position, which
        // carries the cosmetic error offset. this is what anything outside the tank should read.
        public Vector3 PositionAtTime(double time)
        {
            return PositionAtTime(serverTrip, time);
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
            bool authoritativeArrived = authoritativePosition == trip.EndPoint; // unity vec3 already has ~1e-5 tolerance
            float closeSpeed =
                moveSpeed * (authoritativeArrived ? maxMoveSpeedMultiplier : maxMoveSpeedMultiplier - 1f);
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
            RenderTurret();
        }

        // purely cosmetic, never gates firing: the server shoots by distance alone. every machine aims its own
        // copy at the target's drawn position, so the turrets agree without any wire traffic beyond currentTargetId.
        void RenderTurret()
        {
            Vector3 aimDirection = Vector3.zero;
            // the owner aims at its own last command instead of waiting a round trip for currentTargetId; a dead
            // predicted target resolves to null and the turret eases back, same as the replicated path.
            TankController target = TankFromObjectId(IsOwner ? predictedTargetId : currentTargetId.Value);
            if (target != null)
                aimDirection = target.transform.position - transform.position;
            else if (forceFireArmed)
                aimDirection = forceFireAim - transform.position; // server only; clients never have this armed
            aimDirection.y = 0f;
            Quaternion desiredRotation =
                aimDirection != Vector3.zero ? Quaternion.LookRotation(aimDirection) : transform.rotation; // nothing to aim at: ease back to hull forward
            // the pivot is a child, so a hull turn this frame already dragged it along. restore last frame's world aim
            // first, so the turret only ever moves at its own speed and visibly lags a snapping hull.
            turret.rotation = Quaternion.RotateTowards(
                turretWorldRotation,
                desiredRotation,
                turretTurnSpeed * Time.deltaTime
            );
            turretWorldRotation = turret.rotation;
        }

        // temporary spawn logic. later move to edge spawn when have concentric map
        Vector3 SpawnPosition()
        {
            Vector2Int center = new Vector2Int(TileGrid.Instance.Width / 2, TileGrid.Instance.Height / 2);
            Vector2Int tile = center + new Vector2Int((int)OwnerClientId * 3 - 1, 0); // spread players out around center
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
            public Vector3 EndPoint
            {
                get { return points[^1]; }
            }
        }

        // a trip compacted for network: tiles instead of world points. server writes, everyone reads.
        struct TripState : INetworkSerializable
        {
            public Vector3 StartPosition; // derived by the server from the previous trip, not sent by client
            public Vector2Int[] Path; // the server's A* tiles after the start point; the last one is the goal
            public double StartTime; // server clock seconds
            public int AcknowledgedCommandId; // the command this trip answers, so the owner knows when its prediction is done

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
