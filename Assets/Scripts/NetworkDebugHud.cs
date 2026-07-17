using Unity.Netcode;
using UnityEngine;

namespace TankIO
{
    // throwaway dev HUD to start a session. Server is the real target (a dedicated server owns the world and
    // holds no tank); Host is the same thing fused with a local client, which is quicker to test with.
    public class NetworkDebugHud : MonoBehaviour
    {
        void OnGUI()
        {
            NetworkManager networkManager = NetworkManager.Singleton;
            if (networkManager == null)
                return; // not spawned yet, or already torn down

            GUILayout.BeginArea(new Rect(10f, 10f, 200f, 200f));

            if (networkManager.IsClient || networkManager.IsServer)
            {
                GUILayout.Label(networkManager.IsServer ? "server" : "client");
                if (networkManager.IsServer) // the connected list is server-only
                    GUILayout.Label("clients: " + networkManager.ConnectedClientsIds.Count);
                if (GUILayout.Button("shutdown"))
                    networkManager.Shutdown();
            }
            else
            {
                if (GUILayout.Button("host"))
                    networkManager.StartHost();
                if (GUILayout.Button("server"))
                    networkManager.StartServer();
                if (GUILayout.Button("client"))
                    networkManager.StartClient();
            }

            GUILayout.EndArea();
        }
    }
}
