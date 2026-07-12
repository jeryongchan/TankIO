using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

namespace TankIO
{
    // click-to-move tank, C&C style. left-click a tile, A* finds a route. movement is continuous but always stop at a tile
    public class TankController : MonoBehaviour
    {
        [SerializeField]
        private float moveSpeed = 5f;

        [SerializeField]
        private float turnSpeed = 720f; // degrees per second toward the move direction

        [SerializeField]
        private TileGrid tileGrid;

        private readonly Plane groundPlane = new Plane(Vector3.up, Vector3.zero);
        private readonly List<Vector2Int> path = new List<Vector2Int>();
        private Pathfinder pathfinder;
        private Camera mainCamera;
        private int nextWaypoint;

        void Start()
        {
            pathfinder = new Pathfinder(tileGrid);
            mainCamera = Camera.main;
        }

        void Update()
        {
            HandleMoveOrder();
            FollowPath();
        }

        void HandleMoveOrder()
        {
            var mouse = Mouse.current;
            if (mouse == null || !mouse.leftButton.wasPressedThisFrame)
                return;

            Ray ray = mainCamera.ScreenPointToRay(mouse.position.ReadValue());
            if (!groundPlane.Raycast(ray, out float distance))
                return;

            if (!tileGrid.WorldToTile(ray.GetPoint(distance), out Vector2Int goal))
                return; // clicked outside the grid
            if (!tileGrid.WorldToTile(transform.position, out Vector2Int start))
                return;

            bool found = pathfinder.FindPath(start, goal, path); // clears path either way
            nextWaypoint = found ? 1 : 0; // skip the tile the tank is already standing on
        }

        void FollowPath()
        {
            if (nextWaypoint >= path.Count)
                return;

            Vector3 waypoint = tileGrid.TileToWorldCenter(path[nextWaypoint]);
            waypoint.y = transform.position.y; // keep the tank's own height

            Vector3 toWaypoint = waypoint - transform.position;
            float stepDistance = moveSpeed * Time.deltaTime;
            if (toWaypoint.magnitude <= stepDistance)
            {
                transform.position = waypoint;
                nextWaypoint++;
                return;
            }

            Vector3 moveDirection = toWaypoint.normalized;
            transform.position += moveDirection * stepDistance;

            Quaternion targetRotation = Quaternion.LookRotation(moveDirection, Vector3.up);
            transform.rotation = Quaternion.RotateTowards(
                transform.rotation,
                targetRotation,
                turnSpeed * Time.deltaTime
            );
        }
    }
}
