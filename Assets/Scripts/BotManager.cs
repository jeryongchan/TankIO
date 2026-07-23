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
        private const double AdvanceCooldown = 60.0;
        private const int AdvanceStep = 4; // tiles toward the map centre per HQ move

        void Awake()
        {
            Instance = this;
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
                    NextAdvanceTime = Reschedule(NextAdvanceTime, AdvanceCooldown, now);
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

        // called during server start in worldspawner. spawnHq's float is depth: 0 = beside the capital, 1 = the rim
        public void SpawnBots(Action<ulong, ulong, float> spawnHq)
        {
            bots.Clear(); // re-hosting in the same play session would otherwise run the old bots alongside the new ones
            double now = NetworkManager.Singleton.ServerTime.Time;
            for (int index = 0; index < botCount; index++)
            {
                ulong commanderId = FirstBotCommanderId + (ulong)index;
                // bot 0 deepest, last bot at the rim: a joining player finds a world already in progress
                spawnHq(commanderId, NetworkManager.ServerClientId, (index + 1f) / botCount);
                // stagger the decision: with 3 bots the decision tick lands at t=0.00 for bot 0, t=0.33 for bot 1, t=0.67 for bot 2, t=1.00 for bot 0 again;
                double stagger = (double)index / Mathf.Max(1, botCount);
                bots.Add(
                    new Bot
                    {
                        CommanderId = commanderId,
                        NextDecisionTime = now + DecisionInterval * stagger,
                        NextPatrolTime = now + PatrolCooldown * stagger,
                        NextRaidTime = now + RaidCooldown + RaidCooldown * stagger,
                        NextAdvanceTime = now + AdvanceCooldown + AdvanceCooldown * stagger,
                        Rng = new System.Random(botSeed + index)
                    }
                );
            }
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
        }
    }
}
