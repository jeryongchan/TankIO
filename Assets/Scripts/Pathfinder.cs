using System.Collections.Generic;
using UnityEngine;

namespace TankIO
{
    public class Pathfinder
    {
        private const int StraightCost = 10;
        private const int DiagonalCost = 14; // ~sqrt(2) * StraightCost, for determinism
        private const int ReservationPenalty = 15; // detour budget for crossing a moving tank's path: 1.5 straight tiles buys a sidestep. parked tanks are walls, not costs
        private const bool BlockCornerCutPastTanks = false; // false lets diagonals thread the zero-width seam between diagonally-adjacent occupied tiles; hulls are wider, so it reads as clipping

        private static readonly Vector2Int[] neighborOffsets =
        {
            new Vector2Int(1, 0),
            new Vector2Int(-1, 0),
            new Vector2Int(0, 1),
            new Vector2Int(0, -1),
            new Vector2Int(1, 1),
            new Vector2Int(1, -1),
            new Vector2Int(-1, 1),
            new Vector2Int(-1, -1),
        };

        private readonly TileGrid tileGrid;

        // all indexed by col + row * width (flattened).
        // openSearchId/closedSearchId hold the id of the search that last put a tile in that set, so bumping searchId empties both in O(1)
        // instead of clearing them: a tile is only in a set if its id matches the current search.
        private int[] gCost; // cost of the best known path from the start to this tile
        private int[] gDistance; // same path's cost without penalties; kept apart so penalties don't distort arrival times
        private int[] parent; // the tile we reached this one from, for retracing the path
        private int[] openSearchId;
        private int[] closedSearchId;
        private MinHeap open;
        private int searchId;
        private int width;
        private int height;

        // set per search by FindPath
        private ulong pathingTankId; // a tank's own reservations never penalize it
        private double tripStartTime;
        private float tankMoveSpeed;

        public Pathfinder(TileGrid tileGrid)
        {
            this.tileGrid = tileGrid;
            width = tileGrid.Width;
            height = tileGrid.Height;
            int tileCount = width * height;
            gCost = new int[tileCount];
            gDistance = new int[tileCount];
            parent = new int[tileCount];
            openSearchId = new int[tileCount];
            closedSearchId = new int[tileCount];
            open = new MinHeap(tileCount);
            searchId = 0; // arrays default to 0, so 0 means "not visited"; the first search increments to 1.
        }

