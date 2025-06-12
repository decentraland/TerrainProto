using System;
using System.Collections.Generic;
using System.Linq;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Pool;
using UnityEngine.Profiling;
using UnityEngine.Rendering;
using static Unity.Mathematics.math;
using Random = Unity.Mathematics.Random;

namespace Decentraland.Terrain
{
    public sealed class TerrainCollider : MonoBehaviour
    {
        [SerializeField] private TerrainData terrainData;

        private List<ParcelData> freeParcels = new();
        private List<ParcelData> usedParcels = new();
        private PrefabInstancePool[] treePools;

        private void Awake()
        {
            treePools = new PrefabInstancePool[terrainData.treePrototypes.Length];

            for (int i = 0; i < treePools.Length; i++)
                treePools[i] = new PrefabInstancePool(terrainData.treePrototypes[i].collider
#if UNITY_EDITOR
                    , transform
#endif
                );
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

            TerrainDataData terrainData = this.terrainData.GetData();
            float useRadius = terrainData.parcelSize * (1f / 3f);
            var cameraPosition = mainCamera.transform.position;
            RectInt usedRect = terrainData.PositionToParcelRect(cameraPosition, useRadius);

            for (int i = usedParcels.Count - 1; i >= 0; i--)
            {
                ParcelData parcelData = usedParcels[i];

                if (!usedRect.Contains(parcelData.parcel))
                {
                    usedParcels.RemoveAtSwapBack(i);
                    freeParcels.Add(parcelData);
                }
            }

            for (int y = usedRect.yMin; y < usedRect.yMax; y++)
            for (int x = usedRect.xMin; x < usedRect.xMax; x++)
            {
                Vector2Int parcel = new Vector2Int(x, y);

                if (usedParcels.Any(i => i.parcel == parcel))
                    continue;

                Random random = terrainData.GetRandom(int2(parcel.x, parcel.y));

                if (freeParcels.Count > 0)
                {
                    ParcelData parcelData = null;

                    // First, check if the exact parcel we need is in the free list already. If so,
                    // there's nothing to do.
                    for (int i = freeParcels.Count - 1; i >= 0; i--)
                    {
                        ParcelData freeParcel = freeParcels[i];

                        if (freeParcel.parcel == parcel)
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
                        freeParcels.RemoveAt(lastIndex);
                        usedParcels.Add(parcelData);

                        SetParcelMeshVertices(parcelData.mesh, parcel, in terrainData);
                        parcelData.collider.sharedMesh = parcelData.mesh;

                        parcelData.collider.transform.position = new Vector3(
                            parcel.x * terrainData.parcelSize, 0f, parcel.y * terrainData.parcelSize);

                        if (parcelData.treePrototypeIndex >= 0)
                        {
                            treePools[parcelData.treePrototypeIndex].Release(parcelData.treeInstance);
                            parcelData.treeInstance = null;
                            parcelData.treePrototypeIndex = -1;
                        }

                        GenerateTree(parcel, in terrainData, ref random, parcelData);
                    }
                }
                else
                {
                    ParcelData parcelData = new() { parcel = parcel, mesh = new Mesh() };
                    usedParcels.Add(parcelData);

                    parcelData.mesh.name = "Parcel Collision Mesh";
                    parcelData.mesh.MarkDynamic();
                    SetParcelMeshVertices(parcelData.mesh, parcel, in terrainData);
                    SetParcelMeshIndicesAndNormals(parcelData.mesh);

                    Vector3 parcelMax = new Vector3(terrainData.parcelSize, terrainData.maxHeight,
                        terrainData.parcelSize);

                    parcelData.mesh.bounds = new Bounds(parcelMax * 0.5f, parcelMax);

                    parcelData.collider = new GameObject("Parcel Collider")
                        .AddComponent<MeshCollider>();

                    parcelData.collider.cookingOptions = MeshColliderCookingOptions.CookForFasterSimulation
                                                         | MeshColliderCookingOptions.UseFastMidphase;

                    parcelData.collider.sharedMesh = parcelData.mesh;

                    parcelData.collider.transform.position = new Vector3(
                        parcel.x * terrainData.parcelSize, 0f, parcel.y * terrainData.parcelSize);

                    GenerateTree(parcel, in terrainData, ref random, parcelData);

#if UNITY_EDITOR
                    parcelData.collider.transform.SetParent(transform, true);
#endif
                }
            }
        }

