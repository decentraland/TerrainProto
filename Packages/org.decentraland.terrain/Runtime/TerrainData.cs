using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;
using Unity.Mathematics.Geometry;
using UnityEngine;
using static Decentraland.Terrain.TerrainLog;
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
        [field: SerializeField] public float DetailDistance { get; set; }
        [field: SerializeField] public bool RenderGround { get; set; } = true;
        [field: SerializeField] public bool RenderTreesAndDetail { get; set; } = true;
        [field: SerializeField] internal int GroundInstanceCapacity { get; set; }
        [field: SerializeField] internal int TreeInstanceCapacity { get; set; }
        [field: SerializeField] internal int DetailInstanceCapacity { get; set; }

        [field: SerializeField, EnumIndexedArray(typeof(GroundMeshPiece))]
        internal Mesh[] GroundMeshes { get; private set; }

        [field: SerializeField] internal TreePrototype[] TreePrototypes { get; private set; }
        [field: SerializeField] internal DetailPrototype[] DetailPrototypes { get; private set; }

        protected FunctionPointer<GetHeightDelegate> getHeight;
        protected FunctionPointer<GetNormalDelegate> getNormal;
        private Task loadTreeInstancesTask;
        private NativeArray<int> treeIndices;
        private NativeArray<TreeInstance> treeInstances;
        private int2 treeMinParcel;
        private int2 treeMaxParcel;
        private NativeArray<TreePrototypeData> treePrototypes;

        private const int TERRAIN_SIZE_LIMIT = 512;
        private const int TREE_INSTANCE_LIMIT = 262144; // 2 ^ 18

        private void OnValidate()
        {
            RectInt bounds = Bounds;
            bounds.width = clamp(bounds.width, 0, TERRAIN_SIZE_LIMIT);
            bounds.height = clamp(bounds.height, 0, TERRAIN_SIZE_LIMIT);
            Bounds = bounds;

            DetailInstanceCapacity = max(DetailInstanceCapacity, 1);
            GroundInstanceCapacity = max(GroundInstanceCapacity, 1);
            ParcelSize = max(ParcelSize, 1);
            RandomSeed = max(RandomSeed, 1u);
            TreeInstanceCapacity = max(TreeInstanceCapacity, 1);
            treePrototypes.Dispose();
        }

        protected abstract void CompileNoiseFunctions();

        public void LoadTreeInstances()
        {
            string path = $"{Application.streamingAssetsPath}/TreeInstances.bin";
            BinaryReader reader = null;

            try
            {
                reader = new BinaryReader(
                    new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read),
                    new UTF8Encoding(false), false);

                treeMinParcel.x = reader.ReadInt32();
                treeMinParcel.y = reader.ReadInt32();
                treeMaxParcel.x = reader.ReadInt32();
                treeMaxParcel.y = reader.ReadInt32();
                int2 treeIndexSize = treeMaxParcel - treeMinParcel;

                if (any(treeIndexSize > TERRAIN_SIZE_LIMIT))
                    throw new IOException(
                        $"Tree index size of ({treeIndexSize.x}, {treeIndexSize.y}) exceeds the limit of {TERRAIN_SIZE_LIMIT}");

                treeIndices = new NativeArray<int>(treeIndexSize.x * treeIndexSize.y,
                    Allocator.Persistent, NativeArrayOptions.UninitializedMemory);

                unsafe
                {
                    var treeIndicesBytes = new Span<byte>(treeIndices.GetUnsafePtr(),
                        treeIndices.Length * sizeof(int));

                    while (treeIndicesBytes.Length > 0)
                    {
                        int read = reader.Read(treeIndicesBytes);
                        treeIndicesBytes = treeIndicesBytes.Slice(read);
                    }
                }

                int treeInstanceCount = reader.ReadInt32();

                if (treeInstanceCount > TREE_INSTANCE_LIMIT)
                    throw new IOException(
                        $"Tree instance count of {treeInstanceCount} exceeds the limit of {TREE_INSTANCE_LIMIT}");

                treeInstances = new NativeArray<TreeInstance>(treeInstanceCount, Allocator.Persistent,
                    NativeArrayOptions.UninitializedMemory);

                unsafe
                {
                    var treeInstancesSpan = new Span<byte>(treeInstances.GetUnsafePtr(),
                        treeInstances.Length * sizeof(TreeInstance));

                    while (treeInstancesSpan.Length > 0)
                    {
                        int read = reader.Read(treeInstancesSpan);
                        treeInstancesSpan = treeInstancesSpan.Slice(read);
                    }
                }
            }
            catch (Exception ex)
            {
                if (ex is not FileNotFoundException)
                    LogHandler.LogException(ex, this);

                if (treeIndices.IsCreated)
                    treeIndices.Dispose();

                if (treeInstances.IsCreated)
                    treeInstances.Dispose();

                treeIndices = new NativeArray<int>(0, Allocator.Persistent);
                treeInstances = new NativeArray<TreeInstance>(0, Allocator.Persistent);
            }
            finally
            {
                if (reader != null)
                    reader.Dispose();
            }
        }

        public Task LoadTreeInstancesAsync()
        {
            loadTreeInstancesTask = Task.Run(LoadTreeInstances);
            return loadTreeInstancesTask;
        }

        internal TerrainDataData GetData()
        {
            if (!treePrototypes.IsCreated)
            {
                treePrototypes = new NativeArray<TreePrototypeData>(TreePrototypes.Length,
                    Allocator.Persistent, NativeArrayOptions.UninitializedMemory);

                for (int i = 0; i < TreePrototypes.Length; i++)
                    treePrototypes[i] = new TreePrototypeData(TreePrototypes[i]);
            }

            if (loadTreeInstancesTask == null)
            {
                LoadTreeInstances();
                loadTreeInstancesTask = Task.CompletedTask;
            }
            else
            {
                loadTreeInstancesTask.Wait();
            }

            if (!getHeight.IsCreated)
                CompileNoiseFunctions();

            return new TerrainDataData(RandomSeed, ParcelSize, Bounds, MaxHeight, OccupancyMap,
                treePrototypes, treeIndices, treeInstances, getHeight, getNormal, treeMinParcel,
                treeMaxParcel);
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
        [ReadOnly] private readonly NativeArray<TreePrototypeData> treePrototypes;
        [ReadOnly] private readonly NativeArray<int> treeIndices;
        [ReadOnly] private readonly NativeArray<TreeInstance> treeInstances;
        private readonly FunctionPointer<GetHeightDelegate> getHeight;
        private readonly FunctionPointer<GetNormalDelegate> getNormal;
        private readonly int2 treeMinParcel;
        private readonly int2 treeMaxParcel;

        /// <summary>xy = min, zw = max, size = max - min</summary>
        private readonly int4 bounds;

        private static NativeArray<byte> emptyOccupancyMap;

        public TerrainDataData(uint randomSeed, int parcelSize, RectInt bounds, float maxHeight,
            Texture2D occupancyMap, NativeArray<TreePrototypeData> treePrototypes,
            NativeArray<int> treeIndices, NativeArray<TreeInstance> treeInstances,
            FunctionPointer<GetHeightDelegate> getHeight, FunctionPointer<GetNormalDelegate> getNormal,
            int2 treeMinParcel, int2 treeMaxParcel)
        {
            this.randomSeed = randomSeed;
            this.parcelSize = parcelSize;
            this.bounds = int4(bounds.xMin, bounds.yMin, bounds.xMax, bounds.yMax);
            this.maxHeight = maxHeight;
            this.treePrototypes = treePrototypes;
            this.treeIndices = treeIndices;
            this.treeInstances = treeInstances;
            this.getHeight = getHeight;
            this.getNormal = getNormal;
            this.treeMinParcel = treeMinParcel;
            this.treeMaxParcel = treeMaxParcel;

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

        public ReadOnlySpan<TreeInstance> GetTreeInstances(int2 parcel)
        {
            if (parcel.x < treeMinParcel.x || parcel.x >= treeMaxParcel.x
                || parcel.y < treeMinParcel.y || parcel.y >= treeMaxParcel.y)
            {
                return ReadOnlySpan<TreeInstance>.Empty;
            }

            int index = (parcel.y - treeMinParcel.y) * (treeMaxParcel.x - treeMinParcel.x) + parcel.x - treeMinParcel.x;
            int start = treeIndices[index++];
            int end = index < treeIndices.Length ? treeIndices[index] : treeInstances.Length;
            return treeInstances.AsReadOnlySpan().Slice(start, end - start);
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

        public bool TryGenerateTree(int2 parcel, TreeInstance instance, out float3 position,
            out float rotationY, out float scaleXZ, out float scaleY)
        {
            position.x = instance.positionX * parcelSize * (1f / 255f);
            position.y = 0f;
            position.z = instance.positionZ * parcelSize * (1f / 255f);
            rotationY = 0f;
            scaleXZ = 0f;
            scaleY = 0f;
            TreePrototypeData prototype = treePrototypes[instance.prototypeIndex];

            /*if (OverlapsOccupiedParcel(parcel, position.xz, prototype.radius))
                return false;*/

            position.xz += parcel * parcelSize;
            position.y = instance.positionY * (1f / 256f);
            rotationY = instance.rotationY * (360f / 255f);
            scaleXZ = prototype.minScaleXZ + instance.scaleXZ * prototype.scaleSizeXZ;
            scaleY = prototype.minScaleY + instance.scaleY * prototype.scaleSizeY;
            return true;
        }
    }

    public delegate float GetHeightDelegate(float x, float z);

    public delegate void GetNormalDelegate(float x, float z, out float3 normal);

    [Serializable]
    internal struct DetailPrototype
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

    internal readonly struct TreeInstance
    {
        public readonly byte prototypeIndex;
        public readonly byte positionX;
        public readonly short positionY;
        public readonly byte positionZ;
        public readonly byte rotationY;
        public readonly byte scaleXZ;
        public readonly byte scaleY;

        public TreeInstance(UnityEngine.TreeInstance instance, Vector3 position,
            int parcelSize, TreePrototype[] prototypes)
        {
            float2 fracPos = frac(position.XZ() / parcelSize);
            prototypeIndex = (byte)instance.prototypeIndex;
            positionX = (byte)round(fracPos.x * 255f);
            positionY = (short)round(position.y * 256f);
            positionZ = (byte)round(fracPos.y * 255f);
            rotationY = (byte)round(instance.rotation * (255f / PI2));
            TreePrototype prototype = prototypes[prototypeIndex];

            scaleXZ = (byte)round((instance.widthScale - prototype.MinScaleXZ)
                / (prototype.MaxScaleXZ - prototype.MaxScaleXZ) * 255f);

            scaleY = (byte)round((instance.heightScale - prototype.MinScaleY)
                / (prototype.MaxScaleY - prototype.MaxScaleY) * 255f);
        }
    }

    [Serializable]
    internal struct TreeLOD
    {
        [field: SerializeField] public Mesh Mesh { get; set; }
        [field: SerializeField] public float MinScreenSize { get; set; }
        [field: SerializeField] public Material[] Materials { get; set; }
    }

    [Serializable]
    public struct TreePrototype
    {
        [field: SerializeField] internal GameObject Source { get; private set; }
        [field: SerializeField] internal GameObject Collider { get; set; }
        [field: SerializeField] internal float LocalSize { get; set; }
        [field: SerializeField] public float MinScaleXZ { get; set; }
        [field: SerializeField] public float MaxScaleXZ { get; set; }
        [field: SerializeField] public float MinScaleY { get; set; }
        [field: SerializeField] public float MaxScaleY { get; set; }
        [field: SerializeField] internal float Radius { get; set; }
        [field: SerializeField] internal TreeLOD[] Lods { get; set; }
    }

    internal readonly struct TreePrototypeData
    {
        public readonly float minScaleXZ;
        public readonly float scaleSizeXZ;
        public readonly float minScaleY;
        public readonly float scaleSizeY;
        public readonly float radius;

        public TreePrototypeData(TreePrototype prototype)
        {
            minScaleXZ = prototype.MinScaleXZ;
            scaleSizeXZ = prototype.MaxScaleXZ - minScaleXZ;
            minScaleY = prototype.MinScaleY;
            scaleSizeY = prototype.MaxScaleY - minScaleY;
            radius = prototype.Radius;
        }
    }
}
