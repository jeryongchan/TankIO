using System;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

namespace TankIO
{
    public class BotManager : MonoBehaviour
    {
        public static BotManager Instance { get; private set; }

        [SerializeField]
        private int botCount;

        [SerializeField]
        private int botSeed = 1; // for same bot decisions, so two stress runs stay comparable

        public const ulong FirstBotCommanderId = 1000000; // NGO hands out client ids from 0 upward, so first bot is 1mil, nth bot is 1mil+n and so on, never clash
        private const double DecisionInterval = 1.0; // bot decision interval
        private const double PatrolCooldown = 10.0;
        private const int PatrolRadius = 6; // tiles around home a patrol order can land
        private const double RaidCooldown = 90.0;
        private const double RaidDuration = 30.0; // active raid duration. also to prevent patrol during this period
        private const double MinAdvanceInterval = 45.0; // per bot, drawn at spawn: one flat interval would march every bot inward in lockstep
        private const double MaxAdvanceInterval = 180.0;
        private const int AdvanceStep = 4; // tiles toward the map centre per HQ move
        private const double MinLifeSeconds = 60.0; // a bot logs off after this long, and a fresh one joins at the rim: the population is a flow, not a fixed cast
        private const double MaxLifeSeconds = 2400.0;
        private const double MinRespawnDelaySeconds = 10.0; // the wide spread keeps a few bots between lives at any moment, so the population sits a little under botCount and breathes
        private const double MaxRespawnDelaySeconds = 180.0;
        private const float RimQuitChance = 0.95f; // rolled when the lifetime expires, by depth: deep bots mostly re-up another lifetime,
        private const float CenterQuitChance = 0.25f; // so veterans exist at the centre without silting it up permanently

        void Awake()
        {
            Instance = this;
            // SpawnBots runs on OnServerStarted, so Awake is early enough
            if (LaunchArgs.TryGetInt("-botCount", out int count))
                botCount = Mathf.Max(0, count);
        }

        // server-side code holding a commander id and calling Execute methods which a human player RPC wraps in
        private class Bot
        {
            public ulong CommanderId;
            public double NextDecisionTime;
            public System.Random Rng;
            public double NextPatrolTime;
            public double NextRaidTime;
            public double NextAdvanceTime;
            public double AdvanceInterval;
            public double LogOffTime;
            private double raidEndTime;

            public void Decide(double now)
            {
                HqController hq = HqController.ForCommander(CommanderId);
                if (hq == null)
                    return;
                // 1. always deploy maximum tanks.
                hq.ExecuteDeploy();
                // 2. raid (every RaidCooldown, when all tanks alive) every tank attacks the nearest enemy HQ. if no enemy HQ found, retry next tick.
                if (now >= NextRaidTime && OwnTankCount() == HqController.MaxDeployedTanks)
                {
                    HqController target = NearestEnemyHq(hq);
                    if (target != null)
                    {
                        foreach (TankController tank in TankController.SpawnedTanks)
                        {
                            if (tank.CommanderId == CommanderId)
                                tank.ExecuteAttack(target.NetworkObjectId, 0);
                        }
                        NextRaidTime = Reschedule(NextRaidTime, RaidCooldown, now);
                        raidEndTime = now + RaidDuration;
                    }
                }
                // 3. advance (every AdvanceCooldown): move the HQ a step toward the map centre; can target capital at last step
                if (now >= NextAdvanceTime)
                {
                    hq.ExecuteMove(TileTowardCenter(hq.HomeTile));
                    NextAdvanceTime = Reschedule(NextAdvanceTime, AdvanceInterval, now);
                }
                // 4. patrol (every PatrolCooldown, paused during a raid): every tank attack-moves to a random tile near home
                if (now >= NextPatrolTime && now >= raidEndTime)
                {
                    foreach (TankController tank in TankController.SpawnedTanks)
                    {
                        if (tank.CommanderId != CommanderId)
                            continue;
                        Vector2Int goal =
                            hq.HomeTile
                            + new Vector2Int(
                                Rng.Next(-PatrolRadius, PatrolRadius + 1),
                                Rng.Next(-PatrolRadius, PatrolRadius + 1)
                            );
                        // pathfinder will allow two tanks to reach same goal; need to separate the goal tiles of two tanks before executing
                        TripReservations.TryNearestUnclaimedParkTile(goal, 2, tank.NetworkObjectId, out goal);
                        tank.ExecuteAttackMove(goal, 0);
                    }
                    NextPatrolTime = Reschedule(NextPatrolTime, PatrolCooldown, now);
                }
            }

