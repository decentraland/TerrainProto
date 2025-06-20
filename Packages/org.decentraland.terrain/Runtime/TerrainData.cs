using System;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
using static Unity.Mathematics.math;
using Random = Unity.Mathematics.Random;

namespace Decentraland.Terrain
{
    using Noise = MountainsNoise;

    [CreateAssetMenu(menuName = "Decentraland/Terrain/Terrain Data")]
    public sealed class TerrainData : ScriptableObject
    {
        public int randomSeed = 1;
        public Material material;
        public int parcelSize = 16;
        public RectInt bounds;
        public float maxHeight;
        public Texture2D occupancyMap;
        public float treesPerParcel = 0.15f;
        public float detailDistance = 200;
        public bool renderGround = true;
        public bool renderTreesAndDetail = true;
        [SerializeField] internal int groundInstanceCapacity = 80;
        [SerializeField] internal int treeInstanceCapacity = 3000;
        [SerializeField] internal int detailInstanceCapacity = 40000;
        [SerializeField] private Mesh groundMeshMiddle;
        [SerializeField] private Mesh groundMeshEdge;
        [SerializeField] private Mesh groundMeshCorder;
        [SerializeField] internal TreePrototype[] treePrototypes;
        [SerializeField] internal DetailPrototype[] detailPrototypes;

        internal TerrainDataData GetData()
        {
            return new TerrainDataData(parcelSize, bounds, maxHeight, occupancyMap, randomSeed,
                treesPerParcel, treePrototypes.Length);
        }

        internal Mesh GetGroundMesh(int index)
        {
            switch (index)
            {
                case 0: return groundMeshMiddle;
                case 1: return groundMeshEdge;
                case 2: return groundMeshCorder;
                default: throw new IndexOutOfRangeException(
                    $"Ground mesh index {index} is out of range of 0 to {GroundMeshCount - 1}");
            }
        }

        internal int GroundMeshCount => 3;
    }

    internal readonly struct TerrainDataData
    {
        public readonly int parcelSize;
        public readonly RectInt bounds;
        public readonly float maxHeight;
        [ReadOnly] public readonly NativeArray<byte> occupancyMap;
        public readonly int2 occupancyMapSize;
        public readonly int seed;
        public readonly float treesPerParcel;
        public readonly int treePrototypeCount;

        public TerrainDataData(int parcelSize, RectInt bounds, float maxHeight, Texture2D occupancyMap,
            int seed, float treesPerParcel, int treePrototypeCount)
        {
            this.parcelSize = parcelSize;
            this.bounds = bounds;
            this.maxHeight = maxHeight;
            this.occupancyMap = occupancyMap.GetRawTextureData<byte>();
            occupancyMapSize = int2(occupancyMap.width, occupancyMap.height);
            this.seed = seed;
            this.treesPerParcel = treesPerParcel;
            this.treePrototypeCount = treePrototypeCount;
        }

        public float GetHeight(float x, float z)
        {
            // The occupancy map has a 1 pixel border around the terrain.
            float2 scale = 1f / ((float2(bounds.width, bounds.height) + 2f) * parcelSize);

            float occupancy = SampleBilinearClamp(occupancyMap, occupancyMapSize, float2(
                (x - (bounds.x - 1) * parcelSize) * scale.x,
                (z - (bounds.y - 1) * parcelSize) * scale.y));

            // In the "worst case", if occupancy is 0.25, it can mean that the current vertex is on a
            // corner between one occupied parcel and three free ones, and height must be zero.
            if (occupancy < 0.25f)
            {
                float height = Noise.GetHeight(x, z);
                return lerp(height, 0f, occupancy * 4f);
            }
            else
            {
                return 0f;
            }
        }

        public float3 GetNormal(float x, float z) =>
            Noise.GetNormal(x, z);

        public Random GetRandom(int2 parcel)
        {
            return new Random(Noise.lowbias32((uint)(
                (parcel.y - bounds.y) * bounds.width + parcel.x - bounds.x + seed)));
        }

        public bool IsOccupied(int2 parcel)
        {
            if (parcel.x < bounds.x || parcel.y < bounds.y)
                return true;

            Vector2Int max = bounds.max;

            if (parcel.x >= max.x || parcel.y >= max.y)
                return true;

            int index = (parcel.y - bounds.y + 1) * occupancyMapSize.x + parcel.x - bounds.x + 1;
            return occupancyMap[index] > 0;
        }

        public bool NextTree(int2 parcel, ref Random random, out float3 position, out float rotationY,
            out int prototypeIndex)
        {
            if (random.NextFloat() < treesPerParcel)
            {
                position.x = parcel.x * parcelSize + random.NextFloat(parcelSize);
                position.z = parcel.y * parcelSize + random.NextFloat(parcelSize);
                position.y = GetHeight(position.x, position.z);
                rotationY = random.NextFloat(-180f, 180f);
                prototypeIndex = random.NextInt(treePrototypeCount);
                return true;
            }
            else
            {
                position = default;
                rotationY = 0f;
                prototypeIndex = -1;
                return false;
            }
        }

        public RectInt PositionToParcelRect(Vector3 center, float radius)
        {
            float invParcelSize = 1f / parcelSize;

            Vector2Int min = new Vector2Int(
                Mathf.RoundToInt((center.x - radius) * invParcelSize - 0.5f),
                Mathf.RoundToInt((center.z - radius) * invParcelSize - 0.5f));

            Vector2Int max = new Vector2Int(
                Mathf.RoundToInt((center.x + radius) * invParcelSize - 0.5f),
                Mathf.RoundToInt((center.z + radius) * invParcelSize - 0.5f));

            return new RectInt(min.x, min.y, max.x - min.x + 1, max.y - min.y + 1);
        }

        private static float SampleBilinearClamp(NativeArray<byte> texture, int2 textureSize, float2 uv)
        {
            uv = uv * textureSize - 0.5f;
            int2 min = (int2)floor(uv);

            // A quick prayer for Burst to SIMD this. 🙏
            int4 index = clamp(min.y + int4(1, 1, 0, 0), 0, textureSize.y - 1) * textureSize.x +
                         clamp(min.x + int4(0, 1, 1, 0), 0, textureSize.x - 1);

            float2 t = frac(uv);
            float top = lerp(texture[index.w], texture[index.z], t.x);
            float bottom = lerp(texture[index.x], texture[index.y], t.x);
            return lerp(top, bottom, t.y) * (1f / 255f);
        }
    }

    [Serializable]
    internal struct DetailPrototype
    {
        public GameObject source;
        public DetailScatterMode scatterMode;
        public int density;
        public float alignToGround;
        public float minScaleXZ;
        public float maxScaleXZ;
        public float minScaleY;
        public float maxScaleY;
        public Mesh mesh;
        public Material material;
    }

    internal enum DetailScatterMode
    {
        JitteredGrid
    }

    [Serializable]
    internal struct TreeLOD
    {
        public float minScreenSize;
        public Mesh mesh;
        public Material[] materials;
    }

    [Serializable]
    internal struct TreePrototype
    {
        public GameObject source;
        public GameObject collider;
        public float localSize;
        public TreeLOD[] lods;
    }
}
