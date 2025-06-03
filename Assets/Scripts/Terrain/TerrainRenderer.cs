using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEditor;
using UnityEngine;
using UnityEngine.Profiling;
using UnityEngine.Rendering;
using static Unity.Mathematics.math;
using Object = UnityEngine.Object;
using Random = Unity.Mathematics.Random;

namespace Decentraland.Terrain
{
    [ExecuteAlways]
    public sealed class TerrainRenderer : MonoBehaviour
    {
        [SerializeField] private TerrainData terrainData;
        [SerializeField] private Material material;

        private Mesh parcelMesh;
        private Bounds bounds;

#if UNITY_EDITOR
        public Mesh ParcelMesh => parcelMesh;
#endif

        private void OnValidate()
        {
#if UNITY_EDITOR
            if (parcelMesh != null)
            {
                if (!Application.isPlaying)
                    Object.DestroyImmediate(parcelMesh);
                else
                    Object.Destroy(parcelMesh);
            }
#endif

            if (terrainData == null)
                return;

            parcelMesh = CreateParcelMesh();
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
            if (parcelMesh != null)
            {
#if UNITY_EDITOR
                if (!Application.isPlaying)
                    Object.DestroyImmediate(parcelMesh);
                else
#endif
                    Object.Destroy(parcelMesh);
            }
        }

