using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

namespace TankIO
{
    // local cosmetic wreck, never networked: the damaged tank driving home after death. 
    // when the home HQ relocates mid-drive, both turn toward the new destination (see RetargetFor).
    public class WreckVisual : MonoBehaviour
    {
        private ulong ownerClientId;
        private Vector3 startPosition; // the death point, until a mid-drive HQ move re-anchors the line
        private Vector3 homePosition;
        private double startTime;
        private float speed;

        // every wreck on this machine, so an HQ move can find the ones driving home to it
        private static readonly List<WreckVisual> liveWrecks = new List<WreckVisual>();

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetSessionState()
        {
            liveWrecks.Clear();
        }

        public static void Spawn(ulong ownerClientId, Vector3 deathPosition, Vector3 homePosition, double startTime, float speed)
        {
            GameObject wreckObject = GameObject.CreatePrimitive(PrimitiveType.Cube);
            Destroy(wreckObject.GetComponent<Collider>()); // must not catch click raycasts
            wreckObject.transform.localScale = new Vector3(0.6f, 0.25f, 0.8f); // squashed: a hull, not a fighting tank
            wreckObject.transform.position = deathPosition;
            wreckObject.GetComponent<Renderer>().material.color = new Color(0.25f, 0.2f, 0.2f);
            WreckVisual wreck = wreckObject.AddComponent<WreckVisual>();
            wreck.ownerClientId = ownerClientId;
            wreck.startPosition = deathPosition;
            wreck.homePosition = homePosition;
            // a broadcast delayed past the start would be born mid-drive; play a late one from arrival instead
            wreck.startTime = System.Math.Max(startTime, NetworkManager.Singleton.ServerTime.Time);
            wreck.speed = speed;
            liveWrecks.Add(wreck);
        }

        void OnDestroy()
        {
            liveWrecks.Remove(this);
        }

        // this owner's HQ picked a new tile: drive on from wherever the wreck is now.
        // the server re-aims at its own clock, so its troop return can land a delivery delay off ours.
        public static void RetargetFor(ulong ownerClientId, Vector3 newHomePosition)
        {
            foreach (WreckVisual wreck in liveWrecks)
            {
                if (wreck.ownerClientId != ownerClientId)
                    continue;
                wreck.startPosition = wreck.transform.position;
                wreck.homePosition = new Vector3(newHomePosition.x, wreck.transform.position.y, newHomePosition.z);
                wreck.startTime = NetworkManager.Singleton.ServerTime.Time;
            }
        }

        void Update()
        {
            float distanceDriven =
                speed * Mathf.Max(0f, (float)(NetworkManager.Singleton.ServerTime.Time - startTime));
            transform.position = Vector3.MoveTowards(startPosition, homePosition, distanceDriven);
            Vector3 driveDirection = homePosition - startPosition;
            driveDirection.y = 0f;
            if (driveDirection != Vector3.zero)
                transform.rotation = Quaternion.LookRotation(driveDirection);
            if (distanceDriven >= driveDirection.magnitude)
                Destroy(gameObject); // the server runs this same arithmetic to decide when the troops come back
        }
    }
}
