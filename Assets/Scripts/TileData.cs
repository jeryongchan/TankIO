namespace TankIO
{
    /// Pure data for one grid cell. Deliberately NOT a MonoBehaviour or a class so its cheap to walk over
    public struct TileData 
    {
        public bool Walkable;

        public bool HasTree;
    }
}
