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
        [SerializeField]
        private GameObject hitMarker; // the server confirmed this shell hit; a shell owns the marks it leaves

        [SerializeField]
        private GameObject landingMarker; // the shell reached its aim point, no verdict implied

        private static readonly Dictionary<int, ShellVisual> visualsByShellId = new Dictionary<int, ShellVisual>();

        // a hit event can outlive the shell it belongs to, and then there is no instance to read the prefab
        // from. stays null until some shell has spawned here: interest management can hide a shooter, whose
        // fire event then never arrives while the victim's impact event still does.
        private static GameObject lastHitMarker;

        private Renderer[] renderers;
        private bool visible = true;
        private int shellId;
        private Vector3 muzzlePosition;
        private Vector3 aimPoint;
        private double fireTime;
        private float speed;
        private float hitRadius;
        private float serverHitFraction = float.PositiveInfinity; // the server's impact as a fraction of the flight, applied when the local flight reaches it
        private Component serverHitTarget; // tank or HQ; only its drawn transform is read

        public static void Spawn(
            GameObject prefab,
            int shellId,
            Vector3 muzzlePosition,
            Vector3 aimPoint,
            double fireTime,
            float speed,
            float hitRadius
        )
        {
            GameObject shellObject = Instantiate(prefab, muzzlePosition, Quaternion.identity);
            aimPoint.y = muzzlePosition.y; // the server aims level at y=0; fly the visual level at barrel height instead of diving into the target's base
            ShellVisual shell = shellObject.GetComponent<ShellVisual>();
            shell.renderers = shellObject.GetComponentsInChildren<Renderer>();
            shell.shellId = shellId;
            shell.muzzlePosition = muzzlePosition;
            shell.aimPoint = aimPoint;
            // a broadcast delayed past the whole flight time would be born already landed and never seen.
            // play a late flight from arrival instead; normal arrivals sit slightly before fireTime and are unaffected.
            shell.fireTime = System.Math.Max(fireTime, NetworkManager.Singleton.ServerTime.Time);
            shell.speed = speed;
            shell.hitRadius = hitRadius;
            lastHitMarker = shell.hitMarker;
            visualsByShellId[shellId] = shell;
        }

        // the server resolved this shell as a hit on hitTarget (tank or HQ). the fraction says when along the
        // flight, so an event arriving early (the local clock trails the server) cannot cut the shell off midair.
        // the marker then rides hitTarget's locally drawn body, not the stale committed point the shell flew to.
        public static void Impact(int shellId, float hitFraction, Component hitTarget)
        {
            if (visualsByShellId.TryGetValue(shellId, out ShellVisual shell))
            {
                shell.serverHitFraction = Mathf.Min(hitFraction, 1f); // clamped so a known hit always resolves before the generic landing
                shell.serverHitTarget = hitTarget;
            }
            else if (lastHitMarker != null)
            {
                // the shell already landed and took its position with it, so this marks the target, not the contact point
                SpawnMarker(lastHitMarker, hitTarget.transform.position + Vector3.up * 0.8f);
            }
        }

        void OnDestroy()
        {
            visualsByShellId.Remove(shellId);
        }

        void Update()
        {
            if (NetworkManager.Singleton == null)
            {
                Destroy(gameObject); // the session driving this shell's clock is gone
                return;
            }
            // the flight still runs at every tier so a mid-flight zoom-in finds the shell where it should be; only the drawing is gated
            bool near = CameraController.Lod == LodTier.Near;
            if (visible != near)
            {
                visible = near;
                foreach (Renderer shellRenderer in renderers)
                    shellRenderer.enabled = near;
            }

            float flightLength = (aimPoint - muzzlePosition).magnitude;
            // the local clock can read slightly before fireTime when the broadcast arrives early, hence the clamp
            float distanceTraveled =
                speed * Mathf.Max(0f, (float)(NetworkManager.Singleton.ServerTime.Time - fireTime));
            transform.position = Vector3.MoveTowards(muzzlePosition, aimPoint, distanceTraveled);

            // the tank-hit effect comes only from the server's hit event, never a local guess, so it always dealt damage
            float serverHitDistance = serverHitFraction * flightLength;
            if (distanceTraveled >= serverHitDistance)
            {
                if (serverHitTarget != null)
                    MarkHitOn(serverHitTarget); // marker rides the hit target, wherever it is drawn here
                else
                {
                    // the hit despawned the target (a killing blow), so there is no body to ride; mark where the shell is
                    SpawnMarker(hitMarker, transform.position);
                    Destroy(gameObject);
                }
                return;
            }

            if (distanceTraveled >= flightLength)
            {
                SpawnMarker(landingMarker, aimPoint);
                Destroy(gameObject);
            }
        }

        // the marker sits in world space on the target's surface facing the shell, marking where contact happened.
        // it does not follow the target: a moving tank drives out from under it over the marker's lifetime.
        void MarkHitOn(Component target)
        {
            Vector3 contactOffset = (transform.position - target.transform.position).normalized * hitRadius;
            SpawnMarker(hitMarker, target.transform.position + contactOffset);
            Destroy(gameObject);
        }

        static void SpawnMarker(GameObject prefab, Vector3 position)
        {
            if (CameraController.Lod != LodTier.Near)
                return; // sub-tile flashes, invisible from higher up
            Destroy(Instantiate(prefab, position, Quaternion.identity), 1f);
        }
    }
}