            int OwnTankCount()
            {
                int count = 0;
                foreach (TankController tank in TankController.SpawnedTanks)
                {
                    if (tank.CommanderId == CommanderId)
                        count++;
                }
                return count;
            }

            HqController NearestEnemyHq(HqController home)
            {
                HqController nearest = null;
                float nearestDistanceSquared = float.MaxValue;
                foreach (HqController hq in HqController.SpawnedHqs)
                {
                    if (hq.CommanderId == CommanderId)
                        continue;
                    if (!hq.Attackable)
                        continue; // mid-glide: the order would drop and the raid cooldown would be spent on nothing
                    float distanceSquared = (hq.HomeTile - home.HomeTile).sqrMagnitude;
                    if (distanceSquared < nearestDistanceSquared)
                    {
                        nearest = hq;
                        nearestDistanceSquared = distanceSquared;
                    }
                }
                return nearest;
            }

            static Vector2Int TileTowardCenter(Vector2Int fromTile)
            {
                Vector2Int center = new Vector2Int(TileGrid.Instance.Width / 2, TileGrid.Instance.Height / 2);
                Vector2 inward = center - fromTile;
                if (inward.sqrMagnitude <= AdvanceStep * AdvanceStep)
                    return center; // close enough to dock: the capital snap and footprint checks decide the rest
                return fromTile + Vector2Int.RoundToInt(inward.normalized * AdvanceStep);
            }
        }

        private readonly List<Bot> bots = new List<Bot>();

        private Action<ulong, ulong, float> spawnHq; // kept from SpawnBots so a replacement can spawn long after server start
        private ulong nextCommanderId;
        private readonly List<double> pendingRespawnTimes = new List<double>();

        // called during server start in worldspawner. spawnHq's float is depth: 0 = beside the capital, 1 = the rim
        public void SpawnBots(Action<ulong, ulong, float> spawnHq)
        {
            this.spawnHq = spawnHq;
            bots.Clear(); // re-hosting in the same play session would otherwise run the old bots alongside the new ones
            pendingRespawnTimes.Clear();
            nextCommanderId = FirstBotCommanderId;
            double now = NetworkManager.Singleton.ServerTime.Time;
            for (int index = 0; index < botCount; index++)
            {
                // bot 0 deepest, last bot at the rim: a joining player finds a world already in progress.
                // sqrt gives every depth the same density.
                // stagger the decision: with 3 bots the decision tick lands at t=0.00 for bot 0, t=0.33 for bot 1, t=0.67 for bot 2, t=1.00 for bot 0 again;
                SpawnBot(Mathf.Sqrt((index + 1f) / botCount), now, (double)index / Mathf.Max(1, botCount), true);
            }
        }

