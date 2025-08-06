using System;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Profiling;
using UnityEngine.Rendering;
using static Decentraland.Terrain.TerrainLog;
using static Unity.Mathematics.math;
using Random = Unity.Mathematics.Random;

namespace Decentraland.Terrain
{
    [ExecuteAlways]
    public sealed class TerrainRenderer : MonoBehaviour
    {
        [field: SerializeField] private TerrainData TerrainData { get; set; }
        [field: SerializeField] internal GrassIndirectRenderer GrassIndirectRenderer { get; private set; }

        private static MaterialPropertyBlock groundMatProps;
        private static readonly int OCCUPANCY_MAP_ID = Shader.PropertyToID("_OccupancyMap");
        private static readonly int INV_PARCEL_SIZE_ID = Shader.PropertyToID("_InvParcelSize");
        private static readonly int TERRAIN_BOUNDS_ID = Shader.PropertyToID("_TerrainBounds");

#if UNITY_EDITOR
        internal int DetailInstanceCount { get; private set; }
        internal int GroundInstanceCount { get; private set; }
        internal int TreeInstanceCount { get; private set; }
#endif

        private void Update()
        {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            if (TerrainData == null)
            {
                LogHandler.LogFormat(LogType.Error, this, "Terrain data is not set up properly");
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

#if UNITY_EDITOR
            bool renderToAllCameras = true;
#else
            bool renderToAllCameras = false;
#endif

            bool renderGrassIndirect = GrassIndirectRenderer != null;

            if (TerrainData.RenderTreesAndDetail && renderGrassIndirect)
                GrassIndirectRenderer.Render(TerrainData, camera, renderToAllCameras);

            Render(TerrainData, camera, renderToAllCameras, renderGrassIndirect, this);
        }

        private void OnDrawGizmosSelected()
        {
            Gizmos.color = Color.green;
            Bounds bounds = Bounds;
            Gizmos.DrawWireCube(bounds.center, bounds.size);
        }

        public Bounds Bounds => TerrainData != null ? GetBounds(TerrainData) : default;

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

        public static void Render(TerrainData terrainData, Camera camera, bool renderToAllCameras,
            bool renderGrassIndirect = false, TerrainRenderer renderer = null)
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

            var cameraFrustum = new ClipVolume(camera.projectionMatrix * camera.worldToCameraMatrix,
                Allocator.TempJob);

            if (!cameraFrustum.Overlaps(bounds.ToMinMaxAABB()))
            {
                cameraFrustum.Dispose();
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
                groundInstanceCounts = new NativeArray<int>(terrainData.GroundMeshes.Length,
                    Allocator.TempJob);

                groundTransforms = new NativeList<Matrix4x4>(terrainData.GroundInstanceCapacity,
                    Allocator.TempJob);

                var generateGroundJob = new GenerateGroundJob()
                {
                    TerrainData = terrainData.GetData(),
                    CameraPosition = cameraPosition,
                    CameraFrustum = cameraFrustum,
                    InstanceCounts = groundInstanceCounts,
                    Transforms = groundTransforms
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
                int2 min = (int2)floor(cameraFrustum.Bounds.Min.xz / terrainData.ParcelSize);
                int2 size = (int2)ceil(cameraFrustum.Bounds.Max.xz / terrainData.ParcelSize) - min;
                RectInt clipRect = new RectInt(min.x, min.y, size.x, size.y);
                clipRect.ClampToBounds(terrainData.Bounds);

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
                    clipRectMin = int2(clipRect.x, clipRect.y),
                    clipRectSizeX = clipRect.width,
                    cameraFrustum = cameraFrustum,
                    treePrototypes = treePrototypes,
                    treeLods = treeLods,
                    detailPrototypes = detailPrototypes,
                    renderGrassIndirect = renderGrassIndirect,
                    treeInstances = treeInstances.AsParallelWriter(),
                    detailInstances = detailInstances.AsParallelWriter()
                };

                scatterObjects = scatterObjectsJob.Schedule(clipRect.width * clipRect.height,
                    clipRect.width);

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

                    LogHandler.LogFormat(LogType.Warning, terrainData,
                        "The {0} list ran out of space. Increasing capacity to {1}.",
                        nameof(groundTransforms), terrainData.GroundInstanceCapacity);
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
                prepareTreeRenderList.Complete();

                if (treeInstances.Length > treeInstances.Capacity)
                {
                    terrainData.TreeInstanceCapacity
                        = (int)ceil(terrainData.TreeInstanceCapacity * 1.1f);

                    LogHandler.LogFormat(LogType.Warning, terrainData,
                        "The {0} list ran out of space. Increasing capacity to {1}.",
                        nameof(treeInstances), terrainData.TreeInstanceCapacity);
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

                    LogHandler.LogFormat(LogType.Warning, terrainData,
                        "The {0} list ran out of space. Increasing capacity to {1}.",
                        nameof(detailInstances), terrainData.DetailInstanceCapacity);
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

            cameraFrustum.Dispose();
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

            if (terrainData.OccupancyMap != null)
                groundMatProps.SetTexture(OCCUPANCY_MAP_ID, terrainData.OccupancyMap);
            else
                groundMatProps.Clear();

            groundMatProps.SetFloat(INV_PARCEL_SIZE_ID, 1f / terrainData.ParcelSize);
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

                for (int instanceIndex = 0; instanceIndex < totalInstanceCount; instanceIndex++)
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
            public int2 clipRectMin;
            public int clipRectSizeX;
            [ReadOnly] public ClipVolume cameraFrustum;
            [ReadOnly, DeallocateOnJobCompletion] public NativeArray<TreePrototypeData> treePrototypes;
            [ReadOnly, DeallocateOnJobCompletion] public NativeArray<TreeLODData> treeLods;
            [ReadOnly, DeallocateOnJobCompletion] public NativeArray<DetailPrototypeData> detailPrototypes;
            public bool renderGrassIndirect;
            public NativeList<TreeInstance>.ParallelWriter treeInstances;
            public NativeList<DetailInstance>.ParallelWriter detailInstances;

            public void Execute(int index)
            {
                int2 parcel = int2(index % clipRectSizeX, index / clipRectSizeX) + clipRectMin;

                if (terrainData.IsOccupied(parcel))
                    return;

                if (!cameraFrustum.Overlaps(terrainData.GetParcelBounds(parcel)))
                    return;

                Random random = terrainData.GetRandom(parcel);

                if (treePrototypes.Length > 0)
                {
                    ReadOnlySpan<Terrain.TreeInstance> instances = terrainData.GetTreeInstances(parcel);

                    for (int i = 0; i < instances.Length; i++)
                    {
                        if (!terrainData.TryGenerateTree(parcel, instances[i], out float3 position,
                                out float rotationY, out float scaleXZ, out float scaleY))
                        {
                            continue;
                        }

                        int prototypeIndex = instances[i].prototypeIndex;
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
                            var instance = new TreeInstance()
                            {
                                meshIndex = meshIndex,
                                position = position,
                                rotationY = rotationY,
                                scaleXZ = scaleXZ,
                                scaleY = scaleY
                            };

                            if (!treeInstances.TryAddNoResize(instance))
                                break;
                        }
                    }
                }

                /*if (!renderGrassIndirect && detailPrototypes.Length > 0)
                {
                    float3 parcelCenter;
                    parcelCenter.x = (parcel.x + 0.5f) * terrainData.parcelSize;
                    parcelCenter.z = (parcel.y + 0.5f) * terrainData.parcelSize;
                    parcelCenter.y = terrainData.GetHeight(parcelCenter.x, parcelCenter.z);

                    if (distancesq(parcelCenter, cameraPosition) <= detailSqrDistance)
                        for (int prototypeIndex = 0; prototypeIndex < detailPrototypes.Length; prototypeIndex++)
                            if (!JitteredGrid(parcel, prototypeIndex, ref random, detailInstances))
                                break;
                }*/
            }

            private bool JitteredGrid(int2 parcel, int meshIndex,
                ref Random random, NativeList<DetailInstance>.ParallelWriter instances)
            {
                DetailPrototypeData prototype = detailPrototypes[meshIndex];
                int gridSize = (int)(prototype.density * terrainData.parcelSize);
                float invGridSize = (float)terrainData.parcelSize / gridSize;
                float2 corner0 = parcel * terrainData.parcelSize;

                int xMin = terrainData.IsOccupied(int2(parcel.x - 1, parcel.y)) ? 1 : 0;
                int xMax = gridSize - (terrainData.IsOccupied(int2(parcel.x + 1, parcel.y)) ? 1 : 0);
                int zMin = terrainData.IsOccupied(int2(parcel.x, parcel.y - 1)) ? 1 : 0;
                int zMax = gridSize - (terrainData.IsOccupied(int2(parcel.x, parcel.y + 1)) ? 1 : 0);

                for (int z = zMin; z < zMax; z++)
                for (int x = xMin; x < xMax; x++)
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
            public float density;
            public float minScaleXZ;
            public float maxScaleXZ;
            public float minScaleY;
            public float maxScaleY;
        }

        private struct TreeInstance : IComparable<TreeInstance>
        {
            public int meshIndex;
            public float3 position;
            public float rotationY;
            public float scaleXZ;
            public float scaleY;

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
