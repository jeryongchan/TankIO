using System;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

namespace TankIO
{
    // one HQ per player: it earns gold, trains troops, deploys tanks, and defends with garrison fire.
    public partial class HqController : NetworkBehaviour, IShellTarget
    {
        [SerializeField]
        private GameObject tankPrefab;

        public const int FootprintRadius = 1; // 3x3

        // placeholder economy. income rises toward the centre.
        private const float EdgeGoldRate = 20f;
        private const float CenterGoldRate = 100f;

        public const int MaxTroops = 1000;
        public const int TroopsPerTank = 250; // a deploy takes up to this many; fewer at home means a weaker tank, not no tank
        public const int MaxDeployedTanks = 3;
        private const float TroopRegenPerSecond = 3f; // recovery pacing: a dead tank's ~125 lost troops come back in ~40s
        private const int DeploySpawnSearchRadius = 4; // rings around the HQ searched for a free spawn tile

        private const int MaxHqHealth = 500;
        private const float HqHitRadius = 1.5f; // half the 3-tile footprint width; the corners it undercuts are imperceptible

        private const int KnockbackTiles = 4; // rings toward the edge a destroyed HQ is thrown
        private const float KnockbackSpeed = 6f; // the forced glide is fast: a punishment, not a journey

        // balance is a closed form like a trip: value at a timestamp plus a rate, evaluated locally whenever
        // displayed or spent. it replicates only when something changes it (a move, an arrival).
        private struct GoldState : INetworkSerializable
        {
            public double Balance;
            public float RatePerSecond;
            public double Timestamp;

            public void NetworkSerialize<T>(BufferSerializer<T> serializer) where T : IReaderWriter
            {
                serializer.SerializeValue(ref Balance);
                serializer.SerializeValue(ref RatePerSecond);
                serializer.SerializeValue(ref Timestamp);
            }
        }

        // troops are gold's closed form plus a ceiling: regen only replaces dead troops, never troops that
        // are merely outside. Ceiling = MaxTroops - troops currently in tanks.
        private struct TroopState : INetworkSerializable
        {
            public double Balance;
            public float RatePerSecond;
            public double Timestamp;
            public int Ceiling;

            public void NetworkSerialize<T>(BufferSerializer<T> serializer) where T : IReaderWriter
            {
                serializer.SerializeValue(ref Balance);
                serializer.SerializeValue(ref RatePerSecond);
                serializer.SerializeValue(ref Timestamp);
                serializer.SerializeValue(ref Ceiling);
            }
        }

        private readonly NetworkVariable<GoldState> replicatedGoldState = new NetworkVariable<GoldState>();
        private readonly NetworkVariable<TroopState> replicatedTroopState = new NetworkVariable<TroopState>();

        private readonly NetworkVariable<int> returningTanks = new NetworkVariable<int>();

        private readonly NetworkVariable<int> hqHealth = new NetworkVariable<int>(MaxHqHealth);

        // set by the spawner before Spawn, so it rides the spawn payload: OnNetworkSpawn already reads it.
        // a human's client id, or a bot's assigned id; every tank this HQ deploys inherits it.
        private readonly NetworkVariable<ulong> commanderId = new NetworkVariable<ulong>();

        public ulong CommanderId
        {
            get { return commanderId.Value; }
        }

        // true only on the commanding player's own screen; other players' and every bot's read false
        public bool CommandedByLocalPlayer
        {
            get { return commanderId.Value == NetworkManager.LocalClientId; }
        }

        // must run before Spawn: CommandedByLocalPlayer is read in OnNetworkSpawn, so the value has to
        // arrive inside the spawn payload, not as a later delta
        public void ServerSetCommanderBeforeSpawn(ulong id)
        {
            commanderId.Value = id;
        }

        // the wreck's drive as the server tracks it. the full line is kept, not just ArriveTime, so an
        // HQ move mid-return can re-anchor the drive toward the new home from wherever the wreck is now.
        private struct PendingReturn
        {
            public Vector3 StartPosition; // the death point, until a re-anchor replaces it
            public Vector3 HomePosition;
            public double StartTime;
            public double ArriveTime;
            public int Survivors;
            public int Taken;
        }