        // one bot under the next commander id, never reused: reservations and NGO ids key off it.
        // a boot bot keeps only a random fraction of its lifetime, as if it had already been playing:
        // full lifetimes started together would log the whole first generation off in one wave
        void SpawnBot(float spawnDepth, double now, double stagger, bool bootSpawn)
        {
            ulong commanderId = nextCommanderId++;
            spawnHq(commanderId, NetworkManager.ServerClientId, spawnDepth);
            System.Random rng = new System.Random(botSeed + (int)(commanderId - FirstBotCommanderId));
            double advanceInterval = MinAdvanceInterval + rng.NextDouble() * (MaxAdvanceInterval - MinAdvanceInterval);
            double lifeSeconds = DrawLifeSeconds(rng);
            if (bootSpawn)
            {
                double fullLife = lifeSeconds;
                lifeSeconds *= rng.NextDouble();
                // the consumed share backdates the join, so the scoreboard shows a mid-life veteran,
                // not a server-start wave of fresh joins
                if (Scoreboard.Instance != null)
                    Scoreboard.Instance.ServerBackdateJoin(commanderId, fullLife - lifeSeconds);
            }
            bots.Add(
                new Bot
                {
                    CommanderId = commanderId,
                    NextDecisionTime = now + DecisionInterval * stagger,
                    NextPatrolTime = now + PatrolCooldown * stagger,
                    NextRaidTime = now + RaidCooldown + RaidCooldown * stagger,
                    NextAdvanceTime = now + advanceInterval * (1.0 + stagger),
                    AdvanceInterval = advanceInterval,
                    LogOffTime = now + lifeSeconds,
                    Rng = rng
                }
            );
        }

        // squared roll: a flat draw would make a one minute session as likely as a forty minute one,
        // where most players who bounce do it early and only a few settle in
        static double DrawLifeSeconds(System.Random rng)
        {
            double roll = rng.NextDouble();
            return MinLifeSeconds + roll * roll * (MaxLifeSeconds - MinLifeSeconds);
        }

        // instant vanish, the same shape as a player disconnect: tanks first, then the HQ, whose
        // OnNetworkDespawn releases its reservations and takes the detail object with it
        void DespawnBot(ulong commanderId)
        {
            List<TankController> tanks = TankController.SpawnedTanks;
            for (int index = tanks.Count - 1; index >= 0; index--)
            {
                if (tanks[index].CommanderId == commanderId)
                    tanks[index].NetworkObject.Despawn();
            }
            HqController hq = HqController.ForCommander(commanderId);
            if (hq != null)
                hq.NetworkObject.Despawn();
        }

        // whole intervals from the old scheduled time, not from now: each bot keeps the offset it was
        // given at spawn, and a stall skips the missed runs instead of replaying them all at once
        static double Reschedule(double previousTime, double interval, double now)
        {
            while (previousTime <= now)
                previousTime += interval;
            return previousTime;
        }

        void Update()
        {
            if (NetworkManager.Singleton == null || !NetworkManager.Singleton.IsServer) // only server updates the bots
                return;
            double now = NetworkManager.Singleton.ServerTime.Time;
            foreach (Bot bot in bots)
            {
                if (now < bot.NextDecisionTime)
                    continue;
                bot.NextDecisionTime = Reschedule(bot.NextDecisionTime, DecisionInterval, now);
                bot.Decide(now);
            }
            for (int index = bots.Count - 1; index >= 0; index--)
            {
                Bot bot = bots[index];
                if (now < bot.LogOffTime)
                    continue;
                HqController hq = HqController.ForCommander(bot.CommanderId);
                if (hq != null)
                {
                    // linear in depth: the advance interval is slow enough that bots spend most of a
                    // life in transit, so the mid-map fills by travel time and needs no bend
                    float depth = TileGrid.Instance.RingDepth01(hq.HomeTile);
                    if (bot.Rng.NextDouble() >= Mathf.Lerp(RimQuitChance, CenterQuitChance, depth))
                    {
                        // survived the roll: play another lifetime. a knockback toward the rim worsens the next roll
                        bot.LogOffTime = now + DrawLifeSeconds(bot.Rng);
                        continue;
                    }
                }
                DespawnBot(bot.CommanderId);
                bots.RemoveAt(index);
                pendingRespawnTimes.Add(
                    now + MinRespawnDelaySeconds + bot.Rng.NextDouble() * (MaxRespawnDelaySeconds - MinRespawnDelaySeconds)
                );
            }
            for (int index = pendingRespawnTimes.Count - 1; index >= 0; index--)
            {
                if (now < pendingRespawnTimes[index])
                    continue;
                pendingRespawnTimes.RemoveAt(index);
                SpawnBot(1f, now, 0.0, false); // replacements always enter at the rim, like a joining player
            }
        }
    }
}
