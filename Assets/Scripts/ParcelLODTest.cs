using System.Collections.Generic;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Profiling;
using UnityEngine.Rendering;
using static Unity.Mathematics.math;

namespace Decentraland.Terrain
{
    [ExecuteAlways]
    public sealed class ParcelLODTest : MonoBehaviour
    {
        [SerializeField] private Camera camera;
        [SerializeField] private Material material;
        [SerializeField] private int terrainSize = 300;
        [SerializeField] private int parcelSize = 16;

        private NativeArray<int2> magicPattern;
        private Mesh parcelMesh;

        private void Awake()
        {
            Debug.Log("Awake");
            parcelMesh = CreateParcelMesh();

/*#if UNITY_EDITOR
            UnityEditor.AssemblyReloadEvents.beforeAssemblyReload += DisposeMagicPattern;
            UnityEditor.AssemblyReloadEvents.afterAssemblyReload += CreateMagicPattern;
#endif*/
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
            Debug.Log("OnDestroy");

#if UNITY_EDITOR
            /*UnityEditor.AssemblyReloadEvents.beforeAssemblyReload -= DisposeMagicPattern;
            UnityEditor.AssemblyReloadEvents.afterAssemblyReload -= CreateMagicPattern;*/

            if (!Application.isPlaying)
                Object.DestroyImmediate(parcelMesh);
            else
#endif
                Object.Destroy(parcelMesh);
        }

#if UNITY_EDITOR
        private static Vector3[] rectCorners = new Vector3[4];

        private void OnDrawGizmos()
        {
            int halfTerrainSize = terrainSize * parcelSize / 2;

            rectCorners[0] = new Vector3(-halfTerrainSize, 0f, -halfTerrainSize);
            rectCorners[1] = new Vector3(halfTerrainSize, 0f, -halfTerrainSize);
            rectCorners[2] = new Vector3(halfTerrainSize, 0f, halfTerrainSize);
            rectCorners[3] = new Vector3(-halfTerrainSize, 0f, halfTerrainSize);
            Gizmos.color = Color.green;
            Gizmos.DrawLineStrip(rectCorners, true);

            int2 cameraParcel = PositionToParcel(camera.transform.position, parcelSize);
            cameraParcel = ((cameraParcel + 1) & ~1) - 1;
            cameraParcel *= parcelSize;

            rectCorners[0] = new Vector3(cameraParcel.x, 0f, cameraParcel.y);
            rectCorners[1] = new Vector3(cameraParcel.x + parcelSize * 2, 0f, cameraParcel.y);
            rectCorners[2] = new Vector3(cameraParcel.x + parcelSize * 2, 0f, cameraParcel.y + parcelSize * 2);
            rectCorners[3] = new Vector3(cameraParcel.x, 0f, cameraParcel.y + parcelSize * 2);
            Gizmos.color = Color.red;
            Gizmos.DrawLineStrip(rectCorners, true);
        }
#endif

        private void CreateMagicPattern()
        {
            magicPattern = new NativeArray<int2>(16, Allocator.Persistent,
                NativeArrayOptions.UninitializedMemory);

            magicPattern[0] = int2(0, 0);
            magicPattern[1] = int2(-1, 0);
            magicPattern[2] = int2(-1, -1);
            magicPattern[3] = int2(0, -1);
            magicPattern[4] = int2(1, 1);
            magicPattern[5] = int2(0, 1);
            magicPattern[6] = int2(-1, 1);
            magicPattern[7] = int2(-2, 1);
            magicPattern[8] = int2(-2, 0);
            magicPattern[9] = int2(-2, -1);
            magicPattern[10] = int2(-2, -2);
            magicPattern[11] = int2(-1, -2);
            magicPattern[12] = int2(0, -2);
            magicPattern[13] = int2(1, -2);
            magicPattern[14] = int2(1, -1);
            magicPattern[15] = int2(1, 0);
        }

