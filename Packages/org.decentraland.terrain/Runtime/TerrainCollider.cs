using System;
using System.Collections.Generic;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Profiling;
using UnityEngine.Rendering;
using static Unity.Mathematics.math;
using Random = Unity.Mathematics.Random;

namespace Decentraland.Terrain
{
    public sealed class TerrainCollider : MonoBehaviour
    {
        [field: SerializeField] private TerrainData TerrainData { get; set; }

        private readonly List<ParcelData> dirtyParcels = new();
        private readonly List<ParcelData> freeParcels = new();
        private readonly List<ParcelData> usedParcels = new();
        private static short[] indexBuffer;
        private PrefabInstancePool[] treePools;

        private static VertexAttributeDescriptor[] vertexLayout =
        {
            new(VertexAttribute.Position, VertexAttributeFormat.Float32, 3)
#if UNITY_EDITOR
            , new(VertexAttribute.Normal, VertexAttributeFormat.Float32, 3)
#endif
        };

        private void Awake()
        {
            treePools = new PrefabInstancePool[TerrainData.TreePrototypes.Length];
            Transform poolParent;

#if UNITY_EDITOR
            poolParent = transform;
#else
            poolParent = null;
#endif

            for (int i = 0; i < treePools.Length; i++)
                treePools[i] = new PrefabInstancePool(TerrainData.TreePrototypes[i].Collider,
                    poolParent);
        }

        private void OnDestroy()
        {
            for (int i = 0; i < treePools.Length; i++)
                treePools[i].Dispose();
        }

