using System;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
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
        [field: SerializeField] private float TreeSpacing { get; set; }
        [field: SerializeField, Range(0f, 1f)] private float TreeDensity { get; set; }
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
        private NativeArray<int> treeIndices;
        private NativeList<byte2> treePositions;
        private JobHandle generateTreePositions;

        // To detect and automatically regenerate tree positions when these change.
        [NonSerialized] private RectInt oldBounds;
        [NonSerialized] private float oldTreeSpacing;

        private void OnValidate()
        {
            TreeSpacing = max(TreeSpacing, 1f);
        }

        protected abstract void CompileNoiseFunctions();

        /// <remarks>Awaiting this method is optional.</remarks>
        public async Awaitable GenerateTreePositions()
        {
            oldBounds = Bounds;
            oldTreeSpacing = TreeSpacing;

            if (treeIndices.IsCreated)
                treeIndices.Dispose();

            if (treePositions.IsCreated)
                treePositions.Dispose();

            if (Bounds.width * Bounds.height <= 0)
            {
                treeIndices = new NativeArray<int>(0, Allocator.Persistent);
                treePositions = new NativeList<byte2>(0, Allocator.Persistent);
                return;
            }

            var blueNoiseJob = new BlueNoise2D((Bounds.size * ParcelSize).ToFloat2(),
                TreeSpacing * 0.5f, (Bounds.position * -ParcelSize).ToFloat2(), new Random(RandomSeed));

            JobHandle blueNoise = blueNoiseJob.Schedule();
            this.generateTreePositions.Complete();

            treeIndices = new NativeArray<int>(Bounds.width * Bounds.height, Allocator.Persistent,
                NativeArrayOptions.UninitializedMemory);

            treePositions = new NativeList<byte2>(Allocator.Persistent);

            var generateTreePositionsJob = new GenerateTreePositionsJob()
            {
                grid = blueNoiseJob.Grid,
                points = blueNoiseJob.Points,
                gridSize = blueNoiseJob.GridSize,
                boundsSize = Bounds.size.ToInt2(),
                parcelSize = ParcelSize,
                treeIndices = treeIndices,
                treePositions = treePositions
            };

            // Each invocation of this method must only ever await the job handle it created and not
            // whatever latest job handle is stored in the member field, hence the code gymnastics.
            JobHandle generateTreePositions = generateTreePositionsJob.Schedule(blueNoise);
            this.generateTreePositions = generateTreePositions;

            JobHandle.ScheduleBatchedJobs();

            while (!generateTreePositions.IsCompleted)
                await Awaitable.NextFrameAsync();

            generateTreePositions.Complete();
            blueNoiseJob.Dispose();
        }

        internal TerrainDataData GetData()
        {
            if (!treePositions.IsCreated || oldBounds != Bounds || oldTreeSpacing != TreeSpacing)
                GenerateTreePositions();

            if (!getHeight.IsCreated)
                CompileNoiseFunctions();

            generateTreePositions.Complete();

            return new TerrainDataData(ParcelSize, Bounds, MaxHeight, OccupancyMap, RandomSeed,
                TreeDensity, TreePrototypes, treeIndices, treePositions.AsArray(), getHeight,
                getNormal);
        }

        [BurstCompile]
        private struct GenerateTreePositionsJob : IJob
        {
            public NativeArray<int> grid;
            public NativeList<float2> points;
            public int2 gridSize;
            public int2 boundsSize;
            public int parcelSize;
            [WriteOnly] public NativeArray<int> treeIndices;
            public NativeList<byte2> treePositions;

            public void Execute()
            {
                // We loop over parcels. For each parcel, we find the point grid rectangle that contains
                // it. We loop through those grid cells and add any points that are inside the parcel to
                // the parcel's list segment of points.

                float metersToNorm = 255f / parcelSize;
                float2 parcelToGrid = (float2)gridSize / boundsSize;

                for (int z = 0; z < boundsSize.y; z++)
                for (int x = 0; x < boundsSize.x; x++)
                {
                    treeIndices[z * boundsSize.x + x] = treePositions.Length;
                    int2 min = (int2)floor(float2(x, z) * parcelToGrid);
                    int2 max = min + (int2)ceil(parcelToGrid);
                    float2 parcelOrigin = int2(x, z) * parcelSize;

                    for (int j = min.y; j < max.y; j++)
                    for (int i = min.x; i < max.x; i++)
                    {
                        int index = grid[j * gridSize.x + i];

                        if (index < 0)
                            continue;

                        float2 localPosition = points[index] - parcelOrigin;

                        if (any(localPosition < 0f) || any(localPosition >= parcelSize))
                            continue;

                        treePositions.Add((byte2)round(localPosition * metersToNorm));
                    }
                }
            }
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
        private readonly float treeDensity;
        private readonly uint randomSeed;
        [ReadOnly, DeallocateOnJobCompletion] private readonly NativeArray<TreePrototypeData> treePrototypes;
        [ReadOnly] private readonly NativeArray<int> treeIndices;
        [ReadOnly] private readonly NativeArray<byte2> treePositions;
        private readonly FunctionPointer<GetHeightDelegate> getHeight;
        private readonly FunctionPointer<GetNormalDelegate> getNormal;

        /// <summary>xy = min, zw = max, size = max - min</summary>
        public readonly int4 bounds;

        private static NativeArray<byte> emptyOccupancyMap;

        public TerrainDataData(int parcelSize, RectInt bounds, float maxHeight, Texture2D occupancyMap,
            uint randomSeed, float treeDensity, TreePrototype[] treePrototypes,
            NativeArray<int> treeIndices, NativeArray<byte2> treePositions,
            FunctionPointer<GetHeightDelegate> getHeight, FunctionPointer<GetNormalDelegate> getNormal)
        {
            this.parcelSize = parcelSize;
            this.bounds = int4(bounds.xMin, bounds.yMin, bounds.xMax, bounds.yMax);
            this.maxHeight = maxHeight;
            this.treeDensity = treeDensity;
            this.randomSeed = randomSeed;
            this.treeIndices = treeIndices;
            this.treePositions = treePositions;
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

        public bool TryGenerateTree(int2 parcel, byte2 localPosition, ref Random random,
            out int prototypeIndex, out float3 position, out float rotationY)
        {
            prototypeIndex = -1;
            position = default;
            rotationY = 0f;

            if (random.NextFloat() > treeDensity)
                return false;

            prototypeIndex = random.NextInt(treePrototypes.Length);
            position.xz = localPosition * (parcelSize * (1f / 255f));
            float canopyRadius = treePrototypes[prototypeIndex].canopyRadius;

            if (position.x < canopyRadius)
            {
                if (IsOccupied(int2(parcel.x - 1, parcel.y)))
                    return false;

                if (position.z < canopyRadius)
                {
                    if (IsOccupied(int2(parcel.x - 1, parcel.y - 1)))
                        return false;
                }
            }

            if (parcelSize - position.x < canopyRadius)
            {
                if (IsOccupied(int2(parcel.x + 1, parcel.y)))
                    return false;

                if (parcelSize - position.z < canopyRadius)
                {
                    if (IsOccupied(int2(parcel.x + 1, parcel.y + 1)))
                        return false;
                }
            }

            if (position.z < canopyRadius)
            {
                if (IsOccupied(int2(parcel.x, parcel.y - 1)))
                    return false;

                if (parcelSize - position.x < canopyRadius)
                {
                    if (IsOccupied(int2(parcel.x + 1, parcel.y - 1)))
                        return false;
                }
            }

            if (parcelSize - position.z < canopyRadius)
            {
                if (IsOccupied(int2(parcel.x, parcel.y + 1)))
                    return false;

                if (position.x < canopyRadius)
                {
                    if (IsOccupied(int2(parcel.x - 1, parcel.y + 1)))
                        return false;
                }
            }

            position.xz += parcel * parcelSize;
            position.y = GetHeight(position.x, position.z);
            rotationY = random.NextFloat(-180f, 180f);
            return true;
        }

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

        public ReadOnlySpan<byte2> GetTreePositions(int2 parcel)
        {
            int index = (parcel.y - bounds.y) * (bounds.z - bounds.x) + parcel.x - bounds.x;
            int start = treeIndices[index++];
            int end = index < treeIndices.Length ? treeIndices[index] : treePositions.Length;
            return treePositions.AsReadOnlySpan().Slice(start, end - start);
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
            public readonly float canopyRadius;
            public readonly float trunkRadius;

            public TreePrototypeData(TreePrototype prototype)
            {
                canopyRadius = prototype.CanopyRadius;
                trunkRadius = prototype.TrunkRadius;
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
