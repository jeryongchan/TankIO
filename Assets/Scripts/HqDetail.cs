using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

namespace TankIO
{
    // the half of an HQ that is only readable up close: health and who the garrison is shooting.
    // a separate NetworkObject purely so interest management can hide it per client.
    public class HqDetail : NetworkBehaviour
    {
        // set by the spawner before Spawn, so it rides the spawn payload: OnNetworkSpawn already reads it.
        private readonly NetworkVariable<ulong> hqObjectId = new NetworkVariable<ulong>();
        private readonly NetworkVariable<int> health = new NetworkVariable<int>(HqController.MaxHqHealth);

        // who the garrison is shooting, 0 = nobody. replicated only so every machine can draw the tracer;
        // the damage itself is server arithmetic on the existing health path.
        private readonly NetworkVariable<ulong> garrisonVictimId = new NetworkVariable<ulong>();

        // the server's interest pass walks this; on a client it is only the details it currently holds
        public static readonly List<HqDetail> Spawned = new List<HqDetail>();

        public HqController Hq { get; private set; }

        public int Health
        {
            get { return health.Value; }
            set { health.Value = value; }
        }

        public ulong GarrisonVictimId
        {
            get { return garrisonVictimId.Value; }
            set { garrisonVictimId.Value = value; }
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetSessionState()
        {
            Spawned.Clear();
        }

        // must run before Spawn, for the same reason as the HQ's commander id
        public void ServerSetHqBeforeSpawn(ulong id)
        {
            hqObjectId.Value = id;
        }

        public override void OnNetworkSpawn()
        {
            // the HQ is never hidden, so it is always already spawned by the time its detail shows
            Hq = NetworkManager.SpawnManager.SpawnedObjects[hqObjectId.Value].GetComponent<HqController>();
            Hq.Detail = this;
            Spawned.Add(this);
        }

        public override void OnNetworkDespawn()
        {
            if (Hq != null) // the HQ despawning first is the ordinary case: it takes its detail down with it
                Hq.Detail = null;
            Spawned.Remove(this);
        }
    }
}
