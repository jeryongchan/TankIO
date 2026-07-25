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
        private readonly List<Vector3> targetPositions = new List<Vector3>(); // per-frame cache, same order as Targets
        private int lastShellId;

        void Awake()
        {
            Instance = this;
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
                    damage = damage,
                    hitTreeDistance = DistanceToHitTree(muzzlePosition, aimPoint, out Vector2Int hitTile),
                    hitTreeTile = hitTile
                }
            );
            return lastShellId;
        }

        // shared by the server's shells and the client's visuals, so both stop a shell at the same trunk without exchanging a message on it.
        public static float DistanceToHitTree(Vector3 muzzlePosition, Vector3 aimPoint, out Vector2Int tile)
        {
            bool hitTree = TileGrid.Instance.TryFindTreeAlongSegment(
                muzzlePosition,
                aimPoint,
                out float distance,
                out tile
            );
            return hitTree ? distance : float.MaxValue;
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
            targetPositions.Clear(); // last frame's positions
            for (int index = liveShells.Count - 1; index >= 0; index--)
            {
                // a target's position is the same for every shell this frame, so evaluate each trip once.
                // recached when the count moves: a hit can despawn a target mid-frame, shifting the list
                if (targetPositions.Count != Targets.Count)
                {
                    targetPositions.Clear();
                    foreach (IShellTarget target in Targets)
                        targetPositions.Add(target.PositionAtTime(now));
                }

                Shell shell = liveShells[index];
                float flightLength = (shell.aimPoint - shell.muzzlePosition).magnitude;
                float distanceTraveled = shell.speed * (float)(now - shell.fireTime);
                Vector3 shellPosition = Vector3.MoveTowards(shell.muzzlePosition, shell.aimPoint, distanceTraveled);

                // no need to check other tanks from earlier shell path, because they cant walk on tree tiles anyway
                if (distanceTraveled >= shell.hitTreeDistance)
                {
                    if (TileGrid.Instance.HasTree(shell.hitTreeTile)) // check if the tree is still there when shell arrive
                    {
                        liveShells.RemoveAt(index);
                        TreeSystem.Instance.RegisterShellHit(shell.hitTreeTile);
                        continue;
                    }
                    else // else is to be more explicit: if somehow the tree is felled mid-shell-flight, check again if there are another tree further down the flight path
                    {
                        shell.hitTreeDistance = DistanceToHitTree(
                            shell.muzzlePosition,
                            shell.aimPoint,
                            out shell.hitTreeTile
                        );
                        liveShells[index] = shell;
                    }
                }

                IShellTarget hitTarget = null;
                float bestOverlap = 0f; // how far inside its radius the shell sits; deepest wins when several overlap
                for (int targetIndex = 0; targetIndex < Targets.Count; targetIndex++)
                {
                    IShellTarget target = Targets[targetIndex];
                    if (target.CommanderId == shell.shooterCommanderId)
                        continue; // friendly targets never block
                    if (!target.Attackable)
                        continue;
                    float sqrDistance = (shellPosition - targetPositions[targetIndex]).sqrMagnitude;
                    if (sqrDistance >= target.HitRadius * target.HitRadius)
                        continue; // squared compare keeps the sqrt for actual contacts only
                    float overlap = target.HitRadius - Mathf.Sqrt(sqrDistance);
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
            public float hitTreeDistance; // MaxValue means the line is clear.
            public Vector2Int hitTreeTile;
        }
    }
}