        private readonly List<PendingReturn> pendingReturns = new List<PendingReturn>(); // server only

        // the owner's own HQ, for the commander and the HUD
        public static HqController LocalPlayerHq { get; private set; }

        // the server finds a dead tank's home HQ here to return its survivors
        public static readonly List<HqController> SpawnedHqs = new List<HqController>();

        // the one lookup from a commander to its HQ; null when that player or bot has none (never spawned, or gone)
        public static HqController ForCommander(ulong commanderId)
        {
            foreach (HqController hq in SpawnedHqs)
            {
                if (hq.CommanderId == commanderId)
                    return hq;
            }
            return null;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetSessionState()
        {
            SpawnedHqs.Clear();
        }

        public override void OnNetworkSpawn()
        {
            replicatedMoveState.OnValueChanged += OnMoveStateChanged;
            authoredScale = transform.localScale;
            renderers = GetComponentsInChildren<Renderer>();
            SpawnedHqs.Add(this);
            ShellSystem.Targets.Add(this);
            if (CommandedByLocalPlayer)
            {
                LocalPlayerHq = this;
                if (CameraController.Instance != null)
                    CameraController.Instance.CenterOn(transform.position);
            }

            if (IsServer)
            {
                TileGrid.Instance.WorldToTile(transform.position, out Vector2Int spawnTile);
                replicatedGoldState.Value = new GoldState
                {
                    Balance = 0.0,
                    RatePerSecond = GoldRateAt(spawnTile),
                    Timestamp = NetworkManager.ServerTime.Time
                };
                replicatedTroopState.Value = new TroopState
                {
                    Balance = MaxTroops,
                    RatePerSecond = TroopRegenPerSecond,
                    Timestamp = NetworkManager.ServerTime.Time,
                    Ceiling = MaxTroops
                };
                // times in the past: spawns parked. the change handler and the arrival transition do the
                // reservation bookkeeping, same path a real move takes.
                replicatedMoveState.Value = new MoveState
                {
                    FromTile = spawnTile,
                    ToTile = spawnTile,
                    DepartTime = 0.0,
                    ArriveTime = 0.0
                };
            }
            else
            {
                OnMoveStateChanged(default, replicatedMoveState.Value); // the event never fires for the spawn value
            }
        }

        public override void OnNetworkDespawn()
        {
            TripReservations.Release(NetworkObjectId);
            replicatedMoveState.OnValueChanged -= OnMoveStateChanged;
            SpawnedHqs.Remove(this);
            ShellSystem.Targets.Remove(this);
            if (LocalPlayerHq == this)
                LocalPlayerHq = null;
        }

        void Update()
        {
            if (!IsSpawned)
                return; // instantiated but not yet networked: the move state is still all zeros
            MoveState state = replicatedMoveState.Value;
            double now = NetworkManager.ServerTime.Time;

            UpdateMoveTransitions(state, now);
            if (IsServer)
            {
                ReturnArrivedWrecks(now);
                UpdateGarrison(now);
            }
            Render(state, now);
        }

        // owner input path, called by the tank strip
        public void RequestDeploy()
        {
            SubmitDeployRpc();
        }

        [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Owner)]
        void SubmitDeployRpc()
        {
            ExecuteDeploy();
        }

        public void ExecuteDeploy()
        {
            double now = NetworkManager.ServerTime.Time;
            if (!IsParked(now))
                return;
            // a wreck still driving home holds its slot: the drive is the redeploy cooldown
            if (DeployedTankCount(CommanderId) + returningTanks.Value >= MaxDeployedTanks)
                return;
            int troopsToTake = Math.Min(TroopsPerTank, (int)HomeTroops(now));
            if (troopsToTake <= 0)
                return;
            if (!TryFindDeployTile(out Vector2Int spawnTile))
                return; // every nearby tile is claimed; the slot stays ready, try again after making room

            TakeTroops(troopsToTake, now);
            SpawnTank(spawnTile, troopsToTake, false);
        }

