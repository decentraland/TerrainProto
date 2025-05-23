using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Pool;
using UnityEngine.Rendering;

namespace Decentraland.Terrain
{
    [ExecuteAlways]
    public sealed class TerrainRenderer : MonoBehaviour
    {
        [SerializeField] private int parcelSize = 16;
        [SerializeField] private int terrainSize;
        [SerializeField] private Material material;

        private Mesh parcelMesh;
        private RenderParams renderParams;

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

            parcelMesh = CreateParcelMesh(parcelSize);

            renderParams.instanceID = GetInstanceID();
            renderParams.layer = gameObject.layer;
            renderParams.material = material;
            renderParams.receiveShadows = true;
            renderParams.renderingLayerMask = RenderingLayerMask.defaultRenderingLayerMask;
            renderParams.shadowCastingMode = ShadowCastingMode.On;

            float terrainHalfSize = parcelSize * terrainSize * 0.5f;

            renderParams.worldBounds = new Bounds(
                new Vector3(0f, 2f, 0f),
                new Vector3(terrainHalfSize, 4f, terrainHalfSize));
        }

        private void OnEnable()
        {
            RenderPipelineManager.beginContextRendering += DrawParcels;
        }

        private void OnDisable()
        {
            RenderPipelineManager.beginContextRendering -= DrawParcels;
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

        private static Mesh CreateParcelMesh(int parcelSize)
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

        private void DrawParcels(ScriptableRenderContext context, List<Camera> cameras)
        {
            Camera mainCamera = Camera.main;

            if (mainCamera == null)
                return;

            float parcelRadius = parcelSize * Mathf.Sqrt(2f);
            float terrainHalfSize = parcelSize * terrainSize * 0.5f;
            var worldToProjectionMatrix = mainCamera.projectionMatrix * mainCamera.worldToCameraMatrix;

            Vector4 p1 = worldToProjectionMatrix.GetRow(0);
            Vector4 p2 = worldToProjectionMatrix.GetRow(1);
            Vector4 p3 = worldToProjectionMatrix.GetRow(2);
            Vector4 p4 = worldToProjectionMatrix.GetRow(3);

            Vector4 clipPlane0 = p4 + p3;
            clipPlane0 /= ((Vector3)clipPlane0).magnitude;
            Vector4 clipPlane1 = p4 - p3;
            clipPlane1 /= ((Vector3)clipPlane1).magnitude;
            Vector4 clipPlane2 = p4 + p1;
            clipPlane2 /= ((Vector3)clipPlane2).magnitude;
            Vector4 clipPlane3 = p4 - p1;
            clipPlane3 /= ((Vector3)clipPlane3).magnitude;
            Vector4 clipPlane4 = p4 + p2;
            clipPlane4 /= ((Vector3)clipPlane4).magnitude;
            Vector4 clipPlane5 = p4 - p2;
            clipPlane5 /= ((Vector3)clipPlane5).magnitude;

            using (ListPool<Matrix4x4>.Get(out var instanceData))
            {
                for (int z = 0; z < terrainSize; z++)
                for (int x = 0; x < terrainSize; x++)
                {
                    Vector4 parcelCenter = new Vector4(
                        x * parcelSize - terrainHalfSize + parcelSize * 0.5f, 1.5f,
                        z * parcelSize - terrainHalfSize + parcelSize * 0.5f, 1f);

                    if (Vector4.Dot(clipPlane0, parcelCenter) < -parcelRadius
                        || Vector4.Dot(clipPlane1, parcelCenter) < -parcelRadius
                        || Vector4.Dot(clipPlane2, parcelCenter) < -parcelRadius
                        || Vector4.Dot(clipPlane3, parcelCenter) < -parcelRadius
                        || Vector4.Dot(clipPlane4, parcelCenter) < -parcelRadius
                        || Vector4.Dot(clipPlane5, parcelCenter) < -parcelRadius)
                    {
                        continue;
                    }

                    instanceData.Add(Matrix4x4.Translate(
                        new Vector3(x * parcelSize - terrainHalfSize, 0f, z * parcelSize - terrainHalfSize)));
                }

                Graphics.RenderMeshInstanced(in renderParams, parcelMesh, 0, instanceData);
            }
        }
    }
}
