using Unity.Netcode;
using Unity.Netcode.Transports.UTP;
using UnityEngine;

namespace TankIO
{
    // throwaway dev HUD to start a session. Server is the real target (a dedicated server owns the world and
    // holds no tank); Host is the same thing fused with a local client, which is quicker to test with.
    public class NetworkDebugHud : MonoBehaviour
    {
        string address = "127.0.0.1";
        string port = "7777";

        void OnGUI()
        {
            NetworkManager networkManager = NetworkManager.Singleton;
            if (networkManager == null)
                return; // not spawned yet, or already torn down

            GUILayout.BeginArea(new Rect(Screen.width - 210f, 10f, 200f, 200f)); // top-right, clear of the canvas HUD

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
                GUILayout.BeginHorizontal();
                address = GUILayout.TextField(address);
                port = GUILayout.TextField(port, GUILayout.Width(50f));
                GUILayout.EndHorizontal();
                if (GUILayout.Button("client") && ushort.TryParse(port, out ushort parsedPort))
                {
                    // the transport asset stays on localhost; the target server is set here per session
                    networkManager.GetComponent<UnityTransport>().SetConnectionData(address, parsedPort);
                    networkManager.StartClient();
                }
            }

            GUILayout.EndArea();
        }
    }
}