        // debug: a free full-strength tank, ignoring gold, troops, and the slot cap. it returns no troops
        // on death, since none were ever taken.
        [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Owner)]
        public void SubmitDebugDeployRpc()
        {
            if (TryFindDeployTile(out Vector2Int spawnTile))
                SpawnTank(spawnTile, TroopsPerTank, true);
        }

        void SpawnTank(Vector2Int tile, int troops, bool debugFree)
        {
            GameObject tank = UnityEngine.Object.Instantiate(tankPrefab);
            Vector3 position = TileGrid.Instance.TileToWorldCenter(tile);
            position.y = tankPrefab.transform.position.y; // the trip is built from the root's height
            tank.transform.position = position;
            TankController controller = tank.GetComponent<TankController>();
            controller.ServerSetCommanderBeforeSpawn(CommanderId);
            // network ownership follows the HQ's: a client for a human, the server for a bot
            tank.GetComponent<NetworkObject>().SpawnWithOwnership(OwnerClientId);
            controller.ServerInitializeTroops(troops, debugFree);
        }

        // nearest unclaimed tile outside the footprint; the spiral skips the HQ's own parked tiles by itself
        bool TryFindDeployTile(out Vector2Int tile)
        {
            return TripReservations.TryNearestUnclaimedParkTile(HomeTile, DeploySpawnSearchRadius, 0, out tile);
        }

        static int DeployedTankCount(ulong commanderId)
        {
            int count = 0;
            foreach (TankController tank in TankController.SpawnedTanks)
            {
                if (tank.CommanderId == commanderId)
                    count++;
            }
            return count;
        }

        // a dead tank's survivors drive home before they count again: the troops come back at the wreck's
        // arrival time, and the tank slot stays taken until then. the wreck on screen is a local visual
        // computed from the same values, so it lands with the troops and no arrival message is needed.
        public void QueueWreckReturn(
            int survivors,
            int taken,
            Vector3 deathPosition,
            Vector3 homePosition,
            double startTime
        )
        {
            pendingReturns.Add(
                new PendingReturn
                {
                    StartPosition = deathPosition,
                    HomePosition = homePosition,
                    StartTime = startTime,
                    ArriveTime = startTime + (homePosition - deathPosition).magnitude / TankController.WreckReturnSpeed,
                    Survivors = survivors,
                    Taken = taken
                }
            );
            returningTanks.Value++;
        }

        // wrecks drive to where home will be: a confirmed move re-anchors every pending drive from the
        // wreck's current point toward the new destination. the visuals do the same in RetargetFor off
        // the replicated state, so no message is added for this.
        void RetargetPendingReturns(Vector2Int newHomeTile, double now)
        {
            for (int index = 0; index < pendingReturns.Count; index++)
            {
                PendingReturn pending = pendingReturns[index];
                Vector3 position = Vector3.MoveTowards(
                    pending.StartPosition,
                    pending.HomePosition,
                    TankController.WreckReturnSpeed * (float)Math.Max(0.0, now - pending.StartTime)
                );
                Vector3 home = TileGrid.Instance.TileToWorldCenter(newHomeTile);
                home.y = position.y;
                pendingReturns[index] = new PendingReturn
                {
                    StartPosition = position,
                    HomePosition = home,
                    StartTime = now,
                    ArriveTime = now + (home - position).magnitude / TankController.WreckReturnSpeed,
                    Survivors = pending.Survivors,
                    Taken = pending.Taken
                };
            }
        }

        public int ReturningTanks
        {
            get { return returningTanks.Value; }
        }

        void ReturnArrivedWrecks(double now)
        {
            for (int index = pendingReturns.Count - 1; index >= 0; index--)
            {
                if (now < pendingReturns[index].ArriveTime)
                    continue;
                PendingReturn arrived = pendingReturns[index];
                pendingReturns.RemoveAt(index);
                returningTanks.Value--;
                ReturnTroops(arrived.Survivors, arrived.Taken);
            }
        }

