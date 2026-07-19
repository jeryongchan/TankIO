using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

namespace TankIO
{
    // local cosmetic shell, never networked.
    // flies the same closed form as the server's copy and always ends with a generic landing effect,
    // hitting a tank is a separate effect driven only by the server's hit event, so a shown tank hit always dealt damage.
    // a miss is not an event at all.
    public class ShellVisual : MonoBehaviour
    {
        private static readonly Dictionary<int, ShellVisual> visualsByShellId = new Dictionary<int, ShellVisual>();

        private int shellId;
        private Vector3 muzzlePosition;
        private Vector3 aimPoint;
        private double fireTime;
        private float speed;
        private float hitRadius;
        private float serverHitFraction = float.PositiveInfinity; // the server's impact as a fraction of the flight, applied when the local flight reaches it
        private TankController serverHitTank;

        public static void Spawn(
            int shellId,
            Vector3 muzzlePosition,
            Vector3 aimPoint,
            double fireTime,
            float speed,
            float hitRadius
        )
        {
            GameObject shellObject = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            Destroy(shellObject.GetComponent<Collider>()); // must not catch the attack click raycast
            shellObject.transform.localScale = Vector3.one * 0.3f;
            shellObject.transform.position = muzzlePosition;
            aimPoint.y = muzzlePosition.y; // the server aims level at y=0; fly the visual level at barrel height instead of diving into the target's base
            ShellVisual shell = shellObject.AddComponent<ShellVisual>();
            shell.shellId = shellId;
            shell.muzzlePosition = muzzlePosition;
            shell.aimPoint = aimPoint;
            // a broadcast delayed past the whole flight time would be born already landed and never seen.
            // play a late flight from arrival instead; normal arrivals sit slightly before fireTime and are unaffected.
            shell.fireTime = System.Math.Max(fireTime, NetworkManager.Singleton.ServerTime.Time);
            shell.speed = speed;
            shell.hitRadius = hitRadius;
            visualsByShellId[shellId] = shell;
        }

        // the server resolved this shell as a hit on hitTank. the fraction says when along the flight, so an event
        // arriving early (the local clock trails the server) cannot cut the shell off midair. the marker then rides
        // hitTank's locally drawn body, not the stale committed point the shell flew to.
        public static void Impact(int shellId, float hitFraction, TankController hitTank)
        {
            if (visualsByShellId.TryGetValue(shellId, out ShellVisual shell))
            {
                shell.serverHitFraction = Mathf.Min(hitFraction, 1f); // clamped so a known hit always resolves before the generic landing
                shell.serverHitTank = hitTank;
            }
            else
            {
                // the shell already landed and took its position with it, so this marks the tank, not the contact point
                SpawnDebugMarker(hitTank.transform.position + Vector3.up * 0.8f, Color.green);
            }
        }

        void OnDestroy()
        {
            visualsByShellId.Remove(shellId);
        }

        void Update()
        {
            float flightLength = (aimPoint - muzzlePosition).magnitude;
            // the local clock can read slightly before fireTime when the broadcast arrives early, hence the clamp
            float distanceTraveled =
                speed * Mathf.Max(0f, (float)(NetworkManager.Singleton.ServerTime.Time - fireTime));
            transform.position = Vector3.MoveTowards(muzzlePosition, aimPoint, distanceTraveled);

            // the tank-hit effect comes only from the server's hit event, never a local guess, so it always dealt damage
            float serverHitDistance = serverHitFraction * flightLength;
            if (distanceTraveled >= serverHitDistance)
            {
                if (serverHitTank != null)
                    MarkHitOn(serverHitTank); // marker rides the hit tank, wherever it is drawn here
                else
                {
                    // the hit despawned the tank (a killing blow), so there is no body to ride; mark where the shell is
                    SpawnDebugMarker(transform.position, Color.green);
                    Destroy(gameObject);
                }
                return;
            }

            if (distanceTraveled >= flightLength)
            {
                SpawnDebugMarker(aimPoint, Color.red); // the generic landing: the shell ended here, no verdict implied
                Destroy(gameObject);
            }
        }

        // the marker sits in world space on the tank's surface facing the shell, marking where contact happened.
        // it does not follow the tank: a moving tank drives out from under it over the marker's lifetime.
        void MarkHitOn(TankController tank)
        {
            Vector3 contactOffset = (transform.position - tank.transform.position).normalized * hitRadius;
            SpawnDebugMarker(tank.transform.position + contactOffset, Color.green);
            Destroy(gameObject);
        }

        // debug stand-ins for the two effect classes: green = the server-confirmed tank hit, red = the generic landing
        static GameObject SpawnDebugMarker(Vector3 position, Color color)
        {
            GameObject marker = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            Destroy(marker.GetComponent<Collider>());
            marker.transform.localScale = Vector3.one * 0.2f;
            marker.transform.position = position;
            marker.GetComponent<Renderer>().material.color = color;
            Destroy(marker, 1f);
            return marker;
        }
    }
}
