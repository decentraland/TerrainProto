using System;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using Unity.Mathematics.Geometry;
using UnityEngine;
using UnityEngine.Profiling;
using UnityEngine.Rendering;
using static Unity.Mathematics.math;
using Plane = Unity.Mathematics.Geometry.Plane;
using Random = Unity.Mathematics.Random;

namespace Decentraland.Terrain
{
    [ExecuteAlways]
    public sealed class TerrainRenderer : MonoBehaviour
    {
        [field: SerializeField] private TerrainData terrainData { get; set; }

        private static readonly Vector3[] CLIP_CORNERS =
        {
            new(-1f, -1f, 0f), new(-1f, -1f, 1f), new(-1f, 1f, 0f), new(-1f, 1f, 1f), new(1f, -1f, 0f),
            new(1f, -1f, 1f), new(1f, 1f, 0f), new(1f, 1f, 1f)
        };

        public static ILogger Debug = UnityEngine.Debug.unityLogger;
        private static MaterialPropertyBlock groundMatProps;
        private static NativeArray<int4> magicPattern;
        private static readonly int PARCEL_SIZE_ID = Shader.PropertyToID("_ParcelSize");
        private static readonly int TERRAIN_BOUNDS_ID = Shader.PropertyToID("_TerrainBounds");

#if UNITY_EDITOR
        internal int DetailInstanceCount { get; private set; }
        internal int GroundInstanceCount { get; private set; }
        internal int TreeInstanceCount { get; private set; }
#endif

        private void Update()
        {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            if (terrainData == null)
            {
                Debug.LogError("Terrain data is not set up properly", this);
                enabled = false;
                return;
            }
#endif

            Camera camera;

#if UNITY_EDITOR
            if (UnityEditor.SceneVisibilityManager.instance.IsHidden(gameObject))
                return;

            if (!Application.isPlaying)
            {
                var sceneView = UnityEditor.SceneView.lastActiveSceneView;
                camera = sceneView != null ? sceneView.camera : Camera.main;
            }
            else
#endif
            {
                camera = Camera.main;
            }

            if (camera == null)
                return;

            Render(terrainData, camera,
#if UNITY_EDITOR
                true, this
#else
                false
#endif
            );
        }

        private void OnDrawGizmosSelected()
        {
            Gizmos.color = Color.green;
            Bounds bounds = Bounds;
            Gizmos.DrawWireCube(bounds.center, bounds.size);
        }

        public Bounds Bounds => terrainData != null ? GetBounds(terrainData) : default;

        private static NativeArray<int4> CreateMagicPattern()
        {
            var magicPattern = new NativeArray<int4>(16, Allocator.Persistent,
                NativeArrayOptions.UninitializedMemory);

            // x and y are relative parcel coordinates, z is the mesh to use (0 is center piece, 1 is
            // edge piece, and 2 is corner piece), and w is the rotation around the Y axis. Ground
            // meshes are to be placed in a 2x2 square (items 0 to 3) and then in an ever expanding
            // concentric rings around that (items 4 to 15), doubling the parcel coordinate values after
            // every iteration.

            magicPattern[0] = int4(0, 0, 0, 0);
            magicPattern[1] = int4(-1, 0, 0, 0);
            magicPattern[2] = int4(-1, -1, 0, 0);
            magicPattern[3] = int4(0, -1, 0, 0);
            magicPattern[4] = int4(1, 1, 2, 0);
            magicPattern[5] = int4(0, 1, 1, 0);
            magicPattern[6] = int4(-1, 1, 1, 0);
            magicPattern[7] = int4(-2, 1, 2, -90);
            magicPattern[8] = int4(-2, 0, 1, -90);
            magicPattern[9] = int4(-2, -1, 1, -90);
            magicPattern[10] = int4(-2, -2, 2, 180);
            magicPattern[11] = int4(-1, -2, 1, 180);
            magicPattern[12] = int4(0, -2, 1, 180);
            magicPattern[13] = int4(1, -2, 2, 90);
            magicPattern[14] = int4(1, -1, 1, 90);
            magicPattern[15] = int4(1, 0, 1, 90);

            return magicPattern;
        }