        private void Update()
        {
            var mainCamera = Camera.main;

            if (mainCamera == null)
                return;

            TerrainDataData terrainData = TerrainData.GetData();
            float useRadius = terrainData.parcelSize * (1f / 3f);
            float2 cameraPosition = ((float3)mainCamera.transform.position).xz;
            RectInt usedRect = terrainData.PositionToParcelRect(cameraPosition, useRadius);

            for (int i = usedParcels.Count - 1; i >= 0; i--)
            {
                ParcelData parcelData = usedParcels[i];

                if (!usedRect.Contains(parcelData.parcel.ToVector2Int()))
                {
                    usedParcels.RemoveAtSwapBack(i);
                    freeParcels.Add(parcelData);
                }
            }

            for (int y = usedRect.yMin; y < usedRect.yMax; y++)
            for (int x = usedRect.xMin; x < usedRect.xMax; x++)
            {
                int2 parcel = int2(x, y);

                if (ContainsParcel(usedParcels, parcel))
                    continue;

                if (freeParcels.Count > 0)
                {
                    ParcelData parcelData = null;

                    // First, check if the exact parcel we need is in the free list already. If so,
                    // there's nothing to do.
                    for (int i = freeParcels.Count - 1; i >= 0; i--)
                    {
                        ParcelData freeParcel = freeParcels[i];

                        if (all(freeParcel.parcel == parcel))
                        {
                            parcelData = freeParcel;
                            freeParcels.RemoveAtSwapBack(i);
                            usedParcels.Add(parcelData);
                            break;
                        }
                    }

                    // Else, reuse the last parcel in the free list.
                    if (parcelData == null)
                    {
                        int lastIndex = freeParcels.Count - 1;
                        parcelData = freeParcels[lastIndex];
                        parcelData.parcel = parcel;
                        dirtyParcels.Add(parcelData);
                        freeParcels.RemoveAt(lastIndex);
                        usedParcels.Add(parcelData);

                        parcelData.collider.transform.position = new Vector3(
                            parcel.x * terrainData.parcelSize, 0f, parcel.y * terrainData.parcelSize);

                        if (parcelData.treePrototypeIndex >= 0)
                        {
                            treePools[parcelData.treePrototypeIndex].Release(parcelData.treeInstance);
                            parcelData.treeInstance = null;
                            parcelData.treePrototypeIndex = -1;
                        }

                        Random random = terrainData.GetRandom(parcel);
                        GenerateTree(parcel, in terrainData, ref random, parcelData);
                    }
                }
                else
                {
                    Mesh mesh = CreateParcelMesh(in terrainData);
                    ParcelData parcelData = new() { parcel = parcel, mesh = mesh };
                    dirtyParcels.Add(parcelData);
                    usedParcels.Add(parcelData);

                    parcelData.collider = new GameObject("Parcel Collider")
                        .AddComponent<MeshCollider>();

                    parcelData.collider.cookingOptions = MeshColliderCookingOptions.UseFastMidphase;

                    parcelData.collider.transform.position = new Vector3(
                        parcel.x * terrainData.parcelSize, 0f, parcel.y * terrainData.parcelSize);

                    Random random = terrainData.GetRandom(parcel);
                    GenerateTree(parcel, in terrainData, ref random, parcelData);

#if UNITY_EDITOR
                    parcelData.collider.transform.SetParent(transform, true);
#endif
                }
            }

            if (dirtyParcels.Count == 0)
                return;

            var parcels = new NativeArray<int2>(dirtyParcels.Count, Allocator.TempJob,
                NativeArrayOptions.UninitializedMemory);

            for (int i = 0; i < dirtyParcels.Count; i++)
                parcels[i] = dirtyParcels[i].parcel;

            int sideVertexCount = terrainData.parcelSize + 1;
            int meshVertexCount = sideVertexCount * sideVertexCount;

            var vertices = new NativeArray<Vertex>(meshVertexCount * dirtyParcels.Count,
                Allocator.TempJob, NativeArrayOptions.UninitializedMemory);

            var generateVerticesJob = new GenerateVertices()
            {
                terrainData = terrainData,
                parcels = parcels,
                vertices = vertices
            };

            generateVerticesJob.Schedule(vertices.Length).Complete();

            for (int i = 0; i < dirtyParcels.Count; i++)
            {
                ParcelData parcelData = dirtyParcels[i];

                parcelData.mesh.SetVertexBufferData(vertices, i * meshVertexCount, 0, meshVertexCount,
                    0, MeshUpdateFlags.DontValidateIndices | MeshUpdateFlags.DontRecalculateBounds);
            }

            var meshes = new NativeArray<int>(dirtyParcels.Count, Allocator.TempJob,
                NativeArrayOptions.UninitializedMemory);

            for (int i = 0; i < dirtyParcels.Count; i++)
                meshes[i] = dirtyParcels[i].mesh.GetInstanceID();

            var bakeMeshesJob = new BakeMeshes() { meshes = meshes };
            bakeMeshesJob.Schedule(meshes.Length, 1).Complete();

            for (int i = 0; i < dirtyParcels.Count; i++)
            {
                ParcelData parcelData = dirtyParcels[i];

                // Needed even if the mesh is already assigned to the collider. Doing it will cause the
                // collider to check if the mesh has changed.
                parcelData.collider.sharedMesh = parcelData.mesh;
            }

            dirtyParcels.Clear();
        }

        private static bool ContainsParcel(List<ParcelData> parcels, int2 parcel)
        {
            for (int i = 0; i < parcels.Count; i++)
                if (all(parcels[i].parcel == parcel))
                    return true;

            return false;
        }

        private static short[] CreateIndexBuffer(int parcelSize)
        {
            int index = 0;
            var indexBuffer = new short[parcelSize * parcelSize * 6];
            int sideVertexCount = parcelSize + 1;

            for (int z = 0; z < parcelSize; z++)
            {
                for (int x = 0; x < parcelSize; x++)
                {
                    int start = z * sideVertexCount + x;

                    indexBuffer[index++] = (short)start;
                    indexBuffer[index++] = (short)(start + sideVertexCount + 1);
                    indexBuffer[index++] = (short)(start + 1);

                    indexBuffer[index++] = (short)start;
                    indexBuffer[index++] = (short)(start + sideVertexCount);
                    indexBuffer[index++] = (short)(start + sideVertexCount + 1);
                }
            }

            return indexBuffer;
        }

