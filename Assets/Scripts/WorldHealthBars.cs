using System.Collections.Generic;
using TMPro;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.UI;

namespace TankIO
{
    // one pooled set of health bars for every unit and base on screen.
    public class WorldHealthBars : MonoBehaviour
    {
        [SerializeField]
        private Image barPrefab; // the white outline; its interior holds the fill, and a name label rides along

        // world units, so they have no RectTransform to live in
        [SerializeField]
        private float tankBarHeightAboveTank = 1.4f;

        [SerializeField]
        private float hqBarHeightAboveBase = 2.2f;

        private readonly List<Image> backgrounds = new List<Image>();
        private readonly List<Image> fills = new List<Image>();
        private readonly List<TMP_Text> nameLabels = new List<TMP_Text>();
        private readonly List<TMP_Text> troopLabels = new List<TMP_Text>();
        private int usedBars;

        void LateUpdate()
        {
            usedBars = 0;
            if (CameraController.Lod == LodTier.Near && NetworkManager.Singleton != null)
            {
                // HomeTroops() computes from a value plus elapsed time, so reading it every frame shows
                // the count going up even though the server has sent nothing since that base's last deploy
                double now = NetworkManager.Singleton.ServerTime.Time;
                foreach (HqDetail hqDetail in HqDetail.Spawned)
                {
                    HqController hq = hqDetail.Hq;
                    if (hq == null)
                        continue; // the two despawns are separate messages: the HQ's can land a frame first
                    if (!hq.IsSelected && !hq.IsInspected)
                        continue;
                    Place(
                        hq.transform.position + Vector3.up * hqBarHeightAboveBase,
                        hq.HealthFraction(now),
                        FillColor(hq.CommandedByLocalPlayer),
                        hq.IsInspected ? hq.DisplayName : "",
                        hq.HomeTroops(now).ToString("0") + " troops"
                    );
                }
                foreach (TankController tank in TankController.SpawnedTanks)
                {
                    if (!tank.IsSelected && !tank.IsInspected)
                        continue;
                    Place(
                        tank.transform.position + Vector3.up * tankBarHeightAboveTank,
                        tank.HealthFraction,
                        FillColor(tank.CommandedByLocalPlayer),
                        tank.IsInspected ? HqController.DisplayNameFor(tank.CommanderId) : "",
                        tank.Troops + " troops" // troops left, which is also what its damage scales with
                    );
                }
            }
            for (int index = usedBars; index < backgrounds.Count; index++)
                backgrounds[index].gameObject.SetActive(false);
        }

        // green for yours, red for theirs. never lerped by health - the fill's width already shows that,
        // and a colour doing both jobs makes neither readable at a glance.
        static Color FillColor(bool commandedByLocalPlayer)
        {
            return commandedByLocalPlayer ? Color.green : Color.red;
        }

        // pass "" for a line that should not appear: a selected but not inspected unit has no name to show
        void Place(Vector3 worldPosition, float fraction, Color fillColor, string name, string troops)
        {
            if (usedBars == backgrounds.Count)
                CreateBar();
            Image background = backgrounds[usedBars];
            // usedBars is not advanced when this fails, so the loop at the end of LateUpdate turns this bar off along with the rest of the unused ones
            if (!CameraController.TryPin(background.rectTransform, worldPosition))
                return;
            Image fill = fills[usedBars];
            TMP_Text nameLabel = nameLabels[usedBars];
            TMP_Text troopLabel = troopLabels[usedBars];
            usedBars++;

            background.gameObject.SetActive(true);
            // Fill is anchored to stretch across Interior with all four offsets at 0, so anchorMax.x is a
            // fraction of the bar's width.
            fill.rectTransform.anchorMax = new Vector2(fraction, 1f);
            fill.color = fillColor;
            SetLine(nameLabel, name);
            SetLine(troopLabel, troops);
        }

        static void SetLine(TMP_Text label, string text)
        {
            label.gameObject.SetActive(text.Length > 0);
            label.text = text;
        }

        void CreateBar()
        {
            Image background = Instantiate(barPrefab, transform);
            backgrounds.Add(background);
            // looked up by name, not by child index: the prefab's layout is meant to be rearranged freely,
            // and two TMP children mean there is no longer a single one to find by type
            fills.Add(background.transform.Find("Interior/Fill").GetComponent<Image>());
            nameLabels.Add(background.transform.Find("Name").GetComponent<TMP_Text>());
            troopLabels.Add(background.transform.Find("Troops").GetComponent<TMP_Text>());
        }
    }
}
