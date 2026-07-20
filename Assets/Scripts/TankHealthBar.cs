using UnityEngine;

namespace TankIO
{
    // RA2 marks a selected unit with its health bar rather than a separate reticle, so this is the selection
    // indicator and the health readout at once. purely local: selection never leaves this machine.
    public class TankHealthBar : MonoBehaviour
    {
        private const float BarWidth = 0.9f;
        private const float BarHeight = 0.12f;
        private const float BarThickness = 0.02f;
        private const float HeightAboveTank = 1.4f;

        private TankController tank;
        private Transform fill;
        private Transform cameraTransform;

        public static TankHealthBar Create(TankController tank)
        {
            GameObject barObject = new GameObject("HealthBar");
            barObject.transform.SetParent(tank.transform, false);
            // the tank only ever turns about y, so an overhead offset stays overhead
            barObject.transform.localPosition = Vector3.up * HeightAboveTank;

            TankHealthBar bar = barObject.AddComponent<TankHealthBar>();
            bar.tank = tank;
            bar.cameraTransform = Camera.main.transform;
            CreateBox(barObject.transform, new Vector3(BarWidth, BarHeight, BarThickness), Color.black);
            bar.fill = CreateBox(barObject.transform, Vector3.one, Color.green);
            return bar;
        }

        void LateUpdate()
        {
            // the bar is a child of a turning tank, so its rotation is overwritten every frame rather than inherited.
            // matching the camera keeps local x pointing screen-right, which is the direction the fill grows.
            transform.rotation = cameraTransform.rotation;

            float fillWidth = BarWidth * tank.HealthFraction;
            fill.localScale = new Vector3(fillWidth, BarHeight, BarThickness);
            // shifted left by the missing half-width so the bar drains toward the left edge instead of the centre.
            // pulled toward the camera so it never z-fights the background.
            fill.localPosition = new Vector3((fillWidth - BarWidth) * 0.5f, 0f, -BarThickness);
        }

        // boxes rather than quads: a box reads from any angle, so nothing depends on which way the bar faces
        static Transform CreateBox(Transform parent, Vector3 localScale, Color color)
        {
            GameObject box = GameObject.CreatePrimitive(PrimitiveType.Cube);
            Destroy(box.GetComponent<Collider>()); // must not catch the selection raycast
            box.transform.SetParent(parent, false);
            box.transform.localScale = localScale;
            box.GetComponent<Renderer>().material.color = color;
            return box.transform;
        }
    }
}
