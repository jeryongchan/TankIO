using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace TankIO
{
    // one pooled overlay for every health bar floating over the world.
    // every machine sees every HQ's bar.
    // a tank's bar is also its selection marker, RA2-style, so it draws only while that tank is selected
    public class WorldHealthBars : MonoBehaviour
    {
        private const float HqBarWidth = 60f;
        private const float HqBarHeight = 6f;
        private const float HqBarHeightAboveBase = 2.2f;
        private const float TankBarWidth = 44f;
        private const float TankBarHeight = 5f;
        private const float TankBarHeightAboveTank = 1.4f;

        [SerializeField]
        private Image barPrefab; // the background image; its first child is the fill

        private readonly List<Image> backgrounds = new List<Image>();
        private readonly List<Image> fills = new List<Image>();
        private int usedBars;

        void LateUpdate()
        {
            usedBars = 0;
            Camera mainCamera = Camera.main;
            if (mainCamera != null)
            {
                foreach (HqController hq in HqController.SpawnedHqs)
                {
                    Place(
                        mainCamera,
                        hq.transform.position + Vector3.up * HqBarHeightAboveBase,
                        HqBarWidth,
                        HqBarHeight,
                        hq.HealthFraction,
                        Color.Lerp(Color.red, Color.green, hq.HealthFraction)
                    );
                }
                foreach (TankController tank in TankController.SpawnedTanks)
                {
                    if (tank.IsSelected)
                        Place(
                            mainCamera,
                            tank.transform.position + Vector3.up * TankBarHeightAboveTank,
                            TankBarWidth,
                            TankBarHeight,
                            tank.HealthFraction,
                            Color.green
                        );
                }
            }
            for (int index = usedBars; index < backgrounds.Count; index++)
                backgrounds[index].gameObject.SetActive(false);
        }

        void Place(Camera mainCamera, Vector3 worldPosition, float width, float height, float fraction, Color fillColor)
        {
            Vector3 screen = mainCamera.WorldToScreenPoint(worldPosition);
            if (screen.z <= 0f)
                return; // behind the camera
            if (usedBars == backgrounds.Count)
                CreateBar();
            Image background = backgrounds[usedBars];
            Image fill = fills[usedBars];
            usedBars++;

            background.gameObject.SetActive(true);
            background.rectTransform.sizeDelta = new Vector2(width, height);
            background.rectTransform.position = new Vector3(screen.x, screen.y, 0f);
            fill.rectTransform.sizeDelta = new Vector2(width * Mathf.Clamp01(fraction), 0f);
            fill.color = fillColor;
        }

        void CreateBar()
        {
            Image background = Instantiate(barPrefab, transform);
            backgrounds.Add(background);
            fills.Add(background.transform.GetChild(0).GetComponent<Image>());
        }
    }
}
