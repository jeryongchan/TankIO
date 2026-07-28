using Unity.Netcode;
using UnityEngine;
using UnityEngine.UI;

namespace TankIO
{
    // [Move], future maybe have [Upgrade]
    public class HqActionsHud : MonoBehaviour
    {
        [SerializeField]
        private Button moveButton;

        void Awake()
        {
            moveButton.onClick.AddListener(() => PlayerCommander.Instance.BeginPlacingHq());
        }

        void LateUpdate()
        {
            HqController hq = PlayerCommander.Instance.SelectedHq; // only an own HQ can be selected
            // hidden while the base is moving
            bool parked = hq != null && hq.IsParked(NetworkManager.Singleton.ServerTime.Time);
            moveButton.gameObject.SetActive(parked && CameraController.TryPin(transform, hq.transform.position));
        }
    }
}
