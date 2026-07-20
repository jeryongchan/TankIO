using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.InputSystem;

namespace TankIO
{
    // selection is never replicated: the server only knows each tank's command, not which tanks were selected together.
    public class PlayerCommander : MonoBehaviour
    {
        private const float DragThresholdPixels = 8f; // below this a press and release is a click, not a box
        private const float BoxBorderThickness = 2f;

        private readonly List<TankController> selection = new List<TankController>();
        private readonly Plane groundPlane = new Plane(Vector3.up, Vector3.zero);
        private Camera mainCamera;

        private bool dragging;
        private Vector2 dragStartScreenPosition;
        private Vector2 dragCurrentScreenPosition;

        // nothing here is authored, so it creates itself rather than needing a scene object someone can forget
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Create()
        {
            GameObject commanderObject = new GameObject(nameof(PlayerCommander));
            DontDestroyOnLoad(commanderObject);
            commanderObject.AddComponent<PlayerCommander>();
        }

        void Start()
        {
            mainCamera = Camera.main;
        }

        void Update()
        {
            if (NetworkManager.Singleton == null || !NetworkManager.Singleton.IsClient)
                return; // a dedicated server has nobody to take input from

            // stands in for touch's press-and-hold on empty ground, which drags a box that catches nothing
            Keyboard keyboard = Keyboard.current;
            if (keyboard != null && keyboard.escapeKey.wasPressedThisFrame)
            {
                RemoveDestroyedTanks();
                DeselectAll();
            }

            Mouse mouse = Mouse.current;
            if (mouse == null)
                return;
            Vector2 mousePosition = mouse.position.ReadValue();

            if (mouse.leftButton.wasPressedThisFrame)
            {
                dragStartScreenPosition = mousePosition;
                dragCurrentScreenPosition = mousePosition;
                dragging = true;
                return; // what the press meant is only known on release
            }
            if (dragging)
                dragCurrentScreenPosition = mousePosition;
            if (!mouse.leftButton.wasReleasedThisFrame)
                return;

            dragging = false;
            RemoveDestroyedTanks();
            if (IsDragPastThreshold(mousePosition))
                SelectInsideBox(dragStartScreenPosition, mousePosition);
            else
                HandleClick(mousePosition);
        }

        bool IsDragPastThreshold(Vector2 endScreenPosition) // only count as drag once past threshold
        {
            return (endScreenPosition - dragStartScreenPosition).magnitude > DragThresholdPixels;
        }

        void HandleClick(Vector2 screenPosition)
        {
            Ray ray = mainCamera.ScreenPointToRay(screenPosition);

            Keyboard keyboard = Keyboard.current;
            if (keyboard != null && keyboard.ctrlKey.isPressed) // force fire (debug)
            {
                if (groundPlane.Raycast(ray, out float forceFireDistance))
                {
                    foreach (TankController tank in selection)
                        tank.SubmitForceFireCommandRpc(ray.GetPoint(forceFireDistance));
                }
                return;
            }

            TankController clickedTank = TankUnderCursor(ray);
            if (clickedTank != null)
            {
                if (clickedTank.IsOwner)
                {
                    DeselectAll();
                    AddToSelection(clickedTank);
                }
                else
                {
                    foreach (TankController tank in selection)
                        tank.Attack(clickedTank);
                }
                return;
            }

            groundPlane.Raycast(ray, out float groundDistance); // return grounddistance
            if (!TileGrid.Instance.WorldToTile(ray.GetPoint(groundDistance), out Vector2Int goal))
                return; // clicked outside the grid
            MoveSelectionTo(goal);
        }

