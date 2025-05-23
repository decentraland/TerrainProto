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

            float useRadius = parcelSize / 3f;
            var cameraPosition = mainCamera.transform.position;
            RectInt usedRect = WorldPositionToParcelRect(cameraPosition, useRadius);

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

        private Vector2Int WorldPositionToParcel(Vector3 value)
        {
            return new Vector2Int(
                Mathf.RoundToInt(value.x / parcelSize - 0.5f),
                Mathf.RoundToInt(value.z / parcelSize - 0.5f));
        }

        private Vector2Int WorldPositionToParcel(float x, float z)
        {
            return new Vector2Int(
                Mathf.RoundToInt(x / parcelSize - 0.5f),
                Mathf.RoundToInt(z / parcelSize - 0.5f));
        }

        private RectInt WorldPositionToParcelRect(Vector3 position, float halfSize)
        {
            Vector2Int min = WorldPositionToParcel(position.x - halfSize, position.z - halfSize);
            Vector2Int max = WorldPositionToParcel(position.x + halfSize, position.z + halfSize);
            return new RectInt(min.x, min.y, max.x - min.x + 1, max.y - min.y + 1);
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
