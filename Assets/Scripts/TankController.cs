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

        [SerializeField]
        private GameObject shellPrefab; // local visuals, spawned per machine; neither is a NetworkObject

        [SerializeField]
        private GameObject muzzleFlashPrefab;

        [SerializeField]
        private GameObject deathExplosionPrefab;

        [SerializeField]
        private GameObject wreckPrefab;

        [SerializeField]
        private Color ownIconColor = new Color(0f, 1f, 0.13f); // tints the shared icon material

        [SerializeField]
        private Color enemyIconColor = new Color(1f, 0.066f, 0f);

        [SerializeField]
        private MeshRenderer midIcon; // the flat quad standing in for the hull at Mid

        private Renderer[] renderers;
        private bool visible = true;

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

        private readonly NetworkVariable<int> deployedTroops = new NetworkVariable<int>(HqController.TroopsPerTank);

        private const float TroopLossAtZeroHealth = 0.25f; // tank health 100% to 0% > troop count 100% > 75%; see below
        private const float TroopLossOnDeath = 0.25f; //when tank dies at 0%, troop count drop further from 75% to 50%
        private bool debugFreeTank; // server only: took no troops from the pool, so death returns none

        // set by the deploy path before Spawn, so it rides the spawn payload and is never 0 on any machine
        private readonly NetworkVariable<ulong> commanderId = new NetworkVariable<ulong>();

        public ulong CommanderId
        {
            get { return commanderId.Value; }
        }

        // selection and the slots HUD ask this, not IsOwner: on a host, a bot's tanks are network-owned
        // by the server and would read as the host player's.
        public bool CommandedByLocalPlayer
        {
            get { return commanderId.Value == NetworkManager.LocalClientId; }
        }

        public const float WreckReturnSpeed = 3f; // currently const cuz hq needs to read it from tankcontroller to redirect wreck...
        private bool recallActive; // server only: a standing order home, despawn-and-return on arrival

        // fire at whatever comes in range, never touching the trip. set by an attack-move click, or by
        // taking a hit while idle. replicated because the commanding player has no click to predict the
        // server-picked targets from, so their turret must follow the server like everyone else's.
        private readonly NetworkVariable<bool> autoAttackActive = new NetworkVariable<bool>();

        private const double RetaliationSeconds = 5.0; // see autoAttackIsRetaliation
        private bool autoAttackIsRetaliation; // server only: so we can know if the autoattack is triggered by attack-move, or by other tanks hitting you. main purpose is to expire the auto-attack from retaliation
        private double autoAttackLastTargetTime; // server only: when auto-attack last had a target in range

        // held until it dies or command overrides. server writes and replicated so every copy can aim its turret at it. 0 = none.
        private readonly NetworkVariable<ulong> currentTargetId = new NetworkVariable<ulong>();

        // a tree has no NetworkObject to name, so its order carries the tile instead of an id. exactly one
        // of the two target fields is ever live; the setters below are the only writers.
        public static readonly Vector2Int NoTile = new Vector2Int(-1, -1);
        private readonly NetworkVariable<Vector2Int> currentTargetTile = new NetworkVariable<Vector2Int>(
            new Vector2Int(-1, -1)
        );

        private double lastFireTime; // server only, reload gate
        private double targetCheckTimer; // server only
        private int lastAcknowledgedCommandId; // server only: the command the current trip answers, so its own rewrites reuse it
        private Vector3 forceFireAim; // server only, debug tool: the point the turret traverses to and fires once at
        private bool forceFireArmed; // server only
        private Quaternion turretWorldRotation; // last frame's turret world aim, held across hull turns so the turret decouples

        // every machine registers here; ShellSystem scans it to find what a shell met
        public static readonly List<TankController> SpawnedTanks = new List<TankController>();

        private static readonly List<ulong> tanksToRepath = new List<ulong>(); // reused by every server trip write

        // every tank's position at the current server frame, parallel to SpawnedTanks. server time is fixed
        // within a frame, so the auto-attack scan evaluates each trip once instead of once per scanning tank
        private static readonly List<Vector3> frameTankPositions = new List<Vector3>();
        private static double frameTankPositionsTime = double.NaN;

        // statics outlive a play session when domain reload is off, so tanks from one session would leak into
        // the next. runs before the scene loads on every entry to play mode.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetSessionState()
        {
            SpawnedTanks.Clear();
            frameTankPositions.Clear();
            frameTankPositionsTime = double.NaN;
            pathfinder = null; // its arrays are sized to the previous session's grid
        }

        // previously wasnt static so on a big map of 1000x1000, each tank having a separate pathfinder cause huge lag at start due to 26,596 MB. After making it static, its 2,433 MB.
        private static Pathfinder pathfinder;
        private readonly List<Vector2Int> pathBuffer = new List<Vector2Int>();

        private Trip serverTrip; // the replicated trip state, evaluatable
        private Trip predictedTrip; // owner only: the click, run immediately, replaced by the acknowledgement
        private Trip renderedTrip; // which of the two the last frame drew, to notice a swap
        private int lastIssuedCommandId; // owner only: the latest command's id, so an old acknowledgement cannot end a newer command's prediction
        private ulong predictedTargetId; // owner only: the target the owner last commanded, aimed at immediately instead of waiting a round trip for currentTargetId
        private Vector2Int predictedTargetTile = new Vector2Int(-1, -1); // owner only, the tree half of predictedTargetId
        private bool predictedAutoAttack; // owner only: whether the latest click was an attack-move, until the server's autoAttackActive answer lands's

        private Vector3 positionError;

        public override void OnNetworkSpawn()
        {
            if ((IsServer || IsOwner) && pathfinder == null)
                pathfinder = new Pathfinder(TileGrid.Instance); // only the server routes commands, only the owner predicts
            replicatedTripState.OnValueChanged += OnTripStateChanged;
            SpawnedTanks.Add(this);
            ShellSystem.Targets.Add(this);
            turretWorldRotation = turret.rotation;
#if !UNITY_SERVER
            // the icon is a child, so it lands in here too; Render writes it after SetVisible and wins
            renderers = GetComponentsInChildren<Renderer>();
            LodIcon.Tint(midIcon, CommandedByLocalPlayer ? ownIconColor : enemyIconColor);
#endif

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
#if UNITY_SERVER
                ServerAimTurret(NetworkManager.ServerTime.Time); // Render is compiled out of this build; the fire gate still needs the pivot aimed
#endif
                UpdateTargeting();
                if (forceFireArmed && TryFire(0, forceFireAim, NetworkManager.ServerTime.Time))
                    forceFireArmed = false; // one shot per force-fire click, then the turret rests
            }
