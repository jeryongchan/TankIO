using UnityEngine;
using UnityEngine.Rendering;

namespace TankIO
{
    // planar reflection for the puddles: a second camera, mirrored across the ground plane,
    // renders the scene into a low-res texture the ground shader samples by screen uv.
    [ExecuteAlways]
    public class PlanarReflection : MonoBehaviour
    {
        [SerializeField]
        private LayerMask reflectLayers; // the ground must stay out, or the mirrored scene is behind it

        [SerializeField, Range(0.1f, 1f)]
        private float resolutionScale = 0.5f; // the ripple wobble hides the softness, so quarter area is enough

        [SerializeField, Range(1f, 4f)]
        private float stretch = 1f; // >1 elongates reflections downward from each object's feet, like a low sun does shadows

        [SerializeField]
        private GameObject reflectionCameraPrefab; // clear flags, HDR, shadows and post live on the prefab

        private Camera mainCamera;
        private Camera reflectionCamera;
        private RenderTexture reflectionTexture;

        // instanced drawers (grass) check this to opt out of the mirror render, since they
        // have no GameObject layer for the culling mask to filter on
        public static Camera MirrorCamera { get; private set; }

        private static readonly int ReflectionTexId = Shader.PropertyToID("_PlanarReflectionTex");
        private static readonly int ReflectionOnId = Shader.PropertyToID("_PlanarReflectionOn");

        void Reset()
        {
            reflectLayers = ~LayerMask.GetMask("Water", "UI");
        }

        void OnEnable()
        {
            mainCamera = GetComponent<Camera>();
            RenderPipelineManager.beginCameraRendering += OnBeginCamera;
            RenderPipelineManager.endCameraRendering += OnEndCamera;
        }

        void OnDisable()
        {
            RenderPipelineManager.beginCameraRendering -= OnBeginCamera;
            RenderPipelineManager.endCameraRendering -= OnEndCamera;
            Shader.SetGlobalFloat(ReflectionOnId, 0f);
            if (reflectionCamera != null)
                DestroyImmediate(reflectionCamera.gameObject);
            reflectionCamera = null;
            MirrorCamera = null;
            if (reflectionTexture != null)
            {
                reflectionTexture.Release();
                DestroyImmediate(reflectionTexture);
                reflectionTexture = null;
            }
        }

        void LateUpdate()
        {
            if (reflectionCameraPrefab == null)
                return; // nothing to mirror with, and the shader flag is already 0
            EnsureResources();
            reflectionCamera.enabled = !Application.isPlaying || CameraController.Lod == LodTier.Near;
        }

        void EnsureResources()
        {
            int width = Mathf.Max(1, Mathf.RoundToInt(mainCamera.pixelWidth * resolutionScale));
            int height = Mathf.Max(1, Mathf.RoundToInt(mainCamera.pixelHeight * resolutionScale));
            if (reflectionTexture != null && (reflectionTexture.width != width || reflectionTexture.height != height))
            {
                reflectionTexture.Release();
                DestroyImmediate(reflectionTexture);
                reflectionTexture = null;
            }
            if (reflectionTexture == null)
            {
                reflectionTexture = new RenderTexture(width, height, 16)
                {
                    name = "PlanarReflection",
                    filterMode = FilterMode.Bilinear,
                };
            }

            if (reflectionCamera == null)
            {
                // hidden and unsaved, so the mirror never stay in the scene as an editable camera
                GameObject go = Instantiate(reflectionCameraPrefab);
                go.hideFlags = HideFlags.HideAndDontSave;
                reflectionCamera = go.GetComponent<Camera>();
                // derived, not authored: a number typed into the prefab stops ordering correctly
                // the moment the main camera's depth changes
                reflectionCamera.depth = mainCamera.depth - 10f;
                MirrorCamera = reflectionCamera;
            }
            reflectionCamera.cullingMask = reflectLayers;
            reflectionCamera.targetTexture = reflectionTexture;
        }

        void OnBeginCamera(ScriptableRenderContext context, Camera cam)
        {
            if (cam == reflectionCamera)
            {
                SyncReflection();
                // the mirror matrix flips triangle winding, so cull the opposite faces
                GL.invertCulling = true;
            }
            else
            {
                bool ready = cam == mainCamera && reflectionCamera != null && reflectionCamera.enabled;
                // per camera, not per frame: scene view and previews get 0, so only the one
                // view whose screen uvs actually match the texture ever samples it.
                Shader.SetGlobalFloat(ReflectionOnId, ready ? 1f : 0f);
                if (ready)
                    Shader.SetGlobalTexture(ReflectionTexId, reflectionTexture);
            }
        }

        void OnEndCamera(ScriptableRenderContext context, Camera cam)
        {
            if (cam == reflectionCamera)
                GL.invertCulling = false;
        }

        void SyncReflection()
        {
            reflectionCamera.orthographicSize = mainCamera.orthographicSize;
            reflectionCamera.aspect = mainCamera.aspect;
            reflectionCamera.nearClipPlane = mainCamera.nearClipPlane;
            reflectionCamera.farClipPlane = mainCamera.farClipPlane;
            Vector3 p = mainCamera.transform.position;
            reflectionCamera.transform.position = new Vector3(p.x, -p.y, p.z);
            // every puddle lives on the y=0 ground plane, so one mirror serves the whole map.
            // heights scale by -stretch: feet stay pinned at the plane, tops reach further down.
            Matrix4x4 mirror = Matrix4x4.Scale(new Vector3(1f, -stretch, 1f));
            reflectionCamera.worldToCameraMatrix = mainCamera.worldToCameraMatrix * mirror;
            reflectionCamera.projectionMatrix = mainCamera.projectionMatrix;
        }
    }
}
