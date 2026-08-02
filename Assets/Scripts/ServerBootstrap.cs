using Unity.Netcode;
using Unity.Netcode.Transports.UTP;
using UnityEngine;

namespace TankIO
{
    // the dedicated server build runs batchmode with no GUI. 
    // Also covers editor -batchmode runs, which want the same headless server.
    public class ServerBootstrap : MonoBehaviour
    {
        [SerializeField]
        private int maxPlayers = 40;

        void Start()
        {
            // both ends set this, even though only a server runs the callback: NGO folds the flag
            // into the config hash it compares at connect, and kicks any client that disagrees
            NetworkManager.Singleton.NetworkConfig.ConnectionApproval = true;
            NetworkManager.Singleton.ConnectionApprovalCallback = OnConnectionApproval;
            NetworkManager.Singleton.OnClientDisconnectCallback += CommanderNames.ServerForget;

            if (!Application.isBatchMode)
                return;

            // headless has no vsync, so an uncapped loop spins at thousands of fps and pins the
            // core. the cap also sets how often the transport drains its send queue, so it stays
            // well above the tick rate: at 30 a burst of visibility changes overflowed the queue
            Application.targetFrameRate = 60;

            if (LaunchArgs.TryGetInt("-maxPlayers", out int overrideMax))
                maxPlayers = Mathf.Max(1, overrideMax);

            UnityTransport transport = NetworkManager.Singleton.GetComponent<UnityTransport>();
            transport.SetConnectionData("127.0.0.1", 7777, "0.0.0.0"); // listen on all interfaces; the default only accepts same-machine clients
            if (!NetworkManager.Singleton.StartServer())
            {
                // a server that cannot listen must die visibly, not sit there looking alive. previously a bug where a second server failed to bind and never alert the user
                Debug.LogError("StartServer failed, quitting.");
                Application.Quit(1);
                return;
            }
            Debug.Log("Server listening on port 7777.");
        }

        void OnConnectionApproval(
            NetworkManager.ConnectionApprovalRequest request,
            NetworkManager.ConnectionApprovalResponse response
        )
        {
            bool full = NetworkManager.Singleton.ConnectedClientsIds.Count >= maxPlayers;
            response.Approved = !full;
            if (full)
                response.Reason = "world full"; // shows up client-side in NetworkManager.DisconnectReason
            else
            {
                // the payload is the name typed on the title screen, read before the HQ spawns
                CommanderNames.ServerRemember(request.ClientNetworkId, request.Payload);
                // the HQ count is what the joining client is sent up front: HQs are never interest
                // filtered, so it is the one part of the initial sync that grows with world size
                Debug.Log(
                    "Client "
                        + request.ClientNetworkId
                        + " approved as "
                        + CommanderNames.ForCommander(request.ClientNetworkId)
                        + ", world has "
                        + HqController.SpawnedHqs.Count
                        + " HQs and "
                        + TankController.SpawnedTanks.Count
                        + " tanks"
                );
            }
            response.CreatePlayerObject = false; // WorldSpawner spawns the HQ on the connect callback instead
        }
    }
}
