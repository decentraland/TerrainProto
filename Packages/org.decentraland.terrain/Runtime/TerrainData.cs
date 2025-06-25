using System;
using Unity.Burst;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
using static Unity.Mathematics.math;
using Random = Unity.Mathematics.Random;

namespace Decentraland.Terrain
{
    public abstract class TerrainData : ScriptableObject
    {
        public int randomSeed = 1;
        public Material groundMaterial;
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

        [SerializeField, EnumIndexedArray(typeof(GroundMeshType))]
        internal Mesh[] groundMeshes;

        [SerializeField] internal TreePrototype[] treePrototypes;
        [SerializeField] internal DetailPrototype[] detailPrototypes;

        protected FunctionPointer<GetHeightDelegate> getHeight;
        protected FunctionPointer<GetNormalDelegate> getNormal;

        protected abstract void CompileNoiseFunctions();

        internal TerrainDataData GetData()
        {
            if (!getHeight.IsCreated)
                CompileNoiseFunctions();

            return new TerrainDataData(parcelSize, bounds, maxHeight, occupancyMap, randomSeed,
                treesPerParcel, treePrototypes.Length, getHeight, getNormal);
        }

        private enum GroundMeshType : int
        {
            Middle,
            Edge,
            Corner
        }
    }

    internal readonly struct TerrainDataData
    {
        public readonly int parcelSize;
        public readonly RectInt bounds;
        public readonly float maxHeight;
        [ReadOnly] private readonly NativeArray<byte> occupancyMap;
        private readonly int2 occupancyMapSize;
        private readonly int randomSeed;
        private readonly float treesPerParcel;
        private readonly int treePrototypeCount;
        private readonly FunctionPointer<GetHeightDelegate> getHeight;
        private readonly FunctionPointer<GetNormalDelegate> getNormal;

        private static NativeArray<byte> emptyOccupancyMap;

        public TerrainDataData(int parcelSize, RectInt bounds, float maxHeight, Texture2D occupancyMap,
            int randomSeed, float treesPerParcel, int treePrototypeCount,
            FunctionPointer<GetHeightDelegate> getHeight, FunctionPointer<GetNormalDelegate> getNormal)
        {
            this.parcelSize = parcelSize;
            this.bounds = bounds;
            this.maxHeight = maxHeight;
            this.randomSeed = randomSeed;
            this.treesPerParcel = treesPerParcel;
            this.treePrototypeCount = treePrototypeCount;
            this.getHeight = getHeight;
            this.getNormal = getNormal;

            if (occupancyMap != null)
            {
                this.occupancyMap = occupancyMap.GetRawTextureData<byte>();
                occupancyMapSize = int2(occupancyMap.width, occupancyMap.height);
            }
            else
            {
                if (!emptyOccupancyMap.IsCreated)
                    emptyOccupancyMap = new NativeArray<byte>(0, Allocator.Persistent,
                        NativeArrayOptions.UninitializedMemory);

                this.occupancyMap = emptyOccupancyMap;
                occupancyMapSize = default;
            }
        }

        public float GetHeight(float x, float z)
        {
            float occupancy;

            if (occupancyMapSize.x > 0)
            {
                // The occupancy map has a 1 pixel border around the terrain.
                float2 scale = 1f / ((float2(bounds.width, bounds.height) + 2f) * parcelSize);

                occupancy = SampleBilinearClamp(occupancyMap, occupancyMapSize, float2(
                    (x - (bounds.x - 1) * parcelSize) * scale.x,
                    (z - (bounds.y - 1) * parcelSize) * scale.y));
            }
            else
            {
                occupancy = 0f;
            }

            // In the "worst case", if occupancy is 0.25, it can mean that the current vertex is on a
            // corner between one occupied parcel and three free ones, and height must be zero.
            if (occupancy < 0.25f)
            {
                float height = getHeight.Invoke(x, z);
                return lerp(height, 0f, occupancy * 4f);
            }
            else
            {
                return 0f;
            }
        }

        public float3 GetNormal(float x, float z)
        {
            getNormal.Invoke(x, z, out float3 normal);
            return normal;
        }

        public Random GetRandom(int2 parcel)
        {
            static uint lowbias32(uint x)
            {
                x ^= x >> 16;
                x *= 0x21f0aaad;
                x ^= x >> 15;
                x *= 0xd35a2d97;
                x ^= x >> 15;
                return x;
            }

            parcel += 32768;
            uint seed = lowbias32((uint)((parcel.y << 16) + (parcel.x & 0xffff)));
            return new Random(seed != 0 ? seed : 1);
        }

        public bool IsOccupied(int2 parcel)
        {
            if (occupancyMapSize.x == 0)
                return false;

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
            if (treePrototypeCount > 0 && random.NextFloat() < treesPerParcel)
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
        public float density;
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

    public delegate float GetHeightDelegate(float x, float z);

    public delegate void GetNormalDelegate(float x, float z, out float3 normal);

    [Serializable]
    internal struct TreeLOD
    {
        public Mesh mesh;
        public float minScreenSize;
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
