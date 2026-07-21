using System.Collections.Generic;
using UnityEngine;

namespace TankIO
{
    // space-time reservation table:  which tank is on which tile, and between what times.
    // a trip is a fixed route at a fixed speed, so the times are worked out exactly when it is written, never updated as the tank drives.
    // the server fills it in, clients rebuild it from replicated trips so local prediction sees the same traffic.
    // Pathfinder prices an occupied tile rather than banning it, so routes bend only where bending is cheap.
    public static class TripReservations
    {
        private struct Reservation
        {
            public ulong TankId;
            public double EnterTime;
            public double LeaveTime;
        }

        private static readonly Dictionary<Vector2Int, List<Reservation>> reservationsByTile =
            new Dictionary<Vector2Int, List<Reservation>>();
        private static readonly Dictionary<ulong, List<Vector2Int>> tilesByTank =
            new Dictionary<ulong, List<Vector2Int>>();

        // statics outlive a play session when domain reload is off
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetSessionState()
        {
            reservationsByTile.Clear();
            tilesByTank.Clear();
        }

        // replaces the tank's reservations with the given trip's occupancy. tile windows meet at the midpoint
        // in time between centre arrivals, so a diagonal step leaves no unreserved seam and the tile the tank
        // sets off from is covered. the last tile is held open-ended: the tank parks there until its next write.
        // tanksToRepath (when given) comes back holding tanks that cross the park tile after this tank settles.
        public static void Write(
            ulong tankId,
            Vector3 startPosition,
            Vector2Int[] path,
            double startTime,
            float moveSpeed,
            List<ulong> tanksToRepath = null
        )
        {
            Release(tankId);
            if (tanksToRepath != null)
                tanksToRepath.Clear(); // cleared here so the return below cannot leave the caller a stale list

            if (!TileGrid.Instance.WorldToTile(startPosition, out Vector2Int previousTile))
                return;
            double previousEnterTime = startTime;
            double previousArrivalTime = startTime;
            Vector3 previousPoint = startPosition;
            float distanceDriven = 0f;

            foreach (Vector2Int tile in path)
            {
                Vector3 centre = TileGrid.Instance.TileToWorldCenter(tile);
                centre.y = startPosition.y; // trips drive at the tank's height, mirror TripFromState
                distanceDriven += (centre - previousPoint).magnitude;
                previousPoint = centre;
                double arrivalTime = startTime + distanceDriven / moveSpeed;
                double boundaryTime = (previousArrivalTime + arrivalTime) * 0.5;
                Add(tankId, previousTile, previousEnterTime, boundaryTime);
                previousTile = tile;
                previousEnterTime = boundaryTime;
                previousArrivalTime = arrivalTime;
            }
            Add(tankId, previousTile, previousEnterTime, double.MaxValue);

            if (tanksToRepath != null)
                FindTanksCrossingParkTile(previousTile, previousEnterTime, tankId, tanksToRepath);
        }

        // tanks still driving over the tile after the park begins. open-ended windows are other parkers;
        // repathing one to its own goal would change nothing, so they are skipped.
        private static void FindTanksCrossingParkTile(
            Vector2Int tile,
            double parkEnterTime,
            ulong parkedTankId,
            List<ulong> results
        )
        {
            if (!reservationsByTile.TryGetValue(tile, out List<Reservation> reservations))
                return;
            foreach (Reservation reservation in reservations)
            {
                if (
                    reservation.TankId != parkedTankId
                    && reservation.LeaveTime != double.MaxValue
                    && reservation.LeaveTime > parkEnterTime
                )
                    results.Add(reservation.TankId);
            }
        }

        // parked claims for a building footprint, all open-ended. no release here: the HQ holds its origin
        // and its destination under two identities during a move, so what to release and when is the caller's
        // decision, not this table's.
        public static void AddParkedTiles(ulong id, List<Vector2Int> tiles, double enterTime)
        {
            foreach (Vector2Int tile in tiles)
                Add(id, tile, enterTime, double.MaxValue);
        }

        // tanks whose finite windows on any of these tiles extend past fromTime
        // trips written before the claim existed, which would drive through it. 
        // open-ended holders are parkers, not crossers: skipped.
        public static void FindTanksCrossingTiles(List<Vector2Int> tiles, double fromTime, List<ulong> results)
        {
            results.Clear();
            foreach (Vector2Int tile in tiles)
            {
                if (!reservationsByTile.TryGetValue(tile, out List<Reservation> reservations))
                    continue;
                foreach (Reservation reservation in reservations)
                {
                    if (
                        reservation.LeaveTime != double.MaxValue
                        && reservation.LeaveTime > fromTime
                        && !results.Contains(reservation.TankId)
                    )
                        results.Add(reservation.TankId);
                }
            }
        }

        public static void Release(ulong tankId)
        {
            if (!tilesByTank.TryGetValue(tankId, out List<Vector2Int> tiles))
                return;
            foreach (Vector2Int tile in tiles)
            {
                List<Reservation> reservations = reservationsByTile[tile];
                for (int index = reservations.Count - 1; index >= 0; index--)
                {
                    if (reservations[index].TankId == tankId)
                        reservations.RemoveAt(index);
                }
            }
            tiles.Clear();
        }

        public enum Occupation
        {
            None,
            Transit, // a moving tank crosses the tile during the window
            Parked // a tank parks on the tile during the window; certain, since trips are closed form
        }