        private static Bounds GetBounds(TerrainData terrainData)
        {
            RectInt bounds = terrainData.Bounds;
            int parcelSize = terrainData.ParcelSize;
            float maxHeight = terrainData.MaxHeight;
            Vector2 center = bounds.center * parcelSize;
            Vector2Int size = bounds.size * parcelSize;

            return new Bounds(new Vector3(center.x, maxHeight * 0.5f, center.y),
                new Vector3(size.x, maxHeight, size.y));
        }

        private static bool OverlapsClipVolume(MinMaxAABB bounds, MinMaxAABB clipBounds,
            NativeArray<ClipPlane> clipPlanes)
        {
            if (!bounds.Overlaps(clipBounds))
                return false;

            for (int i = 0; i < clipPlanes.Length; i++)
            {
                ClipPlane clipPlane = clipPlanes[i];
                float3 farCorner = bounds.GetCorner(clipPlane.farCornerIndex);

                if (clipPlane.plane.SignedDistanceToPoint(farCorner) < 0f)
                    return false;
            }

            return true;
        }

        public static void Render(TerrainData terrainData, Camera camera, bool renderToAllCameras
#if UNITY_EDITOR
            , TerrainRenderer renderer = null
#endif
        )
        {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            if (terrainData == null)
                throw new ArgumentNullException(nameof(terrainData));

            if (camera == null)
                throw new ArgumentNullException(nameof(camera));
#endif

            float3 cameraPosition = camera.transform.position;

            if (cameraPosition.y < 0f)
                return;

            Bounds bounds = GetBounds(terrainData);

            var clipPlanes = new NativeArray<ClipPlane>(6, Allocator.TempJob,
                NativeArrayOptions.UninitializedMemory);

            var worldToProjectionMatrix = camera.projectionMatrix * camera.worldToCameraMatrix;

            float4 row0 = worldToProjectionMatrix.GetRow(0);
            float4 row1 = worldToProjectionMatrix.GetRow(1);
            float4 row2 = worldToProjectionMatrix.GetRow(2);
            float4 row3 = worldToProjectionMatrix.GetRow(3);

            clipPlanes[0] = new ClipPlane(row3 + row0);
            clipPlanes[1] = new ClipPlane(row3 - row0);
            clipPlanes[2] = new ClipPlane(row3 + row1);
            clipPlanes[3] = new ClipPlane(row3 - row1);
            clipPlanes[4] = new ClipPlane(row3 + row2);
            clipPlanes[5] = new ClipPlane(row3 - row2);

            var projectionToWorldMatrix = worldToProjectionMatrix.inverse;
            float3 clipCorner0 = projectionToWorldMatrix.MultiplyPoint(CLIP_CORNERS[0]);
            MinMaxAABB clipBounds = new MinMaxAABB(clipCorner0, clipCorner0);

            for (int i = 1; i < CLIP_CORNERS.Length; i++)
                clipBounds.Encapsulate(projectionToWorldMatrix.MultiplyPoint(CLIP_CORNERS[i]));

            if (!OverlapsClipVolume(bounds.ToMinMaxAABB(), clipBounds, clipPlanes))
            {
                clipPlanes.Dispose();
                return;
            }

            Profiler.BeginSample("RenderTerrain");

            bool renderGround = terrainData.RenderGround && terrainData.GroundMaterial != null
                                                         && terrainData.GroundMeshes.Length == 3;

            JobHandle generateGround;
            NativeArray<int> groundInstanceCounts;
            NativeList<Matrix4x4> groundTransforms;

            if (renderGround)
            {
                if (!magicPattern.IsCreated)
                    magicPattern = CreateMagicPattern();

                groundInstanceCounts = new NativeArray<int>(terrainData.GroundMeshes.Length,
                    Allocator.TempJob);

                groundTransforms = new NativeList<Matrix4x4>(terrainData.GroundInstanceCapacity,
                    Allocator.TempJob);

                var generateGroundJob = new GenerateGroundJob()
                {
                    terrainData = terrainData.GetData(),
                    cameraPosition = cameraPosition,
                    clipBounds = clipBounds,
                    clipPlanes = clipPlanes,
                    magicPattern = magicPattern,
                    instanceCounts = groundInstanceCounts,
                    transforms = groundTransforms
                };

                generateGround = generateGroundJob.Schedule();
            }
            else
            {
                generateGround = default;
                groundInstanceCounts = default;
                groundTransforms = default;
            }

            bool renderTreesAndDetail = terrainData.RenderTreesAndDetail
                                        && (terrainData.TreePrototypes.Length > 0
                                            || terrainData.DetailPrototypes.Length > 0);

            JobHandle scatterObjects;
            NativeList<TreeInstance> treeInstances;
            NativeArray<int> treeInstanceCounts;
            NativeList<DetailInstance> detailInstances;
            NativeArray<int> detailInstanceCounts;
            JobHandle prepareTreeRenderList;
            NativeList<Matrix4x4> treeTransforms;
            JobHandle prepareDetailRenderList;
            NativeList<Matrix4x4> detailTransforms;

            if (renderTreesAndDetail)
            {
                // Deallocated by ScatterObjectsJob
                var treePrototypes = new NativeArray<TreePrototypeData>(
                    terrainData.TreePrototypes.Length, Allocator.TempJob,
                    NativeArrayOptions.UninitializedMemory);

                int treeMeshCount = 0;

                for (int prototypeIndex = 0; prototypeIndex < treePrototypes.Length; prototypeIndex++)
                {
                    TreePrototype prototype = terrainData.TreePrototypes[prototypeIndex];

                    treePrototypes[prototypeIndex] = new TreePrototypeData()
                    {
                        localSize = prototype.LocalSize,
                        lod0MeshIndex = treeMeshCount
                    };

                    treeMeshCount += prototype.Lods.Length;
                }

                // Deallocated by ScatterObjectsJob
                var treeLods = new NativeArray<TreeLODData>(treeMeshCount, Allocator.TempJob,
                    NativeArrayOptions.UninitializedMemory);

                for (int prototypeIndex = 0; prototypeIndex < treePrototypes.Length; prototypeIndex++)
                {
                    TreeLOD[] lods = terrainData.TreePrototypes[prototypeIndex].Lods;
                    int lod0MeshIndex = treePrototypes[prototypeIndex].lod0MeshIndex;

                    for (int lodIndex = 0; lodIndex < lods.Length; lodIndex++)
                    {
                        treeLods[lod0MeshIndex + lodIndex] = new TreeLODData()
                        {
                            minScreenSize = lods[lodIndex].MinScreenSize
                        };
                    }
                }

                // Deallocated by ScatterObjectsJob
                var detailPrototypes = new NativeArray<DetailPrototypeData>(
                    terrainData.DetailPrototypes.Length, Allocator.TempJob,
                    NativeArrayOptions.UninitializedMemory);

                for (int prototypeIndex = 0; prototypeIndex < detailPrototypes.Length; prototypeIndex++)
                {
                    DetailPrototype prototype = terrainData.DetailPrototypes[prototypeIndex];

                    detailPrototypes[prototypeIndex] = new DetailPrototypeData()
                    {
                        scatterMode = prototype.ScatterMode,
                        density = prototype.Density,
                        minScaleXZ = prototype.MinScaleXZ,
                        maxScaleXZ = prototype.MaxScaleXZ,
                        minScaleY = prototype.MinScaleY,
                        maxScaleY = prototype.MaxScaleY
                    };
                }

                treeInstances = new NativeList<TreeInstance>(terrainData.TreeInstanceCapacity,
                    Allocator.TempJob);

                detailInstances = new NativeList<DetailInstance>(terrainData.DetailInstanceCapacity,
                    Allocator.TempJob);

                var scatterObjectsJob = new ScatterObjectsJob()
                {
                    terrainData = terrainData.GetData(),
                    detailSqrDistance = terrainData.DetailDistance * terrainData.DetailDistance,
                    cameraPosition = cameraPosition,
                    clipBounds = clipBounds,
                    clipPlanes = clipPlanes,
                    treePrototypes = treePrototypes,
                    treeLods = treeLods,
                    detailPrototypes = detailPrototypes,
                    treeInstances = treeInstances.AsParallelWriter(),
                    detailInstances = detailInstances.AsParallelWriter()
                };

                Vector2Int terrainSize = terrainData.Bounds.size;
                int parcelCount = terrainSize.x * terrainSize.y;

                scatterObjects = scatterObjectsJob.Schedule(parcelCount,
                    JobUtility.GetBatchSize(parcelCount));

                treeInstanceCounts = new NativeArray<int>(treeMeshCount, Allocator.TempJob);
                treeTransforms = new NativeList<Matrix4x4>(Allocator.TempJob);

                var prepareTreeRenderListJob = new PrepareTreeRenderListJob()
                {
                    instances = treeInstances,
                    instanceCounts = treeInstanceCounts,
                    transforms = treeTransforms
                };

                prepareTreeRenderList = prepareTreeRenderListJob.Schedule(scatterObjects);

                detailInstanceCounts = new NativeArray<int>(detailPrototypes.Length,
                    Allocator.TempJob);

                detailTransforms = new NativeList<Matrix4x4>(Allocator.TempJob);

                var prepareDetailRenderListJob = new PrepareDetailRenderListJob()
                {
                    instances = detailInstances,
                    instanceCounts = detailInstanceCounts,
                    transforms = detailTransforms
                };

                prepareDetailRenderList = prepareDetailRenderListJob.Schedule(scatterObjects);
            }
            else
            {
                scatterObjects = default;
                treeInstances = default;
                treeInstanceCounts = default;
                detailInstances = default;
                detailInstanceCounts = default;
                prepareTreeRenderList = default;
                treeTransforms = default;
                prepareDetailRenderList = default;
                detailTransforms = default;
            }

            var renderParams = new RenderParams()
            {
                layer = 1, // Default
                receiveShadows = true,
                renderingLayerMask = RenderingLayerMask.defaultRenderingLayerMask,
                worldBounds = bounds,
            };

            if (!renderToAllCameras)
                renderParams.camera = camera;

            if (renderGround)
            {
                generateGround.Complete();

                if (groundTransforms.Length > terrainData.GroundInstanceCapacity)
                {
                    terrainData.GroundInstanceCapacity
                        = (int)ceil(terrainData.GroundInstanceCapacity * 1.1f);

                    Debug.LogWarning(
                        $"The {nameof(groundTransforms)} list ran out of space. Increasing capacity to {terrainData.GroundInstanceCapacity}.",
                        terrainData);
                }

#if UNITY_EDITOR
                if (renderer != null)
                    renderer.GroundInstanceCount = groundTransforms.Length;
#endif

                RenderGround(terrainData, renderParams, groundTransforms.AsArray(),
                    groundInstanceCounts);

                groundInstanceCounts.Dispose();
                groundTransforms.Dispose();
            }

            if (renderTreesAndDetail)
            {
                scatterObjects.Complete();
                clipPlanes.Dispose();

                prepareTreeRenderList.Complete();

                if (treeInstances.Length > treeInstances.Capacity)
                {
                    terrainData.TreeInstanceCapacity
                        = (int)ceil(terrainData.TreeInstanceCapacity * 1.1f);

                    Debug.LogWarning(
                        $"The {nameof(treeInstances)} list ran out of space. Increasing capacity to {terrainData.TreeInstanceCapacity}.",
                        terrainData);
                }

#if UNITY_EDITOR
                if (renderer != null)
                    renderer.TreeInstanceCount = treeInstances.Length;
#endif

                treeInstances.Dispose();
                RenderTrees(terrainData, renderParams, treeTransforms.AsArray(), treeInstanceCounts);
                treeInstanceCounts.Dispose();
                treeTransforms.Dispose();

                prepareDetailRenderList.Complete();

                if (detailInstances.Length > detailInstances.Capacity)
                {
                    terrainData.DetailInstanceCapacity
                        = (int)ceil(terrainData.DetailInstanceCapacity * 1.1f);

                    Debug.LogWarning(
                        $"The {nameof(detailInstances)} list ran out of space. Increasing capacity to {terrainData.DetailInstanceCapacity}.",
                        terrainData);
                }

#if UNITY_EDITOR
                if (renderer != null)
                    renderer.DetailInstanceCount = detailInstances.Length;
#endif

                detailInstances.Dispose();

                RenderDetail(terrainData, renderParams, detailTransforms.AsArray(),
                    detailInstanceCounts);

                detailInstanceCounts.Dispose();
                detailTransforms.Dispose();
            }

            Profiler.EndSample();
        }

