using System.Collections.Generic;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;

namespace TankIO
{
    // server-side interest management: each client is only told about tanks inside the rect it watches.
    // for HQ, the base information like position is always known; only the details like resource is interest managed like the tanks are
    public class InterestManager : MonoBehaviour
    {
        [SerializeField]
        private bool interestManagement; // off = full replication

        [SerializeField]
        private float watchMargin = 50f; // world units past the viewport a client still subscribes, so when we pan around the units already exist.

        private const float RefreshInterval = 0.25f; // check if need update visibility of network objects
        private const string WatchMessage = "TankIO.Watch";

        public static InterestManager Instance { get; private set; }

        // the rect each client last reported watching, on the ground plane (x, z)
        private readonly Dictionary<ulong, Rect> watchRects = new Dictionary<ulong, Rect>();
        private readonly Plane groundPlane = new Plane(Vector3.up, Vector3.zero);
        private NetworkManager network;
        private float nextRefreshTime;

        private const float GizmoHeight = 1f; // off the ground so the lines dont z-fight the terrain

        // gizmo state. the drawn rect is the last one sent, which is what the server filters against
        private Rect lastSentRect;
        private bool hasSentRect;
        private readonly Vector3[] viewportCorners = new Vector3[4];

        void Awake()
        {
            Instance = this;
            network = GetComponent<NetworkManager>();
            // client ids never repeat, so a disconnected client's rect would otherwise linger for the life of the server
            network.OnClientDisconnectCallback += clientId => watchRects.Remove(clientId);
        }

        // with no session both flags below read false, so an idle NetworkManager does nothing here
        void Update()
        {
            if (Time.unscaledTime < nextRefreshTime)
                return;
            nextRefreshTime = Time.unscaledTime + RefreshInterval;

            if (network.IsServer)
            {
                // registering is idempotent, could help after a restart, which builds a fresh CustomMessagingManager
                // Use CustomMessagingManager for sending messages that belong to no NetworkObject (an RPC needs one to live on)
                network.CustomMessagingManager.RegisterNamedMessageHandler(WatchMessage, OnWatchMessage);
                RefreshVisibility();
            }
            else if (network.IsConnectedClient)
                SendWatchRect();
        }

        // initial visibility, consulted by NGO at tank spawn and again for each late joiner.
        // the periodic pass applies the same rule afterward, so the two never fight.
        public static bool TankVisibleTo(TankController tank, ulong clientId)
        {
            if (Instance == null || !Instance.interestManagement)
                return true;
            if (clientId == NetworkManager.ServerClientId)
                return true;
            if (tank.CommanderId == clientId)
                return true;
            double now = Instance.network.ServerTime.Time;
            return Instance.WatchRectContains(clientId, tank.PositionAtTime(now));
        }

        // initial visibility for an HQ's detail half; the HQ itself is never filtered
        public static bool HqDetailVisibleTo(HqController hq, ulong clientId)
        {
            if (Instance == null || !Instance.interestManagement)
                return true;
            if (clientId == NetworkManager.ServerClientId)
                return true;
            if (hq.CommanderId == clientId)
                return true;
            double now = Instance.network.ServerTime.Time;
            return Instance.WatchRectContains(clientId, hq.PositionAtTime(now));
        }

        // a show is a full spawn payload, so a pass that shows many at once is what fills the
        // transport send queue and drops the client. logged, not throttled: the count and the
        // client tell us whether a real burst happened or the same objects are being reshown
        private const int BurstLogThreshold = 30;
        private int showsThisPass;
        private int hidesThisPass;

        // server only! for each tank and HQ (network objects) in the world, we check against each client if they should be visible (only those that crossed its rect!)
        void RefreshVisibility()
        {
            showsThisPass = 0;
            hidesThisPass = 0;
            double now = network.ServerTime.Time;
            IReadOnlyList<ulong> clientIds = network.ConnectedClientsIds;
            List<TankController> tanks = TankController.SpawnedTanks;
            for (int index = 0; index < tanks.Count; index++)
            {
                TankController tank = tanks[index];
                RefreshVisibilityFor(tank.NetworkObject, tank.CommanderId, tank.PositionAtTime(now), clientIds);
            }
            List<HqDetail> hqDetails = HqDetail.Spawned;
            for (int index = 0; index < hqDetails.Count; index++)
            {
                HqDetail hqDetail = hqDetails[index];
                HqController hq = hqDetail.Hq;
                RefreshVisibilityFor(hqDetail.NetworkObject, hq.CommanderId, hq.PositionAtTime(now), clientIds);
            }
            if (showsThisPass >= BurstLogThreshold)
            {
                Debug.Log(
                    "Interest burst: "
                        + showsThisPass
                        + " shows, "
                        + hidesThisPass
                        + " hides across "
                        + clientIds.Count
                        + " clients, over "
                        + tanks.Count
                        + " tanks and "
                        + hqDetails.Count
                        + " details"
                );
            }
        }

        void RefreshVisibilityFor(
            NetworkObject networkObject,
            ulong commanderId,
            Vector3 position,
            IReadOnlyList<ulong> clientIds
        )
        {
            for (int clientIndex = 0; clientIndex < clientIds.Count; clientIndex++)
            {
                ulong clientId = clientIds[clientIndex];
                if (clientId == NetworkManager.ServerClientId)
                    continue; // the host's own client always sees everything
                bool visible = networkObject.IsNetworkVisibleTo(clientId); // right now, is this client receiving this object?
                bool shouldBeVisible =
                    !interestManagement || commanderId == clientId || WatchRectContains(clientId, position); // watchcontains means it's inside the rectangle you last reported watching.
                if (shouldBeVisible == visible) // If the visibility already match, skip this client and move on
                    continue;
                if (shouldBeVisible)
                {
                    networkObject.NetworkShow(clientId);
                    showsThisPass++;
                }
                else
                {
                    networkObject.NetworkHide(clientId);
                    hidesThisPass++;
                }
            }
        }

