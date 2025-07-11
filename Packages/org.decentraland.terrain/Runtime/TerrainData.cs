using System;
using Unity.Burst;
using Unity.Collections;
using Unity.Mathematics;
using Unity.Mathematics.Geometry;
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
                TreesPerParcel, TreePrototypes, getHeight, getNormal);
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
        public readonly float maxHeight;
        [ReadOnly] private readonly NativeArray<byte> occupancyMap;
        private readonly int occupancyMapSize;
        private readonly uint randomSeed;
        private readonly float treesPerParcel;
        [ReadOnly, DeallocateOnJobCompletion] private readonly NativeArray<TreePrototypeData> treePrototypes;
        private readonly FunctionPointer<GetHeightDelegate> getHeight;
        private readonly FunctionPointer<GetNormalDelegate> getNormal;

        /// <summary>xy = min, zw = max, size = max - min</summary>
        public readonly int4 bounds;

        private static NativeArray<byte> emptyOccupancyMap;

        public TerrainDataData(int parcelSize, RectInt bounds, float maxHeight, Texture2D occupancyMap,
            uint randomSeed, float treesPerParcel, TreePrototype[] treePrototypes,
            FunctionPointer<GetHeightDelegate> getHeight, FunctionPointer<GetNormalDelegate> getNormal)
        {
            this.parcelSize = parcelSize;
            this.bounds = int4(bounds.xMin, bounds.yMin, bounds.xMax, bounds.yMax);
            this.maxHeight = maxHeight;
            this.randomSeed = randomSeed;
            this.treesPerParcel = treesPerParcel;
            this.getHeight = getHeight;
            this.getNormal = getNormal;

            if (IsPowerOfTwo(occupancyMap, out occupancyMapSize))
            {
                this.occupancyMap = occupancyMap.GetRawTextureData<byte>();
            }
            else
            {
                if (!emptyOccupancyMap.IsCreated)
                    emptyOccupancyMap = new NativeArray<byte>(0, Allocator.Persistent,
                        NativeArrayOptions.UninitializedMemory);

                this.occupancyMap = emptyOccupancyMap;
            }

            this.treePrototypes = new NativeArray<TreePrototypeData>(treePrototypes.Length,
                Allocator.TempJob, NativeArrayOptions.UninitializedMemory);

            for (int i = 0; i < treePrototypes.Length; i++)
                this.treePrototypes[i] = new TreePrototypeData(treePrototypes[i]);
        }

        public bool BoundsOverlaps(int4 bounds) =>
            all(this.bounds.zw >= bounds.xy & this.bounds.xy <= bounds.zw);

        public float GetHeight(float x, float z)
        {
            float occupancy;

            if (occupancyMapSize > 0)
            {
                // Take the bounds of the terrain, put a single pixel border around it, increase the
                // size to the next power of two, map xz=0,0 to uv=0.5,0.5 and parcelSize to pixel size,
                // and that's the occupancy map.
                float2 uv = (float2(x, z) / parcelSize + occupancyMapSize * 0.5f) / occupancyMapSize;
                occupancy = SampleBilinearClamp(occupancyMap, occupancyMapSize, uv);
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

        private static bool IsPowerOfTwo(Texture2D texture, out int size)
        {
            if (texture != null)
            {
                int width = texture.width;

                if (ispow2(width) && texture.height == width)
                {
                    size = width;
                    return true;
                }
            }

            size = 0;
            return false;
        }

        public bool IsOccupied(int2 parcel)
        {
            if (occupancyMapSize <= 0)
                return false;

            if (parcel.x < bounds.x || parcel.y < bounds.y
                                    || parcel.x >= bounds.z || parcel.y >= bounds.w)
            {
                return true;
            }

            parcel += occupancyMapSize / 2;
            int index = parcel.y * occupancyMapSize + parcel.x;
            return occupancyMap[index] > 0;
        }

        public bool OverlapsClipVolume(int2 parcel, NativeArray<ClipPlane> clipPlanes)
        {
            // Because parcels are small relative to the camera frustum and their size stays
            // constant, there's no need to test against frustum bounds.

            int2 min = parcel * parcelSize;
            int2 max = min + parcelSize;
            var bounds = new MinMaxAABB(float3(min.x, 0f, min.y), float3(max.x, maxHeight, max.y));

            for (int i = 0; i < clipPlanes.Length; i++)
            {
                ClipPlane clipPlane = clipPlanes[i];
                float3 farCorner = bounds.GetCorner(clipPlane.farCornerIndex);

                if (clipPlane.plane.SignedDistanceToPoint(farCorner) < 0f)
                    return false;
            }

            return true;
        }

        private bool OverlapsNeighbor(int2 parcel, float2 positionXZ, float canopyRadius)
        {
            Random random = GetRandom(parcel);

            if (random.NextFloat() >= treesPerParcel)
                return false;

            // This must be the same as in NextTree.
            float2 neighborXZ = random.NextFloat2(parcelSize);
            int prototypeIndex = random.NextInt(treePrototypes.Length);

            float radiusSum = canopyRadius + treePrototypes[prototypeIndex].canopyRadius;

            if (distancesq(positionXZ, neighborXZ) < radiusSum * radiusSum)
                return true;

            return false;
        }

        public bool NextTree(int2 parcel, out Random random, out float3 position, out float rotationY,
            out int prototypeIndex)
        {
            random = GetRandom(parcel);
            position = default;
            rotationY = 0f;
            prototypeIndex = -1;

            if (treesPerParcel <= 0f || treePrototypes.Length == 0)
                return false;

            if (random.NextFloat() >= treesPerParcel)
                return false;

            // This must be the same as in OverlapsNeighbor.
            float2 positionXZ = random.NextFloat2(parcelSize);
            prototypeIndex = random.NextInt(treePrototypes.Length);

            float canopyRadius = treePrototypes[prototypeIndex].canopyRadius;

            if (positionXZ.x < canopyRadius)
            {
                if (IsOccupied(int2(parcel.x - 1, parcel.y)))
                    return false;

                if (positionXZ.y < canopyRadius)
                {
                    if (IsOccupied(int2(parcel.x - 1, parcel.y - 1)))
                        return false;
                }
            }

            if (parcelSize - positionXZ.x < canopyRadius)
            {
                if (IsOccupied(int2(parcel.x + 1, parcel.y)))
                    return false;

                if (parcelSize - positionXZ.y < canopyRadius)
                {
                    if (IsOccupied(int2(parcel.x + 1, parcel.y + 1)))
                        return false;
                }
            }

            if (positionXZ.y < canopyRadius)
            {
                if (IsOccupied(int2(parcel.x, parcel.y - 1)))
                    return false;

                if (parcelSize - positionXZ.x < canopyRadius)
                {
                    if (IsOccupied(int2(parcel.x + 1, parcel.y - 1)))
                        return false;
                }
            }

            if (parcelSize - positionXZ.y < canopyRadius)
            {
                if (IsOccupied(int2(parcel.x, parcel.y + 1)))
                    return false;

                if (positionXZ.x < canopyRadius)
                {
                    if (IsOccupied(int2(parcel.x - 1, parcel.y + 1)))
                        return false;
                }
            }

            if (OverlapsNeighbor(int2(parcel.x - 1, parcel.y), positionXZ, canopyRadius))
                return false;

            if (OverlapsNeighbor(int2(parcel.x - 1, parcel.y - 1), positionXZ, canopyRadius))
                return false;

            if (OverlapsNeighbor(int2(parcel.x, parcel.y - 1), positionXZ, canopyRadius))
                return false;

            if (OverlapsNeighbor(int2(parcel.x + 1, parcel.y - 1), positionXZ, canopyRadius))
                return false;

            position.xz = positionXZ + parcel * parcelSize;
            position.y = GetHeight(position.x, position.z);
            rotationY = random.NextFloat(-180f, 180f);

            return true;
        }

        internal RectInt PositionToParcelRect(float2 centerXZ, float radius)
        {
            float invParcelSize = 1f / parcelSize;
            int2 min = (int2)floor((centerXZ - radius) * invParcelSize);
            int2 size = (int2)ceil((centerXZ + radius) * invParcelSize) - min;
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

        private readonly struct TreePrototypeData
        {
            public readonly float trunkRadius;
            public readonly float canopyRadius;

            public TreePrototypeData(TreePrototype prototype)
            {
                trunkRadius = prototype.TrunkRadius;
                canopyRadius = prototype.CanopyRadius;
            }
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
        [field: SerializeField] public float TrunkRadius { get; internal set; }
        [field: SerializeField] public float CanopyRadius { get; internal set; }
        [field: SerializeField] public TreeLOD[] Lods { get; internal set; }
    }
}