        private static void RenderDetail(TerrainData terrainData, RenderParams renderParams,
            NativeArray<Matrix4x4> instanceData, NativeArray<int> instanceCounts)
        {
            if (instanceData.Length == 0)
                return;

            DetailPrototype[] prototypes = terrainData.DetailPrototypes;
            int startInstance = 0;
            renderParams.matProps = null;
            renderParams.shadowCastingMode = ShadowCastingMode.Off;

            for (int prototypeIndex = 0; prototypeIndex < prototypes.Length; prototypeIndex++)
            {
                // Since details do not have LODs, prototypeIndex is the same as meshIndex.
                int instanceCount = instanceCounts[prototypeIndex];

                if (instanceCount == 0)
                    continue;

                DetailPrototype prototype = prototypes[prototypeIndex];
                renderParams.material = prototype.Material;

                Graphics.RenderMeshInstanced(renderParams, prototype.Mesh, 0, instanceData,
                    instanceCount, startInstance);

                startInstance += instanceCount;
            }
        }

        private static void RenderGround(TerrainData terrainData, RenderParams renderParams,
            NativeArray<Matrix4x4> instanceData, NativeArray<int> instanceCounts)
        {
            if (instanceData.Length == 0)
                return;

            Vector4 bounds = new Vector4(
                terrainData.Bounds.x, terrainData.Bounds.x + terrainData.Bounds.width,
                terrainData.Bounds.y, terrainData.Bounds.y + terrainData.Bounds.height);

            if (groundMatProps == null)
                groundMatProps = new MaterialPropertyBlock();

            groundMatProps.SetFloat(PARCEL_SIZE_ID, terrainData.ParcelSize);
            groundMatProps.SetVector(TERRAIN_BOUNDS_ID, bounds * terrainData.ParcelSize);

            int startInstance = 0;
            renderParams.material = terrainData.GroundMaterial;
            renderParams.matProps = groundMatProps;
            renderParams.shadowCastingMode = ShadowCastingMode.On;

            for (int meshIndex = 0; meshIndex < terrainData.GroundMeshes.Length; meshIndex++)
            {
                int instanceCount = instanceCounts[meshIndex];

                if (instanceCount == 0)
                    continue;

                Graphics.RenderMeshInstanced(renderParams, terrainData.GroundMeshes[meshIndex], 0,
                    instanceData, instanceCount, startInstance);

                startInstance += instanceCount;
            }
        }

