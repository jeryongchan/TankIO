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
        private const float DragThresholdPixels = 64f; // below this a press and release is a click, not a box
        private const float BoxBorderThickness = 3f;

        [SerializeField]
        private Color dragBoxColor = Color.green;

        private readonly List<TankController> selection = new List<TankController>();
        private HqController selectedHq; // never selected together with tanks: picking either deselects the other
        private bool placingHq; // a [Move] press is waiting for its ground click

        // the last enemy clicked, held only so its health bar draws. one at a time, and never both at once.
        private TankController inspectedTank;
        private HqController inspectedHq;
        private readonly Plane groundPlane = new Plane(Vector3.up, Vector3.zero);
        private Camera mainCamera;

        private bool dragging;
        private Vector2 dragStartScreenPosition;
        private Vector2 dragCurrentScreenPosition;
        private Vector2 rightPressScreenPosition; // where a pan began, to tell a cancel from a camera drag

        public static PlayerCommander Instance { get; private set; } // the tank strip routes slot clicks here

        public IReadOnlyList<TankController> Selection
        {
            get { return selection; }
        }

        // the strip's toggle: mobile has no alt key, so while on, ground clicks issue attack-moves
        public bool AutoFireEnabled { get; set; }

        void Awake()
        {
            Instance = this;
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

            // commands are Near-only. escape stays above: deselecting while zoomed out is fine
            if (CameraController.Lod != LodTier.Near)
            {
                dragging = false; // a box in progress dies here rather than firing on the far side of a zoom
                placingHq = false;
                return;
            }

            Mouse mouse = Mouse.current;
            if (mouse == null)
                return;
            Vector2 mousePosition = mouse.position.ReadValue();

            // right click cancels the placement, right drag pans the camera. both are the same button, so this
            // waits for the release and only cancels if the cursor never moved past the drag threshold.
            if (mouse.rightButton.wasPressedThisFrame)
                rightPressScreenPosition = mousePosition;
            else if (
                mouse.rightButton.wasReleasedThisFrame
                && !IsDragPastThreshold(rightPressScreenPosition, mousePosition)
            )
                placingHq = false;

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
            if (IsDragPastThreshold(dragStartScreenPosition, mousePosition))
                SelectInsideBox(dragStartScreenPosition, mousePosition);
            else
                HandleClick(mousePosition);
        }

        void LateUpdate()
        {
            UpdateHqMovePreview();
        }

        // hover shows the cost, the click pays it. which tiles are blocked is HqMovePreview's job,
        // so this label only ever shows the cost and whether you can afford it.
        void UpdateHqMovePreview()
        {
            HudCursorLabel.Hide();
            HqController hq = PlacingHq;
            if (hq == null || !TryGetPlacementTile(out Vector2Int tile))
                return;
            double now = NetworkManager.Singleton.ServerTime.Time;
            if (!hq.IsParked(now))
            {
                placingHq = false; // a knockback can send the base moving while you are placing it
                return;
            }
            Vector2 mousePosition = Mouse.current.position.ReadValue();
            if (!hq.IsValidDestination(tile))
            {
                HudCursorLabel.Show("blocked", false, mousePosition);
                return;
            }
            float cost = HqController.MoveCost(hq.HomeTile, tile);
            bool affordable = hq.Gold(now) >= cost;
            HudCursorLabel.Show($"move: {cost:0} gold", affordable, mousePosition);
        }

        // the tile a placement click would land on: the ground under the cursor, snapped onto the
        // capital's dock. shared by the label, the footprint preview and the click, so they cannot disagree.
        public bool TryGetPlacementTile(out Vector2Int tile)
        {
            tile = default;
            if (Mouse.current == null)
                return false;
            Ray ray = mainCamera.ScreenPointToRay(Mouse.current.position.ReadValue());
            if (!groundPlane.Raycast(ray, out float groundDistance))
                return false;
            if (!TileGrid.Instance.WorldToTile(ray.GetPoint(groundDistance), out tile))
                return false;
            tile = CapitalController.SnapToDock(tile);
            return true;
        }

        // only count as a drag once past threshold; both buttons ask it, one to tell a box from a
        // click, the other to tell a pan from a cancel
        static bool IsDragPastThreshold(Vector2 pressPosition, Vector2 currentPosition)
        {
            return (currentPosition - pressPosition).magnitude > DragThresholdPixels;
        }

        void HandleClick(Vector2 screenPosition)
        {
            Ray ray = mainCamera.ScreenPointToRay(screenPosition);
            ClearInspection(); // any click drops the last one; the enemy branches below set the new one

            if (PlacingHq != null)
            {
                PlaceHq();
                return; // dropping a base onto an enemy tank must not also order an attack on it
            }

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
                    Inspect(clickedTank, null);
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
                        selectedHq.SetSelected(true);
                    }
                }
                else
                {
                    Inspect(null, clickedHq);
                    foreach (TankController tank in selection)
                        tank.Attack(clickedHq); // siege: same standing order as attacking a tank
                }
                return;
            }

            groundPlane.Raycast(ray, out float groundDistance); // return grounddistance
            if (!TileGrid.Instance.WorldToTile(ray.GetPoint(groundDistance), out Vector2Int goal))
                return; // clicked outside the grid
            // trees carry no collider, so they are not something under the cursor: the ground tile is the tree.
            // checked before the move order, or clicking a tree would path into a blocked tile.
            if (selectedHq == null && selection.Count > 0 && TileGrid.Instance.HasTree(goal))
            {
                foreach (TankController tank in selection)
                    tank.Attack(goal);
                return;
            }
            if (selectedHq != null)
            {
                DeselectAll(); // clicking away deselects a building, same as RA2; only [Move] relocates it
                return;
            }
            // alt+click ground: attack-move. alt+click on a tank or HQ already fell through to the plain attack above.
            MoveSelectionTo(goal, AutoFireEnabled || (keyboard != null && keyboard.altKey.isPressed));
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

        // the [Move] button routes here; the next ground click is the placement
        public void BeginPlacingHq()
        {
            if (selectedHq != null)
                placingHq = true;
        }

        public HqController SelectedHq
        {
            get { return selectedHq; }
        }

        // non-null while a placement is waiting for its click; the footprint preview draws from this
        public HqController PlacingHq
        {
            get { return placingHq ? selectedHq : null; }
        }

        // an illegal or unaffordable spot is ignored rather than cancelling, so a misclick does not
        // cost the button press; right-click or escape is the cancel
        void PlaceHq()
        {
            if (!TryGetPlacementTile(out Vector2Int tile))
                return;
            if (!selectedHq.CanMoveTo(tile, NetworkManager.Singleton.ServerTime.Time))
                return; // the same gates the server will apply, asked early so the click is not silently dropped
            selectedHq.RequestMove(tile);
            placingHq = false;
        }

        void DeselectAll()
        {
            foreach (TankController tank in selection)
                tank.SetSelected(false);
            selection.Clear();
            if (selectedHq != null)
                selectedHq.SetSelected(false);
            selectedHq = null;
            placingHq = false; // nothing to place once the HQ is deselected
            ClearInspection(); // escape reaches here without passing through HandleClick
        }

        void Inspect(TankController tank, HqController hq)
        {
            ClearInspection();
            inspectedTank = tank;
            inspectedHq = hq;
            if (inspectedTank != null)
                inspectedTank.SetInspected(true);
            if (inspectedHq != null)
                inspectedHq.SetInspected(true);
        }

        // the Unity null check covers a target that despawned while inspected; its flag went with it
        void ClearInspection()
        {
            if (inspectedTank != null)
                inspectedTank.SetInspected(false);
            if (inspectedHq != null)
                inspectedHq.SetInspected(false);
            inspectedTank = null;
            inspectedHq = null;
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
            if (!dragging || !IsDragPastThreshold(dragStartScreenPosition, dragCurrentScreenPosition))
                return;
            // GUI space runs y down from the top, the mouse runs y up from the bottom
            Vector2 start = new Vector2(dragStartScreenPosition.x, Screen.height - dragStartScreenPosition.y);
            Vector2 current = new Vector2(dragCurrentScreenPosition.x, Screen.height - dragCurrentScreenPosition.y);
            Rect box = ScreenRect(start, current);

            Color previousColor = GUI.color; // shared with every other OnGUI this frame
            GUI.color = dragBoxColor;
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
