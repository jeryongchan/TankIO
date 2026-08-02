using System.Text;
using TMPro;
using Unity.Netcode;
using Unity.Netcode.Transports.UTP;
using UnityEngine;
using UnityEngine.UI;

namespace TankIO
{
    // the client build's front door: nothing connects until Join is pressed, and a refused or
    // dropped session comes back here carrying the server's reason
    public class TitleScreen : MonoBehaviour
    {
        [SerializeField]
        private GameObject panelRoot; // the Panel child, never the object holding this script: it gets SetActive(false)

        [SerializeField]
        private Button joinButton;

        [SerializeField]
        private TMP_Text statusText;

        [SerializeField]
        private TMP_InputField nameField;

        [SerializeField]
        private GameObject[] hiddenUntilJoined; // gameplay HUD that has nothing to show before a world exists, like the minimap

        [SerializeField]
        private string serverAddress = "127.0.0.1"; // the deployed build points this at the domain

        [SerializeField]
        private ushort serverPort = 7777; // 443 once it is behind the proxy

        [SerializeField]
        private bool secureWebSocket; // on for the deployed build: the proxy terminates TLS, so the client speaks wss and the server itself stays plain ws

        private bool waitingForShutdown;

        private const string NameKey = "commanderName";
        private const int MaxNameCharacters = 14; // the server truncates past this anyway

        void Start()
        {
            if (Application.isBatchMode)
            {
                gameObject.SetActive(false); // the dedicated server starts itself in ServerBootstrap
                return;
            }
            joinButton.onClick.AddListener(Join);
            NetworkManager.Singleton.OnClientConnectedCallback += OnConnected;
            NetworkManager.Singleton.OnClientDisconnectCallback += OnDisconnected;
            statusText.text = "";
            nameField.characterLimit = MaxNameCharacters;
            // a first-time player is handed a generated name rather than an empty box, so joining
            // takes one click and the roster never fills with untyped blanks
            nameField.text = PlayerPrefs.GetString(NameKey, CommanderNames.Generate((ulong)Random.Range(1, 1000000)));
            SetGameplayHudVisible(false);
            Debug.Log("Title screen ready, target " + serverAddress + ":" + serverPort);
        }

        void OnDestroy()
        {
            if (NetworkManager.Singleton == null) // teardown order on play mode exit is not guaranteed
                return;
            NetworkManager.Singleton.OnClientConnectedCallback -= OnConnected;
            NetworkManager.Singleton.OnClientDisconnectCallback -= OnDisconnected;
        }

        void Update()
        {
            // NGO finishes tearing the client down over the frames after a disconnect, and refuses
            // StartClient until it has: the button stays dead that long
            if (waitingForShutdown && !NetworkManager.Singleton.IsListening)
            {
                waitingForShutdown = false;
                joinButton.interactable = true;
            }
        }

        void Join()
        {
            Debug.Log("Join pressed, connecting to " + serverAddress + ":" + serverPort);
            joinButton.interactable = false;
            statusText.text = "connecting...";
            string chosenName = nameField.text.Trim();
            PlayerPrefs.SetString(NameKey, chosenName); // the same name comes back next visit
            // the only chance to tell the server anything before it spawns the HQ
            NetworkManager.Singleton.NetworkConfig.ConnectionData = Encoding.UTF8.GetBytes(chosenName);
            // the transport asset stays on localhost; the session's target is set here
            UnityTransport transport = NetworkManager.Singleton.GetComponent<UnityTransport>();
            transport.SetConnectionData(serverAddress, serverPort);
            transport.UseEncryption = secureWebSocket;
            if (secureWebSocket)
                transport.SetClientSecrets(serverAddress); // the name on the certificate, which is the domain we dialled
            if (NetworkManager.Singleton.StartClient())
                return;
            statusText.text = "could not start the client";
            joinButton.interactable = true;
        }

        void SetGameplayHudVisible(bool visible)
        {
            for (int index = 0; index < hiddenUntilJoined.Length; index++)
            {
                if (hiddenUntilJoined[index] != null)
                    hiddenUntilJoined[index].SetActive(visible);
            }
        }

        void OnConnected(ulong clientId)
        {
            Debug.Log("Connected as client " + clientId);
            panelRoot.SetActive(false);
            SetGameplayHudVisible(true);
        }

        // covers both ends of the connection: approval refusing the join, and the server dropping
        // or vanishing mid-session. an empty reason means nobody answered at all
        void OnDisconnected(ulong clientId)
        {
            panelRoot.SetActive(true);
            SetGameplayHudVisible(false);
            string reason = NetworkManager.Singleton.DisconnectReason;
            Debug.Log("Disconnected, reason: " + (string.IsNullOrEmpty(reason) ? "none given" : reason));
            statusText.text = string.IsNullOrEmpty(reason) ? "could not reach the server" : reason;
            NetworkManager.Singleton.Shutdown();
            waitingForShutdown = true;
        }
    }
}