        private static void RenderTrees(TerrainData terrainData, RenderParams renderParams,
            NativeArray<Matrix4x4> instanceData, NativeArray<int> instanceCounts)
        {
            if (instanceData.Length == 0)
                return;

            TreePrototype[] prototypes = terrainData.TreePrototypes;
            int meshIndex = 0;
            int startInstance = 0;
            renderParams.matProps = null;
            renderParams.shadowCastingMode = ShadowCastingMode.On;

            for (int prototypeIndex = 0; prototypeIndex < prototypes.Length; prototypeIndex++)
            {
                TreeLOD[] lods = prototypes[prototypeIndex].Lods;

                for (int lodIndex = 0; lodIndex < lods.Length; lodIndex++)
                {
                    // When you concatenate the LOD lists of all the prototypes, you get the list
                    // meshIndex indexes into.
                    int instanceCount = instanceCounts[meshIndex];
                    meshIndex++;

                    if (instanceCount == 0)
                        continue;

                    TreeLOD lod = lods[lodIndex];

                    for (int subMeshIndex = 0; subMeshIndex < lod.Materials.Length; subMeshIndex++)
                    {
                        renderParams.material = lod.Materials[subMeshIndex];

                        Graphics.RenderMeshInstanced(renderParams, lod.Mesh, subMeshIndex, instanceData,
                            instanceCount, startInstance);
                    }

                    startInstance += instanceCount;
                }
            }
        }

