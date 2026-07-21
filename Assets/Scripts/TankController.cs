using System;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

namespace TankIO
{
    // tanks have no collision or mid-route effects, so position is a closed form:
    //     position(t) = point along trip at moveSpeed * (t - startTime)
    // one message per command covers the whole route, instead of streaming states every tick like a MOBA.
    // client sends command (while also predict its own movement), the server sends the replicated path/trip,
    // the position error (disagreements) are closed smoothly (mostly by speeding up) instead of snapping (unless large gap).

    public class TankController : NetworkBehaviour, IShellTarget
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
        private const int Damage = 10; // at full troops; MaxHealth 100 means a full tank takes 10 hits
        private const float ShellSpeed = 25f;
        private const float TankHitRadius = 0.5f;
        private const float AimAngleTolerance = 10f; // a shot waits until the turret bears this close on the aim point
        private const double TargetCheckInterval = 0.3; // how often a tank re-decides who it is shooting at

        // the tank's whole networked movement state
        private readonly NetworkVariable<TripState> replicatedTripState = new NetworkVariable<TripState>(
            new TripState { Path = Array.Empty<Vector2Int>() } // path is reference field, must not be null to be serialized
        );

        private const int MaxHealth = 100;
        private readonly NetworkVariable<int> health = new NetworkVariable<int>(MaxHealth);

        // the troops this tank carries: its power, not its survivability. damage scales with them; health
        // does not, which is what keeps the wounded-tank rotation play alive (a hurt tank still hits full).
        private readonly NetworkVariable<int> troops = new NetworkVariable<int>(HqController.TroopsPerTank);
        private bool debugFreeTank; // server only: took no troops from the pool, so death returns none

        public const float WreckReturnSpeed = 1.5f; // slower than driving: an injured hull limping home. HqController re-runs the same arithmetic when a move re-anchors pending returns.
        private bool recallActive; // server only: a standing order home, despawn-and-return on arrival

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

        private static readonly List<ulong> tanksToRepath = new List<ulong>(); // reused by every server trip write

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

        public override void OnNetworkSpawn()
        {
            if (IsServer || IsOwner)
                pathfinder = new Pathfinder(TileGrid.Instance); // only the server routes commands, only the owner predicts
            replicatedTripState.OnValueChanged += OnTripStateChanged;
            SpawnedTanks.Add(this);
            ShellSystem.Targets.Add(this);
            turretWorldRotation = turret.rotation;

            if (IsServer)
            {
                replicatedTripState.Value = new TripState
                {
                    StartPosition = transform.position, // the spawner placed us before spawning
                    Path = Array.Empty<Vector2Int>(),
                    StartTime = 0.0,
                    AcknowledgedCommandId = 0
                };
                TripReservations.Write(NetworkObjectId, transform.position, Array.Empty<Vector2Int>(), 0.0, moveSpeed); // parked on the spawn tile
            }
            else
            {
                OnTripStateChanged(default, replicatedTripState.Value); // e.g. for late joiner, they will now get the repltripstate of the tank (stored in server before they join)
            }
        }

        public override void OnNetworkDespawn()
        {
            SpawnedTanks.Remove(this);
            ShellSystem.Targets.Remove(this);
            TripReservations.Release(NetworkObjectId); // server and client tables both hold entries
            replicatedTripState.OnValueChanged -= OnTripStateChanged;
        }

        void Update()
        {
            if (serverTrip == null)
                return; // not spawned yet
            if (IsServer)
            {
                UpdateTargeting();
                if (forceFireArmed && TryFire(0, forceFireAim, NetworkManager.ServerTime.Time))
                    forceFireArmed = false; // one shot per force-fire click, then the turret rests
            }
            Render();
        }

