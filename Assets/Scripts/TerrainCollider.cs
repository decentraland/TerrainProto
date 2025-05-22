using System.Collections.Generic;
using System.Linq;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Pool;
using UnityEngine.Profiling;
using UnityEngine.Rendering;
using static Unity.Mathematics.math;
using static Unity.Mathematics.noise;

namespace Decentraland.Terrain
{
    public sealed class TerrainCollider : MonoBehaviour
    {
        [SerializeField] private int parcelSize = 16;
        [SerializeField] private int terrainSize;

        private readonly List<ParcelData> freeParcels = new();
        private readonly List<ParcelData> usedParcels = new();

        private void Update()
        {
            var mainCamera = Camera.main;

            if (mainCamera == null)
                return;

            float loadRadius = parcelSize / 3f;
            float unloadRadius = loadRadius * 2f;
            var cameraPosition = mainCamera.transform.position;

            {
                Vector2Int cameraParcel = new Vector2Int(
                    Mathf.RoundToInt(cameraPosition.x / parcelSize - 0.5f),
                    Mathf.RoundToInt(cameraPosition.z / parcelSize - 0.5f));

                Vector3 a = new Vector3(cameraParcel.x * parcelSize, cameraPosition.y, cameraParcel.y * parcelSize);
                Vector3 b = a + new Vector3(parcelSize, 0f, 0f);
                Vector3 c = a + new Vector3(parcelSize, 0f, parcelSize);
                Vector3 d = a + new Vector3(0f, 0f, parcelSize);

                Debug.DrawLine(a, b, Color.white);
                Debug.DrawLine(b, c, Color.white);
                Debug.DrawLine(c, d, Color.white);
                Debug.DrawLine(d, a, Color.white);
            }

            using (HashSetPool<Vector2Int>.Get(out var toLoad))
            using (HashSetPool<Vector2Int>.Get(out var toKeep))
            {
                {
                    Vector3 a = cameraPosition + new Vector3(-loadRadius, 0f, -loadRadius);
                    Vector3 b = cameraPosition + new Vector3(loadRadius, 0f, -loadRadius);
                    Vector3 c = cameraPosition + new Vector3(loadRadius, 0f, loadRadius);
                    Vector3 d = cameraPosition + new Vector3(-loadRadius, 0f, loadRadius);

                    Debug.DrawLine(a, b, Color.green);
                    Debug.DrawLine(b, c, Color.green);
                    Debug.DrawLine(c, d, Color.green);
                    Debug.DrawLine(d, a, Color.green);

                    toLoad.Add(WorldPositionToParcel(a));
                    toLoad.Add(WorldPositionToParcel(b));
                    toLoad.Add(WorldPositionToParcel(c));
                    toLoad.Add(WorldPositionToParcel(d));
                }

                toKeep.UnionWith(toLoad);

                {
                    Vector3 a = cameraPosition + new Vector3(-unloadRadius, 0f, -unloadRadius);
                    Vector3 b = cameraPosition + new Vector3(unloadRadius, 0f, -unloadRadius);
                    Vector3 c = cameraPosition + new Vector3(unloadRadius, 0f, unloadRadius);
                    Vector3 d = cameraPosition + new Vector3(-unloadRadius, 0f, unloadRadius);

                    Debug.DrawLine(a, b, Color.red);
                    Debug.DrawLine(b, c, Color.red);
                    Debug.DrawLine(c, d, Color.red);
                    Debug.DrawLine(d, a, Color.red);

                    toKeep.Add(WorldPositionToParcel(a));
                    toKeep.Add(WorldPositionToParcel(b));
                    toKeep.Add(WorldPositionToParcel(c));
                    toKeep.Add(WorldPositionToParcel(d));
                }

                for (int i = usedParcels.Count - 1; i >= 0; i--)
                {
                    ParcelData parcelData = usedParcels[i];
                    if (!toKeep.Contains(parcelData.parcel))
                    {
                        usedParcels.RemoveAtSwapBack(i);
                        freeParcels.Add(parcelData);
                    }
                }

                foreach (Vector2Int parcel in toLoad)
                {
                    if (usedParcels.Any(i => i.parcel == parcel))
                        continue;

                    if (freeParcels.Count > 0)
                    {
                        int lastIndex = freeParcels.Count - 1;
                        ParcelData parcelData = freeParcels[lastIndex];
                        freeParcels.RemoveAt(lastIndex);
                        usedParcels.Add(parcelData);

                        if (parcelData.parcel != parcel)
                        {
                            parcelData.parcel = parcel;

                            SetParcelMeshVertices(parcelData.mesh, parcel);
                            parcelData.mesh.MarkModified();
                            parcelData.collider.sharedMesh = parcelData.mesh;

                            parcelData.collider.transform.position = new Vector3(
                                parcel.x * parcelSize, 0f, parcel.y * parcelSize);
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

                        parcelData.mesh.bounds = new Bounds(
                            new Vector3(parcelSize * 0.5f, 1.5f, parcelSize * 0.5f),
                            new Vector3(parcelSize, 3f, parcelSize));

                        parcelData.collider = new GameObject("Parcel Collider")
                            .AddComponent<MeshCollider>();

                        parcelData.collider.transform.position = new Vector3(
                            parcel.x * parcelSize, 0f, parcel.y * parcelSize);

                        parcelData.collider.sharedMesh = parcelData.mesh;

#if UNITY_EDITOR
                        parcelData.collider.transform.SetParent(transform, true);
#endif
                    }
                }
            }
        }

        private void SetParcelMeshIndicesAndNormals(Mesh mesh)
        {
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

            using (ListPool<Vector3>.Get(out var normals))
            {
                int normalsCount = sideVertexCount * sideVertexCount;
                normals.EnsureCapacity(normalsCount);

                for (int i = 0; i < normalsCount; i++)
                    normals.Add(Vector3.up);

                mesh.SetNormals(normals, 0, normalsCount,
                    MeshUpdateFlags.DontNotifyMeshUsers | MeshUpdateFlags.DontRecalculateBounds);
            }
        }

        private void SetParcelMeshVertices(Mesh mesh, Vector2Int parcel)
        {
            Profiler.BeginSample(nameof(SetParcelMeshVertices));
            float3 parcelOrigin = float3(parcel.x * parcelSize, 0f, parcel.y * parcelSize);

            using (ListPool<Vector3>.Get(out var vertices))
            {
                int sideVertexCount = parcelSize + 1;
                vertices.EnsureCapacity(sideVertexCount * sideVertexCount);
                Profiler.BeginSample(nameof(MountainsNoise_float));

                for (int z = 0; z < sideVertexCount; z++)
                {
                    for (int x = 0; x < sideVertexCount; x++)
                    {
                        MountainsNoise_float(float3(x, 0f, z) + parcelOrigin, 0.02f,
                            float2(-99974.82f, -93748.33f), float2(-67502.3f, -22190.19f),
                            float2(77881.34f, -61863.88f), 0.338f, 2.9f, 3f, out float3 vertex);

                        vertices.Add(vertex - parcelOrigin);
                    }
                }

                Profiler.EndSample();
                Profiler.BeginSample(nameof(Mesh.SetVertices));

                mesh.SetVertices(vertices, 0, vertices.Count,
                    MeshUpdateFlags.DontValidateIndices | MeshUpdateFlags.DontRecalculateBounds);

                Profiler.EndSample();
            }

            Profiler.EndSample();
        }

        private Vector2Int WorldPositionToParcel(Vector3 worldPosition)
        {
            return new Vector2Int(
                Mathf.RoundToInt(worldPosition.x / parcelSize - 0.5f),
                Mathf.RoundToInt(worldPosition.z / parcelSize - 0.5f));
        }

        private static void MountainsNoise_float(float3 positionIn, float scale, float2 octave0, float2 octave1,
            float2 octave2,
            float persistence, float lacunarity, float multiplyValue, out float3 positionOut)
        {
            float amplitude = 1f;
            float frequency = 1f;
            float noiseHeight = 0f;

            // Octave 0
            {
                float2 sample = (positionIn.xz + octave0) * scale * frequency;
                noiseHeight += snoise(sample) * amplitude;
                amplitude *= persistence;
                frequency *= lacunarity;
            }

            // Octave 1
            {
                float2 sample = (positionIn.xz + octave1) * scale * frequency;
                noiseHeight += snoise(sample) * amplitude;
                amplitude *= persistence;
                frequency *= lacunarity;
            }

            // Octave 2
            {
                float2 sample = (positionIn.xz + octave2) * scale * frequency;
                noiseHeight += snoise(sample) * amplitude;
                amplitude *= persistence;
                frequency *= lacunarity;
            }

            positionOut = positionIn;
            positionOut.y += noiseHeight * multiplyValue;
        }

        private sealed class ParcelData
        {
            public Vector2Int parcel;
            public MeshCollider collider;
            public Mesh mesh;
        }
    }
}
