using UnityEngine;
using Random = Unity.Mathematics.Random; // aliased, or it collides with UnityEngine.Random

namespace TankIO
{
    // grass/dirt split, derived from noise on demand rather than stored per tile: same argument
    // as IsGround, one source of truth that the splat texture and tree planting both read.
    public partial class TileGrid
    {
        [Header("Ground splat")]
        [SerializeField]
        private int groundSeed = 1;

        [SerializeField, Min(0.001f)]
        private float patchScale = 0.08f; // a patch spans roughly 1/patchScale tiles

        [SerializeField, Range(0f, 1f)]
        private float grassCoverage = 0.5f; // fraction of ground tiles that come out grass

        private float noiseOffsetX;
        private float noiseOffsetY;
        private float noiseMin;
        private float noiseMax;
        private float noiseCut;

        // 0 = dirt, 1 = grass, crossing 0.5 exactly where grassCoverage says it should.
        // callers compare against 0.5 and never need to know the underlying noise threshold.
        public float GrassWeight(Vector2Int tile)
        {
            return GrassWeight(tile.x, tile.y);
        }

        // coordinates in tile units, fractions allowed: the noise is continuous, so a caller
        // can sample between tile centres (grass chunks read the weight at their own position)
        public float GrassWeight(float x, float y)
        {
            if (tiles == null)
                BuildTiles(); // ExecuteAlways: GroundRenderer's OnEnable can beat Awake after a domain reload

            float noise = GrassNoise(x, y);
            if (noise < noiseCut)
                return Mathf.InverseLerp(noiseMin, noiseCut, noise) * 0.5f;
            return 0.5f + Mathf.InverseLerp(noiseCut, noiseMax, noise) * 0.5f;
        }

        float GrassNoise(float tileX, float tileY)
        {
            float x = tileX * patchScale;
            float y = tileY * patchScale;
            // second octave at double frequency, half strength: ragged edges and islands instead of smooth blobs.
            float noise =
                Mathf.PerlinNoise(x + noiseOffsetX, y + noiseOffsetY)
                + 0.5f * Mathf.PerlinNoise(x * 2f + noiseOffsetX, y * 2f + noiseOffsetY);
            return noise / 1.5f;
        }

        // find the noise value for each tile in tilegrid; then sort it; then determine the cutoff based on the grassCoverage.
        // 0.21  0.28  0.33  0.40  0.44(cutoff)  0.51  0.58  0.62  0.70  0.79
        // grasscoverage=0.6, means cut off here at 40th percentile. 0.44 is also known as the noisecut
        void FindGrassCutoff()
        {
            var rng = Random.CreateFromIndex((uint)groundSeed);
            // fractional offsets: PerlinNoise repeats its value at integer coordinates, so
            // whole-number offsets would give every seed the same map.
            noiseOffsetX = rng.NextFloat(0f, 512f);
            noiseOffsetY = rng.NextFloat(0f, 512f);
            var samples = new float[width * height];
            int count = 0;
            for (int row = 0; row < height; row++)
            {
                for (int col = 0; col < width; col++)
                {
                    var tile = new Vector2Int(col, row);
                    if (IsGround(tile)) // tiles outside the disc are not ground, so they must not skew the share
                        samples[count++] = GrassNoise(col, row);
                }
            }
            System.Array.Sort(samples, 0, count);
            noiseMin = samples[0];
            noiseMax = samples[count - 1];
            int cutIndex = Mathf.Clamp(Mathf.RoundToInt((1f - grassCoverage) * (count - 1)), 0, count - 1);
            noiseCut = samples[cutIndex];
        }
    }
}
