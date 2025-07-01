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
        [field: SerializeField] private uint RandomSeed { get; set; }
        [field: SerializeField] internal Material GroundMaterial { get; private set; }
        [field: SerializeField] internal int ParcelSize { get; private set; }
        [field: SerializeField] public RectInt Bounds { get; set; }
        [field: SerializeField] internal float MaxHeight { get; private set; }
        [field: SerializeField] public Texture2D OccupancyMap { get; set; }
        [field: SerializeField] private float TreesPerParcel { get; set; }
        [field: SerializeField] public float DetailDistance { get; set; }
        [field: SerializeField] public bool RenderGround { get; set; }
        [field: SerializeField] public bool RenderTreesAndDetail { get; set; }
        [field: SerializeField] internal int GroundInstanceCapacity { get; set; }
        [field: SerializeField] internal int TreeInstanceCapacity { get; set; }
        [field: SerializeField] internal int DetailInstanceCapacity { get; set; }

        [field: SerializeField, EnumIndexedArray(typeof(GroundMeshPiece))]
        internal Mesh[] GroundMeshes { get; private set; }

        [field: SerializeField] internal TreePrototype[] TreePrototypes { get; private set; }
        [field: SerializeField] internal DetailPrototype[] DetailPrototypes { get; private set; }

        protected FunctionPointer<GetHeightDelegate> getHeight;
        protected FunctionPointer<GetNormalDelegate> getNormal;

        protected abstract void CompileNoiseFunctions();

        internal TerrainDataData GetData()
        {
            if (!getHeight.IsCreated)
                CompileNoiseFunctions();

            return new TerrainDataData(ParcelSize, Bounds, MaxHeight, OccupancyMap, RandomSeed,
                TreesPerParcel, TreePrototypes.Length, getHeight, getNormal);
        }

        private enum GroundMeshPiece : int
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
        private readonly uint randomSeed;
        private readonly float treesPerParcel;
        private readonly int treePrototypeCount;
        private readonly FunctionPointer<GetHeightDelegate> getHeight;
        private readonly FunctionPointer<GetNormalDelegate> getNormal;

        private static NativeArray<byte> emptyOccupancyMap;

        public TerrainDataData(int parcelSize, RectInt bounds, float maxHeight, Texture2D occupancyMap,
            uint randomSeed, float treesPerParcel, int treePrototypeCount,
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
                // The occupancy map is assumed to have a 1 pixel border around the terrain.
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
            uint seed = lowbias32(((uint)parcel.y << 16) + ((uint)parcel.x & 0xffff) + randomSeed);
            return new Random(seed != 0 ? seed : 0x6487ed51);
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

        internal RectInt PositionToParcelRect(float2 centerXZ, float radius)
        {
            float invParcelSize = 1f / parcelSize;
            int2 min = (int2)((centerXZ - radius) * invParcelSize);
            int2 size = (int2)((centerXZ + radius) * invParcelSize) - min + 1;
            return new RectInt(min.x, min.y, size.x, size.y);
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
        [field: SerializeField] public GameObject Source { get; private set; }
        [field: SerializeField] public DetailScatterMode ScatterMode { get; private set; }
        [field: SerializeField] public float Density { get; private set; }
        [field: SerializeField] public float MinScaleXZ { get; private set; }
        [field: SerializeField] public float MaxScaleXZ { get; private set; }
        [field: SerializeField] public float MinScaleY { get; private set; }
        [field: SerializeField] public float MaxScaleY { get; private set; }
        [field: SerializeField] public Mesh Mesh { get; internal set; }
        [field: SerializeField] public Material Material { get; internal set; }
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
        [field: SerializeField] public Mesh Mesh { get; internal set; }
        [field: SerializeField] public float MinScreenSize { get; internal set; }
        [field: SerializeField] public Material[] Materials { get; internal set; }
    }

    [Serializable]
    internal struct TreePrototype
    {
        [field: SerializeField] public GameObject Source { get; private set; }
        [field: SerializeField] public GameObject Collider { get; internal set; }
        [field: SerializeField] public float LocalSize { get; internal set; }
        [field: SerializeField] public TreeLOD[] Lods { get; internal set; }
    }
}