        [BurstCompile]
        private struct GenerateGroundJob : IJob
        {
            public TerrainDataData terrainData;
            public float3 cameraPosition;
            public MinMaxAABB clipBounds;
            [ReadOnly] public NativeArray<ClipPlane> clipPlanes;
            [ReadOnly] public NativeArray<int4> magicPattern;
            public NativeArray<int> instanceCounts;
            public NativeList<Matrix4x4> transforms;

            public void Execute()
            {
                int2 origin = (PositionToParcel(cameraPosition) + 1) & ~1;
                int scale = (int)(cameraPosition.y / terrainData.parcelSize) + 1;
                var instances = new NativeList<GroundInstance>(transforms.Length, Allocator.Temp);

                for (int i = 0; i < 4; i++)
                    TryGenerateGround(origin, magicPattern[i], scale, instances);

                while (true)
                {
                    bool stop = true;

                    for (int i = 4; i < 16; i++)
                        if (TryGenerateGround(origin, magicPattern[i], scale, instances))
                            stop = false;

                    if (stop || scale >= int.MaxValue / 2)
                        break;

                    scale *= 2;
                }

                instances.Sort();

                if (transforms.Capacity < instances.Length)
                    transforms.Capacity = instances.Length;

                int instanceCount = 0;
                int meshIndex = 0;

                for (int instanceIndex = 0; instanceIndex < instances.Length; instanceIndex++)
                {
                    GroundInstance instance = instances[instanceIndex];

                    if (meshIndex < instance.meshIndex)
                    {
                        instanceCounts[meshIndex] = instanceCount;
                        meshIndex = instance.meshIndex;
                        instanceCount = 0;
                    }

                    instanceCount++;

                    transforms.AddNoResize(Matrix4x4.TRS(
                        new Vector3(instance.positionXZ.x, 0f, instance.positionXZ.y),
                        Quaternion.Euler(0f, instance.rotationY, 0f),
                        new Vector3(instance.scale, instance.scale, instance.scale)));
                }

                instanceCounts[meshIndex] = instanceCount;
                instances.Dispose();
            }