        bool WatchRectContains(ulong clientId, Vector3 position)
        {
            if (!watchRects.TryGetValue(clientId, out Rect rect))
                return false; // nothing reported yet: only own tanks and HQs until the first watch arrives
            return position.x >= rect.xMin
                && position.x <= rect.xMax
                && position.z >= rect.yMin
                && position.z <= rect.yMax;
        }

        void OnWatchMessage(ulong senderClientId, FastBufferReader payload)
        {
            payload.ReadValueSafe(out float minX);
            payload.ReadValueSafe(out float minZ);
            payload.ReadValueSafe(out float width);
            payload.ReadValueSafe(out float height);
            watchRects[senderClientId] = new Rect(minX, minZ, width, height);
        }

        // the viewport's four corners projected onto the ground, expanded by the margin
        void SendWatchRect()
        {
            if (!TryProjectViewportCorners(viewportCorners))
                return;
            if (CameraController.Lod == LodTier.Far)
            {
                SendRect(0f, 0f, -1f, -1f); // contains nothing: Far draws no tanks or HQ details, so the whole-map viewport would subscribe to nothing
                return;
            }
            float minX = float.PositiveInfinity;
            float minZ = float.PositiveInfinity;
            float maxX = float.NegativeInfinity;
            float maxZ = float.NegativeInfinity;
            for (int cornerIndex = 0; cornerIndex < 4; cornerIndex++)
            {
                Vector3 corner = viewportCorners[cornerIndex];
                minX = Mathf.Min(minX, corner.x);
                maxX = Mathf.Max(maxX, corner.x);
                minZ = Mathf.Min(minZ, corner.z);
                maxZ = Mathf.Max(maxZ, corner.z);
            }
            SendRect(
                minX - watchMargin,
                minZ - watchMargin,
                maxX - minX + watchMargin * 2f,
                maxZ - minZ + watchMargin * 2f
            );
        }

        // the viewport's four ground hits in perimeter order; false when there is no camera
        bool TryProjectViewportCorners(Vector3[] corners)
        {
            Camera camera = Camera.main;
            if (camera == null)
                return false;
            for (int cornerIndex = 0; cornerIndex < 4; cornerIndex++)
            {
                int viewportX = cornerIndex == 1 || cornerIndex == 2 ? 1 : 0; // (0,0) (1,0) (1,1) (0,1)
                int viewportY = cornerIndex >> 1;
                Ray ray = camera.ViewportPointToRay(new Vector3(viewportX, viewportY, 0f));
                groundPlane.Raycast(ray, out float distance);
                corners[cornerIndex] = ray.GetPoint(distance);
            }
            return true;
        }

#if UNITY_EDITOR
        void OnDrawGizmos()
        {
            // client-only: the host is never interest-filtered
            if (network == null || !network.IsConnectedClient || network.IsServer || !interestManagement)
                return;
            if (CameraController.Lod == LodTier.Far)
                return; // the sent rect is empty
            const float LineWidth = 5f; // screen pixels
            if (TryProjectViewportCorners(viewportCorners))
            {
                UnityEditor.Handles.color = Color.yellow;
                for (int cornerIndex = 0; cornerIndex < 4; cornerIndex++)
                {
                    Vector3 from = viewportCorners[cornerIndex] + Vector3.up * GizmoHeight;
                    Vector3 to = viewportCorners[(cornerIndex + 1) % 4] + Vector3.up * GizmoHeight;
                    UnityEditor.Handles.DrawLine(from, to, LineWidth);
                }
            }
            if (hasSentRect && lastSentRect.width > 0f)
            {
                UnityEditor.Handles.color = Color.magenta;
                Vector3 cornerA = new Vector3(lastSentRect.xMin, GizmoHeight, lastSentRect.yMin);
                Vector3 cornerB = new Vector3(lastSentRect.xMax, GizmoHeight, lastSentRect.yMin);
                Vector3 cornerC = new Vector3(lastSentRect.xMax, GizmoHeight, lastSentRect.yMax);
                Vector3 cornerD = new Vector3(lastSentRect.xMin, GizmoHeight, lastSentRect.yMax);
                UnityEditor.Handles.DrawLine(cornerA, cornerB, LineWidth);
                UnityEditor.Handles.DrawLine(cornerB, cornerC, LineWidth);
                UnityEditor.Handles.DrawLine(cornerC, cornerD, LineWidth);
                UnityEditor.Handles.DrawLine(cornerD, cornerA, LineWidth);
            }
        }
#endif

        void SendRect(float minX, float minZ, float width, float height)
        {
            lastSentRect = new Rect(minX, minZ, width, height);
            hasSentRect = true;
            using FastBufferWriter writer = new FastBufferWriter(16, Allocator.Temp);
            // Allocator.Temp is a buffer which lives outside C# managed heap, so the GC never see and collect it.
            // also the fastest unmanaged memory allocator, designed for short-lived data that lives for one frame or a single job scope. imported from unity.Collections
            writer.WriteValueSafe(minX);
            writer.WriteValueSafe(minZ);
            writer.WriteValueSafe(width);
            writer.WriteValueSafe(height);
            network.CustomMessagingManager.SendNamedMessage(WatchMessage, NetworkManager.ServerClientId, writer);
        } // disposed here due to the keyword 'using'
    }
}
