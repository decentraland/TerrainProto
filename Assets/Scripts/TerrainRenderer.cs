using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.Pool;
using UnityEngine.Profiling;
using UnityEngine.Rendering;

namespace Decentraland.Terrain
{
    [ExecuteAlways]
    public sealed class TerrainRenderer : MonoBehaviour
    {
        [SerializeField] private TerrainData terrainData;
        [SerializeField] private Material material;

        private Mesh parcelMesh;
        private RenderParams renderParams;

#if UNITY_EDITOR
        public Mesh ParcelMesh => parcelMesh;
#endif

        private void OnValidate()
        {
            if (terrainData == null)
                return;

#if UNITY_EDITOR
            if (parcelMesh != null)
            {
                if (!Application.isPlaying)
                    Object.DestroyImmediate(parcelMesh);
                else
                    Object.Destroy(parcelMesh);
            }
#endif

            parcelMesh = CreateParcelMesh();

            renderParams.instanceID = GetInstanceID();
            renderParams.layer = gameObject.layer;
            renderParams.material = material;
            renderParams.receiveShadows = true;
            renderParams.renderingLayerMask = RenderingLayerMask.defaultRenderingLayerMask;
            renderParams.shadowCastingMode = ShadowCastingMode.On;

            float terrainHalfSize = terrainData.parcelSize * terrainData.terrainSize * 0.5f;
            float maxHeight = terrainData.maxHeight;

            renderParams.worldBounds = new Bounds(
                new Vector3(0f, maxHeight * 0.5f, 0f),
                new Vector3(terrainHalfSize, maxHeight, terrainHalfSize));
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

        private void DrawParcels(ScriptableRenderContext context, List<Camera> cameras)
        {
            Camera camera;

#if UNITY_EDITOR
            if (!Application.isPlaying)
            {
                camera = SceneView.lastActiveSceneView.camera;
            }
            else
#endif
            {
                camera = Camera.main;
            }

            if (camera == null)
                return;

            Profiler.BeginSample(nameof(DrawParcels));

            int parcelSize = terrainData.parcelSize;
            int terrainSize = terrainData.terrainSize;
            float parcelRadius = parcelSize * Mathf.Sqrt(2f);
            float parcelCenterY = terrainData.maxHeight * 0.5f;
            float terrainHalfSize = parcelSize * terrainSize * 0.5f;
            var worldToProjectionMatrix = camera.projectionMatrix * camera.worldToCameraMatrix;

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

            using (ListPool<Matrix4x4>.Get(out var parcelTransforms))
            using (ListPool<Matrix4x4>.Get(out var treeTransforms))
            {
                for (int z = 0; z < terrainSize; z++)
                for (int x = 0; x < terrainSize; x++)
                {
                    Vector4 parcelCenter = new Vector4(
                        x * parcelSize - terrainHalfSize + parcelSize * 0.5f, parcelCenterY,
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

                    parcelTransforms.Add(Matrix4x4.Translate(
                        new Vector3(x * parcelSize - terrainHalfSize, 0f, z * parcelSize - terrainHalfSize)));

                    TerrainNoiseFunction noise = terrainData.noiseFunction;
                    Vector3 treePos;
                    treePos.x = noise.RandomRange(x, z, x * parcelSize - terrainHalfSize, (x + 1) * parcelSize - terrainHalfSize);
                    treePos.z = noise.RandomRange(treePos.x, treePos.x, z * parcelSize - terrainHalfSize, (z + 1) * parcelSize - terrainHalfSize);
                    treePos.y = noise.HeightMap(treePos.x, treePos.z);
                    float treeYaw = noise.RandomRange(treePos.x, treePos.z, -180f, 180f);

                    treeTransforms.Add(Matrix4x4.TRS(treePos, Quaternion.Euler(0f, treeYaw, 0f),
                        Vector3.one));
                }

                Graphics.RenderMeshInstanced(in renderParams, parcelMesh, 0, parcelTransforms);

                // Tree rendering starts here.

                Renderer treeRenderer = terrainData.treePrefabs[0].source.GetComponent<LODGroup>()
                    .GetLODs()[0].renderers[0];
                Mesh treeMesh = treeRenderer.GetComponent<MeshFilter>().sharedMesh;

                var treeParams = new RenderParams()
                {
                    instanceID = GetInstanceID(),
                    layer = gameObject.layer,
                    receiveShadows = true,
                    renderingLayerMask = RenderingLayerMask.defaultRenderingLayerMask,
                    shadowCastingMode = ShadowCastingMode.On
                };

                using (ListPool<Material>.Get(out var treeMaterials))
                {
                    treeRenderer.GetSharedMaterials(treeMaterials);

                    for (int i = 0; i < treeMaterials.Count; i++)
                    {
                        treeParams.material = treeMaterials[i];
                        Graphics.RenderMeshInstanced(in treeParams, treeMesh, i, treeTransforms);
                    }
                }
            }

            Profiler.EndSample();
        }
    }
}