            private int2 PositionToParcel(float3 value)
            {
                return (int2)floor(value.xz * (1f / terrainData.parcelSize));
            }

            private bool TryGenerateGround(int2 origin, int4 magic, int scale,
                NativeList<GroundInstance> instances)
            {
                int2 min = origin + magic.xy * scale;
                int2 max = min + scale;
                int parcelSize = terrainData.parcelSize;

                var bounds = new MinMaxAABB(float3(min.x * parcelSize, 0f, min.y * parcelSize),
                    float3(max.x * parcelSize, terrainData.maxHeight, max.y * parcelSize));

                if (!OverlapsClipVolume(bounds, clipBounds, clipPlanes))
                    return false;

                if (!terrainData.bounds.Overlaps(new RectInt(min.x, min.y, scale, scale)))
                    // Skip this instance, but keep generating. The case to consider is when the camera
                    // is far outside the bounds of the terrain.
                    return true;

                instances.Add(new GroundInstance()
                {
                    meshIndex = magic.z,
                    positionXZ = bounds.Center.xz,
                    rotationY = magic.w,
                    scale = scale
                });

                return true;
            }
        }

        [BurstCompile]
        private struct PrepareDetailRenderListJob : IJob
        {
            public NativeList<DetailInstance> instances;
            [WriteOnly] public NativeArray<int> instanceCounts;
            public NativeList<Matrix4x4> transforms;

            public void Execute()
            {
                // If NativeList<T>.ParallelWriter runs out of space, the length of the list will exceed
                // its capacity. This code deals with that.
                int totalInstanceCount = min(instances.Length, instances.Capacity);

                if (totalInstanceCount == 0)
                    return;

                instances.AsArray().GetSubArray(0, totalInstanceCount).Sort();
                transforms.Capacity = totalInstanceCount;

                int instanceCount = 0;
                int meshIndex = 0;

                for (int instanceIndex = 0; instanceIndex < totalInstanceCount; instanceIndex++)
                {
                    DetailInstance instance = instances[instanceIndex];

                    if (meshIndex < instance.meshIndex)
                    {
                        instanceCounts[meshIndex] = instanceCount;
                        meshIndex = instance.meshIndex;
                        instanceCount = 0;
                    }

                    instanceCount++;

                    transforms.AddNoResize(Matrix4x4.TRS(instance.position,
                        Quaternion.Euler(0f, instance.rotationY, 0f),
                        new Vector3(instance.scaleXZ, instance.scaleY, instance.scaleXZ)));
                }

                instanceCounts[meshIndex] = instanceCount;
            }
        }

