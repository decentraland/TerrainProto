using UnityEngine;

namespace Decentraland.Terrain
{
    public abstract class TerrainNoiseFunction : ScriptableObject
    {
        public abstract float HeightMap(float x, float z);
        public abstract float RandomRange(float x, float z, float min, float max);
    }
}