        // owner only submits input command (goal, i.e. target tile),
        // the server derives its own start point from its own clock, roughly a one-way latency further along than ours.
        public void MoveTo(Vector2Int goal)
        {
            lastIssuedCommandId++;
            double clickTime = NetworkManager.ServerTime.Time;
            Trip currentTrip = predictedTrip ?? serverTrip;
            Vector3 startPosition = PositionAtTime(currentTrip, clickTime);
            // path the click locally and drive it as the trip the server will answer with. the server's
            // answer replaces it; a rare wrong guess just opens a position error.
            predictedTrip = TripFromState(
                new TripState
                {
                    StartPosition = startPosition,
                    Path = PathOrStop(currentTrip, startPosition, goal, clickTime),
                    StartTime = clickTime
                }
            );
            predictedTargetId = 0; // a move order overrides an attack order; drop the aim without waiting for the server

            SubmitMoveCommandRpc(goal, lastIssuedCommandId);
        }

        public float HealthFraction
        {
            get { return (float)health.Value / MaxHealth; }
        }

        public int Troops
        {
            get { return troops.Value; }
        }

        public Vector3 DrawnPosition
        {
            get { return transform.position; }
        }

        public float HitRadius
        {
            get { return TankHitRadius; }
        }

        public bool Attackable
        {
            get { return true; }
        }

        // damage scales with troops, never below 1 so an emptied tank still plinks
        int ScaledDamage
        {
            get { return Math.Max(1, Mathf.RoundToInt(Damage * (troops.Value / (float)HqController.TroopsPerTank))); }
        }

        // the deploy path stamps what the tank carries; a debug tank took nothing and returns nothing
        public void ServerInitializeTroops(int troopCount, bool isDebugFree)
        {
            troops.Value = troopCount;
            debugFreeTank = isDebugFree;
        }

        // the health bar doubles as the selection marker: WorldHealthBars draws it only while this is set
        public bool IsSelected { get; private set; }

        public void SetSelected(bool selected)
        {
            IsSelected = selected;
        }

        // the tank chases the target into range, then fires. the owner predicts the initial approach.
        public void Attack(IShellTarget target)
        {
            lastIssuedCommandId++;
            double clickTime = NetworkManager.ServerTime.Time;
            Trip currentTrip = predictedTrip ?? serverTrip;
            Vector3 startPosition = PositionAtTime(currentTrip, clickTime);
            Vector3 targetPosition = target.PositionAtTime(clickTime);
            Vector2Int[] path =
                (targetPosition - startPosition).magnitude <= IdealFiringDistance
                    ? StopPath(currentTrip, clickTime)
                    : PathOrStop(currentTrip, startPosition, FiringTile(startPosition, targetPosition), clickTime);
            predictedTrip = TripFromState(
                new TripState
                {
                    StartPosition = startPosition,
                    Path = path,
                    StartTime = clickTime
                }
            );
            predictedTargetId = target.NetworkObjectId; // the turret starts its traverse now; the traverse masks the round trip

            SubmitAttackCommandRpc(target.NetworkObjectId, lastIssuedCommandId);
        }

        // drive next to the HQ, then despawn and hand back all troops. 
        // just a trip plus a server flag, so any other click overwrites both and cancels the recall
        public void ReturnToHq(HqController hq)
        {
            lastIssuedCommandId++;
            double clickTime = NetworkManager.ServerTime.Time;
            Trip currentTrip = predictedTrip ?? serverTrip;
            Vector3 startPosition = PositionAtTime(currentTrip, clickTime);
            predictedTrip = TripFromState(
                new TripState
                {
                    StartPosition = startPosition,
                    Path = PathOrStop(currentTrip, startPosition, RecallGoal(hq), clickTime),
                    StartTime = clickTime
                }
            );
            predictedTargetId = 0; // a recall overrides an attack order; drop the aim without waiting for the server

            SubmitRecallCommandRpc(lastIssuedCommandId);
        }

