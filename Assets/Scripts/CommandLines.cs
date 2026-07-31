using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

namespace TankIO
{
    // a straight line from each selected tank to its goal, with a ping under the goal. client visual
    // only: it reads the same trip the tank renders from, so a reroute or recall moves it by itself.
    public class CommandLines : MonoBehaviour
    {
        [SerializeField]
        private LineRenderer linePrefab; // its transform y is the height above the ground

        [SerializeField]
        private Transform ripplePrefab; // the ping quad under the goal; its transform y is the height above the ground

        private readonly List<LineRenderer> lines = new List<LineRenderer>();
        private readonly List<Transform> ripples = new List<Transform>();
        private int usedLines;

        void LateUpdate()
        {
            usedLines = 0;
            if (NetworkManager.Singleton != null && CameraController.Lod == LodTier.Near)
            {
                foreach (TankController tank in PlayerCommander.Instance.Selection)
                {
                    if (tank == null)
                        continue;
                    Vector3? goal = tank.ActiveCommandGoal;
                    if (goal == null)
                        continue;
                    LineRenderer line = NextLine();
                    // the visible tank, not the gameplay position: the line should hang off the model
                    Vector3 start = tank.transform.position;
                    start.y = linePrefab.transform.position.y;
                    Vector3 end = goal.Value;
                    end.y = start.y;
                    line.SetPosition(0, start);
                    line.SetPosition(1, end);
                    NextRipple().position = new Vector3(end.x, ripplePrefab.position.y, end.z);
                }
            }
            for (int index = usedLines; index < lines.Count; index++)
                lines[index].enabled = false;
            for (int index = usedLines; index < ripples.Count; index++)
                ripples[index].gameObject.SetActive(false);
        }

        LineRenderer NextLine()
        {
            if (usedLines == lines.Count)
                lines.Add(Instantiate(linePrefab, transform));
            LineRenderer line = lines[usedLines];
            line.enabled = true;
            usedLines++;
            return line;
        }

        // called after NextLine, so usedLines already counts this pair
        Transform NextRipple()
        {
            if (usedLines > ripples.Count)
                ripples.Add(Instantiate(ripplePrefab, transform));
            Transform ripple = ripples[usedLines - 1];
            ripple.gameObject.SetActive(true);
            return ripple;
        }
    }
}