        // fills path with tiles from start to goal inclusive. false if unreachable.
        // entering a tile inside another tank's reserved time window costs extra (see TripReservations),
        // so the route pays for a short sidestep around predicted traffic but keeps the straight line
        // when every detour is long. soft cost: nothing becomes unreachable.
        public bool FindPath(
            Vector2Int start,
            Vector2Int goal,
            List<Vector2Int> path,
            ulong tankId,
            double startTime,
            float moveSpeed
        )
        {
            pathingTankId = tankId;
            tripStartTime = startTime;
            tankMoveSpeed = moveSpeed;
            path.Clear();

            if (!tileGrid.IsWalkable(start) || !tileGrid.IsWalkable(goal))
                return false;

            searchId++;
            open.Clear();

            int startIndex = ToIndex(start);
            int goalIndex = ToIndex(goal);
            gCost[startIndex] = 0;
            gDistance[startIndex] = 0;
            parent[startIndex] = -1;
            openSearchId[startIndex] = searchId;
            open.Push(startIndex, HeuristicCost(start, goal));

            while (open.Count > 0)
            {
                int current = open.Pop(); // lowest f cost
                if (closedSearchId[current] == searchId) // stale duplicate, already evaluated
                    continue;
                closedSearchId[current] = searchId;

                if (current == goalIndex)
                {
                    RetracePath(goalIndex, path);
                    return true;
                }

                Vector2Int currentTile = ToTile(current);
                foreach (Vector2Int offset in neighborOffsets)
                {
                    Vector2Int neighbor = currentTile + offset;
                    if (!tileGrid.IsWalkable(neighbor))
                        continue;
                    if (CapitalController.CoversTile(neighbor))
                        continue; // solid building to tanks; walkable in the grid only so an HQ can dock

                    bool isDiagonal = offset.x != 0 && offset.y != 0;
                    // if (isDiagonal && !CanCutCorner(currentTile, offset))
                    //     continue;

                    int neighborIndex = ToIndex(neighbor);
                    if (closedSearchId[neighborIndex] == searchId)
                        continue;

                    int stepCost = isDiagonal ? DiagonalCost : StraightCost;
                    int newGDistance = gDistance[current] + stepCost;

                    // a tile someone parks on at arrival time is a wall; a moving tank crossing it is a cost.
                    // a diagonal step also checks its two side tiles: it passes between them without entering
                    // either, and hulls are wider than the zero-width corner gap.
                    TripReservations.Occupation entered = OccupationAtArrival(neighbor, newGDistance);
                    if (entered == TripReservations.Occupation.Parked)
                        continue;
                    int penalty = entered == TripReservations.Occupation.Transit ? ReservationPenalty : 0;
                    if (isDiagonal && BlockCornerCutPastTanks)
                    {
                        TripReservations.Occupation sideA = OccupationAtArrival(new Vector2Int(neighbor.x, currentTile.y), newGDistance);
                        TripReservations.Occupation sideB = OccupationAtArrival(new Vector2Int(currentTile.x, neighbor.y), newGDistance);
                        if (sideA == TripReservations.Occupation.Parked || sideB == TripReservations.Occupation.Parked)
                            continue; // cutting a corner through a parked hull
                        if (sideA == TripReservations.Occupation.Transit || sideB == TripReservations.Occupation.Transit)
                            penalty += ReservationPenalty;
                    }

                    int newGCost = gCost[current] + stepCost + penalty;
                    bool isInOpen = openSearchId[neighborIndex] == searchId;
                    if (isInOpen && newGCost >= gCost[neighborIndex]) // already reached it cheaper
                        continue;

                    gCost[neighborIndex] = newGCost;
                    gDistance[neighborIndex] = newGDistance;
                    parent[neighborIndex] = current;
                    openSearchId[neighborIndex] = searchId;
                    open.Push(neighborIndex, newGCost + HeuristicCost(neighbor, goal)); // f = g + h
                }
            }

            return false;
        }

        // stops tank from slipping diagonally through the gap between two walls.
        // private bool CanCutCorner(Vector2Int from, Vector2Int offset)
        // {
        //     return tileGrid.IsWalkable(from + new Vector2Int(offset.x, 0))
        //         && tileGrid.IsWalkable(from + new Vector2Int(0, offset.y));
        // }

        // how the tile is occupied when the searching tank would arrive, after this much driving.
        // arrival time comes from pure distance at the tank's speed.
        private TripReservations.Occupation OccupationAtArrival(Vector2Int tile, int distanceCost)
        {
            double arrivalTime = tripStartTime + (double)distanceCost / StraightCost * tileGrid.TileSize / tankMoveSpeed;
            double halfTileSeconds = 0.5 * tileGrid.TileSize / tankMoveSpeed;
            return TripReservations.OccupationDuring(tile, arrivalTime - halfTileSeconds, arrivalTime + halfTileSeconds, pathingTankId);
        }

        // h cost. octile distance, means can only go in 8-dir (unobstructed path).
        private static int HeuristicCost(Vector2Int from, Vector2Int to)
        {
            int dx = Mathf.Abs(from.x - to.x);
            int dy = Mathf.Abs(from.y - to.y);
            int diagonalSteps = Mathf.Min(dx, dy);
            int straightSteps = Mathf.Abs(dx - dy);
            return DiagonalCost * diagonalSteps + StraightCost * straightSteps;
        }

        // walk parents back from the goal, then flip, so the path reads start to goal.
        private void RetracePath(int goalIndex, List<Vector2Int> path)
        {
            for (int current = goalIndex; current != -1; current = parent[current])
                path.Add(ToTile(current));
            path.Reverse();
        }

        private int ToIndex(Vector2Int tile)
        {
            return tile.x + tile.y * width;
        }

        private Vector2Int ToTile(int index)
        {
            return new Vector2Int(index % width, index / width);
        }

        // minheap. implement ourselves for netcode determinism (and also cuz unity doesnt have built in.) (see more in below notes)
        private class MinHeap
        {
            private int[] tileIndices;
            private int[] priorities;
            private int count;

            public MinHeap(int capacity)
            {
                tileIndices = new int[capacity];
                priorities = new int[capacity];
            }

            public int Count
            {
                get { return count; }
            }

            public void Clear()
            {
                count = 0;
            }

