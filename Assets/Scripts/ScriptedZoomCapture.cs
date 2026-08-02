using Unity.Netcode;
using UnityEngine;

namespace TankIO
{
    // deterministic zoom for the LOD comparison recording: the path is a pure function of elapsed
    // time from a fixed ground point, so two takes align frame for frame. 2 starts the take,
    // 3 toggles LOD, 4 releases the camera (number keys: browsers own the F-keys).
    // runs before CameraController so it sees the scripted zoom when it applies distance and clamp
    [DefaultExecutionOrder(-50)]
    public class ScriptedZoomCapture : MonoBehaviour
    {
        [SerializeField]
        private CameraController cameraController;

        [SerializeField]
        private bool autoStartOnSession; // arm at session start so runs share one timeline

        [SerializeField]
        private float autoStartDelay = 5f; // seconds before the take begins, to let the bots build a scene

        [SerializeField]
        private Vector3 groundPoint; // the fixed look-at target on the ground plane

        [SerializeField]
        private float startZoom = 4f;

        [SerializeField]
        private float endZoom = 32f; // past farTierZoom so the take crosses every boundary

        [SerializeField]
        private float leadInSeconds = 1.5f; // hold the start zoom as a baseline

        [SerializeField]
        private float zoomSeconds = 20f;

        private Camera cam;
        private bool active;
        private float startTime;
        private float sessionStartTime = -1f;
        private bool autoStarted;

        void Update()
        {
            HandleKeys();
            HandleAutoStart();
            if (!active)
                return;
            if (cam == null)
            {
                cam = cameraController.GetComponent<Camera>();
                if (cam == null)
                    return;
            }
            float elapsed = Time.unscaledTime - startTime - leadInSeconds;
            float t = Mathf.Clamp01(elapsed / zoomSeconds);
            t = t * t * (3f - 2f * t); // smoothstep: no velocity jump at either end
            cam.orthographicSize = Mathf.Lerp(startZoom, endZoom, t);
            cameraController.CenterOn(groundPoint);
        }

        // anchored to session start, the frame the server spawns the world, so the zoom hits the same world age every run
        void HandleAutoStart()
        {
            if (!autoStartOnSession || autoStarted)
                return;
            NetworkManager network = NetworkManager.Singleton;
            if (sessionStartTime < 0f)
            {
                if (network != null && (network.IsListening || network.IsConnectedClient))
                    sessionStartTime = Time.unscaledTime;
                return;
            }
            if (Time.unscaledTime - sessionStartTime < autoStartDelay)
                return;
            autoStarted = true;
            active = true;
            startTime = Time.unscaledTime;
        }

        // editor only: 2, 3 and 4 are ordinary keys a player can hit by accident, and a hijacked
        // camera looks like the game breaking
        void HandleKeys()
        {
#if UNITY_EDITOR
#if ENABLE_INPUT_SYSTEM && !ENABLE_LEGACY_INPUT_MANAGER
            var keyboard = UnityEngine.InputSystem.Keyboard.current;
            if (keyboard == null)
                return;
            bool start = keyboard.digit2Key.wasPressedThisFrame;
            bool toggleLod = keyboard.digit3Key.wasPressedThisFrame;
            bool release = keyboard.digit4Key.wasPressedThisFrame;
#else
            bool start = Input.GetKeyDown(KeyCode.Alpha2);
            bool toggleLod = Input.GetKeyDown(KeyCode.Alpha3);
            bool release = Input.GetKeyDown(KeyCode.Alpha4);
#endif
            if (start)
            {
                active = true;
                startTime = Time.unscaledTime;
            }
            if (toggleLod)
                cameraController.LodEnabled = !cameraController.LodEnabled;
            if (release)
                active = false;
#endif
        }
    }
}