        [BurstCompile]
        private struct PrepareTreeRenderListJob : IJob
        {
            public NativeList<TreeInstance> instances;
            [WriteOnly] public NativeArray<int> instanceCounts;
            public NativeList<Matrix4x4> transforms;

            public void Execute()
            {
                // If NativeList<T>.ParallelWriter runs out of space, the length of the list will exceed
                // its capacity. This code deals with that.
                int totalInstanceCount = min(instances.Length, instances.Capacity);

                if (totalInstanceCount == 0)
                    return;

                instances.AsArray().GetSubArray(0, totalInstanceCount).Sort();
                transforms.Capacity = totalInstanceCount;

                int instanceCount = 0;
                int meshIndex = 0;

                for (int instanceIndex = 0; instanceIndex < instances.Length; instanceIndex++)
                {
                    TreeInstance instance = instances[instanceIndex];

                    if (meshIndex < instance.meshIndex)
                    {
                        instanceCounts[meshIndex] = instanceCount;
                        meshIndex = instance.meshIndex;
                        instanceCount = 0;
                    }

                    instanceCount++;

                    transforms.AddNoResize(Matrix4x4.TRS(instance.position,
                        Quaternion.Euler(0f, instance.rotationY, 0f),
                        Vector3.one));
                }

                instanceCounts[meshIndex] = instanceCount;
            }
        }

        [BurstCompile]
        private struct ScatterObjectsJob : IJobParallelFor
        {
            public TerrainDataData terrainData;
            public float detailSqrDistance;
            public float3 cameraPosition;
            public MinMaxAABB clipBounds;
            [ReadOnly] public NativeArray<ClipPlane> clipPlanes;
            [ReadOnly, DeallocateOnJobCompletion] public NativeArray<TreePrototypeData> treePrototypes;
            [ReadOnly, DeallocateOnJobCompletion] public NativeArray<TreeLODData> treeLods;
            [ReadOnly, DeallocateOnJobCompletion] public NativeArray<DetailPrototypeData> detailPrototypes;
            public NativeList<TreeInstance>.ParallelWriter treeInstances;
            public NativeList<DetailInstance>.ParallelWriter detailInstances;

            public void Execute(int index)
            {
                int2 parcel = int2(
                    index % terrainData.bounds.width + terrainData.bounds.x,
                    index / terrainData.bounds.width + terrainData.bounds.y);

                if (terrainData.IsOccupied(parcel))
                    return;

                int2 min = parcel * terrainData.parcelSize;
                int2 max = min + terrainData.parcelSize;

                var bounds = new MinMaxAABB(float3(min.x, 0f, min.y),
                    float3(max.x, terrainData.maxHeight, max.y));

                if (!OverlapsClipVolume(bounds, clipBounds, clipPlanes))
                    return;

                Random random = terrainData.GetRandom(parcel);

                // Tree scattering

                if (terrainData.NextTree(parcel, ref random, out float3 position, out float rotationY,
                        out int prototypeIndex0))
                {
                    int prototypeIndex = prototypeIndex0;
                    TreePrototypeData prototype = treePrototypes[prototypeIndex];
                    float screenSize = prototype.localSize / distance(position, cameraPosition);
                    int meshIndex = prototype.lod0MeshIndex;

                    int meshEnd = prototypeIndex + 1 < treePrototypes.Length
                        ? treePrototypes[prototypeIndex + 1].lod0MeshIndex
                        : treeLods.Length;

                    while (meshIndex < meshEnd && treeLods[meshIndex].minScreenSize > screenSize)
                        meshIndex++;

                    if (meshIndex < meshEnd)
                    {
                        treeInstances.TryAddNoResize(new TreeInstance()
                        {
                            meshIndex = meshIndex,
                            position = position,
                            rotationY = rotationY
                        });
                    }
                }

                // Detail scattering

                if (distancesq(bounds.Center, cameraPosition) < detailSqrDistance)
                {
                    for (int prototypeIndex = 0; prototypeIndex < detailPrototypes.Length; prototypeIndex++)
                    {
                        DetailPrototypeData prototype = detailPrototypes[prototypeIndex];

                        switch (prototype.scatterMode)
                        {
                            case DetailScatterMode.JitteredGrid:
                                if (!JitteredGrid(parcel, prototypeIndex, ref random, detailInstances))
                                    goto outOfSpace;

                                break;
                        }
                    }

                    outOfSpace: ;
                }
            }