        // a tank died or was recalled: its troops stop being "outside" (ceiling rises by all of them), and
        // the survivors rejoin the pool. the dead half is what regen exists to replace. clamped so debug
        // tanks, which took nothing, cannot print soldiers past the cap.
        public void ReturnTroops(int survivors, int taken)
        {
            double now = NetworkManager.ServerTime.Time;
            TroopState state = replicatedTroopState.Value;
            int ceiling = Math.Min(MaxTroops, state.Ceiling + taken);
            replicatedTroopState.Value = new TroopState
            {
                Balance = Math.Min(HomeTroops(now) + survivors, ceiling),
                RatePerSecond = state.RatePerSecond,
                Timestamp = now,
                Ceiling = ceiling
            };
        }

        public double HomeTroops(double now)
        {
            TroopState state = replicatedTroopState.Value;
            return Math.Min(state.Balance + state.RatePerSecond * Math.Max(0.0, now - state.Timestamp), state.Ceiling);
        }

        public int TroopCeiling
        {
            get { return replicatedTroopState.Value.Ceiling; }
        }

        void SetTroopRate(float ratePerSecond, double now)
        {
            TroopState state = replicatedTroopState.Value;
            replicatedTroopState.Value = new TroopState
            {
                Balance = HomeTroops(now),
                RatePerSecond = ratePerSecond,
                Timestamp = now,
                Ceiling = state.Ceiling
            };
        }

        void TakeTroops(int count, double now)
        {
            TroopState state = replicatedTroopState.Value;
            replicatedTroopState.Value = new TroopState
            {
                Balance = HomeTroops(now) - count,
                RatePerSecond = state.RatePerSecond,
                Timestamp = now,
                Ceiling = state.Ceiling - count
            };
        }

        public float HitRadius
        {
            get { return HqHitRadius; }
        }

        public Vector3 DrawnPosition
        {
            get { return transform.position; }
        }

        // a moving HQ, once commited to move, is non-interactable: shells pass through, attack orders drop it, and its own garrison holds fire.
        public bool Attackable
        {
            get { return IsParked(NetworkManager.ServerTime.Time); }
        }

        // gameplay position: the footprint centre of whichever endpoint the HQ currently stands on.
        // during the glide it returns the line between them, but Attackable already excludes that window.
        public Vector3 PositionAtTime(double time)
        {
            return replicatedMoveState.Value.PositionAtTime(time);
        }

        public void TakeShellHit(
            int shellId,
            float hitFraction,
            int damage,
            ulong attackerCommanderId,
            ulong attackerObjectId
        )
        {
            MarkAggressor(attackerCommanderId);
            ShellImpactRpc(shellId, hitFraction); // before a possible knockback state change, like the tank's ordering rule
            hqHealth.Value -= damage;
            if (hqHealth.Value <= 0)
                Knockback(attackerCommanderId);
        }

        [Rpc(SendTo.ClientsAndHost)]
        void ShellImpactRpc(int shellId, float hitFraction)
        {
            ShellVisual.Impact(shellId, hitFraction, this);
        }

        // destroyed: thrown back toward the edge, healed to half, and the killer plunders half the gold.
        // the relocation is a forced move through the ordinary move state,
        // so reservations, income pause and arrival restoration all come from machinery that already runs.
        void Knockback(ulong attackerCommanderId)
        {
            double now = NetworkManager.ServerTime.Time;
            hqHealth.Value = MaxHqHealth / 2; // respawns viable, not re-killable

            // plunder before the move so the stolen half is measured at the moment of the kill
            double stolen = Gold(now) * 0.5;
            Spend((float)stolen, now);
            HqController killer = ForCommander(attackerCommanderId);
            if (killer != null)
                killer.AddGold((float)stolen, now);

            // away from the map centre, clamped into the grid, then the nearest free footprint around that
            Vector2Int current = replicatedMoveState.Value.ToTile;
            Vector2Int mapCenter = new Vector2Int(TileGrid.Instance.Width / 2, TileGrid.Instance.Height / 2);
            Vector2 outward = current - mapCenter;
            if (outward == Vector2.zero)
                outward = Vector2.right; // dead centre: any direction is outward
            Vector2Int ideal = current + Vector2Int.RoundToInt(outward.normalized * KnockbackTiles);
            if (!TryFindFreeFootprintNear(ideal, out Vector2Int landing))
            {
                SetGoldRate(GoldRateAt(current), now); // nowhere to go: stay, keep the half-health reset
                return;
            }
            // departs immediately: no packing, the building was just blown off its foundations
            BeginMove(landing, now, KnockbackSpeed, now);
        }