        private void GenerateTree(Vector2Int parcel, in TerrainDataData terrainData, ref Random random,
            ParcelData parcelData)
        {
            if (!terrainData.NextTree(int2(parcel.x, parcel.y), ref random,
                    out float3 position, out float rotationY, out int prototypeIndex))
            {
                return;
            }

            parcelData.treeInstance = treePools[prototypeIndex].Get();
            parcelData.treePrototypeIndex = prototypeIndex;

            parcelData.treeInstance.transform.SetPositionAndRotation(position,
                Quaternion.Euler(0f, rotationY, 0f));
        }

        private void SetParcelMeshIndicesAndNormals(Mesh mesh)
        {
            int parcelSize = terrainData.parcelSize;
            int sideVertexCount = parcelSize + 1;

            using (ListPool<int>.Get(out var triangles))
            {
                triangles.EnsureCapacity(parcelSize * parcelSize * 6);

                for (int z = 0; z < parcelSize; z++)
                {
                    for (int x = 0; x < parcelSize; x++)
                    {
                        int start = z * sideVertexCount + x;

                        triangles.Add(start);
                        triangles.Add(start + sideVertexCount + 1);
                        triangles.Add(start + 1);

                        triangles.Add(start);
                        triangles.Add(start + sideVertexCount);
                        triangles.Add(start + sideVertexCount + 1);
                    }
                }

                mesh.SetTriangles(triangles, 0, false);
            }

            // Normals are only needed to draw the collider gizmo.
#if UNITY_EDITOR
            using (ListPool<Vector3>.Get(out var normals))
            {
                int normalsCount = sideVertexCount * sideVertexCount;
                normals.EnsureCapacity(normalsCount);

                for (int i = 0; i < normalsCount; i++)
                    normals.Add(Vector3.up);

                mesh.SetNormals(normals, 0, normalsCount,
                    MeshUpdateFlags.DontNotifyMeshUsers | MeshUpdateFlags.DontRecalculateBounds);
            }
#endif
        }

        private void SetParcelMeshVertices(Mesh mesh, Vector2Int parcel, in TerrainDataData terrainData)
        {
            Profiler.BeginSample(nameof(SetParcelMeshVertices));
            int parcelSize = terrainData.parcelSize;
            Vector2 parcelOriginXZ = new Vector2(parcel.x * parcelSize, parcel.y * parcelSize);

            using (ListPool<Vector3>.Get(out var vertices))
            {
                int sideVertexCount = parcelSize + 1;
                vertices.EnsureCapacity(sideVertexCount * sideVertexCount);
                Profiler.BeginSample("GetTerrainHeight");

                for (int z = 0; z < sideVertexCount; z++)
                {
                    for (int x = 0; x < sideVertexCount; x++)
                    {
                        float y = terrainData.GetHeight(x + parcelOriginXZ.x, z + parcelOriginXZ.y);
                        vertices.Add(new Vector3(x, y, z));
                    }
                }

                Profiler.EndSample();
                Profiler.BeginSample("SetMeshVertices");

                mesh.SetVertices(vertices, 0, vertices.Count,
                    MeshUpdateFlags.DontValidateIndices | MeshUpdateFlags.DontRecalculateBounds);

                Profiler.EndSample();
            }

            Profiler.EndSample();
        }

        [Serializable]
        private sealed class ParcelData
        {
            public Vector2Int parcel;
            public MeshCollider collider;
            public Mesh mesh;
            public int treePrototypeIndex = -1;
            public GameObject treeInstance;
        }
    }
}
