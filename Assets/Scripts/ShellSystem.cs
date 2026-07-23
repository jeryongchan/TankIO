using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

namespace TankIO
{
    // shells belong to no tank: the one that fired can die mid-flight, and a shell can hit a target it was never
    // aimed at. so they are simulated once here, server side, instead of from whichever tank happened to fire.
    // each shell carries its own stats, so this knows nothing about tanks beyond where they are.
    public class ShellSystem : MonoBehaviour
    {
        public static ShellSystem Instance { get; private set; }

        // everything a shell can meet; tanks and HQs register on every machine (the scan is server-only,
        // but clients read the list too, e.g. the garrison tracer resolving its victim)
        public static readonly List<IShellTarget> Targets = new List<IShellTarget>();

        // statics outlive a play session when domain reload is off
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetSessionState()
        {
            Targets.Clear();
        }

        private readonly List<Shell> liveShells = new List<Shell>();
        private int lastShellId;

        // nothing here is authored, so it creates itself rather than needing a scene object someone can forget
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Create()
        {
            GameObject systemObject = new GameObject(nameof(ShellSystem));
            DontDestroyOnLoad(systemObject);
            Instance = systemObject.AddComponent<ShellSystem>();
        }

        // the registered target behind a NetworkObject id, null when despawned or not a target
        public static IShellTarget TargetFromObjectId(ulong objectId)
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
            return networkObject.GetComponent<IShellTarget>();
        }

        // returns the id naming this shell on every machine, so the shot event can name its visual
        public int Fire(
            Vector3 muzzlePosition,
            Vector3 aimPoint,
            double fireTime,
            ulong shooterCommanderId,
            ulong shooterObjectId,
            float speed,
            int damage
        )
        {
            lastShellId++;
            liveShells.Add(
                new Shell
                {
                    shellId = lastShellId,
                    muzzlePosition = muzzlePosition,
                    aimPoint = aimPoint,
                    fireTime = fireTime,
                    shooterCommanderId = shooterCommanderId,
                    shooterObjectId = shooterObjectId,
                    speed = speed,
                    damage = damage
                }
            );
            return lastShellId;
        }

        // hit or miss is decided here, per frame, by distance; the flight everyone sees is cosmetic.
        // a shell hits the first enemy of its shooter it meets, not necessarily the target it was aimed at.
        // contact distance is the target's radius (a building is a wider point), not a shell property.
        // In future maybe can do Broad-phase for optimization
        void Update()
        {
            if (NetworkManager.Singleton == null || !NetworkManager.Singleton.IsServer)
                return; // runs on every machine, but only the server owns shells

            double now = NetworkManager.Singleton.ServerTime.Time;
            for (int index = liveShells.Count - 1; index >= 0; index--)
            {
                Shell shell = liveShells[index];
                float flightLength = (shell.aimPoint - shell.muzzlePosition).magnitude;
                float distanceTraveled = shell.speed * (float)(now - shell.fireTime);
                Vector3 shellPosition = Vector3.MoveTowards(shell.muzzlePosition, shell.aimPoint, distanceTraveled);

                IShellTarget hitTarget = null;
                float bestOverlap = 0f; // how far inside its radius the shell sits; deepest wins when several overlap
                foreach (IShellTarget target in Targets)
                {
                    if (target.CommanderId == shell.shooterCommanderId)
                        continue; // friendly targets never block
                    if (!target.Attackable)
                        continue;
                    float distance = (shellPosition - target.PositionAtTime(now)).magnitude;
                    float overlap = target.HitRadius - distance;
                    if (overlap > bestOverlap)
                    {
                        hitTarget = target;
                        bestOverlap = overlap;
                    }
                }

                if (hitTarget != null)
                {
                    liveShells.RemoveAt(index);
                    // fraction of the flight, not meters: each machine flies from its own drawn muzzle, so line lengths differ.
                    //  overlapping targets can make a zero-length flight, hence the guard.
                    float hitFraction = flightLength > 0f ? distanceTraveled / flightLength : 0f;
                    hitTarget.TakeShellHit(
                        shell.shellId,
                        hitFraction,
                        shell.damage,
                        shell.shooterCommanderId,
                        shell.shooterObjectId
                    );
                }
                else if (distanceTraveled >= flightLength)
                {
                    liveShells.RemoveAt(index); // reached the committed point without meeting anyone: a dodge
                }
            }
        }

        // a shot in flight. position is closed form from muzzle, aim point and fire time, like a trip.
        private struct Shell
        {
            public int shellId; // names the shell across machines so the impact event can end the right visual
            public Vector3 muzzlePosition;
            public Vector3 aimPoint;
            public double fireTime;
            public ulong shooterCommanderId; // the shooter's own targets never block its shells
            public ulong shooterObjectId; // so an idle victim can fire back at the shooter
            public float speed;
            public int damage;
        }
    }
}