        // nearest tile around the ideal whose 3x3 is walkable and unclaimed, spiralling outward
        bool TryFindFreeFootprintNear(Vector2Int ideal, out Vector2Int result)
        {
            for (int radius = 0; radius <= KnockbackTiles + 4; radius++)
            {
                for (int x = -radius; x <= radius; x++)
                {
                    for (int y = -radius; y <= radius; y++)
                    {
                        if (Math.Max(Math.Abs(x), Math.Abs(y)) != radius)
                            continue; // ring only, not the filled square
                        Vector2Int tile = ideal + new Vector2Int(x, y);
                        if (IsFootprintFree(tile, NetworkObjectId))
                        {
                            result = tile;
                            return true;
                        }
                    }
                }
            }
            result = ideal;
            return false;
        }

        void AddGold(float amount, double now)
        {
            GoldState state = replicatedGoldState.Value;
            replicatedGoldState.Value = new GoldState
            {
                Balance = Gold(now) + amount,
                RatePerSecond = state.RatePerSecond,
                Timestamp = now
            };
        }

        public double Gold(double now)
        {
            GoldState state = replicatedGoldState.Value;
            return state.Balance + state.RatePerSecond * Math.Max(0.0, now - state.Timestamp);
        }

        void Spend(float cost, double now)
        {
            GoldState state = replicatedGoldState.Value;
            replicatedGoldState.Value = new GoldState
            {
                Balance = Gold(now) - cost,
                RatePerSecond = state.RatePerSecond,
                Timestamp = now
            };
        }

        void SetGoldRate(float ratePerSecond, double now)
        {
            replicatedGoldState.Value = new GoldState
            {
                Balance = Gold(now),
                RatePerSecond = ratePerSecond,
                Timestamp = now
            };
        }

        // which 9 tiles the HQ is about to land on?
        public static void FootprintTiles(Vector2Int centerTile, List<Vector2Int> results)
        {
            results.Clear();
            for (int x = -FootprintRadius; x <= FootprintRadius; x++)
            {
                for (int y = -FootprintRadius; y <= FootprintRadius; y++)
                    results.Add(centerTile + new Vector2Int(x, y));
            }
        }

        // only permanent bookings block: a parked tank, or another HQ's footprint. tanks merely driving
        // across are ignored here and rerouted when the claim lands, so their timings never matter.
        // don't rewrite this loop to call FootprintTiles: it would overwrite the list the caller is using.
        public static bool IsFootprintFree(Vector2Int centerTile, ulong ignoredId)
        {
            for (int x = -FootprintRadius; x <= FootprintRadius; x++)
            {
                for (int y = -FootprintRadius; y <= FootprintRadius; y++)
                {
                    Vector2Int tile = centerTile + new Vector2Int(x, y);
                    if (!TileGrid.Instance.IsWalkable(tile))
                        return false;
                    ulong holder = TripReservations.ParkedTankAt(tile);
                    if (holder != 0 && holder != ignoredId)
                        return false;
                }
            }
            return true;
        }

        public static float GoldRateAt(Vector2Int tile)
        {
            return Mathf.Lerp(EdgeGoldRate, CenterGoldRate, TileGrid.Instance.RingDepth01(tile));
        }

        // read-only surfaces for the HUD (ResourceHud, WorldHealthBars); every write stays in this class
        public float HealthFraction
        {
            get { return Mathf.Clamp01((float)hqHealth.Value / MaxHqHealth); }
        }

        public float GoldRatePerSecond
        {
            get { return replicatedGoldState.Value.RatePerSecond; }
        }

        public float TroopRatePerSecond
        {
            get { return replicatedTroopState.Value.RatePerSecond; }
        }
    }
}