        private Mesh CreateParcelMesh()
        {
            var parcelMesh = new Mesh()
            {
                name = "Parcel Render Mesh",
                hideFlags = HideFlags.DontSave
            };

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

        private void DisposeMagicPattern()
        {
            magicPattern.Dispose();
        }

        private static int2 PositionToParcel(float3 value, int parcelSize)
        {
            return (int2)round(value.xz * (1f / parcelSize) - 0.5f);
        }

        private void Render(ScriptableRenderContext context, List<Camera> cameras)
        {
            float3 cameraPosition = camera.transform.position;

            if (cameraPosition.y < 0f)
                return;

            if (!magicPattern.IsCreated)
                CreateMagicPattern();

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

            var parcelTransforms = new NativeList<Matrix4x4>(65536, Allocator.TempJob);

            Profiler.BeginSample("GenerateTerrainMesh");

            var parcelMeshesJob = new GenerateTerrainJob()
            {
                terrainSize = terrainSize,
                parcelSize = parcelSize,
                cameraPosition = cameraPosition,
                magicPattern = magicPattern,
                clipPlanes = clipPlanes,
                parcelTransforms = parcelTransforms
            };

            JobHandle parcelMeshes = parcelMeshesJob.Schedule();
            parcelMeshes.Complete();

            Profiler.EndSample();

            clipPlanes.Dispose();

            if (parcelTransforms.Length > 0)
            {
                int terrainHalfSize = terrainSize * parcelSize / 2;

                var renderParams = new RenderParams()
                {
                    instanceID = GetInstanceID(),
                    layer = gameObject.layer,
                    material = material,
                    renderingLayerMask = RenderingLayerMask.defaultRenderingLayerMask,
                    worldBounds = new(Vector3.zero, new(terrainHalfSize, 0f, terrainHalfSize))
                };

                Graphics.RenderMeshInstanced(renderParams, parcelMesh, 0, parcelTransforms.AsArray());
            }

            parcelTransforms.Dispose();
        }

        private struct GenerateTerrainJob : IJob
        {
            public int terrainSize;
            public int parcelSize;
            public float3 cameraPosition;
            [ReadOnly] public NativeArray<int2> magicPattern;
            [ReadOnly] public NativeArray<float4> clipPlanes;
            public NativeList<Matrix4x4> parcelTransforms;

            public void Execute()
            {
                int2 origin = (PositionToParcel(cameraPosition, parcelSize) + 1) & ~1;
                int scale = (int)(cameraPosition.y / parcelSize) + 1;

                for (int i = 0; i < 4; i++)
                {
                    bool outOfBounds = false;
                    GenerateParcel(origin, magicPattern[i], scale, ref outOfBounds);
                }

                while (true)
                {
                    bool outOfBounds = true;

                    for (int i = 4; i < 16; i++)
                        GenerateParcel(origin, magicPattern[i], scale, ref outOfBounds);

                    if (outOfBounds)
                        break;

                    scale *= 2;
                }
            }

            private void GenerateParcel(int2 origin, int2 parcel, int scale, ref bool outOfBounds)
            {
                RectInt terrainRect = new RectInt(terrainSize / -2, terrainSize / -2, terrainSize,
                    terrainSize);

                RectInt parcelRect = new RectInt(origin.x + parcel.x * scale,
                    origin.y + parcel.y * scale, scale, scale);

                if (terrainRect.Overlaps(parcelRect))
                {
                    outOfBounds = false;

                    if (OverlapsFrustum(parcelRect))
                    {
                        int2 position = (origin + parcel * scale) * parcelSize;

                        parcelTransforms.Add(Matrix4x4.TRS(
                            new Vector3(position.x, 0f, position.y),
                            Quaternion.identity, Vector3.one * scale));
                    }
                }
            }

            private bool OverlapsFrustum(RectInt parcelRect)
            {
                float3 parcelLocalCenter
                    = float3(parcelRect.width * parcelSize, 0f, parcelRect.height * parcelSize) * 0.5f;

                float4 rectCenter = float4(parcelRect.x * parcelSize + parcelLocalCenter.x,
                    parcelLocalCenter.y, parcelRect.y * parcelSize + parcelLocalCenter.z, 1f);

                float negRectRadius = -length(parcelLocalCenter);

                for (int i = 0; i < clipPlanes.Length; i++)
                    if (dot(clipPlanes[i], rectCenter) < negRectRadius)
                        return false;

                return true;
            }
        }
    }
}
