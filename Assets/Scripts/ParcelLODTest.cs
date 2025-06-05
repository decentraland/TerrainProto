using System.Collections.Generic;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using Unity.Mathematics.Geometry;
using UnityEngine;
using UnityEngine.Profiling;
using UnityEngine.Rendering;
using static Unity.Mathematics.math;
using Plane = Unity.Mathematics.Geometry.Plane;

namespace Decentraland.Terrain
{
    [ExecuteAlways]
    public sealed class ParcelLODTest : MonoBehaviour
    {
        [SerializeField] private int parcelSize = 16;
        [SerializeField] private int terrainSize = 300;
        [SerializeField] private Material material;
        [SerializeField] private Camera camera;

        private NativeArray<int2> magicPattern;
        private Mesh parcelMesh;

        private Vector3[] clipCorners = { new(-1f, -1f, 0f), new(-1f, -1f, 1f),
            new(-1f, 1f, 0f), new(-1f, 1f, 1f), new(1f, -1f, 0f), new(1f, -1f, 1f), new(1f, 1f, 0f),
            new(1f, 1f, 1f) };

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

            int extent = terrainSize * parcelSize / 2;
            var terrainBounds = new MinMaxAABB(float3(-extent, 0f, -extent), float3(extent, 0f, extent));
            clipBounds.Clip(terrainBounds);

            var parcelTransforms = new NativeList<Matrix4x4>(64, Allocator.TempJob);

            Profiler.BeginSample("GenerateTerrainMesh");

            var generateTerrainJob = new GenerateTerrainJob()
            {
                parcelSize = parcelSize,
                terrainSize = terrainSize,
                cameraPosition = cameraPosition,
                clipBounds = clipBounds,
                clipPlanes = clipPlanes,
                magicPattern = magicPattern,
                parcelTransforms = parcelTransforms
            };

            JobHandle generateTerrain = generateTerrainJob.Schedule();
            generateTerrain.Complete();

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
                    worldBounds = terrainBounds.ToBounds()
                };

                Graphics.RenderMeshInstanced(renderParams, parcelMesh, 0, parcelTransforms.AsArray());
            }

            parcelTransforms.Dispose();
        }

        private struct GenerateTerrainJob : IJob
        {
            public int parcelSize;
            public int terrainSize;
            public float maxHeight;
            public float3 cameraPosition;
            public MinMaxAABB clipBounds;
            [ReadOnly] public NativeArray<ClipPlane> clipPlanes;
            [ReadOnly] public NativeArray<int2> magicPattern;
            public NativeList<Matrix4x4> parcelTransforms;

            public void Execute()
            {
                int2 origin = (PositionToParcel(cameraPosition, parcelSize) + 1) & ~1;
                int scale = (int)(cameraPosition.y / parcelSize) + 1;

                for (int i = 0; i < 4; i++)
                    TryGenerateParcel(origin, magicPattern[i], scale);

                while (true)
                {
                    bool outOfBounds = true;

                    for (int i = 4; i < 16; i++)
                        if (TryGenerateParcel(origin, magicPattern[i], scale))
                            outOfBounds = false;

                    if (outOfBounds)
                        break;

                    scale *= 2;
                }
            }

            private bool TryGenerateParcel(int2 origin, int2 parcel, int scale)
            {
                int2 position = (origin + parcel * scale) * parcelSize;

                if (!OverlapsClipVolume(position, scale))
                    return false;

                parcelTransforms.Add(Matrix4x4.TRS(new Vector3(position.x, 0f, position.y),
                    Quaternion.identity, Vector3.one * scale));

                return true;
            }

            private bool OverlapsClipVolume(int2 min, int scale)
            {
                int2 max = min + scale * parcelSize;
                var bounds = new MinMaxAABB(float3(min.x, 0f, min.y), float3(max.x, maxHeight, max.y));

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
    }
}