        [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Owner)]
        void SubmitRecallCommandRpc(int commandId)
        {
            double now = NetworkManager.ServerTime.Time;
            currentTargetId.Value = 0;
            Vector3 startPosition = PositionAtTime(serverTrip, now);
            HqController hq = HqController.ForOwner(OwnerClientId);
            if (hq == null)
            {
                // no home to return to; still answer the command so the owner's prediction clears
                StopAtNextTile(now, commandId);
                return;
            }
            recallActive = true;
            WriteTripState(startPosition, PathOrStop(serverTrip, startPosition, RecallGoal(hq), now), now, commandId);
        }

        // the nearest free tile beside the footprint. the spiral skips the HQ's own parked 3x3, so the
        // first candidates are exactly the ring around it.
        Vector2Int RecallGoal(HqController hq)
        {
            TripReservations.TryNearestUnclaimedParkTile(
                hq.HomeTile,
                HqController.FootprintRadius + 2,
                NetworkObjectId,
                out Vector2Int tile
            );
            return tile; // on total failure this is the footprint centre: the path fails, the tank stops, the recall check retries
        }

        [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Owner)]
        void SubmitMoveCommandRpc(Vector2Int goal, int commandId)
        {
            double now = NetworkManager.ServerTime.Time;
            currentTargetId.Value = 0; // a move order overrides an attack order
            recallActive = false;
            // the start is server calculated based on owner's command. the owner set off ~one-way latency earlier.
            // that gap comes back with the acknowledgement and the error smoothing closes it.
            Vector3 startPosition = PositionAtTime(serverTrip, now);
            WriteTripState(startPosition, PathOrStop(serverTrip, startPosition, goal, now), now, commandId);
        }

        [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Owner)]
        void SubmitAttackCommandRpc(ulong targetObjectId, int commandId)
        {
            double now = NetworkManager.ServerTime.Time;
            IShellTarget target = ShellSystem.TargetFromObjectId(targetObjectId);
            if (target == null || target.OwnerClientId == OwnerClientId || !target.Attackable)
                return; // already dead, own target, or an HQ mid-glide
            recallActive = false;
            currentTargetId.Value = targetObjectId;
            // always write a trip stamped with this click's id, so it clears the owner's prediction through the ack path
            // in range: move to the next tile centre, out of range: move to the firing tile.
            if ((target.PositionAtTime(now) - PositionAtTime(serverTrip, now)).magnitude <= IdealFiringDistance)
                StopAtNextTile(now, commandId);
            else
                IssueChaseTrip(target, now, commandId);
            TryFire(target.NetworkObjectId, target.PositionAtTime(now), now); // first shot right away, not at the next check
        }

        // fires whenever in range (driving or not); drives toward the target only while too far to fire.
        void UpdateTargeting()
        {
            double now = NetworkManager.ServerTime.Time;
            if (now < targetCheckTimer)
                return;
            targetCheckTimer = now + TargetCheckInterval; // tanks spawn at different moments, so checks stagger naturally

            if (recallActive)
            {
                UpdateRecall(now);
                return;
            }
            if (currentTargetId.Value == 0)
                return;
            IShellTarget target = ShellSystem.TargetFromObjectId(currentTargetId.Value);
            if (target == null || !target.Attackable)
            {
                currentTargetId.Value = 0; // the clicked target died (or packed up and left)
                StopAtNextTile(now, lastAcknowledgedCommandId); // truncate the chase instead of ghost-driving the rest of the approach
                return;
            }

            Vector3 myPosition = PositionAtTime(serverTrip, now);
            Vector3 targetPosition = target.PositionAtTime(now);
            float distance = (targetPosition - myPosition).magnitude;

            // re-path only when the trip no longer ends within range
            // a still target costs no messages, a fleeing one will cost more.
            float tripEndDistanceFromTarget = (targetPosition - serverTrip.EndPoint).magnitude;
            bool tripDeliversFiringPosition = tripEndDistanceFromTarget <= AttackRange;
            if (distance > IdealFiringDistance && !tripDeliversFiringPosition)
                TryImproveChaseTrip(target, now);
            else if (distance <= IdealFiringDistance && !AlreadyStopping(serverTrip, now))
                StopAtNextTile(now, lastAcknowledgedCommandId); // target walked into range: stop early, keep firing

            TryFire(target.NetworkObjectId, targetPosition, now);
        }

        // recall's periodic check, chase-shaped: arrived beside the footprint > complete;
        // HQ relocated or the approach failed > repath toward its current home, writing only improvements.
        void UpdateRecall(double now)
        {
            HqController hq = HqController.ForOwner(OwnerClientId);
            if (hq == null)
            {
                recallActive = false; // the home despawned mid-drive; the trip finishes as an ordinary move
                return;
            }
            Vector3 myPosition = PositionAtTime(serverTrip, now);
            bool arrived = myPosition == serverTrip.EndPoint; // unity vec3 == has ~1e-5 tolerance
            TileGrid.Instance.WorldToTile(arrived ? myPosition : serverTrip.EndPoint, out Vector2Int checkTile);
            Vector2Int toHome = checkTile - hq.HomeTile;
            bool besideFootprint = Math.Max(Math.Abs(toHome.x), Math.Abs(toHome.y)) <= HqController.FootprintRadius + 1;

            if (arrived && besideFootprint)
            {
                // home: every troop climbs out and the tank stops existing. no wreck, no partial loss.
                if (!debugFreeTank)
                    hq.ReturnTroops(troops.Value, troops.Value);
                NetworkObject.Despawn();
                return;
            }
            if (besideFootprint)
                return; // still rolling toward a valid spot
            // the trip no longer ends at home (HQ moved, or the approach degraded to a stop): try again
            Vector2Int goal = RecallGoal(hq);
            Vector2Int[] path = ComputePath(myPosition, goal, now);
            if (path.Length == 0)
                return; // no route this check; retry next
            WriteTripState(myPosition, path, now, lastAcknowledgedCommandId);
        }

        // the tile to attack from: step back from the target to the ideal firing distance along the line to this tank.
        // callers guarantee the tank is farther than that, so the distance is never zero.
        Vector2Int FiringTile(Vector3 selfPosition, Vector3 targetPosition)
        {
            Vector3 targetToSelf = selfPosition - targetPosition;
            Vector3 firingPosition = targetPosition + targetToSelf / targetToSelf.magnitude * IdealFiringDistance;
            TileGrid.Instance.WorldToTile(firingPosition, out Vector2Int idealTile);
            // ring radius stays 1 so the standoff cannot drift out of range;
            // a farther tile would make the 0.3s chase check re-path toward the ring forever.
            TripReservations.TryNearestUnclaimedParkTile(idealTile, 1, NetworkObjectId, out Vector2Int tile);
            return tile;
        }

        // click path: a command always answers with a trip, unreachable or not (see PathOrStop)
        void IssueChaseTrip(IShellTarget target, double now, int commandId)
        {
            Vector3 startPosition = PositionAtTime(serverTrip, now);
            Vector2Int goal = FiringTile(startPosition, target.PositionAtTime(now));
            WriteTripState(startPosition, PathOrStop(serverTrip, startPosition, goal, now), now, commandId);
        }

        // check path: the 0.3s check only writes improvements. an unreachable standoff writes nothing
        // and is retried next check, instead of stopping the tank or spamming equivalent rewrites.
        void TryImproveChaseTrip(IShellTarget target, double now)
        {
            Vector3 startPosition = PositionAtTime(serverTrip, now);
            Vector2Int goal = FiringTile(startPosition, target.PositionAtTime(now));
            Vector2Int[] path = ComputePath(startPosition, goal, now);
            if (path.Length == 0)
                return;
            WriteTripState(startPosition, path, now, lastAcknowledgedCommandId);
        }

        // a route to the goal, or the stop at the next tile centre when no route exists. a command must
        // always produce a trip (its id is the ack that clears the owner's prediction), and a tank must
        // always come to rest on a tile centre, never mid-tile. covers start == goal for free: StopPath
        // stops at the clicked tile's centre when driving and is empty at rest.
        Vector2Int[] PathOrStop(Trip trip, Vector3 startPosition, Vector2Int goal, double startTime)
        {
            Vector2Int[] path = ComputePath(startPosition, goal, startTime);
            if (path.Length == 0)
                path = StopPath(trip, startTime);
            return path;
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
        // rewrite re-anchors the start at the server's clock - and server rewrites skip the ack-time handover, so a
        // client owner would adopt it a one-way latency behind its own drawn tank and visibly wobble at the rest point.
        bool AlreadyStopping(Trip trip, double now)
        {
            Vector2Int[] remaining = StopPath(trip, now);
            if (remaining.Length == 0)
                return true; // at rest
            TileGrid.Instance.WorldToTile(trip.EndPoint, out Vector2Int endTile);
            return remaining[0] == endTile;
        }

        public static TankController TankFromObjectId(ulong objectId)
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
        public void SubmitForceFireCommandRpc(Vector3 aimPoint)
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
                ScaledDamage
            );
            FiredRpc(shellId, targetObjectId, aimPoint, now); // broadcast to clients
            return true;
        }

        // a shell reached this tank. the shell system (server) decides contact; the tank applies the damage.
        public void TakeShellHit(int shellId, float hitFraction, int damage, ulong attackerClientId)
        {
            HqController homeHq = HqController.ForOwner(OwnerClientId);
            if (homeHq != null)
                homeHq.MarkAggressor(attackerClientId); // my home garrison remembers who shot me
            ShellImpactRpc(shellId, hitFraction); // sent before the despawn below, or a killing blow could never announce itself
            health.Value -= damage;
            if (health.Value <= 0)
                Die();
        }

        // garrison fire: no shell, no impact event, just damage landing on the drawn tank
        public void TakeGarrisonDamage(int damage)
        {
            health.Value -= damage;
            if (health.Value <= 0)
                Die();
        }

        // the tank leaves combat instantly; half its troops die, the survivors drive home as a wreck
        // whose travel time is the redeploy cooldown - die deep, wait long. the server queues the troops
        // to come back at the wreck's arrival; the wreck itself is a local visual on every machine.
        void Die()
        {
            if (!debugFreeTank)
            {
                HqController hq = HqController.ForOwner(OwnerClientId);
                if (hq != null)
                {
                    double now = NetworkManager.ServerTime.Time;
                    Vector3 deathPosition = PositionAtTime(serverTrip, now);
                    Vector3 homePosition = TileGrid.Instance.TileToWorldCenter(hq.HomeTile);
                    homePosition.y = deathPosition.y; // drive level at tank height, same line the visuals fly
                    hq.QueueWreckReturn(troops.Value / 2, troops.Value, deathPosition, homePosition, now);
                    WreckRetreatRpc(deathPosition, homePosition, now); // before the despawn, like the impact event
                }
            }
            NetworkObject.Despawn();
        }

        // this only starts the drawing. nobody reports the arrival: the wreck's drive and the server's
        // timer for returning the troops are both computed from these values, so they finish together.
        [Rpc(SendTo.ClientsAndHost)]
        void WreckRetreatRpc(Vector3 deathPosition, Vector3 homePosition, double startTime)
        {
            // runs on the dying tank, so the owner id costs nothing to send; an HQ move finds this wreck by it
            WreckVisual.Spawn(OwnerClientId, deathPosition, homePosition, startTime, WreckReturnSpeed);
        }

        // each machine flies its own local shell from its own drawn barrel tip.
        // each machine overwrites the sent aim point with its own drawn position for the target.
        // the sent aim point is the server's, which the target's owner has already driven past due to latency;
        // without the overwrite the shell would visibly fly to empty ground behind its tank.
        [Rpc(SendTo.ClientsAndHost)]
        void FiredRpc(int shellId, ulong targetObjectId, Vector3 aimPoint, double fireTime)
        {
            IShellTarget target = ShellSystem.TargetFromObjectId(targetObjectId);
            if (target != null)
                aimPoint = target.DrawnPosition;
            // the tracking turret may still be mid-swing; a shot snaps it onto the aim point so the shell never exits sideways
            Vector3 aimDirection = aimPoint - transform.position;
            aimDirection.y = 0f;
            if (aimDirection != Vector3.zero)
            {
                turret.rotation = Quaternion.LookRotation(aimDirection);
                turretWorldRotation = turret.rotation; // or RenderTurret would restore the pre-snap cache and undo the snap
            }
            ShellVisual.Spawn(shellId, muzzle.position, aimPoint, fireTime, ShellSpeed, TankHitRadius);
        }

        // a shell reached a tank before its aim point. the fraction says when along the flight, so an event arriving
        // early cannot cut the shell off midair. it runs on the hit tank, so 'this' is that tank on every machine and
        // the marker rides its locally drawn body. misses need no event: the visual ends itself at the aim point.
        [Rpc(SendTo.ClientsAndHost)]
        void ShellImpactRpc(int shellId, float hitFraction)
        {
            ShellVisual.Impact(shellId, hitFraction, this);
        }

        // every branch that changes a trip funnels here: move and attack commands, the 0.3s targeting check,
        // chase retries, park repaths, the spawn state. so a trip can never be written without its reservation.
        // clients rewrite their own copy in OnTripStateChanged when the state lands.
        void WriteTripState(
            Vector3 startPosition,
            Vector2Int[] path,
            double startTime,
            int commandId,
            bool repathTanksCrossingParkTile = true
        )
        {
            lastAcknowledgedCommandId = commandId;
            TripReservations.Write(
                NetworkObjectId,
                startPosition,
                path,
                startTime,
                moveSpeed,
                repathTanksCrossingParkTile ? tanksToRepath : null
            );
            replicatedTripState.Value = new TripState
            {
                StartPosition = startPosition,
                Path = path,
                StartTime = startTime,
                AcknowledgedCommandId = commandId
            };
            if (repathTanksCrossingParkTile && tanksToRepath.Count > 0)
            {
                foreach (ulong tankId in tanksToRepath)
                {
                    TankController crossingTank = TankFromObjectId(tankId);
                    if (crossingTank != null)
                        crossingTank.RepathAroundPark(startTime);
                }
            }
        }

        // another tank parked on a tile this trip crosses later; the park was written after this trip,
        // so the trip never saw it. reroute to the same goal through the reservation table, one ordinary
        // server rewrite reusing the last acknowledged id. a repath never triggers repaths of its own:
        // its goal and park tile are unchanged, so it would only re-find the same crossers.
        public void RepathAroundPark(double now)
        {
            Vector3 startPosition = PositionAtTime(serverTrip, now);
            TileGrid.Instance.WorldToTile(serverTrip.EndPoint, out Vector2Int goal);
            Vector2Int[] path = ComputePath(startPosition, goal, now);
            if (path.Length == 0)
                return; // no route around the park (or the goal itself is the park): keep the old trip and accept the clip
            WriteTripState(startPosition, path, now, lastAcknowledgedCommandId, false);
        }

        void OnTripStateChanged(TripState previousState, TripState newState)
        {
            // clients mirror the server's reservation table from replicated trips, so owner
            // prediction sees the same traffic the server will route around
            if (!IsServer)
                TripReservations.Write(
                    NetworkObjectId,
                    newState.StartPosition,
                    newState.Path,
                    newState.StartTime,
                    moveSpeed
                );
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

        // the A* tiles after the start point. the start tile is skipped so the tank sets off toward the
        // next tile, not back to prev's centre. tiles other tanks occupy at this tank's arrival time cost
        // extra, so the route sidesteps predicted traffic where a detour is cheap and drives through where
        // it is not. the owner predicts against its mirrored reservation table, so prediction and server
        // route agree except when a trip replicates mid-click; the error smoothing absorbs that mismatch.
        Vector2Int[] ComputePath(Vector3 startPosition, Vector2Int goal, double startTime)
        {
            if (
                TileGrid.Instance.WorldToTile(startPosition, out Vector2Int start)
                && pathfinder.FindPath(start, goal, pathBuffer, NetworkObjectId, startTime, moveSpeed)
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
            IShellTarget target = ShellSystem.TargetFromObjectId(IsOwner ? predictedTargetId : currentTargetId.Value);
            if (target != null)
                aimDirection = target.DrawnPosition - transform.position;
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