        // search outward from the clicked tile for one unclaimed parking spot per tank,
        // then hand each spot to its nearest tank so nobody crosses paths.
        void MoveSelectionTo(Vector2Int clickedGoalTile)
        {
            // a selected tank's own park tile counts as free: it is about to vacate it
            List<ulong> selectionIds = new List<ulong>();
            foreach (TankController tank in selection)
                selectionIds.Add(tank.NetworkObjectId);
            List<Vector2Int> goalTiles = new List<Vector2Int>();
            TripReservations.FindUnclaimedParkTilesNear(clickedGoalTile, selection.Count, selectionIds, goalTiles); // calls the build ring algorithm
            List<TankController> unassignedTanks = new List<TankController>(selection);
            List<Vector2Int> unassignedTankTiles = new List<Vector2Int>();
            double now = NetworkManager.Singleton.ServerTime.Time;
            foreach (TankController tank in unassignedTanks)
            {
                TileGrid.Instance.WorldToTile(tank.PositionAtTime(now), out Vector2Int tankTile);
                unassignedTankTiles.Add(tankTile);
            }

            while (goalTiles.Count > 0 && unassignedTanks.Count > 0)
            {
                int bestGoalIndex = 0;
                int bestTankIndex = 0;
                int bestSquaredDistance = int.MaxValue;
                for (int goalIndex = 0; goalIndex < goalTiles.Count; goalIndex++)
                {
                    for (int tankIndex = 0; tankIndex < unassignedTanks.Count; tankIndex++)
                    {
                        Vector2Int delta = goalTiles[goalIndex] - unassignedTankTiles[tankIndex];
                        int squaredDistance = delta.x * delta.x + delta.y * delta.y;
                        if (squaredDistance < bestSquaredDistance)
                        {
                            bestSquaredDistance = squaredDistance;
                            bestGoalIndex = goalIndex;
                            bestTankIndex = tankIndex;
                        }
                    }
                }
                unassignedTanks[bestTankIndex].MoveTo(goalTiles[bestGoalIndex]);
                unassignedTanks.RemoveAt(bestTankIndex);
                unassignedTankTiles.RemoveAt(bestTankIndex);
                goalTiles.RemoveAt(bestGoalIndex);
            }
        }

        // a box that catches nothing still clears the selection, same as dragging over empty ground in RA2
        void SelectInsideBox(Vector2 corner, Vector2 oppositeCorner)
        {
            Rect box = ScreenRect(corner, oppositeCorner);
            DeselectAll();
            foreach (TankController tank in TankController.SpawnedTanks)
            {
                if (!tank.IsOwner)
                    continue;
                Vector3 tankScreenPosition = mainCamera.WorldToScreenPoint(tank.transform.position);
                if (box.Contains(new Vector2(tankScreenPosition.x, tankScreenPosition.y)))
                    AddToSelection(tank);
            }
        }

        void AddToSelection(TankController tank)
        {
            selection.Add(tank);
            tank.SetSelected(true);
        }

        void DeselectAll()
        {
            foreach (TankController tank in selection)
                tank.SetSelected(false);
            selection.Clear();
        }

        // a selected tank can be destroyed while it is still selected
        void RemoveDestroyedTanks()
        {
            for (int index = selection.Count - 1; index >= 0; index--)
            {
                if (selection[index] == null)
                    selection.RemoveAt(index);
            }
        }

        static TankController TankUnderCursor(Ray ray)
        {
            if (!Physics.Raycast(ray, out RaycastHit hit))
                return null;
            return hit.collider.GetComponentInParent<TankController>();
        }

        static Rect ScreenRect(Vector2 corner, Vector2 oppositeCorner)
        {
            Vector2 min = Vector2.Min(corner, oppositeCorner);
            Vector2 max = Vector2.Max(corner, oppositeCorner);
            return new Rect(min, max - min);
        }

        void OnGUI() // might need replace in future
        {
            if (!dragging || !IsDragPastThreshold(dragCurrentScreenPosition))
                return;
            // GUI space runs y down from the top, the mouse runs y up from the bottom
            Vector2 start = new Vector2(dragStartScreenPosition.x, Screen.height - dragStartScreenPosition.y);
            Vector2 current = new Vector2(dragCurrentScreenPosition.x, Screen.height - dragCurrentScreenPosition.y);
            Rect box = ScreenRect(start, current);

            Color previousColor = GUI.color; // shared with every other OnGUI this frame
            GUI.color = Color.green;
            DrawBoxEdge(new Rect(box.xMin, box.yMin, box.width, BoxBorderThickness));
            DrawBoxEdge(new Rect(box.xMin, box.yMax - BoxBorderThickness, box.width, BoxBorderThickness));
            DrawBoxEdge(new Rect(box.xMin, box.yMin, BoxBorderThickness, box.height));
            DrawBoxEdge(new Rect(box.xMax - BoxBorderThickness, box.yMin, BoxBorderThickness, box.height));
            GUI.color = previousColor;
        }

        static void DrawBoxEdge(Rect edge)
        {
            GUI.DrawTexture(edge, Texture2D.whiteTexture);
        }
    }
}
