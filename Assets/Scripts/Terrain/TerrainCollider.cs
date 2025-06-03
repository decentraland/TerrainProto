using System;
using System.Collections.Generic;
using System.Linq;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Pool;
using UnityEngine.Profiling;
using UnityEngine.Rendering;
using UnityEngine.Serialization;
using static Unity.Mathematics.math;
using Object = UnityEngine.Object;
using Random = Unity.Mathematics.Random;

namespace Decentraland.Terrain
{
    public sealed class TerrainCollider : MonoBehaviour
    {
        [SerializeField] private TerrainData terrainData;

        private List<ParcelData> freeParcels = new();
        private List<ParcelData> usedParcels = new();
        private ObjectPool<GameObject>[] treePools;

        private void Awake()
        {
            var treePrefabs = terrainData.treePrototypes;
            treePools = new ObjectPool<GameObject>[treePrefabs.Length];

            for (int i = 0; i < treePrefabs.Length; i++)
            {
                GameObject colliderPrefab = treePrefabs[i].collider;

                treePools[i] = new ObjectPool<GameObject>(
                    () =>
                    {
                        GameObject instance = Object.Instantiate(colliderPrefab
#if UNITY_EDITOR
                            , transform
#endif
                        );
                        instance.name = colliderPrefab.name;
                        return instance;
                    },
                    go => go.SetActive(true), go => go.SetActive(false), go =>
                    {
                        if (go != null)
                            Object.Destroy(go);
                    });
            }
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

            int parcelSize = terrainData.parcelSize;
            float useRadius = parcelSize * (1f / 3f);
            var cameraPosition = mainCamera.transform.position;
            RectInt usedRect = terrainData.WorldPositionToParcelRect(cameraPosition, useRadius);

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

                Random random = terrainData.GetRandom(parcel);

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

                        SetParcelMeshVertices(parcelData.mesh, parcel);
                        parcelData.collider.sharedMesh = parcelData.mesh;

                        parcelData.collider.transform.position = new Vector3(
                            parcel.x * parcelSize, 0f, parcel.y * parcelSize);

                        for (int i = 0; i < parcelData.trees.Count; i++)
                        {
                            TreeData tree = parcelData.trees[i];
                            treePools[tree.prototypeIndex].Release(tree.instance);
                        }

                        parcelData.trees.Clear();

                        CreateTrees(parcel, ref random, parcelData.trees);
                    }
                }
                else
                {
                    ParcelData parcelData = new() { parcel = parcel, mesh = new Mesh() };
                    usedParcels.Add(parcelData);

                    parcelData.mesh.name = "Parcel Collision Mesh";
                    parcelData.mesh.MarkDynamic();
                    SetParcelMeshVertices(parcelData.mesh, parcel);
                    SetParcelMeshIndicesAndNormals(parcelData.mesh);

                    Vector3 parcelMax = new Vector3(parcelSize, terrainData.maxHeight, parcelSize);
                    parcelData.mesh.bounds = new Bounds(parcelMax * 0.5f, parcelMax);

                    parcelData.collider = new GameObject("Parcel Collider")
                        .AddComponent<MeshCollider>();

                    parcelData.collider.transform.position = new Vector3(
                        parcel.x * parcelSize, 0f, parcel.y * parcelSize);

                    parcelData.collider.cookingOptions = MeshColliderCookingOptions.CookForFasterSimulation
                                                         | MeshColliderCookingOptions.UseFastMidphase;

                    parcelData.collider.sharedMesh = parcelData.mesh;

                    CreateTrees(parcel, ref random, parcelData.trees);

#if UNITY_EDITOR
                    parcelData.collider.transform.SetParent(transform, true);
#endif
                }
            }


#if UNITY_EDITOR
            for (int y = usedRect.yMin; y < usedRect.yMax; y++)
            for (int x = usedRect.xMin; x < usedRect.xMax; x++)
            {
                Vector2Int parcel = new Vector2Int(x, y);

                Vector3 a = new Vector3(parcel.x * parcelSize, cameraPosition.y, parcel.y * parcelSize);
                Vector3 b = a + new Vector3(parcelSize, 0f, 0f);
                Vector3 c = a + new Vector3(parcelSize, 0f, parcelSize);
                Vector3 d = a + new Vector3(0f, 0f, parcelSize);

                Debug.DrawLine(a, b, Color.white);
                Debug.DrawLine(b, c, Color.white);
                Debug.DrawLine(c, d, Color.white);
                Debug.DrawLine(d, a, Color.white);
            }

            {
                Vector3 a = cameraPosition + new Vector3(-useRadius, 0f, -useRadius);
                Vector3 b = cameraPosition + new Vector3(useRadius, 0f, -useRadius);
                Vector3 c = cameraPosition + new Vector3(useRadius, 0f, useRadius);
                Vector3 d = cameraPosition + new Vector3(-useRadius, 0f, useRadius);

                Debug.DrawLine(a, b, Color.green);
                Debug.DrawLine(b, c, Color.green);
                Debug.DrawLine(c, d, Color.green);
                Debug.DrawLine(d, a, Color.green);
            }
#endif
        }

        private void CreateTrees(Vector2Int parcel, ref Random random, List<TreeData> trees)
        {
            TerrainData.NextTree(int2(parcel.x, parcel.y), terrainData.parcelSize,
                terrainData.treePrototypes.Length, ref random, out float3 position, out float rotationY,
                out int prototypeIndex);

            TreeData tree = new TreeData()
            {
                instance = treePools[prototypeIndex].Get(),
                prototypeIndex = prototypeIndex
            };

            tree.instance.transform.SetPositionAndRotation(position,
                Quaternion.Euler(0f, rotationY, 0f));

            trees.Add(tree);
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

        private void SetParcelMeshVertices(Mesh mesh, Vector2Int parcel)
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
                        float y = TerrainData.GetHeight(x + parcelOriginXZ.x, z + parcelOriginXZ.y);
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
            public List<TreeData> trees = new();
        }

        [Serializable]
        private struct TreeData
        {
            public GameObject instance;
            public int prototypeIndex;
        }
    }
}
