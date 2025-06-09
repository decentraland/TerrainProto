using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using Unity.Mathematics.Geometry;
using UnityEditor;
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
        [SerializeField] private TerrainData terrainData;
        [SerializeField] private Material material;
        [SerializeField] private float detailDistance = 200;
        [SerializeField] private bool renderTreesAndDetail = true;
        [SerializeField] private Mesh[] groundMeshes;

        private Bounds bounds;
        private NativeArray<int4> magicPattern;

        // Increase these numbers if you see the terrain renderer complain about them in the console.
        private int groundInstanceCapacity = 80;
        private int treeInstanceCapacity = 3000;
        private int detailInstanceCapacity = 40000;

        private readonly Vector3[] clipCorners =
        {
            new(-1f, -1f, 0f), new(-1f, -1f, 1f),
            new(-1f, 1f, 0f), new(-1f, 1f, 1f), new(1f, -1f, 0f), new(1f, -1f, 1f), new(1f, 1f, 0f),
            new(1f, 1f, 1f)
        };

#if UNITY_EDITOR
        public int DetailInstanceCount { get; private set; }
        public int GroundInstanceCount { get; private set; }
        public int TreeInstanceCount { get; private set; }
#endif

        private void OnValidate()
        {
            if (terrainData == null)
                return;

            int halfSideLength = terrainData.parcelSize * terrainData.terrainSize / 2;
            Vector3 center = new Vector3(0f, terrainData.maxHeight * 0.5f, 0f);
            bounds = new Bounds(center, new Vector3(halfSideLength, center.y, halfSideLength));

            if (!magicPattern.IsCreated)
                magicPattern = CreateMagicPattern();
        }

        private void OnEnable()
        {
            RenderPipelineManager.beginContextRendering += Render;
        }

        private void OnDisable()
        {
            RenderPipelineManager.beginContextRendering -= Render;
        }

        private void OnDestroy()
        {
            if (magicPattern.IsCreated)
                magicPattern.Dispose();
        }

        private static NativeArray<int4> CreateMagicPattern()
        {
            var magicPattern = new NativeArray<int4>(16, Allocator.Persistent,
                NativeArrayOptions.UninitializedMemory);

            // x, y are coordinates, z is the mesh to use (0 normal, 1 low res edge piece, 2 low res
            // corner piece), w is the rotation (0 is corner piece top right or edge piece top, rotate
            // counter-clockwise)

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

        private void Render(ScriptableRenderContext context, List<Camera> cameras)
        {
            if (terrainData == null)
                return;

            Camera camera;

#if UNITY_EDITOR
            if (!Application.isPlaying)
            {
                SceneView sceneView = SceneView.lastActiveSceneView;
                camera = sceneView != null ? sceneView.camera : Camera.main;
            }
            else
#endif
            {
                camera = Camera.main;
            }

            if (camera == null)
                return;

            float3 cameraPosition = camera.transform.position;

            if (cameraPosition.y < 0f)
                return;

            int parcelSize = terrainData.parcelSize;

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
            float3 clipCorner0 = projectionToWorldMatrix.MultiplyPoint(clipCorners[0]);
            MinMaxAABB clipBounds = new MinMaxAABB(clipCorner0, clipCorner0);

            for (int i = 1; i < clipCorners.Length; i++)
                clipBounds.Encapsulate(projectionToWorldMatrix.MultiplyPoint(clipCorners[i]));

            if (!OverlapsClipVolume(bounds.ToMinMaxAABB(), clipBounds, clipPlanes))
            {
                clipPlanes.Dispose();
                return;
            }

            Profiler.BeginSample("RenderTerrain");

            var groundInstanceCounts = new NativeArray<int>(groundMeshes.Length, Allocator.TempJob);
            var groundTransforms = new NativeList<Matrix4x4>(groundInstanceCapacity, Allocator.TempJob);

            var generateGroundJob = new GenerateGroundJob()
            {
                parcelSize = parcelSize,
                terrainSize = terrainData.terrainSize,
                maxHeight = terrainData.maxHeight,
                cameraPosition = cameraPosition,
                clipBounds = clipBounds,
                clipPlanes = clipPlanes,
                magicPattern = magicPattern,
                instanceCounts = groundInstanceCounts,
                transforms = groundTransforms
            };

            JobHandle generateGround = generateGroundJob.Schedule();

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
                    terrainData.treePrototypes.Length, Allocator.TempJob,
                    NativeArrayOptions.UninitializedMemory);

                int treeMeshCount = 0;

                for (int prototypeIndex = 0; prototypeIndex < treePrototypes.Length; prototypeIndex++)
                {
                    TreePrototype prototype = terrainData.treePrototypes[prototypeIndex];

                    treePrototypes[prototypeIndex] = new TreePrototypeData()
                    {
                        localSize = prototype.localSize,
                        lod0MeshIndex = treeMeshCount
                    };

                    treeMeshCount += prototype.lods.Length;
                }

                // Deallocated by ScatterObjectsJob
                var treeLods = new NativeArray<TreeLODData>(treeMeshCount, Allocator.TempJob,
                    NativeArrayOptions.UninitializedMemory);

                for (int prototypeIndex = 0; prototypeIndex < treePrototypes.Length; prototypeIndex++)
                {
                    TreeLOD[] lods = terrainData.treePrototypes[prototypeIndex].lods;
                    int lod0MeshIndex = treePrototypes[prototypeIndex].lod0MeshIndex;

                    for (int lodIndex = 0; lodIndex < lods.Length; lodIndex++)
                    {
                        treeLods[lod0MeshIndex + lodIndex] = new TreeLODData()
                        {
                            minScreenSize = lods[lodIndex].minScreenSize
                        };
                    }
                }

                // Deallocated by ScatterObjectsJob
                var detailPrototypes = new NativeArray<DetailPrototypeData>(
                    terrainData.detailPrototypes.Length, Allocator.TempJob,
                    NativeArrayOptions.UninitializedMemory);

                for (int prototypeIndex = 0; prototypeIndex < detailPrototypes.Length; prototypeIndex++)
                {
                    DetailPrototype prototype = terrainData.detailPrototypes[prototypeIndex];

                    detailPrototypes[prototypeIndex] = new DetailPrototypeData()
                    {
                        scatterMode = prototype.scatterMode,
                        density = prototype.density,
                        minScaleXZ = prototype.minScaleXZ,
                        maxScaleXZ = prototype.maxScaleXZ,
                        minScaleY = prototype.minScaleY,
                        maxScaleY = prototype.maxScaleY
                    };
                }

                treeInstances = new NativeList<TreeInstance>(treeInstanceCapacity,
                    Allocator.TempJob);

                detailInstances = new NativeList<DetailInstance>(detailInstanceCapacity,
                    Allocator.TempJob);

                var scatterObjectsJob = new ScatterObjectsJob()
                {
                    terrainData = terrainData.GetData(),
                    detailSqrDistance = detailDistance * detailDistance,
                    cameraPosition = cameraPosition,
                    clipBounds = clipBounds,
                    clipPlanes = clipPlanes,
                    treeDensity = terrainData.treeDensity,
                    treePrototypes = treePrototypes,
                    treeLods = treeLods,
                    detailPrototypes = detailPrototypes,
                    treeInstances = treeInstances.AsParallelWriter(),
                    detailInstances = detailInstances.AsParallelWriter()
                };

                int parcelCount = terrainData.terrainSize * terrainData.terrainSize;

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
                instanceID = GetInstanceID(),
                layer = gameObject.layer,
                receiveShadows = true,
                renderingLayerMask = RenderingLayerMask.defaultRenderingLayerMask,
                worldBounds = bounds,
#if !UNITY_EDITOR
                camera = camera,
#endif
            };

            generateGround.Complete();

            if (groundTransforms.Length > groundTransforms.Capacity)
            {
                groundInstanceCapacity = (int)ceil(groundInstanceCapacity * 1.1f);

                Debug.LogWarning(
                    $"The {nameof(groundTransforms)} list ran out of space. Increasing capacity to {groundInstanceCapacity}.",
                    this);
            }

