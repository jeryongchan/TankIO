using TMPro;
using UnityEngine;

namespace TankIO
{
    // a 1/2/3 over each of your own tanks, matching the slot it occupies in the tank strip hud
    public class TankSlotBadges : MonoBehaviour
    {
        [SerializeField]
        private TankSlotsHud slots; // the slot-to-tank mapping, so map and strip cannot disagree

        [SerializeField]
        private RectTransform badgePrefab;

        private RectTransform[] badges;

        void Awake()
        {
            badges = new RectTransform[HqController.MaxDeployedTanks];
            for (int slot = 0; slot < badges.Length; slot++)
            {
                badges[slot] = Instantiate(badgePrefab, transform);
                badges[slot].GetComponentInChildren<TMP_Text>(true).text = (slot + 1).ToString();
            }
        }

        void LateUpdate()
        {
            bool drawable = CameraController.Lod == LodTier.Near;
            for (int slot = 0; slot < badges.Length; slot++)
            {
                TankController tank = drawable ? slots.TankInSlot(slot) : null;
                badges[slot].gameObject.SetActive(
                    tank != null && CameraController.TryPin(badges[slot], tank.transform.position)
                );
            }
        }
    }
}
