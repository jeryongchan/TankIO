using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

namespace TankIO
{
    // shells belong to no tank: the one that fired can die mid-flight, and a shell can hit a tank it was never
    // aimed at. so they are simulated once here, server side, instead of from whichever tank happened to fire.
    // each shell carries its own stats, so this knows nothing about tanks beyond where they are.
    public class ShellSystem : MonoBehaviour
    {
        public static ShellSystem Instance { get; private set; }

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

        // returns the id naming this shell on every machine, so the shot event can name its visual
        public int Fire(
            Vector3 muzzlePosition,
            Vector3 aimPoint,
            double fireTime,
            ulong shooterClientId,
            float speed,
            float hitRadius,
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
                    shooterClientId = shooterClientId,
                    speed = speed,
                    hitRadius = hitRadius,
                    damage = damage
                }
            );
            return lastShellId;
        }

        // hit or miss is decided here, per frame, by distance; the flight everyone sees is cosmetic.
        // a shell hits the first enemy of its shooter it meets, not necessarily the tank it was aimed at
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

                TankController hitTank = null;
                float hitDistanceSquared = shell.hitRadius * shell.hitRadius;
                foreach (TankController tank in TankController.SpawnedTanks)
                {
                    if (tank.OwnerClientId == shell.shooterClientId)
                        continue; // friendly tanks never block
                    float distanceSquared = (shellPosition - tank.PositionAtTime(now)).sqrMagnitude;
                    if (distanceSquared <= hitDistanceSquared)
                    {
                        hitTank = tank; // nearest wins when several overlap the shell
                        hitDistanceSquared = distanceSquared;
                    }
                }

                if (hitTank != null)
                {
                    liveShells.RemoveAt(index);
                    // fraction of the flight, not meters: each machine flies from its own drawn muzzle, so line lengths differ.
                    //  overlapping tanks can make a zero-length flight, hence the guard.
                    float hitFraction = flightLength > 0f ? distanceTraveled / flightLength : 0f;
                    hitTank.TakeShellHit(shell.shellId, hitFraction, shell.damage);
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
            public ulong shooterClientId; // the shooter's own tanks never block its shells
            public float speed;
            public float hitRadius;
            public int damage;
        }
    }
}