#if UNITY_EDITOR
            GroundInstanceCount = groundTransforms.Length;
#endif

            RenderGround(renderParams, groundTransforms.AsArray()
                .GetSubArray(0, min(groundTransforms.Length, groundTransforms.Capacity)),
                groundInstanceCounts);

            groundInstanceCounts.Dispose();
            groundTransforms.Dispose();

            if (renderTreesAndDetail)
            {
                scatterObjects.Complete();
                clipPlanes.Dispose();

                prepareTreeRenderList.Complete();

                if (treeInstances.Length > treeInstances.Capacity)
                {
                    treeInstanceCapacity = (int)ceil(treeInstanceCapacity * 1.1f);

                    Debug.LogWarning(
                        $"The {nameof(treeInstances)} list ran out of space. Increasing capacity to {treeInstanceCapacity}.",
                        this);
                }

#if UNITY_EDITOR
                TreeInstanceCount = treeInstances.Length;
#endif

                treeInstances.Dispose();
                RenderTrees(renderParams, treeTransforms.AsArray(), treeInstanceCounts);
                treeInstanceCounts.Dispose();
                treeTransforms.Dispose();

                prepareDetailRenderList.Complete();

                if (detailInstances.Length > detailInstances.Capacity)
                {
                    detailInstanceCapacity = (int)ceil(detailInstanceCapacity * 1.1f);

                    Debug.LogWarning(
                        $"The {nameof(detailInstances)} list ran out of space. Increasing capacity to {detailInstanceCapacity}.",
                        this);
                }

#if UNITY_EDITOR
                DetailInstanceCount = detailInstances.Length;
#endif

                detailInstances.Dispose();
                RenderDetails(renderParams, detailTransforms.AsArray(), detailInstanceCounts);
                detailInstanceCounts.Dispose();
                detailTransforms.Dispose();
            }

            Profiler.EndSample();
        }

        private void RenderDetails(RenderParams renderParams, NativeArray<Matrix4x4> instanceData,
            NativeArray<int> instanceCounts)
        {
            if (instanceData.Length == 0)
                return;

            DetailPrototype[] prototypes = terrainData.detailPrototypes;
            int startInstance = 0;
            renderParams.shadowCastingMode = ShadowCastingMode.Off;

            for (int prototypeIndex = 0; prototypeIndex < prototypes.Length; prototypeIndex++)
            {
                // Since details do not have LODs, prototypeIndex is the same as meshIndex.
                int instanceCount = instanceCounts[prototypeIndex];

                if (instanceCount == 0)
                    continue;

                DetailPrototype prototype = prototypes[prototypeIndex];
                renderParams.material = prototype.material;

                Graphics.RenderMeshInstanced(renderParams, prototype.mesh, 0, instanceData,
                    instanceCount, startInstance);

                startInstance += instanceCount;
            }
        }

        private void RenderGround(RenderParams renderParams, NativeArray<Matrix4x4> instanceData,
            NativeArray<int> instanceCounts)
        {
            if (instanceData.Length == 0)
                return;

            int startInstance = 0;
            renderParams.material = material;
            renderParams.shadowCastingMode = ShadowCastingMode.On;

            for (int meshIndex = 0; meshIndex < groundMeshes.Length; meshIndex++)
            {
                int instanceCount = instanceCounts[meshIndex];

                if (instanceCount == 0)
                    continue;

                Graphics.RenderMeshInstanced(renderParams, groundMeshes[meshIndex], 0, instanceData,
                    instanceCount, startInstance);

                startInstance += instanceCount;
            }
        }

        private void RenderTrees(RenderParams renderParams, NativeArray<Matrix4x4> instanceData,
            NativeArray<int> instanceCounts)
        {
            if (instanceData.Length == 0)
                return;

            TreePrototype[] prototypes = terrainData.treePrototypes;
            int meshIndex = 0;
            int startInstance = 0;
            renderParams.shadowCastingMode = ShadowCastingMode.On;

            for (int prototypeIndex = 0; prototypeIndex < prototypes.Length; prototypeIndex++)
            {
                TreeLOD[] lods = prototypes[prototypeIndex].lods;

                for (int lodIndex = 0; lodIndex < lods.Length; lodIndex++)
                {
                    // When you concatenate the LOD lists of all the prototypes, you get the list
                    // meshIndex indexes into.
                    int instanceCount = instanceCounts[meshIndex];
                    meshIndex++;

                    if (instanceCount == 0)
                        continue;

                    TreeLOD lod = lods[lodIndex];

                    for (int subMeshIndex = 0; subMeshIndex < lod.materials.Length; subMeshIndex++)
                    {
                        renderParams.material = lod.materials[subMeshIndex];

                        Graphics.RenderMeshInstanced(renderParams, lod.mesh, subMeshIndex, instanceData,
                            instanceCount, startInstance);
                    }

                    startInstance += instanceCount;
                }
            }
        }

        private struct GenerateGroundJob : IJob
        {
            public int parcelSize;
            public int terrainSize;
            public float maxHeight;
            public float3 cameraPosition;
            public MinMaxAABB clipBounds;
            [ReadOnly] public NativeArray<ClipPlane> clipPlanes;
            [ReadOnly] public NativeArray<int4> magicPattern;
            public NativeArray<int> instanceCounts;
            public NativeList<Matrix4x4> transforms;

            public void Execute()
            {
                int2 origin = (PositionToParcel(cameraPosition) + 1) & ~1;
                int scale = (int)(cameraPosition.y / parcelSize) + 1;
                var instances = new NativeList<GroundInstance>(transforms.Length, Allocator.Temp);

                for (int i = 0; i < 4; i++)
                    TryGenerateGround(origin, magicPattern[i], scale, instances);

                while (true)
                {
                    bool outOfBounds = true;

                    for (int i = 4; i < 16; i++)
                        if (TryGenerateGround(origin, magicPattern[i], scale, instances))
                            outOfBounds = false;

                    if (outOfBounds)
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
                return (int2)round(value.xz * (1f / parcelSize) - 0.5f);
            }

            private bool TryGenerateGround(int2 origin, int4 magic, int scale,
                NativeList<GroundInstance> instances)
            {
                int2 min = (origin + magic.xy * scale) * parcelSize;
                int2 max = min + scale * parcelSize;
                var bounds = new MinMaxAABB(float3(min.x, 0f, min.y), float3(max.x, maxHeight, max.y));

                if (!OverlapsClipVolume(bounds, clipBounds, clipPlanes))
                    return false;

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

        private struct ScatterObjectsJob : IJobParallelFor
        {
            public TerrainDataData terrainData;
            public float detailSqrDistance;
            public float3 cameraPosition;
            public MinMaxAABB clipBounds;
            [ReadOnly] public NativeArray<ClipPlane> clipPlanes;
            public float treeDensity;
            [ReadOnly, DeallocateOnJobCompletion] public NativeArray<TreePrototypeData> treePrototypes;
            [ReadOnly, DeallocateOnJobCompletion] public NativeArray<TreeLODData> treeLods;
            [ReadOnly, DeallocateOnJobCompletion] public NativeArray<DetailPrototypeData> detailPrototypes;
            public NativeList<TreeInstance>.ParallelWriter treeInstances;
            public NativeList<DetailInstance>.ParallelWriter detailInstances;

            public void Execute(int index)
            {
                int2 parcel = int2(
                    index % terrainData.terrainSize - terrainData.terrainSize / 2,
                    index / terrainData.terrainSize - terrainData.terrainSize / 2);

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
                        try
                        {
                            treeInstances.AddNoResize(new TreeInstance()
                            {
                                meshIndex = meshIndex,
                                position = position,
                                rotationY = rotationY
                            });
                        }
                        catch (InvalidOperationException) { } // If we run out of space, do nothing.
                    }
                }

                // Detail scattering

                if (distancesq(bounds.Center, cameraPosition) < detailSqrDistance)
                {
                    try
                    {
                        for (int prototypeIndex = 0; prototypeIndex < detailPrototypes.Length; prototypeIndex++)
                        {
                            DetailPrototypeData prototype = detailPrototypes[prototypeIndex];

                            switch (prototype.scatterMode)
                            {
                                case DetailScatterMode.JitteredGrid:
                                    JitteredGrid(parcel, prototype, prototypeIndex, ref random,
                                        detailInstances);

                                    break;
                            }
                        }
                    }
                    catch (InvalidOperationException) { } // If we run out of space, do nothing.
                }
            }

            private void JitteredGrid(int2 parcel, DetailPrototypeData prototype, int meshIndex,
                ref Random random, NativeList<DetailInstance>.ParallelWriter instances)
            {
                float invDensity = (float)terrainData.parcelSize / prototype.density;

                for (int z = 0; z < prototype.density; z++)
                for (int x = 0; x < prototype.density; x++)
                {
                    float3 position;

                    position.x = parcel.x * terrainData.parcelSize + x * invDensity
                                                                   + random.NextFloat(invDensity);

                    position.z = parcel.y * terrainData.parcelSize + z * invDensity
                                                                   + random.NextFloat(invDensity);

                    position.y = terrainData.GetHeight(position.x, position.z);

                    float rotationY = random.NextFloat(-180f, 180f);

                    float scaleXZ = random.NextFloat(prototype.minScaleXZ, prototype.maxScaleXZ);
                    float scaleY = random.NextFloat(prototype.minScaleY, prototype.maxScaleY);

                    instances.AddNoResize(new DetailInstance()
                    {
                        meshIndex = meshIndex,
                        position = position,
                        rotationY = rotationY,
                        scaleXZ = scaleXZ,
                        scaleY = scaleY
                    });
                }
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
            public int density;
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
