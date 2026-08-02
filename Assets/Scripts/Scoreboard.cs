using System;
using Unity.Netcode;

namespace TankIO
{
    // live-session scoreboard: one row per online commander, bots indistinguishable from humans.
    // rides the capital's NetworkObject, which every client always sees, so the list reaches everyone.
    // names are not synced: clients regenerate them from the commander id.
    public class Scoreboard : NetworkBehaviour
    {
        public static Scoreboard Instance { get; private set; }

        public struct Row : INetworkSerializable, IEquatable<Row>
        {
            public ulong CommanderId;
            public double JoinTime; // server time. boot bots are backdated, as if they had joined mid-life
            public float BestHoldSeconds; // longest finished capital hold this session; the running hold is added at display time

            public void NetworkSerialize<T>(BufferSerializer<T> serializer) where T : IReaderWriter
            {
                serializer.SerializeValue(ref CommanderId);
                serializer.SerializeValue(ref JoinTime);
                serializer.SerializeValue(ref BestHoldSeconds);
            }

            public bool Equals(Row other)
            {
                return CommanderId == other.CommanderId
                    && JoinTime == other.JoinTime
                    && BestHoldSeconds == other.BestHoldSeconds;
            }
        }

        private readonly NetworkList<Row> rows = new NetworkList<Row>();

        public NetworkList<Row> Rows
        {
            get { return rows; }
        }

        public override void OnNetworkSpawn()
        {
            Instance = this;
        }

        public override void OnNetworkDespawn()
        {
            if (Instance == this)
                Instance = null;
        }

        public void ServerAdd(ulong commanderId, double joinTime)
        {
            rows.Add(new Row { CommanderId = commanderId, JoinTime = joinTime, BestHoldSeconds = 0f });
        }

        public void ServerRemove(ulong commanderId)
        {
            int index = IndexOf(commanderId);
            if (index >= 0)
                rows.RemoveAt(index);
        }

        public void ServerBackdateJoin(ulong commanderId, double seconds)
        {
            int index = IndexOf(commanderId);
            if (index < 0)
                return;
            Row row = rows[index];
            row.JoinTime -= seconds;
            rows[index] = row;
        }

        public void ServerReportHold(ulong commanderId, float holdSeconds)
        {
            int index = IndexOf(commanderId);
            if (index < 0)
                return; // the holder logged off in the same tick its hold ended; the disk leaderboard still has it
            Row row = rows[index];
            if (holdSeconds <= row.BestHoldSeconds)
                return;
            row.BestHoldSeconds = holdSeconds;
            rows[index] = row;
        }

        int IndexOf(ulong commanderId)
        {
            for (int index = 0; index < rows.Count; index++)
            {
                if (rows[index].CommanderId == commanderId)
                    return index;
            }
            return -1;
        }
    }
}