        private Mesh CreateParcelMesh()
        {
            var parcelMesh = new Mesh()
            {
                name = "Parcel Render Mesh",
                hideFlags = HideFlags.DontSave
            };

            int parcelSize = terrainData.parcelSize;
            int sideVertexCount = parcelSize + 1;
            var vertices = new Vector3[sideVertexCount * sideVertexCount];

            for (int z = 0; z <= parcelSize; z++)
            for (int x = 0; x <= parcelSize; x++)
                vertices[z * sideVertexCount + x] = new Vector3(x, 0f, z);

            var triangles = new int[parcelSize * parcelSize * 6];
            int index = 0;

            for (int z = 0; z < parcelSize; z++)
            {
                for (int x = 0; x < parcelSize; x++)
                {
                    int start = z * sideVertexCount + x;

                    triangles[index++] = start;
                    triangles[index++] = start + sideVertexCount + 1;
                    triangles[index++] = start + 1;

                    triangles[index++] = start;
                    triangles[index++] = start + sideVertexCount;
                    triangles[index++] = start + sideVertexCount + 1;
                }
            }

            var normals = new Vector3[sideVertexCount * sideVertexCount];

            for (int z = 0; z <= parcelSize; z++)
            for (int x = 0; x <= parcelSize; x++)
                normals[z * sideVertexCount + x] = Vector3.up;

            parcelMesh.vertices = vertices;
            parcelMesh.triangles = triangles;
            parcelMesh.normals = normals;
            parcelMesh.UploadMeshData(true);

            return parcelMesh;
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

            Profiler.BeginSample("RenderTerrain");

            int parcelSize = terrainData.parcelSize;
            int terrainSize = terrainData.terrainSize;
            float maxHeight = terrainData.maxHeight;

            // Deallocated by GenerateParcelsJob
            NativeArray<float4> clipPlanes = new NativeArray<float4>(6, Allocator.TempJob,
                NativeArrayOptions.UninitializedMemory);

            var worldToProjectionMatrix = camera.projectionMatrix * camera.worldToCameraMatrix;

            float4 row0 = worldToProjectionMatrix.GetRow(0);
            float4 row1 = worldToProjectionMatrix.GetRow(1);
            float4 row2 = worldToProjectionMatrix.GetRow(2);
            float4 row3 = worldToProjectionMatrix.GetRow(3);

            clipPlanes[0] = row3 + row0;
            clipPlanes[1] = row3 - row0;
            clipPlanes[2] = row3 + row1;
            clipPlanes[3] = row3 - row1;
            clipPlanes[4] = row3 + row2;
            clipPlanes[5] = row3 - row2;

            for (int i = 0; i < clipPlanes.Length; i++)
                clipPlanes[i] /= length(clipPlanes[i].xyz);

            // Deallocated by GenerateParcelsJob
            var treePrototypes = new NativeArray<TreePrototypeData>(terrainData.treePrototypes.Length,
                Allocator.TempJob, NativeArrayOptions.UninitializedMemory);

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

            // Deallocated by GenerateParcelsJob
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

            // Deallocated by GenerateParcelsJob
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

            var parcelTransforms = new NativeList<Matrix4x4>(32, Allocator.TempJob);
            var treeInstances = new NativeList<TreeInstance>(32, Allocator.TempJob);
            var detailInstances = new NativeList<DetailInstance>(8192, Allocator.TempJob);

            var generateParcelsJob = new GenerateParcelsJob()
            {
                terrainSize = terrainSize,
                parcelSize = parcelSize,
                maxHeight = maxHeight,
                seed = terrainData.seed,
                cameraPosition = camera.transform.position,
                detailSqrDistance = terrainData.detailDistance * terrainData.detailDistance,
                clipPlanes = clipPlanes,
                treePrototypes = treePrototypes,
                treeLods = treeLods,
                detailPrototypes = detailPrototypes,
                parcelTransforms = parcelTransforms.AsParallelWriter(),
                treeInstances = treeInstances.AsParallelWriter(),
                detailInstances = detailInstances.AsParallelWriter()
            };

            int parcelCount = terrainSize * terrainSize;

            JobHandle generateParcels = generateParcelsJob.Schedule(parcelCount,
                JobUtility.GetBatchSize(parcelCount));

            var treeInstanceCounts = new NativeArray<int>(treeMeshCount, Allocator.TempJob);
            var treeTransforms = new NativeList<Matrix4x4>(Allocator.TempJob);

            var prepareTreeRenderListJob = new PrepareTreeRenderListJob()
            {
                instances = treeInstances,
                instanceCounts = treeInstanceCounts,
                transforms = treeTransforms
            };

            JobHandle prepareTreeRenderList = prepareTreeRenderListJob.Schedule(generateParcels);

            var detailInstanceCounts = new NativeArray<int>(detailPrototypes.Length, Allocator.TempJob);
            var detailTransforms = new NativeList<Matrix4x4>(Allocator.TempJob);

            var prepareDetailRenderListJob = new PrepareDetailRenderListJob()
            {
                instances = detailInstances,
                instanceCounts = detailInstanceCounts,
                transforms = detailTransforms
            };

            JobHandle prepareDetailRenderList = prepareDetailRenderListJob.Schedule(generateParcels);

            var renderParams = new RenderParams()
            {
#if !UNITY_EDITOR
                camera = camera,
#endif
                instanceID = GetInstanceID(),
                layer = gameObject.layer,
                receiveShadows = true,
                renderingLayerMask = RenderingLayerMask.defaultRenderingLayerMask,
            };

            renderParams.worldBounds = new Bounds(
                new Vector3(0f, maxHeight * 0.5f, 0f),
                new Vector3(terrainSize * parcelSize, maxHeight, terrainSize * parcelSize));

            JobHandle.CompleteAll(ref prepareTreeRenderList, ref prepareDetailRenderList);
            treeInstances.Dispose();
            detailInstances.Dispose();

            RenderParcels(renderParams, parcelTransforms.AsArray());
            parcelTransforms.Dispose();

            RenderTrees(renderParams, treeTransforms.AsArray(), treeInstanceCounts);
            treeInstanceCounts.Dispose();
            treeTransforms.Dispose();

            RenderDetails(renderParams, detailTransforms.AsArray(), detailInstanceCounts);
            detailInstanceCounts.Dispose();
            detailTransforms.Dispose();

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

        private void RenderParcels(RenderParams renderParams, NativeArray<Matrix4x4> instanceData)
        {
            if (instanceData.Length == 0)
                return;

            renderParams.material = material;
            renderParams.shadowCastingMode = ShadowCastingMode.On;

            Graphics.RenderMeshInstanced(renderParams, parcelMesh, 0, instanceData);
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

        private struct GenerateParcelsJob : IJobParallelFor
        {
            public int terrainSize;
            public int parcelSize;
            public float maxHeight;
            public int seed;
            public float3 cameraPosition;
            public float detailSqrDistance;
            [ReadOnly, DeallocateOnJobCompletion] public NativeArray<float4> clipPlanes;
            [ReadOnly, DeallocateOnJobCompletion] public NativeArray<TreePrototypeData> treePrototypes;
            [ReadOnly, DeallocateOnJobCompletion] public NativeArray<TreeLODData> treeLods;
            [ReadOnly, DeallocateOnJobCompletion] public NativeArray<DetailPrototypeData> detailPrototypes;
            public NativeList<Matrix4x4>.ParallelWriter parcelTransforms;
            public NativeList<TreeInstance>.ParallelWriter treeInstances;
            public NativeList<DetailInstance>.ParallelWriter detailInstances;

            public void Execute(int index)
            {
                int2 parcel = int2(
                    index % terrainSize - terrainSize / 2,
                    index / terrainSize - terrainSize / 2);

                float3 parcelLocalCenter = float3(parcelSize, maxHeight, parcelSize) * 0.5f;
                float negParcelRadius = -length(parcelLocalCenter);

                float4 parcelCenter = float4(parcel.x * parcelSize + parcelLocalCenter.x,
                    parcelLocalCenter.y, parcel.y * parcelSize + parcelLocalCenter.z, 1f);

                for (int i = 0; i < clipPlanes.Length; i++)
                    if (dot(clipPlanes[i], parcelCenter) < negParcelRadius)
                        return;

                parcelTransforms.AddNoResize(Matrix4x4.Translate(
                    new Vector3(parcel.x * parcelSize, 0f, parcel.y * parcelSize)));

                Random random = TerrainData.GetRandom(parcel, terrainSize, seed);

                // Tree scattering

                {
                    TerrainData.NextTree(parcel, parcelSize, treePrototypes.Length, ref random,
                        out float3 position, out float rotationY, out int prototypeIndex);

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
                        treeInstances.AddNoResize(new TreeInstance()
                        {
                            meshIndex = meshIndex,
                            position = position,
                            rotationY = rotationY
                        });
                    }
                }

                // Detail scattering

                if (distancesq(parcelCenter.xyz, cameraPosition) < detailSqrDistance)
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
            }

            private void JitteredGrid(int2 parcel, DetailPrototypeData prototype, int meshIndex,
                ref Random random, NativeList<DetailInstance>.ParallelWriter instances)
            {
                float invDensity = (float)parcelSize / prototype.density;

                for (int z = 0; z < prototype.density; z++)
                for (int x = 0; x < prototype.density; x++)
                {
                    Vector3 position;
                    position.x = parcel.x * parcelSize + x * invDensity + random.NextFloat(invDensity);
                    position.z = parcel.y * parcelSize + z * invDensity + random.NextFloat(invDensity);
                    position.y = TerrainData.GetHeight(position.x, position.z);

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

        private struct PrepareDetailRenderListJob : IJob
        {
            public NativeList<DetailInstance> instances;
            [WriteOnly] public NativeArray<int> instanceCounts;
            public NativeList<Matrix4x4> transforms;

            public void Execute()
            {
                instances.Sort();
                int instanceCount = 0;
                int meshIndex = 0;

                if (transforms.Capacity < instances.Length)
                    transforms.Capacity = instances.Length;

                for (int instanceIndex = 0; instanceIndex < instances.Length; instanceIndex++)
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
                instances.Sort();
                int instanceCount = 0;
                int meshIndex = 0;

                if (transforms.Capacity < instances.Length)
                    transforms.Capacity = instances.Length;

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
