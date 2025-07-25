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
        [field: SerializeField] public bool RenderGround { get; set; } = true;
        [field: SerializeField] public bool RenderTreesAndDetail { get; set; } = true;
        [field: SerializeField] internal int GroundInstanceCapacity { get; set; }
        [field: SerializeField] internal int TreeInstanceCapacity { get; set; }
        [field: SerializeField] internal int ClutterInstanceCapacity { get; set; }
        [field: SerializeField] internal int GrassInstanceCapacity { get; set; }

        [field: SerializeField, EnumIndexedArray(typeof(GroundMeshPiece))]
        internal Mesh[] GroundMeshes { get; private set; }

        [field: SerializeField] internal TreePrototype[] TreePrototypes { get; private set; }
        [field: SerializeField] internal ClutterPrototype[] ClutterPrototypes { get; private set; }
        [field: SerializeField] internal GrassPrototype[] GrassPrototypes { get; private set; }

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
            ClutterInstanceCapacity = max(ClutterInstanceCapacity, 1);
            GrassInstanceCapacity = max(GrassInstanceCapacity, 1);
            GroundInstanceCapacity = max(GroundInstanceCapacity, 1);
            ParcelSize = max(ParcelSize, 1);
            RandomSeed = max(RandomSeed, 1u);
            TreeInstanceCapacity = max(TreeInstanceCapacity, 1);
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

            return new TerrainDataData(RandomSeed, ParcelSize, Bounds, MaxHeight, OccupancyMap,
                TreeDensity, TreePrototypes, treeIndices, treePositions.AsArray(), ClutterPrototypes,
                getHeight, getNormal);
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
        private readonly uint randomSeed;
        public readonly int parcelSize;
        public readonly float maxHeight;
        [ReadOnly] private readonly NativeArray<byte> occupancyMap;
        private readonly int occupancyMapSize;
        private readonly float treeDensity;
        [ReadOnly, DeallocateOnJobCompletion] private readonly NativeArray<TreePrototypeData> treePrototypes;
        [ReadOnly] private readonly NativeArray<int> treeIndices;
        [ReadOnly] private readonly NativeArray<byte2> treePositions;
        [ReadOnly, DeallocateOnJobCompletion] private readonly NativeArray<ClutterPrototypeData> clutterPrototypes;
        private readonly FunctionPointer<GetHeightDelegate> getHeight;
        private readonly FunctionPointer<GetNormalDelegate> getNormal;

        /// <summary>xy = min, zw = max, size = max - min</summary>
        public readonly int4 bounds;

        private static NativeArray<byte> emptyOccupancyMap;

        public TerrainDataData(uint randomSeed, int parcelSize, RectInt bounds, float maxHeight,
            Texture2D occupancyMap, float treeDensity, TreePrototype[] treePrototypes,
            NativeArray<int> treeIndices, NativeArray<byte2> treePositions,
            ClutterPrototype[] clutterPrototypes, FunctionPointer<GetHeightDelegate> getHeight,
            FunctionPointer<GetNormalDelegate> getNormal)
        {
            this.randomSeed = randomSeed;
            this.parcelSize = parcelSize;
            this.bounds = int4(bounds.xMin, bounds.yMin, bounds.xMax, bounds.yMax);
            this.maxHeight = maxHeight;
            this.treeDensity = treeDensity;
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

            this.clutterPrototypes = new NativeArray<ClutterPrototypeData>(clutterPrototypes.Length,
                Allocator.TempJob, NativeArrayOptions.UninitializedMemory);

            for (int i = 0; i < clutterPrototypes.Length; i++)
                this.clutterPrototypes[i] = new ClutterPrototypeData(clutterPrototypes[i]);
        }

        public bool BoundsOverlaps(int4 bounds) =>
            all(this.bounds.zw >= bounds.xy & this.bounds.xy <= bounds.zw);

        public void GenerateClutterPositions(int2 parcel, NativeList<float2> obstaclePositions,
            ref Random random, NativeList<float2> clutterPositions)
        {
            const float minSqrDistance = 16f;

            static bool CanEmitPoint(in NativeList<float2> points, in float2 point)
            {
                for (int i = 0; i < points.Length; i++)
                    if (distancesq(point, points[i]) < minSqrDistance)
                        return false;

                return true;
            }

            int positionCount = min(clutterPositions.Capacity, 2);

            for (int i = 0; i < positionCount; i++)
            {
                float2 point = random.NextFloat2(parcelSize) + parcel * parcelSize;

                if (CanEmitPoint(in obstaclePositions, point))
                {
                    obstaclePositions.Add(point);
                    clutterPositions.Add(point);
                }
            }
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

        private bool OverlapsOccupiedParcel(int2 parcel, float2 localPosition, float radius)
        {
            if (localPosition.x < radius)
            {
                if (IsOccupied(int2(parcel.x - 1, parcel.y)))
                    return true;

                if (localPosition.y < radius)
                {
                    if (IsOccupied(int2(parcel.x - 1, parcel.y - 1)))
                        return true;
                }
            }

            if (parcelSize - localPosition.x < radius)
            {
                if (IsOccupied(int2(parcel.x + 1, parcel.y)))
                    return true;

                if (parcelSize - localPosition.y < radius)
                {
                    if (IsOccupied(int2(parcel.x + 1, parcel.y + 1)))
                        return true;
                }
            }

            if (localPosition.y < radius)
            {
                if (IsOccupied(int2(parcel.x, parcel.y - 1)))
                    return true;

                if (parcelSize - localPosition.x < radius)
                {
                    if (IsOccupied(int2(parcel.x + 1, parcel.y - 1)))
                        return true;
                }
            }

            if (parcelSize - localPosition.y < radius)
            {
                if (IsOccupied(int2(parcel.x, parcel.y + 1)))
                    return true;

                if (localPosition.x < radius)
                {
                    if (IsOccupied(int2(parcel.x - 1, parcel.y + 1)))
                        return true;
                }
            }

            return false;
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

        public bool TryGenerateClutter(int2 parcel, float2 positionXZ, ref Random random,
            out int prototypeIndex, out float rotationY, out float scale)
        {
            rotationY = 0f;

            prototypeIndex = random.NextInt(clutterPrototypes.Length);
            ClutterPrototypeData prototype = clutterPrototypes[prototypeIndex];
            scale = random.NextFloat(prototype.minScale, prototype.maxScale);
            float radius = prototype.radius * scale;

            if (OverlapsOccupiedParcel(parcel, positionXZ - parcel * parcelSize, radius))
                return false;

            rotationY = random.NextFloat(-180f, 180f);
            return true;
        }

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

            if (OverlapsOccupiedParcel(parcel, position.xz, canopyRadius))
                return false;

            position.xz += parcel * parcelSize;
            position.y = GetHeight(position.x, position.z);
            rotationY = random.NextFloat(-180f, 180f);
            return true;
        }

        private readonly struct ClutterPrototypeData
        {
            public readonly float radius;
            public readonly float minScale;
            public readonly float maxScale;

            public ClutterPrototypeData(ClutterPrototype prototype)
            {
                radius = prototype.Radius;
                minScale = prototype.MinScale;
                maxScale = prototype.MaxScale;
            }
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

    public delegate float GetHeightDelegate(float x, float z);

    public delegate void GetNormalDelegate(float x, float z, out float3 normal);

    [Serializable]
    internal struct ClutterLOD
    {
        [field: SerializeField] public Mesh Mesh { get; set; }
        [field: SerializeField] public float MinScreenSize { get; set; }
        [field: SerializeField] public Material[] Materials { get; set; }
    }

    [Serializable]
    internal struct ClutterPrototype
    {
        [field: SerializeField] public GameObject Source { get; private set; }
        [field: SerializeField] public GameObject Collider { get; set; }
        [field: SerializeField] public float LocalSize { get; set; }
        [field: SerializeField] public float Radius { get; private set; }
        [field: SerializeField] public float MinScale { get; private set; }
        [field: SerializeField] public float MaxScale { get; private set; }
        [field: SerializeField] public ClutterLOD[] Lods { get; set; }
    }

    [Serializable]
    internal struct GrassPrototype
    {
        [field: SerializeField] public GameObject Source { get; private set; }
        [field: SerializeField] public float Density { get; private set; }
        [field: SerializeField] public float MinScaleXZ { get; private set; }
        [field: SerializeField] public float MaxScaleXZ { get; private set; }
        [field: SerializeField] public float MinScaleY { get; private set; }
        [field: SerializeField] public float MaxScaleY { get; private set; }
        [field: SerializeField] public Mesh Mesh { get; set; }
        [field: SerializeField] public Material Material { get; set; }
    }

    [Serializable]
    internal struct TreeLOD
    {
        [field: SerializeField] public Mesh Mesh { get; set; }
        [field: SerializeField] public float MinScreenSize { get; set; }
        [field: SerializeField] public Material[] Materials { get; set; }
    }

    [Serializable]
    internal struct TreePrototype
    {
        [field: SerializeField] public GameObject Source { get; private set; }
        [field: SerializeField] public GameObject Collider { get; set; }
        [field: SerializeField] public float LocalSize { get; set; }
        [field: SerializeField] public float CanopyRadius { get; private set; }
        [field: SerializeField] public float TrunkRadius { get; private set; }
        [field: SerializeField] public TreeLOD[] Lods { get; set; }
    }
}