        private static Mesh CreateParcelMesh(in TerrainDataData terrainData)
        {
            if (indexBuffer == null)
                indexBuffer = CreateIndexBuffer(terrainData.parcelSize);

            var mesh = new Mesh() { name = "Parcel Collision Mesh" };
            mesh.MarkDynamic();
            int sideVertexCount = terrainData.parcelSize + 1;
            mesh.SetVertexBufferParams(sideVertexCount * sideVertexCount, vertexLayout);
            mesh.SetIndexBufferParams(indexBuffer.Length, IndexFormat.UInt16);
            mesh.subMeshCount = 1;

            var flags = MeshUpdateFlags.DontValidateIndices | MeshUpdateFlags.DontNotifyMeshUsers
                                                            | MeshUpdateFlags.DontRecalculateBounds;

            mesh.SetIndexBufferData(indexBuffer, 0, 0, indexBuffer.Length, flags);
            mesh.SetSubMesh(0, new SubMeshDescriptor(0, indexBuffer.Length), flags);

            Vector3 parcelMax = new Vector3(terrainData.parcelSize, terrainData.maxHeight,
                terrainData.parcelSize);

            mesh.bounds = new Bounds(parcelMax * 0.5f, parcelMax);

            return mesh;
        }

        private void GenerateTree(int2 parcel, in TerrainDataData terrainData, ref Random random,
            ParcelData parcelData)
        {
            if (!terrainData.NextTree(parcel, ref random, out float3 position, out float rotationY,
                    out int prototypeIndex))
            {
                return;
            }

            parcelData.treeInstance = treePools[prototypeIndex].Get();
            parcelData.treePrototypeIndex = prototypeIndex;

            parcelData.treeInstance.transform.SetPositionAndRotation(position,
                Quaternion.Euler(0f, rotationY, 0f));
        }

        [BurstCompile]
        private struct BakeMeshes : IJobParallelFor
        {
            [ReadOnly, DeallocateOnJobCompletion] public NativeArray<int> meshes;

            public void Execute(int index) =>
                Physics.BakeMesh(meshes[index], false, MeshColliderCookingOptions.UseFastMidphase);
        }

        [BurstCompile]
        private struct GenerateVertices : IJobParallelForBatch
        {
            public TerrainDataData terrainData;
            [ReadOnly, DeallocateOnJobCompletion] public NativeArray<int2> parcels;
            [WriteOnly] public NativeArray<Vertex> vertices;

            public void Execute(int startIndex, int count)
            {
                int batchEnd = startIndex + count;
                int vertexIndex = startIndex;
                int sideVertexCount = terrainData.parcelSize + 1;
                int meshVertexCount = sideVertexCount * sideVertexCount;
                int meshIndex = startIndex / meshVertexCount;

                while (vertexIndex < batchEnd)
                {
                    int2 parcel = parcels[meshIndex];
                    int2 parcelOriginXZ = parcel * terrainData.parcelSize;
                    int meshStart = meshIndex * meshVertexCount;
                    int meshEnd = min(meshStart + meshVertexCount, batchEnd);

                    while (vertexIndex < meshEnd)
                    {
                        int x = (vertexIndex - meshStart) % sideVertexCount;
                        int z = (vertexIndex - meshStart) / sideVertexCount;
                        float y = terrainData.GetHeight(x + parcelOriginXZ.x, z + parcelOriginXZ.y);

                        var vertex = new Vertex() { position = float3(x, y, z) };
#if UNITY_EDITOR
                        vertex.normal = terrainData.GetNormal(x, z);
#endif

                        vertices[vertexIndex] = vertex;
                        vertexIndex++;
                    }

                    meshIndex++;
                }
            }
        }

        private struct Vertex
        {
            public float3 position;
#if UNITY_EDITOR // Normals are only needed to draw the collider gizmo.
            public float3 normal;
#endif
        }

        [Serializable]
        private sealed class ParcelData
        {
            public int2 parcel;
            public MeshCollider collider;
            public Mesh mesh;
            public int treePrototypeIndex = -1;
            public GameObject treeInstance;
        }
    }
}
