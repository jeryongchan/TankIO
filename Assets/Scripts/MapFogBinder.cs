using UnityEngine;

namespace TankIO
{
    [ExecuteAlways]
    public class MapFogBinder : MonoBehaviour
    {
        [SerializeField]
        private TileGrid tileGrid;

        private static readonly int MapCenterId = Shader.PropertyToID("_MapCenter");
        private static readonly int MapRadiusId = Shader.PropertyToID("_MapRadius");

        void Update()
        {
            if (tileGrid == null)
                return;
            Transform grid = tileGrid.transform;
            // the grid's local origin is its centre: GridCornersBeforeTransform spans -half to +half
            Shader.SetGlobalVector(MapCenterId, new Vector4(grid.position.x, grid.position.z, 0f, 0f));
            Shader.SetGlobalFloat(MapRadiusId, tileGrid.Radius * tileGrid.TileSize * grid.lossyScale.x);
        }
    }
}
