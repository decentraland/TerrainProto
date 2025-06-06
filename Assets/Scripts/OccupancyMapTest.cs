using UnityEngine;

namespace Decentraland.Terrain
{
    public sealed class OccupancyMapTest : MonoBehaviour
    {
        [SerializeField] private Renderer renderer;

        private void Start()
        {
            TerrainData.LoadOccupancyMap(out Texture2D texture, out RectInt parcelRect);
            transform.localScale = new Vector3(parcelRect.width, parcelRect.height, 1f);
            Material material = renderer.material;
            material.mainTexture = texture;
        }
    }
}
