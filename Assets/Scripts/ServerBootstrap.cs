using Unity.Netcode;
using Unity.Netcode.Transports.UTP;
using UnityEngine;

namespace TankIO
{
    // the dedicated server build runs batchmode with no GUI. 
    // Also covers editor -batchmode runs, which want the same headless server.
    public class ServerBootstrap : MonoBehaviour
    {
        void Start()
        {
            if (!Application.isBatchMode)
                return;

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
    }
}