            public void Push(int tileIndex, int priority)
            {
                if (count == tileIndices.Length) // duplicate pushes can exceed tile count
                {
                    System.Array.Resize(ref tileIndices, count * 2);
                    System.Array.Resize(ref priorities, count * 2);
                }
                int child = count;
                tileIndices[child] = tileIndex;
                priorities[child] = priority;
                count++;

                while (child > 0)
                {
                    int parent = (child - 1) / 2;
                    if (!IsBefore(child, parent))
                        break;
                    Swap(child, parent);
                    child = parent;
                }
            }

            public int Pop()
            {
                int result = tileIndices[0];
                count--;
                tileIndices[0] = tileIndices[count];
                priorities[0] = priorities[count];

                int parent = 0;
                while (true)
                {
                    int left = parent * 2 + 1;
                    int right = left + 1;
                    int smallest = parent;
                    if (left < count && IsBefore(left, smallest))
                        smallest = left;
                    if (right < count && IsBefore(right, smallest))
                        smallest = right;
                    if (smallest == parent)
                        break;
                    Swap(parent, smallest);
                    parent = smallest;
                }

                return result;
            }

            // ties break on tile index so the pop order never depends on insertion order
            private bool IsBefore(int a, int b)
            {
                if (priorities[a] != priorities[b])
                    return priorities[a] < priorities[b];
                return tileIndices[a] < tileIndices[b];
            }

            private void Swap(int a, int b)
            {
                int tile = tileIndices[a];
                tileIndices[a] = tileIndices[b];
                tileIndices[b] = tile;

                int priority = priorities[a];
                priorities[a] = priorities[b];
                priorities[b] = priority;
            }
        }
    }
}

// PseudoCode. Revise with https://www.youtube.com/watch?v=-L-WgKMFuhE&t=1s

// searchId++;
// open.Push(startIndex, HeuristicCost(start, goal));

// while (open.Count > 0)
// {
//     int current = open.Pop();                                  // lowest f_cost

//     if (closedSearchId[current] == searchId)                   // already evaluated this search in THIS LOOP
//         continue;                                              // -> stale duplicate, skip it
//     closedSearchId[current] = searchId;                        // add current to CLOSED

//     if (current == goalIndex)
//     {
//         RetracePath(goalIndex, path);
//         return true;
//     }

//     foreach (Vector2Int offset in neighborOffsets)             // 8 neighbours
//     {
//         Vector2Int neighbor = currentTile + offset;

//         if (!tileGrid.IsWalkable(neighbor))
//             continue;
//         if (closedSearchId[neighborIndex] == searchId)          // in CLOSED -> skip
//             continue;

//         int newGCost = gCost[current] + stepCost;
//         bool isInOpen = openSearchId[neighborIndex] == searchId;
//         if (isInOpen && newGCost >= gCost[neighborIndex])       // already reached it cheaper
//             continue;

//         gCost[neighborIndex] = newGCost;
//         parent[neighborIndex] = current;
//         openSearchId[neighborIndex] = searchId;                 // add neighbour to OPEN
//         open.Push(neighborIndex, newGCost + HeuristicCost(neighbor, goal));   // f = g + h
//     }
// }

// return false;                                                   // open ran dry -> unreachable


// Notes
// 1. OPEN does two jobs, CLOSED does one
// CLOSED is only asked "is X in you?" -> one array.

// OPEN is asked two things:

// lowest f? -> ordering -> MinHeap
// is X in you? -> membership -> openSearchId[] (a heap answers this in O(n), too slow)
// No one structure does both, so OPEN is split in two. Always updated together: open.Push(i, f) + openSearchId[i] = searchId.

// That's why 4 arrays vs the video's 2 sets. Same sets, different representation.

// Stamps, not bools: in a set iff stamp == searchId. So searchId++ empties both in O(1), no clearing loop.

// 2. Private nested class
// Two walls:

// private class MinHeap -> only Pathfinder can even name the type.
// public Pop() -> "anyone who can see MinHeap may call this", and only Pathfinder can. So it means "Pathfinder may call this." IsBefore/Swap stay private = the heap's own guts.
// Rule: a member's accessibility is capped by its container's. public inside a private class isn't publicly reachable; it just marks the seam within that private world.

// (All-private wouldn't compile: a nested class is still a separate class, so FindPath couldn't call open.Push.)