            private bool JitteredGrid(int2 parcel, int meshIndex,
                ref Random random, NativeList<DetailInstance>.ParallelWriter instances)
            {
                DetailPrototypeData prototype = detailPrototypes[meshIndex];
                int gridSize = (int)(prototype.density * terrainData.parcelSize);
                float invGridSize = (float)terrainData.parcelSize / gridSize;
                float2 corner0 = parcel * terrainData.parcelSize;

                for (int z = 0; z < gridSize; z++)
                for (int x = 0; x < gridSize; x++)
                {
                    float3 position;
                    position.x = corner0.x + x * invGridSize + random.NextFloat(invGridSize);
                    position.z = corner0.y + z * invGridSize + random.NextFloat(invGridSize);
                    position.y = terrainData.GetHeight(position.x, position.z);

                    float rotationY = random.NextFloat(-180f, 180f);

                    float scaleXZ = random.NextFloat(prototype.minScaleXZ, prototype.maxScaleXZ);
                    float scaleY = random.NextFloat(prototype.minScaleY, prototype.maxScaleY);

                    DetailInstance instance = new DetailInstance()
                    {
                        meshIndex = meshIndex,
                        position = position,
                        rotationY = rotationY,
                        scaleXZ = scaleXZ,
                        scaleY = scaleY
                    };

                    if (!instances.TryAddNoResize(instance))
                        return false;
                }

                return true;
            }
        }

        private struct ClipPlane
        {
            public Plane plane;
            public int farCornerIndex;

            public ClipPlane(float4 coefficients)
            {
                plane = new Plane() { NormalAndDistance = Plane.Normalize(coefficients) };
                if (plane.NormalAndDistance.x < 0f)
                {
                    if (plane.NormalAndDistance.y < 0f)
                        farCornerIndex = plane.NormalAndDistance.z < 0f ? 0b000 : 0b001;
                    else
                        farCornerIndex = plane.NormalAndDistance.z < 0f ? 0b010 : 0b011;
                }
                else
                {
                    if (plane.NormalAndDistance.y < 0f)
                        farCornerIndex = plane.NormalAndDistance.z < 0f ? 0b100 : 0b101;
                    else
                        farCornerIndex = plane.NormalAndDistance.z < 0f ? 0b110 : 0b111;
                }
            }
        }

        private struct DetailInstance : IComparable<DetailInstance>
        {
            public int meshIndex;
            public float3 position;
            public float rotationY;
            public float scaleXZ;
            public float scaleY;

            public int CompareTo(DetailInstance other) => meshIndex - other.meshIndex;
        }

        private struct DetailPrototypeData
        {
            public DetailScatterMode scatterMode;
            public float density;
            public float minScaleXZ;
            public float maxScaleXZ;
            public float minScaleY;
            public float maxScaleY;
        }

        private struct GroundInstance : IComparable<GroundInstance>
        {
            public int meshIndex;
            public float2 positionXZ;
            public float rotationY;
            public float scale;

            public int CompareTo(GroundInstance other) => meshIndex - other.meshIndex;
        }

        private struct TreeInstance : IComparable<TreeInstance>
        {
            public int meshIndex;
            public float3 position;
            public float rotationY;

            public int CompareTo(TreeInstance other) => meshIndex - other.meshIndex;
        }

        private struct TreeLODData
        {
            public float minScreenSize;
        }

        private struct TreePrototypeData
        {
            public float localSize;
            public int lod0MeshIndex;
        }
    }
}