        // how the tile is occupied during [enterTime, leaveTime], ignoring the given tank.
        // a parked overlap outranks transit crossings.
        public static Occupation OccupationDuring(
            Vector2Int tile,
            double enterTime,
            double leaveTime,
            ulong ignoreTankId
        )
        {
            Occupation occupation = Occupation.None;
            if (!reservationsByTile.TryGetValue(tile, out List<Reservation> reservations))
                return occupation;
            foreach (Reservation reservation in reservations)
            {
                if (
                    reservation.TankId == ignoreTankId
                    || reservation.EnterTime >= leaveTime
                    || enterTime >= reservation.LeaveTime
                )
                    continue;
                if (reservation.LeaveTime == double.MaxValue)
                    return Occupation.Parked;
                occupation = Occupation.Transit;
            }
            return occupation;
        }

        // Return walkable tiles nobody has claimed to park on, closest to the centre first.
        // May return fewer tiles if the map runs out.
        // a park claim ignores timing, so a tile someone will park on is taken from now, not from when they get there.
        public static void FindUnclaimedParkTilesNear(
            Vector2Int center,
            int count,
            List<ulong> ignoredTankIds, // means currently selected tanks; treat them as non existent during this move
            List<Vector2Int> results
        )
        {
            results.Clear();
            int maxRadius = Mathf.Max(TileGrid.Instance.Width, TileGrid.Instance.Height);
            for (int radius = 0; results.Count < count && radius <= maxRadius; radius++)
            {
                BuildRingNearestFirst(center, radius);
                foreach (Vector2Int tile in ringBuffer)
                {
                    if (results.Count < count && IsUnclaimedParkTile(tile, ignoredTankIds, 0))
                        results.Add(tile);
                }
            }
        }

        // the nearest unclaimed park tile within maxRingRadius.
        // when all are claimed, returns false and returns back the center unchanged.
        // ONLY USED FOR FIRINGTILE. When a tank tries to position itself for firing, sometimes there are other ally tanks blocking it from reach, and when this happen we dont want it to route too far
        public static bool TryNearestUnclaimedParkTile(
            Vector2Int center,
            int maxRingRadius,
            ulong searchingTankId,
            out Vector2Int result
        )
        {
            for (int radius = 0; radius <= maxRingRadius; radius++)
            {
                BuildRingNearestFirst(center, radius);
                foreach (Vector2Int tile in ringBuffer)
                {
                    if (IsUnclaimedParkTile(tile, null, searchingTankId))
                    {
                        result = tile;
                        return true;
                    }
                }
            }
            result = center;
            return false;
        }

        private static readonly List<Vector2Int> ringBuffer = new List<Vector2Int>();

        // the square ring of tiles this many steps from the center (chebyshev radius).
        private static void BuildRingNearestFirst(Vector2Int center, int radius)
        {
            ringBuffer.Clear();
            if (radius == 0)
            {
                ringBuffer.Add(center);
                return;
            }
            for (int x = -radius; x <= radius; x++)
            {
                ringBuffer.Add(center + new Vector2Int(x, -radius));
                ringBuffer.Add(center + new Vector2Int(x, radius));
            }
            for (int y = -radius + 1; y <= radius - 1; y++)
            {
                ringBuffer.Add(center + new Vector2Int(-radius, y));
                ringBuffer.Add(center + new Vector2Int(radius, y));
            }
            ringBuffer.Sort(
                (tileA, tileB) =>
                {
                    int distanceA = (tileA - center).sqrMagnitude;
                    int distanceB = (tileB - center).sqrMagnitude;
                    if (distanceA != distanceB)
                        return distanceA.CompareTo(distanceB);
                    if (tileA.x != tileB.x)
                        return tileA.x.CompareTo(tileB.x);
                    return tileA.y.CompareTo(tileB.y);
                }
            );
        }

        private static bool IsUnclaimedParkTile(Vector2Int tile, List<ulong> ignoredTankIds, ulong ignoredTankId)
        {
            if (!TileGrid.Instance.IsWalkable(tile))
                return false;
            if (CapitalController.CoversTile(tile))
                return false; // solid building: every tank destination search skips it (park, deploy, recall, firing ring)
            ulong parkedTank = ParkedTankAt(tile);
            if (parkedTank == 0 || parkedTank == ignoredTankId)
                return true;
            return ignoredTankIds != null && ignoredTankIds.Contains(parkedTank);
        }

        // the tank holding an open-ended window on this tile, 0 when none. that tank parks there
        // now, or will once its trip ends.
        public static ulong ParkedTankAt(Vector2Int tile)
        {
            if (!reservationsByTile.TryGetValue(tile, out List<Reservation> reservations))
                return 0;
            foreach (Reservation reservation in reservations)
            {
                if (reservation.LeaveTime == double.MaxValue)
                    return reservation.TankId;
            }
            return 0;
        }

        private static void Add(ulong tankId, Vector2Int tile, double enterTime, double leaveTime)
        {
            if (!reservationsByTile.TryGetValue(tile, out List<Reservation> reservations))
            {
                reservations = new List<Reservation>();
                reservationsByTile[tile] = reservations;
            }
            reservations.Add(
                new Reservation
                {
                    TankId = tankId,
                    EnterTime = enterTime,
                    LeaveTime = leaveTime
                }
            );

            if (!tilesByTank.TryGetValue(tankId, out List<Vector2Int> tiles))
            {
                tiles = new List<Vector2Int>();
                tilesByTank[tankId] = tiles;
            }
            tiles.Add(tile);
        }
    }
}