#if !UNITY_SERVER
            // gameplay reads PositionAtTime, never the transform.
            //  skipping this on dedicated server else PhysX would re-register every frame for nobody to query
            Render();
#endif
        }

        // the owner's half of every plain drive order (move, attack-move, recall): path the click locally
        // and drive it as the trip the server will answer with. the server's answer replaces it; a rare
        // wrong guess just opens a position error. always drops the aim without waiting for the server.
        void PredictDrive(Vector2Int goal, bool autoAttack)
        {
            lastIssuedCommandId++;
            if (noPrediction)
                return;
            double clickTime = NetworkManager.ServerTime.Time;
            Trip currentTrip = predictedTrip ?? serverTrip;
            Vector3 startPosition = PositionAtTime(currentTrip, clickTime);
            predictedTrip = TripFromState(
                new TripState
                {
                    StartPosition = startPosition,
                    Path = PathOrStop(currentTrip, startPosition, goal, clickTime),
                    StartTime = clickTime
                }
            );
            predictedTargetId = 0;
            predictedTargetTile = NoTile;
            predictedAutoAttack = autoAttack;
        }

        // the owner's half of every attack order: stand still when the target is already in range, else
        // drive to a firing position. a unit and a tree differ only in the aim, which the caller sets.
        void PredictAttackApproach(Vector3 targetPosition, double clickTime)
        {
            if (noPrediction)
                return;
            Trip currentTrip = predictedTrip ?? serverTrip;
            Vector3 startPosition = PositionAtTime(currentTrip, clickTime);
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
            predictedAutoAttack = false;
        }

        // owner only submits input command (goal, i.e. target tile),
        // the server derives its own start point from its own clock, roughly a one-way latency further along than ours.
        public void MoveTo(Vector2Int goal)
        {
            PredictDrive(goal, false);
            if (noPrediction)
                clickedGoal = TileGrid.Instance.TileToWorldCenter(goal);
            SubmitMoveCommandRpc(goal, lastIssuedCommandId);
        }

        // attack-move: the same trip as a move click; the server additionally fires at whatever
        // comes in range along the way, without the tank ever stopping or chasing.
        public void AttackMoveTo(Vector2Int goal)
        {
            PredictDrive(goal, true);
            if (noPrediction)
                clickedGoal = TileGrid.Instance.TileToWorldCenter(goal);
            SubmitAttackMoveCommandRpc(goal, lastIssuedCommandId);
        }

        // clamped because an overkill hit sends health.Value negative
        public float HealthFraction
        {
            get { return Mathf.Clamp((float)health.Value / MaxHealth, 0f, 1f); }
        }

        public int Troops
        {
            get
            {
                float lost = TroopLossAtZeroHealth * (1f - HealthFraction);
                return Mathf.RoundToInt(deployedTroops.Value * (1f - lost));
            }
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

        // damage follows the live count, so a hurt tank does less damage: 10 at full health, 8 at the brink.
        // this is the tradeoff for showing troops on the health bar - the number now moves as the bar does.
        // never below 1, or a deploy that only found a few troops would do nothing at all.
        int ScaledDamage
        {
            get { return Math.Max(1, Mathf.RoundToInt(Damage * (Troops / (float)HqController.TroopsPerTank))); }
        }

        // the deploy path stamps what the tank carries; a debug tank took nothing and returns nothing
        public void ServerInitializeTroops(int troopCount, bool isDebugFree)
        {
            deployedTroops.Value = troopCount;
            debugFreeTank = isDebugFree;
        }

        // must run before Spawn: clients read CommanderId in OnNetworkSpawn-adjacent paths, so the value
        // has to arrive inside the spawn payload, not as a later delta
        public void ServerSetCommanderBeforeSpawn(ulong id)
        {
            commanderId.Value = id;
        }

        public bool IsSelected { get; private set; }

        public void SetSelected(bool selected)
        {
            IsSelected = selected;
        }

        // clicking an enemy issues an attack and inspect it.
        public bool IsInspected { get; private set; }

        public void SetInspected(bool inspected)
        {
            IsInspected = inspected;
        }

        // the tank chases the target into range, then fires. the owner predicts the initial approach.
        public void Attack(IShellTarget target)
        {
            lastIssuedCommandId++;
            double clickTime = NetworkManager.ServerTime.Time;
            PredictAttackApproach(target.PositionAtTime(clickTime), clickTime);
            if (!noPrediction)
            {
                predictedTargetId = target.NetworkObjectId; // the turret starts its traverse now; the traverse masks the round trip
                predictedTargetTile = NoTile;
            }
            SubmitAttackCommandRpc(target.NetworkObjectId, lastIssuedCommandId);
        }

        // drive next to the HQ, then despawn and hand back all troops.
        // just a trip plus a server flag, so any other click overwrites both and cancels the recall
        public void ReturnToHq(HqController hq)
        {
            Trip currentTrip = predictedTrip ?? serverTrip;
            Vector3 startPosition = PositionAtTime(currentTrip, NetworkManager.ServerTime.Time);
            PredictDrive(RecallGoal(hq, startPosition), false);
            SubmitRecallCommandRpc(lastIssuedCommandId);
        }

        [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Owner)]
        void SubmitRecallCommandRpc(int commandId)
        {
            ExecuteRecall(commandId);
        }

        public void ExecuteRecall(int commandId)
        {
            double now = NetworkManager.ServerTime.Time;
            ClearTarget();
            autoAttackActive.Value = false;
            Vector3 startPosition = PositionAtTime(serverTrip, now);
            HqController hq = HqController.ForCommander(CommanderId);
            if (hq == null)
            {
                // no home to return to; still answer the command so the owner's prediction clears
                StopAtNextTile(now, commandId);
                return;
            }
            recallActive = true;
            WriteTripState(
                startPosition,
                PathOrStop(serverTrip, startPosition, RecallGoal(hq, startPosition), now),
                now,
                commandId
            );
        }

        // the free tile beside the footprint nearest the tank: it parks on the side it approached from.
        // the spiral skips the HQ's own parked 3x3, so the first candidates are exactly the ring around it.
        // fromPosition is each machine's own idea of where the tank is, so owner and server may pick
        // different tiles on a near tie, like any predicted trip.
        Vector2Int RecallGoal(HqController hq, Vector3 fromPosition)
        {
            TileGrid.Instance.WorldToTile(fromPosition, out Vector2Int fromTile); // tile, not the raw point: the two only disagree while straddling a boundary
            TripReservations.TryNearestUnclaimedParkTile(
                hq.HomeTile,
                HqController.FootprintRadius + 2,
                NetworkObjectId,
                fromTile,
                out Vector2Int tile
            );
            return tile; // on total failure this is the footprint centre: the path fails, the tank stops, the recall check retries
        }

        [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Owner)]
        void SubmitMoveCommandRpc(Vector2Int goal, int commandId)
        {
            ExecuteMove(goal, commandId);
        }

        [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Owner)]
        void SubmitAttackMoveCommandRpc(Vector2Int goal, int commandId)
        {
            ExecuteAttackMove(goal, commandId);
        }

        public void ExecuteAttackMove(Vector2Int goal, int commandId)
        {
            ExecuteMove(goal, commandId); // attack-move is a move order plus an ordered auto-attack
            autoAttackActive.Value = true;
            autoAttackIsRetaliation = false;
        }

        public void ExecuteMove(Vector2Int goal, int commandId)
        {
            double now = NetworkManager.ServerTime.Time;
            ClearTarget(); // a move order overrides an attack order
            recallActive = false;
            autoAttackActive.Value = false;
            // the start is server calculated based on owner's command. the owner set off ~one-way latency earlier.
            // that gap comes back with the acknowledgement and the error smoothing closes it.
            Vector3 startPosition = PositionAtTime(serverTrip, now);
            WriteTripState(startPosition, PathOrStop(serverTrip, startPosition, goal, now), now, commandId);
        }

        [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Owner)]
        void SubmitAttackCommandRpc(ulong targetObjectId, int commandId)
        {
            ExecuteAttack(targetObjectId, commandId);
        }

        // the only writers of the two target fields, so "a unit or a tree, never both" holds in one place
        // instead of at every order site.
        void SetTargetUnit(ulong targetObjectId)
        {
            currentTargetId.Value = targetObjectId;
            currentTargetTile.Value = NoTile;
        }

        void SetTargetTree(Vector2Int tile)
        {
            currentTargetId.Value = 0;
            currentTargetTile.Value = tile;
        }

        void ClearTarget()
        {
            currentTargetId.Value = 0;
            currentTargetTile.Value = NoTile;
        }

        bool HasTreeTarget
        {
            get { return currentTargetTile.Value.x >= 0; }
        }

        // a tree is a fixed point that stops existing, so this needs none of the chase-a-mover machinery:
        // no PositionAtTime, no re-path when it flees, no despawn to resolve. just a tile.
        // the tile is not re-checked for a tree here: the caller tested it to route the click this way at
        // all, and the server tests it again on arrival, which is the test that decides anything.
        public void Attack(Vector2Int treeTile)
        {
            lastIssuedCommandId++;
            double clickTime = NetworkManager.ServerTime.Time;
            PredictAttackApproach(TileGrid.Instance.TileToWorldCenter(treeTile), clickTime);
            if (!noPrediction)
            {
                predictedTargetId = 0;
                predictedTargetTile = treeTile;
            }
            SubmitAttackTreeRpc(treeTile, lastIssuedCommandId);
        }

        [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Owner)]
        void SubmitAttackTreeRpc(Vector2Int treeTile, int commandId)
        {
            ExecuteAttackTree(treeTile, commandId);
        }

        public void ExecuteAttackTree(Vector2Int treeTile, int commandId)
        {
            double now = NetworkManager.ServerTime.Time;
            recallActive = false; // an attack order overrides the standing one, satisfiable or not
            autoAttackActive.Value = false;
            if (!TileGrid.Instance.HasTree(treeTile))
            {
                AbortAttackOrder(now, commandId); // felled between the click and its arrival
                return;
            }
            SetTargetTree(treeTile);
            // 0: a tree has no NetworkObject to name as the shell's target, same as force fire
            ServerApproachAndFire(TileGrid.Instance.TileToWorldCenter(treeTile), 0, now, commandId);
        }

        public void ExecuteAttack(ulong targetObjectId, int commandId)
        {
            double now = NetworkManager.ServerTime.Time;
            recallActive = false;
            autoAttackActive.Value = false;
            IShellTarget target = ShellSystem.TargetFromObjectId(targetObjectId);
            if (target == null || target.CommanderId == CommanderId || !target.Attackable)
            {
                AbortAttackOrder(now, commandId); // already dead, own target, or an HQ mid-glide
                return;
            }
            SetTargetUnit(targetObjectId);
            ServerApproachAndFire(target.PositionAtTime(now), targetObjectId, now, commandId);
        }

        // the server's half of every attack order, unit or tree.
        // always write a trip stamped with this click's id, so it clears the owner's prediction through the ack path
        // in range: move to the next tile centre, out of range: move to the firing tile.
        void ServerApproachAndFire(Vector3 targetPosition, ulong aimTargetId, double now, int commandId)
        {
            if ((targetPosition - PositionAtTime(serverTrip, now)).magnitude <= IdealFiringDistance)
                StopAtNextTile(now, commandId);
            else
                IssueChaseTrip(targetPosition, now, commandId);
            TryFire(aimTargetId, targetPosition, now); // first shot right away, not at the next check
        }

        // nothing left to shoot, on arrival or mid-chase: drop the aim and truncate the chase instead of
        // ghost-driving the rest of the approach. the trip is written even when it changes nothing the tank
        // drives, because its command id is the ack that ends the owner's prediction - a silent return
        // leaves the owner driving an approach the server never ran.
        void AbortAttackOrder(double now, int commandId)
        {
            ClearTarget();
            StopAtNextTile(now, commandId);
        }

        void UpdateTreeAttack(double now)
        {
            Vector2Int tile = currentTargetTile.Value;
            if (!TileGrid.Instance.HasTree(tile))
            {
                AbortAttackOrder(now, lastAcknowledgedCommandId); // felled, by this tank or anyone
                return;
            }
            // a tree never moves, so the trip written at order time already ends in range; the re-path
            // inside only ever catches a chase the pathfinder could not satisfy then.
            HoldFiringPosition(TileGrid.Instance.TileToWorldCenter(tile), 0, now);
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
            if (autoAttackActive.Value)
            {
                UpdateAutoAttack(now);
                return;
            }
            if (HasTreeTarget)
            {
                UpdateTreeAttack(now);
                return;
            }
            if (currentTargetId.Value == 0)
                return;
            IShellTarget target = ShellSystem.TargetFromObjectId(currentTargetId.Value);
            if (target == null || !target.Attackable)
            {
                AbortAttackOrder(now, lastAcknowledgedCommandId); // the clicked target died (or packed up and left)
                return;
            }

            HoldFiringPosition(target.PositionAtTime(now), target.NetworkObjectId, now);
        }

        // the 0.3s check's tail, shared by unit and tree orders: close the distance while the current trip
        // does not deliver a firing position, stop early once the target is within one, and fire either way.
        void HoldFiringPosition(Vector3 targetPosition, ulong aimTargetId, double now)
        {
            float distance = (targetPosition - PositionAtTime(serverTrip, now)).magnitude;

            // re-path only when the trip no longer ends within range
            // a still target costs no messages, a fleeing one will cost more.
            bool tripDeliversFiringPosition = (targetPosition - serverTrip.EndPoint).magnitude <= AttackRange;
            if (distance > IdealFiringDistance && !tripDeliversFiringPosition)
                TryImproveChaseTrip(targetPosition, now);
            else if (distance <= IdealFiringDistance && !AlreadyStopping(serverTrip, now))
                StopAtNextTile(now, lastAcknowledgedCommandId); // target walked into range: stop early, keep firing

            TryFire(aimTargetId, targetPosition, now);
        }

        // hold the current target while it stays in range, else take the nearest enemy tank in range.
        void UpdateAutoAttack(double now)
        {
            Vector3 myPosition = PositionAtTime(serverTrip, now);
            IShellTarget target = ShellSystem.TargetFromObjectId(currentTargetId.Value);
            bool targetStillValid =
                target != null
                && target.Attackable
                && (target.PositionAtTime(now) - myPosition).sqrMagnitude <= AttackRange * AttackRange;
            if (!targetStillValid) // holding a valid target stops the turret flipping between two in-range enemies
            {
                target = NearestEnemyTankInRange(myPosition, now);
                SetTargetUnit(target != null ? target.NetworkObjectId : 0);
            }
            if (target != null)
            {
                autoAttackLastTargetTime = now;
                TryFire(target.NetworkObjectId, target.PositionAtTime(now), now);
            }
            else if (autoAttackIsRetaliation && now - autoAttackLastTargetTime >= RetaliationSeconds)
            {
                autoAttackActive.Value = false; // nobody ordered this; with the range clear it disarms
            }
        }

        // rebuilds frameTankPositions once per server frame; later scans this frame reuse it
        static void CacheTankPositions(double now)
        {
            if (frameTankPositionsTime == now && frameTankPositions.Count == SpawnedTanks.Count)
                return;
            frameTankPositions.Clear();
            for (int index = 0; index < SpawnedTanks.Count; index++)
                frameTankPositions.Add(SpawnedTanks[index].PositionAtTime(now));
            frameTankPositionsTime = now;
        }

        // tanks only
        IShellTarget NearestEnemyTankInRange(Vector3 myPosition, double now)
        {
            CacheTankPositions(now);
            TankController nearest = null;
            float nearestSquaredDistance = AttackRange * AttackRange;
            for (int index = 0; index < SpawnedTanks.Count; index++)
            {
                TankController tank = SpawnedTanks[index];
                if (tank.CommanderId == CommanderId)
                    continue;
                float squaredDistance = (frameTankPositions[index] - myPosition).sqrMagnitude;
                if (squaredDistance <= nearestSquaredDistance)
                {
                    nearest = tank;
                    nearestSquaredDistance = squaredDistance;
                }
            }
            return nearest;
        }

        // recall's periodic check, chase-shaped: arrived beside the footprint > complete;
        // HQ relocated or the approach failed > repath toward its current home, writing only improvements.
        void UpdateRecall(double now)
        {
            HqController hq = HqController.ForCommander(CommanderId);
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
                if (!debugFreeTank)
                    hq.ReturnTroops(Troops, deployedTroops.Value);
                NetworkObject.Despawn();
                return;
            }
            if (besideFootprint)
                return; // still rolling toward a valid spot
            // the trip no longer ends at home (HQ moved, or the approach degraded to a stop): try again
            Vector2Int goal = RecallGoal(hq, myPosition);
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
        // a world position rather than a target: a tree is a tile centre, with no trip to evaluate.
        void IssueChaseTrip(Vector3 targetPosition, double now, int commandId)
        {
            Vector3 startPosition = PositionAtTime(serverTrip, now);
            Vector2Int goal = FiringTile(startPosition, targetPosition);
            WriteTripState(startPosition, PathOrStop(serverTrip, startPosition, goal, now), now, commandId);
        }

        // check path: the 0.3s check only writes improvements. an unreachable standoff writes nothing
        // and is retried next check, instead of stopping the tank or spamming equivalent rewrites.
        void TryImproveChaseTrip(Vector3 targetPosition, double now)
        {
            Vector3 startPosition = PositionAtTime(serverTrip, now);
            Vector2Int goal = FiringTile(startPosition, targetPosition);
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
                CommanderId,
                NetworkObjectId,
                ShellSpeed,
                ScaledDamage
            );
            FiredRpc(shellId, targetObjectId, aimPoint, now); // broadcast to clients
            return true;
        }

        // a shell reached this tank. the shell system (server) decides contact; the tank applies the damage.
        public void TakeShellHit(
            int shellId,
            float hitFraction,
            int damage,
            ulong attackerCommanderId,
            ulong attackerObjectId
        )
        {
            HqController homeHq = HqController.ForCommander(CommanderId);
            if (homeHq != null)
                homeHq.MarkAggressor(attackerCommanderId); // my home garrison remembers who shot me
            // an idle tank answers fire: auto-attack flips on, aimed at the shooter. any standing order outranks it,
            // a tree order included: felling is a command, not idleness.
            if (!recallActive && !autoAttackActive.Value && currentTargetId.Value == 0 && !HasTreeTarget)
            {
                autoAttackActive.Value = true;
                autoAttackIsRetaliation = true;
                autoAttackLastTargetTime = NetworkManager.ServerTime.Time; // the disarm clock starts at the hit
                SetTargetUnit(attackerObjectId);
            }
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

        // the tank leaves combat instantly; a last 25% of troops die here, the survivors drive home as a wreck. 
        // the wreck itself is a local visual on every machine.
        void Die()
        {
            if (!debugFreeTank)
            {
                HqController hq = HqController.ForCommander(CommanderId);
                if (hq != null)
                {
                    double now = NetworkManager.ServerTime.Time;
                    Vector3 deathPosition = PositionAtTime(serverTrip, now);
                    Vector3 homePosition = TileGrid.Instance.TileToWorldCenter(hq.HomeTile);
                    homePosition.y = deathPosition.y; // drive level at tank height, same line the visuals fly
                    // health is already 0 or below, so Troops has already lost its 25%; this takes the rest
                    int survivors = Troops - Mathf.RoundToInt(deployedTroops.Value * TroopLossOnDeath);
                    hq.QueueWreckReturn(survivors, deployedTroops.Value, deathPosition, homePosition, now);
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
            // runs on the dying tank, so the commander id costs nothing to send; an HQ move finds this wreck by it
            WreckVisual.Spawn(wreckPrefab, CommanderId, deathPosition, homePosition, startTime, WreckReturnSpeed);
            if (deathExplosionPrefab != null && CameraController.Lod == LodTier.Near)
                Instantiate(deathExplosionPrefab, deathPosition, deathExplosionPrefab.transform.rotation);
        }

        // each machine flies its own local shell from its own drawn barrel tip.
        // each machine overwrites the sent aim point with its own drawn position for the target.
        // the sent aim point is the server's, which the target's owner has already driven past due to latency;
        // without the overwrite the shell would visibly fly to empty ground behind its tank.
        [Rpc(SendTo.ClientsAndHost)]
        void FiredRpc(int shellId, ulong targetObjectId, Vector3 aimPoint, double fireTime)
        {
            IShellTarget target = ShellSystem.TargetFromObjectId(targetObjectId);
            if (target != null && !noAimOverwrite)
                aimPoint = target.DrawnPosition;
            // the tracking turret may still be mid-swing; a shot snaps it onto the aim point so the shell never exits sideways
            Vector3 aimDirection = aimPoint - transform.position;
            aimDirection.y = 0f;
            if (aimDirection != Vector3.zero)
            {
                turret.rotation = Quaternion.LookRotation(aimDirection);
                turretWorldRotation = turret.rotation; // or RenderTurret would restore the pre-snap cache and undo the snap
            }
            ShellVisual.Spawn(shellPrefab, shellId, muzzle.position, aimPoint, fireTime, ShellSpeed, TankHitRadius);
            SpawnMuzzleFlash();
        }

        // parented to the muzzle: the turret keeps tracking during the flash
        void SpawnMuzzleFlash()
        {
            if (muzzleFlashPrefab == null || CameraController.Lod != LodTier.Near)
                return;
            Instantiate(muzzleFlashPrefab, muzzle);
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
            if (path.Length > 0)
                LogTripBytes(startPosition, path, commandId);
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

        // measurement aid for the report's march-bytes figure; every tank writes here, so filter the console by "TripBytes".
        // rows repeating a cmd are server rewrites (targeting, chase retry, park repath), so a march costs their sum, not one row.
        // 28 = StartPosition 12 + tileCount 4 + StartTime 8 + AcknowledgedCommandId 4, and 8 per tile is one Vector2Int.
        void LogTripBytes(Vector3 startPosition, Vector2Int[] path, int commandId)
        {
            int bytes = 28 + 8 * path.Length;
            float distance = 0f;
            Vector3 previousPoint = startPosition;
            for (int index = 0; index < path.Length; index++)
            {
                Vector3 tileCentre = TileGrid.Instance.TileToWorldCenter(path[index]);
                distance += Vector3.Distance(previousPoint, tileCentre);
                previousPoint = tileCentre;
            }
            Debug.Log(
                $"TripBytes tank={NetworkObjectId} cmd={commandId} tiles={path.Length}"
                + $" bytes={bytes} distance={distance:F1} seconds={distance / moveSpeed:F1}"
            );
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
                clickedGoal = null;
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

        // the tank's gameplay position: the replicated trip evaluated at a time. never transform.position, which carries the cosmetic error offset.
        // this is what anything outside the tank should read.
        public Vector3 PositionAtTime(double time)
        {
            if (serverTrip == null)
                return transform.position; // NGO asks CheckObjectVisibility before OnNetworkSpawn writes the trip; the spawner already placed us there
            return PositionAtTime(serverTrip, time);
        }

        // the goal of the trip the renderer is driving (the owner's prediction until the ack lands,
        // so it never lags the click), or null once arrived. the command line draws from this.
        public Vector3? ActiveCommandGoal
        {
            get
            {
                if (clickedGoal != null)
                    return clickedGoal;
                Trip trip = predictedTrip ?? serverTrip;
                if (trip == null)
                    return null;
                Vector3 end = trip.EndPoint;
                return PositionAtTime(trip, NetworkManager.ServerTime.Time) == end ? null : (Vector3?)end;
            }
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

            LodTier lod = CameraController.Lod;
            SetVisible(lod == LodTier.Near);
            midIcon.enabled = lod == LodTier.Mid;
            if (lod == LodTier.Mid)
                midIcon.transform.rotation = Quaternion.identity; // written in world space: the hull yaw underneath varies per tank

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

        void SetVisible(bool shouldBeVisible)
        {
            if (visible == shouldBeVisible)
                return;
            visible = shouldBeVisible;
            foreach (Renderer meshRenderer in renderers)
                meshRenderer.enabled = shouldBeVisible;
        }

#if UNITY_SERVER
        // the dedicated build strips Render, so nothing rotates the pivot TryFire gates on, and the transforms
        // it would read are stale. aim from the same trip evaluations the gate itself compares.
        void ServerAimTurret(double now)
        {
            Vector3 muzzlePosition = PositionAtTime(serverTrip, now);
            IShellTarget target = ShellSystem.TargetFromObjectId(currentTargetId.Value);
            Vector3 aimDirection = Vector3.zero;
            if (target != null)
                aimDirection = target.PositionAtTime(now) - muzzlePosition;
            else if (HasTreeTarget)
                aimDirection = TileGrid.Instance.TileToWorldCenter(currentTargetTile.Value) - muzzlePosition;
            else if (forceFireArmed)
                aimDirection = forceFireAim - muzzlePosition;
            TurnTurret(aimDirection);
        }
#endif

        // purely cosmetic, never gates firing: the server shoots by distance alone. every machine aims its own
        // copy at the target's drawn position, so the turrets agree without any wire traffic beyond currentTargetId.
        void RenderTurret()
        {
            Vector3 aimDirection = Vector3.zero;
            // the commanding player aims at their own last command instead of waiting a round trip for currentTargetId;
            // a dead predicted target resolves to null and the turret eases back, same as the replicated path.
            // under auto-attack there is no commanded target - the server picks them - so the owner follows
            // currentTargetId like everyone else, ~half a round trip late. while a click is still in flight
            // the owner's own belief about auto-attack wins over the not-yet-updated replicated flag.
            // CommandedByLocalPlayer, not IsOwner: a host network-owns bot tanks but never predicts for them.
            bool followServerAim = predictedTrip != null ? predictedAutoAttack : autoAttackActive.Value;
            bool useOwnCommand = CommandedByLocalPlayer && !followServerAim && !noPrediction;
            IShellTarget target = ShellSystem.TargetFromObjectId(
                useOwnCommand ? predictedTargetId : currentTargetId.Value
            );
            // the tree tile follows the same predicted-vs-replicated split, so the commanding player's turret
            // swings the moment they click instead of waiting for the tile to replicate back.
            Vector2Int aimTile = useOwnCommand ? predictedTargetTile : currentTargetTile.Value;
            if (target != null)
                aimDirection = target.DrawnPosition - transform.position;
            else if (aimTile.x >= 0)
                aimDirection = TileGrid.Instance.TileToWorldCenter(aimTile) - transform.position;
            else if (forceFireArmed)
                aimDirection = forceFireAim - transform.position; // server only; clients never have this armed
            TurnTurret(aimDirection);
        }

        void TurnTurret(Vector3 aimDirection)
        {
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

        // debug flags for the latency demo captures
        [SerializeField] private bool noPrediction;
        [SerializeField] private bool noAimOverwrite;
        private Vector3? clickedGoal; // shows the click while noPrediction waits out the ack
    }
}
