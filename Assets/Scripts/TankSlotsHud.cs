using System.Collections.Generic;
using TMPro;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace TankIO
{
    // similar to Last Z formation panel hud: one slot per tank/march.
    //  owns no gameplay and sends only the deploy request.
    public class TankSlotsHud : MonoBehaviour
    {
        [SerializeField]
        private Button[] slotButtons; // one per HqController.MaxDeployedTanks, wired in the scene

        private readonly List<TankController> ownedTanks = new List<TankController>();
        private TMP_Text[] slotLabels;
        private TankController[] slotTanks; // per slot: its deployed tank, or null when the slot is a deploy button

        void Awake()
        {
            slotLabels = new TMP_Text[slotButtons.Length];
            slotTanks = new TankController[slotButtons.Length];
            for (int slot = 0; slot < slotButtons.Length; slot++)
            {
                slotLabels[slot] = slotButtons[slot].GetComponentInChildren<TMP_Text>();
                int capturedSlot = slot;
                slotButtons[slot].onClick.AddListener(() => OnSlotClicked(capturedSlot));
            }
        }

        void Update()
        {
            // debug: a free full-strength tank past every gate, so combat stays testable without an economy grind
            Keyboard keyboard = Keyboard.current;
            if (keyboard != null && keyboard.f7Key.wasPressedThisFrame && HqController.LocalPlayerHq != null)
                HqController.LocalPlayerHq.SubmitDebugDeployRpc();

            HqController hq = HqController.LocalPlayerHq;
            foreach (Button button in slotButtons)
                button.gameObject.SetActive(hq != null);
            if (hq == null)
                return;
            double now = NetworkManager.Singleton.ServerTime.Time;

            ownedTanks.Clear();
            foreach (TankController tank in TankController.SpawnedTanks)
            {
                if (tank.CommandedByLocalPlayer)
                    ownedTanks.Add(tank);
            }
            // spawn order = slot order, so a slot keeps its tank while others come and go
            ownedTanks.Sort((tankA, tankB) => tankA.NetworkObjectId.CompareTo(tankB.NetworkObjectId));

            for (int slot = 0; slot < slotButtons.Length; slot++)
            {
                if (slot < ownedTanks.Count)
                    ShowDeployedSlot(slot, ownedTanks[slot]);
                else if (slot < ownedTanks.Count + hq.ReturningTanks)
                    ShowReturningSlot(slot);
                else
                    ShowEmptySlot(slot, hq, now);
            }
        }

        // a disabled button cannot fire, so a null tank here always means "deploy"
        // maybe later double click will focus camera on the tank
        void OnSlotClicked(int slot)
        {
            if (slotTanks[slot] != null)
                PlayerCommander.Instance.SelectSingle(slotTanks[slot]);
            else if (HqController.LocalPlayerHq != null)
                HqController.LocalPlayerHq.RequestDeploy();
        }

        void ShowDeployedSlot(int slot, TankController tank)
        {
            slotLabels[slot].text = $"Tank {slot + 1}";
            slotButtons[slot].interactable = true;
            slotTanks[slot] = tank;
        }

        // a wreck driving home still owns its slot: the drive is the redeploy cooldown
        void ShowReturningSlot(int slot)
        {
            slotLabels[slot].text = "returning...";
            slotButtons[slot].interactable = false;
            slotTanks[slot] = null;
        }

        void ShowEmptySlot(int slot, HqController hq, double now)
        {
            int troopsToTake = Mathf.Min(HqController.TroopsPerTank, (int)hq.HomeTroops(now));
            bool canDeploy = hq.IsParked(now) && troopsToTake > 0;
            slotLabels[slot].text = !hq.IsParked(now) ? "in transit" : $"Deploy\n{troopsToTake} troops";
            slotButtons[slot].interactable = canDeploy;
            slotTanks[slot] = null;
        }
    }
}
