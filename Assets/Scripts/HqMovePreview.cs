using System.Collections.Generic;
using UnityEngine;

namespace TankIO
{
    // the 3x3 footprint under the cursor after pressing [Move], like placing a building in a base builder.
    // one quad per tile, green or red by IsFootprintTileFree - the same check ExecuteMove runs on the server,
    // so the preview cannot promise a spot the confirm would refuse.
    // AllowsPlacementAt is about the placement as a whole (overlapping the capital, or already being
    // there), and no single tile fails it, so all 9 go red together.
    public class HqMovePreview : MonoBehaviour
    {
        [SerializeField]
        private MeshRenderer tilePrefab; // a flat quad; its authored y is how far above the ground it sits

        [SerializeField]
        private Material validMaterial;

        // swapped as sharedMaterial, not tinted through a property block: a block would break SRP batching
        [SerializeField]
        private Material blockedMaterial;

        private MeshRenderer[] tiles;
        private readonly List<Vector2Int> footprintBuffer = new List<Vector2Int>();

        void Awake()
        {
            int side = HqController.FootprintRadius * 2 + 1;
            tiles = new MeshRenderer[side * side];
            for (int index = 0; index < tiles.Length; index++)
            {
                tiles[index] = Instantiate(tilePrefab, transform);
                tiles[index].gameObject.SetActive(false);
            }
        }

        void LateUpdate()
        {
            HqController hq = PlayerCommander.Instance.PlacingHq;
            if (hq == null || !PlayerCommander.Instance.TryGetPlacementTile(out Vector2Int centerTile))
            {
                Hide();
                return;
            }
            HqController.FootprintTiles(centerTile, footprintBuffer);
            bool placementAllowed = hq.AllowsPlacementAt(centerTile);
            for (int index = 0; index < footprintBuffer.Count; index++)
            {
                Vector2Int tile = footprintBuffer[index];
                Vector3 position = TileGrid.Instance.TileToWorldCenter(tile);
                position.y = tilePrefab.transform.position.y;
                tiles[index].gameObject.SetActive(true);
                tiles[index].transform.position = position;
                bool free = placementAllowed && HqController.IsFootprintTileFree(tile, hq.NetworkObjectId);
                tiles[index].sharedMaterial = free ? validMaterial : blockedMaterial;
            }
        }

        void Hide()
        {
            foreach (MeshRenderer tile in tiles)
                tile.gameObject.SetActive(false);
        }
    }
}
