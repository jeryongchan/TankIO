using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;

namespace TankIO
{
    // selection is never replicated: the server only knows each tank's command, not which tanks were selected together.
    public class PlayerCommander : MonoBehaviour
    {
        private const float DragThresholdPixels = 8f; // below this a press and release is a click, not a box
        private const float BoxBorderThickness = 2f;

        private readonly List<TankController> selection = new List<TankController>();
        private HqController selectedHq; // never selected together with tanks: picking either deselects the other
        private readonly Plane groundPlane = new Plane(Vector3.up, Vector3.zero);
        private Camera mainCamera;

        private bool dragging;
        private Vector2 dragStartScreenPosition;
        private Vector2 dragCurrentScreenPosition;

        public static PlayerCommander Instance { get; private set; } // the tank strip routes slot clicks here

        // nothing here is authored, so it creates itself rather than needing a scene object someone can forget
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Create()
        {
            GameObject commanderObject = new GameObject(nameof(PlayerCommander));
            DontDestroyOnLoad(commanderObject);
            Instance = commanderObject.AddComponent<PlayerCommander>();
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
                if (EventSystem.current != null && EventSystem.current.IsPointerOverGameObject())
                    return; // the strip owns this press; the UI raycast consumes it and no world order fires
                dragStartScreenPosition = mousePosition;
                dragCurrentScreenPosition = mousePosition;
                dragging = true;
                return; // what the press meant is only known on release
            }
            if (dragging)
                dragCurrentScreenPosition = mousePosition;
            if (!mouse.leftButton.wasReleasedThisFrame)
                return;

            if (!dragging)
                return; // the press landed on the strip, so the release is not ours either
            dragging = false;
            RemoveDestroyedTanks();
            if (IsDragPastThreshold(mousePosition))
                SelectInsideBox(dragStartScreenPosition, mousePosition);
            else
                HandleClick(mousePosition);
        }

        void LateUpdate()
        {
            UpdateHqMovePreview();
        }

        // the confirm flow is hover-shows-cost, click-commits: the preview is the only UI the move needs
        void UpdateHqMovePreview()
        {
            HudCursorLabel.Hide();
            if (selectedHq == null || Mouse.current == null)
                return;
            Vector2 mousePosition = Mouse.current.position.ReadValue();
            Ray ray = mainCamera.ScreenPointToRay(mousePosition);
            if (!groundPlane.Raycast(ray, out float groundDistance))
                return;
            if (!TileGrid.Instance.WorldToTile(ray.GetPoint(groundDistance), out Vector2Int tile))
                return;
            tile = CapitalController.SnapToDock(tile); // preview the tile the confirm will actually use

            double now = NetworkManager.Singleton.ServerTime.Time;
            if (!selectedHq.IsParked(now))
                HudCursorLabel.Show("in transit", Color.red, mousePosition);
            else if (!selectedHq.IsValidDestination(tile))
                HudCursorLabel.Show("blocked", Color.red, mousePosition);
            else
            {
                float cost = HqController.MoveCost(selectedHq.HomeTile, tile);
                bool affordable = selectedHq.Gold(now) >= cost;
                HudCursorLabel.Show($"move: {cost:0} gold", affordable ? Color.green : Color.red, mousePosition);
            }
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
                if (clickedTank.CommandedByLocalPlayer)
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

            HqController clickedHq = HqUnderCursor(ray);
            if (clickedHq != null)
            {
                if (clickedHq.CommandedByLocalPlayer)
                {
                    // with tanks selected, clicking home is an order (recall); with nothing selected, a pick
                    if (selection.Count > 0)
                    {
                        foreach (TankController tank in selection)
                            tank.ReturnToHq(clickedHq);
                    }
                    else
                    {
                        DeselectAll();
                        selectedHq = clickedHq;
                    }
                }
                else
                {
                    foreach (TankController tank in selection)
                        tank.Attack(clickedHq); // siege: same standing order as attacking a tank
                }
                return;
            }

            groundPlane.Raycast(ray, out float groundDistance); // return grounddistance
            if (!TileGrid.Instance.WorldToTile(ray.GetPoint(groundDistance), out Vector2Int goal))
                return; // clicked outside the grid
            if (selectedHq != null)
            {
                selectedHq.RequestMove(CapitalController.SnapToDock(goal)); // hover already showed the cost; this click is the confirm
                return;
            }
            // alt+click ground: attack-move. alt+click on a tank or HQ already fell through to the plain attack above.
            MoveSelectionTo(goal, keyboard != null && keyboard.altKey.isPressed);
        }

        // search outward from the clicked tile for one unclaimed parking spot per tank,
        // then hand each spot to its nearest tank so nobody crosses paths.
        void MoveSelectionTo(Vector2Int clickedGoalTile, bool attackMove)
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
                if (attackMove)
                    unassignedTanks[bestTankIndex].AttackMoveTo(goalTiles[bestGoalIndex]);
                else
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
                if (!tank.CommandedByLocalPlayer)
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

        // a tank-strip slot click: same result as clicking the tank on the map
        public void SelectSingle(TankController tank)
        {
            RemoveDestroyedTanks();
            DeselectAll();
            AddToSelection(tank);
        }

        void DeselectAll()
        {
            foreach (TankController tank in selection)
                tank.SetSelected(false);
            selection.Clear();
            selectedHq = null;
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

        static HqController HqUnderCursor(Ray ray)
        {
            if (!Physics.Raycast(ray, out RaycastHit hit))
                return null;
            return hit.collider.GetComponentInParent<HqController>();
        }

        static Rect ScreenRect(Vector2 corner, Vector2 oppositeCorner)
        {
            Vector2 min = Vector2.Min(corner, oppositeCorner);
            Vector2 max = Vector2.Max(corner, oppositeCorner);
            return new Rect(min, max - min);
        }

        // the drag box stays IMGUI: four stretched textures, no interaction, nothing a canvas would improve
        void OnGUI()
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
