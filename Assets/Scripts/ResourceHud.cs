using TMPro;
using Unity.Netcode;
using UnityEngine;

namespace TankIO
{
    public class ResourceHud : MonoBehaviour
    {
        [SerializeField] private TMP_Text ownerLine;

        void Update()
        {
            HqController hq = HqController.LocalPlayerHq;
            if (NetworkManager.Singleton == null || hq == null)
            {
                ownerLine.text = "";
                return;
            }
            double now = NetworkManager.Singleton.ServerTime.Time;
            string transit = hq.IsParked(now) ? "" : "  (in transit)";
            int fielded = HqController.MaxTroops - hq.TroopCeiling;
            string fieldedNote = fielded > 0 ? $"  ({fielded} fielded)" : "";
            ownerLine.text =
                $"Gold: {hq.Gold(now):0}  (+{hq.GoldRatePerSecond:0.#}/s)    "
                + $"Troops: {hq.HomeTroops(now):0} / {hq.TroopCeiling}  (+{hq.TroopRatePerSecond:0.#}/s)"
                + fieldedNote
                + transit;
        }
    }
}
