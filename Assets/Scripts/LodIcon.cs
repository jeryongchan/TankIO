using UnityEngine;

namespace TankIO
{
    // every icon shares one material; the property block tints each renderer without cloning it,
    // so the icons still batch.
    public static class LodIcon
    {
        private static readonly MaterialPropertyBlock block = new MaterialPropertyBlock();
        private static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
        private static readonly int ColorId = Shader.PropertyToID("_Color");

        public static void Tint(Renderer iconRenderer, Color color)
        {
            iconRenderer.GetPropertyBlock(block);
            block.SetColor(BaseColorId, color);
            block.SetColor(ColorId, color);
            iconRenderer.SetPropertyBlock(block);
        }
    }
}
