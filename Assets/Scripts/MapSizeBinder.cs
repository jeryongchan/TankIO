using UnityEngine;

namespace TankIO
{
    [ExecuteAlways]
    public class MapSizeBinder : MonoBehaviour
    {
        [SerializeField]
        private TileGrid tileGrid;

        [SerializeField]
        private Transform mist; // scaled to the map every frame: its quad has no other tie to the grid

        private static readonly int MapCenterId = Shader.PropertyToID("_MapCenter");
        private static readonly int MapRadiusId = Shader.PropertyToID("_MapRadius");

        void Update()
        {
            if (tileGrid == null)
                return;
            Transform grid = tileGrid.transform;
            // the grid's local origin is its centre: GridCornersBeforeTransform spans -half to +half
            Shader.SetGlobalVector(MapCenterId, new Vector4(grid.position.x, grid.position.z, 0f, 0f));
            float worldRadius = tileGrid.Radius * tileGrid.TileSize * grid.lossyScale.x;
            Shader.SetGlobalFloat(MapRadiusId, worldRadius);
            if (mist != null)
            {
                // x/y, not x/z: the quad is rotated flat, so its local X/Y span world X/Z
                float diameter = 2f * worldRadius;
                mist.localScale = new Vector3(diameter, diameter, 1f);
            }
        }
    }
}
